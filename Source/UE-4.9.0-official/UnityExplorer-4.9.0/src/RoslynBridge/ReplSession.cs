using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 无状态桩的 REPL：提交 N 编译成 Submission_N（继承 Submission_{N-1}），
    /// 顶层变量提升为静态字段以跨提交保留；末表达式写入 __result 实现值打印。
    /// </summary>
    public sealed class ReplSession : IDisposable
    {
        readonly List<string> s_referencePaths;
        // C.5-lite（任务 8）：原 `List<MetadataReference> s_imageRefs`（s_ 前缀名不副实——实为实例字段）。
        // RimWorld 启动期 GetFields 字段扫描解析字段签名时**不按 BindingFlags 过滤，实例字段一样炸**
        // （Player.log 实证：TypeLoadException: Could not load type of field
        // 'UnityExplorer.RoslynBridge.ReplSession:s_imageRefs' ——前置包载荷在场仍发生，属 Mono
        // 元数据遍历行为）。载荷类型只许活在方法体内（局部变量对扫描不可见）→ object 擦除，
        // 使用点 (MetadataReference) 转型；逐提交累积语义不变。
        readonly List<object> imageRefs = new List<object>();
        readonly List<string> s_usings = new List<string>();
        readonly List<string> s_classes = new List<string>();
        readonly List<KeyValuePair<string, Type>> s_vars = new List<KeyValuePair<string, Type>>();
        readonly List<Assembly> s_sessionAssemblies = new List<Assembly>();
        // 会话唯一标签（构造时 GUID 段）：提交程序集名 = Submission_<tag>_<n>_asm，
        // 全局唯一 → 多会话并存时按名回退（RequestingAssembly==null）也不可能答错会话。
        readonly string s_sessionTag = Guid.NewGuid().ToString("N").Substring(0, 8);

        int s_submission;
        bool s_disposed;
        Assembly s_lastAssembly;

        public object LastValue { get; private set; }
        public CompileResult LastResult { get; private set; }

        // A1（合并修复轮）观测点：引用集大小恒等于入参集合（开关不得放大它，测试断言用）。
        public int ReferenceCount => s_referencePaths.Count;

        public ReplSession(IEnumerable<string> referencePaths)
        {
            s_referencePaths = new List<string>(referencePaths);
            // 跨提交继承链的运行期绑定：Assembly.Load(byte[]) 产物不进默认加载上下文，
            // Submission_N 按名解析基类 Submission_{N-1} 时默认探测必失败（实测
            // FileNotFoundException → ReflectionTypeLoadException → GetType 返回 null）。
            // 自挂 AssemblyResolve：只应答"请求方是本会话提交程序集"且本会话按名持有的程序集。
            // 生命周期契约（任务 7 消费方义务）：会话替换/控制台卸载时调用 Dispose() 摘钩；
            // Dispose 后不得再 Submit；不 Dispose 也无害（唯一名下死处理器只直通 null）。
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSessionAssembly;
        }

        /// <summary>摘除 AssemblyResolve 并清空会话程序集注册；幂等。
        /// Dispose 后再 Submit 返回失败结果（ErrorCount=1、诊断"会话已释放"），不抛异常。</summary>
        public void Dispose()
        {
            if (s_disposed) return;
            s_disposed = true;
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveSessionAssembly;
            s_sessionAssemblies.Clear();
        }

        Assembly ResolveSessionAssembly(object sender, ResolveEventArgs args)
        {
            if (args.RequestingAssembly != null && !s_sessionAssemblies.Contains(args.RequestingAssembly))
                return null;   // 非本会话的请求：交链上其他处理器
            for (int i = s_sessionAssemblies.Count - 1; i >= 0; i--)
                if (s_sessionAssemblies[i].FullName == args.Name)
                    return s_sessionAssemblies[i];
            return null;
        }

        public CompileResult Submit(string code)
        {
            // Dispose 后防御：返回失败结果（方案 a，与 Submit 全程"失败=带诊断的 CompileResult"
            // 形态一致；任务 7 求值器统一展示诊断，不打断其调用）。无此防御时首次 Submit 静默
            // 成功（n=1 无基类可解析）、第二次起运行期 NRE，报错不清晰。
            if (s_disposed)
                return new CompileResult
                {
                    Success = false,
                    ErrorCount = 1,
                    Diagnostics = "会话已释放（ReplSession 已 Dispose，不得再 Submit）",
                };

            int n = s_submission + 1;
            string className = "Submission_" + n;
            string baseName = n == 1 ? null : "Submission_" + (n - 1);

            // 输入归一化（§17 根因清扫，替换原"逐语句补分号"补丁）：
            // REPL 输入常缺末尾分号；**缺分号时解析器的错误恢复会把后面的语句并进前一条声明**——
            // 实测 `var v = new Vec(2, 5); v.X * v.Y`（末条缺分号）被解析成**一条**局部声明 + 两个声明符，
            // 第二个声明符是 `v.X *`（指针类型）+ `v` ⇒ 生成 `public static v.X * v;` 这种垃圾字段
            // （诊断表现为 CS0246 'v' / CS0214 指针 / CS0102 重复定义，用户完全看不懂）。
            // 旧补丁只对"解析出来的最后一条语句"重新 ParseStatement ⇒ 单语句重解析**只返回第一条语句**，
            // 拿不回被并进去的第二条。修法：解析**之前**给整段输入补一个分号（末字符非 ; 或 } 时），
            // 让解析器一次拿到正确结构；不需要逐语句重解析。
            string toParse = code;
            string trimmedInput = code == null ? "" : code.TrimEnd();
            if (trimmedInput.Length > 0)
            {
                char last = trimmedInput[trimmedInput.Length - 1];
                // `}` 结尾（块/类型声明）与 `;` 结尾都是完整输入；其余补分号。
                // §18 顺手修（F 残留）：末行是**行注释**时直接尾部追加会把 `;` 补进注释里
                //（`x + 1 // note;` → 注释吃掉分号，语句仍缺分号）⇒ 改为插到注释标记**之前**。
                // 只认"引号之外的第一个 //"（避免 `"http://x"` 这种字符串里的 // 被误判）。
                if (last != ';' && last != '}')
                {
                    int commentAt = LineCommentIndex(trimmedInput);
                    if (commentAt >= 0)
                    {
                        string head = trimmedInput.Substring(0, commentAt).TrimEnd();
                        if (head.Length > 0 && head[head.Length - 1] != ';' && head[head.Length - 1] != '}')
                            toParse = code.Substring(0, commentAt) + "; " + code.Substring(commentAt);
                        else
                            toParse = code;
                    }
                    else
                    {
                        toParse = code + ";";
                    }
                }
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(toParse, new CSharpParseOptions(LanguageVersion.Latest));
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            // 1) using 指令：吞进会话表，不进类体。
            // B3/B1 根因修复（本轮）：① 存**完整指令文本**，别名/static using（`using Foo = …;`、
            // `using static X;`）不再被 `u.Name` 剥成半截命名空间（旧码会把 `using Foo = System.Text.StringBuilder;`
            // 存成 "System.Text.StringBuilder"，下次提交 emit `using System.Text.StringBuilder;` → CS0138）；
            // ② **成功后才提交**（见本方法尾部）：旧码在编译前就把 using 写进表 —— 一条失败的
            // `using Bad;` 会让**此后每一次提交**都带上它而全部失败（B1 实测症状的放大器）。
            var pendingUsings = new List<string>();
            foreach (UsingDirectiveSyntax u in root.Usings)
            {
                string directive = u.ToString().Trim();
                if (directive.Length == 0) continue;
                if (!directive.EndsWith(";", StringComparison.Ordinal)) directive += ";";
                if (!s_usings.Contains(directive) && !pendingUsings.Contains(directive))
                    pendingUsings.Add(directive);
            }

            // 2) 顶层变量声明 → 提升为静态字段；顶层类定义 → 直接放进本提交
            var fields = new StringBuilder();
            var classes = new StringBuilder();
            var notes = new StringBuilder();   // B3：可读提示（不污染 Diagnostics；成功时作为结果回显）
            var unrecognized = new StringBuilder();   // 追加 F.2：未识别成员形态（响亮拒绝，不静默丢弃）
            var statements = new List<StatementSyntax>();
            foreach (MemberDeclarationSyntax m in root.Members)
            {
                if (m is GlobalStatementSyntax g)
                {
                    StatementSyntax stmt = g.Statement;
                    // （原"缺分号 → SyntaxFactory.ParseStatement(…+';')"补丁已删：整段输入已在解析前补过
                    //   末尾分号，逐语句重解析只会拿回第一条语句、治不了被并进去的后续语句。）

                    if (stmt is LocalFunctionStatementSyntax lfs)
                    {
                        // 追加 F 清扫（同类漏分支）：顶层 `int Twice(int a) => a * 2;` 在 Roslyn 眼里是
                        // **局部函数**（GlobalStatement 里的 LocalFunctionStatement）——原实现把它丢进
                        // Run()，于是只在本次提交内可见，**下次提交 CS0103 'Twice'**（"定义了却用不了"）。
                        // REPL 语义要求"定义了就能用" ⇒ 提升为生成类的成员；缺 static 时补上
                        //（否则后续提交的 static Run() 调不到它）。若函数体捕获了局部变量，提升后
                        // 会自然报 CS0103/CS8421——响亮且可解释，好过静默失效。
                        // 另需补 `public`：这些成员最终是**生成类的嵌套成员**，"无修饰符 = private"
                        // ⇒ 后续提交（派生类 Submission_N+1）按简单名找不到它（实测 CS0103 'Twice'）。
                        var fn = lfs;
                        if (!fn.Modifiers.Any(SyntaxKind.StaticKeyword))
                            fn = fn.AddModifiers(StaticToken());
                        if (!HasAccessibility(fn.Modifiers))
                            fn = fn.AddModifiers(PublicToken());
                        classes.AppendLine(fn.ToFullString());
                        // R5c（复审次要项）：**不**把方法名登记进 s_classes —— 那是"类清单"，
                        // 混进方法名会污染 GetClasses() 展示；只登记类型/委托名。
                    }
                    else if (stmt is LocalDeclarationStatementSyntax local && local.UsingKeyword == default)
                    {
                        string typeText = local.Declaration.Type.ToString();
                        bool isVar = typeText == "var";
                        foreach (VariableDeclaratorSyntax v in local.Declaration.Variables)
                        {
                            string fieldType = typeText;
                            if (isVar)
                            {
                                ITypeSymbol inferred = GetInferredType(tree, v);
                                fieldType = inferred?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                                // 引用本次提交之前变量的初始化式（如 var y = x + 37;）在一次性推断编译里
                                // 必然解析失败（x 对该孤立编译不可见）→ 错误类型显示为空串或 TypeKind.Error，
                                // 直接生成字段会得到 `public static  y;` → CS1519。统一回落 object，
                                // 运行后由 RefreshVarTypes 按反射修正显示类型。
                                if (inferred == null || inferred.TypeKind == TypeKind.Error
                                    || string.IsNullOrEmpty(fieldType))
                                    fieldType = "object";
                                // B3 根因修复：**匿名类型**的推断结果 ToDisplayString 出来是
                                // `<anonymous type: int Age, string Name>` 这种**不是合法类型名**的文本
                                // （Roslyn 的显示格式），写进 `public static <anonymous type: …> p;` 只会得到
                                // 解析错误 → 用户看到"半截无意义"的诊断。匿名类型本质上无法作为字段类型跨提交
                                // 保留（其身份只存在于本次编译），故按 object 提升 + 明确提示（Notes 会作为
                                // 本次结果回显），而不是让它污染下一次提交。
                                else if (inferred.IsAnonymousType)
                                {
                                    fieldType = "object";
                                    notes.AppendLine("提示：" + v.Identifier.Text
                                        + " 的推断类型是匿名类型，REPL 无法把匿名类型写成字段类型（跨提交不可保留）"
                                        + "→ 已按 object 提升；成员访问请改用具体类型，或在同一行内联使用。");
                                }
                            }
                            fields.AppendLine($"    public static {fieldType} {v.Identifier.Text};");
                            if (v.Initializer != null)
                                statements.Add(SyntaxFactory.ParseStatement(
                                    $"{v.Identifier.Text} = {v.Initializer.Value};"));
                            // C.5-lite：原 `s_vars.Any(p => p.Key == v.Identifier.Text)` 捕获载荷类型局部 v，
                            // 被提升为 <>c__DisplayClass 实例字段（Player.log 实证 TypeLoadException：
                            // <>c__DisplayClass23_0:v = VariableDeclaratorSyntax）——改显式循环，
                            // v 的使用留在方法体内，闭包类整个消失。
                            bool varExists = false;
                            for (int i = 0; i < s_vars.Count; i++)
                                if (s_vars[i].Key == v.Identifier.Text) { varExists = true; break; }
                            if (!varExists)
                                s_vars.Add(new KeyValuePair<string, Type>(v.Identifier.Text, null));
                        }
                    }
                    else
                    {
                        statements.Add(stmt);
                    }
                }
                else if (m is BaseNamespaceDeclarationSyntax || m is IncompleteMemberSyntax)
                {
                    // 追加 F.2：**兜底必须响亮**。这两类无法嵌进生成类的类体（namespace 只能顶层；
                    // IncompleteMember = 解析残渣），静默丢弃的表现是"类型莫名找不到"（实测两个 agent
                    // 都被带偏过），比报错难查一个数量级 ⇒ 记为本次提交失败（不改会话状态）。
                    unrecognized.AppendLine("不支持的顶层成员形态（已拒绝本次提交，未静默忽略）："
                        + m.Kind() + " → " + Shorten(m.ToString(), 160));
                }
                else if (m is MemberDeclarationSyntax)
                {
                    // B2/追加 F 根因清扫（**基类型判定，不再穷举**）：凡"成员声明"形态一律嵌入生成类的
                    // 类体，因此 class/struct/interface/enum/**record/record struct**/delegate/**方法**/
                    // 属性/事件/嵌套类型 等全部可用，将来 C# 新增成员形态也自动覆盖。
                    // （原实现只列 Class/Struct/Interface/Enum 四种具体语法 ⇒ record/record struct/
                    //   delegate/方法声明统统落进"无分支"被**静默丢弃**：实测 `public record Point(int X, int Y);`
                    //   提交"成功"但之后 `new Point(3, 4)` 报 CS0246 'Point'，用户完全看不到原因。）
                    classes.AppendLine(EnsureAccessible(m).ToFullString());
                    var bt = m as BaseTypeDeclarationSyntax;
                    if (bt != null && !s_classes.Contains(bt.Identifier.Text)) s_classes.Add(bt.Identifier.Text);
                    var del2 = m as DelegateDeclarationSyntax;
                    if (del2 != null && !s_classes.Contains(del2.Identifier.Text))
                        s_classes.Add(del2.Identifier.Text);
                }
                else
                {
                    unrecognized.AppendLine("不支持的顶层成员形态（已拒绝本次提交，未静默忽略）："
                        + m.Kind() + " → " + Shorten(m.ToString(), 160));
                }
            }

            // 追加 F.2：未识别形态 → 本次提交失败（Success=false ⇒ 不推进计数、不改会话状态），
            // 诊断明确指向形态本身，避免用户拿到后续 CS0246 的间接后果。
            if (unrecognized.Length > 0)
            {
                var rejected = new CompileResult
                {
                    Success = false,
                    ErrorCount = 1,
                    Diagnostics = unrecognized.ToString().TrimEnd()
                        + Environment.NewLine
                        + "（提示：受支持的顶层成员 = 语句/变量声明/class|struct|interface|enum|record|record struct/delegate；"
                        + "namespace 等请拆开或改写。）",
                };
                LastResult = rejected;
                return rejected;
            }

            // 3) 末条语句若是表达式 → 捕获为 __result
            string valueExpr = null;
            if (statements.Count > 0 && statements[statements.Count - 1] is ExpressionStatementSyntax es)
            {
                valueExpr = es.Expression.ToString();
                statements.RemoveAt(statements.Count - 1);
            }

            var body = new StringBuilder();
            foreach (StatementSyntax st in statements) body.AppendLine("        " + st.ToFullString().Trim());
            if (valueExpr != null) body.AppendLine($"        __result = {valueExpr};");

            var src = new StringBuilder();
            src.AppendLine("using System;");
            // B1/D ①：**本次新增**的 using 也要进本次编译 —— 否则它无法在自己的提交里被校验，
            // 只会"静默入表、从此每次提交都失败"（实测症状：连 `new[]{1,2,3}.Sum()` 都报
            // CS0246 'UniverseLib'，而那条 using 属于更早的提交）。失败即不提交（见方法尾部）。
            var emittedUsings = new List<string>(s_usings);
            foreach (string d in pendingUsings)
                if (!emittedUsings.Contains(d)) emittedUsings.Add(d);
            src.AppendLine(string.Join(Environment.NewLine, emittedUsings.ToArray()));
            src.AppendLine($"public class {className}" + (baseName != null ? " : " + baseName : ""));
            src.AppendLine("{");
            if (baseName == null) src.AppendLine("    public static object __result;");
            src.Append(fields);
            src.AppendLine("    public static void Run()");
            src.AppendLine("    {");
            src.Append(body);
            src.AppendLine("    }");
            src.Append(classes);
            src.AppendLine("}");

            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(src.ToString(), new CSharpParseOptions(LanguageVersion.Latest)),
                CSharpSyntaxTree.ParseText(PolyfillSource.Code, new CSharpParseOptions(LanguageVersion.Latest)),
            };

            var metadataRefs = new List<MetadataReference>(imageRefs.Count);
            foreach (object r in imageRefs) metadataRefs.Add((MetadataReference)r);
            foreach (string p in s_referencePaths)
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(p)); } catch { }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                "Submission_" + s_sessionTag + "_" + n + "_asm", trees, metadataRefs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var result = new CompileResult();
            result.Notes = notes.ToString().TrimEnd();
            result.GeneratedSource = src.ToString();
            byte[] pe;
            using (var ms = new MemoryStream())
            {
                var emit = compilation.Emit(ms);
                result.Diagnostics = DiagnosticFormatter.FormatAll(emit.Diagnostics);
                // C.5-lite：原 `Count(d => d.Severity == DiagnosticSeverity.Error)` 虽非捕获式，
                // 但编译器会把 lambda 缓存为 <>c.<>9__* 静态字段（Func<Diagnostic,bool>——泛型实参
                // =载荷类型，字段签名解析连带解析实参 typeref，与 List<MetadataReference> 同款炸法）
                // → 改显式循环，杜绝委托缓存字段。
                // ⚠️ 脆弱点：载荷类型仅以方法组/表达式形式出现，禁止改为捕获式（否则闭包
                // <>c__DisplayClass* 字段复活、启动扫描再炸）；非捕获 lambda 亦禁止（<>c 委托缓存字段）。
                result.ErrorCount = 0;
                foreach (Diagnostic d in emit.Diagnostics)
                    if (d.Severity == DiagnosticSeverity.Error) result.ErrorCount++;
                result.Success = emit.Success && result.ErrorCount == 0;
                pe = result.Success ? ms.ToArray() : null;
                result.PeBytes = pe;
            }

            LastResult = result;
            if (!result.Success) return result;   // 失败：会话状态不变（不推进 submission 计数）

            // B3/B1 根因修复：using 只在**提交成功**后进表（失败提交的 using 不得污染后续所有提交）。
            foreach (string directive in pendingUsings)
                if (!s_usings.Contains(directive)) s_usings.Add(directive);

            imageRefs.Add(MetadataReference.CreateFromImage(pe));
            s_submission = n;
            Assembly asm = Assembly.Load(pe);
            s_sessionAssemblies.Add(asm);
            s_lastAssembly = asm;
            LastValue = null;
            try
            {
                Type t = asm.GetType(className);
                t.GetMethod("Run").Invoke(null, null);
                Type probe = asm.GetType(className);
                while (probe != null && probe.GetField("__result") == null) probe = probe.BaseType;
                if (probe != null) LastValue = probe.GetField("__result").GetValue(null);
                RefreshVarTypes(asm, className);
            }
            catch (Exception ex)
            {
                // 运行期异常：程序集已 Assembly.Load 进 AppDomain（字节加载不可卸载），无法干净回滚
                // → 推进计数按失败记账（Success=false、ErrorCount≥1）；诊断必须带上消息（补括号，
                // 否则 `+` 先于 `??`，InnerException 为 null 时整段拼接结果把 ex.Message 吃掉）。
                result.Success = false;
                if (result.ErrorCount == 0) result.ErrorCount = 1;
                result.Diagnostics += Environment.NewLine + "运行时异常: "
                    + (ex.InnerException?.Message ?? ex.Message);
            }
            return result;
        }

        ITypeSymbol GetInferredType(SyntaxTree tree, VariableDeclaratorSyntax v)
        {
            // 只为 var 推断字段类型：用一次性编译的语义模型；失败则回落 object
            try
            {
                var comp = CSharpCompilation.Create("infer",
                    new[] { tree },
                    RoslynRefsFromPaths(s_referencePaths),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                SemanticModel model = comp.GetSemanticModel(tree);
                return model.GetTypeInfo(v.Initializer.Value).Type;
            }
            catch { return null; }
        }

        // C.5-lite：原 `Where(File.Exists).Select(p => MetadataReference.CreateFromFile(p))` 同款
        // 委托缓存雷（Func<string,MetadataReference> 泛型实参=载荷类型，字段签名解析炸）→ 显式循环；
        // File.Exists 过滤与 CreateFromFile 的 try-catch 跳坏 dll 语义保留（外层 GetInferredType
        // 亦有 catch，双保险）。
        // ⚠️ 脆弱点：载荷类型仅以方法组/表达式形式出现，禁止改为捕获式（否则闭包字段复活、
        // 启动扫描再炸）；此处若改回 `Where(...).Select(p => ...)`，编译器生成的 <>c 委托缓存字段
        // 签名含载荷类型，与 s_imageRefs 同款炸法。
        static List<MetadataReference> RoslynRefsFromPaths(IEnumerable<string> paths)
        {
            var refs = new List<MetadataReference>();
            foreach (string p in paths)
            {
                if (!File.Exists(p)) continue;
                try { refs.Add(MetadataReference.CreateFromFile(p)); } catch { }
            }
            return refs;
        }

        void RefreshVarTypes(Assembly asm, string className)
        {
            for (int i = 0; i < s_vars.Count; i++)
            {
                FieldInfo f = null;
                for (Type t = asm.GetType(className); t != null && f == null; t = t.BaseType)
                    f = t.GetField(s_vars[i].Key);
                if (f != null)
                    s_vars[i] = new KeyValuePair<string, Type>(s_vars[i].Key, f.FieldType);
            }
        }

        public string GetVars()
        {
            var sb = new StringBuilder();
            foreach (var kv in s_vars)
            {
                object val = null;
                try
                {
                    // GetType 回到 try 内保护（原修复轮 1 移出 try，若抛出会向外传播）。
                    Type start = s_lastAssembly?.GetType("Submission_" + s_submission);
                    // 单参 GetField 不带 FlattenHierarchy：基类 public static 字段返回 NULL
                    //（探针实证；静态字段不遍历基类）→ 与 RefreshVarTypes 同款逐层上溯取值。
                    FieldInfo f = null;
                    for (Type t = start; t != null && f == null; t = t.BaseType)
                        f = t.GetField(kv.Key, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (f != null) val = f.GetValue(null);
                }
                catch { }
                sb.AppendLine(kv.Key + " = " + (val ?? "null") + "   (" + (kv.Value?.Name ?? "?") + ")");
            }
            return sb.ToString().TrimEnd();
        }

        public string GetUsing() => string.Join(Environment.NewLine, s_usings.ToArray());
        public string GetClasses() => string.Join(Environment.NewLine, s_classes.ToArray());

        /// <summary>
        /// 批量预置 using（B1/D 护栏 + B4 性能，本轮新增）：把一批**根命名空间**一次进表，
        /// 用**一次**校验编译判定可用性；不可用的当场剔除、绝不留在表里。
        /// 语义与动机：
        ///  · 旧路径（ConsoleController.AddUsing → Submit("using X;")）每项一次完整提交编译 +
        ///    Assembly.Load + 一个新 Submission 程序集；`ResetConsole` 的 12 项默认 using 就是
        ///    12 次编译（B4 的 `reset_csharp_console` 5000ms 超时的可疑主因）。
        ///  · 更要紧的是**正确性**：只要表里留了一条编译不过的 using，此后**每一次提交**都会带上它
        ///    而全部失败（B1 实测：连 `new[]{1,2,3}.Sum()` 都报 CS0246 'UniverseLib'）。
        /// 算法（有界，最多 3 次编译）：全量入表 → 校验编译 → 有错则按"错误信息含该命名空间首段"归因、
        ///    剔除归因到的项 → 再校验一次 → 仍有错则**撤销本次全部新增**并原样返回诊断（宁可少 using，
        ///    也不让会话被毒化）。
        /// </summary>
        /// <returns>被接受的根命名空间列表（调用方据此打印/记录）。诊断经 <paramref name="diagnostics"/> 返回。</returns>
        public List<string> AddUsings(IEnumerable<string> rootNamespaces, out string diagnostics)
        {
            var accepted = new List<string>();
            diagnostics = null;
            var candidates = new List<string>();
            if (rootNamespaces != null)
            {
                foreach (string ns in rootNamespaces)
                {
                    if (string.IsNullOrEmpty(ns)) continue;
                    string directive = "using " + ns + ";";
                    if (!s_usings.Contains(directive) && !candidates.Contains(directive)) candidates.Add(directive);
                }
            }
            if (candidates.Count == 0) return accepted;

            // 第一次：全量入表（临时），校验
            foreach (string d in candidates) s_usings.Add(d);
            string firstDiag = ValidateUsings();
            if (firstDiag == null)
            {
                foreach (string d in candidates) accepted.Add(StripUsing(d));   // 全部可用：表已就位
                return accepted;
            }

            // 第二次：按错误归因剔除（CS0246/CS0103 的消息里带命名空间首段）
            // R5a（复审次要项，已知偏保守，可接受）：归因用"首段 + 引号"匹配，CS0234
            //（`'Planet' does not exist in namespace 'RimWorld'`）会把**有效**根项 `RimWorld` 一起剔除。
            // 取舍：宁可少一条 using（有日志可查、用户可用 add_using_directive 补回），
            // 也不要让"一条坏 using 拖垮整条 Roslyn 路径"（B1 的杀伤形态）复活。
            var blamed = new List<string>();
            foreach (string d in candidates)
            {
                string firstSegment = StripUsing(d).Split('.')[0];
                if (firstDiag.IndexOf("'" + firstSegment + "'", StringComparison.Ordinal) >= 0)
                    blamed.Add(d);
            }
            if (blamed.Count > 0)
            {
                foreach (string d in blamed) s_usings.Remove(d);
                string secondDiag = ValidateUsings();
                if (secondDiag == null)
                {
                    foreach (string d in candidates)
                        if (!blamed.Contains(d)) accepted.Add(StripUsing(d));
                    diagnostics = "以下 using 因不可用被剔除：" + string.Join(", ", Names(blamed));
                    return accepted;
                }
                firstDiag = secondDiag;
            }

            // 兜底：撤销本次全部新增，绝不留下"每次提交都失败"的会话
            foreach (string d in candidates) s_usings.Remove(d);
            diagnostics = "批量 using 校验失败，已全部撤销本次新增（会话保持可用）：" + Environment.NewLine + firstDiag;
            return accepted;
        }

        /// <summary>对当前会话 using 表做一次**只编译不发射**的校验；全部可用返回 null，否则返回诊断文本。</summary>
        string ValidateUsings()
        {
            try
            {
                var src = new StringBuilder();
                src.AppendLine("using System;");
                src.AppendLine(string.Join(Environment.NewLine, s_usings.ToArray()));
                src.AppendLine("public class __UsingProbe { public static void Run() { } }");

                var trees = new List<SyntaxTree>
                {
                    CSharpSyntaxTree.ParseText(src.ToString(), new CSharpParseOptions(LanguageVersion.Latest)),
                };
                var refs = new List<MetadataReference>(imageRefs.Count + s_referencePaths.Count);
                foreach (object r in imageRefs) refs.Add((MetadataReference)r);
                foreach (string p in s_referencePaths)
                {
                    try { refs.Add(MetadataReference.CreateFromFile(p)); } catch { }
                }

                CSharpCompilation comp = CSharpCompilation.Create("UsingProbe",
                    trees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                // GetDiagnostics 覆盖语法+语义（含 using 解析）而不发射 PE（B4：这是"廉价探针"的关键）
                var diags = comp.GetDiagnostics();
                bool hasError = false;
                foreach (Diagnostic d in diags)
                    if (d.Severity == DiagnosticSeverity.Error) { hasError = true; break; }
                return hasError ? DiagnosticFormatter.FormatAll(diags) : null;
            }
            catch (Exception ex)
            {
                return "using 校验异常：" + ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>
        /// 返回 <paramref name="text"/> 里第一个"引号之外"的 `//` 位置（无则 -1）。
        /// 只处理行注释定位这一件事：逐字符跟踪双引号/单引号状态与转义，够用且不引入新依赖。
        /// </summary>
        static int LineCommentIndex(string text)
        {
            bool inString = false, inChar = false;
            for (int i = 0; i < text.Length - 1; i++)
            {
                char c = text[i];
                if (c == '\\') { i++; continue; }              // 转义：跳过下一个字符
                if (c == '"' && !inChar) { inString = !inString; continue; }
                if (c == '\'' && !inString) { inChar = !inChar; continue; }
                if (c == '/' && text[i + 1] == '/' && !inString && !inChar) return i;
            }
            return -1;
        }

        /// <summary>
        /// 嵌入类体的成员一律补足可访问性：生成类里的嵌套成员"无修饰符 = private"，
        /// 而后续提交是**派生类**（Submission_N+1 : Submission_N）——private 成员按简单名找不到
        /// （实测 `static int Twice(int a) => a*2;` 提交成功却在下一次提交报 CS0103 'Twice'）。
        /// 只补足、不覆盖：用户写了 public/internal/protected/private 就原样保留。
        /// </summary>
        static MemberDeclarationSyntax EnsureAccessible(MemberDeclarationSyntax m)
        {
            var type = m as BaseTypeDeclarationSyntax;
            if (type != null)
                return HasAccessibility(type.Modifiers) || type.Modifiers.Any(SyntaxKind.FileKeyword)
                    ? m : type.AddModifiers(PublicToken());   // file 类型禁止可访问性修饰符，别加

            var del = m as DelegateDeclarationSyntax;
            if (del != null)
                return HasAccessibility(del.Modifiers) ? m : del.AddModifiers(PublicToken());

            var method = m as MethodDeclarationSyntax;
            if (method != null)
                // R5b：显式接口实现（`void IFoo.Bar(){}`）**禁止**可访问性修饰符，加了会 CS0106；
                // 这类成员只在"用户显式实现接口"时出现（此前被静默丢弃，现在至少能编译）。
                return method.ExplicitInterfaceSpecifier != null || HasAccessibility(method.Modifiers)
                    ? m : method.AddModifiers(PublicToken());

            var prop = m as PropertyDeclarationSyntax;
            if (prop != null)
                return prop.ExplicitInterfaceSpecifier != null || HasAccessibility(prop.Modifiers)
                    ? m : prop.AddModifiers(PublicToken());

            var field = m as FieldDeclarationSyntax;
            if (field != null)
                return HasAccessibility(field.Modifiers) ? m : field.AddModifiers(PublicToken());

            var ev = m as EventDeclarationSyntax;
            if (ev != null)
                return HasAccessibility(ev.Modifiers) ? m : ev.AddModifiers(PublicToken());

            return m;   // 其余形态（如 EventField/Constructor）原样嵌入：不猜、不破坏
        }

        static bool HasAccessibility(SyntaxTokenList modifiers)
            => modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.PrivateKeyword)
            || modifiers.Any(SyntaxKind.ProtectedKeyword) || modifiers.Any(SyntaxKind.InternalKeyword);

        // ⚠️ 补 token 必须带尾随空格 trivia：否则 `public` 与后面的 `int` 会粘成 `publicint`
        //（实测 CS0246 'publicint' —— 加修饰符时最容易踩的一个坑）。
        static SyntaxToken PublicToken() => SyntaxFactory.Token(SyntaxKind.PublicKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        static SyntaxToken StaticToken() => SyntaxFactory.Token(SyntaxKind.StaticKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        /// <summary>诊断用短文本（追加 F.2：拒绝未识别成员时回显原文，但别把整个文件塞进诊断）。</summary>
        static string Shorten(string text, int max)
        {
            if (text == null) return "";
            string oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "…";
        }

        static string StripUsing(string directive)
        {
            string s = directive.Trim();
            if (s.StartsWith("using ", StringComparison.Ordinal)) s = s.Substring(6).Trim();
            if (s.EndsWith(";", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            return s.Trim();
        }

        static string Names(List<string> directives)
        {
            var list = new List<string>();
            foreach (string d in directives) list.Add(StripUsing(d));
            return string.Join(", ", list.ToArray());
        }
    }
}

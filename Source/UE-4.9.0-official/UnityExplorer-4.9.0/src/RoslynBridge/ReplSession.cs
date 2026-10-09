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
        // ⚠️ **引用集契约**（任务 9 收尾轮 R3：调用点已**指名**，勿只当注释看）
        // 本表在构造后**只增不改**；任何"就地增删引用"的调用点 —— **任务 13 的引用自愈**
        // （`RoslynCompiler.CompileWithHealing` 里 `ReferenceSet.TryAdd(...)` 成功之后）—— **必须**紧接着调用
        // `InvalidateNamespaceCache()`；否则命名空间补全表会**静默停留在旧快照**（不崩、只是永远缺候选 =
        // 最难发现的那类缺陷）。**双向登记**：任务 13 简报里也有一条指向本方法
        // （`task-13-brief.md` 的"⚠️ 与补全缓存的耦合（任务 9 复审登记）"），两处互指防漏。
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
        // 任务 9（补全）：命名空间表（惰性构建 + 缓存）。**只缓存字符串**——载荷类型符号
        // （INamespaceSymbol 等）绝不许进字段（C.5：Mono 启动期那次不按 BindingFlags 过滤的字段扫描
        // 会把字段签名里的载荷 typeref 当"要解析的类型"而炸）。
        HashSet<string> s_namespaces;
        // 会话唯一标签（构造时 GUID 段）：提交程序集名 = Submission_<tag>_<n>_asm，
        // 全局唯一 → 多会话并存时按名回退（RequestingAssembly==null）也不可能答错会话。
        readonly string s_sessionTag = Guid.NewGuid().ToString("N").Substring(0, 8);

        int s_submission;
        bool s_disposed;
        Assembly s_lastAssembly;
        /// <summary>生成类继承链根（默认 <see cref="HostBaseTypeName"/>；离线测试可注入以绕过宿主程序集）。</summary>
        readonly string s_hostBaseTypeName;

        public object LastValue { get; private set; }
        public CompileResult LastResult { get; private set; }

        /// <summary>
        /// 最近一次**成功**提交的编译产物程序集（任务 10：动态 Harmony 补丁要从产物里取回补丁类型）。
        /// 语义 = `Submit` 里 `Assembly.Load(pe)` 的产物；失败提交不推进（与 s_lastAssembly 的赋值点一致）。
        /// ⚠️ 消费方必须知道：REPL 会把本次提交的顶层成员**嵌进生成类** `Submission_N`
        /// （见 <see cref="BuildSubmissionSource"/>），故产物里的类型**不是**顶层类型 ——
        /// 按全名取类型请用 <see cref="DynamicPatch.Locate"/>（扫描型），不要 `Assembly.GetType(用户写的名字)`。
        /// </summary>
        public Assembly LastAssembly => s_lastAssembly;

        // A1（合并修复轮）观测点：引用集大小恒等于入参集合（开关不得放大它，测试断言用）。
        public int ReferenceCount => s_referencePaths.Count;

        /// <summary>§19：生成类的**继承链根** = 宿主助手基类（在 UE 程序集里，见 <see cref="ReplHostBase"/>）。</summary>
        public const string HostBaseTypeName = "UnityExplorer.RoslynBridge.ReplHostBase";

        /// <summary>
        /// §19 纯函数（离线可断言）：第 <paramref name="submissionIndex"/> 个提交（从 1 起）的**基类全名**。
        /// 第 1 个提交继承**助手基类**（于是 `Log(...)`/`Inspect(...)`/`GetVars()` 等裸名在 `Run()` 里直接可用，
        /// 且**继承成员优先于 using 导入的类型**——这正是 mcs 路径 `ScriptInteraction.Log` 遮蔽 `Verse.Log` 类型的机制）；
        /// 之后每个提交继承上一个提交，形成线性链（与改造前一致，只换了链根）。
        /// 调用方需保证 <paramref name="hostBaseTypeName"/> 在提交编译的引用集里（宿主自引用，§19 (i)）。
        /// </summary>
        public static string BaseTypeNameFor(int submissionIndex, string hostBaseTypeName = HostBaseTypeName)
            => submissionIndex <= 1 ? hostBaseTypeName : "Submission_" + (submissionIndex - 1);

        public ReplSession(IEnumerable<string> referencePaths)
            : this(referencePaths, HostBaseTypeName) { }

        /// <summary>
        /// <paramref name="hostBaseTypeName"/>：生成类继承链根（默认 <see cref="HostBaseTypeName"/>）。
        /// 离线测试无法解析宿主程序集，故留此注入位以继续单测 REPL 机制本身
        /// （宿主基类的形态另有 §19 的生成源码/纯函数断言覆盖）。
        /// </summary>
        public ReplSession(IEnumerable<string> referencePaths, string hostBaseTypeName)
        {
            s_referencePaths = new List<string>(referencePaths);
            s_hostBaseTypeName = string.IsNullOrEmpty(hostBaseTypeName) ? null : hostBaseTypeName;
            // 跨提交继承链的运行期绑定：Assembly.Load(byte[]) 产物不进默认加载上下文，
            // Submission_N 按名解析基类 Submission_{N-1} 时默认探测必失败（实测
            // FileNotFoundException → ReflectionTypeLoadException → GetType 返回 null）。
            // 自挂 AssemblyResolve：只应答"请求方是本会话提交程序集"且本会话按名持有的程序集。
            // §19 追加：把**宿主程序集**（自引用）也登记进来（S-次要项留档：本应答器只按 FullName 命中
            // **自身**或本会话提交件，且仅当请求方就是本会话提交程序集时才应答 ⇒ 对第三方零影响）——提交类现在还要解析基类 `ReplHostBase`，
            // 而 Unity Mono 的 typeref 绑定一向脆弱（C.5 全轮教训）：宿主虽已在默认上下文，
            // 仍按名兜底应答一次（Identity/FullName 匹配，只有本会话提交程序集发起的请求才走这里）。
            try
            {
                Assembly host = Assembly.GetExecutingAssembly();
                if (host != null && !s_sessionAssemblies.Contains(host)) s_sessionAssemblies.Add(host);
            }
            catch { /* 动态程序集无 GetName：跳过兜底，正常绑定仍可解析宿主 */ }
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
            // §19：只动继承链根——n==1 现在继承**助手基类**（原来是"无基类 + 自声明 __result"），
            // n>1 仍是上一个提交。`__result` 由助手基类承载（见 ReplHostBase），故此处不再声明它。
            string baseName = BaseTypeNameFor(n, s_hostBaseTypeName);

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
            // §23：本提交的引用集（历次提交件 + 外部引用）**只组装一次**，供 ① `var` 推断探针（可能多次）
            // 与 ② 正式提交编译**共用** —— 否则每个 `var` 声明符都重建一遍 `CreateFromFile`（离线实测每次
            // ~35ms，见报告 §23.3）。载荷类型只作**局部变量**（C.5）；**懒建**：被响亮拒绝的提交（未识别成员）
            // 在到达编译点前就返回，一分钱都不付。
            List<MetadataReference> metadataRefs = null;

            // §23（探针可见性 = 正式提交可见性）：**先**把本次提交里的成员声明**全部**收进 `classes`，
            // 再处理语句。理由：正式提交里它们与语句同属生成类的类体，而**成员查找与声明顺序无关** ——
            // `var v = new Vec();` 后面紧跟 `class Vec { … }` 在正式编译里能解析；而 `var` 推断发生在下面的
            // 语句循环内（那时"后置成员"还没进 `classes`）⇒ 探针看不见它 ⇒ 静默退回 `object`。
            // ⚠️ 这是新机制的**倒退点**（旧实现复用整棵原 tree，顶层类型就在同一棵树里，反而没有这个盲区），
            // 靠预扫消除。两类**注定被响亮拒绝**的形态（`namespace` / 解析残渣 `IncompleteMember`）跳过：
            // 塞进探针只会污染诊断，且提交本身马上就会失败返回。
            foreach (MemberDeclarationSyntax pre in root.Members)
            {
                var preGlobal = pre as GlobalStatementSyntax;
                if (preGlobal != null)
                {
                    // 全局语句里的**局部函数**同样是生成类的成员（见下面对应分支），故同样与顺序无关
                    var preFn = preGlobal.Statement as LocalFunctionStatementSyntax;
                    if (preFn != null) classes.AppendLine(EnsureStaticPublic(preFn).ToFullString());
                    continue;
                }
                if (pre is BaseNamespaceDeclarationSyntax || pre is IncompleteMemberSyntax) continue;
                classes.AppendLine(EnsureAccessible(pre).ToFullString());
            }

            foreach (MemberDeclarationSyntax m in root.Members)
            {
                if (m is GlobalStatementSyntax g)
                {
                    StatementSyntax stmt = g.Statement;
                    // （原"缺分号 → SyntaxFactory.ParseStatement(…+';')"补丁已删：整段输入已在解析前补过
                    //   末尾分号，逐语句重解析只会拿回第一条语句、治不了被并进去的后续语句。）

                    if (stmt is LocalFunctionStatementSyntax)
                    {
                        // 追加 F 清扫（同类漏分支）：顶层 `int Twice(int a) => a * 2;` 在 Roslyn 眼里是
                        // **局部函数**（GlobalStatement 里的 LocalFunctionStatement）——原实现把它丢进
                        // Run()，于是只在本次提交内可见，**下次提交 CS0103 'Twice'**（"定义了却用不了"）。
                        // REPL 语义要求"定义了就能用" ⇒ 提升为生成类的成员；缺 static 时补上
                        //（否则后续提交的 static Run() 调不到它）。若函数体捕获了局部变量，提升后
                        // 会自然报 CS0103/CS8421——响亮且可解释，好过静默失效。
                        // 另需补 `public`：这些成员最终是**生成类的嵌套成员**，"无修饰符 = private"
                        // ⇒ 后续提交（派生类 Submission_N+1）按简单名找不到它（实测 CS0103 'Twice'）。
                        // §23：文本已由**预扫**统一收进 `classes`（`EnsureStaticPublic`，与 `var` 推断共用
                        // 同一份、且与声明顺序无关）⇒ 本分支只需保留"局部函数必须是生成类成员、**不得**落进
                        // Run()"这一分支语义（否则会掉回下面的 else：那就是 F.1 的老缺陷）。
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
                                // §23：推断探针与正式提交**同可见性** —— 同 usings（含本次待加入）+ 同引用集
                                // （外部引用 + 历次提交件 + polyfill）+ **同基类**（链根/上一提交）+ 本次提交的
                                // 成员源码（已生成的字段 + 全部类型/方法声明，见下面的预扫）。
                                if (metadataRefs == null) metadataRefs = BuildReferences();
                                ITypeSymbol inferred = GetInferredType(v, pendingUsings,
                                    fields.ToString() + classes.ToString(), metadataRefs);
                                fieldType = inferred?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
                                // 仍可能出现"真解析不了"的初始化式（void 调用、`var x;`、语法残渣…）⇒ 回落 object
                                // （与旧行为一致），随后正式提交会用真实诊断把它暴露出来（CS0815/CS0029 等）。
                                // ⚠️ 正常写法（`new StringBuilder()` / 引用会话变量 / 用户自定义类型）现在都能解析，
                                // 不再走进这支——那正是 §23 修的静默退化。
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
                    // §23：源码文本已由**预扫**收进 `classes`（顺序无关），此处只登记名字供 GetClasses() 展示。
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

            // 3) 末条语句若是表达式 → 捕获为值通道（`__result = <expr>;`）
            // §21：**void 表达式不能进值通道**（`Cannot implicitly convert type 'void' to 'object'` = CS0029）。
            // 机制 = **错误驱动回退（两遍，禁循环）**：先按常规生成并编译；**当且仅当** pass 1 失败且诊断里
            // 存在"指向注入赋值行"的 CS0029 时，才以"末表达式原样语句、不加赋值"的形态重生成并重编译一次。
            // 为何不用"每次先建语义模型"：单提交已 ~600-1500ms，每次多一遍全量编译不划算；回退只在 void 末条发生。
            // 为何不做"助手名白名单"：那只修我们自己的 12 个助手，用户自己的 void API
            //（`Verse.Log.Message(...)`/`Verse.Widgets.BeginGroup(...)` 等）照样 CS0029，而 mcs 路径都能跑 ⇒ 大面积不对等。
            ExpressionStatementSyntax lastExpr = null;
            string lastExprText = null;
            if (statements.Count > 0 && statements[statements.Count - 1] is ExpressionStatementSyntax es)
            {
                lastExpr = es;
                lastExprText = es.Expression.ToString();
                statements.RemoveAt(statements.Count - 1);
            }

            // §23：与 `var` 推断**共用同一份**引用集（懒建：本次提交没有 `var` 时在这里才第一次组装）
            if (metadataRefs == null) metadataRefs = BuildReferences();

            string asmName = "Submission_" + s_sessionTag + "_" + n + "_asm";
            int assignmentLine;
            string srcText = BuildSubmissionSource(className, baseName, fields, statements, lastExpr,
                assignValue: lastExpr != null, pendingUsings: pendingUsings, classesSource: classes,
                out assignmentLine);
            SyntaxTree generatedTree;
            CSharpCompilation compilation = CreateSubmissionCompilation(srcText, asmName, metadataRefs,
                out generatedTree);
            Diagnostic[] firstDiag;
            CompileResult result = EmitOnce(compilation, srcText, notes, out firstDiag);

            if (lastExpr != null && result.ErrorCount > 0
                && IsVoidAssignmentError(compilation, generatedTree, firstDiag, assignmentLine))
            {
                // 回退：同一次提交最多再编译一遍（禁循环）；第二遍的诊断如实上报（绝不吞真实错误）
                int ignoredLine;
                srcText = BuildSubmissionSource(className, baseName, fields, statements, lastExpr,
                    assignValue: false, pendingUsings: pendingUsings, classesSource: classes,
                    out ignoredLine);
                compilation = CreateSubmissionCompilation(srcText, asmName, metadataRefs, out generatedTree);
                Diagnostic[] secondDiag;
                result = EmitOnce(compilation, srcText, notes, out secondDiag);
                result.Passes = 2;
                result.FallbackForVoid = true;
            }

            byte[] pe = result.PeBytes;

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

        /// <summary>
        /// `var` 的字段类型推断（**§23 修复轮，对端游戏内实测挖出的高影响缺陷**）：与正式提交用
        /// **同一套可见性** —— `using System;` + 会话 usings + **本次提交待加入的 usings** +
        /// 引用集（外部引用 + **历次提交件 `Submission_N`**）+ polyfill + **与本次提交相同的基类** +
        /// 本次提交的成员源码（已生成字段 + 全部类型/方法声明）。
        /// 旧实现只用「用户 tree + `s_referencePaths`」⇒ 三处硬缺口：
        ///  ① **缺 usings**：`var sb = new StringBuilder();` 需要 `using System.Text;`，而 `System.Text`
        ///    就在**默认 using** 里 ⇒ 正式提交编译得过、探针却解析不到 ⇒ 回落 `object`；
        ///  ② **缺历次提交件**：初始化式引用会话变量/类型（`var y = sb2.ToString();` / `new Vec(2,5)`）时，
        ///    探针看不到 `Submission_N` ⇒ 同样回落 `object`；
        ///  ③ **缺基类**：第 1 个提交的助手成员（`GetVars()` 等）只在宿主基类上 ⇒ `var g = GetVars();` 回落 `object`。
        /// 后果是**静默**的：字段类型变成 `object`，`GetVars()` 显示 `(Object)`、`sb.Append("x")` 报
        /// CS1929/CS1061（'object' 没有 `Append`）——看不出根因（详见报告 §23.4 可见症状链）。
        /// 机制选择：`var x = &lt;init&gt;;` 的推断类型 **恒等于初始化式的自然类型**，故直接复用表达式探针
        /// （语义模型：**不 Emit、不 Load、不执行**，零副作用），不另建第二套编译。**不**复用 `Submit` 里的
        /// compilation：推断发生在生成源码**之前**（字段类型正是生成源码的输入）⇒ 复用只能"先用 object 编译
        /// 一遍、再重生成重编译"，代价是**两次全量提交编译**（离线实测每次 ~130ms，游戏内更慢），而探针
        /// 单次只 ~1ms 量级（实测见报告 §23.3）；且探针是补全路径已在用的同一个（`ResolveExpressionSymbol`），
        /// 不引入第二套机制。
        /// </summary>
        ITypeSymbol GetInferredType(VariableDeclaratorSyntax v, IEnumerable<string> pendingUsings,
            string pendingMembers, IEnumerable<MetadataReference> suppliedRefs)
        {
            if (v == null || v.Initializer == null) return null;   // `var x;` 非法（正式提交自会报 CS0815）
            return ResolveExpressionSymbol(v.Initializer.Value.ToString(), pendingUsings, pendingMembers,
                suppliedRefs);
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
        ///    Assembly.Load + 一个新 Submission 程序集；`ResetConsole` 的默认 using（项数 = `DefaultUsings.BuiltIn.Length`）就是
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

        // ══════════════ 任务 9：补全的数据源（语义模型 / 命名空间元数据） ══════════════

        /// <summary>
        /// 补全用：把接收者表达式交给**语义模型**判定它的类型符号；判不出返回 null（绝不抛）。
        /// 机制 = 建一个**探测类**（继承**最后一个提交类** ⇒ 会话变量/用户类型/助手都按简单名可见，
        /// 这正是继承链的用途），把表达式注入 `public static object Get() { return &lt;expr&gt;; }`，
        /// 然后**只取语义模型**：**不 Emit、不 Assembly.Load、不 Invoke**。三条理由：
        ///  ① **零副作用**：发射+执行会把接收者表达式**真跑一遍**（属性 getter 有副作用/昂贵）——
        ///    "用户敲字"不该触发游戏逻辑；
        ///  ② **不涨内存**：每次按键 Assembly.Load 一个探测程序集，而字节加载**不可卸载**；
        ///  ③ **快一个量级**：无 IL 生成、无 PE、无加载、无 JIT（每次按键都跑）。
        /// <paramref name="extraUsings"/>：本次提交**待加入**的 using（正式提交成功后才进 `s_usings`）；
        /// <paramref name="pendingMembers"/>：本次提交的成员源码（`var` 推断用：字段按语句顺序**已生成**的部分
        /// + **全部**类型/方法声明 —— 成员查找与声明顺序无关，故后置的 `class Vec` 也必须在）；已有成功提交时
        /// 会话变量/用户类型另经由**基类链**（`Submission_{s_submission}`）与**历次提交件引用**可见。
        /// </summary>
        internal ITypeSymbol ResolveExpressionSymbol(string expression, IEnumerable<string> extraUsings = null,
            string pendingMembers = null, IEnumerable<MetadataReference> suppliedRefs = null)
        {
            if (string.IsNullOrEmpty(expression)) return null;
            try
            {
                // §23：探针类**故意取名为"正在生成的那个提交类名"**（`Submission_{s_submission + 1}`）。
                // 理由：本次提交里声明的**嵌套类型**（如 `class Vec`）在语义模型里的显示名会带上**容器类名**
                // （`<容器>.Vec`），而调用方要把这个显示名写回提交源码当字段类型。取名 `__CompletionProbe_xxx`
                // 时显示名是 `__CompletionProbe_xxx.Vec` —— 提交源码里根本没有这个名字（实测 CS0246，见报告 §23.2）；
                // 取名 `Submission_N` 时显示名与正式提交**逐字一致**（`Submission_N.Vec`，写在提交类体里合法），
                // 无需对显示名做任何字符串前缀剥离。探针是独立编译，真正的 `Submission_N` 此刻尚未存在
                // （`imageRefs` 只到 N-1）⇒ 无重名冲突。
                string probeName = "Submission_" + (s_submission + 1);
                var src = new StringBuilder();
                AppendProbeUsings(src, extraUsings);
                // §23：探针类继承**与正在生成的那个提交完全相同的基类**（`BaseTypeNameFor(s_submission + 1, …)`
                // 就是该提交的链根）：第 1 个提交 ⇒ 宿主助手基类（`ReplHostBase`，由 §19 (i) 宿主自引用保证
                // 它在引用集里）⇒ 助手成员（`GetVars()`/`GetUsing()`/`GetClasses()`/`CurrentTarget`…）按简单名
                // 可见；其后 ⇒ `Submission_{s_submission}`。旧实现对第 1 个提交**没有基类** ⇒ 助手成员解析不到 ⇒
                // `var g = GetVars();` 静默退回 object（§23 缺陷类的第三个盲区）。基类名传 null（离线"无宿主"
                // 形态，§19）时照旧无基类；基类解析不出时也只是该表达式退回 object——与旧行为同档，
                // 不会更糟（提交侧本就会 CS0246 ReplHostBase，那不是本方法能修的）。
                string probeBase = BaseTypeNameFor(s_submission + 1, s_hostBaseTypeName);
                src.AppendLine("public class " + probeName
                    + (probeBase != null ? " : " + probeBase : ""));
                src.AppendLine("{");
                if (!string.IsNullOrEmpty(pendingMembers)) src.AppendLine(pendingMembers);
                src.AppendLine("    public static object Get() { return " + expression + "; }");
                src.AppendLine("}");

                SyntaxTree tree = CSharpSyntaxTree.ParseText(src.ToString(),
                    new CSharpParseOptions(LanguageVersion.Latest));
                var trees = new List<SyntaxTree>
                {
                    tree,
                    // 与提交侧同款：用户类型可能引用 polyfill 提供的类型（isExternalInit/required/params 集合）
                    CSharpSyntaxTree.ParseText(PolyfillSource.Code, new CSharpParseOptions(LanguageVersion.Latest)),
                };
                // suppliedRefs：调用方（`Submit`）传**同一次提交内共享**的引用集，避免每个 `var` 都重建一遍
                //（`CreateFromFile` 的元数据读取是这条路径的主要成本）；补全路径不传 ⇒ 各自组装。
                IEnumerable<MetadataReference> refs = suppliedRefs != null
                    ? suppliedRefs : (IEnumerable<MetadataReference>)BuildReferences();

                CSharpCompilation compilation = CSharpCompilation.Create("CompletionProbe",
                    trees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                SemanticModel model = compilation.GetSemanticModel(tree);

                // 定位注入的表达式：全树只有我们这一句 return（显式循环——禁 LINQ + Roslyn 类型 lambda，
                // 非捕获 lambda 也会被缓存成 `<>c` 的 `Func<SyntaxNode,bool>` 字段，泛型实参是载荷类型 ⇒ C.5 炸）。
                ExpressionSyntax expr = null;
                foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
                {
                    var ret = node as ReturnStatementSyntax;
                    if (ret == null || ret.Expression == null) continue;
                    expr = ret.Expression;
                    break;
                }
                if (expr == null) return null;

                // ① 值表达式（`sb` / `list.Count`）
                ITypeSymbol value = model.GetTypeInfo(expr).Type;
                if (value != null && value.TypeKind != TypeKind.Error) return value;
                // ② 类型名（`System.Text.StringBuilder`）：类型表达式没有"值类型"，符号才是类型
                var named = model.GetSymbolInfo(expr).Symbol as INamedTypeSymbol;
                if (named != null) return named;
                // ③ 其余（命名空间 `System`、解析失败的名字等）⇒ null，调用方回落命名空间来源
                return null;
            }
            catch { return null; }
        }

        /// <summary>补全/`var` 推断探针共用的 **usings 头**：`using System;` + 会话已接受 + **本次提交待加入**。</summary>
        void AppendProbeUsings(StringBuilder src, IEnumerable<string> extraUsings)
        {
            src.AppendLine("using System;");
            foreach (string d in s_usings) src.AppendLine(d);
            if (extraUsings == null) return;
            foreach (string d in extraUsings)
                if (!s_usings.Contains(d)) src.AppendLine(d);
        }

        /// <summary>补全/`var` 推断探针共用的**引用集**（与正式提交同款：**历次提交件** + 外部引用）。
        /// C.5：只作**局部/参数/返回值**流转，绝不留字段（`MetadataReference` 是载荷类型；擦除版见 `imageRefs`）。
        /// 调用点：① `Submit` 的**懒建**共享引用集（同一次提交内多个 `var` 推断与正式提交编译**共用一份**，
        /// 省掉重复的 `CreateFromFile` 元数据读取——离线实测每次组装 ~35ms，见报告 §23.3）；
        /// ② 补全路径（`RoslynCompletion.Complete` 每次按键）走默认参数、各自组装（载荷类型禁止进字段 ⇒
        /// 无法缓存；这是 §22 起就有的既有成本，非本节引入）。
        /// ⚠️ 脆弱点（原 `RoslynRefsFromPaths` 的教训搬来这里，方法随 §23 修复一起退役）：本循环**不许**改回
        /// `paths.Where(File.Exists).Select(p =&gt; MetadataReference.CreateFromFile(p))` —— 非捕获 lambda 也会被
        /// 编译器缓存成 `&lt;&gt;c` 的 `Func&lt;string,MetadataReference&gt;` 字段，泛型实参是**载荷类型** ⇒
        /// 启动期字段扫描照样炸（与 `s_imageRefs` 同款炸法；捕获式更糟：`&lt;&gt;c__DisplayClass*` 实例字段）。</summary>
        List<MetadataReference> BuildReferences()
        {
            var refs = new List<MetadataReference>(imageRefs.Count + s_referencePaths.Count);
            foreach (object r in imageRefs) refs.Add((MetadataReference)r);   // 历次提交件：类型身份在这里
            foreach (string p in s_referencePaths)
            {
                try { refs.Add(MetadataReference.CreateFromFile(p)); } catch { }
            }
            return refs;
        }

        /// <summary>命名空间源里"读不出元数据"的引用条数（0 = 全部可用）。&gt;0 时补全侧会**报一条**日志
        /// （R4③：不静默跳过——少候选但不说，等于让后人查一个不存在的 bug）。</summary>
        public int NamespaceReferenceFailureCount { get; private set; }

        /// <summary>真正折进命名空间表的引用条数（= 可读的 `s_referencePaths` 条数 + `imageRefs` 条数）。
        /// 用途：把"提交件也进了来源"这件事变成**可断言**的事实（命名空间本身在离线装置里不可观测，见报告 §22.9）。</summary>
        public int NamespaceReferenceCount { get; private set; }

        /// <summary>
        /// **引用集变更后必须调用**（任务 9 收尾轮 R3）：清空命名空间表缓存，下次 <see cref="AllNamespaces"/> 重建。
        /// 契约与调用点见 `s_referencePaths` 声明处：**任务 13 的引用自愈在 `ReferenceSet.TryAdd` 成功后必须调用本方法**。
        /// </summary>
        public void InvalidateNamespaceCache() => s_namespaces = null;

        /// <summary>
        /// 会话引用里的**命名空间全集**（惰性构建 + 缓存）。**只读 Roslyn 元数据**——`MetadataReference`
        /// 分派只 mmap/读元数据、**不加载程序集**（把本体 106 个 dll 真载入进程的代价见 §3.1 老教训）。
        /// </summary>
        public IEnumerable<string> AllNamespaces()
        {
            if (s_namespaces != null) return s_namespaces;
            var set = new HashSet<string>(StringComparer.Ordinal);
            int failures = 0;
            int folded = 0;
            try
            {
                var refs = new List<MetadataReference>();
                // R4④（复审必修）：**会话自己的提交件也算来源**——用户定义的类型/polyfill 的命名空间都在
                // 提交件里，漏掉它们 = "我用过的命名空间却补不出来"。载荷类型擦除见 imageRefs 声明处。
                foreach (object r in imageRefs)
                {
                    var mr = r as MetadataReference;
                    if (mr == null) continue;
                    refs.Add(mr);
                    folded++;
                }
                foreach (string p in s_referencePaths)
                {
                    try { refs.Add(MetadataReference.CreateFromFile(p)); folded++; }
                    catch { failures++; }   // R4③：不静默——计入失败数，补全侧会报一条
                }
                CSharpCompilation comp = CSharpCompilation.Create("NamespaceProbe", null, refs);
                foreach (MetadataReference r in refs)
                {
                    IAssemblySymbol asm = null;
                    try { asm = comp.GetAssemblyOrModuleSymbol(r) as IAssemblySymbol; } catch { }
                    if (asm == null) { failures++; continue; }
                    CollectNamespaces(asm.GlobalNamespace, set);
                }
            }
            catch { failures++; /* 极端：整体失败 ⇒ 表退化为已收集部分，且失败数在"不静默"通道上 */ }
            NamespaceReferenceFailureCount = failures;
            NamespaceReferenceCount = folded;
            s_namespaces = set;
            return s_namespaces;
        }

        static void CollectNamespaces(INamespaceSymbol ns, HashSet<string> sink)
        {
            foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            {
                string name = child.ToDisplayString();
                if (name.Length > 0) sink.Add(name);
                CollectNamespaces(child, sink);
            }
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
        /// §21：生成提交源码（**方法而非 lambda**——闭包会捕获 `StatementSyntax` 等**载荷类型**局部，
        /// 生成 `&lt;&gt;c__DisplayClass*` 字段 ⇒ C.5 护栏（启动期字段扫描）会再次炸）。
        /// <paramref name="assignValue"/> = 末表达式进值通道（`__result = &lt;expr&gt;;`）；false 时原样当语句执行。
        /// </summary>
        string BuildSubmissionSource(string className, string baseName, StringBuilder fields,
            IEnumerable<StatementSyntax> statements, ExpressionStatementSyntax lastExpr, bool assignValue,
            IEnumerable<string> pendingUsings, StringBuilder classesSource, out int assignmentLine)
        {
            assignmentLine = -1;
            var body = new StringBuilder();
            foreach (StatementSyntax st in statements) body.AppendLine("        " + st.ToFullString().Trim());
            string assignText = null;
            if (lastExpr != null)
            {
                if (assignValue)
                {
                    assignText = "__result = " + lastExpr.Expression + ";";
                    body.AppendLine("        " + assignText);
                }
                else
                {
                    body.AppendLine("        " + lastExpr.ToFullString().Trim());   // void：原样语句，不捕获
                }
            }

            var src = new StringBuilder();
            src.AppendLine("using System;");
            // B1/D ①：**本次新增**的 using 也要进本次编译 —— 否则它无法在自己的提交里被校验，
            // 只会"静默入表、从此每次提交都失败"（实测症状：连 `new[]{1,2,3}.Sum()` 都报
            // CS0246 'UniverseLib'，而那条 using 属于更早的提交）。失败即不提交（见方法尾部）。
            var emittedUsings = new List<string>(s_usings);
            foreach (string d in pendingUsings)
                if (!emittedUsings.Contains(d)) emittedUsings.Add(d);
            src.AppendLine(string.Join(Environment.NewLine, emittedUsings.ToArray()));
            // §19：继承链根是助手基类；`__result` 现由基类承载（ReplHostBase.__result），故此处不再声明。
            // 若注入的 hostBaseTypeName 为空（离线测试用"无基类"形态），则退回自声明 __result（等价旧行为）。
            src.AppendLine($"public class {className}" + (baseName != null ? " : " + baseName : ""));
            src.AppendLine("{");
            if (baseName == null) src.AppendLine("    public static object __result;");
            src.Append(fields);
            src.AppendLine("    public static void Run()");
            src.AppendLine("    {");
            src.Append(body);
            src.AppendLine("    }");
            src.Append(classesSource);
            src.AppendLine("}");

            string text = src.ToString();
            // §21：**生成阶段就确定注入赋值所在行**（0 基，与 Roslyn LineSpan 同口径）。
            // 用"最后出现一次"定位（注入赋值是本方法体最后一条语句；用户自己若写过 `__result = …;`
            // 也一定在它前面）⇒ 零手算、零模糊规则（不搞"落在文件末尾附近"这类启发式）。
            if (assignText != null)
                assignmentLine = LineOfLastOccurrence(text, assignText);
            return text;
        }

        /// <summary>文本里**最后一次**出现 <paramref name="needle"/> 所在的 0 基行号（找不到 -1）。</summary>
        static int LineOfLastOccurrence(string text, string needle)
        {
            int idx = text == null ? -1 : text.LastIndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return -1;
            int line = 0;
            for (int i = 0; i < idx; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        /// <summary>生成源码 + 载荷 polyfill 编成一个 compilation；同时回传生成源码那棵树（诊断位置判定要用）。</summary>
        static CSharpCompilation CreateSubmissionCompilation(string srcText, string assemblyName,
            List<MetadataReference> metadataRefs, out SyntaxTree generatedTree)
        {
            generatedTree = CSharpSyntaxTree.ParseText(srcText, new CSharpParseOptions(LanguageVersion.Latest));
            var trees = new List<SyntaxTree>
            {
                generatedTree,
                CSharpSyntaxTree.ParseText(PolyfillSource.Code, new CSharpParseOptions(LanguageVersion.Latest)),
            };
            return CSharpCompilation.Create(assemblyName, trees, metadataRefs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        /// <summary>编译一次并填 CompileResult（诊断原样回传，供 §21 过滤器按"错误码 + 位置"精确判定）。</summary>
        static CompileResult EmitOnce(CSharpCompilation compilation, string srcText, StringBuilder notes,
            out Diagnostic[] diagnostics)
        {
            var result = new CompileResult();
            result.Notes = notes.ToString().TrimEnd();
            result.GeneratedSource = srcText;
            using (var ms = new MemoryStream())
            {
                var emit = compilation.Emit(ms);
                diagnostics = new Diagnostic[emit.Diagnostics.Length];
                emit.Diagnostics.CopyTo(diagnostics, 0);
                result.Diagnostics = DiagnosticFormatter.FormatAll(emit.Diagnostics);
                // C.5-lite：原 `Count(d => d.Severity == DiagnosticSeverity.Error)` 虽非捕获式，
                // 但编译器会把 lambda 缓存为 <>c.<>9__* 静态字段（Func<Diagnostic,bool>——泛型实参
                // =载荷类型，字段签名解析连带解析实参 typeref，与 List<MetadataReference> 同款炸法）
                // → 改显式循环，杜绝委托缓存字段。
                // ⚠️ 脆弱点：载荷类型仅以方法组/表达式形式出现，禁止改为捕获式（否则闭包
                // <>c__DisplayClass* 字段复活、启动扫描再炸）；非捕获 lambda 亦禁止（<>c 委托缓存字段）。
                result.ErrorCount = 0;
                foreach (Diagnostic d in diagnostics)
                    if (d.Severity == DiagnosticSeverity.Error) result.ErrorCount++;
                result.Success = emit.Success && result.ErrorCount == 0;
                result.PeBytes = result.Success ? ms.ToArray() : null;
            }
            return result;
        }

        /// <summary>
        /// §21 过滤器（**两重判定，都在同一个 compilation 上完成 ⇒ 不额外全量编译**）：
        ///  ① 位置：pass 1 的诊断里存在 **CS0029**，且 `Location.SourceTree` 就是生成树、
        ///    **行号 == 生成阶段记录的注入赋值行号**（`assignmentLine`，对端实测：末条 void 调用
        ///    稳定落在 `__result = &lt;expr&gt;;` 那一行，如 (18,20)/(19,25)）；
        ///  ② 语义：那条 `__result = &lt;expr&gt;;` 的右值**自然类型**是 `void`。
        /// 为什么两重都要（9a 防线）：用户写 `(object)SomeVoidCall()` 时，cast 的 CS0029 **也在注入行上**，
        /// 只看位置就会过度回退；此时右值类型是 object（非 void）⇒ ② 挡下，原样报用户自己的错。
        /// 任何查询失败/右值非 void ⇒ false（保守，绝不误排除合法值表达式）。
        /// </summary>
        static bool IsVoidAssignmentError(CSharpCompilation compilation, SyntaxTree generatedTree,
            Diagnostic[] pass1, int assignmentLine)
        {
            try
            {
                if (pass1 == null || assignmentLine < 0) return false;

                foreach (Diagnostic d in pass1)
                {
                    if (d.Severity != DiagnosticSeverity.Error || d.Id != "CS0029") continue;
                    if (d.Location == null || d.Location.SourceTree != generatedTree) continue;
                    if (d.Location.GetLineSpan().StartLinePosition.Line != assignmentLine) continue;

                    // 位置命中后再做语义确认（同一次编译的 SemanticModel）
                    foreach (SyntaxNode node in generatedTree.GetRoot().DescendantNodes())
                    {
                        var stmt = node as ExpressionStatementSyntax;
                        if (stmt == null) continue;
                        if (stmt.GetLocation().GetLineSpan().StartLinePosition.Line != assignmentLine) continue;
                        var assign = stmt.Expression as AssignmentExpressionSyntax;
                        if (assign == null || assign.Left.ToString() != "__result") continue;
                        ITypeSymbol rhs = compilation.GetSemanticModel(generatedTree)
                            .GetTypeInfo(assign.Right).Type;
                        return rhs != null && rhs.SpecialType == SpecialType.System_Void;
                    }
                    return false;
                }
            }
            catch { /* 语义/位置查询失败：按"不可回退"处理（保守，保持既有行为） */ }
            return false;
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

        /// <summary>
        /// 顶层**局部函数**提升为生成类成员时的必备修饰符：缺 `static` ⇒ 后续提交的 static `Run()` 调不到它；
        /// 缺 `public` ⇒ 派生类（Submission_N+1）按简单名找不到（实测 CS0103 'Twice'）。§23 起被**预扫**与
        /// 语句循环共用（同一份文本，防两处漂移）。
        /// </summary>
        static LocalFunctionStatementSyntax EnsureStaticPublic(LocalFunctionStatementSyntax fn)
        {
            if (!fn.Modifiers.Any(SyntaxKind.StaticKeyword)) fn = fn.AddModifiers(StaticToken());
            if (!HasAccessibility(fn.Modifiers)) fn = fn.AddModifiers(PublicToken());
            return fn;
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

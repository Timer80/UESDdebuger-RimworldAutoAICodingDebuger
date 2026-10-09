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
        readonly List<MetadataReference> s_imageRefs = new List<MetadataReference>();
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

        /// <summary>摘除 AssemblyResolve 并清空会话程序集注册；幂等，之后不得再 Submit。</summary>
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
            int n = s_submission + 1;
            string className = "Submission_" + n;
            string baseName = n == 1 ? null : "Submission_" + (n - 1);

            SyntaxTree tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest));
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            // 1) using 指令：吞进会话表，不进类体
            foreach (UsingDirectiveSyntax u in root.Usings)
            {
                string ns = u.Name.ToString();
                if (!s_usings.Contains(ns)) s_usings.Add(ns);
            }

            // 2) 顶层变量声明 → 提升为静态字段；顶层类定义 → 直接放进本提交
            var fields = new StringBuilder();
            var classes = new StringBuilder();
            var statements = new List<StatementSyntax>();
            foreach (MemberDeclarationSyntax m in root.Members)
            {
                if (m is GlobalStatementSyntax g)
                {
                    StatementSyntax stmt = g.Statement;
                    // 分号规范化：REPL 输入常缺末尾分号；缺分号时解析器会错误恢复——
                    // 裸标识符（如 `y`）被解析成"缺标识符的局部声明"（type=[y]、ident isMissing），
                    // 按声明提升会生成 `public static y ;` → CS1519。补分号重新解析得真实结构
                    //（`y;` → 表达式语句；`var x = 5` → 合法声明仍走提升）。
                    bool semiMissing =
                        (stmt is ExpressionStatementSyntax se && se.SemicolonToken.IsMissing)
                        || (stmt is LocalDeclarationStatementSyntax sl && sl.SemicolonToken.IsMissing);
                    if (semiMissing) stmt = SyntaxFactory.ParseStatement(stmt.ToString().Trim() + ";");

                    if (stmt is LocalDeclarationStatementSyntax local && local.UsingKeyword == default)
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
                            }
                            fields.AppendLine($"    public static {fieldType} {v.Identifier.Text};");
                            if (v.Initializer != null)
                                statements.Add(SyntaxFactory.ParseStatement(
                                    $"{v.Identifier.Text} = {v.Initializer.Value};"));
                            if (!s_vars.Any(p => p.Key == v.Identifier.Text))
                                s_vars.Add(new KeyValuePair<string, Type>(v.Identifier.Text, null));
                        }
                    }
                    else
                    {
                        statements.Add(stmt);
                    }
                }
                else if (m is ClassDeclarationSyntax || m is StructDeclarationSyntax
                         || m is InterfaceDeclarationSyntax || m is EnumDeclarationSyntax)
                {
                    classes.AppendLine(m.ToFullString());
                    string name = m is BaseTypeDeclarationSyntax b ? b.Identifier.Text : null;
                    if (name != null && !s_classes.Contains(name)) s_classes.Add(name);
                }
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
            foreach (string ns in s_usings) src.AppendLine("using " + ns + ";");
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

            var metadataRefs = new List<MetadataReference>(s_imageRefs);
            foreach (string p in s_referencePaths)
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(p)); } catch { }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                "Submission_" + s_sessionTag + "_" + n + "_asm", trees, metadataRefs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var result = new CompileResult();
            byte[] pe;
            using (var ms = new MemoryStream())
            {
                var emit = compilation.Emit(ms);
                result.Diagnostics = DiagnosticFormatter.FormatAll(emit.Diagnostics);
                result.ErrorCount = emit.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
                result.Success = emit.Success && result.ErrorCount == 0;
                pe = result.Success ? ms.ToArray() : null;
                result.PeBytes = pe;
            }

            LastResult = result;
            if (!result.Success) return result;   // 失败：会话状态不变（不推进 submission 计数）

            s_imageRefs.Add(MetadataReference.CreateFromImage(pe));
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
                    s_referencePaths.Where(File.Exists).Select(p => MetadataReference.CreateFromFile(p)),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                SemanticModel model = comp.GetSemanticModel(tree);
                return model.GetTypeInfo(v.Initializer.Value).Type;
            }
            catch { return null; }
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
            Type start = s_lastAssembly?.GetType("Submission_" + s_submission);
            foreach (var kv in s_vars)
            {
                object val = null;
                try
                {
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
    }
}

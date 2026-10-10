using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace UnityExplorer.RoslynBridge
{
    public sealed class CompileResult
    {
        public bool Success;
        public byte[] PeBytes;
        public string Diagnostics = "";
        public int ErrorCount;
        /// <summary>成功提交的可读提示（B3：如"匿名类型已按 object 提升"）。与 Diagnostics 分开存，
        /// 免得把提示混进错误文本、或顶掉本次的值回显（调用方按需展示）。</summary>
        public string Notes = "";
        /// <summary>本次提交**实际编译的生成源码**（REPL 会重写用户输入：usings/字段提升/类体包裹）。
        /// 诊断里的行列号指的就是它；排错时看它比猜快一个数量级（internal：只给同程序集/离线测试用）。</summary>
        internal string GeneratedSource = "";
        /// <summary>§21：本次提交实际编译了几遍（常规 1；末表达式是 void 走回退 = 2，**最多 2、禁循环**）。</summary>
        internal int Passes = 1;
        /// <summary>§21：是否走了"void 末表达式 → 不进值通道"的回退路径。</summary>
        internal bool FallbackForVoid;
    }

    /// <summary>parse → compile → emit 到内存。不负责加载（调用方决定 Assembly.Load 或 CreateFromImage）。</summary>
    public static class RoslynCompiler
    {
        public static CompileResult Compile(string assemblyName, string source,
                                            IEnumerable<string> referencePaths, bool includePolyfills,
                                            IEnumerable<string> extraSources = null,
                                            string extraBaseSource = null)
        {
            // C.5-lite（任务 8）：ParseOptions/CompileOptions 原为 static readonly 载荷类型字段——
            // RimWorld 启动期 GetFields 字段扫描解析字段签名时不按 BindingFlags 过滤，载荷未加载
            // 即 TypeLoadException。载荷类型只许活在方法体内（局部变量对扫描不可见），
            // 故降为每次调用局部构造（开销相对编译本身可忽略）。
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
            var compileOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, parseOptions) };
            if (extraSources != null)
                foreach (string s in extraSources)
                    trees.Add(CSharpSyntaxTree.ParseText(s, parseOptions));
            if (includePolyfills)
                trees.Add(CSharpSyntaxTree.ParseText(PolyfillSource.Code, parseOptions));

            var refs = new List<MetadataReference>();
            foreach (string p in referencePaths)
            {
                try { refs.Add(MetadataReference.CreateFromFile(p)); }
                catch { /* 非托管/损坏 dll：跳过，Roslyn 只读元数据 */ }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName, trees, refs, compileOptions);

            var result = new CompileResult();
            using (var ms = new MemoryStream())
            {
                EmitResult emit = compilation.Emit(ms);
                result.ErrorCount = 0;
                foreach (Diagnostic d in emit.Diagnostics)
                    if (d.Severity == DiagnosticSeverity.Error) result.ErrorCount++;
                result.Diagnostics = DiagnosticFormatter.FormatAll(emit.Diagnostics);
                result.Success = emit.Success && result.ErrorCount == 0;
                if (result.Success)
                    result.PeBytes = ms.ToArray();
            }
            return result;
        }

        /// <summary>
        /// 任务 13：诊断驱动的引用自愈重试入口（<b>离线测试 + 契约入口</b>；生产主循环在
        /// <see cref="ReplSession.Submit"/> 的 emit 失败路径——`Evaluate` 首行即 Submit，不走本方法）。
        /// ≤5 轮：失败 → <see cref="ReferenceHealer.Resolve"/> → 加引用 →
        /// <paramref name="session"/> 非 null 时走同一钩子 <c>OnReferenceAdded()</c>
        /// （内部调 <c>InvalidateNamespaceCache()</c> —— 契约在 ReplSession s_referencePaths 声明处双登记）。
        /// §4.5：到顶**不得静默截断** —— 打"已补 N 个引用仍未通过 + 剩余诊断"。
        /// </summary>
        public static CompileResult CompileWithHealing(string assemblyName, string source,
            List<string> referencePaths, bool includePolyfills, ReferenceHealer healer, out string healLog,
            ReplSession session = null)
        {
            healLog = "";
            // 引用集只有 21+ 个，首次编译常需连补 1~3 个（Unity 模块/其它 System 件/模组），故给 5 轮。
            int healed = 0;
            CompileResult r = Compile(assemblyName, source, referencePaths, includePolyfills);
            for (int round = 0; round < 5 && !r.Success; round++)
            {
                string add = healer == null ? null : healer.Resolve(r.Diagnostics, referencePaths);
                if (add == null) break;                  // 解析不出来：原样返回诊断（原因已响亮）
                if (ContainsPath(referencePaths, add)) break;   // 防御：已在引用集里（补了也没用）
                string diagBefore = r.Diagnostics;
                referencePaths.Add(add);
                if (session != null) session.OnReferenceAdded();
                r = Compile(assemblyName, source, referencePaths, includePolyfills);
                // 无效自愈回滚（与 Submit 同判据，微审①）：诊断文本完全未变 ⇒ 该引用无关，撤回。
                if (!r.Success && string.Equals(r.Diagnostics, diagBefore, StringComparison.Ordinal))
                {
                    referencePaths.RemoveAt(referencePaths.Count - 1);
                    string back = "自愈回滚：" + Path.GetFileName(add)
                        + " 加入后诊断无任何变化（判为无关引用，已撤回）";
                    healLog += back + Environment.NewLine;
                    if (healer != null)
                    {
                        Action<string> bsink = healer.ReportSink;
                        if (bsink != null) bsink(back);
                    }
                    break;
                }
                healed++;
                healLog += "自愈补引用：" + Path.GetFileName(add) + Environment.NewLine;
            }
            if (!r.Success && healed >= 5)
            {
                string cap = "引用自愈到顶：已补 " + healed + " 个引用仍未通过（上限 5 轮），剩余诊断："
                    + Environment.NewLine + r.Diagnostics;
                healLog += cap + Environment.NewLine;
                r.Diagnostics = r.Diagnostics + Environment.NewLine + cap;
                if (healer != null)
                {
                    Action<string> sink = healer.ReportSink;
                    if (sink != null) sink(cap);
                }
            }
            if (session != null) session.AppendHealLog(healLog);   // 与 Submit 共用 LastHealLog 观测点
            return r;
        }

        static bool ContainsPath(List<string> refs, string path)
        {
            if (refs == null || string.IsNullOrEmpty(path)) return false;
            foreach (string p in refs)
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}

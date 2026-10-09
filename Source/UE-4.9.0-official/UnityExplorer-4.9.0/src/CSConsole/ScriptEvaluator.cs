using HarmonyLib;
using Mono.CSharp;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityExplorer.Config;

// Thanks to ManlyMarco for this

namespace UnityExplorer.CSConsole
{
    public class ScriptEvaluator : Evaluator, IScriptEvaluator
    {
        internal TextWriter _textWriter;
        // C4（合并修复轮）：static 字段，HookInstance 构造第二个 ScriptEvaluator 时其 BuildContext
        // 会覆盖本字段（mcs 报告打印器本质是进程级单例）。任务 8 重构已消除跨实例串包路径
        // （ConsoleController.Evaluate 不再直读 _reportPrinter，现仅本类 Evaluate 与 HookInstance 读）；
        // 新增直读点前须复核此约束。
        internal static StreamReportPrinter _reportPrinter;

        /// <summary>
        /// 本实例**自己**的打印器（构造时捕获，见 R1）：`_reportPrinter` 是静态字段，`HookInstance`
        /// 造第二个 ScriptEvaluator 时会把它指向别的实例 ⇒ 用静态字段读 ErrorsCount / 取文本会串包。
        /// </summary>
        internal StreamReportPrinter _ownReportPrinter;

        private static readonly HashSet<string> StdLib = new(StringComparer.InvariantCultureIgnoreCase)
        {
            "mscorlib",
            "System.Core",
            "System",
            "System.Xml"
        };

        public ScriptEvaluator(TextWriter tw) : base(BuildContext(tw))
        {
            _textWriter = tw;
            // base(BuildContext(tw)) 刚把静态 _reportPrinter 设成本实例的打印器 ⇒ 此刻捕获即为己有。
            _ownReportPrinter = _reportPrinter;

            ImportAppdomainAssemblies();
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            _textWriter.Dispose();
        }

        public bool IsAvailable => true;
        public string FailureReason => null;

        public bool Evaluate(string input, out string output)
        {
            output = null;
            // ── B3 根因（诊断不跨提交累积）+ R1 勘误（失败判定口径）──
            // 事实链（复审反编译 `lib/net35/mcs.dll` 实证，本轮据此勘误）：
            //   `Mono.CSharp.Evaluator.Compile(string, out CompiledMethod)` **开头即调
            //    `ctx.Report.Printer.Reset()`**（唯一调用者），而 `ReportPrinter.Reset()` 非虚、把
            //    `ErrorsCount = 0`；`CompilerContext..ctor` 把本类的 `StreamReportPrinter` 接为
            //    `ctx.Report.Printer` ⇒ **ErrorsCount 每次编译前被清零 = 本次错误数，不是粘性计数器**。
            //   （§17.3 曾断言"粘性计数器"——**该论断作废**，见报告 §18.1。）按旧论断写的
            //   `errorsBefore` 快照实为**上一条**的错误数：本条错误数 ≤ 上条时会被判成成功 = 静默假成功。
            // 故：失败判定 = `ErrorsCount > 0`（Reset 已保证它是本次的）；文本长度增量**只**用于
            // 取"本次新增的诊断文本"（`_textWriter` 本身仍是从上次 ResetConsole 起累积的）。
            var printer = _ownReportPrinter ?? _reportPrinter;
            string textBefore = _textWriter.ToString();

            CompiledMethod repl = null;
            try { repl = Compile(input); }
            catch (Exception ex) { output = ex.Message; return false; }

            if (repl != null)
            {
                object ret = null;
                try { repl.Invoke(ref ret); }
                catch (Exception ex) { output = ex.Message; return false; }
                output = ret?.ToString();
                return true;
            }

            bool failedThisTime = printer != null && printer.ErrorsCount > 0;
            if (failedThisTime)
            {
                string textNow = _textWriter.ToString();
                output = textNow.Length > textBefore.Length
                    ? textNow.Substring(textBefore.Length)
                    : textNow;   // 兜底：报告器未写字（罕见）时退回全量，绝不返回 null 让上层拿不到原因
                return false;
            }

            output = null;
            return true;
        }

        // 以下三个成员与基类 Mono.CSharp.Evaluator 的同名同签名方法构成隐藏：
        // 不加 new 会报 CS0108（破坏 0 警告），故显式声明隐藏意图（对简报的偏离，见报告）。
        public new string[] GetCompletions(string input, out string prefix)
        {
            try { return base.GetCompletions(input, out prefix); }
            catch { prefix = ""; return new string[0]; }
        }

        public new string GetUsing() => base.GetUsing();
        public new string GetVars() => base.GetVars();
        public void AddUsing(string ns) => Run("using " + ns + ";");

        // 原来在 ScriptInteraction.GetClasses 里的 mcs 内部反射，集中搬到这里（接口要求两边都能给类清单）
        static readonly FieldInfo fi_sourceFile = AccessTools.Field(typeof(Evaluator), "source_file");

        public string GetClasses()
        {
            if (fi_sourceFile.GetValue(this) is CompilationSourceFile sourceFile && sourceFile.Containers.Any())
            {
                var sb = new StringBuilder();
                sb.Append($"There are {sourceFile.Containers.Count} defined classes:");
                foreach (TypeDefinition type in sourceFile.Containers.Where(it => it is TypeDefinition))
                {
                    sb.Append($"\n\n{type.MemberName.Name}:");
                    foreach (MemberCore member in type.Members)
                        sb.Append($"\n\t- {member.AttributeTargets}: \"{member.MemberName.Name}\" ({member.ModFlags})");
                }
                return sb.ToString();
            }
            return null;
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            // B4（合并修复轮）：name 声明提升到 try 外——catch 内复用 try 段已捕获的变量，
            // 不再二次调用 args.LoadedAssembly.GetName()（原 catch 里的第二次 GetName 理论可再抛，
            // 异常逃出 AssemblyLoad 事件处理器 = 控制台初始化崩）。改法依据：声明提升后 catch 体内
            // 再无可抛调用（仅 LogWarning + 字符串插值），比"catch 体再套一层 try"少一层嵌套且
            // 彻底消除失败模式，而非把它埋深一层。
            string name = null;
            try
            {
                name = args.LoadedAssembly.GetName().Name;
                if (StdLib.Contains(name)) return;
                Reference(args.LoadedAssembly);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"[CSConsole] 引用新加载程序集失败（已忽略）：{name ?? "<unknown>"} - {ex.GetType().Name}");
            }
        }

        private void Reference(Assembly asm)
        {
            string name = asm.GetName().Name;

            if (name == "completions") // ignore assemblies generated by mcs' autocomplete.
                return;

            foreach (string blacklisted in ConfigManager.CSConsole_Assembly_Blacklist.Value.Split(';'))
            {
                string bl = blacklisted;
                if (bl.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    bl = blacklisted.Substring(0, bl.Length - 4);
                if (string.Equals(bl, name, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            ReferenceAssembly(asm);
        }

        private static CompilerContext BuildContext(TextWriter tw)
        {
            _reportPrinter = new StreamReportPrinter(tw);

            CompilerSettings settings = new()
            {
                Version = LanguageVersion.Experimental,
                GenerateDebugInfo = false,
                StdLib = true,
                Target = Target.Library,
                WarningLevel = 0,
                EnhancedWarnings = false
            };

            return new CompilerContext(settings, _reportPrinter);
        }

        private void ImportAppdomainAssemblies()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (StdLib.Contains(name))
                    continue;

                try
                {
                    Reference(assembly);
                }
                catch // (Exception ex)
                {
                    //ExplorerCore.LogWarning($"Excepting referencing '{name}': {ex.GetType()}.{ex.Message}");
                }
            }
        }
    }
}

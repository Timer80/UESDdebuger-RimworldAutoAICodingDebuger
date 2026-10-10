using HarmonyLib;
using Mono.CSharp;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityExplorer.Config;
using UnityExplorer.RoslynBridge;

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
        // 任务 10 起：交付配置 STANDALONE_Mono 的动态补丁改由 Roslyn 编译，HookInstance **不再**造第二个
        // ScriptEvaluator（也不再读本字段）；下面这条约束在本类自身 Evaluate 与其余 8 个配置上照旧成立。
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

        // ── 任务 15b：最小集模式（opt-in）+ 已加载件注册表 + 自愈驱动 ───────────────
        // 为什么要 opt-in 而**不能**全局改：本类被 9 个配置编译，且旧调用方（`HookInstance` 自建的那个
        // ScriptEvaluator，以及其余非控制台调用点）一直依赖"新加载程序集自动进引用集 + 启动全扫"的
        // 旧行为——全局改会让它们的可见类型集合**突然收窄**（原本能写的类型变成 CS0246），
        // 而它们的失败面（动态补丁/钩子）比控制台更贵。故最小集只由**控制台路径**显式开启
        //（`ConsoleController.ResetConsoleCore` ⇒ `new ScriptEvaluator(tw, minimalReferences: true)`）。
        // 关闭时下面每个分支都逐字走原路径：`ImportAppdomainAssemblies()` 全扫 + `OnAssemblyLoad` 自动引用。
        private readonly bool _minimalReferences;
        /// <summary>最小集模式下"只登记不引用"的已加载程序集表（自愈的零副作用通道）。</summary>
        private readonly LoadedAssemblyRegistry _registry;
        /// <summary>最小集模式下的自愈驱动；旧模式为 null ⇒ 失败路径逐字不变（不做重试）。</summary>
        private readonly McsReferenceHealer _healer;
        /// <summary>本实例订阅的那个 AssemblyLoad 处理器（旧模式=自动引用；最小集模式=只登记）。
        /// 类型是 net35 的 <see cref="AssemblyLoadEventHandler"/>（不是 `EventHandler&lt;AssemblyLoadEventArgs&gt;`）。</summary>
        private readonly AssemblyLoadEventHandler _loadHandler;
        /// <summary>候选目录缓存（注册表/模组路径变化即失效；控制台是单线程调用，无需锁）。</summary>
        private List<string> _candidateCache;
        private Func<IList<string>> _modPathProvider;
        private bool _allowModTypeResolution = true;
        private readonly List<string> _excludedModDirs = new List<string>();
        /// <summary>本轮求值的自愈日志缓冲（并入 <c>output</c>，见 Evaluate：mcs 路径日志不进 Player.log）。</summary>
        private readonly StringBuilder _healLog = new StringBuilder();

        /// <summary>旧构造（逐字保持旧行为）：启动全扫引用 + 新加载程序集自动引用。</summary>
        public ScriptEvaluator(TextWriter tw) : this(tw, false) { }

        /// <summary>
        /// 任务 15b：<paramref name="minimalReferences"/> = true 走"**最小集 + 注册表**"：
        /// ① 启动只引用 <see cref="ReferenceProfile.SmallProfile"/>（22 件，按它的口径）+ 宿主自引用
        ///    （`Assembly.GetExecutingAssembly()`；提交类要继承宿主类型，缺它每条 CS0246）；
        /// ② `AssemblyLoad` **保留订阅但只登记不引用** ⇒ 自愈能零 `LoadFrom` 拿到已加载件；
        /// ③ 引用增长只经**诊断驱动自愈**（Evaluate 的失败路径，≤5 轮）⇒ 引用集有上界、有原因、有日志。
        /// </summary>
        public ScriptEvaluator(TextWriter tw, bool minimalReferences) : base(BuildContext(tw))
        {
            _textWriter = tw;
            // base(BuildContext(tw)) 刚把静态 _reportPrinter 设成本实例的打印器 ⇒ 此刻捕获即为己有。
            _ownReportPrinter = _reportPrinter;

            _minimalReferences = minimalReferences;
            _loadHandler = minimalReferences
                ? new AssemblyLoadEventHandler(OnAssemblyLoadRegisterOnly)
                : new AssemblyLoadEventHandler(OnAssemblyLoad);

            if (minimalReferences)
            {
                _registry = new LoadedAssemblyRegistry();
                _healer = new McsReferenceHealer(_registry)
                {
                    PathLoader = Assembly.LoadFrom,      // 已在进程里的先命中注册表，故这里只兜"未加载的最小集/候选件"
                    AddReference = Reference,            // 方法组注入（C.5：无闭包）
                    CandidatePathProvider = HealingCandidates,
                    IsRefused = IsBlacklisted,
                    IsExcludedPath = IsModExcludedPath,
                    ReportSink = AppendHealLine,
                };
                // 登记**先于**引用：最小集里"已在进程中的件"直接复用，不再 LoadFrom 一份（避免双副本/双身份）。
                RegisterLoadedAssemblies();
                ImportMinimalProfile();
            }
            else
            {
                ImportAppdomainAssemblies();
            }

            AppDomain.CurrentDomain.AssemblyLoad += _loadHandler;
        }

        /// <summary>最小集模式是否开启（供控制台自检/报告；旧调用方恒 false）。</summary>
        public bool MinimalReferences => _minimalReferences;

        /// <summary>自愈驱动（仅最小集模式非 null）——接线方能在此注入模组路径/观测日志。</summary>
        public McsReferenceHealer Healer => _healer;

        /// <summary>模组程序集路径提供者（开关 ON 时并入候选目录；OFF 时这些路径进**排除集**）。</summary>
        public Func<IList<string>> ModPathProvider
        {
            get => _modPathProvider;
            set { _modPathProvider = value; _candidateCache = null; RebuildExcludedModDirs(); }
        }

        /// <summary>配置 `C# Console Allow Mod Type Resolution`（默认 true = 与 Roslyn 侧同默认）。</summary>
        public bool AllowModTypeResolution
        {
            get => _allowModTypeResolution;
            set { _allowModTypeResolution = value; _candidateCache = null; RebuildExcludedModDirs(); }
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= _loadHandler;
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
            _healLog.Length = 0;   // 自愈日志按"本次求值"计

            CompiledMethod repl = null;
            try { repl = Compile(input); }
            catch (Exception ex) { output = ex.Message; return false; }

            // ── 任务 15b：失败 ⇒ 诊断驱动的引用自愈重试（≤5 轮）──
            // 只在**最小集模式**（`_healer != null`）跑：旧调用方的失败路径逐字不变（不重试、不加引用）。
            // 判据与下面失败判定同源（ErrorsCount > 0 即本次失败），不与"编译成功"路径争抢。
            int healed = 0;
            string lastBefore = textBefore;
            if (repl == null && _healer != null && printer != null && printer.ErrorsCount > 0)
            {
                for (int round = 0; round < McsReferenceHealer.MaxHealRounds; round++)
                {
                    string added = _healer.TryHealOne(SliceNewDiagnostics(lastBefore));
                    // null = 无高置信候选（按名无匹配 / 身份不符 / 黑名单 / 排除集）：原因已在 _healLog，停止重试。
                    if (added == null) break;
                    healed++;
                    AppendHealLine(McsHealingPolicy.HealLogLine(added));
                    lastBefore = _textWriter.ToString();
                    try { repl = Compile(input); }
                    catch (Exception ex)
                    {
                        AppendHealLine("自愈重试编译抛异常：" + ex.GetType().Name + "：" + ex.Message);
                        break;
                    }
                    if (repl != null) break;
                }
            }

            if (repl != null)
            {
                object ret = null;
                try { repl.Invoke(ref ret); }
                catch (Exception ex) { output = ex.Message; return false; }
                output = ret?.ToString();
                // 自愈日志的落点 = **求值器 output**（UE 控制台结果/错误面板看见），不进 Player.log（口径 v1.1）。
                if (_healLog.Length > 0)
                    output = (output ?? "") + Environment.NewLine + _healLog;
                return true;
            }

            bool failedThisTime = printer != null && printer.ErrorsCount > 0;
            if (failedThisTime)
            {
                output = SliceNewDiagnostics(lastBefore);
                // §4.5 同款纪律：到顶**禁静默截断**——追加"已补 N 个引用仍未通过 + 剩余诊断"（文案与 Roslyn 侧逐字同款）。
                output = McsReferenceHealer.AppendCapIfNeeded(healed, output);
                if (_healLog.Length > 0)
                    output = output + Environment.NewLine + _healLog;
                return false;
            }

            output = null;
            return true;
        }

        /// <summary>本次新增的诊断文本（`_textWriter` 自上次 ResetConsole 起累积，故只取增量）。</summary>
        private string SliceNewDiagnostics(string textBefore)
        {
            string textNow = _textWriter.ToString();
            // 兜底：报告器未写字（罕见）时退回全量，绝不返回 null 让上层拿不到原因
            return textNow.Length > textBefore.Length ? textNow.Substring(textBefore.Length) : textNow;
        }

        /// <summary>自愈日志行缓冲（换行由本方法统一插入，调用方不必自己拼）。</summary>
        private void AppendHealLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (_healLog.Length > 0) _healLog.Append(Environment.NewLine);
            _healLog.Append(line);
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

        /// <summary>黑名单 + mcs 自动补全件（`completions`）判据：命中即**不加引用**。</summary>
        private static bool IsBlacklisted(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name == "completions") return true;

            foreach (string blacklisted in ConfigManager.CSConsole_Assembly_Blacklist.Value.Split(';'))
            {
                string bl = blacklisted;
                if (bl.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    bl = bl.Substring(0, bl.Length - 4);
                if (string.Equals(bl, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>加入引用集（返回 true = 真的加进去了；供自愈断言"加没加成功"）。</summary>
        private bool Reference(Assembly asm)
        {
            string name = asm.GetName().Name;

            if (name == "completions") // ignore assemblies generated by mcs' autocomplete.
                return false;

            if (IsBlacklisted(name))
                return false;

            ReferenceAssembly(asm);
            return true;
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

        // ═══════════════════════════════════════════════════════════════════════════
        // 任务 15b：最小集 + 注册表 + 自愈（仅 _minimalReferences=true 时启用）
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>最小集模式下的 AssemblyLoad：**只登记不引用**（引用增长只经诊断驱动自愈）。</summary>
        private void OnAssemblyLoadRegisterOnly(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                // 新件只进注册表（供自愈零 LoadFrom 取用）；候选目录缓存随新 Location 失效。
                if (_registry.Register(args.LoadedAssembly)) _candidateCache = null;
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning("[CSConsole] 最小集模式登记新加载程序集失败（已忽略）：" + ex.GetType().Name);
            }
        }

        /// <summary>把当前进程里**已经加载**的程序集登记进注册表（不引用任何一件）。</summary>
        private void RegisterLoadedAssemblies()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { _registry.Register(assembly); }
                catch { /* 无名字/无 Location 的动态件：跳过 */ }
            }
        }

        /// <summary>
        /// 最小集 = <see cref="ReferenceProfile.SmallProfile"/>（22 件，口径以它为准：本体 Managed 21 + UE 侧 1）
        /// + **宿主自引用**（`Assembly.GetExecutingAssembly()`）。已在进程里的先命中注册表用现成的，
        /// 只有"没加载过且盘上有文件"的才 `Assembly.LoadFrom`（避免同简单名出现第二份副本/第二种身份）。
        /// 缺件**响亮**记日志（最小集缺件 ⇒ 对应命名空间整段不可用；静默会让用户"能写却不报错"）。
        /// </summary>
        private void ImportMinimalProfile()
        {
            string managed = ManagedDirOfThisProcess();
            string ueDir = UeAssemblyDirOfThisProcess();
            var missing = new List<string>();

            foreach (string simple in ReferenceProfile.SmallProfile)
            {
                string dir = ReferenceProfile.UeSideProfile.Contains(simple) ? ueDir : managed;
                string path = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, simple + ".dll");
                if (!ImportProfileEntry(simple, path)) missing.Add(simple);
            }

            // 宿主自引用：提交类继承宿主类型（`ReplHostBase` 等）缺它每条 CS0246（与 Roslyn 侧 self= 口径同源）
            try
            {
                Assembly self = Assembly.GetExecutingAssembly();
                if (!Reference(self) && !IsBlacklisted(self.GetName().Name)) missing.Add("<self>");
            }
            catch { missing.Add("<self>"); }

            if (missing.Count > 0)
            {
                ExplorerCore.LogWarning("[CSConsole] mcs 最小集缺件 " + missing.Count + "/"
                    + (ReferenceProfile.ExpectedProfileCount + 1) + "：" + string.Join(",", missing.ToArray())
                    + "（Managed=" + (managed ?? "<不可判定>") + "，UE=" + (ueDir ?? "<不可判定>")
                    + "）→ 这些件对应的类型提交时会 CS0246，将由诊断驱动的引用自愈按需补（若盘上确有该 dll）。");
            }
        }

        private bool ImportProfileEntry(string simple, string path)
        {
            Assembly existing = _registry.FindBySimpleName(simple);
            if (existing != null) return Reference(existing);      // 已在进程里 ⇒ 用现成的，不再 LoadFrom 一份
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try { return Reference(Assembly.LoadFrom(path)); }
            catch { return false; }
        }

        /// <summary>本体 Managed 目录：取运行时 mscorlib/CoreLib 所在目录（不依赖 UnityEngine/Application）。</summary>
        private static string ManagedDirOfThisProcess()
        {
            try
            {
                string loc = typeof(object).Assembly.Location;
                return string.IsNullOrEmpty(loc) ? null : Path.GetDirectoryName(loc);
            }
            catch { return null; }
        }

        /// <summary>UE 侧程序集目录：本 UE 程序集自己所在目录（换副本/重定向跟得住）。</summary>
        private static string UeAssemblyDirOfThisProcess()
        {
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(loc)) return null;
                string dir = Path.GetDirectoryName(loc);
                return string.IsNullOrEmpty(dir) || !Directory.Exists(dir) ? null : dir;
            }
            catch { return null; }
        }

        /// <summary>
        /// 自愈的候选 dll 文件：固定目录（本体 Managed + UE 程序集目录）+ **已加载件所在目录**
        /// （注册表登记，实测覆盖 workshop 模组目录）+ 模组路径提供者给出的目录（开关 ON 才并入）。
        /// 结果缓存；注册表变动、模组路径或开关变化即失效。开关 OFF 时模组件由**排除集**在自愈里挡下。
        /// </summary>
        private IList<string> HealingCandidates()
        {
            if (_candidateCache != null) return _candidateCache;

            var dirs = new List<string>();
            AddDir(dirs, ManagedDirOfThisProcess());
            AddDir(dirs, UeAssemblyDirOfThisProcess());
            foreach (string d in _registry.KnownDirectories) AddDir(dirs, d);
            Func<IList<string>> provider = _modPathProvider;
            if (provider != null)
            {
                IList<string> mods;
                try { mods = provider(); }
                catch (Exception ex)
                {
                    ExplorerCore.LogWarning("[CSConsole] 模组程序集路径提供者抛 " + ex.GetType().Name
                        + "：已忽略（模组件不进候选）。");
                    mods = null;
                }
                if (mods != null)
                    foreach (string p in mods) AddDir(dirs, SafeGetDirectoryName(p));
            }

            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in dirs)
            {
                string[] found;
                try { found = Directory.GetFiles(dir, "*.dll"); }
                catch { continue; }
                foreach (string f in found)
                    if (seen.Add(f)) files.Add(f);
            }

            _candidateCache = files;
            return files;
        }

        private static string SafeGetDirectoryName(string path)
        {
            try { return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path); }
            catch { return null; }
        }

        private static void AddDir(List<string> dirs, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (!dirs.Contains(dir)) dirs.Add(dir);
        }

        /// <summary>开关 `C# Console Allow Mod Type Resolution` OFF ⇒ 模组目录命中排除集（**含已加载件通道**）。</summary>
        private bool IsModExcludedPath(string path)
        {
            if (_allowModTypeResolution || string.IsNullOrEmpty(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { full = path; }
            foreach (string dir in _excludedModDirs)
                if (full.Length > dir.Length && full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void RebuildExcludedModDirs()
        {
            _excludedModDirs.Clear();
            if (_allowModTypeResolution || _modPathProvider == null) return;

            IList<string> mods;
            try { mods = _modPathProvider(); }
            catch { return; }
            if (mods == null) return;

            foreach (string p in mods)
            {
                string dir = SafeGetDirectoryName(p);
                if (string.IsNullOrEmpty(dir)) continue;
                try { dir = Path.GetFullPath(dir); }
                catch { /* 非规范路径：按原样用 */ }
                if (!dir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    dir += Path.DirectorySeparatorChar;   // 带分隔符，避免 "…\Mods\A" 前缀误吞 "…\Mods\AB"
                if (!_excludedModDirs.Contains(dir)) _excludedModDirs.Add(dir);
            }
        }
    }
}

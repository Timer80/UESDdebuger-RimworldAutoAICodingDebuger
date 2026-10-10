using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 任务 15b：**已加载程序集注册表**——`AppDomain.CurrentDomain.AssemblyLoad` 在最小集模式下
    /// "只登记不引用"的落点。
    /// 为什么需要它：最小集模式**不再**把新加载的程序集自动塞进 mcs 引用集（那正是"引用集无上界"的病根），
    /// 但自愈仍要能**零副作用**地拿到"诊断要求的名字已经在进程里"这一事实 ⇒
    /// 命中注册表 ⇒ 直接 <c>ReferenceAssembly</c>（不 <c>LoadFrom</c>，不产生第二份副本、不触发二次加载）。
    /// 纯逻辑（只碰 <see cref="System.Reflection"/>），无 UE/Unity/Roslyn 依赖 ⇒ 可离线单测。
    /// </summary>
    public sealed class LoadedAssemblyRegistry
    {
        readonly Dictionary<string, Assembly> bySimpleName
            = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> dirs = new List<string>();

        /// <summary>已登记的**不同简单名**个数（同简单名先到者胜，重复不计）。</summary>
        public int Count => bySimpleName.Count;

        /// <summary>登记过程中见到的程序集所在目录（去重，按发现顺序）——候选扫描的"实际加载位置"来源。</summary>
        public IList<string> KnownDirectories => dirs;

        /// <summary>
        /// 登记一个已加载程序集（**不引用**）。返回 true = 本次新登记。
        /// `completions`（mcs 自动补全生成的动态件）与无简单名的件不予登记——与
        /// <c>ScriptEvaluator.Reference</c> 的既有判据同源。
        /// 同名不同身份：**先到者胜**（不抛、不覆盖）；mcs 引用一旦加入不可撤回，
        /// 覆盖等于换一份身份，静默换掉比拒绝更危险。
        /// </summary>
        public bool Register(Assembly asm)
        {
            if (asm == null) return false;
            string name;
            try { name = asm.GetName().Name; }
            catch { return false; }
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "completions") return false;
            if (bySimpleName.ContainsKey(name)) return false;

            bySimpleName.Add(name, asm);
            try
            {
                string loc = asm.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir) && !dirs.Contains(dir)) dirs.Add(dir);
                }
            }
            catch { /* 动态程序集无 Location：只登记名字，不登记目录 */ }
            return true;
        }

        /// <summary>按**简单名**查找（忽略大小写）；未登记 ⇒ null。</summary>
        public Assembly FindBySimpleName(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            Assembly a;
            return bySimpleName.TryGetValue(simpleName, out a) ? a : null;
        }
    }

    /// <summary>
    /// 任务 15b：mcs 侧"**一轮**自愈解析"（诊断 → 该加入哪个引用，或响亮说不加入）。
    /// 轮次上限/到顶文案由调用方（<c>ScriptEvaluator.Evaluate</c>）按 <see cref="MaxHealRounds"/> 控制，
    /// 本类只负责一轮，且**只收高置信候选**（mcs 无 <c>RemoveReference</c>，加了撤不回）：
    /// ① 注册表命中（已加载件）⇒ 直接引用，**零 <c>LoadFrom</c>**；
    /// ② 否则按名（<see cref="McsHealingPolicy.PickCandidateByName"/>，首段 vs 文件名 stem）挑文件；
    ///    诊断带完整身份时**必须** <see cref="AssemblyName.GetAssemblyName"/> 校验身份相符，不符即拒绝；
    /// ③ 都不成立 ⇒ 经 <see cref="ReportSink"/> **响亮**说明原因，返回 null（不加入）。
    /// 依赖全部为**注入**（加载器 / 引用动作 / 候选提供者 / 黑名单 / 排除集）⇒ 离线可断言"走了哪条分支、加载了几次"。
    /// 纯逻辑：无 UE/Unity/Roslyn 依赖，net35 安全。
    /// </summary>
    public sealed class McsReferenceHealer
    {
        /// <summary>与 Roslyn 侧（<c>RoslynCompiler.CompileWithHealing</c>）同值的轮次上限。</summary>
        public const int MaxHealRounds = 5;

        /// <summary>
        /// 到顶判定。**生产与离线断言共用同一函数**：`ScriptEvaluator.Evaluate` 的失败路径据此决定
        /// 要不要追加到顶文案（判据 = "已补满 5 轮仍未通过"，与 Roslyn 侧 `healed >= 5` 逐字同源）。
        /// </summary>
        public static bool ReachedCap(int healed) => healed >= MaxHealRounds;

        /// <summary>
        /// 到顶时把 <see cref="McsHealingPolicy.CapMessage"/> **追加**到诊断串（未到顶则原样返回）。
        /// §4.5 纪律：到顶**禁静默截断**——剩余诊断必须留在文本里（本函数保证诊断既在前面、也在文案内部）。
        /// </summary>
        public static string AppendCapIfNeeded(int healed, string diagnostics)
        {
            if (!ReachedCap(healed)) return diagnostics;
            return diagnostics + Environment.NewLine + McsHealingPolicy.CapMessage(healed, diagnostics);
        }

        readonly LoadedAssemblyRegistry registry;

        public McsReferenceHealer(LoadedAssemblyRegistry registry)
        {
            this.registry = registry;
        }

        /// <summary>文件 → 程序集（生产 = <c>Assembly.LoadFrom</c>；测试注入计数假件，用来断言"①分支 0 次加载"）。</summary>
        public Func<string, Assembly> PathLoader { get; set; }

        /// <summary>真正把程序集加进 mcs 引用集（生产 = <c>ScriptEvaluator.Reference</c>，含黑名单判定）。</summary>
        public Func<Assembly, bool> AddReference { get; set; }

        /// <summary>候选 dll 文件（生产 = 宿主扫目录；测试注入内存列表）。</summary>
        public Func<IList<string>> CandidatePathProvider { get; set; }

        /// <summary>黑名单判定（生产 = <c>ConfigManager.CSConsole_Assembly_Blacklist</c>）。</summary>
        public Func<string, bool> IsRefused { get; set; }

        /// <summary>排除集判定（生产 = 开关 <c>C# Console Allow Mod Type Resolution</c> OFF 时的模组路径集合）。</summary>
        public Func<string, bool> IsExcludedPath { get; set; }

        /// <summary>日志出口（生产 = 求值器 <c>output</c> 通道；测试 = 收集器）。</summary>
        public Action<string> ReportSink { get; set; }

        /// <summary>本实例累计 <c>PathLoader</c> 调用次数——断言"已加载优先 ⇒ 不调用 LoadFrom"的观测点。</summary>
        public int LoadCallCount { get; private set; }

        /// <summary>本实例累计"命中注册表（已加载件通道）"次数。</summary>
        public int LoadedRegistryHitCount { get; private set; }

        /// <summary>上一轮加入的路径（已加载件通道时为该程序集 Location，取不到则退回简单名）。</summary>
        public string LastAdded { get; private set; }

        /// <summary>上一轮是否走了①（已加载件）分支。</summary>
        public bool LastFromLoadedRegistry { get; private set; }

        /// <summary>最后一条响亮说明（成功也写，便于观测"为什么加/为什么不加"）。</summary>
        public string LastReport { get; private set; }

        /// <summary>
        /// 一轮自愈：返回**应加入的引用标识**（文件路径 / 已加载件 Location）或 null（= 不加入，原因已响亮）。
        /// 调用方拿到非 null 后自行重编；拿到 null 必须停止重试（再试也是同一结论）。
        /// </summary>
        public string TryHealOne(string diagnostics)
        {
            LastAdded = null;
            LastFromLoadedRegistry = false;

            string demanded = McsHealingPolicy.DemandedName(diagnostics);
            if (string.IsNullOrEmpty(demanded))
            {
                Report("自愈放弃：该诊断里读不出『被要求的名字』（CS0012/CS1069/CS0234/CS0246/CS0103 都没有）⇒ 不折腾。");
                return null;
            }
            string head = HeadOf(demanded);

            // ── ① 已加载件优先：注册表命中 ⇒ 直接引用（不 LoadFrom，零副作用）──
            string excludedNote = null;
            Assembly loaded = registry == null ? null : registry.FindBySimpleName(head);
            if (loaded != null)
            {
                string loc = SafeLocation(loaded);
                if (IsExcluded(loc))
                {
                    // 开关 OFF：模组程序集不进引用集（**含本通道**，与 Roslyn 侧 ReferenceHealer 口径一致）
                    excludedNote = "（『" + head + "』是已加载的模组件，开关 C# Console Allow Mod Type Resolution=OFF ⇒ 排除集命中）";
                }
                else if (Refused(head))
                {
                    Report("自愈拒绝：『" + head + "』命中引用黑名单 ⇒ 未加入（mcs 引用不可撤回）。");
                    return null;
                }
                else if (Add(loaded))
                {
                    LoadedRegistryHitCount++;
                    LastFromLoadedRegistry = true;
                    LastAdded = loc ?? head;
                    Report("自愈命中已加载件：" + head + "（直接 ReferenceAssembly，未 LoadFrom、零副作用）。");
                    return LastAdded;
                }
                else
                {
                    Report("自愈失败：『" + head + "』已在进程内但 ReferenceAssembly 未生效（引用集拒绝）⇒ 未加入。");
                    return null;
                }
            }

            // ── ② 文件候选：按名优先（首段 vs stem），排除集过滤 ──
            IList<string> pool = CandidatePathProvider == null ? null : CandidatePathProvider();
            string cand = null;
            int seen = 0, excluded = 0;
            if (pool != null)
            {
                var usable = new List<string>();
                foreach (string p in pool)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    seen++;
                    if (IsExcluded(p)) { excluded++; continue; }
                    usable.Add(p);
                }
                cand = McsHealingPolicy.PickCandidateByName(demanded, usable);
            }

            if (cand == null)
            {
                Report("自愈放弃：『" + demanded + "』（首段 " + head + "）按名无匹配 ⇒ 未加入 —— "
                    + "候选 " + seen + " 个、被排除集挡下 " + excluded + " 个；"
                    + "mcs 引用**加入后不可撤回**，故宁可不加也不猜。" + (excludedNote ?? ""));
                return null;
            }

            // ── 身份加严（仅当诊断给出完整身份）：同名不同身份 ⇒ 不加入并响亮记日志 ──
            string simple, version, culture, pkt;
            if (MissingReferenceParser.TryAssemblyIdentity(diagnostics, out simple, out version, out culture, out pkt))
            {
                string mismatch = IdentityMismatch(cand, version, culture, pkt);
                if (mismatch != null)
                {
                    Report("自愈拒绝：" + Path.GetFileName(cand) + " 与诊断要求的身份不符（" + mismatch
                        + "）⇒ 未加入（mcs 引用不可撤回，同名不同身份加了会污染整段会话）。");
                    return null;
                }
            }

            if (Refused(head))
            {
                Report("自愈拒绝：『" + head + "』命中引用黑名单 ⇒ 未加入（mcs 引用不可撤回）。");
                return null;
            }
            if (PathLoader == null)
            {
                Report("自愈放弃：无加载器（PathLoader 未注入）⇒ 未加入 " + Path.GetFileName(cand) + "。");
                return null;
            }

            Assembly asm;
            LoadCallCount++;
            try { asm = PathLoader(cand); }
            catch (Exception ex)
            {
                Report("自愈失败：Assembly.LoadFrom(" + cand + ") 抛 " + ex.GetType().Name + "：" + ex.Message + " ⇒ 未加入。");
                return null;
            }
            if (asm == null)
            {
                Report("自愈失败：Assembly.LoadFrom(" + cand + ") 返回 null ⇒ 未加入。");
                return null;
            }
            if (!Add(asm))
            {
                Report("自愈失败：" + Path.GetFileName(cand) + " 已加载但 ReferenceAssembly 未生效（引用集拒绝）⇒ 未加入。");
                return null;
            }

            LastAdded = cand;
            Report("自愈补引用（文件通道，已按名与身份校验）：" + Path.GetFileName(cand));
            return cand;
        }

        bool Add(Assembly asm)
        {
            Func<Assembly, bool> add = AddReference;
            if (add == null) return false;
            try { return add(asm); }
            catch (Exception ex)
            {
                Report("自愈失败：ReferenceAssembly 抛 " + ex.GetType().Name + "：" + ex.Message + " ⇒ 未加入。");
                return false;
            }
        }

        bool Refused(string name) => IsRefused != null && IsRefused(name);

        bool IsExcluded(string path) => !string.IsNullOrEmpty(path) && IsExcludedPath != null && IsExcludedPath(path);

        static string SafeLocation(Assembly asm)
        {
            try
            {
                string loc = asm.Location;
                return string.IsNullOrEmpty(loc) ? null : loc;
            }
            catch { return null; }
        }

        void Report(string line)
        {
            LastReport = line;
            Action<string> sink = ReportSink;
            if (sink != null) sink(line);
        }

        /// <summary>组合命名空间/符号的**首段**（`A.B.C` ⇒ `A`），与 PickCandidateByName 的按名口径同源。</summary>
        internal static string HeadOf(string demanded)
        {
            if (string.IsNullOrEmpty(demanded)) return demanded;
            int dot = demanded.IndexOf('.');
            return dot < 0 ? demanded : demanded.Substring(0, dot);
        }

        /// <summary>
        /// 返回"不符处的说明"或 null（相符 / 该维度诊断没给就不比）。
        /// 只比诊断真给了的维度：Version（诊断有才比）、Culture（`neutral` ↔ 空 CultureName）、
        /// PublicKeyToken（`null` ↔ 空 token）。
        /// </summary>
        internal static string IdentityMismatch(string path, string version, string culture, string publicKeyToken)
        {
            AssemblyName an;
            try { an = AssemblyName.GetAssemblyName(path); }
            catch (Exception ex) { return "读不出程序集清单（" + ex.GetType().Name + "）"; }
            if (an == null) return "读不出程序集清单";

            var diffs = new List<string>();
            if (!string.IsNullOrEmpty(version))
            {
                Version want = null;
                try { want = new Version(version); }
                catch { want = null; }
                if (want != null && an.Version != null && an.Version != want)
                    diffs.Add("Version 要求 " + version + "，文件实为 " + an.Version);
            }
            if (!string.IsNullOrEmpty(culture))
            {
                // net35 的 AssemblyName 没有 CultureName（net45+ 才有）⇒ 走 CultureInfo（中性区域性 = 空名字）
                CultureInfo ci = an.CultureInfo;
                string have = ci == null || ci.Name == null ? "" : ci.Name;
                bool wantNeutral = culture.Equals("neutral", StringComparison.OrdinalIgnoreCase);
                if (wantNeutral ? have.Length != 0 : !have.Equals(culture, StringComparison.OrdinalIgnoreCase))
                    diffs.Add("Culture 要求 " + culture + "，文件实为 " + (have.Length == 0 ? "neutral" : have));
            }
            if (!string.IsNullOrEmpty(publicKeyToken))
            {
                byte[] tok = an.GetPublicKeyToken();
                string have = (tok == null || tok.Length == 0) ? "null" : HexOf(tok);
                if (!have.Equals(publicKeyToken.Trim(), StringComparison.OrdinalIgnoreCase))
                    diffs.Add("PublicKeyToken 要求 " + publicKeyToken + "，文件实为 " + have);
            }
            return diffs.Count == 0 ? null : string.Join("；", diffs.ToArray());
        }

        static string HexOf(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}

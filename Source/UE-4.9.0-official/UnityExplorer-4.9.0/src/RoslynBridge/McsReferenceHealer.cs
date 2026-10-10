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
        /// <summary>简单名 → 该简单名下的**全部**已登记件（任务 15d 起保留同简单名的多个身份；
        /// 15b/15c 时是"同简单名先到者胜、只留一份"）。</summary>
        readonly Dictionary<string, List<Assembly>> bySimpleName
            = new Dictionary<string, List<Assembly>>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> dirs = new List<string>();
        /// <summary>任务 15c：命名空间/类型探针的结论缓存（**命中与未命中都缓存**，register 变动即整体清空）。</summary>
        readonly Dictionary<string, Assembly> nsHits = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        /// <summary>任务 15d：同简单名多份时的取舍偏好。mcs 侧 = <see cref="SameStemPreference.Oldest"/>
        /// （人类裁定"没有 Roslyn 只有 mcs 用旧版"：旧编译器配旧库），在构造器中置定。</summary>
        public SameStemPreference Preference { get; set; }

        public LoadedAssemblyRegistry()
        {
            Preference = SameStemPreference.Oldest;
        }

        /// <summary>已登记的**不同简单名**个数（同简单名多个身份只算 1）。</summary>
        public int Count => bySimpleName.Count;

        /// <summary>登记过程中见到的程序集所在目录（去重，按发现顺序）——候选扫描的"实际加载位置"来源。</summary>
        public IList<string> KnownDirectories => dirs;

        /// <summary>
        /// 登记一个已加载程序集（**不引用**）。返回 true = 本次新登记。
        /// `completions`（mcs 自动补全生成的动态件）与无简单名的件不予登记——与
        /// <c>ScriptEvaluator.Reference</c> 的既有判据同源。
        /// 同名不同身份：**都留**（任务 15d：取件时按 <see cref="Preference"/> 选，不再"先到者胜"）；
        /// 同实例/同身份重复登记 ⇒ false（不重复占位）。mcs 引用一旦加入不可撤回，故取件口径必须确定。
        /// </summary>
        public bool Register(Assembly asm)
        {
            if (asm == null) return false;
            string name;
            try { name = asm.GetName().Name; }
            catch { return false; }
            if (string.IsNullOrEmpty(name)) return false;
            if (name == "completions") return false;

            List<Assembly> list;
            if (!bySimpleName.TryGetValue(name, out list))
            {
                list = new List<Assembly>(1);
                bySimpleName.Add(name, list);
            }
            else
            {
                for (int i = 0; i < list.Count; i++)
                    if (ReferenceEquals(list[i], asm)
                        || string.Equals(SameStemChoice.IdentityOf(list[i]), SameStemChoice.IdentityOf(asm),
                            StringComparison.OrdinalIgnoreCase))
                        return false;      // 同实例/同身份：已经在册
            }

            list.Add(asm);
            nsHits.Clear();   // 注册表变动 ⇒ 命名空间探针的结论失效（与"候选目录缓存随新 Location 失效"同源）
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

        /// <summary>按**简单名**查找（忽略大小写）；未登记 ⇒ null。
        /// 任务 15d：同简单名多份 ⇒ 按 <see cref="Preference"/>（mcs 路 = 选**较旧**）取一份。</summary>
        public Assembly FindBySimpleName(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            List<Assembly> list;
            if (!bySimpleName.TryGetValue(simpleName, out list)) return null;
            return PickByPreference(list);
        }

        /// <summary>任务 15d：同简单名的**全部**已登记件（按登记顺序）——披露里的"未采用清单"来源。</summary>
        public IList<Assembly> FindAllBySimpleName(string simpleName)
        {
            var empty = new List<Assembly>();
            if (string.IsNullOrEmpty(simpleName)) return empty;
            List<Assembly> list;
            if (!bySimpleName.TryGetValue(simpleName, out list)) return empty;
            return new List<Assembly>(list);
        }

        /// <summary>
        /// 任务 15c：**命名空间/类型探针**——"哪个**已加载**程序集声明了 <paramref name="demanded"/>
        /// 这个命名空间或类型"。这是 Roslyn 侧 <c>ReferenceHealer.DefinesNamespace</c> 在 mcs 路径的等价物
        /// （Roslyn 侧靠它扫盘上"哪个文件声明了这个命名空间"，故能补到 `TEXT20.dll`；mcs 侧此前只有
        /// "文件名 stem == demanded 首段"这一条判据 ⇒ 多段名/程序集名与命名空间不同名时**该加也加不上**）。
        /// 关键洞察：模组程序集**本就被 RimWorld 加载**，故遍历"已加载件"的类型即可判定——**零 `LoadFrom`、
        /// 零副本、零污染**。
        /// **树匹配**（与 Roslyn 侧节点逐级下钻等效）：命中 =
        /// <c>type.Namespace == demanded</c> / <c>type.Namespace</c> 以 <c>demanded + "."</c> 开头 /
        /// <c>type.FullName == demanded</c> / <c>type.FullName</c> 以 <c>demanded + "."</c> 开头。
        /// 先整体找**精确**命中，再退**前缀**命中 ⇒ 同一 demanded 的结论确定（不依赖字典枚举顺序）。
        /// 任务 15d：命中多份（同简单名的两个版本都声明它）⇒ 按 <see cref="Preference"/> 取一份
        /// （mcs 路 = 选**较旧**）；单份/同身份副本 ⇒ 结论与 15c 逐字一致。
        /// 结论按 demanded 缓存（含未命中）；<see cref="Register"/> 见到新件即清空缓存。
        /// </summary>
        public Assembly FindDeclaringNamespace(string demanded)
        {
            if (string.IsNullOrEmpty(demanded)) return null;
            Assembly cached;
            if (nsHits.TryGetValue(demanded, out cached)) return cached;

            Assembly hit = PickByPreference(FindAllDeclaringNamespace(demanded));
            nsHits[demanded] = hit;
            return hit;
        }

        /// <summary>任务 15d：声明该命名空间/类型的**全部**已加载件（先精确、再前缀；无命中 ⇒ 空表）——
        /// 披露里的"未采用清单"来源。</summary>
        public IList<Assembly> FindAllDeclaringNamespace(string demanded)
        {
            if (string.IsNullOrEmpty(demanded)) return new List<Assembly>();
            List<Assembly> exact = ScanForNamespace(demanded, true);
            if (exact.Count > 0) return exact;
            return ScanForNamespace(demanded, false);
        }

        List<Assembly> ScanForNamespace(string demanded, bool exact)
        {
            var hits = new List<Assembly>();
            foreach (KeyValuePair<string, List<Assembly>> kv in bySimpleName)
            {
                for (int i = 0; i < kv.Value.Count; i++)
                    if (DeclaresNamespaceOrType(kv.Value[i], demanded, exact)) hits.Add(kv.Value[i]);
            }
            return hits;
        }

        /// <summary>按 <see cref="Preference"/> 从同简单名的多份里取一份（空表 ⇒ null；单份 ⇒ 原样）。</summary>
        Assembly PickByPreference(IList<Assembly> list)
        {
            if (list == null || list.Count == 0) return null;
            if (list.Count == 1) return list[0];
            SameStemCandidate best = SameStemChoice.Pick(SameStemChoice.FromAssemblies(list), Preference);
            if (best != null && best.Tag is Assembly) return (Assembly)best.Tag;
            return list[0];
        }

        static bool DeclaresNamespaceOrType(Assembly asm, string demanded, bool exact)
        {
            Type[] types = SafeGetTypes(asm);
            if (types == null) return false;
            string prefix = demanded + ".";
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null) continue;      // ReflectionTypeLoadException.Types 里可以有 null 项
                string ns = t.Namespace;
                string full = t.FullName;
                if (exact)
                {
                    if (ns == demanded || full == demanded) return true;
                }
                else
                {
                    if (!string.IsNullOrEmpty(ns) && ns.StartsWith(prefix, StringComparison.Ordinal)) return true;
                    if (!string.IsNullOrEmpty(full) && full.StartsWith(prefix, StringComparison.Ordinal)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// <see cref="Assembly.GetTypes"/> 的**不抛**包装：类型依赖缺席时它抛
        /// <see cref="ReflectionTypeLoadException"/>——此时拿 <c>.Types</c> 里的非 null 项继续判定
        /// （模组程序集在游戏里常有解析不全的依赖，抛一次不该让整条自愈通道哑掉）。
        /// </summary>
        static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types; }
            catch { return null; }
        }
    }

    /// <summary>
    /// 任务 15b：mcs 侧"**一轮**自愈解析"（诊断 → 该加入哪个引用，或响亮说不加入）。
    /// 轮次上限/到顶文案由调用方（<c>ScriptEvaluator.Evaluate</c>）按 <see cref="MaxHealRounds"/> 控制，
    /// 本类只负责一轮，且**只收高置信候选**（mcs 无 <c>RemoveReference</c>，加了撤不回）：
    /// ⓪ **已在引用集/已加入过的 ⇒ 跳过**（任务 15c：mcs 的 <c>ReferenceAssembly</c> 不去重也不报错，
    ///    把"重复加同一件"当"补上了"就是实测到的"连加 5 次、5 轮空转到顶"）；
    /// ① **命名空间探针**（任务 15c）= Roslyn 侧 `DefinesNamespace` 的已加载件等价物：树匹配"哪个**已加载**件
    ///    声明了这个命名空间/类型"⇒ 直接引用，**零 `LoadFrom`**（修 `UnityEngine.Video` ⇒ 首段错配、
    ///    `FlightPawn2Framework` ⇒ 程序集名与命名空间不同名这两类"该加也加不上"）；
    /// ② 已加载件注册表按**首段**简单名命中 ⇒ 直接引用，**零 `LoadFrom`**；
    /// ③ 否则按名（<see cref="McsHealingPolicy.PickCandidateByName"/>，首段 vs 文件名 stem）挑文件；
    ///    诊断带完整身份时**必须** <see cref="AssemblyName.GetAssemblyName"/> 校验身份相符，不符即拒绝；
    /// ④ 都不成立 ⇒ 经 <see cref="ReportSink"/> **响亮**说明原因（含"已尝试已加载件命名空间探针"），返回 null（不加入）。
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
            Preference = SameStemPreference.Oldest;   // 任务 15d：mcs 路 = 旧编译器配旧库（选**较旧**）
        }

        /// <summary>任务 15d：mcs 路的取舍偏好（人类裁定"没有 Roslyn 只有 mcs 用旧版"）——只影响披露里
        /// "原因="的文案；真正的取件由 <see cref="LoadedAssemblyRegistry.Preference"/> 完成（两处同值）。</summary>
        public SameStemPreference Preference { get; set; }

        /// <summary>文件 → 程序集（生产 = <c>Assembly.LoadFrom</c>；测试注入计数假件，用来断言"①分支 0 次加载"）。</summary>
        public Func<string, Assembly> PathLoader { get; set; }

        /// <summary>
        /// 任务 15c：**已加载件命名空间探针**（生产 = <c>LoadedAssemblyRegistry.FindDeclaringNamespace</c>）。
        /// 输入 demanded（命名空间或类型名，可多段），输出"声明它的**已加载**程序集"或 null。
        /// 这是"该加的时候加得上"的关键通道（`UnityEngine.Video` ⇒ `UnityEngine.VideoModule`；
        /// `FlightPawn2Framework` ⇒ 含 `FlightPawn2Framework.FogThrough.*` 的 `TEXT20`），且**零 `LoadFrom`**。
        /// 未注入 ⇒ 逐字退回 15b 的"已加载件按首段 + 按名文件"两条通道。
        /// </summary>
        public Func<string, Assembly> LoadedNamespaceProbe { get; set; }

        /// <summary>
        /// 任务 15d：**同简单名多份**的全量枚举（生产 = <c>LoadedAssemblyRegistry.FindAllDeclaringNamespace</c>）。
        /// 只用于**披露未采用清单**：命中的那一份已由 <see cref="LoadedNamespaceProbe"/> 按编译器策略
        /// （mcs = 选较旧）选出。未注入 ⇒ 无披露（退化成单份语义，与 15c 逐字一致）。
        /// </summary>
        public Func<string, IList<Assembly>> LoadedNamespaceProbeAll { get; set; }

        /// <summary>任务 15d：已加载件**注册表通道**的全量枚举（生产 = <c>LoadedAssemblyRegistry.FindAllBySimpleName</c>）。
        /// 同 <see cref="LoadedNamespaceProbeAll"/>：只喂披露，未注入 ⇒ 无披露。</summary>
        public Func<string, IList<Assembly>> LoadedRegistryAll { get; set; }

        /// <summary>
        /// 任务 15c：**该件是否已在 mcs 引用集里**（生产 = 求值器维护的已引用简单名集合）。
        /// 为什么必须有：mcs 的 <c>ReferenceAssembly</c> 对**重复件既不去重也不报错** ⇒ 上层 `Reference`
        /// 恒返回 true ⇒ 自愈会把"本就在最小集里"的件当成"补上了"（实测：`UnityEngine.Video` 的首段
        /// `UnityEngine` 命中已在引用集的 `UnityEngine.dll`，连加 5 次、5 轮空转到顶，诊断一个字没变）。
        /// 故加之前先问它在不在引用集：在 ⇒ **不补引用、不占轮次**，诚实返回 null 停止重试。
        /// </summary>
        public Func<Assembly, bool> IsAlreadyReferenced { get; set; }

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

        /// <summary>任务 15c：本实例累计"命名空间探针命中"次数（探针返回了非 null 即计）。</summary>
        public int NamespaceProbeHitCount { get; private set; }

        /// <summary>任务 15c：本实例累计"候选已在引用集 ⇒ 跳过"次数（这些轮次**不计入** healed）。</summary>
        public int AlreadyReferencedSkipCount { get; private set; }

        /// <summary>任务 15c：上一轮是否因"已在引用集/本实例已加入过"而**跳过**（= 返回 null 且未加任何引用）。</summary>
        public bool LastSkippedAlreadyReferenced { get; private set; }

        /// <summary>本实例已成功加入过的程序集简单名（"已加入过"那半条判据的来源）。</summary>
        readonly HashSet<string> addedByThisHealer = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            LastSkippedAlreadyReferenced = false;

            string demanded = McsHealingPolicy.DemandedName(diagnostics);
            if (string.IsNullOrEmpty(demanded))
            {
                Report("自愈放弃：该诊断里读不出『被要求的名字』（CS0012/CS1069/CS0234/CS0246/CS0103 都没有）⇒ 不折腾。");
                return null;
            }
            string head = HeadOf(demanded);

            string excludedNote = null;
            string alreadyNote = null;     // "候选存在，但已在引用集"的说明（并入最终拒绝对话）
            bool sawAlreadyReferenced = false;

            // ── ① 命名空间探针优先：树匹配"哪个**已加载**件声明了这个命名空间/类型"（**零 LoadFrom**）──
            // 必须用**完整 demanded**去找（不能只拿首段）：实测两类缺口都出在这里——
            // ①`UnityEngine.Video` 的真声明者是 `UnityEngine.VideoModule.dll`，而首段 `UnityEngine` 命中的是
            //   本就在最小集里的 `UnityEngine.dll`；②`FlightPawn2Framework` 在 `TEXT20.dll` 里，程序集名与
            //   命名空间**不同名**，按名/按首段都匹配不上。
            Assembly probed = ProbeLoadedNamespace(demanded);
            if (probed != null)
            {
                NamespaceProbeHitCount++;
                string pName = SimpleNameOf(probed);
                string pLoc = SafeLocation(probed);
                string pShow = pLoc ?? pName ?? "<无 Location 的动态件>";
                string pIdent = SameStemChoice.IdentityOf(probed);
                // 任务 15d：同简单名多份 ⇒ 先打**同款披露**（采用=谁/未采用哪些/原因=按编译器策略：mcs 选较旧），
                // 再打命中日志（含身份 Version+PKT 与路径）——否则"补上了"仍说不清补的是哪一版。
                ReportChoiceDisclosure(DescribeProbeDisclosure(probed, demanded));
                if (AlreadyInReferenceSet(probed))
                {
                    sawAlreadyReferenced = true;
                    alreadyNote = "（命名空间探针命中 " + pShow + "（身份=" + pIdent
                        + "），该件**已在引用集**：再 ReferenceAssembly 是空转，mcs 不去重也不报错）";
                }
                else if (IsExcluded(pLoc))
                {
                    // 开关 OFF：模组程序集不进引用集（**含本通道**，与 Roslyn 侧 ReferenceHealer 口径一致）
                    excludedNote = "（『" + (pName ?? demanded) + "』是已加载的模组件，开关 C# Console Allow Mod Type Resolution=OFF ⇒ 排除集命中）";
                }
                else if ((!string.IsNullOrEmpty(pName) && Refused(pName)) || Refused(head))
                {
                    Report("自愈拒绝：『" + (pName ?? demanded) + "』命中引用黑名单 ⇒ 未加入（mcs 引用不可撤回）。");
                    return null;
                }
                else if (Add(probed))
                {
                    LoadedRegistryHitCount++;
                    LastFromLoadedRegistry = true;
                    LastAdded = pLoc ?? pName;
                    Report("自愈命中已加载件：" + pShow + "（身份=" + pIdent + "；命名空间探针：它是『" + demanded
                        + "』的声明者；直接 ReferenceAssembly，未 LoadFrom、零副作用）。");
                    return LastAdded;
                }
                else
                {
                    Report("自愈失败：『" + demanded + "』的声明者 " + pShow
                        + " 已在进程内但 ReferenceAssembly 未生效（引用集拒绝）⇒ 未加入。");
                    return null;
                }
            }

            // ── ② 已加载件注册表（按**首段**简单名；15b 既有通道）──
            // 任务 15d：`FindBySimpleName` 在同简单名多份时已按编译器策略（mcs = **选较旧**）取一份。
            Assembly loaded = registry == null ? null : registry.FindBySimpleName(head);
            if (loaded != null)
            {
                string loc = SafeLocation(loaded);
                string ident = SameStemChoice.IdentityOf(loaded);
                ReportChoiceDisclosure(DescribeRegistryDisclosure(loaded, head));
                if (AlreadyInReferenceSet(loaded))
                {
                    // 实测缺口①：首段命中的件本就在引用集 ⇒ 不能再当"补上了"（否则连加 5 次空转到顶）
                    sawAlreadyReferenced = true;
                    alreadyNote = "（首段『" + head + "』命中的已加载件**已在引用集**：再 ReferenceAssembly 是空转，"
                        + "mcs 不去重也不报错）";
                }
                else if (IsExcluded(loc))
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
                    // 任务 15d：命中日志补上**路径与版本**（原来只有名字，看不出补的哪一版）
                    Report("自愈命中已加载件：" + (loc ?? head) + "（简单名=" + head + "；身份=" + ident
                        + "；直接 ReferenceAssembly，未 LoadFrom、零副作用）。");
                    return LastAdded;
                }
                else
                {
                    Report("自愈失败：『" + head + "』已在进程内但 ReferenceAssembly 未生效（引用集拒绝）⇒ 未加入。");
                    return null;
                }
            }

            // ── ③ 文件候选：按名优先（首段 vs stem），排除集过滤 ──
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
                if (sawAlreadyReferenced)
                {
                    ReportSkipAlreadyReferenced(demanded, "按名也没有未引用的新件（已尝试已加载件命名空间探针：" + ProbeState()
                        + "、候选 " + seen + " 个、被排除集挡下 " + excluded + " 个）", alreadyNote, excludedNote);
                    return null;
                }
                Report("自愈放弃：『" + demanded + "』（首段 " + head + "）按名无匹配 ⇒ 未加入 —— "
                    + "已尝试已加载件命名空间探针（" + ProbeState() + "）；"
                    + "候选 " + seen + " 个、被排除集挡下 " + excluded + " 个；"
                    + "mcs 引用**加入后不可撤回**，故宁可不加也不猜。" + (excludedNote ?? ""));
                return null;
            }

            if (AlreadyReferencedStem(cand))
            {
                // 文件通道也得挡住"本就在引用集"的件（否则 LoadFrom 一份已在引用集的同名件 = 空转 + 白加载）
                ReportSkipAlreadyReferenced(demanded, "按名挑中的 " + Path.GetFileName(cand)
                    + " 同名件已在引用集", alreadyNote, excludedNote);
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

        /// <summary>
        /// 任务 15c：本轮**什么都没加**、原因是"候选已在引用集"时的统一点（计数 + 响亮报告）。
        /// 语义：返回 null ⇒ 调用方停止重试且**不占轮次**（`healed` 不增），日志里不出现"自愈补引用"/"命中已加载件"。
        /// </summary>
        void ReportSkipAlreadyReferenced(string demanded, string why, string alreadyNote, string excludedNote)
        {
            AlreadyReferencedSkipCount++;
            LastSkippedAlreadyReferenced = true;
            Report("自愈跳过：『" + demanded + "』没有可加的新引用 —— " + why
                + "⇒ 不补引用、不占轮次、停止重试（mcs 引用不可撤回，重复加同一件只会空转到顶）。"
                + (alreadyNote ?? "") + (excludedNote ?? ""));
        }

        Assembly ProbeLoadedNamespace(string demanded)
        {
            Func<string, Assembly> probe = LoadedNamespaceProbe;
            if (probe == null) return null;
            try { return probe(demanded); }
            catch (Exception ex)
            {
                Report("自愈警告：已加载件命名空间探针抛 " + ex.GetType().Name + "：" + ex.Message
                    + " ⇒ 本轮退回按首段/按名解析。");
                return null;
            }
        }

        string ProbeState()
        {
            return LoadedNamespaceProbe == null
                ? "未注入"
                : "未命中（树匹配：没有已加载件声明它）";
        }

        // ───────────────────── 任务 15d：同简单名多份的**采用披露**（mcs 路） ─────────────────────

        /// <summary>
        /// 任务 15d：**已加载件通道的采用披露**（与 Roslyn 侧 `ReferenceHealer.DescribeSameStemChoices`
        /// 同款文案，只是"原因"=按编译器策略：**mcs 选较旧**）。
        /// 公开静态 ⇒ 可离线断言；返回 null = 无"同简单名身份不同"的另一份（常态，零噪声）。
        /// </summary>
        public static string DescribeLoadedChoice(Assembly chosen, IList<Assembly> all,
            SameStemPreference pref, string channelLabel)
        {
            return SameStemChoice.DescribeAssembly(chosen, all, pref, channelLabel);
        }

        /// <summary>①命名空间探针通道的披露（枚举注入缺失 ⇒ null，退化成单份语义）。</summary>
        string DescribeProbeDisclosure(Assembly probed, string demanded)
        {
            Func<string, IList<Assembly>> all = LoadedNamespaceProbeAll;
            if (all == null || probed == null) return null;
            IList<Assembly> list;
            try { list = all(demanded); }
            catch { return null; }
            return DescribeLoadedChoice(probed, list, Preference, "已加载件命名空间探针通道");
        }

        /// <summary>②已加载件注册表通道的披露（枚举注入缺失 ⇒ null）。</summary>
        string DescribeRegistryDisclosure(Assembly loaded, string head)
        {
            Func<string, IList<Assembly>> all = LoadedRegistryAll;
            if (all == null || loaded == null) return null;
            IList<Assembly> list;
            try { list = all(head); }
            catch { return null; }
            return DescribeLoadedChoice(loaded, list, Preference, "已加载件注册表通道");
        }

        /// <summary>披露只在该通道"有多份且身份不同"时才是非 null（单份 ⇒ null ⇒ 零噪声，不写日志）。</summary>
        void ReportChoiceDisclosure(string disclosure)
        {
            if (!string.IsNullOrEmpty(disclosure)) Report(disclosure);
        }

        /// <summary>
        /// 任务 15c：该件是否**已在 mcs 引用集**——本实例加过的（简单名）+ 调用方注入的判据（生产 = 求值器的引用集名单）。
        /// 判据缺失（未注入且本实例没加过）⇒ false（逐字保留 15b 行为）。
        /// </summary>
        bool AlreadyInReferenceSet(Assembly asm)
        {
            string name = SimpleNameOf(asm);
            if (!string.IsNullOrEmpty(name) && addedByThisHealer.Contains(name)) return true;
            Func<Assembly, bool> f = IsAlreadyReferenced;
            if (f == null) return false;
            try { return f(asm); }
            catch { return false; }
        }

        /// <summary>文件路径版的同一判据：按**文件名 stem** 认身份，再问引用集（求值器按简单名记引用）。</summary>
        bool AlreadyReferencedStem(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(stem)) return false;
            if (addedByThisHealer.Contains(stem)) return true;
            Assembly known = registry == null ? null : registry.FindBySimpleName(stem);
            return known != null && AlreadyInReferenceSet(known);
        }

        bool Add(Assembly asm)
        {
            Func<Assembly, bool> add = AddReference;
            if (add == null) return false;
            bool ok;
            try { ok = add(asm); }
            catch (Exception ex)
            {
                Report("自愈失败：ReferenceAssembly 抛 " + ex.GetType().Name + "：" + ex.Message + " ⇒ 未加入。");
                return false;
            }
            // 任务 15c：记住"本实例加过的件" ⇒ 同一件的第二轮不再重复加（mcs 不去重，重复加=空转到顶）
            if (ok)
            {
                string name = SimpleNameOf(asm);
                if (!string.IsNullOrEmpty(name)) addedByThisHealer.Add(name);
            }
            return ok;
        }

        /// <summary>程序集**简单名**（取不到 ⇒ null；不抛）。</summary>
        internal static string SimpleNameOf(Assembly asm)
        {
            if (asm == null) return null;
            try { return asm.GetName().Name; }
            catch { return null; }
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Verse;   // ModContentPack / LoadedModManager

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 引用集**只有一份**：`SmallProfile` 的 **22** 个程序集——前 21 个从本体 `Managed` 解析，
    /// 第 22 个 `UniverseLib.Mono` 是 **UE 侧程序集**（B1 根因清扫轮新增，见下）。
    /// **不预设任何其它引用**（不扫已加载程序集、不预先加模组）——
    /// 一切额外引用由任务 13 的**诊断自愈**按需补入（CS0012 直接给程序集名；CS0246/CS0103 按命名空间反查）。
    /// 实测依据：21 个的成员覆盖残差为 0；本体 106 个在启动后虽全部已加载，但**不加进编译期引用集**，
    /// 以压住元数据开销（明细见报告 §4.6）。
    /// ⚠️ B1（2026-10-09 根因清扫轮）：`DefaultUsings.BuiltIn` 含 `UniverseLib`，而它**不在本体 Managed**
    /// （是 UE 自己的依赖）⇒ 引用集缺它时默认 using 里的 `using UniverseLib;` 会让**每一次提交**都
    /// CS0246 失败（实测：`new[]{1,2,3}.Sum()` 都报 `'UniverseLib' could not be found`），
    /// 而求值器仍显示 Roslyn ⇒ 功能整体不可用 + 报错误导。修法：① 补第 22 件（来源=UE 程序集目录，
    /// 不硬编码 Mods 路径）② 仍缺时由 `DefaultUsings.Unavailable` 数据驱动地剔除该 using 并明确告警。
    /// </summary>
    public static class ReferenceScanner
    {
        /// <summary>唯一引用集：**22** 个 = 本体 21（实测残差 0）+ UE 侧 1（UniverseLib.Mono）。
        /// ⚠️ 前 21 项的**顺序契约不变**（本体 Managed 解析、先加载的在前）；第 22 项追加在末尾。</summary>
        public static readonly string[] SmallProfile =
        {
            "mscorlib", "netstandard", "System", "System.Core", "System.Xml", "System.Xml.Linq",
            "Assembly-CSharp", "Assembly-CSharp-firstpass",
            "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.IMGUIModule", "UnityEngine.UI",
            "UnityEngine.AudioModule", "UnityEngine.TextRenderingModule",
            "UnityEngine.PhysicsModule", "UnityEngine.AnimationModule", "UnityEngine.ParticleSystemModule",
            "UnityEngine.AssetBundleModule",
            "Unity.Collections", "Unity.Mathematics", "com.rlabrecque.steamworks.net",
            // ↓ 第 22 件：**UE 侧**程序集（非本体 Managed），来源目录见 UeAssemblyDir()
            "UniverseLib.Mono",
        };

        /// <summary>22 件里哪些来自 UE 自身程序集目录（其余来自本体 Managed）。</summary>
        public static readonly HashSet<string> UeSideProfile = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UniverseLib.Mono",
        };

        /// <summary>报告分母（`refs: small=&lt;n&gt;/22 total=&lt;m&gt;`）。</summary>
        public const int ExpectedProfileCount = 22;

        /// <summary>本体根目录（实测 Application.dataPath == &lt;game&gt;/RimWorldWin64_Data）。</summary>
        public static string GameRootOfThisProcess()
            => Path.GetDirectoryName(UnityEngine.Application.dataPath);

        /// <summary>本体 Managed 目录（实测存在且 106 个 dll 齐全）。</summary>
        public static string ManagedDir()
            => Path.Combine(UnityEngine.Application.dataPath, "Managed");

        /// <summary>
        /// UE 侧程序集目录（`UniverseLib.Mono.dll` 所在处）。**不硬编码 Mods 路径**——按序取：
        /// ① 本 UE 程序集自身所在目录（`Assembly.GetExecutingAssembly().Location`；Mono 下对 LoadFrom
        ///    加载的程序集可靠，且换副本/重定向跟得住）；
        /// ② 回退 `&lt;ExplorerFolder&gt;/../Assemblies`（部署脚本把 UE dll 与依赖都放 `Assemblies\`；
        ///    仅在 ① 拿到空路径/非目录时才用——`ExplorerFolder` 在壳类 cctor 期不可用，但本方法只在
        ///    Reset 期调用，可用）。
        /// 两条都拿不到 → 返回 null（调用方据此判定"UE 侧引用缺失"并走 B1 护栏）。
        /// </summary>
        public static string UeAssemblyDir()
        {
            try
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    string dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch { /* 动态程序集无 Location：回落 ② */ }

            try
            {
                string folder = ExplorerCore.ExplorerFolder;   // <ModRoot>\UE_Data\sinai-dev-UnityExplorer
                if (!string.IsNullOrEmpty(folder))
                {
                    // ⚠️ net35 的 Path.Combine 没有 3 参/params 重载（本文件进 4 个 Mono 配置），故嵌套调用
                    string dir = Path.GetFullPath(Path.Combine(Path.Combine(folder, ".."), "Assemblies"));
                    if (Directory.Exists(dir)) return dir;
                }
            }
            catch { /* ExplorerFolder 不可用（Loader 未赋值）：返回 null */ }

            return null;
        }

        /// <summary>兼容旧签名的两参重载（不关心默认 using 可用性时用）。</summary>
        public static ReferenceSet Collect(string blacklist, out string report)
            => Collect(blacklist, out report, out _);

        /// <summary>
        /// 组装引用集。`unavailableDefaultUsings` = 引用集缺其来源程序集的**默认 using 根命名空间**
        /// （B1 护栏：调用方须把它们从本会话默认 using 里剔除并明确告警，否则一条 using 会拖垮整条 Roslyn 路径）。
        /// 报告格式：`refs: small=&lt;n&gt;/22 total=&lt;m&gt;`；UE 侧缺件时**响亮**写明目录与后果（不静默降级）。
        /// </summary>
        public static ReferenceSet Collect(string blacklist, out string report,
            out List<string> unavailableDefaultUsings)
        {
            var set = new ReferenceSet();
            int fromManaged = 0, fromUe = 0;
            string managed = ManagedDir();
            string ueDir = UeAssemblyDir();

            foreach (string simple in SmallProfile)
            {
                // 黑名单照旧生效（TryAdd 内部判定），顺序契约不变（先加载的在前）。
                if (UeSideProfile.Contains(simple))
                {
                    if (ueDir != null && set.TryAdd(Path.Combine(ueDir, simple + ".dll"), simple, blacklist))
                        fromUe++;
                    continue;
                }

                if (!Directory.Exists(managed)) continue;
                string f = Path.Combine(managed, simple + ".dll");
                if (File.Exists(f) && set.TryAdd(f, simple, blacklist)) fromManaged++;
            }

            unavailableDefaultUsings = DefaultUsings.Unavailable(DefaultUsings.BuiltIn, set.Contains);

            report = $"refs: small={fromManaged + fromUe}/{ExpectedProfileCount} total={set.Paths.Count}"
                + $" (managed={fromManaged} ue={fromUe})";
            if (fromUe == 0 && !set.Contains("UniverseLib.Mono"))
                report += "；⚠ UE 侧引用 UniverseLib.Mono.dll 缺失（UE 程序集目录="
                    + (ueDir ?? "<不可判定>") + "）→ 本会话默认 using 剔除 "
                    + string.Join(",", unavailableDefaultUsings.ToArray());

            return set;
        }

        /// <summary>
        /// 供**自愈器**使用的候选程序集路径（不预先加入引用集）：由模组加载器汇报的模组程序集。
        /// ⚠️ 不要扫 &lt;Game&gt;/Mods：Steam 创意工坊模组不在那里（实测 33 个模组程序集里 20 个在 workshop 目录）。
        /// ⚠️ `mod.assemblies` 是**字段**（类型 `ModAssemblyHandler`），程序集列表是它的**字段** `loadedAssemblies`。
        /// </summary>
        public static List<string> ModAssemblyPaths(string blacklist)
        {
            var list = new List<string>();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies == null) continue;
                List<Assembly> asms = mod.assemblies.loadedAssemblies;
                if (asms == null) continue;
                foreach (Assembly a in asms)
                {
                    string loc;
                    try { loc = a.Location; } catch { continue; }
                    if (string.IsNullOrEmpty(loc)) continue;
                    if (ReferenceSet.IsBlacklisted(a.GetName().Name, blacklist)) continue;
                    if (!list.Contains(loc)) list.Add(loc);
                }
            }
            return list;
        }
    }
}

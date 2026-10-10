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
        /// <summary>唯一引用集（**22** 个，定义在 <see cref="ReferenceProfile"/>，那里可离线单测）。
        /// 保留本转发名以免调用方/文档断裂。</summary>
        public static string[] SmallProfile => ReferenceProfile.SmallProfile;

        /// <summary>22 件里哪些来自 UE 自身程序集目录（转发，见 <see cref="ReferenceProfile"/>）。</summary>
        public static HashSet<string> UeSideProfile => ReferenceProfile.UeSideProfile;

        /// <summary>报告分母（`refs: small=&lt;n&gt;/22 total=&lt;m&gt;`）。</summary>
        public const int ExpectedProfileCount = ReferenceProfile.ExpectedProfileCount;

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
        /// 报告格式：`refs: small=&lt;n&gt;/22 total=&lt;m&gt; (managed=… ue=… self=…)`；缺件时**响亮**写明目录与后果。
        /// ⚠️ 纯逻辑在 <see cref="ReferenceProfile.CollectFrom"/>（可离线单测）；本方法只负责**推导路径**
        /// （依赖 Unity/UE 类型，故不能进离线测试程序集）。R2：UE 侧命中要求**文件存在**。
        /// </summary>
        public static ReferenceSet Collect(string blacklist, out string report,
            out List<string> unavailableDefaultUsings)
        {
            List<string> missingUe;
            // §19 (i) 宿主自引用：UE 自己的程序集（提交类要继承 `ReplHostBase`，缺它每次提交都 CS0246）。
            // 报告里以 `self=` 单独呈现，**不计入 `small=`**（后者是"本体 21 + UE 侧 1"的口径）。
            string selfPath = null, selfName = null;
            try
            {
                Assembly self = Assembly.GetExecutingAssembly();
                selfPath = self.Location;
                selfName = self.GetName().Name;
            }
            catch { /* 动态程序集无 Location/Name：报 self=0 并响亮告警（见 ReferenceProfile） */ }

            return ReferenceProfile.CollectFrom(ManagedDir(), UeAssemblyDir(), selfPath, selfName, blacklist,
                out report, out unavailableDefaultUsings, out missingUe);
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

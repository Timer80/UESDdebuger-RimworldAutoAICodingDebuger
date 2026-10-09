using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Verse;   // ModContentPack / LoadedModManager

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 引用集**只有一份**：`SmallProfile` 的 21 个本体程序集（从 `Managed` 解析）。
    /// **不预设任何其它引用**（不扫已加载程序集、不预先加模组）——
    /// 一切额外引用由任务 13 的**诊断自愈**按需补入（CS0012 直接给程序集名；CS0246/CS0103 按命名空间反查）。
    /// 实测依据：21 个的成员覆盖残差为 0；本体 106 个在启动后虽全部已加载，但**不加进编译期引用集**，
    /// 以压住元数据开销（明细见报告 §4.6）。
    /// </summary>
    public static class ReferenceScanner
    {
        /// <summary>唯一引用集：21 个（实测残差 0；加 UnityEngine.AssetBundleModule 后归零）。</summary>
        public static readonly string[] SmallProfile =
        {
            "mscorlib", "netstandard", "System", "System.Core", "System.Xml", "System.Xml.Linq",
            "Assembly-CSharp", "Assembly-CSharp-firstpass",
            "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.IMGUIModule", "UnityEngine.UI",
            "UnityEngine.AudioModule", "UnityEngine.TextRenderingModule",
            "UnityEngine.PhysicsModule", "UnityEngine.AnimationModule", "UnityEngine.ParticleSystemModule",
            "UnityEngine.AssetBundleModule",
            "Unity.Collections", "Unity.Mathematics", "com.rlabrecque.steamworks.net",
        };

        /// <summary>本体根目录（实测 Application.dataPath == &lt;game&gt;/RimWorldWin64_Data）。</summary>
        public static string GameRootOfThisProcess()
            => Path.GetDirectoryName(UnityEngine.Application.dataPath);

        /// <summary>本体 Managed 目录（实测存在且 106 个 dll 齐全）。</summary>
        public static string ManagedDir()
            => Path.Combine(UnityEngine.Application.dataPath, "Managed");

        public static ReferenceSet Collect(string blacklist, out string report)
        {
            var set = new ReferenceSet();
            int fromManaged = 0;

            string managed = ManagedDir();
            if (Directory.Exists(managed))
            {
                foreach (string simple in SmallProfile)
                {
                    string f = Path.Combine(managed, simple + ".dll");
                    if (File.Exists(f) && set.TryAdd(f, simple, blacklist)) fromManaged++;
                }
            }

            report = $"refs: small={fromManaged}/{SmallProfile.Length} total={set.Paths.Count}";
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

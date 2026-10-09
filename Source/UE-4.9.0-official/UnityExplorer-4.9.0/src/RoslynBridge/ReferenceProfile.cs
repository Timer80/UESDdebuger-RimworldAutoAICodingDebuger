using System;
using System.Collections.Generic;
using System.IO;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 引用集"档案 + 组装"的**纯逻辑**（无 Verse/UnityEngine/ExplorerCore 依赖 ⇒ 可离线单测，见
    /// `RoslynBridgeTests.TestReferenceProfile`）。
    /// 与 <see cref="ReferenceScanner"/> 的分工：本类只管"给定两个目录 + 黑名单 → 引用集 + 报告"，
    /// 目录**怎么推导**（本体 Managed / UE 程序集目录）留在 ReferenceScanner（那里依赖 Unity/UE 类型）。
    /// ⚠️ R2（复审必修）：UE 侧分支**必须** `File.Exists` 校验——原实现只判 `ueDir != null`，
    /// 目录在而 dll 缺失时名字仍进集合 ⇒ report 假报 `22/22`、响亮告警（依赖 `!set.Contains`）永不触发、
    /// `DefaultUsings.Unavailable` 判为"可用" ⇒ `using UniverseLib;` 不被剔除 ⇒
    /// **之后每次提交都 CS0246（B1 症状原样回归且完全静默）**。
    /// </summary>
    public static class ReferenceProfile
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
            // ↓ 第 22 件：**UE 侧**程序集（非本体 Managed），来源目录由调用方给出
            "UniverseLib.Mono",
        };

        /// <summary>22 件里哪些来自 UE 自身程序集目录（其余来自本体 Managed）。</summary>
        public static readonly HashSet<string> UeSideProfile = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UniverseLib.Mono",
        };

        /// <summary>报告分母（`refs: small=&lt;n&gt;/22 total=&lt;m&gt;`）。</summary>
        public const int ExpectedProfileCount = 22;

        /// <summary>
        /// 组装引用集（纯函数式：只读两个目录的存在性，不触碰任何全局状态）。
        /// <paramref name="missingUeSideAssemblies"/> = UE 侧**实际缺失**的件（按文件存在性判定，
        /// 不依赖"名字是否进了集合"——那正是 R2 的假绿来源）；
        /// <paramref name="unavailableDefaultUsings"/> = 引用集缺其来源程序集的默认 using 根命名空间。
        /// </summary>
        public static ReferenceSet CollectFrom(string managedDir, string ueDir, string blacklist,
            out string report, out List<string> unavailableDefaultUsings,
            out List<string> missingUeSideAssemblies)
        {
            var set = new ReferenceSet();
            int fromManaged = 0, fromUe = 0;
            missingUeSideAssemblies = new List<string>();

            bool managedUsable = !string.IsNullOrEmpty(managedDir) && Directory.Exists(managedDir);
            foreach (string simple in SmallProfile)
            {
                // 黑名单照旧生效（TryAdd 内部判定），顺序契约不变（先加载的在前）。
                if (UeSideProfile.Contains(simple))
                {
                    string uePath = string.IsNullOrEmpty(ueDir) ? null : Path.Combine(ueDir, simple + ".dll");
                    // R2：**文件存在**才算命中（只判目录会让缺失的 dll 也进集合 ⇒ 假报 22/22）
                    if (uePath != null && File.Exists(uePath) && set.TryAdd(uePath, simple, blacklist))
                        fromUe++;
                    else
                        missingUeSideAssemblies.Add(simple);
                    continue;
                }

                if (!managedUsable) continue;
                string f = Path.Combine(managedDir, simple + ".dll");
                if (File.Exists(f) && set.TryAdd(f, simple, blacklist)) fromManaged++;
            }

            unavailableDefaultUsings = DefaultUsings.Unavailable(DefaultUsings.BuiltIn, set.Contains);

            report = $"refs: small={fromManaged + fromUe}/{ExpectedProfileCount} total={set.Paths.Count}"
                + $" (managed={fromManaged} ue={fromUe})";
            // 响亮告警：按**实际缺失**触发（R2 前是 `!set.Contains(...)`，缺件时因假命中而永不触发）
            if (missingUeSideAssemblies.Count > 0)
                report += "；⚠ UE 侧引用缺失 " + string.Join(",", missingUeSideAssemblies.ToArray())
                    + "（UE 程序集目录=" + (ueDir ?? "<不可判定>") + "）"
                    + "→ 本会话默认 using 剔除 " + string.Join(",", unavailableDefaultUsings.ToArray());

            return set;
        }
    }
}

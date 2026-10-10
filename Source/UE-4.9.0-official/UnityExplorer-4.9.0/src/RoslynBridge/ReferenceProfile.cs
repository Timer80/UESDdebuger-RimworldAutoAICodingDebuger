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
        /// <summary>
        /// ⚠️ **仅供旧测试形态 / 勿在生产调用**（S-次要项，§20）：本重载恒传 `selfAssemblyPath=null`
        /// ⇒ 必然产出"宿主自引用缺失"告警、并把 `UnityExplorer` 计入"不可用默认 using"。
        /// 生产路径一律走下面的 6 参重载（由 `ReferenceScanner.Collect` 传真实宿主程序集）。
        /// </summary>
        public static ReferenceSet CollectFrom(string managedDir, string ueDir, string blacklist,
            out string report, out List<string> unavailableDefaultUsings,
            out List<string> missingUeSideAssemblies)
        {
            return CollectFrom(managedDir, ueDir, null, null, blacklist,
                out report, out unavailableDefaultUsings, out missingUeSideAssemblies);
        }

        /// <summary>
        /// 带**宿主自引用**的完整口径（§19 (i)）：报告形如
        /// `refs: small=22/22 total=23 (managed=21 ue=1 self=1)`。
        /// 自引用 = UE 自己的程序集（<paramref name="selfAssemblyPath"/>/<paramref name="selfSimpleName"/>）：
        /// 它**不计入 `small=`**（那是"本体 21 + UE 侧 1"的口径），单独以 `self=` 呈现；
        /// 但必须进引用集——Roslyn 生成类继承 `ReplHostBase`（宿主程序集里的类型），缺它每次提交都 CS0246。
        /// </summary>
        public static ReferenceSet CollectFrom(string managedDir, string ueDir,
            string selfAssemblyPath, string selfSimpleName, string blacklist,
            out string report, out List<string> unavailableDefaultUsings,
            out List<string> missingUeSideAssemblies)
        {
            var set = new ReferenceSet();
            // §4.10-2：同简单名不同身份的去重**不得静默**——把 ReferenceSet 的结构化日志收进报告串
            //（方法组注入，C.5：无 lambda 捕获；报告串由宿主写进日志）。
            var dedupe = new List<string>();
            set.ReportSink = dedupe.Add;
            int fromManaged = 0, fromUe = 0, self = 0;
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

            // 宿主自引用：**最后**加入（不改前 22 项的解析顺序）；走 TryAdd ⇒ 黑名单/去重照常生效
            if (!string.IsNullOrEmpty(selfAssemblyPath) && !string.IsNullOrEmpty(selfSimpleName)
                && File.Exists(selfAssemblyPath) && set.TryAdd(selfAssemblyPath, selfSimpleName, blacklist))
                self = 1;

            // 默认 using 可用性：`@self` 标记按"自引用是否真进了引用集"判定
            unavailableDefaultUsings = DefaultUsings.Unavailable(DefaultUsings.BuiltIn,
                owner => owner == DefaultUsings.SelfAssemblyToken ? self == 1 : set.Contains(owner));

            report = $"refs: small={fromManaged + fromUe}/{ExpectedProfileCount} total={set.Paths.Count}"
                + $" (managed={fromManaged} ue={fromUe} self={self})";
            // 响亮告警：按**实际缺失**触发（R2 前是 `!set.Contains(...)`，缺件时因假命中而永不触发）
            if (missingUeSideAssemblies.Count > 0)
                report += "；⚠ UE 侧引用缺失 " + string.Join(",", missingUeSideAssemblies.ToArray())
                    + "（UE 程序集目录=" + (ueDir ?? "<不可判定>") + "）"
                    + "→ 本会话默认 using 剔除 " + string.Join(",", unavailableDefaultUsings.ToArray());
            if (self == 0)
                report += "；⚠ 宿主自引用缺失（selfAssemblyPath=" + (selfAssemblyPath ?? "<不可判定>")
                    + "）→ 提交类继承 `ReplHostBase` 会 CS0246、REPL 助手不可用";
            // §4.10-2：去重一律随报告出现（防"新版静默不生效"——dll 热重载场景的病根）。
            // 微审④低危：条目数没有天然上限，报告串要**截断**（前 3 条 + 总数），避免整串写进日志过长。
            if (dedupe.Count > 0)
            {
                const int shown = 3;
                var head = new List<string>();
                for (int i = 0; i < dedupe.Count && i < shown; i++) head.Add(dedupe[i]);
                report += "；⚠ 引用集去重 " + dedupe.Count + " 例（同简单名只保留先到者、已结构化登记）："
                    + string.Join(" | ", head.ToArray())
                    + (dedupe.Count > shown ? " …（仅示前 " + shown + " 例）" : "");
            }

            return set;
        }
    }
}

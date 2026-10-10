using System;
using System.Collections.Generic;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 任务 15a：mcs（Mono.CSharp）侧的"最小引用 + 引用自愈"策略，**纯逻辑**。
    /// 零 Roslyn 依赖（mcs 模式下 Roslyn 载荷根本不在进程里）、零 UE/Unity 依赖、net35 安全
    /// （故不用 <c>Array.Empty</c> 等 net40+ API）——本文件可被测试工程 link 做离线断言。
    /// 诊断解析复用 <see cref="MissingReferenceParser"/>（任务 15a 已兼容 mcs 的反引号引号格式）。
    /// </summary>
    public static class McsHealingPolicy
    {
        /// <summary>
        /// 从诊断文本抽"诊断要求的名字"：优先 CS0012/CS1069 的**程序集简单名**；否则 CS0234 的
        /// 组合命名空间；否则 CS0246/CS0103 的符号名；都没有 ⇒ null（调用方据此不自愈）。
        /// </summary>
        public static string DemandedName(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics)) return null;

            string asm = MissingReferenceParser.AssemblyFromCs0012(diagnostics);
            if (!string.IsNullOrEmpty(asm)) return asm;
            asm = MissingReferenceParser.AssemblyFromCs1069(diagnostics);
            if (!string.IsNullOrEmpty(asm)) return asm;

            string ns = MissingReferenceParser.SymbolFromCs0234(diagnostics);
            if (!string.IsNullOrEmpty(ns)) return ns;

            string sym = MissingReferenceParser.SymbolFromCs0246(diagnostics);
            if (!string.IsNullOrEmpty(sym)) return sym;
            return MissingReferenceParser.SymbolFromCs0103(diagnostics);
        }

        /// <summary>
        /// <b>按名优先</b>挑候选文件：取 <paramref name="demanded"/> 的首段（<c>A.B.C</c> ⇒ <c>A</c>），
        /// 在候选里找**文件名 stem 完全相等**（忽略大小写）的那一个并返回其路径；找不到 ⇒ <b>null</b>。
        /// mcs 的引用一旦加入**不可撤回**（无 RemoveReference）⇒ 这里只给高置信候选，
        /// 绝不退化成"随便给一个"。
        /// 同简单名多份时它取**池内首个**（15a 起的原语义）；生产自愈走带 <see cref="SameStemPreference"/>
        /// 的重载（任务 15f：多份按编译器策略取舍 + 披露）。
        /// </summary>
        public static string PickCandidateByName(string demanded, IList<string> candidatePaths)
        {
            IList<string> hits = FindCandidatesByName(demanded, candidatePaths);
            return hits.Count == 0 ? null : hits[0];
        }

        /// <summary>demanded 的**首段**（`A.B.C` ⇒ `A`）；空/无首段 ⇒ null。与按名通道的判据同源。</summary>
        public static string HeadOf(string demanded)
        {
            if (string.IsNullOrEmpty(demanded)) return null;
            int dot = demanded.IndexOf('.');
            string head = dot < 0 ? demanded : demanded.Substring(0, dot);
            head = head.Trim();
            return head.Length == 0 ? null : head;
        }

        /// <summary>
        /// 任务 15f：**全部**同 stem 候选（按池序）——"同简单名多份"的枚举来源
        /// （策略挑选与披露的"未采用 N 份"都基于它）。
        /// </summary>
        public static IList<string> FindCandidatesByName(string demanded, IList<string> candidatePaths)
        {
            var hits = new List<string>();
            if (candidatePaths == null) return hits;
            string head = HeadOf(demanded);
            if (head == null) return hits;

            for (int i = 0; i < candidatePaths.Count; i++)
            {
                string path = candidatePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(stem)) continue;
                if (stem.Equals(head, StringComparison.OrdinalIgnoreCase)) hits.Add(path);
            }
            return hits;
        }

        /// <summary>
        /// 任务 15f：同简单名**多份**时按 **mcs 编译器策略**取舍（mcs 路 = <see cref="SameStemPreference.Oldest"/>
        /// 选**较旧**，与已加载件两条通道同口径）——挑选**复用** <see cref="SameStemChoice.Pick"/>，
        /// 本文件不再写第二份实现；单份、以及同身份副本 ⇒ 结论与"池内首个同 stem"逐字一致
        /// （不引入任何新决定）。
        /// </summary>
        public static string PickCandidateByName(string demanded, IList<string> candidatePaths, SameStemPreference pref)
        {
            IList<string> hits = FindCandidatesByName(demanded, candidatePaths);
            if (hits.Count == 0) return null;
            if (hits.Count == 1) return hits[0];
            SameStemCandidate best = SameStemChoice.Pick(SameStemChoice.FromPaths(hits), pref);
            return best == null ? hits[0] : best.Path;
        }

        /// <summary>
        /// 任务 15f：**文件候选通道的采用披露**（与已加载件两条通道**同款文案**，只是"原因="=按编译器策略：mcs 选较旧）。
        /// 返回 **null** = 没有"同简单名且身份不同"的另一份（单份／同身份副本 ⇒ 常态、零噪声，不写日志）。
        /// </summary>
        public static string DescribeCandidateChoice(string chosenPath, string demanded,
            IList<string> candidatePaths, SameStemPreference pref, string channelLabel)
        {
            IList<string> hits = FindCandidatesByName(demanded, candidatePaths);
            if (hits.Count == 0 && !string.IsNullOrEmpty(chosenPath))
            {
                hits = new List<string>(1);
                hits.Add(chosenPath);
            }
            return SameStemChoice.Describe(chosenPath, SameStemChoice.FromPaths(hits), pref, channelLabel);
        }

        /// <summary>与 Roslyn 侧（RoslynCompiler/ReplSession）**逐字同款**的到顶文案。</summary>
        public static string CapMessage(int healed, string diagnostics)
        {
            return "引用自愈到顶：已补 " + healed + " 个引用仍未通过（上限 5 轮），剩余诊断："
                + Environment.NewLine + diagnostics;
        }

        /// <summary>mcs 路径的自愈日志行：点名"加入后不可撤回"（mcs 无 RemoveReference）。</summary>
        public static string HealLogLine(string path)
        {
            return "自愈补引用：" + System.IO.Path.GetFileName(path) + "（mcs 路径，加入后不可撤回）";
        }
    }
}

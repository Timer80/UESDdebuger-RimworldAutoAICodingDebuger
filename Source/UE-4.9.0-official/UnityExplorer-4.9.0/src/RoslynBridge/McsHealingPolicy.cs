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
        /// </summary>
        public static string PickCandidateByName(string demanded, IList<string> candidatePaths)
        {
            if (string.IsNullOrEmpty(demanded) || candidatePaths == null) return null;

            int dot = demanded.IndexOf('.');
            string head = dot < 0 ? demanded : demanded.Substring(0, dot);
            head = head.Trim();
            if (head.Length == 0) return null;

            for (int i = 0; i < candidatePaths.Count; i++)
            {
                string path = candidatePaths[i];
                if (string.IsNullOrEmpty(path)) continue;
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(stem)) continue;
                if (stem.Equals(head, StringComparison.OrdinalIgnoreCase)) return path;
            }
            return null;
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

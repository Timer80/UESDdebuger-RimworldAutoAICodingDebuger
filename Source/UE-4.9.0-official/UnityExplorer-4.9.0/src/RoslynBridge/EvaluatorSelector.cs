using System;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// "为什么没走 Roslyn"的四态（任务 14 / F1）。前三条是要求**可分辨**的三种回退原因：
    /// 配置关闭 / 前置包缺席或加载失败 / 探针失败；第四条是"本配置根本没编 Roslyn 实现"（ML/BIE/CPP）。
    /// </summary>
    public enum McsFallbackReason
    {
        /// <summary>`C# Console Use Roslyn`=false（玩家显式关闭；语义 = 不用 Roslyn，非"降级"）。</summary>
        ConfigDisabled,
        /// <summary>前置包 `UE.Roslyn` 未安装/未启用/加载失败 ⇒ 自动降级 mcs。</summary>
        FrontPackageUnavailable,
        /// <summary>前置包供上了载荷但探针（`0 == 0`）编译失败 ⇒ 自动降级 mcs。</summary>
        ProbeFailed,
        /// <summary>本配置未编入 Roslyn 实现（`RoslynCompiledIn`=false），与前置包无关。</summary>
        NotCompiledIn,
    }

    /// <summary>
    /// 求值器选择结果（追加 E）：三类终态，与 ConsoleController 的实际分支一一对应。
    /// </summary>
    public enum EvaluatorChoice
    {
        /// <summary>Roslyn 路径（实际生效者必须是 RoslynScriptEvaluator）。</summary>
        Roslyn,
        /// <summary>mcs 回退路径（ScriptEvaluator）。</summary>
        Mcs,
        /// <summary>两条路都不可用 → 控制台整体禁用。</summary>
        Disable,
    }

    /// <summary>
    /// "选哪条求值器"的**纯决策逻辑**（追加 E，2026-10-09 根因清扫轮）。
    /// 动机：修复"Roslyn 就绪但实际编译不了"（B1）与"就绪→探针失败→回退 mcs→再次 Reset 又新建 Roslyn
    /// 且不再探针"（追加 B）时，发现**"当前生效的是哪条求值器"此前没有非游戏内证据**——只能进游戏看日志，
    /// 导致整轮归因绕大圈。抽出纯函数后：真值表可离线断言（见 RoslynBridgeTests.TestEvaluatorSelection），
    /// `ConsoleController` 只负责把三个输入喂进来。
    /// ⚠️ 不依赖 Unity/UE/Roslyn 与 mcs 的任何类型（本文件进全部 9 配置编译），故可离线单测。
    /// </summary>
    public static class EvaluatorSelector
    {
        /// <summary>
        /// 本配置是否编入了 Roslyn 实现（= csproj 是否为 STANDALONE_Mono，即 Roslyn 四件在 glob 内）。
        /// ⚠️ 用 `STANDALONE_MONO`（SDK 按 Configuration 隐式注入）而非 `STANDALONE`：STANDALONE_Cpp
        /// 同样定义 STANDALONE 但不编 Roslyn 四件（会 CS0246）。此处是**唯一**的配置判定点，
        /// 其余代码用局部变量接收（避免恒假 const 条件触发 CS0162）。
        /// </summary>
#if STANDALONE_MONO
        public const bool RoslynCompiledIn = true;
#else
        public const bool RoslynCompiledIn = false;
#endif

        /// <summary>
        /// 首次构造前的选择（真值表）：
        ///   roslynCompiled=false（本配置没编 Roslyn 四件，如 ML/BIE/CPP）→ 恒 mcs；
        ///   wantRoslyn=false（配置关闭 或 forceMcs 强制回退）        → 恒 mcs；
        ///   payloadLoaded=false（载荷缺失/门闩失败）                 → 恒 mcs（且调用方应记 FailureReason）；
        ///   三者皆真 → Roslyn（随后**必须**探针，见 <see cref="ChooseAfterProbe"/>）。
        /// </summary>
        public static EvaluatorChoice ChooseInitial(bool roslynCompiled, bool wantRoslyn, bool payloadLoaded)
        {
            if (!roslynCompiled) return EvaluatorChoice.Mcs;
            if (!wantRoslyn) return EvaluatorChoice.Mcs;
            if (!payloadLoaded) return EvaluatorChoice.Mcs;
            return EvaluatorChoice.Roslyn;
        }

        /// <summary>
        /// 探针（一次平凡编译 `0 == 0`）后的选择：
        ///   初始 Roslyn + 探针通过 → Roslyn（保留）；
        ///   初始 Roslyn + 探针失败 → mcs（**回退**，调用方须重建求值器并再探针一次）；
        ///   初始 mcs + 探针通过    → mcs；
        ///   初始 mcs + 探针失败    → Disable（mcs 都编不了 ⇒ 无路可退）。
        /// </summary>
        public static EvaluatorChoice ChooseAfterProbe(EvaluatorChoice initial, bool probeOk)
        {
            if (initial == EvaluatorChoice.Roslyn)
                return probeOk ? EvaluatorChoice.Roslyn : EvaluatorChoice.Mcs;
            return probeOk ? EvaluatorChoice.Mcs : EvaluatorChoice.Disable;
        }

        /// <summary>`UsingRoslyn` 的**唯一**取值口径：true ⟺ 终态是 Roslyn（即实际生效 `RoslynScriptEvaluator`）。
        /// 追加 C：此前该标志由分支内赋值，"日志/属性说 Roslyn 而实际不是"没有反证；抽成函数后天然成立，
        /// 调用方另有一次运行时交叉核对（类型不符即告警）。</summary>
        public static bool UsingRoslyn(EvaluatorChoice finalChoice)
            => finalChoice == EvaluatorChoice.Roslyn;

        /// <summary>诊断用的中文名（日志锚点 "求值器生效：…" 用）。</summary>
        public static string Describe(EvaluatorChoice choice)
        {
            switch (choice)
            {
                case EvaluatorChoice.Roslyn: return "Roslyn";
                case EvaluatorChoice.Mcs: return "mcs";
                default: return "禁用";
            }
        }

        /// <summary>
        /// 回退原因的**日志文本**（任务 14 / F1：禁静默、且三态可分辨）。
        /// 纯函数 ⇒ 离线可断言"三种原因给出三条互不相同、各自点明原因的文本"
        ///（见 RoslynBridgeTests.TestMcsFallbackReasonTexts）；ConsoleController 只负责喂 reason+detail，
        /// 不得自行拼串（否则又变成"只把日志字样改对、逻辑没动"）。
        /// ⚠️ 前置包缺席/探针失败都是**自动降级**（控制台仍可用），文本必须让玩家能直接分辨是哪一种。
        /// </summary>
        public static string DescribeMcsFallback(McsFallbackReason reason, string detail)
        {
            switch (reason)
            {
                case McsFallbackReason.ConfigDisabled:
                    return "[CSConsole] 使用 mcs 求值器（配置关闭 Roslyn：`C# Console Use Roslyn`=false）";
                case McsFallbackReason.FrontPackageUnavailable:
                    return "[CSConsole] 回退 mcs：前置包 " + RoslynFrontGate.FrontPackageId
                        + " 未安装或加载失败"
                        + (string.IsNullOrEmpty(detail) ? "（未给出原因）" : " → " + detail);
                case McsFallbackReason.ProbeFailed:
                    return "[CSConsole] Roslyn 探针失败，回退 mcs："
                        + (string.IsNullOrEmpty(detail) ? "evaluator probe failed" : detail);
                default:
                    return "[CSConsole] 使用 mcs 求值器（本配置未编入 Roslyn 实现）";
            }
        }
    }
}

using System;
using System.Text;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>C# 控制台编译器（**用户可见名 = 界面/日志里的叫法**：`Roslyn` / `Mono.CSharp`，勿写成 mcs）。</summary>
    public enum ConsoleCompiler
    {
        /// <summary>Roslyn（C# 13；载荷由前置包 UE.Roslyn 供）。</summary>
        Roslyn = 0,
        /// <summary>Mono.CSharp（旧版兜底；无 C# 13 语法）。</summary>
        MonoCSharp = 1,
    }

    /// <summary>
    /// 启动编译器决策的**纯数据**结果：本类只算"该用哪个 / 设置项该落什么值 / 隐藏位该落什么值 + 一句日志"，
    /// 不做任何 I/O、不碰设置与日志接口（调用方 UELoader 负责落盘与打日志）。
    /// 字符串字段是普通 string（C.5：载荷类型 `Microsoft.CodeAnalysis*` 绝不进任何字段）。
    /// </summary>
    public struct StartupCompilerDecision
    {
        /// <summary>本次启动实际要用的编译器（已把 start_game 覆盖算进去）。</summary>
        public ConsoleCompiler Effective;
        /// <summary>落回设置项（`useRoslyn`）的值：只有"首次带前置 ⇒ 默认 Roslyn"会改它。</summary>
        public bool PreferRoslynSetting;
        /// <summary>落回隐藏位的值：前置包缺席 ⇒ 清空，使"下次第一次带前置"能重新默认 Roslyn。</summary>
        public bool DefaultApplied;
        /// <summary>日志文本（**恒非空**，禁静默：玩家/代理必须能从日志看出这次为什么用了这个编译器）。</summary>
        public string Note;
    }

    /// <summary>
    /// "启动时用哪个 C# 控制台编译器"的**纯决策逻辑**（零 Unity/UE/Verse/mcs/Roslyn 依赖 ⇒ 进全部 9 配置 + 可离线断言）。
    ///
    /// 输入（全部由调用方喂入，本类不自己去查）：
    ///   · <c>launchOverride</c>：`start_game(csharpConsole: …)` 写的一次性标记解析结果（null = 没指定）；
    ///   · <c>frontPresent</c>：前置包 UE.Roslyn 是否在场（调用方查 RimWorld 模组表）；
    ///   · <c>settingPrefersRoslyn</c>：模组设置里用户的选择；
    ///   · <c>defaultApplied</c>：隐藏位"已经因为检测到前置而默认设成 Roslyn 过一次"。
    ///
    /// 三条规则（人类裁定，2026-10-11）：
    ///   ① **没有前置 ⇒ 用 Mono.CSharp**（界面上不给选择按钮；设置项原值保留，前置回来时按它走）；
    ///   ② **第一次带前置加载 ⇒ 默认设为 Roslyn**（并把隐藏位置真）；其他时候**按设置走**；
    ///   ③ **前置卸载 ⇒ 清空隐藏位**（这样"重新装上前置"仍算"第一次带前置"，会再次默认 Roslyn）。
    ///   ④ `start_game` 的显式参数**只覆盖本次启动**，不改设置项、不改隐藏位。
    /// </summary>
    public static class StartupCompilerPreference
    {
        /// <summary>Roslyn 的显示名（界面/日志/`start_game` 参数值一律用它）。</summary>
        public const string RoslynName = "Roslyn";

        /// <summary>Mono.CSharp 的显示名（界面/日志/`start_game` 参数值一律用它；**不写 mcs**）。</summary>
        public const string MonoCSharpName = "Mono.CSharp";

        /// <summary>`start_game` 覆写标记文件名（写在游戏侧 `<modRoot>/MCP/` 下）。</summary>
        public const string LaunchOverrideFileName = ".launch-compiler";

        /// <summary>编译器显示名。</summary>
        public static string Describe(ConsoleCompiler compiler)
            => compiler == ConsoleCompiler.Roslyn ? RoslynName : MonoCSharpName;

        /// <summary>
        /// `packageId` 是否就是前置包（**大小写不敏感**）。
        ///
        /// 判中范围 = <see cref="RoslynFrontGate.FrontPackageId"/> + 其别名表
        /// （<see cref="RoslynFrontGate.FrontPackageIdAliases"/>，当前含工坊版 `ue.roslyn_steam`）。
        /// 大小写不敏感；额外去首尾空白与 BOM（与 <see cref="ParseLaunchOverride"/> 同口径，
        /// 防手抄 / 编辑器带入不可见字符）。空/空白 ⇒ false。
        /// </summary>
        public static bool IsFrontPackageId(string packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            string id = packageId.Trim().Trim('\uFEFF').Trim();
            if (string.Equals(id, RoslynFrontGate.FrontPackageId, StringComparison.OrdinalIgnoreCase)) return true;
            string[] aliases = RoslynFrontGate.FrontPackageIdAliases;
            if (aliases != null)
            {
                for (int i = 0; i < aliases.Length; i++)
                {
                    if (string.Equals(id, aliases[i], StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 解析覆写标记文本（`start_game` 写入、游戏启动时读）。可识别 `Roslyn` / `Mono.CSharp` 大小写变体，
        /// 并**宽容接受**别名 `mcs` / `monocsharp` / `mono.csharp`（对手写与旧习惯），
        /// 无法识别返回 **null**（调用方必须响亮报错，不得静默当成"没指定"）。
        /// </summary>
        public static ConsoleCompiler? ParseLaunchOverride(string text)
        {
            if (text == null) return null;
            string t = text.Trim().Trim('\uFEFF').Trim();
            if (t.Length == 0) return null;
            if (Eq(t, RoslynName)) return ConsoleCompiler.Roslyn;
            if (Eq(t, MonoCSharpName) || Eq(t, "mcs") || Eq(t, "monocsharp")) return ConsoleCompiler.MonoCSharp;
            return null;
        }

        /// <summary>见类型注释的四条规则。返回的 <c>Note</c> 恒非空。</summary>
        public static StartupCompilerDecision Resolve(ConsoleCompiler? launchOverride, bool frontPresent,
            bool settingPrefersRoslyn, bool defaultApplied)
        {
            var d = new StartupCompilerDecision();
            d.PreferRoslynSetting = settingPrefersRoslyn;
            d.DefaultApplied = defaultApplied;
            var sb = new StringBuilder();

            if (!frontPresent)
            {
                // ① + ③：没有前置 ⇒ 用 Mono.CSharp；隐藏位清空（设置项**不动**）
                bool cleared = defaultApplied;
                d.DefaultApplied = false;
                d.Effective = ConsoleCompiler.MonoCSharp;
                sb.Append("[UELoader] 未检测到前置包 ").Append(RoslynFrontGate.FrontPackageId)
                  .Append(" ⇒ 本次 C# 控制台使用 ").Append(MonoCSharpName).Append("（界面不提供选择）");
                if (cleared)
                    sb.Append("；已清空\"").Append(RoslynName).Append("\"默认标记（重新装上前置后仍按\"第一次带前置\"处理）");
                if (launchOverride.HasValue)
                {
                    if (launchOverride.Value == ConsoleCompiler.Roslyn)
                        sb.Append("；start_game 指定的 ").Append(RoslynName)
                          .Append(" 无载荷可用，将由 C# 控制台自动降级并记录原因");
                    else
                        sb.Append("；与 start_game 指定（").Append(MonoCSharpName).Append("）一致");
                }
                sb.Append("。设置项保留原值=").Append(settingPrefersRoslyn ? RoslynName : MonoCSharpName)
                  .Append("，前置包可用时按它走。");
                d.Note = sb.ToString();
                return d;
            }

            // ②：第一次带前置 ⇒ 设置项默认 Roslyn；之后按设置走
            bool firstTimeWithFront = !defaultApplied;
            if (firstTimeWithFront)
            {
                d.PreferRoslynSetting = true;
                d.DefaultApplied = true;
                sb.Append("[UELoader] 首次检测到前置包 ").Append(RoslynFrontGate.FrontPackageId)
                  .Append(" ⇒ C# 控制台默认已设为 ").Append(RoslynName).Append("（之后按设置走）");
            }
            else
            {
                sb.Append("[UELoader] C# 控制台按设置使用 ")
                  .Append(settingPrefersRoslyn ? RoslynName : MonoCSharpName);
            }

            d.Effective = d.PreferRoslynSetting ? ConsoleCompiler.Roslyn : ConsoleCompiler.MonoCSharp;

            // ④：start_game 显式参数只覆盖本次
            if (launchOverride.HasValue)
            {
                ConsoleCompiler ov = launchOverride.Value;
                if (ov != d.Effective)
                {
                    sb.Append("；start_game 参数覆盖为 ").Append(Describe(ov)).Append("（仅本次启动，不改设置）");
                    d.Effective = ov;
                }
                else
                {
                    sb.Append("；start_game 参数与之一致（").Append(Describe(ov)).Append("）");
                }
            }
            d.Note = sb.ToString();
            return d;
        }

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

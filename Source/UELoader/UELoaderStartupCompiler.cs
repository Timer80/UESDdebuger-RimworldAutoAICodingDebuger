using System;
using System.Collections.Generic;
using System.IO;
using UnityExplorer.RoslynBridge;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 启动编译器偏好的**宿主侧（UELoader）**：前置包判定 + `start_game` 覆写标记的读写 +
    /// 把纯决策（<see cref="StartupCompilerPreference.Resolve"/>）落回模组设置并写日志。
    ///
    /// 设计要点：
    ///   · 只在**模组构造期**跑一次（早于 UE 初始化 ⇒ 时序天然安全，也早于控制台首次 Reset）；
    ///   · 前置包判定走 RimWorld 自己的模组表（<c>ModsConfig.ActiveModsInLoadOrder</c>），**不依赖 UE**：
    ///     没装/没启用前置包时也要能给出正确界面（"只显示 Mono.CSharp、不给选择按钮"）与正确决策；
    ///   · `start_game` 覆写标记**读到即消费（删除）**⇒ 只影响紧接着的这一次启动，不粘住；
    ///   · 一切失败都**响亮**记日志并退回安全侧（无前置 / 按设置），绝不静默。
    ///
    /// 三类输入与优先级（人类裁定 2026-10-11）：`start_game 参数` &gt; `模组设置` &gt; `UE 面板配置项(运行期后门)`。
    /// </summary>
    public static class UELoaderStartupCompiler
    {
        /// <summary>前置包（UE.Roslyn）是否在场：按 packageId **大小写不敏感**匹配 RimWorld 模组表。</summary>
        public static bool IsRoslynFrontPresent()
        {
            try
            {
                // RimWorld 1.6：`ModsConfig.ActiveModsInLoadOrder` 是 IEnumerable<ModMetaData>（不是 List）
                IEnumerable<ModMetaData> active = ModsConfig.ActiveModsInLoadOrder;
                if (active == null) return false;
                foreach (ModMetaData mod in active)
                {
                    if (mod == null) continue;
                    if (StartupCompilerPreference.IsFrontPackageId(mod.PackageId)) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning("[UELoader] 前置包判定失败（按『无前置』处理）："
                    + ex.GetType().Name + "：" + ex.Message);
                return false;
            }
        }

        /// <summary>覆写标记的完整路径（`&lt;modRoot&gt;/MCP/.launch-compiler`）。</summary>
        public static string LaunchOverridePath(string modRoot)
            => string.IsNullOrEmpty(modRoot)
                ? null
                : Path.Combine(Path.Combine(modRoot, "MCP"), StartupCompilerPreference.LaunchOverrideFileName);

        /// <summary>
        /// 读并**消费**（删除）`start_game(csharpConsole: …)` 写下的标记。
        /// 返回 null = 没指定（或读取失败，会响亮记日志）；内容无法识别 ⇒ 同样返回 null 但**响亮登记裸文本**
        /// （绝不静默当成"没指定"）。无论内容是否合法都删文件 —— 否则一个坏标记会毒化此后每一次启动。
        /// </summary>
        public static ConsoleCompiler? ReadAndConsumeLaunchOverride(string modRoot)
        {
            string path = LaunchOverridePath(modRoot);
            if (path == null || !File.Exists(path)) return null;

            string text = null;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                Log.Warning("[UELoader] 读取 start_game 覆写标记失败（按『没指定』处理）："
                    + ex.GetType().Name + "：" + ex.Message);
                return null;
            }

            try { File.Delete(path); }
            catch (Exception ex)
            {
                Log.Warning("[UELoader] 删除 start_game 覆写标记失败（本次仍按其内容走，下次启动会再读一次）："
                    + ex.GetType().Name + "：" + ex.Message);
            }

            ConsoleCompiler? parsed = StartupCompilerPreference.ParseLaunchOverride(text);
            if (!parsed.HasValue)
                Log.Warning("[UELoader] start_game 覆写标记内容无法识别（已忽略，按设置走）：『"
                    + (text ?? "<null>").Trim() + "』；合法值 = "
                    + StartupCompilerPreference.RoslynName + " / " + StartupCompilerPreference.MonoCSharpName);
            return parsed;
        }

        /// <summary>
        /// 跑一次启动决策：算 + 落回模组设置（含隐藏位）+ 产出日志文本。
        /// 返回**本次启动是否优先 Roslyn**（供注入 UE 用；UE 侧仍会按自身真值表与探针决定最终生效者）。
        /// </summary>
        public static bool Apply(UELoaderSettings settings, string modRoot, out string note)
        {
            note = null;
            if (settings == null) return true;   // 设置缺失：不折腾，交给 UE 配置项原值

            bool frontPresent = IsRoslynFrontPresent();
            ConsoleCompiler? launchOverride = ReadAndConsumeLaunchOverride(modRoot);

            StartupCompilerDecision decision = StartupCompilerPreference.Resolve(
                launchOverride, frontPresent, settings.useRoslyn, settings.roslynDefaultApplied);

            // 落回设置（含"首次带前置 ⇒ 默认 Roslyn""前置卸载 ⇒ 清隐藏位"两处副作用）
            settings.useRoslyn = decision.PreferRoslynSetting;
            settings.roslynDefaultApplied = decision.DefaultApplied;

            note = decision.Note;
            return decision.Effective == ConsoleCompiler.Roslyn;
        }
    }
}

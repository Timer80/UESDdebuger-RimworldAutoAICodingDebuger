using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityExplorer.RoslynBridge;
using Verse;

namespace UELoader
{
    /// <summary>
    /// RimWorld 模组入口。
    /// 延迟到第一个场景（主菜单）加载完成后再初始化 UnityExplorer —— 与 AKUL 加载器相同的稳妥做法：
    /// Mod 构造时场景尚未就绪，过早创建 UI/GameObject 可能失败。
    /// </summary>
    public class UELoaderMod : Mod
    {
        public static UELoaderMod Instance { get; private set; }

        /// <summary>模组设置（双通道：RimWorld 原生 ModSettings + 项目 config.json/toolConfig.json）。</summary>
        public static UELoaderSettings Settings { get; private set; }

        /// <summary>
        /// 本次启动**实际生效**的控制台偏好（= <see cref="UELoaderStartupCompiler.Apply"/> 的决策结果，
        /// 已把 `start_game` 覆盖算进去）。⚠️ 与 <see cref="UELoaderSettings.useRoslyn"/> **不是一回事**：
        /// 后者是"按设置走"的值，前者是"这次到底用哪个"。
        /// 注入给 UE 的必须是**前者** —— 曾经误注入后者，导致 `start_game(csharpConsole:"Mono.CSharp")`
        /// 只在日志里生效、UE 仍走 Roslyn（2026-10-11 上机验收抓到）。
        /// </summary>
        public static bool StartupPreferRoslyn { get; private set; } = true;

        private bool initialized;

        public UELoaderMod(ModContentPack content) : base(content)
        {
            Instance = this;

            Log.Message("[UELoader] UESDdebuger UnityExplorer loader mod constructed.");
            Log.Message($"[UELoader] Mod root: {Content.RootDir}");

            // 模组设置：原生通道（RimWorld 自动持久化到 Config 文件夹）+ 项目配置通道
            // （从 MCP/config.json / toolConfig.json 载入，保证界面显示项目真实配置）。
            try
            {
                Settings = GetSettings<UELoaderSettings>();
                Settings.LoadFromProjectConfig(Content.RootDir);
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] 模组设置初始化失败: {ex.GetType().Name}: {ex.Message}");
            }

            // C# 控制台启动编译器：前置包判定 + start_game 覆写标记消费 + 决策落回设置。
            // 必须在这里（Mod 构造期）跑 —— 早于 UE 初始化 ⇒ 早于控制台首次 Reset，时序天然安全。
            try
            {
                StartupPreferRoslyn = UELoaderStartupCompiler.Apply(Settings, Content.RootDir, out string compilerNote);
                if (!string.IsNullOrEmpty(compilerNote))
                    Log.Message(compilerNote);
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] C# 控制台启动决策失败（按 UE 配置项原值走）: {ex.GetType().Name}: {ex.Message}");
            }

            // 游戏内 UE HTTP 服务（动态端口，从 3001 起递增探测空闲端口）在 Mod 构造时提前启动：
            // RimWorld 初始主菜单场景在 Mod 构造前已加载，SceneManager.sceneLoaded
            // 无法覆盖主菜单（只在进世界时触发）；而 /trigger-quicktest 与
            // /unityexplorer/status 需要主菜单即可用，因此 HTTP 服务独立于 UE 初始化提前启动。
            ExplorerBootstrap.StartHttpServer(Content.RootDir);

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (initialized)
                return;

            initialized = true;
            ExplorerBootstrap.Initialize(Content.RootDir);

            // 进场景后刷新端口文件：此时 Unity 调试代理端口已打印到 Player.log，
            // 补写 unityDebugPort（UE HTTP 启动时可能尚未出现）。失败不影响游戏。
            UEHttpServer.RefreshPortsFile();
        }

        // ---------------------------------------------------------------- 模组设置界面

        public override string SettingsCategory()
        {
            return "UESDdebuger.SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Settings == null)
            {
                try { Settings = GetSettings<UELoaderSettings>(); } catch { }
            }

            // 顶部留出标题区（Dialog_ModSettings 标题高约 35px，标题底距 inRect 顶部约 5px）
            float topPad = 26f;
            float toolsSectionHeight = Mathf.Min(inRect.height * 0.5f, 340f);
            Rect fieldsRect = new Rect(0f, topPad, inRect.width, inRect.height - toolsSectionHeight - topPad - 8f);
            var ls = new Listing_Standard();
            ls.Begin(fieldsRect);

            ls.Label("UESDdebuger.SettingsHeader".Translate());
            ls.GapLine(6f);

            Settings.gamePath = ls.TextEntryLabeled("gamePath", Settings.gamePath);
            Settings.logPath = ls.TextEntryLabeled("logPath", Settings.logPath);
            Settings.steamPath = ls.TextEntryLabeled("steamPath", Settings.steamPath);
            Settings.appId = ls.TextEntryLabeled("appId", Settings.appId);
            ls.TextFieldNumericLabeled("startTimeout(ms)", ref Settings.startTimeout, ref Settings.startTimeoutBuffer, 1000f, 3600000f);
            ls.GapLine(6f);

            DrawConsoleCompilerRow(ls);
            ls.GapLine(6f);

            if (ls.ButtonText("UESDdebuger.ButtonRedetectConfig".Translate()))
            {
                Settings.statusMsg = Settings.ApplyAutoDeploy(Content.RootDir);
            }

            // 再次弹配置公告窗：只展示各 IDE 的 MCP 配置代码，不修改任何配置（不写 config.json / marker）
            if (ls.ButtonText("UESDdebuger.ButtonShowAnnouncements".Translate()))
            {
                var data = AnnouncementDocs.ParseIdeConfigs();
                AnnouncementWindowQueue.Add(new Dialog_FirstRunSetup(data));
            }

            if (!string.IsNullOrEmpty(Settings.statusMsg))
            {
                ls.Label(Settings.statusMsg, 60f);
            }

            ls.End();

            // 工具开关滚动列表
            float toolsTop = fieldsRect.y + fieldsRect.height + 8f;
            float lineHeight = 24f;
            float descPad = 4f;
            float calcWidth = inRect.width - 46f;
            float itemHeight = lineHeight + descPad;
            float viewHeight = 4f;
            for (int i = 0; i < Settings.toolToggles.Count; i++)
            {
                ToolToggle t = Settings.toolToggles[i];
                string desc = UELoaderSettings.GetToolDescription(t.name);
                float descH = 0f;
                if (!string.IsNullOrEmpty(desc))
                {
                    Text.Font = GameFont.Tiny;
                    descH = Text.CalcHeight(desc, calcWidth) + descPad;
                }
                itemHeight = lineHeight + descH;
                viewHeight += itemHeight;
            }
            viewHeight += 4f;
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, viewHeight);
            Widgets.Label(new Rect(0f, toolsTop, inRect.width, 22f),
                "UESDdebuger.ToolSectionHeader".Translate());
            Rect listRect = new Rect(0f, toolsTop + 24f, inRect.width, inRect.height - toolsTop - 24f);
            Widgets.BeginScrollView(listRect, ref Settings.scrollPos, viewRect);
            float curY = 0f;
            for (int i = 0; i < Settings.toolToggles.Count; i++)
            {
                ToolToggle t = Settings.toolToggles[i];
                string desc = UELoaderSettings.GetToolDescription(t.name);
                Rect r = new Rect(0f, curY, viewRect.width, lineHeight);
                Widgets.CheckboxLabeled(r, t.name, ref t.enabled);
                if (!string.IsNullOrEmpty(desc))
                {
                    Text.Font = GameFont.Tiny;
                    float descH = Text.CalcHeight(desc, viewRect.width - 30f) + descPad;
                    GUI.color = new Color(0.75f, 0.75f, 0.75f);
                    Widgets.Label(new Rect(30f, curY + lineHeight, viewRect.width - 30f, descH),
                        desc);
                    GUI.color = Color.white;
                    curY += lineHeight + descH;
                }
                else
                {
                    curY += lineHeight + descPad;
                }
                Text.Font = GameFont.Small;
            }
            Widgets.EndScrollView();
        }

        /// <summary>
        /// C# 控制台编译器一行（人类裁定 2026-10-11）：
        ///   · **前置包 UE.Roslyn 不在场 ⇒ 只显示事实（Mono.CSharp），不给选择按钮**；
        ///   · 在场 ⇒ 勾选框 + 说明 + 一行"当前实际"（未初始化 / 一致 / 不一致都如实说）。
        /// 本项**只在启动时**读一次（求值器在 UE 初始化时选定）⇒ 改后必须重启游戏。
        /// </summary>
        private void DrawConsoleCompilerRow(Listing_Standard ls)
        {
            if (!UELoaderStartupCompiler.IsRoslynFrontPresent())
            {
                ls.Label("UESDdebuger.ConsoleCompilerNoFront".Translate(StartupCompilerPreference.MonoCSharpName), 40f);
                return;
            }

            ls.CheckboxLabeled("UESDdebuger.ConsoleCompilerLabel".Translate(), ref Settings.useRoslyn);
            ls.Label("UESDdebuger.ConsoleCompilerDesc".Translate(), 46f);

            if (!TryGetActualCompiler(out string actual))
            {
                ls.Label("UESDdebuger.ConsoleCompilerActualNotInit".Translate(), 28f);
                return;
            }
            string pref = Settings.useRoslyn
                ? StartupCompilerPreference.RoslynName
                : StartupCompilerPreference.MonoCSharpName;
            ls.Label(actual == pref
                ? "UESDdebuger.ConsoleCompilerActualIs".Translate(actual)
                : "UESDdebuger.ConsoleCompilerMismatchHint".Translate(actual), 40f);
        }

        /// <summary>UE 侧"当前实际生效"的编译器名；返回 false = 控制台尚未初始化（还没进过地图）。</summary>
        private static bool TryGetActualCompiler(out string name)
        {
            name = null;
            try
            {
                if (UnityExplorer.CSConsole.ConsoleController.Evaluator == null) return false;
                name = UnityExplorer.CSConsole.ConsoleController.UsingRoslyn
                    ? StartupCompilerPreference.RoslynName
                    : StartupCompilerPreference.MonoCSharpName;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] 读取 C# 控制台实际编译器失败: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 注入 UE 的**一次性**启动偏好（方法组注入 ⇒ C.5：不创建闭包、不留编译器缓存的委托字段）。
        /// 值是 Mod 构造期算好的**实际生效**结果（<see cref="StartupPreferRoslyn"/>，含 start_game 覆盖），
        /// 不是设置项原值 —— 设置项只是"按设置走"那一支的输入。
        /// </summary>
        public static bool ResolveStartupPreference() => StartupPreferRoslyn;

        public override void WriteSettings()
        {
            base.WriteSettings();
            // 双通道：原生 WriteSettings 已由 base 处理；这里额外写回项目配置文件。
            if (Settings != null)
            {
                Settings.WriteProjectConfigs(Content.RootDir);
            }
        }
    }
}

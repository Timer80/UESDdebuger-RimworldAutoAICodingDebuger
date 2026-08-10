using System;
using UnityEngine;
using UnityEngine.SceneManagement;
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

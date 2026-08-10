using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 主菜单公告队列：由于 AutoDeploy 的 [StaticConstructorOnStartup] 在游戏加载阶段执行，
    /// 此时 Find.WindowStack 尚未就绪，不能直接 Add 弹窗。因此把待弹窗放入队列，
    /// 通过 Harmony 钩子 UIRoot.UIRootOnGUI（主菜单每帧 GUI）消费 —— 与官方
    /// DelayedErrorWindowRequest 同款时机（进入主菜单后才会弹出）。
    /// </summary>
    public static class AnnouncementWindowQueue
    {
        private static readonly List<Window> pending = new List<Window>();
        private static bool patchInstalled;

        /// <summary>登记待弹窗口（主菜单就绪后自动弹出，按加入顺序）。</summary>
        public static void Add(Window window)
        {
            EnsureHookInstalled();
            lock (pending)
            {
                pending.Add(window);
            }
        }

        private static void EnsureHookInstalled()
        {
            if (patchInstalled)
                return;
            patchInstalled = true;
            try
            {
                var harmony = new Harmony("UESDdebuger.debug.announcement");
                var original = AccessTools.Method(typeof(UIRoot), "UIRootOnGUI");
                if (original == null)
                {
                    UEHttpLog.Warning("[AnnouncementQueue] 未找到 UIRoot.UIRootOnGUI，公告无法自动弹出");
                    return;
                }
                var prefix = typeof(AnnouncementWindowQueue).GetMethod("UIRootOnGUI_Prefix",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                harmony.Patch(original, new HarmonyMethod(prefix));
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[AnnouncementQueue] 安装公告弹窗钩子失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        [HarmonyPrefix]
        private static void UIRootOnGUI_Prefix()
        {
            List<Window> toShow = null;
            lock (pending)
            {
                if (pending.Count > 0)
                {
                    toShow = new List<Window>(pending);
                    pending.Clear();
                }
            }
            if (toShow == null)
                return;
            foreach (Window w in toShow)
            {
                try
                {
                    Find.WindowStack.Add(w);
                }
                catch (Exception ex)
                {
                    UEHttpLog.Warning($"[AnnouncementQueue] 弹出公告窗口失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Markdown 分段解析工具：读取外部 .md 中的 `##` / `###` 分段。
    /// - first-configs.md（中文）/ first-configs.en.md（英文副本，游戏语言非中文时读取）用 `## &lt;IDE名&gt;` 分段，
    ///   正文含 ```json 代码围栏（展示前剥离围栏行）；
    /// - announcements.md 用 `### &lt;版本号&gt;` 分段，正文为更新说明（保留原样展示）。
    /// </summary>
    public static class AnnouncementDocs
    {
        public struct Section
        {
            public string Name;
            public string Body;
        }

        /// <summary>first-configs.md 解析结果：头部说明 + IDE 段列表 + 尾部说明（均在弹窗中展示）。</summary>
        public class IdeConfigData
        {
            public string Header;   // 控件上方说明（## Header）
            public string Footer;   // 控件下方说明（## Footer）
            public List<Section> Ides = new List<Section>();
        }

        public static string FilePath { get; private set; }
        static string cnIdeConfigsPath;
        static string enIdeConfigsPath;
        static string announcementsPath;
        static string rootDir;

        public static void SetRootDir(string dir)
        {
            rootDir = dir;
            string mcpDir = Path.Combine(dir, "MCP");
            cnIdeConfigsPath = Path.Combine(mcpDir, "first-configs.md");
            enIdeConfigsPath = Path.Combine(mcpDir, "first-configs.en.md");
            announcementsPath = Path.Combine(mcpDir, "announcements.md");
            FilePath = announcementsPath;
        }

        /// <summary>按当前游戏语言选择 first-configs 文件：非中文时用英文副本 first-configs.en.md，
        /// 中文时用 first-configs.md；所选文件缺失时回退到另一个。
        /// 注：中文语言目录名是 "ChineseSimplified (简体中文)"，用前缀匹配。</summary>
        static string ResolveIdeConfigsPath()
        {
            LoadedLanguage lang = LanguageDatabase.activeLanguage;
            bool chinese = lang != null
                && lang.folderName.StartsWith("ChineseSimplified", StringComparison.Ordinal);
            string primary = chinese ? cnIdeConfigsPath : enIdeConfigsPath;
            string fallback = chinese ? enIdeConfigsPath : cnIdeConfigsPath;
            if (!string.IsNullOrEmpty(primary) && File.Exists(primary))
                return primary;
            return (!string.IsNullOrEmpty(fallback) && File.Exists(fallback)) ? fallback : primary;
        }

        /// <summary>解析 first-configs.md（中文）或 first-configs.en.md（非中文）：`## 名` 分段，剥离 ``` 代码围栏行。
        /// 特殊段「Header」「Footer」分别作为控件上方/下方文字，其余为 IDE 段。
        /// 文件内用 &lt;UESDdebuger&gt; 占位符（不写死某机路径）；展示时用模组真实根目录拼接替换，
        /// 弹窗即显示当前机器可直接复制的完整配置。</summary>
        public static IdeConfigData ParseIdeConfigs()
        {
            var data = new IdeConfigData();
            try
            {
                string ideConfigsPath = ResolveIdeConfigsPath();
                UEHttpLog.Message($"[AnnouncementDocs] 配置公告文件（语言="
                    + (LanguageDatabase.activeLanguage != null ? LanguageDatabase.activeLanguage.folderName : "null")
                    + "）: " + ideConfigsPath);
                if (string.IsNullOrEmpty(ideConfigsPath) || !File.Exists(ideConfigsPath))
                    return data;
                string text = File.ReadAllText(ideConfigsPath);
                foreach (Section s in ParseSections(text, "## "))
                {
                    var sec = s;
                    sec.Body = StripCodeFence(sec.Body);
                    sec.Body = ReplacePlaceholder(sec.Body);
                    if (sec.Name.Trim().Equals("Header", StringComparison.OrdinalIgnoreCase))
                    {
                        data.Header = sec.Body;
                    }
                    else if (sec.Name.Trim().Equals("Footer", StringComparison.OrdinalIgnoreCase))
                    {
                        data.Footer = sec.Body;
                    }
                    else
                    {
                        data.Ides.Add(sec);
                    }
                }
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[AnnouncementDocs] 读取 first-configs.md 失败：{ex.GetType().Name}: {ex.Message}");
            }
            return data;
        }

        /// <summary>把 &lt;UESDdebuger&gt; 占位符替换为模组真实根目录（正斜杠，取模组运行时位置拼接），未取到时保留占位符。</summary>
        static string ReplacePlaceholder(string body)
        {
            if (string.IsNullOrEmpty(body))
                return body;
            string real = string.IsNullOrEmpty(rootDir) ? null : rootDir.Replace('\\', '/');
            return real != null ? body.Replace("<UESDdebuger>", real) : body;
        }

        /// <summary>解析 announcements.md：`### 版本号` 分段（文件内从新到旧）。</summary>
        public static List<Section> ParseAnnouncements()
        {
            var list = new List<Section>();
            try
            {
                if (string.IsNullOrEmpty(announcementsPath) || !File.Exists(announcementsPath))
                    return list;
                string text = File.ReadAllText(announcementsPath);
                list = ParseSections(text, "### ");
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[AnnouncementDocs] 读取 announcements.md 失败：{ex.GetType().Name}: {ex.Message}");
            }
            return list;
        }

        /// <summary>按标记行分段（如 "## " / "### "），标题为标记后的文本，正文为段内其余行。</summary>
        static List<Section> ParseSections(string text, string marker)
        {
            var list = new List<Section>();
            if (string.IsNullOrEmpty(text))
                return list;

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            Section? current = null;
            var body = new List<string>();

            foreach (string rawLine in lines)
            {
                if (rawLine.TrimStart().StartsWith(marker, StringComparison.Ordinal))
                {
                    if (current.HasValue)
                    {
                        var sec = current.Value;
                        sec.Body = string.Join("\n", body).Trim('\n', ' ').Trim();
                        list.Add(sec);
                    }
                    current = new Section
                    {
                        Name = rawLine.Substring(rawLine.IndexOf(marker, StringComparison.Ordinal) + marker.Length).Trim()
                    };
                    body.Clear();
                }
                else if (current.HasValue)
                {
                    body.Add(rawLine);
                }
            }
            if (current.HasValue)
            {
                var sec = current.Value;
                sec.Body = string.Join("\n", body).Trim('\n', ' ').Trim();
                list.Add(sec);
            }
            return list;
        }

        /// <summary>剥离 Markdown 代码围栏行（``` 开头/结尾）。</summary>
        static string StripCodeFence(string body)
        {
            if (string.IsNullOrEmpty(body))
                return body;
            var sb = new StringBuilder(body.Length);
            foreach (string line in body.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    continue;
                sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }
    }

    /// <summary>
    /// 配置公告（首次启动）：复选框列出各 IDE，勾选后下方只读代码区显示对应 MCP 配置；
    /// 底部「复制到剪贴板」+「确定」。确定关闭后经 onClosed 回调（用于接着弹更新公告）。
    /// </summary>
    public class Dialog_FirstRunSetup : Window
    {
        public override Vector2 InitialSize => new Vector2(1024f, 720f);

        public Action onClosed;

        List<AnnouncementDocs.Section> ides;
        bool[] checkedIds;
        string[] codes;
        string headerText;
        string footerText;

        const float NewMargin = 16f;
        const float RowHeight = 32f;
        const float ButtonHeight = 35f;

        public Dialog_FirstRunSetup(AnnouncementDocs.IdeConfigData data)
        {
            data = data ?? new AnnouncementDocs.IdeConfigData();
            ides = data.Ides ?? new List<AnnouncementDocs.Section>();
            headerText = data.Header;
            footerText = data.Footer;
            checkedIds = new bool[ides.Count];
            codes = new string[ides.Count];
            for (int i = 0; i < ides.Count; i++)
                codes[i] = ides[i].Body;
            if (ides.Count > 0)
                checkedIds[0] = true;

            forcePause = true;
            absorbInputAroundWindow = true;
            doCloseX = true;
            onlyOneOfTypeAllowed = true;
        }

        Vector2 scrollPosition;

        public override void DoWindowContents(Rect inRect)
        {
            float x = inRect.x + NewMargin;
            float w = inRect.width - NewMargin * 2f;
            float y = inRect.y + NewMargin;

            // 标题（固定，可选复制）
            Text.Font = GameFont.Medium;
            Widgets.TextArea(new Rect(x, y, w, 34f), "UESDdebuger.DialogFirstRunTitle".Translate(), readOnly: true);
            y += 46f;
            Widgets.DrawLineHorizontal(x, y - 4f, w);
            y += 8f;
            Text.Font = GameFont.Small;

            // ---- 常量 ----
            float btnH = ButtonHeight;        // 底部「确定」行
            float copyBtnH = 30f;             // 复制按钮行
            float groupPad = 6f;              // 代码控件组内边距
            float labelH = 24f;               // 「MCP 配置代码：」标签
            float codeGroupH = 200f;          // 代码控件组固定高（TextArea + 复制按钮）

            // ---- 总滑动条：Header → 控件组 → 代码标签 → 代码控件组（复制按钮在组内） → Footer ----
            int cols = Mathf.Max(1, Mathf.FloorToInt(w / 170f));
            float cellW = w / cols;
            int rows = Mathf.CeilToInt((float)ides.Count / cols);
            float groupH = rows * RowHeight + 8f;

            float headerH = string.IsNullOrEmpty(headerText) ? 0f : Text.CalcHeight(headerText, w) + 12f;
            float footerH = string.IsNullOrEmpty(footerText) ? 0f : Text.CalcHeight(footerText, w) + 12f;
            float contentH = headerH + groupH + 12f + labelH + codeGroupH + footerH;

            Rect scrollOut = new Rect(x, y, w, inRect.yMax - y - btnH - NewMargin - 8f);
            Rect viewRect = new Rect(0f, 0f, w - 16f, contentH);
            Widgets.BeginScrollView(scrollOut, ref scrollPosition, viewRect);

            float curY = 0f;

            // Header（控件上方说明，可选复制）
            if (!string.IsNullOrEmpty(headerText))
            {
                Text.Font = GameFont.Small;
                Widgets.TextArea(new Rect(0f, curY, viewRect.width, headerH - 12f), headerText, readOnly: true);
                curY += headerH;
            }

            // 控件组（单选按钮，位于 Header 与 Footer 之间）
            Rect groupRect = new Rect(-6f, curY - 4f, viewRect.width + 12f, groupH + 4f);
            Widgets.DrawBoxSolid(new Rect(groupRect.x + 1f, groupRect.y + 1f, groupRect.width - 2f, groupRect.height - 2f), new Color(0f, 0f, 0f, 0.18f));
            for (int i = 0; i < ides.Count; i++)
            {
                int row = i / cols;
                int col = i % cols;
                // 每个按钮独立格子（含内边距 + 独立边框切割）
                Rect cell = new Rect(col * cellW + 2f, curY + row * RowHeight + 2f, cellW - 4f, RowHeight - 4f);
                Widgets.DrawBox(cell, 1, null);
                Rect r = new Rect(cell.x + 6f, cell.y, cell.width - 6f, cell.height);
                bool selected = checkedIds[i];
                if (Widgets.RadioButtonLabeled(r, ides[i].Name, selected))
                {
                    // 单选：清空其他，选中当前
                    for (int j = 0; j < checkedIds.Length; j++)
                        checkedIds[j] = false;
                    checkedIds[i] = true;
                }
            }
            curY += groupH;

            // 「MCP 配置代码：」标签（可选复制）
            curY += 12f;
            Text.Font = GameFont.Small;
            Widgets.TextArea(new Rect(0f, curY, viewRect.width, labelH), "UESDdebuger.DialogCodeLabel".Translate(), readOnly: true);
            curY += labelH;

            // 代码控件组（边框包裹 TextArea + 复制按钮，按钮作为控件一部分）
            string combined = BuildCombinedCode();
            Rect codeGroup = new Rect(-groupPad, curY, viewRect.width + groupPad * 2f, codeGroupH);
            Widgets.DrawBoxSolid(new Rect(codeGroup.x + 1f, codeGroup.y + 1f, codeGroup.width - 2f, codeGroup.height - 2f), new Color(0f, 0f, 0f, 0.18f));
            Widgets.DrawBox(codeGroup, 1, null);

            // TextArea（组内上方）
            Rect codeRect = new Rect(codeGroup.x + groupPad, codeGroup.y + groupPad,
                codeGroup.width - groupPad * 2f, codeGroup.height - groupPad * 2f - copyBtnH - 6f);
            Widgets.TextArea(codeRect, combined, readOnly: true);

            // 复制按钮（组内下方，代码控件的一部分）
            Rect copyRow = new Rect(codeGroup.x + groupPad, codeRect.yMax + 6f,
                codeGroup.width - groupPad * 2f, copyBtnH);
            float copyBtnW = Mathf.Max(240f, Text.CalcSize("UESDdebuger.DialogCopyButton".Translate()).x + 24f);
            if (Widgets.ButtonText(new Rect(copyRow.x, copyRow.y, copyBtnW, copyRow.height), "UESDdebuger.DialogCopyButton".Translate()))
            {
                GUIUtility.systemCopyBuffer = combined;
                Messages.Message("UESDdebuger.DialogCopied".Translate(), MessageTypeDefOf.PositiveEvent, historical: false);
            }
            curY += codeGroupH;

            // Footer（最底部说明，可选复制）
            if (!string.IsNullOrEmpty(footerText))
            {
                Text.Font = GameFont.Small;
                Widgets.TextArea(new Rect(0f, curY, viewRect.width, footerH - 12f), footerText, readOnly: true);
                curY += footerH;
            }

            Widgets.EndScrollView();

            // 底部「确定」按钮（固定）
            Rect btnRow = new Rect(x, inRect.yMax - btnH - NewMargin, w, btnH);
            if (Widgets.ButtonText(new Rect(btnRow.xMax - 140f, btnRow.y, 140f, btnRow.height), "UESDdebuger.DialogOk".Translate()))
            {
                Close();
                onClosed?.Invoke();
            }
        }

        string BuildCombinedCode()
        {
            // 单选：只显示当前选中的 IDE 配置
            for (int i = 0; i < ides.Count; i++)
            {
                if (!checkedIds[i])
                    continue;
                return codes[i];
            }
            return ides.Count > 0 ? codes[0] : "UESDdebuger.DialogNoIdeSelected".Translate();
        }
    }

    /// <summary>
    /// 更新公告：展示从 announcements.md 解析出的版本段落（从新到旧）。
    /// 由 AutoDeploy 过滤出「原版本 &lt; 段版本 &lt;= 现版本」的段落后传入。
    /// </summary>
    public class Dialog_Changelog : Window
    {
        public override Vector2 InitialSize => new Vector2(860f, 600f);

        List<AnnouncementDocs.Section> entries;
        Vector2 scrollPosition;
        float contentHeight;

        public Dialog_Changelog(List<AnnouncementDocs.Section> sections)
        {
            entries = sections ?? new List<AnnouncementDocs.Section>();
            forcePause = true;
            absorbInputAroundWindow = true;
            doCloseX = true;
            doCloseButton = true;
            closeOnAccept = true;
            onlyOneOfTypeAllowed = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            float x = inRect.x + 16f;
            float w = inRect.width - 32f;
            float y = inRect.y + 12f;

            Text.Font = GameFont.Medium;
            Widgets.TextArea(new Rect(x, y, w, 34f), "UESDdebuger.DialogChangelogTitle".Translate(), readOnly: true);
            y += 46f;
            Widgets.DrawLineHorizontal(x, y - 4f, w);
            y += 8f;

            Text.Font = GameFont.Small;
            if (entries.Count == 0)
            {
                Widgets.TextArea(new Rect(x, y, w, 26f), "UESDdebuger.DialogChangelogEmpty".Translate(), readOnly: true);
                return;
            }

            // 计算总高度
            contentHeight = 0f;
            foreach (var e in entries)
            {
                Text.Font = GameFont.Small;
                float h = string.IsNullOrEmpty(e.Body) ? 0f : Text.CalcHeight(e.Body, w);
                contentHeight += 30f + h + 20f; // 版本标题行 + 正文 + 段间距
            }

            Rect outRect = new Rect(x, y, w, inRect.yMax - y - 12f);
            Rect viewRect = new Rect(0f, 0f, w - 16f, contentHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            float curY = 0f;
            foreach (var e in entries)
            {
                Text.Font = GameFont.Medium;
                Widgets.TextArea(new Rect(0f, curY, viewRect.width, 30f), "UESDdebuger.Version".Translate(e.Name), readOnly: true);
                curY += 30f;
                if (!string.IsNullOrEmpty(e.Body))
                {
                    Text.Font = GameFont.Small;
                    float h = Text.CalcHeight(e.Body, viewRect.width);
                    Widgets.TextArea(new Rect(0f, curY, viewRect.width, h), e.Body, readOnly: true);
                    curY += h;
                }
                curY += 20f;
            }

            Widgets.EndScrollView();
        }
    }
}

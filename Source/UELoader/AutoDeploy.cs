using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 首次启动自动部署 + 公告：以 RimWorld 官方启动加载模板 [StaticConstructorOnStartup] 执行，
    /// 在带本模组**首次**启动游戏时自动探测本机路径并生成 MCP/config.json
    /// （对应《部署文档.md》§4.1 的目标机配置），使目标机无需手动修改配置。
    ///
    /// 公告分两部分（主菜单弹出）：
    /// - 配置公告（Dialog_FirstRunSetup）：仅首次启动显示，复选框选择 IDE 查看 MCP 配置代码；
    /// - 更新公告（Dialog_Changelog）：读 MCP/announcements.md（### 版本段，从新到旧），
    ///   仅显示「原版本 &lt; 段版本 &lt;= 当前模组版本」区间；首次启动时先配置公告、关闭后全量显示。
    ///
    /// 关键设计：
    /// - 部署与公告**两个独立标记**，互不干扰：
    ///     · MCP/.auto-deploy.done —— 部署完成标记，仅首次生效，之后不再触碰（避免覆盖用户手改路径）；
    ///     · MCP/.announce.done —— 公告版本记录，内容为已播报的模组版本号。版本更新（递增
    ///       About.xml modVersion）时，即使部署早已完成也会**再次播报新版本区间**的更新公告。
    /// - WriteConfig 独立可复用：后续设置界面「强制刷新」可直接调用（会同步更新部署标记）。
    /// - 时序安全：StaticConstructorOnStartupUtility.CallAll() 在 CreateModClasses() 之后执行，
    ///   因此静态构造内 LoadedModManager.GetMod&lt;UELoaderMod&gt;() 可拿到 Content.RootDir；
    ///   极端时序拿不到时静默跳过，不阻断启动。
    /// - 公告时机：弹窗经 AnnouncementWindowQueue（Harmony 钩 UIRootOnGUI）投递，
    ///   进入主菜单后 GUI 帧才弹出，保证公告出现在主菜单而非加载屏。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AutoDeploy
    {
        const string ConfigFileName = "config.json";
        const string MarkerFileName = ".auto-deploy.done";
        const string AnnounceMarkerFileName = ".announce.done";
        const string AppId = "294100";
        const int StartTimeoutMs = 180000;

        /// <summary>模组根目录（静态构造时缓存，WriteConfig 复用）。</summary>
        static string RootDir;

        /// <summary>公告版本号：直接取自 About.xml 的 &lt;modVersion&gt;（ModMetaData.ModVersion）。</summary>
        static string CurrentAnnouncementVersion => RootDir != null ? ResolveModVersion() : "";

        /// <summary>读取 About.xml 的 modVersion（经 ModMetaData.ModVersion，空值回退 "0.0.0"）。</summary>
        static string ResolveModVersion()
        {
            try
            {
                var mod = LoadedModManager.GetMod<UELoaderMod>();
                var meta = mod?.Content?.ModMetaData;
                string v = meta?.ModVersion;
                return string.IsNullOrEmpty(v) ? "0.0.0" : v;
            }
            catch
            {
                return "0.0.0";
            }
        }

        /// <summary>
        /// RimWorld 官方启动加载模板：游戏加载 Defs 阶段自动执行（PlayDataLoader → CallAll）。
        /// 探测并生成 MCP/config.json；任何失败仅记日志，不阻断游戏启动。
        /// </summary>
        static AutoDeploy()
        {
            try
            {
                var mod = LoadedModManager.GetMod<UELoaderMod>();
                RootDir = mod != null && mod.Content != null ? mod.Content.RootDir : null;
                if (string.IsNullOrEmpty(RootDir))
                {
                    UEHttpLog.Warning("[AutoDeploy] 未能获取模组根目录（UELoaderMod 尚未实例化），跳过自动部署与公告");
                    return;
                }
                AnnouncementDocs.SetRootDir(RootDir);
                bool wasFirstRun = !File.Exists(Path.Combine(RootDir, "MCP", MarkerFileName));
                Ensure(RootDir);
                ScheduleAnnouncements(RootDir, wasFirstRun);
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[AutoDeploy] 初始化失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 幂等首启入口：标记文件已存在则跳过（仅首次写入），否则调用 WriteConfig 并落标记。
        /// </summary>
        public static void Ensure(string rootDir)
        {
            try
            {
                string mcpDir = Path.Combine(rootDir, "MCP");
                string marker = Path.Combine(mcpDir, MarkerFileName);
                if (File.Exists(marker))
                {
                    UEHttpLog.Message("[AutoDeploy] 已配置过（存在 .auto-deploy.done），跳过");
                    return;
                }

                string message;
                bool ok = WriteConfig(rootDir, out message);
                if (ok)
                {
                    try
                    {
                        Directory.CreateDirectory(mcpDir);
                        File.WriteAllText(marker,
                            "UESDdebuger 首次自动部署标记。删除此文件后，下次启动会重新探测并写入 config.json。\n"
                            + "生成时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");
                    }
                    catch (Exception ex)
                    {
                        UEHttpLog.Warning($"[AutoDeploy] 写入标记文件失败（不影响 config.json）：{ex.GetType().Name}: {ex.Message}");
                    }
                    UEHttpLog.Message($"[AutoDeploy] 首次自动部署完成：{message}");
                }
                else
                {
                    UEHttpLog.Warning($"[AutoDeploy] 自动部署未完成（不落标记，下次启动重试）：{message}");
                }
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[AutoDeploy] Ensure 失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 公告编排（版本控制）：
        /// - 首次启动（wasFirstRun=true）→ 先弹配置公告，其「确定」回调里再弹全量更新公告；
        /// - 仅版本更新（部署已完成且 .announce.done 版本 &lt; 现版本）→ 只弹更新公告区间
        ///   （原版本 &lt; 段版本 &lt;= 现版本），不弹配置公告。
        /// 弹窗经 AnnouncementWindowQueue 投递，主菜单弹出；任何失败仅记日志不阻断启动。
        /// </summary>
        static void ScheduleAnnouncements(string rootDir, bool isFirstRun)
        {
            try
            {
                string mcpDir = Path.Combine(rootDir, "MCP");
                string marker = Path.Combine(mcpDir, AnnounceMarkerFileName);

                string currentVersion = CurrentAnnouncementVersion;
                string shownVersion = "";
                if (File.Exists(marker))
                {
                    shownVersion = File.ReadAllText(marker).Trim();
                    int nl = shownVersion.IndexOf('\n');
                    if (nl >= 0)
                        shownVersion = shownVersion.Substring(0, nl).Trim();
                }

                bool needUpdate = !VersionAtLeast(shownVersion, currentVersion);

                if (!isFirstRun && !needUpdate)
                {
                    UEHttpLog.Message($"[AutoDeploy] 公告已播报过（.announce.done 版本 {shownVersion} ≥ 当前 {currentVersion}），跳过");
                    return;
                }

                if (isFirstRun)
                {
                    // 首次：先配置公告，关闭后再全量更新公告
                    UEHttpLog.Message($"[AutoDeploy] 首次启动，投递配置公告 + 全量更新公告（版本 {currentVersion}）");
                    var setup = new Dialog_FirstRunSetup(AnnouncementDocs.ParseIdeConfigs());
                    setup.onClosed = () =>
                    {
                        AnnouncementWindowQueue.Add(new Dialog_Changelog(
                            FilterAnnouncements(AnnouncementDocs.ParseAnnouncements(), "", currentVersion)));
                    };
                    AnnouncementWindowQueue.Add(setup);
                }
                else
                {
                    // 版本更新：只弹更新公告区间
                    UEHttpLog.Message($"[AutoDeploy] 版本更新（{shownVersion} → {currentVersion}），投递更新公告区间");
                    AnnouncementWindowQueue.Add(new Dialog_Changelog(
                        FilterAnnouncements(AnnouncementDocs.ParseAnnouncements(), shownVersion, currentVersion)));
                }

                WriteAnnounceMarker(mcpDir, marker, currentVersion);
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[AutoDeploy] 公告编排失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 过滤更新公告段：仅保留 `shownVersion &lt; 段版本 &lt;= currentVersion`（文件本身从新到旧）。
        /// shownVersion 为空（首次）表示全量。
        /// </summary>
        static List<AnnouncementDocs.Section> FilterAnnouncements(
            List<AnnouncementDocs.Section> all, string shownVersion, string currentVersion)
        {
            var result = new List<AnnouncementDocs.Section>();
            foreach (var sec in all)
            {
                string v = sec.Name.Trim();
                if (string.IsNullOrEmpty(v))
                    continue;
                // 段版本 > shownVersion（新于原版本）且 <= currentVersion
                if (string.IsNullOrEmpty(shownVersion) || !VersionAtLeast(v, shownVersion))
                {
                    if (VersionAtLeast(currentVersion, v))
                        result.Add(sec);
                }
            }
            return result;
        }

        static void WriteAnnounceMarker(string mcpDir, string marker, string currentVersion)
        {
            try
            {
                Directory.CreateDirectory(mcpDir);
                File.WriteAllText(marker,
                    currentVersion + "\n"
                    + "UESDdebuger 公告版本记录。删除此文件将重播公告；版本号随 About.xml modVersion 递增。\n"
                    + "播报时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n");
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[AutoDeploy] 写入公告标记失败（不影响弹窗）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>版本字符串比较：a &gt;= b（按点分数字段逐段比较，非纯数字/解析失败时按字符串比较）。</summary>
        static bool VersionAtLeast(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return false;
            if (string.IsNullOrEmpty(b)) return true;
            string[] pa = a.Split('.');
            string[] pb = b.Split('.');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int na = i < pa.Length && int.TryParse(pa[i], out int x) ? x : 0;
                int nb = i < pb.Length && int.TryParse(pb[i], out int y) ? y : 0;
                if (na != nb) return na > nb;
            }
            return true;
        }

        /// <summary>
        /// 探测本机路径并写入 MCP/config.json。独立可复用（设置界面「强制刷新」可调用）。
        /// 只更新成功探测到的字段，未探测到的保留现有 config.json 值；合并保留未知字段。
        /// 返回是否成功写入，message 为结果说明。
        /// </summary>
        public static bool WriteConfig(string rootDir, out string message)
        {
            message = null;
            try
            {
                string mcpDir = Path.Combine(rootDir, "MCP");
                Directory.CreateDirectory(mcpDir);
                string file = Path.Combine(mcpDir, ConfigFileName);

                // 读取现有 config.json（不存在则从空字典开始）
                var cfg = new System.Collections.Generic.Dictionary<string, object>(StringComparer.Ordinal);
                if (File.Exists(file))
                {
                    string raw = File.ReadAllText(file);
                    var parsed = UELightJson.ParseObject(raw);
                    if (parsed != null)
                        foreach (var kv in parsed)
                            cfg[kv.Key] = kv.Value;
                }

                // 探测路径（均带存在性校验，失败返回 null → 保留现有值）
                string gamePath = DetectGamePath();
                string logPath = DetectLogPath();
                string steamPath = DetectSteamPath();

                string updated = "";
                if (gamePath != null)
                {
                    cfg["gamePath"] = ToSlash(gamePath);
                    updated += " gamePath=" + ToSlash(gamePath);
                }
                if (logPath != null)
                {
                    cfg["logPath"] = ToSlash(logPath);
                    updated += " logPath=" + ToSlash(logPath);
                }
                if (steamPath != null)
                {
                    cfg["steamPath"] = ToSlash(steamPath);
                    updated += " steamPath=" + ToSlash(steamPath);
                }
                cfg["appId"] = AppId;
                cfg["startTimeout"] = StartTimeoutMs;

                // 手写紧凑 JSON（保持与 MCP 侧读取兼容；UELightJson 无缩进）
                var sb = new StringBuilder(256);
                sb.Append("{\n");
                bool first = true;
                foreach (var kv in cfg)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("  \"").Append(UELightJson.Escape(kv.Key)).Append("\": ");
                    object v = kv.Value;
                    if (v is string str)
                        sb.Append('"').Append(UELightJson.Escape(str)).Append('"');
                    else if (v is bool b)
                        sb.Append(b ? "true" : "false");
                    else if (v is int || v is long || v is short || v is byte ||
                             v is uint || v is ulong || v is ushort || v is sbyte)
                        sb.Append(Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture));
                    else if (v is double d)
                        sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    else if (v == null)
                        sb.Append("null");
                    else
                        sb.Append('"').Append(UELightJson.Escape(v.ToString())).Append('"');
                }
                sb.Append("\n}");

                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                message = (string.IsNullOrEmpty(updated) ? "未探测到可更新的路径字段，已保留现有值" : "已更新:" + updated) + " → " + file;
                return true;
            }
            catch (Exception ex)
            {
                message = $"{ex.GetType().Name}: {ex.Message}";
                UEHttpLog.Error($"[AutoDeploy] 写入 config.json 失败：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 游戏可执行文件路径：Application.dataPath = &lt;游戏根&gt;/RimWorldWin64_Data，
        /// 取其父目录 + RimWorldWin64.exe，并校验文件存在。
        /// </summary>
        static string DetectGamePath()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (string.IsNullOrEmpty(dataPath))
                    return null;
                string dir = Path.GetDirectoryName(dataPath);
                string exe = Path.Combine(dir, "RimWorldWin64.exe");
                return File.Exists(exe) ? exe : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Player.log 路径：优先 Unity 运行时提供的 consoleLogPath（绝对路径），校验文件存在。
        /// </summary>
        static string DetectLogPath()
        {
            try
            {
                string p = Application.consoleLogPath;
                return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Steam 可执行文件路径：读注册表 HKCU\Software\Valve\Steam 的 SteamPath + steam.exe；
        /// 失败时探测常见安装位置。仍找不到返回 null。
        /// </summary>
        static string DetectSteamPath()
        {
            string steamDir = null;
            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key != null)
                {
                    object v = key.GetValue("SteamPath");
                    if (v != null)
                        steamDir = Convert.ToString(v);
                    key.Close();
                }
            }
            catch { }

            if (string.IsNullOrEmpty(steamDir))
            {
                string[] candidates =
                {
                    @"C:\Program Files (x86)\Steam",
                    @"C:\Program Files\Steam",
                    @"D:\Steam",
                    @"D:\Program Files (x86)\Steam",
                    @"E:\Steam",
                    @"C:\Steam"
                };
                foreach (string c in candidates)
                {
                    if (Directory.Exists(c))
                    {
                        steamDir = c;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(steamDir))
                return null;
            string exe = Path.Combine(steamDir, "steam.exe");
            return File.Exists(exe) ? exe : null;
        }

        static string ToSlash(string p)
        {
            return string.IsNullOrEmpty(p) ? p : p.Replace('\\', '/');
        }
    }
}

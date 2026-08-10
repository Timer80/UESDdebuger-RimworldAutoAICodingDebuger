using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 项目配置的可序列化实体：单个工具开关（供 Scribe 序列化与设置界面绑定）。
    /// </summary>
    public class ToolToggle : IExposable
    {
        public string name;
        public bool enabled;
        public string description;

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name", "");
            Scribe_Values.Look(ref enabled, "enabled", false);
        }
    }

    /// <summary>
    /// UESDdebuger 模组设置（双通道）：
    /// - 原生通道：继承 ModSettings，经 ExposeData/Scribe 持久化到 RimWorld Config 文件夹
    ///   （Config/Mods/Mod_UELoader_UELoaderMod.xml），由 GetSettings&lt;T&gt;()/WriteSettings() 托管；
    /// - 项目配置通道：LoadFromProjectConfig()/WriteProjectConfigs() 读写 MCP/config.json 与
    ///   MCP/toolConfig.json（MCP 服务器启动时读取这两份文件）。
    /// 设置界面编辑的字段在关闭窗口时（WriteSettings）写回两份 JSON，供 MCP 重启后生效。
    /// </summary>
    public class UELoaderSettings : ModSettings
    {
        // ---------------- config.json 字段 ----------------
        public string gamePath = "";
        public string logPath = "";
        public string steamPath = "";
        public string appId = "294100";
        public int startTimeout = 180000;

        // ---------------- toolConfig.json 字段 ----------------
        public bool toolDefaultEnabled;
        public List<ToolToggle> toolToggles = new List<ToolToggle>();

        // ---------------- UI 状态（不参与 Scribe 持久化） ----------------
        public Vector2 scrollPos;
        public string startTimeoutBuffer = "180000";
        public string statusMsg;

        /// <summary>toolConfig.json 缺失时的后备工具名清单（与 MCP/toolConfig.json 当前清单一致）。</summary>
        static readonly string[] DefaultToolNames =
        {
            "agg_list_tools", "agg_call_tool", "mcp_help", "start_game", "stop_game",
            "get_game_status", "read_log", "tail_log", "get_config", "start_quick_test",
            "inspect_type", "get_unityexplorer_status", "clear_unityexplorer_logs",
            "get_unityexplorer_logs", "create_hook", "toggle_hook", "delete_hook", "list_hooks",
            "execute_csharp_code", "reset_csharp_console", "add_using_directive",
            "post_camera_change_zoom", "post_camera_change_position", "post_stream_start",
            "post_stream_stop", "post_stream_setup", "get_game_state", "get_colonists",
            "get_maps", "get_map_things", "get_map_plants", "get_map_weather", "get_map_animals",
            "get_research", "get_factions", "get_world_caravans", "post_game_load",
            "post_game_save", "post_incident_execute", "post_order_designate", "post_game_speed",
            "get_colonists_detailed", "get_colonist_detailed", "get_version", "get_mods_info",
            "post_select", "post_deselect", "post_ui_message", "post_ui_dialog",
            "post_dev_console", "list_debug_actions", "get_debug_action_detail",
            "execute_debug_action", "get_debug_action_categories", "search_debug_actions",
            "get_map_structure", "search_map_structure", "status", "attach", "detach", "resume",
            "suspend", "launch", "break_add", "break_list", "break_remove", "break_clear",
            "break_exception", "wait", "step", "threads", "callstack", "locals", "inspect",
            "eval", "find_types", "find_methods"
        };

        /// <summary>有说明的工具名集合（翻译键由工具名派生：UESDdebuger.ToolDesc.&lt;toolName&gt;；未收录的工具显示为空）。</summary>
        static readonly HashSet<string> ToolNamesWithDescriptions = new HashSet<string>(DefaultToolNames, StringComparer.Ordinal);

        /// <summary>工具说明的翻译键；未收录返回空串。供加载阶段存储用（此时语言尚未激活，不能 Translate）。</summary>
        public static string GetToolDescriptionKey(string toolName)
        {
            if (string.IsNullOrEmpty(toolName) || !ToolNamesWithDescriptions.Contains(toolName))
                return "";
            return "UESDdebuger.ToolDesc." + toolName;
        }

        /// <summary>查询工具简短说明（本地化，需在语言已激活时调用）；未收录返回空串。</summary>
        public static string GetToolDescription(string toolName)
        {
            string key = GetToolDescriptionKey(toolName);
            return string.IsNullOrEmpty(key) ? "" : key.Translate();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref gamePath, "gamePath", "");
            Scribe_Values.Look(ref logPath, "logPath", "");
            Scribe_Values.Look(ref steamPath, "steamPath", "");
            Scribe_Values.Look(ref appId, "appId", "294100");
            Scribe_Values.Look(ref startTimeout, "startTimeout", 180000);
            Scribe_Values.Look(ref toolDefaultEnabled, "toolDefaultEnabled", false);
            Scribe_Collections.Look(ref toolToggles, "toolToggles", LookMode.Deep);
        }

        // ---------------------------------------------------------------- 项目配置通道

        /// <summary>
        /// 从 MCP/config.json 与 MCP/toolConfig.json 载入字段（文件存在则覆盖原生 XML 值，
        /// 保证设置界面显示项目真实配置）。文件缺失时保留默认值；toolConfig.json 缺失时用内置清单。
        /// 任何失败仅日志，不抛异常。
        /// </summary>
        public void LoadFromProjectConfig(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot))
                return;

            try
            {
                string configPath = Path.Combine(modRoot, "MCP", "config.json");
                if (File.Exists(configPath))
                {
                    var cfg = UELightJson.ParseObject(File.ReadAllText(configPath));
                    if (cfg != null)
                    {
                        if (cfg.TryGetValue("gamePath", out object v)) gamePath = Convert.ToString(v);
                        if (cfg.TryGetValue("logPath", out object v2)) logPath = Convert.ToString(v2);
                        if (cfg.TryGetValue("steamPath", out object v3)) steamPath = Convert.ToString(v3);
                        if (cfg.TryGetValue("appId", out object v4)) appId = Convert.ToString(v4);
                        if (cfg.TryGetValue("startTimeout", out object v5) && v5 != null
                            && long.TryParse(Convert.ToString(v5), out long to))
                            startTimeout = (int)to;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] 读取 config.json 失败（使用默认值）: {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                string toolConfigPath = Path.Combine(modRoot, "MCP", "toolConfig.json");
                if (File.Exists(toolConfigPath))
                {
                    var tc = UELightJson.ParseObject(File.ReadAllText(toolConfigPath));
                    if (tc != null)
                    {
                        if (tc.TryGetValue("defaultEnabled", out object de))
                            toolDefaultEnabled = Convert.ToBoolean(de);
                        if (tc.TryGetValue("tools", out object toolsObj) && toolsObj is IDictionary toolsDict)
                        {
                            var list = new List<ToolToggle>();
                            foreach (DictionaryEntry e in toolsDict)
                            {
                                string toolName = Convert.ToString(e.Key);
                                list.Add(new ToolToggle
                                {
                                    name = toolName,
                                    enabled = Convert.ToBoolean(e.Value),
                                    description = GetToolDescriptionKey(toolName)
                                });
                            }
                            toolToggles = list;
                        }
                    }
                }
                else
                {
                    toolToggles = new List<ToolToggle>();
                    foreach (string name in DefaultToolNames)
                        toolToggles.Add(new ToolToggle { name = name, enabled = false, description = GetToolDescriptionKey(name) });
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] 读取 toolConfig.json 失败（使用内置清单）: {ex.GetType().Name}: {ex.Message}");
            }

            startTimeoutBuffer = startTimeout.ToString();
        }

        /// <summary>
        /// 把当前字段写回 MCP/config.json 与 MCP/toolConfig.json。
        /// 合并策略：读回现有 JSON，保留未知键，仅更新本项目管理的键。失败仅日志。
        /// </summary>
        public void WriteProjectConfigs(string modRoot)
        {
            if (string.IsNullOrEmpty(modRoot))
                return;

            try
            {
                string mcpDir = Path.Combine(modRoot, "MCP");
                Directory.CreateDirectory(mcpDir);

                // config.json：保留未知键，更新已知键
                string configPath = Path.Combine(mcpDir, "config.json");
                var cfg = new Dictionary<string, object>(StringComparer.Ordinal);
                if (File.Exists(configPath))
                {
                    var existing = UELightJson.ParseObject(File.ReadAllText(configPath));
                    if (existing != null)
                        cfg = existing;
                }
                cfg["gamePath"] = gamePath ?? "";
                cfg["logPath"] = logPath ?? "";
                cfg["steamPath"] = steamPath ?? "";
                cfg["appId"] = appId ?? "294100";
                cfg["startTimeout"] = (long)startTimeout;
                File.WriteAllText(configPath, UELightJson.Serialize(cfg));
                UEHttpLog.Message("[UELoader] config.json 已写回（gamePath=" + cfg["gamePath"] + "）");

                // toolConfig.json：保留 _comment 等未知键，更新 defaultEnabled 与 tools
                string toolConfigPath = Path.Combine(mcpDir, "toolConfig.json");
                var tc = new Dictionary<string, object>(StringComparer.Ordinal);
                if (File.Exists(toolConfigPath))
                {
                    var existing = UELightJson.ParseObject(File.ReadAllText(toolConfigPath));
                    if (existing != null)
                        tc = existing;
                }
                tc["defaultEnabled"] = toolDefaultEnabled;
                var tools = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (ToolToggle t in toolToggles)
                    tools[t.name] = t.enabled;
                tc["tools"] = tools;
                File.WriteAllText(toolConfigPath, UELightJson.Serialize(tc));
                UEHttpLog.Message("[UELoader] toolConfig.json 已写回（" + toolToggles.Count + " 个工具开关）");
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] 写回项目配置失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 「重新探测并重置 config.json」：复用 AutoDeploy.WriteConfig 的首次启动探测逻辑
        /// （重测 gamePath/logPath/steamPath，写回固定 appId/startTimeout），成功后重载设置界面值。
        /// 只写 config.json，不碰 toolConfig.json。返回给界面展示的 detail。
        /// </summary>
        public string ApplyAutoDeploy(string rootDir)
        {
            bool ok = AutoDeploy.WriteConfig(rootDir, out string detail);
            if (ok)
            {
                LoadFromProjectConfig(rootDir);
                return "UESDdebuger.ResetComplete".Translate(detail);
            }
            return "UESDdebuger.ResetFailed".Translate(detail);
        }
    }
}

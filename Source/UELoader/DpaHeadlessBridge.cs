using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace UELoader
{
    /// <summary>
    /// DPA（Dubs Performance Analyzer）无窗口 profiling 桥 Harmony 补丁（独立补丁，不依赖 UESDdebuger 其他模块）。
    ///
    /// 本类装两个补丁，解决 headless（DPA 窗口未开）下 dpa_* 链路失效的两个独立环节：
    ///
    /// 【补丁 1】Active 门控桥（Patch A）
    /// 背景：RimBridgeServer 头less dpa 链路（dpa_patch_methods → play_for → dpa_snapshot）在 DPA 窗口
    /// 未打开时永远拿不到数据。根因（实测 + 读 DPA 源码确认）：
    ///   - DPA 给目标方法注入的计时 wrapper 门控在静态标志 CustomProfilersTick/Update.Active 上，默认 false；
    ///   - 该标志平时由 DPA 窗口 GUI 每帧执行 GUIController.CurrentEntry.SetActive(!Analyzer.CurrentlyPaused)
    ///     维持；窗口未开则无人刷新；
    ///   - RBS 头less patch（游戏暂停态发起）经 ExecutePatch → SwapToEntry 只把 Active 置 false（!paused），
    ///     之后解暂停计时 wrapper 全部跳过 → dpa_snapshot rows 恒空（currentLogCount 空转增长是唯一迹象）。
    /// 修复：在 DPA patch 统一入口 Panel_DevOptions.ExecutePatch(mode, input, category) 尾部 postfix
    /// 把 CustomProfilersTick/Update 的 Active 置 true，与暂停态解耦。CurrentCategory 由 ExecutePatch
    /// 内部 GUIController.SwapToEntry 已设好。同时激活两个类别是安全的：未打补丁的类别没有 wrapper。
    ///
    /// 【补丁 2】Entry 名本地化容错（Patch B，方案 A）
    /// 背景：DPA 在 ExecutePatch 尾部用**硬编码英文 entry 名**换页：
    ///   string entryName = (cat == Category.Tick) ? "Custom Tick" : "Custom Update";
    ///   GUIController.SwapToEntry(entryName);
    /// 而 GUIController.EntryByName 是精确字符串匹配（e.name == name），Entry.name 来自翻译键
    /// （entry.tick.custom / entry.update.custom）：
    ///   - 英文环境：键值 == "Custom Tick" → 匹配成功；
    ///   - 中文环境：键值 == "自定义Tick" → 无匹配 → Linq First() 抛
    ///     InvalidOperationException: Sequence contains no matching element → 被 DPA 自己 try/catch 吞掉，
    ///     报成 "[Analyzer] Failed to process search bar input"。
    /// 后果：方法其实已被 MethodTransplanting 打上补丁，但 SwapToEntry 没走完 → currentEntry 未切换、
    /// SetActive(true) 未执行 → 目标类别 wrapper 不激活 → rows 依旧为空（补丁 1 也救不回来）。
    /// 这是 DPA 的本地化 bug：只有在非英文语言下才暴露。
    /// 修复（方案 A，最小侵入 + 与语言无关）：给 GUIController.EntryByName 挂 Prefix，在精确匹配失败时
    /// 依次用「归一化 / 键名翻译 / 默认语言值→当前语言值反查 / 内置别名表」把传入名改写成真实 entry 名，
    /// 再交回 DPA 原逻辑执行；始终找不到时保持原样（DPA 行为不变），并只告警一次。
    ///
    /// 约束（DPA 为外部程序集，编译期不引用）：类型/方法/字段全部反射解析一次并缓存；
    /// 目标不可用（DPA 未启用 / 结构变化）时仅记日志 no-op，不阻断游戏。
    /// </summary>
    public static class DpaHeadlessBridge
    {
        public const string HarmonyId = "UESDdebuger.dpa.bridge";

        /// <summary>Active 门控桥（补丁 1）是否安装成功。</summary>
        public static bool Installed { get; private set; }

        /// <summary>Entry 名本地化容错（补丁 2 / 方案 A）是否安装成功。</summary>
        public static bool EntryNamePatchInstalled { get; private set; }

        static Harmony harmony;
        static FieldInfo activeTickField;
        static FieldInfo activeUpdateField;

        // ---- 补丁 2 反射面：GUIController.Tabs → Tab.entries → Entry.name ----
        static PropertyInfo tabsProperty;
        static FieldInfo tabEntriesField;
        static FieldInfo entryNameField;

        /// <summary>DPA patch 入口内部硬编码的英文 entry 名 → 其翻译键（兜底别名表）。</summary>
        static readonly Dictionary<string, string> KnownEntryKeys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "Custom Tick", "entry.tick.custom" },
            { "Custom Update", "entry.update.custom" },
        };

        /// <summary>默认语言（英文）键值 → 当前语言键值 缓存，按语言目录名失效。</summary>
        static Dictionary<string, string> defaultToActiveCache;
        static string defaultToActiveCacheLangFolder;

        /// <summary>已告警过的未命中名 / 错误标记，避免刷屏。</summary>
        static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 安装 DPA 桥（幂等）。通常在 ExplorerBootstrap.Initialize（主线程、场景加载后）调用；
        /// 任何失败仅记录日志，不影响游戏与 DPA 本身。
        /// </summary>
        public static void Install()
        {
            if (Installed && EntryNamePatchInstalled)
                return;

            try
            {
                Assembly dpa = FindDpaAssembly();
                if (dpa == null)
                {
                    Log.Message("[DpaBridge] 未找到 PerformanceAnalyzer 程序集（Dubs Performance Analyzer 未启用？），桥未安装");
                    return;
                }

                if (harmony == null)
                    harmony = new Harmony(HarmonyId);

                // Panel_DevOptions.ExecutePatch(CurrentInput mode, string strinput, Category cat) —— 静态三参重载
                Type panelDevOptions = dpa.GetType("Analyzer.GUI.Panel_DevOptions")
                    ?? dpa.GetTypes().FirstOrDefault(t => t.Name == "Panel_DevOptions");
                MethodInfo executePatch = panelDevOptions == null
                    ? null
                    : panelDevOptions.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "ExecutePatch" && m.GetParameters().Length == 3);

                InstallActiveGatePatch(dpa, executePatch);
                InstallEntryNamePatch(dpa);
            }
            catch (Exception ex)
            {
                Installed = false;
                Log.Error($"[DpaBridge] 安装桥失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ==================================================================================
        // 补丁 1：Active 门控桥
        // ==================================================================================
        static void InstallActiveGatePatch(Assembly dpa, MethodInfo executePatch)
        {
            if (Installed)
                return;

            if (executePatch == null)
            {
                Log.Warning("[DpaBridge] 未找到 DPA Panel_DevOptions.ExecutePatch（三参），Active 门控桥未生效");
                return;
            }

            // 计时 wrapper 门控标志：Analyzer.Profiling.CustomProfilersTick / CustomProfilersUpdate 的静态 Active
            Type customTick = dpa.GetType("Analyzer.Profiling.CustomProfilersTick");
            Type customUpdate = dpa.GetType("Analyzer.Profiling.CustomProfilersUpdate");
            activeTickField = customTick?.GetField("Active", BindingFlags.Public | BindingFlags.Static);
            activeUpdateField = customUpdate?.GetField("Active", BindingFlags.Public | BindingFlags.Static);
            if (activeTickField == null && activeUpdateField == null)
            {
                Log.Warning("[DpaBridge] 未找到 CustomProfilersTick/Update.Active 字段，Active 门控桥未生效");
                return;
            }

            harmony.Patch(executePatch, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(DpaHeadlessBridge), nameof(ExecutePatch_Postfix))));

            Installed = true;
            Log.Message($"[DpaBridge] 已钩住 {executePatch.DeclaringType.FullName}.{executePatch.Name}：patch 后自动激活 DPA profiling 类别（headless 免开窗口）");
        }

        /// <summary>
        /// ExecutePatch 尾部激活：把 CustomProfilersTick/Update 的 Active 置 true（幂等，无参 postfix，
        /// 不依赖 Harmony 对枚举参数的名称绑定）。DPA 计时 wrapper 门控 = 类别.Active && currentlyProfiling && !paused。
        /// </summary>
        static void ExecutePatch_Postfix()
        {
            try
            {
                if (activeTickField != null && !(bool)activeTickField.GetValue(null))
                    activeTickField.SetValue(null, true);
                if (activeUpdateField != null && !(bool)activeUpdateField.GetValue(null))
                    activeUpdateField.SetValue(null, true);
            }
            catch (Exception ex)
            {
                // 静默降级：激活失败不阻断 DPA 自身的 patch 流程
                Log.Warning($"[DpaBridge] 激活 DPA profiling 类别失败（可忽略）: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ==================================================================================
        // 补丁 2（方案 A）：EntryByName 本地化容错
        // ==================================================================================
        static void InstallEntryNamePatch(Assembly dpa)
        {
            if (EntryNamePatchInstalled)
                return;

            Type guiController = dpa.GetType("Analyzer.Profiling.GUIController")
                ?? dpa.GetTypes().FirstOrDefault(t => t.Name == "GUIController");
            Type tabType = dpa.GetType("Analyzer.Profiling.Tab") ?? dpa.GetTypes().FirstOrDefault(t => t.Name == "Tab");
            Type entryType = dpa.GetType("Analyzer.Profiling.Entry") ?? dpa.GetTypes().FirstOrDefault(t => t.Name == "Entry");

            MethodInfo entryByName = guiController?.GetMethod(
                "EntryByName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            tabsProperty = guiController?.GetProperty("Tabs", BindingFlags.Public | BindingFlags.Static);
            tabEntriesField = tabType?.GetField("entries", BindingFlags.Public | BindingFlags.Instance);
            entryNameField = entryType?.GetField("name", BindingFlags.Public | BindingFlags.Instance);

            if (entryByName == null || tabsProperty == null || tabEntriesField == null || entryNameField == null)
            {
                Log.Warning("[DpaBridge] GUIController.EntryByName 反射面不完整（DPA 结构变化？），本地化容错未生效"
                    + $"（entryByName={entryByName != null}, tabs={tabsProperty != null}, tabEntries={tabEntriesField != null}, entryName={entryNameField != null}）");
                return;
            }

            harmony.Patch(entryByName, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(DpaHeadlessBridge), nameof(EntryByName_Prefix))));

            EntryNamePatchInstalled = true;
            Log.Message("[DpaBridge] 已钩住 Analyzer.Profiling.GUIController.EntryByName：英文硬编码 entry 名 → 本地化名容错"
                + "（修复非英文语言下 ExecutePatch → SwapToEntry 抛 Sequence contains no matching element）");
        }

        /// <summary>
        /// EntryByName 前缀：把 DPA 传入的（可能是硬编码英文的）entry 名改写为真实存在的 entry 名。
        /// 命中即改写后交回原方法（原方法的精确匹配随后成立）；未命中则不改，保持 DPA 原行为。
        /// </summary>
        static void EntryByName_Prefix(ref string name)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || tabsProperty == null)
                    return;

                string how;
                string resolved = ResolveEntryName(name, out how);
                if (resolved != null && !string.Equals(resolved, name, StringComparison.Ordinal))
                {
                    Log.Message($"[DpaBridge] EntryByName 本地化容错命中（{how}）: \"{name}\" → \"{resolved}\"");
                    name = resolved;
                }
                else if (resolved == null && warned.Add("miss:" + name))
                {
                    Log.Warning($"[DpaBridge] EntryByName 未找到匹配 entry：\"{name}\""
                        + "（精确/归一化/键名/英文值反查/别名表均未命中），交回 DPA 原逻辑处理");
                }
            }
            catch (Exception ex)
            {
                // 静默降级：容错失败不能让 DPA 的换页流程更糟
                if (warned.Add("err:" + ex.GetType().Name))
                    Log.Warning($"[DpaBridge] EntryByName 容错前缀异常（已忽略）: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 逐层解析真实 entry 名：精确 → 归一化 → 传入名当翻译键 → 默认语言值反查当前语言值 → 内置别名表。
        /// </summary>
        static string ResolveEntryName(string input, out string how)
        {
            how = null;
            List<string> names = CollectEntryNames();
            if (names.Count == 0)
                return null;

            // 1) 精确（与 DPA 原逻辑一致，正常情况下在这里就命中）
            foreach (string n in names)
            {
                if (string.Equals(n, input, StringComparison.Ordinal))
                {
                    how = "精确";
                    return n;
                }
            }

            // 2) 归一化：忽略大小写、空白与常见分隔符
            string normalizedInput = Normalize(input);
            foreach (string n in names)
            {
                if (Normalize(n) == normalizedInput)
                {
                    how = "归一化";
                    return n;
                }
            }

            // 3) 传入名本身就是翻译键（例如 "entry.tick.custom"）
            string byKey = ActiveValueOfKey(input);
            string matched;
            if (MatchEntryName(names, byKey, out matched))
            {
                how = $"键名 {input}";
                return matched;
            }

            // 4) 默认语言（英文）值 → 当前语言值 反查
            Dictionary<string, string> map = GetDefaultToActiveMap();
            string localized;
            if (map != null && map.TryGetValue(input, out localized) && MatchEntryName(names, localized, out matched))
            {
                how = "英文值反查";
                return matched;
            }

            // 5) 内置别名表：DPA patch 入口写死的英文 entry 名（最兜底，不依赖英文语言包是否可用）
            string aliasKey;
            if (KnownEntryKeys.TryGetValue(input, out aliasKey))
            {
                string aliasValue = ActiveValueOfKey(aliasKey);
                if (MatchEntryName(names, aliasValue, out matched))
                {
                    how = $"别名表 {aliasKey}";
                    return matched;
                }
            }

            return null;
        }

        /// <summary>收集当前所有 Tab 下真实存在的 entry 名（GUIController.Tabs → Tab.entries → Entry.name）。</summary>
        static List<string> CollectEntryNames()
        {
            var result = new List<string>();
            var tabs = tabsProperty?.GetValue(null, null) as IEnumerable;
            if (tabs == null)
                return result;

            foreach (object tab in tabs)
            {
                if (tab == null)
                    continue;
                var entries = tabEntriesField.GetValue(tab) as IDictionary;
                if (entries == null)
                    continue;
                foreach (DictionaryEntry de in entries)
                {
                    if (de.Key == null)
                        continue;
                    string n = entryNameField.GetValue(de.Key) as string;
                    if (!string.IsNullOrEmpty(n))
                        result.Add(n);
                }
            }
            return result;
        }

        static bool MatchEntryName(List<string> names, string candidate, out string matched)
        {
            matched = null;
            if (string.IsNullOrEmpty(candidate))
                return false;
            string normalizedCandidate = Normalize(candidate);
            foreach (string n in names)
            {
                if (string.Equals(n, candidate, StringComparison.Ordinal) || Normalize(n) == normalizedCandidate)
                {
                    matched = n;
                    return true;
                }
            }
            return false;
        }

        /// <summary>当前语言下某翻译键的值；键不存在或为占位符（TODO）时返回 null。</summary>
        static string ActiveValueOfKey(string key)
        {
            LoadedLanguage active = LanguageDatabase.activeLanguage;
            if (active == null || string.IsNullOrEmpty(key))
                return null;
            LoadedLanguage.KeyedReplacement kr;
            if (active.keyedReplacements.TryGetValue(key, out kr) && kr != null && !kr.isPlaceholder)
                return kr.value;
            return null;
        }

        /// <summary>
        /// 构建「默认语言（英文）键值 → 当前语言键值」映射，用于把 DPA 硬编码的英文 entry 名翻译成当前语言名。
        /// 仅在精确/归一化都失败时才会走到（即真正的异常场景），并按语言目录名缓存。
        /// </summary>
        static Dictionary<string, string> GetDefaultToActiveMap()
        {
            LoadedLanguage active = LanguageDatabase.activeLanguage;
            LoadedLanguage def = LanguageDatabase.defaultLanguage;
            if (active == null || def == null || ReferenceEquals(active, def))
                return null;
            if (defaultToActiveCache != null && defaultToActiveCacheLangFolder == active.folderName)
                return defaultToActiveCache;

            // 触发默认语言 keyed 数据加载（HaveTextForKey 内部会调用 LoadData；英文语言包平时不会被加载）
            def.HaveTextForKey("__dpa_bridge_probe__");

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, LoadedLanguage.KeyedReplacement> kv in active.keyedReplacements)
            {
                LoadedLanguage.KeyedReplacement ak = kv.Value;
                if (ak == null || ak.isPlaceholder || string.IsNullOrEmpty(ak.value))
                    continue;
                LoadedLanguage.KeyedReplacement dk;
                if (!def.keyedReplacements.TryGetValue(kv.Key, out dk) || dk == null || dk.isPlaceholder || string.IsNullOrEmpty(dk.value))
                    continue;
                if (string.Equals(dk.value, ak.value, StringComparison.Ordinal))
                    continue;
                if (!map.ContainsKey(dk.value))
                    map[dk.value] = ak.value;
            }

            defaultToActiveCache = map;
            defaultToActiveCacheLangFolder = active.folderName;
            return map;
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || c == '_' || c == '-' || c == '|')
                    continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static Assembly FindDpaAssembly()
        {
            try
            {
                return AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "PerformanceAnalyzer");
            }
            catch
            {
                return null;
            }
        }
    }
}

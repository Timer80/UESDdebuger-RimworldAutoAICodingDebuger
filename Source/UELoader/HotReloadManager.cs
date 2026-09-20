using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 热重载桥游戏内核心（规格 2026-08-27-hotreload-bridge-design）：
    /// - 程序集定位：启动时缓存 Location 在 Mods/ 下的已加载程序集（Location → Assembly）；
    /// - 自动监视：FileSystemWatcher 监视全部启用 mod 的 Assemblies 目录（含版本子目录）；
    /// - 重打：mod 重打优先（Harmony 按程序集精确卸载 + 静态构造重放 + PatchAll），
    ///   method detour 兜底（签名 + Cecil 字段引用集校验后写跳板）；
    /// - 状态/历史：暴露给 /hotreload/status，含最近重打记录（affectedMethods 供 MCP 懒清理断点）。
    /// 所有公开方法静默失败（记日志），不抛异常阻断游戏。
    /// </summary>
    public static class HotReloadManager
    {
        static readonly string ModsRoot =
            Path.GetFullPath(Path.Combine(GenFilePaths.ModsFolderPath)); // Mods/ 目录

        static bool initialized;
        /// <summary>
        /// 自动监视开关。**默认 false（休眠）**：本项目把热重载桥定位为"隐藏的实验性工具"，
        /// 当前版本不能满足需求（字段布局变化等不覆盖），因此不允许"任何 DLL 更换就自动生效"——
        /// 必须由明确指令启用：agg_call_tool { tool: "hotreload_watch", args: { "enabled": true } }。
        /// 另一层原因：watcher 会监视所有启用 mod 的 Assemblies（含本模组自己的 UELoader.dll），
        /// 开着就会在自体重建时把自己也当目标重载（工具啃自己），危险且无意义。
        /// </summary>
        static bool watching = false;
        static readonly Dictionary<string, Assembly> loadedByLocation =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        /// <summary>DLL 路径 → 历代程序集（[0]=启动时的原始程序集，其后为历代热加载程序集）。
        /// 卸载补丁时必须覆盖**全部历代**：PatchAll 产生的补丁来自新影子程序集，与启动代不是同一 Assembly 对象。</summary>
        static readonly Dictionary<string, List<Assembly>> generations =
            new Dictionary<string, List<Assembly>>(StringComparer.OrdinalIgnoreCase);

        static readonly Dictionary<string, string> dllHashBase =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Initialize 时的 DLL 内容哈希基准
        static readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        static readonly List<ReloadRecord> history = new List<ReloadRecord>();
        const int HistoryMax = 20;
        static int reloadCount;

        /// <summary>本会话内已"详报"过的跳过类型（每个类型只详报一次，避免每轮重建重复刷屏）。</summary>
        static readonly HashSet<string> loggedSkipTypes = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>本模组自身程序集（禁止自体重载；见 FindChangedDlls）。</summary>
        static readonly Assembly SelfAssembly = typeof(HotReloadManager).Assembly;
        static bool selfSkipNoticed;

        /// <summary>
        /// 使用/查询热重载前必须知道的边界（与 MCP 侧 HOTRELOAD_LIMITATIONS 保持一致的双份声明：
        /// 游戏侧这份让 hotreload_status 直接携带局限，AI/人不依赖外部文档也能看到）。
        /// </summary>
        static readonly List<string> Limitations = new List<string>
        {
            "字段布局变化不覆盖：类型增删/改名/改类型字段 → 该类型本轮整体不重载（原子性保证不出现半新半旧），需重启，或把改动逻辑外移到普通方法 / 用侧表（ConditionalWeakTable）存新状态",
            "静态字段不延续：detour 后访问的是新程序集影子类型的静态字段（零初始化），旧静态状态不会迁移",
            "不覆盖范围：开放泛型类型的方法、编译器生成状态机（迭代器/异步）的成员、字段集合相同仅调换声明顺序的类型",
            "程序集不卸载：每轮重载驻留一个副本（reloadCount 可见），大量重载后建议重启",
            "本工具不重载自身（UELoader.dll）：本模组改动仍需重启游戏",
        };

        public class ReloadRecord
        {
            public string modName;
            public string mode;              // "mod" | "method" | "rejected"
            public bool ok;
            public string message;
            public List<string> affectedMethods = new List<string>();
            public string time = DateTime.Now.ToString("HH:mm:ss");

            // ---- detour 统计（可观测性：每个方法为何没打上都有账） ----
            public int candidates;           // 判定过的新方法数
            public int detoured;             // 成功写跳板数
            public int skipNoOld;            // 旧类型/旧同名方法缺失
            public int skipPatched;          // 已被 Harmony patch（走 patch 管线）
            public int skipSignature;        // 签名变化
            public int skipStructure;        // 字段引用集/布局变化（安全拒绝）
            public int skipCompilerGenerated;// 其中「编译器生成状态机」的成员（预期内，汇总记录，不逐条刷屏）
            public readonly HashSet<string> skippedCompilerTypes = new HashSet<string>(StringComparer.Ordinal);
            // 按类型聚合的跳过/失败账目（供"每种只详报一次"+ 对外暴露）
            public readonly Dictionary<string, int> skippedByType = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> skipReasonByType = new Dictionary<string, string>(StringComparer.Ordinal);
            public int skipNotReloadable;    // 构造器/泛型/访问器/抽象/接口/无方法体
            public int detourFailed;         // 写跳板失败（无入口等）
        }

        // ---- 初始化 ----

        /// <summary>主线程调用（ExplorerBootstrap.Initialize 末尾）。幂等。</summary>
        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                loggedSkipTypes.Clear();
                CacheLoadedAssemblies();
                AttachWatchers();
                UEHttpLog.Info("[HotReload] 已初始化（隐藏工具·自动监视默认休眠）：已挂 " + watchers.Count
                    + " 个 Assemblies 目录监听，但 watching=false 时不会自动重载；"
                    + "如需启用请显式调用 hotreload_watch {enabled:true}（实验性，见 hotreload_status 的 limitations）");
            }
            catch (Exception ex)
            {
                UEHttpLog.Error("[HotReload] Initialize failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// 文件内容哈希（SHA1 十六进制）。判定"DLL 是否真的变了"用内容而非 mtime：
        /// mtime 会被同秒重建/时间戳回写/构建工具跳过复制等情况欺骗（2026-09-19 TEXT20 实测：
        /// watcher 报了 DLL 变化，mtime 判定却认为未变化 → 整次重打静默丢失）。读失败返回 null。
        /// </summary>
        static string HashFile(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var sha = System.Security.Cryptography.SHA1.Create())
                    return BitConverter.ToString(sha.ComputeHash(fs));
            }
            catch { return null; }
        }

        /// <summary>字节内容哈希（与 HashFile 同算法），用于"重载后基准前移"。</summary>
        static string HashBytes(byte[] bytes)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA1.Create())
                    return BitConverter.ToString(sha.ComputeHash(bytes));
            }
            catch { return null; }
        }

        static void CacheLoadedAssemblies()
        {
            loadedByLocation.Clear();
            dllHashBase.Clear();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    string loc = asm.Location;
                    if (string.IsNullOrEmpty(loc)) continue;
                    string full = Path.GetFullPath(loc);
                    if (full.StartsWith(ModsRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        loadedByLocation[full] = asm;
                        generations[full] = new List<Assembly> { asm };
                        string h = HashFile(full);
                        if (h != null) dllHashBase[full] = h;
                    }
                }
                catch { /* 动态/非文件程序集无 Location */ }
            }
        }

        static void AttachWatchers()
        {
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                try
                {
                    string root = mod.RootDir;
                    foreach (string dir in FindAssembliesDirs(root))
                    {
                        var w = new FileSystemWatcher(dir, "*.dll")
                        {
                            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                                | NotifyFilters.Size,
                            IncludeSubdirectories = false
                        };
                        w.Changed += OnDllChanged;
                        w.Created += OnDllChanged;
                        w.EnableRaisingEvents = true;
                        watchers.Add(w);
                    }
                }
                catch { /* 单 mod 监视失败跳过 */ }
            }
        }

        static IEnumerable<string> FindAssembliesDirs(string modRoot)
        {
            var dirs = new List<string>();
            string root = Path.Combine(modRoot, "Assemblies");
            if (Directory.Exists(root)) dirs.Add(root);
            // 版本子目录：<root>/1.x/Assemblies
            foreach (string sub in Directory.GetDirectories(modRoot))
            {
                string name = Path.GetFileName(sub);
                if (name.Length == 3 && name[1] == '.' && char.IsDigit(name[0]) && char.IsDigit(name[2]))
                {
                    string asmDir = Path.Combine(sub, "Assemblies");
                    if (Directory.Exists(asmDir)) dirs.Add(asmDir);
                }
            }
            return dirs;
        }

        // ---- 状态 ----

        public static Dictionary<string, object> GetStatus()
        {
            var dlls = new List<Dictionary<string, object>>();
            foreach (var kv in loadedByLocation)
            {
                dlls.Add(new Dictionary<string, object>
                {
                    { "path", kv.Key },
                    { "assembly", kv.Value.GetName().Name },
                    { "version", kv.Value.GetName().Version.ToString() },
                    { "mtime", File.Exists(kv.Key) ? File.GetLastWriteTime(kv.Key).ToString("yyyy-MM-dd HH:mm:ss") : null }
                });
            }
            return new Dictionary<string, object>
            {
                { "watching", watching },
                { "dormant", !watching },
                { "enableHint", watching ? null
                    : "自动重载处于休眠（隐藏工具默认）。显式启用：agg_call_tool { tool: \"hotreload_watch\", args: { \"enabled\": true } }；"
                      + "或一次性手动重载：agg_call_tool { tool: \"hotreload_apply\", args: {} }" },
                { "limitations", Limitations },
                { "reloadCount", reloadCount },
                { "assemblies", dlls },
                { "history", history.Select(r => new Dictionary<string, object>
                    {
                        { "modName", r.modName }, { "mode", r.mode }, { "ok", r.ok },
                        { "message", r.message }, { "time", r.time },
                        { "affectedMethods", r.affectedMethods },
                        { "candidates", r.candidates }, { "detoured", r.detoured },
                        { "skipNoOld", r.skipNoOld }, { "skipPatched", r.skipPatched },
                        { "skipSignature", r.skipSignature }, { "skipStructure", r.skipStructure },
                        { "skipCompilerGenerated", r.skipCompilerGenerated },
                        { "skippedByType", r.skippedByType.Select(kv => kv.Key + " ×" + kv.Value + " —— "
                            + (r.skipReasonByType.TryGetValue(kv.Key, out string kk) ? kk : "")).ToList() },
                        { "skipNotReloadable", r.skipNotReloadable },
                        { "detourFailed", r.detourFailed }
                    }).ToList() }
            };
        }

        public static Dictionary<string, object> SetWatch(bool enabled)
        {
            watching = enabled;
            UEHttpLog.Info("[HotReload] 自动监视 " + (enabled ? "开启" : "关闭"));
            return Ok("watch", "自动监视已" + (enabled ? "开启" : "关闭"));
        }

        // ---- DLL 变化处理 ----

        /// <summary>
        /// DLL 变化回调（FileSystemWatcher 线程池线程）。
        /// 去抖在 watcher 线程做，**实际重打在游戏主线程执行**——mod `.cctor` 重放
        /// （RunClassConstructor）里普遍调用 Unity API，在后台线程会报
        /// "Tried to create a texture from a different thread."（2026-09-19 日志实证）。
        /// </summary>
        static void OnDllChanged(object sender, FileSystemEventArgs e)
        {
            if (!watching) return;
            try
            {
                if (!UEMainThreadDispatcher.IsReady)
                {
                    UEHttpLog.Warning("[HotReload] 主线程调度器未就绪，忽略本次 DLL 变化");
                    return;
                }
                string name = e != null ? e.Name : "?";
                System.Threading.Thread.Sleep(300);   // watcher 线程去抖，避免占用主线程
                UEMainThreadDispatcher.Enqueue(() =>
                {
                    try
                    {
                        UEHttpLog.Info("[HotReload] 检测到 DLL 变化（" + name + "），开始重打");
                        ApplyReload(null);
                    }
                    catch (Exception ex)
                    {
                        UEHttpLog.Error("[HotReload] OnDllChanged(main) failed: "
                            + ex.GetType().Name + ": " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                UEHttpLog.Error("[HotReload] OnDllChanged failed: " + ex);
            }
        }

        /// <summary>手动/自动重打入口。modId 为空 = 检测全部已变化 DLL；否则按 packageId 定位。</summary>
        public static Dictionary<string, object> ApplyReload(string modId)
        {
            try
            {
                var changed = FindChangedDlls(modId);
                if (changed.Count == 0)
                    return Ok("none", modId == null
                        ? "未检测到变化（已加载程序集的 DLL 内容哈希与基线一致）"
                        : "modId '" + modId + "' 未匹配到已加载程序集");

                var records = new List<ReloadRecord>();
                foreach (var pair in changed)
                {
                    records.Add(ReloadOne(pair.Key, pair.Value));
                    reloadCount++;
                }
                foreach (var r in records)
                {
                    history.Add(r);
                    if (history.Count > HistoryMax) history.RemoveAt(0);
                    string status = r.ok ? "OK" : "REJECTED";
                    UEHttpLog.Info($"[HotReload] {r.modName} {r.mode} {status}: {r.message}");
                }
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "mode", records.Count == 1 ? records[0].mode : "multi" },
                    { "reloadCount", reloadCount },
                    { "results", records.Select(r => new Dictionary<string, object>
                        {
                            { "modName", r.modName }, { "mode", r.mode }, { "ok", r.ok },
                            { "message", r.message }, { "affectedMethods", r.affectedMethods },
                            { "candidates", r.candidates }, { "detoured", r.detoured },
                            { "skipNoOld", r.skipNoOld }, { "skipPatched", r.skipPatched },
                            { "skipSignature", r.skipSignature }, { "skipStructure", r.skipStructure },
                        { "skipCompilerGenerated", r.skipCompilerGenerated },
                        { "skippedByType", r.skippedByType.Select(kv => kv.Key + " ×" + kv.Value + " —— "
                            + (r.skipReasonByType.TryGetValue(kv.Key, out string kk) ? kk : "")).ToList() },
                            { "skipNotReloadable", r.skipNotReloadable },
                            { "detourFailed", r.detourFailed }
                        }).ToList() }
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "success", false }, { "error", ex.GetType().Name + ": " + ex.Message },
                    { "errorCode", "HOTRELOAD_INTERNAL" }
                };
            }
        }

        /// <summary>按 Location 找"DLL 文件 mtime 晚于 Initialize 基准时刻"的条目。</summary>
        static List<KeyValuePair<string, Assembly>> FindChangedDlls(string modId)
        {
            var result = new List<KeyValuePair<string, Assembly>>();
            foreach (var kv in loadedByLocation)
            {
                try
                {
                    if (modId != null)
                    {
                        string modRoot = FindModRootForAssembly(kv.Value);
                        if (modRoot == null) continue;
                        string pid = ModPackageIdForRoot(modRoot);
                        if (!string.Equals(pid, modId, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    if (!File.Exists(kv.Key)) continue;
                    // 跳过本模组自身程序集（UELoader.dll）：热重载"正在运行的调试工具自身"没有意义，
                    // 且会让 HotReloadManager 重写自己的方法/状态（自体重载后行为不可预测）
                    if (kv.Value == SelfAssembly)
                    {
                        if (selfSkipNoticed == false)
                        {
                            selfSkipNoticed = true;
                            UEHttpLog.Info("[HotReload] 跳过自身程序集（UELoader.dll）：工具不重载自己，"
                                + "本模组的改动请重启游戏生效");
                        }
                        continue;
                    }
                    if (!dllHashBase.TryGetValue(kv.Key, out string baseHash)) continue;
                    string nowHash = HashFile(kv.Key);
                    if (nowHash != null && !string.Equals(nowHash, baseHash, StringComparison.Ordinal))
                        result.Add(kv);
                }
                catch { }
            }
            return result;
        }

        static readonly System.Reflection.FieldInfo _modAssembliesField =
            typeof(ModContentPack).GetField("assemblies",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly System.Reflection.FieldInfo _loadedAssembliesField =
            typeof(ModAssemblyHandler).GetField("loadedAssemblies",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static string FindModRootForAssembly(Assembly asm)
        {
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                try
                {
                    object handler = _modAssembliesField?.GetValue(mod);
                    if (handler == null) continue;
                    var list = _loadedAssembliesField?.GetValue(handler) as System.Collections.IEnumerable;
                    if (list == null) continue;
                    foreach (object a in list)
                        if (a is Assembly loaded && loaded == asm) return mod.RootDir;
                }
                catch { }
            }
            return null;
        }

        static string ModPackageIdForRoot(string modRoot)
        {
            try
            {
                string about = Path.Combine(modRoot, "About", "About.xml");
                if (!File.Exists(about)) return null;
                string text = File.ReadAllText(about);
                var m = System.Text.RegularExpressions.Regex.Match(text,
                    @"<packageId>\s*([^<]+?)\s*</packageId>");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            catch { return null; }
        }

        // ---- 重打 ----

        static ReloadRecord ReloadOne(string dllPath, Assembly oldAsm)
        {
            var rec = new ReloadRecord { modName = oldAsm.GetName().Name };
            try
            {
                byte[] bytes = File.ReadAllBytes(dllPath);
                Assembly fresh = Assembly.Load(bytes);
                // 基准必须随"最新已加载代"前移：否则来回切换构建（A→B→A）时，
                // 第二次 A 会因"内容 == 启动时的基准"而被判为未变化 → 漏重载（实测踩到）
                string newHash = HashBytes(bytes);
                if (newHash != null) dllHashBase[dllPath] = newHash;

                if (TryGetTypes(fresh) == null)
                    return Reject(rec, "mod", "新程序集 GetTypes 失败（损坏或依赖缺失）");

                var patchTypes = FindHarmonyPatchTypes(fresh);
                var startupTypes = FindStaticConstructorStartupTypes(fresh);

                if (patchTypes.Count > 0 || startupTypes.Count > 0)
                    return ReloadAsMod(rec, dllPath, oldAsm, fresh, patchTypes, startupTypes);
                return ReloadAsMethod(rec, oldAsm, fresh);
            }
            catch (Exception ex)
            {
                return Reject(rec, "mod", "加载失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static ReloadRecord Reject(ReloadRecord rec, string mode, string msg)
        {
            rec.mode = mode; rec.ok = false; rec.message = msg; return rec;
        }

        static Type[] TryGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException) { return null; }
            catch { return null; }
        }

        static List<Type> FindHarmonyPatchTypes(Assembly asm)
        {
            var list = new List<Type>();
            foreach (Type t in asm.GetTypes())
            {
                try
                {
                    if (t.GetCustomAttributes(false).Any(a =>
                        a.GetType().Name == "HarmonyPatch" ||
                        a.GetType().Name == "HarmonyPatchAll")) list.Add(t);
                }
                catch { }
            }
            return list;
        }

        static List<Type> FindStaticConstructorStartupTypes(Assembly asm)
        {
            var list = new List<Type>();
            foreach (Type t in asm.GetTypes())
            {
                try
                {
                    if (t.GetCustomAttributes(false).Any(a =>
                        a.GetType().Name == "StaticConstructorOnStartup")) list.Add(t);
                }
                catch { }
            }
            return list;
        }

        /// <summary>取某 DLL 的历代程序集列表（不存在则用启动代初始化）。</summary>
        static List<Assembly> GetGenerationsFor(string dllPath, Assembly oldAsm)
        {
            lock (generations)
            {
                if (!generations.TryGetValue(dllPath, out List<Assembly> gens))
                {
                    gens = new List<Assembly> { oldAsm };
                    generations[dllPath] = gens;
                }
                else if (!gens.Contains(oldAsm))
                {
                    gens.Insert(0, oldAsm);
                }
                return gens;
            }
        }

        /// <summary>读程序集类型表；部分类型加载失败时返回能读到的部分，绝不抛。</summary>
        static Type[] SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null).ToArray();
            }
            catch { return new Type[0]; }
        }

        enum DetourOutcome
        {
            Detoured, SkipNoOld, SkipPatched, SkipSignature, SkipStructure, SkipCompilerGenerated,
            SkipNotReloadable, Failed
        }

        /// <summary>从布局变化原因里取出"编译器生成类型"的全名（DescribeLayoutChange 的固定格式）。</summary>
        static readonly System.Text.RegularExpressions.Regex CompilerTypeInReason =
            new System.Text.RegularExpressions.Regex("编译器生成类型布局变化: (?<t>[^（]+)");

        /// <summary>
        /// 单个新方法的 detour 判定与执行（两条重打路径共用）。
        /// 任何异常都转 Failed 并记 Warning，绝不向外抛（异常隔离到单方法）。
        /// </summary>
        static DetourOutcome TryDetourMethod(ReloadRecord rec, Type oldType, MethodInfo freshMethod,
            HashSet<MethodBase> skipPatched)
        {
            try
            {
                if (HotReloadValidator.ShouldSkipMethod(freshMethod)) return DetourOutcome.SkipNotReloadable;

                MethodInfo oldMethod = HotReloadValidator.FindMatchingMethod(oldType, freshMethod);
                if (oldMethod == null) return DetourOutcome.SkipNoOld;
                if (skipPatched != null && skipPatched.Contains(oldMethod)) return DetourOutcome.SkipPatched;
                if (!HotReloadValidator.SignaturesMatch(oldMethod, freshMethod)) return DetourOutcome.SkipSignature;

                string reason;
                if (!HotReloadValidator.FieldAccessSafe(oldMethod, freshMethod, out reason))
                {
                    // 编译器生成状态机（迭代器/异步）的布局随方法体改变，属**预期内**的保守拒绝：
                    // 逐条 Warning 会刷屏（2026-09-20 TEXT20 实测 34 条 ×2 镜像 = 68 行），
                    // 改为分类计数 + 调用方一条汇总；其余布局变化仍逐条 Warning（罕见且可操作）
                    if (reason != null && reason.StartsWith("编译器生成类型布局变化", StringComparison.Ordinal))
                    {
                        var mm = CompilerTypeInReason.Match(reason);
                        if (mm.Success) rec.skippedCompilerTypes.Add(mm.Groups["t"].Value.Trim());
                        return DetourOutcome.SkipCompilerGenerated;
                    }
                    NoteSkip(rec, freshMethod.DeclaringType, reason, null);
                    return DetourOutcome.SkipStructure;
                }

                string err = HotReloadDetour.Detour(oldMethod, freshMethod);
                if (err != null)
                {
                    NoteSkip(rec, freshMethod.DeclaringType, "写跳板失败", err);
                    return DetourOutcome.Failed;
                }
                rec.affectedMethods.Add(freshMethod.DeclaringType.FullName + ":" + freshMethod.Name);
                return DetourOutcome.Detoured;
            }
            catch (Exception ex)
            {
                NoteSkip(rec, freshMethod.DeclaringType, "判定异常", ex.GetType().Name + ": " + ex.Message);
                return DetourOutcome.Failed;
            }
        }

        /// <summary>
        /// 逐类型逐方法 detour（两条路径共用）。类型级与方法级各自隔离异常，
        /// 单点失败不会中止整轮（原实现把 try/catch 包在双层循环外，一次 AmbiguousMatch 就全盘放弃）。
        /// </summary>
        static void DetourMethods(ReloadRecord rec, Assembly oldAsm, Assembly fresh,
            HashSet<MethodBase> skipPatched)
        {
            var oldTypes = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (Type t in SafeGetTypes(oldAsm))
                if (t != null && !oldTypes.ContainsKey(t.FullName)) oldTypes[t.FullName] = t;

            foreach (Type freshType in SafeGetTypes(fresh))
            {
                if (freshType == null) continue;
                try
                {
                    oldTypes.TryGetValue(freshType.FullName, out Type oldType);
                    foreach (MethodInfo freshMethod in HotReloadValidator.EnumerateDeclaredMethods(freshType))
                    {
                        rec.candidates++;
                        DetourOutcome outcome = oldType == null
                            ? DetourOutcome.SkipNoOld
                            : TryDetourMethod(rec, oldType, freshMethod, skipPatched);
                        switch (outcome)
                        {
                            case DetourOutcome.Detoured: rec.detoured++; break;
                            case DetourOutcome.SkipNoOld: rec.skipNoOld++; break;
                            case DetourOutcome.SkipPatched: rec.skipPatched++; break;
                            case DetourOutcome.SkipSignature: rec.skipSignature++; break;
                            case DetourOutcome.SkipStructure: rec.skipStructure++; break;
                            case DetourOutcome.SkipCompilerGenerated:
                                rec.skipStructure++; rec.skipCompilerGenerated++; break;
                            case DetourOutcome.SkipNotReloadable: rec.skipNotReloadable++; break;
                            default: rec.detourFailed++; break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    UEHttpLog.Warning("[HotReload] 类型 " + freshType.FullName + " 判定异常: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>记一条跳过/失败账目（按类型聚合，不在此处打印）。</summary>
        static void NoteSkip(ReloadRecord rec, Type type, string reasonKind, string detail)
        {
            string tn = type != null ? type.FullName : "?";
            rec.skippedByType[tn] = (rec.skippedByType.TryGetValue(tn, out int c) ? c : 0) + 1;
            if (!rec.skipReasonByType.ContainsKey(tn))
                rec.skipReasonByType[tn] = reasonKind + (string.IsNullOrEmpty(detail) ? "" : "（" + detail + "）");
        }

        /// <summary>
        /// 跳过/失败的**按类型聚合**汇总：每个类型在**本会话内只详报一次**，之后仅计数
        /// （计数始终在汇总行 `skipped{...}` 与 hotreload_status 里可见）。
        /// 动机（TEXT20 实测）：19 轮重载累计 95 条逐方法 Warning，UE 日志镜像再翻倍 ≈190 行，
        /// 且每轮重建都重复同一批类型 —— 聚合+去重后首次每类型 1 行、后续 0 行。
        /// </summary>
        static void LogSkipSummary(ReloadRecord rec)
        {
            if (rec.skippedByType.Count == 0) return;
            var freshEntries = new List<string>();
            foreach (var kv in rec.skippedByType)
            {
                if (!loggedSkipTypes.Add(kv.Key)) continue;   // 已详报过 → 只计数
                string kind = rec.skipReasonByType.TryGetValue(kv.Key, out string k) ? k : "跳过";
                freshEntries.Add(kv.Key + " ×" + kv.Value + " —— " + kind);
            }
            if (freshEntries.Count == 0) return;
            UEHttpLog.Info("[HotReload] 本次跳过/失败的类型（同一类型每会话只详报一次，计数见汇总行）："
                + string.Join("；", freshEntries.Take(10))
                + (freshEntries.Count > 10 ? "；…等 " + freshEntries.Count + " 个类型" : "")
                + " —— 字段布局变化的类型需重启后生效，或把改动逻辑外移到普通方法");
        }

        /// <summary>detour 统计的可读描述（含跳过原因分解，供 message / status 展示）。</summary>
        static string DescribeDetour(ReloadRecord rec)
            => "candidates=" + rec.candidates + ", detoured=" + rec.detoured
             + ", skipped{noOld=" + rec.skipNoOld + ", patched=" + rec.skipPatched
             + ", signature=" + rec.skipSignature
             + ", structure=" + rec.skipStructure
             + (rec.skipCompilerGenerated > 0 ? "(compilerGen=" + rec.skipCompilerGenerated + ")" : "")
             + ", notReloadable=" + rec.skipNotReloadable + "}"
             + (rec.detourFailed > 0 ? ", detourFailed=" + rec.detourFailed : "");

        /// <summary>mod 重打：精确卸载旧程序集的 Harmony patch → 静态构造重放 → PatchAll → 对未被 patch 的普通方法做 detour。
        /// hybrid 模式：mod 重打处理 patch 链，detour 处理已有实例的普通方法体（Mono JIT 缓存）。</summary>
        static ReloadRecord ReloadAsMod(ReloadRecord rec, string dllPath, Assembly oldAsm, Assembly fresh,
            List<Type> patchTypes, List<Type> startupTypes)
        {
            try
            {
                var harmony = new HarmonyLib.Harmony("UESDdebuger.HotReload");
                List<Assembly> gens = GetGenerationsFor(dllPath, oldAsm);
                // 全局补丁目标：必须用静态 GetAllPatchedMethods（实例 GetPatchedMethods 是 owner 作用域的，
                // 新实例返回 0 —— 实测 0Harmony 2.4.1）。否则历代补丁永不被发现 → 跨代累积。
                var allTargets = HotReloadValidator.AllPatchedMethods(harmony);
                // 按"单个 patch 方法"精确卸载来自历代程序集的补丁（不按 owner 全量，避免误删
                // 同 owner 但属于未重载程序集——如 mod 附属 dll——的补丁）
                var pairs = HotReloadValidator.CollectPatchesFromAssemblies(allTargets, gens);
                int unpatched = 0;
                foreach (var kv in pairs)
                {
                    try { harmony.Unpatch(kv.Key, kv.Value); unpatched++; }
                    catch (Exception ex)
                    {
                        UEHttpLog.Warning("[HotReload] 卸载 patch 失败 " + kv.Key.Name + " ← "
                            + kv.Value.DeclaringType.FullName + "." + kv.Value.Name + ": " + ex.Message);
                    }
                }
                var mine = HotReloadValidator.FilterPatchesByAssembly(allTargets, gens);
                rec.message = "已卸载旧 patch " + unpatched + " 处";

                foreach (Type t in startupTypes)
                {
                    try { System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(t.TypeHandle); }
                    catch (Exception ex) { UEHttpLog.Warning("[HotReload] 静态构造重放失败 " + t.FullName + ": " + ex.Message); }
                }

                harmony.PatchAll(fresh);
                lock (generations) { if (!gens.Contains(fresh)) gens.Add(fresh); }

                foreach (MethodBase m in mine)
                    rec.affectedMethods.Add(m.DeclaringType.FullName + "." + m.Name);

                // ---- hybrid: 对未被 Harmony patch 的普通方法做 detour ----
                // mod 重打只刷新了 patch 链；已有实例的普通方法体（JIT 缓存）不受 PatchAll 影响，
                // 必须通过 detour 跳板重定向到新方法体，否则旧对象仍执行旧逻辑。
                var patchedSet = new HashSet<MethodBase>(HotReloadValidator.AllPatchedMethods(harmony));
                DetourMethods(rec, oldAsm, fresh, patchedSet);
                LogSkipSummary(rec);

                rec.ok = true;
                rec.mode = "mod";
                rec.message = (rec.message ?? "") + "，重打完成（patchTypes=" + patchTypes.Count
                    + ", startupTypes=" + startupTypes.Count + ", " + DescribeDetour(rec) + "）";
                return rec;
            }
            catch (Exception ex)
            {
                return Reject(rec, "mod", "重打失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// method 兜底：签名 + 字段安全校验后 detour 旧方法 → 新方法。
        /// 语义变更（2026-09-19 修复）：结构变化的方法**逐个跳过并计入统计**，不再因单个方法
        /// 直接 Reject 整轮（原实现遇第一个引用字段的方法就整轮拒绝，导致真实 mod 完全无法重载）。
        /// </summary>
        static ReloadRecord ReloadAsMethod(ReloadRecord rec, Assembly oldAsm, Assembly fresh)
        {
            try
            {
                // GetPatchedMethods() 在 Harmony 2.3.x 是实例方法：借一个 id 容器实例枚举全进程 patch 目标
                var patched = new HashSet<MethodBase>(
                    HotReloadValidator.AllPatchedMethods(new HarmonyLib.Harmony("UESDdebuger.HotReload")));
                DetourMethods(rec, oldAsm, fresh, patched);
                LogSkipSummary(rec);

                rec.mode = "method";
                rec.ok = rec.detoured > 0;
                if (rec.ok)
                {
                    rec.message = "detour 完成 " + rec.detoured + " 个方法（" + DescribeDetour(rec) + "）";
                }
                else if (rec.skipStructure > 0)
                {
                    rec.message = "未重载：结构变化方法 " + rec.skipStructure + " 个（需重启）（"
                        + DescribeDetour(rec) + "）";
                }
                else
                {
                    rec.message = "未找到可 detour 的同名方法（" + DescribeDetour(rec) + "）";
                }
                return rec;
            }
            catch (Exception ex)
            {
                return Reject(rec, "method", "method 重打失败: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static Dictionary<string, object> Ok(string mode, string message) =>
            new Dictionary<string, object> { { "success", true }, { "mode", mode }, { "message", message } };
    }
}

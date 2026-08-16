using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityExplorer;
using UnityExplorer.CSConsole;
using UnityExplorer.Hooks;
using UnityExplorer.UI;
using RimWorld;
using Verse;

namespace UELoader
{
    /// <summary>
    /// UE HTTP 服务端核心逻辑：强类型调用 UnityExplorer 4.9.0 运行时 API
    /// （InspectorManager.Inspect / ConsoleController.Evaluate / HookInstance / UIManager 状态），
    /// 供 UESDdebuger/MCP 的 11 个 UE 工具调用。所有 UE UI/补丁操作经 UEMainThreadDispatcher
    /// 投递到游戏主线程执行；日志通过订阅 ExplorerStandalone.OnLog 事件维护环形缓冲。
    ///
    /// 响应统一结构：{ success, data?, error?, errorCode? }（与 MCP 端 callUnityExplorerAPI 契约一致）。
    /// 端点契约参见 UESDdebuger/MCP/index.js 各 UE 工具 case。
    /// </summary>
    public static class UEHttpHandler
    {
        const int LogBufferMax = 300;

        static readonly object LogLock = new object();
        static readonly List<Dictionary<string, object>> LogBuffer = new List<Dictionary<string, object>>();
        static int logSeq;
        static bool listening;
        static bool mainDispatcherReady;

        public static void Init()
        {
            if (listening && mainDispatcherReady)
                return;

            try
            {
                // 订阅日志流：可重复安全（+= 幂等性弱，但只在 listening 为 false 时执行一次）
                if (!listening)
                {
                    ExplorerStandalone.OnLog += OnUELogBuffer;
                    listening = true;
                }

                // 主线程调度器：Mod 构造阶段（非主线程）EnsureInitialized 会失败，
                // 因此这里必须允许在场景加载（主线程）时重试，直到 mainDispatcherReady。
                if (!mainDispatcherReady)
                {
                    UEMainThreadDispatcher.EnsureInitialized();
                    mainDispatcherReady = true;
                }

                UEHttpLog.Message("[UEHttp] UEHttpHandler ready: UnityExplorer 4.9.0 HTTP API");
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[UEHttp] UEHttpHandler init failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------- 日志缓冲

        static void OnUELogBuffer(string message, LogType logType)
        {
            lock (LogLock)
            {
                LogBuffer.Add(new Dictionary<string, object>
                {
                    { "index", logSeq++ },
                    { "message", message },
                    { "type", LogTypeName(logType) },
                    { "timestamp", DateTime.Now.ToString("O") }
                });
                if (LogBuffer.Count > LogBufferMax)
                    LogBuffer.RemoveAt(0);
            }
        }

        static string LogTypeName(LogType t)
        {
            switch (t)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    return "Error";
                case LogType.Warning:
                    return "Warning";
                default:
                    return "Log";
            }
        }

        public static Dictionary<string, object> GetLogs(int count)
        {
            List<Dictionary<string, object>> logs;
            lock (LogLock)
            {
                int skip = Math.Max(0, LogBuffer.Count - count);
                logs = LogBuffer.Skip(skip).Take(count).ToList();
            }
            return Success(new Dictionary<string, object> { { "logs", logs }, { "totalCount", logs.Count } });
        }

        public static Dictionary<string, object> ClearLogs()
        {
            lock (LogLock)
            {
                LogBuffer.Clear();
                logSeq = 0;
            }
            return Success(new Dictionary<string, object> { { "message", "Logs cleared" } });
        }

        // ---------------------------------------------------------------- 端点实现

        public static Dictionary<string, object> InspectType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return Error(400, "typeName is required", "MISSING_PARAMETER");

            if (!IsUIReady())
                return Error(503, "UnityExplorer UI not ready (游戏内按 F7 打开过 UE 面板后再试，或稍后重试)", "UI_NOT_READY");

            Type type = ResolveType(typeName);
            if (type == null)
                return Error(404, $"Type '{typeName}' not found", "TYPE_NOT_FOUND");

            string err = null;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        InspectorManager.Inspect(type);
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                        Log.Error($"[UEHttp] InspectType failed: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            if (err != null)
                return Error(500, err, "INSPECT_ERROR");

            return Success(new Dictionary<string, object>
            {
                { "typeName", typeName },
                { "message", "Type inspection opened" }
            });
        }

        public static Dictionary<string, object> ExecuteCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return Error(400, "code is required", "MISSING_PARAMETER");

            if (!IsConsoleReady(out string consoleReason))
                return Error(503, consoleReason, "CONSOLE_NOT_READY");

            // 临时订阅 UE 日志流，捕获 Evaluate 的结果/错误输出
            var captured = new List<string>();
            Action<string, LogType> listener = (m, t) =>
            {
                lock (captured)
                    captured.Add(m);
            };

            ExplorerStandalone.OnLog += listener;
            string err = null;
            // executionTimeMs 由主线程（闭包内）写、HTTP 线程（闭包外）读；
            // ExecuteOnMainThread 同步阻塞等待主线程完成后才返回，读发生在返回前，
            // 由此建立 happens-before，普通 long 跨线程读是安全的。
            long executionTimeMs = 0;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        ConsoleController.Evaluate(code, false);
                        sw.Stop();
                        executionTimeMs = sw.ElapsedMilliseconds;
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                        Log.Error($"[UEHttp] ExecuteCode failed: {ex}");
                    }
                }, 20000);
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
            finally
            {
                ExplorerStandalone.OnLog -= listener;
            }

            if (err != null)
                return Error(400, err, "COMPILATION_ERROR");

            // 解析捕获的日志行
            string result = null;
            string errorMsg = null;
            List<string> outputLines = new List<string>();
            lock (captured)
            {
                foreach (string m in captured)
                {
                    if (m != null && m.StartsWith("Invoked REPL, result:", StringComparison.Ordinal))
                        result = m.Substring("Invoked REPL, result:".Length).Trim();
                    else if (m != null && (m.Contains("Unable to compile") || m.Contains("Exception invoking REPL")))
                        errorMsg = m;
                    else if (m != null && m.Length > 0)
                        outputLines.Add(m);
                }
            }

            string output = string.Join("\n", outputLines.ToArray());
            if (string.IsNullOrEmpty(output))
                output = null;

            if (errorMsg != null)
                return Error(400, errorMsg, "COMPILATION_ERROR", new Dictionary<string, object> { { "output", output } });

            return Success(new Dictionary<string, object>
            {
                { "result", result },
                { "output", output },
                { "executionTime", executionTimeMs }
            });
        }

        public static Dictionary<string, object> ResetConsole()
        {
            if (!IsConsoleReady(out string consoleReason))
                return Error(503, consoleReason, "CONSOLE_NOT_READY");

            string err = null;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        ConsoleController.ResetConsole();
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                    }
                });
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            if (err != null)
                return Error(400, err, "RESET_ERROR");

            return Success(new Dictionary<string, object> { { "message", "C# Console reset successfully" } });
        }

        public static Dictionary<string, object> AddUsing(string namespaceName)
        {
            if (string.IsNullOrEmpty(namespaceName))
                return Error(400, "namespace is required", "MISSING_PARAMETER");

            if (!IsConsoleReady(out string consoleReason))
                return Error(503, consoleReason, "CONSOLE_NOT_READY");

            string err = null;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        ConsoleController.AddUsing(namespaceName);
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                    }
                });
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            if (err != null)
                return Error(400, err, "ADD_USING_ERROR");

            return Success(new Dictionary<string, object>
            {
                { "namespaceName", namespaceName },
                { "message", "Using directive added successfully" }
            });
        }

        public static Dictionary<string, object> GetStatus()
        {
            var status = new Dictionary<string, object>
            {
                { "uiReady", false },
                { "uiRoot", false },
                { "navBar", false },
                { "showMenu", false },
                { "unityExplorerVersion", "4.9.0" }
            };

            // 游戏状态块（不依赖 RIMAPI）：直读 Verse 静态字段，供 MCP 的 start_game
            // （游戏初始化完成/主菜单就绪）与 start_quick_test（世界 tick 走动）判定。
            // 注意：HTTP 线程直接读取纯静态字段/引用（主菜单时主线程调度器不可用，无法投递主线程）；
            // Find.TickManager 在 Current.Game 为 null 时会抛 NRE，必须先判空。各读取包 try/catch。
            var gameStatus = new Dictionary<string, object>
            {
                { "programState", "Unknown" },
                { "loading", true },
                { "inGame", false },
                { "mainMenu", false },
                { "gameTick", 0 },
                { "paused", false }
            };
            try
            {
                bool loading = LongEventHandler.AnyEventNowOrWaiting;
                ProgramState ps = Current.ProgramState;
                bool inGame = Current.Game != null && ps == ProgramState.Playing;
                int gameTick = 0;
                bool paused = false;
                if (Current.Game != null && Current.Game.tickManager != null)
                {
                    gameTick = Current.Game.tickManager.TicksGame;
                    paused = Current.Game.tickManager.Paused;
                }
                gameStatus["programState"] = ps.ToString();
                gameStatus["loading"] = loading;
                gameStatus["inGame"] = inGame;
                gameStatus["mainMenu"] = ps == ProgramState.Entry && !loading && !inGame;
                gameStatus["gameTick"] = gameTick;
                gameStatus["paused"] = paused;
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UEHttp] GetStatus game block failed: {ex.Message}");
            }
            status["game"] = gameStatus;

            // 主菜单下 UE 尚未初始化、主线程调度器也不可用（Mod 构造非主线程，见 UEHttpServer.Start 注释）。
            // 状态读取只是读静态字段/属性，无需主线程投递：直接读取，各字段包 try/catch
            // （UE 静态属性可能触发其静态初始化，若需要主线程则抛异常 → 回落默认 false）。
            try
            {
                bool uiRoot = false;
                bool navBar = false;
                bool showMenu = false;
                bool consoleOk = false;
                try { uiRoot = UIManager.UIRoot != null; } catch { }
                try { navBar = UIManager.NavBarRect != null; } catch { }
                try { showMenu = UIManager.ShowMenu; } catch { }
                try { consoleOk = !ConsoleController.SRENotSupported && ConsoleController.Evaluator != null; } catch { }

                status["uiRoot"] = uiRoot;
                status["navBar"] = navBar;
                status["showMenu"] = showMenu;
                status["uiReady"] = uiRoot && navBar;
                status["panels"] = new Dictionary<string, object>
                {
                    { "inspector", uiRoot },
                    { "console", uiRoot && consoleOk },
                    { "log", uiRoot },
                    { "hookManager", uiRoot },
                    { "objectExplorer", uiRoot }
                };
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UEHttp] GetStatus failed: {ex.Message}");
            }

            return Success(new Dictionary<string, object> { { "status", status } });
        }

        public static Dictionary<string, object> CreateHook(string typeName, string methodName, string patchType, string patchCode)
        {
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
                return Error(400, "typeName and methodName are required", "MISSING_PARAMETER");

            if (!IsUIReady())
                return Error(503, "UnityExplorer UI not ready", "UI_NOT_READY");

            Type type = ResolveType(typeName);
            if (type == null)
                return Error(404, $"Type '{typeName}' not found", "TYPE_NOT_FOUND");

            MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName);
            if (method == null)
                return Error(404, $"Method '{methodName}' not found in type '{typeName}'", "METHOD_NOT_FOUND");

            string hookId = null;
            string err = null;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        var hook = new HookInstance(method);

                        if (!string.IsNullOrEmpty(patchCode))
                        {
                            if (!hook.CompileAndGenerateProcessor(patchCode))
                            {
                                err = "Failed to compile patch code";
                                return;
                            }
                            hook.Patch();
                        }

                        hookId = UEHookStore.Store(hook, $"{typeName}.{methodName}", typeName, methodName, patchType ?? "Postfix", patchCode);
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                        Log.Error($"[UEHttp] CreateHook failed: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            if (hookId == null)
                return Error(400, err ?? "Failed to create hook", "HOOK_CREATE_ERROR");

            return Success(new Dictionary<string, object>
            {
                { "hookId", hookId },
                { "targetMethod", $"{typeName}.{methodName}" },
                { "patchType", patchType ?? "Postfix" },
                { "enabled", true }
            });
        }

        public static Dictionary<string, object> ToggleHook(string hookId, bool enabled)
        {
            if (string.IsNullOrEmpty(hookId))
                return Error(400, "hookId is required", "MISSING_PARAMETER");

            var entry = UEHookStore.Get(hookId);
            if (entry == null)
                return Error(404, $"Hook '{hookId}' not found", "HOOK_NOT_FOUND");

            string err = null;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        var hook = entry.Instance as HookInstance;
                        if (hook == null)
                            throw new InvalidOperationException("Stored instance is not a HookInstance");

                        if (enabled)
                            hook.Patch();
                        else
                            hook.Unpatch();

                        entry.Enabled = enabled;
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                    }
                });
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            if (err != null)
                return Error(500, err, "TOGGLE_ERROR");

            return Success(new Dictionary<string, object>
            {
                { "hookId", hookId },
                { "enabled", enabled }
            });
        }

        public static Dictionary<string, object> DeleteHook(string hookId)
        {
            if (string.IsNullOrEmpty(hookId))
                return Error(400, "hookId is required", "MISSING_PARAMETER");

            if (!UEHookStore.Remove(hookId))
                return Error(404, $"Hook '{hookId}' not found", "HOOK_NOT_FOUND");

            return Success(new Dictionary<string, object>
            {
                { "hookId", hookId },
                { "message", "Hook deleted" }
            });
        }

        public static Dictionary<string, object> ListHooks()
        {
            var hooks = new List<Dictionary<string, object>>();
            foreach (var entry in UEHookStore.GetAll())
            {
                hooks.Add(new Dictionary<string, object>
                {
                    { "hookId", entry.HookId },
                    { "targetMethod", entry.TargetMethod },
                    { "declaringType", entry.DeclaringType },
                    { "methodName", entry.MethodName },
                    { "patchType", entry.PatchType },
                    { "enabled", entry.Enabled },
                    { "createdAt", entry.CreatedAt.ToString("O") }
                });
            }

            return Success(new Dictionary<string, object> { { "hooks", hooks }, { "totalCount", hooks.Count } });
        }

        /// <summary>
        /// 快速测试：主菜单下调用官方 DevQuickTest 流程（Root_Play.SetupForQuickTestPlay + PageUtility.InitGameStart）
        /// 进入测试地图。
        /// 不依赖主线程调度器：Mod 构造阶段启动的 HTTP 服务在主菜单时调度器不可用
        /// （EnsureInitialized 需主线程 new GameObject，见 UEHttpServer.Start 注释），
        /// 而 LongEventHandler.QueueLongEvent 是线程安全的官方 API（内部加锁、由游戏主循环消费），
        /// Current.Root/Current.Game 在主菜单空闲态下的读取也是安全的。
        /// 本方法立即返回，MCP 侧自行轮询 inGame。
        /// </summary>
        public static Dictionary<string, object> TriggerQuickTest()
        {
            try
            {
                // 仅主菜单（游戏主循环已建立、尚未进地图）允许启动快速测试：
                // 拒绝条件 = 已进入地图（Root 为 Root_Play 且 Game 已创建）
                if (Current.Root is Verse.Root_Play && Current.Game != null)
                {
                    return Error(400, "已在游戏内", "ALREADY_IN_GAME");
                }

                // 与 MainMenuDrawer.DevQuickTest 相同流程（官方快速测试入口）
                LongEventHandler.QueueLongEvent(() =>
                {
                    Root_Play.SetupForQuickTestPlay();
                    PageUtility.InitGameStart();
                    Log.Message("[UEHttp] Quick test triggered: entering test map...");
                }, "Starting Quick Test", doAsynchronously: false, null);

                return Success(new Dictionary<string, object> { { "message", "Quick test triggered" } });
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[UEHttp] TriggerQuickTest failed: {ex}");
                return Error(400, "Quick test failed: " + ex.Message, "QUICKTEST_FAILED");
            }
        }

        // ---------------------------------------------------------------- 主线程投递辅助

        /// <summary>
        /// 在主线程执行并返回结果字典（供 DebugAction/MapStructure 等访问游戏状态的端点使用；
        /// 内部调用必须在 HTTP 线程，投递经 UEMainThreadDispatcher）。
        /// </summary>
        public static Dictionary<string, object> RunOnMain(Func<Dictionary<string, object>> fn)
        {
            Dictionary<string, object> result = null;
            string err = null;
            try
            {
                // 首次调用需构建完整 DebugAction 树（动态子菜单展开），实测可超 60 秒。
                // 若超时过短会误报 MAIN_THREAD_ERROR，但动作仍在主线程继续执行并缓存整棵树，
                // 之后同类调用毫秒级返回（重试即可）。超时不宜过长：主线程长时间冻结会被
                // Windows 判定为挂起（AppHangB1）并可能结束进程，故维持 60s 让首次调用快速失败、
                // 后台继续建树。后续可用一个“构建中”状态提示替代，暂以重试方案。
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        result = fn();
                    }
                    catch (Exception ex)
                    {
                        err = ex.Message;
                    }
                }, 60000);
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            if (err != null)
                return Error(500, err, "MAIN_THREAD_ERROR");

            return result;
        }

        // ---------------------------------------------------------------- 辅助

        static bool IsUIReady()
        {
            bool ok = false;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try { ok = UIManager.UIRoot != null; } catch { }
                });
            }
            catch { }
            return ok;
        }

        static bool IsConsoleReady(out string reason)
        {
            reason = null;
            bool ready = false;
            string localReason = null;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    try
                    {
                        if (ConsoleController.SRENotSupported)
                        {
                            localReason = "C# Console is disabled (SRE not supported)";
                            return;
                        }
                        if (ConsoleController.Evaluator == null)
                        {
                            localReason = "C# Console not initialized yet (先打开一次 UE 的 C# Console 面板，或稍后重试)";
                            return;
                        }
                        ready = true;
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                localReason = ex.Message;
            }
            reason = localReason;
            return ready;
        }

        static Type ResolveType(string typeName)
        {
            try
            {
                Type type = Type.GetType(typeName);
                if (type != null) return type;

                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        type = assembly.GetType(typeName);
                        if (type != null) return type;
                    }
                    catch { }
                }

                if (typeName.IndexOf('.') < 0)
                {
                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            foreach (Type t in assembly.GetTypes())
                            {
                                if (t.Name == typeName)
                                    return t;
                            }
                        }
                        catch { }
                    }
                }

                string[] commonNamespaces = { "Verse", "RimWorld", "System", "UnityEngine" };
                foreach (string ns in commonNamespaces)
                {
                    string fullName = ns + "." + typeName;
                    type = Type.GetType(fullName);
                    if (type != null) return type;

                    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            type = assembly.GetType(fullName);
                            if (type != null) return type;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UEHttp] Error resolving type '{typeName}': {ex.Message}");
            }
            return null;
        }

        static Dictionary<string, object> Success(Dictionary<string, object> data)
        {
            return new Dictionary<string, object> { { "success", true }, { "data", data } };
        }

        static Dictionary<string, object> Error(int statusCode, string error, string errorCode, Dictionary<string, object> extra = null)
        {
            var result = new Dictionary<string, object>
            {
                { "success", false },
                { "error", error },
                { "errorCode", errorCode }
            };
            if (extra != null)
                result["data"] = extra;
            return result;
        }
    }
}

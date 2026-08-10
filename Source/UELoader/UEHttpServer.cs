using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 游戏内 UE HTTP 服务（本机 127.0.0.1，动态端口）。
    /// 路由表与 UESDdebuger/MCP/index.js 各 UE 工具的调用端点一一对应（/unityexplorer/*）。
    /// 响应契约：{ success, data?, error?, errorCode? }（MCP 端 callUnityExplorerAPI 直接透传）。
    /// 请求在 ThreadPool 线程处理，UE 操作经 UEHttpHandler 投递主线程，安全无阻塞。
    ///
    /// 动态端口：从 3001 起递增探测空闲端口，成功后把实际端口写入 {RootDir}/MCP/ports.json
    /// （含从 Player.log 解析的 Unity 调试端口），MCP 侧（index.js）与 McpRimDebug 按端口文件连接。
    /// </summary>
    public static class UEHttpServer
    {
        // 动态端口：从 3001 起递增探测空闲端口（共 PortTryCount 次尝试，即 3001~3010）。
        // 不再硬编码端口，避免与其他模组（如 RimworldMCPDebug 的 MCPNotifier 也监听 3001）冲突。
        const int PortStart = 3001;
        const int PortTryCount = 10;

        static string rootDir; // 模组根目录（写 MCP/ports.json 用，由 Start(rootDir) 保存）

        static HttpListener listener;
        static Thread listenerThread;
        static volatile bool running;

        /// <summary>实际监听端口（动态探测得到；未启动/启动失败为 0）。</summary>
        public static int ActualPort { get; private set; }

        public static bool IsRunning
        {
            get { return running; }
        }

        /// <summary>
        /// 启动 UE HTTP 服务：从 3001 起递增探测空闲端口，成功后以实际端口监听，
        /// 并把端口写入 {rootDir}/MCP/ports.json。幂等：已运行时直接返回。
        /// 任何失败仅记录日志，不抛异常阻断游戏启动。
        /// </summary>
        public static void Start(string rootDir)
        {
            if (running)
                return;

            UEHttpServer.rootDir = rootDir;
            ActualPort = 0;

            try
            {
                // 注意：这里不调用 UEHttpHandler.Init()。
                // UEHttpHandler.Init() 里 UEMainThreadDispatcher.EnsureInitialized() 会 new GameObject，
                // 而 Mod 构造函数不在主线程（Internal_CreateGameObject can only be called from the main thread），
                // 因此 Init() 改由 ExplorerBootstrap.Initialize（SceneManager.sceneLoaded，主线程）调用。

                // 端口探测：HttpListenerException 视为端口被占用或该端口无 http.sys URL ACL 权限，
                // 关闭本次尝试的 listener 后继续探测下一个端口；其他异常（或探测范围耗尽）则中止。
                for (int port = PortStart; port < PortStart + PortTryCount; port++)
                {
                    HttpListener candidate = null;
                    try
                    {
                        candidate = new HttpListener();
                        candidate.Prefixes.Add("http://127.0.0.1:" + port + "/");
                        candidate.Start();

                        listener = candidate;
                        ActualPort = port;
                        running = true;
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        // 端口被占用 / ACL 未授权：释放本次尝试，继续下一个端口
                        try { candidate?.Close(); } catch { }
                        UEHttpLog.Message($"[UEHttp] 端口 {port} 不可用（被占用或该端口无 URL ACL 权限），尝试下一个端口");
                    }
                    catch (Exception ex)
                    {
                        try { candidate?.Close(); } catch { }
                        UEHttpLog.Error($"[UEHttp] Failed to start UE HTTP server on port {port}: {ex.GetType().Name}: {ex.Message}");
                        break;
                    }
                }

                if (!running)
                {
                    // 探测范围耗尽：记录可读日志（含 ACL 提示），不抛异常
                    UEHttpLog.Error($"[UEHttp] 未能在 {PortStart}~{PortStart + PortTryCount - 1} 找到可用端口，UE HTTP 服务未启动");
                    UEHttpLog.Message("[UEHttp] 若端口被其他模组占用（如 RimworldMCPDebug 的 MCPNotifier 也监听 3001），请只启用一个此类模组；若提示 Access Denied，请以管理员身份运行，或用 netsh http add urlacl 为对应端口授权");
                    return;
                }

                listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "UEHttpServerThread"
                };
                listenerThread.Start();

                UEHttpLog.Message($"[UEHttp] UE HTTP server started on http://127.0.0.1:{ActualPort}/");

                // 启动成功后立即写端口文件（unityDebugPort 此刻可能尚未打印到 Player.log，
                // 进场景后由 RefreshPortsFile() 补写刷新）。
                WritePortsFile();
            }
            catch (Exception ex)
            {
                running = false;
                UEHttpLog.Error($"[UEHttp] Failed to start UE HTTP server: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            try { listener?.Close(); } catch { }
            listener = null;
            UEHttpLog.Message("[UEHttp] UE HTTP server stopped");
        }

        // ---------------------------------------------------------------- 端口文件 / Unity 调试端口

        /// <summary>
        /// 把实际端口写入模组端口文件 {rootDir}/MCP/ports.json：
        /// { "ueHttpPort": &lt;int&gt;, "unityDebugPort": &lt;int|null&gt;, "updatedAt": "&lt;ISO8601&gt;" }。
        /// MCP 侧（UESDdebuger/MCP/index.js）与 McpRimDebug 均读取该文件以适配动态端口。
        /// 写入失败仅记日志，不阻断游戏启动。
        /// </summary>
        public static void WritePortsFile()
        {
            try
            {
                if (string.IsNullOrEmpty(rootDir) || !running)
                {
                    UEHttpLog.Warning("[UEHttp] rootDir 为空或服务未启动，跳过端口文件写入");
                    return;
                }

                string dir = Path.Combine(rootDir, "MCP");
                Directory.CreateDirectory(dir);

                // 手写 JSON 保证格式精确（Unity JsonUtility 对 null 序列化与字段名大小写不友好）
                int? unityPort = DiscoverUnityDebugPort();
                string json = "{ \"ueHttpPort\": " + ActualPort
                    + ", \"unityDebugPort\": " + (unityPort.HasValue ? unityPort.Value.ToString() : "null")
                    + ", \"updatedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz") + "\" }";

                string file = Path.Combine(dir, "ports.json");
                File.WriteAllText(file, json);
                UEHttpLog.Message($"[UEHttp] ports.json 已写入 {file}（ueHttpPort={ActualPort}，unityDebugPort={(unityPort.HasValue ? unityPort.Value.ToString() : "null")}）");
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[UEHttp] 写入 ports.json 失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新写一次端口文件：进场景（SceneManager.sceneLoaded）后调用，此时 Unity 调试代理端口
        /// 通常已打印到 Player.log，可刷新 unityDebugPort 字段。服务未启动或 rootDir 未知时跳过。
        ///
        /// 注意：Unity 调试端口由 boot.config 的 wait-for-managed-debugger=1 触发，**每次运行随机**，
        /// 打印在 Player.log 的**首行**（"Starting managed debugger on port XXXX"）。游戏运行越久，
        /// 尾部 64KB 窗口（见 DiscoverUnityDebugPort）越可能读不到该行，因此这里进场景时尽早刷新，
        /// 之后 unityDebugPort 保持写入值；若届时仍未解析到，端口文件该字段为 null（属正常，
        /// 表示本次游戏未以调试模式启动或该行已不在尾窗内，McpRimDebug 侧会回退其他发现方式）。
        /// </summary>
        public static void RefreshPortsFile()
        {
            if (!running || string.IsNullOrEmpty(rootDir))
                return;
            WritePortsFile();
        }

        /// <summary>
        /// 从 Player.log 解析 Unity 调试端口（"Starting managed debugger on port XXXX"，
        /// 与 McpRimDebug 同款正则）。只读文件尾部 64KB、RightToLeft 取最后一次匹配；
        /// 文件不存在/读取失败/未匹配时返回 null。
        ///
        /// 已知限制（务必牢记，勿误解返回值）：
        /// - 该行打印在 Player.log **首行**（Unity 启动早期，wait-for-managed-debugger=1 时），
        ///   游戏运行越久尾部 64KB 越不含该行 → 返回 null 属**正常**，不代表游戏没开调试代理；
        /// - 端口每次运行随机，多个匹配取最后一个（本文件被游戏反复覆盖写，只应有一个匹配）；
        /// - 如需更高命中率，可改为全文件扫描（日志可达数 MB，本实现取尾窗是省 IO 的取舍）。
        /// unityDebugPort 为 null 时，请先看 Player.log 首行确认本次真实调试端口，
        /// 再用 McpRimDebug 的 attach(host, port) 显式连接。
        /// 常见端口区分（勿混用）：
        /// - 3001：本模组 UE HTTP（动态探测，见 ports.json ueHttpPort）；
        /// - 8765：RIMAPI（第三方模组，固定）；
        /// - 55000：Unity PlayerConnection（引擎内置、Profiler 用，固定，**不是** mono 调试端口）；
        /// - 56030 等随机端口：mono 调试代理（每次运行不同，见 Player.log 首行）。
        /// </summary>
        public static int? DiscoverUnityDebugPort()
        {
            string logPath = GetPlayerLogPath();
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                return null;

            try
            {
                const int TailBytes = 64 * 1024;
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length <= 0)
                        return null;
                    long start = Math.Max(0, fs.Length - TailBytes);
                    fs.Seek(start, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - start];
                    int n = fs.Read(buf, 0, buf.Length);
                    string tail = Encoding.UTF8.GetString(buf, 0, n);
                    Match m = Regex.Match(tail, @"Starting managed debugger on port (\d+)",
                        RegexOptions.RightToLeft | RegexOptions.Multiline);
                    if (!m.Success)
                        return null;
                    return int.TryParse(m.Groups[1].Value, out int p) ? p : (int?)null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 获取 Player.log 路径：优先 Unity 提供的 consoleLogPath（Unity 2018+，运行时可直接
        /// 拿到当前日志绝对路径）；为空时回退 Ludeon Studios 标准日志位置（路径校验文件存在）。
        /// </summary>
        static string GetPlayerLogPath()
        {
            try
            {
                string p = UnityEngine.Application.consoleLogPath;
                if (!string.IsNullOrEmpty(p))
                    return p;
            }
            catch { }

            try
            {
                // 回退 1：spec 指定的 LocalApplicationData 路径
                // （注意 Unity 实际把 Player.log 写在 LocalLow，该路径通常不存在，仅作兜底）
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    string p = Path.Combine(local, "Ludeon Studios", "RimWorld by Ludeon Studios", "Player.log");
                    if (File.Exists(p))
                        return p;
                }
            }
            catch { }

            try
            {
                // 回退 2：McpRimDebug 实测的 Steam 版默认路径（LocalLow，Player.log 实际写入位置）
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local))
                {
                    string p = Path.Combine(local, "..", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Player.log");
                    if (File.Exists(p))
                        return p;
                }
            }
            catch { }

            return null;
        }

        static void ListenLoop()
        {
            while (running)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => ProcessRequest(context));
                }
                catch (Exception ex)
                {
                    if (running)
                        UEHttpLog.Warning($"[UEHttp] Listener error: {ex.Message}");
                }
            }
        }

        static void ProcessRequest(HttpListenerContext context)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string path = null;
            try
            {
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;
                path = request.Url.AbsolutePath.TrimStart('/');

                // 请求体只解析一次（InputStream 只能读一遍，重复读会抛 ObjectDisposedException）
                Dictionary<string, object> body = ParseBody(request);

                // 请求日志：方法 + 路径 + 关键参数摘要（进 Player.log）
                LogRequest(request, path, body);

                Dictionary<string, object> result;
                switch (path)
                {
                    case "trigger-quicktest":
                        result = PostGuard(request, () => UEHttpHandler.TriggerQuickTest());
                        break;
                    case "unityexplorer/inspect/type":
                        result = PostGuard(request, () =>
                            UEHttpHandler.InspectType(GetString(body, "typeName")));
                        break;
                    case "unityexplorer/console/execute":
                        result = PostGuard(request, () =>
                            UEHttpHandler.ExecuteCode(GetString(body, "code")));
                        break;
                    case "unityexplorer/console/reset":
                        result = PostGuard(request, () => UEHttpHandler.ResetConsole());
                        break;
                    case "unityexplorer/console/addusing":
                        result = PostGuard(request, () =>
                            UEHttpHandler.AddUsing(GetString(body, "namespace")));
                        break;
                    case "unityexplorer/status":
                        result = GetGuard(request, () => UEHttpHandler.GetStatus());
                        break;
                    case "unityexplorer/log/clear":
                        result = PostGuard(request, () => UEHttpHandler.ClearLogs());
                        break;
                    case "unityexplorer/log":
                        result = GetGuard(request, () =>
                            UEHttpHandler.GetLogs(GetIntQuery(request, "count", 50)));
                        break;
                    case "unityexplorer/hook/create":
                        result = PostGuard(request, () =>
                            UEHttpHandler.CreateHook(
                                GetString(body, "typeName"),
                                GetString(body, "methodName"),
                                GetString(body, "patchType"),
                                GetString(body, "patchCode")));
                        break;
                    case "unityexplorer/hook/toggle":
                        result = PostGuard(request, () =>
                            UEHttpHandler.ToggleHook(GetString(body, "hookId"),
                                GetBool(body, "enabled")));
                        break;
                    case "unityexplorer/hook/list":
                        result = GetGuard(request, () => UEHttpHandler.ListHooks());
                        break;
                    case "unityexplorer/hook":
                        if (request.HttpMethod == "DELETE")
                            result = DeleteGuard(request, () =>
                                UEHttpHandler.DeleteHook(GetString(body, "hookId")));
                        else
                            result = MethodNotAllowed();
                        break;
                    // ---------- DebugAction 工具（UEDebugActions，游戏状态访问经主线程投递） ----------
                    case "debugaction/list":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.ListActions(
                                GetQueryString(request, "category"),
                                GetQueryString(request, "parentPath"),
                                GetBoolQuery(request, "includeHidden"))));
                        break;
                    case "debugaction/categories":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.GetCategories()));
                        break;
                    case "debugaction/search":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.Search(
                                GetQueryString(request, "query"),
                                GetQueryString(request, "category"),
                                GetIntQuery(request, "maxResults", 200))));
                        break;
                    case "debugaction/info":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.GetDetail(GetQueryString(request, "path"))));
                        break;
                    case "debugaction/execute":
                        result = PostGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.Execute(
                                GetString(body, "path"),
                                GetIntOrNull(body, "mapX"),
                                GetIntOrNull(body, "mapZ"),
                                GetIntOrNull(body, "worldTile"),
                                GetIntOrNull(body, "pawnId"))));
                        break;
                    // ---------- MapStructure 工具 ----------
                    case "mapstructure/get":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.GetMapStructure(GetQueryString(request, "path"))));
                        break;
                    case "mapstructure/search":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.SearchMapStructure(
                                GetQueryString(request, "query"),
                                GetQueryString(request, "category"))));
                        break;
                    default:
                        result = new Dictionary<string, object>
                        {
                            { "success", false },
                            { "error", "Endpoint not found" },
                            { "errorCode", "ENDPOINT_NOT_FOUND" }
                        };
                        LogResult(result, sw);
                        SendJson(response, 404, result);
                        return;
                }

                int status = IsSuccess(result) ? 200 : StatusCodeFor(result);
                LogResult(result, sw);
                SendJson(response, status, result);
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[UEHttp] Error processing request: {ex}");
                try
                {
                    LogResult(null, sw, "INTERNAL_ERROR");
                    SendJson(context.Response, 500, new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error", "Internal error: " + ex.Message },
                        { "errorCode", "INTERNAL_ERROR" }
                    });
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- 请求/结果日志

        /// <summary>请求日志：方法 + 路径 + 关键参数摘要（关键字段按端点取，code 截断防刷屏）。</summary>
        static void LogRequest(HttpListenerRequest request, string path, Dictionary<string, object> body)
        {
            string args = BuildArgsSummary(request.HttpMethod, path, body);
            if (args == null)
                UEHttpLog.Message($"[UEHttp] {request.HttpMethod} /{path}");
            else
                UEHttpLog.Message($"[UEHttp] {request.HttpMethod} /{path} args={{{args}}}");
        }

        static string BuildArgsSummary(string method, string path, Dictionary<string, object> body)
        {
            switch (path)
            {
                case "unityexplorer/console/execute":
                    return "code=" + Truncate(GetString(body, "code"), 80);
                case "unityexplorer/hook/create":
                    return $"typeName={GetString(body, "typeName")},methodName={GetString(body, "methodName")},patchType={GetString(body, "patchType")}";
                case "unityexplorer/inspect/type":
                    return "typeName=" + GetString(body, "typeName");
                case "unityexplorer/hook/toggle":
                    return "hookId=" + GetString(body, "hookId");
                case "unityexplorer/hook":
                    if (method == "DELETE")
                        return "hookId=" + GetString(body, "hookId");
                    return null;
                default:
                    return null;
            }
        }

        static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLen)
                return s;
            return s.Substring(0, maxLen) + "...";
        }

        /// <summary>执行结果日志：→ ok / → error(&lt;errorCode&gt;)，含耗时（从 ProcessRequest 入口计时）。</summary>
        static void LogResult(Dictionary<string, object> result, System.Diagnostics.Stopwatch sw, string fallbackErrorCode = null)
        {
            long elapsed = sw.ElapsedMilliseconds;
            if (IsSuccess(result))
            {
                UEHttpLog.Message($"[UEHttp] → ok, {elapsed}ms");
                return;
            }

            string code = fallbackErrorCode;
            if (result != null && result.TryGetValue("errorCode", out object ec) && ec != null)
                code = Convert.ToString(ec);
            UEHttpLog.Message($"[UEHttp] → error({code}), {elapsed}ms");
        }

        // ---------------------------------------------------------------- 请求体解析

        static Dictionary<string, object> ParseBody(HttpListenerRequest request)
        {
            if (request.ContentLength64 <= 0)
                return new Dictionary<string, object>(StringComparer.Ordinal);

            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                string body = reader.ReadToEnd();
                return UELightJson.ParseObject(body) ?? new Dictionary<string, object>(StringComparer.Ordinal);
            }
        }

        static string GetString(Dictionary<string, object> body, string key)
        {
            if (body != null && body.TryGetValue(key, out object v) && v != null)
                return Convert.ToString(v);
            return null;
        }

        static bool GetBool(Dictionary<string, object> body, string key)
        {
            if (body != null && body.TryGetValue(key, out object v))
            {
                if (v is bool b) return b;
                if (v is long l) return l != 0;
                if (v is string s) return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        static int GetIntQuery(HttpListenerRequest request, string key, int defaultValue)
        {
            string raw = request.QueryString[key];
            if (int.TryParse(raw, out int parsed))
                return Math.Min(Math.Max(1, parsed), 200);
            return defaultValue;
        }

        static string GetQueryString(HttpListenerRequest request, string key)
        {
            string raw = request.QueryString[key];
            return string.IsNullOrEmpty(raw) ? null : raw;
        }

        static bool GetBoolQuery(HttpListenerRequest request, string key)
        {
            string raw = request.QueryString[key];
            return !string.IsNullOrEmpty(raw) && (raw == "true" || raw == "1");
        }

        static int? GetIntOrNull(Dictionary<string, object> body, string key)
        {
            if (body != null && body.TryGetValue(key, out object v) && v != null)
            {
                if (v is long l) return (int)l;
                if (v is int i) return i;
                if (v is double d) return (int)d;
                if (v is string s && int.TryParse(s, out int p)) return p;
            }
            return null;
        }

        // ---------------------------------------------------------------- 方法守卫

        static Dictionary<string, object> PostGuard(HttpListenerRequest request, Func<Dictionary<string, object>> handler)
        {
            if (request.HttpMethod != "POST")
                return MethodNotAllowed();
            return handler();
        }

        static Dictionary<string, object> GetGuard(HttpListenerRequest request, Func<Dictionary<string, object>> handler)
        {
            if (request.HttpMethod != "GET")
                return MethodNotAllowed();
            return handler();
        }

        static Dictionary<string, object> DeleteGuard(HttpListenerRequest request, Func<Dictionary<string, object>> handler)
        {
            if (request.HttpMethod != "DELETE")
                return MethodNotAllowed();
            return handler();
        }

        static Dictionary<string, object> MethodNotAllowed()
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", "Method not allowed" },
                { "errorCode", "METHOD_NOT_ALLOWED" }
            };
        }

        // ---------------------------------------------------------------- 响应

        static bool IsSuccess(Dictionary<string, object> result)
        {
            return result != null && result.TryGetValue("success", out object v) && v is bool b && b;
        }

        static int StatusCodeFor(Dictionary<string, object> result)
        {
            if (result == null) return 500;
            return 400; // 业务错误统一 4xx（MCP 端主要看 success 字段）
        }

        static void SendJson(HttpListenerResponse response, int statusCode, Dictionary<string, object> payload)
        {
            try
            {
                string json = UELightJson.Serialize(payload);
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                response.StatusCode = statusCode;
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UEHttp] Failed to send response: {ex.Message}");
            }
        }
    }
}

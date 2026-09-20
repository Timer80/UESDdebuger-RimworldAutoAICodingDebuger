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

        // 游戏内 HTTP 服务的启动结果自报（写进 ports.json，供 MCP 侧准确归因）。
        // 背景：服务没起来时，MCP 侧只能看到 "Unable to connect"，历史上被误判成 "UE 未就绪 / 需要 GABP"，
        // 排查方向被带偏。这里把失败事实与原因落到端口文件里，MCP 直接读得到。
        static string startError; // null=正常启动（或尚未尝试）；非 null=失败原因

        static HttpListener listener;
        static Thread listenerThread;
        static volatile bool running;
        static string authToken;             // 每次游戏启动随机生成（P0-CS-1）

        /// <summary>校验 Bearer token（P0-CS-1）。空 token 或头不匹配返回 false。</summary>
        static bool IsAuthorized(HttpListenerRequest request)
        {
            if (string.IsNullOrEmpty(authToken)) return false;
            string auth = request.Headers["Authorization"];
            const string prefix = "Bearer ";
            if (string.IsNullOrEmpty(auth) || !auth.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            string token = auth.Substring(prefix.Length).Trim();
            return string.Equals(token, authToken, StringComparison.Ordinal);
        }

        /// <summary>未授权响应（HTTP 401）。</summary>
        static Dictionary<string, object> Unauthorized()
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", "Unauthorized" },
                { "errorCode", "UNAUTHORIZED" }
            };
        }

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
                    // 探测范围耗尽：记录可读日志（含 ACL 提示），不抛异常。
                    // 关键：这里仍然写一次 ports.json（ueHttpStatus=failed + 原因），让 MCP 侧能区分
                    // 「模组没加载（文件不存在/是上次会话的）」与「模组加载了但服务起不来」。
                    startError = $"未能在 {PortStart}~{PortStart + PortTryCount - 1} 找到可用端口（被占用或缺少 URL ACL 授权）";
                    UEHttpLog.Error($"[UEHttp] {startError}，UE HTTP 服务未启动");
                    UEHttpLog.Message("[UEHttp] 若端口被其他模组占用（如 RimworldMCPDebug 的 MCPNotifier 也监听 3001），请只启用一个此类模组；若提示 Access Denied，请以管理员身份运行，或用 netsh http add urlacl 为对应端口授权");
                    WritePortsFile();
                    return;
                }

                listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "UEHttpServerThread"
                };
                listenerThread.Start();

                UEHttpLog.Message($"[UEHttp] UE HTTP server started on http://127.0.0.1:{ActualPort}/");

                // P0-CS-1：每次游戏启动随机生成 token（不落盘即不可预测，保证会话内鉴权可用）
                authToken = Guid.NewGuid().ToString("N"); // 64 个 hex，无特殊字符，安全放进 JSON 与 Header

                startError = null;
                // 启动成功后立即写端口文件（unityDebugPort 此刻可能尚未打印到 Player.log，
                // 进场景后由 RefreshPortsFile() 补写刷新）。
                WritePortsFile();
            }
            catch (Exception ex)
            {
                running = false;
                startError = $"启动异常：{ex.GetType().Name}: {ex.Message}";
                UEHttpLog.Error($"[UEHttp] Failed to start UE HTTP server: {ex.GetType().Name}: {ex.Message}");
                WritePortsFile();
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
        /// { "ueHttpPort": &lt;int&gt;, "token": "&lt;hex&gt;", "unityDebugPort": &lt;int|null&gt;,
        ///   "ueHttpStatus": "running"|"failed", "ueHttpError": "&lt;失败原因，成功时为空串&gt;",
        ///   "updatedAt": "&lt;ISO8601&gt;" }。
        /// MCP 侧（UESDdebuger/MCP/index.js）与 McpRimDebug 均读取该文件以适配动态端口。
        /// 写入失败仅记日志，不阻断游戏启动。
        ///
        /// ueHttpStatus/ueHttpError（2026-09-19 新增）：服务**没起来**时也要写这份文件——
        /// 否则 MCP 侧只能看到 "Unable to connect"，历史上被误判成「UE 未就绪 / 需要 GABP」。
        /// 有了这两个字段，MCP 可以明确区分「模组没加载（文件是上次会话的）」与「模组加载了但端口起不来」。
        ///
        /// 鉴权设计说明（P0-CS-1，待实现，勿删除本注释）:
        /// 已拍板方案 = 游戏侧每次启动生成随机 token 并写入本文件的 token 字段，
        /// MCP 服务器启动时读同一文件取 token；所有“写操作”端点（unityexplorer/console/execute、
        /// unityexplorer/hook/create、area/delete|clear、debugaction/execute 等）校验
        /// Authorization: Bearer &lt;token&gt;，不匹配返回 401；“读操作”（status/log 等）免 token。
        /// token 生命周期 = 本次游戏会话（每次运行随机），这是天然对齐点。
        /// </summary>
        public static void WritePortsFile()
        {
            try
            {
                if (string.IsNullOrEmpty(rootDir))
                {
                    UEHttpLog.Warning("[UEHttp] rootDir 为空，跳过端口文件写入");
                    return;
                }

                string dir = Path.Combine(rootDir, "MCP");
                Directory.CreateDirectory(dir);

                // 手写 JSON 保证格式精确（Unity JsonUtility 对 null 序列化与字段名大小写不友好）
                int? unityPort = DiscoverUnityDebugPort();
                string status = running ? "running" : "failed";
                string errText = running ? "" : JsonEscape(startError ?? "游戏内 HTTP 服务未启动（原因未记录）");
                string json = "{ \"ueHttpPort\": " + ActualPort
                    + ", \"token\": \"" + (authToken ?? "") + "\""
                    + ", \"unityDebugPort\": " + (unityPort.HasValue ? unityPort.Value.ToString() : "null")
                    + ", \"ueHttpStatus\": \"" + status + "\""
                    + ", \"ueHttpError\": \"" + errText + "\""
                    + ", \"updatedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz") + "\" }";

                string file = Path.Combine(dir, "ports.json");
                File.WriteAllText(file, json);
                UEHttpLog.Message($"[UEHttp] ports.json 已写入 {file}（ueHttpPort={ActualPort}，ueHttpStatus={status}"
                    + $"，unityDebugPort={(unityPort.HasValue ? unityPort.Value.ToString() : "null")}"
                    + (running ? "" : $"，ueHttpError={errText}") + "）");
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[UEHttp] 写入 ports.json 失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// JSON 字符串转义（手写 JSON 用）：错误信息可能含引号/反斜杠/换行，直接拼进 ports.json 会写出坏 JSON。
        /// 只做必要替换，保证任何输入都能产出合法 JSON 单行字符串。
        /// </summary>
        static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "/").Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        }

        /// <summary>
        /// 重新写一次端口文件：进场景（SceneManager.sceneLoaded）后调用，此时 Unity 调试代理端口
        /// 通常已打印到 Player.log，可刷新 unityDebugPort 字段。rootDir 未知时跳过；
        /// 服务未起来（running=false）时也写——把失败状态刷新给 MCP 侧（见 WritePortsFile 注释）。
        ///
        /// 注意：Unity 调试端口由 boot.config 的 wait-for-managed-debugger=1 触发，**每次运行随机**，
        /// 打印在 Player.log 的**首行**（"Starting managed debugger on port XXXX"）。游戏运行越久，
        /// 尾部 64KB 窗口（见 DiscoverUnityDebugPort）越可能读不到该行，因此这里进场景时尽早刷新，
        /// 之后 unityDebugPort 保持写入值；若届时仍未解析到，端口文件该字段为 null（属正常，
        /// 表示本次游戏未以调试模式启动或该行已不在尾窗内，McpRimDebug 侧会回退其他发现方式）。
        /// </summary>
        public static void RefreshPortsFile()
        {
            if (string.IsNullOrEmpty(rootDir))
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
                    // ---------- RimBridgeServer（GABP）收发状态（RimBridgeGABPStatusPatch，只读静态快照） ----------
                    case "rimbridge/status":
                        result = GetGuard(request, () => RimBridgeGABPStatusPatch.GetStatus());
                        break;
                    // ---------- 手搓 Area 删除/清空（UEAreaActions，主线程执行，替代 RimBridge 僵死的 delete_area/clear_area） ----------
                    case "area/delete":
                        result = PostGuard(request, () =>
                            UEAreaActions.DeleteArea(GetString(body, "areaId")));
                        break;
                    case "area/clear":
                        result = PostGuard(request, () =>
                            UEAreaActions.ClearArea(GetString(body, "areaId")));
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
                    // ---------- 地图坐标光标（UEMapMarker；自研 shader，见 ShaderProject/） ----------
                    case "mapmarker/set":
                        result = PostGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEMapMarker.Set(
                                GetString(body, "id"),
                                GetIntOrNull(body, "x"),
                                GetIntOrNull(body, "z"),
                                GetString(body, "color"),
                                GetFloatOrNull(body, "size"),
                                GetString(body, "label"),
                                GetFloatOrNull(body, "ttlSeconds"),
                                GetIntOrNull(body, "mapId"),
                                GetBool(body, "keepExisting"))));
                        break;
                    case "mapmarker/clear":
                        result = PostGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEMapMarker.Clear(GetString(body, "id"), GetIntOrNull(body, "mapId"))));
                        break;
                    case "mapmarker/recolor":
                        result = PostGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEMapMarker.Recolor(GetString(body, "id"), GetString(body, "color"))));
                        break;
                    case "mapmarker/list":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEMapMarker.List()));
                        break;
                    // ---------- 热重载桥（HotReloadManager） ----------
                    case "unityexplorer/hotreload/apply":
                        result = PostGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            HotReloadManager.ApplyReload(GetString(body, "modId"))));
                        break;
                    case "unityexplorer/hotreload/watch":
                        result = PostGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            HotReloadManager.SetWatch(GetBool(body, "enabled"))));
                        break;
                    case "unityexplorer/hotreload/status":
                        result = GetGuard(request, () => HotReloadManager.GetStatus());
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
                if (ex is BodyTooLargeException)
                {
                    LogResult(null, sw, "PAYLOAD_TOO_LARGE");
                    SendJson(context.Response, 413, new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error", "Request body too large (max 1MB)" },
                        { "errorCode", "PAYLOAD_TOO_LARGE" }
                    });
                    return;
                }
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
                case "unityexplorer/hotreload/apply":
                    return "modId=" + GetString(body, "modId");
                case "unityexplorer/hotreload/watch":
                    return "enabled=" + GetBool(body, "enabled");
                case "mapmarker/set":
                    return $"id={GetString(body, "id")},x={GetIntOrNull(body, "x")},z={GetIntOrNull(body, "z")},color={GetString(body, "color")}";
                case "mapmarker/clear":
                    return "id=" + GetString(body, "id");
                case "mapmarker/recolor":
                    return $"id={GetString(body, "id")},color={GetString(body, "color")}";
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

        // 请求体大小上限（P2-CS-2）。ContentLength64 与实际读入字节两者任一超限都拒绝，双保险覆盖 chunked / 头不可信。
        const int MaxBodyBytes = 1 * 1024 * 1024;

        static Dictionary<string, object> ParseBody(HttpListenerRequest request)
        {
            if (request.ContentLength64 <= 0)
                return new Dictionary<string, object>(StringComparer.Ordinal);
            if (request.ContentLength64 > MaxBodyBytes)
                throw new BodyTooLargeException();

            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                char[] buf = new char[MaxBodyBytes + 1];
                int n = reader.Read(buf, 0, buf.Length);
                if (n > MaxBodyBytes)
                    throw new BodyTooLargeException();  // 双保险：chunked 或 ContentLength64 不可信时仍截断
                string body = new string(buf, 0, n);
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

        static float? GetFloatOrNull(Dictionary<string, object> body, string key)
        {
            if (body != null && body.TryGetValue(key, out object v) && v != null)
            {
                if (v is long l) return l;
                if (v is int i) return i;
                if (v is double d) return (float)d;
                if (v is float f) return f;
                if (v is string s
                    && float.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float p))
                    return p;
            }
            return null;
        }

        // ---------------------------------------------------------------- 方法守卫

        static Dictionary<string, object> PostGuard(HttpListenerRequest request, Func<Dictionary<string, object>> handler)
        {
            if (request.HttpMethod != "POST")
                return MethodNotAllowed();
            if (!IsAuthorized(request)) return Unauthorized();   // P0-CS-1 写操作鉴权
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
            if (!IsAuthorized(request)) return Unauthorized();   // P0-CS-1 写操作鉴权
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
            if (result.TryGetValue("errorCode", out object ec)
                && Convert.ToString(ec) == "UNAUTHORIZED") return 401;
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

        /// <summary>请求体超限异常（P2-CS-2）。ProcessRequest 捕获后映射为 HTTP 413。</summary>
        sealed class BodyTooLargeException : Exception { }
    }
}

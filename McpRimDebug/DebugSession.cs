using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    /// <summary>调试会话状态机。</summary>
    public enum SessionState
    {
        /// <summary>未连接（初始态，也是 detach/断连后的落点）。</summary>
        Disconnected,

        /// <summary>正在连接游戏调试端口（attach 进行中）。</summary>
        Attaching,

        /// <summary>已连接并完成 DWP 握手/协议协商，可执行 VM 命令。</summary>
        Attached,

        /// <summary>正在安全断开（detach 进行中，等待事件循环确认）。</summary>
        Detaching,
    }

    /// <summary>
    /// 内部调试事件：事件循环线程把 SDB 的 EventSet 扁平化为 RawEvent 后放入
    /// BlockingCollection&lt;RawEvent&gt;，供后续 wait 等工具消费。
    /// 事件循环内不做任何 socket 操作（解析线程/方法留给消费方惰性进行）。
    /// </summary>
    public sealed class RawEvent
    {
        /// <summary>事件类型（Breakpoint/Step/Exception/VMDisconnect ...）。</summary>
        public EventType Type { get; set; }

        /// <summary>触发事件的事件请求 id（无请求则为 0）。</summary>
        public int RequestId { get; set; }

        /// <summary>线程 id（-1 表示事件循环未解析；需经 Source 惰性解析）。</summary>
        public long ThreadId { get; set; }

        /// <summary>该事件集是否使 VM 挂起（SuspendPolicy != None）。</summary>
        public bool SuspendsVm { get; set; }

        /// <summary>人类可读描述。</summary>
        public string Description { get; set; }

        /// <summary>入队时间。</summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>原始 SDB 事件（保留引用，供消费方在命令锁内解析线程/方法/对象）。</summary>
        internal Event Source { get; set; }
    }

    /// <summary>
    /// 默认配置（统一入口，可在调用时经 MCP_RIMDBG_* 环境变量覆盖）：
    ///   MCP_RIMDBG_GAME_PATH → 游戏可执行文件路径（launch 默认）；
    ///   MCP_RIMDBG_HOST       → 调试主机（attach/status 默认）；
    ///   MCP_RIMDBG_PORT       → 调试端口（attach/launch/status 默认，非法值忽略）。
    /// 每次访问都实时读环境变量（不缓存），因此 selftest 可以临时改 env 后断言。
    /// </summary>
    public static class Defaults
    {
        // 默认目标为 Steam 版 RimWorld；其他安装路径（如旧默认 F:\RimWorld3\RimWorldWin64.exe）
        // 仍可经 launch 的 path 参数或 MCP_RIMDBG_GAME_PATH 环境变量传入，仅不再作为默认。
        public const string FallbackGamePath = @"C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe";
        public const string FallbackHost = "127.0.0.1";
        public const int FallbackPort = 56574;

        /// <summary>游戏可执行文件路径：环境变量 MCP_RIMDBG_GAME_PATH 优先，缺省为 Steam 版路径。</summary>
        public static string GamePath
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("MCP_RIMDBG_GAME_PATH");
                return !string.IsNullOrWhiteSpace(v) ? v : FallbackGamePath;
            }
        }

        /// <summary>调试主机：环境变量 MCP_RIMDBG_HOST 优先，缺省 127.0.0.1。</summary>
        public static string Host
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("MCP_RIMDBG_HOST");
                return !string.IsNullOrWhiteSpace(v) ? v : FallbackHost;
            }
        }

        /// <summary>调试端口：环境变量 MCP_RIMDBG_PORT 优先（非法值忽略），缺省 56574。</summary>
        public static int Port
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("MCP_RIMDBG_PORT");
                if (!string.IsNullOrWhiteSpace(v) && int.TryParse(v, out int p) && p > 0 && p <= 65535)
                    return p;
                return FallbackPort;
            }
        }

        // Unity 的玩家构建（RimWorld）启动时由 boot.config 的 wait-for-managed-debugger=1 触发
        // 内嵌调试代理，端口**每次运行随机**，并在 Player.log 打印（首行附近，Mono path 之后）：
        //   "Starting managed debugger on port XXXX"
        //   "Using monoOptions --debugger-agent=transport=dt_socket,embedding=1,server=y,suspend=n,address=0.0.0.0:XXXX"
        // status 工具据此动态发现真实端口（debugPortFromLog），attach 请优先使用该端口。
        // 注意：attach 用 host 是 127.0.0.1（代理监听 0.0.0.0，本机回环即可）。
        // 常见端口区分（勿混用）：3001=UE HTTP（UESDdebuger 模组，动态）；8765=RIMAPI（第三方，固定）；
        // 55000=Unity PlayerConnection（引擎内置 Profiler 用，固定，**不是** mono 调试端口）；
        // 其他随机端口（如 56030）=mono 调试代理（每次运行不同）。
        /// <summary>
        /// 默认 Player.log 路径（Steam 版）。不再硬编码用户名，改为按本机 %LOCALAPPDATA% 动态推导
        /// （Unity 实际写在 LocalLow；LocalLow 无独立 SpecialFolder，取其父目录 Local 的兄弟目录）。
        /// 仍可用环境变量 MCP_RIMDBG_LOG_PATH 显式覆盖。
        /// </summary>
        public static string FallbackLogPath
        {
            get
            {
                try
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (!string.IsNullOrEmpty(local))
                    {
                        string localLow = Path.Combine(Path.GetDirectoryName(local) ?? string.Empty, "LocalLow");
                        string p = Path.Combine(localLow, "Ludeon Studios", "RimWorld by Ludeon Studios", "Player.log");
                        return p;
                    }
                }
                catch { }
                // 最后兜底：仍在本地数据目录下按 Ludeon 惯例推断；找不到时退回 LocalApplicationData 路径
                string local2 = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local2, "Ludeon Studios", "RimWorld by Ludeon Studios", "Player.log");
            }
        }

        /// <summary>Unity Player.log 路径：环境变量 MCP_RIMDBG_LOG_PATH 优先，缺省为 Steam 版路径。</summary>
        public static string LogPath
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("MCP_RIMDBG_LOG_PATH");
                return !string.IsNullOrWhiteSpace(v) ? v : FallbackLogPath;
            }
        }
    }

    /// <summary>
    /// 会话核心（单例）：持有 VM 连接、状态机、全局命令锁与事件循环线程。
    ///
    /// 线程模型：
    /// - 命令线程（MCP 工具调用）：所有 VM socket 读写（attach/detach/resume/suspend）都
    ///   在 _commandLock 串行化下执行，避免并发读写同一 socket。
    /// - 事件循环线程：独占消费 vm.GetNextEventSet()，把事件格式化为 RawEvent 入队，
    ///   并维护挂起状态；遇到 VMDisconnect 时把会话复位到 Disconnected。
    ///
    /// 运行时约束：不可走 VirtualMachineManager.Connect(IPEndPoint)（其内部 Delegate.BeginInvoke
    /// 在 .NET Core 上抛 PlatformNotSupportedException），而是直接构造本程序集内的 TcpConnection
    /// 后调用 VirtualMachineManager.Connect(Connection, ...) 重载。
    /// </summary>
    public sealed partial class DebugSession : IObjectHandleStore
    {
        public static DebugSession Instance { get; } = new DebugSession();

        // ---- 会话状态（受 _stateLock 保护） ----
        readonly object stateLock = new object();
        SessionState state = SessionState.Disconnected;
        VirtualMachine vm;
        string sessionHost;
        int sessionPort;
        bool suspended;       // 挂起状态：断点/步/异常事件或 suspend 命令 → true；resume → false
        bool everResumed;     // attach 后是否执行过 resume（用于“需 resume 游戏才运行”提示）
        string lastDisconnectReason = "";
        int lastLaunchedPid;
        // attach 建立的原始 socket（与 vm 同生命周期；超时强制断开时用 Shutdown 打断接收线程的阻塞读）
        Socket sessionSocket;

        // ---- Task 6：健壮性常量 ----
        /// <summary>阻塞型 VM 命令执行超时（毫秒）：超时后强制断开调试连接，可重新 attach。</summary>
        const int CommandTimeoutMs = 15000;
        /// <summary>socket 连接/握手超时（毫秒），仅 attach 阶段生效（之后接收线程需无限阻塞读）。</summary>
        const int SocketTimeoutMs = 10000;
        /// <summary>事件日志队列上限（wait 的非匹配事件暂存队列，防事件风暴）。</summary>
        internal const int EventLogMax = 200;
        /// <summary>launch 后自动等待调试端口出现并 attach 的最大时长（毫秒）。</summary>
        const int LaunchAutoAttachTimeoutMs = 90000;
        /// <summary>端口进入监听后、发起 attach 前的就绪缓冲（毫秒）：agent 完全就绪需短暂时间，过早连接会被 RST。</summary>
        const int LaunchAttachSettleMs = 3000;
        /// <summary>自动 attach 失败后的重试次数（间隔 2s，应对 agent 就绪窗口内的瞬时失败）。</summary>
        const int LaunchAttachRetries = 2;

        // ---- 全局命令锁：所有 VM socket 操作经此串行化 ----
        readonly object commandLock = new object();
        /// <summary>获取 commandLock 的短等待（毫秒）：拿不到即返回忙警告（P1-MD-1 阻塞返回警告）。</summary>
        const int CommandBusyWaitMs = 50;

        // ---- 事件循环 ----
        readonly BlockingCollection<RawEvent> eventQueue = new BlockingCollection<RawEvent>();
        Thread eventLoopThread;
        volatile bool eventLoopRunning;

        // ---- 断点 / 事件请求注册表 ----
        // 断点注册表：断点 id（= SDB EventRequest id，与事件里的 RequestId 对应）→ 断点条目
        readonly object breakpointsLock = new object();
        readonly Dictionary<int, BreakpointEntry> breakpoints = new Dictionary<int, BreakpointEntry>();
        // 异常事件请求注册表：请求 id → 描述（detach/断连时随会话一并清除）
        readonly Dictionary<int, string> exceptionRequests = new Dictionary<int, string>();

        // ---- 断点/事件请求统计（会话级；随会话复位清零，见 HandleDisconnect/ResetSession）----
        long breakpointsAddedTotal;    // break_add 累计成功次数
        long breakpointsRemovedTotal;  // break_remove + break_clear 累计释放次数
        /// <summary>当前活动异常事件请求数（派生自 exceptionRequests，无需单独维护）。</summary>
        int eventRequestsActive { get { return exceptionRequests.Count; } }

        // ---- Task 4：对象句柄缓存 ----
        // SDB 对象 id → 句柄号（正查）+ 句柄号 → ObjectMirror（反查，供 inspect 展开）；
        // 句柄号为会话内递增整数；断连/会话复位时清空（见 HandleDisconnect/ResetSession）。
        readonly object handlesLock = new object();
        readonly Dictionary<long, int> objectHandles = new Dictionary<long, int>();
        readonly Dictionary<int, ObjectMirror> handleObjects = new Dictionary<int, ObjectMirror>();
        int nextHandle = 1;

        DebugSession()
        {
        }

        /// <summary>事件队列（供后续 wait 工具消费）。</summary>
        public BlockingCollection<RawEvent> EventQueue
        {
            get { return eventQueue; }
        }

        // ---------------------------------------------------------------- status

        /// <summary>
        /// 汇总会话状态：游戏进程探测 + 调试端口 TCP 连通性探测 + 连接状态/协议版本/挂起状态。
        /// 端口探测仅在没有已建立的调试会话时执行，避免干扰调试代理。
        /// </summary>
        public ToolResult Status()
        {
            SessionState st;
            bool susp;
            bool resumed;
            string proto = null;
            string vmVersion = null;
            string host;
            int port;
            string lastReason;
            int breakpointsActive = 0;
            long breakpointsAdded = 0;
            long breakpointsRemoved = 0;
            int exceptionEventsActive = 0;

            lock (stateLock)
            {
                st = state;
                susp = suspended;
                resumed = everResumed;
                host = !string.IsNullOrEmpty(sessionHost) ? sessionHost : DefaultHost();
                port = sessionPort != 0 ? sessionPort : DefaultPort();
                lastReason = lastDisconnectReason;

                // vm.Version 是连接时写好的内存字段，读取不涉及 socket 操作
                if (vm != null && st == SessionState.Attached)
                {
                    proto = vm.Version.MajorVersion + "." + vm.Version.MinorVersion;
                    vmVersion = vm.Version.VMVersion;
                }

                // 断点统计（与 HandleDisconnect/ResetSession 保持 stateLock → breakpointsLock 同序嵌套）
                lock (breakpointsLock)
                {
                    breakpointsActive = breakpoints.Count;
                    breakpointsAdded = breakpointsAddedTotal;
                    breakpointsRemoved = breakpointsRemovedTotal;
                    exceptionEventsActive = eventRequestsActive;
                }
            }

            // 进程探测（慢操作，不持锁）
            var found = new List<string>();
            foreach (string name in new[] { "RimWorldWin64", "RimWorldLinux", "RimWorldMac", "RimWorld" })
            {
                try
                {
                    if (Process.GetProcessesByName(name).Length > 0)
                        found.Add(name);
                }
                catch
                {
                    // 权限等异常忽略，仅影响该名字的探测结果
                }
            }

            // 发现游戏自报的调试端口（Unity 端口每次运行随机，以此为准）：
            // 模组端口文件 MCP/ports.json（游戏侧 UELoader 写入）优先，回退 Player.log 解析。
            int? logPort = DiscoverDebugPortFromPortsFile() ?? DiscoverDebugPortFromLog();

            // TCP 监听探测（未连接时探测；已连接则端口必然开放，无需探测以免干扰调试代理）。
            // 优先探测 Player.log 发现的实际端口；探测方式为本地监听检查（不建立连接），
            // 避免 raw TCP 连接触发 mono 调试代理 "DWP handshake failed" 导致游戏进程退出。
            bool portOpen;
            if (st == SessionState.Disconnected)
                portOpen = ProbeTcpPort(host, logPort ?? port);
            else if (st == SessionState.Attached)
                portOpen = true; // 已建立调试连接
            else
                portOpen = false;

            var data = new Dictionary<string, object>
            {
                ["state"] = st.ToString(),
                ["attached"] = st == SessionState.Attached,
                ["gameProcessRunning"] = found.Count > 0,
                ["gameProcessNames"] = found.ToArray(),
                ["debugHost"] = host,
                ["debugPort"] = port,
                ["debugPortFromLog"] = logPort,
                ["debugPortOpen"] = portOpen,
                ["portOpen"] = portOpen,
                ["suspended"] = susp,
                ["protocolVersion"] = proto,
                ["vmVersion"] = vmVersion,
                ["needResume"] = st == SessionState.Attached && !resumed,
                ["lastDisconnectReason"] = lastReason,
                // 断点统计：任何状态（含未连接）都返回，未连接时恒为 0
                ["breakpointsActive"] = breakpointsActive,
                ["breakpointsAddedTotal"] = breakpointsAdded,
                ["breakpointsRemovedTotal"] = breakpointsRemoved,
                ["eventRequestsActive"] = exceptionEventsActive,
            };

            string msg;
            switch (st)
            {
                case SessionState.Disconnected:
                    msg = "未连接（" + (found.Count > 0 ? "检测到游戏进程 " + string.Join("/", found) : "未检测到游戏进程") + "）";
                    if (logPort.HasValue)
                        msg += "；已从 Player.log 发现游戏调试端口 " + logPort.Value
                            + (portOpen ? "（开放）" : "（未开放）") + "，可用 attach(host, " + logPort.Value + ") 连接";
                    else
                        msg += "；调试端口 " + host + ":" + port + (portOpen ? " 开放" : " 未开放")
                            + "。可用 attach(host, port) 连接，或 launch() 以调试模式启动游戏";
                    break;
                case SessionState.Attaching:
                    msg = "正在连接 " + host + ":" + port + " ...";
                    break;
                case SessionState.Detaching:
                    msg = "正在断开 ...";
                    break;
                default:
                    msg = "已连接 " + host + ":" + port;
                    if (proto != null)
                        msg += "，协议 " + proto;
                    if (vmVersion != null)
                        msg += "，VM " + vmVersion;
                    msg += susp ? "，VM 挂起中" : "，VM 运行中";
                    if (!resumed)
                        msg += "；attach 后需 resume 游戏才运行（wait-for-managed-debugger=1）";
                    break;
            }

            // 挂起提示：VM 挂起且仍有活动断点/异常请求时，解释“卡住”属正常断点挂起并给出释放建议
            if (susp && (breakpointsActive + exceptionEventsActive) > 0)
            {
                msg += "；注意：VM 挂起中仍保留 " + breakpointsActive + " 个活动断点"
                    + (exceptionEventsActive > 0 ? "、" + exceptionEventsActive + " 个异常事件请求" : "")
                    + "——游戏看起来“卡住”属正常的断点挂起，可用 break_remove / break_clear 释放断点后调用 resume 恢复运行";
            }

            return ToolResult.OkResult(msg, data);
        }

        // ---------------------------------------------------------------- attach

        /// <summary>
        /// 连接游戏调试端口并完成 DWP 握手与协议协商。
        /// 绕开 VirtualMachineManager.Connect(IPEndPoint)（.NET Core 上 Delegate.BeginInvoke 不可用），
        /// 改为直接构造 TcpConnection 后调用 Connect(Connection, ...) 重载。
        /// host/port 缺省（null/空）时走配置（MCP_RIMDBG_HOST / MCP_RIMDBG_PORT）。
        /// 超时：TCP 连接与握手阶段受 SocketTimeoutMs 约束（socket 读写超时，握手期间接收线程尚未启动，
        /// 安全）；完整 attach 另受 Guard 看门狗约束。
        /// </summary>
        public ToolResult Attach(string host, int? port)
        {
            if (string.IsNullOrWhiteSpace(host))
                host = DefaultHost();
            int p = port.HasValue ? port.Value : DefaultPort();
            if (p <= 0 || p > 65535)
                return ToolResult.ErrorResult("非法端口: " + p);

            lock (commandLock)
            {
                lock (stateLock)
                {
                    if (state != SessionState.Disconnected)
                        return ToolResult.ErrorResult("当前状态为 " + state + "，无法 attach（请先 detach）");
                    state = SessionState.Attaching;
                }

                Socket socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    // TCP 连接超时（Connect 同步调用在目标不可达时可阻塞 ~20s，这里显式限时）
                    var connectTask = socket.ConnectAsync(host, p);
                    if (!connectTask.Wait(SocketTimeoutMs))
                        throw new TimeoutException("连接超时（" + SocketTimeoutMs + "ms）：主机不可达或端口未开放");

                    // 握手阶段 socket 读写超时（接收线程在 Connect 内部才启动；握手完成后复位为无限）
                    socket.ReceiveTimeout = SocketTimeoutMs;
                    socket.SendTimeout = SocketTimeoutMs;

                    // TcpConnection 为本程序集 internal 类，可直接构造；Connect(Connection,...) 完成
                    // 握手（DWP-Handshake）、协议版本协商与根域获取，全程阻塞。
                    Connection transport = new TcpConnection(socket);
                    VirtualMachine newVm = VirtualMachineManager.Connect(transport, null, null);

                    // 连接完成：接收线程已运行，必须恢复无限超时，否则空闲事件（>10s 无事件）会杀死连接
                    socket.ReceiveTimeout = 0;
                    socket.SendTimeout = 0;

                    lock (stateLock)
                    {
                        vm = newVm;
                        sessionSocket = socket;
                        sessionHost = host;
                        sessionPort = p;
                        suspended = false;
                        everResumed = false;
                        state = SessionState.Attached;
                    }
                    StartEventLoop(newVm);

                    VersionInfo v = newVm.Version;
                    var data = new Dictionary<string, object>
                    {
                        ["host"] = host,
                        ["port"] = p,
                        ["protocolVersion"] = v.MajorVersion + "." + v.MinorVersion,
                        ["vmVersion"] = v.VMVersion,
                        ["endpoint"] = newVm.EndPoint != null ? newVm.EndPoint.ToString() : null,
                    };
                    return ToolResult.OkResult(
                        "已连接 " + host + ":" + p + "，协议 " + v.MajorVersion + "." + v.MinorVersion
                        + "，VM " + v.VMVersion + "；attach 后需 resume 游戏才运行（wait-for-managed-debugger=1）。"
                        + "注意：本会话为一次性调试会话，detach/断开后如需再次调试，请重启调试服务器（McpRimDebug）",
                        data);
                }
                catch (Exception ex)
                {
                    if (socket != null)
                    {
                        try { socket.Close(); } catch { }
                    }
                    lock (stateLock)
                    {
                        // P0-MD-2：只在仍是本调用自设的 Attaching 时才复位；
                        // 若已被新的 Attach/Reset 占用则不动，避免旧后台任务冲掉新会话。
                        if (state == SessionState.Attaching)
                            state = SessionState.Disconnected;
                    }
                    return ToolResult.ErrorResult("attach 失败: " + FriendlyError(ex, "无法连接 " + host + ":" + p)
                        + "；mono 调试为一次性会话，重连需重启调试服务器（McpRimDebug）");
                }
            }
        }

        // ---------------------------------------------------------------- detach

        /// <summary>
        /// 安全断开：告知代理（VM_Dispose 会清除事件请求并恢复 VM），然后关闭连接，游戏继续运行。
        /// 会话复位到 Disconnected（事件循环收到的 VMDisconnect 事件会幂等地执行同样复位）。
        /// </summary>
        public ToolResult Detach()
        {
            VirtualMachine target;
            lock (stateLock)
            {
                if (state != SessionState.Attached || vm == null)
                    return ToolResult.ErrorResult("未连接，无需 detach");
                target = vm;
                state = SessionState.Detaching;
            }

            lock (commandLock)
            {
                try
                {
                    // P3-MD-8（双重关闭语义）：target.Detach() 在代理侧确认后内部会关闭本连接 socket。
                    // 若同一连接此前已经被 ForceDisconnectForTimeout 的 socket.Shutdown（或事件循环的
                    // VMDisconnect 路径）触碰过，这里再次 Close 属幂等安全组合——Socket/连接对象二次关闭
                    // 抛出的 ObjectDisposedException 会被上层 catch 忽略，不会破坏状态机；
                    // 连接对象的生命周期统一由 HandleDisconnect/ResetSession 结束时把 vm/sessionSocket 置 null 收束。
                    target.Detach();
                    ResetSession();
                    return ToolResult.OkResult("已安全 detach，游戏继续运行");
                }
                catch (VMDisconnectedException)
                {
                    ResetSession();
                    return ToolResult.OkResult("VM 已先行断开，会话已复位");
                }
                catch (Exception ex)
                {
                    ResetSession();
                    return ToolResult.ErrorResult("detach 失败: " + FriendlyError(ex, "断开连接"));
                }
            }
        }

        // ---------------------------------------------------------------- resume / suspend

        /// <summary>继续整个 VM 运行，并更新挂起状态。</summary>
        public ToolResult Resume()
        {
            VirtualMachine target;
            lock (stateLock)
            {
                if (state != SessionState.Attached || vm == null)
                    return ToolResult.ErrorResult("未连接，无法 resume");
                target = vm;
            }

            if (!TryEnterCommandLock(out string busy))
                return ToolResult.ErrorResult(busy);
            try
            {
                try
                {
                    target.Resume();
                    lock (stateLock) { suspended = false; everResumed = true; }
                    return ToolResult.OkResult("VM 已恢复运行");
                }
                catch (VMNotSuspendedException)
                {
                    lock (stateLock) { suspended = false; everResumed = true; }
                    return ToolResult.OkResult("VM 本就未挂起，无需恢复");
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("resume 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("resume 失败: " + FriendlyError(ex, "恢复 VM"));
                }
            }
            finally { ExitCommandLock(); }
        }

        /// <summary>挂起整个 VM，并更新挂起状态。</summary>
        public ToolResult Suspend()
        {
            VirtualMachine target;
            lock (stateLock)
            {
                if (state != SessionState.Attached || vm == null)
                    return ToolResult.ErrorResult("未连接，无法 suspend");
                target = vm;
            }

            if (!TryEnterCommandLock(out string busy))
                return ToolResult.ErrorResult(busy);
            try
            {
                try
                {
                    target.Suspend();
                    lock (stateLock) { suspended = true; }
                    return ToolResult.OkResult("VM 已挂起");
                }
                catch (VMNotSuspendedException)
                {
                    lock (stateLock) { suspended = true; }
                    return ToolResult.OkResult("VM 已处于挂起状态");
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("suspend 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("suspend 失败: " + FriendlyError(ex, "挂起 VM"));
                }
            }
            finally { ExitCommandLock(); }
        }

        // ---------------------------------------------------------------- launch

        /// <summary>
        /// 以调试模式启动游戏。游戏由 boot.config（wait-for-managed-debugger=1）触发 Unity
        /// 内嵌调试代理，端口**每次运行随机**（尝试注入 MONO_SDB_ENV_OPTIONS 实测被游戏自身的
        /// monoOptions 覆盖，不可靠），实际调试端口在启动后写入 Player.log。
        /// autoAttach=true（缺省）时，启动后自动轮询 Player.log 拿到实际端口并 attach：
        /// Unity 的 "Debug (Player)" 告知窗在 attach 成功那一刻自动关闭，之后调用 resume
        /// 游戏才开始运行。autoAttach=false 时保持旧行为，仅拉起进程，由调用方自行
        /// status → attach。返回进程 PID。
        /// </summary>
        public ToolResult Launch(string path, int? port, string workingDir, bool autoAttach = true)
        {
            string exe = FirstNonEmpty(path, Defaults.GamePath);
            int p = port.HasValue ? port.Value : Defaults.Port;
            string wd = FirstNonEmpty(workingDir, exe != null ? Path.GetDirectoryName(exe) : null);

            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                return ToolResult.ErrorResult("游戏可执行文件不存在: " + exe
                    + "（可用 path 参数或 MCP_RIMDBG_GAME_PATH 环境变量指定）");
            if (p <= 0 || p > 65535)
                return ToolResult.ErrorResult("非法端口: " + p);
            if (string.IsNullOrEmpty(wd) || !Directory.Exists(wd))
                return ToolResult.ErrorResult("工作目录不存在: " + wd);

            // 一次仅应运行一个游戏进程：Unity 调试端口每次随机，双实例会端口冲突导致无法 attach。
            // 检测到已有 RimWorld 进程时给出明确警告并中止本次启动。
            string running = FindRunningGame();
            if (running != null)
                return ToolResult.ErrorResult("检测到已有 RimWorld 进程正在运行（" + running + "）。"
                    + "Unity 调试端口每次运行随机，同时运行两个实例会端口冲突而无法 attach。"
                    + "建议先关闭现有游戏进程后重试");

            try
            {
                // 记录启动前的旧端口：自动 attach 需等待本次运行出现**新**端口，
                // 避免命中上次运行的残留端口（旧代理可能在关闭过程中仍短暂监听，实测见过）。
                // 端口文件优先（游戏侧写入），回退 Player.log。
                int? preLaunchPort = autoAttach
                    ? (DiscoverDebugPortFromPortsFile() ?? DiscoverDebugPortFromLog()) : (int?)null;

                var psi = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = wd,
                    UseShellExecute = false,
                    // 重定向并丢弃游戏输出：防止游戏写入的 stdout/stderr（如 [UnityMemory] 行）
                    // 继承到 MCP 服务器的标准流、污染 MCP JSON-RPC 协议通道（Trae 等客户端收到
                    // 非 JSON 数据可能弹出“通信异常/是否关闭服务器”之类提示）。
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                Process proc = Process.Start(psi);
                if (proc == null)
                    return ToolResult.ErrorResult("Process.Start 返回 null，启动失败");

                // 后台持续排空游戏的输出流，避免管道缓冲区写满导致游戏阻塞
                _ = Task.Run(() => { try { proc.StandardOutput.ReadToEnd(); } catch { } });
                _ = Task.Run(() => { try { proc.StandardError.ReadToEnd(); } catch { } });

                lock (stateLock) { lastLaunchedPid = proc.Id; }

                if (autoAttach)
                    return AutoAttachAfterLaunch(proc, preLaunchPort);

                var data = new Dictionary<string, object>
                {
                    ["pid"] = proc.Id,
                    ["port"] = p,
                    ["path"] = exe,
                };
                return ToolResult.OkResult(
                    "已启动游戏 PID=" + proc.Id + "。游戏启动后会在 Player.log 打印实际调试端口"
                    + "（每次运行随机），请调用 status 查看 debugPortFromLog 后，用 attach(host, 该端口) 连接",
                    data);
            }
            catch (Exception ex)
            {
                return ToolResult.ErrorResult("launch 失败: " + FriendlyError(ex, "启动进程"));
            }
        }

        /// <summary>
        /// launch 后的自动附加流程：轮询 Player.log 等待**本次运行**的新随机调试端口出现且进入监听，
        /// 缓冲片刻后 attach（失败重试）。attach 成功时 Unity 的 "Debug (Player)" 告知窗自动关闭，
        /// 游戏处于挂起态（需 resume）。端口发现以 Player.log 为准，探测用监听表检查（不建真实连接）。
        /// </summary>
        ToolResult AutoAttachAfterLaunch(Process proc, int? preLaunchPort)
        {
            string host = DefaultHost();
            int? logPort = null;
            string waitReason = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(LaunchAutoAttachTimeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (proc.HasExited)
                {
                    waitReason = "游戏进程已退出";
                    break;
                }
                // 端口发现：ports.json 优先，但若其端口 == 启动前记录（preLaunchPort），
                // 说明是上次运行的残留旧值（本次运行的游戏尚未刷新端口文件），必须回退
                // Player.log 解析本次新端口，否则永远等不到“新端口”而超时。
                // P2-MD-4：ports.json 命中本次新端口时直接采用，跳过 Player.log 的 IO 读。
                int? fromPorts = DiscoverDebugPortFromPortsFile();
                if (fromPorts.HasValue && fromPorts.Value != preLaunchPort)
                    logPort = fromPorts;
                else
                    logPort = DiscoverDebugPortFromLog();
                // 必须是本次运行的新端口（≠ 启动前的残留端口）且已进入监听
                if (logPort.HasValue && logPort.Value != preLaunchPort && ProbeTcpPort(host, logPort.Value))
                    break;
                Thread.Sleep(500);
            }

            if (!logPort.HasValue || logPort.Value == preLaunchPort || !ProbeTcpPort(host, logPort.Value))
            {
                var failData = new Dictionary<string, object>
                {
                    ["pid"] = proc.Id,
                    ["autoAttached"] = false,
                    ["attached"] = false,
                };
                return ToolResult.OkResult(
                    "已启动游戏 PID=" + proc.Id + "，但未在 " + (LaunchAutoAttachTimeoutMs / 1000)
                    + "s 内等到本次运行的调试端口开放"
                    + (waitReason != null ? "（" + waitReason + "）" : "")
                    + "。Unity 的 Debug(Player) 告知窗可能仍显示；可调用 status 查看 debugPortFromLog"
                    + " 后手动 attach",
                    failData);
            }

            int targetPort = logPort.Value;
            // agent 完全就绪需短暂时间，过早 attach 会被 RST；先缓冲再尝试
            Thread.Sleep(LaunchAttachSettleMs);
            ToolResult at = null;
            for (int attempt = 0; attempt <= LaunchAttachRetries; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(2000);
                at = Attach(host, targetPort);
                if (at.Ok)
                    break;
            }

            var data = new Dictionary<string, object>
            {
                ["pid"] = proc.Id,
                ["port"] = targetPort,
                ["autoAttached"] = at.Ok,
                ["attached"] = at.Ok,
                ["needResume"] = at.Ok,
            };
            if (at.Ok)
            {
                lock (stateLock)
                {
                    if (vm != null)
                    {
                        data["protocolVersion"] = vm.Version.MajorVersion + "." + vm.Version.MinorVersion;
                        data["vmVersion"] = vm.Version.VMVersion;
                    }
                }
                // 主动关闭 Unity 的 "Debug (Player)" 告知窗（不会随 attach 自动消失，实测已确认）
                bool windowClosed = DismissDebugPlayerWindow(proc.Id);
                data["debugPlayerWindowClosed"] = windowClosed;
                return ToolResult.OkResult(
                    "已启动游戏 PID=" + proc.Id + " 并自动连接调试端口 " + targetPort
                    + (windowClosed ? "（Unity 的 Debug(Player) 告知窗已自动关闭）"
                                    : "（未找到 Debug(Player) 告知窗，若仍显示请手动点确定）")
                    + "。游戏当前挂起，请调用 resume 让游戏开始运行",
                    data);
            }

            data["autoAttachError"] = at.Message;
            return ToolResult.OkResult(
                "已启动游戏 PID=" + proc.Id + "（调试端口 " + targetPort + " 已开放），但自动 attach 失败: "
                + at.Message + "。可手动调用 attach(host, " + targetPort + ") 连接",
                data);
        }

        // ---- Win32：主动关闭 Unity 的 "Debug (Player)" 告知窗（仅 Windows）----
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        const uint WmClose = 0x0010;

        /// <summary>
        /// 找到指定进程的 "Debug (Player)" 告知窗并发送 WM_CLOSE 关闭（等同点击 OK）。
        /// 该窗口是 Unity 内嵌调试代理的启动告知窗，**不会**随 attach 自动消失，需主动关闭
        /// （实测：启动后约 2s 出现，WM_CLOSE 后消失且游戏继续正常运行）。
        /// 匹配条件：窗口必须属于游戏进程 + 标题精确等于 "Debug (Player)" + 可见，
        /// 避免误伤其它窗口。窗口可能在游戏启动后 1~3s 才出现，此处轮询约 10s 并反复关闭
        /// 可能重复弹出的窗口。返回是否至少成功关闭过一次。
        /// </summary>
        static bool DismissDebugPlayerWindow(int pid)
        {
            if (!OperatingSystem.IsWindows())
                return false;
            bool dismissed = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                IntPtr hwnd = IntPtr.Zero;
                string matchedTitle = null;
                EnumWindows((h, l) =>
                {
                    uint wndPid;
                    GetWindowThreadProcessId(h, out wndPid);
                    if (wndPid == pid)
                    {
                        var title = new StringBuilder(256);
                        GetWindowText(h, title, title.Capacity);
                        string t = title.ToString();
                        if (IsWindowVisible(h) && t.Equals("Debug (Player)", StringComparison.OrdinalIgnoreCase))
                        {
                            hwnd = h;
                            matchedTitle = t;
                            return false; // 停止枚举
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (hwnd != IntPtr.Zero)
                {
                    Console.Error.WriteLine("[DismissDebugPlayerWindow] 进程 {0} 发送 WM_CLOSE 到 hwnd={1} 标题=[{2}]",
                        pid, hwnd.ToInt64(), matchedTitle);
                    PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
                    dismissed = true;
                }
                else if (dismissed)
                {
                    break; // 已关闭且不再出现
                }
                Thread.Sleep(500);
            }
            return dismissed;
        }

        // ---------------------------------------------------------------- 事件循环

        void StartEventLoop(VirtualMachine target)
        {
            eventLoopRunning = true;
            eventLoopThread = new Thread(() => EventLoop(target))
            {
                Name = "SdbEventLoop",
                IsBackground = true,
            };
            eventLoopThread.Start();
        }

        /// <summary>
        /// 事件循环：持续消费 vm.GetNextEventSet()，把事件扁平化为 RawEvent 入队，
        /// 并维护挂起状态。用带超时的重载，使线程能及时响应会话复位。
        /// 本线程不做任何 socket 操作（GetNextEventSet 只读内存队列；SDB 的收包
        /// 由连接内部 receiver 线程完成）。
        /// </summary>
        void EventLoop(VirtualMachine target)
        {
            while (eventLoopRunning)
            {
                bool stillMine;
                lock (stateLock) { stillMine = ReferenceEquals(target, vm); }
                if (!stillMine) break;   // P1-MD-5：会话已更换/该 VM 已非当前 → 旧循环退出
                EventSet es;
                try
                {
                    es = target.GetNextEventSet(500);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("VM 断开连接");
                    break;
                }
                catch (Exception ex)
                {
                    HandleDisconnect("事件循环异常: " + ex.Message);
                    break;
                }

                if (es == null)
                    continue; // 超时：回到循环顶部检查退出标志

                bool suspends = es.SuspendPolicy != SuspendPolicy.None;
                if (suspends)
                {
                    lock (stateLock) { suspended = true; }
                }

                foreach (Event e in es.Events)
                {
                    var raw = new RawEvent
                    {
                        Type = e.EventType,
                        RequestId = e.Request != null ? e.Request.GetId() : 0,
                        ThreadId = -1, // 解析线程需走 socket（GetThread），留给消费方在命令锁内惰性进行
                        SuspendsVm = suspends,
                        Description = FormatEvent(e),
                        Timestamp = DateTimeOffset.Now,
                        Source = e,
                    };
                    EnqueueEvent(raw);

                    // P2-MD-2：新程序集/类型加载会使已缓存程序集元数据过期，清会话级缓存
                    // 避免 find_* 返回旧元数据（新程序集下次 find 会重新拉取）。
                    if (e.EventType == EventType.AssemblyLoad || e.EventType == EventType.TypeLoad)
                        InvalidateMetadataCache();

                    if (e.EventType == EventType.VMDisconnect)
                    {
                        HandleDisconnect("调试器已断开");
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// 尝试获取 commandLock；拿不到返回 busy 提示（P1-MD-1 阻塞返回警告）。
        /// 供短命令（threads/callstack/locals/inspect/eval/搜索/断点等非占用者）使用，
        /// 避免排队在 Step 等长锁占用者后面被看门狗判定超时。
        /// </summary>
        bool TryEnterCommandLock(out string busy)
        {
            if (Monitor.TryEnter(commandLock, CommandBusyWaitMs))
            {
                busy = null;
                return true;
            }
            busy = "调试会话忙（正被 step/等待事件等阻塞型命令占用命令锁），命令已排队/等待中，请稍后重试";
            return false;
        }

        /// <summary>释放 commandLock（与 TryEnterCommandLock 配对；仅在 TryEnter 成功后的 finally 中调用）。</summary>
        void ExitCommandLock()
        {
            Monitor.Exit(commandLock);
        }

        /// <summary>事件入队（统一入口）：入队后维护事件日志上限，超过 EventLogMax 条丢弃最旧事件
        /// （防事件风暴导致内存/输出无界增长）。事件循环与 wait/step 放回未匹配事件时都经此。</summary>
        void EnqueueEvent(RawEvent raw)
        {
            eventQueue.Add(raw);
            int overflow = eventQueue.Count - EventLogMax;
            for (int i = 0; i < overflow && eventQueue.Count > 0; i++)
            {
                RawEvent dropped;
                if (!eventQueue.TryTake(out dropped))
                    break;
            }
        }

        /// <summary>连接断开统一处理：会话复位到 Disconnected（幂等）。</summary>
        void HandleDisconnect(string reason)
        {
            lock (stateLock)
            {
                lastDisconnectReason = reason;
                eventLoopRunning = false;
                suspended = false;
                if (state != SessionState.Disconnected)
                    state = SessionState.Disconnected;
                vm = null;
                sessionSocket = null;
                sessionHost = null;
                sessionPort = 0;
                // 断点/事件请求随连接销毁（代理端请求已随连接失效，本地仅清引用）
                lock (breakpointsLock)
                {
                    breakpoints.Clear();
                    exceptionRequests.Clear();
                    // 会话级断点统计随会话复位清零（活动数由字典派生，自动为 0）
                    breakpointsAddedTotal = 0;
                    breakpointsRemovedTotal = 0;
                }
                // 对象句柄缓存随连接销毁（对象 id 仅对当前连接有意义）
                lock (handlesLock)
                {
                    objectHandles.Clear();
                    handleObjects.Clear();
                    nextHandle = 1;
                }
                // P2-MD-2：程序集元数据缓存随连接销毁（避免跨 VM/跨会话残留旧程序集定义）
                InvalidateMetadataCache();
            }
            // P1-MD-2.2：会话结束清理本会话登记的截断落盘文件
            TruncationSink.SweepOnSessionEnd();
        }

        /// <summary>直接把会话复位到 Disconnected（detach 成功路径；与 HandleDisconnect 幂等共存）。</summary>
        void ResetSession()
        {
            lock (stateLock)
            {
                eventLoopRunning = false;
                vm = null;
                sessionSocket = null;
                sessionHost = null;
                sessionPort = 0;
                suspended = false;
                state = SessionState.Disconnected;
                lock (breakpointsLock)
                {
                    breakpoints.Clear();
                    exceptionRequests.Clear();
                    // 会话级断点统计随会话复位清零（活动数由字典派生，自动为 0）
                    breakpointsAddedTotal = 0;
                    breakpointsRemovedTotal = 0;
                }
                lock (handlesLock)
                {
                    objectHandles.Clear();
                    handleObjects.Clear();
                    nextHandle = 1;
                }
                // P2-MD-2：程序集元数据缓存随会话复位清空（避免跨 VM 残留旧程序集定义）
                InvalidateMetadataCache();
            }
            // P1-MD-2.2：会话结束清理本会话登记的截断落盘文件
            TruncationSink.SweepOnSessionEnd();
        }

        // ---------------------------------------------------------------- Task 6：统一执行包装与超时

        /// <summary>
        /// 所有 MCP 工具的统一切入点：异常统一转可读 ToolResult；
        /// withTimeout=true 的阻塞型操作（attach/resume/suspend/eval 等 VM 命令）附加看门狗超时。
        /// wait/step/status/launch/break_list 传 false（它们各有自己的有界等待或纯本地执行）。
        /// </summary>
        public ToolResult Guard(string what, bool withTimeout, Func<ToolResult> body)
        {
            try
            {
                if (!withTimeout)
                    return body();
                return WithTimeout(what, body);
            }
            catch (VMDisconnectedException)
            {
                HandleDisconnect(what + " 时 VM 断开");
                return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
            }
            catch (Exception ex)
            {
                return ToolResult.ErrorResult(what + " 失败: " + FriendlyError(ex, what));
            }
        }

        /// <summary>
        /// 看门狗：body 在后台线程执行，CommandTimeoutMs 内未完成则判定调试代理无响应——
        /// 强制断开连接（socket.Shutdown 打断接收线程的阻塞读 → 被放弃命令的 Monitor.Wait 被唤醒并抛
        /// VMDisconnectedException 安全退出，命令锁随之释放），会话复位到 Disconnected，可重新 attach。
        /// 不会阻塞事件循环线程（事件循环有独立的 GetNextEventSet(500) 超时）。
        /// </summary>
        ToolResult WithTimeout(string what, Func<ToolResult> body)
        {
            Task<ToolResult> task = Task.Run(body);
            if (task.Wait(CommandTimeoutMs))
                return task.GetAwaiter().GetResult(); // 异常原样重抛，交给 Guard 统一转文本

            string reason = what + " 超时（" + CommandTimeoutMs + "ms）：调试代理未响应，已强制断开连接";
            ForceDisconnectForTimeout(reason);
            // P0-MD-2：force-disconnect 会唤醒阻塞的命令线程抛 VMDisconnectedException 退出；
            // 有界等待让被放弃的命令尽快 settle，避免其残留占用 commandLock/socket，
            // 使超时后能安全立即重新 attach。限时未结束也返回（其 catch 已被上一步守卫成不复查状态）。
            try { task.Wait(CommandTimeoutMs); } catch { /* 忽略 AggregateException */ }
            return ToolResult.ErrorResult(reason + "，可重新 attach");
        }

        /// <summary>超时强制断开：Shutdown 打断接收线程阻塞读，使挂起的命令线程被唤醒并抛 VMDisconnectedException。
        /// P3-MD-8（双重关闭语义）：socket.Shutdown 与 target.ForceDisconnect（及其内部对本 socket 的 Close）
        /// 是幂等安全组合——此处先 Shutdown 唤醒阻塞读，再调 ForceDisconnect 让代理端正式终止并内部 Close 连接；
        /// 即使本连接已被事件循环的 VMDisconnect 路径或后续 detach 二次关闭，二次 Close 抛出的异常均被忽略，
        /// 连接对象的生命周期统一由 HandleDisconnect 结束时把 vm/sessionSocket 置 null 收束。</summary>
        void ForceDisconnectForTimeout(string reason)
        {
            VirtualMachine target;
            Socket s;
            lock (stateLock)
            {
                target = vm;
                s = sessionSocket;
            }
            try { if (s != null) s.Shutdown(SocketShutdown.Both); } catch { }
            // ForceDisconnect 内部会对本连接 socket 执行 Close；与上面的 Shutdown 是幂等组合（二次关闭异常被忽略）
            try { if (target != null) target.ForceDisconnect(); } catch { }
            HandleDisconnect(reason);
        }

        // ---------------------------------------------------------------- 辅助

        static string FormatEvent(Event e)
        {
            switch (e.EventType)
            {
                case EventType.UserLog:
                    var log = (UserLogEvent)e;
                    return "UserLog(level=" + log.Level + ", category=" + (log.Category ?? "") + "): " + (log.Message ?? "");
                case EventType.Step:
                    return "Step @il=" + ((StepEvent)e).Location;
                case EventType.Breakpoint:
                    return "Breakpoint";
                case EventType.Exception:
                    return "Exception";
                default:
                    return e.EventType.ToString();
            }
        }

        // ---- P2-MD-4：GetActiveTcpListeners 短时间窗缓存 ----
        // IPGlobalProperties.GetActiveTcpListeners() 每次都做系统级监听表枚举，auto_attach 每 500ms
        // 探测一次、status 也会探测，重复调用昂贵。短时间窗内复用结果；枚举源可注入（供 selftest 计次断言）。
        static readonly object listenersLock = new object();
        static IPEndPoint[] _activeListeners;
        static DateTime _listenersFetchedAt = DateTime.MinValue;
        static Func<IPEndPoint[]> listenerSource = () =>
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        /// <summary>监听表缓存时间窗（毫秒）：窗口内命中复用上次枚举结果，超出才重新枚举。</summary>
        internal static int ActiveListenersCacheWindowMs = 2000;

        /// <summary>
        /// 获取当前活动 TCP 监听表（短时间窗缓存）。窗口内任一 / 探测共享同一份结果，
        /// 避免每次 ProbeTcpPort 都对系统做监听表枚举；返回数组为只读视图，调用方勿持有引用跨窗口使用。
        /// </summary>
        internal static IPEndPoint[] GetActiveTcpListeners()
        {
            lock (listenersLock)
            {
                DateTime now = DateTime.UtcNow;
                if (_activeListeners != null
                    && (now - _listenersFetchedAt).TotalMilliseconds < ActiveListenersCacheWindowMs)
                    return _activeListeners;
                IPEndPoint[] arr;
                try { arr = listenerSource(); }
                catch { arr = null; }
                _activeListeners = arr != null ? arr : Array.Empty<IPEndPoint>();
                _listenersFetchedAt = now;
                return _activeListeners;
            }
        }

        /// <summary>测试钩子：注入监听表枚举源（传 null 恢复默认）。仅 selftest 用。</summary>
        internal static void SetListenerSourceForTest(Func<IPEndPoint[]> source)
        {
            lock (listenersLock)
            {
                listenerSource = source ?? (() =>
                    IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners());
                _activeListeners = null;
                _listenersFetchedAt = DateTime.MinValue;
            }
        }

        /// <summary>测试钩子：重置监听表缓存（清理注入的枚举源）。仅 selftest 用。</summary>
        internal static void ResetListenerCacheForTest()
        {
            lock (listenersLock)
            {
                listenerSource = () =>
                    IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                _activeListeners = null;
                _listenersFetchedAt = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 端口监听探测：通过本地 TCP 监听表判断端口是否处于监听状态，**不建立真实连接**。
        /// （mono 调试代理收到 raw TCP 连接但 DWP 握手失败时会中止游戏进程，故禁止用连接探测。）
        /// 监听表经 GetActiveTcpListeners 的短时间窗缓存读取（P2-MD-4）。
        /// </summary>
        static bool ProbeTcpPort(string host, int port)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            try
            {
                foreach (IPEndPoint ep in GetActiveTcpListeners())
                {
                    if (ep.Port != port)
                        continue;
                    // 主机过滤：localhost/回环地址可匹配任意本地监听（含 0.0.0.0 全地址监听）
                    if (IsLoopbackHost(host) || ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any))
                        return true;
                    if (IPAddress.TryParse(host, out IPAddress ha) && ha.Equals(ep.Address))
                        return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        static bool IsLoopbackHost(string host)
        {
            if (string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// 从模组端口文件 MCP/ports.json（游戏侧 UELoader 写入，格式
        /// { "ueHttpPort": 3001, "unityDebugPort": 56651, "updatedAt": "..." }）读取
        /// unityDebugPort 作为本次运行的实际调试端口。端口文件优先于 Player.log 解析；
        /// 路径可用环境变量 MCP_RIMDBG_PORTS_FILE 覆盖。文件缺失/不可读/解析失败/
        /// 字段为 null 或非正整数时返回 null（由调用方回退 Player.log 解析）。
        /// </summary>
        internal static int? DiscoverDebugPortFromPortsFile()
        {
            try
            {
                string portsFile = FirstNonEmpty(Environment.GetEnvironmentVariable("MCP_RIMDBG_PORTS_FILE"));
                if (portsFile == null)
                {
                    string modRoot = FindModRootDir();
                    if (modRoot == null)
                        return null;
                    portsFile = Path.Combine(modRoot, "MCP", "ports.json");
                }
                if (!File.Exists(portsFile))
                    return null;
                string json = File.ReadAllText(portsFile);
                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    System.Text.Json.JsonElement root = doc.RootElement;
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object
                        || !root.TryGetProperty("unityDebugPort", out System.Text.Json.JsonElement el)
                        || el.ValueKind == System.Text.Json.JsonValueKind.Null)
                        return null;
                    return el.TryGetInt32(out int port) && port > 0 ? port : (int?)null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 由 exe 所在目录向上推导模组根目录（即含 MCP 子目录的目录）。
        /// AppContext.BaseDirectory 形如 ...\UESDdebuger\McpRimDebug\bin\Debug\net10.0\，
        /// 注意 Path.GetDirectoryName 对带尾斜杠的路径第一次只去掉尾斜杠而不跳级，
        /// 因此逐级向上直至找到含 MCP 子目录的目录（上限 8 级），找不到返回 null。
        /// </summary>
        static string FindModRootDir()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "MCP")))
                    return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        // P3-MD-4：端口行正则提为常量并注明来源。
        // Unity mono 调试代理在 Player.log 首行打印（boot.config wait-for-managed-debugger=1 触发）：
        //   "Starting managed debugger on port XXXX"
        const string DebugPortPattern = @"Starting managed debugger on port (\d+)";

        /// <summary>
        /// 从 Unity Player.log 解析游戏自报的调试端口（"Starting managed debugger on port XXXX"）。
        /// 端口每次运行随机且为本次运行的最新值，故取最后一个匹配；文件可能被游戏独占，
        /// 以 FileShare.ReadWrite|Delete 打开（读不到/无匹配时返回 null）。
        ///
        /// 查找策略（P2-MD-1：尾窗优先，未命中全文件扫描兜底）：
        /// - **尾窗快速命中**：先读尾部 64KB，用 RightToLeft 在尾窗内取最后一个匹配，命中即 parse 返回
        ///   （省 IO 的主路径，覆盖端口行仍落在尾窗内的情况）；
        /// - **全文件扫描兜底**：尾窗未命中时，读整个日志文件并行 Regex.Matches，取**最后一次**匹配返回。
        ///   这覆盖了端口行打印在 **首行** 而日志已增长到尾窗不含该行的场景（游戏启动早期）——
        ///   该场景下不再丢命中（原实现会返回 null）。
        ///
        /// 已知限制（务必牢记，勿误解返回值）：
        /// - 端口每次运行随机，多个匹配取最后一个（文件被游戏覆盖写，通常只有一个匹配）；
        /// - 全文件扫描会读整个文件（日志可达数 MB），仅在尾窗未命中时触发，属必要的 IO 取舍。
        /// 调用方优先级：ports.json（游戏侧 UELoader 写入）> 本函数 > 默认端口 56574。
        /// </summary>
        internal static int? DiscoverDebugPortFromLog()
        {
            string logPath = Defaults.LogPath;
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
                    string tail = System.Text.Encoding.UTF8.GetString(buf, 0, n);
                    Match m = Regex.Match(tail, DebugPortPattern,
                        RegexOptions.RightToLeft | RegexOptions.Multiline);
                    if (m.Success)
                        return ParsePort(m);
                }

                // P2-MD-1 兜底：尾窗未命中 → 全文件扫描，取最后一次匹配（覆盖端口行在首行的情况）。
                Match last = null;
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, System.Text.Encoding.UTF8))
                {
                    foreach (Match im in Regex.Matches(reader.ReadToEnd(), DebugPortPattern, RegexOptions.Multiline))
                        last = im;
                }
                return last != null ? ParsePort(last) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>从已命中的端口匹配里抽取端口号；解析失败返回 null。</summary>
        static int? ParsePort(Match m)
        {
            return int.TryParse(m.Groups[1].Value, out int p) ? p : (int?)null;
        }

        /// <summary>把异常转成可读文本；VMDisconnectedException 统一措辞。</summary>
        static string FriendlyError(Exception ex, string fallback)
        {
            if (ex is VMDisconnectedException)
                return "与游戏的调试连接已断开";
            if (ex is SocketException)
                return fallback + "（" + ex.Message + "）";
            if (ex is IOException)
                return fallback + "（" + ex.Message + "）";
            return !string.IsNullOrEmpty(ex.Message) ? ex.Message : ex.GetType().Name;
        }

        static string FirstNonEmpty(params string[] values)
        {
            foreach (string v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return null;
        }

        /// <summary>探测是否有 RimWorld 进程在运行；有则返回描述（名称 + PID 列表），无则返回 null。</summary>
        static string FindRunningGame()
        {
            foreach (string name in new[] { "RimWorldWin64", "RimWorldLinux", "RimWorldMac", "RimWorld" })
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(name);
                    if (procs != null && procs.Length > 0)
                    {
                        var ids = new List<string>();
                        foreach (Process p in procs)
                        {
                            try { ids.Add(p.Id.ToString()); }
                            catch { }
                        }
                        return name + " (PID=" + string.Join(",", ids.ToArray()) + ")";
                    }
                }
                catch
                {
                    // 权限等异常忽略，仅影响该名字的探测结果
                }
            }
            return null;
        }

        /// <summary>默认调试主机（走配置：MCP_RIMDBG_HOST，缺省 127.0.0.1）。</summary>
        internal static string DefaultHost()
        {
            return Defaults.Host;
        }

        /// <summary>默认调试端口（走配置：MCP_RIMDBG_PORT，缺省 56574）。</summary>
        internal static int DefaultPort()
        {
            return Defaults.Port;
        }

        // ---- 自检测试钩子（仅 selftest 使用，不进入生产调用路径）----

        /// <summary>测试钩子：从当前线程直接获取 commandLock（用于 P1-MD-1 忙锁断言）。仅 selftest 用。</summary>
        internal void EnterCommandLockForTest()
        {
            Monitor.Enter(commandLock);
        }

        /// <summary>测试钩子：释放 test 通过 EnterCommandLockForTest 获取的 commandLock。仅 selftest 用。</summary>
        internal void ReleaseCommandLockForTest()
        {
            Monitor.Exit(commandLock);
        }

        /// <summary>
        /// 测试钩子：把会话置为「已 attach 且 vm 非空」的占位状态（不建立真实调试连接），
        /// 使 Resume/Suspend 越过 state-precheck 走到 TryEnterCommandLock，从而在纯逻辑
        /// selftest 中命中其「忙」分支。测试专用，不进入生产路径。
        /// 占位 vm 仅用于通过 vm==null 判断，绝不真正调用其成员（忙分支在拿到锁前即返回）。
        /// stateLock 保护以保证与生产并发语义一致。
        /// </summary>
        internal void SetAttachedForTest()
        {
            lock (stateLock)
            {
                state = SessionState.Attached;
#pragma warning disable SYSLIB0050 // 测试专用占位 vm：跳过构造函数得到非空实例，仅用于 vm==null 判断
                vm = (VirtualMachine)System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(typeof(VirtualMachine));
#pragma warning restore SYSLIB0050
                sessionSocket = null;
                sessionHost = null;
                sessionPort = 0;
                suspended = false;
                everResumed = false;
                lastDisconnectReason = "";
            }
        }

        /// <summary>测试钩子：把会话复位到未连接（与 SetAttachedForTest 配对），避免污染后续自测。仅 selftest 用。</summary>
        internal void ResetSessionStateForTest()
        {
            lock (stateLock)
            {
                state = SessionState.Disconnected;
                vm = null;
                sessionSocket = null;
                suspended = false;
                everResumed = false;
            }
        }

        /// <summary>测试钩子：触发事件日志上限裁剪（P1-MD-2 断言用）。仅 selftest 用。</summary>
        internal void DrainStaleSuspendEventsForTest()
        {
            DrainStaleSuspendEvents();
        }
    }
}

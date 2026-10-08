using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Verse;

namespace UELoader
{
    /// <summary>
    /// RimBridgeServer（GABP）收发状态监控 Harmony 补丁（独立补丁，不依赖 UESDdebuger 其他模块）。
    ///
    /// 背景：UESDdebuger 的 MCP 服务器作为 GABP 客户端连接游戏内 RimBridgeServer 模组（默认 5174 端口），
    /// 镜像其 rimbridge.* / rimworld.* 工具。当 MCP 请求无响应（GABP 假死、超时）时，难以区分是
    /// 「游戏没收到 GABP 消息」还是「收到但没回（发送失败）」。本补丁在游戏侧记录这两个事实：
    ///   - 是否接收到 GABP 信息：钩 Lib.GAB.Server.GabpServer.OnMessageReceived
    ///     （SetupTransportEvents 订阅 _transport.MessageReceived += OnMessageReceived，
    ///     每条客户端消息解析后的唯一入口）；
    ///   - 是否成功向 GABP 发送信息：钩 Lib.GAB.Transport.TcpConnection.SendMessageAsync
    ///     （所有出站帧的唯一 TCP 写入点，含 session/welcome、tools/list、tools/call 响应与事件推送；
    ///     成功/失败由返回 Task 的完成状态判定）。
    ///
    /// 实现约束（对 RimBridgeServer / Lib.GAB 均为外部程序集，编译期不引用）：
    ///   - 全部经反射解析类型/方法 + Harmony 手动 Patch；
    ///   - 目标方法不可用（Lib.GAB 结构变化 / RimBridgeServer 未启用）时仅记录日志，不阻断游戏；
    ///   - 状态读写均加锁，postfix 运行在线程池线程也安全；
    ///   - GetStatus() 返回 JSON 友好快照（供 UEHttpServer 的 GET /rimbridge/status 直接透传）。
    /// </summary>
    public static class RimBridgeGABPStatusPatch
    {
        public const string HarmonyId = "UESDdebuger.rimbridge.status";

        /// <summary>补丁是否安装成功（RimBridgeServer 未启用或结构变化时为 false）。</summary>
        public static bool Installed { get; private set; }

        /// <summary>运行时解析到的 Lib.GAB 程序集（未找到为 null）。</summary>
        public static Assembly LibGabAssembly { get; private set; }

        // ------------------------------------------------------------ 状态（锁保护）

        static readonly object gate = new object();

        // 接收（GABP -> 游戏）
        static bool receivedAny;
        static long receivedCount;
        static string lastReceivedAtUtc;
        static string lastReceivedType;   // request / response / event（GabpMessage.Type）
        static string lastReceivedId;

        // 发送（游戏 -> GABP）
        static bool sentAny;
        static long sentCount;
        static long sendFailCount;
        static bool lastSendSucceeded;
        static string lastSentAtUtc;
        static string lastSentType;
        static string lastSendError;

        // ------------------------------------------------------------ 安装

        /// <summary>
        /// 安装 GABP 收发监控补丁（幂等）。通常在 ExplorerBootstrap.Initialize（主线程、场景加载后）调用；
        /// 任何失败仅记录日志，不影响游戏与 RimBridgeServer 本身。
        /// </summary>
        public static void Install()
        {
            if (Installed)
                return;

            try
            {
                Assembly gab = FindLibGABAssembly();
                if (gab == null)
                {
                    Log.Message("[RimBridgeStatus] 未找到 Lib.GAB 程序集（RimBridgeServer 模组未启用？），GABP 收发监控补丁未安装");
                    return;
                }
                LibGabAssembly = gab;

                var harmony = new Harmony(HarmonyId);
                bool any = false;

                // 接收钩：GabpServer.OnMessageReceived(object sender, MessageReceivedEventArgs e)
                Type gabpServer = gab.GetType("Lib.GAB.Server.GabpServer");
                MethodInfo onReceived = gabpServer == null
                    ? null
                    : AccessTools.Method(gabpServer, "OnMessageReceived");
                if (onReceived != null)
                {
                    harmony.Patch(onReceived, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(RimBridgeGABPStatusPatch), nameof(OnMessageReceived_Postfix))));
                    Log.Message($"[RimBridgeStatus] 已钩住接收路径 {onReceived.DeclaringType.FullName}.{onReceived.Name}（记录是否接收到 GABP 信息）");
                    any = true;
                }

                // 发送钩：TcpConnection.SendMessageAsync(GabpMessage message, CancellationToken cancellationToken)
                Type tcpConnection = gab.GetType("Lib.GAB.Transport.TcpConnection");
                Type gabpMessage = gab.GetType("Lib.GAB.Protocol.GabpMessage");
                MethodInfo sendAsync = tcpConnection == null || gabpMessage == null
                    ? null
                    : AccessTools.Method(tcpConnection, "SendMessageAsync", new[] { gabpMessage, typeof(CancellationToken) });
                if (sendAsync != null)
                {
                    harmony.Patch(sendAsync, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(RimBridgeGABPStatusPatch), nameof(SendMessageAsync_Postfix))));
                    Log.Message($"[RimBridgeStatus] 已钩住发送路径 {sendAsync.DeclaringType.FullName}.{sendAsync.Name}（记录是否成功向 GABP 发送信息）");
                    any = true;
                }

                Installed = any;
                if (!any)
                    Log.Warning("[RimBridgeStatus] Lib.GAB 结构变化，未找到目标方法，GABP 收发监控补丁未生效");
            }
            catch (Exception ex)
            {
                Installed = false;
                Log.Error($"[RimBridgeStatus] 安装 GABP 收发监控补丁失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 在已加载程序集中按简单名查找 Lib.GAB（RimWorld 会加载每个启用模组 Assemblies 目录下全部 DLL）；
        /// 找不到时尝试 Assembly.Load 按名解析，仍失败返回 null。
        /// </summary>
        static Assembly FindLibGABAssembly()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(a.GetName().Name, "Lib.GAB", StringComparison.Ordinal))
                        return a;
                }
                catch
                {
                    // 个别程序集 GetName 可能抛异常，跳过继续
                }
            }
            try
            {
                return Assembly.Load("Lib.GAB");
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------ Harmony postfix（绝不向被钩方法抛异常）

        /// <summary>GabpServer.OnMessageReceived 后置：每收到一条 GABP 客户端消息记录一次。</summary>
        static void OnMessageReceived_Postfix(object sender, object e)
        {
            try
            {
                string type = null;
                string id = null;
                object msg = e?.GetType().GetProperty("Message")?.GetValue(e, null);
                if (msg != null)
                {
                    type = Convert.ToString(msg.GetType().GetProperty("Type")?.GetValue(msg, null));
                    id = Convert.ToString(msg.GetType().GetProperty("Id")?.GetValue(msg, null));
                }
                RecordReceived(type, id);
            }
            catch
            {
                // 监控失败不影响桥接
            }
        }

        /// <summary>TcpConnection.SendMessageAsync 后置：记录发送尝试；最终成功/失败经返回 Task 的续延回填。</summary>
        static void SendMessageAsync_Postfix(object message, object __result)
        {
            try
            {
                string type = null;
                if (message != null)
                    type = Convert.ToString(message.GetType().GetProperty("Type")?.GetValue(message, null));
                RecordSendAttempt(type);

                if (__result is Task task)
                    task.ContinueWith(RecordSendOutcome);
            }
            catch
            {
                // 监控失败不影响桥接
            }
        }

        static void RecordSendOutcome(Task task)
        {
            try
            {
                bool ok = task.Status == TaskStatus.RanToCompletion;
                string error = null;
                if (task.IsFaulted && task.Exception != null)
                    error = task.Exception.GetBaseException()?.Message ?? task.Exception.Message;
                else if (task.IsCanceled)
                    error = "cancelled";
                RecordSendOutcome(ok, error);
            }
            catch
            {
                // 忽略续延自身的异常
            }
        }

        // ------------------------------------------------------------ 状态记录

        static void RecordReceived(string type, string id)
        {
            bool first;
            lock (gate)
            {
                first = !receivedAny;
                receivedAny = true;
                receivedCount++;
                lastReceivedAtUtc = Now();
                if (!string.IsNullOrEmpty(type))
                    lastReceivedType = type;
                if (!string.IsNullOrEmpty(id))
                    lastReceivedId = id;
            }
            if (first)
                Log.Message($"[RimBridgeStatus] 首次接收到 GABP 信息（type={type ?? "?"}）");
        }

        static void RecordSendAttempt(string type)
        {
            lock (gate)
            {
                sentAny = true;
                sentCount++;
                lastSentAtUtc = Now();
                lastSendSucceeded = false;   // 最终结果由续延回填
                lastSendError = null;
                if (!string.IsNullOrEmpty(type))
                    lastSentType = type;
            }
        }

        static void RecordSendOutcome(bool ok, string error)
        {
            bool firstFail;
            lock (gate)
            {
                if (!ok)
                    sendFailCount++;
                lastSendSucceeded = ok;
                lastSendError = error;
                firstFail = !ok && sendFailCount == 1;
            }
            if (firstFail)
                Log.Message($"[RimBridgeStatus] 首次向 GABP 发送失败（error={error ?? "?"}）");
        }

        // ------------------------------------------------------------ 查询

        /// <summary>
        /// 返回 GABP 收发状态快照（响应契约 { success, data }，与 UE HTTP 端点一致）。
        /// data 顶层含两个语义布尔：receivedAny=是否接收到 GABP 信息；sendSucceeded=是否成功向 GABP 发送信息
        /// （最近一次发送的完成结果，未发送过为 false），另附计数与最近一次明细。
        /// </summary>
        public static Dictionary<string, object> GetStatus()
        {
            var data = new Dictionary<string, object>();
            lock (gate)
            {
                data["installed"] = Installed;
                data["rimBridgeAssemblyFound"] = LibGabAssembly != null;
                // 用户关心的两个布尔
                data["receivedAny"] = receivedAny;                    // 是否接收到 GABP 信息
                data["sendSucceeded"] = sentAny && lastSendSucceeded; // 是否成功向 GABP 发送信息

                var received = new Dictionary<string, object>
                {
                    { "count", receivedCount },
                    { "lastAtUtc", lastReceivedAtUtc ?? "" },
                    { "lastType", lastReceivedType ?? "" },
                    { "lastId", lastReceivedId ?? "" }
                };
                var sent = new Dictionary<string, object>
                {
                    { "count", sentCount },
                    { "failCount", sendFailCount },
                    { "lastAtUtc", lastSentAtUtc ?? "" },
                    { "lastType", lastSentType ?? "" },
                    { "lastSucceeded", sentAny && lastSendSucceeded },
                    { "lastError", lastSendError ?? "" }
                };
                data["received"] = received;
                data["sent"] = sent;
            }
            return new Dictionary<string, object> { { "success", true }, { "data", data } };
        }

        /// <summary>清空累计状态（便于「自某时刻起」的观测语义）。</summary>
        public static void Reset()
        {
            lock (gate)
            {
                receivedAny = false;
                receivedCount = 0;
                lastReceivedAtUtc = null;
                lastReceivedType = null;
                lastReceivedId = null;

                sentAny = false;
                sentCount = 0;
                sendFailCount = 0;
                lastSendSucceeded = false;
                lastSentAtUtc = null;
                lastSentType = null;
                lastSendError = null;
            }
        }

        static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss") + "Z";
        }
    }
}

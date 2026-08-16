using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    /// <summary>
    /// MCP 服务器宿主（stdio 传输）。
    ///
    /// 使用官方 ModelContextProtocol SDK 2.1.0：由于本项目仅还原了该 SDK 的
    /// Microsoft.Extensions.*Abstractions 传递依赖（没有具体 ServiceCollection/LoggerFactory 包），
    /// 因此不采用 DI 构建器，而是直接手工组装：
    ///   McpServerOptions（工具集合 + 能力声明）→ StdioServerTransport → McpServer.Create → RunAsync。
    ///
    /// 支持 --selftest 参数：不经 stdio，直接执行一次 status 工具验证会话核心可工作。
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            TruncationSink.Init(); // P1-MD-2.2：启动清扫旧截断文件
            if (args.Length > 0 && args[0] == "--selftest")
                return RunSelfTest();

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = "McpRimDebug",
                    Version = "0.9.0.0",
                    Description = "RimWorld Mono Soft Debugger MCP server",
                },
                ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal),
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            };

            RimDebugTools.Register(options.ToolCollection);

            var transport = new StdioServerTransport(options, NullLoggerFactory.Instance);
            var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, null);

            // 阻塞运行直到 stdin 关闭（MCP 客户端断开）
            await server.RunAsync();
            return 0;
        }

        /// <summary>
        /// 极简自检（不经 stdio，静态自测）：
        /// 1) 工具注册表包含全部 20 个工具；
        /// 2) 各工具的 InputSchema 完整（含声明的参数）；
        /// 3) 未连接时各工具返回可读错误（不崩溃）；
        /// 4) 配置环境变量（MCP_RIMDBG_*）生效；
        /// 5) 统一执行包装 Guard 把异常转可读文本；
        /// 6) status 返回增强字段（needResume / gameProcessRunning / portOpen）；
        /// 8) P1-MD-2 事件队列上限裁剪（EventLogMax，纯逻辑）；
        /// 9) P1-MD-1 命令锁忙警告（Threads 忙返回 / 不阻塞，纯逻辑）。
        /// </summary>
        private static int RunSelfTest()
        {
            try
            {
                TruncationSink.Init(); // P1-MD-2.2：启动清扫旧截断文件（selftest 起点）
                // ---- 1) 工具注册表完整性 ----
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation
                    {
                        Name = "McpRimDebug",
                        Version = "0.9.0.0",
                        Description = "RimWorld Mono Soft Debugger MCP server",
                    },
                    ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal),
                    Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                };
                RimDebugTools.Register(options.ToolCollection);

                // ---- 1) 工具注册表完整性（P3-MD-6：从硬编码 20 项收敛为最小核心集 + 遍历注册表反查） ----
                // 完整性校验只保留「核心必需工具」子集（这些缺失意味着注册回归），不枚举全部 20 个——
                // 其余工具是否注册、schema 是否合法交给下方「遍历注册表」自动覆盖，新增工具不再需要手改清单。
                string[] requiredTools =
                {
                    "status", "attach", "detach", "launch", "resume",
                    "break_add", "wait", "step", "threads", "eval",
                };
                foreach (string name in requiredTools)
                {
                    bool found = false;
                    foreach (McpServerTool t in options.ToolCollection)
                    {
                        if (t.ProtocolTool.Name == name) { found = true; break; }
                    }
                    if (!found)
                    {
                        Console.Error.WriteLine("selftest 失败: 缺少核心工具 " + name);
                        return 2;
                    }
                }

                // ---- 2) schema 完整性（遍历注册表反查，而非硬编码清单）----
                // 1) 所有已注册工具的 InputSchema 都必须非空（新增工具自动纳入校验，无需改本自测）；
                // 2) 仅对少数“参数语义敏感”的工具（如 eval/break_add）断言关键参数名出现在 schema 中。
                var paramAsserts = new Dictionary<string, string[]>
                {
                    ["break_add"] = new[] { "method", "line" },
                    ["break_exception"] = new[] { "type", "caught", "uncaught" },
                    ["wait"] = new[] { "eventType", "timeoutMs" },
                    ["step"] = new[] { "threadId", "direction" },
                    ["callstack"] = new[] { "threadId", "frameLimit" },
                    ["locals"] = new[] { "threadId", "frameIndex" },
                    ["inspect"] = new[] { "handle" },
                    ["eval"] = new[] { "expression", "threadId", "frameIndex" },
                    ["find_types"] = new[] { "query", "limit" },
                    ["find_methods"] = new[] { "query", "limit" },
                };
                foreach (McpServerTool t in options.ToolCollection)
                {
                    string name = t.ProtocolTool.Name;
                    string schema = t.ProtocolTool.InputSchema.ToString();
                    if (string.IsNullOrWhiteSpace(schema))
                    {
                        Console.Error.WriteLine("selftest 失败: 工具 " + name + " 的 InputSchema 为空");
                        return 2;
                    }
                    if (paramAsserts.TryGetValue(name, out string[] requiredParams))
                    {
                        foreach (string paramName in requiredParams)
                        {
                            if (schema.IndexOf(paramName, StringComparison.Ordinal) < 0)
                            {
                                Console.Error.WriteLine("selftest 失败: " + name + " 的 schema 缺少参数 " + paramName
                                    + "（schema: " + schema + "）");
                                return 2;
                            }
                        }
                    }
                }

                // ---- 3) 未连接时各工具返回可读错误（不崩溃） ----
                ToolResult[] disconnectedResults =
                {
                    DebugSession.Instance.BreakAdd("Verse.Thing:DoWork", null),
                    DebugSession.Instance.BreakList(),
                    DebugSession.Instance.BreakRemove(1),
                    DebugSession.Instance.BreakClear(),
                    DebugSession.Instance.BreakException(null, null, null),
                    DebugSession.Instance.Wait("breakpoint", 100),
                    DebugSession.Instance.Step(1, "into"),
                    DebugSession.Instance.Threads(),
                    DebugSession.Instance.Callstack(1, 50),
                    DebugSession.Instance.Locals(1, 0),
                    DebugSession.Instance.Inspect(1),
                    DebugSession.Instance.Eval("this.def.defName", 0, 0),
                    DebugSession.Instance.FindTypes("ThingDef", 50),
                    DebugSession.Instance.FindMethods("Tick", 50),
                };
                foreach (ToolResult r in disconnectedResults)
                {
                    if (r.Ok)
                    {
                        Console.Error.WriteLine("selftest 失败: 未连接时不应 ok，实际消息: " + r.Message);
                        return 2;
                    }
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(r));
                }

                // ---- 4) 表达式解析器单测（不连 VM，纯解析层） ----
                if (!RunParserSelfTest())
                    return 2;

                // ---- 5) 配置：环境变量 MCP_RIMDBG_* 生效 + 默认值（保存/恢复原 env） ----
                string savedHost = Environment.GetEnvironmentVariable("MCP_RIMDBG_HOST");
                string savedPort = Environment.GetEnvironmentVariable("MCP_RIMDBG_PORT");
                string savedGame = Environment.GetEnvironmentVariable("MCP_RIMDBG_GAME_PATH");
                try
                {
                    if (string.IsNullOrEmpty(Defaults.GamePath)
                        || !Defaults.GamePath.EndsWith("RimWorldWin64.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("selftest 失败: Defaults.GamePath 默认值不正确: " + Defaults.GamePath);
                        return 2;
                    }
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_HOST", "10.1.2.3");
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_PORT", "59999");
                    if (Defaults.Host != "10.1.2.3" || DebugSession.DefaultHost() != "10.1.2.3")
                    {
                        Console.Error.WriteLine("selftest 失败: MCP_RIMDBG_HOST 未生效");
                        return 2;
                    }
                    if (Defaults.Port != 59999 || DebugSession.DefaultPort() != 59999)
                    {
                        Console.Error.WriteLine("selftest 失败: MCP_RIMDBG_PORT 未生效");
                        return 2;
                    }
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_PORT", "not-a-port");
                    if (Defaults.Port != Defaults.FallbackPort)
                    {
                        Console.Error.WriteLine("selftest 失败: 非法 MCP_RIMDBG_PORT 应回退默认 " + Defaults.FallbackPort);
                        return 2;
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_HOST", savedHost);
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_PORT", savedPort);
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_GAME_PATH", savedGame);
                }

                // ---- 6) 统一执行包装 Guard：异常转可读 { ok:false, message } ----
                ToolResult guardOk = DebugSession.Instance.Guard("_selftest", false, () => ToolResult.OkResult("ok"));
                if (!guardOk.Ok)
                {
                    Console.Error.WriteLine("selftest 失败: Guard 应透传 ok 结果");
                    return 2;
                }
                ToolResult guardErr = DebugSession.Instance.Guard("_selftest", false, () => { throw new InvalidOperationException("boom"); });
                if (guardErr.Ok || string.IsNullOrEmpty(guardErr.Message) || guardErr.Message.IndexOf("boom", StringComparison.Ordinal) < 0)
                {
                    Console.Error.WriteLine("selftest 失败: Guard 未把异常转可读错误: " + (guardErr.Message ?? "null"));
                    return 2;
                }

                // ---- 7) status：ok=true 且 data 含增强字段（含断点统计） ----
                ToolResult st = DebugSession.Instance.Status();
                if (!st.Ok || st.Data == null
                    || !st.Data.ContainsKey("state")
                    || !st.Data.ContainsKey("needResume")
                    || !st.Data.ContainsKey("gameProcessRunning")
                    || !st.Data.ContainsKey("portOpen")
                    || !st.Data.ContainsKey("debugPortOpen")
                    || !st.Data.ContainsKey("debugPortFromLog")
                    || !st.Data.ContainsKey("breakpointsActive")
                    || !st.Data.ContainsKey("breakpointsAddedTotal")
                    || !st.Data.ContainsKey("breakpointsRemovedTotal")
                    || !st.Data.ContainsKey("eventRequestsActive"))
                {
                    Console.Error.WriteLine("selftest 失败: status 缺少增强字段（state/needResume/gameProcessRunning/portOpen/debugPortOpen/debugPortFromLog/breakpointsActive/breakpointsAddedTotal/breakpointsRemovedTotal/eventRequestsActive）");
                    return 2;
                }
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(st));

                // ---- 8) P1-MD-2：事件队列上限（纯逻辑，不连游戏）----
                // 用 internal 桩往 EventQueue 灌入 >EventLogMax 条非挂起事件，再调
                // DrainStaleSuspendEvents（统一经 EnqueueEvent 裁剪），断言队列不超上限。
                var dbs = DebugSession.Instance;
                while (dbs.EventQueue.TryTake(out _)) { }   // 清空，避免受前序步骤残留影响
                int fill = DebugSession.EventLogMax + 60;
                for (int i = 0; i < fill; i++)
                {
                    dbs.EventQueue.Add(new RawEvent
                    {
                        Type = Mono.Debugger.Soft.EventType.ThreadStart,
                        SuspendsVm = false,
                        Description = "selftest-stale#" + i,
                    });
                }
                dbs.DrainStaleSuspendEventsForTest();
                if (dbs.EventQueue.Count > DebugSession.EventLogMax)
                {
                    Console.Error.WriteLine("selftest 失败: P1-MD-2 事件队列超上限，Count=" + dbs.EventQueue.Count
                        + "，上限=" + DebugSession.EventLogMax);
                    return 2;
                }
                int drainedFill = fill > DebugSession.EventLogMax ? DebugSession.EventLogMax : fill;
                if (dbs.EventQueue.Count != drainedFill)
                {
                    Console.Error.WriteLine("selftest 失败: P1-MD-2 裁剪后 Count 应为 " + drainedFill + "，实际 " + dbs.EventQueue.Count);
                    return 2;
                }
                while (dbs.EventQueue.TryTake(out _)) { }   // 清理测试桩事件，避免污染后续

                // ---- 9) P1-MD-1：命令锁忙警告（纯逻辑，不连游戏）----
                // 用 internal 钩子在独立线程持住 commandLock，主线程调用短命令，
                // 断言返回 Ok=false、message 含「忙」、且不阻塞（秒级内返回）。
                // 说明：Monitor 同线程可重入，必须在不同线程持锁才能真正模拟"被占用"。
                // Resume/Suspend 的忙分支需 state==Attached && vm!=null 才可越过 precheck，
                // 纯逻辑下用 internal 测试钩子 SetAttachedForTest 置占位态即可命中该分支。
                dbs.SetAttachedForTest();
                try
                {
                    using (var lockHeld = new ManualResetEventSlim(false))
                    using (var releaseLock = new ManualResetEventSlim(false))
                    {
                        int holderError = 0;
                        var holder = new Thread(() =>
                        {
                            try
                            {
                                dbs.EnterCommandLockForTest();
                                lockHeld.Set();
                                releaseLock.Wait();          // 持锁挂起，直到主线程测完
                                dbs.ReleaseCommandLockForTest();
                            }
                            catch { holderError = 1; lockHeld.Set(); }
                        });
                        holder.IsBackground = true;
                        holder.Start();
                        if (!lockHeld.Wait(2000) || holderError != 0)
                        {
                            Console.Error.WriteLine("selftest 失败: P1-MD-1 无法在后台线程持住 commandLock");
                            return 2;
                        }

                        // Threads：忙分支（任何状态都可达，TryEnterCommandLock 在最前）
                        ToolResult busyThreads;
                        var sw = Stopwatch.StartNew();
                        busyThreads = dbs.Threads();
                        sw.Stop();
                        if (busyThreads.Ok || string.IsNullOrEmpty(busyThreads.Message)
                            || busyThreads.Message.IndexOf("忙", StringComparison.Ordinal) < 0)
                        {
                            Console.Error.WriteLine("selftest 失败: P1-MD-1 Threads 被占用时应返回忙警告，实际: " + busyThreads.Message);
                            return 2;
                        }
                        if (sw.ElapsedMilliseconds > 2000)
                        {
                            Console.Error.WriteLine("selftest 失败: P1-MD-1 Threads 忙返回不应阻塞，耗时 " + sw.ElapsedMilliseconds + "ms");
                            return 2;
                        }

                        // Resume：置 Attached 占位态后越过 precheck，走到 TryEnterCommandLock 忙分支
                        ToolResult busyResume;
                        var swResume = Stopwatch.StartNew();
                        busyResume = dbs.Resume();
                        swResume.Stop();
                        if (busyResume.Ok || string.IsNullOrEmpty(busyResume.Message)
                            || busyResume.Message.IndexOf("忙", StringComparison.Ordinal) < 0)
                        {
                            Console.Error.WriteLine("selftest 失败: P1-MD-1 Resume 在锁占用下应返回忙警告，实际 Ok="
                                + busyResume.Ok + " msg=" + busyResume.Message);
                            return 2;
                        }
                        if (swResume.ElapsedMilliseconds > 2000)
                        {
                            Console.Error.WriteLine("selftest 失败: P1-MD-1 Resume 忙返回不应阻塞，耗时 " + swResume.ElapsedMilliseconds + "ms");
                            return 2;
                        }

                        // Suspend：同上，走到 TryEnterCommandLock 忙分支
                        ToolResult busySuspend;
                        var swSuspend = Stopwatch.StartNew();
                        busySuspend = dbs.Suspend();
                        swSuspend.Stop();
                        if (busySuspend.Ok || string.IsNullOrEmpty(busySuspend.Message)
                            || busySuspend.Message.IndexOf("忙", StringComparison.Ordinal) < 0)
                        {
                            Console.Error.WriteLine("selftest 失败: P1-MD-1 Suspend 在锁占用下应返回忙警告，实际 Ok="
                                + busySuspend.Ok + " msg=" + busySuspend.Message);
                            return 2;
                        }
                        if (swSuspend.ElapsedMilliseconds > 2000)
                        {
                            Console.Error.WriteLine("selftest 失败: P1-MD-1 Suspend 忙返回不应阻塞，耗时 " + swSuspend.ElapsedMilliseconds + "ms");
                            return 2;
                        }

                        releaseLock.Set();
                        holder.Join(2000);
                    }
                }
                finally
                {
                    // 复位占位态，避免污染后续自测
                    dbs.ResetSessionStateForTest();
                }

                // ---- 10) P1-MD-2.2：TruncationSink 纯逻辑断言（不连 VM） ----
                if (!RunTruncationSinkSelfTest())
                    return 2;

                // ---- 11) P1-MD-3：句柄超阈值分批回收（纯逻辑，不连 VM） ----
                if (!RunHandleReclaimSelfTest())
                    return 2;

                // ---- 12) P2-MD-3：枚举去重（跨基类合并字段，纯逻辑，不连 VM） ----
                if (!RunDedupSelfTest())
                    return 2;

                // ---- 13) P2-MD-4：GetActiveTcpListeners 短时间窗缓存（可注入枚举源，不连 VM） ----
                if (!RunListenerCacheSelfTest())
                    return 2;

                // ---- 14) P2-MD-1 + P3-MD-4：端口发现（尾窗命中 + 全文件扫兜底首行 + ports.json 优先） ----
                if (!RunPortDiscoverySelfTest())
                    return 2;

                // ---- 15) P2-MD-5：Safe* 访问器失败返回 null，VMDisconnected 重新抛出（纯逻辑，不连 VM） ----
                if (!RunSafeAccessorSelfTest())
                    return 2;

                Console.WriteLine("selftest 通过: 20 个工具注册齐全，schema 完整，未连接行为正确，"
                    + "配置环境变量生效，统一异常包装可用，表达式解析器自测通过，"
                    + "P1-MD-2 事件队列上限 + P1-MD-1 命令锁忙警告 + P1-MD-2.2 TruncationSink + "
                    + "P1-MD-3 句柄分批回收 + P2-MD-3 枚举去重 + P2-MD-4 端口探测缓存 + "
                    + "P2-MD-1 端口发现全文件兜底 + P2-MD-5 Safe* 访问器 纯逻辑断言通过");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: " + ex);
                return 2;
            }
        }

        /// <summary>
        /// P1-MD-3：句柄超阈值分批回收纯逻辑断言（不连 VM）。
        /// 用泛型静态核心 ReclaimStaleHandles&lt;T&gt; 直接离线测试：T 用 long 作假对象（其值即 objId），
        /// isCollected 用可注入桩，不依赖真实 ObjectMirror/VM。
        /// 断言（全部用「注入集合/对象属性」构造，与 Dictionary 枚举顺序解耦）：
        /// 1) 句柄数 ≤ 阈值时不触发（返回 0、两表不变）；
        /// 2) 超阈值且注入 &gt;batchMax 个 known-stale 时，多轮增量推进、逐步收敛；
        ///    每轮回收 ≤ batchMax，最终 stale 全部被清，累计回收数 = 注入数，non-stale 集始终完整；
        /// 3) 返回值与实际删除一致，且句柄表/反查表双向一致；
        /// 4) isCollected 抛非 VMDisconnected 异常时跳过该项、不中断、该句柄保留。
        /// </summary>
        private static bool RunHandleReclaimSelfTest()
        {
            const int threshold = 3;
            const int batchMax = 2;
            try
            {
                // 构造 handle→假对象(即 objId=100+h) 与 反查表。
                Func<Dictionary<int, long>, Dictionary<long, int>> build =
                    handles =>
                    {
                        var reverse = new Dictionary<long, int>();
                        foreach (var kv in handles)
                            reverse[kv.Value] = kv.Key;
                        return reverse;
                    };

                // ---- 1) 句柄数 ≤ 阈值：不触发 ----
                var below = new Dictionary<int, long> { [1] = 101, [2] = 102, [3] = 103 };
                var belowRev = build(below);
                int belowReclaimed = DebugSession.ReclaimStaleHandles(
                    below, belowRev, v => v, v => v == 101, threshold, batchMax);
                if (belowReclaimed != 0 || below.Count != 3 || belowRev.Count != 3)
                    throw new Exception("阈值以下不应触发回收（reclaimed=" + belowReclaimed
                        + ", handle=" + below.Count + ", reverse=" + belowRev.Count + "）");

                // ---- 2) 超阈值：多轮增量推进、逐步收敛，且与字典枚举顺序解耦 ----
                // 用「注入集合」而非迭代序号定义 stale 判定：所有句柄对象按 objId 区分，
                // isCollected 只对被标为 stale 的 objId 判真，其余判假。这样核心删谁只取决于
                //「isCollected 所标记的集合 + 每轮扫描量」，与 Dictionary<int,long> 的枚举顺序无关。
                //
                // 数据构造上刻意保证「可移植收敛」：本收敛用例用专用的小阈值 convThreshold=1 且
                // 只放 1 个 non-stale 垫底（< batchMax=2）。于是无论字典序如何，每轮扫描的前 batchMax
                // 个候选里必然至少含 1 个 stale，不会出现「前置全非 stale 卡住推进」的退化；当 stale
                // 全清后 count 降到 ≤convThreshold，核心返回 0 → while 结束。故 got==0 时 stale 必然全清，
                // 全程不依赖 .NET 移除后跳跃保留插入序的实现细节。
                const int convThreshold = 1;
                var staleSet = new HashSet<long>();
                var top = new Dictionary<int, long>();
                const long nonStaleObj = 7000;
                const int staleCount = 9;             // 注入的 known-stale 数量（>batchMax，需多轮）
                top[1] = nonStaleObj;                 // 1 个 non-stale 垫底（保留）
                for (int i = 1; i <= staleCount; i++) // 注入 staleCount 个 known-stale
                {
                    long o = nonStaleObj + i;
                    top[i + 1] = o;
                    staleSet.Add(o);
                }
                var rev = build(top);

                int rounds = 0;
                int totalDeleted = 0;
                while (true)
                {
                    int got = DebugSession.ReclaimStaleHandles(
                        top, rev, v => v, v => staleSet.Contains(v), convThreshold, batchMax);
                    if (got < 0 || got > batchMax)
                        throw new Exception("单轮回收量应在 0..batchMax，got=" + got);
                    totalDeleted += got;
                    rounds++;
                    if (rounds > 20)
                        throw new Exception("增量回收超过 20 轮仍未收敛（疑似一次清空或死循环）");
                    // 返回值必须等于两表实际删除量（双向一致）
                    if (rev.Count != top.Count || top.Count != staleCount + 1 - totalDeleted)
                        throw new Exception("返回数=" + got + " 与句柄表(" + top.Count
                            + ")/反查表(" + rev.Count + ")不一致");
                    if (got == 0)
                        break;
                }
                // 多轮才收敛（>batchMax 个 stale 不可能一轮清完 → 证明增量、非一次清空）
                if (rounds < (staleCount + batchMax - 1) / batchMax)
                    throw new Exception("增量推进应至少 " + ((staleCount + batchMax - 1) / batchMax)
                        + " 轮才清完 " + staleCount + " 个 stale，实际 " + rounds);
                // 最终：累计回收数 = 注入的 stale 数，stale 全部被清
                if (totalDeleted != staleCount)
                    throw new Exception("累计回收数应=注入 stale 数 " + staleCount + "，实际=" + totalDeleted);
                if (top.Count != 1 || rev.Count != 1)
                    throw new Exception("收敛后应只剩 1 个 non-stale，handle=" + top.Count
                        + ", reverse=" + rev.Count);
                // non-stale 集始终完整：垫底句柄保留且反查指向正确
                //（配合上文 top.Count==1/rev.Count==1，唯一剩余条目即该 non-stale → 无 stale 残留）
                if (top.ContainsValue(nonStaleObj) == false || rev[nonStaleObj] != 1)
                    throw new Exception("non-stale 垫底 (objId=" + nonStaleObj + ") 被误删/错反查");

                // ---- 3) isCollected 抛非 VMDisconnected 异常：跳过该项、不中断、不影响其它回收 ----
                // 4 个句柄：objId=101 判定抛错、102/103 判 collected、104 判 not-collected。
                // 无论扫描到哪些，抛错项与非 stale 项都绝不被删（isCollected=false/抛错 → 不入 stale 集）；
                // 这一断言与字典序无关。
                var errCase = new Dictionary<int, long> { [1] = 101, [2] = 102, [3] = 103, [4] = 104 };
                var errRev = build(errCase);
                int errReclaimed = DebugSession.ReclaimStaleHandles(
                    errCase, errRev, v => v,
                    v => { if (v == 101) throw new InvalidOperationException("boom"); return v != 104; },
                    threshold, batchMax);
                if (errReclaimed < 0 || errReclaimed > batchMax)
                    throw new Exception("回收数应在 0..batchMax，got=" + errReclaimed);
                if (!errCase.ContainsKey(1) || errRev[101] != 1)
                    throw new Exception("抛错项(objId=101)应保留未删且反查完整");
                if (!errCase.ContainsKey(4) || errRev[104] != 4)
                    throw new Exception("non-stale 项(objId=104)应保留未删且反查完整");
                if (errCase.Count != errRev.Count || errCase.Count != 4 - errReclaimed)
                    throw new Exception("异常跳过场景返回数(" + errReclaimed + ")与两表删除量不一致");

                Console.WriteLine("    [句柄回收自测] 阈值不触发 / 多轮增量收敛(stale 全清+non-stale 保留) / 返回与实际删除一致 / 异常跳过 断言通过");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: P1-MD-3 句柄回收自测异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// P2-MD-3：FormatObject 跨基类合并去重纯逻辑断言（不连 VM）。
        /// 直接测试生产共用核心 ValueFormatter.IsFirstSeen（Ordinal HashSet 的 O(1) 命中）：
        /// 用同一批字段名模拟“基类链 + 派生类”合并后出现的重复名，断言去重后字段数正确、
        /// 重复名只保留首次出现、顺序保持。构建对象需要真实 ObjectMirror/VM 才能驱动 FormatObject，
        /// 故用其纯逻辑核心离线验证（如简报允许的降级路径）。
        /// </summary>
        private static bool RunDedupSelfTest()
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var all = new List<string>();
                // 模拟跨基类合并出现的字段名流：基类贡献 {F1,F2}，派生类同名覆盖 F2，并新增 F3。
                // FormatObject 从最派生类型先收（cur=o.Type），再沿基类链，故“首次出现”决定保留谁。
                string[] fieldNames = { "F2", "F3", "F1", "F2" }; // 含跨链重复名 F2
                foreach (string n in fieldNames)
                {
                    if (ValueFormatter.IsFirstSeen(seen, n))
                        all.Add(n);
                }
                int uniqueCount = all.Count; // F2,F3,F1 → 3
                if (uniqueCount != 3)
                {
                    Console.Error.WriteLine("selftest 失败: P2-MD-3 跨基类去重后字段数应为 3，实际 " + uniqueCount);
                    return false;
                }
                if (all[0] != "F2" || all[1] != "F3" || all[2] != "F1")
                {
                    Console.Error.WriteLine("selftest 失败: P2-MD-3 去重应保留首次出现的字段序（F2,F3,F1），实际 "
                        + string.Join(",", all.ToArray()));
                    return false;
                }
                // 重复名第二次出现时应被跳过（IsFirstSeen 返回 false）
                if (ValueFormatter.IsFirstSeen(seen, "F2"))
                {
                    Console.Error.WriteLine("selftest 失败: P2-MD-3 重复字段名第二次出现应被去重");
                    return false;
                }
                // 同名字段在基类链不同层级只计一次
                var seen2 = new HashSet<string>(StringComparer.Ordinal);
                int count = 0;
                foreach (string n in new[] { "A", "B", "B", "C" })
                {
                    if (ValueFormatter.IsFirstSeen(seen2, n))
                        count++;
                }
                if (count != 3)
                {
                    Console.Error.WriteLine("selftest 失败: P2-MD-3 基类{B}与派生{B,C}合并后唯一字段应为 3，实际 " + count);
                    return false;
                }
                Console.WriteLine("    [Enum去重自测] P2-MD-3 跨基类合并去重字段数正确（O(1) HashSet，重复名跳过、序保持）");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: P2-MD-3 枚举去重自测异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// P2-MD-4：GetActiveTcpListeners 短时间窗缓存断言（不连 VM）。
        /// 注入可计数的 Func&lt;IPEndPoint[]&gt; 枚举源，断言窗口内第二次查询不重复枚举
        /// （系统级监听表枚举是昂贵操作，应复用上次结果）。
        /// </summary>
        private static bool RunListenerCacheSelfTest()
        {
            try
            {
                int calls = 0;
                DebugSession.SetListenerSourceForTest(() =>
                {
                    calls++;
                    return new[] { new IPEndPoint(IPAddress.Loopback, 9999) };
                });
                try
                {
                    IPEndPoint[] first = DebugSession.GetActiveTcpListeners();
                    if (first == null || first.Length == 0)
                    {
                        Console.Error.WriteLine("selftest 失败: P2-MD-4 注入的监听表未被采用");
                        return false;
                    }
                    if (calls != 1)
                    {
                        Console.Error.WriteLine("selftest 失败: P2-MD-4 首次查询应枚举 1 次，实际 " + calls);
                        return false;
                    }
                    IPEndPoint[] second = DebugSession.GetActiveTcpListeners();
                    if (calls != 1)
                    {
                        Console.Error.WriteLine("selftest 失败: P2-MD-4 缓存窗口内第二次查询应复用缓存、不重复枚举（枚举次数 " + calls + "）");
                        return false;
                    }
                    // 语义不变：窗口内两次查询应拿到同一份监听表
                    if (!ReferenceEquals(first, second))
                    {
                        Console.Error.WriteLine("selftest 失败: P2-MD-4 窗口内应复用同一份监听表实例");
                        return false;
                    }
                    Console.WriteLine("    [端口探测缓存自测] P2-MD-4 窗口内第二次 GetActiveTcpListeners() 复用缓存、未重复枚举");
                    return true;
                }
                finally
                {
                    DebugSession.ResetListenerCacheForTest();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: P2-MD-4 端口探测缓存自测异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// P2-MD-1 / P3-MD-4 端口发现纯逻辑断言（不连 VM，离线可跑）。
        /// 通过 env 注入日志路径（MCP_RIMDBG_LOG_PATH）与端口文件路径（MCP_RIMDBG_PORTS_FILE），
        /// 直接调用 DebugSession 的端口发现逻辑（internal static，不改生产语义）：
        /// 1) 尾窗快速命中：端口行落在文件尾部 64KB 内时，DiscoverDebugPortFromLog 从尾窗返回该端口；
        /// 2) 全文件扫描兜底（首行场景）：端口行写于文件**首行**、其余填充到 >64KB 使尾窗不含首行时，
        ///    DiscoverDebugPortFromLog 仍能通过全文件扫描兜底拿到该端口（验证 P2-MD-1 必须生效）；
        /// 3) ports.json 优先：日志可解析出端口，但临时 ports.json（unityDebugPort=666）存在时，
        ///    DiscoverDebugPortFromPortsFile() ?? DiscoverDebugPortFromLog() 取 666（文件优先）。
        /// 返回是否全部通过。
        /// </summary>
        private static bool RunPortDiscoverySelfTest()
        {
            string logPath = null;
            string tailLogPath = null;
            string portsPath = null;
            string savedLog = Environment.GetEnvironmentVariable("MCP_RIMDBG_LOG_PATH");
            string savedPorts = Environment.GetEnvironmentVariable("MCP_RIMDBG_PORTS_FILE");
            try
            {
                string tmp = Path.GetTempPath();
                // ---- 2) 全文件扫描兜底：端口行在首行，尾窗（64KB）不含首行 ----
                logPath = Path.Combine(tmp, "mcpdbg_selftest_firstline_" + Guid.NewGuid().ToString("N") + ".log");
                var filler = new string('x', 70 * 1024); // >64KB，保证尾窗不含第一行
                File.WriteAllText(logPath,
                    "Starting managed debugger on port 12345" + Environment.NewLine + filler,
                    new System.Text.UTF8Encoding(false));
                Environment.SetEnvironmentVariable("MCP_RIMDBG_LOG_PATH", logPath);
                Environment.SetEnvironmentVariable("MCP_RIMDBG_PORTS_FILE", null);
                int? firstLinePort = DebugSession.DiscoverDebugPortFromLog();
                if (firstLinePort != 12345)
                {
                    Console.Error.WriteLine("selftest 失败: P2-MD-1 首行端口应经全文件扫描兜底拿到 12345，实际 "
                        + (firstLinePort == null ? "null" : firstLinePort.ToString()));
                    return false;
                }

                // ---- 1) 尾窗快速命中：端口行落在尾部 64KB 内 ----
                tailLogPath = Path.Combine(tmp, "mcpdbg_selftest_tail_" + Guid.NewGuid().ToString("N") + ".log");
                File.WriteAllText(tailLogPath,
                    filler + Environment.NewLine + "Starting managed debugger on port 45678" + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
                Environment.SetEnvironmentVariable("MCP_RIMDBG_LOG_PATH", tailLogPath);
                int? tailPort = DebugSession.DiscoverDebugPortFromLog();
                if (tailPort != 45678)
                {
                    Console.Error.WriteLine("selftest 失败: P2-MD-1 尾窗命中应返回 45678，实际 "
                        + (tailPort == null ? "null" : tailPort.ToString()));
                    return false;
                }

                // ---- 3) ports.json 优先（666 胜出）----
                portsPath = Path.Combine(tmp, "mcpdbg_selftest_ports_" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(portsPath, "{\"ueHttpPort\":3001,\"unityDebugPort\":666,\"updatedAt\":\"t\"}",
                    new System.Text.UTF8Encoding(false));
                Environment.SetEnvironmentVariable("MCP_RIMDBG_LOG_PATH", tailLogPath); // 日志仍可解析 45678
                Environment.SetEnvironmentVariable("MCP_RIMDBG_PORTS_FILE", portsPath);
                int? effective = DebugSession.DiscoverDebugPortFromPortsFile() ?? DebugSession.DiscoverDebugPortFromLog();
                if (effective != 666)
                {
                    Console.Error.WriteLine("selftest 失败: P2-MD-1 ports.json 应优先（666），实际 "
                        + (effective == null ? "null" : effective.ToString()));
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: P2-MD-1 端口发现自测异常: " + ex.Message);
                return false;
            }
            finally
            {
                Environment.SetEnvironmentVariable("MCP_RIMDBG_LOG_PATH", savedLog);
                Environment.SetEnvironmentVariable("MCP_RIMDBG_PORTS_FILE", savedPorts);
                foreach (string p in new[] { logPath, tailLogPath, portsPath })
                {
                    try { if (p != null && File.Exists(p)) File.Delete(p); }
                    catch { /* 清理临时测试文件失败忽略 */ }
                }
            }
        }

        /// <summary>
        /// P2-MD-5：Safe* 访问器纯逻辑断言（不连 VM）：
        /// 1) SafePropLong/SafePropBool/SafeFrameInt 读失败 → null（区别于 0/false/-1 假值）；
        /// 2) SafeFrame/SafePropLong 正常路径透传；
        /// 3) VMDisconnectedException 被重新抛出（交上层断连处理，而非吞掉）。
        /// </summary>
        private static bool RunSafeAccessorSelfTest()
        {
            try
            {
                // ---- 1) 读失败 → null ----
                long? lFail = DebugSession.SafePropLong(() => { throw new InvalidOperationException("boom"); });
                if (lFail.HasValue)
                    throw new Exception("SafePropLong 读失败应返回 null，实际 "
                        + (lFail.HasValue ? lFail.Value.ToString() : "null"));
                bool? bFail = DebugSession.SafePropBool(() => { throw new InvalidOperationException("boom"); });
                if (bFail.HasValue)
                    throw new Exception("SafePropBool 读失败应返回 null，实际 "
                        + (bFail.HasValue ? bFail.Value.ToString() : "null"));
                int? iFail = DebugSession.SafeFrameInt(() => { throw new InvalidOperationException("boom"); });
                if (iFail.HasValue)
                    throw new Exception("SafeFrameInt 读失败应返回 null，实际 "
                        + (iFail.HasValue ? iFail.Value.ToString() : "null"));
                string sFail = DebugSession.SafeFrame(() => { throw new InvalidOperationException("boom"); });
                if (sFail != null)
                    throw new Exception("SafeFrame 读失败应返回 null，实际 " + (sFail ?? "null"));

                // ---- 2) 正常路径透传 ----
                long? lOk = DebugSession.SafePropLong(() => 42L);
                if (!lOk.HasValue || lOk.Value != 42)
                    throw new Exception("SafePropLong 正常路径应透传 42，实际 "
                        + (lOk.HasValue ? lOk.Value.ToString() : "null"));
                string sOk = DebugSession.SafeFrame(() => "x");
                if (sOk != "x")
                    throw new Exception("SafeFrame(() => \"x\") 应返回 \"x\"，实际 " + (sOk ?? "null"));

                // ---- 3) VMDisconnectedException 被重新抛出（不被吞掉） ----
                bool rethrown = false;
                try
                {
                    DebugSession.SafePropLong(() => { throw new VMDisconnectedException(); });
                }
                catch (VMDisconnectedException) { rethrown = true; }
                if (!rethrown)
                    throw new Exception("VMDisconnectedException 应被 Safe* 重新抛出而非吞掉");

                Console.WriteLine("    [Safe* 访问器自测] 读失败返回 null / 正常透传 / VMDisconnected 重新抛出 断言通过");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: P2-MD-5 Safe* 访问器自测异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 表达式解析器单元断言（不连 VM，纯解析层）：
        /// 1) Describe() 应还原表达式文本（成员链 / 静态类型全名 / 方法调用 / 括号 / 字符串转义 / 数字）；
        /// 2) AST 结构（成员链嵌套、调用参数列表、字面量）；
        /// 3) NeedsThread / NeedsFrame 预判；
        /// 4) 语法错误应抛 ExprParseException。
        /// 返回是否全部通过。
        /// </summary>
        private static bool RunParserSelfTest()
        {
            try
            {
                // 1) 成员链重建（Describe 应还原表达式文本）
                AssertParse("this", "this");
                AssertParse("this.def.defName", "this.def.defName");
                AssertParse("x.Value.ToString()", "x.Value.ToString()");
                AssertParse("Verse.Thing", "Verse.Thing");
                AssertParse("Verse.Thing.SomeStatic", "Verse.Thing.SomeStatic");

                // 2) 方法调用参数列表（括号 + 逗号）
                AssertParse("obj.M(1, \"x\", null)", "obj.M(1, \"x\", null)");
                AssertParse("obj.M()", "obj.M()");
                AssertParse("Math.Max(1, 2.5f)", "Math.Max(1, 2.5f)");

                // 3) 括号分组（Describe 重建时括号消失，仅断言不抛异常且语义等价）
                AssertParse("(this).x", "this.x");
                AssertParse("((this).y).z", "this.y.z");

                // 4) 字符串转义与数字字面量保留原文本
                AssertParse("\"a\\n b\"", "\"a\\n b\"");
                AssertParse("\"say \\\"hi\\\"\"", "\"say \\\"hi\\\"\"");
                AssertParse("123", "123");
                AssertParse("-7", "-7");
                AssertParse("1.5d", "1.5d");
                AssertParse("null", "null");

                // 5) AST 结构
                ExprNode chain = ExprParser.Parse("this.def.defName");
                if (chain.Kind != ExprNodeKind.Member || chain.Text != "defName"
                    || chain.Target == null || chain.Target.Kind != ExprNodeKind.Member
                    || chain.Target.Text != "def"
                    || chain.Target.Target == null || chain.Target.Target.Kind != ExprNodeKind.This)
                    throw new Exception("成员链 AST 结构不正确: " + chain.Describe());

                ExprNode call = ExprParser.Parse("obj.M(1, \"x\")");
                if (call.Kind != ExprNodeKind.Member || !call.IsCall
                    || call.Args == null || call.Args.Count != 2
                    || call.Args[0].Kind != ExprNodeKind.Literal || call.Args[1].Kind != ExprNodeKind.Literal)
                    throw new Exception("方法调用 AST 结构不正确: " + call.Describe());

                ExprNode lit = ExprParser.Parse("\"str\"");
                if (lit.Kind != ExprNodeKind.Literal)
                    throw new Exception("字符串字面量应为 Literal 节点: " + lit.Describe());

                // 6) NeedsThread / NeedsFrame 预判
                if (!ExpressionEvaluator.NeedsFrame("this.x"))
                    throw new Exception("NeedsFrame(this.x) 应为 true");
                if (!ExpressionEvaluator.NeedsThread("obj.M(1)"))
                    throw new Exception("NeedsThread(obj.M(1)) 应为 true");
                if (ExpressionEvaluator.NeedsFrame("\"lit\""))
                    throw new Exception("NeedsFrame(字符串字面量) 应为 false");
                if (ExpressionEvaluator.NeedsThread("\"lit\""))
                    throw new Exception("NeedsThread(字符串字面量) 应为 false");

                // 7) 语法错误应抛 ExprParseException
                ExpectParseError(null);
                ExpectParseError("");
                ExpectParseError("this..x");
                ExpectParseError("(this");
                ExpectParseError("this.");
                ExpectParseError("\"未闭合");
                ExpectParseError("obj.M(1,");
                ExpectParseError(".obj");

                Console.WriteLine("    [解析器自测] 全部断言通过（成员链/调用参数/字面量/结构/预判/语法错误）");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: 表达式解析器自测异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// P1-MD-2.2：TruncationSink 纯逻辑断言（不连 VM）：
        /// 1) MCP_RIMDBG_TRUNCATE_LIMIT 覆盖 ReplyLimitChars；
        /// 2) 构造 &gt;阈值序列化 → NewTempPath/WriteFull 生成 %TEMP%\McpRimDebug\&lt;guid&gt;.json，
        ///    内容为完整 JSON，Report 含 truncated=true/fullBytes/fullFile；
        /// 3) AttachTruncation：有截断标记则挂报告并落盘，无标记则不挂；
        /// 4) SweepOnSessionEnd 删除本会话登记文件；
        /// 5) Init 过期清扫（旧 mtime 伪造文件被清）。
        /// </summary>
        private static bool RunTruncationSinkSelfTest()
        {
            string dir = TruncationSink.Dir;
            System.IO.Directory.CreateDirectory(dir);
            string saved = Environment.GetEnvironmentVariable("MCP_RIMDBG_TRUNCATE_LIMIT");
            try
            {
                // ---- 1) 环境变量覆盖 ReplyLimitChars ----
                Environment.SetEnvironmentVariable("MCP_RIMDBG_TRUNCATE_LIMIT", "7");
                if (TruncationSink.ReplyLimitChars != 7)
                    throw new Exception("MCP_RIMDBG_TRUNCATE_LIMIT=7 应使 ReplyLimitChars=7，实际 "
                        + TruncationSink.ReplyLimitChars);
                Environment.SetEnvironmentVariable("MCP_RIMDBG_TRUNCATE_LIMIT", "0");
                if (TruncationSink.ReplyLimitChars != TruncationSink.DefaultReplyLimitChars)
                    throw new Exception("非法 MCP_RIMDBG_TRUNCATE_LIMIT 应回退默认，实际 "
                        + TruncationSink.ReplyLimitChars);

                // ---- 2) 构造 > 阈值序列化 → 落盘 ----
                string full = new string('x', 50000) + System.Text.Json.JsonSerializer.Serialize(
                    new Dictionary<string, object> { ["k"] = "超限全量内容" });
                string path = TruncationSink.NewTempPath();
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("NewTempPath 应在 %TEMP%\\McpRimDebug 下: " + path);
                string fileName = System.IO.Path.GetFileName(path);
                if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^[0-9a-f]{32}\.json$"))
                    throw new Exception("文件名应为 <guid>.json: " + fileName);
                TruncationSink.WriteFull(path, full);
                if (!System.IO.File.Exists(path))
                    throw new Exception("WriteFull 未生成文件: " + path);
                if (System.IO.File.ReadAllText(path) != full)
                    throw new Exception("落盘内容与完整 JSON 不一致");
                long bytes = System.Text.Encoding.UTF8.GetByteCount(full);
                var rep = TruncationSink.Report(path, bytes, "超出回复上限");
                if (rep["truncated"] is bool tb && !tb)
                    throw new Exception("报告 truncated 应为 true");
                if (!(rep["fullBytes"] is long rBytes) || rBytes != bytes)
                    throw new Exception("报告 fullBytes 应为完整 UTF-8 字节数 " + bytes);
                if (rep["fullFile"] as string != path)
                    throw new Exception("报告 fullFile 应为落盘路径");
                Console.WriteLine("    [TruncationSink 自测] 全量落盘 + 报告 断言通过（bytes=" + bytes + "）");

                // ---- 3) AttachTruncation：有截断标记才挂 ----
                var withMarker = new Dictionary<string, object>
                {
                    ["value"] = new Dictionary<string, object> { ["kind"] = "object", ["truncated"] = true },
                };
                string full2 = "{\"full\":true,\"pad\":\"" + new string('y', 30000) + "\"}";
                bool attached = TruncationSink.AttachTruncation(withMarker, () => full2);
                if (!attached || !withMarker.ContainsKey("truncation"))
                    throw new Exception("有截断标记时应挂 truncation 报告");
                var trunc = withMarker["truncation"] as Dictionary<string, object>;
                if (trunc == null || !(trunc["truncated"] is bool tt) || !tt)
                    throw new Exception("AttachTruncation 报告 truncated 应为 true");
                string repFile = trunc["fullFile"] as string;
                if (string.IsNullOrEmpty(repFile) || !System.IO.File.Exists(repFile))
                    throw new Exception("AttachTruncation 应落盘全量文件: " + repFile);
                if (System.IO.File.ReadAllText(repFile) != full2)
                    throw new Exception("AttachTruncation 落盘内容应为全量 JSON");
                var noMarker = new Dictionary<string, object> { ["value"] = "plain" };
                if (TruncationSink.AttachTruncation(noMarker, () => "{\"unused\":true}"))
                    throw new Exception("无截断标记不应挂 truncation");
                if (noMarker.ContainsKey("truncation"))
                    throw new Exception("无截断标记不应写入 truncation 键");
                Console.WriteLine("    [TruncationSink 自测] AttachTruncation 判定/落盘/报告 断言通过");

                // ---- 4) SweepOnSessionEnd 删除登记文件 ----
                TruncationSink.SweepOnSessionEnd();
                if (System.IO.File.Exists(path) || System.IO.File.Exists(repFile))
                    throw new Exception("SweepOnSessionEnd 后本会话登记文件应被删除");

                // ---- 5) Init 过期清扫：伪造旧 mtime 文件被清 ----
                string stale = System.IO.Path.Combine(dir, "stale-" + Guid.NewGuid().ToString("N") + ".json");
                System.IO.File.WriteAllText(stale, "{}");
                System.IO.File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-10));
                TruncationSink.Init();
                if (System.IO.File.Exists(stale))
                    throw new Exception("Init 应清掉旧 mtime 伪造文件: " + stale);
                // 近期文件不应被 Init 误删
                string fresh = System.IO.Path.Combine(dir, "fresh-" + Guid.NewGuid().ToString("N") + ".json");
                System.IO.File.WriteAllText(fresh, "{}");
                TruncationSink.Init();
                if (!System.IO.File.Exists(fresh))
                    throw new Exception("Init 不应删除近期文件: " + fresh);
                System.IO.File.Delete(fresh);

                Console.WriteLine("    [TruncationSink 自测] 生命周期清理（Sweep/Init 过期清扫）断言通过");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: TruncationSink 自测异常: " + ex.Message);
                return false;
            }
            finally
            {
                Environment.SetEnvironmentVariable("MCP_RIMDBG_TRUNCATE_LIMIT", saved);
            }
        }

        /// <summary>断言 Parse(input).Describe() == expected。</summary>
        private static void AssertParse(string input, string expected)
        {
            ExprNode node = ExprParser.Parse(input);
            string actual = node.Describe();
            if (actual != expected)
                throw new Exception("Parse(" + (input ?? "<null>") + ").Describe() = " + actual + "，期望 " + expected);
        }

        /// <summary>断言 Parse(input) 应抛 ExprParseException（否则失败）。</summary>
        private static void ExpectParseError(string input)
        {
            try
            {
                ExprParser.Parse(input);
            }
            catch (ExprParseException)
            {
                return; // 期望的语法错误
            }
            throw new Exception("Parse(" + (input ?? "<null>") + ") 应抛出 ExprParseException 但未抛");
        }
    }
}

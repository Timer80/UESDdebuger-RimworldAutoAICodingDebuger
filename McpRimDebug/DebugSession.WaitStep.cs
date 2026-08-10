using System;
using System.Collections.Generic;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    /// <summary>
    /// Task 3：事件等待与单步执行工具（wait / step）。
    /// 两者共用事件队列：事件循环线程把 SDB EventSet 扁平化为 RawEvent 入队；
    /// 消费方（wait/step）用 TryTake 取出后按过滤器匹配，不匹配的事件暂存并放回队列，
    /// 因此"期间非匹配事件保留在队列供下次取用"。
    /// </summary>
    public sealed partial class DebugSession
    {
        /// <summary>step 等待 StepEvent 的超时（毫秒）。</summary>
        const int StepTimeoutMs = 30000;

        /// <summary>wait 命中时的调用栈摘要最大帧数（防输出爆炸，超限标注 framesTruncated）。</summary>
        const int WaitFramesSummaryMax = 10;

        // ---------------------------------------------------------------- wait

        /// <summary>
        /// 从事件队列阻塞取匹配事件（eventType=any 时不过滤），超时返回 ok=true + timeout 标记。
        /// 命中时返回事件类型、线程、命中位置与调用栈摘要（挂起时经 GetFrames 取前若干帧）。
        /// 期间取出的非匹配事件保留在队列，供后续 wait 取用。
        /// </summary>
        public ToolResult Wait(string eventType, int timeoutMs)
        {
            if (timeoutMs <= 0)
                return ToolResult.ErrorResult("timeoutMs 必须为正数（毫秒）");
            if (!TryParseEventType(eventType, out EventType? filter))
                return ToolResult.ErrorResult("未知 eventType: " + eventType
                    + "。支持: any / breakpoint / step / exception，或任意 SDB 事件名（如 threadStart、userLog）");

            var deferred = new List<RawEvent>();
            RawEvent hit = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            // ---- 阻塞取事件（不持命令锁，避免阻塞其他命令；仅锁内快速查状态） ----
            while (hit == null)
            {
                bool attached;
                lock (stateLock) { attached = state == SessionState.Attached; }
                if (!attached && eventQueue.Count == 0)
                    break; // 已断开且队列取空 → 报错

                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    break; // 超时

                RawEvent raw;
                if (!eventQueue.TryTake(out raw, Math.Min(remaining, 250)))
                    continue; // 分段超时：回到循环检查截止时间/断开

                if (filter.HasValue && raw.Type != filter.Value)
                {
                    deferred.Add(raw);
                    continue;
                }
                hit = raw;
            }

            // 放回未匹配事件，保持队列供下次消费（经限流维护事件日志上限）
            foreach (RawEvent d in deferred)
                EnqueueEvent(d);

            if (hit == null)
            {
                bool attached;
                lock (stateLock) { attached = state == SessionState.Attached; }
                if (!attached)
                    return ToolResult.ErrorResult("连接已断开，等待中止");

                var tdata = new Dictionary<string, object>
                {
                    ["timeout"] = true,
                    ["eventType"] = filter.HasValue ? filter.Value.ToString() : "any",
                    ["waitMs"] = timeoutMs,
                    ["eventLogMax"] = EventLogMax,
                };
                return ToolResult.OkResult("等待超时（" + timeoutMs + "ms），未收到匹配事件；期间非匹配事件已保留在队列供下次取用"
                    + "（事件日志上限 " + EventLogMax + " 条，超出丢弃最旧）", tdata);
            }

            // ---- 命中：在命令锁内解析线程/位置/调用栈（涉及 socket，需挂起） ----
            lock (commandLock)
            {
                bool attached;
                lock (stateLock) { attached = state == SessionState.Attached; }
                if (!attached && hit.Type != EventType.VMDisconnect)
                {
                    // 已断开：只能给出事件本身描述，无法解析调用栈
                    return ToolResult.OkResult("命中 " + hit.Type + "（连接已断开）: " + hit.Description,
                        new Dictionary<string, object>
                        {
                            ["eventType"] = hit.Type.ToString(),
                            ["description"] = hit.Description,
                        });
                }
                try
                {
                    var data = BuildEventData(hit);
                    return ToolResult.OkResult("命中 " + hit.Type + ": " + hit.Description, data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("wait 解析事件时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("wait 失败: " + FriendlyError(ex, "解析事件"));
                }
            }
        }

        // ---------------------------------------------------------------- step

        /// <summary>
        /// 单步执行指定线程（direction=into/over/out，粒度 Line）：
        /// 创建 StepEventRequest → Enable → Resume → 等待 StepEvent → Disable → 返回命中位置。
        /// 等待期间若 VM 被其他事件挂起（如断点命中），step 中止并提示。
        /// </summary>
        public ToolResult Step(long threadId, string direction)
        {
            StepDepth depth;
            if (!TryParseDepth(direction, out depth))
                return ToolResult.ErrorResult("未知 direction: " + direction + "（支持 into / over / out）");
            if (threadId <= 0)
                return ToolResult.ErrorResult("非法 threadId: " + threadId);

            lock (commandLock)
            {
                VirtualMachine target;
                lock (stateLock)
                {
                    if (state != SessionState.Attached || vm == null)
                        return ToolResult.ErrorResult("未连接，无法单步（请先 attach）");
                    target = vm;
                }

                try
                {
                    ThreadMirror thread = FindThread(target, threadId);
                    if (thread == null)
                        return ToolResult.ErrorResult("未找到线程 " + threadId + "。可用线程: " + ListThreads(target));

                    StepEventRequest req = target.CreateStepRequest(thread);
                    req.Depth = depth;
                    // 无调试符号的方法（Locations 空，如发布版游戏程序集）没有行级序列点，
                    // 行级（Line）单步时 mono 不产生 StepEvent（30s 超时实测）；自动退化为
                    // 指令级（Min）单步。带 PDB 的模组 DLL 仍走 Line。
                    req.Size = PickStepSize(thread);

                    // 丢弃队列中残留的历史挂起事件：断点命中时代理挂起有延迟，高频方法
                    // 入口断点（如主循环 Update）可能在挂起生效前产生多个命中事件，wait 只消费
                    // 一个，其余残留；若不清理，WaitStepEvent 会把这些历史事件误判为
                    // 「step 被其他事件打断」而中止（14:53 实测）。清理后再 Enable + Resume。
                    DrainStaleSuspendEvents();

                    req.Enable();
                    int stepReqId = req.GetId();

                    target.Resume();
                    lock (stateLock) { suspended = false; everResumed = true; }

                    RawEvent hit = WaitStepEvent(stepReqId, StepTimeoutMs);

                    if (hit == null)
                    {
                        try { req.Disable(); } catch { }
                        return ToolResult.ErrorResult("step 超时（" + StepTimeoutMs + "ms），未收到 StepEvent；"
                            + "VM 可能已恢复运行（可用 suspend/resume 控制）");
                    }
                    if (hit.Type != EventType.Step)
                    {
                        // VM 被其他事件挂起（如断点/异常命中），step 无法完成
                        try { req.Disable(); } catch { }
                        lock (stateLock) { suspended = true; }
                        return ToolResult.ErrorResult("step 被 " + hit.Type + " 事件打断（VM 已挂起），请先处理该事件后 resume 再重试");
                    }

                    lock (stateLock) { suspended = true; }
                    try { req.Disable(); } catch { }

                    var data = BuildEventData(hit);
                    data["stepSize"] = req.Size == StepSize.Min ? "min（指令级，无调试符号自动退化）" : "line（行级）";
                    object location = data.ContainsKey("location") ? data["location"] : null;
                    return ToolResult.OkResult("step 命中" + (location != null ? " @ " + location : "（无行信息）"), data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("step 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("step 失败: " + FriendlyError(ex, "单步执行"));
                }
            }
        }

        // ---------------------------------------------------------------- 事件解析

        /// <summary>
        /// 把命中的 RawEvent 解析为结构化 data：事件类型/请求 id/挂起标记/线程/命中位置/调用栈摘要。
        /// 必须在命令锁内调用（GetFrames 等走 socket），且仅当 VM 挂起时可安全取帧。
        /// </summary>
        Dictionary<string, object> BuildEventData(RawEvent raw)
        {
            var data = new Dictionary<string, object>
            {
                ["eventType"] = raw.Type.ToString(),
                ["requestId"] = raw.RequestId,
                ["suspendsVm"] = raw.SuspendsVm,
                ["description"] = raw.Description,
                ["timestamp"] = raw.Timestamp.ToString("o"),
                ["suspended"] = IsSuspended(),
            };

            Event e = raw.Source;
            if (e == null)
                return data;

            ThreadMirror thread = null;
            try { thread = e.Thread; }
            catch (VMDisconnectedException) { throw; }
            catch { }

            if (thread != null)
            {
                data["threadId"] = thread.Id;
                try { data["threadName"] = thread.Name; }
                catch (VMDisconnectedException) { throw; }
                catch { }
            }

            string location = DescribeHitLocation(e);
            if (location != null)
                data["location"] = location;

            // 调用栈摘要：挂起状态下经 GetFrames 取前 WaitFramesSummaryMax 帧（方法 + file:line），超限标注
            if (thread != null)
            {
                var frames = new List<object>();
                bool fetched = false;
                if (raw.SuspendsVm || IsSuspended())
                {
                    try
                    {
                        StackFrame[] fs = thread.GetFrames();
                        int n = Math.Min(fs.Length, WaitFramesSummaryMax);
                        for (int i = 0; i < n; i++)
                        {
                            StackFrame f = fs[i];
                            frames.Add(new Dictionary<string, object>
                            {
                                ["method"] = SafeFrame(() => f.Method.FullName),
                                ["file"] = SafeFrame(() => f.Location.SourceFile),
                                ["line"] = SafeFrameInt(() => f.Location.LineNumber),
                                ["ilOffset"] = SafeFrameInt(() => f.Location.ILOffset),
                            });
                        }
                        fetched = true;
                        if (fs.Length > n)
                            data["framesTruncated"] = true;
                    }
                    catch (VMDisconnectedException) { throw; }
                    catch (Exception fex)
                    {
                        frames.Add(new Dictionary<string, object> { ["error"] = fex.Message });
                        fetched = true;
                    }
                }
                if (fetched)
                    data["frames"] = frames;
                else
                    data["frames"] = new List<object>(); // VM 未挂起，无法取帧
            }
            return data;
        }

        /// <summary>事件命中位置描述（断点=方法名；step=方法+file:line；异常=异常类型）。</summary>
        static string DescribeHitLocation(Event e)
        {
            try
            {
                if (e is BreakpointEvent bp)
                {
                    if (bp.Method == null)
                        return null;
                    return bp.Method.FullName;
                }
                if (e is StepEvent se)
                {
                    if (se.Method == null)
                        return null;
                    Location loc = se.Method.LocationAtILOffset((int)se.Location);
                    if (loc != null && loc.LineNumber > 0)
                        return se.Method.FullName + " @ " + loc.SourceFile + ":" + loc.LineNumber;
                    return se.Method.FullName + " @il=" + se.Location;
                }
                if (e is ExceptionEvent ex)
                {
                    ObjectMirror exc = ex.Exception;
                    if (exc != null)
                    {
                        try { return "异常: " + exc.Type.FullName; }
                        catch { return "异常"; }
                    }
                    return "异常";
                }
            }
            catch (VMDisconnectedException) { throw; }
            catch { }
            return null;
        }

        /// <summary>从队列等待指定请求 id 的 StepEvent；若 VM 先被其他挂起事件打断则返回该事件（消费掉，不放回）。</summary>
        RawEvent WaitStepEvent(int requestId, int timeoutMs)
        {
            var deferred = new List<RawEvent>();
            RawEvent hit = null;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (hit == null)
            {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0)
                    break;
                RawEvent raw;
                if (!eventQueue.TryTake(out raw, Math.Min(remaining, 250)))
                    continue;
                if (raw.Type == EventType.Step && (requestId == 0 || raw.RequestId == requestId))
                {
                    hit = raw;
                    break;
                }
                if (raw.SuspendsVm && raw.Type != EventType.Step)
                {
                    hit = raw; // VM 已被其他事件挂起，step 无法完成
                    break;
                }
                deferred.Add(raw);
            }
            foreach (RawEvent d in deferred)
                EnqueueEvent(d);
            return hit;
        }

        /// <summary>
        /// 丢弃队列中残留的历史挂起事件（仅丢弃 SuspendsVm=true 的，其余原样放回队尾）。
        /// 用于 step 开始前清理：断点命中时代理挂起有延迟，高频方法可能在挂起生效前产生
        /// 多个命中事件，wait 只消费一个，其余残留会成为 WaitStepEvent 的误判来源。
        /// </summary>
        void DrainStaleSuspendEvents()
        {
            var keep = new List<RawEvent>();
            RawEvent e;
            while (eventQueue.TryTake(out e, 0))
            {
                if (!e.SuspendsVm)
                    keep.Add(e);
            }
            foreach (RawEvent k in keep)
                eventQueue.Add(k);
        }

        /// <summary>
        /// 根据线程当前帧方法是否带调试符号选择单步粒度：无符号（Locations 空，发布版
        /// 程序集）→ 指令级 Min；带符号（模组 PDB）→ 行级 Line。仅在 VM 挂起时可安全取帧，
        /// 取帧失败时保守回退 Line。
        /// </summary>
        static StepSize PickStepSize(ThreadMirror thread)
        {
            try
            {
                StackFrame[] frames = thread.GetFrames();
                if (frames.Length > 0 && frames[0].Method != null && frames[0].Method.Locations.Count == 0)
                    return StepSize.Min;
            }
            catch (VMDisconnectedException) { throw; }
            catch { }
            return StepSize.Line;
        }

        // ---------------------------------------------------------------- 解析辅助

        static bool TryParseEventType(string eventType, out EventType? filter)
        {
            filter = null;
            if (string.IsNullOrWhiteSpace(eventType) || eventType.Trim().Equals("any", StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (EventType et in Enum.GetValues(typeof(EventType)))
            {
                if (string.Equals(et.ToString(), eventType.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    filter = et;
                    return true;
                }
            }
            return false;
        }

        static bool TryParseDepth(string direction, out StepDepth depth)
        {
            depth = StepDepth.Into;
            if (string.IsNullOrWhiteSpace(direction))
                return true; // 缺省 into
            switch (direction.Trim().ToLowerInvariant())
            {
                case "into": depth = StepDepth.Into; return true;
                case "over": depth = StepDepth.Over; return true;
                case "out": depth = StepDepth.Out; return true;
                default: return false;
            }
        }

        /// <summary>按线程 id（ObjectMirror.Id 或 ThreadId 均可）查找 ThreadMirror。</summary>
        ThreadMirror FindThread(VirtualMachine target, long id)
        {
            IList<ThreadMirror> threads = target.GetThreads();
            foreach (ThreadMirror t in threads)
            {
                if (t.Id == id || t.ThreadId == id)
                    return t;
            }
            return null;
        }

        /// <summary>列出当前线程（id + 名称），用于错误提示。</summary>
        static string ListThreads(VirtualMachine target)
        {
            try
            {
                IList<ThreadMirror> threads = target.GetThreads();
                var parts = new List<string>();
                foreach (ThreadMirror t in threads)
                {
                    string name = "";
                    try { name = t.Name ?? ""; }
                    catch (VMDisconnectedException) { throw; }
                    catch { }
                    parts.Add("#" + t.Id + (name.Length > 0 ? "(" + name + ")" : ""));
                }
                return parts.Count == 0 ? "（无线程）" : string.Join(", ", parts);
            }
            catch (VMDisconnectedException) { throw; }
            catch (Exception ex)
            {
                return "（列出线程失败: " + ex.Message + "）";
            }
        }

        bool IsSuspended()
        {
            lock (stateLock) { return suspended; }
        }

        /// <summary>单帧字段安全取值（帧字段解析失败不拖垮整条调用栈）。</summary>
        static string SafeFrame(Func<string> getter)
        {
            try { return getter(); }
            catch (VMDisconnectedException) { throw; }
            catch { return null; }
        }

        static int SafeFrameInt(Func<int> getter)
        {
            try { return getter(); }
            catch (VMDisconnectedException) { throw; }
            catch { return -1; }
        }
    }
}

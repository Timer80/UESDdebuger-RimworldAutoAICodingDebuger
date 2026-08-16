using System;
using System.Collections.Generic;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    /// <summary>
    /// Task 4（计划阶段 3）：观测工具 threads / callstack / locals / inspect，
    /// 以及对象句柄缓存（IObjectHandleStore 实现）与值格式化器入口。
    ///
    /// 约定：
    /// - 全部观测工具都在全局命令锁内执行（所有 socket 读写串行化）；
    /// - 全部观测工具都要求 VM 已挂起（见 TryGetObservationTarget），未挂起时给出可读错误；
    /// - VMDisconnectedException 统一处理为"连接已断开"，并复位会话；
    /// - 所有返回均为 { ok, message?, data? }（ToolResult）。
    /// </summary>
    public sealed partial class DebugSession
    {
        // ---- 值格式化器（惰性创建；仅观测工具在命令锁内使用，句柄登记经本会话） ----
        ValueFormatter valueFormatter;

        ValueFormatter Formatter
        {
            get
            {
                if (valueFormatter == null)
                    valueFormatter = new ValueFormatter(this);
                return valueFormatter;
            }
        }

        // ---------------------------------------------------------------- IObjectHandleStore

        /// <summary>获取对象句柄（同一 SDB 对象 id 复用同一句柄；首次出现时登记递增序号）。</summary>
        public int GetOrCreateHandle(ObjectMirror obj)
        {
            if (obj == null)
                return 0;
            lock (handlesLock)
            {
                long objId = obj.Id;
                if (objectHandles.TryGetValue(objId, out int existing))
                    return existing;
                int handle = nextHandle++;
                objectHandles[objId] = handle;
                handleObjects[handle] = obj;
                return handle;
            }
        }

        /// <summary>按句柄解析对象；句柄无效/已失效（断连后清空）返回 null。</summary>
        public ObjectMirror ResolveHandle(int handle)
        {
            lock (handlesLock)
            {
                return handleObjects.TryGetValue(handle, out ObjectMirror obj) ? obj : null;
            }
        }

        /// <summary>当前缓存的句柄数。</summary>
        public int HandleCount
        {
            get { lock (handlesLock) { return handleObjects.Count; } }
        }

        // ---------------------------------------------------------------- P1-MD-3 句柄超阈值分批回收

        /// <summary>句柄数超过该值才触发回收。</summary>
        const int HandleReclaimThreshold = 500;
        /// <summary>每次回收时最多判定的候选句柄数（增量回收，不一次清空）。</summary>
        const int HandleReclaimBatchMax = 64;

        /// <summary>超阈值时，只用 IsCollected 分批回收一部分（P1-MD-3 定案）。返回本次回收数。</summary>
        internal int MaybeReclaimHandles()
        {
            lock (handlesLock)
            {
                // IsCollected 是 SDB socket 调用，调用方须已持 commandLock 且 VM 挂起（观测工具前置保证）。
                return ReclaimStaleHandles(handleObjects, objectHandles,
                    om => om.Id, om => om.IsCollected,
                    HandleReclaimThreshold, HandleReclaimBatchMax);
            }
        }

        /// <summary>
        /// 批次回收纯逻辑核心（P1-MD-3，可离线测试）：
        /// 仅在当前句柄数超过 threshold 时，扫描前 batchMax 个句柄，用 isCollected 判定是否已被 GC 回收，
        /// 对回收项做「句柄表 + 反查表」双向删除；单次删除量不会超过 batchMax（增量，不一次清空）。
        /// 返回本次实际回收数。isCollected 抛 VMDisconnectedException 时向上重抛（交上层处理断连）。
        /// 不触碰 handlesLock/commandLock（由调用方保证在锁内执行）。
        /// </summary>
        internal static int ReclaimStaleHandles<T>(
            Dictionary<int, T> handles,   // handle → 对象
            Dictionary<long, int> reverse, // objId → handle（反查删除）
            Func<T, long> idOf,            // 对象 → objId（反查键）
            Func<T, bool> isCollected,     // 判定对象是否已被 GC 回收
            int threshold,
            int batchMax)
        {
            if (handles.Count <= threshold)
                return 0;
            var stale = new List<int>();
            int scanned = 0;
            foreach (int h in handles.Keys)
            {
                if (scanned++ >= batchMax)
                    break;
                T o = handles[h];
                bool collected;
                try { collected = isCollected(o); }   // socket 调用，须在 commandLock 内
                catch (VMDisconnectedException) { throw; } // 交上层统一断连
                catch { continue; }
                if (collected)
                    stale.Add(h);
            }
            foreach (int h in stale)
            {
                if (handles.TryGetValue(h, out var om))
                {
                    handles.Remove(h);
                    reverse.Remove(idOf(om)); // 反查：handle→objId 清理
                }
            }
            return stale.Count;
        }

        // ---------------------------------------------------------------- 挂起检查

        /// <summary>
        /// 观测工具统一前置检查：已连接（Attached）且 VM 挂起。
        /// 失败时输出可读错误文本（未连接 / VM 未挂起提示先 wait/step 或 suspend）。
        /// </summary>
        bool TryGetObservationTarget(out VirtualMachine target, out string error)
        {
            lock (stateLock)
            {
                if (state != SessionState.Attached || vm == null)
                {
                    target = null;
                    error = "未连接，无法执行此操作（请先 attach）";
                    return false;
                }
                if (!suspended)
                {
                    target = null;
                    error = "VM 未挂起，无法观测：线程/调用栈/变量/对象字段仅在挂起时可读。"
                        + "请先调用 suspend，或用 wait/step 等待断点/步进命中后再试";
                    return false;
                }
                target = vm;
                error = null;
                return true;
            }
        }

        // ---------------------------------------------------------------- threads

        /// <summary>列出当前 VM 全部线程（id / 原生 threadId / 名称 / 线程状态）。需 VM 挂起。</summary>
        public ToolResult Threads()
        {
            if (!TryEnterCommandLock(out string busy))
                return ToolResult.ErrorResult(busy);
            try
            {
                if (!TryGetObservationTarget(out VirtualMachine target, out string err))
                    return ToolResult.ErrorResult(err);
                try
                {
                    IList<ThreadMirror> threads = target.GetThreads();
                    var list = new List<object>();
                    foreach (ThreadMirror t in threads)
                    {
                        // P2-MD-5：Safe* 失败返回 null，null 时标记 <read-error>，避免 0/false 假值掩盖失败
                        long? tid = SafePropLong(() => t.ThreadId);
                        bool? isThreadPool = SafePropBool(() => t.IsThreadPoolThread);
                        list.Add(new Dictionary<string, object>
                        {
                            // id = 调试器对象 id，即 callstack/locals/step 的 threadId 参数
                            ["id"] = t.Id,
                            // threadId = 原生线程唯一 id（跨 appdomain 可能重复）
                            ["threadId"] = tid.HasValue ? (object)tid.Value : "<read-error>",
                            ["name"] = SafeFrame(() => t.Name),
                            ["threadState"] = SafeFrame(() => t.ThreadState.ToString()),
                            ["isThreadPoolThread"] = isThreadPool.HasValue ? (object)isThreadPool.Value : "<read-error>",
                        });
                    }
                    var data = new Dictionary<string, object>
                    {
                        ["count"] = list.Count,
                        ["threads"] = list,
                    };
                    return ToolResult.OkResult("共 " + list.Count + " 个线程", data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("threads 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (VMNotSuspendedException)
                {
                    return ToolResult.ErrorResult("VM 未挂起（状态已变化），无法读取线程信息；请重新 wait/step 到挂起点");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("threads 失败: " + FriendlyError(ex, "列出线程"));
                }
            }
            finally { ExitCommandLock(); }
        }

        // ---------------------------------------------------------------- callstack

        /// <summary>获取指定线程的调用栈（逐帧：方法全名 / IL 偏移 / 源码 file:line）。需 VM 挂起。</summary>
        public ToolResult Callstack(long threadId, int frameLimit)
        {
            if (frameLimit <= 0)
                return ToolResult.ErrorResult("frameLimit 必须为正数（缺省 50）");
            if (!TryEnterCommandLock(out string busy))
                return ToolResult.ErrorResult(busy);
            try
            {
                if (!TryGetObservationTarget(out VirtualMachine target, out string err))
                    return ToolResult.ErrorResult(err);
                try
                {
                    ThreadMirror thread = FindThread(target, threadId);
                    if (thread == null)
                        return ToolResult.ErrorResult("未找到线程 " + threadId + "。可用线程: " + ListThreads(target));

                    StackFrame[] frames = thread.GetFrames();
                    int n = Math.Min(frames.Length, frameLimit);
                    var list = new List<object>();
                    for (int i = 0; i < n; i++)
                        list.Add(FormatFrame(frames[i], i));

                    var data = new Dictionary<string, object>
                    {
                        ["threadId"] = thread.Id,
                        ["frameCount"] = frames.Length,
                        ["frameLimit"] = frameLimit,
                        ["framesTruncated"] = frames.Length > frameLimit,
                        ["frames"] = list,
                    };
                    // P1-MD-2.2：存在截断标记（framesTruncated）时全量落盘 + 截断报告
                    TruncationSink.AttachTruncation(data, () =>
                        System.Text.Json.JsonSerializer.Serialize(BuildFullCallstackSnapshot(thread.Id, frames)));
                    string msg = "线程 #" + thread.Id + " 调用栈共 " + frames.Length
                        + " 帧（显示前 " + n + " 帧）";
                    if (frames.Length == 0)
                        msg += "；0 帧通常表示 VM 未挂起或线程刚创建——需先命中目标断点（VM 挂起）后再取帧";
                    return ToolResult.OkResult(msg, data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("callstack 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (VMNotSuspendedException)
                {
                    return ToolResult.ErrorResult("VM 未挂起（状态已变化），无法读取调用栈；请重新 wait/step 到挂起点");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("callstack 失败: " + FriendlyError(ex, "读取调用栈"));
                }
            }
            finally { ExitCommandLock(); }
        }

        // ---------------------------------------------------------------- locals

        /// <summary>
        /// 获取指定线程指定栈帧的实参 / 局部变量 / this 及格式化取值（子对象返回句柄供 inspect）。
        /// 需 VM 挂起。实参按参数表逐个读取；局部变量按可见范围（live range）批量读取；
        /// 无调试符号（AbsentInformationException）时返回实参与 this，局部变量置空并标注。
        /// </summary>
        public ToolResult Locals(long threadId, int frameIndex)
        {
            if (frameIndex < 0)
                return ToolResult.ErrorResult("frameIndex 不能为负（0=最内层帧）");
            if (!TryEnterCommandLock(out string busy))
                return ToolResult.ErrorResult(busy);
            try
            {
                if (!TryGetObservationTarget(out VirtualMachine target, out string err))
                    return ToolResult.ErrorResult(err);
                try
                {
                    ThreadMirror thread = FindThread(target, threadId);
                    if (thread == null)
                        return ToolResult.ErrorResult("未找到线程 " + threadId + "。可用线程: " + ListThreads(target));

                    StackFrame[] frames = thread.GetFrames();
                    if (frameIndex >= frames.Length)
                        return ToolResult.ErrorResult("frameIndex " + frameIndex + " 超出调用栈范围（共 "
                            + frames.Length + " 帧，0=最内层）");
                    StackFrame frame = frames[frameIndex];

                    // P1-MD-3：超阈值时先回收一批已 GC 的句柄（本工具已持 commandLock 且 VM 挂起）
                    int reclaimed = MaybeReclaimHandles();

                    // P2-MD-5：Safe* 失败返回 null，数值字段 null 时标记 <read-error>
                    int? ilOffset = SafeFrameInt(() => frame.Location.ILOffset);
                    int? lineNumber = SafeFrameInt(() => frame.Location.LineNumber);
                    var frameInfo = new Dictionary<string, object>
                    {
                        ["method"] = SafeFrame(() => { frame.Method.GetParameters(); return frame.Method.FullName; }),
                        ["ilOffset"] = ilOffset.HasValue ? (object)ilOffset.Value : "<read-error>",
                        ["sourceFile"] = SafeFrame(() => frame.Location.SourceFile),
                        ["lineNumber"] = lineNumber.HasValue ? (object)lineNumber.Value : "<read-error>",
                    };

                    // ---- this（静态方法/无 this 帧返回 null 或抛错，均不致命） ----
                    bool hasThis = false;
                    object thisVal = null;
                    Value thisRaw = null;
                    try
                    {
                        thisRaw = frame.GetThis();
                        hasThis = true;
                        thisVal = Formatter.Format(thisRaw);
                    }
                    catch (VMDisconnectedException) { throw; }
                    catch { hasThis = false; thisVal = null; thisRaw = null; }

                    // ---- 实参：按参数表逐个读取 ----
                    var args = new List<object>();
                    var argRaw = new List<Value>();
                    var argNames = new List<string>();
                    var argTypes = new List<string>();
                    bool argsOk = false;
                    try
                    {
                        ParameterInfoMirror[] ps = frame.Method.GetParameters();
                        for (int i = 0; i < ps.Length; i++)
                        {
                            Value v = null;
                            try { v = frame.GetValue(ps[i]); }
                            catch (VMDisconnectedException) { throw; }
                            catch { v = null; }
                            string name = !string.IsNullOrEmpty(ps[i].Name) ? ps[i].Name : "arg" + i;
                            string type = SafeFrame(() => ps[i].ParameterType.FullName);
                            argRaw.Add(v);
                            argNames.Add(name);
                            argTypes.Add(type);
                            args.Add(new Dictionary<string, object>
                            {
                                ["name"] = name,
                                ["type"] = type,
                                ["value"] = Formatter.Format(v),
                            });
                        }
                        argsOk = true;
                    }
                    catch (VMDisconnectedException) { throw; }
                    catch (Exception ex)
                    {
                        args.Add(new Dictionary<string, object> { ["error"] = "实参读取失败: " + ex.Message });
                    }

                    // ---- 局部变量：可见范围（live range）内批量读取 ----
                    var locals = new List<object>();
                    var localNames = new List<string>();
                    var localTypes = new List<string>();
                    var localRaw = new List<Value>();
                    bool debugInfo = true;
                    try
                    {
                        var visible = frame.GetVisibleVariables();
                        var vars = new List<LocalVariable>(visible).ToArray();
                        Value[] values = frame.GetValues(vars);
                        for (int i = 0; i < vars.Length; i++)
                        {
                            string type = SafeFrame(() => vars[i].Type.FullName);
                            Value v = values != null && i < values.Length ? values[i] : null;
                            localNames.Add(vars[i].Name);
                            localTypes.Add(type);
                            localRaw.Add(v);
                            locals.Add(new Dictionary<string, object>
                            {
                                ["name"] = vars[i].Name,
                                ["type"] = type,
                                ["value"] = Formatter.Format(v),
                            });
                        }
                    }
                    catch (VMDisconnectedException) { throw; }
                    catch (AbsentInformationException)
                    {
                        debugInfo = false; // 该帧无调试符号：仅实参与 this 可用
                    }
                    catch (Exception ex)
                    {
                        locals.Add(new Dictionary<string, object> { ["error"] = "局部变量读取失败: " + ex.Message });
                    }

                    var data = new Dictionary<string, object>
                    {
                        ["threadId"] = thread.Id,
                        ["frameIndex"] = frameIndex,
                        ["frame"] = frameInfo,
                        ["hasThis"] = hasThis,
                        ["this"] = thisVal,
                        ["args"] = args,
                        ["locals"] = locals,
                        ["hasDebugInfo"] = debugInfo,
                        ["count"] = (argsOk ? args.Count : 0) + locals.Count + (hasThis ? 1 : 0),
                        ["reclaimedHandles"] = reclaimed,
                    };
                    // P1-MD-2.2：存在截断标记（this/args/locals 内嵌字段超限）时全量落盘 + 截断报告
                    TruncationSink.AttachTruncation(data, () => System.Text.Json.JsonSerializer.Serialize(
                        BuildFullLocalsSnapshot(thread.Id, frameIndex, frameInfo, hasThis, thisRaw,
                            argsOk, argRaw, argNames, argTypes, localNames, localTypes, localRaw, debugInfo)));
                    return ToolResult.OkResult("线程 #" + thread.Id + " 帧 #" + frameIndex
                        + "：this=" + (hasThis ? "有" : "无")
                        + "，实参 " + (argsOk ? args.Count : 0) + " 个，局部变量 " + locals.Count + " 个", data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("locals 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (VMNotSuspendedException)
                {
                    return ToolResult.ErrorResult("VM 未挂起（状态已变化），无法读取变量；请重新 wait/step 到挂起点");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("locals 失败: " + FriendlyError(ex, "读取局部变量"));
                }
            }
            finally { ExitCommandLock(); }
        }

        // ---------------------------------------------------------------- inspect

        /// <summary>
        /// 按对象句柄展开对象字段（经格式化器：深度≤3、字段≤50、字符串≤512、数组前≤32 元素），
        /// 子对象返回新句柄供继续展开。需 VM 挂起。
        /// </summary>
        public ToolResult Inspect(int handle)
        {
            if (handle <= 0)
                return ToolResult.ErrorResult("非法句柄: " + handle + "（句柄为正整数，来自 locals/inspect 输出）");
            if (!TryEnterCommandLock(out string busy))
                return ToolResult.ErrorResult(busy);
            try
            {
                if (!TryGetObservationTarget(out VirtualMachine target, out string err))
                    return ToolResult.ErrorResult(err);
                ObjectMirror obj = ResolveHandle(handle);
                if (obj == null)
                    return ToolResult.ErrorResult("句柄 #" + handle + " 无效或已失效（断连/会话复位后句柄清空；请从 locals/inspect 重新获取）");
                // P1-MD-3：超阈值时先回收一批已 GC 的句柄（本工具已持 commandLock 且 VM 挂起）
                int reclaimed = MaybeReclaimHandles();
                try
                {
                    bool collected = false;
                    try { collected = obj.IsCollected; }
                    catch (VMDisconnectedException) { throw; }
                    catch { }
                    if (collected)
                        return ToolResult.ErrorResult("句柄 #" + handle + " 指向的对象已被 GC 回收");

                    object formatted = Formatter.Format(obj);
                    var data = new Dictionary<string, object>
                    {
                        ["handle"] = handle,
                        ["value"] = formatted,
                        ["reclaimedHandles"] = reclaimed,
                    };
                    // P1-MD-2.2：展开值存在截断标记（深度/字段/字符串/数组超限）时全量落盘 + 截断报告
                    TruncationSink.AttachTruncation(data, () => System.Text.Json.JsonSerializer.Serialize(
                        new Dictionary<string, object>
                        {
                            ["handle"] = handle,
                            ["value"] = Formatter.FormatFull(obj),
                        }));
                    return ToolResult.OkResult("句柄 #" + handle + " 已展开", data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("inspect 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (VMNotSuspendedException)
                {
                    return ToolResult.ErrorResult("VM 未挂起（状态已变化），无法读取对象字段；请重新 wait/step 到挂起点");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("inspect 失败: " + FriendlyError(ex, "展开对象"));
                }
            }
            finally { ExitCommandLock(); }
        }

        // ---------------------------------------------------------------- 解析辅助

        /// <summary>Safe* 安全取值：读失败返回 null（区别于真实值 0/false）；VMDisconnectedException 重新抛出（交上层断连）。</summary>
        internal static long? SafePropLong(Func<long?> getter)
        {
            try { return getter(); }
            catch (VMDisconnectedException) { throw; }
            catch { return null; }
        }

        internal static bool? SafePropBool(Func<bool?> getter)
        {
            try { return getter(); }
            catch (VMDisconnectedException) { throw; }
            catch { return null; }
        }

        // ---------------------------------------------------------------- P1-MD-2.2 全量快照构建

        /// <summary>单帧结构化条目（callstack 与 callstack 全量快照共用）。P2-MD-5：数值字段失败以 <read-error> 标记。</summary>
        static Dictionary<string, object> FormatFrame(StackFrame f, int index)
        {
            int? ilOffset = SafeFrameInt(() => f.Location.ILOffset);
            int? lineNumber = SafeFrameInt(() => f.Location.LineNumber);
            return new Dictionary<string, object>
            {
                ["index"] = index,
                // FullName 依赖 param_info 惰性初始化，先 GetParameters() 再取全名
                ["method"] = SafeFrame(() => { f.Method.GetParameters(); return f.Method.FullName; }),
                ["ilOffset"] = ilOffset.HasValue ? (object)ilOffset.Value : "<read-error>",
                ["sourceFile"] = SafeFrame(() => f.Location.SourceFile),
                ["lineNumber"] = lineNumber.HasValue ? (object)lineNumber.Value : "<read-error>",
            };
        }

        /// <summary>callstack 全量快照（不受 frameLimit 限制，含全部帧），供超限回复落盘。</summary>
        static Dictionary<string, object> BuildFullCallstackSnapshot(long threadId, StackFrame[] frames)
        {
            var all = new List<object>();
            for (int i = 0; i < frames.Length; i++)
                all.Add(FormatFrame(frames[i], i));
            return new Dictionary<string, object>
            {
                ["threadId"] = threadId,
                ["frameCount"] = frames.Length,
                ["frameLimit"] = frames.Length,
                ["framesTruncated"] = false,
                ["frames"] = all,
            };
        }

        /// <summary>
        /// locals 全量快照（this/实参/局部变量均用 FormatFull 递归到最底，不受各上限约束），
        /// 供超限回复落盘。结构与受限版 [local] data["this"/"args"/"locals"] 对齐。
        /// </summary>
        Dictionary<string, object> BuildFullLocalsSnapshot(long threadId, int frameIndex, Dictionary<string, object> frameInfo,
            bool hasThis, Value thisRaw,
            bool argsOk, List<Value> argRaw, List<string> argNames, List<string> argTypes,
            List<string> localNames, List<string> localTypes, List<Value> localRaw, bool debugInfo)
        {
            var fullArgs = new List<object>();
            if (argsOk)
            {
                for (int i = 0; i < argRaw.Count; i++)
                {
                    fullArgs.Add(new Dictionary<string, object>
                    {
                        ["name"] = i < argNames.Count ? argNames[i] : "arg" + i,
                        ["type"] = i < argTypes.Count ? argTypes[i] : null,
                        ["value"] = Formatter.FormatFull(i < argRaw.Count ? argRaw[i] : null),
                    });
                }
            }

            var fullLocals = new List<object>();
            if (debugInfo)
            {
                for (int i = 0; i < localRaw.Count; i++)
                {
                    fullLocals.Add(new Dictionary<string, object>
                    {
                        ["name"] = i < localNames.Count ? localNames[i] : "local" + i,
                        ["type"] = i < localTypes.Count ? localTypes[i] : null,
                        ["value"] = Formatter.FormatFull(i < localRaw.Count ? localRaw[i] : null),
                    });
                }
            }

            return new Dictionary<string, object>
            {
                ["threadId"] = threadId,
                ["frameIndex"] = frameIndex,
                ["frame"] = frameInfo,
                ["hasThis"] = hasThis,
                ["this"] = hasThis ? Formatter.FormatFull(thisRaw) : null,
                ["args"] = fullArgs,
                ["locals"] = fullLocals,
                ["hasDebugInfo"] = debugInfo,
                ["count"] = fullArgs.Count + fullLocals.Count + (hasThis ? 1 : 0),
            };
        }
    }
}

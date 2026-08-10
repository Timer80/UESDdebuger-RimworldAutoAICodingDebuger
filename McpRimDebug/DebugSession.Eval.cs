using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    /// <summary>
    /// Task 5（计划阶段 4）：求值与搜索工具（eval / find_types / find_methods）。
    ///
    /// eval：挂起-求值-恢复窗口——VM 运行态时自动 vm.Suspend() → 求值 → finally vm.Resume()（异常路径也恢复，
    /// 避免冻结游戏）；VM 已挂起则直接求值。表达式经 ExpressionEvaluator 求值，结果用 ValueFormatter 输出。
    ///
    /// find_types / find_methods：先经 vm.GetTypes 精确匹配（mono 端），再用 Cecil 元数据做子串匹配。
    /// 限流防卡死：每程序集一次 GetMetadata（含一次 socket 元数据拉取），本地遍历类型/方法树；
    /// 最多扫描 SearchMaxAssemblies 个程序集，结果达 limit 即停止。
    /// </summary>
    public sealed partial class DebugSession
    {
        /// <summary>元数据搜索最多检查的程序集数（防卡死：每程序集一次元数据 blob 拉取）。</summary>
        const int SearchMaxAssemblies = 150;

        /// <summary>搜索 limit 参数上限。</summary>
        const int SearchMaxResults = 200;

        /// <summary>嵌套类型遍历深度上限。</summary>
        const int SearchMaxNestedDepth = 4;

        ExpressionEvaluator evaluator;

        ExpressionEvaluator Evaluator
        {
            get
            {
                if (evaluator == null)
                    evaluator = new ExpressionEvaluator();
                return evaluator;
            }
        }

        // ---------------------------------------------------------------- eval

        /// <summary>
        /// 在游戏进程内求值表达式（迷你 C# 语法：this / 局部变量 / 实参 / 静态类型全名 / 字面量；
        /// 成员链 .字段 .属性 .方法(args)）。VM 运行态自动挂起求值后恢复；已挂起则直接求值。
        /// threadId/frameIndex 用于解析 this 与局部变量/实参（threadId=0 自动选线程）。
        /// </summary>
        public ToolResult Eval(string expression, long threadId, int frameIndex)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return ToolResult.ErrorResult("expression 不能为空。示例: this / this.def.defName / "
                    + "Verse.Thing.SomeStaticField / obj.GetValue(1, \"x\") / \"字符串字面量\"");
            if (threadId < 0)
                return ToolResult.ErrorResult("threadId 不能为负（0=自动选择线程）");
            if (frameIndex < 0)
                return ToolResult.ErrorResult("frameIndex 不能为负（0=最内层帧）");

            // 语法预检（纯解析，不涉及 VM；出错无需挂起）
            ExprNode parsed;
            try
            {
                parsed = ExprParser.Parse(expression);
            }
            catch (ExprParseException pe)
            {
                return ToolResult.ErrorResult("表达式语法错误: " + pe.Message + "（位置 " + pe.Position + "）");
            }
            bool needsThread = ExpressionEvaluator.NeedsThread(parsed);
            bool needsFrame = ExpressionEvaluator.NeedsFrame(parsed);

            lock (commandLock)
            {
                VirtualMachine target;
                lock (stateLock)
                {
                    if (state != SessionState.Attached || vm == null)
                        return ToolResult.ErrorResult("未连接，无法求值（请先 attach）");
                    target = vm;
                }

                bool weSuspended = false;
                try
                {
                    // ---- 挂起窗口：VM 运行态 → 自动挂起；已挂起 → 直接求值 ----
                    if (!IsSuspended())
                    {
                        try
                        {
                            target.Suspend();
                            weSuspended = true;
                        }
                        catch (VMNotSuspendedException)
                        {
                            weSuspended = false; // 已是挂起态
                        }
                        catch (VMDisconnectedException)
                        {
                            HandleDisconnect("eval 挂起时 VM 断开");
                            return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                        }
                        lock (stateLock) { suspended = true; }
                    }

                    try
                    {
                        // ---- 线程/帧上下文（表达式需要才解析） ----
                        ThreadMirror thread = null;
                        StackFrame frame = null;
                        if (needsThread)
                        {
                            thread = ResolveEvalThread(target, threadId);
                            if (thread == null)
                                return ToolResult.ErrorResult("未找到线程 " + threadId
                                    + (threadId > 0 ? "。可用线程: " + ListThreads(target) : "（当前 VM 无可用线程）"));
                        }
                        if (needsFrame)
                        {
                            frame = ResolveEvalFrame(thread, frameIndex);
                            if (frame == null)
                                return ToolResult.ErrorResult("frameIndex " + frameIndex + " 超出线程 #"
                                    + (thread != null ? thread.Id.ToString() : "?") + " 的调用栈范围（0=最内层）");
                        }

                        var ctx = new EvalContext { Vm = target, Thread = thread, Frame = frame };
                        EvalResult result = Evaluator.Evaluate(ctx, parsed);

                        var data = new Dictionary<string, object>
                        {
                            ["expression"] = expression,
                            ["threadId"] = thread != null ? (object)thread.Id : null,
                            ["frameIndex"] = frame != null ? (object)frameIndex : null,
                            ["autoSuspended"] = weSuspended,
                        };
                        if (result.StaticType != null)
                        {
                            string tn = SafeTypeName(result.StaticType);
                            data["value"] = new Dictionary<string, object>
                            {
                                ["kind"] = "type",
                                ["type"] = tn,
                            };
                            return ToolResult.OkResult("求值结果（静态类型引用）: " + tn, data);
                        }
                        data["value"] = Formatter.Format(result.Value);
                        return ToolResult.OkResult("求值完成", data);
                    }
                    finally
                    {
                        // ---- 恢复窗口：异常路径也恢复，避免冻结游戏 ----
                        if (weSuspended)
                        {
                            try { target.Resume(); }
                            catch (VMNotSuspendedException) { }
                            catch (VMDisconnectedException) { HandleDisconnect("eval 恢复时 VM 断开"); }
                            catch { }
                            lock (stateLock) { suspended = false; }
                        }
                    }
                }
                catch (EvalException ex)
                {
                    return ToolResult.ErrorResult("求值失败: " + ex.Message);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("eval 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("求值失败: " + FriendlyError(ex, "求值表达式"));
                }
            }
        }

        ThreadMirror ResolveEvalThread(VirtualMachine target, long threadId)
        {
            if (threadId > 0)
                return FindThread(target, threadId);
            IList<ThreadMirror> ts = target.GetThreads();
            return ts != null && ts.Count > 0 ? ts[0] : null;
        }

        StackFrame ResolveEvalFrame(ThreadMirror thread, int frameIndex)
        {
            StackFrame[] frames = thread.GetFrames();
            if (frames == null || frameIndex >= frames.Length)
                return null;
            return frames[frameIndex];
        }

        // ---------------------------------------------------------------- find_types

        /// <summary>
        /// 在游戏程序集元数据中按子串搜索类型（大小写不敏感），返回类型全名列表。
        /// 先 mono 端精确匹配，再 Cecil 元数据子串匹配；限流防卡死（分装配批次遍历 + 总数上限）。
        /// </summary>
        public ToolResult FindTypes(string query, int limit)
        {
            if (string.IsNullOrWhiteSpace(query))
                return ToolResult.ErrorResult("query 不能为空（子串匹配类型全名/简单名，大小写不敏感，如 ThingDef）");
            int lim = NormalizeLimit(limit);

            lock (commandLock)
            {
                VirtualMachine target;
                lock (stateLock)
                {
                    if (state != SessionState.Attached || vm == null)
                        return ToolResult.ErrorResult("未连接，无法搜索类型（请先 attach）");
                    target = vm;
                }
                try
                {
                    var results = new List<object>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    bool truncated = false;

                    // 1) mono 端精确匹配（完整名/简单名，在游戏进程内执行，快）
                    try
                    {
                        IList<TypeMirror> exact = target.GetTypes(query, true);
                        if (exact != null)
                        {
                            foreach (TypeMirror t in exact)
                                AddTypeName(results, seen, SafeTypeName(t));
                        }
                    }
                    catch (VMDisconnectedException) { throw; }
                    catch { }

                    // 2) Cecil 元数据子串匹配（每程序集一次元数据拉取，本地遍历，限流）
                    if (results.Count < lim)
                        ScanAssemblyMetadata(target, query, lim, seen, results, out truncated, false, null);

                    var data = new Dictionary<string, object>
                    {
                        ["query"] = query,
                        ["limit"] = lim,
                        ["count"] = results.Count,
                        ["truncated"] = truncated,
                        ["types"] = results,
                    };
                    return ToolResult.OkResult("找到 " + results.Count + " 个匹配类型"
                        + (truncated ? "（达到扫描/结果上限，结果不完整；可用更精确的 query 缩小范围）" : ""), data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("find_types 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("find_types 失败: " + FriendlyError(ex, "搜索类型"));
                }
            }
        }

        // ---------------------------------------------------------------- find_methods

        /// <summary>
        /// 在已加载程序集的类型方法上按方法名子串搜索（大小写不敏感），返回 {type, method} 列表。
        /// 同样限流防卡死。
        /// </summary>
        public ToolResult FindMethods(string query, int limit)
        {
            if (string.IsNullOrWhiteSpace(query))
                return ToolResult.ErrorResult("query 不能为空（子串匹配方法名，大小写不敏感，如 Tick）");
            int lim = NormalizeLimit(limit);

            lock (commandLock)
            {
                VirtualMachine target;
                lock (stateLock)
                {
                    if (state != SessionState.Attached || vm == null)
                        return ToolResult.ErrorResult("未连接，无法搜索方法（请先 attach）");
                    target = vm;
                }
                try
                {
                    var results = new List<object>();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    bool truncated = false;
                    ScanAssemblyMetadata(target, query, lim, seen, results, out truncated, true, query);

                    var data = new Dictionary<string, object>
                    {
                        ["query"] = query,
                        ["limit"] = lim,
                        ["count"] = results.Count,
                        ["truncated"] = truncated,
                        ["methods"] = results,
                    };
                    return ToolResult.OkResult("找到 " + results.Count + " 个匹配方法"
                        + (truncated ? "（达到扫描/结果上限，结果不完整；可用更精确的 query 缩小范围）" : ""), data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("find_methods 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("find_methods 失败: " + FriendlyError(ex, "搜索方法"));
                }
            }
        }

        // ---------------------------------------------------------------- 搜索辅助（限流）

        static int NormalizeLimit(int limit)
        {
            if (limit <= 0)
                return 50;
            return Math.Min(limit, SearchMaxResults);
        }

        static void AddTypeName(List<object> results, HashSet<string> seen, string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return;
            if (seen.Add(fullName))
                results.Add(fullName);
        }

        static void AddMethodResult(List<object> results, HashSet<string> seen, string typeFullName, string methodName)
        {
            if (string.IsNullOrEmpty(typeFullName))
                return;
            string key = typeFullName + ":" + methodName;
            if (!seen.Add(key))
                return;
            results.Add(new Dictionary<string, object>
            {
                ["type"] = typeFullName,
                ["method"] = methodName,
            });
        }

        /// <summary>
        /// 遍历根域程序集元数据做子串匹配（searchMethods=false 匹配类型全名；true 匹配方法名）。
        /// 限流：最多检查 SearchMaxAssemblies 个程序集；每程序集一次 GetMetadata（一次 socket 元数据 blob 拉取，
        /// 之后本地遍历）；结果达到 limit 即停止；动态程序集/元数据拉取失败跳过。
        /// </summary>
        void ScanAssemblyMetadata(VirtualMachine target, string typeQuery, int limit, HashSet<string> seen,
            List<object> results, out bool truncated, bool searchMethods, string methodQuery)
        {
            truncated = false;
            // 强制刷新程序集缓存：标准库由 AssemblyLoad 事件驱动失效（EventHandler），本会话用
            // 自定义事件循环不触发，attach 后游戏持续加载的程序集会永远不可见（实测只看到 System 程序集）。
            // InvalidateAssemblyCaches 与 GetAssemblies 均为同程序集 internal，可直接调用。
            try { target.InvalidateAssemblyCaches(); } catch (VMDisconnectedException) { throw; } catch { }
            AssemblyMirror[] assemblies = target.RootDomain.GetAssemblies();
            if (assemblies == null || assemblies.Length == 0)
                return;

            int checkedAsm = 0;
            foreach (AssemblyMirror asm in assemblies)
            {
                if (results.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                if (checkedAsm >= SearchMaxAssemblies)
                {
                    truncated = true;
                    break;
                }
                checkedAsm++;

                AssemblyDefinition def;
                try { def = asm.GetMetadata(); }
                catch (VMDisconnectedException) { throw; }
                catch { continue; } // 动态程序集等无法获取元数据 → 跳过
                if (def == null || def.MainModule == null)
                    continue;

                try
                {
                    foreach (TypeDefinition t in def.MainModule.Types)
                        ScanCecilType(t, typeQuery, limit, seen, results, ref truncated, searchMethods, methodQuery, 0);
                }
                catch (VMDisconnectedException) { throw; }
                catch { /* 单个程序集元数据遍历失败不阻断整体 */ }
            }
        }

        void ScanCecilType(TypeDefinition t, string typeQuery, int limit, HashSet<string> seen,
            List<object> results, ref bool truncated, bool searchMethods, string methodQuery, int depth)
        {
            if (t == null || truncated)
                return;
            if (results.Count >= limit)
            {
                truncated = true;
                return;
            }

            string fullName = CecilTypeFullName(t);
            if (searchMethods)
            {
                if (t.HasMethods)
                {
                    foreach (MethodDefinition m in t.Methods)
                    {
                        if (results.Count >= limit)
                        {
                            truncated = true;
                            return;
                        }
                        if (m.Name.IndexOf(methodQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                            AddMethodResult(results, seen, fullName, m.Name);
                    }
                }
            }
            else if (fullName != null && fullName.IndexOf(typeQuery, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AddTypeName(results, seen, fullName);
            }

            if (depth < SearchMaxNestedDepth && t.HasNestedTypes)
            {
                foreach (TypeDefinition nt in t.NestedTypes)
                    ScanCecilType(nt, typeQuery, limit, seen, results, ref truncated, searchMethods, methodQuery, depth + 1);
            }
        }

        /// <summary>Cecil 类型全名（嵌套类型分隔符 '/' → '+'，与 mono TypeMirror.FullName 一致；编译器注入类型过滤）。</summary>
        static string CecilTypeFullName(TypeDefinition t)
        {
            string name = t.Name;
            if (name == "<Module>" || name == "<PrivateImplementationDetails>")
                return null;
            name = name.Replace('/', '+');
            string ns = t.Namespace;
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        static string SafeTypeName(TypeMirror t)
        {
            if (t == null)
                return null;
            try { return t.FullName; }
            catch (VMDisconnectedException) { throw; }
            catch { }
            try { return t.CSharpName; }
            catch (VMDisconnectedException) { throw; }
            catch { }
            try { return t.Name; }
            catch (VMDisconnectedException) { throw; }
            catch { return null; }
        }
    }
}

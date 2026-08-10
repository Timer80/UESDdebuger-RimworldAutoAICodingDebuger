using System;
using System.Collections.Generic;
using System.Text;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    /// <summary>
    /// 断点注册表条目：断点 id → { 方法镜像, 描述, 断点请求 }。
    /// 断点 id 即 SDB 代理分配的 EventRequest id（与事件中的 RequestId 对应）。
    /// </summary>
    public sealed class BreakpointEntry
    {
        public int Id { get; set; }

        /// <summary>人类可读描述（如 "System.Void Verse.Thing:DoWork() 方法入口"）。</summary>
        public string Description { get; set; }

        public MethodMirror Method { get; set; }

        public BreakpointEventRequest Request { get; set; }
    }

    /// <summary>Task 3：断点与异常事件请求工具（break_add / break_list / break_remove / break_clear / break_exception）。</summary>
    public sealed partial class DebugSession
    {
        // ---------------------------------------------------------------- break_add

        /// <summary>
        /// 在指定方法设置断点。
        /// method 格式：命名空间.类型名:方法名（可选附参数签名，如 "Verse.Thing:DoWork" 或 "Verse.Thing:DoWork(System.String)"）；
        /// 缺省 line 时为方法入口断点；指定 line 时在方法 Locations 中匹配源码行。
        /// </summary>
        public ToolResult BreakAdd(string methodSpec, int? line)
        {
            if (string.IsNullOrWhiteSpace(methodSpec))
                return ToolResult.ErrorResult("method 参数不能为空（格式: 命名空间.类型名:方法名，如 Verse.Thing:DoWork）");

            lock (commandLock)
            {
                VirtualMachine target;
                lock (stateLock)
                {
                    if (state != SessionState.Attached || vm == null)
                        return ToolResult.ErrorResult("未连接，无法设置断点（请先 attach）");
                    target = vm;
                }

                try
                {
                    MethodMirror method = FindMethod(target, methodSpec);
                    if (method == null)
                        return ToolResult.ErrorResult("未找到方法: " + methodSpec
                            + "。类型名请使用完整形式（与 Assembly.GetType 一致，如 Verse.Thing:DoWork）；"
                            + "若方法是重载，请附带参数签名，如 Verse.Thing:DoWork(System.String)");

                    long ilOffset;
                    string desc;
                    if (line.HasValue)
                    {
                        int? found = FindLineOffset(method, line.Value);
                        if (!found.HasValue)
                            return ToolResult.ErrorResult("方法 " + method.FullName + " 中未找到第 " + line.Value + " 行"
                                + "（可用行: " + AvailableLines(method) + "）。可省略 line 参数，退化为方法入口断点");
                        ilOffset = found.Value;
                        desc = method.FullName + " 行 " + line.Value;
                    }
                    else
                    {
                        // 方法入口断点：有调试符号取首个 Location；无符号（发布版游戏程序集无 PDB）退化为
                        // IL offset 0（BreakpointEventRequest 在 Locations 为空时跳过 offset 校验，
                        // 代理按指令级断点处理，方法入口必然命中）。与需求.md「行级断点无符号 → 退化为方法入口断点」一致。
                        ilOffset = method.Locations.Count > 0 ? method.Locations[0].ILOffset : 0;
                        desc = method.FullName + " 方法入口";
                    }

                    BreakpointEventRequest req = target.SetBreakpoint(method, ilOffset);
                    int id = req.GetId();
                    lock (breakpointsLock)
                    {
                        breakpoints[id] = new BreakpointEntry
                        {
                            Id = id,
                            Description = desc,
                            Method = method,
                            Request = req,
                        };
                    }

                    var data = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["method"] = method.FullName,
                        ["ilOffset"] = ilOffset,
                        ["line"] = line.HasValue ? (object)line.Value
                            : (method.Locations.Count > 0 ? (object)method.Locations[0].LineNumber : null),
                        ["mode"] = line.HasValue ? "line" : "entry",
                    };
                    return ToolResult.OkResult("断点 #" + id + " 已设置 @ " + desc, data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("break_add 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("break_add 失败: " + FriendlyError(ex, "设置断点"));
                }
            }
        }

        // ---------------------------------------------------------------- break_list

        /// <summary>列出当前会话全部断点（id + 描述）。</summary>
        public ToolResult BreakList()
        {
            bool attached;
            lock (stateLock) { attached = state == SessionState.Attached; }
            if (!attached)
                return ToolResult.ErrorResult("未连接，无可列出的断点（断点随会话清除）");

            lock (breakpointsLock)
            {
                var list = new List<object>();
                foreach (KeyValuePair<int, BreakpointEntry> kv in breakpoints)
                {
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = kv.Key,
                        ["description"] = kv.Value.Description,
                    });
                }
                var data = new Dictionary<string, object>
                {
                    ["count"] = list.Count,
                    ["breakpoints"] = list,
                };
                return ToolResult.OkResult(list.Count == 0 ? "当前没有断点" : "共 " + list.Count + " 个断点", data);
            }
        }

        // ---------------------------------------------------------------- break_remove

        /// <summary>按 id 移除指定断点（禁用对应 EventRequest 并从注册表删除）。</summary>
        public ToolResult BreakRemove(int id)
        {
            BreakpointEntry entry;
            lock (breakpointsLock)
            {
                if (!breakpoints.TryGetValue(id, out entry))
                    return ToolResult.ErrorResult("断点 #" + id + " 不存在");
            }

            lock (commandLock)
            {
                try
                {
                    entry.Request.Disable();
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("break_remove 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("break_remove 失败: " + FriendlyError(ex, "禁用断点"));
                }
                lock (breakpointsLock) { breakpoints.Remove(id); }
            }
            return ToolResult.OkResult("断点 #" + id + " 已移除（" + entry.Description + "）");
        }

        // ---------------------------------------------------------------- break_clear

        /// <summary>清空当前会话全部断点。</summary>
        public ToolResult BreakClear()
        {
            List<BreakpointEntry> all;
            lock (stateLock)
            {
                if (state != SessionState.Attached)
                    return ToolResult.ErrorResult("未连接，无法清空断点（请先 attach）");
            }
            lock (breakpointsLock) { all = new List<BreakpointEntry>(breakpoints.Values); }

            lock (commandLock)
            {
                foreach (BreakpointEntry e in all)
                {
                    try { e.Request.Disable(); }
                    catch (VMDisconnectedException)
                    {
                        HandleDisconnect("break_clear 时 VM 断开");
                        return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                    }
                    catch { /* 单个断点禁用失败不阻断整体清空 */ }
                }
                lock (breakpointsLock) { breakpoints.Clear(); }
            }
            return ToolResult.OkResult("已清空 " + all.Count + " 个断点");
        }

        // ---------------------------------------------------------------- break_exception

        /// <summary>
        /// 创建异常事件请求。type 为异常类型完整名（缺省=全部异常）；
        /// caught/uncaught 分别过滤捕获/未捕获异常（缺省均为 true）。
        /// </summary>
        public ToolResult BreakException(string type, bool? caught, bool? uncaught)
        {
            bool c = caught.HasValue ? caught.Value : true;
            bool u = uncaught.HasValue ? uncaught.Value : true;
            if (!c && !u)
                return ToolResult.ErrorResult("caught 与 uncaught 不能同时为 false（否则无异常事件可触发）");

            lock (commandLock)
            {
                VirtualMachine target;
                lock (stateLock)
                {
                    if (state != SessionState.Attached || vm == null)
                        return ToolResult.ErrorResult("未连接，无法设置异常断点（请先 attach）");
                    target = vm;
                }

                try
                {
                    TypeMirror excType = null;
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        IList<TypeMirror> matches = target.GetTypes(type.Trim(), false);
                        if (matches == null || matches.Count == 0)
                        {
                            IList<TypeMirror> ci = target.GetTypes(type.Trim(), true);
                            if (ci != null && ci.Count > 0)
                                matches = ci;
                        }
                        if (matches == null || matches.Count == 0)
                            return ToolResult.ErrorResult("未找到异常类型: " + type
                                + "。请使用完整类型名（如 System.NullReferenceException），或省略 type 监听全部异常");
                        excType = matches[0];
                    }

                    ExceptionEventRequest req = target.CreateExceptionRequest(excType, c, u);
                    req.Enable();
                    int id = req.GetId();
                    string desc = (excType != null ? excType.FullName : "全部异常")
                        + " (caught=" + c + ", uncaught=" + u + ")";
                    lock (breakpointsLock) { exceptionRequests[id] = desc; }

                    var data = new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["type"] = excType != null ? (object)excType.FullName : null,
                        ["caught"] = c,
                        ["uncaught"] = u,
                    };
                    return ToolResult.OkResult("异常事件请求 #" + id + " 已启用: " + desc, data);
                }
                catch (VMDisconnectedException)
                {
                    HandleDisconnect("break_exception 时 VM 断开");
                    return ToolResult.ErrorResult("连接已断开（VMDisconnectedException）");
                }
                catch (Exception ex)
                {
                    return ToolResult.ErrorResult("break_exception 失败: " + FriendlyError(ex, "创建异常事件请求"));
                }
            }
        }

        // ---------------------------------------------------------------- 查找辅助

        /// <summary>
        /// 解析 "类型:方法(签名)" 并定位 MethodMirror（在命令锁内调用；全程走 socket 需 VM 已连接）。
        /// 类型用 vm.GetTypes 精确匹配（完整类型名），方法名精确匹配，参数签名可选宽松匹配。
        /// </summary>
        static MethodMirror FindMethod(VirtualMachine target, string spec)
        {
            string typePart = null;
            string methodPart = spec.Trim();
            int colon = methodPart.IndexOf(':');
            if (colon >= 0)
            {
                typePart = methodPart.Substring(0, colon).Trim();
                methodPart = methodPart.Substring(colon + 1).Trim();
            }
            if (typePart == null || typePart.Length == 0 || methodPart.Length == 0)
                return null;

            string methodName = methodPart;
            string paramSig = null;
            int lp = methodPart.IndexOf('(');
            if (lp >= 0)
            {
                methodName = methodPart.Substring(0, lp).Trim();
                if (methodPart.EndsWith(")"))
                    paramSig = methodPart.Substring(lp + 1, methodPart.Length - lp - 2).Trim();
            }
            if (methodName.Length == 0)
                return null;

            // 1) 按完整类型名查（优先）
            List<TypeMirror> candidates = new List<TypeMirror>();
            foreach (bool ignoreCase in new[] { false, true })
            {
                try
                {
                    IList<TypeMirror> byName = target.GetTypes(typePart, ignoreCase);
                    if (byName != null)
                        candidates.AddRange(byName);
                }
                catch (VMDisconnectedException) { throw; }
                catch { }
                if (candidates.Count > 0)
                    break;
            }

            foreach (TypeMirror t in candidates)
            {
                if (!TypeNameMatches(t, typePart))
                    continue;
                MethodMirror[] methods;
                try { methods = t.GetMethods(); }
                catch (VMDisconnectedException) { throw; }
                catch { continue; }
                foreach (MethodMirror m in methods)
                {
                    if (!string.Equals(m.Name, methodName, StringComparison.Ordinal))
                        continue;
                    if (paramSig != null && !ParamsMatch(m.FullName, paramSig))
                        continue;
                    return m;
                }
            }
            return null;
        }

        /// <summary>类型全名精确匹配；允许用户省略命名空间前缀（末段匹配）。</summary>
        static bool TypeNameMatches(TypeMirror t, string typePart)
        {
            string full;
            try { full = t.FullName; }
            catch (VMDisconnectedException) { throw; }
            catch { return false; }
            if (string.Equals(full, typePart, StringComparison.Ordinal))
                return true;
            return full.EndsWith("." + typePart, StringComparison.Ordinal);
        }

        /// <summary>参数签名宽松匹配（忽略空白/大小写/命名空间前缀）。</summary>
        static bool ParamsMatch(string fullName, string paramSig)
        {
            string fullParams = ExtractParams(fullName);
            if (fullParams == null)
                return false;
            string a = StripSpaces(fullParams);
            string b = StripSpaces(paramSig);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(StripNamespace(a), StripNamespace(b), StringComparison.OrdinalIgnoreCase);
        }

        static string ExtractParams(string fullName)
        {
            int lp = fullName.IndexOf('(');
            int rp = fullName.LastIndexOf(')');
            if (lp < 0 || rp <= lp)
                return null;
            return fullName.Substring(lp + 1, rp - lp - 1);
        }

        static string StripSpaces(string s)
        {
            return s.Replace(" ", "").Replace("\t", "");
        }

        static string StripNamespace(string s)
        {
            if (s.Length == 0)
                return s;
            string[] parts = s.Split(',');
            var res = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                int dot = p.LastIndexOf('.');
                if (dot >= 0)
                    p = p.Substring(dot + 1);
                if (i > 0)
                    res.Append(',');
                res.Append(p);
            }
            return res.ToString();
        }

        /// <summary>在方法 Locations 中查找指定源码行的 ILOffset（找不到返回 null）。</summary>
        static int? FindLineOffset(MethodMirror method, int line)
        {
            foreach (Location loc in method.Locations)
            {
                if (loc.LineNumber == line)
                    return loc.ILOffset;
            }
            return null;
        }

        /// <summary>方法可用源码行（去重、最多显示前若干行），用于断点错误提示。</summary>
        static string AvailableLines(MethodMirror method)
        {
            var lines = new SortedSet<int>();
            foreach (Location loc in method.Locations)
            {
                if (loc.LineNumber > 0)
                    lines.Add(loc.LineNumber);
            }
            if (lines.Count == 0)
                return "（无行信息）";
            var sb = new StringBuilder();
            int shown = 0;
            foreach (int l in lines)
            {
                if (shown >= 12)
                {
                    sb.Append(", ...");
                    break;
                }
                if (shown > 0)
                    sb.Append(", ");
                sb.Append(l);
                shown++;
            }
            return sb.ToString();
        }
    }
}

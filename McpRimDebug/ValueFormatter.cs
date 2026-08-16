using System;
using System.Collections.Generic;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    /// <summary>
    /// 对象句柄缓存能力（由 DebugSession 实现）：把 SDB 的 ObjectMirror 登记/解析为稳定的
    /// 整数句柄，供 inspect 工具跨调用继续展开；断连时清空。
    /// </summary>
    public interface IObjectHandleStore
    {
        /// <summary>获取对象句柄（同一对象复用同一句柄；首次出现时登记）。null 返回 0。</summary>
        int GetOrCreateHandle(ObjectMirror obj);

        /// <summary>按句柄解析对象；句柄无效/已失效返回 null。</summary>
        ObjectMirror ResolveHandle(int handle);

        /// <summary>当前缓存的句柄数。</summary>
        int HandleCount { get; }
    }

    /// <summary>
    /// Task 4（计划阶段 3）：值格式化器。把 SDB 的 Value 格式化为 JSON 友好的结构化输出，
    /// 覆盖类型：Primitive / String / Array / Object / Struct / Enum / Pointer / null。
    ///
    /// 截断规则（受限版 Format，防输出爆炸）：深度 ≤3（深层对象/数组/结构体不再展开，只给类型与句柄引用）、
    /// 对象字段 ≤50（含基类链）、字符串 ≤512 字符（用 GetChars 只取前缀，避免整串下载）、
    /// 数组前 ≤32 元素；所有超限一律以 truncated / expanded 字段标注。
    ///
    /// 完整版 FormatFull(Value)：绕过上述全部上限（MaxDepth/MaxFields/MaxString/MaxArray）递归到最底，
    /// 供超限回复全量落盘（TruncationSink）使用。对象图取 loop 防环：完整模式用一个当前递归路径的
    /// ObjectMirror 句柄集合，遇到环回到已在路径上的对象时给出 cycle 引用标记而不继续递归（避免栈溢出）；
    /// 同时保留一个深栈安全阀（超深但无环的对象链也强制截断为 depth-limit 标记，防止 StackOverflowException
    /// ——该异常无法被 catch，进程会终止）。
    ///
    /// 对象（含数组）经 IObjectHandleStore 登记为递增整数句柄，返回给客户端供 inspect 继续展开。
    /// 本类不依赖 DebugSession 具体类型，Task 5 的表达式求值可直接复用。
    /// 所有 socket 读取均要求调用方已持有命令锁且 VM 挂起；VMDisconnectedException 向上抛给工具层统一处理。
    /// </summary>
    public sealed class ValueFormatter
    {
        public const int DefaultMaxDepth = 3;
        public const int DefaultMaxFields = 50;
        public const int DefaultMaxStringLength = 512;
        public const int DefaultMaxArrayElements = 32;

        /// <summary>FormatFull 的安全阀最大递归栈深（防止无环超深对象链导致 StackOverflowException）。</summary>
        public const int FullMaxDepthSafety = 256;

        readonly IObjectHandleStore handles;

        public int MaxDepth { get; }
        public int MaxFields { get; }
        public int MaxStringLength { get; }
        public int MaxArrayElements { get; }

        public ValueFormatter(IObjectHandleStore handles,
            int maxDepth = DefaultMaxDepth,
            int maxFields = DefaultMaxFields,
            int maxStringLength = DefaultMaxStringLength,
            int maxArrayElements = DefaultMaxArrayElements)
        {
            this.handles = handles ?? throw new ArgumentNullException("handles");
            MaxDepth = maxDepth;
            MaxFields = maxFields;
            MaxStringLength = maxStringLength;
            MaxArrayElements = maxArrayElements;
        }

        /// <summary>顶层受限格式化入口（深度 0）。返回 JSON 友好结构（字典/列表/字符串/数字/bool/null）。</summary>
        public object Format(Value value)
        {
            return Format(value, 0);
        }

        /// <summary>受限格式化（depth 为当前嵌套深度；depth &gt;= MaxDepth 时不再展开容器）。</summary>
        public object Format(Value value, int depth)
        {
            var st = new FormatState
            {
                Limits = new FormatLimits
                {
                    MaxDepth = MaxDepth,
                    MaxFields = MaxFields,
                    MaxStringLength = MaxStringLength,
                    MaxArrayElements = MaxArrayElements,
                },
                Path = null, // 受限版有深度上限，无需循环检测
            };
            return FormatCore(value, depth, st);
        }

        /// <summary>
        /// 完整格式化（深度 0）：不受 MaxDepth/MaxFields/MaxStringLength/MaxArrayElements 限制，递归到最底。
        /// 用于超限回复的全量内容落盘（TruncationSink）。环引用经路径句柄集截断，超深无环链经 FullMaxDepthSafety 截断。
        /// 既有受限版 Format 与调用方保持不变。
        /// </summary>
        public object FormatFull(Value value)
        {
            var st = new FormatState
            {
                Limits = new FormatLimits
                {
                    MaxDepth = int.MaxValue,
                    MaxFields = int.MaxValue,
                    MaxStringLength = int.MaxValue,
                    MaxArrayElements = int.MaxValue,
                },
                Path = new HashSet<long>(),
            };
            return FormatCore(value, 0, st);
        }

        // ---------------------------------------------------------------- 共享递归核心

        /// <summary>按 FormatState 递归格式化（受限版与完整版共用；完整版 Path 非 null 且 Limits 无上限）。</summary>
        object FormatCore(Value value, int depth, FormatState st)
        {
            if (value == null)
                return null;
            try
            {
                if (value is PrimitiveValue)
                    return FormatPrimitive((PrimitiveValue)value);
                if (value is StringMirror)
                    return FormatString((StringMirror)value, st);
                if (value is ArrayMirror)
                    return FormatArray((ArrayMirror)value, depth, st);
                if (value is EnumMirror)
                    return FormatEnum((EnumMirror)value);
                if (value is StructMirror)
                    return FormatStruct((StructMirror)value, depth, st);
                if (value is ObjectMirror)
                    return FormatObject((ObjectMirror)value, depth, st);
                if (value is PointerValue)
                    return FormatPointer((PointerValue)value, depth, st);
                return Error("未知值类型: " + value.GetType().Name);
            }
            catch (VMDisconnectedException)
            {
                throw; // 交给工具层统一转可读错误
            }
            catch (ObjectCollectedException)
            {
                return new Dictionary<string, object>
                {
                    ["kind"] = "collected",
                    ["note"] = "对象已被 GC 回收",
                };
            }
            catch (Exception ex)
            {
                return Error("值读取失败: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 各类型格式化

        static object FormatPrimitive(PrimitiveValue p)
        {
            object v = p.Value;
            if (v == null)
                return null; // SDB 空引用（TYPE_NULL）
            return new Dictionary<string, object>
            {
                ["kind"] = "primitive",
                ["type"] = v.GetType().FullName,
                ["value"] = JsonSafe(v),
            };
        }

        object FormatString(StringMirror s, FormatState st)
        {
            int len = -1;
            try { len = s.Length; }
            catch (VMDisconnectedException) { throw; }
            catch { }

            string text;
            bool truncated = false;
            try
            {
                if (len >= 0)
                {
                    // 受限版只取前缀，避免把超长字符串整串下载到客户端；完整版 take=int.MaxValue → 全取。
                    // 这里以镜像长度 len 判定截断（GetChars 已按 take=min(len,MaxStringLength) 限量，
                    // 取回的 prefix 长度 ≤ MaxStringLength，无法从 prefix 本身区分「恰好等于上限」与「超限」，
                    // 因此 truncated 依据源串真实长度 len，而非 Truncate 对已限量文本的判断）。
                    int take = Math.Min(len, st.Limits.MaxStringLength);
                    string prefix = new string(s.GetChars(0, take));
                    text = Truncate(prefix, st.Limits.MaxStringLength).text;
                    truncated = len > st.Limits.MaxStringLength;
                }
                else
                {
                    // len 未知（无 Length 信息）→ 取回完整文本，经统一 Truncate 辅助截断并判定 truncated。
                    (text, truncated) = Truncate(s.Value ?? "", st.Limits.MaxStringLength);
                }
            }
            catch (VMDisconnectedException) { throw; }
            catch (Exception ex)
            {
                return Error("字符串读取失败: " + ex.Message);
            }

            return new Dictionary<string, object>
            {
                ["kind"] = "string",
                ["type"] = "System.String",
                ["value"] = text,
                ["length"] = len,
                ["truncated"] = truncated,
            };
        }

        object FormatArray(ArrayMirror a, int depth, FormatState st)
        {
            string typeName = SafeTypeName(a.Type) ?? "System.Array";
            int length = -1;
            int rank = -1;
            var dims = new List<int>();
            try
            {
                length = a.Length;
                rank = a.Rank;
                for (int i = 0; i < rank; i++)
                    dims.Add(a.GetLength(i));
            }
            catch (VMDisconnectedException) { throw; }
            catch { }

            var d = new Dictionary<string, object>
            {
                ["kind"] = "array",
                ["type"] = typeName,
                ["length"] = length,
                ["rank"] = rank,
            };
            if (dims.Count > 0)
                d["dimensions"] = dims;
            // 数组也是 ObjectMirror，登记句柄保持统一引用
            d["handle"] = handles.GetOrCreateHandle(a);

            // 完整模式循环检测：数组同样可能是环图的一部分（内容含指向自身/父对象的引用）
            bool enteredPath = false;
            if (st.Path != null)
            {
                if (!st.Path.Add(a.Id))
                {
                    d["expanded"] = false;
                    d["reason"] = "cycle";
                    return d;
                }
                enteredPath = true;
            }
            try
            {
                if (depth >= st.Limits.MaxDepth)
                {
                    d["expanded"] = false;
                    d["reason"] = "max-depth";
                    return d;
                }
                if (st.Path != null && depth >= FullMaxDepthSafety)
                {
                    d["expanded"] = false;
                    d["reason"] = "depth-safety";
                    return d;
                }

                var elements = new List<object>();
                bool truncated = false;
                if (length > 0)
                {
                    int take = Math.Min(length, st.Limits.MaxArrayElements);
                    try
                    {
                        IList<Value> values = a.GetValues(0, take);
                        for (int i = 0; i < values.Count; i++)
                            elements.Add(FormatCore(values[i], depth + 1, st));
                        truncated = length > st.Limits.MaxArrayElements;
                    }
                    catch (VMDisconnectedException) { throw; }
                    catch (Exception ex)
                    {
                        elements.Add(Error("数组元素读取失败: " + ex.Message));
                    }
                }
                d["elements"] = elements;
                d["truncated"] = truncated;
                d["expanded"] = true;
                return d;
            }
            finally
            {
                if (enteredPath)
                    st.Path.Remove(a.Id);
            }
        }

        object FormatEnum(EnumMirror en)
        {
            string typeName = SafeTypeName(en.Type) ?? "System.Enum";
            object numeric = null;
            string name = null;
            try { numeric = en.Value; }
            catch (VMDisconnectedException) { throw; }
            catch { }
            try { name = en.StringValue; }
            catch (VMDisconnectedException) { throw; }
            catch { }

            return new Dictionary<string, object>
            {
                ["kind"] = "enum",
                ["type"] = typeName,
                ["value"] = name ?? (numeric != null ? numeric.ToString() : "?"),
                ["numericValue"] = JsonSafe(numeric),
            };
        }

        object FormatStruct(StructMirror st, int depth, FormatState state)
        {
            string typeName = SafeTypeName(st.Type) ?? "System.ValueType";
            var d = new Dictionary<string, object>
            {
                ["kind"] = "struct",
                ["type"] = typeName,
            };
            if (depth >= state.Limits.MaxDepth)
            {
                d["expanded"] = false;
                d["reason"] = "max-depth";
                return d;
            }
            if (state.Path != null && depth >= FullMaxDepthSafety)
            {
                d["expanded"] = false;
                d["reason"] = "depth-safety";
                return d;
            }

            // StructMirror.Fields 与 Type.GetFields() 的非静态字段按序对应
            var fieldInfos = new List<FieldInfoMirror>();
            try
            {
                foreach (FieldInfoMirror f in st.Type.GetFields())
                {
                    if (!f.IsStatic && !f.IsLiteral)
                        fieldInfos.Add(f);
                }
            }
            catch (VMDisconnectedException) { throw; }
            catch (Exception ex)
            {
                return Error("结构体字段读取失败: " + ex.Message);
            }

            bool truncated = fieldInfos.Count > state.Limits.MaxFields;
            int n = Math.Min(fieldInfos.Count, state.Limits.MaxFields);
            Value[] vals = st.Fields;
            var fields = new List<object>();
            for (int i = 0; i < n && vals != null && i < vals.Length; i++)
            {
                fields.Add(new Dictionary<string, object>
                {
                    ["name"] = fieldInfos[i].Name,
                    ["type"] = SafeTypeName(fieldInfos[i].FieldType),
                    ["value"] = FormatCore(vals[i], depth + 1, state),
                });
            }
            d["fields"] = fields;
            d["truncated"] = truncated;
            d["expanded"] = true;
            return d;
        }

        object FormatObject(ObjectMirror o, int depth, FormatState st)
        {
            string typeName = SafeTypeName(o.Type) ?? o.GetType().Name;
            int handle = handles.GetOrCreateHandle(o);
            var d = new Dictionary<string, object>
            {
                ["kind"] = "object",
                ["type"] = typeName,
                ["handle"] = handle,
            };
            try { d["address"] = "0x" + o.Address.ToString("x"); }
            catch (VMDisconnectedException) { throw; }
            catch { }

            // 完整模式循环检测：对象图可能成环（this/parent 反向引用），须沿当前递归路径判重
            bool enteredPath = false;
            if (st.Path != null)
            {
                if (!st.Path.Add(o.Id))
                {
                    d["expanded"] = false;
                    d["reason"] = "cycle";
                    return d;
                }
                enteredPath = true;
            }
            try
            {
                if (depth >= st.Limits.MaxDepth)
                {
                    d["expanded"] = false;
                    d["reason"] = "max-depth";
                    return d;
                }
                if (st.Path != null && depth >= FullMaxDepthSafety)
                {
                    d["expanded"] = false;
                    d["reason"] = "depth-safety";
                    return d;
                }

                // 收集实例字段（含基类链去重，最多 MaxFields 个；完整版 MaxFields=int.MaxValue 取全部）。
                // P2-MD-3：跨基类合并时按字段名去重，从 O(n²) 线性扫描改为 HashSet 的 O(1) 命中。
                var all = new List<FieldInfoMirror>();
                var seenNames = new HashSet<string>(StringComparer.Ordinal);
                TypeMirror cur = o.Type;
                int levels = 0;
                while (cur != null && levels < 16)
                {
                    FieldInfoMirror[] fs = null;
                    try { fs = cur.GetFields(); }
                    catch (VMDisconnectedException) { throw; }
                    catch { fs = null; }
                    if (fs != null)
                    {
                        foreach (FieldInfoMirror f in fs)
                        {
                            if (f.IsStatic || f.IsLiteral)
                                continue;
                            if (!IsFirstSeen(seenNames, f.Name))
                                continue; // O(1) 去重：同名（基类中已出现）字段跳过
                            all.Add(f);
                        }
                    }
                    try { cur = cur.BaseType; }
                    catch (VMDisconnectedException) { throw; }
                    catch { break; }
                    levels++;
                }

                bool truncated = all.Count > st.Limits.MaxFields;
                int n = Math.Min(all.Count, st.Limits.MaxFields);
                var fields = new List<object>();
                if (n > 0)
                {
                    var infos = new FieldInfoMirror[n];
                    for (int i = 0; i < n; i++)
                        infos[i] = all[i];
                    Value[] values = ReadFieldValues(o, infos);
                    for (int i = 0; i < n; i++)
                    {
                        fields.Add(new Dictionary<string, object>
                        {
                            ["name"] = infos[i].Name,
                            ["type"] = SafeTypeName(infos[i].FieldType),
                            ["value"] = FormatCore(values != null && i < values.Length ? values[i] : null, depth + 1, st),
                        });
                    }
                }
                d["fields"] = fields;
                d["truncated"] = truncated;
                d["expanded"] = true;
                return d;
            }
            finally
            {
                if (enteredPath)
                    st.Path.Remove(o.Id);
            }
        }

        object FormatPointer(PointerValue ptr, int depth, FormatState st)
        {
            var d = new Dictionary<string, object>
            {
                ["kind"] = "pointer",
                ["type"] = SafeTypeName(ptr.Type) ?? "pointer",
                ["address"] = "0x" + ptr.Address.ToString("x"),
            };
            try
            {
                d["value"] = FormatCore(ptr.Value, depth + 1, st); // 解引用；空指针为 null
            }
            catch (VMDisconnectedException) { throw; }
            catch (Exception ex)
            {
                d["value"] = Error("解引用失败: " + ex.Message);
            }
            return d;
        }

        // ---------------------------------------------------------------- 辅助

        /// <summary>批量读取字段值；批量失败时逐字段回退（单字段失败记 null 并继续）。</summary>
        static Value[] ReadFieldValues(ObjectMirror o, FieldInfoMirror[] infos)
        {
            try
            {
                return o.GetValues(infos);
            }
            catch (VMDisconnectedException) { throw; }
            catch
            {
                var res = new Value[infos.Length];
                for (int i = 0; i < infos.Length; i++)
                {
                    try { res[i] = o.GetValue(infos[i]); }
                    catch (VMDisconnectedException) { throw; }
                    catch { res[i] = null; }
                }
                return res;
            }
        }

        /// <summary>类型显示名：FullName 优先，失败退化为 CSharpName / Name。</summary>
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

        /// <summary>System.Text.Json 对 NaN/Infinity 会抛异常，转成字符串表示。</summary>
        static object JsonSafe(object v)
        {
            if (v is float f)
                return float.IsNaN(f) || float.IsInfinity(f) ? (object)f.ToString("R") : v;
            if (v is double d)
                return double.IsNaN(d) || double.IsInfinity(d) ? (object)d.ToString("R") : v;
            return v;
        }

        /// <summary>
        /// P2-MD-3 去重核心（纯逻辑）：返回是否首次出现该字段名；返回 true 表示应保留该字段。
        /// 用 Ordinal 比较的 HashSet 做 O(1) 命中，替代跨基类合并字段时的 O(n²) 线性扫描。
        /// FormatObject 与离线 selftest 共用此实现。
        /// </summary>
        internal static bool IsFirstSeen(HashSet<string> seen, string name)
        {
            return name != null && seen.Add(name);
        }

        /// <summary>
        /// 字符串截断统一辅助（P3-MD-5）：把 text 截到 maxLength 个字符，返回被截后的文本与是否发生截断。
        /// 供 FormatString 的两条截断分支共用；对已 ≤ maxLength 的文本恒等返回。
        /// 注意：当调用方只持有已限量（≤ maxLength）的前缀时，无法据此判断源串是否超限 ——
        /// 此场景的 truncated 须由源串真实长度另外判定（见 FormatString 的 len ≥ 0 分支）。
        /// </summary>
        internal static (string text, bool truncated) Truncate(string text, int maxLength)
        {
            if (text == null || text.Length <= maxLength)
                return (text ?? "", false);
            return (text.Substring(0, maxLength), true);
        }

        static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = "error",
                ["message"] = message,
            };
        }

        // ---------------------------------------------------------------- 内部状态

        /// <summary>受限版/完整版的裁剪上限集合（完整版全为 int.MaxValue）。</summary>
        struct FormatLimits
        {
            public int MaxDepth;
            public int MaxFields;
            public int MaxStringLength;
            public int MaxArrayElements;
        }

        /// <summary>格式化递归状态：上限 + 完整模式的路径句柄集合（循环检测）。</summary>
        sealed class FormatState
        {
            public FormatLimits Limits;
            public HashSet<long> Path;
        }
    }
}

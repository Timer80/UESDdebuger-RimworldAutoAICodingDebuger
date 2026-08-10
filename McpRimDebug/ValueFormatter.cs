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
    /// 截断规则（防输出爆炸）：深度 ≤3（深层对象/数组/结构体不再展开，只给类型与句柄引用）、
    /// 对象字段 ≤50（含基类链）、字符串 ≤512 字符（用 GetChars 只取前缀，避免整串下载）、
    /// 数组前 ≤32 元素；所有超限一律以 truncated / expanded 字段标注。
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

        /// <summary>顶层格式化入口（深度 0）。返回 JSON 友好结构（字典/列表/字符串/数字/bool/null）。</summary>
        public object Format(Value value)
        {
            return Format(value, 0);
        }

        /// <summary>按深度格式化（depth 为当前嵌套深度；depth &gt;= MaxDepth 时不再展开容器）。</summary>
        public object Format(Value value, int depth)
        {
            if (value == null)
                return null;
            try
            {
                if (value is PrimitiveValue)
                    return FormatPrimitive((PrimitiveValue)value);
                if (value is StringMirror)
                    return FormatString((StringMirror)value);
                if (value is ArrayMirror)
                    return FormatArray((ArrayMirror)value, depth);
                if (value is EnumMirror)
                    return FormatEnum((EnumMirror)value);
                if (value is StructMirror)
                    return FormatStruct((StructMirror)value, depth);
                if (value is ObjectMirror)
                    return FormatObject((ObjectMirror)value, depth);
                if (value is PointerValue)
                    return FormatPointer((PointerValue)value, depth);
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

        object FormatString(StringMirror s)
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
                    // 只取前缀，避免把超长字符串整串下载到客户端
                    int take = Math.Min(len, MaxStringLength);
                    text = new string(s.GetChars(0, take));
                    truncated = len > MaxStringLength;
                }
                else
                {
                    text = s.Value ?? "";
                    if (text.Length > MaxStringLength)
                    {
                        text = text.Substring(0, MaxStringLength);
                        truncated = true;
                    }
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

        object FormatArray(ArrayMirror a, int depth)
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

            if (depth >= MaxDepth)
            {
                d["expanded"] = false;
                d["reason"] = "max-depth";
                return d;
            }

            var elements = new List<object>();
            bool truncated = false;
            if (length > 0)
            {
                int take = Math.Min(length, MaxArrayElements);
                try
                {
                    IList<Value> values = a.GetValues(0, take);
                    for (int i = 0; i < values.Count; i++)
                        elements.Add(Format(values[i], depth + 1));
                    truncated = length > MaxArrayElements;
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

        object FormatStruct(StructMirror st, int depth)
        {
            string typeName = SafeTypeName(st.Type) ?? "System.ValueType";
            var d = new Dictionary<string, object>
            {
                ["kind"] = "struct",
                ["type"] = typeName,
            };
            if (depth >= MaxDepth)
            {
                d["expanded"] = false;
                d["reason"] = "max-depth";
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

            bool truncated = fieldInfos.Count > MaxFields;
            int n = Math.Min(fieldInfos.Count, MaxFields);
            Value[] vals = st.Fields;
            var fields = new List<object>();
            for (int i = 0; i < n && vals != null && i < vals.Length; i++)
            {
                fields.Add(new Dictionary<string, object>
                {
                    ["name"] = fieldInfos[i].Name,
                    ["type"] = SafeTypeName(fieldInfos[i].FieldType),
                    ["value"] = Format(vals[i], depth + 1),
                });
            }
            d["fields"] = fields;
            d["truncated"] = truncated;
            d["expanded"] = true;
            return d;
        }

        object FormatObject(ObjectMirror o, int depth)
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

            if (depth >= MaxDepth)
            {
                d["expanded"] = false;
                d["reason"] = "max-depth";
                return d;
            }

            // 收集实例字段（含基类链去重，最多 MaxFields 个）
            var all = new List<FieldInfoMirror>();
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
                        bool dup = false;
                        for (int i = 0; i < all.Count; i++)
                        {
                            if (all[i].Name == f.Name) { dup = true; break; }
                        }
                        if (!dup)
                            all.Add(f);
                    }
                }
                try { cur = cur.BaseType; }
                catch (VMDisconnectedException) { throw; }
                catch { break; }
                levels++;
            }

            bool truncated = all.Count > MaxFields;
            int n = Math.Min(all.Count, MaxFields);
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
                        ["value"] = Format(values != null && i < values.Length ? values[i] : null, depth + 1),
                    });
                }
            }
            d["fields"] = fields;
            d["truncated"] = truncated;
            d["expanded"] = true;
            return d;
        }

        object FormatPointer(PointerValue ptr, int depth)
        {
            var d = new Dictionary<string, object>
            {
                ["kind"] = "pointer",
                ["type"] = SafeTypeName(ptr.Type) ?? "pointer",
                ["address"] = "0x" + ptr.Address.ToString("x"),
            };
            try
            {
                d["value"] = Format(ptr.Value, depth + 1); // 解引用；空指针为 null
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

        static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = "error",
                ["message"] = message,
            };
        }
    }
}

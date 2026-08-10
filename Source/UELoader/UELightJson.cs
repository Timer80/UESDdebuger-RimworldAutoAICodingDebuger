using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UELoader
{
    /// <summary>
    /// 极简 JSON 工具（游戏 Managed 目录无 Newtonsoft.Json，RimWorld 自研 Scribe 不适用 HTTP 场景）。
    /// 仅满足 UE HTTP 服务端的需求：
    ///   - Serialize：把 Dictionary&lt;string,object&gt; / List&lt;object&gt; / 简单类型 / 嵌套对象 序列化为 JSON 字符串；
    ///   - ParseObject：解析 JSON 对象（值支持 string / number / bool / null / 嵌套对象 / 数组）。
    /// 不做完整 JSON 规范支持（无转义 \uXXXX 反解等），够端点用即可。
    /// </summary>
    public static class UELightJson
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>序列化任意受支持的值（null/bool/数值/string/IEnumerable/Dictionary/IDictionary）。</summary>
        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null || value is DBNull)
            {
                sb.Append("null");
                return;
            }

            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (value is string s)
            {
                sb.Append('"').Append(Escape(s)).Append('"');
                return;
            }

            if (value is char ch)
            {
                sb.Append('"').Append(Escape(ch.ToString())).Append('"');
                return;
            }

            if (value is float || value is double || value is decimal)
            {
                sb.Append(((double)Convert.ToDouble(value, CultureInfo.InvariantCulture))
                    .ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is int || value is long || value is short || value is byte ||
                value is uint || value is ulong || value is ushort || value is sbyte)
            {
                sb.Append(Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is IDictionary dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Escape(Convert.ToString(e.Key, CultureInfo.InvariantCulture))).Append("\":");
                    WriteValue(sb, e.Value);
                }
                sb.Append('}');
                return;
            }

            if (value is IEnumerable enumerable)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            sb.Append('"').Append(Escape(value.ToString())).Append('"');
        }

        /// <summary>解析 JSON 对象（顶层必须是对象）。失败返回 null。</summary>
        public static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var p = new Parser(json);
            try
            {
                return p.ParseObject();
            }
            catch
            {
                return null;
            }
        }

        sealed class Parser
        {
            readonly string s;
            int i;

            public Parser(string str) { s = str; i = 0; }

            void SkipWs()
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            }

            public Dictionary<string, object> ParseObject()
            {
                SkipWs();
                if (i >= s.Length || s[i] != '{') throw new FormatException("not an object");
                i++;
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                SkipWs();
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                for (;;)
                {
                    SkipWs();
                    string key = ParseString();
                    SkipWs();
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                    i++;
                    SkipWs();
                    object value = ParseValue();
                    obj[key] = value;
                    SkipWs();
                    if (i >= s.Length) throw new FormatException("unterminated object");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return obj; }
                    throw new FormatException("expected ',' or '}'");
                }
            }

            object ParseValue()
            {
                SkipWs();
                if (i >= s.Length) throw new FormatException("eof");
                char c = s[i];
                if (c == '{') return ParseObject();
                if (c == '[')
                {
                    i++;
                    var list = new List<object>();
                    SkipWs();
                    if (i < s.Length && s[i] == ']') { i++; return list; }
                    for (;;)
                    {
                        list.Add(ParseValue());
                        SkipWs();
                        if (i >= s.Length) throw new FormatException("unterminated array");
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == ']') { i++; return list; }
                        throw new FormatException("expected ',' or ']'");
                    }
                }
                if (c == '"') return ParseString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return ParseNumber();
            }

            void Expect(string word)
            {
                if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                    throw new FormatException("bad token");
                i += word.Length;
            }

            string ParseString()
            {
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected string");
                i++;
                var sb = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (i >= s.Length) throw new FormatException("bad escape");
                        char e = s[i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                if (i + 4 > s.Length) throw new FormatException("bad \\u");
                                string hex = s.Substring(i, 4);
                                i += 4;
                                sb.Append((char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                break;
                            default: throw new FormatException("bad escape");
                        }
                    }
                    else sb.Append(c);
                }
                throw new FormatException("unterminated string");
            }

            object ParseNumber()
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' ||
                                        s[i] == 'e' || s[i] == 'E'))
                    i++;
                string num = s.Substring(start, i - start);
                if (num.IndexOf('.') >= 0 || num.IndexOf('e') >= 0 || num.IndexOf('E') >= 0)
                    return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                    return l;
                return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}

using System.Text.RegularExpressions;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>从 Roslyn 诊断文本里提取"缺哪个程序集 / 缺哪个名字"。实测 CS0012/CS1069 会带程序集全名（含版本）。</summary>
    public static class MissingReferenceParser
    {
        static readonly Regex Cs0012 = new Regex(@"You must add a reference to assembly [`']([^`,'\s]+)",
            RegexOptions.Compiled);
        // 任务 13：边界探针实测 CS1069 形态（"This type has been forwarded to assembly '…'"）——
        // 清单外 Unity 模块（VideoPlayer 等）走的就是这条，不是 CS0012。
        static readonly Regex Cs1069 = new Regex(@"forwarded to assembly [`']([^`,'\s]+)", RegexOptions.Compiled);
        // 完整身份（§4.10）：取**引号内全串**（含 Version/Culture/PublicKeyToken），与上面"取到逗号前"互补。
        static readonly Regex IdentityQuoted = new Regex(@"assembly [`']([^`']+)[`']", RegexOptions.Compiled);
        static readonly Regex Named = new Regex(@"The (?:type or namespace name|name) [`']([^`']+)[`']",
            RegexOptions.Compiled);
        // CS0234：'Xml' does not exist in the namespace 'System' —— TDD 首跑实测的主力缺命名空间码。
        static readonly Regex Cs0234Ns = new Regex(@"does not exist in the namespace [`']([^`']+)[`']",
            RegexOptions.Compiled);

        public static string AssemblyFromCs0012(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics) || diagnostics.IndexOf("CS0012", System.StringComparison.Ordinal) < 0)
                return null;
            Match m = Cs0012.Match(diagnostics);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string AssemblyFromCs1069(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics) || diagnostics.IndexOf("CS1069", System.StringComparison.Ordinal) < 0)
                return null;
            Match m = Cs1069.Match(diagnostics);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// §4.10 完整身份解析（net35 安全：out 参数、无元组）：CS0012/CS1069 的
        /// <c>assembly 'Name, Version=…, Culture=…, PublicKeyToken=…'</c> → 四元组。
        /// 无程序集身份的诊断返回 false（version/culture/pkt 置 null）。
        /// </summary>
        public static bool TryAssemblyIdentity(string diagnostics, out string simpleName,
            out string version, out string culture, out string publicKeyToken)
        {
            simpleName = null; version = null; culture = null; publicKeyToken = null;
            if (string.IsNullOrEmpty(diagnostics)) return false;
            bool hasCode = diagnostics.IndexOf("CS0012", System.StringComparison.Ordinal) >= 0
                || diagnostics.IndexOf("CS1069", System.StringComparison.Ordinal) >= 0;
            if (!hasCode) return false;
            Match q = IdentityQuoted.Match(diagnostics);
            if (!q.Success) return false;
            string quoted = q.Groups[1].Value;
            int comma = quoted.IndexOf(',');
            simpleName = comma < 0 ? quoted.Trim() : quoted.Substring(0, comma).Trim();
            if (simpleName.Length == 0) return false;
            if (comma < 0) return true;
            string[] parts = quoted.Substring(comma + 1).Split(',');
            foreach (string raw in parts)
            {
                string part = raw.Trim();
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq);
                string val = part.Substring(eq + 1);
                if (key.Equals("Version", System.StringComparison.OrdinalIgnoreCase)) version = val;
                else if (key.Equals("Culture", System.StringComparison.OrdinalIgnoreCase)) culture = val;
                else if (key.Equals("PublicKeyToken", System.StringComparison.OrdinalIgnoreCase)) publicKeyToken = val;
            }
            return true;
        }

        public static string SymbolFromCs0103(string diagnostics)
            => NamedFrom(diagnostics, "CS0103");

        public static string SymbolFromCs0246(string diagnostics)
            => NamedFrom(diagnostics, "CS0246");

        /// <summary>CS0234（TDD 首跑实测码）：组合成完整命名空间（如 'System.Collections' + 'Immutable' →
        /// System.Collections.Immutable）。<b>不做</b>简报的首段截断——截断会把 System.Data 误配成 System。</summary>
        public static string SymbolFromCs0234(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics) || diagnostics.IndexOf("CS0234", System.StringComparison.Ordinal) < 0)
                return null;
            int at = diagnostics.IndexOf("CS0234", System.StringComparison.Ordinal);
            Match m = Named.Match(diagnostics, at);
            if (!m.Success) return null;
            string name = m.Groups[1].Value;
            Match ns = Cs0234Ns.Match(diagnostics, m.Index);
            if (ns.Success && ns.Groups[1].Value.Length > 0)
                return ns.Groups[1].Value + "." + name;
            return name;
        }

        static string NamedFrom(string diagnostics, string code)
        {
            if (string.IsNullOrEmpty(diagnostics) || diagnostics.IndexOf(code, System.StringComparison.Ordinal) < 0)
                return null;
            int at = diagnostics.IndexOf(code, System.StringComparison.Ordinal);
            Match m = Named.Match(diagnostics, at);
            return m.Success ? m.Groups[1].Value : null;   // 定稿：dotted 名取全名（简报首段截断会误配）
        }
    }
}

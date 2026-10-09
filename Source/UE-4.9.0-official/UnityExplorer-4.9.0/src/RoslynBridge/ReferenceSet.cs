using System;
using System.Collections.Generic;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>引用路径集合：按 simple name 去重（先到先得）、可黑名单、跳过动态程序集。纯逻辑，可离线单测。</summary>
    public sealed class ReferenceSet
    {
        readonly List<string> s_paths = new List<string>();
        readonly HashSet<string> s_names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IList<string> Paths => s_paths;

        public bool TryAdd(string location, string simpleName, string blacklist)
        {
            if (string.IsNullOrEmpty(location)) return false;
            if (string.IsNullOrEmpty(simpleName)) return false;
            if (simpleName.StartsWith("eval-", StringComparison.Ordinal)) return false;
            if (simpleName == "completions") return false;
            if (IsBlacklisted(simpleName, blacklist)) return false;
            if (!s_names.Add(simpleName)) return false;
            s_paths.Add(location);
            return true;
        }

        public static bool IsBlacklisted(string simpleName, string blacklist)
        {
            if (string.IsNullOrEmpty(blacklist)) return false;
            foreach (string raw in blacklist.Split(';'))
            {
                string bl = raw.Trim();
                if (bl.Length == 0) continue;
                if (bl.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    bl = bl.Substring(0, bl.Length - 4);
                if (string.Equals(bl, simpleName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}

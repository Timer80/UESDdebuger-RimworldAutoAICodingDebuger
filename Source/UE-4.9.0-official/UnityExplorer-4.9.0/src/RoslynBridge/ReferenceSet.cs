using System;
using System.Collections.Generic;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>引用路径集合：按 simple name 去重（先到先得）、可黑名单、跳过动态程序集。纯逻辑，可离线单测。
    /// **§4.10-2**：同简单名不同身份的候选**不得静默丢弃**——单一编译内不可能同时引用同简单名两个版本
    ///（Roslyn 会报 CS1704），故仍保留先到者，但必须经 <see cref="ReportSink"/> 结构化登记
    ///（名字/保留路径+身份/丢弃路径+身份/原因）。这是"新版静默不生效"（dll 热重载场景）的病根防线。</summary>
    public sealed class ReferenceSet
    {
        readonly List<string> s_paths = new List<string>();
        readonly HashSet<string> s_names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> s_pathByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>结构化日志出口（宿主注入；null = 静默，但去重**语义**不变）。</summary>
        public Action<string> ReportSink { get; set; }

        public IList<string> Paths => s_paths;

        /// <summary>该 simple name 是否已进引用集（B1 护栏按可用性过滤默认 using 时用；纯查询，无副作用）。</summary>
        public bool Contains(string simpleName)
            => !string.IsNullOrEmpty(simpleName) && s_names.Contains(simpleName);

        public bool TryAdd(string location, string simpleName, string blacklist)
        {
            if (string.IsNullOrEmpty(location)) return false;
            if (string.IsNullOrEmpty(simpleName)) return false;
            if (simpleName.StartsWith("eval-", StringComparison.Ordinal)) return false;
            if (simpleName == "completions") return false;
            if (IsBlacklisted(simpleName, blacklist)) return false;
            if (s_names.Contains(simpleName))
            {
                string kept;
                s_pathByName.TryGetValue(simpleName, out kept);
                if (!SameStemChoice.SamePath(kept, location))
                {
                    Action<string> sink = ReportSink;
                    if (sink != null)
                    {
                        sink("引用集去重（保留先到者）：名字=" + simpleName
                            + " 保留=" + (kept ?? "<未知>") + " 身份=" + SameStemChoice.IdentityOfFile(kept)
                            + " 丢弃=" + location + " 身份=" + SameStemChoice.IdentityOfFile(location)
                            + " 原因=同一编译内同简单名只能有一个版本（Roslyn CS1704）；"
                            + "若需换用新版本，请调整加载顺序/黑名单或先卸载旧版本");
                    }
                }
                return false;
            }
            if (!s_names.Add(simpleName)) return false;
            s_pathByName[simpleName] = location;
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

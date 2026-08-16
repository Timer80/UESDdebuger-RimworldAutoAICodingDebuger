using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace McpRimDebug
{
    /// <summary>
    /// P1-MD-2.2：超限回复全量落盘 + 截断报告。
    ///
    /// 当某观测/求值工具的输出 data 携带截断标记（truncated / framesTruncated / expanded=false /
    /// 任意字段超限）时，说明当前 MCP 回复只含受限裁剪版，属「超限回复」。本 Sink 把**完整未截断**
    /// 内容落盘到 %TEMP%\McpRimDebug\&lt;uuid&gt;.json，并把截断报告（含 fullBytes / fullFile /
    /// truncated=true）挂到回复 data["truncation"]，让 MCP 客户端既能拿到当前裁剪回复，又能按路径
    /// 读取全量内容。
    ///
    /// 定案：
    /// - 落盘路径：%TEMP%\McpRimDebug\&lt;guid&gt;.json；
    /// - 存完整未截断内容（调用方用 ValueFormatter.FormatFull 或全量帧列表构建）；
    /// - MCP 回复返回「已被截断 + 路径 + 报告」；
    /// - 自动清理：会话结束 SweepOnSessionEnd 删本会话登记文件；启动 Init() 清扫 N 天前孤儿文件。
    /// </summary>
    public sealed class TruncationSink
    {
        /// <summary>回复上限（字符）缺省值。</summary>
        public const int DefaultReplyLimitChars = 20000;

        /// <summary>
        /// 回复上限（字符）。实时读环境变量 MCP_RIMDBG_TRUNCATE_LIMIT 覆盖（非法/非正忽略），
        /// 缺省 DefaultReplyLimitChars。不缓存，selftest 可临时改 env 后断言。
        /// </summary>
        public static int ReplyLimitChars
        {
            get
            {
                string v = Environment.GetEnvironmentVariable("MCP_RIMDBG_TRUNCATE_LIMIT");
                if (!string.IsNullOrWhiteSpace(v) && int.TryParse(v, out int p) && p > 0)
                    return p;
                return DefaultReplyLimitChars;
            }
        }

        /// <summary>落盘目录（%TEMP%\McpRimDebug）。</summary>
        public static string Dir
        {
            get { return Path.Combine(Path.GetTempPath(), "McpRimDebug"); }
        }

        static readonly ConcurrentDictionary<string, string> activeFiles = new ConcurrentDictionary<string, string>();

        /// <summary>启动初始化：确保目录存在；清扫 1 天前的孤儿 .json（防磁盘堆积）。</summary>
        public static void Init()
        {
            try { Directory.CreateDirectory(Dir); } catch { }
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (string f in Directory.GetFiles(Dir, "*.json"))
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                        TryDelete(f);
            }
            catch { }
        }

        /// <summary>构造新临时文件路径并登记（供会话结束 Sweep 清理）。</summary>
        public static string NewTempPath()
        {
            string p = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".json");
            activeFiles[p] = p;
            return p;
        }

        /// <summary>把完整内容落盘（UTF-8 无 BOM）。</summary>
        public static void WriteFull(string path, string fullJson)
        {
            File.WriteAllText(path, fullJson, new UTF8Encoding(false));
        }

        /// <summary>截断报告字典。</summary>
        public static Dictionary<string, object> Report(string path, long bytes, string reason)
        {
            return new Dictionary<string, object>
            {
                { "truncated", true },
                { "reason", reason },
                { "fullBytes", bytes },
                { "fullFile", path },
            };
        }

        /// <summary>会话结束清理（Detach/HandleDisconnect 调用）：删除本会话登记且仍存在的文件。</summary>
        public static void SweepOnSessionEnd()
        {
            foreach (string p in activeFiles.Keys)
            {
                if (activeFiles.TryRemove(p, out _))
                    TryDelete(p);
            }
        }

        /// <summary>
        /// 截断接入助手：检测 data 是否携带截断标记；若是，调用 fullJsonBuilder() 生成**完整未截断**
        /// 内容并落盘，把截断报告挂到 data["truncation"]。fullJsonBuilder 仅在需要时调用一次（避免为
        /// 每个回复都构建全量快照）。返回是否已挂上截断报告。落盘失败不阻断正常回复（把错误记入
        /// data["truncationError"]）。
        /// </summary>
        public static bool AttachTruncation(Dictionary<string, object> data, Func<string> fullJsonBuilder)
        {
            if (data == null || fullJsonBuilder == null || !HasTruncationMarker(data))
                return false;
            try
            {
                string path = NewTempPath();
                string full = fullJsonBuilder();
                long bytes = Encoding.UTF8.GetByteCount(full);
                WriteFull(path, full);
                data["truncation"] = Report(path, bytes, "超出回复上限");
                return true;
            }
            catch (Exception ex)
            {
                data["truncationError"] = "全量落盘失败: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 递归扫描 node 是否携带截断标记：值为 true 的 "truncated"/"framesTruncated"，
        /// 或值为 false 的 "expanded"（深度受限对象）——与 ValueFormatter 的受限输出标记一致。
        /// </summary>
        public static bool HasTruncationMarker(object node)
        {
            if (node is Dictionary<string, object> d)
            {
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (kv.Key == "truncated" && kv.Value is bool b && b) return true;
                    if (kv.Key == "framesTruncated" && kv.Value is bool fb && fb) return true;
                    if (kv.Key == "expanded" && kv.Value is bool eb && !eb) return true;
                    if (HasTruncationMarker(kv.Value)) return true;
                }
                return false;
            }
            if (node is IList list)
            {
                foreach (object item in list)
                    if (HasTruncationMarker(item)) return true;
                return false;
            }
            return false;
        }

        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }
}

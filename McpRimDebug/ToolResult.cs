using System.Collections.Generic;

namespace McpRimDebug
{
    /// <summary>
    /// 所有 MCP 工具的统一返回结构：{ ok, message?, data? }。
    /// 工具方法直接返回本类型，由 MCP SDK 序列化为工具调用的 text content。
    /// </summary>
    public sealed class ToolResult
    {
        /// <summary>操作是否成功。</summary>
        public bool Ok { get; set; }

        /// <summary>人类可读的结果/错误说明。</summary>
        public string Message { get; set; }

        /// <summary>结构化数据（可选；包含端口、版本、PID 等）。</summary>
        public Dictionary<string, object> Data { get; set; }

        public static ToolResult OkResult(string message, Dictionary<string, object> data = null)
        {
            return new ToolResult { Ok = true, Message = message, Data = data };
        }

        /// <summary>
        /// 错误结果。data 可选（2026-09-19 新增）：失败也要能带结构化诊断
        /// （如 attach 失败时的 port / portListening / diagnosis），调用方不必解析文案。
        /// </summary>
        public static ToolResult ErrorResult(string message, Dictionary<string, object> data = null)
        {
            return new ToolResult { Ok = false, Message = message, Data = data };
        }
    }
}

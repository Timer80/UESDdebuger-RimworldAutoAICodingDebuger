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

        public static ToolResult ErrorResult(string message)
        {
            return new ToolResult { Ok = false, Message = message };
        }
    }
}

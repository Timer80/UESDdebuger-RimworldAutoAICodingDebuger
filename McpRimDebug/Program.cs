using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpRimDebug
{
    /// <summary>
    /// MCP 服务器宿主（stdio 传输）。
    ///
    /// 使用官方 ModelContextProtocol SDK 2.1.0：由于本项目仅还原了该 SDK 的
    /// Microsoft.Extensions.*Abstractions 传递依赖（没有具体 ServiceCollection/LoggerFactory 包），
    /// 因此不采用 DI 构建器，而是直接手工组装：
    ///   McpServerOptions（工具集合 + 能力声明）→ StdioServerTransport → McpServer.Create → RunAsync。
    ///
    /// 支持 --selftest 参数：不经 stdio，直接执行一次 status 工具验证会话核心可工作。
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest")
                return RunSelfTest();

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation
                {
                    Name = "McpRimDebug",
                    Version = "0.8.0.0",
                    Description = "RimWorld Mono Soft Debugger MCP server",
                },
                ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal),
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            };

            RimDebugTools.Register(options.ToolCollection);

            var transport = new StdioServerTransport(options, NullLoggerFactory.Instance);
            var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, null);

            // 阻塞运行直到 stdin 关闭（MCP 客户端断开）
            await server.RunAsync();
            return 0;
        }

        /// <summary>
        /// 极简自检（不经 stdio，静态自测）：
        /// 1) 工具注册表包含全部 20 个工具；
        /// 2) 各工具的 InputSchema 完整（含声明的参数）；
        /// 3) 未连接时各工具返回可读错误（不崩溃）；
        /// 4) 配置环境变量（MCP_RIMDBG_*）生效；
        /// 5) 统一执行包装 Guard 把异常转可读文本；
        /// 6) status 返回增强字段（needResume / gameProcessRunning / portOpen）。
        /// </summary>
        private static int RunSelfTest()
        {
            try
            {
                // ---- 1) 工具注册表完整性 ----
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation
                    {
                        Name = "McpRimDebug",
                        Version = "0.8.0.0",
                        Description = "RimWorld Mono Soft Debugger MCP server",
                    },
                    ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(StringComparer.Ordinal),
                    Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                };
                RimDebugTools.Register(options.ToolCollection);

                string[] requiredTools =
                {
                    "status", "attach", "detach", "resume", "suspend", "launch",
                    "break_add", "break_list", "break_remove", "break_clear",
                    "break_exception", "wait", "step",
                    "threads", "callstack", "locals", "inspect",
                    "eval", "find_types", "find_methods",
                };
                foreach (string name in requiredTools)
                {
                    bool found = false;
                    foreach (McpServerTool t in options.ToolCollection)
                    {
                        if (t.ProtocolTool.Name == name) { found = true; break; }
                    }
                    if (!found)
                    {
                        Console.Error.WriteLine("selftest 失败: 缺少工具 " + name);
                        return 2;
                    }
                }

                // ---- 2) schema 完整性（检查声明的参数名出现在 InputSchema 中） ----
                var schemaChecks = new Dictionary<string, string[]>
                {
                    ["break_add"] = new[] { "method", "line" },
                    ["break_list"] = new string[0],
                    ["break_remove"] = new[] { "id" },
                    ["break_clear"] = new string[0],
                    ["break_exception"] = new[] { "type", "caught", "uncaught" },
                    ["wait"] = new[] { "eventType", "timeoutMs" },
                    ["step"] = new[] { "threadId", "direction" },
                    ["threads"] = new string[0],
                    ["callstack"] = new[] { "threadId", "frameLimit" },
                    ["locals"] = new[] { "threadId", "frameIndex" },
                    ["inspect"] = new[] { "handle" },
                    ["eval"] = new[] { "expression", "threadId", "frameIndex" },
                    ["find_types"] = new[] { "query", "limit" },
                    ["find_methods"] = new[] { "query", "limit" },
                };
                foreach (KeyValuePair<string, string[]> kv in schemaChecks)
                {
                    McpServerTool tool = null;
                    foreach (McpServerTool t in options.ToolCollection)
                    {
                        if (t.ProtocolTool.Name == kv.Key) { tool = t; break; }
                    }
                    if (tool == null)
                    {
                        Console.Error.WriteLine("selftest 失败: 缺少工具 " + kv.Key);
                        return 2;
                    }
                    string schema = tool.ProtocolTool.InputSchema.ToString();
                    if (string.IsNullOrEmpty(schema))
                    {
                        Console.Error.WriteLine("selftest 失败: " + kv.Key + " 的 InputSchema 为空");
                        return 2;
                    }
                    foreach (string paramName in kv.Value)
                    {
                        if (schema.IndexOf(paramName, StringComparison.Ordinal) < 0)
                        {
                            Console.Error.WriteLine("selftest 失败: " + kv.Key + " 的 schema 缺少参数 " + paramName
                                + "（schema: " + schema + "）");
                            return 2;
                        }
                    }
                }

                // ---- 3) 未连接时各工具返回可读错误（不崩溃） ----
                ToolResult[] disconnectedResults =
                {
                    DebugSession.Instance.BreakAdd("Verse.Thing:DoWork", null),
                    DebugSession.Instance.BreakList(),
                    DebugSession.Instance.BreakRemove(1),
                    DebugSession.Instance.BreakClear(),
                    DebugSession.Instance.BreakException(null, null, null),
                    DebugSession.Instance.Wait("breakpoint", 100),
                    DebugSession.Instance.Step(1, "into"),
                    DebugSession.Instance.Threads(),
                    DebugSession.Instance.Callstack(1, 50),
                    DebugSession.Instance.Locals(1, 0),
                    DebugSession.Instance.Inspect(1),
                    DebugSession.Instance.Eval("this.def.defName", 0, 0),
                    DebugSession.Instance.FindTypes("ThingDef", 50),
                    DebugSession.Instance.FindMethods("Tick", 50),
                };
                foreach (ToolResult r in disconnectedResults)
                {
                    if (r.Ok)
                    {
                        Console.Error.WriteLine("selftest 失败: 未连接时不应 ok，实际消息: " + r.Message);
                        return 2;
                    }
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(r));
                }

                // ---- 4) 表达式解析器单测（不连 VM，纯解析层） ----
                if (!RunParserSelfTest())
                    return 2;

                // ---- 5) 配置：环境变量 MCP_RIMDBG_* 生效 + 默认值（保存/恢复原 env） ----
                string savedHost = Environment.GetEnvironmentVariable("MCP_RIMDBG_HOST");
                string savedPort = Environment.GetEnvironmentVariable("MCP_RIMDBG_PORT");
                string savedGame = Environment.GetEnvironmentVariable("MCP_RIMDBG_GAME_PATH");
                try
                {
                    if (string.IsNullOrEmpty(Defaults.GamePath)
                        || !Defaults.GamePath.EndsWith("RimWorldWin64.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine("selftest 失败: Defaults.GamePath 默认值不正确: " + Defaults.GamePath);
                        return 2;
                    }
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_HOST", "10.1.2.3");
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_PORT", "59999");
                    if (Defaults.Host != "10.1.2.3" || DebugSession.DefaultHost() != "10.1.2.3")
                    {
                        Console.Error.WriteLine("selftest 失败: MCP_RIMDBG_HOST 未生效");
                        return 2;
                    }
                    if (Defaults.Port != 59999 || DebugSession.DefaultPort() != 59999)
                    {
                        Console.Error.WriteLine("selftest 失败: MCP_RIMDBG_PORT 未生效");
                        return 2;
                    }
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_PORT", "not-a-port");
                    if (Defaults.Port != Defaults.FallbackPort)
                    {
                        Console.Error.WriteLine("selftest 失败: 非法 MCP_RIMDBG_PORT 应回退默认 " + Defaults.FallbackPort);
                        return 2;
                    }
                }
                finally
                {
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_HOST", savedHost);
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_PORT", savedPort);
                    Environment.SetEnvironmentVariable("MCP_RIMDBG_GAME_PATH", savedGame);
                }

                // ---- 6) 统一执行包装 Guard：异常转可读 { ok:false, message } ----
                ToolResult guardOk = DebugSession.Instance.Guard("_selftest", false, () => ToolResult.OkResult("ok"));
                if (!guardOk.Ok)
                {
                    Console.Error.WriteLine("selftest 失败: Guard 应透传 ok 结果");
                    return 2;
                }
                ToolResult guardErr = DebugSession.Instance.Guard("_selftest", false, () => { throw new InvalidOperationException("boom"); });
                if (guardErr.Ok || string.IsNullOrEmpty(guardErr.Message) || guardErr.Message.IndexOf("boom", StringComparison.Ordinal) < 0)
                {
                    Console.Error.WriteLine("selftest 失败: Guard 未把异常转可读错误: " + (guardErr.Message ?? "null"));
                    return 2;
                }

                // ---- 7) status：ok=true 且 data 含增强字段 ----
                ToolResult st = DebugSession.Instance.Status();
                if (!st.Ok || st.Data == null
                    || !st.Data.ContainsKey("state")
                    || !st.Data.ContainsKey("needResume")
                    || !st.Data.ContainsKey("gameProcessRunning")
                    || !st.Data.ContainsKey("portOpen")
                    || !st.Data.ContainsKey("debugPortOpen")
                    || !st.Data.ContainsKey("debugPortFromLog"))
                {
                    Console.Error.WriteLine("selftest 失败: status 缺少增强字段（state/needResume/gameProcessRunning/portOpen/debugPortOpen/debugPortFromLog）");
                    return 2;
                }
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(st));
                Console.WriteLine("selftest 通过: 20 个工具注册齐全，schema 完整，未连接行为正确，"
                    + "配置环境变量生效，统一异常包装可用，表达式解析器自测通过");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: " + ex);
                return 2;
            }
        }

        /// <summary>
        /// 表达式解析器单元断言（不连 VM，纯解析层）：
        /// 1) Describe() 应还原表达式文本（成员链 / 静态类型全名 / 方法调用 / 括号 / 字符串转义 / 数字）；
        /// 2) AST 结构（成员链嵌套、调用参数列表、字面量）；
        /// 3) NeedsThread / NeedsFrame 预判；
        /// 4) 语法错误应抛 ExprParseException。
        /// 返回是否全部通过。
        /// </summary>
        private static bool RunParserSelfTest()
        {
            try
            {
                // 1) 成员链重建（Describe 应还原表达式文本）
                AssertParse("this", "this");
                AssertParse("this.def.defName", "this.def.defName");
                AssertParse("x.Value.ToString()", "x.Value.ToString()");
                AssertParse("Verse.Thing", "Verse.Thing");
                AssertParse("Verse.Thing.SomeStatic", "Verse.Thing.SomeStatic");

                // 2) 方法调用参数列表（括号 + 逗号）
                AssertParse("obj.M(1, \"x\", null)", "obj.M(1, \"x\", null)");
                AssertParse("obj.M()", "obj.M()");
                AssertParse("Math.Max(1, 2.5f)", "Math.Max(1, 2.5f)");

                // 3) 括号分组（Describe 重建时括号消失，仅断言不抛异常且语义等价）
                AssertParse("(this).x", "this.x");
                AssertParse("((this).y).z", "this.y.z");

                // 4) 字符串转义与数字字面量保留原文本
                AssertParse("\"a\\n b\"", "\"a\\n b\"");
                AssertParse("\"say \\\"hi\\\"\"", "\"say \\\"hi\\\"\"");
                AssertParse("123", "123");
                AssertParse("-7", "-7");
                AssertParse("1.5d", "1.5d");
                AssertParse("null", "null");

                // 5) AST 结构
                ExprNode chain = ExprParser.Parse("this.def.defName");
                if (chain.Kind != ExprNodeKind.Member || chain.Text != "defName"
                    || chain.Target == null || chain.Target.Kind != ExprNodeKind.Member
                    || chain.Target.Text != "def"
                    || chain.Target.Target == null || chain.Target.Target.Kind != ExprNodeKind.This)
                    throw new Exception("成员链 AST 结构不正确: " + chain.Describe());

                ExprNode call = ExprParser.Parse("obj.M(1, \"x\")");
                if (call.Kind != ExprNodeKind.Member || !call.IsCall
                    || call.Args == null || call.Args.Count != 2
                    || call.Args[0].Kind != ExprNodeKind.Literal || call.Args[1].Kind != ExprNodeKind.Literal)
                    throw new Exception("方法调用 AST 结构不正确: " + call.Describe());

                ExprNode lit = ExprParser.Parse("\"str\"");
                if (lit.Kind != ExprNodeKind.Literal)
                    throw new Exception("字符串字面量应为 Literal 节点: " + lit.Describe());

                // 6) NeedsThread / NeedsFrame 预判
                if (!ExpressionEvaluator.NeedsFrame("this.x"))
                    throw new Exception("NeedsFrame(this.x) 应为 true");
                if (!ExpressionEvaluator.NeedsThread("obj.M(1)"))
                    throw new Exception("NeedsThread(obj.M(1)) 应为 true");
                if (ExpressionEvaluator.NeedsFrame("\"lit\""))
                    throw new Exception("NeedsFrame(字符串字面量) 应为 false");
                if (ExpressionEvaluator.NeedsThread("\"lit\""))
                    throw new Exception("NeedsThread(字符串字面量) 应为 false");

                // 7) 语法错误应抛 ExprParseException
                ExpectParseError(null);
                ExpectParseError("");
                ExpectParseError("this..x");
                ExpectParseError("(this");
                ExpectParseError("this.");
                ExpectParseError("\"未闭合");
                ExpectParseError("obj.M(1,");
                ExpectParseError(".obj");

                Console.WriteLine("    [解析器自测] 全部断言通过（成员链/调用参数/字面量/结构/预判/语法错误）");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: 表达式解析器自测异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>断言 Parse(input).Describe() == expected。</summary>
        private static void AssertParse(string input, string expected)
        {
            ExprNode node = ExprParser.Parse(input);
            string actual = node.Describe();
            if (actual != expected)
                throw new Exception("Parse(" + (input ?? "<null>") + ").Describe() = " + actual + "，期望 " + expected);
        }

        /// <summary>断言 Parse(input) 应抛 ExprParseException（否则失败）。</summary>
        private static void ExpectParseError(string input)
        {
            try
            {
                ExprParser.Parse(input);
            }
            catch (ExprParseException)
            {
                return; // 期望的语法错误
            }
            throw new Exception("Parse(" + (input ?? "<null>") + ") 应抛出 ExprParseException 但未抛");
        }
    }
}

using System;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpRimDebug
{
    /// <summary>
    /// MCP 工具定义：会话管理 / 断点事件 / 观测 / 步进 / 求值与搜索，共 21 个工具。
    /// 全部工具方法为静态方法组（method group），经 McpServerTool.Create 注册；
    /// 参数上的 [Description] 会进入 tools/list 的 JSON Schema 输入描述。
    /// 实现全部委托给 DebugSession 单例，返回统一 ToolResult 结构。
    /// </summary>
    public static class RimDebugTools
    {
        /// <summary>把全部 21 个工具注册进 MCP 服务器的工具集合。</summary>
        public static void Register(McpServerPrimitiveCollection<McpServerTool> collection)
        {
            Add(collection, "status",
                "查看调试会话状态：游戏进程是否运行、调试端口监听情况、连接状态、VM 协议版本、挂起状态；已从 Player.log 发现游戏自报调试端口（debugPortFromLog，Unity 每次运行随机）时优先报告并用它探测监听；已连接但未 resume 时会提示需 resume 游戏才运行",
                (Func<ToolResult>)Status);
            Add(collection, "attach",
                "连接 RimWorld 的 Mono Soft Debugger 端口（缺省取 MCP_RIMDBG_HOST/MCP_RIMDBG_PORT，未设置时为 127.0.0.1:56574；但游戏端口每次运行随机，请先用 status 查看 debugPortFromLog 并传入该端口），完成 DWP 握手并返回协议版本（应为 2.57）与 VM 版本",
                (Func<string, int?, ToolResult>)Attach);
            Add(collection, "reconnect",
                "重连调试会话：**先检查连接是否真的断了**——已 Attached 且 socket 可用时直接短路返回 "
                + "{ok:true, skipped:true, noop:true, detail:\"connection-alive\"}，并明确回报「连接实际未断」"
                + "（这不是错误、未做任何改动）；会话处于 Attaching/Detaching 过渡态时拒绝且不动手。"
                + "确实断连时才复位会话（不向死 VM 发命令），端口缺省按 ports.json（unityDebugPort）→ Player.log → 上次会话端点自动发现，然后 attach。"
                + "用途：launch 后连接被抖动掉、或 attach 因代理会话槽占死失败后补一次连接（失败信息里带 portListening/diagnosis，便于判断该重试还是该重启游戏）",
                (Func<string, int?, ToolResult>)Reconnect);
            Add(collection, "detach",
                "安全断开调试会话：告知代理（VM_Dispose）后关闭连接，游戏继续运行，会话回到 Disconnected",
                (Func<ToolResult>)Detach);
            Add(collection, "resume",
                "恢复整个 VM 运行，并更新会话的挂起状态",
                (Func<ToolResult>)Resume);
            Add(collection, "suspend",
                "挂起整个 VM，并更新会话的挂起状态",
                (Func<ToolResult>)Suspend);
            Add(collection, "launch",
                "以调试模式启动游戏（Unity 由 boot.config 触发内嵌调试代理，端口每次运行随机并写入 Player.log）；"
                + "默认（autoAttach=true）自动等待实际端口出现并 attach，Unity 的 Debug(Player) 告知窗在 attach 成功时自动关闭，"
                + "之后调用 resume 游戏才开始运行；已有 RimWorld 进程运行时会警告并中止（端口会冲突）",
                (Func<string, int?, string, bool, ToolResult>)Launch);
            Add(collection, "break_add",
                "在指定方法设置断点并返回断点 id。method 格式: 命名空间.类型名:方法名（重载可附参数签名）；缺省 line 为方法入口断点，指定 line 则在源码行下断点",
                (Func<string, int?, ToolResult>)BreakAdd);
            Add(collection, "break_list",
                "列出当前会话全部断点（id + 描述）",
                (Func<ToolResult>)BreakList);
            Add(collection, "break_remove",
                "按 id 移除指定断点（禁用对应事件请求）",
                (Func<int, ToolResult>)BreakRemove);
            Add(collection, "break_clear",
                "清空当前会话全部断点",
                (Func<ToolResult>)BreakClear);
            Add(collection, "break_exception",
                "创建异常事件请求：type 为异常类型完整名（缺省=全部异常），caught/uncaught 过滤捕获/未捕获异常，返回请求 id",
                (Func<string, bool?, bool?, ToolResult>)BreakException);
            Add(collection, "wait",
                "阻塞等待匹配的调试事件（eventType: any/breakpoint/step/exception 或任意 SDB 事件名；timeoutMs 默认 30000），超时返回空；命中时返回事件类型、线程、命中位置与调用栈摘要",
                (Func<string, int, ToolResult>)Wait);
            Add(collection, "step",
                "单步执行指定线程（threadId 来自 wait 命中结果；direction=into/over/out，粒度 Line），等 StepEvent 后返回命中位置",
                (Func<long, string, ToolResult>)Step);
            Add(collection, "threads",
                "列出当前 VM 全部线程（id/threadId/名称/线程状态）；需 VM 挂起（先 suspend，或 wait/step 到断点/步进命中点）",
                (Func<ToolResult>)Threads);
            Add(collection, "callstack",
                "获取指定线程的调用栈（逐帧: 方法全名 + IL 偏移 + 源码 file:line）；需 VM 挂起",
                (Func<long, int, ToolResult>)Callstack);
            Add(collection, "locals",
                "获取指定线程指定栈帧的实参 / 局部变量 / this 及格式化取值（子对象返回句柄供 inspect 继续展开）；需 VM 挂起",
                (Func<long, int, ToolResult>)Locals);
            Add(collection, "inspect",
                "按对象句柄展开对象字段（深度≤3、字段≤50、字符串≤512 字符、数组前≤32 元素，子对象返回新句柄）；需 VM 挂起",
                (Func<int, ToolResult>)Inspect);
            Add(collection, "eval",
                "在游戏进程内求值表达式（迷你 C# 语法：this / 局部变量 / 实参 / 静态类型全名 / 字符串数字字面量；成员链 .字段 .属性 .方法(args)）；VM 运行时会自动挂起求值再恢复，结果经值格式化器输出（子对象返回句柄）。"
                + "【危险面声明 · 刻意不加白名单】此工具可调用游戏中任意 public 方法（含副作用/PInvoke/长同步阻塞），是对真实游戏进程的底层操控，结果不可撤销、可能卡顿或损坏存档。"
                + "本项目本义即底层操作排查顽疾，故不做方法过滤。日常请优先调用已有高层安全工具（get_map_things/get_colonists 等 RIMAPI 查询、既有 UE 工具），仅在需要直接操作底层状态时才用 eval。",
                (Func<string, long, int, ToolResult>)Eval);
            Add(collection, "find_types",
                "在游戏程序集元数据中按子串搜索类型（大小写不敏感），返回类型全名列表；分装配批次限流防卡死",
                (Func<string, int, ToolResult>)FindTypes);
            Add(collection, "find_methods",
                "在游戏程序集的类型方法上按方法名子串搜索（大小写不敏感），返回 {type, method} 列表；同样限流防卡死",
                (Func<string, int, ToolResult>)FindMethods);
        }

        static void Add(McpServerPrimitiveCollection<McpServerTool> collection, string name, string description, Delegate method)
        {
            collection.Add(McpServerTool.Create(method, new McpServerToolCreateOptions
            {
                Name = name,
                Description = description,
            }));
        }

        // ------------------------------------------------------------ 工具实现
        // 全部工具经 DebugSession.Guard 统一包装：异常 → 可读 { ok:false, message }；
        // withTimeout=true 的阻塞型 VM 命令（attach/resume/suspend/eval/断点/观测/搜索/detach）
        // 附加看门狗超时；wait/step/status/launch/break_list 传 false（有各自的超时或有界执行）。

        [Description("查看调试会话状态")]
        public static ToolResult Status()
        {
            return DebugSession.Instance.Guard("status", false, () => DebugSession.Instance.Status());
        }

        [Description("连接游戏调试端口")]
        public static ToolResult Attach(
            [Description("目标主机 IP（缺省取 MCP_RIMDBG_HOST，未设置时为 127.0.0.1，支持局域网 IP）")] string host = null,
            [Description("SDB 调试端口（缺省取 MCP_RIMDBG_PORT，未设置时为 56574；游戏端口每次运行随机，请先用 status 查看 debugPortFromLog 并传入该端口）")] int? port = null)
        {
            return DebugSession.Instance.Guard("attach", true, () => DebugSession.Instance.Attach(host, port));
        }

        [Description("安全断开调试会话（游戏继续运行）")]
        public static ToolResult Detach()
        {
            return DebugSession.Instance.Guard("detach", true, () => DebugSession.Instance.Detach());
        }

        [Description("重连调试会话（自动重取端口；旧会话不可用时先复位）")]
        public static ToolResult Reconnect(
            [Description("目标主机 IP（缺省沿用上次会话主机，否则取 MCP_RIMDBG_HOST / 127.0.0.1）")] string host = null,
            [Description("SDB 调试端口（缺省自动发现：ports.json 的 unityDebugPort → Player.log → 上次会话端点）")] int? port = null)
        {
            return DebugSession.Instance.Guard("reconnect", true, () => DebugSession.Instance.Reconnect(host, port));
        }

        [Description("恢复整个 VM 运行")]
        public static ToolResult Resume()
        {
            return DebugSession.Instance.Guard("resume", true, () => DebugSession.Instance.Resume());
        }

        [Description("挂起整个 VM")]
        public static ToolResult Suspend()
        {
            return DebugSession.Instance.Guard("suspend", true, () => DebugSession.Instance.Suspend());
        }

        [Description("以调试模式启动游戏（默认自动 attach，关闭 Unity 的 Debug(Player) 告知窗）")]
        public static ToolResult Launch(
            [Description("游戏可执行文件路径（缺省取 MCP_RIMDBG_GAME_PATH，未设置时为 C:\\SteamLibrary\\steamapps\\common\\RimWorld\\RimWorldWin64.exe；其他安装路径如 F:\\RimWorld3\\RimWorldWin64.exe 可在此传入）")] string path = null,
            [Description("SDB 调试端口（缺省取 MCP_RIMDBG_PORT，未设置时为 56574；autoAttach=true 时以 Player.log 实际端口为准）")] int? port = null,
            [Description("工作目录（默认游戏根目录）")] string workingDir = null,
            [Description("启动后是否自动等待实际调试端口出现并 attach（缺省 true；attach 成功后 Unity 的 Debug(Player) 告知窗自动关闭，之后需 resume 游戏才运行）")] bool autoAttach = true)
        {
            return DebugSession.Instance.Guard("launch", false, () => DebugSession.Instance.Launch(path, port, workingDir, autoAttach));
        }

        // ------------------------------------------------------------ 断点 / 事件（Task 3）

        [Description("在指定方法设置断点并返回断点 id")]
        public static ToolResult BreakAdd(
            [Description("方法定位，格式: 命名空间.类型名:方法名（重载可附参数签名），如 Verse.Thing:DoWork 或 Verse.Thing:DoWork(System.String)")] string method,
            [Description("可选：源码行号（缺省为方法入口断点）")] int? line = null)
        {
            return DebugSession.Instance.Guard("break_add", true, () => DebugSession.Instance.BreakAdd(method, line));
        }

        [Description("列出当前会话全部断点")]
        public static ToolResult BreakList()
        {
            return DebugSession.Instance.Guard("break_list", false, () => DebugSession.Instance.BreakList());
        }

        [Description("按 id 移除指定断点")]
        public static ToolResult BreakRemove(
            [Description("断点 id（break_add 返回值）")] int id)
        {
            return DebugSession.Instance.Guard("break_remove", true, () => DebugSession.Instance.BreakRemove(id));
        }

        [Description("清空当前会话全部断点")]
        public static ToolResult BreakClear()
        {
            return DebugSession.Instance.Guard("break_clear", true, () => DebugSession.Instance.BreakClear());
        }

        [Description("创建异常事件请求")]
        public static ToolResult BreakException(
            [Description("异常类型完整名（缺省=全部异常），如 System.NullReferenceException")] string type = null,
            [Description("是否在异常被捕获处触发（缺省 true）")] bool? caught = null,
            [Description("是否在异常未被捕获处触发（缺省 true）")] bool? uncaught = null)
        {
            return DebugSession.Instance.Guard("break_exception", true, () => DebugSession.Instance.BreakException(type, caught, uncaught));
        }

        [Description("阻塞等待匹配的调试事件")]
        public static ToolResult Wait(
            [Description("事件类型过滤: any / breakpoint / step / exception，或任意 SDB 事件名（缺省 any）")] string eventType = "any",
            [Description("超时毫秒（缺省 30000）")] int timeoutMs = 30000)
        {
            return DebugSession.Instance.Guard("wait", false, () => DebugSession.Instance.Wait(eventType, timeoutMs));
        }

        [Description("单步执行指定线程")]
        public static ToolResult Step(
            [Description("线程 id（wait 命中结果中的 threadId）")] long threadId,
            [Description("步进方向: into / over / out（缺省 into）")] string direction = "into")
        {
            return DebugSession.Instance.Guard("step", false, () => DebugSession.Instance.Step(threadId, direction));
        }

        // ------------------------------------------------------------ 观测（Task 4）

        [Description("列出当前 VM 全部线程")]
        public static ToolResult Threads()
        {
            return DebugSession.Instance.Guard("threads", true, () => DebugSession.Instance.Threads());
        }

        [Description("获取指定线程的调用栈")]
        public static ToolResult Callstack(
            [Description("线程 id（threads/wait 命中结果中的 id）")] long threadId,
            [Description("返回的最大帧数（缺省 50）")] int frameLimit = 50)
        {
            return DebugSession.Instance.Guard("callstack", true, () => DebugSession.Instance.Callstack(threadId, frameLimit));
        }

        [Description("获取指定线程指定栈帧的实参/局部变量/this")]
        public static ToolResult Locals(
            [Description("线程 id（threads/wait 命中结果中的 id）")] long threadId,
            [Description("栈帧索引（0=最内层，缺省 0）")] int frameIndex = 0)
        {
            return DebugSession.Instance.Guard("locals", true, () => DebugSession.Instance.Locals(threadId, frameIndex));
        }

        [Description("按对象句柄展开对象字段（子对象返回新句柄）")]
        public static ToolResult Inspect(
            [Description("对象句柄（来自 locals/inspect 输出的 handle）")] int handle)
        {
            return DebugSession.Instance.Guard("inspect", true, () => DebugSession.Instance.Inspect(handle));
        }

        // ------------------------------------------------------------ 求值与搜索（Task 5）

        [Description("在游戏进程内求值表达式（结果经值格式化器输出，子对象返回句柄）")]
        public static ToolResult Eval(
            [Description("表达式，迷你 C# 语法：this / 局部变量 / 实参 / 静态类型全名 / 字面量；成员链 .字段 .属性 .方法(args)。示例: this.def.defName / Verse.Thing.SomeStatic / obj.M(1, \"x\")")] string expression,
            [Description("线程 id（threads/wait 结果中的 id；0=自动选择线程）")] long threadId = 0,
            [Description("栈帧索引（0=最内层，缺省 0）")] int frameIndex = 0)
        {
            return DebugSession.Instance.Guard("eval", true, () => DebugSession.Instance.Eval(expression, threadId, frameIndex));
        }

        [Description("在游戏程序集元数据中按子串搜索类型（大小写不敏感），返回类型全名列表")]
        public static ToolResult FindTypes(
            [Description("子串查询（匹配类型全名/简单名，大小写不敏感），如 ThingDef")] string query,
            [Description("返回结果上限（缺省 50，最大 200）")] int limit = 50)
        {
            return DebugSession.Instance.Guard("find_types", true, () => DebugSession.Instance.FindTypes(query, limit));
        }

        [Description("在游戏程序集的类型方法上按方法名子串搜索（大小写不敏感），返回 {type, method} 列表")]
        public static ToolResult FindMethods(
            [Description("子串查询（匹配方法名，大小写不敏感），如 Tick")] string query,
            [Description("返回结果上限（缺省 50，最大 200）")] int limit = 50)
        {
            return DebugSession.Instance.Guard("find_methods", true, () => DebugSession.Instance.FindMethods(query, limit));
        }
    }
}

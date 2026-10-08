裁决：值得修

「`execute_csharp_code` 拿不到用户代码写的日志」这一半由源码确凿证实——`Source/UELoader/UEHttpHandler.cs:207-210` 在主线程闭包返回的同一瞬间就摘掉日志监听器，而 `Verse.Log.*` 是排队到下一帧才刷的，**结构上不可能被捕获**；但「`Player.log` 里 0 命中」这个具体观察发生在游戏已被证实卡死的同一分钟内，判据被污染，我**无法**从源码证实它，建议 PLAN 阶段把范围收窄到前者。

## 核实过程

### 1. 吐槽板原文（`docs/mcp-吐槽板.md:271-297`）

断言：在 `execute_csharp_code` 里执行 `UnityEngine.Debug.Log("W1P01_PROBE_MARKER_ALPHA")` 或 `Verse.Log.Message(...)`，`Player.log` 检索该标记 → 0 命中；同时「代码确实跑了、有返回值」。

### 2. 调用链与「已有管道」这一观察属实

`MCP/index.js:4530-4544`：

```javascript
case 'execute_csharp_code': {
  const result = await requireStage([STAGES.IN_GAME], async () => {
    const r = await callUnityExplorerAPI('/unityexplorer/console/execute', 'POST', {
      code: args.code
    }, { slow: true });
```

`Source/UELoader/UEHttpHandler.cs:163-245` 是接收端，**它确实已经在收集日志并放进返回值**：

```csharp
// 临时订阅 UE 日志流，捕获 Evaluate 的结果/错误输出
var captured = new List<string>();
Action<string, LogType> listener = (m, t) => { lock (captured) captured.Add(m); };
ExplorerStandalone.OnLog += listener;
...
UEMainThreadDispatcher.ExecuteOnMainThread(() => { ConsoleController.Evaluate(code, false); ... }, 20000);
...
finally { ExplorerStandalone.OnLog -= listener; }        // ← 同一轮同步摘除
...
return Success(new Dictionary<string, object> { { "result", result }, { "output", output }, { "executionTime", executionTimeMs } });
```

吐槽板说「Roslyn 编译回显已经在返回里了，说明有管道」——**这句是对的**，`output` 字段确实存在（`:242`）。所以它提出的「或至少提供 `execute_csharp_code` 返回 console output」这条建议，**已经实现了**。真正的问题在下面。

### 3. 捕获管道为什么结构上抓不到 `Verse.Log.*`

三段源码串起来看：

**(a) `ExplorerStandalone.OnLog` 只有两个来源。**
`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/Loader/Standalone/ExplorerStandalone.cs:24,43-45` 定义事件；`src/ExplorerCore.cs:105-127` 是唯一的触发点：

```csharp
private static void Log(object message, LogType logType)
{
    string log = message?.ToString() ?? "";
    LogPanel.Log(log, logType);
    switch (logType) { case LogType.Log: Loader.OnLogMessage(log); ... }
}
```

上游两个入口：UE 自己的 `ExplorerCore.Log/LogWarning/LogError`（`:88-95`），以及 `LogUnity`（`:97-103`）。

**(b) `LogUnity` 由全局 Unity 日志钩子驱动，且带一道开关。**
`src/Runtime/MonoHelper.cs:9-13`：`Application.logMessageReceived += ... => ExplorerCore.LogUnity(condition, type);`
`src/ExplorerCore.cs:97-103`：

```csharp
public static void LogUnity(object message, LogType logType)
{
    if (!ConfigManager.Log_Unity_Debug.Value) return;
    Log($"[Unity] {message}", logType);
}
```

开关默认 `false`（`src/Config/ConfigManager.cs:119-121`）。

**(c) 但本机实测环境里这道开关是开的。**
`UE_Data/sinai-dev-UnityExplorer/config.cfg:11`：

```
"Log Unity Debug" = "True"
```

旁证：`UE_Data/sinai-dev-UnityExplorer/Logs/UnityExplorer 2026-10-01 155526Z.txt:5-17` 里每条 UE 日志都成对出现两次 ——

```
5:  [Unity] [UELoader] UnityExplorer initialized. ...
7:  [UniverseLib] Initialized Legacy Input support
8:  [Unity] [UnityExplorer] [UniverseLib] Initialized Legacy Input support
```

`[Unity] ` 前缀只由 `ExplorerCore.LogUnity` 加（`ExplorerCore.cs:102`），`[UnityExplorer] ` 前缀只由本项目的 `Source/UELoader/ExplorerBootstrap.cs:195-218` 的 `OnUELog` 加：

```csharp
private static void OnUELog(string message, LogType logType)
{
    switch (logType) {
        case LogType.Error: case LogType.Exception: case LogType.Assert:
            Log.Error($"[UnityExplorer] {message}"); break;
        case LogType.Warning: Log.Warning($"[UnityExplorer] {message}"); break;
        default: Log.Message($"[UnityExplorer] {message}"); break;
    }
}
```

**这同时证实了本项目存在一条 `Verse.Log` → `Debug.Log` → UE 钩子 → `OnUELog` → `Verse.Log` 的回灌环路**（这正是吐槽板 [15] 记录的那个现象的成因）。

**结论**：在 `Log Unity Debug=True` 下，`Verse.Log.Message(...)` 确实会经过 `ExplorerStandalone.OnLog` 被 `ExecuteCode` 的 listener 收到 —— **但时序上晚了一步**。

`ExecuteCode` 的 `finally`（`UEHttpHandler.cs:207-210`）在 `ExecuteOnMainThread` 返回后立刻退订，而 `ExecuteOnMainThread` 是同步阻塞的，返回即代表主线程闭包跑完。`Verse.Log.Message` 只是**入队**，由 `Log.Update()` 在**后续帧**才 drain 成 `Debug.Log`。退订发生在 drain 之前 ⇒ **无论开关怎么配，`output` 字段都抓不到用户代码写的 `Verse.Log.*` / `Debug.Log`。**

这是本条唯一有完整源码链条的根因，与开关默认值无关。

### 4. 「Player.log 0 命中」这一半无法核实（判据被污染）

调用确实发生过：`MCP/logs/mcp-20261001-093829.log:431`

```
[14:50:08.077] <INFO> tools/call agg_call_tool args={args={"code":"Verse.Log.Message(\"W1P01_PROBE_MARKER_ALPHA\");\nvar asm = typeof(Flig…, tool=execute_csharp_code} → 1960ms success
```

但同一份日志显示，**探针前后游戏正在连续 60 秒 GABP 超时**：

```
[14:39:52.377] GABP 请求超时计数 2/3（rimworld/execute_debug_action）
[14:39:52.378] …execute_debug_action → 60013ms error(GABP_ERROR)
[14:41:53.366] …execute_debug_action → 60074ms error(GABP_ERROR)
[14:44:26.943] GABP 请求超时计数 1/3（rimworld/execute_debug_action）
[14:52:18.578] …execute_debug_action → 60022ms error(GABP_ERROR)
```

探针（14:50:08）正好夹在 14:44:26 与 14:52:18 两次 60 秒超时之间。而 RBS 的 `execute_debug_action` 是要走主线程的 —— 它连续 60 秒不返回，说明主线程当时在挣扎。

在这个前提下，`Verse.Log.Message` 的入队消息很可能**还没被 `Log.Update()` drain**（ drain 靠主线程每帧跑一次），grep 自然 0 命中。吐槽板 [01] 自己也记录了「`Player.log` 6 分钟零写入」。

另外两条无法排除的干扰项（本报告不替维护者判定，只列出）：
- **Player.log 轮转**（同板 [09]）：启动即轮转到 `Player-prev.log`，grep 错文件同样 0 命中。
- **同帧 grep**：`Log.Message` 到 `Player.log` 落盘之间至少隔一帧；紧跟着调用就去 grep 也会 0 命中。

**因此「Player.log 0 命中」这一观察我判为「无法核实」**，最可能的解释是主线程卡死导致队列未 drain（**标注为假设，无代码证据**）。我在本仓库内找不到任何能让 `Verse.Log.Message` 在正常帧下**永久**丢失的代码路径；`Source/UELoader/UEHttpLog.cs:58-66` 的桥接在主线程就绪时也是走 `Verse.Log.*` 入队，语义与用户代码一致。

### 5. `execute_csharp_code` 在做「日志兜底」这件事上还有第二重障碍

`MCP/index.js:4531` 的 `requireStage([STAGES.IN_GAME], …)` 要求已在游戏中；更关键的是 —— 当 GABP 正在超时（也就是 [01] 发生的场景），游戏主线程本身就不健康，此时无论走日志还是走返回值都不可靠。**这两条必须一起修才有效**，单修 [02] 不能救 [01]。

## 影响与严重度复评

**复评：单看是 🟠 中；与 [01] 叠加后是 🔴 高。**

- **单看 [02]：不是「事实相反」，是「没有事实」。** 拿不到日志的正确反应是承认「我无法自证」，而不是得出错误结论。严格按 AGENT-BRIEF §5 的尺子，这不够 🔴。原判 🔴 有一半建立在已被污染的「Player.log 0 命中」上。
- **叠加 [01] 后确实到 🔴**：GABP 说「成功」（实际没跑）→ 改用 `execute_csharp_code` → 返回值里 `output` 为空 → 自动化把「跑了但没输出」记成 PASS。**此时「拿不到日志」本身变成了假成功的帮凶**：它让人无法发现 [01]。这正是吐槽板第 286-289 行的论证，逻辑成立。
- **不阻塞、不崩、不会误导单看**：拿到 `output:null` 的调用方不会误以为代码没跑（`result` / `executionTime` 仍在）。所以它不是 🔴 独立缺陷。
- **修起来便宜**：改动全部落在 `Source/UELoader/UEHttpHandler.cs` 一个函数里，无需改 vanilla、无需改 RBS。

**给 PLAN 的一句话**：范围应当收窄为「让 `execute_csharp_code` 的 `output` 能覆盖用户代码的日志」，**而不是**「把日志写进 Player.log」—— 后者是 vanilla 既有行为，本项目不该去接管。

## 责任归属

**本项目（`Source/UELoader/UEHttpHandler.cs`）。**

- 依据：捕获窗口由 `UEHttpHandler.cs:207-210` 的 `finally` 决定，`Verse.Log` 的排队语义决定了它必然错过。这段代码在本仓库内，改它不需要碰 vanilla、不需要碰 RBS、不需要碰 UE 上游。
- **不是 vanilla 的锅**：`Verse.Log.Message` 入队 + `Log.Update()` 每帧 drain 是 vanilla 的正常设计，正是它让日志不炸游戏。UE 上游 `ExplorerCore.LogUnity` 的 `Log_Unity_Debug` 默认 `false` 也是合理默认。本机把它打开（`config.cfg:11`）是调试配置，不是缺陷。
- **不是 RBS 的锅**：本条走的是本项目自有的 `/unityexplorer/console/execute`，与 GABP 无关。
- 附带责任（本项目，但归属 [15]）：`Source/UELoader/ExplorerBootstrap.cs:195-218` 的 `OnUELog` 把 UE 日志二次包装成 `[UnityExplorer] <原文>` 回灌 `Verse.Log`，形成环路。这条**不属于本条修复范围**，但 PLAN 里必须知道它 —— 任何「扩大日志捕获」的改法都会撞上这个环路，有日志风暴风险（`MCP/index.js:4469` 记录过实测 1 分钟 63.5MB 的同类事故）。

## 修的话怎么修

方向（实现交给 PLAN 角色）：

1. **拉长捕获窗口，覆盖排队 drain —— `Source/UELoader/UEHttpHandler.cs:185-210`（主修）**
   现在是「派发 → 同步返回 → 立刻退订」。改法方向：主线程闭包跑完后**再等 N 帧**（N=1~2，条件式等待而非硬编码 sleep）再摘 listener，把这段 drain 期间收到的行并进 `captured`。风险点：`Verse.Log` 的 drain 依赖主线程仍健康，若主线程已卡死则退化成现有的 `ExecuteOnMainThread` 20 秒超时路径 —— 这正是 [01] 的场景，PLAN 要明确「等不到就照常超时返回，不要无限等」。

2. **在返回里显式声明捕获范围 —— `Source/UELoader/UEHttpHandler.cs:239-244`（必做，成本最低）**
   `Success(...)` 里加一个字段（如 `logCapture: { source: 'unityexplorer', coversUnityDebugLogs: false }`），并在 `MCP/index.js:2937` 的 `execute_csharp_code` notes 里写明：`output` 只含 UnityExplorer 自身 REPL 输出与编译诊断，**用户代码里写的 `Verse.Log.*` / `Debug.Log` 不在其中**。即便第 1 条因风险被否，光这一条也能把「静默失真」变成「明写边界」，成本近乎为零。

3. **兜底通道改成「返回字符串」而不是「写日志」—— `MCP/index.js:4530-4544`（推荐给 PLAN 评估）**
   吐槽板提的「独立缓冲 + `read_rimworld_log` 对应读取口」成本偏高（要新增端点 + 状态管理）。更轻的等价做法：在 `execute_csharp_code` 的入参上支持一个约定的返回值捕获方式（例如要求代码用 `return sb.ToString();` 把结果塞进 `result`，这条现状已可用，见日志里大量 `→ success` 且带返回值的调用），并在 notes 里把它写成**推荐做法**。这样 [01] 的兜底场景有明确指引，不依赖日志通道。

4. **明确不做**：不要为了「让日志落 Player.log」去改 `OnUELog` 的回灌行为或默认打开/关闭 `Log_Unity_Debug`。前者是 [15] 的范围且有环路风险，后者是 UE 上游的合理默认。

5. **验证方式建议**（PLAN 需要，但**不要现在做**）：起一个干净存档进图 → `execute_csharp_code` 写 `Verse.Log.Message("<marker>"); return 1;` → 断言返回值 `result === 1` 且 `output` 含 marker → 再 grep `Player.log` 确认 vanilla 落盘仍在。**分两步断言，是为了把本报告无法核实的「Player.log 那半」一次性钉死或证伪。**

## 是否已被现有计划覆盖

**否（且必须显式指出：该计划不会顺带修好它）。**

- `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md` 全文 12 个任务，逐节核过：任务 8（`ConsoleController` 接线，第 1450 行起）中第 1506-1528 行的新 `Evaluate` 实现**原样保留了** `ExplorerCore.Log($"Invoked REPL, result: {output}")` 的写法，**没有动 `UEHttpHandler.ExecuteCode` 的 listener 生命周期**，也没提 `Verse.Log` / `Player.log` / `output` 捕获。
- 全文 grep `OnLog` / `Player.log` / `Log.Message` / `console/execute`：**零命中**（`Log.Message` 的命中是 UELoader 侧的日志工具，与本条无关）。
- 任务 12 的验收矩阵（第 2069-2084 行）14 条用例全部只验编译/求值，**没有一条验日志捕获** —— 升级后本条会原样留存。
- `docs/issue-triage/plans/` 目录**当前不存在**；同角色产物只有 `triage/06-debug-action-path-format.md`，内容与本条无关。
- **给 PLAN 的提醒**：本条与 `2026-09-27` 计划的**任务 8** 改的是同一个文件（`ConsoleController`），若两条计划并行实施会产生编辑冲突，建议排在任务 8 之后或在计划里标注依赖。

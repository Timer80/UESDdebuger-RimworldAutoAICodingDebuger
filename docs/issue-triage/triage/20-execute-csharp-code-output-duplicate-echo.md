裁决：值得修
归并：并入计划 02 的 **§0.5 范围外（追加 2 条）+ 任务 1 步骤 2 的用例 2 + 任务 4 文案 + 新增任务 5/6**（理由：本条「重复副本」这一半**已经被计划 02 的去重规则覆盖**（`UEExecLogCapture.AddUnityExplorerLog` 丢弃 `[Unity] ` 行、`AddUnityLog` 丢弃 `[UnityExplorer] `/`[UELoader] `/`[UEHttp] ` 行，离线用例 4 已钉死）；真正没被覆盖的只有三小块——①「哨兵彻底不进 `output`」与计划 02 §0.6 的显式选择相反；②`includeOutput:false` 投影开关（改 `MCP/index.js:2073-2082`/`:4539`，**完全在计划 02 §4 改动清单之外**）；③单条日志的**字节**上限（计划 02 的 `MaxEntries=512` 只管条数不管字节，是真实盲区）。另立计划会与计划 02 在同一个工具、同一份响应语义上打架。）

---

一句话结论：`[20]` 的**方向对、归属层错**——重复是真的，但重复发生在 **UnityExplorer 控制台自己的固定尾行**（`Invoked REPL (no return value)` 原文 + `[Unity] [UnityExplorer] ` 回声，各 1 份），**不是**那 69 行用户探针；69 行探针在 `data.output` 里**只出现 1 遍**（且只有第 1 行带 `[Unity] ` 前缀，因为它是一条多行日志）。条目正文写的「每行都出现两次」描述的是 **`Player.log` 里的两份**，位置被记成了 `data.output`。计划 02 落地后两份会变一份，故本条并入，不单立。

---

## 1. 核实过程

### 1.1 吐槽板原文（行号用 `[System.IO.File]::ReadAllLines` 于本次快照复核）

`docs/mcp-吐槽板.md`，标题锚点 `^## \[20\]`（正文起 **41** 行，状态行 **71** 行，索引行 **37** 行）：

- 症状（**41-54** 行）：「用 `execute_csharp_code` 跑一段 `foreach` 遍历地图 pawn、`Log.Message` 一行一条的探针（本轮 69 行），返回的 `data.output` 里 **每行都出现两次**」+ 代码块：
  ```
  [Unity] [T0PROBE-BASE] mapIndex=0 tick=397 total=69
  [T0PROBE-BASE] mapIndex=0 tick=397 total=69
  [Unity] [T0PROBE-BASE] idx=0 def=Human id=94 …
  [T0PROBE-BASE] idx=0 def=Human id=94 …
  …（69 行 ×2）
  [UnityExplorer] [UnityExplorer] Invoked REPL (no return value)
  Invoked REPL (no return value)
  ```
- 顺带观察（**57-58** 行）：「`Verse.Log.Message` 的正文**确实**落到了 `Player.log`」
- 建议（**66-69** 行）：① 探针自己限行数，**或让 `execute_csharp_code` 支持「只回传 `result` 不回传 `output`」的开关**；② 「至少把结尾那句 `Invoked REPL (no return value)` 与 `[UnityExplorer] ` 前缀去掉」

> ⚠️ 行号纪律：内置 `read` 对本文件**少报约 21 行**（实测：同一段正文 `read` 报 121 / `ReadAllLines` 报 142）。本报告所有吐槽板行号均以 `ReadAllLines` 快照为准；条目定位一律用标题锚点。**板子仍在被改写，复核时 [18] 段已从 115 漂到 136、[02] 段从 549 漂到 570。**

### 1.2 先说清楚 `data.output` 的唯一来源（读源码，非转述）

- `Source/UELoader/UEHttpHandler.cs:171-179`：`captured` 列表**只**由 `ExplorerStandalone.OnLog` 喂（`listener = (m,t) => captured.Add(m)`）。
- `Source/UELoader/UEHttpHandler.cs:215-232`：把 `captured` 解析成 `result` / `errorMsg` / `outputLines`，`:232` `string.Join("\n", …)`。
- ⇒ **`data.output` 的每一个元素 = 一次 `ExplorerCore.Log` 调用**。想确知它到底有几份，只需数 `ExplorerCore.Log` 被调了几次。

### 1.3 证据源 A：UnityExplorer 自己的日志文件 = OnLog 事件的权威流水

`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/ExplorerCore.cs:105-127`：

```csharp
private static void Log(object message, LogType logType)
{
    string log = message?.ToString() ?? "";
    LogPanel.Log(log, logType);                 // ← 先写 UE 日志文件
    switch (logType) { … case LogType.Log: Loader.OnLogMessage(log); … }   // ← 再触发 OnLog
}
```

`src/UI/Panels/LogPanel.cs:81-90`（第 **86** 行 `File.AppendAllText(CurrentStreamPath, '\n' + message);`）——**每次 `Log` 调用都原样追加一次**。

⇒ **`UE_Data\sinai-dev-UnityExplorer\Logs\UnityExplorer 2026-10-01 200042Z.txt` 就是那一次 `execute_csharp_code` 的 OnLog 事件逐条流水。**

该文件实测（1-based 行号）：

| 行 | 内容 | 对应 OnLog 事件 |
|---|---|---|
| **18-19** | `Unable to compile the code. …` + 续行 | ① 原文（Warning） |
| **20-21** | `[Unity] [UnityExplorer] Unable to compile the code. …` + 续行 | ② 回声 |
| **22-91** | `[Unity] [T0PROBE-BASE] mapIndex=0 tick=397 total=69` + **69 行续行（无前缀）** | ③ **唯一一个** |
| **92** | （空行） | — |
| **93** | `Invoked REPL (no return value)` | ④ 原文 |
| **94** | `[Unity] [UnityExplorer] Invoked REPL (no return value)` | ⑤ 回声 |

**⇒ 用户探针 = 1 个 OnLog 事件（70 行，`[Unity] ` 只在第 1 行）；哨兵 = 2 个 OnLog 事件。**

### 1.4 证据源 B：`Player.log`（只读共享打开，未调用任何改状态的 MCP 工具）

路径 `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log`。游戏正在运行且占用该文件，`[System.IO.File]::ReadAllLines` 会抛 `IOException`，改用 `Select-String` / `Get-Content`（共享读）成功。本次快照全文 **12604 行**。

| 行 | 内容 | 栈（紧随其后） |
|---|---|---|
| **2724-2813** | `[UnityExplorer] [Unity] [T0PROBE-BASE] mapIndex=0 tick=397 total=69` + 69 续行 | 2801 `ExplorerCore:Log (object,LogType)` ← 2802 `ExplorerCore:LogUnity` ← 2803 `MonoHelper:Application_logMessageReceived` ← 2804 `Application:CallLogCallback` ← 2807 `Debug:Log` ← 2808 `Verse.Log.Message_Patch1` ← 2809 `<InteractiveExpressionClass 4>:Host4` ← 2810 `ConsoleController:Evaluate` ← 2811 `UEHttpHandler…<ExecuteCode>b__1`；2902 出现 `ExplorerBootstrap:OnUELog (… ExplorerBootstrap.cs)` |
| **2815-2895** | `[T0PROBE-BASE] mapIndex=0 tick=397 total=69` + 69 续行（**原文**） | 2887 `Debug:Log` ← 2889 `Verse.Log.Message_Patch1` ← 2890 `<InteractiveExpressionClass 4>:Host4` ← 2891 `ConsoleController:Evaluate` ← 2892 `UEHttpHandler…<ExecuteCode>b__1`；2895 `(Filename: …/UEHttpHandler.cs Line: 192)` |
| **2896** | `[UnityExplorer] [Unity] [UnityExplorer] Invoked REPL (no return value)` | 2902 `ExplorerBootstrap:OnUELog` ← 2904 `ExplorerCore:Log` ← 2905 `LogUnity` ← 2906 `MonoHelper` ← 2907 `CallLogCallback` ← 2910 `Debug:Log` ← 2911 `Verse.Log.Message_Patch1` ← 2912 `ExplorerBootstrap:OnUELog` ← 2914 `ExplorerCore:Log` ← 2915 `ExplorerCore:Log (object)` ← 2916 `ConsoleController:Evaluate` |
| **2921** | `[UnityExplorer] Invoked REPL (no return value)` | 2926 `Debug:Log` ← 2927 `ExplorerBootstrap:OnUELog` ← 2929 `ExplorerCore:Log` ← 2930 `ExplorerCore:Log (object)` |

全文计数（搜索范围 = 本次快照全部 12604 行）：

- `T0PROBE-BASE` 命中 **140** 行 = **70 × 2**（`[UnityExplorer] [Unity] ` 块 + 原文块），且 `mapIndex=0 tick=397 total=69` 精确出现在 **2724** 与 **2815** 两行。
- `Invoked REPL (no return value)` 命中 **10** 行 = **5 组 × 2**（2896/2921、4452/4477、4946/4971、11047/11072、11129/11154）—— 5 组对应 5 次 `execute_csharp_code`（其中一次是编译失败）。

### 1.5 逐层核对源码：重复是怎么来的（`ExplorerBootstrap` 在其中的角色）

| 层 | 位置 | 行为 |
|---|---|---|
| ① 用户代码 | `UEHttpHandler.cs:192` `ConsoleController.Evaluate(code, false)` | 探针里 `Verse.Log.Message(sb.ToString())` |
| ② vanilla | `Verse.Log.Message_Patch1` | 同调用栈 `Debug.Log(text)`（计划 02 §0.2 反编译结论，与此处栈一致） |
| ③ Unity | `Application.CallLogCallback` | 派发 `logMessageReceived` |
| ④ UE 上游 | `src/Runtime/MonoHelper.cs:9,12-13` | → `ExplorerCore.LogUnity` |
| ⑤ UE 上游 | `src/ExplorerCore.cs:97-103`（`:102` 加 `[Unity] ` 前缀；`:99-100` 受 `ConfigManager.Log_Unity_Debug` 门控） | → `Log("[Unity] " + message)` → **OnLog 事件** ⇒ 被 `captured` 收走 |
| ⑥ UE 上游 | `src/ExplorerCore.cs:105-127` → `src/Loader/Standalone/ExplorerStandalone.cs:43-45` `Loader.OnLogMessage → OnLog?.Invoke` | `captured` 的唯一入口 |
| ⑦ **本项目** | `Source/UELoader/ExplorerBootstrap.cs:86` `ExplorerStandalone.CreateInstance(OnUELog, modulesPath)` | UE 的每一条日志都回调本模块 |
| ⑧ **本项目** | `ExplorerBootstrap.cs:195-218`，`:204` `Log.Error` / `:207` `Log.Warning` / `:210` `Log.Message`，一律加 `[UnityExplorer] ` 前缀 | **把同一条消息用 `Verse.Log` 再写一遍** ⇒ 又一次 `Debug.Log` |

**关键实测事实：环路长度恒为 1，不发散。**
`Player.log` 里每条消息恰好两份，**不存在第三份 `[UnityExplorer] [Unity] [UnityExplorer] …`**（搜索范围：全文 12604 行，见 §1.4 计数）。
⇒ 推断：**Unity 不会在日志回调内部再次派发 `Application.logMessageReceived`**。
⚠️ 这条是**从观测反推**的结论，不是读 Unity 源码读到的，标注为**基于证据的推断**。它能被观测直接证伪：`Player.log:2896` 那条的栈里确实多了一层 `OnUELog ← ExplorerCore:Log ← LogUnity`，但那层是在**回调之外**（见 §1.7），不构成反例。

**这条推断直接解释了两个看似矛盾的现象：**

- 用户探针 X：`Debug.Log(X)` 是顶层 → 回调跑 → `LogUnity(X)` → `[Unity] X`（**OnLog 事件 1**）→ `OnUELog([Unity] X)` → `Log.Message("[UnityExplorer] [Unity] X")` → `Debug.Log` **在回调内 ⇒ 不再派发** ⇒ **没有第 3 层**。
- 哨兵 M0：`ExplorerCore.Log(M0)`（`ConsoleController.cs:185`）是**直接调用**，不经过 `Debug.Log`，所以此刻**不在日志回调里** → `OnUELog(M0)` → `Log.Message("[UnityExplorer] "+M0)` = M1 → `Debug.Log(M1)` 是**顶层** ⇒ 回调跑 ⇒ `LogUnity(M1)` → `[Unity] [UnityExplorer] …` = M2（**OnLog 事件 2**）→ `OnUELog(M2)` → M3 → `Debug.Log(M3)` 在回调内 ⇒ 停止。

⇒ **`data.output` 实测形状应为：**
```
[Unity] [T0PROBE-BASE] mapIndex=0 tick=397 total=69
[T0PROBE-BASE] idx=0 …
…（共 70 行一条，只第 1 行带前缀）
Invoked REPL (no return value)
[Unity] [UnityExplorer] Invoked REPL (no return value)
```
**用户探针 1 份；哨兵 2 份。**

### 1.6 「≈12KB」这个数字不能当判据

逐行拼接 `Player.log:2815-2884` 那 70 行实测：**6997 字符 / 7351 UTF-8 字节**（平均 100 字符/行）。
- 1 份 ≈ **7.0 KB**
- 2 份 ≈ **14.0 KB**
- 条目写「≈ 12KB」——**既不精确落在 1 份也不落在 2 份上**，不能用来支持任何一种次数假设。（标注：这是我对其数字来源**无法核实**的部分。）

### 1.7 核实 2：`Invoked REPL (no return value)` 的出处与变体

**出处**：`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ConsoleController.cs:185`
`ExplorerCore.Log($"Invoked REPL (no return value)");`

⚠️ **派单里写的 `McpRimDebug/ScriptEvaluator.cs`、`McpRimDebug/ConsoleController.cs` 在本仓库不存在。**
搜索范围：`glob **/McpRimDebug/**/*.cs` → 82 个文件（`RemotingShim.cs` / `DebugSession*.cs` / `ExpressionEvaluator.cs` / `SdbClient\*` …），**无 `ScriptEvaluator.cs`、无 `ConsoleController.cs`**。
实际位置：`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ScriptEvaluator.cs`（`:1` `using Mono.CSharp;`，`:8` `class ScriptEvaluator : Evaluator`）与同目录 `ConsoleController.cs`。

**变体清单（6 个，不是 2 个）** —— `Evaluate(string input, bool supressLog = false)`，`ConsoleController.cs:158-218`；`UEHttpHandler.cs:192` 传的是 `false`，所以 6 条全部会发：

| # | 触发条件 | 原文 | 源码位置 | LogType |
|---|---|---|---|---|
| 1 | REPL 有返回值 | `Invoked REPL, result: {ret}` | `:183` | Log |
| 2 | REPL 无返回值 | `Invoked REPL (no return value)` | `:185` | Log |
| 3 | 非 REPL（using / 类定义）且无错 | `Code compiled without errors.` | `:205`（`!supressLog`） | Log |
| 4 | 编译失败 | `Unable to compile the code. Evaluator's last output was:\r\n{…}` | `:203` throw → `:211` `LogWarning` | Warning |
| 5 | REPL 运行抛异常 | `Exception invoking REPL: {ex}` | `:189` | Warning |
| 6 | 其它未捕获异常 | `ex.ToString()` | `:216` | Warning |

⇒ **变体 1-5 每一条在 `data.output` 里都会出现两次**（原文 + `[Unity] [UnityExplorer] ` 回声，理由见 §1.5）。变体 3 只在「代码不是 REPL」时出现。

**关于计划 01 那条待核对推测（「记 [01] 的人可能把 MCP 信封的 `success:true` 误读成嵌套的 `operation.Success:true`」）—— 本条不需要同类核对。**
理由：`Invoked REPL (no return value)` 是**逐字落进两个磁盘文件的硬编码字符串**（`UE_Data\…200042Z.txt:93` 与 `Player.log:2921`/`2896` 均实测命中），不是从任何回包字段推断出来的，也与 MCP 信封无关——它产生于游戏侧 `ConsoleController.cs:185`，在 MCP 之前。
**本条真正该警惕的不是「读错字段」，而是「同一条控制台消息会被本项目的 `OnUELog` 回灌一遍」**（§1.5 第 ⑦⑧ 层）。

### 1.8 核实 3：「只回 result 不回 output」开关 —— **可行，且只需改 MCP 一侧**

| 位置（`MCP/index.js`，5460 行，`ReadAllLines` 复核） | 现状 | 可行改法 |
|---|---|---|
| `:2073-2082` | `inputSchema: { properties: { code }, required: ['code'] }` | 加一个可选 boolean（如 `includeOutput`），**纯声明性** |
| `:4530-4544` | `:4532-4533` POST `{ code: args.code }`；`:4536-4538` 编译失败加 `guidance`；`:4539` `return r;`；`:4542` `JSON.stringify(result, null, 2)` | 在 `:4539` 之前 `if (args.includeOutput === false) { delete r.output; delete r.logCapture; }` ⇒ **零游戏侧改动、不重建 DLL、不改 HTTP 协议** |
| `:3817` | `execute_csharp_code: { script:'code', expression:'code' }`（别名归一表） | 新参数**无需**进别名表 |

**走游戏侧也能做，但成本高一个量级**：`Source/UELoader/UEHttpServer.cs:391-393`（`UEHttpHandler.ExecuteCode(GetString(body,"code"))`）要多读一个 body 字段 + `UEHttpHandler.ExecuteCode` 加参数 + `dotnet build Source/UELoader/UELoader.csproj` + **停游戏后**重投 `Assemblies/UELoader.dll`（`UELoader.csproj:92-96` 的 `DeployToAssemblies` 目标会因文件占用失败）。
⇒ **建议只做 MCP 侧。**

**与计划 02 任务 4 的关系**：计划 02 任务 4 只改 `MCP/index.js:2937` 的 `TOOL_USAGE_NOTES` 单行（计划 `:876-889`），其 §4「改动清单」**不含** `:2073-2082` 与 `:4530-4544` ⇒ 这个开关是**计划 02 范围外的缺口**，不能算「已被覆盖」。

---

## 2. 影响与严重度复评

**复评：🟠 中 → 🟡 低-中。维持值得修，但理由必须换。**

- **降级理由一：重复的范围比条目写的小得多。** 真正出现 2 份的只有**结尾那一条控制台哨兵**（外加编译失败/异常类消息），69 行探针**没有被复制**（§1.3）。不构成「系统性浪费」。
- **降级理由二：不产生「事实相反的结论」。** 重复的是控制台自己的固定文案，不是判据；按 AGENT-BRIEF §5 的尺子够不上 🔴。
- **条目真正说对、且仍然成立的那条痛点：单次调用的上下文税。** 实测单条 70 行日志 ≈ **7.0 KB**，**没有任何上限**。
  ⚠️ **这是计划 02 的一个真实盲区**：`UEExecLogCapture.MaxEntries = 512`（计划 `:418`）限的是**条数**，一条多行日志可以是任意字节数。512 条 × 7KB ≈ 3.5 MB 上界，**对 [20] 描述的场景零约束**。
- **叠加计划 02 后可降到 🟡 低**：去重规则落地 → 两份变一份；`includeOutput:false` 落地 → 大探针可整个不回 `output`；文案引导「要结果走 `result`」→ 调用方有正路可走。

---

## 3. 责任归属

| 现象 | 归属 |
|---|---|
| 第二份副本（`[UnityExplorer] [Unity] …` 写回 `Player.log`） | **本项目** —— `Source/UELoader/ExplorerBootstrap.cs:195-218`（`:204/:207/:210`） |
| `[Unity] ` 前缀 | **UnityExplorer 上游** —— `src/ExplorerCore.cs:102`。**不该改**（也不该改 `UE_Data\…\config.cfg:11` 的 `Log Unity Debug`） |
| 「每条控制台消息必然多出一份」这个**结构性**成因 | **本项目，属设计使然**（[15] 的范围）。为了让 [20] 而拆掉这个桥会直接破坏 [15]，**禁止** |
| `data.output` 没有投影开关 / 没有字节限幅 | **本项目 MCP** —— `MCP/index.js:2073-2082`、`MCP/index.js:4530-4544` |
| 条目把 `Player.log` 的两份记成 `data.output` 的两份 | **纯文档**（吐槽板正文需改，属主代理的批注职责） |

**不是 RBS 的锅**（本条走本项目自有 `/unityexplorer/console/execute`）。**`GABS-main/`（Go）未被用作任何证据。**

---

## 4. 修的话怎么修

按「改动落在哪个文件」给方向，不写实现：

1. **哨兵去重 —— 无需新代码。**
   `Source/UELoader/UEExecLogCapture.cs`（计划 02 任务 1 步骤 4 新建）的 `AddUnityExplorerLog`（计划 `:449-460`）已丢弃 `[Unity] ` 开头行（`:453`），离线用例 4（计划 `:245-251`）已钉死「去重后 output 恰为 M / 只算 1 条」。**本条不需要新增实现。**

2. **哨兵彻底不进 `output`（条目建议 2）—— 计划 02 目前明确不做。**
   计划 02 §0.6（计划 `:100-108`）写的是「本计划**保持现状行为**（两个哨兵都照旧进 `output`）」，任务 1 步骤 2 用例 2（计划 `:228-237`）还把「哨兵仍在 output」钉成了断言。
   ⇒ **这是本条与计划 02 的唯一真实分歧点。** 若采纳，应在 `UEExecLogCapture.Parse` 加「控制台自有消息白名单 → 丢弃」，并**同步改掉用例 2 的两条断言**；
   🚫 **不要**写成 `StartsWith("Invoked REPL")` 这种只挡一条的硬编码——变体有 6 个（§1.7）。

3. **字节上限（计划 02 的盲区，建议新增任务 6）。**
   在 `UEExecLogCapture` 加 `MaxOutputBytes`（例如 64 KB），超限只截断并置 `logCapture.truncated = true`（复用计划 `:383-393` 已有的元数据形状）。
   **对环路零加边的性质不变**：观察者仍然只往内存写、不调用任何日志 API（计划 `:629-654`），§0.4 四条设计保证依然成立。

4. **`includeOutput:false` 开关（建议新增任务 5）。**
   `MCP/index.js:2073-2082` 加可选 boolean；`MCP/index.js:4539` 之前 `delete r.output`。**不碰游戏 DLL，不需要停游戏，不需要重新构建。**

5. **文案（计划 02 任务 4，`MCP/index.js:2937`）。**
   在计划 02 已有的新文案后追加两句：
   - `Invoked REPL (no return value)` / `Code compiled without errors.` 是**控制台自身的固定尾行，不是你代码的日志**，别当成判据；
   - `output` **不限字节**；要控制上下文用 `includeOutput:false`，要带数据回来用 `result`（`return sb.ToString();`）。

6. **不建议做的**：
   - 🚫 改 `ExplorerBootstrap.OnUELog` 的前缀或去重 → 破坏 [15]；
   - 🚫 改 `config.cfg` 的 `Log Unity Debug` → UE 上游的合理默认，改了也修不了别人的机器；
   - 🚫 任何形式的 `Verse.Log` / `Debug.Log` Harmony patch → `MCP/index.js:4461`（黑名单注释）与 `:4469`（实测事故文案：1 分钟 63.5MB + mono 原生崩溃）；
   - 🚫 「跨帧等 N 帧」扩大捕获窗口 → 计划 02 §0.2 已证该机制不存在，且正是唯一会真正放大环路的做法。

---

## 5. 与计划 02 的关系 + ②③ 两条协调结论

### 5.1 [20] ↔ 计划 02：**部分覆盖 + 三个小缺口**

**已覆盖（不用再写）：**

| [20] 的诉求 | 计划 02 的落点 |
|---|---|
| 「每行都出现两遍」/ 回声重复 | 计划 `:449-460` `AddUnityExplorerLog` 丢弃 `[Unity] `；`:433-446` `AddUnityLog` 丢弃 `[UnityExplorer] `/`[UELoader] `/`[UEHttp] `；离线用例 4（`:245-251`）钉死 |
| 一次调用 12KB 的上下文税 | 部分覆盖：去重后 70 行只留 1 份（≈7KB） |

**计划 02 是否「反而制造了这个问题」？—— 逐条核对后：不会。**

1. 计划 02 新增的 `Application.logMessageReceived` 观察者**只往内存列表写、绝不写任何日志**（计划 `:629-654` 的硬约束注释）⇒ **给环路零加边**，不会产生新的自激；
2. 闸门只在主线程闭包动态范围内开、`finally` 关（计划 `:692-718`），**不跨帧** ⇒ 不存在「多等几帧把回声灌进来」的可能；
3. 按 §1.5 推演，落地后 `output` = **70 行无前缀原文 + 1 条哨兵**，**两份变一份**；
4. `Log Unity Debug = False` 时 `LogUnity` 直接 return（`src/ExplorerCore.cs:99-100`），回声根本不产生 ⇒ 两种配置下 `output` 一致，计划 02 任务 3 步骤 5（计划 `:843-847`）的验收期望成立。

**未覆盖（三个小缺口，建议追加到计划 02）：**

| # | 缺口 | 锚点 |
|---|---|---|
| 1 | 哨兵要不要彻底从 `output` 里去掉（与计划 02 §0.6 的显式选择相反） | 计划 **`§0.5 范围外`**（计划 `:93-98`）追加 2 条 + **任务 1 步骤 2 用例 2**（计划 `:228-237`）改断言 |
| 2 | `includeOutput:false` 开关 | 计划 **§3 末尾新增任务 5**；代码在 `MCP/index.js:2073-2082` + `:4539`，**与计划 02 的 `:2937` 零行重叠，不构成编辑冲突**，但必须登记进 §4 改动清单并跑 `MCP/test/*.test.js` |
| 3 | 单条日志字节上限（`MaxEntries=512` 只管条数不管字节） | 计划 **§3 末尾新增任务 6** + `UEExecLogCapture` |

另：**计划 02 任务 4 的文案**（计划 `:876-889`）应追加 §4.5 那两句。

**为什么不单独立计划**：剩余产出全部落在**同一个工具**（`execute_csharp_code`）、**同一个响应形状**（`data.output`）上；另起计划会与计划 02 在同一处语义上打架（并与计划 12 在 `MCP/index.js:2937` 形成三方触碰）。

### 5.2 ② [02] 是否应再收窄：**是 —— 而且是「已证实落盘」级别的收窄**

**我独立复核的正面证据（不是转述 [20]）：**
`Player.log:2815-2895` 就是那 70 行探针**原文逐字落盘**，其栈为：

```
UnityEngine.Debug:Log (object)
Verse.Log.Message_Patch1 (string)
<InteractiveExpressionClass 4>:Host4 (object&)
UnityExplorer.CSConsole.ConsoleController:Evaluate (string,bool)
UELoader.UEHttpHandler/<>c__DisplayClass12_0:<ExecuteCode>b__1 () (…/UEHttpHandler.cs Line: 192)
UELoader.UEMainThreadDispatcher:Update () (…/UEMainThreadDispatcher.cs:147…)
```

⇒ **`Verse.Log.Message` 的正文确实落 `Player.log`。** [20] 正文（吐槽板 `:57-58`）这句话**为真**，且与计划 02 §0.2 的反编译结论（`Verse.Log.Message` 同调用栈直接 `Debug.Log`、无跨帧 drain）**方向一致**。triage 02 当时只能写「无法核实」（判据被 GABP 超时污染），现在应升级为**已证实**。

⇒ 索引行 `docs/mcp-吐槽板.md`（`ReadAllLines` 复核时第 **19** 行）：

```
| **[02]** | `execute_csharp_code` 里的 `Verse.Log.*` **不落 Player.log** —— 无法当取日志的兜底通道 | 🔴 高 | 待修 |
```

其**前提「不落盘」已被证伪**。建议改为：

```
| **[02]** | `execute_debug_action` 超时时唯一可用的取日志通道，但其 `output` 覆盖范围随 UE 的 `Log Unity Debug` 开关变化（UE 默认 `False` ⇒ 一条用户日志都没有） | 🟠 中 | 待修（计划 02） |
```

严重度 **🔴 → 🟠**（与 triage 02 自评「单看 🟠、叠加 [01] 后 🔴」一致——[01] 的 🔴 由计划 01 独立承担，不再挂在这里）。

⚠️ **收窄的边界，不要越界写**：本条证据只覆盖 **`Verse.Log.Message`（一次多行调用）**。`UnityEngine.Debug.Log` 直接调用必然落盘（Unity 原生），但**本轮无实测记录**；`Log.ErrorOnce` 另有 [15] 的「正文被 `Error_Patch2` 吞掉」（不同缺陷，不在 [02] 范围）。

### 5.3 ③ [18] 索引措辞：**是，与已证伪结论冲突**

索引行（`ReadAllLines` 复核时第 **36** 行）：

```
| **[18]** | 「日志条数」类判据在 UE 控制台下**会被回显翻倍**（每行两条），而 `execute_debug_action` 的 `logs[]` 数组**已经把回显去重了** ⇒ 同一个量在两个地方数字不同，容易被当成 bug | 🟡 低 | 待绕过 |
```

- **后半句已被 triage 18 §1.4 反编译证伪**：`LogJournal.ShouldCollapse` 要求 11 个字段 `string.Equals` 全等，两条副本的 `Message` 是 `[UnityExplorer] [Unity] X…` vs `X…`，**根本不是同一个字符串，永不折叠**。`[18]` 段的批注（`ReadAllLines` 复核时第 **163** 行）已经写明「已被源码证伪」——**但索引行没跟着改，仍在一字不差地传播被证伪的因果**。⇒ **冲突成立。**
- **前半句也不精确**：实测 `Player.log` 里是**两块各 70 行**（`[UnityExplorer] [Unity] ` 块 + 原文块），且每块**只有第 1 行带前缀**（§1.4），不是「每行两条」。

**建议措辞：**

```
| **[18]** | `Player.log` 里每条 UE 来源日志**成对出现两次**（本项目 `ExplorerBootstrap.OnUELog` 用 `Verse.Log` 写回的 `[UnityExplorer] …` 副本 + 原文；多行消息只有第 1 行带前缀），而 `execute_debug_action` 的 `logs[]` **不做**这层去重（`repeatCount` 是「2 秒内完全相同的日志折叠计数」，与回显无关）—— 计数前先按 `^[UnityExplorer]` 过滤 | 🟡 低 | 待绕过 |
```

**顺带一条对 triage 18 的加强**：triage 18 §1.5 当时是靠「读日志尾部」**观察**到 Player.log 成对，判据偏软；本次全文定位（**2724** vs **2815**，两条各自的栈分别指向 `OnUELog` 与 `ConsoleController.Evaluate`）把它升级为**确证**。

---

## 6. 本报告的搜索范围与「无法核实」清单

| 项 | 状态 |
|---|---|
| 吐槽板全文 884→（复核时）逐段标题锚点扫描 | 已做（`ReadAllLines` + 正则 `^## \[\d\d\]`） |
| `Player.log` 全文（12604 行快照） | 已做（`Select-String` 共享读；两次读取行数不同 ⇒ 文件在被游戏追加，后续行号以复核时快照为准） |
| `UE_Data\…\Logs\UnityExplorer 2026-10-01 200042Z.txt` 全文 439 行 | 已读（关键段） |
| `MCP/logs\*.log` 搜 `T0PROBE` | **零命中** ⇒ MCP 侧日志不记录响应载荷，**无法**直接引用 `data.output` 原文 |
| **无法核实**：`data.output` 的**逐字原文** | MCP 日志无载荷、且禁止用 `execute_csharp_code` 复现。§1.5 的形状是**由源码链路 + UE 日志流水 + Player.log 三方交叉推出**，标注为推断；需主代理在收尾的游戏内验收里用一次真实调用确认 |
| **无法核实**：条目写的「≈12KB」 | 实测 1 份 ≈7.0KB / 2 份 ≈14.0KB（§1.6），数字对不上，来源不明 |
| **推断（基于证据，非源码）**：Unity 不在日志回调里重入 | 由「Player.log 每条恰好 2 份、无第 3 份」反推（§1.5） |
| **未使用**：`GABS-main/`（Go 参考实现）、RBS DLL 反编译 | 本条不需要；RBS 只在 [18] 语境出现，已由 triage 18 覆盖 |

---

*行号复核：吐槽板与 `MCP/index.js` 用 `[System.IO.File]::ReadAllLines`；计划 02 用 `ReadAllLines`（928 行）；`Source/` 与 `Source/UE-4.9.0-official/` 的 `.cs` 行号用内置 `read`（该目录本轮未被并行会话改写）。`Player.log` 行号带"快照"限定。*
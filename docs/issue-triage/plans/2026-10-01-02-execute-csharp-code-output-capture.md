# `execute_csharp_code` 日志捕获 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让 `execute_csharp_code` 返回的 `output` 字段**稳定覆盖用户代码里写的日志**（`Verse.Log.*` / `UnityEngine.Debug.Log*`），不再依赖 UnityExplorer 的 `Log Unity Debug` 调试开关，也不再让日志带 `[Unity] ` 前缀污染。

**架构：** 在 UELoader 里新增一个**零 Unity/Verse 依赖**的纯逻辑类 `UEExecLogCapture`，负责「过滤 → 限流（条数+字节）→ 剔除控制台哨兵 → 解析成 result/output/error」。`UEHttpHandler` 侧新增一个**永久的 `Application.logMessageReceived` 观察者**（纯被动记录，**绝不写任何日志**），并用一个只在用户代码动态范围内开启的静态闸门把它接到本次执行上。UnityExplorer 自己的 `ExplorerStandalone.OnLog` 通道保留，但丢弃 `[Unity] ` 开头的重复回声。响应里新增 `logCapture` 字段，明写捕获来源、条数、字节数与截断状态。MCP 侧再加一个 `includeOutput:false` 投影开关，让大探针可以完全不要 `output`。

**技术栈：** C#（net472，`LangVersion 9.0`，SDK-style csproj）、Unity 2022.3.35f1（`Application.logMessageReceived`）、RimWorld 1.6.9676、测试用 net472 控制台工程 + 手写 `Check()` runner（沿用 `Source/HotReloadTests` / `Source/ConfigJsonTests` 约定）。

---

## 0. 前置事实（已核实，不要重新摸索）

### 0.1 现状调用链（行号已于 2026-10-01 逐条复核）

| 位置 | 事实 |
|---|---|
| `MCP/index.js:4530-4544` | `execute_csharp_code` → POST `/unityexplorer/console/execute`，参数只有 `code`（`:4532-4533`），`:4539` `return r;`，`:4542` `JSON.stringify` |
| `MCP/index.js:2073-2082` | `execute_csharp_code` 的 `inputSchema`：`properties.code`，`required: ['code']`。**新增 `includeOutput` 落在这里（任务 5）** |
| `MCP/index.js:3817` | 别名归一表 `execute_csharp_code: { script:'code', expression:'code' }` —— 只归一 `code` 的别名，新参数**不进**此表 |
| `MCP/test/toolchainFixes.test.js:13,121` | 测试用 `source = fs.readFileSync(indexPath,'utf-8')` 做源码切片断言；`:121` 的切片是 `function toolListLine(` → `function buildMenuNode(`，与本计划改动位置零交集 |
| `Source/UELoader/UEHttpServer.cs:391-393` | 路由 → `UEHttpHandler.ExecuteCode(GetString(body, "code"))` |
| `Source/UELoader/UEHttpHandler.cs:163-245` | 接收端 `ExecuteCode` |
| `Source/UELoader/UEHttpHandler.cs:171-179` | 建 `captured` 列表 → 订阅 `ExplorerStandalone.OnLog` |
| `Source/UELoader/UEHttpHandler.cs:185-201` | `UEMainThreadDispatcher.ExecuteOnMainThread(..., 20000)` 里调 `ConsoleController.Evaluate(code, false)` |
| `Source/UELoader/UEHttpHandler.cs:207-210` | `finally { ExplorerStandalone.OnLog -= listener; }` |
| `Source/UELoader/UEHttpHandler.cs:215-230` | 解析 `captured` → `result` / `errorMsg` / `outputLines` |
| `Source/UELoader/UEHttpHandler.cs:239-244` | `Success({ result, output, executionTime })` |
| `MCP/index.js:2937` | `execute_csharp_code` 的 `TOOL_USAGE_NOTES` 文案（管**「值怎么回传」的写法**） |
| `MCP/index.js:4536-4537` | `execute_csharp_code` case 内的**编译失败归因 `guidance`**（管**「编译报错怎么解释」**）。`:4536` 条件含 `r.error.includes('CS0')` ⇒ 对 `'CS0127'` **必然为真** ⇒ **本计划必须改它**，见 §0.1b |
| `MCP/index.js:2072 / :2078` | `execute_csharp_code` 的 `description` 与 `code` 示例（**归计划 12 任务 1 的改动面**，本计划只在 §0.6 的 Roslyn 切换清单里登记，不重复实施） |
| `MCP/index.js:741 / :743` | 慢档超时 `ueSlowRequestTimeoutMs ?? 10000`；`:4534` 用 `{ slow: true }` 走它 —— 与游戏侧 20s 主线程预算（`UEHttpHandler.cs:201`）错配，构成 [31] 静默重放的结构前提 |
| `MCP/index.js:812-835` | `callUnityExplorerAPI` 的 catch 块：`:816` 正则含 `ECONNRESET`、`:817` 连超时也重试、`:818-821` 复用同一 body 重发，无幂等判断 —— **[31] 的成因**，见任务 3 步骤 4 |

### 0.1b ⚠️ `:4536-4537` 的 guidance 会把 CS0127 误判成「缺 using」（2026-10-02 复核发现）

现状（`MCP/index.js:4536-4537`）：

```javascript
if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || (typeof r.error === 'string' && r.error.includes('CS0')))) {
  r.guidance = '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';
}
```

`'CS0127'.includes('CS0')` ⇒ **必然为真**。于是一个 `return "…";` 的用户（[34] 的场景）拿到的 guidance 是「**默认没有 using 引用，请用全限定名或 add_using_directive**」——

⇒ **比不修更糟**：它把用户的注意力从「宿主是 void、别写 `return` 表达式」这个真实原因，引向「using 缺失」这个完全无关且并不存在的维度，用户会去查一个没问题的方向。

**与 `:2937` 的分工**（两处都要改，各管一件事）：

| 行 | 管什么 | 修法 |
|---|---|---|
| `:2937`（`notes`） | **值怎么回传** —— 宿主是 `void`，不要写 `return 表达式`；末尾裸表达式才是值通道 | 任务 4 |
| `:4536-4537`（`guidance`） | **编译报错怎么归因** —— CS0127 必须被单独识别并指向「宿主 void / 别写 `return`」，不能落进 CS0x 兜底分支 | 新增任务 7 |

> ⚠️ **任务 7 只修 `CS0127` 这一个错误码。** `else if` 兜底分支对另外 8 个错误码（`CS0103`/`CS0234`/`CS0121`/`CS0117`/`CS1061`/`CS0411`/`CS1503`/`CS0841`）的误导向**本计划不修**，由 `docs/issue-triage/plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md` 接管（9 个错误码的路由表 + `:4536-4537` 接线）。两份计划可任意先后落地。若只执行本计划，**别以为归因问题已经全解决** —— 详见任务 7 开头的「范围边界」与步骤 6。

### 0.2 ⚠️ 上游裁决报告的两处错误（本计划已修正，**不要按裁决报告写**）

裁决报告 `docs/issue-triage/triage/02-execute-csharp-code-log-not-in-player-log.md` 说「`Verse.Log.*` 是排队到下一帧才 drain 的，退订早于 drain ⇒ 结构上抓不到」，并据此建议「主线程闭包跑完后再等 N 帧再摘 listener」。**这两点在本仓库实测的游戏版本上不成立**：

1. **`Verse.Log.Message` 在 1.6.9676 是同步写盘，没有跨帧 drain。**
   反编译 `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll` 的 `Verse.Log.Message(string)`：

   ```csharp
   public static void Message(string text)
   {
       lock (logLock)
       {
           if (!PreventLogging)
           {
               messageQueue.Enqueue(new LogMessage(LogMessageType.Message, text, ...), out var repeatsCapped);
               if (!repeatsCapped)
               {
                   Debug.Log(text);        // ← 同调用栈同步触发，不是下一帧
                   PostMessage();
               }
           }
       }
   }
   ```

   `messageQueue`（`Verse.LogMessageQueue`）只喂游戏内 `EditWindow_Log` 窗口；**`Verse.Log` 里根本不存在 `Update()` 之类的 drain 方法**。`Log.Warning` → `Debug.LogWarning`、`Log.Error` → `Debug.LogError`，同样同步。

   ⇒ **「等 N 帧」是针对一个不存在的机制**，而且它恰恰是本仓库里唯一会真正放大回灌环路的做法。**本计划取消该做法。**

2. **在 `Log Unity Debug = True` 下，用户日志今天就已经被捕获了**（只是被前缀污染）。
   `Debug.Log` 同步触发 `Application.logMessageReceived` → UE 的 `MonoHelper` 钩子（`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/Runtime/MonoHelper.cs:9,12-13`）→ `ExplorerCore.LogUnity`（`src/ExplorerCore.cs:97-103`，前置 `ConfigManager.Log_Unity_Debug`，默认 `false`，见 `src/Config/ConfigManager.cs:119-121`）→ `Log($"[Unity] {message}")`（`src/ExplorerCore.cs:102`）→ `Loader.OnLogMessage` → `ExplorerStandalone.OnLog`（`src/Loader/Standalone/ExplorerStandalone.cs:24,43-45`）→ **`UEHttpHandler` 的临时 listener**。
   本机 `UE_Data/sinai-dev-UnityExplorer/config.cfg:11` 是 `"Log Unity Debug" = "True"`，所以 `output` 里现在能看到的是 `[Unity] W1P01_PROBE_MARKER_ALPHA`（带前缀），而不是原文。

   ⇒ **真正的缺陷是「可移植性」**：任何用 UE 默认配置（`False`）的机器上，`output` 里**一条用户日志都不会有**，而且 `output` 的内容会随一个第三方调试开关变来变去。

**因此本计划的验收基线不是「Player.log 里有没有这个 marker」，而是「在 `Log Unity Debug = False` 下 `output` 也必须含 marker」。** 关于 Player.log 那半：吐槽板原文与裁决报告都承认它无法核实（游戏当时已卡死），本计划不做任何基于它的断言。

### 0.3 回灌环路（必须避开）

本机 `config.cfg:11` 让下面这条环路成立：

```
Verse.Log.Message(x)
  → Debug.Log(x)
    → Application.logMessageReceived
      → ExplorerCore.LogUnity → ExplorerCore.Log("[Unity] x")
        → ExplorerStandalone.OnLog("[Unity] x")
          → ExplorerBootstrap.OnUELog          (Source/UELoader/ExplorerBootstrap.cs:195-218)
            → Verse.Log.Message("[UnityExplorer] [Unity] x")   ← 回到起点
```

实测证据：`UE_Data/sinai-dev-UnityExplorer/Logs/UnityExplorer 2026-10-01 155526Z.txt:7-8` 每条外部日志都成对出现一次回声。该环路的**归属是吐槽板 [15]**，本计划**不修它、也不接管它**，只保证**不往里加边**（见 §0.4）。

`MCP/index.js:4461-4469` 记录了给 `Verse.Log.Message` 打 Harmony patch 造成的实测事故：**1 分钟刷出 63.5MB 日志并触发 mono 原生崩溃**，`MCP/index.js:4462` 因此有 `HOOK_BLACKLIST_METHODS` 黑名单。**本计划严禁任何形式的 `Verse.Log` / `Debug.Log` Harmony patch。**

### 0.4 本计划如何做到「给环路零加边」

| 设计点 | 为什么安全 |
|---|---|
| 观察者 `OnUnityLogMessage` **只往内存列表写**，从不调用 `Verse.Log.*` / `Debug.*` / `ExplorerCore.Log*` | 环路是 `日志 API → UE 钩子 → 日志 API`。观察者若不产生新的日志调用，就**不新增任何边**，环路长度恒为原来的 2 |
| 闸门只在**用户代码的动态范围**内开启（主线程闭包里开、`finally` 里关），不开额外帧窗口 | 不存在「多等 N 帧把回声灌进来」的可能 |
| 丢弃 `[UnityExplorer] ` / `[UELoader] ` / `[UEHttp] ` 开头的 UnityLog 行、丢弃 `[Unity] ` 开头的 ExplorerLog 行 | 环路回声不会进 `output`，`output` 也不会因为环路而膨胀 |
| 单次执行硬上限 **512 条 + 64KB**（双维度），超出只计数不追加（`DroppedCount`） | 即使环路真的发散，单次执行的内存/CPU 也是有界的；条数封顶单独用挡不住「条少但每条巨大」（[20] 实测单条 7KB） |

### 0.5 范围外（明确不做）

- 不接管「把日志写进 `Player.log`」——那是 vanilla 既有行为（§0.2 已证 `Debug.Log` 同步落盘）。
- 不改 `ExplorerBootstrap.OnUELog`（[15] 的范围）。
- 不改 `config.cfg` 的 `Log Unity Debug`（UE 上游的合理默认；改了也修不了别人的机器）。
- 不新增 HTTP 端点、不加状态管理（裁决报告建议 3 里「独立缓冲 + 读取口」成本偏高，不采纳；改在 §3 的工具文案里给出「末尾裸表达式」的推荐做法，见 §0.8）。
- 🚫 改 `ExplorerBootstrap.OnUELog` 的前缀或去重 → 那是 [15] 的范围，拆掉它会直接破坏 [15] 的 [UnityExplorer] 桥接。
- 🚫 任何形式的 `Verse.Log` / `Debug.Log` Harmony patch → `MCP/index.js:4461-4462` 有 `HOOK_BLACKLIST_METHODS` 黑名单，`:4469` 记录了实测事故（1 分钟 63.5MB 日志 + mono 原生崩溃）。

### 0.5b 范围新增（2026-10-01 并入吐槽板 [20]）

[20]（`docs/mcp-吐槽板.md:41-71`，裁决 `docs/issue-triage/triage/20-execute-csharp-code-output-duplicate-echo.md`）**并入本计划，不单立**。其中「回声重复」已被本计划覆盖（§0.4 丢弃规则 + 任务 1 用例 4），剩余三个缺口**在本轮扩进范围**：

| # | 缺口 | 落点 |
|---|---|---|
| 1 | **控制台哨兵彻底不进 `output`** | 任务 1 步骤 4 加 `IsConsoleTailLine`；任务 1 步骤 2 用例 2 改断言 |
| 2 | **`includeOutput: false` 投影开关** | 新增任务 5（**纯 MCP 侧**，零游戏 DLL 改动、不用停游戏） |
| 3 | **单次执行的字节上限**（`MaxEntries` 只管条数不管字节 —— 本计划的真实盲区） | 新增任务 6 |

**缺口 3 为何是真盲区**：`[20]` 实测单条 70 行多行日志 = **6997 字符 / 7351 字节（≈7.0 KB）**。条数封顶挡不住「条少但每条巨大」——512 条 × 7KB ≈ 3.5 MB 上界，对该场景零约束。

### 0.5c 哨兵取舍（与旧版 §0.6 相反，此处为最终裁决）

`ConsoleController.Evaluate`（`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ConsoleController.cs:158-218`）无论用户代码写了什么，**每次执行都必定发出**下列 6 条之一（`UEHttpHandler.cs:192` 传的 `supressLog` 是 `false`，故 6 条全部会发）：

| # | 触发条件 | 原文 | 位置 | 本计划归类 |
|---|---|---|---|---|
| 1 | REPL 有返回值 | `Invoked REPL, result: {ret}` | `:183` | → `result`（**值通道，必须保留**） |
| 2 | REPL 无返回值 | `Invoked REPL (no return value)` | `:185` | → **丢弃** |
| 3 | 非 REPL 且无错 | `Code compiled without errors.` | `:205` | → **丢弃** |
| 4 | 编译失败 | `Unable to compile the code. …` | `:203` throw → `:211` | → `error` |
| 5 | REPL 运行抛异常 | `Exception invoking REPL: {ex}` | `:189` | → `error` |
| 6 | 其它未捕获异常 | `ex.ToString()` | `:216` | → 落 `output`（无法按字面量匹配，见下） |

**裁决：采纳 [20] 的建议 2 —— 变体 2、3 不进 `output`。** 理由：

1. 它们是**控制台自己的固定尾行，不是用户数据**，与用户日志混在一起会让调用方把「控制台跑完了」误当成「我的代码有输出」；
2. 按 `[20]` 的核实，变体 1-5 目前在 `output` 里**每条都出现两次**（原文 + `[Unity] [UnityExplorer] ` 回声），是固定的两份纯噪音；
3. 丢弃后 `output == null` 是**诚实信号**：「跑了，但没产出任何你自己的日志」——比一条看不懂的哨兵更有用。是否执行成功由 `success` / `result` / `executionTime` / `logCapture.entryCount` 承担，不依赖 `output` 非空。

**实现纪律（照抄 triage 20 `:200` 的告警）：** 必须用**字面量全等**匹配，🚫 **不得**写成 `StartsWith("Invoked REPL")` —— 那只挡得住变体 2，挡不住变体 3，且会误伤用户自己写的行。匹配必须**只作用于 `UnityExplorer` 通道**：用户 `Debug.Log("Code compiled without errors.")` 走 `UnityLog` 通道，必须原样保留（任务 1 用例 2d 钉死）。变体 6 是任意异常正文、无法按字面量枚举，维持现状落 `output`，并在此明示。

**已知取舍**：用户若手工写一条与变体 2/3 **逐字相同**、且经由 UE 通道发出的日志，会被一并丢弃。概率极低（且只有 UE 通道的日志会被匹配），换取的是 `output` 语义干净。

### 0.6 与 `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md` 的关系

| 冲突面 | 结论 |
|---|---|
| `ConsoleController.cs`（Roslyn 计划任务 8，第 1450 行起） | **本计划一行都不碰 `ConsoleController.cs`** ⇒ **无编辑冲突，任务 8 前后实施均可** |
| 字符串契约 | Roslyn 计划 `:1514` 保留 `ExplorerCore.Log($"Invoked REPL, result: {output}")`，本计划的解析依赖该前缀 ⇒ 兼容 |
| Roslyn 计划 `:1516` 会把空返回值哨兵从 `Invoked REPL (no return value)` 换成 `Code compiled without errors.` | **两个变体都被丢弃**（§0.5c 裁决），任务 1 用例 2a/2b 同时钉死 ⇒ 换实现不改变 `output` 语义 |
| Roslyn 计划 `:1519-1521` 的编译失败文本仍是 `Unable to compile the code.` 开头 | 本计划的 `Error` 判定基于 `Contains("Unable to compile")` ⇒ 兼容 |
| `MCP/index.js:2937` | **真冲突点**：Roslyn 计划任务 12 步骤 1（`:2059-2063`）会整行重写它。**实施顺序：先做本计划任务 4，再做 Roslyn 任务 12 步骤 1，并把本计划的日志说明并入那条新文案**（两条文案讲的是同一个字段，合并即可，不要互相覆盖） |
| `MCP/index.js:2073-2082`（`inputSchema`）/ `:4539` | Roslyn 计划**不碰这两处** ⇒ 新增任务 5（`includeOutput`）与之零冲突 |
| 求值回传写法（宿主 `void`，见 §0.8 / [34]） | Roslyn 计划 `:1511` 的 `Evaluator.Evaluate(input, out output)` 把求值体改成**语句体**，`return expr;` 才合法、末尾裸表达式不再回显。**本计划的文案按当前 mcs 实况单写（不要写 `return 表达式`）** |

> 📌 **切 Roslyn 时的「回头改文案」完整清单（本计划的 §0.6、任务 4、任务 7 全部涉及这四处，漏一处就多一个误导源）**

| # | 位置 | 现文案（mcs） | 切换后应改为 | 归属 |
|---|---|---|---|---|
| 1 | `MCP/index.js:2937`（`notes`） | 宿主 void、不要写 `return 表达式`、末尾裸表达式是值通道 | 反过来：`return expr;` 正确，末尾裸表达式不再回显 | 本计划任务 4 |
| 2 | `MCP/index.js:4536-4537`（`guidance`） | CS0127 独立归因（任务 7 新增） | **CS0127 不再可能发生** ⇒ 该分支应删除或改为反向提示 | 本计划任务 7 |
| 3 | `MCP/index.js:2072`（`description`） | 「Can run REPL code…」+ 表达式模式说明 | 值回传说明改为语句体 | **计划 12 任务 1**（本计划只登记，不实施） |
| 4 | `MCP/index.js:2078`（`code` 的 `description` 示例） | `"var x = 5; x + 3;"` 等末尾裸表达式示例 | 改为 `return` 形态示例 | **计划 12 任务 1**（本计划只登记，不实施） |

### 0.7 [02] 的定位与严重度（2026-10-01 更新）

triage 20 §5.2 在 `Player.log:2815-2895` 找到了那 70 行探针**原文逐字落盘**的正面证据，栈为 `Debug:Log ← Verse.Log.Message_Patch1 ← <InteractiveExpressionClass>:Host4 ← ConsoleController:Evaluate ← UEHttpHandler.cs:192 ← UEMainThreadDispatcher:Update`：

- ⇒ **「`Verse.Log.*` 不落 Player.log」这一前提已被证伪**，`[02]` 严重度 **🔴 → 🟠**（triage 02 当时判「无法核实」，因为判据被 GABP 超时污染）。
- ⇒ **本计划的价值因此要说准**：它做的**不是**唯一的取日志通道（`Player.log` 依然落盘），而是
  **「不随 UnityExplorer 的 `Log Unity Debug` 开关摆布的那条通道」**。该开关 UE 官方默认 `False`（`src/Config/ConfigManager.cs:119-121`），那台配置下 `output` 里**一条用户日志都没有**。这才是本条真正的缺陷形态，也正是 §0.4 去重 + 闸门 + 自有观察者要解决的。
- ⚠️ **边界（不要越界写）**：该证据只覆盖 `Verse.Log.Message`（一次多行调用）。`UnityEngine.Debug.Log` 直调必然落盘（Unity 原生）但本轮无实测；`Log.ErrorOnce` 的「正文被 `Error_Patch2` 吞掉」属 [15]，不在本计划范围。

### 0.8 求值回传写法：宿主是 `void InteractiveHost`，不要写 `return 表达式`

**已由 [34] 闭合**（`docs/mcp-吐槽板.md:212-239`，2026-10-02 TEXT20 W6 收尾实测）：

```
(20,1): error CS0127: `InteractiveHost': A return keyword must not be followed by any
        expression when method returns void
```

⇒ **用户代码被塞进一个 `void` 方法（`InteractiveHost`）里，`return <表达式>;` 不合法，只有 `return;` 合法。** 这与 `docs/mcp-吐槽板.md:934` 的旧实测记录、以及同批计划 12（`…-12-mod-presence-assembly-visibility.md:50`）完全一致。

**本节收敛为单分支**（不再是「mcs vs Roslyn」二选一）：**当前唯一的合法值回传写法是「语句 + 末尾裸表达式」**。

| 输入形态 | mcs 宿主 | `ret` / `result` | 落盘变体 |
|---|---|---|---|
| 语句 + **末尾裸表达式**（`var x = 5; x + 3;`） | `<InteractiveExpressionClass N>:Host(object&)`（**非 void**，走 `out` 参数回值） | **有值** → `result` | 变体 1 `Invoked REPL, result: {ret}` |
| 纯语句 / 类定义 / `using`（无末尾表达式） | `InteractiveHost`（**void**） | `null` → `result` 为 `null` | 变体 2 `Invoked REPL (no return value)` |
| 语句 + `return expr;` | `InteractiveHost`（**void**） | — | 🚫 **CS0127，编译失败** |

证据：变体 1/2 的分叉在 `ConsoleController.cs:180-183`（`repl.Invoke(ref ret)` → `ret?.ToString()` 非空则发变体 1，否则发变体 2）；`Host(object&)` 这一非 void 形态见 §0.7 引用的实测栈 `<InteractiveExpressionClass 4>:Host4 (object&)`。

**Roslyn 计划任务 8 之后会变**：`:1511` 的 `Evaluator.Evaluate(input, out output)` 把求值体换成语句体，届时 `return expr;` 才合法、末尾裸表达式不再回显。
⇒ **任务 3 的探针行保留**，但它的作用从「二选一探测」变成「**确认这条事实仍然成立**」：预期就是 `CS0127`。若哪天它不再报 CS0127，说明求值器已切 Roslyn，届时任务 4 文案才需要跟着调。

**两条必须写进 `notes` 的硬事实（[34] 的核心痛点：照文档写必然编译失败）**：

1. **宿主是 `void`，不要写 `return 表达式`** —— 只有 `return;` 合法；
2. **`void` ⇒ 大段文本只能走 `Debug.Log` → `data.output`**。凡是不能写成末尾裸表达式的东西（例如在循环里拼出来的大段字符串），唯一出口就是 `output`。**因此 `output` 是承重通道，`includeOutput:false` 不能顺手加** —— 加了就把数据一起关在门外了。

### 0.9 [34] 的「双份回显」裁定：**已被本计划覆盖，不是新缺口**

[34] `docs/mcp-吐槽板.md:225-227` 记：「`data.output` 会把 `UnityEngine.Debug.Log` 的全文原样回显一遍，再跟 `result`（或 `Invoked REPL, result:` 那行）**重复一次**」，并自评「与 [20] 同源，但这里是**强制的**」。

**逐条核对，结论：本计划落地后 `output` 里不会出现第二份，不是新缺口。**

| [34] 描述的第二份 | 来源 | 本计划的处置 |
|---|---|---|
| `[Unity] X` | UE 上游 `MonoHelper.cs:9,12-13` → `ExplorerCore.LogUnity` → `ExplorerCore.cs:102` | `AddUnityExplorerLog` 丢弃 `[Unity] ` 前缀行（任务 1 步骤 4）⇒ **不在 `output`** |
| `[UnityExplorer] [Unity] X` | 本项目 `ExplorerBootstrap.cs:195-218`（`:204/:207/:210`）回灌 | `AddUnityLog` 丢弃 `[UnityExplorer] ` 前缀行 ⇒ **不在 `output`** |
| `Invoked REPL, result: X` 落进 `output` | 控制台变体 1 | `Parse` 命中 `ResultPrefix` 即 `continue`，只进 `result` ⇒ **本来就不在 `output`**（改前改后都一样，见 `UEHttpHandler.cs:223-224`） |
| `Invoked REPL (no return value)` 落进 `output` | 控制台变体 2（`void` 宿主必发） | §0.5c 裁决：**丢弃** |

⇒ 剩下的「同一段文本既在 `output` 又在 `result`」是**用户自己写了两遍**（既 `Debug.Log(s)` 又以末尾表达式 `s` 回传），属两个不同字段各自的契约，**不是捕获缺陷**。任务 3 矩阵第 6 行按 §0.8 写成纯语句时，`result` 为 `null`、`output` 恰一份，正是这个判据。

**⚠️ [34] 带来的真实新增后果（已写进 §0.8 第 2 条）**：因为 `void` 宿主让 `result` 只吃末尾裸表达式，**大段文本只能走 `output`** ⇒ `output` 从「可选的日志副产品」升级为**承重的数据回传通道**。这让任务 5（`includeOutput`）与任务 6（64KB 字节上限）从「省上下文的优化」变成「**会挡住唯一出口的闸门**」，两者都必须在 `notes` 里写明代价（已写，见任务 4 文案与任务 5 步骤 3 的 description）。

---

## 1. 文件结构

### 新建

| 文件 | 职责 |
|---|---|
| `Source/UELoader/UEExecLogCapture.cs` | **纯逻辑**（只用 `System` / `System.Collections.Generic` / `System.Text`）：两条日志通道的过滤、限流（条数 + 字节）、控制台哨兵剔除、`result`/`error`/`output` 解析、`logCapture` 元数据字典 |
| `Source/ExecLogCaptureTests/ExecLogCaptureTests.csproj` | net472 Exe 测试工程，`link` 上面那个源文件（沿用 `Source/ConfigJsonTests/ConfigJsonTests.csproj` 的形状） |
| `Source/ExecLogCaptureTests/Program.cs` | 手写 `Check()` runner（沿用 `Source/HotReloadTests/Program.cs` 风格） |

### 修改

| 文件 | 改动 |
|---|---|
| `Source/UELoader/UEHttpHandler.cs:26-32` | 新增静态字段：`activeCapture`（volatile 闸门）、`unityLogHookInstalled` |
| `Source/UELoader/UEHttpHandler.cs:39-61`（`Init` 的 try 块内） | 永久订阅 `Application.logMessageReceived` |
| `Source/UELoader/UEHttpHandler.cs:66-95` | 新增 `OnUnityLogMessage` / `MapSeverity`；`LogTypeName` 改为委托给 `MapSeverity` |
| `Source/UELoader/UEHttpHandler.cs:163-245`（`ExecuteCode` 整体） | listener 改写为转发进闸门；主线程闭包内开/关闸门；解析委托给 `UEExecLogCapture.Parse`；`Success` 增加 `logCapture` |
| `MCP/index.js:2937` | `execute_csharp_code` 工具文案（见任务 4） |
| `MCP/index.js:2073-2082`（`inputSchema`） | 新增可选 boolean `includeOutput`（见任务 5） |
| `MCP/index.js:4538-4539`（`execute_csharp_code` case 内） | `includeOutput === false` 时 `delete r.output` / `delete r.logCapture`（见任务 5） |
| `MCP/index.js:4536-4537`（`execute_csharp_code` case 内） | CS0127 独立归因分支前置，CS0x 兜底降为 `else if`（见任务 7、§0.1b） |
| `MCP/test/toolchainFixes.test.js` | 新增针对 `includeOutput`（任务 5）与 CS0127 归因（任务 7）的断言 |

> `Source/UELoader/UELoader.csproj` 是 SDK-style（`Microsoft.NET.Sdk`），`Source/UELoader/*.cs` 会被自动纳入编译，**不需要改 csproj**。

---

## 2. 测试基础设施（真实命令，不要臆造）

本仓库没有 pytest / xunit / NUnit。测试一律是 **net472 `Exe` + 手写 `Check()` runner + `Main` 返回 0/1**：

| 用途 | 命令（工作目录 = `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger`） |
|---|---|
| 跑本计划的离线测试 | `dotnet run --project Source/ExecLogCaptureTests` |
| 跑既有离线测试（回归） | `dotnet run --project Source/HotReloadTests`、`dotnet run --project Source/ConfigJsonTests` |
| 构建模组程序集（**必须先停游戏**） | `dotnet build Source/UELoader/UELoader.csproj` |
| 跑 MCP 侧 JS 测试 | 在 `MCP/` 目录下执行 `npm test`（= `node --test "test/*.test.js"`，见 `MCP/package.json:9`） |

**前置守卫：** `Source/Directory.Build.props:21-26` 的 `GuardRefs` 目标对 `Source/` 下**所有** csproj 生效，缺 `$(RimWorldManagedDir)\Assembly-CSharp.dll` 或 `$(HarmonyRefDir)\0Harmony.dll` 会直接报 MSBuild `Error`。换机器时用 `-p:RimWorldGameDir=<游戏根>` 或环境变量 `RIMWORLD_GAME_DIR` 指定。

**本仓库不是 git 仓库**（`UESDdebuger/` 下只有 `.gitignore`，无 `.git/`）。因此每个任务最后一步是「改动清单」核对，不是 `git commit`。

**跨帧行为为什么不能离线测、怎么降级：** 本计划的捕获窗口建立在「`Debug.Log` 与用户代码同调用栈同步触发」这一事实（§0.2）上，窗口是**同帧**的，没有「等下一帧」这种行为需要跨帧模拟。真正离线不可测的只有两件事：(a) `Application.logMessageReceived` 真的会被 Unity 派发；(b) 闸门的开/关时机与 `UEMainThreadDispatcher` 的主线程队列一致。这两件只能进游戏验（任务 3）。可离线测的部分——过滤规则、回声去重、控制台哨兵剔除、条数与字节双重限流、`result`/`error`/`output` 解析、`logCapture` 字段形状——就是 `UEExecLogCapture` 的全部职责，已经 100% 覆盖（任务 1 的 29 条 + 任务 6 的 6 条）。**取舍：离线测试覆盖 100% 的决策逻辑，剩下 2 条接线断言（`Application.logMessageReceived` 真的会被 Unity 派发、闸门的开/关时机与主线程队列一致）交给任务 3 的游戏内验收矩阵，并在响应里用 `logCapture` 字段把「有没有真的捕获到」变成调用方可自证的事实。**

---

## 3. 任务

### 任务 1：纯逻辑捕获器 `UEExecLogCapture` + 离线测试

**文件：**
- 创建：`Source/ExecLogCaptureTests/ExecLogCaptureTests.csproj`
- 创建：`Source/ExecLogCaptureTests/Program.cs`
- 创建：`Source/UELoader/UEExecLogCapture.cs`

- [ ] **步骤 1：建测试工程 `ExecLogCaptureTests.csproj`**

`Source/ExecLogCaptureTests/ExecLogCaptureTests.csproj`（逐字照抄，形状对齐 `Source/ConfigJsonTests/ConfigJsonTests.csproj`）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <RootNamespace>ExecLogCaptureTests</RootNamespace>
    <AssemblyName>ExecLogCaptureTests</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- 直接 link 被测源码：UEExecLogCapture 零 Verse/Unity 依赖，可脱离游戏编译 -->
    <Compile Include="..\UELoader\UEExecLogCapture.cs" Link="UEExecLogCapture.cs" />
  </ItemGroup>
</Project>
```

- [ ] **步骤 2：写失败的测试 `Source/ExecLogCaptureTests/Program.cs`**

```csharp
using System;
using UELoader;

namespace ExecLogCaptureTests
{
    /// <summary>
    /// UEExecLogCapture 离线测试（吐槽板 [02]）。
    ///
    /// 覆盖的是「两条日志通道 → 过滤 → 限流 → result/error/output/logCapture」这套决策逻辑。
    /// 它的存在意义是钉死两条不能回退的性质：
    ///   1. 用户 Verse.Log.* / Debug.Log* 的原文必须出现在 output 里，且不带 [Unity] 前缀；
    ///   2. UnityExplorer 的回声（[Unity] ）与环路回声（[UnityExplorer] ）必须被丢弃，
    ///      否则 UE_Data/.../config.cfg:11 的回灌环路会把 output 灌爆。
    /// 接线（Application.logMessageReceived 真的被派发、闸门时机）不在这里测，见计划任务 3。
    /// </summary>
    internal static class Program
    {
        static int failures;

        static void Check(string name, bool cond, string detail = null)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name
                + (cond || string.IsNullOrEmpty(detail) ? "" : "  -> " + detail));
            if (!cond) failures++;
        }

        static int Main()
        {
            // ---- 1) REPL 返回值（UEConsoleController 的 Invoked REPL, result: 契约）
            var c1 = new UEExecLogCapture();
            c1.AddUnityExplorerLog("Invoked REPL, result: 42", UEExecLogSeverity.Info);
            var r1 = c1.Parse();
            Check("result 提取", r1.Result == "42", "Result=" + (r1.Result ?? "<null>"));
            Check("result 不进 output", r1.Output == null, "Output=" + (r1.Output ?? "<null>"));

            var c1b = new UEExecLogCapture();
            c1b.AddUnityExplorerLog("Invoked REPL, result:    hello  ", UEExecLogSeverity.Info);
            Check("result 去空白", c1b.Parse().Result == "hello", "Result=" + (c1b.Parse().Result ?? "<null>"));

            // ---- 2) 控制台固定尾行（哨兵）不进 output —— §0.5c 裁决
            // 6 个变体里，2/3 是纯噪音（与用户日志无关），4/5 走 Error，1 是值通道。
            var c2 = new UEExecLogCapture();
            c2.AddUnityExplorerLog("Invoked REPL (no return value)", UEExecLogSeverity.Info);
            var r2 = c2.Parse();
            Check("mcs 哨兵不进 output", r2.Output == null, "Output=" + (r2.Output ?? "<null>"));
            Check("mcs 哨兵不误判为 error", r2.Error == null, "Error=" + (r2.Error ?? "<null>"));
            Check("mcs 哨兵被丢弃计数", r2.DroppedCount == 1, "DroppedCount=" + r2.DroppedCount);

            var c2b = new UEExecLogCapture();
            c2b.AddUnityExplorerLog("Code compiled without errors.", UEExecLogSeverity.Info);
            Check("Roslyn 哨兵不进 output", c2b.Parse().Output == null,
                "Output=" + (c2b.Parse().Output ?? "<null>"));

            // 2c) 反例守卫：只挡 1 条的前缀写法会被这条钉死（StartsWith 写法在此 FAIL）
            var c2c = new UEExecLogCapture();
            c2c.AddUnityExplorerLog("Invoked REPL (no return value) -- 用户的补充说明", UEExecLogSeverity.Info);
            Check("非逐字相同的同前缀行必须保留",
                c2c.Parse().Output == "Invoked REPL (no return value) -- 用户的补充说明",
                "Output=" + (c2c.Parse().Output ?? "<null>"));

            // 2d) 通道隔离：用户自己 Debug.Log 同样字面量，走 UnityLog 通道，必须原样保留
            var c2d = new UEExecLogCapture();
            c2d.AddUnityLog("Code compiled without errors.", UEExecLogSeverity.Info);
            Check("用户日志里的同字面量不被哨兵规则吃掉",
                c2d.Parse().Output == "Code compiled without errors.",
                "Output=" + (c2d.Parse().Output ?? "<null>"));

            // 2e) 哨兵与用户日志混排时，只丢哨兵
            var c2e = new UEExecLogCapture();
            c2e.AddUnityExplorerLog("Invoked REPL (no return value)", UEExecLogSeverity.Info);
            c2e.AddUnityLog("REAL_USER_LINE", UEExecLogSeverity.Info);
            var r2e = c2e.Parse();
            Check("混排时只丢哨兵", r2e.Output == "REAL_USER_LINE", "Output=" + (r2e.Output ?? "<null>"));

            // ---- 3) 用户日志原文进入 output（核心诉求）
            var c3 = new UEExecLogCapture();
            c3.AddUnityLog("W1P01_PROBE_MARKER_ALPHA", UEExecLogSeverity.Info);
            Check("用户 Verse.Log 原文进 output", c3.Parse().Output == "W1P01_PROBE_MARKER_ALPHA",
                "Output=" + (c3.Parse().Output ?? "<null>"));

            // ---- 4) 同一条 Debug.Log 的 [Unity] 回声必须丢弃（否则重复 + 环路污染）
            var c4 = new UEExecLogCapture();
            c4.AddUnityLog("M", UEExecLogSeverity.Info);
            c4.AddUnityExplorerLog("[Unity] M", UEExecLogSeverity.Info);
            var r4 = c4.Parse();
            Check("去重后 output 恰为 M", r4.Output == "M", "Output=" + (r4.Output ?? "<null>"));
            Check("去重后只算 1 条", r4.EntryCount == 1, "EntryCount=" + r4.EntryCount);

            // ---- 5) 环路回声与本项目自用前缀必须丢弃
            var c5 = new UEExecLogCapture();
            c5.AddUnityLog("[UnityExplorer] [Unity] M", UEExecLogSeverity.Info);
            c5.AddUnityLog("[UELoader] Main thread dispatcher initialized", UEExecLogSeverity.Info);
            c5.AddUnityLog("[UEHttp] UEHttpHandler ready", UEExecLogSeverity.Info);
            var r5 = c5.Parse();
            Check("环路回声与自用前缀被丢弃", r5.Output == null && r5.EntryCount == 0,
                "Output=" + (r5.Output ?? "<null>") + " EntryCount=" + r5.EntryCount);
            Check("丢弃计入 droppedCount", r5.DroppedCount == 3, "DroppedCount=" + r5.DroppedCount);

            // ---- 6) 跨通道顺序保持
            var c6 = new UEExecLogCapture();
            c6.AddUnityLog("a", UEExecLogSeverity.Info);
            c6.AddUnityExplorerLog("b", UEExecLogSeverity.Warning);
            c6.AddUnityLog("c", UEExecLogSeverity.Error);
            Check("跨通道顺序保持", c6.Parse().Output == "a\nb\nc",
                "Output=" + (c6.Parse().Output ?? "<null>").Replace("\n", "|"));

            // ---- 7) 编译失败 / REPL 调用异常 -> Error
            var c7 = new UEExecLogCapture();
            c7.AddUnityExplorerLog(
                "Unable to compile the code. Evaluator's last output was:\r\nCS1002 ; expected", UEExecLogSeverity.Warning);
            var r7 = c7.Parse();
            Check("编译失败进 Error", r7.Error != null && r7.Error.Contains("CS1002"),
                "Error=" + (r7.Error ?? "<null>"));
            Check("编译失败不污染 output", r7.Output == null, "Output=" + (r7.Output ?? "<null>"));

            var c7b = new UEExecLogCapture();
            c7b.AddUnityExplorerLog("Exception invoking REPL: System.NullReferenceException: boom",
                UEExecLogSeverity.Warning);
            var r7b = c7b.Parse();
            Check("REPL 调用异常进 Error",
                r7b.Error != null && r7b.Error.Contains("NullReferenceException"),
                "Error=" + (r7b.Error ?? "<null>"));

            // ---- 8) 限流：硬上限 512，超出只计数
            var c8 = new UEExecLogCapture();
            for (int i = 0; i < 600; i++)
                c8.AddUnityLog("line" + i, UEExecLogSeverity.Info);
            var r8 = c8.Parse();
            Check("上限 512 生效", r8.EntryCount == UEExecLogCapture.MaxEntries,
                "EntryCount=" + r8.EntryCount);
            Check("溢出计入 droppedCount", r8.DroppedCount == 600 - UEExecLogCapture.MaxEntries,
                "DroppedCount=" + r8.DroppedCount);

            // ---- 9) 空串 / null 丢弃
            var c9 = new UEExecLogCapture();
            c9.AddUnityLog("", UEExecLogSeverity.Info);
            c9.AddUnityLog(null, UEExecLogSeverity.Info);
            c9.AddUnityExplorerLog(null, UEExecLogSeverity.Info);
            var r9 = c9.Parse();
            Check("空/null 丢弃", r9.Output == null && r9.EntryCount == 0,
                "EntryCount=" + r9.EntryCount);

            // ---- 10) logCapture 元数据字典形状（UEHttpHandler 直接塞进响应）
            var meta = c8.Parse().BuildLogCaptureData();
            Check("logCapture.unityDebugLogs", meta["unityDebugLogs"] is bool b1 && b1);
            Check("logCapture.unityExplorerLogs", meta["unityExplorerLogs"] is bool b2 && b2);
            Check("logCapture.entryCount 是 int",
                meta["entryCount"] is int i1 && i1 == UEExecLogCapture.MaxEntries,
                "entryCount=" + meta["entryCount"]);
            Check("logCapture.droppedCount", meta["droppedCount"] is int i2 && i2 == 600 - UEExecLogCapture.MaxEntries,
                "droppedCount=" + meta["droppedCount"]);
            Check("logCapture.truncated 为 true", meta["truncated"] is bool b3 && b3);

            var meta2 = new UEExecLogCapture().Parse().BuildLogCaptureData();
            Check("空捕获时 truncated 为 false", meta2["truncated"] is bool b4 && !b4);
            Check("空捕获时 entryCount 为 0", meta2["entryCount"] is int i3 && i3 == 0);

            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURES");
            return failures == 0 ? 0 : 1;
        }
    }
}
```

- [ ] **步骤 3：运行测试验证失败**

运行：

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
dotnet run --project Source/ExecLogCaptureTests
```

预期：编译失败，报 `CS0246: 未能找到类型或命名空间 "UELoader"`（`UEExecLogCapture` 尚未创建）。

- [ ] **步骤 4：实现 `Source/UELoader/UEExecLogCapture.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace UELoader
{
    /// <summary>一条日志来自哪条通道。</summary>
    public enum UEExecLogSource
    {
        /// <summary>Application.logMessageReceived：用户代码的 Verse.Log.* / UnityEngine.Debug.Log*（含 vanilla 把它们转成 Debug.Log 的部分）。</summary>
        UnityLog,

        /// <summary>ExplorerStandalone.OnLog：UnityExplorer 控制台自身日志（Invoked REPL / 编译诊断 / REPL Log() 助手）。</summary>
        UnityExplorer,
    }

    public enum UEExecLogSeverity { Info, Warning, Error }

    public sealed class UEExecLogEntry
    {
        public string Message;
        public UEExecLogSource Source;
        public UEExecLogSeverity Severity;
    }

    /// <summary>一次 execute_csharp_code 的捕获解析结果。</summary>
    public sealed class UEExecLogResult
    {
        /// <summary>REPL 返回值；无返回值或未解析到时为 null。</summary>
        public string Result;
        /// <summary>编译失败 / REPL 调用异常文本；无则 null。</summary>
        public string Error;
        /// <summary>面向用户的多行输出；无内容时为 null（与既有语义一致）。</summary>
        public string Output;
        /// <summary>实际收录的条数。</summary>
        public int EntryCount;
        /// <summary>被过滤或超限丢弃的条数。</summary>
        public int DroppedCount;

        /// <summary>塞进 Success() 的 logCapture 元数据字典（形状由离线测试钉死）。</summary>
        public Dictionary<string, object> BuildLogCaptureData()
        {
            return new Dictionary<string, object>
            {
                { "unityDebugLogs", true },
                { "unityExplorerLogs", true },
                { "entryCount", EntryCount },
                { "droppedCount", DroppedCount },
                { "truncated", DroppedCount > 0 },
            };
        }
    }

    /// <summary>
    /// execute_csharp_code 的日志捕获与解析（纯逻辑，零 Verse / Unity 依赖）。
    ///
    /// 为什么要两条通道：
    ///   - UnityExplorer 控制台自身输出（Invoked REPL、编译诊断、REPL 的 Log() 助手）只走
    ///     ExplorerStandalone.OnLog；
    ///   - 用户代码写的 Verse.Log.* / UnityEngine.Debug.Log* 只走 Application.logMessageReceived。
    ///     后者不依赖 UnityExplorer 的 "Log Unity Debug" 开关（UE 默认 false），
    ///     因此在默认配置下也能稳定拿到 —— 这正是本类存在的理由。
    ///
    /// 为什么丢弃 [Unity] 开头的回声：
    ///   本机 UE_Data/sinai-dev-UnityExplorer/config.cfg:11 是 "Log Unity Debug" = "True"，
    ///   于是同一条 Debug.Log 会同时经 LogUnity 再进一次 OnLog，形成 [Unity] 前缀的重复行。
    ///   原文已由 UnityLog 通道收录，这里丢弃即可去重；同一条环路回声
    ///   （OnUELog → Verse.Log.Message("[UnityExplorer] …")）也因此被挡在 output 之外。
    ///
    /// 线程安全：Add* 可能来自主线程（Unity 回调与 UE 日志都在主线程），
    /// 也可能来自 HTTP 线程（listener 的 += / -=），故内部统一加锁。
    /// </summary>
    public sealed class UEExecLogCapture
    {
        /// <summary>单次执行的收录上限。超过只计数不追加，保证内存与耗时有界。</summary>
        public const int MaxEntries = 512;

        /// <summary>UE 控制台约定的 REPL 返回值前缀（ConsoleController.Evaluate 发出的原文）。</summary>
        public const string ResultPrefix = "Invoked REPL, result:";

        /// <summary>UE 给 Unity 日志加的前缀（ExplorerCore.LogUnity 加，见 ExplorerCore.cs:102）。</summary>
        const string UnityEchoPrefix = "[Unity] ";

        /// <summary>本项目自己、以及回灌环路自己写出的前缀；进了 output 只会变成噪声。</summary>
        static readonly string[] SelfEchoPrefixes = { "[UnityExplorer] ", "[UELoader] ", "[UEHttp] " };

        readonly List<UEExecLogEntry> entries = new List<UEExecLogEntry>();
        int dropped;

        /// <summary>用户代码的 Verse.Log.* / UnityEngine.Debug.Log*（Application.logMessageReceived 通道）。</summary>
        public void AddUnityLog(string message, UEExecLogSeverity severity)
        {
            if (string.IsNullOrEmpty(message)) { Drop(); return; }
            for (int i = 0; i < SelfEchoPrefixes.Length; i++)
            {
                if (message.StartsWith(SelfEchoPrefixes[i], StringComparison.Ordinal)) { Drop(); return; }
            }
            Add(new UEExecLogEntry
            {
                Message = message,
                Source = UEExecLogSource.UnityLog,
                Severity = severity,
            });
        }

        /// <summary>UnityExplorer 控制台自身日志（ExplorerStandalone.OnLog 通道）。</summary>
        public void AddUnityExplorerLog(string message, UEExecLogSeverity severity)
        {
            if (string.IsNullOrEmpty(message)) { Drop(); return; }
            // 同一 Debug.Log 已在 UnityLog 通道收录原文，这里丢掉重复回声。
            if (message.StartsWith(UnityEchoPrefix, StringComparison.Ordinal)) { Drop(); return; }
            Add(new UEExecLogEntry
            {
                Message = message,
                Source = UEExecLogSource.UnityExplorer,
                Severity = severity,
            });
        }

        void Add(UEExecLogEntry entry)
        {
            lock (entries)
            {
                if (entries.Count >= MaxEntries) { dropped++; return; }
                entries.Add(entry);
            }
        }

        void Drop()
        {
            lock (entries) { dropped++; }
        }

        /// <summary>解析成 result / error / output。可重复调用，结果幂等。</summary>
        public UEExecLogResult Parse()
        {
            List<UEExecLogEntry> snapshot;
            int droppedSnapshot;
            lock (entries)
            {
                snapshot = new List<UEExecLogEntry>(entries);
                droppedSnapshot = dropped;
            }

            string result = null;
            string error = null;
            int sentinelDrops = 0;
            var sb = new StringBuilder();

            for (int i = 0; i < snapshot.Count; i++)
            {
                UEExecLogEntry e = snapshot[i];

                if (e.Source == UEExecLogSource.UnityExplorer
                    && e.Message.StartsWith(ResultPrefix, StringComparison.Ordinal))
                {
                    result = e.Message.Substring(ResultPrefix.Length).Trim();
                    continue;
                }

                // 控制台固定尾行（§0.5c 裁决的变体 2/3）：逐字匹配、只作用于 UnityExplorer 通道。
                if (e.Source == UEExecLogSource.UnityExplorer && IsConsoleTailLine(e.Message))
                {
                    sentinelDrops++;
                    continue;
                }

                if (e.Source == UEExecLogSource.UnityExplorer
                    && (e.Message.Contains("Unable to compile") || e.Message.Contains("Exception invoking REPL")))
                {
                    error = e.Message;
                    continue;
                }

                if (e.Message.Length == 0)
                    continue;

                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(e.Message);
            }

            return new UEExecLogResult
            {
                Result = result,
                Error = error,
                Output = sb.Length == 0 ? null : sb.ToString(),
                EntryCount = snapshot.Count,
                DroppedCount = droppedSnapshot + sentinelDrops,
            };
        }

        /// <summary>
        /// UE 控制台自己发出的固定尾行（纯噪音，不是用户日志）。
        ///
        /// 必须**逐字全等**匹配：变体有 6 个，只挡其中一条的前缀写法（StartsWith）会被任务 1 的
        /// 用例 2c 钉死；变体 3（Roslyn 计划任务 8 之后的空返回值哨兵）字面量完全不同。
        /// 调用方已保证只在 Source == UnityExplorer 时调用 —— 用户 Debug.Log 同样字面量
        /// 走的是 UnityLog 通道，必须原样保留（用例 2d）。
        /// 变体 6（ConsoleController.cs:216 的 ex.ToString()）是任意异常正文，无法按字面量枚举，
        /// 维持现状落 output。
        /// </summary>
        static bool IsConsoleTailLine(string message)
        {
            return message == "Invoked REPL (no return value)"
                || message == "Code compiled without errors.";
        }
    }
}
```

- [ ] **步骤 5：运行测试验证通过**

运行：

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
dotnet run --project Source/ExecLogCaptureTests
```

预期：29 行 `PASS`，末行 `ALL PASS`，退出码 `0`。

- [ ] **步骤 6：改动清单**

```
新增 Source/UELoader/UEExecLogCapture.cs
新增 Source/ExecLogCaptureTests/ExecLogCaptureTests.csproj
新增 Source/ExecLogCaptureTests/Program.cs
未改动任何既有文件（UELoader.csproj 是 SDK-style，自动纳入新 .cs）
```

---

### 任务 2：`UEHttpHandler` 接线

**文件：**
- 修改：`Source/UELoader/UEHttpHandler.cs:26-32`
- 修改：`Source/UELoader/UEHttpHandler.cs:39-61`
- 修改：`Source/UELoader/UEHttpHandler.cs:66-95`
- 修改：`Source/UELoader/UEHttpHandler.cs:163-245`

> **前置：RimWorld 必须已关闭。** `UELoader.csproj:92-96` 的 `DeployToAssemblies` 目标会把 `UELoader.dll` 拷进 `Assemblies/`，游戏运行中该文件被占用，`dotnet build` 会失败。

- [ ] **步骤 1：加静态字段**

`Source/UELoader/UEHttpHandler.cs:31` 之后（`static bool mainDispatcherReady;` 下面）插入：

```csharp
        /// <summary>
        /// 当前打开的捕获会话；null = 无人在捕获。
        /// 只在 ExecuteCode 的主线程闭包内部（用户代码的动态范围）置位/清零，
        /// 因此它天然是「同帧、同调用栈」的窗口 —— Verse.Log.* 与 Debug.Log* 是同步触发
        /// Application.logMessageReceived 的（Verse.Log.Message 内部直接调 Debug.Log），
        /// 不需要等任何一帧。
        /// </summary>
        static volatile UEExecLogCapture activeCapture;

        /// <summary>Application.logMessageReceived 是否已订阅（Init 幂等标记）。</summary>
        static bool unityLogHookInstalled;
```

- [ ] **步骤 2：在 `Init()` 里永久订阅 Unity 日志观察者**

`Source/UELoader/UEHttpHandler.cs:56`（`UEHttpLog.Message("[UEHttp] UEHttpHandler ready…")`）之前插入。**用独立 try/catch**：外层 catch 会吞掉异常并跳过 `UEMainThreadDispatcher.EnsureInitialized()`，不能让订阅失败连累调度器。

```csharp
                // 永久订阅 Unity 日志观察者（用户 Verse.Log.* / Debug.Log* 的唯一来源）。
                // Init() 由 ExplorerBootstrap.Initialize（SceneManager.sceneLoaded，主线程）调用，
                // 满足 Unity 对 logMessageReceived 的主线程订阅要求。
                if (!unityLogHookInstalled)
                {
                    try
                    {
                        Application.logMessageReceived += OnUnityLogMessage;
                        unityLogHookInstalled = true;
                    }
                    catch (Exception ex)
                    {
                        UEHttpLog.Error($"[UEHttp] logMessageReceived subscribe failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
```

- [ ] **步骤 3：加观察者与严重度映射，合并 `LogTypeName`**

`Source/UELoader/UEHttpHandler.cs:82-95`（现有的 `static string LogTypeName(LogType t)` 及其后到 `GetLogs` 之前）整段替换为下面这段。**保留 `LogTypeName` 的既有返回字面量**（`OnUELogBuffer` 仍在用它写环形缓冲，`get_unityexplorer_logs` 的消费者见的就是 `"Error"/"Warning"/"Log"` 三个值）—— 注意默认值必须继续叫 `Log` 而不是 `Info`，只把映射收敛到一处。

```csharp
        static UEExecLogSeverity MapSeverity(LogType t)
        {
            switch (t)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    return UEExecLogSeverity.Error;
                case LogType.Warning:
                    return UEExecLogSeverity.Warning;
                default:
                    return UEExecLogSeverity.Info;
            }
        }

        static string LogTypeName(LogType t)
        {
            // 环形缓冲（get_unityexplorer_logs）的既有契约：默认档叫 "Log"，不是 "Info"。
            UEExecLogSeverity severity = MapSeverity(t);
            return severity == UEExecLogSeverity.Info ? "Log" : severity.ToString();
        }

        /// <summary>
        /// Unity 日志观察者：用户代码写的 Verse.Log.* / UnityEngine.Debug.Log* 会同步走到这里。
        ///
        /// 【硬约束】本方法里**只允许往内存列表写**。
        /// 本机 UE_Data/sinai-dev-UnityExplorer/config.cfg:11 是 "Log Unity Debug" = "True"，
        /// 已经构成 Verse.Log → Debug.Log → UE 钩子 → ExplorerBootstrap.OnUELog → Verse.Log
        /// 的回灌环路；这里若再写一次任何日志，就等于给环路加一条边把它放大
        /// （MCP/index.js:4461-4469 记录过给 Verse.Log.Message 打 patch 造成 1 分钟 63.5MB
        /// 日志 + mono 原生崩溃的实测事故）。观察者不写日志 ⇒ 环路长度不变。
        ///
        /// 订阅在 Init() 里一次性完成（主线程），此处不 += / -=，避免非主线程动 Unity 事件。
        /// </summary>
        static void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            UEExecLogCapture capture = activeCapture;
            if (capture == null)
                return;
            try
            {
                capture.AddUnityLog(condition, MapSeverity(type));
            }
            catch
            {
                // 捕获失败不影响用户代码的执行结果
            }
        }
```

- [ ] **步骤 4：重写 `ExecuteCode`（`Source/UELoader/UEHttpHandler.cs:163-245` 整段替换）**

```csharp
        public static Dictionary<string, object> ExecuteCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return Error(400, "code is required", "MISSING_PARAMETER");

            if (!IsConsoleReady(out string consoleReason))
                return Error(503, consoleReason, "CONSOLE_NOT_READY");

            var capture = new UEExecLogCapture();

            // 通道 B：UnityExplorer 自身日志（Invoked REPL / 编译诊断 / REPL 的 Log() 助手）。
            // 闸门关闭时直接丢弃 —— 订阅本身仍按原逻辑在 HTTP 线程成对 +=/-=，
            // 保持「一次调用一个 listener」的原有时序语义不变。
            Action<string, LogType> listener = (m, t) =>
            {
                if (activeCapture == null)
                    return;
                try
                {
                    capture.AddUnityExplorerLog(m, MapSeverity(t));
                }
                catch { }
            };

            ExplorerStandalone.OnLog += listener;
            string err = null;
            // executionTimeMs 由主线程（闭包内）写、HTTP 线程（闭包外）读；
            // ExecuteOnMainThread 同步阻塞等待主线程完成后才返回，读发生在返回前，
            // 由此建立 happens-before，普通 long 跨线程读是安全的。
            long executionTimeMs = 0;
            try
            {
                UEMainThreadDispatcher.ExecuteOnMainThread(() =>
                {
                    // 闸门在主线程闭包内开、finally 里关：窗口 = 用户代码的精确动态范围。
                    // Verse.Log.* / Debug.Log* 是在同调用栈同步派发的，因此不需要跨帧等待。
                    activeCapture = capture;
                    try
                    {
                        try
                        {
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            ConsoleController.Evaluate(code, false);
                            sw.Stop();
                            executionTimeMs = sw.ElapsedMilliseconds;
                        }
                        catch (Exception ex)
                        {
                            err = ex.Message;
                            Log.Error($"[UEHttp] ExecuteCode failed: {ex}");
                        }
                    }
                    finally
                    {
                        // 必须由闭包自己关：ExecuteOnMainThread 超时时闭包可能还没跑或还在跑，
                        // 由 HTTP 线程来关会和闭包里的赋值打架。由闭包收口则总是自洽。
                        activeCapture = null;
                    }
                }, 20000);
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }
            finally
            {
                ExplorerStandalone.OnLog -= listener;
            }

            if (err != null)
                return Error(400, err, "COMPILATION_ERROR");

            UEExecLogResult parsed = capture.Parse();

            if (parsed.Error != null)
            {
                var extra = new Dictionary<string, object>
                {
                    { "output", parsed.Output },
                    { "logCapture", parsed.BuildLogCaptureData() },
                };
                return Error(400, parsed.Error, "COMPILATION_ERROR", extra);
            }

            return Success(new Dictionary<string, object>
            {
                { "result", parsed.Result },
                { "output", parsed.Output },
                { "executionTime", executionTimeMs },
                // 明写捕获边界：output 覆盖哪些来源、有没有截断，调用方可自证，不再「静默失真」。
                { "logCapture", parsed.BuildLogCaptureData() },
            });
        }
```

- [ ] **步骤 5：离线测试仍然全绿（回归）**

运行：

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
dotnet run --project Source/ExecLogCaptureTests
dotnet run --project Source/ConfigJsonTests
```

预期：两次都输出 `ALL PASS`（`UEExecLogCapture` 未被本任务改动，解析逻辑未变）。

- [ ] **步骤 6：构建模组程序集**

先确认游戏已关闭（RimWorld 进程不存在），再运行：

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
dotnet build Source/UELoader/UELoader.csproj
```

预期：`Build succeeded`，产物已由 `UELoader.csproj:92-96` 自动拷到 `Assemblies/UELoader.dll`。
若报 `MSB3021`/文件占用 → 游戏还在跑，先停游戏。若报 `CS0246` 且提到 `UEExecLogCapture` → 检查 `Source/UELoader/UEExecLogCapture.cs` 的命名空间是否写成 `UELoader`。

- [ ] **步骤 7：改动清单**

```
修改 Source/UELoader/UEHttpHandler.cs
  字段区(31 行后)      +activeCapture +unityLogHookInstalled
  Init()(56 行前)      +永久订阅 Application.logMessageReceived
  LogTypeName()(82-95) 替换为 MapSeverity + LogTypeName + OnUnityLogMessage
  ExecuteCode()(163-245) 整段替换
未改动 Source/UE-4.9.0-official/**（UEHttpHandler 是本项目自有文件，不在 UE 源码树里）
未改动 ConsoleController.cs（与 Roslyn 计划任务 8 无冲突）
```

---

### 任务 3：游戏内验收（含回灌环路安全性）

**文件：** 无（纯验收）

> 需要一个能启动 RimWorld 的时段。本任务会启动/停止游戏，**不要在别的并行会话正在用 MCP 调试时执行**。

- [ ] **步骤 1：准备 —— 记录基线 Player.log 大小**

```powershell
$log = "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log"
$before = (Get-Item $log).Length
"before = $before"
```

- [ ] **步骤 2：启动游戏、进图、打开一次 UE 的 C# Console 面板（F7）**

C# Console 必须被打开过一次，`ConsoleController.Evaluator` 才非 null（`UEHttpHandler.cs:168-169` 的 `IsConsoleReady` 会返回 `CONSOLE_NOT_READY`）。

- [ ] **步骤 3：**【关键验收 · 默认配置】把 `UE_Data/sinai-dev-UnityExplorer/config.cfg:11` 改成 `"Log Unity Debug" = "False"`（UE 配置面板里关掉 `Log Unity Debug` 同样效果），重启游戏后重复步骤 2。

- [ ] **步骤 4：前置断言 —— 先确认【31】的静默重放已修好，否则本任务全部矩阵不可信**

> 🚨 **本步骤是整个任务 3 的前置闸门。不通过就不要执行步骤 5 的矩阵。**

`execute_csharp_code` 在计划 31 落地前**会被静默重放**（吐槽板 `docs/mcp-吐槽板.md:296-328`，🔴）：第一遍改完游戏状态、写响应时 UE HTTP 连接被对端重置，MCP 重试后交回的是**第二遍**的结果。

- **重试为何必然发生**：`MCP/index.js:812-835` 的 catch 块**无条件重发同一个 POST** —— `:816` 的正则含 `ECONNRESET`，`:817` 的 `shouldRefreshPort = isConnError || (error && error._aborted)` 连**超时**也重试，`:818-821` 直接复用同一个 `options`（同一 body），**无幂等判断、无 request id、无 attempt 计数**。
- **且是结构必然**：`MCP/index.js:4534` 用 `{ slow: true }`，慢档 = `MCP/index.js:741` 的 `ueSlowRequestTimeoutMs ?? 10000`（10s，`:743` 取用）；而游戏侧主线程预算是 `UEHttpHandler.cs:201` 的 **20s** ⇒ **执行超过 10 秒的调用 100% 被重放**。

**对本计划的影响是「反向假阳性」**：首遍改状态、次遍返回 `0` 或编译失败 ⇒ 验收者会把**没坏的实现**记成失败，去修一个不存在的问题。

**断言怎么判**（对步骤 5 矩阵的**每一条**都要做）：

1. 该条哨兵（例如 `W1P01_MARKER_A`）在 `Player.log` 里**只出现一次**。出现两次 ⇒ 被重放了，**该条结果作废**，重发或先修计划 31。
2. `Player.log` 同时搜 `[UEHttp] Failed to send response`（重放的第一现场，见吐槽板 `:311`）。命中即说明发生过连接重置。
3. 本计划的矩阵**都不带破坏性副作用**，因此重放只影响判读、不损坏存档；但**判读前必须先做这一步**，否则会把第二遍的世界状态当成第一遍的。

**通过条件**：计划 31 已落地（响应带 attempt 计数 / 重放被拦截），**或**步骤 5 每一条的哨兵在 `Player.log` 中均只出现一次。

- [ ] **步骤 5：验收矩阵（`Log Unity Debug = False`，即 UE 官方默认值）**

对每条执行 `agg_call_tool { tool: "execute_csharp_code", args: { "code": "<code>" } }`：

| # | code | 期望 |
|---|---|---|
| 1 | `Verse.Log.Message("W1P01_MARKER_A"); UnityEngine.Debug.Log("W1P01_MARKER_B"); "W1P01_DONE"` | `success=true`；**末尾裸表达式**（不是 `return`，见 §0.8）→ `result` 含 `W1P01_DONE`；**`output` 同时含 `W1P01_MARKER_A` 与 `W1P01_MARKER_B`，且都不带 `[Unity] ` 前缀**；`logCapture.truncated=false` |
| 2 | `1 + 1` | `result` 为 `2`；**`output` 为 `null`**（控制台哨兵已被剔除，见 §0.5c —— 这一条正是哨兵裁决的验收点）；`logCapture.entryCount=1`、`droppedCount=1` |
| 3 | `Log("REPL_HELPER")` | `output` 含 `REPL_HELPER`（REPL 助手走的是 ExplorerStandalone.OnLog 通道，仍可用） |
| 4 | `foreach (var i in 1..3) UnityEngine.Debug.Log("burst" + i); 0` | `output` 为三行 `burst1`/`burst2`/`burst3`，顺序正确；`result` 为 `0`（末尾裸表达式兜底，让这条在两种求值器下都能过） |
| 5 | `this is not valid csharp` | `success=false`、`errorCode=COMPILATION_ERROR`、`error` 含 `CS` 编号 |
| 6 | `return 1;` | **事实确认探针（不是二选一）**：预期 `success=false`、`errorCode=COMPILATION_ERROR`、`error` 含 `CS0127` + `InteractiveHost` —— 这**正是 [34] 记录的行为**，报出来 = 该事实仍成立。若它**没有**报 CS0127，说明求值器已切 Roslyn，把实测写进记录表并回头修任务 4 文案 |
| 7 | `UnityEngine.Debug.Log("DUPLICATE_PROBE_X"); "DUPLICATE_PROBE_X"` | **[34] 双份回显裁定验收**：预期 `output` **恰含一份** `DUPLICATE_PROBE_X`（不带 `[Unity] `、不带 `[UnityExplorer] `），`result` 为 `DUPLICATE_PROBE_X`。`output` 与 `result` 各一份是**两个字段各自的契约**（用户自己写了两遍），不算重复；判据是 `output` 内部不重复 |

**判定**：第 1 条通过 = 本条修复成立。**在 `False` 下通过，才说明捕获不再依赖那个第三方开关**（这正是本计划要修的核心；见 §0.2）。第 6 条**预期就是 CS0127**，报出来即事实成立；第 7 条判 `output` 内部不重复（见 §0.9 裁定）。

> 📌 **`return 1;` 与 `CS0127` 的矛盾已闭合（2026-10-02，由 [34] 定案）**
>
> 此前本矩阵第 1 行写的是 `…; return 1;` 并期望 `result=1`，而 `docs/mcp-吐槽板.md:934` 的实测记录 `return expr;` → `CS0127`，二者不可能同时为真。
> **定案依据**：`docs/mcp-吐槽板.md:219-223`（[34]，2026-10-02 TEXT20 W6 一手实测）逐字给出 `error CS0127: 'InteractiveHost': A return keyword must not be followed by any expression when method returns void`，**并点明宿主方法就是 `void`** ⇒ `return expr;` 必然失败是**结构性事实**，不是环境问题。
> **后果**：矩阵第 1、4 行一律用**末尾裸表达式**（唯一合法值通道）；第 6 行降级为**事实确认探针**（预期 CS0127）；新增第 7 行验收 [34] 的双份回显裁定（§0.9）。机制与两个求值器的差异见 **§0.8**。

**实测记录表（第一格已有权威答案；第二格待 mcs→Roslyn 切换后复核）**

| 项 | 值 |
|---|---|
| 第 6 行 `return 1;` 的结果 | ✅ **已定：`CS0127` / `InteractiveHost`（void 宿主）**。来源：`docs/mcp-吐槽板.md:219-223`（[34]，2026-10-02 一手实测），与 `:934` 旧记录、计划 12 `:50` 三方一致。**执行本任务时只需复核「该事实是否仍然成立」** —— 若不再报 CS0127，在下格记下并回头改任务 4 文案 |
| 当前求值器 | **mcs**（由上格推定）。**待 mcs→Roslyn 切换（Roslyn 计划任务 8）后复核**；届时 `return expr;` 才合法，任务 4 的「不要写 `return 表达式`」需改写 |

- [ ] **步骤 6：重复第 5 条矩阵一次，此时 `Log Unity Debug = True`**（改回 `config.cfg:11`，重启游戏）

期望：第 1 条的 `output` **仍然只含一遍** `W1P01_MARKER_A` / `W1P01_MARKER_B`（不带 `[Unity] `、不带 `[UnityExplorer] `），`logCapture.droppedCount` 明显大于 0（回声被丢弃的计数）。

**判定**：两种配置下 `output` 内容一致 ⇒ 去重与环路过滤生效。

- [ ] **步骤 7：环路安全性断言（`Log Unity Debug = True`，回灌环路成立的配置）**

重复第 5 条矩阵的第 4 行 20 次，然后运行：

```powershell
$log = "$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log"
$after = (Get-Item $log).Length
"delta = $($after - $before) bytes"
```

判定：
- `delta` **不得**出现数量级爆炸（20 次正常执行的增量应在 MB 级以内）。
- 出现「几十 MB / 分钟」的量级 ⇒ 本改动给环路加了边，**立即回滚任务 2**（按 §0.4 复核：观察者里不应有任何写日志的调用）。
- 同时在游戏里观察：帧率不应明显掉档，主线程不应被日志风暴拖死。

- [ ] **步骤 8：确认 MCP 侧 `attachUsageOnFailure` 行为未变**

`MCP/index.js:4006-4019` 只对 `success === false` 的响应追加 usage 噪声。本任务新增的 `logCapture` 只加在 `success=true` 的负载和**本来就失败**的 `COMPILATION_ERROR` 的 `data` 里，**不改任何响应的 success 标志**，因此第 5 条（编译失败）的响应里出现 usage 建议与今天完全一致 —— 这是预期行为，不是回归。

- [ ] **步骤 9：改动清单**

```
本任务不产生任何文件改动。
```

---

### 任务 4：MCP 工具文案

**文件：**
- 修改：`MCP/index.js:2937`

> **与 Roslyn 计划任务 12 步骤 1（`docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md:2059-2063`）改的是同一行。** 先做本步骤，再做那条，并把下面的日志说明并入那条新文案（两者讲同一个字段，合并，不要互相覆盖）。

- [ ] **步骤 1：替换 `execute_csharp_code` 的 notes**

`MCP/index.js:2937` 整行替换为：

```javascript
  execute_csharp_code: { example: '{ "code": "Verse.GenTicks.TicksGame" }', notes: '⚠️ 宿主方法 InteractiveHost 是 void：不要写 return 表达式（return "…"; 必报 CS0127: A return keyword must not be followed by any expression when method returns void），只有 return; 合法。要拿值就把值写成末尾裸表达式（var sb = new System.Text.StringBuilder(); …; sb.ToString();），它经 Invoked REPL, result: 回显为 result；遇到 CS0127 即说明如此，不要因此改写整段代码。控制台默认无 using 引用，裸类型名（如 GenTicks）会报 CS0103；须用全限定名或先 add_using_directive（兼容 script/expression 别名）。返回值三个字段：result = 末尾裸表达式的值（纯语句时为 null）；output = 本次执行期间捕获到的全部日志，含用户代码自己写的 Verse.Log.* 与 UnityEngine.Debug.Log* 原文（不依赖 UnityExplorer 的 Log Unity Debug 开关，也不再带 [Unity] 前缀，同一条日志只出现一次）；logCapture = 捕获元数据 { unityDebugLogs, unityExplorerLogs, entryCount, droppedCount, outputBytes, truncated }，truncated=true 表示条数上限或字节上限被打满、需要的信息被丢弃，此时不能把 output 当作完整记录。output 里不会出现控制台自己的固定尾行 "Invoked REPL (no return value)" / "Code compiled without errors." —— 那是 UE 控制台的收尾噪音、不是你的日志，别拿它当判据；output 为 null 表示"跑了，但没产出你自己的日志"，是否执行成功看 success / result / executionTime / logCapture.entryCount。⚠️ 宿主是 void 意味着"在循环里拼出来的大段字符串"这类没法写成末尾裸表达式的东西，唯一出口就是 Debug.Log → data.output：所以 output 是承重的数据回传通道，它有 512 条与 64KB 双重上限（两种上限都可被撑爆），传 includeOutput:false 前先确认你要的东西不在 output 里，否则会把数据一起关在门外。' },
```

- [ ] **步骤 2：验证没破坏 MCP 侧既有断言**

`MCP/test/toolchainFixes.test.js:121` 会对 `index.js` 做**源码切片断言**（`source.slice(indexOf('function toolListLine('), indexOf('function buildMenuNode('))`）。本步骤只替换 `TOOL_USAGE_NOTES` 里一个对象字面量的一行，**没有在 `toolListLine` 与 `buildMenuNode` 两个函数之间插入任何东西**，该切片不受影响。用测试证明：

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：全部 `pass`，无 `fail`（其中包含 §3 护栏那条用例）。若该用例失败，说明改动位置错了 —— 检查是否误把新文本插到了 `toolListLine` 之前或 `buildMenuNode` 之后。

- [ ] **步骤 3：语法自检**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --check index.js
```

预期：无输出，退出码 `0`。

- [ ] **步骤 4：改动清单**

```
修改 MCP/index.js:2937（execute_csharp_code 的 TOOL_USAGE_NOTES.notes 单行）
未新增/删除任何 JS 函数，未触碰 toolListLine / buildMenuNode / attachUsageOnFailure
```

---

### 任务 5：`includeOutput: false` 投影开关（纯 MCP 侧）

**文件：**
- 修改：`MCP/index.js:2073-2082`（`inputSchema.properties`）
- 修改：`MCP/index.js:4539`（`execute_csharp_code` case 内，`return r;` 之前）
- 测试：`MCP/test/toolchainFixes.test.js`（新增一条用例）

> **为什么只改 MCP 侧**：走游戏侧要多读一个 body 字段 + 改 `UEHttpHandler.ExecuteCode` + `dotnet build Source/UELoader/UELoader.csproj` + **停游戏**后重投 `Assemblies/UELoader.dll`（`UELoader.csproj:92-96` 的 `DeployToAssemblies` 会因文件占用失败）—— 成本高一个量级。本开关是纯响应投影，不改 HTTP 协议、不改游戏侧契约。

- [ ] **步骤 1：写失败的测试**

在 `MCP/test/toolchainFixes.test.js` 末尾（文件最后一条 `});` 之后）追加：

```javascript
// ===========================================================================
// execute_csharp_code 的 includeOutput 投影开关（计划 02 任务 5）
// 大探针会把 output 撑到几十 KB；需要时允许调用方完全不要 output。
// ===========================================================================
test('§5 execute_csharp_code：inputSchema 声明 includeOutput（可选 boolean，默认 true）', () => {
  const schema = source.slice(
    source.indexOf("name: 'execute_csharp_code'"),
    source.indexOf("name: 'reset_csharp_console'"),
  );
  assert.match(schema, /includeOutput/, 'schema 必须声明 includeOutput，否则调用方无从得知它存在');
  assert.match(schema, /type:\s*'boolean'/, 'includeOutput 必须是 boolean');
  assert.doesNotMatch(
    schema,
    /required:\s*\[[^\]]*includeOutput[^\]]*\]/,
    'includeOutput 必须可选（省略 = 现在的行为），不得进 required',
  );
});

test('§5 execute_csharp_code：includeOutput===false 时从响应里删掉 output 与 logCapture', () => {
  const block = source.slice(
    source.indexOf("case 'execute_csharp_code'"),
    source.indexOf("case 'reset_csharp_console'"),
  );
  assert.match(block, /includeOutput\s*===\s*false/, '必须用严格 === false，不能用真值判断');
  assert.match(block, /delete\s+r\.output/, '必须删掉 output');
  assert.match(block, /delete\s+r\.logCapture/, 'logCapture 是 output 的元数据，必须一起删，否则字段不一致');
});

test('§5 execute_csharp_code：includeOutput 不进别名表（它不是 code 的别名）', () => {
  const alias = source.slice(
    source.indexOf('execute_csharp_code: { script:'),
    source.indexOf("execute_csharp_code: { script:") + 200,
  );
  assert.doesNotMatch(alias, /includeOutput/, '别名表只归一 code 的别名（script/expression），不得混入新参数');
});
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：新增的 3 条 **FAIL**（`includeOutput` 尚未出现在 `index.js`）。

- [ ] **步骤 3：`inputSchema` 加可选 boolean**

`MCP/index.js:2076-2080` 的 `properties` 里，在 `code` 之后加一项（保留 `code` 原样）：

```javascript
        code: { 
          type: 'string', 
          description: 'C# code to execute. Examples: "Log(\"Hello\");", "var x = 5; x + 3;", "using UnityEngine; Debug.Log(\"test\");"' 
        },
        includeOutput: {
          type: 'boolean',
          description: '是否回传 output / logCapture。默认 true。仅在"确认要的东西不在 output 里"时才传 false（如 Verse.GenTicks.TicksGame 这类只要一个值的调用）—— 注意宿主方法 InteractiveHost 是 void，在循环里拼出来的大段字符串这类没法写成末尾裸表达式的东西，唯一出口就是 Debug.Log → output，关掉它会把数据一起关在门外。output 本身有 512 条与 64KB 双重上限，见 logCapture.truncated。'
        }
```

`required: ['code']`（`:2081`）**不动** —— `includeOutput` 必须可选。

- [ ] **步骤 4：响应投影**

`MCP/index.js:4538`（`}` 收尾）与 `:4539`（`return r;`）之间插入：

```javascript
        // 调用方显式不要 output 时，连同它的元数据一起删（纯投影，不改游戏侧契约）
        if (args.includeOutput === false) {
          delete r.output;
          delete r.logCapture;
        }
```

> 用 `=== false` 而非 `!args.includeOutput`：省略 `includeOutput` 必须保持现状行为（回传 `output`）。

- [ ] **步骤 5：运行测试验证通过**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
node --check index.js
```

预期：`npm test` 全 `pass`（含既有 §3 护栏用例与新增 3 条），`node --check` 无输出、退出码 `0`。

- [ ] **步骤 6：改动清单**

```
修改 MCP/index.js:2073-2082（inputSchema.properties 增 includeOutput）
修改 MCP/index.js:4538-4539（execute_csharp_code case 内加投影删除）
修改 MCP/test/toolchainFixes.test.js（追加 3 条用例）
未改 :3817 别名表（execute_csharp_code: { script:'code', expression:'code' }）
未改任何游戏侧文件；未触碰 toolListLine / buildMenuNode / attachUsageOnFailure
```

---

### 任务 6：单次执行的字节上限（补本计划的盲区）

**文件：**
- 修改：`Source/UELoader/UEExecLogCapture.cs`
- 修改：`Source/ExecLogCaptureTests/Program.cs`

> **为什么要**：`MaxEntries = 512` 只管**条数**。`[20]` 实测**单条** 70 行多行日志 = 6997 字符 / 7351 字节（≈7.0 KB）—— 「条少但每条巨大」完全绕过条数上限，512 × 7KB ≈ 3.5 MB 上界。
> **对环路零加边的性质不变**：本任务只改 `Add*` 的入库判据，**不碰** `UEHttpHandler.OnUnityLogMessage`（§0.4 的硬约束仍在），观察者依然只往内存写、不调用任何日志 API。
>
> **⚠️ 代价说明（别把 `output` 当成唯一出口）**：字节上限会挡住「大段文本走 `output`」这条路，但**还有第二出口** ——
> `throw new System.Exception(s)` 在 `void` 宿主里**合法**（§0.8：`return expr;` 才非法）。它在 `ConsoleController.cs:187-190` 被接住并发出变体 5
> `Exception invoking REPL: {ex}`，本计划的 `Parse` 认 `Contains("Exception invoking REPL")` ⇒ 进 **`error`**，而 **`error` 不受 64KB 约束**
> （`error` 是 `Error()` 的顶层字段，不经 `captured`、不进 `output`）。
> ⇒ 需要**保证完整送达**的大块文本（例如必须逐字核对、不接受截断的载荷），用 `throw` 而不是 `Debug.Log`。
> ⚠️ **前提须实测**：变体 5 只在输入被 mcs 编译成 **REPL 形态**（`repl != null`，`ConsoleController.cs:174`）时才会命中；纯语句块若落到 `:213-217` 的兜底，异常会走**变体 6** `ex.ToString()`，那条**进 `output`、受 64KB 约束**。
> **执行本任务时必须实测确认**：跑一句 `throw new System.Exception(new string('Z', 200000)); 0`，看返回的 `error` 是否含那 200000 字符（是 ⇒ 第二出口可用；否 ⇒ 第二出口在当前求值器下不成立，只能靠分批 `Debug.Log` + `includeOutput:false` 之外的其它策略）。**结论写进下方改动清单。**

- [ ] **步骤 1：写失败的测试**

`Source/ExecLogCaptureTests/Program.cs` 的 `Main()` 里，把最后这句

```csharp
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURES");
```

**之前**插入：

```csharp
            // ---- 11) 字节上限：条数没超，但体积超了（[20] 的核心缺口）
            // 实测单条 70 行多行日志 ≈ 7000 字符；条数封顶挡不住「条少但每条巨大」。
            // 算术：MaxOutputBytes = 65536；9 × 7000 = 63000 放得下，第 10 条（累计 70000）放不下。
            var c10 = new UEExecLogCapture();
            string big = new string('x', 7000);
            for (int i = 0; i < 10; i++)
                c10.AddUnityLog(big, UEExecLogSeverity.Info);
            var r10 = c10.Parse();
            Check("前 9 条收下", r10.EntryCount == 9, "EntryCount=" + r10.EntryCount);
            Check("第 10 条因字节上限被丢弃", r10.DroppedCount == 1, "DroppedCount=" + r10.DroppedCount);
            Check("output 不超过 MaxOutputBytes",
                r10.Output != null && r10.Output.Length <= UEExecLogCapture.MaxOutputBytes,
                "len=" + (r10.Output == null ? 0 : r10.Output.Length));
            Check("超字节时 truncated=true", r10.DroppedCount > 0, "DroppedCount=" + r10.DroppedCount);

            var meta3 = r10.BuildLogCaptureData();
            Check("logCapture 暴露 outputBytes", meta3.ContainsKey("outputBytes"));
            Check("outputBytes 不超过 MaxOutputBytes",
                meta3["outputBytes"] is int b10 && b10 <= UEExecLogCapture.MaxOutputBytes,
                "outputBytes=" + meta3["outputBytes"]);

            // 11b) 单条就超过整个预算 ⇒ 整条丢弃，且不做「半条腰斩」
            var c10b = new UEExecLogCapture();
            c10b.AddUnityLog(new string('z', UEExecLogCapture.MaxOutputBytes + 1), UEExecLogSeverity.Info);
            var r10b = c10b.Parse();
            Check("单条超预算整条丢弃（不半截输出）",
                r10b.Output == null && r10b.EntryCount == 0 && r10b.DroppedCount == 1,
                "Output=" + (r10b.Output == null ? "<null>" : "len" + r10b.Output.Length)
                    + " EntryCount=" + r10b.EntryCount + " DroppedCount=" + r10b.DroppedCount);

            // 小输出不受影响（防止把正常输出也砍了）
            var c11 = new UEExecLogCapture();
            c11.AddUnityLog("short line", UEExecLogSeverity.Info);
            var r11 = c11.Parse();
            Check("小输出不受字节上限影响",
                r11.Output == "short line" && r11.DroppedCount == 0,
                "Output=" + (r11.Output ?? "<null>") + " DroppedCount=" + r11.DroppedCount);
            Check("小输出的 outputBytes 正确",
                c11.Parse().BuildLogCaptureData()["outputBytes"] is int b11 && b11 == "short line".Length,
                "outputBytes=" + c11.Parse().BuildLogCaptureData()["outputBytes"]);

            // 边界：恰好等于上限的输出不截断
            var c12 = new UEExecLogCapture();
            c12.AddUnityLog(new string('y', UEExecLogCapture.MaxOutputBytes), UEExecLogSeverity.Info);
            var r12 = c12.Parse();
            Check("恰好等于上限不截断",
                r12.Output != null && r12.Output.Length == UEExecLogCapture.MaxOutputBytes
                    && r12.DroppedCount == 0,
                "len=" + (r12.Output == null ? 0 : r12.Output.Length) + " dropped=" + r12.DroppedCount);
```

**不需要新增任何 `using`** —— 上面这段只用到 `UEExecLogCapture` / `UEExecLogSeverity` / `StringBuilder` 之外的基本类型（`new string(...)` 走 `System`），`using System;` 已在任务 1 步骤 2 加好。

- [ ] **步骤 2：运行测试验证失败**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
dotnet run --project Source/ExecLogCaptureTests
```

预期：编译失败，报 `CS0117: 'UELoader.UEExecLogCapture' 不包含 'MaxOutputBytes' 的定义`。

- [ ] **步骤 3：实现字节上限**

`Source/UELoader/UEExecLogCapture.cs` 三处改动。

**(a) 常量** —— 在 `public const int MaxEntries = 512;` 之后加：

```csharp
        /// <summary>
        /// 单次执行 output 的字符数上限（[20] 实测单条 70 行日志 ≈ 7000 字符，
        /// 条数上限挡不住「条少但每条巨大」）。
        /// </summary>
        public const int MaxOutputBytes = 64 * 1024;
```

**(b) 状态字段** —— 与 `int dropped;` 并列加：

```csharp
        int droppedBytes;
```

**(c) `Add` 方法替换** —— 原 `Add` 改为字节 + 条数双重判据：

```csharp
        void Add(UEExecLogEntry entry)
        {
            lock (entries)
            {
                if (entries.Count >= MaxEntries) { dropped++; return; }
                // 字节上限：单条就可能远超预算（实测单条 70 行日志 ≈ 7KB）。
                // 整条放不下就整条丢弃，不做半条截断 —— 半条会产出「看起来完整其实被腰斩」的日志，更危险。
                if (droppedBytes + entry.Message.Length > MaxOutputBytes) { dropped++; droppedBytes += entry.Message.Length; return; }
                entries.Add(entry);
                droppedBytes += entry.Message.Length;
            }
        }
```

**(d) `Parse` 的 `outputBytes`** —— 把 `Parse()` 里 `return new UEExecLogResult { ... }` 中 `DroppedCount = droppedSnapshot + sentinelDrops,` 之后补一行 `OutputBytes = sb.Length,`（`UEExecLogResult` 需新增该字段，见 (e)）。

**(e) `UEExecLogResult` 新增字段 + 元数据**：

```csharp
        /// <summary>本次 output 的字符数（供调用方判断是否接近上限）。</summary>
        public int OutputBytes;
```

`BuildLogCaptureData()` 里，在 `{ "droppedCount", DroppedCount },` 之后加一行：

```csharp
                { "outputBytes", OutputBytes },
```

- [ ] **步骤 4：运行测试验证通过**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
dotnet run --project Source/ExecLogCaptureTests
dotnet run --project Source/ConfigJsonTests
```

预期：全部 `PASS`，末行 `ALL PASS`，退出码 `0`。

- [ ] **步骤 5：构建并游戏内验收**

停游戏后：

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
dotnet build Source/UELoader/UELoader.csproj
```

启动游戏进图后，执行任务 3 步骤 2 的准备，然后：

```powershell
# 单条 100KB 的多行日志：条数=1，必然触发字节上限
$code = 'var sb = new System.Text.StringBuilder(); for (int i = 0; i < 4000; i++) sb.AppendLine("PADDING_LINE_" + i + " " + new string((char)65 + (i % 26), 20)); UnityEngine.Debug.Log(sb.ToString()); 0'
```

用 `agg_call_tool { tool: "execute_csharp_code", args: { "code": "<上面的 $code>" } }` 执行。

| 断言 | 期望 |
|---|---|
| 响应 `data.logCapture.outputBytes` | `≤ 65536` |
| 响应 `data.logCapture.truncated` | `true` |
| 响应 `data.logCapture.droppedCount` | `≥ 1` |
| 响应 `data.output` | 长度 `≤ 65536` |
| 游戏不卡死、日志不风暴 | 帧率无明显掉档 |

- [ ] **步骤 6：改动清单**

```
修改 Source/UELoader/UEExecLogCapture.cs（MaxOutputBytes 常量、droppedBytes 字段、Add 双重判据、OutputBytes 字段、BuildLogCaptureData 增 outputBytes）
修改 Source/ExecLogCaptureTests/Program.cs（追加 11b/11 段的 9 条用例，不新增 using）
未改动 UEHttpHandler.cs（§0.4 的观察者硬约束原样保留，环路零加边性质不变）
未改动 MCP/index.js（文案已在任务 4 写好 outputBytes / 64KB）
```

---

### 任务 7：`CS0127` 的编译失败归因（修 `:4536-4537` 的误导 guidance）

**文件：**
- 修改：`MCP/index.js:4536-4537`
- 测试：`MCP/test/toolchainFixes.test.js`（新增一条用例）

> **为什么必须做**（§0.1b）：`'CS0127'.includes('CS0')` 为真 ⇒ 用户写 `return "…";` 拿到的 guidance 是「默认没有 using 引用，请用全限定名或 `add_using_directive`」，**把用户引向一个完全无关且并不存在的维度**。`:2937` 的 `notes` 说得再对，也救不了这一步 —— guidance 是**失败响应里最显眼的那条**，它会盖过 notes。
> **与任务 4 的分工**：`:2937` 管「值怎么回传」（写法），`:4536-4537` 管「编译报错怎么归因」（解释）。两处都要改。

> 🚧 **本任务只修 `CS0127` 一个错误码，`else if` 兜底分支原样保留 —— 这是刻意为之，不是遗漏。**
>
> 兜底分支对 **`CS0103` / `CS0234` / `CS0121` / `CS0117` / `CS1061` / `CS0411` / `CS1503` / `CS0841` 这 8 个错误码仍会误导向「缺 using」**，而 [33][35] 记录的实测踩坑（`CS0234`/`CS1061`/`CS1503`/`CS0411`/`CS0121`/`CS0841`）主要就出在这串错误码上。**这段兜底由另一份计划接管：**
> `docs/issue-triage/plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md`（[33][35] 的计划）—— 它会补 **9 个错误码的路由表 + `:4536-4537` 的接线**（9 个 = 上述 8 个 + `CS0127`）。
>
> **衔接规则**：该计划的任务 2 步骤 1 已写明「谁先落地都合法，后落地者 `edit` 匹配不上会报错、文件不动」⇒ **本计划与它可以任意先后落地，不需要协调。**
>
> ⚠️ **若只执行本计划、不执行那份计划**：`CS0127` 会被正确归因，但**其余 8 个错误码的误导向依旧存在**。执行者不要以为任务 7 已经解决了全部归因问题。

- [ ] **步骤 1：写失败的测试**

在 `MCP/test/toolchainFixes.test.js` 末尾追加：

```javascript
// ===========================================================================
// execute_csharp_code：CS0127 必须被单独归因，不能落进 CS0x 兜底分支（计划 02 任务 7）
// 宿主 InteractiveHost 是 void，'CS0127'.includes('CS0') 为真 ⇒ 旧代码会把
// 「不要写 return 表达式」误导成「缺少 using 引用」。
// ===========================================================================
test('§7 execute_csharp_code：CS0127 走独立归因，不落进 CS0x 的 using 兜底分支', () => {
  const block = source.slice(
    source.indexOf("case 'execute_csharp_code'"),
    source.indexOf("case 'reset_csharp_console'"),
  );
  // 必须先判 CS0127
  assert.match(block, /CS0127/, '必须显式识别 CS0127，否则 guidance 会误导');
  // CS0127 的分支必须在 CS0x 兜底分支之前
  assert.ok(
    block.indexOf('CS0127') < block.indexOf("includes('CS0')"),
    'CS0127 分支必须排在 CS0x 兜底分支之前，否则永远命中兜底',
  );
  // CS0127 的归因文本必须说清「宿主 void / 不要写 return 表达式」
  assert.match(block, /InteractiveHost/, '归因文本要点名 InteractiveHost');
  assert.match(block, /return/, '归因文本要提到 return 表达式');
});

test('§7 execute_csharp_code：CS0127 的 guidance 不得建议 add_using_directive', () => {
  const block = source.slice(
    source.indexOf("case 'execute_csharp_code'"),
    source.indexOf("case 'reset_csharp_console'"),
  );
  const i = block.indexOf('CS0127');
  const j = block.indexOf("includes('CS0')");
  // 取 CS0127 那一支的 guidance 文本（CS0127 分支结束前的第一个 r.guidance 赋值）
  const cs0127Branch = block.slice(i, j);
  assert.ok(cs0127Branch.length > 0, 'CS0127 分支必须存在');
  assert.doesNotMatch(
    cs0127Branch,
    /add_using_directive/,
    'CS0127 的归因绝不能建议 add_using_directive —— 那会把用户引到无关维度',
  );
});
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：新增的 2 条 **FAIL**（`CS0127` 尚未出现在 `index.js`）。

- [ ] **步骤 3：加 CS0127 独立归因分支**

`MCP/index.js:4536-4537` 两行整段替换为（**新分支在前，CS0x 兜底在后**）：

```javascript
        // 编译失败归因 —— 顺序要紧：CS0127 必须先判。
        // 宿主 InteractiveHost 是 void（docs/mcp-吐槽板.md:219-223），写 return <表达式>;
        // 必然报 CS0127。它不是「缺 using」，旧代码的 includes('CS0') 兜底会把它误导到 using 维度。
        // ⚠️ 本分支的文案**不得出现 add_using_directive 字样** —— 任务 7 步骤 1 的用例 2 会因此断言失败。
        const csErr = typeof r.error === 'string' ? r.error : '';
        if (!r.success && csErr.includes('CS0127')) {
          r.guidance = '宿主方法 InteractiveHost 是 void：不要写 return <表达式>（只有 return; 合法）。'
            + '要让值回到 result，请把值写成末尾裸表达式，例如 var sb = new System.Text.StringBuilder(); …; sb.ToString();。'
            + '这不是命名空间缺失问题。';
        } else if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || csErr.includes('CS0'))) {
          r.guidance = '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';
        }
```

> ⚠️ **上面 `else if` 那一支的文案本计划刻意原样保留、没有改** —— 它由另一份计划接管（见本任务开头的「范围边界」）。此处保留只是为了不扩大改动面。

- [ ] **步骤 4：运行测试验证通过**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
node --check index.js
```

预期：`npm test` 全 `pass`（含既有 §3 护栏、任务 5 的 3 条、本任务 2 条），`node --check` 无输出、退出码 `0`。

- [ ] **步骤 5：改动清单**

```
修改 MCP/index.js:4536-4537（新增 CS0127 独立归因分支并前置；CS0x 兜底降为 else if，文案原样未动）
修改 MCP/test/toolchainFixes.test.js（追加 2 条用例）
未改 :2937（在任务 4 改）、未改 :2072/:2078（属计划 12 任务 1 的改动面）
未改 else if 兜底分支的文案（由 plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md 接管）
未改任何游戏侧文件；未触碰 toolListLine / buildMenuNode / attachUsageOnFailure
```

- [ ] **步骤 6：本任务**不**处理的两处已知遗留（登记在案，别误以为已修）**

| 遗留 | 现状 | 归属 |
|---|---|---|
| **运行时报错被标成编译失败** | `else if` 里的 `r.errorCode === 'COMPILATION_ERROR'` 会把 `Exception invoking REPL`（**变体 5 = 运行期异常**，`ConsoleController.cs:189`）也归类为「编译失败」，然后套上「缺 using」的错误归因 | 由 `2026-10-02-33-35-…` 计划一并处理。**本计划先落地 ⇒ 这个错标签会继续存在**（本计划只保证 `CS0127` 这一条不再被误导向 using；`CS0127` 走的是自己的分支，不受此影响） |
| **其余 8 个错误码的误导向** | `CS0103`/`CS0234`/`CS0121`/`CS0117`/`CS1061`/`CS0411`/`CS1503`/`CS0841` 仍落进 using 兜底 | 同上。路由表共 **9 个**错误码 = 这 8 个 + `CS0127`（本计划覆盖的 1 个） |

---

## 4. 自检记录

| 检查项 | 结果 |
|---|---|
| **规格覆盖度** | 范围（让 `output` 覆盖用户日志）→ 任务 1+2；环路规避（§0.4 四条）→ 任务 2 步骤 2/3/4 + 任务 3 步骤 6；裁决报告建议 2（返回里声明捕获范围）→ 任务 2 步骤 4 的 `logCapture`；**[20] 并入** → 哨兵剔除（§0.5c / 任务 1）+ `includeOutput`（任务 5）+ 字节上限（任务 6）；**[34] 并入** → `void` 宿主定案（§0.8）+ 双份回显裁定（§0.9）+ 矩阵第 6/7 行（任务 3）+ 文案点名「不要写 `return 表达式`」（任务 4 / 任务 5 description）；跨计划兼容 → 任务 1 步骤 2 用例 2a/2b 双哨兵断言 + §0.6 |
| **占位符扫描** | 全文无「待定 / TODO / 后续实现 / 补充细节 / 添加适当的错误处理 / 为上述代码编写测试 / 类似任务 N」；每个代码步骤都有完整可编译代码块，每条命令都有期望输出。**原「实测记录表」的两格已按 [34] 填入权威答案**（第 6 行 = `CS0127`/`InteractiveHost`），第二格改写为「待 mcs→Roslyn 切换后复核」并给出了复核触发条件，**不再是空白实现占位** |
| **`output` 语义单一定义** | `output` = 「本次执行期间用户代码自己写下的日志原文，去回声、去哨兵、受 512 条 + 64KB 双重限幅」。`result` = 「末尾裸表达式的值」。两者是**不同契约**，`[34]` 所说的「同一段文本既在 output 又在 result」是用户自己写了两遍，**不是重复缺陷**（§0.9）。任务 3 矩阵第 7 行把这条判据钉死 |
| **类型一致性** | `UEExecLogCapture.MaxEntries` / `.MaxOutputBytes` / `.AddUnityLog(string, UEExecLogSeverity)` / `.AddUnityExplorerLog(string, UEExecLogSeverity)` / `.Parse()` / `UEExecLogResult.{Result,Error,Output,EntryCount,DroppedCount,OutputBytes}` / `.BuildLogCaptureData()` 在任务 1 的实现、任务 1 的测试、任务 6 的实现与测试、任务 2 的调用点全部一致；`MapSeverity(LogType)` 与 `LogTypeName(LogType)` 在任务 2 步骤 3 定义、步骤 4 使用，一致 |
| **行号复核** | §0.1 表中全部行号、任务 2 标注的 `UEHttpHandler.cs:26-32 / 39-61 / 66-95 / 163-245`、任务 5 标注的 `MCP/index.js:2073-2082 / 4538-4539`、任务 3 标注的 `UEHttpHandler.cs:168-169 / 192`、`MCP/index.js:2937 / 3817 / 4006-4019 / 4461-4469`、`MCP/test/toolchainFixes.test.js:121`、`MCP/package.json:9`、`Source/Directory.Build.props:21-26`、`UELoader.csproj:92-96` 均于 2026-10-01 用 `Get-Content` 逐条打印复核；`ConsoleController.cs:183/185/189/203/205/211/216/158-218`、`ExplorerCore.cs:88-127`、`ExplorerStandalone.cs:24,43-45`、`MonoHelper.cs:9,12-13`、`ConfigManager.cs:119-121`、`config.cfg:11`、UE 日志 `…155526Z.txt:7-8`、Roslyn 计划 `:1450/:1514/:1516/:1519-1521/:2059-2063/:2069-2084` 同样复核 |
| **路径复核** | `ConsoleController.cs` 与 `ScriptEvaluator.cs` 在 **`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/`**，**不在 `McpRimDebug/`**（该目录无这两个文件）。本计划正文引用 `ConsoleController.cs` 时一律不写目录名歧义；需要定位时用完整路径。triage 20 `:128-130` 与吐槽板 `:82` 同结论 |
| **行号漂移兜底** | 若实施时行号已漂移，以现场 `Get-Content` 到的行号为准；每个任务步骤里都写明了要替换的**代码片段内容**（不是只给行号），按内容定位即可 |
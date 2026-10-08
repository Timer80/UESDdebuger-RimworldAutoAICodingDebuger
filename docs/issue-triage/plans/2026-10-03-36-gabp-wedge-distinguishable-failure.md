# GABP 通道楔死时的「可判别失败」实现计划（吐槽板 [36]）

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 当 RimBridgeServer 侧 Unity 主线程楔死、导致 `rimworld.*` / `rimbridge.*` 镜像工具连续 `tools/call` 超时时，MCP 侧不再「再发一次、等它再超时一次」，而是**停止发起新调用**并返回一条**结构化**错误：既点名「重连 TCP 无效、请重启游戏」，又把**每一个被放弃的工作项**标成「**排队中**（很可能仍在游戏侧 `RimBridgeMainThread.Pending` 队列里，恢复后会补执行）」还是「**已丢弃/已执行**（FIFO 排空已被后续成功响应证明）」——因为**超时 ≠ 取消**，现在的返回把「报失败却会晚点做」这件事完全藏起来了。

**架构：** 全部改动只在 MCP 侧三处，不碰 RimBridgeServer 一个字节、不碰任何 `.cs`。① `MCP/index.js` 在既有假死计数（`:2775-2785`）之上加一个**楔死闩锁**（`gabpWedged` + 探针窗口），置位后 `callGABPTool` 在**发起调用之前**短路返回 `GABP_CHANNEL_WEDGED`；② 闩锁期间维护一条**被放弃工作项**的短环形记录（工具名 + 序号 + 时间），每条带 `disposition` 判定，判定依据是「自该次放弃之后，本通道有没有出现过成功的 GABP 响应」——`RimBridgeMainThread.Pump()`（06000E76:M）是严格 FIFO 的 `while{ dequeue; execute }`，所以后发的那次调用能成功，就证明先发的那一项**已经出队执行过**；③ `MCP/gabpClient.js:566` 加注释把这个「超时只是放弃本地等待」的语义**写在代码里**，防止下一个人把它当成安全超时。响应体沿用仓库既有的 `contentErrorResponse` 形状（`success` / `errorCode` / `message`，见 `AREA_OP_CHANNEL_BUSY`），机器可读部分走一个**可选第三参**挂上去，不新造第二套错误格式。

**技术栈：** Node.js **v24.13.0**（实测版本）、ESM（`MCP/package.json:6` 的 `"type":"module"`）、Node 内置测试运行器（`MCP/package.json:9` 的 `"test": "node --test \"test/*.test.js\""`）、`node:test` + `node:assert/strict`。**不引入任何依赖、不改任何 `.cs`、不重编译模组、不构建、不部署、不启动游戏。**

---

## 文件结构

| 文件 | 动作 | 职责（改完之后它负责什么） |
|---|---|---|
| `MCP/index.js` | **修改**（4 处） | ①`:2517-2518` 之后新增楔死闩锁与放弃记录的**模块级状态**；②`:2705-2707` `contentErrorResponse` 加**可选第三参 `detail`**（纯附加，14 个既有 2 参调用点输出逐字节不变）；③`:2747-2755` 之后、`:2756` 之前加**短截断**（闩锁期内不再发起 `tools/call`）；④`:2775-2785` 阈值分支加**置位/重置**。职责：把「GABP 通道楔死」变成一个**可判别、可行动、且不制造副作用**的失败态 |
| `MCP/index.js:5450-5459` | **修改**（并入上面的 ①） | `_testHooks` 增加 `getGabpWedgeState` / `resetGabpWedge` / `openGabpWedgeProbeWindow`，让离线测试能直接观测与驱动闩锁，不必真的等 20 秒 |
| `MCP/gabpClient.js` | **修改**（1 处，`:566-569`） | **只加注释，不改行为**。把「此处只放弃本地等待、**不取消游戏侧**、被放弃的 `tools/call` 仍会执行」写进 `_request` 的 `setTimeout` 回调。成本近零，是防止误读的最重要一行改动 |
| `MCP/test/gabpWedge.test.js` | **新建** | 本计划全部离线断言：闩锁状态机（任务 2）、端到端 fail-fast 与结构化错误（任务 3）、源码切片锚点护栏（任务 4）。文件名刻意**避开**计划 32 已占用的 `MCP/test/mirrorLongWaitBudget.test.js` |

**本计划明确不碰：** `MCP/longTask/timeoutPolicy.js`（一个字都不改 —— 它的三张白名单是计划 32 的战场，且 §3.3-A 已证明「把预算翻倍」对本条无效）、`MCP/config.json`、任何 `.cs`、任何 `Assemblies/` 或 `runtime/` 下的东西（`runtime/` 下**无** `index.js` 副本，改 `MCP/index.js` 即改到生效那份，**无需构建部署**）、`docs/` 下除本计划外的任何文件。

---

## 0. 行号复核（本计划所有行号均已逐条回读，**以本节为准**）

> **复核方法（重要）：** 本节所有行号都用
> `[System.IO.File]::ReadAllLines('<绝对路径>')[<n>-1]` 复核过，**不是**用带行号的文件阅读工具的输出
> （那种输出会随编辑漂移 —— `docs/issue-triage/AGENT-BRIEF.md` §3.5 记着它少报过 27 行、且同文件内偏移不一致）。
> 执行者若要再核实，用同样方式，**不要相信任何「读到的行号」**；数总行数也用 `ReadAllLines(...).Count`
> （`Get-Content | Measure-Object -Line` 会少算）。
>
> 复核时确认：`MCP/index.js` **TOTAL = 5460 行**；`MCP/gabpClient.js` **TOTAL = 727 行**；
> `MCP/longTask/timeoutPolicy.js` **TOTAL = 154 行**；`MCP/test/toolchainFixes.test.js` **TOTAL = 294 行**；
> `MCP/test/index.integration.test.js` **TOTAL = 310 行**；`MCP/package.json` 的 `scripts.test` 在第 **9** 行；
> `node --version` = **v24.13.0**。

### 0.1 本计划要改的位置（全部已复核）

| 对象 | 用途 / 行号（已复核） | 任务 |
|---|---|---|
| `MCP/index.js:2516-2518` | 既有假死检测三行：`:2516` 注释、`:2517` `let gabpConsecutiveTimeout = 0;`、`:2518` `const GABP_TIMEOUT_RECOVER_THRESHOLD = (config?.rimBridge?.timeoutRecoverThreshold) \|\| 3;`。**「连续 3 次」的出处**。新的闩锁状态块紧接 `:2518` 之后 | 2 |
| `MCP/index.js:2705-2707` | `contentErrorResponse(errorCode, message)` 三行。**加一个可选第三参 `detail`**（14 个既有调用点全是 2 参，输出逐字节不变 —— 见 §0.6） | 3 |
| `MCP/index.js:2747-2755` | `callGABPTool` 开头：`:2747` 函数签名、`:2748` `const ready = await ensureGabpConnected();`、`:2749-2751` 未就绪返回、`:2752-2755` `!gabpBridge` 返回。**短截断插在这之后、`:2756` `const callOpts = opts \|\| {};` 之前** | 3 |
| `MCP/index.js:2758-2759` | 成功路径：`:2759` `gabpConsecutiveTimeout = 0; // 成功响应重置超时计数` —— **闩锁在同一行旁边清零** | 2 |
| `MCP/index.js:2775-2785` | 超时分支：`:2776` `gabpConsecutiveTimeout++;`、`:2777` 阈值判定、`:2778` WARN 日志、`:2779` 清零、`:2781` `forceReconnect()`、`:2783-2784` DEBUG 日志 | 2 / 3 |
| `MCP/index.js:2786-2787` | `} else { gabpConsecutiveTimeout = 0; // 非超时失败（如参数错误）不累计` —— **闩锁也在这里清零** | 2 |
| `MCP/index.js:5450-5459` | `export const _testHooks = {` …… `};`。新增 3 个钩子 | 2 |
| `MCP/gabpClient.js:566-569` | `const timer = setTimeout(() => {` / `this._pending.delete(id);` / `reject(this._err(GABP_ERROR_CODES.SERVER_ERROR, \`GABP 请求 '${method}' 超时（${timeoutMs}ms）\`));` / `}, timeoutMs);`。**只加注释，一个字符的行为都不改** | 1 |

### 0.2 只作旁证、本计划**不**修改的位置

| 对象 | 内容 |
|---|---|
| `MCP/gabpClient.js:551-579` | `_request` 全函数。`:563-565` 三档取值（`opts.timeoutMs` → `opts.slow ? _requestTimeoutMs*2` → `_requestTimeoutMs`）；`:570` `this._pending.set(id, {...})`；`:572` **唯一一次** `_writeMessage`（**零重试、零重放**） |
| `MCP/gabpClient.js:309-337` | `forceReconnect()`：`:314-321` 销毁 socket 并置 `null`、`:322-327` reject 全部 `_pending`、`:331` `state = 'idle'`、`:336` `Promise.resolve().then(() => this.ensureConnected())`。**实测帮不上忙**（见 §1.5） |
| `MCP/gabpClient.js:694-696` | `isConnected() { return this.state === 'connected'; }` —— `forceReconnect()` 之后它会短暂为 `false`，**任何依赖它的闩锁清零条件都会被立刻误清**，本计划因此**不用它** |
| `MCP/index.js:2666-2687` | `ensureGabpConnected()`：`:2670` 已连接零成本直通。**短截断放在它之后**（它先跑，才能在游戏被重启时给出「未连接/未就绪」这种更有信息量的错误） |
| `MCP/index.js:2697-2702` | `gabpErrorResponse()` —— `wrapGABPResult(msg, false)` 形状（`success/errorCode:'GABP_ERROR'/error`）。本条**不用**它，要用 `contentErrorResponse`（见 §3.1） |
| `MCP/index.js:2710-2713` | `isGabpErrorTimeout()`，`:2712` 的匹配正则含 `/-32001/`。**本计划不改它** |
| `MCP/index.js:2772` | `const isTimeout = /超时/.test(errMsg) \|\| /-32001/.test(errMsg);` —— 计划 32 会给它加一个 `|| callOpts.expectedLongWait === true` 支路（`plans/2026-10-02-32-…:758-779`）。**本计划不碰这一行** |
| `MCP/index.js:4321-4324` | `AREA_OP_CHANNEL_BUSY` —— 仓库里现成的正面先例，`delete_zone` 的「连接可能繁忙、勿重复并发」。**本计划的响应体形状照抄它** |
| `MCP/index.js:1339` | `getGameStatus()` 里的 GABP 探测 `gabpBridge.callTool('rimworld/get_game_info', {})` —— **直连，不经 `callGABPTool`** ⇒ 闩锁期间 `get_game_status` 照常可用（这正是楔死时最该用的诊断工具） |
| `MCP/index.js:4292` | `start_debug_game_ready` 的幂等探测 `const probe = await gabpBridge.callTool('rimworld/get_game_info', {});` —— 同样**直连，不经 `callGABPTool`** ⇒ 不受闩锁影响。计划 32 任务 3 会给它补 5 秒短预算 |
| `MCP/index.js:5422` | `callGABPTool,` —— **它是被导出的**，任务 3 的测试可不经 `handleToolCall` 直接驱动 |
| `MCP/index.js:5453-5454` | `setGabpBridge` / `getGabpConsecutiveTimeout` —— 现成钩子，**照抄它们的写法**新增本计划需要的 3 个 |
| `MCP/longTask/timeoutPolicy.js:18` | `SLOW_BARE_NAMES = new Set(['load_game','save_game','execute_debug_action','create_hook'])` —— **`execute_debug_action` 早已登记在慢档**，`:147-148` 给它 60000ms |
| `MCP/longTask/timeoutPolicy.js:150` | `return null` —— 未登记工具回落默认（`FALLBACK_DEFAULT_MS = 30000`，`:15`），于是 `get_debug_action` / `list_debug_action_roots` / `list_operations` 拿到 30s |
| `MCP/config.json` | **单行 JSON，只有 5 个键**（`gamePath`/`logPath`/`steamPath`/`appId`/`startTimeout`），**无 `rimBridge`** ⇒ `:2518` 的阈值恒为 3；`gabpClient.js:109` 恒回落到默认 30000 |
| `MCP/node_modules/@modelcontextprotocol/sdk/dist/esm/shared/protocol.js:8` | `export const DEFAULT_REQUEST_TIMEOUT_MSEC = 60000;` |
| `MCP/node_modules/@modelcontextprotocol/sdk/dist/esm/shared/protocol.js:712-713` | `const timeout = options?.timeout ?? DEFAULT_REQUEST_TIMEOUT_MSEC;` / `cancel(McpError.fromError(ErrorCode.RequestTimeout, 'Request timed out', { timeout }))` —— **`-32001` + `Request timed out` 在本仓库的唯一出处**，是**宿主客户端**的截止线，与本仓库产出的 `GABP_ERROR` 是两回事 |

### 0.3 源码切片断言的风险评估（改前必读）—— 结论：**碰不到**

`MCP/test/` 下共有 6 个测试文件读 `MCP/index.js` 的源码，其中 3 处是**按字符切片**的断言。已用 `Select-String -Pattern '<锚点>'` 逐个复核锚点行号（AGENT-BRIEF §3.5 要求的做法）：

| 断言 | 位置 | 切片范围（锚点行号已复核） | 本计划是否碰得到 |
|---|---|---|---|
| `toolListLine` → `buildMenuNode` | `test/toolchainFixes.test.js:121` | `index.js:3281` → `index.js:3290` | ❌ **不碰。** 本计划的 4 处改动全在 `:2516-2518` / `:2705-2707` / `:2747-2756` / `:2758-2787` / `:5450-5459`，**全部在 `:3281` 之前**，切片逐字节不变 |
| `case 'start_game'` / `case 'stop_game'` | `test/toolchainFixes.test.js:263-268` | `index.js:4356` → `:4371` → `:4384` | ❌ **不碰**（全在切片之后） |
| `dpa_patch_methods` 前置分支 | `test/toolchainFixes.test.js:255-260` | `index.js:4337` 起**固定 700 个字符**（≈ 到 `:4350`） | ❌ **不碰。** 切片起点是**相对偏移** `indexOf(...)`，本计划在 `:2747` 之后新增的行只会把它整体后移，**内容不变** |
| `start_quick_test` 链路切片 | `test/quickTest.test.js:28` | `index.js:1430` → `:1675` | ❌ **不碰** |
| GABP 镜像分支切片 | `test/quickTest.test.js:47` | `index.js:3759`（`if (isGabpMirrorName(name)) {`）→ `:4355`（`switch (name) {`） | ❌ **不碰**。本计划**不在** `:3759-4355` 之间加任何含 `start_quick_test` 的行；`:3759` 仍是 `indexOf` 的**首次**命中，`:4412` 的 `case 'start_quick_test'` 仍在它之后 ⇒ `:45` 的 `caseAt > gabpBlockAt` 断言不受影响 |
| `start_quick_test` 工具描述切片 | `test/quickTest.test.js:52` | `index.js:1941` → `:1954` | ❌ **不碰** |
| 整文件正则（`hotreloadConcealment.test.js:66-68,75-77`） | — | 全文件 `assert.match(source, …)` | ❌ 本计划新增的标识符 `gabpWedged` / `GABP_CHANNEL_WEDGED` 不与其中任何正则冲突（已复核：`hotreloadConcealment.test.js` 的正则涉及 `needResume` / `hotreload_*` / `isConcealedTool` / `hotreloadWrap`） |

> ⚠️ 但切片**锚点本身**有一个真实的、计划 32 会引发的风险 —— 见下一节。

### 0.4 🚨 与计划 32 的行号耦合（本计划复核时新发现，**执行前必读**）

**`plans/2026-10-02-32-gabp-per-call-timeout-derivation.md` 的任务 2 会把 `MCP/index.js:2772-2774` 这 3 行替换成 7 行**（该计划 `plans/2026-10-02-32-…:762-779` 的 diff，以及它自己的改动清单 `:798` 自报「+5 行 / -1 行」）。**净增 4 行 ⇒ 计划 32 一旦落地，本计划 §0.1 里所有 `:2775` 及其后的行号全部 +4。**

- 两份计划**不冲突**（32 明文声明「**不要动 `:2775-2785` 的计数逻辑**」，见 `plans/2026-10-02-32-…:781`；它只改 `:2773` 与 `:4292`），但**行号会互相漂移**。
- **执行本计划前必须做的事**：
  1. 先跑 `[System.IO.File]::ReadAllLines('…\MCP\index.js').Count` 看总行数。**若已 > 5460**，说明计划 32 已落地；
  2. 若是，**不要**照抄本计划 `:2775-2785` 这些行号，改用**文本锚点**定位：
     - 阈值分支起点 = `source.indexOf('} else if (isTimeout) {')` 那一行
     - 成功路径清零点 = `source.indexOf('gabpConsecutiveTimeout = 0; // 成功响应重置超时计数')`
     - 非超时清零点 = `source.indexOf('gabpConsecutiveTimeout = 0; // 非超时失败（如参数错误）不累计')`
     - 短截断插入点 = `source.indexOf('const callOpts = opts || {};')` 所在行的**上一行末尾**
     - 钩子块 = `source.indexOf('export const _testHooks = {')`
  3. §0.3 的所有切片断言**不依赖行号**（它们用 `indexOf` 取锚点），所以计划 32 的 +4 位移**不会**弄碎它们 —— 但**本计划 §0.1 的行号会全部失效**。
- **建议执行顺序**：32 → 36（本计划）。若 36 先做，32 后做时它自己也要按上面第 2 条重定位。

### 0.5 命名冲突预检

对 `MCP/` 下**排除 `node_modules`**、扩展名 `.js` / `.json` / `.md` 的**全部 26 个文件**做逐字（`-SimpleMatch`，非正则）检索：

| 待引入的标识符 | 命中数 | 结论 |
|---|---|---|
| `GABP_CHANNEL_WEDGED` | **0** | 新错误码，全仓库无同名 |
| `gabpWedged` | **0** | 新状态变量，无同名 |
| `AREA_OP_CHANNEL_BUSY` | **1**（`index.js:4322`） | 形状参照物，确认只有一处先例 |

⇒ **本计划引入的两个新标识符与全仓库零冲突。**（检索范围已写明，避免「局部检索当全局结论」。）

### 0.6 `contentErrorResponse` 加可选第三参的兼容性论证

`MCP/index.js` 里 `contentErrorResponse` 共 **14 个调用点**（`:2736` / `:4112` / `:4136` / `:4147` / `:4165` / `:4189` / `:4192` / `:4199` / `:4205` / `:4254` / `:4269` / `:4322` / `:4341` / `:4578`），**全部是 2 参调用**。

加第三参写成 `...(detail || {})`（`detail` 为 `undefined` 时展开成 `{}`，`JSON.stringify` 不产生任何键）⇒ **14 处既有输出的 JSON 逐字节不变**。这一点由任务 3 的回归断言守住（`AREA_OP_CHANNEL_BUSY` 的文本必须与改动前完全一致）。

---

## 1. 背景：板上归因三处皆错，本计划改的到底是什么

> **执行者请先读完这一节再动手。** 上游裁决报告（`triage/36-debug-action-family-timeout-misattribution.md`，328 行）已经把板上 `[36]` 的每一条论断都实测过了。**照板上原文去修，会修错地方。**

### 1.1 现象为真，但不是「整个调试动作族全灭」

实测日志 `MCP/logs/mcp-20261001-222618.log` 里有 6 条真实失败记录（`:1494` / `:1498` / `:1531` / `:1571` 是 `execute_debug_action` 的 60s 超时，`:1535` / `:1540` 是 `get_debug_action` / `list_debug_action_roots` 的 30s 超时；另加与之同窗口的 `:1503` `rimbridge.list_operations` 30045ms、`:198` `rimworld.select_pawn` 30038ms），全落在 22:36–22:55 约 20 分钟的一个窗口里。同一份日志里 `rimworld.execute_debug_action` **成功 146 次**（35ms–5.2s），`rimworld.get_debug_action` 还有一次 **16ms 成功**（`:862`）—— 就是板上点名「全灭」的那个工具。

⇒ 单看 `execute_debug_action` 一族，失败 5 / 成功 146 ≈ **3.3%**，且 5 次全在同一个时限窗口内。**这是「一段时限的通道故障」，不是「这族工具坏了」。**

### 1.2 不是「一律 30s」

`execute_debug_action` **早在 `MCP/longTask/timeoutPolicy.js:18` 的 `SLOW_BARE_NAMES` 里登记过**，经 `:147-148` 拿到 **60000ms**。实测四条 `execute_debug_action` 失败是 **60024 / 60010 / 60027 / 60041 ms** —— 全部贴着 60 秒预算。

**⇒ 板上 `:59` 那一带的「30s」对 `execute_debug_action` 是逐字错误；而把它「登记进白名单 / 把预算翻倍」当修法更是错药 —— 60 秒不够是因为游戏侧压根没回包，不是因为等得不够久。**（裁决报告 §3.3 的证据 A：同一份日志里 `get_debug_action` 30 秒预算下 **16ms 就回来了**。）

### 1.3 真正的分界是「GABP 通道 vs 非 GABP 通道」，不是「DebugAction vs 非 DebugAction」

| 通道 | 工具 | 楔死窗口内 |
|---|---|---|
| GABP（RBS） | `rimworld.execute_debug_action` / `get_debug_action` / `list_debug_action_roots` | ❌ 60s / 30s 超时 |
| GABP（RBS） | **`rimbridge.list_operations`**（**`Immediate` 分支，跟 DebugAction 毫无关系**） | ❌ 30045ms 超时（日志 `:1503`） |
| GABP（RBS） | `rimworld.select_pawn` | ❌ 30038ms 超时（日志 `:198`） |
| 原生 UE HTTP | `search_debug_actions`（`index.js:4949` 的 `/debugaction/search`，**GET**）、`execute_csharp_code` | ✅ 410ms 成功（日志 `:1546`） |
| 原生 | `start_quick_test` / `get_game_status` | ✅ 63068ms / 370ms 成功 |
| 本地文件 | `read_rimworld_log` | ✅ 不经任何通道 |

**⇒ 共同因子只有「这次 GABP 调用」一个。**

⚠️ 板上「同一时刻 `pause_game` 正常」**在日志里查无对应记录**：最近一次 `pause_game` 是 `[22:52:08.593]` 58ms success（日志 `:1529`），落在窗口**开始之前**；下一次在窗口**结束之后**（`:1601`）。而且 `pause_game` 本身就是 **GABP 镜像**（`index.js:3027`）—— 对照组和实验组走同一条通道，不构成对照。

### 1.4 🚨 内核：超时根本不取消动作。报失败，却会晚点做

反编译 `RimBridgeServer.dll`（MVID `46e27953d7324877a067e7458bf2c0d5`）：

```
RimBridgeMainThread.InvokeAsync<T>  (06000E79:M)
  if (IsMainThread) return func();
  var workItem = new MainThreadWorkItem<T>(func);
  lock (Sync) { Pending.Enqueue(workItem); }
  return await workItem.Completion;        // ← 无超时、无 deadline

RimBridgeMainThread.Pump()               (06000E76:M)
  while (true) {                          // ← 严格 FIFO
    lock (Sync) { if (Pending.Count == 0) break; work = Pending.Dequeue(); }
    work.ExecuteIfPending();
  }

ExecuteDebugActionResponse              (060010B9:M)
  … node.action?.Invoke()                // ← 同步调用，没有 timeoutMs 参数
```

对照 `MCP/gabpClient.js:566-569`：那个 `setTimeout` **只**做 `this._pending.delete(id)` + `reject(...)`，**不发任何取消报文**（GABP 协议里也没有这种消息 —— `Lib.GAB` 的方法集实测只有 7 个）。

**⇒ 时序是：**

```
t=0     MCP 发 tools/call(A)，游戏侧把 A 塞进 Pending
t=60s   MCP 的 setTimeout 到点 → 本地放弃 → 报错「超时」
        ⚠️ 游戏侧的 A 仍在 Pending 里，一个字都没少
t=90s   用户重试 → MCP 发 tools/call(B) → 又塞进 Pending
t=95s   用户再重试 → tools/call(C) → 又塞进 Pending
t=8min  主线程恢复 → Pump() 依次取出 A、B、C → 全部执行
```

**这才是本条真正的内核，比板上写的严重：板上说「超时无法与『正在执行』区分」，方向对但低估了 —— 真相是「报了失败，动作仍会以不可观测的方式补执行」。**

**⇒ 而且缺 fail-fast 会让它成倍恶化：每多发一次 `tools/call`，就等于往那个死队列里多塞一个「稍后必然执行」的工作项。** 现有的 `:2775-2785` 只记一次日志 + 调一次 `forceReconnect()`，然后 `return gabpErrorResponse(...)`（`:2789`）继续正常服务下一次调用 —— 于是一次「重试 5 次」就会留下 5 个待执行项。这就是任务 2 要做闩锁的**唯一**理由。

### 1.5 `forceReconnect()` 是错药（已有实测）

日志 `:1500` 记录了「连续 3 次超时 → 强制重连 → 重连成功」，**紧接着 `:1503` 的 `rimbridge.list_operations` 仍然 30045ms 超时**。

**⇒ 重连 TCP 无效，因为卡的是游戏进程不是 socket。** 本计划**保留** `forceReconnect()`（它是既有自愈机制的一部分，不该删），但**不再指望它**，并把「重连无效，请重启/重载游戏」写进错误文案。

### 1.6 严重度：🟠 维持（板上 🔴 建议下调）

- `gabpClient.js:568` 的错误串 `GABP 请求 'tools/call' 超时（60000ms）` **自曝真实预算**，不像 `[04]` 的 `-32001` 什么都不给；
- 真实后果是「延迟补执行」，会与重试叠加成**重复执行**，但触发前提是调用方在超时后重试；
- 板上 `:86-104` 的替代方案（UnityExplorer REPL + 反射）已完全绕开本条，实测当时就能用（`:1546` 410ms success）。

**⇒ 是真问题、可修、有低成本修法，但不属于「会让自动化得出事实相反结论」的最高档（那是 `[01]` / `[31]`）。**

### 1.7 `-32001` 与 `GABP_ERROR` 是两回事（不要混）

- `-32001` + 逐字文本 `Request timed out` 在本仓库的**唯一出处**是 SDK 的 `protocol.js:713`，是**宿主客户端**的截止线。
  **检索范围**：`MCP/` 下排除 `node_modules`、扩展名 `.js`/`.json`/`.md` 的**全部 26 个文件**，逐字检索 `32001` ⇒ **只有 2 处命中**（`index.js:2712` 与 `index.js:2772`），**两处都是「匹配」用的正则、都不是产出点**；逐字检索 `Request timed out` ⇒ **0 命中**。
- 服务端自己产出的是 `gabpClient.js:568` 的 `GABP_ERROR`（`GABP_ERROR_CODES.SERVER_ERROR` = `-32000`，见 `gabpClient.js:40`），经 `index.js:2789` 包成**工具结果**返回（不是协议错误）。
- **本计划不碰 `-32001`，也不试图消除它** —— 那是宿主客户端的截止线，不在本仓库内（裁决报告 §4.7 明确：**不得并入计划 04**）。

> 📌 **一处精度修正（复核时新发现）：** 裁决报告 §4.7 说「宿主客户端的截止线比 60s 更早先放弃」。按 SDK 默认值 `protocol.js:8` = `DEFAULT_REQUEST_TIMEOUT_MSEC = 60000`，而 `execute_debug_action` 的服务端预算经 `timeoutPolicy.js:147-148` 也**正好是 60000** —— **两者是平局竞态，不是「客户端更早」**。这不影响本计划的任何设计（两种情况下 `-32001` 都会盖住那条自曝预算的 `GABP_ERROR`，与实测日志一致），但下一个读报告的人不该被「更早」两个字误导。

---

## 2. 板上三条已被证伪的建议（**不要实现**）

| 板上建议（`docs/mcp-吐槽板.md` 的 `## [36]` 段） | 裁决 | 依据 |
|---|---|---|
| 「进入调试菜单路径前先探 `Find.WindowStack` 里有没有 `Dialog_DebugSettings` / dev-mode 是否开，不可用就立刻返回明确错误」 | ❌ **对准了一个不存在的门禁** | `EnsureNodeGraph`（`060010BD:M`）的函数体就是 `private static void EnsureNodeGraph() { Dialog_Debug.TrySetupNodeGraph(); }` —— **无条件调、不查任何前置**。`ListDebugActionRootsResponse`（`060010B5:M`）里的 `devModeEnabled = Prefs.DevMode` 只是**返回值里的一个字段**。实测日志里 dev mode 一直是好的（146 次成功 + `search_debug_actions` 查得到 GOD 动作） |
| 「与 [01]/[25] 合并处理」 | ⚠️ **同属「返回值与事实相反」，但方向相反、不合并** | `[01]` 是「**报成功却没做**」，本条是「**报失败却会晚点做**」。修法完全不同：`[01]` 要补执行证据，本条要治**取消语义 + 楔死处置** |
| 把 `get_debug_action` / `list_debug_action_roots` / `list_operations` 加进 `timeoutPolicy.js` 白名单（把 30s 抬到 120s） | ❌ **翻倍无效** | §1.2 的证据 A：60s 照样超时。而计划 32 的 §4.5 决策表把「未登记工具一律回落 30s」列为**回归护栏** —— 并进去会打破它 |

**⇒ 本计划连一个字节都不改 `MCP/longTask/timeoutPolicy.js`。**

---

## 3. 设计：判定规则与状态机

### 3.1 响应体形状（照抄 `AREA_OP_CHANNEL_BUSY`，不发明第二套格式）

既有先例 `MCP/index.js:4321-4324`：

```javascript
      if (isGabpErrorTimeout(r)) {
        return contentErrorResponse('AREA_OP_CHANNEL_BUSY',
          '删除操作 GABP 超时；连接可能繁忙。请用 list_areas/list_zones 确认当前状态后重试，勿重复并发删除。');
      }
```

本计划产出同形状的兄弟：

```json
{
  "success": false,
  "errorCode": "GABP_CHANNEL_WEDGED",
  "message": "GABP 通道楔死：连续 3 次 tools/call 超时，判定 RimBridge 侧 Unity 主线程无响应。…（见 §3.4 全文）",
  "abandoned": {
    "summary": "2 项工作项仍在游戏侧队列中排队，1 项已被证明执行过",
    "queuedCount": 2,
    "executedCount": 1,
    "canRetrySafely": false,
    "retryRisk": "排队中的那 2 项在主线程恢复时会补执行；已执行的那 1 项再发一次就是执行两次。两种情况都不该重试。",
    "basis": "判定规则见计划 §3.2；本判定是「已被后续成功响应证明的」结论，不是对游戏内部状态的直接观测。",
    "items": [
      { "seq": 1, "tool": "rimworld/execute_debug_action", "atMs": 1770000000000, "disposition": "still-queued-game-side",     "basis": "…" },
      { "seq": 2, "tool": "rimworld/execute_debug_action", "atMs": 1770000060000, "disposition": "still-queued-game-side",     "basis": "…" },
      { "seq": 3, "tool": "rimworld/list_debug_action_roots", "atMs": 1770000120000, "disposition": "drained-already-executed", "basis": "…" }
    ]
  }
}
```

**顶层没有「排水中 vs 已丢弃」两值枚举，而是 `items[].disposition` 逐项给。** 理由：闩锁只由超时置位，所以任何一次楔死错误里**必然**至少含一项刚被放弃、尚未被证明执行的 ⇒ 顶层枚举在实践中恒为「排队中」，做成两值枚举等于把一个恒真的值写死。逐项才有信息量（一次「重试 5 次」里，前几次已执行、后几次还在排队，是真实且常见的形态）。

### 3.2 「排队中」vs「已丢弃」的判定规则

**唯一判据：`RimBridgeMainThread.Pump()`（06000E76:M）是严格 FIFO。**

```csharp
while (true) {
    lock (Sync) { if (Pending.Count == 0) break; work = Pending.Dequeue(); }
    work.ExecuteIfPending();
}
```

于是有一条**可从反编译推出的硬结论**：

> 若工作项 A 比工作项 B 更早入队，而 B 对应的 GABP 调用**拿到了成功响应**，那么 A 必然已经出队并执行过。
> （B 成功 ⇒ B 的工作项被 `Pump()` 取出来执行过 ⇒ `Pump()` 跑过了 ⇒ 它是 FIFO ⇒ 在 B 之前的 A 早已执行完。）

所以 MCP 侧只需记两个数：

| 变量 | 含义 | 更新点 |
|---|---|---|
| `gabpAbandonSeq` | 放弃序号自增源 | 每次超时（`noteGabpAbandoned`） |
| `gabpDrainedThroughSeq` | 已**被证明**排空执行的最大放弃序号 | 每次**成功**响应（`noteGabpDrained`，`= gabpAbandonSeq`） |

判定函数（纯函数，只读模块状态）：

```javascript
function gabpAbandonDisposition(item) {
  return item.seq <= gabpDrainedThroughSeq ? 'drained-already-executed' : 'still-queued-game-side';
}
```

| `disposition` | 含义 | 依据文案（写进 `items[].basis`） |
|---|---|---|
| `still-queued-game-side` | **排队中** —— 很可能**仍在** `RimBridgeMainThread.Pending` 里，主线程一恢复就补执行 | 「自本次 `tools/call` 被本地放弃之后，本通道**尚未出现任何成功的 GABP 响应** ⇒ `Pump()` 未被证明排空过队列 ⇒ 该工作项很可能仍在 `RimBridgeMainThread.Pending` 中，主线程恢复后会**补执行**。」 |
| `drained-already-executed` | **已丢弃/已执行** —— 「超时未执行」这个结论对它是**错的** | 「自本次 `tools/call` 被本地放弃之后，本通道已出现**成功的 GABP 响应**。`Pump()`（06000E76:M）是严格 FIFO 的 dequeue→execute，而本项入队更早 ⇒ 它**必然已出队并执行过**。『超时 ⇒ 未执行』对本项不成立。」 |

**⚠️ 前提与残余不确定性（必须写进 `basis`，不许含糊过去）：**

1. 该 `tools/call` 前提是**确实到达了游戏侧并入了队**。若 TCP write 在到达前就断了（连接已死），它从未入队 —— 此时判「排队中」是**误判**（方向是「高估了还会执行」，宁可保守也不要说成「不会执行」）。
2. `ExecuteIfPending()` 在工作项已被 `CancelIfPending()` 取消时是 no-op，而 `CancelIfPending()` 只在调用方传了可取消的 `CancellationToken` 时才注册。`ExecuteDebugActionResponse`（`060010B9:M`）**没有传** —— 但这是对**这一条路径**的观察，不是对 RBS 全局的断言。
3. 综上：字段名用 `disposition`（判定）而不是 `fact`（事实），文案用「很可能」，**绝不用「一定 / 必定没执行」**。

### 3.3 闩锁状态机

| 事件 | `gabpWedged` | `gabpNextProbeAt` | 动作 |
|---|---|---|---|
| 一次 `tools/call` 超时（且**非** `inputDerived`） | 不变 | 不变 | `noteGabpAbandoned(rbsName)`；`gabpConsecutiveTimeout++` |
| `gabpConsecutiveTimeout` 达阈值（`:2777`） | **置位** | `= now + 20000` | 保留既有 `forceReconnect()`（`:2781`）；**并就地返回 `GABP_CHANNEL_WEDGED`**，不再落回 `:2789` |
| 闩锁期内的后续调用，`now < gabpNextProbeAt` | 不变 | 不变 | **不发起 `tools/call`**，直接返回 `GABP_CHANNEL_WEDGED` |
| 闩锁期内的后续调用，`now >= gabpNextProbeAt` | 不变 | `= now + 20000` | **放行这一次探针**（并把窗口推后，保证「最多 20 秒发一个」） |
| **任何** GABP 成功响应（`:2758`） | **清零** | `= 0` | `noteGabpDrained()` + `clearGabpWedge()` |
| 非超时失败（`:2786-2787`） | **清零** | `= 0` | `clearGabpWedge()` |

**为什么要有探针窗口而不是永久闩死：** 永久闩死 = 永久失去自愈能力（游戏恢复了也永远返回楔死错误）。20 秒一次足够稀（一次「连续重试 5 次」在闩锁下最多只多发 1 次，不会灌满死队列），又足够密（最坏 20 秒后就能自证恢复）。

**为什么不用 `gabpBridge.isConnected()` 做清零条件：** `forceReconnect()`（`gabpClient.js:309-337`）在 `:331` 把 `state` 置为 `'idle'`、`:336` 才异步重连 ⇒ 紧随其后的下一次调用里 `isConnected()`（`:694-696`）**必为 false**。用它当清零条件会让闩锁**刚置位就被自己清掉**。

**为什么短截断放在 `ensureGabpConnected()` 之后：** `:2748` 会先做惰性重连（重读 `Player.log` 拿新端口/token）。玩家重启了游戏时，先由它给出「未连接/未就绪」这种更有信息量的错误；只有它返回 `ok` 才是「连着但不响应」，那才是楔死。

### 3.4 `GABP_CHANNEL_WEDGED` 的 `message` 全文（逐字照抄进实现）

```
GABP 通道楔死：连续 3 次 tools/call 超时，判定 RimBridge 侧 Unity 主线程无响应。
【不要重试】重试只会往游戏侧的待执行队列里再叠一个工作项 —— 主线程恢复时它们会挨个补执行。
【重连无效】实测：强制重连成功后的下一次调用仍然 30045ms 超时。卡的是游戏进程，不是 TCP 连接。
【怎么办】重启游戏（stop_game / start_game）或重载存档后再试；游戏重启后 MCP 会从 Player.log 重新发现端口与 token 自动重连。
【楔死期间可用的旁路】search_debug_actions、execute_csharp_code（原生 UnityExplorer HTTP，不经 GABP）；read_rimworld_log（读本地文件）；get_game_status（直连探测，不经本次闩锁）。
【注意】超时 ≠ 取消：被判超时的那次 tools/call 并没有被取消，它会在主线程恢复后补执行。
```

其中「连续 3 次」要用模板串取 `GABP_TIMEOUT_RECOVER_THRESHOLD`，不要写死。

### 3.5 明确不做的事（逐条，并给出理由）

- ❌ **不加白名单、不改 `timeoutPolicy.js`、不改 `config.json`** —— §1.2：翻倍预算已实测无效，只会把 30 秒的失败换成 120 秒的失败，还会拖慢 `forceReconnect` 的自愈阈值。
- ❌ **不删 `forceReconnect()`** —— 它是既有自愈机制（计划 32 任务 2 的对照组测试 `mirrorLongWaitBudget.test.js` 还断言它照常被调用）。本计划只是**不再指望它**，并在文案里明说。
- ❌ **不实现板上「探 dev-mode / `Dialog_DebugSettings`」的建议** —— §2：它对准了一个不存在的门禁。
- ❌ **不与 `[01]` / `[25]` 合并、不动计划 04 / 31 / 32 的任何文件** —— §2 与 §0.4。
- ❌ **不试图消除 `-32001`** —— §1.7：那是宿主客户端的截止线，不在本仓库内。
- ❌ **不改 `getGameStatus()`（`:1339`）与 `start_debug_game_ready` 探测（`:4292`）** —— 它们直连 `gabpBridge.callTool`，天然不受闩锁影响。**这正是我们要的**：`get_game_status` 是楔死时最该用的诊断工具，被闩住就麻烦了。

---

## 4. 任务 1：把「超时 ≠ 取消」写进代码

**交付物：** `MCP/gabpClient.js:566-569` 的 `setTimeout` 回调里多一段注释（**行为零变化，一个字符的可执行代码都不改**），外加一条源码切片断言把这段语义**钉在那个位置上**，防止日后「顺手清理注释」时把它连同语义一起删掉。

**为什么它是本计划最重要的一行改动：** 三个静态要素（`_pending.delete` + `reject` + GABP 协议无取消消息）都已经在代码里了，缺的是把它们**串成一句人话**。没有这句话，下一个人会把这段 `setTimeout` 当成「安全超时」，然后写出「超时了，重试一下」的重试循环 —— 那正是 §1.4 里「执行两次」的来源。

**文件：**
- 修改：`MCP/gabpClient.js:566-569`
- 测试：`MCP/test/gabpWedge.test.js`（**新建**）

- [ ] **步骤 1：编写失败的测试**

新建 `MCP/test/gabpWedge.test.js`，写入完整内容（**本文件后续任务都往里加测试，步骤 2/3 之后再贴**）：

```javascript
// [36] GABP 通道楔死的可判别失败 —— 全部离线断言，不连游戏、不动游戏状态。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const readMcp = (f) => fs.readFileSync(path.join(here, '..', f), 'utf-8');
const gabpSource = readMcp('gabpClient.js');
const indexSource = readMcp('index.js');

// ---------------------------------------------------------------------------
// 任务 1：gabpClient._request 的 setTimeout 回调必须写明「超时 ≠ 取消」
// ---------------------------------------------------------------------------
test('[36] gabpClient._request：setTimeout 回调里必须写明「超时 ≠ 取消 / 不取消游戏侧 / Pending / 照样执行」', () => {
  const s = gabpSource.indexOf('_request(method, params, opts = {}) {');
  assert.ok(s > 0, '找不到 _request');
  const e = gabpSource.indexOf('\n  // ---------- LSP 帧读写', s);
  assert.ok(e > s, '找不到 _request 的结束锚点「// ---------- LSP 帧读写」');
  const fn = gabpSource.slice(s, e);

  // 注释必须落在 setTimeout 回调里、且在真正 delete/reject 之前 —— 否则读代码的人先看到行为、后看到解释
  const t = fn.indexOf('const timer = setTimeout(() => {');
  assert.ok(t > 0, '找不到 setTimeout');
  const d = fn.indexOf('this._pending.delete(id);', t);
  assert.ok(d > t, '找不到 setTimeout 里的 _pending.delete');
  const head = fn.slice(t, d);

  assert.match(head, /超时\s*≠\s*取消/, '必须写出「超时 ≠ 取消」');
  assert.match(head, /不取消游戏侧/, '必须写出「不取消游戏侧」');
  assert.match(head, /RimBridgeMainThread\.Pending/, '必须点名游戏侧的工作项仍在 Pending 队列里');
  assert.match(head, /照样执行/, '必须写出「照样执行」');
  assert.match(head, /执行两次/, '必须点出「重试 = 执行两次」这个后果');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/gabpWedge.test.js
```

预期：**FAIL**，且失败信息是 `head` 上的第一条 `assert.match`（`必须写出「超时 ≠ 取消」`）。若报的是「找不到 `_request`」或「找不到结束锚点」，说明锚点字符串写错了 —— **改锚点，不要改断言意图**。

- [ ] **步骤 3：加注释（行为零变化）**

把 `MCP/gabpClient.js:566-569` 这 4 行：

```javascript
      const timer = setTimeout(() => {
        this._pending.delete(id);
        reject(this._err(GABP_ERROR_CODES.SERVER_ERROR, `GABP 请求 '${method}' 超时（${timeoutMs}ms）`));
      }, timeoutMs);
```

替换为（**只多出注释；`:568` 的错误串一个字都不许改** —— 计划 32 的测试与 `isGabpErrorTimeout`（`index.js:2712`）都依赖它）：

```javascript
      const timer = setTimeout(() => {
        // ⚠️ 超时 ≠ 取消（吐槽板 [36]；计划 docs/issue-triage/plans/2026-10-03-36-gabp-wedge-distinguishable-failure.md）
        // 下面两行**只**放弃**本地**等待：把条目从 _pending 摘掉、reject 掉这个 Promise。
        // **没有向游戏侧发任何取消报文** —— GABP 协议里也没有这种消息（Lib.GAB 的方法集实测只有 7 个）。
        //
        // 游戏侧那一份工作项此刻**原封不动**：
        //   RimBridgeMainThread.InvokeAsync<T>（06000E79:M）无超时地 await workItem.Completion，
        //   该工作项仍躺在 RimBridgeMainThread.Pending 队列里；
        //   主线程一旦恢复，Pump()（06000E76:M）会把它 dequeue 出来**照样执行**
        //   （ExecuteDebugActionResponse 060010B9:M 末尾是 node.action?.Invoke()，同步、无超时参数）。
        //
        // ⇒ 报「超时」之后，动作仍可能在若干分钟后才发生。**调用方重试 = 执行两次。**
        // ⇒ 绝不要把这个 setTimeout 当成「安全超时」或任何形式的取消点。
        // ⇒ 想知道「到底执行了没有」：看 MCP/index.js 的 GABP_CHANNEL_WEDGED 响应里的
        //   abandoned.items[].disposition（still-queued-game-side / drained-already-executed）。
        this._pending.delete(id);
        reject(this._err(GABP_ERROR_CODES.SERVER_ERROR, `GABP 请求 '${method}' 超时（${timeoutMs}ms）`));
      }, timeoutMs);
```

- [ ] **步骤 4：运行测试验证通过**

运行：`node --test test/gabpWedge.test.js`
预期：**1 PASS**（`node --test` 结尾打印 `# pass 1` / `# fail 0`）。

- [ ] **步骤 5：改动清单**

| 文件 | 动作 | 锚点（行号已复核；见 §0.4 的重定位说明） |
|---|---|---|
| `MCP/gabpClient.js` | 修改（+14 行 / -0 行，**纯注释**） | `:566-569` `setTimeout` 回调 |
| `MCP/test/gabpWedge.test.js` | **新建** | 1 条源码切片断言 |

**没有改 C#、没有改 `MCP/index.js`、没有改 `timeoutPolicy.js` / `config.json`、没有重编译、没有碰 `docs/` 下除本计划外的任何文件。**

---

## 5. 任务 2：楔死闩锁的状态机与放弃记录

**交付物：** `MCP/index.js` 里一块新的模块级状态（闩锁标志 + 探针窗口 + 被放弃工作项记录）与 6 个配套函数，以及 `index.js:5450-5459` 的 `_testHooks` 里 3 个新钩子。**这一步不改变任何对外可见的返回内容** —— 闩锁只被置位，还没有被 `callGABPTool` 用来短路（那是任务 3）。它先可被观测、可被驱动、可被单测。

**文件：**
- 修改：`MCP/index.js:2518` 之后（新增状态块 + 6 个函数）、`MCP/index.js:2759` 旁（成功路径清闩）、`MCP/index.js:2776` 旁（超时记放弃）、`MCP/index.js:2779` 旁（阈值置闩）、`MCP/index.js:2787` 旁（非超时清闩）、`MCP/index.js:5450-5459`（3 个钩子）
- 测试：`MCP/test/gabpWedge.test.js`（追加）

- [ ] **步骤 1：编写失败的测试**

把下面的**共享夹具** + **测试**追加到 `MCP/test/gabpWedge.test.js` 末尾（放在任务 1 的测试之后；任务 3 会继续往同一个文件里加）：

```javascript
// ---------------------------------------------------------------------------
// 共享夹具：真 GabpClient._request 绑到假 socket 上 —— 超时是真的（真的 setTimeout 到点），
// 但全程不连游戏、不动游戏状态。写法照抄 test/index.integration.test.js:127-161。
// ---------------------------------------------------------------------------

// 「只收不回」的 TCP 服务：连上但永不发任何帧 ⇒ 真的把 _request 拖到超时。
// make() 造出的假桥接会被登记，finally 里统一 destroy 掉 socket（不销毁会吊住测试进程，
// 参照 test/index.integration.test.js:177 / :190 的 fakeA._socket.destroy()）。
async function withSilentServer(fn) {
  const net = await import('node:net');
  const srv = net.createServer(() => { /* 静默，不回帧 */ });
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const made = [];
  const make = async (timeoutMs = 300) => {
    const b = await makeSilentBridge(srv.address().port, timeoutMs);
    made.push(b);
    return b;
  };
  try {
    return await fn(make);
  } finally {
    for (const b of made) { try { b._socket.destroy(); } catch { /* ignore */ } }
    mod._testHooks.setGabpBridge(null);
    mod._testHooks.resetGabpWedge();
    try { srv.close(() => {}); } catch { /* ignore */ }
    try { srv.unref(); } catch { /* ignore */ }
  }
}

async function makeSilentBridge(port, timeoutMs = 300) {
  const net = await import('node:net');
  const { GabpClient } = await import('../gabpClient.js');
  const real = new GabpClient({ rimBridge: { requestTimeoutMs: timeoutMs } }, { log: () => {} });
  const fake = {
    state: 'connected',
    isConnected: () => true,
    forceReconnect: () => {},
    lastError: null,
    _requestTimeoutMs: timeoutMs,
    _pending: new Map(),
    _socket: new net.Socket(),
    _log: () => {},
    _err: (code, message) => ({ code, message }),
    calls: [],          // 每次真正发出去的 tools/call 的工具名（任务 3 断言「第 4 次没发出去」）
  };
  fake._request = real._request.bind(fake);
  fake._writeMessage = real._writeMessage.bind(fake);
  // callTool 必须存在：否则 callGABPTool 抛 TypeError 被上层 uncaughtException 吞掉，
  // Promise 永不 settle，测试会挂到超时而不是快速失败（index.integration.test.js:150-151 踩过）。
  fake.callTool = async (name, args, opts) => {
    fake.calls.push(name);
    try {
      const r = await fake._request('tools/call', { name, parameters: args || {} }, opts || {});
      return { ok: true, result: r };
    } catch (e) {
      return { ok: false, error: e };
    }
  };
  fake._socket.connect(port, '127.0.0.1');
  await new Promise((r) => fake._socket.once('connect', r));
  return fake;
}

// 立刻成功的假桥接（用来证明「一次成功 ⇒ 之前的放弃项都已被 Pump 排空」）
function makeSucceedingBridge() {
  const b = {
    state: 'connected',
    isConnected: () => true,
    forceReconnect: () => {},
    lastError: null,
    calls: [],
    callTool: async (name) => { b.calls.push(name); return { ok: true, result: { echo: name } }; },
  };
  return b;
}

const callOk = (name) => mod.callGABPTool(name, {}, undefined, { timeoutMs: 300 });

// ---------------------------------------------------------------------------
// 任务 2：闩锁状态机
// ---------------------------------------------------------------------------
test('[36] 连续 3 次超时 → 闩锁置位 + 3 条放弃记录全部判为 still-queued-game-side', async () => {
  await withSilentServer(async (make) => {
    mod._testHooks.resetGabpWedge();
    mod._testHooks.setGabpBridge(await make());

    for (let i = 0; i < 3; i++) {
      const res = await callOk('rimworld/execute_debug_action');
      assert.match(JSON.stringify(res), /超时/, `第 ${i + 1} 次应真的超时`);
    }

    const st = mod._testHooks.getGabpWedgeState();
    assert.equal(st.wedged, true, '连续 3 次（阈值见 index.js:2518）后应置位');
    assert.equal(st.abandoned.length, 3, '每次超时都要留一条放弃记录');
    assert.equal(st.drainedThroughSeq, 0, '还没有任何成功响应 ⇒ drainedThroughSeq 必须是 0');
    assert.deepEqual(
      st.abandoned.map((a) => a.disposition),
      ['still-queued-game-side', 'still-queued-game-side', 'still-queued-game-side'],
      '没有排空证据时，三项都必须判为「排队中」'
    );
    assert.deepEqual(
      st.abandoned.map((a) => a.seq),
      [1, 2, 3],
      '序号必须单调递增 —— FIFO 判定全靠它'
    );
  });
});

test('[36] 放弃之后出现一次成功 ⇒ 全部改判 drained-already-executed，且闩锁清零', async () => {
  await withSilentServer(async (make) => {
    mod._testHooks.resetGabpWedge();
    mod._testHooks.setGabpBridge(await make());
    for (let i = 0; i < 3; i++) await callOk('rimworld/execute_debug_action');
    assert.equal(mod._testHooks.getGabpWedgeState().wedged, true);

    // Pump()(06000E76:M) 是严格 FIFO：后发的这次成功 ⇒ 先发的 3 项必然已出队执行
    mod._testHooks.setGabpBridge(makeSucceedingBridge());
    const ok = await mod.callGABPTool('rimworld/pause_game', {}, undefined, { timeoutMs: 300 });
    assert.equal(JSON.parse(ok.content[0].text).success, true, '对照调用应成功');

    const st = mod._testHooks.getGabpWedgeState();
    assert.equal(st.wedged, false, '成功响应必须清闩（否则永久闩死 = 永久失去自愈）');
    assert.equal(st.drainedThroughSeq, 3, 'drainedThroughSeq 应追到放弃序号 3');
    assert.deepEqual(
      st.abandoned.map((a) => a.disposition),
      ['drained-already-executed', 'drained-already-executed', 'drained-already-executed'],
      'FIFO 判据：后发的成功证明先前三项都已执行'
    );
  });
});

test('[36] 非超时失败不计入放弃、不置闩；resetGabpWedge 能把状态清干净', async () => {
  await withSilentServer(async (make) => {
    mod._testHooks.resetGabpWedge();
    const b = await make();
    b.callTool = async () => ({ ok: false, error: { code: -32602, message: '参数错误：缺 path' } });
    mod._testHooks.setGabpBridge(b);

    for (let i = 0; i < 3; i++) await mod.callGABPTool('rimworld/execute_debug_action', {}, undefined, { timeoutMs: 300 });

    const st = mod._testHooks.getGabpWedgeState();
    assert.equal(st.wedged, false, '非超时失败不是楔死（对照 index.js:2786-2787 的既有分支）');
    assert.equal(st.abandoned.length, 0, '参数错误不是「被放弃的工作项」');

    mod._testHooks.resetGabpWedge();
    const clean = mod._testHooks.getGabpWedgeState();
    assert.equal(clean.wedged, false);
    assert.equal(clean.abandoned.length, 0);
    assert.equal(clean.abandonedSeq, 0);
    assert.equal(clean.drainedThroughSeq, 0);
  });
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`node --test test/gabpWedge.test.js`
预期：**3 PASS（任务 1 的）+ 3 FAIL**。三个 FAIL 都应报 `mod._testHooks.getGabpWedgeState is not a function` / `resetGabpWedge is not a function`。**若报别的错，先看是不是夹具本身有问题**（例如 `mod._testHooks` 整个是 `undefined` ⇒ 忘了 `process.env.UESD_MCP_NO_START = '1'`，那会真的去监听 3000 端口）。

- [ ] **步骤 3：实现状态机**

**3a. 在 `MCP/index.js:2518` 之后**（`const GABP_TIMEOUT_RECOVER_THRESHOLD = …` 那行下面、`// ---------- 长任务运行器` 上面）插入这一整块：

```javascript

// —— 楔死闩锁（吐槽板 [36]；计划 docs/issue-triage/plans/2026-10-03-36-gabp-wedge-distinguishable-failure.md）——
// 连续 GABP_TIMEOUT_RECOVER_THRESHOLD 次 tools/call 超时 ⇒ 判定 RimBridge 侧 Unity 主线程楔死。
// 为什么必须**闩住后续调用**（而不只是像现在这样记一次日志 + 重连一次）：
//   超时只放弃**本地**等待（见 MCP/gabpClient.js:566 的注释），游戏侧的工作项仍躺在
//   RimBridgeMainThread.Pending 队列里（InvokeAsync 06000E79:M 无超时地 await workItem.Completion），
//   主线程一恢复，Pump()（06000E76:M）就会把它 dequeue 出来**照样执行**。
//   ⇒ 每多发一次 tools/call，就等于往那个死队列里多塞一个「稍后必然执行」的工作项。
//   ⇒ 楔死期间**不发起**调用，是唯一能阻止副作用继续堆积的手段。
//   ⇒ forceReconnect() 保留（既有自愈），但不指望它：实测强制重连后的下一次调用仍 30045ms 超时
//      —— 卡的是游戏进程，不是 TCP 连接。
let gabpWedged = false;         // 闩锁是否置位
let gabpNextProbeAt = 0;        // 闩锁期内，每隔 GABP_WEDGE_PROBE_AFTER_MS 放行 1 次探针
let gabpAbandonSeq = 0;         // 放弃序号自增源（FIFO 判定的依据）
let gabpDrainedThroughSeq = 0;  // 已被证明排空执行的最大放弃序号
let gabpAbandoned = [];         // [{ seq, tool, atMs }]，上限 GABP_ABANDONED_KEEP
// 闩锁期内放行探针的节奏。够慢（一次「连续重试 5 次」最多只多发 1 次，灌不满死队列），
// 够快（最坏 20 秒后就能自证恢复）。
const GABP_WEDGE_PROBE_AFTER_MS = 20000;
// 错误里最多列出多少个被放弃的工作项。8 条足够把一次「重试 5 次」讲清楚。
const GABP_ABANDONED_KEEP = 8;

// 判定一个「已被放弃」的工作项现在是排队中还是已执行。
// 依据：Pump()（06000E76:M）是严格 FIFO 的 dequeue→execute —— 只要在本项之后入队的某次
// GABP 调用拿到了成功响应，就证明 Pump() 跑过了，本项必然已出队执行。
// ⚠️ 这是「已被后续成功响应证明的推断」，不是对游戏内部状态的直接观测；前提是本项确实
//    到达过游戏侧并入队。若 TCP write 在到达前就断了，它从未入队，此时本判定会高估。
function gabpAbandonDisposition(item) {
  return item.seq <= gabpDrainedThroughSeq ? 'drained-already-executed' : 'still-queued-game-side';
}

// 记录一次「本地已放弃、游戏侧未知」的工具调用。
function noteGabpAbandoned(rbsName) {
  gabpAbandoned.push({ seq: ++gabpAbandonSeq, tool: rbsName, atMs: Date.now() });
  while (gabpAbandoned.length > GABP_ABANDONED_KEEP) gabpAbandoned.shift();
}

// 任何一次 GABP 成功响应 ⇒ Pump() 跑过了 ⇒ 此刻之前入队的所有工作项必然已出队执行。
function noteGabpDrained() {
  gabpDrainedThroughSeq = gabpAbandonSeq;
}

function setGabpWedge() {
  gabpWedged = true;
  gabpNextProbeAt = Date.now() + GABP_WEDGE_PROBE_AFTER_MS;
}

function clearGabpWedge() {
  gabpWedged = false;
  gabpNextProbeAt = 0;
}
```

**3b. 在 `MCP/index.js:2759` 旁**（成功路径）把

```javascript
    gabpConsecutiveTimeout = 0; // 成功响应重置超时计数
```

替换为

```javascript
    gabpConsecutiveTimeout = 0; // 成功响应重置超时计数
    noteGabpDrained();  // 成功 ⇒ Pump() 跑过了 ⇒ 先前被放弃的工作项都已被排空执行
    clearGabpWedge();   // 通道恢复，放行后续调用（否则永久闩死 = 永久失去自愈）
```

**3c. 在 `MCP/index.js:2776` 旁**（`gabpConsecutiveTimeout++;` 下面一行）插入：

```javascript
    noteGabpAbandoned(rbsName); // 本地放弃这一项；游戏侧那一份仍在 Pending 队列里（见上方注释）
```

**3d. 在 `MCP/index.js:2779` 旁**（`gabpConsecutiveTimeout = 0;`，夹在 WARN 日志与 `if (gabpBridge) {` 之间）插入：

```javascript
      setGabpWedge(); // 判定楔死：后续调用在发起之前就会被短路（任务 3 接线）
```

**3e. 在 `MCP/index.js:2787` 旁**（非超时分支）把

```javascript
    gabpConsecutiveTimeout = 0; // 非超时失败（如参数错误）不累计
```

替换为

```javascript
    gabpConsecutiveTimeout = 0; // 非超时失败（如参数错误）不累计
    clearGabpWedge();           // 通道在正常应答（哪怕是业务错误）⇒ 没楔死
```

**3f. 在 `MCP/index.js:5450-5459` 的 `_testHooks` 里**，`getGabpConsecutiveTimeout: () => gabpConsecutiveTimeout,` 那一行之后插入：

```javascript
  // 楔死闩锁（[36]）：让离线测试能直接观测闩锁/放弃记录，并能立刻打开探针窗口
  getGabpWedgeState: () => ({
    wedged: gabpWedged,
    nextProbeAt: gabpNextProbeAt,
    abandonedSeq: gabpAbandonSeq,
    drainedThroughSeq: gabpDrainedThroughSeq,
    abandoned: gabpAbandoned.map((a) => ({ ...a, disposition: gabpAbandonDisposition(a) })),
  }),
  openGabpWedgeProbeWindow: () => { gabpNextProbeAt = 0; },
  resetGabpWedge: () => {
    gabpWedged = false;
    gabpNextProbeAt = 0;
    gabpAbandoned = [];
    gabpAbandonSeq = 0;
    gabpDrainedThroughSeq = 0;
    gabpConsecutiveTimeout = 0;
  },
```

- [ ] **步骤 4：运行测试验证通过**

运行：`node --test test/gabpWedge.test.js`
预期：**6 PASS / 0 FAIL**。若第二个测试（「出现一次成功 ⇒ 全部改判」）失败而第一个通过，几乎一定是 `noteGabpDrained()` 没挂在成功路径上（3b 漏了），或者 `clearGabpWedge()` 顺手把 `drainedThroughSeq` 也清了 —— **它只该清 `gabpWedged` 和 `gabpNextProbeAt`**。

- [ ] **步骤 5：改动清单**

| 文件 | 动作 | 锚点（行号已复核；见 §0.4 的重定位说明） |
|---|---|---|
| `MCP/index.js` | 修改（+约 60 行） | `:2518` 之后（状态块 + 6 个函数）、`:2759`、`:2776`、`:2779`、`:2787`、`:5450-5459` |
| `MCP/test/gabpWedge.test.js` | 修改（追加） | 共享夹具 + 3 条状态机测试 |

**本任务不改任何对外可见的返回内容**（闩锁还没接线到 `callGABPTool`，那是任务 3）。**没有改 C#、没有改 `gabpClient.js`（那是任务 1）、没有改 `timeoutPolicy.js` / `config.json`、没有重编译。**

---

## 6. 任务 3：接线 —— 短截断 + `GABP_CHANNEL_WEDGED` 结构化错误

**交付物：** ①`contentErrorResponse` 加可选第三参（§0.6 的兼容性论证由本任务的回归断言守住）；②新的 `gabpWedgeErrorResponse()`；③`callGABPTool` 开头加短截断，**楔死期间不再发起 `tools/call`**；④阈值那一路**就地返回**结构化错误。这是本计划唯一改变对外可见行为的一步。

**文件：**
- 修改：`MCP/index.js:2705-2707`（`contentErrorResponse`）、`MCP/index.js:2708` 之后（新增 `gabpWedgeErrorResponse`）、`MCP/index.js:2755-2756`（短截断）、`MCP/index.js:2777-2785`（阈值就地返回）
- 测试：`MCP/test/gabpWedge.test.js`（追加）

- [ ] **步骤 1：编写失败的测试**

追加到 `MCP/test/gabpWedge.test.js` 末尾：

```javascript
// ---------------------------------------------------------------------------
// 任务 3：楔死期不再发起 tools/call，并返回可判别的结构化错误
// ---------------------------------------------------------------------------
const wedgeBody = async (res) => JSON.parse(res.content[0].text);

test('[36] 连续 3 次超时后的第 4 次调用：不得发起 tools/call，直接返回 GABP_CHANNEL_WEDGED', async () => {
  await withSilentServer(async (make) => {
    mod._testHooks.resetGabpWedge();
    const b = await make();
    mod._testHooks.setGabpBridge(b);

    for (let i = 0; i < 3; i++) await callOk('rimworld/execute_debug_action');
    assert.equal(b.calls.length, 3, '前 3 次是真的发出去了');

    const body = await wedgeBody(await callOk('rimworld/execute_debug_action'));

    // —— 核心断言：第 4 次一个字节都没发出去 ——
    assert.equal(b.calls.length, 3, '闩锁期内不得再发起 tools/call（否则就是往游戏侧死队列里再塞一个待执行项）');

    // —— 形状照抄 AREA_OP_CHANNEL_BUSY ——
    assert.equal(body.success, false);
    assert.equal(body.errorCode, 'GABP_CHANNEL_WEDGED');
    assert.equal(typeof body.message, 'string');

    // —— 文案必须点名三件事：别重试 / 重连无效 / 怎么办 ——
    assert.match(body.message, /不要重试/);
    assert.match(body.message, /重连无效/);
    assert.match(body.message, /重启游戏/);
    assert.match(body.message, /超时 ≠ 取消/);
    assert.match(body.message, /search_debug_actions/, '必须给出楔死期间可用的旁路');

    // —— 逐项判定 ——
    assert.equal(body.abandoned.queuedCount, 3);
    assert.equal(body.abandoned.executedCount, 0);
    assert.equal(body.abandoned.canRetrySafely, false);
    assert.match(body.abandoned.retryRisk, /执行两次|补执行/);
    assert.equal(body.abandoned.items.length, 3);
    for (const it of body.abandoned.items) {
      assert.equal(it.disposition, 'still-queued-game-side', '没有任何成功响应 ⇒ 三项都必须判为排队中');
      assert.match(it.basis, /尚未出现任何成功的 GABP 响应/);
      assert.equal(it.tool, 'rimworld/execute_debug_action');
    }
    assert.equal(body.abandoned.items[2].seq, 3);
    assert.equal(body.blockedTool, 'rimworld/execute_debug_action');
    assert.equal(body.consecutiveTimeoutThreshold, 3, '阈值出处是 index.js:2518；别写死成别的数');
    assert.equal(body.probeWindowMs, 20000);
  });
});

test('[36] 混合状态：先排空 3 项、再新增 3 项 ⇒ 错误里逐项区分 drained 与 still-queued', async () => {
  await withSilentServer(async (make) => {
    mod._testHooks.resetGabpWedge();
    mod._testHooks.setGabpBridge(await make());
    for (let i = 0; i < 3; i++) await callOk('rimworld/execute_debug_action'); // seq 1..3

    // 一次成功 ⇒ Pump() 跑过 ⇒ seq 1..3 必然已执行
    mod._testHooks.setGabpBridge(makeSucceedingBridge());
    await mod.callGABPTool('rimworld/pause_game', {}, undefined, { timeoutMs: 300 });

    // 再次楔死：seq 4..6 还没排空
    mod._testHooks.setGabpBridge(await make());
    for (let i = 0; i < 3; i++) await callOk('rimworld/get_debug_action');

    const body = await wedgeBody(await callOk('rimworld/get_debug_action'));
    assert.equal(body.errorCode, 'GABP_CHANNEL_WEDGED');
    assert.deepEqual(
      body.abandoned.items.map((i) => i.disposition),
      ['drained-already-executed', 'drained-already-executed', 'drained-already-executed',
        'still-queued-game-side', 'still-queued-game-side', 'still-queued-game-side'],
      '「重试 5 次里前几次已执行、后几次还排队」是真实形态，必须逐项说清'
    );
    assert.equal(body.abandoned.queuedCount, 3);
    assert.equal(body.abandoned.executedCount, 3);
    assert.match(body.abandoned.items[0].basis, /已出现\*\*成功的 GABP 响应\*\*/);
    assert.match(body.abandoned.items[3].basis, /仍在 RimBridgeMainThread\.Pending/);
  });
});

test('[36] 探针窗口：窗口内不发调用，窗口打开后放行一次并重新计时', async () => {
  await withSilentServer(async (make) => {
    mod._testHooks.resetGabpWedge();
    const b = await make();
    mod._testHooks.setGabpBridge(b);
    for (let i = 0; i < 3; i++) await callOk('rimworld/execute_debug_action');

    // 窗口内：第 5、6 次也都不发
    await callOk('rimworld/pause_game');
    await callOk('rimworld/pause_game');
    assert.equal(b.calls.length, 3, '闩锁窗口内任何工具都不发');

    // 打开探针窗口：放行这一次（这次会真的超时）
    mod._testHooks.openGabpWedgeProbeWindow();
    await callOk('rimworld/pause_game');
    assert.equal(b.calls.length, 4, '探针窗口到期后必须放行一次，否则永久失去自愈能力');

    // 放行后窗口立刻重新计时：紧接着的调用又不发了
    const st = mod._testHooks.getGabpWedgeState();
    assert.ok(st.nextProbeAt > Date.now(), '窗口必须被推后，不能变成「每 20 秒放行无限次」');
    await callOk('rimworld/pause_game');
    assert.equal(b.calls.length, 4, '放行一次后应重新进入静默');
  });
});

test('[36] 探针成功 ⇒ 闩锁解除，后续调用恢复正常下发', async () => {
  await withSilentServer(async (make) => {
    mod._testHooks.resetGabpWedge();
    mod._testHooks.setGabpBridge(await make());
    for (let i = 0; i < 3; i++) await callOk('rimworld/execute_debug_action');

    mod._testHooks.openGabpWedgeProbeWindow();
    const good = makeSucceedingBridge();
    mod._testHooks.setGabpBridge(good);
    const res = await mod.callGABPTool('rimworld/pause_game', {}, undefined, { timeoutMs: 300 });
    assert.equal(JSON.parse(res.content[0].text).success, true);
    assert.equal(mod._testHooks.getGabpWedgeState().wedged, false, '成功必须解闩');

    await mod.callGABPTool('rimworld/pause_game', {}, undefined, { timeoutMs: 300 });
    assert.equal(good.calls.length, 2, '解闩后每一次都要真的发出去');
  });
});

test('[36] 回归：contentErrorResponse 加可选第三参后，2 参调用点的输出必须逐字节不变', async () => {
  // 选 task_cancel 缺 taskId 这条：index.integration.test.js:210-215 已证明它可离线走到，
  // 且它调的是 contentErrorResponse('INVALID_PARAMS', '缺少参数 taskId')（index.js:4189）。
  const body = await wedgeBody(await mod.handleToolCall('task_cancel', {}));
  assert.deepEqual(
    Object.keys(body).sort(),
    ['errorCode', 'message', 'success'],
    '2 参调用不得多出任何键（...(detail || {}) 在 detail 为 undefined 时不产生键）'
  );
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'INVALID_PARAMS');
  assert.equal(body.message, '缺少参数 taskId');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`node --test test/gabpWedge.test.js`
预期：**6 PASS（任务 1+2）+ 5 FAIL**。五个 FAIL 的典型信息：

| 测试 | 失败原因（应该是这个） |
|---|---|
| 第 4 次不发 | `b.calls.length` 是 4 不是 3（闩锁还没接进 `callGABPTool`），或 `body.errorCode` 是 `undefined` |
| 混合状态 | 同上 |
| 探针窗口 | `b.calls.length` 是 5 不是 3 |
| 探针成功 | `wedged` 仍是 `true`（任务 2 的 `clearGabpWedge()` 没挂上成功路径） |
| 2 参兼容 | **PASS**（这条本来就该过，它是护栏不是驱动） |

> 若「2 参兼容」这条 FAIL，说明你提前把 `detail` 写成了必填 —— 改实现，不要改断言。

- [ ] **步骤 3：实现接线**

**3a. `MCP/index.js:2705-2707` 的 `contentErrorResponse` 改成**（第三参 `detail` 可选，缺省时输出与现在**逐字节相同**）：

```javascript
// 检查表缺陷错误响应：文本统一可读 errorCode + message（区别于 gabpErrorResponse 的 wrapGABPResult 结构）
// detail（可选）：额外的机器可读字段，摊平到顶层。缺省时不产生任何键 —— 14 个既有的 2 参调用点
// 输出逐字节不变（见计划 2026-10-03-36-… §0.6）。
function contentErrorResponse(errorCode, message, detail) {
  return { content: [{ type: 'text', text: JSON.stringify({ success: false, errorCode, message, ...(detail || {}) }, null, 2) }], isError: true };
}
```

**3b. 在 `MCP/index.js:2708` 之后**（`contentErrorResponse` 函数结束的 `}` 与 `// 检查表缺陷 4` 注释之间）插入：

```javascript

// 楔死期的对外响应。形状照抄 AREA_OP_CHANNEL_BUSY（见 :4321-4324），多挂一个 abandoned 明细块，
// 把「排队中 vs 已丢弃」逐项讲清楚 —— 因为超时 ≠ 取消，光说「超时了」会让人以为「没执行」。
function gabpWedgeErrorResponse(rbsName) {
  const items = gabpAbandoned.map((a) => {
    const disposition = gabpAbandonDisposition(a);
    const drained = disposition === 'drained-already-executed';
    return {
      seq: a.seq,
      tool: a.tool,
      atMs: a.atMs,
      disposition,
      basis: drained
        ? '自本项被本地放弃之后，本通道已出现**成功的 GABP 响应**。Pump()（06000E76:M）是严格 FIFO 的 dequeue→execute，而本项入队更早 ⇒ 它必然已出队并执行过。**「超时 ⇒ 未执行」对本项不成立。**'
        : '自本项被本地放弃之后，本通道**尚未出现任何成功的 GABP 响应** ⇒ Pump() 未被证明排空过队列 ⇒ 本项很可能仍在 RimBridgeMainThread.Pending 里，主线程恢复后会**补执行**。（前提：本项确实到达过游戏侧并入队；若写入前连接已断，本判定会高估。）',
    };
  });
  const queuedCount = items.filter((i) => i.disposition === 'still-queued-game-side').length;
  const executedCount = items.length - queuedCount;
  return contentErrorResponse('GABP_CHANNEL_WEDGED',
    `GABP 通道楔死：连续 ${GABP_TIMEOUT_RECOVER_THRESHOLD} 次 tools/call 超时，判定 RimBridge 侧 Unity 主线程无响应。\n`
    + `【本次未发出】${rbsName}\n`
    + '【不要重试】重试只会往游戏侧的待执行队列里再叠一个工作项 —— 主线程恢复时它们会挨个补执行。\n'
    + '【重连无效】实测：强制重连成功后的下一次调用仍然 30045ms 超时。卡的是游戏进程，不是 TCP 连接。\n'
    + '【怎么办】重启游戏（stop_game / start_game）或重载存档后再试；游戏重启后 MCP 会从 Player.log 重新发现端口与 token 自动重连。\n'
    + '【楔死期间可用的旁路】search_debug_actions、execute_csharp_code（原生 UnityExplorer HTTP，不经 GABP）；read_rimworld_log（读本地文件）；get_game_status（直连探测，不经本次闩锁）。\n'
    + '【注意】超时 ≠ 取消：被判超时的那次 tools/call 并没有被取消，它会在主线程恢复后补执行。',
    {
      blockedTool: rbsName,
      consecutiveTimeoutThreshold: GABP_TIMEOUT_RECOVER_THRESHOLD,
      probeWindowMs: GABP_WEDGE_PROBE_AFTER_MS,
      nextProbeAllowedAtMs: gabpNextProbeAt,
      abandoned: {
        summary: `${queuedCount} 项工作项仍在游戏侧队列中排队，${executedCount} 项已被证明执行过`,
        queuedCount,
        executedCount,
        canRetrySafely: false,
        retryRisk: queuedCount > 0
          ? `排队中的 ${queuedCount} 项在主线程恢复时会补执行；已执行的 ${executedCount} 项再发一次就是执行两次。两种情况都不该重试。`
          : `已执行的 ${executedCount} 项再发一次就是执行两次，不要重试。`,
        basis: '本判定由「自放弃之后有没有出现过成功的 GABP 响应」经 Pump() 的 FIFO 语义推出，属推断而非对游戏内部状态的直接观测。',
        items,
      },
    });
}
```

**3c. 在 `MCP/index.js:2755` 与 `:2756` 之间**（`!gabpBridge` 那个 return 之后、`const callOpts = opts || {};` 之前）插入：

```javascript
  // 楔死闩锁：探针窗口外**不发起** tools/call（理由见 :2519 的注释块 ——
  // 每多发一次就等于往游戏侧的死队列里多塞一个「稍后必然执行」的工作项）。
  // 放在 ensureGabpConnected 之后：游戏被重启时先由它给出「未连接/未就绪」那种更有信息量的错误。
  if (gabpWedged) {
    if (Date.now() < gabpNextProbeAt) return gabpWedgeErrorResponse(rbsName);
    // 探针窗口到期：放行这一次，并把窗口推后（保证楔死期间最多 20 秒发一个）
    gabpNextProbeAt = Date.now() + GABP_WEDGE_PROBE_AFTER_MS;
  }
```

**3d. 在 `MCP/index.js:2777-2782` 的阈值分支里**，把任务 2 步骤 3d 加的 `setGabpWedge();` 那一行**后面**再插一行：

```javascript
      // 判定的这一刻就返回结构化错误，而不是落回 :2789 的通用 gabpErrorResponse ——
      // 这是「第一次明确告诉调用方通道楔死」的位置。
      return gabpWedgeErrorResponse(rbsName);
```

改完后整个超时分支长这样（**逐字照抄替换 `:2775-2785`**，只多加 2 行）：

```javascript
  } else if (isTimeout) {
    gabpConsecutiveTimeout++;
    noteGabpAbandoned(rbsName); // 本地放弃这一项；游戏侧那一份仍在 Pending 队列里（见 :2519 的注释）
    if (gabpConsecutiveTimeout >= GABP_TIMEOUT_RECOVER_THRESHOLD) {
      log('WARN', `GABP 连续 ${gabpConsecutiveTimeout} 次 tools/call 超时，判定 RimBridge 假死，强制重连`);
      gabpConsecutiveTimeout = 0;
      if (gabpBridge) {
        try { gabpBridge.forceReconnect(); } catch (e) { log('WARN', `GABP 强制重连异常: ${e.message}`); }
      }
      setGabpWedge();
      // 判定的这一刻就返回结构化错误，而不是落回 :2789 的通用 gabpErrorResponse ——
      // 这是「第一次明确告诉调用方通道楔死」的位置。
      return gabpWedgeErrorResponse(rbsName);
    } else {
      log('DEBUG', `GABP 请求超时计数 ${gabpConsecutiveTimeout}/${GABP_TIMEOUT_RECOVER_THRESHOLD}（${rbsName}）`);
    }
  } else {
```

- [ ] **步骤 4：运行测试验证通过**

运行：`node --test test/gabpWedge.test.js`
预期：**11 PASS / 0 FAIL**（任务 1 的 1 条 + 任务 2 的 3 条 + 任务 3 的 5 条 = 9 条测试；其中任务 2 的 3 条 + 任务 3 的 5 条共 8 条会各自起一个静默 TCP 服务，故运行约需 3–5 秒）。

> 顺手确认一条**不该变的东西**：`node --test test/index.integration.test.js` 仍全绿。它的「T3 入参推导超时不计入假死计数」那条（`index.integration.test.js:119-196`）只打一次非入参推导的超时（计数 1 < 阈值 3），走不到闩锁分支 ⇒ 不受影响。**若它 FAIL，说明你在 3c/3d 里把判定写错了。**

- [ ] **步骤 5：改动清单**

| 文件 | 动作 | 锚点（行号已复核；见 §0.4 的重定位说明） |
|---|---|---|
| `MCP/index.js` | 修改（+约 50 行 / -1 行） | `:2705-2707`（`contentErrorResponse` 加可选第三参）、`:2708` 之后（新增 `gabpWedgeErrorResponse`）、`:2755-2756` 之间（短截断）、`:2775-2785`（阈值就地返回） |
| `MCP/test/gabpWedge.test.js` | 修改（追加） | 5 条测试 |

**没有改 C#、没有改 `gabpClient.js`（那是任务 1）、没有改 `timeoutPolicy.js` / `config.json`、没有重编译、没有构建、没有启动游戏。**

---

## 7. 任务 4：护栏与全量回归

**交付物：** 一组**源码切片护栏**（把本计划的 4 个改动位置与既有 6 处源码切片断言的锚点顺序全部钉死），加一次全量测试。**本计划的核心风险不是逻辑写错，是「顺手改一下」把别人的切片断言碰碎** —— §0.3 已确认本计划碰不到它们，但那份结论是**今天**的；计划 32 落地后会 +4 行，任何人下一次动 `index.js` 都不该再靠人眼复核。

**文件：**
- 修改：`MCP/test/gabpWedge.test.js`（追加）

- [ ] **步骤 1：编写失败的测试**

追加到 `MCP/test/gabpWedge.test.js` 末尾：

```javascript
// ---------------------------------------------------------------------------
// 任务 4：护栏
// ---------------------------------------------------------------------------
test('[36] 护栏：闩锁短截断必须落在 ensureGabpConnected 之后、callOpts 之前', () => {
  const i = indexSource.indexOf('async function callGABPTool(');
  assert.ok(i > 0, '找不到 callGABPTool');
  const head = indexSource.slice(i, i + 1600);
  const conn = head.indexOf('await ensureGabpConnected()');
  const gate = head.indexOf('if (gabpWedged) {');
  const opts = head.indexOf('const callOpts = opts || {};');
  assert.ok(conn > 0, '找不到 ensureGabpConnected');
  assert.ok(gate > conn, '闩锁必须在 ensureGabpConnected 之后（否则游戏重启后会给错错误）');
  assert.ok(opts > gate, '闩锁必须在 callOpts 之前（它是纯旁路，不该动 opts）');
});

test('[36] 护栏：阈值那一路必须就地返回结构化错误（不能落回通用 gabpErrorResponse）', () => {
  const i = indexSource.indexOf('} else if (isTimeout) {');
  assert.ok(i > 0, '找不到超时分支');
  const block = indexSource.slice(i, i + 1200);
  assert.match(block, /noteGabpAbandoned\(rbsName\)/, '每次超时都要留放弃记录');
  assert.match(block, /setGabpWedge\(\)/, '阈值必须置闩');
  assert.match(block, /return gabpWedgeErrorResponse\(rbsName\)/, '阈值那一路必须就地返回');
});

test('[36] 护栏：既有的源码切片锚点顺序不得改变（toolchainFixes / quickTest / hotreload 三家共 6 处）', () => {
  const ordered = [
    '// ---------- 快速测试触发链路（与 GABP 无关） ----------', // quickTest.test.js:28 起点（:1430）
    '// Tool definitions',                                       // quickTest.test.js:28 终点（:1675）
    'function toolListLine(',                                   // toolchainFixes.test.js:121 起点（:3281）
    'function buildMenuNode(',                                  // toolchainFixes.test.js:121 终点（:3290）
    'if (isGabpMirrorName(name)) {',                            // quickTest.test.js:42 起点（:3759）
    "if (name === 'rimworld.dpa_patch_methods') {",             // toolchainFixes.test.js:255 起点（:4337）
    "case 'start_game': {",                                     // toolchainFixes.test.js:264（:4356）
    "case 'stop_game': {",                                      // toolchainFixes.test.js:264/266（:4371）
    "case 'get_game_status': {",                                // toolchainFixes.test.js:266 终点（:4384）
    "case 'start_quick_test': {",                               // quickTest.test.js:41（:4412）
  ];
  let prev = -1;
  let prevName = '(文件开头)';
  for (const name of ordered) {
    const at = indexSource.indexOf(name);
    assert.ok(at > 0, `锚点不存在: ${name}`);
    assert.ok(at > prev, `锚点顺序被破坏：「${name}」出现在「${prevName}」之前 —— 有切片断言会取错区间`);
    prev = at;
    prevName = name;
  }
});

test('[36] 护栏：新错误码与形状参照物都必须是「各出现且仅出现一次」的 errorCode 字面量', () => {
  assert.equal(indexSource.includes("name: 'GABP_CHANNEL_WEDGED'"), false, '它是 errorCode，不是工具名');
  assert.equal((indexSource.match(/'GABP_CHANNEL_WEDGED'/g) || []).length, 1,
    'GABP_CHANNEL_WEDGED 只应作为 errorCode 出现一次（误复制到多处会让调用方无法按码归因）');
  assert.equal((indexSource.match(/'AREA_OP_CHANNEL_BUSY'/g) || []).length, 1,
    '既有形状参照物 AREA_OP_CHANNEL_BUSY（:4322）不得被顺手改掉');
  assert.match(indexSource, /function contentErrorResponse\(errorCode, message, detail\)/,
    'contentErrorResponse 必须保持第三参可选的签名（见 §0.6）');
});
```

- [ ] **步骤 2：运行测试验证通过（不是失败）**

运行：`node --test test/gabpWedge.test.js`
预期：**13 PASS / 0 FAIL**。这 4 条是**护栏**，任务 3 做完后它们本来就该过 —— 它们的作用是从此以后让「碰碎别人切片」变成一次响亮的失败，而不是一个静默的错误区间。

若「既有的源码切片锚点顺序」FAIL：**说明有人在本计划之外动过 `index.js`**（最可能是计划 32 的 +4 行位移，但位移不改顺序，所以 FAIL 一定意味着**顺序**变了）。去查是谁，不要改锚点表。

- [ ] **步骤 3：全量回归**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：**全部 10 个测试文件全绿**（原有 9 个 + 本计划新建的 `gabpWedge.test.js`；若计划 32 已落地则是 11 个）。`node --test` 结尾必须出现 `# fail 0`。

**重点逐个确认这 3 个含源码切片断言的文件**（它们是最容易被碰碎的）：

| 文件 | 相关断言 | 为什么关键 |
|---|---|---|
| `test/toolchainFixes.test.js` | `:121`（`toolListLine`→`buildMenuNode`）、`:255-260`（`dpa_patch_methods`）、`:263-268`（`start_game`/`stop_game`） | 切片区间若被本计划的插入撑大或串味，断言会在**不相关**的地方失败 |
| `test/quickTest.test.js` | `:28`、`:45`、`:47`、`:52` | 护栏 2 断言「`start_quick_test` 必须排在 GABP 镜像分支之后」——本计划在 `:2747` 之后加代码，理论上不影响，但它一旦 FAIL 就是真问题 |
| `test/index.integration.test.js` | `:119-196` | 假死计数与 `forceReconnect` 的既有行为（任务 3 的 `return` 之后 `forceReconnect` 仍必须照常被调用） |

- [ ] **步骤 4：确认没有碰不该碰的东西**

运行（只读核验，不修改）：

```powershell
$root = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
foreach ($f in @('MCP\index.js','MCP\gabpClient.js','MCP\longTask\timeoutPolicy.js','MCP\config.json')) {
  $p = Join-Path $root $f
  "{0}  {1} 行  修改时间 {2}" -f $f, [System.IO.File]::ReadAllLines($p).Count, (Get-Item $p).LastWriteTime
}
```

预期：**只有 `MCP/index.js` 和 `MCP/gabpClient.js` 的修改时间变了**；`timeoutPolicy.js` 与 `config.json` 的修改时间必须还是改动前的时间戳。`MCP/index.js` 的行数从 **5460** 增加到约 **5470 ± 5**（本计划净增约 110 行，若计划 32 已落地则再多 4）。

- [ ] **步骤 5：改动清单（替代 commit —— 本仓库不是 git 仓库）**

> ⚠️ **本仓库没有 `.git`。** `AGENT-BRIEF.md` §3.5 记着一条真实事故：一个子代理想用 `pwsh` 做行级重排，脚本中途抛异常却没终止，最后用空数组覆写了整个 1212 行的计划，**且无备份可恢复**。所以：
> 1. **本计划不含任何 `git commit` 步骤**，每一步的收尾一律是「改动清单」。
> 2. **禁止用 `pwsh` 改文件** —— 行级插入 / 重排 / 删除只能用 `edit` 工具（`edit` 是「不匹配就报错、文件不动」；shell 脚本是「静默写成空」）。
> 3. 任何写文件的 PowerShell 脚本，第一行必须是 `$ErrorActionPreference = 'Stop'`。
> 4. 每次改完立刻核对：`[System.IO.File]::ReadAllLines($p).Count`。

**全计划改动清单（四个任务合起来）：**

| 文件 | 动作 | 锚点 | 任务 |
|---|---|---|---|
| `MCP/gabpClient.js` | 修改（纯注释，+14 行） | `:566-569` `setTimeout` 回调 | 1 |
| `MCP/index.js` | 修改（+约 110 行 / -1 行） | `:2518` 之后（状态块 + 6 函数）、`:2705-2707`、`:2708` 之后、`:2755-2756` 之间、`:2759`、`:2775-2785`、`:2787`、`:5450-5459` | 2 / 3 |
| `MCP/test/gabpWedge.test.js` | **新建** | 13 条测试 | 1 / 2 / 3 / 4 |

**明确没有碰：** 任何 `.cs`、任何 `Assemblies/` / `runtime/` 文件、`MCP/longTask/timeoutPolicy.js`、`MCP/config.json`、`MCP/package.json`、任何既有测试文件、`docs/` 下除本计划外的任何文件（含吐槽板 `docs/mcp-吐槽板.md` —— 主代理负责写批注）。

---

## 8. 自检

**1. 规格覆盖度**（裁决报告 §7.1 的四件，本计划逐件对应）：

| 规格项 | 对应任务 | 覆盖方式 |
|---|---|---|
| ① `MCP/index.js:2775-2785` 楔死时返回**结构化错误**（区分「排队中」vs「已丢弃」） | 任务 2 + 任务 3 | 任务 2 建状态与判定函数；任务 3 接进 `callGABPTool` 并产出 `GABP_CHANNEL_WEDGED` |
| ② 仿 `AREA_OP_CHANNEL_BUSY`（`MCP/index.js:4321-4324`）的既有形状 | 任务 3 步骤 3a/3b | 复用同一个 `contentErrorResponse` 构造器；任务 3 的「2 参兼容」测试守住 14 个既有调用点 |
| ③ `MCP/gabpClient.js:566` 加注释写明「超时 ≠ 取消」 | 任务 1 | 纯注释 + 一条把它钉在那个位置上的切片断言 |
| ④ 离线断言 | 任务 1/2/3/4 | 13 条测试，全部用「静默 TCP + 真 `GabpClient._request`」或纯源码切片，**不连游戏、不动游戏状态** |

裁决报告 §7.1 的三条「明确不建议」也逐条落到 §3.5（不加白名单 / 不改 `config.json` / 不动 RBS），**没有遗漏**。

**2. 占位符扫描** —— 全文无「待定 / TODO / 后续实现 / 补充细节 / 添加适当的错误处理 / 为上述代码编写测试 / 类似任务 N」。每个代码步骤都带可直接粘贴的代码块；每个断言都带**具体的失败信息文案**（例如「必须写出「超时 ≠ 取消」」），便于执行者区分「实现写错」和「锚点写错」。

**3. 类型 / 命名一致性**（逐个核对过，无漂移）：

| 名字 | 定义处 | 使用处 | 一致？ |
|---|---|---|---|
| `gabpWedged` | 任务 2 步骤 3a | 3c 短截断、钩子 `getGabpWedgeState`、护栏测试 | ✅ |
| `gabpNextProbeAt` | 3a | 3c、`setGabpWedge`/`clearGabpWedge`、`openGabpWedgeProbeWindow` | ✅ |
| `gabpAbandonSeq` / `gabpDrainedThroughSeq` / `gabpAbandoned` | 3a | `noteGabpAbandoned` / `noteGabpDrained` / `gabpAbandonDisposition` / `gabpWedgeErrorResponse` / 钩子 | ✅ |
| `GABP_WEDGE_PROBE_AFTER_MS`（20000） | 3a | `setGabpWedge`、3c、`gabpWedgeErrorResponse`、测试 `body.probeWindowMs` | ✅ |
| `GABP_ABANDONED_KEEP`（8） | 3a | `noteGabpAbandoned` | ✅ |
| `gabpAbandonDisposition(item)` | 3a | 钩子、`gabpWedgeErrorResponse` | ✅ |
| `noteGabpAbandoned` / `noteGabpDrained` / `setGabpWedge` / `clearGabpWedge` | 3a | 3b/3c/3d/3e、钩子 | ✅ |
| `gabpWedgeErrorResponse(rbsName)` | 任务 3 步骤 3b | 3c、3d | ✅ |
| `contentErrorResponse(errorCode, message, detail?)` | 任务 3 步骤 3a | 3b + 14 个既有 2 参调用点 | ✅ |
| `GABP_CHANNEL_WEDGED` | 任务 3 步骤 3b（**只此一处**） | 响应体、测试断言、护栏测试 | ✅ |
| `disposition` 取值 `still-queued-game-side` / `drained-already-executed` | §3.2 | `gabpWedgeErrorResponse`、`_testHooks.getGabpWedgeState`、全部测试断言 | ✅ |
| 钩子 `getGabpWedgeState` / `openGabpWedgeProbeWindow` / `resetGabpWedge` | 3f | 全部测试 | ✅ |

**4. 已知并接受的局限（明写，不藏）**

- 本计划**不能**告诉调用方「那次动作最终到底执行了没有」—— MCP 侧没有任何能观测游戏侧 `Pending` 队列的通道。它只能给出**基于 FIFO 语义的推断**（§3.2）以及**推断的前提**。
- 楔死的**成因**在 RimBridgeServer 侧（本计划一个字都不改），本计划只负责「把超时变成可读、可行动、不误导的失败态」。
- 放弃记录上限 8（`GABP_ABANDONED_KEEP`）。一次重试超过 8 次时，最早的那几项会被挤出报告 —— 但它们一定是最早被排空的那些（若有成功响应），信息损失可接受。

---

## 9. 执行交接

计划已完成并保存到 `docs/issue-triage/plans/2026-10-03-36-gabp-wedge-distinguishable-failure.md`。**两种执行方式：**

**1. 子代理驱动（推荐）** —— 每个任务调度一个新的子代理，任务间进行审查，快速迭代。必需子技能：使用 `superpowers:subagent-driven-development`。

**2. 内联执行** —— 在当前会话中使用 `superpowers:executing-plans`，批量执行并设有检查点。必需子技能：使用 `superpowers:executing-plans`。

**执行前必读的三条：**

1. **§0.4 的行号耦合** —— 先核对 `MCP/index.js` 的总行数。若已 > 5460（计划 32 已落地），**不要照抄本计划的行号**，改用 §0.4 给的文本锚点重定位。
2. **禁止用 `pwsh` 改文件** —— 行级插入 / 重排 / 删除只能用 `edit` 工具。本仓库无版本控制，写坏了没有第二次机会（`AGENT-BRIEF.md` §3.5 记着一条把 1212 行计划覆写成 0 字节的真实事故）。
3. **禁止编译、禁止启动游戏、禁止调用任何会改变游戏状态的 MCP 工具。** 本计划的 13 条测试全部离线。

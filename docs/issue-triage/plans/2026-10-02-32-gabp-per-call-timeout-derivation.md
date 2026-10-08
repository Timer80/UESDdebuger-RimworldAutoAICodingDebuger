# GABP 镜像每次调用超时预算推导实现计划（吐槽板 [24] + [32] 同源合并）

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让 `rimworld.start_debug_game_ready`（[32]）与 `rimworld.select_pawn`（[24]）这两个 GABP 镜像工具拿到**与实际耗期相称的本地等待预算**——现在它们因为在 `MCP/longTask/timeoutPolicy.js` 的三张白名单里**一条都没登记**而一律落到 `MCP/gabpClient.js:67` 的 `DEFAULT_REQUEST_TIMEOUT_MS = 30000`，于是调用方传了 `timeoutMs:300000` 也只会被 30 秒砍掉（参数在**传输层有效**、在**控制层无效**，即「参数摆设」）。

**架构：** 一个纯数据表文件（`timeoutPolicy.js`）负责算出本次调用的 `timeoutMs`，`MCP/index.js` 的镜像派发处把它当 `opts` 一路传到 `gabpClient._request` 的 `setTimeout`。本计划把两个工具登记进 `INPUT_DERIVED`（T3，按入参推导，带**每工具专属上限 `capMs`**）与 `LONG_DEFAULTS_MS`（T2 长默认，覆盖不传 `timeoutMs` 的情形）；上限取 **120000**，因为 300000 直接放行会算出 305000ms，超过宿主客户端截止线的实测上界 192.7s（`triage/04-startup-call-timeout-ambiguity.md:98`），失败点会从「30s 超时」平移成 `-32001`——痛点没消除、只换了错误码。另外新增一个与 `inputDerived` **分开的** `expectedLongWait` 标记，让「这次长等待是预期内的」不再被 `MCP/index.js:2773-2785` 的「连续 3 次 GABP 假死 → 强制重连」计数误伤。最后把 `MCP/index.js:4292` 那个幂等探测从「白吃 30 秒」改成显式 5 秒短预算。

**技术栈：** Node.js **v24.13.0**（实测版本）、ESM（`MCP/package.json:6` 的 `"type":"module"`）、Node 内置测试运行器（`MCP/package.json:9` 的 `"test": "node --test \"test/*.test.js\""`）、`node:test` + `node:assert/strict`。**不引入任何依赖、不改任何 `.cs`、不重编译模组、不动 RimBridgeServer 一个字节。**

---

## 0. 行号复核（本计划所有行号均已逐条回读，**以本节为准**）

> **复核方法（重要）：** 下表所有行号都用
> `[System.IO.File]::ReadAllLines('<绝对路径>')[<n>-1]` 复核过，**不是**用带行号的文件阅读工具的输出
> （那种输出会随编辑漂移 —— `docs/issue-triage/AGENT-BRIEF.md` §3.5 记着它少报过 27 行、且同文件内偏移不一致）。
> 执行者若要再核实，用同样方式，**不要相信任何「读到的行号」**；数总行数也用 `ReadAllLines(...).Count`
> （`Get-Content | Measure-Object -Line` 会少算）。
>
> 复核时确认：`MCP/index.js` **TOTAL = 5460 行**；`MCP/gabpClient.js` **TOTAL = 727 行**；
> `MCP/longTask/timeoutPolicy.js` **TOTAL = 154 行**；`MCP/test/timeoutPolicy.test.js` **TOTAL = 230 行**；
> `MCP/test/toolchainFixes.test.js` **TOTAL = 294 行**；`MCP/package.json` 的 `scripts.test` 在第 **9** 行。

### 0.1 本计划要改的位置（全部已复核）

| 对象 | 用途 / 行号（已复核） |
|---|---|
| `MCP/longTask/timeoutPolicy.js:21-25` | `INPUT_DERIVED` 三行登记 + 末尾追加 `start_debug_game_ready`（含 `capMs`）。任务 1 |
| `MCP/longTask/timeoutPolicy.js:28` | `LONG_DEFAULTS_MS` —— 追加两个工具。任务 1 |
| `MCP/longTask/timeoutPolicy.js:30` | `const DEFAULTS = {` —— `EXPECTED_LONG_WAIT` 插在 `:28` 之后、`:30` 之前。任务 1 |
| `MCP/longTask/timeoutPolicy.js:88-96` | `deriveFromArgs()`；**`:95` 是唯一需要加 `capMs` 夹紧的那一行**：`return Math.max(t.defaultMs, raw + overhead);` |
| `MCP/longTask/timeoutPolicy.js:136-151` | `timeoutOptsFor()`；`:141`（T4）、`:145`（T3）、`:148`（T2）、`:150`（`return null`）四个 return 都要带上 `expectedLongWait` |
| `MCP/longTask/timeoutPolicy.js:98-104` | `baselineMs()` —— `:101-102` 从 `LONG_DEFAULTS_MS` 取长默认值。**本计划的行为推演依赖它**（见 §4.2） |
| `MCP/index.js:2773` | `if (isTimeout && callOpts.inputDerived === true) {` —— 加 `expectedLongWait` 豁免。任务 2 |
| `MCP/index.js:2775-2785` | 假死计数分支：`:2776` `gabpConsecutiveTimeout++`、`:2777` 阈值判定、`:2781` `forceReconnect()` |
| `MCP/index.js:2518` | `const GABP_TIMEOUT_RECOVER_THRESHOLD = (config?.rimBridge?.timeoutRecoverThreshold) \|\| 3;` —— **「连续 3 次」的出处** |
| `MCP/index.js:4292` | `const probe = await gabpBridge.callTool('rimworld/get_game_info', {});` —— 补 5 秒短预算。任务 3 |
| `MCP/index.js:4287-4290` | 既有注释「…不转发 RimBridge（**避免 30s 超时**）」+ 「探测失败时保守走原转发」 |
| `MCP/index.js:4291-4298` | 已加载幂等短路；`:4299` 是未短路时的 `继续转发 RimBridge` |
| `MCP/index.js:4332-4335` | `_rbs` / `_derived = timeoutOptsFor(...)` / `_slow` / `_opts`。**本计划不动这四行**（见 §5.3 切片风险） |
| `MCP/index.js:5422` | `callGABPTool,` —— **它是被导出的**，任务 2 可以不经 `handleToolCall` 直接驱动它（见任务 2 的测试设计） |
| `MCP/index.js:5453-5454` | `setGabpBridge` / `getGabpConsecutiveTimeout` —— 现成的测试钩子，**本计划不需要新增钩子** |

### 0.2 只作旁证、本计划**不**修改的位置

| 对象 | 内容 |
|---|---|
| `MCP/gabpClient.js:67` | `const DEFAULT_REQUEST_TIMEOUT_MS = 30000;` —— 本条 bug 的最终落点 |
| `MCP/gabpClient.js:107-109` | `const rb = this._config.rimBridge \|\| {};` / `Number(rb.requestTimeoutMs)` / `this._requestTimeoutMs = … : DEFAULT_REQUEST_TIMEOUT_MS` |
| `MCP/gabpClient.js:563-565` | 三档取值：`opts.timeoutMs` → `opts.slow ? _requestTimeoutMs * 2` → `_requestTimeoutMs` |
| `MCP/gabpClient.js:566-569` | `setTimeout(...)` + `:568` 的错误串**唯一出处** |
| `MCP/gabpClient.js:674` | `await this._request('tools/call', { name, parameters }, opts);` —— `args` 原样转发给 RBS（**传输层有效**的证据） |
| `MCP/config.json` | **单行 JSON，只有 5 个键**（`gamePath` / `logPath` / `steamPath` / `appId` / `startTimeout`），**无 `rimBridge`、无 `requestTimeoutMs`** ⇒ `gabpClient.js:109` 必然回落到 `:67` |
| `triage/04-startup-call-timeout-ambiguity.md:98` | 「客户端截止线落在 **(131.5s, 192.7s]**」—— 本计划选 120000 的**唯一**数值依据 |

### 0.3 源码切片断言的风险评估（改前必读）

`MCP/test/toolchainFixes.test.js` 有两处**按字符切片**的源码断言，它们最容易被「顺手改一下」碰碎：

| 断言 | 位置 | 切片范围（锚点行号已复核） | 本计划是否碰得到 |
|---|---|---|---|
| `toolListLine` → `buildMenuNode` | `:121` | `index.js:3281` → `index.js:3290` | ❌ 不碰。本计划只在 `:2773` / `:4292` 各动 1 行 |
| `case 'start_game'` / `case 'stop_game'` | `:263-268` | `index.js:4356` → `4371` → `4384` | ❌ 不碰 |
| `dpa_patch_methods` 前置分支（另一处切片，`:255-260`） | `:255-260` | `index.js:4337` 起 **700 个字符**（≈ 到 `:4350`） | ❌ 不碰。本计划**不改** `:4333-4335`（裁决报告 §4.2 的改动 ①/② 全在 `timeoutPolicy.js` 里，`index.js` 一行控制流都不动） |

---

## 1. 背景：板上归因写反了，而且 `[24]` 根本没有正文

### 1.1 ⚠️ `[24]` 只有索引行、没有正文 —— 本计划有一半证据来自裁决报告而非板上原文

**核实方法与范围（AGENT-BRIEF §3.5：凡「不存在」必须写出搜索范围）：**

- `[System.IO.File]::ReadAllText('C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\mcp-吐槽板.md')`
  + 正则 `(?m)^## \[(23|24)\]` ⇒ **0 命中**（该命令返回 `heading count = 0`）。
- 同文件 `(?m)^## \[[0-9]+\]` 全量列举，实际存在的正文编号为：
  `[35] [34] [33] [32] [31] [30] [29] [28] [22] [21] [20] [19] [18] [17] [16] [15] [14] [13] [12] [11] [26] [01] [02] [03] [04] [05] [06] [07] [08] [09] [10]`（共 30 个）—— **没有 `[24]`，也没有 `[23]`**。
- `docs/issue-triage/triage/` 目录下**无** `24-*` / `23-*` 报告。

⇒ **板上关于 `[24]` 的全部可读文字只有索引行那一句话**（引自 `docs/mcp-吐槽板.md`，用锚点定位、不依赖行号）：

> `| **[24]** | `rimworld.select_pawn` 经 GABP **两次都 30s 超时**（`GABP 请求 'tools/call' 超时（30000ms)`）⇒ 设选择集没有可靠路径。绕过：`execute_csharp_code` 调 `Find.Selector.Select(thing,false,true)` | 🟠 中 | 待绕过 |`

⇒ 因此 **`[24]` 侧的一切根因结论都继承自 `triage/32-start-debug-game-ready-timeoutms-ignored.md` §2 的那一行**（「本仓库 `timeoutPolicy.js` 三张表未登记 `select_pawn`」）。本计划对 `[24]` 的参数取值只做**同源最小登记**，并在 §2.3 把「`select_pawn` 真的需要 >30s」明确标为**未经独立证据支持的假设**。

### 1.2 板上归因说反了：砍掉 `timeoutMs` 的是**本仓库**，不是 GABP 内建 30s

板上 `[32]` 标题写的是「**GABP 内建 30s 超时**先砍掉」，索引行写的是「GABP 内建 30s 砍掉」。**这是错的。** 复核后的完整因果链（每跳行号见 §0.1 / §0.2）：

| # | 位置 | 发生了什么 |
|---|---|---|
| 1 | `index.js:4291` | `start_debug_game_ready` 进入幂等短路判定 |
| 2 | `index.js:4292` | 幂等探测 `callTool('rimworld/get_game_info', {})` —— **没传 opts，照吃 30s 默认预算** |
| 3 | `index.js:4294-4298` | 冷启动（`playable=false` / `mapCount=0`）**不短路** |
| 4 | `index.js:4299` | 落到「继续转发 RimBridge」的通用路径 |
| 5 | `index.js:4333` | `timeoutOptsFor('rimworld/start_debug_game_ready', args, config)` ⇒ **`null`**（三张表全未登记，§1.3） |
| 6 | `index.js:4335` | `const _opts = _derived \|\| (_slow ? { slow: true } : null);` ⇒ **`_opts = null`**（`_slow` 的正则 `^(rimworld\|rimbridge)\.(load_game\|save_game\|execute_debug_action)$` 也不命中） |
| 7 | `index.js:2756-2757` | `const callOpts = opts \|\| {};` ⇒ **`{}`**；`gabpBridge.callTool(rbsName, args, {})` |
| 8 | `gabpClient.js:674` | `_request('tools/call', { name, parameters }, {})` —— **注意 `parameters` 就是 `args`：`timeoutMs:300000` 原样发给了 RBS** |
| 9 | `gabpClient.js:563-565` | `opts.timeoutMs` 是 `undefined`、`opts.slow` 是 `undefined` ⇒ 落回 `this._requestTimeoutMs` |
| 10 | `gabpClient.js:107-109` | `MCP/config.json` **只有 5 个键、无 `rimBridge`** ⇒ `_requestTimeoutMs = DEFAULT_REQUEST_TIMEOUT_MS` |
| 11 | `gabpClient.js:67` | **`const DEFAULT_REQUEST_TIMEOUT_MS = 30000;`** |
| 12 | `gabpClient.js:566-569` | 30 秒后 `reject`，`:568` 产出板上那条错误串 |

⇒ **这就是「参数摆设」的机制**：`args.timeoutMs` 在**第 8 步（传输层）确实有效**——RBS 真的收到了 300000 并会老老实实等最多 300 秒（反编译 `RimBridgeServer.LifecycleCapabilityModule.StartDebugGameReady`，memberId `46e27953d7324877a067e7458bf2c0d5:06000D9A:M`，默认 `timeoutMs = 120000`、描述 `"Maximum time to wait in milliseconds"`、无 `Math.Min`）；但它在**第 5-6 步（控制层）从未参与本地等待预算的计算**。所以游戏照样造好了殖民地，RPC 却只等了 30 秒。

### 1.3 错误串逐字同源：全仓库只有一处能产出它

**核实方法与范围：** 在 `MCP/` 目录下递归检索 `*.js` / `*.json`、**排除 `node_modules`**，用模式 `GABP 请求`（**字面量，无 `|`**）检索 `MCP/logs/`、`MCP/*.js`、`MCP/test/`、`MCP/longTask/`、`MCP/config.json`、`MCP/toolConfig.json`、`MCP/*.json`，**命中 2 处**：

| 命中 | 是否是错误串 |
|---|---|
| `gabpClient.js:568` `` `GABP 请求 '${method}' 超时（${timeoutMs}ms）` `` | ✅ **唯一的错误串出处** |
| `index.js:2784` `` `GABP 请求超时计数 ${gabpConsecutiveTimeout}/${…}（${rbsName}）` `` | ❌ 这是 `log('DEBUG', …)` 的**日志**文本，不是抛给调用方的错误串 |

⇒ `[32]` 正文与 `[24]` 索引行里的那条报错，**逐字只能由 `gabpClient.js:568` 这一个 `setTimeout` 产出**，即同一个 `_requestTimeoutMs` 的同一次 reject，只是落在不同工具上。

**关于两处右括号不一致：** 板上 `[24]` 索引行写的是全角 `（` + **半角** `)`（`超时（30000ms)`），`[32]` 正文写的是全角 `（` + 全角 `）`（`超时（30000ms）`）。**这只是一个右括号的全/半角笔误，不构成「不同源」的证据**（同一个模板字符串不可能产出两种括号）。真正的同源证据是上面那张表：**错误串全文在仓库自有代码里只有一处**。

**关于字面量 `30000`：** 同范围检索 `30000` 共 20+ 处命中，但**没有一处是本条因果链上的预算来源**，逐个排除：`gabpClient.js:55` `BACKOFF_CAP_MS`（重连退避上限，不同子系统）、`timeoutPolicy.js:15` `FALLBACK_DEFAULT_MS`（同一策略文件的镜像常量，不参与 `gabpClient` 构造）、`timeoutPolicy.js:36` `taskSnapshotIntervalMs`（长任务快照间隔）、`runner.js:33` `RPC_TIMEOUT_MS`（**长任务运行器自己的超时，与 GABP 镜像通道无关**）、其余全在 `test/` 下。⇒ `DEFAULT_REQUEST_TIMEOUT_MS = 30000`（`gabpClient.js:67`）是这条链上**唯一**的 30000 来源。

### 1.4 三张白名单（`timeoutPolicy.js`）为什么全都没登记 —— 逐张复核

| 档 | 行号 | 现内容 | 对 `start_debug_game_ready` | 对 `select_pawn` |
|---|---|---|---|---|
| T4 显式覆盖 | `:106-109` 读 `t.toolTimeouts` | `MCP/config.json` **无 `rimBridge`** ⇒ `readThresholds` 的 `:63-65` 把 `rb` 容错成 `{}`、`:82-84` 把 `toolTimeouts` 容错成 `{}` | ❌ 未命中 | ❌ 未命中 |
| T3 入参推导 | `:21-25` `INPUT_DERIVED` | `play_for{durationMs}` / `step_game_ticks{timeoutMs}` / `play_until_letter{timeoutMs}` | ❌ 未命中（`:90-91` 未命中即 `return null`） | ❌ 未命中（且它**没有**时长型入参，本就不该进 T3） |
| T2 慢档 | `:18` `SLOW_BARE_NAMES` | `load_game` / `save_game` / `execute_debug_action` / `create_hook` | ❌ 未命中 | ❌ 未命中 |
| T2 长默认 | `:28` `LONG_DEFAULTS_MS` | `step_game_ticks→120000` / `play_until_letter→300000` | ❌ 未命中 | ❌ 未命中 |
| ⇒ `:150` | `return null` | | ⇒ `index.js:4335` 的 `_opts = null` | ⇒ 同左 |

### 1.5 旁证：本仓库**早就知道**这个 30s，而且只修了一半

`MCP/index.js:4287-4288` 的注释原文（逐字复核）：

```javascript
    // 修复 2（fix-debug-server-defects）：start_debug_game_ready 已加载幂等短路——
    // 殖民地已加载（playable=true）时直接返回 ready，不转发 RimBridge（避免 30s 超时）。
```

作者明确知道这个工具会被 30s 砍掉，写的对策是「**已加载就短路**」（`:4291-4298`）。但短路只在 `playable === true` / `mapCount > 0` / `status === 'game_loaded'` 时成立（`:4294`）；**从主菜单冷启动造新殖民地必然不短路**，走到 `:4299` 继续转发，原样撞上 30s。

⇒ 这不是「第三方不可控」，是一个**已开工、只覆盖了 easy case 的自家 workaround**。责任归属毫无争议：**本仓库（MCP 侧），不是 RimBridgeServer。**

### 1.6 为什么严重度维持 🟠、不上调 🔴

- **不会得出事实相反的结论**：错误串**自曝真实预算**（`超时（30000ms）`），调用方一眼能看出「我其实只等了 30 秒」。信息没被抹掉。这与 `[01]` / `[31]`（「工具报成功但事情没做」）和 `[04]`（`-32001` 什么都不给，只能靠 `get_game_status` 反查）**不同类**。
- **但每次必踩**：冷启动造殖民地是所有调试会话的必经步骤；踩法具有欺骗性——调用方传 300000 会形成「我给了 5 分钟预算」的错误心智模型。
- **修复成本极低**（一张数据表 + 两行控制流 + 断言），收益/成本比在整块板子里排前列。

### 1.7 与 `[04]` **不同源**，不得并入计划 04（复核确认）

| 条目 | 通道 | 报错形态 | 超时预算由谁定 | 根因层 | 本计划处置 |
|---|---|---|---|---|---|
| `[04]` / `[30]` `start_game` / `start_quick_test` | **原生 MCP 工具**，**不经 GABP**（`index.js:4356-4369` / `:4412-4414`，均已复核：`4356` `case 'start_game':`、`:4358` 调 `startGame(...)`；`4412` `case 'start_quick_test':`、`:4414` 调 `startQuickTest(...)`） | `MCP error -32001: Request timed out` | **MCP SDK 客户端请求截止线**（`@modelcontextprotocol/sdk/dist/esm/shared/protocol.js:712-713`），实测落在 **(131.5s, 192.7s]** | 宿主客户端 / IDE 配置（**不在本仓库内**） | 🚫 **不并入**。根因层、报错形态、修复文件三者全不同 |
| `[24]` `rimworld.select_pawn` | GABP 镜像 | `GABP 请求 'tools/call' 超时（30000ms)` | **本仓库 `gabpClient.js` 的 30s 请求预算** | 本仓库（三张表未登记） | ✅ 本计划覆盖 |
| `[32]` `rimworld.start_debug_game_ready` | GABP 镜像 | `GABP 请求 'tools/call' 超时（30000ms）` | 同上，同一个 30000 常量 | 本仓库 | ✅ 本计划覆盖 |

⇒ **`[32]` 板上写的「与 [04]/[24] 同源」应改为「与 [24] 同源、与 [04] 不同源」。**（板上这两处的行号引用已失效，位置见 §0 末的漂移说明；本计划一律用锚点文本定位条目，不引用板上行号。）

> **⚠️ 板上行号会漂：** 复核时 `docs/mcp-吐槽板.md` 已 **1470 行**，`## [32]` 标题在 **第 286 行**、`## [31]` 在 **第 307 行**。裁决报告 `triage/32-…:13` 记的「`:275-293` / `:275` / `:296`」**已经全部失效**（那是报告写就时的位置，板子后来又被扩写了）。索引行 `[24]`（第 41 行）与 `[32]`（第 49 行）仍在原位。⇒ **本计划只引用 `timeoutPolicy.js` / `index.js` / `gabpClient.js` / 测试文件的行号（已复核且这些文件本计划不改到无关处），不引用吐槽板行号。**

---

## 2. 范围

### 2.1 ✅ 进计划

| 项 | 落点 | 理由 |
|---|---|---|
| **① `[32]` `start_debug_game_ready`：T3 登记 + `capMs` 上限** | `timeoutPolicy.js:21-25` + `:95` | 板上复现**明确传了** `timeoutMs:300000`；让入参能穿透、同时把推导值夹在客户端截止线下界以下 |
| **② `[32]` `start_debug_game_ready`：T2 长默认登记（覆盖不传 `timeoutMs`）** | `timeoutPolicy.js:28` | **调用方不传参是默认情形**，只修 T3 会漏掉它；且它自己的默认值就是 120000 |
| **③ `[24]` `select_pawn`：T2 长默认登记** | `timeoutPolicy.js:28` | 与 ② 同源；它**没有**时长型入参，进不了 T3 |
| **④ 预期内长等待的豁免（`expectedLongWait`）** | `timeoutPolicy.js` 新增 + `index.js:2773` | **不修这一条，①②③ 修好之后反而更糟**——见 §4.2 |
| **⑤ 幂等探测的显式短预算** | `index.js:4292` | 该探测失败就保守转发（`:4290`），**不需要 30s**；现在白烧 30 秒才轮得到真正要等的转发 |
| **⑥ 回归断言** | `test/timeoutPolicy.test.js` + 新建 `test/mirrorLongWaitBudget.test.js` | 决策表必须钉死，否则下一个人改表就会悄悄破功 |

### 2.2 ❌ 明确排除

| 排除项 | 理由 |
|---|---|
| **改 RimBridgeServer / 任何 `.cs`** | 反编译已证 RBS 侧**守规矩**（`06000F09:M` 声明并默认 120000、`06000D9A:M` 两段完整消费、`060000D9:M` 无 per-request deadline）。**不改它一个字节。** |
| **把 `timeoutMs` 直接放行成 300000** | `timeoutPolicy.js:95` 会算出 `305000`，超过客户端截止线上界 192.7s ⇒ 失败点从 30s **平移**成 `-32001`，痛点未消、错误码更差，还顺带把 `[04]` 的坑再踩一遍。详见 §4.1 |
| **把 `capMs` 做成全局常量并套用到 `play_for`** | `play_for` 有专门的上界检查（`index.js:4146-4150`：超 `maxSingleCallTimeoutMs` 时返回 `MAX_SINGLE_CALL_TIMEOUT_EXCEEDED` 并给出两条出路），`start_debug_game_ready` **没有**等价机制，照搬会把长任务能力一起削掉。⇒ **`capMs` 必须是「每条目可选字段」，只作用于登记了它的那个工具** |
| **改 `MCP/config.json` 加 `rimBridge` 段** | 本计划走**代码内**登记（对用户开箱即用，不需要重启改配置）；`config.json` 的 `rimBridge.longTask.toolTimeouts` 只作为**调参逃生舱**在文档里提一句（§4.5） |
| **在 schema 里把 `timeoutMs` 标成「仅游戏侧生效」**（板上 `:29x` 的第二个建议） | **错的**。它**本来就该在两侧都生效**——RBS 一直在等，是本仓库没让它等。标成「无效参数」等于把一个自家 bug 固化成契约 |
| **并入计划 04** | 根因层 / 报错形态 / 修复文件三者全不同，见 §1.7 |
| **改 `MCP/gabpClient.js`** | 它执行 `opts.timeoutMs` 的逻辑**完全正确**（`:563-565`）。问题在上游没人给它 `opts` |
| **新增 `_testHooks` 钩子** | `setGabpBridge`（`:5453`）与 `getGabpConsecutiveTimeout`（`:5454`）**已经现成**，且 `callGABPTool` **已被导出**（`:5422`）⇒ 任务 2 可以直接驱动真实代码路径，不需要任何新钩子，也**不依赖计划 22 的 `setGabpTools`** |

### 2.3 ⚠️ 标为「假设」的部分（**不得当作事实引用**）

- **「`select_pawn` 真的需要 >30 秒」是假设，不是已证事实。** 依据只有板上索引行一句话（「两次都 30s 超时」），而它的**正文不存在**（§1.1），也没有任何独立实测。它有两种可能：(a) 这个工具本来就慢 ⇒ 120000 有用；(b) 它当时其实是 GABP 卡死 ⇒ 120000 只是把 30 秒的失败换成 120 秒的失败。**本计划对它的处置刻意保守**：只登记 T2 长默认（**不**登记 `expectedLongWait`，见 §4.4），这样万一它真会长时间挂住，`index.js:2777` 的「连续 3 次 → 强制重连」自愈**依然有效**。落地后若实机确认 (a)，再由后续改动把它也登记进 `EXPECTED_LONG_WAIT`。
- **120000 这个数值本身有 RBS 侧证据（`06000F09:M` 的默认值），但它对 `[32]` 只是「下限侧的合理选择」，不是「实测所需值」。** 板上那次调用的真实耗时没有被记录（只记了 30 秒超时 + 之后 60 秒轮询到 `IN_GAME`）。
- **`triage/04-…:98` 的 (131.5s, 192.7s] 是推断**，依据是两组日志的成功/失败耗时，报告自己写明「DSH harness 的实际 timeout 配置我没读到（不在本仓库内）」。⇒ 本计划**不把它写进任何文案或错误消息**，只拿它做「推导值不能超过它」这一个内部约束。

---

## 3. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/longTask/timeoutPolicy.js` | 修改（4 个位置） | 全部策略：`INPUT_DERIVED` 登记 + `capMs` 上限、`LONG_DEFAULTS_MS` 登记、新增 `EXPECTED_LONG_WAIT` + `withLongWait`、`deriveFromArgs` 夹紧。**纯函数、无 I/O**，单测直接断言决策表。 |
| `MCP/index.js` | 修改（2 个位置，各 1 行） | ①`:2773` 的假死判定加上 `expectedLongWait` 豁免；②`:4292` 的幂等探测补显式 5 秒短预算。 |
| `MCP/test/timeoutPolicy.test.js` | 修改（末尾追加 8 个 `test`；既有 17 个） | 任务 1：钉死两个工具的**完整决策表**（传/不传 `timeoutMs`、`capMs` 夹紧、`expectedLongWait` 只在登记工具上出现、既有工具逐字节不变）。 |
| `MCP/test/mirrorLongWaitBudget.test.js` | **新建**（3 个 `test`） | 任务 2：经**真实导出** `callGABPTool` 证明预期内长超时**不**进假死计数、且对照组仍会自愈（假桥接，不起游戏、不改游戏状态）；任务 3：用源码切片断言钉死探测的 5 秒预算。 |

**明确不动：** `Source/` 下任何 `.cs`、`Assemblies/`、**`MCP/gabpClient.js`**（它只是执行 `opts.timeoutMs`，逻辑本身是对的）、`MCP/config.json`（本计划**不**加 `rimBridge` 配置项，只在文档里把它当调参逃生舱）、`MCP/package.json`、`MCP/toolConfig.json`、任何 `*.log`、`docs/mcp-吐槽板.md`、`docs/issue-triage/` 下除本计划外的任何文件。

**⚠️ 关于「改完要不要构建/部署」：** 本仓库**不是 git 仓库**，且 `runtime/` 目录下**不存在** `index.js` 副本（已用 `Get-ChildItem runtime -Filter index.js -Recurse` 核实，返回空；`MCP/` 下除 `node_modules` 外只有 `MCP\index.js` 与 `MCP\aggregator\index.js`）。⇒ **改 `MCP\index.js` 就是改到生效的那一份，无需任何构建 / 复制 / 部署步骤。**

---

## 4. 设计决策（本节是本计划的核心，**改代码前必读**）

### 4.1 🚩 陷阱①：不能简单「把 300000 放行」——必须 clamp 到 120000

`triage/04-startup-call-timeout-ambiguity.md:98` 用「7 次超时 / 8 次成功」的实测把宿主客户端截止线钉在 **(131.5s, 192.7s]**。若在 `INPUT_DERIVED` 里登记 `['start_debug_game_ready', { argKey: 'timeoutMs', overheadMs: 5000 }]` 而**不加夹紧**：

```
deriveFromArgs → Math.max(30000, 300000 + 5000) = 305000
```

**305000 > 192.7s** ⇒ 失败点从「30 秒超时」**平移**成 `-32001`，痛点没消除、只是换了个错误码，还顺带把 `[04]` 的坑再踩一遍。

⇒ **安全值是 120000**，理由有两条、互相独立：
1. **120000 < 131.5s**，落在客户端截止线**下界以下** ⇒ 不可能撞上 `-32001`；
2. **120000 正是 RBS 自己的默认值**（`06000F09:M` 的 `int timeoutMs = 120000`）⇒ 我们本地等的时长与游戏侧自己打算等的时长**天然对齐**，不会「本地放弃、游戏侧还在跑」。

⇒ **实现方式是给 `INPUT_DERIVED` 的条目加一个可选的 `capMs` 字段**（只作用于登记了它的工具），**不是**全局常量、**不是**改 `Math.max` 的语义。夹紧点：`timeoutPolicy.js:95`。

### 4.2 🚩 陷阱②：走 `LONG_DEFAULTS_MS` 分支会拿到 `inputDerived: false` —— 以及一个裁决报告没算到的连带效应

**第一层（裁决报告已指出）：** `timeoutPolicy.js:148` 的 T2 长默认分支固定返回 `inputDerived: false`。而 `index.js:2773` 的豁免条件是 `callOpts.inputDerived === true` ⇒ **一次合法的 120 秒超时会被计进 `index.js:2775-2785` 的「连续 3 次假死 → 强制重连」**（阈值 `:2518` = 3）。冷启动时游戏经常超过 30 秒，**连续三次这种超时就会打断正在跑的启动流程**。

**第二层（本计划复核时发现，裁决报告 §4.2 的判断在这里是错的）：**

裁决报告原文说：「改动 ② 会让推导路径带上 `inputDerived: true`（`timeoutPolicy.js:145`：`derived !== baselineMs` 成立）」。

**这句话只在「不加 clamp」时成立。** 本计划按 §4.1 **必须**加 clamp，而 clamp 的上限（120000）**恰好等于**我们要写进 `LONG_DEFAULTS_MS` 的值。于是：

```
baselineMs('start_debug_game_ready', t)          // timeoutPolicy.js:101-102
  = Math.max(t.defaultMs, LONG_DEFAULTS_MS.get(...)) = Math.max(30000, 120000) = 120000
derived（传 timeoutMs:300000，被 cap 夹过）
  = min(305000, 120000) = 120000
:145  inputDerived = (derived !== baselineMs) = (120000 !== 120000) = false   ← ❌ 不是 true
```

⇒ **T3 分支也会拿到 `inputDerived: false`。** 也就是说，**光靠「两处都登记 + 加 clamp」这一套改法，`start_debug_game_ready` 的两条路径（传参 / 不传参）都会失去假死计数豁免** —— 这正是裁决报告让它「显式记录权衡」的那件事，**但它低估了范围**：不只是改动 ①，是**两条路径全中**。

**⇒ 因此本计划的设计是：不复用 `inputDerived`，另立一个语义正交的 `expectedLongWait`。**

### 4.3 `expectedLongWait` 为什么不复用 `inputDerived`

| | `inputDerived`（既有） | `expectedLongWait`（新增） |
|---|---|---|
| 语义 | 「这个超时**值**是从入参推导来的」 | 「这次**长等待**本来就是预期内的」 |
| 计算方式 | `derived !== baselineMs`（`:145`）——**比较值**，clamp 后会与 baseline 撞成相等 | `EXPECTED_LONG_WAIT.has(bare)`——**查表**，与数值无关 |
| 消费者 | `index.js:2773` | `index.js:2773`（新增一个 `||` 支路） |
| 既有工具是否受影响 | —— | ❌ **完全不受影响**：只在登记了的工具上出现该键，且用 `withLongWait()` **条件附加**，所以 `timeoutPolicy.test.js:180-188` 那两条 `assert.deepEqual` 断言**逐字节不变** |

**为什么必须分开、不能把 `:148` 改成 `inputDerived: true`：** 那样会**连带**把 `step_game_ticks` / `play_until_letter` 在「不传 `timeoutMs`」时的超时也豁免掉，改变它们自 2026-09-17 以来一直存在的假死计数行为 —— 那是**本条完全不需要的行为变更**，属于把一个定点修复扩散成全局语义调整。

### 4.4 🚩 为什么 `select_pawn` **不**登记进 `EXPECTED_LONG_WAIT`（与 `[32]` 的差别待遇，不是遗漏）

豁免的**代价**是：一旦 GABP 真的卡死，这个工具的长时间无响应就**不再**触发 `index.js:2777` 的自动 `forceReconnect()` 自愈（那是 `triage/07-gabp-idle-no-fail-fast.md:113` 记录的那套机制）。

对 `[32]` 判「豁免值得」，依据充分：RBS 侧**本来就打算等最多 `timeoutMs`（默认 120s）**（反编译 `06000D9A:M` 是两段串行等待、第二段还要扣掉第一段已耗时），**游戏侧真的在跑、只是慢**，此时 `forceReconnect()` 会打断正在进行的启动流程 —— 这是明显更坏的失败模式。

对 `[24]` **证据不足**（§2.3：正文不存在，「需要 >30s」只是板上索引行的一句话）。两种失败模式的代价对比：

| | 保留计数（不豁免） | 加豁免 |
|---|---|---|
| GABP 真的卡死 | 3 次后自动 `forceReconnect()` 自愈 ✅ | **不再自愈**，一直失败到人工介入 ❌ |
| 工具真的只是慢 | 3 次长超时后被重连打断（可恢复、有 WARN 日志）⚠️ | 不会被打断 ✅ |

⇒ **失去自愈比过早重连坏得多**（重连是可恢复的、有 `:2778` 的 WARN 日志，且游戏侧不受影响）。所以 `[24]` **只登记 `LONG_DEFAULTS_MS`**：预算从 30s 提到 120s（解决「参数摆设」的一半），自愈机制原样保留。**这一条是刻意的不对称处理，§4.4 的理由必须写进代码注释**，否则下一个人会以为它写漏了。

### 4.5 落地后的完整决策表（**这就是任务 1 的断言清单**）

前提：`EMPTY_CFG = {}`（与 `test/timeoutPolicy.test.js:46` 同款）⇒ `t.defaultMs = 30000`、`t.slowMs = 60000`、`t.playForOverheadMs = 10000`。与真机 `MCP/config.json`（只有 5 个键、无 `rimBridge`）**完全一致**，所以这张表就是实机行为。

| 工具 | 入参 | 命中档 | `timeoutMs` | `inputDerived` | `expectedLongWait` | 改动前 | 改动后 |
|---|---|---|---|---|---|---|---|
| `start_debug_game_ready` | `{}` | T2 长默认 `:147-148` | **120000** | `false` | **`true`** | 30s ❌ | 120s ✅ |
| `start_debug_game_ready` | `{timeoutMs:300000}`（板上复现） | T3 + `capMs` `:143-145` | **120000**（`305000` 被夹） | `false` | **`true`** | 30s ❌ | 120s ✅ |
| `start_debug_game_ready` | `{timeoutMs:60000}` | T3 `:145` | **65000** | `true` | `true` | 30s ❌ | 65s ✅ |
| `start_debug_game_ready` | `{timeoutMs:5000}` | T3 `:145` | **30000**（`:95` 的 `Math.max` 下限） | `true` | `true` | 30s | 30s（无变化，预期内） |
| `select_pawn` | `{}` | T2 长默认 `:147-148` | **120000** | `false` | *（无此键）* | 30s ❌ | 120s ✅ |
| `play_for` | `{durationMs:600000}` | T3 `:145` | **610000** | `true` | *（无此键）* | 610000 | 610000（无变化） |
| `step_game_ticks` | `{ticks:100}`（不传 `timeoutMs`） | T2 长默认 `:147-148` | 120000 | `false` | *（无此键）* | 120000 | 120000（无变化） |
| `load_game` | `{}` | T2 慢档 `:147-148` | 60000 | `false` | *（无此键）* | 60000 | 60000（无变化） |
| `list_colonists` | `{}` | —— `:150` `return null` | `null` | —— | —— | 30s（回落到 `gabpClient.js:67`） | 无变化（未登记的工具一律不动） |

**读这张表的方法（重要）：** 最后四行是**回归护栏** —— 它们证明本计划只影响两个点名的工具，既有的长任务能力（`play_for` 610s、`play_until_letter` 300s）**没有被顺带削掉**。

### 4.6 调参逃生舱（本计划不写 `config.json`，但执行者要知道它存在）

T4 档（`timeoutPolicy.js:106-109`）优先于本计划新增的一切登记。若用户日后想改这两个工具的预算，**不需要改代码**，在 `MCP/config.json` 里加：

```json
"rimBridge": { "longTask": { "toolTimeouts": { "start_debug_game_ready": 90000 } } }
```

即可逐工具覆盖（本计划不创建这个文件，也不改现有 `config.json`）。**注意：T4 覆盖会走 `:141`，此时 `expectedLongWait` 仍会带上**（§4.3 的 `withLongWait()` 在三个 return 上都调了）⇒ 豁免不会因为调参而丢失。

---

## 任务 1：在 `timeoutPolicy.js` 里登记两个工具、加 `capMs` 夹紧与 `expectedLongWait`

**交付物：** `MCP/longTask/timeoutPolicy.js` 的三张表里出现这两个工具；`start_debug_game_ready` 的推导值被夹在 120000；`timeoutOptsFor` 对它返回的 opts 带 `expectedLongWait: true`；`select_pawn` 拿到 120s 但**不带**该标记。既有工具的返回体与预算**逐字节不变**。

**文件：**
- 修改：`MCP/longTask/timeoutPolicy.js:20-25`（`INPUT_DERIVED` 注释 + 新增一条）
- 修改：`MCP/longTask/timeoutPolicy.js:27-28`（`LONG_DEFAULTS_MS` 展开为多行 + 新增两条 + 新增 `EXPECTED_LONG_WAIT`）
- 修改：`MCP/longTask/timeoutPolicy.js:94-95`（`deriveFromArgs` 加 `capMs` 夹紧）
- 修改：`MCP/longTask/timeoutPolicy.js:136-151`（`timeoutOptsFor` 三个 return + 新增 `withLongWait` 辅助函数）
- 测试：`MCP/test/timeoutPolicy.test.js`（末尾追加）

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/timeoutPolicy.test.js` **末尾追加**（该文件现有 230 行；它没有 import `index.js`，所以只测纯策略，不起服务器）：

```javascript

// ========== 2026-10-02 修吐槽板 [24] / [32]：GABP 镜像每次调用的等待预算 ==========
// 背景 / 决策表 / 两条陷阱：plans/2026-10-02-32-gabp-per-call-timeout-derivation.md §1、§4.5
// 下面每一条断言都对应 §4.5 表里的一行。

test('[32] start_debug_game_ready：两档都拿到 120s，且都带 expectedLongWait', () => {
  // 不传 timeoutMs → 走 T2 长默认分支（:147-148）
  assert.deepEqual(timeoutOptsFor('rimworld/start_debug_game_ready', {}, EMPTY_CFG), {
    timeoutMs: 120000,
    inputDerived: false,
    expectedLongWait: true,
  });
  // 板上复现：传 timeoutMs:300000 → 走 T3，但裸算 305000 会被 capMs 夹到 120000（:95）
  assert.deepEqual(timeoutOptsFor('rimworld/start_debug_game_ready', { timeoutMs: 300000 }, EMPTY_CFG), {
    timeoutMs: 120000,
    inputDerived: false,
    expectedLongWait: true,
  });
});

test('[32] clamp：推导值不得超过 120000（绝不允许出现 305000 —— 那会撞 -32001）', () => {
  for (const ms of [300000, 600000, 3600000, Number.MAX_SAFE_INTEGER]) {
    assert.equal(
      timeoutOptsFor('rimworld/start_debug_game_ready', { timeoutMs: ms }, EMPTY_CFG).timeoutMs,
      120000,
      `timeoutMs=${ms} 必须被 capMs 夹到 120000`,
    );
  }
  // cap 之下按「入参 + 5s 余量」正常推导，不是一律 120000
  assert.equal(
    timeoutOptsFor('rimworld/start_debug_game_ready', { timeoutMs: 60000 }, EMPTY_CFG).timeoutMs,
    65000,
  );
  assert.equal(
    timeoutOptsFor('rimworld/start_debug_game_ready', { timeoutMs: 5000 }, EMPTY_CFG).timeoutMs,
    30000,
    '推导值低于默认档时按 :95 的 Math.max 取 30000 下限（与改动前一致）',
  );
});

test('[32] clamp 不能泄漏到别的工具：play_for 的 610s / play_until_letter 的 305s 必须原样保留', () => {
  assert.equal(timeoutOptsFor('rimworld/play_for', { durationMs: 600000 }, EMPTY_CFG).timeoutMs, 610000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 600000 }, EMPTY_CFG), 610000);
  assert.equal(
    timeoutOptsFor('rimworld/play_until_letter', { timeoutMs: 300000 }, EMPTY_CFG).timeoutMs,
    305000,
    'play_until_letter 没有登记 capMs ⇒ 不受本计划的 120000 上限影响（裁决报告 §4.3 明令不许做的那个全局 clamp）',
  );
});

test('[24] select_pawn：拿到 120s，但刻意不带 expectedLongWait（保留假死自愈，见计划 §4.4）', () => {
  assert.deepEqual(timeoutOptsFor('rimworld/select_pawn', {}, EMPTY_CFG), {
    timeoutMs: 120000,
    inputDerived: false,
  });
  assert.equal(
    'expectedLongWait' in timeoutOptsFor('rimworld/select_pawn', {}, EMPTY_CFG),
    false,
    'select_pawn 证据不足（板上无正文）⇒ 不豁免，forceReconnect 自愈必须保留',
  );
});

test('expectedLongWait 只附加在登记了的工具上，既有工具的返回体逐字节不变', () => {
  const absent = (opts) => 'expectedLongWait' in opts === false;
  assert.ok(absent(timeoutOptsFor('rimworld/play_for', { durationMs: 60000 }, EMPTY_CFG)));
  assert.ok(absent(timeoutOptsFor('rimworld/step_game_ticks', {}, EMPTY_CFG)));
  assert.ok(absent(timeoutOptsFor('rimworld/play_until_letter', { timeoutMs: 120000 }, EMPTY_CFG)));
  assert.ok(absent(timeoutOptsFor('rimworld/load_game', {}, EMPTY_CFG)));
  assert.ok(absent(timeoutOptsFor('rimworld/create_hook', {}, EMPTY_CFG)));
});

test('T4 显式覆盖时 expectedLongWait 仍在（调参不该让豁免丢失，见计划 §4.6）', () => {
  const cfg = { rimBridge: { longTask: { toolTimeouts: { start_debug_game_ready: 90000 } } } };
  assert.deepEqual(timeoutOptsFor('rimworld/start_debug_game_ready', { timeoutMs: 300000 }, cfg), {
    timeoutMs: 90000,
    inputDerived: false,
    expectedLongWait: true,
  });
  // select_pawn 被 T4 覆盖时同样不带该键
  assert.equal(
    'expectedLongWait' in timeoutOptsFor('rimworld/select_pawn', {}, {
      rimBridge: { longTask: { toolTimeouts: { select_pawn: 90000 } } },
    }),
    false,
  );
});

test('未登记的工具仍然返回 null（不因本计划被连带改动）', () => {
  assert.equal(timeoutOptsFor('rimworld/list_colonists', {}, EMPTY_CFG), null);
  assert.equal(timeoutOptsFor('rimworld/get_game_info', {}, EMPTY_CFG), null);
});

test('斜杠名 / 点号名 / 裸名三种写法的预算与豁免一致（bareToolName 只认最后一段）', () => {
  for (const n of ['rimworld/start_debug_game_ready', 'rimworld.start_debug_game_ready', 'start_debug_game_ready']) {
    assert.equal(timeoutOptsFor(n, { timeoutMs: 300000 }, EMPTY_CFG).timeoutMs, 120000, n);
    assert.equal(timeoutOptsFor(n, { timeoutMs: 300000 }, EMPTY_CFG).expectedLongWait, true, n);
    assert.equal(timeoutOptsFor(n, {}, EMPTY_CFG).timeoutMs, 120000, n);
  }
  assert.equal(timeoutOptsFor('rimworld/select_pawn', {}, EMPTY_CFG).timeoutMs, 120000);
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/timeoutPolicy.test.js
```

预期：**新增的 8 个里 5 个 FAIL、3 个 PASS（三条护栏），既有的 17 个全部 PASS。** 逐条：

| 新增断言 | 此刻实际值 | 为什么 |
|---|---|---|
| `[32] 两档都拿到 120s` | `timeoutOptsFor('rimworld/start_debug_game_ready', {})` ⇒ **`null`**；带 `{timeoutMs:300000}` ⇒ 也是 **`null`** | 工具没登记，`timeoutOptsFor` 走 `:150` 的 `return null` |
| `[32] clamp` | 同上，`null.timeoutMs` ⇒ 抛 `TypeError`（`assert.equal` 读属性失败） | 既没有推导值，也没有 `capMs` |
| `[32] clamp 不泄漏` | `play_for{600000}` ⇒ **610000 ✓ PASS**；`play_until_letter{300000}` ⇒ **305000 ✓ PASS** | **这一条此刻就该 PASS** —— 它是护栏，证明护栏本身没被写错。若它 FAIL，说明你误改了 `deriveFromArgs` 的既有分支，**停下来先还原** |
| `[24] select_pawn` | **`null`** ⇒ `deepEqual` 失败 | 未登记 |
| `expectedLongWait 只附加在登记工具上` | **PASS**（此刻一个都没附加，全部 `absent`） | **这一条此刻就该 PASS** —— 它是「不能反向波及既有工具」的护栏 |
| `T4 覆盖时仍在` | `null` ⇒ FAIL | `timeoutOptsFor` 走 `:141` 前就已经…… 不，它是 T4 命中，返回 `{timeoutMs:90000, inputDerived:false}` —— **FAIL**，因为缺 `expectedLongWait` |
| `未登记工具仍返回 null` | **PASS** | **这一条此刻就该 PASS** —— 护栏 |
| `三种写法一致` | **`null.timeoutMs`** ⇒ FAIL | 同第一条 |

> **三条此刻就该 PASS 的护栏（`clamp 不泄漏` / `只附加在登记工具上` / `未登记仍返回 null`）是刻意设计的。** 它们证明「本计划没有把改动扩散到别的工具」，因此在步骤 3 之后必须仍然 PASS。

- [ ] **步骤 3：`INPUT_DERIVED` 登记 `start_debug_game_ready`（含 `capMs`）**

把 `MCP/longTask/timeoutPolicy.js:20-25` 这 6 行：

```javascript
// T3 推导表：工具短名 → { 取值字段, 余量字段 或 余量常量 }
const INPUT_DERIVED = new Map([
  ['play_for', { argKey: 'durationMs', overheadKey: 'playForOverheadMs' }],
  ['step_game_ticks', { argKey: 'timeoutMs', overheadMs: 5000 }],
  ['play_until_letter', { argKey: 'timeoutMs', overheadMs: 5000 }],
]);
```

替换为：

```javascript
// T3 推导表：工具短名 → { 取值字段, 余量字段 或 余量常量, 可选 capMs }
//
// capMs 是**本工具专属**的推导上限（2026-10-02 起，只有登记了它的条目才有）。
// 为什么必须有它：调用方传 timeoutMs:300000 时裸算会得到 300000+5000=305000ms，
// 超过宿主 MCP 客户端截止线的实测上界 192.7s（triage/04-startup-call-timeout-ambiguity.md:98）
// ⇒ 失败点会从「30s 超时」**平移**成 -32001，痛点没消除、只换了错误码，还把 [04] 的坑再踩一遍。
// 为什么不能做成全局常量：play_for 有自己的上界检查（index.js:4146-4150，超 maxSingleCallTimeoutMs
// 时返回 MAX_SINGLE_CALL_TIMEOUT_EXCEEDED 并给出两条出路），start_debug_game_ready 没有等价机制，
// 照搬会把长任务能力一起削掉。详见 plans/2026-10-02-32-gabp-per-call-timeout-derivation.md §4.1。
const INPUT_DERIVED = new Map([
  ['play_for', { argKey: 'durationMs', overheadKey: 'playForOverheadMs' }],
  ['step_game_ticks', { argKey: 'timeoutMs', overheadMs: 5000 }],
  ['play_until_letter', { argKey: 'timeoutMs', overheadMs: 5000 }],
  // 2026-10-02 修吐槽板 [32]：start_debug_game_ready 过去 timeoutMs 在**控制层**完全无效
  // （args 里的 300000 被原样转发给了 RBS ⇒ 传输层有效；但本地等待预算走的是另一条路，
  //  因为本表与 LONG_DEFAULTS_MS 都没登记它 ⇒ timeoutOptsFor 返回 null ⇒ 落回
  //  gabpClient.js:67 的 DEFAULT_REQUEST_TIMEOUT_MS = 30000）。
  // capMs=120000 的两条依据（互相独立）：
  //   ① 120000 < 131.5s 的客户端截止线下界 ⇒ 不可能撞上 -32001；
  //   ② 120000 正是 RBS 自己的默认值（反编译 06000F09:M 的 `int timeoutMs = 120000`）
  //      ⇒ 本地等得时长与游戏侧打算等的时长天然对齐，不会「本地放弃、游戏侧还在跑」。
  ['start_debug_game_ready', { argKey: 'timeoutMs', overheadMs: 5000, capMs: 120000 }],
]);
```

- [ ] **步骤 4：`LONG_DEFAULTS_MS` 登记两个工具 + 新增 `EXPECTED_LONG_WAIT`**

把 `MCP/longTask/timeoutPolicy.js:27-28` 这 2 行：

```javascript
// 这些工具自身的默认值本来就长于 30 s，不能当成 T1 处理
const LONG_DEFAULTS_MS = new Map([['step_game_ticks', 120000], ['play_until_letter', 300000]]);
```

替换为（**这一段同时产出了 `EXPECTED_LONG_WAIT`**，插在 `:28` 之后、`:30` 的 `const DEFAULTS = {` 之前）：

```javascript
// 这些工具自身的默认值本来就长于 30 s，不能当成 T1 处理
const LONG_DEFAULTS_MS = new Map([
  ['step_game_ticks', 120000],
  ['play_until_letter', 300000],
  // 2026-10-02 修 [32]：RBS 侧 StartDebugGameReady 的默认 timeoutMs 就是 120000
  // （反编译 06000F09:M），本地等得比游戏侧还短就会出现「传输层有效、控制层无效」。
  // 这一条覆盖的是**调用方不传 timeoutMs** 的情形（默认情形），T3 覆盖不了 —— deriveFromArgs
  // 在 :93 对缺失/非正数的 raw 直接 return null。
  ['start_debug_game_ready', 120000],
  // 2026-10-02 修 [24] select_pawn：与 [32] 同源（同一个 gabpClient.js:67 的 30s 预算）。
  ['select_pawn', 120000],
]);

// 这些工具的「长等待」是**预期内**的，不该被 MCP/index.js:2773-2785 的
// 「连续 3 次 GABP 假死 → 强制重连」（阈值 index.js:2518）当成 RimBridge 无响应。
//
// 为什么不能靠既有的 inputDerived 顺带解决：加了 capMs 之后，推导值恰好与 baselineMs()
// 的长默认值相撞（min(305000,120000) === max(30000,120000) === 120000），
// :145 的 `derived !== baselineMs` 于是算出 **false** ⇒ 豁免在「传参」和「不传参」两条路径上
// **都会**失效。详见 plans/2026-10-02-32-gabp-per-call-timeout-derivation.md §4.2。
//
// 为什么 select_pawn 不在这张表里（这是刻意的不对称，不是漏写）：它的「真的需要 >30s」
// 只有板上索引行一句话为证（该条目**没有正文**，见计划 §1.1），证据不足。豁免的代价是失去
// forceReconnect 自愈；「失去自愈」比「过早重连」坏得多（重连可恢复且有 :2778 的 WARN 日志，
// 游戏侧不受影响）。所以它只登记 LONG_DEFAULTS_MS。见计划 §4.4。
const EXPECTED_LONG_WAIT = new Set(['start_debug_game_ready']);
```

- [ ] **步骤 5：`deriveFromArgs` 加上 `capMs` 夹紧**

把 `MCP/longTask/timeoutPolicy.js:94-95` 这 2 行：

```javascript
  const overhead = spec.overheadMs != null ? spec.overheadMs : t[spec.overheadKey];
  return Math.max(t.defaultMs, raw + overhead);
}
```

替换为：

```javascript
  const overhead = spec.overheadMs != null ? spec.overheadMs : t[spec.overheadKey];
  const derived = Math.max(t.defaultMs, raw + overhead);
  // capMs 只对**登记了它的那个工具**生效（当前仅 start_debug_game_ready）。
  // 计划 §4.1：传 300000 时裸算是 305000ms，会超过客户端截止线上界 192.7s ⇒ 失败点平移成 -32001。
  // 没有 capMs 的条目（play_for / step_game_ticks / play_until_letter）行为逐字节不变。
  return Number.isFinite(spec.capMs) && derived > spec.capMs ? spec.capMs : derived;
}
```

- [ ] **步骤 6：`timeoutOptsFor` 三个 return 都带上 `expectedLongWait`**

先在 `MCP/longTask/timeoutPolicy.js` 的 `/** 派发处用的 opts 工厂 … */` 那段文档注释**之前**插入辅助函数（在 `isInputDerivedTimeout` 的收尾 `}` 之后、`timeoutOptsFor` 的文档注释之前）：

```javascript
// 给 opts 条件附加 expectedLongWait。**只有登记在 EXPECTED_LONG_WAIT 里的工具才多这一个键** ——
// 条件展开是刻意的：这样 test/timeoutPolicy.test.js:179-188 那两条既有的 assert.deepEqual
// 断言逐字节不变，也保证 play_for / step_game_ticks / play_until_letter / load_game 的返回体
// 不会被本计划顺带改形（计划 §4.3）。
function withLongWait(opts, expectedLongWait) {
  return expectedLongWait ? { ...opts, expectedLongWait: true } : opts;
}
```

再把 `MCP/longTask/timeoutPolicy.js:136-151` 的整个 `timeoutOptsFor` 函数体替换为：

```javascript
export function timeoutOptsFor(name, args, config) {
  const t = readThresholds(config);
  const bare = bareToolName(name);
  const expectedLongWait = EXPECTED_LONG_WAIT.has(bare);
  const explicit = explicitOverrideMs(bare, t);
  if (explicit != null) {
    return withLongWait({ timeoutMs: explicit, inputDerived: false }, expectedLongWait);
  }
  const derived = deriveFromArgs(bare, args, t);
  if (derived != null) {
    return withLongWait({ timeoutMs: derived, inputDerived: derived !== baselineMs(bare, t) }, expectedLongWait);
  }
  if (SLOW_BARE_NAMES.has(bare) || LONG_DEFAULTS_MS.has(bare)) {
    return withLongWait({ timeoutMs: baselineMs(bare, t), inputDerived: false }, expectedLongWait);
  }
  return null;
}
```

> **注意 `null` 那一路（`:150`）不加 `expectedLongWait`** —— 未登记的工具返回 `null` 是「本层不干预」的契约（`index.js:4335` 靠它回落 `{slow:true}` 判定），任何情况下都不该改。

- [ ] **步骤 7：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/timeoutPolicy.test.js
```

预期：**全部 PASS**（既有 17 个 + 新增 8 个 = 25 个）。若失败，按下表定位：

| 失败的是 | 说明 |
|---|---|
| 新增的 `[32] 两档都拿到 120s` | 检查 `INPUT_DERIVED` 的键名有没有写成 `start_debug_game_ready`（`bareToolName` 取的是最后一段，`rimworld/start_debug_game_ready` → `start_debug_game_ready`） |
| 新增的 `clamp` | 检查步骤 5 有没有真的加 `capMs`（漏了会得到 305000） |
| 新增的 `T4 覆盖时仍在` | 检查 `withLongWait()` 有没有套在 `:141` 那个 return 上 |
| **既有的 `timeoutOptsFor：入参推导时返回 {timeoutMs, inputDerived:true}`（`:179`）** | 说明 `withLongWait` 变成了无条件展开 ⇒ 既有工具也多出了 `expectedLongWait` 键。**改实现，不要改断言** |
| 既有的 `T3 入参推导` / `T2 慢档` / `T1 默认档` | 说明动了 `deriveFromArgs` 的 `Math.max` 下限或 `baselineMs`。**改实现** |

- [ ] **步骤 8：改动清单（本项目不是 git 仓库，无提交动作）**

本次触碰 2 个文件：

| 文件 | 动作 | 锚点（行号已复核） |
|---|---|---|
| `MCP/longTask/timeoutPolicy.js` | 修改 | `:20-25` 注释 + `INPUT_DERIVED` 新增一条（+1 条目 / +16 行注释） |
| `MCP/longTask/timeoutPolicy.js` | 修改 | `:27-28` `LONG_DEFAULTS_MS` 展开为多行 + 新增 2 条 + 新增 `EXPECTED_LONG_WAIT`（+1 常量） |
| `MCP/longTask/timeoutPolicy.js` | 修改 | `:94-95` `deriveFromArgs` 加 `capMs` 夹紧（+4 行 / -1 行） |
| `MCP/longTask/timeoutPolicy.js` | 修改 | `:136-151` `timeoutOptsFor` 三个 return 改调 `withLongWait`；其前新增 `withLongWait` 辅助函数（+8 行） |
| `MCP/test/timeoutPolicy.test.js` | 修改 | 末尾追加 8 个 `test`（文件现有 17 个） |

**没有改任何 C#、没有改 `gabpClient.js`、没有改 `MCP/index.js`、没有改 `config.json` / `toolConfig.json` / `package.json`、没有重编译模组、没有碰 `docs/` 下除本计划外的任何文件。**

---

## 任务 2：`MCP/index.js:2773` 接住 `expectedLongWait`，让预期内的长超时不进假死计数

**交付物：** 带 `expectedLongWait: true` 的调用若超时，`gabpConsecutiveTimeout` **一次都不加**、`forceReconnect()` **一次都不调**；而不带该标记的同样超时，连打 3 次仍会触发 `forceReconnect()`（证明自愈机制没被改坏）。

**文件：**
- 修改：`MCP/index.js:2773`（判定条件加一个 `||` 支路）
- 创建：`MCP/test/mirrorLongWaitBudget.test.js`（2 个行为测试，任务 3 会往同一文件追加第 3 个）

**⚠️ 本任务的前置条件：任务 1 已完成。** 没有 `expectedLongWait` 这个键，本任务的第一个测试无从谈起。

- [ ] **步骤 1：创建测试文件（先只写行为测试）**

创建 `MCP/test/mirrorLongWaitBudget.test.js`：

```javascript
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8'); // 任务 3 用

/**
 * 2026-10-02 修吐槽板 [24] / [32] 的**行为层**回归。
 * 背景、决策表与两条陷阱：plans/2026-10-02-32-gabp-per-call-timeout-derivation.md
 *
 * 这里**不起游戏、不连 GABP、不改任何游戏状态**：用 setGabpBridge（index.js:5453）
 * 注入假桥接，直接驱动**真实导出**的 callGABPTool（index.js:5422），
 * 即 index.js:2756-2789 那段真代码。
 *
 * 为什么驱动 callGABPTool 而不是 handleToolCall：后者有 :4281 的 gabpToolNames() 闸门，
 * 而能注入工具清单的 setGabpTools 钩子属于 plans/2026-10-01-22-…（尚未落地），
 * 本计划**刻意不依赖**它，以免两份计划互相阻塞。
 */

// 与 gabpClient.js:568 逐字同形的错误（只有那段 setTimeout 能产出它）
const TIMEOUT_ERR = { code: -32000, message: "GABP 请求 'tools/call' 超时（120000ms）" };

function makeBridge(over = {}) {
  return {
    isConnected: () => true,          // 让 callGABPTool:2748 的 ensureGabpConnected 直接放行
    state: 'connected',
    ensureConnected: async () => true,
    forceReconnect: () => { over.onForceReconnect && over.onForceReconnect(); },
    callTool: async () => over.reply || ({ ok: true, result: {} }),
    ...over.extra,
  };
}

async function withFakeBridge(bridge, fn) {
  mod._testHooks.setGabpBridge(bridge);
  try { return await fn(); } finally { mod._testHooks.setGabpBridge(null); }
}
afterEach(() => mod._testHooks.setGabpBridge(null));

test('expectedLongWait:true 的长超时不计入 GABP 假死计数，也不会触发强制重连', async () => {
  let forceReconnectCalls = 0;
  const bridge = makeBridge({
    onForceReconnect: () => { forceReconnectCalls++; },
    reply: { ok: false, error: TIMEOUT_ERR },
  });
  await withFakeBridge(bridge, async () => {
    // 先用一次成功调用把计数清零（成功路径 index.js:2759 会重置），保证下面的 0 是可信的
    bridge.callTool = async () => ({ ok: true, result: {} });
    await mod.callGABPTool('rimworld/reset_probe', {}, undefined, {});
    assert.equal(mod._testHooks.getGabpConsecutiveTimeout(), 0, '前置条件：计数从 0 开始');

    // 连打 5 次（> 阈值 index.js:2518 的 3）：带 expectedLongWait ⇒ 一次都不许计入
    for (let i = 0; i < 5; i++) {
      const r = await mod.callGABPTool(
        'rimworld/start_debug_game_ready',
        {},
        undefined,
        { timeoutMs: 120000, inputDerived: false, expectedLongWait: true },
      );
      assert.equal(r.isError, true, `第 ${i + 1} 次应当以错误返回`);
      assert.match(r.content[0].text, /超时（120000ms）/,
        '错误体必须仍然自曝真实预算（这正是本条不上调 🔴 的理由：信息没被抹掉）');
    }
    assert.equal(mod._testHooks.getGabpConsecutiveTimeout(), 0,
      '预期内的长等待被当成了 RimBridge 假死 —— 连续 3 次就会打断正在跑的冷启动');
    assert.equal(forceReconnectCalls, 0, '连打 5 次仍未达阈值，绝不该 forceReconnect');
  });
});

test('对照组：不带 expectedLongWait 的同样超时，3 次后必须仍然 forceReconnect（自愈没被改坏）', async () => {
  let forceReconnectCalls = 0;
  const bridge = makeBridge({
    onForceReconnect: () => { forceReconnectCalls++; },
    reply: { ok: false, error: TIMEOUT_ERR },
  });
  await withFakeBridge(bridge, async () => {
    bridge.callTool = async () => ({ ok: true, result: {} });
    await mod.callGABPTool('rimworld/reset_probe', {}, undefined, {});
    assert.equal(mod._testHooks.getGabpConsecutiveTimeout(), 0, '前置条件：计数从 0 开始');

    // select_pawn 刻意不登记 expectedLongWait（计划 §4.4）⇒ 必须仍由 :2777 兜底
    for (let i = 0; i < 3; i++) {
      await mod.callGABPTool('rimworld/select_pawn', {}, undefined,
        { timeoutMs: 120000, inputDerived: false });
    }
    assert.equal(forceReconnectCalls, 1, '第 3 次连续超时应触发一次 forceReconnect');
    assert.equal(mod._testHooks.getGabpConsecutiveTimeout(), 0, '触发后 :2779 会把计数清零');
  });
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/mirrorLongWaitBudget.test.js
```

预期：**2 个里 1 个 FAIL、1 个 PASS**。逐条：

| # | 此刻 | 为什么 |
|---|---|---|
| 1 `expectedLongWait:true` 的长超时 | **FAIL** | `index.js:2773` 还只认 `inputDerived === true`，所以 5 次超时把计数推到 5、`getGabpConsecutiveTimeout()` 返回 5（且第 3 次就调了 `forceReconnect`）⇒ 两个断言都挂 |
| 2 对照组 | **PASS** | 这是我们要守住的下界：不带标记的自愈**必须**照旧工作。**若它 FAIL，说明假桥接/计数钩子有问题，先排查环境，绝不要改断言** |

- [ ] **步骤 3：`index.js:2773` 加 `expectedLongWait` 支路**

把 `MCP/index.js:2772-2774` 这 3 行：

```javascript
  const isTimeout = /超时/.test(errMsg) || /-32001/.test(errMsg);
  if (isTimeout && callOpts.inputDerived === true) {
    log('DEBUG', `GABP 长档超时（预期内，不计入假死检测）：${rbsName}`);
```

替换为：

```javascript
  const isTimeout = /超时/.test(errMsg) || /-32001/.test(errMsg);
  // expectedLongWait（MCP/longTask/timeoutPolicy.js 的 EXPECTED_LONG_WAIT，见 :148 附近的登记表）：
  // 工具的**长等待本身就是预期内的**，不应被当成 RimBridge 假死。
  // 为什么必须与 inputDerived 并列而不是靠它：加了 capMs 之后推导值会与 baselineMs() 相等，
  // :145 的 `derived !== baselineMs` 算出 false ⇒ 单靠 inputDerived 在「传参 / 不传参」两条路径上
  // 都会漏豁免。详见 plans/2026-10-02-32-gabp-per-call-timeout-derivation.md §4.2 / §4.3。
  if (isTimeout && (callOpts.inputDerived === true || callOpts.expectedLongWait === true)) {
    log('DEBUG', `GABP 长档超时（预期内，不计入假死检测）：${rbsName}`);
```

> **只改这一处判定，不要动 `:2775-2785` 的计数逻辑。** 「3 次阈值」「触发后清零」「`forceReconnect` 包在 try/catch 里」都是既有自愈机制的一部分，本计划一个字都不改。

- [ ] **步骤 4：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/mirrorLongWaitBudget.test.js
```

预期：**2 个全部 PASS**。若第 2 个（对照组）反而 FAIL，说明 `|| callOpts.expectedLongWait === true` 写成了恒真（比如写成了 `!== undefined`）—— **改实现，不要改断言**。

- [ ] **步骤 5：改动清单**

| 文件 | 动作 | 锚点（行号已复核） |
|---|---|---|
| `MCP/index.js` | 修改（+5 行 / -1 行） | `:2772-2774`，假死判定加 `expectedLongWait` 支路 |
| `MCP/test/mirrorLongWaitBudget.test.js` | **新建** | 2 个行为测试 |

**没有改 C#、没有改 `gabpClient.js` / `timeoutPolicy.js`（那是任务 1）、没有改配置、没有重编译、没有碰 `docs/` 下除本计划外的任何文件。**

---

## 任务 3：`MCP/index.js:4292` 的幂等探测补显式 5 秒短预算

**交付物：** `start_debug_game_ready` 的幂等探测 `callTool('rimworld/get_game_info', …)` 不再白吃 30 秒默认预算，而是显式 5 秒；探测失败仍然按 `:4290` 的既有约定**保守转发**（本任务不碰 `:4290-4298` 的判定逻辑）。

**文件：**
- 修改：`MCP/index.js:4292`
- 测试：`MCP/test/mirrorLongWaitBudget.test.js`（任务 2 建好的文件，末尾追加 1 个 `test`）

> **⚠️ 本任务为什么只做源码切片断言、不做行为断言（不是偷懒，是有硬理由）：**
> 探测在 `handleToolCall` 内部，而 `handleToolCall` 的镜像分支在 `index.js:4281` 有一道
> `if (!gabpToolNames().has(name)) return …` 闸门；`gabpToolNames()`（`:2797-2803`）只读
> 模块级 `gabpTools`，测试进程里恒为 `[]`。唯一能注入它的钩子是 `setGabpTools`，
> **那个钩子属于 `plans/2026-10-01-22-play-for-guidance-and-unknown-arg-guard.md`，尚未落地。**
> ⇒ 行为断言会让本计划**硬依赖**另一份计划；切片断言则完全自洽。
> （同款手法在本仓库已有先例：`test/toolchainFixes.test.js:255-260` 就是一段源码切片断言。）
> 计划 22 落地后，这一条可以升级成行为断言——**但不必**，切片断言已经钉住了关键事实。

- [ ] **步骤 1：追加失败的测试**

在 `MCP/test/mirrorLongWaitBudget.test.js` **末尾追加**：

```javascript
// ---- 幂等探测的短预算（源码切片；理由见本任务头部说明） ----

test('start_debug_game_ready 的幂等探测带显式 5 秒短预算（不再白吃 30s）', () => {
  const i = source.indexOf("await gabpBridge.callTool('rimworld/get_game_info'");
  assert.ok(i > 0, '找不到 start_debug_game_ready 的幂等探测调用');
  const stmt = source.slice(i, source.indexOf('\n', i));
  assert.match(stmt, /\{\s*timeoutMs:\s*5000\s*\}\s*\)\s*;?\s*$/,
    '探测必须显式给短预算；它只用来判「是否已加载」，失败就保守转发，不需要 30s');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/mirrorLongWaitBudget.test.js
```

预期：**3 个里 1 个 FAIL** —— 新增这条 FAIL（`stmt` 是 `...('rimworld/get_game_info', {});`，匹配不上 `{ timeoutMs: 5000 }`），任务 2 那 2 条仍 PASS。

> 若新增这条报的是 `找不到 start_debug_game_ready 的幂等探测调用`（`i <= 0`）而不是正则不匹配，**说明 `index.js` 的源码被别处改动过**（或该文件不在你以为的位置）。先核实 `[System.IO.File]::ReadAllLines('MCP\index.js').Count` 是否仍是 **5460**，再继续。

- [ ] **步骤 3：`index.js:4292` 补 opts**

把 `MCP/index.js:4292` 这一行：

```javascript
      const probe = await gabpBridge.callTool('rimworld/get_game_info', {});
```

替换为：

```javascript
      // 探测只用来判「是否已加载」，失败就保守转发（见上面 :4290 的约定）⇒ 不需要长预算。
      // 过去这里不传 opts，白吃 gabpClient.js:67 的 DEFAULT_REQUEST_TIMEOUT_MS=30000：
      // GABP 半卡时每次冷启动都要先白等 30 秒，才轮得到真正需要长预算的那次转发。
      const probe = await gabpBridge.callTool('rimworld/get_game_info', {}, { timeoutMs: 5000 });
```

- [ ] **步骤 4：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/mirrorLongWaitBudget.test.js
```

预期：**3 个全部 PASS**。

- [ ] **步骤 5：跑全量测试**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：**全部 PASS**（`node --test "test/*.test.js"`，`MCP/package.json:9`）。若有任何一条不是 PASS，**不要改断言**，按下表定位：

| 断言 | 位置 | 本任务为什么不该影响它 |
|---|---|---|
| `toolListLine` → `buildMenuNode` 源码切片 | `test/toolchainFixes.test.js:120-124` | 切片是 `index.js:3281` → `3290`（已复核）。本计划只动 `:2773` / `:4292` 两个点，切片逐字节不变 |
| `case 'start_game'` / `case 'stop_game'` 源码切片 | `test/toolchainFixes.test.js:263-268` | 切片是 `index.js:4356` → `4371` → `4384`（已复核）。本计划不碰 |
| `dpa_patch_methods` 前置分支切片 | `test/toolchainFixes.test.js:255-260` | 切片从 `index.js:4337` 起 700 个字符（≈ 到 `:4350`）。本计划**不改 `:4333-4335`**，只改 `:2773` / `:4292`，都在切片之外 |
| `agg_call_tool` 调 `rimworld.press_accept` | `test/toolchainFixes.test.js:60-70` | 走的是 `:4274` 的未连接 fail-fast 与 `agg_call_tool` 的 `unknownToolText`，本计划三处都不碰 |
| 既有 `timeoutOptsFor` 两条 `deepEqual` | `test/timeoutPolicy.test.js:179-188` | `withLongWait()` 是**条件展开**，未登记的工具返回体逐字节不变 |

另外确认 `MCP/index.js` 仍能正常加载（在 `MCP` 目录下执行）：

```
node -e "process.env.UESD_MCP_NO_START='1'; import('./index.js').then(()=>console.log('OK'))"
```

预期输出 `OK`。这一步不启动服务器、不连游戏、不改游戏状态，只是验证没有语法错误。

- [ ] **步骤 6：改动清单（本项目不是 git 仓库，无提交动作）**

**三个任务合起来共触碰 4 个文件：**

| 文件 | 动作 | 锚点（行号已复核） |
|---|---|---|
| `MCP/longTask/timeoutPolicy.js` | 修改 | `:20-25`（`INPUT_DERIVED` + `capMs`）、`:27-28`（`LONG_DEFAULTS_MS` + `EXPECTED_LONG_WAIT`）、`:94-95`（夹紧）、`:136-151`（`timeoutOptsFor` + `withLongWait`） |
| `MCP/index.js` | 修改 | `:2773`（豁免判定）、`:4292`（探测短预算） |
| `MCP/test/timeoutPolicy.test.js` | 修改 | 末尾追加 8 个 `test`（既有 17 个 → 25 个） |
| `MCP/test/mirrorLongWaitBudget.test.js` | **新建** | 3 个 `test` |

**没有改任何 `.cs`、没有改 `MCP/gabpClient.js`、没有改 `MCP/config.json` / `MCP/toolConfig.json` / `MCP/package.json`、没有改 `docs/mcp-吐槽板.md`、没有重编译模组、没有启动游戏、没有碰 `docs/` 下除本计划外的任何文件。**

---

## 5. 落地后会发生什么 / 还剩什么

### 5.1 `[32]` 冷启动造殖民地的完整调用链（改后）

| # | 位置 | 改后 |
|---|---|---|
| 1 | `index.js:4292` | 幂等探测：**5 秒**（原来 30 秒） |
| 2 | `index.js:4294` | 冷启动不短路 |
| 3 | `index.js:4299` | 继续转发 RimBridge |
| 4 | `index.js:4333` | `timeoutOptsFor('rimworld/start_debug_game_ready', {timeoutMs:300000, …})` ⇒ **`{timeoutMs:120000, inputDerived:false, expectedLongWait:true}`**（原来 `null`） |
| 5 | `index.js:4335` | `_opts` 非空 |
| 6 | `index.js:2756-2757` | `callOpts = {timeoutMs:120000, inputDerived:false, expectedLongWait:true}` |
| 7 | `gabpClient.js:674` | `args`（含 `timeoutMs:300000`）**照旧原样转发给 RBS**（传输层不变） |
| 8 | `gabpClient.js:563` | `opts.timeoutMs = 120000` **命中第一档**，不再落回 `_requestTimeoutMs` |
| 9 | 若真的超过 120s | `gabpClient.js:568` 报 `超时（120000ms）`（**仍是本仓库的错误串、仍自曝真实预算**），但 `index.js:2773` **不计入**假死计数 |

⇒ **失败点从 30 秒推迟到 120 秒，且 120 秒仍然低于客户端截止线下界 131.5s ⇒ 不会平移成 `-32001`。** 这就是「两条陷阱都躲开」的结果。

### 5.2 本计划**没有**解决的（留给实机验证或后续条目）

- **120000 够不够。** 依据是「RBS 自己的默认值」+「低于客户端截止线下界」，**不是**「实测冷启动要多久」。若实机发现某些机器上冷启动超过 120 秒，正确做法是用 §4.6 的 T4 配置**调高到 120000 与 131500 之间**，**不是**去掉 `capMs`（去掉就会撞 `-32001`）。
- **`[24]` `select_pawn` 是否真的需要 >30 秒**（§2.3 的假设）。实机若确认它只是 GABP 卡死，本条对它做的改动只会把失败从 30 秒推迟到 120 秒——那属于**如实反映既有 30 秒预算不足**，不算更糟；但**自愈仍在**（§4.4 的刻意取舍）。若实机确认它确实很慢，再由后续改动把它也登记进 `EXPECTED_LONG_WAIT`。
- **板上 `[32]` 与 `[04]` 的归因措辞需要主代理批注**（裁决报告 `triage/32-…:226` 已给出三条建议）：①「与 [04] 同源」改为「与 [24] 同源、与 [04] 不同源」；②索引行的「GABP 内建 30s 超时」改为「本仓库 `MCP/gabpClient.js:67` 的 30s 请求预算」；③保留 `已绕过` 状态但补一句「该绕过有效**只因为游戏侧会继续跑完**，不代表 RPC 能等到结果」。**本计划不改板子**（AGENT-BRIEF §1）。

### 5.3 自检（对照 `writing-plans` SKILL 的三条）

1. **规格覆盖度：** 任务书列出的四张白名单变更 —— ①`SLOW_BARE_NAMES`（`:18`）：**不变更**，并在本计划 §4.3 给出了「为什么不变更」的理由（改成 `inputDerived:true` 会顺带改掉 `step_game_ticks` / `play_until_letter` 的既有自愈行为）；②`INPUT_DERIVED`（`:21-25`）→ 任务 1 步骤 3；③`LONG_DEFAULTS_MS`（`:28`）→ 任务 1 步骤 4；④`timeoutPolicy.js:95` 的 clamp → 任务 1 步骤 5。另有 `EXPECTED_LONG_WAIT`（新增）→ 任务 1 步骤 4+6、`index.js:2773` → 任务 2、`index.js:4292` → 任务 3。**无遗漏。**
2. **占位符扫描：** 全文无「待定 / TODO / 后续实现 / 类似任务 N / 添加适当的错误处理」。所有代码步骤均带完整代码块，所有测试均带完整测试代码与**逐条预期**（含「这一条此刻就该 PASS」的护栏标注）。
3. **类型/命名一致性：** `capMs`（`INPUT_DERIVED` 条目字段，仅 `start_debug_game_ready` 有）、`expectedLongWait`（`timeoutOptsFor` 返回值字段）、`EXPECTED_LONG_WAIT`（`timeoutPolicy.js` 模块级 `Set`）、`withLongWait(opts, expectedLongWait)`（`timeoutPolicy.js` 私有辅助函数）、`deriveFromArgs` / `baselineMs` / `explicitOverrideMs` / `bareToolName`（既有，未改名）—— 全文只用这五个新名字，三处任务里的引用完全一致。

### 5.4 执行交接

计划已完成并保存到 `docs/issue-triage/plans/2026-10-02-32-gabp-per-call-timeout-derivation.md`。两种执行方式：

**1. 子代理驱动（推荐）** - 每个任务调度一个新的子代理，任务间进行审查，快速迭代
**必需子技能：** 使用 superpowers:subagent-driven-development

**2. 内联执行** - 在当前会话中使用 executing-plans 执行任务，批量执行并设有检查点
**必需子技能：** 使用 superpowers:executing-plans

> 三个任务有**严格顺序**：任务 2 依赖任务 1 产出的 `expectedLongWait` 键；任务 3 与 1/2 无耦合，可并行，但同文件 `MCP/index.js` 的两处改动建议一人一次改完。
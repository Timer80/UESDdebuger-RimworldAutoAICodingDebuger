# play_for 失败指路 + GABP 镜像未知参数守卫 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 把 `MCP/index.js:4254-4255` 那条 `PLAY_FOR_MAIN_THREAD` 错误建议改成**点名真实参数名、且指向已验证可达**的替代方案；并在 GABP 镜像派发路径上**真正执行** RBS schema 里那句 `additionalProperties: false`，让写错的参数名（如 `tickCount` / `durationSeconds`）当场被拒并点名回显，而不是静默回落成默认值后照样返回 `success:true`。

**架构：** 两个独立改动，共用一次插入点。①在 `MCP/index.js` 模块作用域新增一张 `MIRROR_ALTERNATIVE_HINTS` 表（「这个工具坏了该改用什么」的**唯一事实源**）+ `formatMirrorAlternative()`，`4254-4255` 改为调它；同一次插入里再加 `GABP_MIRROR_LOCAL_ARGS` 与 `mirrorUnknownArgs()` / `unknownArgsResponse()` 三个声明，并在 `4244` 加一行守卫调用（**必须排在 play_for 分支 4249 之前**，因为 play_for 是唯一被提前接管的镜像工具）。守卫只在「能从 `gabpTools` 取到该工具的非空 `inputSchema.properties`」时才生效，取不到一律放行 —— 因此离线、未连接、`properties` 为空这三种情况下守卫是彻底的 no-op，对既有测试零影响。

**技术栈：** Node.js **v24.13.0**（实测版本）、ESM（`MCP/package.json` 的 `"type":"module"`）、Node 内置测试运行器 `node --test`（`MCP/package.json:9` 的 `npm test` = `node --test "test/*.test.js"`）。不引入任何依赖、不改任何 `.cs`、不重编译模组。

---

## 0. 行号复核（本计划所有行号均已逐条回读，**以本节为准**）

> **复核方法（重要）：** 下表所有行号都用
> `[System.IO.File]::ReadAllLines('<绝对路径>')[<n>-1]` 复核过，**不是**用带行号的文件阅读工具的输出
> （那种输出会随编辑漂移 —— 本项目 `docs/issue-triage/AGENT-BRIEF.md` §3.5 记着它少报过 27 行、且同文件内偏移不一致）。
> 执行者若要再核实，用同样方式，不要相信任何「读到的行号」。
>
> 复核时确认：`MCP/index.js` **TOTAL = 5460 行**；`MCP/gabpClient.js` **TOTAL = 727 行**；
> `MCP/package.json` 的 `scripts.test` 在第 **9** 行。

| 对象 | 用途 | 行号（已复核） |
|---|---|---|
| `MCP/index.js:4245-4258` | `play_for` 派发分支（守卫必须排在它前面） | 4245 注释、4249 `if (name === 'rimworld.play_for')`、4253 正则判定、**4254-4255 待改文案**、4258 分支收尾 |
| `MCP/index.js:4241-4243` | `task_status`/`task_cancel`/`task_configure` 三个早返回（守卫插在它们之后） | 4241-4243 |
| `MCP/index.js:4244` | **空行**，任务 2 的守卫调用插在这里 | 4244 |
| `MCP/index.js:4208-4211` | `handleTaskConfigure` 收尾 `}`(4208) / 空行(4209) / `// Tool handler function`(4210) / `async function handleToolCall`(4211) —— 任务 1、2 的模块级声明插在 4209 与 4210 之间 | 4208-4211 |
| `MCP/index.js:2793-2795` | `isGabpMirrorName()`（守卫判定镜像名） | 2793-2795 |
| `MCP/index.js:2797-2803` | `gabpToolNames()`（对照：守卫另起一份按 `inputSchema` 取值） | 2797-2803 |
| `MCP/index.js:2808` | `agg_call_tool` 的 schema：`args: { type:'object', additionalProperties: true }` —— **契约缺口所在** | 2808 |
| `MCP/index.js:2487-2488` | 既有注释：「这里原先声明 additionalProperties:false，但实测未声明的多余参数会被静默忽略（服务端不校验）」 | 2487-2488 |
| `MCP/index.js:2514` | `let gabpTools = []`（守卫的唯一 schema 来源；测试进程里恒为空） | 2514 |
| `MCP/index.js:2639-2645` | `tools-changed` 事件填充 `gabpTools`（含去重/移除集过滤） | 2639-2645 |
| `MCP/index.js:2697-2702` | `gabpErrorResponse()` | 2697-2702 |
| `MCP/index.js:2705-2707` | `contentErrorResponse(errorCode, message)` —— 任务 1 沿用它；**注意它只拼 `{success,errorCode,message}` 三个字段**，任务 2 需要额外字段所以自己拼返回体 | 2705-2707 |
| `MCP/index.js:2747` | `async function callGABPTool(...)` | 2747 |
| `MCP/index.js:2930` / `2983` | `const TOOL_USAGE_NOTES = {` / 它的收尾 `};` —— 任务 1 步骤 7 插在 2982 之后 | 2930 / 2983 |
| `MCP/index.js:3028-3029` | `TOOL_SUBGROUP` 里 `'rimworld.play_for'` 与 `'rimworld.step_game_ticks'` 都在 `game_control/game/state` | 3028-3029 |
| `MCP/index.js:3369-3376` | `menuUniverseNames()` —— 离线也能证明某个镜像名**登记在册**（任务 1 的可达性护栏用它） | 3369-3376 |
| `MCP/index.js:3875-3883` | `toolSchema()`：查找顺序 `TOOLS → monoTools → gabpTools → AGG_META_TOOLS`，**镜像工具在这里能查到**（任务 2 的错误体会因此被 `attachUsageOnFailure` 自动附上正确的 `usage`） | 3875-3883 |
| `MCP/index.js:3934-3956` | `formatCallPattern()` / `buildUsageHint()` | 3934-3956 |
| `MCP/index.js:4006-4075` | `attachUsageOnFailure()`；**触发条件在 4009-4018**（`isError===true` **或** JSON 体 `success===false` 两者之一即可）；`targetName` 取法在 4021-4024 | 4006-4075 |
| `MCP/index.js:4082-4085` | `PLAY_FOR_CONTROL_KEYS`（守卫的本地键白名单直接复用它，**因此守卫声明必须在 4085 之后**，本计划放在 4210 ✅） | 4082-4085 |
| `MCP/index.js:4087-4096` | `splitPlayForArgs()` | 4087-4096 |
| `MCP/index.js:4105` | `async function callPlayFor(args)`（任务 1 文案描述的对象） | 4105 |
| `MCP/index.js:4262` | `if (isGabpMirrorName(name))` 镜像派发块开头 | 4262 |
| `MCP/index.js:4266-4272` | `delete_area`/`clear_area` 走自研实现，`areaId ?? id ?? areaIdOrAlias`（**`id`/`areaIdOrAlias` 是 MCP 本地键**，必须进白名单） | 4266-4272 |
| `MCP/index.js:4281-4286` | 「已连接但镜像清单里没有该工具」分支（**任务 2 的守卫排在它前面也不影响它**，守卫只处理参数不处理工具名） | 4281-4286 |
| `MCP/index.js:4337-4349` | `dpa_patch_methods` 分支；`delete fwdArgs.force` 在 **4345**（`force` 是 MCP 本地键） | 4337-4349 |
| `MCP/index.js:4350-4352` | 镜像尾部 `callGABPTool(_rbs, args, ...)` / `noteDpaStateAfterCall` / `return _r` | 4350-4352 |
| `MCP/index.js:4371-4382` | `case 'stop_game': {` … `}` —— **源码切片断言 `test/toolchainFixes.test.js:263-268` 依赖它，本计划任何改动都不能碰碎这一段** | 4371-4382 |
| `MCP/index.js:5107` | `agg_call_tool` 把 `innerArgs` 交给 `handleToolCall(resolved, innerArgs)` —— **守卫放在 `handleToolCall` 内即同时覆盖直接调用与聚合调用** | 5107 |
| `MCP/index.js:5123-5132` | `tools/call` 日志行，`outcome` 由返回体 `parsed.success` 推导（守卫的拒绝会在日志上显示为 `error(UNKNOWN_MIRROR_ARG)`） | 5123-5132 |
| `MCP/index.js:5406` | `if (process.env.UESD_MCP_NO_START === '1')` —— 任务 2 的逃生阀沿用同一个 env 开关惯例 | 5406 |
| `MCP/index.js:5416-5443` | `export { ... }` 清单 | 5416-5443 |
| `MCP/index.js:5450-5459` | `export const _testHooks`；`setGabpBridge` 在 **5453** —— **任务 2 要在这里加 `setGabpTools`** | 5450-5459 |
| `MCP/gabpClient.js:71-77` | `toMCPTool()`；`inputSchema: raw.inputSchema \|\| { type:'object', properties:{} }` 在 **75** —— **这个 fallback 就是守卫的"properties 为空则放行"逃生门存在的理由** | 71-77 |
| `MCP/package.json:9` | `"test": "node --test \"test/*.test.js\""` | 9 |
| `MCP/test/toolchainFixes.test.js:120-124` | 源码切片断言 `toolListLine` → `buildMenuNode`（本计划不碰这两个函数，切片逐字节不变） | 120-124 |
| `MCP/test/toolchainFixes.test.js:263-268` | 源码切片断言 `case 'start_game'` / `case 'stop_game'`（同上，本计划不碰） | 263-268 |
| `MCP/test/toolchainFixes.test.js:60-70` | 既有测试：`agg_call_tool` 调 `rimworld.press_accept` 得到 `GABP_MIRROR_UNAVAILABLE`。**任务 2 的守卫在这个测试里是 no-op**（`gabpTools` 为空 ⇒ 拿不到 schema ⇒ 放行），因此它不受影响 | 60-70 |
| `MCP/toolConfig.json` | `"rimworld.play_for":false`、`"rimworld.step_game_ticks":false` —— 两者都必须经 `agg_call_tool` 调用 | 单行 JSON |

## 1. 背景：板上这条被描述成「两层都坏」，实际上只有一层坏

### 1.1 板上已被证伪的四点（**不要按板子修**）

裁决报告 `triage/22-play-for-main-thread-and-step-game-ticks-params.md` 已逐条核实，本计划完全采纳，并在复核后再次确认：

| 板上的说法 | 复核结论 | 本计划的处置 |
|---|---|---|
| 「两层都坏、没有可用路径」 | **错**。`step_game_ticks` 一直好用：`MCP/logs/mcp-20260928-173737.log` 里 `ticks=200/300/120/90/400/25` 六次调用全部 `success`（400 tick 耗时 7337 ms） | 修法从「承认没有路径」改成「**点名参数名的可达替代**」——任务 1 |
| 「`step_game_ticks` 的 schema 是 `params:{}` 空」 | **双重证伪**。反编译 `Lib.GAB.Server.GabpServer.BuildInputSchema` 产出标准 JSON Schema；实机 `mcp_help{tool:"rimworld.step_game_ticks"}` 返回完整 5 参数（`ticks`/`timeoutMs`/`pollIntervalMs`/`pauseFirst`/`playSound`）与默认值 | 🚫 **不动 schema**。`params` 这个字段名本身就不存在（本仓库 `mcp_help` 返回的是 `inputSchema`），去"把 `params` 填上"是在修一个不存在的字段 |
| 「参数名不对只走了 1 tick」 | **属实但责任在调用方**。RBS 签名是 `StepGameTicks(int ticks = 1, ...)`；`tickCount` 不被识别 ⇒ 走 C# 默认参数 ⇒ `requestedTicks:1`。**这是 C# 可选参数的正常语义，RBS 没坏** | 不改 RBS；改**我们自己发的指路文案**——任务 1 |
| 「绕法成本 ≈ 6 分钟/轮，4 轮里 1 轮失败」 | **无法核实且被夸大**。它依赖的 `DebugActionStageTestMap` / `AdvanceToTakeoff` 在本仓库的文本文件与已扫描的前 40 个 DLL 里都查无此物 | 🚫 **本计划任何地方都不引用这个影响叙述** |

**搜索范围声明（AGENT-BRIEF §3.5 的铁律）：** 本计划不依赖任何「零命中」结论。唯一用到的检索是**肯定命中**的逐字回读（`[System.IO.File]::ReadAllLines` + 锚点文本定位），不存在假阴性风险面。凡本计划标注为「假设」的地方，见 §2.2。

### 1.2 真正剩下的缺陷只有两条，都在**本仓库**内

**缺陷①（任务 1）：错误文案里就地手写了一条指路，而那条指路没点名参数。**

`MCP/index.js:4254-4255`：

```js
      return contentErrorResponse('PLAY_FOR_MAIN_THREAD',
        'rimworld.play_for 当前不可用：RimBridge 端调用 Unity get_realtimeSinceStartup 需在主线程执行（C# 端缺陷）。建议改用 step_game_ticks 逐帧推进，或等待 RimBridge mod 修复。');
```

这段文字**是本项目写的**（`errorCode` 字面量 `'PLAY_FOR_MAIN_THREAD'` 与中文 message 都只出现在这一行；RBS 侧单词检索 `realtimeSinceStartup` / `main thread` 均 0 命中）。它把调用方送去第二个工具却不说参数名——**这正是板子 [22] 踩坑的第二跳**。它也是「错误文案里嵌指路」这一反模式在本仓库的**第三份副本**（前两份是 `:3611` 与 `:4284`，由计划 07 任务 2 处理）。

**缺陷②（任务 2，更值得修）：RBS 声明的 `additionalProperties: false` 没有任何一层在执行。**

- RBS 下发的 schema 明确写着 `"additionalProperties": false`；
- 调用通道 `agg_call_tool` 的 `args` 声明却是 `additionalProperties: true`（`MCP/index.js:2808`）；
- 本仓库其实**早就记下过这件事**——`MCP/index.js:2487-2488` 的注释写着：「这里原先声明 additionalProperties:false，但实测未声明的多余参数会被静默忽略（服务端不校验）」。当时对原生工具的选择是把声明去掉；**镜像工具的 schema 是 RBS 下发的、我们改不了，只能真的执行它**。

后果是**静默用错值**：`{durationSeconds:3}` 会用 RBS 的默认 `durationMs` 跑一次，返回 `success:true`，而返回体里**连线索都没有**。`{tickCount:180}` 那次至少还能从 `requestedTicks:1` 看出来——**任务 2 要修的是比那更隐蔽的那一半**。

### 1.3 为什么这条不是 🔴（严重度复评 🟠 → 🟡 的落点）

`step_game_ticks{tickCount:180}` 返回的是 `success:true` + `requestedTicks:1`——RBS 如实回报了「我实际被要求走 1 tick」，**工具层没有撒谎**，是调用方读错了。只要看 `requestedTicks`/`advancedTicks` 就能发现，属于「可发现但费 token」。裁决报告因此复评为 🟡。本计划接受这个评级：两处改动都只改**本仓库自己发出去的话**与**本仓库自己没执行的校验**，不改变任何已验证可用的能力。

---

## 2. 范围

### 2.1 ✅ 进计划

| 项 | 落点 | 理由 |
|---|---|---|
| **① `PLAY_FOR_MAIN_THREAD` 的替代建议提成单一事实源 + 点名 `ticks`** | 任务 1 | 本项目 100% 归属；成本 ≈ 1 处文案 + 1 张表 |
| **② GABP 镜像未知参数守卫（fail-loud + 点名回显）** | 任务 2 | 契约在 RBS 侧、执行在 MCP 侧，缺口归属本项目；比 ① 更隐蔽 |
| **③ `TOOL_USAGE_NOTES` 补 `rimworld.step_game_ticks` 一条** | 任务 1 步骤 7 | 1 个对象的 1 行。`mcp_help tool=` 走 `index.js:3667` 的 `...(TOOL_USAGE_NOTES[meta.name] || {})` 展开，**这是让"参数名是 ticks"出现在调用方必然会看的那一页**上的最低成本做法 |

### 2.2 ❌ 明确排除

| 排除项 | 理由 |
|---|---|
| **改 RBS 的 `inputSchema` 生成逻辑 / 给 `step_game_ticks` 补手写 schema 常量** | 它没坏（实机 `mcp_help` 已返回完整 5 参数）。改成硬编码只会制造第二个事实源并引入腐烂风险 —— 与计划 07 §0.7「不缓存镜像 schema」同一条纪律 |
| **给板子 [22] 的 `params` 字段"补值"** | `params` 这个字段名在 `mcp_help` 的返回体里**根本不存在**（返回的是 `inputSchema`）。板上那句是转述不是逐字引用。去修它等于修一个不存在的字段 |
| **把「`usageHint` 对离线镜像说谎」拉进来做** | 已由计划 07 任务 2 覆盖（裁决报告 §6.1 修法 C）。重复做会让 07 的 10 个测试被无关分支污染 |
| **修 `play_for` 的主线程缺陷本身** | 需要改 RBS（本仓库无 RBS 源码）。本计划**只能改文案，不能改行为**——文案里必须如实说明这一点 |
| **让 `step_game_ticks` 支持 `tickCount` 之类的参数别名** | 这会把本仓库变成第二个参数名事实源。真要别名，加在 `TOOL_ARG_ALIASES`（`index.js:3785`）里显式登记，且必须有 schema 声明——本条不需要 |
| **沿用「≈6 分钟/轮冷重启、1/3 空中样本不够」的影响叙述** | 无法核实（§1.1 末行）。任何文案、注释、commit 说明里都**不得**出现它 |

### 2.3 标为「假设」的部分（不得当作事实引用）

- **`requestedTicks` / `completedTicks` / `advancedTicks` 的精确语义**是按字段命名与类型推断的，本计划**没有**真实回包实测。任务 1 的文案里**只**引用字段名本身（`requestedTicks:1` 这个**已实测出现过的值**，见 `MCP/logs/mcp-20261001-093829.log`），**不解释** `advancedTicks` 的含义。
- **「`step_game_ticks` 在 play_for 失效时总是可达」**按日志推断：同一 GABP 连接下 `play_for` 失败的那次会话里，`step_game_ticks{ticks:5}` 是 `success`（`mcp-20261001-093829.log:169`）。但**没有任何机制保证** RBS 未来不会同时移除它——所以任务 1 的文案措辞是「改用 X」，并由 `MIRROR_ALTERNATIVE_HINTS` 表集中登记，改一处即可。

---

## 3. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/index.js` | 修改（2 处插入 + 2 处改写 + 1 处加钩子，约 +95 行） | 全部逻辑。 |
| `MCP/test/playForGuidance.test.js` | **新建** | 任务 1 的 5 个测试：钉住「点名 `ticks`」「替代目标登记在册」「文案单一事实源」「`mcp_help` 能查到参数名」。 |
| `MCP/test/mirrorArgGuard.test.js` | **新建** | 任务 2 的 7 个测试：钉住守卫的纯函数语义、白名单、放行条件、回显契约、以及「离线时对既有行为零影响」。 |

**明确不动：** `Source/` 下任何 `.cs`、`Assemblies/`、`MCP/gabpClient.js`、`MCP/toolConfig.json`、`MCP/config.json`、`MCP/package.json`、任何 `*.log`、`docs/mcp-吐槽板.md`、`docs/issue-triage/` 下任何文件（**除本计划自身**）、以及 `MCP/index.js` 里第 4371-4382 行那一段（既有源码切片断言依赖它）。

### `MCP/index.js` 的 5 个改动点一览

| # | 锚点（行号已复核） | 动作 | 任务 |
|---|---|---|---|
| 1 | `4210`（`// Tool handler function` 之前） | 新增 `MIRROR_ALTERNATIVE_HINTS` + `formatMirrorAlternative()` | 1 |
| 2 | `2982` 之后 / `2983`（`};`）之前 | `TOOL_USAGE_NOTES` 新增 `rimworld.step_game_ticks` 一条 | 1 |
| 3 | `4254-4255` | `PLAY_FOR_MAIN_THREAD` 文案改为调 `formatMirrorAlternative(name)` | 1 |
| 4 | `4210` 之后（紧跟 #1 的块）/ `4244` | 新增 `GABP_MIRROR_LOCAL_ARGS` + 三个函数；`4244` 加一行守卫调用 | 2 |
| 5 | `5453` 之后 | `_testHooks` 加 `setGabpTools` | 2 |

> **#1 与 #4 为什么都落在 4210：** 两块声明的唯一消费者都在 `handleToolCall` 里（4254 与 4244），放在函数体外可以避免每次调用都重建对象。4210 在 `PLAY_FOR_CONTROL_KEYS`（4082-4085）之后 ⇒ #4 的白名单可以直接引用那个既有常量，不会撞 TDZ。

---

## 4. 与计划 07 的共用常量：怎么对齐、谁先落地

计划 07（`plans/2026-10-01-07-gabp-offline-help-fallback.md` 任务 2）已经在做同一件事的另一面：**把「去 `mcp_help` 查」提成单一事实源**，动的正是 `MCP/index.js:3611` 与 `:4284`。

**两者的关系：同一个反模式（错误/提示文案里就地手写指路），两条不同的事实。**

| | 计划 07 | 本计划（任务 1） |
|---|---|---|
| 事实是什么 | 这个工具的**说明**去哪儿查 | 这个工具坏了**改用什么** |
| 常量 | `MCP_HELP_TOOL_POINTER`（计划 07 步骤 4 插在 `3594`/`3596` 之间） | `MIRROR_ALTERNATIVE_HINTS` + `formatMirrorAlternative()`（本计划插在 `4210`） |
| 使用点 | `:3610-3611`（`agg_list_tools`）、`:4277-4279`（镜像 fail-fast） | `:4254-4255`（`PLAY_FOR_MAIN_THREAD`） |
| 回答的问题 | `mcp_help tool=<名字>` 查得到吗 | `step_game_ticks{ticks:N}` 真的能用吗 |

**落地方式（三条硬约定，执行者照办）：**

1. **标识符不冲突、插入点不相邻、使用点不重叠。** 07 的插入点（3594/3596）与使用点（3610-3611、4277-4279）同本计划的插入点（4210）和使用点（4254-4255）**互不相交**，最远的一对（4277 vs 4254）相隔 23 行，且**没有任何一条源码切片断言**横跨这段。⇒ **两份计划可以任意顺序落地，不需要重排、不需要 cherry-pick 协调。**
2. **谁先落地都行，互不阻塞。** 两条常量描述的是不同的事实，谁也不引用谁。若两份都已落地，**唯一约定的收尾动作**是：把两个常量的注释块在各自文件里互相留一行「另一条指路事实源在 X」——这是**可选**的注释整理，**不是阻塞项**，不要为了它去改计划 07 的产物文件。
3. **共同的纪律（本计划用测试钉死）**：错误/提示文案里**不允许**再出现就地手写的指路散文。任务 1 步骤 3 有一条源码断言要求旧的内联文案 `'建议改用 step_game_ticks 逐帧推进'` 从 `index.js` 里**彻底消失**，且 `:4254-4255` 附近必须调 `formatMirrorAlternative(...)`。这条断言**不依赖计划 07 是否落地**，因此两份计划各自 `npm test` 都能独立通过。

---

## 任务 1：把 `PLAY_FOR_MAIN_THREAD` 的替代建议提成单一事实源，并点名 `ticks`

**交付物：** `MCP/index.js:4254-4255` 的错误文案不再就地手写指路，改为调 `formatMirrorAlternative(name)`；新增的 `MIRROR_ALTERNATIVE_HINTS` 表是「工具坏了改用什么」的唯一出处；`mcp_help tool=rimworld.step_game_ticks` 能查到正确参数名。

**文件：**
- 修改：`MCP/index.js:4210` 之前（新增 `MIRROR_ALTERNATIVE_HINTS` + `formatMirrorAlternative`）
- 修改：`MCP/index.js:4254-4255`（文案改用 `formatMirrorAlternative`）
- 修改：`MCP/index.js:2982` 之后 / `2983` 之前（`TOOL_USAGE_NOTES` 新增一条）
- 创建：`MCP/test/playForGuidance.test.js`

- [ ] **步骤 1：编写失败的测试**

创建 `MCP/test/playForGuidance.test.js`：

```javascript
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：必须在 import('../index.js') 之前设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8');

// 假桥接：让 play_for 真的走到 callGABPTool 并返回 Unity 主线程错误原文，
// 从而触发 handleToolCall:4253 的正则判定。不起真游戏、不改任何游戏状态。
const MAIN_THREAD_ERR = 'get_realtimeSinceStartup can only be called from the main thread';
function withFakeBridge(fn) {
  mod._testHooks.setGabpBridge({
    isConnected: () => true,
    state: 'connected',
    callTool: async () => ({ ok: true, result: { success: false, error: MAIN_THREAD_ERR } }),
  });
  try { return fn(); } finally { mod._testHooks.setGabpBridge(null); }
}
afterEach(() => mod._testHooks.setGabpBridge(null));

// ---- ① 文案单一事实源：内联指路散文必须消失 ----

test('旧的内联指路文案已从 index.js 消失（错误文案里不许就地手写"改用什么"）', () => {
  assert.ok(!source.includes('建议改用 step_game_ticks 逐帧推进'),
    'MCP/index.js:4255 仍在就地手写指路；应改为调 formatMirrorAlternative(name)');
});

test('PLAY_FOR_MAIN_THREAD 分支调的是格式器，不是字面量', () => {
  const i = source.indexOf("if (name === 'rimworld.play_for') {");
  assert.ok(i > 0, '找不到 play_for 分支');
  const block = source.slice(i, source.indexOf('isGabpMirrorName(name)', i));
  assert.match(block, /formatMirrorAlternative\(name\)/, 'play_for 分支必须调单一事实源');
  assert.match(block, /PLAY_FOR_MAIN_THREAD/, 'errorCode 保持不变（这是板子与日志都已记录的稳定契约）');
});

// ---- ② 替代建议必须"已验证可达"：目标名必须登记在册，且点名真实参数 ----

test('替代目标 rimworld.step_game_ticks 登记在册（菜单/注册表里查得到这个名字）', async () => {
  const names = await mod.menuUniverseNames();
  assert.ok(names.includes('rimworld.step_game_ticks'),
    '替代目标必须是本项目已登记的镜像名，不能是现编的字符串');
  assert.ok(names.includes('rimworld.play_for'), '故障源本身也应登记在册');
});

test('替代建议点名 ticks 且不再把 tickCount 当参数', async () => {
  const res = await withFakeBridge(() => mod.handleToolCall('rimworld.play_for', {}));
  assert.equal(res.isError, true);
  const body = JSON.parse(res.content[0].text);
  assert.equal(body.errorCode, 'PLAY_FOR_MAIN_THREAD');
  assert.match(body.message, /rimworld\.step_game_ticks/, '必须点名替代工具');
  assert.match(body.message, /"ticks"/, '必须点名真实参数名 ticks');
  assert.match(body.message, /不是 tickCount/, '必须显式否掉板子上的那个错参数名');
  assert.match(body.message, /C# 端缺陷/, '如实说明这是上游缺陷、本仓库改不了行为');
  assert.match(body.message, /requestedTicks/, 'message 必须点名"只走 1 tick"的可见线索');
  // 本测试直调 handleToolCall，绕开了 tools/call 层；实机经 agg_call_tool 调用时
  // attachUsageOnFailure（:4006，触发条件见 :4009-4018）会往这个失败体上**追加** usage
  // （toolSchema 能在 gabpTools 里查到 step_game_ticks ⇒ usage.params 是真实 schema）。
  // 它只加字段、不动 message，所以上面 4 条断言在真机上同样成立。
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/playForGuidance.test.js
```

预期：**4 个全部 FAIL**，逐条原因：

- ① `source.includes('建议改用 step_game_ticks 逐帧推进')` 为 `true` —— 旧文案仍在 4255 行。
- ② `block` 里没有 `formatMirrorAlternative(name)`（源码里压根没这个函数）。
- ③ 这一条**预期 PASS**（`menuUniverseNames()` 与本任务无关，GABP 未连接时它仍靠 `TOOL_SUBGROUP` + `toolConfig.tools` 登记推出两个名字）。**若它 FAIL，说明环境出了问题，停下来先排查，不要继续往下做。**
- ④ `body.message` 不含 `rimworld.step_game_ticks` 的带参形式，也不含 `不是 tickCount` / `requestedTicks`（当前只有一句「建议改用 step_game_ticks 逐帧推进」）。

- [ ] **步骤 3：新增「替代建议」单一事实源**

在 `MCP/index.js:4209`（空行）与 `:4210`（`// Tool handler function`）**之间**插入：

```javascript
// ========== 替代建议单一事实源（issue-triage [22]） ==========
// 为什么必须有这张表：PLAY_FOR_MAIN_THREAD 的旧文案在错误体里**就地手写**了一句替代建议
// 「建议改用 step_game_ticks 逐帧推进」——既没点名参数，也没说这个替代到底可不可用。
// 板子 [22] 把它读成"两层都坏、没有可用路径"，那是错的：step_game_ticks 一直好用
// （MCP/logs/mcp-20260928-173737.log 里 ticks=200/300/120/90/400/25 六次全部 success，
//  400 tick 耗时 7337ms）。**写法错误的代价不在于没有替代，而在于没点名参数。**
// 本表是"工具不可用时该改用什么"的唯一出处：新增条目只改这里，错误文案只调 formatMirrorAlternative()。
// 与计划 07 的 MCP_HELP_TOOL_POINTER 同构但**不是同一条事实**——那是"说明去哪儿查"，
// 本表是"改用什么"；两者标识符、插入点、使用点均不重叠（见本计划 §4）。
const MIRROR_ALTERNATIVE_HINTS = {
  'rimworld.play_for': {
    tool: 'rimworld.step_game_ticks',
    // 可达性依据（不是拍脑袋）：见上引日志，六次不同 tick 数的调用全部 success。
    example: '{ "ticks": 180, "timeoutMs": 60000 }',
    note: '参数名是 ticks，不是 tickCount：RBS 的签名是 StepGameTicks(int ticks = 1, int timeoutMs = 10000, int pollIntervalMs = 10, bool pauseFirst = true, bool playSound = false)。'
      + '写成 tickCount 会被静默丢弃并回落到默认值 ticks=1，返回体里的 requestedTicks:1 就是这么来的——'
      + '只看 success 会误判成"只能走 1 tick"，请同时读 requestedTicks / completedTicks。',
    keepWaiting: '实测 400 tick 约 7.3s，timeoutMs 要给足，否则先超时的是等待而不是步进。'
  }
};

// 渲染某个镜像工具失效时的替代建议。未登记的工具返回中性文案（不给没核实过的指路）。
function formatMirrorAlternative(name) {
  const h = MIRROR_ALTERNATIVE_HINTS[name];
  if (!h) {
    return '该工具当前不可用；当前可调用工具集见 agg_list_tools（未登记的替代方案一律不给，避免把调用方送去死路）。';
  }
  const parts = [`改用 ${h.tool}：agg_call_tool { tool: "${h.tool}", args: ${h.example} }。`, h.note];
  if (h.keepWaiting) parts.push(h.keepWaiting);
  return parts.join('');
}
```

- [ ] **步骤 4：让 `PLAY_FOR_MAIN_THREAD` 文案调这个事实源**

把 `MCP/index.js:4254-4255` 这两行：

```javascript
      return contentErrorResponse('PLAY_FOR_MAIN_THREAD',
        'rimworld.play_for 当前不可用：RimBridge 端调用 Unity get_realtimeSinceStartup 需在主线程执行（C# 端缺陷）。建议改用 step_game_ticks 逐帧推进，或等待 RimBridge mod 修复。');
```

替换为：

```javascript
      // name 在此处必然仍是 'rimworld.play_for'（上游 if 判定过），直接用它查表，不写第二份字面量
      return contentErrorResponse('PLAY_FOR_MAIN_THREAD',
        'rimworld.play_for 当前不可用：RimBridge 端调用 Unity get_realtimeSinceStartup 需在主线程执行'
        + '（C# 端缺陷，行为需 RimBridge mod 侧修复；本 MCP 服务器只能改文案，改不了行为）。'
        + formatMirrorAlternative(name));
```

- [ ] **步骤 5：追加第 5 个失败的测试（`mcp_help` 必须查得到正确参数名）**

在 `MCP/test/playForGuidance.test.js` **末尾追加**：

```javascript
// ---- ③ 调用方必然会看的那一页：mcp_help tool= 必须给出正确参数名 ----

test('TOOL_USAGE_NOTES 里补了 rimworld.step_game_ticks，且示例用 ticks', async () => {
  // mcp_help 的 tool 分支在 index.js:3667 做 `...(TOOL_USAGE_NOTES[meta.name] || {})` 展开，
  // 所以这条 note 会原样出现在 mcp_help tool=rimworld.step_game_ticks 的返回体里。
  // 这里不连游戏（GABP 未连接 ⇒ gabpTools 为空 ⇒ 查不到该工具），
  // 因此只断言"note 本身写对了"，不断言它已出现在 mcp_help 返回体里。
  const detail = mod.mcpHelpResult({ tool: 'hotreload_status' }); // 任取一个本地工具，验证展开链路没被改坏
  assert.equal(JSON.parse(detail.content[0].text).success, undefined,
    '本地工具的 mcp_help 返回体形状不受本任务影响（不带 success）');
  assert.ok(source.includes("'rimworld.step_game_ticks': {"),
    'TOOL_USAGE_NOTES 里必须有 rimworld.step_game_ticks 这一条');
  const block = source.slice(source.indexOf("'rimworld.step_game_ticks': {"), source.indexOf('};', source.indexOf("'rimworld.step_game_ticks': {")));
  assert.match(block, /"ticks"/, '示例必须用真实参数名 ticks');
  assert.match(block, /不是 tickCount/, 'notes 必须显式否掉 tickCount');
});
```

- [ ] **步骤 6：给 `TOOL_USAGE_NOTES` 补 `rimworld.step_game_ticks`**

在 `MCP/index.js:2982`（`hotreload_status` 那一条）**之后**、`:2983`（`};`）**之前**插入：

```javascript
  // GABP 镜像（issue-triage [22]）：这张 note 会经 mcpHelpResult:3667 原样展开到
  // mcp_help tool=rimworld.step_game_ticks 的返回体里——是调用方必然会看到的那一页。
  'rimworld.step_game_ticks': {
    example: '{ "ticks": 180, "timeoutMs": 60000 }',
    notes: '给**暂停中**的游戏补真 tick 的入口（play_for 在 RimBridge 修好主线程缺陷前不可用）。'
      + '参数名是 ticks，不是 tickCount：RBS 签名 StepGameTicks(int ticks = 1, int timeoutMs = 10000, int pollIntervalMs = 10, bool pauseFirst = true, bool playSound = false)。'
      + '写成 tickCount 会被静默忽略并回落成 ticks=1，只看 success 会误判成"只能走 1 tick"，请同时读 requestedTicks / completedTicks。'
  }
```

- [ ] **步骤 7：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/playForGuidance.test.js
```

预期：**5 个全部 PASS**。若第 4 条（端到端那条）失败，打印 `body.message` 看实际文案——最常见的原因是文案漏了 `requestedTicks` 或漏了 `不是 tickCount`，**改实现，不要改断言**。

- [ ] **步骤 8：跑全量测试**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：全部 PASS。特别确认这三条与本任务相邻、极易被碰碎的：

| 断言 | 位置 | 本任务为什么不该影响它 |
|---|---|---|
| `toolListLine` → `buildMenuNode` 源码切片 | `test/toolchainFixes.test.js:120-124` | 本任务不碰这两个函数，插入点在 4210 / 2983 / 4254，切片逐字节不变 |
| `case 'start_game'` / `case 'stop_game'` 源码切片 | `test/toolchainFixes.test.js:263-268` | 本任务不碰 4371-4382 |
| `agg_call_tool` 调 `rimworld.press_accept` | `test/toolchainFixes.test.js:60-70` | 本任务不改镜像 fail-fast 分支（4281-4286），也不改 `gabpErrorResponse` |

另外确认 `MCP/index.js` 仍能正常加载：`node -e "process.env.UESD_MCP_NO_START='1'; import('./index.js').then(()=>console.log('OK'))"`（在 `MCP` 目录下执行）。这一步不启动服务器、不连游戏，只是验证没有语法错误。

- [ ] **步骤 9：改动清单（本项目不是 git 仓库，无提交动作）**

本次触碰 4 个文件：

| 文件 | 动作 | 锚点 |
|---|---|---|
| `MCP/index.js` | 修改（+22 行） | `4210` 前新增 `MIRROR_ALTERNATIVE_HINTS` + `formatMirrorAlternative` |
| `MCP/index.js` | 修改（+2 行 / -1 行） | `4254-4255` 文案改调 `formatMirrorAlternative(name)` |
| `MCP/index.js` | 修改（+7 行） | `2982` 之后 / `2983` 之前，`TOOL_USAGE_NOTES` 新增一条 |
| `MCP/test/playForGuidance.test.js` | **新建** | 5 个测试 |

**没有改任何 C#、没有改 `gabpClient.js`、没有改 `toolConfig.json` / `config.json` / `package.json`、没有重编译模组、没有碰 `docs/` 下除本计划外的任何文件。**

---

## 任务 2：GABP 镜像未知参数守卫（真正执行 RBS 的 `additionalProperties: false`）

**交付物：** 镜像工具收到 RBS schema 不认的参数时，MCP 层**当场拒绝**并点名回显（你传的键 + 它认识的键 + 去哪儿查完整 schema），而不是静默丢弃后回落到默认值再返回 `success:true`。守卫只在能拿到该工具真实 `inputSchema.properties` 时生效，拿不到一律放行。

**文件：**
- 修改：`MCP/index.js:4210` 之后（新增 `GABP_MIRROR_LOCAL_ARGS` + `mirrorDeclaredArgs` / `mirrorUnknownArgs` / `unknownArgsResponse` / `mirrorArgGuardEnabled`）
- 修改：`MCP/index.js:4244`（一行守卫调用）
- 修改：`MCP/index.js:5453` 之后（`_testHooks` 加 `setGabpTools`）
- 创建：`MCP/test/mirrorArgGuard.test.js`

- [ ] **步骤 1：先加测试钩子 `setGabpTools`**

守卫要读 `gabpTools`（`:2514`），但它只在 GABP 连上后由 `tools-changed` 事件填充（`:2639-2645`）。测试进程里它恒为 `[]`，不加钩子就没法测。加在 `MCP/index.js:5453`（`setGabpBridge: (b) => { gabpBridge = b; },`）**之后**：

```javascript
  // 2026-10-01 issue-triage [22]：未知参数守卫要读 gabpTools 的 inputSchema，
  // 而它只在 GABP 连上后由 tools-changed 填充 ⇒ 测试需要一个注入口，不必起真游戏。
  setGabpTools: (list) => { gabpTools = Array.isArray(list) ? list : []; },
```

- [ ] **步骤 2：编写失败的测试**

创建 `MCP/test/mirrorArgGuard.test.js`：

```javascript
import { test, afterEach } from 'node:test';
import assert from 'node:assert/strict';

process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

// RBS 真实下发的 schema 形状（反编译 Lib.GAB.Server.GabpServer.BuildInputSchema + 实机 mcp_help 双重证实）
const STEP_TICKS_SCHEMA = {
  type: 'object',
  properties: {
    ticks: { type: 'integer', description: 'Number of game ticks to advance.', default: 1 },
    timeoutMs: { type: 'integer', default: 10000 },
    pollIntervalMs: { type: 'integer', default: 10 },
    pauseFirst: { type: 'boolean', default: true },
    playSound: { type: 'boolean', default: false }
  },
  additionalProperties: false
};
const STEP_TICKS = { name: 'rimworld.step_game_ticks', inputSchema: STEP_TICKS_SCHEMA };

const PLAY_FOR = {
  name: 'rimworld.play_for',
  inputSchema: { type: 'object', properties: { durationMs: { type: 'integer' }, speed: { type: 'string' } }, additionalProperties: false }
};

let lastCall = null;
function withFakeGabp(tools, fn) {
  mod._testHooks.setGabpBridge({
    isConnected: () => true,
    state: 'connected',
    callTool: async (name, args) => { lastCall = { name, args }; return { ok: true, result: { success: true, requestedTicks: (args || {}).ticks ?? 1 } }; }
  });
  mod._testHooks.setGabpTools(tools);
  try { return fn(); } finally { mod._testHooks.setGabpBridge(null); mod._testHooks.setGabpTools([]); }
}
afterEach(() => { mod._testHooks.setGabpBridge(null); mod._testHooks.setGabpTools([]); });
```

接着**继续追加**到 `MCP/test/mirrorArgGuard.test.js` 末尾：

```javascript
// ---- ① 纯函数语义（不起游戏、不连桥接） ----

test('声明过的参数一律放行', () => {
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.step_game_ticks', { ticks: 180, timeoutMs: 60000 }), []);
});

test('未声明的参数被点名（板子 [22] 的 tickCount / durationSeconds 两个真实案例）', () => {
  mod._testHooks.setGabpTools([STEP_TICKS, PLAY_FOR]);
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.step_game_ticks', { tickCount: 180 }), ['tickCount']);
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.play_for', { durationSeconds: 3 }), ['durationSeconds']);
  mod._testHooks.setGabpTools([]);
});

test('MCP 本地键不得被误判成未知参数（它们由本层消费、不转发给 RBS）', () => {
  // 必须把带真实 schema 的工具也放进 gabpTools，否则守卫会因"取不到 schema"而放行，
  // 测出来的 [] 说明不了白名单起作用 —— 那正是它最容易悄悄坏掉的方式。
  mod._testHooks.setGabpTools([
    PLAY_FOR,
    { name: 'rimworld.dpa_patch_methods', inputSchema: { type: 'object', properties: { path: { type: 'string' } }, additionalProperties: false } },
    { name: 'rimworld.delete_area', inputSchema: { type: 'object', properties: { areaId: { type: 'string' } }, additionalProperties: false } }
  ]);
  // nonBlocking/sampleIntervalMs/... = PLAY_FOR_CONTROL_KEYS（index.js:4082-4085）
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.play_for', { nonBlocking: true, sampleIntervalMs: 500, snapshotPath: 'x' }), []);
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.play_for', { nonBlocking: true, typoKey: 1 }), ['typoKey'],
    '白名单只豁免已登记的本地键，同一批里真正写错的键仍要被抓出来');
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.dpa_patch_methods', { force: true }), [],
    'force 在 index.js:4345 转发前已 delete，属本层控制参数');
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.delete_area', { id: 'Area_6' }), [],
    'id/areaIdOrAlias 是 index.js:4267 的本地别名');
  mod._testHooks.setGabpTools([]);
});

test('拿不到 schema 一律放行（离线 / 未登记 / properties 为空）', () => {
  mod._testHooks.setGabpTools([]);                                    // GABP 未连接
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.step_game_ticks', { tickCount: 180 }), []);
  mod._testHooks.setGabpTools([{ name: 'rimworld.foo', inputSchema: { type: 'object', properties: {} } }]);
  assert.deepEqual(mod.mirrorUnknownArgs('rimworld.foo', { whatever: 1 }), [],
    'toMCPTool 的 fallback（gabpClient.js:75）就是空 properties，必须放行');
  mod._testHooks.setGabpTools([]);
});

test('非镜像工具完全不受守卫影响', () => {
  mod._testHooks.setGabpTools([STEP_TICKS]);
  assert.deepEqual(mod.mirrorUnknownArgs('mcp_help', { query: 'tick', whatever: 1 }), []);
  assert.deepEqual(mod.mirrorUnknownArgs('get_game_status', { anything: 1 }), []);
  mod._testHooks.setGabpTools([]);
});

// ---- ② 端到端：真调一次 handleToolCall（假桥接，不起游戏、不改游戏状态） ----

test('端到端：{tickCount:180} 被拒并点名回显，且**没有**转发给游戏', async () => {
  await withFakeGabp([STEP_TICKS], async () => {
    lastCall = null;
    const res = await mod.handleToolCall('rimworld.step_game_ticks', { tickCount: 180 });
    assert.equal(res.isError, true);
    const body = JSON.parse(res.content[0].text);
    assert.equal(body.success, false);
    assert.equal(body.errorCode, 'UNKNOWN_MIRROR_ARG');
    assert.equal(body.tool, 'rimworld.step_game_ticks');
    assert.deepEqual(body.unknownArgs, ['tickCount']);
    assert.ok(body.knownArgs.includes('ticks'), '必须点名它认识的参数（含 ticks）');
    assert.match(body.message, /静默丢弃/);
    assert.match(body.message, /mcp_help/, '必须给出去哪儿查完整 schema 的路');
    assert.equal(lastCall, null, '被拒的调用**绝不能**真的发出去');
  });
});

test('端到端：合法调用照常转发，args 原样送到 GABP', async () => {
  await withFakeGabp([STEP_TICKS], async () => {
    lastCall = null;
    const res = await mod.handleToolCall('rimworld.step_game_ticks', { ticks: 5, timeoutMs: 60000 });
    assert.notEqual(res.isError, true);
    assert.ok(lastCall, '合法调用必须真的转发');
    assert.equal(lastCall.name, 'rimworld/step_game_ticks');
    assert.deepEqual(lastCall.args, { ticks: 5, timeoutMs: 60000 });
  });
});

test('端到端：GABP 未连接时既有形状逐字节不变（守卫不得抢戏）', async () => {
  // 真·未连接：bridge=null。直调 handleToolCall 会走 index.js:4274-4279 的 fail-fast，
  // 经 gabpErrorResponse（:2697-2702）→ wrapGABPResult(msg,false) ⇒ errorCode 固定 'GABP_ERROR'。
  // （注意：既有测试 toolchainFixes.test.js:60-70 断言的 'GABP_MIRROR_UNAVAILABLE' 走的是另一条路
  //  —— agg_call_tool 在 :5097 发现 allToolNames() 里没有该镜像名 ⇒ unknownToolText（:3624）。）
  mod._testHooks.setGabpBridge(null);
  mod._testHooks.setGabpTools([]);
  const res = await mod.handleToolCall('rimworld.press_accept', {});
  assert.equal(res.isError, true);
  const body = JSON.parse(res.content[0].text);
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'GABP_ERROR',
    '离线时必须仍是原来的 fail-fast，不许被守卫改写成 UNKNOWN_MIRROR_ARG');
  assert.match(body.error, /GABP 未连接/);
  assert.match(body.error, /brrainz\.rimbridgeserver/);
});

test('逃生阀：UESD_MCP_ALLOW_UNKNOWN_MIRROR_ARGS=1 时整体放行（真机应急用）', () => {
  process.env.UESD_MCP_ALLOW_UNKNOWN_MIRROR_ARGS = '1';
  try {
    mod._testHooks.setGabpTools([STEP_TICKS]);
    assert.deepEqual(mod.mirrorUnknownArgs('rimworld.step_game_ticks', { tickCount: 180 }), []);
  } finally {
    delete process.env.UESD_MCP_ALLOW_UNKNOWN_MIRROR_ARGS;
    mod._testHooks.setGabpTools([]);
  }
});
```

- [ ] **步骤 3：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/mirrorArgGuard.test.js
```

预期：**9 个里 6 个 FAIL（第 1-6 条），第 7、8 条 PASS**。逐条：

| # | 此刻 | 为什么 |
|---|---|---|
| 1、2、3、4、5、9 | FAIL | `TypeError: mod.mirrorUnknownArgs is not a function` —— 守卫还没写、还没导出 |
| 6 `{tickCount:180}` | FAIL | 守卫不存在 ⇒ 参数**真的被转发出去**（`lastCall` 非 null）、`errorCode` 也不是 `UNKNOWN_MIRROR_ARG`。**这正是本任务要消灭的那个静默用错值** |
| 7 合法调用 | PASS | 既有行为，本来就该通。过不了说明假桥接/`timeoutOptsFor` 有别的因素，先打印 `lastCall` 排查，**不要改断言** |
| 8 未连接形状 | PASS | 这是我们要守住的下界（守卫不抢戏）。**若它 FAIL，说明环境里 `gabpBridge` 不是 null，先修环境再往下做** |

> 第 3、6 条若报的是 `mod._testHooks.setGabpTools is not a function` 而不是 `mirrorUnknownArgs`，说明**步骤 1 漏了**，回到步骤 1 补上。

- [ ] **步骤 4：实现守卫（在 `MCP/index.js:4210` 之前、紧跟任务 1 的块之后）**

在 `MCP/index.js:4209`（空行）与 `:4210`（`// Tool handler function`）之间，**紧接任务 1 的 `formatMirrorAlternative` 之后**插入（此时 4254-4255 已改写完成，所以 4210 附近已有任务 1 的块）：

```javascript
// ========== GABP 镜像未知参数守卫（issue-triage [22]） ==========
// 契约在 RBS 侧，执行在 MCP 侧，而 MCP 侧一直没执行：
//   · RBS 下发的 inputSchema 明确写着 "additionalProperties": false（Lib.GAB BuildInputSchema 生成）；
//   · 但调用通道 agg_call_tool 的 args 声明是 additionalProperties: true（index.js:2808），且没有任何一层校验；
//   · 本仓库早就记下过这件事：post_map_marker 的 schema 注释（index.js:2487-2488）写着
//     「这里原先声明 additionalProperties:false，但实测未声明的多余参数会被静默忽略（服务端不校验）」。
//     当时对原生工具的选择是把声明去掉；**镜像工具的 schema 是 RBS 下发的、我们改不了，只能真的执行它。**
// 后果是静默用错值：{tickCount:180} 回落成 ticks=1（还有 requestedTicks:1 当线索），
//   而 {durationSeconds:3} 会用 RBS 默认的 durationMs 真跑一次——**连线索都没有**，那才是本条最该修的一半。
//
// 放行原则（宁可不拦，不可误伤）：
//   ① 非镜像工具 → 放行；② 该工具不在 gabpTools（GABP 未连接/工具已移除）→ 放行；
//   ③ inputSchema.properties 缺失或为空（gabpClient.js:75 的 fallback 就是空表）→ 放行；
//   ④ 只拦 args 的顶层键；⑤ 逃生阀 UESD_MCP_ALLOW_UNKNOWN_MIRROR_ARGS=1 整体关闭。

// 逃生阀：沿用 index.js:5406 的 UESD_MCP_NO_START 同款 env 开关惯例。
// 真机上若发现某个 RBS 工具的 schema 与它实际接受的参数对不上，起 MCP 前设这个变量即可整体关掉守卫。
function mirrorArgGuardEnabled() {
  return process.env.UESD_MCP_ALLOW_UNKNOWN_MIRROR_ARGS !== '1';
}

// MCP 层自己消费、**不会**转发给 RBS 的键：它们不在 RBS 的 properties 里，但也不该被守卫拦下。
// 与既有 PLAY_FOR_CONTROL_KEYS（index.js:4082-4085）、dpa_patch_methods 的 force 剥离（:4345）、
// delete_area/clear_area 的本地别名（:4267）同源；新增此类键时**必须**在这里登记，否则会被误伤。
const GABP_MIRROR_LOCAL_ARGS = {
  'rimworld.play_for': PLAY_FOR_CONTROL_KEYS,
  'rimworld.dpa_patch_methods': ['force'],
  'rimworld.delete_area': ['id', 'areaIdOrAlias'],
  'rimworld.clear_area': ['id', 'areaIdOrAlias']
};

// 该镜像工具的 inputSchema.properties 键表；拿不到 / 空表 → null（守卫据此整体放行）。
function mirrorDeclaredArgs(name) {
  if (!isGabpMirrorName(name)) return null;
  const tool = gabpTools.find(t => t && t.name === name);
  const props = tool && tool.inputSchema && tool.inputSchema.properties;
  if (!props || typeof props !== 'object' || Array.isArray(props)) return null;
  const declared = Object.keys(props);
  return declared.length > 0 ? declared : null;
}

// 纯函数：算出本次调用里 RBS 不认识的顶层键。拿不到 schema / 守卫关闭 → []。
function mirrorUnknownArgs(name, args) {
  if (!mirrorArgGuardEnabled()) return [];
  const declared = mirrorDeclaredArgs(name);
  if (!declared) return [];
  const allowed = new Set(declared.concat(GABP_MIRROR_LOCAL_ARGS[name] || []));
  const keys = (args && typeof args === 'object' && !Array.isArray(args)) ? Object.keys(args) : [];
  return keys.filter(k => !allowed.has(k));
}

// 回显契约，与 plans/2026-10-01-08-mcp-help-query-param.md §2.3 第 2 条同一套措辞：
// **被拒的参数必须点名回显，不回显 = 假成功。** 这里比那条更严格——不是"覆盖了但告诉你"，
// 而是"认不出的参数直接不让发出去"，并把你传的键、它认识的键、去哪儿查完整 schema 三样都给全。
// 注意返回体**不带** usage 字段：attachUsageOnFailure（:4006，触发条件 :4009-4018）会自动补一个
// schema 准确的 usage（toolSchema 能查到 gabpTools，见 :3875-3883），不必在这里手写第二份。
function unknownArgsResponse(name, unknown) {
  const declared = mirrorDeclaredArgs(name) || [];
  const local = GABP_MIRROR_LOCAL_ARGS[name] || [];
  return {
    content: [{ type: 'text', text: JSON.stringify({
      success: false,
      errorCode: 'UNKNOWN_MIRROR_ARG',
      tool: name,
      message: `${name} 的 schema 不认这些参数：${unknown.join('、')}。`
        + `它认识的是：${declared.join('、') || '（无）'}。`
        + '该工具的 schema 由 RimBridgeServer 下发并声明 additionalProperties:false，'
        + '未声明的参数会被**静默丢弃**并回落到默认值——那样你会拿到一个 success:true 但什么都没按你传的做的结果。'
        + '已原样拒绝、**没有**转发给游戏（游戏状态未变）；改正参数名后重试。'
        + `完整 schema（含类型与默认值）见 mcp_help tool="${name}" 的 inputSchema。`,
      unknownArgs: unknown,
      knownArgs: declared,
      localArgs: local
    }, null, 2) }],
    isError: true
  };
}
```

- [ ] **步骤 5：把 `mirrorUnknownArgs` 加进 export 清单**

在 `MCP/index.js:5443`（`};`，`export { ... }` 的收尾）**之前**、`:5442`（`menuUniverseNames,`）**之后**加一行：

```javascript
  // issue-triage [22]：GABP 镜像未知参数守卫（纯函数 + 回显体），供单测直接断言
  mirrorUnknownArgs,
```

- [ ] **步骤 6：接线到派发路径（`MCP/index.js:4244`）**

把 `MCP/index.js:4244`（`if (name === 'task_configure') return handleTaskConfigure(args);`（4243）之后、`// ---------- play_for：长任务支持（2026-09-17） ----------`（4245）之前的空行）替换为：

```javascript
  // ---------- GABP 镜像未知参数守卫（issue-triage [22]） ----------
  // 为什么必须排在 play_for 分支（4250）之前：play_for 是**唯一**在 isGabpMirrorName 块（4263）之前
  // 被接管的镜像工具，放这里才能让它和其余镜像走同一道校验。
  // 为什么一处就够：直接调用与 agg_call_tool 的 args 最终都汇合到 handleToolCall 的这一行（见 :5107），
  // 所以 create_allowed_area（:4305）、delete_zone（:4320）、dpa_patch_methods（:4346）、
  // 以及镜像尾部默认转发（:4350）全部自动覆盖，不需要在每个分支各写一遍。
  // mirrorUnknownArgs 对非镜像工具、以及拿不到 schema 的情形一律返回 []（见上方注释的放行原则）。
  const _unknown = mirrorUnknownArgs(name, args);
  if (_unknown.length > 0) return unknownArgsResponse(name, _unknown);
```

> **注意执行顺序：步骤 1 的 `setGabpTools` 必须先落地**，否则测试里 `mod._testHooks.setGabpTools` 不存在，第 3 步会整片红掉。

- [ ] **步骤 7：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/mirrorArgGuard.test.js
```

预期：**9 个全部 PASS**。重点确认两条端到端：

- `{tickCount:180}` → `errorCode === 'UNKNOWN_MIRROR_ARG'`、`unknownArgs === ['tickCount']`、`knownArgs` 含 `'ticks'`、**且 `lastCall === null`**（真的没发出去）。
- `{ticks:5, timeoutMs:60000}` → `lastCall.name === 'rimworld/step_game_ticks'`、`args` 逐字段原样。

若第 1 条红了但第 2 条也红了，多半是守卫插错了位置（比如被放进了 `isGabpMirrorName` 块内部，`play_for` 走不到）——回步骤 6 对照行号。

- [ ] **步骤 8：跑全量测试**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：全部 PASS，且**总数比落地前多 14 个**（任务 1 的 5 个 + 任务 2 的 9 个）。逐条确认下列既有断言没被碰碎：

| 断言 | 位置 | 为什么不该受影响 |
|---|---|---|
| `toolchainFixes.test.js:60-70`（`rimworld.press_accept` → `GABP_MIRROR_UNAVAILABLE`） | 守卫只在有 schema 时生效，测试进程 `gabpTools` 为 `[]` ⇒ no-op | 任务 2 步骤 6 的第 8 条测试已直接钉死这一点 |
| `toolchainFixes.test.js:120-124`（`toolListLine`→`buildMenuNode` 切片） | 守卫代码插在 4210，不在 3300 段切片范围内 | 切片逐字节不变 |
| `toolchainFixes.test.js:263-268`（`start_game`/`stop_game` 切片） | 不碰 4371-4382 | 同上 |
| `index.integration.test.js:163-195`（超时计数两对照） | 守卫插在 `handleToolCall` 内，该测试直调 `callGABPTool`，不经过它 | 不受影响 |

- [ ] **步骤 9：改动清单（本项目不是 git 仓库，无提交动作）**

本次触碰 4 个文件：

| 文件 | 动作 | 锚点 |
|---|---|---|
| `MCP/index.js` | 修改（+65 行） | `4210` 前新增 `mirrorArgGuardEnabled` / `GABP_MIRROR_LOCAL_ARGS` / `mirrorDeclaredArgs` / `mirrorUnknownArgs` / `unknownArgsResponse` |
| `MCP/index.js` | 修改（+11 行） | `4244` 守卫接线 |
| `MCP/index.js` | 修改（+1 行） | `5453` 之后 `_testHooks.setGabpTools` |
| `MCP/index.js` | 修改（+1 行） | `5442` 之后 export `mirrorUnknownArgs` |
| `MCP/test/mirrorArgGuard.test.js` | **新建** | 9 个测试 |

**两份计划合起来的总改动**：`MCP/index.js`（+100 行左右，3 个测试文件共 14 个测试）之外**零改动**——没碰 `Source/`、`gabpClient.js`、`toolConfig.json`、`config.json`、`package.json`，没重编译模组，没动 `docs/` 下除两份计划自身以外的任何文件。

- [ ] **步骤 10：真机人工验收（**可选**，需要维护者在并行调试会话结束后自己做）**

> ⚠️ 本计划**不代替**人工验收——上面 14 个测试全部是**静态/离线**验证（假桥接 + 假 schema），
> 它们证明的是"MCP 层的校验逻辑正确"，**不是**"RBS 真实下发的 schema 与我们假设的一致"。
> 那一条只有连上真游戏才验得了。验收时**只调只读工具**，别在并行会话调试期间做。

GABP 连上后依次跑（都不改变游戏状态）：

1. `mcp_help {tool: "rimworld.step_game_ticks"}` → 确认 `inputSchema.properties` 里确有 `ticks`/`timeoutMs`/`pollIntervalMs`/`pauseFirst`/`playSound` 五个键，且返回体多了 `example: '{ "ticks": 180, "timeoutMs": 60000 }'` 与 `notes`（任务 1 的成果）。
2. `agg_call_tool {tool:"rimworld.step_game_ticks", args:{tickCount: 1}}` → 期望拿到 `errorCode: "UNKNOWN_MIRROR_ARG"`、`unknownArgs: ["tickCount"]`。
3. `agg_call_tool {tool:"rimworld.step_game_ticks", args:{ticks: 1, pauseFirst: true}}` → 期望正常返回 `success:true`。**这一步会真的让游戏走 1 tick**，若当时有人在调试请跳过。

若第 2 步反而透传出去了 ⇒ RBS 真实 schema 的 `properties` 与我们假设不同（例如它下发的是 `parameters` 而不是标准 JSON Schema），**此时不要放宽守卫**，改去核对 `MCP/logs/` 里那次 `tools-changed` 的实际内容，并按「`mirrorDeclaredArgs` 取不到 schema 就放行」的原则修正**取值来源**，而不是改成无条件透传。

---

## 5. 自检（writing-plans 的三项）

**① 规格覆盖度。** 上游裁决报告 §6.1 列了三档修法：

| 修法 | 覆盖情况 |
|---|---|
| 修法 A（必做）：`PLAY_FOR_MAIN_THREAD` 文案改成可达且带参数名 | ✅ 任务 1 步骤 3-4 |
| 修法 A 的"更完整"版：替代建议提成可验证常量 + 返回体补 `alternativeVerified` | ✅ 任务 1 步骤 3 做成 `MIRROR_ALTERNATIVE_HINTS` 常量表；**没有**加 `alternativeVerified` 字段 —— 理由：那是给散文加一个布尔，而 `formatMirrorAlternative()` 把"能不能用"直接表达成"给不给得出这条建议"，登记表里没有的建议一律不给（步骤 3 的 else 分支）。再加布尔就是第二个事实源 |
| 修法 B：未知参数守卫（fail-loud / warn 二选一） | ✅ 任务 2，本计划选 **fail-loud**，理由见步骤 4 的回显契约注释 |
| 修法 B 的「至少 warn：透传但加 `ignoredArgs`」 | ❌ 不做。warn 版在 `play_for{durationSeconds:3}` 上仍会**真跑一次**（连线索都没有），与本条要修的东西正好相反 |
| 修法 C（可选）：`usageHint` 对离线镜像说谎 | ❌ 归计划 07 §0.8-4 / 任务 2，不重复（§2.2） |
| 裁决报告 §7.2 结论 2：与计划 07 共用「指路单一事实源」抽象 | ✅ §4 给了三条硬约定；**共用抽象与常量命名规范，不共用计划文档**，本计划不修改计划 07 的任何文件 |

吐槽板条目 [22] 的四条建议逐条对账：(a)「主线程限制能不能绕开」→ 不能，本仓库改不了行为，文案如实说明；(b)「参数名进 schema」→ **已经是既成事实，无需实施**，本计划改为「保证调用方能在 `mcp_help` 那一页看到正确参数名」；(c)「两层都坏时 fail-fast 并给可执行替代」→ 任务 1；(d)「在返回值里回显『你传的参数名 / 我认的参数名』」→ 任务 2 的 `unknownArgs` / `knownArgs` 字段，正是它。**无遗漏项。**

**② 占位符扫描。** 全文无「待定」「TODO」「后续实现」「类似任务 N」；每个代码步骤都带可粘贴的完整代码块；所有引用的标识符（`PLAY_FOR_CONTROL_KEYS` 4082、`isGabpMirrorName` 2793、`gabpTools` 2514、`gabpErrorResponse` 2697、`contentErrorResponse` 2705、`callGABPTool` 2747、`toolSchema` 3875、`attachUsageOnFailure` 4006、`_testHooks` 5450、`menuUniverseNames` 3369、`mcpHelpResult` 3646）都在 §0 表里给出**已复核的行号**，不存在"引用了未定义的东西"。

**③ 类型/命名一致性。** `MIRROR_ALTERNATIVE_HINTS`（任务 1）与 `GABP_MIRROR_LOCAL_ARGS`（任务 2）是两张不同职责的表，名字不重叠；`formatMirrorAlternative`（任务 1 定义、4254 调用）与 `mirrorUnknownArgs` / `unknownArgsResponse` / `mirrorDeclaredArgs` / `mirrorArgGuardEnabled`（任务 2 定义、4244 调用）签名与用法一一对应；测试里引用的 `mod.mirrorUnknownArgs` / `mod._testHooks.setGabpTools` 都在步骤 5 / 步骤 1 里落地。**无 drift。**

<!-- SECTION-ANCHOR -->
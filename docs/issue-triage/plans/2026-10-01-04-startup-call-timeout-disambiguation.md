# [04] 启动类调用超时可判定性（文案 + 默认策略）实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让调用方在调用 `start_game` / `start_quick_test` 之前就知道「该传 `waitForNotification:false`」以及「收到 `-32001` 不等于启动失败」，从而消除盲目重试。

**架构：** 本条**不是功能缺失**——吐槽板建议的「拆成两个工具，后者快速返回」早已实现为 `MCP/index.js:1082-1089` 的 `waitForNotification:false` 分支（实测 4 次调用零超时，11.9s–21.8s）。真正缺的是**文案**：`start_game` 的工具描述与参数描述既没提示这个快速返回开关，也没提示「MCP 客户端有自己的请求截止线，可能先于服务端返回就掐断连接」。因此本计划只改字符串常量与 `mcp_help` 备注，**一行控制流都不动**，并用仓库既有的 `node --test` 契约测试把文案钉死防回归。

**技术栈：** Node.js ESM、`node:test` + `node:assert/strict`、`@modelcontextprotocol/sdk` 的 `InMemoryTransport`（全部离线，不启动 RimWorld、不占用 MCP 服务器的 3000 端口）。

---

## 零、开工前必读：这个需求的根因与「不要修成什么样」

### 0.1 超时的真实来源（已逐行复核，勿凭推测改动）

| 事实 | 位置（本次已复核） |
|---|---|
| MCP **客户端**有一个独立于服务端的请求截止线；到点后由 SDK 抛 `RequestTimeout`（即 `-32001 / 'Request timed out'`） | `MCP/node_modules/@modelcontextprotocol/sdk/dist/esm/shared/protocol.js:712-713` |
| 服务端 `startGame` 的等待循环**没有服务端自限**，上界就是调用方给的 `timeout` | `MCP/index.js:1099` `while (Date.now() - startTime < timeout)` |
| `startQuickTest` 的等待循环同构 | `MCP/index.js:1595` `while (Date.now() - startTime < timeout)` |
| 循环结束后还要再跑一次 `detectGameStage()` 才返回 | `MCP/index.js:1159`（`startGame`）/ `MCP/index.js:1648`（`startQuickTest`） |
| 所以失败路径总耗时 ≈ `timeout` + 一次探测 | 由 `:1099`+`:1159` / `:1595`+`:1648` 共同决定 |

> **⚠️ 不要把任何具体毫秒数写进文案。** SDK 里确实有 `protocol.js:8` → `DEFAULT_REQUEST_TIMEOUT_MSEC = 60000`，
> 但**那是 SDK 的默认值，不是本机 harness 客户端的实际截止线**——实测 `-32001` 要 3~4 分钟才出现，
> 两者不可能同时成立。客户端实际值**不在本仓库内**，也未在任何可见配置文件里找到。
> 因此文案只说「客户端有自己的截止线，可能先于服务端返回就掐断」，**不给数字、不给推荐预算**。

⇒ **`-32001` 是客户端截止线，不是服务端业务错误。** 一旦它触发，客户端就收不到服务端在 `MCP/index.js:1160-1168` 准备好的那份结构化载荷（`state:'timeout'` / `currentStage` / `pid` / `guidance`），于是调用方拿到一个「超时 vs 失败」不可分辨的字符串 —— 这正是吐槽板 `[04]` 的核心痛点。

### 0.1.1 两种同名不同义的「超时」（文案必须写清，否则会把排查带偏）

| | 服务端 `state:'timeout'` | 客户端 `-32001` |
|---|---|---|
| 触发方 | 服务端自己判定等待超时 | MCP 客户端截止线 |
| 返回体 | **正常 JSON**：`success:false` / `state:'timeout'` / `currentStage` / `pid` / `elapsedTime` / `guidance`（`MCP/index.js:1160-1168`；`startQuickTest` 另有 `inGame` / `paused`，见 `MCP/index.js:1649-1665`） | **什么都拿不到**，只有一个错误串 |
| 信息量 | 能判定游戏到底起没起 | 完全不可判定 |

⇒ **给 `timeout` 设一个很大的值 ≠ 一定拿到 `-32001`。** 例：`timeout:30000` 时服务端约 30s 就返回正常的 `state:'timeout'` JSON，不是 `-32001`。
⇒ 因此**本计划不写任何「`timeout` 要 ≥N」的建议值**：既无证据支撑，又会把两种「超时」混为一谈。
⇒ 文案只做一件事：告诉调用方**怎么区分**（见 `MCP/index.js:1160-1168` 返回体里有 `state` 字段 ⇒ 是服务端结论；没有、只有错误串 ⇒ 是 `-32001`，去查 `get_game_status`）。

### 0.2 服务端早就能给出结构化信息 —— 不要再实现一遍

`MCP/index.js:1160-1168` 的超时分支**已经**返回：

```javascript
    return {
      success: false,
      state: 'timeout',
      message: `Timeout waiting for main menu (${timeout}ms)`,
      currentStage: finalDetected.stage,
      pid: finalDetected.gamePid || process.pid,
      elapsedTime: Date.now() - startTime,
      guidance: '游戏进程已启动；可调 get_game_status 查看当前阶段，或手动等待主菜单'
    };
```

这已经覆盖了吐槽板要求的 `{timedOut:true, processSeen:true, pid:...}`。**本计划不新增任何返回字段。**

### 0.3 超时后如何判定游戏到底起没起 —— 也已经存在

`get_game_status` 的处理器（`MCP/index.js:4384-4389`）调用 `getGameStatus()`，其返回值第一段（`MCP/index.js:1296-1299`）是：

```javascript
  return {
    running: detected.running,
    stage: detected.stage,
    game_pid: detected.gamePid,
```

⇒ 判定规则现成：**`running === true` 表示进程在，`stage` 区分 `STOPPED` / `STARTING` / `MAIN_MENU` / `IN_GAME`**（阶段判定逻辑在 `MCP/index.js:623-647`）。所以「超时 ≠ 失败」的可判定性是**文档指路**，不是新增能力。

### 0.4 `waitForNotification` 的默认值到底由谁决定（**本计划最重要的一条核实**）

默认值写在**三个地方**，但真正生效的是第 2、3 个：

1. `MCP/index.js:1839` —— JSON Schema 里的 `default: true`。**这是纯注解**：JSON Schema 规范中 `default` 不会由校验器自动填充，MCP SDK 也没有注入它（SDK 在 `protocol.js:712` 只读 `options.timeout`，与 schema 无关）。
2. `MCP/index.js:4358` —— 处理器 `startGame(args?.useSteam ?? true, args?.waitForNotification ?? true, timeout)`。**调用方不传时，这里独立决定为 `true`。**
3. `MCP/index.js:942` —— 函数签名默认值 `async function startGame(useSteam = true, waitForNotification = true, timeout = config.startTimeout || 180000)`。

**推论（决定了本计划的形态）：只改 `:1839` 的 schema 默认值是完全无效的**，调用方不传参数时 `:4358` 依然会传 `true` 进去。任何涉及默认值的改动必须同时动 `:4358` 与 `:942`。

### 0.5 决定：**不**改默认值（破坏性变更）

`:4358` 的 `?? true` 若改成 `?? false`，对所有不显式传参的既有调用方就是破坏性变更。
下面三条候选理由里**只有第 ① 条承重**，② 已被证伪、③ 只是文案。**执行者请勿把三条并列看待**——
把已被源码自我豁免的顾虑说成硬约束，会让人质疑整个结论。

#### ① 【承重·唯一的理由】`false` 分支的返回体根本没有 `state` 键

`MCP/index.js:1084-1088`（`waitForNotification:false` 的快速返回分支）返回的是：

```javascript
      return {
        success: true,
        message: `Game started (PID: ${process.pid})`,
        pid: process.pid
      };
```

而阻塞路径的成功分支 `MCP/index.js:1132-1139` 与 `MCP/index.js:1145-1152` 返回的是 `state: 'main_menu',`。

⇒ 翻默认值会让**所有不显式传参的既有调用方**（人写的脚本、别的 agent、编排器）从 `{state:'main_menu', …}` 静默降级到 `{pid}`。
任何形如 `if (r.state === 'main_menu')` 的后继判断会**恒为 false —— 不报错、不抛异常，行为静默错误**。这是不翻默认值的**唯一**硬约束。

> **同一缺口对今天的显式 `false` 调用方同样存在**：它不是默认值带来的，是 `false` 分支本来就少这个键。
> 因此本计划**必须**在文案里提醒「`false` 只回 pid、不回 `state`」（见任务 1 步骤 4 与任务 2 步骤 5），否则等于把一个未声明的契约差异推荐成推荐路径。

#### ② 【已证伪·不承重】「`ensureConnected` 时机变早」**不是**破坏性

原提法是：`MCP/index.js:4359-4365` 在成功时触发 `gabpBridge.ensureConnected()`，改默认值会让重连提前。**该顾虑不成立，三点源码反证：**

1. `MCP/index.js:4364` 是 `void` + `try/catch` ⇒ fire-and-forget，失败只打 WARN，不影响返回。
2. `MCP/index.js:4362-4363` 的注释自己就写着「RimBridgeServer 启动约 65s 延迟，**此刻可能尚未 discover 到，属正常**」⇒ 「触发得早」本来就是这段代码预期的常态。
3. `MCP/index.js:2745-2748` 明写「惰性断连自愈：**每次 GABP 工具调用先 ensureConnected**」，`callGABPTool` 开头就是 `const ready = await ensureGabpConnected();` ⇒ `:4364` 这次提前触发即便失败，也会被下一次 GABP 调用无条件补上。

⇒ **降级为「无害的时序前移」，不得再作为保留 `true` 的理由。**

#### ③ 【成立但只是文案漂移·不承重】`MCP/index.js:370` 的指引文本

`GAME_NOT_RUNNING` 的 guidance 写着「`waitForNotification=true` 会等待主菜单就绪」，翻默认值后确实会自相矛盾。**但它只是一条字符串，零风险**，属于「若将来真要翻默认值，才必须一并改的附带文案」，不构成破坏性。

⇒ **把推荐用法写进 description，把默认值原样保留。** 对既有调用方零风险，对新调用方零成本。

### 0.6 `start_quick_test` 没有 `waitForNotification` 参数

`MCP/index.js:1940-1953` 的 `start_quick_test` schema 只有 `timeout` 一个参数，**没有 `waitForNotification`**（全文仅 5 处命中：`:370`、`:942`、`:1083`、`:1836`、`:4358`，均与 quick test 无关）。它的处理器（`:4413`）是 `const timeout = args?.timeout ?? (config.timings?.quickTestTimeoutMs ?? 120000)`。

⇒ 它**永远阻塞**，文案策略与 `start_game` 不同：不能推荐 `false`（没这个开关），只能讲清「`timeout` 别设得比客户端截止线更大」以及「它不是启动游戏的前提」。

### 0.7 已核实的完整行号索引（本计划所有 `文件:行号` 的唯一依据）

| 对象 | 行号 |
|---|---|
| `startGame()` 函数签名与默认参数 | `MCP/index.js:942` |
| `waitForNotification:false` 快速返回分支 | `MCP/index.js:1082-1089` |
| 无自限的等待循环 | `MCP/index.js:1099-1156` |
| 循环后的额外一次 `detectGameStage()` | `MCP/index.js:1159` |
| 超时分支的结构化返回 | `MCP/index.js:1160-1168` |
| `getGameStatus()` 返回体开头 | `MCP/index.js:1296-1299` |
| `TOOLS` 数组起点 | `MCP/index.js:1824` |
| `start_game` 的 `description` | `MCP/index.js:1827` |
| `start_game` 的 `waitForNotification` schema | `MCP/index.js:1836-1840` |
| `start_game` 的 `timeout` schema | `MCP/index.js:1841-1844` |
| `start_quick_test` 的 `description` | `MCP/index.js:1942` |
| `start_quick_test` 的 `timeout` schema | `MCP/index.js:1946-1950` |
| `TOOL_PRECONDITION`（含 `start_game`） | `MCP/index.js:2922-2927` |
| `TOOL_USAGE_NOTES`（无 start_game / start_quick_test 条目） | `MCP/index.js:2930` 起 |
| `start_game` 分发（含 `?? true` 与 ensureConnected） | `MCP/index.js:4356-4369` |
| `get_game_status` 分发 | `MCP/index.js:4384-4389` |
| `start_quick_test` 分发（含 `?? 120000`） | `MCP/index.js:4413-4414` |
| 测试钩子导出（`_testHooks`） | `MCP/index.js:5450-5459` |
| `npm test` 脚本 | `MCP/package.json:9` → `node --test "test/*.test.js"` |
| README 工具表 `start_game` 行 | `README.md:66` |
| README 工具表 `start_quick_test` 行 | `README.md:70` |

> **注意：** `TOOLS` 常量本身**没有**被导出（见 `MCP/index.js:5416-5443` 的导出清单），所以测试不能 `import { TOOLS }`。必须走 `_testHooks.createServer()` + SDK `Client.listTools()` 的既有路子，见任务 1。
>
> **`start_game` / `start_quick_test` / `get_game_status` 在 `MCP/toolConfig.json` 里都是 `true`**，即三者都出现在 `tools/list` 中 —— 改了 description 就一定会到达模型，这条文案链路是通的。

---

## 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/test/startupTimeoutContract.test.js` | **创建** | 超时文案契约测试：断言两个启动工具的 `tools/list` 描述里含有「`false` 快速返回」「`-32001` 不是失败」「用 `get_game_status` 判定」三条指引，以及 `mcp_help` 的 `notes` 不再为空。离线运行，防文案回退。 |
| `MCP/index.js:1827` | **修改** | `start_game` 的顶层描述：给出「优先 `waitForNotification:false` + 轮询 `get_game_status`」的推荐路径。 |
| `MCP/index.js:1836-1840` | **修改** | `waitForNotification` 参数描述：写明 `false` 立即返回、`true` 可能被客户端截止线切断。 |
| `MCP/index.js:1841-1844` | **修改** | `timeout` 参数描述：写明不要设得比客户端截止线更大。 |
| `MCP/index.js:1942` | **修改** | `start_quick_test` 顶层描述：写明它**不是**启动游戏的方式，且没有快速返回开关。 |
| `MCP/index.js:1946-1950` | **修改** | `start_quick_test` 的 `timeout` 描述：同上，并指向 `get_game_status`。 |
| `MCP/index.js:2930` 附近 | **修改** | 在 `TOOL_USAGE_NOTES` 里补 `start_game` / `start_quick_test` 两条 `notes`（当前这两个工具完全没有 notes，是最容易踩坑却最没提示的一处）。 |
| `MCP/index.js:4358` / `:942` | **仅加注释** | 在真正决定默认值的两行旁加注释，写明「此处 `?? true` 才是生效默认值，改 schema 无效；改默认为破坏性变更，故不改」。**不改行为。** |
| `README.md:66` / `README.md:70` | **修改** | 工具表两行各加一句超时语义提示，给人读的版本。 |

**明确不在本计划范围内：** 不改 `MCP/index.js:1099` 的等待循环；不新增返回字段；不改 `toolConfig.json`；不新增 `waitForNotification` 到 `start_quick_test`；不动任何 `Source/` 下的 C# 源码。

---

### 任务 1：建立超时文案契约测试，并修好 `start_game` 的三处描述

**文件：**
- 创建：`MCP/test/startupTimeoutContract.test.js`
- 修改：`MCP/index.js:1827`、`MCP/index.js:1836-1840`、`MCP/index.js:1841-1844`

- [ ] **步骤 1：编写失败的契约测试（先只覆盖 `start_game`）**

创建 `MCP/test/startupTimeoutContract.test.js`。开头的守卫环境变量与 `withClient` 辅助函数**必须照抄** `MCP/test/mapMarker.test.js:1-25` 的既有写法（守卫缺失会导致测试进程真的去监听 3000 端口）：

```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
const { InMemoryTransport } = await import('@modelcontextprotocol/sdk/inMemory.js');

async function withClient(fn) {
  const server = mod._testHooks.createServer();
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'test-client', version: '1.0.0' });
  try {
    await Promise.all([server.connect(serverT), client.connect(clientT)]);
    return await fn(client);
  } finally {
    await client.close().catch(() => {});
    await server.close().catch(() => {});
  }
}

/** 取 tools/list 里的某个工具定义（走真实对外可见性，不 import 未导出的 TOOLS） */
async function listTool(name) {
  const listed = await withClient((client) => client.listTools());
  const t = listed.tools.find((x) => x.name === name);
  assert.ok(t, `${name} 应出现在 tools/list（toolConfig.json 已设为 true）`);
  return t;
}

/** mcp_help 的返回值可能是 string 也可能是 content 数组，两种都吃 */
async function helpJson(args) {
  const v = mod.mcpHelpResult(args);
  const raw = typeof v === 'string'
    ? v
    : (v.content || []).map((c) => c.text || '').join('');
  return JSON.parse(raw);
}

// ===========================================================================
// 启动类工具的超时文案契约（吐槽板 [04]）
// 背景：`-32001 Request timed out` 是 MCP 客户端的请求截止线（SDK protocol.js:8/:712-713），
// 不是服务端失败信号。服务端其实早已能给出结构化结果（MCP/index.js:1160-1168），
// 但描述里一个字都没提，调用方只能盲重试。本文件把三条指引钉死。
// ===========================================================================

test('start_game 顶层描述：给出推荐的快速返回路径', async () => {
  const t = await listTool('start_game');
  assert.match(t.description, /waitForNotification/);
  assert.match(t.description, /get_game_status/);
});

test('start_game.waitForNotification 描述：写明 false 立即返回、true 会被客户端截止线切断', async () => {
  const t = await listTool('start_game');
  const d = t.inputSchema.properties.waitForNotification.description;
  assert.match(d, /false/);
  assert.match(d, /立即返回/);
  assert.match(d, /32001/);
  assert.match(d, /不代表启动失败/);
});

test('start_game.waitForNotification 描述：点明 false 分支不返回 state（契约差异，勿删）', async () => {
  const t = await listTool('start_game');
  const d = t.inputSchema.properties.waitForNotification.description;
  assert.match(d, /NO `state` key|没有 `state`|无 `state`/,
    'false 分支（MCP/index.js:1084-1088）返回体里根本没有 state，必须在文案里告知');
});

test('start_game 描述与参数描述都不写死客户端截止线的毫秒数（该值不在本仓库内）', async () => {
  const t = await listTool('start_game');
  const all = [t.description, t.inputSchema.properties.waitForNotification.description,
    t.inputSchema.properties.timeout.description].join('\n');
  assert.ok(!/60000|60\s?s\b|SDK default/i.test(all),
    'SDK 的 DEFAULT_REQUEST_TIMEOUT_MSEC=60000 只是 SDK 默认值，不是本机 harness 的实际截止线，禁止写进文案');
});

test('start_game.timeout 描述：提醒不要超过客户端请求截止线，且区分两种「超时」', async () => {
  const t = await listTool('start_game');
  const d = t.inputSchema.properties.timeout.description;
  assert.match(d, /客户端/);
  assert.match(d, /state/,
    '必须点明服务端 state:\'timeout\' 与客户端 -32001 是两回事（见计划 §0.1.1）');
});

test('start_game 的 waitForNotification 默认值仍是 true（本次只改文案不改行为）', async () => {
  const t = await listTool('start_game');
  assert.equal(t.inputSchema.properties.waitForNotification.default, true,
    '默认值语义变更属于破坏性变更，本计划不触碰（承重理由见计划 §0.5 第 ① 条）');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/startupTimeoutContract.test.js`

预期：本文件共 6 个 test，其中**前 5 个 FAIL**，报错形如
`AssertionError: The input did not match the regular expression /waitForNotification/`
（当前 `start_game` 的 description 是英文的 `'Start RimWorld game and wait for main menu notification from mod'`，不含任何指引）；
**最后一个 PASS** —— 它守的是「行为没被动过」，现在就是 `true`。

- [ ] **步骤 3：修改 `start_game` 的顶层描述（`MCP/index.js:1827`）**

把这一行：

```javascript
    description: 'Start RimWorld game and wait for main menu notification from mod',
```

替换为：

```javascript
    description: 'Start RimWorld game. Recommended for agents: pass waitForNotification:false to return as soon as the process exists (you get pid), then poll get_game_status until stage is MAIN_MENU. Only pass waitForNotification:true when you really need to block until the main menu.',
```

- [ ] **步骤 4：修改 `waitForNotification` 参数描述（`MCP/index.js:1836-1840`）**

把这一段：

```javascript
        waitForNotification: {
          type: 'boolean',
          description: 'Wait for main menu notification from mod (default true)',
          default: true
        },
```

替换为：

```javascript
        waitForNotification: {
          type: 'boolean',
          description: 'true (default) = block until the main menu is ready, up to `timeout` ms. false = return immediately once the process exists (measured ~12-22s) with pid ONLY - the fast path returns {success, message, pid} and has NO `state` key, so poll get_game_status for stage instead of reading r.state. Warning for true: the MCP client has its own request deadline that can fire BEFORE the server replies; you then get error -32001 "Request timed out" and lose the server result. That does NOT mean startup failed - poll get_game_status and read running/stage/game_pid.',
          default: true
        },
```

> 文案里的 `~12-22s measured` 来自本机历史日志观测，不是承诺值；它只用来传达「快返回与阻塞返回差一个数量级」，不要改成更精确的数字。
>
> **「NO `state` key」这一句是承重的**（见 §0.5 第 ① 条）：它既是「不翻默认值」的核心依据，
> 也是显式传 `false` 的调用方必须知道的契约差异。**不要为了简洁把它删掉。**
> 同理，**不要在这段文案里写客户端截止线的毫秒数**（见 §0.1 的警示框）。

- [ ] **步骤 5：修改 `timeout` 参数描述（`MCP/index.js:1841-1844`）**

把这一段：

```javascript
        timeout: {
          type: 'number',
          description: 'Timeout in ms for waiting notification (default from config.startTimeout, fallback 180000)'
        }
```

替换为：

```javascript
        timeout: {
          type: 'number',
          description: 'Server-side wait budget in ms (default from config.startTimeout, fallback 180000). Only meaningful when waitForNotification is true. On budget exhaustion the server returns a normal JSON with state:"timeout" plus currentStage/pid/elapsedTime/guidance - that is a verdict, use it. A large value does NOT guarantee you will get it: the MCP client has its own request deadline that can fire first, and then you only get -32001 "Request timed out" and lose the result entirely. Only make it very large if you can accept that outcome.'
        }
```

> **这里刻意不给推荐毫秒数。** 给 `timeout` 设一个很大的值 **≠** 一定拿到 `-32001`——例：`timeout:30000` 时服务端约 30s 就正常返回 `state:'timeout'` JSON。
> 「本工具的 `timeout` 要 ≥ N」这类结论没有证据支撑（SDK 的 `DEFAULT_REQUEST_TIMEOUT_MSEC=60000` 只是 SDK 默认值，
> 不是本机 harness 的实际截止线，实测 `-32001` 要 3~4 分钟才出现），**禁止写进文案**。

- [ ] **步骤 6：运行测试验证通过**

运行：`cd MCP && node --test test/startupTimeoutContract.test.js`

预期：6 pass 0 fail。

- [ ] **步骤 7：Commit**

```bash
git add MCP/index.js MCP/test/startupTimeoutContract.test.js
git commit -m "docs(mcp): 补齐 start_game 超时文案，指明 waitForNotification:false 快速返回路径"
```

---

### 任务 2：让 `start_quick_test` 与 `get_game_status` 把「超时≠失败」讲清楚

**文件：**
- 修改：`MCP/test/startupTimeoutContract.test.js`（追加断言）
- 修改：`MCP/index.js:1942`、`MCP/index.js:1946-1950`、`MCP/index.js:1864`、`MCP/index.js:2930` 附近
- 修改：`MCP/index.js:4358`、`MCP/index.js:942`（仅加注释）

- [ ] **步骤 1：追加会失败的测试**

在 `MCP/test/startupTimeoutContract.test.js` 末尾追加：

```javascript
test('start_quick_test 描述：说明它不是启动游戏的方式，且没有快速返回开关', async () => {
  const t = await listTool('start_quick_test');
  assert.match(t.description, /start_game/);
  assert.match(t.description, /waitForNotification/);
});

test('start_quick_test.timeout 描述：区分两种「超时」，且不给推荐毫秒数', async () => {
  const t = await listTool('start_quick_test');
  const d = t.inputSchema.properties.timeout.description;
  assert.match(d, /客户端/);
  assert.match(d, /get_game_status/);
  assert.match(d, /state/, '必须点明服务端 state:"timeout" 与客户端 -32001 是两回事（见计划 §0.1.1）');
  assert.ok(!/240000|300000|≥\s*\d{5,}/.test(d),
    '「timeout 要 ≥N」没有证据支撑，禁止写进文案');
});

test('get_game_status 描述：点明它就是超时后判定成功与否的权威来源', async () => {
  const t = await listTool('get_game_status');
  assert.match(t.description, /start_game|start_quick_test/);
});

test('mcp_help：start_game 的 notes 含 false 分支无 state 与两种超时的区分', async () => {
  const a = await helpJson({ tool: 'start_game' });
  assert.match(a.notes, /32001/);
  assert.match(a.notes, /state/, '必须同时讲 state:"timeout"（服务端结论）与 -32001（客户端掐断）');
  assert.match(a.notes, /没有 state|无 state|NO `state`/,
    'false 快速返回体没有 state（MCP/index.js:1084-1088），必须告知');
});

test('mcp_help：start_quick_test 的 notes 指向 start_game 且区分两种超时', async () => {
  const b = await helpJson({ tool: 'start_quick_test' });
  assert.match(b.notes, /start_game/);
  assert.match(b.notes, /state/);
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/startupTimeoutContract.test.js`

预期：已有 6 个仍 pass，新增 5 个 FAIL（共 11 个 test）。其中两条 `mcp_help` 会因为 `payload.notes` 为 `null` 或不含 `/32001/` 而失败 —— `TOOL_USAGE_NOTES`（`MCP/index.js:2930` 起）当前**根本没有** `start_game` / `start_quick_test` 条目。

- [ ] **步骤 3：修改 `start_quick_test` 顶层描述（`MCP/index.js:1942`）**

把这一行：

```javascript
    description: 'Trigger quick test in RimWorld and wait for map loading completion（主菜单即可用：只需 UESDdebuger 模组的游戏内 HTTP 服务，不需要 GABP/RimBridgeServer、RIMAPI 或 UnityExplorer 就绪）',
```

替换为：

```javascript
    description: 'Trigger quick test in RimWorld and wait for map loading completion（主菜单即可用：只需 UESDdebuger 模组的游戏内 HTTP 服务，不需要 GABP/RimBridgeServer、RIMAPI 或 UnityExplorer 就绪）。注意：本工具不启动游戏，游戏没跑请先 start_game；本工具没有 waitForNotification 开关（永远阻塞等待），启动场景请勿用本工具替代 start_game',
```

- [ ] **步骤 4：修改 `start_quick_test` 的 `timeout` 描述（`MCP/index.js:1946-1950`）**

把这一段：

```javascript
        timeout: {
          type: 'number',
          description: 'Timeout in ms for waiting map loaded notification (default config.timings.quickTestTimeoutMs 120000)',
          default: 120000
        }
```

替换为：

```javascript
        timeout: {
          type: 'number',
          description: 'Server-side wait budget in ms for the map-loaded notification (default config.timings.quickTestTimeoutMs 120000). On budget exhaustion the server returns a normal JSON with state:"timeout" plus currentStage/pid/elapsedTime/inGame/paused/guidance - that is a verdict (paused:true means the game is in but world tick is frozen by pause), use it. A large value does NOT guarantee you will get it: the MCP client has its own request deadline that can fire first, and then you only get -32001 "Request timed out" and lose the result. In that case call get_game_status and read running/stage/in_game. There is no verified recommended value for this parameter - do not guess one from "bigger is safer".',
          default: 120000
        }
```

- [ ] **步骤 5：在 `TOOL_USAGE_NOTES` 里补两条 notes**

在 `MCP/index.js:2930` 的 `const TOOL_USAGE_NOTES = {` 之后、`get_colonist_detailed` 那一行（`:2931`）之前，插入两行：

```javascript
  start_game: { example: '{ "waitForNotification": false }', notes: 'Agent 编排首选 waitForNotification:false —— 进程出现即返回（实测 12-22s）。注意该快速返回体只有 {success, message, pid}，**没有 state 字段**（阻塞路径才有 state:"main_menu"），所以不要读 r.state，改用 pid + get_game_status 轮询 stage 到 MAIN_MENU。两种「超时」要分清：① 拿到 JSON 且 state=="timeout" = 服务端判定等待超时，返回体带 currentStage/pid/elapsedTime/guidance，直接读它即可判定游戏状态；② 拿到 -32001 "Request timed out" = MCP 客户端截止线先掐断，什么都拿不到，此时必须调 get_game_status 看 running/stage/game_pid，且**这不代表启动失败**。默认值保持 true 是有意为之（改默认为破坏性变更），需要快速返回请显式传 false' },
  start_quick_test: { example: '{ "timeout": 120000 }', notes: '只负责触发 DevQuickTest 进图，不负责启动游戏；游戏没跑请先 start_game。本工具无 waitForNotification，永远阻塞等待。同样要分清两种「超时」：state=="timeout"（服务端判定，带 currentStage/pid/inGame/paused，直接读它；paused:true 表示游戏在但世界 tick 被暂停，解暂停即可）vs -32001（客户端截止线掐断，什么都拿不到，改用 get_game_status 的 running/stage/in_game 判定）。本工具的 timeout 没有已验证的推荐值，不要凭「timeout 越大越安全」去猜' },
```

- [ ] **步骤 6：给 `get_game_status` 的描述补上判定职责（`MCP/index.js:1864`）**

把这一行：

```javascript
    description: '获取游戏运行状态与阶段（多源探测：进程/RIMAPI/UE，无需前置）',
```

替换为：

```javascript
    description: '获取游戏运行状态与阶段（多源探测：进程/RIMAPI/UE，无需前置）。start_game / start_quick_test 返回 -32001 "Request timed out" 后，用本工具判定结果：running=true 表示进程在，stage 取值 STOPPED/STARTING/MAIN_MENU/IN_GAME',
```

- [ ] **步骤 7：在真正决定默认值的两行加「不要改」的注释**

`MCP/index.js:942`，把函数签名改为（**只加注释，不改默认值**）：

```javascript
// 注意：下面三个默认参数是「最后一道」默认值。工具处理器 MCP/index.js:4358 会用
// `args?.waitForNotification ?? true` 独立决定一遍，schema 里的 default:true 并不生效
// （JSON Schema 的 default 只是注解，MCP SDK 不会填充）。
// 因此「改 schema 默认值」是无效改动；真要改默认值必须同时动 :4358 与这里。
// 本次不改默认值的唯一承重理由：waitForNotification:false 的返回体（MCP/index.js:1084-1088）
// 只有 {success, message, pid}，**没有 state 键**；而阻塞成功分支（:1132-1139 / :1145-1152）返回
// state:'main_menu'。翻了默认值，既有调用方的 r.state === 'main_menu' 判断会恒为 false 且不报错。
async function startGame(useSteam = true, waitForNotification = true, timeout = config.startTimeout || 180000) {
```

`MCP/index.js:4358`，把分发那两行改为（**只加注释，不改行为**）：

```javascript
      // `?? true` 才是真正生效的默认值：调用方不传 waitForNotification 时阻塞等待到主菜单。
      // 想改成快速返回的默认值，必须同时改 :942 的签名默认值和 :1839 的 schema 注解，
      // 并接受这是破坏性变更：false 分支（:1084-1088）返回体没有 state 键，既有调用方的
      // r.state === 'main_menu' 会恒为 false。当前刻意保留 true，靠工具描述引导显式传 false。
      const result = await startGame(args?.useSteam ?? true, args?.waitForNotification ?? true, timeout);
```

- [ ] **步骤 8：运行测试验证通过**

运行：`cd MCP && node --test test/startupTimeoutContract.test.js`

预期：11 pass 0 fail。

- [ ] **步骤 9：Commit**

```bash
git add MCP/index.js MCP/test/startupTimeoutContract.test.js
git commit -m "docs(mcp): start_quick_test/get_game_status 补超时文案与 notes，注明默认值不改的理由"
```

---

### 任务 3：README 同步 + 全量回归

**文件：**
- 修改：`README.md:66`、`README.md:70`

- [ ] **步骤 1：修改 README 的 `start_game` 工具表行（`README.md:66`）**

把这一行：

```markdown
| `start_game` / `stop_game` | 通过 Steam 或直接启动 / 正常或强制关闭游戏 |
```

替换为：

```markdown
| `start_game` / `stop_game` | 通过 Steam 或直接启动 / 正常或强制关闭游戏。**agent 编排请给 `start_game` 传 `waitForNotification:false`**：进程出现即返回 pid（实测 12-22s），再用 `get_game_status` 轮询到 `MAIN_MENU`。注意该快速返回体**没有 `state` 字段**（阻塞模式才有 `state:"main_menu"`），别读 `r.state`。默认的阻塞模式（`true`）最多等 `timeout` 毫秒；两种「超时」分清：拿到 JSON 且 `state=="timeout"` 就是服务端的结论（带 `currentStage`/`pid`，可直接用），拿到 `-32001` 才是客户端截止线掐断（什么都拿不到），此时用 `get_game_status` 的 `running`/`stage` 判定，**不要盲目重试** |
```

- [ ] **步骤 2：修改 README 的 `start_quick_test` 工具表行（`README.md:70`）**

在该行**末尾**（`...token 过期」` 之后）追加这句：

```markdown
。本工具不启动游戏，也不提供 `waitForNotification` 快速返回开关；两种「超时」同样分清：JSON 里 `state=="timeout"` 是服务端结论（带 `inGame`/`paused`，可直接读），`-32001` 才是客户端掐断，此时改用 `get_game_status` 的 `running`/`stage`/`in_game` 判定。`timeout` 没有已验证的推荐值，不要凭「设大一点更安全」去猜
```

- [ ] **步骤 3：全量回归**

运行：`cd MCP && npm test`

预期：全部测试通过，新增的 `startupTimeoutContract.test.js` 贡献 11 个 pass，且**没有任何既有测试由 pass 变 fail**。若既有测试失败，说明步骤 3/4/5/6 改动了别的工具描述或 `mcp_help` 结构已被别处依赖 —— 回看 `git diff MCP/index.js`，不要改测试来迁就。

- [ ] **步骤 4：人工确认 `-32001` 端到端提示确实到达模型（不启动游戏）**

运行：`cd MCP && node --test test/startupTimeoutContract.test.js`

预期：11 pass。这一步已经在 `tools/list` 层面证明三份文案（`tools/list` 描述、`mcp_help` notes、README）都就位；**不要为此真的去启动 RimWorld 做验证** —— 超时行为依赖客户端配置，无法在本仓库内复现。

- [ ] **步骤 5：Commit**

```bash
git add README.md
git commit -m "docs(readme): 工具表补 start_game 快速返回用法与超时判定说明"
```

---

## 验收清单

执行完三个任务后，逐条确认：

- [ ] `node --test "test/*.test.js"`（即 `npm test`）全绿，无既有测试回归。
- [ ] `start_game` 的 `tools/list` 描述含 `waitForNotification:false` + `get_game_status` 两条指引。
- [ ] `waitForNotification` 参数描述含 `立即返回`、`-32001`、`不代表启动失败`，**且含「false 分支无 `state`」这一承重契约差异**。
- [ ] `start_game` 与 `start_quick_test` 的 `timeout` 描述都点明客户端截止线**并区分两种「超时」**（服务端 `state:'timeout'` vs 客户端 `-32001`）。
- [ ] **全计划任何文案里都没有出现客户端截止线的毫秒数**，也没有出现「`timeout` 要 ≥N」的建议值（`start_quick_test.timeout` 有一条断言在守这条）。
- [ ] `mcp_help` 的 `start_game` / `start_quick_test` 两条 `notes` 均非空、含超时指引，且 `start_game` 的 notes 含「无 `state`」与两种超时的区分。
- [ ] `get_game_status` 描述点明它是超时后的判定来源。
- [ ] `waitForNotification` 的生效默认值仍为 `true`（`MCP/index.js:942` 与 `:4358` 只加了注释，行为未变）。
- [ ] `MCP/index.js:1099` 的等待循环、`MCP/toolConfig.json`、`Source/` 下任何 C# 文件均未被改动（`git diff --stat` 可确认）。

---

## 附：本计划的修订记录

- **2026-10-01 初版**：3 个任务，只改文案。
- **2026-10-01 修订（并入吐槽板 [30] 的裁决 `triage/30-start-game-waitfornotification-default-false.md`）**：
  - §0.5 的三条「破坏性」重新定权重 —— **只有「`false` 分支丢 `state`」承重**；「`ensureConnected` 时机」经 `MCP/index.js:4364`（`void`+`try/catch`）、`:4362-4363`（注释自称「属正常」）、`:2745-2748`（每次 GABP 调用先重连兜底）三重证伪，降级为「无害的时序前移」；`:370` 降级为文案漂移。
  - **不采纳** [30] 的「`start_quick_test` 的 `timeout` 要 ≥240000」：与 `MCP/index.js:1595` + `:1648-1665` 矛盾 —— 传 30000 时服务端约 30s 就返回正常 `state:'timeout'` JSON，不是 `-32001`；且「把 `timeout` 设大」与「拿到 `-32001`」无必然联系。新增 §0.1.1 把两种「超时」分开写，并加断言禁止该数值进文案。
  - **移除**「SDK 默认 60000ms」这一数值表述：常量 `protocol.js:8` 属实，但本机实际截止线实测为 3~4 分钟量级，该数值当根因讲站不住（结论不受影响，仅措辞）。
  - 采纳 [30] 的两处孤儿项：`false` 分支无 `state` 的契约告知、两种超时的区分。

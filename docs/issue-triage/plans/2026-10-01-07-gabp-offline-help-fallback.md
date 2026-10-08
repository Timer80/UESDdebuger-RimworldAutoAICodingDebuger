# GABP 离线时 mcp_help 镜像工具可发现性 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

> ## ⚠️ 本计划修的是**文案 / 可发现性缺陷**，不是超时问题
> 吐槽板 `docs/mcp-吐槽板.md` 的 `[07]` 条目（444-462 行）里写「GABP 空闲期间调用 `rimworld.*` 是**超时**、不是明确报错」——**这一句已被证伪，不要照着它改代码**。`MCP/index.js:4274-4280` 在把调用转发出去**之前**就短路 fail-fast，实测 3 ms 返回结构化错误。日志里那些 30 s / 60 s 的超时发生在 GABP **已经连上之后**，是第三方 RimBridgeServer 假死，**另一个根因**，项目已有"连续 3 次超时判定假死并强制重连"的缓解（`MCP/logs/mcp-20261001-093829.log:405`、`:408`、`:411`）。本计划**不碰**超时。
> 本计划真正要修的残留缺陷是：**本项目自己的文案把调用方指向一条死路。** 详见 §0.2。

**目标：** 让 `mcp_help{tool:"<某个 rimworld.* 镜像名>"}` 在 GABP 未连接时返回一份**离线占位说明**（含 `precondition` / `availability` / 归因），而不是笼统的「未知工具」——使 `agg_list_tools` 里「对应能力说明见 mcp_help tool=\<名字\> 的 precondition」这句指路真正可达。

**架构：** 在 `MCP/index.js` 的可用性归因区（`gabpMirrorUnavailable` 之后）加两个函数：一个**纯函数** `buildOfflineMirrorHelpDetail(name, input)` 负责拼装占位详情（所有状态字段由入参注入，与 `buildCapabilityReport` 同一套路，便于单测覆盖每种状态组合），一个薄封装 `offlineMirrorHelpDetail(name)` 负责从模块当前状态取数。把 `mcpHelpResult` 的「未命中」分支从"一律报未知工具"改成三路分流：已知且未注册的镜像 → 离线占位详情；已移除/改名的镜像 → 原 `unknownToolText`（`GABP_MIRROR_REMOVED`）；真·不存在的名字 → 原 `unknownToolText`（`UNKNOWN_TOOL`）。另外把"去 mcp_help 查"这句指路提成单一常量 `MCP_HELP_TOOL_POINTER`，让 `agg_list_tools` 与镜像 fail-fast 共用，消除两处文案各自漂移。**不改任何 C#，不缓存镜像 schema，不重编译模组。**

**技术栈：** Node.js ESM（`MCP/package.json` 的 `"type": "module"`）、`node:test` + `node:assert/strict`（项目现有测试栈，`MCP/package.json:9`）。

---

## 0. 给零上下文执行者的背景（不要跳过）

### 0.1 这条issue 的来龙去脉（一句话版）

`UESDdebuger` 的 MCP 服务器在 GABP（RimBridgeServer 桥接）连上之前，工具表里**没有** `rimworld.*` / `rimbridge.*` 这些镜像工具的名字。维护者在吐槽板里记了一条「GABP 静默 idle 导致调用方不知道哪些工具不可用」。上游裁决（`docs/issue-triage/triage/07-gabp-idle-no-fail-fast.md`）把「超时」证伪、把严重度从 🟠 降到 🟡，判定剩下的**唯一**残留缺陷是：`mcp_help` 对离线镜像返回「未知工具」，而项目自己的文案正叫调用方去问 `mcp_help`。本计划就是那份裁决的落地。

### 0.2 缺陷链路（逐行，均为本人复核过的行号）

| 环节 | 位置 | 事实 |
|---|---|---|
| 断连时镜像清单被清空 | `MCP/index.js:2514` | `let gabpTools = [];`（注释写明"仅 isConnected() 时非空"） |
| 断连事件清空清单 | `MCP/index.js:2649` | `gabpTools = []; // 断连后镜像清单清空` |
| 工具条目来源不含离线镜像 | `MCP/index.js:3352-3359` | `allToolEntries()` = `[...TOOLS, ...AGG_META_TOOLS, ...gabpTools]` + mono 占位；离线时 `gabpTools` 为空 |
| 离线镜像仍算"已知名字" | `MCP/index.js:3429-3435` | `isKnownGabpMirrorName(name)`：名字在 `toolConfig.tools` 或 `TOOL_SUBGROUP` 里就算已知，与是否已注册无关 |
| 归因函数已存在且正确 | `MCP/index.js:3567-3594` | `gabpMirrorUnavailable(name)` 返回 `GABP_MIRROR_UNAVAILABLE` / `GABP_MIRROR_REMOVED` / `null` |
| 错误形态的通用渲染 | `MCP/index.js:3624-3643` | `unknownToolText(target)` **已经**正确处理 GABP 镜像（先问 `gabpMirrorUnavailable`） |
| ❌ **缺陷点** | `MCP/index.js:3653-3656` | `mcpHelpResult` 的 `!meta` 分支**自己手写**了一份「未知工具」文案，**没有**复用 `unknownToolText` |
| ❌ **矛盾点** | `MCP/index.js:3611` | `aggListToolsText` 的缺席提示写着「对应能力说明见 mcp_help tool=\<名字\> 的 precondition」——离线时这句指的就是 3653-3656 那条死路 |
| 三处文案都要跟着改 | `MCP/index.js:3611`、`:4284` | fail-fast 响应里还有一处指向 `mcp_help path:"bridge"` |

**为什么这是"错误归因"而不只是"体验糙"：** 名字完全正确、只是提供方没连上，调用方（人或 AI）却收到「未知工具: rimworld.pause_game」。它唯一合理的推断是"我名字写错了"，于是开始猜别的名字、反复重试——而正确反应是"等 GABP 连上"。这会持续烧 token。

**为什么不是 🔴：** 不会产生假成功 / 假阴性。离线调用 `rimworld.*` 返回的是结构化错误（`get_game_status` 的 `capabilities[].usable` 也是真值），自动化不会因此得出相反结论。信息另外两条路也拿得到（`agg_list_tools` 会枚举缺席镜像、`mcp_help path:"game_control/game/state"` 的叶子列表会标 `[当前不可用：GABP 未连接(state=idle)]`，见 `MCP/index.js:3464-3469` 与 `:3281-3287`）。所以是 🟡。

### 0.3 吐槽板里的「超时」为什么是错的（**别改这里**）

- 源码：`MCP/index.js:4274-4280`，GABP 路由分支里在 `gabpBridge.callTool` 之前就 `return gabpErrorResponse(...)`。
- `MCP/gabpClient.js:694-695` 的 `isConnected()` 就是 `this.state === 'connected'`，而 `state` 初值就是 `'idle'`（`MCP/gabpClient.js:112`）→ 空闲态必然落进这个分支。
- 日志实测（`MCP/logs/mcp-20261001-093829.log`）：第 4 行 `GABP 桥接初始化完成（state=idle，惰性重连模式）`；第 938-939 行刚 `GABP 连接断开` / `GABP 断开（reason=connection lost）`；第 **961** 行紧接着就是 `tools/call agg_call_tool args={tool=rimworld.execute_debug_action, ...} → **3ms** error(GABP_MIRROR_UNAVAILABLE)`。3 ms，是设计好的快速失败。
- 日志里 30 s / 60 s 的超时在第 405/408/411/420/432/435/979/983/1010 行，全部发生在 GABP **已连接之后**（第 371-374 行刚握手成功、同步 125 个工具、并入 121 个），是 RimBridgeServer 在游戏线程执行 DebugAction 时假死。项目已有缓解（第 411 行 `GABP 连续 3 次 tools/call 超时，判定 RimBridge 假死，强制重连`）。**不要把它和 idle 混成一条计划。**

### 0.4 「GABP 离线」在测试里如何构造（**本条最容易翻车的地方，先读完**）

**前提铁律：不启游戏、不连真实 RimBridgeServer、不占用并行会话树正在用的游戏。** 本计划的所有测试都靠下面三条路径构造离线，**零 IO**。

**路径 ①：天然离线（默认形态）。**
测试文件在 `import '../index.js'` **之前**设 `process.env.UESD_MCP_NO_START = '1'`。这个守卫让 `MCP/index.js:5406-5413` 跳过 `main()`，因此 `startGABPBridge()` 从不执行 → 模块级 `gabpBridge` 恒为 `null`（`MCP/index.js:2513`）、`gabpTools` 恒为 `[]`（`:2514`）。
同一前提已经被 `MCP/test/toolchainFixes.test.js:52` 写进注释：「测试进程不连游戏 → GABP 必然未连接」。

> **陷阱：路径 ① 复刻不出真机的 `state=idle`。**
> `toolAvailability`（`MCP/index.js:3442-3448`）和 `gabpMirrorUnavailable`（`:3580-3581`）都是 `const st = gabpBridge ? gabpBridge.state : 'disabled'`。桥为 `null` 时 reason 会变成 `GABP 未连接(state=disabled)`，而真机上 `startGABPBridge()` 会先建出一个 `state='idle'` 的桥（GabpClient 初值），报的是 `state=idle`。**要断言 idle 形态必须走路径 ②。**

**路径 ②：注入假桥接（复刻真机的两种真实形态）。**
`mod._testHooks.setGabpBridge(bridge)`（`MCP/index.js:5453`）只替换一个模块变量，**不做任何 IO、不发任何网络请求**。传 `{ state: 'idle', isConnected: () => false }` 复刻主菜单/加载中的 idle；传 `{ state: 'connected', isConnected: () => true }` 复刻"已连上但该工具没注册"。
`MCP/test/` 下已有测试在用这个钩子（`toolchainFixes.test.js` 用 `_testHooks.resetDpaState` / `createServer`）。
**必须 `finally` 里 `setGabpBridge(null)` 复位**，否则会污染同文件后续用例（`node:test` 默认同文件串行，复位即可保证隔离）。另外 `node --test` 每个测试文件跑在独立子进程里，跨文件不会互相污染。

**路径 ③：纯函数注入（覆盖状态组合，不碰桥接）。**
本计划新增的 `buildOfflineMirrorHelpDetail(name, input)` 刻意做成纯函数：`category` / `enabled` / `reason` / `precondition` / `errorCode` / `message` / `guidance` / `notes` 全部从 `input` 取，函数内部**不读任何模块状态**。这条与既有的 `buildCapabilityReport(input)`（`MCP/index.js:3517`，注释里明写"纯函数，便于单测（输入可注入）"）是同一套路，也让"空入参"这种边界能被直接断言。

### 0.5 测试基础设施（用项目真实命令，不臆造）

| 项 | 值 | 依据 |
|---|---|---|
| 测试框架 | `node:test` + `node:assert/strict` | `MCP/test/toolCountConsistency.test.js:1-2` |
| 全量 | `cd MCP && npm test`（= `node --test "test/*.test.js"`） | `MCP/package.json:9` |
| 单文件 | `cd MCP && node --test test/gabpOfflineHelp.test.js` | 同上 |
| 导入守卫 | `process.env.UESD_MCP_NO_START = '1'` 必须在 `import '../index.js'` **之前** | `MCP/test/toolCountConsistency.test.js:4-6`、`toolchainFixes.test.js:7-9` |
| 现有测试文件 | `timeoutPolicy / runner / index.integration / quickTest / toolchainFixes / hotreloadConcealment / mapMarker / toolCountConsistency / journal`（共 9 个 `.test.js`） | `MCP/test/` |
| 必看的现有写法 | `MCP/test/toolchainFixes.test.js`（§3 归因三态、§4 capabilities、§5 DPA 状态机） | 本计划与它同构 |

**回归风险提示：** 改 `mcpHelpResult` 的 `!meta` 分支会影响 `MCP/test/toolchainFixes.test.js:82-86`（断言真·不存在的名字经 `agg_call_tool` 仍是 `UNKNOWN_TOOL`）——**那条走的是 `unknownToolText`（`MCP/index.js:5098`），不是 `mcpHelpResult`，不受本计划影响**。`MCP/test/` 全目录检索确认：现有测试**没有**任何一条断言 `mcp_help` 的旧「未知工具」文案形状。

### 0.6 执行者要改的文件清单（本计划**唯一**允许触碰的）

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/index.js` | 修改（1 个新常量 + 2 个新函数 + 3 处调用点 + 1 处 export） | 离线占位详情 + 指针文案单一事实源 |
| `MCP/test/gabpOfflineHelp.test.js` | **新建** | 钉住上面的行为不被回退 |
| `README.md` | 修改（GABP 镜像小节，160 行后加 1 行） | 人类可读的一句话指路 |

**明确不动：** `Source/` 下任何 `.cs`、任何 `*.dll`、`MCP/toolConfig.json`、`MCP/gabpClient.js`、`MCP/test/` 下已存在的 9 个测试文件、`MCP/logs/**`、任何日志文件。

> ### ⚠️ 关于本计划里的行号
> 所有 `MCP/index.js:NNNN` 都是**基线行号**（= 写下本计划那一刻的文件状态，本人生成计划时逐行核对过）。任务 1 会往 `3644` 处插入约 55 行，其**之后**的行号（`3653-3656`、`4277-4279`、`5433`）都会下移。
> **执行时请一律以每个步骤代码块里的"原代码内容"去定位替换点，不要按行号硬套。** 每个步骤都给出了要替换的原文，照着原文找即可唯一确定位置。

### 0.7 明确不做的事（裁决报告点名过，别顺手做）

- **不缓存镜像 schema。** 不在 GABP 连接时把 schema 存下来、离线时复用。那会让「名字正确但调不到」在返回体里看起来像「可调用」，与 `toolAvailability` 的三态设计（`MCP/index.js:3437-3462`）直接冲突。占位详情因此固定 `inputSchema: null` + `schemaNote` 说明原因。
- **不新增"idle"独立状态。** 不把「游戏进程在但未握手就绪」单列一个状态。`get_game_status` 的 `capabilities`（`MCP/index.js:3517-3564`）+ `gabp.state` 已经把这个信息给全了，再加一层只会制造第二套口径。
- **不给离线占位详情加 `suggestions`。** 裁决报告提醒过 `suggestTools`（`MCP/index.js:3979-4002`）遍历的是 `allToolNames()`，离线镜像不在里面，加进去只会得到一个空数组或一堆不相干的"你是不是想找"。一个**名字完全正确**的工具收到"你是不是想找别的名字"本身就是另一种误导。占位详情靠 `message` + `guidance` 讲清归因就够了。
- **不动 `MCP/index.js:3339` 的死代码。** 理由与实测见 §0.8 第 5 条。清理它属于另一件事。

### 0.8 逐行复核裁决报告后发现的修正（**与上游 `triage/07-gabp-idle-no-fail-fast.md` 不一致之处，以本节为准**）

上游报告的行号全部复核过，**无漂移**。但有 5 处它没说到、或说得不准，执行者必须知道：

1. **上游 §5 说的缺陷代码是 `3646-3656`，实际缺陷分支是 `3653-3656`。** `mcpHelpResult` 起点确为 `3646`，但 `if (target) {` 在 `3652`、`entries.find` 在 `3653`、手写「未知工具」的那行是 `3655`。
2. **上游没有指出真正省事的关键事实：`unknownToolText` 已经写对了。** `MCP/index.js:3624-3643` 的 `unknownToolText(target)` 第一件事就是 `gabpMirrorUnavailable(target)`，已经能返回 `GABP_MIRROR_UNAVAILABLE` / `GABP_MIRROR_REMOVED` / `UNKNOWN_TOOL` 三态。也就是说 `mcp_help` 之所以错，是因为它**绕过**了这个已有的正确实现自己手写了一份。本计划因此不新造归因逻辑，只做分流 + 加占位详情。
3. **`isError` 标志不能一刀切。** 离线占位详情返回 `isError: false`（名字是合法的，只是调不到，报成"调用失败"会误导）；真·未知 / 已移除仍走 `isError: true`。
4. **`mcp_help` 的 `usageHint` 对离线镜像是错的。** `MCP/index.js:3649` 的 `usageHintFor` 会说「该工具在 toolConfig.json 中禁用，需通过 agg_call_tool 调用（tool: xxx）」——但 GABP 没连上时 `agg_call_tool` 同样被 `4274` 行拦下。给这条指引等于把调用方送去撞第二堵墙。占位详情里**必须不带** `usageHint`，并用 `alternateRoute` 字段把这一点讲明。
5. **`MCP/index.js:3339` 是死代码，本计划不动它。** `toolCategory`（`:3324-3330`）在 `:3327` 已经把**所有** `rimworld.* / rimbridge.*` 归进 `'bridge'`，所以 `toolPrecondition` 里 `:3338-3339` 的 `cat === 'game_control'` + `isGabpMirrorName` 组合永远进不去。后果：任何镜像的 `precondition` 都取 `3336` 那句 `'需 RimBridgeServer 连接'`（**不是** 3339 那句带「（GABP 镜像）」的）。本计划的测试断言按 3336 写。清理这段死代码属于另一件事，不要顺手改。

---

## 任务 1：`mcp_help` 对离线镜像返回占位详情（主修）

**文件：**
- 创建：`MCP/test/gabpOfflineHelp.test.js`
- 修改：`MCP/index.js:3644`（在 `unknownToolText` 之后、`mcpHelpResult` 之前插入两个新函数）、`MCP/index.js:3653-3656`（改三路分流）、`MCP/index.js:5433`（补 export）

- [ ] **步骤 1：创建测试文件（此时全部是红灯）**

创建 `MCP/test/gabpOfflineHelp.test.js`，内容如下（整体写入，不要留空壳）：

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则 main() 会执行、监听 3000 端口并拉起 GABP/mono 桥接
// （守卫见 MCP/index.js:5406）。**本文件全程不启游戏、不连真实 RimBridgeServer。**
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const modRoot = path.join(here, '..');
const source = fs.readFileSync(path.join(modRoot, 'index.js'), 'utf-8');
const toolConfig = JSON.parse(fs.readFileSync(path.join(modRoot, 'toolConfig.json'), 'utf-8'));

// ===========================================================================
// 「GABP 离线」在本文件里如何构造 —— 动手前必读
// ===========================================================================
// ① 天然离线：UESD_MCP_NO_START='1' 让 main() 不执行，startGABPBridge() 从不执行
//    → gabpBridge 恒为 null（MCP/index.js:2513）、gabpTools 恒为 []（:2514）。
//    ⚠ 陷阱：桥为 null 时 toolAvailability / gabpMirrorUnavailable 报的是
//    "state=disabled"（那两处的 `gabpBridge ? gabpBridge.state : 'disabled'`），
//    而真机 idle 是 "state=idle"（GabpClient 初值）——要复刻真机形态必须走 ②。
// ② 注入假桥接：mod._testHooks.setGabpBridge（:5453）只换模块变量，零 IO。
//    用完必须在 finally 里复位为 null。
// ③ 纯函数注入：buildOfflineMirrorHelpDetail 的字段全来自入参，与模块状态无关。
// ===========================================================================

const IDLE_BRIDGE = { state: 'idle', isConnected: () => false };
const CONNECTED_BRIDGE = { state: 'connected', isConnected: () => true };

const withGabpBridge = async (bridge, fn) => {
  mod._testHooks.setGabpBridge(bridge);
  try {
    return await fn();
  } finally {
    mod._testHooks.setGabpBridge(null);
  }
};

const helpFor = async (tool) => JSON.parse(
  (await Promise.resolve(mod.mcpHelpResult({ tool }))).content[0].text);

// rimworld.pause_game 同时登记在 toolConfig.tools（值 false）与 TOOL_SUBGROUP
// （MCP/index.js:3027 → 'game_control/game/state'），故 isKnownGabpMirrorName 必为真
const MIRROR = 'rimworld.pause_game';
// toolConfig 里为 true（默认暴露）的一个镜像：钉住"直接暴露"与"当前可调用"是两件事
const EXPOSED_MIRROR = 'rimworld.execute_debug_action';
// 已移除/改名的镜像名（MCP/index.js:3422-3426 的 REMOVED_MIRROR_HINTS）
const REMOVED = ['rimworld.get_game_info', 'rimbridge.get_bridge_status', 'rimbridge.ping'];
const NONEXISTENT = 'rimworld.no_such_tool_xyz';

// toolConfig 里登记的全部镜像名（GABP 离线时它们都不在 allToolNames() 里）
const MIRROR_NAMES = Object.keys(toolConfig.tools).filter((n) => /^(rimworld|rimbridge)\./.test(n));

// ===========================================================================
// ① 缺陷本体：mcp_help 查一个名字完全正确的离线镜像，不得报「未知工具」
// ===========================================================================

test('GABP 离线时 mcp_help 查已知镜像：给离线占位详情，不是「未知工具」', async () => {
  await withGabpBridge(IDLE_BRIDGE, async () => {
    const d = await helpFor(MIRROR);
    assert.equal(d.success, false);
    assert.equal(d.errorCode, 'GABP_MIRROR_UNAVAILABLE');
    assert.equal(d.name, MIRROR);
    assert.equal(d.offline, true);
    assert.equal(d.enabled, false); // toolConfig 里 rimworld.pause_game = false
    assert.deepEqual(d.availability, { state: 'unavailable', reason: 'GABP 未连接(state=idle)' });
    assert.equal(d.category, 'bridge'); // toolCategory：所有镜像名都落 bridge（MCP/index.js:3327）
    assert.equal(d.precondition, '需 RimBridgeServer 连接'); // 来自 MCP/index.js:3336
    assert.equal(d.inputSchema, null);
    assert.match(d.message, /GABP 未连接（state=idle）/);
    assert.match(d.guidance, /brrainz\.rimbridgeserver/);
    assert.ok(!/未知工具/.test(JSON.stringify(d)),
      `整个响应体都不该出现"未知工具"：${JSON.stringify(d)}`);
  });
});

test('「默认暴露」与「当前可调用」是两件事：默认暴露的镜像离线时 enabled=true 但 availability=unavailable', async () => {
  await withGabpBridge(IDLE_BRIDGE, async () => {
    assert.equal(toolConfig.tools[EXPOSED_MIRROR], true, `${EXPOSED_MIRROR} 应是默认暴露的镜像`);
    const d = await helpFor(EXPOSED_MIRROR);
    assert.equal(d.enabled, true);
    assert.equal(d.availability.state, 'unavailable');
    assert.match(d.availability.reason, /GABP 未连接\(state=idle\)/);
  });
});

test('GABP 已连接但该工具未注册：理由换一种，仍然是占位详情而不是「未知工具」', async () => {
  await withGabpBridge(CONNECTED_BRIDGE, async () => {
    const d = await helpFor(MIRROR);
    assert.equal(d.offline, true);
    assert.equal(d.availability.state, 'unavailable');
    assert.match(d.availability.reason, /已连接但未暴露该工具/);
    assert.match(d.message, /不在 RimBridgeServer 当前暴露的工具清单里/);
    assert.ok(!/未知工具/.test(JSON.stringify(d)));
  });
});

test('已移除/改名的镜像名仍是错误形态并指向替代工具（不能被当成"暂时离线"）', async () => {
  for (const n of REMOVED) {
    const d = await helpFor(n);
    assert.equal(d.errorCode, 'GABP_MIRROR_REMOVED', `${n} 应报 GABP_MIRROR_REMOVED`);
    assert.equal(d.offline, undefined, `${n} 不应拿到离线占位详情`);
    assert.match(d.guidance, /get_game_status|原生 get_game_info/, `${n} 应指向替代工具`);
  }
});

test('真·不存在的名字仍是 UNKNOWN_TOOL，且保留原有的「共 N 个工具 / 支持前缀别名」提示', async () => {
  const d = await helpFor(NONEXISTENT);
  assert.equal(d.errorCode, 'UNKNOWN_TOOL');
  assert.equal(d.offline, undefined);
  assert.match(d.message, /未知工具/);
  assert.match(d.hint, /共 \d+ 个工具/);
  assert.match(d.hint, /rimworld\.\/rimbridge\.\/rigworld\./);
  assert.ok(Array.isArray(d.suggestions), 'unknownToolText 仍应带 suggestions');
});

test('全量扫一遍 toolConfig 里的镜像：每一个都能在 mcp_help 查到带 precondition 的说明', async () => {
  // 这一条故意**不**注入假桥接：走的是"桥压根没建起来"（gabpBridge === null）那种形态。
  // 它只断言契约（offline 标记 / name / availability.state / 非空 precondition / 不含"未知工具"），
  // 不断言 reason 里的 state 字符串——那会随 idle / disabled 两种形态变化。
  assert.ok(MIRROR_NAMES.length >= 100,
    `toolConfig 里应登记上百个镜像，实际 ${MIRROR_NAMES.length}`);
  const offenders = [];
  for (const n of MIRROR_NAMES) {
    const d = await helpFor(n);
    if (d.offline !== true) { offenders.push(`${n}: offline=${d.offline}`); continue; }
    if (d.name !== n) offenders.push(`${n}: name=${d.name}`);
    if (!d.availability || d.availability.state !== 'unavailable') {
      offenders.push(`${n}: availability=${JSON.stringify(d.availability)}`);
    }
    if (typeof d.precondition !== 'string' || d.precondition.length === 0) {
      offenders.push(`${n}: precondition=${d.precondition}`);
    }
    if (/未知工具/.test(String(d.message))) offenders.push(`${n}: message 里含"未知工具"`);
  }
  assert.deepEqual(offenders, [],
    '以下镜像在 mcp_help 里查不到可用说明：\n' + offenders.join('\n'));
});

// ===========================================================================
// ② 纯函数：不读模块状态，空入参不炸
// ===========================================================================

test('纯函数 buildOfflineMirrorHelpDetail：字段全部来自入参，空入参也不炸', () => {
  const d = mod.buildOfflineMirrorHelpDetail('rimworld.some_tool', {
    category: 'bridge',
    enabled: true,
    reason: 'GABP 未连接(state=discovering)',
    precondition: '需 RimBridgeServer 连接',
    errorCode: 'GABP_MIRROR_UNAVAILABLE',
    message: 'msg',
    guidance: 'guide',
    notes: { example: '{}', notes: '易错点' },
  });
  assert.equal(d.name, 'rimworld.some_tool');
  assert.equal(d.success, false);
  assert.equal(d.errorCode, 'GABP_MIRROR_UNAVAILABLE');
  assert.equal(d.enabled, true);
  assert.equal(d.offline, true);
  assert.equal(d.category, 'bridge');
  assert.deepEqual(d.availability, { state: 'unavailable', reason: 'GABP 未连接(state=discovering)' });
  assert.equal(d.precondition, '需 RimBridgeServer 连接');
  assert.equal(d.message, 'msg');
  assert.equal(d.guidance, 'guide');
  assert.equal(d.example, '{}'); // 来自 notes
  assert.equal(d.notes, '易错点');
  assert.equal(d.inputSchema, null);

  const bare = mod.buildOfflineMirrorHelpDetail('rimworld.some_tool');
  assert.equal(bare.name, 'rimworld.some_tool');
  assert.equal(bare.success, false);
  assert.equal(bare.offline, true);
  assert.equal(bare.enabled, false);
  assert.equal(bare.inputSchema, null);
  assert.equal(typeof bare.precondition, 'string');
  assert.ok(bare.precondition.length > 0, '空入参也必须给得出 precondition');
  assert.match(bare.description, /离线占位/, 'description 必须说明这是离线占位');
  assert.match(bare.schemaNote, /schema/);
  assert.match(bare.alternateRoute, /agg_call_tool/);
});

// ===========================================================================
// ③ 不撒谎：离线占位详情不得伪造 schema，也不得说"改走 agg_call_tool 就行"
//    （离线时 agg_call_tool 同样被 MCP/index.js:4274 的 fail-fast 拦截）
// ===========================================================================

test('不撒谎：占位详情不伪造参数 schema，也不给"改走 agg_call_tool"的假解法', async () => {
  await withGabpBridge(IDLE_BRIDGE, async () => {
    const d = await helpFor(MIRROR);
    assert.equal(d.inputSchema, null, '不得凭空造一个 inputSchema');
    assert.match(d.schemaNote, /RimBridgeServer/);
    assert.match(d.schemaNote, /不缓存/);
    assert.match(d.alternateRoute, /无法/);
    assert.ok(!d.usageHint,
      '离线时不得再给"改走 agg_call_tool"的指引（那条路同样被 fail-fast 拦截）');
  });
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/gabpOfflineHelp.test.js`

预期：**7 个测试全部 FAIL**。三类失败形态：
- 第 1/2/3/6 个：`d.offline` 是 `undefined`（当前返回的是 `error: "未知工具: ..."`，`assert.equal(d.success, false)` 会先炸，因为当前返回体里根本没有 `success` 字段 → `undefined !== false`）。
- 第 4/5 个：`d.errorCode` 是 `undefined`（当前返回体只有 `error` / `hint` 两个字段）。
- 第 7 个：抛 `TypeError: mod.buildOfflineMirrorHelpDetail is not a function`。

**这就是正确的红灯。** 如果某个测试意外 PASS，说明它没测到东西，回去核对断言。

- [ ] **步骤 3：加纯函数 `buildOfflineMirrorHelpDetail`**

在 `MCP/index.js:3643`（`unknownToolText` 的收尾 `}`）**之后**、`:3644` 空行处，插入：

```js
// mcp_help tool=<离线镜像> 的占位详情（纯函数：不读任何模块状态，所有状态字段由 input 注入）。
//
// 背景（issue-triage [07]，triage/07-gabp-idle-no-fail-fast.md）：GABP 离线时 allToolEntries()（:3352-3359）
// 不含 gabpTools，mcpHelpResult 的"未命中"分支会把**名字完全正确**的镜像报成"未知工具"，
// 调用方据此推断"我名字写错了"→ 开始猜别的名字反复重试。而 agg_list_tools（:3605-3612）
// 恰恰让调用方「见 mcp_help tool=<名字> 的 precondition」，指的就是这条死路。
//
// 这里返回的是**离线占位说明**，不是该工具的真实参数定义，三条自律：
//   1. inputSchema 固定 null + schemaNote 说明原因：真实 schema 由 RimBridgeServer 在连接后下发，
//      本项目刻意不缓存（缓存会让"名字正确但调不到"看起来像"可调用"，与 availability 三态冲突）。
//   2. availability 复用 toolAvailability 的判定，保证与 agg_list_tools / 菜单叶子同一口径，不各说各话。
//   3. 不给 usageHint（MCP/index.js:3649 那句"改走 agg_call_tool"）：GABP 没连上时 agg_call_tool
//      同样被 :4274 的 fail-fast 拦下，指过去等于把调用方送到第二堵墙前。改用 alternateRoute 讲清这件事。
function buildOfflineMirrorHelpDetail(name, input) {
  const src = input || {};
  return {
    success: false,
    errorCode: src.errorCode || 'GABP_MIRROR_UNAVAILABLE',
    name,
    category: src.category || 'unknown',
    // enabled = "是否直接暴露给 AI"（toolConfig 白名单）；availability = "此刻是否调得到"。
    // 两者正交：默认暴露的 13 个镜像在 GABP 离线时是 enabled=true + availability=unavailable。
    enabled: src.enabled === true,
    offline: true,
    availability: { state: 'unavailable', reason: src.reason || '提供方未就绪' },
    description: `（离线占位说明，非真实工具定义）${name} 是 RimBridgeServer（GABP）镜像工具，`
      + '名字正确；现在调不到是因为提供方没连上，不是写法问题。',
    inputSchema: null,
    schemaNote: '参数 schema 由 RimBridgeServer 在 GABP 连接后下发，本层不缓存；'
      + 'GABP 连上后重新调用 mcp_help tool=<名字> 即可拿到真实 schema。'
      + '在此之前，可用 mcp_help path:<菜单路径> 查叶子列表（叶子已带 [当前不可用：原因] 标记）。',
    precondition: src.precondition || '需 RimBridgeServer 连接',
    message: src.message || `${name} 当前不在可调用集中：GABP 未连接`,
    guidance: src.guidance || '需要在游戏内启用 brrainz.rimbridgeserver，并等 MCP 连上 GABP。',
    alternateRoute: 'GABP 未连接时该工具**无法**通过 agg_call_tool 绕过（同样被 fail-fast 拦截）；'
      + '连接状态看 get_game_status 的 gabp 字段与 capabilities。',
    // 放在最后展开：TOOL_USAGE_NOTES 的条目形如 { example, notes }，放末尾可保证它不会覆盖上面的归因字段
    ...(src.notes || {}),
  };
}
```

- [ ] **步骤 4：加薄封装 `offlineMirrorHelpDetail`（唯一读模块状态的地方）**

紧接着步骤 3 插入的代码**之后**，插入：

```js
// 把模块当前状态喂给纯函数。这是本计划里唯一读 gabpBridge 的地方，
// 单测用 mod._testHooks.setGabpBridge（:5453）注入假桥接即可复刻 idle / connected 两种形态。
//
// 只处理「已知镜像名 + 当前未注册」且**不是**已移除/改名的那一类：
//   - GABP_MIRROR_REMOVED（已删除/改名）→ 返回 null，交回 unknownToolText 的错误形态；
//     把"永久删除"说成"暂时离线"同样是错的归因。
//   - null（真·不存在的名字）→ 返回 null，交回 unknownToolText 的 UNKNOWN_TOOL 形态。
function offlineMirrorHelpDetail(name) {
  const mirror = gabpMirrorUnavailable(name);
  if (!mirror || mirror.errorCode !== 'GABP_MIRROR_UNAVAILABLE') return null;
  return buildOfflineMirrorHelpDetail(name, {
    category: toolCategory(name),
    enabled: toolEnabledNow(name),
    reason: toolAvailability(name).reason,
    precondition: toolPrecondition(name),
    errorCode: mirror.errorCode,
    message: mirror.message,
    guidance: mirror.guidance,
    notes: TOOL_USAGE_NOTES[name] || null,
  });
}
```

> `TOOL_USAGE_NOTES`（`MCP/index.js:2930`）里目前没有任何镜像条目，所以 `notes` 实际是 `null`；这一行是为了让未来补进镜像的 `example` / `notes` 自动出现在离线占位详情里（不要为它改 `TOOL_USAGE_NOTES`，那是别的计划的事）。

- [ ] **步骤 5：把 `mcpHelpResult` 的「未命中」分支改成三路分流**

把 `MCP/index.js:3653-3656` 这一段：

```js
    const meta = entries.find(t => t.name === target);
    if (!meta) {
      return { content: [{ type: 'text', text: JSON.stringify({ success: false, error: `未知工具: ${target}`, hint: `共 ${entries.length} 个工具；先无参调用 mcp_help 查看一级概览；工具名支持 rimworld./rimbridge./rigworld. 前缀别名` }, null, 2) }], isError: true };
    }
```

整段替换为：

```js
    const meta = entries.find(t => t.name === target);
    if (!meta) {
      // 三路分流（issue-triage [07]）：
      //   ① 已知 GABP 镜像名但当前未注册（GABP 未连接 / 该能力未注册）→ 离线占位详情。
      //      旧版一律报「未知工具」，让调用方以为自己名字写错而反复猜名字；
      //      而 agg_list_tools 的缺席提示（:3611）恰恰让调用方来查这里。
      //      isError 留 false：名字是合法的，只是此刻调不到，报成"调用失败"同样是误导。
      const offline = offlineMirrorHelpDetail(target);
      if (offline) {
        return { content: [{ type: 'text', text: JSON.stringify(offline, null, 2) }] };
      }
      //   ②③ 已移除/改名的镜像（GABP_MIRROR_REMOVED）与真·不存在的名字（UNKNOWN_TOOL）→ 走既有的
      //       unknownToolText（:3624-3643），它本来就正确区分这两类。
      //       额外把旧版那条"共 N 个工具 / 支持前缀别名"的 hint 补回去，避免信息倒退。
      const err = JSON.parse(unknownToolText(target));
      err.hint = `共 ${entries.length} 个工具；先无参调用 mcp_help 查看一级概览；工具名支持 rimworld./rimbridge./rigworld. 前缀别名`;
      return { content: [{ type: 'text', text: JSON.stringify(err, null, 2) }], isError: true };
    }
```

> `JSON.parse(unknownToolText(target))` 是安全的：`unknownToolText` 的两条分支（`:3627` 与 `:3636`）都返回 `JSON.stringify(...)` 的结果，这里只是把它解回对象再补一个字段。

- [ ] **步骤 6：把纯函数加进 export 块**

在 `MCP/index.js:5433`（`  unknownToolText,`）**下一行**插入：

```js
  buildOfflineMirrorHelpDetail,
```

改完后这一段应是：

```js
  aggListToolsText,
  unknownToolText,
  buildOfflineMirrorHelpDetail,
  toolAvailability,
```

> `offlineMirrorHelpDetail`（薄封装）**不导出**：它读模块状态，测试通过 `mcpHelpResult` 走真实接线覆盖它，单独导出只会多一个没人用的公共面。

- [ ] **步骤 7：运行测试验证通过**

运行：`cd MCP && node --test test/gabpOfflineHelp.test.js`

预期：**7 个测试全部 PASS**。特别确认第 6 个（全量扫 121 个镜像）没有 offender——若报某个名字 `offline=undefined`，先查它是不是 `REMOVED_MIRROR_HINTS` 里的名字（`MCP/index.js:3422-3426` 列出 3 个，而它们**不在** `toolConfig.tools` 里，所以正常不会出现在扫描集合中）。

- [ ] **步骤 8：跑全量测试确认没有回退**

运行：`cd MCP && npm test`

预期：全部 PASS。重点关注三个文件：
- `toolchainFixes.test.js`（§3 归因三态、§4 capabilities、§5 DPA）——本计划复用了它的 `gabpMirrorUnavailable` / `toolAvailability`，语义必须一字不变。
- `toolCountConsistency.test.js`（四套计数口径自洽）——本计划没动 `allToolEntries` / `menuUniverseNames` / `allToolNames`，计数不应有任何变化。
- `mapMarker.test.js`（`mcp_help tool=post_map_marker` 详情形状）——正常工具的详情分支没动，仍走 `:3657-3670` 原逻辑。

- [ ] **步骤 9：Commit**

```bash
git add MCP/index.js MCP/test/gabpOfflineHelp.test.js
git commit -m "fix(mcp_help): GABP 离线时镜像工具返回离线占位详情而非未知工具"
```

---

## 任务 2：把「去 mcp_help 查」提成单一事实源，并给镜像 fail-fast 补上指路

**为什么必须在本任务里做、不能并进任务 1：** 任务 1 之前，`agg_list_tools` 的那句指路（`MCP/index.js:3611`）指的是死路；任务 1 之后它才可达。**先落地任务 1 再改指路**，否则中途任何一次 commit 都处于"指路指向尚未修好的地方"的破损状态。同理，fail-fast 响应里补指路也必须排在任务 1 之后。

**文件：**
- 修改：`MCP/index.js:3595`（新增常量）、`MCP/index.js:3610-3611`（改用常量）、`MCP/index.js:4277-4279`（补指路）
- 修改：`MCP/test/gabpOfflineHelp.test.js`（末尾追加）

- [ ] **步骤 1：确认任务 1 已合入**

运行：`cd MCP && node --test test/gabpOfflineHelp.test.js`

预期：7 个 PASS。**不通过就不要继续**，先去修任务 1。

- [ ] **步骤 2：追加失败的测试**

在 `MCP/test/gabpOfflineHelp.test.js` **末尾追加**：

```js
// ===========================================================================
// ④ 指路不许指向死路：agg_list_tools 与镜像 fail-fast 说的「去 mcp_help 查」必须真的查得到
// ===========================================================================

test('护栏：agg_list_tools 指的 mcp_help 路由确实可用（文案不许指向死路）', async () => {
  await withGabpBridge(IDLE_BRIDGE, async () => {
    const list = mod.aggListToolsText();
    assert.match(list, /GABP 未连接/);
    assert.match(list, /mcp_help tool=<名字>/, 'agg_list_tools 必须指路到 mcp_help tool=');
    // 指针真的落得到带 precondition 的详情上
    const d = await helpFor(MIRROR);
    assert.equal(typeof d.precondition, 'string');
    assert.ok(d.precondition.length > 0);
  });
});

test('文案单一事实源：agg_list_tools 与镜像 fail-fast 用的是同一份指针常量', () => {
  const aggBlock = source.slice(
    source.indexOf('function aggListToolsText('),
    source.indexOf('function unknownToolText('));
  assert.ok(!/对应能力说明见 mcp_help/.test(aggBlock), '不应再保留旧的内联指针文案');
  assert.match(aggBlock, /MCP_HELP_TOOL_POINTER/);

  const failFast = source.slice(source.indexOf('if (!gabpBridge || !gabpBridge.isConnected()) {'));
  assert.match(failFast.slice(0, 900), /MCP_HELP_TOOL_POINTER/,
    '镜像 fail-fast 也必须用同一份指针，否则两处文案会各自漂移');

  const defBlock = source.slice(
    source.indexOf('const MCP_HELP_TOOL_POINTER'),
    source.indexOf('function aggListToolsText('));
  assert.match(defBlock, /mcp_help tool=<名字>/);
});

test('镜像 fail-fast 仍是毫秒级结构化失败（GABP 未连接），并带上 mcp_help 指路', async () => {
  await withGabpBridge(IDLE_BRIDGE, async () => {
    const t0 = Date.now();
    const res = await mod.handleToolCall('rimworld.press_accept', {});
    const elapsed = Date.now() - t0;
    assert.equal(res.isError, true);
    const body = JSON.parse(res.content[0].text);
    assert.equal(body.success, false);
    // gabpErrorResponse（:2697-2702）走 wrapGABPResult(msg, false)（:2690-2693）：
    // 错误文本落在 body.error，errorCode 固定 'GABP_ERROR'（不是 GABP_MIRROR_UNAVAILABLE）
    assert.equal(body.errorCode, 'GABP_ERROR');
    assert.match(body.error, /GABP 未连接/);
    assert.match(body.error, /mcp_help tool=<名字>/);
    assert.ok(elapsed < 3000,
      `未连接时应毫秒级失败（真实日志实测 3ms），实际 ${elapsed}ms —— 若接近 30s 说明退化成了超时`);
  });
});
```

- [ ] **步骤 3：运行测试验证失败**

运行：`cd MCP && node --test test/gabpOfflineHelp.test.js`

预期：**新增 3 个 FAIL，任务 1 的 7 个仍 PASS**。
- 第 8 个：`assert.match(list, /mcp_help tool=<名字>/)` 失败——当前 `aggListToolsText` 写的是「对应能力说明见 mcp_help tool=\<名字\> 的 precondition」，**没有**「工具不在线时也有占位说明」这句，且源码里没有 `MCP_HELP_TOOL_POINTER`。
- 第 9 个：`source.indexOf('const MCP_HELP_TOOL_POINTER')` 返回 `-1`，`slice(-1, ...)` 拿到的是文件末尾片段，三个断言全废。
- 第 10 个：`assert.match(body.error, /mcp_help tool=<名字>/)` 失败——当前 fail-fast 只拼了 `mirror.message` + `mirror.guidance`。

- [ ] **步骤 4：新增指针常量**

在 `MCP/index.js:3594`（`gabpMirrorUnavailable` 的收尾 `}`）**之后**、`:3596`（`// agg_list_tools 输出：…` 注释行）**之前**插入：

```js
// 「这个工具去哪儿查说明」的唯一事实源（issue-triage [07]）。
// agg_list_tools 的缺席提示（下方）与镜像 fail-fast（:4274 附近）都必须用这一份，
// 避免两处文案各自漂移——文案互相矛盾比没有文案更糟：调用方只会照着文案走。
// 这条指路只有在 mcpHelpResult 的离线占位详情（buildOfflineMirrorHelpDetail）落地后才可达，
// 两者必须成对存在：先有占位详情，再挂指路。
const MCP_HELP_TOOL_POINTER =
  '完整说明（含 precondition 与当前可用性）见 mcp_help tool=<名字>；GABP 离线时该查询会返回这条工具的'
  + '离线占位说明（名字正确，缺的是提供方），不会误报成"未知工具"。';
```

- [ ] **步骤 5：让 `aggListToolsText` 用这个常量**

把 `MCP/index.js:3610-3611` 这两行：

```js
      + '。这些名字是正确的，失败原因是提供方模组未启用/未连接，不是名字写错；'
      + '对应能力说明见 mcp_help tool=<名字> 的 precondition。');
```

替换为：

```js
      + '。这些名字是正确的，失败原因是提供方模组未启用/未连接，不是名字写错；'
      + MCP_HELP_TOOL_POINTER);
```

- [ ] **步骤 6：给镜像 fail-fast 补上指路**

把 `MCP/index.js:4277-4279` 这三行：

```js
      return gabpErrorResponse(mirror
        ? `${mirror.message}。${mirror.guidance}`
        : `GABP 未连接（state=${gabpBridge ? gabpBridge.state : 'disabled'}），镜像工具 ${name} 需 RimBridgeServer 连接`);
```

替换为：

```js
      return gabpErrorResponse(mirror
        ? `${mirror.message}。${mirror.guidance}。${MCP_HELP_TOOL_POINTER}`
        : `GABP 未连接（state=${gabpBridge ? gabpBridge.state : 'disabled'}），镜像工具 ${name} 需 RimBridgeServer 连接。${MCP_HELP_TOOL_POINTER}`);
```

> **不要动 `:4281-4286`**（"GABP 已连接但镜像清单里没有该工具"分支）。它已经指向 `mcp_help path:"bridge"`，那是一条**有效**的菜单路径（`resolveMenuPath` 能解析），不是死路。

- [ ] **步骤 7：运行测试验证通过**

运行：`cd MCP && node --test test/gabpOfflineHelp.test.js`

预期：**10 个测试全部 PASS**。

- [ ] **步骤 8：跑全量测试**

运行：`cd MCP && npm test`

预期：全部 PASS。`toolchainFixes.test.js:60-70` 断言 `agg_call_tool` 的 `body.message` 匹配 `/不在可调用集中|名字本身没错/` 且不以「未知工具」开头——本任务只在 `handleToolCall` 的镜像 fail-fast 里追加文案，**不经过** `unknownToolText`，因此不受影响；如果它失败，说明你改错了分支。

- [ ] **步骤 9：Commit**

```bash
git add MCP/index.js MCP/test/gabpOfflineHelp.test.js
git commit -m "fix(mcp_help): 镜像缺席与 fail-fast 共用同一份 mcp_help 指路文案"
```

---

## 任务 3：README 写清「GABP 没连上时也能查到镜像工具的说明」

**文件：**
- 修改：`README.md:160` 之后插入 1 行
- 修改：`MCP/test/gabpOfflineHelp.test.js`（末尾追加）

- [ ] **步骤 1：追加失败的测试**

在 `MCP/test/gabpOfflineHelp.test.js` **末尾追加**：

```js
// ===========================================================================
// ⑤ 人类可读文档
// ===========================================================================

test('README 的 GABP 镜像小节写明：未连上时 mcp_help 仍能查到镜像工具的说明', () => {
  const readme = fs.readFileSync(path.join(modRoot, '..', 'README.md'), 'utf-8');
  const i = readme.indexOf('**RimBridgeServer（GABP）镜像工具');
  assert.ok(i > 0, 'README 应有 GABP 镜像工具小节');
  const section = readme.slice(i, i + 1600);
  assert.match(section, /mcp_help tool=/, '该小节应给出离线也可用的查询入口');
  assert.match(section, /未连接/, '该小节应说明未连接时的行为');
  assert.match(section, /未知工具/, '该小节应点明不会再被误报成"未知工具"');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/gabpOfflineHelp.test.js`

预期：新增 1 个 FAIL，报 `The expression evaluated to a falsy value` 或 `regexp ... did not match`（`README.md:160` 那一大段引用块里目前没有 `mcp_help tool=` 字样）。任务 1/2 的 10 个仍 PASS。

- [ ] **步骤 3：改 README**

在 `README.md:160`（那一长段 `> 依赖 **RimBridgeServer**（…13 个重叠能力的原生工具已删除…）。` 引用块）**之后**、`:162` 的空行处插入：

```markdown
> **GABP 未连接时怎么查镜像工具的用法**：`mcp_help` 始终可查。传 `mcp_help tool=rimworld.pause_game` 这类镜像名时，返回的是一条**离线占位说明**——含 `precondition`（需 RimBridgeServer 连接）、`availability`（`unavailable` + 具体原因，如 `GABP 未连接(state=idle)`）和归因文案，**不会被误报成"未知工具"**；参数 `inputSchema` 为 `null`（真实 schema 由 RimBridgeServer 在连接后下发，本层不缓存）。GABP 连上后重查同一工具名即可拿到完整定义。
```

- [ ] **步骤 4：运行测试验证通过**

运行：`cd MCP && node --test test/gabpOfflineHelp.test.js`

预期：**11 个测试全部 PASS**。

- [ ] **步骤 5：跑全量测试**

运行：`cd MCP && npm test`

预期：全部 PASS。

- [ ] **步骤 6：Commit**

```bash
git add README.md MCP/test/gabpOfflineHelp.test.js
git commit -m "docs(readme): 写明 GABP 未连接时 mcp_help 仍可查镜像工具说明"
```

---

## 人工验收（自动化测试覆盖不到的部分）

本计划的 11 个自动化测试覆盖了"离线态下文案说什么"。下面三条要在**真机**上确认（做验收时逐条跑并核对输出）：

1. **离线态的 `mcp_help` 详情（主菜单，游戏已起但 GABP 未握手）**

   ```json
   agg_call_tool { tool: "mcp_help", args: { tool: "rimworld.pause_game" } }
   ```

   预期：返回 JSON 里 `success:false`、`errorCode:"GABP_MIRROR_UNAVAILABLE"`、`offline:true`、`enabled:false`、`availability.state:"unavailable"`、`availability.reason:"GABP 未连接(state=idle)"`、`category:"bridge"`、`precondition:"需 RimBridgeServer 连接"`、`inputSchema:null`，且**整段文本不含"未知工具"**。

2. **`agg_list_tools` 的指路确实可走通（同一离线态）**

   ```json
   agg_call_tool { tool: "agg_list_tools", args: {} }
   ```

   预期：输出含「# 注意：GABP 未连接 → N 个 rimworld.*/rimbridge.* 镜像工具当前**不可调用**」，末尾含「完整说明（含 precondition 与当前可用性）见 mcp_help tool=\<名字\>」。**把指路里点到的第一个工具名原样喂给第 1 条的 `mcp_help tool=`，必须返回占位详情而不是"未知工具"。**

3. **GABP 连上之后，回归检查在线态没被改坏（需要游戏进主菜单并等 GABP 握手完成）**

   ```json
   agg_call_tool { tool: "mcp_help", args: { tool: "rimworld.pause_game" } }
   ```

   预期：返回的是**正常详情**（`name` / `category` / `enabled` / `availability.state === "exposed"` 或 `"agg"` / `description` / **真实 `inputSchema`** / `precondition`），**不含** `offline:true`、**不含** `errorCode`。即：占位详情只在离线时出现，绝不污染在线态。

**回滚方式：** 本计划只碰 `MCP/index.js`、`MCP/test/gabpOfflineHelp.test.js`、`README.md`，`git revert` 任务 1/2/3 三个 commit 即可。无需重编译模组、无需动 `Source/`。

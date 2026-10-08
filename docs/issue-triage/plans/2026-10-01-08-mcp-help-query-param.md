# `mcp_help` 关键词检索（`query` 参数）实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 给 `mcp_help` 加一个 `query` 入参，让「找一个工具名」从「无参概览 → `path:"game_control/game"` → `path:".../state`」共 3 次往返压缩到 **1 次调用**，并且在 GABP 离线时也能命中镜像名并标出「当前不可用」。

**架构：** 只改 MCP 服务器侧 `MCP/index.js` 一个文件——在 `AGG_META_TOOLS` 的 `mcp_help` 定义里给 `inputSchema` 加 `query`（`additionalProperties` 保持 `false`），在 `mcpHelpResult()` 的 `tool` 分支与 `path` 分支之间插第三个分支。检索的名字集合直接取 `menuUniverseNames()`（项目既有的「菜单唯一事实源」），渲染直接调 `toolListLine()`（三态可用性标记），**不新建任何名字表或计数口径**。零命中时返回一个带 `hint` / `leafPaths` / `nearMisses` 的 `success` 语义正常响应，把「没搜到」和「不存在」明确区分开。

**技术栈：** Node.js ESM（`MCP/package.json` `"type": "module"`）、`node:test` + `node:assert/strict`（项目现有测试栈，`MCP/package.json:9`）。**不改任何 C#，不重编译模组 DLL，不启动游戏。**

---

## 0. 给零上下文执行者的背景（不要跳过）

### 0.1 这条要修的到底是什么

来源是 `docs/mcp-吐槽板.md` 的 `## [08] mcp_help 要逐层下钻 🟡` 条目（正文第 466-482 行）。原文：

> 找一个 `pause_game` 要：`mcp_help{}` → `path:"game_control/game"` → `path:".../state"` 才看到。而 `pause_game` 是每个游戏会话都要用的高频工具。
>
> 建议：`mcp_help` 支持关键词过滤：`mcp_help{query:"pause"}`。

上游裁决报告：`docs/issue-triage/triage/08-mcp-help-deep-drill.md`，裁决 **值得修（🟡 低，不产生假成功/假阴性）**。

### 0.2 为什么「加个 `query`」在协议层根本不存在

`MCP/index.js:2806-2810` 是三个元工具 `agg_list_tools` / `agg_call_tool` / `mcp_help` 的定义。`mcp_help`（**第 2809 行**）的 `inputSchema` 完整长这样：

```js
{ name: 'mcp_help', description: '...', inputSchema: { type: 'object',
  properties: {
    tool: { type: 'string', description: '要查询的工具名（省略则返回菜单概览/子组）' },
    path: { type: 'string', description: '菜单路径，/ 分隔（如 "bridge/lua_script"、"game_control/game/gameplay/debug_action"）' }
  }, additionalProperties: false } }
```

只有 `tool` / `path`，且 `additionalProperties: false`。实现侧 `mcpHelpResult()`（`MCP/index.js:3646-3720`）同样只有两个 `if`：`tool` 分支在 **3652-3671**、`path` 分支在 **3673-3681**，剩下的 **3683-3719** 是无参概览。**没有任何对 `args` 的关键词扫描。**

`mcp_help` 的调用入口在 `MCP/index.js:5110-5115`：

```js
} else if (name === 'mcp_help') {
  if (!isToolEnabled('mcp_help')) {
    result = { content: [{ type: 'text', text: 'mcp_help 已禁用' }], isError: true };
  } else {
    result = mcpHelpResult(args);
  }
}
```

`args` 原样透传，没有额外校验层。

### 0.3 为什么 `pause_game` 确实藏在三层下

- 一级类别 6 个：`MENU_CATEGORIES`（`MCP/index.js:3183`）= `system / unityexplorer / mono / meta / bridge / game_control`。
- `game_control` 下**只有** `game` 一个子组 —— `MCP/index.js:3247-3249` 强制校验，其他名字一律报错。
- `game` 下 10 个子组：`MENU_GAME_CONTROL_SUBGROUPS.game`（`MCP/index.js:3187`）= `state / gameplay / arch / mods / dpa / debugmenu / colonist / stream / world / long_task`。
- `'rimworld.pause_game': 'game_control/game/state'` —— `MCP/index.js:3027`。

`state` 是叶子（`MCP/index.js:3261-3263`），传 `path` 才返回工具列表。所以无参 → `game_control/game` → `game_control/game/state` **3 次往返**属实。

### 0.4 三个可以直接复用的现成件（**不要另起炉灶**）

| 件 | 位置 | 为什么能直接用 |
|---|---|---|
| `menuUniverseNames()` | `MCP/index.js:3369-3376` | 注释明写它是菜单/计数的**唯一事实源**；返回可调用集 ∪ 已登记的镜像名，再剔除隐藏工具与 `_reserved` 项。**GABP 离线时镜像名照样在里面**——这正是「附带收益」的来源。 |
| `toolListLine(name)` | `MCP/index.js:3281-3287` | 已带三态可用性标记（`[已暴露]` / `[经 agg_call_tool 可用]` / `[当前不可用：原因]`），叶子菜单和 `agg_list_tools` 都在用。 |
| `suggestTools(name, limit)` | `MCP/index.js:3979-4002` | 双字母组 Dice 模糊建议，零命中时用来给「你是不是想找……」 |

### 0.5 测试基础设施（用项目真实命令）

- 框架：`node:test` + `node:assert/strict`，见 `MCP/test/toolCountConsistency.test.js:1-2`。
- 全部测试：`cd MCP && npm test`（= `node --test "test/*.test.js"`，`MCP/package.json:9`）。
- 单文件：`cd MCP && node --test test/mcpHelpQuery.test.js`。
- **关键守卫**：任何 `import '../index.js'` 的测试文件都必须在 import **之前**写 `process.env.UESD_MCP_NO_START = '1'`，否则会启动 HTTP 服务器占 3000 端口（见 `MCP/test/toolCountConsistency.test.js:4-6`）。
- 想走**真实对外协议**（`tools/list` / `tools/call`）而不是直接调函数，用 `MCP/test/toolchainFixes.test.js:17-36` 的 `withClient` / `withCallText` 模式（`InMemoryTransport` 连真 server）。本计划的任务 1 会用到。

### 0.6 执行者不要做的事

- 不要改 `docs/mcp-吐槽板.md`（给条目加「已修」批注是主代理的活）。
- 不要改 `Source/` 下任何 `.cs`，不要重编译 `Assemblies/`，不要启动游戏。
- 不要为了验证去连真实游戏：本计划的所有断言都必须在「MCP 进程不连游戏」的状态下成立。

---

## 1. 逐条复核裁决报告后的修正（**与上游报告不一致之处，以本节为准**）

1. **吐槽板行号引错段。** 报告 §2.1 写「吐槽板原文 `docs/mcp-吐槽板.md:417-433`」。实读：**417-433 是条目 `[06]` 的范围**；`[08]` 的标题行在 **466**，条目正文在 **466-482**。结论不变，行号必须改。
2. **`docs/issue-triage/plans/` 现在不是空的。** 报告写「`docs/issue-triage/plans/` 目录当前为空，无重复计划」（§「是否已被现有计划覆盖」）。实读：目录现有 3 份计划 —— `2026-10-01-06-debug-action-path-format.md`、`2026-10-01-04-startup-call-timeout-disambiguation.md`、`2026-10-01-11-search-source-false-negative-convention.md`。逐份核对，**均未覆盖 `mcp_help` 的 `inputSchema` 或 `mcpHelpResult`**，故「未被覆盖」的结论成立，但依据要换成这三份文件名。
3. **「先做 [08] 再做 [06] 以免 `toolCountConsistency` 在中间态失败」不成立。** 报告 §「与 [06] 的同组关系」最后一句如此建议。复核 `MCP/test/toolCountConsistency.test.js` 全文 148 行：它只断言概览/叶子/宇宙的**数量与集合关系**（`total`、`menuVisible`、`callableTotal`、`concealedCount`、各子组求和），**没有任何一条断言 `inputSchema` 的形状或属性个数**。给 `mcp_help` 加一个 `inputSchema` 属性既不改工具数也不改集合，因此两个计划的执行顺序与该测试无关。
4. **「`pause_game` 在菜单里已有三处引用」表述不准。** 报告 §修法 2 说「菜单里已经有 `pause_game` 的三处引用（`MCP/index.js:1882` / `2942` / `2943` / `4168`）」。实读：这四处是 `task_cancel` 的**描述**与 `task_status` / `task_cancel` / 长任务超时文案的**正文**，以及 `MCP/index.js:3027` 的 `TOOL_SUBGROUP` 映射 —— 不是「菜单条目」。数字也应是「四处」不是「三处」。结论（`pause_game` 确实高频）不受影响。
5. **`TOOL_SUBGROUP` / `MENU_*` 的行号范围写错了。** 报告 §责任归属写「常量全在 `MCP/index.js:2905-3197`」。实读：**2905 落在 `TOOL_CATEGORY` 里**（`get_map_things` 那一段）。准确范围是：`TOOL_SUBGROUP` = **2995-3180**，`MENU_*` 块 = **3182-3197**（`MENU_CATEGORIES` 3183 / `MENU_BRIDGE_SUBGROUPS` 3185 / `MENU_GAME_CONTROL_SUBGROUPS` 3187 / `MENU_GAMEPLAY_SUBGROUPS` 3189 / `MENU_PATH_MIGRATIONS` 3192）。
6. **复核通过的项（照单接受）**：`mcp_help` 的 `inputSchema` 确实在 **2809**；`mcpHelpResult` 确实是 **3646-3720** 且只有两个分支；`menuUniverseNames()` 确实是 **3369-3376**；`toolListLine` 确实是 **3281-3287**；`'rimworld.pause_game': 'game_control/game/state'` 确实在 **3027**；`MCP/first-configs.md` 检索 `mcp_help|高频|常用|agg_list_tools` 确实**零命中**；`MCP/index.js:3361-3368` 的「三套口径互相矛盾 → 194 vs 191」注释确实存在。
7. **报告没有指出、但本计划必须处理的两处「既有机制耦合」**（见 §2.4 与任务 3 步骤 1）。

---

## 2. 范围决策：进计划的 / 明确排除的

裁决报告给了三档修法。逐条判：

### 2.1 ✅ 进计划

| 项 | 落点 | 理由 |
|---|---|---|
| **① `mcp_help` 加 `query` 参数** | 任务 1 + 任务 2 | 唯一必做项。改动集中在 `MCP/index.js`，不新增依赖、不改菜单结构、不改现有两种调用的行为。 |
| **③ `agg_list_tools` 输出一行引导文案** | 任务 3 步骤 5 | 报告说是「可选，一行」。**判断为该做**：`agg_list_tools` 是「不知道有什么工具」时的兜底通道，而 [08] 的症状正是「不知道有 `pause_game`」。只加 `query` 却不在这条通道上指路，等于新功能没人发现——那正是本条的病根。成本是 1 行。 |

### 2.2 ❌ 明确排除

| 项 | 排除理由 |
|---|---|
| **② 无参概览加「高频 N 个」快捷区** | ① 报告自己已警告「这份名单会立刻过时」，需跟着工具增删手工维护；② 没有任何实测数据支撑「哪 N 个高频」——菜单里唯一能拿到的量是「引用次数」，那是文本引用不是调用频次，拿它当依据就是新的假成功来源；③ **它的诉求已被 `query` 覆盖**：找高频工具正是关键词检索的典型用法。引入一份会腐烂的硬编码清单，换不来 `query` 换不到的东西。 |
| **拍平菜单层级**（如 `game_control` 直接挂 10 个子组） | `MCP/index.js:3361-3368` 的注释明确记录了此前因「三套口径互相矛盾」导致计数漂移（概览 194 vs 实际 191）。动导航结构 = 重踩那个坑。本计划的 `query` **完全不改导航结构**，只加一个旁路入口。 |
| **给 `query` 加 `keyword` 别名** | `additionalProperties: false` 是对外声明的契约。未在 `properties` 里声明的 `keyword` 会被做 schema 校验的 MCP 客户端**直接拒收**——那是**可见**的失败，比静默接受一个没写进文档的参数要好。本项目其它工具支持 `keyword` 别名是因为它们的 `properties` 里确实写了/走 `TOOL_ARG_ALIASES`（`MCP/index.js:3785-3794`）；`mcp_help` 不走 `normalizeToolArgs`（见 `MCP/index.js:5110-5115`，args 原样透传），所以要加别名就得先把 `additionalProperties` 放开，那才是真正的倒退。 |
| **改 `tool` 分支的 `tool` vs `path` 优先级回显** | 传 `{tool, path}` 时 `tool` 胜、`path` 被静默丢弃，这是**既有**行为。本计划只对**新增的** `query` vs `path` 做 `ignoredParams` 回显，并在工具描述里把三级优先级写清楚。顺手改 `tool` 分支的返回体形状属于越界，会动到 `mcp_help tool=<名字>` 这个已被多处测试断言的输出契约。 |

### 2.3 三个必须由本计划拍板的语义（任务 1/2 实现，测试钉死）

1. **优先级：`tool` > `query` > `path` > 无参。**
   `tool` 是精确匹配、意图最窄，放最前；`query` 显式给了关键词，意图明确，压过需要用户已知道层级结构的 `path`；三个都不给才是概览。
   实现上 `tool` 分支在 `mcpHelpResult` 的最前面（3652 行）就已经 `return` 了，所以 `query` 分支**只可能和 `path` 冲突**。
2. **被覆盖的参数必须点名回显。** 同时传 `{query, path}` 时走 `query`，返回体带 `ignoredParams: ["path"]`。
   不回显 = 调用方会以为「我传了 path 却拿到关键词结果」是服务端算错了 —— 那就是**假成功**。同理，`query` 分支**不回显** `tool`，因为到不了这里。
3. **`additionalProperties` 保持 `false`，只往 `properties` 里加 `query`。** 不放开、不加别名（理由见 §2.2）。

### 2.4 ⚠️ 零命中必须显式判读（本计划唯一的假阴性风险点）

`query` 没搜到工具时，返回体**不是错误、也不是「不存在」**，而是「按这两个字段做了子串匹配，没匹配上」。必须带：

- `hint`：**明写「0 命中不等于该能力不存在」**，并给出 ≥3 条可行动的下一步（缩短关键词 / 中英文各试一遍 / 用 `path` 看某一层 / 用 `agg_list_tools` 看可调用全集）。
- `leafPaths`：由 `MENU_*` 常量**推导**出的全部叶子路径（不会与菜单漂移），让「改用 path 逐层看」这条建议可以直接照抄。
- `nearMisses`：`suggestTools(query, 5)` 的模糊建议，外加一句「可能不相关」的免责。

依据：`docs/mcp-吐槽板.md` 条目 `[11]` 已经吃过一次亏——`search_source` 零命中被读成「该 vanilla 成员不存在」，并把这个**事实相反的结论**写进了源码。见 `docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md`。

**返回体刻意不带 `success` 字段、也不带 `isError`。** 这不是随手选的：`attachUsageOnFailure()`（`MCP/index.js:4006-4075`）在 **4009-4018** 对「`isError === true`」或「JSON 里有 `success === false`」的响应会自动追加 `usage` / `suggestions`；而它在 **4024** 行对 `mcp_help` 取的内部目标是 `(args.tool || args.path) || 'mcp_help'` —— 传 `query` 时这两个都没有，会去给 `mcp_help` 本身算调用范式，输出一堆与本次检索无关的噪声。所以零命中走「正常成功形状」，与叶子列表 `buildMenuNode()`（3314-3320）和无参概览（3704-3718）**同一种形状**（都不带 `success`），从根上绕开那条噪声路径。

---

## 3. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/index.js` | 修改（1 个 schema + 1 个新分支 + 3 个新函数 + 1 次 DRY 重构 + 5 处文案） | 全部逻辑。 |
| `MCP/test/mcpHelpQuery.test.js` | **新建** | 18 个测试，钉住 schema、分发、优先级、零命中契约、发现面文案、计数口径不变。 |

**明确不动：** `Source/` 下任何 `.cs`、`Assemblies/`、`MCP/toolConfig.json`、任何 `*.log`、`docs/mcp-吐槽板.md`、`docs/issue-triage/` 下任何文件。

### `MCP/index.js` 的 11 个改动点一览

| # | 锚点位置（本次复核后的行号） | 动作 |
|---|---|---|
| 1 | `MCP/index.js:3183` 之后 | 新增常量 `MENU_LEAF_CATEGORIES` |
| 2 | `MCP/index.js:3222` | 把 `['system','unityexplorer','mono','meta']` 字面量换成 `MENU_LEAF_CATEGORIES`（语义等价，纯 DRY） |
| 3 | `MCP/index.js:3277` 之后 / `3279` 注释之前 | 新增 `toolDescriptionFirstLine(name)` |
| 4 | `MCP/index.js:3281-3287` | 重构 `toolListLine` 改调 #3（**输出逐字节不变**） |
| 5 | `MCP/index.js:3322` 之后 / `3324 function toolCategory` 之前 | 新增 `MENU_QUERY_MAX_RESULTS` / `menuLeafPaths()` / `buildMenuQueryPayload()` |
| 6 | `MCP/index.js:3672`（`tool` 分支结束的 `}` 与 `// path →` 注释之间的空行） | 插入 `query` 分支 |
| 7 | `MCP/index.js:2809` | `mcp_help` 的 `description` + `inputSchema.properties.query` |
| 8 | `MCP/index.js:2979` | `TOOL_USAGE_NOTES.mcp_help` 文案 |
| 9 | `MCP/index.js:3301` | `buildMenuNode()` 的 `note` 追加 query 指引 |
| 10 | `MCP/index.js:3601` 之后 | `aggListToolsText()` 插入一行引导 |
| 11 | `MCP/index.js:3711` | 无参概览的 `note` 开头追加 query 指引 |

> **#5 为什么必须放在 `buildMenuNode` 之后**：`MCP/test/toolchainFixes.test.js:121` 有一条**源码切片**断言 ——
> `source.slice(source.indexOf('function toolListLine('), source.indexOf('function buildMenuNode('))`，
> 断言这段里必须出现 `toolAvailabilityTag(name)` 且不得出现 `[禁用]`。把新函数插在 `buildMenuNode` **之后**，这段切片保持逐字节不变，最省事也最安全。
> 同理，#3 的 `toolDescriptionFirstLine` 放在 `toolListLine` **之前**（`3277` 之后），也不会落进这段切片。

---

## 任务 1：`query` 参数打通（schema + 分发 + 纯函数 + 优先级）

**文件：**
- 修改：`MCP/index.js:3183` 之后、`MCP/index.js:3222`、`MCP/index.js:3277` 之后、`MCP/index.js:3281-3287`、`MCP/index.js:3322` 之后、`MCP/index.js:3672`、`MCP/index.js:2809`
- 创建：`MCP/test/mcpHelpQuery.test.js`

- [ ] **步骤 1：创建测试文件（本任务的 10 个测试）**

创建 `MCP/test/mcpHelpQuery.test.js`（整体写入，不要留空文件）：

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const modRoot = path.join(here, '..');
const source = fs.readFileSync(path.join(modRoot, 'index.js'), 'utf-8');

/** 与 MCP/test/toolCountConsistency.test.js:15-17 同一口径 */
const parse = async (value) => JSON.parse(
  await Promise.resolve(value).then((r) =>
    typeof r === 'string' ? r : (r.content || []).map((c) => c.text || '').join('')));

/** 从叶子行 "- [标记] 工具名 描述" 里取工具名（与 toolCountConsistency.test.js:20 同一口径） */
const nameOfLine = (line) => line.replace(/^-\s*/, '').replace(/^\[[^\]]*\]\s*/, '').split(/\s+/)[0];

const query = async (q) => parse(mod.mcpHelpResult({ query: q }));

/** 走真实对外协议 tools/list（mcp_help 在 MCP/toolConfig.json 里是 true，必然列出） */
async function withListTools(fn) {
  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { InMemoryTransport } = await import('@modelcontextprotocol/sdk/inMemory.js');
  const server = mod._testHooks.createServer();
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'test-client', version: '1.0.0' });
  try {
    await Promise.all([server.connect(serverT), client.connect(clientT)]);
    return fn(await client.listTools());
  } finally {
    await client.close().catch(() => {});
    await server.close().catch(() => {});
  }
}

// ===========================================================================
// ① 协议层：真实 tools/list 里 mcp_help 的 inputSchema 带 query
// ===========================================================================

test('协议层：真实 tools/list 的 mcp_help 带 query 参数，additionalProperties 仍为 false', async () => {
  const tool = await withListTools((l) => l.tools.find((t) => t.name === 'mcp_help'));
  assert.ok(tool, 'mcp_help 必须在 tools/list 里（toolConfig.json 里是 true）');
  const props = tool.inputSchema.properties || {};
  assert.ok(props.query, 'inputSchema.properties 必须含 query');
  assert.equal(props.query.type, 'string');
  assert.ok(props.tool, '原有 tool 参数必须仍在');
  assert.ok(props.path, '原有 path 参数必须仍在');
  assert.equal(tool.inputSchema.additionalProperties, false, '不得为图省事放开 additionalProperties');
});

// ===========================================================================
// ② 检索行为：这条吐槽的全部诉求就是「3 次往返压成 1 次」
// ===========================================================================

test('query:"pause" 一次命中 rimworld.pause_game（不再需要 3 次 path 下钻）', async () => {
  const r = await query('pause');
  assert.equal(r.level, 'query');
  assert.ok(r.tools.some((l) => nameOfLine(l) === 'rimworld.pause_game'),
    `一次检索就应命中，实际返回: ${JSON.stringify(r).slice(0, 400)}`);
});

test('大小写不敏感：query:"PAUSE" 与 query:"pause" 命中集合完全相同', async () => {
  const lower = await query('pause');
  const upper = await query('PAUSE');
  assert.deepEqual(upper.tools.map(nameOfLine), lower.tools.map(nameOfLine));
  assert.ok(upper.tools.length > 0);
});

test('命中范围是菜单宇宙的子集，且每行都带三态可用性标记', async () => {
  const universe = new Set(mod.menuUniverseNames());
  const r = await query('game');
  assert.ok(r.tools.length > 0, '关键词 "game" 应有命中');
  for (const line of r.tools) {
    const n = nameOfLine(line);
    assert.ok(universe.has(n), `命中项 ${n} 必须属于 menuUniverseNames()`);
    assert.match(line, /^- \[(已暴露|经 agg_call_tool 可用|当前不可用：[^\]]+)\] /,
      `每行都应带三态标记，实际: ${line}`);
  }
});

test('附带收益：GABP 离线时仍能命中镜像名，并标出"当前不可用"（测试进程不连游戏）', async () => {
  const r = await query('pause_game');
  const line = r.tools.find((l) => nameOfLine(l) === 'rimworld.pause_game');
  assert.ok(line, 'GABP 未连接时 menuUniverseNames() 仍应含 rimworld.pause_game');
  assert.match(line, /\[当前不可用：GABP 未连接\(state=/,
    `离线镜像应标出"名字对但提供方没起"，实际: ${line}`);
});

test('隐藏工具不得被 query 搜出来（menuUniverseNames 已剔除 hotreload_*）', async () => {
  const r = await query('hotreload');
  assert.equal(r.total, 0, '隐藏工具既不进菜单也不进检索');
  assert.ok(!/hotreload/.test(JSON.stringify(r.tools)), '工具行里不得出现 hotreload_*');
});

// ===========================================================================
// ③ 优先级与参数回显：被覆盖的参数必须点名，不许静默丢弃
// ===========================================================================

test('优先级 tool > query：同时传时按 tool 走', async () => {
  const r = await parse(mod.mcpHelpResult({ tool: 'get_game_status', query: 'pause' }));
  assert.equal(r.name, 'get_game_status', 'tool 应胜出');
  assert.notEqual(r.level, 'query', '不该进 query 分支');
});

test('优先级 query > path：同时传时走 query，并点名被忽略的 path', async () => {
  const r = await parse(mod.mcpHelpResult({ query: 'pause', path: 'bridge/ops' }));
  assert.equal(r.level, 'query');
  assert.deepEqual(r.ignoredParams, ['path'], '被丢掉的 path 必须回显，否则调用方会以为算错了');
  assert.ok(r.tools.some((l) => nameOfLine(l) === 'rimworld.pause_game'));
});

test('只传 query 时不产生 ignoredParams 字段', async () => {
  const r = await query('pause');
  assert.equal('ignoredParams' in r, false);
});

test('空白 query 按"未提供"处理（回落概览），不返回 0 命中', async () => {
  const r = await parse(mod.mcpHelpResult({ query: '   ' }));
  assert.equal(r.level, 'root', '应回落到一级概览');
  assert.ok(Array.isArray(r.categories), '概览才有 categories');
});

test('非字符串 query 被归一并原样回显（不抛异常、不静默吞掉）', async () => {
  const r = await parse(mod.mcpHelpResult({ query: 12345 }));
  assert.equal(r.level, 'query');
  assert.equal(r.query, '12345', '归一后的 query 必须回显，让调用方看得见发生了什么');
});

// ===========================================================================
// ④ 口径护栏：新增 query 分支不得扰动任何工具计数
// ===========================================================================

test('计数口径不变：概览自洽，且 total == menuUniverseNames().length', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  assert.equal(root.total, root.categories.reduce((a, c) => a + c.count, 0));
  assert.equal(root.total, mod.menuUniverseNames().length);
  const gc = root.categories.find((c) => c.path === 'game_control');
  assert.equal(gc.count, root.game_control.gameSubgroups.reduce((a, s) => a + s.count, 0));
  // query 只是 mcpHelpResult 里的第三个 if，既不读也不写计数，上面的不变量必须原样成立。
  // MCP/test/toolCountConsistency.test.js 因此**无需同步任何断言**。
});

test('mcp_help 自己仍可被 query 搜到（加参数不该把自己弄丢）', async () => {
  const r = await query('mcp_help');
  assert.ok(r.tools.some((l) => nameOfLine(l) === 'mcp_help'));
});

// ===========================================================================
// ⑤ DRY：叶子行与检索行必须共用同一个渲染函数（防"各说各话"）
// ===========================================================================

test('toolListLine 与 query 检索行共用 toolDescriptionFirstLine（DRY，防口径漂移）', () => {
  const from = source.indexOf('function toolListLine(');
  const to = source.indexOf('function buildMenuNode(');
  const slice = source.slice(from, to);
  assert.match(slice, /toolDescriptionFirstLine\(name\)/,
    'toolListLine 必须改调 toolDescriptionFirstLine，不得自己再抄一遍取描述首行的逻辑');
  assert.ok(!/RimBridgeServer 镜像工具/.test(slice),
    '镜像占位描述的兜底必须只在 toolDescriptionFirstLine 里出现一次');
  assert.match(slice, /toolAvailabilityTag\(name\)/,
    '必须保留 toolAvailabilityTag 调用（MCP/test/toolchainFixes.test.js:122 依赖它）');
  assert.ok(!/\[禁用\]/.test(slice), '不得再出现旧版 [禁用] 标记');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/mcpHelpQuery.test.js`

预期：**11 个测试全部 FAIL**。前 9 个因 `r.level` 为 `undefined` / 断言不成立而 FAIL（`query` 分支还不存在，`mcpHelpResult({query:'pause'})` 现在返回的是**无参概览**）；第 10 个 `计数口径不变` 会 PASS（它断言的是本来就成立的不变量）；第 11 个因 `toolListLine` 里没有 `toolDescriptionFirstLine` 而 FAIL。

> 只要看到「`query` 分支不存在导致的 FAIL」就对了。若第 10 个也 FAIL，说明工作区已被别人改动过，先停下来核对。

- [ ] **步骤 3：加 `MENU_LEAF_CATEGORIES` 常量**

在 `MCP/index.js:3183`（`const MENU_CATEGORIES = [...]`）**下一行**插入：

```js
// 叶子一级类别（下面没有子组，path 传一级即返回工具列表）。判定口径与 resolveMenuPath 里的
// ['system','unityexplorer','mono','meta'] 字面量必须一致——抽成常量就是为了不让两处各写一遍后漂移。
const MENU_LEAF_CATEGORIES = ['system', 'unityexplorer', 'mono', 'meta'];
```

- [ ] **步骤 4：把 `resolveMenuPath` 里的字面量换成常量（语义等价）**

把 `MCP/index.js:3222` 整行替换为：

```js
  if (MENU_LEAF_CATEGORIES.includes(category)) {
```

> 语义完全等价：原字面量 `['system','unityexplorer','mono','meta']` 与新常量逐项相同，`includes` 的入参仍是 `category`。这是一次纯 DRY 收敛，不改任何分支走向。

- [ ] **步骤 5：加 `toolDescriptionFirstLine`**

在 `MCP/index.js:3277`（`resolveMenuPath` 结束的 `}`）**之后**、`3279`（`// 工具名 → 可用性三态与描述首行…` 注释）**之前**插入：

```js
// 工具描述首行：叶子列表行（toolListLine）与 query 检索行必须用**同一个**取值口径，
// 否则会出现「列表里显示 A、检索按 B 匹配」这种各说各话。
// 已注册条目取 allToolEntries() 的 description 首行；
// 只登记在 TOOL_SUBGROUP、当前未注册的（典型：GABP 离线时的 rimworld.* 镜像）取占位描述；
// 其余为空串。
function toolDescriptionFirstLine(name) {
  const t = allToolEntries().find(e => e.name === name);
  const desc = t ? String(t.description || '') : (TOOL_SUBGROUP[name] ? '（RimBridgeServer 镜像工具）' : '');
  return String(desc).split(/\r?\n/)[0];
}

```

- [ ] **步骤 6：重构 `toolListLine`（输出逐字节不变）**

把 `MCP/index.js:3281-3287`（`function toolListLine(name) {` 到它的 `}`）替换为：

```js
function toolListLine(name) {
  const a = toolAvailability(name);
  const suffix = a.state === 'exposed' ? '' : `（${a.reason}）`;
  return `- ${toolAvailabilityTag(name)} ${name} ${toolDescriptionFirstLine(name)}${suffix}`;
}
```

> 改动只有一处：描述首行的取值从内联表达式换成调用 `toolDescriptionFirstLine(name)`，两者算出来是同一个字符串（步骤 5 是把步骤 6 的原表达式原样搬过去的）。
> `toolAvailability(name)` 与 `toolAvailabilityTag(name)` **都保留、都照旧各调一次**：`toolAvailabilityTag` 内部会再调一次 `toolAvailability`，这是既有行为，且 `MCP/test/toolchainFixes.test.js:122` 的源码切片断言依赖 `toolAvailabilityTag(name)` 这行文字。不要"顺手优化"掉。

- [ ] **步骤 7：加 `menuLeafPaths()` 与 `buildMenuQueryPayload()`**

在 `MCP/index.js:3322`（`buildMenuNode` 结束的 `}`）**之后**、`3324`（`function toolCategory(name) {`）**之前**插入：

```js
// ========== mcp_help 关键词检索（query）==========
// 用途：找一个工具（如 rimworld.pause_game 在菜单里埋在 game_control/game/state 三层下），
// 以前要「无参概览 → path:"game_control/game" → path:".../state"」共 3 次往返；加 query 后 1 次命中。
// 事实源与叶子列表**完全同源**：名字取 menuUniverseNames()（唯一事实源），行渲染取 toolListLine()
// （三态可用性标记）。**不要**在这里另起一套名字表或计数口径——历史上"概览 194 vs 实际 191"
// 就是这么来的（见本文件上方"菜单/计数统一口径"注释）。
const MENU_QUERY_MAX_RESULTS = 50;

// 全部叶子路径（零命中时给调用方一份"改用 path 逐层看"的现成清单）。
// 由 MENU_* 常量**推导**，不手写，因此不会与菜单结构漂移。
// 叶子判定沿用 resolveMenuPath：MENU_LEAF_CATEGORIES 是叶子类别；
// bridge / game_control 的子组是叶子，唯一例外 game_control/game/gameplay 还有一层孙组。
function menuLeafPaths() {
  return [
    ...MENU_LEAF_CATEGORIES,
    ...MENU_BRIDGE_SUBGROUPS.map(s => `bridge/${s}`),
    ...MENU_GAME_CONTROL_SUBGROUPS.game
      .filter(s => s !== 'gameplay')
      .map(s => `game_control/game/${s}`),
    ...MENU_GAMEPLAY_SUBGROUPS.map(g => `game_control/game/gameplay/${g}`),
  ];
}

// query 检索的返回体（纯函数，无 IO）。匹配只看"工具名 + 描述首行"——描述首行正是
// toolListLine() 打印出来的那一行，所以**检索范围与肉眼所见一致**，命中/不命中都能被复核
// （返回体里的 matchedOn 字段就是把这个范围说出来，避免"缩小了范围却不告诉你"）。
// 返回体刻意**不带** success 字段、也不带 isError：与 buildMenuNode()、无参概览同一形状，
// 同时避开 attachUsageOnFailure（见该函数 4009-4018 行：它对 isError===true 或 success===false
// 的响应会追加 usage/suggestions，而它给 mcp_help 取的内部目标是 args.tool || args.path，
// 传 query 时两者都没有 → 会输出一堆与本次检索无关的噪声）。
function buildMenuQueryPayload(query, ignoredParams) {
  const q = String(query).trim();
  const lower = q.toLowerCase();
  const names = menuUniverseNames();
  const hits = names
    .filter((n) => n.toLowerCase().includes(lower) || toolDescriptionFirstLine(n).toLowerCase().includes(lower))
    .sort();
  const shown = hits.slice(0, MENU_QUERY_MAX_RESULTS);
  const payload = {
    level: 'query',
    query: q,
    matchedOn: ['name', 'description'],
    menuTotal: names.length,
    total: hits.length,
    returned: shown.length,
    truncated: hits.length > shown.length,
    maxResults: MENU_QUERY_MAX_RESULTS,
    tools: shown.map(toolListLine),
  };
  if (ignoredParams && ignoredParams.length) payload.ignoredParams = ignoredParams;
  return payload;
}

```

- [ ] **步骤 8：在 `mcpHelpResult` 插入 `query` 分支**

在 `MCP/index.js:3672`（`tool` 分支结束的 `}` 与 `3673` 的 `// path → 多级菜单…` 注释之间的空行处）插入：

```js
  // query → 关键词检索（优先级：tool > query > path > 无参）
  // 能走到这里说明 tool 没给（tool 分支在上面已经 return），所以只有 path 可能被 query 盖掉。
  // 为什么要显式回显 ignoredParams：同时传了 query 和 path 时，被丢掉的那一个如果不点名，
  // 调用方会以为"我传了 path 却拿到关键词结果"是服务端算错了——那是**假成功**。
  // query 一律按字符串处理并原样回显（String()+trim）：空白串按"未提供"处理，
  // 非字符串归一后可从 payload.query 看见归一结果，不需要再开一条类型门禁。
  const rawQuery = args && args.query;
  const query = rawQuery === undefined || rawQuery === null ? '' : String(rawQuery).trim();
  if (query) {
    const ignoredParams = args && args.path ? ['path'] : [];
    return { content: [{ type: 'text', text: JSON.stringify(buildMenuQueryPayload(query, ignoredParams), null, 2) }] };
  }

```

- [ ] **步骤 9：给 `inputSchema` 加 `query`，并改写 `description`**

把 `MCP/index.js:2809` **整行**替换为：

```js
  { name: 'mcp_help', description: '[元工具] 查工具用法：传 query 按关键词（工具名/描述，大小写不敏感子串）一次检索全量菜单，如 query:"pause" 直达 rimworld.pause_game；传 tool 返回单个工具的详细说明（参数schema/前置条件/调用示例/易错提示）；传 path 逐级深入（如 path:"bridge/lua_script"、"path:"game_control/game/gameplay/camera"）返回子组计数或工具列表；无参返回一级概览（6 个类别及计数）。三个参数同时出现时按 tool > query > path 生效，query 与 path 同传时返回体的 ignoredParams 会点名被忽略的 path。始终可用。', inputSchema: { type: 'object', properties: { tool: { type: 'string', description: '要查询的工具名（精确匹配；优先级最高）' }, query: { type: 'string', description: '关键词：按工具名与描述首行做大小写不敏感的子串匹配（优先级低于 tool、高于 path）' }, path: { type: 'string', description: '菜单路径，/ 分隔（如 "bridge/lua_script"、"game_control/game/gameplay/debug_action"）（优先级最低）' } }, additionalProperties: false } }
```

> 手误自检：上面第二个 `path:"..."` 前面多打了一个 `path:`，应当是 `path:"bridge/lua_script"、"game_control/game/gameplay/camera"`。写入后请核对第 9 步的测试 `协议层：真实 tools/list 的 mcp_help 带 query 参数…` 通过，它会顺带校验 `properties.query` 与 `additionalProperties`。

- [ ] **步骤 10：运行测试验证通过**

运行：`cd MCP && node --test test/mcpHelpQuery.test.js`

预期：**11 个测试全部 PASS**。

- [ ] **步骤 11：跑全量测试确认没有回退**

运行：`cd MCP && npm test`

预期：全部 PASS。**重点盯这四个**：

| 文件 | 为什么盯它 |
|---|---|
| `MCP/test/toolCountConsistency.test.js` | 菜单计数口径。本任务**不需要**改它任何一个断言——`query` 分支只是第三个 `if`，不参与任何计数。若它 FAIL，说明改动越界了。 |
| `MCP/test/toolchainFixes.test.js` | `121-123` 行对 `toolListLine` 做**源码切片**断言（步骤 6 动了这个函数）。 |
| `MCP/test/hotreloadConcealment.test.js` | 隐藏工具不得漏进任何菜单呈现面。 |
| `MCP/test/mapMarker.test.js` | 它会走 `mcp_help path=game_control/game/gameplay/marker`，验证步骤 6 的重构没改坏叶子渲染。 |

- [ ] **步骤 12：Commit**

```bash
git add MCP/index.js MCP/test/mcpHelpQuery.test.js
git commit -m "feat(mcp-help): 给 mcp_help 加 query 关键词检索，一次命中代替三层 path 下钻"
```

---

## 任务 2：零命中的判读契约（本计划唯一的假阴性风险点）

**为什么单独一个任务：** 任务 1 交付的是「搜得到」。搜不到时的返回体是另一种失败模式——调用方极容易把 `total: 0` 读成「这个能力不存在」，然后把**事实相反的结论**写进后续的代码/注释/计划（`docs/mcp-吐槽板.md` 条目 `[11]` 已经这么踩过一次）。这值得一个独立审查者单独把关。

**文件：**
- 修改：`MCP/index.js:3372` 附近（`buildMenuQueryPayload` 函数体内，零命中分支）
- 修改：`MCP/test/mcpHelpQuery.test.js`（追加 5 个测试）

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/mcpHelpQuery.test.js` **末尾追加**：

```js
// ===========================================================================
// 零命中判读契约：没搜到 ≠ 不存在（参见 docs/issue-triage/plans/
//   2026-10-01-11-search-source-false-negative-convention.md 的同一条教训）
// ===========================================================================

/** 一个绝不会出现在工具名/描述里的关键词 */
const MISS = 'zzz绝对不可能存在的工具名qqq';

test('零命中：返回体不带 success / 不带 isError（正常成功形状，别触发 usage 噪声）', async () => {
  const r = await query(MISS);
  assert.equal(r.level, 'query');
  assert.equal(r.total, 0);
  assert.deepEqual(r.tools, []);
  assert.equal('success' in r, false,
    '带 success:false 会让 attachUsageOnFailure（index.js:4009-4018）追加一堆与检索无关的 usage');
  assert.equal(r.isError, undefined, '零命中不是协议错误');
});

test('零命中：hint 必须明说"0 命中不等于该能力不存在"，并给出至少 3 条可行动下一步', async () => {
  const r = await query(MISS);
  assert.equal(typeof r.hint, 'string', '零命中必须带 hint');
  assert.match(r.hint, /不等于该能力不存在/, '必须把"没搜到"与"不存在"明确切开');
  assert.match(r.hint, /缩短关键词/, '建议 1：缩短关键词');
  assert.match(r.hint, /path/, '建议 2：改用 path 看某一层');
  assert.match(r.hint, /agg_list_tools/, '建议 3：改用 agg_list_tools 看可调用全集');
  assert.match(r.hint, /菜单共 \d+ 个工具/, '必须报出检索空间大小，让调用方知道搜的是多大一个集合');
});

test('零命中：leafPaths 每一条都能被 resolveMenuPath 解析成叶子（不给走不通的路径）', async () => {
  const r = await query(MISS);
  assert.ok(Array.isArray(r.leafPaths) && r.leafPaths.length > 0, '零命中必须给出 leafPaths');
  for (const p of r.leafPaths) {
    const node = await parse(mod.mcpHelpResult({ path: p }));
    assert.ok(Array.isArray(node.tools),
      `${p} 应解析成叶子工具列表，实际: ${JSON.stringify(node).slice(0, 200)}`);
    assert.equal(typeof node.total, 'number', `${p} 应带 total`);
  }
});

test('零命中：leafPaths 覆盖四个叶子一级类别与 game 的 9 个叶子子组', async () => {
  const r = await query(MISS);
  for (const c of ['system', 'unityexplorer', 'mono', 'meta']) {
    assert.ok(r.leafPaths.includes(c), `叶子类别 ${c} 应出现在 leafPaths 里`);
  }
  for (const s of ['state', 'arch', 'mods', 'dpa', 'debugmenu', 'colonist', 'stream', 'world', 'long_task']) {
    assert.ok(r.leafPaths.includes('game_control/game/' + s), `叶子子组 ${s} 应出现在 leafPaths 里`);
  }
  for (const g of ['camera', 'interact', 'query', 'selection', 'ui_panel', 'ui_notice', 'ui_dialog', 'debug_action', 'marker']) {
    assert.ok(r.leafPaths.includes('game_control/game/gameplay/' + g), `孙组 ${g} 应出现在 leafPaths 里`);
  }
  for (const s of ['lua_script', 'ops', 'wait']) {
    assert.ok(r.leafPaths.includes('bridge/' + s), `bridge 子组 ${s} 应出现在 leafPaths 里`);
  }
  assert.equal(r.leafPaths.length,
    4 + 3 + 9 + 9,
    'leafPaths 应恰好 25 条：4 叶子类别 + 3 bridge + 9 game 子组 + 9 gameplay 孙组');
});

test('零命中：给出 nearMisses 并附免责说明（模糊建议，不能当结论）', async () => {
  const r = await query(MISS);
  assert.ok(Array.isArray(r.nearMisses), 'nearMisses 必须是数组（可能为空，不许省略）');
  assert.ok(r.nearMisses.length <= 5, 'nearMisses 最多 5 条');
  assert.match(r.nearMissesNote, /不相关|仅供参考/,
    '必须声明 nearMisses 是模糊建议、可能不相关');
  // 正例防回归：hint 教人"缩短关键词再试"，但本来就该命中的绝不能被这条 hint 教成"搜不到"
  assert.ok((await query('pause_game')).total >= 1, 'query:"pause_game" 必须命中');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/mcpHelpQuery.test.js`

预期：新增的 5 个测试 FAIL——第 1 个通过（零命中已经是正常成功形状，任务 1 就已经对），第 2/3/4/5 个 FAIL，报错形如 `The expression evaluated to a falsy value` / `assert.ok(Array.isArray(r.leafPaths)…)` 失败，因为 `hint` / `leafPaths` / `nearMisses` 字段还不存在。任务 1 的 11 个仍然 PASS。

- [ ] **步骤 3：实现零命中的可行动返回体**

在 `MCP/index.js` 的 `buildMenuQueryPayload` 函数体内，找到任务 1 步骤 7 写入的这一行：

```js
  if (ignoredParams && ignoredParams.length) payload.ignoredParams = ignoredParams;
  return payload;
```

把它整体替换为：

```js
  if (ignoredParams && ignoredParams.length) payload.ignoredParams = ignoredParams;

  // 【零命中 ≠ 不存在】这是本计划唯一可能产出"事实相反结论"的地方，必须给出可行动的下一步。
  // 同源教训见 docs/mcp-吐槽板.md 条目 [11]：search_source 零命中曾被读成"该 vanilla 成员不存在"，
  // 并把这个错误认知写进了源码。这里只用"没匹配上"这个说法，永远不用"没有这个东西"。
  if (hits.length === 0) {
    const unavailableCount = names.filter((n) => toolAvailability(n).state === 'unavailable').length;
    payload.menuUnavailable = unavailableCount;
    payload.hint =
      `菜单共 ${names.length} 个工具，按【工具名 + 描述首行】做了大小写不敏感的子串匹配，0 命中**不等于该能力不存在**（本菜单有 ${unavailableCount} 个工具当前因提供方未就绪而不可调用，它们仍在菜单里、仍会被检索命中，只是标了[当前不可用]）。`
      + '① 缩短关键词再试：工具名里的长词拆一半（如 query:"pause_game" → query:"pause"）；'
      + '② 工具名是英文 snake_case、描述首行是中文，两边都试一遍；'
      + '③ 不想猜关键词就用 path 直接看某一层的完整工具列表，本菜单的全部叶子路径见 leafPaths；'
      + '④ 用 agg_list_tools 看当前**可调用**全集（它只含已就绪提供方的工具，与本菜单不等价）。';
    payload.leafPaths = menuLeafPaths();
    payload.nearMisses = suggestTools(q, 5);
    payload.nearMissesNote = 'nearMisses 是按工具名字符相似度（含双字母组 Dice 系数）给的模糊建议，可能完全不相关，仅供参考，不是结论';
  }

  return payload;
```

- [ ] **步骤 4：运行测试验证通过**

运行：`cd MCP && node --test test/mcpHelpQuery.test.js`

预期：**16 个测试全部 PASS**。

- [ ] **步骤 5：跑全量测试**

运行：`cd MCP && npm test`

预期：全部 PASS。本任务只往返回体加字段，不动任何分支走向与计数，`MCP/test/toolCountConsistency.test.js` 应原样通过。

- [ ] **步骤 6：Commit**

```bash
git add MCP/index.js MCP/test/mcpHelpQuery.test.js
git commit -m "fix(mcp-help): query 零命中给出可行动判读，杜绝把'没搜到'读成'不存在'"
```

---

## 任务 3：让 `query` 真的被看见（5 处发现面文案）

**为什么不能省：** 功能做了但没人调用，等于没做。`query` 的使用者是**靠工具描述决定用什么**的 AI，所以文案必须挂在 AI 必然会读到的地方。

**文件：**
- 修改：`MCP/index.js:2979`（`TOOL_USAGE_NOTES.mcp_help`）
- 修改：`MCP/index.js:3301`（`buildMenuNode()` 的 `note`）
- 修改：`MCP/index.js:3601` 之后（`aggListToolsText()` 插入引导行）
- 修改：`MCP/index.js:3711`（无参概览的 `note`）
- 修改：`MCP/test/mcpHelpQuery.test.js`（追加 3 个测试）

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/mcpHelpQuery.test.js` **末尾追加**：

```js
// ===========================================================================
// 发现面：query 存在的四个入口 + agg_list_tools 的引导行
// ===========================================================================

/** 从 index.js 源码里切出 TOOL_USAGE_NOTES 的某一条（到下一个顶层 "\n  }," 为止） */
function usageNotesEntry(name) {
  const i = source.indexOf(`\n  ${name}: {`);
  if (i < 0) return null;
  const end = source.indexOf('\n  },', i);
  return source.slice(i, end < 0 ? i + 2000 : end);
}

test('发现面一：TOOL_USAGE_NOTES.mcp_help 教 query 用法 + 三级优先级 + 零命中免责', () => {
  const entry = usageNotesEntry('mcp_help');
  assert.ok(entry, 'mcp_help 应在 TOOL_USAGE_NOTES 里有条目');
  assert.match(entry, /query/, 'notes 必须提到 query');
  assert.match(entry, /\{ "query": "pause" \}/, '示例要直接给出可照抄的调用');
  assert.match(entry, /tool > query > path/, 'notes 必须写明三级优先级');
  assert.match(entry, /不等于该能力不存在/, 'notes 必须带零命中免责');
});

test('发现面二/三：概览与子组节点的 note 都把 query 摆在 path 前面', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  assert.match(root.note, /query/, '一级概览的 note 必须提到 query');
  const node = await parse(mod.mcpHelpResult({ path: 'game_control/game' }));
  assert.match(node.note, /query/, '子组节点的 note 必须提到 query');
  // 子组 note 里 query 的位置要早于 path —— AI 是顺着读第一句的
  assert.ok(node.note.indexOf('query') < node.note.indexOf('path'),
    `query 应排在 path 之前，实际: ${node.note}`);
});

test('发现面四：agg_list_tools 首部有一行把用户导向 mcp_help{query:...}', async () => {
  const text = mod.aggListToolsText();
  const head = text.split('\n').slice(0, 4).join('\n');
  assert.match(head, /mcp_help \{ query: "关键词" \}/,
    `全量倾倒的开头应有一行指路（取前 4 行检查），实际开头: ${text.slice(0, 300)}`);
  // 既有断言不能被破坏：这一行不得含旧版 [禁用]，也不得动"提供方状态"那行
  assert.ok(!/\[禁用\]/.test(text), '不得出现旧版 [禁用] 标记');
  assert.match(text, /GABP\/RimBridgeServer=未连接|已连接/, '提供方状态行必须保留');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/mcpHelpQuery.test.js`

预期：新增的 3 个测试 FAIL——第 1 个因 `notes` 里没有 `query`；第 2 个因 `root.note` 里没有 `query`；第 3 个因 `aggListToolsText()` 前 4 行没有引导行。任务 1/2 的 16 个仍然 PASS。

- [ ] **步骤 3：改 `TOOL_USAGE_NOTES.mcp_help`**

把 `MCP/index.js:2979` 整行替换为：

```js
  mcp_help: { example: '{ "query": "pause" }', notes: '**找工具优先传 query**：按【工具名 + 描述首行】做大小写不敏感子串匹配，一次列全（例：query:"pause" 直达 rimworld.pause_game），比逐层 path 下钻省 2 次往返。query 0 命中**不等于该能力不存在**——把关键词缩短、换中英文各试一遍，或照返回体里的 leafPaths 用 path 看某一层。传 tool 查单个工具用法（参数 schema/前置条件/调用示例/易错提示）；传 path 逐级深入子组菜单；无参返回一级概览。三个参数同传按 tool > query > path 生效，query 盖掉 path 时返回体的 ignoredParams 会点名。' },
```

- [ ] **步骤 4：改 `buildMenuNode()` 的 `note`**

把 `MCP/index.js:3301` 整行替换为：

```js
      note: '继续深入请传 path（如 ' + (node.children[0] ? node.children[0].path : '') + '）；叶子节点传 path 返回工具列表；**不知道工具名时直接传 query 按关键词检索（如 query:"pause"），1 次往返就能定位**；查询单个工具传 tool 参数'
```

- [ ] **步骤 5：给 `agg_list_tools` 加一行引导**

在 `MCP/index.js:3601`（`  lines.push('# 本列表 = 当前**可调用**的工具集…');`）**之后**、`3602`（`  lines.push('# 提供方状态：'`）**之前**插入：

```js
  lines.push('# 只想找某个功能的工具？改用 mcp_help { query: "关键词" } —— 按工具名/描述关键词检索，比这份全量倾倒省得多；');
```

> 注意：插入后第 2 行是新引导、第 3 行才是「提供方状态」。`MCP/test/toolchainFixes.test.js:43-47` 的既有断言用的是 `assert.match(text, …)`（全文匹配），不受行号位置影响。

- [ ] **步骤 6：改无参概览的 `note`**

把 `MCP/index.js:3711` 整行替换为：

```js
    note: '多级菜单：**找工具优先传 query**（按工具名/描述关键词一次检索全量，如 query:"pause" 直达 rimworld.pause_game，0 命中不等于该能力不存在）；无参=一级概览；传 path 逐级深入（如 path:"bridge/lua_script"、path:"game_control/game/gameplay/camera"）；传 tool 返回单个工具详情（含 availability 三态）；三者同传按 tool > query > path 生效；'
```

- [ ] **步骤 7：运行测试验证通过**

运行：`cd MCP && node --test test/mcpHelpQuery.test.js`

预期：**19 个测试全部 PASS**。

- [ ] **步骤 8：跑全量测试**

运行：`cd MCP && npm test`

预期：全部 PASS。**重点确认 `MCP/test/toolchainFixes.test.js` 的 §3 三个用例（41-58 行）**——本任务在 `aggListToolsText()` 里插了一行，必须确认「标记语义写明」「提供方状态」「镜像缺席说明」三条断言都没被打破。

- [ ] **步骤 9：Commit**

```bash
git add MCP/index.js MCP/test/mcpHelpQuery.test.js
git commit -m "docs(mcp-help): 工具描述/notes/概览/agg_list_tools 四处写明 query 用法与三级优先级"
```

---

## 人工验收（自动化测试覆盖不到的部分）

下面三条需要真会话确认，**做验收时逐条跑并核对输出**。三条都**只读**，不碰游戏状态。

1. **协议层确实暴露了 `query`（MCP 离线即可）**

   ```bash
   cd MCP
   node --input-type=module -e "process.env.UESD_MCP_NO_START='1'; const m=await import('./index.js'); const {Client}=await import('@modelcontextprotocol/sdk/client/index.js'); const {InMemoryTransport}=await import('@modelcontextprotocol/sdk/inMemory.js'); const s=m._testHooks.createServer(); const [ct,st]=InMemoryTransport.createLinkedPair(); const c=new Client({name:'v',version:'1.0.0'}); await Promise.all([s.connect(st),c.connect(ct)]); const t=(await c.listTools()).tools.find(x=>x.name==='mcp_help'); console.log(JSON.stringify(t.inputSchema,null,2)); await c.close(); await s.close();"
   ```

   预期：`properties` 里有 `query`（`type: "string"`），`tool` / `path` 仍在，`additionalProperties` 仍是 `false`。

2. **吐槽板场景复现：3 次往返变 1 次**

   ```bash
   cd MCP
   node --input-type=module -e "process.env.UESD_MCP_NO_START='1'; const m=await import('./index.js'); const p=await m.mcpHelpResult({query:'pause'}); console.log(p.content[0].text)"
   ```

   预期：`"level": "query"`；`tools` 里有 `- [当前不可用：GABP 未连接(state=disabled)] rimworld.pause_game …`（离线状态下的正常形态——**名字是对的，缺的是提供方**）；`menuTotal` 等于无参概览的 `total`。

3. **零命中的判读（最容易写错的一条）**

   ```bash
   cd MCP
   node --input-type=module -e "process.env.UESD_MCP_NO_START='1'; const m=await import('./index.js'); const p=await m.mcpHelpResult({query:'绝对不可能存在的关键词'}); console.log(p.content[0].text)"
   ```

   预期：`"total": 0`；**没有** `success` 字段；`hint` 里出现「不等于该能力不存在」和 4 条编号建议；`leafPaths` 是 25 条；`nearMisses` 有免责说明。

**回滚方式：** 本计划只碰 `MCP/index.js` 与新建的 `MCP/test/mcpHelpQuery.test.js`，`git revert` 三个 commit 即可，无需重编译模组 DLL、无需重新发布。
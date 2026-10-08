# 打开 `search_debug_actions` 暴露开关并修正菜单挂载 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让调用方在 DebugAction 场景下**默认就走轻量路径**——打开 `search_debug_actions` 这个早已端到端实现完毕、却被 `toolConfig.json` 关掉的工具，并把它从 `gameplay/debug_action` 挪到调用方实际会下钻的 `debugmenu` 分组下；同时给 `rimworld.list_debug_action_children` 接上一个 **opt-in** 的结果投影开关 `summary:true`，让确实需要遍历全树的调用方有办法避开 400KB 载荷。

**架构：** 改三处、零新文件依赖、零 C# 改动。①`MCP/toolConfig.json` 里 `search_debug_actions` 由 `false` 改 `true`（单字段）；②`MCP/index.js` 的 `TOOL_SUBGROUP` 映射把 `search_debug_actions` 从 `game_control/game/gameplay/debug_action` 移到 `game_control/game/debugmenu`；③在 `MCP/index.js` 新增一张按镜像名索引的投影器表 `GABP_RESULT_PROJECTORS`，由 GABP 镜像统一派发处（`index.js:4350`）取出并交给 `callGABPTool` **早已存在**的 `filterResult` 钩子。③采用 **opt-in**（`summary:true`）而非改默认，理由见 §0.4；采用**按名剔除** `node`/`source`/`execution`/`operation` 四个重块而非白名单挑选，理由见 §0.5。最后修正 `README.md` 里指向已删除工具的 DebugAction 表。

**技术栈：** Node.js ESM（`MCP/package.json` `"type": "module"`）、`node:test` + `node:assert/strict`（项目现有测试栈，`MCP/package.json:9`）。**不改任何 C#，不重编译模组 DLL，不启动游戏，不调用任何会改游戏状态的 MCP 工具。**

---

## 0. 给零上下文执行者的背景（不要跳过）

### 0.1 这条要修的到底是什么

来源是 `docs/mcp-吐槽板.md` 的 `## [03] list_debug_action_children 返回 400KB+ 🔴` 条目（正文第 **417-436** 行；用标题文本 `^## \[03\]` 做锚点定位，不要依赖行号——该文件正在被并行会话改写）。原文关键段：

> `agg_call_tool{tool:"rimworld.list_debug_action_children", args:{path:"Actions"}}` 返回 **400,385 字节**（377 个子节点），harness 侧被 spill 到临时文件。**每个叶子节点都重复了一整套 `node` + `source` + `execution` + `operation` 结构。**
>
> 建议：加**过滤/深度/只取路径**的参数，例如 `?filter=Stage`、`?depth=1`、`?pathsOnly=true`。或默认只回 `path` + `label` + `hasChildren`。

上游裁决报告：`docs/issue-triage/triage/03-list-debug-action-children-payload.md`，裁决 **值得修**（现象 🔴、机制 🟠，单次约 100K-115K token）。

### 0.2 ⚠️ 这**不是**功能开发——吐槽板要的"过滤参数"本仓库早已实现

**执行者必须先建立这个认知，否则会白忙。** 吐槽板建议的"轻量返回"在本仓库里**端到端已经做完了**：

- MCP 侧工具定义 —— `MCP/index.js:2355-2372`（`name: 'search_debug_actions'` 在 **2356**，描述在 **2357**）
- MCP 侧派发实现 —— `MCP/index.js:4934`（`case 'search_debug_actions': {`，走 `callUnityExplorerAPI('/debugaction/search?...')`）
- 游戏侧 HTTP 端点 —— `Source/UELoader/UEHttpServer.cs:460-466`（`case "debugaction/search":`）
- 游戏侧实现 —— `Source/UELoader/UEDebugActions.cs:232-241`，每条结果**只有 4 个字段**：`path` / `label` / `category` / `actionType`（外加动态菜单时的 `dynamicMenu`）。**没有 `node` / `source` / `execution` / `operation`。**

**真正坏掉的只有两处**：

| # | 症状 | 位置 |
|---|---|---|
| ① | `search_debug_actions` 在 `MCP/toolConfig.json` 里是 `false` → 工具**根本没出现在 `listTools` 里**，直接调用会被 `index.js:5117` 拦成"工具已禁用" | `MCP/toolConfig.json`（**单行压缩 JSON，全文只有 1 行**，只能按 key 定位） |
| ② | `mcp_help` 把它挂在 `game_control/game/gameplay/debug_action`，而不是 `debugmenu` → 调用方照文档下钻 `game_control/game/debugmenu` 时**永远看不到那个轻量替代** | `MCP/index.js:3117`（对照 `MCP/index.js:3158-3164` 的 debugmenu 分组） |

**本计划没有"实现 `filter`/`depth`/`pathsOnly` 参数"这种任务。** 上游 RimBridgeServer 的 `rimworld/list_debug_action_children` 只有 `path` + `includeHidden` 两个参数（`RimBridgeServer-main/docs/tool-reference.md:275-281`），要加真正的分页得改上游，本计划不做——那会与已有的 `search_debug_actions` 功能重叠。**任务 1 和任务 2 才是本条的主要修复。**

### 0.3 运行时证据：这条路径真的被走过

`MCP/logs/mcp-20261001-093829.log:46-50`（该日志是**只读副本**，本计划只引用不修改）记录了吐槽板描述的同一次会话：

```
46: tools/call mcp_help args={path=game_control/game/debugmenu} → 2ms ok
47: tools/call agg_call_tool tool=rimworld.list_debug_action_roots → 38ms success
48: tools/call agg_call_tool tool=rimworld.get_debug_action args={"children":"true","path":"Actions/FlightAStar"} → 195ms success
49: tools/call agg_call_tool tool=rimworld.list_debug_action_children args={"path":"Actions"} → 60ms success
```

第 46 行正是"照 debugmenu 路径下钻"——而该分组里（`index.js:3158-3164`）今天**没有** `search_debug_actions`，于是第 49 行直接吃了全量。

### 0.4 ⚠️ 与裁决报告的一处**有意分歧**：投影做成 opt-in，不改默认

裁决报告 `triage/03-...md:192` §4.3 建议"默认投影成 `{path, label, hasChildren}`，保留 `?verbose=true` 走全量"。**本计划不采纳"改默认"，改用 `summary:true` 显式开启。**

理由：`list_debug_action_children` 是**已上线**的工具，改默认会静默改变它的响应形状。任何依赖 `children[].node` / `children[].execution` 字段做筛选的既有自动化，会在不报错的情况下拿到 `undefined` —— 这正是 `AGENT-BRIEF.md:123` 描述的「🔴 会让自动化得出事实相反的结论（假成功 / 假阴性）」那一类危害，**比 400KB 更难排查**。改成 opt-in 后，投影是纯增量的转义阀：默认行为逐字不变，风险为零。

### 0.5 投影采用「按名剔除四个重块」而非「白名单挑选」

`RimBridgeServer-main` 这个副本在 `C:\SteamLibrary\steamapps\common\RimWorld\RimBridgeServer-main` 下**只有 `Tests/` 和 `docs/`，没有产出 `children` 的 C# 源码**（已核实：`Get-ChildItem -Recurse -Filter *.cs` 只在 `Tests\RimBridgeServer.LiveSmoke\` 下命中）。能确认的只有：

- 顶层是 `children` 数组 —— `Tests\RimBridgeServer.LiveSmoke\SmokeScenarioCatalog.cs:405`（`JsonNodeHelpers.ReadArray(outputChildren.StructuredContent, "children")`）
- 子节点内嵌 `node` / `execution` 结构 —— 同文件 `:417`（`ReadBoolean(..., "node", "execution", "supported")`）
- 四个重块的字段名来自吐槽板原文：`node` + `source` + `execution` + `operation`

**因此不能按白名单硬挑字段**（在无 RBS 源码的机器上挑就是猜）。任务 3 用**按名剔除**：只删 `node` / `source` / `execution` / `operation` 这四个已确认的重块，其余字段一律原样保留。这样 RBS 日后新增字段也不会被误删，投影在字段未知时依然成立。

### 0.6 环境事实（已定死，不要重新验证）

- **本仓库不是 git 仓库** ⇒ 每个任务末尾的"Commit"步骤统一换成 **改动清单**。
- **`runtime/` 下没有 `index.js` 副本** ⇒ 改 `MCP/index.js` 就是改到生效的那份，**无需构建、无需部署**。
- **测试栈**：`node:test` + `node:assert/strict`，跑法 `Set-Location MCP; npm test`（脚本定义在 `MCP/package.json:9`）。
- **只改 MCP 侧**。不碰 `Source/`、不碰 `Assemblies/`、不碰任何 `.cs`、不碰 `Languages/`。

---

## 1. 文件清单

**修改（3 个文件）：**

| 文件 | 职责 | 本计划改什么 |
|---|---|---|
| `MCP/toolConfig.json` | 每个工具一个 `bool` 开关，控"是否直接暴露给 AI" | `tools.search_debug_actions`：`false` → `true`；同步 `_comment` 的清单描述与总数 |
| `MCP/index.js` | MCP 服务器主文件（工具定义 / 菜单映射 / 派发） | ①`TOOL_SUBGROUP` 里挪一行菜单路径（3117 → 3164 区块）；②新增 `GABP_RESULT_PROJECTORS` 投影器表 + 改 4350 派发行；③`TOOL_USAGE_NOTES` 加一条镜像说明 |
| `README.md` | 对外文档 | 修 `131-141` 的 DebugAction 表：删 3 行已删工具、补镜像入口、标注需开启 |

**创建（1 个文件）：**

| 文件 | 职责 |
|---|---|
| `MCP/test/debugActionExposure.test.js` | 本计划全部断言的载体：暴露开关、菜单归位、计数守恒、投影器纯函数 |

**不新建任何生产代码文件。** 投影器以纯函数形式就地放进 `MCP/index.js` 并加进 `5416-5443` 的 `export { ... }` 块——该块已经是本项目暴露纯函数给单测的既有约定（`callGABPTool` 在 `5422` 就在里面）。

---

## 2. 任务 1：打开 `search_debug_actions` 的暴露开关

**文件：**
- 修改：`MCP/toolConfig.json`（**全文 1 行**，用 JSON key 定位，不要按行号编辑）
- 测试：`MCP/test/debugActionExposure.test.js`（新建）

**交付物：** `search_debug_actions` 出现在 `listTools` 里，可被直接调用。

- [ ] **步骤 1：编写失败的测试**

新建 `MCP/test/debugActionExposure.test.js`，**完整内容如下**（后续任务继续往这个文件里追加 `test(...)`，不要新建第二个测试文件）：

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器/桥接
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const readToolConfig = () =>
  JSON.parse(fs.readFileSync(path.join(here, '..', 'toolConfig.json'), 'utf-8'));

// mcpHelpResult 返回 { content: [{ text }] }，统一解包
const parse = async (value) => JSON.parse(
  await Promise.resolve(value).then((r) =>
    typeof r === 'string' ? r : (r.content || []).map((c) => c.text || '').join('')));

// 从叶子行 "- [标记] 工具名 描述" 里取工具名（与 toolCountConsistency.test.js:20 同口径）
const nameOfLine = (line) => line.replace(/^-\s*/, '').replace(/^\[[^\]]*\]\s*/, '').split(/\s+/)[0];

// ===========================================================================
// 吐槽板 [03]：search_debug_actions 是 list_debug_action_children 的轻量替代，
// 每条只回 path/label/category/actionType（Source/UELoader/UEDebugActions.cs:232-241）。
// 它早已端到端实现完毕（index.js:2356 定义 / index.js:4934 派发 /
// UEHttpServer.cs:460 端点），却因 toolConfig.json 开关为 false 而从未暴露。
// ===========================================================================

test('search_debug_actions 在 toolConfig.json 中为 true（轻量替代已暴露）', () => {
  const cfg = readToolConfig();
  assert.equal(cfg.tools.search_debug_actions, true,
    'search_debug_actions 必须开启：关闭时调用方只能吃 list_debug_action_children 的 400KB 全量载荷');
});

test('toolConfig.json 的 _comment 与实际开启清单一致（防止再次漂移）', () => {
  const cfg = readToolConfig();
  const enabled = Object.values(cfg.tools).filter((v) => v === true).length;
  assert.equal(enabled, 25, '开启清单应为 25 个（10 压缩 + 13 GABP 镜像 + search_debug_actions + post_map_marker）');
  assert.ok(cfg._comment.includes('共 25 个'),
    '_comment 必须同步声明开启总数，否则下一位维护者又会按陈旧数字判断暴露面');
  assert.ok(cfg._comment.includes('search_debug_actions'),
    '_comment 必须点名 search_debug_actions，说明它是 DebugAction 轻量搜索入口');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; node --test test/debugActionExposure.test.js
```

预期：2 个 test **全部 FAIL**。第一个报 `AssertionError: search_debug_actions 必须开启…`（实际值 `false`），第二个报 `AssertionError: 开启清单应为 25 个`（实际值 `24`）。

- [ ] **步骤 3：改 `toolConfig.json` 的开关**

`MCP/toolConfig.json` 是**单行压缩 JSON**（`[System.IO.File]::ReadAllLines()` 返回长度为 1）。**不要试图按行编辑**——整份文件就是第 1 行。用文本替换精确改动两处：

第一处，把 `tools` 对象里的

```
"search_debug_actions":false
```

改成

```
"search_debug_actions":true
```

第二处，把 `_comment` 的值整体替换为下面这个新字符串（注意：原来那句里"13 个恢复暴露的 GABP 镜像（…）共 24 个（+ post_map_marker：…）"的写法会让总数读起来含糊，新版把每一项都摊开写）：

```
每个工具一个 bool 开关：true=该工具直接暴露给 AI（出现在工具列表 listTools），false=不暴露（从工具列表消失，但仍可经 agg_call_tool 按名调用，不影响使用）。默认暴露清单 = 压缩 10 个（agg_list_tools/agg_call_tool/mcp_help/start_game/stop_game/get_game_status/read_rimworld_log/tail_rimworld_log/get_game_info/start_quick_test）+ 13 个恢复暴露的 GABP 镜像（rimworld.list_colonists/rimworld.list_mods/rimworld.save_game/rimworld.load_game/rimworld.set_time_speed/rimworld.clear_selection/rimworld.list_debug_action_roots/rimworld.list_debug_action_children/rimworld.get_debug_action/rimworld.jump_camera_to_cell/rimworld.set_camera_zoom/rimworld.select_pawn/rimworld.execute_debug_action）+ 1 个 DebugAction 轻量搜索 search_debug_actions（每条只回 path/label/category/actionType，是 list_debug_action_children 全量载荷的替代，找动作路径优先用它）+ post_map_marker（地图坐标光标，UE 实现/自研 shader），共 25 个；其余工具默认关闭；修改后需重启 MCP 服务器生效。
```

改完立刻自检 JSON 没写坏（下面这条命令只读文件，不改任何东西）：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
$j = Get-Content .\toolConfig.json -Raw | ConvertFrom-Json
"search_debug_actions = $($j.tools.search_debug_actions)"
"enabled count       = $((@($j.tools.PSObject.Properties | Where-Object { $_.Value -eq $true })).Count)"
```

预期输出：`search_debug_actions = True` / `enabled count = 25`。

- [ ] **步骤 4：运行测试验证通过**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; node --test test/debugActionExposure.test.js
```

预期：2 个 test 全部 PASS。

- [ ] **步骤 5：跑全量测试确认没碰坏别的**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; npm test
```

预期：全部 PASS，**包括 `test/toolCountConsistency.test.js` 全部 8 个用例**。它们不会失败——见 §3 的「连带影响」一节：`menuUniverseNames()`（`index.js:3369-3376`）和 `allToolNames()`（`index.js:3728-3735`）**都不调用** `isToolEnabled`，菜单宇宙与可调用集的大小和开关无关。

- [ ] **步骤 6：改动清单（本仓库不是 git 仓库，用它代替 Commit）**

| 文件 | 改动 |
|---|---|
| `MCP/toolConfig.json` | `tools.search_debug_actions`：`false`→`true`；`_comment` 重写为摊开计数的版本 |
| `MCP/test/debugActionExposure.test.js` | 新建，2 个用例 |

**连带影响（任务 1 单独带来的）：**
- **对外工具表**：25 → 26 个？**不是**。任务 1 之前是 24 个开启，改后是 **25 个**（新增的 1 个就是 `search_debug_actions` 本身）。
- `mcp_help` 里 `search_debug_actions` 的可用性标记从 `[经 agg_call_tool 可用]`（`index.js:3467`）变成 `[已暴露]`（`index.js:3466`），描述由"经 agg_call_tool 调用"变成"已暴露，可直接调用"（`index.js:3459-3461`）。
- 直接调用 `search_debug_actions` 不再被 `index.js:5117` 拦成"工具已禁用"。
- **菜单计数、层级、`toolCountConsistency.test.js` 的任何断言：零变化。** `menuUniverseNames()` 靠 `allToolEntries()`（`index.js:3352-3356`）+ `TOOL_SUBGROUP` 的已登记 key 构造，与 `isToolEnabled` 无关；`search_debug_actions` 本来就在 `TOOL_SUBGROUP`（`index.js:3117`）且已登记在 `toolConfig.tools` 里，改开关前后都已在宇宙中。
- **生效方式：需重启 MCP 服务器**（`_comment` 原本就写明"修改后需重启 MCP 服务器生效"）。测试全部走 `import` 进程内状态，不受此影响。

---

## 3. 任务 2：把 `search_debug_actions` 挂到 `debugmenu` 分组

**文件：**
- 修改：`MCP/index.js:3115-3119`（删一行）、`MCP/index.js:3158-3164`（加一行，并把分组注释的计数从 `（6）` 改成 `（7）`）
- 测试：`MCP/test/debugActionExposure.test.js`（追加用例）

**交付物：** 照 `mcp_help{path:"game_control/game/debugmenu"}` 下钻能看到轻量替代；且每个工具名**恰好**归入一个分组（这是本文件计数口径不重复数的硬约束，见 `index.js:3378-3381` 的历史 bug 注释）。

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/debugActionExposure.test.js` **末尾**追加下面 4 个用例（文件顶部已定义的 `parse` / `nameOfLine` 直接复用，不要重复声明）：

```js
// ===========================================================================
// 吐槽板 [03] 的第二个病灶：菜单挂错位置。
// 日志 mcp-20261001-093829.log:46 显示调用方下钻的是 game_control/game/debugmenu，
// 而 search_debug_actions 挂在 game_control/game/gameplay/debug_action —— 永远看不到。
// ===========================================================================

test('search_debug_actions 挂在 debugmenu 分组（照文档下钻能看到轻量替代）', async () => {
  const leaf = await parse(mod.mcpHelpResult({ path: 'game_control/game/debugmenu' }));
  const names = (leaf.tools || []).map(nameOfLine);
  assert.ok(names.includes('search_debug_actions'),
    'debugmenu 叶子必须列出 search_debug_actions，否则按文档路径下钻永远看不到轻量替代');
  assert.ok(names.includes('rimworld.list_debug_action_children'),
    '全量工具必须与轻量替代同组出现，调用方才能在同一屏里做取舍');
});

test('search_debug_actions 已从 gameplay/debug_action 移出（一个名字只归一个分组）', async () => {
  const leaf = await parse(mod.mcpHelpResult({ path: 'game_control/game/gameplay/debug_action' }));
  const names = (leaf.tools || []).map(nameOfLine);
  assert.ok(!names.includes('search_debug_actions'),
    '不得同时挂在两个分组：菜单按"恰好归一个一级类别"计数，重复挂会虚增总数');
});

test('search_debug_actions 的可用性三态为 exposed（任务 1 的开关已生效）', () => {
  const a = mod.toolAvailability('search_debug_actions');
  assert.equal(a.state, 'exposed');
  assert.equal(a.reason, '已暴露，可直接调用');
});

test('菜单计数在跨分组移动后守恒（守住 toolCountConsistency 的四套口径）', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  // ① 概览自洽
  assert.equal(root.total, root.categories.reduce((a, c) => a + c.count, 0));
  // ② 概览 total == menuVisible == 菜单宇宙大小
  assert.equal(root.menuVisible, root.total, 'menuVisible 必须等于 total');
  assert.equal(root.total, mod.menuUniverseNames().length);
  // ③ game_control 概览计数 == 各子组叶子 total 之和（这次移动最容易破坏的就是这条）
  const gc = root.categories.find((c) => c.path === 'game_control');
  let leafSum = 0;
  for (const s of root.game_control.gameSubgroups) {
    const leaf = await parse(mod.mcpHelpResult({ path: 'game_control/game/' + s.name }));
    leafSum += leaf.total;
  }
  assert.equal(gc.count, leafSum, `game_control 计数 ${gc.count} != 各子组叶子合计 ${leafSum}`);
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; node --test test/debugActionExposure.test.js
```

预期：6 个用例里 **2 个 FAIL**——`search_debug_actions 挂在 debugmenu 分组…` 报"debugmenu 叶子必须列出 search_debug_actions"；`search_debug_actions 已从 gameplay/debug_action 移出…` 报"不得同时挂在两个分组"。另外 4 个 PASS（`toolAvailability` 那条在任务 1 之后就该绿了）。计数守恒那条此时也 PASS——因为改动还没做。

- [ ] **步骤 3：移动菜单映射**

`MCP/index.js:3115-3119`，把

```javascript
  // debug_action（4，原 debugaction）
  'get_debug_action_categories': 'game_control/game/gameplay/debug_action',
  'search_debug_actions': 'game_control/game/gameplay/debug_action',
  'get_map_structure': 'game_control/game/gameplay/debug_action',
  'search_map_structure': 'game_control/game/gameplay/debug_action',
```

改成（删掉 `search_debug_actions` 那一行，注释计数 4→3）：

```javascript
  // debug_action（3，原 debugaction；search_debug_actions 已移至 debugmenu 分组，
  // 因为它是 list_debug_action_children 的轻量替代，必须与全量遍历工具同组出现）
  'get_debug_action_categories': 'game_control/game/gameplay/debug_action',
  'get_map_structure': 'game_control/game/gameplay/debug_action',
  'search_map_structure': 'game_control/game/gameplay/debug_action',
```

`MCP/index.js:3158-3164`，把

```javascript
  // debugmenu（6）
  'rimworld.spawn_thing': 'game_control/game/debugmenu',
  'rimworld.set_draft': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_roots': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_children': 'game_control/game/debugmenu',
  'rimworld.get_debug_action': 'game_control/game/debugmenu',
  'rimworld.execute_debug_action': 'game_control/game/debugmenu',
```

改成（加一行 `search_debug_actions`，注释计数 6→7）：

```javascript
  // debugmenu（7：6 镜像 + search_debug_actions 轻量搜索，与全量遍历同组）
  'rimworld.spawn_thing': 'game_control/game/debugmenu',
  'rimworld.set_draft': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_roots': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_children': 'game_control/game/debugmenu',
  'rimworld.get_debug_action': 'game_control/game/debugmenu',
  'rimworld.execute_debug_action': 'game_control/game/debugmenu',
  'search_debug_actions': 'game_control/game/debugmenu',
```

**只改这两处。** 不要动 `index.js:2916`（`TOOL_CATEGORY` 里的 `search_debug_actions: 'game_control'`——一级分类本来就是 `game_control`，跨子组移动不影响它），也不要动 `index.js:2933`（`TOOL_USAGE_NOTES`，任务 3 会加一条镜像的）。

- [ ] **步骤 4：运行测试验证通过**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; node --test test/debugActionExposure.test.js
```

预期：6 个用例全部 PASS。

- [ ] **步骤 5：跑全量测试**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; npm test
```

预期：全部 PASS。特别确认 `test/toolCountConsistency.test.js` 的 8 个用例全绿——第 4 个用例（`toolCountConsistency.test.js:64-77`，`game_control 概览计数 == 子组计数之和 == 各子组叶子 total 之和`）是这次移动的主要风险点，第 3 个（`:34-62`）次之。**守恒是结构性的**：`menuCountBySubgroup`（`index.js:3392-3398`）按 `TOOL_SUBGROUP[n]` 的前缀归属计数，一个名字从 `gameplay/debug_action` 挪到 `debugmenu`，前者 -1、后者 +1，`game_control/game` 的合计与 `root.total` 均不变。

- [ ] **步骤 6：确认没碰碎两处源码切片断言**

`MCP/test/toolchainFixes.test.js` 里有两处对 `index.js` 做**字符串切片**的断言，它们对行号漂移敏感。任务 2 的改动位置在 3117 与 3164，都在两处切片的范围之外，但仍需实测确认：

- `test/toolchainFixes.test.js:120-124`：切 `source.indexOf('function toolListLine(')` 到 `source.indexOf('function buildMenuNode(')`，断言切片里有 `toolAvailabilityTag(name)` 且没有 `[禁用]`。**这两个函数在 `index.js:3281` / `3290`，都在 3117 和 3164 之后，本次改动不影响它们之间的相对内容。**
- `test/toolchainFixes.test.js:263-268`：切 `case 'start_game': {` 到 `case 'stop_game': {`、再到 `case 'get_game_status': {`，断言两个块里都有 `dpaRoundActive = false`。**这两个 case 在 `index.js:4356` / `4371` / `4384`，用 `indexOf` 按文本定位，本次在其前方插入行不会影响。**

实测命令（跑一次即可，断言通过就说明没碰碎）：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; node --test test/toolchainFixes.test.js
```

预期：全部 PASS。

- [ ] **步骤 7：改动清单（本仓库不是 git 仓库，用它代替 Commit）**

| 文件 | 改动 |
|---|---|
| `MCP/index.js:3115-3119` | 删掉 `'search_debug_actions': 'game_control/game/gameplay/debug_action'` 一行；注释 `debug_action（4，…）` → `（3，…）` 并说明去向 |
| `MCP/index.js:3158-3164` | 末尾加 `'search_debug_actions': 'game_control/game/debugmenu'`；注释 `debugmenu（6）` → `（7：6 镜像 + search_debug_actions 轻量搜索…）` |
| `MCP/test/debugActionExposure.test.js` | 追加 4 个用例 |

**连带影响（任务 2 单独带来的）：**
- **菜单层级**：`game_control/game/debugmenu` 叶子 6 → **7**；`game_control/game/gameplay` 4 → **3**；`gameplay/debug_action` 叶子 4 → **3**。
- **`root.total` / `menuVisible` / `callableTotal` / `concealedCount`：全部不变**（跨分组移动，宇宙大小守恒）。
- **`toolCountConsistency.test.js`：不会失败。** 8 个用例都是自洽性断言（总计 == 各分类之和 == 菜单宇宙大小 == 子组叶子之和），对"哪个工具在哪个子组"不敏感。唯一会被计数影响的断言风格是"某子组恰好有 N 个工具"的硬编码——**本文件里没有这种断言**（已核实 8 个用例逐条读过）。
- **连带影响（任务 1 单独带来的）：** 对外暴露工具 24 → **25** 个；`search_debug_actions` 的可用性标记从 `[经 agg_call_tool 可用]` 变 `[已暴露]`。菜单宇宙与 `listTools` 计数口径互相独立，不存在需要同步改的第二个数字。

---

## 4. 任务 3：给 `list_debug_action_children` 接上 opt-in 结果投影

**文件：**
- 修改：`MCP/index.js:4349-4352`（派发行）、`MCP/index.js:2740` 上方（新增投影器表）、`MCP/index.js:2930-2933` 附近（加镜像的 usage note）、`MCP/index.js:5443`（`export` 块追加）
- 测试：`MCP/test/debugActionExposure.test.js`（追加用例）

**交付物：** `agg_call_tool{tool:"rimworld.list_debug_action_children", args:{path:"Actions", summary:true}}` 返回**只含轻量字段**的 `children`，而**不带 `summary:true` 的调用逐字不变**。

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/debugActionExposure.test.js` **末尾**追加：

```js
// ===========================================================================
// 吐槽板 [03] 的第三条：给已经存在却一直传 undefined 的 filterResult 钩子接上投影。
// 钩子本体在 index.js:2747（签名）与 2761-2763（调用点），GABP 镜像统一派发处
// index.js:4350 一直传 undefined。以下用例全部是纯函数断言，不触碰任何游戏状态。
// ===========================================================================

const HEAVY = ['node', 'source', 'execution', 'operation'];

// 造一个贴近真实形状的载荷：顶层 children 数组，每个子节点带一整套重块
const fakePayload = () => ({
  path: 'Actions',
  children: [
    { path: 'Actions/FlightAStar', label: 'Flight A*', category: 'Debug', node: { actionType: 'Menu' }, source: { x: 1 }, execution: { supported: true }, operation: { id: 3 } },
    { path: 'Actions/Run All Tests', label: 'Run All Tests', category: 'Debug', node: { actionType: 'Action' }, source: { x: 2 }, execution: { supported: true }, operation: { id: 4 } }
  ]
});

test('投影器：summary:true 时剔掉 node/source/execution/operation 四个重块', () => {
  const proj = mod.gabpResultProjector('rimworld/list_debug_action_children', { path: 'Actions', summary: true });
  assert.equal(typeof proj, 'function', '显式要 summary 时必须给出投影器');
  const out = proj(fakePayload());
  assert.equal(out.children.length, 2, '子节点数量不得改变');
  assert.equal(out.path, 'Actions', '顶层字段必须保留');
  for (const c of out.children) {
    for (const h of HEAVY) {
      assert.ok(!(h in c), `重块 ${h} 必须被剔除`);
    }
  }
});

test('投影器：轻量字段 path/label/category 必须原样保留', () => {
  const proj = mod.gabpResultProjector('rimworld/list_debug_action_children', { summary: true });
  const out = proj(fakePayload());
  assert.deepEqual(out.children.map((c) => c.path), ['Actions/FlightAStar', 'Actions/Run All Tests']);
  assert.deepEqual(out.children.map((c) => c.label), ['Flight A*', 'Run All Tests']);
  assert.deepEqual(out.children.map((c) => c.category), ['Debug', 'Debug']);
});

test('投影器：补一个 childCount，调用方不用自己数数组长度', () => {
  const proj = mod.gabpResultProjector('rimworld/list_debug_action_children', { summary: true });
  assert.equal(proj(fakePayload()).childCount, 2);
});

test('投影器：未登记的镜像名返回 undefined（不投影）', () => {
  assert.equal(mod.gabpResultProjector('rimworld/get_debug_action', { summary: true }), undefined);
  assert.equal(mod.gabpResultProjector('rimworld/list_debug_action_roots', { summary: true }), undefined);
});

test('投影器：没显式传 summary:true 时返回 undefined（默认行为逐字不变）', () => {
  const RBS = 'rimworld/list_debug_action_children';
  for (const args of [undefined, {}, { path: 'Actions' }, { path: 'Actions', summary: false },
                      { path: 'Actions', summary: 'true' }, { path: 'Actions', summary: 1 }]) {
    assert.equal(mod.gabpResultProjector(RBS, args), undefined,
      `args=${JSON.stringify(args)} 不得触发投影`);
  }
});

test('投影器：载荷形状不符（无 children 数组）时原样返回，不猜不抛', () => {
  const proj = mod.gabpResultProjector('rimworld/list_debug_action_children', { summary: true });
  const weird = { path: 'Actions', note: '上游改了形状' };
  assert.equal(proj(weird), weird, '形状不符必须原样透传，不能返回 undefined 让调用方误判');
  assert.equal(proj(null), null);
  assert.equal(proj('not-an-object'), 'not-an-object');
});

test('投影器：summary 是本层控制参数，不得出现在转发给游戏的 args 里', () => {
  const source = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8');
  const i = source.indexOf("const _r = await callGABPTool(_rbs,");
  assert.ok(i > 0, '找不到 GABP 镜像统一派发行');
  const block = source.slice(i - 400, i + 200);
  assert.match(block, /delete fwdArgs\.summary/,
    'summary 必须在转发前删掉：RBS 的 list_debug_action_children 只认 path/includeHidden，多传会报错');
  assert.match(block, /gabpResultProjector\(_rbs,/,
    '派发处必须把投影器接进 callGABPTool 的第三个参数');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; node --test test/debugActionExposure.test.js
```

预期：前 6 个用例 PASS，新增的 7 个里 **6 个 FAIL**，报 `TypeError: mod.gabpResultProjector is not a function`（因为 `index.js` 还没导出它）；最后一个用例报 `找不到 GABP 镜像统一派发行` 之类的断言失败。

- [ ] **步骤 3：实现投影器**

在 `MCP/index.js:2739`（`callGABPTool` 上方注释块的第一行之前）插入下面整段。**插入位置必须在 `callGABPTool` 函数定义（2747）之前，因为它是该函数的数据依赖**：

```javascript
// ===== GABP 镜像结果投影（callGABPTool 的 filterResult 钩子，§index.js:2747/2761-2763）=====
// 吐槽板 [03]：rimworld/list_debug_action_children 对 path:"Actions" 返回 400,385 字节（377 个子节点），
// 每个子节点重复一整套 node + source + execution + operation，单次约吃掉 10 万 token 级上下文。
// 上游 RBS 的该工具只有 path / includeHidden 两个参数，没有 filter/depth/limit
// （RimBridgeServer-main/docs/tool-reference.md:275-281），所以瘦身只能在本仓库这层做。
//
// 【设计取舍：opt-in，不改默认】裁决报告建议"默认投影成 {path,label,hasChildren}"，这里不采纳：
// list_debug_action_children 是已上线工具，改默认会静默改变响应形状，依赖 children[].node/execution
// 的既有自动化会在不报错的情况下拿到 undefined —— 那比 400KB 更难排查（AGENT-BRIEF §5.2 的「假阴性」）。
// 所以必须由调用方显式传 summary:true 打开，默认逐字不变。
//
// 【设计取舍：按名剔除，不按白名单挑选】本机没有 RBS 的产出源码（RimBridgeServer-main 只有 Tests/ 和 docs/），
// 除 node/execution 外无法确证还有哪些字段是"轻量"的。白名单会在字段猜错时静默丢信息；
// 按名只剔除吐槽板实测确认的 4 个重块，既能砍掉主体体积，又不会误删 RBS 日后新增的字段。
const DEBUG_ACTION_CHILD_HEAVY_FIELDS = ['node', 'source', 'execution', 'operation'];

function projectDebugActionChildren(data) {
  // 形状不符就原样透传：宁可不投影，也不要返回一个让调用方误判的残缺结构
  if (!data || typeof data !== 'object' || !Array.isArray(data.children)) return data;
  return {
    ...data,
    childCount: data.children.length,
    children: data.children.map((c) => {
      const out = {};
      for (const [k, v] of Object.entries(c || {})) {
        if (!DEBUG_ACTION_CHILD_HEAVY_FIELDS.includes(k)) out[k] = v;
      }
      return out;
    })
  };
}

// 镜像名（RBS 原名，斜杠形式）→ 投影器。**只登记确实需要瘦身的镜像**，不搞全局默认投影。
const GABP_RESULT_PROJECTORS = {
  'rimworld/list_debug_action_children': projectDebugActionChildren
};

// 挑投影器：镜像没登记、或调用方没显式要 summary → undefined（= 不投影，行为与今天完全一致）
function gabpResultProjector(rbsName, args) {
  const proj = GABP_RESULT_PROJECTORS[rbsName];
  if (!proj) return undefined;
  if (!args || args.summary !== true) return undefined;
  return (data) => proj(data);
}
```

- [ ] **步骤 4：接进派发处**

`MCP/index.js:4350`，把

```javascript
    const _r = await callGABPTool(_rbs, args, undefined, _opts);
```

改成

```javascript
    // summary 是本层投影开关，不转发给游戏：RBS 只认 path / includeHidden，多传会报错。
    // 做法与上面 dpa_patch_methods 剥 force 完全同构（index.js:4343-4345）。
    const fwdArgs = { ...(args || {}) };
    delete fwdArgs.summary;
    const _r = await callGABPTool(_rbs, fwdArgs, gabpResultProjector(_rbs, args), _opts);
```

- [ ] **步骤 5：导出投影器供单测直接断言**

`MCP/index.js:5439-5443`，把

```javascript
  // 2026-09-20 计数口径修复：菜单/计数单一事实源，供一致性测试直接断言
  allToolNames,
  menuUniverseNames,
};
```

改成

```javascript
  // 2026-09-20 计数口径修复：菜单/计数单一事实源，供一致性测试直接断言
  allToolNames,
  menuUniverseNames,
  // 吐槽板 [03]：GABP 镜像结果投影器（纯函数），供单测直接断言，不需起游戏
  gabpResultProjector,
};
```

- [ ] **步骤 6：补 usage note，让 `summary` 可被发现**

`MCP/index.js:2933`（`search_debug_actions` 那行）**之后**、`search_map_structure` 那一行**之前**，插入一条以镜像名为 key 的 note：

```javascript
  'rimworld.list_debug_action_children': { example: '{ "path": "Actions", "summary": true }', notes: '全量遍历会回 400KB 级载荷（每个子节点重复 node/source/execution/operation）；找动作路径请优先用 search_debug_actions。确需遍历时传 summary:true 剔除这四个重块（仅回 path/label/category 等轻量字段），summary 是本层开关、不会转发给游戏' },
```

**为什么 key 用带前缀的镜像名能用**：`TOOL_USAGE_NOTES` 在 `mcpHelpResult` 的 `tool` 分支里是**按名字直接查表**（`index.js:3667`：`...(TOOL_USAGE_NOTES[meta.name] || {})`），不剥前缀，所以在 `mcp_help{tool:"rimworld.list_debug_action_children"}` 下能直接命中。

- [ ] **步骤 7：运行测试验证通过**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; node --test test/debugActionExposure.test.js
```

预期：13 个用例全部 PASS。

- [ ] **步骤 8：跑全量测试**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; npm test
```

预期：全部 PASS。

**⚠️ 本任务唯一有风险的切片断言，务必确认**：`test/toolchainFixes.test.js:254-261` 断言

```javascript
const i = source.indexOf("if (name === 'rimworld.dpa_patch_methods') {");
const block = source.slice(i, i + 700);
assert.match(block, /dpaPatchGuard\(args\)/);
assert.match(block, /delete fwdArgs\.force/, ...);
assert.match(block, /noteDpaStateAfterCall\(name, r\)/);
```

它取的是从 `index.js:4337` 起**固定 700 个字符**的窗口，而本任务要改的 `index.js:4350` 恰好落在这个窗口的**尾部边缘**（实测：4337-4347 累计约 590 字符，4348 结束约 608，4349 结束约 640，4350 结束约 700）。**结论：安全。** 三条断言的目标分别在 4339 / 4345 / 4347，全部在**改动位置之前**，在插入新行时不会移出窗口；而窗口只会因为尾部变长而把 4350 之后的内容挤出，那部分没有任何断言。跑一遍 `node --test test/toolchainFixes.test.js` 即可实证。

- [ ] **步骤 9：改动清单（本仓库不是 git 仓库，用它代替 Commit）**

| 文件 | 改动 |
|---|---|
| `MCP/index.js`（2747 之前插入） | 新增 `DEBUG_ACTION_CHILD_HEAVY_FIELDS` / `projectDebugActionChildren` / `GABP_RESULT_PROJECTORS` / `gabpResultProjector` 四项，含设计与取舍注释 |
| `MCP/index.js:4350` | 派发处新增 `fwdArgs` 剥离 `summary`，并把 `gabpResultProjector(_rbs, args)` 接入 `callGABPTool` 第三参 |
| `MCP/index.js:2933` 后 | `TOOL_USAGE_NOTES` 新增 `rimworld.list_debug_action_children` 条目 |
| `MCP/index.js:5443` | `export` 块追加 `gabpResultProjector` |
| `MCP/test/debugActionExposure.test.js` | 追加 7 个用例 |

**连带影响：**
- **默认行为零变化**：不带 `summary` 的调用，`gabpResultProjector` 返回 `undefined`，`callGABPTool` 的 `filterResult` 仍走不到（`index.js:2761` 的 `typeof filterResult === 'function'` 判false）。400KB 载荷原样保留。
- **工具表 / 菜单计数零变化**：只加了一个 `TOOL_USAGE_NOTES` 条目，不进 `TOOL_SUBGROUP`，不进 `toolConfig.tools`。
- **`callGABPTool` 签名不变**（`index.js:2747` 本来就是 4 参），`callGABPTool` 已在 `index.js:5422` 导出，无需再动。
- **对其它 13 个 GABP 镜像零影响**：`GABP_RESULT_PROJECTORS` 只登记了一个 key，其它名字一律返回 `undefined`。
- **`summary` 不会泄漏给游戏**：在 `index.js:4350` 之前就被 `delete` 掉，与 dpa 分支剥 `force` 同构（`index.js:4343-4345`）。

---

## 5. 任务 4：修正 README 的 DebugAction 工具表

**文件：**
- 修改：`README.md:131-141`
- 测试：本任务无自动化断言，靠步骤 3 的人工核对 + 步骤 4 的 grep 自检

**交付物：** README 不再宣传三个**已删除**的工具名。

- [ ] **步骤 1：确认要删的三行确实已不存在**

运行（只读，不改任何东西）：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path .\MCP\index.js -Pattern "name: '(list_debug_actions|get_debug_action_detail|execute_debug_action)'"
```

预期：**零命中**。`MCP/index.js:4899` 的注释已明说三者已删：

```javascript
// tool-cleanup：list_debug_actions / get_debug_action_detail / execute_debug_action 已删除
```

执行 `execute_debug_action` 的唯一入口是 GABP 镜像 `rimworld.execute_debug_action`（`MCP/index.js:3164`），与已删的原生同名工具不是一回事。

- [ ] **步骤 2：改写 README 小节**

`README.md:131-141`，把

```markdown
**DebugAction（5，需进入地图，经 UE 桥接，不依赖 RIMAPI）**

> 调试菜单遍历与执行已被镜像承接：`list_debug_actions`→`rimworld.list_debug_action_roots`/`rimworld.list_debug_action_children`、`get_debug_action_detail`→`rimworld.get_debug_action`、`execute_debug_action`→`rimworld.execute_debug_action`（仅 `path`/`pawnId`）。

| 工具 | 说明 |
|---|---|
| `list_debug_actions` | 列出原版 DebugAction 菜单（支持子菜单/隐藏项） |
| `get_debug_action_detail` | 获取动作完整元数据 |
| `execute_debug_action` | 执行 DebugAction（支持 Action / ToolMap / ToolWorld / ToolMapForPawns，自动执行） |
| `get_debug_action_categories` | 分类列表及数量 |
| `search_debug_actions` | 按关键词搜索 |
```

替换为

```markdown
**DebugAction（需进入地图；查询类经 UE 桥接，遍历/执行类经 GABP 镜像，均不依赖 RIMAPI）**

> 原生 `list_debug_actions` / `get_debug_action_detail` / `execute_debug_action` 已删除，遍历与执行改由 GABP 镜像承接；找动作路径**优先用 `search_debug_actions`**，它每条只回 `path`/`label`/`category`/`actionType`，而 `rimworld.list_debug_action_children` 的全量遍历会回 400KB 级载荷。

| 工具 | 说明 | 归属 |
|---|---|---|
| `search_debug_actions` | 按关键词搜索动作路径，每条只回轻量字段。**需在 `MCP/toolConfig.json` 开启**（默认关闭） | UE 桥接 |
| `get_debug_action_categories` | 分类列表及数量 | UE 桥接 |
| `rimworld.list_debug_action_roots` | 列出 DebugAction 根节点 | GABP 镜像 |
| `rimworld.list_debug_action_children` | 列出某路径的直接子节点。**载荷很大**（每个子节点重复 `node`/`source`/`execution`/`operation`）；确需遍历时传 `summary:true` 剔除这四个重块 | GABP 镜像 |
| `rimworld.get_debug_action` | 获取单个动作的完整元数据 | GABP 镜像 |
| `rimworld.execute_debug_action` | 执行 DebugAction（仅 `path`/`pawnId`） | GABP 镜像 |
```

- [ ] **步骤 3：核对替换结果**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path .\README.md -Pattern 'DebugAction（'
```

预期：命中 1 行，且该行是 `**DebugAction（需进入地图；查询类经 UE 桥接，遍历/执行类经 GABP 镜像，均不依赖 RIMAPI）**`。

- [ ] **步骤 4：确认 README 不再宣传已删工具**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path .\README.md -Pattern 'list_debug_actions|get_debug_action_detail'
```

预期：**零命中**。新表里出现的 `rimworld.list_debug_action_roots` / `rimworld.get_debug_action` 带 `rimworld.` 前缀，模式 `list_debug_actions` 匹配不到它们（注意 `rimworld.list_debug_action_roots` 里也不含 `list_debug_actions` 子串——是 `list_debug_action_` 后面接了 `roots`）。

- [ ] **步骤 5：改动清单（本仓库不是 git 仓库，用它代替 Commit）**

| 文件 | 改动 |
|---|---|
| `README.md:131-141` | 重写 DebugAction 小节：删 3 个已删工具、补 4 个 GABP 镜像入口、给 `search_debug_actions` 标注需开启、给 `list_debug_action_children` 标注载荷大 + `summary:true` 用法 |

**连带影响：**
- **纯文档，零运行时影响。** 不改 `MCP/index.js`、不改 `toolConfig.json`、不改任何 `.cs`。
- **不改语言文件。** `Languages/ChineseSimplified/Keyed/UESDdebuger.xml:76-79` 与 `Languages/English/Keyed/UESDdebuger.xml:76-79` 同样残留 `ToolDesc.get_debug_action_detail` / `ToolDesc.execute_debug_action` 两个孤儿 key，但**本任务不动它们**：删 XML key 需要重新发布模组资源，风险收益比不划算，且它们不影响 MCP 工具表（本任务的 `README.md` 是唯一面向调用方的文档表）。这是一条**已核实并明确搁置**的文档债，不是待办占位。

---

## 6. 完成后的整体复核

- [ ] 跑一次全量测试：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'; npm test
```

预期：全部 PASS。四个测试文件必须全绿：`debugActionExposure.test.js`（新增，13 例）、`toolCountConsistency.test.js`（8 例，一例未受影响）、`toolchainFixes.test.js`（含 `:120-124` / `:254-261` / `:263-268` 三处源码切片断言，已逐条分析不受影响）、其余 `*.test.js`。

- [ ] 确认没碰任何不该碰的文件。本计划只允许改这 4 个：

```
MCP/toolConfig.json
MCP/index.js
MCP/test/debugActionExposure.test.js   （新建）
README.md
```

**未改动（刻意为之）**：`Source/` 下任何 `.cs`、`Assemblies/`、`Languages/`、`runtime/`、`docs/mcp-吐槽板.md`、`docs/issue-triage/` 下任何文件。本仓库不是 git 仓库，用下面这条只读命令核对文件修改时间（`LastWriteTime` 应只有上面 4 个是今天）：

```powershell
Get-ChildItem 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP','C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\README.md' -Recurse -File |
  Where-Object { $_.LastWriteTime -gt (Get-Date).Date } |
  Select-Object LastWriteTime, FullName
```

- [ ] 交给维护者的一句话验收标准（**需要重启 MCP 服务器后**在真实会话里确认，本计划无法代劳）：

  1. `mcp_help{path:"game_control/game/debugmenu"}` 的工具列表里能同时看到 `search_debug_actions` 和 `rimworld.list_debug_action_children`；
  2. 直接调用 `search_debug_actions{query:"Spawn"}` 不再报"工具已禁用"；
  3. `rimworld.list_debug_action_children{path:"Actions", summary:true}` 的返回显著变小，`summary` 不出现在报错信息里。

  **注意：第 3 条会真实调用游戏，必须在维护者自己安排的时段做，不要由自动化的计划执行会话代劳。** 本计划的 13 个用例全部是纯函数/配置断言，不触碰游戏。

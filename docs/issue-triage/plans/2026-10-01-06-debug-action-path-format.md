# DebugAction 路径格式文档化 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 把 RimWorld DebugAction 的 `path` 格式规则（反斜杠分段、每段是 `[DebugAction(...)]` 的第 2 个参数 `name`、`category` 不进路径）写进 AI 与用户**必然会看到**的地方，让调用方不再靠猜。

**架构：** 在 `MCP/debug-action-path-format.js` 里建立唯一事实源常量，其余 4 个入口（两个原生工具的 `description`、六个工具的 `TOOL_USAGE_NOTES` 即 `mcp_help tool=` 易错提示、两个查询工具的返回体 `data.pathFormat`、`README.md` + 首次配置向导）全部引用这一份常量，不各自复述。**不改任何 C#**（理由见 §4 修正 4/5），因此不需要重编译模组 DLL、不需要启动游戏。

**技术栈：** Node.js ESM（`MCP/package.json` `"type": "module"`）、`node:test` + `node:assert/strict`（项目现有测试栈，`MCP/package.json:9`）。

---

## 0. 给零上下文执行者的背景（不要跳过）

### 0.1 这条规则由 vanilla 源码定义，不可改

| 事实 | 依据（vanilla 源码，**不在本仓库**，只能查不能改） |
|---|---|
| `path` 只按**反斜杠**切分 | `Source/LudeonTK/Dialog_Debug.cs:292` `public static DebugActionNode GetNode(string path)`；第 296 行 `string[] s = path.Split('\\');` |
| 任何一段匹配不到子节点 `label` 就返回 `null` | 同上函数：`debugActionNode.children.FirstOrDefault(x => x.label == s[i])` 为 null 即 `return null` |
| `Path` 由 `label` 逐级拼接，`category` 全程不参与 | `Source/LudeonTK/DebugActionNode.cs:138-155`（`public string Path`），第 146 行 `cachedPath = parent.Path + "\\" + label;` |
| `DebugActionAttribute` 的参数顺序是 `(category, name)` | `Source/LudeonTK/DebugActionAttribute.cs` 构造函数 `DebugActionAttribute(string category = null, string name = null, …)` |

**推论：** `[DebugAction("FlightAStar", "Run All Tests")]` 的路径是 `Actions\Run All Tests`；`Actions/FlightAStar/Run All Tests` 必然失败（`Split('\\')` 得到长度 1 的数组，拿整串去比 `label`）。本项目**不要**去支持 `/` 双写法——那是 vanilla 行为，不是本项目的 bug。

> 查 vanilla 源码不要去 `Mods/UESDdebuger/Source/LudeonTK/` 找，那个目录不存在。用 `mcp__rimworld-source-online__read_file`（`path: "Source/LudeonTK/Dialog_Debug.cs"`）。

### 0.2 现在的缺口（为什么"唯一样例"等于没有）

全仓库源码/文档里，`Actions\` 这个路径样例只出现 **2 次**，且都不在 DebugAction 工具的说明里：

- `MCP/index.js:2411` —— `hotreload_apply` 的 `description` 尾部。`hotreload_apply` 是**隐藏工具**：`MCP/index.js:2410` 自己写着「本工具**不出现在任何菜单/工具清单**中」，`MCP/toolConfig.json` 里它是 `false`，`MCP/test/hotreloadConcealment.test.js` 有专门测试钉住"不出现在任何菜单叶子"。
- `MCP/index.js:2980` —— 同一句在 `TOOL_USAGE_NOTES.hotreload_apply.notes` 里，同样只有按名显式调用才看得到。

### 0.3 一次真实的成功调用长什么样

`MCP/logs/mcp-20261001-093829.log` 里，反斜杠写法被实跑验证过：

- `Actions\Run All Tests` —— **15 次 success**（第 50、163、176、252、292、360、380、399、560、581、688、725、751、769、796 行）
- `rimworld.get_debug_action args={"path":"Actions\\Stage Test Map"} → 25ms success`（第 404 行）

**不要**把 `Actions\运行全部动画测试`（第 406、414、421、980、984 行，持续 30~60s `GABP_ERROR`）当成格式问题的证据——同一条路径在 `get_debug_action`（只查详情、不执行）时也超时，那是那个动作本身耗时，不是路径错。

### 0.4 执行者要改的文件清单（本计划**唯一**允许触碰的）

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/debug-action-path-format.js` | **新建** | 路径格式的唯一事实源：两个导出常量。不含逻辑。 |
| `MCP/index.js` | 修改（6 处 + 2 个新函数 + 导出） | 把常量接到工具描述、`TOOL_USAGE_NOTES`、两个查询工具的返回体上。 |
| `MCP/test/debugActionPathFormat.test.js` | **新建** | 钉住上面 6 处接入点不被回退。 |
| `README.md` | 修改（131-141、171-172 行附近） | 人类可读的格式说明 + 正例 + 反例。 |
| `MCP/first-configs.md` | 修改（12 行后） | 首次配置向导里的一句话指路。 |
| `MCP/first-configs.en.md` | 修改（12 行后） | 同上，英文版。 |

**明确不动：** `Source/` 下任何 `.cs`（`UEDebugActions.cs`、`UEHttpServer.cs` 一律不改）、`MCP/toolConfig.json`、任何 `*.log`。

### 0.5 测试基础设施（用项目真实命令）

- 框架：`node:test` + `node:assert/strict`（见 `MCP/test/toolCountConsistency.test.js:1-2`）。
- 全部测试：`cd MCP && npm test`（等价于 `node --test "test/*.test.js"`，`MCP/package.json:9`）。
- 单文件：`cd MCP && node --test test/debugActionPathFormat.test.js`。
- **关键守卫**：任何 `import '../index.js'` 的测试文件都必须在 import 之前写 `process.env.UESD_MCP_NO_START = '1'`，否则会启动 HTTP 服务器并占 3000 端口（见 `MCP/test/toolCountConsistency.test.js:4-6`）。

---

## 1. 逐行复核裁决报告后发现的修正（**与上游报告不一致之处，以本节为准**）

1. **`DebugActionNode.cs` 行号**：上游写 `139-155`，实为 **138-155**（138 行是 `public string Path` 声明行），拼接语句在 **146** 行。
2. **上游报告 §4 声称"没有成功样本"不成立**。日志里 `Actions\Run All Tests` 有 15 次 `success`，另有 `get_debug_action` 的成功样本（§0.3）。正确写法已被实跑证实。
3. **入口判断偏了**：`MCP/toolConfig.json` 默认启用的 DebugAction 工具是 **4 个 GABP 镜像**（`rimworld.list_debug_action_roots` / `rimworld.list_debug_action_children` / `rimworld.get_debug_action` / `rimworld.execute_debug_action`），而上游重点关注的 `search_debug_actions` / `get_debug_action_categories` 在 toolConfig 里是 `false`，**默认根本不出现在工具列表里**。镜像的 `description` 来自 GABP（本项目改不了），但 `mcp_help tool=<镜像名>` 的 `notes` 来自本项目 `TOOL_USAGE_NOTES`（`MCP/index.js:3667`），而**目前没有任何镜像条目**——这才是主入口，本计划把它当重点。
4. **上游建议改 `Source/UELoader/UEDebugActions.cs` 的 `ListActions` 返回体（167-172 行）——对 MCP 不可见**。`ListActions` 唯一的调用方是 HTTP 端点 `debugaction/list`（`Source/UELoader/UEHttpServer.cs:449-455`），原生 MCP 工具 `list_debug_actions` 已删除（`MCP/index.js:4899`），`MCP/index.js` 里没有任何 `/debugaction/list` 调用。
5. **上游建议改 `Search` 返回体（245-253 行）——字段会被丢弃**。`MCP/index.js:4961-4969` 是**手工重建** `data` 对象（只取 `finalData.results` / `complete` / `truncated`），C# 侧新增字段到不了调用方。→ payload 层必须在 `MCP/index.js` 加。
6. `UEDebugActions.Execute` 的签名在 **295** 行（293-294 是它的 XML doc 注释），上游写"约 293 行"。
7. "全仓库搜 `Actions\\` 只有两处命中"应表述为：**源码/文档**里 2 处（`MCP/index.js:2411` 与 `:2980`）；另有 57 行命中在 `MCP/logs/mcp-20261001-093829.log`（调用记录，不是说明性文档）。

---

## 任务 1：建立唯一事实源，并把格式接进工具发现面

**文件：**
- 创建：`MCP/debug-action-path-format.js`
- 修改：`MCP/index.js:25-31`（加 import）、`MCP/index.js:2349`、`MCP/index.js:2357`、`MCP/index.js:2933` 及其后（`TOOL_USAGE_NOTES`）、`MCP/index.js:5416-5443`（export 块）
- 创建：`MCP/test/debugActionPathFormat.test.js`

- [ ] **步骤 1：编写失败的测试**

创建 `MCP/test/debugActionPathFormat.test.js`，内容如下（整体替换，不要留空文件）：

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');
const { DEBUG_ACTION_PATH_FORMAT, DEBUG_ACTION_PATH_EXAMPLE } =
  await import('../debug-action-path-format.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const modRoot = path.join(here, '..');
const source = fs.readFileSync(path.join(modRoot, 'index.js'), 'utf-8');

const helpFor = async (tool) => JSON.parse(
  (await Promise.resolve(mod.mcpHelpResult({ tool }))).content[0].text);

/** 从 index.js 源码里切出 TOOL_USAGE_NOTES 的某一条（到下一个顶层 "\n  }," 为止） */
function usageNotesEntry(name) {
  const i = source.indexOf(`\n  ${name}: {`);
  if (i < 0) return null;
  const end = source.indexOf('\n  },', i);
  return source.slice(i, end < 0 ? i + 2000 : end);
}

// 默认工具清单里真正露出来的 4 个 DebugAction 镜像（toolConfig.json 里是 true）
const EXPOSED_MIRRORS = [
  'rimworld.list_debug_action_roots',
  'rimworld.list_debug_action_children',
  'rimworld.get_debug_action',
  'rimworld.execute_debug_action',
];

// ===========================================================================
// DebugAction 路径格式：唯一事实源常量本身
// ===========================================================================

test('常量声明了三件事：反斜杠分段 / 段是 name / category 不进路径', () => {
  assert.equal(DEBUG_ACTION_PATH_EXAMPLE, 'Actions\\Run All Tests');
  assert.ok(DEBUG_ACTION_PATH_FORMAT.includes('Actions\\Run All Tests'),
    '格式串必须自带一个可直接照抄的样例路径');
  assert.match(DEBUG_ACTION_PATH_FORMAT, /反斜杠/, '必须点明分隔符是反斜杠');
  assert.match(DEBUG_ACTION_PATH_FORMAT, /第 2 个参数 name/, '必须点明段取第 2 个参数 name');
  assert.match(DEBUG_ACTION_PATH_FORMAT, /第 1 个参数 category 不进路径/,
    '必须点明 category 不是路径段');
});

// ===========================================================================
// 入口一：两个原生查询工具的 description（mcp_help tool= 可见）
// ===========================================================================

test('原生查询工具的描述带格式规则（不依赖游戏）', async () => {
  for (const name of ['get_debug_action_categories', 'search_debug_actions']) {
    const d = await helpFor(name);
    assert.match(d.description, /反斜杠/, `${name} 描述应说明反斜杠分隔，实际: ${d.description}`);
    assert.match(d.description, /category 不进路径/,
      `${name} 描述应点明 category 不进路径，实际: ${d.description}`);
    assert.match(d.description, /Actions\\Run All Tests/,
      `${name} 描述应带一个真实样例路径`);
  }
});

// ===========================================================================
// 入口二：TOOL_USAGE_NOTES —— 默认暴露的 4 个 GABP 镜像的唯一本项目侧说明位
// （镜像只有 GABP 在线时才进 allToolEntries，故离线断言源码文本；
//   这与 MCP/test/hotreloadConcealment.test.js 钉隐藏工具的写法一致）
// ===========================================================================

test('四个默认暴露的 DebugAction 镜像都有带格式规则的 notes', () => {
  for (const name of EXPOSED_MIRRORS) {
    const entry = usageNotesEntry(name);
    assert.ok(entry, `${name} 应在 TOOL_USAGE_NOTES 里有条目`);
    assert.match(entry, /DEBUG_ACTION_PATH_FORMAT/,
      `${name} 的 notes 应引用常量而不是复述一份文案`);
  }
});

test('原生查询工具的 notes 也引用常量', () => {
  for (const name of ['search_debug_actions', 'get_debug_action_categories']) {
    const entry = usageNotesEntry(name);
    assert.ok(entry, `${name} 应在 TOOL_USAGE_NOTES 里有条目`);
    assert.match(entry, /DEBUG_ACTION_PATH_FORMAT/, `${name} 的 notes 应引用常量`);
  }
});

test('execute_debug_action 的调用示例必须是反斜杠单反斜杠写法', () => {
  const entry = usageNotesEntry('rimworld.execute_debug_action');
  assert.ok(entry, 'rimworld.execute_debug_action 应在 TOOL_USAGE_NOTES 里有条目');
  // index.js 源码里写 \\n，渲染出去才是 \n。这里锁的是"源码里恰好两个反斜杠"，
  // 防止有人顺手改成 \n（变成换行）或 \\\\（变成两个反斜杠）。
  assert.match(entry, /Actions\\\\Run All Tests/,
    `示例里的路径在源码中必须是 Actions\\\\Run All Tests，实际片段: ${entry.slice(0, 300)}`);
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/debugActionPathFormat.test.js`
预期：FAIL。第一个报错是 `ERR_MODULE_NOT_FOUND`（找不到 `../debug-action-path-format.js`）——这是正确的红灯，说明待建模块还不存在。

- [ ] **步骤 3：创建唯一事实源常量模块**

创建 `MCP/debug-action-path-format.js`：

```js
// DebugAction 路径格式的唯一事实源：工具描述、mcp_help 易错提示、查询返回体、
// README、首次配置向导——全部引用这里，不要在别处复述文案。
//
// 规则来自 vanilla，本项目不可改（详见 docs/issue-triage/plans/2026-10-01-06-debug-action-path-format.md §0.1）：
//   1. LudeonTK/Dialog_Debug.cs:292 GetNode(string path) 只做 path.Split('\\')，
//      再逐段按 label 匹配子节点；匹配不到即 null（本项目转成 PATH_NOT_FOUND）。
//   2. LudeonTK/DebugActionNode.cs:138-155 Path 由 label 逐级 parent.Path + "\\" + label 拼成。
//   3. DebugActionAttribute 构造函数签名 (category = null, name = null, ...)：
//      category 是第 1 个参数，只用于菜单分组排序；进路径的是第 2 个参数 name。
// 因此 [DebugAction("FlightAStar", "Run All Tests")] 的路径是 Actions\Run All Tests。
// 不要"顺手"支持 / 分隔或剥掉 category：那是 vanilla 行为，本项目不应静默改写调用方的 path。

/** 可直接照抄的样例路径（[DebugAction("FlightAStar", "Run All Tests")] → Actions\Run All Tests） */
export const DEBUG_ACTION_PATH_EXAMPLE = 'Actions\\Run All Tests';

/** 一句话格式规则，供工具描述 / mcp_help notes / 返回体 / 文档共用 */
export const DEBUG_ACTION_PATH_FORMAT =
  'DebugAction 路径格式：path 用反斜杠分隔，形如 Actions\\<label>\\<子菜单 label>；'
  + '每一段都是 [DebugAction(...)] 的第 2 个参数 name（菜单显示名）。'
  + '第 1 个参数 category 不进路径，只作为返回里的 category 字段出现——'
  + '所以 Actions/FlightAStar/Run All Tests（用 / 分隔或多写了 category）会报"路径不存在"。'
  + '例：[DebugAction("FlightAStar", "Run All Tests")] → ' + DEBUG_ACTION_PATH_EXAMPLE;
```

- [ ] **步骤 4：在 `MCP/index.js` 里 import 这两个常量**

在 `MCP/index.js:31`（`import { appendRecord, readOrphans, filterOrphans } from './longTask/journal.js';`）**下一行**加入：

```js
import { DEBUG_ACTION_PATH_FORMAT, DEBUG_ACTION_PATH_EXAMPLE } from './debug-action-path-format.js';
```

- [ ] **步骤 5：把格式接进两个原生工具的 description**

把 `MCP/index.js:2349` 整行替换为：

```js
    description: '[DebugAction工具][需要进入地图] 获取所有DebugAction分类列表及其数量。' + DEBUG_ACTION_PATH_FORMAT,
```

把 `MCP/index.js:2357` 整行替换为：

```js
    description: '[DebugAction工具][需要进入地图] 按关键词搜索DebugAction，在路径、标签和分类中搜索匹配项。仅返回简化信息（path和label），如需详细信息请使用 GABP 镜像 rimworld.get_debug_action。' + DEBUG_ACTION_PATH_FORMAT,
```

- [ ] **步骤 6：补齐 `TOOL_USAGE_NOTES`（本任务的关键交付）**

把 `MCP/index.js:2933` 整行替换为：

```js
  search_debug_actions: { example: '{ "query": "Spawn" }', notes: '参数为 query（子串匹配）；也兼容 keyword 别名。结果里的 path 用法：' + DEBUG_ACTION_PATH_FORMAT },
```

并在它**下一行**（即原 2934 行 `search_map_structure: …` 之前）插入这 5 条：

```js
  get_debug_action_categories: { example: '{}', notes: '分类只用于筛选/聚合，不参与 path。' + DEBUG_ACTION_PATH_FORMAT },
  'rimworld.list_debug_action_roots': { example: '{}', notes: '列出 DebugAction 根菜单；后续所有 path 段都按同一规则拼：' + DEBUG_ACTION_PATH_FORMAT },
  'rimworld.list_debug_action_children': { example: '{ "path": "Actions" }', notes: '列出某个父路径下的子菜单/动作，返回的 path 可直接喂给 get_debug_action / execute_debug_action。' + DEBUG_ACTION_PATH_FORMAT },
  'rimworld.get_debug_action': { example: '{ "path": "Actions\\Run All Tests" }', notes: 'path 传错只返回"路径不存在"，不报错细节；先对照规则确认写法。' + DEBUG_ACTION_PATH_FORMAT },
  'rimworld.execute_debug_action': { example: '{ "path": "Actions\\Run All Tests" }', notes: 'path 必填，写法错了必然报"路径不存在"；确认法：先用 rimworld.search_debug_actions 或 get_debug_action 查一次。' + DEBUG_ACTION_PATH_FORMAT },
```

> **双反斜杠陷阱（务必注意）：** 示例字符串 `'Actions\\Run All Tests'` 在源码里是**两个**反斜杠，渲染出去才是**一个**。参考 `MCP/index.js:2411` 已有的写法 `path="Actions\\Hot reload Defs"`。写成 `'\n'` 会变成换行，写成 `'\\\\'` 会变成两个反斜杠——两种都是错的。步骤 4 的第 4 个测试就是专门钉这一点的。

- [ ] **步骤 7：把常量加进 export 块**

在 `MCP/index.js:5442`（`  menuUniverseNames,`）下一行、`};` 之前加入：

```js
  // DebugAction 路径格式（任务 1/2）：唯一事实源常量 + 返回体构造纯函数，供单测直接断言
  DEBUG_ACTION_PATH_FORMAT,
  DEBUG_ACTION_PATH_EXAMPLE,
  buildDebugActionCategoriesPayload,
  buildDebugActionSearchPayload,
```

> `buildDebugActionCategoriesPayload` / `buildDebugActionSearchPayload` 是任务 2 才定义的。**在任务 2 完成前，先只写前两行常量导出**，并在任务 2 的步骤 4 再把两个函数名补进去。

- [ ] **步骤 8：运行测试验证通过**

运行：`cd MCP && node --test test/debugActionPathFormat.test.js`
预期：5 个测试全部 PASS。

- [ ] **步骤 9：跑全量测试确认没有回退**

运行：`cd MCP && npm test`
预期：全部 PASS，特别注意 `toolCountConsistency.test.js` 与 `hotreloadConcealment.test.js`（本次改动不得影响工具计数口径，也不得让 `hotreload_*` 漏进菜单）。

- [ ] **步骤 10：Commit**

```bash
git add MCP/debug-action-path-format.js MCP/index.js MCP/test/debugActionPathFormat.test.js
git commit -m "docs(debugaction): 把 path 格式规则接入工具描述与 mcp_help 易错提示"
```

---

## 任务 2：让格式随返回值走（`data.pathFormat`）

**为什么必须改 MCP 层而不是 C#：** `MCP/index.js:4961-4969` 与 `4925-4930` 是**手工重建** `data` 对象，只挑 `finalData.results` / `categories` / `complete` / `truncated`。即使在 `Source/UELoader/UEDebugActions.cs` 的 `Search`（245-253 行）返回体里加字段，也会在这一层被丢弃。而 `ListActions`（167-172 行）唯一的调用方是 HTTP 端点 `debugaction/list`（`Source/UELoader/UEHttpServer.cs:449-455`），原生 MCP 工具 `list_debug_actions` 已在 `MCP/index.js:4899` 删除。所以 payload 层只能在 `MCP/index.js` 加。

**文件：**
- 修改：`MCP/index.js:4898` 之前（新增两个纯函数）、`MCP/index.js:4925-4930`、`MCP/index.js:4961-4969`、`MCP/index.js:5442` 附近（补两个函数名的导出）
- 修改：`MCP/test/debugActionPathFormat.test.js`（追加测试）

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/debugActionPathFormat.test.js` **末尾追加**（并在文件顶部 import 之后已有的 `EXPOSED_MIRRORS` 常量下面，紧跟其后插入同样风格的镜像数组）：

```js
// ===========================================================================
// 入口三：查询工具的返回体自带 pathFormat —— 调用方从哪拿到 path，规则就跟到哪
// ===========================================================================

test('search_debug_actions 返回体带 pathFormat，且原字段一个不少', () => {
  const r = mod.buildDebugActionSearchPayload(
    { results: [{ path: DEBUG_ACTION_PATH_EXAMPLE, label: 'Run All Tests', category: 'FlightAStar' }],
      complete: true, truncated: false },
    { query: 'Run All' }, 200, false);

  assert.equal(r.success, true);
  assert.equal(r.data.pathFormat, DEBUG_ACTION_PATH_FORMAT, '返回体必须自带格式规则');
  // 关键：path 与 category 同时在场——规则说 category 不进路径，但 payload 里它仍在，
  // 靠这一对断言钉住"规则说归规则、字段归字段"。
  assert.equal(r.data.results[0].path, DEBUG_ACTION_PATH_EXAMPLE);
  assert.equal(r.data.results[0].category, 'FlightAStar');
  assert.equal(r.data.query, 'Run All');
  assert.equal(r.data.count, 1);
  assert.equal(r.data.maxResults, 200);
  assert.equal(r.data.complete, true);
  assert.equal(r.data.truncated, false);
  assert.equal(r.data.incomplete, false);
});

test('search_debug_actions 返回体在空/异常数据下不炸，且 pathFormat 仍在', () => {
  const r = mod.buildDebugActionSearchPayload(undefined, { query: 'x' }, 200, true);
  assert.equal(r.data.pathFormat, DEBUG_ACTION_PATH_FORMAT);
  assert.deepEqual(r.data.results, []);
  assert.equal(r.data.count, 0);
  assert.equal(r.data.complete, false);
  assert.equal(r.data.truncated, false);
  assert.equal(r.data.incomplete, true);
});

test('get_debug_action_categories 返回体带 pathFormat', () => {
  const r = mod.buildDebugActionCategoriesPayload(
    { categories: [{ name: 'FlightAStar', count: 3 }], complete: true }, false);
  assert.equal(r.data.pathFormat, DEBUG_ACTION_PATH_FORMAT);
  assert.equal(r.data.count, 1);
  assert.equal(r.data.complete, true);
  assert.equal(r.data.incomplete, false);

  const empty = mod.buildDebugActionCategoriesPayload(undefined, true);
  assert.equal(empty.data.pathFormat, DEBUG_ACTION_PATH_FORMAT);
  assert.deepEqual(empty.data.categories, []);
  assert.equal(empty.data.count, 0);
  assert.equal(empty.data.incomplete, true);
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/debugActionPathFormat.test.js`
预期：新增的 3 个测试 FAIL，报错为 `TypeError: mod.buildDebugActionSearchPayload is not a function`；任务 1 的 5 个仍然 PASS。

- [ ] **步骤 3：写两个纯函数**

在 `MCP/index.js:4898`（`    // ========== DebugAction 工具处理 ==========`）**上方**插入：

```js
// DebugAction 返回体构造（纯函数，无 IO）：把 C# 侧的 finalData 收敛成对外 payload。
// 单独抽出来是为了能脱离游戏单测——MCP 侧是手工重建 data 的，新增字段必须在这一层加，
// 加在 Source/UELoader/UEDebugActions.cs 的返回体里会被这里的重建覆盖掉。
function buildDebugActionCategoriesPayload(finalData, incomplete) {
  return { success: true, data: {
    pathFormat: DEBUG_ACTION_PATH_FORMAT,
    categories: finalData?.categories ?? [],
    count: finalData?.categories?.length ?? 0,
    complete: !!finalData?.complete,
    incomplete: !!incomplete
  }};
}

function buildDebugActionSearchPayload(finalData, args, maxResults, incomplete) {
  return { success: true, data: {
    pathFormat: DEBUG_ACTION_PATH_FORMAT,
    query: args?.query,
    results: finalData?.results ?? [],
    count: finalData?.results?.length ?? 0,
    complete: !!finalData?.complete,
    truncated: !!finalData?.truncated,
    incomplete: !!incomplete,
    maxResults
  }};
}
```

- [ ] **步骤 4：把两个 case 分支改成调用纯函数**

把 `MCP/index.js:4925-4931`（`const catResult = …` 一直到该 case 的 `}`）替换为：

```js
      const catResult = buildDebugActionCategoriesPayload(
        finalData, !finalData?.complete && Date.now() >= deadline);
      return { content: [{ type: 'text', text: JSON.stringify(catResult, null, 2) }] };
```

把 `MCP/index.js:4961-4969` 替换为：

```js
      const searchResult = buildDebugActionSearchPayload(
        finalData, args, maxResults,
        !finalData?.complete && !finalData?.truncated && Date.now() >= deadline);
      return { content: [{ type: 'text', text: JSON.stringify(searchResult, null, 2) }] };
```

> 语义保持等价：`incomplete` 原先是在对象字面量里现算的同一个布尔表达式，现在提前算好传进去。`maxResults` 保持由 case 分支在第 4938 行算出的值，不再二次推导，避免两处漂移。

- [ ] **步骤 5：把两个函数名补进 export 块**

在 `MCP/index.js` 任务 1 步骤 7 加的那组导出里，确认这一行存在（不存在就补上）：

```js
  buildDebugActionCategoriesPayload,
  buildDebugActionSearchPayload,
```

- [ ] **步骤 6：运行测试验证通过**

运行：`cd MCP && node --test test/debugActionPathFormat.test.js`
预期：8 个测试全部 PASS。

- [ ] **步骤 7：跑全量测试**

运行：`cd MCP && npm test`
预期：全部 PASS。

- [ ] **步骤 8：Commit**

```bash
git add MCP/index.js MCP/test/debugActionPathFormat.test.js
git commit -m "feat(debugaction): 查询返回体携带 pathFormat，随返回值下发路径格式规则"
```

---

## 任务 3：README 与首次配置向导文档化

**文件：**
- 修改：`README.md:131-141`、`README.md:171-173`
- 修改：`MCP/first-configs.md:12` 之后
- 修改：`MCP/first-configs.en.md:12` 之后
- 修改：`MCP/test/debugActionPathFormat.test.js`（追加测试）

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/debugActionPathFormat.test.js` 末尾追加：

```js
// ===========================================================================
// 入口四：人类可读文档（README + 首次配置向导）
// ===========================================================================

const readme = fs.readFileSync(path.join(modRoot, '..', 'README.md'), 'utf-8');
const firstConfigZh = fs.readFileSync(path.join(modRoot, 'first-configs.md'), 'utf-8');
const firstConfigEn = fs.readFileSync(path.join(modRoot, 'first-configs.en.md'), 'utf-8');

test('README 的 DebugAction 小节给出规则、正例与反例', () => {
  assert.match(readme, /路径格式/, 'README DebugAction 小节应有「路径格式」小标题');
  assert.match(readme, /Actions\\Run All Tests/, 'README 应给出可直接照抄的正例');
  assert.match(readme, /反斜杠/, 'README 应点明只认反斜杠');
  assert.match(readme, /category[^\n]*不进路径/, 'README 应点明 category 不进路径');
  assert.match(readme, /Actions\/FlightAStar/, 'README 应给出「多写了 category」的反例');
});

test('首次配置向导（中/英）都指向这条规则', () => {
  assert.match(firstConfigZh, /Actions\\Run All Tests/, '中文向导应给出正例');
  assert.match(firstConfigEn, /Actions\\Run All Tests/, '英文向导应给出正例');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`cd MCP && node --test test/debugActionPathFormat.test.js`
预期：新增的 2 个测试 FAIL，报错为 `The expression evaluated to a falsy value` / `regexp ... did not match`（README 目前没有「路径格式」小节，`MCP/first-configs.md:12` 也没有该样例）。

- [ ] **步骤 3：改 README 的 DebugAction 小节**

在 `README.md:133`（那条 `> 调试菜单遍历与执行已被镜像承接：…` 的引用行）**之后**、`README.md:134` 空行处插入：

```markdown
> **路径格式（调用前必读）**：`path` 形如 `Actions\<label>\<子菜单 label>`，**分隔符只能是反斜杠 `\`**；每一段都是 `[DebugAction(...)]` 的**第 2 个参数 `name`**（菜单显示名）。第 1 个参数 `category` **不进路径**，只作为返回里的 `category` 字段出现。
> 正例：`[DebugAction("FlightAStar", "Run All Tests")]` → `Actions\Run All Tests`。
> 反例：`Actions/FlightAStar/Run All Tests` —— 用了 `/` 分隔、又多了 `category` 段，两处都错，必报"路径不存在"。
> 拿不准路径时先 `search_debug_actions` 或 `rimworld.get_debug_action` 查一次，把返回里的 `path` 原样回填。
```

在 `README.md:173`（镜像表格最后一行 `| rimworld.jump_camera_to_cell / rimworld.set_camera_zoom | … |`）**之后**、表格结束处插入：

```markdown
> 上表 4 个 `rimworld.*debug_action*` 镜像的 `path` 参数同样适用上方「DebugAction（5，需进入地图）」小节里的**路径格式**规则。
```

- [ ] **步骤 4：改首次配置向导（中文）**

在 `MCP/first-configs.md:12`（`| DebugAction | 执行 RimWorld 原生调试菜单动作（列表 / 搜索 / 执行） | 由 MCP 服务器自动调用（需进图） |`）**之后**插入：

```markdown
> DebugAction 动作的 `path` 形如 `Actions\Run All Tests`：**只用反斜杠**分段，每一段是 `[DebugAction(...)]` 的第 2 个参数 `name`；第 1 个参数 `category` 不是路径段。完整规则与反例见 `README.md` 的 DebugAction 小节。
```

- [ ] **步骤 5：改首次配置向导（英文）**

在 `MCP/first-configs.en.md:12`（`| DebugAction | Execute native RimWorld debug menu actions (list / search / execute) | Automatically invoked by the MCP server (requires entering a map) |`）**之后**插入：

```markdown
> DebugAction paths look like `Actions\Run All Tests`: segments are separated by a **backslash only**, and each segment is the **2nd argument (`name`)** of `[DebugAction(...)]`; the 1st argument (`category`) is not a path segment. Full rules and counter-examples: the DebugAction section of `README.md`.
```

- [ ] **步骤 6：运行测试验证通过**

运行：`cd MCP && node --test test/debugActionPathFormat.test.js`
预期：10 个测试全部 PASS。

- [ ] **步骤 7：跑全量测试**

运行：`cd MCP && npm test`
预期：全部 PASS。

- [ ] **步骤 8：Commit**

```bash
git add README.md MCP/first-configs.md MCP/first-configs.en.md MCP/test/debugActionPathFormat.test.js
git commit -m "docs(debugaction): README 与首次配置向导写明 path 格式规则与反例"
```

---

## 人工验收（自动化测试覆盖不到的部分）

本计划的 10 个自动化测试已覆盖"文案是否挂上去了"。下面三条需要真环境确认，**做验收时逐条跑并核对输出**：

1. **两个原生工具的帮助文案（GABP 离线即可）**

   ```bash
   cd MCP
   node --input-type=module -e "process.env.UESD_MCP_NO_START='1'; const m=await import('./index.js'); console.log((await m.mcpHelpResult({tool:'search_debug_actions'})).content[0].text)"
   ```

   预期输出（JSON）里 `description` 字段同时含 `反斜杠`、`category 不进路径`、`Actions\Run All Tests` 三个片段。把 `search_debug_actions` 换成 `get_debug_action_categories` 再跑一次，结果相同。

2. **四个默认暴露的镜像的帮助文案（需要 GABP 在线）**

   在游戏已进地图、且 MCP 已连上 GABP 的会话里调用：

   ```json
   agg_call_tool { tool: "mcp_help", args: { tool: "rimworld.execute_debug_action" } }
   ```

   预期：返回 JSON 的 `notes` 字段含 `反斜杠`、`category 不进路径`、`Actions\Run All Tests`；`example` 字段为 `{ "path": "Actions\Run All Tests" }`（**一个**反斜杠）。同样验证 `rimworld.list_debug_action_roots`、`rimworld.list_debug_action_children`、`rimworld.get_debug_action`。

3. **返回体里的 `pathFormat`（需要进图，UE 桥接在线）**

   ```json
   agg_call_tool { tool: "search_debug_actions", args: { query: "Run All Tests" } }
   ```

   预期：返回 JSON 的 `data` 对象里有 `pathFormat` 字段，内容为同一句格式规则；同时 `results[0].path` 是反斜杠写法，`results[0].category` 仍是分类名（如 `FlightAStar`）。再跑一次 `get_debug_action_categories`，预期同样带 `pathFormat`。

**回滚方式：** 本计划只碰 `MCP/` 下 3 个文件 + `README.md`，`git revert` 任务 1/2/3 三个 commit 即可，无需重编译模组。
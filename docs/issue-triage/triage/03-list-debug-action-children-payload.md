裁决：值得修

# [03] `list_debug_action_children` 返回 400KB+ 🔴

**一句话结论**：现象属实（一次调用吃掉约 10 万 token 级上下文），但**根因不在"少了个 filter 参数"，而在"本项目已经有一个轻量替代 `search_debug_actions`，它端到端全部实现完毕却没被暴露、也没被 `mcp_help` 挂在 debugmenu 分组下"** —— 修复面全在本仓库内，成本极低。

---

## 1. 核实过程

### 1.1 这次调用确实发生过，且发生在吐槽板记录的同一场会话里

`MCP/logs/mcp-20261001-093829.log:49`：

```
[13:03:14.367] <INFO> tools/call agg_call_tool args={args={"path":"Actions"}, tool=rimworld.list_debug_action_children} → 60ms success
```

`MCP/logs/mcp-20261001-093829.log:46-50` 显示调用前后的完整动作链，与吐槽板描述逐字吻合：

```
46: [13:02:46.344] <INFO> tools/call mcp_help args={path=game_control/game/debugmenu} → 2ms ok
47: [13:02:48.678] <INFO> tools/call agg_call_tool args={tool=rimworld.list_debug_action_roots, args={}} → 38ms success
48: [13:03:01.196] <INFO> tools/call agg_call_tool args={args={"children":"true","path":"Actions/FlightAStar"}, tool=rimworld.get_debug_action} → 195ms success
49: [13:03:14.367] <INFO> tools/call agg_call_tool args={args={"path":"Actions"}, tool=rimworld.list_debug_action_children} → 60ms success
50: [13:03:27.011] <INFO> tools/call agg_call_tool args={args={"path":"Actions\\Run All Tests"}, tool=rimworld.execute_debug_action} → 2635ms success
```

⇒ **「按文档路径下钻 → 拿到 400KB → 只能 grep spill 文件」的路径是真的。**

### 1.2 载荷不是本项目造的：本仓库对它是纯透传

`MCP/index.js:4350`（GABP 镜像的分发点）：

```javascript
    const _r = await callGABPTool(_rbs, args, undefined, _opts);
```

`MCP/gabpClient.js:674-680`：

```javascript
      const res = await this._request('tools/call', { name, parameters }, opts);
      // 服务器直接透传 result；非对象时防御性包装（spec §3.3）
      let result = res;
```

⇒ 从游戏内 RimBridgeServer 回来的字节，原封不动进 `JSON.stringify(...)` 回给 harness。**本仓库这一层没有任何裁剪。**

注意 `callGABPTool` 的签名 `MCP/index.js:2747`：

```javascript
async function callGABPTool(rbsName, args, filterResult, opts) {
```

**已经预留了 `filterResult` 投影钩子**（`MCP/index.js:2761-2763` 会调用它），只是 `list_debug_action_children` 这条路径传的是 `undefined`。这是现成的落刀点。

### 1.3 上游确实没有 `filter` / `depth` / `limit` 参数

`C:\SteamLibrary\steamapps\common\RimWorld\RimBridgeServer-main\docs\tool-reference.md:275-281`：

```
### `rimworld/list_debug_action_children`

List direct children of a RimWorld debug action path

Parameters:
- `path` (`string`, `required`): Stable debug action path returned by the discovery tools
- `includeHidden` (`bool`, `optional`, default `false`): Include child nodes that are currently hidden in the active game state
```

只有两个参数。同文件 `283-292` 行是兄弟工具 `rimworld/search_debug_actions`，带 `query` + `limit`（默认 50）。

响应形状可从 RimBridgeServer 自带的 live smoke 测试反推 —— `Tests\RimBridgeServer.LiveSmoke\SmokeScenarioCatalog.cs:405` 读顶层 `children` 数组，`:417` 读 `node.execution.supported`，证实每个子节点确实内嵌 `node` / `execution` 子结构。与吐槽板说的「每个叶子节点都重复了一整套 `node` + `source` + `execution` + `operation`」一致。

### 1.4 关键发现：吐槽板想要的「只回 path + label」**本项目已经实现了**

`MCP/index.js:2356-2357` 的工具描述：

```javascript
    name: 'search_debug_actions',
    description: '[DebugAction工具][需要进入地图] 按关键词搜索DebugAction，在路径、标签和分类中搜索匹配项。仅返回简化信息（path和label），如需详细信息请使用 GABP 镜像 rimworld.get_debug_action。',
```

UE 侧端点存在 —— `Source\UELoader\UEHttpServer.cs:460-466`：

```csharp
                    case "debugaction/search":
                        result = GetGuard(request, () => UEHttpHandler.RunOnMain(() =>
                            UEDebugActions.Search(
                                GetQueryString(request, "query"),
                                GetQueryString(request, "category"),
                                GetIntQuery(request, "maxResults", 200))));
```

C# 实现确认每条结果**只有 4 个字段** —— `Source\UELoader\UEDebugActions.cs:232-241`：

```csharp
                    var item = new Dictionary<string, object>
                    {
                        { "path", path },
                        { "label", label },
                        { "category", e.Category },
                        { "actionType", e.Node.actionType.ToString() }
                    };
                    if (e.Dynamic)
                        item["dynamicMenu"] = true;
```

**没有 `node` / `source` / `execution` / `operation`。** 这正是吐槽板 `docs\mcp-吐槽板.md:317` 建议的「或默认只回 `path` + `label` + `hasChildren`」。

### 1.5 但它没被暴露，也没被挂到 debugmenu 分组下（这才是根因）

`MCP/toolConfig.json` 解析后 `true` 的工具共 24 个，`search_debug_actions` **不在其中**（值为 `false`）。而 `rimworld.list_debug_action_children` 在其中。

`mcp_help` 菜单映射里，debugmenu 分组是 —— `MCP/index.js:3158-3164`：

```javascript
  // debugmenu（6）
  'rimworld.spawn_thing': 'game_control/game/debugmenu',
  'rimworld.set_draft': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_roots': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_children': 'game_control/game/debugmenu',
  'rimworld.get_debug_action': 'game_control/game/debugmenu',
  'rimworld.execute_debug_action': 'game_control/game/debugmenu',
```

`search_debug_actions` 在**另一个路径**下 —— `MCP/index.js:3117`：

```javascript
  'search_debug_actions': 'game_control/game/gameplay/debug_action',
```

⇒ 严格照着 1.1 的动作链走（`mcp_help path:"game_control/game/debugmenu"` → `list_debug_action_children`），**永远不会看到那个轻量替代**。这是可发现性问题，不是能力缺失。

### 1.6 顺带发现：README 的 DebugAction 小节是陈旧的

`README.md:131-141` 标题写「DebugAction（5，…）」，表里列了 `list_debug_actions` / `get_debug_action_detail` / `execute_debug_action` / `get_debug_action_categories` / `search_debug_actions`。

但 `MCP/index.js:4899-4901` 明说前两个已删除：

```javascript
    // tool-cleanup：list_debug_actions / get_debug_action_detail / execute_debug_action 已删除
```

在 `MCP/index.js` 里 grep `name: 'list_debug_actions'` 与 `name: 'get_debug_action_detail'` 均**零命中**，只余 `name: 'get_debug_action_categories'`（`:2348`）。⇒ README 这张表有 2 行指向不存在的工具、1 行指向未暴露的工具。

### 1.7 未能独立复核的部分

- **400,385 字节 / 377 个子节点 / harness spill**：这是维护者自测数字。复现它需要向运行中的游戏发 `list_debug_action_children`，属铁律禁止的改游戏状态操作，**我没有独立复核**。我在 `~/.dsh/cache`、`~/.dsh/attachments`、`~/.dsh/staging` 也未找到当次 spill 产物（最新附件是 2026-09-24，早于本场会话）。
- MCP 日志只记录耗时与成败，**不记录响应字节数**，所以日志无法佐证 400KB 这个量级。
- 上面 1.1–1.6 的每一条都是从源码与文档直接读出的，**与 400KB 无关，独立成立**。

---

## 2. 影响与严重度复评

**维持 🔴，但理由要改写。**

原判 🔴 的理由是"冲垮上下文"。复评：单次 400,385 字节，纯 ASCII JSON，按 3.5–4 字符/token 估算约 **100K–115K token**（**估算值**，非精确计量）。这个量级确实足以在 128K 上下文里一次吃掉八成，且第一次调用在 spill 之前已经实打实付过账 —— 吐槽板说「第一次调用仍吃掉了大量预算」是对的。

**但它不会让自动化得出事实相反的结论。** 载荷是**超集**而不是错误集：`list_debug_action_children` 返回的每个子节点都是真实存在的 DebugAction 节点，只是冗余。调用方拿到的信息在语义上不会错，只是被噪音淹没。所以它属于「上下文税 / 系统性浪费往返」，而不是 [01]（假成功）、[11]（假阴性）那一类。按 AGENT-BRIEF §5.2 的口径，这其实是 🟠。

**结论：现象 🔴，机制 🟠。** 之所以仍判 🔴 优先，是因为修复成本异常低（见 §4）—— 属于"高收益 / 极低成本"，应优先做。

**每次调用成本**：约 100K–115K token（估算）。
**对比 `search_debug_actions` 的成本**：单条结果只有 4 个短字段，200 条上限，粗估 5K–15K token（估算），且可搜索 —— 相差一个数量级。

---

## 3. 责任归属

**本项目（UESDdebuger）。** 三条理由：

1. 载荷虽然由 RimBridgeServer / RIMAPI 生成，但**暴露面、可见性、路由全在本仓库**。`MCP/toolConfig.json` 决定谁能被看见，`MCP/index.js:3117` vs `:3158-3164` 决定 `mcp_help` 把它挂在哪。
2. 轻量替代 `search_debug_actions` 是**本仓库自己写的**（`MCP/index.js:2356`、`Source/UELoader/UEDebugActions.cs:206`、`Source/UELoader/UEHttpServer.cs:460`），端到端已实现且已在线。**能力已经在仓库里躺着没开** —— 这不是上游的锅。
3. `callGABPTool` 的 `filterResult` 钩子（`MCP/index.js:2747,2761`）也是本仓库的，只是这条路径没传。

**不是** vanilla RimWorld 的锅（DebugAction 树本身就这么大，是设计使然）；**不是** RimBridgeServer 的锅（它按自己的契约正确实现了 `path` + `includeHidden`，且另有 `search_debug_actions` 兄弟工具）；**也不只是**文档缺失 —— 代码侧的暴露开关也要动。

---

## 4. 修的话怎么修

按成本从低到高：

1. **`MCP/toolConfig.json`**：把 `"search_debug_actions"` 从 `false` 改为 `true`。
   单字段改动，立刻让轻量替代出现在工具表里。**这是单点最高性价比的一刀。**

2. **`MCP/index.js:3117`**：把 `search_debug_actions` 的菜单路径并入 debugmenu 分组（即改为 `game_control/game/debugmenu`），或让 `:3158-3164` 的 debugmenu 分组同时收录它。
   消除"照文档路径下钻却看不到替代"的断链。同时补一条 `index.js:2933` 附近的 `TOOL_NOTES`（现有 `search_debug_actions: { example: '{ "query": "Spawn" }', ... }` 已有条目，可扩写），写明"找动作路径优先用它，别用 `list_debug_action_children` 遍历"。

3. **`MCP/index.js:4350`**：给 `list_debug_action_children` 传 `filterResult`，默认投影成 `{path, label, hasChildren}`，保留 `?verbose=true` 走全量。
   这是 `callGABPTool` 已有的钩子（`:2747` / `:2761-2763`），不用改签名。可参考 `Source/UELoader/UEDebugActions.cs:232-241` 的字段集保持一致。

4. **`MCP/index.js:1836-1844` / README**：在 `rimworld.list_debug_action_children` 的工具描述里补一句"节点很多、默认全量返回，找路径请优先 `search_debug_actions`"。
   纯文案，成本近零。

5. **`README.md:131-141`**：修掉陈旧表格 —— 删 `list_debug_actions` / `get_debug_action_detail` 两行（已删除），`search_debug_actions` 行标注需 `toolConfig` 开启。
   建议与 1–4 合并成一次改动。

**不建议**做的：给 `list_debug_action_children` 强行在 MCP 层实现 `depth` / `pathsOnly` 分页 —— 上游没有对应参数，做了就是 MCP 层重新实现一遍树遍历，与已有的 `search_debug_actions` 功能重叠。按 brainstorming 的 YAGNI，先做 1–3。

---

## 5. 是否已被现有计划覆盖

**否。**

- `docs\superpowers\plans\` 下**只有一份**计划：`2026-09-27-ue-csharp-console-roslyn-upgrade.md`（102,682 字符）。对该文件 grep `list_debug_action_children|search_debug_actions|search_source|start_quick_test|waitForNotification|上下文|context budget|裁剪|截断|truncat` —— **零命中**。
- 该计划的任务是 `## 任务 1`～`## 任务 12`（`:95` / `:304` / `:503` / `:657` / `:750` / `:918` / `:1226` / `:1450` / `:1599` / `:1814` / `:1926` / `:2050`），主题是 Roslyn 编译器升级。唯一沾边的是 `## 任务 12：MCP 文案与端到端验收`（`:2050-2107`），但它只改 `execute_csharp_code` 一条 notes（`:2053`），与本条无关。
- `docs\issue-triage\plans\` 目录**当前不存在**（尚无 PLAN 子代理产出），无重复计划风险。

⇒ 建议派 PLAN 子代理，范围限定为上面的 1–3 项（暴露 + 菜单归位 + `filterResult` 默认投影），第 4–5 项作为附带文案修正。
裁决：值得修

`mcp_help` 的入参只有 `tool` 和 `path` 两个（`MCP/index.js:2809`），没有关键词检索也没有高频快捷区，所以找一个高频工具必须逐层下钻——但这是本项目纯粹的接口设计取舍，改动小、收益直接，且不产生假成功/假阴性。

## 核实过程

### 1. 吐槽板原文（`docs/mcp-吐槽板.md:417-433`）

断言：找一个 `pause_game` 要 `mcp_help{}` → `path:"game_control/game"` → `path:".../state"` 才看到，共 3 次调用。逐条核实。

### 2. 入口 schema 确认没有 `query`

`MCP/index.js:2809`（`AGG_META_TOOLS` 里的 `mcp_help` 定义）：

```js
{ name: 'mcp_help', description: '...无参返回一级概览（6 个类别及计数）；传 path 逐级深入...；传 tool 返回单个工具的详细说明...',
  inputSchema: { type: 'object',
    properties: {
      tool: { type: 'string', description: '要查询的工具名（省略则返回菜单概览/子组）' },
      path: { type: 'string', description: '菜单路径，/ 分隔（如 "bridge/lua_script"、"game_control/game/gameplay/debug_action"）' }
    }, additionalProperties: false } }
```

只有 `tool` / `path`，且 `additionalProperties: false`。**关键词过滤在协议层就不存在。** 实现侧同样确认：`mcpHelpResult`（`MCP/index.js:3646-3720`）只有 `target`（`args.tool`）和 `path`（`args.path`）两个分支，**没有第三个分支**，也没有任何对 `args` 的关键词扫描。

### 3. `pause_game` 的实际深度 —— 确认是 3 层

`MCP/index.js:3027`：`'rimworld.pause_game': 'game_control/game/state'`。

结合 `resolveMenuPath` 的层级定义：
- 一级：`MENU_CATEGORIES`（`MCP/index.js:3183`）= `system / unityexplorer / mono / meta / bridge / game_control`
- `game_control` 下只有 `game` 一个子组（`MCP/index.js:3247-3249` 强制校验）
- `game` 下 10 个子组（`MCP/index.js:3187`），`state` 是其中之一
- `state` 是叶子（`MCP/index.js:3262`），返回工具列表

所以定位 `rimworld.pause_game` 需要：无参（概览）→ `game_control/game`（拿到 10 个子组名）→ `game_control/game/state`（拿到工具列表）。**吐槽板描述的分层与实际完全一致。**

### 4. 现有下钻过程会输出什么（确认信息确实可得，只是路径长）

`MCP/index.js:3683-3719`（无参）返回 6 个一级类别 + `bridge` / `game_control.game` 的子组计数；
`MCP/index.js:3693-3701` 显示 `game_control` 一级只展开到 `game`，`game` 的 10 个子组计数在概览里就已经列出（`gameSubgroups`）。

也就是说，**第 3 次调用（`.../state`）在拿到 10 个子组名之后才需要**——即确实是 3 次往返，无法压缩到 2 次。

### 5. 是否已有替代的发现通道

- `agg_list_tools`（`MCP/index.js:3597-3621`）：输出**全部**可调用工具的名单（连接后 191 个），每行带描述首行。它是一条替代路径，但它是「全量倾倒」而非「按需检索」，在本项目的 MCP 工具集规模下反而更贵 —— 这与吐槽板 [03]、[11]（400 KB / 400+ 条结果冲爆上下文）记录的痛点是同一个矛盾面。
- `MCP/first-configs.md`：全文检索 `mcp_help` / `高频` / `常用` / `工具清单`，**零命中**。首次配置向导里完全没有教 `mcp_help` 的用法。
- `MCP/announcements.md`：只有版本变更公告，无工具速查。

结论：**没有现成的高频工具快捷通道**，吐槽板属实。

### 6. 是否已有 `query` 相关实现在别处（防止重复造轮子）

检索 `MCP/index.js` 中 `mcp_help` 相关的 `inputSchema` 只有一处（`MCP/index.js:2809`），`TOOL_USAGE_NOTES.mcp_help`（`MCP/index.js:2979`）的文案也只提 `tool` 与 `path` 两种用法，无 `query`。**没有半成品可以直接启用。**

## 影响与严重度复评

- **复评严重度：🟡 低，维持不变。** 代价是每次新会话找工具时多 2 次元调用。这几次调用不碰游戏状态、不改任何东西，纯本地查表，成本远低于 [03]/[11] 那两条会冲爆上下文的。
- **假成功 / 假阴性：不会。** `mcp_help` 是纯查询，输出与实际工具注册表同源（`menuUniverseNames()`，`MCP/index.js:3369-3376`，注释明确它是「唯一事实源」），不存在返回过时/错误结论的可能。查不到就是真查不到，路径拼错也会在 `resolveMenuPath`（`MCP/index.js:3216` / `3234` / `3258` / `3272`）给出明确 `未知一级类别` / `未知 xxx 子组` 错误并列出可用值。
- 归为 🟡 的另一个理由：**根因是「入口形态」而非「信息缺失」**。信息全都在，只是不在一个能被关键词直达的入口里。

## 责任归属

**纯本项目（`mcp_help` 自身的接口设计）。**

不涉及 vanilla、不涉及 UnityExplorer、不涉及 MonoMod、不涉及 GABP。「菜单分了几层」是本项目的导航结构（`TOOL_SUBGROUP` / `MENU_*` 常量全在 `MCP/index.js:2905-3197`），层级深度也是本项目自己定的。改第三方只能改 `mcp_help` 在别人那里的样子——而 `mcp_help` 本来就是本项目自己的元工具（`AGG_META_TOOLS`，`MCP/index.js:2806-2810`）。

## 修的话怎么修

按性价比排序。三条**互相独立**，可只做第 1 条：

1. **给 `mcp_help` 加 `query` 参数（推荐，唯一必做项）**
   - 改 `MCP/index.js:2809` 的 `inputSchema`，加 `query: { type: 'string', description: '按工具名/描述关键词过滤（子串，大小写不敏感），可与 tool/path 二选一' }`。
   - 在 `mcpHelpResult`（`MCP/index.js:3646`）的 `target` 分支之后、`path` 分支之前，插入 `query` 分支：遍历 `menuUniverseNames()`（唯一事实源），对 `name` 与描述首行做子串匹配，命中的按 `toolListLine`（`MCP/index.js:3281-3287`，**已带三态可用性标记，可直接复用**）输出。
   - 好处：`query` 在 GABP 离线时照样能命中镜像名并标出 `[当前不可用：GABP 未连接(state=idle)]`——顺带缓解 `07-gabp-idle-no-fail-fast.md` 里「找不到可用工具名」的那一半痛点。
   - 成本极低：不新增依赖，不改菜单结构，不改现有两种调用的行为。

2. **在无参概览里加「高频 N 个」快捷区（可选）**
   - 改 `MCP/index.js:3704-3718` 的 `overview` 对象，加一个 `frequent: [工具名...]` 字段，数据源同样是 `menuUniverseNames()`。
   - 风险提示：**这份名单会立刻过时**。菜单里已经有 `pause_game` 的三处引用（`MCP/index.js:1882` / `2942` / `2943` / `4168`）说明它确实是高频，但任何硬编码的高频清单都要跟着工具增删手工维护。**建议先只做第 1 条**，用实测数据（哪些工具真被高频调）决定要不要引入这份清单，而不是凭感觉列 10 个。

3. **在 `agg_list_tools` 输出里加一行「先试 `mcp_help{query:...}`」引导（可选，一行文案）**
   - 改 `MCP/index.js:3601-3604` 的头部说明。零风险，但只是缓解，不能替代第 1 条。

**明确不建议**：把菜单层级拍平（比如 `game_control` 直接挂 10 个子组）。`MCP/index.js:3361-3368` 的注释记录了此前因「三套口径互相矛盾」导致的计数漂移（概览 194 vs 实际 191），动导航结构会重新踩那类坑。

## 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/` 下只有 `2026-09-27-ue-csharp-console-roslyn-upgrade.md`。逐节核对 12 个任务：任务 1-11 是 Roslyn 载荷 / 引用集合 / 默认 using / polyfill / 编译内核 / REPL / IScriptEvaluator / ConsoleController 接线 / 自动补全 / HookInstance / 打包，与元工具体系无关。
- 任务 12「MCP 文案与端到端验收」（第 2050-2107 行）步骤 1 只改 `MCP/index.js:2937` 的 `execute_csharp_code` notes，**不涉及 `mcp_help` 的 inputSchema 或 `mcpHelpResult`**。
- 全文检索 `mcp_help` 只命中任务 12 之外的两处上下文提及，无对应任务。
- `docs/issue-triage/plans/` 目录当前为空，无重复计划。

## 与 [06] 的同组关系（父任务点名要判）

**不同根因，不建议合并成一个计划任务，但可以共用一次改动窗口。**

- [06] 是**知识缺失**：路径格式规则（`\` 分隔、段是 `name` 不是 `category`）在项目里没有任何权威声明。修法是「往返回值 / 描述 / README 里写规则」，改的是 DebugAction 相关工具的文案与 payload。
- [08] 是**入口缺失**：`mcp_help` 没有关键词直达能力。修法是「给元工具加 `query` 参数」，改的是 `mcp_help` 自己的 inputSchema 与分发逻辑。
- 交叉验证：给 `mcp_help` 加 `query` **修不好** [06]（`query:"debug action"` 只会把 `search_debug_actions` 找出来，仍不告诉你 `path` 该怎么写）；给 [06] 写格式文档也**修不好** [08]（`pause_game` 依然藏在三层下）。两者互不覆盖。

唯一的交集是执行顺序：两者都改 `MCP/index.js` 的工具定义区，若同一批发布，先做 [08] 的 schema 变更再补 [06] 的文案，可避免 `toolCountConsistency.test.js` 之类的测试在中间态失败。
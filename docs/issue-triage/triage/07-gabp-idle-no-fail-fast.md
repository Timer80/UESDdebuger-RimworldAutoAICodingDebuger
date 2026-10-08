裁决：值得修

吐槽板的头号症状「idle 期间调用 `rimworld.*` 是超时不是明确报错」**已被现有代码和真实日志证伪**（实测 3 ms fail-fast）；但同一条目里残留一个真实的、自相矛盾的缺陷：GABP 离线时 `mcp_help{tool:"rimworld.pause_game"}` 返回「未知工具」，而本项目自己的提示文案正叫调用方去用它——范围缩小后仍值得修，严重度从 🟠 下调到 🟡。

## 核实过程

### 1. 吐槽板原文（`docs/mcp-吐槽板.md:395-413`）

三条子断言，逐条核：

| 子断言 | 裁决 |
|---|---|
| (a) `get_game_status` 的 `capabilities` 会写 `usable:false ... note:"GABP 未连接（state=idle）"` | ✅ 属实 |
| (b) 具体哪些 `rimworld.*` 不可用要靠 `mcp_help` 再翻一层才知道 | ⚠️ 部分不实（`agg_list_tools` 已经枚举），但 `mcp_help` 那条路确实是死的 |
| (c) idle 期间调用它们是**超时**，不是明确报错 | ❌ **证伪**，实测 3 ms 明确报错 |

### 2. (a) 属实：`MCP/index.js:3539-3543`

```js
} else if (p.key === 'gabp') {
  usable = src.gabpConnected === true;
  if (!usable) note = loaded === false
    ? 'RimBridgeServer 未在激活列表 → rimworld.*/rimbridge.* 镜像工具全部不可调用（名字正确，缺提供方）'
    : `GABP 未连接（state=${src.gabpState ?? 'unknown'}）`;
}
```

调用点 `MCP/index.js:1284-1295`（`buildCapabilityReport`）传入 `gabpConnected` / `gabpState`，来自 `MCP/index.js:1291-1292`。文案与吐槽板引用逐字一致。

### 3. (c) **证伪** —— 源码层面已经 fail-fast

`MCP/index.js:4274-4280`，GABP 路由分支里在转发**之前**就短路：

```js
if (!gabpBridge || !gabpBridge.isConnected()) {
  const mirror = gabpMirrorUnavailable(name);
  return gabpErrorResponse(mirror
    ? `${mirror.message}。${mirror.guidance}`
    : `GABP 未连接（state=${...}），镜像工具 ${name} 需 RimBridgeServer 连接`);
}
```

`MCP/gabpClient.js:694-695` 的 `isConnected()` 就是 `this.state === 'connected'`，而 `state` 初始值就是 `'idle'`（`MCP/gabpClient.js:112`）。所以 **idle 态必然落进这个分支**，返回 `GABP_MIRROR_UNAVAILABLE`（`MCP/index.js:3583-3593`，含 `module` / `packageId` / `state` / 明确 `message` / `guidance`）。

运行时日志直接证实（`MCP/logs/mcp-20261001-093829.log`）：

- 第 4 行：`GABP 桥接初始化完成（state=idle，惰性重连模式）`
- 第 961 行：`tools/call agg_call_tool args={tool=rimworld.execute_debug_action, args={"path":"Actions\\Stage Test Map"}} → 3ms error(GABP_MIRROR_UNAVAILABLE)`
- 第 938-939 行紧邻的上文：`17:43:05.141 GABP 连接断开: connection lost` → `GABP 断开（reason=connection lost）；下次工具调用将惰性重连`

**3 ms**。这不是超时，是设计好的快速失败。

### 4. 但日志里确实有超时 —— 根因完全不同，不能混为一谈

同一份日志第 405-421 行：

```
[14:38:46.758] <DEBUG> GABP 请求超时计数 1/3（rimworld/get_debug_action）
[14:38:46.758] <INFO> tools/call agg_call_tool args={args={"path":"Actions\\运行全部动画测试"}, tool=rimworld.execute_debug_action} → 30013ms error(GABP_ERROR)
[14:41:53.298] <WARN> GABP 连续 3 次 tools/call 超时，判定 RimBridge 假死，强制重连
```

这段发生在 **GABP 已连接** 之后（第 371-374 行刚握手成功、镜像工具同步 121 个），是 RimBridgeServer 在游戏线程执行 DebugAction 时假死。**这属于另一个根因（第三方 RBS 假死），不能用来支持 [07] 的「idle 不 fail-fast」结论。** 吐槽板很可能把这两件事混成了一件。

### 5. (b) 部分不实 + 一个真实残留缺陷

**不实的部分**：`MCP/index.js:3605-3612` 的 `aggListToolsText()` 在 GABP 未连接时**已经枚举**了缺席的镜像：

```js
if (!gabpConnected) {
  const missing = Object.keys(toolConfig.tools).filter(n => isGabpMirrorName(n) && !gabpToolNames().has(n));
  lines.push(`# 注意：GABP 未连接 → ${missing.length} 个 rimworld.*/rimbridge.* 镜像工具当前**不可调用**（例：...）...`);
}
```

而且菜单叶子节点也会标：`MCP/index.js:3281-3287` 的 `toolListLine` 追加 `（GABP 未连接(state=idle)）`，其数据源 `menuUniverseNames()`（`MCP/index.js:3369-3376`）会把 `TOOL_SUBGROUP` 里已登记的镜像名并入，即使离线。已核实 `MCP/toolConfig.json` 里 `rimworld.pause_game` 等 121 个镜像键全部在册，所以离线时叶子列表**看得到**它们。

**真实的缺陷**：`mcp_help{tool: <镜像名>}` 在离线时**查不到**。`MCP/index.js:3646-3656`：

```js
const entries = allToolEntries();
...
const meta = entries.find(t => t.name === target);
if (!meta) {
  return {... error: `未知工具: ${target}`, hint: `...` ...};
}
```

而 `allToolEntries()`（`MCP/index.js:3352-3359`）= `[...TOOLS, ...AGG_META_TOOLS, ...gabpTools]` + mono 占位。`gabpTools` 在断连时被清空（`MCP/index.js:2514` 注释「仅 isConnected() 时非空」、`MCP/index.js:2649` `gabpTools = []`）。**没有兜底分支**，于是返回「未知工具」。

矛盾点在于，本项目自己的文案正指向这条死路：
- `MCP/index.js:3611`：`对应能力说明见 mcp_help tool=<名字> 的 precondition。`
- `MCP/index.js:4284`：`（当前镜像清单见 agg_list_tools / mcp_help path:"bridge"）`

第一条在 GABP 离线时**必然失败**。这不是「体验略糙」，这是**错误归因**：调用方（人或 AI）看到「未知工具」会合理地推断「我名字写错了」，于是开始猜别的名字、反复重试——而真实原因是提供方没连上。

### 6. 是否已有 workaround

有部分 workaround：`mcp_help path:"game_control/game/state"` 的叶子列表能看到 `rimworld.pause_game` 并带 `[当前不可用：GABP 未连接(state=idle)]`（`MCP/index.js:3464-3469` 的 `toolAvailabilityTag`）。所以信息**拿得到**，只是要多下钻两层、且要走对路径。这解释了为什么问题被标成 🟠 而不是 🔴——它确实不阻塞。

## 影响与严重度复评

- **复评严重度：🟠 → 🟡 下调。**
- **假成功 / 假阴性：不会。** 离线调用 `rimworld.*` 返回的是结构化错误（`errorCode=GABP_MIRROR_UNAVAILABLE`、`state=idle`），不是成功；`get_game_status` 的 `usable:false` 也是真值。自动化不会因此得出相反结论。
- 真实代价是**归因误导导致的往返浪费**：错误信息说「未知工具」，Agent 的正确反应（改名字）必然失败，错误反应（试别的名字）会持续烧 token。这属于 🟡~🟠 边界，考虑到已经有 `agg_list_tools` 与菜单叶子作为兜底可达路径，取 🟡。
- **同组对比（[07] vs [09]）是否同一根因：否。** 两者共享「返回值缺少可信度元数据」这一**模式**，但实例不同、修法不同：[07] 要补的是「提供方状态」，[09] 要补的是「数据源世代」。不存在一处改动同时修好两条的路径。详见 `09-player-log-rotation.md` 的同组小节。

## 责任归属

**本项目（`mcp_help` 的工具元数据查询逻辑）。**

- idle 状态本身：**不是本项目的锅**，也不是 vanilla 的锅 —— 是 GABP 客户端的惰性重连设计（`MCP/gabpClient.js:138-183`，`ensureConnected` 按需触发）。RBS 侧要等游戏进主菜单才起服务（`README.md:160`），这是第三方模组的行为。
- 日志第 405-421 行的 30 s/60 s 超时：**第三方 RimBridgeServer** 的假死。本项目已经做了缓解（连续 3 次超时判定假死并强制重连，`MCP/logs/.../mcp-20261001-093829.log:411-412`），无需再处理。
- 真正要修的 `mcp_help` 对离线镜像返回「未知工具」：**100% 本项目**，`MCP/index.js:3646-3671` 一处逻辑，不需要改 vanilla，也不需要改第三方。

## 修的话怎么修

改动集中在 `MCP/index.js`，一处为主、一处为辅：

1. **主修（必做）**：`MCP/index.js:3652-3656`。在 `entries.find(...)` 未命中之后、`return` 错误之前加一层「已知镜像名」兜底：复用已有的 `isKnownGabpMirrorName(name)`（`MCP/index.js:3429-3435`）与 `gabpMirrorUnavailable(name)`（`MCP/index.js:3567-3594`），返回一份**离线占位详情**——至少包含 `name` / `category` / `availability: {state:'unavailable', reason:'GABP 未连接(state=idle)'}` / `precondition: '需 RimBridgeServer 连接'`（`toolPrecondition` 在 `MCP/index.js:3336` 已经会对 bridge 类返回这句）。这样文案 `MCP/index.js:3611` 才成立。

   注意：`suggestTools()`（`MCP/index.js:3979-4002`）遍历的是 `allToolNames()`，同样不含离线镜像，兜底分支里若要带 `suggestions`，需先让它感知 `TOOL_SUBGROUP` 登记名，否则会返回空数组。

2. **辅修（建议）**：`MCP/index.js:4276-4279` 的 fail-fast 响应里，`gabpMirrorUnavailable` 已有 `guidance`，可再补一句「本工具的完整用法可查 `mcp_help tool=<名字>`」——前提是第 1 条先落地，否则又是一次死路指引。

3. **不推荐**：把 idle 判据前移成「游戏进程在但未握手就绪」的独立状态。当前 `get_game_status` 已经通过 `capabilities` + `gabp.state` 把这个信息给全了，再加一层只会制造第二套口径。

4. **明确不做**：把 GABP 镜像的 schema 缓存下来（连接时存一份、离线时复用）。这会让「名字正确但调不到」看起来像「可调用」，与 `toolAvailability` 的三态设计（`MCP/index.js:3437-3462`）直接冲突，属于倒退。

## 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/` 下只有 `2026-09-27-ue-csharp-console-roslyn-upgrade.md` 一份，12 个任务全部围绕 Roslyn 编译器升级。与本条相关的只有任务 12「MCP 文案与端到端验收」，其步骤 1（第 2057-2063 行）只改 `MCP/index.js:2937` 的 `execute_csharp_code` notes，步骤 2-5 是 `execute_csharp_code` 的验收矩阵、Text20 回归、Roslyn 回退路径、commit——**不涉及 `mcp_help`、不涉及 GABP 状态可见性**。
- 全文检索无 `mcp_help` 相关任务、无 `GABP_MIRROR_UNAVAILABLE` 相关任务。
- `docs/issue-triage/plans/` 目录当前为空，无重复计划。
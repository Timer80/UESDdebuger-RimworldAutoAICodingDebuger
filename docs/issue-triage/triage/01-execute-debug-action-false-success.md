裁决：值得修

MCP 层在 `MCP/index.js:2690-2694` 无条件把「GABP 回包没有 JSON-RPC error」渲染成 `success: true`，完全不看回包里 RBS 自己给的 `operation.Success` / `effects.logCount` / `durationMs`，所以「动作压根没跑」和「动作跑成功了」在返回值上**不可区分** —— 这是教科书式的假成功，且 100% 由本项目自己的代码制造。

## 核实过程

### 1. 吐槽板原文（`docs/mcp-吐槽板.md:235-267`）

断言：调用返回 `success:true` + `node` 完整 + `operation.Success:true`，但 `effects.logCount:0` / `effects.logs:[]` / `durationMs:72`（同一条正常执行 2097ms）。

### 2. `rimworld.execute_debug_action` 是纯 GABP 镜像，本项目没有自己的执行实现

`MCP/index.js:4332-4352`（`isGabpMirrorName` 分支的尾部）：

```javascript
const _rbs = name.replace(/\./g, '/');
const _derived = timeoutOptsFor(_rbs, args, config);
const _slow = /^(rimworld|rimbridge)\.(load_game|save_game|execute_debug_action)$/.test(name);
const _opts = _derived || (_slow ? { slow: true } : null);
...
const _r = await callGABPTool(_rbs, args, undefined, _opts);
noteDpaStateAfterCall(name, _r);
return _r;
```

入参原样转发、回包原样返回。`MCP/index.js:2641` 与 `:4899-4901` 的注释确认原生 `execute_debug_action` 已被 tool-cleanup 删除，现在唯一入口就是 GABP 镜像。`README.md:160` 说明 `rimworld.*` 由 Steam 创意工坊的 RimBridgeServer（`brrainz.rimbridgeserver`）提供。

### 3. `success: true` 是本项目盖的章，与「动作是否执行」无关

`MCP/index.js:2747-2764`：

```javascript
const res = await gabpBridge.callTool(rbsName, args || {}, callOpts);
if (res && res.ok) {
  gabpConsecutiveTimeout = 0; // 成功响应重置超时计数
  let data = res.result;
  ...
  return { content: [{ type: 'text', text: JSON.stringify(wrapGABPResult(data, true), null, 2) }] };
}
```

`MCP/index.js:2690-2694`：

```javascript
function wrapGABPResult(raw, ok) {
  return ok
    ? { success: true, data: raw }
    : { success: false, errorCode: 'GABP_ERROR', error: String(raw) };
}
```

而 `ok` 的唯一来源是 `MCP/gabpClient.js:667-684`：

```javascript
async callTool(name, args, opts = {}) {
  ...
  const res = await this._request('tools/call', { name, parameters }, opts);
  let result = res;
  if (result === null || typeof result !== 'object') { result = { value: result }; }
  return { ok: true, result };
```

配合 `MCP/gabpClient.js:644-648`：`if (msg.error) reject(...) else entry.resolve(msg.result)`。

**即：`ok` 只等价于「JSON-RPC 层没报错」。** 传输层成功 = `success: true`。回包 `data` 里 RBS 自己写的任何判据（`operation.Success`、`effects.logCount`、`durationMs`）在这条链路上**一个都没有参与判定**。吐槽板说的「回显请求参数就算成功」属实，且比它自己写的更严重 —— 不是回显参数，是**无条件盖章**。

### 4. 时序指纹在真实调用日志里对得上

`MCP/logs/mcp-20261001-093829.log`（同一条 `Actions\Stage Test Map`）：

| 行号 | 时间 | 耗时 | 结果 |
|---|---|---|---|
| 249 | 14:04:18 | **2174ms** | success |
| 291 | 14:13:46 | **2621ms** | success |
| 687 | 15:58:15 | **2948ms** | success |
| 254 | 14:09:12 | **137ms** | success |
| 295 | 14:15:00 | **111ms** | success |
| 400 | 14:32:09 | **85ms** | success |
| 750 | 16:09:09 | **89ms** | success |
| 799 | 16:24:16 | **83ms** | success |
| 296 | 14:15:04 | **71ms** | success（`Actions\Inject Cell Cost Tests`）|

**71ms / 83ms / 85ms / 89ms 这一档，与吐槽板写的「72ms」是同一现象**（同一次会话、同一份日志）。短耗时档全部报 `success`，长耗时档也报 `success` —— 返回值层面零区分度，吐槽板的判据成立。

14:15:00 与 14:15:04 两次短耗时调用之前 31 秒（14:14:29）是 `start_game{timeout:240000}` 跑了 **252458ms 后 error(unknown)**（日志第 293 行）—— 游戏当时确实处在不正常状态，与 [01] 描述的「主线程被 OnGUI 异常循环打挂」吻合。

### 5. RBS 回包里那些字段，本仓库无法核实

全仓库 grep `logCount` / `longEventPending` / `stateBefore` / `durationMs`，**在 `docs/mcp-吐槽板.md` 之外零命中**（`durationMs` 的命中全是 `play_for` 的入参语义，见 `MCP/index.js:4110-4148`、`MCP/longTask/timeoutPolicy.js:22`）。这些字段名只出现在吐槽板正文里，RimBridgeServer 的源码不在本仓库内，**其内部是否真的「入队成功即返回 success」我无法核实**。因此本报告不对 RBS 的行为下断言，只对 MCP 层的盖章行为下断言 —— 而后者已经足够定性。

### 6. 现有的超时保护对这条路径无效

`MCP/index.js:2769-2785` 的假死检测只在**超时**时计数。71ms 的调用根本不超时，既不计数也不触发 `forceReconnect`。也就是说 MCP 现有的健康保护对这批「秒回假成功」完全无感。

## 影响与严重度复评

**复评：🔴 高，维持不变。是本批三条里唯一的「事实相反」型缺陷。**

- **假成功确凿**：`success:true` + `node` 完整 → 自动化读成「跑了」。随后 `effects.logCount:0` 会被读成「跑了但没输出」而不是「没跑」。这两个世界的后续动作完全相反：前者记 PASS，后者应当记 NOT-RUN。
- **无法从返回值自证**：因为 `success` 不携带执行信息，调用方只能靠 `durationMs` 这种性能侧信道去猜（本条吐槽的作者就是这么发现的）。这不是设计，是被迫的反推。
- **与 [02] 的耦合**：`triage/02-execute-csharp-code-log-not-in-player-log.md` 独立核实了「`execute_csharp_code` 的返回值拿不到用户代码的日志」。也就是说本条唯一的兜底通道在 [01] 触发时**同时是瞎的**，两条叠加才是「彻底无法自证」的完整解释。
- **不影响 GABP 不可用时的行为**：未连接时走 `MCP/index.js:4274-4286`，返回 `GABP_MIRROR_UNAVAILABLE` + `isError:true`，这一层是对的。问题只在「连着、回包了、但动作没跑」这一种情形。

## 责任归属

**本项目（MCP 层），主责。第三方 RimBridgeServer，次责且无法核实。**

- 主责依据：`success:true` 的盖章动作发生在 `MCP/index.js:2690-2694` / `:2757-2764`，全在本仓库内，RBS 的回包内容在这里被原样封装、无任何判据介入。**即使 RBS 返回的一切都正确，这段代码也一定会输出 `success:true`。** 这不是继承来的行为，是自己写的。
- 次责（RBS）：如果它把「已入队」当成「已执行」来报，那是它的问题；但它的源码不在仓库内，**我无法核实，只能标注为假设**。
- 不是 vanilla RimWorld 的锅：`Dialog_Debug` / `DebugActionNode` 只负责把动作入队，执行与否是执行器的事，vanilla 侧没有「报告成功」这个概念。
- 不需要改 vanilla、不需要改第三方才能修主责部分。

## 修的话怎么修

方向（具体实现交给 PLAN 角色），全部落在本仓库：

1. **停止无条件盖章 —— `MCP/index.js:2690-2694` / `:2757-2764`（必做，最小改动）**
   `wrapGABPResult` 增加一个「语义判据」入参：对已知的写操作类镜像（至少 `execute_debug_action`），先探 `data.operation.Success === false` 或 `data.effects?.logCount === 0`（且 RBS 另有 `executed` 类字段时优先用它），命中则返回 `success:false` + `errorCode:'REMOTE_REPORTED_FAILURE'`，把 `data` 原样挂在 `data` 字段上不丢信息。
   **注意**：判据必须能区分「RBS 明确报失败」与「RBS 没给这个字段」，后者不能误判 —— 这是 [01] 的反面风险（把真成功判成失败），PLAN 里要有对应的反例用例。

2. **把「传输成功」和「动作成功」拆成两个字段 —— `MCP/index.js:2690`（推荐）**
   返回 `{ success, transport: 'ok', executed: <bool|null>, durationMs: <RBS 值> }`。`executed` 为 `null` 表示 RBS 未提供执行凭据，调用方据此知道「无法判定」而不是「成功」。这样即使 RBS 长期不修，本项目也不再输出误导性的单一布尔。

3. **传输层异常形态快速判失败 —— `MCP/index.js:4334-4335`（低成本兜底）**
   `_slow` 已把该工具的预算放大到 2 倍（`MCP/longTask/timeoutPolicy.js:18`）。可再加一条：GABP 回包耗时 **显著低于**该动作的历史下限时，在返回里附一条显式告警字段（例如 `suspiciouslyFast: true` + 建议复查 Player.log），而不是静默盖章。**这条只能作提示，不能改 `success`** —— 性能侧信道不能当判据。

4. **文档层（必做，成本最低）**
   - `MCP/index.js:1850` 附近 `stop_game` 同级的工具描述区、以及 `MCP/index.js:2930` 的 `TOOL_USAGE_NOTES` 里给 `rimworld.execute_debug_action` 补一条 notes：`GABP 镜像的 success 仅代表传输成功，不代表动作已执行；执行判据见返回的 operation/effects 字段`。
   - `README.md:172`（RimBridgeServer 镜像工具表）同句。

5. **不建议做的**：在 MCP 层包一个「执行后轮询 Player.log 行数变化」的伪执行凭据。Player.log 会轮转（[09]）、会被本项目的 `UELoader.OnUELog` 二次包装（[15]）、还会在主线程卡死时零写入 —— 三条已记录的问题都会让它变成新的假判据源。

## 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/` 下只有一份计划：`2026-09-27-ue-csharp-console-roslyn-upgrade.md`（2129 行 / 12 个任务）。任务清单：1 Roslyn 载荷、2 默认引用集合、3 默认 using、4 polyfill、5 编译内核、6 REPL 会话、7 `IScriptEvaluator`、8 `ConsoleController` 接线、9 自动补全、10 `HookInstance` 迁移、11 打包许可、12 MCP 文案与端到端验收。全文无 `execute_debug_action`、无 `wrapGABPResult`、无 GABP 判据相关任务；任务 12 步骤 1 只改 `MCP/index.js:2937` 的 `execute_csharp_code` 文案。
- `docs/issue-triage/plans/` 目录**当前不存在**（`docs/issue-triage/` 下只有 `triage/06-debug-action-path-format.md` 一份同角色产物），无重复计划风险。
- 同角色的 `triage/06-debug-action-path-format.md` 第 118 行把「静默改写路径后执行」明确列为不建议项 —— 与本条第 3 点的「性能侧信道不能当判据」是同一条纪律，两份报告互相印证。

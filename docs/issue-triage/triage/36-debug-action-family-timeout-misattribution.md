裁决：值得修（但板上归因必须整体重写 —— 「整个族全灭」不成立）

# [36] `rimworld.*` 调试动作族「一律 30s 超时且不执行」—— 归因错误，实为 RimBridgeServer 侧的主线程楔死 🟠

**一句话结论**：板上 `[36]` 的**现象是真的**（那段时间里 `execute_debug_action` / `get_debug_action` / `list_debug_action_roots` 确实全部超时且没执行），但它的**三条关键论断都被实测推翻** —— ①「**一律 30s**」错：`execute_debug_action` 走的是 `timeoutPolicy.js:18` 的**慢档 60s**，实测 60024 / 60010 / 60027 / 60041ms；②「**整个族全灭**」是**采样偏差**：同一份日志里 `execute_debug_action` 有 **146 次成功**（35ms–5.2s）、`get_debug_action` 有一次 **16ms 成功**；③「**超时无法与『正在执行』区分**」**成立，而且比板上写的更严重** —— 反编译证明超时**根本不取消**任何东西，被卡住的工作项仍留在 `RimBridgeMainThread.Pending` 队列里，主线程一旦恢复就会**照样把动作执行掉**。⇒ **不是 `[32]`/`[24]` 那张 `timeoutPolicy.js` 白名单缺登记**（`execute_debug_action` 本来就登记在慢档里），**也不属计划 31 的重放问题**（它根本没有重放）。真正的根因是 **RimBridgeServer 侧 Unity 主线程楔死**，而 MCP 侧对这种楔死的处置是**错的**（重连 TCP 无效，因为卡的是游戏进程不是 socket）。

---

## 0. 坐标与证据纪律声明

| 项 | 值 |
|---|---|
| 吐槽板正文 | `docs/mcp-吐槽板.md:55-117`（`## [36]` 标题在 `:55`，下一条 `## [35]` 在 `:119`）—— **正文已落盘**，非「只有索引行」 |
| 吐槽板索引行 | `docs/mcp-吐槽板.md:53` |
| 吐槽板总行数 | `1535`（`[System.IO.File]::ReadAllLines(...).Count` 复核） |
| 本报告全部代码行号 | 均用 `[System.IO.File]::ReadAllLines(...)` 复核。`MCP/index.js` = **5460** 行 / `MCP/gabpClient.js` = **727** 行 / `MCP/longTask/timeoutPolicy.js` = **154** 行 |
| **实测日志（本条的决定性证据）** | `MCP/logs/mcp-20261001-222618.log`，快照时 **1644 行**。该文件被运行中的 MCP 进程独占（`ReadAllLines` 报 `being used by another process`），故改用 `File.Open(..., Read, ReadWrite)` + `StreamReader` 只读读取，**全程未写入、未触碰游戏** |
| RBS 证据形式 | decompilerserver 反编译（无 .cs 源码），以 **memberId** 引用 |
| 反编译程序集 | `RimBridgeServer.dll`（MVID `46e27953d7324877a067e7458bf2c0d5`，689 类型 / 3188 方法）、`Lib.GAB.dll`（`02339f43725c4ef6a41454c77507e911`，94 类型 / 294 方法）、vanilla `Assembly-CSharp.dll`（`61e4173561894da49d210260257b5097`，MVID 取自 `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\`） |
| ⚠️ 未采信 | `GABS-main\`（Go 参考实现）—— 非运行时路径，全程未作为 RBS 行为证据 |
| ⚠️ 未执行 | 任何会动游戏状态的 MCP 工具。本条全部结论来自静态阅读 + 一份**已存在的**日志副本 |

> **板上引用会被本作废**：§3 会逐条指出板上 `[36]` 正文里哪几句话与实测冲突，主代理需要打批注。

---

## 1. 「整个族全灭」是采样偏差 —— 同一份日志里有 146 次成功

板上 `[36]` 正文 `:57-77` 描述的复现窗口，在日志里精确定位到 **22:51:59 – 22:55:25**（板上没写时间，是从调用序列反推的）。但同一份日志的其余部分直接推翻了「全灭」：

| 日志行 | 时间 | 调用 | 结果 |
|---|---|---|---|
| `MCP/logs/mcp-20261001-222618.log:862` | `[14:49:38.233]` | `rimworld.get_debug_action{"Actions\Toggle FlightAStar Interception"}` | **16ms success** ⬅ **板上点名「全灭」的那个工具** |
| `:21` | `[23:22:54.616]` | `rimworld.execute_debug_action{"Actions\Stage Test Map"}` | 5241ms success |
| `:53-70` | `[23:50]–[23:59]` | 18 次 `execute_debug_action`（Hot Query Guard / Run All Tests / …） | 全部 success，**35ms – 2786ms** |
| `:187` | `[00:44:47.484]` | `rimworld.execute_debug_action{"Actions\Stage Test Map"}` | **4529ms success** |
| `:214` / `:215` | `[00:50:12.752]` / `[00:50:19.268]` | `execute_debug_action`（Stage Test Map / Hot Query Guard Tests） | **408ms / 162ms success** —— 就在同一次楔死**恢复之后** |
| `:1494` | `[22:36:17.619]` | `rimworld.execute_debug_action{"Actions\GOD 1 建造并生成飞船"}` | **60024ms** error(GABP_ERROR) |
| `:1498` | `[22:38:27.721]` | `rimworld.execute_debug_action{"Actions\Hot Query Guard Tests"}` | **60010ms** error(GABP_ERROR) |
| `:1531` | `[22:53:16.985]` | `rimworld.execute_debug_action{"Actions\GOD 1 建造并生成飞船"}` | **60027ms** error(GABP_ERROR) |
| `:1571` | `[23:08:12.342]` | 同上 | **60041ms** error(GABP_ERROR) |
| `:1535` | `[22:54:55.097]` | `rimworld.get_debug_action{"Actions\GOD 1 …"}` | 30031ms error(GABP_ERROR) |
| `:1540` | `[22:55:25.147]` | `rimworld.list_debug_action_roots{"Actions"}` | 30034ms error(GABP_ERROR) |

**全日志逐条 tally（对 `tool=rimworld.execute_debug_action.*→\s*(\d+)ms\s+(\w+)` 的完整枚举，不抽样）：`success = 146`，真实 `error = 5`**（另有 1 行是 `mcp_help{tool=…}` 的说明查询，非调用，不计）。

⇒ **失败率 5/151 ≈ 3.3%，且 5 次全部落在同一个约 20 分钟的窗口里。** 这不是「工具坏了」，是**一段有时限的故障窗口**。板上把窗口内的连续失败读成了「整个族一律如此」，属于典型的**采样偏差 → 过度归因**。

**为什么作者会这么读**：楔死窗口的**最后一次** `execute_debug_action`（`:1571`，`[23:08:12]`）也是失败，之后该会话就没再用它 —— 所以「最后看到的印象」确实是「全灭」。但同一份日志往前翻 8 小时，成功记录密密麻麻。**⇒ 板上 `:73-74` 的「整个调试动作族全灭，等于当轮没有任何正规入口能驱动 DebugAction」必须改写为「一段约 20 分钟的窗口内 GABP 通道无响应」。**

---

## 2. 逐个工具：走 GABP 镜像还是原生（复核，不采信转述）

**复核结论：`[22]` triage 转述的那条是对的，且本报告独立复核过。** 但还必须补两条板上没提的。

| 工具（板上写法） | 真实入口 | 判定依据（已复核行号） |
|---|---|---|
| `execute_debug_action` | **GABP 镜像** `rimworld.execute_debug_action`；**原生实现已删** | `MCP/index.js:4899`（注释「tool-cleanup：list_debug_actions / get_debug_action_detail / execute_debug_action 已删除」）、`:4901`（明写「DebugAction 操作全部走 GABP 镜像」）。原生 `TOOLS` 注册表里**没有** `name: 'execute_debug_action'` —— 对 `execute_debug_action` 的全文检索只命中 `:2357/:2411` 两处**描述文本**、`:2496/:2641/:2913/:3164/:3747/:4334/:4338` 若干注释与正则，**没有一处 `name:` 注册**。菜单归属 `TOOL_SUBGROUP` `:3164` = `game_control/game/debugmenu` |
| `get_debug_action` | **GABP 镜像**；原生已删 | `MCP/index.js:3747-3748`（「list_debug_action_roots / list_debug_action_children / get_debug_action / execute_debug_action 对应原生工具已完全清除」）、`:3163` 菜单归属 |
| `list_debug_action_roots` | **GABP 镜像**；原生已删 | 同上（`:3747`、`:3161`） |
| `list_operations` | **GABP 镜像** `rimbridge.list_operations`（板上漏了前缀） | `MCP/index.js:3014` `'rimbridge.list_operations': 'bridge/ops'`；全文无 `name: 'list_operations'` 的原生注册 |
| `search_debug_actions`（对照，正常） | **原生**，走 **UnityExplorer HTTP**，**完全不经过 GABP** | `MCP/index.js:4934` `case 'search_debug_actions':` → `:4949` `callUnityExplorerAPI('/debugaction/search?…','GET')`；镜像 `rimworld.search_debug_actions` 被 `GABP_DEDUP_TOOL_NAMES`（`:2498-2500`）隐藏并经 `DEDUP_MIRROR_TO_NATIVE`（`:3750-3752`）映射回原生 |
| `pause_game`（对照，正常） | **GABP 镜像** `rimworld.pause_game`；**没有原生实现** | `MCP/index.js:3027` `'rimworld.pause_game': 'game_control/game/state'`；全文检索 `pause_game` 在 `MCP/**/*.js` 中除 `runner.js` 的 `rimworld/pause_game` 内部调用与三处提示文案外，**无 `case 'pause_game'`** |
| `read_rimworld_log`（对照，正常） | **原生**，读本地文件，不经任何通道 | `MCP/index.js:4391` `case 'read_rimworld_log':` → `readLog()` |

**⚠️ 板上 `:71` 的「同一时刻 `pause_game` 正常」经不起逐字核对**：`pause_game` 是**和三个死掉工具一模一样的 GABP 镜像**，它「正常」并不能区分出「调试动作族」这个维度。日志里它在楔死窗口内**一次都没被调用过**（最近一次 `[22:52:08.593]` 在窗口**开始之前**，`:1529`）。⇒ 板上用来做对照的那条证据，**对照组和实验组走的是同一条通道**，不构成对照。

---

## 3. 🚩 必答：是不是 `[32]` 同一处 `timeoutPolicy.js` 白名单缺登记？—— **否**

### 3.1 逐张白名单核对（`MCP/longTask/timeoutPolicy.js`，154 行，已复核）

| 档 | 行号（已复核） | 内容 | `execute_debug_action` | `get_debug_action` / `list_debug_action_roots` / `list_operations` |
|---|---|---|---|---|
| T4 显式覆盖 | `:106-109` 读 `t.toolTimeouts` | `MCP/config.json` 单行 5 键、无 `rimBridge` ⇒ `readThresholds` `:63-65`/`:82-84` 容错成 `{}` | ❌ | ❌ |
| T3 入参推导 | `:21-25` `INPUT_DERIVED` | `play_for` / `step_game_ticks` / `play_until_letter` | ❌ | ❌ |
| **T2 慢档** | **`:18` `SLOW_BARE_NAMES`** | **`['load_game','save_game','execute_debug_action','create_hook']`** | ✅✅✅ **已登记！** | ❌ |
| T2 长默认 | `:28` `LONG_DEFAULTS_MS` | `step_game_ticks`→120000 / `play_until_letter`→300000 | ❌ | ❌ |
| ⇒ `:147-148` / `:150` | | `SLOW_BARE_NAMES.has(bare)` ⇒ `{timeoutMs: baselineMs(bare,t), inputDerived:false}` | **60000ms** | **落 `return null` ⇒ 30000ms** |

⇒ **`execute_debug_action` 根本不缺登记**。它经 `timeoutPolicy.js:147-148` 拿到 `{timeoutMs: 60000}`，再到 `gabpClient.js:563-565` 的 `opts.timeoutMs` 分支 ⇒ **60 秒**。板上「一律 30s」对它**逐字错误**。

### 3.2 实测日志独立印证（不经推导，直接看数字）

| 日志行 | 工具 | 实测耗时 | 对应预算 |
|---|---|---|---|
| `:1494` | `execute_debug_action` | **60024ms** | 60s ✔ |
| `:1498` | `execute_debug_action` | **60010ms** | 60s ✔ |
| `:1531` | `execute_debug_action` | **60027ms** | 60s ✔ |
| `:1571` | `execute_debug_action` | **60041ms** | 60s ✔ |
| `:190` | `execute_debug_action` | **60016ms** | 60s ✔ |
| `:1535` | `get_debug_action` | 30031ms | 30s ✔ |
| `:1540` | `list_debug_action_roots` | 30034ms | 30s ✔ |
| `:1503` | `rimbridge.list_operations` | 30045ms | 30s ✔ |
| `:198` | `rimworld.select_pawn` | 30038ms | 30s ✔（这就是 `[24]`！） |

⇒ **预算推导链完全按 `timeoutPolicy.js` 预测地工作。** `execute_debug_action` 一律 60s、其余一律 30s —— 板上把 60s 写成了 30s。

### 3.3 三条决定性的「不是白名单问题」证据

| # | 证据 | 为什么排除白名单假设 |
|---|---|---|
| **A** | `execute_debug_action` **已经登记**在 `:18` 慢档、拿到 **60s**，**照样超时**（`:1494` 60024ms / `:1531` 60027ms） | 若是「预算不够」，翻倍成 60s 应该能过。60s 不过是因为**游戏侧压根没回包** |
| **B** | `get_debug_action` 在 `:862` **16ms success** | 同一个工具、同一条通道、同一份 30s 预算，**16ms 就回来了**。工具本身不慢 |
| **C** | `rimworld.select_pawn`（`:198`，**与 DebugAction 毫无关系**）在同一窗口里也是 30038ms 超时 | 白名单缺登记是**逐工具**的缺陷，不可能整齐地打到「选中小人」这种不相干工具上。**能同时打中它们的只有「通道/进程」这一个共同因子** |

### 3.4 错误码也对不上：板上记的是 `-32001`，服务端产出的其实是 `GABP_ERROR`

- 板上 `:59` 写的是 `MCP error -32001: Request timed out`。
- `-32001` + 逐字文本 `Request timed out` 在**全仓库只有一个出处**：`MCP/node_modules/@modelcontextprotocol/sdk/dist/esm/shared/protocol.js:713`（`cancel(McpError.fromError(ErrorCode.RequestTimeout, 'Request timed out', { timeout }))`），且它是 `Protocol.request()` 里的**客户端**截止线。同文件 `:8` `DEFAULT_REQUEST_TIMEOUT_MSEC = 60000`。
- **检索范围**：对 `MCP/**/*.js` 递归检索 `32001|RequestTimeout|Request timed out`，`node_modules` 之外**只有 2 处命中且都不是产出点** —— `MCP/index.js:2712`（`isGabpErrorTimeout` 的**匹配**正则）与 `:2772`（假死计数的**匹配**正则）。服务端自己的产出点是 `MCP/gabpClient.js:568` `` `GABP 请求 '${method}' 超时（${timeoutMs}ms）` ``，经 `MCP/index.js:2789` 包成 `gabpErrorResponse` 返回**工具结果**（不是协议错误）。
- decompilerserver 在 `RimBridgeServer.dll`（689 类型 / 3188 方法）里对字面量 `-32001` 检索 ⇒ **0 命中** ⇒ RBS 也产不出它。

⇒ **推论**：日志里服务端在 60s / 30s 时**确实**产出了 `GABP_ERROR`（那 5 条 `error(GABP_ERROR)` 就是），但**宿主客户端的截止线比 60s 更早先放弃**，于是 AI 看到的是 `-32001`。**⇒ 板上感知到的「30s」= 客户端截止线，不是本仓库的预算。** 这与 `triage/04-startup-call-timeout-ambiguity.md` 测到的客户端截止线机制是同一回事（只是那次落在 131–193s，这次显然更短）。

### 3.5 归并判定

| 问题 | 结论 |
|---|---|
| 与 `[32]`/`[24]` 同一处白名单缺登记？ | **否。** `execute_debug_action` 在 `timeoutPolicy.js:18` 早已登记（60s）；其余三个确实未登记（30s），但 §3.3 的 A/B/C 三条证据表明**未登记不是致因** |
| 是否并入 `plans/2026-10-02-32-gabp-per-call-timeout-derivation.md`？ | **否。** ①该计划 §0.1/§4.5 的决策表钉死「`load_game` 60000 不变」「未登记工具一律回落 30s」为**回归护栏**；把 `get_debug_action` / `list_debug_action_roots` 加进白名单会把 30s 抬到 120s，只是把 30 秒的失败换成 120 秒的失败 —— §3.3 的 A 已证明翻倍无效。②`execute_debug_action` 在那张表里**没有位置**，它已经是 60s |
| 建议 | **单立**，且**根因层从「超时预算」改判为「游戏进程楔死 + MCP 侧处置错误」** |

## 4. 必答：那个 30s / 60s 到底花在哪？（反编译逐层核实）

板上 `:75-77` 说「实测主线程 tick 在超时期间正常推进（`ue_game_tick` 持续增长）」，并据此说「超时无法与『正在执行』区分」。**前半句和后半句都对，但因果被搞反了**，而且**实际情况比板上写的更糟**。

### 4.1 分发层：这三个工具全都在 **Unity 主线程**上跑

`RimBridgeServer.BuiltInCapabilityModuleProvider.ResolveExecutionKind`
（memberId `46e27953d7324877a067e7458bf2c0d5:06000D38:M`）是一个 26 分支的 `switch (toolName)`，**默认分支是 `"_" => CapabilityExecutionKind.MainThread`**。

- `rimworld/execute_debug_action`、`rimworld/get_debug_action`、`rimworld/list_debug_action_roots` —— **都不在 switch 里** ⇒ `MainThread`
- `rimworld/pause_game` —— **也不在 switch 里** ⇒ `MainThread`
- `rimbridge/list_operations` —— **在** switch 里 ⇒ `Immediate`

配套的 `RequiresMainThread`（`06000D37:M`）：`Immediate → false`、`BackgroundObserved → false`、**其余 → true**。

### 4.2 执行层：排队 → `await Completion` → **没有任何超时**

`RimBridgeServer.RimBridgeMainThread.InvokeAsync<T>`（`06000E79:M`）：

```csharp
if (IsMainThread) { return func(); }
MainThreadWorkItem<T> workItem = new MainThreadWorkItem<T>(func);
lock (Sync) { Pending.Enqueue((IMainThreadWorkItem)workItem); }
// …只有当 cancellationToken.CanBeCanceled 时才注册 workItem.CancelIfPending()
return await workItem.Completion.ConfigureAwait(false);
```

`RimBridgeMainThread.Pump()`（`06000E76:M`）：

```csharp
while (true) {
    lock (Sync) { if (Pending.Count == 0) break; work = Pending.Dequeue(); }
    work.ExecuteIfPending();
}
```

`RimWorldWaits` / `LifecycleCapabilityModule` **完全不在这条链上**（那是 `[32] start_debug_game_ready` 的等待逻辑，与本条无关）。

### 4.3 断链处（`060000D9:M`）：GABP 服务端 `await` 工具结果，**没有 deadline**

`Lib.GAB.Server.GabpServer.HandleToolsCallAsync`
（memberId `02339f43725c4ef6a41454c77507e911:060000D9:M`）：

```csharp
object result = await _toolRegistry.CallToolAsync(name, parameters);
await SendResponseAsync(connection, request.Id, result);
```

**无 `CancellationTokenSource`、无 `Task.WhenAny`、无 `Task.Delay`** —— 与 `triage/32-…` §1.1(d) 的结论一致。

### 4.4 ⇒ 这 30/60s 花在哪：**花在 Node 侧一个纯挂钟的 `setTimeout` 上**

`MCP/gabpClient.js:566-569`（727 行，已复核）：

```javascript
const timer = setTimeout(() => {
  this._pending.delete(id);
  reject(this._err(GABP_ERROR_CODES.SERVER_ERROR, `GABP 请求 '${method}' 超时（${timeoutMs}ms）`));
}, timeoutMs);
```

**这 30/60 秒里，MCP 进程什么也没等 —— 它只是数着自己的墙钟到点，然后单方面放弃。** 游戏侧那份工作项仍躺在 `Pending` 队列里。

### 4.5 🚨 由此得到一个比板上更严重的结论：**超时根本不取消动作**

`gabpClient.js:551-579` 的 `_request` 只做 `_pending.delete(id)` + `reject(...)`，**没有任何"向游戏侧发取消"的报文**（GABP 协议里也没有这种消息 —— `Lib.GAB` 的方法集实测只有 7 个，`:1505` 日志 `methods=7`）。

而 `ExecuteDebugActionResponse`（`060010B9:M`）末尾是 `node.action?.Invoke()`，**同步调用，没有任何超时参数**（方法签名 `ExecuteDebugAction(string path, string pawnName, string pawnId, int? x, int? z, string thingId)` —— 与 `StartDebugGameReady` 的 `int timeoutMs = 120000` 形成鲜明对照）。

⇒ **时序是**：主线程恢复 → `Pump()` 把那个**早就被"放弃"了的**工作项取出来 → `node.action.Invoke()` **照样执行**。调用方在 60 秒前已经读到「没执行」，实际动作在**若干分钟后**才发生。

⇒ **板上 `:75-77` 的判断方向正确、措辞不准。** 不是「无法区分超时和正在执行」，而是「**超时 ≠ 取消：被判超时之后，动作仍会以不可观测的方式补执行**」。**这才是这条 🔴 的真正内核**，也正是它与 `[01]`（秒回假成功）的分界：`[01]` 是「报了成功却没做」，本条是「**报了失败却会晚点做**」—— 两者都是**结果与事实相反**，方向相反。

### 4.6 「主线程 tick 在超时期间正常推进」怎么解释（不矛盾）

`ue_game_tick` 推进说明 **Unity 主循环活着**（`Root_Play.Update` 在跑），但 `Pump()` 是由 RBS 自己挂进主循环的回调。两者不矛盾的三种可能（**按可能性排序，均未实机复现，不作为已证事实**）：

1. `Pump()` 的宿主回调（`RimBridgeMainThread.Initialize`，`06000E74:M`）依赖某个具体阶段，在当前游戏状态下没被调到；
2. `EnsureNodeGraph()` → `Dialog_Debug.TrySetupNodeGraph()`（见 §5）在**建图**而非**执行**阶段就把主线程拖住，而 tick 计数来自另一条更早的采样链；
3. `node.action.Invoke()` 内部自己起了一个 `LongEventHandler`/模态窗，主循环仍在跑但那个工作项还没轮到。

⇒ **无论哪一种，`gabpClient` 的那个 `setTimeout` 都是「纯墙钟、单方面放弃」，与游戏侧状态无关。** 这条结论不依赖上面三种解释的取舍。

### 4.7 ⇒ 属不属于 `[04]` 那类「超时歧义」？—— **一半一半，必须拆开说**

| 层 | 表现 | 是否 `[04]` 类 |
|---|---|---|
| **宿主客户端层** | AI 实际看到的是 `-32001: Request timed out`，**比服务端 60s 更早放弃**，什么信息都没有 | ✅ **是 `[04]` 类**。根因在宿主客户端截止线、**不在本仓库内**（`triage/04-…:98` 实测落在 (131.5s, 192.7s]；这次显然更短）。⇒ **不得并入计划 04**（那个计划的全部改动落在 `MCP/index.js` 的文案常量上，一行控制流不动；本条要动的是别的东西） |
| **本仓库客户端层** | 服务端侧产出的 `GABP 请求 'tools/call' 超时（60000ms）` **自曝真实预算** | ❌ **不是 `[04]` 类**，与 `triage/32-…` §3 的判据一致（信息没被抹掉）。⚠️ 但它被 `-32001` **挡在了调用方看不见的地方** |

---

## 5. 「全灭」的真实分界在哪：GABP 通道 × 游戏进程状态，**不是 DebugAction 族**

### 5.1 楔死窗口内 / 外的完整交错（`MCP/logs/mcp-20261001-222618.log`）

| 日志行 | 时间 | 通道 | 工具 | 结果 |
|---|---|---|---|---|
| `:1489` | `[22:34:57.565]` | GABP | `pause_game` | 95ms success |
| `:1494` | `[22:36:17.619]` | GABP | `execute_debug_action` | **60024ms** GABP_ERROR |
| `:1498` | `[22:38:27.721]` | GABP | `execute_debug_action` | **60010ms** GABP_ERROR |
| `:1500` | `[22:39:39.927]` | — | （连续 3 次超时 → `forceReconnect`） | WARN |
| `:1503` | `[22:39:39.931]` | GABP | `rimbridge.list_operations`（**Immediate**，非 DebugAction） | **30045ms** GABP_ERROR |
| `:1505` | `[22:39:39.940]` | — | GABP 握手成功 / 重连成功 | ok |
| `:1510` | `[22:42:38.264]` | — | `GABP 连接断开: connection lost` | WARN |
| `:1522` | `[22:51:33.028]` | 原生 | `start_quick_test` | 63068ms success |
| `:1529` | `[22:52:08.593]` | GABP | `pause_game` | **58ms success** ← 窗口开始前最后一通 GABP |
| `:1531` | `[22:53:16.985]` | GABP | `execute_debug_action` | **60027ms** GABP_ERROR |
| `:1533` | `[22:53:52.246]` | 原生 | `get_game_status` | 370ms ok |
| `:1535` | `[22:54:55.097]` | GABP | `get_debug_action` | 30031ms GABP_ERROR |
| `:1540` | `[22:55:25.147]` | GABP | `list_debug_action_roots` | 30034ms GABP_ERROR |
| `:1546` | `[22:55:45.769]` | **UE HTTP** | `execute_csharp_code` | **410ms success** ← 板上推荐的绕过方案，当时就能用 |

**读法（三条硬结论）**：

1. **死掉的不止 DebugAction。** `rimbridge.list_operations`（`:1503`）是 `Immediate` 分支的工具，跟 DebugAction 毫无关系，照样 30s 超时。⇒ 共同因子**不是**「调试动作」，是「**这次 GABP 调用**」。
2. **`pause_game` 在窗口内一次都没被调过。** 最近一次在窗口开始**之前**（`:1529`），下一次在窗口结束**之后**（`:1601`，`[23:18:49.897]`，24ms success）。⇒ 板上「同一时刻 `pause_game` 正常」这句话，**在日志里找不到对应记录**。
3. **非 GABP 通道在窗口内全部正常**（`start_quick_test` `:1522` / `get_game_status` `:1533` / `execute_csharp_code` `:1546-1548`）。这才是作者观察到的「只有调试动作族死」的全部来源 —— **但真正的分界是 GABP vs 非 GABP，被误读成了 DebugAction vs 非 DebugAction。**

### 5.2 唯一真正的 DebugAction 专属共同点（值得记，但不解释「全灭」）

反编译显示这三个工具共享一条 DebugAction 专属前置：

```
ListDebugActionRootsResponse (060010B5:M)  → EnsureNodeGraph() → …
GetDebugActionResponse     (060010B8:M)  → TryResolveNode()   → EnsureNodeGraph() → …
ExecuteDebugActionResponse(060010B9:M)  → TryResolveNode()   → EnsureNodeGraph() → …

EnsureNodeGraph (060010BD:M):  private static void EnsureNodeGraph() { Dialog_Debug.TrySetupNodeGraph(); }
```

vanilla `LudeonTK.Dialog_Debug.TrySetupNodeGraph()`（`61e4173561894da49d210260257b5097:060002CA:M`）：

```csharp
if (rootNode != null) return;
rootNode = new DebugActionNode("Root");
foreach (DebugTabMenuDef allDef in DefDatabase<DebugTabMenuDef>.AllDefs)
    roots.Add(allDef, DebugTabMenu.CreateMenu(allDef, null, rootNode).InitActions(rootNode));
```

⇒ 这是全量 `DebugAction` 反射建图（对照 `[03]`：`list_debug_action_children` 能返回 400KB+，说明图确实巨大）。**它是这三个工具唯一的专属前置，也是「首个超时都发生在 DebugAction 上」这一现象的最合理嫌疑**（第一次卡住的是 `execute_debug_action`，随后 `list_operations`、`select_pawn` 也跟着卡 —— 与「建图/执行把主线程拖住 ⇒ `Pump()` 停摆 ⇒ 全通道排队」的形状吻合）。

⚠️ **但必须明说证据边界**：①「主线程为何停摆」**没有实机复现**，上述三种可能见 §4.6；②`ListDebugActionRootsResponse` 里 `devModeEnabled = Prefs.DevMode` 只是**返回值里的一个字段**，`EnsureNodeGraph()` **完全不检查 dev mode** —— 所以板上 `:79-81` 建议的「先探 `Find.WindowStack` 里有没有 `Dialog_DebugSettings` / dev-mode 是否开」**对准了一个不存在的门禁**（同一份日志里 dev mode 显然一直是好的：`search_debug_actions` 查得到 GOD 动作、`execute_debug_action` 成功过 146 次）。

---

## 6. 必答：与计划 31 的先后关系会不会变？—— **不会，两者零交集**

**`execute_debug_action` 不是 HTTP 端点，也没有任何重放。** 三条依据：

| # | 依据 | 已复核位置 |
|---|---|---|
| 1 | 计划 31 的写端点全表 **15 条**，逐条列出为 `/area/delete`、`/area/clear`、`/unityexplorer/inspect/type`、`/unityexplorer/log/clear`、`/unityexplorer/hook/create`、`/unityexplorer/hook/toggle`、`DELETE /unityexplorer/hook`、`/unityexplorer/console/execute`、`/unityexplorer/console/reset`、`/unityexplorer/console/addusing`、`/unityexplorer/hotreload/apply`、`/unityexplorer/hotreload/watch`、`/mapmarker/clear`、`/mapmarker/recolor`、`/mapmarker/set` —— **零条 DebugAction 端点** | `plans/2026-10-01-31-csharp-silent-replay-guard.md:61-77` |
| 2 | `execute_debug_action` 走 **GABP `tools/call`**（RBS），不是 `callUnityExplorerAPI`。唯一与 DebugAction 有关的 UE HTTP 端点是 `/debugaction/search`（`MCP/index.js:4949`，**GET**）与 `/debugaction/categories`（`:4913`，**GET**）—— 计划 31 §0 的默认规则「GET 一律可重放」正好把它们划进**可重放**一侧，**行为不变** | `MCP/index.js:4913` / `:4949` |
| 3 | `gabpClient._request`（`MCP/gabpClient.js:551-579`）**只有一次 `_writeMessage`，没有任何重试/重放逻辑** —— 本条在 GABP 侧**本来就不存在静默重放风险** | `MCP/gabpClient.js:572` |

⇒ **计划 31 既不会让 `[36]` 从「超时」变成「快速失败」，也不会加剧它。** 两条计划的先后关系**不变**，**不存在互相踩**。

⇒ 顺带说明：本条与 `plans/2026-10-01-04-startup-call-timeout-disambiguation.md` 也**不得**并单 —— 计划 04 的全部改动落在 `MCP/index.js` 的**文案常量**上（`:1827` / `:1836-1844` / `:1942` / `:1946-1950` / `:2930`，见 `triage/32-…` §2），一行控制流都不动；本条要动的是「楔死时该返回什么、该不该重连」。

---

## 7. 真正值得修的是什么（板上 `:79-82` 的两条建议都不成立）

| 板上建议 | 裁决 | 依据 |
|---|---|---|
| 「进入调试菜单路径前先探 `Find.WindowStack` 有没有 `Dialog_DebugSettings` / dev-mode 是否开，不可用就立刻返回明确错误」 | ❌ **对准了不存在的门禁** | `EnsureNodeGraph()`（`060010BD:M`）无条件调 `Dialog_Debug.TrySetupNodeGraph()`，**不查任何前置**；`ListDebugActionRootsResponse`（`060010B5:M`）只是把 `Prefs.DevMode` 当**返回值字段**读。实测日志里 dev mode 一直正常（146 次成功 + `search_debug_actions` 查得到 GOD 动作） |
| 「与 [01]/[25] 合并处理：共同点是超时/空返回被当成执行结果」 | ⚠️ **方向对、比合更严重，但不宜合并** | 共同点确实存在（都是「返回值与事实相反」），但 `[01]` 是「**报成功却没做**」、本条是「**报失败却会晚点做**」—— 修法完全不同（`[01]` 要补执行证据；本条要治**取消语义 + 楔死处置**）。板上 `:82` 说的「空返回」在 `[25]` 那里，与本条无关 |
| —（板上未提，但本条真正的内核） | ✅ **值得修** | **超时 ≠ 取消**：`gabpClient.js:566-569` 单方面放弃，工作项仍在 `RimBridgeMainThread.Pending` 里，主线程一恢复就补执行（§4.5）。**这会让「重试」变成「执行两次」** |

### 7.1 建议的最小改动（锚点，全部已复核）

| # | 落点 | 动作 | 为什么 |
|---|---|---|---|
| ① | `MCP/index.js:2775-2785`（`callGABPTool` 的假死计数分支，紧邻 `:2776` 的 `gabpConsecutiveTimeout++`） | 连续超阈值后**在下一次调用前**返回结构化错误（复用 `contentErrorResponse`，`:2705`），文案点名「游戏进程疑似主线程无响应；**重连无效，请重启/重载游戏**」 | 现在的 `forceReconnect()`（`:2781`）**在实测里帮不上忙**：`:1500` 强制重连后 `:1503` 仍然 30045ms 超时。卡的是游戏进程不是 socket，重连 TCP 是错药 |
| ② | `MCP/index.js:4292` 邻近 | 仿照已有的 `AREA_OP_CHANNEL_BUSY`（`:4321-4324`）给**其它**易楔死工具加一条同类提示 | 仓库里已经有一个正面先例（`delete_zone` 的「连接可能繁忙、勿重复并发」），本条照抄模式即可，不发明新机制 |
| ③ | `MCP/gabpClient.js:566-569` **注释**（不改行为） | 写明「此处**只**放弃本地等待，**不取消游戏侧**；被放弃的 `tools/call` 仍会在游戏侧执行」 | 防止下一个人把它当成「安全超时」。**这是本条最重要的一行改动**，成本近零 |
| ④ | `MCP/test/`（新建） | 断言「连续 N 次 GABP 超时后，第 N+1 次**不发起** `tools/call`，直接返回带指引的结构化错误」 | 用现成的 `_testHooks.setGabpBridge`（`:5453`）+ `callGABPTool` 导出（`:5422`）即可离线驱动，不起游戏 |

**明确不建议**：
- ❌ 把 `get_debug_action` / `list_debug_action_roots` / `list_operations` 加进 `timeoutPolicy.js` 白名单 —— §3.3 的 A 已证明翻倍预算无效，只会把 30 秒的失败换成 120 秒的失败，**并让 `forceReconnect` 的自愈阈值更难触发**（`MCP/index.js:2775-2785`）。
- ❌ 改 `MCP/config.json` 的 `rimBridge.requestTimeoutMs` —— 同上，纯加时长。
- ❌ 改 RBS 一个字节 —— 楔死的成因在主线程调度，属第三方；本条要做的是**把「超时」变成「可读、可行动、不误导」**，这恰好是本仓库该负的责任。

---

## 8. 归并锚点 / 单立理由

| 问题 | 结论 |
|---|---|
| 是否已被现有计划覆盖？ | **否。** `plans/` 现有 17 份。逐份核对沾边的三份：`2026-10-02-32-gabp-per-call-timeout-derivation.md`（§1.7 明确只含 `[24]`+`[32]`，§4.5 决策表把「未登记工具回落 30s」列为**回归护栏**）、`2026-10-01-04-startup-call-timeout-disambiguation.md`（§0.1/§0.1.1 明文把 `-32001` 与其它超时拆开）、`2026-10-01-31-csharp-silent-replay-guard.md`（§0.2 写端点表零 DebugAction 条目）。`triage/` 下无 `23-*` / `24-*` / `36-*` 报告 |
| 是否并入计划 32？ | **否**（§3.5） |
| 是否并入计划 04？ | **否**（§6） |
| 是否并入计划 31？ | **否**，两者零交集（§6） |
| **建议** | **单立一份计划**，标题形如 `2026-10-03-36-gabp-wedge-distinguishable-failure.md`，范围限定为 §7.1 的 ①②③④。**计划正文必须先复述 §1 的实测数字（146 成功 / 5 失败）与 §4.5 的取消语义结论**，否则下一个执行者会重蹈板上「一族全灭」的误读 |
| 严重度 | **🟠 维持（板上 🔴 建议下调）**。理由：①`gabpClient.js:568` 的错误串**自曝真实预算**，不像 `[04]` 的 `-32001` 什么都不给；②真��后果是「延迟补执行」，会与重试叠加成**重复执行**，但触发前提是调用方在超时后重试 —— 而板上 `:86-104` 的替代方案（UnityExplorer REPL + 反射）已完全绕开本条，实测当时就能用（`:1546` 410ms success）。**⇒ 是真问题、可修、有低成本修法，但不属于「会让自动化得出事实相反结论」的最高档**（那是 `[01]`/`[31]`） |
| 板上应补的批注 | ①`:59` 的 `-32001` 与仓库产出的 `GABP_ERROR` 是**两回事**，前者是宿主客户端截止线；②「一律 30s」对 `execute_debug_action` **错**，它是 60s（`timeoutPolicy.js:18`）；③`:73-74` 的「整个族全灭」改为「约 20 分钟窗口内 GABP 通道无响应」，并补 `get_debug_action` 16ms 成功（日志 `:862`）与 `execute_debug_action` 146 次成功的反证；④`:71` 的「同一时刻 `pause_game` 正常」在日志里**无对应记录**，且 `pause_game` 是 GABP 镜像、不构成对照；⑤`:79-81` 的 dev-mode 探测建议**对准了不存在的门禁**，建议撤回；⑥`:82` 的「与 [01]/[25] 合并」改为「与 [01] 同属『返回值与事实相反』，但方向相反、修法不同，不合并」；⑦`:75-77` 的「超时无法与正在执行区分」升级为「**超时 ≠ 取消，被放弃的动作仍会补执行**」 |

---

## 9. 一句话给主代理

`[36]` **现象为真、归因三处皆错**：`execute_debug_action` 早在 `timeoutPolicy.js:18` 就登记在慢档（**实测 60024/60010/60027/60041ms，不是 30s**），另三个工具 30s 也只是回落默认值而非致因（`get_debug_action` 在日志 `:862` **16ms 成功**、`execute_debug_action` 全日志 **146 成功 / 5 失败**）；真正的分界是 **GABP 通道 vs 非 GABP**（同窗口里不相干的 `rimbridge.list_operations` `:1503` 与 `rimworld.select_pawn` `:198` 同样超时，而 `pause_game` 在窗口内**根本没被调用过**）；根因是 **RimBridgeServer 侧 Unity 主线程楔死**（`RimBridgeMainThread.InvokeAsync` 无超时地 `await workItem.Completion`，`Pump()` 停摆即全通道排队），而**超时根本不取消动作** —— 被放弃的 `tools/call` 仍留在 `Pending` 队列里，主线程一恢复就补执行，这才是本条 🔴 的真内核。**不并入计划 32/04/31（与三者均零交集），建议单立**，主线是「楔死时返回可读错误 + 明写『重连无效、请重启游戏』+ 在 `gabpClient.js:566` 注明超时≠取消」。
裁决：值得修

# [32] `rimworld.start_debug_game_ready` 的 `timeoutMs` 不生效（被 GABP 通道 30s 请求预算砍掉）🟠

**一句话结论**：吐槽板的现象**逐字属实**，但归因写错了 —— 砍掉 `timeoutMs:300000` 的**不是**「GABP 内建 30s 超时（第三方 RBS）」，而是**本仓库自己的 `MCP/gabpClient.js` 客户端请求预算**；`timeoutMs` 之所以无效，是因为 `MCP/longTask/timeoutPolicy.js` 的三张白名单**全都没登记 `start_debug_game_ready`**，于是分发处算出 `_opts = null`，最终落回硬编码的 `30000`。RBS 侧已用 decompilerserver 逐行反编译核实：**它完整尊重 `timeoutMs`（默认 120000，无上限），GABP 服务端也完全没有 per-request deadline**。⇒ **这不是"改不了第三方"，而是"我们自己漏了一行表项"**。

---

## 0. 坐标与证据纪律声明

| 项 | 值 |
|---|---|
| 吐槽板正文 | `docs/mcp-吐槽板.md:275-293`（`## [32]` 标题在 `:275`，下一条 `## [31]` 在 `:296`） |
| 吐槽板索引行 | `docs/mcp-吐槽板.md:49` |
| 本报告全部行号 | 均用 `[System.IO.File]::ReadAllLines(...)` 复核（`MCP/index.js` 5460 行 / `MCP/gabpClient.js` 727 行 / `MCP/longTask/timeoutPolicy.js` 154 行） |
| RBS 证据形式 | decompilerserver 反编译（无 .cs 源码），以 **memberId** 引用；四个运行时程序集全部实载 |
| 反编译程序集 | `RimBridgeServer.dll`（MVID `46e27953d7324877a067e7458bf2c0d5`，689 类型 / 3188 方法）、`RimBridgeServer.Core.dll`（`5f081ec2db344499a3cc73ddc96ec4ef`）、`Gabp.Runtime.dll`（`00259f0eb6cc4baaaaeb1628de0b6a81`）、`Lib.GAB.dll`（`02339f43725c4ef6a41454c77507e911`），均在 `C:\SteamLibrary\steamapps\workshop\content\294100\3727949765\1.6\Assemblies\` |
| ⚠️ 未采信 | `GABS-main\`（Go 参考实现）—— 非运行时路径，全程未作为 RBS 行为证据 |

---

## 1. 责任层判定：**本仓库（MCP 侧）**，不是第三方 RBS

这是本条最关键、也最容易判错的结论。分两半查证。

### 1.1 RBS 侧：`timeoutMs` 被**完整尊重**，且**没有**任何 30s 上限

**(a) 工具注册与参数声明** —— `RimBridgeServer.RimBridgeTools.StartDebugGameReady`
（memberId `46e27953d7324877a067e7458bf2c0d5:06000F09:M`）：

```csharp
[Tool("rimworld/start_debug_game_ready", Description = "Start RimWorld's built-in quick test colony ...")]
public object StartDebugGameReady(
    [ToolParameter(Description = "Maximum time to wait in milliseconds")] int timeoutMs = 120000,
    [ToolParameter(Description = "Polling interval in milliseconds")] int pollIntervalMs = 50,
    [ToolParameter(Description = "Readiness target: gameData, mapData, currentMap, playable, or visual")] string readiness = "mapData",
    ... )
    => InvokeAlias(Arguments(("timeoutMs", timeoutMs), ...), "StartDebugGameReady");
```

⇒ 默认 **120000**，描述明写 "Maximum time to wait in milliseconds"，**没有 `Math.Min`、没有 clamp**。

**(b) 实现体** —— `RimBridgeServer.LifecycleCapabilityModule.StartDebugGameReady`
（memberId `46e27953d7324877a067e7458bf2c0d5:06000D9A:M`）：

```csharp
public object StartDebugGameReady(int timeoutMs = 120000, int pollIntervalMs = 50, string readiness = "mapData", ...)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    Dictionary<string, object> dictionary = RimWorldWaits.WaitForEntrySceneReadyResult(timeoutMs, pollIntervalMs);
    ...
    Dictionary<string, object> dictionary3 = RimWorldWaits.WaitForGameLoadedResult(
        (timeoutMs > 0) ? Math.Max(0, timeoutMs - (int)stopwatch.ElapsedMilliseconds) : 0, pollIntervalMs, readiness, pauseIfNeeded);
```

⇒ `timeoutMs` 被**两段消费**（先等 entry scene，再等 game loaded，且第二段扣掉已耗时）。传 300000 ⇒ RBS 会老老实实等最多 300 秒。

**(c) 归一化函数没有上限** —— `LifecycleCapabilityModule.NormalizeTimeoutMs`
（`46e27953d7324877a067e7458bf2c0d5:06000DB6:M`）：

```csharp
private static int NormalizeTimeoutMs(int timeoutMs) => Math.Max(0, timeoutMs);
```

`ResolveRemainingTimeoutMs`（`06000DB5:M`）只做减法 + 下限 1ms；`ResolveProbeDispatchTimeoutMs`（`06000DB3:M`）/ `RimWorldWaits.ResolveProbeTimeout`（`06001295:M`）里的 `Math.Min(timeoutMs, 10000)` **只夹单次轮询探针的时长，不夹整体等待**。`RimWorldWaits.WaitForEntrySceneReadyResult`（`06001291:M`）的 `timeoutMs = 30000` 是**该 helper 自己的默认形参**，被 `StartDebugGameReady` 显式传值覆盖，**不是**全局上限。

**(d) GABP 服务端没有 per-request deadline** —— `Lib.GAB.Server.GabpServer.HandleToolsCallAsync`
（memberId `02339f43725c4ef6a41454c77507e911:060000D9:M`）全文关键三行：

```csharp
object result = await _toolRegistry.CallToolAsync(name, parameters);
await SendResponseAsync(connection, request.Id, result);
```

**没有 `CancellationTokenSource`、没有 `Task.WhenAny`、没有 `Task.Delay(30000)`、没有任何 deadline** —— 服务端会一直等到工具自己跑完再回包。`HandleRequestAsync`（`060000CF:M`）的 `switch` 只是把 `tools/call` 转发过去，同样无超时。
`Lib.GAB.Server.GabpServerConfig`（`02339f...:02000018:T`）共 11 个成员（`Port` / `Token` / `AgentId` / `AppInfo` / `EnableAttentionSupport` 及其 backing field），**没有任何超时字段**。

**(e) 错误串归属（决定性）**：把板上那条报错 `GABP 请求 'tools/call' 超时（30000ms）` 拿去在 RBS 侧做**字面量**检索 ——

| 检索（`search_string_literals`，字面量，无 `|`） | 程序集 | 命中 |
|---|---|---|
| `GABP 请求` | `RimBridgeServer.dll`（689 类型 / 3188 方法） | **0** |
| `GABP 请求` | `RimBridgeServer.Core.dll`（133 类型 / 598 方法） | **0** |
| `GABP 请求` | `Lib.GAB.dll`（94 类型 / 294 方法） | **0** |
| `tools/call`（**阳性对照**，证明索引非空、工具可用） | `Gabp.Runtime.dll` | **1**（`Gabp.Runtime.ToolsCallRequest`） |

⇒ **这条错误串是本仓库自己造的**，逐字对应 `MCP/gabpClient.js:568`。RBS 从未产出过它。

### 1.2 本仓库侧：`timeoutMs` 被**静默忽略**的完整调用链

以 `agg_call_tool{tool:"rimworld.start_debug_game_ready", args:{timeoutMs:300000, ...}}` 为例，逐跳：

| # | 位置 | 代码 | 结果 |
|---|---|---|---|
| 1 | `MCP/index.js:4291` | `if (name === 'rimworld.start_debug_game_ready') {` | 冷启动（未加载）时不短路 |
| 2 | `MCP/index.js:4299` | `log('INFO', \`start_debug_game_ready 未短路（…），继续转发 RimBridge\`)` | 落到通用镜像分支 |
| 3 | `MCP/index.js:4332` | `const _rbs = name.replace(/\./g, '/')` | `rimworld/start_debug_game_ready` |
| 4 | `MCP/index.js:4333` | `const _derived = timeoutOptsFor(_rbs, args, config)` | **`null`**（见 1.3） |
| 5 | `MCP/index.js:4334` | `const _slow = /^(rimworld\|rimbridge)\.(load_game\|save_game\|execute_debug_action)$/.test(name)` | **`false`** |
| 6 | `MCP/index.js:4335` | `const _opts = _derived \|\| (_slow ? { slow: true } : null)` | **`null`** |
| 7 | `MCP/index.js:4350` | `await callGABPTool(_rbs, args, undefined, _opts)` | — |
| 8 | `MCP/index.js:2756` | `const callOpts = opts \|\| {}` | `{}` |
| 9 | `MCP/index.js:2757` | `await gabpBridge.callTool(rbsName, args \|\| {}, callOpts)` | 传 `{}` |
| 10 | `MCP/gabpClient.js:667` | `async callTool(name, args, opts = {})` | `opts = {}` |
| 11 | `MCP/gabpClient.js:674` | `await this._request('tools/call', { name, parameters }, opts)` | `opts = {}` |
| 12 | `MCP/gabpClient.js:563-565` | `Number.isFinite(opts.timeoutMs) && opts.timeoutMs > 0 ? opts.timeoutMs : (opts.slow ? this._requestTimeoutMs * 2 : this._requestTimeoutMs)` | 前两支全不成立 ⇒ **`this._requestTimeoutMs`** |
| 13 | `MCP/gabpClient.js:107-109` | `const rb = this._config.rimBridge \|\| {}; const timeout = Number(rb.requestTimeoutMs); this._requestTimeoutMs = Number.isFinite(timeout) && timeout > 0 ? timeout : DEFAULT_REQUEST_TIMEOUT_MS;` | `MCP/config.json:1` **只有 5 个键**（`gamePath` / `logPath` / `steamPath` / `appId` / `startTimeout`），**无 `rimBridge` 无 `requestTimeoutMs`** ⇒ 回落 `MCP/gabpClient.js:67` 的 `DEFAULT_REQUEST_TIMEOUT_MS = 30000` |
| 14 | `MCP/gabpClient.js:566-569` | `setTimeout(() => { … reject(this._err(…, \`GABP 请求 '${method}' 超时（${timeoutMs}ms）\`)) }, timeoutMs)` | 30000ms 后 reject，**逐字产出板上那条错误串** |
| 15 | `MCP/index.js:2789` | `return gabpErrorResponse(\`GABP 调用 ${rbsName} 失败（code=${err.code}）: ${errMsg}\`)` | 板上完整报错 |

**关键**：`args`（含 `timeoutMs:300000`）在第 11 步被原样塞进 `{ name, parameters }` **转发给了 RBS**（RBS 确实收到了 300000 并会等 300 秒），**但它从未参与本地等待预算的计算**。参数在**传输层有效、在控制层无效** —— 这正是「看起来可用、实际是摆设」的机制。

### 1.3 为什么第 4 步返回 `null`（三张表全都没登记它）

`MCP/longTask/timeoutPolicy.js:136-151` 的 `timeoutOptsFor` 按 T4 → T3 → T2 顺序判定：

| 档 | 代码位置 | 内容 | 对 `start_debug_game_ready` |
|---|---|---|---|
| T4 显式覆盖 | `timeoutPolicy.js:106-109` | `t.toolTimeouts[bare]`，来自 `config.rimBridge.longTask.toolTimeouts` | **null** —— `MCP/config.json:1` 根本没有 `rimBridge`（`timeoutPolicy.js:65-66` 容错成 `{}`，`:82-84` 容错成 `{}`） |
| T3 入参推导 | `timeoutPolicy.js:21-25` | `INPUT_DERIVED = new Map([['play_for',…],['step_game_ticks',{argKey:'timeoutMs',…}],['play_until_letter',{argKey:'timeoutMs',…}]])` | **null** —— `timeoutPolicy.js:90-91`：`INPUT_DERIVED.get('start_debug_game_ready')` 未命中即 `return null` |
| T2 慢档 | `timeoutPolicy.js:18` | `SLOW_BARE_NAMES = new Set(['load_game','save_game','execute_debug_action','create_hook'])` | **不命中** |
| T2 长默认 | `timeoutPolicy.js:28` | `LONG_DEFAULTS_MS = new Map([['step_game_ticks',120000],['play_until_letter',300000]])` | **不命中** |
| ⇒ `timeoutPolicy.js:150` | `return null` | | **⇒ 第 4 步 `_derived = null`** |

（`timeoutPolicy.js:15` 的 `FALLBACK_DEFAULT_MS = 30000` 与 `gabpClient.js:67` 同值、互为镜像，二者始终一致。）

### 1.4 决定性旁证：**本仓库早就知道这个 30s，并且只修了一半**

`MCP/index.js:4287-4288` 的注释原文：

```javascript
    // 修复 2（fix-debug-server-defects）：start_debug_game_ready 已加载幂等短路——
    // 殖民地已加载（playable=true）时直接返回 ready，不转发 RimBridge（避免 30s 超时）。
```

⇒ 作者**明确知道** `start_debug_game_ready` 会被 30s 砍掉，写的对策是「**已加载就短路**」（`MCP/index.js:4291-4298`）。但短路只在 `playable/mapCount>0` 时成立；**从主菜单冷启动造新殖民地**（板上 `:284` 的复现条件）必然不短路，走到 `:4299` 继续转发 ⇒ **原样撞上 30s**。

这不是"第三方不可控"，这是**一个已开工、只覆盖了 easy case 的自家 workaround**。责任归属毫无争议。

（顺带记录同源的次生点：`MCP/index.js:4292` 的幂等探测 `gabpBridge.callTool('rimworld/get_game_info', {})` **也没传 opts**，同样吃 30s 默认预算。GABP 假死时这次探测会先烧掉 30 秒。）

### 1.5 裁决措辞：为什么这不是「查不到」，而是**确定可改**

任务书要求在"不能在本仓库修"时写清「为什么这不是查不到而是确定不可改」。**本条的结论恰好相反**，因此反向说明：

- **不是「无法核实」**：RBS 四个程序集全部实载反编译成功，`start_debug_game_ready` 的注册、实现、归一化、GABP 服务端分发逐层读通，结论是**肯定性证据**，不是缺证。
- **不是「确定不可改」**：RBS 侧是**守规矩的一方**（`06000F09:M` 声明 `timeoutMs`、"Maximum time to wait in milliseconds"、默认 120000；`06000D9A:M` 完整消费；`060000D9:M` 无 deadline）。**不改 RBS 一个字节**，只要在本仓库 `MCP/longTask/timeoutPolicy.js` 补一条登记，`timeoutMs` 立刻穿透。
- ⇒ 裁决 `值得修`，且修复面是**一个纯数据表条目**，不涉及任何控制流。

---

## 2. 与 [04] / [24] 是否同源：**与 [24] 同源；与 [04] 不同源**（板上归因需要修正）

板上 `:277` / `:286` 写的是「与 [04]/[24] 同源」。逐条核：

| 条目 | 通道 | 报错形态 | 超时预算由谁定 | 根因层 |
|---|---|---|---|---|
| **[04]** / **[30]** `start_game` / `start_quick_test` | **原生 MCP 工具**（`MCP/index.js:4356-4369` / `:4413-4414`），**不经过 GABP** | `MCP error -32001: Request timed out` | **MCP SDK 客户端请求截止线**（`@modelcontextprotocol/sdk/dist/esm/shared/protocol.js:712-713`），实测落在 (131.5s, 192.7s]（`triage/04-startup-call-timeout-ambiguity.md:98`） | 宿主客户端 / IDE 配置（**不在本仓库内**） |
| **[24]** `rimworld.select_pawn` | GABP 镜像 | `GABP 请求 'tools/call' 超时（30000ms)` | **本仓库 `gabpClient.js` 的 30s 请求预算** | 本仓库（`timeoutPolicy.js` 三张表未登记 `select_pawn`） |
| **[32]** `rimworld.start_debug_game_ready` | GABP 镜像 | `GABP 请求 'tools/call' 超时（30000ms）` | **同上，同一个 30000 常量** | 本仓库（`timeoutPolicy.js:21-25/28` 未登记） |

**证据（逐字级）**：[24] 索引行（`docs/mcp-吐槽板.md:41`）的错误串与 [32] 正文（`docs/mcp-吐槽板.md:280`）的错误串**只差一个右括号**，且 30000 这个数值**只有一个来源** —— `MCP/gabpClient.js:67` 的 `DEFAULT_REQUEST_TIMEOUT_MS`（经 `:109` 回落，因 `MCP/config.json:1` 无 `rimBridge`）。两者是**同一个 setTimeout 的同一次 reject**，只是落在不同工具上。

⇒ **判定：**
- **与 [24]：是，同一条（100% 同源）。** 同一个 30s 客户端请求预算、同一张漏登记的白名单、同一类修法。
- **与 [04] / [30]：否，不同源。** [04] 走的是 MCP 客户端截止线（`-32001`），量级 131–193s，**报错码与报错串都不一样**，与 `gabpClient.js` 无关；`plans/2026-10-01-04-startup-call-timeout-disambiguation.md:30` 与 `:34-42`（§0.1 / §0.1.1）已经把这两类「超时」明确拆开，并明写「**`-32001` 是客户端截止线，不是服务端业务错误**」。
  **⇒ 板上 `[32]:277` 与 `:286` 的「与 [04] 同源」表述应予修正为「与 [24] 同源」。**

**⇒ 归并判定：**
- **不得并入计划 04。** 根因层不同（一个是宿主客户端截止线、不可在本仓库修；一个是本仓库漏一行表项、可以修）、报错形态不同、修复文件不同（计划 04 全部落在 `MCP/index.js:1827/1836-1844/1942/1946-1950/2930` 的**文案常量**上，一行控制流都不动；本条要动 `MCP/longTask/timeoutPolicy.js` 的**数据表**）。硬并进去会让计划 04 的核心结论（"本条不是功能缺失，只是文案"）被污染。
- **应与 [24] 合并成一份新计划**（见 §5）。

---

## 3. 严重度复评：**维持 🟠，不上调 🔴**（板上的 🟠 判对）

| 判据 | 结论 |
|---|---|
| 会不会得出**事实相反**的结论？ | **不会（因此不上调 🔴）。** 错误串**自曝真实预算**：`超时（30000ms）`（`MCP/gabpClient.js:568`）。调用方能从返回值直接看出"我其实只等了 30 秒"，**信息没被抹掉**。这与 [01] / [31] 的"工具报成功但事情没做"（`triage/01-*` / `triage/31-*`）不同类，也与 [04] 的 `-32001`（**什么都不给**，只能靠 `get_game_status` 反查）不同类。 |
| 但是不是"每次都要重踩"？ | **是。** 冷启动造殖民地是所有调试会话的必经步骤，**每次必踩**，且踩法具有欺骗性：调用方传了 300000 会形成"我给了 5 分钟预算"的错误心智模型。 |
| 后果严重吗？ | **有真实副作用，但方向是"多干活"不是"少干活"** —— 板上 `:282` 实测游戏**照样造好了新殖民地**（`Map-0-PlayerHome` / 41 pawn）。本条**不产生数据损坏、不产生错误状态**，只产生「结果不可见 + 调用方误以为失败 ⇒ 可能重复触发」。 |
| 修复成本 | **极低**（一条表项 + 断言，见 §4），收益/成本比在整块板子里排前列。 |

**⇒ 🟠 维持。** 与板上标注一致，无需调整。
**唯一要提醒的边界**：若将来有人把它当"参数摆设"泛化成"schema 不可信"，那才是 🔴 的形态（会让 agent 对全部镜像工具的入参失去信任）。当前证据不支持那一层。

---

## 4. 修的话怎么修（**必读 §4.1 的陷阱 —— 修法选错会从 30s 变成 -32001**）

### 4.1 ⚠️ 承重约束：不能简单"把 300000 放行"

`triage/04-startup-call-timeout-ambiguity.md:98` 用 7 次超时 / 8 次成功的实测数据定死了宿主客户端截止线落在 **(131.5s, 192.7s]**；`plans/2026-10-01-04-startup-call-timeout-disambiguation.md:25-28` 进一步写明该值**不在本仓库内、不可写死**。

⇒ 若在 `INPUT_DERIVED` 里登记 `['start_debug_game_ready', { argKey: 'timeoutMs', overheadMs: 5000 }]`，`timeoutPolicy.js:95` 会算出 `Math.max(30000, 300000 + 5000) = 305000` —— **远超客户端截止线上界**。结果是**失败点从"30s 超时"平移到"-32001"**，痛点没消除，只是换了个错误码，并顺带把 [04] 的坑又踩一遍。

⇒ **推导预算必须夹在客户端截止线的下界（131.5s）以下。** 唯一有证据支撑的安全值是 **120000**（`< 131.5s`，且恰好就是 RBS 自己的默认值 `06000F09:M`）。

### 4.2 建议的最小改动

| # | 文件:行号 | 动作 | 理由 |
|---|---|---|---|
| 1 | `MCP/longTask/timeoutPolicy.js:28` | `LONG_DEFAULTS_MS` 增 `['start_debug_game_ready', 120000]` | 覆盖**不传 `timeoutMs`** 的冷启动默认情形。走 `timeoutPolicy.js:147-148` ⇒ `_opts = {timeoutMs: 120000, inputDerived: false}`，`MCP/index.js:4335` 直接生效，**零控制流改动** |
| 2 | `MCP/longTask/timeoutPolicy.js:21-25` | `INPUT_DERIVED` 增 `['start_debug_game_ready', { argKey: 'timeoutMs', overheadMs: 5000 }]` | 覆盖**传了 `timeoutMs`** 的情形。**必须同时**在 `timeoutPolicy.js:95` 的 `Math.max(t.defaultMs, raw + overhead)` 外加一道 `Math.min(..., 120000)` 上限（新增 clamp 只作用于本条推导，不影响 `play_for` / `step_game_ticks` / `play_until_letter`） |
| 3 | `MCP/index.js:4292` | 幂等探测 `callTool('rimworld/get_game_info', {})` 补一个显式短预算（如 `{ timeoutMs: 5000 }`） | 该探测是「短路判定」，**失败就保守转发**（`MCP/index.js:4290`），不需要 30s；现在却会吃掉 30s |
| 4 | `MCP/test/timeoutPolicy.test.js` | 补断言 | 钉死"`start_debug_game_ready` 的本地等待预算 ∈ (30000, 120000]"、"推导值被 clamp 在 120000 以内"，防回归 |

**⚠️ `inputDerived` 的连带效应（必须让 PLAN 阶段处理）**：
`MCP/index.js:2772-2785` 里，`callOpts.inputDerived === true` 的超时会**被排除出"GABP 假死 → 连续 3 次强制重连"计数**（这是 `triage/07-gabp-idle-no-fail-fast.md:113` 记录的那套机制）。改动 ② 会让推导路径带上 `inputDerived: true`（`timeoutPolicy.js:145`：`derived !== baselineMs` 成立），**这是对的**（长等待超时是预期内，不该触发强制重连）；但改动 ① 走的是 `LONG_DEFAULTS_MS` 分支，固定 `inputDerived: false`（`timeoutPolicy.js:148`）⇒ **一次合法的 120s 超时会被计进假死计数**。连续 3 次就会打断正在跑的启动流程（`MCP/index.js:2777-2782` 调 `forceReconnect()`）。**这是改动 ① 的隐藏代价，PLAN 阶段要么给 `LONG_DEFAULTS_MS` 分支也加豁免，要么在报告里显式记录该权衡。**

### 4.3 明确不建议

- **不建议**把 `timeoutPolicy.js:95` 的 clamp 做成全局常量并套用到 `play_for`。`play_for` 有专门的上界检查（`MCP/index.js:4146-4150`，超 `maxSingleCallTimeoutMs` 时返回结构化错误并给出两条出路），`start_debug_game_ready` 没有等价机制，照搬会把长任务能力一起削掉。
- **不建议**照 [04]/[30] 的路子去改默认值或改文案了事。板上 `:290` 的第二个建议（"在 schema 里标成仅游戏侧生效"）是**错的** —— §1.1 已证明它**本来就该在两侧都生效**，RBS 一直在等，是本仓库没让它等。**标成"无效参数"等于把一个自家 bug 固化成契约。**

---

## 5. 归并锚点

| 问题 | 结论 |
|---|---|
| 是否已被现有计划覆盖？ | **否。** `plans/` 目录现有 14 份计划，逐份核对：唯一沾边的是 `2026-10-01-04-startup-call-timeout-disambiguation.md`，但它 §0.1（`:19-30`）/ §0.1.1（`:32-42`）**明文把 `-32001` 与其它超时拆开**，全文（`§0.7` 行号索引 `:133-158`）**不含 `gabpClient.js`、不含 `timeoutPolicy.js`、不含任何 GABP 镜像工具**。`2026-10-01-07-gabp-offline-help-fallback.md` 处理的是 GABP **离线**时的 `mcp_help` 兜底，与超时无关。 |
| 是否并入计划 04？ | **否**（理由见 §2：根因层、报错形态、修复文件三者全不同）。 |
| 建议的归并锚点 | **建议单立一份计划，但计划范围必须同时覆盖 [24] 与 [32]**，标题形如 `2026-10-02-32-gabp-per-call-timeout-derivation.md`，正文明写「同批覆盖吐槽板 [24] `rimworld.select_pawn` 与 [32] `rimworld.start_debug_game_ready`」—— 两者的根因是**同一张漏登记的白名单**（`timeoutPolicy.js:18/21-25/28`），分头修必然互相踩（改 T3 就同时影响两者的推导档）。 |
| ⚠️ 派单前必查 | **[24] 目前只有索引行、没有正文**（`docs/mcp-吐槽板.md:41`）。核实范围：`[System.IO.File]::ReadAllText('docs/mcp-吐槽板.md')` + 正则 `(?m)^## \[(23|24)\]` → **0 命中**；正文章节实际只有 `[35][34][33][32][31][30][29][28][22][21][20][19][18][17][16][15][14][13][12][11][26][01]…[10]`。`triage/` 目录下亦**无 `23-*` / `24-*` 报告**（31 份报告，实测列举）。⇒ **派 PLAN 前需确认是否已有并行会话在 triage [24]**，避免两份计划抢同一张表。 |
| 板上应补的批注 | ①把 `:277` / `:286` 的「与 [04]/[24] 同源」改为「**与 [24] 同源，与 [04] 不同源**」；②把索引行 `:49` 的「GABP 内建 30s 超时」改为「**本仓库 `MCP/gabpClient.js:67` 的 30s 请求预算**（RBS 侧已核实无 30s 上限，`timeoutMs` 在 RBS 侧是有效的）」；③状态 `已绕过` 保留（板上 `:292` 的 `timeoutMs` 照传 + `get_game_status` 轮询确实是可行绕过），但补一句「该绕过有效**只因为游戏侧会继续跑完**，不代表 RPC 能等到结果」。 |

---

## 6. 一句话给主代理

`[32]` **属实但归因写反了**：砍掉 `timeoutMs:300000` 的是**本仓库** `MCP/gabpClient.js:67` 的 30s 客户端请求预算，`timeoutMs` 失效的原因是 `MCP/longTask/timeoutPolicy.js:18/21-25/28` 三张白名单**全都没登记 `start_debug_game_ready`**，分发处 `MCP/index.js:4335` 算出 `_opts = null`；RBS 侧已用 decompilerserver 逐层反编译**证否**了"第三方 30s 上限"（`06000F09:M` 完整尊重 `timeoutMs`、`060000D9:M` 无 deadline、错误串 `GABP 请求` 在四个 RBS 程序集里 0 命中）。**与 [24] 同源、与 [04] 不同源**；🟠 维持；建议单立计划但**必须与 [24] 合并成一份**，且派单前先确认 [24] 是否已被并行会话 triage。

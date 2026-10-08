裁决：值得修
归并：**建议单立**（不并入计划 07 / 08 / 03）—— 理由见 §7；与计划 07 任务 2 共用**同一处抽象**（"错误文案里的替代建议必须先验证可达"），但**不共用同一份计划**（计划 07 的场景是 GABP 离线，本条是 GABP 在线；且计划 07 §0.7 明确禁止缓存镜像 schema，与本条正交）。

一句话结论：本条被描述成「两层都坏、没有可用路径」是**错的**——`step_game_ticks` 的参数名一直是 `ticks`、schema 一直是完整下发的、历史上还跑过 400 tick；真正剩下的缺陷只有**一条**，且完全在本仓库内：**`PLAY_FOR_MAIN_THREAD` 的中文建议文案写死在本仓库里，它把调用方指向一个当时并不可用的替代方案**（`MCP/index.js:4254-4255`）。

---

## 3. 核实过程

### 3.1 读了哪些文件

| 文件 | 用途 | 行号来源 |
|---|---|---|
| `docs/issue-triage/AGENT-BRIEF.md` | 铁律全文 | `read` |
| `docs/issue-triage/skills/{using-superpowers,systematic-debugging,brainstorming}/SKILL.md` | 方法论 | `read` |
| `docs/mcp-吐槽板.md` | 条目 [22] 原文 | **`[System.IO.File]::ReadAllLines` 复核**，段落在 **57-86 行**（`## [22]` 标题在第 **57** 行，全文 `TOTAL_LINES=1037`） |
| `MCP/index.js` | 派发/渲染层 | **全部经 `[System.IO.File]::ReadAllLines` 复核**（`TOTAL=5460`）：`4253-4255` / `4249-4258` / `3611` / `3028-3029` / `2508-2518` / `2636-2657` / `4240-4314` / `4096-4125` / `3646-3735`（含 `2807-2809`、`3663-3666`）/ `1725-1764`。另与计划 07 §0.8-1 的独立断言（`mcpHelpResult` 起点 3646、缺陷分支 3653-3656）交叉对齐 |
| `MCP/gabpClient.js` | GABP 客户端 | `read` + **`ReadAllLines` 复核** `71-77`（`toMCPTool`，第 75 行为承重行） |
| `MCP/longTask/timeoutPolicy.js` | 超时推导表 | `read`（全文 154 行，行号直接可用） |
| `MCP/test/timeoutPolicy.test.js` | 参数名旁证 | grep 命中行 |
| `MCP/logs/mcp-20260928-173737.log` | 运行时证据 | **`ReadAllLines` 复核**，`TOTAL=707` |
| `MCP/logs/mcp-20261001-093829.log` | 运行时证据 | **共享读（FileShare.ReadWrite）复核**，`TOTAL=1326`（该文件被并行会话占用，普通 `ReadAllLines` 抛 `IOException`） |
| `docs/issue-triage/plans/2026-10-01-07-*.md`（734 行）、`...-08-*.md`（826 行）、`...-03-*.md` | 协调 | 标题 + 指定节 |
| `docs/issue-triage/triage/07-gabp-idle-no-fail-fast.md`（136 行） | 协调 | 标题索引 |

### 3.2 反编译了什么（运行时 RBS，非参考实现）

从 `C:\SteamLibrary\steamapps\workshop\content\294100\3727949765\1.6\Assemblies\` 加载并反编译：

| 程序集 | alias | 规模 |
|---|---|---|
| `RimBridgeServer.dll` | `rbs` | 689 类型 / 3188 方法，mvid `46e27953d7324877a067e7458bf2c0d5` |
| `RimBridgeServer.Core.dll` | `rbscore` | 133 类型 / 598 方法 |
| `Gabp.Runtime.dll` | `gabprt` | 36 类型 / 39 方法 |
| `Lib.GAB.dll` | `libgab` | 94 类型 / 294 方法，mvid `02339f43725c4ef6a41454c77507e911` |
| `RimBridgeServer.Contracts.dll` | `rbcontracts` | 19 类型 / 25 方法 |

> ⚠️ **搜索范围声明（含一次自我纠错）**：`search_string_literals` 的 `pattern` 默认按**字面量**匹配，不是正则。我最初用 `inputSchema|schema|parameters` 这类带 `|` 的模式在 `rbs`/`libgab` 上得到 0 命中，**那些"零命中"是无效证据，已全部重做**。下文所有"零命中"结论均来自**单词模式**重跑。

### 3.3 四条独立核实的结论

#### ① `PLAY_FOR_MAIN_THREAD` 与那条中文 message 是**本仓库**生成的 —— **不是 RBS**

`MCP/index.js:4249-4258`（`ReadAllLines` 复核）：

```js
4249:  if (name === 'rimworld.play_for') {
4250:    // 主线程错误分类（检查表缺陷 1）：RimBridge 端 get_realtimeSinceStartup 仅主线程可调。
4251:    const r = await callPlayFor(args);
4252:    const text = JSON.stringify(r);
4253:    if (/get_realtimeSinceStartup can only be called from the main thread/.test(text)) {
4254:      return contentErrorResponse('PLAY_FOR_MAIN_THREAD',
4255:        'rimworld.play_for 当前不可用：RimBridge 端调用 Unity get_realtimeSinceStartup 需在主线程执行（C# 端缺陷）。建议改用 step_game_ticks 逐帧推进，或等待 RimBridge mod 修复。');
4256:    }
4257:    return r;
4258:  }
```

- `errorCode` 字面量 `'PLAY_FOR_MAIN_THREAD'` 与中文 message 都出现在**本仓库这一行**。
- 触发条件是**正则匹配 Unity 引擎抛出的原始英文**（`4253`）。
- RBS 侧无此字符串：单词检索 `realtimeSinceStartup` 与 `main thread` 在 `RimBridgeServer.dll` 均 0 命中（该 assembly 的字符串索引已证实非空——单词 `ticks` 命中 17 条）。
  > 🔴 **主代理订正（2026-10-03，来自 [37] 的零命中审计）**：本行**后半句「`main thread` 0 命中」是错的，实际为 7 命中**。
  > 那 7 条**全是 RBS 自有的等待日志，不是 Unity 报错串**；而**本条结论依赖的两个零命中——`realtimeSinceStartup` 与报错全文——均复核为真**。
  > ⇒ **结论不变，但这一行证据是假的。**
  > **教训**：零命中被随手写进证据表、**没人回头验**。本流水线的 [37] 审计把 38 条零命中结论逐条重跑，才发现这一处。
  > 详见 `triage/37-search-symbols-current-context-alias-and-zero-hit-audit.md` §8.3.1。
- 运行时旁证：`MCP/logs/mcp-20261001-093829.log:1303`
  `[21:06:28.736] <INFO> tools/call agg_call_tool args={args={"durationSeconds":"3","speed":"2"}, tool=rimworld.play_for} → 100ms error(PLAY_FOR_MAIN_THREAD)`
  日志打的是 MCP 侧的 `errorCode`，不是游戏侧的。

**⇒ 缺陷 ① 成立，但归属本项目：文案是我们写的、建议是我们给的。**

#### ② `step_game_ticks` 的真实参数名 = **`ticks`** —— 反编译实证

`rbs` / `RimBridgeServer.RimBridgeTools.StepGameTicks`（memberId `46e27953d7324877a067e7458bf2c0d5:06000ED3:M`）反编译原文：

```csharp
[Tool("rimworld/step_game_ticks", Description = "Advance the paused game by an exact number of ticks, ...")]
public object StepGameTicks(
    [ToolParameter(Description = "Number of game ticks to advance. ...")] int ticks = 1,
    [ToolParameter(Description = "Maximum time to wait in milliseconds before cancelling the step request")] int timeoutMs = 10000,
    [ToolParameter(Description = "How often to poll step completion while waiting")] int pollIntervalMs = 10,
    [ToolParameter(Description = "Set time speed to Paused before stepping. ...")] bool pauseFirst = true,
    [ToolParameter(Description = "Play RimWorld's dev tick-once clock sound for each stepped tick")] bool playSound = false)
```

**五个真实参数名：`ticks` / `timeoutMs` / `pollIntervalMs` / `pauseFirst` / `playSound`。`tickCount` 不存在。**

`tickCount` 不被识别 ⇒ 走 C# 默认值 `ticks = 1` ⇒ 返回 `requestedTicks:1`。**这不是"静默忽略 bug"，这是 C# 可选参数的正常语义**——RBS 没有做未知参数校验，但也没有承诺过要做。

同一 assembly 里的 `PlayFor`（memberId `46e27953d7324877a067e7458bf2c0d5:06000D96:M` 与 `...:06000ED1:M`，两处同签名）签名是
`Object PlayFor(Int32 durationMs, String speed, Int32 pollIntervalMs, Boolean forceRequestedSpeed)`
⇒ 板上复现用的 `durationSeconds`（以及数字型 `speed`）**同样不是真实参数名/类型**，但因为 `play_for` 在主线程检查处就已失败，这两个错误没有暴露出来。

#### ③ schema **不是空的** —— 反编译 + **实机 `mcp_help` 双重证实**（**证伪板上第 ③ 点**）

**(a) 反编译：** `libgab` / `Lib.GAB.Server.GabpServer.BuildToolDescriptor`（`02339f43725c4ef6a41454c77507e911:060000D2:M`）：

```csharp
["name"]        = tool.Name,
["title"]       = ...,
["description"] = tool.Description,
["inputSchema"] = BuildInputSchema(tool.Parameters),
["parameters"]  = tool.Parameters.Select(p => new { name = p.Name, type = p.Type.Name,
                                                    description = p.Description,
                                                    required = p.Required,
                                                    defaultValue = p.DefaultValue }).ToList(),
["outputSchema"]= BuildOutputSchema(...),
["requiresAuth"]= tool.RequiresAuth,
```

`BuildInputSchema`（`060000D3:M`）产出的是**标准 JSON Schema**：`{ type:"object", properties:{ <每个参数名>: {type, description, default} }, additionalProperties:false, required:[...] }`。

**(b) 实机（只读 `mcp_help`，属铁律允许范围）：**

```
mcp_help { tool: "rimworld.step_game_ticks" } →
{
  "name": "rimworld.step_game_ticks",
  "category": "bridge",
  "availability": { "state": "agg", "reason": "经 agg_call_tool 调用" },
  "inputSchema": {
    "type": "object",
    "properties": {
      "ticks":          { "type":"integer", "description":"Number of game ticks to advance. Each requested tick is executed on a separate Unity update frame.", "default":1 },
      "timeoutMs":      { "type":"integer", "description":"Maximum time to wait in milliseconds before cancelling the step request", "default":10000 },
      "pollIntervalMs": { "type":"integer", "description":"How often to poll step completion while waiting", "default":10 },
      "pauseFirst":     { "type":"boolean", "description":"Set time speed to Paused before stepping. If false, the game must already be paused.", "default":true },
      "playSound":      { "type":"boolean", "description":"Play RimWorld's dev tick-once clock sound for each stepped tick", "default":false }
    },
    "additionalProperties": false
  },
  "precondition": "需 RimBridgeServer 连接",
  "usageHint": "该工具在 toolConfig.json 中禁用，需通过 agg_call_tool 调用（tool: rimworld.step_game_ticks）"
}
```

**schema 完整、参数名齐全、默认值齐全，与反编译签名逐字一致。**

链路也核了：`RBS BuildToolDescriptor` → `gabpTools`（`MCP/index.js:2644-2645`，由 `tools-changed` 事件填充，`:2514` 声明）→ `toMCPTool` 直传 `raw.inputSchema`（`MCP/gabpClient.js:71-77`，第 **75** 行）→ `mcpHelpResult` 原样回显（`MCP/index.js:3665` `inputSchema: meta.inputSchema`）。**这条链上没有任何一环会把参数名吃掉。**

> **附带结论：板上「`params:{}` 空」连字段名都不是真的。** 本仓库 `mcp_help` 返回的字段名是 `inputSchema`，全仓库无 `params` 字段。该措辞是转述，不是逐字引用。写计划时**不要**照着「把 `params` 填上」去做。

#### ④ `requestedTicks` / `completedTicks` / `advancedTicks` 的差异 —— **全部由 RBS 生成，且本仓库不加工**

- 三个字段名在 `RimBridgeServer.dll` 里各只有 **1 处**字符串字面量，全部落在
  `RimBridgeServer.RimWorldTickStepper.StepSnapshot.ToToolResponse()`（`06001932:M`）。
- 同一方法回显的**完整字段集**（反编译原文）：
  `success, stepId, status, requestedTicks, completedTicks, remainingTicks, startTicksGame, endTicksGame, advancedTicks, initialFrame, lastFrame, advancedFrames, initialTimeSpeed, startedAtUtc, completedAtUtc, message, state`
- 字段类型（`get_members_of_type` on `02000297:T`）：全部 `Int32`（`StartTicksGame` / `EndTicksGame` / `CompletedTicks` / `RequestedTicks` / `RemainingTicks` / `AdvancedTicks` / `AdvancedFrames` / `InitialFrame` / `LastFrame`），`StartedAtUtc` 为 `Nullable<DateTimeOffset>`。
- **`RimBridgeServer.Core.dll` 里 `requestedTicks|advancedTicks|completedTicks` 0 命中**；本仓库 grep 这三个字段名**除吐槽板正文外零命中**。

**语义（按命名与类型推断，标为假设）**：`requestedTicks` = RBS 解析后的实际请求数（参数名错时 = 默认 1）；`completedTicks` = 推进循环跑完的 tick 数；`remainingTicks` = requested − completed；`advancedTicks` = `endTicksGame − startTicksGame`，即**游戏世界时钟真正走过的 tick**。`advancedTicks` 恒等于 `completedTicks` 时说明逐帧步进正常、没有丢 tick。

**⇒ `advancedTicks` 并不恒为 1。** 它恒为 1 只是因为 `requestedTicks` 恒为 1（参数名错的连锁结果），不是因为步进器坏了。

### 3.4 「多 tick 推进」历史上真的跑通过 —— 日志实证

`MCP/logs/mcp-20260928-173737.log`（`ReadAllLines` 复核，`TOTAL=707`）：

| 行 | 内容 |
|---|---|
| 387 | `tools/call mcp_help args={tool=rimworld.step_game_ticks} → 0ms ok` |
| 388 | `tools/call agg_call_tool args={args={"ticks":"200","timeoutMs":"60000"}, tool=rimworld.step_game_ticks} → **4656ms success**` |
| 397 | `args={"timeoutMs":"60000","ticks":"300"} → 5987ms success` |
| 401 | `args={"timeoutMs":"60000","ticks":"120"} → 2429ms success` |
| 415 | `args={"timeoutMs":"60000","ticks":"90"} → 1887ms success` |
| 459 | `args={"timeoutMs":"90000","ticks":"400"} → **7337ms success**` |
| 470 | `args={"timeoutMs":"60000","ticks":"25"} → 482ms success` |

`MCP/logs/mcp-20261001-093829.log`（共享读复核，`TOTAL=1326`）：

| 行 | 内容 |
|---|---|
| 169 | `agg_call_tool args={args={"ticks":5}, tool=rimworld.step_game_ticks} → 1018ms success` |
| 1303 | `agg_call_tool args={args={"durationSeconds":"3","speed":"2"}, tool=rimworld.play_for} → 100ms error(PLAY_FOR_MAIN_THREAD)` |
| 1305 | `agg_call_tool args={args={"tickCount":"180"}, tool=rimworld.step_game_ticks} → **51ms success**` |

耗时与帧率自洽（400 tick / 7337 ms ≈ 18 ms/帧 ≈ 55 fps；`tickCount:"180"` 51 ms ≈ 只走了 1 tick 的量级）。
**⇒ 「给暂停中的游戏补几百个真 tick」这件事一直做得到，且在同一个工具上做得到。**

### 3.5 「铺场器」在本仓库中查无此物（搜索范围声明）

`DebugActionStageTestMap` / `AdvanceToTakeoff`：

- `grep`（ripgrep，全仓库文本文件）**只命中吐槽板本身**：`docs/mcp-吐槽板.md:85`、`docs/issue-triage/inbox/pending-20261001-212044.md:24`。
- 对模组目录下前 **40 个** `.dll` 做 ASCII 全字节扫描，**零命中**。
- 计划 03（`2026-10-01-03-enable-search-debug-actions-and-fix-menu.md`）全文对 `StageTestMap|AdvanceToTakeoff|step_game_ticks|tick` 的检索 **0 命中**。

**⇒ 我不能说这个铺场器「不存在」**——本报告只能说：它不在本仓库已落盘的任何文本文件或已扫描的 40 个 DLL 里。板上第 ③ 点的影响叙述（`≤8 tick 预算`、空中样本不够、1/3 祭品停在 `WalkingToTakeoff`）**我无法核实**，它描述的更像是并行会话树上尚未落盘的进行中工作。

---

## 4. 影响与严重度复评

板上标 🟠，主张「绕法成本 ≈ 6 分钟/轮，4 轮里 1 轮失败」。**复评为 🟡**（体验糙 / 静默踩坑），**不是 🟠**，理由逐条：

| 板上的说法 | 复评 | 依据 |
|---|---|---|
| 「没有任何工具能给暂停中的游戏补几个真 tick」 | **证伪** | `ticks` 参数真实存在；日志有 200/300/120/90/400/25 tick 的成功记录（§3.4）；400 tick 只要 7.3 s |
| 「schema 是 `params:{}` 空」 | **证伪** | 实机 `mcp_help` 返回完整 5 参数 schema（§3.3b）；反编译 `BuildInputSchema` 产出标准 JSON Schema |
| 「参数名不对只走了 1 tick」 | **属实，但责任在调用方** | `tickCount` 不是真实参数名；RBS 走 C# 默认值 `ticks=1` |
| 「4 轮里 1 轮失败」「≈6 分钟/轮」 | **无法核实，且成本估算被夸大** | 铺场器不在本仓库（§3.5）；即使按 `WalkingToTakeoff` 卡住，`step_game_ticks{ticks:180}` 是 7 秒内可得的补救，不是 6 分钟冷重启 |
| 「两层都坏」 | **不成立** | 只有第一层（`play_for` 主线程）真坏；第二层是好的，只是调用方没查 schema |

**为什么不是 🔴（不会让自动化得出事实相反的结论）：**
`step_game_ticks{tickCount:180}` 返回的是 `success:true` + `requestedTicks:1`。这是**诚实的**——RBS 如实回报了「我实际被要求走 1 tick」。工具层没有撒谎，是调用方读错了。这意味着自动化若只看 `success` 会漏判，但只要看 `requestedTicks`/`advancedTicks`（板上自己都写了这是"唯一线索"）就能发现。**这属于"可发现但费 token"，属于 🟡。**

**为什么仍然值得修（升回 🟠 的唯一理由）：**
`play_for` 的失败文案（`MCP/index.js:4255`）**主动**把调用方送去第二个当时不可靠的替代方案，而且**没有告诉对方替代方案要用什么参数**。这是本项目自己制造的一次错误导航。板上建议 (c) 说得很准：**两层都坏时应该直接说"没有可用路径"**——而既然第二层其实是好的，正确的说法是「**改用 `step_game_ticks{ticks:N}`**」，并把参数名写进去。

**另有一条被顺带证实的真实隐患（本条范围内、归属本仓库）：**
RBS 下发的 schema 明确写了 `"additionalProperties": false`，但调用路径 `agg_call_tool` 的 `args` 是 `additionalProperties: true`（`MCP/index.js:2808`），**没有任何一层执行这个契约**。后果：未知参数被静默丢弃、默认值顶替、调用照常 `success`。`play_for{durationSeconds:3}` 会用 RBS 的默认 `durationMs` 跑一次——**这是本条真正的"静默用错值"，比 `tickCount` 那次更值得修**，因为它会让 `nonBlocking` 之外的路径也悄悄跑错时长。

---

## 5. 责任归属

| 部分 | 归属 | 说明 |
|---|---|---|
| `play_for` 的 Unity 主线程缺陷 | **RBS 第三方**（`get_realtimeSinceStartup` 只能在主线程调） | 该原始错误由 Unity 引擎抛出；`RimBridgeServer.dll` 内无 `realtimeSinceStartup` / `main thread` 字面量（该 assembly 字符串索引非空，`ticks` 命中 17 条）。**本仓库无能力修**，只能改文案 |
| `PLAY_FOR_MAIN_THREAD` errorCode + 中文建议 | **本项目 100%** | `MCP/index.js:4254-4255`，是我们把 Unity 英文错误翻译成中文并**附加建议**的地方 |
| `step_game_ticks` 的参数名 | **无缺陷**（`ticks` 是唯一正确名） | `tickCount` 是调用方写错 |
| 镜像工具 schema 是否下发 | **无缺陷** | `libgab.BuildToolDescriptor` / `BuildInputSchema` 产出完整 schema，本仓库 `gabpClient.js:75` → `index.js:3665` 原样透传 |
| 未声明参数被静默吞掉 | **本项目** | 契约在 RBS 侧，执行在 MCP 侧，而 MCP 侧没执行 |

**一句话：能力缺陷在上游，导航缺陷在我们自己。**

---

## 6. 修的话怎么修

### 6.1 能在本仓库改（`MCP/index.js` / `MCP/gabpClient.js`，不需要碰 RBS）

**修法 A（必做，成本 ≈ 1 处文案）—— 把 `PLAY_FOR_MAIN_THREAD` 的建议改成"可达且带参数名"。**

文件：`MCP/index.js:4254-4255`。

- 最低限度：建议文案从"建议改用 `step_game_ticks` 逐帧推进"改成带参数名的形态，例如
  `改用 rimworld.step_game_ticks { ticks: <N>, timeoutMs: <M> }（注意参数名是 ticks，不是 tickCount）`。
- 更完整：按板上 (c) 的思路，把"替代建议"提成**一个可验证的常量**（与计划 07 任务 2 的"指路单一事实源"同构），并在返回体里补 `alternativeVerified: <bool>`，让调用方能判断这条建议的可信度，而不是只能读一段散文。
- 判定"替代是否可用"的真实依据应该是**能力层**（`rimworld/play_for` 与 `rimworld/step_game_ticks` 的 `CapabilityExecutionKind`，反编译见 `rbs` 的 `ResolveExecutionKind`：`06000D38:M` 中两者**都是** `BackgroundObserved`）而不是硬编码。

**修法 B（建议做，成本 ≈ 1 个纯函数 + 1 处调用）—— 未知参数不得静默吞掉。**

- 纯函数（便于单测，套路同既有 `buildCapabilityReport`）：给定 `args` 与该工具的 `inputSchema.properties`，返回**未声明键列表**。
- 在 GABP 镜像派发路径上（`MCP/index.js:4262` 起的 `isGabpMirrorName` 块）对该列表做处理，二选一：
  - **fail-loud**：有未知键 ⇒ 直接返回 `UNKNOWN_ARGUMENT`，列出你传的键与该工具认识的键（板上 (b) 的诉求）；或
  - **至少不 fail-loud 也要 warn**：透传但在响应里加 `ignoredArgs: ["tickCount"]`。
- 参照物：计划 08 §2.3-2 已经为 `query` 定了同样的契约（"被覆盖的参数必须点名回显……不回显 = 假成功"），**两边应当用同一套措辞**。

**修法 C（可选，成本 ≈ 1 行）—— `mcp_help` 的 `usageHint` 对 GABP 镜像说谎。**
实机返回里 `availability.state = "agg"`，同时 `usageHint` 说"该工具在 toolConfig.json 中禁用，需通过 agg_call_tool 调用"。这句对**已连接**的镜像成立，但计划 07 §0.8-4 已经指出：GABP 未连接时它会把人送去撞 `index.js:4274` 那堵墙。**这一条归计划 07 任务 2，不在本条重复做。**

### 6.2 必须改上游 RBS（本仓库无法替代）

| 事项 | 说明 |
|---|---|
| `play_for` 的主线程缺陷 | 需要 RBS 把耗时等待放到主线程（或改用不受主线程限制的时钟）。**本仓库只能改文案，不能修行为** |
| （可选）GABP 侧未知参数校验 | `tools/call` 若对 `additionalProperties:false` 的 schema 做拒绝，会让 `tickCount` 立刻报错而不是静默取默认值。**但修法 B 已在本仓库达成同等效果，不构成阻塞** |

### 6.3 明确**不要**做的事

- ❌ 不要把 `step_game_ticks` 的 schema "补"成手写常量——它已经是完整的，改成硬编码只会制造第二个事实源并引入腐烂风险。
- ❌ 不要改 RBS 的 `inputSchema` 生成逻辑——它没坏。
- ❌ 不要把"给暂停中的游戏补 tick"当成缺能力来设计新工具——能力一直有。

---

## 7. 与计划 07 / 08 / 03 的关系 + 三条协调结论

### 7.1 关系判定

| 计划 | 关系 | 依据 |
|---|---|---|
| **计划 07**（GABP 离线时 `mcp_help` 兜底，3 任务 / 11 测试 / 734 行） | **不合并。** 场景不同（离线 vs **在线**），且计划 §0.7 明确「**不缓存镜像 schema**」、占位详情固定 `inputSchema: null` —— 与本条"在线 schema 本来就完整"完全正交。计划的离线夹具（§0.4 路径①②③）也根本跑不到本条路径 | `plans/2026-10-01-07-...md:23-35`（缺陷链路表）、`:96`（§0.7 第 1 条）、`:80-88`（§0.6 文件清单，明写"明确不动 `MCP/gabpClient.js`"） |
| **计划 08**（`mcp_help` 加 `query`） | **不合并。** 本条不是"检索不到工具"，而是"检索到了但没用对参数"——schema 本来就在返回体里。而且计划 §2.2 已明确排除改动 `tool` 分支的返回体形状（"会动到 `mcp_help tool=<名字>` 这个已被多处测试断言的输出契约"） | `plans/2026-10-01-08-...md:109-116`（§2.2）、`:118-126`（§2.3） |
| **计划 03**（`search_debug_actions` 暴露 + 菜单挂载） | **不相关。** 全文对 `StageTestMap|AdvanceToTakeoff|step_game_ticks|tick` 检索 **0 命中** | `plans/2026-10-01-03-...md` 全文 grep |

### 7.2 三条协调结论（明确回答派单的三问）

**结论 1：「参数名不生效」的责任层 = 调用方 + 文档可发现性，**不是** RBS，也不是本仓库的 schema 管道。**
- 真实参数名 **`ticks`**（反编译 `06000ED3:M` + 实机 `mcp_help` 双重证实）。
- `tickCount` 未命中 → C# 默认参数 `ticks = 1` → `requestedTicks:1`。**RBS 行为完全正常。**
- 本仓库的透传链（`gabpClient.js:75` → `index.js:3665`）**零缺陷**：schema 完整送达。**⇒ 这一半不需要修 schema，只需要把参数名写进 `play_for` 的建议文案（修法 A）+ 别再静默吞未知参数（修法 B）。**

**结论 2：不并入计划 07，建议单立；但"指路单一事实源"这个抽象要与计划 07 任务 2 共用。**
- 计划 07 任务 2 的标题就是「把『去 mcp_help 查』提成**单一事实源**，并给镜像 fail-fast 补上指路」，它动的正是 `MCP/index.js:3611` 与 `:4284` 两处指路文案。
- 本条的 `index.js:4255` 是**同一反模式的第三份副本**（在错误文案里嵌一条指路）。
- **建议：把"错误文案中的替代建议必须指向一个已验证可达的目标"提炼成 07 任务 2 的那个单一事实源，让 07/22 两边都调它。** 共用**抽象与常量**，不共用**计划文档**——因为计划 07 的任务矩阵、夹具（离线三路径）、断言集合与本条无一重合，强行合并会让 07 的 11 个测试被无关分支污染，且违反 §0.7 的既定范围。
- **锚点**：若主代理决定合并，唯一可接受的锚点是 **计划 07 任务 2 的"指路单一事实源"那一节**（不是任务 1 的离线占位详情）。但我**不建议**这么做。

**结论 3：本条的 `mcp_help` 部分不并入计划 08。**
- 板上诉求 (b)「把 `step_game_ticks` 的参数名进 schema」——**已经是既成事实，无需实施**（实机返回已含 5 个参数名与默认值）。这一半应当**从条目里划掉**，否则 PLAN 子代理会去"修"一个没坏的东西。
- 真正残留的 `mcp_help` 相关问题只有一条：**`usageHint` 对离线镜像说谎**——而这条计划 07 §0.8-4 已经点名、由计划 07 任务 2 覆盖。**不重复。**

### 7.3 建议的落点

新建一份 `plans/2026-10-01-22-play-for-guidance-and-unknown-arg-guard.md`，**两任务**：
- **任务 1**：修 `MCP/index.js:4254-4255` 的 `PLAY_FOR_MAIN_THREAD` 文案 + 把替代建议提为可验证常量（与计划 07 任务 2 共用同一事实源）。
- **任务 2**：GABP 镜像未知参数守卫（纯函数 + fail-loud/warn 二选一），措辞对齐计划 08 §2.3-2。

**规模预期**：远小于计划 07（3 任务 / 11 测试 / 734 行）；本条是单点文案 + 一个纯函数。

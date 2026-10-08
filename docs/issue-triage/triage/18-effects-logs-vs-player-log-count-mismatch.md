裁决：不值得修
归并：仅并入计划 01 的任务 3（`EXECUTE_DEBUG_ACTION_NOTE` 文案追加**一句话**），**不动任何代码**）（理由：`repeatCount` 字段真实存在，但语义与本条设想的完全不同；它想加的「本条已去 UE 回显」标记既不存在、也无法从 `repeatCount` 推导；本条给出的因果解释已被源码证伪）

一句话结论：`effects.logs[]` **没有**去 UE 回显副本（`ShouldCollapse` 要求 `Message` 逐字相等，而两条副本的 `Message` 本来就不同），所以「两个地方数字不同」这个前提本身站不住；真实成因是**本项目自己的 UE→Player.log 日志桥**，属于设计使然，不该改代码，只需补一句文案。

---

## 1. 核实过程

### 1.1 吐槽板原文（`docs/mcp-吐槽板.md`，标题锚点 `^## \[18\]`，1-based 行号 **39**；`[System.IO.File]::ReadAllLines` 复核，全文 783 行）

四条断言：(a) `effects.logs[]` 是**去重后**的；(b) `Player.log` 里同一条消息**被 UE 控制台回显两遍**（`[UnityExplorer] [Unity] …` + 原行）；(c) `logCount` 是 18，而不去前缀 grep 会得到 ~36；(d) **`effects.logs[]` 的元素里已经带 `repeatCount` 字段**。状态：待绕过（调用方自己过滤 `^\[UnityExplorer\]`）。

### 1.2 断言 (d) 独立核实：**`repeatCount` 确实存在 —— 属实**

反编译 `RimBridgeServer.RimWorldDebugActions.DescribeEffects`（运行时 workshop DLL，memberId `46e27953d7324877a067e7458bf2c0d5:060010CB:M`，hash `c7ee7c50d2901301…`）：

```csharp
logs = readOnlyList.Select(entry => new {
    sequence = entry.Sequence, level = entry.Level, message = entry.Message,
    source = entry.Source,
    repeatCount = entry.RepeatCount,        // ← 确实被序列化进 effects.logs[]
    timestampUtc = entry.TimestampUtc
}).ToList(),
```

⇒ **「元素里已经带 `repeatCount`」这句话是准的。**

### 1.3 但 `repeatCount` 的语义与本条设想的**完全不同**

`RimBridgeServer.Core.LogJournal.Record`（`:0600017C:M`，hash `492d7fb162b46120…`）：

```csharp
if (_entries.Count > 0 && ShouldCollapse(_entries[0], bridgeLogEntry)) {
    _entries[0].TimestampUtc = bridgeLogEntry.TimestampUtc;
    _entries[0].RepeatCount++;              // ← 只在「折叠」时 +1
    _entries[0].Sequence = _nextSequence;
    bridgeLogEntry = Clone(_entries[0]);
}
```

`LogJournal.ShouldCollapse`（`:06000180:M`，hash `e3f298ab1ae5c9ff…`）要求 **11 个字段全部 `string.Equals` 相等**（Level / Message / **StackTrace** / Source / OperationId / CapabilityId / ParentOperationId / RootOperationId / ScriptStatementId / ScriptStepId / ScriptCall），**且** `(candidate.TimestampUtc - existing.TimestampUtc).TotalMilliseconds <= _collapseWindowMs`（`LogJournal..ctor(collapseWindowMs = 2000)`，hash `516d18945c8a1bd9…`）。

⇒ **`repeatCount` 是「连续 2 秒内、完全逐字相同的同一条日志被折叠了几次」的计数器。它与 UE 回显毫无关系。**

### 1.4 断言 (a)「`effects.logs[]` 已经把回显去重」—— **被源码证伪**

折叠的前提是 `string.Equals(existing.Message, candidate.Message)`。而 Player.log 里的那两条副本**消息正文本来就不同**：

```
[UnityExplorer] [Unity] [零覆盖] 本轮共 5 个用例零覆盖……
[零覆盖] 本轮共 5 个用例零覆盖……
```

后者是 `Source/UELoader/ExplorerBootstrap.cs:207` 用 `Log.Warning($"[UnityExplorer] {message}")` 重新写出去的那一份（`:204` Error / `:207` Warning / `:210` Message，三处都加了 `[UnityExplorer]` 前缀），与原行**不是同一个字符串**。⇒ **它们永远不可能被 `ShouldCollapse` 合并**，RBS 的 journal 里就是**两条独立记录**。

⇒ **「`effects.logs[]` 是一条消息一次」为假；`effects.logs[]` 里同样有两份。**

### 1.5 断言 (b)「被 UE 控制台回显两遍」—— 现象为真，但**归因要改**

我只读调了 `tail_rimworld_log`（只读工具，不改游戏状态）。真实 `Player.log` 尾部**确实**成对出现，且两份的栈也不同：

- 前一份的栈里有 `UELoader.ExplorerBootstrap:OnUELog (string,UnityEngine.LogType) (at C:/…/Source/UELoader/ExplorerBootstrap.cs:207)`，调用链是 `UnityExplorer.Runtime.MonoHelper:Application_logMessageReceived` → `ExplorerCore:LogUnity` → `OnUELogWarning` → `OnUELog` → `Verse.Log.Warning_Patch1`
- 后一份的栈直接是 `Verse.Log.Warning_Patch1` ← `DegeneratePatherPathTests:Run` ← `DemoMain:RunAll`

**所以真正的机制是：本模组的 UE→RimWorld 日志桥把每条 UE 看见的日志又用 `Verse.Log` 写回了一遍 Player.log，于是同一件事出现两行。** 这不是「UE 控制台回显」这种外部现象，是 `ExplorerBootstrap.OnUELog`（`Source/UELoader/ExplorerBootstrap.cs:195-218`）**主动制造的第二份**。

顺带：`RimBridgeServer.RimBridgeLogs.Initialize` 订阅的是 `Application.logMessageReceivedThreaded`，即 RBS 从 Unity 侧收日志 —— **UE 桥接写回 Player.log 的那一次同样会再触发一次 `logMessageReceivedThreaded`**。两边都收到，两份都在。方向一致。

### 1.6 断言 (c)「`logCount` 是 18、grep 是 ~36」—— **无法核实**

要核实必须真的调一次 `execute_debug_action`，属禁止工具，**我没做**。

但由 §1.4 的结论可以给出一个**相反的预测**：既然 journal 里两条副本是独立记录，`effects.logCount` 应当**同样接近 grep 到的 ~36**（再减去被 `ShouldCollapse` 折叠掉的真重复）。若如此，**本条「同一个量在两处数字不同」的前提本身就不成立**。
⚠️ 这只是**基于源码的预测，不是实测**。真要定案需要主代理授权后跑一次真调用。

### 1.7 额外确认：本仓库 C# 侧没有任何「去重 / 标记 UE 回显」的逻辑

`Source\` + `McpRimDebug\` 全部 `.cs` 搜 `repeatCount` → **零命中**；搜 `logCount` → 仅 `Source/UELoader/DpaHeadlessBridge.cs:24` 一条注释里的 `currentLogCount`（无关）。⇒ 本条设想的修复（「在返回值里标出本条已去 UE 回显」）在本仓库**无处可落**，因为该字段是 RBS 产出的。

---

## 2. 影响与严重度复评

**复评：🟡 低，维持不变 —— 但降级理由与吐槽板写的不同。**

- 吐槽板的理由是「调用方必须自己记住 UE 会回显两遍」。**实测下来这条提醒本身没错，但它针对的机制被搞错了**：不是「UE 回显」，是**本模组的日志桥写了两遍**。
- **不会造成「事实相反的结论」。** 按 §1.4，若 `logCount` 与 grep 数字其实是一致的，那连「两处数字不同」这个前提都不成立；即便不一致，方向是**两处都可能偏大**（都含副本），不是「一处说跑了、一处说没跑」那种硬性对立。
- **已有的绕过（过滤 `^\[UnityExplorer\]`）是对的、且是唯一需要的动作。** 吐槽板的处置建议本身没问题。
- 真正值得留一句文案的点是：**别把 `repeatCount` 误读成「去重了几条 UE 回显」** —— 它是「连续相同日志折叠计数」，这是本条核实后最容易被后来者误用的字段。

---

## 3. 责任归属

**纯文档（就本条设想的修复而言）+ 本项目的日志桥（就重复现象的成因而言）。**

- **不是 RBS 的锅**：journal 的折叠逻辑（`ShouldCollapse`）是合理设计，它对**真正逐字重复**的日志生效；本条期待的「UE 回显去重」从来就不在它的职责里 —— 因为那两个副本的 `Message` 就不同。
- **不是 UnityExplorer 的锅**：它只是把 Unity 日志转发给订阅者。
- **成因在本项目的 `Source/UELoader/ExplorerBootstrap.cs:195-218`**：这个桥的**目的**就是把 UE 日志搬进 Player.log（[15] 已经确立了这个行为是有用的）。**去掉或去重它会直接破坏 [15] 的修复**，所以**不该动**。
- **本条真正需要的**只有一件事：在 `execute_debug_action` 的文案里说清「Player.log 里 UE 来源的日志有两行、且 `repeatCount` 是折叠计数不是回显计数」。

---

## 4. 修的话怎么修

**不改代码。** 只做一件事：

**并入计划 01 任务 3**，在它新增的常量 `EXECUTE_DEBUG_ACTION_NOTE`（计划 01 步骤 3 的代码块）末尾追加**一句话**，例如：

> `effects.logs[]` 与 `Player.log` 都会同时包含 UE 来源日志的两份（`[UnityExplorer]` 前缀副本 + 原行），按 `^[UnityExplorer]` 过滤后再数条数；`logs[].repeatCount` 是「连续 2 秒内完全相同的日志被折叠了几次」，**不是**去掉了 UE 回显。

**三条明确不做的**（若被写进计划就是错的）：

- **不要**在 MCP 层加去重/标记逻辑：源数据里没有可用的标记位（§1.7），硬造一个只会制造新的假信号。
- **不要**改 `ExplorerBootstrap.OnUELog` 的前缀或去重：会破坏 [15] 已确立的「UE 日志能在 Player.log 里被看到」这一行为。
- **不要**把 [17] 的 `logWindow` 和本条合并成同一个机制：两者碰的是同一个 `effects.logs[]`，但一个是 **RBS 的窗口上限**（[17]），一个是**本项目的日志桥写了两遍**（本条），判据与修法都不一样，只是在文案里相邻而已。

---

## 5. 与计划 01 的关系 + 协调结论

**结论：不单独立计划；仅把一句话文案并入计划 01 的任务 3，代码部分零改动。**

**为什么不并入代码部分**：本条在核实后**没有留下任何可实施的代码改动**——它想要的字段不存在，可去重的对象也不是重复对象。并入代码只会给计划 01 增加一个无判据、无 fixture 可写的分支。

**为什么不单独立计划**：剩下的全部产出就是一句文案，而计划 01 的任务 3 正好在同一处写文案（`MCP/index.js:2930` 附近的 `TOOL_USAGE_NOTES` 与它新引入的 `EXECUTE_DEBUG_ACTION_NOTE` 常量）。**另起一份计划去改同一个对象，等于让两个计划再次在同一处代码上打架** —— 与 [17] 并入的理由同源，但结论相反。

**具体锚点（供主代理回灌给计划 01 的作者）**：

1. **任务 3 步骤 3**，`EXECUTE_DEBUG_ACTION_NOTE` 常量（`plans/2026-10-01-01-debug-action-execution-evidence.md:711-716`）的末尾追加上面那句。
2. **任务 3 步骤 6 的「改动清单」**：行数 +1。
3. **计划 01 §6「明确不做」表**：加一行 ——「为 UE 回显副本加去重/标记」→ 副本的 `Message` 与原行本就不同，`LogJournal.ShouldCollapse` 不会也**不应**合并它们；本项目无从在 `effects.logs[]` 里造出可信的回显标记位。
4. **与 [17] 的边界（写进两份报告的交叉引用即可，不要合并实现）**：[17] 负责「`logs` 最多 50 条且不告诉你被截了」，本条负责「同一条逻辑日志在两处各占两行」。二者在同一段文案里相邻，但**不要**共用一个字段或一个判定分支。

**顺带修正本条对计划 01 的一处潜在污染**：本条建议的「在返回值里也标出『Player.log 里会出现 2 行』」若被写进计划 01，会引入一个**无法从回包推导**的字段（回包里没有任何东西能区分「这条是 UE 回显副本」）。若已有人起草了这类字段，**应当撤掉**。

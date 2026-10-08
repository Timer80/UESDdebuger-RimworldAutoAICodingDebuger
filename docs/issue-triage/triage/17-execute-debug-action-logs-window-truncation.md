裁决：值得修
归并：并入计划 01 的任务 1（`MCP/executionEvidence.js`）+ 任务 2（`wrapGABPResult` 接线，**实际无需改动**）（理由：改的是同一个文件、同一个返回信封、同两个测试文件；单立计划必然与计划 01 在 `MCP/index.js:2689-2694` 打架。**但只能修「可观测性」那一半 —— 被挤掉的 50 条本仓库拿不回来**）

一句话结论：50 条窗口是 **RimBridgeServer 硬编码**的（`DescribeEffects` 里字面量 `GetEntries(50, …)`），MCP 与 GABS 两层都没有再截一次；但「返回里没有任何字段说明被截断了」是**本项目可以在包装层修掉的**，成本约 10 行 + 4 条测试。

---

## 1. 核实过程

### 1.1 吐槽板原文（`docs/mcp-吐槽板.md`，标题锚点 `^## \[17\]`，1-based 行号 **67**，全文 783 行）

用 `[System.IO.File]::ReadAllLines` 复核，`## [17]` 在第 **67** 行，`## [18]` 在第 **39** 行（未用 `read` 工具的行号）。

条目断言：`logCount:50` 是返回条数不是真实条数；**关键第一条与 `[零覆盖]` 逐例行被挤掉**；返回里没有任何截断标记；`ResultWasTruncated:false` 指 operation 的 Result 而非 logs 数组；`logs[0].message` 是**最后一条**、`logs[49]` 是 50 条窗口里最早的那条（即**倒序**）；状态「待绕过（改用 `Get-Content` + grep 直接读 `Player.log`）」。

### 1.2 三层逐一排查，先证伪「MCP 层截断」

| 层 | 查了什么 | 结论 |
|---|---|---|
| **MCP** | `MCP/gabpClient.js` 全文 727 行找 `slice(` / `.length <` / `limit` / `maxBytes` | **无条数上限**。唯一上限是 `MCP/gabpClient.js:59` `const MAX_BUFFER_BYTES = 16 * 1024 * 1024;`，用于 `:598` `:600` 的**接收缓冲**（16MB 级）；40KB 远不到，且它是字节级分帧保护，不产生「50 条」 |
| **MCP** | `MCP/index.js` 全文 5460 行找 `logsTruncated\|logsTotal\|logWindow\|Truncat` | **零命中**（只有 `:4940`-`:4967` 一个与本条无关的惰性索引 `complete/truncated`） |
| **MCP** | `MCP/index.js:4350` `execute_debug_action` 的分发 | `callGABPTool(_rbs, args, undefined, _opts)` —— **第三个形参 `filterResult` 传的是 `undefined`**（已用 `ReadAllLines` 复核） |
| **MCP** | `MCP/index.js:2761-2762` | `if (typeof filterResult === 'function') { data = filterResult(...) }` —— 上一步传 `undefined`，**这条分支不进**，`data = res.result` 逐字节原样 |
| **MCP** | `MCP/index.js:2764` | `JSON.stringify(wrapGABPResult(data, true), null, 2)` —— 纯序列化，无切片 |
| **GABS（Go）** | `GABS-main\internal\**\*.go` 找 `TakeLast\|Truncat\|MaxLogs\|LogLimit\|effects\|logCount` | 全部命中都是**游戏进程 stdout/stderr 尾巴**（`internal\process\hookrunner.go:225-239` `tailBuffer`）与**工具名归一化**（`internal\util\tool_normalization.go:67-70`），**没有一处碰 `effects.logs[]`**。⚠️ 按主代理指示：GABS 是换语言重写的同类实现，**此处仅作协议参考，不作为 RBS 行为证据** |
| **本仓库 C#** | `Source\**\*.cs` + `McpRimDebug\**\*.cs` 找 `logCount\|"effects"` | **零命中**（唯一 `currentLogCount` 在 `Source\UELoader\DpaHeadlessBridge.cs:24` 的注释里，无关）。`effects` 结构**不是本仓库产出的** |

⇒ **MCP 层与 GABS 层均无截断。MCP 是纯透传。**

### 1.3 截断的真实位置：RimBridgeServer（反编译确认）

> ⚠️ **反编译对象以「游戏里真正在跑的那份」为准**：创意工坊 `C:\SteamLibrary\steamapps\workshop\content\294100\3727949765\1.6\Assemblies\`。
> 我最初查的是开发副本 `RimBridgeServer-main\1.6\Assemblies\`，**两者的 `RimBridgeServer.dll` / `RimBridgeServer.Core.dll` SHA256 不同（MVID `af1f59d6…` vs `46e27953…` / `ce27bcd7…` vs `5f081ec2…`）**，所以全部结论在运行时副本上重跑了一遍，**每个方法的反编译 hash 两边逐字相同**，结论成立。

**`RimBridgeServer.RimWorldDebugActions.DescribeEffects`**（memberId `46e27953d7324877a067e7458bf2c0d5:060010CB:M`，hash `c7ee7c50d2901301…`）：

```csharp
IReadOnlyList<BridgeLogEntry> readOnlyList =
    RimBridgeCapabilities.LogJournal?.GetEntries(50, null, before.LogSequence)   // ← 50 是硬编码字面量
    ?? Array.Empty<BridgeLogEntry>();
return new {
    logCount = readOnlyList.Count,                                              // ← 饱和在 50，永远不是真实条数
    logs = readOnlyList.Select(entry => new {
        sequence = entry.Sequence, level = entry.Level, message = entry.Message,
        source = entry.Source, repeatCount = entry.RepeatCount, timestampUtc = entry.TimestampUtc
    }).ToList(),
    ...
};
```

**`RimBridgeServer.Core.LogJournal.GetEntries`**（`5f081ec2db344499a3cc73ddc96ec4ef:0600017D:M`，hash `2073f2403bb7af83…`）：先按 `minimumLevel` / `afterSequence` / `operationId` … 过滤，末尾 `return source.Take(limit).Select(Clone).ToList();`

**`RimBridgeServer.Core.LogJournal.Record`**（`:0600017C:M`，hash `492d7fb162b46120…`）关键三行：

```csharp
_entries.Insert(0, bridgeLogEntry);                       // ← _entries 是【倒序】：index 0 = 最新
if (_entries.Count > _maxEntries)
    _entries.RemoveRange(_maxEntries, _entries.Count - _maxEntries);   // ← ring 上限 2000
```

⇒ **`Take(50)` 取的是「最新的 50 条」，且返回顺序是倒序（最新在前）。** 这正好解释了吐槽板 `:80-81` 那两句看起来自相矛盾的复现描述（`logs[0]` 是最后一条 / `logs[49]` 是窗口里最早的那条）—— **吐槽板这句是准的，且现在有了机制解释。**

**`LogJournal..ctor(maxEntries = 2000, collapseWindowMs = 2000)`**（`:06000176:M`，hash `516d18945c8a1bd9…`）。

**为什么调用方无法自救**：`RimWorldDebugActions.CaptureExecution`（`:060010CA:M`）把 `LogSequence = LogJournal?.LatestSequence ?? 0` 存进 `ExecutionCapture.LogSequence`，而回包里的 `stateBefore = executionCapture.State`（`ToolStateSnapshot`）——**两者是不同字段，`stateBefore` 里没有 `logSequence`**。所以窗口下界**没有交给调用方**，返回体里也就无从判断「后面还有被丢掉的」。

### 1.4 唯一能把 50 条补回来的通道（另有一个工具）

**`RimBridgeServer.DiagnosticsCapabilityModule.ListLogs`**（`46e27953…:06000D5F:M`，hash `367bba958a496dae…`，工具名 `rimworld/list_logs`）：

```csharp
public object ListLogs(int limit = 50, string minimumLevel = "info", long afterSequence = 0L,
                       string operationId = null, string rootOperationId = null, string capabilityId = null)
    => new { logs = _logJournal.GetEntries(limit, minimumLevel, afterSequence, …) };
```

`limit` **由调用方给**，所以 `list_logs{limit:2000}` 能一次取回整个 2000 条 ring（覆盖「几百行」场景）。
**但它不是分页**：`afterSequence` 过滤的是 `Sequence > X`（更新的），**没有 `beforeSequence`**，所以拿不到「比窗口更老」的那部分；且返回同样**倒序**。真要补数只能在 MCP 层按 `logs[].sequence` 自行裁剪。⚠️ `minimumLevel` 默认 `"info"`，`LevelPriority` 的完整档位我没逐个核实（全量补数时应显式传最低档，不要依赖默认值）。

### 1.5 关于 `ResultWasTruncated`（吐槽板 `:74-75`）

`ResultWasTruncated` 在 `RimBridgeServer.dll` 与 `RimBridgeServer.Core.dll` 里**零字符串命中**；`Gabp.Runtime.dll`（两个副本 SHA256 相同）里 `Truncated` / `durationMs` 均**零命中**。RBS 里 `durationMs` 只出现在 `CapabilityScriptRunner` / `CapabilityScriptReferenceBuilder` 的**脚本参数**语境（与 `maxDurationMs` 成对）。
⇒ **`execute_debug_action` 的真实回包里既没有 `ResultWasTruncated`，也没有 `durationMs`，也没有 `operation` 包装**——返回的是顶层 `success / path / node / targetPawn / mapTarget / stateBefore / stateAfter / effects / followUp`（`RimWorldDebugActions.ExecuteDebugActionResponse`，`:060010B9:M`）。吐槽板这一句的**结论方向对**（「没有任何字段说明被截断了」），**但它引用的字段名对不上当前 RBS**。

---

## 2. 影响与严重度复评

**复评：🟠 中，维持吐槽板的 `🔴→🟠` 判定 —— 但要逐条拆开看，两句断言强度不同。**

- **「以为那些用例没跑」——站得住，且是本条最主要的危害。** 窗口是**最新 50 条**，所以 `== [XxxTests] 开始 ==` 这种开头行和逐例行必然先被丢掉。调用方看到的是一个「只有尾巴、没有头」的数组，据此说「那些用例没跑」是一个**方向相反的假阴性**。这符合 🔴 尺子里「事实相反的结论」那一栏的形状。
- **「只看汇总行就判绿」——部分站住，比吐槽板写的弱一些。** 因为数组是**倒序**，`logs[0]` 恰好就是最新的汇总行（`== [FlightAStar] 全部通过 ==`），扫一眼首元素确实最容易撞上它。但反过来说，demo 自己在汇总**之后**才打的 `[零覆盖]` 警告行也在窗口里（我在真实 `Player.log` 尾部看到它紧跟在失败汇总后面），**往往能活下来**。所以「判绿」是可能的，但不是必然，也不像吐槽板暗示的那样「几乎必定」。
- **不升 🔴 的理由（三条，都成立）**：
  1. 它**不伪造执行结论**。RBS 的顶层 `success` 是真实的执行结果（见 §7），「动作跑没跑」由它回答；本条破坏的是**日志完整性**，不是执行真伪。
  2. **已有可用的绕过**，且吐槽板自己标了「待绕过」——直接读 `Player.log`。`Run All Tests` 几百行的量级，`Player.log` 里全都在。
  3. **本仓库的修复只能「贴标签」，不能「把数据还回来」**。修完之后调用方拿到的仍然是 50 条，只是知道了「这只是尾部、前面还有被丢的」。所以它降低的是**误判概率**，不是**信息缺失量**。

---

## 3. 责任归属

**RimBridgeServer（第三方模组）主责，本项目次责（只承担「可观测性」那一半）。**

- **RBS 主责**：`DescribeEffects` 的 `50` 是硬编码字面量、`logCount = readOnlyList.Count` 让计数饱和在 50、返回体不带任何 `logsTotal`/`truncated`、`stateBefore` 不带 `logSequence`。四条全部发生在 `RimBridgeServer.dll` / `RimBridgeServer.Core.dll` 内，**本仓库一行都碰不到**。本仓库的 C# 侧（`Source\`、`McpRimDebug\`）对 `effects`/`logCount` **零命中**，可以确定不是自己产的。
- **本项目次责**：MCP 把这份「已经被削过」的载荷**原样透传并盖章**，没有加任何「这是尾部窗口」的提示。这是**包装层的义务没履行**，不是继承来的 bug —— 与 [01] 指出的「无条件盖章」是同一类责任。
- **不是 vanilla RimWorld / UnityExplorer 的锅**：DebugAction 只负责提供 `DebugActionNode`，日志 journal 是 RBS 自己的东西。
- **不是文档问题**：文档没写清只会让更多人踩，不构成「不值得修」的理由。

---

## 4. 修的话怎么修

> 只给方向，完整步骤交给 PLAN。**并且明确哪些做不了** —— 这是本条最容易被计划员写歪的地方。

### 4.1 能做（本仓库，`MCP/`）

1. **加窗口描述对象 —— 并入计划 01 任务 1**（`MCP/executionEvidence.js`）
   在 `assessExecution` 的 `base` 之后追加一个字段，例如
   `logWindow: { returned, cap: 50, truncated, newestSequence, oldestSequence, totalRecoverableVia: 'rimworld/list_logs' }`。

   **三条硬要求：**
   - `cap` 必须是**带出处注释的具名常量**（例：`const RBS_EFFECTS_LOG_CAP = 50;`，注释写明「出处：`RimBridgeServer.RimWorldDebugActions.DescribeEffects` 的 `GetEntries(50, null, before.LogSequence)`，由 workshop DLL `46e27953…:060010CB:M` 反编译确认，MVID `46e27953d7324877a067e7458bf2c0d5`」）。否则 RBS 日后改了这个值，MCP 会开始**无声地撒谎**。
   - **`truncated` 不要用布尔。** 恰好 50 条且**没**被截断的情况无法区分，所以 `truncated: true | false | 'possible'`（或单独一个 `maybeTruncated: boolean` + 一句 note），别让调用方拿它当确定事实。**这是假阳性保护，反向 bug 比原 bug 更糟。**
   - `oldestSequence` / `newestSequence` 从 `logs[0].sequence` / `logs[logs.length-1].sequence` 取（注意**倒序**，`logs[0]` 是最新的）。这两个值是「去补数」的唯一线索。
2. **接线 —— 并入计划 01 任务 2，但实际几乎零改动**
   计划 01 的 `wrapGABPResult`（`MCP/index.js:2689-2694`）已经把整个 `verdict` 挂进 `execution`。**只要任务 1 的 `verdict` 里带上 `logWindow`，任务 2 一行都不用改。** 这是并入成本极低的关键点。
3. **测试 —— 并入计划 01 已建的两个测试文件，不另起文件**
   在 `MCP/test/fixtures/executeDebugActionPayloads.js` 加一个 `SATURATED_WINDOW` 样本（`logs` 长度 50、首元素是汇总行、末元素是 `== [XxxTests] 开始 ==`、若干元素带 `repeatCount`），再在 `executionEvidence.test.js` 加 3~4 条用例：`logs.length===50` ⇒ 窗口标记为 possible；`logs.length<50` ⇒ 不标记；`logs` 缺失/非数组 ⇒ 字段为 `null` 不抛。
4. **文案 —— 并入计划 01 任务 3**（`EXECUTE_DEBUG_ACTION_NOTE` 末尾一句）：`effects.logs 最多只有 50 条（最新 50 条，倒序），执行几百行的动作时开头的逐例行会被丢掉；读 execution.logWindow.truncated，完整日志见 rimworld.list_logs 或 Player.log。`

### 4.2 做不到（必须在计划里写进「明确不做」）

- **吐槽板建议 ② `logWindow{offset, limit}` 参数化 —— 本仓库不可实施。** `DescribeEffects` 里的 `50` 是硬编码字面量，`execute_debug_action` **没有任何分页入参**（`RimBridgeTools.ExecuteDebugAction` 的签名只有 `path / pawnName / pawnId / x / z / thingId`）。要分页只能改 RBS。
- **在 MCP 层自动补全那被挤掉的条目 —— 不建议默认做。** 唯一通道 `rimworld/list_logs` 是**另一个工具、另一个返回结构、全 ring 最多 2000 条倒序、且没有「比窗口更老」的过滤器**。把它做成 `execute_debug_action` 的隐式二次调用，会把一次调用变成两次、可能返回上百万字符，并且**掩盖了「原来只有 50 条」这个事实**。**要做就做成调用方显式发起的第二次调用，文档写清配方**（先拿 `logWindow.oldestSequence`，再 `list_logs{limit:2000, minimumLevel:<最低档>}` 全取后本地裁剪）。
- **拿 `logsTotal` 当权威值 —— 做不到。** 返回体里没有，回包里也没有；任何「真实总数」都得靠二次查询或读 `Player.log`。

---

## 5. 与计划 01 的关系 + 协调结论

**结论：并入计划 01，不单独立计划。**

**为什么并入的代价接近零**（这是与「单立」的核心差别）：

| 维度 | 并入 | 单立 |
|---|---|---|
| 改的文件 | 同一个 `MCP/executionEvidence.js`（计划 01 任务 1 新建）+ 同一个 `MCP/index.js:2689-2694`（任务 2 已经在改） | 新开一个模块 + **再动一次同一个函数** → 两份计划在同一处代码打架 |
| 测试 | 复用计划 01 已建的 `executionEvidence.test.js` / `gabpExecutionVerdict.test.js` / `fixtures/` | 另起三个文件，重复 fixture |
| 审查 | 一处审完 | 两处审同一个 envelope，字段命名极易漂移 |

**具体锚点（供主代理回灌给计划 01 的作者）**：

1. **任务 1**（`MCP/executionEvidence.js` 步骤 3 的实现代码，`base` 对象之后）：加 `RBS_EFFECTS_LOG_CAP` 常量 + `describeLogWindow(effects)`，把结果并进 `base.logWindow`。**`assessExecution` 的三态判定逻辑本身不要动**（本条与「动作跑没跑」无关，不该污染执行凭据判定）。
2. **任务 2**（`MCP/index.js:2689-2694` 的 `wrapGABPResult`）：**不需要新增代码**，只需在任务 2 的步骤 3 说明里补一句「`verdict` 已含 `logWindow`，随 `execution` 一并透出」。若计划 01 作者选择把它写成 `execution.logWindow`，则计划 01 §5 的新增字段清单要补这一项。
3. **任务 1 步骤 5 与任务 2 步骤 6 的「改动清单」**：各补一行「新增 `execution.logWindow`（纯增量，不改 `success` 语义）」，以保持计划 01 的向后兼容承诺成立。
4. **计划 01 §6「明确不做」表**：加一行 ——「支持 `logWindow{offset,limit}` 分页参数」→ 50 是 RBS 硬编码字面量，`execute_debug_action` 无分页入参，本仓库不可实施；补数只能由调用方显式二次调用 `rimworld/list_logs`。
5. **计划 01 §7「已知降级路径」**：加一条 ——「若 RBS 日后改了窗口上限（50 → N），MCP 的 `cap` 常量会失配」；处置办法是文案里把 `cap` 写出来并附「以 `logWindow.cap` 为准」，而不是试图自动探测。

### 5.1 顺带发现的两条事实纠正（建议一并回灌，影响计划 01 的 fixture）

这两条是本条排查中反编译出来的，**不属于 [17] 的范围，但会让计划 01 的判定规则落空**：

1. **真实回包里没有 `operation` 包装、没有 `durationMs`、没有 `ResultWasTruncated`。** `ExecuteDebugActionResponse` 返回的是**顶层** `success / path / node / targetPawn / mapTarget / stateBefore / stateAfter / effects / followUp`。⇒ 计划 01 的 fixture（`plans/2026-10-01-01-debug-action-execution-evidence.md:104-132`）里的 `operation: { Success: … }` 与 `durationMs: 71` **对应不上真实载荷**；`assessExecution` 的 `operation.Success === false` 分支**永不命中**，`remoteDurationMs` **恒为 `null`**。计划 01 同时检查的 `d.success === false` 才是会命中的那条，但它没意识到这一点。

2. ⚠️ **RBS 的顶层 `success` 是一条真实的执行结果，不是「投递成功」。** 反编译显示 `ExecuteDebugActionResponse` 在两次 `CaptureExecution()` 之间**同步 `Invoke`** 了 `node.action` / `node.pawnAction`，抛异常才走 `success = false` + `exceptionType`；调用本身由 `RimBridgeMainThread/Pump()`（`Root_Update_Patch.Postfix` 驱动）派发，主线程卡死时**根本回不出响应**（那会表现为超时，即吐槽板 [04]）。⇒ `data.success === true` 比 `effects.logCount` **强得多**，本该是一档正面凭据；计划 01 现在把「没有日志」一律判 `unverified`，会白白丢掉这条。

   **但我把它标为「待核对」而不是断言**：吐槽板 [01] 观察到的字段名是 `operation.Success` / `durationMs:72`，与本次反编译的形状**不一致**，可能来自旧版 RBS，也可能 [01] 的那次观察记错了字段。**建议计划 01 的作者先拿一次真实回包核对字段名再改判定规则** —— 这是只读核对，不需要调 `execute_debug_action` 以外的东西（但仍需主代理授权，因为要真调一次）。

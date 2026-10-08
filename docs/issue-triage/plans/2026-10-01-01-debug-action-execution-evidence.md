# execute_debug_action 执行凭据（假成功修复）实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 修掉 `docs/mcp-吐槽板.md` 条目 **[01]**——`rimworld.execute_debug_action` 在动作压根没执行时返回 `success:true`，把「没跑」伪装成「跑了」，让自动化得出事实相反的结论。

**架构：** 在 MCP 层新增一个纯函数模块 `MCP/executionEvidence.js`，对 GABP 回包做**三态**执行凭据判定（`reported-success` / `reported-failure` / `unverified`）。`MCP/index.js` 的 `wrapGABPResult` 只在**远端明确报失败**时把 `success` 翻成 `false`；拿不到凭据时 `success` 保持原义（传输层成功），另附 `execution.executed=null` 明说「无法判定」。判定范围只含 `execute_debug_action` 一个短名，其余 GABP 镜像工具的返回体**逐字节不变**。

> **本计划同时归并了吐槽板 [17]（日志窗口）、[18]（UE 日志成对）、[25]（快耗时启发式）三条**。核心一句话：
> **「时间侧信道」不参与任何判定**（[25] 板上那条「短耗时 + 零日志 ⇒ 判未执行」被本计划否决），但 MCP 自己测得的 wall-clock 会**如实透出**成 `execution.transportDurationMs`——阈值比较的责任交给**有历史基线**的调用方，MCP 不发明一个它无法证伪的阈值。

**技术栈：** Node.js v24.13.0（实测版本）、ESM（`MCP/package.json` 的 `"type":"module"`）、Node 内置测试运行器 `node --test`（`MCP/package.json:9` 的 `npm test` 脚本）。

---

## 0. 行号复核（本计划所有行号均已逐条回读，**以本节为准**）

> **复核方法（重要）：** 下表所有行号都用
> `[System.IO.File]::ReadAllLines('<绝对路径>')[<n>-1]` 复核过，**不是**用带行号的文件阅读工具的输出
> （那种输出会随编辑漂移）。执行者若要再核实，用同样方式，不要相信任何「读到的行号」。

裁决报告 `triage/01-execute-debug-action-false-success.md` 的行号全部成立，仅两处需要更精确的引用。**下面这一列才是本计划引用的行号。**

| 对象 | 裁决报告写的 | 我回读到的实际位置 | 结论 |
|---|---|---|---|
| `wrapGABPResult` | `MCP/index.js:2690-2694` | `MCP/index.js:2690-2694` | ✅ 一致 |
| 「盖章」那一行 | `:2757-2764` | 函数 `callGABPTool` 是 **2747-2790**；`if (res && res.ok)` 在 **2758**；盖章的 `return` 在 **2764** | ⚠️ 函数范围要写 2747-2790，判定点是 **2758**，不要笼统引 2764 |
| `ok` 的来源 | `MCP/gabpClient.js:667-684` | **667-684**（`async callTool(name, args, opts = {})`，`return { ok: true, result }` 在 680） | ✅ 一致 |
| JSON-RPC error/result 分流 | `MCP/gabpClient.js:644-648` | **644-648** | ✅ 一致 |
| 镜像名分发尾部 | `MCP/index.js:4332-4352` | **4332-4352** | ✅ 一致 |
| `_slow` 慢档白名单 | `:4334-4335` | **4334-4335** | ✅ 一致 |
| 假死检测 | `:2769-2785` | **2769-2788**（`else { gabpConsecutiveTimeout = 0 }` 收在 2788） | ⚠️ 上界差 3 行，按 2769-2788 引用 |
| GABP 未连接分支 | `:4274-4286` | 未连接分支 **4274-4280**；未知镜像分支 **4281-4286** | ⚠️ 语义一致，行号要拆成两段引用 |
| `TOOL_USAGE_NOTES` 定义 | `:2930` | **2930** | ✅ 一致 |
| T2 慢档白名单 | `longTask/timeoutPolicy.js:18` | **18**（`SLOW_BARE_NAMES`，含 `execute_debug_action`） | ✅ 一致 |
| README 镜像说明 / 工具表 | `README.md:160` / `:172` | **160** / **172** | ✅ 一致 |

另外本计划引用、裁决报告没提的位置（均已用 `ReadAllLines` 回读确认）：

- `MCP/index.js:5450-5459` —— `export const _testHooks`，其中 `setGabpBridge: (b) => { gabpBridge = b; }` 在 **5453**。**这是本计划全部集成测试的入口。**
- `MCP/index.js:5416-5443` —— `export { ... }` 清单，`callGABPTool` 在 **5422**、`mcpHelpResult` 在 **5439**。
- `MCP/index.js:5124-5132` —— `tools/call` 日志行里的 `outcome` 是从返回体的 `parsed.success` 推出来的。这条会让本计划的改动在日志上**可见**（见 §5 向后兼容）。
- `MCP/index.js:3480-3485` —— `DPA_STATE_RESET_TOOLS`（不含 `execute_debug_action`）。
- `MCP/index.js:3501-3510` —— `noteDpaStateAfterCall`，只看 `success === true` 且只在上述集合内生效。
- `MCP/index.js:2666-2670` —— `ensureGabpConnected`，`if (gabpBridge.isConnected()) return { ok: true };` 在 **2670**。
- `MCP/index.js:4334` / `4335` / `4350` / `4352` —— `_slow` 正则、`_opts`、镜像尾部的 `callGABPTool(_rbs, args, undefined, _opts)` 与 `return _r`。
- `MCP/index.js:2411` —— 既有文案里 `path="Actions\\Hot reload Defs"` 的写法（对照任务 3 里 `example` 的转义，**这两处转义层数不同，别抄错**）。
- `MCP/index.js:2514` / `2644` —— `gabpTools` 的声明与并入时机（解释任务 3 为什么不能用 `mcp_help` 做行为断言）。
- `MCP/index.js:3670` —— `mcpHelpResult` 里 `JSON.stringify(detail, null, 2)`，`example` 会被它**再转义一次**。
- `MCP/gabpClient.js:59` —— `const MAX_BUFFER_BYTES = 16 * 1024 * 1024;`，**接收缓冲上限，不是条数上限**（吐槽板 [17] 的「日志被截断」与它无关，见 §0.2）。
- `MCP/index.js:4350` 传 `filterResult = undefined`、`:2761-2762` 的 `typeof filterResult === 'function'` 分支**不进** ⇒ MCP 层与 GABP 层都没有任何二次截断，[17] 的截断**只发生在 RBS 侧**。
- `Source/UELoader/ExplorerBootstrap.cs:195-218` —— `OnUELog`，其中 **204 / 207 / 210** 把每条 UE 日志用 `[UnityExplorer] ` 前缀写回 `Player.log`。这是吐槽板 [18] 的**真实成因**（详见 §0.2 第 4 条）。

### 0.1 已知的两处「事实纠正」

1. **本目录不是 git 仓库。** `git -C <项目根> status` 返回 `fatal: not a git repository (or any of the parent directories): .git`。所以 writing-plans 模板里的 `Commit` 步骤在本项目**不适用**——本计划把每个任务的收尾步骤换成「列出本次改动文件清单并回报」，不做任何提交动作。
2. **吐槽板 [01] 建议里的「让 `stateBefore/stateAfter` 的 `longEventPending` 与是否排队挂钩」在本项目不可实施。** 「是否入队」发生在 RimBridgeServer（RBS）内部，MCP 层只是转发方，读得到 `longEventPending` 却无法让它与入队行为产生因果；RBS 的源码不在本仓库内（裁决报告 §5 已核实）。**可实施的是它的只读版本**：读 `stateBefore.longEventPending` 并写进 `execution.mainThreadPendingBefore` + 强化 `note` 文案，**绝不**据此把 `success` 判成 `false`。理由见任务 1 步骤 3 的注释。

### 0.2 RBS 回包的**真实形状**来自反编译 —— 但有两条**待核对**（第 3 轮 triage 补充）

RBS 的源码不在本仓库，但第 3 轮 triage 反编译了**运行时**的 RimBridgeServer（创意工坊 `content/294100/3727949765`，在运行时路径上重跑并逐字核对了 hash；它与开发副本 MVID 不同）。以下三条**可以当作事实引用**：

1. **`RimBridgeServer.RimWorldDebugActions.ExecuteDebugActionResponse` 的字段是顶层的**：
   `success / path / node / targetPawn / mapTarget / stateBefore / stateAfter / effects / followUp`。
2. **RBS 在两次 `CaptureExecution()` 之间同步 Invoke `node.action`**，抛异常才 `success:false` + `exceptionType`；调用由 `RimBridgeMainThread/Pump()`（`Root_Update_Patch.Postfix`）派发，主线程卡死时**根本回不出响应**（表现为超时，即吐槽板 [04]，不是 [01]）。
3. **`effects.logs` 只给最后 50 条且不告知是否截断**：`DescribeEffects` → `LogJournal.GetEntries(50,…)` → `source.Take(limit)`，`logCount = list.Count` 因此**饱和在 50**（吐槽板 **[17]**）。

**⚠️ 下面两条没有真实回包支撑，只有反编译，所以是「待核对」，不得当作断言写进任何文案或注释：**

- **待核对 A：`success === true` 能不能当正面凭据。** 若第 2 条成立，`data.success === true` 就是一档**很强**的正面凭据（同步 Invoke 没抛异常），比 `effects.logCount` 强得多——毕竟很多调试动作本来就不产日志。
  **本计划的处理：不赌，但也不浪费。** 默认 `PROMOTE_REMOTE_SUCCESS = false`（`success === true` 单独出现时仍判 `unverified`），同时把它**如实透传**成 `execution.remoteSuccess` + `evidence` 说明，让调用方拿得到这条信息。核对到真实回包后，翻一个常量即可升级（步骤见 **§7.1**，改动面：一个常量 + 一条 fixture + 一条用例，`MCP/index.js` 与 `README.md` 零改动）。
- **待核对 B：吐槽板 [01] 里记的 `operation.Success` 与 `durationMs` 是否真实存在。** 按第 1 条，反编译出的顶层字段里**没有** `operation`，也没有 `durationMs`。
  ⇒ **一种很可能的情况是：当初记录 [01] 的人把 MCP 信封自己的 `success:true` 误读成了嵌套的 `operation.Success`。** 本计划因此**不再把 `operation.Success` 当主判据**（降级为「若真的存在也认」的兼容分支），改以**顶层 `d.success`** 为准。
  ⇒ ⚠️ **`durationMs` 这一半现在有了更强的解释**（我已独立复核，见 §0.3 第 3 条）：`MCP/index.js:5077` 取 `startMs`、`:5123` 算 `elapsed`、`:5132` 把它写进 `log('INFO', 'tools/call … → ${elapsed}ms ${outcome}')`，此后 `:5134-5135` 直接 `return result`——**`elapsed` 此后一次也没被读过**。⇒ 板上那个 `durationMs: 72` **极可能是把 MCP 自己的日志行 `→ 72ms success` 读成了回包字段**。这与 `operation.Success` 的误读**同型**。
  ⇒ **本计划的取舍：`d.success === false` 升为第一判据**（反编译里它是 RBS 同步 Invoke 抛异常的唯一出口，可信度最高），`operation.Success === false` 保留为兼容分支，`durationMs` 保留为**尽力而为的旁证**（取不到就是 `null`，**且按上面的推理它很可能恒为 `null`**，不参与判定）。
  ⇒ **本计划自带的 fixture 因此分两类**（任务 1 步骤 1）：一类按反编译形状（**以顶层 `success` 为准**），一类保留吐槽板原样形状（证明旧形状也能吃）。**B 组现在只能算「历史记录形状」，不是「现场观测」**——它记录的两个字段都可能根本不在回包里。**执行者落地后必须用一次真实回包替换/校准 fixture**——步骤见 §7.1。

**吐槽板 [18]**（`effects.logs[]` 里 UE 回显成对出现）**裁决为不值得修、代码零改动**，只在本计划任务 3 的文案里补一句。两条配套事实：
- **板子 [18] 给的因果解释已被证伪**：`effects.logs[]` 并**没有**把 UE 回显去重——`ShouldCollapse` 要求 `Message` **逐字相等**，而两条副本是 `[UnityExplorer] [Unity] X…` vs `X…`，根本不是同一个字符串，永不折叠。真实成因是**本项目自己的** `Source/UELoader/ExplorerBootstrap.cs:204/207/210`（`OnUELog` 把每条 UE 日志用 `[UnityExplorer] ` 前缀写回 `Player.log`）。**这不是 RBS 的锅。**
- **板子 [18]「两处数字必然不同（18 vs 36）」这个前提本身存疑**——按上面的成因，两边本应接近。子代理**没有实测**（需要起游戏）。**所以文案里不得把「两处必然不同」写成定论。**

**另外两条已核实的顺带事实（可引用，别搞反）**：
- `effects.logs[]` 的 **`repeatCount` 字段确实存在**，但语义是 `LogJournal.Record` + `ShouldCollapse` 的「连续 2 秒内、11 个字段全等」的**折叠计数**，**不是** UE 回显标记。**不许拿它解释 [18]。**
- 吐槽板 [01] 里「`logs[0]` 是最后一条」**是准的**——RBS 的 `_entries` 用 `Insert(0,…)`，即**倒序**。任务 1 的 `describeLogWindow` 会把这个顺序一并回报。

### 0.3 吐槽板 [25] 归并进本计划（triage/25-fast-nolog-suspicion-heuristic.md）

**先说三条事实，它们决定了 [25] 在本计划里只占很小的位置：**

1. **`## [25]` 在板上没有正文。** 我用 `[System.IO.File]::ReadAllLines` 复核 `docs/mcp-吐槽板.md`（**1459 行**、**38 条** `#` 开头标题）：`[25]` 全文只出现 **2 次**——`mcp-吐槽板.md:42`（索引行本体）与 `mcp-吐槽板.md:151`（[26] 批注里的一句同族引用）。**没有 `## [25]` 段。** ⇒ **不要把 [25] 当独立观测源。**
   ⚠️ **吐槽板是主代理持续批注的文件，行号会漂。** 本节所有吐槽板行号是**改稿时点**的快照（该文件当时 1459 行）；实现者若要引用，请自己重跑一次 `ReadAllLines` 并以**内容**（不是行号）为准。
2. **[25] 的「72ms + 空 logs」与 [01] 正文是同一次事件。** 索引行 `:42` 自称「[01] 的加强版」，其内容逐字对应 [01] 正文 `mcp-吐槽板.md:1066-1075`（`success:true` / `logCount:0` / `durationMs:72` / `OnGUI` 异常循环打挂）。⇒ **[25] 没有带来第二个样本，它唯一的净增量是一条推断规则。**
3. **「耗时」这个信号对调用方目前根本不可见** —— 已独立复核：`MCP/index.js:5077` / `:5123` / `:5124-5135`（见上面的待核对 B）。⇒ **板上那个「72ms」极可能本来就是从日志行读的**，不是回包字段。这条**完全在本仓库内、修起来不需要碰第三方**。

**[25] 那条推断规则（「耗时异常短 + 日志为零 ⇒ 直接判为未执行」）本计划明确否决**，理由从「裁决报告 §修法 3 已定」升级为可复核的实测证据（triage/25 §3.1，数据来自 `MCP/logs/mcp-20261001-093829.log` 该文件全部 1361 行中的 87 条 `execute_debug_action`）：

- `Actions\Hot Query Guard Tests` 6 次耗时 **37 / 39 / 43 / 51 / 60 / 72 ms** —— **板上那条「72ms」阈值会把这 6 次全部误伤**；`72` 这个数字在**现场档和正常档里同时存在**，单看耗时根本分不开。
- 87 次里 **14 次 < 133ms，分属 7 个不同 action**。阈值取 <150ms 会同时误伤 7 类。
- `Actions\Stage Test Map` n=36 却跨 **3 → 60013ms** ⇒ **「正常要 2097ms」只是单点、不是基线**，任何 per-path 阈值都不稳。
- **日志里没有 ground truth**：`MCP\logs\*.log`（全部 5 个文件）grep 字面量 `logCount` **零命中**，且所有调用的 outcome 都写着那个**已知不可信**的 `success`。⇒ 拿 2097ms 这一个点去拟合阈值就是过拟合。

**[25] 真正有价值的净增量是一个缺口（见 §0.4），不是它自己那条判据。** 本计划对 [25] 的全部动作只有三条：
1. 采纳 **`execution.fastNoLog: true | false | 'possible' | null`** 三态字段（`true` 永不可达，与 `truncated` 同纪律），**MCP 不内置阈值**（本仓库没有 per-path 历史基线表，见 §6）；
2. 新增 **`execution.transportDurationMs`**——MCP 自己测的 wall-clock，**只透传、不判定**（上面第 3 条事实的直接补丁）；
3. 修补 §0.4 那个缺口。

### 0.4 ⚠️ 本计划原来选错了「主线程被卡」的代理（[25] triage 挖出）

**原方案**：`execution.mainThreadPendingBefore`（读 `stateBefore.longEventPending`）当「主线程被卡」的**唯一**代理，未执行 fixture 也据此写成 `longEventPending: true`。

**问题**：[01]/[25] 的现场是 `ColonistBarColonistDrawer.DrawIcons` 的 NRE 引发 **`OnGUI` 异常循环自旋**（`mcp-吐槽板.md:1074-1075`：主线程 ≈170% CPU、`Player.log` 6 分钟零写入）。**自旋 ≠ 长事件排队**——游戏卡在 `OnGUI` 死循环里**根本不 tick**，长事件队列很可能是**空的** ⇒ 在这个恰恰最该报案的场景里 `mainThreadPendingBefore` 很可能为 `false`，那条 note 不会触发。

**⚠️ 这是推断，不是事实**：RBS 源码不在本仓库，`longEventPending` 在自旋场景下的真值**无法核实**，必须按 §7.1 步骤 A 实测。

**本计划的修补（三条同时做，缺一不可）：**

1. **note 改成纯追加式**（任务 1 步骤 3）：`longEventPending` **不再决定 note 的内容分支**，只在为 `true` 时**追加**一句。⇒ 代理猜错时，**只会少一句提示，不会少掉任何已定的结论**——`status`/`executed` 本来就与它无关。
2. **note 里明写反向提醒**：`mainThreadPendingBefore=false` **不代表主线程健康**（`OnGUI` 自旋下长事件队列可能是空的）。避免调用方把 `false` 读成「一切正常」。
3. **fixture 的 `longEventPending: true` 标为「假设值，非现场观测」**，并在 fixture 注释里绑定到 §7.1 步骤 C（校准时必须用真实回包替换）。

### 0.5 吐槽板 [33]（`add_using_directive` 静默假成功）—— **评估结论：不纳入代码，只纳入一行文案**

`[33]` 的 triage 挖出：`add_using_directive` 可以在编译失败的情况下返回成功（游戏侧 `ConsoleController.AddUsing` 先 `usingDirectives.Add` 再编译、失败不回滚）。这**形状上**和 [01] 同族（报告成功、实际没发生），但——

**⚠️ 它走的不是同一条路径，本计划的机制覆盖不到它。** 证据（全部 `ReadAllLines` 复核）：

| | `execute_debug_action`（本计划对象） | `add_using_directive`（[33]） |
|---|---|---|
| 派发位置 | `MCP/index.js:4332-4352`（`isGabpMirrorName` 分支尾部） | `MCP/index.js:4555-4564`（`switch (name)` 的一个 case） |
| 传输层 | GABP / JSON-RPC → `callGABPTool`（`MCP/index.js:2747-2790`） | 游戏内 HTTP → `callUnityExplorerAPI`（**定义在 `MCP/index.js:736`**） |
| 信封产出 | `wrapGABPResult`（`MCP/index.js:2690-2694`） | `callUnityExplorerAPI` 自己拼 `{ success, data, error, errorCode, statusCode }`（**`MCP/index.js:805-811`**） |
| 判据来源 | RBS 回包的 `success` / `operation.Success` / `effects.logCount` | UE HTTP 响应体，字段名与 RBS 完全不同 |

**三条「不纳入」的具体理由（不是「不在范围内」）：**

1. **UE 路径其实【已经】做了收紧**，问题不在 MCP 层。`MCP/index.js:806` 是 `success: response.ok && data.success`——只要游戏侧 `/unityexplorer/console/addusing` 在编译失败时回 `success:false`，MCP 就会如实报失败。所以这不是「MCP 盖了章」，而是**游戏侧自己报了 `success:true`**。⇒ 改 MCP 层收不到任何新信息。
2. **本计划没有可用的判据可加。** `assessExecution(toolName, data, …)` 读的是 RBS 形状的字段；而按 [33] 的 triage，失败细节会被游戏侧 `Evaluate` 就地吞掉 `FormatException`（`Source\UE-4.9.0-official\UnityExplorer-4.9.0\src\CSConsole\` 的 `:202-212`），**很可能压根不会以结构化字段回到 MCP**。**没有信号就没有判据**——这正是本计划 §1.1 反复守的纪律（只按有据的字段判）。
3. **真正的根因在 vendored 的 UnityExplorer 源码里**，既不在 MCP 也不在 RBS。我已独立复核该文件（本仓库内，`Source\UE-4.9.0-official\UnityExplorer-4.9.0\src\CSConsole\ConsoleController.cs`，共 747 行）：

   ```csharp
   // ConsoleController.cs:141-148
   public static void AddUsing(string assemblyName)
   {
       if (!usingDirectives.Contains(assemblyName))
       {
           Evaluate($"using {assemblyName};", true);   // :145  第二个参数 supressLog=true
           usingDirectives.Add(assemblyName);          // :146  无论成功失败都加
       }
   }
   ```

   而 `Evaluate`（`ConsoleController.cs:202-212`）在 `:208-212` 就地 `catch (FormatException fex)`，且因为 `supressLog` 为真，`:210-211` 那句 `LogWarning` **也被跳过** ⇒ **失败连警告都不留**。⇒ 修法是让 `AddUsing` 在编译成功后才 `Add`（或失败时回滚并让 `/addusing` 回 `success:false`）；**那样 `MCP/index.js:806` 的既有检查就会自动生效，MCP 侧零改动。**

**⇒ 本计划对 [33] 的唯一动作**：在任务 3 的 `TOOL_USAGE_NOTES` 里加一条 `add_using_directive` 的**易错提醒**（告诉调用方「返回 success 不等于这个 using 真的加上了，请用一次裸类型名的 `execute_csharp_code` 验一把」），并把修法指向游戏侧文件。**不新增任何判据代码。**

> ⚠️ 引用 [33] 的一条**未实测**结论时必须照抄它的限定语：「重复程序集引用导致 CS0121 二义」是 triage 标注的**唯一说得通的假设、运行时未核实**（核实需跑 AppDomain 枚举，而那被本流水线禁止）。同一条 triage 确证、我也已逐行复核的**静态**事实是：`ScriptEvaluator.cs:81-98`（`ImportAppdomainAssemblies`）把 AppDomain 里**每个**程序集都喂给 mcs，`:35-43`（`OnAssemblyLoad`）→ `:45-62`（`Reference`）**只按简单名做黑名单**（`Config\ConfigManager.cs:131` 的 `CSConsole_Assembly_Blacklist` 默认空）、**无去重键**。**静态事实可以引用，运行时假设不可以。**

---

## 1. 背景：为什么是「假成功」，以及为什么不能简单改成 fail-closed

`MCP/index.js:2758-2764` 的链路是这样的：

```
agg_call_tool{tool:"rimworld.execute_debug_action"}
  → handleToolCall → isGabpMirrorName 分支 (index.js:4332-4352)
  → callGABPTool('rimworld/execute_debug_action', args)
  → gabpBridge.callTool(...)          ← gabpClient.js:667-684，ok=true 只代表 JSON-RPC 没报错
  → wrapGABPResult(data, true)        ← index.js:2690-2694，无条件 { success:true, data }
```

`ok` 的唯一来源是 `gabpClient.js:644-648`：`if (msg.error) reject(...) else entry.resolve(msg.result)`。**RBS 自己回包里的 `success` / `effects.logCount` 一个都没有参与判定**——它们只是被原样塞进 `data`。所以「动作跑了」和「动作压根没跑」在返回值上完全同形。

**为什么不能把 `success` 直接改成「执行成功」：** 本仓库拿不到**已核实**的正面执行凭据（§0.2 待核对 A：反编译说 RBS 顶层 `success` 是同步 Invoke 的结果，但没有真实回包验证过）。若在没有凭据时一律 `success:false`，会把**每一次真成功的调用**都判成失败——那是这条问题的**反面**（假阴性），比假成功更糟，因为它会打断所有现有调试流程。因此本计划选的路是：

- 有据报失败 → `success:false`（有据可依，不是推断）；
- 有正面凭据 → `success:true` + `execution.executed:true`；
- 没凭据 → `success` **保持原义（传输成功）**，但同时给出 `execution.status:'unverified'` + `execution.executed:null` + 一句写明「不要读成已执行」的 `note`。

**净效果：返回值不再「宣称」动作执行过。** 调用方想读 `success` 仍然能读（向后兼容，见 §5），但返回值里不再有任何一处替 RBS 打包票。

### 1.1 判据的优先级（实现时照这个顺序，一行都不要改）

| 序 | 判据 | 结论 | 依据强度 |
|---|---|---|---|
| 1 | 顶层 `data.success === false` | `reported-failure` / `executed:false` | **最强**（反编译：RBS 同步 Invoke 抛异常的唯一出口） |
| 2 | `data.operation.Success === false`（若该字段真存在） | `reported-failure` / `executed:false` | 兼容分支；**反编译的顶层字段表里没有它**（§0.2 待核对 B） |
| 3 | `data.executed === true` | `reported-success` / `executed:true` | 强（RBS 若提供显式凭据） |
| 4 | `effects.logCount > 0` | `reported-success` / `executed:true` | 中（执行窗口里真的抓到了日志） |
| 5 | 顶层 `data.success === true` | **仍判 `unverified`**（`PROMOTE_REMOTE_SUCCESS` **写死 false**，见 §7.1 步骤 B） | 疑似很强，**待核对 A**；[01]/[25] 的现场（动作没跑、顶层 `success:true`）**已经构成反例**。只如实透传成 `execution.remoteSuccess` |
| — | `effects.logCount === 0` | **不判失败**，只强化 note | 调试动作本来就不产日志 |
| — | `data.durationMs` | **不参与判定**，只透传 `remoteDurationMs` | 反编译的顶层字段表里没有它（§0.2 待核对 B）；**且按 `:5123` 的推理它很可能恒为 `null`** |
| — | **任何耗时**（回包 `durationMs` 或 MCP 自己测的 `transportDurationMs`） | **一律不参与判定** | 性能侧信道。见 §0.3 的实测分布：`Hot Query Guard Tests` 6 次 37–72ms 全是正常档，板上那条 72ms 阈值会成片误伤；`Stage Test Map` n=36 跨 3→60013ms，**没有可用的基线**。⚠️ 另注：`MCP/index.js:5123` 那个 `elapsed` **只进日志、从不进返回值**（`:5132` 写日志、`:5134-5135` 直接 return），所以板上「72ms」极可能是从日志行读的，不是回包字段 |

**为什么第 5 条不提升为正面凭据 —— 而且现在不是「不赌」，是「已经有反例」**：
按 [01]/[25] 的现场（顶层 `success:true`、动作**没跑**、72ms 秒回），`success === true` 这条判据**已经被 [01] 本身证伪过一次**。§7.1 步骤 B 给出的是「要改判需要什么**新**证据」——一条能推翻上述反例的实测样本。在拿到它之前，`PROMOTE_REMOTE_SUCCESS` **保持 `false`**。

**为什么耗时连「辅助提示」都不算**：`execution.fastNoLog` 存在（见 §0.3、§6.2），但 MCP **不内置阈值**（本仓库没有 per-path 历史基线表），所以它实际上**只能取到 `false` 或 `null`**——拿不到耗时就是 `null`，有正面凭据就是 `false`。这是刻意的：把阈值比较留给有基线的调用方。

**净效果（不变）：返回值不再「宣称」动作执行过。**

---

## 2. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/executionEvidence.js` | **新建** | 纯函数模块，两个职责且各自独立可测：①把一次 GABP 回包翻译成执行凭据三态；②描述 `effects.logs` 的可见窗口（吐槽板 [17] 的 50 条截断）。无 I/O、无外部导入。 |
| `MCP/index.js` | **修改**（只动 3 处：`25-32` 附近加 import、`2690-2694`、`2758-2764`） | 接线：把判定结果塞进返回信封。**吐槽板 [17] 在这里零改动**——`wrapGABPResult` 已经把整个 verdict 原样挂进 `execution`。 |
| `MCP/test/fixtures/executeDebugActionPayloads.js` | **新建** | 5 个 GABP 回包样本（纯数据），两个测试文件共用。放在 `test/fixtures/` 下，`npm test` 的 glob `test/*.test.js` 不会把它当测试跑。 |
| `MCP/test/executionEvidence.test.js` | **新建** | 纯函数单测：执行凭据判定 + 日志窗口描述（含三态 `truncated` 的全部边界）。 |
| `MCP/test/gabpExecutionVerdict.test.js` | **新建** | 端到端（无游戏、无网络）：假 GABP 桥接 + `callGABPTool`，验返回信封。任务 3 追加文案守卫用例。 |
| `README.md` | **修改**（第 172 行那一行） | 工具表补一句 `success` 的真实含义 + 日志窗口截断提醒。 |

**不放进本计划的文件**：`Source/`、`McpRimDebug/`、RimBridgeServer（第三方，不在本仓库）、`docs/mcp-吐槽板.md`（主代理改）。
**本计划明确零改动的源文件**：`MCP/index.js` 的除上述 3 处以外的任何行；`Source/UELoader/ExplorerBootstrap.cs`（吐槽板 [18] 的真实成因在它那里，但裁决为不值得修，见 §0.2 与 §6）。

---

## 3. 任务

### 任务 1：执行凭据纯函数 `MCP/executionEvidence.js`

**文件：**
- 创建：`MCP/executionEvidence.js`
- 创建：`MCP/test/fixtures/executeDebugActionPayloads.js`
- 测试：`MCP/test/executionEvidence.test.js`

- [ ] **步骤 1：先写失败测试（连同 fixture）**

新建 `MCP/test/fixtures/executeDebugActionPayloads.js`：

```javascript
// execute_debug_action 的 GABP 回包样本。
// 纯数据：不需要游戏、不需要 RimBridgeServer、不需要网络。
//
// ⚠️⚠️ 这些**全部是构造出来的样本，不是现场抓包**。没有任何一个样本来自真实回包 ——
// 原因见 §7.1：RBS 回包的真实形状至今未用实测核对过。实现者落地后**必须**用一次真实
// 回包替换 A 组三个样本（步骤 C），否则整套用例只是在验证「我们对猜测的处理是自洽的」。
//
// ⚠️ 形状来源分两层：
//   A 组（*_RBS_SHAPE）= 按第 3 轮 triage 对**运行时** RBS 的反编译写的：
//      RimBridgeServer.RimWorldDebugActions.ExecuteDebugActionResponse 顶层字段为
//      success / path / node / targetPawn / mapTarget / stateBefore / stateAfter / effects / followUp。
//      **没有 operation，也没有 durationMs** —— 所以 A 组里刻意不放这两个字段。
//   B 组（BOARD_SHAPE）= docs/mcp-吐槽板.md [01]（2026-10-01）原样记下的形状。
//      ⚠️ **B 组现在只能算「历史记录形状」，不是「现场观测」**：它记的 operation.Success 可能是
//      把 MCP 信封的 success:true 误读了；它记的 durationMs: 71 / 72 可能是把 MCP 自己的日志行
//      `→ 71ms success`（MCP/index.js:5132 写的）误读成了回包字段（见 §0.2 待核对 B、§0.3 第 3 条）。
//      保留 B 组只为证明：**即使这两个字段根本不存在，本模块也不会崩、也不会误判**。
// 取值（logCount=0 / durationMs=71）来自 [01] 与 MCP/logs/mcp-20261001-093829.log 第 296 行那条 71ms 的调用。

// —— A 组：按反编译的顶层字段形状 ——

// [01] 的现场（反编译形状）：JSON-RPC 没报错（MCP 在 index.js:2690-2694 盖了 success:true），
// 但动作压根没跑。RBS 顶层 success 仍是 true —— 这正是原 bug 的形状。
//
// ⚠️⚠️ stateBefore/stateAfter 里的 longEventPending:true 是【假设值，不是现场观测】——
//    [01]/[25] 的现场是 OnGUI 异常循环自旋（mcp-吐槽板.md [01] 复现段），**自旋 ≠ 长事件排队**：
//    游戏卡在 OnGUI 里根本不 tick，长事件队列很可能是空的 ⇒ 真实回包里这里很可能是 false
//    甚至没有该字段。RBS 源码不在仓库，无法核实（见 §0.4）。
//    本模块的设计保证：猜错时【只少一句提示，不少任何结论】——status/executed 与它无关。
//    §7.1 步骤 A 必须实测这个字段；步骤 C 校准 fixture 时按实测值改。
export const NOT_EXECUTED_RBS_SHAPE = {
  success: true,
  path: 'Actions\\Inject Cell Cost Tests',
  node: { label: 'Inject Cell Cost Tests', path: 'Actions\\Inject Cell Cost Tests', enabled: true, children: [] },
  targetPawn: null,
  mapTarget: null,
  stateBefore: { longEventPending: true, ticksGame: 1234567 },
  stateAfter: { longEventPending: true, ticksGame: 1234567 },
  effects: { logCount: 0, logs: [] },
  followUp: null,
};

// 同一条动作真跑了一次（反编译形状）：执行窗口内抓到日志
export const EXECUTED_RBS_SHAPE = {
  success: true,
  path: 'Actions\\Inject Cell Cost Tests',
  node: { label: 'Inject Cell Cost Tests', path: 'Actions\\Inject Cell Cost Tests', enabled: true, children: [] },
  targetPawn: null,
  mapTarget: null,
  stateBefore: { longEventPending: false, ticksGame: 1234000 },
  stateAfter: { longEventPending: false, ticksGame: 1234012 },
  effects: { logCount: 37, logs: [{ level: 'Info', message: 'Injecting cell costs...' }] },
  followUp: null,
};

// RBS 明确报失败（反编译形状）：同步 Invoke 抛异常 → 顶层 success:false + exceptionType
export const REMOTE_FAILED_RBS_SHAPE = {
  success: false,
  exceptionType: 'System.InvalidOperationException',
  exceptionMessage: 'Debug action not found',
  path: 'Actions\\Inject Cell Cost Tests',
  node: { label: 'Inject Cell Cost Tests', path: 'Actions\\Inject Cell Cost Tests', enabled: false, children: [] },
  targetPawn: null,
  mapTarget: null,
  stateBefore: { longEventPending: false, ticksGame: 1234000 },
  stateAfter: { longEventPending: false, ticksGame: 1234000 },
  effects: { logCount: 0, logs: [] },
  followUp: null,
};

// —— B 组：吐槽板 [01] 原样形状（含 operation / durationMs）——
// 只用来验证「老形状也能吃」，不是判据来源。

export const NOT_EXECUTED_BOARD_SHAPE = {
  node: { label: 'Inject Cell Cost Tests', path: 'Actions\\Inject Cell Cost Tests', enabled: true, children: [] },
  operation: { Success: true, OperationId: 'op-20261001-141504' },
  effects: { logCount: 0, logs: [] },
  durationMs: 71,
  stateBefore: { longEventPending: true, ticksGame: 1234567 },
  stateAfter: { longEventPending: true, ticksGame: 1234567 },
};

// 老形状下的远端失败：operation.Success === false
export const REMOTE_FAILED_BOARD_SHAPE = {
  node: { label: 'Inject Cell Cost Tests', path: 'Actions\\Inject Cell Cost Tests', enabled: false, children: [] },
  operation: { Success: false, Message: 'Debug action not found' },
  effects: { logCount: 0, logs: [] },
  durationMs: 3,
  stateBefore: { longEventPending: false, ticksGame: 1234000 },
  stateAfter: { longEventPending: false, ticksGame: 1234000 },
};

// —— 吐槽板 [17]：effects.logs 的 50 条硬上限（在 RBS 侧，MCP 层无解）——
// 反编译：DescribeEffects → LogJournal.GetEntries(50,…) → source.Take(limit)，logCount = list.Count 故饱和在 50。
// returnedCount < cap → 肯定没截断；=== cap → 【无法区分】「恰好 50 条」与「被截断」，只能报 'possible'。
export const LOGS_EXACTLY_AT_CAP = {
  success: true,
  effects: {
    logCount: 50,
    logs: Array.from({ length: 50 }, (_, i) => ({ sequence: 1000 - i, level: 'Info', message: `line ${i}` })),
  },
};

export const LOGS_BELOW_CAP = {
  success: true,
  effects: { logCount: 3, logs: [{ sequence: 1000, level: 'Info', message: 'a' }] },
};
```

新建 `MCP/test/executionEvidence.test.js`：

```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  assessExecution, describeLogWindow,
  EXECUTION_ERROR_CODE, EXECUTION_VERDICT_TOOLS, PROMOTE_REMOTE_SUCCESS, RBS_EFFECTS_LOG_CAP,
  FAST_NO_LOG_THRESHOLD_MS,
} from '../executionEvidence.js';
import {
  NOT_EXECUTED_RBS_SHAPE, EXECUTED_RBS_SHAPE, REMOTE_FAILED_RBS_SHAPE,
  NOT_EXECUTED_BOARD_SHAPE, REMOTE_FAILED_BOARD_SHAPE,
  LOGS_EXACTLY_AT_CAP, LOGS_BELOW_CAP,
} from './fixtures/executeDebugActionPayloads.js';

// ============ 吐槽板 [01]：执行凭据三态 ============

test('[01] 传输成功但动作没跑 → unverified / executed=null，绝不判成功', () => {
  const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE);
  assert.equal(v.status, 'unverified');
  assert.equal(v.executed, null, 'executed 必须是三态的 null，不是 true 也不是 false');
  assert.equal(v.evidence, null);
  assert.match(v.note, /不要读成「已执行」/);
  assert.match(v.note, /logCount=0/);
});

test('[01] 顶层 success===true 单独出现时不判成功（默认不提升，等真实回包核对）', () => {
  assert.equal(PROMOTE_REMOTE_SUCCESS, false, '默认必须是不提升——提升需要真实回包背书');
  assert.equal(NOT_EXECUTED_RBS_SHAPE.success, true, '样本必须保持顶层 success=true');
  const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE);
  assert.notEqual(v.executed, true);
  assert.equal(v.remoteSuccess, true, '但要如实透传，不能把这条证据扔掉');
});

test('[01] 老形状（operation.Success=true + durationMs）也不判成功、不崩', () => {
  const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_BOARD_SHAPE);
  assert.equal(v.status, 'unverified');
  assert.equal(v.executed, null);
  assert.equal(v.remoteSuccess, null, '老形状里没有顶层 success，必须是 null 而不是 false');
  assert.equal(v.remoteDurationMs, 71, '老形状有 durationMs 就如实透传');
});

test('抓到日志 → reported-success / executed=true，判据是 effects.logCount', () => {
  const v = assessExecution('rimworld/execute_debug_action', EXECUTED_RBS_SHAPE);
  assert.equal(v.status, 'reported-success');
  assert.equal(v.executed, true);
  assert.equal(v.evidence, 'effects.logCount');
});

test('远端顶层 success===false → reported-failure / executed=false（第一判据）', () => {
  const v = assessExecution('rimworld/execute_debug_action', REMOTE_FAILED_RBS_SHAPE);
  assert.equal(v.status, 'reported-failure');
  assert.equal(v.executed, false);
  assert.equal(v.evidence, 'success');
  assert.equal(v.remoteSuccess, false);
});

test('老形状 operation.Success===false 也能命中（兼容分支，evidence 标明来源）', () => {
  const v = assessExecution('rimworld/execute_debug_action', REMOTE_FAILED_BOARD_SHAPE);
  assert.equal(v.status, 'reported-failure');
  assert.equal(v.evidence, 'operation.Success');
});

test('顶层 success 优先于 operation.Success（两个都 false 时 evidence 必须是 success）', () => {
  const v = assessExecution('rimworld/execute_debug_action',
    { success: false, operation: { Success: false } });
  assert.equal(v.evidence, 'success');
});

test('反例保护：非判定范围的工具一律返回 null（调用方据此保持原返回体不变）', () => {
  assert.equal(assessExecution('rimworld/list_debug_action_children', NOT_EXECUTED_RBS_SHAPE), null);
  assert.equal(assessExecution('rimworld/create_allowed_area', EXECUTED_RBS_SHAPE), null);
  assert.deepEqual([...EXECUTION_VERDICT_TOOLS], ['execute_debug_action']);
});

test('工具名两种分隔符都认（/ 与 .）', () => {
  assert.equal(assessExecution('rimworld.execute_debug_action', EXECUTED_RBS_SHAPE).status, 'reported-success');
  assert.equal(assessExecution('rimbridge/execute_debug_action', EXECUTED_RBS_SHAPE).status, 'reported-success');
  assert.equal(assessExecution('execute_debug_action', EXECUTED_RBS_SHAPE).status, 'reported-success');
});

test('日志比 longEventPending 强：两者冲突时以日志为准（有日志=执行窗口真的跑了）', () => {
  const v = assessExecution('rimworld/execute_debug_action',
    { ...EXECUTED_RBS_SHAPE, stateBefore: { longEventPending: true } });
  assert.equal(v.status, 'reported-success', '有日志就是跑了，不该被 pending 降级');
  assert.equal(v.mainThreadPendingBefore, true, '但 pending 事实仍要如实回报');
});

test('longEventPending 缺失时不得当成 true（否则会把全部正常调用降级）', () => {
  const v = assessExecution('rimworld/execute_debug_action',
    { ...NOT_EXECUTED_RBS_SHAPE, stateBefore: { ticksGame: 1 } });
  assert.equal(v.mainThreadPendingBefore, null, '字段缺失必须是 null，不能是 false 也不能是 true');
  assert.doesNotMatch(v.note, /主线程/);
  assert.match(v.note, /logCount=0/, '缺失时核心信息仍必须完整');
});

test('[25]/§0.4：结论完全不依赖 longEventPending（true / false / 缺失 三种取值结论相同）', () => {
  const base = NOT_EXECUTED_RBS_SHAPE;
  const variants = [
    { ...base, stateBefore: { longEventPending: true } },
    { ...base, stateBefore: { longEventPending: false } },
    { ...base, stateBefore: { ticksGame: 1 } },
  ].map((d) => assessExecution('rimworld/execute_debug_action', d));
  for (const v of variants) {
    assert.equal(v.status, 'unverified', 'pending 只许影响提示，不许影响结论');
    assert.equal(v.executed, null);
    assert.equal(v.evidence, null);
    assert.match(v.note, /logCount=0/, '三种取值下核心信息都必须完整');
  }
});

test('[25]/§0.4：longEventPending=false 不得被读成「主线程健康」', () => {
  const v = assessExecution('rimworld/execute_debug_action',
    { ...NOT_EXECUTED_RBS_SHAPE, stateBefore: { longEventPending: false, ticksGame: 1 } });
  assert.equal(v.mainThreadPendingBefore, false);
  assert.equal(v.status, 'unverified');
  assert.doesNotMatch(v.note, /主线程健康|一切正常/,
    'false 只代表「没有长事件在排队」，不代表主线程没被卡死');
});

test('longEventPending=true 且没日志 → note 额外点名主线程排队（只追加、不替换）', () => {
  const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE);
  assert.match(v.note, /longEventPending=true/);
  assert.match(v.note, /极可能根本没被执行/);
  // §0.4：longEventPending 只许【追加】提示，不得决定 note 的内容分支。
  // 所以 logCount=0 那句在 pending=true 时【也必须还在】——否则代理一旦猜错就会连带丢掉核心信息。
  assert.match(v.note, /logCount=0/);
});

test('残缺/异常回包不抛，落 unverified', () => {
  for (const bad of [undefined, null, 'nope', 42, []]) {
    const v = assessExecution('rimworld/execute_debug_action', bad);
    assert.equal(v.status, 'unverified');
    assert.equal(v.executed, null);
    assert.equal(v.remoteSuccess, null);
  }
  const v = assessExecution('rimworld/execute_debug_action', { value: { operation: 1, effects: [] } });
  assert.equal(v.status, 'unverified', 'gabpClient 的非对象包装 {value:...} 也要能吃');
  assert.equal(v.logCount, null);
});

test('透传诊断字段：remoteDurationMs / logCount / 主线程前后状态 / logWindow', () => {
  const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_BOARD_SHAPE);
  assert.equal(v.remoteDurationMs, 71, '耗时只透传，不参与判定');
  assert.equal(v.logCount, 0);
  assert.equal(v.mainThreadPendingBefore, true);
  assert.equal(v.mainThreadPendingAfter, true);
  assert.equal(v.tool, 'execute_debug_action');
  assert.deepEqual(v.logWindow, { cap: 50, logCount: 0, returnedCount: 0, truncated: false, order: 'newest-first', note: '' },
    'verify 内嵌对象');
  // 反编译形状里没有 durationMs → 必须是 null，不能是 0
  assert.equal(assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE).remoteDurationMs, null);
});

test('EXECUTION_ERROR_CODE 是 REMOTE_REPORTED_FAILURE（README/日志/文案三处共用同一串）', () => {
  assert.equal(EXECUTION_ERROR_CODE, 'REMOTE_REPORTED_FAILURE');
});

// ============ 吐槽板 [17]：effects.logs 的 50 条硬上限 ============

test('[17] 上限常量是 50（RBS 侧硬编码，MCP 层不可改）', () => {
  assert.equal(RBS_EFFECTS_LOG_CAP, 50);
});

test('[17] 未到上限 → truncated:false', () => {
  assert.equal(describeLogWindow(LOGS_BELOW_CAP.effects).truncated, false);
});

test('[17] 恰好 50 条 → truncated 必须是字符串 "possible"，绝不能是 true', () => {
  const w = describeLogWindow(LOGS_EXACTLY_AT_CAP.effects);
  assert.equal(w.returnedCount, 50);
  assert.equal(w.truncated, 'possible',
    '恰好 50 条与「被截断」不可区分——用布尔就是制造新的假阳性');
  assert.equal(typeof w.truncated, 'string');
  assert.match(w.note, /可能已截断/);
});

test('[17] logs 数组比 logCount 短时以数组实际长度为准（不盲信 logCount）', () => {
  const w = describeLogWindow({ logCount: 999, logs: [{ sequence: 1, message: 'a' }] });
  assert.equal(w.returnedCount, 1);
  assert.equal(w.truncated, false, 'logCount 声称很大但只回了 1 条时，不能报「已截断」把锅甩给 RBS');
});

test('[17] 缺 effects / logs 不是数组时容错，truncated 落 null 而不是编一个值', () => {
  for (const bad of [undefined, null, 'nope', {}, { logs: 'nope' }, { logs: [] }]) {
    const w = describeLogWindow(bad);
    assert.ok(w.truncated === null || typeof w.truncated === 'boolean' || w.truncated === 'possible',
      `truncated 只能是 true/false/'possible'/null，实际 ${JSON.stringify(w.truncated)}`);
  }
  assert.equal(describeLogWindow(undefined).truncated, null);
  assert.equal(describeLogWindow({ logs: [] }).returnedCount, 0);
});

test('[17] 顺序回报为 newest-first（RBS 用 Insert(0,…) 倒序，吐槽板 [01] 的说法是准的）', () => {
  assert.equal(describeLogWindow(LOGS_BELOW_CAP.effects).order, 'newest-first');
});

test('[17] verdict 里的 logWindow 与 describeLogWindow 输出一致（不得两处各算一套）', () => {
  const v = assessExecution('rimworld/execute_debug_action', LOGS_EXACTLY_AT_CAP);
  assert.deepEqual(v.logWindow, describeLogWindow(LOGS_EXACTLY_AT_CAP.effects));
  assert.equal(v.logWindow.truncated, 'possible');
});

// ============ 吐槽板 [25]：耗时透传 + fastNoLog 三态（不判定） ============

test('[25] MCP 不内置阈值（FAST_NO_LOG_THRESHOLD_MS 恒为 null）', () => {
  assert.equal(FAST_NO_LOG_THRESHOLD_MS, null,
    '本仓库没有 per-path 历史基线表，内置阈值就是把一个点过拟合成规则');
});

test('[25] 有正面凭据时 fastNoLog=false（「短但有日志」不构成嫌疑）', () => {
  const v = assessExecution('rimworld/execute_debug_action', EXECUTED_RBS_SHAPE, 37);
  assert.equal(v.status, 'reported-success');
  assert.equal(v.fastNoLog, false);
});

test('[25] 无凭据时 fastNoLog=null（本层算不出来就不要编一个值）', () => {
  for (const ms of [3, 72, 60013, null, undefined]) {
    const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE, ms);
    assert.equal(v.fastNoLog, null, `transportDurationMs=${ms} 时 fastNoLog 必须是 null`);
  }
});

test('[25] fastNoLog 取值域只有 false/null —— true 与 possible 当前不可达', () => {
  for (const payload of [NOT_EXECUTED_RBS_SHAPE, EXECUTED_RBS_SHAPE, REMOTE_FAILED_RBS_SHAPE, LOGS_EXACTLY_AT_CAP]) {
    for (const ms of [0, 1, 37, 72, 2097, 60013, null]) {
      const v = assessExecution('rimworld/execute_debug_action', payload, ms);
      assert.ok(v.fastNoLog === false || v.fastNoLog === null,
        `fastNoLog 只能是 false/null，实际 ${JSON.stringify(v.fastNoLog)}（true 会误伤 7 类正常快动作）`);
    }
  }
});

test('[25] transportDurationMs 原样透传，且不参与任何判定', () => {
  // 72ms 与 2097ms 判定结果必须完全一样 —— 这是「时间不参与判定」的硬断言。
  const fast = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE, 72);
  const slow = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE, 2097);
  assert.equal(fast.status, slow.status);
  assert.equal(fast.executed, slow.executed);
  assert.equal(fast.evidence, slow.evidence);
  assert.equal(fast.transportDurationMs, 72);
  assert.equal(slow.transportDurationMs, 2097);
  assert.equal(fast.note, slow.note, 'note 不得因为耗时不同而变化');
});

test('[25] 不传第三参时 transportDurationMs 为 null（缺参不是 0）', () => {
  const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE);
  assert.equal(v.transportDurationMs, null);
  assert.equal(v.fastNoLog, null);
});

test('[25] remoteDurationMs 与 transportDurationMs 是两个不同的量，不得混用', () => {
  // 回包里的 durationMs（很可能是板上误读的日志行数字）vs MCP 自己测的往返耗时。
  const v = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_BOARD_SHAPE, 71);
  assert.equal(v.remoteDurationMs, 71, '回包字段：兼容分支');
  assert.equal(v.transportDurationMs, 71, 'MCP 自测：本次恰好同值，但来源不同');
  const w = assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_BOARD_SHAPE, 5);
  assert.equal(w.remoteDurationMs, 71);
  assert.equal(w.transportDurationMs, 5, '两者必须能独立取值，不得互相覆盖');
});

test('fastNoLog 出现在每一种 status 上（调用方不能靠 status 推断它是否存在）', () => {
  assert.ok('fastNoLog' in assessExecution('rimworld/execute_debug_action', NOT_EXECUTED_RBS_SHAPE, 72));
  assert.ok('fastNoLog' in assessExecution('rimworld/execute_debug_action', EXECUTED_RBS_SHAPE, 72));
  assert.ok('fastNoLog' in assessExecution('rimworld/execute_debug_action', REMOTE_FAILED_RBS_SHAPE, 72));
});
```

- [ ] **步骤 2：运行测试验证失败**

工作目录 `MCP/`，运行：

```
node --test test/executionEvidence.test.js
```

预期：整个文件加载失败，报 `ERR_MODULE_NOT_FOUND: Cannot find module 'C:\...\MCP\executionEvidence.js'`。**这正是「先红」**。

- [ ] **步骤 3：写最少实现让测试通过**

新建 `MCP/executionEvidence.js`：

```javascript
/**
 * GABP 回包的「执行凭据」判定 —— 吐槽板 [01]：execute_debug_action 报告成功但根本没执行。
 *
 * 背景：MCP 层的 wrapGABPResult（MCP/index.js:2690-2694）把「JSON-RPC 没报错」渲染成
 * success:true。对 rimworld.execute_debug_action 这种写操作镜像，传输成功 ≠ 动作执行，
 * 于是自动化把「没跑」读成「跑了」，得出事实相反的结论。
 *
 * 本模块是纯函数：无 I/O、无外部导入，可直接单测（对照 longTask/timeoutPolicy.js 的写法）。
 *
 * 三态判定，只对 EXECUTION_VERDICT_TOOLS 里的短名生效，其余一律返回 null（调用方保持原样）：
 *   reported-failure  远端明确报了失败            → executed:false（有据）
 *   reported-success  拿到了正面凭据              → executed:true （有据）
 *   unverified        既没报失败，也没有任何凭据    → executed:null （无法判定，不许当成跑了）
 *
 * ── 关于 RBS 回包的字段 ──────────────────────────────────────────────
 * RBS 源码不在本仓库，本文件的字段知识有两个来源，强度不同，代码里对应注释也分开写：
 *
 * (1) 反编译「运行时」RBS（创意工坊 content/294100/3727949765）所得 —— 可当事实引用：
 *     - RimBridgeServer.RimWorldDebugActions.ExecuteDebugActionResponse 的顶层字段是
 *       success / path / node / targetPawn / mapTarget / stateBefore / stateAfter / effects / followUp。
 *       **没有 operation，也没有 durationMs。**
 *     - RBS 在两次 CaptureExecution() 之间同步 Invoke node.action，抛异常才 success:false +
 *       exceptionType；调用由 RimBridgeMainThread/Pump()（Root_Update_Patch.Postfix）派发，
 *       主线程卡死时根本回不出响应（表现为超时，是吐槽板 [04]，不是 [01]）。
 *
 * (2) docs/mcp-吐槽板.md [01] / [17] 的现场记录 —— 与 (1) 存在冲突，见下：
 *     - [01] 记的 operation.Success 与 durationMs 在 (1) 的顶层字段表里不存在。
 *       本模块仍读它们（d.operation.Success / d.durationMs），但**只当兼容分支**，
 *       拿不到就是 null，绝不让判定依赖它们。
 *     - effects.logCount > 0   ← 当作正面凭据（执行窗口里真的抓到了日志）。
 *     - effects.logCount === 0 ← 【不是】失败证据。调试动作本来就可能不产日志，
 *       把「真跑了但没日志」判成失败就是本条问题的反面，故只用于强化 note 文案。
 *     - 顶层 d.success === true ← 【待核对】。按 (1) 它应是「同步 Invoke 没抛异常」，是很强的
 *       正面凭据；但没有真实回包验证过，故默认**不**据此判 executed:true，只如实透传成
 *       execution.remoteSuccess。核对后按计划 §7.1 翻 PROMOTE_REMOTE_SUCCESS 即可升级。
 *     - durationMs ← 【不参与判定】，只透传成 remoteDurationMs（可能恒为 null）。
 *     - stateBefore.longEventPending === true ← 调不出因果（「是否入队」发生在 RBS 内部），
 *       故【不降级 success】，只透传成 mainThreadPendingBefore 并在 note 里点名。
 */

export const EXECUTION_ERROR_CODE = 'REMOTE_REPORTED_FAILURE';

/** 需要给出执行凭据的镜像工具短名（RBS 原名的最后一段）。要扩范围只改这里。 */
export const EXECUTION_VERDICT_TOOLS = new Set(['execute_debug_action']);

/**
 * 吐槽板 [17]：RBS 的 effects.logs 只给最后 50 条且**不告知是否截断**。
 * 反编译：RimWorldDebugActions.DescribeEffects → LogJournal.GetEntries(50,…) → source.Take(limit)，
 * 且 logCount = list.Count，所以 logCount 会**饱和在 50**。
 * 这 50 是 RBS 侧的硬编码字面量，execute_debug_action 的签名（path/pawnName/pawnId/x/z/thingId）
 * 也**没有任何分页入参** ⇒ 本仓库无法分页、无法提高上限（见计划 §6）。
 * MCP 层与 GABP 层都没有二次截断：MCP/index.js:4350 传 filterResult=undefined，
 * MCP/index.js:2761-2762 的过滤分支不进；gabpClient.js:59 的 16MB 是**接收缓冲**不是条数上限。
 */
export const RBS_EFFECTS_LOG_CAP = 50;

/**
 * 吐槽板 [01]/[25]：本开关**当前一律保持 false，且已有反例**。
 *
 * 按反编译（§0.2 第 2 条），RBS 顶层 success=true 是「同步 Invoke 没抛异常」的结果，本该是很强的
 * 正面凭据——但**吐槽板 [01]/[25] 的现场恰恰就是「顶层 success:true、动作没跑、72ms 秒回」**。
 * ⇒ 「success=true 就是执行成功」这条判据**已经被 [01] 本身证伪过一次**，
 *    而 §0.2 第 2 条说的「主线程卡死时会回不出响应（超时）」在这条现场上**并不成立**。
 *
 * 所以本计划**不赌、也不开**：默认 false，只把这条证据如实透传成 execution.remoteSuccess。
 * 要改判必须先拿到**能推翻上述反例的新证据**（一份「动作确实执行了、且耗时同样很短」的实测回包），
 * 条件与步骤见计划 §7.1 步骤 B。**不要在没有那份实测的情况下把它改成 true。**
 */
export const PROMOTE_REMOTE_SUCCESS = false;

/**
 * 吐槽板 [25]：「耗时异常短 + 日志为零 ⇒ 判为未执行」这个启发式，**本层不采纳为判据**。
 *
 * 依据（triage/25 §3.1，数据来自 MCP/logs/mcp-20261001-093829.log 全部 1361 行中的 87 条
 * execute_debug_action 调用）：
 *   - `Actions\Hot Query Guard Tests` 6 次耗时 37/39/43/51/60/72ms —— 板上那条「72ms 阈值」
 *     会把这 6 次**全部误伤**，且 72 在现场档与正常档里同时出现，单看耗时根本分不开。
 *   - 87 次里 14 次 < 133ms，分属 **7 个不同 action**。
 *   - `Actions\Stage Test Map` n=36 却跨 3 → 60013ms ⇒「正常要 2097ms」只是单点，**没有可用基线**。
 *   - 日志里**没有 ground truth**（MCP\logs\*.log 五个文件 grep 字面量 logCount 零命中，且
 *     所有调用的 outcome 都写着那个已知不可信的 success）⇒ 任何阈值都是拿一个点过拟合出来的。
 *
 * ⇒ 本层【不内置阈值】（本仓库没有 per-path 历史基线表），只如实透出原始量
 *   （execution.logCount 与 execution.transportDurationMs），阈值比较交给有基线的调用方。
 *   fastNoLog 因此只能取到 false（已有正面凭据）或 null（拿不到耗时）——这是刻意的。
 */
export const FAST_NO_LOG_THRESHOLD_MS = null; // 恒为 null：MCP 不发明自己无法证伪的阈值

const UNVERIFIED_NOTE =
  'RimBridgeServer 的回包只确认了「动作被投递/受理」，没有给出能证明动作已执行的凭据。'
  + 'execution.executed=null 表示「无法判定」——不要读成「已执行」。'
  + '复核办法：对比 Player.log 的行数/mtime 前后变化，或用 rimbridge.list_operation_events 查该次 operation。'
  + '注意 success=true 在本工具上只代表传输成功。';

const LOG_WINDOW_ORDER = 'newest-first'; // RBS 的 _entries 用 Insert(0,…) 倒序；[01] 里「logs[0] 是最后一条」是准的
const LOG_WINDOW_POSSIBLE_NOTE =
  `RimBridgeServer 最多只回 ${RBS_EFFECTS_LOG_CAP} 条 effects.logs 且不告知是否截断（[17]）；`
  + '本次恰好回满，无法区分「真的只有这么多条」与「被截断了」——只能去 rimworld.list_logs 取全量。';

function isPlainObject(v) {
  return !!v && typeof v === 'object' && !Array.isArray(v);
}

function numOrNull(v) {
  return Number.isFinite(v) ? v : null;
}

function boolOrNull(v) {
  return typeof v === 'boolean' ? v : null;
}

/** 取短名：rimworld/execute_debug_action 与 rimworld.execute_debug_action 都 → execute_debug_action */
function shortName(toolName) {
  const s = String(toolName == null ? '' : toolName);
  const cut = Math.max(s.lastIndexOf('/'), s.lastIndexOf('.'));
  return cut === -1 ? s : s.slice(cut + 1);
}

/**
 * 统一给 verdict 盖章 fastNoLog（吐槽板 [25]）。取值域是 true | false | 'possible' | null，
 * 但**本层只可能产出两种**——因为 MCP 不内置阈值（FAST_NO_LOG_THRESHOLD_MS 恒为 null）：
 *   false = 已有正面凭据（status==='reported-success'），「短但有日志」不构成任何嫌疑；
 *   null  = 其余全部情况（无凭据 / 拿不到耗时）⇒ 无法描述，不要编一个值。
 *   'possible' = 需要一个本层拿不到的 per-path 阈值，当前**不可达**；保留在取值域里只为不推翻
 *                调用方的类型判断（与 describeLogWindow 的 truncated:true 同一纪律）。
 *   true  = 🚫 **永不可达**。它会把实测里 7 个正常快动作整片染成「未执行」，正是本条修复要消灭的东西。
 */
function finalize(verdict) {
  return { ...verdict, fastNoLog: verdict.status === 'reported-success' ? false : null };
}

/**
 * 描述 effects.logs 的可见窗口（吐槽板 [17]）。
 *
 * @param {unknown} effects RBS 回包里的 effects 对象（可以整个缺失）
 * @returns {{cap:number, logCount:number|null, returnedCount:number|null,
 *            truncated:true|false|'possible'|null, order:string, note:string}}
 *   truncated 的取值有严格含义，**不许简化成布尔**：
 *     false      = 回包条数 < 50，肯定没截断。
 *     'possible' = 回包条数 >= 50，无法区分「恰好这么多」与「被截断」——这是**不确定**，
 *                  不是 true。用布尔 true 就是凭空制造一个新的假阳性（本条修复的全部意义就是
 *                  不制造假阳性），用布尔 false 则是凭空制造一个假阴性。
 *     true       = 只有 RBS 将来显式回一个 truncated 字段时才可能到达；当前实现不产生它，
 *                  保留在取值域里是为了不推翻调用方的类型判断。
 *     null       = effects/logs 缺失或形状不对，无法描述（不要编一个值）。
 */
export function describeLogWindow(effects) {
  const e = isPlainObject(effects) ? effects : null;
  const logs = e && Array.isArray(e.logs) ? e.logs : null;
  const returnedCount = logs ? logs.length : null;
  const logCount = e ? numOrNull(e.logCount) : null;
  let truncated = null;
  let note = '';
  if (returnedCount != null) {
    if (returnedCount < RBS_EFFECTS_LOG_CAP) {
      truncated = false; // 以实际返回的数组长度为准，不盲信 logCount（见测试「logs 数组比 logCount 短」）
    } else {
      truncated = 'possible';
      note = LOG_WINDOW_POSSIBLE_NOTE;
    }
  }
  return { cap: RBS_EFFECTS_LOG_CAP, logCount, returnedCount, truncated, order: LOG_WINDOW_ORDER, note };
}

/**
 * 判定一次 GABP 工具调用到底带不带执行凭据。判据优先级见计划 §1.1。
 * @param {string} toolName RBS 原名（rimworld/execute_debug_action）或镜像名（rimworld.execute_debug_action）
 * @param {unknown} data    gabpClient.callTool 返回的 result（原样透传的 RBS 回包）
 * @returns {null | {
 *   tool: string,
 *   status: 'reported-failure' | 'reported-success' | 'unverified',
 *   executed: true | false | null,
 *   evidence: string | null,
 *   remoteSuccess: true | false | null,
 *   logCount: number | null,
 *   remoteDurationMs: number | null,
 *   mainThreadPendingBefore: boolean | null,
 *   mainThreadPendingAfter: boolean | null,
 *   logWindow: {cap:number, logCount:number|null, returnedCount:number|null,
 *               truncated:true|false|'possible'|null, order:string, note:string},
 *   note: string,
 * }}
 *   返回 null = 该工具不在判定范围内，调用方必须保持原返回结构逐字节不变。
 */
export function assessExecution(toolName, data, transportDurationMs = null) {
  const tool = shortName(toolName);
  if (!EXECUTION_VERDICT_TOOLS.has(tool)) return null;

  const d = isPlainObject(data) ? data : {};
  const operation = isPlainObject(d.operation) ? d.operation : null;
  const effects = isPlainObject(d.effects) ? d.effects : null;
  const stateBefore = isPlainObject(d.stateBefore) ? d.stateBefore : null;
  const stateAfter = isPlainObject(d.stateAfter) ? d.stateAfter : null;
  const transportMs = numOrNull(transportDurationMs);

  const base = {
    tool,
    evidence: null,
    // RBS 自己的顶层 success。null = 回包里没有这个字段（老形状 / 被包裹过）。
    remoteSuccess: boolOrNull(d.success),
    logCount: effects ? numOrNull(effects.logCount) : null,
    // 反编译的顶层字段表里没有 durationMs，这里取不到就是 null，绝不当判据。
    // ⚠️ 吐槽板 [01] 记的 durationMs: 72 极可能是把 MCP 自己的日志行（MCP/index.js:5132 写的
    // 「→ 72ms success」）读成了回包字段，所以这个字段很可能**恒为 null**（见计划 §0.2 待核对 B）。
    remoteDurationMs: numOrNull(d.durationMs),
    // MCP 自己测的 wall-clock（由 callGABPTool 在 gabpBridge.callTool 两侧测量后传进来）。
    // 这是**传输往返耗时**，不是 RBS 报的执行耗时；它**只透传、不参与任何判定**（吐槽板 [25]，
    // 理由见 FAST_NO_LOG_THRESHOLD_MS 的注释）。改动前调用方在返回值里根本拿不到耗时
    // （MCP/index.js:5123 的 elapsed 只在 :5132 进日志，:5134-5135 直接 return）。
    transportDurationMs: transportMs,
    mainThreadPendingBefore: stateBefore ? boolOrNull(stateBefore.longEventPending) : null,
    mainThreadPendingAfter: stateAfter ? boolOrNull(stateAfter.longEventPending) : null,
    logWindow: describeLogWindow(effects),
  };
  // fastNoLog 的取值在下面每个 return 之前由 finalize() 统一盖章（见 finalize 的注释）。
  base.fastNoLog = null;

  // ① 远端明确报失败 —— 有据，不含任何本地推断。
  //    顶层 d.success 是第一判据（反编译：RBS 同步 Invoke 抛异常的唯一出口）；
  //    operation.Success 与 d.executed 是兼容分支（[01] 的老形状 / RBS 将来可能加的显式凭据）。
  if (d.success === false || (operation && operation.Success === false) || d.executed === false) {
    const evidence = (d.success === false) ? 'success'
      : (operation && operation.Success === false) ? 'operation.Success'
        : 'executed';
    return finalize({
      ...base,
      status: 'reported-failure',
      executed: false,
      evidence,
      note: `RimBridgeServer 明确报告动作未成功执行（判据：${evidence}）。`
        + '这是远端自己的结论，不是本地推断——请读 data 里的报错信息（异常类型/原文）再决定是否重试或改 path。',
    });
  }

  // ② 正面凭据。先看 RBS 自己声明 executed，再看有没有抓到日志。
  //    注意顺序：日志判定排在 longEventPending 提示之前 —— 日志是执行窗口的直接证据，
  //    longEventPending 的语义本层无法核实，所以两者冲突时以日志为准（见步骤 1 的对应用例）。
  if (d.executed === true || (operation && operation.Executed === true)) {
    return finalize({
      ...base,
      status: 'reported-success',
      executed: true,
      evidence: 'executed',
      note: 'RimBridgeServer 明确回报动作已执行（判据：executed）。',
    });
  }
  if (base.logCount != null && base.logCount > 0) {
    return finalize({
      ...base,
      status: 'reported-success',
      executed: true,
      evidence: 'effects.logCount',
      note: `RimBridgeServer 在本次执行窗口内抓到 ${base.logCount} 条日志（判据：effects.logCount），据此判定动作确实执行了。`
        + (base.logWindow.truncated === 'possible' ? LOG_WINDOW_POSSIBLE_NOTE : ''),
    });
  }

  // ③ 顶层 success === true —— 【待核对，且当前已有反例】，见计划 §0.2 待核对 A / §0.3。
  //    [01]/[25] 的现场就是「顶层 success:true、动作没跑、72ms 秒回」，所以这条判据已被证伪过一次。
  //    PROMOTE_REMOTE_SUCCESS 写死 false ⇒ 本分支当前不可达；要改判的条件见计划 §7.1 步骤 B。
  if (PROMOTE_REMOTE_SUCCESS && base.remoteSuccess === true) {
    return finalize({
      ...base,
      status: 'reported-success',
      executed: true,
      evidence: 'success',
      note: 'RimBridgeServer 回报顶层 success=true（判据：success）——该判据经真实回包核对后才启用。',
    });
  }

  // ④ 没有任何可采信的凭据 —— 这是 [01] 的形状，绝不判成功，也绝不判失败。
  //
  //    ⚠️ §0.4（[25] triage 挖出的缺口）：note 必须写成**纯追加式**。
  //    longEventPending 只是「主线程被卡」的**代理**，而 [01]/[25] 的现场是 OnGUI 自旋
  //    （自旋 ≠ 长事件排队，真实回包里它很可能是 false 甚至没有该字段）。
  //    ⇒ 它【不得决定 note 的内容分支】，只能在为 true 时追加一句；否则代理一旦猜错，
  //      就会连带把 logCount / remoteSuccess 这些与它无关的核心信息一起丢掉。
  return finalize({
    ...base,
    status: 'unverified',
    executed: null,
    evidence: null,
    note: UNVERIFIED_NOTE
      + (base.logCount === 0 ? ' 本次 effects.logCount=0：动作可能确实没跑，也可能是跑了但没产日志——两者在本层无法区分。' : '')
      + (base.remoteSuccess === true
        ? ' （RBS 顶层回的是 success=true，但该字段的含义尚未用真实回包核对，本层不据此判定执行。）'
        : '')
      + (base.logWindow.truncated === 'possible' ? LOG_WINDOW_POSSIBLE_NOTE : '')
      + (base.mainThreadPendingBefore === true
        ? ' 另外 stateBefore.longEventPending=true：调用发出时游戏主线程还有长事件在排队，本次极可能根本没被执行。'
        : '')
      + ' 提醒：mainThreadPendingBefore=false 只代表「没有长事件在排队」，**不代表主线程健康**'
      + '（游戏卡在 OnGUI 异常循环里时根本不 tick，长事件队列可能是空的）。',
  });
}

export default {
  assessExecution,
  describeLogWindow,
  EXECUTION_ERROR_CODE,
  EXECUTION_VERDICT_TOOLS,
  RBS_EFFECTS_LOG_CAP,
  PROMOTE_REMOTE_SUCCESS,
  FAST_NO_LOG_THRESHOLD_MS,
};
```

- [ ] **步骤 4：运行测试验证通过**

工作目录 `MCP/`，运行：

```
node --test test/executionEvidence.test.js
```

预期：`# pass 32`、`# fail 0`（24 条 [01]/[17] 用例 + 8 条本轮新增的 [25]/§0.4 用例）。任何一条 fail 都是判定规则与用例对不上，**不要改用例去迁就实现**——回步骤 3 对齐规则。

- [ ] **步骤 5：改动清单（本项目不是 git 仓库，无提交动作）**

本次触碰 3 个新文件，**没有任何已存在文件被修改**：

- 新建 `MCP/executionEvidence.js`
- 新建 `MCP/test/fixtures/executeDebugActionPayloads.js`
- 新建 `MCP/test/executionEvidence.test.js`

---

### 任务 2：接线到 `MCP/index.js` 的返回信封

**文件：**
- 修改：`MCP/index.js:2690-2694`（`wrapGABPResult`）
- 修改：`MCP/index.js:2758-2764`（`callGABPTool` 里的盖章分支）
- 修改：`MCP/index.js:32` 之后（加一行 import）
- 测试：`MCP/test/gabpExecutionVerdict.test.js`

- [ ] **步骤 1：写失败测试**

新建 `MCP/test/gabpExecutionVerdict.test.js`：

```javascript
import { test, after } from 'node:test';
import assert from 'node:assert/strict';

// 关键：必须在 import('../index.js') 之前设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');
import {
  NOT_EXECUTED_RBS_SHAPE, EXECUTED_RBS_SHAPE, REMOTE_FAILED_RBS_SHAPE,
  NOT_EXECUTED_BOARD_SHAPE, LOGS_EXACTLY_AT_CAP,
} from './fixtures/executeDebugActionPayloads.js';

// —— 「动作没跑但 JSON-RPC 没报错」场景的造法 ——
// 用 _testHooks.setGabpBridge（MCP/index.js:5453）注入一个假桥接：不起游戏、不连网、不需要 RimBridgeServer。
//   isConnected()=true  → ensureGabpConnected 在 MCP/index.js:2670 直接放行，不去真连。
//   callTool 返回 {ok:true, result:<样本>} —— 这正是 gabpClient.js:667-684 / :644-648
//   在「JSON-RPC 没报错」时的返回值形状。ok=true 之外什么都不做。
// 换句话说：**传输层成功的全部条件 = 假桥接的 ok:true；执行没发生 = 我们喂进去的样本本身没凭据。**
// 两件事完全解耦，所以「没跑但报成功」这个状态可以被精确复现。
function fakeBridge(callToolImpl) {
  return {
    state: 'connected',
    isConnected: () => true,
    lastError: null,
    forceReconnect: () => {},
    callTool: callToolImpl,
  };
}
const repliesWith = (payload) => fakeBridge(async () => ({ ok: true, result: payload }));

after(() => { mod._testHooks.setGabpBridge(null); });

test('[01] 假成功样本：success 仍是传输语义，但 execution 明说「无法判定」', async () => {
  mod._testHooks.setGabpBridge(repliesWith(NOT_EXECUTED_RBS_SHAPE));
  const res = await mod.callGABPTool('rimworld/execute_debug_action', { path: 'Actions\\Inject Cell Cost Tests' });
  const body = JSON.parse(res.content[0].text);

  assert.equal(body.transport, 'ok', '必须显式区分「传输成功」与「动作成功」');
  assert.equal(body.success, true, '传输层确实成功，success 保持原义（向后兼容，见计划 §5）');
  assert.equal(body.execution.status, 'unverified');
  assert.equal(body.execution.executed, null, 'executed=null 才是本条修复的核心：不再替 RBS 打包票');
  assert.equal(body.execution.remoteSuccess, true, 'RBS 自己的 success=true 必须如实透传，不能被吞');
  assert.match(body.execution.note, /不要读成「已执行」/);
  // 信息不丢：RBS 的原始回包必须原样保留在 data 里
  assert.equal(body.data.success, true, 'data 是 RBS 的回包，它的 success 不能被 MCP 覆盖');
  assert.equal(body.data.node.path, 'Actions\\Inject Cell Cost Tests');
  assert.equal(body.data.effects.logCount, 0);
  assert.equal(body.execution.remoteDurationMs, null,
    '反编译的顶层字段表里没有 durationMs，取不到必须是 null 而不是 0');
});

test('[01] 老形状（operation.Success / durationMs）在端到端路径上也不崩', async () => {
  mod._testHooks.setGabpBridge(repliesWith(NOT_EXECUTED_BOARD_SHAPE));
  const body = JSON.parse((await mod.callGABPTool('rimworld/execute_debug_action', { path: 'x' })).content[0].text);
  assert.equal(body.execution.status, 'unverified');
  assert.equal(body.execution.remoteSuccess, null);
  assert.equal(body.execution.remoteDurationMs, 71);
});

test('真跑过（有日志）：execution.executed=true，判据写明来源', async () => {
  mod._testHooks.setGabpBridge(repliesWith(EXECUTED_RBS_SHAPE));
  const body = JSON.parse((await mod.callGABPTool('rimworld/execute_debug_action', { path: 'x' })).content[0].text);
  assert.equal(body.success, true);
  assert.equal(body.execution.status, 'reported-success');
  assert.equal(body.execution.executed, true);
  assert.equal(body.execution.evidence, 'effects.logCount');
});

test('[17] 日志窗口随 verdict 一起进返回值，且回满 50 条时报 "possible" 而非 true', async () => {
  mod._testHooks.setGabpBridge(repliesWith(LOGS_EXACTLY_AT_CAP));
  const body = JSON.parse((await mod.callGABPTool('rimworld/execute_debug_action', { path: 'x' })).content[0].text);
  assert.equal(body.execution.logWindow.cap, 50);
  assert.equal(body.execution.logWindow.returnedCount, 50);
  assert.equal(body.execution.logWindow.truncated, 'possible');
  assert.equal(body.execution.logWindow.order, 'newest-first');
  assert.match(body.execution.logWindow.note, /可能已截断/);
  assert.equal(body.data.effects.logs.length, 50, '原始 logs 必须原样保留，没被 MCP 层裁剪');
});

test('[25] transportDurationMs 由 MCP 自己测得并透传进返回值（不参与判定）', async () => {
  // 假桥接故意 sleep 25ms，好让 transportDurationMs 有个确定的量级。
  mod._testHooks.setGabpBridge(fakeBridge(async () => {
    await new Promise((r) => setTimeout(r, 25));
    return { ok: true, result: NOT_EXECUTED_RBS_SHAPE };
  }));
  const body = JSON.parse((await mod.callGABPTool('rimworld/execute_debug_action', { path: 'x' })).content[0].text);
  assert.equal(typeof body.execution.transportDurationMs, 'number', '耗时现在必须能被调用方读到');
  assert.ok(body.execution.transportDurationMs >= 20,
    `应至少覆盖那次 25ms sleep，实际 ${body.execution.transportDurationMs}`);
  // 但它绝不能改变判定
  assert.equal(body.execution.status, 'unverified');
  assert.equal(body.execution.executed, null);
  assert.equal(body.execution.fastNoLog, null, 'MCP 不内置阈值 ⇒ fastNoLog 只能是 false/null');
});

test('远端明确报失败：success 翻成 false，errorCode=REMOTE_REPORTED_FAILURE，data 仍保留', async () => {
  mod._testHooks.setGabpBridge(repliesWith(REMOTE_FAILED_RBS_SHAPE));
  const body = JSON.parse((await mod.callGABPTool('rimworld/execute_debug_action', { path: 'x' })).content[0].text);
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'REMOTE_REPORTED_FAILURE');
  assert.equal(body.transport, 'ok', '传输层是成功的，别把两种失败混为一谈');
  assert.equal(body.execution.status, 'reported-failure');
  assert.equal(body.execution.executed, false);
  assert.equal(body.execution.evidence, 'success');
  assert.equal(body.data.exceptionMessage, 'Debug action not found', '远端报错原文不能被吞掉');
});

test('向后兼容：非判定范围的镜像工具，返回体不含任何新字段', async () => {
  mod._testHooks.setGabpBridge(repliesWith(NOT_EXECUTED_RBS_SHAPE));
  const body = JSON.parse((await mod.callGABPTool('rimworld/list_debug_action_children', {})).content[0].text);
  assert.equal(body.success, true);
  assert.ok(!('execution' in body), '非判定范围不得被塞 execution');
  assert.ok(!('transport' in body), '非判定范围不得被塞 transport');
  assert.deepEqual(Object.keys(body).sort(), ['data', 'success'], '返回体必须逐字节不变');
});

test('传输层真失败时仍是 GABP_ERROR，不得混入执行判定', async () => {
  mod._testHooks.setGabpBridge(fakeBridge(async () => ({ ok: false, error: { code: -32001, message: '超时' } })));
  const body = JSON.parse((await mod.callGABPTool('rimworld/execute_debug_action', { path: 'x' })).content[0].text);
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'GABP_ERROR', '传输失败与远端报失败是两个不同的 errorCode');
  assert.ok(!('execution' in body), '传输失败没有回包可判，不该出现 execution');
});

test('RBS 改形状也不炸：非对象回包落 unverified 而不是抛异常，且原样透传', async () => {
  // gabpClient.js:675-679 会把非对象回包包成 {value: ...}。假桥接喂这个形状，
  // 才是真 gabpClient 的忠实替身（MCP 层不该、也必须不碰这个包装）。
  mod._testHooks.setGabpBridge(repliesWith({ value: 'queued' }));
  const body = JSON.parse((await mod.callGABPTool('rimworld/execute_debug_action', { path: 'x' })).content[0].text);
  assert.equal(body.success, true);
  assert.equal(body.execution.status, 'unverified');
  assert.equal(body.data.value, 'queued', '回包必须原样透传，不许在 MCP 层被吃掉');
});
```

- [ ] **步骤 2：运行测试验证失败**

工作目录 `MCP/`，运行：

```
node --test test/gabpExecutionVerdict.test.js
```

预期：7 条涉及 `execution`/`transport` 的用例 fail（`body.execution` 是 `undefined` → `TypeError: Cannot read properties of undefined`），两条向后兼容用例 pass。**这正是「先红」**。

- [ ] **步骤 3：接线（改 3 处）**

> **吐槽板 [17]（effects.logs 50 条上限）在这一步是【零额外改动】**：`wrapGABPResult` 已经把整个
> `verdict` 原样挂进 `execution`，而 `logWindow` 是 verdict 的一个字段。所以下面这三处改完，
> `execution.logWindow.truncated` 就已经在返回值里了。**不要**为了 [17] 再动 `MCP/index.js` 的任何一行——
> 那是本条修复最容易「写歪」的地方。

第一处 —— `MCP/index.js`，在第 32 行 `import { appendRecord, ... } from './longTask/journal.js';` **之后**新增一行：

```javascript
import { assessExecution, EXECUTION_ERROR_CODE } from './executionEvidence.js';
```

第二处 —— `MCP/index.js:2689-2694`，把现有的

```javascript
// §6.4 结果归一化：保证 tools/call outcome 日志解析（{success:true|false}）与现有调用方契约兼容
function wrapGABPResult(raw, ok) {
  return ok
    ? { success: true, data: raw }
    : { success: false, errorCode: 'GABP_ERROR', error: String(raw) };
}
```

替换为

```javascript
// §6.4 结果归一化：保证 tools/call outcome 日志解析（{success:true|false}）与现有调用方契约兼容
// 2026-10-01 吐槽板 [01]：ok 只等价于「JSON-RPC 没报错」（gabpClient.js:644-648 → :667-684），
// 对 execute_debug_action 这类写操作镜像，传输成功 ≠ 动作执行。
// 第三个参数 verdict 由 assessExecution 产出（其中已含 [17] 的 logWindow），有据报失败才翻 success；
// 没凭据时 success 保持传输语义，另给 execution.executed=null 明说「无法判定」。
// verdict 为 null（不在判定范围的工具）时返回体逐字节不变。
function wrapGABPResult(raw, ok, verdict) {
  if (!ok) return { success: false, errorCode: 'GABP_ERROR', error: String(raw) };
  if (!verdict) return { success: true, data: raw };
  if (verdict.status === 'reported-failure') {
    return {
      success: false,
      errorCode: EXECUTION_ERROR_CODE,
      error: verdict.note,
      transport: 'ok',
      execution: verdict,
      data: raw,
    };
  }
  return { success: true, transport: 'ok', execution: verdict, data: raw };
}
```

第三处 —— `MCP/index.js:2757-2764`（`const res = ...` 到盖章 `return` 整段），把现有的

```javascript
  const res = await gabpBridge.callTool(rbsName, args || {}, callOpts);
  if (res && res.ok) {
    gabpConsecutiveTimeout = 0; // 成功响应重置超时计数
    let data = res.result;
    if (typeof filterResult === 'function') {
      data = filterResult(data, args || {});
    }
    return { content: [{ type: 'text', text: JSON.stringify(wrapGABPResult(data, true), null, 2) }] };
  }
```

替换为

```javascript
  // [25]：记下 MCP 自己的往返耗时，**只透传给 verdict，不参与任何判定**。
  // ⚠️ 它与 MCP/index.js:5123 那个外层 elapsed 是【两个不同的量】：那个包住了整个 tools/call
  //    （含 ensureGabpConnected 的重连耗时，冷重连时能到秒级），而这里只包住这一次
  //    gabpBridge.callTool 往返。改动前这个耗时只在 :5132 进日志、从不进返回值。
  const transportStart = Date.now();
  const res = await gabpBridge.callTool(rbsName, args || {}, callOpts);
  const transportMs = Date.now() - transportStart;
  if (res && res.ok) {
    gabpConsecutiveTimeout = 0; // 成功响应重置超时计数
    let data = res.result;
    if (typeof filterResult === 'function') {
      data = filterResult(data, args || {});
    }
    // 吐槽板 [01]：ok 只说明 JSON-RPC 没报错。对 execute_debug_action 再判一次「有没有执行凭据」，
    // verdict 里已含 execution 全字段、[17] 的 logWindow 与 [25] 的 transportDurationMs/fastNoLog；
    // verdict 为 null 时（非判定范围的工具）wrapGABPResult 原样返回，不引入任何新字段。
    const verdict = assessExecution(rbsName, data, transportMs);
    return { content: [{ type: 'text', text: JSON.stringify(wrapGABPResult(data, true, verdict), null, 2) }] };
  }
```

> ⚠️ **`transportStart/transportMs` 在失败路径上也会被取到**（`res.ok === false` 时 `transportMs` 白算了），但那条路径 `return gabpErrorResponse(...)` 根本不经过 `assessExecution`，所以不会有任何影响——**不要**为了「省一次 `Date.now()`」把测量挪进 `if (res && res.ok)` 里面，那会把 `const` 声明关进一个它必须活到外面用不到的块里、且让「测量点 = 调用点」这个不变量断掉。

**不要动** `MCP/index.js:2699`（`gabpErrorResponse`，走 `ok=false` 分支，`verdict` 为 `undefined`，返回体不变）和 `MCP/index.js:4297`（`start_debug_game_ready` 幂等短路，调的是 `wrapGABPResult(obj, true)` 两个参数，`verdict` 为 `undefined`，返回体不变）。

- [ ] **步骤 4：运行测试验证通过**

工作目录 `MCP/`，运行：

```
node --test test/gabpExecutionVerdict.test.js
```

预期：`# pass 9`、`# fail 0`。

- [ ] **步骤 5：跑全量确认没有回归**

工作目录 `MCP/`，运行：

```
npm test
```

预期：`# fail 0`。本仓测试全程不起游戏、不改游戏状态（已逐个确认 `MCP/test/` 下 9 个测试文件：无一个 spawn/启动 RimWorld，`start_game` 只在 `toolchainFixes.test.js:263,288` 的源码字符串断言里出现）。

- [ ] **步骤 6：改动清单（本项目不是 git 仓库，无提交动作）**

- 修改 `MCP/index.js`（3 处，共 +22 −4 行；**吐槽板 [17]/[25] 在这三处之外零改动**）
- 新建 `MCP/test/gabpExecutionVerdict.test.js`

---

### 任务 3：把「`success` 到底代表什么」写进工具文案与 README

**文件：**
- 修改：`MCP/index.js:2930` 附近的 `TOOL_USAGE_NOTES`（新增一条 `rimworld.execute_debug_action` / `rimbridge.execute_debug_action`）
- 修改：`README.md:172`
- 测试：`MCP/test/gabpExecutionVerdict.test.js`（**在任务 2 建的同一个文件末尾追加用例**，不另起文件）

- [ ] **步骤 1：写失败测试**

在 `MCP/test/gabpExecutionVerdict.test.js` **文件顶部**（现有 `import { test, after } from 'node:test';` 那两行下面）补上三行 import：

```javascript
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
```

然后在**文件末尾**追加：

```javascript
// —— 文案守卫 ——
// 为什么用源码/文档字符串断言而不是跑 mcp_help：
// `rimworld.execute_debug_action` 不是静态注册的工具，它是连上 RBS 之后才被并进工具表的
// （index.js:2514 的 gabpTools / :2644），不启动游戏时 mcp_help 对它的 tool 查询会回「未知工具」。
// 所以这里退一步做**文案回归守卫**：确保那句「success 只代表传输成功」的说明不会被后续改动悄悄删掉。
const here = path.dirname(fileURLToPath(import.meta.url));
const indexSource = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8');
const readme = fs.readFileSync(path.join(here, '..', '..', 'README.md'), 'utf-8');

test('mcp_help 能查到的 notes 里写明了 success 的真实含义（两个前缀都登记）', () => {
  assert.match(indexSource, /'rimworld\.execute_debug_action':\s*\{/,
    'TOOL_USAGE_NOTES 必须登记 rimworld.execute_debug_action');
  assert.match(indexSource, /'rimbridge\.execute_debug_action':\s*\{/,
    'rimbridge.* 前缀同样能命中，必须一并登记');
  assert.match(indexSource, /EXECUTE_DEBUG_ACTION_NOTE/,
    '两条 notes 必须共用同一个常量，避免两份文案漂移');
  assert.match(indexSource, /success 只代表 GABP\/JSON-RPC 传输成功/,
    '文案必须点明 success 的真实含义');
  assert.match(indexSource, /executed/, '文案必须指向 execution.executed 判据');
});

// 断言必须**只针对那一句文案**，不能扫整个 index.js：
// MCP/index.js:2646 的既有注释里就有「去重」二字（「GABP 镜像工具并入清单: N 个（去重+移除 …）」），
// 对整个文件做 doesNotMatch(/去重|折叠/) 会假失败。这里把 note 单独切出来再断言。
const noteStart = indexSource.indexOf('const EXECUTE_DEBUG_ACTION_NOTE');
assert.ok(noteStart > 0, 'EXECUTE_DEBUG_ACTION_NOTE 常量必须存在');
const noteEnd = indexSource.indexOf('\nconst ', noteStart + 10);
const noteBlock = indexSource.slice(noteStart, noteEnd > noteStart ? noteEnd : noteStart + 3000);

test('吐槽板 [17]：文案必须点明 effects.logs 的 50 条上限与三态 truncated', () => {
  assert.match(noteBlock, /只回 50 条/,
    '文案必须写明 RBS 只给 50 条');
  assert.match(noteBlock, /truncated/, '文案必须指向 execution.logWindow.truncated');
  assert.match(noteBlock, /possible/, '文案必须点明回满 50 条时的不确定性');
  assert.match(noteBlock, /list_logs\{limit:2000\}/, '文案必须给出补全量日志的实际办法');
  assert.match(noteBlock, /没有分页入参|不是分页/, '文案必须如实说明没有分页能力');
});

test('吐槽板 [18]：文案只提醒「UE 日志会成对出现」，不断言两处计数必然不同', () => {
  assert.match(noteBlock, /Player\.log/, '[18] 的成因在 Player.log 侧，文案要点到它');
  assert.match(noteBlock, /成对/, '必须用「成对出现」这种不越界的说法');
  assert.doesNotMatch(noteBlock, /去重|折叠/,
    '不得把「effects.logs 会去重」写进文案——那条因果已被证伪（ShouldCollapse 要 Message 逐字相等）');
  assert.doesNotMatch(noteBlock, /两处必然不同|计数必然不一致|必然对不上/,
    '板子 [18]「两处数字必然不同」的前提未经实测，文案不得写成定论');
});

test('吐槽板 [33]：add_using_directive 的提醒单独登记，且给出可执行的验法', () => {
  // [33] 与本计划不是同一条路径（见 §0.5），所以这里只要求「有提醒 + 提醒可执行」，
  // 绝不能出现任何 add_using_directive 相关的判据代码（本测试的反面即回归）。
  assert.match(indexSource, /'add_using_directive':\s*\{/, 'TOOL_USAGE_NOTES 必须登记 add_using_directive');
  assert.match(indexSource, /ADD_USING_DIRECTIVE_NOTE/, '提醒文案必须是独立常量，便于单独守卫');
  assert.match(indexSource, /execute_csharp_code/, '提醒必须给出可执行的验法（用一次裸类型名调用验一把）');
});

test('README 的 GABP 镜像工具表同样写明该含义', () => {
  // 必须限定为表格行：README.md:160 那段说明文字里也出现了 rimworld.execute_debug_action，
  // 不加 startsWith('|') 会匹配到它，然后断言必然失败。
  const line = readme.split('\n')
    .find((l) => l.trimStart().startsWith('|') && l.includes('rimworld.execute_debug_action'));
  assert.ok(line, 'README 工具表应仍有 rimworld.execute_debug_action 那一行');
  assert.match(line, /success/, 'README 该行必须提到 success');
  assert.match(line, /REMOTE_REPORTED_FAILURE/, 'README 该行必须给出远端报失败时的 errorCode');
});
```

- [ ] **步骤 2：运行测试验证失败**

工作目录 `MCP/`，运行：

```
node --test test/gabpExecutionVerdict.test.js
```

预期：5 条新用例 fail——两条报「`TOOL_USAGE_NOTES 必须登记 rimworld.execute_debug_action`」，三条报「`EXECUTE_DEBUG_ACTION_NOTE 常量必须存在`」。**这正是「先红」**。

- [ ] **步骤 3：写文案**

`MCP/index.js`，在第 2929 行注释 `// 易错点与调用示例（运行时实测积累）` 与第 2930 行 `const TOOL_USAGE_NOTES = {` **之间**插入常量：

```javascript
// 吐槽板 [01]（2026-10-01）：GABP 镜像的 success 只能证明「投递没在传输层报错」，
// 对 execute_debug_action 这类写操作镜像不证明动作真的执行了。两个前缀共用同一句文案。
// [17]/[18] 是「读返回值时容易踩的坑」，一并写在这里，避免调用方自己踩一遍。
// [33] 是另一个工具、另一条路径（见计划 §0.5），所以单列一条，不混进上面这句里。
const EXECUTE_DEBUG_ACTION_NOTE = '⚠️ success 只代表 GABP/JSON-RPC 传输成功，不代表动作已执行（吐槽板 [01] 假成功）。'
  + '判断是否真的跑了，请读返回里的 execution 字段：'
  + 'executed=true 表示 RBS 抓到了执行证据，false 表示 RBS 明确报失败（此时 success=false、errorCode=REMOTE_REPORTED_FAILURE），'
  + 'null 表示远端没给凭据、无法判定——不要把它读成「已执行」。'
  + '疑似没跑时复核：对比 Player.log 的行数/mtime 前后变化，或用 rimbridge.list_operation_events 查该次 operation。'
  + '另：effects.logs 最多只回 50 条且不告知是否截断（吐槽板 [17]），execution.logWindow.truncated 会告诉你这件事——'
  + '它是三态：false=肯定没截断，"possible"=回满 50 条但无法区分「恰好这么多」与「被截断」（不是 true，别当布尔读）。'
  + '需要全量日志请改用 rimworld.list_logs{limit:2000}（取整个 ring，不是分页：只支持 afterSequence、没有 beforeSequence，且是倒序）。'
  + '本工具没有任何分页入参，想翻历史窗口只能自己再跑一次动作。'
  + '还有（吐槽板 [25]）：execution.transportDurationMs 是 MCP 自己测的传输往返耗时，只是旁证、不参与判定；'
  + '同一份实测里 7 个不同 action 的正常耗时就有 37~133ms 的，board 上那条「72ms 即未执行」会成片误伤，别自己照抄。'
  + '还有（吐槽板 [18]）：UnityExplorer 的每条日志也会被本模组带 [UnityExplorer] 前缀写回 Player.log（Source/UELoader/ExplorerBootstrap.cs），'
  + '所以 Player.log 里同一条 UE 日志常成对出现，数行数时别当成动作跑了两遍。';

// 吐槽板 [33]：add_using_directive 走的是 callUnityExplorerAPI（游戏内 HTTP），不是 GABP 路径
// （见计划 §0.5 的路径对照表）。MCP/index.js:806 已经检查了 response.ok && data.success，
// 问题是游戏侧 ConsoleController.AddUsing 在编译失败时仍报成功——本项目改不了它，只能提醒调用方自查。
const ADD_USING_DIRECTIVE_NOTE = '⚠️ 返回 success 不代表这个 using 真的加上了（吐槽板 [33]）：'
  + '游戏侧 ConsoleController.AddUsing 是先加进列表再编译、编译失败也不回滚，'
  + '而失败细节会被就地吞掉、不回传。'
  + '加完请用一次 execute_csharp_code 验一把：直接写裸类型名（如 Verse.GenTicks.TicksGame）能跑通才算生效。';
```

紧接着，在 `TOOL_USAGE_NOTES` 对象里（放在 `search_debug_actions` 那条 `:2933` 之后、`search_map_structure` 那条 `:2934` 之前）插入两行：

```javascript
  'rimworld.execute_debug_action': { example: '{ "path": "Actions\\\\Stage Test Map" }', notes: EXECUTE_DEBUG_ACTION_NOTE },
  'rimbridge.execute_debug_action': { example: '{ "path": "Actions\\\\Stage Test Map" }', notes: EXECUTE_DEBUG_ACTION_NOTE },
  add_using_directive: { example: '{ "namespace": "Verse" }', notes: ADD_USING_DIRECTIVE_NOTE },
```

> **为什么 `example` 里是四个反斜杠**：`TOOL_USAGE_NOTES[...].example` 是一段**要被用户复制走的 JSON 片段**，而它自身还会被 `mcpHelpResult`（`MCP/index.js:3670`）`JSON.stringify` 一次。JS 源码里的 `\\\\` 求值成两个字符 `\\`，那正是**合法 JSON** 里表示「一个反斜杠」的写法；复制出去直接粘进 `args` 才不会变成非法转义。对照 `MCP/index.js:2411` 的工具描述用的是单个 `\\`（求值成一个反斜杠），那是因为它描述的是**值**而不是 JSON 片段——两处用途不同，别抄错。

`README.md:172`，把这一行

```
| `rimworld.get_debug_action` / `rimworld.execute_debug_action` | 调试动作详情 / 执行（仅 `path`/`pawnId`） |
```

替换为

```
| `rimworld.get_debug_action` / `rimworld.execute_debug_action` | 调试动作详情 / 执行（仅 `path`/`pawnId`）。⚠️ 执行的 `success` **只代表 GABP/JSON-RPC 传输成功，不代表动作已执行**（issue [01] 假成功）：判据看返回里的 `execution` 字段——`executed:true` 有证据、`false` 是 RBS 明确报失败（此时 `success:false`、`errorCode:REMOTE_REPORTED_FAILURE`）、`null` 表示远端没给凭据、**无法判定**。疑似没跑时对比 `Player.log` 的行数/mtime 变化 |
```

- [ ] **步骤 4：运行测试验证通过**

工作目录 `MCP/`，运行：

```
node --test test/gabpExecutionVerdict.test.js
```

预期：`# pass 14`、`# fail 0`（任务 2 的 9 条 + 本任务新增的 5 条）。

- [ ] **步骤 5：跑全量确认没有回归**

工作目录 `MCP/`，运行：

```
npm test
```

预期：`# fail 0`。

- [ ] **步骤 6：改动清单（本项目不是 git 仓库，无提交动作）**

- 修改 `MCP/index.js`（`EXECUTE_DEBUG_ACTION_NOTE` + `ADD_USING_DIRECTIVE_NOTE` 两个常量 + `TOOL_USAGE_NOTES` 里 3 条条目，+26 行；**吐槽板 [17]/[18]/[25]/[33] 在这一层零代码改动**）
- 修改 `README.md`（第 172 行那一行）
- 修改 `MCP/test/gabpExecutionVerdict.test.js`（追加 5 条用例 + 3 行 import + 3 行 note 切片）

---

## 5. 向后兼容（务必先读完再动手）

**`success` 字段的语义变了吗？** 分两种情况，**没有一刀切地改含义，而是只在有据时收紧**：

| 场景 | 改动前 | 改动后 | 调用方影响 |
|---|---|---|---|
| 传输层失败（`ok=false`） | `success:false` + `errorCode:'GABP_ERROR'` | 完全不变（`MCP/index.js:2699`、`:2789`） | 无 |
| `execute_debug_action` + RBS 明确报失败 | `success:true`（**假成功**） | `success:false` + `errorCode:'REMOTE_REPORTED_FAILURE'` | 唯一的行为变更。任何读 `success` 的调用方会**少**收到假阳性；不可能有调用方依赖「RBS 说失败时仍报成功」 |
| `execute_debug_action` + 有凭据执行成功 | `success:true` | `success:true`（不变）**+ 多出** `transport`/`execution` 字段 | 读 `success` 无感；新增字段被忽略即可 |
| `execute_debug_action` + 无凭据 | `success:true` | `success:true`（不变）**+ 多出** `transport`/`execution`（`executed:null`） | 读 `success` 无感；但返回值里**不再有**任何地方宣称执行过 |
| **其余全部 GABP 镜像工具** | 原样 | 逐字节不变（任务 2 已用 `Object.keys(body)` 断言锁死） | 无 |

**为什么不改 `success` 的含义而是新增字段**：把 `success` 直接改成「执行成功」，在拿不到**已核实**正面凭据的前提下（见 §1、§0.2 待核对 A）会把每一次真成功都判成失败——那是本条问题的反面。本计划的取舍是「**有据才收紧，无据只说明**」。

**新增字段都是纯增量的**：`transport`（恒为 `'ok'`）、`execution.{tool,status,executed,evidence,remoteSuccess,logCount,remoteDurationMs,transportDurationMs,fastNoLog,mainThreadPendingBefore,mainThreadPendingAfter,logWindow,note}`。旧的 `data` 一字未动地保留在 `data` 字段里——**包括 RBS 自己的 `data.success`**（它与 MCP 信封的 `success` 是两个不同层级的字段，任务 2 已用断言锁死二者不被互相覆盖），远端报错原文（如 `data.exceptionMessage` / `data.exceptionType`）也不会被吞。

> ⚠️ **`data.success` 与 `success` 是两个东西，别在实现时合并它们**：`success` 是 MCP 信封对调用方说的（传输层/执行判定结果），`data.success` 是 RBS 说的（它自己的执行结论）。前者是本层加工过的，后者必须原样透传。

> ⚠️ **`transportDurationMs` 是本计划新引入的「以前拿不到」的字段**（[25]）。它只影响**判定范围外**的观测面、不改变任何既有字段的取值，但它带一个**运行时抖动**（`Date.now()` 的实际值每次不同）。若将来有人写「整份返回体逐字相等」的回归断言，必须把 `execution` 整体排除——任务 2 的逐字不变断言是**只针对非判定范围工具**的（那里整个 `execution` 都不存在），不受影响。

**其他会被顺带影响的地方（都核过，都是想要的行为）**：

1. **`MCP/index.js:5124-5132` 的 `tools/call` 日志行**：`outcome` 是从返回体的 `parsed.success` 推的。所以「RBS 明确报失败」的那类调用 henceforth 会在 `MCP/logs/*.log` 里记成 `→ 71ms error(REMOTE_REPORTED_FAILURE)` 而不是 `→ 71ms success`。**这正是我们要的**：日志从此不再把远端报失败记成 success。
2. **`MCP/index.js:4006-4019` `attachUsageOnFailure`**：JSON 业务错误体（`success===false`）会触发它注入 `suggestions`/`usage`。新增的 `REMOTE_REPORTED_FAILURE` 会走这条既有分支，附上调用范式建议——与 `stageError` 等既有业务失败的处理完全一致，不会产生新的返回结构。
3. **`MCP/index.js:3501-3512` `noteDpaStateAfterCall`**：只在 `rimworld.dpa_patch_methods` 与 `DPA_STATE_RESET_TOOLS`（`MCP/index.js:3480-3485`）里的工具上生效，**`execute_debug_action` 不在其中**，DPA 记账不受影响。
4. **`MCP/index.js:4310` 的 `create_allowed_area` 分支**：不在判定范围内，返回体不变，不受影响。
5. **MCP 协议层**：`callGABPTool` 返回的仍是 `{content:[{type:'text',...}]}`，没有设 `isError`。所以协议层行为不变，只有 JSON 正文变了。

---

## 6. 明确不做（以及为什么）

| 不做 | 原因 |
|---|---|
| 在 MCP 层用「耗时显著低于历史下限」判失败（吐槽板 [25] 的原建议） | **维持否决，但理由已升级为可复核的实测证据**（§0.3，数据来自 `MCP/logs/mcp-20261001-093829.log` 全部 1361 行中的 87 条调用）：`Actions\Hot Query Guard Tests` 6 次耗时 37/39/43/51/60/**72**ms 全是正常档，板上那条「72ms 阈值」会**成片误伤**；87 次里 14 次 < 133ms、分属 **7 个不同 action**；`Actions\Stage Test Map` n=36 跨 **3→60013ms**，所以「正常要 2097ms」只是单点、不是基线。**且日志里没有 ground truth**（`MCP\logs\*.log` 五个文件 grep 字面量 `logCount` 零命中，outcome 全是那个已知不可信的 `success`）⇒ 任何阈值都是拿一个点过拟合出来的 |
| **在 MCP 层内置 per-path 耗时基线表** | 本仓库**没有**这张表：`MCP/logs/` 里没有结构化耗时记录，MCP 也不落盘（`elapsed` 只在 `MCP/index.js:5132` 进日志）。要建就得先采集，而采集要起游戏 |
| **让调用方传一个 `fastThresholdMs` 进来** | 需要新增控制参数 + 在转发给 RBS 前剥离（照 `MCP/index.js:4344-4345` 的 `force` 做法），并且要定死「这个阈值是什么语义、与哪个 path 比」——而 path 基线不存在，语义无从定义。⇒ 等真有基线表了再单独立项 |
| **给 `execution.fastNoLog` 用布尔 / 让它产出 `true`** | 见 §6.2。`true` 会把实测里 7 类正常快动作整片染成「未执行」；布尔 `false` 则会让「拿不到耗时」反而显得可信——两个方向都在制造新的假阳性/假阴性 |
| **修吐槽板 [33]（`add_using_directive` 静默假成功）** | **不是同一条路径**（§0.5）：它走 `callUnityExplorerAPI`（`MCP/index.js:736`），不是 GABP。且 `MCP/index.js:806` 的 `success: response.ok && data.success` **已经**做了收紧 ⇒ 真正报 `success:true` 的是游戏侧 `ConsoleController.cs:145-146`（先 `Evaluate(..., supressLog:true)` 再无条件 `Add`，且 `:208-212` 把 `FormatException` 连警告一起吞掉）。本项目改不了它，只加一条文案提醒（任务 3），判据代码一律不加 |
| 包装「执行后轮询 `Player.log` 行数变化」的伪执行凭据 | `Player.log` 会轮转（吐槽板 [09]）、会被 `UELoader.OnUELog` 二次包装（[15]）、还会在主线程卡死时零写入——三条已记录问题会让它变成新的假判据源（裁决报告 §修法 5） |
| 让 `longEventPending` 与「是否入队」挂钩 | 入队发生在 RimBridgeServer 内部，源码不在本仓库；MCP 层只能**读**不能**挂**。只读版本已落在 `execution.mainThreadPendingBefore` + note 文案里（§0.1 第 2 条） |
| 把判定范围扩到 `spawn_thing` / `create_allowed_area` 等其他写操作镜像 | YAGNI。范围由 `EXECUTION_VERDICT_TOOLS` 一个常量控制，将来有实测依据再加，扩范围不需要改任何别的代码 |
| 改 `docs/mcp-吐槽板.md` 把 [01] 标成「已修」 | 吐槽板由主代理批注，子代理禁止写入 |
| 改 RimBridgeServer | 第三方模组，不在本仓库，且其行为无法核实 |
| **给 `execute_debug_action` 加 `logWindow{offset,limit}` 分页入参（吐槽板 [17]）** | **本仓库不可实施**：`50` 是 RBS 侧的硬编码字面量（`LogJournal.GetEntries(50,…)` → `source.Take(limit)`），而 `execute_debug_action` 的签名只有 `path/pawnName/pawnId/x/z/thingId`，**没有任何分页入参**——MCP 层往 args 里塞 `logWindow` 只会被 RBS 当未知参数忽略或报错。本计划能做的是**如实描述窗口**（`execution.logWindow`），让调用方知道「看到的可能是残缺的」，并指向唯一的补数途径（`rimworld.list_logs`） |
| **在 MCP 层自己缓存/合并 `effects.logs` 来「补齐」50 条窗口** | MCP 层只在这一次调用里看到这 50 条（`MCP/index.js:4350` 传 `filterResult=undefined`、`:2761-2762` 不进），没有跨调用的日志流可合并。要合并就得引入缓存与失效策略，那是**新的假凭据源**（缓存过期/串窗口），与本条修复的目的相反 |
| **给 `execution.logWindow.truncated` 用布尔** | 见 §6.1，用布尔就是**制造一个新的假阳性**（或假阴性）。详见下 |
| **把 `stateBefore.logSequence` 当作窗口下界去做精确补数** | **做不到**：RBS 的 `CaptureExecution` 把 `LogSequence` 存进 `ExecutionCapture.LogSequence`，回包给的是 `.State`——**`stateBefore` 里没有 `logSequence`**。调用方拿不到窗口下界，只能靠 `logs[].sequence` 推断。本计划如实写明这个限制，**不承诺做不到的补数能力** |
| **把 `list_logs{afterSequence:…}` 当成分页游标** | 它**不是分页**：`afterSequence` 只过滤更新的、**没有 `beforeSequence`**、且结果是**倒序**的。拿它当「取下一段」会漏数据 |
| **修吐槽板 [18]（UE 日志成对出现）** | 裁决为**不值得修、代码零改动**：成因在本项目 `Source/UELoader/ExplorerBootstrap.cs:195-218`（`OnUELog` 把每条 UE 日志用 `[UnityExplorer] ` 前缀写回 `Player.log`，见 `:204/:207/:210`），但它只是日志可读性问题，不影响任何判定。本计划只在文案里提醒一句 |
| **改 `Source/UELoader/ExplorerBootstrap.cs` 去掉 UE 日志回显** | 会破坏 UE 侧用户自己的日志排查路径，收益仅是「Player.log 少几行重复」，不在本条范围内 |

### 6.1 吐槽板 [17] 的三个硬约束（实现者最容易写歪的地方）

1. **`truncated` 不许用布尔。** 返回条数**恰好 50** 与「被截断」在信息上**不可区分**（RBS 不告知、且 `logCount = list.Count` 会跟着饱和在 50）。所以取值必须是 `false`（< 50，肯定没截断）/ `'possible'`（≥ 50，不确定）/ `null`（形状不对，无法描述）。
   **用 `true` 是凭空制造假阳性，用 `false` 是凭空制造假阴性——两者都正好毁掉本条修复的全部意义。**
2. **补数只能靠 `rimworld/list_logs{limit:2000}`**（RBS 的整个 ring），而且**必须写清它不是分页**：`afterSequence` 只过滤更新的、没有 `beforeSequence`、结果是倒序的。
3. **拿不到窗口下界。** `stateBefore` 里**没有** `logSequence`（RBS 存的是 `ExecutionCapture.LogSequence`，回包只给 `.State`），所以**无法**精确说出「本次动作对应哪一段日志」，只能靠 `logs[].sequence` 推断。**不要在文案或返回值里承诺做不到的精确补数。**

### 6.2 吐槽板 [25] 的 `fastNoLog`：同一条三态纪律，`true` 永不可达

`execution.fastNoLog` 的取值域是 `true | false | 'possible' | null`，但**本实现只可能产出 `false` 和 `null`**：

| 取值 | 含义 | 可达性 |
|---|---|---|
| `false` | 已有正面凭据（`status==='reported-success'`）⇒「短但有日志」不构成任何嫌疑 | ✅ 可达 |
| `null` | 无凭据 / 拿不到耗时 ⇒ **无法描述**，不要编一个值 | ✅ 可达（当前最常见） |
| `'possible'` | 需要一个本层拿不到的 per-path 阈值 | ⚠️ 当前**不可达**（`FAST_NO_LOG_THRESHOLD_MS` 恒为 `null`）；保留在取值域里只为不推翻调用方的类型判断 |
| `true` | —— | 🚫 **永不可达**：会把 §0.3 表里 7 类正常快动作整片染成「未执行」 |

**为什么 `'possible'` 也不做**：MCP 不内置阈值（§6 第 2/3 条），而「同 path 历史下界」这张表本仓库没有。硬凑一个全局阈值就是拿 2097ms 一个点去拟合 87 次调用——**过拟合出来的规则就是新的假阳性源**。

**配套的硬断言**（任务 1 有专门用例锁死）：同一份回包，`transportDurationMs=72` 与 `=2097` 的判定结果（`status` / `executed` / `evidence` / `note`）必须**逐字相同**。只要这条还在，「时间不参与判定」就没有被偷偷破坏。

---

## 7. 已知降级路径（提前说清，免得执行者以为实现坏了）

**如果 RBS 对「真的执行了」的动作也回 `effects.logCount: 0`**（即日志抓取不可靠），那么所有 `execute_debug_action` 调用都会落到 `unverified` / `executed:null`。这是**设计内的降级**：宁可「无法判定」，也不误报成功；且 `success` 仍是 `true`、既有调用方一行都不会坏。届时正确动作是把这个事实写进 `README.md`，而不是放宽判据——放宽会直接退回假成功。

**如果 RBS 日后新增了明确的执行凭据字段**，只需在 `MCP/executionEvidence.js` 的第 ② 步里加一个分支（判据优先于 `logCount`），测试文件里加一条用例即可，`MCP/index.js` 与 `README.md` 一行都不用改。

---

## 7.1 §0.2 那两条「待核对」的落地方式（**必读，别跳过**）

反编译给了很强的线索，但**本计划不把「待核对」的东西当断言**。以下是核对完成后要做的**确切三步**，每步都很小：

### 步骤 A：取一份真实回包（唯一需要起游戏的一步）

在游戏里跑一次 `agg_call_tool{tool:"rimworld.execute_debug_action", args:{path:"Actions\\Stage Test Map"}}`，把返回里 `data` 那段完整 JSON 存下来。**看五件事**：

| 看什么 | 若与反编译一致 | 若不一致 |
|---|---|---|
| `data` 里有没有顶层 `success` | 判据 A 确认 → 走步骤 B | 把它补进 §0.2，并把 `base.remoteSuccess` 的取值逻辑改到能反映真实形状 |
| `data` 里有没有 `operation` / `durationMs` | 待核对 B 确认「没有」→ 任务 1 的兼容分支可以保留但永远不触发，`remoteDurationMs` **恒为 `null`**（符合 §0.3 第 3 条的推理） | 有了 → 兼容分支变成真实判据，把 §1.1 的优先级表相应前移 |
| `data.effects.logCount` 与 `data.effects.logs.length` 是否一致 | 与 `logCount = list.Count` 一致，`describeLogWindow` 的实现不用动 | 不一致 → 先查清哪边才是真值，再改 `describeLogWindow`（**不要**顺手改判据） |
| **⚠️ `data.stateBefore` 里 `longEventPending` 到底是 `true` 还是 `false`、还是压根没有这个字段** | 若在一次**能确认动作没跑**的现场里它是 `false`（或缺失）⇒ **§0.4 的推断成立**：自旋不排队，代理无效。计划已经做了防御（note 纯追加式，见 §0.4 第 1 条），此处**只需把 fixture 的假设值改成实测值并更新 `NOT_EXECUTED_RBS_SHAPE` 的注释** | 若是 `true` ⇒ §0.4 的推断被推翻，note 的追加分支会真的帮上忙，fixture 保持原样即可 |
| **⚠️ `data` 里有没有任何「耗时」字段**（`durationMs` / `elapsedMs` / `timings`…） | 没有 ⇒ 板上那个 `72ms` 确系日志行误读，`remoteDurationMs` 永远是 `null`，而 `execution.transportDurationMs` 是**唯一**能拿到耗时的途径 | 有 ⇒ 把它接进 `base.remoteDurationMs`，并把 §1.1 那一行从「很可能恒为 null」改成「有时可得」 |

> **第 4、5 行是 [25] triage 新增的核对项，优先级不低于前 3 行**——它们分别决定 §0.4 的防御是否被触发、决定「耗时」这条线到底还有没有第二个数据源。

### 步骤 B：`PROMOTE_REMOTE_SUCCESS` —— **当前结论：保持 `false`（且已有反例）**

**⚠️ 这一节的结论在第 3 轮 triage 补充 [25] 之后被推翻了，请勿照旧执行旧版的三条条件。**

**现状（写死）：保持 `false`。** 理由不是「还没核对」，而是**已经有反例**：

- 吐槽板 [01]/[25] 的现场就是**顶层 `success:true`、动作压根没跑、72ms 秒回**。
- ⇒ 「顶层 `success:true` ⇒ 动作执行成功」这条判据**已经被 [01] 本身证伪过一次**。
- ⇒ §0.2 第 2 条反编译说的「主线程卡死时回不出响应（走超时）」**在这条现场上并不成立**——旧版条件 3（「确认卡死走的是超时而不是秒回」）**已经被这条现场否掉了**。

**要改判，需要的是能【推翻上述反例】的新证据**，具体是**一份同时满足下面三条的实测样本**：

1. 这次调用**能独立证明动作确实执行了**（例如：动作有可观测副作用，且副作用在回包前后可验证；**不接受**「日志里没报错」当证据）；
2. 这次调用的**顶层 `success` 是 `true`**；
3. 这次调用的**耗时同样很短**（例如 `< 200ms`）——即证明「秒回」并不总是假成功。

**三条同时满足**才有资格重新评估；即便如此，也**不是**自动打开，而是先把该样本作为新 fixture 加进 `MCP/test/fixtures/executeDebugActionPayloads.js`、让现有那 32 条用例继续全绿，再在 §0.2 记录新证据，最后才动那个常量。

**只改这一行**：

```javascript
export const PROMOTE_REMOTE_SUCCESS = false;   // ← 只有在步骤 B 的三条全中之后才改成 true
```

**同时**把任务 1 的用例「`[01] 顶层 success===true 单独出现时不判成功（默认不提升，等真实回包核对）」改名并改断言（把 `PROMOTE_REMOTE_SUCCESS === false` 改成 `=== true`，把 `executed` 的期望从「不是 true」改成 `true`）。**只改这两处**——`MCP/index.js` 与 `README.md` 零改动，因为它们已经透传整个 verdict。

### 步骤 C：用真实回包替换 fixture

把 `MCP/test/fixtures/executeDebugActionPayloads.js` 里的 `*_RBS_SHAPE` 三个样本换成真实抓到的（**脱敏**掉 pawnId / mapId / 存档名等），保留 `*_BOARD_SHAPE` 两个（它们证明老形状不会崩），保留 `LOGS_*` 两个（[17] 的样本与真实游戏无关）。

**⚠️ 替换时必须连带处理两件事，否则计划里的两条纪律会被悄悄破坏：**
1. **`NOT_EXECUTED_RBS_SHAPE` 的 `stateBefore.longEventPending`** —— 现在写的是**假设值 `true`**（§0.4）。必须改成步骤 A 第 4 行测到的实测值，并**同步改掉那段注释**（它现在明确写着「假设值，不是现场观测」）。若实测是 `false` 或缺失，note 的追加分支就不会触发——这是**预期行为**，不是 bug。
2. **耗时字段** —— 若实测回包里真的**没有**任何耗时字段，在 `*_RBS_SHAPE` 里**不要**补 `durationMs`（那会把一个「板上误读」洗成「已核实」）；`execution.remoteDurationMs` 保持 `null` 是正确结果，不是待修项。

然后跑 `npm test`——**全绿才算核对完成**。

> **顺序很重要**：A → C 先做（校准形状），B 再做（决定是否提升）。反过来会在没确认形状的情况下先改判据。

---

## 8. 自检结果（本计划写完后已逐条过一遍 writing-plans 的自检清单）

1. **规格覆盖度** —— 吐槽板 [01] 的三条建议逐条对照：
   - 「返回一个可判定的执行凭据（至少 `executed: true` + 实际执行耗时下限）」→ 任务 1 步骤 3 的 `executed` 三态 + `remoteDurationMs`（耗时只透传不当判据，理由已写明）；
   - 「主线程繁忙 / `longEventPending` 时显式 fail-fast，返回『未执行』而不是『成功』」→ 任务 1 步骤 3 第 ④ 步的 `mainThreadPendingBefore` 强化路径；**「fail-fast 判失败」这一半被明确否决**，理由写在 §0.1 第 2 条与 §6；
   - 「建议让 `longEventPending` 与是否排队挂钩」→ 已核实不可实施，理由与可实施的只读替代方案写在 §0.1 第 2 条。
   裁决报告 §修法 的 1/2/4 条（停止无条件盖章、拆成两个字段、文档层）全部落在任务 1/2/3；§修法 的 3（快耗提示）被 §6 显式否决并给出理由。
   **吐槽板 [17]**（日志窗口）落在任务 1 的 `RBS_EFFECTS_LOG_CAP` + `describeLogWindow` + 7 条用例，任务 2 的 `logWindow` 端到端用例，以及任务 3 的文案；三个硬约束写在 §6.1，四个「明确不做」（分页入参 / 本层缓存合并 / 布尔 truncated / 拿 `stateBefore.logSequence` 当下界）逐条列在 §6。**吐槽板 [18]** 按裁决**零代码改动**，只在任务 3 加一句不越界的文案提醒，并有 2 条守卫用例防止文案被写成「必然不同」。
   **吐槽板 [25]**（快耗时启发式）落在三处：§0.3 的实测证据（87 次调用的耗时分布 + 「日志里没有 ground truth」）→ §6 的**否决**（含理由升级）与两条新的「不做」→ 任务 1 的 `transportDurationMs` **纯透传** + `FAST_NO_LOG_THRESHOLD_MS` 恒 `null` + `fastNoLog` 三态（`true` 永不可达，§6.2）+ 8 条新用例。**板上那条「短耗时 + 零日志 ⇒ 判未执行」被明确否决。** 同时 §0.4 修掉了它顺带挖出的缺口（`longEventPending` 代理在 `OnGUI` 自旋场景下可能失效）：note 改成**纯追加式**，并有 3 条新用例锁死「结论完全不依赖 `longEventPending`」。
   **吐槽板 [33]**（`add_using_directive` 静默假成功）经**路径核对**后判定**不是同一条路径**（§0.5 的对照表）：它走 `callUnityExplorerAPI`（`MCP/index.js:736`）而非 GABP，且 `MCP/index.js:806` 的 `success: response.ok && data.success` **已经**做了收紧 ⇒ 本计划**只加一条文案提醒**（任务 3 + 1 条守卫用例），**不加任何判据代码**；真修法指向游戏侧 `ConsoleController.AddUsing`。
   **第 3 轮 triage 提的两条「待核对」**（顶层 `success` 的强度、`operation`/`durationMs` 是否存在）分别由 §0.2 记录、§1.1 的判据优先级表消化、`PROMOTE_REMOTE_SUCCESS` 开关承接。**⚠️ 其中「待核对 A」的处置在 [25] 并入后被改写**：`PROMOTE_REMOTE_SUCCESS` 从「不赌、等核对」改成「**已有反例、写死 false**」（§7.1 步骤 B 给的是「要改判需要什么新证据」）。**全程没有任何一处把待核对的反编译结论、或 [33] 的未实测假设，写成断言。**
2. **占位符扫描** —— 全文无「待定 / TODO / 后续实现 / 补充细节 / 添加适当的错误处理 / 类似任务 N」；每个代码步骤都给出了可直接粘贴的完整代码块；跨任务引用的 `assessExecution` / `describeLogWindow` / `finalize` / `EXECUTION_ERROR_CODE` / `EXECUTION_VERDICT_TOOLS` / `RBS_EFFECTS_LOG_CAP` / `PROMOTE_REMOTE_SUCCESS` / `FAST_NO_LOG_THRESHOLD_MS` / `execution.status` / `executed` / `evidence` / `remoteSuccess` / `transportDurationMs` / `fastNoLog` / `logWindow.truncated` / `note` 全部在任务 1 步骤 3 一次性定义，后续任务只引用不重定义。§7.1 的「待核对」也**不是占位符**——它给出了触发条件、确切改动行和执行顺序。
3. **类型一致性** —— `status` 的三个取值（`reported-failure` / `reported-success` / `unverified`）、`executed` 的三态（`true` / `false` / `null`）、`evidence` 的可能来源（`success` / `operation.Success` / `executed` / `effects.logCount` / `null`）、`truncated` 与 `fastNoLog` 各自的四态（`true` / `false` / `'possible'` / `null`，两者都只有 `true` 不可达）在任务 1 的实现、任务 1 的 **32** 条用例、任务 2 的 **9** 条用例、任务 3 的 **5** 条用例中逐字一致（合计 46 条）。
4. **行号纪律** —— 本计划出现的每一个 `文件:行号` 都用 `[System.IO.File]::ReadAllLines(...)[n-1]` 复核过（§0 开头已声明复核方法）。**执行者若要新增行号引用，请用同样方式复核，不要相信任何阅读工具打印的行号。**
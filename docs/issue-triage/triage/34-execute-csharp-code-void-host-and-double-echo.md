# [34] 裁决报告 —— `execute_csharp_code` 宿主是 `void`（`return "…";` → CS0127）与 `data.output` 双份回显

> 角色：TRIAGE。输入：`docs/mcp-吐槽板.md` 的 `## [34]`（2026-10-02，TEXT20 W6 · 收尾实测代理）。
> 必读对照：`docs/issue-triage/plans/2026-10-01-02-execute-csharp-code-output-capture.md`（下称「计划 02」）§0.8 / §0.9 / 任务 3 / 任务 4 / 任务 5 / 任务 6，全文 1371 行，**已逐节读完**。
> 全文行号均用 `[System.IO.File]::ReadAllLines(...)` 复核（见 §5）。

**裁决：值得修 —— 但只剩一处真缺口（一行 MCP 逻辑），且不单立计划，建议并入 [33]+[35] 合并计划**

---

## 0. 裁决摘要

| 项 | 结论 |
|---|---|
| 裁决 | **值得修**（≈80% 已被现有计划覆盖；残余缺口见 §3） |
| 严重度复评 | **🟠 中，维持**（不升 🔴：全部是编译期硬失败或已披露的截断，无假绿；不降 🟡：照文档写必然失败 + 落败后还会被**主动误导**） |
| 责任归属 | **本项目 MCP 层**（`MCP/index.js:4536-4537`）；文档面已由计划 02 任务 4 + 计划 12 任务 1 分工认领 |
| 归并锚点 | **不单立**。并入 [33]+[35] 合并计划，作为"错误码 `CS0127`"一行 + 一条验收项 |

---

## 1. 条目原文与定位

`docs/mcp-吐槽板.md`：

- 正文段 `## [34]`：**第 212-239 行**（标题 212，⑤状态 239 = **待修**）。
- 索引行：**:51**（🟠 中 / 待修）。

原文四个要点：

1. `:216-223` —— 项目里流传的写法是「`UnityEngine.Debug.Log("…"); return "…";`」（**前言里就是这么写的**），照抄立刻：
   `(20,1): error CS0127: 'InteractiveHost': A return keyword must not be followed by any expression when method returns void`
   ⇒ 用户代码被塞进一个 **`void` 方法**里，只有 `return;` 合法；**而工具描述 / `guidance` 里给的例子与返回体 `data.result` 又暗示「有返回值」**。
2. `:225-227` —— `data.output` 会把 `Debug.Log` 全文**原样回显一遍**，再跟 `result`（或 `Invoked REPL, result:` 那行）**重复一次**；
   「这与 [20] 同源，但这里是**强制的**（`void` ⇒ 只能靠 `Debug.Log` 出结果）」。
3. `:231-233` —— 影响：照文档写必然编译失败；被迫改成 `void` 之后只能从 `data.output` 读结果 ⇒ 与 [02] 叠加时**无法事后从 `Player.log` 自证**。
4. `:235-237` —— 建议：① 宿主改返回 `string`；或 ② 在 `guidance` 里明写"宿主是 void"；③ `output` 与 `result` 的重复回显只留一份。

---

## 2. 与计划 02 的一致性核对

### 2.1 结论：**一致。计划 02 已经把 [34] 当成已闭合的事实写进去了，且内部自洽。**

| [34] 的断言 | 计划 02 的落点 | 核对结果 |
|---|---|---|
| 宿主是 `void`，`return expr;` 必报 CS0127 | **§0.8**（`:161-188`）逐字抄了 CS0127 报文（`:165-168`），并给出三分叉表（`:174-180`）：末尾裸表达式 → `Host(object&)` 非 void 走 `out` 回值；纯语句 → `InteractiveHost` void；`return expr;` → CS0127 | ✅ 一致 |
| 「待核对：宿主是不是 void」这个悬项 | §0.8 首行「**已由 [34] 闭合**」（`:163`），并回指 `docs/mcp-吐槽板.md:212-239` | ✅ **已闭合，计划 02 里没有残留的"待核对"** |
| 工具描述暗示有返回值 | 任务 4（`:1030-1036`）整行重写 `MCP/index.js:2937`，首句就是「⚠️ 宿主方法 `InteractiveHost` 是 `void`：不要写 `return` 表达式……只有 `return;` 合法」 | ✅ 一致 |
| 唯一合法值通道是末尾裸表达式 | 任务 4 文案给出 `var sb = …; sb.ToString();` 正例；任务 3 验收矩阵第 1/4 行改用末尾裸表达式，第 6 行降级为"事实确认探针（预期就是 CS0127）"（`:967/:970/:972`） | ✅ 一致 |
| 双份回显 | **§0.9**（`:190-205`）逐条裁定"不是新缺口"：`[Unity] X` 被丢弃、`[UnityExplorer] [Unity] X` 被丢弃、`Invoked REPL, result:` 只进 `result`；剩下的"既在 output 又在 result"是**用户自己写了两遍** | ✅ 一致（我把 §0.9 的四条路径对着源码与计划正文又核了一遍，见 §2.2） |
| 「双份回显是**强制的**」 | §0.8 第 2 条 + §0.9 末段：**因为 `void` 宿主让 `result` 只吃末尾裸表达式 ⇒ `output` 升级为"承重的数据回传通道"**，任务 5/任务 6 从"省上下文的优化"变成"会挡住唯一出口的闸门"，两处文案都必须写明代价 | ✅ 一致，且**已经被写进任务 4 文案与任务 5 步骤 3 的 `description`** |

### 2.2 §0.9「双份回显不是新缺口」这条裁定，我独立复核后**维持**

逐条对照（本报告实际读到的代码/计划文本）：

| 第二份的来源 | 会被谁吃掉 | 依据 |
|---|---|---|
| `ExplorerCore.LogUnity` → `[Unity] x` | `UEExecLogCapture.AddUnityExplorerLog` 的 `UnityEchoPrefix` 丢弃分支 | 计划 02 `:577` |
| `ExplorerBootstrap.OnUELog` → `Verse.Log.Message("[UnityExplorer] [Unity] x")` → `Debug.Log` → `Application.logMessageReceived` | `AddUnityLog` 的 `SelfEchoPrefixes` 丢弃分支 | 计划 02 `:551` / `:560-563` |
| `Invoked REPL, result: X` | `Parse()` 命中 `ResultPrefix` 即 `continue`，只进 `result` | 计划 02 `:620-625`；发送侧 `ConsoleController.cs:180-183` |
| `Invoked REPL (no return value)`（void 宿主必发） | §0.5c 裁决：**丢弃** | 计划 02 `:128-138` / `:669-673`；发送侧 `ConsoleController.cs:185` |

⇒ **[34] 的"双份回显"在计划 02 落地后确实只剩一份**，`output` 内部不再重复。**该裁定成立，本报告不改。**

---

## 3. 计划 02 漏掉的缺口（[34] 带来的）

### 3.1 🔴 缺口 A：**`MCP/index.js:4536-4537` 的 `r.guidance` 全程未被计划 02 触碰，而它对 CS0127 给出的是"错的方向"**

**证据（静态可确证）：**

- 计划 02 §0.1 事实表把 `MCP/index.js:2937` 标注为「`execute_csharp_code` 的 `TOOL_USAGE_NOTES` 文案（**唯一要改的 MCP 行**）」（`:30`）；
- 计划 02 §1「修改」清单里 `MCP/index.js` 只有 4 处：`:2937`（`:227`）、`:2073-2082`（`:228`）、`:4539`（`:229`）、以及 `MCP/test/toolchainFixes.test.js`（`:230`）。
  **`:4536-4537` 不在其中**；全文检索计划 02，`:4537` / `guidance` **一次都没出现**（`4539` 的两次出现都是 `return r;` 与 `includeOutput` 插入点）。
- 而 `MCP/index.js:4536-4537`（本报告逐字读过）是：

  ```js
  // MCP/index.js:4536-4537
  if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || (typeof r.error === 'string' && r.error.includes('CS0')))) {
    r.guidance = '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';
  }
  ```

  触发条件 `r.error.includes('CS0')` 对 `'CS0127'` **必然为真**（`'CS0127'.includes('CS0')`）。

⇒ **[34] 的场景在计划 02 落地后变成这样**：用户照文档写 `return "…";` → CS0127 → 响应里 `guidance` 告诉他
「**编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名，或先用 `add_using_directive`**」。
**这正是 [34] 说的"工具描述/guidance 暗示有返回值"那一类误导，而且是指向另一个方向的误导。**
计划 02 任务 4 修好了静态文案，却把**运行时**那条更靠后的、每次失败都会读到的文案留在原地。

**⇒ 这是计划 02 相对 [34] 的唯一实质缺口。** 修法不需要新工具：**按错误码分流这一行**（`CS0127` → 讲 void 宿主；`CS0103/CS0234` → 讲命名空间；`CS0121/CS0117/CS1061/CS0411/CS1503` → 讲"成员/签名怎么查"，见 [35] 报告 §4.1）。这使它天然成为 33+34+35 三条共用 `:4537` 的理由。

### 3.2 🟠 缺口 B：计划 02 会让"**运行时异常**"也背上"编译失败 + 没有 using 引用"的错标签（**由计划 02 自己放大**）

- 计划 02 任务 2 步骤 4 的 `ExecuteCode`（`:882-890`）：`if (parsed.Error != null) return Error(400, parsed.Error, "COMPILATION_ERROR", extra);`
- 而 `parsed.Error` 同时覆盖两种东西（`Parse()` `:634-639`）：`Contains("Unable to compile")` **或** `Contains("Exception invoking REPL")`。
- 后者来自 `ConsoleController.cs:187-190`：**用户代码在 REPL 里抛的任何异常**都会被 `catch (Exception ex)` 记成 `Exception invoking REPL: {ex}`。

⇒ **一次纯运行时异常（NullReferenceException 之类）会以 `errorCode = "COMPILATION_ERROR"` 返回**，
于是 `MCP/index.js:4536` 的第一个条件命中 → 同样挂上「编译失败：默认没有 using 引用」。
这条错标签**今天就存在**（不是计划 02 引入的），但计划 02 把 `error` 通道整个重构了一遍却没顺手修它，
**而且它恰好是缺口 A 的同一个修法能一起带走的**（按 `error` 内容分流，而不是只看 `errorCode`）。

### 3.3 🟡 缺口 C：计划 02 认定"`output` 是唯一出口"，但**没有登记第二条出口**，而这直接关系到任务 6 的风险评估

§0.8 第 2 条与 §0.9 末段都写了"`output` 是承重通道，任务 5/6 是会挡住唯一出口的闸门"。
但计划 02 全文**从未提到 `throw`**：

- `ConsoleController.cs:187-190` 证明 `throw new System.Exception(bigString)` 在 `void` 宿主里是**合法语句**，
  异常会被记成 `Exception invoking REPL: …` → 进 `error`；
- 计划 02 的 `error` **不受 `MaxEntries` / `MaxOutputBytes` 约束**（字节上限的 `Add()` 双判据只作用于 `output` 的 `StringBuilder`，`error` 是单条赋值，见 `:1292` 与 `:637`）。

⇒ **存在一条未被 64KB 卡脖子的数据出口**。它不完美（会被标成 `COMPILATION_ERROR`，见缺口 B），
但足以让"大段文本只能走 `output`、而 `output` 会被腰斩"这条风险从**结构性**降级为**有绕路的**。
**建议 PLAN 把这一条写进任务 4 文案**（一句"要传出大文本且不愿被 64KB 腰斩，可 `throw new System.Exception(s)` 并从 `error` 读，注意它会被标成 COMPILATION_ERROR"）。
⚠️ **本报告没有实跑验证这条路**（禁止调 `execute_csharp_code`），依据是上述源码 + 计划 02 自身代码的静态链路。

### 3.4 🟡 缺口 D：Roslyn 切换后的"回头改文案"义务，计划 02 只登记了 1 处，实际有 3 处

计划 02 §0.6 表格末行（`:150`）与 §0.8 末段（`:182-183`）都写了
「Roslyn 计划任务 8 落地后**必须回头改写任务 4 的 notes 与 §0.8**」。
但同一义务还落在另外两处，计划 02 **没有登记**：

| 还需回头改的地方 | 归谁 |
|---|---|
| `MCP/index.js:2072`（工具 description）/ `:2078`（`code` 示例） | 计划 12 任务 1（`2026-10-01-12-mod-presence-assembly-visibility.md:199`、`:177`）——该计划自己在 `:50` 已记 CS0127，**但两处计划都没把"Roslyn 切换后要回改"写成交接义务** |
| `MCP/index.js:4537`（本报告缺口 A） | 无人认领 |

⇒ 建议在 33+34+35 合并计划里加一行**三方交接登记表**（谁的文案、谁先做、谁在后），避免第三次各改各的。

### 3.5 附带观察（不属于 [34] 的缺口，但与"能不能自证"相关）

计划 02 §2（`:251`）声称响应里的 `logCapture` 能把"有没有真的捕获到"变成调用方可自证的事实。
但 `BuildLogCaptureData()` 里 `unityDebugLogs` / `unityExplorerLogs` 是**硬编码 `true`**（`:511-512`），
**无论是否真的捕获到**。⇒ 唯一真实的证据只有 `entryCount` / `droppedCount` / `truncated`。
不影响 [34] 的裁决，但文案里不宜把那两个布尔说成"通道开着/关着"的证据。

---

## 4. 严重度复评 / 责任归属 / 归并锚点

### 4.1 严重度：**🟠 中，维持板上 `:51` 的值**

| 尺子（AGENT-BRIEF §5.2） | 判定 |
|---|---|
| 会不会得出**事实相反**的结论 | **不会**。`CS0127` 是硬失败（每次都红）；`output` 截断由 `truncated=true` 明写（计划 02 任务 4 文案已承诺） |
| 是不是每次都要重踩 / 系统性浪费往返 | **是**。照项目里流传的写法（`:216`）写就**必然**失败一轮；失败后还要再读一遍**方向相反**的 `guidance` |
| 是不是文档没写清而非代码错 | **是**（宿主是 `void` 是 mcs 求值器的既有行为，不是本项目引入的）⇒ 修复成本极低 |
| 计划 02 的 64KB 上限会不会把唯一出口堵死 | 结构性有风险（计划 02 自己承认），但**有绕路**（缺口 C）⇒ 不构成 🔴 |

⇒ 维持 🟠。**不升 🔴** 的理由要说清：最容易升 🔴 的是"任务 6 腰斩了唯一数据通道"，
但计划 02 已在 `truncated`/`droppedCount`/`outputBytes` 三个字段上**明写**了这件事（任务 4 文案 + 任务 6 的 `BuildLogCaptureData`），
按 COMMON.md「**零命中最危险的假阴性**」的同一原则，**已披露的截断不构成事实相反的结论**。

### 4.2 责任归属

| 面 | 归属 | 依据 |
|---|---|---|
| 工具 description / `code` 示例 | **本项目**，已由计划 12 任务 1 认领 | `2026-10-01-12-…:60`、`:101`、`:199` |
| `TOOL_USAGE_NOTES`（`:2937`） | **本项目**，已由计划 02 任务 4 认领 | 计划 02 `:1028`/`:1061` |
| **`r.guidance`（`:4536-4537`）** | **本项目，尚未被任何计划认领** | 见 §3.1 |
| `errorCode` 误标（运行时报 `COMPILATION_ERROR`） | **本项目游戏侧**（`Source/UELoader/UEHttpHandler.cs`，计划 02 任务 2 已在改这个方法） | 计划 02 `:882-890` |
| 宿主为何是 `void` | **UE 上游 mcs 求值器**，非本项目引入；Roslyn 计划任务 8 落地后会变 | 计划 02 `:182` |

### 4.3 归并锚点：**不单立计划**

理由：

1. **可动的只剩一行**（`:4536-4537`），单独为它开一份计划，成本与收益不成比例；
2. **这一行正是 [33]/[35] 的落点**（`CS0121/CS0117/CS1061` 与 `CS0234/CS1503/CS0411/CS0841` 都从这里返回），
   拆开写会得到两个互相覆盖的 guidance 分流表；
3. **必须避开的两处领地**（`MCP/index.js:2937` 属计划 02 任务 4；`:2072/:2078` 属计划 12 任务 1），
   合并计划只做"按错误码分流 `:4537` + 一张对照表 + 两条验收项"，冲突面为零。

**⇒ 建议 [34] 的内容在合并计划里落成三件事：**

| # | 内容 | 落点 |
|---|---|---|
| 1 | `CS0127` 进 `guidance` 分流表（讲 `void` 宿主、给末尾裸表达式正例） | `MCP/index.js:4536-4537` |
| 2 | `guidance` 分流顺带把「运行时报错被标成 `COMPILATION_ERROR`」这条标出来 | 同上 + 任务 4 文案一句 |
| 3 | 把 `throw new System.Exception(s)` 作为"绕过 64KB 的第二条出口"写进文案 | 任务 4 文案（`:2937` 领地 ⇒ **以"交接句"形式交给计划 02 的执行者并入**，合并计划自己不碰那一行） |

---

## 5. 检索范围声明与本报告的自我限制

**检索范围：**

| 结论 | 范围 |
|---|---|
| 计划 02 是否提到 `:4537` / `guidance` | **全文 1371 行逐节读完**（§0 全部小节、任务 1-6 全部步骤、§4 自检表）。判定依据是**通读**，非抽样 grep |
| 计划 12 对 `CS0127` 的既有认领 | `docs/issue-triage/plans/2026-10-01-12-mod-presence-assembly-visibility.md` 的 `:50`、`:60`、`:101`、`:177`、`:199`（**未逐字读完该计划全文**）⇒ 覆盖判定基于命中行 |
| `MCP/index.js` 相关行 | `:2070-2110`、`:4520-4580` 用 `[System.IO.File]::ReadAllLines` 逐行打印复核；`:2937`/`:3817`/`:4536-4537` 逐字读过 |
| UE 控制台源码 | `Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ConsoleController.cs` **全文 747 行读完**（`AddUsing :141-148`、`Evaluate :158-218`、`HELP_REPL :686-711`） |
| 板子行号 | `Select-String -Pattern '^## \[3[345]\]'` 定位 + `ReadAllLines` 复核；全文件 1459 行 |

**本报告明确没有做：**

- ❌ 未启动游戏、未调用 `execute_csharp_code`、未构建（AGENT-BRIEF §1.2/§1.3）。
- ❌ **未实跑验证 `CS0127` 报文本身**——CS0127 的存在由计划 02 §0.8（`:163-168`，已注明"已由 [34] 闭合"）、计划 12 `:50`、以及板上 `docs/mcp-吐槽板.md:219-223` 三方互证，本报告采信，未复现。
- ❌ **未实跑验证缺口 C 的 `throw` 出口**（§3.3 已标注为静态推断）。
- ❌ **未核实 `:4537` 那行 `guidance` 在真实失败响应里是否逐字出现**（MCP 日志不记载荷；禁止用 `execute_csharp_code` 复现）⇒ §3.1 的结论建立在**条件表达式逐字可读**上（`'CS0127'.includes('CS0')` 为真），这一点是无需运行即可判定的。
- ❌ 未用 `search_string_literals`；本报告所有检索均为本地文件读取/打印，模式原文已在正文引用处给出。
- ⚠️ 计划 02 §0.1 表格里的行号（例如 `UEHttpHandler.cs:163-245`、`ConsoleController.cs:183/185/189`）**本报告未逐条复核**（那是计划作者 2026-10-01 的复核结论）；本报告只在引用时按计划正文原样转述，未据此新增断言。

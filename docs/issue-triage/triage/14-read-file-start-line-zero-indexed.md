# [14] `read_file` 的 `start_line` 是 0-indexed —— 核精确行号时每条都差 1 行

裁决：值得修

## 2. 一句话结论

现象**属实且比板上写的更糟**（实测：不仅参数是 0-indexed，返回切片还会悄悄吃掉开头的空行，导致无法从返回值反推行号），但产生该行为的 `rimworld-source-online` MCP 服务器**不在本仓库内**，改不了它的代码 —— 因此值得修的是**本仓库内的文档与核证流程约定**，不是那个 server。

## 3. 核实过程

### 3.1 读了什么

| 文件 | 用途 |
|---|---|
| `docs/mcp-吐槽板.md:79-108` | 条目原文 |
| `docs/issue-triage/AGENT-BRIEF.md` | 铁律与裁决尺子 |
| `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md` | 查是否已被覆盖（grep） |
| `MCP/`（本仓库自己的 MCP 服务器，Node.js：`index.js` / `package.json` / `aggregator` / `longTask` / `stream-capture`） | 确认 `read_file` 归属 |
| 全仓库 grep `start_line`、`rimworld-source-online`、`read_file|search_source` | 定位实现 |

### 3.2 归属证据：`read_file` 不在本仓库

- 全仓库 grep `start_line` → **6 处命中，全部在 `docs/mcp-吐槽板.md`**（第 31/79/84/90/106/142 行），**代码里 0 处**。
- `MCP/` 目录 grep `read_file|start_line|search_source` → `No matches found`。该目录是本项目自己的 Node.js MCP 服务器（`rimworld-debug` 链路），**不提供** `read_file`。
- 全仓库 grep `rimworld-source-online` → **仅 2 处，均在吐槽板自身**（第 128、129 行）。

⇒ `read_file` / `search_source` 属于**外部 MCP 服务器 `rimworld-source-online`**，源码不在本仓库。
（**假设**：该 server 的仓库归属与维护权我没有查到公开来源，不排除是维护者本人所有；未证实。）

### 3.3 实测复现（8 次受控 `read_file` 调用）

工具描述本身即写明 `start_line` 是 *"0-indexed start line"*。用同一文件同一已知行号反复打靶：

**基准（1-based，来自 `search_source`）**：`Source/Verse/GizmoGridDrawer.cs:134` = `Log.ErrorOnce($"{arg} currentSelectable: {obj.ToStringSafe()}", 3427734);`

| # | 调用 | 返回首行 | 该首行的真实 1-based 行号 | Δ |
|---|---|---|---|---|
| 1 | `GizmoGridDrawer.cs  start_line=130, n=10` | `}` | 131 | +1 |
| 2 | `GizmoGridDrawer.cs  start_line=133, n=4` | `Log.ErrorOnce(...)` | 134 | +1 |
| 3 | `PawnRenderer.cs  start_line=158, n=5` | `public void SetStatuePaintColor(...)` | 159 | +1 |
| 4 | `PawnRenderer.cs  start_line=160, n=8` | `statuePaintColor = color;` | 161 | +1 |
| 5 | `PawnRenderer.cs  start_line=161, n=4` | `}` | 162 | +1 |
| 6 | `PawnRenderer.cs  start_line=163, n=4` | `GetBlitMeshUpdatedFrame(...)` | 164 | +1 |
| 7 | `PawnRenderer.cs  start_line=162, n=6` | `GetBlitMeshUpdatedFrame(...)` | **164**（应为 163） | **+2** ⚠ |
| 8 | `PawnRenderer.cs  start_line=162, n=2` | `GetBlitMeshUpdatedFrame(...)` | **164**（应为 163） | **+2** ⚠ |

**结论（由 8 次探针共同确定，无一例外）：**

1. **`start_line` 是 0-indexed**（`start_line = N` ⇒ 实际返回 1-based 第 `N+1` 行）。探针 1–6、8 完全一致。
2. **`search_source` 是 1-based**。⇒ 同一 MCP 服务器的两个工具基线**不一致**，板上说的「一次给 0-based、一次给 1-based」**属实**。
3. **`read_file` 返回不带任何行号前缀**（8 次调用全部如此）。板上说的「得自己数空行」**属实**。
4. **⚠ 板上没写到的增量问题：返回切片会丢掉开头的空行。**
   唯一打破 Δ=+1 的探针 7、8，起点都落在 `PawnRenderer.cs:163` —— 而 163 是一个**空行**（探针 4/5 已证明 162=`}`、163=空、164=`GetBlitMeshUpdatedFrame`）。
   若纯粹是 0-indexed，`start_line=162` 应返回 163（空行）为首行；实测首行是 164，**空行被静默丢弃**，且**返回行数不减**（探针 8 要 2 行仍给 2 行，等于把区间整体右移）。

### 3.4 板上给的复现步骤没有复现

板上 [14]「症状」写：`read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:162, line_count:22 }` 返回「**第一行是空行**，第二行才是 `GetBlitMeshUpdatedFrame`」。

我原样调用（`start_line=162, line_count=22` 与 `n=6` 各一次）：**首行直接就是 `GetBlitMeshUpdatedFrame`，前面没有空行**。空行在当前版本被吃掉了。
⇒ 板上的「第一行是空行」这个细节**未复现**；真正发生的是「空行被吃、首行右移到 164」。**更隐蔽**，因为读起来「差了一行」而不是「多了一行空行」。

### 3.5 试过、但**不可用**的替代方案

- `mcp__decompilerserver__get_source_slice` 支持 `includeLineNumbers: true`，实测能输出 `   1: public static void DrawGizmoGridFor(...)`。**但**它返回的是 `sourceKind: "decompiled"`，`DrawGizmoGridFor` 反编译后仅 84 行，而 `GizmoGridDrawer.cs` 原文有 449 行 ⇒ **它的行号是「反编译产物」的相对行号，与 `search_source` 的原文件行号不可互换**，不能拿来核原文件行号。
- `get_source_slice` 传越界区间（`startLine=128,endLine=140`，超出 84 行）直接报 `Start line cannot be greater than end line`。

⇒ **目前不存在「换个工具精确核原文件行号」的现成方案。**

## 4. 影响与严重度复评

**维持 🟠 中，不升 🔴。**

- **会给出事实相反的结论 —— 会。** 这正是它最危险的形态：审查者用 `read_file` 核完，**拿到的是错的一行**，却会据此得出「注释里的行号对不上」的**事实相反**结论（真相是注释没错、是工具读错了行）。
- **为什么仍不升 🔴**：它是**可检出的**假阴性 —— `search_source` 给出的行号是准的，只要**交叉核对**（同一行号在 `read_file` 与 `search_source` 各查一次）就会立刻暴露。🔴 的定义是「不可检出的假成功/假阴性」。本条可检出，属于「系统性浪费往返 + 高频踩」。
- **加重项（板上未提）**：因为**空行会被静默丢弃**，现有 workaround「自己数一遍空行再对」**本身不可靠** —— 返回值里根本没有那行空行可数，调用方**无法从返回内容反推起点**。这把它从「一个偏移常量」升级成「不可自校验」。
- **真实代价**：本项目的核心交付物就是「核 vanilla 行号」。每一次核证都要额外花一次 `search_source` 调用做交叉校验，且校验失败时没有明确报错，只会安静地给出错误的行。

## 5. 责任归属

**第三方（外部 MCP 服务器），不是本项目的锅。**

理由：
1. `read_file` / `search_source` 由 `rimworld-source-online` 提供，其源码**不在本仓库**（§3.2 已证：全仓库 `start_line` 0 处代码命中，`MCP/` 下 0 处命中）。
2. 本仓库的 MCP 服务器是另一条链路（`rimworld-debug`，Node.js），不提供该工具。
3. 0/1 基线不一致**纯粹发生在那个 server 内部**：它自己的 `read_file`（0-based）与自己的 `search_source`（1-based）就不自洽。**没有跨到本项目。**
4. 板上把 [14] 与 [13]（命名空间≠目录）并列 —— [13] 也是同一个 server 的路径猜测问题，同样是外部责任。

### 5.1 两个方案的权衡（板上要求）

| 方案 | 破坏性 | 评价 |
|---|---|---|
| **A. 返回值加行号前缀**（如 `[164] ...`） | **无** | ✅ **推荐**。纯增量；不碰 `start_line` 语义，所有既有调用方行为不变；直接把「不可自校验」变成「可自校验」，收益最大。唯一代价：返回值多占字符 —— 但相对 [03]（400KB 返回）量级可忽略。 |
| **B. `start_line` 改 1-based** | **有** | ❌ **不推荐**。会**打翻所有既有调用方**：任何按 0-based 书写的调用全部错位 1 行，且**静默出错**（正是本条要消除的失败模式）。属于典型的破坏性变更换一致性。 |

> 注意：若选择 B，还必须同步修 `read_file` 的空行丢弃逻辑，否则两条不一致会叠加。
> **推荐组合：A（加前缀）单独做即可，不要碰基。**

## 6. 修的话怎么修

**代码侧（本仓库做不到）**：`rimworld-source-online` 的源码不在 `MCP/` 下，§3.2 已证。若维护者确实拥有该 server，可选方案 A；若不拥有，跳过。

**本仓库内可做的（这才是值得修的部分）**：

1. **写进既有文档的约定**，不要新建零散笔记。建议落点：
   - `docs/issue-triage/AGENT-BRIEF.md`（§4 项目背景处）加一条「核行号工具约定」；**或**
   - `README.md` / `AI-DEPLOYMENT.md`（供 AI 会话读取的那份）加一节。二者需先确认哪份是 agent 的实际入口。
   约定内容必须写死三条，全部来自 §3.3 实测：
   - `search_source` 的行号是 **1-based**（可直接引用）。
   - `read_file` 的 `start_line` 是 **0-indexed**：`start_line = 目标1based行号 - 1`。
   - ⚠ **`read_file` 返回里没有行号，且开头空行会被丢弃 —— 严禁从返回内容反推起点行号。**

2. **规定强制交叉校验流程**（比约定更重要，因为它能自动消掉假阴性）：
   > 凡是「注释/计划里的 `:N` 行号是否属实」这类核证，**必须**以 `search_source` 的行号为准；
   > `read_file` 只用于看上下文，不得作为行号真值来源；两者不一致时以 `search_source` 为准并记录分歧。

3. **禁止把 `read_file` 返回的相对位置写进任何产出物**（注释、计划、报告），除非已用 `search_source` 二次确认。

4. **不要**新增「在 MCP 层给 `read_file` 包一层行号」的方案：§3.5 已证没有等价的上游工具可替代，包一层反而会固化一个错位基。

## 7. 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/` 下当前**只有 1 个计划文件**：`2026-09-27-ue-csharp-console-roslyn-upgrade.md`。
- 对该文件 grep `start_line|0-indexed|0-based|行号前缀|Player\.log` → **`No matches found`**。
- 该计划的 12 个任务（`## 任务 1` … `## 任务 12`）全部围绕 Roslyn 编译器升级 / C# 控制台 / 打包验收，与核行号工具链**零交集**。
- `docs/issue-triage/plans/` 目录当前**不存在**，无其它子代理的同类计划可参考或复用。

⇒ 需新写计划；但注意其产出形态是**文档约定 + 流程**，不是代码改造。
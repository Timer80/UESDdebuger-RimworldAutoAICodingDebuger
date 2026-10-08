# [53] REV-LEDGER 取证路径审计：那次截断错发生在「读 mod 本地文件」还是「拿到 vanilla 假阳性」？

> **产出角色**：TRIAGE（核实员）· **纯静态只读审计**
> **上游**：吐槽板 `[51]`（下游损坏描述）· `[50]`（假阳性隐患）· TRIAGE 报告 `triage/50-search-source-vanilla-only-index-scope.md` §6.2
> **被审对象**：`Mods\TEXT20\.superpowers\sdd\2026-10-01-原版寻路接管-缺口修复-总计划\W8-LEDGER-review.md`（审查者 `REV-LEDGER`）
> **本报告唯一允许的写入**：`docs/issue-triage/triage/53-rev-ledger-evidence-path-audit.md`

---

## 1. 裁决

**污染发生了吗：否。**

**REV-LEDGER 的取证路径 = 宿主文件系统工具（`Select-String` / `[IO.File]::ReadAllLines`），读的是 mod 本地 `FlightState.cs`，只取了该属性的第 1 行（`:20`）。**
**不是** `read_csharp_symbol{typeName:"FlightState"}` 的 vanilla 假阳性 —— 该假阳性**存在、且本会话实测复现**，但**它没有产生这次损坏**。

**判定类型：直接判定（报告里记了工具名），不是「不可考」。** 依据见 §3。

⚠️ **同时纠正一条已经写进吐槽板的错误归因**：`[51]` §③ 末尾那句
> 「该文件是 **mod 本地**的，MCP 根本取不到（见 [50]）——**这才是它被误读的真正原因**」

**这句不成立。** 按这句去理解，会得出「只要给 MCP 加 `root` 参数覆盖 `Mods/`，这次损坏就不会发生」的修法 ——
**而实际修好 MCP 之后，这次损坏照样会发生**（因为它压根不是 MCP 干的）。详见 §5。

---

## 2. 口径声明

| 项 | 口径 |
|---|---|
| **被审文件** | `W8-LEDGER-review.md`（396 行 / 35811 B / mtime **2026-10-03 16:57:27**） |
| **事实源文件** | `Mods\TEXT20\source\PathPlan\FlightAStar\FlightState.cs`（24 行 / 918 B / mtime **2026-09-18 12:29:37**） |
| **检索工具** | 宿主 `Select-String -SimpleMatch`（**不用 `grep` 正则**，遵守「按字面量匹配、不用 `|`」纪律） |
| **圈了什么范围** | ① `FlightState.cs` **全文 24 行**（`ReadAllLines`，非片段）· ② W8 目录**顶层 168 个 `.md`**（不递归）· ③ `W8-LEDGER-review.md` **单文件 396 行** |
| **行号复核** | **全部行号用 `[System.IO.File]::ReadAllLines(...)` 复核后写入**，未经复核者未落笔（`AGENT-BRIEF.md` §3.5） |
| **MCP 副作用** | **零**。本会话只调了 `mcp__rimworld-source-online__read_csharp_symbol`（只读、不动游戏状态）。**decompilerserver 一次未调用**，无 `contextAlias` 风险面。 |
| **写入** | **只新建了本文件**；TEXT20 工作区 **0 字节改动**（未 `write`、未 `edit`、未用 `pwsh` 改文件） |

---

## 3. 决定性证据：取证路径**被记下来了**（判定标准的第一档）

### 3.1 证据 ① —— 审查者自述的**工具名**（这就是「取证路径记录」）

**`W8-LEDGER-review.md:385`**（§7.3「我没做的事（按纪律声明）」）逐字：

```
 385: - **所有行号均现查**（`Select-String` / `[IO.File]::ReadAllLines` + 脚本化逐条回打），**未照抄任何文档里的行号**；本报告引用的每个 `:NNN` 都可按 §1 的哈希复现。
```

**口径**：`FlightState.cs:20` 是一个 `:NNN` 行号引用，落在「所有行号」的表述范围内
⇒ 这条声明**直接覆盖**了本次出问题的那次读取。
**记了工具名 ⇒ 按派单给的判定标准，这就是「直接判定」，不需要进入「不可考」分支。**

相邻的 `:383` 逐字（工具类别的旁证）：

```
 383: - **未 `git add` / `commit` / `reset` / `checkout`**；只用了 `git log` / `git status` / `git diff` / `git show HEAD:` 只读查询。
```

### 3.2 证据 ② —— 它**引用的是 mod 本地路径 + 精确到行的行号**

**`W8-LEDGER-review.md:124`** 逐字：

```
 124: 账本 `:401` 写「三相位全在 `IsInAir` 内 ⇒ 默认 AirborneActive 即可」。回代码核 `FlightState.cs:20`：
```

**`:20` 正是 mod 本地文件里 `IsInAir` 属性的首行行号**（§3.4 实测）。
⇒ 假阳性路径给不出这个行号：vanilla 返回的头是 `// File: RimWorld/Pawn_FlightTracker.cs (Lines 9-15)`，
**是一个区间头、不是单行锚点**，且指向的是**另一个文件**。

### 3.3 证据 ③ —— 负向：`W8-LEDGER-review.md` 里**零个 MCP 工具名**

`Select-String -SimpleMatch`，范围 = `W8-LEDGER-review.md` 全文 396 行：

| 模式 | 在 `W8-LEDGER-review.md` 的命中 |
|---|---|
| `read_csharp_symbol` | **0** |
| `rimworld-source-online` | **0** |
| `search_source` | **0** |
| `read_file` | **0** |
| `decompilerserver` | **0** |
| `contextAlias` | **0** |
| `Select-String` | **1**（`:385`） |
| `ReadAllLines` | **1**（`:385`） |

**⚠ 这个 0 命中不是「工具坏了」** —— 同一次扫描在**同一目录**上对其它文件有大量命中（§3.5），
`IsInAir` 命中 **346 次 / 78 个文件**、`FlightState` 命中 **149 次 / 50 个文件**。
⇒ **扫描本身有效，0 命中是真的 0。**

### 3.4 证据 ④ —— 逐字比对：报告里那句**是 mod 本地第 20 行的前缀**，而 vanilla 载荷里**根本没有 `IsInAir` 这个词**

**(a) mod 本地 `FlightState.cs` 全文 24 行（`ReadAllLines` 实测）：**

```
 20:         public bool IsInAir => Phase == FlightPhase.Takeoff || Phase == FlightPhase.Cruising
 21:             || Phase == FlightPhase.Hovering || Phase == FlightPhase.Landing
 22:             || Phase == FlightPhase.Falling;
```

**(b) 报告 `:127` 抄进代码块的那句，逐字：**

```
 127: public bool IsInAir => Phase == FlightPhase.Takeoff || Phase == FlightPhase.Cruising;
```

**(c) vanilla 假阳性的载荷 —— 本会话 `mcp__rimworld-source-online__read_csharp_symbol{ typeName:"FlightState" }` 实测，逐字：**

```
// File: RimWorld/Pawn_FlightTracker.cs (Lines 9-15)
	private enum FlightState
	{
		Grounded = 0,
		Flying = 1,
		TakingOff = 2,
		Landing = 3
	}
```

**逐项对比（这是本报告的核心判据）：**

| 比对项 | mod 本地 `:20` | 报告 `:127` | vanilla 载荷 |
|---|---|---|---|
| 含字符串 `IsInAir` | ✅ | ✅ | ❌ **一次都没出现** |
| 类型形态 | `struct` 内的属性 | 属性 | `private enum` |
| 成员数 | 5 个相位 | 只列 2 个 | 4 个枚举项 |
| 相位命名 | `Takeoff` / `Cruising`（+ `Hovering`/`Landing`/`Falling`） | `Takeoff` / `Cruising` | `Grounded`/`Flying`/`TakingOff`/`Landing` |
| 命名风格 | `FlightPhase.Takeoff`（**带命名空间限定**） | `FlightPhase.Takeoff`（**带限定**） | 裸成员名，**无限定** |

**⇒ vanilla 载荷里既没有 `IsInAir` 这个标识符，也没有任何 `Phase == ...` 形式的表达式。**
**报告 `:127` 那句不可能是它的产物 —— 假阳性路径在文本层面就被排除，不是靠推测排除的。**

**唯一一处差异（不改变结论，反而佐证）**：mod 本地 `:20` 行尾**没有分号**（`…Phase == FlightPhase.Cruising`），
而报告 `:127` 补了一个 `;`。
⇒ **恰恰是「只取到 `:20` 这一行、发现它不是完整语句、于是补分号把它凑成一句合法 C#」的形态** ——
**补分号这件事本身就是「我知道后面还有续行」的痕迹**；真拿到 vanilla 那段的人不会去补分号。

### 3.5 证据 ⑤ —— 那些 MCP 调用是**另一个代理**跑的，且它自己写明了归属

`W8-LEDGER-r2-report.md:594-601`（`LEDGER-R2` 修复轮，**与 REV-LEDGER 是不同代理**）：

```
 594: | **[51]** | `read_file` 的返回块**不带行号、也不告诉你块边界落在一条多行语句的第几行** …
 598: `search_source(query="IsInAir")` → `No results found`；
 599: `read_file("Source/RimWorld/FlightState.cs")` 与 `("Source/Verse/FlightState.cs")` → 均 `File not found`；
 600: 正控 `read_file("Source/Verse/GenGrid.cs", start_line=74, line_count=3)` → 成功（`Showing 3/279 lines`）。
 597: 📌 两条的证据**都是本轮真跑出来的**，不是推测：
 601: ⇒ [51] 里「`W8-LEDGER-review.md` §3.3 只读 `:20` 首行」这段**下游损坏**也已写进板子，因为它正是本轮 I-1 的起因。
```

**`:597` 明写「本轮」＝ `LEDGER-R2` 本轮**，不是 REV-LEDGER 那一轮。
⇒ **`[50]`/`[51]` 的 MCP 证据全部产生于 `LEDGER-R2`，与 REV-LEDGER 的取证路径无关。**

**这一条最容易被读反，所以单列**：板子 `[50]`/`[51]` 大量列着 MCP 调用记录，
**看起来**像是 REV-LEDGER 干的 —— **其实全部出自 `LEDGER-R2`（见上表逐条归属）**。

---

## 4. 结论：污染（vanilla 假阳性）**没有**发生；发生的是**截断**

```
           REV-LEDGER 的实际路径                        假阳性路径（未走）
   ┌────────────────────────────────┐         ┌──────────────────────────────────┐
   │ 宿主 Select-String /           │         │ read_csharp_symbol               │
   │   ReadAllLines  (review:385)   │         │   { typeName:"FlightState" }     │
   │            ↓                   │         │            ↓                     │
   │ 读到 mod 本地 FlightState.cs   │  ≠→     │ 返回 vanilla private enum        │
   │ （真值，24 行）                │         │ （无 IsInAir，4 个枚举项）        │
   │            ↓                   │         │            ↓                     │
   │ ✂️ 只取 :20 首行                │         │ ✂️ 自我截断（无文本可截）          │
   │            ↓                   │         │            ↓                     │
   │ 报告:127「只含 {Takeoff,        │         │ 报告写「只含 {Takeoff,Cruising}」 │
   │  Cruising}、Landing 不在其中」   │         │ ⛔ 该文本无法从上面载荷产生        │
   └────────────────────────────────┘         └──────────────────────────────────┘
```

**一句话**：`[50]` 的假阳性**是一条真实存在的、已复现的**隐患通道；
**但这次损坏不是从那条通道出来的** —— 它是 [51] 那条「只取 `:20` 首行」的通道出来的，
**两条通道各自独立成立，本次只走了后者**。

---

## 5. 对 `[50]` 严重度的影响：**不调整**，但要改一处**证据链表述**

### 5.1 严重度：**维持 🔴 高**，不降

理由：**这次污染没发生，不构成给 [50] 降级的理由。**

| 判据 | 结论 |
|---|---|
| **假阳性本身还在** | ✅ 本会话**实测复现**，返回内容与 `[50]` triage 报告 §6.2 抄录的**逐字一致**（含 `// File:` 头与 `Lines 9-15`）。**隐患未被证伪。** |
| **失败形态** | §3.8 ④b：**静默返错**（不是显式报错）⇒ **不可自纠正**，比零命中更危险。这条定性**不受本次审计影响**。 |
| **本次没被咬到 ≠ 不危险** | 这正是 §3.7 第 5 条「恒真断言伪装成判据」的反面：一个**尚未造成损失**的缺陷，不能因为「这次侥幸」就降级。 |

### 5.2 🚨 要改的是**归因**，不是严重度 —— `[51]` §③ 的最后一句是错的

板子 `[51]` §③ 逐字：

> ⚠️ 附注：该文件是 **mod 本地**的，MCP 根本取不到（见 [50]）——**这才是它被误读的真正原因**。

**这条附注与 `[51]` 自己的主体陈述自相矛盾**：

- `[51]` 主体说病因是「`read_file` 按 `line_count=1` 取跨行定义」；
- 这条附注说病因是「MCP 取不到 mod 本地文件（[50]）」。

**而 §3 的证据表明：REV-LEDGER 压根没用 MCP 读这个文件**（`review:385` 自述用宿主工具）。

**⇒ 按附注去修，会得到一个「修好了但没修掉」的假绿**：
给 MCP 加 `root` 参数 / 改提示之后，**这次损坏仍会原样重演**（一个审查者照样可以只读 `:20`）。

### 5.3 建议的最小改动（**供主代理写进计划，本会话不碰板子**）

1. **`[51]` 的附注改写**：「REV-LEDGER 那次走的是**宿主** `Select-String`/`ReadAllLines` 取 `:20` 首行，
   **不是 MCP 取不到导致的**。MCP 取不到是 `[50]` 的问题，与这次损坏**没有因果关系**。」
   （按 `AGENT-BRIEF.md` §3.8 ②：观测到「MCP 查不到」不等于「那就是原因」。）
2. **`[50]` 本体不动**（严重度 🔴 维持），但可在「实测方」行后加一句：
   **该隐患本会话实测复现，但至今未在 TEXT20 侧造成实际损坏**（最近一次疑似受害者经查是 [51] 型，非本型）。
3. **`[50]` 建议 ②（`read_member(path, memberName)` 返回完整成员体）价值上升** ——
   它能**同时**堵住 [51] 型与 [50] 型两条通道，是两条里唯一「一次修两条」的退路。

---

## 6. 纪律自检（三件套 · §3.8 ④）

本报告未写任何未经三件套支撑的「不存在 / 查不到」结论。已用到的两处零命中，完整三件套如下：

### 6.1 零命中 A：`W8-LEDGER-review.md` 中无 MCP 工具名

| | |
|---|---|
| **① 工具 + 参数** | 宿主 `Select-String -LiteralPath <W8-LEDGER-review.md> -SimpleMatch -Pattern <9 个模式>`，**逐个字面量**，无 `\|` |
| **② 圈了什么范围** | **单文件全文 396 行**（`ReadAllLines` 实测）。**未**扩到 W8 目录，也**未**扩到 TEXT20 全树 |
| **③ 权威来源本应在哪里** | **REV-LEDGER 那一轮代理会话的工具调用日志**（transcript）。⚠️ **该日志不在 TEXT20 工作区落盘** —— 我扫的是**它自己的产物**（报告 + 台账），**不是**它的调用记录。⇒ 本结论的边界是：**产物层已可判定**（§3.4 的逐字比对独立成立，不依赖日志）；**调用层的逐条原样记录不可考**（没有落盘副本）。 |

### 6.2 零命中 B：`AGENTS-REGISTRY.md` 中无 `REV-LEDGER` 登记

| | |
|---|---|
| **① 工具 + 参数** | `Select-String -LiteralPath AGENTS-REGISTRY.md -SimpleMatch -Pattern 'REV-LEDGER','LEDGER','FlightState','IsInAir','contextAlias','反编译'` |
| **② 圈了什么范围** | 单文件 **79 行 / 5851 B / mtime 2026-10-03 19:56:26**（该表「进行中」列**最后更新于 13:02**，见 `W8-ledger.md:270` 转述的登记表滞后问题） |
| **③ 权威来源本应在哪里** | `W8-ledger.md` 的「本棒分派台账」节 —— **`W8-ledger.md:684` 逐字登记了 `REV-LEDGER`**：<br>`| 3 | **审查者** | \`REV-LEDGER\` | 只读 | 🔄 在跑 |`。⇒ **登记表存在、但只记「谁在跑」，不记「它调了哪些工具」** —— 这是**登记表的设计粒度**，不是我找不到。 |

### 6.3 其它纪律

- ✅ **未改任何既有文件**：TEXT20 工作区 **0 写入**（`Get-Item` 的 mtime 全部停在审计前的值，`FlightState.cs` 仍是 `2026-09-18 12:29:37`）。
- ✅ **未用 `pwsh` 改文件**；本报告分两次 `write` 完成，**先落骨架再补全**，未用 shell 做行级重排（§3.5 末条）。
- ✅ **未用 `Measure-Object -Line`**；行数一律 `ReadAllLines(...).Count`。
- ✅ **所有写入本报告的行号**都来自 `[System.IO.File]::ReadAllLines(...)` 的逐行枚举输出，非 `read` 工具的行号（§3.5 第二条坑）。
- ✅ **吐槽板定位用标题锚点**（`## [50]` / `## [51]` 正则切块），未用行号定位板子条目。
- ✅ **检索 query 无 `|`**，不触发「带 `|` 的结论一律无效」。
- ✅ **分区统计先圈范围再数**：§3.3 的表**只圈了 `W8-LEDGER-review.md` 单文件**；W8 目录 168 文件的统计**另列为对照**，两者**不混算**（避免 §3.8 ① 的口径错）。
- ✅ **未把「内容文本」当「数据结构」**：§3.4 的比对是**按标识符逐字比**，不是按词频/语义猜。
- ✅ **decompilerserver 一次未调用**；唯一的 MCP 调用 `read_csharp_symbol` 为只读、不动游戏状态。
- ✅ **未编译、未启动游戏、未调 `execute_csharp_code` / `execute_debug_action` / `start_*`**。

---

## 7. 一句话交回主代理

**污染没发生。** REV-LEDGER 用的是**宿主 `Select-String` / `ReadAllLines`**（`W8-LEDGER-review.md:385` 自述）读 **mod 本地** `FlightState.cs`，**只取了 `:20` 首行**；
vanilla 假阳性载荷里**连 `IsInAir` 这个词都没有**（本会话实测复现，只有 4 个枚举项），**在文本层面就排除了第二条路径**。
**`[50]` 严重度维持 🔴 不降**，但 `[51]` 末尾「这才是它被误读的真正原因」的归因**要改** —— 照它去修 MCP 会得到一个修不到点上的假绿。

---

*审计：TRIAGE 子代理 · 只读 · 未改 TEXT20 工作区任何文件 · 未动游戏状态*
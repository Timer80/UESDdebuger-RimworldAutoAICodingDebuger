# TRIAGE 报告 · 条目 [47] 扩展方法在按 declaringType 索引的工具里查不到

**裁决：值得修（🟠 中）**
**归并锚点：并入计划 11 零命中出处纪律 + `triage/50-…md` §5.3；不单独立计划**

## 1. 一句话结论

**症状成立，逐字复现，正控成立。** `IntVec3.InBounds` **是 `Verse.GenGrid` 上的扩展方法**，
按 `declaringTypeFilter="Verse.IntVec3"` 查 → `items:[] · totalEstimate:0`。
**但「一律查不到」这个说法要收窄**：不是工具看不见扩展方法，而是
**`declaringTypeFilter` 的语义本就是「声明类型」，而扩展方法的声明类型是静态类、不是被扩展类型**
—— 这不是索引缺陷，是**参数语义与语言语义的错配**，且**可以一句话修好**。

## 2. 核实过程（含检索范围）

**工具**：`mcp__decompilerserver__*`（**每次显式带 `contextAlias:"vanilla"`**，MVID `61e4173561894da49d210260257b5097`）
+ `mcp__rimworld-source-online__*`（不触碰共享状态）。**未抢 `current`**（无 `load_assembly` / `select_context`）。

### 2.1 反编译侧

| # | 调用（均带 `contextAlias:"vanilla"`） | 返回（逐字） |
|:--:|---|---|
| 1 | `search_members(query="Bounds", declaringTypeFilter="Verse.IntVec3")` | **`{"items":[],"hasMore":false,"totalEstimate":0}`** |
| 2 | `search_members(query="Valid", declaringTypeFilter="Verse.IntVec3")`（**正控**） | **`totalEstimate: 2`**：`040002F7:F` `Invalid`（**Field**）/ `17000106:P` `IsValid`（**Property**） |
| 3 | `search_members(query="Valid", declaringTypeFilter="Verse.IntVec3")` 的成员详情（#2 返回体） | `IsValid` 的 `kind` = **`"Property"`** |

⇒ **#1 与 #2 的对照是全部要害**：同工具、同声明类型、同一轮，**一个 0 命中、一个 2 命中**
⇒ **通道有效**；`Bounds` 的 0 **对实例成员是真阴性**，对「调用可行性」是假阴性（板上判断正确）。

**⚠️ #2 还复现了一个关键细节**：`IsValid` 的 `kind` 是 **`"Property"`** ——
这直接坐实了板上 §① 的论点：`dest.IsValid(map)` 是**给一个属性加了实参**，编译期必得 `CS1955`。

### 2.2 在线源码侧

| # | 调用 | 返回（逐字） |
|:--:|---|---|
| 4 | `search_source(query:"public static bool InBounds\(this IntVec3")` | `./Source/Verse/GenGrid.cs` → **`75:	public static bool InBounds(this IntVec3 c, Map map)`**、**`85:	public static bool InBounds(this IntVec3 c, Map map, int contractedBy)`** |
| 5 | `search_source(query:"public bool IsValid", file_pattern:"Source/Verse/IntVec3.cs")` | `./Source/Verse/IntVec3.cs` → **`39:	public bool IsValid`** |
| 6 | `search_source(query:"public static bool InBounds")`（**穷举全部 InBounds 扩展**） | **4 条**：`Gen.cs:375` `InBounds<T>(this T[,] array, int i, int j)` / `GenGrid.cs:75` / `GenGrid.cs:85` / `GenGrid.cs:95` `InBounds(this Vector3 v, Map map)` |

⇒ **板上三条读数（`GenGrid.cs:75`、`IntVec3.cs:39`、`kind:"Property"`）全部逐字复现。**

### 2.3 📌 「一律查不到」需要收窄的一处

[47] 标题说「扩展方法在『按类型搜成员』的工具里**一律**查不到」。
按 #6，vanilla 里名为 `InBounds` 的扩展方法共 **4 个**，
它们的**被扩展类型**是 `T[,]` / `IntVec3`（×2）/ `Vector3`。
⇒ 严格说，**只要用 `declaringTypeFilter` 指向被扩展类型，这 4 个一律查不到；指向静态类则能查到。**
「一律」应理解为「**凡是以被扩展类型作 `declaringType` 的查询，一律查不到**」——
这个收窄很重要，因为它决定了**修法是改语义还是改索引**（§6）。

## 3. 索引行读数是否无出处

**核实结果：读数全在板上，且本轮全部独立复现；只有「原文出处」无法核实。**

| 读数 | 在板上 | 本轮 |
|---|---|:--:|
| `search_members(declaringTypeFilter="Verse.IntVec3", query:"Bounds", contextAlias="game")` → `items:[] · totalEstimate:0` | 索引行 L23 | ✅ 复现（#1，alias 换 `vanilla`，同 MVID） |
| 正控同轮 `query:"Valid"` 返 **2** 条（`Invalid` 字段 + `IsValid` **Property**） | 索引行 L23 | ✅ **复现且 kind 逐字吻合**（#2/#3） |
| `search_source("InBounds\(Map map\)")` 只回 `CellRect.cs:445` | 索引行 L23 | ⚠️ **未复跑该 query**（本轮用 #4/#6 达成了更强的目标：枚举出全部 4 个 `InBounds` 扩展） |
| `GenGrid.cs:75` / `:85` 声明 | 索引行 + 正文 ① | ✅ 复现（#4） |
| `IntVec3.cs:39` `public bool IsValid` | 索引行 + 正文 ② | ✅ 复现（#5） |
| `W8-docbackfill-report.md §1.3 台账 W8-E18` 等 | 正文「原文」行 | ❌ 读不到（TEXT20 工作区） |

**⇒ 正文说「此处不做扩写」的那两条（`search_members` 逐条读数 + 正控 2 条），
在索引行里是齐的，本轮也复现了。** 与 [46] 同构：**板上没记 ≠ 板上没有**。

## 4. 🚨 是否覆盖 33-35 计划 :129「会误绑到 Map 的扩展」那句

**父问题的直接回答：这一条不属于 33 号报告，它在 `plans/2026-10-02-33-35-…md:129` 与 `triage/35-…md:45`；
[47] 确实击穿了那条判定的【证据链】，但没有推翻它的【结论】。**

**证据链（`plans/2026-10-02-33-35-…md:129` 原文口径）：**

> | ✗3 | 吐槽板 `:64` 括号 | 「无 using 时 `x.InBounds(y)` 会误绑到 `Map` 的扩展」 | **🔴 错**。
> `search_members(query:"InBounds", declaringTypeFilter="Verse.Map")` → **0 命中**（F8），
> `Verse.Map` 上没有名为 `InBounds` 的成员、更没有同名扩展 | 删掉这句。 |

**问题**：用来证明「**没有**同名扩展」的那个证据，是一次 **`declaringType` 过滤的查询**。
而 [47] 证明的恰恰是：**`declaringType` 过滤在原理上看不见扩展方法。**
⇒ **这是一个不可失败的断言**（AGENT-BRIEF §3.7 第 5 条）——
**无论 `Verse.Map` 上有没有 `InBounds` 扩展，那次查询都会返 0** ⇒ **它作为「不存在」的证据，判别力 = 0。**

**但结论本身是真的。本轮用一把能失败的尺子重验（§2.2 #6）：**

| 调用 | 结果 | 判定 |
|---|---|---|
| `search_source(query:"public static bool InBounds")`（**全仓，不按 declaringType 过滤**） | 4 条，接收者分别为 `T[,]` / `IntVec3` / `IntVec3` / `Vector3` | — |
| `search_source(query:"InBounds\(this Map")`（**全仓，正则锚定被扩展类型**） | **`No results found`** | ✅ **没有任何以 `Map` 为接收者的 `InBounds` 扩展** |

⇒ **「`x.InBounds(y)` 不会误绑到 `Map` 的扩展」这个结论成立**，33-35 计划要**删掉那句话的处置也是对的**。
⚠️ **但它的依据必须换掉**：不能写在 `search_members(declaringTypeFilter=…) → 0 命中` 上。

**⇒ 对父问题的结论**：
1. **33-35 计划 `:129` 那条判定的【结论】保留；【证据】必须替换**为全仓、无 `declaringType` 过滤的检索。
2. **`triage/33-…md` 本体没有这句话**（本轮全文检索 `误绑|扩展方法|extensi`，33 号仅 `:54` 提到扩展方法形态的 `GetRegion`）；
   **该句在 `triage/35-…md:45` 与 33-35 计划 `:129`。**
   ⇒ **需要补「通病表述」的是 35 号报告与 33-35 计划，不是 33 号。** 建议主代理据此更正派单里的落点。
3. 33 号 `:54` 那条本身**不受影响**且**恰好是对的**：
   它说全程序集 `GetRegion` 8 条命中、只有一条是 `GetRegion(this IntVec3, …)` 扩展 ——
   **这正是「放开 declaringType 过滤的全仓检索」**，方法正确。✅

## 5. 影响与严重度复评

- **真实性**：✅✅ 逐字复现，正控充分，机制明确。
- **严重度：🟠 中（维持索引口径）**。但**必须按「后果形态」分档**，因为它有两种截然不同的后果：

| 后果形态 | 判据 | 是否触发 | 严重度 |
|---|---|---|---|
| **假绿**（调用方改写成 `dest.IsValid`） | `IsValid` **只判 `y >= 0`、不查地图边界**（`IntVec3.cs:39`，本轮 #5 已确认它是 Property）；照写 `dest.IsValid` **能编译** | ✅ **成立** | 🔴 |
| **编译期硬失败**（照 `dest.IsValid(map)` 写） | 参数个数与属性不符 ⇒ `CS1955` | ✅ 成立（这是原始症状） | 🟠 |

⇒ **同一份零命中，走向「假绿」还是走向「编译失败」，取决于调用方下一步写什么。**
本条索引记 🟠 中，是按**发现时**的形态（编译期硬失败，每次都红）记的；**一旦按 `IsValid` 落地就升 🔴**。
**⇒ 本报告维持 🟠 中，但建议在规范里写明这条分档**，而不是笼统写一个严重度。
- **是否设计使然**：**部分是，但极易修**（§6.1）。这是**语义错配**，不是能力缺失。
- **可修性**：**工具侧一句话可修**；本项目侧规范零成本。

## 6. 责任归属

| 侧 | 归属 |
|---|---|
| `decompilerserver`（`search_members` 工具描述/零命中提示） | 缺陷所在 |
| 本项目 | 出规范：**查「某类型有没有 X」时，`declaringType` 过滤的 0 命中不等于该 API 不可调用** |

## 7. 修的话怎么修

### 7.1 工具侧（最小改法，按性价比排序）

1. **改 `declaringTypeFilter` 的文档语义**（最低成本，直接消灭误解）：
   「按**声明类型**过滤。**扩展方法的声明类型是静态类**（如 `IntVec3.InBounds` 声明在 `Verse.GenGrid` 上），
   **用本参数查被扩展类型将必然 0 命中。**」
2. **`search_members` 0 命中时补一句**：`query` 命中 0 且本轮存在同名扩展方法时，回一句
   「该名字有 N 个扩展方法，声明在：`Verse.GenGrid:75` / `:85` …」。
3. **兜底检索**：`query` 追加一次「全仓 `public static` + 首参 `this`」的扫描（成本较高，建议只在 0 命中时触发）。

**⚠️ 修法取舍**：**不建议**把扩展方法「归并」进 `declaringTypeFilter`（那会污染语义、且与 [29] 的重名解析问题叠乘）。
**推荐 1 + 2**：改语义 + 零命中时给线索，**成本最低、不引入新歧义**。

### 7.2 本项目侧（落进规范，零代码）

> **「按 `declaringType` 过滤查不到」≠「这个 API 不存在」—— 尤其对扩展方法，它必然查不到。**
> 判断某 API 能否在某类型上调用，须做**第二次不带 `declaringType` 过滤的全仓检索**，
> 看是否存在 `public static … (this <该类型> …)` 形态的声明。
> ⚠️ **特别警告**：`X.IsValid` 这类**只判部分条件**的成员，被误当成 `X.InBounds(map)` 的替代品会得到**恒真断言（假绿）**。

## 8. 与既有计划 / 报告的关系

| 对象 | 关系 |
|---|---|
| `plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md` | ⚠️ **需修订 `:129`**：结论保留，**证据换成无 `declaringType` 过滤的全仓检索**（本报告 §4 给了现成的两条调用） |
| `triage/35-no-using-member-name-resolution-guessing.md:45` | ⚠️ 同上，**该处也需要补一句通病表述** |
| `triage/33-intvec3-…md` | ✅ **本体不需要改**（见 §4.2 第 3 点）。父问题里的落点需更正到 35 号 |
| `triage/50-search-source-vanilla-only-index-scope.md` §5.3 | ✅ **已把 [47] 与 [50] 归为同一病根「工具的索引范围 < 语言的可见性范围」**，建议本条沿用该措辞，**不要另起第三套说法** |
| `plans/2026-10-01-11-…`（零命中出处） | **共享落点**：47 号是「零命中 ⇒ 不存在」的又一种形态，但**机制不同**（45 号是模式错，47 号是参数语义错）⇒ **建议分两条写，不要合并成一条** |
| `docs/mcp-source-lookup-conventions.md` | 🚨 **不存在**（本轮 `File.Exists = False`）⇒ 规范落点被阻塞，**只能改计划** |

## 9. 检索范围声明与自我限制

| 结论 | 范围 |
|---|---|
| `search_members` 的 0 / 2 命中 | decompilerserver，**每次显式 `contextAlias:"vanilla"`**，MVID `61e4173561894da49d210260257b5097`。**仅 vanilla 本体**（`Assembly-CSharp.dll`），**未检索任何模组程序集** ⇒ 「`Verse.Map` 上无 `InBounds` 扩展」**只对 vanilla 成立** |
| 「全部 `InBounds` 扩展共 4 个」 | `search_source(query:"public static bool InBounds")`，范围 = **该服务全部 vanilla `Source/` 语料**。⚠️ **只覆盖 `public static bool InBounds` 这一形状**；`internal` / 非 `bool` 返回 / 别的修饰符的同名扩展**不在本次枚举内** |
| 「没有以 `Map` 为接收者的 `InBounds` 扩展」 | `search_source(query:"InBounds\(this Map")` → `No results found`，同上一行范围。⚠️ **正则只覆盖 `InBounds(this Map` 这一字面形状**，参数名不同的写法不在内 |
| 板上 [47] 正文 / 索引行定位 | 标题锚点；索引节 **L14–L125** 圈定后取列 |

**板子快照（⚠️ 本轮内漂移）**：初读 **2154 行** / `mtime = 2026-10-03 20:41:37`；复读 **2190 行** / `20:50:37`。
`## [47]` 位置**未变**（L319）。⚠️ 不圈范围地匹配索引行正则还会命中 **L759 起的 `## 🤖 Triage 台账`**（另一张表）。

**自我限制**：

- ❌ 未修改任何既有文件；未编译、未启动游戏、未调用改状态的 MCP 工具。
- ❌ 未抢 decompilerserver 的 `current`；全部调用显式带 alias。
- ⚠️ **未复跑索引行记的那条 `search_source("InBounds\(Map map\)") → 只回 CellRect.cs:445`**
  （本轮改用了 §4 的更强检索，目标是判定「有无以 Map 为接收者的扩展」，该 query 的读数本身不影响结论）。
- ⚠️ **未核实 `W8-docbackfill-report.md §1.3 W8-E18`**（TEXT20 工作区，读不到）；
  也**未核实 `Demo/DegeneratePatherPathTests.cs:1293-1297` 与「全仓 82 处在用」**这两条正文引用的计数。
- ⚠️ 本报告说「修法 1+2 成本最低」，是**基于本轮读到的工具返回结构**的判断，**未审阅 `decompilerserver` 源码**（不在仓库内）。


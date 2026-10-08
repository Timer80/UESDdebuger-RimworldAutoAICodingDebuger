# TRIAGE 报告 · 条目 [29] `get_members_of_type` 静默漏项却报 `hasMore:false` + `get_overloads` 不返回被查者

**裁决：值得修**
**归并：不并入计划 10。两条「接口不一致」各单列裁决见 §6.3 / §6.4。建议单立 —— 但**责任方是 `decompilerserver` MCP（外部服务），本项目侧只能文档化 + 加防御约定**。**

---

## 2. 一句话结论

两个主症状**全部逐字复现**，但根因比板上写的更准：**不是「按可见性过滤漏了 public」，而是「类型名解析到重名类型之一后，把那一个的成员集当成整个类型的全部，并报 `hasMore:false`」** —— 本机 `Verse.ThingOwner` 恰好有**两个**类型（`020008E2` 泛型 / `020008EC` 非泛型），工具挑了错的那个。与计划 10 **不是同一个缺陷**（两套独立实现，且计划 10 的实现结构上免疫）；板上的「`typeId` 不接受点分全限定名」**实测为假**（真正原因是 `Verse.GenPath` 这个类型在 1.6 里根本不存在，真名是 `Verse.AI.GenPath`）。

## 3. 核实过程

全部为**只读** decompilerserver 调用，当前上下文 `rw16` = `RimWorldWin64_Data\Managed\Assembly-CSharp.dll`，MVID `61e4173561894da49d210260257b5097`（`status` 工具确认，`typeCount:16158, methodCount:76770`）。

### 3.1 症状 A：`get_members_of_type` 漏 public 成员 + 谎报完整

```
get_members_of_type(typeId="Verse.ThingOwner", kind="Method", mode="signatures", limit=60)
→ items 27 条, hasMore:false, totalEstimate:27
   返回的 token 区间恰好是 06003A11 – 06003A2B（连续 27 个），**一条 Contains 都没有**
```

同一次会话里的三条独立佐证：

| 调用 | 结果 |
|---|---|
| `search_members(declaringTypeFilter="Verse.ThingOwner", kind="Method", query=".")` | **77** 条，`hasMore:false`（含 `06003A5A:M` `Contains`） |
| `get_member_details("61e417…:06003A5A:M")` | `Boolean Contains(Thing item)`，`accessibility: Public`，`declaringType: Verse.ThingOwner` |
| `get_overloads("61e417…:06003A5A:M")` | **2** 条（`Contains(ThingDef)` / `Contains(ThingDef, Int32)`），**不含被查的 `Contains(Thing)`** |

⇒ **板上 ① 全部属实**，包括 `hasMore:false` 与「27 条里一条 `Contains` 都没有」这两个关键特征。

### 3.2 🚩 根因：**类型名重名 → 解析器静默挑了错的那个**

```
search_types(query="ThingOwner", limit=10)
→ 9 条，其中**两条**的 fullName 都是 "Verse.ThingOwner"：
     020008E2:T  ThingOwner   （泛型 ThingOwner<T>，带嵌套编译器生成类 <>c / <>c__DisplayClass32_0…）
     020008EC:T  ThingOwner   （非泛型，真正的 Verse.ThingOwner）
resolve_member_id("61e417…:06003A2C:M")
→ Verse.ThingOwner.<>c..cctor      ← 断点落在嵌套类型上，说明 06003A2C 之后已不是 ThingOwner 本体
```

`get_members_of_type("Verse.ThingOwner")` 只把**其中一个**（泛型那个，27 个方法）当成「`Verse.ThingOwner` 的全部成员」，于是 `hasMore:false, totalEstimate:27`。

**两个数字完全对上账**：

| 来源 | 计数 |
|---|---|
| `search_members(kind=Method, declaringType=Verse.ThingOwner)` | 77 |
| 泛型 `ThingOwner` 的方法（06003A11–06003A2B） | 27 |
| 非泛型 `ThingOwner` 的方法（06003A53–06003A77，实测 `get_members_of_type("…:020008EC:T")` = 42） | 42 |
| 构造器 4 + 4 | 8 |
| 27 + 42 + 8 | **77 ✓** |

**⇒ 「漏掉 public 成员」是表象**：被漏的不止 public —— 连 private/protected（`GetAt` protected、`NotifyColonistBarIfColonistCorpse` private…）一起没了，因为**整块 42 个成员根本没被枚举**。可见性不是变量。

**对照实验（证明这是重名专属、不是普遍缺陷）**：`Verse.AI.Pawn_PathFollower` 无重名 ⇒ `get_members_of_type(kind="Method", mode="ids", limit=200)` = **39**，`search_members` = **40**（含 1 个 `.ctor`），token 区间 `06006364`–`0600638A` 逐个吻合 ⇒ **该类型上完全正确**。

### 3.3 症状 B：`get_overloads` 不返回被查者

```
get_overloads("61e417…:06003A5A:M")
→ items 2, hasMore:false, totalEstimate:2
   06003A6B:M  Boolean Contains(ThingDef def)
   06003A6C:M  Boolean Contains(ThingDef def, Int32 minCount)
```

属实。它与症状 A **是两个独立缺陷**：即使重名解析修好了，`get_overloads` 仍会把被查者排除（语义是「同名的**其他**重载」）。

**📌 这条已由「推断」升级为「实证」（2026-10-03，triage `[43]` §2）：**
本样本（`Verse.ThingOwner.Contains` 的 3 个重载**全是 public**）**无法区分**「不做可见性过滤」与「只给 public」两种机制 —— 两种机制在这个样本上预测完全相同（`[43]` §2.1 的 H1/H2 表）⇒ 上面那句结论**当时是对的，但并没有证据支持它**。
决定性实验是**反过来查 public 的那一个**：`get_overloads("…:06006324:M")`（`Verse.AI.PathGrid.RecalculatePerceivedPathCostAt(IntVec3, Boolean&)`，**public**）返回 `totalEstimate: 1`，而**那唯一一条是 `06006323:M`，返回体里 `accessibility: "Private"`** ⇒ **不存在任何可见性过滤**，真机制就是上面那句「不含被查者」。
完整实验表与严重度复评见 §6.3 与 `triage/43-get-overloads-silent-underreport-public-only.md`。

**关于描述**：`mcp_help(tool="get_overloads")` 返回 `Error: 未知工具: get_overloads`（`共 191 个工具`）。⚠️ 这本身是一条附带发现：**`rimworld-debug` 的 `mcp_help` 菜单不覆盖 `decompilerserver` 的工具集** ⇒ `decompilerserver` 侧**没有任何 `mcp_help` 入口可以承载「本工具不含你查询的那个」的免责说明**，这句话只能落在 `decompilerserver` 自己的工具描述里。

### 3.4 与计划 10 的关系：**两个独立实现，不是同一缺陷**

先按派单要求直接回答：**不是同一个缺陷，修计划 10 不会解决 [29]，修 [29] 也不会解决计划 10。**

| | 计划 10 / `find_methods` | [29] / `get_members_of_type` |
|---|---|---|
| 代码位置 | 本仓库 `McpRimDebug/DebugSession.Eval.cs:290` `FindMethods`、`:355-367` `AddMethodResult`、`:497-509` 遍历 | **外部 MCP 服务 `decompilerserver`**，源码不在本仓库（搜索范围：`Mods\UESDdebuger`、`Mods\TEXT20`、`Mods\RimworldMCPDebug` 全树 `-Recurse -Force` 匹配 `(?)decompil`，**0 命中**；`list_contexts` 显示它只注册了 8 个 assembly context） |
| 枚举方式 | `foreach (TypeDefinition t …) foreach (MethodDefinition m in t.Methods)`（`DebugSession.Eval.cs:499`）—— **按 TypeDefinition 走**，`fullName` 来自 `TypeDefinition` | 按**字符串类型名**走一个名字索引 |
| 重名风险 | **结构上免疫**：Cecil 的 `FullName` 对泛型是 `Verse.ThingOwner\`1`，与非泛型天然不同名 | **正中靶心**：两个类型共用同一 fullName |
| 完整性承诺 | 诚实：命中上限时 `truncated = true`（`DebugSession.Eval.cs:501-505`） | **谎报**：`hasMore:false, totalEstimate:27` |

⇒ 唯一真正的交集是**症状**：两者都会让「某个成员在不在」的判定得出相反结论。**根因、代码、修复面三者都不相交。**

⚠️ 但有一条**必须回灌给计划 10** 的下游风险（见 §7）。

### 3.5 板上「`typeId` 不接受点分全限定名」—— **实测为假**

```
get_members_of_type(typeId="Verse.GenPath")
→ error type_not_found: Type 'Verse' could not be found.
   details.parsed = {"typeName":"Verse","memberName":"GenPath"}
```

**这个报错是真的，但根因不是「点分名不被接受」**：

1. `search_types(query="GenPath", limit=15)` → 唯一命中是 **`Verse.AI.GenPath`**（namespace `Verse.AI`）。**`Verse.GenPath` 这个类型在 RimWorld 1.6 里根本不存在** —— 调用方把命名空间写错了。
2. 点分全限定名**确实被接受**：`get_members_of_type(typeId="Verse.AI.Pawn_PathFollower", mode="signatures", limit=3)` 正常返回 3 条、`hasMore:true, totalEstimate:83`。
3. `Verse.ThingOwner` 本身就是点分全限定名，本次调用成功。

⇒ 板上「（另：`typeId` 建议和其它工具对齐，接受点分全限定名）」这条**应当撤回**。

**但它裹着的一个真缺陷应当单独立项**（见 §6.3）。

### 3.6 ✅ 找到一条**一次调用即可绕过**的姿势

```
get_members_of_type(typeId="61e4173561894da49d210260257b5097:020008EC:T", kind="Method", mode="signatures", limit=60)
→ items 42, hasMore:false
   … 06003A5A:M  Boolean Contains(Thing item)   accessibility: Public   ✅
   … 06003A6B:M  Boolean Contains(ThingDef def)      Public
   … 06003A6C:M  Boolean Contains(ThingDef def, Int32 minCount)  Public
```

⇒ **传 canonical type memberId 而不是点分名，就不会踩重名解析**，结果与真值完全吻合（42 = §3.2 对账表中的 42）。取得该 id 的成本是一次 `search_types`。

## 4. 影响与严重度复评

| 维度 | 结论 |
|---|---|
| 真实性 | ✅ 主症状 A、B 逐字复现；A 的根因比板上更准；**A 的附属小节（typeId）实测为假** |
| 严重度 | **🟠 中 → 复评 🔴 高**。理由：板上 §③ 说的「这是 COMMON.md §5.3 那类『符号不存在』判定的**第二条独立路径**」这句是对的，而 [29] 让**这条独立路径本身变成假阴性机器** —— `COMMON.md §5.3` 的整个三步排除法（`Mods\TEXT20\.superpowers\sdd\…\COMMON.md:486`「『符号不存在』/『编译不过』⇒ **不许直接下结论**（本项目已栽 3 次）」）**失去第二只脚**。且它比「老实分页」更危险：`hasMore:false` 是一个**主动的谎**，不是沉默的省略（板上 §③ 已指出这点，本报告确认）。 |
| 是否设计使然 | **否。** 名字→类型的解析必须处理重名（要么按全名消歧、要么报歧义、要么两者的成员合并）；当前是「挑一个 + 谎报完整」，三种正确做法一种都没选。 |
| 可修性 | **本项目侧修不了**（外部服务的源码不在仓库内）。本项目能做的是：**① 立刻把绕过姿势写进规范；② 给计划 10 的文档任务加一条防御措辞。** |

## 5. 责任归属

| 侧 | 归属 | 证据 |
|---|---|---|
| `decompilerserver` MCP 服务 | **真缺陷所在**，需上游修 | 源码不在本仓库（§3.4 搜索范围已写明） |
| `Mods\UESDdebuger` | **只出规范与绕过姿势**，不出代码 | — |
| 计划 10 的文档任务 | **下游风险，需要改措辞**（§7） | `plans/…-10-…md:707` / `:749` 把 `get_members_of_type` 写成「**写码时首选**」 |

## 6. 修的话怎么修

### 6.1 `decompilerserver` 侧（本流水线管不了，但要把修法写清楚交出去）

- **根因修法**：`get_members_of_type` 的类型解析遇到**多个候选**时，三选一 —— (a) 按 `FullName` 精确匹配优先；(b) 歧义时**报错并列出候选**（如 `type_ambiguous` + `[{memberId, fullName, kind}]`）；(c) 合并所有同名类型的成员并在每条结果上标 `declaringType`。**无论选哪个，「返回一个子集 + `hasMore:false`」必须从代码里删掉。**
- **完整性修法**：命中条数达到任何上限时，`hasMore` 必须是 `true` 且给 `nextCursor`；`totalEstimate` 必须是真的全量计数（现在 27 也是错的，真值 77）。
- **`get_overloads` 修法**：把被查者一并返回（推荐，最符合「overloads」二字的直觉），或至少在返回值里加一行 `"note": "不包含你查询的那个重载"`。因为 `decompilerserver` 没有 `mcp_help` 入口（§3.3），这条 note **只能落在返回值里**。

### 6.2 本项目侧（能立刻做，零代码）

在源码检索/查证规范里加一条硬规矩（落点建议与 [27] 同一份规范文档 `docs/mcp-source-lookup-conventions.md`，由计划 11 任务 1 承载）：

> **查类型成员时，`get_members_of_type` 的 `typeId` 必须传 canonical type memberId，不能传点分全限定名。**
> 理由：`Verse.ThingOwner` 这类名字下有两个类型（泛型 + 非泛型），传名字会解析到错的那个并谎报 `hasMore:false`。
> 正例：`search_types(query="ThingOwner")` 拿到 `020008EC:T` 后 → `get_members_of_type(typeId="<mvid>:020008EC:T")`。
> **拿到「不完整但自称完整」的结果时，一律按 COMMON.md §5.3 走三步排除法，不得直接下「不存在」的结论。**

### 6.3 单列裁决 ①：`get_overloads` 的「不含被查者」—— **值得修，但归 decompilerserver 文档**

裁决：**值得修（🔴 高）**，独立于 §6.1 的重名解析。修法见 §6.1 末段。
（**严重度 🟡 低 → 🔴 高，复评上调**，2026-10-03，triage `[43]` §4。）

#### 📌 机制：已由推断升为实证 —— **`get_overloads` 不做任何可见性过滤**

决定性实验（triage `[43]` §2，**decompilerserver，每次显式 `contextAlias:"vanilla"`，MVID `61e4173561894da49d210260257b5097`**，未抢 `current`）：

| 步骤 | 调用 | 返回 |
|:--:|---|---|
| 1 | `get_member_details("…:06006323:M")` | `Void RecalculatePerceivedPathCostAt(IntVec3 c)`，**`accessibility: Private`** |
| 2 | `get_member_details("…:06006324:M")` | `Void RecalculatePerceivedPathCostAt(IntVec3 c, Boolean& haveNotified)`，**`accessibility: Public`** |
| 3 | `get_overloads("…:06006323:M")`（查 **private**） | `totalEstimate: 1` = **仅** `06006324:M`（Public） |
| 4 | 🚩 **`get_overloads("…:06006324:M")`（查 public —— 决定性）** | `totalEstimate: 1` = **仅 `06006323:M`，且返回体里 `accessibility: "Private"`** |
| 5 | `get_overloads("…:06003A6B:M")`（**第二个类型** `Verse.ThingOwner.Contains`） | `totalEstimate: 2` = `Contains(Thing)` + `Contains(ThingDef, Int32)`，**不含被查者** |

**读法**：步骤 4 是全部要害。若存在可见性过滤，查 **public** 的重载应当返回 **0** 条（其余全是 private）或 **1** 条（它自己）；
**实测返回的是 private 那一条** ⇒ **过滤不存在。**

⇒ **真机制：`get_overloads(N)` 返回与 `N` 同名的全部「其他」重载，不含 `N` 自己，可见性一律不管** —— 即 §3.3 那句，由**推断升级为实证**。

⚠️ **本条曾经只能靠推断**：`Verse.ThingOwner.Contains` 的 3 个重载**全是 public**，在那个样本上
「不过滤」与「只给 public」**预测完全相同** ⇒ §3.3 的结论**当时正确但无证据支持**（`[43]` §2.1）。
**教训**：*当候选机制的预测在某组样本上完全重合时，要换一个能分开它们的样本，而不是在重合的样本上投票。*

#### ⇒ 本项目侧规范措辞（**指路已改**）

> **`get_overloads(N)` 返回的是与 `N` 同名的全部「其他」重载：不含 `N` 自己，且不做任何可见性过滤。**
> 🚫 **禁止用 `get_overloads` 的条数 / `totalEstimate` 论证「是否存在重载歧义」。**
> **要看全部重载，请用 `search_members(query:"<方法名>", declaringTypeFilter:"<声明类型>", kind:"Method")`，取 `totalEstimate`。**
> ⚠️ **不要指路到 `get_members_of_type` 当「看全集」入口** —— 本报告 §3.2 自己证明它在**重名类型**上会**谎报完整**（`hasMore:false, totalEstimate:27`，真值 77）⇒ 那条指路会把人**从一个坑引进另一个坑**。（真要用它，必须传 canonical type memberId，见 §6.2。）
> ⚠️ `search_members` 的 `declaringTypeFilter` **看不见扩展方法**（triage `[47]`）—— 但**重载不是扩展方法**，枚举重载不受此影响。

**为何升 🔴**：`totalEstimate` 在其它工具里一律表示「全量计数」，在这里却表示「全量减一」，
且 `hasMore:false` 是一个**主动的谎**而非沉默的省略 ⇒ **不可自纠正**。
谁拿它论证「不存在重载歧义」就会拿到**假阴性**，而那个假阴性**恰好否认了「补丁挂错重载」**这个最贵的 bug
（板上 `D-W8-05` 的原话，经 `[43]` 转述；本流水线读不到 TEXT20 工作区原文）。

### 6.4 单列裁决 ②：`typeId` 不接受点分全限定名 —— **不值得修（板上这条应撤回）**

裁决：**不值得修**。实测点分全限定名被正常接受（`Verse.AI.Pawn_PathFollower` ✅、`Verse.ThingOwner` ✅），`Verse.GenPath` 报错是因为**该类型不存在**（真名 `Verse.AI.GenPath`）。

**但它裹着的真缺陷单独立项**：报错信息把 `parsed: {"typeName":"Verse","memberName":"GenPath"}` 里的**命名空间 `Verse` 当成了类型名**去报 `Type 'Verse' could not be found.`，**报错把矛头指错了对象** —— 调用方会以为「`Verse` 命名空间有问题」，而真正的问题是自己的类型名写错了一个段。这与条目 [26]（「守卫报『账本多行/已失效』指向**数据**，实际是『识别依据失效』指向**守卫自己**」）、`COMMON.md:486` 的 §5.3 是**同一族**。裁决：**值得修（🟡 低，成本是错误信息里加一句「若 `Verse` 是命名空间，请确认类型名的段数」+ 在 `candidates` 里给出 `search_types` 的建议）**，同样归 `decompilerserver` 上游，本项目侧只能在规范里写「类型名写错时，报错里的 `typeName` 可能只是你写的最后一段」。

### 6.5 现成的绕过姿势（今天就能用，取代板上 ⑤ 的「三路交叉复核」）

| 场景 | 姿势 |
|---|---|
| 查类型成员 | `search_types` 拿 canonical type memberId → `get_members_of_type(typeId="<mvid>:02000xxx:T", mode="signatures")` |
| 确认某成员存在 | `search_members(declaringTypeFilter=…, query=…, kind=…)` 的 `totalEstimate` 与 `get_members_of_type` 的**不一致**本身就是「你踩到重名了」的告警信号 |
| 确认可见性 | `get_member_details(memberId)` —— 它按 memberId 定位，**不受重名解析影响**（实测 `06003A5A` 直接给出 `Public`） |

⇒ 板上 ⑤ 的「三路交叉复核」成本高得多且不彻底；上表三条都是**单次调用**，且第 1 条是完备的。

## 7. 与既有计划的关系

| 计划 | 关系 |
|---|---|
| `plans/2026-10-01-10-api-member-visibility-tools.md` | **不归并。** 两套独立实现（§3.4）。**但计划 10 有一处下游风险需要打补丁**：任务 3 的三份文档把 `decompilerserver` 的 `get_members_of_type` 写成「**纯离线，游戏不用跑，**写码时首选**」并让读者据此下「某成员是否 public」的结论 —— `plans/…-10-…md:707`（`README.md` 新增小节表第 2 行）、`:749`（`AI-DEPLOYMENT.md` 新增小节第 1 条）、`:582`（`find_methods` 工具描述里的指路句）三处都是这个口径。**建议主代理把这三处的指路补上一句**：「⚠️ `typeId` 传**点分全限定名**时，若该名字下存在多个同名类型（泛型/非泛型），工具会解析到其中一个并返回它的成员集且 `hasMore:false` —— **查成员请传 canonical type memberId**（`search_types` 拿）。」<br>⚠️ 这**不是**让计划 10 改代码，只是给它的文档任务加一条措辞；不要因此把 [29] 塞进计划 10 的任务清单（会破坏该计划的离线 `--selftest` 验收纪律）。 |
| `plans/2026-10-01-11-search-source-false-negative-convention.md` | **共享落点，不共享缺陷。** §6.2 那条绕过规矩建议写进计划 11 任务 1 产出的同一份规范文档（那里已经有「规矩 5.3 不许直接下结论」的纪律位），但它与 [27] 的声明行正则无关，不要混进「规矩 2」。 |
| 其余计划（01–09、12、14、22） | 无关。 |

**⇒ 建议：不派本流水线的 PLAN。** 真缺陷在**外部服务**里，plan 若写成「改本仓库代码」会误导执行者；本项目侧的产物只有一份规范措辞（§6.2 + §6.3 + §6.4 各一句），应并进计划 11 任务 1 或计划 10 任务 3 的文档步骤，**不单独立计划**。若维护者愿意向上游提 issue，则以「根因 + 对账表 + 正例绕过姿势」的形式提 `decompilerserver`，那属于仓库外动作，本流水线不产出计划。
# [35] 裁决报告 —— 「无 using」下 `execute_csharp_code` 的成员名解析全靠撞

> 角色：TRIAGE。输入：`docs/mcp-吐槽板.md` 的 `## [35]`（2026-10-02，W6 收官实测）。
> 必读对照：计划 02（任务 4 文案）、计划 10（`2026-10-01-10-api-member-visibility-tools.md`）、吐槽板 `[29]`、同族条目 `[33]`。
> 全文行号均用 `[System.IO.File]::ReadAllLines(...)` 复核（见 §6）。

**裁决：值得修** —— 但**不单立**，与 [33] 合并为一份计划；且板上那张对照表**有两处必须先纠正**再落进文档。

---

## 0. 裁决摘要

| 项 | 结论 |
|---|---|
| 裁决 | **值得修** |
| 严重度复评 | **🟠 中，维持**（不造假结论：每次都红；但"每轮撞 5 次 = 5 次 MCP 往返"，且 `CS0841` 把"类型解析失败"伪装成"变量作用域"，差点导向错误排查方向） |
| 责任归属 | **本项目（MCP 文案层 + 文档层）**；④建议里"自动附 decompile 全表"这一半**在本仓库内不可实现**（见 §3.2） |
| 归并锚点 | **与 [33] 合并**；共用落点 `MCP/index.js:4536-4537`；⛔ 不得碰 `:2937`（计划 02 任务 4）与 `:2072/:2078`（计划 12 任务 1） |
| 板上需要更正的事实 | **2 处**（`CellRect` "没有 `InBounds`" ❌；`Map.area` 的错因标错 ⚠️）——详见 §2 |

---

## 1. 条目原文与定位

`docs/mcp-吐槽板.md`：

- 正文段 `## [35]`：**第 54-79 行**（标题 54，⑤状态 79 = **已绕过**）。
- 索引行：**:52**（🟠 中 / 已绕过）。
- 板上 `:73` 自述：「与 [33]（`IntVec3.GetRegion` 二义 + `RegionType` 无 `Region` 成员）**同族，建议合并处理**」。

原文五个要素：①症状 = 造夹具脚本**连撞 5 次**编译错（`:59-65` 的对照表）；②复现 = 冷重启进图后任意涉及 `IntVec3`/`Map`/`CellRect`/`ThingDefOf`/网格查询的代码（`:67-68`）；③影响 = 🟠 中，「不造假结论（每次都会红，不会假绿）」，但每轮撞错 = 一次 MCP 往返，5 次约浪费 3~5 分钟，**`CS0841` 把"类型解析失败"伪装成"代码顺序写错了"**（`:70-72`）；④建议 = 编译错里多打一行"你可能想要的那一支"，**用 `get_members_of_type` 那套**（`:75-77`）；⑤状态 = 已绕过（`:79`）。

---

## 2. 板上那张对照表逐行复核（哪些对、哪些错）

复核方式：对 `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`
（decompilerserver 上下文 `rw16`，MVID `61e4173561894da49d210260257b5097`）做**只读**结构化查询。**未启动游戏、未调用 `execute_csharp_code`。**

| # | 我写的（板上 `:61-65`） | 板上给的"正确写法" | 复核结论 |
|---|---|---|---|
| 1 | `Verse.RimWorld.Pawn flyer = null;` | `Verse.Pawn` | ✅ **正确**。`list_namespaces`（`totalEstimate=29`、`hasMore:false`）**没有 `Verse.RimWorld` 这个命名空间**（只有 `Verse`/`Verse.*` 9 个 + `RimWorld`/`RimWorld.*` 7 个 + 若干非游戏命名空间）；`normalize_member_id("Verse.Pawn")` → `…:020007AC:T`，`get_members_of_type("Verse.Pawn")` → 347 成员。⚠️ "CS0841 报错方向指错"这一条属 mcs 行为，**本报告未实跑复核** |
| 2 | `Verse.ThingDefOf.Wall` | `RimWorld.ThingDefOf.Wall` | ✅ **正确**。`search_types(query="ThingDefOf")` → **只有** `RimWorld.ThingDefOf` 与 `RimWorld.RoadPathingDefOf`，**无 `Verse.ThingDefOf`** |
| 3 | `map.area.InBounds(q)` | `CellRect.FromLimits(0,0,map.Size.x-1,map.Size.z-1).Contains(q)` | ⚠️ **结论能用，错因标错**。`search_members(query="area", declaringTypeFilter="Verse.Map")` → 字段 `areaManager`、属性 **`Area`（`get_member_details` 签名 = `Int32 Area`）**、…，**没有小写 `area`** ⇒ `CS1061` 属实；但真正让 `.InBounds` 挂不上去的原因是 **`Map.Area` 是个 `Int32`**，不是"Area 对象没有 InBounds"。正确写法（`CellRect.FromLimits(...)`）确实可用，**但它不是从"area 不存在"这条推出来的** |
| 4 | `rect.InBounds(q)` / `rect.Contains` | 「`CellRect` **没有 `InBounds`**，只有 `Contains`」 | 🔴 **板上这条错了**。`get_member_signature("…:0600042F:M")` → **`Verse.CellRect.InBounds` 存在**，签名 **`Boolean InBounds(Map map)`**。`CS1503: IntVec3 不能转成 Map` **恰恰因为它收的是 `Map`**——板上把"参数类型不对"读成了"方法不存在"。`CellRect` 同时**确实**有 `Boolean Contains(IntVec3 c)`（`…:0600044D:M`）⇒ **"用 `Contains`" 这个结论是对的，"CellRect 没有 `InBounds`" 这个理由是错的**。板上括号里"无 using 时 `x.InBounds(y)` 会误绑到 `Map` 的扩展"同样与事实不符。**⚠️ 2026-10-03 修订（triage `[47]` §4）：本句【结论】保留，【证据】已换。**<br>**原证据不成立**：① 它把「全程序集 `InBounds` 共 13 处命中」当作「没有 `Map` 扩展」的凭据 —— **计数推不出接收者是谁**；② 它转引的「本报告 §2.1」是**悬空交叉引用**（§2.1 讲的是 `regionManager`，不含任何 `InBounds` 内容）；③ 33-35 计划 `:129` 同一条判定用的证据 `search_members(query:"InBounds", declaringTypeFilter="Verse.Map")` → 0 命中，是**恒真断言**（`AGENT-BRIEF.md` §3.7 第 5 条）：`declaringType` 过滤**原理上看不见扩展方法**（`[47]`），**无论 `Verse.Map` 上有没有 `InBounds` 扩展都会返 0** ⇒ **判别力 = 0**。<br>**新证据（无 `declaringType` 过滤的全仓检索，`[47]` §2.2 #6 实测）**：`search_source(query:"public static bool InBounds")` → **4 条**，接收者分别为 `T[,]` / `IntVec3` ×2 / `Vector3`；`search_source(query:"InBounds\(this Map")` → **`No results found`** ⇒ **没有任何以 `Map` 为接收者的 `InBounds` 扩展**。⚠️ 该结论**只对 vanilla 成立**（`[47]` §9：未检索任何模组程序集）。 |
| 5 | `q.Standable(map)` / `GridsUtility.GetFirstThing(q, map)` | `Verse.GenGrid.Standable(q, map)`；`GridsUtility.GetFirstThing<Verse.Thing>(q, map)` | ✅ **正确**。`get_member_signature("…:0600557E:M")` → `public static Boolean Standable(IntVec3 c, Map map)`（**静态、IntVec3 在前**）；`get_members_of_type("Verse.GridsUtility")` 显示**两个同名不同参的 `GetFirstThing` 并存**：泛型 `T GetFirstThing(IntVec3 c, Map map)`（`06001B73`）与非泛型 `Thing GetFirstThing(IntVec3, Map, ThingDef)`（`06001B71`）⇒ 2 参调用只剩泛型那支、而 `T` 无处推断 ⇒ `CS0411` 成立，**必须显式类型实参** |
| 附 | `map.Size` 是 `IntVec3` 不是 `CellRect` | — | ✅ **正确**。`get_member_details("…:17000536:P")` → `IntVec3 Size`，Public property |

### 2.1 与 [33] 共享的第三条（板上 `[33]` `:254`）

`search_members(query="regionManager")`（**全程序集**）→ **0 命中**；`Verse.Map` 上叫 Region 的字段是
`regionGrid` / `regionMaker` / `regionAndRoomUpdater` / `regionLinkDatabase` / `regionDirtyer`。
⇒ 「`Map.regionManager` 是 internal 字段」**是错的**（详见 [33] 报告 §2.3）。

### 2.2 净结果

**5 行里：3 行完全正确、1 行结论对但理由错（且错得会污染文档）、1 行错因标错。** 另加 [33] 的 1 条错误断言。
⇒ **本条绝不能按"照抄对照表进文档"处理。** PLAN 必须先按本节逐条订正，再把订正后的表落进文档。

---

## 3. ④建议的可行性核实：`get_members_of_type` 真能提供这个映射吗

板上 `:75-77` 建议：「编译错里**多打一行"你可能想要的那一支"**：对 `CS0234/CS1503/CS1061/CS0411/CS0121`
自动附上 decompile 查到的**同名成员全表**（`get_members_of_type` 那套已经在服务端了）。
至少把 `Type.Name` → 候选全限定名的映射做成一张小表，随错回传。」

拆成三个子问题逐一核实。

### 3.1 它接受点分全限定名吗？—— ✅ **接受（实测）**

| 调用 | 结果 |
|---|---|
| `get_members_of_type("Verse.RegionType")` | ✅ 命中，10 个成员，`hasMore:false` |
| `get_members_of_type("Verse.GridsUtility")` | ✅ 命中，`totalEstimate=57` |
| `get_members_of_type("Verse.CellRect")` | ✅ 命中，`totalEstimate=121` |
| `get_members_of_type("Verse.Pawn")` | ✅ 命中，`totalEstimate=347` |

返回字段：`memberId` / `name` / `kind` / `signature` / `accessibility` / `isStatic` / `isAbstract` / `isVirtual`
⇒ **足以拼出「你可能想要的那一支」所需的「签名 + 静态性 + 可见性」三件套。**

⚠️ 但**同族的 `search_types` 对点分全限定名是模糊匹配**：
`search_types(query="Verse.Pawn")` 前 6 条里**没有** `Verse.Pawn` 本身（`hasMore:true`、`totalEstimate:202`），
而 `normalize_member_id("Verse.Pawn")` 能精确解析 ⇒ **精确入口是 `normalize_member_id` / `get_members_of_type`，不是 `search_types`。**

### 3.2 它会漏成员吗？—— ⚠️ **会，三种漏法，其中一种是板上已实测记录的静默漏**

| 漏法 | 证据 | 影响 |
|---|---|---|
| ① **基类成员默认不列** | 工具参数 `includeInherited` 默认 `false`（**读的是工具 schema，本报告未实测 `true` 的行为**） | `Verse.Map` **无基类**（`find_base_types` → `bases: []`）故不受影响；但 `Verse.Pawn`（347 成员）等有继承链的类型会漏掉基类成员 ⇒ **用它回答 CS1061「这个成员在哪」可能答不全** |
| ② **分页静默截断** | `limit` 默认 100；实测 `Verse.CellRect` 121、`Verse.Pawn` 347、`Verse.GridsUtility` 57 ⇒ 大类型一次拉不全（有 `hasMore` / `nextCursor`，但**不回显就会静默少列**） | 同上 |
| ③ 🔴 **板上 [29] 已实测：它会静默漏掉 public 成员，却报 `hasMore:false`** | `docs/mcp-吐槽板.md:46`（索引行）原文：「`get_members_of_type` **静默漏掉 public 成员**却报 `hasMore:false`（实测 `Verse.ThingOwner.Contains(Thing)` 缺席）+ `get_overloads` 不返回被查的那个 ⇒ **直接制造"这个 API 不存在"的假结论**」 | **决定性**：拿一个会静默漏成员的源去"自动回填候选表"，等于**用一个新的假阴性替换掉旧的假阴性** |

⇒ **答复：能提供映射，但不能作为权威来源直接回填。** 板上的 ④ 建议在"能不能"这一层**部分成立**（工具面有、点分名可用、字段够），
在"准不准"这一层**不成立**（[29] 已证其会假阴性）。

### 3.3 架构层：「随错自动回传」在本仓库内**做不出来**

- `MCP/index.js`（rimworld-debug 服务器）**完全没有**这套工具的任何痕迹：
  在 `MCP/` 全目录（`*.js`）检索 `get_members_of_type` / `search_members` / `get_overloads` / `membersOfType` / `listMembers` / `resolveMemberId` —— **0 命中**。
  `get_members_of_type` 属于**另一个 MCP 服务器**（`decompilerserver`），不是本仓库的实现。
- 计划 10 §0.4（`:100-101`）也确认了同一边界：「`inspect_type` 的正确定位是**如实描述**」、
  「**不新增** `describe_type` 这类成员级新工具；离线场景由任务 3 写明的 `decompilerserver` 覆盖」。
- ⇒ **"在 `execute_csharp_code` 的错误响应里自动附 decompile 查到的全表"需要先给 `MCP/index.js` 引入反编译能力**（Cecil 或一个到 decompilerserver 的客户端），
  这是一个**远大于本条**的工程，**不应挂在本条上**。

### 3.4 ⇒ 可行的替代方案（给 PLAN 的形状建议）

按代价从低到高：

1. **（推荐）按错误码分流的 `guidance` + 一张人工策展的高频对照表。**
   板上 5 次撞错**全部落在 8 个错误码**上，而每个错误码的"正确方向"是**有限且稳定**的：

   | 错误码 | 今天 `MCP/index.js:4537` 给的（对**所有** `CS0*`） | 应该给的 |
   |---|---|---|
   | `CS0127` | 「默认没有 using 引用，请用全限定类型名…」 | **完全无关**。宿主是 `void`，`return 表达式` 不合法 ⇒ 见 [34] 报告 §3.1 |
   | `CS0103` / `CS0234` | ✅ 大体正确 | 保留；补一句「全限定名的正确拼法去哪查」（§3.1 的入口） |
   | `CS0121` | ❌ 错（是重复引用/二义，与 using 无关） | 「两个候选逐字相同 = 同一方法被引用两次，别用 `using` 解二义，走全限定名」 |
   | `CS0117` / `CS1061` | ❌ 错 | 「该类型上没有这个成员/枚举值；正确成员名与全表去哪查」+ 直接给 `RegionType.Normal` 这类**已核实的正确答案** |
   | `CS0411` / `CS1503` | ❌ 错 | 「显式类型实参 / 参数类型对不上；已知高频：`GetFirstThing` 须 `GetFirstThing<Verse.Thing>(…)`；`CellRect.InBounds` 收的是 `Map` 不是 `IntVec3`」 |
   | `CS0841` | ❌ 错（会被读成"变量作用域"） | 「本条在无 using 控制台里通常是**类型解析失败**的伪装，先查上一行的类型名」 |

2. **（推荐）表格的每一条都必须带"这条结论的核验方式"**（哪个类型、哪个 memberId），
   这样下一个人能一眼看出它是**实测过的**还是**猜的**——这正是 COMMON.md §5「零命中必须附检索模式原文」的精神。
3. **（可选，单独排期）** 若真要"随错自动附候选"，先修 [29]，再考虑在 `MCP/index.js` 侧引入反编译；**本轮不做**。

---

## 4. 与 [33] / 计划 02 / 计划 10 的关系

### 4.1 与 [33]：**同族，且落点完全重合 ⇒ 合并**

- 板上自己就说「与 [33] 同族，建议合并处理」（`:73`）；
- 两者改善都落在**同一个返回值**上：`MCP/index.js:4536-4537` 的 `r.guidance`（[33] 报告 §4 已给出该行逐字内容）；
- 两者都要写同一张"正确答案表"，而那张表的落点文件（`README.md` / `AI-DEPLOYMENT.md`）**正是计划 10 任务 3 正在改的**（`2026-10-01-10-…:132-134`）⇒ 拆两份计划必然打架。
- 两者共享一个已核实的错误事实（`Map.regionManager` 不存在）⇒ 合并后只需纠错一次。

### 4.2 与计划 02 任务 4 文案：**部分重合，但不应合并进计划 02**

| 面 | 重合度 | 处置 |
|---|---|---|
| `MCP/index.js:2937`（`TOOL_USAGE_NOTES`） | **有交集**：现有文案已写「控制台默认无 using 引用，裸类型名（如 `GenTicks`）会报 CS0103；须用全限定名或先 `add_using_directive`」——这正是 [35] 的第一层痛点 | ⛔ **合并计划一个字都不碰这一行**。计划 02 任务 4（`:1030-1036`）会整行重写它；若需要加一句"成员/签名去哪查"，按计划 12 的既有做法（`2026-10-01-12-…:101`）**以交接句形式交给计划 02 的执行者并入** |
| `MCP/index.js:2072` / `:2078`（工具 description / `code` 示例） | 计划 12 任务 1 已认领 | ⛔ 不碰 |
| `MCP/index.js:4536-4537`（`guidance`） | **零重合**——计划 02 全文 1371 行**一次都没提到 `:4537` 或 `guidance`** | ✅ **合并计划唯一的 MCP 落点** |

⇒ **结论：不并入计划 02（避免与任务 4 抢同一行），但也不单立——与 [33] 合成一份新计划。**
建议文件名：`plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md`。

### 4.3 与计划 10：**部分重叠，必须串行**

| 计划 10 的任务 | 与本条的关系 |
|---|---|
| 任务 1（`:144`）`find_methods` 补 `signature`/`isPublic`/… | **互补**（在线腿）。计划 10 §0.3 明确它治的就是"**相反结论**"，而本条 §2 的两处错误事实正是同一种病 |
| 任务 3（`:658`）文档补「成员可见性该去哪问」 | **文件重叠**（都改 `README.md` / `AI-DEPLOYMENT.md`）⇒ **必须串行**，且本条的"正确答案表"应当挂在计划 10 任务 3 已经建立的那节之下，而不是另起一节 |

---

## 5. 严重度复评 / 责任归属 / 归并锚点

### 5.1 严重度：**🟠 中，维持板上 `:52` 的值**

| 尺子（AGENT-BRIEF §5.2） | 判定 |
|---|---|
| 会不会得出**事实相反**的结论 | **不会**（板上 `:70` 自己就写了"每次都会红，不会假绿"）。**最接近的一处是 `CS0841`**（把类型解析失败报成"用前未声明"）——它不会让你得出相反结论，但会把你**导向错误的排查方向**（去查变量作用域），这属于"系统性浪费往返"而非"造假结论" |
| 是不是每次都要重踩 / 静默烧 token | **是**：板上 `:70-72` 实测一个夹具脚本 5 次往返 ≈ 3~5 分钟；且这是**所有 Map/网格类探针**的共同入口 |
| 是不是本项目的锅 | **一半是**：mcs 默认无 using、裸类型名 CS0103 是 UE 上游行为；但"错误响应里给错方向的 guidance"（`MCP/index.js:4537`）**是本项目的锅**，且是**可改一行**的那种 |
| 可修性 | **高**（文案 + 一张表 + 一处分流逻辑，零游戏侧改动、零构建） |

⇒ 维持 🟠。**不升 🔴**（不造假结论）；**不降 🟡**（每轮必踩、且发生在最高频的探针路径上）。

### 5.2 责任归属

| 面 | 归属 |
|---|---|
| `MCP/index.js:4536-4537` 的 `guidance` 分流 | **本项目 MCP 层**（Node，零风险） |
| 对照表 / "正确答案"文档 | **本项目文档层**（须与计划 10 任务 3 串行） |
| 「随错自动附 decompile 全表」 | **本仓库内不可实现**（§3.3）；要实现属**新工程**，且应先修 [29] |
| 「控制台默认无 using / 裸名 CS0103」本身 | **UE 上游**（`ConsoleController.cs:39-53` 的 `DefaultUsing` 不含 `Verse`/`RimWorld`）⇒ 不是本项目的锅，但**缓解手段全在本项目**（`add_using_directive` 工具 + 文案） |

### 5.3 归并锚点

**[33] + [35] 合并为一份计划**（见 §4.1/§4.2 的三条理由）。给 PLAN 的硬约束：

- ⛔ 不得改 `MCP/index.js:2937`（计划 02 任务 4 整行重写，见 `2026-10-01-02-…:1028`/`:1061`）。
- ⛔ 不得改 `MCP/index.js:2072` / `:2078`（计划 12 任务 1，见 `2026-10-01-12-…:60`/`:101`）。
- ⛔ 不得把 §3.3 的"自动附 decompile 全表"写成本轮承诺（不可实现）；可写成**后续项**，前置条件是 [29] 修完。
- ✅ 唯一允许改的 MCP 行是 `MCP/index.js:4536-4537`，且必须**按错误码分流**（8 个错误码各一句），不能是第二段固定文案。
- ✅ 对照表落进文档前，**必须先按本报告 §2 订正那两处**。

---

## 6. 检索范围声明与本报告的自我限制

**检索范围（凡"查不到"必须随附）：**

| 结论 | 范围 |
|---|---|
| `Verse.RimWorld` 不存在 / 命名空间全集 | decompilerserver 上下文 `rw16` = `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（**仅 vanilla 本体**，MVID `61e4173561894da49d210260257b5097`），`list_namespaces` 返回 `totalEstimate=29`、`hasMore:false` ⇒ 全量 |
| `regionManager` 不存在、`Map` 的 Region* 字段 | 同上（`search_members`，全程序集 + `declaringTypeFilter` 两种都查过） |
| `CellRect.InBounds(Map)` / `Contains(IntVec3)` / `Map.Size` / `Map.Area` / `GenGrid.Standable` / `GridsUtility.GetFirstThing` / `ThingDefOf` / `Verse.Pawn` | 同上（`get_members_of_type` / `get_member_signature` / `get_member_details` / `search_types` / `normalize_member_id`） |
| `InBounds` 全程序集分布（13 处） | 同上，`search_members(query="InBounds")`（**不带 `declaringTypeFilter`**）。⚠️ 该计数**不足以**支撑「`Verse.Map` 上无 `InBounds` 扩展」—— 见下一行 |
| **「没有任何以 `Map` 为接收者的 `InBounds` 扩展」**（§2 第 4 行的判定依据） | ① **工具 + 参数**：`mcp__rimworld-source-online__search_source{ query: "public static bool InBounds" }` → **4 条**（`Gen.cs:375` / `GenGrid.cs:75` / `:85` / `:95`，接收者 `T[,]` / `IntVec3` ×2 / `Vector3`）；`search_source{ query: "InBounds\(this Map" }` → **`No results found`**。② **范围**：**该服务全部 vanilla `Source/` 语料**，**未检索任何模组程序集** ⇒ 结论**只对 vanilla 成立**。⚠️ 只覆盖 `public static bool` 这一形状与 `InBounds(this Map` 这一字面形状。③ **该类事实的权威来源本应在哪里**：**vanilla 源码本体**（`Source/Verse/Gen.cs:375`、`Source/Verse/GenGrid.cs:75/:85/:95`），已逐条列在 ① 的返回里可核。🚨 **不可用作证据的形式**：任何带 `declaringTypeFilter` 的查询（`search_members` 亦然）—— 它**原理上看不见扩展方法**，是**恒真断言**，判别力 = 0（`triage/47-…md` §4） |
| `get_members_of_type` 是否在 `MCP/` 里 | `MCP/` 全目录 `*.js`，grep 模式 `get_members_of_type\|search_members\|get_overloads\|membersOfType\|listMembers\|resolveMemberId` ⇒ **0 命中**（第二次 grep，模式原文见上） |
| 计划 02 是否提到 `:4537`/`guidance` | 计划 02 **全文 1371 行逐节读完** |
| 计划 10 | 只读了 `:1-145`（§0 全文 + §1 文件结构）与小节目录；任务 1/2/3 **未逐字读完** |
| 板子行号 | `Select-String -Pattern '^## \[3[345]\]'` 定位 + `ReadAllLines` 复核；全文件 1459 行 |

**本报告明确没有做：**

- ❌ 未启动游戏、未调用 `execute_csharp_code`、未构建（AGENT-BRIEF §1.2/§1.3）。
- ❌ **未实跑复核任何一条编译错误码**（`CS0234/CS0841/CS1503/CS1061/CS0411/CS0121`）——
  本报告只核实了"**正确写法那一侧**"（成员/签名确实长什么样）。板上"我写的那一侧报了什么"是**采信**的，未经复现。
- ❌ **未实测 `get_members_of_type(includeInherited: true)`**；§3.2 ① 是读工具 schema 得出的，未验证。
- ❌ **未复现 [29] 的静默漏成员**（`Verse.ThingOwner.Contains`）；采信板上 `:46` 的实测记录。若 PLAN 要依赖该结论，建议在计划里列为前置复核项。
- ❌ **未检索任何模组程序集**（`TEXT20.dll` 等在 decompilerserver 里是**另外的上下文**）
  ⇒ 本报告所有"不存在/存在"结论**只对 vanilla `Assembly-CSharp.dll` 成立**；模组若自带同名类型/扩展方法，结论需按该模组的上下文重核。
- ❌ 未用 `search_string_literals`（字面量匹配，带 `|` 的模式无效）；所有检索模式原文已在正文引用处逐条给出。

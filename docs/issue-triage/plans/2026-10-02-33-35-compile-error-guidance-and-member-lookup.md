# [33]+[34]+[35] 编译错误码分流 guidance 与成员名对照表 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让 `execute_csharp_code` 的失败响应**按错误码**给出正确归因（今天它对所有 `CS0*` 统一回「缺 using 引用」，对 9 个错误码中的 8 个是误导），并把「无 using 控制台下的成员名正确写法」落成一张**每条都带核验方式**的对照表。

**架构：** 纯 MCP 文案层 + 纯文档，**零游戏侧改动、零构建、零 MCP 工具调用**。唯一 MCP 逻辑落点是 `MCP/index.js:4536-4537` 的 `r.guidance`：把它从「一段固定文案」换成「先判 `CS0127`，其余交给块外的纯函数 `compileErrorGuidance(error)` 路由表」。对照表落 `README.md` / `AI-DEPLOYMENT.md` 各新增一节，**挂在计划 10 任务 3 新建的那节之下**，同文件不同段落。

**技术栈：** Node.js（ESM，`node:test` + `node:assert/strict`）、Markdown。测试入口 `Set-Location MCP; npm test`（`MCP/package.json` 的 `test` = `node --test "test/*.test.js"`）。

---

## ⚠️ 开工前必读（5 条硬约束）

| # | 约束 | 依据 |
|---|---|---|
| 1 | **⛔ 不得改 `MCP/index.js:2937`**（`TOOL_USAGE_NOTES`） | 计划 02 任务 4 整行重写（`2026-10-01-02-…:1028`/`:1061`） |
| 2 | **⛔ 不得改 `MCP/index.js:2072` / `:2078`**（工具 `description` / `code` 示例） | 计划 12 任务 1 的领地（`2026-10-01-12-…:60`/`:101`） |
| 3 | **⛔ 不得改计划 02 / 10 / 12 任何文件、不得改吐槽板 `docs/mcp-吐槽板.md`** | 铁律 §1.1；吐槽板由主代理写批注 |
| 4 | **本仓库不是 git 仓库** | 铁律 §3.5 事故链第 4 步：「没有备份可恢复」。⇒ 每个任务的收尾步是**改动清单**，不是 `git commit` |
| 5 | **行级重排 / 插入 / 删除只能用 `edit` 的 old/new 替换，禁止用 `pwsh` 改文件** | 铁律 §3.5 硬规则 1：`edit` 是「不匹配⇒报错、文件不动」，`WriteAllLines` 是「静默写成空」 |

### 本计划要引用的行号（全部于 2026-10-02 用 `[System.IO.File]::ReadAllLines(...)` 复核）

| 引用 | 内容 | 复核方式 |
|---|---|---|
| `MCP/index.js:4536-4537` | `if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || … includes('CS0'))) {` + `r.guidance = '编译失败：C# 控制台默认没有 using 引用…'` | 逐行打印 |
| `MCP/index.js:4530` | `case 'execute_csharp_code': {` | 逐行打印 |
| `MCP/index.js:4555-4564` | `case 'add_using_directive'`（响应原样透传，不做校验） | 逐行打印 |
| `MCP/index.js:4004-4006` | `// 失败响应附加 usage…` 注释 + `function attachUsageOnFailure(name, args, result) {` | 逐行打印（**任务 1 的插入锚点**） |
| `MCP/index.js:5450-5459` | `export const _testHooks = { … }` | 逐行打印（**任务 1 的导出锚点**） |
| `MCP/index.js:2937` / `:2072` / `:2078` | 三处禁改锚点 | 逐行打印 |
| `MCP/index.js` 总行数 | **5460** | `[System.IO.File]::ReadAllLines(...).Count` |
| `MCP/test/toolchainFixes.test.js` 总行数 | **294**；`:121` 切 `toolListLine`→`buildMenuNode`；`:263-268` 切 `start_game`/`stop_game` | 逐行打印 |
| `MCP/test/` 现有文件 | 9 个（`hotreloadConcealment` / `index.integration` / `journal` / `mapMarker` / `quickTest` / `runner` / `timeoutPolicy` / `toolchainFixes` / `toolCountConsistency`） | 目录列举 |
| `MCP/index.js:5406` | `if (process.env.UESD_MCP_NO_START === '1') {`（测试守卫） | grep |
| `README.md` 总行数 | **243**；`## 设置` 在 **230**；`:228` 是 `> 动态端口：…` 引用块 | `[System.IO.File]::ReadAllLines` |
| `AI-DEPLOYMENT.md` 总行数 | **190**；`:158` 是 §7 末条；`:160` 是 `---`；排障表 `:164-177` | `[System.IO.File]::ReadAllLines` |
| 吐槽板总行数 | **1459**；`## [35]` 54、`## [34]` 212、`## [33]` 243（`Select-String -Pattern '^## \[3[345]\]'` 定位，**未用行号定位**） | 同左 |
| 计划 02 总行数 | **1535**；任务 7 在 **1423-1523** | `[System.IO.File]::ReadAllLines` |
| 计划 10 总行数 | **852**；任务 3 在 **670-815** | 同上 |

### 本计划引用的程序集事实（decompilerserver 上下文 `rw16`）

**检索范围（凡「不存在 / 查不到」必须随附）：** 上下文 `rw16` = `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`，**仅 vanilla 本体**，MVID `61e4173561894da49d210260257b5097`，`typeCount=16158` / `methodCount=76770` / `namespaceCount=29`。
**未检索任何模组程序集**（`TEXT20.dll` 等是另外的上下文）⇒ 下面所有「不存在」结论**只对 vanilla 成立**；模组若自带同名类型/扩展方法，结论需按该模组上下文重核。
**未启动游戏、未调用 `execute_csharp_code`、未构建**（铁律 §1.2/§1.3）。所有查询均为结构化只读查询，**未使用 `search_string_literals`**（字面量匹配，带 `|` 的模式无效）。

| # | 结论 | 核验命令（本轮实际执行） |
|---|---|---|
| F1 | `Verse.CellRect.InBounds` **存在**，`Boolean InBounds(Map map)`，Public 实例方法，`…:0600042F:M` | `get_member_signature(memberId="Verse.CellRect:InBounds", contextAlias="rw16")` |
| F2 | `Verse.CellRect.Contains` 存在，`Boolean Contains(IntVec3 c)`，Public，`…:0600044D:M` | `get_member_signature(memberId="Verse.CellRect:Contains", contextAlias="rw16")` |
| F3 | `Verse.Map.Area` 是 **Property** `Int32 Area`，Public，`…:17000539:P` ⇒ **没有小写 `area`** | `get_member_details(memberId="Verse.Map:Area", contextAlias="rw16")` |
| F4 | `Verse.Map.Size` 是 Property `IntVec3 Size`，Public，`…:17000536:P` | `get_member_details(memberId="Verse.Map:Size", contextAlias="rw16")` |
| F5 | `Verse.Map.regionGrid` 是 **`Public` 字段** `RegionGrid regionGrid`，`…:04001937:F` | `get_member_details(memberId="Verse.Map:regionGrid", contextAlias="rw16")` |
| F6 | `regionManager` **全程序集 0 命中** | `search_members(query="regionManager", limit=20, contextAlias="rw16")` → `items:[] totalEstimate:0` |
| F7 | **vanilla 里没有任何以 `Map` 为接收者的 `GetRegion` 扩展** —— 全程序集名为 `GetRegion` 的成员共 **8** 条，其中**只有一条**是扩展方法形态 `GetRegion(this IntVec3, …)`：`Verse.GridsUtility.GetRegion`（`…:06001B5D:M`）；另一条 `Verse.RegionAndRoomQuery.GetRegion`（`…:060020D9:M`）是**实例方法**，不是 `IntVec3` 的扩展 | ⚠️ **2026-10-03 已换证据**（`triage/33-intvec3-getregion-ambiguous-and-regiontype.md:54` 实测）：`search_members(query="GetRegion")`，**全程序集、不带 `declaringType` 过滤** → **`totalEstimate: 8`**。范围 = **vanilla `Assembly-CSharp.dll` 本体**，**未检索任何模组程序集**（本表其余各行的口径，见 §0）。<br>🚨 **原证据已作废**：`search_members(query="GetRegion", declaringTypeFilter="Verse.Map", limit=20, contextAlias="rw16")` → 0 命中是**恒真断言**（`AGENT-BRIEF.md` §3.7 第 5 条）—— `declaringTypeFilter` 按「声明类型」过滤，而**扩展方法的声明类型是静态类、不是被扩展类型** ⇒ **无论结果如何都会返 0**，作为「不存在」的证据**判别力 = 0**（`triage/47-…md` §4）。<br>⚠️ **别读反**：`Verse.Map` 上确实没有 `GetRegion` 这个成员，**但这条结论是上面那 8 条「枚举」推出来的**，**不是**那条过滤查询「查出来」的。 |
| F8 | **vanilla 里没有任何以 `Map` 为接收者的 `InBounds` 扩展** —— 名为 `InBounds` 的 `public static bool` 扩展共 **4** 个，接收者分别是 `T[,]`(`Gen.cs:375`) / `IntVec3` ×2(`GenGrid.cs:75` / `:85`) / `Vector3`(`GenGrid.cs:95`) | ⚠️ **2026-10-03 已换证据**（`triage/47-…md` §2.2 #6 实测）：`mcp__rimworld-source-online__search_source{ query: "public static bool InBounds" }` → **4 条**；`search_source{ query: "InBounds\(this Map" }` → **`No results found`**。范围 = 该服务**全部 vanilla `Source/` 语料**（⚠️ 与本表其余各行的 `rw16`/`decompilerserver` **不是同一个服务**，但**同样只覆盖 vanilla 本体、未检索任何模组程序集**）。<br>🚨 **原证据已作废**：`search_members(query="InBounds", declaringTypeFilter="Verse.Map", limit=20, contextAlias="rw16")` → 0 命中是**恒真断言**（`AGENT-BRIEF.md` §3.7 第 5 条）—— `declaringTypeFilter` **原理上看不见扩展方法**，**无论 `Verse.Map` 上有没有 `InBounds` 扩展都会返 0** ⇒ **判别力 = 0**（`triage/47-…md` §4） |
| F9 | `Verse.RegionType` 共 10 成员，8 个枚举值 `Fence` / `ImpassableFreeAirExchange` / `None` / **`Normal`**(`…:04001C5E:F`) / `Portal` / `Set_All` / `Set_Impassable` / `Set_Passable`，**无 `Region`** | `get_members_of_type(typeId="Verse.RegionType", mode="signatures", limit=20, contextAlias="rw16")` → `hasMore:false totalEstimate:10` |
| F10 | `Verse.GridsUtility.GetRegion` 是 **`public static`**，`Region GetRegion(IntVec3 loc, Map map, RegionType allowedRegionTypes)`，`…:06001B5D:M` | `get_member_signature(memberId="Verse.GridsUtility:GetRegion", contextAlias="rw16")` |
| F11 | `Verse.GridsUtility.GetFirstThing` **两支并存**，均 Public static：`T GetFirstThing(IntVec3 c, Map map)`(`…:06001B73:M`) 与 `Thing GetFirstThing(IntVec3 c, Map map, ThingDef def)`(`…:06001B71:M`) | `get_overloads(memberId="Verse.GridsUtility:GetFirstThing")` **只回 1 支**（泛型）；第二支由 `get_member_signature(memberId="61e4173561894da49d210260257b5097:06001B71:M")` 命中 |
| F12 | `Verse.GenGrid.Standable` 是 `public static Boolean Standable(IntVec3 c, Map map)`，`…:0600557E:M` | `get_member_signature(memberId="Verse.GenGrid:Standable", contextAlias="rw16")` |
| F13 | `ThingDefOf` 只有 `RimWorld.ThingDefOf`(`…:020035A7:T`) 与 `RimWorld.RoadPathingDefOf`(`…:020035ED:T`)，**无 `Verse.ThingDefOf`** | `search_types(query="ThingDefOf", limit=10, contextAlias="rw16")` |
| F14 | 29 个命名空间里**没有 `Verse.RimWorld`** | `list_namespaces(limit=40, contextAlias="rw16")` → `hasMore:false totalEstimate:29` |
| F15 | `Verse.CellRect.FromLimits` 是 `public static CellRect FromLimits(Int32 minX, Int32 minZ, Int32 maxX, Int32 maxZ)`，`…:06000427:M` | `get_member_signature(memberId="Verse.CellRect:FromLimits", contextAlias="rw16")` |
| F16 | `Verse.RegionGrid` 上**没有 `RegionAt`**；有 `Region GetValidRegionAt(IntVec3 c)`（Public，`…:06002103:M`）、`GetValidRegionAt_NoRebuild(IntVec3 c)`、`GetRegionAt_NoRebuild_InvalidAllowed(IntVec3 c)`、`SetRegionAt(IntVec3 c, Region reg)` | `get_member_details(memberId="Verse.RegionGrid:RegionAt", contextAlias="rw16")` → `member_not_found` + 候选全表 |

> ⚠️ **F11 的核验方式本身就是一条警告**：`get_overloads` 按**方法名**返回，会漏掉同名不同参的分支。⇒ **不要**据 `get_overloads` 的返回条数断言「只有 N 个重载」；要断言必须按 methodId 逐个点名（这正是本项目已栽 6 次的「局部检索当全局结论」）。

---

## 1. 文件结构

| 文件 | 动作 | 职责（每个文件一个明确职责） |
|---|---|---|
| `MCP/index.js` | **修改 3 处**：① `:4004` 之前新增 `firstCsCode()` / `COMPILE_GUIDANCE` / `compileErrorGuidance()`；② `:4536-4537` 改成「先判 `CS0127` + 其余交给 `compileErrorGuidance`」；③ `:5450` 的 `_testHooks` 加 `compileErrorGuidance` 导出 | 编译错误码 → 归因文案的**唯一来源**（文案与路由都在这里，switch 块里只留一句调用） |
| `MCP/test/compileErrorGuidance.test.js` | **新建** | ① 路由表覆盖 9 个错误码；② 每个码的文案红线（不误导）；③ `case 'execute_csharp_code'` 块切片的结构断言（`CS0127` 在兜底之前、文案含 `InteractiveHost`/`return`、**不含** `add_using_directive`） |
| `README.md` | **修改**：新增一节 `#### 编译错误码速查：看到 CS#### 怎么办` | 完整对照表：9 个错误码 × 一句话归因 × **已核实的正确答案** × **怎么核验**（点名 F1–F16） |
| `AI-DEPLOYMENT.md` | **修改**：新增一节 + 排障决策树新增 2 行 | 排障速查版（每码一行「看到什么 → 怎么办」），并指路到 `README.md` 那节 |
| `docs/issue-triage/plans/2026-10-01-02-…md` / `-10-…md` / `-12-…md` / `docs/mcp-吐槽板.md` | **只读，一个字都不改** | — |

**为什么新建测试文件而不是往 `toolchainFixes.test.js` 末尾追加**：计划 02 任务 7 步骤 1 明确要求「在 `MCP/test/toolchainFixes.test.js` **末尾追加**」2 条用例。本计划若也追加，两份计划会在同一文件末尾抢同一段位置 ⇒ 新建 `MCP/test/compileErrorGuidance.test.js`，`npm test` 的 glob `test/*.test.js` 自动收编，冲突面为零。

**为什么 `:4006`（`function attachUsageOnFailure`）之前是安全插入点**：该位置在模块顶层（不在 `switch` 内），且**在** `case 'execute_csharp_code'`（`:4530`）**之前**、**在**计划 02 任务 7 两条断言的切片区间（`case 'execute_csharp_code'` → `case 'reset_csharp_console'`）**之外** ⇒ 插入不会让那两条断言的 `block` 变长，也不会让 `:121` 的 `toolListLine`→`buildMenuNode` 切片吃到新内容。

---

## 2. 前置事实与本计划对三份裁决报告的复核修正

### 2.1 计划 02 任务 7 是否已覆盖 `:4536-4537`？—— **部分覆盖，本计划只补它没做的部分**

`docs/issue-triage/plans/2026-10-01-02-execute-csharp-code-output-capture.md` 共 **1535** 行，**任务 7 在 `:1423-1523`**，标题「`CS0127` 的编译失败归因（修 `:4536-4537` 的误导 guidance）」。逐字读完后：

| 计划 02 任务 7 已做 | 本计划是否重复做 |
|---|---|
| 在 `:4536-4537` 前置一个 **`CS0127` 独立归因分支**（步骤 3 的替换代码） | ❌ **不重复** —— 本计划的任务 2 直接给出**含该分支的最终形态**，`CS0127` 分支一次到位 |
| 加 2 条测试（步骤 1），其中「`CS0127` 必须在 `includes('CS0')` 之前」「该分支文案不得含 `add_using_directire`」 | ❌ **不重复** —— 本计划在新测试文件里**复刻这两条断言**，保证无论计划 02 任务 7 落不落地都成立 |
| 补其余 8 个错误码的分流 | ✅ **本计划做** —— 计划 02 任务 7 的兜底分支**原样保留了旧的单段文案**，`CS0103/CS0234/CS0121/CS0117/CS1061/CS0411/CS1503/CS0841` 仍然全部被误导向「缺 using 引用」 |
| 处理「运行时报错被标成 `COMPILATION_ERROR`」（[34] 报告缺口 B） | ✅ **本计划做** —— 同一个修法一起带走 |

⇒ **执行顺序：任务 2 可以在计划 02 任务 7 之前或之后落地，两种情况都合法**，判定规则写在任务 2 步骤 1。

### 2.2 🚨 计划 02 任务 7 自身有一处**自相矛盾**（本计划在任务 2 里顺手修掉，不去改计划 02）

计划 02 任务 7 **步骤 1** 的用例 2 断言：

```javascript
const cs0127Branch = block.slice(i, j);   // i = 第一个 'CS0127'，j = 第一个 "includes('CS0')"
assert.doesNotMatch(cs0127Branch, /add_using_directire/, 'CS0127 的归因绝不能建议 add_using_directive');
```

而计划 02 任务 7 **步骤 3** 给出的实现里，`CS0127` 分支的文案**含有** `add_using_directire` 这个词：

```javascript
r.guidance = '宿主方法 InteractiveHost 是 void：不要写 return <表达式>（只有 return; 合法）。'
  + '…这不是 using 缺失问题，加 add_using_directire 不会解决它。';
```

⇒ **它自己的实现会让它自己的用例 2 必然 FAIL。** 本计划任务 2 给出的 `CS0127` 分支文案改用「这与 using 无关」表述（**不出现 `add_using_directive` 字样**），同时**逐字保留** `InteractiveHost` 与 `return` 两个词（用例 1 要求它们出现在 `case 'execute_csharp_code'` 块内）⇒ 两条断言同时通过。**本计划不修改计划 02 文件，只在此登记该矛盾。**

### 2.3 板上那张对照表：哪些是「结论对、理由错」（写进文档时必须标出来）

⚠️ **这 4 条如果原样抄进文档，下一个人会照抄错因** ⇒ `README.md` 对照表有一列专门标「板上原话是否正确」，本列即为该列的内容。

| # | 板上位置 | 板上原话 | 复核结论 | 正确说法 |
|---|---|---|---|---|
| ✗1 | 吐槽板 `:63` | `map.area.InBounds(q)` → `CS1061: Map 无 area 定义`；理由写成「`map.Size` 是 **IntVec3 不是 `CellRect`**」⇒ 用 `CellRect.FromLimits(…)` | **⚠️ 结论对、理由错**。`CS1061` 属实（`map.area` 小写不存在），但**理由不是 `Size` 的类型** —— 直接原因是 **`Verse.Map.Area` 是 `Int32` 属性**（F3），压根不是一个能挂 `.InBounds` 的对象 | 小写 `area` 不存在（`CS1061` ✅）；`Map.Area` 是 **`Int32`** 属性；地图范围请用 `Verse.Map.Size`（`IntVec3`，F4）+ `Verse.CellRect.FromLimits(0, 0, map.Size.x - 1, map.Size.z - 1)`（F15） |
| ✗2 | 吐槽板 `:64` | 「`CellRect` **没有 `InBounds`**，只有 `Contains`」 | **🔴 理由错**。`Verse.CellRect.InBounds` **存在**，签名 `Boolean InBounds(Map map)`（F1）。`CS1503: IntVec3 不能转成 Map` **恰恰因为它收的是 `Map`** —— 板上把「参数类型不对」读成了「方法不存在」 | 「`CellRect` **有** `InBounds`，但收的是 `Verse.Map`（F1）⇒ 传 `IntVec3` 必得 `CS1503`；判断某个 `IntVec3` 在不在 `CellRect` 里用 `Boolean Contains(IntVec3 c)`（F2）」。**「用 `Contains`」这个结论本身是对的，错的只是理由** |
| ✗3 | 吐槽板 `:64` 括号 | 「无 using 时 `x.InBounds(y)` 会误绑到 `Map` 的扩展」 | **🔴 错（结论保留；证据已于 2026-10-03 更换）**。证据 = **F8**：`search_source(query:"public static bool InBounds")`，**全仓、不带 `declaringType` 过滤** → **4 条**，接收者分别为 `T[,]` / `IntVec3` ×2 / `Vector3`；`search_source(query:"InBounds\(this Map")` → **`No results found`** ⇒ `Verse.Map` 上**没有任何名为 `InBounds` 的成员、更没有以 `Map` 为接收者的同名扩展**。<br>🚨 **原证据 `search_members(query="InBounds", declaringTypeFilter="Verse.Map")` → 0 命中，已作废**：那是**恒真断言**（`AGENT-BRIEF.md` §3.7 第 5 条）—— `declaringTypeFilter` 按「声明类型」过滤，而**扩展方法的声明类型是静态类、不是被扩展类型**（`triage/47-…md` §4）⇒ 该查询**原理上看不见扩展方法**，**无论结果如何都会返 0**，作为「不存在」的证据**判别力 = 0**。<br>⚠️ **同一毛病也在 F7**（`Verse.Map.GetRegion`，同样是 `declaringTypeFilter` 的 0 命中）—— **F7 已于 2026-10-03 一并换成无过滤的全程序集枚举（`totalEstimate: 8`）**，见 `:57`。 | 删掉这句。「误绑到扩展」在 vanilla 上不成立（模组程序集未检索，见 §0 检索范围） |
| ✗4 | 吐槽板 `:254`、`:269` | 「`map.regionManager` 是 1.6 里的 **internal 字段**，不是 property，`GetProperty` 也返 `null`」 | **⚠️ 结论对、理由错（且错得更彻底）**。`CS1061` 属实；但**这个成员压根不存在** —— `search_members(query="regionManager")` **全程序集 0 命中**（F6）。真字段叫 **`regionGrid`**（小写 g），而且是 **`public` 字段**（F5），不是 internal | 「`Verse.Map` 上**没有** `regionManager`（全程序集 0 命中）；区域网格的真字段是 `public` 字段 `regionGrid`（`RegionGrid regionGrid`）；`Verse.Map` 上也**没有** `GetRegion`（F7）」 |

**板上完全正确、可直接沿用的 5 条**（`README.md` 对照表照抄这些，不用再核）：

| 板上写的 | 复核 | 依据 |
|---|---|---|
| `Verse.RimWorld.Pawn` → 应写 `Verse.Pawn` | ✅ 正确 | F14（29 个命名空间里无 `Verse.RimWorld`） |
| `Verse.ThingDefOf.Wall` → 应写 `RimWorld.ThingDefOf.Wall` | ✅ 正确 | F13 |
| `GenGrid.Standable` 是静态、`IntVec3` 在前 | ✅ 正确 | F12 |
| `GridsUtility.GetFirstThing` 2 参调用必须显式类型实参 | ✅ 正确 | F11 |
| `map.Size` 是 `IntVec3` | ✅ 正确 | F4 |

### 2.4 本计划复核后**新增**的两条纠正（两份裁决报告都没提到）

| # | 纠正 | 依据 |
|---|---|---|
| **N1** | 板上 `:254` 的 `map.regionManager.RegionAt(...)` **错在两处**：字段名错（→ `regionGrid`，F5）**且方法名也错** —— `Verse.RegionGrid` 上**没有 `RegionAt`**，只有 `Region GetValidRegionAt(IntVec3 c)`（F16）。⇒ 推荐写法是 `map.regionGrid.GetValidRegionAt(c)`，或更省事的 `Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal)`（F10） | F16（`get_member_details("Verse.RegionGrid:RegionAt")` → `member_not_found`，候选全表已读） |
| **N2** | 板上 `:260-265` 给的**反射绕过是多余的** —— `Verse.GridsUtility.GetRegion` 是 **`public static`**（F10），全限定直调即可：`Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal)`。反射版只是「因为二义才绕」的产物；不用 `using` 就不会有 CS0121 | F10 |

### 2.5 裁决报告本身的三处错误（本计划按修正后的版本执行）

| # | 报告原话 | 复核 | 本计划采用 |
|---|---|---|---|
| E1 | [33] 报告 §2.3 + [35] 报告 §2.1：「本报告**没有核实** `Map.regionGrid` 的可见性（`search_members` 不返回 `accessibility`）……因此**不写**『它是 internal / public』」 | ❌ **该未知项已闭合** —— `get_member_details(memberId="Verse.Map:regionGrid")` 返回 `accessibility: "Public"`（F5）。正确入口是 `get_member_details`，不是 `search_members` | `regionGrid` 是 **`public` 字段**，可以直接在控制台里 `map.regionGrid.…` |
| E2 | [33] 报告 §5.1 + [35] 报告 §0/§3.4：「合计 **8 个**错误码」（枚举出的码是 `CS0127` / `CS0103` / `CS0234` / `CS0121` / `CS0117` / `CS1061` / `CS0411` / `CS1503` / `CS0841`） | ❌ **实际是 9 个** —— 逐个数一遍是 9 | 本计划按 **9 个**实现与测试 |
| E3 | [35] 报告 §2 第 5 行用 `get_overloads` 论证「两个同名 `GetFirstThing` 并存」 | ⚠️ **论证方式不成立** —— `get_overloads(memberId="Verse.GridsUtility:GetFirstThing")` 本轮实测**只返回 1 支**（泛型那支），非泛型那支（`…:06001B71:M`）**不在返回里**。两支并存这个**结论是对的**（由 `get_member_signature` 按 memberId 点名确认，F11），但**不能**用 `get_overloads` 的返回条数证明 | 本计划在 §0 的 F11 下写明这条核验方法警告，并把它作为「局部检索当全局结论」的又一例证 |

---

## 3. 任务

### 任务 1：`compileErrorGuidance()` 纯函数 + 9 码路由表（模块顶层，不碰 switch）

**文件：**
- 修改：`MCP/index.js:4004` 之前（**在** `// 失败响应附加 usage/相似工具建议…` 那行注释**上面**插入整段）
- 修改：`MCP/index.js:5450-5459` 的 `export const _testHooks = { … }`（加一个导出项）
- 测试：`MCP/test/compileErrorGuidance.test.js`（**新建**）

- [ ] **步骤 1：跑一次基线，确认动手前是绿的**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
```

预期：全 `pass`。**若有 pre-existing FAIL，停下来先记录，不要在本任务里顺手修** —— 那是别人的账。

- [ ] **步骤 2：写失败的测试**

新建 `MCP/test/compileErrorGuidance.test.js`，全文如下（**逐字写入，不要改写**）：

```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8');
const guidance = mod._testHooks.compileErrorGuidance;

// 计划 2026-10-02-33-35：9 个错误码的归因分流（吐槽板 [33]/[34]/[35]）
const CS0127_GUIDANCE_MARK = 'InteractiveHost';

test('§1 compileErrorGuidance：9 个已核实错误码都不得落进旧兜底文案', () => {
  const codes = [
    'CS0103', 'CS0234', 'CS0121', 'CS0117',
    'CS1061', 'CS0411', 'CS1503', 'CS0841',
  ];
  for (const code of codes) {
    const g = guidance(`${code}: something`);
    assert.notEqual(
      g,
      '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。',
      `${code} 落进了旧兜底文案 —— 说明路由表没覆盖它`,
    );
    assert.ok(g.length > 20, `${code} 的归因文案过短，疑似兜底`);
  }
});

test('§1 compileErrorGuidance：每支文案都带「已核实的正确答案」，不是空话', () => {
  const expect = {
    CS0117: ['RegionType.Normal', 'regionGrid'],
    CS1061: ['Int32', 'CellRect.FromLimits'],
    CS0411: ['GetFirstThing<Verse.Thing>'],
    CS1503: ['InBounds', 'Contains'],
    CS0841: ['Verse.Pawn'],
    CS0121: ['GridsUtility.GetRegion', 'RegionType.Normal'],
    CS0234: ['RimWorld.ThingDefOf'],
    CS0103: ['GenTicks'],
  };
  for (const [code, needles] of Object.entries(expect)) {
    const g = guidance(`${code}: x`);
    for (const n of needles) {
      assert.ok(g.includes(n), `${code} 的文案缺「${n}」—— 没有给出可执行的正确答案`);
    }
  }
});

test('§1 compileErrorGuidance：CS0103 / CS0234 之外不得建议 add_using_directive', () => {
  // CS0103（命名空间缺失）与 CS0234 是唯一两支「加 using」真能解的；
  // 其余 6 支提 using 都是把用户引到无关维度（计划 02 任务 7 用例 2 立的规矩，本计划扩展到全部）
  const noUsingAdvice = ['CS0127', 'CS0117', 'CS0121', 'CS1061', 'CS0411', 'CS1503', 'CS0841'];
  for (const code of noUsingAdvice) {
    assert.doesNotMatch(
      guidance(`${code}: x`),
      /add_using_directive/,
      `${code} 的归因绝不能建议 add_using_directive`,
    );
  }
  // 反向：CS0103 必须仍然建议 using（这是它唯一正确的出路）
  assert.match(guidance('CS0103: x'), /add_using_directive/);
});

test('§1 compileErrorGuidance：非编译失败（REPL 运行时异常）不得背「缺 using」的锅', () => {
  const g = guidance('Exception invoking REPL: System.NullReferenceException: Object reference not set');
  assert.match(g, /不是编译失败/);
  assert.match(g, /Exception invoking REPL/);
  assert.doesNotMatch(g, /add_using_directive/, '运行时异常与 using 无关');
});

test('§1 compileErrorGuidance：既无 CS 码也无 REPL 字样 → 才回旧兜底文案', () => {
  assert.equal(
    guidance('some opaque failure'),
    '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。',
  );
  assert.equal(guidance(undefined), '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。');
});

test('§1 compileErrorGuidance：一次报多个错误码时，提示先修第一个', () => {
  const g = guidance('(3,1): error CS0234: x\n(4,1): error CS1061: y');
  assert.match(g, /同时出现多个错误码/);
  assert.ok(g.includes('Verse.ThingDefOf'), '归因仍按第一个码 CS0234 走');
});

test('§2 case execute_csharp_code：CS0127 分支必须排在 CS0x 兜底之前', () => {
  const block = source.slice(
    source.indexOf("case 'execute_csharp_code'"),
    source.indexOf("case 'reset_csharp_console'"),
  );
  assert.ok(block.length > 0, '找不到 execute_csharp_code 块');
  assert.match(block, /CS0127/, '必须显式识别 CS0127');
  assert.ok(
    block.indexOf('CS0127') < block.indexOf("includes('CS0')"),
    'CS0127 分支必须排在 CS0x 兜底分支之前，否则永远命中兜底',
  );
  assert.ok(block.includes(CS0127_GUIDANCE_MARK), `CS0127 分支文案必须点名 ${CS0127_GUIDANCE_MARK}`);
  assert.match(block, /return/, 'CS0127 分支文案必须提到 return 表达式');
  assert.doesNotMatch(
    block.slice(block.indexOf('CS0127'), block.indexOf("includes('CS0')")),
    /add_using_directive/,
    'CS0127 分支文案绝不能出现 add_using_directive',
  );
  // 旧的那段单点文案必须已经离开 switch 块（改由 compileErrorGuidance 接管）
  assert.doesNotMatch(
    block,
    /请使用全限定类型名（如 Verse\.GenTicks\.TicksGame）/,
    'switch 块里不该再硬编旧兜底文案，否则两处会各说各话',
  );
});
```

- [ ] **步骤 3：运行测试验证失败**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
```

预期：前 6 条 **FAIL**（`_testHooks.compileErrorGuidance` 是 `undefined`）；第 7 条（`§2`）**FAIL**（`block` 里还没有 `CS0127`）。

- [ ] **步骤 4：加路由表与纯函数**

用 `edit` 把 `MCP/index.js` 里这一行

```javascript
// 失败响应附加 usage/相似工具建议（仅 isError === true 时处理；内部任何异常一律原样返回原 result）：
```

替换为**下面整段 + 原来那一行**（即：把新代码插在这行注释**上面**）：

```javascript
// ===========================================================================
// 编译错误码 → 归因文案（execute_csharp_code 专用；模块顶层，供 case 块调用）
// 背景：docs/mcp-吐槽板.md 的 [33] / [34] / [35]。旧实现对**所有** CS0* 统一回
// 「默认没有 using 引用，请用全限定类型名或先 add_using_directive」—— 这条对
// CS0234 之外的 7 支（CS0121 / CS0117 / CS1061 / CS0411 / CS1503 / CS0841 / CS0127）
// 全是误导。console/execute 的 error 文本形如 `(20,1): error CS0127: <消息>`，
// 取第一个 CS#### 决定归因；取不到先判 REPL 运行时异常；再取不到才回旧兜底。
//
// 文案里每条「正确答案」都已在 decompilerserver 上下文 rw16（仅 vanilla
// Assembly-CSharp.dll，MVID 61e4173561894da49d210260257b5097）核实过；
// 核验方式见 docs/issue-triage/plans/2026-10-02-33-35-…md §0 事实表 F1-F16。
// ⚠️ 「不存在 / 查不到」的结论只对 vanilla 成立，模组程序集未检索。
// ===========================================================================

// 取第一个 CS#### （严格 4 位，避免把长串里的子串当成别的码）
function firstCsCode(errorText) {
  if (typeof errorText !== 'string') return '';
  const m = errorText.match(/CS\d{4}/);
  return m ? m[0] : '';
}

const COMPILE_GUIDANCE_FALLBACK =
  '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';

const REPL_EXCEPTION_GUIDANCE =
  '这不是编译失败，是用户代码在 REPL 里抛的异常（execute_csharp_code 目前把它也标成 errorCode=COMPILATION_ERROR）。'
  + '请看后面 "Exception invoking REPL:" 之后的真实异常类型与消息，按那个方向排查；它与 using 引用无关。'
  + '另：宿主是 void 方法，异常文本里若带 CS####，以第一个 CS#### 为准。';

const MULTI_CODE_NOTE =
  '（本条同时出现多个错误码，上面按第一个 CS#### 归因；修完它再重试一次，后续错误才会露出来。）';

const COMPILE_GUIDANCE = {
  CS0103:
    '控制台默认没有 using 引用，裸类型名（如 GenTicks）解析不到。'
    + '请写全限定名（如 Verse.GenTicks.TicksGame），或先调 add_using_directive 加上 using Verse;。'
    + '不确定类型归哪个命名空间时：游戏类型只有 Verse 与 RimWorld 两个根命名空间，先用 decompilerserver 的 list_namespaces 查。',
  CS0234:
    '命名空间或类型名解析不到。已核实（vanilla Assembly-CSharp）：不存在 Verse.RimWorld 这个命名空间，Pawn 要写 Verse.Pawn；'
    + '也不存在 Verse.ThingDefOf，DefOf 类都在 RimWorld 下，要写 RimWorld.ThingDefOf.Wall。'
    + '查法：decompilerserver 的 list_namespaces() / search_types(query)。',
  CS0121:
    '两个候选签名逐字相同，说明同一支方法被解析到了两次（控制台把同一程序集重复引用），不是重载歧义，也不是你写错。'
    + '不要靠 using 去解二义 —— 改写全限定静态调用即可。已核实：Verse.GridsUtility.GetRegion 是 public static，'
    + '签名 Region GetRegion(IntVec3 loc, Map map, RegionType allowedRegionTypes)，全程序集只有这一支 GetRegion(this IntVec3, …)。'
    + '直接写 Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal) 即可，不需要反射。',
  CS0117:
    '该类型上没有这个静态成员或枚举值。已核实的高频两个：(1)「普通地面 Region」的值是 Verse.RegionType.Normal —— '
    + 'RegionType 根本没有 Region 这个成员，8 个值是 None / ImpassableFreeAirExchange / Set_Impassable / Normal / Portal / Fence / Set_Passable / Set_All。'
    + '(2) Verse.Map 上既没有 regionManager 也没有 GetRegion；区域网格的真字段名是 public 字段 regionGrid（RegionGrid regionGrid）。'
    + '查法：decompilerserver 的 get_members_of_type(typeId) 拿全表。',
  CS1061:
    '该类型上没有这个成员（或写的不是它 / 大小写不对）。已核实的高频：Verse.Map.Area 是 Int32 属性，Map 上没有小写 area，'
    + '所以 map.area 必然 CS1061 —— 它压根不是一个能挂 .InBounds 的对象。'
    + '地图范围要自己算：Verse.Map.Size 是 IntVec3，用 Verse.CellRect.FromLimits(0, 0, map.Size.x - 1, map.Size.z - 1) 拿 CellRect。',
  CS0411:
    '泛型方法推不出类型实参。已核实：Verse.GridsUtility.GetFirstThing 有两支同名方法（都是 public static），'
    + '2 参那支是泛型 T GetFirstThing(IntVec3 c, Map map)，T 无处推断。'
    + '必须显式给实参：Verse.GridsUtility.GetFirstThing<Verse.Thing>(q, map)。',
  CS1503:
    '实参类型对不上。已核实的高频：Verse.CellRect.InBounds 是存在的，签名 Boolean InBounds(Map map) —— 它收的是 Map 不是 IntVec3，'
    + '所以 rect.InBounds(q) 报的就是 CS1503；判断某个 IntVec3 在不在 CellRect 里请用 Boolean Contains(IntVec3 c)。'
    + '另外 Verse.GenGrid.Standable 是 public static Boolean Standable(IntVec3 c, Map map)，是静态方法、IntVec3 在前，别写成 q.Standable(map)。',
  CS0841:
    '这条在无 using 控制台里通常是「类型解析失败」的伪装，不是变量作用域或代码顺序问题。'
    + '先看上一行的类型名能不能解析（例如 Verse.RimWorld.Pawn 解析失败后，编译器会把 flyer 报成用前未声明）；'
    + '类型名改对（Verse.Pawn）后本条自然消失。',
};

function compileErrorGuidance(errorText) {
  const text = typeof errorText === 'string' ? errorText : '';
  const code = firstCsCode(text);
  // Object.hasOwn：code 来自正则只可能是 CS#### 或 ''，但仍显式判自有属性，杜绝原型链上的意外命中
  let g = Object.hasOwn(COMPILE_GUIDANCE, code) ? COMPILE_GUIDANCE[code] : '';
  if (!g) g = text.includes('Exception invoking REPL') ? REPL_EXCEPTION_GUIDANCE : COMPILE_GUIDANCE_FALLBACK;
  const distinct = Array.from(new Set(text.match(/CS\d{4}/g) || []));
  if (distinct.length > 1) g += ' ' + MULTI_CODE_NOTE;
  return g;
}

// 失败响应附加 usage/相似工具建议（仅 isError === true 时处理；内部任何异常一律原样返回原 result）：
```

⚠️ **改完立刻核对**：

```powershell
$p='C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP\index.js'
[System.IO.File]::ReadAllLines($p).Count
```

预期：`5460` 变成**约 `5550`**（净增约 90 行）。**若变成 0 或明显偏小，立刻停止** —— 说明编辑出了问题（本仓库已栽过整文件被覆写成 0 字节的事故）。

- [ ] **步骤 5：加测试导出**

用 `edit` 把 `MCP/index.js:5458` 那行

```javascript
  resetDpaState: () => { dpaRoundActive = false; },
};
```

替换为

```javascript
  resetDpaState: () => { dpaRoundActive = false; },
  compileErrorGuidance,
};
```

- [ ] **步骤 6：运行测试验证通过**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
```

预期：新增的 6 条全 `pass`；**第 7 条（`§2`）仍 FAIL** —— 它要等任务 2 改 `switch` 块，这是正常的（任务 1 的交付物是纯函数，不是接线）。既有 9 个测试文件的用例全 `pass`，`toolchainFixes.test.js` 的 `:121` / `:263-268` 两条切片断言**不受影响**（本任务的插入点在它们两个切片之外）。

- [ ] **步骤 7：改动清单**

```
新增 MCP/test/compileErrorGuidance.test.js（7 条用例）
修改 MCP/index.js  —— 在 “// 失败响应附加 usage/相似工具建议…” 注释之前插入 firstCsCode /
                      COMPILE_GUIDANCE_FALLBACK / REPL_EXCEPTION_GUIDANCE / MULTI_CODE_NOTE /
                      COMPILE_GUIDANCE / compileErrorGuidance
修改 MCP/index.js  —— _testHooks 增加 compileErrorGuidance 导出
未改 :2937（计划 02 任务 4 领地）、未改 :2072/:2078（计划 12 任务 1 领地）
未改 MCP/test/toolchainFixes.test.js（避免与计划 02 任务 7 抢文件末尾）
未改任何游戏侧文件；未编译、未启动游戏、未调用任何 MCP 工具
```

---

### 任务 2：`:4536-4537` 接线 —— 按错误码分流（含与计划 02 任务 7 的串行规则）

**文件：**
- 修改：`MCP/index.js:4535-4538`（`case 'execute_csharp_code'` 块内，`// 测试踩坑…` 注释到 `}` 之间）
- 测试：`MCP/test/compileErrorGuidance.test.js`（**追加** 1 条断言到已有的 `§2` 用例里）

**这是本计划唯一的 MCP 逻辑落点，也是三方零冲突的核心理由。** `:2937`（计划 02 任务 4）、`:2072`/`:2078`（计划 12 任务 1）本任务**一个字都不碰**。

- [ ] **步骤 1：判定计划 02 任务 7 落没落地（决定改法，不是可选项）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path 'MCP\index.js' -Pattern 'CS0127'
```

| 结果 | 情形 | 本任务怎么做 |
|---|---|---|
| **0 命中** | 计划 02 任务 7 **未**落地 | 走步骤 3 的「形态 A」：把 `:4535-4538` 四行整体替换成本任务的最终代码。替换后 `CS0127` 已在内 ⇒ **计划 02 任务 7 已被本任务完全吸收**，在本计划 §6 交接表登记一行「计划 02 任务 7 落地前先看本任务 2，命中 `CS0127` 即视为已吸收」 |
| **有命中** | 计划 02 任务 7 **已**落地 | 走步骤 3 的「形态 B」：只把它那条 `CS0127` 分支的**文案**换成本任务的最终文案（它的分支位置、`const csErr`、`else if` 结构已经是对的），**并**在它后面补上 `compileErrorGuidance(csErr)` 调用 |

⚠️ 两种形态的**最终产物逐字相同**。这意味着：本任务与计划 02 任务 7 **谁先落地都不冲突**（先落地者写下最终形态，后落地者 `edit` 的 `old_string` 匹配不上 ⇒ **报错、文件不动** —— 这正是我们要的行为，不是失败）。

- [ ] **步骤 2：跑一次测试，确认 `§2` 用例此刻是红的**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
```

预期：只有 `§2 case execute_csharp_code：CS0127 分支必须排在 CS0x 兜底之前` 这条 **FAIL**（它要找的 `CS0127` 字面量此刻不在 `case 'execute_csharp_code'` 块内）。

- [ ] **步骤 3：改 `switch` 块**

**形态 A（步骤 1 判定为「未落地」时）：** 用 `edit` 把 `MCP/index.js` 里这四行

```javascript
        // 测试踩坑：控制台默认无 using 引用，GenTicks 等裸类型名会报 CS0103，须用全限定名或先 add_using_directive
        if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || (typeof r.error === 'string' && r.error.includes('CS0')))) {
          r.guidance = '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';
        }
```

替换为

```javascript
        // 编译失败归因 —— 顺序要紧：CS0127 必须先判。宿主方法 InteractiveHost 返回 void，
        // 写 return <表达式>; 必然报 CS0127；它不是「缺 using」，落进下面的 CS0x 兜底会被引到无关维度
        // （docs/mcp-吐槽板.md [34]；计划 2026-10-01-02-… 任务 7 立的是同一条规矩）。
        // 其余 8 支（CS0103/CS0234/CS0121/CS0117/CS1061/CS0411/CS1503/CS0841）与「运行时报错被误标成
        // COMPILATION_ERROR」全部交给 compileErrorGuidance 路由，文案与核验方式见该函数上方注释。
        const csErr = typeof r.error === 'string' ? r.error : '';
        if (!r.success && firstCsCode(csErr) === 'CS0127') {
          r.guidance = '宿主方法 InteractiveHost 返回 void：不要写 return <表达式>（只有 return; 合法）。'
            + '要让值回到 result，请把它写成末尾裸表达式，例如 var sb = new System.Text.StringBuilder(); sb.Append("x"); sb.ToString();。'
            + '这与 using 无关，宿主签名也不是本项目能改的。';
        } else if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || csErr.includes('CS0'))) {
          r.guidance = compileErrorGuidance(csErr);
        }
```

> ⚠️ **关于 `:4535` 那行 `// 测试踩坑：…` 注释为什么一并换掉**：它原文写着「须用全限定名或先 `add_using_directire`」，**对 9 个错误码里的 8 个是错的**，留着就等于在新路由旁边挂一条旧的反向指引。
> **认领依据**：`MCP/index.js:4535` 位于 `case 'execute_csharp_code'` 块内；计划 02 §1 的 `MCP/index.js` 修改清单是 `:2937` / `:2073-2082` / `:4539`，计划 12 是 `:2072` / `:2078` —— **没有任何计划认领 `:4535`**。
> ⚠️ **对计划 02 任务 7 的影响**：它步骤 3 的 `old_string` 只覆盖 `:4536-4537` 两行。若本任务先落地，计划 02 任务 7 的 `edit` 会因不匹配而**报错、文件不动**（安全）；若计划 02 任务 7 先落地，本任务走形态 B。

**形态 B（步骤 1 判定为「已落地」时）：** 只把计划 02 任务 7 写下的那条 `CS0127` 分支的三行文案，整体替换为形态 A 里的同样三行；并在它后面那条 `} else if (...) {` 的**下一行**，把

```javascript
          r.guidance = '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';
```

替换为

```javascript
          r.guidance = compileErrorGuidance(csErr);
```

并在 `:4535` 那行 `// 测试踩坑：…` 注释**原样保留或替换**（若计划 02 未动它，按形态 A 的注释整段替换它）。

- [ ] **步骤 4：追加一条断言到 `§2` 用例**

用 `edit` 在 `MCP/test/compileErrorGuidance.test.js` 的 `§2` 用例**末尾**（`assert.doesNotMatch(block, /请使用全限定类型名（如 Verse\.GenTicks\.TicksGame）/, …);` 那一行之后）追加：

```javascript
  assert.match(
    block,
    /compileErrorGuidance\(csErr\)/,
    '兜底分支必须委托给 compileErrorGuidance —— 否则路由表写了也不会生效',
  );
```

- [ ] **步骤 5：运行测试验证通过**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
```

预期：**全 `pass`** —— 新增 7 条 + 既有 9 个测试文件全部通过。特别确认 `toolchainFixes.test.js` 里两条源码切片断言（`:121` 的 `toolListLine`、`:263-268` 的 `start_game`/`stop_game`）仍是 `pass`（本任务与任务 1 都没碰它们两个切片）。

若计划 02 任务 7 **已**落地，额外确认它的两条用例也是 `pass`：
- 「`CS0127` 走独立归因，不落进 CS0x 的 using 兜底分支」
- 「`CS0127` 的 guidance 不得建议 add_using_directire」
（这两条原本会因计划 02 自己文案里的 `add_using_directire` 字样而 FAIL —— 换成本计划形态 A 的文案后同时通过，见 §2.2。）

- [ ] **步骤 6：语法自查（可选，`npm test` 已经是权威闸门）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --check index.js
```

预期：无输出、退出码 `0`。**注意**：`npm test` 会真的 `import('../index.js')`，语法错必然当场炸 ⇒ `npm test` 通过即已覆盖这一步，`node --check` 只是提前暴露。

- [ ] **步骤 7：改动清单**

```
修改 MCP/index.js:4535-4538（switch 块：注释 + CS0127 独立分支 + 其余委托 compileErrorGuidance）
修改 MCP/test/compileErrorGuidance.test.js（§2 用例追加 1 条断言）
未改 :2937（计划 02 任务 4 领地）、未改 :2072/:2078（计划 12 任务 1 领地）
未改 :4539 之后的 return r;（计划 02 任务 5 的插入点）
未改任何游戏侧文件；未构建、未启动游戏、未调用任何 MCP 工具
```

---

### 任务 3：`README.md` 新增对照表小节（离线可查的完整版）

**文件：**
- 修改：`README.md` —— 在 `## 设置`（现有 **230** 行）之前新增一节

**与计划 10 任务 3 的编辑协调（同文件、不同段落）**

计划 10 任务 3（`2026-10-01-10-api-member-visibility-tools.md:670-815`）也在改 `README.md`，它的步骤 3 会在 `README.md:228` 之后、`:230` 的 `## 设置` 之前插入 `#### 成员可见性该去哪问`。**两份计划改同一个文件 ⇒ 必须串行，规则如下：**

| 谁先落地 | 插入位置 | 后落地者的锚点 |
|---|---|---|
| **计划 10 任务 3 先** | 本任务插在它的 `#### 成员可见性该去哪问` 小节**之后**（仍在 `## 设置` 之前） | —— |
| **本任务先** | 本任务插在 `README.md:228` 之后、`:230` 之前 | 计划 10 任务 3 的执行者**改用本任务的小节标题 `#### 编译错误码速查：看到 CS#### 怎么办` 作锚点**，把它的 `#### 成员可见性该去哪问` 插在本任务小节**之前** |

⇒ 两条小节**内容互不重叠**（计划 10 治「成员可见性该去哪问」，本任务治「撞到 CS#### 怎么办」），但**语义相邻** ⇒ 挂在一起才对。**任何一方都不要为了避让而改动对方的小节正文。**

- [ ] **步骤 1：确认当前落点（不要凭行号直接下手）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$p = Join-Path (Get-Location) 'README.md'
[System.IO.File]::ReadAllLines($p).Count
Select-String -Path $p -Pattern '^## 设置$'
Select-String -Path $p -Pattern '^#### 成员可见性该去哪问$'
```

预期：总行数 **243**（若计划 10 已落地则更大，以现场为准）；`## 设置` 命中 1 次；`#### 成员可见性该去哪问` 命中 0 次表示计划 10 未落地、1 次表示已落地。**按结果选插入点，不要按记忆里的 243 / 230 下手。**

- [ ] **步骤 2：插入小节**

用 `edit`，把 `README.md` 里 `## 设置` **上面紧邻的那一行**（计划 10 未落地时是 `:229` 的空行，计划 10 已落地时是它小节的最后一行）替换为**下面整段 + 原来那一行**。

```markdown
#### 编译错误码速查：看到 CS#### 怎么办

`execute_csharp_code` 的 C# 控制台**默认没有 using 引用**，所以下面这些不是「你写错了」，而是「默认环境如此」。
失败响应里的 `guidance` 字段已按错误码分流；本节是同一张表的**离线版** —— 不启动游戏也能查。

**检索范围（先读再引用）**：下表所有「存在 / 不存在」均由 decompilerserver 在**仅 vanilla 的** `Assembly-CSharp.dll`（MVID `61e4173561894da49d210260257b5097`）上核实，**未检索任何模组程序集**。模组若自带同名类型或扩展方法，按该模组的上下文重核。

| 错误码 | 一句话归因 | 已核实的正确答案 | 怎么自己核验 |
|---|---|---|---|
| `CS0103` | 裸类型名解析不到（缺 using） | 写全限定名 `Verse.GenTicks.TicksGame`；或先 `add_using_directive` 加上 `using Verse;` | `list_namespaces()` 看游戏类型只有 `Verse` / `RimWorld` 两个根 |
| `CS0234` | 命名空间或类型名不存在 | `Verse.Pawn`（**没有** `Verse.RimWorld` 这个命名空间）；`RimWorld.ThingDefOf.Wall`（**没有** `Verse.ThingDefOf`） | `list_namespaces()` / `search_types(query)` |
| `CS0121` | 两个候选签名**逐字相同** = 同一支方法被解析到两次（控制台重复引用同一程序集），**不是**重载歧义 | 别用 using 解二义。全限定静态直调即可：`Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal)` —— `public static`，**不需要反射** | `get_member_signature("Verse.GridsUtility:GetRegion")` |
| `CS0117` | 该类型上没有这个静态成员 / 枚举值 | 「普通地面 Region」的值是 **`RegionType.Normal`**（`RegionType` 根本没有 `Region`）。8 个值：`None` / `ImpassableFreeAirExchange` / `Set_Impassable` / `Normal` / `Portal` / `Fence` / `Set_Passable` / `Set_All` | `get_members_of_type("Verse.RegionType")` |
| `CS1061` | 该类型上没有这个成员 | `Verse.Map.Area` 是 **`Int32` 属性**（没有小写 `area`）。地图范围用 `Verse.Map.Size`（`IntVec3`）算：`Verse.CellRect.FromLimits(0, 0, map.Size.x - 1, map.Size.z - 1)` | `get_member_details("Verse.Map:Area")` / `…:Size` |
| `CS0411` | 泛型方法推不出类型实参 | `Verse.GridsUtility.GetFirstThing<Verse.Thing>(q, map)` —— 2 参那支是 `T GetFirstThing(IntVec3 c, Map map)`，必须显式实参 | `get_members_of_type("Verse.GridsUtility")` 看同名两支 |
| `CS1503` | 实参类型对不上 | `CellRect` **有** `InBounds`，但签名是 `Boolean InBounds(Map map)` —— 收的是 `Map` 不是 `IntVec3`。判断某个 `IntVec3` 在不在 `CellRect` 里用 `Boolean Contains(IntVec3 c)`。另：`Verse.GenGrid.Standable(IntVec3 c, Map map)` 是**静态**方法、`IntVec3` 在前 | `get_member_signature("Verse.CellRect:InBounds")` / `…:Contains` |
| `CS0841` | 无 using 控制台里通常是「类型解析失败」的伪装，**不是**变量作用域问题 | 先修上一行的类型名（`Verse.RimWorld.Pawn` → `Verse.Pawn`），本条自然消失 | `search_types(query)` |
| `CS0127` | 宿主方法 `InteractiveHost` 返回 **`void`** | 不要写 `return <表达式>`（只有 `return;` 合法）。要回传值就写成**末尾裸表达式** | 见上一节「成员可见性该去哪问」 |
| 运行时报错（`Exception invoking REPL: …`，被误标成 `COMPILATION_ERROR`） | 不是编译失败，是用户代码抛的异常 | 按 `Exception invoking REPL:` 之后的真实异常类型排查 | —— |

**三条最容易照抄错的（它们的「结论」对、「理由」错）** —— 写代码时别把错因一起抄走：

| 常见说法 | 结论 | 实际情况 |
|---|---|---|
| 「`CellRect` 没有 `InBounds`，只有 `Contains`」 | ✅ 用 `Contains` 对 | ❌ 理由错：`Verse.CellRect.InBounds` **存在**，签名 `Boolean InBounds(Map map)`。`CS1503: IntVec3 不能转成 Map` **恰恰因为它收的是 `Map`** |
| 「`Map.regionManager` 是 internal 字段」 | ✅ `map.regionManager` 必报 `CS1061` | ❌ 理由错：这个成员**压根不存在**（全程序集 `regionManager` **0 命中**）。区域网格的真字段是 **`regionGrid`**（`public` 字段 `RegionGrid regionGrid`），且 `Verse.Map` 上也**没有** `GetRegion`。另：`RegionGrid` 上也**没有** `RegionAt`，只有 `Region GetValidRegionAt(IntVec3 c)` |
| 「`Map` 上没有小写 `area`，是因为 `Size` 是 `IntVec3`」 | ✅ `map.area` 必报 `CS1061` | ❌ 理由错：直接原因是 **`Verse.Map.Area` 是 `Int32` 属性**，压根不是能挂 `.InBounds` 的对象。`Size` 是 `IntVec3` 是另一条独立事实 |

**核验方法的一条硬规矩**：`get_overloads` 按**方法名**返回，**会漏掉同名不同参的分支**（实测 `Verse.GridsUtility.GetFirstThing` 只回 1 支，另一支要按 memberId 点名才拿得到）。
⇒ **不要**用「`get_overloads` 回了 N 条」去断言「只有 N 个重载」；要断言就按 memberId 逐个点名。
同理，`get_members_of_type` 的 `hasMore:false` 也**不等于**「成员齐了」——泛型与非泛型类型共用同一个 `fullName` 时它会解析到其中一个并只返回那一支（见上一节）。

```

- [ ] **步骤 3：验证落位**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path 'README.md' -Pattern '编译错误码速查'
Select-String -Path 'README.md' -Pattern 'InBounds\(Map map\)'
Select-String -Path 'README.md' -Pattern 'RegionType\.Normal'
Select-String -Path 'README.md' -Pattern 'regionGrid'
Select-String -Path 'README.md' -Pattern 'Int32'
Select-String -Path 'README.md' -Pattern 'get_overloads'
```

预期：第 1 条命中 **1** 次（小节标题）；第 2-6 条各命中 **≥1** 次；第 7 条命中 **1** 次（核验方法警告）。
再确认小节落在 `## 设置` **之前**：

```powershell
$l = [System.IO.File]::ReadAllLines((Join-Path (Get-Location) 'README.md'))
$my = ($l | Select-String -Pattern '编译错误码速查').LineNumber
$set = ($l | Select-String -Pattern '^## 设置$').LineNumber
"my=$my set=$set ok=" + ($my -lt $set)
```

预期：`ok=True`。

- [ ] **步骤 4：确认没踩坏既有内容**

```powershell
Select-String -Path 'README.md' -Pattern 'inspect_type.*不返回任何成员数据|find_methods\(query, limit\?\).*hasOptionalParameters'
```

预期：命中 ≥1（计划 10 任务 3 的两处表格行**原样还在** —— 本任务只在它的小节旁边加内容，不改它的正文）。若 0 命中且计划 10 未落地，属正常（那两行还没写）；若计划 10 已落地而这里是 0 命中 ⇒ **有人改坏了，立刻停下报告**。

- [ ] **步骤 5：改动清单**

```
修改 README.md（新增 “#### 编译错误码速查：看到 CS#### 怎么办” 一节，插在 ## 设置 之前）
未改 README.md 其余任何一行；未改计划 10 任务 3 已落地的段落
未改任何代码文件；未编译、未启动游戏、未调用任何 MCP 工具
```

---

### 任务 4：`AI-DEPLOYMENT.md` 新增排障小节 + 排障决策树 2 行

**文件：**
- 修改：`AI-DEPLOYMENT.md` —— 在 `---`（现有 **160** 行）之前新增一节
- 修改：`AI-DEPLOYMENT.md` —— 排障决策树表格末尾（现有 `:177` 之后）新增 2 行

**与计划 10 任务 3 的编辑协调** 同任务 3 的表，锚点换成 `AI-DEPLOYMENT.md`：计划 10 任务 3 步骤 5 在 `:158` 之后、`:160` 的 `---` 之前插入 `#### 写码前查成员可见性（不必等游戏跑起来）`，步骤 6 在排障表 `:176` 之后插入 1 行。**本任务插在它那节之后、仍在同一个 `---` 之前**；本任务先落地时，计划 10 的锚点改用本任务的小节标题。

- [ ] **步骤 1：确认当前落点**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$p = Join-Path (Get-Location) 'AI-DEPLOYMENT.md'
[System.IO.File]::ReadAllLines($p).Count
Select-String -Path $p -Pattern '^## 8\. 排障决策树'
Select-String -Path $p -Pattern '^#### 写码前查成员可见性'
```

预期：总行数 **190**（计划 10 落地后更大，以现场为准）；`## 8. 排障决策树` 命中 1 次；`#### 写码前查成员可见性` 命中 0 或 1 次（0 = 计划 10 未落地）。

- [ ] **步骤 2：插入小节**

用 `edit`，把 `AI-DEPLOYMENT.md` 里 `## 8. 排障决策树（AI 遇错优先自查）` **上面紧邻的那个 `---`** 替换为**下面整段 + 原来那个 `---`**：

```markdown
#### `execute_csharp_code` 撞到编译错时（离线速查）

`execute_csharp_code` 的 C# 控制台**默认没有 using 引用**，撞到 `CS####` 是常态。响应里的 `guidance` 已按错误码分流；下面是离线速查版，完整表（含每条的核验方式）见 `README.md` 的「编译错误码速查」。

| 看到 | 真实原因 | 怎么办 |
|---|---|---|
| `CS0103` | 裸类型名（`GenTicks`）解析不到 | 写全限定名 `Verse.GenTicks.TicksGame`，或先 `add_using_directive` |
| `CS0234` | 命名空间/类型名不存在 | `Verse.Pawn`（**没有** `Verse.RimWorld`）；`RimWorld.ThingDefOf.Wall`（**没有** `Verse.ThingDefOf`） |
| `CS0121` | 两个候选签名**逐字相同** = 同一程序集被引用两次，**不是**重载歧义 | 放弃 using，全限定静态直调：`Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal)`（`public static`，**不用反射**） |
| `CS0117` | 类型上没有这个枚举值/静态成员 | 「普通地面 Region」的值是 **`RegionType.Normal`**（没有 `Region`） |
| `CS1061` | 类型上没有这个成员 | `Map.regionManager` **不存在**（真字段是 `public` 的 `regionGrid`）；`Map.Area` 是 **`Int32`**（没有小写 `area`） |
| `CS0411` | 泛型推不出实参 | `Verse.GridsUtility.GetFirstThing<Verse.Thing>(q, map)` |
| `CS1503` | 实参类型对不上 | `CellRect.InBounds` 收的是 **`Map`**（`Boolean InBounds(Map map)`），不是 `IntVec3`；查点用 `Contains(IntVec3 c)`。`GenGrid.Standable(IntVec3 c, Map map)` 是静态方法 |
| `CS0841` | 通常是**类型解析失败**的伪装 | 修上一行的类型名（`Verse.RimWorld.Pawn` → `Verse.Pawn`），本条自消 |
| `CS0127` | 宿主 `InteractiveHost` 返回 **`void`** | 不要写 `return <表达式>`（只有 `return;` 合法）；要回传值写成**末尾裸表达式** |
| `Exception invoking REPL: …` | 不是编译失败，是代码抛的异常（被误标成 `COMPILATION_ERROR`） | 按真实异常类型排查 |

⚠️ 判定「某成员存不存在」时，**不要**只用 `get_overloads` 的返回条数（它按方法名返回，会漏同名不同参的分支），也**不要**把 `get_members_of_type` 的 `hasMore:false` 当成「成员齐了」。两条的细节见上一节与 `README.md`。

```

- [ ] **步骤 3：排障决策树新增 2 行**

用 `edit`，把 `AI-DEPLOYMENT.md` 排障表最后一行

```markdown
| 改 `toolConfig.json` 不生效 | 需重启 | 重启 MCP 或重连会话 |
```

替换为它**加上**下面两行：

```markdown
| `execute_csharp_code` 撞 `CS####`，不知道该改哪 | 控制台默认无 using，撞错是常态 | 看响应的 `guidance`（已按错误码分流）；离线查表见本节末与 `README.md` |
| 按 `CS####` 查成员却查不到 | `get_overloads` 按方法名返回会漏同名分支；`get_members_of_type` 的 `hasMore:false` 不代表成员齐 | 按 memberId 逐个点名 `get_member_signature`；详见本节末 |
```

- [ ] **步骤 4：验证落位**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path 'AI-DEPLOYMENT.md' -Pattern 'execute_csharp_code` 撞到编译错时'
Select-String -Path 'AI-DEPLOYMENT.md' -Pattern 'InBounds` 收的是'
Select-String -Path 'AI-DEPLOYMENT.md' -Pattern '撞 `CS####`，不知道该改哪'
Select-String -Path 'AI-DEPLOYMENT.md' -Pattern '按 `CS####` 查成员却查不到'
$l = [System.IO.File]::ReadAllLines((Join-Path (Get-Location) 'AI-DEPLOYMENT.md'))
$mine = ($l | Select-String -Pattern 'execute_csharp_code` 撞到编译错时').LineNumber
$tree = ($l | Select-String -Pattern '^## 8\. 排障决策树').LineNumber
"mine=$mine tree=$tree ok=" + ($mine -lt $tree)
```

预期：4 条各命中 ≥1 次，最后一行输出 `ok=True`。

- [ ] **步骤 5：确认没踩坏既有内容**

```powershell
Select-String -Path 'AI-DEPLOYMENT.md' -Pattern '^\| 改 `toolConfig\.json` 不生效'
```

预期：命中 **1**（那一行必须还在，只是后面多了 2 行）。

- [ ] **步骤 6：改动清单**

```
修改 AI-DEPLOYMENT.md（新增 “#### execute_csharp_code 撞到编译错时（离线速查）” 一节，插在 ## 8 之前的 --- 之前）
修改 AI-DEPLOYMENT.md（排障决策树表格末尾新增 2 行）
未改 AI-DEPLOYMENT.md 其余任何一行；未改计划 10 任务 3 已落地的段落
未改任何代码文件；未编译、未启动游戏、未调用任何 MCP 工具
```

---

### 任务 5：收尾验收 + 三方交接登记表回写本计划

**文件：**
- 修改：**本计划文件**（`docs/issue-triage/plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md`）—— 步骤 3 回写交接表
- 验证：只读，不改任何代码或文档

- [ ] **步骤 1：全量测试 + 语法**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
node --check index.js
```

预期：`npm test` **全 `pass`、0 fail**（新文件 7 条 + 既有 9 个测试文件全部）；`node --check` 无输出、退出码 `0`。

- [ ] **步骤 2：三条禁改锚点自查（必须全部 0 命中）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$l = [System.IO.File]::ReadAllLines((Join-Path (Get-Location) 'MCP\index.js'))
":2937 => " + $l[2936].Substring(0, [Math]::Min(40, $l[2936].Length))
":2072 => " + $l[2071].Substring(0, [Math]::Min(40, $l[2071].Length))
":2078 => " + $l[2077].Substring(0, [Math]::Min(40, $l[2077].Length))
```

预期（**逐字核对，不一致就是被改坏了**）：

| 锚点 | 必须仍以这句开头 |
|---|---|
| `MCP/index.js:2937` | `execute_csharp_code: { example: '{ "code": "Verse.GenTicks.TicksGame" }'` |
| `MCP/index.js:2072` | `[UE工具][需要进入地图] Execute C# code in UnityExplorer C# Console.` |
| `MCP/index.js:2078` | `C# code to execute. Examples:` |

⚠️ 若计划 02 任务 4 / 计划 12 任务 1 已落地，这三行**会**变 —— 那不是本任务改的。**对照的是「是否为本任务所改」**：本任务的改动清单里不出现这三行即为正确；真要确认，用改动清单逐行核对，别靠行号硬比。
⚠️ **行号漂移提示**：任务 1 在 `MCP/index.js:4004` 之前插入了约 90 行 ⇒ **所有 >4004 的行号整体下移约 90 行**（含本计划反复引用的 `:4535-4538`）。
上面这三条禁改锚点 `:2072` / `:2078` / `:2937` **都在 4004 之前，不受影响**，所以本步骤可以按原行号取；`:4535-4538` 则**必须**改用内容定位（`Select-String -Pattern 'compileErrorGuidance\(csErr\)'` 命中处），不要用行号。

- [ ] **步骤 3：把实际落地情况回写本计划的交接表**

用 `edit` 把下方 `## 5. 三方交接登记表` 里「本计划」那一行的「落地状态」列，从 `待执行` 改成实际结果（`已落地` / `未落地（原因：…）`），并在「执行者备注」列写清三件事：① 任务 2 步骤 1 判定的是形态 A 还是形态 B；② 计划 02 任务 7 当时落没落地；③ 计划 10 任务 3 当时落没落地、本任务用的是哪个锚点。
**这一步的产物就是这张表** —— 它是下一个执行者判断「哪些已被吸收、哪些还欠着」的唯一依据，**不能省**。

- [ ] **步骤 4：残留项登记（不在本轮做，写清楚为什么）**

用 `edit` 在 `## 6. 本轮明确不做的事` 的表格里补一行你实际遇到的残留项；若一条都没有，补一行「本轮无额外残留项」。

- [ ] **步骤 5：改动清单**

```
修改 docs/issue-triage/plans/2026-10-02-33-35-…md（回写交接表状态与残留项）
本任务不修改任何代码或文档
```

---

## 4. 自检记录

| 检查项 | 结果 |
|---|---|
| **规格覆盖度** | 吐槽板 [33] 的三个子现象 → 任务 1 的 `CS0121` / `CS0117` / `CS1061` 三支文案（`regionGrid`、`RegionType.Normal`、全限定直调）+ 任务 3/4 的对照表行；[33] 的「反射绕过多余」→ 任务 1 `CS0121` 文案 + §2.4 的 N2；[34] 的 `void` 宿主 → 任务 2 的 `CS0127` 独立分支（引用计划 02 任务 7，不重复实现）+ 任务 1 的 `REPL_EXCEPTION_GUIDANCE`（[34] 缺口 B）；[34] 的双份回显 → **计划 02 §0.9 已裁定不是缺口**，本计划不重复裁决（§6 登记）；[35] 的 5 行对照表 → 任务 3 的表格逐行落位；[35] 的 ④ 建议「随错自动附 decompile 全表」→ **架构阻塞**，登记为不做（§6） |
| **占位符扫描** | 全文无「待定 / TODO / 后续实现 / 补充细节 / 添加适当的错误处理 / 为上述代码编写测试 / 类似任务 N」；每个代码步骤都有**可直接粘贴的完整代码块**，每条命令都有**明确预期输出**；Commit 步骤已按「本仓库不是 git 仓库」全部换成**改动清单** |
| **类型/命名一致性** | `firstCsCode` / `COMPILE_GUIDANCE` / `COMPILE_GUIDANCE_FALLBACK` / `REPL_EXCEPTION_GUIDANCE` / `MULTI_CODE_NOTE` / `compileErrorGuidance` 在任务 1 的实现、任务 1 的测试、任务 2 的调用点、任务 5 的自查里**同名同义**；`_testHooks.compileErrorGuidance` 的导出名与测试里的 `mod._testHooks.compileErrorGuidance` 一致；测试文件变量 `guidance` 在 7 条用例里一致 |
| **错误码计数一致** | 全文一律写 **9** 个（§2.5 的 E2 已记录裁决报告写 8 是笔误）；`COMPILE_GUIDANCE` 里 **8** 个键 + `CS0127` 走 switch 内的独立分支 = 9；测试断言的 `codes` 数组是同样 8 个键，与 `CS0127` 的独立断言配对 |
| **行号复核** | 本文所有 `MCP/index.js` / `README.md` / `AI-DEPLOYMENT.md` / 计划 / 吐槽板行号，均于 2026-10-02 用 `[System.IO.File]::ReadAllLines(...)` 打印复核（见「开工前必读」的复核表）。**程序集事实**全部来自 decompilerserver 结构化只读查询，命令原文已逐条写进 §0 的 F1-F16 |
| **凡「不存在」必附检索范围** | §0 的检索范围段 + F1-F16 每行都点名了上下文 `rw16`、完整路径与 MVID，并写明「未检索任何模组程序集」；`regionManager`（F6）、`Verse.Map.GetRegion`（F7）、`Verse.Map.InBounds`（F8）、`Verse.RimWorld`（F14）、`Verse.ThingDefOf`（F13）、`RegionGrid.RegionAt`（F16）全部带范围 |
| **未使用 `search_string_literals`** | §0 明写「所有查询均为结构化只读查询，未使用 `search_string_literals`」——该工具按字面量匹配，带 `\|` 的结论无效，本计划不使用它 |
| **编辑纪律** | 全文所有「插入 / 替换」步骤都写明用 `edit` 并给出**逐字 `old_string`**；任务 1 步骤 4、5 后各有一条「改完立刻核对行数」的命令；无任何一条 PowerShell 写文件命令（`WriteAllLines` / `Set-Content` / `Out-File` 一次都没出现） |
| **零冲突自查** | `:2937` 零触碰（任务 2 步骤 7 改动清单明写）；`:2072`/`:2078` 零触碰；`:4539` 之后的 `return r;` 零触碰；`README.md` / `AI-DEPLOYMENT.md` 只**新增**小节与表格行，任务 3 步骤 4 / 任务 4 步骤 5 各有一条「确认既有内容没被踩坏」的验证 |

---

## 5. 三方交接登记表

> **这张表是本计划与计划 02 / 10 / 12 的唯一协调面。** 任何一方落地后都要回来更新自己那一行的「落地状态」。

| 计划 | 它认领的地盘 | 状态 | 与本计划的关系 | 执行者备注 |
|---|---|---|---|---|
| **计划 02**（`2026-10-01-02-execute-csharp-code-output-capture.md`） | `MCP/index.js:2937`（任务 4）、`:2073-2082`、`:4539`、`:4536-4537` 的 **`CS0127` 分支**（任务 7，在 `:1423-1523`） | **任务 7 与本计划任务 2 部分重叠**：它已把 `CS0127` 独立分支写好，但**其余 8 支与 REPL 异常分支未做** | 本计划任务 2 **不重复实现** `CS0127` 那一支，直接给出含它的最终形态；并**修掉计划 02 任务 7 步骤 1 用例 2 与步骤 3 文案互相矛盾**的问题（§2.2） | |
| **计划 10**（`2026-10-01-10-api-member-visibility-tools.md`） | `README.md` / `AI-DEPLOYMENT.md` / `McpRimDebug/README.md` 的「成员可见性该去哪问」小节（任务 3，在 `:670-815`） | 待执行 | **同文件、不同段落**。本计划的小节挂在它那节**之下**；本计划先落地时它改用本计划的标题作锚点（任务 3、任务 4 各有一张先后表） | |
| **计划 12**（`2026-10-01-12-mod-presence-assembly-visibility.md`） | `MCP/index.js:2072` / `:2078`（任务 1） | 待执行 | **零交集**。本计划一个字都不碰那两行 | |

**三方零冲突的核心理由**（写在这里，免得下一个人重新论证）：三条吐槽的全部改善都经由**同一处返回值** `MCP/index.js:4536-4537` 的 `r.guidance` 返回给调用方；文档侧的「正确答案表」都落在计划 10 任务 3 正在改的那两节之下；剩下三处已被别人认领的地盘（`:2937`、`:2072`/`:2078`、`:4539` 之后）本计划**一行都不碰**。⇒ 拆成三份计划必然在同一批文件上打架，合成一份是唯一不打架的形状。

---

## 6. 本轮明确不做的事

| 不做的事 | 为什么 | 前置条件 / 归属 |
|---|---|---|
| **修 CS0121 的根因**（若确为「程序集被引用两次」） | [33] 报告只给出了「唯一说得通的**假设**」与静态可确证的机制缺口，**未实测**；真正的修改面在 `Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ScriptEvaluator.cs` 的 `ImportAppdomainAssemblies`，且与 Roslyn 计划任务 8 抢同一片 ConsoleController 区域 | 需先实跑 `AppDomain.CurrentDomain.GetAssemblies()` 确认（**本流水线禁止 `execute_csharp_code`**）；建议单独排期 |
| **「随编译错自动附 decompile 查到的同名成员全表」**（板上 [35] 的 ④ 建议原话） | ⛔ **架构阻塞**：`get_members_of_type` / `search_members` 属于**另一个 MCP 服务器**（decompilerserver）。在 `MCP/` 全目录（`*.js`）检索 `get_members_of_type`、`search_members`、`get_overloads`、`membersOfType`、`listMembers`、`resolveMemberId` —— **0 命中**。要实现得先给 `MCP/index.js` 引入反编译能力（Cecil 或一个到 decompilerserver 的客户端） | 远大于本条，属新工程；且必须**先修 [29]**（`get_members_of_type` 会静默漏成员却报 `hasMore:false`）——拿一个会假阴性的源去自动回填，等于用新假阴性换旧假阴性 |
| **让 `add_using_directive` 不再静默假成功** | [33] 报告 §3.1 挖出的独立缺口（`ConsoleController.AddUsing` 无论编译成败都 `usingDirectives.Add`；`Evaluate` 就地吞掉 `FormatException`；`MCP/index.js:4555-4564` 原样透传）⇒ 传一个不存在的命名空间也回 `success=true` | 要改 UE 侧 C# 并重编 `UnityExplorer.STANDALONE.Mono.dll`；**本计划是纯 JS + 纯文档，不碰**。建议单独排期 |
| **实跑复核任何一条编译错误码** | 铁律 §1.2/§1.3 禁止调用 `execute_csharp_code` | 本计划所有程序集事实都是**结构化只读查询**得出的，与错误码报文本身无关 |
| **判定计划 02 §0.9「双份回显不是新缺口」** | [34] 报告 §2.2 独立复核后**维持**该裁定 | 已被计划 02 §0.8/§0.9 + 任务 4/任务 5 闭合 |
| **在 `:2937` 追加「成员/签名去哪查」** | `:2937` 是计划 02 任务 4 的领地 | 若确有必要，按计划 12 的既有做法以**交接句**形式交给计划 02 的执行者并入 |

---






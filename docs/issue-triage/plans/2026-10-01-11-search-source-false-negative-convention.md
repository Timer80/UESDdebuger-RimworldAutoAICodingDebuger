# `search_source` 零命中判读规范 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 把「用 `search_source` 判断某个 vanilla 成员存不存在」这件事，从「靠猜 query / 猜路径、零命中就误判不存在」改成一条**有实测支撑、可复现、零命中时禁止下结论**的项目规范，落进 AI 代理必然会读到的文档。

**架构：** 一份规范正文放在**新建的** `docs/mcp-source-lookup-conventions.md`（该 MCP 服务的唯一规范文档），在根目录 `AI-DEPLOYMENT.md` 的排障小节里挂一个短入口把 AI 引过去。**不改任何代码** —— `search_source` 的源码在外部 MCP 服务里，不在本仓库；本计划的全部产出是文档 + 一条可复现的检索姿势。**覆盖吐槽板 `[11]` 与同族条目 `[16]` / `[19]` / `[21]` / `[27]` / `[29]`（均已归并）。**

**技术栈：** Markdown。验证手段是**只读** MCP 调用（`rimworld-source-online` 的 `search_source` / `read_file` / `list_directory` / `read_csharp_symbol` / `search_defs`，以及 `decompilerserver` 的 `search_types` / `get_members_of_type` / `get_overloads` / `search_members` / `get_member_signature`）+ PowerShell 文本核查。**不编译、不启动游戏、不调用任何会改变游戏状态的工具。**

---

## 0. 给零上下文执行者的背景（不要跳过）

### 0.1 硬约束一：本计划**改不动** `search_source`，也不要试图改

提供 `search_source` / `read_file` / `search_defs` / `list_directory` 的是**外部 MCP 服务
`rimworld-source-online`**，它的源码**不在本仓库**、也不在本机。已复核：

- 全仓库 `MCP/` 目录 grep `search_source|start_line|rimworld-source-online` → **零命中**
  （本仓库自己的 MCP 服务器是 `MCP/index.js`，走 rimworld-debug 链路，不提供这些工具）。
- 全仓库 grep `start_line` → 命中**只出现在 markdown 文档里**，`.cs` / `.js` / `.json` 中**零命中**。
- 本机只找到 trae-cn 留下的会话元数据存根，其内容仅
  `{"server_name":"mcp_rimworld-source_online","description":null}`，无源码。

> ⚠️ **分清「服务」与「程序集」**：本计划里另外提到的 `decompilerserver` 是**另一个**外部服务
> （离线反编译路线），同样不在本仓库。**而游戏程序集（RBS / RIMAPI / `Mono.Cecil` 等 DLL）是可反编译的** ——
> 拿「反编译 DLL」的能力去套这两个服务是无效的，那是形态不同，不是难度问题。

⇒ **凡是形如「修改 `search_source` 的返回契约 / 加 `limit` 参数 / 加声明行折叠」的任务，
都不在本仓库内，不要写进计划。** 本计划的唯一手段是**改变调用方怎么写 query**。

### 0.2 硬约束二：本计划**只覆盖 `search_source`**；成员可见性不归这里

本条与吐槽板 [10]（索引表 `[10]` 行：「无法查询当前 vanilla / 模组 API 的实际成员可见性」）
是**同一能力缺口的两面**：

| | 归属 | 落点 |
|---|---|---|
| **怎么搜、搜不到时怎么下结论、怎么不烧上下文** | **本计划**（[11]） | `docs/mcp-source-lookup-conventions.md` + `AI-DEPLOYMENT.md` §8.1 |
| **成员可见性查询能力本身** | **不归本计划**，由 [10] 的计划实现 | 本仓库 `McpRimDebug/DebugSession.Eval.cs`（经 Cecil 元数据输出 `isPublic` 等字段），计划见 `docs/issue-triage/plans/2026-10-01-10-api-member-visibility-tools.md` |

**执行本计划时不要重复实现可见性查询、不要新建任何查可见性的工具。**
（若上面那份 [10] 计划文件执行时尚未落盘，忽略这条链接即可 —— 本计划不依赖它才能执行；
边界以上面的表格为准。）

> 为什么边界成立、而不是「随手就能覆盖」：`search_source` 是**行级文本匹配**，**原理上没有符号表**，
> 且索引里**只有 vanilla**（§0.4 实测）。它能把声明行上的修饰符**原文**带出来，
> 但那是**源码字面量**，不是**可访问性判定** —— 它不解析 `internal` + `InternalsVisibleTo`、
> 不做泛型约束与重载决议、也完全看不到第三方模组的程序集。规范里会写清这条线。

### 0.3 执行者身份约束（读这条再决定能不能开工）

本计划要**新建一份文档 + 修改 `AI-DEPLOYMENT.md`**。因此：

- ✅ 可以执行的角色：主代理、维护者、或任何**不受** `docs/issue-triage/AGENT-BRIEF.md` §1.1
  约束的代理/人。
- ❌ **不能**由 issue-triage 的 PLAN 子代理执行 —— 那类子代理受铁律约束「禁止修改任何已存在的文件」，
  改 `AI-DEPLOYMENT.md` 会直接违规。
- 开工时机：本仓库同时有一条并行 MCP 调试会话在跑（`AGENT-BRIEF.md` 开头）。**文档写入不影响游戏状态，
  但提交（commit）会动 git 工作树**，所以 commit 步骤要在没有并行会话时做。

### 0.4 索引范围（决定「零命中」还有第三种含义）

实测 `list_directory{ path:"" }` →

```
Defs/
Source/
```

⇒ 该服务索引里**只有 vanilla RimWorld 自带源码与 Def XML**。
**任何第三方模组的成员，用 `search_source` 永远搜不到** —— 这是零命中的第三种语义，
必须写进规范，否则规范本身就是新的假阴性来源。

### 0.5 行号引用的快照警告（重要）

`docs/mcp-吐槽板.md` 在本计划编写期间**正被主代理追加批注**，行号持续漂移
（实测：条目 `[11]` 的标题行在同一次会话内从 215 → 242，文件总行数 504 → 537）。

⇒ **本计划里凡引用吐槽板，一律以「`## [NN]` 标题文本」为锚点，行号只作当时的参考值。**
执行者要重新定位时按标题文本找，不要按行号跳。

### 0.6 本条真正该修的是什么（裁决已定，别跑偏）

- **假阴性（🔴，本计划重点）**：一个**确实存在且 `public`** 的成员，用带返回类型的精确 query 搜
  会返回 `No results found`。调用方极易把它读成「这个成员不存在/不可见」，并把这个错误认知
  **写进源码/注释/计划**。这是「得出事实相反的结论」。
- **假阳性（🔴，同样致命，方向相反）**：非零命中**看起来像印证了你的猜测**，人会直接采信。
  机制是默认大小写不敏感（见 §0.8）与路径写法（见 §0.9）。
- **上下文税（🟡，已被上游约束住）**：400 条硬上限 + 显式 `[TRUNCATED]` + `Tip` 都在，
  是「吵但有界」，不是失控。本计划只写调用方义务，不去改上游。

### 0.7 同族条目 `[16]` 已并入本计划（裁决：值得修 + 归并，不另立计划）

吐槽板 `[16]` 说「`search_source` / `read_csharp_symbol` 命中面不一致」。
其**表层诊断已被判断员证伪**，真机制是：

> **`file_pattern` 必须带 `Source/` 前缀，否则静默零命中。**

这与 `[11]` 修的是**同一件事的两半**：`[11]` 是「别把你猜的签名写进 `query`」，
`[16]` 是「别把少一级前缀的路径写进 `file_pattern`」——**两者产出同一种后果：
一个明明存在的东西返回 `No results found`**。本计划已把它并入规范正文的
**规矩 0** 与 **规矩 3 的第 0 步**，执行者不需要、也不应该另建文档。

`[16]` 的 🔴 比 `[11]` 的 🔴 半更重：`[11]` 要你**自己写错 query** 才中招；
`[16]` 只要**照抄工具自己打印的路径**就中招。

**判断员转述的两条细节，本计划作者已逐条复测，其中两条与转述不一致 —— 以计划里的实测为准：**

| 转述 | 本次复核实测 | 结论 |
|---|---|---|
| `read_csharp_symbol` 返回头打印 `// File: Verse/Thing.cs` | `read_csharp_symbol{ typeName: "StealAIDebugDrawer" }` → `// File: RimWorld/StealAIDebugDrawer.cs (Lines 7-135)`；`{ typeName: "Thing", memberName: "Notify_ThingChanged" }` → `Method 'Notify_ThingChanged' in type 'Thing' not found in index.` | **那个具体文件名没复现**。但机制成立、而且更可证：返回头打印的**正是不带 `Source/` 前缀的路径**（`RimWorld/StealAIDebugDrawer.cs`），把它原样喂给 `file_pattern` 确实零命中 |
| 字面量 `pather?.StopDead()` 不转义会**静默零命中** | 未转义 → **28 条命中**（绝大多数是 `pather.StopDead();` 这类**不含 `?`** 的行）；转义成 `pather\?\.StopDead\(\)` → **12 条命中**，全部是真空条件调用点 | **方向说反了**：未转义不是零命中，而是**匹配放大（假阳性）**。危害同级（同样得出事实相反的结论），但不是同一种病，规范里按「假阳性」写。顺带实测：`(` 不配对是**响亮**的 `rg` 报错（`unclosed group`），不是静默失败 |

### 0.8 同族条目 `[19]` 已并入本计划：真病因是**假阳性**，不是假阴性

吐槽板 `[19]` 写的是「成员存在但 private ⇒ 零命中 ⇒ 无法判别」。
**表层诊断已被复现推翻**（实测，同一 query、同一个文件、只差 `case_sensitive`）：

```
search_source{ query: "public LocalTargetInfo destination",
               file_pattern: "Source/Verse/AI/Pawn_PathFollower.cs" }      ← 默认参数
→ ./Source/Verse/AI/Pawn_PathFollower.cs
  81:	public LocalTargetInfo Destination => destination;      ← 命中了「另一个」成员

同一调用 + case_sensitive: true
→ No results found. Try adjusting your search query or file pattern.
```

⇒ **真病因是 `search_source` 默认大小写不敏感**（`case_sensitive` 默认 `false`）：
`destination` 同时匹配 `:33` 的 **`private`** 字段和 `:81` 的 **`public`** 属性。
**这比假阴性更危险**：假阴性至少让人起疑；非零命中看起来像「印证了我的猜测」，人会直接采信。

**对本计划的三处实际影响（都已改）：**

1. 计划原来 4 处把 `case_sensitive` 当机械参数写，**没有一处说明它默认是 `false`** ——
   已补在规范正文的规矩 2（`case_sensitive` 警告块）与入口文案里。
2. **规矩 3 第 1 步的「裸名宽搜」原本没要求 `case_sensitive`** ⇒ **这条排除法自己执行就会串味** ——
   已补上要求，并写明实测：裸搜 `destination` 返回 **33 条**，private 与 public 混在一起。
3. 「零命中四态清单」漏了非零命中侧 ⇒ 已拆成 **（A）零命中四态 +（B）第五态**两段。

**另一条加强本计划结论的实测**：「成员存在但 private」其实**可判** ——
声明行模板 + `regex:true` + `case_sensitive:true` 搜 `destination`，带 `file_pattern` 收窄后
返回 `33:	private LocalTargetInfo destination;`（全仓则 6 个文件块）。
**一次调用同时回答「在不在」和「什么可见性」**，见规范正文规矩 2。

**关于 `[19]` 引用的 `COMMON.md`（更正后的事实）**：
`COMMON.md` **存在**，673 行 / 42 KB，标题《共通规约 · 所有实现/审查/登记子代理必读》，
路径在 **TEXT20 项目**下
（`Mods\TEXT20\.superpowers\sdd\2026-10-01-原版寻路接管-缺口修复-总计划\COMMON.md`），
**不在 UESDdebuger 仓库内**。吐槽板是两个项目**共用**的文件，`COMMON.md §10` 明确写着本板位置
就是 `UESDdebuger\docs\mcp-吐槽板.md`。

**⚠️ 曾经的错判**：把检索范围限定在 UESDdebuger 仓库内，没找到就写成了「不存在」。
**「这个范围内没找到」≠「不存在」** —— 这恰恰是本文件第 1 节反复警告的那种错误推理。

**`COMMON.md` 里确实有相关纪律**，§5.2 写着「② 正控必须选另一个字符串字面量」
与「正控必须是『方法失效时就会失败』的那个，否则它不是正控」，
并记着一次实战：用 `#Strings` 堆的名字当 `#US` 堆检索的正控，**恒成立地骗过了自己**。

**⇒ 但它只覆盖 `[19]` 的一半，两者的检索通道不同，不要混为一谈：**

| | 覆盖什么 | 对 `[19]` |
|---|---|---|
| `COMMON.md` §5.2 | 在**产物 DLL** 里做**字节级检索**时的正控纪律（`#US` UTF-16 vs `#Strings` UTF-8、奇数偏移、格式串） | ❌ **不是** `search_source`。DLL 字节检索的正控，**管不到** `search_source` 的行级匹配 |
| `COMMON.md` §5.1 / §5 `:413` | 「`Player.log` 零命中的头号假阴性」「报告零命中必须附上检索模式原文」 | ✅ **同类认知纪律**，方向一致 |
| **本文件（规范正文）** | `search_source` 的**具体机制**：`case_sensitive` 默认 `false`、`file_pattern` 接 glob、缺首级/缺中间级都静默零命中 | ← `COMMON.md` **没有**这部分 |

⇒ **`[19]` 的「已绕过」是「部分有依据」**：认知纪律侧站得住，**机制侧没有**。
所以本计划写的 `case_sensitive` 那一段**不是重复劳动**，它补的正是 `COMMON.md` 没写的那一半。
（板子上「已绕过」的措辞建议按这个「部分」重新界定，别当成完全被兜住。）

### 0.9 同族条目 `[21]` 已并入本计划：同一根因的**第二种**写错姿势

吐槽板 `[21]` 的现场：真实路径是 `Source/Verse/AI/Pawn_JobTracker.cs`，
`file_pattern` 写成了 `Source/Verse/Pawn_JobTracker.cs` —— **首级 `Source/` 是对的，
错的是中间少了一级 `AI/`**，同样静默零命中。

⇒ **`[16]` 与 `[21]` 不是两条规则，是同一根因的两种姿势**：
`file_pattern` 要**精确到最终目录**，而「精确」这件事**不能靠手拼**。

**本轮实测到的三条新事实（都已写进规范正文）：**

1. **`file_pattern` 是锚在索引根的 glob，不是精确路径。** 实测
   `"Pawn_JobTracker.cs"`（裸 basename）、`"**/Pawn_JobTracker.cs"`、`"*JobTracker*"`
   与精确路径 `"Source/Verse/AI/Pawn_JobTracker.cs"` **全部命中**。
   ⇒ 板上建议 2「0 命中时自动放宽到 basename 重试」**已是现成功能、零改动**。
2. **一个零拼写错误的目录前缀也返回 0**：`file_pattern:"Source/Verse/"` → `No results found.`
   ⇒ **只给目录前缀不成立，必须精确到「文件」这一级。** 这比板上自述的场景更宽。
3. **`query:"."` 是通用诊断探针**：它匹配每一行非空行、与要找的内容无关，
   所以它的返回只反映「这个 pattern 选中了多少文件」。
   路径写错 → `No results found.`；换成 basename → `[TRUNCATED] Showing 400/1077 results.`
   ⇒ **一次调用把「路径写错」与「内容没有」彻底分开。** 板上建议 1「返回该 pattern 下有 N 个文件」
   可用这个探针在调用侧等价实现，**不需要上游改**。

**对本计划的实际修正（已改）**：原规矩 3 第 0 步写的是「多半缺 `Source/` 前缀 … 把正确前缀补回去即可」——
对 `[21]` 是 **no-op 且反向误导**：照做会**改的地方和错的地方不是一处**，人以为修好了其实没修。
已改为「先诊断（探针 / 去掉 `file_pattern` 重搜）→ 再决定怎么写」，并明确列出三种错法。

### 0.10 同族条目 `[27]` / `[29]` 已并入本计划：声明行模板对**方法**失效 + 重名类型

**`[27]` —— 本计划最严重的一个缺陷，已修**：

规范正文原来的声明行模板末尾是 `(=>|;|=)`，**只覆盖属性与字段，对方法一律 0 命中**
（方法声明以 `)` 收尾）。而本计划最早的实测正例 `AllPawnsSpawned`（属性）与
`destination`（字段）**恰好都在前两类** ⇒ **这个模板从未被方法验证过**，
「看起来一直能用」掩盖了它对某一整类成员的完全失效。修法与实测见规范正文规矩 2。
**这条同时给了本计划一条通用纪律：一个「已被正例验证过」的模板，不等于对所有成员形态都验证过。**

**`[29]` —— 另一个外部 MCP（`decompilerserver`）的重名类型陷阱**：
`search_types("ThingOwner")` 返回**两个 `fullName` 完全相同**的类型
（泛型 `…:020008E2:T` 与非泛型 `…:020008EC:T`），
`get_members_of_type` **只枚举其中一个**，另一整块成员**根本不出现在返回里**。
三条实情（`typeId` 要 canonical、`get_overrides` 不含被查的那条、类型名报错常是命名空间被当类型名）
与实测数字见规范正文**规矩 4B**。

## 1. 逐条复核裁决报告后的修正（**与上游报告不一致之处，以本节为准**）

上游裁决报告：`docs/issue-triage/triage/11-search-source-result-volume.md`（裁决：值得修）。

1. **「输出按文件路径字母序」不成立。** 报告 §1.2 写「排序规则看起来是按文件路径字母序」。
   本次复核实测，同一次调用里文件块顺序是
   `Source/RimWorld/LoadTransportersJobUtility.cs` → `Source/RimWorld/Alert_Digesting.cs` →
   `Source/Verse/AI/JailbreakerMentalStateUtility.cs` → `Source/RimWorld/GatherItemsForCaravanUtility.cs` → …，
   **`RimWorld/` 与 `Verse/` 交错**，不是字母序。
2. **「目标声明行出现在第 5 个文件块」不可复现。** 本次复核实测，同一个 query
   `AllPawnsSpawned` + `file_pattern:"*.cs"` 的返回里，`./Source/Verse/MapPawns.cs` 排在
   **第 19 个**文件块。两次观测不一致 ⇒ **输出顺序既不是字母序、也不保证跨调用稳定**。
   结论不变但更强了：**任何依赖顺序去「翻找声明行」的做法都不成立**。
3. **报告 §5 写「`docs\issue-triage\plans\` 目录当前不存在」有误。** 该目录**存在**，
   只是当时为空（现已出现 `2026-10-01-06-debug-action-path-format.md`）。不影响结论。
4. **报告引用吐槽板的行号普遍漂移**（如把 `Showing 400/416` 记在 `:204`）。**结论本身逐条属实**，
   已由本次 6 次只读调用重新证实；只是行号不能直接抄，按 §0.5 用锚点定位。
5. **报告说「`search_source` 无符号表 ⇒ 原理上答不了可见性」，本次实测给出了一个更精确的结论**：
   `search_source` 支持 `regex: true`，可以只匹配**声明行**，从而把修饰符原文带出来。
   这**不推翻** §0.2 的边界（那是源码字面量，不是可访问性判定），
   但它让本条从「只能写禁令」升级成「**有可用的正确姿势**」—— 这是本次复核最大的增量。
6. **【本计划自身的缺陷，由 `[27]` 暴露】** 报告与我最初的模板都只被属性/字段验证过，
   **对方法完全失效**。见 §0.10 与规范正文规矩 2。
   **教训：正例只能证明「这一类对」，不能证明「所有类都对」。**

---

## 2. 落点决策：这份规范写进哪个文件才会真的被看到

| 候选 | 结论 | 理由 |
|---|---|---|
| 仓库根 `AGENTS.md`（新建） | ❌ | 仓库根**当前没有** `AGENTS.md`；本机 harness 自动加载的是用户级 `~/.dsh/AGENTS.md`，不保证读仓库根的同名文件。新建一个「写了但没人读」的文件是本条最典型的失败模式 —— 讽刺的是这正是本条在讲的「零命中 ≠ 结论」。 |
| 根 `README.md` | ❌ | 人类向 + 工坊发布页。使用这些工具的是 AI，不是读发布页的人。 |
| **`AI-DEPLOYMENT.md`（正文直接写进去）** | ⚠️ 部分 | 它是根目录**唯一**自称「面向 AI 代理」的文档（首行原文），§8 就是「排障决策树（AI 遇错优先自查）」，同类小节现成。**但**它通篇只讲本模组自己部署的那套工具链（UE / RIMAPI / GABP / DebugAction），`rimworld-source-online` 是**外部服务**、正文里一次都没出现过。把一个外部服务的完整规范塞进去会撑歪它的主题，也会随外部服务升级而整段腐坏。 |
| **`AI-DEPLOYMENT.md` 里只挂一个入口 + 正文另立一份规范文档** | ✅ **采纳** | 正文进**新建的** `docs/mcp-source-lookup-conventions.md`：它是「该 MCP 服务的唯一规范文档」，可以持续增补（[13] 的命名空间≠目录、[14] 的 `start_line` 0-indexed 都该进这里，见规范文档里的 `## 5. 维护规则`）；`AI-DEPLOYMENT.md` 只加一个短入口的 `### 8.1` 指路，保证 AI 在读部署文档时被告知「还有这套工具、规矩在那边」。 |
| `docs/issue-triage/` 下 | ❌ | 那是流水线自己的目录，`AGENT-BRIEF.md` §1.1 明令子代理不得改动其中既有文件；且那里放的是「问题记录」，不是「长期规范」。本计划自己就是从那儿产出的，不该把规范再塞回去。 |
| `docs/mcp-吐槽板.md` | ❌ | 铁律：吐槽板本体只有主代理能写批注。子代理/计划不得触碰。 |
| 正文要不要放到 `TEXT20` 项目下、让两项目共用？ | ⚠️ **本计划不擅自决定，标给维护者** | `COMMON.md §10` 把吐槽板定义为**两项目共用文件**，且明确位置就在 UESDdebuger 侧。但它**只把「吐槽板」定为共享**，**没有**把「所有规范文档」都定为共享。本规范的**服务对象**（`rimworld-source-online`）目前只被 UESDdebuger 侧使用 ⇒ 落 UESDdebuger `docs/` 成立。**若维护者希望两项目共用**，正确做法是**在 `COMMON.md` 里加一节指路**，而不是把规范正文搬去 TEXT20（正文留在产生它的一方、另一方指路，与吐槽板同构）。⇒ **执行本计划时不要自行搬文件；这一条留给维护者裁断。** |

**采纳方案的文件名**：`docs/mcp-source-lookup-conventions.md`
—— `docs/` 已有 `shader-guide-for-ai.md`、`csharp-console-compiler-upgrade-feasibility.md`
两个 kebab-case 英文名**规范文档**，本文件属于规范而非记录，故沿用同一命名风格。

---

## 3. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `docs/mcp-source-lookup-conventions.md` | **新建** | 外部 MCP `rimworld-source-online` 的**唯一规范文档**：归属与索引范围、**五条硬规矩**（规矩 0 路径锚定 / 规矩 1 query 写法 / 规矩 2 声明行正则 / 规矩 3 零命中四步排除法 / 规矩 4 同名与重载）、标准三段式检索流程、上下文预算、与 [10] 的边界、维护规则。每条结论必须带一次实测调用与其真实返回。 |
| `AI-DEPLOYMENT.md` | **修改一处**：在 `## 8. 排障决策树（AI 遇错优先自查）` 的 `**通用日志入口**：` 两条列表之后、`---` 之前，插入 `### 8.1 查 vanilla 源码：外部 MCP \`rimworld-source-online\`` | 唯一入口。只写「这套工具存在 + 最容易踩的几条 + 指向规范正文」。 |

**明确不动：** `MCP/index.js`、`McpRimDebug/**`、`Source/**`、任何 `.cs` / `.js` / `.json` / `.xml`、
`docs/mcp-吐槽板.md`、`COMMON.md`（在 TEXT20 项目下，**本计划不碰**）、`docs/issue-triage/` 下的任何既有文件
（**包括本计划自身**）。

## 任务 1：建立规范正文 `docs/mcp-source-lookup-conventions.md`

**文件：**
- 创建：`docs/mcp-source-lookup-conventions.md`
- 参考（只读，不改）：`docs/mcp-吐槽板.md` 的 `## [11]` / `## [13]` / `## [14]` / `## [16]` / `## [19]` / `## [21]` 条目、
  `docs/issue-triage/triage/11-search-source-result-volume.md`、
  `Mods\TEXT20\.superpowers\sdd\2026-10-01-原版寻路接管-缺口修复-总计划\COMMON.md`（只读，**不许改**）

- [ ] **步骤 1：确认目标文件尚不存在**

运行（PowerShell）：

```powershell
Test-Path 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\mcp-source-lookup-conventions.md'
```

预期：`False`。若为 `True`，**停下来**，说明有人已经建过这份文档 —— 改为阅读它并只补缺失章节，
不要整体覆盖。

- [ ] **步骤 2：分段落盘创建规范正文（⚠️ 不要一次性 write 整个文件）**

`COMMON.md §9`（两项目共用规约）明写「**绝不要一次性输出大文件**」，
并记着一次实测事故：代理读完 37 份计划后想一次性 `write`，**卡 65 分钟、盘上一个字没有**。
下面这段正文有数百行 —— **照抄它一次性写会踩同一个坑**。按 §9 的办法落盘：

1. **先写骨架**：只写文件头（标题 + 那句「面向 AI 代理」的引言）+ `## 0. 归属与边界` + `## 1. 五条硬规矩` 两个标题，各带一句占位说明 + 文件末尾的固定锚点：
   `<!-- APPEND-HERE -->`
2. **然后每次追加 ≤ 60 行**：把锚点替换成「新一节 + 锚点」。**永远只有一个锚点**，
   不会因为前面的内容变长而匹配失败。
3. 段与段之间**不回读全文**，只读你还需要的行。
4. 每落一段就确认文件真的变长了（目录列表 / `git status`），别闷头写。
5. ⚠️ **只允许用 `write` / `edit` 工具改文件。不要用一条 `pwsh` 去做行级重排或覆写** ——
   本计划作者自己就栽过：一条 `$new = @()` 拼错的 PowerShell 命令用**空数组覆写**了整个文件，
   脚本抛异常却没终止，1300 行计划当场归零（该仓库**不是 git 仓库**，无备份可回滚）。
   **行级重排请用 `edit` 工具的 old/new 替换。**

**要落盘的内容如下（按 §9 的方式分段追加，**不要**当成一次 write 的 payload**）：**

下面是 `docs/mcp-source-lookup-conventions.md` 的全文，**内容逐字照抄**（不要留空壳再补，也不要改写措辞）：

````markdown
# 外部静态检索 MCP（`rimworld-source-online`）使用规范

> **面向 AI 代理。** 查 vanilla RimWorld 源码时**先读本文件再调工具**。
> 本文件里的每条结论都带**一次可复现的只读调用**和它的**真实返回**，不接受只有描述的说法。

## 0. 归属与边界（先搞清楚，否则会用错工具、也会去改错地方）

| 事实 | 说明 |
|---|---|
| 宿主 | **外部 MCP 服务 `rimworld-source-online`**。它的源码**不在本仓库**，**改不动**。 |
| 索引范围 | 该服务索引根目录下**只有** `Source/` 与 `Defs/` 两项（实测 `list_directory{ path:"" }`）。**全部是 vanilla RimWorld 自带源码与 Def。第三方模组的源码不在索引里。** |
| 与本项目 MCP 的关系 | 本仓库 `MCP/index.js`（rimworld-debug 链路）**不提供** `search_source` / `read_file`（在 `MCP/` 下 grep 这两个名字 → 零命中）。不要去改 `MCP/index.js` 试图影响它。 |
| 本文件不负责什么 | **成员可见性的权威查询不归本文件**，见 §4。 |

## 1. 五条硬规矩（规矩 0 / 1 / 2 / 3 / 4）

### 规矩 0 —— `file_pattern` 必须锚定在 server 根（**这五条里最危险的一条**）

`file_pattern` 是**相对该 MCP 服务自己的索引根**的路径，**不是**相对仓库根，也不是相对 `Source/`。
索引根下只有 `Source/` 与 `Defs/`（见 §0），所以：

| `file_pattern` 写法 | 对不对 |
|---|---|
| `"Source/Verse/Thing.cs"`、`"Defs/..."`、`"*.cs"`、`"*"` | ✅ 锚在根，正确 |
| `"Verse/Thing.cs"`、`"Verse/AI/Pawn_PathFollower.cs"` | ❌ **缺 `Source/` 前缀 → 静默零命中** |

同一个 token、同一个文件，**只改路径写法**（实测）：

```
search_source{ query: "StealAIDebugDrawer", file_pattern: "Source/Verse/Thing.cs" }
→ ./Source/Verse/Thing.cs
  883:	StealAIDebugDrawer.Notify_ThingChanged(this);
  1010:	StealAIDebugDrawer.Notify_ThingChanged(this);
  1591:	StealAIDebugDrawer.Notify_ThingChanged(this);      ← 3 条

search_source{ query: "StealAIDebugDrawer", file_pattern: "Verse/Thing.cs" }
→ No results found. Try adjusting your search query or file pattern.   ← 0 条
```

```
search_source{ query: "StopDead", file_pattern: "Source/Verse/AI/Pawn_PathFollower.cs" }
→ 4 条，含 204:	public void StopDead()

search_source{ query: "StopDead", file_pattern: "Verse/AI/Pawn_PathFollower.cs" }
→ No results found. Try adjusting your search query or file pattern.
```

**为什么它比规矩 1 更危险**：规矩 1 要你**自己写错** query 才中招；
这条**只要照抄工具自己打印的路径就中招**。实测 `read_csharp_symbol` 的返回头打印的正是
**不带 `Source/` 前缀**的路径：

```
read_csharp_symbol{ typeName: "StealAIDebugDrawer" }
→ // File: RimWorld/StealAIDebugDrawer.cs (Lines 7-135)
```

把它原样塞进 `file_pattern`：

```
search_source{ query: "StealAIDebugDrawer", file_pattern: "RimWorld/StealAIDebugDrawer.cs" }
→ No results found. Try adjusting your search query or file pattern.
```

**而「路径写错就报错」的那个工具反而是好的**：

```
read_file{ path: "Verse/Thing.cs" }
→ Error: File not found: Verse/Thing.cs          ← 响亮、诚实
```

⇒ **`search_source` 在这件事上会撒谎，`read_file` 不会。越规范地照抄工具输出，越中招。**

**第二条错法：中间少一级目录 —— 吐槽板 `[21]` 的现场。**
真实路径是 `Source/Verse/AI/Pawn_JobTracker.cs`，写成 `Source/Verse/Pawn_JobTracker.cs`
（首级对了、**中间少了 `AI/`**）同样**静默零命中**（实测）：

```
search_source{ query: "JobTracker", file_pattern: "Source/Verse/AI/Pawn_JobTracker.cs" }
→ 4 条，含 10:public class Pawn_JobTracker : IExposable

search_source{ query: "JobTracker", file_pattern: "Source/Verse/Pawn_JobTracker.cs" }
→ No results found. Try adjusting your search query or file pattern.
```

⇒ **规矩是「路径必须精确到最终目录」，不只是「补上 `Source/`」。**

**更狠的一条：一个完全正确、零拼写错误的目录前缀也返回 0**（实测）：

```
search_source{ query: "JobTracker", file_pattern: "Source/Verse/" }
→ No results found. Try adjusting your search query or file pattern.
```

⇒ **必须精确到「文件」这一级 —— 只给目录前缀是不成立的。**

**好消息：它接 glob，所以「猜不准路径」有零成本的正解。**
下面四种写法**全部命中**（实测，同一个 `query:"JobTracker"`）：

| `file_pattern` | 结果 |
|---|---|
| `"Source/Verse/AI/Pawn_JobTracker.cs"` | 4 条 ✅ 精确路径 |
| `"Pawn_JobTracker.cs"`（**裸 basename**） | 4 条 ✅ **补救首选** |
| `"**/Pawn_JobTracker.cs"` | 4 条 ✅ **补救首选** |
| `"*JobTracker*"` | 4 条 ✅ **但补救时别用它**（见下） |

⚠️ **`*X*` 这类宽泛通配能用，但不要拿它做补救。** 原因是**严重程度的差别**：
`*JobTracker*` 会把**无关文件**一起捞进来，于是零命中（假阴性）立刻变成**一堆非零命中**，
人从里面挑一条当答案 —— **那是把假阴性换成了假阳性**，不是把问题解决了。
`AllPawnsSpawned` 的 400/416 就是同一个陷阱的另一副面孔。
**补救只走 basename 或 `**/<文件名>`**：它们命中且只命中你要的那个文件。

⇒ **`[16]`（缺首级）与 `[21]`（缺中间级）是同一根因的两种写错姿势，不是两条规则。**
**猜不准目录时就写 basename 或 `**/<文件名>`** —— 这已经能命中，**零改动、不需要上游做任何事**
（板上建议 2「含 `/` 但 0 命中时自动放宽到 basename 重试」**已是现成功能**）。

**通用诊断探针（本条最实用的东西）：用 `query:"."` 配你怀疑的那个 `file_pattern` 试一次。**
`query:"."` 匹配每一行非空行，**与你要找的内容无关**，所以它的结果只反映
「这个 pattern 到底选中了多少文件」（实测）：

```
search_source{ query: ".", file_pattern: "Source/Verse/Pawn_JobTracker.cs" }   ← 路径写错
→ No results found. Try adjusting your search query or file pattern.        ← 0 行：连文件都没选中

search_source{ query: ".", file_pattern: "Pawn_JobTracker.cs" }                ← 换成 basename
→ [TRUNCATED] Showing 400/1077 results.                                       ← 选中了：路径原本写错
```

⇒ **一次调用就把「路径写错」与「内容没有」彻底分开。**
零命中时先跑这个探针，**不要猜、不要改业务代码、不要急着"顺手把前缀补回去"**。
（顺带实测：`query:"."` 配 `file_pattern:"Source/Verse/"` 同样返回 0，
再次证明「只给目录前缀」不算选中文件。）

> ⚠️ **不要因为踩过这个坑就整体弃用 `search_source`。**
> 吐槽板 `[16]` 曾给出的绕法是「别用 `search_source` 核 Verse 内部调用链」——
> **那建立在错误诊断上，照做等于为了一个路径 bug 把一个好工具整个废掉。**
> 实测 `search_source` 搜 `Verse` 里的东西搜得好好的（`StopDead` →
> `Source/Verse/AI/Pawn_PathFollower.cs:204` 就摆在那儿），坏的只是**路径写法**。
> `[21]` 又踩了同一根因的另一种姿势（少写中间级 `AI/`）—— **这更说明是写法问题，不是工具问题**。
> **正确姿势就是规矩 0（写 basename / `**/<文件名>`）+ 规矩 3 的第 0 步（先诊断再动手），不是弃用。**

### 规矩 1 —— 写 `query` 的两条禁忌：它按**正则**解释；别把你猜的签名塞进去

**禁忌一：`query` 里的字面量必须转义正则元字符。**

这个服务底层就是 `rg`（ripgrep），`query` 无论 `regex` 标志设不设都按正则跑（实测见下方错误信息里的 `rg:`）。
所以字面量里的 `.` `?` `*` `+` `(` `)` `[` `]` `|` `^` `$` `{` `}` `\` **必须转义**。
未转义**不会**「零命中」，而是**匹配放大**（实测）：

| `query` | 返回 |
|---|---|
| `pather?.StopDead()`（未转义） | **28 条命中**，绝大多数是 `pather.StopDead();` / `base.pawn.pather.StopDead();` —— **不含 `?` 的行也被算成命中** |
| `pather\?\.StopDead\(\)`（已转义） | **12 条命中**，全部是真的 `pather?.StopDead();` 空条件调用点 |

⇒ 未转义得到的是**假阳性**：你以为在精确找一串代码，实际拿到一堆不相干的行，
**同样会得出事实相反的结论**（方向与零命中相反，但危害同级）。

括号不配对时是**响亮**的错误、不是静默失败（实测）：

```
search_source{ query: "StopDead(", file_pattern: "Source/Verse/AI/Pawn_PathFollower.cs" }
→ Error: rg failed with exit code 2: rg: regex parse error:
    (?:StopDead()
    ^
  error: unclosed group
```

**禁忌二：不要把返回类型 / 可见性修饰符写进 `query`。**

`search_source` 是**行级匹配**，没有符号表。查
`public IEnumerable<Pawn> AllPawnsSpawned` 会得到零命中，而这个成员**确实存在且是 `public`**
—— 真实返回类型是 `IReadOnlyList<Pawn>`。

实测（零命中）：

```
search_source{ query: "public IEnumerable<Pawn> AllPawnsSpawned", file_pattern: "*.cs" }
→ No results found. Try adjusting your search query or file pattern.
```

同一个成员实际长这样：

```
search_source{ query: "AllPawnsSpawned =>", file_pattern: "Source/Verse/MapPawns.cs" }
→ ./Source/Verse/MapPawns.cs
  327:	public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;
```

**`search_source` 的返回值有两个方向的陷阱，必须分开看 —— 只有 A 侧才是「零命中」。**

**（A）零命中时：`No results found` 同时承载四种语义**

1. 该成员**真的不存在**；
2. 该成员**存在**，但返回类型 / 签名和你猜的不同；
3. 该成员**存在于第三方模组里**（不在本服务索引内，见 §0）；
4. **你的 `file_pattern` 少了 `Source/` 前缀**（见规矩 0）—— 这一条最常见、也最容易完全不去想。

⇒ **零命中永远不是「成员不存在」的证据。**

**（B）第五态：非零命中也可能完全不是你要找的那个成员**

5. **你命中的是"另一个"成员** —— 默认大小写不敏感（`case_sensitive` 默认 `false`，
   见规矩 2），于是 `destination` 同时匹配 `private` 字段 `destination` 与
   `public` 属性 `Destination`；同前缀子串同理（`AllPawnsSpawned` 会连
   `public int AllPawnsSpawnedCount` 一起命中）。

⇒ **非零命中只证明「这段文本里出现过这个词」，不证明「它是你要找的那个成员」，
更不证明「它是这个可见性」。**

⚠️ **这一侧比 A 侧更危险**：零命中会让人起疑，非零命中却像「印证了我的猜测」，
人会**直接采信**并据此写代码 —— 这就是「用非零结果造出事实相反的结论」。
⇒ **任何存在性 / 可见性结论都必须来自规矩 2 的「声明行正则 + `case_sensitive:true`」，
不能来自一次裸名宽搜的命中条数。**

### 规矩 2 —— 存在性 / 签名判定用「声明行正则」，不要裸名宽搜

裸名宽搜 `AllPawnsSpawned`（`file_pattern:"*.cs"`）实测返回
`[TRUNCATED] Showing 400/416 results.`，目标声明行所在的文件块排在**第 19 位**；
输出顺序**既不是字母序、也不保证跨调用稳定**（同一 query 的另一次观测里它排在第 5 位）。
**不能靠翻找。**

改用**只匹配声明行**的正则。模板（把 `<成员名>` 换成裸成员名）：

```
^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)+<成员名>\s*(=>|;|=|\()
```

参数固定用 `regex: true` + `case_sensitive: true`。

> ⚠️ **`case_sensitive` 默认是 `false` —— 也就是默认大小写不敏感。所以它必须显式传，不能靠默认。**
>
> 这不是「严谨一点更好」，而是**不传就会串味**。实测：默认情况下搜 `destination` 会**同时**命中
> `Source/Verse/AI/Pawn_PathFollower.cs` 的 `:33` **`private`** 字段
> `private LocalTargetInfo destination;` 和 `:81` 的 **`public`** 属性
> `public LocalTargetInfo Destination => destination;` —— **两个可见性相反的成员一起命中。**
>
> 同一 query、同一个文件，**只差一个参数**（实测）：
>
> ```
> search_source{ query: "public LocalTargetInfo destination",
>                file_pattern: "Source/Verse/AI/Pawn_PathFollower.cs" }
> → ./Source/Verse/AI/Pawn_PathFollower.cs
>   81:	public LocalTargetInfo Destination => destination;     ← 假阳性：查的是小写 d，给的是大写 D
>
> 同一调用 + case_sensitive: true
> → No results found. Try adjusting your search query or file pattern.
> ```
>
> ⇒ **凡是做存在性 / 可见性判定，一律显式传 `case_sensitive: true`。**
> **假阳性比假阴性更危险**：零命中至少会让人起疑；非零命中看起来像「印证了我的猜测」，
> 人会**直接采信** —— 于是「我搜到了 public 的它」这个事实相反的结论就写进源码了。

其中 `([\w<>\.]+\s+)+` 用来吃掉夹在可见性修饰符与类型之间的
`static` / `readonly` / `const` / `override` 等修饰符；
`\s*(=>|;|=|\()` 同时覆盖三类声明：**属性**（`=>`）、**字段 / 自动属性**（`=` 或 `;`）、
**方法**（`\(`，方法声明以参数列表的左括号收尾）。

> 🚨 **末尾的 `\(` 不是可选项 —— 少了它，这个模板对「方法」一律零命中。**
> 这是个**被正例掩盖的错误**：本模板最早的实测正例是 `AllPawnsSpawned`（属性）与
> `destination`（字段），**两者恰好都在前两类里**，所以模板「看起来一直能用」，
> **实际上从没被方法验证过**。直到有人拿它搜一个方法才暴露（实测）：
>
> ```
> search_source{ query: "^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)+TryRecoverFromUnwalkablePosition\s*(=>|;|=)",
>                regex: true, case_sensitive: true, file_pattern: "*.cs" }
> → No results found. Try adjusting your search query or file pattern.     ← 模板对方法完全失效
> ```
>
> 加上 `\(` 之后，同一个成员名得到**恰好 2 条、跨两个类型各 1 条、修饰符与完整签名俱全、
> 零条调用点混入**：
>
> ```
> search_source{ query: "^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)+TryRecoverFromUnwalkablePosition\s*(=>|;|=|\()",
>                regex: true, case_sensitive: true, file_pattern: "*.cs" }
> → ./Source/Verse/AI/Pawn_PathFollower.cs
>   529:	public bool TryRecoverFromUnwalkablePosition(bool error = true)
>
> ./Source/RimWorld/Planet/Caravan_PathFollower.cs
>   238:	private bool TryRecoverFromUnwalkablePosition()
> ```
>
> ⇒ **教训比结论更重要**：一个「已被正例验证过」的模板，**不等于对所有成员形态都验证过**。
> 每加一类新的声明形态，都要**单独跑一次反例**（拿一个该形态的成员名去试）。

实测（只读）：

| 形态 | 成员 | 返回 |
|---|---|---|
| 属性 | `AllPawnsSpawned` | `./Source/Verse/MapPawns.cs`<br>`327:	public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;` |
| 字段 | `pawnsSpawned` | `./Source/Verse/MapPawns.cs`<br>`44:	private List<Pawn> pawnsSpawned = new List<Pawn>();` |
| 字段（跨类型） | `destination` | 全仓 **6 个文件块**：`Pawn_PathFollower.cs:33 private`、`Verse/Projectile.cs:16 protected`、`CompProperties_EffectWithDest.cs:7 public`、`ShipJob_WaitSendable.cs:10 public`、`QuestPart_AddShipJob_WaitSendable.cs:8 public`、`QuestNode_AddShipJob_FlyAway.cs:7 public` |
| 方法 | `TryRecoverFromUnwalkablePosition` | **2 条**：`Source/Verse/AI/Pawn_PathFollower.cs:529 public bool …(bool error = true)` 与 `Source/RimWorld/Planet/Caravan_PathFollower.cs:238 private bool …()` |
| `static` / `private` 不漏 | `TicksGame` | **2 条**：`./Source/RimWorld/GenDate.cs` `39:	private static int TicksGame => Find.TickManager.TicksGame;`<br>`./Source/Verse/TickManager.cs` `53:	public int TicksGame => ticksGameInt;` |

⇒ **属性 / 字段 / 方法三类都覆盖；`static` / `private` 成员不漏。**
上下文税与假阴性一起解决。

**这条规则同时让「成员存在但 private」变得可判**（实测，`regex:true` + `case_sensitive:true`，
声明行模板搜 `destination`，带 `file_pattern` 收窄到 `Source/Verse/AI/Pawn_PathFollower.cs`）：

```
33:	private LocalTargetInfo destination;        ← 命中，且可见性就在这一行里写着
```

**已知漏网**（漏了就走规矩 3，**不要**下「不存在」的结论）：
泛型方法（`<T>` / `where` 子句）、索引器 `this[...]`、显式接口实现
（`string IFoo.Bar`）、`event` / `operator` / `unsafe` / `extern`，
以及由宏或分部类拼出来的声明。

### 规矩 3 —— 零命中时的四步排除法（结论只能从这里出）

对任何「某成员是否存在」的疑问，按顺序走完这四步，才允许写结论：

0. **【最有价值的一步】如果你这次带了 `file_pattern` 却零命中 —— 先分清是「路径写错」
   还是「内容没有」，再动手。两个只读动作，任选其一：**

   - **通用探针（推荐）**：`search_source{ query: ".", file_pattern: "<你原来那个 pattern>" }`。
     仍然 0 ⇒ **这个 pattern 连文件都没选中**，是**路径写错**；
     返回了条数 ⇒ pattern 没问题，问题在 `query` 本身（回去走规矩 1）。
   - **兜底动作**：原样去掉 `file_pattern`、用同一个 `query` 重搜一次。
     有命中 ⇒ 同样是**铁证是 `file_pattern` 写错了**，**不是内容不存在**。

   ⚠️ **别在没诊断之前"顺手把前缀补回去"** —— 路径写错**不止缺首级一种**：
   缺 `Source/`（`[16]`）、**中间少一级目录**（`[21]`：真实 `Source/Verse/AI/Pawn_JobTracker.cs`
   写成 `Source/Verse/Pawn_JobTracker.cs`）、或**只给了目录没给文件**（`"Source/Verse/"` 也是 0）
   —— 详见规矩 0。只补首级很可能**改的地方和错的地方不是一处，你会以为修好了、其实没修**。
   确诊后**直接用 `file_pattern:"<文件名>"` 或 `"**/<文件名>"`**，不要再手拼整条路径。
   **注意这一步的理由是「避免把假阴性换成假阳性」，不是「避免捞进无关类」** ——
   后者只是整洁，前者关乎正确性：`*X*` 这类宽泛通配会把无关文件一起捞进来，
   零命中立刻变成一堆非零命中，人从中挑一条当答案 —— **那是 `[19]` 的死法，不是修好了**。

   **没做完这一步之前，不允许写下任何「不存在 / 不可见」的结论。**
1. **去掉 `query` 里的一切限定** —— 返回类型、可见性修饰符、以及 `.` `?` `(` `)` 等
   未转义的元字符（见规矩 1）—— 只留裸成员名，**显式传 `case_sensitive: true`**，
   做**不带 `file_pattern`** 的裸名宽搜。
   有命中 → **不要拿这条宽搜的命中下结论**，用规矩 2 的声明行正则（或 `read_file`）
   读出那一行的**真实签名**。

   > ⚠️ **`case_sensitive` 在这一步同样不能省 —— 漏掉它，这条排除法自己就会串味。**
   > 实测：`file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"`、裸搜 `destination`（**默认参数**）
   > 返回 **33 条**，`:33` 的 **`private`** 字段与 `:81` 的 **`public`** 属性
   > **混在同一个返回里**。也就是说：不传 `case_sensitive` 时，这一步会同时"证实"两个
   > 可见性相反的成员，而你以为它只证实了一个。

   ⚠️🚨 **本步的「换口径」清单必须显式包含第三种口径：集合字段的【元素类型】**
   （**2026-10-03 增补**，依据 `triage/45-search-source-generic-element-type-false-negative.md` §2 / §6.2）。

   | 写法 | 实测 |
   |---|---|
   | `search_source{ query: "public List<Region> links" }` | **0 命中** |
   | 真值 `Source/Verse/Region.cs:20` | **`public List<RegionLink> links = new List<RegionLink>();`** |

   **元素类型是 `RegionLink`，不是 `Region`。**

   **为什么它必须单列**：这一形态与「路径写错」「模式非法」「可见性写错」**全都不同形** ——
   `query` 按工具说明**是正则**，`public List<Region> links` 里的 `<` `>` 是**普通字面量**、没有任何需要转义的元字符
   ⇒ **语法完全合法**；同一次会话、同一个索引，**正控 `search_source{query:"ShouldAvoidFences"}` → 4 条**、`search_source{query:"public static TraverseParms For"}` → 2 条
   ⇒ **通道是通的**；路径也没错。
   ⇒ **「我是不是哪里写错了」这三个自查方向，一个都不会被触发**（`triage/45-…md` §2）。

   **⇒ 判据（硬）**：零命中的 `query` 里含 `List<` / `Dictionary<` / `HashSet<` / `IEnumerable<` / `IList<` 时，
   **必须**把元素类型换一组候选**各查一次**（`RegionLink` 与 `Region` 都试），才允许往下走第 2 步。

   > 🚨 **关键：不加这一项，规矩 3 在最常见的情形下（不带 `file_pattern`）走完仍然是 0。**
   > 实测：`search_source{ query: "links", case_sensitive: true }`（**全仓、无 `file_pattern`**）返回 **1055 条**、
   > **只回显前 400 条**，而 `./Source/Verse/Region.cs:20` **不在这 400 条里** ⇒ 裸名宽搜救不回来，
   > 调用方很容易径直走到第 3 步写下「vanilla 源码里没有这个名字」。
   > ⚠️ **唯一的例外**：若已经把 `file_pattern` 锚到具体文件，则
   > `search_source{ query: "links", case_sensitive: true, file_pattern: "Source/Verse/Region.cs" }` → **命中 `:20`**，
   > 此时能靠**读出真实签名**自救 —— 但那是**碰巧**，不是规矩，**不能拿来替代这一条**。
2. **裸名也零命中** → 先确认拼写与大小写；再确认它是否属于**第三方模组**
   （本服务索引里只有 vanilla，见 §0）—— 模组成员只能查该模组自己的源码，
   或走 `decompilerserver` 的 `search_types` / **`search_members`**（**优先用 `search_members` 做枚举**；
   `get_members_of_type` 在重名类型上会谎报完整，见**规矩 4B** 的 🚨 段）。
   **不要指望 `search_defs` 兜底**：它**只索引 Defs XML**，实测
   `search_defs("Gun_Revolver")` → `[ThingDef] Gun_Revolver (label: "revolver")`，
   而 `search_defs("Pawn_PathFollower")` → `No results found. Try a shorter keyword.`
   —— C# 类名它一个都不收。
3. **以上都不是** → 此时才可以说「**vanilla 源码里没有这个名字**」。

⇒ **禁止**把 `No results found` 直接翻译成「这个成员不存在 / 不可见」，
再写进源码、注释、计划或报告。

### 规矩 4 —— 同名 / 重载：**命中不带「类型身份」，枚举会整块漏掉**

前面四条都在讲「写法」；这一条讲**工具给的信息本身不够**。
它是假阴性里最阴的一种：**不是搜不到，是搜到了「半个」**。

#### 4A `search_source` 侧：**跨类型同名方法是常态，命中不带类型标识**

vanilla 里同名的方法经常散落在多个类型上，而 `search_source` 返回的每一行
**只有文件名 + 行号 + 代码原文，代码原文里没有类型名**（类名在文件更上方，不在返回里）。
⇒ **你看到 N 条同名命中，并不知道它们分属几个类型。**

实测（`TryRecoverFromUnwalkablePosition`，见规矩 2 的完整调用）：**2 条，分属 2 个类型**
（`Source/Verse/AI/Pawn_PathFollower.cs:529` 与 `Source/RimWorld/Planet/Caravan_PathFollower.cs:238`），
**一个 `public`、一个 `private`**。

⇒ **做法**：
- 判定可见性时**逐条读修饰符**（模板已经把 `public`/`private` 原文带出来了），**不要只数命中条数**；
- 想知道「我调用的那个类型里到底有没有」→ 拿到路径后**用 `read_file` + 类的声明行**确认类型名，
  或直接把 `file_pattern` 收窄到那个文件；
- ⚠️ **同名方法还会让「这个方法存在吗」变成无意义的问句** —— 必须先说清是**哪个类型**的。

#### 4B `decompilerserver` 侧：**两个类型共用一个 `fullName`，`get_members_of_type` 只枚举其中一个**

> ⚠️ **注意这是另一个外部 MCP**（`decompilerserver`，离线反编译路线），
> 同样**源码不在本仓库、改不动**。它与 `rimworld-source-online` 是两个服务，
> 但**踩的是同一类坑（重名 → 结论不完整）**，所以写在一起。**可见性的权威实现见 §4 与 [10]。**

实测 `search_types("ThingOwner")` 返回**两个 `fullName` 完全相同的类型**：

| `memberId` | `fullName` | 说明 |
|---|---|---|
| `61e4173561894da49d210260257b5097:020008E2:T` | `Verse.ThingOwner` | 泛型那个 |
| `61e4173561894da49d210260257b5097:020008EC:T` | `Verse.ThingOwner` | 非泛型那个 |

⇒ **对其中一个调 `get_members_of_type` 只能枚举到它自己那一块**，另一块**整块不出现**。
实测 `totalEstimate`：泛型 `020008E2` = **37**、非泛型 `020008EC` = **62**，
**两次调用看到的 `declaringType` 都是 `Verse.ThingOwner`，无法从返回值分辨自己看的是哪一个。**

🚨 **「看全集」不许走 `get_members_of_type`（2026-10-03 增补，依据 `triage/29-…md` §3.2 / §6.3）：**

> **`get_members_of_type` 不得被写成「要看某个类型的全部成员，请用它」的入口。**
> 理由就是上面这段：**它在重名类型上返回一个子集，却报 `hasMore:false`**（`Verse.ThingOwner` 实测
> `hasMore:false, totalEstimate:27`，真值 **77**）⇒ 那不是「没列完」，那是**谎报列完了**。
> ⇒ **看全集请用 `search_members`**：`search_members(query:"<名字>", declaringTypeFilter:"<类型>", kind:"<kind>")` 取 `totalEstimate`
> （**两个工具对不上本身就是告警信号**，见下方 📌 规则）。
> ⚠️ 若仍要用 `get_members_of_type`，**必须传 canonical type `memberId`**（见下面第 1 条实情），
> 且**必须自己核对 `totalEstimate` 是否等于 `search_members` 的读数** —— 对不上就是踩到了重名。
> ⚠️ `search_members` 的 `declaringTypeFilter` **看不见扩展方法**（`triage/47-…md`）：
> 扩展方法的**声明类型是静态类**、不是被扩展类型 ⇒ **凡以被扩展类型作 `declaringType` 的查询必然 0 命中**。
> ⇒ **任何带 `declaringTypeFilter` 的 0 命中都不能当「该 API 不可调用」的证据**（恒真断言，判别力 = 0）；
> 判断「能不能调用」必须再做一次**不带该过滤**的全仓检索。

**三条必须知道的实情（全部实测）：**

1. **`typeId` 要传 canonical type `memberId`，不能传点分类型名。**
   传 `61e4173561894da49d210260257b5097:020008EC:T` → 正常返回，
   其中就有 `Boolean Contains(Thing item)`、`accessibility: "Public"` ✅
2. 🚨 **`get_overloads` 返回的是「与被查者同名的其他重载」：不含被查者本身，可见性一律不管。**
   实测查 `Verse.ThingOwner.Contains` 的 `06003A5A:M`（= `Boolean Contains(Thing item)`），
   `get_overloads` 只返回另外 **2 个** `Contains(ThingDef def)` / `Contains(ThingDef def, Int32 minCount)`
   —— **它给的是"别的重载"，不是"这一条"**。
   ⚠️ **「不做可见性过滤」已由推断升为实证**（`triage/43-…md` §2，2026-10-03）：
   **反过来查那个 public 的** `get_overloads("…:06006324:M")`（`Verse.AI.PathGrid`，`RecalculatePerceivedPathCostAt`），
   返回 `totalEstimate: 1`，而**那唯一一条是 `06006323:M`，返回体里 `accessibility: "Private"`**
   ⇒ **返回 private 的那条，说明过滤不存在**。
   ⇒ 🚫 **禁止用它的条数 / `totalEstimate` 论证「是否存在重载歧义」**：
   `totalEstimate` 在其它工具里一律表示「全量计数」，在这里却表示**「全量减一」**，且 `hasMore:false` 是**一个主动的谎**
   ⇒ 照它下结论必然拿到**假阴性**，而那个假阴性**恰好否认「补丁挂错重载」**（`triage/43-…md` §4，🔴 高）。
   **要看全部重载，请改用**：
   `search_members(query:"<方法名>", declaringTypeFilter:"<声明类型>", kind:"Method")` → 取 `totalEstimate`。
   （⚠️ `search_members` 的 `declaringTypeFilter` **看不见扩展方法**——但**重载不是扩展方法**，枚举重载不受此影响。）
3. **类型名报错时，错误信息里的 `typeName` 可能只是最后一段。**
   实测 `search_types("Verse.AI.Pawn_PathFollower")` → 正常返回；
   `search_types("Verse.GenPath")` → **0 条**。
   ⇒ **点分全限定名是被接受的**（板上「不接受点分名」的说法不成立）；
   `Verse.GenPath` 报错**不是因为写法**，而是**该类型真名是 `Verse.AI.GenPath`、你少写了一级命名空间**。
   报错文本可能把命名空间当类型名报出来 —— **看到 `typeName` 像命名空间时，先怀疑自己少写了一级**。

> 📌 **一条可以泛化成告警信号的规则**：
> **同一个类型的成员数，在 `search_members` 与 `get_members_of_type` 两个工具里对不上，
> 本身就是「你踩到重名类型了」的信号** —— 不是某个工具坏了，是被枚举的那块不完整。
> （实测：`search_members(declaringTypeFilter:"Verse.ThingOwner", query:"Contains")` 返回 4 条，
> 全部 `declaringType: "Verse.ThingOwner"` —— **filter 分不清那两个同名类型**。）

⇒ **通用纪律**：**任何「枚举出的成员/命中」的结论，都要先确认「我是不是只看了一个重名的那一个」。**
确认手段：① 看 `memberId` / `typeId` 而不是名字；② 用另一个工具交叉计数；
③ 类型名报错时**逐级补全命名空间**再试。

## 2. 标准检索流程（三段式）

```
① 宽搜   search_source{ query:"<裸成员名>", file_pattern:"*.cs", case_sensitive:true }
                                                                     → 只为拿到「文件在哪」
        （`*.cs` 锚在 server 根，安全；**不要**写成 "Verse/..." 这类缺 Source/ 的形式）
        （case_sensitive **必须显式传 true**，默认 false 会大小写串味，见规矩 2）
        一旦看到 [TRUNCATED] 就别在结果里翻找，直接进 ②
② 定位   search_source{ query:"<声明行正则>", regex:true, case_sensitive:true }
                                                                     → 拿到真实签名 + 可见性 + 1-based 行号
③ 精读   read_file{ path:"<②的路径>", start_line:<②的行号 - 1>, line_count:20 }   → 只为看上下文
```

- **② 的输出是行号的唯一真值来源。** `search_source` 的行号是 **1-based**，可直接引用。
- ③ 的 `start_line` 是 **0-indexed**（`start_line = 目标1based行号 - 1`），
  且返回值**不带任何行号前缀**。⇒ **禁止从 `read_file` 的返回内容反推行号**；
  `read_file` 只用于看上下文。
- 实测 ③ 的返回形态
  （`read_file{ path:"Source/Verse/MapPawns.cs", start_line:326, line_count:4 }`）：

  ```
  public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;
  （空行）
  public List<Pawn> FreeColonistsSpawned => FreeHumanlikesSpawnedOfFaction(Faction.OfPlayer);
  （空行）
  [TRUNCATED] Showing 4/966 lines.
  (Tip: Continue reading using `start_line`: 330)
  ```

  首行即 1-based 第 327 行；末尾那两个脚注是**正常输出，不是失败**，不要当成「没读到」。

## 3. 上下文预算

- 实测 `search_source` 有 **400 条硬上限**，超出时返回
  `[TRUNCATED] Showing 400/416 results.` 与
  `(Tip: Refine your search query or add a more specific \`file_pattern\`.)`。
- 这个上限与截断声明都是上游的**正确**行为。**不要**在本仓库给它包一层代理 / 包装工具
  —— 那解决不了假阴性，只会多出第二份要维护的索引。
- 调用方的义务：**看到 `[TRUNCATED]` 就停止在结果里翻找**，改走规矩 2 的收窄查询。
- 同一类事故在本项目已经发生过一次：`MCP/index.js` 的 `list_debug_action_children`
  曾一次返回 400KB+，把子代理上下文冲爆（吐槽板条目 `[03]`）。检索侧不要再来一遍。

## 4. 与 [10] 的边界（不要互相重复实现）

- 吐槽板 `[10] api-member-visibility` 与本文件是**同一能力缺口的两面**：
  本服务是**行级文本匹配**，**原理上没有符号表**，答不了「某成员对调用方到底可不可见」。
- **本文件负责**（已在本文件解决，不需要再做）：怎么搜、零命中怎么判读、上下文预算。
  §1 规矩 2 的声明行正则确实会把修饰符**原文**带出来（见上面 `private static` vs `public` 的样例），
  但那是**源码字面量**，不是**可访问性判定** —— 它不解析 `internal` + `InternalsVisibleTo`、
  不做泛型约束与重载决议、也完全看不到第三方模组的程序集。
- **成员可见性查询能力归 [10]** 实现，落点在本仓库 `McpRimDebug/DebugSession.Eval.cs`
  （经 Cecil 元数据输出 `isPublic` 等字段），计划见
  `docs/issue-triage/plans/2026-10-01-10-api-member-visibility-tools.md`。
  **本文件不实现它，也不重复它的工具。**

## 5. 维护规则

- 任何关于 `rimworld-source-online` 的新踩坑（`file_pattern` 的 `Source/` 前缀陷阱、
  `query` 的正则元字符转义、`read_file` 的 0-indexed、命名空间 ≠ 目录、
  `read_file` / `list_directory` 的 `[TRUNCATED]` 脚注、其它工具的截断行为……）
  **追加到本文件对应小节**，**不要另建文件**。本文件是该服务的**唯一**规范文档。
- 每条追加必须有**一次实测调用 + 它的真实返回**。写不出返回的，说明还没核实，**不要写进来**。
- **每加一条新的「写法规则」，必须补一个反例。** 只跑通正例不构成验证 ——
  声明行模板对属性/字段一直是对的，对方法却完全失效，就是被正例掩盖的。
  判定标准：新规则至少要在一个**原本会失败的形态**上跑过一次。
- **若 `COMMON.md`（两项目共用规约，路径见下）里已有相关纪律，指向它、不要复述。**
  但注意**检索通道不同**：它的 §5.2 正控纪律管的是**在产物 DLL 里做字节级检索**，
  **管不到**本文件讲的 `search_source` 行级匹配（详见 §0.8 的对照表）。
  已知相关位置：§5.1「`Player.log` 零命中的头号假阴性」、`:413`「报告零命中必须附上检索模式原文」。
- **不要用「在某个目录里没搜到」推出「不存在」** —— 检索范围本身就是结论的一部分。
  `COMMON.md` 在 `TEXT20` 项目下、不在 UESDdebuger 仓库内，这个坑已经真实踩过一次。
- 与吐槽板条目的对应关系（**一张表，不要再分散写在各处**）：

  | 条目 | 严重度 | 落在本文件哪里 | 状态 |
  |---|---|---|---|
  | `[11]` | 🟠 | §1 规矩 1（query 写法）、规矩 2（声明行正则）、§3（400 条上限） | 待绕过 → 由本文件规范化 |
  | `[13]` | 🟡 | 规矩 0（路径锚定）+ 规矩 2 里「命名空间 ≠ 目录」那段 | **已判「不值得修」**：工具行为是对的；本文件只保留「**引用 vanilla 行号必须带 `Source/` 前缀的完整目录**」这一条书写约定 |
  | `[14]` | 🟠 | §2 ③ 的 `start_line` 0-indexed | 待绕过 |
  | `[16]` | 🔴 | **规矩 0（`file_pattern` 必须带 `Source/`）+ 规矩 3 的第 0 步** | 同一 token、同一文件，**仅路径写法不同**：3 条命中 vs 零命中、4 条命中 vs 零命中。**板上「别用 `search_source` 核 Verse 调用链」那条绕法已被证伪，不要采纳** |
  | `[19]` | 🔴 | **规矩 2 的 `case_sensitive` 警告块 + 规矩 1 的（B）第五态 + 规矩 3 第 1 步的 `case_sensitive` 要求** | 板上「成员存在但 private ⇒ 零命中 ⇒ 无法判别」**复现不成立**；真病因是**默认大小写不敏感导致的假阳性**。板上引的 `COMMON.md` **确实存在**（在 **TEXT20 项目**下，非本仓库），其 §5.2 确有硬核正控纪律、§5.1 与 `:413` 也确有「零命中要附检索模式原文」的同类纪律 ⇒ **「已绕过」是「部分有依据」**：认知纪律侧站得住，但 `COMMON.md` **没有** `search_source` 的 `case_sensitive` 默认值等**具体机制**，本计划补的正是这一半 |
  | `[21]` | 🟠（**弱于 `[16]` / `[19]`**） | **规矩 0 的「第二条错法」+「通用诊断探针」+ 规矩 3 的第 0 步** | 与 `[16]` **同一根因的另一种姿势**（缺中间级 `AI/`，不是缺首级 `Source/`）。**弱于前两条的理由**：**自投毒不是被投毒**、规矩 3 第 0 步**一步抓到**、且**路径写错本身不会直接造出假阳性** —— **但放宽 pattern 补救会引入 `[19]` 型假阳性风险**：假阳性不是被排除，是**被推迟**到「人开始修的那一刻」；说「没风险」与说「风险换了个位置」给出的行动指引完全不同。板上两条建议**都不需要上游改动** |
  | `[27]` | 🔴 | **规矩 2 的模板末尾 `\(` 分支 + 反例 + 维护规则「补反例」那条** | **本计划自身的缺陷**：模板只被属性/字段验证过，**对方法完全失效**。已修 |
  | `[29]` | 🔴 | **规矩 4B** | 两个类型共用 `fullName`，`get_members_of_type` 只枚举一块。**跨服务**（`decompilerserver`），与本文件其余部分是同族不同源 |

  裁决报告在 `docs/issue-triage/triage/` 下同名文件。
````

- [ ] **步骤 3：核查正文落盘且锚点齐全**

运行：

```powershell
$p = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\mcp-source-lookup-conventions.md'
Select-String -Path $p -Pattern '^## 0\. 归属与边界','^## 1\. 五条硬规矩','^### 规矩 0 ——','^### 规矩 3 —— 零命中时的四步排除法','^### 规矩 4 ——','^## 2\. 标准检索流程','^## 3\. 上下文预算','^## 4\. 与 \[10\] 的边界','^## 5\. 维护规则','\s\*\(=>\|;\|=\|\()'
```

预期：**10 行命中**，行号分别递增且都小于 400。若少于 10，说明漏了小节或规矩，补齐再继续。
**特别确认模板那一行末尾是 `(=>|;|=|\()` 而不是 `(=>|;|=)`** —— 少一个分支整个模板对方法失效。

- [ ] **步骤 4：Commit**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
git add docs/mcp-source-lookup-conventions.md
git commit -m "docs: add rimworld-source lookup conventions (path anchoring, declaration regex incl. methods, zero-hit rule)"
```

> 若你在 `docs/issue-triage/AGENT-BRIEF.md` 的禁改范围内运行（流水线子代理），
> **跳过本步骤**，且本任务整体也不该由你执行 —— 见 §0.3。

## 任务 2：在 `AI-DEPLOYMENT.md` 挂入口，让 AI 真的会读到它

**文件：**
- 修改：`AI-DEPLOYMENT.md` —— 在 `## 8. 排障决策树（AI 遇错优先自查）` 小节内，
  `**通用日志入口**：` 的两条列表之后、分隔线 `---` 之前，**插入**下列三级标题块。
  **纯插入，不改动该小节任何既有行。**

- [ ] **步骤 1：确认插入锚点存在且唯一**

运行：

```powershell
$p = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\AI-DEPLOYMENT.md'
Select-String -Path $p -Pattern '^\*\*通用日志入口\*\*：'
```

预期：**恰好 1 行命中**。为 0 说明文件结构与本计划假设不符，停下来重新定位；
大于 1 说明有重复小节，也停下来。

- [ ] **步骤 2：插入入口块**

在紧接该行之后（`## 8` 小节内、`---` 之前）插入以下内容，逐字写入：

```markdown
### 8.1 查 vanilla 源码：外部 MCP `rimworld-source-online`（非本项目部署）

查 RimWorld 原版 API 的真实签名时，用 `rimworld-source-online` 的 `search_source` /
`read_file` / `list_directory`。它是**独立于本模组**的外部 MCP 服务，源码不在本仓库；
索引里**只有 vanilla 源码**（`Source/` 与 `Defs/`），**不含任何模组源码**。

**最容易踩的几条（违反会得出事实相反的结论）：**

1. **`file_pattern` 必须带 `Source/` 前缀、且精确到文件。** 它相对的是**该服务自己的索引根**，
   不是仓库根、也不是 `Source/`。同一 token、同一文件，`"Source/Verse/Thing.cs"` 命中 3 条、
   `"Verse/Thing.cs"` 零命中（🔴）。**中间少一级目录**同样零命中，而**正确的目录前缀
   `"Source/Verse/"` 也是零命中**。它接 glob，**猜不准就写 `"<文件名>"` 或 `"**/<文件名>"`**。
   **零命中时用 `query:"."` 配原 pattern 探一次**：0 = 路径没选中文件；有条数 = 路径没问题。
2. **禁止把返回类型 / 可见性修饰符写进 `query`。** `No results found` 不区分
   「成员不存在」「签名和你猜的不同」「它在某个模组里」「你的 `file_pattern` 少一级前缀」。
   另外 `query` 按正则跑，字面量里的 `.` `?` `(` `)` 必须转义，否则**匹配放大**成假阳性。
3. **存在性判定用「声明行正则」**，不要裸名宽搜 —— 裸名 `AllPawnsSpawned` 实测
   `Showing 400/416 results`，声明行埋在中间且顺序不稳定。模板：
   `^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)+<成员名>\s*(=>|;|=|\()`，
   **必须**配 `regex: true` + `case_sensitive: true`。
   ⚠️ 末尾的 `\(` **不能省** —— 省了对**方法**一律 0 命中（`\(` 收方法、`=>` 收属性、`;`/`=` 收字段）。
   ⚠️ `case_sensitive` **默认是 `false`**，漏传会大小写串味：查 `destination` 会连
   `public` 的 `Destination` 属性一起命中（实测）。**假阳性比假阴性更危险** ——
   非零命中像是在印证你的猜测，人会直接采信。
4. **同名成员 / 方法跨类型很常见，而命中里不带类型名。** 看到 N 条同名命中，
   要**逐条读修饰符**，不要只数条数。

完整规范（含每条规矩的可复现调用与真实返回）见
**[`docs/mcp-source-lookup-conventions.md`](docs/mcp-source-lookup-conventions.md)**。
成员可见性的权威查询不归该文件（那是另一议题 `[10]`，见 `docs/mcp-吐槽板.md` 索引表的 `[10]` 行）。
```

- [ ] **步骤 3：确认插入成功且链接目标存在**

运行：

```powershell
$root = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path "$root\AI-DEPLOYMENT.md" -Pattern '^### 8\.1 查 vanilla 源码'
Test-Path "$root\docs\mcp-source-lookup-conventions.md"
```

预期：第一条**恰好 1 行命中**；第二条为 `True`。
再确认既有内容没被动过：

```powershell
Select-String -Path 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\AI-DEPLOYMENT.md' -Pattern '^## 9\. 一句话提醒','^\*\*通用日志入口\*\*：'
```

预期：仍各 **1 行命中**（说明只是插入，没有改写或重复既有行）。

- [ ] **步骤 4：Commit**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
git add AI-DEPLOYMENT.md
git commit -m "docs: link rimworld-source lookup conventions from AI-DEPLOYMENT troubleshooting"
```

> 同任务 1 步骤 4 的约束：流水线子代理跳过；且 §0.3 说的并行会话期间不要动 git 工作树。

## 任务 3：验收演练 —— 证明规范里写的每一句都还是真的

**文件：**
- 无新建、无修改。**只读验证**；若实测与规范里的样例不一致，才回到任务 1 修正样例。
- ⚠️ **同样的纪律适用于演练记录**：按 `COMMON.md §9`，**跑完几步就先落盘几步**，
  不要攒到最后一次写（原文：「盘上的东西不会因为服务商抖动而消失，你脑子里的会」）。
  若在输出端被截断，已落盘的部分仍然算数。

- [ ] **步骤 1：跑「反例演练」，确认假阴性仍然存在（这份规范要治的病没消失）**

调用（只读）：

```
mcp__rimworld-source-online__search_source
{ "query": "public IEnumerable<Pawn> AllPawnsSpawned", "file_pattern": "*.cs" }
```

预期输出（逐字）：

```
No results found. Try adjusting your search query or file pattern.
```

若这里**居然有命中**，说明上游服务变了 —— 停下来重新评估整份规范，别硬改文档。

- [ ] **步骤 2：跑「路径前缀」对照组，确认规矩 0 的两种写法一真一假**

调用（只读，连续两次，**只改 `file_pattern` 一个字**）：

```
mcp__rimworld-source-online__search_source
{ "query": "StealAIDebugDrawer", "file_pattern": "Source/Verse/Thing.cs" }

mcp__rimworld-source-online__search_source
{ "query": "StealAIDebugDrawer", "file_pattern": "Verse/Thing.cs" }
```

预期输出（逐字）：

```
第一次 → ./Source/Verse/Thing.cs
         883:	StealAIDebugDrawer.Notify_ThingChanged(this);
         1010:	StealAIDebugDrawer.Notify_ThingChanged(this);
         1591:	StealAIDebugDrawer.Notify_ThingChanged(this);     （3 条）
第二次 → No results found. Try adjusting your search query or file pattern.
```

- [ ] **步骤 3：跑「中间少一级目录」反例，确认缺中间级同样静默零命中**

调用（只读，连续两次，**只差中间那级 `AI/`**）：

```
mcp__rimworld-source-online__search_source
{ "query": "JobTracker", "file_pattern": "Source/Verse/AI/Pawn_JobTracker.cs" }

mcp__rimworld-source-online__search_source
{ "query": "JobTracker", "file_pattern": "Source/Verse/Pawn_JobTracker.cs" }
```

预期输出（逐字）：

```
第一次 → ./Source/Verse/AI/Pawn_JobTracker.cs
         10:public class Pawn_JobTracker : IExposable
         85:	public Pawn_JobTracker(Pawn newPawn)
         131:	public virtual void JobTrackerTick()
         138:	public virtual void JobTrackerTickInterval(int delta)     （4 条）
第二次 → No results found. Try adjusting your search query or file pattern.
```

- [ ] **步骤 4：跑「glob 三写法」演练，确认 basename / `**/` / `*X*` 全部命中**

调用（只读，三次，**同一个 `query`、只改 `file_pattern`**）：

```
mcp__rimworld-source-online__search_source
{ "query": "JobTracker", "file_pattern": "Pawn_JobTracker.cs" }

mcp__rimworld-source-online__search_source
{ "query": "JobTracker", "file_pattern": "**/Pawn_JobTracker.cs" }

mcp__rimworld-source-online__search_source
{ "query": "JobTracker", "file_pattern": "*JobTracker*" }
```

预期：**三次返回完全相同的 4 条**（`./Source/Verse/AI/Pawn_JobTracker.cs` 的 10 / 85 / 131 / 138）。
任何一次不同，就要回来更新规矩 0 里那张表。

- [ ] **步骤 5：跑「`query:\".\"` 诊断探针」，确认它能把路径错与内容没有分开**

调用（只读，三次）：

```
mcp__rimworld-source-online__search_source
{ "query": ".", "file_pattern": "Source/Verse/Pawn_JobTracker.cs" }     ← 写错的路径

mcp__rimworld-source-online__search_source
{ "query": ".", "file_pattern": "Source/Verse/" }                      ← 正确但只到目录

mcp__rimworld-source-online__search_source
{ "query": ".", "file_pattern": "Pawn_JobTracker.cs" }                 ← basename
```

预期：

```
第一次 → No results found. Try adjusting your search query or file pattern.
第二次 → No results found. Try adjusting your search query or file pattern.
第三次 → [TRUNCATED] Showing 400/1077 results.
         (Tip: Refine your search query or add a more specific `file_pattern`.)
```

⇒ **前两次 0 = 这个 pattern 连文件都没选中（路径问题）；第三次有 = 选中了。
这就是「一次调用分开路径错 / 内容没有」的完整证据，也是规范正文那段探针的支撑。**

- [ ] **步骤 6：跑「照抄工具路径」反例，确认 `read_csharp_symbol` 返回头确实带毒**

调用（只读）：

```
mcp__rimworld-source-online__read_csharp_symbol { "typeName": "StealAIDebugDrawer" }
```

预期：**首行**是 `// File: RimWorld/StealAIDebugDrawer.cs (Lines 7-135)` ——
注意它**没有 `Source/` 前缀**。再把它原样喂回去：

```
mcp__rimworld-source-online__search_source
{ "query": "StealAIDebugDrawer", "file_pattern": "RimWorld/StealAIDebugDrawer.cs" }
```

预期：`No results found. Try adjusting your search query or file pattern.`
—— 这就是「照抄工具自己打印的路径必然零命中」的实证。
（**不要**把本步写成 `typeName:"Thing"`：实测 `read_csharp_symbol` 对
`{ typeName: "Thing", memberName: "Notify_ThingChanged" }` 返回的是
`Method 'Notify_ThingChanged' in type 'Thing' not found in index.`，不是路径头。）

- [ ] **步骤 7：跑「正则转义」对照组，确认不转义是匹配放大而不是零命中**

调用（只读，连续两次）：

```
mcp__rimworld-source-online__search_source { "query": "pather?.StopDead()", "file_pattern": "*.cs" }

mcp__rimworld-source-online__search_source { "query": "pather\\?\\.StopDead\\(\\)", "file_pattern": "*.cs" }
```

预期：**第一次 28 条命中**（其中大量是 `pather.StopDead();` 这类**不含 `?`** 的行 —— 假阳性）；
**第二次 12 条命中**，且每一条都是真空条件调用 `pather?.StopDead();`。
数一下命中行数对不上也不要紧，**关键差别是「未转义会把不含 `?` 的行也算进来」**。
若第一次变成零命中，说明服务端改了正则语义，回来更新规范正文里的这张表。

- [ ] **步骤 8：跑「大小写串味」对照组，确认 `case_sensitive` 默认 `false` 且必须显式传**

调用（只读，连续两次，**只差 `case_sensitive` 一个字段**）：

```
mcp__rimworld-source-online__search_source
{ "query": "public LocalTargetInfo destination",
  "file_pattern": "Source/Verse/AI/Pawn_PathFollower.cs" }          ← 不传 case_sensitive

mcp__rimworld-source-online__search_source
{ "query": "public LocalTargetInfo destination",
  "case_sensitive": true,
  "file_pattern": "Source/Verse/AI/Pawn_PathFollower.cs" }
```

预期输出（逐字）：

```
第一次 → ./Source/Verse/AI/Pawn_PathFollower.cs
         81:	public LocalTargetInfo Destination => destination;      ← 假阳性
第二次 → No results found. Try adjusting your search query or file pattern.
```

若第一次**零命中**，说明上游改了默认行为 —— 回去更新规范正文里 `case_sensitive` 的警告块。

- [ ] **步骤 9：跑「裸名宽搜不传 `case_sensitive`」反例，确认规矩 3 第 1 步确实会串味**

调用（只读）：

```
mcp__rimworld-source-online__search_source
{ "query": "destination", "file_pattern": "Source/Verse/AI/Pawn_PathFollower.cs" }
```

预期：**一个文件块、约 33 条命中**，其中 **`:33` 是 `private LocalTargetInfo destination;`**、
**`:81` 是 `public LocalTargetInfo Destination => destination;`** —— 两个可见性相反的成员
**同时**出现。看到这个就说明：不传 `case_sensitive` 时，规矩 3 的排除法会自己串味。

- [ ] **步骤 10：跑「存在但 private 可判」演练，确认声明行模板能同时给出可见性**

调用（只读）：

```
mcp__rimworld-source-online__search_source
{ "query": "^\\s*(public|protected|internal|private)\\s+([\\w<>\\.]+\\s+)+destination\\s*(=>|;|=|\\()",
  "regex": true, "case_sensitive": true,
  "file_pattern": "Source/Verse/AI/Pawn_PathFollower.cs" }
```

预期输出（逐字，一条）：

```
./Source/Verse/AI/Pawn_PathFollower.cs
33:	private LocalTargetInfo destination;
```

再去掉 `file_pattern` 全仓跑同一 query，预期 **6 个文件块**。
这一条是「存在但 private 也能判」的证据；缺了它，规范正文最后那段加强说明就没有支撑。

- [ ] **步骤 11：🚨 跑「方法」正反例，确认模板末尾的 `\(` 分支在位**

这是 `[27]` 暴露的那个缺陷的专属验收，**必须跑**。

调用（只读，两次）：

```
mcp__rimworld-source-online__search_source
{ "query": "^\\s*(public|protected|internal|private)\\s+([\\w<>\\.]+\\s+)+TryRecoverFromUnwalkablePosition\\s*(=>|;|=)",
  "regex": true, "case_sensitive": true, "file_pattern": "*.cs" }      ← 旧模板（缺 \()）

mcp__rimworld-source-online__search_source
{ "query": "^\\s*(public|protected|internal|private)\\s+([\\w<>\\.]+\\s+)+TryRecoverFromUnwalkablePosition\\s*(=>|;|=|\\()",
  "regex": true, "case_sensitive": true, "file_pattern": "*.cs" }      ← 新模板
```

预期输出（逐字）：

```
第一次 → No results found. Try adjusting your search query or file pattern.
第二次 → ./Source/Verse/AI/Pawn_PathFollower.cs
         529:	public bool TryRecoverFromUnwalkablePosition(bool error = true)

         ./Source/RimWorld/Planet/Caravan_PathFollower.cs
         238:	private bool TryRecoverFromUnwalkablePosition()
```

⇒ **第一次 0 / 第二次 2** 就是「旧模板对方法完全失效」的证据；
**第二次恰好 2 条、跨两个类型各 1 条、零条调用点混入** 则是新模板的证据。
**如果第一次不再是 0，说明有人动了模板 ⇒ 立刻回去检查规范正文那一行。**

- [ ] **步骤 12：跑「正解演练」，确认属性正例仍然有效**

调用（只读）：

```
mcp__rimworld-source-online__search_source
{ "query": "^\\s*(public|protected|internal|private)\\s+([\\w<>\\.]+\\s+)+AllPawnsSpawned\\s*(=>|;|=|\\()",
  "regex": true, "case_sensitive": true, "file_pattern": "*.cs" }
```

预期输出（逐字，只有一条）：

```
./Source/Verse/MapPawns.cs
327:	public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;
```

- [ ] **步骤 13：跑「修饰符分支」演练，确认 `static` / `private` 成员不被模板漏掉**

调用（只读）：

```
mcp__rimworld-source-online__search_source
{ "query": "^\\s*(public|protected|internal|private)\\s+([\\w<>\\.]+\\s+)+TicksGame\\s*(=>|;|=|\\()",
  "regex": true, "case_sensitive": true, "file_pattern": "*.cs" }
```

预期输出（逐字，两条）：

```
./Source/RimWorld/GenDate.cs
39:	private static int TicksGame => Find.TickManager.TicksGame;

./Source/Verse/TickManager.cs
53:	public int TicksGame => ticksGameInt;
```

- [ ] **步骤 14：跑「重名类型」演练，确认规矩 4B 的三条实情仍然成立**

调用（只读，四组）：

```
mcp__decompilerserver__search_types { "query": "ThingOwner" }
```

预期：返回**两个 `fullName` 都是 `Verse.ThingOwner`** 的类型
（泛型 `…:020008E2:T` 与非泛型 `…:020008EC:T`）。

```
mcp__decompilerserver__get_members_of_type
{ "typeId": "61e4173561894da49d210260257b5097:020008EC:T", "mode": "discovery", "limit": 3 }

mcp__decompilerserver__get_members_of_type
{ "typeId": "61e4173561894da49d210260257b5097:020008E2:T", "mode": "discovery", "limit": 3 }
```

预期：两次都返回 `declaringType: "Verse.ThingOwner"`，
但 **`totalEstimate` 不同**（实测 **62** 与 **37**，合计 99）。
⇒ **这就是「只看了一个重名的那一个」的证据**；数字对不上（工具版本变了）也要记录，
因为规范正文写死了实测值。

```
mcp__decompilerserver__get_member_signature { "memberId": "61e4173561894da49d210260257b5097:06003A5A:M" }
```

预期：`signature: "Boolean Contains(Thing item)"`、`accessibility: "Public"` ✅

```
mcp__decompilerserver__get_overloads { "memberId": "61e4173561894da49d210260257b5097:06003A5A:M" }
```

预期：**只返回另外 2 条** `Contains(ThingDef def)` / `Contains(ThingDef def, Int32 minCount)`，
**`Boolean Contains(Thing item)` 自己不在返回里** —— 这就是规矩 4B 第 2 条的证据。

```
mcp__decompilerserver__search_types { "query": "Verse.AI.Pawn_PathFollower" }
mcp__decompilerserver__search_types { "query": "Verse.GenPath" }
```

预期：第一条**返回 1 条**（点分名可用）；第二条 **0 条**（真名是 `Verse.AI.GenPath`，少写一级）。

- [ ] **步骤 15：跑「第三段精读」演练，确认 `start_line` 换算成立**

调用（只读）：

```
mcp__rimworld-source-online__read_file
{ "path": "Source/Verse/MapPawns.cs", "start_line": 326, "line_count": 4 }
```

预期：**首行**是 `public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;`
（即 1-based 第 327 行），末尾带 `[TRUNCATED] Showing 4/966 lines.` 与
`(Tip: Continue reading using \`start_line\`: 330)`。

- [ ] **步骤 16：确认索引范围（零命中的第三种语义）**

调用（只读）：

```
mcp__rimworld-source-online__list_directory { "path": "" }
```

预期输出（逐字，两行）：

```
Defs/
Source/
```

- [ ] **步骤 17：确认 `search_defs` 补不了 C# 那一支**

调用（只读，连续两次）：

```
mcp__rimworld-source-online__search_defs { "query": "Gun_Revolver" }

mcp__rimworld-source-online__search_defs { "query": "Pawn_PathFollower" }
```

预期输出（逐字）：

```
第一次 → [ThingDef] Gun_Revolver_Unique (label: "unique revolver")
         [ThingDef] Gun_Revolver (label: "revolver")
第二次 → No results found. Try a shorter keyword.
```

⇒ `search_defs` **只索引 Defs XML**，不能拿来兜底 C# 类的存在性判断。

- [ ] **步骤 18：环境受限时的处理**

若当前会话**没有**上述 MCP 工具（步骤 1–17 无法执行），
**不要**跳过、也不要编造输出。在本计划的执行记录里写一行：
「验收步骤 1–17 未执行：当前会话未接入这些外部 MCP，为环境受限。」
任务 1、任务 2 的产物仍然有效 —— 规范里的每条样例都是 2026-10-01 实测记录，
可被任何人按步骤 1–17 独立复现。

- [ ] **步骤 19：Commit（仅当步骤 1–18 的结果与规范一致，无需改动时）**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
git status --short
```

预期：**无输出**（工作树干净，说明验收没有产生意外改动）。
若有输出，先查清来源再决定，不要盲目 `git add -A`。

---

## 4. 交回主代理的移交清单（**不是执行任务，不要自己做**）

1. **给吐槽板条目 `[11]`、`[16]`、`[19]`、`[21]`、`[27]`、`[29]` 各加一条 Triage 批注**：
   都指向本计划与 `docs/mcp-source-lookup-conventions.md`。`[11]` 状态改为「已绕过（检索姿势已规范化）」。
   **`[16]` 的批注要明确写清「板上那条『别用 `search_source` 核 Verse 调用链』的绕法已被证伪，
   正确姿势是 `file_pattern` 补 `Source/` 前缀」** —— 否则下一个人会照着错误绕法把好工具废掉。
   **`[21]` 的批注要写清**：① 与 `[16]` **同根因**（缺中间级 `AI/`，不是缺首级）；
   ② 严重度取 **🟠、且注明弱于 `[16]`/`[19]`**（自投毒非被投毒、一步抓到、不会造假阳性）——
   板上正文 🔴 与索引 🟠 的自相矛盾按 🟠 收敛；③ 板上两条建议**都不需要上游改**
   （basename 放宽已是现成功能，文件计数可用 `query:"."` 探针在调用侧等价实现）。
   **`[19]` 的批注要写清**：① 真病因是**默认大小写不敏感造成的假阳性**，
   不是板上写的「零命中无法判别」；② 板上引的 `COMMON.md` **存在**
   （673 行，在 **TEXT20 项目**下 `Mods\TEXT20\.superpowers\sdd\2026-10-01-原版寻路接管-缺口修复-总计划\COMMON.md`，
   **不在本仓库**），§5.2 确有硬核正控纪律 ⇒ **重核 `[19]` 的「已绕过」到底兜住了多少**：
   **认知纪律侧站得住，机制侧（`search_source` 的 `case_sensitive` 默认值等）`COMMON.md` 没写**，
   所以**不能当成完全被兜住而直接降严重度**。
   **`[27]` 的批注要写清**：① 表层诊断对、真机制是**声明行正则对方法失效**；
   ② 这条同时指出了**本计划自身的一个缺陷**，已修（模板末尾加 `\(`）。
   **`[29]` 的批注要写清**：① **跨服务**（是 `decompilerserver`，不是 `rimworld-source-online`），
   板上没说这一点；② 三条实情（`typeId` 要 canonical、`get_overloads` 不含被查的那条、
   **点分全限定名是被接受的 —— 板上「不接受点分名」的说法不成立**）。
   **吐槽板只有主代理能写**（`AGENT-BRIEF.md` §1.1），本计划不碰它。
2. **把 [13] / [14] 的文档化落点指向同一份规范文档**
   （`docs/mcp-source-lookup-conventions.md` 里的 `## 2. 标准检索流程` 与 `## 1` 的规矩 2），
   **不要**让它们各自新建文件 —— 统一收进那份文档的 `## 5. 维护规则`。
3. **不要**为「零命中的可判读性」向本仓库提工单：那是上游 `rimworld-source-online` 的返回契约问题，
   本项目修不了；本计划已把它转成调用方约定。
4. **`COMMON.md` 若要做两项目共用规约**：加一节指路到本规范即可，**不要**把规范正文搬去 TEXT20，
   也**不要**改 `COMMON.md` 本身（它不在本仓库，且本流水线只写 `docs/issue-triage\`）。

---

## 5. 本计划的实测记录（2026-10-01，全部只读，可复现）

| # | 调用 | 实际返回 | 用途 |
|---|---|---|---|
| 1 | `search_source{query:"public IEnumerable<Pawn> AllPawnsSpawned", file_pattern:"*.cs"}` | `No results found. Try adjusting your search query or file pattern.` | 假阴性证据 |
| 2 | `search_source{query:"AllPawnsSpawned =>", file_pattern:"Source/Verse/MapPawns.cs"}` | `./Source/Verse/MapPawns.cs` / `327:	public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;` | 成员真实签名 + `file_pattern` 接受完整相对路径 |
| 3 | `search_source{query:"AllPawnsSpawned", file_pattern:"*.cs"}` | `[TRUNCATED] Showing 400/416 results.` + `(Tip: Refine your search query or add a more specific \`file_pattern\`.)`；`MapPawns.cs` 块排在**第 19 位**，顺序非字母序 | 上下文税上限 + 顺序不可依赖 |
| 4 | 声明行模板（成员名 `AllPawnsSpawned`，`regex:true, case_sensitive:true`） | 1 条：`327:	public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;` | 声明行正则 · 属性 |
| 5 | 同模板，成员名 `pawnsSpawned` | 1 条：`./Source/Verse/MapPawns.cs` / `44:	private List<Pawn> pawnsSpawned = new List<Pawn>();` | 字段分支（`=`）可用 |
| 6 | 同模板，成员名 `TicksGame` | 2 条：`./Source/RimWorld/GenDate.cs` / `39:	private static int TicksGame => …`；`./Source/Verse/TickManager.cs` / `53:	public int TicksGame => ticksGameInt;` | `static` / `private` 分支不漏 |
| 7 | `read_file{path:"Source/Verse/MapPawns.cs", start_line:326, line_count:4}` | 首行即 1-based 327 行；末尾 `[TRUNCATED] Showing 4/966 lines.` + `(Tip: Continue reading using \`start_line\`: 330)` | `start_line` 0-indexed 换算 |
| 8 | `list_directory{path:""}` | `Defs/` / `Source/` | 索引只有 vanilla |
| 9 | `MCP/` 目录 grep `search_source\|start_line\|rimworld-source-online` | 零命中 | 本仓库无该工具实现，改不动 |
| 10 | 全仓库 grep `start_line` | 仅 markdown 文档命中，代码零命中 | 同上 |
| 11 | `search_source{query:"StealAIDebugDrawer", file_pattern:"Source/Verse/Thing.cs"}` | 3 条：`./Source/Verse/Thing.cs` 的 883 / 1010 / 1591 行 | 规矩 0 的正例 |
| 12 | `search_source{query:"StealAIDebugDrawer", file_pattern:"Verse/Thing.cs"}` | `No results found. …` | 规矩 0 的反例（仅路径写法不同） |
| 13 | `search_source{query:"StopDead", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}` | 4 条，含 `204:	public void StopDead()` | 同 token 铁证 |
| 14 | `search_source{query:"StopDead", file_pattern:"Verse/AI/Pawn_PathFollower.cs"}` | `No results found. …` | 同上，反例 |
| 15 | `read_csharp_symbol{typeName:"StealAIDebugDrawer"}` | 首行 `// File: RimWorld/StealAIDebugDrawer.cs (Lines 7-135)` —— **无 `Source/` 前缀** | 工具自己打印带毒路径 |
| 16 | `search_source{query:"StealAIDebugDrawer", file_pattern:"RimWorld/StealAIDebugDrawer.cs"}` | `No results found. …` | 照抄工具路径 → 零命中 |
| 17 | `read_file{path:"Verse/Thing.cs"}` | `Error: File not found: Verse/Thing.cs` | `read_file` 诚实报错，`search_source` 不报 |
| 18 | `search_source{query:"pather?.StopDead()", file_pattern:"*.cs"}`（未转义） | **28 条命中**，大量是 `pather.StopDead();`（**不含 `?`**） | 假阳性（转义规则的对照组） |
| 19 | `search_source{query:"pather\\?\\.StopDead\\(\\)", file_pattern:"*.cs"}`（已转义） | **12 条命中**，全部是真空条件 `pather?.StopDead();` | 转义后的精确集 |
| 20 | `search_source{query:"StopDead(", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}` | `Error: rg failed with exit code 2: rg: regex parse error: … unclosed group` | 底层是 `rg`；括号不配对是**响亮**错误 |
| 21 | `read_csharp_symbol{typeName:"Thing", memberName:"Notify_ThingChanged"}` | `Method 'Notify_ThingChanged' in type 'Thing' not found in index.` | **未能复现「返回头打印 `Verse/Thing.cs`」那一说**，见 §0.7 |
| 22 | `search_source{query:"public LocalTargetInfo destination", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}`（**默认参数**） | 1 条：`81:	public LocalTargetInfo Destination => destination;` —— **假阳性** | `case_sensitive` 默认 `false` 的铁证 |
| 23 | 同一调用 + `case_sensitive:true` | `No results found. …` | 只差一个参数，结论相反 |
| 24 | `search_source{query:"destination", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}`（**默认参数**） | **33 条**；`:33 private` 字段与 `:81 public` 属性混在同一返回 | 裸名宽搜不传 `case_sensitive` 会串味 |
| 25 | 声明行模板 + `regex:true, case_sensitive:true, file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"`，成员名 `destination` | 1 条：`33:	private LocalTargetInfo destination;` | **「存在但 private」可判** |
| 26 | 同上但**不带** `file_pattern`（全仓） | **6 个文件块**（`Pawn_PathFollower.cs:33`、`Verse/Projectile.cs:16 protected`、`CompProperties_EffectWithDest.cs:7 public`、`ShipJob_WaitSendable.cs:10 public`、`QuestPart_AddShipJob_WaitSendable.cs:8 public`、`QuestNode_AddShipJob_FlyAway.cs:7 public`） | 同上 |
| 27 | `search_defs{query:"Gun_Revolver"}` | `[ThingDef] Gun_Revolver_Unique …` / `[ThingDef] Gun_Revolver …` | `search_defs` 只索引 Defs XML |
| 28 | `search_defs{query:"Pawn_PathFollower"}` | `No results found. Try a shorter keyword.` | 同上，C# 类名一个都不收 |
| 29 | `search_source{query:"JobTracker", file_pattern:"Source/Verse/AI/Pawn_JobTracker.cs"}` | 4 条：`10:public class Pawn_JobTracker : IExposable` 等 | 规矩 0「第二条错法」的正例 |
| 30 | `search_source{query:"JobTracker", file_pattern:"Source/Verse/Pawn_JobTracker.cs"}`（**缺中间级 `AI/`**） | `No results found. …` | **反例 —— 首级对、中间错，一样零命中** |
| 31 | `search_source{query:"JobTracker", file_pattern:"Source/Verse/"}`（**零拼写错误，只到目录**） | `No results found. …` | **只给目录前缀也不成立** |
| 32 | `search_source{query:"JobTracker", file_pattern:"Pawn_JobTracker.cs"}`（裸 basename） | 与 #29 **完全相同的 4 条** | glob：basename 可用 |
| 33 | 同上但 `file_pattern:"**/Pawn_JobTracker.cs"` | 与 #29 **完全相同的 4 条** | glob：`**/` 可用 |
| 34 | 同上但 `file_pattern:"*JobTracker*"` | 与 #29 **完全相同的 4 条** | glob：`*X*` 可用（但**补救时别用**） |
| 35 | `search_source{query:".", file_pattern:"Source/Verse/Pawn_JobTracker.cs"}`（**诊断探针**） | `No results found. …` | 探针：pattern 连文件都没选中 → **路径写错** |
| 36 | `search_source{query:".", file_pattern:"Pawn_JobTracker.cs"}`（诊断探针） | `[TRUNCATED] Showing 400/1077 results.` | 探针：选中 → **路径原本写错，不是内容没有** |
| 37 | **旧模板**（`(=>\|;\|=)`）搜 `TryRecoverFromUnwalkablePosition` | `No results found. …` | 🚨 **模板对方法完全失效**（`[27]`） |
| 38 | **新模板**（`(=>\|;\|=\|\()`）搜同一成员名 | **2 条**：`Source/Verse/AI/Pawn_PathFollower.cs` `529:	public bool TryRecoverFromUnwalkablePosition(bool error = true)`；`Source/RimWorld/Planet/Caravan_PathFollower.cs` `238:	private bool TryRecoverFromUnwalkablePosition()` | 修复证据；**跨类型同名、修饰符俱全、零调用点混入** |
| 39 | `search_types{query:"ThingOwner"}` | **9 条结果里有两条 `fullName` 都是 `Verse.ThingOwner`**：`…:020008E2:T`（泛型）与 `…:020008EC:T`（非泛型） | `[29]` 重名类型 |
| 40 | `get_members_of_type{typeId:"…:020008EC:T", mode:"discovery", limit:3}` | `totalEstimate: 62`，`declaringType: "Verse.ThingOwner"` | 只枚举非泛型那一块 |
| 41 | `get_members_of_type{typeId:"…:020008E2:T", mode:"discovery", limit:3}` | `totalEstimate: 37`，`declaringType: "Verse.ThingOwner"` | 只枚举泛型那一块；**两块加起来 99，一个调用看不到全部** |
| 42 | `get_member_signature{memberId:"…:06003A5A:M"}` | `signature: "Boolean Contains(Thing item)"`，`accessibility: "Public"` | canonical `typeId` 路径可用 |
| 43 | `get_overloads{memberId:"…:06003A5A:M"}` | **2 条**：`Contains(ThingDef def)` / `Contains(ThingDef def, Int32 minCount)` —— **被查的 `Boolean Contains(Thing item)` 不在里面** | `get_overloads` 不含被查的那条 |
| 44 | `search_members{query:"Contains", declaringTypeFilter:"Verse.ThingOwner", limit:10}` | 4 条，全部 `declaringType: "Verse.ThingOwner"` | **filter 分不清那两个同名类型** |
| 45 | `search_types{query:"Verse.AI.Pawn_PathFollower"}` | 1 条：`fullName: "Verse.AI.Pawn_PathFollower"` | **点分全限定名可用** |
| 46 | `search_types{query:"Verse.GenPath"}` | `0 条` | 真名是 `Verse.AI.GenPath`，**少写一级命名空间**，不是写法不被接受 |

**这些数字（416 条、第 19 位、4/966 行、28 vs 12 条、1077 行、62 vs 37）都绑定当前那一份
vanilla 源码索引与当前那一份反编译程序集。** 外部服务升级后可能变化 ——
届时按任务 3 重跑一遍，用新实测替换样例，并在提交信息里写明差异。

裁决：值得修
归并：并入计划 11 的 **规矩 2（声明行正则，`plans\2026-10-01-11-search-source-false-negative-convention.md:340` 之后）**，并连带修正 **规矩 3 第 1 步（`:379-381`）**、**规矩 1 禁忌二的四态清单（`:318-324`）**、**`AI-DEPLOYMENT.md` 入口第 3 条（`:527-530`）**（理由：本条的真实病因是 `search_source` **默认大小写不敏感**导致同名兄弟成员串味，而计划 11 的规范正文文件**此刻尚未创建**（`Test-Path = False`），现在回灌是一次编辑，计划 11 执行后再补就是第二遍改；且计划 11 的**维护规则**本就规定同族踩坑「追加到本文件对应小节，不要另建文件」）

**一句话结论**：吐槽板 `[19]` 的**现象属实但因果描述错了** —— 它贴的那条复现**按默认参数根本不会返回 `No results found`**，实测返回的是**一条「看起来正好印证你」的命中**（另一个成员）；真正成立、且比板上写的更严重的问题是 `search_source` **默认大小写不敏感**，会把 `private` 字段和同名 `public` 属性混成一条命中，**用非零结果造出事实相反的结论**；这一支**计划 11 没有覆盖**，其余两态已被它覆盖。

---

## 3. 核实过程

### 3.1 吐槽板原文（行号经 `[System.IO.File]::ReadAllLines` 复核，快照总行数 **849**）

- 锚点 `## [19]` 在**第 40 行**；`## [18]` 在第 80 行 ⇒ 条目正文占 40–78 行。
- `:44` 复现调用：`search_source(query="public LocalTargetInfo destination", file_pattern="Source/Verse/AI/Pawn_PathFollower.cs")` → 板上称回 `No results found`。
- `:55` 板上自称的「正解」：`^\s*(public|private|internal).*(LocalTargetInfo destination|PathEndMode peMode)`。
- `:58-60` 板上贴出的命中（**只贴了 2 行**）。
- `:64` 影响段标 **🔴**；而索引行标 **🟠**（**板内自相矛盾，见 §4**）。
- `:74` 「**任何零命中都要配一条必然命中的正控模式**，这条已经写进本项目 `COMMON.md`」。
- `:76` 状态：`待绕过（调用侧已用正控模式兜住；工具未修）`。

### 3.2 三条要核实的，逐条实测（全部 `rimworld-source-online` 只读调用）

#### 核实一：**三态真的分不清吗？——「板上的复现按默认参数不成立」**

同一个 query / 同一个 `file_pattern`，**只改 `case_sensitive` 一个参数**：

| # | 调用 | 真实返回 |
|---|---|---|
| A | `search_source{query:"public LocalTargetInfo destination", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}`（**默认**） | `./Source/Verse/AI/Pawn_PathFollower.cs`<br>`81:	public LocalTargetInfo Destination => destination;` ← **有命中** |
| B | 同上，**加 `case_sensitive:true`** | `No results found. Try adjusting your search query or file pattern.` ← 板上描述的那个返回 |

⇒ **板上 `:44` 的「回 `No results found`」只在传了 `case_sensitive:true` 时才成立**；按工具默认参数，它返回的是一条**指向另一个成员**的命中。
这条命中的危害比零命中更大：它长得**完全像你要的答案**（`public` + `LocalTargetInfo` + `Destination`），调用方极可能直接记成
「**存在一个 public 的 `LocalTargetInfo destination`**」—— 而你问的字段在 `:33` 是 `private`。**这是用非零结果造出的事实相反结论。**

不带修饰符的一侧：

| # | 调用 | 真实返回 |
|---|---|---|
| C | `search_source{query:"LocalTargetInfo destination", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}`（默认） | **2 条**：`:33  private LocalTargetInfo destination;` + `:81  public LocalTargetInfo Destination => destination;` |
| D | `search_source{query:"private LocalTargetInfo destination", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}`（默认） | **1 条**：`:33  private LocalTargetInfo destination;` |

**`search_defs` 能不能补上这一支？——不能，实测排除：**

| # | 调用 | 真实返回 |
|---|---|---|
| E | `search_defs{query:"Pawn_PathFollower", limit:5}` | `No results found. Try a shorter keyword.` |
| F | `search_defs{query:"Gun_Revolver", limit:5}`（**对照**） | `[ThingDef] Gun_Revolver_Unique (label: "unique revolver")` / `[ThingDef] Gun_Revolver (label: "revolver")` |

⇒ F 证明工具本身是好的、E 是**范围问题**：它只索引 `Defs/` 的 Def XML（与计划 11 `:763` 实测的 `list_directory{path:""}` → `Defs/` `Source/` 一致），**对 C# 成员零覆盖，补不上 `[19]` 这一支**。

#### 核实二：`regex: true` 能否让「成员存在但 private」可判？——**能，计划 11 的模板实测通过**

| # | 调用 | 真实返回 |
|---|---|---|
| G | `search_source{query:"^\s*(public\|protected\|internal\|private)\s+([\w<>\.]+\s+)+destination\s*(=>\|;\|=)", regex:true, case_sensitive:true, file_pattern:"*.cs"}` | 6 个文件块，其中 `./Source/Verse/AI/Pawn_PathFollower.cs`<br>`33:	private LocalTargetInfo destination;` |
| H | 板上自称的「正解」`^\s*(public\|private\|internal).*(LocalTargetInfo destination\|PathEndMode peMode)`（默认大小写） | **4 条**：`:33`、`:35 private PathEndMode peMode;`、**`:81 public LocalTargetInfo Destination => destination;`**、**`:154 public void StartPath(LocalTargetInfo dest, PathEndMode peMode)`** |

⇒ **G 成立**：`regex:true` + 声明行锚 + `case_sensitive:true` 能把 private 字段精确定位出来，「成员存在但 private」**是**可判的。
⇒ **H 不成立为「正解」**：板上只贴了 `:33`/`:35` **两行**，实际返回**四行**，多出的 `:81`（public 属性）与 `:154`（参数也叫 `peMode` 的方法）**正是大小写/子串串味**。**板上的「正解」自己就会串味。**

再验计划 11 自己推荐的那一步（规矩 3 第 1 步原文要求「裸名宽搜」，**未要求 `case_sensitive`**）：

| # | 调用 | 真实返回 |
|---|---|---|
| I | `search_source{query:"destination", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}`（默认） | **32 条**，`:33`（private 字段声明）与 `:81`（public 属性）**混在同一个文件块里**，中间夹 30 行使用点 |

⇒ **计划 11 自己规定的排除法第 1 步，在默认参数下就是大小写歧义的**。这是计划 11 的**真实缺口**，且可被下述实测直接推翻/证实。

#### 核实三：计划 11 的「零命中判读契约」是否已给出「去掉返回类型限定再试」的提示？——**逐字比对：已给**

- `plans\2026-10-01-11-…md:297` 原文小标题：**「禁忌二：不要把返回类型 / 可见性修饰符写进 `query`。」**
- `:318` 原文：**「`No results found` 这一个返回，同时承载四种语义：」** —— 随后 `:320-323` 逐条列出（真的不存在 / 签名与你猜的不同 / 在第三方模组里 / `file_pattern` 少 `Source/` 前缀）。
- `:379-381` 规矩 3 第 1 步原文：「**去掉 `query` 里的一切限定** —— 返回类型、可见性修饰符、以及 `.` `?` `(` `)` 等未转义的元字符……」
- `AI-DEPLOYMENT.md` 入口第 2 条（`:524-526`）复述同一句。

⇒ **板上那句「没有任何提示告诉调用方该去掉返回类型限定再试」不成立**，计划 11 已经给了提示、还给了可复现的对照样例（`:303-316`，`AllPawnsSpawned` 的真实返回类型是 `IReadOnlyList<Pawn>`）。
⇒ **但四种语义清单里没有第五种**（大小写折叠串味），且它只覆盖**零命中**这一侧 —— **不覆盖实测 A 那条非零命中的串味**。

### 3.3 `COMMON.md`：板上引用的那份退路纪律**不存在**

- `Test-Path`（复核）：仓库根 `COMMON.md` → **False**；`docs\COMMON.md` → **False**。仓库根 `*.md` 只有 `AI-DEPLOYMENT.md` / `README.md` / `THIRD_PARTY_NOTICES.md`；`docs\*.md` 只有 4 份，**无 `COMMON.md`**。
- 搜索范围：全仓 `*.md`（80 份，排除 `node_modules`）grep `正控` → **仅 4 处命中**，全部是**吐槽板自身**：
  `docs\mcp-吐槽板.md:74`、`docs\mcp-吐槽板.md:76`、`docs\issue-triage\inbox\pending-20261001-200606.md:42`、`…:44`。

⇒ 板上 `:74`「这条已经写进本项目 `COMMON.md`」与 `:76` 状态「调用侧已用正控模式兜住」**均无落点**。**这条不能作为降严重度的理由。**

### 3.4 板上「建议」的可行性实测

| # | 调用 | 真实返回 |
|---|---|---|
| J | `search_source{query:".", file_pattern:"Source/Verse/AI/Pawn_PathFollower.cs"}` | 全文件逐行 + `[TRUNCATED] Showing 400/971 results.` ⇒ **该文件共 971 行** |

⇒ 板上建议的「**匹配到 N 行但无一命中你的 query**」**在调用侧可以做到**（用 `.` 或 `^` 之类必然命中的模式探一次总量），不需要改上游。计划 14 已独立记录同机制（`read_file` 页脚 `Showing N/449`，`plans\2026-10-01-14-…md:97`）。

---

## 4. 影响与严重度复评

**索引 🟠 / 正文 🔴 的板内矛盾 ⇒ 复评取 🔴，但机理与板上写的不同。**

- 板上说的是「**零命中**一刀切」。实测 A 证明：**默认参数下这条查询根本不零命中**，它返回一条**指向 `public` 属性 `Destination` 的命中**。危害方向与板上写的**相反** —— 不是「分不清」，而是「**自信地答错了**」。
- 符合 AGENT-BRIEF §5 第 2 条 🔴「会让自动化得出**事实相反的结论**」：看到 `:81 public LocalTargetInfo Destination => destination;` 后写进源码/注释/计划的是「`destination` 是 public」，实际 `:33` 是 `private`。
- **升 🔴 的第二理由**：实测 I 证明**计划 11 自己规定的排除法第 1 步在默认参数下就会串味**（32 条里 `destination` 与 `Destination` 混排）。若只按现计划 11 执行，这条不会自愈 —— 反而会被「已规范」的状态掩盖。
- **不升到「最高」的理由**：有机械可救的路径（声明行正则 + `case_sensitive:true`，实测 G 通过），且 `read_file` 能直接读到该行原文；属于「按对姿势即可 100% 判对」。

**板上 🟠 vs 🔴 的不一致建议**：以 **🔴** 为准（正文影响段），并请主代理**修索引行**使其与正文一致；另建议在批注里写明「板上复现需补 `case_sensitive:true` 才成立」。

---

## 5. 责任归属

**外部 MCP 服务 `rimworld-source-online`**（源码不在本仓库，本轮复核沿用计划 11 `:17-27` 的结论，本轮未重新穷举）。

- 它不报错、不提示，把「大小写折叠」这个语义决定**静默**留在返回值里，且**工具描述未声明 `case_sensitive` 的默认值**。
- **但本项目侧仍有一份可执行的债**：计划 11 的规范正文**尚未创建**（`Test-Path docs\mcp-source-lookup-conventions.md = False`），而它已经把「`case_sensitive: true`」写成了机械参数（`:340`）却**没写它是必须的、也没写默认为 false** —— 这属于本项目的文档债，可在本仓库内修掉。

---

## 6. 修的话怎么修（方向）

**只改文档，不改代码。**（上游改不动，见计划 11 §0.1。）

1. **`docs\mcp-source-lookup-conventions.md`（由计划 11 任务 1 步骤 2 新建，此刻尚不存在）—— 规矩 2，`plans\2026-10-01-11-…md:340` 之后插入一段**，要点：
   - `search_source` 的 `case_sensitive` **默认是 `false`（大小写不敏感）**；
   - 因此**任何存在性/签名判定都必须显式传 `case_sensitive: true`**，这不是优化、是正确性前提；
   - 并给出实测反例表：`public LocalTargetInfo destination` 默认 → 命中 `:81 public LocalTargetInfo Destination => destination;`（**答的是另一个成员**）；加 `case_sensitive:true` → `No results found`。
2. **同文件规矩 1 禁忌二 `:318-324` 的四态清单 → 补第五态**：第 2 条（「签名与你猜的不同」）里点明包含「**同名不同大小写的兄弟成员（字段 vs 属性）串味**」，并强调它**发生在非零命中上**。
3. **同文件规矩 3 第 1 步 `:379-381`**：在「裸名宽搜」后补一句「**这一次也必须带 `case_sensitive: true`**；否则同文件内同名不同大小写的字段/属性会混在同一个返回块里（实测 `destination` 在 `Pawn_PathFollower.cs` 返回 32 条，含 `:33` private 字段与 `:81` public 属性）」。
4. **`AI-DEPLOYMENT.md` 入口第 3 条（`plans\2026-10-01-11-…md:527-530`）**：把 `配 regex: true + case_sensitive: true` 改成「**必须**配 `case_sensitive: true`（**默认大小写不敏感，会把 private 字段和同名 public 属性串成一条命中**）」。
5. **计划 11 任务 3（验收演练）新增一步**：跑实测 A/B/H 三组，把真实返回贴进规范的实测记录表（计划 11 `:752-779` 的格式）。
6. **不建议**做的事：不要新建规范文件、不要改 `MCP/index.js`、不要给上游提「加区分性文案」的工单当本条的主要修复（那是板上的建议，调用侧实测 J 已证明可在不改上游的前提下自证总量）。

---

## 7. 与计划 11 的关系 + 协调结论

### 7.1 [19] 是覆盖还是缺口？——**部分覆盖，缺口是「非零命中侧的大小写串味」**

| [19] 声称的三态 | 计划 11 是否覆盖 | 依据 |
|---|---|---|
| ① query 写错了 | ✅ 覆盖 | 规矩 3 第 1 步 `:379-381` |
| ② 成员不存在 | ✅ 覆盖 | 规矩 3 第 2/3 步 `:382-385` + 禁令 `:387-388` |
| ③ **成员存在但是 private** | ✅ **程序上已覆盖**（实测 G 证实可行） | 规矩 3 第 1 步 + 规矩 2 声明行正则 `:327-353`；其样例表 `:760` 本就有 private 字段（`44: private List<Pawn> pawnsSpawned = new List<Pawn>();`） |
| **（板上没写，但实测存在）④ 非零命中串味** | ❌ **完全未覆盖** | 全文 grep `case_sensitive` 命中 4 处（`:340` `:530` `:659` `:676`）**全部只是把它当机械参数**，无一处说明默认值、也无一例警告串味 |

**缺的具体是哪一步？** —— 缺的不是「排除法少一步」，而是**规矩 3 第 1 步自身缺一条前置约束**：它要求「裸名宽搜」却**没要求 `case_sensitive: true`**（实测 I：这一步默认执行就会把 `destination`/`Destination` 混成 32 条）。补这一条比新增一步更省事，也不需要改动四步排除法的骨架。

### 7.2 「成员存在但是 private」归谁？——**程序判读归计划 11，可见性权威查询归计划 10**

- 计划 11 `:37` 已写明落点 `McpRimDebug/DebugSession.Eval.cs`（Cecil 元数据输出 `isPublic`），归属 `plans\2026-10-01-10-api-member-visibility-tools.md`；`triage\10-api-member-visibility.md:99-101` 复核确认该文件 `:355-367` 的 `AddMethodResult` 确实丢掉了 `m.Attributes`/`m.IsPublic`。
- 计划 11 `:43-46` 的边界论证成立且本轮再次实测支持：`search_source` 是**行级文本匹配、没有符号表**，它带出的修饰符是**源码字面量**，不是可访问性判定（实测 `search_defs` 对 `Pawn_PathFollower` 零命中、对 `Gun_Revolver` 命中 ⇒ Defs-only，**补不了这一支**）。
- ⇒ **本条不抢计划 10 的活。** 本条只管「怎么把那一行搜出来、别搜串」。

### 7.3 建议：**并入计划 11**（不单立），锚点如下（供主代理回灌给计划 11 的作者）

| 优先级 | 锚点（计划 11 现行行号） | 改什么 |
|---|---|---|
| **P0** | 规矩 2，`:340` 之后 | 加「`case_sensitive` 默认 `false` ⇒ 存在性判定**必须**显式传 `case_sensitive: true`」+ 实测 A/B 对照 |
| **P0** | 规矩 3 第 1 步，`:379-381` | 补「裸名宽搜这一次也要带 `case_sensitive: true`」+ 实测 I 的 32 条证据 |
| P1 | 规矩 1 禁忌二四态清单，`:318-324` | 补第五态「同名不同大小写兄弟成员串味（发生在**非零命中**上）」 |
| P1 | `AI-DEPLOYMENT.md` 入口第 3 条，`:527-530` | 「配」→「**必须**配」，并补一句串味警告 |
| P2 | 任务 3 验收，`:571-687` | 新增一步跑实测 A/B/H，贴真实返回进 `:752-779` 的记录表 |

### 7.4 为什么不单立（代价论证）

- **单立的代价**：规范正文 `docs\mcp-source-lookup-conventions.md` 由计划 11 任务 1 步骤 2 **逐字整体写入**（`:186`「整体写入，不要留空壳再补」），计划 14 又要在同一份文件里**插入 `## 4.` 并重编号**（`plans\2026-10-01-14-…md:157-174`）。若 `[19]` 单立第三份计划，它要么重写正文（与 11、14 打架），要么在正文已建好后再改一次 —— **同一个文件三轮插入 + 一次重编号 + 两处交叉引用同步**，漂移风险最高。
- **并入的代价**：只需在计划 11 那份**逐字块**里加 2 段、改 2 句 —— 且**现在文件还没建**，是零冲突窗口。
- 计划 11 的 `## 5. 维护规则`（`:443-461`）本来就规定同族踩坑**追加到本文件对应小节、不要另建文件**，并要求「每条追加必须有**一次实测调用 + 它的真实返回**」—— 本报告 §3.2 的 10 次只读实测正好满足这个准入条件。
- **唯一需要主代理裁决的取舍**：并入会让计划 11 作者**改动已写好的逐字正文块**。我认为代价可接受（一次编辑 vs 一份新计划 + 三方漂移），故建议并入。

### 7.5 顺带请主代理处理的板面问题（我无权写板）

1. **索引 🟠 与正文 🔴 不一致**（见 §4），建议以 🔴 为准并统一。
2. **`:44` 的复现缺 `case_sensitive:true` 才成立**（实测 A），建议在批注里注明，否则下一个人会照抄一条**根本不会零命中**的调用。
3. **`:74` 引用的 `COMMON.md` 不存在**，`正控` 纪律全仓只出现在板自己身上（§3.3）⇒ **`:76` 的「待绕过」状态缺少依据**。
4. **`:58-60` 的「正解」返回被少贴** —— 实际是 4 条不是 2 条（实测 H）。
5. 建议在批注里补一句板上没写、但危害更大的事实：**默认大小写不敏感会让 `search_source` 用非零结果答错成员**（实测 A）。
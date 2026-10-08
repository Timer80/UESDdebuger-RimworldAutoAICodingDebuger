# `read_file` 行号基线约定（吐槽板 [14]）实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。
>
> ⚠️ **先读这一段，否则你会卡在两个地方。**
>
> **卡点一（找错文件）**：产生 `read_file` / `search_source` 的 MCP 服务器叫 **`rimworld-source-online`，源码不在本仓库**（§0.1 事实 F1，本会话用 11 个目录的穷举搜索复核）。你**不要**去找它的源码，**不要**在本仓库里改 `read_file` 的实现 —— 写「修改 `read_file` 的实现」这种步骤的人会得到一个空步骤。
>
> **卡点二（造出两份互相不知情的约定）**：**不要新建规范文件。** 本条属于同一族（`rimworld-source-online`），规范正文落在
> `docs/mcp-source-lookup-conventions.md` —— 那份文件由 **[11] 号计划**（`docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md`）
> 的任务 1 创建。本计划的任务 1 是**往里加一节**，不是另起一份。详见 §0.2。

**目标：** 把「核 vanilla 行号时 `read_file` 会给出错的行」这件事，从每个执行者各自踩一遍，变成 `docs/mcp-source-lookup-conventions.md` 里**一份可被机器复现、可被自我否证**的硬约定，并给出一条能机械执行的核证配方。

**架构：** 两层、互不重复。① **单一权威正文** —— `docs/mcp-source-lookup-conventions.md` 新增 `## 4. 行号基线` 一节；它已经是 `[11]` 号计划建好的那份文档，沿用其 `维护规则` 收编结构，不再造第二份文件。② **入口指针** —— `AI-DEPLOYMENT.md` 的 `### 8.1`（由 `[11]` 号计划任务 2 建立）指向该文档；本计划**不改**它，只在计划必带块里加一行指针，让零上下文执行者知道去哪儿翻。

**技术栈：** 无代码。Markdown，只写 `docs/issue-triage/plans/` 下一个新文件 + （由有写权限的会话）编辑 `docs/mcp-source-lookup-conventions.md`。验证用**只读**的 PowerShell（`System.IO.File::ReadAllLines` + `Select-String` + `Test-Path`）与**只读**的 MCP 工具 `rimworld-source-online.search_source` / `rimworld-source-online.read_file`。全程不编译、不启动游戏、不调用任何改变游戏状态的 MCP 工具。

---

## 0. 前置事实（已实测，不要重新摸索）

> **行号引用一律以 `System.IO.File::ReadAllLines` 为准。**
> 本会话中内置 `read` 工具返回的行号**出现过漂移**：对 `docs/mcp-吐槽板.md` 少报 27 行、对 `docs/issue-triage/AGENT-BRIEF.md` 少报 1 行且在同一文件内偏移不一致（同文件一处少 1 行、另一处又对上了）。这恰恰就是本条在讲的同类故障。**§0.5 里每一个行号都经 PowerShell 复核过。**

### 0.1 归属：改不了外部服务器

| 编号 | 事实 | 复核方式与结果 |
|---|---|---|
| F1 | `read_file` / `search_source` 由 MCP 服务器 `rimworld-source-online` 提供，**源码不在本仓库** | 对 `Source/` `MCP/` `McpRimDebug/` `runtime/` `About/` `ShaderProject/` `_poc_roslyn/` `Languages/` `UE_Data/` `Assemblies/` `AssetBundles/` 递归搜 `read_file\|start_line\|search_source\|rimworld-source-online` → **11 个目录全部 0 命中** |
| F2 | 全仓库 `start_line` 只出现在吐槽板与本流水线的报告里，**产品代码 0 处** | `grep start_line` → 仅 `docs\mcp-吐槽板.md`、`docs\issue-triage\triage\13-read-file-wrong-dir.md`、`docs\issue-triage\triage\14-read-file-start-line-zero-indexed.md` |
| F3 | 本仓库自己的 MCP 服务器是另一条链路（`rimworld-debug`，Node.js，目录 `MCP/`），不提供这两个工具 | 含在 F1 的搜索里，`MCP/` 0 命中 |

⇒ **凡是「改 `read_file` 实现」的步骤都是空步骤。**

### 0.2 与 [11] 号计划的关系（本计划的前置依赖，**先读完再动手**）

| 项 | 值（本会话实测） |
|---|---|
| `[11]` 号计划 | `docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md`，**1404 行**（`ReadAllLines` 口径；⚠️ 此前记作 565 行，**已过时** —— 该计划此后又经历修订，行数会继续漂移，**只作快照**） |
| 它要建的规范正文 | `docs/mcp-source-lookup-conventions.md` |
| 该文件此刻是否存在 | **`False`**（`Test-Path` 实测）—— 它是 `[11]` 号计划**任务 1 步骤 2** 的产物，尚未执行 |
| 它建好后的小节结构 | `## 0. 归属与边界` / `## 1. 三条硬规矩` / `## 2. 标准检索流程（三段式）` / `## 3. 上下文预算` / `## 4. 与 [10] 的边界（不要互相重复实现）` / `## 5. 维护规则` |
| 它建的 `AI-DEPLOYMENT.md` 入口 | 任务 2：`### 8.1 查 vanilla 源码：外部 MCP rimworld-source-online`，插在 `## 8. 排障决策树` 的「**通用日志入口**：」两条之后、`---` 之前 |

**⚠️ `[11]` 号计划已经写了本条的一部分。** 它的规范正文 `## 2` ③ 已经写了
「`start_line` 是 **0-indexed**（`start_line = 目标1based行号 - 1`），且返回值**不带任何行号前缀**。
⇒ **禁止从 `read_file` 的返回内容反推行号**」。

**本计划绝不重复这句话。** 本计划的**全部增量**是 `[11]` 号计划**没有实测过**的四件事，全部列在 §0.3 的规则 4/5/6/7 与 §3 的正文里。任务 1 的 `## 4.` 节首行会显式指回 `## 2` ③，声明自己只做补充。

**⚠️ 前置依赖的处置（写给执行者）**：任务 1 的步骤 1 是 `Test-Path` 检查。
- 返回 `True` → 按步骤 2 起继续。
- 返回 `False` → **停下来，报告「被 `[11]` 号计划任务 1 阻塞」，不要自己建这份文件。**
  自己建会把 `[11]` 号计划任务 1 步骤 2 的整份正文重写一遍，两份必然漂移。
  这不是失败，是正确的前置判定。

**🚨🚨 阻塞声明（写给执行者：改完这份计划 ≠ 做完了）**

> `docs/mcp-source-lookup-conventions.md` **至今不存在**，而**计划 11 与本计划互为前置，构成循环依赖**：

| 环节 | 依赖方向 |
|---|---|
| 本计划任务 1 | 要**往里加一节** `## 4.` —— 该文件不存在 ⇒ 做不了 |
| `[11]` 号计划任务 1 | 要**新建**该文件 —— 而 `[11]` 号计划正文引用了本计划的 §3/§4 交付正文 |

⇒ **因此：本轮（以及 `[49]` / `[46]` / `[52]` 三份 triage 的）所有修订，现阶段都只能改计划本身，落不了文档。**
⇒ **别把「计划已修订」当成「规范已落地」。** 本计划的完成判据在守卫解除前一律是
「**本文件 §3 里已存在这段终稿**」，**不是**「文档已改」。
⇒ 解除顺序：**先跑 `[11]` 号计划任务 1 建好文件，再跑本计划任务 1**；反过来不行（会重写正文、造出第二份漂移副本）。
⇒ **🚫 任何人不得因为「前置文件不在」就自建这份规范文档**（`§1` 落点决策整节就是为这件事写的）。

**🚨 阻塞守卫覆盖范围（本计划全部文档产出都在它之内）**：

`docs/mcp-source-lookup-conventions.md` 此刻**仍是 `Test-Path = False`**。
**搜索范围（按 `AGENT-BRIEF.md:260-266` 三件套）**：① 工具 = `Test-Path` + `Get-ChildItem -Path docs -Filter *.md`；
② 范围 = `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\`（**仅顶层**，未递归 `issue-triage/`）；
③ 权威来源本应在哪 = `[11]` 号计划任务 1 步骤 2 的产物路径。
实测结果：该目录顶层只有 3 个 `.md`（`csharp-console-compiler-upgrade-feasibility.md` / `mcp-吐槽板.md` / `shader-guide-for-ai.md`），
**无 `mcp-source-lookup-conventions.md`** ⇒ 结论「**不存在**」成立（不是「我没找到」）。
因此下列改动**全部无法落文档，现阶段只能改本计划**：

| 阻塞项 | 内容 | 落在哪 |
|---|---|---|
| **M1** | 重写「替代路径：没有」→ 替代路径**现在存在**（`read_csharp_symbol` 的同服行号头） | §3 的 `### 4.2.1` |
| **M2** | 加方向限制：格式可复用，**行号计算不可复用** | §3 的 `### 4.2.2` |
| **M3** | 加 A/B 对拍规则（不依赖锚点的自证手段） | §3 的 `### 4.1` 规则 5 + §4 指针块 |
| **M4** | 加「顺序错了会更危险」警告与三步正确次序 | §3 的 `### 4.2.3` |
| **M5** | 边界声明措辞改为「格式可复用外部已有实现，行号计算需在外部仓库内重做」 | §7 边界声明 |
| 附 | 规则 6（三套契约不一致）与 🔴 升级触发 | §3 的 `### 4.1` 规则 6 + §4 指针块 |
| **M6** | 「行数不减」→ **丢行时返回 `N−1` 行、页脚仍报 `N`**；`Δ ∈ {0,+1}`、`+2` 不可能 | §0.3 结论 2/4 + §3 的 `### 4.1` 规则 1 |
| **M7** | 恒真的右移判据 → **`tip ≠ 首行真实行号 + 返回行数 − 1`** | §0.3 结论 5 + §3 的 `### 4.1` 规则 4 |
| **M8** | A/B 规则后半句改成比**整块**（丢行时首行本就相同） | §3 的 `### 4.1` 规则 5 |
| **M9** | 并入板 `[46]`（首行对应第几行无法自证）+ `[49]` 的闭式模型 | §0.3 + §3 的 `### 4.1` 规则 5 |
| **R1** | **「更正」与「首次给出」适用同一套核证义务**（记录模板加两栏） | §3 的 `### 4.4` |
| **R2** | **落笔的行号必须有第二个不依赖被纠正对象的锚点**（首选零计数 dump） | §3 的 `### 4.4` + §3 的 `### 4.6` 取证纪律 |

**M1–M9、R1–R2 全部是 `## 4.` 一节的内容，而该节由任务 1 插入** ⇒ 守卫覆盖完整。
`[11]` 号计划落地前，本计划对这些改动的完成判据就是「**本文件 §3 里已存在这段终稿**」，
不是「文档已改」。

**⚠️ 用 decompilerserver 时的环境陷阱（实测）**：`list_contexts` 可能返回
`"currentContextAlias":"gab"`（`Lib.GAB.dll`）—— **共享上下文是脏的**。
要查 vanilla 就**每次显式传 `contextAlias:"vanilla"`**（`game` / `default` / `rw16` 同 MVID），
**绝不 `select_context` 去抢 current**，否则会污染并行会话。
本计划 §0.3 的探针全部走 `rimworld-source-online`，与此无关；但 §4.2 提到
`get_source_slice` 时若要实测，务必带上这个 `contextAlias`。

### 0.3 工具行为（11 次受控只读探针，全部本会话亲测）

> §0.3 只列 `read_file` / `search_source` 行为（A–F）。同一批会话里另外 5 次
> `search_source` / `read_file` / `read_csharp_symbol` 调用（G–K）见 §3 的 `### 4.5`。


| 探针 | 调用 | 实测返回首行 / 结果 | 该首行的真实 1-based 行号 |
|---|---|---|---|
| **A** | `search_source{ query:"GetBlitMeshUpdatedFrame", file_pattern:"Source/Verse/PawnRenderer.cs" }` | 两行：`164:` 与 `243:` | **164 / 243（1-based）** |
| **B** | `search_source{ query:"^\tprivate Mesh GetBlitMeshUpdatedFrame\(", regex:true, file_pattern:"Source/Verse/PawnRenderer.cs" }` | **仅 1 行**：`164:	private Mesh GetBlitMeshUpdatedFrame(…)` | **164** |
| **C** | `read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:163, line_count:3 }` | `	private Mesh GetBlitMeshUpdatedFrame(…)` | **164** |
| **D** | `read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:162, line_count:3 }` | `	private Mesh GetBlitMeshUpdatedFrame(…)` —— **与 C 逐字相同** | **164**（纯 0-indexed 应为 163） |
| **E** | `read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:158, line_count:10 }` | `	public void SetStatuePaintColor(Color? color)` | **159** |
| **F** | `read_file{ path:"Source/Verse/GizmoGridDrawer.cs", start_line:133, line_count:2 }` | `			Log.ErrorOnce($"{arg} currentSelectable: {obj.ToStringSafe()}", 3427734);` | **134** |

**由 A–K 共 11 次探针共同确定、无一例外：**

1. **`search_source` 的行号是 1-based**，是全仓库**唯一的行号真值来源**（探针 A/B）。
2. **`read_file` 的 `start_line` 是 0-indexed**：`start_line = 目标 1-based 行号 − 1`。
   ⚠️ **但这是条件式，不是恒等式** —— 必须分两支读（依据：`triage/49-…` §4 闭式模型 + 本计划 §0.7 探针 V–Y 独立复核）：

   | 块首行 `start_line + 1` | 实际返回的首行 | `Δ = 首行真实 − (start_line + 1)` | 返回行数 | 页脚 |
   |---|---|---|---|---|
   | **非空** | `start_line + 1` | **0** | `N` | `Showing N/M` **相符** |
   | **为空行** | `start_line + 2`（该空行被吞） | **+1** | **`N − 1`** | **仍印 `Showing N/M`，虚高 1** |

   ⇒ **`Δ ∈ {0, +1}`；`+2` 在机制上不可能**（最多只丢块首那**一个**空行，窗口内部的空行不吞）。
   ⇒ 🚨 **凡把 `start_line + 1 = 首行真实行号` 当作无条件恒等式使用的推理，一律作废。**
   （探针 C/E/F 全部落在「块首行非空」那一支，所以它们当年看起来吻合 —— 那是**样本偏**，不是恒等式成立。）
3. **`read_file` 的返回不带任何行号前缀**（8 次 `read_file` 调用全部如此：C/D/E/F/H/I 及更早的同类调用）。
4. **⚠️ 仅当块首行为空行时，该行被静默丢弃、窗口整体右移 1 行；此时返回 `N − 1` 行，而页脚仍打印 `Showing N/M`。**
   **页脚与正文不符，本身就是第二个可利用的检测信号**（页脚虚高 1）。
   探针 D 的窗口首行（1-based 163）正是空行 —— 探针 E 直接证明 163 是空行、164 是 `GetBlitMeshUpdatedFrame`。纯 0-indexed 下探针 D 首行应是空行，实测却是 164。**C 与 D 的 `start_line` 相差 1、返回内容逐字相同**，这就是丢行的铁证。
   **窗口内部的空行不会被吞**：探针 E 返回 10 行，第 5 行是空行（= 1-based 163）、第 6 行是 164，与探针 A 完全吻合。

   🚨 **本条的三条更正（依据 `triage/49-…` §4/§6 缺陷 1，本计划 §0.7 探针 V/W 独立复核，两个文件各一次 `line_count=1` 单行探针零计数复现）**：

   | 原写法 | ❌ 为什么错 | ✅ 正确写法 |
   |---|---|---|
   | 「返回行数**不减**」 | 丢行时**确实少返回 1 行**。`line_count=1` + 块首为空 ⇒ 正文**一行都没有**，页脚却写 `Showing 1/285` | 「丢行时返回 `N − 1` 行，**页脚仍报 `N`**」 |
   | 「窗口右移 1 行」（暗示右移量任意） | 右移量被机制锁死为 **1** | 「`Δ ∈ {0, +1}`，**`+2` 不可能**」 |
   | 把「Δ 不是常数」当作工具不可预测 | Δ **完全可预测**，判据是块首行是否空行 | 「Δ 由「块首行是否为空」**唯一决定** ⇒ 可预测、可复现、可检测」 |

   ⚠️ **板上「Δ 出现过 0 / +1 / +2」已否证**：那 4 行读数里的 `search_source` 列**全部属实**，
   第 3 行的 `+2` 是**手数少一位**（末行定位法实测该符号在块内第 **24** 位 ⇒ 正确 Δ=+1）。
   ⇒ **「两份 vanilla 快照」的假设同样被否证** —— 每次 `read_file` 都与 `search_source` 行号逐行吻合。
5. **⚠️ 窗口右移时，续读 tip 比真实下一行少 1。** 实测 tip 恒等于 `start_line + line_count`：
   - 探针 C（未右移）：`tip = 166` → 1-based 167 = 真实下一行，**正确**；
   - 探针 D（右移 1 行）：`tip = 165` → 1-based 166，**而 166 已经在返回里** ⇒ 照 tip 续读会**重复读最后一行**。
   ⇒ 🚨 **判定是否发生右移的正确式：`tip ≠ 首行真实行号 + 返回行数 − 1` 即为右移。**

    ⚠️ **原写法 `tip ≠ 首行真实行号 + 返回行数` 是一条恒真断言 —— 判别力 = 0（已删除，勿复活）。**
    推导：`tip = start_line + line_count = (首行真实行号 − 1) + 返回行数`，
    所以 `tip` **恒等于** `首行真实行号 + 返回行数 − 1`，**永远不等于** `首行真实行号 + 返回行数`。
    ⇒ 那条式子**无论右移与否都为真**，**绿着但什么也挡不住** —— 这正是 `AGENT-BRIEF.md:225`
    「恒真断言伪装成判据：在/不在殊途同归返同一值，判别力=0 但它**绿着**」。**这是装饰，不是判据。**
    **实测反例**：`PathGrid.cs, start_line=30, line_count=9`，首行真值 31、返回 9 行、tip 39。
    `39 ≠ 31 + 9 = 40` ⇒ 旧式子判「右移了」，**但该窗口首行 31 非空、根本没丢行**。
6. **`search_source` 支持 `regex: true`，声明行可正则精确定位。** 探针 A 裸名返回 2 条、探针 B 加 `^\t` 锚定后返回 **1 条** —— 这是 `[11]` 号计划已确立的正确姿势，本计划直接复用，不另想检索方案。
7. **`get_source_slice{ includeLineNumbers:true }` 不能替代**（裁决报告 §3.5 实测）：它返回**反编译产物**的相对行号，与原文件行号不可互换。其「原文 449 行 vs 反编译 84 行」中的 **449 本会话已复核**（探针 F 的 `Showing 2/449`）；84 那一侧引自裁决报告，本会话未二次复核 —— 本计划不依赖该数字做任何决策。
8. **⚠️ 同服的 `read_csharp_symbol` 却自带行号头 `// File: <路径> (Lines N-M)`**，对**方法**与**整个类型**都发（探针 J/K）。⇒ 同一个服务、同一个 `Source/` 索引里，**行号格式化能力已经存在**，只是没接到 `read_file` 上。
   ⇒ **同服三套行号契约互不一致，这是设计缺陷不是缺功能**：`search_source` 1-based 无头 / `read_file` 0-indexed 无头且吞行 / `read_csharp_symbol` 1-based 有头。
   ⇒ **🚨 升级触发：任何未经 `search_source` 交叉核对的行号，该单个产物一律按 🔴 对待** —— 行号错了不会有任何报错。

### 0.4 已验证锚点表（照抄即可直接核对，无需重新推导）

| `文件:行号`（**1-based**） | 该行内容 | 复核探针 |
|---|---|---|
| `Source/Verse/PawnRenderer.cs:159` | `	public void SetStatuePaintColor(Color? color)` | E |
| `Source/Verse/PawnRenderer.cs:160` | `	{` | E |
| `Source/Verse/PawnRenderer.cs:161` | `		statuePaintColor = color;` | E |
| `Source/Verse/PawnRenderer.cs:162` | `	}` | E |
| `Source/Verse/PawnRenderer.cs:163` | **（空行）** | E |
| `Source/Verse/PawnRenderer.cs:164` | `	private Mesh GetBlitMeshUpdatedFrame(PawnTextureAtlasFrameSet frameSet, Rot4 rotation, PawnDrawMode drawMode)` | A + B + C + E 四重吻合 |
| `Source/Verse/PawnRenderer.cs:243` | `					GenDraw.DrawMeshNowOrLater(GetBlitMeshUpdatedFrame(frameSet, facing, drawMode), bodyPos, Quaternion.AngleAxis(bodyAngle, Vector3.up), mat, drawNow: false);` | A |
| `Source/Verse/GizmoGridDrawer.cs:134` | `			Log.ErrorOnce($"{arg} currentSelectable: {obj.ToStringSafe()}", 3427734);` | F |
| `Source/Verse/AI/Pathing.cs:34` | `	private readonly PathingContext flying;` | H（`start_line=33` 未右移） |
| `Source/Verse/AI/Pathing.cs:35` | **（空行）** | H/I（被吞，故无一行窗口以它为首行） |
| `Source/Verse/AI/Pathing.cs:36` | **（空行）** | H（`start_line=35` 指向它 ⇒ 右移到 37） |
| `Source/Verse/AI/Pathing.cs:37` | `	public PathingContext Normal => normal;` | H + I 两组首行都是它 |
| `Source/Verse/AI/Pathing.cs:39` | `	public PathingContext FenceBlocked => fenceBlocked;` | **G（`search_source`）+ I（第 3 行）双重互证** |
| `Verse/Pawn.cs:5348-5359` | `public PathingContext GetPathContext(Pathing pathing)` 起 12 行 | J（`read_csharp_symbol` 的 `Lines 5348-5359`，正文行数逐行吻合） |
| `Source/Verse/AI/Pathing.cs:7-171` | `public class Pathing : IDisposable` 起整个类 | K（证明行号头不是对方法硬编码） |
| `Source/Verse/PawnRenderer.cs` 总行数 | **714** | C/D/E 的 `Showing N/714` |
| `Source/Verse/GizmoGridDrawer.cs` 总行数 | **449** | F 的 `Showing 2/449` |
| `Source/Verse/AI/Pathing.cs` 总行数 | **172** | H/I 的 `Showing 3/172` |

### 0.5 本仓库文档锚点（全部经 `ReadAllLines` 复核）

> **吐槽板正在被主代理持续追加批注，行号持续漂移。** 本会话实测：条目 `[11]` 的标题行从 215 → 242、文件总行数 504 → 537（`[11]` 号计划 §0.5 已记录同一现象），
> 本会话又测到 `docs/mcp-吐槽板.md` 总行数 505 → 555 → 569、条目 `[14]` 标题行 88 → 115 → **129**。
> ⇒ **定位吐槽板一律用「`## [NN]` 标题文本」作锚点，行号只作当时快照。** 下表快照时刻：`LastWriteTime = 2026-10-01 18:27:31`。

| 引用 | 值 |
|---|---|
| `docs/mcp-吐槽板.md` 快照总行数 | **569**（纯 LF） |
| 吐槽板 `[14]` 标题（**定位锚点**） | `## [14] \`read_file\` 的 \`start_line\` 是 0-indexed —— 核精确行号时每条都差 1 行 🟠` —— 快照在**第 129 行** |
| 板上 `read_file{ … start_line:162, line_count:22 }` | 快照第 **134** 行 |
| 板上「返回的第一行是**空行**」 | 快照第 **135** 行 |
| 板上「`没有行号`，得自己数空行」 | 快照第 **142** 行 |
| `docs/mcp-source-lookup-conventions.md` | **尚不存在**，由 `[11]` 号计划任务 1 创建 |
| `AI-DEPLOYMENT.md` 总行数 | **190**；`## 8. 排障决策树（AI 遇错优先自查）` 在 **164**；表体 166–177；`**通用日志入口**：` 在 **179**，两条 bullet 在 180–181；`---` 在 **183**；`## 9. 一句话提醒` 在 **185**；全文 `rimworld-source-online` 命中 **0** 次 |
| `docs/issue-triage/AGENT-BRIEF.md` | 117 行；§1 禁改标题 **23**、第 1 条 **28**、禁改 `docs/*.md` **32**、禁碰吐槽板 **33** |
| `docs/issue-triage/triage/13-read-file-wrong-dir.md` | 108 行；其 `read_file` 探针 **18–22**；其 0-indexed 结论 **23–24** |
| `docs/issue-triage/triage/14-read-file-start-line-zero-indexed.md` | 130 行（上游裁决报告） |
| `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md` | 2129 行；头部模板 **1–11**；`## 0. 前置事实（已实测，不要重新摸索）` 在 **13** |

### 0.6 上游裁决报告需要更正的 6 处（已逐条复核）

执行者不必回读裁决报告，直接用本节。

| # | 裁决报告的说法 | 复核结论 |
|---|---|---|
| 1 | §3.1 吐槽板 `[14]` 在 **79–108** 行 | **错**。锚点见 §0.5；该条目在编写期间已被主代理多次下推（88 → 115 → 129） |
| 2 | §3.2 全仓库 `start_line` 命中「吐槽板第 31/79/84/90/106/142 行」 | 命中数 **6 处正确**，但**行号 5 个错**：现为 **31 / 129 / 134 / 140 / 155 / 191**（31 与 129 已复核；后四个随板漂移，用标题锚点定位） |
| 3 | §7 「`docs/issue-triage/plans/` 目录当前**不存在**」 | **已过时**，该目录**现已存在**，已有 4 份计划 |
| 4 | §3.3/§3.4「返回切片会丢掉开头的空行」 | **方向对、规则写宽了**：不是「丢掉所有开头空行」，而是**仅当空行落在窗口首行时丢弃**；窗口内部空行完好（探针 E 直接证明） |
| 5 | （报告未提） | **报告漏了一条实测发现**：窗口右移时**续读 tip 比真实下一行少 1**，照 tip 续读会重复最后一行（探针 C vs D 实测） |
| 6 | §3.4「板上『第一行是空行』未复现」 | **正确**，本会话探针 D 独立复现该结论 |

### 0.7 本次修订的独立复核（探针 V–Y，2026-10-03，零计数）

> 这一节是**为让上面的修订可被否证**而存在的：任何人 30 秒内重跑 V–Y 就能推翻它们。
> 全部只读、全部走 `rimworld-source-online`，**未调用 decompilerserver**（本条完全不需要反编译）。
> 手法：**单行探针 `line_count=1`** —— 只返回一行，**没有可数的余地**，从根上消掉数行误差（`triage/49-…` §3.2）。

| # | 调用 | 真实返回 | 判读 |
|---|---|---|---|
| **V** | `read_file{ path:"Source/Verse/PathFinderMapData.cs", start_line:40, line_count:1 }` | 正文**一行都没有**（只有分隔空行）；`[TRUNCATED] Showing 1/317 lines.`；`(Tip: … start_line: 41)` | **决定性**：要 1 行、页脚报 1，**实际返回 0 行** ⇒ **丢行时返回 `N−1` 行、页脚仍报 `N`**。「行数不减」被证伪 |
| **W** | `read_file{ path:"Source/Verse/PathFinderMapData.cs", start_line:41, line_count:1 }` | `	private readonly List<IPathFinderDataSource> sources;`；`Showing 1/317`；`(Tip: … start_line: 42)` | 块首行非空 ⇒ 原样返回 1 行、页脚相符 ⇒ **`Δ = 0` 那一支** |
| **X** | `read_file{ path:"Source/Verse/AI/PathGrid.cs", start_line:100, line_count:1 }` | 正文**一行都没有**；`[TRUNCATED] Showing 1/285 lines.`；`(Tip: … start_line: 101)` | **第二个文件独立复现 V** ⇒ 不是单文件偶然 |
| **Y** | `search_source{ query:"private void NotifyCellDirtied", file_pattern:"Source/Verse/AI/PathGrid.cs" }` | `102:	private void NotifyCellDirtied(IntVec3 cell)` | X 的意图首行 = `start_line+1` = **101**，真值却落在 **102** ⇒ 101 是空行且被吞 ⇒ **`Δ = +1`**，与 X 的 0 行返回自洽 |

**⇒ V/W/X/Y 共同确认的闭式模型（与 `triage/49-…` §4 一致，本次为独立第二来源）：**

> `read_file` 取 `[S+1, S+N]`；**仅当第 `S+1` 行是空行时该行被丢弃，返回 `N−1` 行、而页脚仍报 `N`**。
> ⇒ **`Δ ∈ {0, +1}`；`+2` 在机制上不可能。**

**⚠️ 顺带证伪 `triage/46-…` §7 的一处读数（不影响其结论）**：该报告称
「`start_line=75, line_count=3` 指定 3 行、返回 3 行，其中 1-based 75 的空行被丢、实际内容是 76/77/78」——
按 0-indexed，窗口首行本应是 `start_line+1` = **76**，**76 非空 ⇒ 那次根本没有丢行、返回 3 行是对的（`Δ=0`）**；
它把「首行 − start_line = 1」当成了丢行迹象，而那个差值**正是 0-indexed 的定义**。
⇒ **`[46]` 的主结论（与 `[49]` 同机制、需 A/B 对拍自证）不受影响；受影响的只有这处「行数不减」的举例。**

---

## 1. 落点决策：为什么进 `docs/mcp-source-lookup-conventions.md` 而不是另建文件

| 候选 | 结论 | 理由 |
|---|---|---|
| **新建 `docs/read-file-line-number.md`** | ❌ | 行号基线与检索规范是**同一套工具的两个面**，且**行号约定依赖检索约定**（「真值来源 = `search_source`」这句话只有在检索规范存在时才有意义）。拆成两份，两份都会腐坏 —— 这正是吐槽板 [16]「能读不能搜」那类问题的文档版 |
| 仓库根 `AGENTS.md`（新建） | ❌ | 根目录本来就没有 `AGENTS.md`；本机 harness 自动加载的是用户级 `~/.dsh/AGENTS.md`。新建一个「写了没人读」的文件，正是这批条目在讲的病 |
| `docs/issue-triage/AGENT-BRIEF.md` | ❌ | `AGENT-BRIEF.md:28` 明令子代理不得改动既有文件；且那里放的是「问题记录」，不是「长期规范」。把规范塞回流水线自己的目录，会让规范随流水线一起腐坏 |
| 根 `README.md` | ❌ | 人类 / 工坊发布页，使用者是 AI |
| `AI-DEPLOYMENT.md` 正文 | ❌ | 全文 `rimworld-source-online` 命中 **0** 次，通篇只讲本模组部署的那套工具链；把外部服务的完整规范塞进去会撑歪主题 |
| **`docs/mcp-source-lookup-conventions.md` 新增一节** | ✅ **采纳** | 它已被 `[11]` 号计划定为「该服务的**唯一**规范文档」，其 `维护规则` 明文写着同族踩坑「**追加到本文件对应小节**，**不要另建文件**」。进它 = 遵守既有约定；另建 = 造出第二份互相不知情的约定 |

**与 `[13]` 的边界**：`[13]`（命名空间 ≠ 目录）已判**不值得修**，只保留一条「引用 vanilla 行号必须带目录」的约定 —— 该约定已由 `[11]` 号计划落在同一份文档的 `规矩 2`（`先不带 file_pattern 宽搜拿真实路径`）。**本计划不碰它、不重复它**，只在 §3 的正文里指向它。

---

## 2. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `docs/issue-triage/plans/2026-10-01-14-read-file-line-number-convention.md` | **本文件（已创建）** | 本计划；`## 4.` 节正文的逐字来源；两个任务的全部交付物 |
| `docs/mcp-source-lookup-conventions.md` | 由 `[11]` 号计划创建后，**本计划任务 1** 追加 `## 4.` 一节并重编号 | **唯一权威正文**。行号基线的四条增量规则、可验证锚点表、核证记录格式 |
| `docs/issue-triage/plans/<此后每一份>.md` | 任务 2：必带**指针块** | 让零上下文执行者知道去规范文档翻；**不复制全文**，避免两份漂移 |

### 2.1 插入后的重编号表（任务 1 步骤 2 逐字执行）

| 插入前 | 插入后 |
|---|---|
| `## 0. 归属与边界（先搞清楚，否则会用错工具、也会去改错地方）` | 不变 |
| `## 1. 三条硬规矩` | 不变 |
| `## 2. 标准检索流程（三段式）` | 不变 |
| `## 3. 上下文预算` | 不变 |
| — | **`## 4. 行号基线：`start_line` 是 0-indexed，且返回不带行号`（新增，正文见 §3）** |
| `## 4. 与 [10] 的边界（不要互相重复实现）` | **`## 5. 与 [10] 的边界（不要互相重复实现）`** |
| `## 5. 维护规则` | **`## 6. 维护规则`** |

**必须同步改的交叉引用恰好 2 处**（`[11]` 号计划建出的正文里，`§0` / `§1` / `§3` 的引用不受影响）：

| 原文 | 改成 |
|---|---|
| `\| 本文件不负责什么 \| **成员可见性的权威查询不归本文件**，见 §4。 \|` | `…，见 §5。 \|` |
| `…、[14]（§2 ③ 里 \`start_line\` 0-indexed）。` | `…、[14]（本文件 §2 ③ 讲基线；**§4** 讲返回不带行号、空行吞行与 tip 错位）。` |

---

## 3. 交付正文：`## 4.` 一节（**逐字终稿，不需要你润色**）

插入位置：`## 3. 上下文预算` 一节结束之后、原 `## 4. 与 [10] 的边界` 之前。

````markdown
## 4. 行号基线：`start_line` 是 0-indexed，且返回不带行号

> 本节只做 `## 2` ③ 的**补充**，不重复它。`## 2` ③ 已经写了「`start_line` 是 0-indexed」
> 和「返回值不带行号前缀」这两条基线。本节补的是那两条**没有覆盖到的后果**。

### 4.1 六条规则（全部实测，见本节末尾的调用与真实返回）

1. **⚠️ 仅当块首行（`start_line + 1`）是空行时，它被静默丢弃、窗口整体右移 1 行；
   此时返回的是 `N − 1` 行，而页脚仍打印 `Showing N/M`（页脚虚高 1）。**
   —— 你要 3 行（`line_count=3`），拿到的是 **164/165 两行**，不是三行。
   **页脚说 3、正文只有 2 行，这个不符本身就是检测信号之一。**
   ⚠️ **块首行非空时**则原样返回 `N` 行，页脚与正文相符，**`Δ = 0`**。
   ⇒ 合起来就是闭式模型：**`Δ ∈ {0, +1}`，`+2` 不可能**（详见 §0.3 结论 2/4 与 §0.7 探针 V/W）。
2. **窗口内部的空行不会被丢弃。** 只有恰好落在**块首行**的那一个会。
3. **窗口右移时，续读 tip 比真实下一行少 1。** 实测 tip 恒等于 `start_line + line_count`；
   窗口右移时这个数仍按「没右移」算，于是照 tip 续读会**重复读最后一行**。
4. **🚨 判定是否右移的机械判据（已修正）**：`tip ≠ 首行真实行号 + 返回行数 − 1` ⇒ 发生了右移，
   该返回块只能当下文看，**不得用于计数**。

   > **原式 `tip ≠ 首行真实行号 + 返回行数` 是恒真断言，判别力 = 0，已删除。**
   > 因为 `tip = start_line + line_count = (首行真实行号 − 1) + 返回行数` **恒成立**，
   > 于是 `tip` 永远比 `首行真实行号 + 返回行数` 少 1 —— **右移与否都是真的**。
   > （`AGENT-BRIEF.md:225`：「恒真断言伪装成判据 …… 判别力=0 但它**绿着**」。**装饰，不是判据。**）
   >
   > **正确式的校准**（用 `triage/49-…` §6 缺陷 2 的实测数据）：
   > `PathGrid@30/count=9` ⇒ 预期 `31+9−1 = 39` = 实测 tip **39** ⇒ **不丢行** ✅
   > `PathFinderMapData@40/count=2` ⇒ 预期 `42+2−1 = 43` ≠ 实测 tip **42** ⇒ **丢行** ✅
5. **🚨 `read_file` 返回的首行到底对应第几行，无法从返回值自证（板 `[46]`，`triage/46-…`）。**
   **A/B 对拍是唯一不依赖外部锚点的自证手段**：
   对同一个 `path`、同一个 `line_count` 连调两次：`[start_line=N]` 与 `[start_line=N+1]`。
   - 两次返回**整块逐字相同** ⇒ 1-based 第 `N+1` 行是空行且被吞，**窗口右移了（`[start_line=N]` 那组）**；
   - 两次返回**整块不同** ⇒ **未右移**，`[start_line=N+1]` 那组是准的。

   > ⚠️ **必须比「整块」，不能只比「首行」** —— 原后半句「两次返回**首行不同** ⇒ 没有右移」**不成立**：
   > **丢行时首行本来就相同**（块首空行被吞 ⇒ 两组都从 `N+2` 起），
   > 所以「首行相同」**既可能是右移、也可能是两块整体错位**，**首行这一列根本没有判别力**。
   > **实测反例**（`PathGrid.cs`，同样 `line_count=2`）：
   > `[start_line=100]` → `NotifyCellDirtied` + **空行**（**丢行**）
   > `[start_line=101]` → `NotifyCellDirtied` + `{`（不丢行）
   > ⇒ **首行相同、块不同**。按旧后半句会被判成「没有右移」，**而 `[start_line=100]` 恰恰丢了行。**
   > （前半句「整块逐字相同 ⇒ 右移」经实测**仍然成立**，保留。）

   实测（`Source/Verse/AI/Pathing.cs`，`search_source` 独立确认 `39` = `public PathingContext FenceBlocked => fenceBlocked;`）：

   | 调用 | 返回首行 | 该首行真实 1-based 行号 | tip |
   |---|---|---|---|
   | `[start_line=35, line_count=3]` | `	public PathingContext Normal => normal;` | **37**（应为 36，36 是空行 ⇒ 右移） | `38`（少 1） |
   | `[start_line=36, line_count=3]` | `	public PathingContext Normal => normal;` | **37**（准） | `39`（正确接续） |

   代价只是**多一次调用**。这是本条唯一一条「不靠外部锚点也能自我否证」的手段。
   📌 **板 `[46]` 与 `[49]` 是同一条机制的两份样本，结论完全一致、无冲突**（`triage/46-…` §4 对照表），
   ⇒ **本规则 5 就是 `[46]` 的归并落点，不另立计划、不另写一份规范。**
6. **同服三套行号契约互不一致 —— 这是设计缺陷，不是「缺功能」。**

   | 工具 | 行号基线 | 返回带行号头 | 吞行 |
   |---|---|---|---|
   | `search_source` | **1-based** | ❌ | — |
   | `read_file` | **0-indexed（条件式，见 §0.3 结论 2）** | ❌ | **会 —— 仅块首行；`Δ ∈ {0,+1}`，丢行时返回 `N−1` 行而页脚仍报 `N`** |
   | `read_csharp_symbol` | **1-based** | **✅ `// File: <路径> (Lines N-M)`** | — |

⇒ **综合后果：`read_file` 的返回值无法反推起点行号。** 既没有行号前缀，
首行空行又会被吞，连「数空行」这条退路都被堵死。
**准确的说法是「单次 `read_file` 调用无法自校验」—— 不是「任何判断都是猜」**：
规则 5 的 A/B 对拍不需要任何预先知道的锚点，多花一次调用即可把右移当场测出来。
**唯一行号真值来源仍然是 `search_source`**（`## 1` 规矩 2 / `## 2` ②）。

🚨 **升级触发（严于原有约定）**：本服务的三套契约不一致，意味着**一个行号错了不会有任何报错**。
因此：**任何未经 `search_source` 交叉核对的行号，该单个产物一律按 🔴 对待** ——
不得据此下「注释里的行号对不上」之类的结论，也不得写进源码、注释、计划或报告。

### 4.2 替代路径：现在存在一个，但只能当「格式化器」用

#### 4.2.1 同服已经有行号头 —— `read_csharp_symbol`

`read_csharp_symbol` 的返回**自带行号头**，且对**方法**和**整个类型**都发（实测 2 次）：

```
read_csharp_symbol{ typeName:"Pawn", memberName:"GetPathContext" }
→ // File: Verse/Pawn.cs (Lines 5348-5359)
   public PathingContext GetPathContext(Pathing pathing)
   …（正文 12 行，与 5359-5348+1=12 逐行吻合）

read_csharp_symbol{ typeName:"Pathing" }
→ // File: Verse/AI/Pathing.cs (Lines 7-171)
   public class Pathing : IDisposable
   …（整个类，165 行）
```

⇒ **同服对照非常刺眼**：`read_csharp_symbol` 有行号头，`read_file` 一个行号都没有 ——
而**两个工具读的是同一个服务、同一个 `Source/` 索引**。
这条头证明**行号格式化能力在本服务里已经存在**，不是做不到，是没接到 `read_file` 上。

#### 4.2.2 方向限制：格式可复用，**行号计算不可复用**

这两件事看起来像一件事，其实是两件，**别混**：

| | 符号 → 区间 | 窗口 → 区间 |
|---|---|---|
| 谁在做 | `read_csharp_symbol` | `read_file`（要补的） |
| 起点从哪来 | 符号表里的声明行，**直接就是 1-based 真实行号** | 用户的 `start_line`（**0-indexed**） |
| 要额外处理什么 | 无 | ① `start_line + 1` 的 0/1 换算；② **§4.1 规则 1 的空行吞行右移**；③ 规则 3 的 tip 补偿 |
| 能不能直接搬 | — | ❌ **不能** |

⇒ **能复用的是「`// File: <路径> (Lines N-M)`」这个格式化模板**；
**行号必须重新算一遍**，而且必须把上面 ①②③ 一起算进去。
只搬模板不搬算法，得到的是一个**稳定错 1 行**的头 —— 见 §4.2.3。

`get_source_slice{ includeLineNumbers: true }` 仍然**不可替代**：
它给的是**反编译产物**的相对行号（`Source/Verse/GizmoGridDrawer.cs` 原文 **449** 行，
其 `DrawGizmoGridFor` 反编译后仅 **84** 行），与原文件行号不可互换。

#### 4.2.3 ⚠️ 加行号头的顺序错了，**比现在没有行号更危险**

> **先修空行吞行，再加行号头 —— 顺序反了会造出更糟的东西。**

照着「给 `read_file` 加个 `// File: … (Lines N-M)` 头」的建议直接改、而不先处理 §4.1 规则 1：

| 步骤 | 做法 | 后果 |
|---|---|---|
| ❌ 错序 | 直接加头，行号从 `start_line` **正算**：`[start_line=162] → Lines 163-165` | 实际返回的是 **164 起的行**，头却印 `163-165` ⇒ **整段稳定错 1 行** |
| ✅ 正序 1 | **先修空行吞行**（首行是空行时不要丢，改为保留或明确报错） | 右移消失，`start_line` 与返回内容重新对齐 |
| ✅ 正序 2 | 头里的 `N-M` **从「实际返回的切片」反算**，**不是**从 `start_line` 正算 | 头与内容永远一致 |
| ✅ 正序 3 | 再做 opt-in 的 `include_line_numbers`（**沿用同服 `get_source_slice` 已有的参数名**，别另造一个） | 行为可预测、可对照 |

🚨 **为什么「错序」比「现在没有行号」更危险**：现在的返回至少**看起来朴素**，
读的人知道它不带行号、会去 `search_source` 核对。而一个**看起来权威**的头
（带路径、带区间、格式与同服 `read_csharp_symbol` 一模一样）会让人**直接采信**，
于是 off-by-one 从「需要交叉核对才能发现」变成「**没人会再去核对**」——
**把可检出的假阴性，升级成不可检出的假成功。**

⇒ **本仓库 0 行可改**（§0.1 F1/F2：`start_line` 在本仓库代码里 0 命中）。
上面三步全部要在**外部仓库 `rimworld-source-online`** 内完成。
**本计划只负责把这条顺序要求写下来，避免下一个动手的人踩这个序。**

### 4.3 可验证锚点（照抄即可复核，无需重新推导）

| `文件:行号`（**1-based**） | 该行内容 |
|---|---|
| `Source/Verse/PawnRenderer.cs:159` | `	public void SetStatuePaintColor(Color? color)` |
| `Source/Verse/PawnRenderer.cs:161` | `		statuePaintColor = color;` |
| `Source/Verse/PawnRenderer.cs:162` | `	}` |
| `Source/Verse/PawnRenderer.cs:163` | **（空行）** |
| `Source/Verse/PawnRenderer.cs:164` | `	private Mesh GetBlitMeshUpdatedFrame(PawnTextureAtlasFrameSet frameSet, Rot4 rotation, PawnDrawMode drawMode)` |
| `Source/Verse/PawnRenderer.cs:243` | `					GenDraw.DrawMeshNowOrLater(GetBlitMeshUpdatedFrame(frameSet, facing, drawMode), bodyPos, Quaternion.AngleAxis(bodyAngle, Vector3.up), mat, drawNow: false);` |
| `Source/Verse/GizmoGridDrawer.cs:134` | `			Log.ErrorOnce($"{arg} currentSelectable: {obj.ToStringSafe()}", 3427734);` |

`Source/Verse/PawnRenderer.cs` 共 **714** 行；`Source/Verse/GizmoGridDrawer.cs` 共 **449** 行。

### 4.4 核证记录格式（每次核 `:N` 行号都要贴）

**两条硬要求（来自板 `[52]`，`triage/52-…` §6.2）：**

- **R1 —— 「更正」与「首次给出」适用同一套核证义务。**
  核证记录必须写明：本次值是**首次给出**还是**更正他人**；
  若是后者，**额外**要指出**被更正值是什么、来自哪里、当时带没带标记**。
- **R2 —— 任何要落进正式结论 / 派工单 / 注释的行号，必须有第二个不依赖被纠正对象的锚点。**
  ⚠️ **「用 `read_file` 手工数块得一个数，再用 `read_file` 手工数块得另一个数」不算两个锚点** ——
  它们共享同一个偏差源（`Δ = +1` 的丢行 + 末尾恒定分隔空行）。
  **首选第二锚点是零计数取法**（§4.6）：`search_source{ query:".", file_pattern:"<文件>" }` 直接吐整份带行号 dump。

```markdown
### 核证记录：<被核证对象>

| 字段 | 值 |
|---|---|
| 声明的行号 | `Source/Verse/PawnRenderer.cs:164` |
| 来源动作（R1） | **首次给出** / **更正他人**（二选一，不许留空） |
| 被更正值及其标记状态（R1） | 首次给出 ⇒ 不适用；更正他人 ⇒ 写明旧值 / 出处 / **旧值当时是否带 ⚠️ 标记** |
| 真值来源 | `search_source{ query:"^\tprivate Mesh GetBlitMeshUpdatedFrame\(", regex:true, file_pattern:"Source/Verse/PawnRenderer.cs" }` |
| search_source 原始输出 | `164:	private Mesh GetBlitMeshUpdatedFrame(PawnTextureAtlasFrameSet frameSet, Rot4 rotation, PawnDrawMode drawMode)` |
| 第二锚点（R2） | **必须写明是哪一个、为什么它不依赖被纠正对象**（零计数 dump 最佳） |
| 核验结论 | **属实**（1-based 直引，无需换算） |
| 上下文佐证 | `read_file{ start_line:158, line_count:10 }` 首行 = 1-based 159；窗口内部空行 163 完好保留，与 §4.3 锚点表一致 |
```

「真值来源」与「原始输出」两栏是这套流程能自我否证的关键：
它们让任何人 30 秒内重跑一次并推翻你的结论。

#### 🚨 R1 的由来：**更正值没有检测面 ⇒ 比原始错更贵**

> **原始错至少被标记过（下游知道「这里存疑，会去查」）；更正值从未被标记（下游以为已经核过了，直接引用）。**
> 而**更正动作恰好发生在最不该省掉复核的时刻** —— 因为它天然带着「我已经看过了」的权威感。
> （`AGENT-BRIEF.md:221` 第 1 条「文档里写着 ≠ 我数过」的同根形态。）

#### 🚨🚨 但 `[52]` 这条**只并元模式，实例已被我方 triage 证伪 —— 照板面去「修」会把已正确的锚点改错**

**逐字写明，不得省略：**

> **本条实例已被我方 triage 证伪，⚠️ 照板面去「修」会把已正确的锚点改错。**

| 项 | 板上 `[52]` 的说法 | 我方 triage 实测（`triage/52-…` §3.1/§3.2） |
|---|---|---|
| 审查者更正为 `:37/:42/:47/:52` | 「这四个数**全部指向闭合大括号** `}`」 | ❌ **这四个数就是四处 `< 10000` 比较本身** |
| 四处比较的真实行号 | `:36 / :41 / :46 / :51` | ❌ **真值就是 `:37 / :42 / :47 / :52`**（闭合大括号在 `:38/:43/:48/:53`） |

**⇒ 审查者的更正是对的；引入错误的是 `[52]` 的执行方自己**（它少算 1 行、自己的示意图每个数偏 −1）。
**⇒ 保留元模式，丢弃实例；不要为它新开计划、不要去改 `PathGrid.cs` 的锚点 —— 没有东西需要修。**

**⚠️ 口径附注（`read_file` 页脚总行数 ≠ `search_source` 最大行号）**：`PathGrid.cs` 页脚报 **285**，
而 `search_source` 全文件 dump 的**最大行号是 284**。⇒ **两个口径别混**，本条的裁决只依赖带行号的 dump，不用页脚。

### 4.5 本节的实测记录（11 次只读调用，可复现）

| # | 调用 | 返回 |
|---|---|---|
| A | `search_source{ query:"GetBlitMeshUpdatedFrame", file_pattern:"Source/Verse/PawnRenderer.cs" }` | `164:` 与 `243:` |
| B | `search_source{ query:"^\tprivate Mesh GetBlitMeshUpdatedFrame\(", regex:true, file_pattern:"Source/Verse/PawnRenderer.cs" }` | **仅 `164:`**（2 条 → 1 条） |
| C | `read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:163, line_count:3 }` | 首行 = `private Mesh GetBlitMeshUpdatedFrame(…)`；`[TRUNCATED] Showing 3/714 lines.`；`(Tip: … start_line: 166)` |
| D | `read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:162, line_count:3 }` | 首行 = `private Mesh GetBlitMeshUpdatedFrame(…)` —— **与 C 逐字相同**；tip = `165` |
| E | `read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:158, line_count:10 }` | 首行 = `public void SetStatuePaintColor(Color? color)`；**第 5 行是空行**（= 1-based 163）；tip = `168` |
| F | `read_file{ path:"Source/Verse/GizmoGridDrawer.cs", start_line:133, line_count:2 }` | 首行 = `Log.ErrorOnce($"{arg} currentSelectable: {obj.ToStringSafe()}", 3427734);`；`[TRUNCATED] Showing 2/449 lines.`；tip = `135` |
| G | `search_source{ query:"FenceBlocked => fenceBlocked", file_pattern:"Source/Verse/AI/Pathing.cs" }` | `39:	public PathingContext FenceBlocked => fenceBlocked;`（`Pathing.cs` 基准锚点，1-based） |
| H | `read_file{ path:"Source/Verse/AI/Pathing.cs", start_line:35, line_count:3 }` | 首行 = `	public PathingContext Normal => normal;` = 1-based **37**（36 是空行 ⇒ 右移）；`[TRUNCATED] Showing 3/172 lines.`；tip = `38`（**少 1**） |
| I | `read_file{ path:"Source/Verse/AI/Pathing.cs", start_line:36, line_count:3 }` | 首行 = `	public PathingContext Normal => normal;` = 1-based **37**（准）；第 3 行 = `FenceBlocked => fenceBlocked;` = **39**，与 G 互证；tip = `39`（正确接续） |
| J | `read_csharp_symbol{ typeName:"Pawn", memberName:"GetPathContext" }` | `// File: Verse/Pawn.cs (Lines 5348-5359)` + 正文 12 行（与 5359−5348+1 吻合） |
| K | `read_csharp_symbol{ typeName:"Pathing" }` | `// File: Verse/AI/Pathing.cs (Lines 7-171)` + 整个类（证明头不是对方法硬编码） |

**C 与 D 的 `start_line` 相差 1、返回内容逐字相同** ⇒ 中间那行（163）是空行且被吞。
**C 的 tip（166）正确、D 的 tip（165）少 1** ⇒ 右移的机械判据成立。
**E 的第 5 行空行完好、第 6 行正是 164** ⇒ 窗口内部的空行不被吞。
**H 与 I 的 `start_line` 相差 1、首行却相同** ⇒ 在 `Pathing.cs` 上独立复现了同一条规则；
**I 的第 3 行（39）与 G 的 `search_source` 结果互证** ⇒ A/B 对拍可脱离已知锚点使用。
**J 与 K 同时给方法与整类发行号头** ⇒ 行号格式化能力在本服务里已存在（§4.2.1）。

### 4.6 取证纪律（零计数优先 —— 写在这里是因为它比任何单条规则都更省事）

**首选动作：零计数取证。**

```
search_source{ query: ".", file_pattern: "Source/Verse/AI/PathGrid.cs" }   → 整文件带行号 dump
```

（`query:"."` 是**单字符正则**，匹配任意含字符的行，不是字面量 —— 按 `AGENT-BRIEF.md:106-111` 记录口径。）

⇒ **核一段连续代码时，这是首选动作** —— 它**根本不需要数**，
因此不受「Δ=+1 丢行」「末尾恒定分隔空行」「手数少一位」这三个已知偏差源影响。

**次选：单行探针**（`read_file{ line_count: 1 }`）。它只返回一行，**没有可数的余地**，
把这一行的内容拿去找 `search_source` 比对即可。

**🚨 两条口径警告，别混：**

| 口径 | `PathGrid.cs` 的值 | 说明 |
|---|---|---|
| `read_file` **页脚**总行数（`Showing N/M`） | **285** | ⚠️ 这是**它自己口径**下的长度 |
| `search_source` 全文件 dump 的**最大行号** | **284** | ⚠️ 这是**带行号真值**下的长度 |

⇒ **两个口径不一致，不要混用、不要拿它们互相验证**；行号裁决一律以带行号的 `search_source` 为准。

**其它已知偏差源（数行时必须逐条排除）**：

1. **正文末尾恒定有一个渲染分隔空行**（正文与 `[TRUNCATED]` 之间）—— **它不是内容行**。
   数行时必须扣掉，否则每块多算 1。**这正是板上 `[49]`「+2」与 `[46]`「+2」最可能的来源。**
2. **丢行时页脚虚高 1**（见规则 1）—— 不要用页脚数数。
3. **不能用 `search_source` 判「某行是不是空行」**：`search_source(query:"^$", regex:true)` 检索不到空行。
   「块首行是否为空」只能靠 `read_file(start_line=S, line_count:1)` 返回空行来证，或靠 A/B 对拍。
````

---

## 4. 计划必带块（任务 2 的交付物）

**只放指针，不复制全文** —— 全文只在 `docs/mcp-source-lookup-conventions.md` 维护，
复制会立刻产生第二份会漂移的副本。

```markdown
## 0.2 行号基线（必读，一行指针 + 三条最小事实）

- 查 vanilla 行号的**唯一规范**：`docs/mcp-source-lookup-conventions.md` 的
  `## 4. 行号基线`。本计划涉及的一切 `文件:行号` 以该节 §4.3 锚点表与 `search_source` 的返回为准。
- `rimworld-source-online.search_source` 的行号是 **1-based**（真值来源，引用时必须连同原始输出一并贴出）。
- `rimworld-source-online.read_file` 的 `start_line` 是 **0-indexed**（`= 目标1based行号 − 1`），
  返回**不带行号**；且**仅当块首行（`start_line+1`）为空行时**，该行被静默丢弃、窗口右移 1 行，
  **此时只返回 `N−1` 行而页脚仍报 `N`** ⇒ **`Δ ∈ {0,+1}`，`+2` 不可能**
  ⇒ **不得从它的返回值反推行号或计数**。
- **A/B 对拍（不依赖任何已知锚点）**：同一 `path` + 同一 `line_count`，
  `[start_line=N]` 与 `[start_line=N+1]` 各调一次。**两块整块逐字相同 ⇒ 右移了**，
  该块只能当下文看。要判定行号时以 `[start_line=N+1]` 那组为准。
  ⚠️ **必须比「整块」，不能只比「首行」** —— 丢行时**首行本来就相同**，只比首行没有判别力。
- **零计数取证优先**：`search_source{ query:".", file_pattern:"<文件>" }` 直接吐整份带行号 dump，
  **根本不需要数** ⇒ 不受丢行 / 分隔空行 / 手数少一位影响。比任何 `read_file` 手法都稳。
- `get_source_slice{includeLineNumbers}` 是反编译产物行号，**不可**用于核原文件行号。
- 🚨 **升级触发**：本服务三套行号契约不一致（`search_source` 1-based 无头 /
  `read_file` 0-indexed 无头且吞行 / `read_csharp_symbol` 1-based 有头），行号错了**不会报错**。
  **任何未经 `search_source` 交叉核对的行号，该单个产物一律按 🔴 对待**，
  不得据此下「注释里的行号对不上」之类的结论。
```

---

## 5. 任务

### 任务 1：把 `## 4.` 节追加进 `docs/mcp-source-lookup-conventions.md`

**文件：**
- 修改：`docs/mcp-source-lookup-conventions.md`（由 `[11]` 号计划创建；**本任务不创建它**）
- 参考（只读，不改）：`docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md:166-326`（它要写入的正文全文）
- 测试：`docs/mcp-source-lookup-conventions.md` 的小节序号自洽性 + 交叉引用闭合

- [ ] **步骤 1：确认前置文件已由 [11] 号计划建好**

  ```powershell
  Test-Path 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\mcp-source-lookup-conventions.md'
  ```

  预期：`True`。
  为 `False` 时**停止并报告「被 `[11]` 号计划任务 1 阻塞」** —— 见 §0.2 的处置条款，不要自己建这份文件。
  为 `True` 时再确认它不是空壳：

  ```powershell
  $p='C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\mcp-source-lookup-conventions.md'
  $l=[System.IO.File]::ReadAllLines($p)
  "$($l.Length) lines"
  Select-String -Path $p -Pattern '^## \d\.' | ForEach-Object { "$($_.LineNumber): $($_.Line)" }
  ```

  预期：6 行命中，依次为 `## 0.` / `## 1.` / `## 2.` / `## 3.` / `## 4. 与 [10] 的边界（不要互相重复实现）` / `## 5. 维护规则`，且总行数 > 100。少于 6 行说明 `[11]` 号计划没跑完整，停止并报告。

- [ ] **步骤 2：插入 `## 4.` 一节并按 §2.1 重编号**

  1. 把 §3 的 ```` ```markdown ```` 围栏块**整体**插入到 `## 4. 与 [10] 的边界` 这一行的**紧前方**。
  2. 把原 `## 4. 与 [10] 的边界（不要互相重复实现）` 改写为 `## 5. 与 [10] 的边界（不要互相重复实现）`。
  3. 把原 `## 5. 维护规则` 改写为 `## 6. 维护规则`。
  4. 按 §2.1 表格同步改那 **2 处**交叉引用。

- [ ] **步骤 3：核查序号自洽、交叉引用闭合**

  ```powershell
  $p='C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\mcp-source-lookup-conventions.md'
  Select-String -Path $p -Pattern '^## \d\.' | ForEach-Object { "$($_.LineNumber): $($_.Line)" }
  "--- 所有 §N 引用 ---"
  Select-String -Path $p -Pattern '§\s*\d' | ForEach-Object { "$($_.LineNumber): $($_.Line.Trim())" }
  ```

  预期：
  - 第一段 **7 行**命中：`## 0.` … `## 3.`、`## 4. 行号基线`、`## 5. 与 [10] 的边界`、`## 6. 维护规则`，序号连续无跳号。
  - 第二段里**不再出现** `见 §4。` 指向「与 [10] 的边界」——它必须已改成 `见 §5。`；`[14]` 那一行必须指向 `§4`。

- [ ] **步骤 4：Commit**

  ```powershell
  cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
  git add docs/mcp-source-lookup-conventions.md
  git commit -m "docs: add line-number baseline section (0-indexed start_line, blank-line drop, tip drift)"
  ```

  > 若你在 `docs/issue-triage/AGENT-BRIEF.md` 的禁改范围内运行（流水线子代理），**跳过本步骤**，且本任务整体不该由你执行。

---

### 任务 2：把 §4 的指针块定为本流水线的计划必带块

**文件：**
- 创建：`docs/issue-triage/plans/<你正在写的那份计划>.md` —— 把 §4 的围栏块放进「## 0. 前置事实」之后
- 修改：**无**
- 测试：块内容与 §4 逐字一致（见步骤 3）

- [ ] **步骤 1：确认 §4 的指针块完整**

  打开 `docs/issue-triage/plans/2026-10-01-14-read-file-line-number-convention.md`，确认 §4 是一个完整的 ```` ```markdown ```` 围栏块，首行 `## 0.2 行号基线（必读，一行指针 + 三条最小事实）`，含 **7 条 bullet**（指针 / `search_source` 1-based / `read_file` 0-indexed 吞行且返回 `N−1` / A/B 对拍**须比整块** / 零计数取证优先 / `get_source_slice` 不可用 / 🚨 升级触发）。
  缺任何一项则停止并报告 —— §4 是终稿，不要自行改写。

- [ ] **步骤 2：把 §4 的围栏块原样抄进你正在写的计划**

  放在「## 0. 前置事实（已实测，不要重新摸索）」之后、「## 1. 文件结构」之前。
  **逐字抄，不要复制 §3 的锚点表** —— 锚点表只有一份，在规范文档里。

- [ ] **步骤 3：核查指针块已落位**

  ```powershell
  $p = '<你正在写的那份计划>.md'
  Select-String -Path $p -Pattern 'mcp-source-lookup-conventions\.md','0-indexed','1-based'
  ```

  预期：≥ 5 行命中，其中至少 1 行含 `docs/mcp-source-lookup-conventions.md`、至少 1 行含 `A/B 对拍`、至少 1 行含 `按 🔴 对待`。
  为 0 行说明块没抄进去。

- [ ] **步骤 4：Commit**

  ```powershell
  cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
  git add docs/issue-triage/plans/
  git commit -m "docs: add line-number baseline pointer block to plan template"
  ```

---

### 任务 3：验收演练 —— 证明写进规范文档的每一句都还是真的

**文件：**
- 验证（只读）：`docs/mcp-source-lookup-conventions.md` §4.3 锚点表、§4.5 实测记录
- 测试：无自动化测试。这是**只读探针 + 逐条比对**，判定标准写在下面

- [ ] **步骤 1：重跑 §4.5 的探针 C 与 D，确认外部 server 行为未漂移**

  两次只读调用（不启动游戏、不改任何状态）：

  ```
  read_file{ path: "Source/Verse/PawnRenderer.cs", start_line: 163, line_count: 3 }
  read_file{ path: "Source/Verse/PawnRenderer.cs", start_line: 162, line_count: 3 }
  ```

  预期输出：

  - 第一次首行 = `	private Mesh GetBlitMeshUpdatedFrame(…)`，页脚 `(Tip: Continue reading using start_line: 166)`；
  - 第二次首行 = **同一行文本**（不是空行），页脚 `(Tip: Continue reading using start_line: 165)`。

  两次内容相同 ⇒ 空行吞行仍存在；tip 相差 1 ⇒ 右移判据仍成立。
  任一条不符 ⇒ 外部 server 行为已变，**不要继续引用 §4**，把两次真实返回原样记录下来并上报。

- [ ] **步骤 2：重跑探针 E，确认窗口内部空行仍被保留**

  ```
  read_file{ path: "Source/Verse/PawnRenderer.cs", start_line: 158, line_count: 10 }
  ```

  预期：第 1 行 = `	public void SetStatuePaintColor(Color? color)`，**第 5 行是空行**，第 6 行 = `	private Mesh GetBlitMeshUpdatedFrame(…)`，页脚 `Showing 10/714 lines.` + `Tip: … 168`。
  由此可推出 §4.3 的 `PawnRenderer.cs:163` 是空行、`164` 是 `GetBlitMeshUpdatedFrame` —— 与锚点表一致。

- [ ] **步骤 3：用 `search_source` 独立复核锚点表里的 164 与 243**

  ```
  search_source{ query: "^\tprivate Mesh GetBlitMeshUpdatedFrame\(", regex: true, file_pattern: "Source/Verse/PawnRenderer.cs" }
  search_source{ query: "GetBlitMeshUpdatedFrame", file_pattern: "Source/Verse/PawnRenderer.cs" }
  ```

  预期：第 1 次返回**恰好 1 条** `164:`；第 2 次返回 **2 条** `164:` 与 `243:`。
  两条都成立 ⇒ §4.3 的锚点表与 §4.5 的探针 A/B 记录仍然属实。

- [ ] **步骤 4：在第二个文件上跑 A/B 对拍（§4.1 规则 5）**

  ```
  search_source{ query: "FenceBlocked => fenceBlocked", file_pattern: "Source/Verse/AI/Pathing.cs" }
  read_file{ path: "Source/Verse/AI/Pathing.cs", start_line: 35, line_count: 3 }
  read_file{ path: "Source/Verse/AI/Pathing.cs", start_line: 36, line_count: 3 }
  ```

  预期：
  - 第 1 次返回 `39:	public PathingContext FenceBlocked => fenceBlocked;`
  - 第 2 次首行 = `	public PathingContext Normal => normal;`（= 1-based **37**，因为 36 是空行被吞），页脚 `Showing 3/172 lines.` + `Tip: … 38`
  - 第 3 次首行 = **同一行文本**，页脚 `Tip: … 39`；且**第 3 行是 `FenceBlocked => fenceBlocked;`（= 39），与第 1 次互证**

  三条全符 ⇒ 规则 5 成立：**不靠任何预先知道的锚点，多一次调用即可测出右移**。
  两条 `read_file` 返回相同即判右移 —— 这就是 §4.1 规则 5 写的那条判据本身。

- [ ] **步骤 5：确认同服行号头仍存在（§4.2.1）**

  ```
  read_csharp_symbol{ typeName: "Pawn", memberName: "GetPathContext" }
  read_csharp_symbol{ typeName: "Pathing" }
  ```

  预期：第 1 次首行 = `// File: Verse/Pawn.cs (Lines 5348-5359)`，正文 **12 行**（与 5359−5348+1 吻合）；
  第 2 次首行 = `// File: Verse/AI/Pathing.cs (Lines 7-171)`，是**整个类**。
  两条都成立 ⇒ 行号格式化能力仍在本服务里，§4.2.1/§4.2.3 的修复方向不变；
  **任一条消失** ⇒ 外部 server 已改过行号策略，**立即停止并上报**（此时 §4.2 的全部推论需重做）。

- [ ] **步骤 6：把五步的真实返回记进你的计划**

  在你正在写的那份计划里追加一节，逐条贴出步骤 1–5 的**真实返回**（不是转述）。
  这一节就是 §4.4「核证记录格式」的第一个实例。

- [ ] **步骤 7：Commit**

  ```powershell
  cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
  git add docs/issue-triage/plans/
  git commit -m "docs: record line-number baseline verification run"
  ```

---

## 7. 边界声明（不属于本计划，交接时别丢）

- **吐槽板 `[14]` 的状态字段本计划不改。** 按 `docs/issue-triage/README.md:19`，追加 `> 🤖 **Triage 批注**` 是**主代理**的职责。批注时应当点明两件事：
  ① 板上写的那个 workaround（每次读完自己数一遍空行再对）**本身已被证伪** —— 返回值里根本没有那行空行可数（§4.1 规则 1）；
  ② 严重度论证里「任何判断都是猜」**说过头了** —— 准确说法是「**单次 `read_file` 调用无法自校验**」，A/B 对拍多一次调用就能测出右移（§4.1 规则 5）。
- **新条目 `[39]`（同服的 `read_csharp_symbol` 带行号头）改变了本条的性质，本计划已吸收**（§4.2）。若主代理要给 `[39]` 单独写批注，请指向本计划 §4.2.1 / §4.2.3，**不要另起一份规范**。
- **🚨 选型不动（[49] 明确要求，下一个人别改）**：板上 `[49]` 标题写「**光加『起点回显』修不好**」**不成立**。
  `triage/49-…` §5.3 的全部 14 次探针**恰恰就是照这条建议做的**，结果是：每次只要**一次 `search_source` 调用**
  就能拿到 `行号:原文` 一对，行号**再也不会错**；**没有一次**出现两份快照对不上。
  ⇒ **Δ 到底是 0 还是 +1 根本不重要** —— 因为你**不再需要从 `read_file` 的返回值推行号**。
  ⇒ **本计划原选的修法（`read_file` 返回块加起点回显 / 每行前缀真实行号）恰恰是唯一能让本条失效的修法。**
  **本计划不改选型；下一个人也不许以「Δ 不是常数」为由换掉它。**
- **`[46]`（首行对应第几行无法自证）已并入 §4.1 规则 5，不另立计划。** 它与 `[49]` 是**同一条机制的两份样本**，
  `triage/46-…` §4 对照表逐项一致。⚠️ 唯一例外见 §0.7 末段：`triage/46-…` §7 那处「行数不减」的举例**已被本计划探针 V–Y 证伪**，
  但**该报告的主结论不受影响**。
- **🚨 `[52]` 只并元模式，实例已被证伪 —— 照板面去「修」会把已正确的锚点改错。**
  真值是 `:37/:42/:47/:52`（四处 `< 10000` 比较本身），**审查者的更正是对的**，
  **引入错误的是 `[52]` 的执行方自己**。⇒ **不要为它新开计划、不要去改 `PathGrid.cs` 的锚点 —— 没有东西需要修。**
  保留的是元模式（R1/R2，见 §4.4）。
- **上游裁决报告 `docs/issue-triage/triage/14-read-file-start-line-zero-indexed.md` 本计划不改。** §0.6 的 6 条更正只在本计划内生效。
- **`docs/mcp-source-lookup-conventions.md` 仍然不存在，本计划的文档产出全部处于阻塞态。**
  **改完这份计划 ≠ 规范已落地** —— 详见 §0.2 的 🚨🚨 阻塞声明（含计划 11 ↔ 本计划的循环依赖与解除顺序）。
- **`AI-DEPLOYMENT.md` 的 `### 8.1` 入口本计划不改** —— 它由 `[11]` 号计划任务 2 建立，本计划只产出指针去指它。
- **修复成本已从「外部仓库的 TODO」降为「复用外部仓库已有的那一行」—— 但仍未降到本仓库能修。**
  裁决报告 §5.1 推荐的方案 A（返回值加行号前缀）**不再是「想不想做」的问题**：
  同服的 `read_csharp_symbol` 已经在输出 `// File: <路径> (Lines N-M)`（§4.2.1 实测），
  **行号格式化能力在该服务里已经存在**，把同一个格式接到 `read_file` 上是接线工作。
  **但要接对，顺序是硬要求**：① 先修空行吞行 → ② 行号从**实际返回的切片**反算
  → ③ 再做 opt-in 的 `include_line_numbers`（沿用 `get_source_slice` 已有参数名）。
  **跳过 ① 直接加头，会造出稳定错 1 行、且看起来权威的头 —— 比现在没有行号更危险**（§4.2.3）。
  **方向限制：格式可复用，行号计算不可复用**（§4.2.2）—— 因为 `read_file` 要处理的
  `start_line+1` 换算、空行吞行右移、tip 补偿，`read_csharp_symbol` 一个都不需要。
  **本仓库仍然 0 行可改**（§0.1 F1/F2），三步全部要在外部仓库 `rimworld-source-online` 内完成。
  **方案 B（把 `start_line` 改成 1-based）仍被否决** —— 它会静默错位所有既有调用方，
  正是本条要消除的失败模式。

---

## 8. 自检（写完对着规格逐条核对）

1. **规格覆盖度** —— 吐槽板 `[14]` 的每条诉求都有落点：`start_line` 0-indexed 与「返回无行号前缀」已在 `[11]` 号计划的 `## 2` ③，本计划补它没覆盖的增量：**空行吞行（丢行时返回 `N−1` 行、页脚仍报 `N`）/ tip 错位 / 反推不可能**（§4.1 规则 1–4）、**A/B 对拍自证（须比整块）**（规则 5）、**三套契约不一致 + 🔴 升级触发**（规则 6）、**替代路径现在存在**（§4.2.1）、**方向限制**（§4.2.2）、**修复次序警告**（§4.2.3），外加锚点表（§4.3）、核证记录格式 + **R1/R2**（§4.4）、可复现实测（§4.5，A–K 共 11 次）、**取证纪律**（§4.6）、**本次修订的独立复核**（§0.7，V–Y 共 4 次）。
   **并入的同族条目**：`[39]`（§4.2）、**`[46]`**（§4.1 规则 5）、**`[49]`**（§0.3 结论 2/4/5 + §4.1 规则 1/4/5 + §0.7）、**`[52]`**（§4.4 的 R1/R2，**仅元模式，实例已证伪**）。
2. **占位符扫描** —— 全文无「待定 / TODO / 后续实现 / 补充细节 / 类似任务 N」（唯一命中是本节自检语句本身在引用这些词）。三个任务共 **15 个步骤**，每步都有可执行的命令、可粘贴的文本或可复现的调用与预期返回。
3. **类型与命名一致** —— 探针 A–K 在 §0.3（A–F）、§3 的 `## 4.5`（A–K）、§0.4 锚点表「复核探针」列、任务 3 步骤 1–5 中编号一致；**探针 V–Y 是 §0.7 本次修订专用，不与 A–K 混号**；`§2` / `§3` / `§4` 与 `4.1`–`4.6` 编号在全文引用一致；重编号表（§2.1）与其后的交叉引用修正一一对应；**M1–M9、R1–R2 的落点表（§0.2 阻塞守卫）与各节实际位置一致**。
4. **行号复核** —— §0.5 全部行号经 `System.IO.File::ReadAllLines` 或 `Select-String` 复核；§0.4 锚点表经 **11 次只读 MCP 探针**复核（`search_source` / `read_file` / `read_csharp_symbol` 三种工具）；**§0.7 的 V–Y 是本次修订新增的 4 次只读探针**。吐槽板行号按 §0.5 的要求以**标题文本**为锚点，行号只作快照。
5. **恒真断言自查**（`AGENT-BRIEF.md:225`）—— 本轮修订的核心动机就是**删掉一条恒真判据**（原 `tip ≠ 首行真实行号 + 返回行数`，判别力 = 0）。
   ⇒ **全文自问一遍「这条断言能不能失败？」**：§4.1 规则 4（`tip ≠ … + 返回行数 − 1`）**能失败**（有校准数据）；
   规则 5（整块逐字相同 / 整块不同）**能失败**；**原来那条不能失败，已删除并在原位留下推导过程，防止下一个人复活它。**
6. **阻塞状态** —— 本计划的**全部文档产出仍被阻塞**（§0.2 🚨🚨）。**本轮只改了计划本身，没有落任何文档。**

---

## 9. 执行交接

计划已完成并保存到 `docs/issue-triage/plans/2026-10-01-14-read-file-line-number-convention.md`。两种执行方式：

**1. 子代理驱动（推荐）** —— 每个任务调度一个新的子代理，任务间进行审查，快速迭代
**2. 内联执行** —— 在当前会话中使用 executing-plans 执行任务，批量执行并设有检查点

**选哪种方式？**

若选子代理驱动：**必需子技能：** 使用 superpowers:subagent-driven-development
若选内联执行：**必需子技能：** 使用 superpowers:executing-plans
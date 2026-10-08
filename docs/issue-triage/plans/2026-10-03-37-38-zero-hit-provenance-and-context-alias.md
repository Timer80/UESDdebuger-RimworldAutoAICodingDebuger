# [37]+[38] 零命中出处与 `contextAlias` 护栏 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 给「零命中」装上**出处** —— 把 `decompilerserver` 检索的「在哪儿搜的」变成硬性交付物，并立一条通用规约：**跨程序集阳性对照是无效对照**。

**架构：** 本计划**不改一行游戏代码**。它只做三件事：①把「零命中必须附出处 + 阳性对照必须同 alias」写进**每一个子代理开工前必读**的 `AGENT-BRIEF.md` §3.5（**本仓库侧唯一实际动作**）；②给最高优先级的计划 33-35 钉上「必须显式传 `contextAlias`」的前置护栏（它要把 33/35 的 vanilla 结论**固化进代码与文档**，而那些结论全部来自 decompilerserver）；③产出一份**可直接整段复制**的上游 issue 正文（decompilerserver 侧，本仓库改不了）。长期规范正文**不由本计划创建** —— 那是计划 11 任务 1 的产物 `docs/mcp-source-lookup-conventions.md`，本计划只出交接稿。

**技术栈：** 纯 Markdown（`AGENT-BRIEF.md` + 计划文件）。`MCP/index.js` **可能**在任务 2 的「已落地分支」里被改一行文案，不新增逻辑、不新增构建。测试入口 `Set-Location MCP; npm test`（`MCP/package.json` 的 `test` = `node --test "test/*.test.js"`）。

---

## 0. 开工前必读

### 0.1 五条硬约束

| # | 约束 | 依据 |
|---|---|---|
| 1 | **不得改吐槽板 `docs/mcp-吐槽板.md`**、不得改计划 01/02/04/05/06/07/08/09/11/12/14/22/28/31/32/36 的任何文件 | 铁律 §1.1；本计划只碰 `AGENT-BRIEF.md`、计划 33-35、以及本文件自己 |
| 2 | **⛔ 不得创建 `docs/mcp-source-lookup-conventions.md`** | 它是**计划 11 任务 1** 的产物（`2026-10-01-11-…:274`），目前 `Test-Path` = `False`。两份计划各建一份会互相覆盖。本计划**只出交接稿**（§5.2） |
| 3 | **⛔ 不得把 `contextAlias` 改成必填的建议写进任何文档** | 会破坏 191 个工具的调用契约。见 §3.3 |
| 4 | **本仓库不是 git 仓库** | 铁律 §3.5 事故链第 4 步「没有备份可恢复」⇒ 每个任务的收尾步是**改动清单**，不是 `git commit` |
| 5 | **行级重排 / 插入 / 删除只能用 `edit` 的 old/new 替换，禁止用 `pwsh` 改文件** | 铁律 §3.5 硬规则 1：`edit` 是「不匹配⇒报错、文件不动」，`WriteAllLines` 是「静默写成空」 |

> ⚠️ **本计划自身也遵守**：`docs/*.md` 在铁律 §1.1 的禁改清单上，**PLAN 子代理不许碰**。
> 本计划的产物是这个**计划文件**；它描述的改动由**主代理或维护者**执行。
> 若你就是执行者，先确认你不在铁律 §1.1 的禁改范围内（计划 11 `:56` / 计划 14 `:334` 给的是同一条判据）。

### 0.2 本计划引用的行号（全部于 2026-10-03 用 `[System.IO.File]::ReadAllLines(...)` 复核）

| 引用 | 内容 | 复核结果 |
|---|---|---|
| `AGENT-BRIEF.md` | 总行数 | **214**（⚠️ 原为 **195**；**主代理 2026-10-03 已在 §3.5 补入第三段 🚨**，见下一行） |
| `AGENT-BRIEF.md:81` | `## 3.5 ⚠️ 引用行号的三条纪律（本项目实测踩出来的）` | 逐字相符（**未变**；任务 1 步骤 2 是**可选**改动） |
| `AGENT-BRIEF.md:113` | `### 🚨🚨 decompilerserver 的 current context 是**跨会话共享的全局可变状态**` | 逐字相符（**未变**） |
| `AGENT-BRIEF.md:133` | `### 🚨🚨 但**优先不要用 decompilerserver** —— 它是跨会话共享状态，本流水线会去挪它` | 逐字相符。**主代理 2026-10-03 新增**：优先级 ①`rimworld-source-online` ②文件系统 grep ③decompilerserver 只在「必须查 IL/元数据/源码索引里根本没有该成员」时用；`load_assembly` 必带 `makeCurrent:false`；「用完不要去『恢复』别人的上下文」 |
| `AGENT-BRIEF.md:149-150` | 该段末尾两行 `> 这条与 [37] 是同一件事的两面…` / `> TEXT20 那边已立规矩…` | 逐字相符（**任务 1 步骤 3 的插入锚点**） |
| `AGENT-BRIEF.md:155` | `### 🚨🚨 禁止用 \`pwsh\` 改文件 —— 只用 \`edit\` / \`write\`` | 逐字相符（**已因主代理那段而下移**，**不再用作锚点**） |
| 计划 33-35 | 总行数 | **882** |
| 计划 33-35`:43` | `### 本计划引用的程序集事实（decompilerserver 上下文 \`rw16\`）` | 逐字相符 |
| 计划 33-35`:49-66` | F1–F16 事实表 16 行 | 逐行扫描：**15 行含 `contextAlias`，仅 `:61`（F11）不含** |
| 计划 33-35`:353` / `:357` / `:367` | `COMPILE_GUIDANCE` 里 3 处「查法：decompilerserver 的 …」 | 逐字相符 |
| 计划 33-35`:615-626` | README 表格「怎么自己核验」列（8 条命令） | 逐行读出，**8 条全部不带 `contextAlias`** |
| 计划 33-35`:726` / `:742` | `AI-DEPLOYMENT.md` 排障行与 ⚠️ 行 | 逐字相符 |
| 计划 33-35`:852-862` | `## 5. 三方交接登记表` + 表头 `:856` | 逐字相符（**任务 3 的写入面**） |
| 计划 11 | 总行数 | **1404** |
| 计划 11`:274` | `## 任务 1：建立规范正文 \`docs/mcp-source-lookup-conventions.md\`` | 逐字相符 |
| 计划 11`:329` | `## 1. 五条硬规矩（规矩 0 / 1 / 2 / 3 / 4）` | 逐字相符（**规矩 0.5 的插槽**） |
| 计划 11`:253` / 计划 14`:140` | 两处都论证过「别把长期规范塞进 `docs/issue-triage/`」 | 逐字相符（**任务 1 的设计约束**，见 §4.3） |
| 吐槽板 | 总行数 | **1624**（`## [37]` / `## [38]` 用 `Select-String -Pattern '^## \[3[78]\]'` 定位，**未用行号**） |
| `docs/mcp-source-lookup-conventions.md` | `Test-Path` | **`False`**（确认必须由计划 11 建） |

### 0.3 本计划引用的程序集事实（decompilerserver 上下文 `vanilla`）

**检索范围（凡「不存在 / 查不到」必须随附）：** 上下文 `vanilla` = `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`，**仅 vanilla 本体**，MVID **`61e4173561894da49d210260257b5097`**，`typeCount=16158` / `methodCount=76770` / `namespaceCount=29`。
本环境 `vanilla` / `game` / `default` / `rw16` **四个别名指向同一 MVID**，所以用哪个都行 —— **但必须显式写出来**（这正是本计划的主题）。
**未检索任何模组程序集**（`TEXT20.dll` / `Lib.GAB.dll` 等是另外的上下文）⇒ 下面所有「不存在」结论**只对 vanilla 成立**。
**未启动游戏、未调用 `execute_csharp_code`、未调用 `select_context` / `load_assembly` / `warm_index`、未构建**（铁律 §1.2/§1.3）。
**未使用 `search_string_literals`** —— 该工具按**字面量**匹配，带 `|` 的模式无效（铁律 §3.5）。

### 0.4 失败路径基线快照（本计划作者亲跑，2026-10-03，**全部显式带 `contextAlias:"vanilla"`**）

这是任务 2 / §5.1 的**验收基线**。上游改完之后，这些形状就是「改前 / 改后」的对照点。

| # | 调用（`contextAlias:"vanilla"`） | 本轮实测返回 |
|---|---|---|
| E1 | `get_server_stats{contextAlias:"vanilla"}` | **`currentContextAlias:"gab"`**（仍然是 `gab`！`Lib.GAB.dll`，94 类型，`loadedAtUnix=1790956347`，为全部 19 个已加载上下文里**最新**的一个）；`vanilla` 自身 MVID `61e41735…5097` / 16158T / 76770M / 29NS |
| E2 | `get_members_of_type{typeId:"Verse.GenPath", mode:"signatures"}` | `status:"error"`，`message:"Type 'Verse' could not be found."`，`error.code:"type_not_found"`，`details.parsed:{typeName:"Verse", memberName:"GenPath"}`，`details.candidates:[]` |
| E3 | 同上，错误体的 `hints` | `hints[0] = search_symbols{query:"GenPath", kind:"type", limit:10}`；`hints[1] = search_types{query:"Verse", limit:10}` |
| E4 | `get_members_of_type{typeId:"Verse.AI.Pathing", mode:"signatures"}` | ✅ `ok`，**`totalEstimate:21`** —— **点分名在正确上下文下完全正常** |
| E5 | `search_symbols{query:"GenPath", kind:"type", limit:10}` | **`totalEstimate:1`** —— `61e4…:02000FC2:T` = `Verse.AI.GenPath`。**`hints[0]` 是精确的** |
| E6 | `get_overloads{memberId:"Verse.GridsUtility:GetFirstThing"}` | **`totalEstimate:1`**（只回 `06001B73:M` 一支）—— 计划 33-35 的 F11 成立 |

**E2/E3 逐字全文（本计划最重要的一行证据）**：

```
Type 'Verse' could not be found.
details = {"input":"Verse.GenPath","expectedKind":"type",
           "parsed":{"typeName":"Verse","memberName":"GenPath"},"candidates":[]}
error.code = "type_not_found"
hints[0] = {"tool":"search_symbols","arguments":{"query":"GenPath","kind":"type","limit":10}}
hints[1] = {"tool":"search_types","arguments":{"query":"Verse","limit":10}}
```

**⇒ 三个可直接验收的事实：**

1. **响应体里没有 `searchedContext` / `searchedContextAlias` / `searchedAssemblyPath` 任何一个字段。** 这就是上游建议 1（F1）的验收基线 —— 逐条判据见 **§0.6**。
2. **`hints[1]` 带 `limit:10`，而 `query:"Verse"` 的 `totalEstimate` 是 4186** ⇒ 调用方会拿到 **10 条全噪声**（全是 `Verse` 前缀的无关类型），**占满整次返回的预算**。它长得像一条精确的检索式，实际是纯噪声（§2.2）。
3. **`hints[0]`（`search_symbols{kind:"type", limit:10}`）只回 1 条且正中目标** ⇒ `hints[1]` 不仅无用，还在**挤掉唯一有用的那条 hint 的预算**（同一次返回，hints 是数组，读者按序读，10 条噪声排在正确建议前面）。

### 0.5 🚨 给维护者的实锤：**污染至今在线**（E1，2026-10-03 实测）

**`currentContextAlias` 此刻仍然是 `gab`。** 那次由 Pathos 会话造成的漂移**至今没有被任何人清掉** ——
`gab` 的 `loadedAtUnix=1790956347`，是全部 **19** 个已加载上下文里**最新的一个**
⇒ 最近一次有人动过共享状态，**动完就留下了，没有人复原**。

**这不是历史事故，是当前事实。** 任何此刻省略 `contextAlias` 的调用，都在
`Lib.GAB.dll`（**94 类型 / 7 命名空间，根本不含 `Verse` 命名空间**）上检索。

⇒ 它同时印证了主代理 2026-10-03 补进 `AGENT-BRIEF.md:133` 的那段「**优先不要用 decompilerserver**」：
**共享状态已经脏了又没人清 ⇒ 默认路径就该绕开它，而不是每次都记得传 alias。**
本计划两条腿都不废：**绕开是首选（上游/工具选择层），alias 纪律是万一要用时的底线（证据层）。**

### 0.6 上游建议 1（`searchedContext` 恒带字段）的**可执行验收判据**

本轮 E2/E3 逐字实测给出**改前基线**，据此验收：

| 检查 | 命令 | 改前（2026-10-03 实测） | 改后必须满足 |
|---|---|---|---|
| 失败响应体有无 `searchedContext` | `get_members_of_type{typeId:"Verse.GenPath", contextAlias:"vanilla"}` → 看 `error` / `details` | ❌ **无**（`details` 只有 `input` / `expectedKind` / `parsed` / `candidates`） | ✅ `error.searchedContext = { alias, assemblyPath, mvid }`，且 `alias=="vanilla"`、`mvid=="61e4173561894da49d210260257b5097"` |
| 成功响应体有无 `searchedContextAlias` / `searchedAssemblyPath` | `get_members_of_type{typeId:"Verse.AI.Pathing", contextAlias:"vanilla"}` → 看 `data` | ❌ **无**（`data` 只有 `items` / `hasMore` / `totalEstimate`） | ✅ `data` 下**恒有**这两个字段，**非零命中也要有** |
| 零命中分支有无 `warning` | 任一 0 命中调用，**且当前上下文 ≠ `Assembly-CSharp`** 时 | ❌ 无 | ✅ `data.warning` 出现，文案含「这不等于该符号不存在」与 `contextAlias:"vanilla"` |

⚠️ **验收时必须自己也带 `contextAlias`** —— 否则你是在**脏上下文**下验收「脏上下文告警」，
这正是本条要防的事。

---

## 1. 文件结构

| 文件 | 动作 | 职责（每个文件一个明确职责） |
|---|---|---|
| `docs/issue-triage/AGENT-BRIEF.md` | **修改 1–2 处**：①**`:149-150` 之后新增一段 🚨**（主代理那段之后）；②`:81` 的 §3.5 标题（**可选**） | **本仓库侧唯一实际动作**。承载「万一用了 decompilerserver，证据怎么算」：零命中附出处 + 阳性对照同 alias。⛔ **不重复**主代理已写的「优先不要用 decompilerserver」那一段 |
| `docs/issue-triage/plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md` | **修改 5 处**（任务 2 的「未落地分支」）：`:43` 标题、`:61` 的 F11 行、`:353`/`:357`/`:367` 三条 guidance 文案、`:615-626` 的 README 表格 | 给最高优先级的计划钉上「必须显式传 `contextAlias`」前置护栏。**不改它的事实内容**（F1–F16 经复核全部成立，见 §2.1） |
| `README.md` / `AI-DEPLOYMENT.md` / `MCP/index.js` | **修改**（任务 2 的「已落地分支」，条件执行） | 只在计划 33-35 **已落地**时执行，给它**发货出去的文案**补 alias |
| `docs/issue-triage/plans/2026-10-03-37-38-…md`（**本文件**） | **修改**：§5 两段交接稿 + §7 交接登记表 | 唯一新建文件。承载「可整段复制」的交接物 |
| `docs/mcp-吐槽板.md` / 计划 01/02/04/05/06/07/08/09/11/12/14/22/28/31/32/36 | **只读，一个字都不改** | — |
| `docs/mcp-source-lookup-conventions.md` | **⛔ 不创建** | 计划 11 任务 1 的产物（`2026-10-01-11-…:274`）。本计划只在 §5.2 交出**可粘贴的交接稿** |

**为什么护栏落在 `AGENT-BRIEF.md` 而不是 `docs/mcp-source-lookup-conventions.md`** —— 两条都做，但分工不同：

| 落点 | 承载什么 | 为什么 |
|---|---|---|
| `AGENT-BRIEF.md` §3.5 | **3–5 行**的「动手前必看」 | [37] 的暴露面**就是派单**：裁决报告 37 的审计证明 35 份报告里 0 例栽在这上面，**恰恰因为执笔者都读了 §3.5 那段 🚨**。规约必须待在执笔者**开工前已经读过**的地方 |
| `docs/mcp-source-lookup-conventions.md` 规矩 0.5 | **完整规范**（四要素 + 验收方法 + 对照词表） | 计划 11`:253` 与计划 14`:140` **都论证过**「长期规范不该塞进 `docs/issue-triage/`（那是问题记录目录，会随流水线一起腐坏）」。本计划接受这个论证 |

**为什么不并入计划 11**：对象是**不同的服务、不同的失败模式**。计划 11 治 `rimworld-source-online` 的 `search_source`（**query 写错** ⇒ 路径/正则层面的假阴性）；本条治 `decompilerserver` 的**程序集上下文**（**搜错程序集** ⇒ 索引范围层面的假阴性）。计划 11 已 1404 行，塞一条异构规则会稀释它的主题。

---

## 2. 对两份裁决报告的复核与修正（**本计划作者本轮亲跑，全部带 `contextAlias:"vanilla"`**）

上游两份报告的结论我**全部采信**，但复核时发现 **3 处需要修正**。这 3 条不是推翻结论，是把结论**收紧到能直接当护栏用**的程度。

### 2.1 ⚠️ 修正一：裁决报告 37 §9.1 说「F1–F16 每一行都显式含 `contextAlias="rw16"`」—— **不成立，F11 那一行没有**

逐行扫描 `2026-10-02-33-35-…` 的 `:49-66`：**16 行里 15 行含 `contextAlias`，唯一例外是 `:61` 的 F11**。

```
| F11 | … | `get_overloads(memberId="Verse.GridsUtility:GetFirstThing")` **只回 1 支**（泛型）；
        第二支由 `get_member_signature(memberId="61e4173561894da49d210260257b5097:06001B71:M")` 命中 |
```

**两条命令都没带 `contextAlias`。** 这不是排版瑕疵 —— F11 的核验姿势是**照抄即跑**型的，执行者在污染上下文下执行它会得到与事实相反的结果：

- `Verse.GridsUtility` 在 `Lib.GAB.dll`（94 类型 / 7 命名空间，`gab`）里**定义性不存在** ⇒ 照抄 F11 会得到 `member_not_found`，而 F11 断言的事实（有两支 `GetFirstThing`）**是真的**。
- ⇒ 正好是 [38] 那个「**对一个正确的名字报「不存在」**」的形态，只是这次发生在**计划自己的证据表里**。

**⇒ 任务 2 必须逐字改 F11 那一行，不能只加一句表头警告。**

### 2.2 ⚠️ 修正二：裁决报告 38 §7 说 `hints[1]`「返 **4186 条**」—— **措辞不准，实况更坏**

本轮 E3 抓到 `hints[1]` 的**逐字原文**：

```json
{"tool":"search_types","arguments":{"query":"Verse","limit":10},"reason":"Find the canonical type memberId."}
```

`4186` 是**不带 `limit`** 时的 `totalEstimate`；**hint 自身带 `limit:10`** ⇒ 调用方实际拿到 **10 条**，而这 10 条**全部是 `Verse` 前缀的无关类型**。同批的 `hints[0]`（E5）**只回 1 条且正中 `Verse.AI.GenPath`**。

**⇒ 缺陷的准确描述不是「噪声多」，是「**一条长得像精确检索式的纯噪声，排在唯一正确建议的前面，把它的预算挤掉了**」。** 这比 4186 这个数字更能说明为什么必须改。

**⇒ 修法（裁决报告 38 §7 已给出，本计划采纳）**：`hints[1]` 改用 `memberName` 段（即 `query:"GenPath"`），**或直接删掉**（`hints[0]` 已经够用）。**推荐直接删** —— 少一个 API 契约变更面。

### 2.3 ⚠️ 修正三：两份报告都只查了 33-35 的「事实表」，**没查它发货出去的三面**

33-35 要把 33/35 的 vanilla 结论**固化进代码与文档**。两份报告核对的是它的**证据表**（F1–F16），**没有核对它写出去的文案**。逐行读出后发现**三个面全部不带 `contextAlias`**：

| # | 面 | 位置 | 条数 | 为什么比 F11 严重 |
|---|---|---|---|---|
| **S1** | **`COMPILE_GUIDANCE` 运行时文案**（`MCP/index.js`） | 计划 33-35`:353`、`:357`、`:367` | 3 处「查法：decompilerserver 的 `list_namespaces` / `list_namespaces()` / `search_types(query)` / `get_members_of_type(typeId)`」 | **这是发给机器的指令**，在 AI 撞到 `CS0234` 的**那一刻**被派发出去。事实表的命令只被**一个**执行者抄一次；这段文案被**每一个撞错的 AI** 现场执行 |
| **S2** | **README 表格「怎么自己核验」列** | 计划 33-35`:615-626` | **8 条**命令，**0 条**带 alias | 这是 `README.md` 里**「怎么自己核验」那一列**。任何人拿它去核「这个成员存不存在」，都在裸奔 |
| **S3** | **`AI-DEPLOYMENT.md`** | 计划 33-35`:726`、`:742` | 2 处（⚠️ 行 + 排障表行） | 排障决策树是 AI 遇错时的**首选入口** |

**⇒ 任务 2 因此有 5 个落点（`:43` / `:61` / 3 条 guidance / `:615-626`），不是一个。**

### 2.4 ✅ 复核通过的（不再讨论，只登记）

| 断言 | 本轮复核 | 结果 |
|---|---|---|
| `Verse.AI.Pathing` 在 vanilla 上可解析 | E4 → `totalEstimate:21` | ✅ **板上 [38]「含点命名空间必然解析错」实测为假，应撤回** |
| 解析器是「先试完整名，失败才回退按最后一点切分」 | E2 的 `parsed:{typeName:"Verse", memberName:"GenPath"}` + E4 成功 | ✅ 双向证实 |
| `candidates` 机制**对含点名称工作正常** | 裁决报告 38 §7 六个含点用例全返正确候选 | ✅ **板上 [38] 的「`candidates:[]`」观察作废，不立项** |
| `hints[0]` 精确可用 | E5 → 1 条正中目标 | ✅ |
| F11「`get_overloads` 只回 1 支」 | E6 → `totalEstimate:1` | ✅ |
| 失败响应体**无** `searchedContext*` 字段 | E2/E3 逐字 | ✅ —— 这是 F1 的验收基线 |
| `currentContextAlias` 此刻**仍是** `gab` | E1 → `currentContextAlias:"gab"` | ✅ **污染状态此刻仍在线**（`loadedAtUnix=1790956347`，19 个上下文里最新） |
| `stringLiteralIndexReady:false` **不是**第二条假阴性机制 | 裁决报告 37 §2.3 实测 `gab` 上 `search_string_literals("GABP")` = 6 条 | ✅ **阴性结果**（见 §4.4 规约 ④） |
| 38 条零命中结论中因 [37] 作废的 | **0 条** | ✅ 本计划不追溯修改任何既有报告 |

---

## 3. 归并与严重度（裁决报告已裁定，照办）

### 3.1 合并 [37] + [38]

| 项 | 裁定 |
|---|---|
| 合并？ | ✅ **合并成这一份**。裁决报告 38 §12 的三条理由全部采信：**[38] 是 [37] 的一个逐字可复现的实例**（同一 `typeId`、同一工具，**唯一变量是当前上下文**），不是「同族」而是**同一条因果链**；合并后严重度才不会被低估；修复面重叠（[37]④ 的「响应里始终带上被搜的 assemblyPath」一次同时修好 [38] 的误导文案） |
| 并入计划 11？ | ⛔ **不并入**（§1 已论证） |
| 独立看严重度 | 🟡 **低**（[38] 单看：点分名 100% 可用，失败路径自诊断友好，残值只有「错误文案 + 一条指错的 hint」，成本是改字符串） |
| **作为 [37] 实例合并后** | 🔴 **高**。两条理由：①**触发条件调用方完全无法预测**（`currentContextAlias` 是别人留下的全局状态）；②会对**一个正确的类型名**报「类型不存在」—— **这正是最容易被写进勘误文档、然后被下一个人继承的假阴性** |

### 3.2 撤回不立项（两条）

| 板上说法 | 处置 | 依据 |
|---|---|---|
| [38]「含点命名空间**必然**解析错」 | ⛔ **撤回** | E4：`Verse.AI.Pathing` = 21，**一次调用就成功**。若是无条件切分，它必然被切成 `Verse.AI` 而失败 |
| [38]「`candidates:[]` 是缺陷」 | ⛔ **撤回** | 裁决报告 38 §7：候选机制**对含点名称工作正常**（`Verse.AI.Pathin` → 3 条含正确的 `Verse.AI.Pathing`）。空的那些场合（`gab` 上下文、命名空间约束过滤）**空是正确的** |

### 3.3 ⛔ 不建议把 `contextAlias` 改成必填（裁决报告 37 §6.1 已裁定，照办）

会**破坏 191 个工具的调用契约**，且对「就是想查 `gab`」的合法用法造成伤害。**保持可选 + 响应急诊化**是正确的。**本计划不得在任何文档里写「应该改成必填」**（硬约束 3）。

### 3.4 责任层

| 侧 | 权重 | 本计划对其做什么 |
|---|---|---|
| 外部 `decompilerserver` MCP 服务 | **7** | 产出一份可直接提交的上游 issue 正文（§5.1），**源码不在本仓库**（裁决报告 37 §5 已独立核实：全树 grep `decompil` 零命中） |
| `Mods\UESDdebuger`（本仓库） | **3** | **任务 1 + 任务 2**：把「零命中必须附出处 / 对照必须同 alias」写成本项目的硬约定，并给 33-35 钉护栏 |

---

## 4. ⭐ 通用形态：**跨程序集阳性对照是无效对照**（本计划真正的增量）

> 这是裁决报告 37 §0b(b) / §8.3 提级的产物。**它不是本项目独有的** ——
> **主代理自己给的对照就犯过这个错**：`tools/call` 的阳性对照跑在 `Gabp.Runtime.dll`，
> 而它要背书的「`GABP 请求` 零命中」跑在 `RimBridgeServer.dll` / `.Core.dll` / `Lib.GAB.dll` —— **三个不同的程序集、三个不同的索引**。
> 裁决报告用**同程序集对照**（`ticks` / `timeoutMs` in `rbs`）把结论救回来了 ——
> **结论成立、论证弱。** 若当时没有这层复核，一份「索引非空」的错证会长期留在归档里被继承。

### 4.1 规约原文（要写进 `AGENT-BRIEF.md` §3.5 的那一条）

> **阳性对照必须在与被验证检索 _同一个 `contextAlias`_ 上执行。**
> 一个程序集索引非空，**推不出**另一个程序集的索引非空 —— 它们是两套独立的索引、两套独立的 MVID。
> 对照跑在别的 alias 上时，那个对照**不是弱证据，是无效证据**：零命中的可信度**不因它增加半分**。

### 4.2 可执行的验收方式（不是口号，是能跑的三条）

设零命中检索为 **Z**，阳性对照为 **C**。

| 步 | 动作 | 判据 |
|---|---|---|
| **1** | 记下 **Z** 的 `contextAlias`，并用 `get_server_stats{contextAlias:"<同一 alias>"}` 读出该 alias 的 **MVID** | MVID 必须写进结论文本。**只写 alias 不写 MVID 不算数** —— alias 指向的程序集可以被换掉（裁决报告 37 §0c） |
| **2** | 用**步骤 1 的同一个 alias** 跑 **C** | C 的 `arguments` 里必须逐字含同一个 `contextAlias` |
| **3** | 比对 Z 与 C 的 **MVID 字符串**是否相等 | **不相等 ⇒ C 作废**（不是「降级」，是作废），Z 的零命中降为「**未验证**」，不得写进任何报告/计划/文档 |

**对照词怎么选（每个 alias 各备一个「已知一定存在」的 token）** —— 这是让步骤 2 真正可执行的关键：

| alias | 目标程序集 | 对照词 | 命中数 | 来源 |
|---|---|---|---|---|
| `vanilla`（≡ `game` / `default` / `rw16`） | `Assembly-CSharp.dll` | `InBounds` | **13** | 裁决报告 37 §8.2 #37，本轮逐字复现 |
| `rbs` | `RimBridgeServer.dll` | `ticks` | **17** | 裁决报告 37 §8.3，本轮逐字复现 |
| `rbscore` | `RimBridgeServer.Core.dll` | `timeout` | **20** | 裁决报告 37 §8.6 |

⚠️ **对照词必须与被验证词在同一个程序集里各有一个。** 想在 `rbs` 上验证零命中，就在 `rbs` 上找对照词；**不要**拿 `gabprt` 的对照去背书 `rbs` 的零命中 —— 那正是报告 32 犯的错。

### 4.3 为什么「看起来危险的东西」不能靠推理排除（⇒ 规约 ④）

裁决报告 37 §2.3 记了一条**阴性结果**，它排除了一个本会显著复杂化修法的假设：

`status` 返回 `"stringLiteralIndexReady":false` —— 看起来像**第二条独立的假阴性机制**（索引没建 ⇒ 字面量检索恒 0）。
**实测证伪**：在 `gab` 上 `search_string_literals("GABP")` 返 **6 条**，与带 alias 的 `libgab` **逐条相同** ⇒ 该标志是**惰性/陈旧**的，不是失效。**[37] 的机制是单一的：搜错了程序集。**

**⇒ 由它得出的规约：**

> **排除一条机制的唯一凭据，是在它「出问题」的那个上下文上跑一次，并留下返回值。**
> 「看起来危险」**不构成**排除它的理由；「推理上应该不会」也不是。
> 一条阴性结果和一条阳性结果**同权** —— 都必须带出处。

### 4.4 完整四要素（写进 `AGENT-BRIEF.md` 的精简版 / 交接稿 B 的完整版）

| # | 要素 | 一句话 |
|---|---|---|
| ① | **范围** | 每一次 `mcp__decompilerserver__*` 调用**都**显式带 `contextAlias`（本环境查 vanilla 用 `"vanilla"`）。不带 alias 的结果**连「没搜到」都不算** |
| ② | **出处** | 「0 结果」必须随附 **alias + assemblyPath + MVID**。`COMMON.md §5.1` 要求零命中附**检索式**；这里要附的是**检索范围** |
| ③ | **同 alias 对照** | 阳性对照必须与零命中**同一个 `contextAlias`、同一 MVID**（§4.2 的三条） |
| ④ | **措辞** | 结论写「**在 `<alias>`（MVID `…`）下零命中**」，**不是**「不存在」 |

---

## 5. 交接稿（两段，**可整段复制**）

### 5.1 交接稿 A —— 提交给 decompilerserver 上游的 issue 正文

> **用法**：把下面 `---` 之间的全文复制到上游 issue。**本仓库改不了它**（源码不在仓库内，裁决报告 37 §5 已核实全树 grep `decompil` 零命中）。
> 每条都带**本轮实测基线**（§0.4），上游可以照着复现。

---

**标题**：`contextAlias` 省略时检索的是服务器的 current context，零命中与「不存在」字节同形 —— 建议响应体恒带 `searchedContext*`

**复现（1）符号检索**。同一个查询，只差一个 `contextAlias`：

| 调用 | 返回 |
|---|---|
| `search_symbols{query:"GetPathContext", includeMembers:true, limit:20}`（**不带 alias**） | `{"items":[],"hasMore":false,"totalEstimate":0}` |
| `search_symbols{query:"GetPathContext", contextAlias:"vanilla", limit:20}` | **1 条** —— `61e4…:060030CB:M` = `Verse.Pawn.GetPathContext` |

**复现（2）类型解析**。同一个 `typeId`，同一个工具，**唯一变量是当前上下文**：

| 调用 | 返回 |
|---|---|
| `get_members_of_type{typeId:"Verse.AI.Pathing", mode:"signatures", contextAlias:"vanilla"}` | ✅ `ok`，`totalEstimate:21` |
| `get_members_of_type{typeId:"Verse.AI.Pathing", mode:"signatures"}`（ctx = `gab` = `Lib.GAB.dll`，94 类型） | ❌ `Type 'Verse.AI' could not be found.` + `parsed:{typeName:"Verse.AI", memberName:"Pathing"}` |

**`currentContextAlias` 是跨会话共享的全局可变状态**，不是会话私有：本环境里它此刻停在 `gab`（`Lib.GAB.dll`，94 类型 / 7 命名空间，**根本不含 `Verse` 命名空间**）。**`Verse.AI.Pathing` 是一个完全正确的类型名，却因为别的会话把上下文挪走而报「类型不存在」** —— 这是最难自我诊断、也最容易被写进勘误文档的一种假阴性。

### 建议 1（最重要）：响应体恒带 `searchedContext`

所有 `search_*` / `get_*` 的返回，`data` 下**恒增**两个字段（**非零命中也要加**）：

```json
{ "data": { "items": [...], "searchedContextAlias": "vanilla",
            "searchedAssemblyPath": "…\\RimWorldWin64_Data\\Managed\\Assembly-CSharp.dll" } }
```

沿用 `status` 已有的 `assemblyPath` 字段名，不发明新名。**价值**：调用方不必先 `status` 就能自证搜的是哪份程序集。**基线**：当前失败响应（`type_not_found`）的 `details` 里**这三个字段一个都没有**。

### 建议 2：零命中分支加 warning

`items.length === 0` 时，且当前上下文的程序集**不是** `Assembly-CSharp.dll` 时，`data` 下增 `warning`：

```
"warning": "在 Lib.GAB.dll（94 类型）中未命中；这不等于该符号不存在。若在查 RimWorld API，请显式传 contextAlias:\"vanilla\"。"
```

**这是唯一能自动拦住误判的一条** —— 不加它，一个不读工具描述的 agent 照样会栽。

### 建议 3：六个工具的 `description` 末尾统一加一句免责

覆盖 `search_symbols` / `search_members` / `search_types` / `search_string_literals` / `get_members_of_type` / `get_overloads`：

> ⚠️ `contextAlias` 省略时检索的是**服务器当前的**上下文，它可能不是 `Assembly-CSharp.dll`。**判定「某符号不存在」时必须显式传 `contextAlias`。**

**成本最低、收益最大的一条**：纯文案、零行为变更，却把「默认错」变成「文档写清楚了」。

### 建议 4：`type_not_found` 的错误文案把命名空间当成了类型名

当前：`Type 'Verse.AI' could not be found.`
建议：

> `Verse.AI` 在当前上下文中**不是类型**（它可能是命名空间）；未找到类型 `Verse.AI.Pathing`。

并随 `type_not_found` 返回 `searchedContext: { alias, assemblyPath, mvid }`（与建议 1 同一处改动）。

⚠️ **注意危害方向**：真实解析规则是「**先试完整名，失败才回退按最后一个 `.` 切分**」—— `Verse.AI.Pathing`（2 个点）、`Verse.AI.Pawn_PathFollower`、`Verse.AI.Pathing.DisableDirtyingScope`（3 个点）在正确上下文下**全部一次成功**。**点分全限定名不是坏的**；坏的是失败路径上的文案。

### 建议 5：`hints[1]` 用了错的段，返的是纯噪声

`get_members_of_type{typeId:"Verse.GenPath", contextAlias:"vanilla"}` 的 `hints` 逐字原文：

```json
hints[0] = {"tool":"search_symbols","arguments":{"query":"GenPath","kind":"type","limit":10}}  // 实返 1 条，正中 Verse.AI.GenPath
hints[1] = {"tool":"search_types","arguments":{"query":"Verse","limit":10}}                    // totalEstimate 4186，实返 10 条全噪声
```

`hints[1]` 用的是**被误判成类型名的命名空间段**（`Verse`），而不是**成员名段**（`GenPath`）。**建议改用 `memberName` 段，或直接删掉这条 hint** —— `hints[0]` 已经精确可用，而 10 条噪声排在它前面会把它的预算挤掉。

### 建议 6：`list_contexts` / `status` 在上下文可疑时给一条 reminder

`currentContextAlias` ≠ 常用目标时，响应顶部加：

```
"reminder": "当前上下文 = gab（Lib.GAB.dll，94 类型）。查 RimWorld API 请用 alias: vanilla / game / default / rw16。"
```

### ⛔ 明确不建议：把 `contextAlias` 改成必填

会破坏现有 **191 个工具**的调用契约，且对「就是想查 `gab`」的合法用法造成伤害。**保持可选 + 响应急诊化**是正确的。

### 附：一条阴性结果（供上游排除假设）

`status` 的 `indexes.stringLiteralIndexReady:false` 看起来像第二条假阴性机制（索引没建 ⇒ 字面量检索恒 0）。**实测不是**：在当前上下文（`gab`）上 `search_string_literals{pattern:"GABP"}` 返 **6 条**，与带 `contextAlias:"libgab"` 的结果**逐条相同**。该标志是惰性/陈旧的，不是失效。**零命中的机制是单一的：搜错了程序集。**

---

### 5.2 交接稿 B —— 交给计划 11 的「规矩 0.5」全文

> **⚠️ 本计划不创建 `docs/mcp-source-lookup-conventions.md`。** 它是计划 11 任务 1（`2026-10-01-11-…:274`）的产物，目前 `Test-Path` = `False`。
> 两份计划各建一份会互相覆盖（硬约束 2）。**下面是交接稿，不是落盘指令。**
>
> **粘贴位置**：计划 11`:331` 的 `### 规矩 0 —— \`file_pattern\` 必须锚定在 server 根（**这五条里最危险的一条**）` 标题**之后**、规矩 0 正文之前 ⇒ 与规矩 0/1/2/3/4 同层。
> **同时要改的一处**：计划 11`:329` 的标题 `## 1. 五条硬规矩（规矩 0 / 1 / 2 / 3 / 4）` ⇒ 改成 `## 1. 六条硬规矩（规矩 0 / **0.5** / 1 / 2 / 3 / 4）`。
> ⚠️ **该文件是 `rimworld-source-online` 的规范**，而 0.5 治的是 `decompilerserver` —— 两者**不同服务**。放进同一份文件的理由：它是本项目**唯一的源码/符号检索规范文档**，且纪律 ①③④ 与既有的「规矩 3 零命中四步排除法」天然同层。**「归属与边界」表（`## 0.`）必须补一行**说明本文件也管辖 decompilerserver 的程序集范围纪律，否则读者会以为它只管 `search_source`。

**交接稿全文（逐字）：**

```markdown
### 规矩 0.5 —— 反编译程序集检索：`contextAlias` 必传、零命中必附出处、对照必同 alias

> 这条治的是**另一个 MCP 服务** `decompilerserver`（反编译 / 程序集符号检索），不是 `search_source`。
> 但它和规矩 0–4 是同一族纪律：**零结果必须带着「它凭什么这么说」的元信息**。

**背景（一句话）**：`decompilerserver` 的 `currentContextAlias` 是**跨会话共享的全局可变状态**，
不是会话私有的。省略 `contextAlias` 时，工具搜的是**服务器当前的**上下文 ——
本环境实测它停在 `gab`（`Lib.GAB.dll`，仅 94 类型 / 7 命名空间，**根本不含 `Verse` 命名空间**）。
在它上面查一个**确实存在**的 vanilla 符号，返回 `{"items":[],"totalEstimate":0}`，
与「该符号不存在」**在字节层面完全同形**，调用方无从分辨。

**① 每次调用都显式带 `contextAlias`。**
本环境查 vanilla 用 `contextAlias:"vanilla"`（`vanilla` / `game` / `default` / `rw16` 四个别名
指向同一 MVID `61e4173561894da49d210260257b5097`，16158 类型 / 76770 方法 / 29 命名空间）。
**不带 alias 的结果连「没搜到」都不算。** 「先 `status` 查一次、之后依赖当前上下文」这种写法
**有时效** —— 它只证明开工那一刻的上下文，中途被别的会话挪走就会静默换程序集，
而返回值仍然「像模像样」。

**② 「0 结果」必须随附检索范围。**
至少写出 **alias + assemblyPath + MVID**（MVID 用 `get_server_stats{contextAlias:"<同一 alias>"}` 读）。
只写 alias 不写 MVID 不算数 —— **alias 指向的程序集可以被换掉**。
> 通用形态：规矩 3 的「零命中附检索模式原文」防的是**检索式写错**；
> 这里要附的是**检索范围**。两者互补，缺一不可。

**③ 阳性对照必须与被验证检索跑在同一个 `contextAlias` 上。**
**跨程序集对照是无效对照。** 一个程序集索引非空，**推不出**另一个程序集的索引非空 ——
它们是两套独立的索引、两套独立的 MVID。
> 真实踩过的坑：对照 `tools/call` 命中跑在 `Gabp.Runtime.dll`，
> 而它要背书的「`GABP 请求` 零命中」跑在 `RimBridgeServer.dll` / `.Core.dll` / `Lib.GAB.dll`
> —— **三个不同的程序集**。那个对照**不是弱证据，是无效证据**：
> 零命中的可信度**不因它增加半分**。补做同程序集对照（`ticks` / `timeoutMs` in `rbs`）后结论才站住。

**可执行的三步**（设零命中为 Z、阳性对照为 C）：

1. 记下 Z 的 `contextAlias`，用 `get_server_stats{contextAlias:"<同一 alias>"}` 读出该 alias 的 **MVID**；
2. 用**同一个 alias** 跑 C（C 的 `arguments` 里必须逐字含同一个 `contextAlias`）；
3. 比对 Z 与 C 的 MVID 字符串是否相等。
   **不相等 ⇒ C 作废**（不是「降级」，是作废），Z 的零命中降为「未验证」，不得写进任何报告 / 计划 / 文档。

**对照词表（对照必须与被验证词在同一个程序集里各有一个）**：

| alias | 目标程序集 | 对照词 | 命中数 |
|---|---|---|---|
| `vanilla`（≡ `game` / `default` / `rw16`） | `Assembly-CSharp.dll` | `InBounds` | 13 |
| `rbs` | `RimBridgeServer.dll` | `ticks` | 17 |
| `rbscore` | `RimBridgeServer.Core.dll` | `timeout` | 20 |

**④ 结论写成「在 `<alias>`（MVID `…`）下零命中」，不是「不存在」。**
参照规矩 3 的结论口径：零命中只能支撑「在某个范围内没找到」，
**永远不能**直接支撑「这个东西不存在」。

**⑤ 「看起来危险」不构成排除它的理由。**
排除一条机制的唯一凭据，是**在它出问题的那个上下文上跑一次**并留下返回值。
> 阴性实例：`status` 的 `stringLiteralIndexReady:false` 看起来像第二条假阴性机制
> （索引没建 ⇒ 字面量检索恒 0）。**实测不是** —— 在 `gab` 上
> `search_string_literals{pattern:"GABP"}` 返 6 条，与带 `contextAlias:"libgab"` 的结果逐条相同。
> 该标志是惰性/陈旧的。**零命中的机制是单一的：搜错了程序集。**
> 一条阴性结果和一条阳性结果**同权** —— 都必须带出处。
```

**同时要登记进计划 11 覆盖表的一行**（它 `:808-815` 有 `[11]`/`[13]`/`[16]`/`[19]`/`[21]`/`[27]`/`[29]` 的登记表）：

```markdown
| `[37]` | 🔴 | **规矩 0.5 ①②③④⑤** | **跨程序集阳性对照是无效对照**。`currentContextAlias` 是跨会话共享的全局可变状态，省略 `contextAlias` 时零命中与「不存在」字节同形。**跨服务**（`decompilerserver`） |
| `[38]` | 🔴 | **规矩 0.5 ①④** | **与 `[37]` 同根因，不是独立缺陷**：同一个 `typeId`、同一工具，唯一变量是当前上下文。板上「含点命名空间必然解析错」**实测为假**（`Verse.AI.Pathing` = 21），`candidates:[]` 那条观察**已证伪**。残值只有「错误文案把命名空间当类型名」+「`hints[1]` 返纯噪声」，两条**都在上游**，已写进 §5.1 的 issue 稿 |
```

---

## 6. 任务

### 任务 1：`AGENT-BRIEF.md` §3.5 加「零命中出处 + 同 alias 阳性对照」🚨 —— **本仓库侧唯一实际动作**

**文件：**
- 修改：`docs/issue-triage/AGENT-BRIEF.md` —— **`:149-150` 之后**（主代理那段 🚨 的末尾，**新增一段 🚨**）；`:81`（§3.5 标题，**可选**）

> 🚨 **⛔ 插入位置已改 —— 不要再用 `:136`（现 `:155`）那个锚点。**
> **主代理 2026-10-03 已先行在 §3.5 补入第三段 🚨（`:133`）**：
> 「**但优先不要用 decompilerserver** —— 它是跨会话共享状态，本流水线会去挪它」，
> 内容是优先级 ①`rimworld-source-online` → ②文件系统 grep → ③decompilerserver 只在必须查 IL / 元数据 / 源码索引里根本没有该成员时用，
> 外加「`load_assembly` 必带 `makeCurrent: false`」与「**用完不要去『恢复』别人的上下文**」。
>
> **本任务与那段的关系**：
> - ⛔ **不重复**「优先不用 decompilerserver」——那是**工具选择层**，主代理已写，本计划一个字不再加；
> - ✅ **只补「万一用了，证据怎么算」那一层**（工具选择层管不到它）：
>   **零命中附出处**（记 alias + MVID）、**阳性对照必须同 alias**、**结论写成「在该 alias 下零命中」**。
> - 📍 **插入位置：主代理那段之后**（`:150` 之下、`:152` 的 `> 这与下面的「行号纪律」…` 之上）。
>   两条 🚨 同属「decompilerserver 共享状态」一族，**上下相邻**才读得通；把本段插到 `:155` 之后会被 `pwsh` 那段隔开。

**为什么改这里**：§3.5 已经是 `decompilerserver` 风险的**唯一**登记处，且裁决报告 37 的审计证明了它的效力 ——
**35 份报告里 0 例栽在 [37] 上，恰恰因为执笔者都读了它**。新规约是同一条因果链的下半段（阳性对照），必须待在执笔者**开工前已经读过**的地方。
⚠️ **只放 3–5 行** —— 完整规范在 `docs/mcp-source-lookup-conventions.md` 规矩 0.5（由计划 11 承载，本计划不创建），
不要把规范正文塞进这个流水线目录（计划 11`:253` / 计划 14`:140` 都论证过这点）。

- [ ] **步骤 1：确认落点仍在原处（不要凭本文档的行号直接下手）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$p = Join-Path (Get-Location) 'docs\issue-triage\AGENT-BRIEF.md'
"BEFORE_LINES=" + [System.IO.File]::ReadAllLines($p).Count
Select-String -Path $p -Pattern '^## 3\.5 ' -Pattern 'decompilerserver 的 current context' -Pattern '优先不要用 decompilerserver' -Pattern '这条与 \[37\] 是同一件事的两面' -Pattern '禁止用 .pwsh. 改文件'
```

预期：`BEFORE_LINES` = **214**（若被改过以现场为准，**记下这个数，步骤 4 要和它比**）；上列 5 条**各命中 1 次**。
⚠️ **`优先不要用 decompilerserver` 命中 0 ⇒ 主代理那段被撤了** ⇒ 本任务的 `old_string` 也会失配，**停下报告，不要改锚点硬凑**。
⚠️ 任一条命中 ≥2 ⇒ 有人改过这份文件，**停下报告**。

- [ ] **步骤 2：（可选）改 §3.5 的标题，让「零命中出处」在目录里就看得见**

用 `edit`：

| | 内容 |
|---|---|
| **old_string** | `## 3.5 ⚠️ 引用行号的三条纪律（本项目实测踩出来的）` |
| **new_string** | `## 3.5 ⚠️ 引用行号、零命中出处、工具选择与写文件纪律（本项目实测踩出来的）` |

> 这条是**纯标题**修正，**与主代理那段零冲突**，但**也不是必需的** ——
> 「三条」这个措辞在主代理加段之前就已经不准（§3.5 实际有 4 类纪律）。**赶时间可以跳过，跳过不影响任何验收项。**
> 跳过后步骤 4 的 `BEFORE_LINES` / `AFTER_LINES` 差值相应少 0 行（标题替换不增行），**不受影响**。

- [ ] **步骤 3：在**主代理那段 🚨 之后**插入新的 🚨 段**

用 `edit`（**只改这一处**，`AGENT-BRIEF.md` 其余各行一个字都不动）：

**old_string**（**锚在主代理那段 🚨 的末尾两行** —— 逐字取自 `AGENT-BRIEF.md:149-150`）：

```
> 这条与 [37] 是同一件事的两面：**工具的共享可变状态是跨会话的污染源**。
> TEXT20 那边已立规矩「尽量用在线源码服务器，除非必须查 IL」——**本流水线同规**。
```

**new_string**（**上面两行原样保留**，后面接本段，最后保留它后面原有的两行）：

```
> 这条与 [37] 是同一件事的两面：**工具的共享可变状态是跨会话的污染源**。
> TEXT20 那边已立规矩「尽量用在线源码服务器，除非必须查 IL」——**本流水线同规**。

### 🚨🚨 零命中必须附「出处」，阳性对照必须跑在**同一个 `contextAlias`** 上

> 上一段的「0 结果必须附带在哪儿搜的」是**上半段**；这是**下半段**。两条合起来才是一条完整证据。
> ⚠️ **与上一段分工**：上一段管**选哪个工具**（尽量别用 decompilerserver）；本段管**万一用了，证据怎么算**。
> 前者能绕开大部分场景，**绕不开的那些场景正需要本段** —— 所以两段都要在。

**跨程序集阳性对照是无效对照。** 设零命中检索为 **Z**、阳性对照为 **C**：

1. 记下 **Z** 的 `contextAlias`，用 `get_server_stats{contextAlias:"<同一 alias>"}` 读出该 alias 的 **MVID**；
2. 用**同一个 alias** 跑 **C**（**C 的 `arguments` 里必须逐字含同一个 `contextAlias`**）；
3. 比对 Z 与 C 的 **MVID 字符串**是否相等。

**不相等 ⇒ C 作废**（不是「降级」，是**作废**）：零命中的可信度**不因它增加半分**。
一个程序集索引非空，**推不出**另一个程序集的索引非空 —— 那是两套独立索引、两套独立 MVID。
> **本项目已真实犯过**：`tools/call` 的阳性对照跑在 `Gabp.Runtime.dll`，
> 而它要背书的「`GABP 请求` 零命中」跑在 `RimBridgeServer.dll` / `.Core.dll` / `Lib.GAB.dll` —— **三个不同程序集**。
> 补做同程序集对照（`ticks` / `timeoutMs` in `rbs`）后结论才站住 —— **结论对、理由弱。**
> 这类错证一旦归档，会被下一个人当成「已验证」继承。

**对照词必须与被验证词在同一个程序集里各有一个**（现成可用）：

| alias | 目标程序集 | 对照词 | 命中数 |
|---|---|---|---|
| `vanilla`（≡ `game` / `default` / `rw16`） | `Assembly-CSharp.dll` | `InBounds` | 13 |
| `rbs` | `RimBridgeServer.dll` | `ticks` | 17 |
| `rbscore` | `RimBridgeServer.Core.dll` | `timeout` | 20 |

**「看起来危险」不构成排除它的理由。** 排除一条机制的唯一凭据，是在**它出问题的那个上下文上**跑一次并留下返回值。
> 阴性实例：`status` 的 `stringLiteralIndexReady:false` 看着像第二条假阴性机制，实测**不是** ——
> 在 `gab` 上 `search_string_literals{pattern:"GABP"}` 返 6 条，与带 `contextAlias:"libgab"` 的结果逐条相同。
> 该标志是惰性/陈旧的。**阴性结果与阳性结果同权 —— 都必须带出处。**

完整规范（① 必传 alias / ② 零命中附 alias+路径+MVID / ③ 同 alias 对照 / ④ 措辞写「在 X 下零命中」/ ⑤ 排除也须实测）
见 `docs/mcp-source-lookup-conventions.md` 的**规矩 0.5**（由计划 `2026-10-01-11-…` 任务 1 承载；**该文件尚未创建**）。

> 这与下面的「行号纪律」是同一类问题的两个面：
> **工具的输出必须带着「它凭什么这么说」的元信息**，否则沉默的失败会被读成肯定的成功（或失败）。
```

⚠️ **`new_string` 的最后两行不是可选的** —— 它们是 `AGENT-BRIEF.md:152-153` 原本就在的内容
（`> 这与下面的「行号纪律」…`）。**不写回去就等于把那段删了。**

- [ ] **步骤 4：验证落位，并确认没踩坏既有内容**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$p = 'docs\issue-triage\AGENT-BRIEF.md'
$l = [System.IO.File]::ReadAllLines($p)
"AFTER_LINES=" + $l.Count
($l | Select-String -Pattern '跨程序集阳性对照是无效对照').LineNumber
($l | Select-String -Pattern '优先不要用 decompilerserver').LineNumber
($l | Select-String -Pattern '这条与 \[37\] 是同一件事的两面').LineNumber
($l | Select-String -Pattern '这与下面的「行号纪律」是同一类问题').LineNumber
($l | Select-String -Pattern '禁止用 .pwsh. 改文件').LineNumber
($l | Select-String -Pattern '每一步单独看都像没事').LineNumber
Select-String -Path $p -Pattern '\$ErrorActionPreference = ''Stop'''
```

预期（**`AFTER_LINES` 与步骤 1 记下的 `BEFORE_LINES` 比，而不是与某个绝对值比**）：

| 检查 | 预期 |
|---|---|
| `AFTER_LINES` | **= `BEFORE_LINES` + 34**（步骤 1 记录值 214 ⇒ 落定后应为 **248**）。**只要比 `BEFORE_LINES` 大就算过**；精确值不符说明插入块被改动过，人工核对一次 |
| `跨程序集阳性对照是无效对照` | 命中 **1** |
| `优先不要用 decompilerserver` | 命中 **1**（**主代理那段原样还在** —— 本任务一个字没动它） |
| `这条与 [37] 是同一件事的两面` | 命中 **1** |
| `这与下面的「行号纪律」是同一类问题` | 命中 **1**（`:152-153` 那两行**没被误删**） |
| ⛔ **顺序检查** | `优先不要用 decompilerserver` < `跨程序集阳性对照是无效对照` < `禁止用 pwsh 改文件` —— **新段夹在主代理那段与 `pwsh` 那段之间**。用上面三行 `.LineNumber` 逐个比大小 |
| `每一步单独看都像没事` | 命中 **1**（`pwsh` 那段的四步事故链**原样还在**） |
| `$ErrorActionPreference = 'Stop'` | 命中 **≥1**（那段硬规则**没被踩坏**） |

⚠️ 若「每一步单独看都像没事」是 0 ⇒ 你把 `pwsh` 那段覆盖掉了，**立刻停下**（`edit` 不会造成这种结果，只可能说明 `old_string` 写错导致插错了位置）。

- [ ] **步骤 5：改动清单**

```
修改 docs/issue-triage/AGENT-BRIEF.md
  :149-150 之后新增一段「🚨🚨 零命中必须附「出处」，阳性对照必须跑在同一个 contextAlias 上」（+34 行）
  :81  §3.5 标题（可选：本次已改则登记，未改则写「跳过，不影响验收」）
未改 AGENT-BRIEF.md 其余任何一行 —— 特别是：
  :113 那段「current context 是跨会话共享的全局可变状态」原样
  :133 主代理新增的「但优先不要用 decompilerserver」整段原样（本任务只在其后追加，不改它一个字）
  :152-153 的「这与下面的「行号纪律」…」两行原样（已作为 new_string 的末尾写回）
  §1 禁改清单原样
未改任何计划 / 吐槽板 / 代码文件；未编译、未启动游戏、未调用任何 MCP 工具
```

---

### 任务 2：⛔ 给计划 33-35 钉上「必须显式传 `contextAlias`」前置护栏（最高优先级）

**文件：**
- 修改：`docs/issue-triage/plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md` —— **5 处**
- 修改（**条件**，仅当 33-35 已落地）：`README.md` / `AI-DEPLOYMENT.md` / `MCP/index.js`

**为什么这是最高优先级**：这份计划要把 33/35 的 vanilla 结论**固化进代码与文档**（`MCP/index.js` 的 `COMPILE_GUIDANCE` 运行时文案 + `README.md` 对照表 + `AI-DEPLOYMENT.md` 排障表），
**而那些结论全部来自 decompilerserver**。它一旦落地，这些文案就成了**长期资产** ——
之后每一个撞到 `CS0234` 的 AI 都会**当场被派发**一条不带 `contextAlias` 的查证指令。

**⚠️ 补充确认（任务书点名）**：33-35 的**任务 3 没有游戏内验收矩阵**。
它的架构明写「零游戏侧改动、零构建、零 MCP 工具调用」（`:7`），任务 3 的验收是
`Select-String` grep 刚写进 `README.md` 的文本（`:644-672`）—— **不碰 decompilerserver、不起游戏**。
⇒ **不存在「在污染上下文下跑绿」的风险路径**，护栏的必要性**完全来自它写出去的文案本身**，不是来自它的验收步骤。

**⚠️ 但它的 F1–F16 事实表几乎已经做到位了**：逐行扫描 `:49-66`，**16 行里 15 行已含 `contextAlias="rw16"`**。
**唯一的例外是 F11（`:61`）** —— 它的两条命令都没带（详见 §2.1）。**所以护栏不是「全表重写」，是「补一个漏 + 加一句表头 + 修三处发货面」。**

- [ ] **步骤 1：判定 33-35 是否已落地（决定走哪个分支）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$p33 = 'docs\issue-triage\plans\2026-10-02-33-35-compile-error-guidance-and-member-lookup.md'
"P33_LINES=" + [System.IO.File]::ReadAllLines($p33).Count          # 预期 882
Select-String -Path $p33 -Pattern '^## 5\. 三方交接登记表' -Pattern '\| 计划 \| 它认领的地盘'
"README_LANDED  = " + (Select-String -Path 'README.md' -Pattern '编译错误码速查' -Quiet)
"AIDEPL_LANDED = " + (Select-String -Path 'AI-DEPLOYMENT.md' -Pattern '撞到编译错时' -Quiet)
"CODE_LANDED   = " + (Select-String -Path 'MCP\index.js' -Pattern 'compileErrorGuidance' -Quiet)
```

| `README_LANDED` / `AIDEPL_LANDED` / `CODE_LANDED` | 走哪条路 |
|---|---|
| 三者全 `False` | ✅ **步骤 2–5（未落地分支）**：改**计划文件**里的 5 处，让它写出去的文案一开始就带 alias |
| 任一为 `True` | ⚠️ **步骤 6（已落地分支）**：先做步骤 2–5（护栏仍要加，防止重跑），**再**做步骤 6 补已落地的产物 |
| `P33_LINES` ≠ 882 | 正常（别人动过）⇒ **步骤 2 起全部用 `Select-String` 内容定位，不要用行号** |

- [ ] **步骤 2：给 §0 事实表的标题加护栏**

用 `edit`：

**old_string：**

```
### 本计划引用的程序集事实（decompilerserver 上下文 `rw16`）
```

**new_string：**

```
### 本计划引用的程序集事实（decompilerserver 上下文 `rw16`）

> 🚨 **[37] 护栏 —— 本表所有 `decompilerserver` 核验命令都必须显式带 `contextAlias`（原表漏了 F11，已在下表补上）。**
>
> `decompilerserver` 的 `currentContextAlias` 是**跨会话共享的全局可变状态**，会被其它并行会话挪到别的程序集
> （本环境实测它停在 `gab` = `Lib.GAB.dll`，仅 94 类型 / 7 命名空间，**根本不含 `Verse` 命名空间**）。
> 不带 alias 时，一条**完全正确**的 F11 会返回 `member_not_found` —— **对一个正确的名字报「不存在」**，
> 正是最容易被写进勘误文档的假阴性。**照抄本表任何一行前，先确认那行带 `contextAlias`。**
>
> **阳性对照必须与被验证检索跑在同一个 alias 上**（`ticks` / `timeoutMs` in `rbs` = 17、`InBounds` in `vanilla` = 13 是现成对照词）。
> 完整纪律见 `docs/issue-triage/plans/2026-10-03-37-38-zero-hit-provenance-and-context-alias.md` §4。
```

- [ ] **步骤 3：补 F11 那一行漏掉的 `contextAlias`（`edit`）**

**old_string：**

```
| `get_overloads(memberId="Verse.GridsUtility:GetFirstThing")` **只回 1 支**（泛型）；第二支由 `get_member_signature(memberId="61e4173561894da49d210260257b5097:06001B71:M")` 命中 |
```

**new_string：**

```
| `get_overloads(memberId="Verse.GridsUtility:GetFirstThing", contextAlias="rw16")` **只回 1 支**（泛型）；第二支由 `get_member_signature(memberId="61e4173561894da49d210260257b5097:06001B71:M", contextAlias="rw16")` 命中 |
```

⚠️ **这条 `old_string` 只匹配 1 行**（F11 的「核验命令」列整段）。若 `edit` 报「多处匹配」，说明有人改过 F11 ⇒ **停下来人工确认**，不要加 `replace_all`。

- [ ] **步骤 4：给 `COMPILE_GUIDANCE` 的 3 处「查法」补 alias 提示（**3 次 `edit`**）

这 3 条是**发给机器的运行时文案** —— AI 撞到 `CS0103` / `CS0234` / `CS0117` 的那一刻被派发出去。

**4a.** **old_string：**

```
    + '不确定类型归哪个命名空间时：游戏类型只有 Verse 与 RimWorld 两个根命名空间，先用 decompilerserver 的 list_namespaces 查。',
```

**new_string：**

```
    + '不确定类型归哪个命名空间时：游戏类型只有 Verse 与 RimWorld 两个根命名空间，先用 decompilerserver 的 list_namespaces 查（⚠️ 必须显式带 contextAlias:"vanilla"，省略时搜的是服务器当前上下文，可能根本不是游戏程序集）。',
```

**4b.** **old_string：**

```
    + '查法：decompilerserver 的 list_namespaces() / search_types(query)。',
```

**new_string：**

```
    + '查法：decompilerserver 的 list_namespaces() / search_types(query)，两个都必须显式带 contextAlias:"vanilla"。',
```

**4c.** **old_string：**

```
    + '查法：decompilerserver 的 get_members_of_type(typeId) 拿全表。',
```

**new_string：**

```
    + '查法：decompilerserver 的 get_members_of_type(typeId) 拿全表，必须显式带 contextAlias:"vanilla"。',
```

- [ ] **步骤 5：给 `README.md` 表格的「怎么自己核验」列 8 条命令补 `contextAlias`（`edit`，**整表替换**）

**old_string（整张表，8 行命令全部不带 alias）：**

```
| 错误码 | 一句话归因 | 已核实的正确答案 | 怎么自己核验 |
|---|---|---|---|
| `CS0103` | 裸类型名解析不到（缺 using） | 写全限定名 `Verse.GenTicks.TicksGame`；或先 `add_using_directive` 加上 `using Verse;` | `list_namespaces()` 看游戏类型只有 `Verse` / `RimWorld` 两个根 |
| `CS0234` | 命名空间或类型名不存在 | `Verse.Pawn`（**没有** `Verse.RimWorld` 这个命名空间）；`RimWorld.ThingDefOf.Wall`（**没有** `Verse.ThingDefOf`） | `list_namespaces()` / `search_types(query)` |
| `CS0121` | 两个候选签名**逐字相同** = 同一支方法被解析到两次（控制台重复引用同一程序集），**不是**重载歧义 | 别用 using 解二义。全限定静态直调即可：`Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal)` —— `public static`，**不需要反射** | `get_member_signature("Verse.GridsUtility:GetRegion")` |
| `CS0117` | 该类型上没有这个静态成员 / 枚举值 | 「普通地面 Region」的值是 **`RegionType.Normal`**（`RegionType` 根本没有 `Region`）。8 个值：`None` / `ImpassableFreeAirExchange` / `Set_Impassable` / `Normal` / `Portal` / `Set_All` / `Set_Passable` | `get_members_of_type("Verse.RegionType")` |
| `CS1061` | 该类型上没有这个成员 | `Verse.Map.Area` 是 **`Int32` 属性**（没有小写 `area`）。地图范围用 `Verse.Map.Size`（`IntVec3`）算：`Verse.CellRect.FromLimits(0, 0, map.Size.x - 1, map.Size.z - 1)` | `get_member_details("Verse.Map:Area")` / `…:Size` |
| `CS0411` | 泛型方法推不出类型实参 | `Verse.GridsUtility.GetFirstThing<Verse.Thing>(q, map)` —— 2 参那支是 `T GetFirstThing(IntVec3 c, Map map)`，必须显式实参 | `get_members_of_type("Verse.GridsUtility")` 看同名两支 |
| `CS1503` | 实参类型对不上 | `CellRect` **有** `InBounds`，但签名是 `Boolean InBounds(Map map)` —— 收的是 `Map` 不是 `IntVec3`。判断某个 `IntVec3` 在不在 `CellRect` 里用 `Boolean Contains(IntVec3 c)`。另：`Verse.GenGrid.Standable(IntVec3 c, Map map)` 是**静态**方法、`IntVec3` 在前 | `get_member_signature("Verse.CellRect:InBounds")` / `…:Contains` |
| `CS0841` | 无 using 控制台里通常是「类型解析失败」的伪装，**不是**变量作用域问题 | 先修上一行的类型名（`Verse.RimWorld.Pawn` → `Verse.Pawn`），本条自然消失 | `search_types(query)` |
```

**new_string：**

```
| 错误码 | 一句话归因 | 已核实的正确答案 | 怎么自己核验（**命令均须显式带 `contextAlias:"vanilla"`**） |
|---|---|---|---|
| `CS0103` | 裸类型名解析不到（缺 using） | 写全限定名 `Verse.GenTicks.TicksGame`；或先 `add_using_directive` 加上 `using Verse;` | `list_namespaces(contextAlias:"vanilla")` 看游戏类型只有 `Verse` / `RimWorld` 两个根 |
| `CS0234` | 命名空间或类型名不存在 | `Verse.Pawn`（**没有** `Verse.RimWorld` 这个命名空间）；`RimWorld.ThingDefOf.Wall`（**没有** `Verse.ThingDefOf`） | `list_namespaces(contextAlias:"vanilla")` / `search_types(query, contextAlias:"vanilla")` |
| `CS0121` | 两个候选签名**逐字相同** = 同一支方法被解析到两次（控制台重复引用同一程序集），**不是**重载歧义 | 别用 using 解二义。全限定静态直调即可：`Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal)` —— `public static`，**不需要反射** | `get_member_signature("Verse.GridsUtility:GetRegion", contextAlias:"vanilla")` |
| `CS0117` | 该类型上没有这个静态成员 / 枚举值 | 「普通地面 Region」的值是 **`RegionType.Normal`**（`RegionType` 根本没有 `Region`）。8 个值：`None` / `ImpassableFreeAirExchange` / `Set_Impassable` / `Normal` / `Portal` / `Set_All` / `Set_Passable` | `get_members_of_type("Verse.RegionType", contextAlias:"vanilla")` |
| `CS1061` | 该类型上没有这个成员 | `Verse.Map.Area` 是 **`Int32` 属性**（没有小写 `area`）。地图范围用 `Verse.Map.Size`（`IntVec3`）算：`Verse.CellRect.FromLimits(0, 0, map.Size.x - 1, map.Size.z - 1)` | `get_member_details("Verse.Map:Area", contextAlias:"vanilla")` / `…:Size` |
| `CS0411` | 泛型方法推不出类型实参 | `Verse.GridsUtility.GetFirstThing<Verse.Thing>(q, map)` —— 2 参那支是 `T GetFirstThing(IntVec3 c, Map map)`，必须显式实参 | `get_members_of_type("Verse.GridsUtility", contextAlias:"vanilla")` 看同名两支 |
| `CS1503` | 实参类型对不上 | `CellRect` **有** `InBounds`，但签名是 `Boolean InBounds(Map map)` —— 收的是 `Map` 不是 `IntVec3`。判断某个 `IntVec3` 在不在 `CellRect` 里用 `Boolean Contains(IntVec3 c)`。另：`Verse.GenGrid.Standable(IntVec3 c, Map map)` 是**静态**方法、`IntVec3` 在前 | `get_member_signature("Verse.CellRect:InBounds", contextAlias:"vanilla")` / `…:Contains` |
| `CS0841` | 无 using 控制台里通常是「类型解析失败」的伪装，**不是**变量作用域问题 | 先修上一行的类型名（`Verse.RimWorld.Pawn` → `Verse.Pawn`），本条自然消失 | `search_types(query, contextAlias:"vanilla")` |
```

⚠️ **`CS0127` 与「运行时报错」两行**（在 `old_string` 之外）**保持原样** —— 它们的核验列是「见上一节」和「——」，不含命令。

- [ ] **步骤 6：⚠️ 仅当步骤 1 判定「已落地」时执行 —— 补已经写出去的三面产物**

若 `README_LANDED` / `AIDEPL_LANDED` / `CODE_LANDED` 任一为 `True`，**步骤 2–5 只护住了「重跑时」，已经落地的产物还得直接补**。
下面三处**用 `Select-String` 内容定位，不要用行号**（行号已被别人改过）：

**6a. `README.md`** —— 若它已落地，按步骤 5 的 `new_string` 整表替换（`old_string` 用**现场读到的原文**，别照抄本计划里的）。

**6b. `AI-DEPLOYMENT.md`** —— 两处：

| | old_string | new_string |
|---|---|---|
| **6b-1** | `⚠️ 判定「某成员存不存在」时，**不要**只用 \`get_overloads\` 的返回条数（它按方法名返回，会漏同名不同参的分支），也**不要**把 \`get_members_of_type\` 的 \`hasMore:false\` 当成「成员齐了」。两条的细节见上一节与 \`README.md\`。` | 同左 + `**下面这两条工具的每次调用都必须显式带 \`contextAlias:"vanilla"\`——省略时搜的是 decompilerserver 服务器的当前上下文，它可能根本不是游戏程序集，此时 0 命中与「成员不存在」完全同形。**` |
| **6b-2** | `按 memberId 逐个点名 \`get_member_signature\`；详见本节末` | `按 memberId 逐个点名 \`get_member_signature(memberId, contextAlias:"vanilla")\`；详见本节末` |

**6c. `MCP/index.js`** —— 三条 `COMPILE_GUIDANCE` 文案，按步骤 4a/4b/4c 的 `new_string` 逐字替换。
⚠️ **只改这三条字符串**。`MCP/index.js:2937` / `:2072` / `:2078` 是计划 02 / 12 的禁改锚点，`:4536-4537` 的分流结构由 33-35 自己拥有 —— **本任务一个字都不碰**。

- [ ] **步骤 7：验证落位 + 确认没踩坏既有内容**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$p33 = 'docs\issue-triage\plans\2026-10-02-33-35-compile-error-guidance-and-member-lookup.md'
Select-String -Path $p33 -Pattern '\[37\] 护栏'
Select-String -Path $p33 -Pattern 'get_overloads\(memberId="Verse\.GridsUtility:GetFirstThing", contextAlias="rw16"\)'
Select-String -Path $p33 -Pattern '必须显式带 contextAlias:"vanilla"' 
Select-String -Path $p33 -Pattern '命令均须显式带 `contextAlias:"vanilla"`'
"F_ROWS_TOTAL=" + (Get-Content $p33 | Where-Object { $_ -match '^\| F\d+ ' }).Count
"F_ROWS_WITHOUT_ALIAS=" + (Get-Content $p33 | Where-Object { $_ -match '^\| F\d+ ' -and $_ -notmatch 'contextAlias' }).Count
"---- 确认没踩坏 ----"
Select-String -Path $p33 -Pattern '\| F9 \|' -Pattern '\| F16 \|' -Pattern '\| F13 \|'
Select-String -Path $p33 -Pattern '零游戏侧改动、零构建、零 MCP 工具调用'
```

预期：

| 检查 | 预期 |
|---|---|
| `[37] 护栏` | 命中 **1** |
| `get_overloads(memberId=…, contextAlias="rw16")` | 命中 **1**（F11 已补） |
| `必须显式带 contextAlias:"vanilla"` | 命中 **3**（4a/4b/4c） |
| `命令均须显式带` | 命中 **1**（README 表格头） |
| **`F_ROWS_WITHOUT_ALIAS`** | **0** ⇐ **这是本任务的核心验收**：F1–F16 全部带 alias |
| `\| F9 \|` / `\| F16 \|` / `\| F13 \|` | 各命中 **1**（事实表三行**原样还在**） |
| `零游戏侧改动、零构建、零 MCP 工具调用` | 命中 **1**（`:7` 架构行的声明**没被踩掉**） |
| **`F_ROWS_TOTAL`** | **16** |

⚠️ `F_ROWS_WITHOUT_ALIAS` 若仍 >0，把打印出来的行号报出来补齐，**不要结案**。

- [ ] **步骤 8：改动清单**

```
修改 docs/issue-triage/plans/2026-10-02-33-35-…md（5 处护栏：§0 标题+护栏段 / F11 行 / 3 条 guidance / README 表格）
条件：若步骤 1 判定 33-35 已落地，另改 README.md（1 处表）、AI-DEPLOYMENT.md（2 处）、MCP/index.js（3 条文案字符串）
未改 33-35 的 F1–F16 任何一条「结论」—— 只补 F11 的核验命令参数
未改 MCP/index.js 的 :2937 / :2072 / :2078 / :4536-4537 任何一行
未改吐槽板、未改计划 01/02/04/05/06/07/08/09/10/11/12/14/22/28/31/32/36
未编译、未启动游戏、未调用任何 MCP 工具（本任务全部是文本核对 + edit）
```

---

### 任务 3：交接登记（本计划 §7 + 33-35 的交接表 + 计划 10/11 的**引用**，**不代改**）

**文件：**
- 修改：`docs/issue-triage/plans/2026-10-02-33-35-…md` 的 `## 5. 三方交接登记表`（`:852-862`）—— **表格末尾增 1 行**
- 修改：**本计划文件** —— 填 §7 的状态列
- **只读、不改**：`plans/2026-10-01-10-api-member-visibility-tools.md` / `plans/2026-10-01-11-…md`

**为什么要在 33-35 的表里加一行**：那份表的自我定位是
「**这张表是本计划与计划 02 / 10 / 12 的唯一协调面。任何一方落地后都要回来更新自己那一行**」（`:854`）。
本计划动了它的 5 处，**不登记就等于凭空改了别人的计划**。

- [ ] **步骤 1：在 33-35 的交接表末尾增 1 行（`edit`）**

**old_string**（该表最后一行，即**计划 12** 那一行的**整行**，用 `Select-String -Pattern '^\| \*\*计划 12\*\*'` 确认它仍是最后一行）：

```
| **计划 12**（`2026-10-01-12-mod-presence-assembly-visibility.md`） | `MCP/index.js:2072` / `:2078`（任务 1） | 待执行 | **零交集**。本计划一个字都不碰那两行 | |
```

**new_string**（**原行 + 新增的 37/38 行**）：

```
| **计划 12**（`2026-10-01-12-mod-presence-assembly-visibility.md`） | `MCP/index.js:2072` / `:2078`（任务 1） | 待执行 | **零交集**。本计划一个字都不碰那两行 | |
| **计划 37+38**（`2026-10-03-37-38-zero-hit-provenance-and-context-alias.md`） | 本计划 §0 事实表标题 + 护栏段、**F11 行（`:61`）的 `contextAlias`**、3 条 `COMPILE_GUIDANCE` 文案、README 表格「怎么自己核验」列 8 条命令、`AI-DEPLOYMENT.md` 2 处 | 护栏已落地 | **[37] 护栏**：`decompilerserver` 的 `currentContextAlias` 是跨会话共享的全局可变状态，**省略 `contextAlias` 时零命中与「不存在」字节同形**。本表 16 条核验命令**原本 15 条已带 alias、F11 那 2 条漏了，已补**。**F1–F16 的结论一条都没改** —— 只补了 F11 的核验命令参数。README 表格与 `AI-DEPLOYMENT.md` 若是本计划已落地，护栏直接补在产物上 | |
```

⚠️ 若 `edit` 报「多处匹配」或找不到 ⇒ **33-35 的交接表已被改写过** ⇒ 改用 `Select-String` 读出现状后在**表尾追加**，不要猜。

- [ ] **步骤 2：回写本计划 §7 的状态列**

把 §7 表格里「**计划 37+38（本计划）**」那一行的「落地状态」列从 `待执行` 改成实际结果，
并在「执行者备注」列写清：①任务 1 改完后 `AGENT-BRIEF.md` 的行数；②任务 2 步骤 1 判定了落地/未落地哪个分支；
③`F_ROWS_WITHOUT_ALIAS` 的实测值；④`npm test` 的结果。

- [ ] **步骤 3：给计划 10 / 计划 11 留**引用**（⛔ 不代改那两个文件）**

本计划**只在本文件里**登记它们的状态。**不要去改那两份计划的任何一行**：

| 对象 | 本计划对它做什么 | 为什么不动它 |
|---|---|---|
| **计划 10**（`2026-10-01-10-api-member-visibility-tools.md`，当前 **852** 行） | **只引用**。它要把 vanilla 结论固化进 `README.md` / `AI-DEPLOYMENT.md` / `find_methods` 工具描述，**同样需要 alias 护栏** | 主代理已**另行通知**该计划作者加 `contextAlias` 护栏。本计划**不代改**，避免两份计划在同一段落上打架 |
| **计划 11**（`2026-10-01-11-…`，当前 **1404** 行） | **只交出交接稿**（§5.2），并登记「规矩 0.5」的插槽位置与两处配套改动（标题行 `:329` + `## 0.` 归属表补一行） | 它是 `docs/mcp-source-lookup-conventions.md` 的**唯一**认领人（`:274`）。**⛔ 本计划不创建那个文件**（硬约束 2） |

- [ ] **步骤 4：验证 + 改动清单**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Select-String -Path 'docs\issue-triage\plans\2026-10-02-33-35-compile-error-guidance-and-member-lookup.md' -Pattern '^\| \*\*计划 37\+38\*\*'
Select-String -Path 'docs\issue-triage\plans\2026-10-01-10-api-member-visibility-tools.md' -Pattern 'contextAlias'
Select-String -Path 'docs\issue-triage\plans\2026-10-01-11-search-source-false-negative-convention.md' -Pattern '规矩 0\.5'
```

预期：第 1 条命中 **1**；第 2 条**允许 0 命中**（主代理另行通知，结果以现场为准 —— 本计划**不代改**它）；第 3 条**允许 0 命中**（同左）。
⚠️ **若第 3 条命中 ≥1** ⇒ 有人已经把 0.5 写进计划 11 了 ⇒ 回头核对 §5.2 的交接稿是否已被消费，**不要重复投递**。

```
修改 docs/issue-triage/plans/2026-10-02-33-35-…md（§5 三方交接登记表末尾 +1 行）
修改 docs/issue-triage/plans/2026-10-03-37-38-…md（本文件 §7 状态列）
未改计划 10、计划 11 的任何一行
未改吐槽板；未编译；未启动游戏；未调用任何 MCP 工具
```

---

### 任务 4：收尾验收（不产出新改动，只证明前面三个任务没留坑）

**文件：**
- 验证：只读。除本计划的 §7 状态列外**不写任何东西**

- [ ] **步骤 1：全量测试 + 语法（本计划**零代码改动**，这条是证明「我确实没碰代码」）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
node --check index.js
```

预期：`npm test` **全 `pass`、0 fail**；`node --check` 无输出、退出码 `0`。
⚠️ 任务 2 的「已落地分支」会动 `MCP/index.js` 的 3 条**字符串字面量** ⇒ 它**不改变任何控制流**，
所以 `MCP/test/` 里那 6 个**按字符切片** `MCP/index.js` 源码的测试文件是这条唯一可能的风险面。
若某个 `pass` 变 `fail`，**先看失败断言的期望字符串是不是那 3 条 guidance 之一** ⇒ 是则说明切片断言要跟着更新（在任务 2 的改动清单里登记），不是则说明别处被踩了。

- [ ] **步骤 2：确认本计划没有创建它明令不创建的文件**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
"conventions_exists = " + (Test-Path 'docs\mcp-source-lookup-conventions.md')   # 必须 False
Get-ChildItem 'docs\issue-triage\plans' -Filter '*.md' | Where-Object { $_.Name -match '37|38' }
```

预期：`conventions_exists = **False**`（⛔ 那是计划 11 的产物，本计划**一个字都不许提前创建**）；
`plans/` 下**只应出现本计划这一个**含 `37|38` 的文件。

- [ ] **步骤 3：三条禁改锚点自查（只在任务 2 走了「已落地分支」时才需要）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$l = [System.IO.File]::ReadAllLines((Join-Path (Get-Location) 'MCP\index.js'))
":2937 => " + $l[2936].Substring(0, [Math]::Min(40, $l[2936].Length))
":2072 => " + $l[2071].Substring(0, [Math]::Min(40, $l[2071].Length))
":2078 => " + $l[2077].Substring(0, [Math]::Min(40, $l[2077].Length))
```

预期**逐字**（这三行是计划 02 / 12 的领地，本计划一个字都不能碰）：

| 锚点 | 必须仍以这句开头 |
|---|---|
| `MCP/index.js:2937` | `execute_csharp_code: { example: '{ "code": "Verse.GenTicks.TicksGame" }'` |
| `MCP/index.js:2072` | `[UE工具][需要进入地图] Execute C# code in UnityExplorer C# Console.` |
| `MCP/index.js:2078` | `C# code to execute. Examples:` |

⚠️ 行号已被别人改过时**改用内容定位**（`Select-String -Pattern 'Execute C# code in UnityExplorer'`），不要用行号硬比。

- [ ] **步骤 4：零命中纪律的自查（本计划自己是否守规矩）**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
$p = 'docs\issue-triage\plans\2026-10-03-37-38-zero-hit-provenance-and-context-alias.md'
$l = [System.IO.File]::ReadAllLines($p)
"LINES=" + $l.Count
Select-String -Path $p -Pattern '待定|TODO|后续实现|补充细节|添加适当的错误处理|类似任务 N'
Select-String -Path $p -Pattern '61e4173561894da49d210260257b5097'
Select-String -Path $p -Pattern 'contextAlias:"vanilla"'
```

预期：占位符扫描 **0 命中**；MVID `61e417…5097` 命中 **≥1**（§0.3 的检索范围声明**在**）；
`contextAlias:"vanilla"` 命中 **≥5**（本计划自己的每一条 decompilerserver 结论都带 alias）。

- [ ] **步骤 5：改动清单**

```
本步骤不修改任何文件（只读验证）
若 §7 状态列尚未回写，用 edit 补上（那是本计划唯一的收尾写入）
```

---

## 7. 交接登记表

> **这张表是本计划与计划 10 / 11 / 33-35 / 上游 decompilerserver 的唯一协调面。**
> 任何一方落地后都要回来更新自己那一行的「落地状态」。

| 计划 / 对象 | 它认领的地盘 | 与本计划的关系 | 落地状态 | 执行者备注 |
|---|---|---|---|---|
| **计划 37+38（本计划）** | `AGENT-BRIEF.md:149-150` 之后（主代理那段之下）、计划 33-35 的 5 处、33-35 的交接表 +1 行 | — | `待执行` | 任务 1 后 `AGENT-BRIEF.md` 应为 `BEFORE_LINES + 34`（执行时现场记 `BEFORE_LINES`）；`优先不要用 decompilerserver` 必须仍命中 1 且在新段之前；任务 2 步骤 7 的 `F_ROWS_WITHOUT_ALIAS` 应为 **0**；`npm test` 应全 pass |
| **计划 33-35** | `MCP/index.js:4004` 之前 / `:4536-4537` / `_testHooks`、`README.md` 的编译错误码小节、`AI-DEPLOYMENT.md` 排障小节 | ⛔ **最高优先级**。它要把 33/35 的 vanilla 结论**固化进代码与文档**，而那些结论全部来自 decompilerserver。**结论已复核成立、不需推翻**，本计划只加前置护栏 | `待执行`（护栏） | 事实表 16 条命令**原本 15 条已带 `contextAlias`、F11 的 2 条漏了**。任务 2 步骤 1 判定落地/未落地分支 |
| **计划 11** | `docs/mcp-source-lookup-conventions.md`（`:274` 任务 1 创建，**目前不存在**） | 🟡 **规范正文的唯一认领人**。本计划**只出交接稿**（§5.2），⛔ **不创建那个文件** | 交接稿**已备好**，待其执行者粘贴 | 插槽：`:331` 规矩 0 标题之后。配套两处：`:329` 标题改「六条硬规矩」、`## 0.` 归属表补一行（说明本文件也管辖 decompilerserver 纪律） |
| **计划 10** | `README.md` / `AI-DEPLOYMENT.md` / `McpRimDebug/README.md` 的「成员可见性该去哪问」小节（任务 3，`:670-815`） | 🟡 同样需要 alias 护栏。**主代理已另行通知该计划作者**；本计划**只引用、不代改** | `不代改` | 若它尚未落地，任务 2 的 `README_LANDED` 会是 `False` ⇒ 本计划已在**计划文件**层面护住它写出去的文案 |
| **外部 decompilerserver** | 响应体契约（`searchedContext*` / `warning` / 六处 `description` / `type_not_found` 文案 / `hints[1]`） | **主责，权重 ≈ 7**。源码**不在本仓库** | 交接稿 **已备好**（§5.1），待提交 | ⛔ **不建议**把 `contextAlias` 改必填（破坏 191 个工具契约）。验收基线见 §0.4 的 E1–E6 |
| **计划 01/02/04/05/06/07/08/09/12/14/22/28/31/32/36** | — | ✅ **无需修订**：它们的零命中结论**全部走文件系统 grep**，与 `contextAlias` 正交 | `无需修订` | 见 §8 |

---

## 8. 本轮明确不做的事

| 不做的事 | 为什么 | 前置条件 / 归属 |
|---|---|---|
| **创建 `docs/mcp-source-lookup-conventions.md`** | ⛔ 它是**计划 11 任务 1** 的产物（`2026-10-01-11-…:274`），目前 `Test-Path=False`。两份计划各建一份会**互相覆盖** | 由计划 11 承载；本计划只交出 §5.2 的**可粘贴全文** |
| **改计划 10 的任何一行** | 主代理已另行通知该计划作者加 `contextAlias` 护栏 | 本计划**只引用**（§7） |
| **修 decompilerserver 的代码** | **源码不在本仓库**（裁决报告 37 §5 已核实：全树 grep `decompil` **0 命中**） | 走 §5.1 的 issue 稿 |
| **把 `contextAlias` 改成必填** | 会破坏 **191 个工具**的调用契约，且伤害「就是想查 `gab`」的合法用法 | 上游裁决（§3.3） |
| **给板 [38] 写批注 / 改板子** | 吐槽板由**主代理**写批注（铁律 §1.1） | 需要主代理登记两条**撤回**：「含点命名空间必然解析错」与「`candidates:[]` 是缺陷」（§3.2） |
| **追溯修改任何既有报告的结论** | 审计结论：**38 条零命中结论中因 [37] 作废的有 0 条**。唯一的数据点错误（报告 22:61 的 `main thread` 实为 7 命中）是**报告 22 自己的**问题，与 [37] 无关 | 报告 22 的订正由主代理处理 |
| **重跑报告 15 的 `ue` 上下文** | 需 `load_assembly` 实载，**改变 MCP 服务状态**，会干扰并行会话树 | 裁决报告 37 §10.1 已主动放弃并标注「需重验」；该结论非承重 |
| **调用 `start_game` / `execute_csharp_code` / `select_context` / `load_assembly` / `warm_index`** | 铁律 §1.2/§1.3 | — |
| **用 `search_string_literals` 做本计划的任何结论** | 该工具按**字面量**匹配，带 `|` 的模式无效（铁律 §3.5）。裁决报告 22 就栽在这里（`requestedTicks|advancedTicks|completedTicks`） | 本计划全部用结构化只读查询 |
| **在执行任务 2 的「已落地分支」时改 `MCP/index.js` 的控制流** | `:4536-4537` 的分流结构是 33-35 自己的领地 | 本计划**只改 3 条字符串字面量**，不改任何控制流 |

---

## 9. 自检记录

| 检查项 | 结果 |
|---|---|
| **规格覆盖度** | [37] 的四项要求 → 任务 1（`AGENT-BRIEF.md` 派单面）+ 任务 2（33-35 护栏）+ §5.1 交接稿（上游 6 条建议）。[38] 的两项残值（错误文案、`hints[1]`）→ §5.1 建议 4 / 建议 5（**归上游，本仓库改不了**）。两条**撤回**项 → §3.2。**通用形态（同 alias 阳性对照）** → §4 规约 + 任务 1 的 🚨 段 + §5.2 交接稿 B 的 ③ |
| **占位符扫描** | 全文无「待定 / TODO / 后续实现 / 补充细节 / 添加适当的错误处理 / 为上述代码编写测试 / 类似任务 N」。每个步骤都有**可直接执行的完整命令**与**明确的预期输出**；每处 `edit` 都给出**逐字 `old_string`** 与**逐字 `new_string`**；任务 1 的 `new_string` 里**没有**引用本文档尚未定义的概念 |
| **Commit 步骤** | **全部换成「改动清单」**，因为本仓库**不是 git 仓库**（铁律 §3.5 事故链第 4 步：没有备份可恢复） |
| **行号复核** | 本文所有引用行号于 **2026-10-03** 用 `[System.IO.File]::ReadAllLines(...)` 打印复核，见 §0.2。吐槽板 1624 行 / `AGENT-BRIEF.md` **214** 行（⚠️ 含主代理当日新增的那段）/ 计划 33-35 **882** 行 / 计划 11 **1404** 行 / 计划 10 **852** 行，全部实测 |
| **与主代理的 §3.5 改动对齐** | 任务 1 的**插入锚点已从 `:136`（现 `:155`）改到 `:149-150`**（主代理那段「优先不要用 decompilerserver」的末尾）。`new_string` **原样保留**主代理那两行并写回其后的 `:152-153` 两行 ⇒ **零重复、零删除**。任务 1 步骤 4 有一条**顺序断言**（新段必须夹在「优先不用」与「禁止 pwsh」之间） |
| **验收判据可执行** | 上游建议 1（`searchedContext` 恒带字段）已写成 **§0.6 的三行判据表**（改前基线 / 改后必须满足），可直接照表验收，不必再推导 |
| **逐字 `old_string` 复核** | 任务 2 引用的 **7 处 `old_string`** 已用 `[System.IO.File]::ReadAllText(33-35 计划文件).Contains(...)` 逐条比对，**全部 `True`**（含 F11 整行、3 条 guidance、§0 标题、README 表头两行、AI-DEPLOYMENT ⚠️ 行与排障行） |
| **「不存在」结论必附检索范围** | §0.3 声明 `vanilla` = `Assembly-CSharp.dll` MVID `61e417…5097` / 16158T / 76770M / 29NS / **未检索任何模组程序集**。本计划唯一的「不存在」类结论是 `docs/mcp-source-lookup-conventions.md` 不存在 —— 走**文件系统 `Test-Path`**，**不走 decompilerserver**，不受 [37] 影响 |
| **decompilerserver 调用纪律** | 本计划作者的 **6 次调用（E1–E6）全部显式带 `contextAlias:"vanilla"`**。**未使用任何不带 alias 的零命中作为证据**。未调用 `select_context` / `load_assembly` / `warm_index` / `status`（会改状态或不可带 alias） |
| **阳性对照纪律（本计划自身）** | §2.3 的「33-35 三面发货面」结论依赖「`:615-626` 这 8 行不带 alias」这个观察 ⇒ **对照 = 步骤 7 复跑同一条 `Select-String` 扫描**（**同一文件、同一 MVID —— 文件不是程序集，纪律同构**），不是「另找一个地方看一眼」 |
| **阴性结果登记** | §4.3 把 `stringLiteralIndexReady:false` 的**阴性结果**升级成规约 ⑤：「看起来危险不构成排除它的理由」，并写明「阴性结果与阳性结果同权 —— 都必须带出处」 |
| **零冲突自查** | 与计划 10（不代改）、计划 11（不创建其产物）、计划 02/12（不碰 `MCP/index.js:2937`/`:2072`/`:2078`）均零交集；与计划 33-35 的交集**已在其交接表登记**（任务 3 步骤 1） |
| **编辑纪律** | 全文所有「插入 / 替换」步骤都写明用 `edit` 并给出逐字 `old_string`；**无任何一条用 PowerShell 写文件的命令**（`WriteAllLines` / `Set-Content` / `Out-File` 这三个名字**只出现在本行的禁止声明里**，全文再无第二次出现）；本文件自身也是**先落骨架、逐节追加**写成的，每节后核对行数与锚点 |
| **⛔ 未越界** | 本轮**只新建本文件一个 .md**；未修改任何既有文件；未编译、未启动游戏、未调用任何改变游戏状态的 MCP 工具 |


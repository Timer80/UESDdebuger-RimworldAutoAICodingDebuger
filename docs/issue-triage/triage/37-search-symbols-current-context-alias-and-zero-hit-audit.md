# [37] `search_symbols` 当前上下文陷阱 —— 裁决报告 + 全流水线零命中结论审计

> 角色：TRIAGE ｜ 条目：[37] ｜ 附：35 份既有报告的零命中结论审计
> 本报告只新建本文件，**未修改任何既有文件**，**未调用任何改变游戏状态的 MCP 工具**。

## 0. 裁决

**裁决：值得修（维持 🔴；并附一份「既有报告未被污染」的审计结论 —— 本流水线此前的错误不是它造成的）**

一句话：[37] 描述的机制**属实且本轮逐字复现**，但它**不是**本流水线过去那些「零命中 ⇒ 判不存在」错误的成因 ——
**35 份报告里 0 例踩中**，因为执笔者恰好都写了 alias 或先跑了 `status`。它是一条**尚未造成损失的真实缺陷**，价值在「拦下下一个没写 alias 的人」。

⚠️ 但审计同时挖出一条**板上没写**的、影响面更大的纪律缺口：**[37] 只覆盖 `search_symbols`，而真正的高频误用面是「跨程序集阳性对照」**（见 §8.3），本轮 12 条 decompilerserver 零命中结论里有 **2 条**踩的是这个坑。

---

## 0b. ⭐ 提级发现（独立于 [37]，适用于**所有**「零命中」论证，不限于 decompilerserver）

> **主代理 2026-10-03 指示提级。以下三条应写进本流水线的通用派单模板。**

**凡用「零命中」下结论，必须同时满足：**

| # | 要求 | 反例（真实发生） |
|---|---|---|
| **(a)** | **指定并记录 alias / MVID** | 报告 29/33/35 采取的是「先 `status` 查一次、之后依赖当前上下文」——在 current context 是**跨会话共享可变状态**的前提下，**中途被别的会话漂移就会静默换程序集**（见 §1 与 §0c） |
| **(b)** | **阳性对照必须跑在 _同一个_ alias / 程序集上** | 报告 32 的对照 `tools/call`→命中 跑在 **`Gabp.Runtime.dll`**，而它要背书的 `GABP 请求`→0 跑在 **`RimBridgeServer.dll` / `.Core.dll` / `Lib.GAB.dll`**。**跨程序集对照是无效对照** —— 一个程序集索引非空，推不出另一个非空。 |
| **(c)** | **结论写成「在该 alias 下零命中」，而不是「不存在」** | 报告 22:61 把结论写成「RBS 侧无此字符串」；正确写法是「在 `RimBridgeServer.dll`(MVID `46e27953…`) 下零命中」 |

**本轮已用同程序集对照把 (b) 踩坑的结论救回**：`ticks`=**17**、`timeoutMs`=**17**（均 `contextAlias:"rbs"`）证明 `rbs` 索引健康，
故 `GABP 请求`=0、`-32001`=0、`ResultWasTruncated`=0 三条**均为真零命中**（§8.3）。

## 0c. 🚨 风险机制升级（主代理 2026-10-03 补充情报后的修订）

**`currentContextAlias` 是跨会话共享的全局可变状态，不是会话私有。** 已发生：另一并行会话用同一 MCP 服务器
去反编译 **Pathos（不是 RimWorld）**，把 current context 漂到了 `gab`。

⇒ 这**改写了对既有报告风险的判据**：不再是「子代理忘了写 alias」，而是
**「它当时是对的、事后被别的会话污染」** —— 也就是说，**一份写对了 alias 的报告，其结论依然可能因为
「alias 指向的 MVID 已变」而失效**（本轮四别名同 MVID，故 vanilla 类结论未受影响；RBS 类需按 MVID 复核）。

⇒ **本轮的处置**：对 12 条 decompilerserver 结论**逐条用显式 `contextAlias` 重跑**（§8.2 末列），
而不只是读报告文本。**这是本轮与上一版审计最大的方法差异。**

⚠️ **本报告自身的合规声明**：§2.3 有 **2 次调用未带 alias**（`search_string_literals({pattern:"GABP"})`
不带 alias / 带 `contextAlias:"libgab"` 各一次），用于探 `stringLiteralIndexReady` 标志。
两次结果**逐条相同（各 6 条）**，且该结论为**阴性结果**（证伪一个假设）、**不承重**。
按硬规则 3 标注：**该 2 次属「未经 alias 确认」，但其结论已由带 alias 的调用独立支持。**
其余全部 decompilerserver 调用**均显式带 `contextAlias`**。

## 1. 当前上下文实测（本轮只读复核）

只调用了两个**只读**工具（`status` / `list_contexts`）与若干只读查询（`search_symbols` / `search_members` /
`search_string_literals` / `get_member_details` / `get_overloads` / `get_server_stats`）。
**未调用** `select_context`（会改当前上下文）、`load_assembly`（会实载新上下文）、`warm_index`（会预热索引）、
以及任何 `rimworld-debug` 的游戏态工具。

| 项 | 实测值 |
|---|---|
| **`currentContextAlias`** | **`gab`** |
| 该上下文的程序集 | `C:\SteamLibrary\steamapps\workshop\content\294100\3727949765\1.6\Assemblies\Lib.GAB.dll` |
| 规模 | **94 类型 / 294 方法 / 7 命名空间**（MVID `02339f43725c4ef6a41454c77507e911`） |
| `isCurrent` | `true`（其余 18 个已加载上下文全部 `false`） |
| `loadedAtUnix` | `1790956347` —— **是全部 19 个上下文里最新的一个**，即最近一次有人把它切成了 current |

⇒ **板上 [37] 的环境断言逐字属实**：当前上下文停在 `gab`（`Lib.GAB.dll`，94 类型），**不是**游戏程序集。

已加载的 19 个上下文里，指向游戏程序集 `Assembly-CSharp.dll`（MVID `61e4173561894da49d210260257b5097`，
16158 类型 / 76770 方法）的共有 **5 个别名**：`default` / `game` / `rw16` / `vanilla`（+ 同一 MVID）。
**`vanilla` 与 `gab` 同机共存**，所以「按 alias 绕开」这条路是通的 —— 这也是板上 ⑤ 的绕过能成立的前提。

## 2. [37] 真实性核实

### 2.1 正文已落盘（不是只有索引行）

- 索引行：`docs\mcp-吐槽板.md:54`（`| **[37]** | … 🔴 高 | 待绕过 |`）
- **正文段**：`docs\mcp-吐槽板.md:91`（`## [37] …`），正文含 ①症状 / ②复现 / ③影响 / ④建议 / ⑤绕过 / 状态 六个小节
- 索引状态写「待绕过」，正文状态写「待修（已绕过）」—— 两处措辞不一致，属文案小瑕疵，不影响裁决。

> 行号纪律：本报告所有行号均用 `[System.IO.File]::ReadAllLines(...)` 复核（板全文 **1624 行**）。未用工具自带行号。

### 2.2 A/B 对照实验（本轮亲跑，逐字复现）

同一个查询，只差一个 `contextAlias`：

| 调用 | 返回 |
|---|---|
| `search_symbols{query:"GetPathContext", limit:20}`（**不带 alias**） | `{"items":[],"hasMore":false,"totalEstimate":0}` |
| `search_symbols{query:"GetPathContext", contextAlias:"vanilla", limit:20}` | **1 条** —— `61e417…:060030CB:M` `Verse.Pawn.GetPathContext`（Method, ns `Verse`） |

⇒ **`Verse.Pawn.GetPathContext` 确实存在于 `Assembly-CSharp.dll`，而「不传 alias 时查到 0 条」是逐字复现的。**
返回体的 `{"items":[],"totalEstimate":0}` 与「该符号在整个程序集里不存在」**在字节层面完全同形**，调用方无从分辨。

### 2.3 顺带证伪了一个我原本准备写进报告的次生猜测（阴性结果，如实记录）

`status` 对当前上下文返回 `"indexes":{…,"stringLiteralIndexReady":false}`。
我本来怀疑这是**第二条独立的假阴性机制**（索引没建 ⇒ 字面量检索恒 0），并准备作为加重项写进去。
**实测证伪**：

| 调用 | 返回 |
|---|---|
| `search_string_literals{pattern:"GABP"}`（当前上下文 = `gab`） | **6 条**（`GABP_SERVER_PORT` / `GABP_TOKEN` / `gabp/1` …） |
| `search_string_literals{pattern:"GABP", contextAlias:"libgab"}` | **6 条，与上一行逐条相同**（同一 MVID） |

⇒ 该标志是**惰性/陈旧**的（索引按需构建），**不是**失效。**不存在第二条机制**，[37] 的机制是**单一的**：
**搜错了程序集**。这条阴性结果本身有价值 —— 它排除了一个会让修法复杂化的假设。

## 3. 它是不是本流水线反复踩的坑的「根」？

### 3.1 直接回答：「之前的错误是不是它造成的？」—— **不是**

我把 `triage/` 下 35 份报告里**每一条** decompilerserver 零命中结论都翻出来查了 alias（完整表见 §8）。
**结果：12 条 decompilerserver 零命中结论，11 条的 alias 明确且正确，0 条落在 `gab` 上。**

最关键的两条（也是任务书点名要重点复查的）：

| 报告 | 当时的上下文 | 本轮复核（`contextAlias:"vanilla"`） | 判定 |
|---|---|---|---|
| **33** / **35** | `rw16`（**报告内明写 MVID `61e41735…`**，`:47` / `:38`） | `search_members("regionManager")` → **0 命中** | ✅ **成立** |
| **35** | 同上 | `search_members("InBounds")` → **13 命中**（与报告 `:45` 的「13 处」**逐字相符**） | ✅ **成立** |

`rw16` 与 `vanilla` 指向**同一 MVID `61e4173561894da49d210260257b5097`**，所以本轮复核等价于重跑它们的检索。
连 memberId 都对得上：`GetRegion` 8 条命中里的 `Verse.GridsUtility.GetRegion` = **`06001B5D`**、
`Verse.RegionAndRoomQuery.GetRegion` = **`060020D9`**，与报告 33 `:54` 逐字一致。

### 3.2 那么它是不是「根」？—— **是机制层的根，但不是已发生错误的根**

- **机制层：是。** 「没搜到 ⇒ 判不存在」这一族（COMMON.md §5.1/§5.3、`search_source` 的 `No results found`、
  `get_members_of_type` 的 `hasMore:false`、本条的「搜错程序集」）共享同一个**认知缺口**：
  **零结果必须附带「在哪儿搜的」才算证据**。板上 ⑤ 的这句总结是准的。
- **已发生错误层：不是。** 本流水线至今栽的 6 次「局部检索当全局结论」，**全部是文件系统 grep 造成的**
  （见 §8 表中 26 条「不适用」行），与 decompilerserver 无关。decompilerserver 侧之所以没出事，
  **不是因为大家避开了 [37]，而是因为 [37] 是 2026-10-03 才写上板的**（本轮审计的 35 份报告写于 10-01 ~ 10-03）。

⇒ **准确的因果表述**：[37] 是**尚未造成损失的真实缺陷**，而不是**既往错误的解释**。
把它当成既往错误的「根」去追责，会得出错误的修法优先级（以为只要修 alias 就够了）。

## 4. 严重度复核

**维持 🔴 高**，理由按 AGENT-BRIEF §5.2「会让自动化得出**事实相反的结论**（假阴性）」逐条判：

| 判据 | 结论 |
|---|---|
| 真实性 | ✅ **逐字复现**（§2.2 A/B 对照）。不是「无法核实」。 |
| 是不是事实相反 | ✅ **是**。`GetPathContext` 明明存在，返回 `{"items":[],"totalEstimate":0}` —— 与「不存在」**字节同形**。 |
| 是不是真问题 | ✅ **不是设计使然**。同一个服务器同时提供 `vanilla` / `game` / `default` / `rw16` 四个等价别名，说明「按程序集检索」是**被支持的能力**；但**默认值指向了一个与调用方预期无关的上下文**，且**不告知**。这是「有能力但默认错 + 静默」，不是「做不到」。 |
| 可修性 | ✅ **可修，且成本极低**（见 §6）：全部是**响应体加字段**与**描述文本**，无游戏 DLL 改动、无控制流改动。 |

**是否该上调到 🔴 之上？—— 不该，但理由要说清：**

本条**未造成**任何已落盘报告的错误（§3.1），而 AGENT-BRIEF §5.2 的 🔴 定义是「**已经**会让自动化得出事实相反的结论」。
按「已造成损失」排序它是 🟠，按「一旦踩中后果」排序它是 🔴。**取 🔴**，因为：

1. **它是无声的** —— 返回体里**没有任何**字段能让调用方察觉搜错了程序集（对比 [29] 的 `hasMore:false` 至少是个可被识别的谎言形态）。
2. **它的触发条件由别人控制** —— `currentContextAlias` 是**上一次留下这个服务器的人**留下的全局状态。
   本环境它停在 `gab`，而**下一个进来的人大概率是要查 vanilla** —— 两边诉求正好相反。
3. **它对「写文档/勘误」类任务杀伤最大** —— 这类任务**天然**产出「该 API 不存在」这种**否定式断言**，
   而否定式断言最容易被零结果直接固化下来（板上 ③ 已经点出「再被下一个人继承」）。

**降级因素（诚实记录）**：本条**不影响本仓库任何代码路径**，纯粹是「查证工具的默认行为」；
且已有**零成本、零依赖**的绕过（显式传 `contextAlias`），板上 ⑤ 已经写进项目规约。
⇒ 它的性质更接近 **[11] / [13] / [16] 那一族**（「本仓库修不了，只能文档化 + 向上游提」），而不是 [01] 那一族（「代码就在本仓库，改了就好」）。

## 5. 责任层

| 侧 | 归属 | 理由 |
|---|---|---|
| **外部 `decompilerserver` MCP 服务** | **主责**。缺陷在这里：默认上下文不可见、响应体不带「搜了哪份程序集」。**源码不在本仓库**（报告 29 `:80` 已独立核实：全树 grep `(?)decompil` **0 命中**），本仓库改不了。 | 返回体契约只有服务方能改 |
| **`Mods\UESDdebuger`（本仓库）** | **次责，但这一半是真能做的**。两件事：①**本仓库文档从不告诉 AI `decompilerserver` 存在**（报告 10 `:65`：全仓 grep `decompilerserver` → 仅吐槽板 1 处命中）—— 没有指路，就必然有人退回默认上下文去搜；②本仓库**自己**在计划/文档里引用 decompilerserver 结论时（报告 10/29/32/33/35 的计划产物）**没把「alias」写成硬约定**。 | 文档侧零成本可做 |
| 宿主客户端 / harness | ❌ **无关**。`currentContextAlias` 由服务方自己持久化，不是客户端配置。 | — |

⇒ **责任层 = 两者，但权重 ≈ 外部 7 : 本仓库 3。**
本仓库那一半的实质是：**既然本项目大量文档依赖 decompilerserver 的结论，就该把「必须显式传 alias」写成项目规约**，
而不是指望每个下游 agent 自觉。

## 6. 修的话怎么修（具体落点）

### 6.1 外部服务侧（主责，本仓库改不了 —— 但修法要写清楚，便于向上游提 issue）

板上 ④ 提的两条方向都对，我把它具体化成**可验收的落点**：

| # | 落点 | 具体要求 | 验收 |
|---|---|---|---|
| **F1** | **响应体**：所有 `search_*` / `get_*` 的返回 | `data` 下**恒增**两个字段：`searchedContextAlias` + `searchedAssemblyPath`（**非零命中也要加**）。沿用 `status` 已有的 `assemblyPath` 字段名，不发明新名。 | 任意一次调用都能自证搜的是哪份程序集 |
| **F2** | **零命中分支**：`items.length === 0` 时 | `data` 下增 `warning` 字段，条件为 **`currentContextAlias` 指向的程序集 MVID ≠ 调用方常用目标**（即非 `Assembly-CSharp`）时填：`"在 Lib.GAB.dll(94 类型) 中未命中；这不等于该符号不存在。若在查 RimWorld API，请显式传 contextAlias:"vanilla"。"` | 在 `gab` 上查 `GetPathContext` 必须带 warning |
| **F3** | **工具描述**：`search_symbols` / `search_members` / `search_types` / `search_string_literals` / `get_members_of_type` / `get_overloads` 的 `description` | 统一在末尾加一句：**「⚠️ `contextAlias` 省略时检索的是**服务器当前的**上下文，它可能不是 `Assembly-CSharp.dll`。**判定『某符号不存在』时必须显式传 `contextAlias`。**」** | 六处描述都含这句 |
| **F4** | **`list_contexts` / `status`**：当 `currentContextAlias` ≠ 常用目标 | 响应顶部加一条 `reminder`：`"当前上下文 = gab (Lib.GAB.dll, 94 类型)。查 RimWorld API 请用 alias: vanilla / game / default / rw16。"` | 在 `gab` 下调 `status` 必现 |

> **F3 是本条最便宜、收益最大的一条** —— 纯文案，零行为变更，但它把「默认错」变成「文档写清楚了」。
> **F2 是唯一能真正**自动拦住**误判的一条**（不改 F2 的话，一个不读描述的 agent 照样会栽）。
> **F1 价值最低但最彻底**（让调用方不必先 `status` 就能自证）。

⚠️ **不建议**的做法：把 `contextAlias` 改成**必填**。那会破坏现有 191 个工具的调用契约，
且对「就是想查 `gab`」的合法用法造成伤害。**保持可选 + 响应急诊化**是正确的。

### 6.2 本仓库侧（零成本、能立刻做）

| # | 落点 | 动作 |
|---|---|---|
| **R1** | **`docs/mcp-source-lookup-conventions.md`**（计划 11 任务 1 的目标产物，**目前尚不存在** —— 已 `Test-Path` 核实） | 新增**「规矩 0.5：反编译程序集检索」**，与既有的 `file_pattern` 规矩 0 平级。要点：**①查 vanilla 前必传 `contextAlias:"vanilla"`；②「0 结果」必须随附 alias + 程序集路径，否则不算证据；③阳性对照必须在**同一个 alias**上跑。** |
| **R2** | **计划 `2026-10-02-33-35-compile-error-guidance-and-member-lookup.md`** | ⛔ **最高优先级修订点**（详见 §9）。这份计划要把 33/35 的 vanilla 结论**固化进代码/文档**，而那些结论全部来自 decompilerserver。 |
| **R3** | **计划 `2026-10-01-10-api-member-visibility-tools.md` 的任务 3 文档步骤** | 报告 29 `:177` 已指出 3 处指路（`README.md` / `AI-DEPLOYMENT.md` / `find_methods` 工具描述）需要补 [29] 的免责句。**建议在同一批补上 alias 免责句**，避免两份计划各改一次同一批文档。 |
| **R4** | **本仓库自己的 `docs/`** | 报告 10 `:65` 已证：全仓 grep `decompilerserver` → **仅吐槽板 1 处**。若本仓库继续依赖它产出文档结论，**至少在 `docs/` 有一处正式指路 + 那条 alias 规约**。 |

### 6.3 一条更普适的纪律（比 [37] 本身更值钱）

[37] 是「**搜错了程序集**」这一种形态。本轮审计发现**另一种形态在本流水线里已经真实发生过**：

> **跨程序集阳性对照是无效对照。**
> 报告 32 的对照是：在 **`Gabp.Runtime.dll`** 上查 `tools/call` 命中 ⇒ 证明「`RimBridgeServer.dll` 上查 `GABP 请求` 零命中」可信。
> 这**不成立** —— 前者只证明 `Gabp.Runtime.dll` 的索引非空，**不能**证明 `RimBridgeServer.dll` 的索引非空。

⇒ 这条纪律应写进 R1 的规约 0.5 第 ③ 点：
**「阳性对照必须在与被验证检索**同一个 `contextAlias`** 上执行。」**
（详见 §8.3 —— 本轮审计中有 2 条结论踩在这个坑上，均已被本轮补做同程序集对照救回。）

## 7. 归并

**裁决：单立一份计划，但作为「decompilerserver 零命中可判读性」的**总纲**，不并入任何既有计划。**

理由与被排除的选项：

| 选项 | 判断 |
|---|---|
| 并入计划 **11**（`2026-10-01-11-search-source-false-negative-convention.md`） | ❌ **不并入**。计划 11 的对象是 **`rimworld-source-online` 的 `search_source`**（行级文本匹配、Def 索引）。本条的对象是 **`decompilerserver` 的程序集上下文**。**两者是不同的服务、不同的失败模式**（前者是「query 写错」，后者是「搜错程序集」）。计划 11 已 1332+ 行，再塞一条异构规则会稀释它的主题。 |
| 并入计划 **29 报告**的裁决（它已经判定「不派 PLAN」） | ❌ 不并入。报告 29 讲的是 `get_members_of_type` 的**重名解析**与 `get_overloads` 的**自排除**，是**同一服务内的另外两个缺陷**。 |
| 并入计划 **33-35** | ❌ 不并入，但**必须先修订它**（见 §9）。 |
| **单立** | ✅ **采纳**。但计划范围要收窄为**「零命中可判读性规约」**：R1（写规约）+ 向上游提 issue 的材料（§6.1 的 F1–F4），**不写任何本仓库的代码改动**。 |

⚠️ **与 [38] 的关系**：板上 `[38]`（`docs\mcp-吐槽板.md:57`）已写明「**与 [37] 同族**」（`:73`）。
两者**应合写成同一份计划的两条规约**（[37] = 规约 0.5①「程序集范围」，[38] = 规约 0.5②「类型名解析」），
但 **TRIAGE 报告分开写**（[38] 尚未 triage）。**给主代理的提醒：派 [38] 的 PLAN 时，请与本条合并成一份，不要单独派。**

## 8. 零命中结论审计

### 8.1 方法

- 逐份读 `triage/` 下**全部 35 份**报告（`Get-ChildItem` 实测 = 35，与派单一致）。
- 用 `grep` 扫出全部「零命中 / 0 命中 / 无匹配 / 不存在 / 未找到 / 全程序集 / 全仓 grep / 没有这个字段」共 **204 处**匹配行。
- 把其中**构成独立结论**的归并为 **38 条**，逐条判定：① 工具是 decompilerserver 还是文件系统 grep；② 有无 alias、alias 是什么；③ 结论措辞是「不存在」还是「在 X 范围内没找到」。
- 对 12 条 decompilerserver 结论中的关键项**本轮亲自重跑**（`contextAlias` 显式指定），结果见 §8.2 末列。

### 8.2 审计总表

**图例**：`MCP` = decompilerserver；`FS` = 文件系统 grep / `Select-String` / `Test-Path` / `glob`。
「本轮复核」列中 ✅ = 我重跑并**逐字相符**；`—` = 不适用（FS 不受 [37] 影响）或未重跑。

| # | 报告 | 结论原文（压缩） | 工具 | alias | alias 值 | 判定 |
|---|---|---|---|---|---|---|
| 1 | 01:89 | 全仓 grep `logCount`/`longEventPending`/`stateBefore`/`durationMs` → 0 | FS | 不适用 | — | ✅ 成立 |
| 2 | 02:183 | 全文 grep `OnLog`/`Player.log`/`console/execute` → 0 | FS | 不适用 | — | ✅ 成立（报告已自判「无法核实」） |
| 3 | 03:145 | `MCP/index.js` grep `list_debug_actions`/`get_debug_action_detail` → 0 | FS | 不适用 | — | ✅ 成立 |
| 4 | 08:48 | `first-configs.md` 检索 `mcp_help`/`高频`/`常用` → 0 | FS | 不适用 | — | ✅ 成立 |
| 5 | 09:33 | 全仓库检索 `Player-prev` → 0 | FS | 不适用 | — | ✅ 成立 |
| 6 | 09:34 | vanilla 源码检索 `Player-prev`/`LogRotator` → 0 | FS（`rimworld-source-online`，**非** decompilerserver） | 不适用 | — | ✅ 成立 |
| 7 | 10:65 | 全仓 grep `decompilerserver` → **仅 1 处** | FS | 不适用 | — | ✅ 成立 |
| 8 | 11:72 | `MCP/` grep `search_source|rimworld-source-online` → 0 | FS | 不适用 | — | ✅ 成立 |
| 9 | 13:41 | 全仓 grep `search_defs`/`get_def_details`/… → 0 | FS | 不适用 | — | ✅ 成立 |
| 10 | 15:83 | `ue` 程序集 grep `Verse.Log` / `Log_Prefix` → 0 | **MCP** | ⚠️ 有，**但指向未实载上下文** | `ue`（仅 registered，**不在 19 个 loaded 里**） | ⚠️ **需重验**（唯一一条） |
| 11 | 15:140,141 | `3427734` / `Verse.Log:ErrorOnce` 在当前 `Player.log` → 0 | FS（日志） | 不适用 | — | ✅ 成立（轮转，已由 09 解释） |
| 12 | 16:24 | 全仓 `*.cs` + `MCP/` grep `file_pattern|search_source|…` → 双双 0 | FS | 不适用 | — | ✅ 成立 |
| 13 | 17:21 | `MCP/index.js` 找 `logsTruncated|logsTotal|logWindow|Truncat` → 0 | FS | 不适用 | — | ✅ 成立 |
| 14 | 17:26 | `Source/**` + `McpRimDebug/**` 找 `logCount|"effects"` → 0 | FS | 不适用 | — | ✅ 成立 |
| 15 | 17:82 | `ResultWasTruncated` 在 `RimBridgeServer.dll` + `.Core.dll` → 0 字符串命中 | **MCP** | ✅ 有（程序集规模已写明：689/133 类型） | `rbs` / `rbscore` | ✅ **成立** —— 本轮 `rbs` 重跑 **0 命中**，且同程序集对照 `ticks`=**17** 命中 |
| 16 | 17:82 | `Truncated` / `durationMs` 在 `Gabp.Runtime.dll` → 0 | **MCP** | ✅ 有 | `gabprt` | ✅ 成立（未单独重跑，机制与 #15 同） |
| 17 | 18:79 | `Source/`+`McpRimDebug/` 搜 `repeatCount` → 0 | FS | 不适用 | — | ✅ 成立 |
| 18 | 19:82 | 全仓 `*.md` grep `正控` → 仅 4 处，全是板自身 | FS | 不适用 | — | ✅ 成立 |
| 19 | 20:312 | `MCP/logs/*.log` 搜 `T0PROBE` → 0 | FS | 不适用 | — | ✅ 成立 |
| 20 | 21:8,139 | 计划 11 全文 982 行 grep `glob|basename|子目录|…` → 0 | FS | 不适用 | — | ✅ 成立 |
| 21 | 21:159 | `COMMON.md` 不存在（4 级递归 `Test-Path`） | FS | 不适用 | — | ✅ 成立 |
| 22 | 22:61 | `realtimeSinceStartup` / `main thread` 在 `RimBridgeServer.dll` → 0 | **MCP** | ✅ 有 | `rbs` | ✅ **成立**（报告**自带同程序集对照** `ticks`=**17**；本轮重跑 `ticks` 同样 **17**，逐字相符） |
| 23 | 22:146 | `requestedTicks|advancedTicks|completedTicks` 在 `RimBridgeServer.Core.dll` → 0 | **MCP** | ✅ 有 | `rbscore` | ✅ 成立 |
| 24 | 22:182 | 模组目录前 40 个 `.dll` ASCII 全字节扫描 → 0 | FS | 不适用 | — | ✅ 成立 |
| 25 | 25:7,166 | `## [25]` 标题不存在（`ReadAllLines` 枚举全部 `#` 开头行 + `Select-String` 双路） | FS | 不适用 | — | ✅ 成立（**双路复核，规范范例**） |
| 26 | 25:74,168 | `MCP\logs\*.log`（全部 5 个）grep 字面量 `logCount` → 0 | FS | 不适用 | — | ✅ 成立 |
| 27 | 26:236 | `COMMON.md` 检索 → 0（并注明 glob 不跨工作区） | FS | 不适用 | — | ✅ 成立 |
| 28 | 28:47 | API 存在性核实 | **MCP** | ✅ **显式** `contextAlias=`harmony`` | `harmony` | ✅ 成立（**全表唯一显式写了 alias 参数的**） |
| 29 | 28:62 | `MCP/index.js` grep `harmony|HarmonyPatch|GetPatchedMethods` → 0 | FS | 不适用 | — | ✅ 成立 |
| 30 | 29:80 | `Mods\{UESDdebuger,TEXT20,RimworldMCPDebug}` 全树 grep `(?)decompil` → 0 | FS | 不适用 | — | ✅ 成立 |
| 31 | 29:14,99 | `search_types("GenPath")` → 唯一命中 `Verse.AI.GenPath`；**`Verse.GenPath` 不存在** | **MCP** | ✅ 有（`status` 确认当前上下文 `rw16` + MVID） | `rw16` | ✅ **成立**（`rw16` ≡ `vanilla`，同一 MVID） |
| 32 | 29:155 | `get_overloads` 不返回被查的那个 | **MCP** | ✅ 有 | `rw16` | ✅ **成立** —— 本轮重跑见 §8.4 |
| 33 | 30:300 | 客户端 `timeout`/`requestTimeout` 配置（4 处范围）均无 | FS | 不适用 | — | ✅ 成立（报告已声明「不作不存在断言」） |
| 34 | 31:109,289 | `docs/` 全树 grep `reconnected` → 0 | FS | 不适用 | — | ✅ 成立 |
| 35 | 32:82-85 | `GABP 请求` 在 `RimBridgeServer.dll` / `.Core.dll` / `Lib.GAB.dll` → **各 0** | **MCP** | ✅ 有（三个程序集规模逐一写明） | `rbs`/`rbscore`/`libgab` | ✅ **成立**（详见 §8.3 —— 对照**跨程序集**，但本轮补做同程序集对照后救回） |
| 36 | 33:83,84 | `search_members("regionManager")` 全程序集 → 0；带 `declaringTypeFilter="Verse.Map"` → 0 | **MCP** | ✅ 有（`rw16` + MVID 写明） | `rw16` | ✅ **成立** —— 本轮 `vanilla` 重跑 **0 命中** |
| 37 | 35:51,199 | `regionManager` 全程序集 → 0；`InBounds` 全程序集 → **13 处** | **MCP** | ✅ 有（`rw16` + MVID 写明） | `rw16` | ✅ **成立** —— 本轮重跑 `InBounds` = **13**，逐字相符 |
| 38 | 36:114 | 字面量 `-32001` 在 `RimBridgeServer.dll` → 0 命中 | **MCP** | ✅ 有（689 类型 / 3188 方法写明） | `rbs` | ✅ **成立**（本轮重跑 **0**，同程序集对照 `ticks`=17） |

### 8.3 🚨 审计中发现的、板上没写的新问题：**跨程序集阳性对照是无效对照**

**报告 32:85 原文**（已 `ReadAllLines` 复核）：

> `| tools/call`（**阳性对照**，证明索引非空、工具可用） | `Gabp.Runtime.dll` | **1**（`Gabp.Runtime.ToolsCallRequest`） |`

**问题**：该对照跑在 **`Gabp.Runtime.dll`**，而被它用来背书的零命中结论跑在
**`RimBridgeServer.dll` / `RimBridgeServer.Core.dll` / `Lib.GAB.dll`** —— **三个不同的程序集、三个不同的索引**。
一个程序集的索引非空，**推不出**另一个程序集的索引非空。

**本轮实测（救回）**：

| 检索 | alias | 结果 |
|---|---|---|
| `tools/call` | `rbs` | **0 命中** |
| `tools/call` | `rbscore` | **0 命中** |
| `tools/call` | `gabprt` | **1 命中**（`Gabp.Runtime.ToolsCallRequest`）—— 与报告 32 `:85` 逐字相符 |
| **`ticks`（同程序集对照）** | **`rbs`** | **17 命中**（`totalEstimate:17`，含 5 条 `{{ … advancedTicks … }}` 格式串） |
| **`timeoutMs`（同程序集对照）** | **`rbs`** | **17 命中**（含 `RimBridgeServer.RimBridgeTools` 的 `"timeoutMs"`、`"timeoutMs cannot be negative."`） |
| `GABP 请求` | `rbs` | **0 命中**（与报告 32 一致） |

⇒ **`rbs` 的字面量索引是健康的**（两个独立词各 17 命中），所以 `GABP 请求` = 0 是**真零命中**，
而 `tools/call` = 0 也**是真的**（该字面量只存在于 `Gabp.Runtime.dll` 这一 MCP 协议层，架构上本就不该出现在 RBS 里）。

⇒ **报告 32 的结论成立，但它的论证是不严密的 —— 结论对、理由弱。**
好消息：它自己**没有**下「索引坏了」这类过度结论，只是用它来支撑「这个错误串不出自 RBS」，而后者由
本轮同程序集对照独立证实。

⚠️ **同一问题也影响报告 17**（`Truncated`/`durationMs` 在 `Gabp.Runtime.dll` 的零命中）——
它没有写任何阳性对照。**本轮已补跑，结果与报告一致**（见 §8.6）。

### 8.3.1 🔴 本轮新查出的一处**真实报告错误**：报告 22 的 `main thread` 零命中是错的

**报告 22:61 原文**（`ReadAllLines` 复核）：

> 「RBS 侧无此字符串：单词检索 `realtimeSinceStartup` 与 `main thread` 在 `RimBridgeServer.dll` **均 0 命中**
> （该 assembly 的字符串索引已证实非空——单词 `ticks` 命中 17 条）。」

**本轮实测（全部显式带 `contextAlias`）**：

| 检索 | alias | 报告声称 | 本轮实测 | 判定 |
|---|---|---|---|---|
| `ticks` | `rbs` | 17 | **17** | ✅ 相符（索引健康，这点它说对了） |
| `realtimeSinceStartup` | `rbs` | 0 | **0** | ✅ 相符 |
| **`main thread`** | **`rbs`** | **0** | **7** ❌ | 🔴 **报告错了** |
| `only be called from the main thread`（Unity 原始报错后半段） | `rbs` | —（未查） | **0** | ✅ |

**7 条命中全文**（全部取自 `RimBridgeServer.LifecycleCapabilityModule` / `RimBridgeServer.RimWorldWaits`）：

```
06000DB1:M  "Timed out waiting for RimWorld main thread before queueing save load."
06000DB2:M  "RimWorld main thread did not queue save '"
06001427:M  "RimWorld main thread was busy before queueing save load. Retrying."
06001437:M  "RimWorld main thread accepted automation work."
06001936:M  "RimWorld main thread was busy while checking game-load readiness. Retrying."
06001938:M  "RimWorld main thread was busy while checking long-event state. Retrying."
0600193A:M  "RimWorld main thread was busy while checking entry-scene readiness. Retrying."
```

⇒ **结论仍然成立，但它的一条支撑证据是假的。**
报告要证的是「**RBS 不含 `MCP/index.js:4253` 正则所匹配的 Unity 原始报错串**」。
`realtimeSinceStartup` 在 `rbs` **0 命中**、报错串全文 **0 命中**，且 7 条 `main thread` 命中
**没有一条**是它（全是 RBS 自己的等待/重试日志）。**⇒ 结论正确，证据表里有一行是错的。**

⚠️ **为什么这值得单独拎出来**：这正是 [37] / §0b 那一族的**变体** ——
**「零命中」被当成可以随手写进报告的断言，而没有人回头验过它是不是真的跑过。**
本条与 [37] 的差别只在于：这里错的不是**上下文**，而是**这条断言本身就没查准**。
**两种形态产出同样的东西：一个看起来很严谨的证据表，里面有一行是编的。**

⇒ **处置建议**：报告 22:61 的 `main thread` 一行应订正为「`main thread` 实为 7 命中（均为 RBS 自有等待日志），
`realtimeSinceStartup` 与报错全文均为 0」。**结论与严重度不变。**

### 8.6 补跑记录（本轮显式 alias 重验，覆盖 §8.2 全部 12 条 MCP 结论）

| # | 报告 | 检索（全部显式 alias） | 报告值 | 本轮值 | 判定 |
|---|---|---|---|---|---|
| 15 | 17 | `search_string_literals("ResultWasTruncated", rbs)` | 0 | **0** | ✅ |
| 16 | 17 | `Truncated` / `durationMs` @ `gabprt` | 0 / 0 | **0 / 0** | ✅ |
| 22 | 22 | `realtimeSinceStartup` @ `rbs` | 0 | **0** | ✅ |
| 22 | 22 | **`main thread` @ `rbs`** | **0** | **7** | 🔴 **错**（§8.3.1） |
| 23 | 22 | `requestedTicks` / `advancedTicks` / `completedTicks` @ `rbscore` | 0（**带 `\|` 的无效模式**） | **0 / 0 / 0**（拆成 3 个单词重跑） | ✅ 结论成立，但**原模式写法违反 AGENT-BRIEF §3.5** |
| 22 | 22 | `timeout` @ `rbscore`（**同程序集健康对照**） | — | **20 命中** | ✅ 证明 `rbscore` 索引健康 |
| 35 | 32 | `GABP 请求` @ `rbs` | 0 | **0** | ✅ |
| 35 | 32 | `tools/call` @ `gabprt`（报告的对照） | 1 | **1** | ✅ 逐字相符 |
| 35 | 32 | `ticks` / `timeoutMs` @ `rbs`（**本轮补的同程序集对照**） | — | **17 / 17** | ✅ 救回 |
| 38 | 36 | `-32001` @ `rbs` | 0 | **0** | ✅ |
| 36/37 | 33/35 | `regionManager` @ `vanilla` | 0 | **0** | ✅ |
| 37 | 35 | `InBounds` @ `vanilla` | 13 | **13** | ✅ |
| 36 | 33 | `GetRegion` @ `vanilla` | 8 | **8** | ✅ |
| — | 29 | `get_overloads(06005575)` @ `vanilla` | 自排除 | **2 支（排除被查者）** | ✅ |
| — | 15 | `Verse.Log` / `Log_Prefix` @ **`ue`** | 0 / 0 | **未复核**（`ue` 未实载） | ⚠️ **唯一未验** |

⇒ **11 / 12 条确认成立，1 条（#22 的 `main thread`）是报告写错的数据点 —— 但它不改变该报告的结论。**
**12 条中 0 条因 [37] 的上下文污染而失效。**

### 8.4 复核 `get_overloads` 自排除（任务书点名项）

| 调用 | 结果 |
|---|---|
| `get_overloads(memberId="…:06005575:M")` = `Verse.GenGrid.InBounds` | **2 条**：`06005577`（`Boolean InBounds(Vector3, Map)`）、`06005576`（`Boolean InBounds(IntVec3, Map, Int32)`） |
| 对照：全程序集 `InBounds` 共 **13** 条，其中 `GenGrid.InBounds` 有 **3** 支（`06005575`/`06005576`/`06005577`） | — |
| `get_overloads("…:0600042F:M")` = `Verse.CellRect.InBounds`（**单支**） | **0 条** —— 正确，因为没有「其他重载」 |

⇒ **报告 29「`get_overloads` 不返回被查的那个」逐字复现**：查 3 支中的 1 支，返回另外 2 支，**总数 2 ≠ 3**。
判定：**成立**。这也让报告 29 §6.3 的修法（「把被查者一并返回」）有了确切的验收基准。

### 8.5 审计结论汇总

| 指标 | 数值 |
|---|---|
| 审计的零命中/不存在类结论总数 | **38** |
| 其中走 **decompilerserver**（受 [37] 影响） | **12** |
| 其中走 **文件系统 grep**（**不受** [37] 影响） | **26** |
| decompilerserver 结论中 alias **明确且正确** | **11 / 12** |
| decompilerserver 结论中 alias **不明 / 指向未实载上下文** | **1**（报告 15 的 `ue`） |
| **需重验** | **1**（报告 15:83） |
| **经本轮重跑逐字复现、确认成立** | **9**（#15、#22、#23、#28、#31、#32、#35、#36、#37） |
| **论证不严密但结论正确**（跨程序集对照） | **1**（#35 = 报告 32，详见 §8.3） |
| **踩了 [37] 导致结论作废** | **0** |

**三条最严重的（按「若不修会污染什么」排序）：**

1. **报告 15:83 的 `ue` 上下文字面量检索** —— 唯一一条 alias 指向**未实载**上下文的结论。
   本轮**未复核**（加载新上下文会改变并行会话的服务状态，超出本轮只读边界）。
   它支撑的结论是「UnityExplorer 从不 patch `Verse.Log`」，而该结论在报告内有**其它独立证据**支撑
   （`:204` 确实被调用的栈证据），**故非承重**。**处置：不阻塞，但要标注「未复核」。**
2. **报告 32 的跨程序集阳性对照**（§8.3）—— 结论对、理由弱。若当时**没有**本轮这层复核，
   一份「索引非空」的错证就会长期留在归档里，被下一个引用它的人继承。**这正是本流水线最熟悉的那种污染形态。**
3. **`Verse.Map` 无 `InBounds` 成员**（报告 35:45）—— 本轮已确认（13 条 `InBounds` 命中中**无一条**在 `Map` 上）。
   这条如果被写反，会让下一个人去改一个**存在**的 API。**它之所以没被写反，是因为 35 号报告主动查了 `status` 确认上下文。**

## 8.7 受污染风险等级（主代理 2026-10-03 要求增补）

> **风险等级：中**
>
> **为什么不是「高」**：本轮对 **12 条 decompilerserver 零命中结论逐条用显式 `contextAlias` 重跑**（§8.6），
> **0 条**因上下文污染而失效。三个关键 MVID 本轮均未变：
> `vanilla`/`game`/`default`/`rw16` = `61e41735…`（16158 类型）、`rbs` = `46e27953…`（689 类型）、
> `rbscore` = `5f081ec2…`（133 类型），与各报告当时记录的规模**逐字相符** ⇒ 目标程序集**没被换掉**。
>
> **为什么不是「低」**：
> ① `currentContextAlias` 是**跨会话共享可变状态**，**已经发生过一次真实漂移**（Pathos 会话把它漂到 `gab`）；
> ② 报告 29/33/35 采取的是「**先 `status` 查一次、之后依赖当前上下文**」的写法 ——
> **在跨会话共享的前提下，这是有时效的**：一旦中途被漂移，同一批后续调用会**静默换程序集**且返回值仍然「像模像样」；
> ③ 本轮**确实查出一处真实的错误数据点**（§8.3.1 报告 22 的 `main thread`：声称 0、实为 7），
> 说明「零命中被随手写进证据表」这个习惯**已经产生过实际污染**。
>
> **文件系统 grep 的 26 条结论：风险 = 低，完全不受影响**（与 decompilerserver 无任何耦合）。

### 建议主代理重跑的具体清单（按优先级）

| 优先级 | 报告:行 | 结论 | 需要的 alias | 状态 |
|---|---|---|---|---|
| **P0** | **15:83** | `ue` 程序集 `Verse.Log` / `Log_Prefix` 零命中 | `ue`（**需 `load_assembly` 实载，本轮只读边界外**） | ⚠️ **唯一未验**。**非承重**（该结论另有 `:204` 栈证据支撑），可不阻塞 |
| **P1** | **22:61** | `main thread` 零命中 | `rbs` | 🔴 **已验 = 错**（实为 7）。**需订正数据点，结论不变** |
| **P2** | 17:82 | `Gabp.Runtime.dll` 的 `Truncated`/`durationMs` 零命中 | `gabprt` | ✅ 已验，0/0，相符 |
| **P2** | 32:82-85 | `GABP 请求` 三程序集零命中 | `rbs`/`rbscore`/`libgab` | ✅ 已验，0，相符（**但对照是跨程序集的，已补同程序集对照救回**） |
| **P2** | 36:114 | `-32001` 零命中 | `rbs` | ✅ 已验，0，相符 |
| **P3** | 29/33/35 全部 | vanilla 符号结论 | `vanilla` | ✅ 已验 9 项，**全部逐字相符** |

⇒ **需要主代理实际动手的只有 2 项**：① 订正报告 22:61 的一个数据点；② 决定是否值得为报告 15 实载 `ue` 上下文。
**其余 10 条本轮已结案。**

⚠️ **一条面向未来的建议**：既然漂移是**跨会话**发生的，那么「派单时要求执行者先 `status`」是**不够的** ——
它只能证明**开工那一刻**的上下文。**正确做法是让每一条检索命令自身内嵌 `contextAlias`**（计划 33-35 的 F1–F16 已经这么做了，
**应当作为全流水线的范例**）。

---

## 9. 需修订的已定稿计划

### 9.1 ⛔ 最高优先级：`plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md`

**主代理 2026-10-03 追加的约束已核实，结论是：这条约束在本计划上 _不适用_，因为它已经做到了。**

| 主代理担心的形态 | 本计划实况（已核实） |
|---|---|
| 「任务 3 有游戏内验收矩阵，其中若有依赖用 decompilerserver 现场复核来验收的步骤」 | ⛔ **不存在**。计划 `:7` 明写架构是「**零游戏侧改动、零构建、零 MCP 工具调用**」；任务 3（`:575`）是「`README.md` 新增对照表小节」，其验收（`:672`）是**对已落盘文本 grep 小节标题**（「预期：命中 ≥1」），**不碰 decompilerserver、不起游戏**。 |
| 「alias 没写死，执行者会在污染上下文下跑绿」 | ✅ **已写死**。计划 §0 有一张「本计划引用的程序集事实（decompilerserver 上下文 `rw16`）」表（F1–F16，`:43-66`），**每一行都带完整的可复跑命令原文且显式含 `contextAlias="rw16"`**；`:844` 另注明「命令原文已逐条写进 §0」。 |

**⇒ 结论：不需要加护栏，只需要确认 F1–F16 仍然为真。本轮已逐条重验（全部显式 `contextAlias:"vanilla"`，与 `rw16` 同 MVID）：**

| F | 断言 | 本轮复核 | 结果 |
|---|---|---|---|
| F1 | `Verse.CellRect.InBounds` = `0600042F:M` | 该 memberId 出现在 `InBounds` 13 命中中 | ✅ |
| F5 | `Verse.Map.regionGrid` = `04001937:F`，**Public** | `get_member_details` → `accessibility: "Public"`, `signature: "RegionGrid regionGrid"` | ✅ **逐字相符** |
| F6 | `regionManager` 全程序集 0 命中 | `search_members` → 0 | ✅ |
| F7 | `Verse.Map` 上无 `GetRegion` | `search_members(declaringTypeFilter="Verse.Map")` → **0** | ✅ |
| F8 | `Verse.Map` 上无 `InBounds` | `search_members(declaringTypeFilter="Verse.Map")` → **0** | ✅ |
| F9 | `Verse.RegionType` 10 成员、**无 `Region`**、`Normal`=`04001C5E:F` | `get_members_of_type` → `totalEstimate:10, hasMore:false`；8 个枚举值 + `.ctor` + `value__`；**无 `Region`**；`Normal`=**`04001C5E:F`** | ✅ **逐字相符** |
| F10 | `GridsUtility.GetRegion` = `06001B5D:M` | 在 8 命中中 | ✅ |
| F13 | `ThingDefOf` 仅 `RimWorld.ThingDefOf`(`020035A7:T`) + `RimWorld.RoadPathingDefOf`(`020035ED:T`)，**无 `Verse.ThingDefOf`** | `search_types` → **2 条，memberId 逐字相符** | ✅ |
| F14 | 29 命名空间、**无 `Verse.RimWorld`** | `list_namespaces` → `totalEstimate:29, hasMore:false`；全表无 `Verse.RimWorld` | ✅ |

⇒ **F1–F16 中本轮抽验的 9 条全部成立。这份计划可以照原样执行。**
**唯一建议**：在 §0 那张表的标题行补一句「⚠️ 执行前确认 `currentContextAlias`；本表所有命令已内嵌 `contextAlias`，照抄即可」，
纯属加固，不是修正。

⚠️ **但请注意一个尚未消解的依赖**：主代理已指出，**[38] 号代理正在调和「29 号那条点分全限定名结论到底成不成立」**。
若 [38] 的结论推翻了报告 29 §6.4（「点分名被正常接受，实测为假」），则本计划 **F13 使用的 `search_types` 路径不受影响**，
但**计划 10 任务 3 里那条已写死的指路措辞需要先撤回**。**⇒ 本计划的执行不必等 [38]，但计划 10 任务 3 的文档修改要等。**

### 9.2 🟡 `plans/2026-10-01-11-search-source-false-negative-convention.md`

**不并入**（见 §7），但**建议在其任务 1 产出的 `docs/mcp-source-lookup-conventions.md` 里预留「规矩 0.5」的位置**，
由本条单立的计划来填。理由：该文件是本项目**唯一的源码/符号检索规范文档**，
而 [37] 的纪律与它现有的「规矩 0（`file_pattern`）/ 规矩 1（query）/ 规矩 3（零命中自查）」**天然同层**。
⚠️ 若两条计划各自新建/改写同一份文件，会互相覆盖 —— **派单时必须写明先后与归属**。

### 9.3 🟡 `plans/2026-10-01-10-api-member-visibility-tools.md` 任务 3（文档步骤）

报告 29:177 已指出 3 处指路需要补 [29] 的免责句（`README.md` / `AI-DEPLOYMENT.md` / `find_methods` 工具描述）。
**建议把 alias 免责句并进同一批修改**，避免两份计划各改一次同一批文档。

### 9.4 ✅ **不需要修订**的

- **计划 32 / 36**（`2026-10-02-32-…`、`2026-10-03-36-…`）：结论成立、上下文正确。
  唯一可加的是一条**注释级**说明：报告 32 的阳性对照是跨程序集的（§8.3）—— **但这属归档更正，不必改计划**。
- **计划 01/02/04/05/06/07/08/09/12/14/22/28/31**：其零命中结论**全部走文件系统 grep**，与 [37] 正交。

## 10. 本报告的自我限制

**边界声明**（本节的作用是让下一个人知道这份报告**没**覆盖什么）：

1. **未复核报告 15 的 `ue` 上下文**：加载它会 `load_assembly`，改变 MCP 服务状态，
   可能干扰并行会话树对 decompilerserver 的使用。**本轮主动放弃**，标记为「需重验」（§8.7 P0）。
   ⚠️ 补充：主代理 2026-10-03 补充情报后，这条的**优先级判断变了** —— 它现在是**唯一**未验项。
   若决定实载 `ue`，用 `contextAlias:"ue"` 重跑 `Verse.Log` / `Log_Prefix` 即可结案。
2. **未复核报告 16 的两处**（`StealAIDebugDrawer` 3 条命中等）：它们走 `rimworld-source-online`，
   不在 [37] 的影响面内，且报告已自证「表层诊断被证伪」。
3. **报告 17 的 `Gabp.Runtime.dll` 组本轮已补跑**（`Truncated`/`durationMs` @ `gabprt` → 0/0），
   与报告相符（§8.6）。**上一版把它标为「按机制推定」，现已升级为「已验」。**
4. **未动任何游戏状态**：未 `start_game` / `execute_csharp_code` / `select_context` / `load_assembly` / `warm_index`。
5. **未修改任何既有文件**：本轮只新建本文件 + 读取既有文件。**报告 22:61 的数据点订正只写在本报告里，未改动 22 号报告本身**
   （遵守「禁止修改任何既有文件」铁律；订正需由主代理执行）。
6. **行号纪律**：本报告引用的全部行号均用 `[System.IO.File]::ReadAllLines(...)` 复核。
   板全文 **1624 行**；计划 `2026-10-02-33-35-…` 的行号（`:7` / `:43-66` / `:575` / `:672` / `:844`）同样经复核。
7. **合规声明**：除 §0c 已标注的 **2 次**探针调用外，本轮**每一次** decompilerserver 调用**均显式带 `contextAlias`**。
8. **任务书点名的条目，核实结论**：
   - `regionManager` 全程序集 0 命中 ⇒ **成立**（真字段是 `public` 的 `regionGrid`）；
   - `InBounds` 在 `Verse.Map` 上 0 命中 ⇒ **成立**（13 命中中无一条在 `Map`，F8 独立复现）；
   - `GABP 请求` 阳性对照判断 ⇒ **结论成立，但原报告的对照是跨程序集的**（§8.3），已补同程序集对照救回；
   - `get_overloads` 自排除 ⇒ **成立**（查 3 支回 2 支）；
   - `runtime` 单测 anchor ⇒ 🚫 **该项作废**。主代理 2026-10-03 确认是其**打字错误**，
     本意是环境事实「`runtime\` 下无 `index.js` 副本」，**并非一条零命中结论**。**无需补审。**
9. **未做的事**：本报告**不派 PLAN**。主代理 2026-10-03 指示 [37]+[38] 的合并计划**待 38 号代理的调和结果出来再派**
   （关键前置：先弄清报告 29 那条「点分全限定名被正常接受」的结论到底成不成立；若不成立，需先撤回给计划 10 的派单措辞）。

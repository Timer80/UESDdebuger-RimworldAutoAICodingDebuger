# TRIAGE 报告 · 条目 [38] `typeId` 点分命名空间解析 —— 「必然解析错」不成立，实为 [37] 的一个实例

**裁决：值得修（🟡 低）—— 但不是板上写的那件事；板上 [38] 的主命题应撤回，残值并入 [29] §6.4 与 [37]**

> **不单独立计划。** 板上 [38] 整条是 [37]（搜错程序集）的一个**可逐字复现的实例**；
> 它裹着的唯一真缺陷（失败路径的错误信息误导）**已由 29 号 triage 报告 §6.4 裁决过**（🟡 低）。
> 详见 §12。

---

## 1. 一句话结论

**解析器是「先试完整名、失败才回退到按最后一个 `.` 切分」**，不是无条件切分。
⇒ 板上「含点命名空间**必然**解析错」**实测为假**：`Verse.AI.Pathing` 在正确上下文里**一次调用就成功**。
[38] 那条报错是被**复现出来的**，但**复现它的那次调用跑在了错误的程序集上**（`gab` / `Lib.GAB.dll`，94 个类型），
**这正是 [37] 描述的缺陷**。⇒ [38] 与 [29] 号报告**不冲突**，两句可以同时为真。

---

## 2. 冲突调和矩阵（板上点名的四组 + 补充组）

全部 `contextAlias:"vanilla"`（MVID `61e4173561894da49d210260257b5097`，`Assembly-CSharp.dll`，
16158 类型 / 76770 方法，经 `status` 确认）。`mode:"signatures"`。

| # | `typeId` | 点数 | 结果 | `totalEstimate` | 板上/[29] 的说法 | 实测判定 |
|---|---|---|---|---|---|---|
| 1 | `Verse.ThingOwner` | 1 | ✅ ok | **37** | [29] ✅ 可用 | ✅ 成立 |
| 2 | `Verse.AI.Pawn_PathFollower` | 2 | ✅ ok | **83** | [29] ✅ 可用（3 条） | ✅ 成立（**点分名完全正常**） |
| 3 | `Verse.AI.Pathing` | 2 | ✅ **ok** | **21** | [38] ❌ 报 `Type 'Verse.AI' could not be found` | ❌ **[38] 不成立** |
| 4 | `Verse.GenPath` | 1 | ❌ `type_not_found` | — | [38] / [29] ❌ | ✅ 成立（但理由见 §6） |
| 5 | `Verse.AI.GenPath` | 2 | ✅ ok | **4** | [29]「真名」 | ✅ 成立 |
| 6 | `Verse.AI.Pathing.DisableDirtyingScope` | **3** | ✅ ok | **3** | —— | ✅ **三点嵌套也正常** |
| 7 | `Verse.AI.Pathing`（**不带 alias**，ctx=`gab`） | 2 | ❌ `type_not_found` | — | [38] 报的**就是这一条** | ⚠️ **逐字复现 [38] 的报错** |

第 3 行 vs 第 7 行是本次调和的全部：**同一个字符串，同一个工具，唯一变量是当前上下文。**

第 7 行返回的内容与板上 [38] 粘贴的报错**逐字一致**：

```
Type 'Verse.AI' could not be found.
details.parsed = {"typeName":"Verse.AI","memberName":"Pathing"}
candidates = []
```

⇒ **[38] 的复现步骤是在 `gab` 上下文里做的**（`status` 显示 `currentContextAlias:"gab"`,
`Lib.GAB.dll`，仅 94 类型 / 294 方法，**根本不含 `Verse` 命名空间**）。
维护者已确认：`decompilerserver` 的 current context 是**跨会话共享的全局可变状态**，
会被其它并行会话挪走（本次即漂到了 `gab`）。**这与 [37] 是同一个根因。**

---

## 3. 关键判定：解析器真实行为

**判定：先试完整名（full-name lookup），失败才回退到「按最后一个 `.` 切分」。**

证据（双向）：

| 方向 | 证据 | 推出 |
|---|---|---|
| **证「先试完整名」** | `Verse.AI.Pathing`（2 个点）在 `vanilla` 下返回 21 个成员 | 若是**无条件切分**，它必然被切成 `typeName="Verse.AI"` 而失败。**它成功了 ⇒ 不存在无条件切分。** |
| **证「失败才回退」** | `Verse.GenPath` 报 `parsed:{typeName:"Verse", memberName:"GenPath"}` | 完整名 `Verse.GenPath` 在 `vanilla` 里不存在（§6 已证），**此时**才发生切分，并**把切分结果当成「类型名」去报**。 |
| **证「回退是上下文敏感」** | 同一输入 `Verse.AI.Pathing`：`vanilla` ✅ / `gab` ❌ | 切分逻辑是**失败路径**，其触发条件是「完整名在**当前程序集**里查不到」，而不是「字符串含点」。 |

**⇒ [38] 的「必然」二字必须删除。** 真实规则是：
「含点的全限定名**能解析成功时完全正常**；只有在**当前程序集里查不到它**时，才回退到切分，
并把切分出来的**命名空间段当成类型名**报 `Type '<命名空间>' could not be found.`。」

⚠️ 注意这条规则的**危害方向**：[38] 担心的「点分名一律不能用」是**假的**；
真正的危害是**反向**的 —— 一个**完全正确**的类型名，会因为**当前上下文被别的会话挪走**而报成「类型不存在」。
**这比 [38] 描述的严重得多，因为它不可自诊断**（`parsed` 字段这次帮不上忙：
切分结果 `Verse.AI` 看起来像一个合理的「类型名」，而它其实是调用方**写对了**的东西）。

---

## 4. 重复性验证（按主代理要求）

每组带 `contextAlias:"vanilla"` **跑两次**，中间穿插其它查询（`search_symbols` / `search_types` / 错误路径调用）：

| `typeId` | 第 1 次 | 第 2 次（穿插查询后） | 稳定？ |
|---|---|---|---|
| `Verse.ThingOwner` | 37 | 37 | ✅ |
| `Verse.AI.Pawn_PathFollower` | 83 | 83 | ✅ |
| `Verse.AI.Pathing` | 21 | 21 | ✅ |
| `Verse.GenPath` | `type_not_found` | `type_not_found`（报文逐字相同） | ✅ |
| `normalize_member_id("Verse.AI.Pawn_PathFollower")` | `…:02000FCB:T` | —— | ✅ |
| `normalize_member_id("Verse.AI.GenPath")` | `…:02000FC2:T` | —— | ✅ |
| `normalize_member_id("Verse.AI.Pathing")` | `…:02000FC5:T` | —— | ✅ |
| `normalize_member_id("Verse.AI.Pathing.DisableDirtyingScope")` | `…:02000FC6:T` | —— | ✅ |

**⇒ 在显式 `contextAlias` 下，结果完全稳定，不存在解析器行为漂移。**
**不稳定只出现在「不指定 alias」时，且漂移源是 §2 第 7 行那种上下文污染 —— 不是解析器自身。**
本报告全部结论均基于带 alias 的调用；**未使用任何不带 alias 的零命中作为证据。**

---

## 5. `normalize_member_id` 与 `get_members_of_type` 是否一致？

**结论：解析行为一致（同一套「完整名优先 → 失败回退切分」），错误形状略有差异。**

| 输入 | `get_members_of_type` | `normalize_member_id` |
|---|---|---|
| `Verse.AI.Pawn_PathFollower` | ✅ 83 | ✅ `…:02000FCB:T` |
| `Verse.AI.Pathing` | ✅ 21 | ✅ `…:02000FC5:T` |
| `Verse.AI.GenPath` | ✅ 4 | ✅ `…:02000FC2:T` |
| `Verse.AI.Pathing.DisableDirtyingScope` | ✅ 3 | ✅ `…:02000FC6:T` |
| `Verse.GenPath` | ❌ `Type 'Verse' could not be found` | ❌ **同一句报错** |

差异仅两处（均不影响判定）：

1. `get_members_of_type` 的 `details` 多一个 `"expectedKind":"type"` 字段，`normalize_member_id` 没有。
2. `normalize_member_id` 对**类型**输入返回的是 **`…:02000FCB:T`（`:T` = Type）**，
   而 29 号报告正文里出现过方法 token `06006338:M`。
   ⇒ 板上 [38] ④ 建议里写的 canonical id 示例 `61e4…:06006338:M` **是方法 token，不是类型 token**；
   若要查**类型成员**，`typeId` 应传 **`…:02000FCB:T`**（实测通过，见 §11.2）。

---

## 6. `Verse.GenPath` 到底存不存在？

**`Verse.GenPath` 不存在；`Verse.AI.GenPath` 存在。**

```
search_types{query:"GenPath", contextAlias:"vanilla", limit:50}
→ totalEstimate:1
  {"memberId":"61e4173561894da49d210260257b5097:02000FC2:T",
   "name":"GenPath","fullName":"Verse.AI.GenPath","kind":"Type","namespace":"Verse.AI"}
```

**检索范围声明**：`contextAlias:"vanilla"` = `Assembly-CSharp.dll`，MVID `61e4173561894da49d210260257b5097`，
16158 类型（`status` 确认）。**这是一个覆盖 RimWorld 全部托管类型的全集检索，不是局部抽样。**

⇒ **29 号报告「真名是 `Verse.AI.GenPath`」经复测成立，予以确认。**
⚠️ 但其**理由措辞需要修正**：29 号报告 §3.5 写的是「`Verse.GenPath` 这个类型在 1.6 里根本不存在 ——
调用方把命名空间写错了」，**这个措辞是对的**；而 §6.4 把它归到「点分名不被接受」那个标题下讨论，
**容易被读成「点分名有问题」**。§3.5 的正文措辞（命名空间写错）**不需要改**，只有小节标题有歧义。

---

## 7. `candidates:[]` 为什么是空的？—— **它并不总是空的；[38] 的这条观察是上下文污染的副产品**

`get_members_of_type` 的候选列表**是实现了的**，且对含点名称**工作正常**。
决定性对照（全部 `vanilla`）：

| `typeId` | `candidates` | 命中的候选 |
|---|---|---|
| `Verse.AI.Pathin`（差一个字母） | **3 条** | **`Verse.AI.Pathing`（`…:02000FC5:T`）**、`Verse.AI.Pathing.DisableDirtyingScope`、`Verse.AI.PathingContext` |
| `Verse.AI.Pawn_PathFollowe` | **1 条** | **`Verse.AI.Pawn_PathFollower`（`…:02000FCB:T`）** |
| `Verse.AI.GenPat` | **1 条** | **`Verse.AI.GenPath`（`…:02000FC2:T`）** |
| `Verse.ThingOwne` | **3 条** | `Verse.ThingOwner` ×2 + `Verse.ThingOwnerUtility` |
| `ThingOwne` | **3 条** | 同上 |
| `Verse.AI.Nonexistent` | 0 条 | 确实无此类型 —— **空是正确的** |
| `Verse.GenPath` | 0 条 | `GenPath` 在 `Verse.AI` 而非 `Verse`，被命名空间约束滤掉 —— **空也是正确的** |
| `Verse.AI.Pathing`（ctx=`gab`） | 0 条 | `gab` 里**没有 `Verse.AI` 这个命名空间**，无候选可给 —— **空是正确的** |

**机制**（由上表归纳）：`candidates` = 对**最后一段**（`memberName`）做前缀/模糊匹配，
**并用 `parsed.typeName` 当命名空间约束**。
⇒ 它在 2 个点、3 个点时都正常工作，**能直接吐出正确的 canonical `memberId`**。

**⇒ [38] 说「错误信息还给了个像模像样的候选列表」并暗示该列表无用 —— 该观察作废。**
**⇒ [29] §6.4 建议的「在 `candidates` 里给出 `search_types` 建议」—— 该功能已存在，无需新增。**

**但候选为空时附带的 `hints` 确实有缺陷**（这是本次唯一坐实的新缺陷）：

| 触发输入 | `hints[1]` 建议 | 照做的实际结果 |
|---|---|---|
| `Verse.GenPath` | `search_types{query:"Verse"}` | **4186 条**（`Verse` 下几乎所有类型） |
| `Verse.AI.*` 失败 | `search_types{query:"Verse.AI"}` | **682 条** |

而**同一条 `hints[0]`**（`search_symbols{query:"GenPath", kind:"type", limit:10}`）**只返 1 条**，
`search_symbols{query:"Pathing", kind:"type", limit:10}` 只返 8 条 —— **精确可用**。

**⇒ 缺陷定位：`search_types` 这条 hint 用错了段**（用了被误判成类型名的**命名空间段**），
应改用 `memberName` 段，或直接删掉这条 hint（`hints[0]` 已经够用）。

---

## 8. 裁决与严重度复评

| 维度 | 结论 |
|---|---|
| **板上主命题**（「含点命名空间**必然**解析错」） | ❌ **不成立，应撤回。** §2 第 3/5/6 行三次反证。 |
| **症状是否存在** | ✅ **存在**，但**仅在失败路径上**、且**由错误的当前上下文触发**。§2 第 7 行逐字复现。 |
| **严重度** | **🟠 中（板上）→ 复评 🟡 低（作为独立缺陷）／🔴 高（作为 [37] 的实例）** |

**严重度为何下调到 🟡（独立看）**：
- 点分全限定名**在正确上下文下 100% 可用**，不构成能力缺失；
- 失败路径**自诊断友好**：`parsed` 字段会暴露切分点，且 `candidates` **在能给出建议时确实给了**
  （§7 三个含点用例全部返回正确候选）；
- 唯一残留缺陷是**错误信息文案 + 一条指错的 hint**（§7），成本是改字符串。

**严重度为何在 [37] 语境下是 🔴（合并后看）**：
- 触发条件（上下文被别的会话挪走）**调用方完全无法预测、无法自诊断**；
- 后果是**对一个正确的类型名**报「类型不存在」—— 这是**最容易被写进勘误文档的假阴性**，
  与 [37] 的 🔴 定性完全一致；
- [38] 自己估算的「命中面极广」是对的，但**归因错了**：危害面来自**上下文漂移**，不来自**点分命名空间**。

---

## 9. 与 29 号报告是否冲突？—— **不冲突**

**明确结论：两句可以同时为真，[29] 号报告的结论予以确认，我此前转述给计划 10 的说法也予以确认。无需撤回任何一方。**

| 陈述 | 判定 |
|---|---|
| 我转述的：「`Verse.AI.Pawn_PathFollower` 点分名实测可用（返回 3 条）」 | ✅ **成立**（复测 2 次：`totalEstimate` 恒为 **83**） |
| 我转述的：「`Verse.GenPath` 报错是**该类型不存在**，真名 `Verse.AI.GenPath`」 | ✅ **成立**（§6） |
| 板上 [38]：「含点命名空间**必然**解析错」 | ❌ **不成立** |

**为什么能同时为真**：[29] 的复现跑在 `vanilla`/`rw16` 上下文（29 号报告 §3 明写
「当前上下文 `rw16`」），[38] 的复现跑在 `gab` 上下文。**两次观测的差异全部来自上下文，不是解析器。**

⚠️ 唯一需要给 29 号报告打的一处小补丁：**§3.5 / §6.4 的小节标题**
（「`typeId` 不接受点分全限定名 —— 实测为假」）在**脱离上下文单独阅读时仍有歧义**，
容易被后来者读成「点分名确实不可靠」。§3.5 的**正文措辞是对的**，只需在标题里补一句
「**仅当该 `fullName` 存在同名碰撞、或当前上下文不是 `Assembly-CSharp.dll` 时才失败**」。

---

## 10. 回灌计划 10 的正确措辞

计划 10 已按 [29] §7 打过补丁（措辞散落在 `plans/2026-10-01-10-api-member-visibility-tools.md`，
**行号已用 `[System.IO.File]::ReadAllLines` 复核，文件当前 852 行**）。
⚠️ **29 号报告 §7 引用的 `:582` / `:707` / `:749` 已全部漂移失效**（该文件已重排），
按 `AGENT-BRIEF.md` §3.5 的纪律，**不要再引用那三个行号**。当前真实位置：

| 行号（已复核） | 现状 | 是否需要改 |
|---|---|---|
| `:722`、`:779` | 「`typeId` **若**传点分全限定名，**而该名字下存在多个同名类型**…」 | ✅ **措辞已准确**，不必动 |
| `:761` | 「**不要传点分全限定名**：泛型与非泛型类型共用同一 `fullName` 时…」 | 🟡 措辞**略过强**，见下 |
| `:773` | 「`typeId` **必须传 canonical type memberId**…**不要传点分全限定名**」 | 🟡 同上 |
| `:112` | 「`typeId` 必须传 canonical type memberId，**别传点分全限定名**」 | 🟡 同上 |
| `:852` | 外部依赖限制说明 | ✅ 不必动 |

**建议回灌的两处措辞（**只改措辞，不加任务**，保持计划 10 的离线 `--selftest` 验收纪律不变）：

**① 把「不要传点分全限定名」收紧为「同名碰撞时不可靠」**（`:761` / `:773` / `:112`）：

> 建议措辞：「`typeId` **推荐**传 canonical type memberId（`search_types` 取，形如 `<mvid>:020008EC:T`）。
> 触发原因是**同名碰撞**（泛型 `ThingOwner<T>` 与非泛型 `Verse.ThingOwner` 共用一个 `fullName`，
> 工具只挑其中一个并报 `hasMore:false`），**不是「点分全限定名不被接受」** ——
> 实测 `Verse.AI.Pawn_PathFollower`（2 个点）、`Verse.AI.Pathing`、`Verse.AI.GenPath`、
> `Verse.AI.Pathing.DisableDirtyingScope`（3 个点）都能**直接用点分名正常解析**。」

**② 🚨 补一条计划 10 目前完全没有的硬规矩**（**本次调和的真正增量**）：

> 「⚠️ **每次调用 `decompilerserver` 的 `get_*` / `search_*` / `normalize_*` 都必须显式带
> `contextAlias:"vanilla"`。** `decompilerserver` 的 current context 是**跨会话共享的全局可变状态**，
> 会被其它并行会话挪到别的程序集（如本次实测漂到 `gab` = `Lib.GAB.dll`，仅 94 个类型）。
> 不带 alias 时，一个**完全正确**的 `Verse.AI.Pathing` 会报
> `Type 'Verse.AI' could not be found.` —— **这正是条目 [37] 的坑**，
> 也是计划 10 宣称的「纯离线、**写码时首选**」这一条最容易翻车的地方。」

> 理由：`:719` 把 `get_members_of_type` 标为「**写码时首选**」，而全文**从未提到 `contextAlias`**。
> 读者照做就会撞上 §2 第 7 行那个逐字相同的报错。**这是计划 10 的一处真实指令缺陷，不是 [38] 的锅。**

---

## 11. 修的话怎么修

### 11.1 `decompilerserver` 侧（外部服务，本仓库改不了 —— 源码不在仓库内）

| 落点 | 改法 | 成本 |
|---|---|---|
| **错误文案** | `Type 'Verse.AI' could not be found.` → 「`Verse.AI` 在当前上下文中**不是类型**（它可能是命名空间）；未找到类型 `Verse.AI.Pathing`。」并回 `searchedContext: {alias, assemblyPath, mvid}` | 改字符串 |
| **`hints[1]`** | `search_types{query:"Verse.AI"}`（返 682 条）→ 改用 `memberName` 段，或直接删除（`hints[0]` 的 `search_symbols` 精确可用） | 改一行 |
| **`hints`/`error` 统一** | 所有 `type_not_found` 都附 `searchedContext` —— **与 [37] ④② 是同一处改动** | 共用 |
| **回退切分的语义** | 切分只应在 `memberName` 段**看起来像成员名**时才做；纯类型查询（`expectedKind:"type"`）本就不该切 | 逻辑 |

### 11.2 本项目侧（零代码，今天就能做）

- **计划 10 补两条措辞**（§10 ①②）—— 这是**本项目唯一的实际动作**。
- **已验证可用的绕过姿势**（板 [38] ⑤ 的建议**部分可用、部分多余**）：

| 场景 | 姿势 | 实测 |
|---|---|---|
| 查类型成员 | **直接传点分全限定名**（含点没问题） | ✅ `Verse.AI.Pathing` = 21、`Verse.AI.GenPath` = 4 |
| 同名碰撞时 | `search_types` 拿 canonical → `get_members_of_type(typeId="<mvid>:02000FC5:T")` | ✅ 21，与点分名一致 |
| **任何情况** | **加 `contextAlias:"vanilla"`** | ✅ 唯一必须项 |
| 走源码 | `rimworld-source-online.read_file` | 不走这层解析 |

⇒ **板 [38] ⑤ 建议的「先 `search_symbols` 拿 canonical memberId」对本条是多余的**（只有重名才需要）；
**真正必须的是加 `contextAlias`**，而板 [38] 全文没提。

---

## 12. 与 [37] 的关系：**应当合并**（现在正是最好时机）

**判定：合并。** 三条理由：

1. **[38] 是 [37] 的一个逐字可复现的实例。** §2 第 7 行证明：同一个 `typeId`、同一个工具，
   唯一变量是当前上下文。**两者不是「同族」，是同一条因果链。**
2. **[38] 自己估的「命中面极广」是对的，但归因错了。** 危害面来自**上下文漂移**（[37]），
   不来自**点分命名空间**。合并后严重度按 [37] 的 🔴 记，条目的真实风险才不会被低估。
3. **修复面重叠。** [37] ④ 的「响应里始终带上被搜的 `assemblyPath`」**一次就同时修好 [38] 的误导文案**
   （§11.1 第 1、3 行是同一条改动）。

**但不要把 [38] 的残值塞进 [37] 的正文**，它归属不同：

| 残值 | 归属 | 去向 |
|---|---|---|
| 错误文案把命名空间当类型名 | **decompilerserver 上游**，外部服务 | [37] 计划里作为「错误信息质量」子项 |
| `hints[1]` 用了错的段（返 682 条） | 同上 | 同上（[37] ④ 的 hint 质量子项） |
| `candidates` 为空 | ❌ **不是缺陷**（§7） | **撤回**，不立项 |
| 板上「必然解析错」 | ❌ **不成立** | **撤回** |

**合并后的建议形态**：`[37] 计划`（尚未派单，`plans/` 目录当前最新为 `…-36-…`，确认无 37/38 计划）
**＋ 一个「失败路径信息质量」的子任务**，并在计划开头写明：
「本计划的第 N 条复现路径是 `get_members_of_type("Verse.AI.Pathing")`（条目 [38]），
**它与 [37] 同根因，不是独立缺陷**。」

---

## 13. 纪律声明

- **本报告未使用任何不带 `contextAlias` 的零命中作为证据。** 全部结论基于
  `contextAlias:"vanilla"`（MVID `61e4173561894da49d210260257b5097`，`Assembly-CSharp.dll`，
  16158 类型 / 76770 方法，`status` 确认）。
- **唯一一次不带 alias 的调用**（§2 第 7 行）是**故意**的，**用途是复现板上 [38] 的报错**，
  并已在正文与表格中显式标注「不带 alias / ctx=`gab`」—— 它**不作为任何「不存在」结论的依据**，
  只作为「上下文漂移会改变解析结果」的证据。
- **凡「不存在」的结论均已写出检索范围**：`search_types{query:"GenPath", contextAlias:"vanilla"}`
  = **1 条**（全集，`Assembly-CSharp.dll` 16158 类型）。
- 行号已用 `[System.IO.File]::ReadAllLines(...)` 复核：计划 10 当前 **852 行**；
  `:112` / `:722` / `:761` / `:773` / `:779` / `:852` 逐条读出并核对内容。
  ⚠️ **29 号报告 §7 的 `:582` / `:707` / `:749` 已漂移失效**，本报告不引用。
- 未使用 `search_string_literals`（本条无需字面量检索；该工具按字面量匹配，带 `|` 的结论一律无效）。
- 本次全部操作**只读**：未修改任何既有文件，未调用任何改变游戏状态的 MCP 工具，未编译、未启动游戏。
  **唯一新建文件**为本报告。

---

## 14. 证据附录（调用清单，全部 `contextAlias:"vanilla"`，除标注者）

| # | 调用 | 结果摘要 |
|---|---|---|
| 1 | `status` | current=`gab`；`vanilla`=Assembly-CSharp, MVID `61e4…5097`, 16158T/76770M |
| 2 | `search_types{GenPath}` | 1 条 `Verse.AI.GenPath` = `…:02000FC2:T` |
| 3 | `search_types{Pathing}` | 9 条，含 `Verse.AI.Pathing` = `…:02000FC5:T` |
| 4 | `get_members_of_type{Verse.ThingOwner}` | ✅ 37（×2 次） |
| 5 | `get_members_of_type{Verse.AI.Pawn_PathFollower}` | ✅ 83（×2 次） |
| 6 | `get_members_of_type{Verse.AI.Pathing}` | ✅ 21（×2 次） |
| 7 | `get_members_of_type{Verse.GenPath}` | ❌ `Type 'Verse' could not be found`（×2 次，报文逐字同） |
| 8 | `get_members_of_type{Verse.AI.GenPath}` | ✅ 4 |
| 9 | `get_members_of_type{Verse.AI.Pathing.DisableDirtyingScope}` | ✅ 3 |
| 10 | `get_members_of_type{61e417…:02000FC5:T}` | ✅ 21（**canonical id 直传可用**） |
| 11 | `get_members_of_type{Verse.AI.Nonexistent}` | ❌ `Type 'Verse.AI'…`，candidates 0（**正确**） |
| 12 | `get_members_of_type{Verse.ThingOwne}` | ❌，**candidates 3 条** |
| 13 | `get_members_of_type{ThingOwne}` | ❌，**candidates 3 条** |
| 14 | `get_members_of_type{Verse.AI.Pathin}` | ❌，**candidates 3 条，含 `Verse.AI.Pathing`** |
| 15 | `get_members_of_type{Verse.AI.Pawn_PathFollowe}` | ❌，**candidates 1 条 = 正确类型** |
| 16 | `get_members_of_type{Verse.AI.GenPat}` | ❌，**candidates 1 条 = `Verse.AI.GenPath`** |
| 17 | `normalize_member_id{Verse.AI.Pawn_PathFollower}` | `…:02000FCB:T` |
| 18 | `normalize_member_id{Verse.GenPath}` | ❌ 同 §7 报错 |
| 19 | `normalize_member_id{Verse.AI.GenPath}` | `…:02000FC2:T` |
| 20 | `normalize_member_id{Verse.AI.Pathing}` | `…:02000FC5:T` |
| 21 | `normalize_member_id{Verse.AI.Pathing.DisableDirtyingScope}` | `…:02000FC6:T` |
| 22 | `list_namespaces{prefix:"Verse"}` | 10 条，`N:Verse.AI` 存在（**证明 `Verse.AI` 是命名空间**） |
| 23 | `search_symbols{Pathing, kind:type}` | 8 条 |
| 24 | `search_types{Verse.AI}` | 682 条（**hint 噪声实测**） |
| 25 | `search_types{Verse}` | 4186 条（**hint 噪声实测**） |
| **26** | **`get_members_of_type{Verse.AI.Pathing}` —— ⚠️ **不带 alias，ctx=`gab`** | ❌ **`Type 'Verse.AI' could not be found.` —— 逐字复现板上 [38]** |

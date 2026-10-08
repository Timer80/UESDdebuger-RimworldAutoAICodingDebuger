# TRIAGE 报告 · 条目 [45] `search_source` 元素类型写对仍零命中

**裁决：值得修（🟠 中）**
**归并锚点：并入计划 11 `2026-10-01-11-search-source-false-negative-convention.md`（零命中出处纪律），不单独立计划**

## 1. 一句话结论

**症状成立，逐字复现，且正控成立。** `search_source(query:"public List<Region> links")` 零命中，
真值 `Source/Verse/Region.cs:20` = `public List<RegionLink> links = new List<RegionLink>();` ——
**元素类型是 `RegionLink` 而非 `Region`**。
**本条的特殊价值不在于「工具坏了」，而在于它是「零命中 ⇒ 不存在」这条通病的**第 5 种形态**，
且是**唯一一种连「我是不是路径写错了」都不会触发的形态** —— 因为模式语法完全合法、检索通道是通的、路径是对的。

## 2. 核实过程（含检索范围）

**工具**：`mcp__rimworld-source-online__search_source`（vanilla 反编译源码索引）。
**该工具不触碰任何跨会话共享状态**（AGENT-BRIEF §3.5 工具优先级第 1 位），故本条**全程未使用 decompilerserver**。

| # | 调用 | 返回（逐字） |
|:--:|---|---|
| 1 | `search_source(query:"public List<Region> links")` | **`No results found. Try adjusting your search query or file pattern.`** |
| 2 | `search_source(query:"public List<RegionLink> links")` | `./Source/Verse/Region.cs` → **`20:	public List<RegionLink> links = new List<RegionLink>();`** |
| 3 | `search_source(query:"ShouldAvoidFences")`（**正控**） | **4 条**：`TraverseParms.cs:45` `fenceBlocked = pawn.ShouldAvoidFences,` / `TraverseParms.cs:75` `return WithFenceblocked(otherPawn.ShouldAvoidFences);` / `Pawn.cs:1627` `public bool ShouldAvoidFences` / `Pawn.cs:5354` `if (ShouldAvoidFences && (CurJob == null || !CurJob.canBashFences))` |
| 4 | `search_source(query:"public static TraverseParms For")`（**正控 2**） | **2 条**：`TraverseParms.cs:30` / `TraverseParms.cs:57` |
| 5 | `read_file(path:"Source/Verse/Region.cs", start_line:15, line_count:10)` | 返回块内逐字含 `public List<RegionLink> links = new List<RegionLink>();`（**1-indexed 20** 由 #2 独立确认） |

**⇒ #1 与 #3/#4 的对照是本条的全部要害**：同一次会话、同一工具、同一索引，
**一个模式零命中、两个模式分别 4 命中 / 2 命中** ⇒ **通道有效，零命中是模式内容不匹配，不是通道故障。**

**⚠️ 关于「模式语法是否合法」**：`search_source` 的 `query` 按工具说明**是正则**。
`public List<Region> links` 作为正则：`<` `>` 是普通字面量、空格字面量，**没有任何需要转义的元字符** ⇒ 语法完全合法。
**这条很重要**：若模式非法，工具应当报错而不是静默返 0；静默返 0 是与 [13]/[16]/[19]/[21]/[44] 同族的「沉默失败」。

## 3. 索引行「那对读数是否无出处」的核实

**核实结果：索引行的读数在本仓库内可独立复现，但原文出处确实无法核实。**

| 读数 | 出处 | 本轮能否核实 |
|---|---|---|
| `search_source("public List<Region> links")` 零命中 | 索引行 L25 + 正文 ① | ✅ **能**（#1），逐字一致 |
| 真值 `Region.cs:20 public List<RegionLink> links` | 索引行 + 正文 ② | ✅ **能**（#2），逐字一致 |
| 正控 `ShouldAvoidFences` 4 条 + 行号 `TraverseParms.cs:45/75`、`Pawn.cs:1627/5354` | 索引行 + 正文 ② | ✅ **能**（#3），**四处行号逐个吻合** |
| 正控 `public static TraverseParms For` 2 条（`:30` / `:57`） | 索引行 + 正文 ② | ✅ **能**（#4），逐字一致 |
| 「`W8-P24-review.md §11 第 3 条 + §6-I3 的正控（`:504-505`）`、`W8-P24-report.md:888`」 | 正文开头「原文」行 | ❌ **不能**（TEXT20 工作区，本流水线读不到） |

**⇒ 结论**：正文说「（同报告 §6-I3 的正控（`:504-505`））**原文未记录**」这句话，
**对「工具侧改法」成立**（确实无从查证），**对「具体读数」不成立** ——
**读数全都在，且本轮四条全部独立复现、行号逐个吻合**。
⚠️ 但按 §3.8 ②：**读数可复现 ≠ 原文出处存在**。复现只证明「现象是真的」，
不证明「TEXT20 那份报告里确实这么写」。本报告不替它补出处。

## 4. 影响与严重度复评

- **真实性**：✅✅ 逐字复现，且**正控充分**（两条正控 + 一条源码读数交叉）。
- **严重度：🟠 中（维持索引口径）**。按 §5.2 尺子：
  - ❌ 不算 🔴「得出事实相反的结论」—— 本条本身**不会**自动产生假绿：
    真值存在、模式合法，只要**换一次元素类型**就能查到，属于「多花一轮」而非「错得无声」。
  - ✅ 算 🟠「每次都要重踩 / 静默烧 token / 系统性浪费往返」——
    且**成本不对称**：零命中的代价不是 1 轮，而是「先怀疑路径、再怀疑模式、再怀疑索引范围」**三轮**，
    因为**这个形态恰好让三个自查方向全部落空**。
  - ⚠️ 若调高，需要一个条件：**当调用方据此改代码时**。
    与 [47] 同理 —— 这里 `Region.links` 若被误判为 `List<Region>`，改出来的代码**编译不过**（硬失败），
    所以**本条单独看不会假绿**，🟠 中是对的。
- **是否设计使然**：**否**。`No results found` 对「模式无匹配」与「工具/索引异常」**同形**（[19] 族），
  而 `query` 含泛型时**本可给出一句低成本的提示**。
- **可修性**：**本项目侧修不了**（服务在仓库外）；但**调用侧纪律本项目可立刻立**。

## 5. 责任归属

| 侧 | 归属 |
|---|---|
| `rimworld-source-online` 服务 | 缺陷所在（错误信息不区分失败原因） |
| 本项目 | **出规范纪律**：零命中先换口径 + 跑正控，再下「不存在」 |

## 6. 修的话怎么修

### 6.1 服务侧（交出去）

`No results found` 时，**若 `query` 含 `List<` / `Dictionary<` / `HashSet<` / `IEnumerable<` 等泛型括号**，
补一句：「模式含泛型，请确认**元素类型**（集合字段的 `T` 常常不是名字里那个类型，例如 `Region.links` 是 `List<RegionLink>`）」；
或返回同文件同前缀的近似声明行。

### 6.2 本项目侧（落进规范，零代码）

> **`search_source` 零命中 ⇒ 不许直接下「不存在」的结论。**
> 必做三件事：① **换口径**（换元素类型 / 换词根 / 去掉 `file_pattern` 放开全仓）；
> ② **跑正控**（同轮换一个已知存在的 query，证明通道是通的）；
> ③ 报告里写出**检索范围 + 换过哪几种口径**。
> ⚠️ 「换口径」里**必须包含「元素类型」这一项** —— 路径错、模式错、可见性错这三项自查在 [45] 上全部落空。

## 7. 与既有计划 / 报告的关系

| 对象 | 关系 |
|---|---|
| `plans/2026-10-01-11-search-source-false-negative-convention.md` | **并入，不另立计划。** 45 号是 11 号「零命中出处纪律」清单上的**第 5 种形态**，正文 ④ 的建议（「零命中前先换口径 + 跑正控」）**就是** 11 号的核心纪律。**本报告的新增量只有一条**：11 号的「换口径」清单里**必须显式列出「集合字段的元素类型」**——否则执行者换完路径、换完模式、换完 file_pattern 就会下结论 |
| `triage/50-search-source-vanilla-only-index-scope.md` §5.1 | ✅ 该报告已把 `[13][16][21][44][45]` 列为「**在语料内写错**」一族 —— **45 号的位置正确**。但 50 号说这几条「都能靠一次调用自查（规矩 3 走一遍）」—— ⚠️ **对 45 号不完全成立**：`规矩 3` 的换口径清单若不含「元素类型」，走完仍然是 0 |
| `docs/mcp-source-lookup-conventions.md` | 🚨 **不存在**（本轮实测 `File.Exists = False`）⇒ 45 号的规范落点被阻塞，**只能改计划 11** |

## 8. 检索范围声明与自我限制

| 结论 | 范围 |
|---|---|
| `Region.links` 元素类型 = `RegionLink` | `mcp__rimworld-source-online__search_source`，索引范围 = **该服务的全部 vanilla `Source/` 语料**。**未检索任何模组程序集** |
| 「`public List<Region> links` 零命中」 | 同上，同一次会话；**同轮正控 4 命中 + 2 命中**（见 §2 #3/#4）⇒ 排除「通道故障」 |
| 「`read_file` 返回块内含该行」 | `Source/Verse/Region.cs`，`start_line=15, line_count=10`。⚠️ 返回块**不带行号**，其中的位置由 #2 的 1-based 回显独立确认 |
| 板上 [45] 正文 / 索引行定位 | 标题锚点 `Select-String -Pattern '^## \[(45)\]'`；索引节 L14–L125 圈定后取列 |

**板子快照（⚠️ 本轮内漂移）**：初读 **2154 行** / `mtime = 2026-10-03 20:41:37`；复读 **2190 行** / `20:50:37`。
`## [45]` 位置**未变**（L392）。**索引节 L14–L125 与全部严重度复读后一致。**
⚠️ 不圈范围地匹配 `^\|\s*\*\*\[(\d+)\]\*\*\s*\|` 还会命中 **L759 起的 `## 🤖 Triage 台账`**（另一张表），**不是索引行重复**。

**自我限制**：

- ❌ 未修改任何既有文件；未编译、未启动游戏、未调用改状态的 MCP 工具。
- ❌ **未使用 decompilerserver**（本条不需要，且它是跨会话共享状态）。
- ⚠️ **未核实 `W8-P24-review.md §11` / `W8-P24-report.md:888` 是否真的写了这些**（TEXT20 工作区，读不到）。
- ⚠️ **本报告未验证「`List<Region>` 这条路径是调用方唯一走过的路」** ——
  正文说「连『我是不是路径写错了』这个自查方向都不会被触发」，本报告只验证了**该模式确实语法合法**，
  「不触发」是关于**人的反应**的陈述，不在本流水度的可核实范围内。

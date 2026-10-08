# MCP / Mod 使用吐槽板（现场问题堆栈）

> **这是给 UESDdebuger 维护者看的。** 模组和 MCP 都是同一个人维护，所以这里记的每一条都是真实踩过的坑。
>
> **用法：** 遇到 MCP / 模组用着别扭的地方，**粗略写一条追加进来就行**，不用写工整。
> 格式随意，但请带上 **①症状 ②怎么复现 ③影响 ④严重度**。
>
> **最新在最上面**（往下翻是历史）。状态：`待修` / `已绕过` / `已修` / `不接受`
>
> 维护者视角：本板只收**真实使用中产生**的问题，不收设想。

---

## 索引

| # | 症状 | 严重度 | 状态 |
|:--:|---|:--:|:--:|
| **[53]** | 🔴 **mono `eval` 成功一次之后，对同一表达式连报 `frameIndex 0 超出线程 #1 的调用栈范围`** ⇒ 通道从「可用」**静默退化**成「不可用」，而**第一次的返回值是真值**（TEXT20 RC-18 实测：`Verse.Find.TickManager.TicksGame` 成功得 490，此后同表达式连报）。**为什么这条贵**：调用方按第一条读到真值并据此下结论，**第二次之后的读数全部无效**；错误提示虽带原因，但**只看 `success` 字段就会误判为「已取到」**。📌 **风险面只在「读」这一环** —— 另一项目实测：计数器**写入**走 `create_hook`（零日志、正控已验），`eval` 只管最后读出 ⇒ **读写两段要分开报，别把「读出口不稳」写成「整条链不可信」**。**建议**：`success` 与「真的取到值」绑定 + 提供原生计数器通道（`get_game_status.ue_game_tick` 实测 3 次全成、与 490 一致）+ 文档写明「eval 是单次通道」。⚠️ **本条同时使 [36] 对 1.6 现网过期** —— `rimworld.execute_debug_action` 本轮实测**两次成功**（1571 / 1549 ms），**但需进地图后 GABP 才 connected，且必须用全限定名**。实测方：TEXT20 RC-18 / RC-19 | 🔴 高 | 待修 |
| **[52]** | 🔴 **`read_file` 手数出来的行号，会在「纠正别人的错误行号」这一步**反向**再错 1 —— 因为返回值不带行号，而 `start_line` 是 0-indexed** ⇒ 审查者判「`:32/:37/:42/:47` 错了」，给出的「更正」`:37/:42/:47/:52` **指的根本不是比较语句，而是四个方法的闭合大括号 `}`**；四处 `< 10000` 的真实行号是 **`:36/:41/:46/:51`**。**为什么这条贵**：它和 [14]/[46]/[49]/[51] 不是同一种病 —— 那几条是「原始行号错了」，这条是「**错误被更正成了另一个错误**」，而且更正值因**整偏移 1 而显得极其可信**（`:32`→`:37` 这种整 5 平移更像真核对过）。下游若照单执行，会把一条**已被标记的问题**改成一个**从未被标记的新问题**，且再也无人复查。⚠️ 正控：同一次 `read_file(PathGrid.cs, start_line=112)` 数出的 `CalculatedCostAt`=120 / `if`=125 / `return 10000;`=127 **与派工单给的锚点全中** ⇒ 1-index 映射本身是对的，**错的只是"数到哪一行"这一步**。**建议**：(a) `read_file` 每行前缀真实 1-indexed 行号（与 [14]/[46]/[49]/[51] 同一个修法，这条最省事）；(b) 在此之前，**任何"更正行号"的结论必须用第二个不依赖 `read_file` 的锚点交叉验**（本例用 `search_source` 回显 + 一个已知锚点双验才敢落笔）。实测方：TEXT20/W8 P24-⑤c-r2 修复轮（5C-R2） | 🟠 中 | 待修 |
| **[51]** | 🔴 **`read_file` 的返回块不带行号、也不告诉你块边界落在一条多行语句的第几行** ⇒ 按 `line_count=1` 取定义会把**跨行表达式**读成**语法闭合、能编译、语义错**的单行。TEXT20 `W8-LEDGER-review.md` §3.3 就是这么错的：只取 `FlightState.cs:20` 首行，据此写下「`IsInAir` 仅含 Takeoff\|Cruising、`Landing` 不在其中」并进了正式报告（真值跨 `:20-22` **三行**，含 `Hovering`/`Landing`/`Falling`）。⚠️ 正控：同文件 `line_count=3` 三行齐全 ⇒ 通道是通的，问题只在**按行取的粒度 + 无截断提示** | 🔴 高 | 待修 |
| **[50]** | 🔴 **符号检索族（`search_source` / `read_file` / `read_csharp_symbol`）**只索引 vanilla 仓库**，mod 本地符号一律查不到 ⇒ `search_source("IsInAir")` 返 `No results found`、`read_file("Source/RimWorld/FlightState.cs")` 与 `("Source/Verse/FlightState.cs")` 均返 `File not found`（文件**确实存在**，在 `Mods\TEXT20\source\PathPlan\FlightAStar\`）。`read_csharp_symbol` 同样只认 vanilla 类型名。⇒ **「工具范围外」与「不存在」提示同形**，调用方合理默认是后者。⚠️ 正控：同轮 `read_file("Source/Verse/GenGrid.cs", start_line=74, line_count=3)` 成功 ⇒ 通道是通的，差别只在索引范围 | 🔴 高 | 待修 |
| **[49]** | 🟠 **`read_file` 与 `search_source` 的行号偏差**不是常数**（同两文件实测 Δ=0 / +1 / +2）⇒ [14]/[46] 提的「加起点回显」不够用** —— 同一轮审查里交叉核 4 次：`read_file(PathGrid.cs, start_line=0)` 返回首行 `using System;`，按「首行=1」数得 `ImpassableCost`=20、`Cost`=55，**与 `search_source` 全中（Δ=0）**；但 `read_file(PathGrid.cs, start_line=100)` 按「首行=100」数得 `CalculatedCostAt`=**119**，`search_source` 说 **120**（Δ=+1）；`read_file(PathFinderMapData.cs, start_line=40)` 数得 `normalCost=`**63**，`search_source` 说 **65**（Δ=**+2**）；同文件 `start_line=185` 数得 `GatherData`=**193**，`search_source` 说 **194**（Δ=+1）。**为什么这条贵**：它不是 [14] 说的「恒差 1 行」，也不是 [46] 说的「连基准都不可信」—— 是**同一对工具、同一轮、两个文件里出现三种偏差**，说明二者很可能读的是**两份不同的 vanilla 快照**（或 `read_file` 的返回块会吞行）；此时**即使按文档回显了 `start_line` 起点，手工数出来的行号仍可能与 `search_source` 不一致**，而本项目的注释行号正是这么写的。**已造成真实损坏**：TEXT20 `W8-P24-5c-report.md` §A-2 因此把 `PathFinderMapData.cs` 的 ctor 三行记成 **`:65/66/67`**（实为 `:64/65/66`）、把 `PathingContext.cs:10` 记成 **`:10`**（实为 **`:9`**），而同文 §A-3 的 `GatherData:194` 又是 1-index ⇒ **一份报告内部 0/1-index 混用**，直接产出 3 条 Minor 级错锚点。⚠️ 正控：`read_file(CostSource.cs, start_line=0)` 全文 33 行逐行数得 `ComputeAll`=17 / `CopyFrom`=19 / `UpdateIncrementally`=22，**与 `search_source` 全中** ⇒ 通道是通的，问题只在**跨工具对齐**。**建议**：(a) `read_file` 每行前缀真实行号（可开关）—— 这一条同时解决 [14]/[39]/[46]；(b) `search_source` 的命中结果里**回显该行原文**，使「行号 ↔ 内容」成为可自证的一对，而不是让人再去 `read_file` 对齐；(c) 若两者确实读不同快照，请在其中一侧显式标注快照版本。实测方：TEXT20/W8 P24-⑤c 审查者（REV-5C） | 🟠 中 | 待修 |
| **[48]** | 🔴 **注释里当审计凭据用的行号锚点，在「别的修复轮并发改同一个文件」时会当场失效，而没有任何机制会告诉你** —— TEXT20/W8 给 `CaseLedger` 补 15 行账本，52 行新代码里放了 33 个 `` `:NNN` `` 跨文件引用当审计链（`DebugActionStageTestMap.cs:409/416/417/425/429/430` 指向 `PathGridCostTests.cs`）。**症状**：我按锚点逐条回查，`:711`（注释说 `fake.Dispose()`）实际是 ⑬d 的断言描述串、`:550`（说 ⑨d）实际是 ⑦ 里的 `var jobSeg = default(PathGridJob);`、`:497`/`:506`（说「与 ⑦ 同款」）实际指向**另一个用例 ⑥** —— 5 处全落空。**怎么复现 / 影响**：`PathGridCostTests.cs` 在我审查开始后 **10 分钟**（16:44:12）被另一轮 `P24-⑤c` 改写，**52001 B → 56654 B（+48 行）**；五处偏移**统一为 +48**，与改写量逐字吻合 ⇒ **写注释时它们全部命中**，是**事后**失效的。危害不在于行号本身，而在于：① 注释自称的「按行号可复核」这个卖点会**静默变成骗人的注释**（同文件 `:446-447` 自己写着「写死会变成一条骗人的注释」）；② 按锚点去核的人会**指向错误的用例**（⑦→⑥）而不自知；③ `AGENTS-REGISTRY.md` 的「进行中」表最后更新于 13:02、仍写「无任何代理在跑」，**完全没登记 16:44 那一轮** ⇒ 审查者连「现在还有谁在改」都查不到。⚠️ 正控：同一次审查里指向 `ClaimGateTests.cs` 的 **19 处锚点全部逐字命中**（该文件当轮未被改）⇒ 失效与「该文件是否被并发改」一一对应，不是「行号本来就爱错」。**建议**：(a) 跨文件引用改用**符号名为主、行号为辅**（`RestoreAirborne` 唯一 ⇒ 行号漂移不致命）；(b) 同域文件（账本 ↔ 它引用的 `*Tests.cs`）的修复轮**排他串行**；(c) 代理登记表**派工前即写入**，并用磁盘 mtime 反向校对。实测方：TEXT20/W8 `LEDGER` 审查者（REV-LEDGER） | 🟠 中 | 待绕过 |
| **[47]** | 🔴 **扩展方法在「按类型搜成员」的工具里一律查不到 ⇒ 「这个类型有没有 X」会拿到假阴性** —— 想把 `dest.IsValid(map)` 换成按地图判边界，先反编译侧查「`IntVec3` 有没有 `InBounds`」：`search_members(declaringTypeFilter="Verse.IntVec3", query="Bounds", contextAlias="game")` ⇒ `items:[] · totalEstimate:0`；在线源码侧同样：`search_source("InBounds\(Map map\)")` 只回 `Source/Verse/CellRect.cs:445`。**它其实存在，但是扩展方法**：`Source/Verse/GenGrid.cs:75 public static bool InBounds(this IntVec3 c, Map map)`（`:85` 还有第二个重载 `(…, int contractedBy)`）。**为什么这条贵**：扩展方法长得和实例成员一模一样（`dest.InBounds(map)` 照写），而**所有按 `declaringType` 索引的工具都看不见它** —— 与 [43] 的 `get_overloads` 少报是同一类病（**工具的索引范围 < 语言的可见性范围**），但后果更隐蔽：[43] 让你**误判有歧义**，这条让你**误判 API 不存在**，进而把代码「修」成 `dest.IsValid`（能编译！）—— 而 `IntVec3.IsValid` **只判 `y >= 0`**（`Source/Verse/IntVec3.cs:39`）⇒ 判定**恒为真** ⇒ 断言变恒真断言，**假绿**。建议：`search_members` 在 0 命中时补一句「该名字是否可能是 `Verse.GenGrid` 之类静态类上的扩展方法（首参带 `this`）」；或对 `query` 追加一次「全仓 `public static` + 首参 `this`」的兜底检索。另外**重载枚举仍然要绕开 `get_overloads`**（见 [43]）。⚠️ 正控：同轮 `search_members(declaringTypeFilter="Verse.IntVec3", query="Valid")` 返 **2** 条（`Invalid` 字段 + `IsValid` **Property**）⇒ 通道是通的；`Bounds` 的 0 对**实例成员**是真阴性，对**调用可行性**却是假阴性。实测方：TEXT20/W8 文档回填代理（gap-05 / gap-11 勘误） | 🟠 中 | 待修 |
| **[46]** | 🔴 **`read_file` 返回的**首行到底对应第几行无法从返回值自证** —— 读 `Source/Verse/AI/Pathing.cs` 传 `start_line=69`，返回的 25 行里既没有行号、也没回显"从第 N 行开始" ⇒ 我按"首行=69"手工数，得到 `For(TraverseParms)` 在 **75**；而同文件 `search_source(query:"For\\(\\|flying\\|GetPathContext", file_pattern:"*Pathing.cs")` 给的是 **76**（`For(Pawn)`=85、`GetPathContext` 那句=91，三处一起对不上同一个 offset）。**为什么这条贵**：它是 [14] 的**加强版**——[14] 说"差 1 行"，实测是**连基准都不可信**：如果不去和 `search_source` 交叉，我就会把一条**本来就正确的注释锚点**（`Pathing.cs:76`）误判成"又错了一行"，进而给出错误的复审结论。⚠️ 正控：同一轮 `read_file(PathFinder.cs, start_line=93)` 手工数出的 `:106 / :117 / :132 / :141` 与 `search_source` 的 4 条命中**全部吻合** ⇒ `read_file` 本身没错，纯粹是"返回值不告诉你起点"。建议：返回体首行加一行 `# from line N (1-based)`，或每行前缀行号（可开关）。实测方：TEXT20/W8 修复轮定向复审 | 🟠 中 | 待修 |
| **[45]** | 🔴 **`search_source` 的正则里把**元素类型**写对仍会零命中 —— 集合字段的 `T` 常常不是「名字里那个类型」** —— 要核 `Region.links` 这个 public 字段（派工词里点名的大小写笔误项），`search_source(query:"public List<Region> links;")` 与 `query:"public List<Region> links"` **都返 `No results found`**；真实声明是 `Source/Verse/Region.cs:20  public List<RegionLink> links = new List<RegionLink>();` ⇒ **元素类型是 `RegionLink`、不是 `Region`**。**为什么这条贵**：这是继 [13]（命名空间≠目录）、[16]（能读不能搜）、[19]（`No results found` 一刀切）、[21]/[44]（`file_pattern` 路径/glob 失配）之后**第 5 条独立的「搜不到 ⇒ 不存在」路径**，而且是唯一一条**模式本身语法完全正确、只是类型猜错**的 —— 它连「我是不是路径写错了」这个自查方向都不会触发。⚠️ 正控：同轮 `search_source(query:"ShouldAvoidFences")` 返 **4 条**、`query:"public static TraverseParms For"` 返 **2 条** ⇒ 检索通道本身是通的。建议：`No results found` 时若 `query` 里含 `List<` / `Dictionary<` / `HashSet<`，回一句「模式里有泛型，请确认元素类型」；或在结果里给出同文件同前缀的近似声明。实测方：TEXT20/W8-P24 审查者 | 🟠 中 | 待修 |
| **[44]** | 🔴 **`search_source` 的 `file_pattern` glob 不递归 ⇒ 猜错目录就是一条「不存在」的假阴性** —— 查 `PathEndMode` 的命名空间时：`search_source{query:"enum PathEndMode", file_pattern:"Source/Verse/PathEndMode.cs"}` ⇒ `No results found`；改 `file_pattern:"Source/Verse/*.cs"` ⇒ **仍然** `No results found`；去掉 `file_pattern` 放开全仓 ⇒ 立刻命中 `./Source/Verse/AI/PathEndMode.cs`。根因是 `PathEndMode` 在 `Verse.AI` 命名空间（[13] 同族：命名空间 ≠ 目录），而 `Source/Verse/*.cs` 的 glob **不跨子目录**。**为什么这条贵**：`No results found` 与 [19] 同族、又与 [16] 的「能读不能搜」重叠 —— 在 `COMMON.md §5.3` 的语境下，调用方很容易把「glob 没匹配上」读成「这个 API 不存在」，而它其实存在。建议：`file_pattern` 未命中时**回一句「该 pattern 匹配 0 个文件，下面按全仓搜的结果是…」**（区分「pattern 没匹配上文件」与「文件里没有匹配行」）；或把 `Source/Verse/*.cs` 改成 `**` 语义。⚠️ 正控：同一轮 `read_file(Source/Verse/AI/PathEndMode.cs, start_line=0)` 成功返回 ⇒ 检索通道本身是通的。实测方：TEXT20/W8-P26 审查者 | 🟠 中 | 待修 |
| **[42]** | 🔴 **`search_source` 的参数名必须是 `query`，传 `pattern` 会报一条「驴唇不对马嘴」的错** —— 传 `{pattern:"…", regex:true}` ⇒ 报错 `Input validation error: … query: Invalid input: expected string, received undefined`。**报错完全没提 `pattern` 是多余的**，调用方会以为是自己漏传了 `query`，于是把同一个东西改名叫 `query` 再试一次（能过，但白白多花一轮）。症状与 [19]/[21] 同族：**错误信息不指向真正的错误**。建议：要么 schema 里把 `pattern` 也声明成别名，要么报错里显式列出「未识别参数：pattern」。实测方：TEXT20/W8-P24（本轮连踩 3 次） | 🟡 低 | 待修 |
| **[43]** | 🔴 **`get_overloads` 对重载会【静默少报】，且报的是 public 的那一个** —— 同一 memberId `Verse.AI.PathGrid:06006323:M`，`get_overloads` 返 **`totalEstimate: 1`**，而 `search_members(query="RecalculatePerceivedPathCostAt", declaringTypeFilter="Verse.AI.PathGrid", contextAlias="game")` 返 **`totalEstimate: 2`**（private 单参 + public 双参各一条，`get_member_details` 分别给 `accessibility: Private` / `Public`）。⇒ **「这个方法没有重载歧义」这个结论，用 `get_overloads` 去证会拿到假阴性**，而那个假阴性恰好否认的正是「补丁打到了 private 重载上」这个最贵的 bug。建议：`get_overloads` 改成返回全部重载并标注 accessibility，或在只剩 1 条且同名同参个数不同的场景下直接拒答。⚠️ 反编译侧**显式传了 `contextAlias="game"`**，所以这条**不是** [COMMON.md §5.9] 的串味。实测方：TEXT20/W8-P24（独立复核波次管理代理的读数） | 🔴 高 | 待修 |
| **[40]** | 🔴 **`get_members_of_type(kind:"field")` 的字段签名里没有 `readonly` / `const` 修饰符** —— 只给「类型 + 名称」（如 `Map map`）⇒ 「这条字段是不是 `readonly`」在反编译侧**无从判断**。而这正是**写 `AccessTools.FieldRef` 前必须确认的一件事**（readonly 字段写会失败）。TEXT20/W8-P25 实测：`Verse.PathFinder.map` 反编译只回 `map : Map` Private Instance，`readonly` 只能回在线源码 `Source/Verse/PathFinder.cs` 拿。建议：字段签名补修饰符位，或返回里加 `isInitOnly` / `isLiteral` | 🟠 中 | 待修 |
| **[41]** | ✅ **[38] 的实测反例（限定条件待查，不是推翻）** —— `get_members_of_type{typeId:"Verse.AI.Pathing", contextAlias:"game"}` **成功**返回 21 个成员（含 `RecalculateAllPerceivedPathCosts` Public Instance），`typeId:"Verse.PathFinder"` 也成功 ⇒ 含点命名空间**没有**必然解析错。结合 [38] 记的 `parsed:{typeName:"Verse.AI", memberName:"Pathing"}`，疑似**取决于 `contextAlias`**：带 alias 时走的是完整名解析、不带时走「按最后一个点切分」那条路。**待验证**：两条调用各跑一次同 `typeId` 对照 | 🟡 低 | 待确认 |
| **[01]** | `execute_debug_action` **报告成功但根本没执行**（72ms / `logCount:0`） | 🔴 高 | 待修 |
| **[02]** | `execute_csharp_code` 里的 `Verse.Log.*` **不落 Player.log** —— 无法当取日志的兜底通道 | 🔴 高 | 待修 |
| **[03]** | `list_debug_action_children{"path":"Actions"}` 返回 **400KB+**，会撑爆调用方上下文 | 🔴 高 | 待绕过 |
| **[04]** | 游戏启动类调用（`start_game` / `start_quick_test`）**必超时**，超时 ≠ 失败，无法区分 | 🟠 中 | 待绕过 |
| **[05]** | `stop_game` 非 force 与 force 之间的**竞态误导**（"still closing" → 紧接着 "not running"） | 🟠 中 | 待修 |
| **[06]** | DebugAction 路径格式 `Actions\<label>` **无任何文档**，`category` 是字段不是路径段（极易猜错） | 🟠 中 | 待修 |
| **[07]** | GABP 在**主菜单 / 加载中**静默 `idle`，`rimworld.*` 全不可用，但工具本身不 fail-fast | 🟠 中 | 待修 |
| **[08]** | `mcp_help` 要**逐层下钻**才能找到常用工具（如 `pause_game` 藏在三层下） | 🟡 低 | 待修 |
| **[09]** | 每次游戏启动 **`Player.log` 轮转**，旧内容进 `Player-prev.log`，但没有任何工具暴露这一点 | 🟡 低 | 待修 |
| **[10]** | 无法查询**当前 vanilla / 模组 API 的实际成员可见性**（只能靠 decompile 或撞编译错） | 🟡 低 | 待修 |
| **[11]** | `search_source` 对**高频标识符**返回 400+ 条近乎重复的结果，把调用方上下文冲爆 | 🟠 中 | 待绕过 |
| **[12]** | 没有「**某模组在不在场**」的工具；`activePackageIds` 只给 packageId **不给磁盘路径**（Workshop 模组不在 `RimWorld\Mods` 下 ⇒ 目录名 grep 必然假阴性） | 🟠 中 | 待绕过 |
| **[13]** | vanilla 渲染栈的**命名空间 ≠ 目录**（`PawnRenderer` 在 `Source/Verse/` 不在 `Source/RimWorld/`），而计划通篇只引裸文件名 ⇒ `read_file` 猜错目录只回 `File not found` | 🟡 低 | 待绕过 |
| **[14]** | `read_file` 的 `start_line` 是 **0-indexed**，而本项目的核心工作恰恰是核**精确行号** ⇒ 每一次核证都差 1 行，且返回里**没有任何提示标明了哪一行是第几行** | 🟠 中 | 待绕过 |
| **[15]** | `Log.ErrorOnce` 的**消息正文**被 `Log.Error_Patch2` 吞掉，只剩**栈**落 `Player.log` ⇒ 靠消息文本做的判据在 UE 下失效 | 🟠 中 | 待绕过 |
| **[16]** | `search_source` / `read_csharp_symbol` 的**命中面不一致**：`read_csharp_symbol(typeName="Thing", memberName="DeSpawn")` 能返回 `Verse/Thing.cs` 的全文，但 `search_source(query="pather\\?\\.StopDead\\(\\)", file_pattern="Verse/Thing.cs")` 回 `No results found` ⇒ 同一个文件"能读不能搜" | 🟠 中 | 待绕过 |
| **[17]** | `execute_debug_action` 的 `effects.logs` **只给最后 50 条**且**每条都带完整回显**，一次调用返 ~40KB ⇒ 关键行（尤其是**第一条**和 `[零覆盖]`）经常被挤掉，只能改去 grep `Player.log` | 🟠 中 | 待绕过 |
| **[19]** | `search_source` 的 **`No results found` 一刀切**：分不清「模式写错」「成员不存在」「成员存在但是 private」 | 🟠 中 | 待绕过 |
| **[18]** | 「日志条数」类判据在 UE 控制台下**会被回显翻倍**（每行两条），而 `execute_debug_action` 的 `logs[]` 数组**已经把回显去重了** ⇒ 同一个量在两个地方数字不同，容易被当成 bug | 🟡 低 | 待绕过 |
| **[20]** | `execute_csharp_code` 把 `Verse.Log.Message(多行)` 的**全文回显进 `data.output`** ⇒ 69 行的活体探针在上下文里出现**两遍**（UE 回显 + 原文），一次调用 ≈ 12KB 灌进主上下文 | 🟠 中 | 待绕过 |
| **[21]** | `search_source` + `file_pattern` **路径猜错目录时静默回 `No results found`**（与「成员不存在」不可区分）⇒ 在**核精确行号**的任务里是一次现成的假阴性 | 🟠 中 | 待绕过 |
| **[22]** | `rimworld.play_for` **恒不可用**（`PLAY_FOR_MAIN_THREAD`），而 `step_game_ticks` 的 `tickCount` 参数名不生效（回 `requestedTicks:1`）⇒ **没有任何工具能给暂停中的游戏补几个真 tick** | 🟠 中 | 待绕过 |
| **[23]** | **「工具报成功 / 超时，但实际没执行」的第三例** —— `execute_debug_action{path:"Actions\运行全部动画测试"}` 走 GABP **超时，且日志里一条输出都没有**（工具层面报成功）⇒ 调用方若只信返回值，会把「没跑」读成「跑了但没输出」。绕过：`execute_csharp_code` 反射调同一个私有静态方法 | 🔴 高 | 待修 |
| **[24]** | `rimworld.select_pawn` 经 GABP **两次都 30s 超时**（`GABP 请求 'tools/call' 超时（30000ms)`）⇒ 设选择集没有可靠路径。绕过：`execute_csharp_code` 调 `Find.Selector.Select(thing,false,true)` | 🟠 中 | 待绕过 |
| **[25]** | **[01] 的加强版**：当 GABP 已被主线程打挂（`OnGUI` 异常循环）时，`execute_debug_action` 会**在 72ms 内返回 `success:true` + 空 `logs`** ⇒ 「假成功」有了**可判别的特征：耗时异常短 + 日志为零**。建议把这个组合直接判为未执行 | 🔴 高 | 待修 |
| **[26]** | `DebugActionStageTestMap` 的**交叉校验守卫对整个新编号段结构性失明** —— 抠编号的正则字符类只写到 `㉟`(U+325F)，`㊰`(U+32B0) 起全部匹配不到 ⇒ 加了 `CaseLedger` 行反而报「账本多行/已失效」 | 🔴 高 | 待绕过 |
| **[27]** | `search_source` 命中行**只有 `路径:行号`、不带声明类型**，而 vanilla 的**同名方法跨类型**是常态（`TryRecoverFromUnwalkablePosition` 同时在 `Pawn_PathFollower`(public, `bool` 参数) 与 `Caravan_PathFollower`(private, 无参)）⇒ 「给 X 打补丁」时必须逐个打开命中才知道挂载目标对不对 | 🟡 低 | 待绕过 |
| **[28]** | **装了 Reachability 类 Harmony 补丁的模组里，`pawn.CanReach(...)` 不再是原版语义，且无任何运行时信号** —— 补丁 `return false` 静默截断原版、换成模组自己的判定（TEXT20 实测：换成 `FlightReachGate.CanReachFor` = **飞行**可达性）。编译器不报、运行时不报、反射也看不见补丁 ⇒ 在测试/调试代码里写 `pawn.CanReach` 极易拿到**另一个问题**的答案 | 🟠 中 | 待绕过 |
| **[29]** | `get_members_of_type` **静默漏掉 public 成员**却报 `hasMore:false`（实测 `Verse.ThingOwner.Contains(Thing)` 缺席）+ `get_overloads` 不返回被查的那个 ⇒ **直接制造「这个 API 不存在」的假结论** | 🟠 中 | 待绕过 |
| **[30]** | `start_game` 的 **`waitForNotification` 默认值（`true`）会稳定触发 MCP 超时**（`MCP error -32001: Request timed out`），而**游戏其实照常起来了** ⇒ 与 [04] 同源，但这里有**可判定的绕过**：`start_game{waitForNotification:false}` **立刻返回并带 PID**（实测连续 6 轮冷重启，0 次超时）。建议把 `waitForNotification` 的默认值改成 `false`，或至少在超时后**回一句「游戏可能已启动，请查 `get_game_status`」** —— 现在这个超时把「已成功启动」伪装成「调用失败」 | 🟠 中 | 已绕过 |
| **[31]** | **`execute_csharp_code` 会被静默重放** —— 第一遍改完游戏状态、但写响应时 UE HTTP 连接被重置（`Player.log` 有 `[UEHttp] Failed to send response`），MCP 重试后返回的是**第二遍**的结果（实测「destroying 2099」被回成「destroying 0」）⇒ 带副作用的代码拿到的数字**不是这一次真实执行的** | 🔴 高 | 待修 |
| **[32]** | `rimworld.start_debug_game_ready` 的 **`timeoutMs` 不生效**（传 300000 仍被 GABP 内建 30s 砍掉），而**新殖民地照样造好了** ⇒ 参数看起来可用、实际是摆设 | 🟠 中 | 已绕过 |
| **[33]** | `add_using_directive("Verse")` 之后 `IntVec3.GetRegion(map)` 变 **CS0121 二义**（两个候选签名逐字相同）；`RegionType` 也**没有 `Region` 成员**（要 `RegionType.Normal`）；`Map.regionManager` 是 **internal**（property 反射也取不到）⇒ 「查 Region/District」这条最高频探针写法一次踩三个坑 | 🟡 低 | 已绕过 |
| **[34]** | `execute_csharp_code` 的包装宿主方法**是 `void`** ⇒ 代码里按工具描述写 `return "…";` 会**编译失败**；且 `data.output` 把 `UnityEngine.Debug.Log` 的全文**再回显一遍** | 🟠 中 | 待修 |
| **[35]** | **`execute_csharp_code` 在「无 using」下的成员名解析全靠撞** —— 同一个造夹具脚本连撞 **5 次** `CS0234/CS0841/CS1503/CS1061/CS0411`，而每次报错只给行列号不给「你想要的签名是哪一支」 | 🟠 中 | 已绕过 |
| **[39]** | `read_file` **不返回行号**、`start_line` 又是 **0-indexed** ⇒ 核精确行号（P-4 类任务）必须绕到 shell；同服务器的 `read_csharp_symbol` 却**带** `Lines N-M` 头 ⇒ 能力已在仓内，只是没给 `read_file` | 🟠 中 | 待修 |
| **[38]** | `get_members_of_type` / `normalize_member_id` **按最后一个 `.` 切分** `typeId`，于是**含点的命名空间**必然解析错：`get_members_of_type{typeId:"Verse.AI.Pathing"}` 回 `Type 'Verse.AI' could not be found`（`parsed:{typeName:"Verse.AI", memberName:"Pathing"}`）⇒ 又一个「符号不存在」的假结论 | 🟠 中 | 待绕过 |
| **[37]** | **`decompilerserver.search_symbols` 不带 `contextAlias` 时搜的是「当前上下文」** —— 而当前上下文可能根本不是游戏程序集（本环境实测停在 `gab` = `Lib.GAB.dll`，94 个类型），此时查 `GetPathContext` 返回 **`{"items":[],"totalEstimate":0}`**，与「这个符号不存在」**完全同形** ⇒ 直接制造 1.6 API 漂移勘误里的假结论。绕过：先 `list_contexts` / `status` 看 `currentContextAlias`，再给 `contextAlias:"vanilla"` | 🔴 高 | 待绕过 |
| **[36]** | **整个 `rimworld.*` 调试动作族（`execute_debug_action` / `get_debug_action` / `list_debug_action_roots`）在本环境一律 30s 超时且不执行**，而 `pause_game` / `search_debug_actions` 正常 ⇒ 与 [01]/[23]/[25] 的「单条假成功」不同，这里是**整个族全灭**，且**超时无法与「正在执行」区分**（主线程 tick 同时在正常推进） | 🔴 高 | 待绕过 |

## [55] UE REPL 的「静默丢弃」有**三种同 facebar**，而且**可以只丢一个方法、不丢整个类**（2026-10-05，TEXT20 RC-19 实测）

**①症状**　`execute_csharp_code` / REPL 返回
`Code compiled without errors.` + `result: null` + `executionTime` 5–12 ms
⇒ **看起来完全成功，实际根本没调用。**

**②实测到的三种触发条件，提示完全相同**

| # | 触发 | 实测 |
|:-:|---|---|
| a | **引用不存在的成员** | 引用 `g.AllRegions`（不存在）⇒ `result:null`，`R19.Seq` / `R19.Dump()` **照常建成** |
| b | **调用串超长** | **~155 字符可过；~175 起静默**（无任何截断提示） |
| c | **类型在该 REPL 里不可解析** | `PathEndMode` 加 `Verse`/`RimWorld` using、试全限定名 `RimWorld.PathEndMode` ⇒ **全部**返回「compiled without errors」却**静默不执行** |

**③🔴 最危险的新事实：静默丢弃可以只丢一个方法，不丢整个类**
⇒ **不能拿「这个类建成了」推断「它的每个方法都建成了」。**
⚠️ 这是对 `RC-14d §8-3`（「静默丢弃丢整个类」）的**修正** —— 那条只对了一半。

**④影响**　与 [31]（「`execute_csharp_code` 被静默重放」）**不同族**：[31] 是**返回值来自动第二遍**，
这条是**压根没执行**。两者提示词不重叠（一个有 `result`，一个 `result:null`），
但**调用方若只看 `success` 会把两者混为一谈**。

**⑤严重度**　🔴 高 · **状态**：待修

**建议**：(a) 静默丢弃时**在返回体里显式回一句失败原因**（哪怕只是「成员未找到」/「串过长」）；
(b) **给 REPL 返回「本次真正创建/执行的成员清单」**，让调用方能逐个核对，而不是只看类建没建成；
(c) 文档写明 **~155 字符是实测安全线**，并建议长表达式拆成多段静态字段。

**⚠️ 正控**　同轮 `R19R2PROBE-ALPHA777` 标记在面板 index 46 命中 ⇒ **UE `Log()` 确实落面板**，
所以「没落日志」与「没执行」是两件事，可用来区分。

---

## [54] `get_unityexplorer_logs` 是一条**不吃 `Player.log` 预算**的读出口，且**自带重放见证**（2026-10-05，TEXT20 RC-19 实测；**全板此前零前人记录**）

**①症状/价值**　在 UE 面板里，**每一次 REPL 调用的返回值都会被自动记一条**
（形如 `Invoked REPL, result: …`）
⇒ **它同时是两个东西**：① 一条**不写 `Player.log`** 的读出口 ② **免费的「是否被重放」见证**。

**②怎么用**　探针把返回值写进 UE 面板 → 用 `get_unityexplorer_logs` 读回。

**③三重价值（正对本项目两次日志撑爆 + 板上 [31]）**
- **不占 `Player.log` 预算**：TEXT20 RC-19 收工 77,645 行 / **未撞顶**，
  而此前两次撞顶分别是 **421,488 行** 与 **422,538 行** ⇒ **低一个数量级**，
  且**读数通道一行 `Player.log` 都没花**（放大源是 `X failed to interact with Y` 每 tick 刷屏，**不是读数**）
- **免费重放见证**：被 [31] 那种「静默重放」搞过，就会出现
  `R19SEQ=N` 紧跟 `R19SEQ=N+1` ⇒ **两两同号 = 只跑了一遍**
  ⇒ **不必再为自证去花 `Player.log` 行数**（RC-19 原方案的 nonce 计次因此可以省掉）
- **可与 `execute_csharp_code` 的 `result` 互证**：RC-19 实测**两条读出口逐字节同串**

**④最硬的互证**　同一瞬间 R₁ `T0=9045` vs `get_game_status.ue_game_tick` = `9047` ⇒ **Δ2**
⇒ 两条**互不相干**的通道对上了。

**⑤严重度**　💡 提示（**新增可用通道**，非缺陷）· **状态**：建议写进文档

**建议**：(a) 在 `COMMON.md`/`mcp-吐槽板` 的取数纪律里把本条列为**默认候选读出口**；
(b) 文档写明它**自带重放见证**这一点（[31] 目前只能靠调用方自建 nonce）。

**⚠️ 前人记录**　全板 grep **零命中** ⇒ **本条是首次记录**。

---

## [53] mono `eval` **成功一次之后**对同一表达式连报「调用栈范围」错 ⇒ 通道静默退化，而第一次的返回值是真的（2026-10-05，TEXT20 RC-18 `IdleError` 取证代理实测）

**①症状**　GABP 的 mono 软调试通道（`eval`）在**同一会话内成功一次之后**，对**完全相同的表达式**连报

```
frameIndex 0 超出线程 #1 的调用栈范围
```

⇒ 通道从「可用」**静默退化成「不可用」**，而**第一次的返回值是真值**。

**②怎么复现**　冷启动游戏 → `eval` 求 `Verse.Find.TickManager.TicksGame` ⇒ **成功，值 490**
⇒ **同一表达式**再求 ⇒ 报上述错，**连报多次**，不自行恢复。

**③影响**　🔴 **「成功一次」是最坏的形态**：调用方按第一条读到**真值**、据此下结论，**第二次之后的读数全部无效**。
若脚本里有「取不到就重试」或「同一表达式跨 tick 取样」，**后续读数全是错的**。

⚠️ **与板上其他条目的关键差别**：这条的错误提示**确实带了原因**（`frameIndex` / `调用栈范围`），
**但调用方若只看 `success` 字段就会误判为「已取到」** ⇒ 建议 **`success` 与「真的取到值」绑定，不要让二者脱钩**。

🚨 **实测代价（这条为什么贵）**：TEXT20 一份 **49 KB 的运行时报告**
（`RC-14d-layered-reach-frequency.md`，含「S→G **15.65 次/tick**」「峰值 **1,482/tick**」
「`district==null` **0/61,354**」「A/B 命中率塌到 0」等**全部读数**）
**在通道不稳定这一点被发现之前，计数器值的「写」与「读」都没有第二个通道交叉过**。

⚠️ **订正（2026-10-05 15:1x，同项目 RC-19 回核）**：
本条初版写的是「该报告的读数**全部走 `eval`**」—— **过头了**。
实测结构是：**写入**走 UE `create_hook` 钩子累加到 REPL 静态类（**零行 `Player.log`**，且该路已被正控验证：
8 个钩子逐个证明触发）；**`eval` 只负责最后「读出来」那一环**。
⇒ **风险面只在「读」这一环，不在「写」** —— 这两者的处置完全不同（前者可换读出口，后者要重做装置）。
📌 **建议**：报通道问题时**先分清读写两段**，否则会把一条「读出口不稳」写成「整条链不可信」。

**④严重度**　🔴 高 · **状态**：待修

**建议**：(a) `eval` 返回体显式给 `ok` / `frameIndex`，**失败时 `success:false`**；
(b) 提供**原生计数器通道**让调用方有第二选择（`get_game_status.ue_game_tick` 实测可用，见正控）；
(c) 文档写明「**`eval` 是单次通道；要重复取值请换通道**」。

**⚠️ 正控**　`get_game_status.ue_game_tick` 同轮 **3 次全成**，值与 `eval` 首次的 **490 一致**
⇒ **`eval` 的首次成功不是幻觉**；问题只在「第二次起」。

**🚨 附带证伪：本条使板上 [36] 对 1.6 现网过期**
[36] 记「整个 `rimworld.*` 调试动作族**一律 30s 超时且不执行**」——
本轮实测 **`rimworld.execute_debug_action` 两次成功（1571 ms / 1549 ms，未 30s 超时）**。
建议把 [36] 标为「**已过期（1.6 现网），待复核**」，**不要**按原文继续假定该族不可用。

---

## [52] `read_file` 手数出来的行号，会在「纠正别人的错误行号」这一步**反向**再错 1（2026-10-03，TEXT20/W8 P24-⑤c-r2 修复轮实测 + 一条已被标记的缺陷被"更正"成新缺陷）

**①症状**　`read_file` 返回块不带行号，`start_line` 是 **0-indexed**，而块尾还会打 `[TRUNCATED] Showing N/M lines.`
⇒ 手数出来的行号天然有 1 的风险。这本身是老问题（[14]/[46]/[49]/[51] 都记了）。
**新的部分是**：`PathGridCostTests.cs` 上一轮报告里那句

> `PathGrid.Walkable/WalkableFast`（`:32/:37/:42/:47`）判的都是 `< 10000`

被审查者判为 Minor 错标，给的更正是「`:32` 是 `if (!loc.InBounds(map))` 守卫，不是比较；且漏了第四处 `:52`」
⇒ **建议改成 `:37/:42/:47/:52`**。执行方逐条核下来：**这四个数全部指向四个方法的闭合大括号 `}`**。
四处 `< 10000` 比较的真实行号是 **`:36 / :41 / :46 / :51`**。

```
30  public bool Walkable(IntVec3 loc)
31  {
32      if (!loc.InBounds(map))          ← 守卫（审查者的判断是对的）
33      {
34          return false;
35      }
36      return Cost(loc) < 10000;        ← 第 1 处比较
37  }                                     ← 审查建议的「:37」
41  return Cost(loc) < 10000;            ← 第 2 处（WalkableFast(IntVec3)）
42  }                                     ← 审查建议的「:42」
46  return pathGrid[...CellToIndex(x, z)] < 10000;   ← 第 3 处（WalkableFast(int,int)）
47  }                                     ← 审查建议的「:47」
51  return pathGrid[index] < 10000;      ← 第 4 处（WalkableFast(int)）
52  }                                     ← 审查建议的「:52」
```

**②怎么复现**　`read_file("Source/Verse/AI/PathGrid.cs", start_line=20, line_count=45)`（0-indexed ⇒ 1-indexed 21-65），
从返回块第一行按 1 数到 `return Cost(loc) < 10000;` = 第 16 行 ⇒ 21+16-1 = **36**。

**③影响**　**错误被更正成了另一个错误**，而且**更正值看起来极可信**：
`:32 → :37` 这种整 5 平移比"数错一行"更像认真核对过。更严重的是可追溯性：
原值至少**被标记过**，下游会去看；新值**从未被标记**，会被当成已核实的事实一路传下去
（本例它已经被写进正式审查结论、再被转抄进下游派工单 —— 只是碰巧被派工单同时给了"你自己复核一遍"的指令才没落地）。
⇒ 这比原始行号错更贵：**原始错有检测面，更正值没有**。

⚠️ **正控**：同一次 `read_file(PathGrid.cs, start_line=112)` 数出的
`CalculatedCostAt`=120 / `if`=125 / `return 10000;`=127，**与派工单给的锚点全中**
⇒ **1-index 映射本身是对的，错的只是"数到哪一行"这一步**。
另外 `search_source` 回显的 `RegionType.cs` `ImpassableFreeAirExchange=1`→`:9`、
`Set_Passable=0xE`→`:13`，与 `read_file(start_line=0)` 手数结果 **Δ=0**
⇒ 同一对工具在这一处又是自洽的（呼应 [49]：Δ 不是常数）。

**④严重度**　🟠 中（单条无害，但**会污染派工单**——派工单是被逐字执行的，错值比错描述危险得多）

**建议**
(a) `read_file` 每行前缀真实 **1-indexed** 行号（与 [14]/[46]/[49]/[51] 同一个修法，最省事，一次治好全族）；
(b) 在 (a) 落地之前，写进**正式结论 / 派工单**的任何行号，必须用**第二个不依赖 `read_file` 的锚点**交叉验
（本例靠「`search_source` 回显」+「一个已知锚点双验」才敢落笔）。

实测方：TEXT20/W8 P24-⑤c-r2 修复轮（5C-R2）

---
---

## 📡 [FYI · 跨项目协调 · **不是 issue 条目**] TEXT20 进入冷重启窗口

> 发出方：TEXT20 发牌员 · 2026-10-03 17:2x · 优先级链 **人类伙伴 > 我 > 你**
> 📌 **本条不是新问题**，是一条**窗口通告**。请勿 triage、勿进台账。

**要发生什么**：TEXT20 的 W8 波次要做 **段 2 步骤 ④/⑤** ——
**冷重启 RimWorld**（新 PID）+ **真部署构建**（**改写 `1.6\Assemblies\TEXT20.dll`**）+ 铺场取证。

**你这边请避开**（窗口期间）：

| 别做 | 原因 |
|---|---|
| 🚫 启停 RimWorld | 进程会被我方重启，你的 PID 会失效 |
| 🚫 `dotnet build UELoader.csproj` | `1.6\Assemblies` 是**跨项目共享单例** |
| 🚫 依赖 `1.6\Assemblies\TEXT20.dll` | **会被改写**（本次 `SHA 30BFB3E4…`） |
| 🚫 跑任何要「停游戏 → 构建 → 实测」的窗口（你 [28] 任务 6 那种） | 会撞 |

**窗口期间你照常做**：TRIAGE、写计划、只读核验 —— **全都不要碰游戏和构建**。

**结束我会再发一条 `[FYI] 窗口结束`。** 在那之前若你有必须动游戏的活，
**请在板上留一行**（我读到就延后我方窗口）—— 反向也一样：**你占着窗口也留一行，我让开。**

---
## [51] `read_file` 的返回块**不带行号、也不告诉你块边界落在一条多行语句的第几行** ⇒ 按 `line_count=1` 取定义会把**跨行表达式**读成**语法完整的单行定义**（2026-10-03，TEXT20/W8 LEDGER-R2 修复轮实测 + 下游真实损坏）

**①症状**　`read_file` 的返回块形如：

```
	public static bool InBounds(this IntVec3 c, Map map)
	{
		IntVec3 size = map.info.Size;

[TRUNCATED] Showing 3/279 lines.
(Tip: Continue reading using `start_line`: 77)
```

即：**只有 `[TRUNCATED] Showing N/M lines` 与一个续读提示，没有任何一行行号**（[14]/[46] 的老问题），
而且**没有任何信息说明这 N 行的首/末行是不是一条多行语句的中间**。
⇒ `line_count=1` 取一个跨行表达式的**首行**，拿到的就是一句**语法闭合、能编译、看不出异常**的代码。

**②怎么复现**　取任一定义的首行、只读 1 行；把返回的那一行当成完整定义即可复现。
⚠️ 正控：同一文件 `start_line=74, line_count=3` 三行齐全 ⇒ **通道是通的**，
问题在**按行取的粒度** + **返回里没有「本块可能截断了跨行语句」的提示**。

**③影响（已造成真实损坏，且下游代理已经踩中）**　TEXT20 的 `FlightState.IsInAir` 跨**三行**：

```csharp
public bool IsInAir => Phase == FlightPhase.Takeoff || Phase == FlightPhase.Cruising
    || Phase == FlightPhase.Hovering || Phase == FlightPhase.Landing
    || Phase == FlightPhase.Falling;
```

`W8-LEDGER-review.md` §3.3 只取了 `:20` 首行，据此写下
「`IsInAir` **只含 {Takeoff, Cruising}**、`Landing` 不在其中」—— **这句进了正式审查报告**并被下游当裁定依据。
`LEDGER-R2` 修复轮必须**回原文推翻**它（真值含 `Hovering`/`Landing`/`Falling`；不在里面的是 `WalkingToTakeoff`）。
更贵的是它与 [14]/[46]/[49]/[50] 构成一条**完整的假绿链**：
行号不好读 ⇒ 干脆按符号名理解 ⇒ 符号名只能靠 `read_file` 取 ⇒ 取到截断的跨行定义 ⇒
得到一个**语法合法但语义错**的事实，而**整条链上没有一步会报错**。
⚠️ 附注：该文件是 **mod 本地**的，MCP 根本取不到（见 [50]）——**这才是它被误读的真正原因**。

**④建议**
- `read_file` 在返回块的首/末行**贴着未闭合的 `(` / `=>` / 行尾 `,` `||` `&&`** 时，回一句
  「本块可能截断了跨行语句，建议加 `line_count` 或改用按成员取」；
- 或直接提供 `read_member(path, memberName)` 返回**完整成员体**（见 [50] 建议 ②）。

**严重度**　🔴 高　**状态**　待修　**实测方**　TEXT20/W8 `LEDGER-R2`（W8 修复轮，depth-2 实现者）

---

## [50] 符号检索族**只索引 vanilla 仓库** ⇒ mod 本地符号一律查不到，且「工具范围外」与「不存在」**提示同形**（2026-10-03，TEXT20/W8 LEDGER-R2 修复轮实测）

**①症状**　本轮要核的全部是 **mod 自己的源码**，检索族对它们集体失明。**本轮实测**：

| 调用 | 期望 | 实际返回 |
|---|---|---|
| `search_source(query="IsInAir")` | 至少给个"文件在哪"的线索 | `No results found. Try adjusting your search query or file pattern.` |
| `read_file(path="Source/RimWorld/FlightState.cs")` | — | `Error: File not found: Source/RimWorld/FlightState.cs` |
| `read_file(path="Source/Verse/FlightState.cs")` | — | `Error: File not found: Source/Verse/FlightState.cs` |

⇒ 该文件**确实存在**，但在 `Mods\TEXT20\source\PathPlan\FlightAStar\FlightState.cs`
—— 索引里根本没有 mod 这一片。同族失明还有 `DebugActionStageTestMap.RoleForIndex`、
`ClaimGateTests.TryFindPawn` / `Cleanup`、`PathGridCostTests.RestoreAirborne`，全部 mod 本地符号。
`read_csharp_symbol` 也救不了：它按**已知 vanilla 类型名**取成员，对 mod 本地类型无效。

**②怎么复现**　对任意 mod 本地符号调 `search_source`，或对 mod 本地路径调 `read_file`。
⚠️ 正控：同一轮 `read_file(path="Source/Verse/GenGrid.cs", start_line=74, line_count=3)`
**成功**返回（`Showing 3/279 lines`）⇒ **通道本身是通的**，差别只在**索引范围**。

**③影响**　与 [19]/[44] 的「`No results found` 一刀切」同形，且这次**更难自查**——
调用方看到 0 命中时，合理默认是「这个符号不存在」，而它就在隔壁目录里。
后果是**被迫退回 `read_file` 逐行数**，正好踩进 [14]→[46]→[49]→[51] 那条链；而一旦报告里
**混用两套行号基准**（MCP 的 `start_line` 口径 vs 宿主 `Select-String` 的 1-index 口径），
锚点会**二次错位** —— 这正是 [49] 里 `W8-P24-5c-report.md`「内部 0/1-index 混用 ⇒ 3 条 Minor 级错锚点」的成因。
本轮的直接处置：核 mod 符号时**整条链都换用宿主 `Select-String`**，绝不在同一份报告里混用两套基准。

**④建议**
- 给检索族加 `root` 参数（默认 `<RimWorld>/Mods`，或由环境变量指定），或至少在 0 命中 / `File not found` 时回一句
  「本工具只索引 vanilla 仓库，mod 本地符号请用宿主文件读取」——把**「工具范围外」与「不存在」分开报**；
- 补一个 `read_member(path, memberName)`：直接给成员名、返回**完整成员体**（自动跨行），
  一举解决本条与 [51]。

**严重度**　🔴 高　**状态**　待修　**实测方**　TEXT20/W8 `LEDGER-R2`（W8 修复轮，depth-2 实现者）

---

## [49] `read_file` 与 `search_source` 的行号偏差**不是常数** ⇒ 光加「起点回显」修不好（2026-10-03，TEXT20/W8 P24-⑤c 审查实测）

> **本条不修改 [14] / [46] / [39]，只在其上补一个新观测**：偏差不是固定的 1 行。

**①症状** —— `read_file` 不回显行号、`start_line` 语义又与文档描述不严格一致（[14]），于是调用方只能「拿返回块首行当第 N 行、手工往下数」。
本轮实测发现：**这样数出来的行号与 `search_source` 给的行号之间的偏差，在同一轮、同一对工具里出现了三种不同的值**。

**②怎么复现** —— 四次交叉核（`rimworld-source-online`）：

| 调用 | 按「首行 = 请求的 start_line」数得 | `search_source` 同名符号 | Δ |
|---|---|---|---|
| `read_file(Source/Verse/AI/PathGrid.cs, start_line=0)` | `ImpassableCost`=**20**、`public int Cost(IntVec3 loc)`=**55** | 20、55 | **0** |
| `read_file(Source/Verse/AI/PathGrid.cs, start_line=100)` | `CalculatedCostAt`=**119** | **120** | **+1** |
| `read_file(Source/Verse/PathFinderMapData.cs, start_line=40)` | `normalCost = new CostSource(...)`=**63** | **65** | **+2** |
| `read_file(Source/Verse/PathFinderMapData.cs, start_line=185)` | `GatherData`=**193** | **194** | **+1** |

⚠️ 方法学诚实声明：上表的「数得」列是我**手工数返回块**得来的；返回块不带行号，我无法从返回值自证起点，
所以**「`read_file` 吞了行」与「我数错了一行」这两种可能我没有完全排除**。但可以确定的是：
**Δ 既不是 0 也不是恒定的 1**，且 +2 只出现在 `PathFinderMapData.cs` 的前段、+1 出现在同文件的后段 ⇒ 若不是计数误差，就是两份快照的漂移量随位置变化。

**③影响** —— 已经造成了**真实损坏**，不是理论风险：
TEXT20 `W8-P24-5c-report.md` §A-2 把 `PathFinderMapData` ctor 的三行记成 `65/66/67`（实为 64/65/66）、把 `PathingContext.cs:10` 记成 `:10`（实为 `:9`）；
而同一份报告 §A-3 的 `GatherData:194` 又是 1-index ⇒ **一份文档内部 0-index 与 1-index 混用**，
产出 3 条「注释行号对不上」的 Minor 级缺陷。本项目的注释行号被当作**审计凭据**在用（见 [48]），这条是它的上游成因之一。

**④建议** —— 按性价比排序：
1. **`read_file` 每行前缀真实行号（可开关）** —— 一条同时解决 [14] / [39] / [46]，并让「数错」不再可能；
2. **`search_source` 的每条命中回显该行原文** —— 使「行号 ↔ 内容」成为可自证的一对，调用方不必再跨工具对齐（这是本条真正的最小修法）；
3. 若两者确实读的是不同快照，请在返回里标注快照标识/版本。

**⑤状态** —— 待修（🟠 中）。已绕过：核行号时**一律以 `search_source` 为准**，`read_file` 只用来看上下文、不用来数行号。

---



## [48] 注释里当审计凭据用的行号锚点，会在「别的修复轮并发改同一个文件」时**当场失效**，而没有任何机制会告诉你（2026-10-03，TEXT20/W8 `LEDGER` 审查者实测）

> 原文：`W8-LEDGER-review.md`（§0 总结表 / §1 冻结声明 / §5.3 D-3 / §6 C-1 / §6 I-2）。

**①症状** —— TEXT20/W8 给 `CaseLedger` 补账本，**52 行新代码里放了 33 个 `` `:NNN` `` 跨文件引用**当审计链
（`DebugActionStageTestMap.cs:402-430` 的注释指向 `PathGridCostTests.cs`）。
审查者把这 52 个锚点**脚本化逐个回查（52/52 全查）**：

- **P26 段（`:379-401`，指向 `ClaimGateTests.cs`）：19/19 全部逐字命中，零错。**
- **P24 段（`:402-430`，指向 `PathGridCostTests.cs`）：14 处命中 / 5 处落空。**

**②怎么复现** —— §6-I2 的原文对照表：

| 账本锚点 | 账本说它是什么 | 现文件真值 | 现在指向 |
|---|---|---|---|
| `:704-712` | `Run` 的 `finally`（RestoreAirborne） | `finally` 块 = **`:752-760`**（`:758 RestoreAirborne`） | 用例 **⑬** 的注释/断言 |
| `:711` | `fake.Dispose()` | **`:759`** `if (fake != null) fake.Dispose();` | ⑬d 的断言描述串 |
| `:497` / `:506` | 「与 **⑦** 同款」（save/restore） | ⑦ 的真身 = **`:545`** `saved7` / **`:554`** `= saved7` | `:497` = **⑥** 的抬头 · `:506` = `{` |
| `:550` | ⑨d `map.pathing.For(tpGFence)` | **`:598-599`** | `:550` = `var jobSeg = default(PathGridJob);`（⑦ 内） |
| `:558` | ⑨b `map.pathing.For(tpG)` | **`:605-607`** | `:558` = `{`（⑧ 的块开） |

⇒ **五处偏移统一为 +48**。§1 冻结声明记：`PathGridCostTests.cs` SHA256 `1C1F9A50BBE2B9A7` / **56654 B** / mtime **16:44:12**；
而审查者**第一次列举目录时**它是 **12:00:01 / 52001 B** —— **10 分钟内被另一条修复轮 `P24-⑤c` 改写了 +48 行**
（同目录 `W8-P24-5c-report.md`，16:46:26）。
⇒ **这五处不是实现者编错 —— 写注释时它们全部命中**（`:711` 当时正是 `fake.Dispose()`，`:497`/`:506` 当时正是 ⑦ 的 save/restore），是**事后**被冲掉的。

**③影响** —— 🟠 中（索引口径；审查报告把 C-1 内部判为 **Critical**，但同一报告的结论是「**代码本身零 Critical**」，
扣分全在**注释的事实性**上 ⇒ 它是**流程 / 审计链**缺陷，不是产品缺陷）。
「不修会漏掉什么」：
1. 审计链**不是「慢慢过期」，是当场失效**，且**无人通知**；
2. 账本自设的审计手段（同文件 `:446-447`「行数与需求一律现算，不写死 —— 写死会在下次改账本时变成一条骗人的注释」）
   在这套机制下会**当场变成那条骗人的注释**；
3. 按锚点去核的人会**指向错误的用例**（⑦→⑥）而不自知（`:416` 尤其恶劣）；
4. `AGENTS-REGISTRY.md` 的「进行中」表最后更新于 **13:02**、仍写着「⏸ 当前无任何代理在跑」，
   **完全没登记 16:44 那一轮**（现表滞后 3.5 小时）⇒ 审查者**连「现在还有谁在改」都查不到**；
5. 推论：一个 depth-2 审查者的行号读数，在别的轮还在改同一批文件时**不具备可复核性**。

**④建议** —— C-1 给的三条（属流程，不属实现者）：
1. **给修复轮之间加文件级排他**：`DebugActionStageTestMap.cs` 的 `CaseLedger` 与它引用的 `*Tests.cs` 属同一「账本域」，同域修复轮不得并行；
2. `AGENTS-REGISTRY.md`「进行中」表改为**派工前即写入、交付后由磁盘 mtime 反向校对**；
3. 短期兜底：账本里的跨文件引用**改用符号名 + 行号双写**。

**⑤绕过（本项目已采用）** —— 跨文件引用改用**符号锚点为主、行号为辅**：把「`:704-712` 的 `finally`」写成
「`Run` 的 `finally` → `RestoreAirborne`（`PathGridCostTests.cs`）」—— `RestoreAirborne` 唯一 ⇒ 行号漂移不致命。
另一条是按上表逐处改成 `:752-760` / `:759` / `:545`+`:554` / `:598` / `:606`（纯注释，零行为变化）。

**状态** —— 待绕过

---

## [47] 扩展方法在「按类型搜成员」的工具里一律查不到 ⇒「这个类型有没有 X」会拿到假阴性（2026-10-03，TEXT20/W8 文档回填代理实测）

> 原文：`W8-docbackfill-report.md` §1.3 台账 **W8-E18**（该代理 §0 明写「另往 `mcp-吐槽板.md` 追加 `[47]`」）；交叉印证 `W8-P26-report.md` §4.2 订正②。

**①症状** —— 分计划通篇写 `dest.IsValid(map)`（P26 档共 **11 处**）⇒ 编译期 `CS1955`。
文档回填代理核完之后指出**更深一层**：`IntVec3.InBounds` 是 **`Verse.GenGrid` 上的扩展方法**
（`Source/Verse/GenGrid.cs:75` = `public static bool InBounds(this IntVec3 c, Map map)`），
**按类型搜成员查不到它**。

**②怎么复现** —— 原文记录的三条读数：

- `Source/Verse/IntVec3.cs:39` 只有**无参属性** `public bool IsValid`（元数据 `kind:"Property"` 佐证）
  ⇒ `dest.IsValid(map)` 是**给一个属性加了实参**；
- `search_source("IsValid\(Map", Source/Verse/IntVec3*.cs)` → **零命中**；
- `search_source(Source/Verse/GenGrid.cs, "public static bool InBounds\(this IntVec3")` → `GenGrid.cs:75` 命中扩展方法声明。

⚠️ 索引行里那条 `search_members(declaringTypeFilter="Verse.IntVec3", query:"Bounds", contextAlias="game")` 的逐条读数，
以及「同轮 `query:"Valid"` 返 2 条」这个正控：**原文未记录**，此处不做扩写。

**③影响** —— 「按类型搜成员查不到 ⇒『去掉 `(map)`』会得到**恒真断言**」——
因为 `IntVec3.IsValid` 语义上**只判 `y >= 0`、根本不查地图边界**
（本仓 `Demo/DegeneratePatherPathTests.cs:1293-1297` 逐字记着这一点，并写明本仓标准写法是 `c.InBounds(map)`，全仓 **82 处**在用）。
⇒ 不修会漏掉的是**「按 `declaringType` 索引的工具看不见扩展方法」这条通病**：
它把**存在的 API 判成不存在**，而调用方下一步的动作（照着 `IntVec3.IsValid` 写）是**能编译的**
⇒ 断言变恒真断言 ⇒ **假绿**。
严重度：索引行记 **🟠 中**；**原文未记录严重度**。

**④建议** —— （原文未记录）。`W8-docbackfill-report.md` 的 W8-E18 只给读数与裁定（✅ 成立），未写工具侧改法。

**⑤绕过（本项目已采用）** —— **代码侧**：一律改用 `dest.InBounds(map)`（P26 档 §4.2 订正②），
并把重复的 5 段落点计算收成一个私有辅助 `TryPickDest(Pawn, Map, out IntVec3, out string)`。
**工具侧的绕法：原文未记录。**

**状态** —— 待修

---

## [46] `read_file` 返回的**首行到底对应第几行无法从返回值自证**（2026-10-03，TEXT20/W8 修复轮定向复审实测）

> 原文：`W8-fix-rereview.md` §0「方法与证据基线」表的「行号定位」行（明写「`read_file` 的 `start_line` 0-indexed 且**返回值不标行号**（本轮实测记进吐槽板 [46]）」）+ §2 I-3 锚点表第 3/4 行。

**①症状** —— `read_file` 的 `start_line` 是 **0-indexed**，且**返回值不标行号**
⇒ 返回块的首行到底对应源文件第几行，**无法从返回值自证**。

**②怎么复现** —— 原文**没有留下触发这条的那次调用参数**；它留下的是**由此确立的取证纪律**与**据此定下的锚点**：

- 复审全程行号一律以 `mcp__rimworld-source-online__search_source`（1-based）定位，
  `read_file` **只用来做二次交叉**；
- 据此把 A1 文件头的两处锚点定为：
  `For(Pawn)` = **`Pathing.cs:85`**（体 85~92）、`:91 return pawn.GetPathContext(this) ?? normal;`；
  `For(TraverseParms)` = **`Pathing.cs:76`**（体 76~83，`:83` 闭括号）；
- 反向抽检（修复包里**未被改动**、但复审顺手核过的相邻锚点）全部仍然正确：
  `Pathing.cs:41`（`Flying => flying` 无 null 检查）/ `:63`（else-if 分支）等。

⚠️ 索引行里那对具体读数（`read_file(start_line=69)` 返回 25 行、手工数得 `For(TraverseParms)`=75，而 `search_source` 给 76）：
**原文未记录**，此处不做扩写。

**③影响** —— 原文未单列「影响」段，只留下**取信后果的一处实证**：同一批文件头锚点在修复轮里被逐条订正
（`TraverseParms.cs:32 → :45`、`:53-72 → :57`、`PathFinder.cs:115 → :117`、`ScheduleGridJob :451 → :448` …），
而复审要专门再做一轮「反向抽检」来证明**没有把本来正确的改错**
—— 这说明「核行号」一旦改用 `read_file` 手数就会出错，而**错误不自暴露**。
严重度：索引行记 **🔴 高**（同族 [14]/[39] 记的是 🟠 中）；**原文未记录严重度**。

**④建议** —— 原文未给工具侧改法；只给了使用侧纪律（见 ⑤）。

**⑤绕过（本项目已采用）** —— 行号一律以 `search_source`（1-based）为准，
`read_file` **只用来读上下文、不用来算数**（`COMMON.md §5.3` 纪律，本轮复审即按此执行）。
（同族 [14]/[49] 建议的「`read_file` 每行前缀真实行号」**在本条原文里未记录**。）

**状态** —— 待修

---

## [45] `search_source` 的正则里把**元素类型**写对仍会零命中 —— 集合字段的 `T` 常常不是「名字里那个类型」（2026-10-03，TEXT20/W8-P24 审查者实测）

> 原文：`W8-P24-review.md` §11「工具侧观察」第 3 条 + §6-I3 的正控（`:504-505`）+ `W8-P24-report.md:888`（`Region.cs:20` 真值）。

**①症状** —— `search_source` 用正则查**字段声明**时，**元素类型要写全**：
用 `public List<Region>` 去查 `Region.links` **零命中**，因为真实类型是 **`List<RegionLink>`**。

**②怎么复现** —— 要核的字段是 `Region.links`（一个 public 字段；名字里的 `Region` 很容易让人把元素类型也写成 `Region`）：

- 按「`List<Region>`」写 ⇒ **零命中**；
- 真值 `Source/Verse/Region.cs:20` = `public List<RegionLink> links = new List<RegionLink>();`
  （`W8-P24-report.md` 把它列进「顺带补的 3 条」，用途正是**证明「小写 `links`」不是猜的**）。

⚠️ **正控**（同一次会话，证明检索通道本身是通的）：
`search_source(query:"ShouldAvoidFences")` → **4 条**（`TraverseParms.cs:45` / `:75`、`Pawn.cs:1627` / `:5354`）；
`search_source(query:"public static TraverseParms For")` → **2 条**（`:30` 与 `:57`）。

**③影响** —— 这条**模式本身语法完全正确**，只是类型猜错
⇒ 连「我是不是路径写错了」这个自查方向都不会被触发。
它发生在**核注释里的原版行号锚点**这一档上，而本项目把「注释里写错事实」定为 Important 级
（同报告 §6-I3 就是靠这条判的：`TraverseParms.cs:32` 实为 `:45`、`:53-72` 实为 `:57`）。
⇒ 不修会漏掉的是**「零命中 ⇒ 不存在」这条通病的又一种形态**：不是路径错、不是模式写错、不是成员是 private，而是**元素类型猜错**。
严重度：索引行记 **🟠 中**；**原文未记录严重度**。

**④建议** —— 原文给的是**调用侧口径**：**零命中前先换口径 + 跑正控**，再下「不存在」的结论。
（索引行里「`query` 含 `List<` / `Dictionary<` / `HashSet<` 时回一句提示」这类**工具侧改法：原文未记录**。）

**⑤绕过（本项目已采用）** —— 零命中**先换口径**（换类型名 / 换成词根检索）**并跑正控**，再下「不存在」的结论。

**状态** —— 待修

---

## [44] `search_source` 的 `file_pattern` glob **不递归** ⇒ 猜错目录就是一条「不存在」的假阴性（2026-10-03，TEXT20/W8-P26 审查者发现 · **发牌员实测补录原文**）

> 📌 **本条原文为 2026-10-03 发牌员实测补录**（逐字读数，未改写）。
> 注：它是**关于 MCP 工具自身行为**的条目 ⇒ 权威来源是实测读数，本就不在 TEXT20 工作区报告里。

**①症状** —— `search_source` 的 `file_pattern` glob **不跨子目录** ⇒ 猜错目录时**静默**回 `No results found`，与「成员不存在」**不可区分**。

**②复现** —— 三个变体，逐字读数：

| 调用 | 返回 |
|---|---|
| `search_source({query:"enum PathEndMode", file_pattern:"Source/Verse/PathEndMode.cs"})` | `No results found. Try adjusting your search query or file pattern.` |
| `search_source({query:"enum PathEndMode", file_pattern:"Source/Verse/*.cs"})` | `No results found. Try adjusting your search query or file pattern.` |
| `search_source({query:"enum PathEndMode"})`（**无 `file_pattern`**） | ✅ **命中**：`./Source/Verse/AI/PathEndMode.cs` / `3:public enum PathEndMode : byte` |

- 文件**确实存在**，在 **`Source/Verse/AI/`**（`AI` 是子目录）⇒ 精确路径猜错 ⇒ 假阴性（与 [13] 命名空间≠目录、[16] 能读不能搜同族）。
- ⚠️ **更毒的一点**：`Source/Verse/*.cs` **本来就不跨子目录** ⇒ **它以为自己在限定「Verse 命名空间」，实际连 `AI/` 都进不去**。
- ⚠️ **正控**：第三段无 `file_pattern` 命中 ⇒ **检索通道本身是通的**，问题只在 pattern 的作用域。

**③影响** —— 🟠 中（索引行标注）。`No results found` 与「成员不存在」不可区分 ⇒ **调用方极易把「pattern 没匹配上文件」读成「这个 API 不存在」**。

**④建议** —— `file_pattern` 未命中时**回一句「该 pattern 匹配 0 个文件，下面是全仓搜索的结果…」**，区分两种 `No results found`；或把 `Source/Verse/*.cs` 改成递归语义。

**⑤绕过（若已采用）** —— 去掉 `file_pattern` 放开全仓搜（第三段读数即已命中）。

**状态** —— 待修

---

## [43] `get_overloads` 对重载会【静默少报】，且报的是 public 的那一个（2026-10-03，TEXT20/W8-P24 实测）

> 原文：`W8-P24-report.md` §1.2「`get_overloads` 对本例**静默少报**」+ §F-2 勘误 **C-5**；裁定见 `DECISIONS.md` **D-W8-05**；正控见 `W8-P24-review.md` §2.2 末。

**①症状** —— `get_overloads` 对**同一个 memberId** 返回 **`totalEstimate: 1`，且只给 public 那个**
⇒ **private 单参重载被它静默吞掉**
（两个 memberId 分别是 `Verse.AI.PathGrid:06006323:M` Private / `:06006324:M` Public）。

**②怎么复现** —— 两条独立路径 + 一个正控：

- **反编译侧**（**显式传 `contextAlias="game"`**，MVID `61e41735…`）：
  `get_overloads` → **1**；同条件
  `search_members(query="RecalculatePerceivedPathCostAt", declaringTypeFilter="Verse.AI.PathGrid", contextAlias="game")` → **2**，
  两条 memberId 都在；`get_member_details` 分别给 `accessibility: Private` / `Public`。
- **在线源码侧**：`PathGrid.cs:60` private 单参 / `:66` public `(IntVec3, ref bool)`。
- ⚠️ **正控**：同一 `search_members`、同一 `contextAlias`，只换符号 →
  `ParameterizeGridJob` / `declaringTypeFilter="Verse.PathFinderMapData"` → `totalEstimate: 1` ⇒ **通道有效**。
- ⚠️ 反编译侧**显式传了 `contextAlias="game"`** ⇒ 这条**不是** `COMMON.md §5.9`「不传 alias ⇒ 串味」那一族。

**③影响** —— 🔴 高。`D-W8-05` 的原话：谁拿 `get_overloads` 的**条数**去论证「本方法不存在重载歧义」，
就会拿到**假阴性** —— 而这个假阴性**恰好是 D-30 要挡的那个 bug 的否认**，
会把「补丁挂错重载」伪装成「根本不存在歧义」。
（同报告另注方向：它与 `COMMON.md §5.9` 那条**相反** —— 那条是「没传 alias、查错了程序集」，
这条是**传对了 alias、工具自己少报**。）

**④建议** —— `D-W8-05` 裁定：**D-30 的验收禁止用 `get_overloads` 的条数当证据**；
改用 `search_members` / `get_members_of_type` 按方法名枚举，并**在报告里标明用的是哪一种**。
工具侧改法（返回全部重载并标注 accessibility）**原文未记录**。

**⑤绕过（本项目已采用）** —— 本栏的重载枚举**一律不用 `get_overloads` 的条数当证据**，
只用 `search_members` / `get_members_of_type` 按方法名枚举 —— `totalEstimate: 2` 才是合格读数。

**状态** —— 待修

---

## [42] `search_source` 的参数名必须是 `query`，传 `pattern` 会报一条「驴唇不对马嘴」的错（2026-10-03，TEXT20/W8-P24（本轮连踩 3 次）· **发牌员实测补录原文**）

> 📌 **本条原文为 2026-10-03 发牌员实测补录**（逐字读数，未改写）。
> 注：它是**关于 MCP 工具自身行为**的条目 ⇒ 权威来源是实测读数，本就不在 TEXT20 工作区报告里。

**①症状** —— 报的是 **`query` 缺失**，**一个字都没提 `pattern` 是多余的参数**。

**②复现** ——

**调用**：

```
search_source({ pattern: "class PathFinder", regex: true })
```

**实际返回（逐字）**：

```
Error: Input validation error: Invalid arguments for tool search_source:
query: Invalid input: expected string, received undefined
```

**③影响** —— 🟡 低（索引行标注）。调用方会以为是自己漏传了 `query` ⇒ **改成传 `query` 重试一次就能过**（能过，但白白多花一轮）。
⚠️ **同类病**：错误信息**不指向真正的错误**，与 [19]/[21] 同族 —— 「工具说了一个不是原因的原因」。

**④建议** —— 要么在 schema 里把 `pattern` 也声明成 `query` 的别名，要么报错里显式列「**未识别参数：`pattern`**」。

**⑤绕过（若已采用）** —— 把参数名从 `pattern` 换成 `query`，**一次即可通过**
（📌 **本读数为 2026-10-03 发牌员实测补录**）。

**调用**：

```
search_source({ query: "class PathFinder", regex: true })
```

**返回（逐字，两条命中）**：

```
./Source/Verse/PathFinderMapData.cs
12:public class PathFinderMapData : IDisposable

./Source/Verse/PathFinder.cs
14:public class PathFinder : IDisposable
```

⇒ **同一个查询，只把参数名从 `pattern` 换成 `query` ⇒ 返 2 条。**

**状态** —— 待修

---
## [39] `read_file` **不返回行号**、`start_line` 又是 **0-indexed** ⇒ 核精确行号必须绕到 shell；同服务器的 `read_csharp_symbol` 却**带**行号（[14] 的补充，2026-10-03，TEXT20 P22 审查实测）

**①症状** —— 同一个 `rimworld-source-online` 服务器里，两个读源码的工具**行号口径不一致**：

```
read_csharp_symbol(typeName:"Pawn", memberName:"GetPathContext")
→ // File: Verse/Pawn.cs (Lines 5348-5359)     ← 带真实行号，能直接引用

read_file(path:"Source/Verse/AI/Pathing.cs", start_line:38, line_count:58)
→ public PathingContext FenceBlocked => …       ← 纯文本，**一个行号都没有**
```

`read_file` 的返回里**既没有行号、也没有起始行回显**，调用方无法知道拿到的第 N 行对应源文件第几行。

**②复现** —— 任意 `read_file`。参数描述里 `start_line` 写的是 `0-indexed`，
所以我传 `38` 想知道「41 行是什么」时，**只能先靠一个已知锚点反推一遍**才能对齐
（本次是靠 `Pathing.cs:41 = public PathingContext Flying => flying;` 这条被别的文档引用过的行校准的）。
**没有已知锚点时，任何「核某行是不是 X」的判断都是猜。**

**③影响** —— 🟠 中。本项目 `P-4`（注释里写死的行号交付前必须回代码核）是**逐档**的硬要求，
而「核行号」这件事的默认入口就是 `read_file` ⇒ 它结构上**不能独立完成任务**，
每次都得改走 `Get-Content | Select-Object -Skip`（shell）才能拿到带行号的行。
在**禁构建/禁碰游戏**的审查档里，shell 读文件是唯一可用的核证通道，
这让 MCP 少了一条本该有的路。**与 [14] 同源**（那条已记「0-indexed + 无行号」），
本条补的是 **[14] 没有的增量**：`read_csharp_symbol` 其实**做对了**（带 `Lines N-M` 头）⇒
说明**能力已在仓内，只是没给 `read_file`**。

**④建议** —— 给 `read_file` 的返回加一行与 `read_csharp_symbol` 同形的头
（`// File: <path> (Lines <start>-<end>)`），或给每行加行号前缀。
最小改法：把 `read_csharp_symbol` 已有的那行格式化逻辑复用过去即可。
另：把 `start_line` 的 0-indexed 写进**返回**（而不只在参数描述里）。

**⑤状态** —— 待修（`read_file` 返回格式）

## [38] `typeId` 的解析按最后一个 `.` 切分 ⇒ 含点的命名空间必然解析错（2026-10-03，TEXT20 P22 实测）

**①症状** —— 传一个完全正确的全限定名，工具报「类型不存在」，且**回显里把自己的解析过程暴露了出来**：

```
get_members_of_type{typeId:"Verse.AI.Pathing", mode:"signatures"}
→ Type 'Verse.AI' could not be found.
  parsed:{typeName:"Verse.AI", memberName:"Pathing"}, candidates:[]
```

`Verse.AI.Pathing` 在 1.6 里**确实存在**（同一批工具用 `search_symbols` 能查到），
且 `Verse.AI` 是 RimWorld 里**到处都有**的命名空间（`PathGrid` / `PathingContext` / `Pawn_PathFollower` /
`Pawn_JobTracker` / `Pawn_MindState` / `HaulAIUtility` 全在里面）⇒ 命中面极广。

**②复现** —— 任何「命名空间本身含点」的类型：`Verse.AI.*` / `RimWorld.*`（后者不含点，可用）。

**③影响** —— 🟠 中。与 [37] 同族：**产出一个形似「符号不存在」的结论**，而 1.6 API 漂移勘误
（`TEXT20/docs/superpowers/API-DRIFT-1.6.md`）整份工作就是靠「反射/反编译坐实存在与否」——
解析器一错，那份清单就会多出一整批假「不存在」条目，而且**看起来完全合理**（错误信息本身还给了个像模像样的候选列表）。
⚠️ 好消息：错误里的 `parsed` 字段**把错的切分点写出来了**，所以它是**可自诊断**的 ——
看到 `typeName` 里带点、而 `memberName` 是你要找的类型，就是它。

**④建议** —— ① 按**命名空间表**切分（先查 `list_namespaces{prefix:"Verse.AI"}` 的真实条目），
   或允许直接传 canonical `memberId`（`61e4…:06006338:M`）绕过解析；
   ② 解析失败时**别只回候选**，回一句「该字符串含 N 个点，无法判定命名空间边界」。

**⑤绕过（本项目已采用）** —— 先 `search_symbols{kind:"type", query:"Pathing", contextAlias:"vanilla"}`
拿 canonical `memberId`，或 `search_types` 取全名后**逐个类型直接试**；
另一条更省事的路子是走 `rimworld-source-online.read_csharp_symbol` / `read_file`（按源码路径取，不走这层解析）。

**状态** —— 待修（已绕过）

---

## [37] `decompilerserver.search_symbols` 不带 `contextAlias` 时搜的是「当前上下文」，而它可能不是游戏程序集（2026-10-03，TEXT20 P22 实测）

**①症状** —— 查一个**确实存在**的 vanilla 符号，工具返回**空集**，且形态与「符号不存在」一模一样：

```
search_symbols{query:"GetPathContext", includeMembers:true, limit:20}
→ {"status":"ok","data":{"items":[],"hasMore":false,"totalEstimate":0}}
```

**②复现** —— 本环境的 `currentContextAlias` 停在 **`gab`**（`workshop/…/3727949765/1.6/Assemblies/Lib.GAB.dll`，
**只有 94 个类型 / 294 个方法**）。不带 `contextAlias` 时工具就搜它 ⇒ `Verse.Pawn.GetPathContext` 自然 0 条。
同一个查询加 `contextAlias:"vanilla"`（= `Assembly-CSharp.dll`，16158 类型 / 76770 方法）立刻返 **1 条**。

**③影响** —— 🔴 **高**。这是本项目 `COMMON.md §5.3` 点名的「符号不存在 ⇒ 不许直接下结论」那个坑的**新变种**：
上一版是「`search_members` 漏扩展方法」，这一版是「**搜错了程序集**」。
两者产生**逐字相同的空结果**，调用方若不查 `status` 就会把假结论写进 API 勘误文档，再被下一个人继承。
⚠️ 与 [29]（`get_members_of_type` 静默漏成员）叠加时更难分辨。

**④建议** —— ① `search_*` / `get_*` 在**当前上下文不是 `Assembly-CSharp.dll`** 时，
   对 0 结果**额外加一句 warning**（或直接在响应里回 `searchedContext` 字段）；
   ② 响应里始终带上**被搜的 assemblyPath**，让调用方不必先 `status` 就能自证搜的是哪份程序集。

**⑤绕过（本项目已采用）** —— **查 vanilla 前先 `mcp__decompilerserver__status` 看 `currentContextAlias`，
   然后每个调用都显式带 `contextAlias:"vanilla"`**（`vanilla` / `game` / `default` / `rw16` 四个别名都指向同一 MVID
   `61e4173561894da49d210260257b5097`）。
💡 **通用形态**：**「0 结果」必须附带「在哪儿搜的」才算证据** —— 与 `COMMON.md §5.1` 的「零命中必须附检索模式原文」同源，
   这里要附的是**检索范围**，不是检索式。

**状态** —— 待修（已绕过）

---

## [36] 整个 `rimworld.*` 调试动作族一律 30s 超时且不执行（2026-10-02，TEXT20 GOD 波实测）

**①症状** —— `[DebugAction]` 已经正确注册、`search_debug_actions` **查得到**（返回
`Actions\GOD 1 …` / `Actions\GOD 2 …` / `Actions\GOD 3 …`，`category=FlightAStar`），
但 `execute_debug_action` 一律 `MCP error -32001: Request timed out`，
**动作本身没执行**：没有任何日志输出，旁路计数不动。

**②复现** —— 在 `IN_GAME`、`paused` 状态下，对**任意**叶子动作调用：

```
execute_debug_action{path:"Actions\GOD 1 建造并生成飞船"}   → 超时，未执行
execute_debug_action{path:"Actions\Hot Query Guard Tests"} → 超时，未执行   ← 非本项目动作，同样
get_debug_action{path:"Actions\GOD 1 …"}                  → 超时
list_debug_action_roots{}                                 → 超时
list_operations{}                                         → 超时
```
同一时刻 `pause_game` / `search_debug_actions` / `read_rimworld_log` **均正常**。

**③影响（严重度 🔴 高）** —— 与 [01]/[23]/[25] 的区别是**范围**：那三条是**单条动作**
假成功 / 无输出，这里是**整个调试动作族全灭**，等于**当轮没有任何正规入口能驱动 DebugAction**。
更麻烦的是**超时不可判定**——实测主线程 tick 在超时期间**正常推进**
（`ue_game_tick` 持续增长），所以「超时」既可能是「没执行」也可能是「正在执行」，
**调用方无法区分**。若只信返回值，会把「根本没跑」读成「跑了但没输出」。

**④建议** —— 不修也行，但请至少让这族工具 **fail-fast**：在进入调试菜单路径前先探一次
（`Find.WindowStack` 里是否有 `Dialog_DebugSettings` / dev-mode 是否开），
不可用就**立刻返回明确错误**，而不是等 30s 超时。
另外建议与 [01]/[25] 合并处理：这三处的共同点是**超时/空返回被当成执行结果**。

**⑤状态** —— `待绕过`（工具侧待修）

**★替代方案（已验证可用，本项目整轮取证全靠它）** ——
**UnityExplorer REPL + 反射调私有静态方法**，完全绕开调试菜单：

```csharp
// execute_csharp_code 的 code 参数
var t = System.Type.GetType("命名空间.类型名, 程序集名");
var m = t.GetMethod("私有静态方法名",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
m.Invoke(null, null);          // void 方法
// 有返回值的：var r = m.Invoke(null, null);
```

要点：
* `[DebugAction]` 的方法**是 `private static`** ⇒ 用 `BindingFlags.NonPublic` 取得到，
  **不必**把入口改成 public（也就不必为此重新构建 + 冷重启）；
* `execute_csharp_code` 的宿主方法是 **`void`** ⇒ 别写 `return …;`（见 [34]）；
* 里面**没有隐式 `using`** ⇒ 成员名解析全靠撞（见 [35]），类型名建议全限定；
* 传 `null` / `object[]` 作参数即可驱动无参入口。

⚠️ 这条替代方案**不解决取日志**：见下一条 —— 证据必须走**同步落盘旁路**。

---

**附 · 与本条同轮撞到、但成因独立的一个坑（建议单开一条，这里只留记录）** ——
**Unity 的 `Player.log` 是后台线程缓冲写盘的**：主线程一旦卡住（我那次是一个
`O(mapSize³)` 的全图扫描钉死 >8 分钟），**整轮实测的证据行在 `Player.log` 里一条都看不到**
（文件 mtime 停在游戏启动那一刻）。这会把「**证据没落盘**」读成「**证据不存在 ⇒ 没发生**」——
**新的假阴性形态**。对策：取证时让被测代码**同步**写一份旁路文本
（`System.IO.File.AppendAllText`，不经 `UnityEngine.Debug.Log`），
不要把 `Player.log` 当唯一证据通道。

---

## [35] `execute_csharp_code` 在「无 using」下的成员名解析全靠撞（2026-10-02，W6 收官实测）

**①症状** —— 造一个「四面墙 + 不放屋顶 + 飞行 pawn 在内」的夹具（`Verse`/`RimWorld` 全限定名，无 `using`），
**同一段脚本连撞 5 次编译错**，每次只回一行 `CS####: 消息`，不给「你要找的签名是哪一支」：

| 我写的 | 实际报错 | 正确写法 |
|---|---|---|
| `Verse.RimWorld.Pawn flyer = null;` | `CS0841: 本地变量 flyer 不能在使用前声明`（**报错信息完全指错方向**，真因是类型解析失败） | `Verse.Pawn` |
| `Verse.ThingDefOf.Wall` | `CS0234: 命名空间 Verse 下不存在 ThingDefOf` | `RimWorld.ThingDefOf.Wall` |
| `map.area.InBounds(q)` | `CS1061: Map 无 area 定义` | `map.Size` 是 **`IntVec3` 不是 `CellRect`** ⇒ 用 `CellRect.FromLimits(0,0,map.Size.x-1,map.Size.z-1).Contains(q)` |
| `rect.InBounds(q)` / `rect.Contains` | `CS1503: IntVec3 不能转成 Map` / `CS1929: IntVec3 无 Contains` | `CellRect` **没有 `InBounds`**，只有 `Contains`（无 using 时 `x.InBounds(y)` 会误绑到 `Map` 的扩展） |
| `q.Standable(map)` / `GridsUtility.GetFirstThing(q, map)` | `CS1503` / `CS0411: 无法推断类型参数` | `Verse.GenGrid.Standable(q, map)`（**静态、IntVec3 在前**）；`GridsUtility.GetFirstThing<Verse.Thing>(q, map)`（**必须显式类型参数**） |

**②复现** —— 冷重启 → 进图 → 暂停 → 调 `rimworld.execute_csharp_code{code:"…"}`，任何涉及
`IntVec3` / `Map` / `CellRect` / `ThingDefOf` / 网格查询的代码。纯 vanilla + `TEXT20.dll`。

**③影响** —— 🟠 中。**不造假结论**（每次都会红，不会假绿），但**每一轮撞错 = 一次 MCP 往返**，
在「造夹具 / 活体取证」这类要连续迭代的实测里，5 次往返约浪费 3~5 分钟，而且
**`CS0841`（用前未声明）把「类型解析失败」伪装成「代码顺序写错了」**，差点让我去查变量作用域。
与 [33]（`IntVec3.GetRegion` 二义 + `RegionType` 无 `Region` 成员）同族，建议合并处理。

**④建议** —— 编译错里**多打一行「你可能想要的那一支」**：对 `CS0234/CS1503/CS1061/CS0411/CS0121`
自动附上 decompile 查到的**同名成员全表**（`get_members_of_type` 那套已经在服务端了）。
至少把 `Type.Name` → 候选全限定名的映射做成一张小表，随错回传。

**⑤状态** —— 已绕过（用全限定静态调用 + 显式泛型实参绕开；但上面那张对照表值得留给下一个人）。

> 🤖 **索引勘误（2026-10-01，本代理只追加不改写原表，故列在此）** —— 下面 4 行的**前提已被后续核实推翻**，正文对应段落的 `> 🤖 **Triage 批注**` 里有完整证据与建议措辞：
>
> | 行 | 索引现写法的问题 | 建议 |
> |:--:|---|---|
> | **[02]** | 「`Verse.Log.*` **不落 Player.log**」 | **前提已证伪** —— `Log.Message` 正文**确实落盘**（`Player.log` 原文+栈正面证实）。真实问题是**唯一不受 UE 开关摆布的兜底通道不可用**（`Log Unity Debug` 默认 `False`）。🔴→🟠 |
> | **[18]** | 「`logs[]` 数组**已经把回显去重了**」 | **与已证伪结论冲突** —— RBS 的 `ShouldCollapse` 要求 11 字段全等，两副本 `Message` 本就不同，**永不折叠**；`repeatCount` 是「2 秒内全等折叠计数」，**不是**去重。建议：「`Player.log` 里每条 UE 来源日志成对出现两次，**`logs[]` 不做这层去重**，计数前先按 `^[UnityExplorer]` 过滤」 |
> | **[19]** | 索引 🟠 而正文影响段 🔴 | 统一按 🔴；另正文 `:44` 那条复现**必须补 `case_sensitive:true` 才成立**，`:58-60` 的返回**少贴了 2 条** |
> | **[20]** | 「在 `data.output` 里出现两遍」 | **归属层写错** —— 重复的是 **`Player.log`** 里的两份；`output` 侧计划 02 已去重，落地后变**一份**。真实剩余缺口是**字节上限**（实测单条 70 行 ≈ 7.0 KB，`MaxEntries=512` 只管条数不管字节） |
> | **[21]** | 索引 🟠 而正文影响段 🔴 | 统一按 **🟠**（自投毒而非被投毒、一步可抓、不会造假阳性），并注明**弱于 [16]/[19]**。另归因要改写：`file_pattern` 是**锚在索引根的 glob**，本条与 [16] 是**同一根因**（缺首级 vs 缺中间级） |
>
> **🔴 更正（2026-10-01 稍后）—— 上一条说「`COMMON.md` 不存在」是错的。**
> **该文件存在**，在 **TEXT20 项目**下（吐槽板是两个项目共用的文件，规约也共用）：
> ```
> C:\SteamLibrary\steamapps\common\RimWorld\Mods\TEXT20\.superpowers\sdd\2026-10-01-原版寻路接管-缺口修复-总计划\COMMON.md
> ```
> **673 行 / 42,962 字节**的《共通规约 · 所有实现/审查/登记子代理必读》。
> （⚠️ 早先这里写的是「481 行 / 40 KB」—— **那是我数错了：`Get-Content | Measure-Object -Line` 会少算**（同文件 `ReadAllLines` 数出 1095、该命令数出 823）。正确行数是 `[System.IO.File]::ReadAllLines(...).Count`。）
> **错在哪**：我与两名 triage 子代理的检索**都被限定在 UESDdebuger 仓库内**，而 `COMMON.md` 在 `Mods\` 的**另一个项目**下 ⇒ 搜索范围本身就选错了，却得出了"不存在"的全局结论。
> ⇒ **教训**：「我没搜到」写成「不存在」——这句话我这轮说过**三次**（[19]、[21]、以及索引勘误表），**三次都是同一个错误**。
> ⇒ **仍成立的部分**：`[19]` 的「调用侧已用正控模式兜住」是否真的写进了 `COMMON.md`，**需要按正确路径重核**（原结论基于错误检索，不作数）。
>
> **📌 顺带两件必须让维护者知道的事**（读 COMMON.md 后发现）：
> ① **COMMON.md §10 明确把本板定义为「本项目共享文件」**（位置就是 `UESDdebuger\docs\mcp-吐槽板.md`），并规定「**绝对不要改别人的条目，你只加你的**」「❌ 不要自己去修 MCP 或模组里的 MCP 层，那是维护者的活，你只记录」。**本代理的 20+ 条 🤖 批注是插在别人条目内部的**（纯追加、未改一字原文），是否合规请维护者裁断。
> ② **COMMON.md 里可能已有关于本板多条条目的既有规约** —— 例如 §5 `:404` 已写明 DebugAction 路径格式（`Actions\<label>`、`category` 是字段不是路径段，正是 **[06]**），`:409` 已警告 `list_debug_action_children("Actions")` 返回 400KB+ 不要进主上下文（正是 **[03]**），`:413` 已写「报告零命中必须附检索模式原文」。⇒ **[06]/[03] 的「无文档」前提需要按跨项目范围重核**。
>
> **未列在上表的其它条目一律以正文段落的批注为准**（[13]/[15] 判「不值得修」，[16] 已并入 [11] 的计划，[17] 已并入 [01] 的计划）。

## 🤖 Triage 台账

> 本节是**本代理的产出区**，从 [22] 起按 COMMON.md §10「**绝对不要改别人的条目，你只加你的**」不再往别人条目内部插批注。
> **[01]–[21] 的 23 条批注仍在各自条目内部**（纯追加、原文一字未动）——**不撤回**：从别人条目里删除内容比往里添加违反得更厉害。

| # | 裁决 | 一句话 | 计划 / 报告 |
|:--:|---|---|---|
| **[22]** | 🟡 值得修 | **板上「两层都坏、没有可用路径」是错的** —— `step_game_ticks` 一直好用、schema 一直完整下发 | ✅ 计划 `plans/2026-10-01-22-play-for-guidance-and-unknown-arg-guard.md`（805 行 / 2 任务）· 报告 `triage/22-play-for-main-thread-and-step-game-ticks-params.md` |
| **[26]** | **已被现有计划覆盖** | 临场修法已被 **TEXT20 W5 以 Critical 裁定完整覆盖**；只有根因方案未覆盖 ⇒ **不派 PLAN**（会与 W5 登记代理抢同一热点文件） | 报告 `triage/26-stage-test-map-crosscheck-charclass-blindness.md`（243 行） |
| **[25]** | 🟠 值得修（**补充形态**） | 「耗时短 + 零日志 ⇒ 未执行」**单独成判据会成片误伤** —— 87 次调用里 14 次 <133ms、分属 **7 个 action**，板上 72ms 阈值正好命中正常档 ⇒ 必须三态、`true` 永不可达 | 并入计划 01 · `triage/25-…`（175 行） |
| **[30]** | **已被现有计划覆盖** | 板上建议改默认值 `false`；裁定**计划 04 对**，但它三条「破坏性」**只有 1 条承重**（`false` 分支返回体无 `state` 键） | 并入计划 04 · `triage/30-…`（305 行） |
| **[31]** | 🔴 值得修（**单立·必须先于计划 02**） | 重试**无条件重发同一 POST**，且慢档 10s < 游戏侧 20s 预算 ⇒ **重放是结构必然**；爆炸半径覆盖 **12 个写端点** | ✅ 计划 `plans/2026-10-01-31-csharp-silent-replay-guard.md` · `triage/31-…`（298 行） |
| **[27]** | 🟡 值得修 | 计划 11 的声明行正则**对方法 0 命中**（模板只覆盖属性 `=>` / 字段 `;`，方法以 `)` 收尾）⇒ 加 `\(` 分支 | 并入计划 11 · `triage/27-…` |
| **[28]** | 🟠 值得修（**单立**） | `Harmony.GetPatchedMethods()` 照抄**返回 0**（owner 作用域实例方法）；正解是**静态 `GetAllPatchedMethods()`** —— 本仓库 `HotReloadValidator.cs:383-387` 早就写明这个坑 | 计划派单中 · `triage/28-…` |
| **[29]** | 🔴 值得修（**与计划 10 不是同一缺陷**） | `search_types` 返回**两个 `fullName` 同为 `Verse.ThingOwner` 的类型**，工具挑了泛型那个就报 `hasMore:false` ⇒ **整块 42 个成员根本没被枚举**（不是"按可见性过滤漏了"） | 不派 PLAN（外部服务）· `triage/29-…` |
| **[33]** | 🟡 值得修（与 [35] 合并） | `RegionType.Region`→`Normal` 是文档缺失；**`Map.regionManager` 是板上事实错误**（真字段 `Map.regionGrid`，全程序集 0 命中）；CS0121 根因**未定**（"重复程序集引用"是假设、未实测） | 合并计划派单中 · `triage/33-…`（222 行） |
| **[34]** | 🟠 值得修（≈80% 已覆盖） | **与计划 02 一致**（宿主是 `void`、CS0127 已闭合）；但计划 02 曾**漏了 `index.js:4536-4537` 的 `r.guidance`** —— 对 `'CS0127'` 必为真，会把误导指向"没有 using 引用" ⇒ **计划 02 已补任务 7** | 并入计划 02 · `triage/34-…`（207 行） |
| **[35]** | 🟠 值得修（与 [33] 合并） | 编译错只给行列号不给"你要哪一支"；⛔ **"随错自动附 decompile 全表"在本仓库做不出来**（属另一 MCP 服务器，`MCP/` grep 0 命中） | 合并计划派单中 · `triage/35-…`（216 行） |
| **[32]** | 🟠 值得修（**板上归因写反了**） | **30s 是本仓库自己的客户端请求预算，不是 GABP 内建** —— `MCP/longTask/timeoutPolicy.js:18/:21-25/:28` **三张白名单全未登记该工具** ⇒ `_opts=null` ⇒ 落回 `gabpClient.js:67` 的 `DEFAULT_REQUEST_TIMEOUT_MS=30000`。反编译 4 个运行时程序集证否 RBS 假说 | 计划派单中（**与 [24] 同一份**）· `triage/32-…`（232 行） |
| **[24]** | 🟠 | `rimworld.select_pawn` 经 GABP 两次都 30s 超时 ⇒ 与 [32] **逐字同源**（同一个 `30000` 常量） | ✅ 计划 `plans/2026-10-02-32-gabp-per-call-timeout-derivation.md`（**刻意不进豁免集，保留自愈**） |
| **[36]** | 🟠 值得修（**板上前缀 🔴 建议下调**） | **板上三条归因皆错**：「一律 30s」错（`execute_debug_action` 早已登记、拿 60s、**照样超时**）；「整个 DebugAction 族全灭」错（**分界是 GABP 通道 vs 非 GABP**，`search_debug_actions` 根本不经 GABP）；「与 [32] 同源」错 ⇒ **不并入计划 32** | ✅ 计划 `plans/2026-10-03-36-gabp-wedge-distinguishable-failure.md`（1248 行 / 4 任务 / 13 测试）· `triage/36-…`（328 行） |
| **[37]** | 🔴 值得修（**与 [38] 合并单立**） | `search_symbols` 不带 `contextAlias` 搜的是"当前上下文"，而它是**跨会话共享的全局可变状态**（实测停在 `gab`=`Lib.GAB.dll`，94 类型）⇒ 零命中与"符号不存在"**逐字同形** | 计划派单中 · `triage/37-…`（534 行，**含 38 条零命中审计**） |
| **[38]** | 🟡 **板上主命题应撤回** | 解析器**确实先试完整名**（2~3 个点的 `Verse.AI.*` 全部可用）；本条的复现**本身就是 [37] 的受害者**——唯一变量是当前上下文 | 并入 [37] 那份计划 · `triage/38-…`（361 行） |
| **[39]** | 🟠 值得修（**并入 [14]，不新建计划**） | 同服两个读源码工具行号口径不一致：`read_csharp_symbol` **已带** `// File: … (Lines N-M)`（方法与整类都带 ⇒ 通用格式化器），`read_file` **一个行号都没有** | ✅ 并入 `plans/2026-10-01-14-…` 的 M1–M5 · `triage/39-…`（267 行） |

### [39] 详细批注 —— 🚨 **它证伪了计划 14 里一条已定稿的断言**

**本条的真正价值不是"注脚"，而是**：`plans/2026-10-01-14-…` 的 **`### 4.2 替代路径：没有`** —— **今天已不成立**。
⇒ 该文件 `:202` 需改写；**修复成本的性质变了**：从「外部仓库的 TODO」变成「**复用外部仓库已有的那行**」。

**⚠️ 但没有降到"本仓库能修"**：①**格式化模板 ≠ 行号计算**（前者「符号→区间」，后者「窗口→区间」，还要处理 `start_line+1` 换算与**空行吞行**右移）；② 本仓库仍 **0 行可改**。
⇒ 本条上面写「能力已在仓内」**说过头了**。

**🚨🚨 本条下面那个"建议"的顺序陷阱（最危险的一条）**：
> **照建议直接加 `// File: <path> (Lines N-M)` 头、而不先修空行吞行 —— 新头会稳定整体错 1 行**
> （传 `start_line=162` 实际返回 164 起的行，头却印 163-165）。
> **比现在没有行号更危险，因为它看起来权威** ⇒ 把**可检出的假阴性升级成不可检出的假成功**。

**正确顺序**：① **先修空行吞行** → ② **头从「实际返回的切片」反算**（⚠️ 不是从 `start_line` 正算）→ ③ 再做 **opt-in `include_line_numbers`**（沿用同服 `get_source_slice` 已有参数名，别另造）。

**本条「③影响」那段的两处论证要改**：
① 「没有已知锚点时任何判断都是猜」**过强** —— **A/B 对拍**（`[start_line=N]` 与 `[N+1]` 各调一次，返回逐字相同即判右移）**不需要任何外部锚点**，代价只是多 1 次调用。准确表述是「**单次调用无法自校验**」。
② 真正该加重的是**同服三套行号契约不一致**：`search_source` 1-based 无头 / `read_file` **0-indexed**无头且吞行 / `read_csharp_symbol` 1-based 有头 ⇒ 这是**设计缺陷**。
**升级触发**：任何**未经 `search_source` 交叉核对**的行号，该**单个产物**按 🔴 对待。

**阻塞状态**：`docs\mcp-source-lookup-conventions.md` **仍不存在** ⇒ M1–M5 全部被**计划 11 任务 1** 阻塞，**现阶段只能改计划、还不能落文档**。

### [37][38] 详细批注 —— 💡 **一次对我自己 35 份报告的审计**

**审计结论**：38 条零命中结论中 **12 条走 decompilerserver、26 条走文件系统 grep**（后者**不受影响**）；12 条已**逐条显式 alias 重跑**，**因污染失效 0 条**。
⇒ **我这一路的错误不是 [37] 造成的**（执笔者恰好都写了 alias，且 [37] 今日才上板）。它是**机制层的根**，不是**已发生错误的解释**。
**受污染风险等级：中**（三个关键 MVID 本轮均未变 ⇒ 程序集没被换；但"先 status 查一次、之后依赖当前上下文"在跨会话前提下**有时效**）。

**🔴 审计唯一一条改变了既有报告内容的发现**：报告 22 声称 `main thread` 在 `RimBridgeServer.dll` 零命中，**实为 7 命中**（那 7 条是 RBS 自有等待日志，不是 Unity 报错串）。
**结论仍成立**（`realtimeSinceStartup` 与报错全文两个零命中均复核为真），**但那一行证据是假的**。
⇒ **教训**：零命中被随手写进证据表、**没人回头验**。**主代理已执行订正**（`triage/22-…` 内已加订正块，原文保留）。

**💡 通用形态（比 [37] 本身更值钱）：凡用「零命中」下结论，三件事必须同时成立**
1. **指定并记录 alias / MVID**
2. **阳性对照跑在同一个 alias 上** —— ⚠️ **主代理自己犯过**：对照 `tools/call` 跑在 `Gabp.Runtime.dll`，零命中跑在 RBS 三个程序集，**跨程序集对照无效**（已用同程序集 `ticks`/`timeoutMs`@rbs 各 17 命中救回结论）
3. **结论写成"在该 alias 下零命中"，而非"不存在"**

⇒ `COMMON.md §5.1` 要求零命中附**检索式**，**这里要附的是检索范围**。

**✓ 一条阴性结果也值得记**：`status` 的 `stringLiteralIndexReady:false` **不是**第二条假阴性机制（实测在 `gab` 上 `search_string_literals("GABP")` 返回 6 条）——**看起来危险的东西不一定是危险，但必须测过才能排除。**

**⚠️ 已回灌计划 10 的真实指令缺陷**：它把 `get_members_of_type` 标为「写码时首选」而**全文从未提 `contextAlias`** ⇒ 读者照做就会撞上那个误导报错。**这不是 [38] 的锅。**

**✅ 计划 33-35 复核后确认无需加护栏** —— 它 `:7` 已写死"零 MCP 工具调用"，§0 的 F1–F16 事实表**每行都内嵌 `contextAlias="rw16"`**，9 条已复验逐字相符，**可照原样执行**。

### [36] 详细批注 —— 🚨 **真内核比板上严重，方向相反**

**🔴 超时根本不取消动作。** 反编译 `RimBridgeMainThread.InvokeAsync<T>`(`06000E79:M`) **无超时地** `await workItem.Completion`；`Pump()`(`06000E76:M`) 停摆即全通道排队；`MCP\gabpClient.js:566-569` **只删本地 pending + reject，不发任何取消**
⇒ **被判超时后工作项仍留在队列，主线程一恢复就补执行动作。**
⇒ 本条上面写「超时无法与正在执行区分」**方向对但低估了**，应升级为「**报失败却会晚点做**」
⇒ **这与 [01] 的「报成功却没做」方向相反，所以不建议与 [01]/[25] 合并。**

**三条决定性反证**（逐条否掉板上的归因）：
1. `execute_debug_action` 早已登记在 `timeoutPolicy.js:18` `SLOW_BARE_NAMES` ⇒ 拿 **60000ms** ⇒ 实测 60024/60010/60027/60041ms **照样超时**
2. `get_debug_action` 在 `MCP\logs\mcp-20261001-222618.log:862` 是 **16ms success**
3. 同一窗口里不相干的 `rimworld.select_pawn`（`:198`，30038ms）**也超时** —— 逐工具的白名单缺陷打不中「选中小人」

**⚠️ 本条上面「同一时刻 `pause_game` 正常」在日志里查无对应记录** —— `pause_game` **也是 GABP 镜像**（`index.js:3027`，无原生），最后一次在窗口**开始前**（22:52:08，58ms success），下一次在窗口**结束后**。它与死掉的工具走**同一条通道**，不构成对照。

**真实分界是 GABP 通道 vs 非 GABP 通道**，不是 DebugAction vs 非 DebugAction：楔死窗口内 `rimbridge.list_operations`（**非 DebugAction**，`Immediate` 分支）同样 30045ms 超时；而 `start_quick_test`/`get_game_status`/`execute_csharp_code`（非 GABP）全部正常。全日志 `execute_debug_action` 是 **146 成功 / 5 失败**。

**板上两条建议已被证伪，别实现**：① dev-mode / `Dialog_DebugSettings` 探测**对准了不存在的门禁** —— `EnsureNodeGraph`(`060010BD:M`) **无条件调** `Dialog_Debug.TrySetupNodeGraph()`，不查任何前置；② **`forceReconnect()` 是错药** —— `:1500` 强制重连后 `:1503` 仍超时。**明确不建议**把三个工具加白名单（翻倍无效、且会拖慢 `forceReconnect` 自愈阈值）。

**`-32001` 与 `GABP_ERROR` 是两回事**：`-32001` + `Request timed out` 全仓库**唯一出处**是 SDK `protocol.js:713`（**客户端截止线**），**RBS 里检索 `-32001` 字面量 0 命中**。

### [27] 命中行不带声明类型 —— 「同名方法跨类型」在打补丁任务上是现成的挂错源

**① 症状**：`search_source(query="TryRecoverFromUnwalkablePosition")` 回 4 行，形如
`./Source/Verse/AI/Pawn_PathFollower.cs 529` —— **只有路径 + 行号，没有"这是哪个类型的方法"**。

**② 复现**：任意一个 vanilla 方法名，只要它同时挂在两个类型上就会这样。实测这一例：

| 命中 | 实际是 |
|---|---|
| `Source/Verse/AI/Pawn_PathFollower.cs:529` | `Verse.AI.Pawn_PathFollower` 的 **public** `bool TryRecoverFromUnwalkablePosition(bool error = true)` |
| `Source/Verse/AI/Pawn_PathFollower.cs:164` | 同类型的**调用点**（不是声明） |
| `Source/Verse/GenSpawn.cs:188` | 同类型的**调用点**（不是声明） |
| `Source/RimWorld/Planet/Caravan_PathFollower.cs:238` | **另一个类型**的 `private bool TryRecoverFromUnwalkablePosition()`（**无参**） |

⇒ 光看命中行，**声明 / 调用点 / 另一个类型的同名方法**三者长得一模一样。分 4 行里只有 2 行是声明，
其中只有 1 行是你要挂的那个。

**③ 影响**：🟡 低但**正好打在高频任务上** —— 本类项目（Harmony 补丁）每天要回答
"这个方法名在 vanilla 里有几个、分别挂在谁身上、签名是什么"，而这个答案必须靠逐个 `read_file` 打开才能拼出来。
一旦凭命中行猜类型去写 `[HarmonyPatch(typeof(X), nameof(X.M), …)]`，后果是 **Ambiguous match 或挂错类型**，
而这两种都要等运行时才炸。**与 [13]/[21] 叠加时更难受**：`read_file` 还要先猜对目录
（`Pawn_PathFollower.cs` 实际在 `Source/Verse/AI/` 不在 `Source/RimWorld/`）⇒ 一步里两个坑。

**④ 建议**：`search_source` 的命中行加个 `⟨DeclaringType⟩` 前缀（`Source/Verse/AI/Pawn_PathFollower.cs:529 [Pawn_PathFollower]`），
零成本、直接解决"声明 vs 调用点"和"跨类型同名"两件事。或者加一个只回结构化字段的小工具
（`{declaringType, name, params, access}`），让"这名字在 vanilla 里有几个、各自什么签名"变成一次查询。

**⑤ 状态**：待绕过（逐个 `read_file` 打开；靠 `typeof()` 限定 + 写全参数表把风险压到运行时可见）。

---

### [26] 详细批注 —— 🚩 **三处订正要转告给 TEXT20 侧**

**判定属于本板范围**（`COMMON.md:669` 触发条件「工具报错而实际不是那样」）：守卫报「账本多行/已失效」指向**数据**，实际是「识别依据失效」指向**守卫自己** —— **报错把矛头指错了对象**，与 [01]/[25] 同族。有先例：[06] 同为 TEXT20 侧 DebugAction 约定，本流水线已为它写过计划。

**🔴 实测边界**：`DebugActionStageTestMap.cs:307` 的字符类逐码位 = `①`U+2460 / `⑳`U+2473 / `㉑`U+3251 / `㉟`**U+325F**；
`IsMatch` 实测 U+325F=**True**、**U+32B0=False** ⇒ **㊰~㊿ 整段 15 个码位全盲**。
**这些码位已经在用**（非潜在缺陷）：`DegeneratePatherPathTests.cs:2588/:2638/:2649/:2685/:2739/:2744/:2764` = ㊱㊲㊳㊴；用原正则跑该文件只得 **30** 个 testCases，**㊱~㊴ 一个不在内**。
**两个方向的失败模式都存在**：现态 = **假阴性**（㊱~㊴ 真吃共享池 pawn、真无账本行，`RequiredFlyerCount()`=27 而实需 31，而守卫对它本该抓的故障恰好瞎）；按 W5 加行那一刻起 = **必发假阳性** `[无结论] 账本多行/已失效`（`:367-370`），**不是"可能"**。

**🚩 订正 ①（最重要）：W5 提议的字符类 `[…㊱-㊿]` 下界差一格。** 实测对 **U+32B0（㊰ = 编号 36）仍为 False**，而 P13 留了 `㉟~㊰` 空档 ⇒ **谁分出 36 号就再失明一次**。
⇒ 建议 **`[①-⑳㉑-㉟㊰-㊿]`**（20+15+15 = 1..50 零空隙）。
⇒ **扩字符类不引入新误判**：这 15 个码位全已分配为 CIRCLED NUMBER 36..50、在 U+3200–U+32FF 内**连续**，不存在「匹配到但不是编号」的码位。

**🚩 订正 ②：本条正文两处引「`COMMON.md §2b.4 ②` 的结构性失明」章节号错了。** 该词全文仅 1 处命中，在 `COMMON.md:237` 的 **§2b.4 引言**；小节 ②（`:276`）讲的是 `[零覆盖]` 否决③，与本条无关。

**🚩 订正 ③：`W5-ledger.md:77` 并列的「ⓐ-ⓕ」与本守卫无关。** `TargetDriftTests.cs:68` 自带 `private static MarkZeroCoverage`（`:16` 注明原因），而本守卫**只读** `DegeneratePatherPathTests.cs` 一个文件（`DebugActionStageTestMap.cs:280-281`）⇒ ⓒⓓⓔⓕ 根本不参与对账。

**根因方案（本条建议 2）未被覆盖**：`W5-P14-report.md:148` 明写「改动只有上面这一行正则，其余逻辑不用动」，把它**显式排除**了。
⇒ 建议挂 **TEXT20 自己的 `W5-report.md §6` Minor 清单**，**不要进本流水线的 `plans/`**。

**严重度 🔴 成立且不降反升**（㊱~㊺ 共 10 条全在盲区，P14 自标 Critical）。但 **`[零覆盖]` 是否真发生属运行时事实，需冷重启跑游戏核实** —— 本流水线未核实，已在报告里标出。

**📌 计划阶段又拦下一处「看着对、其实不生效」的修法**：裁决报告建议把未知参数守卫放在 `MCP\index.js:4262`，
但 `rimworld.play_for` **在 4249 就被提前接管**（该块自身注释 `:4246-4248` 写明「必须排在 `isGabpMirrorName` 块之前」）
⇒ **守卫放在 4262 永远看不到它**，**恰恰漏掉裁决自己点名的最坏案例 `play_for{durationSeconds:3}`**。已改到 **4244**。
（另拦下 3 处会误伤的本地键、以及 `attachUsageOnFailure` 触发条件被说窄了 —— `:4012-4018` 是「`isError !== true` 时才查 `success === false`」，实际 `isError===true` **或** `success===false` 任一即触发。）
**⇒ 这是本轮第 6 次「上游给的修法要复核才敢用」** —— 前 5 次分别是 [01] 的 `logCount===0`、[05] 的清空时机、[07] 的超时方向、[13] 的"只能文档化"、[19]/[21] 的 COMMON.md 检索范围。

### [22] 详细批注

**🔴 板上三条断言被证伪**：
1. **真实参数名是 `ticks`**（反编译运行时 `RimBridgeServer.dll` → `RimBridgeServer.RimBridgeTools.StepGameTicks`，签名 `int ticks = 1, int timeoutMs = 10000, int pollIntervalMs = 10, bool pauseFirst = true, bool playSound = false`）。
   板上传的 `tickCount` **不存在** → 走 C# 默认参数 → `requestedTicks:1`。**这是 C# 可选参数的正常语义，RBS 没坏、调用方写错了。**
2. **「schema 是 `params:{}` 空」双重证伪**（反编译 `libgab` + 实机 `mcp_help{tool:"rimworld.step_game_ticks"}` 返回完整 5 参数 schema，与反编译逐字一致）。
   ⚠️ **`params` 这个字段名本身就不存在** —— 本仓库 `mcp_help` 返回的是 `inputSchema`。**不要去"把 `params` 填上"。**
3. **日志实证多 tick 一直做得到**：`MCP\logs\mcp-20260928-173737.log` 的 `:388` `ticks:"200"`→4656ms、`:459` `ticks:"400"`→7337ms，全部 success。
   （对照 `mcp-20261001-093829.log:1305` 的 `tickCount:"180"` → 仅 51ms，**只走了 1 tick 的量级**。）
   板上说的 `play_for{durationSeconds:3}` 参数名也不对 —— RBS 真实签名是 `PlayFor(Int32 durationMs, String speed, ...)`。

**⚠️ 本条上面「≈6 分钟/轮冷重启、4 轮里 1 轮失败」的影响叙述**：
> **🔴 更正（2026-10-02）：上一行说「查无此物」是错的 —— 它存在，在 TEXT20 项目下**
> ```
> Mods\TEXT20\source\PathPlan\FlightAStar\Demo\DebugActionStageTestMap.cs
> ```
> （同款错误：这已是本轮**第 6 次**「把局部检索的结论当全局结论」。triage 当时只搜了 UESDdebuger 仓库，**而该文件在隔壁项目**——与 `COMMON.md` 同款失误。）
> ⇒ **所以「影响叙述无法核实」这个判断不成立**，`[26]`（同一铺场器的交叉校验守卫失明）证明它**正在被积极使用**。
> ⇒ **⚠️ `[22]` 的严重度 🟠→🟡 这个降级需要重新审视** —— 它部分建立在上面那条错误检索上。**降级是否仍成立，要按 `TEXT20` 的真实路径重核后再定。**
> ~~且即使真卡住，`step_game_ticks{ticks:180}` 也是 7 秒内可得的补救，不是 6 分钟。~~ —— 这条**仍成立**（参数名 `ticks` 已被反编译证实），**但它不足以单独支撑降级**：工具没撒谎 ≠ 影响小，**"没有可用路径"与"路径存在但没人知道参数名"是两件事**。

**✅ 真正剩下的缺陷只有一条，且 100% 在本仓库**：`PLAY_FOR_MAIN_THREAD` 的中文建议文案（`MCP\index.js:4254-4255`，errorCode 与文案都是本项目自己拼的、RBS 里 0 命中）把调用方指向替代方案却**不点名参数**。

**🚨 顺带查出本条范围内一个更值得修的隐患（本仓库的）**：RBS 下发的 schema 明确写 `"additionalProperties": false`，
但 `agg_call_tool` 的 `args` 是 **`additionalProperties: true`**（`MCP\index.js:2808`），**没有任何一层执行这个契约**
⇒ **未知参数静默丢弃、默认值顶替、调用照常 `success:true`**。`play_for{durationSeconds:3}` 就会用 RBS 默认 `durationMs` 跑一次。
**这比 `tickCount` 那次更危险** —— `tickCount` 至少能从 `requestedTicks:1` 看出来，**`play_for` 那次连线索都没有**。
措辞对齐**计划 08 §2.3-2 的既有契约**（「被覆盖的参数必须点名回显……**不回显 = 假成功**」）。

**协调结论**：**不并入计划 07**（07 的场景是 GABP **离线**、且明确「不缓存镜像 schema」，本条是**在线**且 schema 本就完整，正交）；
但**共用其任务 2 的「指路单一事实源」抽象** —— 本条的 `index.js:4255` 是**同一反模式的第三份副本**（错误文案里嵌指路）。
与计划 08、03 无关。**建议单立**一份 2 任务的小计划。

---

## [34] `execute_csharp_code` 的宿主方法**是 `void`** —— 写 `return "…";` 直接编译失败 🟠

**①症状**（2026-10-02，TEXT20 W6 · 收尾实测代理）

- 项目里流传的写法是「`UnityEngine.Debug.Log("…"); return "…";`」（前言里就是这么写的），照抄立刻编译失败：

  ```
  (20,1): error CS0127: `InteractiveHost': A return keyword must not be followed by any
          expression when method returns void
  ```

  ⇒ 用户代码被塞进一个 **`void` 方法**里，`return <表达式>;` 不合法；只有 `return;` 合法。
  而工具描述 / `guidance` 里给的例子与返回体 `data.result` 又**暗示**「有返回值」。
- 顺带：**`data.output` 会把 `UnityEngine.Debug.Log` 的全文原样回显一遍**，
  再跟 `result`（或 `Invoked REPL, result:` 那行）**重复一次** ⇒ 一段 6 行探针在上下文里出现两遍。
  这与 [20] 同源，但这里是**强制**的（`void` ⇒ 只能靠 `Debug.Log` 出结果）。

**②怎么复现**：`agg_call_tool{tool:"rimworld.execute_csharp_code", args:{code:"UnityEngine.Debug.Log(\"x\"); return \"x\";"}}`

**③影响**：🟠 中。照文档/前言写就**必然编译失败**，浪费一轮往返；
被迫改成 `void` 之后又只能从 `data.output` 读结果 ⇒ 与 [02]「`Verse.Log.*` 不落 `Player.log`」叠加时，
调试信息只能留在这次调用的返回值里，**无法事后从 `Player.log` 自证**。

**④建议**：要么把宿主改成返回 `string`（与 `data.result` 字段对齐），要么在 `guidance` 里
明写「宿主是 `void`，不要写 `return 表达式`；结果请用 `UnityEngine.Debug.Log` 并从 `data.output` 读」。
另外 `data.output` 与 `result` 的重复回显可以只留一份。

**⑤状态**：待修

---

## [33] `add_using_directive("Verse")` 之后 `IntVec3.GetRegion(map)` 变成 **CS0121 二义**，`RegionType` 也没有 `Region` 成员 🟡

**①症状**（2026-10-02，TEXT20 W6 · P18 实测代理）

- 按前言加 `using Verse;` 之后，`w.Position.GetRegion(map)` 报
  `error CS0121: The call is ambiguous between 'Verse.GridsUtility.GetRegion(this Verse.IntVec3, Verse.Map, Verse.RegionType)' and 'Verse.GridsUtility.GetRegion(this Verse.IntVec3, Verse.Map, Verse.RegionType)'`
  —— **两个候选签名逐字相同**（看着像程序集被加载了两次）。
- 改成显式三参 `GetRegion(map, RegionType.Region)` 又报
  `CS0117: 'Verse.RegionType' does not contain a definition for 'Region'`。
  实际枚举值是 `None, ImpassableFreeAirExchange, Set_Impassable, Normal, Portal, Fence, Set_Passable, Set_All`
  ⇒ **要的是 `RegionType.Normal`**。
- 再退一步用 `map.regionManager.RegionAt(...)` ⇒ `CS1061: 'Verse.Map' does not contain a definition for 'regionManager'`（1.6 里是 **internal 字段**，不是 property，`GetProperty` 也返 `null`）。

**②复现**：`add_using_directive{namespace:"Verse"}` → 任意含 `IntVec3.GetRegion` 的 C# 片段。

**③影响**（🟡 中）：直接把「查 Region / District / `TouchesMapEdge`」这条**最高频**的活体探针写法堵死一次，
容易让人误判成「这个 API 不存在」。**可判定的绕过**（本轮实测走通）：
```csharp
var asm  = typeof(Verse.IntVec3).Assembly;                      // Assembly-CSharp
var gu   = asm.GetType("Verse.GridsUtility");
var gm   = gu.GetMethod("GetRegion", new System.Type[]{ typeof(Verse.IntVec3), typeof(Verse.Map), typeof(Verse.RegionType) });
var reg  = (Verse.Region)gm.Invoke(null, new object[]{ cell, map, Verse.RegionType.Normal });
```

**④建议**：① `add_using_directive` 加 `Verse` 前后各试一次；② 或者文档里直接写明
`RegionType.Normal` 是「普通地面 Region」那个值（`Region` 不是成员名）；
③ 顺带：`Map.regionManager` 是 internal，`Map` 上**没有**公开的 `GetRegion`。

**⑤状态**：已绕过

---

## [32] `rimworld.start_debug_game_ready` 的 `timeoutMs` **不生效**，GABP 内建 30s 超时先砍掉 🟠

**①症状**（2026-10-02，TEXT20 W6 · P18 实测代理，与 [04]/[24] 同源但是**另一个工具**）

- `agg_call_tool{tool:"rimworld.start_debug_game_ready", args:{timeoutMs:300000, readiness:"playable", pauseIfNeeded:true}}`
  回 `GABP_ERROR: GABP 调用 rimworld/start_debug_game_ready 失败（code=-32000）: GABP 请求 'tools/call' 超时（30000ms）`
  —— **传了 300000，实际被 30000 砍掉**。
- 之后 **60 秒 `get_game_status` 轮询到 `stage=IN_GAME`**，游戏**确实造好了新殖民地**（`Map-0-PlayerHome`，41 pawn）。

**②复现**：从主菜单调 `start_debug_game_ready`（造新殖民地）即可；本轮 1/1 命中。

**③影响**（🟠 中）：与 [04] 同源，但更隐蔽 —— `timeoutMs` 这个参数**看起来可用**（schema 里有），
实际上调大它毫无作用 ⇒ 调用方会以为是「等了 5 分钟还是失败」，实际只等了 30 秒。
**它骗到的是**「造新图要很久所以超时很正常」这个自我安慰。

**④建议**：要么让 `timeoutMs` 真能穿透 GABP，要么在 schema 里把它标成「仅游戏侧生效、不影响 RPC 超时」。

**⑤状态**：已绕过（改成 `timeoutMs` 照传 + `get_game_status` 轮询）

---

## [31] `execute_csharp_code` 会被**静默重放**：副作用型代码拿到的返回值是「第二遍」的结果 🔴

**①症状**（2026-10-02，TEXT20 W6 · P18 实测代理，本波最险的一条）

我发了一段「扫描并销毁 46×46 区域内全部非 pawn 物」的 C#（2099 个天然岩石），**MCP 返回体里写的是**：
```
output: [Unity] [DOORFIX] destroying 0 non-pawn things in 0..45 x 0..45
        [Unity] [DOORFIX] destroyed=0 errors=0
```
看起来像「什么都没干 / 已经是空的了」。

**但 `Player.log` 里明明白白是**：
```
:6924  [UnityExplorer] [Unity] [DOORFIX] destroying 2099 non-pawn things in 0..45 x 0..45
:6960  [UnityExplorer] [Unity] [DOORFIX] destroyed=2099 errors=0
:7076  [UnityExplorer] [Unity] [UEHttp] Failed to send response: Unable to write data to the transport connection: 远程主机强迫关闭了一个现有的连接。
```
⇒ **同一段代码执行了两次**：第一遍**成功改了游戏状态**，但写响应时 UE 的 HTTP 连接被对端重置 ⇒ MCP 重试 ⇒
第二遍才把响应交回，而第二遍看到的是**第一遍做完之后的世界**。

**②复现**：发一段「有副作用、且第二次跑会输出不同数字」的 `execute_csharp_code`，同时抓 `Player.log` 对照。

**③影响**（🔴 高，**这是会骗到人的方向**）：
返回体里的数字**是第二遍的**，不是这一次真实执行的。副作用型代码一旦「看起来没生效」，
调用方很可能**再发一次**（幂等地再改一遍世界）或反过来**判定为失败**。
本波我差点据此得出「山已经没了、我什么都没删」的相反结论；是 `Player.log` 把它纠正过来的。

**④建议**：
① `UEHttpHandler` 写响应失败时**不要**让上层无条件重试带副作用的调用，或在重试前先回一句「上次可能已执行」；
② 至少在 MCP 侧**标注本次调用被执行了 N 次**（哪怕只是 `attempts: 2`）；
③ 文档里写死一条纪律：**带副作用的 `execute_csharp_code`，验收必须回 `Player.log` 看第一遍的输出，不能只看工具返回值。**

**⑤状态**：待修

---

## [30] `start_game` 的 `waitForNotification` 默认值会把「游戏已成功启动」伪装成「调用失败」🟠

**①症状**（2026-10-02，TEXT20 W6 登记代理 · 裁定 B 反事实实测，连做 6 轮冷重启，每轮都撞）

- `mcp__rimworld-debug__start_game{timeout:240000, waitForNotification:true}` **稳定超时**：
  `MCP error -32001: Request timed out`（本轮 6 次里命中 6 次，约每次浪费 3~4 分钟）。
- 紧接着 `get_game_status` 显示 **游戏已经起来了**（`stage` 从 `GAME_STOPPED` 走到 `IN_GAME`，
  `game_pid` 也确实变了）⇒ **超时与失败根本不是一回事**（这是 [04] 的一个具体实例）。
- 更糟的是 `start_quick_test{timeout:30000}` 也超时，但随后 `get_game_status` 又变成 `IN_GAME` ——
  ⇒ 调用方若把超时读成「没跑」，会得出「游戏没起来」，然后白等第二轮。

**②复现**：`stop_game{force:true}` → `start_game{waitForNotification:true}` ⇒ 必超时；
`get_game_status` ⇒ 游戏在跑。

**③影响 / 严重度**：🟠 中。**不产生错误结论，但每一轮白等 3~4 分钟**；
在「造 fixture → 构建 → 冷重启 → 实测」这种要跑好几轮的循环里，是实打实的时间损耗；
更要紧的是它**把成功伪装成失败**，容易诱发调用方做多余的补救（重复启动、写「启动失败」的假记录）。

**④建议 / 绕过（我实测有效）**：

```
start_game{ waitForNotification: false }   ⇒  立刻返回，data 里直接带 pid
```

6 轮冷重启**全部 0 超时**。建议维护者把 `waitForNotification` 的默认值改成 `false`
（它的语义本来就是「等主菜单通知」，不是「等启动完成」），
或至少在超时分支里回一句「游戏可能已启动，请用 `get_game_status` 复核」而不是抛错。
另外 `start_quick_test` 的 `timeout` 实测**要 ≥ 240000**（世界生成常超过 60s），
给 30s 会稳定超时但游戏仍在后台继续加载。

**⑤状态**：`已绕过`（调用方侧绕开；建议维护者把默认值改掉）

---

## [29] `get_members_of_type` 会**静默漏掉 public 成员**，`get_overloads` 又不返回被查的那个 ⇒ 直接制造「这个 API 不存在」的假结论 🟠

**①症状**（2026-10-02，TEXT20 W6/P19 实际踩到）

- `get_members_of_type(typeId="Verse.ThingOwner", kind="Method", mode="signatures", limit=40)`
  回 `hasMore:false, totalEstimate:27`、27 条里**一条 `Contains` 都没有**。
  而 vanilla `ReachabilityImmediate.CanReachImmediate` 的 IL（`callvirt 0x06003A5A`）**确实在调**
  `Verse.ThingOwner.Contains`。再查 `get_member_details("…:06003A5A:M")` ⇒
  **`Boolean Contains(Thing item)` / accessibility=Public** —— 它**存在且 public**，只是没被列出来。
- 放大伤害的是 `get_overloads("…:06003A5A:M")`：它只返回**另外两个**同名重载
  （`Contains(ThingDef)` / `Contains(ThingDef, Int32)`），**不包含自己查的那个**。
  ⇒ 返回读起来就是「`Contains` 一共两个重载，都是 `ThingDef`」⇒ 我据此差点写下
  「`ThingOwner` 没有 `Contains(Thing)`」这条**错误结论**并写进补丁注释。

**②复现**：`get_members_of_type(Verse.ThingOwner)` 对比 `get_member_details(0x06003A5A)`
＋ vanilla `ReachabilityImmediate` 的 IL 里 `callvirt Verse.ThingOwner.Contains`。
（附带一条小摩擦：`get_members_of_type(typeId="Verse.GenPath")` 会把输入解析成类型 `Verse` 并报
`type_not_found`，带点的全限定名在这个工具里不被接受，而 `search_symbols` / `get_decompiled_source` 都接受。）

**③影响**：🔴 对**"某个成员存不存在/可不可见"**这类判定来说，这是**一台假阴性机器** ——
而这恰好是本项目 `COMMON.md §5.3` 明令要用「第二条独立路径」复核的那类结论。
工具已经返回了 `hasMore:false, totalEstimate:N`（"这是全部"）却实际不全，
**比老实分页更危险**：`COMMON.md §5.3` 的三种"看起来像不存在"全是假的，本条是**第四种**。

**④建议**：`get_members_of_type` 要么保证返回集**真的完整**，要么在有截断时如实标出
（`truncated:true` + 游标），**绝不**在漏项的前提下报 `hasMore:false`；
`get_overloads` 在结果里显式说明「**不含你查询的那个重载**」，或直接把被查者一并返回。
（另：`typeId` 建议和其它工具对齐，接受点分全限定名。）

**⑤状态**：待绕过（用 `get_member_details` + `get_il` + `search_symbols` 三路交叉复核）

## [28] 装了 Reachability 补丁的模组里，`pawn.CanReach(...)` 已不是原版语义，且**零信号** 🟠

**①症状**

在一个给 `Verse/ReachabilityUtility.CanReach` 打了 Harmony Prefix（`return false` 截断、改写 `__result`）的模组里，
**测试代码里写 `pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)` 拿到的不是原版地面可达性**，
而是模组自己的判定。实测（TEXT20 模组，1.6）：

- 原版 `Verse/ReachabilityUtility.cs:8-16` 的函数体**只有一句**：`pawn.Map.reachability.CanReach(pawn.Position, dest, peMode, TraverseParms.For(...))`
- 而 `TEXT20\source\PathPlan\FlightAStar\ReachabilityPatches.cs:17-32` 有一条形参类型**逐个相同**的 Prefix：
  `__result = FlightReachGate.CanReachFor(pawn, pawn.Position, dest.Cell, peMode, FlightLayer.Ground); return false;`
  ⇒ 对会飞的 pawn，**原版那句地面可达性被整条换成了飞行可达性判定**

⇒ 编译器不报、运行时不报、`Player.log` 不打、反射枚举 API 也**看不出来**（方法签名一个字没变）。

**②怎么复现**：任何模组只要给 `ReachabilityUtility.CanReach` / `Reachability.CanReach` 打了 `return false` 型 Prefix；
调用方在测试代码、调试探针、或任何「只是想问一下」的语境里写 `pawn.CanReach(...)`。

**③影响（这次骗到我的地方）**：我正在给一个 flaky 测试挑目标格，本能地选了 `pawn.CanReach` 当「静态可达性判据」。
如果不追 `ReachabilityPatches.cs`，我会写下注释「用原版可达性判据」，而实际判的是**飞行可达性**——
**注释里的事实是错的，下一个人会把它当事实继承**（本项目 COMMON.md 明确把「注释里写错事实」列为 Important）。
反向也成立：想拿**原版地面**语义做对照时，同一句 `pawn.CanReach` 会静默给飞行答案 ⇒ 对照组失效。
⇒ 这条同时命中 COMMON.md §10 的两条触发：「拿不到本该拿到的证据」与「文档/直觉与实际 API 不符（且会连锁）」。

**④建议**：① 调试/测试语境下，若确实要原版语义，绕开扩展方法直接调
`map.reachability.CanReach(a, b, PathEndMode.OnCell, TraverseParms.For(TraverseMode.ByPawn))`
并**显式注明**「这也可能被 `Patch_Reachability_CanReach_Flight` 接管」；
② 更根本的：`execute_csharp_code` / UE 控制台给一个
「当前已加载程序集里，某个 vanilla 方法**被哪些 Harmony patch 截断/改写**」的查询
（`Harmony.GetPatchedMethods()` 已在 Harmony 2 里可用，只是没暴露成工具）。
否则调用方只能靠 grep 模组源码，且**很容易 grep 不到**（本例的补丁在 `ReachabilityPatches.cs`，与调用点不同文件、不同命名空间）。

**⑤状态**：🟠 待绕过（本次靠「读源码 + 追补丁」解决，未找到通用工具路径）。

---

## [22] 暂停中的游戏**补不了几个真 tick**：`play_for` 恒失败 + `step_game_ticks` 参数名不生效 🟠

**①症状**

```
rimworld.play_for {durationSeconds:3, speed:2}
→ {"success":false,"errorCode":"PLAY_FOR_MAIN_THREAD",
   "message":"rimworld.play_for 当前不可用：RimBridge 端调用 Unity get_realtimeSinceStartup 需在主线程执行（C# 端缺陷）。
              建议改用 step_game_ticks 逐帧推进，或等待 RimBridge mod 修复。"}

rimworld.step_game_ticks {tickCount:180}
→ {"success":true, "requestedTicks":1, "completedTicks":1, "advancedTicks":1, …}   ← ★参数名不对，只走了 1 tick★
```

**②怎么复现**：进图 → `pause_game{pause:true}` → 依次调上面两个工具。两次都是必现，与地图/模组无关。

**③影响（这次骗到我的地方）**：本项目的铺场器 `DebugActionStageTestMap.AdvanceToTakeoff` 只有
**≤8 tick** 的手动推进预算，而原版起飞门控依赖 pawn 自己走到起飞格 ⇒ 地形不理想时约 1/3 的祭品
停在 `WalkingToTakeoff`，**空中样本不够**。这时唯一合理的补救是「解除暂停跑几秒再暂停、重新铺场」，
而**恰恰这条路径没有工具可用** ⇒ 只能反复冷重启换图碰运气（本项目 4 轮里 1 轮失败）。
`step_game_ticks` 的 schema 也没在 `mcp_help` 里写参数名，我照 `tickCount` 传被静默忽略、
返回体里**只有 `requestedTicks:1` 这一个线索**，差点误判成"只能走 1 tick"。

**④建议**：(a) `play_for` 的主线程限制能不能在 RimBridge 侧绕开（或至少在 `mcp_help` 里标注"当前必失败"，
别让调用方浪费一轮）；(b) `step_game_ticks` 的**参数名进 schema**（现在是 `params:{}` 空），
并在返回值里回显"你传的参数名 / 我认的参数名"；(c) 两者都不可用时，**fail-fast 并给出可执行的替代**
（现在 `play_for` 给了替代建议，但那个替代也是坏的 ⇒ 两层都坏时应该直接说"没有可用路径"）。

**⑤状态**：待绕过（本项目当前绕法 = 冷重启换图；绕法成本 ≈ 6 分钟/轮）

---

## [21] `search_source` + `file_pattern` 路径猜错目录 ⇒ 静默 `No results found`，当成「成员不存在」就上当了 🟠

**症状**

```
search_source(query="ErroredPather", file_pattern="Source/Verse/Pawn_JobTracker.cs")
→ No results found. Try adjusting your search query or your file pattern.
```

而真实路径是 `Source/Verse/AI/Pawn_JobTracker.cs`（`Pawn_JobTracker` 在 **`AI/` 子目录**下）。
去掉 `file_pattern` 再搜同一 query，立刻返回 `Source/Verse/AI/Pawn_JobTracker.cs:450`。

**复现**　本轮审 W4-P12，核一个既有 XML doc 里的 `Pawn_JobTracker.cs:450-452`：

```
search_source(query="JobCondition\\.ErroredPather|case JobCondition\\.|StartJob\\(JobDefOf\\.Wait|void Notify_TuckedIntoBed", file_pattern="Source/Verse/Pawn_JobTracker.cs")
→ No results found
search_source(query="JobDefOf\\.Wait", file_pattern="Source/Verse/Pawn_JobTracker.cs")
→ No results found
```

⇒ 两次都 0 命中，且**返回体没有任何提示说「这个路径下没有文件」**，与 [19] 的 `No results found` 一刀切完全同形。若不是顺手把 `file_pattern` 去掉重搜一次，就会把它读成「vanilla 1.6 里没有 Errored 分支」—— 正好撞在本项目的**头号假阴性**（`COMMON.md §5.1`）。

**影响**　🔴 在**核行号 / 核行号漂移**这类任务里是致命的，因为那里的默认失败解释就是「不存在」。
本条与 [13]（命名空间 ≠ 目录）是同一条根因的两种触发姿势：[13] 是 `read_file` 回 `File not found`（**有提示**），本条是 `search_source` 回 `No results found`（**无提示**）—— 同一个错误路径，**静默度差一档**。
本条也是 [19] 的一个具体触发源，建议合并处理。

**建议**（三条，任选其一即可救命）

1. `file_pattern` 命中 0 文件时，返回体里带上「该 pattern 下有 N 个文件 / 0 个文件」；
2. pattern 含 `/` 但 0 命中时，**自动放宽到 basename 匹配**再搜一次，并在结果里标注「已按 basename 重试」；
3. 至少在 0 命中时补一句 `Try dropping file_pattern, or run search_source without it to locate the file`。

**状态**　待绕过（本次靠「去掉 `file_pattern` 重搜」绕过，已在审查报告里如实登记为独立核实项）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**，**并入计划 11**。报告：`docs/issue-triage/triage/21-search-source-file-pattern-wrong-dir.md`
> **现象逐字属实**（10 次只读实测，含本条写的「去掉 `file_pattern` 立刻返回 `:450`」），但**归因要改写**：
> **`file_pattern` 不是精确路径，而是锚在索引根的 glob。** 实测下列三种**全部命中**：
> ```
> file_pattern:"Pawn_JobTracker.cs"        （裸 basename）
> file_pattern:"*JobTracker*"
> file_pattern:"**/Pawn_JobTracker.cs"
> ```
> ⇒ **本条与 [16] 是同一根因的两种写错姿势**（[16] 缺**首级**、本条缺**中间级**），**不是两条规则**。差别只在触发来源：[16] 是被 `read_csharp_symbol` 印错的路径**投毒**，本条是**自己猜错**。
> **📌 附带的更宽反例**：`file_pattern:"Source/Verse/"` —— 一个**完全正确、零拼写错误**的目录前缀 —— 也返回 0。**比本条自述的场景更宽。**
> **✅ 本条不产生任何上游工单**（三条建议里有两条根本不用改上游）：
> 建议 2「含 `/` 但 0 命中时自动放宽到 basename」—— **已是现成功能、零改动**（glob 就是这个）。
> 建议 1「返回该 pattern 下有 N 个文件 / 0 个文件」—— **可用调用侧探针等价实现**：`query:"."` + 同一 `file_pattern`，错路径 → 0 条、换成 basename → `Showing 400/1077 results.`，**一次调用就把「路径写错」与「内容没有」分开**。⇒ 已要求写进规范正文，这是本条最实用的东西。
> **与计划 11 的关系：部分覆盖。** 诊断已被**规矩 3 第 0 步**逐字覆盖（本条用的正是那个绕法）。
> **🔴 但计划 11 现有文案会误导人**：
> ① 规矩 0（`:243-307`）对照表只分两类、理由一律写「缺 `Source/` 前缀」，**从不说「少写中间子目录同样静默零命中」**（全文 grep `glob|basename|子目录|中间目录|少写|漏一` **零命中**）；
> ② **规矩 3 第 0 步的括注「（多半缺 `Source/` 前缀）」与补救句「把正确前缀补回去即可」对本条是 no-op 且反向误导** —— 本条已带 `Source/` 前缀，按该文案去补救**改的地方和错的地方不是一处**，人会以为修好了却没修。已按 P0 回灌。
> **严重度**：索引 🟠 而正文 🔴，**板内自相矛盾**。裁决取 **🟠** —— **自投毒而非被投毒**、规矩 3 第 0 步**一步抓到**、**不可能像 [19] 那样造出假阳性**。建议正文统一 🟠 并注明「**弱于 [16]/[19]**」以免抢占修顺位。
> **⚠️ 本条上面引用的 `COMMON.md §5.1` 不存在**（本轮独立复核：根与 `docs\` 下 `Test-Path` 均 False、递归 Depth4 找 `COMMON*.md` 无结果、Depth5 只有 `node_modules\ip-address\common.*` 与 `System.Data.Common.dll`）。
> ⚠️ **[19] 也引用了同一个不存在的文件** ⇒ **两处引用均缺依据，不能拿"已绕过"给这两条降严重度**。
> （本条不受影响——它的绕法是**当场实测**的，不依赖任何文档。）
> **🔴 更正（2026-10-01 稍后）：上面三行说「不存在」是错的——`COMMON.md` 确实存在**，在 **TEXT20 项目**下（详见索引勘误表末尾的更正）：
> `Mods\TEXT20\.superpowers\sdd\2026-10-01-原版寻路接管-缺口修复-总计划\COMMON.md`（481 行）。
> **§5.1 的标题确实是**「`Player.log` 会轮转 —— 「0 命中」的头号假阴性」——**所以本条引用的那处内容是真的存在的**，只是当时检索范围选错（在 UESDdebuger 仓库内找，而文件在隔壁项目）。
> **不单立的理由**：规范正文由计划 11 逐字整体写入、计划 14 还要插入 `## 4.` 并重编号；且该正文**此刻尚未创建**，**现在是零冲突窗口**。

---

## [20] `execute_csharp_code` 把多行 `Log.Message` 的全文回显进 `data.output`，大探针在上下文里出现两遍 🟠

**症状**　用 `execute_csharp_code` 跑一段 `foreach` 遍历地图 pawn、`Log.Message` 一行一条的探针
（本轮 69 行），返回的 `data.output` 里 **每行都出现两次**：

```
[Unity] [T0PROBE-BASE] mapIndex=0 tick=397 total=69
[T0PROBE-BASE] mapIndex=0 tick=397 total=69
[Unity] [T0PROBE-BASE] idx=0 def=Human id=94 …
[T0PROBE-BASE] idx=0 def=Human id=94 …
…（69 行 ×2）
[UnityExplorer] [UnityExplorer] Invoked REPL (no return value)
Invoked REPL (no return value)
```

而且结尾固定多一句 `Invoked REPL (no return value)`（也是两份）。
（顺带：好消息 —— `Verse.Log.Message` 的正文**确实**落到了 `Player.log`，
`[02]` 说的「不落盘」在 `Log.Message` 上已不成立，落盘的是 `[UnityExplorer] [Unity] …` 那份回显 + 原文两份。）

**复现**　`agg_call_tool{tool:"execute_csharp_code", args:{code:"… foreach … sb.AppendLine(...); Verse.Log.Message(sb.ToString());"}}`

**影响**　想跑「活体热探针」的人（这个项目几乎每轮都要跑）会被迫在上下文里付 **2× 的钱**，
而探针越有用输出越大 ⇒ 正反馈。建议改成：探针只打**汇总 + 可疑条目**，
逐 pawn 的明细 `File.WriteAllText` 到临时文件再用 `grep`/`read` 工具读。

**建议**
1. 探针代码里对 `Log.Message` 的输出做**行数上限**（例如只打前 N 条 + 计数），
   或让 `execute_csharp_code` 支持「只回传 `result` 不回传 `output`」的开关。
2. 至少把结尾那句 `Invoked REPL (no return value)` 与 `[UnityExplorer] ` 前缀去掉（纯噪音）。

**状态**　待绕过（调用方自己限制输出行数）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**，**并入计划 02**。报告：`docs/issue-triage/triage/20-execute-csharp-code-output-duplicate-echo.md`
> **⚠️ 归属层写错了：重复的不是那 69 行探针，而是 UE 控制台自己的固定尾行。**
> 实测 `UE_Data\sinai-dev-UnityExplorer\Logs\UnityExplorer 2026-10-01 200042Z.txt:22-91`：69 行探针 = **1 个** OnLog 事件（`[Unity] ` 只在第 1 行），
> 不是 70 个事件；哨兵 = **2 个**。**本条上面看到的「两份」是 `Player.log` 里的两份**（全文实测 `T0PROBE-BASE` 140 行 = 70×2），
> **不是 `data.output` 的两份**。原文那一份**只进 `Player.log`、不进 `output`**。
> 两份副本的来源：`[Unity] X` = UE 上游回声（`MonoHelper.cs:9,12-13 → ExplorerCore.LogUnity → ExplorerCore.cs:102`）；
> `[UnityExplorer] [Unity] X` = **本项目** `Source\UELoader\ExplorerBootstrap.cs:195-218`（`:204/:207/:210`）用 `Verse.Log` 二次写回。**环路长度恒为 1**（全文无第 3 份）。
> **✅ 计划 02 已经去重了**：`AddUnityExplorerLog`（`:449-460`）丢 `[Unity] `、`AddUnityLog`（`:433-446`）丢 `[UnityExplorer] `/`[UELoader] `/`[UEHttp] `，用例 `:245-251` 已钉死 —— **落地后两份变一份**，且**不会反向制造这个问题**。
> **⚠️ 但有三条真实缺口已回灌计划 02**：
> ① **哨兵是否彻底出 `output`** —— `Invoked REPL (no return value)` 来自 `Source\UE-4.9.0-official\UnityExplorer-4.9.0\src\CSConsole\ConsoleController.cs:185`（**注意不在 `McpRimDebug\`**，该目录 87 个 .cs 里没有这两个文件），共 **6 个变体**（`:183`/`:185`/`:205`/`:203→:211`/`:189`/`:216`）。它是**逐字落盘的硬编码字符串、产生于 MCP 之前**，所以**不需要**计划 01 那种「误读信封字段」的核对。
> ② **`includeOutput:false`** —— **只需改 MCP**（`index.js:2073-2082` 加可选 boolean + `:4539` 前 `delete r.output`），**零游戏 DLL 改动、不用停游戏**；走游戏侧要 `UEHttpServer.cs:391-393` + 重建 DLL + 停游戏，**成本高一个量级，不建议**。
> ③ **🔴 字节上限 —— 计划 02 的真实盲区**：`MaxEntries=512` **只管条数不管字节**。实测单条 70 行消息 = **6997 字符 / 7351 字节（≈7.0 KB）**，两份 ≈ **14.0 KB** ⇒ **条数封顶挡不住「条少但每条巨大」**。（本条写的「≈12KB」不构成次数判据，实测 ≈14.0 KB。）
> **归并锚点**：计划 02 §0.5 + 任务 1 步骤 2 用例 2 改断言 + **新增任务 5**（`includeOutput`）+ **新增任务 6**（字节上限，复用 `logCapture.truncated`）+ 任务 4 文案加两句。
> **⚠️ 有一项无法核实**：`data.output` 的逐字原文（MCP 日志不记载荷，且禁止用 `execute_csharp_code` 复现抢并行会话的游戏）。上述形状由**源码链路 + UE 日志流水 + Player.log 三方交叉**推出，**需在收尾的游戏内验收里用一次真实调用确认**。
> **📌 顺带更正 [02]（见 [02] 段批注）** —— 本条的观测证实 `Verse.Log.Message` 正文**确实落盘**，故 [02] 的核心前提已证伪、严重度 🔴→🟠。

---

## [19] `search_source` 的 `No results found` 一刀切：分不清「模式写错」「成员不存在」「成员存在但是 private」 🟠

**症状**

`search_source(query="public LocalTargetInfo destination", file_pattern="Source/Verse/AI/Pawn_PathFollower.cs")`
回 **`No results found`** —— 和下面两种情况的返回**一模一样**：

- query 写错了（正则没匹配上）
- 这个成员压根不存在
- **这个成员存在，只是可见性不是 `public`** ← 我这次踩的就是这个

**复现**

在核 1.6 的 `Pawn_PathFollower` 时，我按计划文档的说法去证 `destination` / `peMode` 的可见性，
用 `public ...` 开头的模式搜，零命中。当时差点读成"字段不存在"。
换成 `^\s*(public|private|internal).*(LocalTargetInfo destination|PathEndMode peMode)` 一次就搜到：

```
33:	private LocalTargetInfo destination;
35:	private PathEndMode peMode;
```

**影响**

🔴 对"这条到底存不存在 / 是不是 public"的判断**零判别力**，而这恰好是**计划文档最不可信**的那类信息
（本项目就栽过：文档称某个成员存在，源码里零命中；反过来文档写 `public`，实际是 `private`）。
它和板上 [10]、[16] 是同一个家族（"能读不能搜 / 搜不到不等于没有"），但触发路径不同，值得单独记一条。

⚠️ 附带发现：**`search_source` 命中面正常时很好用**，问题只在"零命中"这一侧没有自证手段。

**建议**

零命中时回一句区分性文案，比如「匹配到 N 行但无一命中你的 query」vs「query 本身语法有问题」；
或者对 `file_pattern` 命中的文件先把成员声明行全列出来，让调用方自己比可见性。
（退而求其次的调用侧纪律：**任何零命中都要配一条必然命中的正控模式**，这条已经写进本项目 `COMMON.md`。）

**状态** 待绕过（调用侧已用正控模式兜住；工具未修）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**，**并入计划 11**（`docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md`）。报告：`docs/issue-triage/triage/19-search-source-no-results-indiscriminate.md`
> **🔴 本条比板上写的严重：不是假阴性，是假阳性。**
> 板上那条复现**按默认参数不成立** ——
> `search_source(query="public LocalTargetInfo destination", file_pattern="Source/Verse/AI/Pawn_PathFollower.cs")`
> **默认返回的是命中**，不是 `No results found`：
> ```
> :81  public LocalTargetInfo Destination => destination;    ← 另一个成员（首字母大写）
> ```
> 加 `case_sensitive:true` 才回 `No results found`。
> **真病因：`search_source` 默认大小写不敏感** ⇒ `destination` 同时命中 `:33` 的 **private 字段**和 `:81` 的 **public 属性 `Destination`**。
> **假阳性比假阴性更危险**：假阴性至少让人起疑；**非零命中看起来像"印证了你的猜测"，人会直接采信**。⇒ **用非零结果造出事实相反的结论。**
> **⚠️ 本条上面那段自查也被串味了**：实测「换成 `^\s*(public|private|internal).*(...)` 一次搜到 2 条」——**实际返回 4 条**（多出 `:81` public 属性、`:154` 同名参数方法）。
> **三处板面需修正（原文保留，仅标注）**：① **索引标 🟠 而正文影响段标 🔴**，建议统一按 🔴；
> ② `:44` 那条复现**必须补 `case_sensitive:true` 才成立**；③ `:58-60` 的返回**少贴了 2 条**。
> **❌ 本条状态里的「调用侧已用正控模式兜住」缺依据**：所引的 `COMMON.md` **不存在**（根目录与 `docs\` 下 `Test-Path` 均 False；全仓 80 份 md 检索「正控」仅 4 处命中，**全在本板自身**）。⇒ **不能拿它降严重度。**
> **🔴 更正（2026-10-01 稍后）：「不存在」是错的，`COMMON.md` 在 TEXT20 项目下真实存在**（481 行，详见索引勘误表末尾的更正）。**且「正控」确实写在里面** —— §5.2 有「**② 正控必须选另一个字符串字面量**」与「**正控必须是「方法失效时就会失败」的那个**，否则它不是正控」，还记着一次实战：`用 #Strings 堆的名字当 #US 堆检索的正控，恒成立地骗过了自己`。
> ⇒ **本条状态里的「已用正控模式兜住」很可能是有依据的**，**不该被降级**。但**具体引用的是哪一节仍需按正确路径重核**（原结论基于错误检索，不作数）。
> **✅ 但「存在但 private」是可判的**：`regex:true` + `case_sensitive:true` 搜
> `^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)+destination\s*(=>|;|=)` → 命中含 `:33 private LocalTargetInfo destination;`
> **⚠️ `search_defs` 补不了这一支**：搜 `Pawn_PathFollower` 零命中、对照搜 `Gun_Revolver` 命中 ⇒ **它只索引 Defs XML**。
> **归并锚点**（已回灌计划 11 作者）：P0 在规矩 2 补「`case_sensitive` **默认 false**，存在性判定**必须**显式传 `true`」+ P0 规矩 3 第 1 步的「裸名宽搜」**也要带** `case_sensitive:true`（**否则那条规矩自己就会串味**）→ P1 四态清单补**第五态**（串味**发生在非零命中上**）→ P1 `AI-DEPLOYMENT.md` 入口「配」→「**必须**配」→ P2 加验收。
> **不单立的理由**：规范文档 `docs/mcp-source-lookup-conventions.md` 由计划 11 建、计划 14 再插入 `## 4.` 并重编号；单立会让同一文件三轮插入+重编号+交叉引用同步，漂移风险高于并入成本。且该文件 `Test-Path=False`（**尚未创建**），**现在是零冲突窗口**。

---

## [18] 同一条日志在 `effects.logs` 与 `Player.log` 里条数不同（回显去重不一致） 🟡

**症状**

`execute_debug_action` 返回的 `effects.logs[]` 是**去重后**的（一条消息一次），
但 `Player.log` 里同一条消息**被 UE 控制台回显两遍**（`[UnityExplorer] [Unity] …` + 原行）。
⇒ 同一个「收口日志有几条」的判据，两个地方给出不同的数字。

**复现**

`[FlightAStar][外部销毁收口]` 在 `Player.log` 里 grep（去 `[UnityExplorer]` 前缀后）18 条，
`execute_debug_action` 的 `logCount` 也会是 18 —— 但**不**去前缀去 grep 就会得到 ~36。
`[零覆盖]` 尤其明显：10 行命中其实是 **9 个用例 + 1 条汇总**。

**影响 / 严重度**

🟡 低-中。两次都数对的前提下不影响结论；但**「grep 多少行」被当成「多少条判据」**时会差一倍。
调用方必须自己记住「UE 会回显两遍」。

**建议**

`effects.logs[]` 的元素里已经带 `repeatCount` 字段 —— 建议在返回值里也标出
「本条已去 UE 回显 / Player.log 里会出现 2 行」，或者干脆给一个 `playerLogLineNumbers[]`。

**状态**：待绕过（调用方自己过滤 `^\[UnityExplorer\]`）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**不值得修（代码零改动）**，仅并入计划 01 任务 3 的一句文案。报告：`docs/issue-triage/triage/18-effects-logs-vs-player-log-count-mismatch.md`
> **⚠️ 本条的因果解释已被源码证伪**：「`effects.logs[]` 已经把 UE 回显去重」**不成立** ——
> RBS 的 `ShouldCollapse` 要求 `Message` **逐字相等**，而两条副本是 `[UnityExplorer] [Unity] X…` vs `X…`，**根本不是同一个字符串**，永不折叠。
> **真实成因是本项目自己的** `Source\UELoader\ExplorerBootstrap.cs:195-218`（`:204/:207/:210` 把每条 UE 日志用 `Verse.Log` 写回 `Player.log`）—— **不是 RBS 的锅。**
> **⚠️ 但「两处数字不同」这个前提本身存疑**：按上面的成因，`logCount` 也该接近 Player.log 的行数，而本条说它是 18。**该预测未能实测（需起游戏）。**
> ⇒ **文案别把「两处必然不同」写成定论**，只写「Player.log 里 UE 日志会成对出现，计数时注意」这种不越界的说法。
> **另**：本条提到的 `effects.logs[]` 里那个 `repeatCount` 字段**确实存在**，但语义**完全不同** —— 它是「连续 2 秒内 11 个字段全等」的**折叠计数**，**不是** UE 回显标记。别拿它解释本条。

---

## [17] `execute_debug_action` 的 `effects.logs` 只给最后 50 条，一条 DebugAction 的开头被挤掉 🔴→🟠

**症状**

一次 `Run All Tests` 产生几百行日志，`execute_debug_action` 只返回**最后 50 条**
（`logCount:50` 是返回条数，不是真实条数）。
⇒ **最关键的「第一条」**（`== [XxxTests] 开始 ==`）和 `[零覆盖]` 的**逐例行**几乎必定被挤掉。
更糟的是**返回里没有任何字段说明「被截断了」** —— `ResultWasTruncated:false` 指的是
**operation 的 Result**，不是 logs 数组。

**复现**

`rimworld.execute_debug_action{path:"Actions\\Run All Tests"}` ⇒ `logCount:50`，
`logs[0].message = "== [FlightAStar] 全部通过 =="`（最后一条），
`logs[49]` 是 50 条窗口里最早的那条。

**影响 / 严重度**

🟠 中。`Player.log` 里明明有，但**结构上读不到**；不知道截断这件事的人会以为「那些用例没跑」，
或者反过来只看返回的最后几行就下结论（本项目已经踩过「只看汇总行就判绿」这个坑）。

**建议**

① 加一个 `logsTruncated: true` + `logsTotal: N`；
② 或支持 `logWindow: {offset, limit}`；
③ 或至少在 `logCount < 真实条数` 时给个显式 warning。

**状态**：待绕过（改用 `Get-Content` + grep 直接读 `Player.log`）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**，**并入计划 01 的任务 1 + 任务 2**（并入成本接近零）。报告：`docs/issue-triage/triage/17-execute-debug-action-logs-window-truncation.md`
> **截断确在哪一层**（反编译**运行时**的 RBS：`workshop\content\294100\3727949765`）：`RimWorldDebugActions.DescribeEffects` → `LogJournal.GetEntries(50, …)` → `source.Take(limit)`，且 **`logCount = list.Count` 所以饱和在 50**。`_entries` 用 `Insert(0,…)` **倒序** ⇒ **本条上面那句「`logs[0]` 是最后一条」是准的**。
> **MCP 层与 GABS 层均无截断**：`MCP\index.js:4350` 传 `filterResult=undefined`、`:2761-2762` 该分支不进；`gabpClient.js:59` 的 16MB 是**接收缓冲**不是条数上限。
> **⇒ 所以能修的不是"窗口大小"，而是"没告诉你被截了"** —— 本仓库加 `RBS_EFFECTS_LOG_CAP = 50` + `describeLogWindow(effects)`，挂进计划 01 已有的 `execution` 块；**任务 2 的 `wrapGABPResult` 实际零改动**。
> **❌ 建议 2（`logWindow{offset,limit}` 分页）明确不做**：RBS 的 `50` 是**硬编码字面量**，且 `execute_debug_action` 签名只有 `path/pawnName/pawnId/x/z/thingId`，**没有任何分页入参** ⇒ 本仓库不可实施。
> **🚨 三个硬约束，不写清楚实现者一定会写歪**：
> ① **`truncated` 不能用布尔！** 恰好 50 条且**未**截断，与**已**截断**无法区分** ⇒ 必须 `true | false | 'possible'`。用布尔就是**制造一个新的假阳性**。
> ② 补数只能靠 `rimworld/list_logs{limit:2000}`（RBS 整个 ring），但它**不是分页**：`afterSequence` 只过滤更新的、**没有 `beforeSequence`**、且**倒序**。
> ③ `stateBefore` **不含** `logSequence`（RBS 的 `CaptureExecution` 把它存进 `ExecutionCapture.LogSequence`，回包给的是 `.State`）⇒ 调用方拿不到窗口下界，只能靠 `logs[].sequence` 推断。**别许诺一个做不到的补数能力。**
> **⚠️ 本条顺带查出计划 01 的两处前提问题**，已回灌给计划 01 的作者：真实回包**无** `operation` 包装 / **无** `durationMs` / **无** `ResultWasTruncated`（本条说的 `ResultWasTruncated:false` 指的是 operation 的 Result，**不是 logs 数组** —— 你这个判断是对的）；且 **RBS 顶层 `success` 是真实执行结果**（同步 Invoke + 抛异常才 false），比 `logCount` 强得多，计划 01 现有判定规则可能白丢了这条凭据。该结论子代理标为「待核对」，执行前需拿真实回包核对字段名。

---

## [16] `search_source` 对 `read_csharp_symbol` 能读到的文件回 `No results found` 🟠

**症状**

同一个 MCP 里两条工具对**同一个文件**给出矛盾的结论：

- `read_csharp_symbol(typeName="Thing", memberName="DeSpawn")` → **成功**，返回 `// File: Verse/Thing.cs (Lines 945-1036)` 全文字（Verse 命名空间，不是 RimWorld）。
- `search_source(query="pather\\?\\.StopDead\\(\\)", file_pattern="Verse/Thing.cs")` → **`No results found. Try adjusting your search query or file pattern.`**
- 同样 `file_pattern="Verse/AI/Pawn_PathFollower.cs"` 搜 `StopDead\(\)` 也是 `No results found`。

**复现**：任何"某方法体里到底调了什么"的问题，先 `read_csharp_symbol` 读全文（它会告诉你文件路径），
再用 `search_source` + 那个 `file_pattern` 复核 → 必然零命中。

**影响**：**典型的零命中假阴性**。我据此一度得出"`Thing.DeSpawn` 不碰 pather"的**错误结论**，
差点把一份分计划里写对了的东西当成写错（真实情况是 `Verse.Pawn.DeSpawn` override 里第 3 行就调 `pather?.StopDead()`，
而 `Verse.Thing.DeSpawn` 确实不调 —— 只读基类会得出相反答案）。
在这种项目里，一条"零命中"的错误结论会被写进代码注释 / 分计划正文 / 报告，然后被下一个人当事实继承。

**建议**：要么让 `search_source` 的索引覆盖 `read_csharp_symbol` 能读到的同一份源，
要么在返回 `No results found` 时区分"文件不在索引里"与"文件在但没匹配"，两者提示词必须不同。
另外 `read_csharp_symbol` 返回的是 `override` 还是 `base` 方法建议**显式标注**（这次就是 base/override 区分踩的坑）。

**状态**：待绕过（绕法：用 `read_csharp_symbol` / `read_file` 读全文，别用 `search_source` 核 Verse 内部调用链；或用 decompiler MCP 的 `find_callers`）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**，**并入 [11]**，不另立计划。报告：`docs/issue-triage/triage/16-search-source-index-coverage.md`
> **⚠️ 先说最要紧的：上面「状态」里那句话——「别用 `search_source` 核 Verse 内部调用链」——是错的，请勿照做。**
> 它建立在错误诊断上，照做会把一个好工具误废（实测 `search_source` 搜 Verse 搜得好好的）。原文保留在状态行里**仅为记录**，不代表仍然有效。
> **⚠️ 表层诊断被证伪，根因不是「索引没覆盖」。** 实测 `search_source{query:"StealAIDebugDrawer", file_pattern:"Source/Verse/Thing.cs"}` → **3 条命中**；
> `Source/Verse/Thing.cs` **100% 在索引里**，行号还是准的。板上「同一文件能读不能搜 / 命中面不一致」**事实相反** —— 逐行交叉验证显示两个工具**零分歧**。
> **真机制（第三种）**：`file_pattern` **必须带 `Source/` 前缀，否则静默零命中**。同一 token、同一文件，只改路径写法：
> `StopDead` + `Source/Verse/AI/Pawn_PathFollower.cs` → **4 条命中**（含 `204: public void StopDead();`）；`StopDead` + `Verse/AI/Pawn_PathFollower.cs` → **`No results found`** ❌
> **致命处**：`read_csharp_symbol` 返回头打印的就是**不带 `Source/` 的路径**（计划方复测得 `// File: RimWorld/StealAIDebugDrawer.cs (Lines 7-135)`）——把它原样回喂 `file_pattern` 实测**零命中**；
> 而 `read_file{path:"…"}` 会正确报 `Error: File not found`，`search_source` 却回 `No results found` —— **撒谎的那个反而是静默的**。**越规范越中招。**
> **严重度 🟠 → 🔴，且比 [11] 的那半更重**：[11] 要你自己写错 query 才中招；[16] 只要**照抄工具自己打印的路径**就中招。
> **❌ 本条上面那句「绕法」建立在错误诊断上，请勿照做** —— 「别用 `search_source` 核 Verse 内部调用链」会把一个好工具误废（实测它搜 Verse 搜得好好的）。
> 正确姿势：**去掉 `file_pattern` 裸名宽搜定位文件**，或把路径补成 `Source/...`；要核调用链也可用 decompiler MCP 的 `find_callers`。
> 归属：外部 MCP server `rimworld-source-online`，本仓库 `*.cs` 与 `MCP\` 目录 grep **双双 0 命中**，改不了代码 ⇒ 只能落文档约定。
> 计划：`docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md`（已追加 [16] 相关四条：规矩 0 / 排除法第 0 步 / 对应表 / 正则元字符转义）
> **板上细节更正**：「`Pawn.DeSpawn` override 调 `pather?.StopDead()`」**结论对、行号错** —— 在方法体第 9 行、文件第 **3666** 行，不是「第 3 行」。而「`Verse.Thing.DeSpawn` 不碰 pather」经复核**是对的**，那次零命中本就是真阴性。

---

## [15] `Log.ErrorOnce` 的消息正文被 `Error_Patch2` 吞掉，只剩栈 🟠

**症状**

vanilla `GizmoGridDrawer.cs:132-135` 是 `Log.ErrorOnce($"{arg} currentSelectable: {obj…}", 3427734)`。
按这条判据 grep `Player.log`：**`currentSelectable:` 命中 0 行、`3427734` 命中 0 行** ——
但**异常确实发生了**，栈也在：

```
System.NullReferenceException: Object reference not set to an instance of an object
  at RimWorld.Pawn_CreepJoinerTracker+<GetGizmos>d__50.MoveNext () [0x000c3] …
  …
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Log.Error_Patch2 (string)
UELoader.ExplorerBootstrap:OnUELog (string,UnityEngine.LogType) (at …/UELoader/ExplorerBootstrap.cs:204)
UnityExplorer.ExplorerCore:LogUnity (object,UnityEngine.LogType)
…
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Log:ErrorOnce (string,int)
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.GizmoGridDrawer.DrawGizmoGridFor_Patch5 (...)
```

即：`ErrorOnce` 的**字符串参数**经 `Log.Error_Patch2` → `ExplorerBootstrap.OnUELog` 之后
**不再原样写进 `Player.log`**，只留下 `Log.Error_Patch2` 与 `ErrorOnce` 两帧**栈**。

**复现**：装一个会在 IMGUI gizmo 收集里抛 NRE 的探针，然后
`Select-String Player.log -Pattern 'currentSelectable'` ⇒ 0 行；
`Select-String Player.log -Pattern 'Verse.Log:ErrorOnce'` ⇒ 有。

**影响**

- 严重度 **🟠 中**：**不崩，但会让判据假阴** —— 「grep 不到消息」会被误读成「没抛异常」。
  本档差点据此判 C3 失败；栈里 `Verse.Log:ErrorOnce` 明明白白写着。
- 这是本项目「零命中最危险的假阴性」的**新变种**：**不是日志轮转，是消息被中间层改了。**

**建议**

1. `UELoader.ExplorerBootstrap.OnUELog` 的 `LogType.Error` 分支别丢原 message，
   或至少补一行 `[UELoader-original] <原 message>`，保持 vanilla 文本可 grep。
2. 文档里写明「**UE 下判 vanilla 日志要靠栈帧（如 `Verse.Log:ErrorOnce`），不能靠消息正文**」。

**状态**：待修（本档已绕过：改判「栈首帧 + `ErrorOnce` 在栈中」）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**不值得修（核心断言已证伪）**。
> `Player-prev.log:11301` 逐字命中「currentSelectable: null」，当前 `Player.log` 0 命中**纯因日志轮转**，
> 不是消息被中间层改了 —— 板上建议 1 是在修一个不存在的 bug。
> 责任更正：`Verse.Log.Error_Patch2` 来自**第三方 RIMAPI**（`DevToolsHooks.cs:70`，`[HarmonyPatch(typeof(Log), nameof(Log.Error))]`，**Postfix 不抑制**），
> **UnityExplorer 对 `Verse.Log` 零 patch**；本项目 `Source/UELoader/ExplorerBootstrap.cs:204` **本来就在**转发 message。
> 真实根因 = 日志轮转 + vanilla `usedKeys` 去重，**已由 [09] 覆盖**。严重度 🟠 → 🟡。
> 残留可修项（仅影响可读性/行首匹配，子串 grep 不受影响）：`[UnityExplorer]` `[Unity]` 双层前缀。
> 报告：`docs/issue-triage/triage/15-log-erroronce-message-swallowed.md`

---

## [14] `read_file` 的 `start_line` 是 0-indexed —— 核精确行号时每条都差 1 行 🟠

**症状**

核 vanilla 行号（本项目的常规动作）时调用
`read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:162, line_count:22 }`。
返回的第一行是**空行**，第二行才是 `private Mesh GetBlitMeshUpdatedFrame(...)`。
按 1-based 读就是 `:163 = private Mesh …`，但它实际在 `:164`。

**复现**

`start_line` 是 **0-indexed**，而 `search_source` 返回的行号、以及所有人类写的计划/注释里的
`:164` `:243` `:402` 全是 **1-based**。同一个工具一次给 0-based、一次给 1-based，
而 `read_file` 的返回里**没有行号**，得自己数空行 ⇒ 数错一个就得到"计划行号对不上"的**假结论**。
本次审查核 `PawnRenderer.cs` 的 `:170`（第 5 实参 `0f`）、`:243`、`:334`、`:402/:403/:404`
和 `PawnRenderTree.cs:405-419` 时全程踩在这条缝上 —— 只是这次数对了。

**影响**

🟠 中。不是功能坏，是**给假证据**：一个 off-by-one 就会让审查者去报"注释里的行号对不上"，
而真正的错可能根本不在代码上。本次 W2-P05 审查正好报了 3 处**真的**行号失准
（`PawnRenderer_PosePatches.cs:114` 引用本档自己作废的 `:88`），如果再叠上这个 0/1 基混淆，
下一轮很容易把"读错了 1 行"当成"又一处注释缺陷"。

**建议**

`read_file` 的返回里带上行号前缀（哪怕只是 `[164] ...`），
或把 `start_line` 改成 1-based。任一条都行，现在这个 0/1 混用是纯陷阱。

**状态**：待修（临时绕过：每次读完自己数一遍空行再对）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修，但产出形态是「文档/流程约定」而非改代码**。
> 计划：`docs/issue-triage/plans/2026-10-01-14-read-file-line-number-convention.md`（3 个任务 / 6 次只读探针，预期输出全是真跑出来的）。
> **为什么不能改代码**：`read_file` / `search_source` 由**外部** MCP 服务器 `rimworld-source-online` 提供，**其源码不在本仓库**（全仓 `start_line` 代码 0 命中，`MCP\` 目录 0 命中）。
> 所以板上建议 1（加行号前缀）**属于那个外部仓库的 TODO，不在本仓库范围内**；本计划落地的是"引用行号时按什么规矩走"的硬约定。
> **⚠️ 本条比板上写的更糟，且有一条板上没发现**：
> ① 不只是 0/1 基混淆 —— **返回还会静默丢弃窗口首行的空行**（仅首行，窗口内部空行完好），导致**无法从返回值反推起点**，实测出现 **+2** 偏移；
> ② **板上漏了的一条**：窗口右移时，续读 tip 给的行号**比真实下一行少 1** ⇒ **照 tip 续读会重复最后一行**（`start_line=163` 与 `162` 返回逐字相同、tip 却是 166 / 165）。
> **采用方案：加行号前缀**（非破坏、可自校验）。**不采用「把 `start_line` 改成 1-based」** —— 那会打翻所有既有调用方且**静默错位**，正是本条要消除的失败模式。
> **另证一条退路不可用**：`get_source_slice{includeLineNumbers}` 给的是**反编译产物的相对行号**（该文件 84 行 vs 原文 449 行），与原文件行号**不可互换**。
> **落点**：`docs/mcp-source-lookup-conventions.md` 新增 `## 4. 行号基线`（**与 [11] 共用同一份规范文档**）。⚠️ 前置依赖：该文件目前**不存在**，是 [11] 计划任务 1 的产物 —— 计划已加 `Test-Path` 守卫，文件不在则**停止并报告被 [11] 阻塞**，不许自建覆盖。
> **`regex: true` 可复用**（[11] 实测）：裸名搜 2 条 → 用 `^\tprivate Mesh GetBlitMeshUpdatedFrame\(` 精确到 **1 条（164）**。
> 报告：`docs/issue-triage/triage/14-read-file-start-line-zero-indexed.md`

---

## [13] vanilla 渲染栈的「命名空间 ≠ 目录」，而计划只引裸文件名 🟡

**症状**

核一份分计划里的 vanilla 行号（`PawnRenderer.cs:353` / `:340` / `:170` / `:204`）时，
按上下文理所当然地试 `read_file{ path:"Source/RimWorld/PawnRenderer.cs" }` ⇒ **`File not found: Source/RimWorld/PawnRenderer.cs`**。
`search_source` 一查，实际路径是 **`Source/Verse/PawnRenderer.cs`**。

同属渲染栈的这几个类**全在 `Source/Verse/`**：
`PawnRenderer` / `PawnRenderNode` / `PawnRenderNodeWorker` / `PawnRenderTree` / `PawnDrawParms` / `PawnRenderFlags` / `PawnRenderNodeProperties`。
而 `PortraitsCache` / `ColonistBar` / `Dialog_StylingStation` / `DebugToolsMisc` / `Page_ConfigureStartingPawns`
**在 `Source/RimWorld/`**。⇒ **同一个渲染主题被劈成两个目录，劈法还和「这算不算 UI」之类的主题分类对不上。**

**复现**

```
mcp__rimworld-source-online__read_file{ path:"Source/RimWorld/PawnRenderer.cs" }        → File not found
mcp__rimworld-source-online__search_source{ query:"PawnRenderFlags\.Cache \| PawnRenderFlags\.DrawNow" }
  → ./Source/Verse/PawnRenderer.cs
      353:  PawnRenderFlags pawnRenderFlags = DefaultRenderFlagsNow | PawnRenderFlags.Cache | PawnRenderFlags.DrawNow;
```

⚠️ 坑的真正来源不是 MCP，是**上游文档**：分计划通篇写裸文件名（`PawnRenderer.cs:353`）、
只给 `:目录/文件` 的地方也是 `Source/Verse/PawnRenderNode.cs` 这种混着来的，看着就容易归错类。

**影响**

- 🟡 低，但**静默浪费一次往返**，而且是在「核行号」这种要求精确的活上 —— 容易把「目录猜错」一路带进
  写进代码注释 / 写进规格档的结论里（COMMON.md §10 点名的「一处 API 认知错误会连锁」那类）。
- 本次就踩了一次；另外还有一次同类小坑：`read_file` 即使**按设计**截断也总在末尾加
  `[TRUNCATED] … (Tip: Continue reading using start_line)` 脚注，容易被当成「没读到、失败」。

**建议**

1. `read_file` 路径不存在时，**能不能回一句「你是不是要找 `Source/Verse/PawnRenderer.cs`」**（基于索引做一次 basename 模糊匹配）？
2. 或加一个 `locate_type{ name:"PawnRenderer" }` —— 直接回答「这个类型在哪个文件 / 哪个命名空间」。
   现有工具必须先 `search_source` 才拿得到路径，**没有「按类型名定位文件」的入口**。
3. 上游文档侧：分计划引用 vanilla 行号时**带目录**（`Source/Verse/PawnRenderer.cs:353`）。
   本档的代码注释里我已经这么建议了（审查报告 Minor M4）。

**状态**：`待绕过`（用 `search_source` 先定位路径再 `read_file` 即可；不阻塞本档）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**不值得修**。
> 现象 100% 复现（`Source/RimWorld/PawnRenderer.cs` → `File not found` 且无任何提示），
> 但 `read_file` / `search_source` 由**外部 MCP 服务器 `rimworld-source-online`** 提供，**其源码不在本仓库**（全仓 `start_line` 代码 0 命中），改不了。
> 保留一条文档约定即可：**引用 vanilla 行号时必须带目录**（`Source/Verse/PawnRenderer.cs:353`）。
> 与 [14] 属**同一外部工具链**，共用该约定；真正值得修的是 [14] 的行号自校验，已单独立计划。
> 报告：`docs/issue-triage/triage/13-read-file-wrong-dir.md`

---

## [12] 没有「某模组在不在场」的工具；`activePackageIds` 不给路径 🟠

**症状**

`get_game_status` 返回的 `activePackageIds` 里有 `ancot.ancotlibrary`，
但 MCP **不给它的磁盘路径**，而且 `RimWorld\Mods\` 目录下 grep 不到 ——
因为 **Workshop 模组走 `steamapps\workshop\content\294100\<workshopId>\`，根本不在 `Mods\` 下**。

按「目录名 grep」判「模组装没装」⇒ **必然假阴性**。本档就被骗了一次：
分计划写「`Mods\*` 目录名含 Ancot ⇒ 零命中 ⇒ 当前安装没有 `AncotLibrary` ⇒ 结构性排除」，
**但它装了、启用了、而且已经加载进游戏进程**。

**复现**

```
agg_call_tool{ tool:"execute_csharp_code", args:{ code:
  string.Join(" || ", System.Linq.Enumerable.Select(
    System.Linq.Enumerable.Where(System.AppDomain.CurrentDomain.GetAssemblies(),
      a => a.GetName().Name != null && a.GetName().Name.ToLower().Contains("ancot")),
    a => a.GetName().Name + " @ Location=[" + a.Location + "]")) }}
```
→ `AncotLibrary @ Location=[C:\SteamLibrary\steamapps\workshop\content\294100\2988801276\1.6\Assemblies\AncotLibrary.dll]`

（顺带：本次调用确认了 `execute_csharp_code` 是**表达式**模式 ——
写 `return expr;` 会报 `CS0127: A return keyword must not be followed by any expression when method returns void`，
写「语句 + 末尾裸表达式」才行。文档 example `"Verse.GenTicks.TicksGame"` 确实是裸表达式，但没明说。）

**影响**

- 严重度 **🟠 中**：不崩，但**会让人得出事实相反的结论**（本档差点把「已加载、可能上栈的第三方 mod」判成「结构性不可能」）。
- 连带：`Player.log` 也**不是**判「装没装」的正确来源 —— 本档 grep `ancot` 零命中，但它压根不记录已加载程序集名。

**建议**

1. 加个 `mods.list_active` / `rimworld.list_loaded_mods`，直接返回
   **`packageId` + `Assembly.Location` + 「是否真在 AppDomain 里」**，一眼看穿「配置里启用」与「进程里加载」的区别。
2. `get_game_status.activePackageIds` 旁边补 `modRoots` 字段（`RimWorld\Mods` **和** `steamapps\workshop\content\294100\`）。
3. `execute_csharp_code` 的 description 写明「**表达式模式，不要写 `return`**」。

**状态**：待修（本档已绕过：查 `AppDomain.CurrentDomain.GetAssemblies()` 的 `Location`）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修，但原条目的主诉前提被推翻、范围大幅收窄**。
> 计划：`docs/issue-triage/plans/2026-10-01-12-mod-presence-assembly-visibility.md`（5 个任务 / 36 步 / 1409 行，本轮最长）。
> **⚠️ 主诉前提被推翻**：`rimworld.list_mods` **已经存在且够用** —— 实测直接给 `packageId` / `rootDir` / `source` / `enabled` / `loadedInSession`，
> `ancot.ancotlibrary` → `workshop\content\294100\2988801276`。板上那个「假阴性」**一击可消解**。
> **计划里已明写「不要重复造 `list_mods`」**（它的 schema 由 GABP 决定，本项目改不了）。
> **建议 2（`get_game_status` 补 `modRoots`）明确不做。**
> **真正剩下的缺口只有两个**：
> ① **程序集级** —— 新原生工具 `get_loaded_assemblies`，读 `AppDomain.CurrentDomain.GetAssemblies()` 并与
> `LoadedModManager.RunningModsListForReading → mod.assemblies.loadedAssemblies` 做**归属 join**，回答「某个 dll 是否真在进程里 / 归哪个 packageId」。
> **主菜单即可用**（刻意不加 `requireStage(IN_GAME)`）。
> **② 可发现性** —— 登记进 `game_control/game/mods` 叶子 + 默认暴露 + 文档写死「**不要用 `Mods\` 目录 grep**」。
> **📌 本条最好的一个协议设计**：游戏未运行时返回 **`answerAvailable:false` 而非空数组**，且 `assemblies:[]` **永远伴随 `appDomainAssemblyCount`**
> ⇒ **从协议层消灭假阴性** —— 空数组不再能被误读成"没加载任何东西"。
> ⚠️ **本条需要改 `.cs` 并重编译**（构建前**必须先停游戏**），与其余 11 份「零 C#」计划**性质不同**，排期要单独看。
> **建议 3（文案）已并入**，但只改未被占用的 `index.js:2072`/`:2078`；**`:2937` 一字不动** —— 它已被计划 02 整行重写，两处改同一行会互相覆盖（交接文案写在计划 §0.5）。
> **⚠️ 本条顺带查出计划 02 与本板的一手实测互相矛盾**（需起游戏才能裁定，**无人拍板**）：
> 计划 02 的验收行用 `…; return 1;` 并期望 `result=1`，而本板 `[02]` 段的一手实测是 `return expr;` → **`CS0127`**。二者不可能同时为真。
> 静态可确认**当前是 mcs 求值器**（`ScriptEvaluator.cs:2/22/72/73`，值通道 `ConsoleController.cs:180-183`）—— 计划已把文案写成**双分支**规避。
> **⇒ 下一位执行者请先起一次游戏裁定这一点，再改文案，否则可能把错的写法写进帮助文本。**
> 报告：`docs/issue-triage/triage/12-mod-presence-and-disk-path.md`

---

## [11] `search_source` 搜高频标识符 → 400+ 条重复结果，冲爆上下文 🟠

**症状**

搜 `AllPawnsSpawned`（只是想确认它的**类型**和**可见性**），返回
`[TRUNCATED] Showing 400/416 results`，约 150 行，全是同一标识符在不同调用点的复制粘贴上下文。
真正需要的答案（`public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;` 在 `MapPawns.cs:327`）
埋在正中间，且**没有任何排序保证它先出现**。

**复现**

`search_source{ query: "AllPawnsSpawned", file_pattern: "*.cs" }`
—— 不带 `file_pattern` 限定到 `Source/Verse/MapPawns.cs` 时最惨。

**影响**

🟠 **假阴性 + 上下文税**。两种受害：
1. 一次调用吃掉大半个上下文预算（本项目子代理本来就被 `list_debug_action_children` 的
   400KB 坑过一次，见 [03]）；
2. 更隐蔽的是**第一次搜"精确签名"会空手而归**：
   `query:"public IEnumerable<Pawn> AllPawnsSpawned"` 返回 `No results found`（因为实际返回类型是
   `IReadOnlyList` 而非 `IEnumerable`），很容易让人误判"这个成员不存在/不可见"。
   我差点据此改掉测试代码 —— 差点把一个 API 认知错误写进源码（正是 §10 说的连锁坑）。

**建议**

- 加一个**精确定位**档：query 命中"声明行"（`public/protected/private` 开头或含 `=>` 的赋值）
  时只回声明行，调用点折叠成 `… 还有 N 处调用点`；
- 或者支持 `file_pattern` 直指单文件（当前是 glob，但输出不按文件聚合）；
- 至少给一条提示：**No results found 时提示"试试去掉返回类型限定"** ——
  现在的零命中是最危险的假阴性。

**状态**：`待绕过`（我的绕法：先宽搜一次定位文件，再 `read_file` 精读那个文件）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**，但**真正该修的是「零命中假阴性」(🔴)，不是上下文税(🟡)**。
> 上下文税已经被 400 条上限 + 截断提示 + Tip 兜住，代价可控；而「写了 `public IEnumerable<Pawn> AllPawnsSpawned` → `No results found`」极易被误读成「这个成员不存在/不可见」，**这类错误结论会被写进代码注释和分计划正文，然后被下一个人当事实继承** —— 那才是本条真正的伤害。
> **计划里的关键增量（比本板建议更好用）**：实测 `search_source` **支持 `regex: true`**，
> 用声明行正则 `^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)+<成员名>\s*(=>|;|=)` 可以**直接命中声明行** ——
> `AllPawnsSpawned` 从 **416 条 → 1 条**（`./Source/Verse/MapPawns.cs:327`）。
> ⇒ 本板建议 1 想要的「精确定位档」**不用改外部服务器就能拿到**，假阴性与上下文税一次性解决。
> 另注：结果顺序**跨调用不稳定**（同一 query 实测一次排第 5、一次排第 19），所以更不能靠翻找。
> **约定落点**：新建 `docs/mcp-source-lookup-conventions.md` 作该服务的唯一规范文档，[13]/[14] 后续**并入同一份**；
> 入口挂在 `AI-DEPLOYMENT.md` §8.1。刻意**不**放根 `AGENTS.md`（harness 只自动加载用户级那份，新建即"写了没人读"）。
> 报告：`docs/issue-triage/triage/11-search-source-result-volume.md`

---

## [26] `Stage Test Map` 的交叉校验守卫对整个新编号段结构性失明 🔴

**症状**

`DebugActionStageTestMap.CrossCheckLedgerAgainstTests()` 抠测试文件里用例编号的正则是：

```csharp
@"MarkZeroCoverage\(\s*""([①-⑳㉑-㉟][^""]*)"""
```

字符类 `㉑-㉟` = **U+3251..U+325F**。而用例编号 36 以后是 **U+32B1 起**（`㊱`=U+32B1 … `㊿`=U+32BF）
⇒ **`㊱`~`㊿` 一个都匹配不到**（PowerShell `[regex]::IsMatch` 实测：`⑩`/`㉑`/`㉟` = True，`㊱`~`㊵` = **False**）。

**复现**

给 `CaseLedger` 加上 `Pawns > 0` 的新行（编号用 `㊱` 这类），不去动那个正则，然后跑 `Actions\Stage Test Map`：

- `missing` 方向不会报（它压根没识别到这些编号）；
- `orphan` 方向**会报**：`Pawns > 0` 的行参与判定 ⇒ 在 `testCases` 里找不到 ⇒
  `Log.Error("[StageTestMap][无结论] 账本多行/已失效：㊱ ㊲ ㊳ ㊴ …")`。

⇒ **加了正确的账本行，反而被判成账本腐化。** 与 `COMMON.md §2b.4 ②` 的「结构性失明」是同一族。

**影响**

🔴 高。它把「正确维护账本」这个动作变成「会报假红」的动作 ⇒ 施工者要么不敢加行（`RequiredFlyerCount()` 偏小 ⇒ 末尾用例零覆盖），
要么加了行被 `[无结论]` 挡回去。**而且失败信息的方向是反的**（说"多行/已失效"，实际是"识别依据失效"）。

**建议**

1. 字符类扩到 `㊰-㊿`（U+32B0..U+32BF）：`[①-⑳㉑-㉟㊱-㊿]`（实测加完 `㊱`/`㊴`/`㊿` 都命中）。
2. **更根上的一层**：这类"手写字符类枚举 Unicode 区段"迟早还会漏下一段。
   建议把"识别依据是否还覆盖得住"本身做成断言 —— 例如 `orphan` 分支在报出 `Pawns > 0` 的行之前，
   先检查 `testCases` 里的编号**是不是全部落在账本覆盖的区段内**；否则改报
   「交叉校验的识别依据已失效（测试里的编号超出账本正则的字符类）」而不是「账本多行」。
   —— 这正是 `COMMON.md §2b.4 ②` 说的：**这类守卫的失败信息必须能区分"数据错了"和"守卫自己瞎了"。**

**状态**：待绕过（本项目 W5 由登记代理在落地账本行的**同一处**顺手改字符类；根因方案见建议 2）

---

## [01] `execute_debug_action` 报告成功但根本没执行 🔴

**症状**

调用返回 `success: true`、`node` 完整、`operation.Success: true`，但：
- `effects.logCount: 0`、`effects.logs: []`
- `durationMs: 72`（正常执行同一条要 **2097ms**）

⇒ **动作压根没跑，但报的是成功。**

**复现**

游戏因 `ColonistBarColonistDrawer.DrawIcons` 的 NRE 被 `OnGUI` 异常循环打挂
（主线程 ≈170% CPU、`Player.log` 6 分钟零写入）之后，调
`agg_call_tool{tool:"rimworld.execute_debug_action", args:{path:"Actions\Stage Test Map"}}`。
同一时刻 `get_game_status` 仍报 `in_game:true / automationReady:true / gabp: connected`。

**影响**

🔴 **这是最坏的一类 —— 假成功。** 自动化流程会把「没跑」读成「跑了但没输出」，
于是要么误判通过，要么反复重试一个永远不会成功的调用。
本次是我准备取判据 F 的末行时撞上的：以为执行了、以为「没有日志 = 没有输出」，
实际上什么都没发生。**如果没有去查 `Player.log` 的行数/mtime，这个假成功会一路骗下去。**

**建议**

- 动作真正执行时返回一个**可判定的执行凭据**（至少 `executed: true` + 实际执行耗时下限），
  而不是回显请求参数就算成功。
- 或者：主线程繁忙 / `longEventPending` 时**显式 fail-fast**，返回"未执行"而不是"成功"。
- 参考：现有返回里 `stateBefore/stateAfter` 明明有 `longEventPending` 字段，
  但动作是否排队成功与之无关 —— 建议让二者挂钩。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修（🔴 全场最高优先级）**。计划：`docs/issue-triage/plans/2026-10-01-01-debug-action-execution-evidence.md`（3 个任务，**全程不起游戏、不联网**）。
> **责任更正**：本条**不是 RimBridgeServer 报假成功，是本项目自己盖的章** ——
> `MCP/index.js:2690-2694` 的 `wrapGABPResult` + `gabpClient.js:667-684` 让 `success:true` **只等价于「JSON-RPC 没报错」**，
> RBS 自己返回的 `operation.Success` / `effects.logCount` / `durationMs` **一个都没参与判定**。盖章判定点在 `callGABPResult` 的 **2758**。修主责部分**不需要碰第三方**。
> **⚠️ 本条上面两条建议，计划里都否决了，理由如下：**
> ① 「让 `longEventPending` 与是否排队挂钩」**在本仓库不可实施** —— 入队发生在 RBS 内部，其源码不在仓库。计划只做**只读回填**（`execution.mainThreadPendingBefore` + note 文案），明确拒绝据此把 `success` 判 false。
> ② 「`logCount === 0` 命中即 `success:false`」**会把真成功判成失败** —— 调试动作本可以不产日志，那正是本条问题的反面。改用 `logCount > 0` 作**正面凭据**，`=0` 只强化 note。
> **采取的判据**：「**有据才收紧**」—— 仅当 RBS 明确报失败才 `success:false`；无凭据时 `success` **保持传输语义**，另给 `execution.executed = null`。
> **不采取**把 `success` 直接改成「执行成功」：拿不到可信正面凭据，那样会变成**全面假阴性**，比现在的假成功更难查。
> **「未执行」如何测**：用现成的 `_testHooks.setGabpBridge`（`index.js:5453`）注入假桥接，`callTool` 返回 `{ok:true, result:<样本>}`（即 `gabpClient` 在「JSON-RPC 没报错」时的真实形状），"没执行"由样本承载 —— 传输与执行两件事解耦。
> 报告：`docs/issue-triage/triage/01-execute-debug-action-false-success.md`
>
> 🤖 **批注追加（2026-10-01，第 3 轮回灌后计划已修订）** — 新条目 [17][18] 的 triage 反编译了**运行时** RBS，挖出本计划两处前提可能有问题，计划已据此改：
> ① **反编译显示 `ExecuteDebugActionResponse` 返回的是顶层字段**（`success/path/node/stateBefore/effects/…`），**未必有** `operation` 包装 / `durationMs` / `ResultWasTruncated`。⚠️ **这条与本条正文写的字段名对不上**，可能是 RBS 版本差异。计划**没有把它当断言**，而是把 fixture 拆成 `*_RBS_SHAPE`（反编译形状）与 `*_BOARD_SHAPE`（板上原样）两套，并提出一条**标为待核对**的推测：**当初记本条的人可能把 MCP 信封自己的 `success:true` 误读成了嵌套的 `operation.Success:true`**。
> ② **RBS 顶层 `success` 可能是真实执行结果**（同步 Invoke `node.action`，抛异常才 false）⇒ 比 `effects.logCount` **强得多**。
> **计划对 ② 的处理值得记**：`PROMOTE_REMOTE_SUCCESS` **默认 `false`** —— `success===true` 单独出现**仍判 `unverified`**（不制造假阳性），但**如实透传** `execution.remoteSuccess` 并在 note 注明"语义未用真实回包核对"。**证据没被扔掉，升级只是翻一个常量**；§7.1 给了打开它的三条判定条件，其中一条是「若卡死表现为**秒回**而非超时，就不要打开」。
> **⇒ 给维护者的一句话**：执行本计划前，**先拿一次真实回包核对字段名**（计划 §7.1 给了确切三步，顺序不可反）。

---

## [02] `execute_csharp_code` 的 `Verse.Log.*` 不落 Player.log 🔴

**症状**

在 `execute_csharp_code` 的评估器里执行
`UnityEngine.Debug.Log("W1P01_PROBE_MARKER_ALPHA")` 或 `Verse.Log.Message(...)`，
**`Player.log` 里检索该标记 → 0 命中**。

**复现**

`get_game_status` 显示 `ue_available: true`、评估器可执行（代码确实跑了、有返回值），
但日志不落盘。同一时刻走 GABP 的 `execute_debug_action` 产生的日志是**能**落盘的。

**影响**

🔴 这条让 **[01] 的后果放大** —— GABP 超时时，唯一想到的兜底就是"改用 `execute_csharp_code` 反射调"，
但**兜底通道拿不到日志**，于是连"我到底跑没跑"都无法自证。
本次 P01 代理正是因此只能交出**等价证据**（10 个套件零抛异常）而不是读末行，
并明确标注「这弱于读末行」。

**建议**

- 评估器内的日志走一条**独立缓冲**，并提供 `read_rimworld_log` 的对应读取口；
  或至少提供 `execute_csharp_code` 返回 `console output`（Roslyn 编译回显已经在返回里了，
  说明有管道，只是 `Verse.Log.*` 没接进去）。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修，但范围必须收窄**。计划：`docs/issue-triage/plans/2026-10-01-02-execute-csharp-code-output-capture.md`。
> **严重度**：单看 🟠，**与 [01] 叠加后 🔴** —— [02] 会放大 [01] 的后果：GABP 超时时唯一想到的兜底是"改用 `execute_csharp_code` 反射调"，但兜底通道拿不到日志，于是**连"我到底跑没跑"都无法自证**。
> **⚠️ 范围收窄（否则会修错方向）**：本条要修的是**让 `execute_csharp_code` 的 `output` 覆盖用户代码里写的日志**，
> **不是**去接管「日志写进 `Player.log`」—— **那是 vanilla 既有行为**。
> ~~而 `Source\...\UEHttpHandler.cs:207-210` 的**退订时机早于 `Verse.Log` 的下一帧 drain** ⇒ **结构上就抓不到**用户日志。别指望改接线就能补上这一半。~~
> **🔴 上面这句已被计划方实测推翻，见下方「根因更正」—— 不要按它修。**
> **⚠️ 本条最要紧的前置**：板上那个「`Player.log` 0 命中」的观测，发生在游戏**已证实卡死**的同一分钟内（`MCP\logs\mcp-20261001-093829.log` 显示当时连续 60s GABP 超时），
> **判据被污染** —— 到底有没有真丢日志**无法核实**。别把修复目标建立在那个未证实的因果上。
> **⚠️ 扩大日志捕获前必须避开的坑**：`UE_Data\sinai-dev-UnityExplorer\config.cfg:11` 是 `"Log Unity Debug" = "True"`，
> 导致本项目 `ExplorerBootstrap.OnUELog` 存在 **`Verse.Log → Debug.Log → UE 钩子 → Verse.Log` 的回灌环路**
> （`UnityExplorer 2026-10-01 155526Z.txt:5-17` 可见成对出现）。**任何扩大捕获的改法踩上它都会放大成死循环。**
> **与已有计划的冲突**：`docs\superpowers\plans\2026-09-27-ue-csharp-console-roslyn-upgrade.md` **任务 8 改的是同一个 `ConsoleController`**，
> 且**不会顺带修好本条**（新 `Evaluate` 原样保留旧写法，其验收矩阵 14 条里无一条验日志捕获）→ 需排在任务 8 之后或标注冲突点。
> **🔴 根因更正（计划方反编译 `Assembly-CSharp.dll` 实证，推翻裁决报告）**：
> ① 「`Verse.Log` 排队到下一帧才 drain」在 **1.6.9676 不成立** —— 反编译 `Verse.Log.Message(string)` 可见 `messageQueue.Enqueue(...)` 之后**在同一调用栈里直接 `Debug.Log(text)`**；`Verse.Log` 里**根本没有 `Update()` 之类 drain**（`LogMessageQueue` 只喂游戏内 `EditWindow_Log`）。⇒ 裁决报告的根因链与它建议的「闭包跑完再等 N 帧」**都建立在一个不存在的机制上**，而那恰好是**唯一会真正放大环路**的做法 → **计划已取消等帧方案**。
> ② 「`Log Unity Debug = True` 下用户日志今天已被捕获」—— **其实早就捕获到了**（`Debug.Log` 同步触发 MonoHelper → `ExplorerCore.LogUnity` → `ExplorerStandalone.OnLog` → 现有 listener），只是被 `[Unity] ` 前缀污染并混入环路回声。**真正的缺陷是可移植性**：UE 默认配置（`False`，`ConfigManager.cs:119-121`）下 `output` 里一条用户日志都没有。
> **⇒ 验收基线已换掉**：不再基于无法核实的「Player.log 有没有 marker」，改为「**在 `Log Unity Debug = False` 下 `output` 也必须含 marker**」，并在 `True` 下验证内容与 `False` **完全一致**（只出现一遍、无前缀）。
> **回灌环路怎么避开**：改用**自有 `Application.logMessageReceived` 永久观察者**，且**只往内存列表写、绝不写任何日志** ⇒ **给环路零加边**；闸门只在用户代码的主线程闭包**动态范围**内开/`finally` 关（不跨帧）；单次 512 条硬上限只计数不追加。
> **🚫 硬禁令**：**禁止 Harmony patch `Verse.Log` / `Debug`** —— `MCP\index.js:4461-4469` 有**实测事故**（1 分钟 63.5MB + mono 崩溃）。
> 报告：`docs/issue-triage/triage/02-execute-csharp-code-log-not-in-player-log.md`
>
> 🤖 **批注追加（2026-10-01，第 5 轮 · 本条的核心前提已被正面证实）** — **🔴 → 🟠**
> `[20]` 的观测给出**正面证据**：`Player.log` 里 `Verse.Log.Message` 的**原文 + 栈**（栈为 `Debug.Log ← Verse.Log.Message_Patch1 ← ConsoleController.Evaluate ← UEHttpHandler.cs:192`）
> **证实 `Log.Message` 正文确实落盘** —— 与计划 02 §0.2 的反编译结论方向一致。triage 02 当时只能写「无法核实」，**现已坐实**。
> ⇒ **索引里「`Verse.Log.*` 不落 Player.log」这个前提是错的**（🔴 假阴性把观测当因果）。
> **建议索引改为**：「`execute_csharp_code` 仍是唯一**不受 UE 开关摆布**的取日志通道，但 `output` 的覆盖随 UE `Log Unity Debug` 开关变化（**默认 `False` ⇒ 一条用户日志都没有**）」。
> **边界（别过度推广）**：只证实了 `Verse.Log.Message`；`Debug.Log` 直调本轮**无实测**；`Log.ErrorOnce` 属 [15]（已判不值得修）。
> **⇒ 本条剩下的真实价值**不是「日志没落盘」（那是假的），而是：**唯一的、不依赖第三方开关的兜底通道目前不可用**。计划 02 做的 `output` 覆盖正是这条通道本身。

---

## [03] `list_debug_action_children` 返回 400KB+ 🔴

**症状**

`agg_call_tool{tool:"rimworld.list_debug_action_children", args:{path:"Actions"}}`
返回 **400,385 字节**（377 个子节点），harness 侧被 spill 到临时文件。
**每个叶子节点都重复了一整套 `node` + `source` + `execution` + `operation` 结构。**

**影响**

🔴 直接冲垮调用方上下文（我的上下文里它被 spill 掉了，但**第一次调用仍吃掉了大量预算**）。
要找一个动作路径只能靠 grep spill 文件，很别扭。

**建议**

- 加**过滤/深度/只取路径**的参数，例如 `?filter=Stage`、`?depth=1`、`?pathsOnly=true`。
- 或默认只回 `path` + `label` + `hasChildren`，需要详情再单独 `get_debug_action`。
- 顺带：`operation` 块每次都完整回一遍，字段量很大且对调用方无用。

**状态**：待绕过（我用 grep 工具搜 spill 文件）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**。计划：`docs/issue-triage/plans/2026-10-01-03-enable-search-debug-actions-and-fix-menu.md`（4 个任务 / 30 步 / 13 个用例）。
> **⚠️ 根因与板上写的完全不同：这不是"缺参数"，是"实现已有、暴露被关、位置挂错"。**
> 本仓库**早就有**一个端到端做完的 `search_debug_actions`，每条只回 `path` / `label` / `category` / `actionType` —— 正是板上建议 2 要的东西。真正的问题是：
> ① `MCP\toolConfig.json` 里它是 **`false`**（没开）；② `mcp_help` 把它挂在 `game_control/game/gameplay/debug_action` 而不是 `debugmenu`，**照文档路径下钻永远看不到它**。
> 板上建议 1（加 `filter`/`depth`/`pathsOnly`）**不需要做** —— 已经有一个更轻的专用工具了，只要把它打开。
> **`filterResult` 钩子：接上，但做成 opt-in、不改默认。** 钩子确实已存在（`index.js:2747` 签名、`:2761-2763` 判 `typeof === 'function'`），镜像分发处 `:4350` 一直传 `undefined`。
> **❌ 不采纳"默认投影"**：改已上线工具的默认响应形状，会让依赖 `children[].node/execution` 的既有自动化**不报错地拿到 `undefined`** —— 那正是这批板子反复出现的**假阴性**，比 400KB 更难排查。
> 实现用**按名剔除** `node`/`source`/`execution`/`operation` 四个重块，**不用白名单**：本机 RBS 只有 `Tests/`+`docs/`、没有产出 `children` 的 C# 源码，白名单只能靠猜、猜错就静默丢信息。形状不符时**原样透传，不猜不抛**。
> **📌 文档债比板上预估更严重：是 3 行不是 2 行** —— `README.md` 那张 DebugAction 表 5 行里，`list_debug_actions` / `get_debug_action_detail` **和** `execute_debug_action` **都已是已删原生工具**（`index.js:4899` 注释明说三个都删了），**只有 `get_debug_action_categories` 还活着**。文档写着根本不存在/走不通的工具，会持续把调用方引到死路。
> 另核实并**搁置**（非占位符）：`Languages\*\Keyed\UESDdebuger.xml:76-77` 同样残留孤儿 key，但删 XML key 需重新发布模组资源，收益风险比不划算，理由已写进计划。
> **连带影响已查清**：改开关**不会**破坏 `toolCountConsistency.test.js` —— `menuUniverseNames()` 与 `allToolNames()` **都不调用** `isToolEnabled`，跨分组移动结构守恒；对外 `listTools` 24→25。另 `toolchainFixes.test.js:254-261` 的 700 字符窗口右边缘正好压在 `:4350`，但三条断言目标都在改动点之前，**安全**（计划里单列了一步实测）。
> ⚠️ 两个环境事实：`MCP\toolConfig.json` 是**单行压缩 JSON**（`ReadAllLines` 长度=1），只能按 key 定位；镜像是 GABP 镜像，描述来自 RBS 的 `tools/list`，**本仓库没有可改的描述** —— 所以只能改 `TOOL_USAGE_NOTES`。
> 报告：`docs/issue-triage/triage/03-list-debug-action-children-payload.md`

---

## [04] 启动类调用必超时，超时 ≠ 失败 🟠

**症状**

`start_game{waitForNotification:true, timeout:180000}` → `MCP error -32001: Request timed out`
`start_quick_test{timeout:300000}` → 同上

但**游戏确实起来了**：随后 `get_game_status` 显示 `stage: GAME_STARTING` → `IN_GAME`。

**影响**

🟠 无法区分「超时且失败」与「超时但正在成功启动」。调用方只能盲重试或盲等。
本次我连发两次 `start_quick_test`，两次都超时，实际第一次就成了。

**建议**

- 启动类工具在超时时返回**结构化状态**（至少 `{timedOut:true, processSeen:true, pid:...}`）。
- 或把「等待就绪」与「发起启动」拆成两个工具，后者应快速返回。

**状态**：待绕过（靠 `get_game_status` 复查）

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**。计划：`docs/issue-triage/plans/2026-10-01-04-startup-call-timeout-disambiguation.md`（3 个任务，**验收全程不启动游戏**）。
> **根因更正**：板上建议的「拆成两个工具」其实**早已实现**为 `waitForNotification:false`（实测 4 次零超时、11.9–21.8s）。真正缺的不是能力，是**文案 + 默认策略** —— 调用方不知道该传这个参数，于是盲重试。
> **最关键的一层**：真正生效的默认值在 `MCP/index.js:4358` 的 `args?.waitForNotification ?? true`；schema 里的 `default:true` 只是注解、**SDK 不填充** ⇒ **只改 schema 默认值是完全无效的改动**。
> 本条保留 `true` 不动（改成 `false` 是破坏性变更，会改变 GABP `ensureConnected` 时机），只补描述文案与 `mcp_help` notes —— 而 `TOOL_USAGE_NOTES` 对这两个最易踩的工具**原本完全空白**。
> 另：`start_quick_test` **根本没有** `waitForNotification` 参数（永远阻塞），文案必须与 `start_game` 分开写。
> 报告：`docs/issue-triage/triage/04-startup-call-timeout-ambiguity.md`

---

## [05] `stop_game` 的 force 竞态误导 🟠

**症状**

```
stop_game{force:false}  → {"success":false,"message":"Game still closing, may need force","pid":24364}
stop_game{force:true}   → {"success":false,"message":"Game not running"}
```

两次之间游戏**自己退掉了**（或正在退）。

**影响**

🟠 「may need force」这句话会诱导调用方升级到 force，结果拿到 "not running" ——
反而比第一次更难判断。

**建议**

`still closing` 时返回**已观察到的进程状态**（还在/已不在），或提示调用方复查而不是升级。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**。计划：`docs/issue-triage/plans/2026-10-01-05-stop-game-pid-ownership-and-status.md`（3 个任务）。
> **⚠️ 本条比板上定的 🟠 严重得多：会杀掉你手动开的另一个 RimWorld。** 计划方已**完整推演坐实**：
> `start_game` 登记 `selfStartedPid=A` → 第一次 `stop_game` 在 `:1184` **无条件抹掉归属** → A 自行退出、B（你手开的）仍在
> → 第二次 `stop_game` 在 `:1185` **回退到 `findRimWorldProcess()` 取到 B** → `:1192` `taskkill /F /PID B` **强杀你的实例**。
> **触发条件**：≥2 个 `RimWorldWin64.exe`，且对同一次停止动作调用 ≥2 次 `stop_game`（板上记录的那次正是此路径，pid 24364=A）。
> 所以本条**不能只改文案** —— 只改文案，这条会原样留下。
> **⚠️ 裁决报告给自己标的误杀开的药方，治不了它自己标的 bug**：报告建议「把 `:1184` 的无条件清空移到成功路径」，但第二次调用的真实故障态是
>「**登记过的 PID 已死 + 扫到别的实例**」——此时 `selfStartedPid` **仍非空**（只是不存活），`:1183` 判 false 后**照样回退扫描**，误杀照旧。
> 计划改成**硬规则**：**登记过的 PID 一旦已退出，就绝不回退扫描**，直接返回「本来就没在跑」（`already-stopped`）。回退仅在「本服务从未登记过任何 PID」时启用。
> **同函数里还有一处假成功，板上和报告都没发现**：`:1198` 复查用 `findRimWorldProcess()`，而 `findProcess`（`:878-890`）**只返回 tasklist 第一个匹配**；
> 多实例时若靠前的不是 `target`，`:1200` 比对失败直接落到 `:1204` 返回 **`{success:true, message:'Game closed'}`** —— 而 target 其实还在跑。
> 计划改用**已存在的** `isPidAlive(target)`（`:899-907`）复查，并加源码护栏钉住。
> 测试全程**不碰任何进程**，用纯策略模块 `MCP/stopPolicy.js` 构造三种状态（自启动 / 非自启动 / 已退出）。
> 计划内含的护栏：不得写真实 `stop_game` 调用测试；不得改 `:4371-4382` 工具入口（`test/toolchainFixes.test.js:263-268` 依赖它）；不得把 2 秒等待改成循环等待。
> 报告：`docs/issue-triage/triage/05-stop-game-force-race-misleading.md`

---

## [06] DebugAction 路径格式无文档 🟠

**症状**

`[DebugAction("FlightAStar", "Run All Tests", …)]` 的路径是 **`Actions\Run All Tests`**：
第一个参数 `category` 是**返回字段**，**不是路径段**。

我第一次猜成 `Actions/FlightAStar` → `Could not find debug action 'Actions/FlightAStar'.`
又试 `/` 分隔（其他地方用 `/`）→ 同样失败。

**影响**

🟠 每次新会话都要重新踩；分计划正文里也普遍写成 `FlightAStar/Run All Tests` 这种错法，
建议要么改文档、要么让 `get_debug_action` 在找不到时**提示最接近的候选**。

**建议**

- 在 `list_debug_action_roots` 的返回里直接给出**格式说明 + 一个例子**。
- 或支持 `Actions/FlightAStar/Run All Tests` 与 `Actions\Run All Tests` 双写法。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**。已出计划 `docs/issue-triage/plans/2026-10-01-06-debug-action-path-format.md`（3 个任务，**零 C# 改动**，因此**不需要重编译模组 DLL、不会撞上正在跑的并行调试会话**）。
> 落地后规则进「唯一事实源」常量，再分发到：2 个工具 `description` + 6 条 `TOOL_USAGE_NOTES`（= `mcp_help tool=` 的易错提示，主入口）+ 2 个返回体的 `data.pathFormat` + `README.md` + 首次配置向导（中英各一）。
> **核实更正**：唯一样例确实藏在**隐藏工具** `hotreload_apply` 里（`index.js:2410-2411`、`:2980`，`toolConfig.json` 为 `false`，`test/hotreloadConcealment.test.js` 钉住它不出现）；而正确写法其实**已被实跑验证**（MCP 日志里 `Actions\Run All Tests` 共 15 次 success）。
> **`/` 双写法不做**：vanilla `Dialog_Debug.cs:296` 的 `path.Split('\\')` 决定 —— 那不是本项目的 bug。
> 报告：`docs/issue-triage/triage/06-debug-action-path-format.md`

---

## [07] GABP 在主菜单/加载中静默 idle 🟠

**症状**

`get_game_status` 的 `capabilities` 里会写
`"RimBridgeServer（GABP 镜像工具）… usable:false … note":"GABP 未连接（state=idle）"`，
但**具体哪些 `rimworld.*` 工具不可用**要靠 `mcp_help` 再翻一层才知道。
且 idle 期间调用它们是**超时**，不是明确报错。

**影响**

🟠 容易在游戏还在加载时反复重试，白等。文档里也反复强调「等 GABP 连上」。

**建议**

- GABP 未连时，`rimworld.*` 调用应立即返回 **明确的 "GABP 未连接"**，而不是超时。
- `mcp_help` 里给每个工具标 `requires: [GABP connected]`。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修，但严重度 🟠 → 🟡、范围大幅收窄**。
> 计划：`docs/issue-triage/plans/2026-10-01-07-gabp-offline-help-fallback.md`（3 个任务 / 24 步 / 11 个自动化测试，**零 IO 构造离线态**）。
> **⚠️ 本条的头号症状已被证伪**：不是「超时」。`index.js:4274-4280` 在转发前就短路 fail-fast，日志实测 `→ 3ms error(GABP_MIRROR_UNAVAILABLE)`（`mcp-20261001-093829.log:961`）。
> 日志里那些 30s/60s 超时（日志 `:405/408/411`）发生在 GABP **已连接之后**，是 **RimBridgeServer 假死** —— **另一个根因、第三方**，本项目已有「3 次超时强制重连」的缓解。**不要和本条混成一条。**
> **真正剩下的缺陷**：`index.js:3611` 写着「对应能力说明见 `mcp_help tool=<名字>` 的 precondition」，但 GABP 离线时这条指路**必然失败** ——
> `allToolEntries()`（`:3352-3359`）不含 `gabpTools`，断连时 `gabpTools=[]`（`:2514`/`:2649`），缺陷分支在 **`:3653-3656`** 且无兜底 → 返回「未知工具」→ **把调用方送到第二堵墙**。
> 而 `agg_list_tools`（`:3605-3612`）和菜单叶子其实**已经能标出离线镜像** —— **信息可达，只是路走错了**。
> **最省事的关键事实**：`unknownToolText`（`:3624-3643`）**本来就已经正确实现了三态归因**；`mcp_help` 出错是因为**绕过它自己手写了一份**。所以计划**不新造归因逻辑**，只做三路分流 + 占位详情。
> **两个容易做错的细节**：① 离线占位详情必须 `isError:false` 且**不能带 `usageHint`** —— `:3649` 那句「改走 `agg_call_tool`」在离线时**同样是死路**；
> ② `gabpErrorResponse`（`:2697-2702`）的错误文本在 **`body.error`**、errorCode 恒为 `GABP_ERROR`（不是 `GABP_MIRROR_UNAVAILABLE`），断言写错就翻车。
> 另注：`index.js:3339` 是**死代码**（`:3327` 已把所有镜像归入 `'bridge'`），计划已标注「不要顺手清理」。
> **离线态如何测（零 IO）**：`UESD_MCP_NO_START='1'` 使 `gabpBridge` 恒 null；`_testHooks.setGabpBridge({state:'idle',isConnected:()=>false})` 复刻真机 idle —— ⚠️ 前者会得到 `state=disabled` 而非真机的 `idle`，**要断言 idle 必须走后者**。
> 报告：`docs/issue-triage/triage/07-gabp-idle-no-fail-fast.md`

---

## [08] `mcp_help` 要逐层下钻 🟡

**症状**

找一个 `pause_game` 要：`mcp_help{}` → `path:"game_control/game"` → `path:".../state"` 才看到。
而 `pause_game` 是**每个游戏会话都要用**的高频工具。

**影响**

🟡 每次新会话都要重新翻。

**建议**

- `mcp_help` 支持关键词过滤：`mcp_help{query:"pause"}`。
- 或在概览里给一个「高频 10 个」快捷区。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修（🟡 维持，但根因是「入口形态」而非「信息缺失」）**。
> 计划：`docs/issue-triage/plans/2026-10-01-08-mcp-help-query-param.md`（3 个任务 / 19 个测试，**只碰 `MCP/index.js`，零 `.cs` 改动**）。
> **只采纳建议 1（加 `query`）**，另加一行 `agg_list_tools` 引导文案。
> **❌ 建议 2（「高频 10 个」快捷区）明确不做** —— 硬编码名单**会立刻过时**，且没有实测数据支撑；这条诉求已被 `query` 完全覆盖。
> **❌ 也不做「拍平菜单层级」** —— `index.js:3361-3368` 的注释记录过此前因「三套口径互相矛盾」导致的计数漂移（概览 194 vs 实际 191），动导航结构会重踩那个坑。
> **优先级定为 `tool` > `query` > `path` > 无参**；同时传 `{query, path}` 时走 `query` 并**回显 `ignoredParams:["path"]`** —— 杜绝静默丢弃参数造成假成功。
> `additionalProperties` **保持 `false`**、不加 `keyword` 别名：未声明的参数会被 schema 校验的客户端**硬拒**，那是**可见的失败**，比静默接受一个错别字安全。
> **两个额外发现的坑（裁决报告未提，已在计划中处理）**：
> ① `attachUsageOnFailure`（`:4009-4018`/`:4024`）会给 `success:false` 的响应追加无关 usage 噪声 ⇒ 零命中返回体**刻意不带** `success`/`isError`；
> ② `test/toolchainFixes.test.js:121` 对 `toolListLine`→`buildMenuNode` 做**源码切片断言** ⇒ 新函数必须插在 `buildMenuNode` 之后、`toolDescriptionFirstLine` 插在 `toolListLine` 之前，否则测试会碎。
> 报告：`docs/issue-triage/triage/08-mcp-help-deep-drill.md`

---

## [09] Player.log 轮转无任何提示 🟡

**症状**

RimWorld **每次启动都轮转**：`Player.log` = 本次会话，`Player-prev.log` = 上次会话。
没有任何工具暴露这一事实；`read_rimworld_log` 只读当前。

**影响**

🟡 **制造假阴性**：冷重启之后 grep 新 `Player.log` 报「0 条 Harmony 告警」，
很可能只是因为日志还是新的、本轮什么都没写进去。

**建议**

- `read_rimworld_log` 加参数 `source: current|prev|all`，并在返回里带上
  `path` / `lineCount` / `lastWriteTime` —— 这三项齐了，调用方才能判断「这个 0 命中可不可信」。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**（严重度维持 🟡，但本条有 **🔴 的假阴性内核** ——「零命中」无法判断可不可信，就会被当成「没有发生」）。
> 计划：`docs/issue-triage/plans/2026-10-01-09-rimworld-log-provenance-metadata.md`（3 个任务 / 20 步 / 18 个用例，全 TDD 顺序）。
> **⚠️ 先划清责任**：日志轮转是 **Unity 引擎行为，本项目零参与、修不掉**（仓库与 vanilla 源码检索 `Player-prev` / `LogRotator` 均零命中）。**别去改轮转本身**，本条只修"返回值缺少可信度元数据"。
> **真缺陷（板上没点出）**：`index.js:1373-1399` 的 `readLog` **不返回 mtime**，而同文件 `tailLog`（`:1402-1428`）**返回了 lastModified** —— **同一对孪生工具口径不一致**。计划做了口径对齐 + 一致性护栏测试（两者返回键集合必须逐字相同）。
> **⚠️ 计划方新发现、裁决报告也没抓到的陷阱 —— NTFS metadata tunneling**：
> 实测 `Player.log` 与 `Player-prev.log` 的 **`birthtime` 都是 `2025-12-15T12:54:30.738Z`** —— Windows 把旧文件的创建时间**套到了新文件上**，ctime 也与 mtime 同步。
> ⇒ **`birthtime` 和 `ctime` 都不能当会话起点**（看起来能用的东西最会骗人）。
> 会话起点只能取 **`Player-prev.log` 的 mtime**，这就是新增的 **`rotatedFrom`** 字段的定义。
> **另一个假阴性源（裁决报告漏了）**：返回值有 `totalLines`/`returnedLines`，却**没告诉调用方 `content` 只是尾部片段** ⇒ 计划补 `coverage: full|tail` 字段。
> **`Player-prev.log` 路径推算**：`derivePrevLogPath(logPath)` 走 `dirname`/`basename`/`extname`（不用正则），规则 = 同目录同名、扩展名前插入 `-prev`；本机实测该目录只有 `Player.log` 与 `Player-prev.log` 两个轮转文件（无编号副本）。**首次启动时 prev 不存在 → 明确报错，绝不静默回退。**
> 报告：`docs/issue-triage/triage/09-player-log-rotation.md`

---

## [10] 无法查询实际 API 成员可见性 🟡

**症状**

想知道某个 vanilla / 模组类型的成员是否 public，只能靠
`mcp__decompilerserver__*` 反编译，或撞编译错。
实战里连续踩了两次相反的结论：
- `ThingMaker.MakeThing` 到底是「只有单参重载」还是「一个带默认参的方法」——
  前者让人写出 `stuff=null` 的 error 级日志，后者显式传参即可消除。
- `Thing.Position` 到底是「有 public setter」还是「不可写、须反射 `_position`」。

**影响**

🟡 直接导致错误代码与错误注释（本次两份分计划的正文都记错了）。

**建议**

`inspect_type` 之类的工具若能直接给出**成员可见性 / 是否 static / 是否只读**
（而不是只给类型是否存在），这类错误在写码前就能消掉。

**状态**：待修

> 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修**，严重度 🟡 → **🟠**（不升 🔴：没有工具在撒谎，**一次查询即可证伪**，但错误结论**已经写进过分计划正文**）。
> 计划：`docs/issue-triage/plans/2026-10-01-10-api-member-visibility-tools.md`（3 个任务 / 22 步，8 个文件）。
> **好消息**：本条**能改代码**（不像 [11]/[13]/[14]/[16] 那族只能写文档）—— Cecil 元数据就在本仓库 `McpRimDebug\DebugSession.Eval.cs` 里，只是**拿到了却没往外给**。
> **⚠️ 计划方发现的真根因（裁决报告完全漏掉）**：`DebugSession.Eval.cs:359` 的**去重键是 `typeFullName + ":" + methodName`**
> ⇒ **同名重载会被合并成一条**。这才是「返回看起来像个答案、实际不足以判断」的真正原因
> —— 板上那两次相反结论（`ThingMaker.MakeThing` 到底几个重载）就是栽在这里。（`Verse.ThingMaker` 那个例子恰好因该类型只有 1 个同名成员而**侥幸没被吃掉**。）
> **补的字段**：`signature` / `isPublic` / `isStatic` / `isVirtual` / `hasOptionalParameters` / `isSpecialName`。
> 其中 **`isSpecialName` 是最低成本的识别手段** —— 板上第二个坑（`Thing.Position` 有没有 setter）对应的 IL 成员正是 **`set_Position`**。
> **明确不做**：① 不给 `inspect_type` 返回体加成员数据（载荷无上界，且只在 IN_GAME 可用）；
> ② 不新增 `describe_type`（YAGNI —— 离线场景已由 decompilerserver 覆盖）。
> **C# 侧铁律（反编译 Mono.Cecil 0.10.4 实证）**：`ParameterDefinition.Constant` 的 setter **只写字段、不置 `HasConstant`**，
> 而 `HasConstant` getter 走 `ResolveConstant`（`module.HasImage()` 为假时会把常量重置为 `NoValue`）
> ⇒ **只有 `IsOptional` 为真时才读 `Constant`**。这类"看着能读、读了就是错的"陷阱，只有反编译依赖库才挖得到。
> 测试：`dotnet run --project McpRimDebug -- --selftest`（C# 侧，全程不连 VM/游戏）+ `npm --prefix MCP test`（Node 侧）。
> 报告：`docs/issue-triage/triage/10-api-member-visibility.md`

---

## 值得表扬的部分（免得以后改坏）

- **超长输出的 spill 机制**：`agg_call_tool` 返回巨大结果时自动落临时文件并告知路径，
  避免了单次调用把上下文撑爆 —— 这个设计救过场（见 [03]）。
- **`get_game_status` 的 `capabilities` 结构**：把「哪个 provider 提供什么能力、当前可用吗、
  不可用时为什么」讲清楚了，省了大量猜测。
- **`get_game_status` 里带 `mono.debugPortFromLog` / `debugPortOpen`**：交叉验证通道是否真的开着。

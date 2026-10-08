# DebugAction 索引改造：建表时急建静态索引（设计）

日期：2026-10-08
状态：待用户审查
范围：本模组原生实现（C# `UEDebugActions.cs` + MCP `index.js`），不含 GABP 镜像

## 1. 背景与问题

### 现状机制（懒建树）

- C# 侧 `UEDebugActions.ExpandBudgeted`：每次请求只给 50ms 时间预算增量展开动作树，dynamic 菜单（`childGetter != null`）只登记不展开。
- MCP 侧 `get_debug_action_categories` / `search_debug_actions`：带指数退避的轮询循环（150ms→1500ms 封顶，最多 200 次 / 120s）直到 `complete=true`，首次调用常返回 `incomplete` 结果或白等数秒。
- 痛点：调用慢、结果不确定、两处轮询样板代码维护成本高；`UEHttpHandler.RunOnMain` 注释还残留「首次构建完整树实测可超 60 秒、AppHangB1」的历史信息。

### 关键先例

rimbridge（RimBridgeServer）选择「直接建总表」，实测卡 5 分钟。该先例**否决**了「建表时把 dynamic 全量展开」的方案——dynamic 首次展开（调用 `childGetter`）是原版自己都不付的成本，集中支付有 AppHangB1 风险。

### 原版机制事实（本设计依赖的三个行为，均已核对源码并实测）

1. **建表时刻**：`Dialog_Debug.TrySetupNodeGraph()` → 每个 `DebugTabMenuDef` 调 `InitActions()` 扫 `GenTypes.AllTypes` 属性建静态节点表；返回类型为 `List<DebugActionNode>` 的 getter **只存委托不调用**（`DebugTabMenu_Actions.GenerateCacheForMethod`）。
2. **懒展开缓存**：`DebugActionNode.TrySetupChildren()` 首次触发 `childGetter` 后置 `childrenSetup`，之后原版 UI 与本模组共享缓存，毫秒级。
3. **路径行走零预读**：`Dialog_Debug.GetNode(path)` 逐段匹配子节点、边走 `TrySetupChildren()`——**单次调用即可穿过未展开的 dynamic 级直达目标，无任何先读表的前置依赖**。

实测锚点（2026-10-08，IN_GAME，直连 UE HTTP `/debugaction/info`）：

| 探针 | 结果 |
|---|---|
| `info?path=Spawn full thing stack\Gold`（裸形式） | PATH_NOT_FOUND |
| `info?path=Spawn full thing stack...\Gold`（仅补省略号） | PATH_NOT_FOUND |
| `info?path=Actions\Spawn full thing stack...\Gold`（规范路径） | **单次调用成功，直达 ToolMap 节点 Gold**，途中按需触发该级 getter |
| `search?query=spawn full thing stack` | 130ms 返回 `complete=true`（索引已就绪状态；冷建耗时以实现阶段基准为准） |

静态遍历实测（2026-10-08）：全树 2013 节点，dynamic 表 104 张（分布：Spawning 28、Pawns 22、General 19、Map 12、Incidents 5、其余零散；其中 3 张嵌套于 `Spawn thing with wipe mode...` 且无 category）。getter 方法体全部为原版实现、源码 MCP 可读 ⇒ 离线标注可行。

## 2. 目标 / 非目标

### 目标

- 静态索引在**原版建表那一刻一次建完**（选定的方案 A 时机），无预算、无增量栈。
- 查询工具（categories/search）**首次调用即 `complete=true`**，MCP 轮询循环整体删除。
- dynamic 条目只登记进索引（表里有这一行），其以下部分不展开、不显示。
- 执行/详情维持原版 `GetNode` **严格精确匹配**，单次调用、零预读。

### 非目标

- dynamic 全量急展（rimbridge 5 分钟先例否决）。
- dynamic 子节点进搜索索引（否决：搜索结果随使用变化会不确定）。
- 路径容错解析（裸路径补省略号、推断 `Actions\` 前缀等——已明确否决，维持原版精确匹配）。
- GABP 镜像工具（`rimworld.list_debug_action_*` 等走 RimBridgeServer，不在本次范围）。

## 3. 设计决策

| # | 决策 | 理由 |
|---|---|---|
| D1 | 建树时机 = Harmony 后缀挂 `Dialog_Debug.TrySetupNodeGraph()`；另在工具入口设 `EnsureIndex()` 幂等兜底（先置 `indexSeeded` 再构建，防后缀重入） | 选定的 A 方案；兜底覆盖 MCP 先于原版触发的顺序 |
| D2 | dynamic 节点只登记条目本身：索引存 `Dynamic=true`，**绝不调 `childGetter`**；list/search 结果给 `dynamicMenu:true` + 展示用 `template` 字段（格式 = 规范path + "\" + 占位名，占位名四级降级见第 4 节「template 字段与离线标注」） | 表里可见、可搜到入口；以下部分用的时候再显示 |
| D3 | dynamic 子节点**永不补录**进索引（即使已被下钻展开过） | 搜索结果确定性优先（选定的 A） |
| D4 | 执行/详情不经索引，维持 `Dialog_Debug.GetNode` 严格精确匹配；已知规范路径即单次直达，未知先用 search/list 取规范路径 | 已否决容错解析；零预读已实测证实 |
| D5 | `ListActions` 对 `parentPath` 仅做**末尾分隔符剥离**（反斜杠与 `/` 均剥），标签匹配仍旧严格 | 「输入表路径 → 返回子表」时防调用方追尾分隔符；不触及标签匹配语义 |
| D6 | 索引与 `rootNode` 同生命周期：原版 `rootNode` 进程内只建一次（自身不重建），无失效问题 | 无需失效/重建逻辑 |

## 4. 架构与数据流

### C#（Source/UELoader/UEDebugActions.cs）

- 状态：`indexEntries`、`indexSeeded` 保留；删除 `indexExpandStack`、`ExpandBudgetMs`、`indexBuildSw`、`indexStatsLogged`。
- `EnsureIndex(): bool`——幂等；`GetRoot()` 后一次性 DFS 遍历已建好的静态树（static 节点直接走 `children`；dynamic 节点登记后**不下钻**）；`root == null` 返回 false。
- Harmony 后缀：`TrySetupNodeGraph` Postfix → `EnsureIndex()`，补丁只应用一次（复用类内既有 `HarmonyInstance`）。
- `GetCategories` / `Search`：入口 `EnsureIndex()`，失败返回 `Err("动作树初始化失败", "TREE_INIT_FAILED")`；直接扫 `indexEntries`；`complete` 恒 `true`，`cursor` 字段保留（响应形状不破坏）。
- `ListActions`：D5 尾分隔符剥离；条目 `hasChildren` 改为 `child.childGetter != null || children.Count > 0`（未触发的 dynamic 此前误报 false）；dynamic 条目补 `dynamicMenu`/`template`。
- `GetDetail` / `Execute`：逻辑不变（`GetNode` 严格路径）。


### template 字段与离线标注

`template` 只出现在 dynamic 条目上，格式 = `规范path + "\" + 占位名`。占位名四级降级：

| 级 | 条件 | 占位名示例 | 调用方含义 |
|---|---|---|---|
| 1 | 标注：子项为 `DefDatabase<T>` 枚举 | `<thingDef>` / `<pawnKindDef>` / `<hediffDef>` … | 子名可预知，**可直接拼路径** `path\<def标签>` |
| 2 | 标注：子项为固定且已知的少量项 | 直接列全：`<Adulthood,Childhood>` | 子名全部已知，**可直接拼路径** |
| 3 | 标注：子项为运行时实例 | `<currentPawn>` | 子名随局变化，**必须先 list** |
| 4 | 未标注 / 判不出 | `<child>` | 有子表，list 看 |

- **标注来源**：源码 MCP / 反编译读 `childGetter.Method` 方法体，离线生成映射；vanilla 104 张表已实证全部可读（2026-10-08 实读 12 张样本，全为 def 类；live 实测 `Spawn full thing stack` 子项即 thingDef 标签 `Gold`）。
- **存储键**：`childGetter.Method` 的 `DeclaringType.FullName + "." + MethodName`，不用 path——label 跨版本变动不影响命中；方法改名则未命中、走 `<child>` 兜底。共享 helper 方法（如 `TryPlaceOptionsForStackCount` 被 5 个表复用）天然得到同一占位名。
- **兜底**：未收录（模组新增表、版本变动）→ `<child>`，永不错报。
- 子名形态提醒：def 类子名有 `defName`（`Gold`）与 `LabelCap`（Add Hediff 用 LabelCap）两种；占位名里的类型名让调用方可用源码 MCP 核确切标签。

实读样本（方法体证据 → 占位名）：

| 动态表 | 方法体 | 占位名 |
|---|---|---|
| `Actions\Spawn full thing stack...` | `TryPlaceOptionsForStackCount` → `DefDatabase<ThingDef>` | `<thingDef>` |
| `Actions\Spawn Pawn...` | `SpawnPawn()` → `DefDatabase<PawnKindDef>` | `<pawnKindDef>` |
| `Actions\Add Hediff...` | `AddHediff()` → `DefDatabase<HediffDef>` | `<hediffDef>` |
| `Actions\Do incident` | `GetIncidentDebugAction` → `DefDatabase<IncidentDef>` | `<incidentDef>` |
| `Actions\Spawn thing set...` | `SpawnThingSet()` → `DefDatabase<ThingSetMakerDef>` | `<thingSetMakerDef>` |
| `Actions\Trigger effecter...` | `DefDatabase<EffecterDef>`（category 却挂 Spawning——分类不可作判据的实证） | `<effecterDef>` |
| `Actions\T: Abandon Thing...` | `AbandonThing()` → `DefDatabase<ThingDef>` | `<thingDef>` |
| `Actions\Equip primary (selected)...` | `DefDatabase<ThingDef>`（`(selected)` 是执行目标非子项） | `<thingDef>` |
| `Actions\Set Backstory...` | 固定两项 Adulthood / Childhood 分组（诚实列全） | `<Adulthood,Childhood>` |
| 运行时实例类 | vanilla 104 张抽样未见，属模组新增表场景 | `<currentPawn>` |
| 未标注 | — | `<child>` |

### MCP（MCP/index.js）

- `get_debug_action_categories`（:4902-4932）与 `search_debug_actions`（:4934-4971）删除轮询循环（`MAX_POLLS`/`deadline`/退避 sleep），改为单次调用取结果；响应保留 `complete`/`incomplete`/`truncated` 字段（值恒为 `complete:true`、`incomplete:false`）。

### 注释同步

- `UEDebugActions.cs:35-40` 混合惰性索引注释 → 改写为「建表时急建静态索引、dynamic 只登记」。
- `UEHttpHandler.cs:617-621`「首次可超 60 秒、后台继续建树」→ 改写为现状说明；**60s 主线程超时本身保留**作保险。
- `index.js:4903`、`:4940` 轮询注释 → 删除。

## 5. 删除清单

| 位置 | 删除项 |
|---|---|
| UEDebugActions.cs | `ExpandBudgeted`、`indexExpandStack`、`ExpandBudgetMs`、`indexBuildSw`、`indexStatsLogged`、`IndexComplete`（改为常量语义）、惰性索引注释块（:35-40） |
| index.js | 两处 `MAX_POLLS`/`deadline`/`sleep`/`do…while` 轮询（:4906-4924、:4942-4960）及配套注释 |
| UEHttpHandler.cs | 仅改写 :617-621 注释（超时值不动） |

## 6. 错误处理

- `EnsureIndex` 失败（`root == null`）→ categories/search 返回 `TREE_INIT_FAILED`。**行为变化**：此前 seed 失败会静默返回空结果 + `complete=true`，改为显式报错。
- 裸路径 / 错误层级 → `PATH_NOT_FOUND`（严格语义，预期行为，不加容错）。
- dynamic getter 执行异常 → 沿既有链路冒泡为 `MAIN_THREAD_ERROR`（与现状一致，不改）。
- `Execute` 对菜单节点报 `NOT_ACTION`、对无执行体报 `NO_ACTION_BODY` 等现有错误码全部不变。

## 7. 验证

1. 冷启动后 `get_debug_action_categories` / `search_debug_actions` 首次调用即返回且 `complete=true`；MCP 日志中该 tools/call 单次完成、无 sleep 循环。
2. 回归锚点（已实测通过）：`info?path=Actions\Spawn full thing stack...\Gold` → 成功、`actionType=ToolMap`。
3. 严格守护：`info?path=Spawn full thing stack\Gold` → `PATH_NOT_FOUND`（防未来误加容错）。
4. `list?parentPath=Actions\Spawn full thing stack...` 及其尾 `/`、尾反斜杠变体 → 均返回子表；dynamic 条目带 `dynamicMenu`/`template`；`hasChildren` 对未触发 dynamic 为 true。
5. `execute` 规范路径的 Gold（ToolMap，带坐标）→ 执行成功。
6. 搜索不到 dynamic 内部叶子（未下钻时搜 `Gold` 不返回该动态子节点）——D3 生效证据。
7. 代码检核：`grep MAX_POLLS`、`grep ExpandBudgeted` 零命中。
8. template 抽查：`Spawn full thing stack...` 条目 → `<thingDef>`；`Set Backstory...` → `<Adulthood,Childhood>`；临时移除任一标注 → 该条目回退 `<child>`。

## 8. 文件清单

- `Source/UELoader/UEDebugActions.cs`（主改；内含 template 标注静态字典）
- `Source/UELoader/UEHttpHandler.cs`（仅注释）
- `MCP/index.js`（删两处轮询）

不改动：GABP 镜像工具、`toolConfig.json`、菜单归属映射、`MapStructure` 相关实现。

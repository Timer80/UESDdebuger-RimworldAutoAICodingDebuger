# TRIAGE 报告 · 条目 [27] 命中行不带声明类型 —— 「同名方法跨类型」是现成的挂错源

**裁决：值得修**
**归并：并入 `plans/2026-10-01-11-search-source-false-negative-convention.md` 的任务 1（新建 `docs/mcp-source-lookup-conventions.md`），作为「规矩 2 声明行正则」的第二次修订 + 一条新规矩 4；不单独立计划。**

---

## 2. 一句话结论

症状 100% 复现（4 条命中、只有 `路径:行号`、2 条声明 2 条调用点跨 2 个类型），但**计划 11 的声明行正则模板恰好对「方法」完全失效（实测 0 命中）**，所以 [27] **不是「已被现有计划覆盖」而是「计划 11 差一个字符」** —— 把模板末尾 `\s*(=>|;|=)` 改成 `\s*(=>|;|=|\()` 就实测一次给出 2 条带修饰符与完整签名的跨类型声明。

## 3. 核实过程

### 3.1 症状复现（逐字实测）

`mcp__rimworld-source-online__search_source(query="TryRecoverFromUnwalkablePosition", case_sensitive=true, file_pattern="*.cs")`：

```
./Source/Verse/AI/Pawn_PathFollower.cs
164:			if ((!PawnCanOccupy(pawn.Position) && !TryRecoverFromUnwalkablePosition()) || (moving && curPath != null && destination == dest && this.peMode == peMode))
529:	public bool TryRecoverFromUnwalkablePosition(bool error = true)

./Source/Verse/GenSpawn.cs
188:						pawn.pather.TryRecoverFromUnwalkablePosition(error: false);

./Source/RimWorld/Planet/Caravan_PathFollower.cs
145:		if (!IsPassable(caravan.Tile) && !TryRecoverFromUnwalkablePosition())
238:	private bool TryRecoverFromUnwalkablePosition()
```

4 条命中里 `:164` / `:188` / `:145` 是调用点、`:529` / `:238` 是声明，且声明分属 `Verse.AI.Pawn_PathFollower`（public，带默认参）与 `RimWorld.Planet.Caravan_PathFollower`（private，无参）两个类型 —— **板上 [27] 的表格逐行属实**。板上条目正文在 `docs/mcp-吐槽板.md:118-145`（1-based，行号已用 `[System.IO.File]::ReadAllLines` 复核），索引行在 `:44`。

**搜索范围声明**：本条只对 `rimworld-source-online` MCP（即线上 RimWorld 反编译源码镜像）的索引做检索；未检索 `Mods\` 下任何本地模组源码。

### 3.2 关键实测：计划 11 的声明行正则**对方法 0 命中**

计划 11 `plans/2026-10-01-11-search-source-false-negative-convention.md:519`（1-based，`ReadAllLines` 复核）给出的模板原文：

```
^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)<成员名>\s*(=>|;|=)
```

代入 `TryRecoverFromUnwalkablePosition` 实测（`regex:true` + `case_sensitive:true` + `file_pattern:"*.cs"`）：

> **No results found. Try adjusting your search query or file pattern.**

**根因**：模板末尾的 `(=>|;|=)` 三选一只覆盖**属性**（`=>`）与**字段**（`;`/`=`），而**方法声明以 `)` 收尾**。计划 11 的两条正例（`AllPawnsSpawned` 是表达式属性 `=>`、`destination` 是字段 `;`）恰好都在前两类里，**模板从未被方法验证过**。⇒ 这是一条**规范自身的缺陷**，不是「规范已覆盖 [27]」。

### 3.3 修正后实测：一次调用同时给出两个跨类型的声明

把末尾改成 `\s*(=>|;|=|\()`（只加一个 `\(` 分支），实测：

```
search_source{ query:"^\s*(public|protected|internal|private)\s+([\w<>\.]+\s+)+TryRecoverFromUnwalkablePosition\s*(=>|;|=|\()",
               regex:true, case_sensitive:true, file_pattern:"*.cs" }
→ ./Source/Verse/AI/Pawn_PathFollower.cs
      529:	public bool TryRecoverFromUnwalkablePosition(bool error = true)
   ./Source/RimWorld/Planet/Caravan_PathFollower.cs
      238:	private bool TryRecoverFromUnwalkablePosition()
```

**恰好 2 条**，两个类型各 1 条，修饰符（`public` / `private`）、返回类型、完整形参表、`= true` 默认值全在，**0 条调用点**（调用点的行首不是访问修饰符，被 `^\s*(public|…)` 锚点天然排除）。⇒ **板上的诉求已被一条实测成立的姿势完全满足。**

另测宽松版 `^\s*(public|protected|internal|private|static|sealed|override|virtual|abstract|new|extern|unsafe)[A-Za-z0-9_<>,\[\]\. ]*\bTryRecoverFromUnwalkablePosition\s*\(` 同样给出这 2 条，可作为「返回类型里带数组/泛型逗号」的加固备选。

## 4. 影响与严重度复评

| 维度 | 结论 |
|---|---|
| 真实性 | ✅ 逐字复现 |
| 严重度 | **🟡 低 → 复评维持 🟡**，但**理由变了**：原判据是「必须逐个 `read_file` 打开才能拼出答案」（体验糙）；修正后应改判为「**规范模板对方法无效 ⇒ 按现有约定执行会得到 0 命中并被当成「不存在」**」—— 这是 COMMON.md §5.1/§5.3 家族里最危险的**零命中假阴性**，比「多花两次 read」严重一档 |
| 是否设计使然 | 否。`search_source` 是本项目接入的外部服务，但「约定该怎么写」是**本项目自己的规范文档**（计划 11 任务 1 新建的 `docs/mcp-source-lookup-conventions.md`），有完全的自有编辑权 |
| 可修性 | 最高：改一段 Markdown 里的模板字符串 + 补两条实测记录，零代码 |

**与 [21] 的叠加**（板上已点出）仍然成立：修正后的正则也**必须配 `file_pattern`**，否则 400 条上限会先把它截掉 —— 这一点计划 11 §3 已经覆盖，本条不重复要求。

## 5. 责任归属

**本项目自有文档**，不涉及任何第三方代码。落点只有一个：`docs/issue-triage/plans/2026-10-01-11-…` 任务 1 产出的 `docs/mcp-source-lookup-conventions.md`。**不需要动 `search_source` 服务本身**（板上建议 ④ 的「给命中行加 `<DeclaringType>` 前缀」不采纳 —— 见 §6）。

## 6. 修的话怎么修

并入计划 11 任务 1，追加 3 条编辑（不改任务数、不改文件清单）：

1. **规矩 2 的模板加 `\(` 分支**，并在紧邻处补一句实测反例：
   > ⚠️ 方法声明以 `)` 收尾，模板若只有 `(=>|;|=)` 会**对方法 0 命中**。实测 `TryRecoverFromUnwalkablePosition` → `No results found`。字段/属性不受影响，**方法必须带 `\(` 分支**。

2. **补一条新「规矩 4 —— 跨类型同名方法：用修正后的声明行正则一次问全」**，把 §3.3 的两行实测原样贴进去（含 `529:` / `238:` 两行输出），并写明它同时解决「声明 vs 调用点」与「跨类型同名」两件事。**这是 [27] 唯一的交付物。**

3. **把板上建议 ④ 的第二方案（新增「`{declaringType, name, params, access}` 结构化小工具」）显式标为「本轮不做」并给出理由**：声明行正则已经一次给出修饰符 + 返回类型 + 完整形参表（实测两行俱在），结构化新工具属 YAGNI，且计划 11 任务 1 明确「不改 `search_source` 的返回契约」。

**同时驳回板上建议 ④ 的第一方案**（在命中行前加 `⟨DeclaringType⟩` 前缀）：要改 `search_source` 的返回契约，属于**外部服务**改动，计划 11 §0 已把它排除（`plans/…-11-…md:26`）。

## 7. 与既有计划的关系

| 计划 | 关系 |
|---|---|
| `plans/2026-10-01-11-search-source-false-negative-convention.md` | **归并到这里。** 它已覆盖 [27] 的**目标**（存在性/可见性判定走声明行正则），但**模板对方法无效** ⇒ 归并动作 = 任务 1 的产物加一次模板修订 + 新增规矩 4。**不单独立计划**（否则两个 PLAN 子代理会改同一份规范文件）。 |
| `plans/2026-10-01-10-api-member-visibility-tools.md` | 无关。计划 10 改的是 `McpRimDebug/DebugSession.Eval.cs` 的 `find_methods`（C# / Cecil 侧），本条是 `rimworld-source-online` MCP 的 `search_source`（文本检索侧），**两条链路无交集**。 |
| 板上 [13] / [21] | [27] 叠在它们上面，但本条**不重复** [21] 的 `file_pattern` 约定（计划 11 §1 规矩 0 已覆盖）。 |

**给主代理的顺序提醒**：计划 11 的任务 1 若已经开工，务必先打上这一条再执行 —— 否则 `docs/mcp-source-lookup-conventions.md` 会带着一个对方法 0 命中的模板定稿，而它是本项目唯一的源码检索规范文档，会持续向下游传染。
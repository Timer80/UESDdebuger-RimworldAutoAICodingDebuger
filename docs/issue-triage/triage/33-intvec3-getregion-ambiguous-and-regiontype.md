# [33] 裁决报告 —— `add_using_directive("Verse")` 后 `IntVec3.GetRegion(map)` 变 CS0121 二义 / `RegionType` 无 `Region` / `Map.regionManager`

> 角色：TRIAGE。输入：`docs/mcp-吐槽板.md` 的 `## [33]`（2026-10-02，TEXT20 W6 · P18 实测代理）。
> 本报告只做裁决与事实复核，**不含实现计划**。全文行号均用 `[System.IO.File]::ReadAllLines(...)` 复核（见 §6）。

**裁决：值得修**（但**不是**"去修 CS0121"，而是"改一行会主动误导的 guidance + 把两条被写错的事实纠正进文档"）

---

## 0. 裁决摘要

| 项 | 结论 |
|---|---|
| 裁决 | **值得修** |
| 严重度复评 | **🟡 低**（维持索引 `:50` 的值；**正文 `:258` 自评"🟡 中"与索引不一致**，建议统一按 🟡，理由见 §5.1） |
| 责任归属 | **本项目（MCP 文案层 + 文档层）**；CS0121 根因**未定**，若确认为重复程序集引用则转 UE 侧（`Source/UE-4.9.0-official/.../CSConsole/ScriptEvaluator.cs`），成本高一个量级 |
| 归并锚点 | **与 [35] 合并成一份新计划**（编号 33+35）。共用落点 `MCP/index.js:4536-4537` 的 `r.guidance`；**禁止碰** `MCP/index.js:2937`（计划 02 任务 4 整行重写）与 `:2072/:2078`（计划 12 任务 1） |
| 板上需要更正的事实 | **2 处**（`Map.regionManager` 根本不存在；CS0121 的"程序集被加载两次"只能算假设，见 §2.1） |

---

## 1. 条目原文与定位

`docs/mcp-吐槽板.md`：

- 正文段 `## [33]`：**第 243-271 行**（标题在 243，⑤状态在 271）。
- 索引行：**:50**。

原文三个子现象（逐字要点）：

1. `:247-249` —— 按前言加 `using Verse;` 后，`w.Position.GetRegion(map)` 报
   `CS0121: The call is ambiguous between 'Verse.GridsUtility.GetRegion(this Verse.IntVec3, Verse.Map, Verse.RegionType)' and 'Verse.GridsUtility.GetRegion(this Verse.IntVec3, Verse.Map, Verse.RegionType)'`，
   **两个候选逐字相同**。
2. `:250-253` —— 改成显式三参 `GetRegion(map, RegionType.Region)` 报
   `CS0117: 'Verse.RegionType' does not contain a definition for 'Region'`；实际枚举值是
   `None, ImpassableFreeAirExchange, Set_Impassable, Normal, Portal, Fence, Set_Passable, Set_All` ⇒ 要的是 `RegionType.Normal`。
3. `:254` —— 再退一步用 `map.regionManager.RegionAt(...)` 报
   `CS1061: 'Verse.Map' does not contain a definition for 'regionManager'`，括号里写「1.6 里是 **internal 字段**，不是 property，`GetProperty` 也返 `null`」。
4. `:259-265` —— 已验证绕过：反射 `typeof(Verse.IntVec3).Assembly.GetType("Verse.GridsUtility")` → `GetMethod("GetRegion", {IntVec3, Map, RegionType})` → `Invoke(null, {cell, map, Verse.RegionType.Normal})`。
5. `:271` —— 状态：**已绕过**。

---

## 2. 逐条事实复核

复核方式：对 `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`
（decompilerserver 上下文 `rw16`，MVID `61e4173561894da49d210260257b5097`，`typeCount=16158`、`methodCount=76770`）
做**只读**成员查询。**未启动游戏、未调用 `execute_csharp_code`。**

### 2.1 子现象 ①（CS0121 二义）—— 现象可信，根因**未定**

| 复核动作 | 结果 |
|---|---|
| `search_members(query="GetRegion")`（全程序集） | **8 条命中**；其中**只有一条**是扩展方法形态的 `GetRegion(this IntVec3, …)`：`Verse.GridsUtility.GetRegion`（`06001B5D`）。另一条 `Verse.RegionAndRoomQuery.GetRegion`（`060020D9`）是**实例方法**，不是 `IntVec3` 的扩展 |
| `get_members_of_type("Verse.GridsUtility")` | `GetRegion` **只有一个重载**：`Region GetRegion(IntVec3 loc, Map map, RegionType allowedRegionTypes)`（memberId `…:06001B5D:M`） |

⇒ **两个逐字相同的候选不可能来自"重载歧义"**——全程序集只有这一支 `GetRegion(this IntVec3, Map, RegionType)`。
所以板上"看着像程序集被加载两次"的直觉是**目前唯一说得通的解释**，但它**仍是假设**：

- 支持它的静态证据：`ScriptEvaluator.cs:81-98`（`ImportAppdomainAssemblies`）把 AppDomain 里**每一个**程序集都喂给 mcs，
  `ScriptEvaluator.cs:21-27` 又在构造时 `ImportAppdomainAssemblies()` **并**订阅 `AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad`（`:35-43` 再喂一次），
  而 `ScriptEvaluator.Reference(Assembly)`（`:45-62`）**只按简单名做黑名单过滤**（`ConfigManager.cs:131` 该黑名单默认值是**空字符串**），
  **没有任何"已引用过"的去重键**。若运行时存在两个同名程序集对象，`using Verse;` 的命名空间导入就会给出两份一模一样的候选。
- **未核实**：运行时是否真的有同名程序集对象。核实它需要 `execute_csharp_code` 跑
  `AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name + "|" + a.FullName + "|" + a.Location)`——
  **本流水线明令禁止调用 `execute_csharp_code`，故此项标为「待实测」，不在本报告里下结论。**
- 顺带一个**结构事实**（可静态确证）：`ConsoleController.AddUsing`（`ConsoleController.cs:141-148`）的参数名叫 `assemblyName`，
  实际拼的是 `using {namespace};`；且 `DefaultUsing`（`:39-53`）里**不含 `Verse`/`RimWorld`**——这就是板上"前言教你 `add_using_directive("Verse")`"的背景事实。

### 2.2 子现象 ②（`RegionType` 无 `Region`）—— ✅ **板上完全正确**

`get_members_of_type("Verse.RegionType")` → **共 10 个成员**（`totalEstimate=10`，`hasMore:false`）：
`.ctor`、`value__`、以及 8 个枚举值
`Fence` / `ImpassableFreeAirExchange` / `None` / **`Normal`** / `Portal` / `Set_All` / `Set_Impassable` / `Set_Passable`。

⇒ **没有 `Region` 成员**（`CS0117` 属实），**要的是 `RegionType.Normal`**（板上正确）。
这与 `search_members` 的枚举顺序无关，成员表是全量（`hasMore:false`）。

### 2.3 子现象 ③（`Map.regionManager`）—— ⚠️ **板上事实错误**

| 复核动作 | 结果 |
|---|---|
| `search_members(query="regionManager")`（**全程序集**，无声明类型过滤） | **0 命中** |
| `search_members(query="regionManager", declaringTypeFilter="Verse.Map")` | **0 命中** |
| `search_members(query="Region", declaringTypeFilter="Verse.Map")` | 5 条**字段**：`regionGrid` / `regionMaker` / `regionAndRoomUpdater` / `regionLinkDatabase` / `regionDirtyer` |
| `find_base_types("Verse.Map")` | `bases: []`（**无基类**，只有 5 个接口） |

⇒ **`Verse.Map` 上根本没有叫 `regionManager` 的成员**（连非 public 的都没有），所以
`CS1061: 'Verse.Map' does not contain a definition for 'regionManager'` **属实**，
但板上括号里那句「1.6 里是 **internal 字段**，不是 property，`GetProperty` 也返 `null`」**是错的**——
正确形状是：字段名是 **`regionGrid`**（小写 g），不是 `regionManager`。
**这条错误如果原样写进文档，会把下一个人送去查一个不存在的字段。**

> ⚠️ **本报告没有核实 `Map.regionGrid` 的可见性**（`search_members` 的结果不返回 `accessibility`），
> 因此**不写**"它是 internal / public"。要写进文档必须先用 `get_member_signature("…:04001937:F")` 或 `get_member_details` 取到可见性再落笔。

### 2.4 板上给的绕过（`:259-265`）—— ✅ 方向正确，但**不是唯一解**

`Verse.GridsUtility.GetRegion(IntVec3, Map, RegionType)` 是 **public static**（`get_members_of_type` 返回
`accessibility: Public`、`isStatic: true`），所以**根本不需要反射**——全限定名直调即可：

```csharp
Verse.Region GetRegionAtCell(Verse.IntVec3 cell, Verse.Map map)
    => Verse.GridsUtility.GetRegion(cell, map, Verse.RegionType.Normal);
```

（无 using 的控制台里这是合法写法；`Verse.Region` / `Verse.RegionType.Normal` 全部是 public 枚举值。）
⇒ **反射版是"因为二义才绕"的产物**；一旦不用 `using Verse;`（全限定直调），CS0121 根本不出现。
**这一点直接决定 §4 的改法方向：不需要修 mcs，只需要告诉调用方"别用 `using` 去解二义"。**

---

## 3. 三个子现象的定性

| 子现象 | 定性 | 归属 | 成本 |
|---|---|---|---|
| ②`RegionType.Region` → `Normal` | **纯文档缺失**（板上给的正确答案正确，只是没进任何项目文档） | 本项目文档层 | ≈0（写一行表） |
| ③`Map.regionManager` 不存在 | **板上事实错误** + 纯文档缺失 | 本项目文档层（须先纠错再落笔） | ≈0 |
| ①CS0121 二义 | **真实但根因未定**；**有零成本规避**（不 `using`、走全限定名） | 规避=本项目文档层；根因=UE 侧 `ScriptEvaluator.cs`（高成本） | 规避 ≈0；根因 高 |

### 3.1 顺手挖出来的一个**独立**缺口：`add_using_directive` 会**静默假成功**（本条原文没提）

静态可确证（不需要游戏）：

- `ConsoleController.cs:141-148`：`AddUsing` 无论编译成败，都会执行 `usingDirectives.Add(assemblyName)`；
- `ConsoleController.cs:202-212`：`Evaluate` 在编译失败时 `throw new FormatException(...)`，
  但**就地被自己的 `catch (FormatException fex)` 吞掉**（`supressLog=true` 时连日志都不打），**不外抛**；
- `MCP/index.js:4555-4564`：`case 'add_using_directive'` 把响应**原样透传**，不做任何"这条 using 真的生效了吗"的校验。

⇒ **传一个不存在的命名空间也会回 `success=true`。** 这与 [01]「工具报成功但实际没执行」同族，
且它正是板上 `:267` 建议"① `add_using_directive` 加 `Verse` 前后各试一次"这种**试错式**用法的成因。
**建议把它列为 33+35 合并计划里的一条独立任务**（成本低：让 `AddUsing` 把 `ErrorsCount>0` 透出，或让 MCP 侧在响应里回显"当前 using 集合"）。

---

## 4. 与已有计划的关系

| 已有计划 | 是否覆盖 [33] | 依据 |
|---|---|---|
| 计划 02 `2026-10-01-02-execute-csharp-code-output-capture.md` | **否**（只覆盖 `void` 宿主与 `output` 捕获） | 任务 4 只改 `MCP/index.js:2937` 的 `TOOL_USAGE_NOTES`（`:1030-1035`），该行现有文案只讲"无 using ⇒ 裸类型名 CS0103"，**没有** CS0121/CS0117/CS1061 的任何字样 |
| 计划 10 `2026-10-01-10-api-member-visibility-tools.md` | **部分重叠，不覆盖** | 任务 3（`:658`）是"文档补齐**成员可见性该去哪问**"，治的是 §2.3 这类"可见性结论相反"的病；它**不涉及** `add_using_directive` / CS0121 / 枚举成员名 |
| 计划 12 `2026-10-01-12-mod-presence-assembly-visibility.md` | **否** | 任务 1（`:199`）只改 `MCP/index.js:2072/:2078` 讲 `CS0127` 与表达式模式，与 [33] 无关 |
| Roslyn 计划 `2026-09-27-ue-csharp-console-roslyn-upgrade.md` | **否** | 任务 12 步骤 1（`:2053-2062`）重写 `:2937`，讲的是"默认 using + 模组命名空间不加 using"；**与本条无交集** |

**与 [35] 的关系：同族，且共用同一个落点。** [33] 的三个错误码 `CS0121/CS0117/CS1061` 与
[35] 的 `CS0234/CS1503/CS1061/CS0411/CS0841` 全部经由 **`MCP/index.js:4536-4537`** 这一处返回给调用方：

```js
// MCP/index.js:4536-4537
if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || (typeof r.error === 'string' && r.error.includes('CS0')))) {
  r.guidance = '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';
}
```

**这一行今天对 [33]/[35] 的每一个错误码都给出错误建议**（详见 [35] 报告 §4.1 的完整枚举）。
⇒ 合并成一份计划、并把共用落点定在 `:4537`，是本条最强的技术理由。

---

## 5. 严重度复评 / 责任归属 / 归并锚点

### 5.1 严重度：🟡 低（维持索引 `:50`；正文的"🟡 中"建议改回 🟡）

| 尺子（AGENT-BRIEF §5.2） | 判定 |
|---|---|
| 会不会让自动化得出**事实相反**的结论 | **不会** —— 三个现象全部是**编译期硬失败**，每次都红，不存在假绿 |
| 是不是每次都要重踩 / 静默烧 token | **否**（本条只 1 条探针写法；[35] 才是"每轮 5 次往返"） |
| 是不是体验略糙 | **是** |
| 有没有已验证绕过 | **有**，且板上 `:259-265` 走通；本报告进一步把它**降到零复杂度**（全限定直调，见 §2.4） |

⇒ 保持 🟡。**但**：`MCP/index.js:4537` 的 guidance 主动误导这一项，单独看也是 🟡 级；
它之所以值得动，**不是因为 [33] 严重，而是因为它同时误伤 [33] + [35]（合计 8 个错误码）与 [34] 的 `CS0127`**——
这个"合计效应"是 33+35 合并计划应当按 🟠 排期的理由，**不是 [33] 单条的理由**。本报告不为抬高单条严重度而虚报。

### 5.2 责任归属

| 层 | 归属 | 说明 |
|---|---|---|
| 文档（对照表 / 正确答案） | **本项目** | 纯新增，无风险 |
| `MCP/index.js:4536-4537` guidance | **本项目 MCP 层** | 唯一需要"改一行逻辑"的地方 |
| `ConsoleController.AddUsing` 假成功 | **本项目（UE 源码树在库内）** | `Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ConsoleController.cs`；改它要重编 `UnityExplorer.STANDALONE.Mono.dll`（`Source/Directory.Build.props:16` 的 `$(UEProjectDir)` = `Assemblies/`） |
| CS0121 根因（若为重复引用） | **UE 侧 `ScriptEvaluator.cs`** | 同样在库内，但要动"引用所有 AppDomain 程序集"这条核心逻辑；**且与 Roslyn 计划任务 8 抢同一片 ConsoleController 区域** ⇒ 高冲突风险，**本轮不建议** |

### 5.3 归并锚点

**建议：[33] 与 [35] 合并为一份计划（建议文件名 `plans/2026-10-02-33-35-compile-error-guidance-and-member-lookup.md`），不单立。**

理由三条：

1. **落点同一处**：两者的全部改善都经由 `MCP/index.js:4537` 的 `r.guidance`；
2. **文件同一族**：两者的"正确答案表"都写进计划 10 任务 3 正在改的 `README.md` / `AI-DEPLOYMENT.md`（`2026-10-01-10-…md:132-134`），拆成两份计划必然在同一批文件上打架；
3. **不与计划 02 重合**：计划 02 任务 4 的领地是 `MCP/index.js:2937`（`TOOL_USAGE_NOTES`），
   合并计划**一个字都不碰那一行**，也不碰 `:2072/:2078`（计划 12 任务 1）⇒ 三方零冲突。

**给 PLAN 的三条硬约束（写进计划正文）：**

- ⛔ 不得改 `MCP/index.js:2937`（计划 02 任务 4 整行重写，已在 `2026-10-01-02-…:1028` / `:1061` 登记）。
- ⛔ 不得改 `MCP/index.js:2072` / `:2078`（计划 12 任务 1 的领地，见 `2026-10-01-12-…:60` / `:101`）。
- ✅ 唯一允许改的 MCP 行是 `MCP/index.js:4536-4537`，且必须是**按错误码分流**而不是追加第二段固定文案。

---

## 6. 检索范围声明与本报告的自我限制

**检索范围（凡"查不到"必须随附）：**

| 结论 | 范围 |
|---|---|
| `regionManager` 不存在 | decompilerserver 上下文 `rw16` = `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（**仅 vanilla 本体，MVID `61e4173561894da49d210260257b5097`**）。**未检索任何模组程序集**（如 `TEXT20.dll`）—— 模组里若有同名 `Map` 扩展则不在本报告结论内 |
| `RegionType` 成员表 | 同上（`hasMore:false`，全量） |
| `CellRect.InBounds` 等 [35] 用到的成员 | 同上 |
| UE 控制台源码 | `Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/`（`ScriptEvaluator.cs` 100 行、`ConsoleController.cs` 747 行，**全文读完**） |
| 计划 02 / 10 / 12 / Roslyn 计划 | `docs/issue-triage/plans/` 全目录 + `docs/superpowers/plans/`，**未逐字读完计划 10（817 行）与计划 12**，只读了它们的小节目录与被 grep 命中的行 ⇒ 覆盖判定基于**命中证据**而非全文通读 |
| 板子行号 | `Select-String -Pattern '^## \[3[345]\]'` 定位 + `[System.IO.File]::ReadAllLines` 复核；全文件 1459 行 |

**本报告明确没有做（因而以下都是未知的）：**

- ❌ 未启动游戏、未调用 `execute_csharp_code`（AGENT-BRIEF §1.2/§1.3）。
- ❌ **未核实 CS0121 的运行时根因**（是否真有同名程序集被喂两次）——§2.1 只给了"唯一说得通的假设"+可静态确证的机制缺口。
- ❌ **未核实 `Verse.Map.regionGrid` 的可见性**（`search_members` 不返回 `accessibility`）——§2.3 因此拒绝落笔。
- ❌ 未核实 `add_using_directive` 假成功在**运行时**是否真的回 `success=true`（静态链路完整，但没有实跑响应体）。
- ❌ 未用 `search_string_literals`（字面量匹配，带 `|` 的模式无效），本报告所有 MCP 侧检索均为本地文件 `Select-String` / decompilerserver 结构化查询，模式原文已在正文给出。

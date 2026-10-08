# TRIAGE 报告 · 条目 [28] 装了 Reachability 补丁的模组里 `pawn.CanReach(...)` 已不是原版语义且零信号

**裁决：值得修**
**归并：不并入任何既有计划（计划 10/11/22 均无交集）。建议单立一个「新增工具」计划，锚点＝`Source/UELoader/UEHttpHandler.cs` + `MCP/index.js`。**

---

## 2. 一句话结论

现象 100% 属实且比板上写的更广（TEXT20 一共 patch 了 **4 个** vanilla 可达性方法，不只 `ReachabilityUtility.CanReach`），而 `Harmony.GetPatchedMethods()` 在本项目**确实可用**（本仓库游戏侧代码早就在用，0Harmony 2.3.3）—— 但**板上照抄的那个写法会返回 0**，因为它是 owner 作用域的**实例**方法；正确入口是静态 `GetAllPatchedMethods()`。**90% 的实现已在本仓库内**，缺的只是「暴露成 MCP 工具」这一层。

## 3. 核实过程

### 3.1 补丁本体逐行核实（板上引用全部属实）

`Mods\TEXT20\source\PathPlan\FlightAStar\ReachabilityPatches.cs`（全文 505 行，1-based 行号已用 `[System.IO.File]::ReadAllLines` 复核）：

| 行 | 内容 |
|---|---|
| 17-19 | `[HarmonyPatch(typeof(ReachabilityUtility), "CanReach", new Type[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(Danger), typeof(bool), typeof(bool), typeof(TraverseMode) })]` |
| 20 | `public static class Patch_ReachabilityUtility_CanReach_Flight` |
| 22 | `static bool Prefix(Pawn pawn, LocalTargetInfo dest, PathEndMode peMode, ref bool __result)` |
| **27** | `__result = FlightReachGate.CanReachFor(pawn, pawn.Position, dest.Cell, peMode, FlightLayer.Ground);` |
| **28** | `return false;` |

⇒ 板上 `:17-32` 的引用逐字属实。

原版侧：`Source/Verse/ReachabilityUtility.cs:8`（`search_source` + 声明行正则实测行号）= `public static bool CanReach(this Pawn pawn, LocalTargetInfo dest, PathEndMode peMode, Danger maxDanger, bool canBashDoors = false, bool canBashFences = false, TraverseMode mode = TraverseMode.ByPawn)`，函数体到 `:16` 收尾，实质只有 `return pawn.Map.reachability.CanReach(pawn.Position, dest, peMode, TraverseParms.For(...));`。⇒ 板上 `Verse/ReachabilityUtility.cs:8-16` 的引用属实。

### 3.2 🚩 板上低估了范围：**同一个文件 patch 了 4 个 vanilla 方法**

同一文件内 `[HarmonyPatch(...)]` 全部命中（`grep` 全文，`*.cs`，含 `HarmonyPatch(typeof` 与 `class Patch_` 两组模式）：

| 行 | 目标 |
|---|---|
| 17 | `ReachabilityUtility.CanReach(Pawn, LocalTargetInfo, PathEndMode, Danger, bool, bool, TraverseMode)` |
| 35 | `ReachabilityUtility.CanReach(Pawn, IntVec3, LocalTargetInfo, PathEndMode, Danger, bool, bool, TraverseMode)` |
| **99** | `Reachability.CanReach(IntVec3, LocalTargetInfo, PathEndMode, TraverseParms)` |
| 234 / 426 | `ReachabilityImmediate.CanReachImmediate`（核心实现，1 个补丁覆盖 3 个重载） |

**⇒ 直接订正板上建议 ④①**：板上写「绕开扩展方法直接调 `map.reachability.CanReach(...)` 并注明『这也可能被 `Patch_Reachability_CanReach_Flight` 接管』」—— **它自己点名的那条绕法就是不成立的**：`ReachabilityPatches.cs:99-114` 同样对 `map.reachability.CanReach` 加了 `return false` 型 Prefix。**在 TEXT20 这类模组下，可达性判定链上不存在任何一个「原版语义」的入口。**

### 3.3 `Harmony.GetPatchedMethods()` 在本项目可用吗？—— 可用，但**板上那个写法会返回 0**

**(a) Harmony 版本**：`Mods\UESDdebuger\Assemblies\` 下**没有** `0Harmony.dll`（该目录实测 9 个文件：`Mono.Cecil*.dll` ×4、`MonoMod.*.dll` ×2、`UELoader.dll`、`UELoader.pdb`、`UnityExplorer.STANDALONE.Mono.dll`、`UniverseLib.Mono.dll`）。⚠️ 本次派单前提「`Assemblies\` 下有 `0Harmony.dll`」**不成立**，特此订正。本仓库树内的 Harmony 只有两处：`Source\UELoader\lib\0Harmony.dll`（FileVersion **2.3.3.0**）与 `Source\UE-4.9.0-official\UnityExplorer-4.9.0\UnityEditorPackage\Runtime\0Harmony.dll`（2.5.2.0，UE 自带）。另注：RimWorld 1.6 **vanilla 不带 Harmony**（`RimWorldWin64_Data\Managed` 实测 106 个程序集，**零个** 0Harmony），实际加载哪一份由 ModsConfig 里的第一个带 0Harmony 的模组决定。

**(b) API 存在性（decompilerserver 只读核实，contextAlias=`harmony`，即 `1.6update\AerocraftFramework\0Harmony.dll`）**：

- `abec1146…:060001C2:M` = `HarmonyLib.Harmony.GetPatchedMethods` —— `IEnumerable<MethodBase>`，**实例**方法
- `abec1146…:060001C3:M` = `HarmonyLib.Harmony.GetAllPatchedMethods` —— `get_member_details` 返回 `accessibility: Public, isStatic: true`，签名 `IEnumerable GetAllPatchedMethods()`

**(c) 板上建议 ② 若照抄会失败 —— 本仓库早就踩过并写在注释里**：

`Source/UELoader/HotReloadValidator.cs:380-406`（1-based，`ReadAllLines` 复核）：

- `:383-387` 原文：「**必须用静态 `Harmony.GetAllPatchedMethods()`**：实测 0Harmony 2.4.1 下 `Harmony.GetPatchedMethods()` 是**实例方法且按 owner 作用域** —— 任意新实例返回 0（2026-09-19 实测：`new Harmony("probe").GetPatchedMethods()` → `totalTargets=0`，而静态版 → **495**）。用错会同时搞坏两件事…」
- `:391` `public static List<MethodBase> AllPatchedMethods(HarmonyLib.Harmony instance)`
- `:395-400` 反射取静态 `GetAllPatchedMethods`；`:404` 反射失败才退回实例方法
- `:360` / `:423` 已在用 `HarmonyLib.Harmony.GetPatchInfo(m)` 拿 `Prefixes/Postfixes/Finalizers/Transpilers`
- `:414-441` `CollectPatchesFromAssemblies` 已经能把「目标方法 × patch 方法」成对收集出来

**(d) 归属确认**：`MCP/index.js` 全文 `grep`（模式 `(?i)harmony|HarmonyPatch|\bPatchInfo\b|GetPatchedMethods|patched_methods`）→ **0 命中**（38 条命中全部落在 `MCP\logs\*.log`、`MCP\test\toolchainFixes.test.js:131` 的 `brrainz.harmony` 包名、`MCP\node_modules\`）⇒ **当前没有任何 MCP 工具能回答这个问题**。`Source/UELoader/CompatibilityHooks.cs:98` 只在启动时检查 `HarmonyLib.Harmony.GetPatchedMethods` 是否存在（缺了打降级警告），不是对外端点。

**(d2) ✅「走 `execute_csharp_code` 的绕法今天就能用」有本项目自己的历史成功记录**：`MCP\logs\mcp-20261001-093829.log` 里连续 30 余条 `execute_csharp_code` 调用在游戏进程内直接用 `HarmonyLib.AccessTools.Method(typeof(…))` / `typeof(HarmonyL…`，其中 `:837` 的 `success` 与 `:838/:844/:847/:850/:851/:852/:853` 的 `success` 证明 **`HarmonyLib` 在 `execute_csharp_code` 的编译上下文里可直接解析**（同批调用里也出现过 `COMPILATION_ERROR`，说明这条路的门槛是「写法对不对」，不是「访问不到 Harmony」）。

**(e) 架构边界（关键）**：Harmony 补丁**不改动目标方法的元数据**，只在运行时把委托指针换掉 ⇒ Cecil / `decompilerserver` / `find_methods` 这条离线链**原理上不可能看见补丁**。而 `McpRimDebug` 是**离线进程**（`McpRimDebug.csproj:29-39` 的 `ItemGroup` 只有 `ModelContextProtocol` 2.1.0 与 `Mono.Cecil` 0.10.4，**无 HarmonyLib 引用**），它也拿不到游戏进程的补丁表。⇒ **只有游戏进程内的 UELoader 侧能提供这个查询。**

## 4. 影响与严重度复评

| 维度 | 结论 |
|---|---|
| 真实性 | ✅ 逐行属实，且范围比板上写的更大（§3.2） |
| COMMON.md 归属 | ✅ **板上判的 §10 两条触发都成立**。`Mods\TEXT20\.superpowers\sdd\2026-10-01-原版寻路接管-缺口修复-总计划\COMMON.md:755` = 「**拿不到本该拿到的证据**」，`:756` = 「**文档/直觉与实际 API 不符**」，`:758-759` 原文「⚠️ 最后一条特别容易**连锁**：一处 API 认知错误会写进分计划正文、写进代码注释、写进文档，然后被下一个人当成事实继承」。[28] 的「注释里的事实是错的，下一个人会把它当事实继承」正是这条逐字命中。 |
| 严重度 | **🟠 中，复评维持 🟠**（不上调）。不上调的理由：它**不产生相反的工具级结论**，而是「本该是原版语义的表达式换了语义」—— 这是 Harmony 生态的**固有属性**，不是本项目引入的缺陷，模组作者本来就负全责（板上 §⑤ 自己也标了「本次靠读源码 + 追补丁解决」）。下调不了的理由：命中的是最高频的调试探针写法，且后果写进注释后**连锁**。 |
| 是不是设计使然 | **一半是**。零信号是 Harmony 的设计（运行时换委托，元数据不变）；**另一半不是** —— 「本项目不提供任何查询入口」是本项目可以自己决定的事。 |
| 可修性 | 中高。**实现有 90% 现成**（`HotReloadValidator` 的三个方法可直接复用），要写的是：UELoader 侧一个新 handler + `MCP/index.js` 侧一个新工具定义与转发。验收需要游戏在跑 ⇒ **不能像计划 10/11 那样离线 `--selftest` 验收**。 |

## 5. 责任归属

**本项目（McpRimDebug / UESDdebuger）**，但**落点在游戏侧**：

| 层 | 改动 | 现状 |
|---|---|---|
| `McpRimDebug\` | **不改** | 它是离线进程，拿不到运行时补丁表；`McpRimDebug.csproj` 也无 HarmonyLib |
| `Source\UELoader\` | **新增 1 个 handler** | 已有 `using HarmonyLib;` 的文件 10 个；`UEHttpHandler.cs:120` 的 `InspectType` 就是现成的端点形状可照抄；`HotReloadValidator` 已备好枚举逻辑 |
| `MCP\index.js` | **新增 1 个工具定义 + 转发** | 与 `inspect_type`（`index.js:1956-1968` 定义、`:4421-4430` 转发）同构 |

## 6. 修的话怎么修

### 6.1 立刻可做、零代码：把绕过姿势写进规范

**今天就能用的绕法**（不需要新工具，只需 `execute_csharp_code`，游戏进程内）：

```csharp
// 静态入口才是对的；实例方法按 owner 作用域，会返回 0（HotReloadValidator.cs:383-387 的实测）
foreach (var m in HarmonyLib.Harmony.GetAllPatchedMethods()) { … HarmonyLib.Harmony.GetPatchInfo(m) … }
```

⚠️ 但要如实告诉调用方：**这个查询只回答「谁被 patch 了」，不回答「`pawn.CanReach` 此刻走的是谁的逻辑」** —— 后者取决于前缀里的守卫条件（如 `FlightPawnConfig.EnableInterception && pawn.Spawned && HasFlightCapability(pawn)`，`ReachabilityPatches.cs:24-25`）。⇒ **最诚实的提示语应写「此 API 已被 N 个 Harmony patch 覆盖，语义可能不是原版」，而不是「语义已被替换成 X」。**

### 6.2 正解（若派 PLAN）

新增工具，命名建议 `list_harmony_patches`：

- **入参**：`typeName?`（可选，全限定名或短名）、`methodName?`（可选）、`limit?`（默认 100）
- **游戏侧实现**：`Source/UELoader/UEHttpHandler.cs` 照 `InspectType`（`:120-161`）的形状加一个 `ListHarmonyPatches(...)`；内部直接调 `HotReloadValidator.AllPatchedMethods(null)` 拿全表，逐个 `HarmonyLib.Harmony.GetPatchInfo(m)` 取 `Owners` / `Prefixes` / `Postfixes` / `Transpilers`，输出 `{ target: "<DeclaringType>.<Name>(<paramTypes>)", owners: [...], prefixes: [...], … }`
- **必须复制的两条既有教训**（都写在本仓库注释里，别重踩）：
  1. **用静态 `GetAllPatchedMethods()`，不要用实例 `GetPatchedMethods()`** —— 后者对任意新实例返回 0（`HotReloadValidator.cs:383-387`，2026-09-19 实测 495 vs 0）。
  2. Harmony 版本随 ModsConfig 里第一个带 0Harmony 的模组漂移（本机实测副本从 2.3.1.1 到 2.3.6.0 不等）⇒ handler 内必须按 `HotReloadValidator.cs:395-405` 的**反射 + 退回**写法，不要硬绑 `GetAllPatchedMethods`。
- **命名/形状上的额外建议**：把 [28] 本身当**验收用例** —— 工具跑出来必须能一眼看出 `Verse.ReachabilityUtility.CanReach` 有 4 条前缀挂在 `FlightPawn2Framework.PathPlan.FlightAStar.Patch_ReachabilityUtility_CanReach_Flight` 等类上。

### 6.3 同时要订正的两处板上内容（供主代理批注）

- 建议 ④① 的绕法**不成立**（§3.2）——`map.reachability.CanReach` 同样被 patch。
- 建议 ④② 的 `Harmony.GetPatchedMethods()` **照抄返回 0**（§3.3c）——必须写静态版。

## 7. 与既有计划的关系

| 计划 | 关系 |
|---|---|
| `plans/2026-10-01-10-api-member-visibility-tools.md` | **无关。** 计划 10 全程离线（Cecil + `dotnet run --project McpRimDebug -- --selftest`），并在 `§0.4` 明确「不给 `inspect_type` 加成员数据」的理由 (b) 正是「它只在 `IN_GAME` 且 UE 面板就绪时可用」。本条是**运行时的**、需要游戏在跑的**新增能力**，两者的前提条件正相反。 |
| `plans/2026-10-01-11-search-source-false-negative-convention.md` | **无关。** 那是 `rimworld-source-online` 文本检索的规范文档，管的是「源码文本里有什么」，管不到「运行时谁被 patch 了」。 |
| `plans/2026-10-01-22-play-for-guidance-and-unknown-arg-guard.md` | **无关。** |
| 计划 12 `2026-10-01-12-mod-presence-assembly-visibility.md` | **无冲突**（那份管 `mod_presence` / 程序集可见性）。 |

**⇒ 建议单立。** 理由：它是本板唯一一条**「新增能力」而非「修 bug」**的条目（缺陷在上游 Harmony 的不可见性，我们的锅只是「没提供出口」），落点是游戏侧 handler + Node 侧转发的**新组合**，与计划 10（C# 离线纯逻辑）/ 11（Markdown 规范）/ 22（Node 一行文案 + 未知参数守卫）的文件清单与验收方式**零交集**。若并入任一现有计划，都会把一个「必须在游戏里验收」的交付物塞进一个「离线可全绿」的计划，破坏其验收纪律。

**优先级建议**：🟠，排在计划 10/11 之后、不阻塞它们 —— 但**建议先把 §6.1 的绕过姿势与 §6.3 的两条订正写进规范**，因为这两条今天就能挡住误判，而新工具要等一次带模组的冷重启才能验收。
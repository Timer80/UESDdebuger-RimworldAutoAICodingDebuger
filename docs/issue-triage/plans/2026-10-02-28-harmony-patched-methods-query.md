# Harmony 补丁可见性查询（`list_harmony_patches`）实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让「某个方法此刻有没有被 Harmony patch 掉、被谁 patch 的」成为**一次只读工具调用就能证伪**的事，并在此之前先把**今天就能用的绕法**与**两条会被照抄错的订正**写进规范 —— 消灭「写了 `pawn.CanReach(...)` 就以为那是原版地面可达性」这类**零信号**误判。

**架构：** 三层。①**零代码那半**：`README.md` / `AI-DEPLOYMENT.md` 落一条 `execute_csharp_code` 绕法 + 两条订正（今天即可用，零风险）。②**离线可测的取数与整形**：新建纯逻辑 `Source/UELoader/UEHarmonyPatchIndex.cs`（零 Harmony / 零 Verse 依赖，只做过滤/排序/计数）+ 反射适配器 `Source/UELoader/UEHarmonyPatchSource.cs`（只依赖 `HarmonyLib` + `System.Reflection`，**全部访问器走「属性→字段」两级反射**以吃下 2.3.1.1 / 2.3.3.0 / 2.5.2.0 三种 API 形态）；两者由新工程 `Source/HarmonyPatchIndexTests` **真打一个 Harmony 补丁做往返断言**，**不需要游戏就能验证 90% 的功能**。③**暴露面**：UE 侧新端点 `GET /harmony/patches`（`UEHttpHandler.ListHarmonyPatches` + `UEHttpServer` 路由），MCP 侧新工具 `list_harmony_patches`。**`McpRimDebug` 一个字节都不改** —— 它是离线进程，原理上拿不到运行时补丁表。

**技术栈：** C#（net472，`LangVersion 9.0`，SDK-style csproj，`Source/UELoader/*.cs` 自动纳入编译）+ HarmonyLib 2.3.3.0 编译引用（`Private=false`，运行期由 ModsConfig 里第一个带 0Harmony 的模组提供）+ Node.js ESM（`MCP/package.json:6` `"type": "module"`）+ `node:test` & `node:assert/strict`（`MCP/package.json:9`）+ net472 `Exe` 手写 `Check()` runner（沿用 `Source/ConfigJsonTests` / `Source/HotReloadTests` 形状）。**改 `MCP/index.js` 即改到生效那份**（`runtime/` 下无 `index.js` 副本）。**C# 改动需要重编译 `Assemblies/UELoader.dll` 才生效，且必须先关闭游戏**（`Source/UELoader/UELoader.csproj:91-96` 的 `DeployToAssemblies` 要往被游戏占用的 DLL 拷文件）。**本仓库不是 git 仓库**（`UESDdebuger/` 下只有 `.gitignore`，无 `.git/`）⇒ 每个任务最后一步是「改动清单」核对，不是 `git commit`。

---

## 0. 给零上下文执行者的背景（不要跳过）

### 0.0 🚫 两条板上订正：照抄就会得出事实相反的结论

吐槽板 `## [28]` 的建议 ④ 里有两条写法**是错的**。本计划把它们钉在最前面，因为它们正是「COMMON.md §10 说的会连锁」的那一类 —— 错一次会写进注释、写进文档、被下一个人当事实继承。

| 板上原文 | 裁定 | 正确写法 |
|---|---|---|
| 建议 ④①：「绕开扩展方法直接调 `map.reachability.CanReach(a, b, PathEndMode.OnCell, TraverseParms.For(TraverseMode.ByPawn))` 并显式注明『这也可能被 `Patch_Reachability_CanReach_Flight` 接管』」 | ❌ **这条绕法不成立** —— 它自己点名的那条补丁就在同一条判定链上 | **可达性判定链上不存在任何「原版语义」入口**。唯一正确的做法是**先查运行时补丁表**（本计划的工具），而不是找一条「没被 patch 的原版路径」 |
| 建议 ④②：「`Harmony.GetPatchedMethods()` 已在 Harmony 2 里可用」 | ❌ **照抄返回 0** —— 它是**实例方法且按 owner 作用域**，任意新实例枚举不到别的 owner 的补丁 | 必须用**静态 `Harmony.GetAllPatchedMethods()`** |

证据（**行号已用 `[System.IO.File]::ReadAllLines` 复核**）：

- **④① 不成立**：`TEXT20\source\PathPlan\FlightAStar\ReachabilityPatches.cs` 全文 505 行，同一文件里共 patch 了 **4 个** vanilla 可达性方法 —— `:17`（`ReachabilityUtility.CanReach(Pawn, LocalTargetInfo, …)`）、`:35`（`ReachabilityUtility.CanReach(Pawn, IntVec3, LocalTargetInfo, …)`）、`:99`（`Reachability.CanReach(IntVec3, LocalTargetInfo, …)`）、`:234` / `:426`（`ReachabilityImmediate.CanReachImmediate`，一条补丁覆盖 3 个重载）。后两个与 `:17` 同为 `return false` 型 Prefix ⇒ **原版 `Verse/ReachabilityUtility.cs:8-16` 的函数体只有一句 `pawn.Map.reachability.CanReach(pawn.Position, dest, peMode, TraverseParms.For(...))`，而这一句本身就被 patch 掉了**。搜索范围：对该文件全文 `grep` `[HarmonyPatch(` 与 `class Patch_` 两组模式，命中即上表 5 处。
- **④② 不成立**：`Source/UELoader/HotReloadValidator.cs:383-387` 的注释原文已经记着这件事 ——「**必须用静态 `Harmony.GetAllPatchedMethods()`**：实测 0Harmony 2.4.1 下 `Harmony.GetPatchedMethods()` 是**实例方法且按 owner 作用域** —— 任意新实例返回 0（2026-09-19 实测 `new Harmony("probe").GetPatchedMethods()` → **0**，静态版 → **495**）」。**本仓库早就踩过这个坑，坑就写在注释里。**
- **静态版的 API 存在性**（`decompilerserver` 只读核实，三个本机真实副本逐个查过）：

  | 0Harmony 副本 | FileVersion | `Harmony.GetAllPatchedMethods` | `GetPatchInfo` | `Patches.Prefixes` 等 | `Patch.PatchMethod` | `Patch.Owners` |
  |---|---|---|---|---|---|---|
  | `Source\UELoader\lib\0Harmony.dll`（**编译面**） | 2.3.3.0 | ✅ `Public` + `isStatic`（`060001C3:M`） | ✅ `060001C1:M` static | ✅ **public 字段** `ReadOnlyCollection<Patch>` | ✅ **public 属性** | ❌ 只有 public **字段** `owner` |
  | `Mods\TEXT33\tools\refs\Blueprints\Assemblies\0Harmony.dll` | 2.3.1.1 | ✅ `060001C6:M` | ✅ | ✅ public 字段 | ✅ 属性 | ❌ 字段 `owner` |
  | `Source\UE-4.9.0-official\…\Runtime\0Harmony.dll`（UE 自带） | 2.5.2.0 | ✅ `060000CC:M` | ✅ | ✅ public 字段，**另有 `ILManipulators`（第 5 类补丁）** | ✅ 属性 | ❌ 字段 `owner` |

  ⚠️ **这张表就是「必须走反射 + 版本退回」的完整理由**，比裁决报告说的更宽：
  1. `GetAllPatchedMethods` 在三个版本里都有，但**老版本可能没有**（本机只能证明 2.3.1.1 起有；RimWorld 1.6 vanilla **不带 Harmony**，实际加载哪一份由 ModsConfig 里第一个带 `0Harmony` 的模组决定）⇒ 必须反射探测 + 退回。
  2. `Patch.Owners` **在三个版本里都不存在**（`owners` 只能是 public 字段 `owner`）⇒ 任何 `p.Owner` 的写法都编译不过。
  3. 2.5.2 多出 **`ILManipulators`**（IL 操作器）这一类补丁。**只循环 4 类会静默丢掉它** —— 这正是本条目说的「零信号」在工具自身重演。

### 0.1 吐槽板原文（`docs/mcp-吐槽板.md`，用 `^## \[28\]` 定位，**不要用行号** —— 板子正在被主代理与另一条并行会话持续改写）

> ## [28] 装了 Reachability 补丁的模组里，`pawn.CanReach(...)` 已不是原版语义，且**零信号** 🟠
>
> **①症状** — 在一个给 `Verse/ReachabilityUtility.CanReach` 打了 Harmony Prefix（`return false` 截断、改写 `__result`）的模组里，测试代码里写 `pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)` 拿到的不是原版地面可达性，而是模组自己的判定。原版 `Verse/ReachabilityUtility.cs:8-16` 的函数体**只有一句**；而 `TEXT20\source\PathPlan\FlightAStar\ReachabilityPatches.cs:17-32` 有一条形参类型逐个相同的 Prefix：`__result = FlightReachGate.CanReachFor(pawn, pawn.Position, dest.Cell, peMode, FlightLayer.Ground); return false;` ⇒ 对会飞的 pawn，**原版那句地面可达性被整条换成了飞行可达性判定**。编译器不报、运行时不报、`Player.log` 不打、反射枚举 API 也看不出来（方法签名一个字没变）。
>
> **③影响（这次骗到我的地方）** — 正在给一个 flaky 测试挑目标格，本能地选了 `pawn.CanReach` 当「静态可达性判据」。如果不追 `ReachabilityPatches.cs`，会写下注释「用原版可达性判据」，而实际判的是**飞行可达性** —— **注释里的事实是错的，下一个人会把它当事实继承**。反向也成立：想拿原版地面语义做对照时，同一句 `pawn.CanReach` 会静默给飞行答案 ⇒ 对照组失效。
>
> **⑤状态** — 🟠 待绕过（本次靠「读源码 + 追补丁」解决，未找到通用工具路径）。

### 0.2 今天就能用的绕法（任务 1 要落进规范的那段）

不需要新工具、不需要编译、不需要停游戏 —— `execute_csharp_code` 的编译上下文里**可以直接解析 `HarmonyLib`**（有本项目自己的历史成功记录，见 §0.6）：

```csharp
// 静态入口才是对的；实例方法按 owner 作用域，任意新实例返回 0
// （Source/UELoader/HotReloadValidator.cs:383-387 的 2026-09-19 实测：实例 0 / 静态 495）
var sb = new System.Text.StringBuilder();
foreach (var m in HarmonyLib.Harmony.GetAllPatchedMethods())
{
    var info = HarmonyLib.Harmony.GetPatchInfo(m);
    if (info == null) continue;
    foreach (var p in info.Prefixes)
        sb.AppendLine(m.DeclaringType.FullName + "." + m.Name
            + "  <-Prefix  " + (p.PatchMethod == null ? "?" : p.PatchMethod.DeclaringType.FullName));
}
sb.ToString();
```

⚠️ **但必须同时如实告诉调用方**：这个查询只回答「**谁被 patch 了**」，**不回答「`pawn.CanReach` 此刻走的是谁的逻辑」** —— 后者取决于前缀内部的守卫条件（如 `FlightPawnConfig.EnableInterception && pawn.Spawned && HasFlightCapability(pawn)`，`ReachabilityPatches.cs:24-25`）。⇒ **最诚实的提示语是「此 API 已被 N 个 Harmony patch 覆盖，语义可能不是原版」，不是「语义已被替换成 X」。** 本计划的工具描述、`TOOL_USAGE_NOTES`、两处文档全部按前半句写。

### 0.3 「零信号」纪律 → 本工具的反假阴性契约

吐槽板这条的痛点不是「没有工具」，是「**工具说零 = 调用方当成没有补丁 = 当成原版语义**」。因此新工具必须满足（下面每一条在任务 2 的测试里都有对应用例）：

1. **`rows: []` 永远不是孤立的空数组。** 响应必带 `totalPatchedTargets`（取到的被 patch 目标方法总数）、`totalRows`（展开后的总行数）、`matchedTargets` / `matchedRows`，以及 `lookupScope: "runtime-harmony-patch-table"`。零命中时调用方一眼看到「枚举到 N 个目标、0 个匹配」，而不是「返回了空」。
2. **`lookup` 块必须自曝取数路径**：`{ source, harmonyVersion, targetsFailed }`。`source` 三态：`static-getAllPatchedMethods`（正常）/ `instance-getPatchedMethods`（**退回路径：owner 作用域，计数很可能不可信，响应里必须写明**）/ `unavailable`（两条入口都没有 ⇒ 直接返回 `errorCode: "HARMONY_UNAVAILABLE"`，**绝不返回空列表冒充「没有补丁」**）。
3. **`targetsFailed` 不吞。** `GetPatchInfo(m)` 对个别方法抛异常时，记 `targetsFailed++` 并在响应里报出来，不静默 `continue`（否则「总数对不上」本身就是新的假阴性）。
4. **绝不把「0 条」说成「原版语义」。** 工具描述与文档统一写「已被 N 个 Harmony patch 覆盖，语义可能不是原版」。

### 0.4 为什么不能落在 `McpRimDebug`（本计划一个字都不改它）

`McpRimDebug\McpRimDebug.csproj:36-39` 的 `ItemGroup` 只有两个 `PackageReference`：`ModelContextProtocol` 2.1.0 与 `Mono.Cecil` 0.10.4，**无 HarmonyLib**。更根本的是：**Harmony 补丁不改动目标方法的元数据**，只在运行时把委托指针换掉 ⇒ Cecil / `decompilerserver` / `find_methods` 这条离线链**原理上不可能看见补丁**。离线进程也拿不到另一个进程（游戏）的补丁表。⇒ **只有游戏进程内的 UELoader 侧能提供这个查询。** 执行者不要试图给 `McpRimDebug` 加引用或加自测。

### 0.5 游戏未运行时 / 主菜单时的表现（明确契约）

`list_harmony_patches` 走 `callUnityExplorerAPI`（`MCP/index.js:736`）。**本工具刻意不加 `requireStage([STAGES.IN_GAME])`**（对照 `MCP/index.js:4422` / `:4433` / `:4521` 等需要进图的 UE 工具）：

| 情形 | 发生什么 | 必须保证 |
|---|---|---|
| **游戏完全没运行** | `fetch` 连不上 → `callUnityExplorerAPI` 的 catch 分支返回失败 | MCP case 转成失败响应 + `guidance`：**「读的是游戏进程内的运行时补丁表；游戏未运行时无法回答，这不等于该方法没有补丁」** |
| **游戏在主菜单** | UE HTTP 服务在模组构造阶段即已启动（`UEHttpHandler.Init`，`UEHttpHandler.cs:34`）；模组程序集与 Harmony 补丁在启动期就已加载 | 工具**可用**，且**不依赖 UnityExplorer 面板是否打开**（不查 `UIManager.UIRoot`，对照 `InspectType` 的 `:125-126`）。「某个 vanilla 方法有没有被 patch」恰恰是主菜单最常问的问题 |
| **游戏在加载中** | 补丁集合可能尚未齐 | 响应带 `totalPatchedTargets`，调用方自行判断规模；**不得**把加载中的短清单说成「没装」 |
| **新 DLL 没生效（端点没部署）** | UE 返回 404 / 非 JSON → `callUnityExplorerAPI` 的 `readJson`（`:781-784`）对空 body 返回 `{}` → `success:false` | MCP case 走同一失败分支并附 `guidance` 指明「可能需要重启游戏让新 `Assemblies/UELoader.dll` 生效」 |

### 0.6 必须先停游戏：构建与实测是**整树操作**

⚠️ **本计划会改 `.cs` ⇒ 必须重编译 ⇒ 必须先停游戏。** 三件事必须一次做完，中途任何一步停下来都会留下不一致的树：

| 步骤 | 命令 | 为什么不能与游戏并行 |
|---|---|---|
| 构建 | `dotnet build Source/UELoader/UELoader.csproj` | `Source/UELoader/UELoader.csproj:91-96` 的 `DeployToAssemblies` 目标要把 `UELoader.dll` / `.pdb` **拷进 `Assemblies/`**，而游戏运行时该 DLL **已被 mmap 占用** ⇒ 拷贝必失败（`MSB3021` 类）或产生半截文件 |
| 生效 | 停游戏 → 构建 → **重启游戏** | 游戏只在启动期加载模组 DLL；不重启，新端点 `/harmony/patches` 不存在（返回 404） |
| 实测 | 起游戏 → 进图或停在主菜单 → 调 `list_harmony_patches` | 这是本计划**唯一必须「游戏在跑」才能验收**的部分 |

> **不要用 `execute_csharp_code` 去验这个工具。** 理由有二：(a) 它是写操作工具，本流水线的铁律禁止调用；(b) 它能验的只是 §0.2 那段绕法，验不到 MCP 工具的路由与参数契约。
>
> **也不要破坏其它计划的离线验收纪律。** 任务 1 / 2 / 3 / 5 是纯离线（文档 / 纯逻辑 / 反射适配器 / Node 测试），**跑完必须全绿且不碰游戏**；只有任务 4 的构建步骤与任务 6 的实测需要整树停启游戏。执行者可以把 1→2→3→5 一次性做完、跑绿，再单独安排一次「停游戏 → 构建 → 起游戏 → 实测」的窗口。

### 0.7 测试基础设施（真实命令，不要臆造）

工作目录 = `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger`

| 用途 | 命令 |
|---|---|
| 跑 MCP 侧 JS 测试（全部） | `Set-Location MCP; npm test`（= `node --test "test/*.test.js"`，`MCP/package.json:9`） |
| 跑 MCP 侧 JS 测试（单文件） | `Set-Location MCP; node --test test/harmonyPatchesTool.test.js` |
| 跑本计划的 C# 离线测试 | `dotnet run --project Source/HarmonyPatchIndexTests` |
| 跑既有 C# 离线测试（回归） | `dotnet run --project Source/HotReloadTests`、`dotnet run --project Source/ConfigJsonTests` |
| 构建模组程序集（**必须先关闭游戏**） | `dotnet build Source/UELoader/UELoader.csproj` |
| 离线自测 `McpRimDebug`（回归，本计划不改它） | `dotnet run --project McpRimDebug -- --selftest` |

**前置守卫：** `Source/Directory.Build.props:21-26` 的 `GuardRefs` 目标对 `Source/` 下**所有** csproj 生效，缺 `$(RimWorldManagedDir)\Assembly-CSharp.dll` 或 `$(HarmonyRefDir)\0Harmony.dll` 会直接报 MSBuild `Error`。换机器时用 `-p:RimWorldGameDir=<游戏根>` 或环境变量 `RIMWORLD_GAME_DIR` 指定。

**JS 测试守卫：** 任何 `import '../index.js'` 的测试文件都必须在 import **之前**写 `process.env.UESD_MCP_NO_START = '1'`，否则会启动 HTTP 服务器占 3000 端口（见 `MCP/test/toolchainFixes.test.js:7-9`、`MCP/test/toolCountConsistency.test.js:4-6`）。

**C# 离线测试守卫：** 测试工程必须**只 link 纯逻辑文件**（形状照抄 `Source/ConfigJsonTests/ConfigJsonTests.csproj:11-14`），因为 `UELoader.dll` 本身依赖 Verse/Unity，脱离游戏编译不了。`Source/HarmonyPatchIndexTests` 额外 `<Reference Include="0Harmony">`（`Private=true`）—— 因为本计划的被测代码里 `UEHarmonyPatchSource.cs` 真的要引用 `HarmonyLib` 并在测试进程里真打一个补丁。

### 0.8 证据：`HarmonyLib` 在 `execute_csharp_code` 里可直接解析

`MCP\logs\mcp-20261001-093829.log`（全文 1361 行，`[System.IO.File]::ReadAllLines` 复核）里，`:831-853` 这一段连续出现 `tool=execute_csharp_code` 且参数里含 `HarmonyLib` 的调用，其中 **`success` 共 9 条**：`:837` / `:838` / `:839` / `:844` / `:849` / `:850` / `:851` / `:852` / `:853`；同段 `:847` 是 `COMPILATION_ERROR`（**不是** success）。整份日志里同类 `success` 共 13 条（另加 `:36` / `:41` / `:816` / `:1004`）。⇒ **§0.2 的绕法在本项目里有历史成功记录**，不是纸上谈兵。

### 0.9 与其它计划 / 既有断言的冲突面（实施前必读）

| 冲突面 | 结论 |
|---|---|
| **`MCP/test/toolchainFixes.test.js:120-124`** | 该断言切片 `function toolListLine(`（`MCP/index.js:3281`）→ `function buildMenuNode(`（`:3290`），断言区间内含 `toolAvailabilityTag(name)`。本计划**不在这两者之间插任何函数**，不碰该区间。 |
| **`MCP/test/toolchainFixes.test.js:263-268`** | 该断言切片 `case 'start_game': {`（`MCP/index.js:4356`）→ `case 'stop_game': {`（`:4371`）→ `case 'get_game_status': {`（`:4384`），断言两个区间内都有 `dpaRoundActive = false`。本计划的新 case 插在 `:4527` 之后，**远离该区间**，不碰。 |
| **`MCP/test/toolchainFixes.test.js:273-286`** | 「三件套齐全」断言（`MONO_DEBUG_TOOL_NAMES` / `toolConfig.json` / 两份语言 XML）只针对 **mono 工具** `reconnect`。本计划的工具**不是** mono 工具（不经 mono 桥接），照抄那条断言会误判。本计划在自己的测试文件里写**同样形状但只覆盖 `toolConfig.json` + 两份语言 XML** 的断言。 |
| **`MCP/test/toolCountConsistency.test.js`** | 全文 148 行，**无任何硬编码工具数**（`:24` / `:30` / `:45` / `:58` 全是自洽关系断言）。新增一个工具不破坏它 —— 但**必须**登记进 `TOOL_CATEGORY`（本计划做）。**不需要**登记进 `TOOL_SUBGROUP`：计划 `2026-10-01-12` 之所以要登记，是因为它把工具挂在嵌套叶子 `game_control/game/mods` 上；而 `unityexplorer` 在 `resolveMenuPath`（`MCP/index.js:3222-3225`）里是**扁平叶子类别**，`menuCategoryOf`（`:3382-3386`）对无 `TOOL_SUBGROUP` 的名字回落到 `toolCategory` → `TOOL_CATEGORY`，结果是 `unityexplorer`，`menuUniverseNames()`（`:3369-3376`）也已因它进了 `TOOLS` 而包含它 ⇒ 概览计数与叶子列表两侧同步自洽。 |
| 计划 `2026-10-01-11-search-source-false-negative-convention.md` | 它计划新建 `docs/mcp-source-lookup-conventions.md` 并在 `AI-DEPLOYMENT.md` §8 挂入口。本计划**只往 §8 的表格尾部追加两行**，不新增小节、不动它规划的任何一行 ⇒ 两者先后实施均不冲突。 |
| 计划 `2026-10-01-12-mod-presence-assembly-visibility.md` | 它要改 `MCP/index.js:2072` / `:2078` / `:2912` / `:2926` / `:2937` / `:3148` / `:3812` / `:4985` 与 `toolConfig.json`、两份语言 XML。本计划改的是**另外的行**（见 §1 表），只有 `toolConfig.json` 与语言 XML 是**同文件不同条目** ⇒ 并行实施不冲突，但两份语言 XML 与 `toolConfig.json` **建议串行**（同一文件同时写）。 |
| `Source/UELoader/HotReloadValidator.cs` | **一个字都不改。** 本计划只**读**它的 `AllPatchedMethods`（`:391-406`）与 `CollectPatchesFromAssemblies`（`:414-444`）作为设计参照，代码另起炉灶（见任务 3 步骤 1 的理由）。 |

### 0.10 执行者不要做的事

- 不要改 `docs/mcp-吐槽板.md`（给条目加批注是主代理的活，`AGENT-BRIEF.md` §1.1 明令 PLAN 子代理不许碰）。
- 不要用**实例** `Harmony.GetPatchedMethods()` 作为主路径（§0.0）。
- 不要写 `patch.Owner`（三个 0Harmony 版本都没有这个属性，只有 public 字段 `owner`）。
- 不要只循环 4 类补丁（2.5.2 有第 5 类 `ILManipulators`，见 §0.0 表）。
- 不要为了「顺便」去改 `McpRimDebug`（§0.4）。
- 不要在 `Source/UELoader/UEHarmonyPatchIndex.cs` 里引入 `System.Reflection` / `HarmonyLib` / Verse / Unity 引用 —— 保持它零依赖，这是它能被离线测的唯一前提。
- 不要在游戏运行时跑 `dotnet build Source/UELoader/UELoader.csproj`（§0.6）。
- 不要为了让新工具「看起来更全」去改 `rimworld.list_mods`、`inspect_type` 等既有工具的 schema。
- 不要声明 `additionalProperties: false`（实测未声明的多余参数会被静默忽略，声明与行为不符；见 `MCP/index.js:2487-2488` 的既有注释）。

---

## 1. 文件结构

### 新建

| 文件 | 职责 |
|---|---|
| `Source/UELoader/UEHarmonyPatchIndex.cs` | **纯逻辑**（只用 `System` / `System.Collections.Generic`，零 HarmonyLib / 零 Verse / 零 Unity / 零 `System.Reflection`）：一行 =「目标方法 × 补丁种类 × patch 方法」，只做 `query` 过滤、稳定排序、`limit` 截断、六个计数、vanilla 程序集判定。**它不碰 `MethodInfo`，只碰一个纯数据类 `HarmonyPatchRow`** —— 这是它能被离线测的唯一前提。 |
| `Source/UELoader/UEHarmonyPatchSource.cs` | **反射适配器**（只用 `HarmonyLib` + `System.Reflection` + `System.Linq` + `System.Collections.Generic`，零 Verse）：枚举被 patch 的目标方法、读出每个目标的 5 类补丁、生成 `HarmonyPatchRow` 列表、回传取数来源与 0Harmony 版本。**全部 `PatchInfo` / `Patch` 成员一律「先属性后字段」两级反射**，并把结果缓存。 |
| `Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj` | net472 `Exe` 测试工程，`link` 上面两个源文件 + `<Reference Include="0Harmony">`（形状逐字照抄 `Source/HotReloadTests/HotReloadTests.csproj:11-19`） |
| `Source/HarmonyPatchIndexTests/Program.cs` | 手写 `Check()` runner（形状照抄 `Source/ConfigJsonTests/Program.cs:20-26`）+ **真 Harmony 补丁往返用例** |
| `MCP/test/harmonyPatchesTool.test.js` | 本计划全部 JS 侧断言：工具注册、菜单可见性、参数钳制、失败分支引导语、两条订正的文案、以及**不破坏既有源码切片断言**的回归守卫 |

### 修改

| 文件 | 改动 | 需要重编译 |
|---|---|---|
| `Source/UELoader/UEHttpHandler.cs:161` 之后 | 新增 `ListHarmonyPatches(string query, int limit)`（形状照抄 `:120-161` 的 `InspectType` 与 `:753-769` 的 `Success`/`Error`） | **是** |
| `Source/UELoader/UEHttpServer.cs:408` 之后 | `case "harmony/patches":`（`GetGuard` + `GetQueryString` + `GetIntQuery`，形状照抄 `:402-404`） | **是** |
| `Source/UELoader/CompatibilityHooks.cs:99` 之后 | 启动期 API 存在性检查补 `GetPatchInfo` 与 `GetAllPatchedMethods` 两项（现有 `:98-99` 只查 `GetPatchedMethods`） | **是** |
| `MCP/index.js:2490` 之后 | `TOOLS.push({ name: 'list_harmony_patches', … })`（形状照抄 `:2450-2490` 的 `post_map_marker` 那个 push） | 否 |
| `MCP/index.js:2898` 之后 | `TOOL_CATEGORY` 加 `list_harmony_patches: 'unityexplorer'` | 否 |
| `MCP/index.js:2926` 之后 | `TOOL_PRECONDITION` 加 `list_harmony_patches` 一行 | 否 |
| `MCP/index.js:2935` 之后 | `TOOL_USAGE_NOTES` 加 `list_harmony_patches` 一条 | 否 |
| `MCP/index.js:3793` 之后 | `TOOL_ARG_ALIASES` 加 `list_harmony_patches` 一条 | 否 |
| `MCP/index.js:4527` 之后 | `handleToolCall` 的 `case 'list_harmony_patches'`（形状照抄 `:4520-4527` 的 `list_hooks`） | 否 |
| `MCP/toolConfig.json` | `tools` 里加 `"list_harmony_patches": false`（与 `inspect_type` 同档：默认不直接暴露，经 `agg_call_tool` 可达） | 否（需重启 MCP 进程） |
| `Languages/ChineseSimplified/Keyed/UESDdebuger.xml` | 加 `UESDdebuger.ToolDesc.list_harmony_patches` | 否 |
| `Languages/English/Keyed/UESDdebuger.xml` | 同上 | 否 |
| `README.md:90` 之后 | UE 工具表加一行（**`:60` 的工具数口径行不动** —— 本工具登记为 `false`，直接暴露数不变） | 否 |
| `AI-DEPLOYMENT.md:177` 之后 | §8 排障决策树加两行（「零信号 = 静默被 patch」/「新工具报游戏未运行」） | 否 |
| `AI-DEPLOYMENT.md:190` 之后 | §9 一句话提醒加一条「Harmony 语义查询只能用静态 `GetAllPatchedMethods`」 | 否 |

> `Source/UELoader/UELoader.csproj` 是 SDK-style（`Microsoft.NET.Sdk`），`Source/UELoader/*.cs` **自动**纳入编译，**不需要改 csproj**。两个新 `.cs` 落在同目录即自动进程序集。

---

## 2. 任务

### 任务 1：零代码那半 —— 把绕法与两条订正写进 `README.md` / `AI-DEPLOYMENT.md`

**为什么先做这个：** 新工具要等一次「停游戏 → 构建 → 重启 → 带模组冷启动」的窗口才能验收，而 §0.2 的绕法**今天就能用**。先落文档，本条目至少立刻挡住误判。

**文件：**
- 修改：`README.md:90` 之后（UE 工具表尾）、`README.md:60`（工具数口径行）
- 修改：`AI-DEPLOYMENT.md:177` 之后（§8 表格尾）、`AI-DEPLOYMENT.md:185` 之后（§9 列表尾）
- 测试：`MCP/test/harmonyPatchesTool.test.js`（本任务先建骨架，后续任务往里加断言）

- [ ] **步骤 1：新建测试文件骨架，先写会失败的文案断言**

`MCP/test/harmonyPatchesTool.test.js`：

```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(here, '..', '..');
const readText = (rel) => fs.readFileSync(path.join(root, rel), 'utf-8');

const parse = async (value) => JSON.parse(
  await Promise.resolve(value).then((r) =>
    typeof r === 'string' ? r : (r.content || []).map((c) => c.text || '').join('')));

const STATIC_CALL = 'GetAllPatchedMethods';

test('§0 订正一：两份文档都写明"必须用静态入口、实例版返回 0"', () => {
  for (const rel of ['README.md', 'AI-DEPLOYMENT.md']) {
    const t = readText(rel);
    assert.ok(t.includes(STATIC_CALL), `${rel} 未出现静态入口 ${STATIC_CALL}`);
    assert.match(t, /GetPatchedMethods\(\)[^\n]*返回\s*0|返回\s*0[^\n]*GetPatchedMethods\(\)/,
      `${rel} 未写明实例版返回 0`);
  }
});

test('§0 订正二：两份文档都写明"绕开扩展方法调 map.reachability.CanReach"这条不成立', () => {
  for (const rel of ['README.md', 'AI-DEPLOYMENT.md']) {
    const t = readText(rel);
    assert.match(t, /reachability\.CanReach/, `${rel} 应点到这条被否的绕法`);
    assert.match(t, /不成立|同样被\s*patch|也被\s*patch/, `${rel} 未标注该绕法不成立`);
  }
});

test('§0 能力边界：提示语是"可能被 patch"，不是"语义已被替换成 X"', () => {
  for (const rel of ['README.md', 'AI-DEPLOYMENT.md']) {
    const t = readText(rel);
    assert.match(t, /语义可能不是原版/, `${rel} 缺少保守提示语`);
    assert.ok(!/语义已被替换成/.test(t), `${rel} 不得断言"语义已被替换成 X"`);
  }
});

test('§0.2 绕法可直接复制：两份文档都含可粘贴的 C# 片段', () => {
  for (const rel of ['README.md', 'AI-DEPLOYMENT.md']) {
    const t = readText(rel);
    assert.match(t, /foreach\s*\(var\s+\w+\s+in\s+HarmonyLib\.Harmony\.GetAllPatchedMethods\(\)\)/,
      `${rel} 缺少静态枚举片段`);
    assert.match(t, /HarmonyLib\.Harmony\.GetPatchInfo\(/, `${rel} 缺少 GetPatchInfo 调用`);
  }
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：`Set-Location MCP; node --test test/harmonyPatchesTool.test.js`
预期：4 个测试**全部 FAIL**，`README.md 未出现静态入口 GetAllPatchedMethods` 等（文档尚未改）。

- [ ] **步骤 3：`README.md` 的两处改动**

先读 `README.md:58-91`，确认下面两段插入点的上下文仍然长这样（板子与 README 都在被并行会话改，**行号以标题文本为准**）：

- `:60` 那行是工具数口径行（以 `> 口径：连接 RimBridgeServer（GABP）后` 开头）—— **本任务不动它**（它是实测数字，改错就是新的假阴性；工具数变更由任务 5 负责，本工具默认不进直接暴露清单，所以数字不变）。

  **实际只做一处**：在 `:90`（`| \`get_unityexplorer_logs\` / \`clear_unityexplorer_logs\` | UE 日志查看 / 清空 |`）之后、`**RIMAPI 游戏数据（14， 依赖 RIMAPI…` 之前，插入：

```markdown
| `list_harmony_patches` | 运行时 Harmony 补丁查询：**哪些方法被谁 patch 了**。读游戏进程内的补丁表（静态 `HarmonyLib.Harmony.GetAllPatchedMethods()`）。**默认关闭，经 `agg_call_tool` 调用** |

#### ⚠️ 装了 Harmony 补丁的模组：`pawn.CanReach(...)` 可能已经不是原版语义

**现象（零信号）**：某模组给 `Verse.ReachabilityUtility.CanReach` 打了 `return false` 型 Prefix 后，测试代码里
`pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)` 拿到的不是原版地面可达性，而是模组自己的判定。
**编译器不报、运行时不报、`Player.log` 不打、反射枚举也看不出来** —— Harmony 只在运行时把委托指针换掉，
方法签名与元数据一个字没变。

**⚠️ 两条已被推翻的写法（照抄会得出事实相反的结论）：**

| 常见写法 | 裁定 |
|---|---|
| 「绕开扩展方法直接调 `map.reachability.CanReach(a, b, PathEndMode.OnCell, TraverseParms.For(TraverseMode.ByPawn))`」 | ❌ **不成立**。该判定链上共 4 个 vanilla 方法被 patch（含 `Reachability.CanReach` 本身，见 `ReachabilityPatches.cs:99`），**不存在任何「原版语义」入口** |
| 「`Harmony.GetPatchedMethods()`」 | ❌ **返回 0**。它是**实例方法且按 owner 作用域**，任意新实例枚举不到别的 owner 的补丁。必须用**静态 `HarmonyLib.Harmony.GetAllPatchedMethods()`**（见 `Source/UELoader/HotReloadValidator.cs:383-387` 的实测注释：实例 0 / 静态 495） |

**今天的绕法（零代码，`execute_csharp_code` 直接可跑）**：

```csharp
var sb = new System.Text.StringBuilder();
foreach (var m in HarmonyLib.Harmony.GetAllPatchedMethods())
{
    var info = HarmonyLib.Harmony.GetPatchInfo(m);
    if (info == null) continue;
    foreach (var p in info.Prefixes)
        sb.AppendLine(m.DeclaringType.FullName + "." + m.Name
            + "  <-Prefix  " + (p.PatchMethod == null ? "?" : p.PatchMethod.DeclaringType.FullName));
}
sb.ToString();
```

**有工具后的正解**：`list_harmony_patches`（默认关闭，经 `agg_call_tool { tool: "list_harmony_patches", args: { query: "Reachability" } }`）。
零结果时看 `totalPatchedTargets` —— 它告诉你「枚举到多少个被 patch 的目标」，**空数组 ≠ 没有补丁**。

**⚠️ 提示语只能是「此 API 已被 N 个 Harmony patch 覆盖，语义可能不是原版」**，
**不能**写「语义已被替换成 X」—— 补丁前缀内部还有守卫条件（如
`FlightPawnConfig.EnableInterception && pawn.Spawned && HasFlightCapability(pawn)`），守卫不成立时走的仍是原版逻辑。
任何把 `pawn.CanReach` 当「静态可达性判据」写进注释的地方，都应改注明「经 `list_harmony_patches` 确认未被 patch」或「语义可能被覆盖」。
```

- [ ] **步骤 4：`AI-DEPLOYMENT.md` 的两处改动**

在 §8 排障决策树表格（`AI-DEPLOYMENT.md:166-177`，最后一行是 `| 改 \`toolConfig.json\` 不生效 | 需重启 | 重启 MCP 或重连会话 |`）之后追加两行：

```markdown
| 调试结果「看起来像原版语义」却没有依据 | **方法可能被 Harmony patch 接管**（编译器/运行时/Player.log 全部零信号） | 先 `agg_call_tool { tool: "list_harmony_patches", args: { query: "<方法名>" } }`；查不到再用 §0.2 的 `GetAllPatchedMethods()` 片段手工确认。零结果要看 `totalPatchedTargets`，**空数组 ≠ 没有补丁** |
| `list_harmony_patches` 报连接失败 / `HARMONY_UNAVAILABLE` | 游戏未运行，或新 `Assemblies/UELoader.dll` 未生效 | 本工具读的是**游戏进程内**的运行时补丁表，**游戏未运行时无法回答 ≠ 该方法没有补丁**；若游戏在跑仍失败，多半是没重启游戏加载新 DLL（构建必须先停游戏） |
```

并在 §9「一句话提醒」（`AI-DEPLOYMENT.md:185-190`）的列表**末尾**（`:190` 那条 `- 本模组为个人调试用途发布，非官方，与 Ludeon Studios 无关联。` 之后）追加一条：

```markdown
- 查「某方法有没有被 Harmony patch」只能用**静态** `HarmonyLib.Harmony.GetAllPatchedMethods()`：实例 `Harmony.GetPatchedMethods()` 按 owner 作用域，任意新实例返回 0（实测 0 / 495）。
```

- [ ] **步骤 5：运行测试验证通过**

运行：`Set-Location MCP; node --test test/harmonyPatchesTool.test.js`
预期：4 个测试全部 **PASS**。

- [ ] **步骤 6：改动清单（替代 commit —— 本仓库不是 git 仓库）**

| 文件 | 改动 |
|---|---|
| `README.md` | UE 工具表后新增 1 行工具 + 新增 1 个 `####` 小节（含 2 行表格 + 1 段 C# 片段 + 2 段提示） |
| `AI-DEPLOYMENT.md` | §8 表格 +2 行；§9 列表 +1 条 |
| `MCP/test/harmonyPatchesTool.test.js` | **新建**（本任务先放 4 条文案断言；任务 3 / 5 继续往里加） |

> ⚠️ **本任务全程不编译、不启动游戏、不改任何 `.cs`。** 执行完这两份文档对当前并行会话零影响。

### 任务 2：纯逻辑 `UEHarmonyPatchIndex.cs` + 离线测试（零 Harmony / 零 Verse 依赖）

**文件：**
- 新建：`Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj`
- 新建：`Source/HarmonyPatchIndexTests/Program.cs`
- 新建：`Source/UELoader/UEHarmonyPatchIndex.cs`
- 测试：`Source/HarmonyPatchIndexTests/Program.cs`（自包含 `Check()` runner，形状照抄 `Source/ConfigJsonTests/Program.cs:20-26`）

- [ ] **步骤 1：新建测试工程（此时还不 link 被测文件，先让它能编译空壳）**

`Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <RootNamespace>HarmonyPatchIndexTests</RootNamespace>
    <AssemblyName>HarmonyPatchIndexTests</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- 直接 link 被测源码，避免测试工程引用 UELoader 程序集（其依赖 Verse/Unity） -->
    <Compile Include="..\UELoader\UEHarmonyPatchIndex.cs" Link="UEHarmonyPatchIndex.cs" />
  </ItemGroup>
</Project>
```

`Source/HarmonyPatchIndexTests/Program.cs`（**先只写 runner 骨架 + 4 条占位断言，步骤 3 才补实现**）：

```csharp
using System;
using System.Collections.Generic;
using UELoader;

namespace HarmonyPatchIndexTests
{
    /// <summary>
    /// UEHarmonyPatchIndex 纯逻辑离线测试（零 Harmony / 零 Verse / 零 Reflection）。
    /// 运行：dotnet run --project Source/HarmonyPatchIndexTests
    /// </summary>
    internal static class Program
    {
        static int failures;

        static void Check(bool cond, string label)
        {
            Console.WriteLine((cond ? "[PASS] " : "[FAIL] ") + label);
            if (!cond) failures++;
        }

        /// <summary>构造一行样本。字段名与 UEHarmonyPatchRow 逐字对应。</summary>
        static HarmonyPatchRow Row(string declaringType, string methodName, string paramTypes,
                                   string kind, string patchMethod, string patchAssembly,
                                   string owner, bool vanilla = true)
        {
            return new HarmonyPatchRow
            {
                DeclaringTypeFullName = declaringType,
                MethodName = methodName,
                ParamTypes = paramTypes,
                Kind = kind,
                PatchMethod = patchMethod,
                PatchAssembly = patchAssembly,
                Owner = owner,
                TargetIsVanilla = vanilla
            };
        }

        /// <summary>TEXT20 真实形态的样本：4 个 vanilla 可达性方法各被 patch，CanReach 有 3 条 Prefix。</summary>
        static List<HarmonyPatchRow> FlightAStarSample()
        {
            const string owner = "com.text20.flightastar";
            var rows = new List<HarmonyPatchRow>
            {
                Row("Verse.ReachabilityUtility", "CanReach",
                    "(Verse.Pawn, Verse.LocalTargetInfo, Verse.PathEndMode, Verse.Danger, System.Boolean, System.Boolean, Verse.TraverseMode)",
                    "Prefix", "FlightPawn2Framework.PathPlan.FlightAStar.Patch_ReachabilityUtility_CanReach_Flight.Prefix",
                    "TEXT20.Unity", owner),
                Row("Verse.Reachability", "CanReach",
                    "(Verse.IntVec3, Verse.LocalTargetInfo, Verse.PathEndMode, Verse.TraverseParms)",
                    "Prefix", "FlightPawn2Framework.PathPlan.FlightAStar.Patch_Reachability_CanReach_Flight.Prefix",
                    "TEXT20.Unity", owner),
                Row("Verse.ReachabilityImmediate", "CanReachImmediate",
                    "(Verse.IntVec3, Verse.Map, Verse.AI, Verse.PathEndMode, Verse.TraverseParms)",
                    "Prefix", "FlightPawn2Framework.PathPlan.FlightAStar.Patch_ReachabilityImmediate_CanReachImmediate.Prefix",
                    "TEXT20.Unity", owner),
                Row("RimWorld.CompFood", "CompTick", "()", "Postfix",
                    "SomeOther.Mod.Patches.CompFoodPatch.CompTick_Postfix", "SomeOther", "com.someother"),
                Row("Verse.Map", "get_Label", "()", "Transpiler",
                    "SomeOther.Mod.Patches.MapLabelPatch.MapLabel_Transpiler", "SomeOther", "com.someother", false),
            };
            return rows;
        }

        static int Main()
        {
            Check(false, "步骤 1 占位：实现尚未写就绪（步骤 3 后本行改为真断言）");
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
```

- [ ] **步骤 2：运行确认它以「占位断言 FAIL」红**

运行：`dotnet run --project Source/HarmonyPatchIndexTests`
预期：`[FAIL] 步骤 1 占位…` 一行 + `1 FAILED`，退出码 1。（此时还 link 不到 `UEHarmonyPatchIndex.cs` 会编译失败 —— 那是正常的，**先把 `UEHarmonyPatchIndex.cs` 建出来空壳**再继续。）

- [ ] **步骤 3：先写全部失败的断言**

把 `Program.cs` 的 `Main()` 整体替换成下面这段（**只引用还不存在的 `HarmonyPatchIndex` / `HarmonyPatchRow`**，跑起来必然编译失败 = 红灯）：

```csharp
        static int Main()
        {
            var all = FlightAStarSample();

            // ① 空 query 不过滤，且六个计数自洽
            var r = HarmonyPatchIndex.Build(all, 495, null, 100);
            Check(r.Rows.Count == 5, "空 query 不过滤：5 行全回");
            Check(r.TotalPatchedTargets == 495, "totalPatchedTargets 原样透传");
            Check(r.TotalRows == 5, "totalRows = 全量行数");
            Check(r.MatchedRows == 5, "matchedRows = 命中行数");
            Check(r.MatchedTargets == 4, "matchedTargets = 命中行里 distinct target 数（4 个方法）");
            Check(r.ReturnedRows == 5, "returnedRows = 实际返回行数");
            Check(r.Truncated == false, "未截断时 truncated=false");
            Check(r.LookupScope == "runtime-harmony-patch-table", "lookupScope 常量正确");
            Check(r.Query == "", "query 原样回显（null 归一为空串）");

            // ② query 命中「类型」而不是「方法名」——Reachability 是 DeclaringType 的一部分
            var q = HarmonyPatchIndex.Build(all, 495, "Reachability", 100);
            Check(q.MatchedTargets == 3, "query=Reachability 命中 3 个 vanilla 方法（CanReach x2 + CanReachImmediate）");
            Check(q.MatchedRows == 3, "query=Reachability 命中 3 行");
            Check(q.Rows.TrueForAll(x => x.DeclaringTypeFullName.Contains("Reachability")), "命中行都属于可达性链");

            // ③ query 大小写不敏感
            var ci = HarmonyPatchIndex.Build(all, 495, "reachability", 100);
            Check(ci.MatchedRows == q.MatchedRows, "query 大小写不敏感");

            // ④ query 命中 patch 方法名与 owner
            Check(HarmonyPatchIndex.Build(all, 495, "com.someother", 100).MatchedRows == 2, "query 可命中 owner");
            Check(HarmonyPatchIndex.Build(all, 495, "FlightAStar", 100).MatchedRows == 3, "query 可命中 patch 方法全名");
            Check(HarmonyPatchIndex.Build(all, 495, "TEXT20.Unity", 100).MatchedRows == 3, "query 可命中 patch 程序集");

            // ⑤ 排序稳定：target 序 → kind 序（PatchKinds 声明顺序）→ patchMethod 序
            var sorted = HarmonyPatchIndex.Build(all, 495, null, 100);
            var keys = new List<string>();
            foreach (var row in sorted.Rows)
                keys.Add(row.DeclaringTypeFullName + "|" + row.MethodName + "|" + row.Kind + "|" + row.PatchMethod);
            var expect = new List<string>(keys);
            expect.Sort(StringComparer.Ordinal);
            Check(string.Join("\n", keys) == string.Join("\n", expect),
                "排序键（DeclaringType|Method|Kind|PatchMethod）与自身序数排序一致");

            var ru = HarmonyPatchIndex.Build(all, 495, "ReachabilityImmediate", 100);
            Check(ru.Rows.Count == 1 && ru.Rows[0].Kind == "Prefix", "单条命中时 kind 原样带回");

            // ⑥ limit 钳制：0/负数 → 1；>200 → 200；中间值原样
            Check(HarmonyPatchIndex.Build(all, 495, null, 0).Rows.Count == 1, "limit=0 → 钳到 1");
            Check(HarmonyPatchIndex.Build(all, 495, null, -5).Rows.Count == 1, "limit=-5 → 钳到 1");
            Check(HarmonyPatchIndex.Build(all, 495, null, 1).Truncated == true, "limit=1 时 truncated=true");
            Check(HarmonyPatchIndex.Build(all, 495, null, 3).Rows.Count == 3, "limit=3 生效");
            Check(HarmonyPatchIndex.Build(all, 495, null, 3).Truncated == true, "limit=3 < 5 → truncated=true");
            Check(HarmonyPatchIndex.Build(all, 495, null, 5).Truncated == false, "limit=5 == 全量 → truncated=false");
            Check(HarmonyPatchIndex.Build(all, 495, null, 200).Rows.Count == 5, "limit=200 正常返回全量");
            Check(HarmonyPatchIndex.Build(all, 495, null, 99999).Limit == 200, "limit 上限硬钳到 200（与 UEHttpServer.GetIntQuery 一致）");

            // ⑦ 空输入：rows 空 + query 命中不了 ⇒ 零结果仍带总量（反假阴性）
            var empty = HarmonyPatchIndex.Build(new List<HarmonyPatchRow>(), 0, "Reachability", 100);
            Check(empty.Rows.Count == 0, "空输入 rows 为空");
            Check(empty.TotalPatchedTargets == 0 && empty.TotalRows == 0, "空输入两个总量为 0");
            Check(empty.MatchedRows == 0 && empty.MatchedTargets == 0, "空输入命中数为 0");
            Check(empty.Truncated == false, "空输入不标截断");

            var nullRows = HarmonyPatchIndex.Build(null, 0, null, 100);
            Check(nullRows.Rows.Count == 0 && nullRows.TotalRows == 0, "null 行集合按空处理，不抛异常");

            // ⑧ query 为空/空白视作不过滤
            Check(HarmonyPatchIndex.Build(all, 495, "   ", 100).Rows.Count == 5, "query 为纯空白视作不过滤");

            // ⑨ vanilla 程序集判定
            Check(HarmonyPatchIndex.IsVanillaAssembly("Assembly-CSharp"), "Assembly-CSharp 判为 vanilla");
            Check(HarmonyPatchIndex.IsVanillaAssembly("UnityEngine.CoreModule"), "UnityEngine.* 判为 vanilla");
            Check(HarmonyPatchIndex.IsVanillaAssembly("UnityEngine"), "UnityEngine 判为 vanilla");
            Check(!HarmonyPatchIndex.IsVanillaAssembly("TEXT20.Unity"), "模组程序集不判为 vanilla");
            Check(!HarmonyPatchIndex.IsVanillaAssembly("UnityExplorer.STANDALONE.Mono"), "UnityExplorer 不是 vanilla");
            Check(!HarmonyPatchIndex.IsVanillaAssembly(null), "null 程序集名不判为 vanilla");
            Check(!HarmonyPatchIndex.IsVanillaAssembly(""), "空程序集名不判为 vanilla");

            // ⑩ 一致性守卫：RimWorld/Verse 类型都在 Assembly-CSharp 里（防有人改窄判定）
            Check(HarmonyPatchIndex.IsVanillaAssembly("Assembly-CSharp"),
                "Verse.* / RimWorld.* 的归属程序集仍是 Assembly-CSharp");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }
```

- [ ] **步骤 4：运行确认编译失败（红灯 = 类型还不存在）**

运行：`dotnet run --project Source/HarmonyPatchIndexTests`
预期：`CS0246: 未能找到类型或命名空间名 'HarmonyPatchIndex' / 'HarmonyPatchRow'`，非零退出。

- [ ] **步骤 5：写最少实现让它通过**

新建 `Source/UELoader/UEHarmonyPatchIndex.cs`（**全文如下，逐字使用**）：

```csharp
using System;
using System.Collections.Generic;

namespace UELoader
{
    /// <summary>
    /// 一行 =「某个目标方法 × 某个补丁种类 × 挂上去的 patch 方法」。
    ///
    /// 纯数据 + 纯逻辑：**零 HarmonyLib / 零 Verse / 零 Unity / 零 System.Reflection 依赖**。
    /// 取数（怎么拿到 MethodBase、怎么读 PatchInfo）全部在 UEHarmonyPatchSource.cs 里，
    /// 本文件只负责「已经拿到行之后怎么过滤、怎么排序、怎么计数」。
    /// 这是它能被 Source/HarmonyPatchIndexTests 离线 link 测试的唯一前提。
    ///
    /// 一行的语义严格限定为「**谁被 patch 了**」，**不回答「此刻走的是谁的逻辑」** ——
    /// 后者取决于补丁前缀内部的守卫条件（如 ReachabilityPatches.cs:24-25）。
    /// 任何据此生成的提示语都只能是「语义可能不是原版」，不能是「语义已被替换成 X」。
    /// </summary>
    public sealed class HarmonyPatchRow
    {
        /// <summary>目标方法声明类型全名，如 "Verse.ReachabilityUtility"。</summary>
        public string DeclaringTypeFullName;

        /// <summary>目标方法名，如 "CanReach"。</summary>
        public string MethodName;

        /// <summary>参数类型列表，形如 "(Verse.Pawn, Verse.LocalTargetInfo)"；无参为 "()"。</summary>
        public string ParamTypes;

        /// <summary>补丁种类：Prefix / Postfix / Finalizer / Transpiler / ILManipulator。</summary>
        public string Kind;

        /// <summary>patch 方法全名，如 "FlightPawn2Framework.…Patch_ReachabilityUtility_CanReach_Flight.Prefix"。</summary>
        public string PatchMethod;

        /// <summary>patch 方法所在程序集简名，如 "TEXT20.Unity"；取不到为 null。</summary>
        public string PatchAssembly;

        /// <summary>Harmony owner id（如 "com.text20.flightastar"）；取不到为 null。</summary>
        public string Owner;

        /// <summary>目标方法所在程序集是否为 vanilla（见 HarmonyPatchIndex.IsVanillaAssembly）。</summary>
        public bool TargetIsVanilla;

        /// <summary>目标方法的完整签名（由四个字段拼出），排序与显示都用它。</summary>
        public string TargetSignature
        {
            get { return DeclaringTypeFullName + "." + MethodName + ParamTypes; }
        }
    }

    /// <summary>查询结果。六个计数的作用是：让"零命中"永远不是孤立的空数组。</summary>
    public sealed class HarmonyPatchIndexResult
    {
        /// <summary>实际返回的行（已过滤、已排序、已截断）。</summary>
        public List<HarmonyPatchRow> Rows = new List<HarmonyPatchRow>();

        /// <summary>取数端枚举到的被 patch 目标方法总数（过滤前）。</summary>
        public int TotalPatchedTargets;

        /// <summary>展开成行之后的总行数（过滤前）。</summary>
        public int TotalRows;

        /// <summary>query 命中的行数。</summary>
        public int MatchedRows;

        /// <summary>query 命中的行里，去重后的目标方法数。</summary>
        public int MatchedTargets;

        /// <summary>实际返回行数（= Rows.Count）。</summary>
        public int ReturnedRows;

        /// <summary>true = 因为 limit 被截断过，调用方应加大 limit 再查。</summary>
        public bool Truncated;

        /// <summary>生效的 limit（已钳制）。</summary>
        public int Limit;

        /// <summary>归一后的 query（null/空白 → 空串）。</summary>
        public string Query = "";

        /// <summary>固定字面量：告诉调用方这些数据来自运行时补丁表，不是元数据。</summary>
        public string LookupScope = HarmonyPatchIndex.LookupScopeName;
    }

    /// <summary>
    /// Harmony 补丁索引的纯逻辑部分：过滤 / 排序 / 计数 / 钳制。零外部依赖。
    ///
    /// ⚠️ 反假阴性契约（新增字段/改动语义前先读）：
    ///   ① rows 为空时，TotalPatchedTargets / TotalRows 必须仍然如实带给调用方；
    ///   ② Truncated 只表示"被 limit 截断"，绝不表示"没有更多"之外的任何推断；
    ///   ③ 本类不判断"某方法此刻走的是谁的逻辑"，只回答"谁被 patch 了"。
    /// </summary>
    public static class HarmonyPatchIndex
    {
        /// <summary>响应里固定的 lookupScope 字面量。</summary>
        public const string LookupScopeName = "runtime-harmony-patch-table";

        /// <summary>limit 上限。与 Source/UELoader/UEHttpServer.cs 的 GetIntQuery 钳制上限保持一致。</summary>
        public const int MaxLimit = 200;

        /// <summary>
        /// 补丁种类的**声明顺序即排序顺序**（同名 kind 排前面）。
        /// ILManipulator 是 0Harmony 2.5.2 才有的第 5 类（HarmonyLib.Patches.ILManipulators），
        /// 旧版本没有该成员时取数侧会跳过它，但顺序表里保留位置，保证跨版本排序一致。
        /// </summary>
        public static readonly string[] PatchKinds =
        {
            "Prefix", "Postfix", "Finalizer", "Transpiler", "ILManipulator"
        };

        /// <summary>vanilla 程序集判定：RimWorld 本体在 Assembly-CSharp，Unity 在 UnityEngine*.dll。</summary>
        public static bool IsVanillaAssembly(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return false;
            if (string.Equals(assemblyName, "Assembly-CSharp", StringComparison.Ordinal)) return true;
            return assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal);
        }

        /// <summary>kind 在 PatchKinds 里的序号；未知 kind 排到末尾（不丢行）。</summary>
        static int KindOrder(string kind)
        {
            for (int i = 0; i < PatchKinds.Length; i++)
            {
                if (string.Equals(PatchKinds[i], kind, StringComparison.Ordinal)) return i;
            }
            return PatchKinds.Length;
        }

        /// <summary>query 命中判定：目标侧 4 个字段 + patch 侧 2 个字段，任一命中即算命中。</summary>
        static bool Matches(HarmonyPatchRow row, string query)
        {
            return Contains(row.DeclaringTypeFullName, query)
                || Contains(row.MethodName, query)
                || Contains(row.TargetSignature, query)
                || Contains(row.PatchMethod, query)
                || Contains(row.PatchAssembly, query)
                || Contains(row.Owner, query);
        }

        static bool Contains(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 主入口。allRows 为取数端已展开的全部行；totalPatchedTargets 为取数端枚举到的目标方法数
        /// （单独传入，因为"0 行"既可能是"没补丁"，也可能是"取数失败"，调用方必须能区分后者）。
        /// </summary>
        public static HarmonyPatchIndexResult Build(
            IList<HarmonyPatchRow> allRows, int totalPatchedTargets, string query, int limit)
        {
            var result = new HarmonyPatchIndexResult
            {
                TotalPatchedTargets = totalPatchedTargets < 0 ? 0 : totalPatchedTargets,
                Query = string.IsNullOrEmpty(query) || query.Trim().Length == 0 ? "" : query.Trim(),
                Limit = ClampLimit(limit)
            };

            var source = allRows ?? new List<HarmonyPatchRow>();
            result.TotalRows = source.Count;

            string effectiveQuery = result.Query;
            var matched = new List<HarmonyPatchRow>();
            var distinctTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (HarmonyPatchRow row in source)
            {
                if (row == null) continue;
                if (effectiveQuery.Length > 0 && !Matches(row, effectiveQuery)) continue;
                matched.Add(row);
                distinctTargets.Add(row.TargetSignature);
            }

            result.MatchedRows = matched.Count;
            result.MatchedTargets = distinctTargets.Count;

            matched.Sort(CompareRows);

            int take = Math.Min(matched.Count, result.Limit);
            for (int i = 0; i < take; i++) result.Rows.Add(matched[i]);
            result.ReturnedRows = result.Rows.Count;
            result.Truncated = take < matched.Count;

            return result;
        }

        /// <summary>排序：target 签名 → kind（PatchKinds 声明顺序）→ patch 方法 → owner。</summary>
        static int CompareRows(HarmonyPatchRow a, HarmonyPatchRow b)
        {
            int c = string.CompareOrdinal(a.TargetSignature, b.TargetSignature);
            if (c != 0) return c;
            c = KindOrder(a.Kind).CompareTo(KindOrder(b.Kind));
            if (c != 0) return c;
            c = string.CompareOrdinal(a.PatchMethod ?? "", b.PatchMethod ?? "");
            if (c != 0) return c;
            return string.CompareOrdinal(a.Owner ?? "", b.Owner ?? "");
        }

        /// <summary>limit 钳制：&lt;1 → 1；&gt;MaxLimit → MaxLimit。与 GetIntQuery 同口径。</summary>
        public static int ClampLimit(int limit)
        {
            if (limit < 1) return 1;
            if (limit > MaxLimit) return MaxLimit;
            return limit;
        }
    }
}
```

- [ ] **步骤 6：运行确认全绿**

运行：`dotnet run --project Source/HarmonyPatchIndexTests`
预期：`[PASS]` 逐条打印，末行 `ALL PASS`，退出码 0。断言条数 ≥ 30（步骤 3 的 10 组）。

- [ ] **步骤 7：回归 —— 确认没有碰坏既有 C# 离线测试**

运行：`dotnet run --project Source/HotReloadTests` 与 `dotnet run --project Source/ConfigJsonTests`
预期：两者都 `ALL PASS`。本任务只新增文件，理论上不可能影响它们；跑一遍是为了把"确实没影响"变成证据。

- [ ] **步骤 8：改动清单（替代 commit）**

| 文件 | 性质 |
|---|---|
| `Source/UELoader/UEHarmonyPatchIndex.cs` | **新建**（进入 `UELoader.dll` 编译面 —— 但本任务不构建，故游戏不受影响） |
| `Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj` | **新建** |
| `Source/HarmonyPatchIndexTests/Program.cs` | **新建** |

> ⚠️ **本任务不需要停游戏**：只新增源文件，不跑 `dotnet build Source/UELoader/UELoader.csproj`，因此 `Assemblies/UELoader.dll` 不变、正在跑的游戏不受影响。

### 任务 3：反射适配器 `UEHarmonyPatchSource.cs` + **真 Harmony 补丁往返离线测试**（本计划风险最高、回报最大的一步）

**这一步为什么关键：** 新工具最终必须"游戏在跑"才能验收，但**取数与整形这两层完全不需要游戏** —— 本任务在测试进程里真打一个 Harmony 补丁，再让新代码把它读回来。跑通它，就等于在**离线**条件下验证了：静态 `GetAllPatchedMethods()`、两级反射访问器、5 类补丁名探测、`owner` 取法、vanilla 判定、以及和任务 2 的过滤逻辑的接缝。剩下的只有「HTTP 路由 + MCP 转发」两层薄壳。

**文件：**
- 新建：`Source/UELoader/UEHarmonyPatchSource.cs`
- 修改：`Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj`（多 link 一个文件 + 加 `0Harmony` 引用）
- 修改：`Source/HarmonyPatchIndexTests/Program.cs`（拆成 `RunIndexTests()` / `RunSourceTests()`，新增往返用例）

- [ ] **步骤 1：测试工程加两个 link 与 0Harmony 引用**

把 `Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj` 全文替换为：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>disable</Nullable>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <RootNamespace>HarmonyPatchIndexTests</RootNamespace>
    <AssemblyName>HarmonyPatchIndexTests</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- 直接 link 被测源码，避免测试工程引用 UELoader 程序集（其依赖 Verse/Unity）。
         两个被测文件都只依赖 HarmonyLib + System.*，可脱离游戏编译。 -->
    <Compile Include="..\UELoader\UEHarmonyPatchIndex.cs" Link="UEHarmonyPatchIndex.cs" />
    <Compile Include="..\UELoader\UEHarmonyPatchSource.cs" Link="UEHarmonyPatchSource.cs" />
    <!-- 往返测试要真打一个 Harmony 补丁，故本工程需要 0Harmony 运行期引用（Private=true）。
         形状照抄 Source/HotReloadTests/HotReloadTests.csproj:15-18。 -->
    <Reference Include="0Harmony">
      <HintPath>..\UELoader\lib\0Harmony.dll</HintPath>
      <Private>true</Private>
    </Reference>
  </ItemGroup>
</Project>
```

- [ ] **步骤 2：先写会失败的往返测试**

把 `Source/HarmonyPatchIndexTests/Program.cs` 里 `Main` 的**首行**改成下面这样（其余部分见步骤 4）：

```csharp
        static int Main()
        {
            RunIndexTests();
            RunSourceTests();

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }
```

并在 `Program.cs` 的 `Main` 之前插入 `RunIndexTests()`（函数体 = 任务 2 步骤 3 那段 `Main` 的全部语句，从 `var all = FlightAStarSample();` 一直到 `Check(HarmonyPatchIndex.IsVanillaAssembly("Assembly-CSharp"), ...)` 那条 ⑩ 守卫，**末尾原本那两行 `Console.WriteLine()` 与 `return` 不要带进来**）：

```csharp
        /// <summary>任务 2 的纯逻辑断言组（内容不变，只是从 Main 抽出来）。</summary>
        static void RunIndexTests()
        {
            // …（同上：原 Main 的全部 Check 语句，一字不改）…
        }
```

紧接着插入往返测试组（**这是本任务的红灯来源** —— `HarmonyPatchSource` 还不存在）：

```csharp
        // ================================================================
        // 往返测试：在测试进程里真打一个 Harmony 补丁，验证取数适配器能读回来
        // ================================================================

        /// <summary>被打补丁的目标：一个平凡的普通类方法。</summary>
        static class FixtureTarget
        {
            public static int TargetMethod(int x)
            {
                return x + 1;
            }
        }

        /// <summary>补丁类：Prefix / Postfix / Transpiler 三种都挂上，覆盖多种 kind。</summary>
        [HarmonyLib.HarmonyPatch(typeof(FixtureTarget), nameof(FixtureTarget.TargetMethod))]
        static class FixturePatch
        {
            static void Prefix(int x)
            {
                FixtureState.PrefixHits++;
            }

            static void Postfix(int x)
            {
                FixtureState.PostfixHits++;
            }

            static void Transpiler(System.Collections.Generic.IList<HarmonyLib.CodeInstruction> il)
            {
                FixtureState.TranspilerHits++;
            }
        }

        static class FixtureState
        {
            public static int PrefixHits;
            public static int PostfixHits;
            public static int TranspilerHits;
        }

        static void RunSourceTests()
        {
            // ⓪ 先把补丁真的打上去 —— 没有补丁，下面所有断言都是空转
            var harmony = new HarmonyLib.Harmony("harmony.patchindex.tests");
            try
            {
                harmony.PatchAll(typeof(FixturePatch).Assembly);
            }
            catch (Exception ex)
            {
                Check(false, "PatchAll 应成功（实际异常: " + ex.Message + "）");
                return;
            }
            Check(true, "PatchAll 已把 FixturePatch 挂到 FixtureTarget.TargetMethod 上");

            // ① 静态入口必须拿得到，且不能走实例退回路径
            var snap = HarmonyPatchSource.Query();
            Check(snap != null, "Query() 返回非 null");
            Check(snap.LookupSource == HarmonyPatchSource.SourceStatic,
                "0Harmony 2.3.3 有静态版，应走 static-getAllPatchedMethods（实际: " + snap.LookupSource + "）");
            Check(snap.HarmonyVersion != null && snap.HarmonyVersion.Length > 0,
                "回报 0Harmony 版本号（实际: " + snap.HarmonyVersion + "）");
            Check(snap.TargetsFailed == 0, "取数过程无失败目标（TargetsFailed=" + snap.TargetsFailed + "）");

            // ② 补丁确实被打上了（否则下面的断言都是空转）
            int before = FixtureTarget.TargetMethod(1);
            Check(before == 2, "打补丁后 FixtureTarget.TargetMethod(1) 仍是 2（Prefix 不改 __result）");
            Check(FixtureState.PrefixHits == 1, "Prefix 被真实执行（PrefixHits=" + FixtureState.PrefixHits + "）");
            Check(FixtureState.PostfixHits == 1, "Postfix 被真实执行（PostfixHits=" + FixtureState.PostfixHits + "）");

            // ③ 核心：取数适配器必须把刚才那个补丁读回来
            Check(snap.TotalPatchedTargets >= 1,
                "静态 GetAllPatchedMethods 至少枚举到 1 个目标（实际: " + snap.TotalPatchedTargets + "）");
            Check(snap.Rows.Count >= 1, "至少展开出 1 行（实际: " + snap.Rows.Count + "）");

            var target = typeof(FixtureTarget).FullName;
            int prefixRows = snap.Rows.FindAll(r => r.DeclaringTypeFullName == target
                                                     && r.MethodName == nameof(FixtureTarget.TargetMethod)
                                                     && r.Kind == "Prefix").Count;
            Check(prefixRows == 1, "恰好读回 1 条 Prefix 行（实际: " + prefixRows + "）");

            int postfixRows = snap.Rows.FindAll(r => r.DeclaringTypeFullName == target
                                                      && r.Kind == "Postfix").Count;
            Check(postfixRows == 1, "恰好读回 1 条 Postfix 行（实际: " + postfixRows + "）");

            int transpilerRows = snap.Rows.FindAll(r => r.DeclaringTypeFullName == target
                                                         && r.Kind == "Transpiler").Count;
            Check(transpilerRows == 1, "恰好读回 1 条 Transpiler 行（实际: " + transpilerRows + "）");

            // ④ 字段完整性（这三条是 [28] 的验收用例形态：谁 patch 了 vanilla 方法）
            var row = snap.Rows.Find(r => r.DeclaringTypeFullName == target && r.Kind == "Prefix");
            Check(row != null, "Prefix 行存在");
            Check(row.PatchMethod != null && row.PatchMethod.Contains("FixturePatch.Prefix"),
                "patchMethod 含 FixturePatch.Prefix（实际: " + row.PatchMethod + "）");
            Check(row.PatchAssembly == "HarmonyPatchIndexTests",
                "patchAssembly 归属本测试程序集（实际: " + row.PatchAssembly + "）");
            Check(row.Owner == "harmony.patchindex.tests",
                "owner 读回 Harmony 实例 id（实际: " + row.Owner + "）");
            Check(row.ParamTypes == "(Int32)", "paramTypes 形如 (Int32)（实际: " + row.ParamTypes + "）");
            Check(row.TargetSignature == target + ".TargetMethod(Int32)",
                "TargetSignature 拼装正确（实际: " + row.TargetSignature + "）");
            Check(row.TargetIsVanilla == false,
                "测试程序集不判为 vanilla（IsVanillaAssembly 走的是同一判定）");

            // ⑤ 接缝：取数 → 纯逻辑，一个都不能少
            var indexed = HarmonyPatchIndex.Build(snap.Rows, snap.TotalPatchedTargets,
                "FixtureTarget", HarmonyPatchIndex.MaxLimit);
            Check(indexed.MatchedTargets >= 1, "索引层按 FixtureTarget 过滤命中 ≥1 个目标");
            Check(indexed.Rows.Count >= 1, "索引层至少返回 1 行");
            Check(indexed.TotalRows == snap.Rows.Count, "索引层 totalRows 与取数行数一致");

            var none = HarmonyPatchIndex.Build(snap.Rows, snap.TotalPatchedTargets,
                "NoSuchTypeAnywhere", HarmonyPatchIndex.MaxLimit);
            Check(none.Rows.Count == 0, "查不到时 rows 为空");
            Check(none.TotalPatchedTargets == snap.TotalPatchedTargets,
                "查不到时 totalPatchedTargets 仍如实带回（反假阴性）");

            // ⑥ ILManipulator 是 2.5.2 才有的第 5 类：2.3.3 上必须"不存在但不能报错"
            Check(HarmonyPatchIndex.PatchKinds.Length == 5, "PatchKinds 含 5 类（ILManipulator 在列）");
            Check(HarmonyPatchIndex.PatchKinds[4] == "ILManipulator", "第 5 类是 ILManipulator");
            Check(snap.Rows.TrueForAll(r => r.Kind != "ILManipulator"),
                "0Harmony 2.3.3 没有 ILManipulators 成员 ⇒ 不应产出该类行（且不抛异常）");

            // ⑦ 重复查询必须稳定（反射缓存不得串味）
            var again = HarmonyPatchSource.Query();
            Check(again.TotalPatchedTargets == snap.TotalPatchedTargets, "重复查询计数稳定");
            Check(again.Rows.Count == snap.Rows.Count, "重复查询行数稳定");
            Check(again.LookupSource == snap.LookupSource, "重复查询取数来源不变");

            // ⑧ 卸载后必须立刻读不到，且计数归零（证明查询是实时的，不是启动时快照）
            harmony.UnpatchSelf();
            var after = HarmonyPatchSource.Query();
            int afterRows = after.Rows.FindAll(r => r.DeclaringTypeFullName == target && r.Kind == "Prefix").Count;
            Check(afterRows == 0, "UnpatchSelf 之后再查读不到 Prefix 行（实际: " + afterRows + "）");
        }
```

- [ ] **步骤 3：运行确认编译失败（红灯 = `HarmonyPatchSource` 还不存在）**

运行：`dotnet run --project Source/HarmonyPatchIndexTests`
预期：`CS0246: 未能找到类型或命名空间名 'HarmonyPatchSource'`，非零退出。`RunIndexTests()` 那组断言此时**不会执行**（编译就断了），这是预期的红灯。

- [ ] **步骤 4：写最少实现让它通过**

新建 `Source/UELoader/UEHarmonyPatchSource.cs`（**全文如下，逐字使用**）：

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UELoader
{
    /// <summary>取数快照：一次运行时补丁表查询的原始结果 + 自曝取数路径。</summary>
    public sealed class HarmonyPatchSnapshot
    {
        /// <summary>展开后的全部行（未过滤未截断）。</summary>
        public List<HarmonyPatchRow> Rows = new List<HarmonyPatchRow>();

        /// <summary>枚举到的被 patch 目标方法数。与 Rows.Count 不同义：一个目标可能有多类/多条补丁。</summary>
        public int TotalPatchedTargets;

        /// <summary>GetPatchInfo 取数时抛异常的目标方法数（不吞，报出来）。</summary>
        public int TargetsFailed;

        /// <summary>取数来源，三态之一（见 HarmonyPatchSource.SourceXxx 常量）。</summary>
        public string LookupSource = HarmonyPatchSource.SourceUnavailable;

        /// <summary>实际加载的 0Harmony 程序集版本，如 "2.3.3.0"；取不到为 null。</summary>
        public string HarmonyVersion;
    }

    /// <summary>
    /// 运行时 Harmony 补丁表的取数适配器。
    ///
    /// ⚠️ 为什么**全部访问器都走反射**（这是本文件存在的唯一理由）：
    ///   实际加载的 0Harmony 由 ModsConfig 里第一个带 0Harmony 的模组决定，版本会漂。
    ///   实测三个真实副本：
    ///     2.3.1.1 / 2.3.3.0 → Patches.Prefixes… 是 public **字段**，无 ILManipulators
    ///     2.5.2.0           → 同上，**另有 ILManipulators**（第 5 类补丁）
    ///   且**三个版本都没有 Patch.Owners 属性**，只有 public 字段 Patch.owner。
    ///   任何写死成员名的代码都会在某个版本上 MissingField/MissingMethodException，
    ///   而 Harmony 的补丁表是**静默**的 —— 一次反射失败就变成新的"零信号"。
    ///   故本文件对 Harmony/PatchInfo/Patch 的成员一律「先属性、后字段」两级探测并缓存结果。
    ///
    /// ⚠️ 只在两处直接引用 HarmonyLib 类型（都已核实存在于 2.3.1.1 / 2.3.3.0 / 2.5.2.0）：
    ///   typeof(HarmonyLib.Harmony)（静态 GetAllPatchedMethods / GetPatchInfo）
    ///   new HarmonyLib.Harmony(id)（实例 GetPatchedMethods 退回路径）
    /// 其余全部是反射 —— 这样即使版本再漂，受影响的也只是"少报一类补丁"，不是"整个端点 500"。
    ///
    /// ⚠️ 取数**只在游戏进程内**有意义：Harmony 补丁不改动目标方法的元数据，
    /// Cecil / 离线反编译原理上不可能看见补丁。
    /// </summary>
    public static class HarmonyPatchSource
    {
        /// <summary>正常路径：静态 GetAllPatchedMethods()。</summary>
        public const string SourceStatic = "static-getAllPatchedMethods";

        /// <summary>退回路径：实例 GetPatchedMethods()。**owner 作用域，计数很可能不可信。**</summary>
        public const string SourceInstance = "instance-getPatchedMethods";

        /// <summary>两条入口都不存在：调用方必须报 HARMONY_UNAVAILABLE，绝不可当成"没有补丁"。</summary>
        public const string SourceUnavailable = "unavailable";

        /// <summary>实例退回路径用的 Harmony id（只是个名字，不影响语义）。</summary>
        const string ProbeOwnerId = "uesd.harmony-patch-index";

        /// <summary>
        /// 每类补丁在 Harmony/PatchInfo 上可能的成员名，按优先级排列：
        ///   1) PascalCase 复数（2.x 主形态，属性或字段都吃）
        ///   2) camelCase 复数（旧 PatchInfo 形态）
        /// ILManipulator 的成员名是复数 ILManipulators —— 写单数会静默取不到。
        /// </summary>
        static readonly Dictionary<string, string[]> KindMembers = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "Prefix",        new[] { "Prefixes",        "prefixes" } },
            { "Postfix",       new[] { "Postfixes",       "postfixes" } },
            { "Finalizer",     new[] { "Finalizers",      "finalizers" } },
            { "Transpiler",    new[] { "Transpilers",     "transpilers" } },
            { "ILManipulator", new[] { "ILManipulators",  "ilmanipulators" } },
        };

        /// <summary>Patch 上 patch 方法的成员名（2.x 是 public 属性 PatchMethod）。</summary>
        static readonly string[] PatchMethodMembers = { "PatchMethod", "patchMethod" };

        /// <summary>Patch 上 owner 的成员名（三个版本都没有 Owner 属性，只有 public 字段 owner）。</summary>
        static readonly string[] OwnerMembers = { "Owner", "owner" };

        static readonly object MemberCacheLock = new object();
        static readonly Dictionary<string, MemberInfo> MemberCache = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);

        /// <summary>成员反射缓存。缓存的是 MemberInfo 本身，取值时的异常不缓存（每次都重试）。</summary>
        static MemberInfo FindMember(Type type, string[] names)
        {
            foreach (string name in names)
            {
                string key = type.FullName + "::" + name;
                lock (MemberCacheLock)
                {
                    MemberInfo cached;
                    if (MemberCache.TryGetValue(key, out cached))
                    {
                        if (cached != null) return cached;
                        continue;   // 已确认不存在，别再找
                    }
                }

                MemberInfo found = null;
                try
                {
                    BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Instance | BindingFlags.Static;
                    found = (MemberInfo)type.GetProperty(name, flags)
                         ?? (MemberInfo)type.GetField(name, flags);
                }
                catch { found = null; }

                lock (MemberCacheLock) { MemberCache[key] = found; }
                if (found != null) return found;
            }
            return null;
        }

        static object ReadMember(object instance, MemberInfo member)
        {
            if (member == null || instance == null) return null;
            try
            {
                if (member is PropertyInfo p)
                {
                    // 属性 getter 抛异常（动态程序集卸载中）时不能让它冒泡成整个端点 500
                    return p.GetGetMethod(true) != null ? p.GetValue(instance, null) : null;
                }
                return (member as FieldInfo)?.GetValue(instance);
            }
            catch { return null; }
        }

        /// <summary>取一组被 patch 的目标方法，并自曝走的是哪条路径。</summary>
        static bool TryCollectTargets(out List<MethodBase> targets, out string source)
        {
            targets = null;
            source = SourceUnavailable;

            // ① 静态 GetAllPatchedMethods —— 唯一可信的入口
            try
            {
                MethodInfo mi = typeof(HarmonyLib.Harmony).GetMethod("GetAllPatchedMethods",
                    BindingFlags.Public | BindingFlags.Static);
                if (mi != null)
                {
                    var all = mi.Invoke(null, null) as IEnumerable;
                    if (all != null)
                    {
                        var list = new List<MethodBase>();
                        foreach (object o in all)
                        {
                            var mb = o as MethodBase;
                            if (mb != null) list.Add(mb);
                        }
                        targets = list;
                        source = SourceStatic;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                UEHttpLogWarning("[UEHarmonyPatch] 静态 GetAllPatchedMethods 调用失败: " + ex.Message);
            }

            // ② 实例 GetPatchedMethods 退回 —— 语义是 owner 作用域，计数不可信，必须自曝
            try
            {
                var instance = new HarmonyLib.Harmony(ProbeOwnerId);
                MethodInfo mi = typeof(HarmonyLib.Harmony).GetMethod("GetPatchedMethods",
                    BindingFlags.Public | BindingFlags.Instance)
                    ?? typeof(HarmonyLib.Harmony).GetMethod("GetPatchedMethods",
                        BindingFlags.Public | BindingFlags.Static);
                if (mi != null && mi.IsStatic)
                {
                    var all = mi.Invoke(null, null) as IEnumerable;
                    if (all != null)
                    {
                        var list = new List<MethodBase>();
                        foreach (object o in all)
                        {
                            var mb = o as MethodBase;
                            if (mb != null) list.Add(mb);
                        }
                        targets = list;
                        source = SourceInstance;
                        return true;
                    }
                }
                else if (mi != null)
                {
                    var all = mi.Invoke(instance, null) as IEnumerable;
                    if (all != null)
                    {
                        var list = new List<MethodBase>();
                        foreach (object o in all)
                        {
                            var mb = o as MethodBase;
                            if (mb != null) list.Add(mb);
                        }
                        targets = list;
                        source = SourceInstance;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                UEHttpLogWarning("[UEHarmonyPatch] 实例 GetPatchedMethods 退回失败: " + ex.Message);
            }

            targets = null;
            return false;
        }

        static void UEHttpLogWarning(string message)
        {
            // 取数层不能依赖 Verse.Log（UEHarmonyPatchSource 的其它部分刻意零 Verse 依赖），
            // 因此只走 System.Diagnostics.Debug；宿主 UEHttpHandler 会把降级原因写进响应。
            System.Diagnostics.Debug.WriteLine(message);
        }

        /// <summary>展开一个目标的补丁行。返回 false 表示取数失败（调用方负责 TargetsFailed 记账）。</summary>
        static bool AppendRows(MethodBase target, List<HarmonyPatchRow> rows)
        {
            MethodInfo getter = typeof(HarmonyLib.Harmony).GetMethod("GetPatchInfo",
                BindingFlags.Public | BindingFlags.Static);
            if (getter == null) return false;

            object patchInfo;
            try
            {
                patchInfo = getter.Invoke(null, new object[] { target });
            }
            catch
            {
                return false;
            }
            // patchInfo == null 是合法结果（该目标当前没有补丁），不算失败
            if (patchInfo == null) return true;

            Type infoType = patchInfo.GetType();
            string declaringType = target.DeclaringType?.FullName ?? target.DeclaringType?.Name ?? "";
            string methodName = target.Name;
            string paramTypes = FormatParams(target);
            string targetAssembly = SafeAssemblyName(target.DeclaringType);
            bool vanilla = HarmonyPatchIndex.IsVanillaAssembly(targetAssembly);

            foreach (KeyValuePair<string, string[]> kind in KindMembers)
            {
                MemberInfo member = FindMember(infoType, kind.Value);
                if (member == null) continue;   // 该版本没有这一类（如 2.3.x 的 ILManipulator）
                var list = ReadMember(patchInfo, member) as IEnumerable;
                if (list == null) continue;

                foreach (object patch in list)
                {
                    if (patch == null) continue;
                    Type patchType = patch.GetType();

                    var patchMethod = ReadMember(patch, FindMember(patchType, PatchMethodMembers)) as MethodInfo;
                    // 取不到 patch 方法的行没有信息量，跳过（不算 TargetsFailed）
                    if (patchMethod == null) continue;

                    string owner = ReadMember(patch, FindMember(patchType, OwnerMembers)) as string;
                    string patchAssembly = SafeAssemblyName(patchMethod.DeclaringType);

                    rows.Add(new HarmonyPatchRow
                    {
                        DeclaringTypeFullName = declaringType,
                        MethodName = methodName,
                        ParamTypes = paramTypes,
                        Kind = kind.Key,
                        PatchMethod = patchMethod.DeclaringType?.FullName + "." + patchMethod.Name,
                        PatchAssembly = patchAssembly,
                        Owner = owner,
                        TargetIsVanilla = vanilla
                    });
                }
            }
            return true;
        }

        static string SafeAssemblyName(Type type)
        {
            try { return type?.Assembly?.GetName()?.Name; }
            catch { return null; }
        }

        /// <summary>参数列表渲染："(Verse.Pawn, Verse.IntVec3)"；无参 "()"；取不到 null。</summary>
        static string FormatParams(MethodBase method)
        {
            try
            {
                ParameterInfo[] ps = method.GetParameters();
                var parts = new List<string>(ps.Length);
                foreach (ParameterInfo p in ps) parts.Add(p.ParameterType.FullName ?? p.ParameterType.Name);
                return "(" + string.Join(", ", parts) + ")";
            }
            catch { return null; }
        }

        /// <summary>查一次运行时补丁表。任何异常都被收敛成"取数失败"，不冒泡成端点 500。</summary>
        public static HarmonyPatchSnapshot Query()
        {
            var snap = new HarmonyPatchSnapshot();
            try
            {
                snap.HarmonyVersion = typeof(HarmonyLib.Harmony).Assembly.GetName().Version.ToString();
            }
            catch { snap.HarmonyVersion = null; }

            List<MethodBase> targets;
            string source;
            if (!TryCollectTargets(out targets, out source))
            {
                snap.LookupSource = SourceUnavailable;
                return snap;
            }

            snap.LookupSource = source;
            snap.TotalPatchedTargets = targets.Count;

            foreach (MethodBase target in targets)
            {
                if (!AppendRows(target, snap.Rows)) snap.TargetsFailed++;
            }

            return snap;
        }
    }
}
```

- [ ] **步骤 5：运行确认全绿**

运行：`dotnet run --project Source/HarmonyPatchIndexTests`
预期：`[PASS]` 逐条打印（两组共 ≥ 55 条），末行 `ALL PASS`，退出码 0。

**若 `[FAIL] 0Harmony 2.3.3 有静态版，应走 static-getAllPatchedMethods` 失败**：说明本机 `Source\UELoader\lib\0Harmony.dll` 被换成了没有静态版的版本。**不要改代码去迁就** —— 按 §0.0 表核对 `Source\Directory.Build.props:17` 的 `$(HarmonyRefDir)` 指向，然后按 `Source/UELoader/UELoader.csproj:68-73` 的既有约定「vendor 副本版本必须恒等于 brrainz.harmony 运行面版本」把 vendor 副本换回匹配版本，再重跑。

- [ ] **步骤 6：回归 —— 确认没有碰坏既有 C# 离线测试**

运行：`dotnet run --project Source/HotReloadTests` 与 `dotnet run --project Source/ConfigJsonTests`
预期：两者都 `ALL PASS`。

- [ ] **步骤 7：改动清单（替代 commit）**

| 文件 | 性质 |
|---|---|
| `Source/UELoader/UEHarmonyPatchSource.cs` | **新建** |
| `Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj` | **改**（多 link 一个文件 + `0Harmony` `Private=true`） |
| `Source/HarmonyPatchIndexTests/Program.cs` | **改**（拆两组 + 往返用例 + `FixtureTarget`/`FixturePatch`/`FixtureState`） |

> ⚠️ **本任务同样不需要停游戏**：`Source/HarmonyPatchIndexTests` 是独立 net472 `Exe`，跑它**不会**触发 `UELoader.csproj:91-96` 的 `DeployToAssemblies`，`Assemblies/UELoader.dll` 不变。

### 任务 4：UE 端点 `GET /harmony/patches`（**需要停游戏构建**）

**文件：**
- 修改：`Source/UELoader/UEHttpHandler.cs:161` 之后（新增 `ListHarmonyPatches`）
- 修改：`Source/UELoader/UEHttpServer.cs:408` 之后（新增 `case "harmony/patches"`）
- 修改：`Source/UELoader/CompatibilityHooks.cs:99` 之后（补两项 API 存在性检查）

- [ ] **步骤 1：先写 `ListHarmonyPatches` 的响应契约测试（离线可测部分）**

`ListHarmonyPatches` 本身跑在游戏里，不能离线调用。但它的**失败分支**可以离线钉住：把这条断言加进 `Source/HarmonyPatchIndexTests/Program.cs` 的 `RunSourceTests()` 末尾（Harmony 一定可用，所以这里测的是"可用路径下 `TargetsFailed` 必须为 0 且响应可序列化"）：

```csharp
            // ⑨ 响应契约：UELightJson 必须能把我们这份快照原样序列化（端点返回的就是它）
            var payload = new Dictionary<string, object>
            {
                { "lookupScope", HarmonyPatchIndex.LookupScopeName },
                { "harmonyVersion", snap.HarmonyVersion },
                { "totalPatchedTargets", snap.TotalPatchedTargets },
                { "targetsFailed", snap.TargetsFailed },
            };
            var rowDicts = new List<Dictionary<string, object>>();
            foreach (var rr in snap.Rows)
                rowDicts.Add(new Dictionary<string, object> {
                    { "target", rr.TargetSignature }, { "kind", rr.Kind },
                    { "patchMethod", rr.PatchMethod }, { "targetIsVanilla", rr.TargetIsVanilla } });
            payload["rows"] = rowDicts;
            string json = UELightJson.Serialize(payload);
            Check(json.Contains("\"lookupScope\":\"runtime-harmony-patch-table\""), "快照可被 UELightJson 序列化");
            Check(json.Contains("\"rows\":["), "rows 序列化为数组");
            Check(json.Contains("\"targetsFailed\""), "targetsFailed 出现在响应里（不吞）");
```

并在测试工程 csproj 的 `ItemGroup` 里**再加一行 link**（`UELightJson.cs` 是零依赖纯逻辑，`Source/ConfigJsonTests/ConfigJsonTests.csproj:13` 已经这么用）：

```xml
    <Compile Include="..\UELoader\UELightJson.cs" Link="UELightJson.cs" />
```

- [ ] **步骤 2：运行确认新断言先红**

运行：`dotnet run --project Source/HarmonyPatchIndexTests`
预期：`CS0246: 未能找到类型或命名空间名 'UELightJson'`（还没 link），非零退出。

- [ ] **步骤 3：加 link，重跑确认只剩真实断言**

运行：`dotnet run --project Source/HarmonyPatchIndexTests`
预期：`ALL PASS`。

- [ ] **步骤 4：写 `ListHarmonyPatches`**

在 `Source/UELoader/UEHttpHandler.cs` 的 `:161`（`InspectType` 的收尾 `}`）之后插入：

```csharp
        /// <summary>
        /// 列出当前**游戏进程内**被 Harmony patch 的目标方法（吐槽板 [28]）。
        ///
        /// ⚠️ 能力边界（文案与文档必须与此一致）：本方法只回答「**谁被 patch 了**」，
        /// **不回答「此刻走的是谁的逻辑」** —— 后者取决于补丁前缀内部的守卫条件
        /// （如 ReachabilityPatches.cs:24-25）。因此返回体里刻意**没有**「语义已被替换成 X」这种字段。
        ///
        /// ⚠️ 反假阴性：rows 为空时仍带 totalPatchedTargets / totalRows / lookup 块；
        /// 取数不可用时返回 errorCode=HARMONY_UNAVAILABLE，**绝不返回空列表冒充「没有补丁」**。
        ///
        /// ⚠️ 不查 UIManager（对照 InspectType 的 :125-126）：主菜单即可用 —— 「某个 vanilla 方法
        /// 有没有被 patch」恰恰是主菜单最常问的问题，且 Harmony 补丁在启动期就已加载完毕。
        /// </summary>
        public static Dictionary<string, object> ListHarmonyPatches(string query, int limit)
        {
            HarmonyPatchSnapshot snap;
            try
            {
                snap = HarmonyPatchSource.Query();
            }
            catch (Exception ex)
            {
                return Error(500, "Harmony 补丁表查询失败: " + ex.Message, "HARMONY_QUERY_ERROR",
                    new Dictionary<string, object>
                    {
                        { "lookupScope", HarmonyPatchIndex.LookupScopeName },
                        { "answerAvailable", false }
                    });
            }

            if (snap.LookupSource == HarmonyPatchSource.SourceUnavailable)
                return Error(503,
                    "本进程加载的 0Harmony 既无静态 GetAllPatchedMethods 也无可用的实例 GetPatchedMethods，无法回答「谁被 patch 了」。"
                    + "注意：这**不等于**该方法没有补丁。",
                    "HARMONY_UNAVAILABLE",
                    new Dictionary<string, object>
                    {
                        { "lookupScope", HarmonyPatchIndex.LookupScopeName },
                        { "harmonyVersion", snap.HarmonyVersion },
                        { "answerAvailable", false }
                    });

            HarmonyPatchIndexResult r = HarmonyPatchIndex.Build(snap.Rows, snap.TotalPatchedTargets, query, limit);

            var rows = new List<Dictionary<string, object>>(r.Rows.Count);
            foreach (HarmonyPatchRow row in r.Rows)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "target", row.TargetSignature },
                    { "declaringType", row.DeclaringTypeFullName },
                    { "methodName", row.MethodName },
                    { "paramTypes", row.ParamTypes },
                    { "kind", row.Kind },
                    { "patchMethod", row.PatchMethod },
                    { "patchAssembly", row.PatchAssembly },
                    { "owner", row.Owner },
                    { "targetIsVanilla", row.TargetIsVanilla }
                });
            }

            return Success(new Dictionary<string, object>
            {
                { "lookupScope", r.LookupScope },
                { "lookup", new Dictionary<string, object>
                    {
                        { "source", snap.LookupSource },
                        { "instanceFallbackWarning", snap.LookupSource == HarmonyPatchSource.SourceInstance
                            ? "当前 0Harmony 无静态 GetAllPatchedMethods，已退回实例 GetPatchedMethods()——它是 owner 作用域枚举，计数很可能不完整，**不能据此断言「没有别的补丁」**"
                            : null },
                        { "harmonyVersion", snap.HarmonyVersion },
                        { "targetsFailed", snap.TargetsFailed }
                    }
                },
                { "query", r.Query },
                { "limit", r.Limit },
                { "totalPatchedTargets", r.TotalPatchedTargets },
                { "totalRows", r.TotalRows },
                { "matchedTargets", r.MatchedTargets },
                { "matchedRows", r.MatchedRows },
                { "returnedRows", r.ReturnedRows },
                { "truncated", r.Truncated },
                { "rows", rows },
                { "notice", "本表回答「谁被 patch 了」，**不回答**「此刻走的是谁的逻辑」——后者取决于补丁前缀内部的守卫条件。"
                          + "零结果时先看 totalPatchedTargets：空数组 ≠ 没有补丁。" }
            });
        }
```

- [ ] **步骤 5：加路由**

在 `Source/UELoader/UEHttpServer.cs` 的 `:408`（`case "rimbridge/status":` 块的收尾 `}`）之后插入：

```csharp
                    // ---------- Harmony 运行时补丁表（吐槽板 [28]；读游戏进程内的补丁表，主菜单即可用） ----------
                    case "harmony/patches":
                        result = GetGuard(request, () => UEHttpHandler.ListHarmonyPatches(
                            GetQueryString(request, "query"),
                            GetIntQuery(request, "limit", 100)));
                        break;
```

> ⚠️ **`GetIntQuery` 在 `UEHttpServer.cs:680-686` 里硬钳到 `[1, 200]`**（`Math.Min(Math.Max(1, parsed), 200)`）。这就是任务 2 里 `HarmonyPatchIndex.MaxLimit = 200` 的来源 —— 两处必须同口径，不要在 `GetIntQuery` 之外另开一个上限不同的解析器。

- [ ] **步骤 6：补 `CompatibilityHooks` 的 API 存在性检查**

在 `Source/UELoader/CompatibilityHooks.cs` 的 `:99` 之后插入：

```csharp
            // 2026-10-02 吐槽板 [28]：list_harmony_patches 端点依赖这两个静态入口。
            // 缺任何一个都会让「谁被 patch 了」退化成不可查（不是「没有补丁」），因此提前报出来。
            if (harmonyAssembly.GetType("HarmonyLib.Harmony")?.GetMethod("GetPatchInfo") == null)
                missing.Add("Harmony.GetPatchInfo(MethodBase)");
            if (harmonyAssembly.GetType("HarmonyLib.Harmony")?.GetMethod("GetAllPatchedMethods") == null)
                missing.Add("Harmony.GetAllPatchedMethods()（缺它只能退回 owner 作用域的实例版，计数不可信）");
```

- [ ] **步骤 7：构建（**必须先关闭游戏**）**

1. 先确认游戏进程已退出（`Get-Process RimWorldWin64 -ErrorAction SilentlyContinue` 应无输出）。
2. 运行：`dotnet build Source/UELoader/UELoader.csproj`
   预期：`Build succeeded`，且 `Assemblies\UELoader.dll` / `Assemblies\UELoader.pdb` 的时间戳已更新（由 `UELoader.csproj:91-96` 的 `DeployToAssemblies` 自动拷贝）。

**若报 `CS0246`/`MissingMethod` 之类与 Harmony 相关的编译错误**：多半是 `$(HarmonyRefDir)`（`Source/Directory.Build.props:17`）指向的 vendor 副本与代码期望的 API 面不一致。按 `Source/UELoader/UELoader.csproj:68-73` 的既有约定处理，**不要为了让它编译过而删掉降级检查**。

- [ ] **步骤 8：改动清单（替代 commit）**

| 文件 | 改动 |
|---|---|
| `Source/UELoader/UEHttpHandler.cs` | +1 方法（`:161` 之后，约 80 行） |
| `Source/UELoader/UEHttpServer.cs` | +1 case（`:408` 之后，5 行） |
| `Source/UELoader/CompatibilityHooks.cs` | +2 条检查（`:99` 之后，4 行） |
| `Source/HarmonyPatchIndexTests/Program.cs` | +3 条序列化断言 +1 个 link |
| `Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj` | +1 个 link |
| `Assemblies/UELoader.dll` / `.pdb` | 构建产物（`UELoader.csproj:91-96` 自动生成） |

> ⚠️ 步骤 7 之后**必须重启游戏**端点才存在。重启与实测统一放在任务 6。

### 任务 5：MCP 工具 `list_harmony_patches` + 五处登记 + 两份语言文件

**文件：**
- 修改：`MCP/index.js`（`:2490`、`:2898`、`:2926`、`:2935`、`:3793`、`:4527` 六处）
- 修改：`MCP/toolConfig.json`
- 修改：`Languages/ChineseSimplified/Keyed/UESDdebuger.xml`、`Languages/English/Keyed/UESDdebuger.xml`
- 测试：`MCP/test/harmonyPatchesTool.test.js`（追加断言）

> ⚠️ **本任务不需要停游戏、不需要编译**：只改 `MCP/` 下的 Node 侧文件。改完**需要重启 MCP 进程**才生效（`AI-DEPLOYMENT.md:177` 的既有结论）。

- [ ] **步骤 1：先写会失败的测试**

在 `MCP/test/harmonyPatchesTool.test.js` 末尾追加：

```javascript
const TOOL = 'list_harmony_patches';

// ===========================================================================
// 工具注册（任务 5）
// ===========================================================================
test('已登记进 TOOLS / TOOL_CATEGORY，且描述写明静态入口与能力边界', async () => {
  const detail = await parse(await withCallText('mcp_help', { tool: TOOL }));
  assert.equal(detail.name, TOOL);
  assert.equal(detail.category, 'unityexplorer', '必须归 unityexplorer，否则概览计数与叶子列表会不一致');
  assert.match(detail.description, /GetAllPatchedMethods/, '描述必须点名静态入口');
  assert.match(detail.description, /语义可能不是原版/, '描述必须是保守提示语');
  assert.ok(!/语义已被替换成/.test(detail.description), '不得断言"语义已被替换成 X"');
  assert.match(detail.description, /GetPatchedMethods\(\)/, '描述要点名被否的实例版，避免调用方照抄');
  assert.equal(detail.inputSchema.properties.query.type, 'string');
  assert.equal(detail.inputSchema.properties.limit.type, 'integer');
  assert.equal(detail.inputSchema.properties.limit.maximum, 200, 'limit 上限必须与 UE 侧 GetIntQuery 同口径');
  assert.equal(detail.inputSchema.required, undefined, '两个参数都可省略：无参=列出全表');
  assert.ok(detail.inputSchema.additionalProperties === undefined,
    '不声明 additionalProperties:false（实测多余参数会被静默忽略，声明与行为不符）');
});

test('前置条件写明"主菜单即可、不依赖 UE 面板"', async () => {
  const detail = await parse(await withCallText('mcp_help', { tool: TOOL }));
  assert.match(detail.precondition, /主菜单/);
  assert.ok(!/需进入地图/.test(detail.precondition), '本工具刻意不进图，描述不得写"需进入地图"');
});

test('USAGE_NOTES 含可粘贴的调用范式与两条订正', async () => {
  const detail = await parse(await withCallText('mcp_help', { tool: TOOL }));
  assert.match(detail.example, /list_harmony_patches/);
  assert.match(detail.notes, /GetAllPatchedMethods/);
  assert.match(detail.notes, /map\.reachability\.CanReach/);
  assert.match(detail.notes, /不成立/);
});

test('菜单：概览 / 叶子 / 菜单宇宙三处口径一致', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  assert.ok(root.categories.find((c) => c.path === 'unityexplorer').count >= 1);
  const leaf = await parse(mod.mcpHelpResult({ path: 'unityexplorer' }));
  assert.ok(leaf.tools.some((l) => new RegExp(`^-\\s*\\[[^\\]]*\\]\\s+${TOOL}\\s`).test(l)),
    'unityexplorer 叶子列表必须出现该工具');
  assert.ok(mod.menuUniverseNames().includes(TOOL), '菜单宇宙必须包含该工具');
});

test('默认关闭（不直接暴露）但仍可 agg 调用', async () => {
  const toolConfig = JSON.parse(readText('MCP/toolConfig.json'));
  assert.equal(toolConfig.tools[TOOL], false, '应与 inspect_type 同档：默认不直接暴露');
  const exposed = mod.toolAvailability(TOOL);
  assert.notEqual(exposed.state, 'unavailable', '名字正确，不能报"提供方未就绪"');
});

test('两份语言 XML 都有 ToolDesc（游戏内设置界面要显示说明）', () => {
  for (const lang of ['ChineseSimplified', 'English']) {
    const xml = readText(`Languages/${lang}/Keyed/UESDdebuger.xml`);
    assert.ok(xml.includes(`UESDdebuger.ToolDesc.${TOOL}`), `${lang} 缺 ToolDesc.${TOOL}`);
  }
});

// ===========================================================================
// 转发契约（任务 5）
// ===========================================================================
test('转发：GET /harmony/patches，query 走 URL 编码，limit 钳到 [1,200]', () => {
  const src = readText('MCP/index.js');
  const i = src.indexOf(`case '${TOOL}': {`);
  assert.ok(i > 0, '找不到转发 case');
  const block = src.slice(i, src.indexOf("case 'execute_csharp_code': {", i));
  assert.match(block, /callUnityExplorerAPI\(\s*`\/harmony\/patches\$\{qs\}`,\s*'GET'\s*\)/);
  assert.match(block, /encodeURIComponent/, 'query 必须 URL 编码');
  assert.match(block, /Math\.min\(Math\.max\(1,/, 'limit 必须钳到下限 1');
  assert.match(block, /200/, 'limit 必须钳到上限 200');
  assert.ok(!/requireStage/.test(block),
    '刻意不加 requireStage：主菜单即可用。加了就与 precondition 文案矛盾');
  assert.match(block, /guidance/, '失败分支必须附 guidance');
  assert.match(block, /answerAvailable|HARMONY_UNAVAILABLE|GAME_NOT_RUNNING|不等于/, '失败文案必须写明"不可得 ≠ 没有补丁"');
});

// ===========================================================================
// 回归守卫：不破坏既有源码切片断言（AGENT-BRIEF §3.5 的行号纪律在测试里的对应物）
// ===========================================================================
test('回归：新 case 不落在 toolchainFixes 的两个切片区间内', () => {
  const src = readText('MCP/index.js');
  const toolPos = src.indexOf(`case '${TOOL}': {`);
  const startPos = src.indexOf("case 'start_game': {");
  const statusPos = src.indexOf("case 'get_game_status': {");
  assert.ok(toolPos > 0 && startPos > 0 && statusPos > 0);
  assert.ok(toolPos > statusPos,
    '新 case 必须插在 case get_game_status 之后，否则会破坏 toolchainFixes.test.js:263-268 的切片');

  const linePos = src.indexOf('function toolListLine(');
  const buildPos = src.indexOf('function buildMenuNode(');
  const toolDefPos = src.indexOf(`name: '${TOOL}'`);
  assert.ok(toolDefPos < linePos,
    '工具定义必须插在 function toolListLine 之前，否则会破坏 toolchainFixes.test.js:120-124 的切片');
});

test('回归：既有 C# 侧契约文本没被改动', () => {
  const src = readText('Source/UELoader/HotReloadValidator.cs');
  assert.match(src, /GetAllPatchedMethods/, 'HotReloadValidator 的静态入口注释仍在（本计划一个字都不改它）');
});
```

并在文件顶部（`const readText = ...` 之后）补两个小工具，因为上面的测试用到了：

```javascript
const source = readText('MCP/index.js');

async function withClient(fn) {
  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { InMemoryTransport } = await import('@modelcontextprotocol/sdk/inMemory.js');
  const server = mod._testHooks.createServer();
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'test-client', version: '1.0.0' });
  try {
    await Promise.all([server.connect(serverT), client.connect(clientT)]);
    return await fn(client);
  } finally {
    await client.close().catch(() => {});
    await server.close().catch(() => {});
  }
}

const callText = (client, name, args) =>
  client.callTool({ name, arguments: args }).then((r) => (r.content || []).map((c) => c.text || '').join(''));

const withCallText = (name, args) => withClient((client) => callText(client, name, args));
```

> ⚠️ `readText` 的根是 `path.join(here, '..', '..')` = 模组根（任务 1 已定义）。`Source/UELoader/HotReloadValidator.cs` 这条断言需要同一个根 —— 确认 `root` 变量确实指向模组根。

- [ ] **步骤 2：运行确认失败**

运行：`Set-Location MCP; node --test test/harmonyPatchesTool.test.js`
预期：任务 1 的 4 条文案断言 **PASS**；新加的 9 条全部 **FAIL**（`未知工具: list_harmony_patches` / `找不到转发 case`）。

- [ ] **步骤 3：工具定义**

在 `MCP/index.js` 的 `:2490`（`TOOLS.push({ … post_map_marker … });` 的收尾）之后插入：

```javascript

// Harmony 运行时补丁查询（吐槽板 [28]）：放在数组定义之后，与 post_map_marker 同一形状。
// ⚠️ 刻意不给它加 [需要进入地图] 前缀：Harmony 补丁在启动期就已加载，主菜单即可查。
TOOLS.push({
  name: 'list_harmony_patches',
  description: '[UE工具][主菜单即可用] 列出当前**游戏进程内**被 Harmony patch 的目标方法：谁被 patch 了、挂了几条、'
    + '挂在哪个 patch 方法与哪个程序集上。读的是运行时补丁表（静态 HarmonyLib.Harmony.GetAllPatchedMethods()）——'
    + 'Harmony 补丁不改动方法元数据，因此离线反编译 / find_methods / 反射枚举**原理上看不见**，只有本工具能回答。'
    + ' ⚠️ 两条常见写法是错的：① 实例 Harmony.GetPatchedMethods() 返回 0（owner 作用域），必须用静态版；'
    + '② 绕开扩展方法改调 map.reachability.CanReach 并不成立——该判定链上的 vanilla 方法本身就被 patch。'
    + ' ⚠️ 能力边界：本工具只回答「谁被 patch 了」，**不回答**「此刻走的是谁的逻辑」（那取决于补丁前缀内部的守卫条件）。'
    + ' 空结果时先看 totalPatchedTargets：**空数组 ≠ 没有补丁**。',
  inputSchema: {
    type: 'object',
    properties: {
      query: {
        type: 'string',
        description: '可选：子串过滤（大小写不敏感），同时匹配目标类型/方法/签名、patch 方法全名、patch 程序集、owner。'
          + '例："Reachability"、"TEXT20.Unity"、"com.text20.flightastar"。省略=列出全表'
      },
      limit: {
        type: 'integer',
        description: '返回行数上限，默认 100，范围 [1, 200]（与游戏侧上限同口径）',
        default: 100,
        minimum: 1,
        maximum: 200
      }
    }
    // 注：这里原先声明 additionalProperties:false，但实测未声明的多余参数会被静默忽略（服务端不校验），
    // 声明与行为不符，故去掉该声明（同 :2487-2488 的既有结论）。
  }
});
```

- [ ] **步骤 4：五处登记**

**① `TOOL_CATEGORY`** —— 在 `MCP/index.js:2898`（`list_hooks: 'unityexplorer', execute_csharp_code: 'unityexplorer',`）之后插入：

```javascript
  list_harmony_patches: 'unityexplorer',
```

**② `TOOL_PRECONDITION`** —— 在 `MCP/index.js:2926`（`post_map_marker: '…',`）之后插入：

```javascript
  list_harmony_patches: '游戏进程已启动即可（主菜单亦可；Harmony 补丁在启动期加载完毕）。不依赖 UnityExplorer 面板是否打开，不依赖 RIMAPI/GABP',
```

**③ `TOOL_USAGE_NOTES`** —— 在 `MCP/index.js:2935`（`inspect_type: { example: … }`）之后插入：

```javascript
  list_harmony_patches: {
    example: '{ "query": "Reachability" }',
    notes: '查「某方法有没有被 Harmony patch 掉、被谁 patch 的」。读游戏进程内的运行时补丁表（静态 GetAllPatchedMethods）。'
      + '⚠️ 必须用**静态** HarmonyLib.Harmony.GetAllPatchedMethods()：实例 Harmony.GetPatchedMethods() 是 owner 作用域，任意新实例返回 0（实测 0 vs 495，见 Source/UELoader/HotReloadValidator.cs:383-387）。'
      + '⚠️ 绕开 pawn.CanReach 直接调 map.reachability.CanReach **不成立**：可达性判定链上 4 个 vanilla 方法全被 patch，不存在原版语义入口。'
      + '⚠️ 本工具只回答「谁被 patch 了」，不回答「此刻走的是谁的逻辑」——后者取决于补丁前缀内部的守卫条件。'
      + '零结果先看 totalPatchedTargets：空数组 ≠ 没有补丁。兼容参数别名 type/method/keyword/q → query。'
  },
```

**④ `TOOL_ARG_ALIASES`** —— 在 `MCP/index.js:3793`（`inspect_type: { type_name: 'typeName', typeName_simple: 'typeName' },`）之后插入：

```javascript
  list_harmony_patches: { type: 'query', typeName: 'query', method: 'query', methodName: 'query', keyword: 'query', q: 'query' },
```

**⑤ `handleToolCall` 的 case** —— 在 `MCP/index.js:4527`（`case 'list_hooks': { … }` 的收尾 `}`）之后插入：

```javascript

    case 'list_harmony_patches': {
      // 刻意不套 requireStage：Harmony 补丁在启动期就已加载，主菜单即可查
      // （对照 :4422 / :4433 / :4521 那些需要进图的 UE 工具）。
      const rawQuery = args?.query == null ? '' : String(args.query);
      const query = rawQuery.trim();
      const parsedLimit = Number(args?.limit);
      const limit = Math.min(Math.max(1, Number.isFinite(parsedLimit) && parsedLimit > 0 ? Math.floor(parsedLimit) : 100), 200);
      const qs = (query ? `?query=${encodeURIComponent(query)}&limit=${limit}` : `?limit=${limit}`);
      const result = await callUnityExplorerAPI(`/harmony/patches${qs}`, 'GET');
      if (!result || result.success !== true) {
        result = result || {};
        result.guidance = '本工具读的是**游戏进程内**的 Harmony 运行时补丁表。'
          + (result.errorCode === 'HARMONY_UNAVAILABLE'
            ? '本进程加载的 0Harmony 既无静态 GetAllPatchedMethods 也无可用实例版 ⇒ 无法回答「谁被 patch 了」，**这不等于该方法没有补丁**。'
            : '若游戏在跑仍失败，多半是新 Assemblies/UELoader.dll 未生效 —— C# 改动必须先停游戏、重新构建、再重启游戏。')
          + ' 游戏未运行时同样不可得 ⇒ 也不等于没有补丁。';
      }
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }
```

- [ ] **步骤 5：`toolConfig.json` 与两份语言 XML**

**① `MCP/toolConfig.json`**（**单行压缩 JSON**）：在 `"list_hooks":false,` 之后插入 `"list_harmony_patches":false,`。
`_comment` 里枚举的是**默认直接暴露（`true`）**的工具，本工具登记为 `false` ⇒ **`_comment` 与 `README.md:60` 的计数都不变，不要动它们**。

**② `Languages/ChineseSimplified/Keyed/UESDdebuger.xml`**：在 `<li>UESDdebuger.ToolDesc.list_hooks</li>` 之后插入：

```xml
		<li>UESDdebuger.ToolDesc.list_harmony_patches</li>
```

**③ `Languages/English/Keyed/UESDdebuger.xml`**：同一位置插入：

```xml
		<li>UESDdebuger.ToolDesc.list_harmony_patches</li>
```

> ⚠️ 语言文件用的是 `<li>标签文本</li>` 这种**标签即翻译键**的写法（`Source/UELoader/UELoaderSettings.cs:79-91` 的 `GetToolDescriptionKey` 派生 `UESDdebuger.ToolDesc.<toolName>`）。若现有条目带 `<label>`/`<labelPlural>` 子标签，本工具只有一个名字，**照抄 `list_hooks` 那条的标签结构**即可。
> ⚠️ `Source/UELoader/UELoaderSettings.cs:155-162` 会把 `toolConfig.json` 里出现的每个键都加进 `ToolNamesWithDescriptions` ⇒ **只在 `toolConfig.json` 登记而漏了语言 XML，游戏内设置界面会显示原始 key**。两处必须同时改。

- [ ] **步骤 6：运行测试验证通过**

运行：`Set-Location MCP; node --test test/harmonyPatchesTool.test.js`
预期：全部 **PASS**（任务 1 的 4 条 + 本任务 9 条 = 13 条）。

- [ ] **步骤 7：回归 —— 全量 JS 测试**

运行：`Set-Location MCP; npm test`
预期：全部通过。特别确认这三条没被破坏：

| 文件 | 为什么必须绿 |
|---|---|
| `MCP/test/toolchainFixes.test.js` | `:120-124` 与 `:263-268` 是源码切片断言（任务 5 步骤 4 的回归测试只防"插入位置"，这里防"内容被改"） |
| `MCP/test/toolCountConsistency.test.js` | 概览 / 菜单可见 / 菜单宇宙 / 可调用四套口径的自洽断言 |
| `MCP/test/index.integration.test.js` | 走真实 `tools/list`，验证新增工具没有破坏暴露面 |

- [ ] **步骤 8：改动清单（替代 commit）**

| 文件 | 改动 |
|---|---|
| `MCP/index.js` | 改 6 处（`:2490` / `:2898` / `:2926` / `:2935` / `:3793` / `:4527` 之后各插入一段） |
| `MCP/toolConfig.json` | +1 键（`false`） |
| `Languages/ChineseSimplified/Keyed/UESDdebuger.xml` | +1 条 `<li>` |
| `Languages/English/Keyed/UESDdebuger.xml` | +1 条 `<li>` |
| `MCP/test/harmonyPatchesTool.test.js` | 改（追加 9 条断言 + 3 个小工具） |

> ⚠️ 改完**要重启 MCP 进程**（`MCP/start-mcp.ps1` 或 `MCP/start-mcp.bat`）才生效 —— `MCP/index.js` 是启动时读入的。

---

### 任务 6：游戏内实测验收矩阵（**唯一必须「游戏在跑」的一步**）

**为什么单独成任务：** 前 5 个任务全绿只能证明「代码能编译、契约自洽」。本工具的存在意义 —— 「装了 Reachability 补丁的模组里，AI 能不能一眼看出 `pawn.CanReach` 的原版语义已被接管」—— **只能在真游戏 + 真模组里验收**。这也是本计划**唯一**的「构建与实测是整树操作」环节（§0.6）。

**前置条件（全部满足才能开工）：**
1. 任务 1–5 的全部测试已绿（`Set-Location MCP; npm test`、`dotnet run --project Source/HarmonyPatchIndexTests`、`dotnet run --project Source/HotReloadTests`、`dotnet run --project Source/ConfigJsonTests`）。
2. 游戏**未运行**（`Get-Process RimWorldWin64 -ErrorAction SilentlyContinue` 无输出）。
3. **当前没有别的并行会话在调试这款游戏** —— 本任务要停启游戏，会打断它们。开工前先跟主代理确认。

- [ ] **步骤 1：构建**

运行：`dotnet build Source/UELoader/UELoader.csproj`
预期：`Build succeeded`。用 `Get-Item Assemblies\UELoader.dll | Select-Object LastWriteTime` 确认时间戳是刚刚。

- [ ] **步骤 2：起游戏（带模组），进图**

启动 RimWorld，确认 `ModsConfig.xml` 里 **TEXT20 已启用**（验收用例就靠它，§0.0 的 4 条补丁全来自它），然后进测试地图。
起完游戏后重启 MCP 进程（任务 5 步骤 8 的要求）。

- [ ] **步骤 3：先验「端点存在」**

调用：`agg_call_tool { tool: "list_harmony_patches", args: {} }`
预期：`success: true`，`data.lookupScope == "runtime-harmony-patch-table"`，`data.lookup.source == "static-getAllPatchedMethods"`（**不是** `instance-getPatchedMethods`），`data.lookup.harmonyVersion` 非空，`data.totalPatchedTargets > 0`。

> ⚠️ 若 `source` 是 `instance-getPatchedMethods` ⇒ 本机加载的 0Harmony 没有静态版，**不要**把它的计数当权威；按响应里的 `instanceFallbackWarning` 处理并上报主代理。

- [ ] **步骤 4：本条目的验收用例 —— 查 `Reachability`**

调用：`agg_call_tool { tool: "list_harmony_patches", args: { "query": "Reachability" } }`
逐条对照（**这是本计划最重要的一格**）：

| # | 预期 | 不满足说明什么 |
|---|---|---|
| 1 | `matchedTargets >= 4` | 板上只以为 patch 了 1 个；实测是 4 个 vanilla 方法（§0.0） |
| 2 | `rows` 里出现 `declaringType` ∈ {`Verse.ReachabilityUtility`, `Verse.Reachability`, `Verse.ReachabilityImmediate`} | 取数漏了 |
| 3 | 至少 2 行 `kind == "Prefix"` | 同上 |
| 4 | `patchMethod` 含 `Patch_ReachabilityUtility_CanReach_Flight` | §0.0 的核心用例，必须一眼可见 |
| 5 | `patchAssembly` 指向 TEXT20 的程序集（`TEXT20.Unity` 或等价） | 归属 join 写错 |
| 6 | 每行 `targetIsVanilla == true` | vanilla 判定写错 |
| 7 | `matchedRows > returnedRows` 时 `truncated == true` | 反假阴性契约被破坏 |

**把 `rows` 原文贴进验收记录** —— 它就是以后 AI 判断「这句 `pawn.CanReach` 是不是原版语义」的唯一依据。

- [ ] **步骤 5：query 零命中也必须自洽**

调用：`agg_call_tool { tool: "list_harmony_patches", args: { "query": "NoSuchTypeAnywhere" } }`
预期：`success: true`，`rows == []`，**但** `totalPatchedTargets > 0`、`totalRows > 0`、`matchedRows == 0`、`truncated == false`。
⇒ **空数组 ≠ 没有补丁**，这一格专门验它。

- [ ] **步骤 6：limit 钳制**

调用：`agg_call_tool { tool: "list_harmony_patches", args: { "limit": 1 } }` 与 `{ "limit": 99999 }`
预期：前者 `returnedRows == 1` 且 `truncated == true`；后者 `data.limit == 200`（不是 99999）。

- [ ] **步骤 7：query 的 URL 编码不炸**

调用：`agg_call_tool { tool: "list_harmony_patches", args: { "query": "Reachability&limit=999" } }`
预期：请求成功，`data.query == "Reachability&limit=999"`（**不是**被截断成 `Reachability`）。这一格专门验任务 5 步骤 4 的 `encodeURIComponent`。

- [ ] **步骤 8：主菜单可用性**

回到主菜单（不必进图），重复步骤 3。
预期：**照样成功**。若报 `MAP_NOT_LOADED`，说明有人给这个 case 加了 `requireStage` —— 去掉（任务 5 步骤 4 已明确不加）。

- [ ] **步骤 9：停游戏后必须给出诚实失败**

关掉游戏，再调一次。
预期：失败响应**带** `guidance`，正文写明「游戏未运行时同样不可得 ⇒ 也不等于没有补丁」；**不允许**返回一个看起来像「零补丁」的 `success: true`。

- [ ] **步骤 10：把结果落进本文件末尾的验收记录（不要新建文件）**

在本文件末尾的 `## 7. 验收记录（执行者填写）` 表格里逐格填实测值。**没跑到的格子写「未跑 + 原因」，不要留空、更不要写推测值。**

- [ ] **步骤 11：改动清单（替代 commit）**

本任务**不产生任何源文件改动**，只产生 `Assemblies/UELoader.dll` 的重建产物与本文件末尾的验收记录。

---

## 3. 全部改动的总清单

| 文件 | 性质 | 所属任务 | 需要停游戏 / 重编译 |
|---|---|---|---|
| `README.md` | 改 | 1 | 否 |
| `AI-DEPLOYMENT.md` | 改 | 1 | 否 |
| `MCP/test/harmonyPatchesTool.test.js` | **新建** | 1 / 5 | 否 |
| `Source/UELoader/UEHarmonyPatchIndex.cs` | **新建** | 2 | 进编译面（任务 4 才构建） |
| `Source/HarmonyPatchIndexTests/HarmonyPatchIndexTests.csproj` | **新建** | 2 / 3 / 4 | 否（独立 `Exe`，不进 `UELoader.dll`） |
| `Source/HarmonyPatchIndexTests/Program.cs` | **新建** | 2 / 3 / 4 | 否 |
| `Source/UELoader/UEHarmonyPatchSource.cs` | **新建** | 3 | 进编译面（任务 4 才构建） |
| `Source/UELoader/UEHttpHandler.cs` | 改 | 4 | **是** |
| `Source/UELoader/UEHttpServer.cs` | 改 | 4 | **是** |
| `Source/UELoader/CompatibilityHooks.cs` | 改 | 4 | **是** |
| `Assemblies/UELoader.dll` / `.pdb` | 构建产物 | 4（自动） | 由 `UELoader.csproj:91-96` 生成 |
| `MCP/index.js` | 改 6 处 | 5 | 否（需重启 MCP 进程） |
| `MCP/toolConfig.json` | 改 | 5 | 否（需重启 MCP 进程） |
| `Languages/ChineseSimplified/Keyed/UESDdebuger.xml` | 改 | 5 | 否 |
| `Languages/English/Keyed/UESDdebuger.xml` | 改 | 5 | 否 |

**没被本计划碰过的东西**（逐个点名，免得下一个人以为漏了）：`McpRimDebug/**`、`Source/UELoader/HotReloadValidator.cs`、`docs/mcp-吐槽板.md`、`Source/UELoader/UELoader.csproj`、`Source/Directory.Build.props`、`runtime/**`、其余 13 份既有计划文件。

---

## 4. 自检（writing-plans §自检，三项）

**① 规格覆盖度** —— 逐条对回裁决报告与吐槽板：

| 规格里的要求 | 落在哪里 |
|---|---|
| 裁决 §6.1「立刻可做、零代码：把绕过姿势写进规范」 | 任务 1（`README.md` 新小节 + `AI-DEPLOYMENT.md` §8/§9）+ 任务 1 步骤 1 的 4 条文案断言 |
| 裁决 §6.2「新增工具，命名建议 `list_harmony_patches`」 | 任务 5 步骤 3（工具定义） |
| 裁决 §6.2「入参 `typeName?` / `methodName?` / `limit?`」 | **改名并加强**：统一为 `query`（可同时命中类型/方法/patch/程序集/owner），因为「哪些 vanilla 方法被 patch 了」这个问题几乎从不只按一个字段筛。原 `typeName` / `methodName` 以 `TOOL_ARG_ALIASES` 别名兼容回来（任务 5 步骤 4 ④） |
| 裁决 §6.2「照 `InspectType`（`:120-161`）的形状加 `ListHarmonyPatches(...)`」 | 任务 4 步骤 4，位置与形状均照抄 |
| 裁决 §6.2「内部直接调 `HotReloadValidator.AllPatchedMethods(null)`」 | ⚠️ **未采纳**：`AllPatchedMethods(null)` 在静态入口缺失时会走 `:404` 的 `instance != null ? … : new List<MethodBase>()`，即返回**空表且不自曝取数路径** —— 那正是「空数组冒充没有补丁」。本计划改为独立的 `HarmonyPatchSource`（任务 3），复用同一套反射配方（`:395-401`）但**补上报错路径与退回路径的自曝**。`HotReloadValidator` 一个字节都没改（§0.9 末行） |
| 裁决 §6.2「逐个 `GetPatchInfo` 取 `Owners` / `Prefixes` / `Postfixes` / `Transpilers`」 | ⚠️ **`Owners` 取不到**（三个 0Harmony 版本都只有 public 字段 `owner`，无 `Owner` 属性，§0.0 表）→ 改为逐个 `Patch` 读 `owner`；`Prefixes…` 改为「先属性后字段」两级反射（任务 3） |
| 裁决 §6.2「必须复制两条既有教训：静态版 + 反射退回」 | 任务 3 全部；§0.0 把「反射退回」的范围从 `GetAllPatchedMethods` 扩到**全部访问器 + 第 5 类补丁** |
| 裁决 §6.2「把 [28] 本身当验收用例」 | 任务 6 步骤 4 的 7 格逐条对照 |
| 裁决 §6.3「建议 ④① 的绕法不成立」 | §0.0 表格第 1 行 + 任务 1 的 3 处文案 + 任务 5 步骤 3/④ 的描述与 notes |
| 裁决 §6.3「建议 ④② 照抄返回 0，必须写静态版」 | §0.0 表格第 2 行 + 任务 1 步骤 3 的 C# 片段 + 任务 5 步骤 3 的描述 |
| 裁决 §6.1「最诚实的提示语应是『已被 N 个 patch 覆盖，语义可能不是原版』」 | 任务 4 步骤 4 的 `notice` 字段 + 任务 5 步骤 3 的 description + 任务 1 的文案断言（正反两面都断言） |
| 裁决 §4「验收需要游戏在跑 ⇒ 不能离线 `--selftest`」 | 任务 6 是唯一需要游戏的一步；但**把 90% 的验证前移到任务 2/3 的离线往返测试**（§0.0 与任务 3 开头的说明） |
| 裁决 §5「`McpRimDebug` 不改」 | §0.4；§3 末段逐个点名未碰过的文件 |

**② 占位符扫描** —— 全文无「待定 / TODO / 后续实现 / 补充细节 / 类似任务 N / 为上述代码编写测试 / 添加适当的错误处理」。逐项自查：

- 任务 1 步骤 1/3/4、任务 2 步骤 1/3/5、任务 3 步骤 1/2/4、任务 4 步骤 1/4/5/6、任务 5 步骤 1/3/4/5、任务 6 步骤 4 全部带**可直接粘贴的完整代码块**或**逐条对照的验收表**。
- 唯一的「提取式」指示是任务 3 步骤 2 的 `RunIndexTests()`：它要求把任务 2 步骤 3 那段 `Main` 的函数体**原样搬过去**（明确写出了「从哪一行到哪一行」「末尾两行 `Console.WriteLine` 与 `return` 不要带进来」），不是「类似任务 N」的省略。
- 两处**有意留白但给了确切边界**：`row` 在任务 2 步骤 2 的空壳版是占位（`Check(false, "步骤 1 占位…")`），步骤 3 明确要求把它替换掉；`## 7. 验收记录` 是任务 6 步骤 10 要求执行者**实测后**填写，并写明「没跑到的格子写『未跑 + 原因』，不要留空、更不要写推测值」。

**③ 类型一致性** —— 跨任务逐个对过：

- `HarmonyPatchRow` 的 8 个字段（`DeclaringTypeFullName` / `MethodName` / `ParamTypes` / `Kind` / `PatchMethod` / `PatchAssembly` / `Owner` / `TargetIsVanilla`）+ 只读属性 `TargetSignature`：定义在任务 2 步骤 5；构造于任务 2 步骤 2 的 `Row(...)` 辅助与 `FlightAStarSample()`；读于任务 3 步骤 4 的 `AppendRows` 填充；断言于任务 3 步骤 2（`row.PatchMethod` / `row.PatchAssembly` / `row.Owner` / `row.ParamTypes` / `row.TargetSignature` / `row.TargetIsVanilla`）；序列化为 camelCase 于任务 4 步骤 4。**无一处拼写漂移。**
- `HarmonyPatchIndexResult` 的 10 个成员（`Rows` / `TotalPatchedTargets` / `TotalRows` / `MatchedRows` / `MatchedTargets` / `ReturnedRows` / `Truncated` / `Limit` / `Query` / `LookupScope`）：定义于任务 2 步骤 5，断言于任务 2 步骤 3（10 组全覆盖），映射为响应字段于任务 4 步骤 4（同名 camelCase），实测于任务 6 步骤 4/5。命名一一对应。
- `HarmonyPatchIndex.Build(allRows, totalPatchedTargets, query, limit)` 的 4 参签名在**定义处（任务 2 步骤 5）、测试调用处（任务 2 步骤 3 的 20+ 次、任务 3 步骤 2 的 3 次）、handler 调用处（任务 4 步骤 4）** 三处完全一致。`HarmonyPatchIndex.MaxLimit` / `LookupScopeName` / `PatchKinds` / `IsVanillaAssembly` / `ClampLimit` 三个静态成员名在三处一致。
- `HarmonyPatchSnapshot` 的 5 个字段（`Rows` / `TotalPatchedTargets` / `TargetsFailed` / `LookupSource` / `HarmonyVersion`）与 `HarmonyPatchSource` 的 3 个常量（`SourceStatic` / `SourceInstance` / `SourceUnavailable`）：定义于任务 3 步骤 4，断言于任务 3 步骤 2，消费于任务 4 步骤 4。`LookupSource` 的三态取值在 §0.3 契约、任务 3 实现、任务 4 分支、任务 6 步骤 3 的验收四处完全一致。
- 工具名 `list_harmony_patches`（全小写下划线，无连字符变体）在 `TOOLS` / `TOOL_CATEGORY` / `TOOL_PRECONDITION` / `TOOL_USAGE_NOTES` / `TOOL_ARG_ALIASES` / `handleToolCall` / `toolConfig.json` / 两份语言 XML / 测试 / 文档共 **11 处**，逐字一致。
- 端点路径 `/harmony/patches` 在 `UEHttpServer` 的 case、`UEHttpHandler` 的注释、MCP 转发、测试断言四处逐字一致；开关 `harmony/patches` 另计。

---

## 5. 与上游裁决报告的逐条对账（含本计划复核后修正的错误）

| 裁决报告的说法 | 本计划复核结果 |
|---|---|
| §3.2「同一文件 patch 了 4 个 vanilla 方法（`:17` / `:35` / `:99` / `:234`+`:426`）」 | ✅ 成立，本计划采信并写进 §0.0 与任务 6 步骤 4 的验收格 1 |
| §3.3(a)「`Assemblies\` 下没有 `0Harmony.dll`」 | ✅ 成立。实测该目录 **10** 个文件（`Mono.Cecil*.dll` ×4、`MonoMod.RuntimeDetour.dll`、`MonoMod.Utils.dll`、`UELoader.dll`、`UELoader.pdb`、`UnityExplorer.STANDALONE.Mono.dll`、`UniverseLib.Mono.dll`）。报告写「9 个文件」**少算 1 个**（`MonoMod.Utils.dll`），结论不变 |
| §3.3(a)「本仓库树内的 Harmony 只有两处：`Source\UELoader\lib`（2.3.3.0）与 `Source\UE-4.9.0-official\…`（2.5.2.0）」 | ⚠️ **本仓库树内**成立；但若按「实际会加载哪一份」的口径，**全盘 `Mods\` 下有 3 个不同版本**（2.3.1.1 / 2.3.3.0 / 2.5.2.0，其中 2.3.3.0 有 14 份完全相同的副本，MD5 `4131A87E7C8076C155EB116AAFBB696A`）。**结论方向不变**（版本会漂、必须反射），但搜索范围必须写清是「本仓库树内」还是「全盘」 |
| §3.3(b)「`060001C2:M` = 实例、`060001C3:M` = `Public` + `isStatic`」 | ✅ 逐字复核正确（上下文 `1.6update\AerocraftFramework\0Harmony.dll`，MVID `abec1146…`）。**补一条**：该 MVID 与本仓库 vendor 副本 `Source\UELoader\lib\0Harmony.dll` **完全相同**，报告未点破这一点 —— 也就是说它核实的其实**就是**本仓库编译面对的那一份 |
| §3.3(c)「`:383-387` 注释必须用静态版；实测 0 vs 495」 | ✅ 行号与内容逐字复核正确（`[System.IO.File]::ReadAllLines` 确认 `HotReloadValidator.cs` 全文 446 行） |
| §3.3(c)「`:395-400` 反射取静态、`:404` 反射失败才退回实例」 | ⚠️ **行号小偏**：反射块实际是 `:393-402`（`:395` 是 `GetMethod`、`:397` 是 `if (mi != null)`、`:399` 是 `Invoke`、`:400` 是 `ToList`）；`catch { /* 反射失败 → 退回实例方法 */ }` 是 `:403`，退回语句是 `:404`。结论不变 |
| §3.3(d)「`MCP/index.js` 全文 grep Harmony 相关 → 0 命中」 | ✅ 成立（`TOOLS` / `handleToolCall` / `toolConfig.json` 三处均无）。本计划任务 5 是**首次**新增 |
| §3.3(d2)「`:837` 的 `success` 与 `:838/:844/:847/:850/:851/:852/:853` 的 `success` 证明 `HarmonyLib` 可直接解析」 | ❌ **`:847` 是 `COMPILATION_ERROR`，不是 success**（`[System.IO.File]::ReadAllLines` 逐行复核）。正确的 success 行是 **9 条**：`:837` / `:838` / `:839` / `:844` / `:849` / `:850` / `:851` / `:852` / `:853`；整份日志同类 success 共 13 条（另加 `:36` / `:41` / `:816` / `:1004`）。**结论方向不变**（`HarmonyLib` 确实可直接解析），但清单必须改 |
| §3.3(e)「`McpRimDebug.csproj:29-39` 的 `ItemGroup` 只有 ModelContextProtocol 与 Mono.Cecil」 | ⚠️ **行号偏了**：该 `ItemGroup` 是 **`:36-39`**（`:29-34` 是另一个只放 `<Compile Include>` 的 `ItemGroup`）。`PackageReference` 两条一字不差，结论不变 |
| §6.2「必须复制的教训 2：handler 内按 `:395-405` 的反射 + 退回写法，不要硬绑」 | ⚠️ **不够**：`:395-405` 只覆盖 `GetAllPatchedMethods`。**真正会炸的是访问器本身** —— `HarmonyLib.Patches.Prefixes…` 在三个版本里都是 **public 字段**（不是属性），`HarmonyLib.Patch` **没有 `Owner` 属性**（只有 public 字段 `owner`），而 **2.5.2 多出第 5 类 `ILManipulators`**。本计划任务 3 因此把反射范围扩到**全部访问器**并加第 5 类探测 |
| §6.2「`HotReloadValidator.AllPatchedMethods(null)` 拿全表」 | ⚠️ **不能直接用**：`null` 实例会让 `:404` 返回空表，且**不自曝取数来源** —— 恰好是「空数组冒充没有补丁」。本计划另起 `HarmonyPatchSource`（§4 ①表已说明） |
| §6.2「输出 `{ target, owners, prefixes, … }`」 | ⚠️ **`owners` 取不到**：三个 0Harmony 版本都没有 `Patch.Owners` / `PatchInfo.Owners` 属性（2.3.x 的 `PatchInfo` 甚至整体没有 PascalCase 成员，只有 `prefixes` 等小写 public 字段）。本计划改为**逐行**（目标 × 种类 × patch 方法）并从 public 字段 `owner` 取 owner |

---

## 6. 需要主代理裁决的事（本计划无权自行决定）

1. **吐槽板 `## [28]` 条目目前没有 `Triage 批注`。** 给条目加批注是主代理的活（`AGENT-BRIEF.md` §1.1 明令 PLAN 子代理不许碰吐槽板），**本计划没有动它一个字节**。建议批注内容：

   > 🤖 **Triage 批注（2026-10-02）** — 裁决：**值得修**。两条订正：④①「绕开扩展方法直接调 `map.reachability.CanReach`」**不成立**（可达性链上 4 个 vanilla 方法全被 patch，不存在原版语义入口）；④②「`Harmony.GetPatchedMethods()`」**照抄返回 0**（owner 作用域，必须用静态 `GetAllPatchedMethods()`）。状态由「待绕过」改为「已绕过 + 待修」：零代码那半（绕法 + 两条订正）已写进 `README.md` / `AI-DEPLOYMENT.md`，正解是新增工具 `list_harmony_patches`。
   > 报告：`docs/issue-triage/triage/28-harmony-patch-silent-semantics-override.md`
   > 计划：`docs/issue-triage/plans/2026-10-02-28-harmony-patched-methods-query.md`（6 个任务；**任务 4 需先停游戏重编译 `Assemblies/UELoader.dll`，任务 6 必须游戏在跑**；任务 1/2/3/5 全离线）

2. **任务 6 需要一次「停游戏 → 构建 → 起游戏 → 实测」的独占窗口。** 本仓库同时有并行 MCP 调试会话在跑（`AGENT-BRIEF.md` 开头）。**请主代理决定排期**；在此之前任务 1/2/3/5 可以全部做完并跑绿，它们对并行会话零影响。

3. **本机实际加载哪一份 0Harmony 由 ModsConfig 决定，本计划无法静态确定。** 若任务 6 步骤 3 的 `lookup.source` 不是 `static-getAllPatchedMethods`，说明该会话的 Harmony 缺静态入口 —— 此时应按响应里的 `instanceFallbackWarning` 处理，**并把实际加载的 `0Harmony.dll` 路径与版本上报**，因为它决定 `Assemblies/` 的 vendor 副本（`Source/UELoader/lib/0Harmony.dll`，2.3.3.0）是否还与运行面匹配（`Source/UELoader/UELoader.csproj:68-73` 的既有约定）。

---

## 7. 验收记录（执行者填写）

> 任务 6 步骤 10 要求填写。**每格写实测值；没跑的写「未跑 + 原因」，不要留空、不要写推测值。**
> 未填写时本节保持原样即为「未验收」。

| 格 | 内容 | 实测值 | 结论 |
|---|---|---|---|
| 6-1 | `dotnet build Source/UELoader/UELoader.csproj` | | |
| 6-2 | 起游戏（含 TEXT20）+ 进图 | | |
| 6-3 | 端点存在、`lookup.source` / `harmonyVersion` / `totalPatchedTargets` | | |
| 6-4 | `query=Reachability` 的 7 条对照 | | |
| 6-5 | `query=NoSuchTypeAnywhere` 的零命中自洽 | | |
| 6-6 | `limit=1` / `limit=99999` 的钳制 | | |
| 6-7 | `query="Reachability&limit=999"` 的 URL 编码 | | |
| 6-8 | 主菜单可用（不进图） | | |
| 6-9 | 停游戏后的诚实失败 + `guidance` | | |
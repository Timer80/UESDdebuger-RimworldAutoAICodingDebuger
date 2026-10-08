# 模组在场判定：程序集级可见性 + 可发现性 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 把「某个模组 / 某个程序集到底在不在场」这件事，从「grep `RimWorld\Mods` 目录名 → 零命中 → 得出事实相反的结论」变成**一次只读工具调用即可证伪**，并让调用方**看得见**这个工具。

**架构：** 三件事。①**不重造 `list_mods`** —— 它已存在、已默认暴露、已给出 `packageId`/`rootDir`/`source`/`enabled`/`loadedInSession`，吐槽板上那次「假阴性」本可一击消解。②**新增程序集级真值源**：UE 侧新端点 `GET /mods/loaded_assemblies`，直接读 `AppDomain.CurrentDomain.GetAssemblies()`，再用 `LoadedModManager.RunningModsListForReading → mod.assemblies.loadedAssemblies` 做归属 join，把「配置里启用」与「进程里真加载」一眼分开；「零命中」永远伴随 `appDomainAssemblyCount`，从协议层消灭假阴性。MCP 侧新增原生工具 `get_loaded_assemblies` 并登记进 `game_control/game/mods` 叶子菜单 + 默认暴露。③**可发现性**：`execute_csharp_code` 的「表达式模式」文案补齐，`README.md` / `AI-DEPLOYMENT.md` 写死「判模组在场用 `rimworld.list_mods` / `get_loaded_assemblies`，**不要**用 `Mods\` 目录 grep」。

**技术栈：** C#（net472，`LangVersion 9.0`，SDK-style csproj，`Source/UELoader/*.cs` 自动纳入编译）+ Node.js ESM（`MCP/package.json:6` `"type": "module"`）+ `node:test` & `node:assert/strict`（`MCP/package.json:9`）+ net472 `Exe` 手写 `Check()` runner（沿用 `Source/ConfigJsonTests` / `Source/HotReloadTests` 约定）。**改 `MCP/index.js` 即改到生效那份**（`MCP/resolve-runtime.ps1` 只解析便携运行时路径，不复制文件；`runtime/` 下无 `index.js` 副本）。**C# 改动需要重编译 `Assemblies/UELoader.dll` 才会生效**，且**必须先关闭游戏**（`Source/UELoader/UELoader.csproj:92-96` 的 `DeployToAssemblies` 目标要往被占用的 DLL 拷文件）。

---

## 0. 给零上下文执行者的背景（不要跳过）

### 0.0 🚫 不要重复造 `list_mods`（本计划最重要的一句话）

上游裁决报告 `docs/issue-triage/triage/12-mod-presence-and-disk-path.md` 的实测已经确认：

- `MCP/toolConfig.json` 里 `"rimworld.list_mods": true` —— **它是默认暴露工具**，不需要 `agg_call_tool` 转发就能直接调。
- 它的 schema 由 **GABP（RimBridgeServer）**定义，经 `tools-changed` 事件灌进本项目的 `gabpTools`（`MCP/index.js:2512-2514`），**本项目改不了它的返回体**。所以**不要**试图给它加 `modRoots` / 加程序集字段。
- 实测 payload（吐槽板 `:171-176` 记录的那一次假阴性，就是用下面这次调用可以消解的）：

  ```json
  {
    "packageId": "ancot.ancotlibrary",
    "name": "Ancot Library",
    "folderName": "2988801276",
    "rootDir": "C:\\SteamLibrary\\steamapps\\workshop\\content\\294100\\2988801276",
    "source": "SteamWorkshop",
    "onSteamWorkshop": true,
    "enabled": true,
    "loadedInSession": true,
    "matchesLoadedSession": true,
    "activeLoadOrder": 17
  }
  ```

- `MCP/index.js:2494-2500` 的注释确认它是「被删原生工具的唯一入口」；`MCP/index.js:3147-3148` 已把它归到 `game_control/game/mods`。

**结论**：`list_mods` 解决的是「**包**级」问题（装没装 / 启没启用 / 在哪 / 本次会话有没有加载）。本计划只补它**没有**的那一层：**程序集级**（某个 dll 是不是真的在 AppDomain 里、被哪个 `packageId` 拥有），外加**可发现性**。两条工具的分工在任务 4 的 `TOOL_USAGE_NOTES` 里写死。

### 0.1 吐槽板原文（`docs/mcp-吐槽板.md` `## [12]`，标题行 228，正文 228-270）

> ## [12] 没有「某模组在不在场」的工具；`activePackageIds` 不给路径 🟠
>
> `get_game_status` 返回的 `activePackageIds` 里有 `ancot.ancotlibrary`，但 MCP **不给它的磁盘路径**，而且 `RimWorld\Mods\` 目录下 grep 不到 —— 因为 **Workshop 模组走 `steamapps\workshop\content\294100\<workshopId>\`，根本不在 `Mods\` 下**。
>
> 按「目录名 grep」判「模组装没装」⇒ **必然假阴性**。本档就被骗了一次：分计划写「`Mods\*` 目录名含 Ancot ⇒ 零命中 ⇒ 当前安装没有 `AncotLibrary` ⇒ 结构性排除」，**但它装了、启用了、而且已经加载进游戏进程**。
>
> （顺带：本次调用确认了 `execute_csharp_code` 是**表达式**模式 —— 写 `return expr;` 会报 `CS0127: A return keyword must not be followed by any expression when method returns void`，写「语句 + 末尾裸表达式」才行。文档 example `"Verse.GenTicks.TicksGame"` 确实是裸表达式，但没明说。）
>
> **状态**：待修（本档已绕过：查 `AppDomain.CurrentDomain.GetAssemblies()` 的 `Location`）

原始「建议」三条，本计划的处置：

| 原建议 | 处置 | 理由 |
|---|---|---|
| 1. 加 `mods.list_active` / `rimworld.list_loaded_mods`，返回 `packageId` + `Assembly.Location` + 「是否真在 AppDomain 里」 | **采纳，但改名并降级为补充工具** | `rimworld.*` 前缀在 `MCP/index.js:3740` 的 `TOOL_NAME_PREFIXES` 里是 **GABP 镜像专用**，本项目自研的原生 UE 工具一律用裸名（`get_map_structure` / `post_map_marker`）。故取名 **`get_loaded_assemblies`**。 |
| 2. `get_game_status.activePackageIds` 旁边补 `modRoots` 字段 | **不做** | 裁决报告 §6.3 已否：它与 `rimworld.list_mods.rootDir` 重复，且写死 `Mods\` 与 `steamapps\workshop\content\294100\` 反而把「来源目录不止这两个」（实测还有 `Data\Core`、`Data\Royalty` 等 `OfficialModsFolder`）固化错。本计划同样**不硬编码任何模组根目录**（见 §0.4）。 |
| 3. `execute_csharp_code` 的 description 写明「表达式模式，不要写 `return`」 | **采纳，但只改未被占用的那一处** | `MCP/index.js:2937` 已被计划 `2026-10-01-02-execute-csharp-code-output-capture.md` 的任务 4 **整行重写**（该计划 `:27` / `:130` / `:863` / `:869-872` 均登记了这一行）。本计划改 `:2072`（工具 description）与 `:2078`（`code` 参数示例），并在 §0.5 给出要并进计划 02 新文案的那半句。 |

### 0.2 缺口一：程序集级 —— 现在看到的是「包」，不是「实际加载进 AppDomain 的程序集」

**读的是什么（写代码时不要含糊）：**

| 数据 | 来源 | 为什么 |
|---|---|---|
| 「进程里真加载了哪些程序集」 | **`AppDomain.CurrentDomain.GetAssemblies()`** | 这是唯一的「进程内真值源」。`rimworld.list_mods.loadedInSession` 是**模组会话**层面的标记，不等于「某个 dll 确实进了 AppDomain」。 |
| 「这些程序集归哪个模组」 | **`LoadedModManager.RunningModsListForReading` → `mod.assemblies.loadedAssemblies`** | 必须走模组加载器。`docs/csharp-console-compiler-upgrade-feasibility.md:318-319` 实测：`20/34 个模组程序集在 Steam 创意工坊目录 → 只扫 <Game>/Mods/*/Assemblies 会漏掉 59%`。 |
| 「模组根目录」 | `ModContentPack` 上的 `RootDir`（**反射探测，拿不到就返回 `null`**） | 裁决报告 §6.3 明令禁止硬编码根目录。反射失败就少给一个字段，**绝不用猜的路径补**。 |

**API 踩坑（已踩 3 次，必须照抄注释）：** `ModContentPack.assemblies` 是**字段**（类型 `Verse.ModAssemblyHandler`），程序集列表是它的**字段** `loadedAssemblies`。
`GetProperty("assemblies")` 返回 null 直接 NRE；`handler.assemblies` 不存在；猜成员名会连着编译失败。
—— 原始记录 `docs/csharp-console-compiler-upgrade-feasibility.md:321-323`，可用实现样例 `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md:448-453`（`ReferenceScanner.Collect`）。

**为什么必须新增、不能靠 `execute_csharp_code` 顶：**
吐槽板 `:242-248` 给的绕法是一段 5 行 LINQ（`AppDomain.CurrentDomain.GetAssemblies()` + `Where` + `Select` + `string.Join`），每次都要重写；而且它**只能给程序集名 + Location，给不出 `packageId` 归属**（「这个 dll 属于哪个 mod」仍然答不了）。更糟的是它是**写操作工具**（本流水线明令禁止调用），不能出现在任何自动验收路径里。

### 0.3 缺口二：可发现性 —— 工具在清单里，但没人知道该用它

- `MCP/index.js:2891-2893` / `:3147-3148` / `toolConfig.json` 三处都能看到 `rimworld.list_mods`，但**没有任何一句文案说「判模组在场就用它」**。
- `MCP/index.js:569` / `:662` 的 `activePackageIds` 只是透传字段，无 guidance —— 调用方拿到一串 `packageId` 却没有下一步指引，于是自然地去 grep 目录（这正是被坑的机制）。
- `README.md:167` 只有一行 `| rimworld.list_mods | Mod 列表 |`，没有「什么场景用它、返回哪几个关键字段」。
- `AI-DEPLOYMENT.md:166-177` 的排障决策树里**没有**「grep 目录零命中」这一行。

**最高杠杆的修法不是写文档，是登记进菜单叶子**：`mcp_help{path:"game_control/game/mods"}` 会列出该叶子下所有工具（`MCP/index.js:3261-3263` 确认 `mods` 是叶子，`3262` 行直接返回 `{type:'tools', filter:{subgroup:'game_control/game/mods'}}`）。新工具登记进 `TOOL_SUBGROUP` 之后，**一次 `mcp_help` 下钻就能看到**，这是 [08] 那份计划（`2026-10-01-08-mcp-help-query-param.md`）确立的同一套机制。

### 0.4 反假阴性契约（本计划所有设计选择的唯一判据）

吐槽板这条的痛点不是「没有工具」，是「**工具说零 = 调用方当成没有**」。因此新工具必须满足：

1. **`assemblies: []` 永远不是孤立的空数组。** 响应里必带 `appDomainAssemblyCount`（枚举到的程序集总条数）与 `scope: "appdomain"`。零命中时调用方一眼看到「枚举了 214 个、其中 0 个匹配」，而不是「返回了空」。
2. **游戏未运行时不得返回任何「没有」的语义。** 返回 `success:false` + `answerAvailable:false` + `errorCode`，正文里写明「不可得 ≠ 不存在」（见 §0.6）。
3. **绝不硬编码模组根目录。** 「这个路径属于工坊还是本地」交给 `rimworld.list_mods.source` 去说（那是 GABP 的权威），新工具只报 `location` + `packageId`。
4. **静默丢条目是 bug。** `Assembly.Location` 对动态程序集会**抛异常**（Roslyn 计划 `:434` 的 `catch { continue; }` 就是这么吞掉的）。本计划**不吞**：记 `location:null` + `locationUnavailable:true`，并计入 `locationUnknownCount`。否则「总数对不上」本身就是新的假阴性。

### 0.5 与其它计划的冲突面 / 交接（实施前必读）

| 冲突面 | 结论 |
|---|---|
| **`MCP/index.js:2937`**（`execute_csharp_code` 的 `TOOL_USAGE_NOTES`） | **已被 `2026-10-01-02-execute-csharp-code-output-capture.md` 任务 4 整行重写**（该计划 `:27`/`:130`/`:863`/`:869-872`）。**本计划一个字都不碰这一行。** 要并进去的那半句见下方「交接文案」，由计划 02 的执行者合进它自己的新文案里。 |
| **计划 02 与本条目的正面矛盾（⚠️ 上报给主代理）** | 计划 02 的游戏内验收矩阵第一行（`:819`）写的是 `…; return 1;`，并期望 `result` 为 `1`。但吐槽板 `:251-253` 记录的是**一手实测**：`return expr;` 会报 `CS0127`。两者不可能同时为真。静态复核能确认的是：当前编译路径是 **mcs**（`Source/UE-4.9.0-official/…/CSConsole/ScriptEvaluator.cs:2` `using Mono.CSharp;`、`:22` `base(BuildContext(tw))`、`:72` `LanguageVersion.Experimental`、`:73` `Target.Library`），值通道是 `ConsoleController.cs:180-183` 的 `repl.Invoke(ref ret)` → `Invoked REPL, result: {ret}` 日志。**本计划不裁定谁对**（不能起游戏验证），只做两件事：(a) 在任务 1 的文案里把这件事**如实写成两个分支**（见下方交接文案）；(b) 把矛盾点写进本计划 §6 交接表，由主代理决定是否暂停计划 02 的该项验收。 |
| `MCP/index.js:4990-5024`（`post_map_marker` case） | 本计划的新 case 插在 `4985`（`get_map_structure` 的收尾 `}`）之后、`4987` 注释之前，**不碰** `post_map_marker`。 |
| `MCP/test/toolchainFixes.test.js:121` | 该断言切片 `function toolListLine(`（`MCP/index.js:3281`）→ `function buildMenuNode(`（`:3290`）。本计划**不在这两者之间插任何函数**，不碰该区间。 |
| `MCP/test/toolchainFixes.test.js:263-268` | 该断言切片 `case 'start_game': {`（`MCP/index.js:4371`）→ `case 'stop_game': {`（`:4371`）→ `case 'get_game_status': {`（`:4384`）。本计划的新 case 插在 `4985` 之后，**远离该区间**，不碰。 |
| `MCP/test/toolCountConsistency.test.js` | 全文 148 行，**无任何硬编码工具数**（`:24`/`:30`/`:45`/`:58` 全是自洽关系断言）。新增一个工具不破坏它 —— 但**必须同时**登记进 `TOOL_SUBGROUP` 与 `toolConfig.json`，否则 `menuCountBySubgroup`（`MCP/index.js:3392-3399`）与 `menuUniverseNames()`（`:3369-3376`，`3372-3373` 只收 `toolConfig.tools` 里已登记的键）会对不上。 |
| Roslyn 计划 `2026-09-27-ue-csharp-console-roslyn-upgrade.md` | 该计划的 `ReferenceScanner`（`:405-468`）与本计划**零编辑冲突**（本计划只新增 `UEModAssemblyIndex.cs`，不碰 `ReferenceScanner`），两者可以任意先后实施。本计划的归属 join **参考**它的 `LoadedModManager` 写法（`:448-461`）。 |

**交接文案（交给计划 02 的执行者，并进它重写 `:2937` 的那段 `notes` 里，不要另起一行、不要覆盖它已有的内容）：**

```text
求值回传方式：代码被编译进控制台的求值体，末尾那条语句是**裸表达式**（不带 return）时，
它的值经 "Invoked REPL, result: ..." 回显为 result —— 即 `var x = 5; x + 3;`。
不要写成 `return expr;`：mcs 求值器下会报 `CS0127: A return keyword must not be
followed by any expression when method returns void`。若本会话的控制台已被 Roslyn
计划升级为语句体求值器，`return expr;` 才是正确写法 —— 遇到 CS0127 说明该会话仍是
mcs 求值器，此时把 return 去掉即可，不要改写整段代码。
```

### 0.6 游戏未运行时，新工具怎么表现（明确契约）

`get_loaded_assemblies` 走 `callUnityExplorerAPI`（`MCP/index.js:736`），因此：

| 情形 | 发生什么 | 必须保证 |
|---|---|---|
| **游戏完全没运行** | `fetch` 连不上 → `callUnityExplorerAPI` 在 `:812-830` 捕获，返回 `{success:false, errorCode:'GAME_NOT_RUNNING'}`（或类似） | MCP case 转成 `answerAvailable:false` 的响应体，**正文必须写明「读的是游戏进程内的 AppDomain，游戏未运行时无法回答，这不等于该模组不存在」**，并指向 `rimworld.list_mods`（游戏运行后可用）。 |
| **游戏在主菜单** | UE HTTP 服务已启动（模组构造即起），`AppDomain.CurrentDomain.GetAssemblies()` 与 `LoadedModManager.RunningModsListForReading` 都可用 | **本工具刻意不加 `requireStage([STAGES.IN_GAME])`**（对照 `MCP/index.js:4422`/`:4433`/`:4452` 等需要进图的 UE 工具）。主菜单恰恰是最常问「什么已加载」的时机。`TOOL_PRECONDITION` 里写明「主菜单即可」。 |
| **游戏在加载中** | 同上，程序集清单可能尚未齐 | 响应里带 `appDomainAssemblyCount`，调用方自己判断规模；**不得**把加载中的短清单说成「没装」。 |
| **端点没部署（新 DLL 未生效）** | UE 返回 404 / 非 JSON | `callUnityExplorerAPI` 的 `readJson`（`:781-784`）对空 body 返回 `{}` → `success:false`。MCP case 走同一个失败分支，`errorCode` 透传。任务 4 步骤 6 给出一条可手敲的探针命令，让执行者能区分「端点没部署」与「游戏没运行」。 |

### 0.7 测试基础设施（真实命令，不要臆造）

工作目录 = `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger`

| 用途 | 命令 |
|---|---|
| 跑 MCP 侧 JS 测试（全部） | `Set-Location MCP; npm test`（= `node --test "test/*.test.js"`，`MCP/package.json:9`） |
| 跑 MCP 侧 JS 测试（单文件） | `Set-Location MCP; node --test test/modPresenceGuidance.test.js` |
| 跑本计划的 C# 离线测试 | `dotnet run --project Source/ModAssemblyIndexTests` |
| 跑既有 C# 离线测试（回归） | `dotnet run --project Source/HotReloadTests`、`dotnet run --project Source/ConfigJsonTests` |
| 构建模组程序集（**必须先关闭游戏**） | `dotnet build Source/UELoader/UELoader.csproj` |

**前置守卫：** `Source/Directory.Build.props:21-26` 的 `GuardRefs` 目标对 `Source/` 下**所有** csproj 生效，缺 `$(RimWorldManagedDir)\Assembly-CSharp.dll` 或 `$(HarmonyRefDir)\0Harmony.dll` 会直接报 MSBuild `Error`。换机器时用 `-p:RimWorldGameDir=<游戏根>` 或环境变量 `RIMWORLD_GAME_DIR` 指定。

**JS 测试守卫：** 任何 `import '../index.js'` 的测试文件都必须在 import **之前**写 `process.env.UESD_MCP_NO_START = '1'`，否则会启动 HTTP 服务器占 3000 端口（见 `MCP/test/toolchainFixes.test.js:7-9`、`MCP/test/toolCountConsistency.test.js:4-6`）。

**C# 离线测试守卫：** `Source/UELoader/UELoader.csproj` 是 SDK-style（`Microsoft.NET.Sdk`），`Source/UELoader/*.cs` **自动**纳入编译，不需要改 csproj —— 这一点对本计划很重要（§1）。而测试工程必须**只 link 纯逻辑文件**（形状照抄 `Source/ConfigJsonTests/ConfigJsonTests.csproj:11-14`），因为 `UELoader.dll` 本身依赖 Verse/Unity，脱离游戏编译不了。

**本仓库不是 git 仓库**（`UESDdebuger/` 下只有 `.gitignore`，无 `.git/`）。因此每个任务最后一步是「改动清单」核对，不是 `git commit`。

### 0.8 执行者不要做的事

- 不要改 `docs/mcp-吐槽板.md`（给条目加批注是主代理的活）。
- 不要为了让新工具「看起来更全」而去改 `rimworld.list_mods` 的 schema —— 那是 GABP 的，本项目改不了（`MCP/index.js:2512-2514`）。
- 不要给 `get_game_status` 加 `modRoots` 字段（§0.1 建议 2，裁决报告已否）。
- 不要在 `Source/UELoader/*.cs` 里引入对 `Microsoft.CodeAnalysis` / UnityEditor 的引用 —— 保持 `UEModAssemblyIndex.cs` 零依赖，这是它能离线测的唯一前提。
- 不要用 `execute_csharp_code` 去验证本计划的任何东西（它是写操作工具，且本流水线禁止调用；且它正是本条目说不可靠的路径）。
- 游戏没关闭就不要跑 `dotnet build Source/UELoader/UELoader.csproj`（`UELoader.csproj:92-96` 的 `DeployToAssemblies` 会往被游戏占用的 `Assemblies/UELoader.dll` 拷文件，必失败）。

---

## 1. 文件结构

### 新建

| 文件 | 职责 |
|---|---|
| `Source/UELoader/UEModAssemblyIndex.cs` | **纯逻辑**（只用 `System` / `System.Collections.Generic`，零 Verse / 零 Unity / 零 `System.Reflection`）：AppDomain 程序集清单 ↔ 模组归属的 join、`query` 过滤、非模组条目隐藏、`modsWithoutAssemblies` / 未归属计数、稳定排序。**它不碰 `Assembly`，只碰三个纯数据类** —— 这是它能被离线测的唯一前提。 |
| `Source/ModAssemblyIndexTests/ModAssemblyIndexTests.csproj` | net472 `Exe` 测试工程，`link` 上面那个源文件（形状逐字照抄 `Source/ConfigJsonTests/ConfigJsonTests.csproj`） |
| `Source/ModAssemblyIndexTests/Program.cs` | 手写 `Check()` runner（形状照抄 `Source/ConfigJsonTests/Program.cs:19-25`） |
| `MCP/test/modPresenceGuidance.test.js` | 本计划全部 JS 侧断言：文案、工具注册、菜单可见性、文档关键句 |

### 修改

| 文件 | 改动 |
|---|---|
| `MCP/index.js:2072` | `execute_csharp_code` 的 `description` 末尾补「表达式模式 / 不要写 `return` / CS0127」一段 |
| `MCP/index.js:2078` | `code` 参数的 `description`：示例列表里加一条「多语句 + 末尾裸表达式」的正例 |
| `MCP/index.js:2490` 之后 | `TOOLS.push({ name: 'get_loaded_assemblies', … })`（紧跟 `post_map_marker` 那个 push，形状照抄 `:2450-2490`） |
| `MCP/index.js:2912` 之后 | `TOOL_CATEGORY` 加 `get_loaded_assemblies: 'game_control'` |
| `MCP/index.js:2926` 之后 | `TOOL_PRECONDITION` 加 `get_loaded_assemblies` 一行 |
| `MCP/index.js:2937` **之后**（`get_map_plants` 行前） | `TOOL_USAGE_NOTES` 加 `get_loaded_assemblies` 一条；**`:2937` 本行本身一个字都不动**（计划 02 的地盘） |
| `MCP/index.js:3148` 之后 | `TOOL_SUBGROUP` 加 `'get_loaded_assemblies': 'game_control/game/mods'` |
| `MCP/index.js:3812` 之后 | `TOOL_ARG_ALIASES` 加 `get_loaded_assemblies` 一条 |
| `MCP/index.js:4985` 之后 | `handleToolCall` 的 `case 'get_loaded_assemblies'`（形状照抄 `:4974-4985`） |
| `MCP/toolConfig.json` | `tools` 里加 `"get_loaded_assemblies": true`；`_comment` 里的「共 24 个」改成「共 25 个」并在枚举里补上工具名 |
| `Source/UELoader/UEHttpHandler.cs:420` 之后 | `GetLoadedAssemblies(string query, bool includeNonMod)` + `SafeModRoot`（Verse/反射部分，只负责取数） |
| `Source/UELoader/UEHttpServer.cs:408` 之后 | `case "mods/loaded_assemblies":`（`GetGuard` + `GetQueryString` + `GetBoolQuery`，形状照抄 `:449-455`） |
| `README.md:167` 之后 | 「RimBridgeServer 镜像工具」表里补 `rimworld.list_mods` 的字段说明；`:158` 前新增「判断模组在不在场」小节 |
| `README.md:60` / `README.md:183` | 「默认压缩仅直接暴露 23 个」两处改为 25（算术见任务 5 步骤 2） |
| `AI-DEPLOYMENT.md:177` 之后 | 排障决策树加两行：「grep `Mods\` 零命中 ≠ 没装」「`get_loaded_assemblies` 报游戏未运行」 |

> `Source/UELoader/UELoader.csproj` 是 SDK-style（`Microsoft.NET.Sdk`），`Source/UELoader/*.cs` 自动纳入编译，**不需要改 csproj**。

---

## 2. 任务

### 任务 1：`execute_csharp_code` 的「表达式模式」文案（纯文案，零风险）

**为什么先做它：** 吐槽板 `:251-253` 是一手实测的踩坑（`CS0127`），而工具文案两处都没写；改两个字符串不碰任何控制流，**不可能碰碎** `MCP/test/toolchainFixes.test.js` 的两处源码切片断言（它们切的是 `3281→3290` 与 `4371→4384`，与本任务零交集）。

**为什么只改 `:2072`/`:2078` 不改 `:2937`：** `MCP/index.js:2937` 已被 `docs/issue-triage/plans/2026-10-01-02-execute-csharp-code-output-capture.md` 任务 4 整行重写（该计划 `:27`/`:130`/`:863`/`:869-872`）。两处改同一行 = 两个计划的执行者互相覆盖。交接文案见 §0.5。

**文件：**
- 修改：`MCP/index.js:2072`、`MCP/index.js:2078`
- 创建：`MCP/test/modPresenceGuidance.test.js`

- [ ] **步骤 1：写失败的测试**

  创建 `MCP/test/modPresenceGuidance.test.js`（本文件在后续任务里逐步追加断言；守卫与 harness 照抄 `MCP/test/toolchainFixes.test.js:1-13`）：

  ```javascript
  import { test } from 'node:test';
  import assert from 'node:assert/strict';
  import fs from 'node:fs';
  import path from 'node:path';
  import { fileURLToPath } from 'node:url';

  // 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
  process.env.UESD_MCP_NO_START = '1';

  const here = path.dirname(fileURLToPath(import.meta.url));
  const modRoot = path.join(here, '..', '..');
  const indexPath = path.join(here, '..', 'index.js');
  const source = fs.readFileSync(indexPath, 'utf-8');

  // 工具 description 所在那段（TOOLS 数组里 execute_csharp_code 那一条）
  const csharpDescBlock = (() => {
    const i = source.indexOf("name: 'execute_csharp_code',");
    assert.ok(i > 0, '找不到 execute_csharp_code 的工具定义');
    return source.slice(i, i + 1400);
  })();

  test('[12] execute_csharp_code：description 写明「表达式模式 / 不要写 return / CS0127」', () => {
    assert.match(csharpDescBlock, /CS0127/, 'description 必须点名 CS0127，否则调用方认不出这个错');
    assert.match(csharpDescBlock, /return/, '必须提到 return 这个关键字');
    assert.match(csharpDescBlock, /表达式/, '必须说明末尾是裸表达式');
  });

  test('[12] execute_csharp_code：code 参数示例里有一条「多语句 + 末尾裸表达式」的正例', () => {
    // 正例必须真的不带 return —— 否则示例本身就会踩 CS0127
    assert.match(csharpDescBlock, /var x = 5; x \+ 3;/,
      '应保留原文档里的裸表达式示例，并补一条多语句形态');
    assert.doesNotMatch(csharpDescBlock, /return\s+\w+;\s*"\s*,\s*"/,
      '示例列表里不得出现以 return 开头的条目（mcs 求值器下必报 CS0127）');
  });

  test('[12] execute_csharp_code：不许把「本计划」的占位符写进生产文案', () => {
    for (const bad of ['TODO', '待定', '后续实现', '待补充']) {
      assert.ok(!csharpDescBlock.includes(bad), `文案里不应出现占位符「${bad}」`);
    }
  });
  ```

- [ ] **步骤 2：运行测试验证失败**

  运行：`Set-Location MCP; node --test test/modPresenceGuidance.test.js`
  预期：前两条 FAIL（`CS0127` / `表达式` 尚未写进 `MCP/index.js:2072`），第三条 PASS。

- [ ] **步骤 3：改 `MCP/index.js:2072` 的 description**

  把 `MCP/index.js:2072` 整行替换为（保留原文全部内容，只在句尾追加）：

  ```javascript
    description: '[UE工具][需要进入地图] Execute C# code in UnityExplorer C# Console. Can run REPL code, define classes, or execute scripts. Only works after entering a game map and opening UnityExplorer C# Console panel at least once. ' +
      '⚠️ 求值回传：代码被编译进控制台的求值体，末尾那条语句写成**裸表达式**（不带 return）时，它的值经 "Invoked REPL, result: ..." 回显为 result —— 形如 "var x = 5; x + 3;"。' +
      '不要写成 "return expr;"：当前默认的 mcs 求值器下会报 CS0127（A return keyword must not be followed by any expression when method returns void）。' +
      '遇 CS0127 说明该会话仍是 mcs 求值器，把 return 去掉、让末尾表达式裸露出来即可，不要因此改写整段代码。',
  ```

- [ ] **步骤 4：改 `MCP/index.js:2078` 的 code 参数示例**

  把 `MCP/index.js:2078` 整行替换为：

  ```javascript
          description: 'C# code to execute. Examples: "Log(\"Hello\");", "var x = 5; x + 3;", "using UnityEngine; Debug.Log(\"test\");", "var sb = new System.Text.StringBuilder(); sb.Append(\"a\"); sb.ToString();" ' +
            '（末条示例示范「多语句 + 末尾裸表达式」形态；不要在末尾写 return，会报 CS0127）'
  ```

- [ ] **步骤 5：运行测试验证通过**

  运行：`Set-Location MCP; node --test test/modPresenceGuidance.test.js`
  预期：3 个用例全 PASS。

  再运行回归：`Set-Location MCP; npm test`
  预期：全绿。若 `toolchainFixes.test.js` 变红，说明改错了行 —— 本任务只允许动 `:2072` 与 `:2078` 两行字符串。

- [ ] **步骤 6：改动清单（本仓库非 git 仓库，无 commit）**

  | 文件 | 改了什么 |
  |---|---|
  | `MCP/index.js:2072` | `execute_csharp_code` 的 `description` 追加求值回传 / CS0127 说明 |
  | `MCP/index.js:2078` | `code` 参数 `description` 追加一条「多语句 + 末尾裸表达式」示例 + 禁止 `return` 提示 |
  | `MCP/test/modPresenceGuidance.test.js` | 新建，含 3 个用例 |

  交接（不落盘，由主代理或计划 02 的执行者处理）：把 §0.5 的「交接文案」并进计划 02 对 `MCP/index.js:2937` 的新 `notes`，并让计划 02 复核它 `:819` 验收行里 `return 1;` 与 CS0127 的矛盾。

### 任务 2：纯逻辑 `UEModAssemblyIndex` + 离线测试（零 Verse 依赖）

**为什么把它拆成独立任务：** `Source/UELoader/UEHttpHandler.cs` 的 771 行里每一行都 `using Verse;`（`:11`）且 `using UnityEngine;`（`:5`），脱离游戏**编译不了**。但本条目真正有决策含量的部分（join / 过滤 / 计数 / 排序）完全可以脱离游戏断言 —— 做法就是把它抽成一个只认纯数据、不认 `Assembly` 的静态类，测试工程 `link` 过去。形状沿用 `Source/ConfigJsonTests/ConfigJsonTests.csproj:11-14`。

**文件：**
- 创建：`Source/ModAssemblyIndexTests/ModAssemblyIndexTests.csproj`
- 创建：`Source/ModAssemblyIndexTests/Program.cs`
- 创建：`Source/UELoader/UEModAssemblyIndex.cs`

- [ ] **步骤 1：建测试工程**

  `Source/ModAssemblyIndexTests/ModAssemblyIndexTests.csproj`（逐字照抄 `Source/ConfigJsonTests/ConfigJsonTests.csproj` 的形状，只换命名与 link 目标）：

  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <OutputType>Exe</OutputType>
      <TargetFramework>net472</TargetFramework>
      <LangVersion>9.0</LangVersion>
      <Nullable>disable</Nullable>
      <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
      <RootNamespace>ModAssemblyIndexTests</RootNamespace>
      <AssemblyName>ModAssemblyIndexTests</AssemblyName>
    </PropertyGroup>
    <ItemGroup>
      <!-- 直接 link 被测源码：UEModAssemblyIndex 零 Verse/Unity 依赖，可脱离游戏编译 -->
      <Compile Include="..\UELoader\UEModAssemblyIndex.cs" Link="UEModAssemblyIndex.cs" />
    </ItemGroup>
  </Project>
  ```

- [ ] **步骤 2：写失败的测试 `Source/ModAssemblyIndexTests/Program.cs`**

  ```csharp
  using System;
  using System.Collections.Generic;
  using UELoader;

  namespace ModAssemblyIndexTests
  {
      /// <summary>
      /// UEModAssemblyIndex 离线测试（吐槽板 [12]）。
      ///
      /// 测的是「AppDomain 程序集清单 ↔ 模组归属」的 join / 过滤 / 计数 / 排序这套决策逻辑，
      /// 它的存在意义是钉死一条不能回退的性质：**「零命中」必须能被调用方与「枚举到 0 个」区分开**。
      /// 吐槽板那次假阴性就是这两者被当成同一件事的结果。
      ///
      /// 取数部分（AppDomain.CurrentDomain.GetAssemblies / LoadedModManager.RunningModsListForReading）
      /// 在 UEHttpHandler 里，离线不可测，见计划任务 3 的游戏内验收矩阵。
      /// </summary>
      internal static class Program
      {
          static int failures;

          static void Check(string name, bool cond, string detail = null)
          {
              Console.WriteLine((cond ? "PASS " : "FAIL ") + name
                  + (cond || string.IsNullOrEmpty(detail) ? "" : "  -> " + detail));
              if (!cond) failures++;
          }

          // 三种真实存在的模组根目录（实测各有实例，见吐槽板 [12] 与 triage 12 §3.2）：
          // 工坊 / 本地 Mods\ / 官方 Data\Core。任何一种都必须能被归到 packageId。
          const string WORKSHOP = @"C:\SteamLibrary\steamapps\workshop\content\294100\2988801276";
          const string LOCALMODS = @"C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger";
          const string OFFICIAL = @"C:\SteamLibrary\steamapps\common\RimWorld\Data\Core";

          static ModAssemblyEntry Asm(string name, string location, string version = "1.0.0.0")
          {
              return new ModAssemblyEntry { AssemblyName = name, Version = version, Location = location };
          }

          static ModOwnerEntry Mod(string pkg, string name, string rootDir, params ModAssemblyEntry[] asms)
          {
              return new ModOwnerEntry
              {
                  PackageId = pkg,
                  ModName = name,
                  RootDir = rootDir,
                  Assemblies = new List<ModAssemblyEntry>(asms)
              };
          }

          /// <summary>吐槽板上那份真实清单：ancot 在工坊、uesddebuger 在本地、ludeon.rimworld 在 Data\Core。</summary>
          static List<ModAssemblyEntry> SampleAppDomain()
          {
              return new List<ModAssemblyEntry>
              {
                  Asm("Assembly-CSharp", @"C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\Assembly-CSharp.dll", "1.6.9676.0"),
                  Asm("AncotLibrary", WORKSHOP + @"\1.6\Assemblies\AncotLibrary.dll"),
                  Asm("UELoader", LOCALMODS + @"\Assemblies\UELoader.dll"),
                  Asm("UnityEngine.CoreModule", @"C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed\UnityEngine.CoreModule.dll"),
              };
          }

          static List<ModOwnerEntry> SampleMods()
          {
              return new List<ModOwnerEntry>
              {
                  Mod("ancot.ancotlibrary", "Ancot Library", WORKSHOP,
                      Asm("AncotLibrary", WORKSHOP + @"\1.6\Assemblies\AncotLibrary.dll")),
                  Mod("uesddebuger.debug.unityexplorer", "UESDdebuger", LOCALMODS,
                      Asm("UELoader", LOCALMODS + @"\Assemblies\UELoader.dll")),
                  Mod("ludeon.rimworld", "Core", OFFICIAL),
              };
          }

          static int Main()
          {
              // ---- 1) 三种根目录都能归到 packageId（核心：吐槽板上那次假阴性）
              var r1 = ModAssemblyIndex.Build(SampleAppDomain(), SampleMods(), "", false);
              Check("默认只回模组程序集", r1.Rows.Count == 2, "Rows=" + r1.Rows.Count);
              var ancot = Find(r1, "AncotLibrary");
              Check("工坊程序集归到 ancot.ancotlibrary", ancot != null && ancot.PackageId == "ancot.ancotlibrary",
                  ancot == null ? "<无该行>" : ancot.PackageId);
              Check("工坊行带回 ModName 与 RootDir",
                  ancot != null && ancot.ModName == "Ancot Library" && ancot.RootDir == WORKSHOP,
                  ancot == null ? "<无该行>" : ancot.ModName + " / " + ancot.RootDir);
              var uel = Find(r1, "UELoader");
              Check("本地 Mods\\ 程序集归到 uesddebuger", uel != null && uel.PackageId == "uesddebuger.debug.unityexplorer",
                  uel == null ? "<无该行>" : uel.PackageId);

              // ---- 2) Data\Core 这类官方模组根不在前两者里，也必须能归（证明来源目录不止两个）
              var withOfficial = SampleAppDomain();
              withOfficial.Add(Asm("CoreAssembly", OFFICIAL + @"\Assemblies\CoreAssembly.dll"));
              var r2 = ModAssemblyIndex.Build(withOfficial, SampleMods(), "", false);
              var core = Find(r2, "CoreAssembly");
              Check("Data\\Core 程序集归到 ludeon.rimworld", core != null && core.PackageId == "ludeon.rimworld",
                  core == null ? "<无该行>" : core.PackageId);
              Check("无程序集的模组进 modsWithoutAssemblies",
                  r2.ModsWithoutAssemblies.Count == 0 || r2.ModsWithoutAssemblies[0] == "ancot.ancotlibrary",
                  "实际=" + string.Join(",", r2.ModsWithoutAssemblies));

              // ---- 3) 非模组程序集默认隐藏，includeNonMod=true 才出现且归属为 null
              var r3 = ModAssemblyIndex.Build(SampleAppDomain(), SampleMods(), "", false);
              Check("Assembly-CSharp 默认不出现", Find(r3, "Assembly-CSharp") == null);
              Check("非模组条目被计数而非丢弃", r3.HiddenNonModCount == 2, "HiddenNonModCount=" + r3.HiddenNonModCount);
              var r4 = ModAssemblyIndex.Build(SampleAppDomain(), SampleMods(), "", true);
              var acs = Find(r4, "Assembly-CSharp");
              Check("includeNonMod=true 时出现", acs != null);
              Check("非模组行 OwnedByMod=false 且 PackageId=null",
                  acs != null && !acs.OwnedByMod && acs.PackageId == null && acs.RootDir == null,
                  acs == null ? "<无该行>" : acs.OwnedByMod + "/" + (acs.PackageId ?? "<null>"));

              // ---- 4) 反假阴性：零命中时 AppDomainCount 必须仍然是真实总数
              var r5 = ModAssemblyIndex.Build(SampleAppDomain(), SampleMods(), "zzzz", false);
              Check("零命中 → Rows 为空", r5.Rows.Count == 0, "Rows=" + r5.Rows.Count);
              Check("零命中 → AppDomainCount 仍是 4（可与「枚举到 0 个」区分）", r5.AppDomainCount == 4,
                  "AppDomainCount=" + r5.AppDomainCount);
              Check("零命中 → ModAssemblyCount 仍回报加载器口径", r5.ModAssemblyCount == 2,
                  "ModAssemblyCount=" + r5.ModAssemblyCount);

              // ---- 5) query 过滤：命中工坊那一行，且大小写不敏感
              var r6 = ModAssemblyIndex.Build(SampleAppDomain(), SampleMods(), "ancot", false);
              Check("query=ancot 命中一行", r6.Rows.Count == 1 && r6.Rows[0].AssemblyName == "AncotLibrary",
                  "count=" + r6.Rows.Count);
              var r7 = ModAssemblyIndex.Build(SampleAppDomain(), SampleMods(), "ANCOT", false);
              Check("query 大小写不敏感", r7.Rows.Count == 1, "count=" + r7.Rows.Count);
              var r8 = ModAssemblyIndex.Build(SampleAppDomain(), SampleMods(), "2988801276", false);
              Check("query 命中 Location 里的 workshopId", r8.Rows.Count == 1, "count=" + r8.Rows.Count);

              // ---- 6) Location 读不到的动态程序集：计入、标注，**不得静默丢弃**
              var withDynamic = SampleAppDomain();
              var dyn = new ModAssemblyEntry { AssemblyName = "RoslynDynamic", Version = "0.0.0.0", Location = null, LocationUnavailable = true };
              withDynamic.Add(dyn);
              var r9 = ModAssemblyIndex.Build(withDynamic, SampleMods(), "", true);
              var dynRow = Find(r9, "RoslynDynamic");
              Check("动态程序集仍然出现", dynRow != null);
              Check("动态程序集 LocationUnavailable=true 且 Location=null",
                  dynRow != null && dynRow.LocationUnavailable && dynRow.Location == null);
              Check("动态程序集计入 LocationUnknownCount", r9.LocationUnknownCount == 1,
                  "LocationUnknownCount=" + r9.LocationUnknownCount);
              Check("动态程序集计入 AppDomainCount（总数不因读不到 Location 而缩水）",
                  r9.AppDomainCount == 5, "AppDomainCount=" + r9.AppDomainCount);

              // ---- 7) 模组加载器报了、但 AppDomain 里 join 不上 → Unattributed > 0
              var withOrphan = SampleMods();
              withOrphan.Add(Mod("ghost.mod", "Ghost", @"C:\nowhere",
                  Asm("GhostAsm", @"C:\nowhere\Assemblies\GhostAsm.dll")));
              var r10 = ModAssemblyIndex.Build(SampleAppDomain(), withOrphan, "", false);
              Check("加载器有、AppDomain 没有的条目被计入 Unattributed",
                  r10.UnattributedModAssemblyCount == 1, "Unattributed=" + r10.UnattributedModAssemblyCount);
              Check("正常情况下 Unattributed 为 0", r1.UnattributedModAssemblyCount == 0,
                  "Unattributed=" + r1.UnattributedModAssemblyCount);

              // ---- 8) 路径尾部分隔符差异不影响 join（Defensive，避免现场踩路径规范化坑）
              var modsSep = new List<ModOwnerEntry>
              {
                  Mod("sep.mod", "Sep", WORKSHOP, Asm("AncotLibrary", WORKSHOP + @"\1.6\Assemblies\AncotLibrary.dll\")),
              };
              var r11 = ModAssemblyIndex.Build(SampleAppDomain(), modsSep, "", false);
              Check("Location 尾部 \\ 与无尾分隔符仍能 join", r11.Rows.Count == 1 && r11.Rows[0].PackageId == "sep.mod",
                  "count=" + r11.Rows.Count);

              // ---- 9) 排序稳定：同 packageId 下按 AssemblyName 升序
              var many = new List<ModAssemblyEntry>
              {
                  Asm("ZetaAsm",  WORKSHOP + @"\1.6\Assemblies\ZetaAsm.dll"),
                  Asm("AlphaAsm", WORKSHOP + @"\1.6\Assemblies\AlphaAsm.dll"),
                  Asm("MidAsm",   WORKSHOP + @"\1.6\Assemblies\MidAsm.dll"),
              };
              var manyMods = new List<ModOwnerEntry> { Mod("m", "M", WORKSHOP, many.ToArray()) };
              var r12 = ModAssemblyIndex.Build(many, manyMods, "", false);
              Check("同 packageId 下按 AssemblyName 升序",
                  r12.Rows.Count == 3 && r12.Rows[0].AssemblyName == "AlphaAsm"
                  && r12.Rows[1].AssemblyName == "MidAsm" && r12.Rows[2].AssemblyName == "ZetaAsm",
                  r12.Rows.Count == 3 ? string.Join(",", Names(r12)) : "count=" + r12.Rows.Count);

              // ---- 10) 空/缺省输入不崩（HTTP 线程上任何一步抛异常都不该让整个端点挂掉）
              var r13 = ModAssemblyIndex.Build(null, null, null, false);
              Check("全 null 输入返回空结果而非抛异常",
                  r13.Rows.Count == 0 && r13.AppDomainCount == 0 && r13.UnattributedModAssemblyCount == 0);

              Console.WriteLine(failures == 0 ? "ALL PASS" : (failures + " FAILED"));
              return failures == 0 ? 0 : 1;
          }

          static ModAssemblyRow Find(ModAssemblyIndexResult r, string assemblyName)
          {
              foreach (var row in r.Rows) if (row.AssemblyName == assemblyName) return row;
              return null;
          }

          static List<string> Names(ModAssemblyIndexResult r)
          {
              var list = new List<string>();
              foreach (var row in r.Rows) list.Add(row.AssemblyName);
              return list;
          }
      }
  }
  ```

- [ ] **步骤 3：运行测试验证失败**

  运行：`dotnet run --project Source/ModAssemblyIndexTests`
  预期：编译失败，报 `error CS0234: The type or namespace name 'UEModAssemblyIndex' does not exist in the namespace 'UELoader'`（源文件还没建）。

- [ ] **步骤 4：写最少实现代码 `Source/UELoader/UEModAssemblyIndex.cs`**

  ```csharp
  using System;
  using System.Collections.Generic;

  namespace UELoader
  {
      /// <summary>
      /// AppDomain 程序集清单 ↔ 模组归属 的**纯连接逻辑**（吐槽板 [12]）。
      ///
      /// 刻意零依赖：不 using Verse / UnityEngine / System.Reflection，不碰 Assembly、不碰 ModContentPack。
      /// 取数由 UEHttpHandler 负责（那里才有 AppDomain 与 LoadedModManager），取完塞进下面这三个
      /// 纯数据类再调本类。这样整段决策逻辑可以在 Source/ModAssemblyIndexTests 里脱离游戏离线断言。
      ///
      /// 为什么值得单独抽出来：吐槽板那次假阴性的根因不是「没有工具」，而是
      /// 「零命中」与「枚举到 0 个」被调用方当成同一件事。本类用 AppDomainCount /
      /// LocationUnknownCount / UnattributedModAssemblyCount 三个计数把这件事在协议层拆开。
      /// </summary>

      /// <summary>一个已加载程序集的纯数据快照。</summary>
      public class ModAssemblyEntry
      {
          public string AssemblyName;
          public string Version;
          /// <summary>磁盘路径；动态程序集为 null。</summary>
          public string Location;
          /// <summary>Location 读取失败（或本就为空）时为 true —— 把「读不到」与「不在盘上」区分开。</summary>
          public bool LocationUnavailable;
      }

      /// <summary>模组加载器（LoadedModManager）汇报的一个模组及其程序集清单。</summary>
      public class ModOwnerEntry
      {
          public string PackageId;
          public string ModName;
          /// <summary>模组根目录；反射探测不到时为 null（本类绝不猜测、绝不硬编码）。</summary>
          public string RootDir;
          /// <summary>mod.assemblies.loadedAssemblies 的快照；null 表示拿不到。</summary>
          public List<ModAssemblyEntry> Assemblies;
      }

      /// <summary>连接后的一行输出。</summary>
      public class ModAssemblyRow
      {
          public string AssemblyName;
          public string Version;
          public string Location;
          public bool LocationUnavailable;
          public bool OwnedByMod;
          public string PackageId;
          public string ModName;
          public string RootDir;
      }

      /// <summary>整次 Build 的输出。三个计数是反假阴性的核心，别删。</summary>
      public class ModAssemblyIndexResult
      {
          public List<ModAssemblyRow> Rows = new List<ModAssemblyRow>();
          /// <summary>AppDomain 清单总条数（含被 query 过滤掉与 Location 读不到的）。零命中时它仍真实。</summary>
          public int AppDomainCount;
          /// <summary>模组加载器汇报的程序集总条数。</summary>
          public int ModAssemblyCount;
          /// <summary>加载器报了、但没能在 AppDomain 清单里 join 上的条数（&gt;0 说明两边口径不一致）。</summary>
          public int UnattributedModAssemblyCount;
          /// <summary>因 includeNonMod=false 而未返回的「非模组程序集」条数。</summary>
          public int HiddenNonModCount;
          /// <summary>Location 为 null / 读取失败的条数。</summary>
          public int LocationUnknownCount;
          /// <summary>加载器里报告了、但一个程序集都没有的模组 packageId。</summary>
          public List<string> ModsWithoutAssemblies = new List<string>();
      }

      public static class ModAssemblyIndex
      {
          /// <param name="appDomain">进程内已加载程序集快照，不能为 null 语义之外的 null。</param>
          /// <param name="mods">模组加载器汇报的清单；传 null 当作空清单。</param>
          /// <param name="query">大小写不敏感的子串过滤，命中 AssemblyName / Location / PackageId 任一即可；null 或空白表示不过滤。</param>
          /// <param name="includeNonMod">false（默认）= 只回「归到某个模组」的程序集；true = 连 vanilla/引擎程序集一起回。</param>
          public static ModAssemblyIndexResult Build(
              List<ModAssemblyEntry> appDomain,
              List<ModOwnerEntry> mods,
              string query,
              bool includeNonMod)
          {
              var r = new ModAssemblyIndexResult();
              var list = appDomain ?? new List<ModAssemblyEntry>();
              r.AppDomainCount = list.Count;

              // location(归一化) → 归属模组。同一个 location 出现两次时取第一个，不覆盖。
              var ownerByLocation = new Dictionary<string, ModOwnerEntry>(StringComparer.OrdinalIgnoreCase);
              var modList = mods ?? new List<ModOwnerEntry>();
              foreach (var m in modList)
              {
                  if (m == null) continue;
                  if (m.Assemblies == null || m.Assemblies.Count == 0)
                  {
                      if (!string.IsNullOrEmpty(m.PackageId)) r.ModsWithoutAssemblies.Add(m.PackageId);
                      continue;
                  }
                  r.ModAssemblyCount += m.Assemblies.Count;
                  foreach (var a in m.Assemblies)
                  {
                      if (a == null) continue;
                      string key = NormKey(a.Location);
                      if (key.Length > 0 && !ownerByLocation.ContainsKey(key)) ownerByLocation[key] = m;
                  }
              }

              var attributed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
              string q = (query ?? "").Trim();

              foreach (var a in list)
              {
                  if (a == null) continue;
                  bool hasLoc = !string.IsNullOrEmpty(a.Location);
                  if (!hasLoc) r.LocationUnknownCount++;

                  ModOwnerEntry owner = null;
                  string key = NormKey(a.Location);
                  if (hasLoc && key.Length > 0 && ownerByLocation.TryGetValue(key, out var found)) owner = found;
                  bool owned = owner != null;
                  if (owned) attributed.Add(key);

                  // 归属与过滤是两件事：先按归属决定回不回，再按 query 决定给不给。
                  if (!owned && !includeNonMod) { r.HiddenNonModCount++; continue; }
                  if (q.Length > 0 && !Matches(a, owner, q)) continue;

                  r.Rows.Add(new ModAssemblyRow
                  {
                      AssemblyName = a.AssemblyName,
                      Version = a.Version,
                      Location = a.Location,
                      LocationUnavailable = a.LocationUnavailable || !hasLoc,
                      OwnedByMod = owned,
                      PackageId = owned ? owner.PackageId : null,
                      ModName = owned ? owner.ModName : null,
                      RootDir = owned ? owner.RootDir : null
                  });
              }

              int unattributed = ownerByLocation.Count - attributed.Count;
              r.UnattributedModAssemblyCount = unattributed > 0 ? unattributed : 0;

              r.Rows.Sort((x, y) =>
              {
                  int c = string.Compare(x.PackageId ?? "", y.PackageId ?? "", StringComparison.OrdinalIgnoreCase);
                  if (c != 0) return c;
                  return string.Compare(x.AssemblyName ?? "", y.AssemblyName ?? "", StringComparison.OrdinalIgnoreCase);
              });

              return r;
          }

          /// <summary>路径归一化：Trim 两端空白 + 去掉尾部目录分隔符。刻意不做 GetFullPath ——
          /// 那是 IO/文件系统相关的行为，会让本类在离线测试里依赖当前工作目录。</summary>
          static string NormKey(string location)
          {
              if (string.IsNullOrEmpty(location)) return "";
              return location.Trim().TrimEnd('\\', '/');
          }

          static bool Matches(ModAssemblyEntry a, ModOwnerEntry owner, string q)
          {
              if (Contains(a.AssemblyName, q)) return true;
              if (Contains(a.Location, q)) return true;
              if (owner != null && Contains(owner.PackageId, q)) return true;
              return false;
          }

          static bool Contains(string s, string q)
          {
              return !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
          }
      }
  }
  ```

- [ ] **步骤 5：运行测试验证通过**

  运行：`dotnet run --project Source/ModAssemblyIndexTests`
  预期：打印 25 条 `PASS`，末行 `ALL PASS`，退出码 0。

  若 `HiddenNonModCount` 一条不过，检查是不是把 `!owned && !includeNonMod` 的 continue 写在了 `LocationUnknownCount++` 之前 —— 顺序反了会把动态程序集从两个计数里都抹掉。

- [ ] **步骤 6：回归（确认没有碰坏既有离线测试）**

  运行：`dotnet run --project Source/HotReloadTests` 与 `dotnet run --project Source/ConfigJsonTests`
  预期：两者都仍是 `ALL PASS`（本任务只新增文件，不改任何既有源码，理论上必然通过；跑一遍是为了确认 `Source/UELoader/` 下新增 `.cs` 没有被某个工程用通配符纳入而引入重复类型）。

- [ ] **步骤 7：改动清单**

  | 文件 | 改了什么 |
  |---|---|
  | `Source/UELoader/UEModAssemblyIndex.cs` | 新建。三个纯数据类（`ModAssemblyEntry` / `ModOwnerEntry` / `ModAssemblyRow`）+ `ModAssemblyIndexResult` + `static class ModAssemblyIndex.Build(...)`。零 Verse / 零 Unity / 零 `System.Reflection`。 |
  | `Source/ModAssemblyIndexTests/ModAssemblyIndexTests.csproj` | 新建。net472 Exe，`link` 上面那个源文件。 |
  | `Source/ModAssemblyIndexTests/Program.cs` | 新建。25 条 `Check()` 断言，覆盖工坊/本地/官方三种根目录、反假阴性计数、动态程序集不丢、尾分隔符 join、稳定排序、null 输入。 |

  **本任务刻意不做**：`UEHttpHandler` 不动、`UEHttpServer` 不动、`MCP/` 不动、游戏不启动、不重编译模组 DLL。产物是一段**尚未被调用**的纯逻辑，下一任务才接线。

### 任务 3：UE 端点 `GET /mods/loaded_assemblies`（Verse 取数 + 路由）

**这一步会改 `.cs`，因此需要重编译模组 DLL 才生效。** 与本仓库其它纯 MCP 侧计划不同，这是本计划的成本所在 —— 它换来的是**唯一能回答「这个 dll 是不是真在进程里」的数据源**。

**为什么不在 HTTP 线程上包 `RunOnMain`：** `UEHttpHandler.cs:387-389` 的注释明写「主菜单时主线程调度器不可用」，而主菜单恰恰是最常问「什么已加载」的时机；且 `UEHttpHandler.cs:365-383` 已经在 HTTP 线程上直接读 `ModsConfig.ActiveModsInLoadOrder`，注释明写「HTTP 线程读取安全」。本任务照同一先例直读，并把两处 Verse 访问各自包 try/catch，失败时返回**明确的** `ASSEMBLY_SCAN_FAILED` 而不是静默返回空清单。

**文件：**
- 修改：`Source/UELoader/UEHttpHandler.cs`（在 `:420` 的 `GetStatus` 收尾 `}` 之后、`:422` 的 `CreateHook` 之前插入）
- 修改：`Source/UELoader/UEHttpServer.cs`（在 `:408` 的 `rimbridge/status` case 之后、`:410` 的 `area/delete` 之前插入）

- [ ] **步骤 1：写 handler `Source/UELoader/UEHttpHandler.cs`**

  在 `GetStatus()` 的收尾（`:420` 的 `}`）与 `CreateHook`（`:422`）之间插入以下两个成员。`Success` / `Error` 是本文件既有的静态助手（`:753` / `:758`），`using` 列表（`:1-11`）已含 `System` / `System.Collections.Generic` / `System.Reflection` / `Verse`，**不需要加任何 using**：

  ```csharp
        /// <summary>
        /// 模组程序集级在场判定（吐槽板 [12]）。读的是**游戏进程内**的两份清单：
        ///   1) AppDomain.CurrentDomain.GetAssemblies()  —— 「进程里真加载了哪些程序集」的唯一定义
        ///   2) LoadedModManager.RunningModsListForReading → mod.assemblies.loadedAssemblies —— 归属
        /// 连接 / 过滤 / 计数全部委托给 UEModAssemblyIndex（纯逻辑、可离线测）。
        ///
        /// 刻意不读磁盘目录：Steam 创意工坊模组不在 &lt;Game&gt;/Mods 下（实测 20/34 个模组程序集在工坊目录，
        /// 见 docs/csharp-console-compiler-upgrade-feasibility.md:318-319），扫目录必然假阴性。
        /// 也不硬编码任何模组根目录 —— rootDir 反射拿不到就返回 null，交给 rimworld.list_mods.source 去说。
        ///
        /// 直读不 RunOnMain 的理由：GetStatus() 已在 HTTP 线程直读 ModsConfig.ActiveModsInLoadOrder
        /// （:365-383，注释写明 HTTP 线程读取安全）；且主菜单时主线程调度器不可用（:387-389），
        /// 包了 RunOnMain 这个工具就会在「最需要它的时刻」不可用。
        /// </summary>
        public static Dictionary<string, object> GetLoadedAssemblies(string query, bool includeNonMod)
        {
            // ---- ① AppDomain 快照。Location 对动态程序集会抛，必须 catch 后保留该行（不得 continue 丢弃）
            var appDomainEntries = new List<ModAssemblyEntry>();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == null) continue;
                string name = null, version = null, location = null;
                bool locationErr = false;
                try { name = asm.GetName().Name; } catch { }
                try { version = asm.GetName().Version != null ? asm.GetName().Version.ToString() : null; } catch { }
                try { location = asm.Location; } catch { locationErr = true; }
                // 名与址都拿不到 = 这条程序集不构成任何证据，丢掉不损失信息
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(location)) continue;
                appDomainEntries.Add(new ModAssemblyEntry
                {
                    AssemblyName = name,
                    Version = version,
                    Location = location,
                    LocationUnavailable = locationErr || string.IsNullOrEmpty(location)
                });
            }

            // ---- ② 模组加载器快照（归属来源）
            var owners = new List<ModOwnerEntry>();
            List<ModContentPack> running;
            try
            {
                running = new List<ModContentPack>(LoadedModManager.RunningModsListForReading);
            }
            catch (Exception ex)
            {
                // 读不到加载器 ≠ 「没有模组」。必须显式报错，绝不返回空清单冒充答案。
                return Error(500, "读取 LoadedModManager.RunningModsListForReading 失败: " + ex.Message,
                             "ASSEMBLY_SCAN_FAILED");
            }

            foreach (ModContentPack mod in running)
            {
                if (mod == null) continue;
                var asms = new List<ModAssemblyEntry>();
                try
                {
                    // ⚠️ mod.assemblies 是「字段」（Verse.ModAssemblyHandler），程序集列表是它的
                    //    「字段」loadedAssemblies。写 GetProperty("assemblies") 会拿到 null 直接 NRE；
                    //    handler.assemblies 不存在；猜成员名会连着编译失败。
                    //    原始记录：docs/csharp-console-compiler-upgrade-feasibility.md:321-323
                    if (mod.assemblies != null && mod.assemblies.loadedAssemblies != null)
                    {
                        foreach (Assembly a in mod.assemblies.loadedAssemblies)
                        {
                            if (a == null) continue;
                            string n = null, v = null, l = null;
                            bool le = false;
                            try { n = a.GetName().Name; } catch { }
                            try { v = a.GetName().Version != null ? a.GetName().Version.ToString() : null; } catch { }
                            try { l = a.Location; } catch { le = true; }
                            asms.Add(new ModAssemblyEntry { AssemblyName = n, Version = v, Location = l, LocationUnavailable = le });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 单个模组读失败只影响该模组，不让整个端点挂掉
                    UEHttpLog.Warning($"[UEHttp] GetLoadedAssemblies mod '{mod.PackageId}' failed: {ex.Message}");
                }
                owners.Add(new ModOwnerEntry
                {
                    PackageId = mod.PackageId,
                    ModName = mod.Name,
                    RootDir = SafeModRoot(mod),
                    Assemblies = asms
                });
            }

            // ---- ③ 纯连接
            var idx = ModAssemblyIndex.Build(appDomainEntries, owners, query, includeNonMod);

            var rows = new List<Dictionary<string, object>>();
            foreach (ModAssemblyRow row in idx.Rows)
            {
                rows.Add(new Dictionary<string, object>
                {
                    { "assemblyName", row.AssemblyName },
                    { "version", row.Version },
                    { "location", row.Location },
                    { "locationUnavailable", row.LocationUnavailable },
                    { "ownedByMod", row.OwnedByMod },
                    { "packageId", row.PackageId },
                    { "modName", row.ModName },
                    { "modRootDir", row.RootDir }
                });
            }

            var warnings = new List<string>();
            if (idx.UnattributedModAssemblyCount > 0)
                warnings.Add($"{idx.UnattributedModAssemblyCount} 个模组加载器汇报的程序集未能在 AppDomain 清单里对上，两侧口径不一致。");
            if (idx.LocationUnknownCount > 0)
                warnings.Add($"{idx.LocationUnknownCount} 个已加载程序集读不到磁盘路径（动态程序集），已保留条目但无法按路径判断来源。");
            if (includeNonMod && idx.HiddenNonModCount == 0)
                warnings.Add("includeNonMod=true 但没有任何非模组程序集返回，请确认是否漏了参数传递。");

            return Success(new Dictionary<string, object>
            {
                // 口径显式回显：调用方必须知道这是「进程内」口径，不是磁盘口径
                { "scope", "appdomain" },
                { "sourceOfTruth", "AppDomain.CurrentDomain.GetAssemblies() + LoadedModManager.RunningModsListForReading" },
                { "query", string.IsNullOrEmpty(query) ? null : query },
                { "includeNonMod", includeNonMod },
                // 反假阴性三计数：assemblies 为空时，靠这三个数区分「没匹配上」与「枚举到 0 个」
                { "appDomainAssemblyCount", idx.AppDomainCount },
                { "modAssemblyCount", idx.ModAssemblyCount },
                { "locationUnknownCount", idx.LocationUnknownCount },
                { "unattributedModAssemblyCount", idx.UnattributedModAssemblyCount },
                { "hiddenNonModCount", idx.HiddenNonModCount },
                { "rowCount", rows.Count },
                { "truncated", false },
                { "assemblies", rows },
                { "modsWithoutAssemblies", idx.ModsWithoutAssemblies },
                { "warnings", warnings },
                { "note", "判「装没装/启没启用/来源目录」请用 rimworld.list_mods（packageId + rootDir + source + enabled + loadedInSession）；本工具只回答「哪些程序集真的在进程里」。不要用 <游戏根>\\Mods 目录名 grep 判存在性——工坊模组不在该目录下。" }
            });
        }

        /// <summary>反射探测 ModContentPack 的模组根目录。**探测不到就返回 null**：
        /// 少给一个字段好过用硬编码的 &lt;Game&gt;/Mods 或 steamapps\\workshop\\content\\294100 去猜 ——
        /// 写死会把「来源目录不止这两个」（实测还有 Data\\Core 等 OfficialModsFolder）固化错。
        /// </summary>
        static string SafeModRoot(ModContentPack mod)
        {
            if (mod == null) return null;
            try
            {
                PropertyInfo prop = typeof(ModContentPack).GetProperty("RootDir");
                if (prop != null) return prop.GetValue(mod, null) as string;
            }
            catch { }
            try
            {
                FieldInfo field = typeof(ModContentPack).GetField("RootDir");
                if (field != null) return field.GetValue(mod) as string;
            }
            catch { }
            return null;
        }
  ```

- [ ] **步骤 2：加路由 `Source/UELoader/UEHttpServer.cs`**

  在 `case "rimbridge/status":`（`:406-408`）与 `case "area/delete":`（`:410`）之间插入：

  ```csharp
                    // ---------- 模组程序集级在场判定（UEHttpHandler，读 AppDomain + LoadedModManager） ----------
                    // ⚠️ 读的是游戏进程内，不是磁盘：主菜单即可用，不需要进图（因此不用 RunOnMain）。
                    case "mods/loaded_assemblies":
                        result = GetGuard(request, () =>
                            UEHttpHandler.GetLoadedAssemblies(
                                GetQueryString(request, "query"),
                                GetBoolQuery(request, "includeNonMod")));
                        break;
  ```

  `GetGuard`（`:738`）/ `GetQueryString` / `GetBoolQuery` 都是本文件既有助手，用法照抄 `case "debugaction/list"`（`:449-455`）。**不需要改 csproj**（SDK-style 自动纳入编译），也不需要改 `switch` 的任何其它分支。

- [ ] **步骤 3：编译（必须先关闭游戏）**

  运行：`dotnet build Source/UELoader/UELoader.csproj`
  预期：`Build succeeded`。若报 `MSB3021` / 文件占用，说明游戏还开着 —— **关掉游戏再重跑，不要试图绕过 `DeployToAssemblies`**。

  若报 `CS0246: The type or namespace name 'ModAssemblyEntry' could not be found`，说明 `UEModAssemblyIndex.cs` 不在 `Source/UELoader/` 下 —— 检查任务 2 的文件名与目录。

- [ ] **步骤 4：回归（确认纯逻辑没被接线改坏）**

  运行：`dotnet run --project Source/ModAssemblyIndexTests`、`dotnet run --project Source/HotReloadTests`、`dotnet run --project Source/ConfigJsonTests`
  预期：三者都 `ALL PASS`。本任务不改 `UEModAssemblyIndex.cs`，这一跑是为了确认重编译没把纯逻辑的签名带偏。

- [ ] **步骤 5：游戏内验收矩阵（必须人工执行 —— 本计划全程不起游戏）**

  关闭并重启游戏后（MCP 侧任务 4 还没接，所以**先用 curl 直打 UE 端点**验，端口读 `MCP/ports.json` 的 `ueHttpPort`，不是 3001）：

  | # | 前置 | 请求 | 预期（**逐条对照实际 payload**） |
  |---|---|---|---|
  | 1 | 游戏在**主菜单** | `GET /mods/loaded_assemblies?query=ancot` | `success:true`；`scope="appdomain"`；`assemblies` 恰 1 条，`packageId="ancot.ancotlibrary"`、`assemblyName="AncotLibrary"`、`location` 以 `…\workshop\content\294100\` 开头；`appDomainAssemblyCount > 0`。**这一步是本条目存在的全部理由 —— 主菜单就能证伪那次假阴性。** |
  | 2 | 同上 | `GET /mods/loaded_assemblies?query=zzz-no-such-assembly` | `success:true`；`assemblies` 为 `[]`；**`appDomainAssemblyCount` 仍是第 1 步那个真实总数**；`warnings` 为空数组。 |
  | 3 | 同上 | `GET /mods/loaded_assemblies`（无 query） | `rowCount == modAssemblyCount`；每条 `ownedByMod=true` 且 `packageId` 非空；**不得**出现 `Assembly-CSharp`（`includeNonMod` 默认 false）。 |
  | 4 | 同上 | `GET /mods/loaded_assemblies?includeNonMod=true` | 多出 `Assembly-CSharp` / `UnityEngine.*` 等；它们 `ownedByMod=false` 且 `packageId=null`、`modRootDir=null`。 |
  | 5 | 同上 | `GET /mods/loaded_assemblies?includeNonMod=false` | `hiddenNonModCount` 恰等于第 4 步多出来的条数（**计数对得上 = 没静默丢条目**）。 |
  | 6 | 游戏**关闭** | 同一 URL | 连不上（connection refused）。这正是任务 4 里 MCP 侧失败分支要转成 `answerAvailable:false` 的那个输入。 |
  | 7 | 游戏在**加载中** | `GET /mods/loaded_assemblies` | `success:true`，`appDomainAssemblyCount` 可能偏小。**记下这个数**，它提醒调用方「加载中清单不全」。 |

  任一行不符：**停下**，按 `assemblies` / 三个计数 / `warnings` 定位是哪一层（AppDomain 枚举 / LoadedModManager 读取 / join / 过滤）出的问题，不要靠改文案掩盖。

- [ ] **步骤 6：改动清单**

  | 文件 | 改了什么 |
  |---|---|
  | `Source/UELoader/UEHttpHandler.cs` | 新增 `public static Dictionary<string, object> GetLoadedAssemblies(string query, bool includeNonMod)` 与 `static string SafeModRoot(ModContentPack mod)`，插在 `:420` 与 `:422` 之间。 |
  | `Source/UELoader/UEHttpServer.cs` | `switch (path)` 里新增一个 `case "mods/loaded_assemblies":`，插在 `:408` 与 `:410` 之间。 |
  | `Assemblies/UELoader.dll` / `Assemblies/UELoader.pdb` | `dotnet build` 的产物（由 `UELoader.csproj:92-96` 的 `DeployToAssemblies` 自动拷贝）。**这两个文件不进版本控制的内容判断由维护者定；执行者不要手动编辑它们。** |

  **本任务刻意不做**：`MCP/` 一行不改（所以此任务结束时 MCP 侧还调不到这个端点，任务 4 才接）。

### 任务 4：MCP 工具 `get_loaded_assemblies` + 菜单登记 + 默认暴露

**取名理由（别改成 `rimworld.*`）：** `MCP/index.js:3740` 的 `TOOL_NAME_PREFIXES = ['rigworld.', 'rimworld.', 'rimbridge.']` 是 **GABP 镜像专用**的前缀剥离表，`MCP/index.js:3757` 靠 `isGabpMirrorName(name) && gabpToolNames().has(name)` 判断镜像路由。本项目自研的原生 UE 工具一律用裸名（`get_map_structure` / `post_map_marker`）。取 `get_loaded_assemblies`。

**插桩位置的三条硬约束（都在 `MCP/test/toolchainFixes.test.js` 里被切片断言守住）：**
- 不在 `function toolListLine(`（`MCP/index.js:3281`）与 `function buildMenuNode(`（`:3290`）之间插任何东西 → `:121` 断言。
- 不在 `case 'start_game': {`（`:4371`）与 `case 'get_game_status': {`（`:4384`）之间插任何 case → `:263-268` 断言。
- 本任务的 `case` 插在 `:4985`（`get_map_structure` 收尾）之后、`post_map_marker`（`:4990`）之前，天然远离。

**关于 `attachUsageOnFailure`：** `MCP/index.js:4006-4019` 会对 `success:false` 的响应追加 usage/建议文本。本任务的失败分支返回的正是 `success:false` JSON，**因此会触发**——这是**预期行为，不必规避**：它额外给出的相似工具里可能就有 `rimworld.list_mods`。但为了不依赖这个模糊匹配兜底，失败分支的 `guidance` 字段里已经**确定性地**写了 `rimworld.list_mods` 与「不要用 `Mods\` 目录 grep」。

**文件：**
- 修改：`MCP/index.js`（`:2490` 之后 / `:2912` 之后 / `:2926` 之后 / `:2938` 之前 / `:3148` 之后 / `:3812` 之后 / `:4985` 之后）
- 修改：`MCP/toolConfig.json`
- 修改：`MCP/test/modPresenceGuidance.test.js`（追加断言）

- [ ] **步骤 1：追加失败的测试**

  在 `MCP/test/modPresenceGuidance.test.js` 末尾追加（`mod` 已在文件顶部按守卫导入）：

  ```javascript
  // ===========================================================================
  // 程序集级工具 get_loaded_assemblies：注册 / 菜单 / 暴露面 / 失败语义
  // ===========================================================================

  const parse = async (value) => JSON.parse(
    await Promise.resolve(value).then((r) =>
      typeof r === 'string' ? r : (r.content || []).map((c) => c.text || '').join('')));

  test('[12] get_loaded_assemblies：工具已注册、归 game_control/game/mods 叶子、默认暴露', async () => {
    assert.ok(mod.allToolNames().has('get_loaded_assemblies'), '工具必须在可调用集里');
    assert.ok(mod.menuUniverseNames().includes('get_loaded_assemblies'), '工具必须在菜单宇宙里（否则 mcp_help 看不到）');

    const leaf = await parse(mod.mcpHelpResult({ path: 'game_control/game/mods' }));
    const lines = (leaf.tools || []).map((l) => l.replace(/^-\s*/, '').replace(/^\[[^\]]*\]\s*/, '').split(/\s+/)[0]);
    assert.ok(lines.includes('get_loaded_assemblies'),
      '必须出现在 mcp_help{path:"game_control/game/mods"} 的叶子列表里；实际=' + lines.join(','));

    // 注意：allToolEntries() 没有导出（见 MCP/index.js:5416-5443 的 export 块），
    // 工具元信息走 mcpHelpResult 的 tool 分支取（MCP/index.js:3658-3670，:3665 回 inputSchema）。
    const detail = await parse(mod.mcpHelpResult({ tool: 'get_loaded_assemblies' }));
    assert.equal(detail.name, 'get_loaded_assemblies');
    assert.ok(detail.inputSchema.properties.query, '必须有 query 入参');
    assert.ok(detail.inputSchema.properties.include_non_mod, '必须有 include_non_mod 入参');
    assert.equal(detail.precondition && /主菜单/.test(detail.precondition), true,
      'precondition 必须写明「主菜单即可」（它决定调用方会不会先去进图）');
  });

  test('[12] get_loaded_assemblies：不在 toolListLine→buildMenuNode 切片里新增函数（护栏）', () => {
    // 与 MCP/test/toolchainFixes.test.js:121 同一条护栏，防止将来有人把函数插进这个区间
    const slice = source.slice(source.indexOf('function toolListLine('), source.indexOf('function buildMenuNode('));
    assert.ok(!slice.includes('get_loaded_assemblies'), '不要往 toolListLine→buildMenuNode 之间塞新代码');
  });

  test('[12] get_loaded_assemblies：调用形态被正确传参，且失败时明说「不可得 ≠ 不存在」', async () => {
    // 游戏未运行 → callUnityExplorerAPI 必然连不上，这里断言 MCP 侧的失败转写语义。
    // 不依赖真游戏：UESD_MCP_NO_START=1 下没有 UE 服务，走的就是连接失败分支。
    const raw = await mod.handleToolCall('get_loaded_assemblies', { query: 'ancot' });
    const body = JSON.parse((raw.content || []).map((c) => c.text || '').join(''));
    const payload = body.data && typeof body.data === 'object' && body.data.answerAvailable !== undefined
      ? body.data
      : body;
    assert.equal(payload.success, false, '游戏未运行时必须 success:false（不能假装成功）');
    assert.equal(payload.answerAvailable, false, '必须显式声明「拿不到答案」');
    assert.match(payload.guidance, /rimworld\.list_mods/, '失败时必须指向 rimworld.list_mods');
    assert.match(payload.guidance, /grep/, '失败时必须明说「不要用目录 grep 代替」');
    assert.ok(!payload.assemblies, '失败时不得返回 assemblies 字段（空的也要避免被读成「没有」）');
  });

  test('[12] get_loaded_assembles 的 usage notes 里写死了与 rimworld.list_mods 的分工', async () => {
    const block = (() => {
      const i = source.indexOf('get_loaded_assemblies: {');
      assert.ok(i > 0, 'TOOL_USAGE_NOTES 里必须有 get_loaded_assembles');
      return source.slice(i, i + 1200);
    })();
    assert.match(block, /rimworld\.list_mods/, 'notes 必须指向 rimworld.list_mods');
    assert.match(block, /AppDomain|进程内/, 'notes 必须说明口径是进程内');
  });

  test('[12] toolConfig.json：新工具默认暴露，且 _comment 的计数与实际一致', () => {
    const cfg = JSON.parse(fs.readFileSync(path.join(modRoot, 'MCP', 'toolConfig.json'), 'utf-8'));
    assert.equal(cfg.tools.get_loaded_assemblies, true, '必须默认暴露（可发现性是本条目的另一半）');
    const trueCount = Object.keys(cfg.tools).filter((k) => cfg.tools[k] === true).length;
    assert.match(cfg._comment, new RegExp('共 ' + trueCount + ' 个'),
      '_comment 里的暴露数与实际 true 数量必须一致；实际=' + trueCount);
  });
  ```

- [ ] **步骤 2：运行测试验证失败**

  运行：`Set-Location MCP; node --test test/modPresenceGuidance.test.js`
  预期：新增的 5 条全 FAIL，首条报 `'工具必须在可调用集里'`。

- [ ] **步骤 3：注册工具定义 `MCP/index.js`（`:2490` 之后）**

  在 `TOOLS.push({ name: 'post_map_marker', … });` 的收尾（`:2490` 的 `});`）之后、`// ========== GABP（RimBridgeServer）桥接集成` 注释（`:2492`）之前插入：

  ```javascript
  // 模组程序集级在场判定（吐槽板 [12]）：读**游戏进程内**的 AppDomain，不是磁盘目录。
  // 与 rimworld.list_mods 的分工：list_mods 回答「这个 mod 装没装/启没启用/来源目录」，
  // 本工具回答「哪些 dll 真的在进程里、被哪个 packageId 拥有」。不要合并、不要互相替代。
  TOOLS.push({
    name: 'get_loaded_assemblies',
    description: '[UE工具][主菜单即可] 列出**游戏进程内**（AppDomain）实际已加载的程序集，并标注每个程序集归哪个 mod。'
      + '判「某个程序集/类型是不是来自某个 mod」用本工具；判「某个 mod 装没装、启没启用、来源目录」用 rimworld.list_mods。'
      + '⚠️ 口径警告：本工具读的是进程内状态，游戏未运行时**无法回答**（返回 success:false + answerAvailable:false），'
      + '这**不等于**「该 mod 不存在」。'
      + '⚠️ 不要用 <游戏根>\\Mods 目录名 grep 判模组存在性：Steam 创意工坊模组不在该目录下（实测多数模组程序集在 '
      + 'steamapps\\workshop\\content\\294100\\），grep 零命中是必然假阴性。'
      + '返回值里的 assemblies 可能为空数组——此时必须同时看 appDomainAssemblyCount：'
      + '那是 AppDomain 里枚举到的程序集总数，用来区分「没匹配上」与「枚举到 0 个」。',
    inputSchema: {
      type: 'object',
      properties: {
        query: {
          type: 'string',
          description: '大小写不敏感的子串过滤，命中 程序集名 / 磁盘路径 / packageId 任一即返回；省略=不过滤。'
            + '例：query="ancot"、query="2988801276"（工坊目录名）、query="ancot.ancotlibrary"'
        },
        include_non_mod: {
          type: 'boolean',
          description: '是否连 vanilla / 引擎程序集（Assembly-CSharp、UnityEngine.* 等不属于任何 mod 的）一起返回；默认 false。'
            + '默认 false 时它们被计入 hiddenNonModCount 但不出现在 assemblies 里。'
        }
      }
    }
    // 注：与 post_map_marker 同款 —— 实测服务端不校验未声明的多余参数，声明 additionalProperties:false 会言不符
  });
  ```

- [ ] **步骤 4：接四处注册表 `MCP/index.js`**

  四处都只加一行，紧跟各自的既有同类条目（形状照抄 `post_map_marker`）：

  ```javascript
  // ① TOOL_CATEGORY —— 紧跟 :2912 的 post_map_marker 行之后
    get_loaded_assemblies: 'game_control',

  // ② TOOL_PRECONDITION —— 紧跟 :2926 的 post_map_marker 行之后
    get_loaded_assemblies: '游戏已运行且 UE HTTP 服务已启动（**主菜单即可，不需要进图**）；游戏未运行时返回 answerAvailable:false，不给出任何存在性判断',

  // ③ TOOL_USAGE_NOTES —— 插在 :2937（execute_csharp_code 那行，**属于计划 02，不要动**）之后、:2938（get_map_things）之前
    get_loaded_assemblies: {
      example: '{ "query": "ancot" }',
      notes: '读的是**进程内** AppDomain（+ LoadedModManager 做归属），不是磁盘目录。与 rimworld.list_mods 分工：'
        + 'list_mods 判「mod 装没装/启没启用/rootDir/source」，本工具判「哪些 dll 真在进程里/归哪个 packageId」。'
        + 'assemblies 为空**不等于**没装——必须同时看 appDomainAssemblyCount（AppDomain 枚举总数）。'
        + 'locationUnavailable=true 的行是动态程序集（读不到磁盘路径），已保留条目。'
        + 'unattributedModAssemblyCount>0 表示加载器与 AppDomain 两侧口径不一致，warnings 会说明。'
        + '⚠️ 绝不要用 <游戏根>\\Mods 目录名 grep 代替本工具：工坊模组不在该目录下，grep 零命中是必然假阴性。'
    },

  // ④ TOOL_SUBGROUP —— 紧跟 :3148 的 'rimworld.list_mods' 行之后
    'get_loaded_assemblies': 'game_control/game/mods',

  // ⑤ TOOL_ARG_ALIASES —— 紧跟 :3812 的 post_map_marker 块之后
    get_loaded_assemblies: { filter: 'query', q: 'query', name: 'query', assembly: 'query', includeNonMod: 'include_non_mod', all: 'include_non_mod' },
  ```

  > ② 的措辞里带了 `**` 是刻意的：`mcpHelpResult` 与 `agg_list_tools` 直接把 `TOOL_PRECONDITION` 的原文渲染给调用方（与 `TOOL_USAGE_NOTES` 同机制），加粗标记能提高 AI 的注意率。

- [ ] **步骤 5：实现 case `MCP/index.js`（`:4985` 之后）**

  在 `case 'get_map_structure': { … }` 的收尾（`:4985`）与 `// ========== 地图坐标光标` 注释（`:4987`）之间插入：

  ```javascript
    // ========== 模组程序集级在场判定（UE 实现，不依赖 RIMAPI/GABP；主菜单即可用，不 requireStage） ==========
    // 端点：GET /mods/loaded_assemblies?query=&includeNonMod=
    // 刻意不加 requireStage([STAGES.IN_GAME])：AppDomain 与 LoadedModManager 在主菜单即可读，
    // 而主菜单恰恰是最常问「什么已加载」的时机（对照 :4422 / :4452 等需进图的 UE 工具）。
    case 'get_loaded_assemblies': {
      const query = args?.query != null ? String(args.query) : '';
      const includeNonMod = args?.include_non_mod === true;
      const qs = new URLSearchParams();
      if (query) qs.append('query', query);
      if (includeNonMod) qs.append('includeNonMod', '1');
      const suffix = qs.toString() ? `?${qs.toString()}` : '';
      const result = await callUnityExplorerAPI(`/mods/loaded_assemblies${suffix}`, 'GET');

      if (!result.success) {
        // 反假阴性契约：拿不到答案就说「拿不到」，绝不让调用方把失败读成「不存在」。
        // attachUsageOnFailure（:4006-4019）会给本响应追加 usage/相似工具，属于预期行为，不规避。
        return { content: [{ type: 'text', text: JSON.stringify({
          success: false,
          answerAvailable: false,
          error: result.error || '游戏未运行，或游戏内 UE HTTP 服务不可达（模组未加载 / 端点未部署 / token 失效）',
          errorCode: result.errorCode || 'GAME_NOT_RUNNING',
          guidance: '本工具读的是**游戏进程内**的 AppDomain，游戏未运行时无法回答。'
            + '这**不等于**该模组不存在——不要据此下「没装」的结论。'
            + '判定「装没装/启没启用/来源目录」：启动游戏后调用 rimworld.list_mods（看 packageId + rootDir + source + enabled + loadedInSession）。'
            + '判定「哪些 dll 真在进程里」：启动游戏后（主菜单即可）再调用本工具一次。'
            + '⚠️ 无论本工具成功与否，都不要用 <游戏根>\\Mods 目录名 grep 代替：'
            + 'Steam 创意工坊模组不在该目录下，grep 零命中是必然假阴性。',
          hintIfEndpointMissing: '若游戏确实在运行却报此错，多半是新端点尚未部署：确认 Assemblies/UELoader.dll 已是重编译版本（见计划任务 3 步骤 3）。'
        }, null, 2) }] };
      }
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  ```

  > 成功分支直接透传 `result`（形状与 `get_map_structure` 的 `:4984` 一致）：它自带 `success:true` 与 `data:{scope, appDomainAssemblyCount, assemblies, warnings, …}`，其中 `assemblies: []` 永远伴随 `appDomainAssemblyCount`，这就是反假阴性契约在协议层的落点。

- [ ] **步骤 6：打开暴露面 `MCP/toolConfig.json`**

  在 `tools` 对象里 `"get_game_info":true` 之后加一项（保持字母序附近的既有风格即可）：

  ```json
  "get_loaded_assemblies":true
  ```

  同时把 `_comment` 里的 `共 24 个（+ post_map_marker：地图坐标光标，UE 实现/自研 shader）` 改成
  `共 25 个（+ post_map_marker：地图坐标光标，UE 实现/自研 shader；+ get_loaded_assemblies：模组程序集级在场判定，UE 实现/主菜单即可）`。

  > 算术依据：`toolConfig.json` 当前 `true` 的键实测恰为 **24** 个（含 `post_map_marker`）；加本工具 → **25**。测试步骤 1 的最后一条会拿实际 `true` 数量去正则匹配 `_comment`，所以改错数字必 FAIL。

- [ ] **步骤 7：运行测试验证通过**

  运行：`Set-Location MCP; node --test test/modPresenceGuidance.test.js`
  预期：8 个用例全 PASS（任务 1 的 3 条 + 本任务的 5 条）。

  运行回归：`Set-Location MCP; npm test`
  预期：全绿。重点看两个文件：
  - `MCP/test/toolCountConsistency.test.js` —— 新增工具后 `total` / `menuVisible` / 各子组求和必须仍自洽（它无硬编码数，但 `menuUniverseNames()` 与 `TOOL_SUBGROUP`/`toolConfig` 三者任一漏登都会让它红）。
  - `MCP/test/toolchainFixes.test.js` —— `:121` 与 `:263-268` 两条切片断言必须仍绿（若红，说明有人把新代码插进了被切区间）。

- [ ] **步骤 8：游戏内端到端验证（人工，与任务 3 步骤 5 配对）**

  重启 MCP 服务器（`toolConfig.json` 改动需重启才生效，见其 `_comment` 与 `AI-DEPLOYMENT.md:177`），游戏进主菜单后：

  ```
  agg_call_tool{ tool:"get_loaded_assemblies", args:{ query:"ancot" } }
  ```
  预期：`success:true` 的 JSON 里 `data.assemblies` 恰 1 条、`packageId="ancot.ancotlibrary"`、`location` 以 `…\workshop\content\294100\2988801276\` 开头，且 `data.appDomainAssemblyCount > 0`。

  然后 `mcp_help{ path:"game_control/game/mods" }` —— 叶子列表里必须同时出现 `rimworld.list_mods` 与 `get_loaded_assemblies`。这一步是「可发现性」的最终验收：**一次下钻就能同时看到两个工具**。

  最后停掉游戏再调一次，必须拿到 `answerAvailable:false` 的响应（而不是 `assemblies: []`）。

- [ ] **步骤 9：改动清单**

  | 文件 | 改了什么 |
  |---|---|
  | `MCP/index.js` | ① `:2490` 后新增 `TOOLS.push({name:'get_loaded_assemblies',…})`；② `:2912` 后 `TOOL_CATEGORY` 加一行；③ `:2926` 后 `TOOL_PRECONDITION` 加一行；④ `:2938` 前 `TOOL_USAGE_NOTES` 加一条（**`:2937` 本行未动**）；⑤ `:3148` 后 `TOOL_SUBGROUP` 加一行；⑥ `:3812` 后 `TOOL_ARG_ALIASES` 加一行；⑦ `:4985` 后 `handleToolCall` 新增 `case 'get_loaded_assemblies'`。 |
  | `MCP/toolConfig.json` | `tools` 加 `"get_loaded_assemblies":true`；`_comment` 暴露数 24 → 25 并补工具名。 |
  | `MCP/test/modPresenceGuidance.test.js` | 追加 5 个用例（可调用集 / 菜单宇宙 / 叶子可见 / 切片护栏 / 失败语义 / notes 分工 / 计数一致）。 |

### 任务 5：可发现性落到文档（`README.md` + `AI-DEPLOYMENT.md`）

**为什么工具已经登记进菜单了还要改文档：** 菜单下钻解决的是「知道要找的人」，但吐槽板的机制是「**根本不知道要找**」→ 直接去 grep 磁盘目录。`AI-DEPLOYMENT.md` 的排障决策树（`:166-177`）是 AI 出错时的**第一查表**，它现在没有这一行，所以 AI 不会想到去查工具。

**关于 `README.md` 的工具计数：** `README.md:60` 写「默认压缩仅直接暴露 **23** 个」、`:183` 也写 **23** —— 实测 `toolConfig.json` 里 `true` 的是 **24** 个（`post_map_marker` 加进来时没同步这两处）。本任务把它改成 **25**，顺带把上一轮漏掉的 1 个补上（24 + 本工具 = 25，一步到位）。**但 `README.md:60` 的「总数 185」是 GABP 连接态的实测值，离线无法重算，本任务不动它** —— 改一个能算的、留一个不能算的，比两个都瞎改强。

**文件：**
- 修改：`README.md:60`、`README.md:158`、`README.md:167`、`README.md:183`
- 修改：`AI-DEPLOYMENT.md:177` 之后
- 修改：`MCP/test/modPresenceGuidance.test.js`（追加断言）

- [ ] **步骤 1：追加失败的测试**

  在 `MCP/test/modPresenceGuidance.test.js` 末尾追加：

  ```javascript
  // ===========================================================================
  // 可发现性：文档里必须写死「判模组在场用工具、不要用目录 grep」
  // ===========================================================================

  const readRootDoc = (name) => fs.readFileSync(path.join(modRoot, name), 'utf-8');

  test('[12] README.md：写死「不要用 Mods 目录 grep 判存在性」并指向两个工具', () => {
    const md = readRootDoc('README.md');
    assert.match(md, /不要.{0,12}grep|禁止.{0,12}grep/,
      'README 必须出现「不要/禁止 grep 目录」的明确禁令');
    assert.match(md, /rimworld\.list_mods/, '必须点名 rimworld.list_mods');
    assert.match(md, /get_loaded_assemblies/, '必须点名 get_loaded_assemblies');
    assert.match(md, /workshop[\\/]content[\\/]294100/,
      '必须写明工坊目录的真实形状，否则禁令没有说服力');
  });

  test('[12] README.md：默认暴露工具数与 toolConfig.json 的实际 true 数量一致', () => {
    const md = readRootDoc('README.md');
    const cfg = JSON.parse(readRootDoc(path.join('MCP', 'toolConfig.json')));
    const trueCount = Object.keys(cfg.tools).filter((k) => cfg.tools[k] === true).length;
    // 两处计数（:60 与 :183）必须同时对上，避免只改一处
    const claims = md.match(/仅直接暴露\s*(\d+)\s*个|仅暴露\s*(\d+)\s*个高频工具/g) || [];
    assert.ok(claims.length >= 2, `应能找到两处「默认暴露 N 个」的表述，实际找到 ${claims.length} 处`);
    for (const c of claims) {
      const n = Number((c.match(/(\d+)/) || [])[1]);
      assert.equal(n, trueCount, `README 写的「${c}」与 toolConfig 实际 ${trueCount} 不一致`);
    }
  });

  test('[12] AI-DEPLOYMENT.md：排障决策树里必须有「grep 零命中 ≠ 没装」这一行', () => {
    const md = readRootDoc('AI-DEPLOYMENT.md');
    assert.match(md, /grep/, '排障决策树必须提到 grep');
    assert.match(md, /rimworld\.list_mods/, '排障决策树必须指向 rimworld.list_mods');
    assert.match(md, /get_loaded_assemblies/, '排障决策树必须指向 get_loaded_assemblies');
    assert.match(md, /工坊|workshop/i, '必须点明工坊模组不在 <Game>\\Mods 下');
  });
  ```

- [ ] **步骤 2：运行测试验证失败**

  运行：`Set-Location MCP; node --test test/modPresenceGuidance.test.js`
  预期：新增的 3 条 FAIL；其中「默认暴露工具数」那条会先报 `23 !== 25`。

- [ ] **步骤 3：`README.md` 新增判断小节（`:158` 之前）**

  在 `**RimBridgeServer（GABP）镜像工具（121 个，依赖 RimBridgeServer）**`（`:158`）之前插入：

  ```markdown
  **⚠️ 判断「某个模组在不在场」—— 不要用目录 grep**

  > 这是本项目最容易被搞错的一类判断：**按 `<游戏根>\Mods\` 目录名 grep 判「模组装没装」，必然假阴性。**
  > Steam 创意工坊模组根本不在该目录下，它们在 `steamapps\workshop\content\294100\<workshopId>\`，
  > 官方模组在 `RimWorld\Data\<ModName>\`。实测一次模组程序集统计里，**34 个模组程序集有 20 个在工坊目录**
  > （只扫 `Mods\*` 会漏掉 59%）。本项目自己就因此错过一次：把已加载的 `AncotLibrary` 判成「结构性不可能」。
  > `Player.log` **也不是**判「装没装」的正确来源 —— 它不记录已加载程序集名。

  | 你要回答的问题 | 用哪个工具 | 关键字段 |
  |---|---|---|
  | 这个 mod 装没装、启没启用、在磁盘哪个目录 | `rimworld.list_mods` | `packageId` + `rootDir` + `source`（`ModsFolder` / `SteamWorkshop` / `OfficialModsFolder`）+ `enabled` + `loadedInSession` |
  | 某个 dll / 某个类型是不是真的**在游戏进程里**、归哪个 mod | `get_loaded_assemblies`（本模组 UE 端点，**主菜单即可**，不依赖 GABP） | `assemblies[].packageId` + `assemblies[].location` + `appDomainAssemblyCount` |
  | 这个能力为什么不好使（需要哪个 mod 才能用） | `get_game_status` 的 `capabilities` | `loaded` / `usable` / `note` |

  两条铁律：
  1. **`get_loaded_assemblies` 返回的 `assemblies` 是空数组时，不等于「没装」** —— 必须同时看 `appDomainAssemblyCount`（AppDomain 枚举总数）。零命中与「枚举到 0 个」是两件事。
  2. **`get_loaded_assemblies` 在游戏未运行时返回 `answerAvailable:false`，同样不等于「没装」** —— 它读的是进程内状态，游戏没跑就没有答案。启动游戏后（主菜单即可）再问一次。

  ```

- [ ] **步骤 4：`README.md` 补 `rimworld.list_mods` 字段说明（`:167` 之后）**

  把 `| rimworld.list_mods | Mod 列表 |` 这一行替换为：

  ```markdown
  | `rimworld.list_mods` | Mod 列表。**判「某 mod 在不在场」用这个**（`packageId` + `rootDir` + `source` + `enabled` + `loadedInSession`）；要看程序集级（「某 dll 是否真在进程里」）用 `get_loaded_assemblies`。入参 `includeInactive`（默认 `true`） |
  ```

- [ ] **步骤 5：`README.md` 改两处计数（`:60` 与 `:183`）**

  - `:60` 的 `默认压缩仅直接暴露 23 个` → `默认压缩仅直接暴露 25 个`
  - `:183` 的 `默认压缩预设仅暴露 23 个高频工具` → `默认压缩预设仅暴露 25 个高频工具`

  > `:60` 同一行里的 `实测工具**总数 185**（system 7 / unityexplorer 11 / mono 20 / meta 3 / bridge 16 / game_control 128）` **保持原样** —— 那是 GABP 连接态的实测值，离线无法重算，本任务不碰。

- [ ] **步骤 6：`AI-DEPLOYMENT.md` 排障决策树加两行（`:177` 之后）**

  在 `| 改 toolConfig.json 不生效 | 需重启 | 重启 MCP 或重连会话 |`（`:177`）之后插入两行：

  ```markdown
  | grep `<游戏根>\Mods` 零命中 ⇒ 「这 mod 没装」 | **必然假阴性** | 工坊模组在 `steamapps\workshop\content\294100\<id>\`，官方模组在 `RimWorld\Data\<ModName>\`，都不在 `Mods\` 下。改用 `rimworld.list_mods`（看 `packageId`+`rootDir`+`source`）|
  | `get_loaded_assemblies` 报 `answerAvailable:false` | 游戏未运行 / 端点未部署 | 它读的是**进程内** AppDomain，游戏没跑就没有答案（**不等于「没装」**）。启动游戏到主菜单即可用；若游戏在跑仍报错，检查 `Assemblies/UELoader.dll` 是否为重编译版本 |
  ```

- [ ] **步骤 7：运行测试验证通过**

  运行：`Set-Location MCP; node --test test/modPresenceGuidance.test.js`
  预期：11 个用例全 PASS。

  运行回归：`Set-Location MCP; npm test`
  预期：全绿。

- [ ] **步骤 8：改动清单**

  | 文件 | 改了什么 |
  |---|---|
  | `README.md:158` 前 | 新增「⚠️ 判断「某个模组在不在场」—— 不要用目录 grep」小节（三工具分工表 + 两条铁律）。 |
  | `README.md:167` | `rimworld.list_mods` 行补字段清单与工具分工。 |
  | `README.md:60` / `README.md:183` | 「默认暴露 N 个」23 → 25（`:60` 的 GABP 总数 185 不动）。 |
  | `AI-DEPLOYMENT.md:177` 后 | 排障决策树加 2 行。 |
  | `MCP/test/modPresenceGuidance.test.js` | 追加 3 个文档守护用例。 |

---

## 3. 全部改动的总清单

| 文件 | 性质 | 所属任务 | 需要重编译 |
|---|---|---|---|
| `MCP/index.js` | 改（7 处） | 1 / 4 | 否 |
| `MCP/toolConfig.json` | 改 | 4 | 否（但需重启 MCP 进程） |
| `MCP/test/modPresenceGuidance.test.js` | 新建 | 1 / 4 / 5 | 否 |
| `Source/UELoader/UEModAssemblyIndex.cs` | 新建 | 2 | **是** |
| `Source/ModAssemblyIndexTests/ModAssemblyIndexTests.csproj` | 新建 | 2 | 否 |
| `Source/ModAssemblyIndexTests/Program.cs` | 新建 | 2 | 否 |
| `Source/UELoader/UEHttpHandler.cs` | 改 | 3 | **是** |
| `Source/UELoader/UEHttpServer.cs` | 改 | 3 | **是** |
| `Assemblies/UELoader.dll` / `.pdb` | 构建产物 | 3（自动） | 由 `UELoader.csproj:92-96` 生成 |
| `README.md` | 改 | 5 | 否 |
| `AI-DEPLOYMENT.md` | 改 | 5 | 否 |

---

## 4. 自检（writing-plans §自检，三项）

**① 规格覆盖度** —— 逐条对回 §0 与裁决报告：

| 规格里的要求 | 落在哪个任务 |
|---|---|
| 「不要重复造 `list_mods`」 | §0.0 明写；任务 4 的 `TOOL_USAGE_NOTES` 把两者分工钉死；`README.md` 分工表（任务 5 步骤 3） |
| 原建议 1：返回 `packageId` + `Assembly.Location` + 「是否真在 AppDomain 里」 | 任务 2（`packageId` / `location` / `ownedByMod` 三列）+ 任务 3（端点）+ 任务 4（工具面） |
| 原建议 2：补 `modRoots` | **明确不做**，理由写在 §0.1 表格第 2 行与 §0.8 |
| 原建议 3：`execute_csharp_code` 文案 | 任务 1（`:2072` / `:2078`）+ §0.5 交接文案给 `:2937` |
| 程序集级可见性「读 AppDomain 还是读已加载程序集列表」 | §0.2 的来源表 + 任务 3 步骤 1 的三段式取数 |
| 游戏未运行时的表现 | §0.6 契约表 + 任务 4 步骤 5 的失败分支 + 任务 4 步骤 8 的第三次验证 |
| 可发现性 | 任务 4（菜单 + 暴露面）+ 任务 5（README + AI-DEPLOYMENT） |
| 裁决报告 §6.3「不要硬编码模组根目录」 | 任务 3 步骤 1 的 `SafeModRoot` 反射探测失败即返回 `null` |
| 裁决报告 §3.5 的 30s 超时观察 | 属条目 [07]，本计划不展开（与 §0.5 表格一致） |

**② 占位符扫描** —— 全文无「待定 / TODO / 后续实现 / 补充细节 / 类似任务 N / 为上述代码编写测试 / 添加适当的错误处理」。每个代码步骤都带可粘贴的完整代码块；任务 1、2、4、5 的测试步骤都带完整断言代码；任务 3 步骤 5 是**游戏内人工验收矩阵**（7 行，每行写明前置、请求、逐条对照的预期），不是占位。任务 1 步骤 1 的测试里另有一条断言显式禁止占位符进入生产文案。

**③ 类型一致性** —— 跨任务逐个对过：
- `ModAssemblyEntry` / `ModOwnerEntry` / `ModAssemblyRow` / `ModAssemblyIndexResult` 四个类型在任务 2 步骤 4 定义，任务 2 步骤 2 的测试与任务 3 步骤 1 的 handler 用的是同一套字段名（`AssemblyName` / `Version` / `Location` / `LocationUnavailable`；`PackageId` / `ModName` / `RootDir` / `Assemblies`；`OwnedByMod` / `PackageId` / `ModName` / `RootDir`）——无一处拼写漂移。
- `ModAssemblyIndex.Build(appDomain, mods, query, includeNonMod)` 的签名在定义处、测试调用处（`SampleAppDomain(), SampleMods(), "", false`）、handler 调用处（`ModAssemblyIndex.Build(appDomainEntries, owners, query, includeNonMod)`）三处完全一致。
- `ModAssemblyIndexResult` 的五个计数（`AppDomainCount` / `ModAssemblyCount` / `UnattributedModAssemblyCount` / `HiddenNonModCount` / `LocationUnknownCount`）在任务 3 的 `Success(...)` 字典里逐一映射为 `appDomainAssemblyCount` / `modAssemblyCount` / `unattributedModAssemblyCount` / `hiddenNonModCount` / `locationUnknownCount`，命名一一对应，无遗漏。
- 工具名 `get_loaded_assemblies` 在 `TOOLS` / `TOOL_CATEGORY` / `TOOL_PRECONDITION` / `TOOL_USAGE_NOTES` / `TOOL_SUBGROUP` / `TOOL_ARG_ALIASES` / `handleToolCall` 的 case / `toolConfig.json` / 测试 / 两份文档中共 11 处，**全部逐字一致**（全小写下划线，无连字符变体）。

---

## 5. 与上游裁决报告的逐条对账（含本计划复核后修正的错误）

| 裁决报告的说法 | 本计划复核结果 |
|---|---|
| §3.1 `MCP/index.js:2494-2500`、`3147-3148`、`aggregator/config.json:6-8` | ✅ 行号逐条复核正确 |
| §3.3 `docs/csharp-console-compiler-upgrade-feasibility.md:307/310/318-319/321-323` | ✅ 逐条复核正确（`:307` 标题、`:310` 统计、`:318-319` 59% 漏扫、`:321-323` 字段非属性的坑） |
| §3.4 `MCP/index.js:2071-2082`、`:2937` | ✅ 正确。补一条报告没说的：当前编译路径是 **mcs**（`Source/UE-4.9.0-official/…/CSConsole/ScriptEvaluator.cs:2`/`:22`/`:72`/`:73`），不是 Roslyn —— 这解释了为什么 `return` 的行为依赖求值器实现，也让 §0.5 的矛盾有了确切的技术坐标 |
| §6.2 「新增 UE HTTP 端点：`Source/UELoader/UEHttpServer.cs:382-440` 的 `switch (path)`」 | ⚠️ **行号范围偏大**：实际 `switch (path)` 起于 **`:382`**，而 `UEHttpServer.cs` 全文 799 行，`switch` 体在 `:530` 之前就结束（`:526` 是最后一个 `case`）。`:382-440` 只覆盖到 `unityexplorer/hook/create`，不含 `debugaction/*`。本计划改用精确锚点 `:408`（`rimbridge/status` case 收尾）与 `:410`（`area/delete`）。**结论不变，行号必须改。** |
| §6.2 「按 `:2487-2489` 的既有说明不要声明 `additionalProperties:false`」 | ✅ **正确**（复核后撤回本计划初稿的「行号错」判断）。`MCP/index.js:2487-2488` 正是那两行注释、`:2489` 是收尾的 `}`，`:2487-2489` 正好框住它。本计划在 `TOOLS.push` 里照抄了同一段注释，并按同一理由不声明该属性。 |
| §6.2 「在 `MCP/index.js:3147-3148` 的目录表登记到 `game_control/game/mods`」 | ✅ 正确，且本计划补了一条报告没说的：该路径是**叶子**（`MCP/index.js:3261-3263` 的 `sub !== 'gameplay'` 分支直接返回 `{type:'tools'}`），所以登记后无需任何菜单结构改动即可下钻看到 |
| §6.2 「在 `MCP/toolConfig.json` 设为 `true`」 | ✅ 正确。报告未提的是：还**必须**同时登记进 `TOOL_SUBGROUP`，否则 `MCP/index.js:3369-3376` 的 `menuUniverseNames()` 与 `:3392-3399` 的 `menuCountBySubgroup()` 会和 `MCP/test/toolCountConsistency.test.js` 的自洽断言打架。本计划两处都登记。 |
| §7 「`docs/issue-triage/plans/` 当前为空」 | ❌ **已过时**：该目录现有 **12 份**计划。本计划逐份核对，均未覆盖 `rimworld.list_mods` 的暴露面、程序集级端点、或「不要用目录 grep」这句禁令 —— 「未被覆盖」的结论成立，但依据要换成这 12 个文件名（其中 `2026-10-01-02-…` 与本计划有**直接的行级冲突**，见 §0.5）。 |
| §5「补程序集级工具」归属「本项目的锅（可做）」 | ✅ 成立，但成本被低估：它需要改 `.cs` 并重编译 `Assemblies/UELoader.dll`（且必须先停游戏），与本批其它 11 份计划的「零 C# 改动」性质不同。本计划已把这条成本写进头部技术栈与任务 3 步骤 3。 |

---

## 6. 需要主代理裁决的两件事（本计划无权自行决定）

1. **计划 02 的验收行 `:819`（`…; return 1;`）与吐槽板 `:251-253` 的一手实测（`return expr;` → `CS0127`）矛盾。** 本计划在任务 1 里如实写成「mcs 下不要写 `return`，若已被 Roslyn 升级则 `return` 才对」的双分支文案，并在 §0.5 标出冲突，但**没有裁定谁对**（裁定需要起游戏跑一次，本流水线禁止）。建议主代理决定：先让执行者实跑一次定论，再决定计划 02 的验收矩阵要不要改。
2. **吐槽板 `## [12]` 条目目前没有 `Triage 批注（2026-10-01）`。** 板上 `[01]`–`[11]`、`[13]`–`[16]` 共 12 条都有批注（行 61/120/162/219/305/352/394/454/485/525/553/587/620/656），唯独 `[12]`（`docs/mcp-吐槽板.md:228-270`）正文里没有。**给条目加批注是主代理的活（`AGENT-BRIEF.md` §1.1 明令 PLAN 子代理不许碰吐槽板），本计划没有动它一个字节。** 建议批注内容：

   > 🤖 **Triage 批注（2026-10-01）** — 裁决：**值得修，但原条目前提被推翻、范围必须收窄**。建议 1（加工具）降级为「程序集级补充工具 + 不重造 `rimworld.list_mods`」；建议 2（`get_game_status` 补 `modRoots`）**不做**；建议 3 并入计划。
   > 报告：`docs/issue-triage/triage/12-mod-presence-and-disk-path.md`
   > 计划：`docs/issue-triage/plans/2026-10-01-12-mod-presence-assembly-visibility.md`（5 个任务；**含 `.cs` 改动，需先停游戏重编译 `Assemblies/UELoader.dll`**，其余 4 个任务纯文案/纯 MCP 侧）
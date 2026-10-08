裁决：值得修

**一句话结论**：条目标题「没有『某模组在不在场』的工具」**已被实测推翻** —— 已暴露的 `rimworld.list_mods` 就返回 `packageId + rootDir + source + enabled + loadedInSession`，Ancot 那次假阴性本可一击消解；真正剩下的缺口只有「**程序集级**（某个 dll 是否真在 AppDomain 里）」和「可发现性」，范围比原条目小得多，但依然是每次都要重踩的真痛。

---

## 3. 核实过程

### 3.1 工具确实存在、已暴露（本项目 toolConfig）

- `MCP/toolConfig.json`（单行 JSON）解析后：`"rimworld.list_mods": true` —— 它是 `game/mods` 类里**唯一默认暴露**的工具（其余 `rimworld.get_mod_settings` / `set_mod_enabled` / `reorder_mod` 等均为 `false`，靠 `agg_call_tool` 按名调）。
- `MCP/index.js:2494-2500` 注释确认它是 GABP 去重镜像：`其余 13 个镜像（list_colonists/list_mods/save_game/…）已恢复暴露，作为被删原生工具的唯一入口`。
- `MCP/index.js:3147-3148` —— 目录归类：`'rimworld.get_mod_configuration_status': 'game_control/game/mods'`、`'rimworld.list_mods': 'game_control/game/mods'`。
- `MCP/aggregator/config.json:6-8` —— 聚合器只挂本项目的 `rimworld`（`http://127.0.0.1:3000/sse`），与本条目无关。

### 3.2 只读实测：它把「磁盘路径」给得比吐槽者以为的还全

`mcp_help{path:"game_control/game/mods"}` 返回：
`"- [已暴露] rimworld.list_mods [RimBridge] Rimworld List Mods List installed RimWorld mods, whether each one is enabled in the current configuration, and whether it matches the currently loaded session"`
入参只有 `includeInactive`（boolean，默认 true），`additionalProperties:false`。

实测调用 `agg_call_tool{tool:"rimworld.list_mods", args:{includeInactive:false}}` → `success:true`，`modCount:21`。**吐槽者点名的那一条，逐字段如下**：

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

⇒ 与吐槽板 `:177` 用 `execute_csharp_code` 绕出来的 `AncotLibrary @ Location=[…\workshop\content\294100\2988801276\1.6\Assemblies\AncotLibrary.dll]` **同一个 workshopId 2988801276**。也就是说：**不用写 C#，一次只读工具调用就能证伪那次假阴性。**

顺带实测到第三种来源，`Mods\` 目录 grep 同样会漏：
`ludeon.rimworld` → `rootDir:"C:\SteamLibrary\steamapps\common\RimWorld\Data\Core"`、`source:"OfficialModsFolder"`；
`uesddebuger.debug.unityexplorer` → `rootDir:"…\RimWorld\Mods\UESDdebuger"`、`source:"ModsFolder"`。

### 3.3 缺口核实：payload 里没有程序集级信息

- `rimworld.list_mods` 的 payload 每条 mod 只有 mod 级字段（`packageId` / `folderName` / `rootDir` / `source` / `enabled` / `loadedInSession` / 依赖与排序），**没有程序集列表、没有 `Assembly.Location`**。原条目建议 1 的 `packageId + Assembly.Location + 是否真在 AppDomain 里` 只满足第一项。
- 但「程序集级在哪拿」本项目**已经踩过并写进文档**：`docs/csharp-console-compiler-upgrade-feasibility.md:307-323`
  - `:307` 标题即结论：`模组程序集来源：必须走模组加载器，不能扫目录`
  - `:310` 实测统计：`mods=21 withAssemblies=14 modAssemblies=34 | path: workshop=20 local=14 noLocation=0`
  - `:318-319`：`20/34 个模组程序集在 Steam 创意工坊目录 → 只扫 <Game>/Mods/*/Assemblies 会漏掉 59%，必须用 LoadedModManager.RunningModsListForReading → mod.assemblies.loadedAssemblies`
  - `:321-323` API 踩坑：`ModContentPack.assemblies` 是**字段**（`Verse.ModAssemblyHandler`），程序集列表是它的**字段** `loadedAssemblies`；`GetProperty("assemblies")` 返回 null。
  ⇒ 这套 API 现成可用，只是**没有 MCP 工具暴露它**。

### 3.4 建议 3（`execute_csharp_code` 是表达式模式）核实：属实

- `MCP/index.js:2071-2082` —— description 全文只有
  `'[UE工具][需要进入地图] Execute C# code in UnityExplorer C# Console…'`，`code` 的示例是 `"Log(\"Hello\");", "var x = 5; x + 3;", "using UnityEngine; Debug.Log(\"test\");"`。
- `MCP/index.js:2937` —— 唯一备注只讲 using：`控制台默认无 using 引用，裸类型名（如 GenTicks）会报 CS0103…`。
- **两处都没写「不要写 `return`」**，也没提 CS0127。吐槽板 `:179-181` 的观察与现状一致。

### 3.5 附带发现（不属本条目，但影响「工具有 ≠ 拿得到」）

同一次核实中，`agg_call_tool{tool:"rimworld.list_mods", args:{includeInactive:true}}` **第一次调用 30s 超时**：
`GABP 调用 rimworld/list_mods 失败（code=-32000）: GABP 请求 'tools/call' 超时（30000ms）`；
紧接着 `includeInactive:false` 的调用 **344ms 成功**（payload 内 `operation.DurationMs: 344`）。
⇒ 一次超时不能证明工具坏，但它说明 GABP 链路会随机卡 30s；这条与 [07] 相邻，本条目不展开。

---

## 4. 影响与严重度复评

原标注 **🟠 中** → 复评：**主诉降为 🟡，残留的程序集级部分维持 🟠**。

- **主诉（假阴性）已可被一次工具调用消解**。既然 `rimworld.list_mods` 是默认暴露工具，它就不该再按「必然假阴性」计重。真正的问题降级为**可发现性**：AI 不知道有它，于是去 grep 目录。
- **残留的真痛（🟠）**：「某个**程序集 / 类型**是不是来自某个 mod」仍然只能靠 `execute_csharp_code` 写一段 LINQ（吐槽板 `:171-176` 给的绕法）。这段代码不短、每次都要重写、且踩过 API 字段/属性坑（`feasibility.md:321-323`），符合 §5 的「每次都要重踩 / 系统性浪费往返」。
- **假成功 / 假阴性风险仍在**：grep `Mods\*` 得零命中时，调用方无法区分「真没装」与「装在 workshop / Data 目录」——这正是吐槽者被骗的那次。在没有工具兜底时，这仍是**事实相反的结论**。加上 3.5 的 30s 超时，「先试工具、失败就 grep」的退路本身也不可靠。
- 不升 🔴：主诉已有可用的现成答案，纠正只需一次调用。

---

## 5. 责任归属

**混合，按子项拆开**：

| 子项 | 归属 | 理由 |
|---|---|---|
| 「没有工具」这半句 | **纯文档/可发现性缺失（本项目的锅）** | 工具一直在（`toolConfig.json` 里 `true`）；缺的是 README / `AI-DEPLOYMENT.md` / 工具描述里没有一句「判模组在场用 list_mods」。`get_game_status` 在 `activePackageIds` 旁也没提（MCP/index.js:569 / :662 只透传字段，无 guidance）。 |
| payload 改造（加 `modRoots` 之类） | **第三方 RimBridgeServer 的锅** | `rimworld.list_mods` 是 GABP 镜像工具，schema 由 GABP 决定并经 `tools-changed` 事件灌进 `gabpTools`（`MCP/index.js:2512-2514`），本项目改不了它的返回体。 |
| 补程序集级工具 | **本项目的锅（可做）** | 需要本项目新增 UE 端点 + MCP 工具，底层 API 已实测可用。 |
| `execute_csharp_code` 缺「表达式模式」说明 | **纯文档缺失（本项目的锅，成本≈0）** | `MCP/index.js:2072` / `:2937` 补一句即可。 |

---

## 6. 修的话怎么修（方向）

按性价比排序：

1. **零成本文案（当天可做，建议无论其它做不做都做）**
   - `README.md` / `AI-DEPLOYMENT.md` 的 MCP 能力清单补一条：
     「判断某 mod 在不在场 / 在哪 → `rimworld.list_mods`，看 `packageId` + `rootDir` + `source`(`ModsFolder` / `SteamWorkshop` / `OfficialModsFolder`) + `enabled` + `loadedInSession`；**不要**用 `RimWorld\Mods` 目录名 grep 判存在性。」
   - `MCP/index.js`：`get_game_status` 的 `activePackageIds` 旁加 guidance（参考同文件 `:3516-3519` `buildCapabilityReport` 已有的 guidance 文案模式），以及 `:2937` 附近的备注表里给 `execute_csharp_code` 加「表达式模式，不要写 `return`，否则 CS0127」。

2. **程序集级（真缺口，需要写代码）**
   - 新增 UE HTTP 端点：在 `Source/UELoader/UEHttpServer.cs:382-440` 的 `switch (path)` 里加一个 case（例如 `mods/loaded_assemblies`），实现复用 `LoadedModManager.RunningModsListForReading → mod.assemblies.loadedAssemblies`（坑见 `feasibility.md:321-323`），每条返回 `{packageId, rootDir, assemblyName, location}`。
   - 新增 MCP 工具：在 `MCP/index.js` 的 `TOOLS` 数组加定义（仿 `rimworld.list_colonists` 之类），并在 `MCP/index.js:3147-3148` 的目录表登记到 `game_control/game/mods`。
   - 在 `MCP/toolConfig.json` 设为 `true`（默认暴露面本来就含 mods 类工具），并按 `:2487-2489` 的既有说明不要声明 `additionalProperties:false`。

3. **不建议做**：给 `get_game_status` 加 `modRoots` 字段（吐槽板建议 2）。它与 `rimworld.list_mods.rootDir` 重复，且那是**只读静态路径常量**，写死 `RimWorld\Mods` 与 `steamapps\workshop\content\294100\` 反而把「来源目录不止这两个」（实测还有 `Data\Core` / `Data\Royalty` 等 `OfficialModsFolder`）固化错。真要省 token，让 `get_game_status` 的 guidance 直接指向 `list_mods` 更稳。

---

## 7. 是否已被现有计划覆盖

**否，但关键事实已被现有文档固化，计划里没有把它变成工具/指引的那一步。**

- `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md:447` 已写死：
  `// ⚠️ 不要扫 <Game>/Mods：Steam 创意工坊模组不在那里（实测 20/33 个模组程序集在 workshop 目录）`
  —— 但这是 `ReferenceScanner` 的实现注释，只服务于 C# 控制台的引用集合，**不解决「AI 判断某 mod 在不在场」**。
- 同计划 `:2081` 的验收项 11 顺带涉及「工坊模组程序集」，同样是编译引用场景，不是工具面。
- `docs/csharp-console-compiler-upgrade-feasibility.md:307-323` 提供了完整 API 与踩坑记录，但同样只服务于编译引用。
- `docs/issue-triage/plans/` 当前为空，无其它子代理写过重复计划。

**结论：值得修，但请 PLAN 角色把范围压到「1（文案）+ 2（程序集级端点）」，并在计划开头明确写出「原条目主诉已被 `rimworld.list_mods` 覆盖，不要重复造 list_mods」。**
裁决：值得修

**一句话结论**：项目自带的三个「查类型」入口（`inspect_type` 只开 UE 面板、`find_types`/`find_methods` 只回 `{type, method}`）确实一个字节的成员可见性都不返回，而答案其实已经躺在手里被丢掉（`AddMethodResult` 手握 Cecil `MethodDefinition` 却只取 `Name`）；真正能答的 `decompilerserver` 在本仓库**所有文档里零提及**，调用方无从知道该去问谁 —— 现象属实、且在本项目内改起来很便宜。

---

## 3. 核实过程（实际读了什么 / 看到什么原文）

### 3.1 `inspect_type` 确实不给任何成员信息（本项目自己的实现）

- `MCP/index.js:1956-1967` —— 工具定义，描述原文：
  `'[UE工具][需要进入地图] Inspect a type in UnityExplorer, opens the Inspector panel showing fields, methods, properties of the type. Only works after entering a game map.'`
- `MCP/index.js:4421-4430` —— 转发 `POST /unityexplorer/inspect/type`，body 只有 `typeName`。
- `Source/UEHttpServer.cs:387-390` —— 路由 `case "unityexplorer/inspect/type": result = PostGuard(request, () => UEHttpHandler.InspectType(GetString(body, "typeName")));`
- `Source/UELoader/UEHttpHandler.cs:120-161` —— 实现全文要点：
  - `Type type = ResolveType(typeName); if (type == null) return Error(404, …, "TYPE_NOT_FOUND");`
  - `UEMainThreadDispatcher.ExecuteOnMainThread(() => { … InspectorManager.Inspect(type); … });`
  - 返回体只有：`Success(new Dictionary<string, object> { { "typeName", typeName }, { "message", "Type inspection opened" } })`

  ⇒ **返回体里没有任何成员/可见性字段**。它只是在游戏 UI 上开面板，面板内容 AI 读不到。
  吐槽者写的「而不是只给类型是否存在」其实还乐观了：它连类型存在与否都只用一个 404/errorCode 表达。

### 3.2 `find_types` / `find_methods` 丢弃了手头的元数据（本项目自己的实现）

- `MCP/index.js:1679-1684` —— `MONO_DEBUG_TOOL_NAMES` 含 `'find_types', 'find_methods'`。
- `McpRimDebug/DebugSession.Eval.cs:290-336` —— `FindMethods` 只做子串匹配 + 限流。
- `McpRimDebug/DebugSession.Eval.cs:355-367` —— 结果构造：
  ```csharp
  results.Add(new Dictionary<string, object>
  {
      ["type"] = typeFullName,
      ["method"] = methodName,
  });
  ```
- `McpRimDebug/DebugSession.Eval.cs:499-507` —— 调用点，`m` 是 `Mono.Cecil.MethodDefinition`（只读了 `m.Name`）：
  ```csharp
  foreach (MethodDefinition m in t.Methods)
      if (m.Name.IndexOf(methodQuery, StringComparison.OrdinalIgnoreCase) >= 0)
          AddMethodResult(results, seen, fullName, m.Name);
  ```
  ⇒ `m.Attributes` / `m.IsPublic` / `m.IsStatic` / `m.ReturnType` / `m.Parameters`（含 optional flag）**当场就有，全被丢掉**。
- `McpRimDebug/RimDebugTools.cs:86-88` —— 工具描述自己也承认：`"返回 {type, method} 列表"`。

### 3.3 两个「相反结论」我用只读查询实证了真伪（未 attach、未 load_assembly、未改任何状态）

decompilerserver 已有 context `default` = `RimWorldWin64_Data\Managed\Assembly-CSharp.dll`（`status` 实测，16158 types / 76770 methods）。以下均为只读查询：

**(a) `ThingMaker.MakeThing` —— 真相是「一个带默认参的方法」**

- `get_members_of_type{typeId:"Verse.ThingMaker", mode:"signatures"}` → **全类型只有 1 个成员**：
  `{"name":"MakeThing","kind":"Method","signature":"Thing MakeThing(ThingDef def, ThingDef stuff)","accessibility":"Public","isStatic":true,"isVirtual":false}`
- `get_decompiled_source{…:06003A06:M}` → 首行原文：
  `public static Thing MakeThing(ThingDef def, ThingDef stuff = null)`
  方法体内含：`Log.Error("MakeThing error: " + def?.ToString() + " is madeFromStuff but stuff=null. Assigning default.");`
  ⇒ 吐槽者记的两条相反结论里，「只有单参重载」是**错的**，会诱导写出 `stuff=null` 的 error 级日志；正确是「带默认参，显式传参即可消」。

**(b) `Thing.Position` —— 真相是「有 public setter」**

- `get_members_of_type{typeId:"Verse.Thing", kind:"property", cursor:"30"}` → `{"name":"Position","kind":"Property","signature":"IntVec3 Position","accessibility":"Public", …}`
- `get_decompiled_source{…:17000AA8:P}` → `public IntVec3 Position { get { return positionInt; } set { … } }`，setter 里会 `DirtyMapMesh(Map)` / `Map.thingGrid.Deregister/Register` / `coverGrid` / `gasGrid.Notify_ThingSpawned` / `reachability.ClearCache()`。
  ⇒ 「不可写、须反射」是**错的**；可以直接赋值。（附带知识：反编译出的 backing field 名是 `positionInt`，不是 `_position`。）

### 3.4 「正确答案在哪儿」这件事，本仓库从没告诉过任何人

- 全仓 grep `decompilerserver` → **只有 1 处命中**：`docs/mcp-吐槽板.md:463`。
- 也就是说 `README.md` / `AI-DEPLOYMENT.md` / `docs/*.md` 从未提及这个 MCP 服务器。新会话的 AI 不会知道「成员可见性可以去那儿问」。

---

## 4. 影响与严重度复评

原标注 **🟡 低** → 复评 **🟠 中**。

- **会造成相反结论，且已经发生**：吐槽者明确记录「本次两份分计划的正文都记错了」（`docs/mcp-吐槽板.md:464-471`）。这符合 §5 第 2 条 🔴 的描述（「会让自动化得出事实相反的结论」）。
- **但不该升 🔴**，两个理由：
  1. 没有工具在**谎报**数据 —— `inspect_type` 是明明白白「我开了个面板」，`find_methods` 是明明白白「我只有 type+method」。假数据源 ≠ 信息不足。
  2. 纠错成本低：一次 decompilerserver 只读查询即可证伪（本次两条都在秒级返回）。
- 真正的假阴性风险点在这里：`find_methods` 返回 `{type, method}` 时，**无法区分「只有单参重载」与「带默认参的两参方法」**，而这恰恰是吐槽者踩的那个坑。这类「看起来像答案、其实不足以判断」的信息最容易诱发脑补。

---

## 5. 责任归属

**本项目**（不是 vanilla，不是 UnityExplorer，不是文档缺失）。

理由：
- `McpRimDebug/DebugSession.Eval.cs:355-367` 丢掉 Cecil 元数据是本项目自己的取舍；
- `Source/UELoader/UEHttpHandler.cs:120-161` 决定 `inspect_type` 只回 `{typeName, message}` 也是本项目的实现；
- `McpRimDebug/RimDebugTools.cs:86-88` 的工具描述没有提醒「这只是名字，不是签名」也是本项目写的文案。

上游 vanilla RimWorld 与第三方 UnityExplorer 都不背这个锅：`InspectorManager.Inspect(type)` 只是被调用方，它没做错任何事。

唯一带一点「文档缺失」性质的是：项目文档从不告诉 AI `decompilerserver` 存在 —— 但那也仍是本项目的锅。

---

## 6. 修的话怎么修（方向，具体实现交给 PLAN）

1. **最小改（半天级，性价比最高）**
   - 文件：`McpRimDebug/DebugSession.Eval.cs:355-367`（`AddMethodResult`）与 `:507`（调用点）。
   - 改法：`AddMethodResult` 增加入参 `MethodDefinition m`，输出字段加 `signature`（`m.ReturnType` + 参数串）、`isPublic`（`m.IsPublic`）、`isStatic`、`isVirtual`、`hasOptionalParameters`。
   - 同步改文案：`McpRimDebug/RimDebugTools.cs:86-88` 的描述（现在写死「返回 `{type, method}` 列表」）。
   - 注意：`Program.cs:107-144` 的 selftest 会遍历注册表校验 schema，新增字段不影响；但若要加断言要在 `Program.cs:120-121` 的 `paramAsserts` 里补一行。

2. **更实用：新增一个真正的「成员级」工具**
   - 文件：`McpRimDebug/RimDebugTools.cs`（注册，仿 `:83-88` 的 `Add(...)`）、`McpRimDebug/DebugSession.Eval.cs`（实现）。
   - 改法：加 `describe_type{ typeName, kind? }`，走 mono 端 `McpRimDebug/SdbClient/TypeMirror.cs:416 / :436 / :481` 的 `GetMethods()` / `GetFields()` / `GetProperties()`，返回成员名 + `IsPublic` + `IsStatic` + `IsVirtual` + 属性有无 setter（参照 `SdbClient/PropertyInfoMirror.cs:73-86` 已有 `get_method.IsPublic` / `set_method.IsPublic` 判定）。
   - 注意：`SdbClient/TypeMirror.cs:260` 的 `IsPublic` 只反映类型本身，对成员逐个判定仍要走各自的 Mirror。
   - 前置条件与 `find_types` 相同（需 attach + VM 在），和 `decompilerserver` 的离线路线互补。

3. **零成本文案修正（当天就能做，建议无论 1/2 做不做都做）**
   - `MCP/index.js:1957`：`inspect_type` 的 description 补一句「本工具只在游戏 UI 打开面板，**不向调用方返回任何成员信息**」。
   - `README.md` / `AI-DEPLOYMENT.md` 的 MCP 能力清单里补一行：「查成员签名 / 可见性 / 是否 static → 用 decompilerserver 的 `get_members_of_type(mode:"signatures")`（返回 `signature` / `accessibility` / `isStatic` / `isVirtual`）」。

---

## 7. 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md` 共 12 个任务：
  - **任务 9「自动补全迁移（语义模型三来源）」** 是**游戏内 C# 控制台**的补全（`CSAutoCompleter` → `RoslynCompletion`，计划 `:1599-1809`），与 MCP 工具面无关；
  - **任务 12「MCP 文案与端到端验收」**（计划 `:2050-2102`）只做既有工具的文案与验收矩阵，没有「成员可见性查询」这一项。
- 计划 §0 `:33` 记录的 `Verse.PathFinder.ScheduledPathJob` `CS0122`（非 public）只是**踩坑事实**，不是针对本条目的修复任务。
- `docs/issue-triage/plans/` 目录当前为空，无其它子代理写过重复计划。

**结论：值得修，建议排一张小计划（1 + 3 优先，2 可选）。**
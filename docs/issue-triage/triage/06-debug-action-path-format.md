裁决：值得修

DebugAction 路径格式（`\` 分隔、路径段是 `DebugActionAttribute` 的 `name` 而非 `category`）在 vanilla 源码里有唯一权威定义，但本项目**没有任何一处文档声明这条规则**，连唯一出现该格式的地方还是一个不出现在任何菜单里的隐藏工具的一行提示——所以每个新会话都会重踩。

## 核实过程

### 1. 吐槽板原文（`docs/mcp-吐槽板.md:371-391`）

断言三条：(a) `[DebugAction("FlightAStar", "Run All Tests", …)]` 的路径是 `Actions\Run All Tests`；(b) 第一个参数 `category` 是返回字段不是路径段；(c) 用 `/` 分隔同样失败。三条全部可核实。

### 2. 路径格式的权威定义在 vanilla（**不是**本项目代码）

`Source/LudeonTK/DebugActionNode.cs:139-155` —— `Path` 由 `label` 拼接而成，`category` 全程不参与：

```csharp
public string Path
{
    get
    {
        if (cachedPath == null)
        {
            if (parent != null && !parent.IsRoot) { cachedPath = parent.Path + "\\" + label; }
            else { cachedPath = label; }
        }
        return cachedPath;
    }
}
```

`Source/LudeonTK/Dialog_Debug.cs` 的 `GetNode`（第 292 行起）证明 `/` 一定失败——**只按 `'\\'` 切分**：

```csharp
public static DebugActionNode GetNode(string path)
{
    TrySetupNodeGraph();
    DebugActionNode debugActionNode = rootNode;
    string[] s = path.Split('\\');
    for (i = 0; i < s.Length; i++)
    {
        DebugActionNode debugActionNode2 = debugActionNode.children.FirstOrDefault((DebugActionNode x) => x.label == s[i]);
        if (debugActionNode2 == null) { return null; }
        debugActionNode = debugActionNode2;
        debugActionNode.TrySetupChildren();
    }
    return debugActionNode;
}
```

传 `Actions/FlightAStar` 时 `Split('\\')` 得到长度 1 的数组，拿整串去比 `label`，必然 `null`。**吐槽板 (c) 属实，且是设计使然**。

`Source/LudeonTK/DebugActionAttribute.cs` 的构造函数确认参数顺序 —— `category` 在前、`name` 在后：

```csharp
public DebugActionAttribute(string category = null, string name = null, bool requiresRoyalty = false, ...)
{
    this.name = name;
    ...
    if (!string.IsNullOrEmpty(category)) { this.category = category; }
}
```

即 `[DebugAction("FlightAStar", "Run All Tests", …)]` → `category="FlightAStar"`、`name="Run All Tests"`，而 `Path` 只用 `label`（= `name`）。**吐槽板 (a)(b) 属实。**

`category` 确实是返回字段：`Source/UELoader/UEDebugActions.cs:156-163` 的 `ListActions` 把 `{"path", "label", "hasChildren", "category", "actionType"}` 并列返回。

### 3. 本项目侧：确实没有任何格式文档

- `README.md:131-141` 与 `README.md:171-172` 列出了 `rimworld.list_debug_action_roots` / `get_debug_action` / `execute_debug_action`，只说「仅 `path`/`pawnId`」，**没有任何格式说明**。
- `MCP/first-configs.md:12` 只提到「DebugAction 执行原生调试菜单动作」，同样没有格式。
- 全仓库搜 `Actions\\`，只有两处命中，且**都不是 DebugAction 工具自己的说明**：
  - `MCP/index.js:2411`（`hotreload_apply` 的 `description` 尾部）：`提示：XML/Defs 改动不需要本工具，用 rimworld.execute_debug_action path="Actions\\Hot reload Defs"。`
  - `MCP/index.js:2980`（`hotreload_apply` 的 `TOOL_USAGE_NOTES.notes`）：同一句。

关键问题：`hotreload_apply` 是**隐藏工具**。`MCP/index.js:2410` 自己写明「本工具**不出现在任何菜单/工具清单**中，仅可按名显式调用」。也就是说，从工具列表出发的发现路径**永远看不到**这条唯一的格式样例。

### 4. 真实调用日志佐证（`MCP/logs/mcp-20261001-093829.log`）

所有实际发起的调用都用了反斜杠格式，且**没有** category 段：

- 第 406 行：`agg_call_tool args={args={"path":"Actions\\运行全部动画测试"}, tool=rimworld.execute_debug_action}`
- 第 409 行：`{"path":"Actions\\Stage Test Map"}`
- 第 433 行：`{"path":"Actions\\Run All Tests"}`
- 第 961 行：`{"path":"Actions\\Stage Test Map"} → 3ms error(GABP_MIRROR_UNAVAILABLE)`

（这些调用全部因 GABP 超时/未连接而失败，**没有**成功样本，因此「`Actions\Run All Tests` 这个具体路径是否真能命中 FlightAStar 的动作」我无法用日志证实——只能证实调用方已经采用了 `\` 格式。路径规则本身由上面第 2 节的 vanilla 源码独立证实。）

### 5. 是否已有「找不到就给候选」机制

`MCP/index.js:3979-4002` 的 `suggestTools` 是**工具名**相似度建议（双字母组 Dice 系数），与 DebugAction 的 `path` 参数无关。而 `execute_debug_action` / `get_debug_action` 现在全部走 GABP 镜像（`MCP/index.js:4898-4901` 的注释确认原生实现已删），失败文案由 RimBridgeServer 侧生成，本项目无法在其中插入候选。**吐槽板建议的「提示最接近的候选」目前不存在。**

## 影响与严重度复评

- **复评严重度：🟠 中，维持不变。** 「每次新会话都要重新踩」有代码结构支撑：唯一格式样例藏在一个隐藏工具里。
- **假成功 / 假阴性风险：无。** 传错路径的后果是明确报错（`GetNode` 返回 `null` → `PATH_NOT_FOUND`，见 `Source/UELoader/UEDebugActions.cs:140`），不会让自动化得出相反结论。
- 真实代价是**往返浪费**：猜错一次 = 一次调用 + 一次报错；`/` 和 `category` 两种错法都是「看起来很合理」的错法，容易连续踩两次。这符合 🟠「每次都要重踩 / 系统性浪费往返」。
- 附带的文档污染风险成立：吐槽板自己就说「分计划正文里也普遍写成 `FlightAStar/Run All Tests` 这种错法」。本轮核查的 `docs/superpowers/plans/2026-09-27-ue-csharp-console-roslyn-upgrade.md` **没有**出现这类错写（全文无 DebugAction 路径），所以该计划不是受害者，但也不能算作已有覆盖。

## 责任归属

**本项目（纯文档缺失），不是 vanilla 的锅。**

理由：vanilla 侧行为完全自洽——`Path` 用 `label` 拼、`GetNode` 用 `'\\'` 切，两边一致，不存在 vanilla bug。本项目做的是「把 vanilla 的既有约定转成 AI 可发现的接口」，而这一步没做。`category` 不进路径也不是缺陷，是 vanilla 的正常设计（用于菜单分组排序，`DebugActionNode.cs:208` 按 `category` 排序）。需要改 vanilla 吗？**不需要**；需要改第三方（GABP）吗？**不需要**（本项目自有的 `search_debug_actions` / `get_debug_action_categories` 与 README 都可承载说明）。

## 修的话怎么修

按「成本从低到高」列，全部落在本项目内：

1. **声明格式（最低成本，必做）**
   - `MCP/index.js:2356-2357`（`search_debug_actions` 的 `description`）与 `MCP/index.js:2348-2349`（`get_debug_action_categories`）补一句格式规则：`path` 形如 `Actions\<label>`，分隔符**只能是反斜杠**，段是 `DebugActionAttribute` 的 `name`（第 2 个参数），`category`（第 1 个参数）**不是**路径段、只作为返回字段出现。
   - `README.md:131-141` 与 `README.md:171-172` 补同一段规则 + 一个 `[DebugAction("FlightAStar","Run All Tests")]` → `Actions\Run All Tests` 的例子。

2. **让格式随返回值走（推荐，性价比最高）**
   - `Source/UELoader/UEDebugActions.cs:167-172`（`ListActions` 返回体）与 `:245-253`（`Search` 返回体）各加一个 `pathFormat` 说明字段，值为上面那句规则。这样无论调用方从哪个工具拿到 `path`，格式规则就跟在同一份 payload 里，不依赖它先读过文档。
   - 这条对 GABP 镜像路径同样有效：`MCP/index.js:4934-4971` 在转发 `search_debug_actions` 时可以在 `data` 上补同一个字段。

3. **兼容 `/` 与多写 category（可选，代码改动）**
   - 在 `Source/UELoader/UEDebugActions.cs` 的 `ListActions` / `GetDetail` / `Execute` 三个入口（当前分别在第 129、257、约 293 行）加一个 `NormalizePath()`：把 `/` 归一成 `\`，并在首段不是已知根标签时尝试剥掉一个疑似 category 段。归一化失败则原样透传，保留原报错。
   - 风险提示：这条会让「路径是 Opaque 字符串」变成「路径可被静默改写」，与 [01]「报告成功但没执行」这类 🔴 问题的排查方向冲突。建议只在**明确未命中**时做候选猜测并把猜测过程写进返回，而不是静默改写后执行。**这一条优先级最低，建议单独立项。**

4. **失败时给候选（成本较高，收益递减）**
   - 失败发生在 RimBridgeServer 侧，本项目改不了。若一定要做，只能在 `MCP/index.js` 的 GABP 路由层（`isGabpMirrorName` 分支，第 4262 行起）拦截 `rimworld.execute_debug_action` / `rimworld.get_debug_action` 的失败响应，再用本项目自有的 `search_debug_actions` 端点做一次候选召回。这需要新增一次游戏往返，**不建议**。

## 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/` 下只有一份计划：`2026-09-27-ue-csharp-console-roslyn-upgrade.md`（全文 2129 行，12 个任务）。逐节核过后，它的所有任务都围绕 Roslyn 编译器升级（任务 1 Roslyn 载荷、2 默认引用集合、3 默认 using、4 polyfill、5 编译内核、6 REPL、7 IScriptEvaluator、8 ConsoleController 接线、9 自动补全、10 HookInstance 迁移、11 打包许可、12 MCP 文案与端到端验收）。任务 12 步骤 1 只改 `MCP/index.js:2937` 的 `execute_csharp_code` notes。
- 全文无 `DebugAction`、无 `Actions\` 路径相关任务，也无 `README` 的 DebugAction 小节改动计划。
- `docs/issue-triage/plans/` 目录**当前为空**，没有其他子代理已经写过覆盖本条的计划。
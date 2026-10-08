裁决：值得修

# [11] `search_source` 搜高频标识符 → 400+ 条重复结果 🟠

**一句话结论**：现象 100% 可复现（我实测拿到 `[TRUNCATED] Showing 400/416 results`），但**真正该修的不是"上下文税"那半条 —— 那是已被上游 400 条上限约束住的、可接受的；而是"精确签名零命中"那半条：一个确实存在且 `public` 的成员返回 `No results found`，这是货真价实的假阴性**。后者在本仓库内修不了（工具属于另一个 MCP 服务），所以本条的产出主要是**文档 + 已知更好路径**，而非改代码。

---

## 1. 核实过程

### 1.1 复现：400/416 精确命中

我实际调用了 `mcp__rimworld-source-online__search_source{ query: "AllPawnsSpawned", file_pattern: "*.cs" }`（只读静态检索，不触碰游戏状态）。

返回末尾：

```
./Source/RimWorld/Building_SubcoreScanner.cs
501:				IReadOnlyList<Pawn> allPawnsSpawned = base.Map.mapPawns.AllPawnsSpawned;

[TRUNCATED] Showing 400/416 results.
(Tip: Refine your search query or add a more specific `file_pattern`.)
```

⇒ **`Showing 400/416` 与吐槽板 `docs\mcp-吐槽板.md:204` 写的数字逐字一致。** 属实。

**与吐槽板的一处出入**：吐槽板没提到输出末尾的 `(Tip: ...)` 提示行。**截断时已经带提示了** —— 这条吐槽板记录时可能还没有，或当时没注意。

### 1.2 复现：声明行确实"埋在正中间"，且无排序保证

同一返回里，目标声明行出现在**第 5 个文件块**：

```
./Source/Verse/MapPawns.cs
185:			IReadOnlyList<Pawn> allPawnsSpawned = AllPawnsSpawned;
...
327:	public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;
```

排序规则看起来是**按文件路径字母序**（`RimWorld/*` 与 `Verse/*` 交错），**没有任何"声明优先"的启发式**。⇒ 吐槽板 `docs\mcp-吐槽板.md:205-206` 说的「埋在正中间，且没有任何排序保证它先出现」，属实。

### 1.3 复现（更严重的那半条）：假阴性

我实际调用了 `mcp__rimworld-source-online__search_source{ query: "public IEnumerable<Pawn> AllPawnsSpawned", file_pattern: "*.cs" }`：

```
No results found. Try adjusting your search query or file pattern.
```

而同一个成员**确实存在，且是 `public`**，由另一次调用证明 —— `search_source{ query: "AllPawnsSpawned =>", file_pattern: "Source/Verse/MapPawns.cs" }`：

```
./Source/Verse/MapPawns.cs
327:	public IReadOnlyList<Pawn> AllPawnsSpawned => pawnsSpawned;
```

⇒ **吐槽板 `docs\mcp-吐槽板.md:219-220` 描述的假阴性，逐字复现。**

**但根因要说准**：这不是匹配器坏了。源码里就是 `IReadOnlyList`，查询串里的 `IEnumerable` 天然零命中。真正的问题是 —— **`No results found` 这一个返回，同时承载了三种语义完全不同的状态**：

1. 该成员不存在
2. 该成员存在但签名/返回类型与你的猜测不同
3. 该成员存在但你猜错了可见性（`private` / `internal`）

工具对这三种**给出完全相同的输出**，且没有任何区分提示。调用方无从判断该往哪个方向怀疑。

⇒ 这不是"工具算错了"，而是"工具的零命中**不可判读**"。吐槽板说「很容易让人误判这个成员不存在/不可见」——准确。

### 1.4 归属核实：`search_source` 不在本仓库

- 在整个 `UESDdebuger` 目录 grep `rimworld-source-online`，**只命中吐槽板自身**（`docs\mcp-吐槽板.md:128-129`）两处文字引用，无任何代码。
- `MCP/` 目录 grep `search_source|rimworld-source-online` —— **零命中**。本仓库的 MCP 服务器（`MCP/index.js`，5460 行）**不提供**这个工具。
- 工具的实际宿主是独立的 MCP 服务 `rimworld-source-online`。本机只找到 trae-cn 的会话元数据存根：
  `C:\Users\Timer_0\.trae-cn\mcps\s_294100-84c857ec\solo_agent\mcp_rimworld-source_online\SERVER_METADATA.json`，内容仅：

  ```json
  {
    "server_name": "mcp_rimworld-source_online",
    "description": null
  }
  ```

  **该服务的源码不在本机、不在本仓库。** ⇒ 任何"改 `search_source` 内部实现"的方案都不落在本项目内。

### 1.5 工具契约已部分到位（这削弱了"上下文税"那半条的严重度）

从 1.1 的实测可见，上游已经做了三件事：

1. **400 条硬上限**（不是无限流）
2. **显式截断声明**：`[TRUNCATED] Showing 400/416 results.`
3. **行动提示**：`(Tip: Refine your search query or add a more specific `file_pattern`.)`

⇒ 吐槽板 `docs\mcp-吐槽板.md:316` 的"加过滤参数"诉求，上游**没有**响应（没有 `limit` 参数，只有固定 400）；但也**没有**让调用方在无提示的情况下被淹没。**这是一个"吵但有界"的设计，不是"失控"的设计。**

### 1.6 顺手证实一条吐槽板写错的点：`file_pattern` 接受路径，不只是 glob

吐槽板 `docs\mcp-吐槽板.md:227` 说「或者支持 `file_pattern` 直指单文件（当前是 glob，但输出不按文件聚合）」。

实测 `file_pattern: "Source/Verse/MapPawns.cs"`（一个完整相对路径）**可用**，直接命中目标文件并只回 1 条。⇒ **它已经支持直指单文件了**，吐槽板这条建议已经过时。

**输出确实是按文件聚合的**（每个文件块一个 header），这一半吐槽板说反了。

---

## 2. 影响与严重度复评

**拆成两半分别定级。**

### 2.1 「400+ 条重复结果」→ 从 🟠 降为 🟡

- **有界**：400 条上限 + 显式 `[TRUNCATED]` + `Tip` 提示，不是失控流。
- **不产生错误结论**：返回的每一条都是真实匹配，只是冗余。调用方不会因此得出事实相反的结论。
- **成本（实测）**：我这次调用返回约 200 个文件块、250+ 行 ASCII 代码，粗估约 15K 字符 / **4–5K token**（**估算值**，非精确计量）。
  与 [03] 的约 100K–115K token（估算）差一个数量级。
- ⇒ 按 AGENT-BRIEF §5.2，这属于「体验略糙 / 有界的系统性浪费」，**🟡**。

### 2.2 「精确签名零命中」→ 🔴（维持，且我认为比原判更高）

- **这是真正的假阴性**：返回 `No results found`，而目标成员存在且 `public`。
- **会直接导致事实相反的结论。** 吐槽板 `docs\mcp-吐槽板.md:220-221` 自述：「很容易让人误判『这个成员不存在/不可见』… 我差点据此改掉测试代码 —— 差点把一个 API 认知错误写进源码」。
  这正是 AGENT-BRIEF §5.2 定义的 🔴 判据：**自动化得出事实相反的结论，并把它固化进源码**。
- 讽刺的是，本条最该修的半条（零命中的可判读性），**恰好是上游服务的锅，本项目修不了**（见 §3）。

⇒ **本条整体维持 🟠，但内部严重度重新分配：假阴性 🔴 > 上下文税 🟡。** 这个再分配很重要 —— 它直接决定 PLAN 应该写什么。

---

## 3. 责任归属

**两个半条归属不同，必须分开说。**

### 2.1 假阴性（`No results found` 不可判读）→ **第三方 / 外部 MCP 服务，不是本项目的锅**

`search_source` 属于 `rimworld-source-online` 服务，其源码不在本仓库（1.4）。工具本质是**对 vanilla 反编译源码做行级子串/正则匹配**：

- 它**没有符号表**，因此从原理上就**不可能**回答「这个成员可不可见」—— 那需要成员签名元数据。
- 这也解释了为什么吐槽板索引里的 **[10]「无法查询当前 vanilla / 模组 API 的实际成员可见性」**（`docs\mcp-吐槽板.md:27`）和本条其实是**同一个能力缺口的两面**。

⇒ 要真修零命中的可判读性，得改那个外部服务的返回契约（例如零命中时附带「同文件内模糊匹配候选 N 条」）。**本项目做不到。**

**本项目能做的**：把这条认知写进项目文档，让子代理不再被坑。

### 2.2 上下文税 → **纯文档缺失，不是代码错**

400 条上限 + 截断声明 + Tip 提示都是上游正确行为。本项目缺的是**告诉子代理「怎么用这个工具」的知识**。

---

## 4. 修的话怎么修

**关键约束：本条的主体修不动代码。** 按性价比排序：

1. **把「宽搜 → 定位文件 → `read_file` 精读」固化成项目内的既定流程，写进文档。**
   落点建议：新建 `docs/` 下的一节，或并入已有的 MCP 使用说明。
   要点固化为三条可执行规则：
   - **一次宽搜只为拿文件名**，拿到 `Source/Verse/MapPawns.cs` 就停；
   - **再读用 `read_file` + `file_pattern` 直指该文件**（实测可行，见 1.6），
   - **绝不把宽搜结果当结论** —— 结论只能来自读到的那一行。

2. **明确写一条禁令：禁止用带返回类型限定的 query 做存在性判断。**
   这是本条 🔴 半条的**唯一在本仓库内可行的止血手段**。
   建议措辞：「判断『某成员是否存在/可见』时，query **只能**用成员名本身；**不要**把返回类型、可见性修饰符写进 query —— 猜错类型会得到零命中，而零命中在 `search_source` 里**不区分**『不存在』和『你猜错了』。」

3. **交叉指向 [10]。**
   本条与索引 [10]（`docs\mcp-吐槽板.md:27`）是同一个能力缺口的两面。建议主代理把 [10] 和 [11] 合并成一个议题处理，避免两份计划各写一半。

4. **（不推荐，仅记录）** 若愿意维护外部服务，可在 `rimworld-source-online` 的零命中分支补一条
   「`query` 含 `类型 ` 前缀时提示『可试试去掉返回类型限定』」—— 吐槽板 `docs\mcp-吐槽板.md:228-229` 的建议。
   但这**不属于本项目**，主代理需要先确认那个服务是否也在自己的维护范围内。**本仓库内无此代码。**

**不建议**做的：不要为绕开 400 条上限在本仓库内造一个 `search_source` 的代理/包装工具 —— 那是在别人的服务外面套一层，既解决不了假阴性，又制造第二份要维护的索引。

---

## 5. 是否已被现有计划覆盖

**否。**

- `docs\superpowers\plans\` 下只有 `2026-09-27-ue-csharp-console-roslyn-upgrade.md`。grep `list_debug_action_children|search_debug_actions|search_source|start_quick_test|waitForNotification|上下文|context budget|裁剪|截断|truncat` —— **零命中**。
  再 grep `^#{1,3} |^## Task|start_game|工具返回|token` 得到的 26 处命中全部是 Roslyn 相关（`## 任务 1`～`## 任务 12`，见 `:95` ～ `:2050`），与本条无关。
- 该计划的验收矩阵（`:2069-2084`）第 12 行恰好用了 `Find.CurrentMap.mapPawns.AllPawnsSpawned.Count` —— 说明维护者在计划里确实会用这个 API，但那是**在 `execute_csharp_code` 里跑**，不是走 `search_source`，所以不构成覆盖。
- `docs\issue-triage\plans\` 目录当前不存在，无重复计划风险。

⇒ **本条不适合派标准 PLAN 子代理**（主体改不动代码）。建议主代理：
1. 把 [10] + [11] 合并为一个议题；
2. 派一个**文档型**任务，落点即 §4 的第 1–2 条；
3. 在计划里显式写明「零命中的可判读性问题需上游 `rimworld-source-online` 配合，本项目无法单独修复」，避免后续误以为漏做了。
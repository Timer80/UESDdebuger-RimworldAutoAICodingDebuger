裁决：值得修
归并：并入 [11]（理由：症状类、修法形态（外部 server + 文档约定）、落点文档全部相同；但**不是重复条目** —— 根因与 [11] 不同，见 §7，且 [11] 计划的 §5 维护规则已自建「该服务唯一规范文档」，[16] 应作为其中一节追加）

# [16] `search_source` 对 `read_csharp_symbol` 能读到的文件回 `No results found` 🟠

## 1. 一句话结论

**派单时给我的假设（「[16] 是索引覆盖问题」）被我实测证伪**：`Source/Verse/Thing.cs` **100% 在 `search_source` 索引里**；
真正的新机制是 **`file_pattern` 必须带 `Source/` 前缀，否则静默零命中** —— 而 `read_csharp_symbol` 自己的返回头 `// File: Verse/Thing.cs`
打印的**恰好就是那种必然零命中的路径**，两个工具互相喂毒。
⇒ 吐槽板上「同一个文件能读不能搜」这个结论是**事实相反的**（该文件可搜得很，我拿到了 3 条命中）。

## 2. 核实过程

### 2.1 读了什么

| 文件 | 用途 | 关键行 |
|---|---|---|
| `docs/mcp-吐槽板.md` | 条目原文 | 索引行 `:33`；正文 `## [16]` `:37-59` |
| `docs/issue-triage/AGENT-BRIEF.md` | 铁律与裁决尺子 | `:22-52`, `:93-117` |
| `docs/issue-triage/triage/11-search-source-result-volume.md` | [11] 裁决（协调必读） | 全文 187 行，重点 §1.3 假阴性、§1.6 `file_pattern`、§4 修法 |
| `docs/issue-triage/triage/14-read-file-start-line-zero-indexed.md` | [14] 裁决（同工具链） | 全文 130 行 |
| `docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md` | [11] 计划（**并发写入中，我只读**） | 565 行，重点 `:59-70`, `:240-263`, `:317-325` |
| 全仓 `*.cs` + `MCP/` grep `file_pattern\|search_source\|read_csharp_symbol\|list_directory` | 归属核实 | **双双 0 命中** |

### 2.2 归属：`search_source` 源码不在本仓库（独立复核，不沿用 [11] 结论）

- 全仓 `*.cs` grep `file_pattern|rimworld-source-online|read_csharp_symbol` → **No matches found**
- `MCP/` 目录 grep `file_pattern|search_source|read_csharp_symbol|list_directory` → **No matches found**

⇒ 四个工具全部由**外部 MCP server `rimworld-source-online`** 提供，本项目一行代码都碰不到。

### 2.3 复现 A —— 板上现象**逐字属实**（只读工具，20 次调用）

```
search_source{ query:"pather\\?\\.StopDead\\(\\)", file_pattern:"Verse/Thing.cs" }
  → No results found. Try adjusting your search query or file pattern.
search_source{ query:"StopDead", file_pattern:"Verse/AI/Pawn_PathFollower.cs" }
  → No results found. Try adjusting your search query or file pattern.
read_csharp_symbol{ typeName:"Thing", memberName:"DeSpawn" }
  → // File: Verse/Thing.cs (Lines 945-1036)  + 92 行方法体全文
```

⇒ 板上 `:43-45` 的两条 `No results found` 与成功返回，**全部复现**。

### 2.4 证伪 #1：索引覆盖完全正常 —— `Thing.cs` 可搜

```
search_source{ query:"StealAIDebugDrawer", file_pattern:"Source/Verse/Thing.cs" }
  → ./Source/Verse/Thing.cs
    883:  StealAIDebugDrawer.Notify_ThingChanged(this);
    1010: StealAIDebugDrawer.Notify_ThingChanged(this);
    1591: StealAIDebugDrawer.Notify_ThingChanged(this);

search_source{ query:"Notify_ThingDespawned", file_pattern:"Source/Verse/Thing.cs" }
  → 968:  map.tooltipGiverList.Notify_ThingDespawned(this);
    1004: map.attackTargetsCache.Notify_ThingDespawned(this);
    1035: map.events.Notify_ThingDespawned(this);
```

**「索引覆盖」假设被推翻。** `Source/Verse/Thing.cs` 就在索引里，返回带正确的 1-based 行号。

### 2.5 交叉校验：两个工具的行号**逐行吻合**（这是最硬的一条证据）

把 `read_csharp_symbol(Thing, DeSpawn)` 返回的方法体逐行数（方法体第 1 行 = 头部声明的 945）：

| 方法体第 N 行 | 代码 | 推出 1-based 行号 | `search_source` 实测 | |
|---|---|---|---|---|
| 24 | `map.tooltipGiverList.Notify_ThingDespawned(this);` | 945+23 = **968** | 968 | ✅ |
| 60 | `map.attackTargetsCache.Notify_ThingDespawned(this);` | 945+59 = **1004** | 1004 | ✅ |
| 66 | `StealAIDebugDrawer.Notify_ThingChanged(this);` | 945+65 = **1010** | 1010 | ✅ |
| 91 | `map.events.Notify_ThingDespawned(this);` | 945+90 = **1035** | 1035 | ✅ |
| 92 | `}` | **1036** | 头部 `Lines 945-1036` | ✅ |

同理 `read_csharp_symbol(Pawn, DeSpawn)` 头部 `Lines 3658-3690`，方法体第 9 行
`pather?.StopDead();` → 3658+8 = **3666**，而全局搜索返回的正是 `./Source/Verse/Pawn.cs  3666: pather?.StopDead();`。

⇒ **`read_csharp_symbol` 与 `search_source` 返回同一份内容、行号逐行一致。二者零分歧。**
⇒ 板上「命中面不一致」的前提**不成立**；「能读不能搜」是**事实相反的结论**。

### 2.6 证伪 #2：板上那条 query 是**真阴性**（工具答对了）

```
search_source{ query:"pather", file_pattern:"Source/Verse/Thing.cs" }
  → No results found.
```

⇒ `Thing.cs` **全文件不含 `pather` 二字**。`Thing.DeSpawn` 确实不碰 pather。
⇒ **板上的代码事实结论是对的**（「`Verse.Thing.DeSpawn` 确实不调 pather」）。
**错的只是它对「为什么搜不到」的解释** —— 它把一次正确的真阴性，误诊成了工具坏了。

（**行号细节更正**：板上 `:51-52` 说 `pather?.StopDead()` 在 override「第 3 行」，
实际在方法体**第 9 行 / 文件第 3666 行**。结论对，行号错。）

### 2.7 发现 #3（真机制）：`file_pattern` 缺 `Source/` 前缀 ⇒ **静默假阴性**

同一个 token、同一个文件，只改路径写法：

| # | `query` | `file_pattern` | 返回 | 判定 |
|---|---|---|---|---|
| 1 | `StealAIDebugDrawer` | `Source/Verse/Thing.cs` | **3 条命中** | ✅ 基准 |
| 2 | `StealAIDebugDrawer` | `Verse/Thing.cs` | **No results** | ❌ **假阴性** |
| 3 | `StealAIDebugDrawer` | `Verse/*.cs` | **No results** | ❌ **假阴性**（glob 也一样） |
| 4 | `StopDead` | `Source/Verse/Pawn.cs` | **2 条命中** | ✅ |
| 5 | `StopDead` | `Verse/Pawn.cs` | **No results** | ❌ **假阴性** |
| 6 | `StopDead` | `Source/Verse/AI/Pawn_PathFollower.cs` | **4 条命中**（`204: public void StopDead();` …） | ✅ |
| 7 | `StopDead` | `Verse/AI/Pawn_PathFollower.cs` | **No results** | ❌ **铁的假阴性** |

**#7 是无法辩解的**：全局搜索已独立证明 `Source/Verse/AI/Pawn_PathFollower.cs:204` 就是 `public void StopDead()`。
拿**板上自己写的那个路径**去搜它**确实存在的东西**，回 `No results found`。这就是货真价实的假阴性。

配套佐证（`list_directory`，只读）：

```
list_directory{ path:"" }        →  Defs/   Source/          ← server 根只有这两项
list_directory{ path:"Source/Verse" } → AI/ Glow/ Grammar/ … [TRUNCATED] Showing 20/1755 items.
```

⇒ 根下**只有 `Defs/` 和 `Source/`**，`Verse/Thing.cs` 在根下**根本不存在**。
**（假设）**：`file_pattern` 是拿整仓相对路径做锚定匹配，缺 `Source/` 前缀 ⇒ 永不命中。
这是**推断**，四条观测全部一致但我没有该 server 源码可证 —— 按铁律标为假设。

### 2.8 发现 #4：`read_file` 对同一路径**报错**，`search_source` 却**静默**

```
read_file{ path:"Verse/Thing.cs", start_line:945 }
  → Error: File not found: Verse/Thing.cs          ← 显式报错，行为正确
search_source{ file_pattern:"Verse/Thing.cs", … }
  → No results found. Try adjusting your search query or file pattern.   ← 静默，同一句
```

⇒ **同一 server 内两个工具对同一个坏路径给出两种反应**：`read_file` 报错，`search_source` 谎称「没匹配」。

### 2.9 附带发现（补强 [11]）：`query` 是**正则**，字面量元字符不转义 ⇒ 又一种假阴性

```
search_source{ query:"pather\\?\\.StopDead\\(\\)", file_pattern:"Source/Verse/Pawn.cs" }
  → 3666: pather?.StopDead();   4059: pather?.StopDead();      ← 转义正确 ✅
search_source{ query:"pather?.StopDead()",         file_pattern:"Source/Verse/Pawn.cs" }
  → No results found.                                          ← 字面量未转义 ❌ 假阴性
```

⇒ 同一个字符串，转义与否差一个数量级的结果。**这一条机制与 [11] 完全同类**（query 形状）。

### 2.10 四义混淆矩阵（本条的真正危害）

`search_source` 用**同一句** `No results found` 表达四种语义完全不同的状态：

| 语义 | 实例 | 真/假阴性 |
|---|---|---|
| (a) 文件不在索引 | 第三方模组源码（`[11]` 计划 §0.4 已证索引只有 vanilla） | 真阴性 |
| (b) token 确实不在该文件 | `Source/Verse/Thing.cs` + `pather`（§2.6） | 真阴性 |
| (c) `file_pattern` 路径形状不对 | `Verse/Thing.cs` + `StealAIDebugDrawer`（§2.7） | **假阴性** |
| (d) `query` 正则形状不对 | `pather?.StopDead()` 未转义（§2.9） | **假阴性** |

调用方拿到零命中，**没有任何手段区分自己中的是 (b) 还是 (c)/(d)**。

### 2.11 次要观察：输出顺序跨调用不稳定

同一 `query:"StopDead"` + `file_pattern:"*.cs"`，两次调用（`regex` 默认 vs `regex:false`）
返回**同一组 31 个文件**，但顺序不同（`Pawn_HealthTracker` 在前 vs `JobDriver_NatureRunning` 在前）。
⇒ 印证 [11] 计划 `:94-102` 的结论：**不得依赖返回顺序翻找**。

## 3. 影响与严重度复评

**板上判 🟠。我判 🔴 —— 并且认为这一半比 [11] 的 🔴 半更重。**

### 3.1 它确实造成了「事实相反的结论」，且已经发生

吐槽者本人写下了 **「同一个文件『能读不能搜』」**，并据此把整条定级为「命中面不一致」。
§2.4 已证**真相相反**（该文件可搜，3 条命中），§2.6 已证那次零命中**本来就是对的**。
⇒ 一个**正确的答案被误诊成工具缺陷**，这类误诊会污染后续所有推理（写进分计划，就会派生出「修索引」这种不存在的任务）。

### 3.2 为什么比 [11] 更重

| 维度 | [11] 的假阴性 | [16] 的假阴性 |
|---|---|---|
| 触发条件 | 调用方**自己写错 query**（猜错返回类型） | 调用方**照抄工具自己打印的路径** |
| 是否需要犯错 | 需要 —— 写得对就没事 | **不需要** —— 照做就中招 |
| 失败可自查吗 | 部分可（去掉限定再搜一次） | **难** —— 越"规范"越中招 |

**关键在 §2.5 + §2.7 的组合**：`read_csharp_symbol` 返回头 `// File: Verse/Thing.cs` 是**唯一**告诉调用方「这方法在哪」的信息，
而这个字符串**恰好是 `search_source.file_pattern` 唯一不能接受的写法**（`read_file.path` 同样不接受）。
**工具把正确答案印在了一个喂回去就出错的位置上**，且无任何告警。

### 3.3 但仍不构成「静默破坏」

`Source/` 前缀是**可以事先知道的固定规则**（`list_directory` 一查即知），写进约定即可完全规避。
⇒ 属于「🔴 后果 + 🟠 可预防」，**不是**不可检出的假成功。

**⇒ [16] 整体：🟠 → 🔴（假阴性半条）。建议与 [11] 同级抢修。**

## 4. 责任归属

**第三方（外部 MCP server `rimworld-source-online`），不是本项目的锅。**

1. §2.2 已证：全仓 `*.cs` 与 `MCP/` 双双 0 命中，源码不在本仓库，改不了。
2. 缺陷**完全发生在该 server 内部**：`read_csharp_symbol` 输出 `Source/`-less 路径，
   `search_source` / `read_file` 只认 `Source/`-prefixed —— **它自己的输出和自己的输入不兼容**，
   没有跨到本项目。
3. 本项目唯一的"锅"是**文档没写这条规则**，修复成本极低（补一节约定）。

**与 [13]（命名空间 ≠ 目录）的区别（易混，必须写清）**：
[13] 是「你**猜不出**文件在哪」→ `read_file` 报 `File not found`；
[16] 是「路径**已经给对**了，但**写法不被接受**」→ `search_source` 回 `No results found`。
症状不同、根因不同、修法不同。

## 5. 修的话怎么修

### 5.1 代码侧（上游，本项目做不到）

若维护者确实拥有该 server，按性价比：

| 优先级 | 改法 | 收益 |
|---|---|---|
| **P0** | `read_csharp_symbol` 返回头补上真实路径：`// File: Source/Verse/Thing.cs` | **一行改动，直接消灭本条的主要触发路径** |
| P0 | `search_source` 零命中且 `file_pattern` 不含 `/` 或不以 `Source/`、`Defs/` 开头时，改回 `file_pattern 路径不存在（hint: 真实路径为 Source/…）` | 把 (c) 从假阴性变成真报错 |
| P1 | `read_file` 与 `search_source` 对坏路径**统一报错文案**（§2.8） | 消除内部不自洽 |
| P2 | 零命中时按 `file_pattern` **basename 在索引内模糊匹配**，回候选路径 | 呼应板上 `:55-56` 的建议 |

### 5.2 本仓库内可做（**这才是能落地的部分**）

**不要新建文件** —— [11] 计划 `:319-321` 的维护规则已定：
「本文件（`docs/mcp-source-lookup-conventions.md`）是该服务的**唯一**规范文档，**不要另建文件**」。

追加到 `docs/mcp-source-lookup-conventions.md`：

1. **新增「规矩 0：路径必须锚定在 server 根」**（放在三条硬规矩最前，因为它决定另两条是否生效）：
   - `file_pattern` / `read_file.path` **必须带 `Source/` 或 `Defs/` 前缀**；
   - `list_directory{path:""}` 实测根下只有 `Defs/` 与 `Source/` —— 这是前缀的由来；
   - ⚠ **`read_csharp_symbol` 返回头里的 `// File: Verse/Thing.cs` 是「展示用相对路径」，
     直接喂回 `file_pattern` 或 `read_file.path` 必然零命中 / `File not found`。需要时自己补 `Source/`。**
   - 配套的**零命中自查第 0 步**：`No results found` 且用了 `file_pattern` 时，
     **先原样去掉 `file_pattern` 重搜一次**再谈别的结论。

2. **规矩 3 的三步排除法（[11] 计划 `:251-263`）扩成四步**，把「路径形状」插到最前：
   > 0. 带了 `file_pattern` 却零命中 → 先去掉 `file_pattern` 裸名宽搜。去掉后有命中 = 你的 `file_pattern` 写错了（多半缺 `Source/`），**不是内容不存在**。
   > 1.–3. 保持原样（裸名 → 拼写/大小写/第三方 → 才可说「vanilla 里没有」）。

3. **在 [11] 计划的条目对应表（`:323-324`）补一行 `[16]`**，指向新加的规矩 0 + 规矩 3 第 0 步。

4. **补一条「正元字符必须转义」**（§2.9 实测）：
   > `query` 是**正则**。想搜字面量 `pather?.StopDead()` 必须写成 `pather\?\.StopDead\(\)`；
   > 直接贴源码片段当 query 会静默零命中。

5. **不要**做的：
   - 不要在本仓库给 `search_source` 包代理/包装层（[11] 计划 `:298-299` 已否决，理由同样成立）；
   - 不要因为本条而写「别用 `search_source` 核 Verse 内部调用链」—— §2.7 #6 证明它**搜 Verse 搜得好好的**，
     板上 `:59` 写的这条「绕法」是**建立在错误诊断上的，会让正确工具被误废**。

## 6. 与 [11] 的关系（派单指定的核心问题）

**答：既不是 [11] 的新实例，也不是重复条目 —— 是「同一症状类下的第二种机制」，必须并入同一份约定文档，但不能并成同一条规则。**

逐条对照派单给的两个判据：

| | 机制 | 我的裁决 | 依据 |
|---|---|---|---|
| **[11]** | **query 形状**（写了返回类型限定 `IEnumerable` 而实际是 `IReadOnlyList`） | ✅ 属实 | [11] 报告 §1.3 |
| **[16]** | **索引覆盖**（文件不在 `search_source` 索引里） | ❌ **证伪** | §2.4：`Source/Verse/Thing.cs` 拿到 3 条命中；§2.7 #6：`Pawn_PathFollower.cs` 拿到 4 条 |
| **[16]** | **`file_pattern` 路径形状**（缺 `Source/` 前缀） | ✅ **新机制** | §2.7 七行对照表，#7 为铁证 |

⇒ 派单的二选一（query 形状 vs 索引覆盖）**两边都不对**：
[16] 的主机制是**第三种**——路径形状。它与 [11] 的 query 形状**同族不同种**（都是"你给的东西不对"），
但**触发点不同**（query 里的标识符 vs `file_pattern` 里的路径）、**修法不同**（别写返回类型 vs 必须带 `Source/` 前缀）。

### 6.1 是否合并进同一份约定文档：**应该合并**

三条理由，全部有据：

1. **[11] 计划自己已经定了单一文档政策**：`:319-321`
   「任何关于 `rimworld-source-online` 的新踩坑 … **追加到本文件对应小节**，**不要另建文件**。
   本文件是该服务的**唯一**规范文档。」
2. **同症状类**：`No results found` 的不可判读性（[11]）与本条的四义混淆（§2.10）是**同一个返回值的同一缺陷**，
   拆成两份文档，读者仍会去看错那一份。
3. **同修法、同归属**：都是外部 server、都改不动、都只能落成文档约定、都有同一份上游 P0 建议。

### 6.2 但并入的方式是「同一文档的两个小节」，不是「同一条规矩」

- [11] 的规则管 **query**；[16] 的规则管 **path**。硬合成一条会让规矩变长到无法执行。
- 建议落点：[11] 计划的**规矩 1/2/3 之前插入「规矩 0：路径必须锚定在 server 根」**，
  并把本条标记为 `[16]` 追加项 —— 这样既满足「不要另建文件」，又不掩盖两者根因不同。

## 7. 是否已被现有计划覆盖

**部分覆盖，但不是零 —— 缺口精确可指。**

`docs/issue-triage/plans/2026-10-01-11-search-source-false-negative-convention.md`（该文件在本报告写作期间由
另一子代理并发写入，**我只读、未做任何修改**）：

| [11] 计划位置 | 与本条的关系 | 覆盖? |
|---|---|---|
| `:240-242`「第一条搜索不要带 `file_pattern`；命名空间 ≠ 目录」 | 讲的是**猜不出目录**（[13]），不是**路径写法不被接受** | ❌ 不同根因 |
| `:243`「实测 `file_pattern` 接受**完整相对路径**（`Source/Verse/MapPawns.cs` 可用）」 | 只说了 `Source/` 写法**可用**，**从没说 `Source/`-less 会静默零命中** | ❌ **关键缺口** |
| `:255` 规矩 3 第 1 步「去掉一切限定 … 做**不带 `file_pattern`** 的裸名宽搜」 | 副作用上能躲开本条，但**没解释为什么**，也没覆盖「必须带 `Source/`」 | ⚠ 部分（意外的） |
| `:260`「此时才可以说 vanilla 源码里没有这个名字」 | 与本条 §2.6 结论一致 | ✅ |
| `:319-321` §5 维护规则「本文件是该服务的唯一规范文档，不要另建文件」 | **直接决定 [16] 应追加而非另立** | ✅ 落点已定 |
| `:323-324` 条目对应表 | 列了 `[11] [13] [14]`，**没有 `[16]`** | ❌ **缺口** |

`docs/mcp-source-lookup-conventions.md`（计划的目标产出）**目前尚不存在** —— 计划未执行。

⇒ **不是「已被现有计划覆盖」，也不该单独立计划。**
⇒ 建议主代理在 [11] 计划收尾时，**追加 §5.2 的 4 条**（尤其「规矩 0」与对应表补 `[16]`），
并**在计划正文补一句 [16] 的证伪结论**：索引覆盖没问题，别去修索引。

---

### 附：核实边界声明

- 全部结论基于 **20 次只读 MCP 调用**（`search_source` / `read_csharp_symbol` / `read_file` / `list_directory`）
  与仓库内**只读**文件/grep。**未**触碰任何会改变游戏状态的工具，**未**修改任何已存在的文件。
- 标注为**假设**的唯一一条：§2.7 末尾「`file_pattern` 按整仓相对路径锚定匹配」——
  四条观测一致，但该 server 源码不在本仓库，无法读到实现。
- 板上行号已按 [11] 计划 `:72-78` 的快照警告处理：引用以 `## [16]` 标题为锚，行号仅作当时参考。
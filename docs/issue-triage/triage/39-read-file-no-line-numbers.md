# [39] `read_file` 不返回行号、`start_line` 0-indexed；同服务器 `read_csharp_symbol` 却带行号

裁决：**值得修 —— 并入 [14]，不新建计划**（但必须先修正 [14] 计划里已被本条**证伪**的一节）

> 条目位置：`docs/mcp-吐槽板.md` 第 **58–91** 行（标题锚点 `## [39]`，2026-10-03 写入；文件快照总行数 **1685**，
> `LastWriteTime = 2026-10-03 01:05:25`）。索引行在第 **53** 行。
> 本报告所有行号均经 `[System.IO.File]::ReadAllLines(...)` 复核。

---

## 1. 一句话结论

板上说的**两件事都属实**，而且第一件比板上写的更有价值：`read_csharp_symbol` 确实已经输出
`// File: <path> (Lines N-M)` 头 —— 这**证伪了 [14] 号计划里 `### 4.2 替代路径：没有` 一节**
（`2026-10-01-14-…md:202`），即"核行号没有替代路径"这句话今天已经不成立。
因此 [39] 不能只写成"[14] 的注脚"，它要求**改 [14] 计划已定稿的正文**。

---

## 2. 核实过程（5 组只读调用 + 本机形态核查，全部本会话亲测）

### 2.1 探针 1：`read_csharp_symbol` 确实带头（板上 ① 的前半）

```
read_csharp_symbol{ typeName:"Pawn", memberName:"GetPathContext" }
→ // File: Verse/Pawn.cs (Lines 5348-5359)
  public PathingContext GetPathContext(Pathing pathing) { … }
```

**✅ 逐字属实**，与板上 L64 抄录的完全一致。

### 2.2 探针 2：`read_file` 确实一行号都没有（板上 ① 的后半）

```
read_file{ path:"Source/Verse/AI/Pathing.cs", start_line:38, line_count:58 }
→ public PathingContext FenceBlocked => fenceBlocked;
  ( …58 行纯文本… )
  [TRUNCATED] Showing 58/172 lines.
  (Tip: Continue reading using `start_line`: 96)
```

**✅ 属实**：正文 58 行**无任何行号前缀**，页脚**只回显了总行数 172 和 tip 96，没有回显实际起始行**。
板上 L70「既没有行号、也没有起始行回显」**成立**。

补充一条板上没提的：**页脚给出了总行数（`172`）**。这是唯一的自校验素材 —— 能确认文件长度，但确认不了起点。

### 2.3 探针 3：用 1-based 真值源校准，确认基线未漂移

```
search_source{ query:"PathingContext (Flying|FenceBlocked) =>", regex:true,
               file_pattern:"Source/Verse/AI/Pathing.cs" }
→ 39:	public PathingContext FenceBlocked => fenceBlocked;
   41:	public PathingContext Flying => flying;
```

三条事实同时被钉死：

1. **板上 L74 用的那条校准锚点 `Pathing.cs:41 = public PathingContext Flying => flying;` —— 逐字正确。**
2. **`read_file` 本次没有发生"空行吞行"右移**：`start_line=38` 的首行是 `FenceBlocked`，
   而它正是 1-based **39** ⇒ 再次确认 `首行1based = start_line + 1`（[14] 计划 §0.3 规则 2 仍然成立，未漂移）。
3. **tip = `start_line + line_count` = 38 + 58 = 96**，与 [14] 计划 §0.3 规则 5 完全吻合。

### 2.4 探针 4：`read_csharp_symbol` 不带 `memberName` 时也带头

```
read_csharp_symbol{ typeName:"Pathing" }
→ // File: Verse/AI/Pathing.cs (Lines 7-171)
  public class Pathing : IDisposable { …全类… }
```

⇒ 该工具的 `Lines N-M` 头**对「类」和「方法」两种粒度都输出**，说明它背后是一个通用的
「符号 → span」格式化器，不是 `GetPathContext` 一处的硬编码。**这一点加强了板上"能力已在同服内"的可信度。**

### 2.5 本机形态核查（核实转述，不采信）

**转述称**：`rimworld-source-online` 是「Node/Python 侧的独立服务，不是游戏程序集 ⇒ decompilerserver 对它无效」。
**我的核实结论：前半句未能证实，后半句成立。**

| 核查项 | 方法与范围 | 结果 |
|---|---|---|
| 实现是否在本仓库 | 对 `Source/` `MCP/` `McpRimDebug/` 等目录递归 `Select-String -SimpleMatch 'rimworld-source-online','read_file','start_line'`，**仅限文本扩展名**（`.cs/.xml/.json/.md/.txt/.csproj/.props/.targets/.yml/.yaml/.ps1/.js/.ts/.py/.log/.shader`） | **`Source/` = 0 命中，`MCP/` = 0 命中** |
| ⚠️ 上面这条差点得出错误结论 | 同一次搜索**不限扩展名** | 报 3 处命中 —— 逐一定位后发现**全部是二进制文件里的字节巧合**：`Source/UE-4.9.0-official/…/lib/ILRepack.exe`、`…/Runtime/Mono.Cecil.dll`、`Source/UELoader/lib/0Harmony.dll`。**ripgrep 视角（默认跳二进制）为 0 命中，与文本视角一致。** |
| 它是不是本机的 .NET 程序集 | `decompilerserver.list_contexts`（**本次显式核对了全量 18 个已加载上下文 + 107 个已注册别名**） | 全部是 `Assembly-CSharp.dll` / `Lib.GAB.dll` / `0Harmony.dll` / `RimBridgeServer.dll` / `UnityEngine.CoreModule.dll` / `UELoader.dll` / `TEXT20.Unity.dll` —— **没有任何一个是 `rimworld-source-online`** ⇒ decompilerserver **确实无法**指向它 |
| 它是不是 `RimSearcher`（本机 PATH 上确实有个 .NET CLI） | 对 `C:\SteamLibrary\steamapps\common\RimWorld\RimSearcher-master\Sources` 递归 grep `read_file\|read_csharp_symbol\|start_line\|// File:` | **0 命中**。其 `Commands/` 只有 `DefCommands / FieldCommands / MaintenanceCommands / SearchCommands / StatisticsCommands`，`Queries/` 是 SQLite 仓储 ⇒ **`RimSearcher.Cli` 不实现这六个工具**，是误认 |
| 它服务的源文件在不在本机 | 对 `C:\SteamLibrary\steamapps\common\RimWorld` 与 `C:\RimSearcher` 递归找 `Pathing.cs` | **一个都没有** ⇒ 那份 vanilla `.cs` 语料不在本机 |
| MCP 服务器定义文件 | 搜 `~/.dsh\settings.yaml`（`rimworld-source-online` **0 命中**）、`~/.dsh\data`、`%APPDATA%\DeepSeek`（**目录不存在**）、`%LOCALAPPDATA%\DeepSeek`（**不存在**）、`C:\RimSearcher\decompiler-server`、本仓库 `**/*mcp*.{json,yaml,yml,toml}`（**0 文件**） | **未找到其注册定义** ⇒ 无法判定其技术栈是 Node / Python / 还是远端 HTTP 服务 |

**⇒ 结论（精确版）**：**「改不了」成立，「Node/Python」未能证实。**
本仓库无代码可改 ✅；本机找不到其实现 ✅；decompilerserver 点不到它 ✅（18 个上下文逐个核过）。
但"它是 Node/Python 服务"这一点**我没有证据**，不应写进任何交付物。

> 💡 顺带实测到 `AGENT-BRIEF §3.5` 那个坑**此刻正在发生**：`list_contexts` 返回
> `"currentContextAlias":"gab"`（`Lib.GAB.dll`，仅 94 类型），而 `vanilla` 是 `Assembly-CSharp.dll`（16158 类型）。
> 若此刻有人不带 `contextAlias` 去 `search_symbols`，拿到的会是 Pathos/GAB 的结果 —— **与"符号不存在"逐字同形**。
> 本报告没有依赖任何不带 alias 的零命中。

---

## 3. 与 [14] 的关系：已覆盖什么、缺口在哪

### 3.1 文档那一半：**已覆盖**

[14] 号计划（`2026-10-01-14-read-file-line-number-convention.md`，**460 行**）的交付正文
`## 4. 行号基线`（该文件第 **178** 行起的 `## 3. 交付正文` 段）中已经写死了：

- 第 **73** 行：`read_file` 的返回不带任何行号前缀（6 次调用全部如此）
- §`## 4.1` 规则 1/4：窗口首行空行被吞 ⇒ **返回值无法反推起点行号**
- 第 **270** 行的计划必带块：`read_file` 的 `start_line` 是 **0-indexed**（`= 目标1based行号 − 1`），返回**不带行号**

⇒ **"返回不带行号"这个事实本身，[14] 计划一个字不差地已经写了。本条在这半边没有新增事实。**

### 3.2 代码那一半：**明确未覆盖 —— 而且是被 [14] 计划"点名排除"的**

[14] 计划第 **432** 行 `## 7. 边界声明` 里写着（第 **437** 行）：

> **不给外部 MCP server 开 issue、不写「返回值加行号前缀」的代码方案。**
> 裁决报告 §5.1 推荐的方案 A（加行号前缀，非破坏）**属于 server 维护者的活，本仓库无从落地**

⇒ **缺口就在这里**：[14] 计划把「给 `read_file` 加行号」整件事**判为 out-of-scope 并封存**了。
[39] 正是冲着这个封存去的。

### 3.3 [39] 的真实增量：三条，**不是一条**

板上说的增量是「能力已在仓内」（L82–83）。逐条核实后，增量应拆成：

| # | 增量 | 证据 | [14] 计划是否已含 |
|---|---|---|---|
| **I1** | 同服务器存在一个**已经输出 `Lines N-M` 头**的工具，且对「类」和「方法」两种粒度都输出 | 探针 1、探针 4 | ❌ 无。计划 §4.2 甚至写了**相反**的话 |
| **I2** | 同服两个读源码工具**行号契约不一致**（`search_source` 1-based / `read_file` 0-indexed 且无输出 / `read_csharp_symbol` 1-based 且带头） | 探针 1/2/3/4 交叉 | ❌ 无。计划只覆盖 `search_source`↔`read_file` 一对，没把 `read_csharp_symbol` 纳入 |
| **I3** | **一条计划里写错的断言**：`### 4.2 替代路径：没有`（计划文件第 **202** 行）**已被证伪** —— 类型成员这条路 `read_csharp_symbol` 是通的 | 探针 1、探针 4 | ❌ 无，且**必须修正** |

**归并锚点：`[14]` 号计划 · §3 交付正文 · `### 4.2 替代路径：没有`（`2026-10-01-14-read-file-line-number-convention.md:202`）。**

---

## 4. [39] 的新事实把修复成本降到多少？—— **降了一半，没降到"本仓库能修"**

板上 L87 说「最小改法：把 `read_csharp_symbol` 已有的那行格式化逻辑复用过去即可」。
这句话**对了一半，必须拆开说**，否则会得出错误的成本估计。

### 4.1 确实下降的部分（**推动力层面，质变**）

- 核实前：[14] 计划的定性是「**外部仓库的 TODO**，需要对方从零新建能力」⇒ 推动力 ≈ 0。
- 核实后：**同一台服务器里已经在生产环境输出这个头了**（`read_csharp_symbol`，探针 1/4），
  且对类/方法两种粒度通用 ⇒ 它是一个**已发布的、复用的格式化器**，不是某处硬编码。
- ⇒ **性质从"提新需求"变成"请复用你自己已经有的那行"**。这是 [39] 最值钱的地方。

### 4.2 没有下降的部分

| 层面 | 判断 | 依据 |
|---|---|---|
| **本仓库可动性** | ❌ **完全没变，仍然 0 代码可改** | §2.5：源码不在本仓库、本机也找不到其实现；18 个 decompilerserver 上下文逐个核过，没有它 |
| **格式化模板** | ✅ 已在同服内可直接复用 | 探针 1/4 |
| **行号真值计算** | ⚠️ **不是同一件事** | `read_csharp_symbol` 是「**符号 → 区间**」：先定位符号，span 天然已知。`read_file` 是「**窗口 → 区间**」：必须处理 `start_line+1` 的换算**以及空行吞行造成的右移**。**两者只共享格式，不共享计算。** |

### 4.3 ⚠️ 板上没发现的坑：**照板上建议直接加头，新头会自己说谎**

这是本条最值得写进规范的一条增量。

如果按板上 L85–87「给 `read_file` 的返回加一行 `// File: <path> (Lines <start>-<end>)`」实现，
而**没有同时修掉 [14] 已实测的空行吞行**（计划 §0.3 规则 4），那么：

- 传 `start_line=162`，实际返回的是 1-based **164** 起的行（[14] 探针 D 实测）；
- 而头会按 `start_line+1` 老老实实印出 `Lines 163-165`。

⇒ **返回一个语法正确、看起来权威、实际整体错 1 行的行号区间。**
这比现在"没有行号"**更危险**：现在调用方知道要自己去 `search_source` 校；加了头之后没人会再去校。

**⇒ 正确顺序是：先修吞行，再加头；或者让头从「实际返回的切片」反算，而不是从 `start_line` 算。**

### 4.4 一句话回答

**修复成本从「要在外部服务器新建能力」降到「复用已有格式化逻辑 + 补一个窗口→区间的换算」——
但仍不是本仓库能落地的活。** 准确表述是：
**"格式化能力已在同服内；行号计算能力仍不在。"** 板上「能力已在仓内」这句话**说过了头**。

---

## 5. 严重度复评：**维持 🟠，不升 🔴也不降 🟡**，但板的论证要改

### 5.1 板上 L75 那句要修正

> 板上：「**没有已知锚点时，任何「核某行是不是 X」的判断都是猜。**」

**这句过强。** 存在一个**不需要任何外部锚点**的判别法 —— 就是 [14] 计划探针 C/D 的做法
（`2026-10-01-14-…md:64-65`）：

> 同一个窗口用 `start_line=N` 与 `start_line=N+1` 各读一次。
> **两次返回逐字相同 ⇒ 发生了空行吞行右移**；不同 ⇒ 没发生。

所以：
- **能不能判？能**，代价是**多花 1 次调用**。
- **能不能自校验？不能** —— 单看一次 `read_file` 的返回值，永远无法区分两种情况。
  （本次探针 2 就是"没右移"的干净例子：首行 = 39 = `38+1`。）

⇒ 板上 L70「无法知道拿到的第 N 行对应源文件第几行」**成立**；L75「是猜」**不成立**，应改为
**「单次调用无法自校验，必须再花一次调用做 A/B 对拍才能确定偏移」**。

### 5.2 维持 🟠 的理由

- **不降 🟡**：它是**默认入口工具**、**静默**、且**高频**（本项目每个交付都要核行号）。
  一次误判就会写出"注释行号对不上"的**事实相反**结论，而真相是代码没问题、工具读错了行。
- **不升 🔴**：**可检出**。两条检出通道都存在且都便宜 ——
  ① `search_source` 给 1-based 真值；② A/B 对拍判定是否右移。
  🔴 的定义是"不可检出的假成功/假阴性"，本条不满足。
- **⚠️ 附带的升级触发条件（建议写进规范）**：
  若某个交付物里的行号**没有经过 `search_source` 交叉核对**，那么**那一个交付物**的行号应按 🔴 对待
  ——不是本条升 🔴，而是该产物失去自证能力。这是**逐档**判定，不是整体判定。

### 5.3 [39] 相对 [14] 唯一真正加重的地方

不是严重度，是**同一服务器内部契约不一致**：`search_source`（1-based 无头）、
`read_file`（0-indexed 无头且吞行）、`read_csharp_symbol`（1-based **有头**）
—— **三个工具三套口径**。这在设计上是缺陷，不只是"少个功能"。
**这正是本条值得独立留档、而不只是当 [14] 脚注的理由。**

---

## 6. 修的话怎么修（具体落点）

### 6.1 本仓库内：**今天就能做，且必须做** —— 改 [14] 计划已定稿的正文

落点：`docs/issue-triage/plans/2026-10-01-14-read-file-line-number-convention.md:178` 起的 `## 3. 交付正文` 围栏块。

> ⚠️ **本报告不改它**（子代理禁改既有文件）。以下是给主代理 / PLAN 子代理的逐条改法。

| # | 位置 | 动作 |
|---|---|---|
| **M1** | `### 4.2 替代路径：没有`（同文件 **202** 行） | **重命名并改写**为 `### 4.2 替代路径：类型成员走 read_csharp_symbol`。正文写：`read_csharp_symbol{ typeName, memberName }` 返回 `// File: <path> (Lines N-M)` 头（1-based）；**不带 `memberName` 时给整个类型的 span**。实测：`Pathing.GetPathContext` → `Verse/Pawn.cs (Lines 5348-5359)`；`Pathing` → `Verse/AI/Pathing.cs (Lines 7-171)`。**核「某个方法/类的起止行」时优先用它，一次调用同时拿到正文和可引用区间。** |
| **M2** | 紧接 M1 之后 | **加一条限制**（防止被误当万能）：`read_csharp_symbol` 是**正向**定位 —— 你必须**已经知道类型名与成员名**；它**回答不了「第 243 行是不是 X」**这类**反向**问题。方向相反，不可替代 `search_source`。 |
| **M3** | `## 4.1 四条规则` 之后 | **加规则 5（新增，板上没有）**：单次 `read_file` 的返回值**无法自校验**是否发生了空行吞行；**判定方法是对同一窗口做 `start_line=N` / `N+1` 的 A/B 对拍，返回逐字相同即发生了右移。** |
| **M4** | `## 4.5 本节的实测记录` | **追加探针 G/H/I**（= 本报告 §2.1–2.4 的四次真实调用与真实返回），特别是 `Pathing.cs:39/41` 这对锚点，它同时锚住了 `read_file` 基线与 `read_csharp_symbol` 头格式。 |
| **M5** | §7 边界声明（**437** 行） | 保留"不给外部 server 写代码方案"的结论（§2.5 已证本仓库确实无从落地），**但补一句**：该能力在同服内**已存在**（`read_csharp_symbol`），⇒ 这不是"外部仓库的 TODO"，而是**可低成本促成的复用**，值得单独留档跟踪。 |

> 🔒 **前置依赖不变**：`docs/mcp-source-lookup-conventions.md` **此刻仍不存在**
> （`Test-Path` 本会话实测 = `False`），是 [11] 号计划任务 1 的产物。
> [14] 计划第 **42** 行已写明「返回 `False` 则**停止并报告被 [11] 阻塞**，不许自建覆盖」——
> **M1–M5 同受此约束**，现在还不能落地写文档。

### 6.2 上游 server 代码（本仓库做不了，但应作为 issue 内容留给 server 维护者）

**顺序很重要 —— 先 1 再 2，反了会造出"看起来权威但是错的"行号头：**

1. **先修空行吞行**（`start_line` 命中空行时被静默丢弃、窗口整体右移、行数不减）。
   不修这步，第 2 步加的头会稳定地整体错 1 行 —— 见 §4.3。
2. **再加头**，复用 `read_csharp_symbol` 的现有模板，逐字同形：
   `// File: <path> (Lines <start>-<end>)`，`<start>`/`<end>` 取 **1-based**。
3. **头必须从「实际返回的切片」反算**，不能从 `start_line` 正算 —— 这样即便第 1 步没做，
   头也只会说"我不知道起点"而不会说错。
4. **建议做成 opt-in 开关**，参数名沿用同服 `get_source_slice` 已有的 `include_line_numbers`
   （保持一个 MCP 服务器内参数命名一致），默认关闭 ⇒ **对既有调用方零破坏**，不重蹈方案 B 的覆辙。
5. **板上 L88 的第二条建议（把 0-indexed 写进返回）已并入第 2 步** —— 头里用 1-based 印出来，
   等于同时消灭了"参数是 0-indexed 但返回不给基准"这个歧源。

---

## 7. 边界声明

- **吐槽板本体 `[39]` 的状态字段与索引行，本报告不改** —— 按 `docs/issue-triage/README.md:19`，追加
  `> 🤖 **Triage 批注**` 是主代理的职责。
  建议批注要点见 §5.1（板上 L75 过强）、§4.4（板上 L87「能力已在仓内」说过头）、§4.3（加头前必须先修吞行）。
- **`triage/14-…` 与 `plans/2026-10-01-14-…` 本报告不改**，§6.1 的 M1–M5 是改法建议，不是已执行的改动。
- **不新建计划**。[39] 产出形态是「对 [14] 计划正文的 5 处修正」，不是一份独立实现计划 ——
  独立成计划会造出第二份互相不知情的行号规范，正是 [14] 计划 §1 花了整节论证要避免的事。
- **本次全程只读**：未改任何既有文件、未编译、未启动游戏、未调用任何改变游戏状态的 MCP 工具。
  所用 MCP 工具仅 `rimworld-source-online` 的 2 个只读读文件工具（4 次调用）与
  `decompilerserver.list_contexts`（1 次，只读列举，未做任何 alias 依赖的检索）。
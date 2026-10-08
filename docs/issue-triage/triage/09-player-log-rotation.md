裁决：值得修

RimWorld 每次启动把上一局的 `Player.log` 轮转成 `Player-prev.log` 是 Unity/上游行为、本项目代码零参与，但 `read_rimworld_log` 只回一个不带 mtime 的裸字符串，调用方无法判断「这次 0 命中可不可信」——吐槽板点名的**假阴性风险是真的**，修法成本极低。

## 核实过程

### 1. 吐槽板原文（`docs/mcp-吐槽板.md:437-455`）

断言：每次启动都轮转；没有任何工具暴露这一点；`read_rimworld_log` 只读当前；**冷重启后 grep 新 `Player.log` 报「0 条 Harmony 告警」很可能是假阴性**。逐条核实。

### 2. 轮转确实发生（磁盘实测 + 日志交叉印证）

只读列举 `C:\Users\Timer_0\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\`：

```
Name              Length  LastWriteTime
----              ------  -------------
Player.log        1053806  2026/10/1 17:53:50
Player-prev.log   1005570  2026/10/1 17:43:06
```

`Player-prev.log` 的最后写入时间 **17:43:06**，与 MCP 日志 `MCP/logs/mcp-20261001-093829.log:938` 的

```
[17:43:05.141] <WARN> [GabpClient] GABP 连接断开: connection lost
[17:43:05.141] <INFO> GABP 断开（reason=connection lost）；下次工具调用将惰性重连
```

相差 1 秒——即**游戏进程在此刻退出/重启**，旧 `Player.log` 被改名归档为 `Player-prev.log`。同日志第 965 行 `17:46:23.998 GABP 握手成功` 确认新会话在重启后正常起来了。**轮转是真实、每次启动都发生的行为，吐槽板属实。**

### 3. 轮转不来自本项目代码，也不来自 RimWorld C# 代码

- 全仓库（`*.cs` / `*.js` / `*.json`）检索 `Player-prev`：**零命中**。本项目从不读、从不写这个文件。
- 在 vanilla RimWorld 源码里检索 `Player-prev` / `LogRotator` / `previous log`：**零命中**。RimWorld 的 C# 代码里没有任何轮转逻辑。
- 本项目对 `Player.log` 的全部使用都是**只读 + 只读当前文件**：`MCP/config.json` 里 `"logPath":".../Player.log"` 是唯一路径配置；`MCP/index.js:70` 只做 `%USERNAME%` 展开。

结论：轮转发生在 Unity 引擎层的 player-log 管理，**本项目与 vanilla C# 都不是责任方**。（Unity 引擎源码不在本仓库范围内，此处结论基于「本仓库与 vanilla 源码均无相关代码」这一事实推断，标注为**推断**，非直接读到引擎源码。）

### 4. `read_rimworld_log` 的返回值确实不足以判断可信度

工具定义 `MCP/index.js:1904-1917`：入参只有 `lines`（默认 100），描述为「读取 RimWorld 游戏日志文件（Player.log）末尾内容」。**没有 `source` / 没有 `path` 之外的任何元信息参数。**

实现 `MCP/index.js:1373-1399`：

```js
async function readLog(lines = 100) {
  if (!fs.existsSync(config.logPath)) { return { success: false, message: `Log file not found: ${config.logPath}` }; }
  const content = fs.readFileSync(config.logPath, 'utf-8');
  const allLines = content.split('\n');
  const lastLines = allLines.slice(-lines).join('\n');
  return {
    success: true,
    content: lastLines,
    totalLines: allLines.length,
    returnedLines: Math.min(lines, allLines.length),
    logPath: config.logPath
  };
}
```

返回 `success` / `content` / `totalLines` / `returnedLines` / `logPath`。**`read_rimworld_log` 没有 `mtime`、没有文件大小、没有「这份日志对应哪一次游戏会话」的标识。** 对比同文件的 `tailLog`（`MCP/index.js:1402-1428`）反而**有** `fileSize` 和 `lastModified`——即 `tail_rimworld_log` 能拿到 mtime，`read_rimworld_log` 拿不到。**同一对孪生工具的元信息口径不一致**，这是吐槽板没点出、但实打实存在的一个细节缺陷。

### 5. 假阴性的成因链是完整的

冷重启游戏 → `Player.log` 被清空重建为本次会话 → 本次会话刚跑起来、还没产生任何 Harmony 告警 → `read_rimworld_log{lines:100}` 返回 `success:true`、`totalLines` 很小、内容里 0 条命中 → 调用方读成「没有 Harmony 告警」。

链条上**每一环都无法被返回值打断**：`success` 是 true（文件确实存在）、`logPath` 是一个绝对路径（看不出世代）、`totalLines` 小恰恰是新日志的特征但工具没给任何解释。**吐槽板说的「制造假阴性」成立。**

需要说明的边界：`read_rimworld_log` **本身不提供 grep 能力**，它只返回文本。吐槽板里「grep 新 Player.log」这一步是调用方（人或 AI）用 read 工具自己做的。所以本条要修的不是「grep 错了」，而是「工具没给判断依据」——这与吐槽板的建议（加 `source` 参数 + 返回 `path`/`lineCount`/`lastWriteTime`）完全对得上。

### 6. 是否已有 workaround

有一条**没用在本项目工具里**的路子：`McpRimDebug` 侧会解析 `Player.log` 找调试端口（`McpRimDebug/DebugSession.cs:1525-1529`「查找策略：读头部 4KB」），说明代码库对「日志是本次会话的」这件事**并非全无认知**。但那是为端口发现服务的局部逻辑，没有做成通用的日志元信息能力，也没有暴露到 MCP 工具层。**吐槽板说的「没有任何工具暴露这一点」属实。**

## 影响与严重度复评

- **复评严重度：🟡 低 → 建议维持 🟡，但明确标注它有 🔴 的「假阴性」内核。**

  按 AGENT-BRIEF §5.2，🔴 的定义是「会让自动化得出事实相反的结论（假成功 / 假阴性）」。本条**确实命中假阴性**：0 命中 ≠ 没有告警。但与 🔴 条目相比有两点减分：

  1. 假阴性需要**跨一次冷重启 + 调用方主动做 grep**才成立，是特定工作流下的陷阱，不是每次调用都踩；
  2. 仓库里已有的实践已经在**部分规避**它——吐槽板 [02]（`Verse.Log.*` 不落 Player.log）和 [15]（`Log.ErrorOnce` 被 `Error_Patch2` 吞消息）两条都记录了「日志不可靠」的判据，维护者在实际使用中是有警觉的。

  **净裁决：保留 🟡，但把「假阴性」写进条目说明，避免它按 🟡 被长期搁置。** 修法便宜（见下），成本收益比不支持把它推上 🔴 队列。

- **不会产生假成功。** `success:true` 只表示「文件读到了」，语义诚实；`Log file not found` 分支（`MCP/index.js:1375-1380`）也返回了明确 `message`。

## 责任归属

**本项目（MCP 侧日志读取工具的返回值设计）。**

- **轮转行为本身**：Unity 引擎层，**不是本项目的锅**，也不是 vanilla C# 的锅。理由见第 3 节的两处零命中检索。本项目**不应该、也无法**去改轮转行为。
- **「没暴露轮转」这件事**：本项目的锅。`readLog` 手里**已经有** `fs`（同一个函数上一行就在用 `fs.existsSync` / `fs.readFileSync`），多调一次 `fs.statSync` 的成本是零，却能让返回值带上世代信息。同文件里的 `tailLog` 已经这么干了（`MCP/index.js:1411` `fs.statSync`），说明这是**已确立的内部惯例被漏掉**，不是设计决策。
- 需要改 vanilla 吗？**不需要**。需要改第三方吗？**不需要**。

## 修的话怎么修

全部落在 `MCP/index.js` 与 `MCP/config.json`，不动任何游戏侧代码：

1. **补元信息（必做，最低成本）**
   - `MCP/index.js:1386-1392`（`readLog` 返回体）加三个字段：`lastWriteTime: stats.mtime.toISOString()`、`fileSize: stats.size`、`path: config.logPath`（现在叫 `logPath`，建议**保留旧键**以免破坏既有调用方，同时补 `path` 别名或直接沿用 `logPath` 并加说明）。
   - 改法照抄同文件 `tailLog`（`MCP/index.js:1411` 的 `fs.statSync`）即可，两行以内。
   - 顺手在 `totalLines` 旁加 `note`：内容为「本次游戏会话的日志；上一会话已轮转至 Player-prev.log」。

2. **加 `source` 参数（推荐，落地吐槽板的具体建议）**
   - 改 `MCP/index.js:1904-1917` 的 `inputSchema`，加 `source: { type: 'string', enum: ['current','prev','all'], default: 'current' }`。
   - 改 `readLog(lines, source)`（`MCP/index.js:1373`）：由 `config.logPath` 推导 `prevPath = config.logPath.replace(/\.log$/, '-prev.log')`；`prev` 读 prev，`all` 读两个并各自带上 `path` / `lineCount` / `lastWriteTime`，**分开返回不合并**——合并会制造一个不存在的「单次会话」视图，比假阴性更危险。
   - `prev` 文件不存在时返回明确的 `success:false, message` + `hint`，**不要**静默回退到 current（静默回退等于又造一次假阴性）。

3. **补上工具描述（必做，一行）**
   - `MCP/index.js:1906` 与 `MCP/index.js:1920` 的 description 补「（每次游戏启动会把上一会话轮转为 Player-prev.log；本工具默认只读本次会话）」。
   - 成本最低、覆盖最广：即使调用方只读了工具描述而没看返回结构，也能知道有这回事。

4. **统一 `read_rimworld_log` / `tail_rimworld_log` 的口径（建议）**
   - 目前一个返回 `logPath` 不返回 mtime、另一个返回 `lastModified` 不返回 path（`MCP/index.js:1386-1392` vs `:1415-1421`）。统一成同一套字段集（`path` / `lineCount` / `lastWriteTime` / `fileSize`），否则调用方还得记「哪个工具有哪个字段」。

5. **明确不做**：在工具内部加 `grep` / `contains` 过滤参数。这会与吐槽板 [03]、[11]（大结果集冲爆上下文）撞车，也会让「0 命中」这个最危险的输出形态更难被察觉。**过滤留在调用方，工具只负责把元信息给全。**

## 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/` 下只有 `2026-09-27-ue-csharp-console-roslyn-upgrade.md`（2129 行，12 任务）。全文检索 `Player-prev` / `Player.log` 轮转 / `read_rimworld_log`：仅在任务 8 与执行说明里出现「冷启动时延：重启游戏，进图后读日志」这类**验收步骤的顺带提及**（第 2035 行），以及任务 11 的日志核对；**没有任何针对 `read_rimworld_log` / `tail_rimworld_log` 返回结构的改动任务**。
- 任务 12「MCP 文案与端到端验收」步骤 1（第 2057-2063 行）只改 `MCP/index.js:2937` 的 `execute_csharp_code` notes，**不涉及这两个日志工具**。
- `docs/issue-triage/plans/` 目录当前为空，无重复计划。

## 与 [07] 的同组关系（父任务点名要判）

**同一个设计模式，但不是同一个根因，不建议合并成同一个任务。**

- 共同模式：**工具返回值缺少「可信度元数据」，导致调用方无法自行判断「这个空结果 / 不可用状态可不可信」。**
  - [09] 缺的是**数据源世代**（这份日志属于哪一次会话）。
  - [07] 缺的是**提供方就绪度**（这个名字当前调不调得动）。
- 不同根因：`readLog` 是文件 IO 层的问题，改 `MCP/index.js:1373-1428` 两个函数；`mcp_help` 是元工具分发逻辑的问题，改 `MCP/index.js:3646-3671`。**没有任何一处改动能同时覆盖两者。**
- 建议：作为同一个「返回值元信息」主题在同一个发布窗口落地（两条都只动 `MCP/index.js` 的工具层），但保持为两个独立计划任务、各自独立验收。
# [30] `start_game` 的 `waitForNotification` 默认值 —— 裁决报告

裁决：已被现有计划覆盖

> 冲突裁定：**计划 04 对**（保留默认值 `true` 不改），但它的三条「破坏性」理由中只有 **1 条成立**（返回体丢 `state`）、**1 条不成立**（`ensureConnected` 时机）、**1 条成立但只是文案**（`:370`）。
> [30] 的 6 轮实测**不足以推翻**计划 04 —— 它测的是「不超时」，而破坏性来自「返回体语义变了」，这两件事不在同一条轴上。

- **条目**：`docs/mcp-吐槽板.md` → `## [30]`（用 `Select-String -Pattern '^## \[30\]'` 定位，未用行号）
- **裁定对象**：`docs/issue-triage/plans/2026-10-01-04-startup-call-timeout-disambiguation.md`
- **核实方式**：纯静态阅读。**未启动游戏、未调用任何会动游戏状态的 MCP 工具、未编译、未改动任何既有文件。**

---

## 1. 条目原文摘要

`## [30] start_game 的 waitForNotification 默认值会把「游戏已成功启动」伪装成「调用失败」🟠`

| 项 | 内容 |
|---|---|
| ① 症状 | `start_game{timeout:240000, waitForNotification:true}` 6/6 轮稳定返回 `MCP error -32001: Request timed out`，每轮浪费 3~4 分钟；随后 `get_game_status` 显示游戏其实已起来 |
| ① 附带 | `start_quick_test{timeout:30000}` 也超时，随后 `get_game_status` 又变成 `IN_GAME` |
| ② 复现 | `stop_game{force:true}` → `start_game{waitForNotification:true}` ⇒ 必超时 |
| ③ 影响 | 🟠 中。**条目自称「不产生错误结论，但每一轮白等 3~4 分钟」**；并称「更要紧的是它把成功伪装成失败」 |
| ④ 建议 | ①把默认值改成 `false`；②「或至少在超时分支里回一句『游戏可能已启动，请用 get_game_status 复核』」 |
| ④ 附带 | `start_quick_test` 的 `timeout` 实测**要 ≥ 240000** |
| ⑤ 状态 | `已绕过` |

**条目内部已存在的张力**：④ 的建议是二选一（改默认值 **或** 改超时文案）。计划 04 恰好选了后者。这本身就是「非互斥」的第一证据。

---

## 2. 核实（源码复核）

全部行号用 `[System.IO.File]::ReadAllLines('MCP/index.js')` 逐行复核（文件共 **5460** 行），未采信 `read` 工具的行号输出。

### 2.1 默认值写在三处，生效的是第 2、3 处 —— **计划 04 §0.4 完全正确**

| 位置 | 实际内容（逐字） |
|---|---|
| `MCP/index.js:1839` | `default: true`（在 `:1836-1840` 的 `waitForNotification` schema 内） |
| `MCP/index.js:1838` | `description: 'Wait for main menu notification from mod (default true)'` |
| `MCP/index.js:4358` | `const result = await startGame(args?.useSteam ?? true, args?.waitForNotification ?? true, timeout);` |
| `MCP/index.js:942` | `async function startGame(useSteam = true, waitForNotification = true, timeout = config.startTimeout \|\| 180000) {` |

⇒ **只改 schema 的 `default:false` 是彻底的空操作。** [30] 的建议若按字面理解（改 schema 默认值）落地，不会产生任何可观测变化。这一点计划 04 判断无误，[30] 未识别。

### 2.2 SDK 默认请求截止线 —— 常量属实，但**不是本机实际生效的截止线**

- `MCP/node_modules/@modelcontextprotocol/sdk/dist/esm/shared/protocol.js:8` → `export const DEFAULT_REQUEST_TIMEOUT_MSEC = 60000;`（已逐行复核，该文件共 1107 行）。
- **但 [30] 实测是「每次浪费 3~4 分钟」才拿到 `-32001`**，而服务端预算是 `timeout:240000`（4 分钟）。若客户端截止线真是 60s，`-32001` 应在 **60 秒**出现。二者不可能同时成立 ⇒ **本机 harness 客户端的实际截止线 ≫ 60s**（数量级在 180~240s）。
- ⇒ 计划 04 §0.1 表格第 1 行把它写成「MCP 客户端有独立请求截止线，默认 60s」，作为**根因**成立；作为**数值**不成立。这是措辞风险，不是结论错误。

> **搜索范围声明（未找到即不可断言）**：我在 `C:\Users\Timer_0\AppData\Roaming\Code\User\mcp.json`、`C:\Users\Timer_0\AppData\Roaming\Trae CN\User\mcp.json`、以及 `C:\Users\Timer_0\.dsh\` 下 `-Depth 1` 的 `*.json` 中查找过任何客户端 `timeout` / `requestTimeout` 配置；另在 `C:\SteamLibrary\steamapps\common\RimWorld\Mods\` 下 `-Recurse -Depth 3` 查找 `*.mcp.json` / `mcp.json`（结果为空）。**这三个范围之外是否还有 harness 侧配置，本次未覆盖，不作「不存在」断言。** harness 客户端本体不在本仓库与用户目录内，属本次权限边界外。

### 2.3 阻塞路径的真实耗时来源

- `MCP/index.js:1099` → `while (Date.now() - startTime < timeout)` —— 服务端**无自限**，上界就是调用方给的 `timeout`。
- `MCP/index.js:1595`（`startQuickTest` 内）→ `while (Date.now() - startTime < timeout)` —— 同构。
- 循环退出后各自还要再跑一次 `detectGameStage()`（`MCP/index.js:1159` / `MCP/index.js:1648`）⇒ 失败路径总耗时 ≈ `timeout` + 一次探测。
- `MCP/config.json` 实测内容（全文）：`{"gamePath":".../RimWorldWin64.exe","logPath":".../Player.log","steamPath":"d:/steam/steam.exe","appId":"294100","startTimeout":180000}` ⇒ **默认 `timeout` 是 180000（3 分钟）**，不是仅 fallback。

⇒ [30] 用 240000 的预算实测 6/6 超时，说明**阻塞路径在本机几乎必然跑满预算**。这一条 [30] 说得对，且比计划 04 的「可能超时」更强 —— 但它同样**不构成改默认值的理由**。

---

## 3. 正面冲突裁定：计划 04 vs 吐槽板 [30]

### 3.1 谁对？—— **计划 04 对**，但其论证需要一处修正

| 维度 | [30] | 计划 04 | 裁定 |
|---|---|---|---|
| 默认值写在 schema `default:true` | 隐含按此理解 | 「改 schema 无效，生效值在 `:4358` / `:942`」 | **计划 04 对**（§2.1 已复核） |
| 「改默认值是破坏性变更」 | 未论证，直接建议改 | §0.5 论证 | **计划 04 对**（详见 §3.2） |
| 「超时 ≠ 失败」需要可判定 | 建议在超时分支加提示 | §0.2 指出该结构化返回**早已存在**（`:1160-1168`） | **计划 04 对** —— [30] 建议②其实**早就在代码里了** |
| 快速返回实测 | 6 轮 0 超时 | 4 次 0 超时，11.9s–21.8s | **两边都对，且互为佐证**（6 轮比 4 次证据更强） |

### 3.2 计划 04 的「破坏性」具体指什么？—— 三条里 **1 条成立、1 条被证伪、1 条只是文案**

计划 04 §0.5 列了三条破坏性理由。逐条用源码裁定：

#### ✅ 第 ① 条成立，且比计划 04 的散文更硬：**返回体丢 `state`**

这是**破坏性的真正来源**，源码可逐字对照：

```javascript
// MCP/index.js:1082-1089  —— waitForNotification:false 的快速返回分支
    // If not waiting for notification, return immediately
    if (!waitForNotification) {
      return {
        success: true,
        message: `Game started (PID: ${process.pid})`,
        pid: process.pid
      };
    }
```

```javascript
// MCP/index.js:1132-1139  —— 阻塞路径的主菜单就绪成功分支
      if (detected.ueMainMenu) {
        return {
          success: true,
          message: 'Game started and initialization complete (main menu ready)',
          state: 'main_menu',
          pid: detected.gamePid || process.pid,
          elapsedTime: now - startTime,
          source: 'stage_probe'
        };
      }
// MCP/index.js:1145-1152 —— 同构的「进程稳定期」退回分支，也带 state:'main_menu'
```

⇒ **快速返回分支的返回体里根本没有 `state` 这个键**（也没有 `elapsedTime` / `source`）。
⇒ 把默认值翻成 `false` 后，**所有不显式传参的既有调用方**（包括人写的脚本、别的 agent、编排器）会静默地从 `{state:'main_menu', …}` 降级到 `{pid}`。任何形如 `if (r.state === 'main_menu')` 的后继判断会**恒为 false**，代码不报错、行为静默错误。
⇒ **计划 04 §0.5 第 ① 条「返回值不再保证含 `state:'main_menu'`，调用方的后继判断全部要重写」—— 成立，且措辞精确。**
⇒ **额外发现（计划 04 未点明）**：这个坑**不只影响默认值**。今天显式传 `false` 的调用方（[30] 推荐的绕过方式、以及计划 04 要写进描述的推荐路径）**同样拿不到 `state`**，只是它们已经知道要靠 `pid` + `get_game_status` 轮询。计划 04 若只写「推荐传 false」而不提醒「false 分支不返回 `state`」，等于把一个既有的返回契约缺口顺延成「推荐路径」。

#### ❌ 第 ② 条被证伪：**`ensureConnected` 时机不是破坏性**

计划 04 §0.5 第 ② 条担心「改成快速返回后，重连会在游戏尚未初始化完时更早被触发」。实测源码：

```javascript
// MCP/index.js:4360-4365
      if (result && result.success) {
        // 成功路径末尾：游戏进程已存在，主动试一次 GABP 重连（ensureConnected 有限尝试，
        // 失败无妨——后续工具调用仍会惰性重连）。RimBridgeServer 启动约 65s 延迟，
        // 此刻可能尚未 discover 到，属正常。
        if (gabpBridge) { try { void gabpBridge.ensureConnected(); } catch (e) { log('WARN', `GABP ensureConnected 异常: ${e.message}`); } }
      }
```

关键三点，逐条可核：
1. `void` + `try/catch` ⇒ **fire-and-forget，失败只打 WARN，不影响返回**。
2. 注释 `:4362-4363` 明写「RimBridgeServer 启动约 65s 延迟，**此刻可能尚未 discover 到，属正常**」⇒ 「触发得早」**本来就是这段代码预期的常态**，不是新引入的偏差。
3. `:2745-2746` 注释 + `:2748` 代码 ⇒ **每一次 GABP 工具调用都会先 `ensureGabpConnected()`**（「惰性断连自愈：每次 GABP 工具调用先 ensureConnected——已连接零成本直通；断连/未连接（含外部手动重启游戏后 token 滚动）先重连」）。⇒ `:4364` 这次提前触发即便失败，也会被下一次 GABP 调用无条件补上。

⇒ **第 ② 条应从计划 04 的「破坏性理由」里降级为「无害的时序前移」。** 它不是保留 `true` 的支撑理由。

#### ⚠️ 第 ③ 条成立，但只是文案漂移：`:370` 的 guidance

```javascript
// MCP/index.js:370
  GAME_NOT_RUNNING: { message: '游戏未运行', guidance: '先用 start_game 启动游戏（waitForNotification=true 会等待主菜单就绪），再调用本工具' },
```

⇒ 翻默认值确实会让这句与新默认自相矛盾。**但这是一条字符串，改起来零风险**，不构成「破坏性」，只构成「改了默认值之后必须一并改文案」。

#### ⇒ 小结

计划 04 的**结论**（不改默认值）**正确**，但**支撑它的三条理由里只有一条（返回体丢 `state`）是真正承重的**。这不改变裁决，但主代理若向维护者复述计划 04 的论证，应当只讲第 ① 条，否则会把一个已被 `:4361-4364` 自我豁免的担忧说成硬约束。

---

## 4. [30] 的 6 轮实测是否足以推翻计划 04 的顾虑？

### **不足以。结论：不推翻。**

关键在于两件事在**不同的轴**上，而计划 04 的破坏性只由后者决定：

| 轴 | [30] 的 6 轮实测是否覆盖 |
|---|---|
| 轴 A：**会不会超时**（时延） | ✅ 覆盖，且证据强（6/6 vs 计划 04 的 4/4） |
| 轴 B：**返回体语义变不变**（契约） | ❌ **零覆盖** |

6 轮实测记录的是「`false` 不超时」，它**在数学上不可能**回答「返回体还是不是 `{state:'main_menu', …}`」——因为返回体根本不随**是否超时**变化，`false` 分支的 `:1084-1088` 是无条件 `return`。即使 [30] 再做 60 轮，也依然拿不到这个轴上的信息。

**一条必须点明的区分**（这也是本次裁定的核心）：

> 「实测没超时」是**时延性质**的观察；
> 「返回体语义没变」是**契约性质**的判断。
> **破坏性来自后者，而 [30] 的证据全部落在前者。**
>
> 这正是计划 04 §0.2 / §0.3 与 §0.5 的分野：§0.2-0.3 说「结构化返回**已经存在**」（所以不用加能力），§0.5 说「默认值不能翻」（所以只加文案）。[30] 的实测既没有推翻 §0.2-0.3，也没有触及 §0.5。

**但 [30] 的证据确实带来了两处计划 04 应当吸收的增量**（见 §6 的 d/e），它们改变的是「计划 04 该写什么文案」，**不是「默认值该不该翻」**。

### 4.1 附带核实：[30] 的 `start_quick_test` 子结论**不可信，不应进计划**

[30] 称「`start_quick_test{timeout:30000}` 也超时」，并推出「`timeout` 实测**要 ≥ 240000**」。**这条推导与服务端代码矛盾**：

```javascript
// MCP/index.js:1595（startQuickTest 内）
    while (Date.now() - startTime < timeout) {
// MCP/index.js:1648-1656（超时分支）
    const finalDetected = await detectGameStage();
    return {
      success: false,
      message: `Timeout waiting for map loaded / world tick advancing (${timeout}ms)`,
      state: 'timeout',
      ...
```

⇒ 传 `timeout:30000` 时，**服务端在约 30 秒后返回一个正常的 JSON 结果**（`success:false` / `state:'timeout'`），**不是 `-32001`**。30 秒远小于任何合理的客户端截止线（§2.2 实测为 180~240s 量级），没有理由产生 `-32001`。
⇒ 最可能的解释：**报告者把服务端返回体里的 `state:'timeout'` / `message:'Timeout waiting for map loaded (30000ms)'` 与客户端的 `-32001 "Request timed out"` 混为一谈。** 两者字面都叫「超时」，但一个是**服务端业务结论**（游戏在不在、有没有进图、是否暂停），一个是**客户端根本没收到任何结果**。
⇒ 因此 **「`timeout` 要 ≥ 240000」这个结论目前没有证据支撑，不应写进计划 04 的文案。** 若要采用，必须先重跑一次并明确记录拿到的是 `-32001` 还是 `state:'timeout'`。

> 这反而**加强**了计划 04 任务 2 步骤 5 的 notes：**必须把「`state:'timeout'`（服务端判定超时，但返回体里有 `running`/`currentStage`/`inGame`/`paused`）与 `-32001`（客户端掐断，什么都拿不到）」这两种同名不同义的超时分开写。** 现有代码 `:1649-1666` 已经具备做这种区分所需的全部字段，`MCP/index.js:1658-1659` 的注释甚至已经点明「暂停时 tick 不走，会一直等到超时」。

### 4.2 附带核实：[30] 建议②（超时分支加提示）**早已实现**

[30] ④ 的第二个建议「在超时分支里回一句『游戏可能已启动，请用 `get_game_status` 复核』」——`MCP/index.js:1160-1168` 的超时返回体里**已经有**：

```javascript
      state: 'timeout',
      ...
      guidance: '游戏进程已启动；可调 get_game_status 查看当前阶段，或手动等待主菜单'
```

⇒ 与计划 04 §0.2 的判断一致。**建议②不是缺口，是已有能力缺文案。**

---

## 5. 严重度复评

### **维持 🟠，不上调 🔴。**

**不上调的依据**：
1. 报告者自己在 ③ 里写明「**不产生错误结论**，但每一轮白等 3~4 分钟」。按 AGENT-BRIEF §5.2，🔴 的门槛是「会让自动化得出**事实相反的结论**」，报告者本人否认了这一条，**我没有独立证据推翻报告者的自评**（[30] 的复现记录里，调用方确实靠 `get_game_status` 正确判定了「游戏已起来」—— 即误判并未实际发生）。
2. 危害是**可恢复**的：`get_game_status`（`MCP/index.js:1296-1299`）可靠返回 `running` / `stage` / `game_pid`，误判后一次调用即可纠正，不产生不可逆后果。
3. 已有稳定绕过且经过 6 轮验证（`waitForNotification:false`），不阻塞任何流程。

**不上调的补充理由**：危害主体是**时延**（3~4 分钟/轮），不是正确性。按 AGENT-BRIEF §5.2，🟠「每次都要重踩 / 系统性浪费往返」正是这一档的定义。

**但标注为 🟠/🔴 边界**，理由是 [30] ③ 的后半句「更要紧的是它**把成功伪装成失败**，容易诱发调用方做多余的补救（重复启动）」——**这句话本身描述的正是 🔴 行为**。之所以仍判 🟠，是因为：
- 该风险**依赖调用方不去查 `get_game_status`**（条件性），而 [30] 自己的复现记录显示调用方查了；
- 计划 04 的修复（把「`-32001` 不代表失败 + 用 `get_game_status` 复核」写进三处描述与 notes）**正是消除这一诱导的最小手段**，修完之后 🟠 的残余风险接近于零。

**⇒ 结论：严重度 🟠 由计划 04 的文案任务闭环；不需要为提级单独立计划。**

---

## 6. 责任归属

| 事项 | 归属 | 理由 |
|---|---|---|
| `waitForNotification` 的**生效默认值**（`:4358` / `:942`） | **计划 04**（§0.4 论证机制、§0.5 决定不改） | §2.1 已逐行复核，计划 04 判断正确 |
| **默认值的「是否翻」这一决策本身** | **计划 04**（不翻） | §3.2 第 ① 条：翻了就丢 `state:'main_menu'` |
| `-32001` 与「超时≠失败」的可判定性 | **计划 04**（§0.2-0.3 判定为**已有能力，缺文案**） | §4.2 已复核 `:1160-1168` |
| `false` 分支返回体缺 `state` / `elapsedTime` / `source` | **⚠️ 无归属 —— 计划 04 §0.2 明写「本计划不新增任何返回字段」，因此这条被排除在计划 04 之外** | 见下 §6.1 |
| `start_quick_test` 的 `timeout` 指引文案 | **计划 04 任务 2 步骤 4**（已有归属） | 但 [30] 的「≥240000」结论不可信，见 §4.1 |
| `state:'timeout'` vs `-32001` 两种「超时」的区分 | **⚠️ 无归属** —— 计划 04 任务 2 步骤 5 的 notes 只笼统写「回 -32001 时用 `get_game_status` 判定」，**没有区分服务端 `state:'timeout'`** | 见下 §6.2 |
| `:370` 的 guidance 文案 | 计划 04 **不涉及**（因为不改默认值，所以不必改） | 保持原样即自洽 |

### 6.1 孤儿项 ①：`false` 分支的返回契约缺口

`MCP/index.js:1084-1088` 返回 `{success, message, pid}`，**无 `state`**；而阻塞成功分支 `:1132-1139` / `:1145-1152` 返回 `state:'main_menu'`。

计划 04 **明确不处理**（§0.2「本计划不新增任何返回字段」）⇒ 这是一条**真实存在、但两边都没认领**的缺口。它不影响本次裁决（默认值保持 `true`，`false` 是显式选择），但计划 04 既然要把「推荐传 `false`」写进三处描述，**就该在同一处把「false 分支不返回 `state`，请用 `pid` + `get_game_status`」一起写上**，否则等于把一个未声明的契约差异推荐成默认路径。

### 6.2 孤儿项 ②：两种「超时」同名不同义

`state:'timeout'`（服务端 `:1160-1168` / `:1649-1656`，**带 `currentStage` / `pid` / `elapsedTime` / `guidance` / `inGame` / `paused`**）与 `-32001`（客户端截止线，**什么都没有**）都叫「超时」。[30] §4.1 已经**实测踩中**了这个混淆并写进了建议里，而计划 04 的 notes 没有区分。

---

## 7. 归并锚点

### 主锚点 —— 归并进 **计划 04**，不单立

`docs/issue-triage/plans/2026-10-01-04-startup-call-timeout-disambiguation.md`

| 锚点（精确到任务/步骤） | 承接 [30] 的哪一部分 |
|---|---|
| **§0.5「决定：不改默认值（破坏性变更）」** | [30] ④ 的「把默认值改成 `false`」—— 驳回，理由见本报告 §3.2 第 ① 条 |
| **任务 1 · 步骤 4**（改 `MCP/index.js:1836-1840` 的 `waitForNotification` 参数描述） | [30] ④ 的「快速返回实测有效」—— 写成推荐路径 |
| **任务 2 · 步骤 7**（`:4358` / `:942` 只加注释、不改行为） | [30] ④ 的「改默认值」在实现层面的正确落点（§2.1） |
| **任务 2 · 步骤 5**（`TOOL_USAGE_NOTES` 补 `start_game` notes） | [30] ④ 的建议②「超时后用 `get_game_status` 复核」 |
| **验收清单最后一条**（「`waitForNotification` 的生效默认值仍为 `true`」） | 回归护栏，防止未来被误翻 |

### 建议对计划 04 追加的两处**小修订**（不构成新计划，建议以「计划修订」形式并入）

| # | 位置 | 建议内容 | 依据 |
|---|---|---|---|
| **A** | 计划 04 **任务 2 · 步骤 5** 的 `start_game` notes | 在既有 notes 末尾补一句：`false` 分支**只返回 pid、不返回 `state`**；同时把「服务端 `state:'timeout'`」与「客户端 `-32001`」**分开写**，前者带 `currentStage`/`pid`/`elapsedTime`，后者什么都拿不到 | 本报告 §6.1 / §6.2 |
| **B** | 计划 04 **任务 2 · 步骤 4** 的 `start_quick_test.timeout` 描述 | **不要**采纳 [30] 的「要 ≥240000」结论。如需给数值指引，必须先重跑一次并区分拿到的是 `-32001` 还是 `state:'timeout'` | 本报告 §4.1 |

### 为什么不单立

- [30] 的**主诉**（默认值应否翻）在计划 04 里**已有明确决策**（§0.5），不是未决问题；
- [30] 的**第二个建议**（超时分支给提示）经复核**早已实现**（§4.2），是文案问题，正是计划 04 的主体任务；
- [30] 的**实测数据**（6 轮 0 超时）与计划 04 自己的 4 轮实测**方向一致、互相加强**，不构成冲突，不需要新计划来消化；
- 唯一真正的增量（§6.1 / §6.2 两处孤儿项）都是**一句话级别的文案补充**，量级远低于「值得新开一份计划」的门槛。

### 一句话给维护者

> **不用改默认值。** 计划 04 提的「改 schema 无效、生效值在 `MCP/index.js:4358` / `:942`」是对的；保留 `true` 的真正理由是 —— 快速返回分支 `MCP/index.js:1084-1088` **不返回 `state`**（阻塞分支 `:1132-1139` 返回 `state:'main_menu'`），翻了默认值所有既有调用方的 `state` 判断会静默失效。至于「`ensureConnected` 会更早触发」：**那不是问题**，`MCP/index.js:4361-4364` 自己就注明「RimBridgeServer 启动约 65s 延迟，此刻可能尚未 discover 到，属正常」，且 `:2748` 每次 GABP 调用都会先重连兜底。

---

## 8. 搜索范围与零命中声明

本报告所有「已核实」结论均来自下列**逐行复核**（`[System.IO.File]::ReadAllLines` + `ReadAllText`），未采信 `read` / `read_file` 工具的行号输出（AGENT-BRIEF §3.5）：

| 检索目标 | 搜索范围 | 结果 |
|---|---|---|
| `startGame` 默认值 / `waitForNotification` | `MCP/index.js` 全文 **5460 行** | 命中 `:370` `:942` `:1082-1089` `:1836-1844` `:4356-4369`，与计划 04 §0.7 索引**逐条一致** |
| `ensureConnected` 全部出现处 | `MCP/index.js` 全文（正则 `ensureConnected`） | 9 处：`:156 :1218 :2629 :2634 :2672 :2745 :4361 :4364 :4375 :5365 :5396` |
| `startQuickTest` 全部出现处 | `MCP/index.js` 全文（正则 `startQuickTest`） | 3 处：`:1545`（定义）`:4414`（调用）`:5426`（导出） |
| SDK 默认截止线 | `MCP/node_modules/@modelcontextprotocol/sdk/dist/esm/shared/protocol.js`（全文 1107 行） | `:8 DEFAULT_REQUEST_TIMEOUT_MSEC = 60000` |
| 服务端配置 | `MCP/config.json`（全文 `ReadAllText`） | `startTimeout: 180000` |
| 客户端 `timeout` 配置 | ① `%APPDATA%\Code\User\mcp.json` ② `%APPDATA%\Trae CN\User\mcp.json` ③ `%USERPROFILE%\.dsh\` 下 `-Depth 1` 的 `*.json` ④ `Mods\` 下 `-Recurse -Depth 3` 的 `*mcp*.json` | **均无 `timeout`/`requestTimeout` 字段**。范围之外（harness 客户端本体）未覆盖，**不作「不存在」断言** |
| `TEXT20` 项目 | `Mods\TEXT20\` 下 `-Depth 1` 的 `*.json`/`*.md`（列出 40 项文件名） | 本次未发现 [30] 相关的额外实现/配置；`TEXT20` 是 [30] 中「登记代理」的工作目录，非代码归属方 |
| 吐槽板条目定位 | `Select-String -Pattern '^## \[30\]'` + `[regex]::Match($text,'(?s)## \[30\].*?(?=## \[\d+\]|\z)')` | 命中 1 段，全文已读 |

**未使用 `search_string_literals`**（本报告所有结论均由文件直读取得，无需该工具）。
**未调用任何会改变游戏状态的 MCP 工具；未启动游戏；未编译；未修改任何既有文件。**
裁决：值得修

# [04] 启动类调用「必超时，超时 ≠ 失败」🟠

**一句话结论**：对 `start_game` 完全属实且已量化（日志里 7 次长耗时调用全部超时，成功/失败的分界线落在 131.5s 与 192.7s 之间），但**吐槽板建议的修法「拆成两个工具，后者应快速返回」本仓库早已实现 —— 就是 `waitForNotification:false`，实测 4 次调用 0 超时**；真正缺的是**默认策略与文案**。另：`start_quick_test` 超时这一半在全部日志里查无实据。

---

## 1. 核实过程

### 1.1 服务端在超时路径上确实返回结构化状态 —— 吐槽板的「建议」已被实现

`MCP/index.js:1158-1168`（`startGame()` 的超时分支）：

```javascript
    // Timeout
    const finalDetected = await detectGameStage();
    return {
      success: false,
      state: 'timeout',
      message: `Timeout waiting for main menu (${timeout}ms)`,
      currentStage: finalDetected.stage,
      pid: finalDetected.gamePid || process.pid,
      elapsedTime: Date.now() - startTime,
      guidance: '游戏进程已启动；可调 get_game_status 查看当前阶段，或手动等待主菜单'
    };
```

**这正是 `docs\mcp-吐槽板.md:340` 要求的「返回结构化状态（至少 `{timedOut:true, processSeen:true, pid:...}`）」** —— `pid`、`currentStage`、`state:'timeout'`、外加一条 guidance，全都在。吐槽板写这条时没读到这段代码。

而且 `waitForNotification:false` 的快速返回路径也早就有了 —— `MCP/index.js:1082-1089`：

```javascript
    // If not waiting for notification, return immediately
    if (!waitForNotification) {
      return {
        success: true,
        message: `Game started (PID: ${process.pid})`,
        pid: process.pid
      };
    }
```

这就是吐槽板 `docs\mcp-吐槽板.md:341` 说的「把『等待就绪』与『发起启动』拆成两个工具，后者应快速返回」。**已实现，只是没被当默认、也没被写进工具描述。**

### 1.2 `-32001` 来自客户端截止线，早于服务端响应 —— 这是根因

`MCP/node_modules\@modelcontextprotocol\sdk\dist\esm\shared\protocol.js:712-713`：

```javascript
            const timeout = options?.timeout ?? DEFAULT_REQUEST_TIMEOUT_MSEC;
            const timeoutHandler = () => cancel(McpError.fromError(ErrorCode.RequestTimeout, 'Request timed out', { timeout }));
```

`.../protocol.js:8`：`export const DEFAULT_REQUEST_TIMEOUT_MSEC = 60000;`

⇒ `-32001 / Request timed out` 是 **MCP 客户端的请求截止线**，不是 MCP 服务器的业务错误。它一旦触发，客户端就**拿不到**上面 1.1 那份结构化载荷 —— 于是 `pid` / `currentStage` / `guidance` 全部丢失，调用方只剩下一个「超时 vs 失败」不可分辨的错误串。这与吐槽板描述的现象逐字吻合。

### 1.3 日志把分界线量出来了（全部 5 个 MCP 日志，2026-09-28 → 2026-10-01）

用 `tools/call (start_quick_test|start_game) .*(error|timeout)` grep 全部日志，43 条命中。`start_game` 按结果分组：

**`waitForNotification=true`（默认）→ 超时 7 次，无一例外：**

| 日志行 | timeout 参数 | 实测耗时 | 结果 |
|---|---|---|---|
| `mcp-20261001-093829.log:210` | 240000 | **262696ms** | `error(unknown)` |
| `mcp-20261001-093829.log:250` | 180000 | **192905ms** | `error(unknown)` |
| `mcp-20261001-093829.log:293` | 240000 | **252458ms** | `error(unknown)` |
| `mcp-20261001-093829.log:668` | 180000 | **192944ms** | `error(unknown)` |
| `mcp-20261001-093829.log:726` | 240000 | **252384ms** | `error(unknown)` |
| `mcp-20261001-093829.log:793` | 180000 | **192691ms** | `error(unknown)` |
| `mcp-20261001-093829.log:977` | 240000 | **252476ms** | `error(unknown)` |

**`waitForNotification=true` → 共 15 次调用（8 成功 / 7 超时），成功最慢 131472ms（`:350`）：**

```
350: [14:21:46.557] tools/call start_game args={timeout=240000, waitForNotification=true} → 131472ms success
370: [14:25:55.965] tools/call start_game args={timeout=240000, waitForNotification=true} → 128514ms success
390: [14:30:12.261] tools/call start_game args={timeout=240000, waitForNotification=true} → 114857ms success
736: [16:07:16.225] tools/call start_game args={timeout=240000, waitForNotification=true} →  96531ms success
760: [16:11:40.382] tools/call start_game args={timeout=240000, waitForNotification=true} →  94476ms success
871: [16:46:52.123] tools/call start_game args={timeout=300000, waitForNotification=true} →  96286ms success
893: [16:51:31.556] tools/call start_game args={timeout=300000, waitForNotification=true} → 103840ms success
```

（另有 `mcp-20260928-173737.log:504` 一次 `timeout=240000, waitForNotification=true` → 105268ms success。）

**`waitForNotification=false` → 4 次全成功，无一超时：**

```
mcp-20260928-173737.log:641  [09:40:58.716] start_game args={timeout=200000, waitForNotification=false} → 21725ms success
mcp-20261001-093829.log:478  [15:05:13.165] start_game args={waitForNotification=false, timeout=180000} → 21786ms success
mcp-20261001-093829.log:542  [15:18:02.504] start_game args={timeout=180000, waitForNotification=false} → 12072ms success
mcp-20261001-093829.log:569  [15:22:50.365] start_game args={timeout=180000, waitForNotification=false} → 11953ms success
```

**⇒ 客户端截止线落在 (131.5s, 192.7s] 区间**（成功最慢 131472ms 全部回包成功，失败最快 192691ms 全部没回包）。这是**推断**，依据是上面两组数据；DSH harness 的实际 timeout 配置我没读到（不在本仓库内）。

### 1.4 服务端在失败路径上白烧掉整个 timeout，这是可修的成本项

从 1.3 的表能直接读出规律：**失败路径耗时 ≈ 请求的 `timeout` + 约 12.4 秒**。

- `timeout=180000` → 192905 / 192944 / 192691（三次都是 ~192.8s，+12.8s）
- `timeout=240000` → 252458 / 252384 / 252476（三次都是 ~252.4s，+12.4s），另一次 262696（+22.7s）

多出的 ~12.4 秒来自 `MCP/index.js:1159` 超时后还要再跑一次 `detectGameStage()`。⇒ **无论成功失败，`timeout` 参数一大，服务端就必然占用这么久。** 调用方若把 `timeout` 设成 300000，等于宣告「我一定会先于服务端超时」。

### 1.5 `start_quick_test` 超时这一半：全部日志里查无实据

在 43 条命中里，`start_quick_test` 共 24 次调用，结果是：

- 成功 **19** 次，耗时 37444ms ～ 104074ms（**最长 104s，从未接近 120s 默认上限**）
- 快速失败 **5** 次，且都带**正确的错误码**：`error(GAME_NOT_RUNNING)`（`:260` 210ms、`:654` 247ms、`:693` 313ms、`:946` 228ms）、`error(ALREADY_IN_GAME)`（`:959` 491ms）
- **超时 0 次**

日志时间覆盖到 `mcp-20261001-093829.log` 末尾 `[18:07:13.069]`，而吐槽板快照时间戳是 `docs/issue-triage/state/board-state.json:3` 的 `2026-10-01 18:01:10` ⇒ **这场会话被完整覆盖了**，不是日志轮转掉了。

⇒ `docs\mcp-吐槽板.md:329` 的「`start_quick_test{timeout:300000}` → 同上」**在我能读到的全部证据里不成立**。两种可能：(a) 当时客户端截止线被配得比现在短；(b) 记错了。**我不下结论**，只标注证据缺口。

代码侧也支持「不太可能超时」：`startQuickTest()`（`MCP/index.js:1545`）进图判据是「模组通知 / 阶段探测 + 连续两次 `gameTick` 递增」（`:1611`、`:1620-1631`），比 `startGame` 的「主菜单就绪」早得多，实测 104s 也只是勉强过半程。

### 1.6 工具描述完全没有提示这两件事

`MCP/index.js:1827`：`description: 'Start RimWorld game and wait for main menu notification from mod'`
`MCP/index.js:1836-1839`：

```javascript
        waitForNotification: {
          type: 'boolean',
          description: 'Wait for main menu notification from mod (default true)',
          default: true
        },
```

`MCP/index.js:1841-1844`：`timeout` 描述只说「Timeout in ms for waiting notification」。

三处都没说：(a) 客户端可能先于服务端超时、拿到的是 `-32001` 而不是结构化结果；(b) `waitForNotification:false` 是快速返回的正确用法。`start_quick_test`（`:1942`）同理。

### 1.7 关于「连发两次会不会起两个游戏」

`MCP/index.js:2923` 的工具备注写着 `start_game: '游戏未运行时可启动；已有 RimWorld 进程运行时会拒绝（端口冲突）'`，`README.md:53` 也提到 `start_game` / `stop_game` 有防止重复启动的实现。⇒ 盲目重试**大概率**被端口冲突挡下，不会起两个实例。但这一点我**没有逐行验证** `startGame()` 的进程检查分支，标为**假设**。

---

## 2. 影响与严重度复评

**维持 🟠。** 复评理由：

- **不会导致事实相反的结论。** 与 [01]（假成功）不同，超时**不会**让调用方误以为动作执行了 —— 它只会让调用方什么都不知道。真正被破坏的是「可判定性」而非「正确性」。
- **但确实制造了事实相反的结论 —— 关于游戏状态。** 吐槽板原文：「无法区分『超时且失败』与『超时但正在成功启动』」。调用方看到 `-32001` 时，既可能游戏根本没起来，也可能 10 秒后就到了主菜单。1.3 的 7/7 超时全部是**后者**（`guidance` 字段明写「游戏进程已启动」）。**在这种分布下，自动化按「超时=失败」处理，会系统性地得出反向结论。**
- **每次调用成本**：token 几乎为 0（一条错误串）。真正的代价是**墙钟时间** —— 失败路径 192.7s ～ 262.7s，且这 3～4.5 分钟是纯等待、无任何增量信息。加上盲目重试，一轮「启动游戏」可能烧掉 6～9 分钟。这在 agent 编排里是实打实的时间税。
- **对比**：`waitForNotification:false` 实测 11.9s ～ 21.8s，4 次调用 0 超时。**同样的目标，成本差一个数量级，且结果可判定。**

⇒ 🟠 中等偏高，且修复成本极低（见 §4）。

---

## 3. 责任归属

**本项目（UESDdebuger）。** 理由：

1. `startGame()` / `startQuickTest()` 的阻塞等待模型、默认 `waitForNotification:true`、默认 `timeout:180000`（`MCP/index.js:4357` 回落 `config.startTimeout`，见 `MCP/config.json` 的 `"startTimeout":180000`）全部是本仓库的选择。
2. 服务端在失败路径上**必定烧满整个 timeout**（1.4），这是本仓库的循环写法造成的（`MCP/index.js:1099` 的 `while (Date.now() - startTime < timeout)` 无任何服务端自限）。
3. 工具描述缺警告（1.6）是本仓库的文案缺口。

**不是** MCP SDK 的锅 —— `-32001` 是 SDK 的标准语义，SDK 行为正确。
**不是** RimWorld 的锅 —— 游戏启动慢是事实，但本仓库有义务不让「慢」变成「不可判定」。

**附带一块偏纯文档缺失**：即便不改任何代码，只要把 `waitForNotification:false` 写进工具描述，吐槽板描述的场景立刻可解。这是全条里成本最低的一刀。

---

## 4. 修的话怎么修

按成本从低到高：

1. **`MCP/index.js:1836-1839`**：改 `waitForNotification` 的 description，明写
   「设 `false` 则**立即返回**（实测 12–22s）并给出 pid，不等待主菜单；这是 agent 编排的推荐默认值。设 `true`（默认）最多阻塞 `timeout` 毫秒，且**可能先于服务端返回就被客户端截止线打断，此时你会拿到 `-32001` 而不是结构化结果** —— 那不代表启动失败，请用 `get_game_status` 复查。」
   纯文案，零风险，立刻解决吐槽板的全部痛点。

2. **`MCP/index.js:1841-1844` / `:1946-1949`**：给 `timeout` 的 description 补一句「不要把本参数设成大于客户端请求截止线的值，否则结果必然丢失」。
   纯文案。

3. **`MCP/index.js:1827`**：`start_game` 的整体 description 补一句「启动游戏请优先 `waitForNotification:false` + 轮询 `get_game_status`；只有明确需要『等到主菜单』时才用默认阻塞模式」。

4. **`MCP/index.js:1099-1156`**：给等待循环加服务端自限，例如把可自设的上限压到远小于客户端截止线（如 90s），或增加「检测到客户端已断开则提前退出」。**注意**：这条改起来有风险，需要先确认 SDK 侧能否感知取消（`protocol.js:713` 的 `cancel(...)` 表明有取消通道，但**本项目是否订阅了取消信号我还没查**，属**待核实**项）。若不打算查，第 4 条可先不做。

5. **（可选）与 [03] 合并**：`start_quick_test` 部分按 1.5 的结论暂不动代码，只在文案里补一句「本工具实测不会超时，超时即异常，请上报」。

**不建议**做的：把 `start_game` 的默认改成 `waitForNotification:false`。虽然 1.3 的数据支持这么做，但默认语义变更会影响所有既有调用方（有人可能就依赖它阻塞到主菜单）。**先做文案（1–3），观察后再议默认值。**

---

## 5. 是否已被现有计划覆盖

**否。**

- `docs\superpowers\plans\` 下只有 `2026-09-27-ue-csharp-console-roslyn-upgrade.md`。grep `list_debug_action_children|search_debug_actions|search_source|start_quick_test|waitForNotification|上下文|context budget|裁剪|截断|truncat` —— **零命中**；再 grep `start_game|工具返回|token` —— 该计划内也无相关任务。
- 该计划唯一涉及 MCP 文案的是 `## 任务 12：MCP 文案与端到端验收`（`2026-09-27-ue-csharp-console-roslyn-upgrade.md:2050`），但它只改 `MCP/index.js:2937` 的 `execute_csharp_code` 一条 notes（`:2053`），与 `start_game` 无关。
- `docs\issue-triage\plans\` 目录当前不存在，无重复计划风险。

⇒ 建议派 PLAN 子代理，范围限定为第 1–3 项（纯文案）；第 4 项作为可选后续，前置条件是先查清 SDK 取消信号是否可用。
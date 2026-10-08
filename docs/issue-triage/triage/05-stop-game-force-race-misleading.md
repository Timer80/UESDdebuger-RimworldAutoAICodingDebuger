裁决：值得修

`stopGame` 把「游戏已经不在了」返回成 `{success:false, message:'Game not running'}`（`MCP/index.js:1186-1188`），而 `success` 在这个工具里的语义是「我完成了一次杀进程动作」而不是「你要求的终态已达成」；更糟的是 `MCP/index.js:1184` 每次调用都无条件把 `selfStartedPid` 清空，导致第一次调用就把定向 PID 的记忆擦掉 —— 第二次调用被迫回退到全局 `findRimWorldProcess()`，这正好是 `README.md:53` 声称要防的误杀路径。

## 核实过程

### 1. 吐槽板原文（`docs/mcp-吐槽板.md:347-367`）

```
stop_game{force:false}  → {"success":false,"message":"Game still closing, may need force","pid":24364}
stop_game{force:true}   → {"success":false,"message":"Game not running"}
```

断言：两次之间游戏自己退掉了；「may need force」诱导调用方升级到 force，结果拿到更难判定的「not running」。

### 2. 完整实现（本项目自有代码，不经 GABP）

`MCP/index.js:1179-1208`：

```javascript
async function stopGame(force = false) {
  try {
    // P1-MCP-2：优先按 selfStartedPid 定向停止（只杀本服务启动的进程，不误伤玩家手动开的其它实例）。
    let pid = selfStartedPid && await isPidAlive(selfStartedPid) ? selfStartedPid : null;
    selfStartedPid = null;                                     // ← 无条件清空
    const proc = pid ? null : await findRimWorldProcess();
    if (!pid && !proc) {
      return { success: false, message: 'Game not running' };   // ← 终态已达成，却报 success:false
    }
    const target = pid || Number(proc.pid);

    if (force) { await execAsync(`taskkill /F /PID ${target}`); }
    else       { await execAsync(`taskkill /PID ${target}`); }

    await new Promise(resolve => setTimeout(resolve, 2000));
    const still = await findRimWorldProcess();

    if (still && Number(still.pid) === target) {
      return { success: false, message: 'Game still closing, may need force', pid: target };
    }
    return { success: true, message: 'Game closed', pid: target };
  } catch (error) {
    return { success: false, message: `Stop failed: ${error.message}` };
  }
}
```

工具入口 `MCP/index.js:4371-4372`：`case 'stop_game': { const result = await stopGame(args?.force ?? false); ... }`。**这条路径完全不经过 GABP / RimBridgeServer / vanilla**，是纯本项目代码。

### 3. 三处缺陷，逐条对应源码

**(a) `success` 的语义是「动作完成」而不是「终态达成」。**
四个出口里三个是 `success:false`，但其中两个（`'Game not running'`、`'Game still closing'`）描述的都是**进程状态的正常观测**。调用方按 `success` 分支时，「游戏已不在」被读成「停止失败」。吐槽板说的「反而比第一次更难判断」正是这个：`Game still closing` 至少附带了 `pid:24364` 这个可复查线索，`Game not running` 连 pid 都没有。

**(b) `:1184` 的 `selfStartedPid = null;` 是无条件赋值。**
它在**每次**调用开头执行，包括「第一次只发了 WM_CLOSE、2 秒后进程还在」这种正常路径。于是：
- 第二次调用（不论 `force` 多少）必然走 `findRimWorldProcess()`（`:893-895`，即 `tasklist /FI "IMAGENAME eq RimWorldWin64.exe"`），**匹配任意一个** RimWorld 实例，而不是原来那个 `selfStartedPid`。
- `README.md:53` 把这条写成设计不变量：「`selfStartedPid` 等全局状态…防止重复启动与定向停止」「误杀风险」。第一次调用把这个不变量破坏了。

**(c) `may need force` 这句文案与它自己返回的数据矛盾。**
返回体里同时有 `success:false`（暗示什么都没做成）和 `pid`（暗示已经发过 WM_CLOSE、进程仍在）。文案没有告诉调用方「**你刚才那次已经成功发出了关闭请求，现在只是在等它退**」这个关键事实。这不是文案不好，是**返回体缺少「已发起关闭」这一状态维度**。

### 4. 与已记录的相邻事实对照

- `MCP/logs/mcp-20261001-093829.log:256-259` 有一条真实的非 force→force 序列（但顺序相反，是先 force）：
  ```
  [14:10:07.710] [GabpClient] GABP 连接断开: connection lost
  [14:10:09.211] tools/call stop_game args={force=true} → 2708ms success
  ```
  这次走的是成功路径（`Game closed`），**不能证实也不证伪吐槽板那条具体记录**。吐槽板的两条 JSON 是维护者当场粘贴的原始返回值，我**没有**在仓库日志里找到同样的 `Game still closing` 字样 —— 因此「这条具体交互确实发生过」标注为**未经日志独立复核**，但**产生它的代码路径在 `:1200-1202` 明确存在且必然产出该文案**。

- `MCP/index.js:1848-1861` 的工具 schema 里，`stop_game` 的 `description` 只有一句 `'Stop RimWorld game'`，`force` 的描述是 `'Force stop (default false)'`。**没有任何地方告诉调用方「游戏已不在」会返回 `success:false`**。
- `MCP/index.js:2924` 的 `TOOL_PRECONDITION.stop_game = '游戏运行中'` —— 这句前置条件恰好是**误导的另一半来源**：它让人以为「不满足前置 = 我的调用非法/失败」，而不是「终态已达成」。

## 影响与严重度复评

**复评：🟠 中，维持不变。不升 🔴。**

- **不是「事实相反」型。** 与 [01] 不同，这里返回的 `message:'Game not running'` 在**文本层面是准确的** —— 游戏确实不在。错的是 `success` 这个布尔位的方向性。自动化若只读 `message` 不会得出相反结论；只有**按 `success` 分支**的自动化才会误判。
- **但确实是假阴性。** 「停止失败」是关于目标达成的**相反结论**，且没有任何字段能救它（`Game not running` 连 pid 都不回）。若调用方据此重启游戏、或反复 force、或转人工排查，就产生了「每次都要重踩 / 系统性浪费往返」—— 符合 🟠。
- **(b) 的误杀风险高于本次观察。** 吐槽板只报了「难判断」，但 `:1184` 的无条件清空意味着：**玩家手动开着另一个 RimWorld 实例时，第二次 `stop_game` 可能杀掉玩家自己那个。** 这条比文案问题严重，但本报告**没有**实测证据证明发生过，标注为**风险推演（基于代码，非实测）**。这一条建议在修复时一并处理，即使维护者认为 [05] 只值 🟠。
- **不涉及假成功**，不涉及静默烧 token 的大额开销。

## 责任归属

**本项目，纯本仓库代码，无第三方成分。**

- 依据：`MCP/index.js:1179-1208` 全部逻辑在这里，`stopGame` 不调 GABP、不调 vanilla API，只用 `taskkill` / `tasklist`。改它不碰 vanilla、不碰 RimBridgeServer、不碰 UnityExplorer/MonoMod。
- 不是 vanilla 的锅：`taskkill /PID` 发 WM_CLOSE 后进程需要时间退栈，这是 Windows 与 Mono 停机的固有行为；项目代码里 `await new Promise(...2000)` 后再复查（`:1197-1198`）**已经承认了这个时序**，问题出在承认之后的**表达方式**，不是时序处理本身。
- 不是文档缺失 —— 但**也有一半是文档缺失**：`:1850` 的工具描述与 `:2924` 的前置条件都没说明「已停止」会返回 `success:false`。两条都得改才算修干净。

## 修的话怎么修

方向（实现交给 PLAN 角色），全部落在 `MCP/index.js`：

1. **让 `success` 表达终态，而不是动作 —— `MCP/index.js:1186-1188`（必做）**
   把「未找到进程」返回成 `success: true` + `message: 'Game not running'`，另加一个 `alreadyStopped: true` 供需要区分的调用方使用。理由：`stop_game` 的自然契约是「调用结束时游戏应当不在」，这个契约达成就该算成功。这是最小、最对症的一改。

2. **把「已发起关闭」这个状态显式返回 —— `MCP/index.js:1200-1202`（必做）**
   现状 `{success:false, message:'Game still closing, may need force', pid:target}` 改为携带三段信息：`requested: true`（WM_CLOSE 已送达）、`stillRunning: true`（复查时仍在）、`observedPid: target`，并把文案改成明确的「关闭请求已发出，进程仍在退出中；**不建议**立即升级 force，可稍候复查」。`pid` 字段名保持不变以免破坏既有调用方。

3. **`selfStartedPid` 只在真正停止后清 —— `MCP/index.js:1183-1185`（必做，消掉误杀面）**
   把 `:1184` 的无条件 `selfStartedPid = null;` 移到成功路径（`:1204` 返回前）与「进程确实已不存在」路径上；在 `Game still closing` 路径上**保留**它，使后续调用仍能定向到同一个 PID，而不是回退到全局 `findRimWorldProcess()`。这一条同时满足 `README.md:53` 声称的不变量。

4. **`force` 路径加存在性前置 —— `MCP/index.js:1191-1195`（可选，防御）**
   `force:true` 时若 `findRimWorldProcess()` 返回 null 就不要再拼 `taskkill /F /PID <dead>`：该命令会非零退出并抛异常，被 `:1205` 的 catch 吞成 `Stop failed: ...`，制造第三种误导文案。先查后杀。

5. **文档层（必做）**
   - `MCP/index.js:1850` 的 `description` 补一句：`调用结束时游戏应当已停止；已停止时返回 success:true + alreadyStopped:true`。
   - `MCP/index.js:2924` 的 `TOOL_PRECONDITION.stop_game` 从 `'游戏运行中'` 改为不误导的表述（现状会强化「不满足前置 = 失败」的误读）。
   - `README.md:66` 的工具表同句。

6. **不建议做的**：在 `stopGame` 内部把 2 秒等待改成循环等待直到进程消失。`taskkill` 的退出语义本身不确定（可能先退出再让子进程收尾），无界等待会把 MCP 的 stdio 调用卡住，得不偿失。**保持「观测 + 如实返回」，不要替调用方做决定** —— 这与 `triage/01-…md` 第「不建议做的」同一条纪律。

## 是否已被现有计划覆盖

**否。**

- `docs/superpowers/plans/` 下只有 `2026-09-27-ue-csharp-console-roslyn-upgrade.md`（12 个任务，任务清单见 `triage/01-execute-debug-action-false-success.md` 的同节）。全文无 `stop_game`、无 `stopGame`、无 `taskkill` / `selfStartedPid` 相关任务；任务 12 只改 `MCP/index.js:2937` 的 `execute_csharp_code` 文案。
- `MCP/logs/` 与 `docs/` 下没有针对该行为的既有设计文档。
- `docs/issue-triage/plans/` 目录**当前不存在**；同角色产物只有 `triage/06-debug-action-path-format.md`，与本条无关。
- 补充事实：本仓库**不是 git 仓库**（`git log` 返回 `fatal: not a git repository`），因此无法用提交历史判断 `selfStartedPid = null;` 是何时、为哪次需求引入的。若维护者需要溯源，只能靠 `MCP/index.js:1181` 的注释「P1-MCP-2」去对当时的计划文档，而该计划不在本仓库内。

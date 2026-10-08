# stop_game 进程归属与返回状态语义 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让 `stop_game` 只杀它自己登记过的那个 RimWorld 进程（杜绝杀掉玩家手动开的另一个实例），并让返回体的 `success` 表达「调用结束时游戏已经停止」这一终态，而不是「我执行了一次 kill」。

**架构：** 把 `stopGame` 里的**归属判定**和**返回体构造**抽成 `MCP/stopPolicy.js` 里的纯函数（无 I/O、依赖注入），`MCP/index.js` 只保留 `tasklist` / `taskkill` 的实际调用。单测直接断言纯函数，**全程不启动、不查询、不杀死任何进程**。这是照抄本仓库既有先例 `MCP/longTask/timeoutPolicy.js` + `MCP/test/timeoutPolicy.test.js` 的做法。

**技术栈：** Node.js v24.13.0（本机实测版本）、ESM（`MCP/package.json` 的 `"type": "module"`）、内置测试运行器 `node --test`。无新依赖。

---

## 0. 给零上下文工程师的前置阅读

### 0.1 这个工具现在长什么样（全部行号已用 `[System.IO.File]::ReadAllLines` 复核，2026-10-01）

`MCP/index.js` 共 **5460** 行。`stopGame` 在 **1179-1208**，完整现状：

```javascript
// MCP/index.js:1179-1208
async function stopGame(force = false) {
  try {
    // P1-MCP-2：优先按 selfStartedPid 定向停止（只杀本服务启动的进程，不误伤玩家手动开的其它实例）。
    // 自启 PID 失效或无时回退 findRimWorldProcess()；只有两者都无才判定未运行。
    let pid = selfStartedPid && await isPidAlive(selfStartedPid) ? selfStartedPid : null;   // :1183
    selfStartedPid = null;                                                                  // :1184
    const proc = pid ? null : await findRimWorldProcess();                                  // :1185
    if (!pid && !proc) {
      return { success: false, message: 'Game not running' };                                // :1186-1188
    }
    const target = pid || Number(proc.pid);                                                 // :1189

    if (force) {
      await execAsync(`taskkill /F /PID ${target}`);                                         // :1192
    } else {
      await execAsync(`taskkill /PID ${target}`);                                            // :1194
    }

    await new Promise(resolve => setTimeout(resolve, 2000));                                // :1197
    const still = await findRimWorldProcess();                                              // :1198

    if (still && Number(still.pid) === target) {
      return { success: false, message: 'Game still closing, may need force', pid: target };// :1200-1202
    }

    return { success: true, message: 'Game closed', pid: target };                          // :1204
  } catch (error) {
    return { success: false, message: `Stop failed: ${error.message}` };                    // :1205-1206
  }
}
```

工具入口 `MCP/index.js:4371-4382`：

```javascript
    case 'stop_game': {
      const result = await stopGame(args?.force ?? false);
      dpaRoundActive = false;   // 游戏结束：DPA 记账清零（§5）
      if (gabpBridge) {
        try { gabpBridge.disconnect(); } catch (e) { log('WARN', `GABP 断开异常: ${e.message}`); }
      }
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }
```

### 0.2 相关基础设施（行号同样已复核）

| 位置 | 内容 |
|---|---|
| `MCP/index.js:45` | `let selfStartedPid = null;` —— 全服务唯一的「我们启动过哪个游戏」的记忆 |
| `MCP/index.js:1013` | `selfStartedPid = Number(data.pid);`（`start_game` 走桥接 launch 成功时登记） |
| `MCP/index.js:1044` | `selfStartedPid = Number(already.pid);`（桥接 launch 失败但**检测到已有实例**时登记） |
| `MCP/index.js:1072` | `selfStartedPid = Number(process.pid);`（`start_game` 收尾时按 `findRimWorldProcess()` 覆盖登记） |
| `MCP/index.js:878-890` | `findProcess(name)`：跑 `tasklist /FI "IMAGENAME eq <name>" /FO CSV /NH`，用 `.find(l => l)` 取**第一个**非空行 |
| `MCP/index.js:893-895` | `findRimWorldProcess()` = `findProcess('RimWorldWin64.exe')` |
| `MCP/index.js:899-907` | `isPidAlive(pid)`：跑 `tasklist /FI "PID eq <pid>" /NH`，用 `/\.exe\s+\d+/i` 判存活 |
| `MCP/index.js:34` | `const execAsync = promisify(exec);` —— `taskkill` 失败会 throw |
| `MCP/index.js:1850` | 工具 schema 里 `stop_game` 的 `description: 'Stop RimWorld game'`（整个 schema 在 1848-1861） |
| `MCP/index.js:2924` | `TOOL_PRECONDITION` 里 `stop_game: '游戏运行中'`（表在 2922-2927） |
| `MCP/index.js:3332-3343` | `toolPrecondition(name)` —— **纯元数据**，只在 `:3666` 和 `:3926` 塞进 `mcp_help` 输出，不做任何调用拦截。所以改 `:2924` 的文案是安全的 |
| `README.md:53` | 声称 `selfStartedPid` 提供「防止重复启动与定向停止 / 误杀风险」 |
| `README.md:66` | 工具表里 `start_game / stop_game \| 通过 Steam 或直接启动 / 正常或强制关闭游戏` |
| `Languages/ChineseSimplified/Keyed/UESDdebuger.xml:29` | `停止 RimWorld 游戏进程`（模组游戏内 UI 文案，与 MCP 返回语义无关，见 §4 说明） |

### 0.3 本仓库没有 git

`C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger` **不是 git 仓库**（`.git` 不存在，`git rev-parse` 返回 `fatal: not a git repository`）。因此本计划**没有 Commit 步骤**，每个任务末尾用「改动清单 + 全量回归」作为检查点。

### 0.4 测试基础设施（必须用项目真实的命令）

- `MCP/package.json:9` → `"test": "node --test \"test/*.test.js\""`
- 测试文件放在 `MCP/test/*.test.js`，用 Node 内置的 `node:test` + `node:assert/strict`，ESM `import`。
- 导入 `../index.js` **之前必须**先设 `process.env.UESD_MCP_NO_START = '1'`（守卫在 `MCP/index.js:5406-5413`），否则会真的起服务器监听 3000 端口。本计划的新测试**不需要**导入 `index.js`，只导入新建的纯模块 —— 更安全。
- 现成参考：`MCP/test/quickTest.test.js`（`sliceSource` 源码护栏写法）、`MCP/test/timeoutPolicy.test.js`（纯策略模块单测写法）。
- 本机实测 `node --version` = **v24.13.0**，`npm --version` = **11.6.2**（`node --test` 的 glob 参数在此版本受支持）。

**运行命令（下面每个任务里都用它）：**

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --test test/stopGamePolicy.test.js     # 单跑本次新增的用例
npm test                                      # 全量回归（9 个既有 .test.js + 新增）
```

---

## 1. 本计划要修的四条缺陷（已逐条复核，附证据）

### 缺陷 A：`success:false` 把「游戏已经不在了」报成失败 —— `MCP/index.js:1186-1188`

`success` 的实际语义是「我做完了一次 kill 动作」，而不是「你要求的终态（游戏已停止）已达成」。`stop_game` 的自然契约是前者蕴含后者，所以**本来就没在跑应该算成功**。

这条同时解释了吐槽板上那个「竞态错觉」：`Game still closing` → 紧接着 `Game not running` 看着像矛盾，其实只是同一个进程先后被观察到两种状态，而 `success:false` 让两次都读成「失败」，于是第二次拿到的信息量**反而比第一次少**（连 `pid` 都没有）。

### 缺陷 B：`selfStartedPid` 被无条件清空 —— `MCP/index.js:1184`

`selfStartedPid = null;` 在**每次**调用开头执行，连「刚发出关闭信号、2 秒后进程还在」这种正常路径也会执行。于是第二次调用必然回退到 `findRimWorldProcess()`（`:1185`），匹配 `tasklist` 里的**第一个** `RimWorldWin64.exe`，而不是原来登记的那个。`README.md:53` 声称的防误杀不变量，从第一次调用起就不再成立。

### 缺陷 C：复查用的是「第一个实例」而不是「我杀的那个」 —— `MCP/index.js:1198-1200`

```javascript
const still = await findRimWorldProcess();          // :1198  ← tasklist 的第一个匹配
if (still && Number(still.pid) === target) {         // :1200  ← 拿别人的 PID 跟 target 比
```

`findProcess`（`:878-890`）用 `.find(l => l)` 只返回**第一行**。机器上跑着 ≥2 个 RimWorld 实例时：

- 若排序靠前的那个**不是** `target` → `still.pid !== target` → 直接落到 `:1204` 返回 `{success:true, message:'Game closed'}`，
  **而 `target` 其实还在运行**。这是一次**假成功**。
- 反过来，若靠前的那个恰好就是 `target` 才走 `:1200`。

正确的复查是 `await isPidAlive(target)`（`:899-907` 已经写好，`:1183` 已经在用）。

### 缺陷 D：`taskkill` 打在已经死掉的 PID 上会造出第三种误导文案

`execAsync`（`:34`）在 `taskkill` 非零退出时 throw，被 `:1205` 的 catch 吞成 `Stop failed: <系统原文>`。这产生一个既不是「Game closed」也不是「still closing」的第三种失败文案，同样没法让调用方判断游戏到底停了没有。

### 缺陷 A/B/C 串起来 = 「第二次 `stop_game` 可能杀掉玩家手动开的另一个 RimWorld 实例」

**结论：成立。** 复现所需的完整推演（每一步都对应上面复核过的行号）：

1. 玩家手动开着实例 **B**。此前 MCP 通过 `start_game` 启动过实例 **A**，因此 `selfStartedPid === A`（登记点在 `:1013` / `:1044` / `:1072`）。
2. 第一次 `stop_game{force:false}`：
   - `:1183` `isPidAlive(A)` 为真 → `pid = A`
   - `:1184` `selfStartedPid = null` ← **归属记忆在这里被抹掉**
   - `:1189` `target = A`；`:1194` 发出 WM_CLOSE
   - `:1197` 等 2 秒；`:1198` 复查；`:1200` 判定 → 返回 `Game still closing, may need force, pid: 24364`
3. 这 2 秒之后 **A 自行退出**（Windows 上 WM_CLOSE 之后 Unity 退栈本来就需要时间，这正是 `:1197` 那句 `setTimeout(2000)` 在承认的事），**B 还活着**。
4. 第二次 `stop_game{force:true}`（吐槽板上那条就是这么发的）：
   - `:1183` `selfStartedPid` 已是 `null` → `pid = null`
   - `:1185` 回退 `findRimWorldProcess()` → 此时 tasklist 里唯一的 RimWorld 是 **B** → `proc = {pid: B}`
   - `:1189` `target = B`
   - `:1192` `taskkill /F /PID B` → **玩家手动开的那个实例被强杀。**

**触发条件**：同时存在 ≥2 个 `RimWorldWin64.exe`；且对同一次停止动作调用了 ≥2 次 `stop_game`（第一次非 force 拿到 `still closing`、随后升级 force，是最自然的路径 —— 正是吐槽板上记录的那一次）。

**本计划怎么消掉它**：核心规则改为「**本服务登记过的 PID 若已退出，就直接报「本来就没在跑」，绝不再回退到全局扫描去找别的实例杀**」。回退只在「本服务从来没登记过任何 PID」时启用（那种情况下不存在「本服务拥有的那个实例」被误当成别人的风险，那是调用方明确要求停游戏、且机器上只有/只有它自己的场景）。

---

## 2. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/stopPolicy.js` | **新建** | `stop_game` 的纯策略模块：进程归属判定 `resolveStopTarget`、返回体构造 `buildStopResult`、`taskkill` 错误分类 `isAlreadyGoneError`。零 I/O、零导入，纯函数，可直接单测。 |
| `MCP/index.js` | **修改** | `stopGame`（1179-1208）改写为「探针 → 交给纯函数决策 → 执行 taskkill → 复查 → 交给纯函数构造返回体」；`:1181-1182` 注释更新；`:1850` 工具描述；`:2924` 前置条件文案；`:24-33` 附近新增一行 import。 |
| `MCP/test/stopGamePolicy.test.js` | **新建** | `stopPolicy.js` 的单测 + 针对 `index.js` 的源码护栏回归。 |
| `README.md` | **修改** | `:66` 工具表补一句 `stop_game` 的返回语义。`:53` 的防误杀不变量表述**无需改动**（修完之后它才真正成立），只加一条测试守住它。 |
| `Languages/ChineseSimplified/Keyed/UESDdebuger.xml:29`、`Languages/English/Keyed/UESDdebuger.xml:29` | **不改** | 这是模组**游戏内 UI** 的文案（`ToolDesc.stop_game` = 「停止 RimWorld 游戏进程」），不喂给 MCP 客户端，也不表达返回语义。§4 有专门验证步骤确认无需同步。 |
| `MCP/toolConfig.json` | **不改** | 只有 `"stop_game":true` 的暴露开关，与本条无关。 |

---

## 3. 新的返回体规格（实现时以这张表为准）

`stop_game` 的四个出口，加上一个错误出口：

| 情形 | 返回体 |
|---|---|
| 全服务从未登记过 PID，且全局也没找到 RimWorld | `{ success: true, message: 'Game not running', alreadyStopped: true, stopped: false }` |
| 登记过的 PID 已经退出 | `{ success: true, message: 'Game not running（已登记的 PID <n> 此前已退出；未扫描其它实例，以免误杀玩家手动启动的 RimWorld）', alreadyStopped: true, stopped: false, pid: <n>, pidSource: 'tracked' }` |
| 已发出关闭信号，2 秒后**仍在运行** | `{ success: false, message: '关闭请求已发出（已向 PID <n> 发送关闭信号），2 秒后进程仍在运行：正常退出需要更长时间', pid: <n>, pidSource: 'tracked'\|'fallback', closeRequested: true, stillRunning: true, alreadyStopped: false, guidance: '不建议立刻重复调用或升级 force：重复调用会再发一次关闭信号，force 会直接终止进程、可能丢失未保存的进度。等几秒后用 get_game_status 复查即可。' }` |
| 已发出关闭信号，2 秒后**已退出** | `{ success: true, message: 'Game closed', pid: <n>, pidSource: 'tracked'\|'fallback', closeRequested: true, stillRunning: false, alreadyStopped: false }` |
| `taskkill` 失败且**不是**「进程已不存在」 | `{ success: false, message: 'Stop failed: <系统原文>' }` |

### 向后兼容影响（**这是一次语义变更，必须显式记录**）

1. **「游戏本来就没在跑」从 `success:false` 变成 `success:true`。**
   - **变得更好**：按 `success` 分支的自动化不再把已达成的事实当失败，从而停止「反复 force / 重启游戏 / 转人工排查」这类无效动作。
   - **可能变坏的**：如果有调用方靠 `success === false` + `message === 'Game not running'` 来判断「这台机器上从来没有游戏在跑」（比如据此决定要不要 `start_game`），它必须改看 `alreadyStopped === true`。`message` 字面量保持 `'Game not running'` 不变，所以只读 `message` 的调用方**不受影响**。
2. **新增字段全是增量**：`alreadyStopped` / `stopped` / `closeRequested` / `stillRunning` / `pidSource` / `guidance`。既有字段 `success` / `message` / `pid` 一个都没删，`pid` 字段名在所有分支都保留（含原来连 pid 都不给的 `not running` 分支，现在也会在有登记 PID 时带上）。
3. **`'Game still closing, may need force'` 这个字面量消失。** 有调用方按它做字符串匹配的会失配 —— 这正是本条要修的东西，属于有意破坏。
4. `MCP/index.js:4371-4382` 的工具入口、`dpaRoundActive = false`、`gabpBridge.disconnect()` 三件事**一行都不改**（见 §5 的回归约束）。

---

## 4. 任务

### 任务 1：抽出纯策略模块 `MCP/stopPolicy.js`

**文件：**
- 创建：`MCP/stopPolicy.js`
- 测试：`MCP/test/stopGamePolicy.test.js`

- [ ] **步骤 1：编写失败的测试**

新建 `MCP/test/stopGamePolicy.test.js`，完整内容如下（一次写完，后面步骤只改这一个文件）：

```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 本文件只导入纯策略模块，**不导入 ../index.js**：这样连 UESD_MCP_NO_START 守卫都不需要，
// 更重要的是——测试全程不会 tasklist / taskkill / 启动 / 杀死任何进程。
import { resolveStopTarget, buildStopResult, isAlreadyGoneError } from '../stopPolicy.js';

const here = path.dirname(fileURLToPath(import.meta.url));
const indexSource = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8');

// 取 index.js 里 stopGame 的函数体（源码级回归护栏用）
function stopGameBody() {
  const s = indexSource.indexOf('async function stopGame');
  assert.ok(s >= 0, 'index.js 里找不到 async function stopGame');
  const e = indexSource.indexOf('// Get game status', s);
  assert.ok(e > s, 'index.js 里找不到 stopGame 之后的 "// Get game status" 结束标记');
  return indexSource.slice(s, e);
}

// ===========================================================================
// 归属判定：三种状态（自启动 / 非自启动 / 已退出）
// ===========================================================================

test('归属：登记过的 PID 仍存活 → 杀它，来源标记 tracked', () => {
  const d = resolveStopTarget({ trackedPid: 24364, trackedAlive: true, firstFoundPid: null });
  assert.deepEqual(d, { action: 'kill', pid: 24364, pidSource: 'tracked' });
});

test('归属：登记过的 PID 已退出，即使扫到别的实例也**不再杀它**（这就是误杀回归）', () => {
  const d = resolveStopTarget({ trackedPid: 24364, trackedAlive: false, firstFoundPid: 17700 });
  assert.deepEqual(d, { action: 'already-stopped', pid: 24364, pidSource: 'tracked' });
  assert.notEqual(d.action, 'kill', '绝不能退化成 kill：那是杀掉玩家手动开的另一个实例');
  assert.equal(d.pid, 24364, '报告的必须是本服务登记过的那个 PID，而不是扫到的 17700');
});

test('归属：从未登记过 PID（玩家手动开的）→ 回退杀扫到的那个，来源标记 fallback', () => {
  const d = resolveStopTarget({ trackedPid: null, trackedAlive: false, firstFoundPid: 17700 });
  assert.deepEqual(d, { action: 'kill', pid: 17700, pidSource: 'fallback' });
});

test('归属：既没登记也没扫到 → not-running', () => {
  const d = resolveStopTarget({ trackedPid: null, trackedAlive: false, firstFoundPid: null });
  assert.deepEqual(d, { action: 'not-running', pid: null, pidSource: null });
});

test('归属：登记过但存活，同时扫到另一个实例 → 仍然只杀登记的那个', () => {
  const d = resolveStopTarget({ trackedPid: 24364, trackedAlive: true, firstFoundPid: 17700 });
  assert.equal(d.pid, 24364);
  assert.equal(d.pidSource, 'tracked');
});

test('归属：PID 归一化——字符串数字接受；0 / 负数 / NaN / null 一律当没有', () => {
  assert.equal(resolveStopTarget({ trackedPid: '24364', trackedAlive: true }).pid, 24364);
  assert.deepEqual(resolveStopTarget({ trackedPid: null, trackedAlive: true, firstFoundPid: null }).action, 'not-running');
  assert.deepEqual(resolveStopTarget({ trackedPid: null, trackedAlive: false, firstFoundPid: 0 }).action, 'not-running');
  assert.deepEqual(resolveStopTarget({ trackedPid: null, trackedAlive: false, firstFoundPid: -5 }).action, 'not-running');
  assert.deepEqual(resolveStopTarget({ trackedPid: null, trackedAlive: false, firstFoundPid: NaN }).action, 'not-running');
  assert.deepEqual(resolveStopTarget({ trackedPid: null, trackedAlive: false, firstFoundPid: undefined }).action, 'not-running');
});

test('归属：完全不给参数不炸，退化为 not-running', () => {
  assert.deepEqual(resolveStopTarget(), { action: 'not-running', pid: null, pidSource: null });
});

// ===========================================================================
// 返回体语义：success 表达终态，不表达动作
// ===========================================================================

test('返回体：not-running → success:true + alreadyStopped:true（语义变更的核心）', () => {
  const r = buildStopResult({ action: 'not-running', pid: null, pidSource: null, force: false, stillAlive: false });
  assert.equal(r.success, true);
  assert.equal(r.alreadyStopped, true);
  assert.equal(r.stopped, false);
  assert.equal(r.message, 'Game not running');
});

test('返回体：already-stopped → success:true，且文案点明没有扫描其它实例', () => {
  const r = buildStopResult({ action: 'already-stopped', pid: 24364, pidSource: 'tracked', force: false, stillAlive: false });
  assert.equal(r.success, true);
  assert.equal(r.alreadyStopped, true);
  assert.equal(r.pid, 24364);
  assert.match(r.message, /24364/);
  assert.match(r.message, /未扫描其它实例/);
});

test('返回体：仍在运行 → success:false，但必须带上「已发出关闭请求」这一状态维度', () => {
  const r = buildStopResult({ action: 'kill', pid: 24364, pidSource: 'tracked', force: false, stillAlive: true });
  assert.equal(r.success, false);
  assert.equal(r.closeRequested, true);
  assert.equal(r.stillRunning, true);
  assert.equal(r.alreadyStopped, false);
  assert.equal(r.pid, 24364);
  assert.match(r.guidance, /get_game_status/);
});

test('返回体：仍在运行时文案**不得**再出现 "may need force"（吐槽板 [05] 的原始症状）', () => {
  for (const force of [false, true]) {
    const r = buildStopResult({ action: 'kill', pid: 24364, pidSource: 'tracked', force, stillAlive: true });
    assert.ok(!/may need force/i.test(r.message), `force=${force} 时文案仍诱导升级 force: ${r.message}`);
    assert.ok(!/may need force/i.test(r.guidance || ''), `force=${force} 时 guidance 仍诱导升级 force: ${r.guidance}`);
  }
});

test('返回体：force 分支的仍在运行文案与非 force 分支不同（要说清已经强杀过）', () => {
  const soft = buildStopResult({ action: 'kill', pid: 24364, pidSource: 'tracked', force: false, stillAlive: true });
  const hard = buildStopResult({ action: 'kill', pid: 24364, pidSource: 'tracked', force: true, stillAlive: true });
  assert.notEqual(soft.message, hard.message);
  assert.match(hard.message, /强制关闭/);
  assert.equal(hard.closeRequested, true);
  assert.equal(hard.stillRunning, true);
});

test('返回体：已退出 → success:true "Game closed"，带 pidSource', () => {
  const r = buildStopResult({ action: 'kill', pid: 24364, pidSource: 'tracked', force: false, stillAlive: false });
  assert.equal(r.success, true);
  assert.equal(r.message, 'Game closed');
  assert.equal(r.closeRequested, true);
  assert.equal(r.stillRunning, false);
  assert.equal(r.alreadyStopped, false);
  assert.equal(r.pid, 24364);
  assert.equal(r.pidSource, 'tracked');
});

// ===========================================================================
// taskkill 错误分类
// ===========================================================================

test('taskkill 错误分类：「进程不存在」判为已消失（不算 Stop failed）', () => {
  assert.equal(isAlreadyGoneError({ message: 'Command failed: taskkill /F /PID 24364', stderr: 'ERROR: The process "24364" not found.' }), true);
  assert.equal(isAlreadyGoneError({ message: 'x', stdout: '没有找到进程 24364' }), true);
  assert.equal(isAlreadyGoneError({ message: 'x', stderr: '错误: 找不到指定的进程。' }), true);
  assert.equal(isAlreadyGoneError({ message: 'x', stderr: 'ERROR: No running instance of the task.' }), true);
});

test('taskkill 错误分类：真失败（权限等）不得被误判成已消失', () => {
  assert.equal(isAlreadyGoneError({ message: 'Command failed', stderr: 'ERROR: Access is denied.' }), false);
  assert.equal(isAlreadyGoneError({ message: 'Command failed', stderr: '拒绝访问。' }), false);
  assert.equal(isAlreadyGoneError(null), false);
  assert.equal(isAlreadyGoneError(undefined), false);
  assert.equal(isAlreadyGoneError({}), false);
});

// ===========================================================================
// 源码护栏：防止这四类缺陷被改回来
// ===========================================================================

test('护栏：stopGame 不得再出现「读完 selfStartedPid 立刻无条件清空」的回归写法', () => {
  const body = stopGameBody();
  assert.ok(
    !/await isPidAlive\([^)]*\)\s*\?\s*selfStartedPid\s*:\s*null;\s*\r?\n\s*selfStartedPid\s*=\s*null;/.test(body),
    'stopGame 开头又出现了「读完即无条件清空」——这会让第二次调用失去定向能力、进而误杀玩家手动开的实例',
  );
});

test('护栏：stopGame 的归属判定必须委托给 resolveStopTarget', () => {
  assert.match(stopGameBody(), /resolveStopTarget\(/, 'stopGame 必须走 resolveStopTarget 做归属判定');
});

test('护栏：stopGame 的「是否仍在运行」复查必须按 PID 判定，不得拿 tasklist 首个匹配比对', () => {
  const body = stopGameBody();
  assert.match(body, /isPidAlive\([^)]*pid[^)]*\)/i, '复查应使用 isPidAlive(target)');
  assert.ok(!/still\.pid\s*===\s*target/.test(body), '不得再用 findRimWorldProcess() 的首个匹配与 target 比对');
});

test('护栏：stopGame 不得再把「找不到进程」报成 success:false', () => {
  assert.ok(!/success:\s*false,\s*message:\s*'Game not running'/.test(stopGameBody()),
    '"Game not running" 是终态已达成，必须是 success:true');
});

test('护栏：本测试文件绝不真的调用 stop_game（会杀掉并行会话正在调试的游戏）', () => {
  assert.ok(!/handleToolCall\(\s*['"]stop_game/.test(fs.readFileSync(path.join(here, 'stopGamePolicy.test.js'), 'utf-8')),
    '测试文件里出现了对 stop_game 的真实调用');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --test test/stopGamePolicy.test.js
```

预期：整体 FAIL，报 `Cannot find module '../stopPolicy.js'`（`ERR_MODULE_NOT_FOUND`）。**此时不要建空壳文件绕过** —— 保持这个失败，直到步骤 3 把模块写出来。

- [ ] **步骤 3：编写最少实现代码**

新建 `MCP/stopPolicy.js`，完整内容：

```javascript
/**
 * stop_game 的进程归属判定与返回体构造（纯逻辑，无 I/O、无外部导入，便于单测）。
 *
 * 为什么要有这个模块：stopGame 里最危险的一段是「杀哪个 PID」。
 * 原实现每次调用开头就无条件把 selfStartedPid 清空，于是第二次调用被迫回退到
 * findRimWorldProcess()——那个函数只返回 `tasklist` 里的**第一个** RimWorldWin64.exe。
 * 玩家手动开着另一个实例时，第二次 stop_game 就会把玩家那个杀掉。
 * 把判定抽成纯函数后，这条规则可以被单测直接钉死，且测试全程不碰任何进程。
 *
 * 判定规则（按优先级）：
 *   1. 登记过且仍存活      → 杀它                （action:'kill',        pidSource:'tracked'）
 *   2. 登记过但已退出      → 不杀，报「已退出」   （action:'already-stopped'）
 *      ↑ 这是防误杀的关键：**绝不在这里回退去扫别的实例**
 *   3. 从未登记过          → 杀扫到的那个        （action:'kill',        pidSource:'fallback'）
 *   4. 从未登记且也没扫到  → 报未运行            （action:'not-running'）
 */

/** PID 归一化：只接受正整数；其余（字符串数字先转number）一律视作「没有」 */
function normalizePid(v) {
  const n = Number(v);
  return Number.isInteger(n) && n > 0 ? n : null;
}

/**
 * 决定这一次 stop_game 该杀谁 / 要不要杀。
 * @param {object} input
 * @param {number|null} input.trackedPid   本服务登记过的 PID（selfStartedPid），没有则 null
 * @param {boolean}      input.trackedAlive 该 PID 当前是否存活
 * @param {number|null}  input.firstFoundPid 全局扫描（tasklist）找到的第一个 RimWorld PID
 * @returns {{action:'kill'|'already-stopped'|'not-running', pid:number|null, pidSource:'tracked'|'fallback'|null}}
 */
export function resolveStopTarget({ trackedPid = null, trackedAlive = false, firstFoundPid = null } = {}) {
  const t = normalizePid(trackedPid);
  const f = normalizePid(firstFoundPid);
  if (t != null) {
    return trackedAlive === true
      ? { action: 'kill', pid: t, pidSource: 'tracked' }
      : { action: 'already-stopped', pid: t, pidSource: 'tracked' };
  }
  if (f != null) {
    return { action: 'kill', pid: f, pidSource: 'fallback' };
  }
  return { action: 'not-running', pid: null, pidSource: null };
}

/**
 * 由归属判定 + 复查结果构造 MCP 返回体。
 *
 * success 的语义：**调用结束时「游戏应当已停止」这一终态是否达成**，
 * 而不是「我是否执行了一次 kill 动作」。所以「本来就没在跑」是 success:true。
 * 需要区分「本次真的关了」还是「本来就没在跑」的调用方看 alreadyStopped。
 */
export function buildStopResult({ action, pid, pidSource, force, stillAlive }) {
  if (action === 'not-running') {
    return {
      success: true,
      message: 'Game not running',
      alreadyStopped: true,
      stopped: false,
    };
  }
  if (action === 'already-stopped') {
    return {
      success: true,
      message: `Game not running（已登记的 PID ${pid} 此前已退出；未扫描其它实例，以免误杀玩家手动启动的 RimWorld）`,
      alreadyStopped: true,
      stopped: false,
      pid,
      pidSource,
    };
  }
  // action === 'kill'：已经发过关闭信号，看复查结果
  if (stillAlive) {
    return {
      success: false,
      message: force
        ? `强制关闭已下达，但 PID ${pid} 仍在运行；请稍候后调 get_game_status 复查`
        : `关闭请求已发出（已向 PID ${pid} 发送关闭信号），2 秒后进程仍在运行：正常退出需要更长时间`,
      pid,
      pidSource,
      closeRequested: true,
      stillRunning: true,
      alreadyStopped: false,
      guidance: '不建议立刻重复调用或升级 force：重复调用会再发一次关闭信号，force 会直接终止进程、可能丢失未保存的进度。等几秒后用 get_game_status 复查即可。',
    };
  }
  return {
    success: true,
    message: 'Game closed',
    pid,
    pidSource,
    closeRequested: true,
    stillRunning: false,
    alreadyStopped: false,
  };
}

/**
 * 判断一次 taskkill 失败是不是「进程本来就已经不在了」。
 * taskkill 对不存在的 PID 非零退出并 throw，若不分类就会被外层 catch 吞成
 * `Stop failed: ...`——第三种既不是「Game closed」也不是「still closing」的误导文案。
 * 注意：必须能区分「找不到进程」与「找不到/无权限访问」，后者是真失败。
 */
export function isAlreadyGoneError(err) {
  if (!err) return false;
  const text = `${err.stdout || ''} ${err.stderr || ''} ${err.message || ''}`;
  return /not\s+found|no running instance|没有找到|找不到指定的进程|未找到进程/i.test(text);
}

export default { resolveStopTarget, buildStopResult, isAlreadyGoneError };
```

- [ ] **步骤 4：运行测试验证通过**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --test test/stopGamePolicy.test.js
```

预期：归属判定 / 返回体 / 错误分类 12 条全 PASS；**5 条 `护栏：` 开头的那几条此时仍 FAIL** —— 这是正常的，它们要等任务 2 改完 `index.js` 才转绿。这是本任务的交付边界：**纯模块自身全绿，护栏留给任务 2**。

- [ ] **步骤 5：改动清单（本仓库不是 git 仓库，无 Commit 步骤）**

- 新增：`MCP/stopPolicy.js`
- 新增：`MCP/test/stopGamePolicy.test.js`
- 未触碰 `MCP/index.js`、未触碰任何既有文件

---

### 任务 2：把 `stopGame` 接到策略模块，消掉误杀与假成功

**文件：**
- 修改：`MCP/index.js:31-32`（新增一行 import）
- 修改：`MCP/index.js:1179-1208`（整个 `stopGame` 函数，含 `:1181-1182` 的注释）

- [ ] **步骤 1：确认测试当前是红的（护栏已写好）**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --test test/stopGamePolicy.test.js
```

预期：5 条 `护栏：` 用例 FAIL，分别报「又出现了『读完即无条件清空』」「必须走 resolveStopTarget」「复查应使用 isPidAlive(target)」「不得再用 tasklist 首个匹配与 target 比对」「'Game not running' 必须是 success:true」。

- [ ] **步骤 2：加 import**

在 `MCP/index.js:32`（`import { appendRecord, readOrphans, filterOrphans } from './longTask/journal.js';`）**之后**新起一行：

```javascript
import { resolveStopTarget, buildStopResult, isAlreadyGoneError } from './stopPolicy.js';
```

不要动 `:24-31` 的任何现有 import 行。

- [ ] **步骤 3：整体替换 `stopGame`**

把 `MCP/index.js:1179-1208`（从 `async function stopGame(force = false) {` 到它闭合的 `}`）替换为下面这段。**`// Stop game` 那一行注释保留在函数上方**（`MCP/index.js:1178`），不要动它：

```javascript
async function stopGame(force = false) {
  try {
    // P1-MCP-2：归属优先。归属判定与返回体构造交给 ./stopPolicy.js 纯函数（可单测）。
    // 三条规则（详见 stopPolicy.js 顶部注释）：
    //   1) 登记过且还活着 → 杀它；
    //   2) 登记过但已退出 → **不杀**，直接报"本来就没在跑"。这里绝不回退到全局扫描，
    //      否则玩家手动开的另一个 RimWorld 实例会被误杀（原实现在 :1184 无条件清空 selfStartedPid 导致）；
    //   3) 从未登记过     → 才回退到 findRimWorldProcess()（机器上只有玩家自己那个 game 时这是想要的）。
    const trackedPid = selfStartedPid != null ? Number(selfStartedPid) : null;
    const trackedAlive = trackedPid != null ? await isPidAlive(trackedPid) : false;
    // 只有在登记的 PID 不可用时才做全局扫描 —— 扫描是 tasklist 的"第一个"匹配，不精确。
    const firstFoundPid = trackedAlive
      ? null
      : Number((await findRimWorldProcess() || {}).pid || 0) || null;

    const decision = resolveStopTarget({ trackedPid, trackedAlive, firstFoundPid });

    if (decision.action !== 'kill') {
      // not-running / already-stopped：终态已达成。没有进程被我们持有，记忆可以清。
      selfStartedPid = null;
      return buildStopResult({ ...decision, force, stillAlive: false });
    }

    let killError = null;
    try {
      if (force) {
        await execAsync(`taskkill /F /PID ${decision.pid}`);
      } else {
        await execAsync(`taskkill /PID ${decision.pid}`);
      }
    } catch (err) {
      // 进程在"判定存活"与"taskkill"之间自己退掉了 → 这不是失败，是终态已达成。
      if (!isAlreadyGoneError(err)) killError = err;
    }

    if (killError) {
      // 真失败：**不要清 selfStartedPid** —— 归属没变，下次调用还能定向到同一个 PID。
      return { success: false, message: `Stop failed: ${killError.message}` };
    }

    await new Promise(resolve => setTimeout(resolve, 2000));
    // 复查按 PID 判定存活。原来的 findRimWorldProcess() 只返回 tasklist 的第一个匹配，
    // 多实例时拿别人的 PID 跟 target 比，会把"target 还在跑"误报成"Game closed"（假成功）。
    const stillAlive = await isPidAlive(decision.pid);

    const result = buildStopResult({ ...decision, force, stillAlive });
    // 只在"确实已不在"时清登记；still-closing 必须留着，否则下一次 stop_game 会失去定向能力。
    // fallback 来源的 PID 从来不是本服务启动的，不登记为 selfStartedPid。
    selfStartedPid = (stillAlive && decision.pidSource === 'tracked') ? decision.pid : null;
    return result;
  } catch (error) {
    return { success: false, message: `Stop failed: ${error.message}` };
  }
}
```

改动后函数长度会变，后续任务里再引用行号时**必须重新用 `[System.IO.File]::ReadAllLines` 取**，不要沿用本计划里的行号。

- [ ] **步骤 4：运行测试验证通过**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --test test/stopGamePolicy.test.js
```

预期：**全部 17 条 PASS，0 FAIL。**

- [ ] **步骤 5：跑全量回归**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
npm test
```

预期：9 个既有测试文件 + 新增的 `stopGamePolicy.test.js` 全部通过。特别确认这两条既有断言没被打破：

- `MCP/test/toolchainFixes.test.js:263-268`（`'§5 start_game / stop_game 会清零 DPA 记账'`）—— 它切的是 `case 'stop_game': {` 到 `case 'get_game_status': {` 之间的**工具入口块**（`index.js:4371-4382`），本任务**没有碰那一段**，`dpaRoundActive = false` 必须还在。
- `MCP/test/toolCountConsistency.test.js` 与 `MCP/test/index.integration.test.js` —— 本任务只改函数体和加一行 import，工具数量/导出面不变。

若全量回归出现与本任务无关的既有失败，**先记录下来**，不要顺手修（本计划范围外）。

- [ ] **步骤 6：改动清单**

- 修改：`MCP/index.js:31-32` 之后新增 1 行 import
- 修改：`MCP/index.js:1179-1208` 整体替换 `stopGame`，含 `:1181-1182` 注释
- **未触碰**：`MCP/index.js:4371-4382`（工具入口）、`MCP/index.js:45`（`selfStartedPid` 声明）、`:1013/:1044/:1072`（三个登记点）、`:899-907`（`isPidAlive`）、`:878-895`（`findProcess` / `findRimWorldProcess`）

---

### 任务 3：同步对外文案（工具描述 / 前置条件 / README）

**文件：**
- 修改：`MCP/index.js:1850`（`stop_game` 的 `description`）
- 修改：`MCP/index.js:2924`（`TOOL_PRECONDITION.stop_game`）
- 修改：`README.md:66`（工具表行）
- 测试：`MCP/test/stopGamePolicy.test.js`（追加 3 条断言）

- [ ] **步骤 1：先写会失败的断言**

在 `MCP/test/stopGamePolicy.test.js` 末尾追加：

```javascript
// ===========================================================================
// 对外文案护栏：调用方必须能从工具描述/前置条件里预知返回语义
// ===========================================================================

test('护栏：工具描述必须写明 success 表达终态、以及 alreadyStopped 的存在', () => {
  const s = indexSource.indexOf("name: 'stop_game'");
  assert.ok(s > 0, "index.js 里找不到 name: 'stop_game'");
  const e = indexSource.indexOf("name: 'get_game_status'", s);
  const block = indexSource.slice(s, e);
  assert.match(block, /alreadyStopped/,
    "stop_game 的工具描述必须告诉调用方：游戏本来就没在跑时返回 success:true + alreadyStopped:true");
  assert.match(block, /stillRunning/,
    'stop_game 的工具描述必须告诉调用方：关闭请求已发出但进程仍在时返回 stillRunning:true');
});

test('护栏：前置条件不得再用"游戏运行中"这种会诱导"未满足=失败"的表述', () => {
  const s = indexSource.indexOf('const TOOL_PRECONDITION = {');
  assert.ok(s > 0, 'index.js 里找不到 TOOL_PRECONDITION');
  const e = indexSource.indexOf('};', s);
  const block = indexSource.slice(s, e);
  const line = block.split(/\r?\n/).find((l) => l.includes("stop_game:"));
  assert.ok(line, 'TOOL_PRECONDITION 里找不到 stop_game');
  assert.ok(!/stop_game:\s*'游戏运行中'/.test(line),
    `前置条件仍写着"游戏运行中"，会强化"不满足前置=调用失败"的误读: ${line}`);
});

test('护栏：README 工具表里的 stop_game 说明要能看出返回语义', () => {
  const readme = fs.readFileSync(path.join(here, '..', '..', 'README.md'), 'utf-8');
  const row = readme.split(/\r?\n/).find((l) => l.includes('`stop_game`') && l.startsWith('|'));
  assert.ok(row, 'README.md 工具表里找不到 stop_game 行');
  assert.match(row, /alreadyStopped|终态/,
    `README 工具表未说明 stop_game 的返回语义: ${row}`);
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --test test/stopGamePolicy.test.js
```

预期：3 条新护栏 FAIL，分别报「工具描述必须告诉调用方…」「前置条件仍写着"游戏运行中"…」「README 工具表未说明 stop_game 的返回语义」。

- [ ] **步骤 3：改工具描述**

把 `MCP/index.js:1850` 的这一行：

```javascript
    description: 'Stop RimWorld game',
```

替换为：

```javascript
    description: 'Stop RimWorld game。调用结束时游戏应当已停止：已停止返回 success:true + alreadyStopped:true；关闭请求已发出但进程仍在退出中返回 success:false + stillRunning:true（此时不建议立刻升级 force，用 get_game_status 复查即可）。只停止本服务登记过的那个 PID（start_game 启动的实例），不会误杀玩家手动开的其它 RimWorld 实例。',
```

同时把 `MCP/index.js:1856` 的 `force` 描述：

```javascript
          description: 'Force stop (default false)',
```

替换为：

```javascript
          description: 'Force stop：直接终止进程、可能丢失未保存的进度。仅在非 force 调用返回 stillRunning:true 且等待若干秒后仍不退出时再用（default false）',
```

- [ ] **步骤 4：改前置条件文案**

把 `MCP/index.js:2924` 的这一行：

```javascript
  stop_game: '游戏运行中',
```

替换为：

```javascript
  stop_game: '无（游戏没在运行也算调用成功：返回 success:true + alreadyStopped:true）。只停止本服务 start_game 登记过的那个实例',
```

`toolPrecondition()`（`MCP/index.js:3332-3343`）只把这句原样塞进 `mcp_help` 输出（`:3666`、`:3926`），**不做任何调用拦截**，所以这一改是纯文案、零风险。

- [ ] **步骤 5：改 README 工具表**

把 `README.md:66` 的这一行：

```markdown
| `start_game` / `stop_game` | 通过 Steam 或直接启动 / 正常或强制关闭游戏 |
```

替换为：

```markdown
| `start_game` / `stop_game` | 通过 Steam 或直接启动 / 正常或强制关闭游戏。`stop_game` 的 `success` 表示**终态是否达成**（游戏是否已停止），不是「是否执行过 kill」：本来就没在跑返回 `success:true` + `alreadyStopped:true`；关闭请求已发出但进程仍在退出中返回 `success:false` + `stillRunning:true`（此时别立刻升级 `force`，等几秒用 `get_game_status` 复查）。它只停止本服务 `start_game` 登记过的那个 PID，不会误杀玩家手动开的其它 RimWorld 实例。 |
```

`README.md:53` 那句「`selfStartedPid` 等全局状态…防止重复启动与定向停止 / 误杀风险」**不需要改动** —— 修完之后它才真正成立（改动前它是失效的）。任务 2 步骤 4 的护栏已经把这个不变量钉住了。

- [ ] **步骤 6：确认语言文件确实不需要同步**

运行（只读检查，不改文件）：

```powershell
Select-String -Path 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\Languages\*\Keyed\UESDdebuger.xml' -Pattern 'ToolDesc\.stop_game'
```

预期：只命中 `ChineseSimplified` 和 `English` 各 1 行，内容分别是「停止 RimWorld 游戏进程」和「Stop the RimWorld game process」。这两行是**模组游戏内 UI** 的标签，不喂给 MCP 客户端，也不表达返回语义 → **确认无需改动，不要动它们**。MCP 侧真正给调用方看的文案只有 `index.js:1850` 的 `description` 和 `index.js:2924` 的前置条件。

- [ ] **步骤 7：运行测试验证通过 + 全量回归**

运行：

```powershell
Set-Location 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP'
node --test test/stopGamePolicy.test.js
npm test
```

预期：单跑 **20 条全 PASS**；全量 **9 个既有文件 + 新增全部通过**。

- [ ] **步骤 8：改动清单**

- 修改：`MCP/index.js:1850`、`:1856`（工具描述 / force 描述）
- 修改：`MCP/index.js:2924`（前置条件文案）
- 修改：`README.md:66`（工具表行）
- 修改：`MCP/test/stopGamePolicy.test.js`（追加 3 条护栏）
- 确认无需改动：`README.md:53`、`Languages/*/Keyed/UESDdebuger.xml:29`、`MCP/toolConfig.json`

---

## 5. 回归约束（**不要违反**）

1. **绝不许为 `stop_game` 写会真的调用它的测试。** `MCP/test/stopGamePolicy.test.js` 里有一条护栏专门守这条。理由：本仓库此刻有一个并行会话正在用 MCP 调试 RimWorld，任何真实的 `stop_game` 调用都会把它的游戏杀掉。
2. **`MCP/index.js:4371-4382` 的工具入口块一行都不许改。** `MCP/test/toolchainFixes.test.js:263-268` 断言这段里有 `dpaRoundActive = false`；改坏它会让既有测试红。
3. **`MCP/index.js:45` 的 `selfStartedPid` 声明和 `:1013` / `:1044` / `:1072` 三个登记点都不许改。** 本计划只改**读取与清除**时机，不动**写入**时机。
4. **不要把 2 秒等待改成循环等待。** 保持 `MCP/index.js` 里那句 `await new Promise(resolve => setTimeout(resolve, 2000))` 原样。`taskkill` 的退出语义本身不确定（可能自己先退、再让子进程收尾），无界等待会把 MCP 的 stdio 调用卡死。策略是「**如实观测并返回，不替调用方做决定**」。
5. **不要动 `findProcess` / `findRimWorldProcess`。** 它们的「只返回第一个匹配」是全局行为，`get_game_status` 等多处也在用。本计划只在 `stopGame` 里不再拿它做复查。
6. **不要改 `get_game_status` 或加新工具。** 本条只修 `stop_game` 的归属判定与返回语义。

## 6. 完成标准

全部满足才算做完：

- [ ] `cd MCP; npm test` 全绿（既有 9 个文件 + 新增 1 个，0 FAIL）。
- [ ] 吐槽板上那条记录的两步序列，现在返回：`{success:false, message:'关闭请求已发出（已向 PID 24364 发送关闭信号），2 秒后进程仍在运行：正常退出需要更长时间', pid:24364, pidSource:'tracked', closeRequested:true, stillRunning:true, alreadyStopped:false, guidance:'…'}` → `{success:true, message:'Game not running（已登记的 PID 24364 此前已退出；未扫描其它实例，以免误杀玩家手动启动的 RimWorld）', alreadyStopped:true, stopped:false, pid:24364, pidSource:'tracked'}`。**第二次调用不会碰玩家手动开的另一个实例。**
- [ ] 任何返回体里都不再出现 `'Game still closing, may need force'` 字面量（由 `护栏：仍在运行时文案不得再出现 "may need force"` 守住）。
- [ ] `README.md:53` 声称的 `selfStartedPid` 防误杀不变量，第一次调用后就仍然成立。
- [ ] `README.md:66`、`MCP/index.js:1850`、`MCP/index.js:2924` 三处文案与实际返回体一致。
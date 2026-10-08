# RimWorld 日志「来源可信度元数据」实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

## ⚠️ 开始前必读：本计划修的是什么，不修什么

**不修「轮转」，轮转修不了。**

RimWorld 每次启动都会把上一局的 `Player.log` 轮转成同目录的 `Player-prev.log`。这是 **Unity 引擎层的行为**：

- 全仓库（`*.cs` / `*.js` / `*.json`）检索 `Player-prev`：**零命中** —— 本项目从不读、从不写这个文件；
- vanilla RimWorld 源码检索 `Player-prev` / `LogRotator`：**零命中** —— RimWorld 的 C# 代码里没有任何轮转逻辑；
- 本项目对日志的全部使用都是只读：`MCP/config.json` 的 `"logPath": ".../Player.log"` 是唯一的日志路径配置（`MCP/index.js:67-74` 的 `loadConfig()` 只做 `%USERNAME%` 展开）。

**结论：轮转不是本项目的锅，本项目也无法阻止它。不要去改任何轮转行为、不要去 `chmod`/`移动`/`锁定` 日志文件。**

**本计划修的是：日志工具的返回值缺少「可信度元数据」，导致假阴性。**

具体缺陷（已逐行复核，行号为本计划撰写时亲眼读到的）：

| 位置 | 现状 |
|---|---|
| `MCP/index.js:1373-1399` `readLog()` | 返回 `success` / `content` / `totalLines` / `returnedLines` / `logPath`。**没有 mtime、没有文件大小、没有「这份日志属于哪一次会话」的标识。** |
| `MCP/index.js:1402-1428` `tailLog()` | 返回 `fileSize` + `lastModified`，**却没有 `path`**。 |

**同一对孪生工具口径不一致** —— `tail_rimworld_log` 拿得到 mtime，`read_rimworld_log` 拿不到。这是本条最扎实的抓手。

**假阴性链条（每一环都无法被现有返回值打断）：**
冷重启游戏 → `Player.log` 被轮转重建为本次会话 → 本会话刚跑起来、还没写任何告警 → 调用方 `read_rimworld_log{lines:100}` 得到 `success:true`、内容里 grep 到 0 条 → 读成「没有 Harmony 告警」。

**还有一个次级假阴性源**：现有返回值有 `totalLines` 与 `returnedLines` 两个数，但没告诉调用方「你拿到的 `content` 只是尾部片段」。尾部片段里 grep 0 命中，连本次会话都不成立。

---

**目标：** 让 `read_rimworld_log` / `tail_rimworld_log` 的返回值自带「这份日志属于哪一次会话、覆盖了多少、上一代在哪」的可信度元数据，并让两个孪生工具的字段口径完全一致，使调用方能自行判断「这次 0 命中可不可信」。

**架构：** 在 `MCP/index.js` 里把 `readLog` / `tailLog` 两个函数共同的 IO 逻辑抽成 4 个小辅助函数（路径推导、stat 摘要、读取+覆盖度计算、统一返回体组装），两个工具都改走这套辅助层，从而保证字段口径一致。`read_rimworld_log` 新增 `source: current|prev|all` 入参。**不新增任何 MCP 工具，不改工具数量，不改游戏侧任何代码。**

**技术栈：** Node.js 24（`node --test` 内置测试运行器）、`node:fs` / `node:path` / `node:os`、`@modelcontextprotocol/sdk` 的 `InMemoryTransport`（做 `tools/list` 断言）。

---

## 前提条件（动手前必须知道）

1. **本目录不是 git 仓库。** `git rev-parse` 在 `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger` 及其所有父目录都返回 `fatal: not a git repository`（目录下有 `.gitignore` 但没有 `.git`）。**因此本计划不含 `git commit` 步骤**——照抄 writing-plans 的 commit 步骤会直接失败。每个任务自带可运行的测试作为验收点与回退点。如需版本控制，由维护者决定是否 `git init` 并先提交基线。

2. **测试运行方式（用项目真实命令，不要臆造）：**
   `MCP/package.json:9` 定义了 `"test": "node --test \"test/*.test.js\""`。工作目录必须是 `MCP/`。

   ```powershell
   cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
   npm test                                          # 全量
   node --test test/logProvenance.test.js            # 只跑本计划新增的测试文件
   ..\runtime\node\node.exe --test test\logProvenance.test.js   # 用模组自带的便携 node（runtime\node\node.exe 已确认存在）
   ```

3. **测试导入 `index.js` 前必须设守卫** `process.env.UESD_MCP_NO_START = '1'`，否则会真的监听 3000 端口。这是本仓库所有测试文件的既有约定（见 `MCP/test/toolCountConsistency.test.js:5`、`MCP/test/mapMarker.test.js:5`、`MCP/test/toolchainFixes.test.js:8`）。

4. **`_testHooks.config` 与模块内的 `config` 是同一个对象引用**（`MCP/index.js:5452` 是 `{ config, }` 的简写）。测试可以直接改 `mod._testHooks.config.logPath` 把工具指向临时夹具，用例结束还原。`node --test` 默认每个测试文件一个独立进程，不会污染其它文件。

5. **无需构建、无需部署。** `MCP/resolve-runtime.ps1` 只负责解析 `runtime/` 下的便携 node/bun/dotnet 路径，不复制任何文件；`runtime/` 下没有 `index.js` 副本。**直接改 `MCP/index.js` 即是改到生效的那一份。**

---

## 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/index.js` | 修改（5 处，见各任务） | 新增 4 个日志元数据辅助函数；改写 `readLog` / `tailLog`；改两个工具的 `inputSchema` 与 `description`；改 dispatch 传参；给 `_testHooks` 挂上辅助函数 |
| `MCP/test/logProvenance.test.js` | 创建 | 本条全部单测：路径推导、覆盖度、轮转上下文、`source` 三态、口径对齐护栏、`tools/list` 与 `mcp_help` 暴露面 |

**为什么不新增源文件：** 辅助函数总共约 50 行，全部服务于同一件事（日志元数据），放进 `MCP/index.js` 里紧邻 `readLog` / `tailLog`，比拆一个 `MCP/logMeta.js` 多一层 import 更难读。这符合 writing-plans 的「在现有代码库中遵循已有模式」。

---

## 字段契约（三个任务都必须与此一致，实现前先背下来）

**成功返回的顶层键（`read` 与 `tail` 完全相同，15 个）：**

| 键 | 类型 | 含义 |
|---|---|---|
| `success` | `true` | 文件读到了 |
| `source` | `'current'` \| `'prev'` | 这次读的是哪一代（`tail` 恒为 `'current'`） |
| `content` | `string` | 返回的文本片段 |
| `path` | `string` | 实际读的那个文件的绝对路径 |
| `logPath` | `string` | **旧键，保留兼容**，值 === `path` |
| `lineCount` | `number` | 文件总行数 |
| `totalLines` | `number` | **旧键，保留兼容**，值 === `lineCount` |
| `returnedLines` | `number` | `content` 实际包含的行数 |
| `coverage` | `'full'` \| `'tail'` | `returnedLines < lineCount` ? `'tail'` : `'full'` |
| `fileSize` | `number` | 文件字节数 |
| `lastWriteTime` | `string` | ISO 8601 |
| `lastModified` | `string` | **旧键，保留兼容**，值 === `lastWriteTime` |
| `rotatedFrom` | `string` \| `null` | **固定语义**：=`session.prev.lastWriteTime`，即「上一会话最后一次写入 / 本次 `Player.log` 轮转重建」的时刻 ≈ 本次会话起点。`prev` 不存在时为 `null` |
| `session` | `{current, prev}` | 与 `source` 无关的**两代摘要**，每项 `{ path, fileSize, lastWriteTime }`，不存在的一侧为 `null` |
| `note` | `string` | 中文一句话，教调用方怎么用上面这些字段判断 0 命中可不可信 |

**错误返回的键：** `success:false` + `source`（`tail` 的失败分支没有 `source`）+ `errorCode` + `message` + `hint?` + `session` + `rotatedFrom?`。`errorCode` 取值固定为 `INVALID_SOURCE` / `LOG_NOT_FOUND` / `LOG_PREV_NOT_FOUND` / `LOG_READ_FAILED`（与仓库既有 UPPER_SNAKE 风格一致，如 `MISSING_PARAMETER`、`TASK_NOT_FOUND`）。

**`source:'all'` 独有：** 顶层额外有 `prevStatus`（`'ok'` \| `'missing'`）、`current`（完整成功返回体）、`prev`（完整成功返回体 \| `null`）。**永不把两代合并成一份文本。**

**关键设计取舍（实现时不要「优化」掉）：**

- **`session` 是固定语义的两代摘要，不随 `source` 变化。** 读 `source:'current'` 时 `session.current` 与顶层 `path` 指向同一文件，这份冗余是刻意的：调用方不必按 `source` 分支去判断 `session.current` 是不是「自己」。这是本计划里唯一被保留的冗余，其余全部避免重复。
- **`session.current` / `session.prev` 不带 `lineCount`。** 为了凑一个字段把 1 MB 日志再 `split` 一遍不划算；判断可信度需要的是「上一代在哪、写到什么时候」，不是它的行数。只做 `statSync`，不重读。
- **绝不用 `stat.birthtime` 当会话起点。** Windows NTFS 的 metadata tunneling 会把旧文件的创建时间套到新文件上——**本机实测**：`Player.log` 与 `Player-prev.log` 的 `birthtime` 都是 `2025-12-15T12:54:30.738Z`（首次安装那天），而它们的 `mtime` 分别是 `2026-10-01T09:53:50.352Z` / `2026-10-01T09:43:06.533Z`。`ctime` 在本机与 `mtime` 同步，也无参考价值。**会话起点只能来自 `Player-prev.log` 的 `mtime`。**
- **旧键一律不删。** `logPath` / `totalLines` / `lastModified` 是既有返回契约的一部分，删了会打破调用方。`lastModified` 原本是 `Date` 对象，`JSON.stringify` 之后本来就是 ISO 串，改成字符串在网络上完全等价。
- **`tail_rimworld_log` 刻意不加 `source`。** 它的用途是盯本次会话的实时尾部；读上一会话统一走 `read_rimworld_log{source:'prev'|'all'}`。两处文案都要写明这个分工。
- **不在工具内加 grep / contains 过滤。** 会与吐槽板 [03]、[11] 撞车，也会让「0 命中」这个最危险的输出形态更难被察觉。过滤留在调用方，工具只负责把元信息给全。

---

### 任务 1：轮转路径推导与元数据辅助层

**为什么先做这个：** 「`Player-prev.log` 到底在哪、怎么推算」是本条唯一的**事实性**风险点。跨平台路径规则一旦搞错，后面全错。先用纯函数把它钉死并单测，再往上接。

- **文件：**
  - 修改：`MCP/index.js:1372`（在 `// Read log` 注释上方插入整个辅助层）
  - 修改：`MCP/index.js:5450-5459`（`_testHooks` 里挂上 4 个辅助函数）
  - 创建：`MCP/test/logProvenance.test.js`

- [ ] **步骤 1：编写失败的测试（测试文件骨架 + 路径推导 + 辅助层）**

创建 `MCP/test/logProvenance.test.js`，完整内容如下（**一次写完，任务 2、3 会往里追加用例**）：

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

// ---------- 共用小工具 ----------

// 把 mcpHelpResult / handleToolCall 的返回整形为纯文本
// （与 MCP/test/mapMarker.test.js:101-106 的 withPromise 同一写法）
const asText = (r) =>
  Promise.resolve(r).then((x) =>
    typeof x === 'string' ? x : (x.content || []).map((c) => c.text || '').join(''));

// 在系统临时目录造一份日志夹具；用例结束由 t.after 清掉，仓库里不落任何文件。
// files 只写实际要创建的项（值形如 { content, mtime }），
// 返回的 paths 两个键都给出——不存在的那个路径也返回，好让用例构造「文件缺失」场景。
function makeFixture(t, files) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'uesd-log-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const paths = {
    'Player.log': path.join(dir, 'Player.log'),
    'Player-prev.log': path.join(dir, 'Player-prev.log')
  };
  for (const [name, spec] of Object.entries(files)) {
    fs.writeFileSync(paths[name], spec.content, 'utf-8');
    if (spec.mtime) fs.utimesSync(paths[name], spec.mtime, spec.mtime);
  }
  return { dir, paths };
}

// 把 MCP 的日志路径临时指向夹具；用例结束还原。
// _testHooks.config 与模块内 config 是同一个对象引用（见 MCP/index.js:5452）。
function useLogPath(t, logPath) {
  const original = mod._testHooks.config.logPath;
  mod._testHooks.config.logPath = logPath;
  t.after(() => { mod._testHooks.config.logPath = original; });
}

// 连真实 server（InMemoryTransport）走 tools/list —— 唯一能验证对外 schema 的路子
async function withListTools(fn) {
  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { InMemoryTransport } = await import('@modelcontextprotocol/sdk/inMemory.js');
  const server = mod._testHooks.createServer();
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'log-test', version: '1.0.0' });
  try {
    await Promise.all([server.connect(serverT), client.connect(clientT)]);
    return await fn(client);
  } finally {
    await client.close().catch(() => {});
    await server.close().catch(() => {});
  }
}

const callRead = (args) => asText(mod.handleToolCall('read_rimworld_log', args)).then(JSON.parse);
const callTail = (args) => asText(mod.handleToolCall('tail_rimworld_log', args)).then(JSON.parse);

// 夹具常量：ASCII 内容，无尾换行，故 split('\n') 后行数就是数组长度
const CURRENT_TEXT = ['cur-1', 'cur-2', 'cur-3', 'cur-4', 'cur-5'].join('\n'); // 5 行
const PREV_TEXT = ['pre-1', 'pre-2', 'pre-3'].join('\n');                     // 3 行
const CURRENT_MTIME = new Date('2026-10-01T10:00:00Z');
const PREV_MTIME = new Date('2026-10-01T09:30:00Z');

// ===========================================================================
// [09] 第 1 层：轮转路径推导与元数据辅助层
// ===========================================================================

test('[09] derivePrevLogPath：同目录同名、扩展名之前插入 -prev（跨平台一致）', () => {
  const d = mod._testHooks.derivePrevLogPath;
  // 本仓库只跑 Windows（MCP 由 start-mcp.bat/.ps1 启动，config.json 里也是 Windows 路径），
  // 所以 Windows 形态只在 win32 上断言；在 POSIX 上 path.dirname 认不出反斜杠路径。
  if (process.platform === 'win32') {
    assert.equal(
      d('C:\\Users\\u\\AppData\\LocalLow\\Ludeon Studios\\RimWorld by Ludeon Studios\\Player.log'),
      path.join('C:\\Users\\u\\AppData\\LocalLow\\Ludeon Studios\\RimWorld by Ludeon Studios', 'Player-prev.log'),
      'Windows 路径：目录名带空格也不能影响结果'
    );
  }
  assert.equal(
    d('/home/u/.config/unity3d/Ludeon Studios/RimWorld/Player.log'),
    '/home/u/.config/unity3d/Ludeon Studios/RimWorld/Player-prev.log',
    'POSIX 路径'
  );
  assert.equal(
    d('/opt/RimWorld.Linux/Player.log'),
    '/opt/RimWorld.Linux/Player-prev.log',
    '目录名里带点不能被当成扩展名'
  );
  assert.equal(
    d('/var/log/rw/Player'),
    path.join('/var/log/rw', 'Player-prev'),
    '无扩展名时退化为「基名 + -prev」'
  );
});

test('[09] readLogFile：coverage/lineCount/returnedLines 三者自洽', (t) => {
  const { paths } = makeFixture(t, { 'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME } });
  const readLogFile = mod._testHooks.readLogFile;

  const full = readLogFile(paths['Player.log'], 100);
  assert.equal(full.lineCount, 5);
  assert.equal(full.returnedLines, 5);
  assert.equal(full.coverage, 'full');
  assert.equal(full.content, CURRENT_TEXT);
  assert.equal(full.path, paths['Player.log']);
  assert.equal(full.fileSize, Buffer.byteLength(CURRENT_TEXT));

  const tail = readLogFile(paths['Player.log'], 2);
  assert.equal(tail.lineCount, 5);
  assert.equal(tail.returnedLines, 2);
  assert.equal(tail.coverage, 'tail', '2 < 5 → 只拿到尾部片段，0 命中连本次会话都不成立');
  assert.equal(tail.content, 'cur-4\ncur-5');

  assert.equal(
    readLogFile(path.join(path.dirname(paths['Player.log']), 'Nope.log'), 10),
    null,
    '文件不存在必须返回 null 交给调用方报错，不得静默换源'
  );
});

test('[09] buildSessionBlock：推导出的 prev 路径必须真的命中磁盘上的 Player-prev.log', (t) => {
  const { paths } = makeFixture(t, {
    'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME },
    'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME }
  });
  const block = mod._testHooks.buildSessionBlock(paths['Player.log']);

  assert.equal(block.session.current.path, paths['Player.log']);
  assert.equal(block.session.prev.path, paths['Player-prev.log'], '路径推导必须落到真实文件上');
  assert.equal(block.session.prev.fileSize, Buffer.byteLength(PREV_TEXT));
  assert.equal(block.session.current.fileSize, Buffer.byteLength(CURRENT_TEXT));
  assert.equal(block.session.prev.lineCount, undefined, '摘要刻意只 statSync，不重读文件算行数');

  // rotatedFrom = 上一会话最后写入时刻 ≈ 本次会话起点
  assert.equal(block.rotatedFrom, block.session.prev.lastWriteTime);
  assert.equal(Date.parse(block.rotatedFrom), Date.parse(PREV_MTIME.toISOString()));
  assert.ok(
    Date.parse(block.session.current.lastWriteTime) > Date.parse(block.rotatedFrom),
    '本次会话的最后写入必然晚于轮转时刻'
  );
});

test('[09] buildSessionBlock：没有 Player-prev.log（首次启动）时 prev=null、rotatedFrom=null，不抛错', (t) => {
  const { paths } = makeFixture(t, { 'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME } });
  const block = mod._testHooks.buildSessionBlock(paths['Player.log']);
  assert.equal(block.session.prev, null);
  assert.equal(block.rotatedFrom, null);
  assert.equal(block.session.current.path, paths['Player.log']);
});
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/logProvenance.test.js
```

预期：4 个用例全部 FAIL，报错形如 `TypeError: mod._testHooks.derivePrevLogPath is not a function`（`readLogFile` / `buildSessionBlock` 同样报 not a function）。**必须看到失败再往下走。**

- [ ] **步骤 3：实现辅助层**

在 `MCP/index.js:1372` 的 `// Read log` 这一行**上方**，插入以下整段（这一段同时替换掉旧的 `// Read log` 注释头，旧的 `readLog` / `tailLog` 函数体在任务 2、3 才改，本步骤不碰它们）：

```js
// ---------- Player.log 轮转元数据（吐槽板 [09]） ----------
// 事实前提（已核实，不要重新怀疑）：RimWorld 每次启动都会把上一局的 Player.log 轮转成同目录的
// Player-prev.log。这是 Unity 引擎层行为，本项目零参与、也改不掉（仓库与 vanilla 源码检索
// Player-prev 均零命中）。本项目该负责的是把「这份日志属于哪一次会话」如实告诉调用方——
// 否则冷重启后在新日志里 grep 到 0 命中会被读成「没有告警」（假阴性）。
// 由此 read_rimworld_log 与 tail_rimworld_log 共用下面这套元数据，两个工具的字段口径必须一致。

// 由主日志路径推导上一会话轮转日志的绝对路径。
// Unity 的规则：同目录、同名、扩展名之前插入 "-prev"。本机磁盘实测：
//   C:\Users\<u>\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log
//   C:\Users\<u>\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player-prev.log
// 走 path.dirname/basename/extname 而不是正则替换，Windows / macOS / Linux 行为一致。
// 首次启动（只玩过一次）时该文件不存在；调用方据 existsSync 判定，绝不静默回退到 current。
function derivePrevLogPath(logPath) {
  const ext = path.extname(logPath);
  const base = path.basename(logPath, ext);
  return path.join(path.dirname(logPath), `${base}-prev${ext}`);
}

// 两代日志的纯 statSync 摘要；不存在的一侧为 null。
// 不带 lineCount 是刻意的：为了凑一个字段把 1MB 日志再 split 一遍不划算，而调用方判断可信度
// 需要的是「上一代在哪、写到什么时候」，不是它的行数。
function summarizeLogFile(filePath) {
  if (!fs.existsSync(filePath)) return null;
  try {
    const st = fs.statSync(filePath);
    return { path: filePath, fileSize: st.size, lastWriteTime: st.mtime.toISOString() };
  } catch (error) {
    log('WARN', `summarizeLogFile 失败 ${filePath}: ${error.message}`);
    return null;
  }
}

// 固定语义、与 source 无关的轮转上下文：
//   session.current = Player.log（本次游戏会话）
//   session.prev    = Player-prev.log（上一会话，已被本次轮转取代）
//   rotatedFrom     = session.prev 的 lastWriteTime = 上一会话最后一次写入 / 本次会话开始的时刻；
//                     prev 不存在（首次启动）时为 null。
// 读 source='current' 时 session.current 与顶层 path 指向同一文件，这份冗余是刻意保留的：
// 固定语义让调用方不必按 source 分支去判断 session.current 是不是「自己」。
// 刻意不用 birthtime 当会话起点：Windows NTFS 的 metadata tunneling 会把旧文件的创建时间套到
// 新文件上（本机实测 Player.log 的 birthtime 停在首次安装那天，对会话起点毫无参考价值）；
// ctime 则与 mtime 同步。会话起点只能来自 Player-prev.log 的 mtime。
function buildSessionBlock(currentPath) {
  const prev = summarizeLogFile(derivePrevLogPath(currentPath));
  return {
    session: { current: summarizeLogFile(currentPath), prev },
    rotatedFrom: prev ? prev.lastWriteTime : null
  };
}

// 读一个日志文件并算出「这次返回的内容覆盖了多少」——调用方判断 0 命中可不可信的第一依据。
// 文件不存在返回 null，由调用方决定报什么错（绝不静默换源）。
function readLogFile(filePath, lines) {
  if (!fs.existsSync(filePath)) return null;
  const stats = fs.statSync(filePath);
  const allLines = fs.readFileSync(filePath, 'utf-8').split('\n');
  const returnedLines = Math.min(lines, allLines.length);
  return {
    path: filePath,
    content: allLines.slice(-lines).join('\n'),
    lineCount: allLines.length,
    returnedLines,
    coverage: returnedLines < allLines.length ? 'tail' : 'full',
    fileSize: stats.size,
    lastWriteTime: stats.mtime.toISOString()
  };
}

// 把 readLogFile 的结果与轮转上下文拼成统一返回体。
// 旧键 logPath / totalLines / lastModified 全部保留，值与新键相同，不破坏既有调用方。
// lastModified 原本是 Date 对象，JSON.stringify 后本来就是 ISO 串，改成字符串在网络上完全等价。
function buildLogResult(file, extra) {
  return {
    success: true,
    content: file.content,
    path: file.path,
    logPath: file.path,
    lineCount: file.lineCount,
    totalLines: file.lineCount,
    returnedLines: file.returnedLines,
    coverage: file.coverage,
    fileSize: file.fileSize,
    lastWriteTime: file.lastWriteTime,
    lastModified: file.lastWriteTime,
    ...extra
  };
}

const NOTE_COVERAGE_TAIL =
  '本次只返回了尾部片段（coverage=tail），grep 0 命中连本次会话都不成立，请加大 lines 读全文。';
const NOTE_COVERAGE_FULL =
  '本次返回了全文（coverage=full）；若本次会话刚轮转不久（lastWriteTime 与 rotatedFrom 相距很短），'
  + '0 命中多半只是本轮还没写进去，请用 source:"prev" 复查上一会话。';
const NOTE_PREV =
  '你正在读上一会话日志（Player-prev.log）；rotatedFrom 是它被本次会话取代的时刻。'
  + '想同时看两代请用 source:"all"（本工具不会把两代合并成一份文本）。';

// 读 current 时的说明句：把「本次会话日志」这个事实和覆盖度结论一次讲清。
function currentNoteFor(file) {
  return '本次读到的是本次游戏会话日志（Player.log）；rotatedFrom 给出本次会话的起点。'
    + (file.coverage === 'tail' ? NOTE_COVERAGE_TAIL : NOTE_COVERAGE_FULL);
}
```

- [ ] **步骤 4：把 4 个辅助函数挂到 `_testHooks`**

在 `MCP/index.js:5458` 的 `resetDpaState: () => { dpaRoundActive = false; },` 之后、`5459` 的 `};` 之前，追加：

```js
  // 2026-10-01 Player.log 轮转元数据（吐槽板 [09]）：纯函数 / 纯 IO，供单测直接驱动
  derivePrevLogPath,
  summarizeLogFile,
  readLogFile,
  buildSessionBlock,
```

- [ ] **步骤 5：运行测试验证通过**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/logProvenance.test.js
```

预期：4 个用例全部 PASS（`# pass 4`，`# fail 0`）。

- [ ] **步骤 6：跑全量确认无回归**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：全部 PASS。**本任务只新增了未被调用的函数，工具行为一个字节都没变**，所以任何失败都是既存问题或环境问题——先确认失败用例名与本任务无关再动手。

---

### 任务 2：`read_rimworld_log` 的 `source` 参数与可信度元数据

**文件：**
- 修改：`MCP/index.js:1373-1399`（`readLog` 整个函数）
- 修改：`MCP/index.js:1904-1917`（`read_rimworld_log` 的工具定义）
- 修改：`MCP/index.js:2982` 之后、`2983` 的 `};` 之前（`TOOL_USAGE_NOTES` 追加两条）
- 修改：`MCP/index.js:4391-4396`（dispatch 传 `source`）
- 测试：`MCP/test/logProvenance.test.js`（追加第 2 层用例）

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/logProvenance.test.js` 末尾追加：

```js
// ===========================================================================
// [09] 第 2 层：read_rimworld_log 的 source 参数与可信度元数据
// ===========================================================================

test('[09] read_rimworld_log source=current（默认）：全套元数据齐备，旧键一字未删', async (t) => {
  const { paths } = makeFixture(t, {
    'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME },
    'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME }
  });
  useLogPath(t, paths['Player.log']);

  const body = await callRead({ lines: 100 });
  assert.equal(body.success, true);
  assert.equal(body.source, 'current');
  assert.equal(body.path, paths['Player.log']);
  assert.equal(body.lineCount, 5);
  assert.equal(body.returnedLines, 5);
  assert.equal(body.coverage, 'full');
  assert.equal(body.content, CURRENT_TEXT);
  assert.equal(body.fileSize, Buffer.byteLength(CURRENT_TEXT));
  assert.equal(body.lastWriteTime, body.session.current.lastWriteTime);
  assert.equal(body.rotatedFrom, body.session.prev.lastWriteTime);
  assert.equal(Date.parse(body.rotatedFrom), Date.parse(PREV_MTIME.toISOString()));
  assert.equal(body.session.prev.path, paths['Player-prev.log']);
  assert.match(body.note, /本次游戏会话/);

  // 旧键兼容：既有调用方不许被打破
  assert.equal(body.logPath, paths['Player.log']);
  assert.equal(body.totalLines, 5);
  assert.equal(body.lastModified, body.lastWriteTime);
});

test('[09] read_rimworld_log：lines 截断时 coverage=tail，note 明说 0 命中连本次会话都不成立', async (t) => {
  const { paths } = makeFixture(t, { 'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME } });
  useLogPath(t, paths['Player.log']);

  const body = await callRead({ lines: 2 });
  assert.equal(body.coverage, 'tail');
  assert.equal(body.returnedLines, 2);
  assert.equal(body.lineCount, 5);
  assert.match(body.note, /coverage=tail/);
  // 没有 prev 时必须明说，不能假装有上一会话
  assert.equal(body.rotatedFrom, null);
  assert.equal(body.session.prev, null);
});

test('[09] read_rimworld_log source=prev：读到上一会话，字段口径与 current 一致', async (t) => {
  const { paths } = makeFixture(t, {
    'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME },
    'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME }
  });
  useLogPath(t, paths['Player.log']);

  const body = await callRead({ source: 'prev' });
  assert.equal(body.success, true);
  assert.equal(body.source, 'prev');
  assert.equal(body.path, paths['Player-prev.log']);
  assert.equal(body.content, PREV_TEXT);
  assert.equal(body.lineCount, 3);
  assert.equal(body.coverage, 'full');
  assert.equal(body.returnedLines, 3);
  // session 语义不随 source 变：仍描述两代
  assert.equal(body.session.current.path, paths['Player.log']);
  assert.equal(body.session.prev.path, paths['Player-prev.log']);
  assert.equal(body.rotatedFrom, body.session.prev.lastWriteTime);
  assert.match(body.note, /上一会话/);
});

test('[09] read_rimworld_log source=prev 且 prev 不存在：明确报错，绝不静默回退到 current', async (t) => {
  const { paths } = makeFixture(t, { 'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME } });
  useLogPath(t, paths['Player.log']);

  const body = await callRead({ source: 'prev' });
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'LOG_PREV_NOT_FOUND');
  assert.match(body.message, /Player-prev\.log/);
  assert.match(body.hint, /不会静默回退/);
  assert.equal(
    body.content, undefined,
    '静默回退会把本次会话内容冒充成上一会话，正是本条要消灭的假阴性'
  );
  assert.equal(body.session.prev, null);
  assert.equal(body.rotatedFrom, null);
  assert.equal(body.session.current.path, paths['Player.log'], '即使失败也要报出当前日志在哪');
});

test('[09] read_rimworld_log source=all：两代各一段，绝不合并', async (t) => {
  const { paths } = makeFixture(t, {
    'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME },
    'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME }
  });
  useLogPath(t, paths['Player.log']);

  const body = await callRead({ source: 'all' });
  assert.equal(body.success, true);
  assert.equal(body.source, 'all');
  assert.equal(body.prevStatus, 'ok');
  assert.equal(body.current.path, paths['Player.log']);
  assert.equal(body.current.content, CURRENT_TEXT);
  assert.equal(body.prev.path, paths['Player-prev.log']);
  assert.equal(body.prev.content, PREV_TEXT);
  // 合并会造出一个并不存在的「单次会话」视图，比假阴性更危险
  assert.ok(!body.current.content.includes('pre-1'), 'current 段不得混入 prev 内容');
  assert.ok(!body.prev.content.includes('cur-1'), 'prev 段不得混入 current 内容');
  assert.equal(body.current.coverage, 'full');
  assert.equal(body.prev.coverage, 'full');
  assert.equal(body.rotatedFrom, body.session.prev.lastWriteTime);
  assert.match(body.note, /不.*合并|绝不合并/);
});

test('[09] read_rimworld_log source=all 且 prev 不存在：prev=null + prevStatus=missing，不算失败', async (t) => {
  const { paths } = makeFixture(t, { 'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME } });
  useLogPath(t, paths['Player.log']);

  const body = await callRead({ source: 'all' });
  assert.equal(body.success, true);
  assert.equal(body.prevStatus, 'missing');
  assert.equal(body.prev, null);
  assert.equal(body.current.content, CURRENT_TEXT);
  assert.equal(body.rotatedFrom, null);
});

test('[09] read_rimworld_log：source 非法值直接报错，不猜', async (t) => {
  const { paths } = makeFixture(t, { 'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME } });
  useLogPath(t, paths['Player.log']);

  const body = await callRead({ source: 'previous' });
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'INVALID_SOURCE');
  assert.match(body.message, /previous/);
});

test('[09] read_rimworld_log：Player.log 本身不存在时报 LOG_NOT_FOUND 并仍带轮转上下文', async (t) => {
  const { paths } = makeFixture(t, { 'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME } });
  useLogPath(t, paths['Player.log']); // 这个文件本次故意不创建

  const body = await callRead({});
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'LOG_NOT_FOUND');
  assert.equal(body.content, undefined);
  assert.equal(body.session.current, null);
  assert.equal(body.session.prev.path, paths['Player-prev.log']);
});

test('[09] read_rimworld_log：tools/list 里 source 带 enum 与默认值，描述写明轮转', async () => {
  await withListTools(async (client) => {
    const tools = await client.listTools();
    const t = tools.tools.find((x) => x.name === 'read_rimworld_log');
    assert.ok(t, 'read_rimworld_log 应在 tools/list 中（toolConfig.json 已设为 true）');
    assert.deepEqual(t.inputSchema.properties.source.enum, ['current', 'prev', 'all']);
    assert.equal(t.inputSchema.properties.source.default, 'current');
    assert.match(t.description, /轮转/);
    assert.match(t.description, /Player-prev\.log/);
  });
});

test('[09] mcp_help tool=read_rimworld_log：notes 写清 source 三态与「不静默回退」', async () => {
  const detail = JSON.parse(await asText(mod.mcpHelpResult({ tool: 'read_rimworld_log' })));
  assert.deepEqual(detail.inputSchema.properties.source.enum, ['current', 'prev', 'all']);
  assert.match(detail.notes, /current/);
  assert.match(detail.notes, /prev/);
  assert.match(detail.notes, /all/);
  assert.match(detail.notes, /不合并|各返回一段/);
  assert.match(detail.notes, /不.*静默回退/);
});
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/logProvenance.test.js
```

预期：任务 1 的 4 个用例仍 PASS；新增的 10 个用例 FAIL。典型失败信息：
- 前 8 个报 `body.source === undefined` / `body.coverage === undefined` / `body.success === true`（source 被忽略、仍只读 current）；
- `tools/list` 那个报 `Cannot read properties of undefined (reading 'enum')`（schema 里还没有 `source`）；
- `mcp_help` 那个报 `detail.notes` 为 `undefined`。

- [ ] **步骤 3：改写 `readLog`**

把 `MCP/index.js:1373-1399` 现有的整个 `readLog` 函数替换为：

```js
// Read log
// source 语义：
//   'current'（默认）—— 只读本次游戏会话的 Player.log
//   'prev'           —— 只读上一会话的 Player-prev.log；不存在时明确报错，绝不静默回退到 current
//   'all'            —— 两代各返回一段（current / prev 字段），永不合并成一份文本
// 返回体不提供 grep/过滤能力（过滤留给调用方）：本工具的责任是把「这次 0 命中可不可信」的判据给全——
// coverage（覆盖了多少）、lineCount/returnedLines（覆盖了哪一段）、rotatedFrom + lastWriteTime
// （这份日志覆盖哪一段时间）、session（上一代在哪）。
async function readLog(lines = 100, source = 'current') {
  try {
    if (!['current', 'prev', 'all'].includes(source)) {
      return {
        success: false,
        source,
        errorCode: 'INVALID_SOURCE',
        message: `source 只接受 current / prev / all，收到 "${source}"`,
        hint: 'current=本次游戏会话日志（默认）；prev=上一会话 Player-prev.log；all=两代各返回一段、不合并'
      };
    }

    const currentPath = config.logPath;
    const prevPath = derivePrevLogPath(currentPath);
    const rotation = buildSessionBlock(currentPath);

    if (source === 'all') {
      const current = readLogFile(currentPath, lines);
      if (!current) {
        return {
          success: false, source, errorCode: 'LOG_NOT_FOUND',
          message: `Log file not found: ${currentPath}`, ...rotation
        };
      }
      const prev = readLogFile(prevPath, lines);
      return {
        success: true,
        source: 'all',
        prevStatus: prev ? 'ok' : 'missing',
        current: buildLogResult(current, { source: 'current', note: currentNoteFor(current) }),
        prev: prev ? buildLogResult(prev, { source: 'prev', note: NOTE_PREV }) : null,
        note: '本工具永不把两代日志合并成一份文本：current / prev 各是一段完整内容 + 各自的元数据，'
          + '避免造出一个并不存在的「单次会话」视图。',
        ...rotation
      };
    }

    const targetPath = source === 'prev' ? prevPath : currentPath;
    const file = readLogFile(targetPath, lines);
    if (!file) {
      if (source === 'prev') {
        return {
          success: false,
          source,
          errorCode: 'LOG_PREV_NOT_FOUND',
          message: `上一会话日志不存在：${prevPath}（Player-prev.log 只在游戏启动过第二次之后才会出现）`,
          hint: '本工具不会静默回退到 current——回退等于又造一次假阴性。'
            + '改用 source:"current" 读本次会话日志，或先启动一次 RimWorld 再退出后重试。',
          ...rotation
        };
      }
      return {
        success: false, source, errorCode: 'LOG_NOT_FOUND',
        message: `Log file not found: ${currentPath}`, ...rotation
      };
    }

    return buildLogResult(file, {
      ...rotation,
      source,
      note: source === 'prev' ? NOTE_PREV : currentNoteFor(file)
    });
  } catch (error) {
    return {
      success: false, source, errorCode: 'LOG_READ_FAILED',
      message: `Read log failed: ${error.message}`
    };
  }
}
```

- [ ] **步骤 4：给工具定义加 `source` 入参并改描述**

把 `MCP/index.js:1904-1917` 整个 `read_rimworld_log` 条目替换为：

```js
  {
    name: 'read_rimworld_log',
    description: '[RimWorld 日志文件] 读取 RimWorld 游戏日志末尾内容。RimWorld 每次启动都会把上一局的 Player.log 轮转成同目录的 Player-prev.log——本工具默认（source="current"）只读本次会话；返回值带 path/lineCount/returnedLines/coverage/fileSize/lastWriteTime/rotatedFrom/session，调用方据此判断「grep 0 命中」是否可信（冷重启后新日志 0 命中多半是假阴性）。',
    inputSchema: {
      type: 'object',
      properties: {
        lines: {
          type: 'number',
          description: 'Number of lines (default 100)',
          default: 100
        },
        source: {
          type: 'string',
          enum: ['current', 'prev', 'all'],
          description: '读哪一代日志：current=本次游戏会话的 Player.log（默认）；prev=上一会话的 Player-prev.log（不存在时明确报错，不静默回退）；all=两代各返回一段（current/prev 字段，绝不合并成一份文本）。',
          default: 'current'
        }
      }
    }
  },
```

- [ ] **步骤 5：dispatch 传 `source`**

把 `MCP/index.js:4391-4396` 的 dispatch 分支改为：

```js
    case 'read_rimworld_log': {
      const result = await readLog(args?.lines ?? 100, args?.source ?? 'current');
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }
```

- [ ] **步骤 6：追加调用范式 / 易错点文案**

在 `MCP/index.js:2982` 的 `hotreload_status: {...}` 这一行之后、`2983` 的 `};` 之前，追加：

```js
  read_rimworld_log: {
    example: '{ "lines": 100, "source": "current" }',
    notes: 'RimWorld 每次启动会把上一局 Player.log 轮转成同目录的 Player-prev.log；source 选 current(默认)/prev/all，'
      + 'all 分 current/prev 两段返回、绝不合并（合并会造出并不存在的「单次会话」视图）。'
      + '返回值 coverage=tail 说明只拿到尾部片段，grep 0 命中连本次会话都不成立；'
      + 'coverage=full 时若 rotatedFrom 与 lastWriteTime 相距很短（本会话刚起），0 命中多半只是本轮还没写进去，请用 source="prev" 复查。'
      + 'rotatedFrom 就是 Player-prev.log 的最后写入时刻，prev 不存在（首次启动）时为 null。'
      + 'source="prev" 但 Player-prev.log 不存在时明确报错、不静默回退到 current。'
  },
  tail_rimworld_log: {
    example: '{ "lines": 50 }',
    notes: '只读本次游戏会话的 Player.log 尾部，元数据字段与 read_rimworld_log 完全一致；'
      + '要读上一会话用 read_rimworld_log{source:"prev"|"all"}。'
  },
```

- [ ] **步骤 7：运行测试验证通过**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/logProvenance.test.js
```

预期：全部 14 个用例 PASS（`# fail 0`）。

- [ ] **步骤 8：跑全量确认无回归**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：全部 PASS。`MCP/test/toolCountConsistency.test.js` 特别关注——本任务只改了一个工具的 `inputSchema`，**没有新增/删除工具**，工具总数、菜单可见数、工具宇宙数都必须与改前完全一致。

---

### 任务 3：`tail_rimworld_log` 口径对齐 + 一致性护栏

**文件：**
- 修改：`MCP/index.js:1402-1428`（`tailLog` 整个函数）
- 修改：`MCP/index.js:1918-1931`（`tail_rimworld_log` 的工具定义描述）
- 测试：`MCP/test/logProvenance.test.js`（追加第 3 层用例）

- [ ] **步骤 1：编写失败的测试**

在 `MCP/test/logProvenance.test.js` 末尾追加：

```js
// ===========================================================================
// [09] 第 3 层：tail_rimworld_log 与 read_rimworld_log 口径对齐
// ===========================================================================

test('[09] tail_rimworld_log：补齐 read_rimworld_log 的全套元数据（原有 fileSize/lastModified 保留）', async (t) => {
  const { paths } = makeFixture(t, {
    'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME },
    'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME }
  });
  useLogPath(t, paths['Player.log']);

  const body = await callTail({ lines: 2 });
  assert.equal(body.success, true);
  assert.equal(body.source, 'current');
  assert.equal(body.path, paths['Player.log']);
  assert.equal(body.lineCount, 5);
  assert.equal(body.returnedLines, 2);
  assert.equal(body.coverage, 'tail');
  assert.equal(body.content, 'cur-4\ncur-5');
  assert.equal(body.fileSize, Buffer.byteLength(CURRENT_TEXT));
  assert.equal(Date.parse(body.lastWriteTime), Date.parse(CURRENT_MTIME.toISOString()));
  assert.equal(body.lastModified, body.lastWriteTime);
  assert.equal(body.totalLines, 5);
  assert.equal(body.logPath, paths['Player.log']);
  assert.equal(body.rotatedFrom, body.session.prev.lastWriteTime);
  assert.equal(body.session.prev.path, paths['Player-prev.log']);
  assert.match(body.note, /read_rimworld_log/);
});

test('[09] 口径护栏：tail 与 read 的返回键集合必须逐字相同（不许再各说各话）', async (t) => {
  const { paths } = makeFixture(t, {
    'Player.log': { content: CURRENT_TEXT, mtime: CURRENT_MTIME },
    'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME }
  });
  useLogPath(t, paths['Player.log']);

  const read = await callRead({ source: 'current', lines: 100 });
  const tail = await callTail({ lines: 100 });
  assert.deepEqual(
    Object.keys(tail).sort(),
    Object.keys(read).sort(),
    `read 的键: ${Object.keys(read).sort().join(',')} | tail 的键: ${Object.keys(tail).sort().join(',')}`
  );
  // 值也必须来自同一次读取，而不是各自 stat 一次
  assert.equal(tail.lineCount, read.lineCount);
  assert.equal(tail.lastWriteTime, read.lastWriteTime);
  assert.equal(tail.coverage, read.coverage);
  assert.equal(tail.rotatedFrom, read.rotatedFrom);
});

test('[09] tail_rimworld_log：Player.log 不存在时报错并仍带轮转上下文', async (t) => {
  const { paths } = makeFixture(t, { 'Player-prev.log': { content: PREV_TEXT, mtime: PREV_MTIME } });
  useLogPath(t, paths['Player.log']); // 这个文件本次故意不创建

  const body = await callTail({});
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'LOG_NOT_FOUND');
  assert.equal(body.content, undefined);
  assert.equal(body.session.current, null);
  assert.equal(body.session.prev.path, paths['Player-prev.log']);
});

test('[09] tail_rimworld_log：描述写明只读本次会话、去上一会话用 read{source}，且刻意不给 source', async () => {
  await withListTools(async (client) => {
    const tools = await client.listTools();
    const t = tools.tools.find((x) => x.name === 'tail_rimworld_log');
    assert.ok(t, 'tail_rimworld_log 应在 tools/list 中');
    assert.match(t.description, /本次游戏会话/);
    assert.match(t.description, /read_rimworld_log/);
    assert.match(t.description, /轮转|Player-prev\.log/);
    assert.equal(
      t.inputSchema.properties.source, undefined,
      '刻意不给 tail 加 source：读上一会话统一走 read_rimworld_log{source}'
    );
  });
});
```

- [ ] **步骤 2：运行测试验证失败**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/logProvenance.test.js
```

预期：任务 1、2 的 14 个用例仍 PASS；新增的 4 个用例 FAIL。典型失败信息：
- 前 3 个报 `body.coverage === undefined` / `body.path === undefined` / `body.source === undefined`；
- 「口径护栏」那条报键集合不匹配，实际差异形如 `read 有 path/session/rotatedFrom，tail 没有`；
- 最后一条报 `t.description` 不含「本次游戏会话」。

- [ ] **步骤 3：改写 `tailLog`**

把 `MCP/index.js:1402-1428` 现有的整个 `tailLog` 函数替换为：

```js
// Tail log —— 与 readLog 共用同一套元数据字段（口径必须一致，否则调用方得记「哪个工具有哪个字段」）。
// 刻意不加 source：tail 的用途是盯本次会话的实时尾部；要读上一会话用 read_rimworld_log{source:'prev'|'all'}。
async function tailLog(lines = 50) {
  try {
    const currentPath = config.logPath;
    const file = readLogFile(currentPath, lines);
    if (!file) {
      return {
        success: false,
        errorCode: 'LOG_NOT_FOUND',
        message: `Log file not found: ${currentPath}`,
        ...buildSessionBlock(currentPath)
      };
    }
    return buildLogResult(file, {
      ...buildSessionBlock(currentPath),
      source: 'current',
      note: '本工具只读本次游戏会话的 Player.log 尾部；元数据字段与 read_rimworld_log 完全一致。'
        + '要读上一会话请用 read_rimworld_log{source:"prev"|"all"}。'
        + (file.coverage === 'tail' ? NOTE_COVERAGE_TAIL : '')
    });
  } catch (error) {
    return {
      success: false,
      errorCode: 'LOG_READ_FAILED',
      message: `Read log failed: ${error.message}`
    };
  }
}
```

- [ ] **步骤 4：改 `tail_rimworld_log` 的工具描述**

把 `MCP/index.js:1918-1931` 整个 `tail_rimworld_log` 条目替换为（`inputSchema` 的 `lines` 部分刻意保持不变）：

```js
  {
    name: 'tail_rimworld_log',
    description: '[RimWorld 日志文件] 获取 RimWorld 游戏日志（Player.log）最新内容。RimWorld 每次启动都会把上一局的 Player.log 轮转成同目录的 Player-prev.log——本工具只读本次游戏会话；返回元数据字段与 read_rimworld_log 完全一致（path/lineCount/returnedLines/coverage/fileSize/lastWriteTime/rotatedFrom/session）；要读上一会话请用 read_rimworld_log{source:"prev"|"all"}。',
    inputSchema: {
      type: 'object',
      properties: {
        lines: {
          type: 'number',
          description: 'Number of lines (default 50)',
          default: 50
        }
      }
    }
  },
```

- [ ] **步骤 5：运行测试验证通过**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/logProvenance.test.js
```

预期：全部 18 个用例 PASS（`# fail 0`）。

- [ ] **步骤 6：跑全量确认无回归**

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：全部 PASS。

---

## 验收：怎么确认假阴性真的被堵住了

单测全绿只证明字段存在。收尾时按下面这条**真实调用链**手工验一次（不需要起游戏，只读文件）：

```powershell
cd C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
$env:UESD_MCP_NO_START = '1'   # 不设这个守卫会真的监听 3000 端口
node -e "import('./MCP/index.js').then(async (m) => {
  const j = (r) => JSON.parse((r.content||[]).map(c=>c.text||'').join(''));
  console.log(JSON.stringify(j(await m.handleToolCall('read_rimworld_log', {lines: 5})), null, 2));
})"
```

逐条核对输出里有：

1. `path` / `lineCount` / `returnedLines` / `coverage` / `fileSize` / `lastWriteTime` —— 元信息齐了；
2. `rotatedFrom` 非空，且 `session.prev.path` 结尾是 `Player-prev.log`（本机该文件确实存在，见下文）；
3. `coverage` 为 `full`（`lines: 5` 而文件约 1 MB，必然是 `tail` 才对——若为 `full` 说明 `coverage` 算错了）；
4. `logPath` / `totalLines` / `lastModified` 三个旧键仍在。

再跑一次 `read_rimworld_log{source:'all'}`，确认输出是 `{ current: {...}, prev: {...} }` 两段而非一段拼接文本。

---

## 本计划明确不做的事（范围边界，不是「后续再做」）

1. **不碰轮转行为本身。** 不改配置、不改引擎、不改 vanilla、不去 `chmod`/移动/锁定 `Player.log`。轮转是 Unity 的事，本项目无责也改不掉。
2. **不加 grep / contains / 过滤参数。** 会与吐槽板 [03]、[11]（大结果集冲爆上下文）撞车，也会让「0 命中」这个最危险的输出形态更难被察觉。过滤留在调用方。
3. **不删任何旧返回键。** `logPath` / `totalLines` / `lastModified` 一律保留。
4. **不给工具加 `session` 分页/offset 参数。** 现在只有「末尾 N 行」。分页是独立需求，本条的核心是可信度元数据，不是读取能力。
5. **不改 `MCP/aggregator/`。** 压缩模式的聚合器从上游工具池按原名拉取 schema（见 `MCP/aggregator/index.js:224-225`），不重复声明这两个工具的 `inputSchema`，无需同步。
6. **不改 `MCP/config.json`。** 日志路径仍只有一个 `logPath`；`Player-prev.log` 由 `derivePrevLogPath` 从它推出来，不新增配置项（新增配置项等于把 Unity 的固定行为搬到本项目配置里，反而制造两处可能不一致的事实源）。
7. **不改 `About/About.xml` 的 `modVersion`，不写 `MCP/announcements.md`。** 是否把这条修复作为一条更新公告发布，由维护者在发版时决定。
8. **不改 `docs/mcp-吐槽板.md`。** 吐槽板的批注由主代理负责，子代理与本计划的执行者都不碰。
9. **不新增 MCP 工具、不改工具总数。** 工具数量口径由 `MCP/test/toolCountConsistency.test.js` 把关。

---

## 参考事实（执行者不必重新核实，但可以复核）

| 事实 | 证据 |
|---|---|
| `readLog` 在 `MCP/index.js:1373-1399`，返回 `success`/`content`/`totalLines`/`returnedLines`/`logPath`，**无 mtime** | 亲眼读过 |
| `tailLog` 在 `MCP/index.js:1402-1428`，返回 `fileSize`/`lastModified`，**无 path** | 亲眼读过 |
| `config.logPath` 是本 MCP 服务器唯一的日志路径来源（`loadConfig()`，`MCP/index.js:67-74`，只做 `%USERNAME%` 展开） | 全文检索 `logPath` 共 10 处命中，均在 `index.js` |
| `Player-prev.log` 与 `Player.log` 同目录并存：`Player.log` 1053806 字节 / mtime `2026-10-01T09:53:50.352Z`；`Player-prev.log` 1005570 字节 / mtime `2026-10-01T09:43:06.533Z` | 只读列举 `C:\Users\Timer_0\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\` |
| 该目录**只有** `Player.log` 与 `Player-prev.log` 两个轮转文件（无 `Player1.log` / `Player-prev1.log` 等编号副本） | 同上 |
| 上一会话的退出时刻与 MCP 日志 `MCP/logs/mcp-20261001-093829.log` 记录的 GABP 断连（`17:43:05.141`）相差 1 秒 → 轮转发生在游戏进程退出/重启时 | 日志交叉印证 |
| `Player.log` 与 `Player-prev.log` 的 `birthtime` **都是** `2025-12-15T12:54:30.738Z`（NTFS metadata tunneling，对会话起点无参考价值）；`ctime` 与 `mtime` 同步 | `fs.statSync` 实测 |
| 本机跑测试用 `npm test`（`MCP/package.json:9` → `node --test "test/*.test.js"`），node v24.13.0；便携 node 在 `runtime/node/node.exe` | `package.json` + 环境探测 |
| 仓库**不是 git 仓库**（`git rev-parse` 在模组目录及所有父目录均报 `fatal: not a git repository`） | 环境探测 |

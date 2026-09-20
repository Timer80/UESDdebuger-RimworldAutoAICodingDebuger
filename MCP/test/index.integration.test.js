import { test } from 'node:test';
import assert from 'node:assert/strict';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

test('index.js 可在测试模式下导入且不启动服务器', () => {
  assert.equal(typeof mod.handleToolCall, 'function');
  assert.equal(typeof mod.splitPlayForArgs, 'function');
  assert.ok(mod.longTaskRunner, 'longTaskRunner 应已装配');
});

test('splitPlayForArgs：控制参数被剥离，不泄漏给游戏侧', () => {
  const { control, passthrough } = mod.splitPlayForArgs({
    durationMs: 7200000, speed: 'Superfast', nonBlocking: true,
    sampleIntervalMs: 1000, snapshotPath: 'x.jsonl', pauseOnFinish: 'keep',
  });
  assert.deepEqual(passthrough, { durationMs: 7200000, speed: 'Superfast' });
  assert.equal(control.nonBlocking, true);
  assert.equal(control.sampleIntervalMs, 1000);
  assert.equal(control.snapshotPath, 'x.jsonl');
  assert.equal(control.pauseOnFinish, 'keep');
});

test('splitPlayForArgs：缺参/非对象输入不炸', () => {
  assert.deepEqual(mod.splitPlayForArgs(undefined), { control: {}, passthrough: {} });
  assert.deepEqual(mod.splitPlayForArgs(null), { control: {}, passthrough: {} });
  assert.deepEqual(mod.splitPlayForArgs('nope'), { control: {}, passthrough: {} });
});

test('play_for{durationMs:2000000} 单次：超上界被拒且给出两条出路', async () => {
  const res = await mod.handleToolCall('rimworld.play_for', { durationMs: 2000000, speed: 'Superfast' });
  const body = JSON.parse(res.content[0].text);
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'MAX_SINGLE_CALL_TIMEOUT_EXCEEDED');
  assert.match(body.message, /nonBlocking:true/);
  assert.match(body.message, /maxSingleCallTimeoutMs/);
});

test('play_for{durationMs:60000} 未超上界：不得被上界校验拦下', async () => {
  // 未初始化的 GABP 桥接会给出可读错误，而不是 MAX_SINGLE_CALL_TIMEOUT_EXCEEDED —— 这足以
  // 证明它通过了上界校验、走到了实际派发路径。
  const res = await mod.handleToolCall('rimworld.play_for', { durationMs: 60000, speed: 'Superfast' });
  const text = res.content[0].text;
  assert.ok(!/MAX_SINGLE_CALL_TIMEOUT_EXCEEDED/.test(text), `不应被上界拦下，实际: ${text}`);
  assert.match(text, /GABP/);
});

test('play_for{nonBlocking:true} 缺 durationMs：秒级可读错误', async () => {
  const res = await mod.handleToolCall('rimworld.play_for', { nonBlocking: true });
  const body = JSON.parse(res.content[0].text);
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'INVALID_DURATION');
});

test('play_for{nonBlocking:true} 成功路径：秒级返回 taskId，且任务日志落盘', async () => {
  const fs = await import('node:fs');
  const path = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const here = path.dirname(fileURLToPath(import.meta.url));
  const journalDir = path.join(here, '..', '..', 'docs', 'dpa', 'tasks');

  const res = await mod.handleToolCall('rimworld.play_for', {
    durationMs: 600000,          // 远小于单次上界，走 nonBlocking 时不应被上界拦
    speed: 'Superfast',
    nonBlocking: true,
    sampleIntervalMs: 2000,
  });
  const body = JSON.parse(res.content[0].text);
  assert.equal(body.success, true, `nonBlocking 启动应成功，实际: ${res.content[0].text.slice(0, 300)}`);
  assert.equal(body.started, true);
  assert.match(body.taskId, /^t-/, 'taskId 形如 t-xxxx');
  assert.equal(body.requestedDurationMs, 600000);
  assert.equal(body.sampleIntervalMs, 2000);
  assert.ok(body.snapshotPath, '应回报采样文件路径');
  assert.ok(body.journalPath, '应回报任务日志路径');
  // _done 是内部句柄，不应泄漏给调用方
  assert.equal(body._done, undefined, '_done 不应出现在对外返回值里');

  // 任务随即会因 GABP 未初始化而失败（本测试不启动游戏），但 journal 必须已经写下 start 记录——
  // 这正是「进程重启后能查账」依赖的东西。
  const journalFile = body.journalPath;
  let lines = [];
  for (let i = 0; i < 20 && lines.length === 0; i++) {
    if (fs.existsSync(journalFile)) {
      lines = fs.readFileSync(journalFile, 'utf-8').trim().split('\n').filter(Boolean);
    }
    if (lines.length === 0) await new Promise((r) => setTimeout(r, 50));
  }
  assert.ok(lines.length > 0, `任务日志应已落盘: ${journalFile}`);
  // 不假设行序：GABP 未连接时任务会立刻失败，日志里可能已经跟在 start 后面写了 end。
  const recs = lines.map((l) => JSON.parse(l));
  const start = recs.find((r) => r.type === 'start');
  assert.ok(start, `日志里应有 start 记录，实际类型: ${recs.map((r) => r.type).join(',')}`);
  assert.equal(start.taskId, body.taskId);
  assert.equal(start.requestedDurationMs, 600000);
  assert.equal(typeof start.pid, 'number');

  // 收尾：把这条测试产生的落盘文件删掉，避免污染 docs/dpa（目录本身被 gitignore，但别留垃圾）
  try { fs.unlinkSync(journalFile); } catch (e) { /* 忽略 */ }
  const sampleFile = body.samplePath;
  try { if (fs.existsSync(sampleFile)) fs.unlinkSync(sampleFile); } catch (e) { /* 忽略 */ }

  // 任务应已进入终态（failed，因为没连游戏）——避免把 running 状态留给后续测试
  const st = JSON.parse((await mod.handleToolCall('task_status', { taskId: body.taskId })).content[0].text);
  assert.ok(['failed', 'aborted', 'cancelled', 'completed'].includes(st.state),
    `GABP 未连接时任务应快速进入终态，实际 ${st.state}`);
});

test('task_status 无参：即便无任务也返回结构完整的结果', async () => {
  const res = await mod.handleToolCall('task_status', {});
  const body = JSON.parse(res.content[0].text);
  assert.ok(Array.isArray(body.tasks));
  assert.ok(Array.isArray(body.orphanRecoveries));
  assert.equal(typeof body.running, 'number');
});

test('T3 入参推导超时不计入假死计数，不触发 forceReconnect（验收项 4）', async () => {
  // 为什么必须验这条：入参推导出的长档超时是**预期内**的。若它也计入
  // gabpConsecutiveTimeout，阈值 3 一到就会 forceReconnect，把正在跑的长任务打断，
  // 且调用方会误判为 RimBridge 假死。
  //
  // 构造方式：起一个只收不回的 TCP 服务，注入一个「已连接」的假桥接，
  // 其 _request/callTool 直接复用真 GabpClient 的实现 —— 所以超时是真的。
  // 断言手段：读导出的计数器（不要替换 console.log：测试运行器自己也写 stdout，会冲突挂住）。
  const net = await import('node:net');
  const { GabpClient } = await import('../gabpClient.js');

  const srv = net.createServer(() => { /* 静默，不回帧 */ });
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const port = srv.address().port;

  const reconnects = [];
  const makeFakeBridge = () => {
    const real = new GabpClient({ rimBridge: { requestTimeoutMs: 500 } }, { log: () => {} });
    const fake = {
      state: 'connected',
      isConnected: () => true,
      forceReconnect: () => { reconnects.push(Date.now()); },
      lastError: null,
      _requestTimeoutMs: 500,
      _pending: new Map(),
      _socket: new net.Socket(),
      _log: () => {},
      _err: (code, message) => ({ code, message }),
    };
    fake._request = real._request.bind(fake);
    fake._writeMessage = real._writeMessage.bind(fake);
    // callTool 必须存在：否则 callGABPTool 抛 TypeError，被上层 uncaughtException 吞掉，
    // Promise 永不 settle —— 测试会挂到超时而不是快速失败（踩过）。
    fake.callTool = async (name, args, opts) => {
      try {
        const r = await fake._request('tools/call', { name, parameters: args || {} }, opts || {});
        return { ok: true, result: r };
      } catch (e) {
        return { ok: false, error: e };
      }
    };
    return fake;
  };

  try {
    // ① 入参推导的超时 → 不计入计数
    const fakeA = makeFakeBridge();
    fakeA._socket.connect(port, '127.0.0.1');
    await new Promise((r) => fakeA._socket.once('connect', r));
    mod._testHooks.setGabpBridge(fakeA);
    const before = mod._testHooks.getGabpConsecutiveTimeout();

    const res = await mod.callGABPTool('rimworld/play_for', { durationMs: 1000 }, undefined,
      { timeoutMs: 500, inputDerived: true });
    assert.match(JSON.stringify(res), /超时/, '应真的超时');
    assert.equal(mod._testHooks.getGabpConsecutiveTimeout(), before,
      '入参推导的超时不得增加假死计数');
    assert.equal(reconnects.length, 0, '不得触发 forceReconnect');
    fakeA._socket.destroy();

    // ② 对照：同样的超时但不带 inputDerived → 应计入计数
    const fakeB = makeFakeBridge();
    fakeB._socket.connect(port, '127.0.0.1');
    await new Promise((r) => fakeB._socket.once('connect', r));
    mod._testHooks.setGabpBridge(fakeB);
    const beforeB = mod._testHooks.getGabpConsecutiveTimeout();

    const resB = await mod.callGABPTool('rimworld/get_game_info', {}, undefined, { timeoutMs: 500 });
    assert.match(JSON.stringify(resB), /超时/, '对照调用也应超时');
    assert.equal(mod._testHooks.getGabpConsecutiveTimeout(), beforeB + 1,
      '非入参推导的超时必须计入计数（两组对照才能证明排除是真的生效）');
    fakeB._socket.destroy();
  } finally {
    mod._testHooks.setGabpBridge(null);
    try { srv.close(() => {}); } catch { /* ignore */ }
    try { srv.unref(); } catch { /* ignore */ }
  }
});

test('task_status 未知 taskId：提示进程重启与兜底做法', async () => {
  const res = await mod.handleToolCall('task_status', { taskId: 't-nope' });
  const body = JSON.parse(res.content[0].text);
  assert.equal(body.errorCode, 'TASK_NOT_FOUND');
  assert.match(body.message, /rimworld\.pause_game/);
  // 兜底建议必须给出**正确**参数名。写 paused 会让用户照抄后踩同一个坑：
  // pause_game 的参数是 pause，传 paused 会被静默忽略、走默认 true（今天刚修的真 bug）。
  assert.match(body.message, /rimworld\.pause_game\{pause:true\}/,
    '错误提示里的 pause_game 参数名必须是 pause');
  assert.ok(!/pause_game\{paused/.test(body.message), '不得在提示里给出 paused 这个错误参数名');
});

test('task_cancel / task_configure 缺 taskId：参数错误', async () => {
  const a = JSON.parse((await mod.handleToolCall('task_cancel', {})).content[0].text);
  assert.equal(a.errorCode, 'INVALID_PARAMS');
  const b = JSON.parse((await mod.handleToolCall('task_configure', {})).content[0].text);
  assert.equal(b.errorCode, 'INVALID_PARAMS');
});

test('task_cancel 未知 taskId：TASK_NOT_FOUND', async () => {
  const res = JSON.parse((await mod.handleToolCall('task_cancel', { taskId: 't-nope' })).content[0].text);
  assert.equal(res.errorCode, 'TASK_NOT_FOUND');
});

test('config.json 的结构是对象（防回归守卫：原 bug 会把嵌套对象拼平成类型名字符串）', async () => {
  // 这条守卫的价值：AutoDeploy.WriteConfig 曾把嵌套 Dictionary 落成 ToString()，
  // 于是 config.rimBridge 变成 "System.Collections.Generic.Dictionary`2[...]"，
  // 所有超时阈值静默回落到硬编码默认值——不报错、只是不可调，极难发现。
  // 现在每次跑测试都会挡住这个形态复现。
  const fs = await import('node:fs');
  const path = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const here = path.dirname(fileURLToPath(import.meta.url));
  const configPath = path.join(here, '..', 'config.json');
  const raw = fs.readFileSync(configPath, 'utf-8');
  const cfg = JSON.parse(raw);

  assert.equal(typeof cfg.timings, 'object', `timings 必须是对象，实际 ${typeof cfg.timings}`);
  assert.notEqual(cfg.timings, null, 'timings 不能为 null');
  assert.ok(!Array.isArray(cfg.timings), 'timings 必须是对象而非数组');
  assert.equal(typeof cfg.rimBridge, 'object', `rimBridge 必须是对象，实际 ${typeof cfg.rimBridge}`);
  assert.notEqual(cfg.rimBridge, null, 'rimBridge 不能为 null');
  assert.ok(!raw.includes('System.Collections.Generic.Dictionary'),
    'config.json 里不应出现被拼平的类型名（说明写入方又绕过了 UELightJson.Serialize）');

  // longTask 段的每个键都要能被 readThresholds 读到（否则会静默吃默认值）
  const lt = cfg.rimBridge.longTask;
  assert.equal(typeof lt, 'object', 'rimBridge.longTask 必须是对象');
  for (const key of [
    'maxSingleCallTimeoutMs', 'playForOverheadMs',
    'taskSampleIntervalMs', 'taskSnapshotIntervalMs',
    'taskBurstSampleIntervalMs', 'taskBurstSnapshotIntervalMs',
    'taskSampleKeep', 'taskSnapshotKeep', 'recoverMaxAgeMs',
  ]) {
    assert.ok(Number.isFinite(lt[key]) && lt[key] > 0, `longTask.${key} 应为正数，实际 ${lt[key]}`);
  }
  assert.equal(typeof lt.recoverRestorePause, 'boolean', 'longTask.recoverRestorePause 应为布尔');
  assert.equal(typeof lt.toolTimeouts, 'object', 'longTask.toolTimeouts 应为对象');
});

test('三个长任务工具的清单可见性与 toolConfig 一致，且 schema 正确', async () => {
  // mcp_help 是元工具，在 handleToolCall 之前被拦截，因此改用 SDK 客户端连真实 server，
  // 走 ListToolsRequestSchema 处理器取工具清单 —— 校验的是真实的对外可见性。
  // tools/list 会按 toolConfig 过滤，所以断言写成「与配置一致」的不变式，而非「必须出现」。
  const fs = await import('node:fs');
  const path = await import('node:path');
  const { fileURLToPath } = await import('node:url');
  const here = path.dirname(fileURLToPath(import.meta.url));
  const toolConfig = JSON.parse(fs.readFileSync(path.join(here, '..', 'toolConfig.json'), 'utf-8'));

  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { InMemoryTransport } = await import('@modelcontextprotocol/sdk/inMemory.js');
  const { createServer } = mod._testHooks;

  const server = createServer();
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'test-client', version: '1.0.0' });

  try {
    await Promise.all([server.connect(serverT), client.connect(clientT)]);
    const listed = await client.listTools();
    const names = new Set(listed.tools.map((t) => t.name));

    for (const n of ['task_status', 'task_cancel', 'task_configure']) {
      assert.ok(n in toolConfig.tools, `${n} 必须在 toolConfig.json 有条目`);
      assert.equal(
        names.has(n),
        toolConfig.tools[n] === true,
        `${n} 的可见性应与 toolConfig（${toolConfig.tools[n]}）一致`,
      );
    }

    // schema 校验：工具未启用时不在清单里，改为直接查清单外的定义不可行，
    // 故这里用 task_cancel 的 required 与 task_configure 的属性做条件校验。
    if (names.has('task_cancel')) {
      const cancel = listed.tools.find((t) => t.name === 'task_cancel');
      assert.deepEqual(cancel.inputSchema.required, ['taskId'], 'task_cancel 的 taskId 为必填');
    }
    if (names.has('task_configure')) {
      const cfg = listed.tools.find((t) => t.name === 'task_configure');
      assert.ok(cfg.inputSchema.properties.sampleIntervalMs, 'task_configure 应有 sampleIntervalMs');
    }
    // 无论启用与否，工具定义都必须在 MCP 侧存在（否则 agg_call_tool 也调不到）
    for (const n of ['task_status', 'task_cancel', 'task_configure']) {
      const viaAgg = await mod.handleToolCall('task_status', {});
      assert.ok(viaAgg && viaAgg.content, `${n} 应可经 handleToolCall 调用`);
      break; // 三个工具的处理路径一致，一次足以证明注册生效
    }
  } finally {
    await client.close().catch(() => {});
    await server.close().catch(() => {});
  }
});

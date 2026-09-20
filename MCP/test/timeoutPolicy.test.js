import { test } from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import {
  resolveTimeoutMs,
  timeoutOptsFor,
  isInputDerivedTimeout,
  readThresholds,
} from '../longTask/timeoutPolicy.js';
import { GabpClient } from '../gabpClient.js';

/**
 * 起一个只收不回的 TCP 服务，用于观察超时定时器的取值。
 * 注意：**不要 await srv.close()**——实测本机 Node v24.13 下该回调永不触发
 * （服务端连接未真正关闭，且 server.closeAllConnections 为 undefined），
 * await 会让测试挂到超时。改为不等待关闭 + unref 监听句柄，让进程能自然退出。
 */
async function silentServer() {
  const srv = net.createServer(() => { /* 静默丢弃，不回任何帧 */ });
  await new Promise((r) => srv.listen(0, '127.0.0.1', r));
  const port = srv.address().port;
  return {
    port,
    close() {
      try {
        if (typeof srv.closeAllConnections === 'function') srv.closeAllConnections();
        srv.close();
        srv.unref();
      } catch (e) {
        /* 测试收尾失败不影响断言结果 */
      }
    },
  };
}

/** 建一个「已连接」的 GabpClient，跳过握手 */
async function connectedClient(port, requestTimeoutMs) {
  const client = new GabpClient({ rimBridge: { requestTimeoutMs } }, { log: () => {} });
  client._socket = new net.Socket();
  client._socket.connect(port, '127.0.0.1');
  await new Promise((r) => client._socket.once('connect', r));
  client.state = 'connected';
  return client;
}

const EMPTY_CFG = {};

test('T1 默认档：普通工具 30s', () => {
  assert.equal(resolveTimeoutMs('rimworld/list_colonists', {}, EMPTY_CFG), 30000);
  assert.equal(resolveTimeoutMs('get_game_status', {}, EMPTY_CFG), 30000);
});

test('T2 慢档：负载/存档/调试动作 60s', () => {
  assert.equal(resolveTimeoutMs('rimworld/load_game', {}, EMPTY_CFG), 60000);
  assert.equal(resolveTimeoutMs('rimworld/save_game', {}, EMPTY_CFG), 60000);
  assert.equal(resolveTimeoutMs('rimworld/execute_debug_action', {}, EMPTY_CFG), 60000);
  assert.equal(resolveTimeoutMs('create_hook', {}, EMPTY_CFG), 60000);
});

test('T3 入参推导：play_for 时长 + 余量', () => {
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 60000 }, EMPTY_CFG), 70000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 120000 }, EMPTY_CFG), 130000);
});

test('T3 入参推导：step_game_ticks 用自身 timeoutMs + 5s', () => {
  assert.equal(
    resolveTimeoutMs('rimworld/step_game_ticks', { ticks: 300, timeoutMs: 40000 }, EMPTY_CFG),
    45000,
  );
  assert.equal(
    resolveTimeoutMs('rimworld/step_game_ticks', { ticks: 300, timeoutMs: 20000 }, EMPTY_CFG),
    30000,
    '推导值 25000 低于默认档，按规格 §2.3 取 30000 下限',
  );
});

test('T3 入参推导：play_until_letter 用自身 timeoutMs + 5s', () => {
  assert.equal(
    resolveTimeoutMs('rimworld/play_until_letter', { timeoutMs: 120000 }, EMPTY_CFG),
    125000,
  );
  assert.equal(
    resolveTimeoutMs('rimworld/play_until_letter', { timeoutMs: 10000 }, EMPTY_CFG),
    30000,
  );
});

test('T3 派生：斜杠名、点号名、裸名三种写法一致', () => {
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 60000 }, EMPTY_CFG), 70000);
  assert.equal(resolveTimeoutMs('rimworld.play_for', { durationMs: 60000 }, EMPTY_CFG), 70000);
  assert.equal(resolveTimeoutMs('play_for', { durationMs: 60000 }, EMPTY_CFG), 70000);
});

test('T3 下限：推导值不低于 30s（短 durationMs 不缩短超时）', () => {
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 2000 }, EMPTY_CFG), 30000);
});

test('T3 缺参不推导：回落默认档', () => {
  assert.equal(resolveTimeoutMs('rimworld/play_for', {}, EMPTY_CFG), 30000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 0 }, EMPTY_CFG), 30000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: -5 }, EMPTY_CFG), 30000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 'abc' }, EMPTY_CFG), 30000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: NaN }, EMPTY_CFG), 30000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: Infinity }, EMPTY_CFG), 30000);
  assert.equal(resolveTimeoutMs('rimworld/play_for', null, EMPTY_CFG), 30000);
});

test('T3 缺参的 step_game_ticks：timeoutMs 是它自己的默认值 120000，不得误判为参推导', () => {
  assert.equal(resolveTimeoutMs('rimworld/step_game_ticks', { ticks: 100 }, EMPTY_CFG), 120000);
  assert.equal(isInputDerivedTimeout('rimworld/step_game_ticks', { ticks: 100 }, EMPTY_CFG), false);
});

test('T4 显式覆盖优先于一切', () => {
  const cfg = { rimBridge: { longTask: { toolTimeouts: { play_for: 40000 } } } };
  assert.equal(resolveTimeoutMs('rimworld/play_for', { durationMs: 600000 }, cfg), 40000);
  assert.equal(isInputDerivedTimeout('rimworld/play_for', { durationMs: 600000 }, cfg), false);
});

test('T4 显式覆盖：零值与负值视为未配置', () => {
  assert.equal(
    resolveTimeoutMs('rimworld/play_for', { durationMs: 60000 }, { rimBridge: { longTask: { toolTimeouts: { play_for: 0 } } } }),
    70000,
  );
  assert.equal(
    resolveTimeoutMs('rimworld/play_for', { durationMs: 60000 }, { rimBridge: { longTask: { toolTimeouts: { play_for: -1 } } } }),
    70000,
  );
});

test('readThresholds 读 config.rimBridge 与 longTask', () => {
  const t = readThresholds({
    rimBridge: {
      requestTimeoutMs: 5000,
      longTask: {
        maxSingleCallTimeoutMs: 900000,
        playForOverheadMs: 3000,
        taskSampleIntervalMs: 1000,
        taskSnapshotIntervalMs: 10000,
        taskBurstSampleIntervalMs: 800,
        taskBurstSnapshotIntervalMs: 6000,
        taskSampleKeep: 50,
        taskSnapshotKeep: 5,
        recoverRestorePause: true,
        recoverMaxAgeMs: 123456,
        toolTimeouts: { play_for: 1234 },
      },
    },
  });
  assert.equal(t.defaultMs, 5000);
  assert.equal(t.slowMs, 10000);
  assert.equal(t.maxSingleCallTimeoutMs, 900000);
  assert.equal(t.playForOverheadMs, 3000);
  assert.equal(t.taskSampleIntervalMs, 1000);
  assert.equal(t.taskSnapshotIntervalMs, 10000);
  assert.equal(t.taskBurstSampleIntervalMs, 800);
  assert.equal(t.taskBurstSnapshotIntervalMs, 6000);
  assert.equal(t.taskSampleKeep, 50);
  assert.equal(t.taskSnapshotKeep, 5);
  assert.equal(t.recoverRestorePause, true);
  assert.equal(t.recoverMaxAgeMs, 123456);
  assert.deepEqual(t.toolTimeouts, { play_for: 1234 });
});

test('readThresholds：recoverRestorePause 默认 false（不自动动游戏）', () => {
  assert.equal(readThresholds({}).recoverRestorePause, false);
  assert.equal(readThresholds({}).recoverMaxAgeMs, 600000);
});

test('readThresholds 对损坏配置容错（字符串/缺失/null）', () => {
  const a = readThresholds({ rimBridge: "System.Collections.Generic.Dictionary`2[System.String,System.Object]" });
  assert.equal(a.maxSingleCallTimeoutMs, 1800000);
  assert.equal(a.taskSampleIntervalMs, 2000);
  assert.deepEqual(a.toolTimeouts, {});
  const b = readThresholds(null);
  assert.equal(b.defaultMs, 30000);
  assert.equal(b.slowMs, 60000);
});

test('timeoutOptsFor：入参推导时返回 {timeoutMs, inputDerived:true}，否则 null', () => {
  assert.deepEqual(timeoutOptsFor('rimworld/play_for', { durationMs: 60000 }, EMPTY_CFG), {
    timeoutMs: 70000,
    inputDerived: true,
  });
  assert.equal(timeoutOptsFor('rimworld/list_colonists', {}, EMPTY_CFG), null);
  assert.deepEqual(timeoutOptsFor('rimworld/load_game', {}, EMPTY_CFG), {
    timeoutMs: 60000,
    inputDerived: false,
  });
});

test('gabpClient._request 认显式 opts.timeoutMs（优先于 slow）', async () => {
  const srv = await silentServer();
  const client = await connectedClient(srv.port, 30000);
  try {
    const t0 = Date.now();
    // 注意 _request 是 (method, params, opts) 三参签名；slow 与 timeoutMs 都在第 3 个参数里。
    // slow:true 本会给出 60000ms，显式 120ms 必须获胜。
    // 它拒绝的是 {code, message} 结构（不是 Error 实例），所以断言要打在 .message 上。
    const err = await client
      ._request('tools/call', { name: 'x', parameters: {} }, { timeoutMs: 120, slow: true })
      .then(() => null, (e) => e);
    const dt = Date.now() - t0;
    assert.ok(err, '应当超时拒绝，但请求成功返回了');
    assert.match(String(err.message), /超时（120ms）/, `实际 message=${err.message}`);
    assert.ok(dt >= 100 && dt < 5000, `应在 ~120ms 超时，实测 ${dt}ms`);
  } finally {
    client._socket.destroy();
    srv.close();
  }
});

test('gabpClient._request 无显式 timeoutMs 时维持 slow 翻倍语义', async () => {
  const srv = await silentServer();
  const client = await connectedClient(srv.port, 150);
  try {
    // requestTimeoutMs=150 → slow 档应为 300ms（证明未被显式逻辑破坏）
    const slowErr = await client
      ._request('tools/call', { name: 'x', parameters: {} }, { slow: true })
      .then(() => null, (e) => e);
    assert.match(String(slowErr && slowErr.message), /超时（300ms）/);
    const plainErr = await client
      ._request('tools/call', { name: 'x', parameters: {} }, {})
      .then(() => null, (e) => e);
    assert.match(String(plainErr && plainErr.message), /超时（150ms）/);
  } finally {
    client._socket.destroy();
    srv.close();
  }
});


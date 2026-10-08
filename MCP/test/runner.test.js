import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRunner } from '../longTask/runner.js';

/**
 * 假环境：虚拟时钟 + 可控 RPC + 立即 resolve 的 sleep。
 * sleep 直接推进虚拟时间，因此一个"2 小时"的任务在测试里是瞬时完成的。
 *
 * 心跳工具是 rimworld/get_game_info（已用 rimbridge/list_capabilities 核实真实工具名），
 * 它只返回 ticksGame、**不返回 paused**。因此：
 *   - "启动前是否在跑" 靠 probeWasRunning（连续两次读，tick 前进即在跑）；
 *   - "外部暂停/卡死" 靠 tick 停滞（连续 STALE_SAMPLE_LIMIT 轮 tick 不前进）。
 * freezeAfterTicks：从第 N 次心跳读开始冻结 tick，模拟外部暂停/游戏卡住。
 */
function makeDeps({ script = [], tickRate = 1, freezeAfterTicks = null, tickSource = null } = {}) {
  const t = { ms: 0 };
  const calls = [];
  const journalLines = [];
  const snapshotLines = [];
  let tickReads = 0;
  let frozenAt = null;
  const deps = {
    now: () => t.ms,
    // 推进虚拟时间，同时让出一个宏任务：这样测试里的「等几轮再取消」才有机会插进循环中间，
    // 而不是被一连串微任务一路跑到终态。
    sleep: (ms) => new Promise((r) => setTimeout(() => { t.ms += ms; r(); }, 0)),
    journalDir: '/fake/tasks',
    sampleDir: '/fake/samples',
    journalAppend: (file, rec) => { journalLines.push({ file, ...rec }); return true; },
    appendSnapshot: (file, entry, payload) => { snapshotLines.push({ file, entry, payload }); return true; },
    call: async (name, args, opts) => {
      calls.push({ name, args, opts });
      if (name === 'rimworld/get_game_info') {
        tickReads++;
        if (freezeAfterTicks !== null && tickReads > freezeAfterTicks) {
          if (frozenAt === null) frozenAt = t.ms * tickRate;
          return { ok: true, result: { ticksGame: frozenAt, status: 'game_loaded' } };
        }
        // tickSource 可注入一个真实语义的 tick 值（如可控计数器），否则用虚拟时钟派生
        return {
          ok: true,
          result: { ticksGame: tickSource ? tickSource() : t.ms * tickRate, status: 'game_loaded' },
        };
      }
      if (script.length > 0) {
        const idx = calls.filter((c) => c.name === name).length - 1;
        if (script[idx]) return script[idx];
      }
      return { ok: true, result: { success: true } };
    },
  };
  return { deps, t, calls, journalLines, snapshotLines, getTickReads: () => tickReads };
}

const BASE = {
  taskId: 't-0001',
  runId: '2026-09-17-t-0001',
  durationMs: 60000,
  speed: 'Superfast',
  sampleIntervalMs: 1000,
  snapshotIntervalMs: 30000,
  snapshotPath: '/fake/samples/a.jsonl',
  pauseOnFinish: 'restore',
};

/** 常用节奏：启动 → 等到终态。返回 runner 与最终快照。 */
async function runToCompletion(opts, depsOpts = {}) {
  const env = makeDeps(depsOpts);
  const r = createRunner(env.deps);
  const started = await r.start({ ...BASE, ...opts });
  assert.equal(started.success, true, `start 失败: ${started.message || started.errorCode}`);
  await started._done;
  return { ...env, r, final: r.get(BASE.taskId), started };
}

test('start：先设速度、再探测游戏是否在跑、再解暂停、然后按节奏采样', async () => {
  const { calls } = await runToCompletion({ durationMs: 3000, sampleIntervalMs: 1000 });
  const names = calls.map((c) => c.name);
  assert.equal(names[0], 'rimworld/set_time_speed');
  assert.equal(names[1], 'rimworld/get_game_info', '第 2 步开始探测（连续两次读 tick）');
  assert.equal(names[2], 'rimworld/get_game_info', '探测需要两次读');
  assert.equal(names[3], 'rimworld/pause_game');
  assert.deepEqual(calls[3].args, { pause: false });
  // 心跳走的是真实存在的工具名；曾经的 rimworld/get_game_status 在 RimBridge 侧不存在
  assert.ok(!names.includes('rimworld/get_game_status'), '心跳不得使用 game_status（该工具不存在）');
  assert.ok(names.filter((n) => n === 'rimworld/get_game_info').length >= 4, '探测 2 次 + 循环内采样');
});

test('探测：解暂停前先连续两次读 tick，且都在解暂停之前', async () => {
  const { calls } = await runToCompletion({ durationMs: 1000, sampleIntervalMs: 1000 });
  const pauseIdx = calls.findIndex((c) => c.name === 'rimworld/pause_game');
  const heartbeatIdxs = calls
    .map((c, i) => (c.name === 'rimworld/get_game_info' ? i : -1))
    .filter((i) => i >= 0);
  assert.ok(heartbeatIdxs.length >= 2, '至少两次心跳读');
  assert.ok(heartbeatIdxs[0] < pauseIdx && heartbeatIdxs[1] < pauseIdx, '两次探测读都必须在解暂停之前');
});

test('探测：启动前 tick 在走 → wasRunning=true，pauseOnFinish=restore 结束时保持运行', async () => {
  // 虚拟时钟在 sleep 后推进，因此两次探测读会读到不同 tick → running
  const { deps, calls } = makeDeps();
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 3000, sampleIntervalMs: 1000, pauseOnFinish: 'restore' });
  await started._done;
  const pauseCalls = calls.filter((c) => c.name === 'rimworld/pause_game');
  assert.deepEqual(pauseCalls[pauseCalls.length - 1].args, { pause: false }, '原本在跑 → 结束时保持运行');
});

test('探测：启动前 tick 冻结 → wasRunning=false，pauseOnFinish=restore 结束时暂停', async () => {
  const { deps, calls } = makeDeps({ freezeAfterTicks: 0 });
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 3000, sampleIntervalMs: 1000, pauseOnFinish: 'restore' });
  await started._done;
  assert.equal(r.get('t-0001').pausedBefore, true);
  const pauseCalls = calls.filter((c) => c.name === 'rimworld/pause_game');
  assert.deepEqual(pauseCalls[pauseCalls.length - 1].args, { pause: true });
});

test('同时只允许一个 running 任务', async () => {
  const { deps } = makeDeps();
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 600000 });
  const second = await r.start({ ...BASE, taskId: 't-0002' });
  assert.equal(second.success, false);
  assert.equal(second.errorCode, 'TASK_ALREADY_RUNNING');
  assert.equal(second.runningTaskId, 't-0001');
  const stopRes = r.stop('t-0001');
  assert.equal(stopRes.success, true);
  await started._done;
  assert.equal(r.get('t-0001').state, 'cancelled');
});

test('正常跑完：state=completed，写入 start 与 end，采样/快照计数正确', async () => {
  const { journalLines, final } = await runToCompletion({
    durationMs: 5000, sampleIntervalMs: 1000, snapshotIntervalMs: 2000,
  });
  assert.equal(final.state, 'completed');
  assert.ok(final.samplesDone >= 3 && final.samplesDone <= 5, `5000ms/1000ms 应采到 3~5 点，实际 ${final.samplesDone}`);
  assert.ok(final.snapshotsDone >= 1 && final.snapshotsDone <= 3, `2000ms 快照间隔应采到 1~3 次，实际 ${final.snapshotsDone}`);
  assert.equal(final.completionReason, 'duration-elapsed');
  assert.ok(journalLines.some((l) => l.type === 'start'));
  assert.ok(journalLines.some((l) => l.type === 'end' && l.state === 'completed'));
});

test('快照间隔强制联动：采样 <= 阈值时快照压到 burst 值并标记 coerced', async () => {
  const { final } = await runToCompletion({
    durationMs: 60000, sampleIntervalMs: 1000, snapshotIntervalMs: 30000,
    burst: { sampleMs: 1000, snapshotMs: 10000 },
  });
  assert.equal(final.snapshotIntervalMs, 10000);
  assert.equal(final.snapshotIntervalMsCoerced, true);
  assert.ok(final.snapshotsDone >= 4 && final.snapshotsDone <= 7, `10000ms 快照间隔应采到 4~7 次，实际 ${final.snapshotsDone}`);
});

test('快照间隔不联动：采样高于阈值时保留用户值', async () => {
  const { final } = await runToCompletion({
    durationMs: 60000, sampleIntervalMs: 2000, snapshotIntervalMs: 30000,
    burst: { sampleMs: 1000, snapshotMs: 10000 },
  });
  assert.equal(final.snapshotIntervalMs, 30000);
  assert.equal(final.snapshotIntervalMsCoerced, false);
  assert.ok(final.snapshotsDone >= 1 && final.snapshotsDone <= 2, `30000ms 快照间隔应采到 1~2 次，实际 ${final.snapshotsDone}`);
});

test('外部暂停（tick 持续停滞）→ aborted + completionReason=tick-stalled', async () => {
  // 判据是「tick 持续 stallAbortMs 未前进」，不是「连续 N 轮」。
  // 这里把阈值压到 3s（采样 1s），冻结从第 3 次心跳读开始（前 2 次是启动探测）。
  const { final } = await runToCompletion(
    { durationMs: 600000, sampleIntervalMs: 1000, stallAbortMs: 3000 },
    { freezeAfterTicks: 2 },
  );
  assert.equal(final.state, 'aborted');
  assert.equal(final.completionReason, 'tick-stalled');
  assert.match(String(final.error), /持续 \d+s 未前进/);
  assert.ok(final.samplesDone <= 5, '停滞 3s 后应很快中止');
});

test('短暂停顿不中止（关键回归：原先按 3 轮判会误杀 2h 任务）', async () => {
  // 真实事故：2 小时任务在 34 分钟时因「连续 3 轮（6s）tick 未前进」被打成 aborted，
  // 而游戏只是自动存档/跑长事件停了 6 秒，随后继续推进（实测 20s 内又走了 ~2856 ticks）。
  // 现在按时长判：默认 60s。这里模拟「冻结 4 轮后恢复」，应完整跑完不中止。
  const { deps } = makeDeps();
  const origCall = deps.call;
  let tickReads = 0;
  deps.call = async (name, args, opts) => {
    if (name === 'rimworld/get_game_info') {
      tickReads++;
      if (tickReads >= 3 && tickReads <= 6) {
        return { ok: true, result: { ticksGame: 1000, status: 'game_loaded' } };
      }
      return { ok: true, result: { ticksGame: 1000 + tickReads * 10, status: 'game_loaded' } };
    }
    return origCall(name, args, opts);
  };
  const r = createRunner(deps);
  const started = await r.start({
    ...BASE, durationMs: 8000, sampleIntervalMs: 1000, stallAbortMs: 60000,
  });
  await started._done;
  const final = r.get('t-0001');
  assert.equal(final.state, 'completed', `短暂停顿不应中止，实际 ${final.state} / ${final.completionReason}`);
  assert.ok(final.samplesDone >= 6, `应继续采完，实际 ${final.samplesDone}`);
});

test('取消：最多一个采样周期内停止，state=cancelled', async () => {
  const { deps } = makeDeps();
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 600000, sampleIntervalMs: 1000 });
  for (let i = 0; i < 30; i++) await new Promise((r) => setTimeout(r, 0));
  r.stop('t-0001');
  await started._done;
  const final = r.get('t-0001');
  assert.equal(final.state, 'cancelled');
  assert.equal(final.completionReason, 'cancelled-by-request');
  assert.ok(final.samplesDone >= 1);
  assert.ok(final.samplesDone < 600, '取消后不应跑满整个时长');
});

test('pauseOnFinish=keep：结束时不动暂停状态', async () => {
  const { deps, calls } = makeDeps();
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 3000, sampleIntervalMs: 1000, pauseOnFinish: 'keep' });
  await started._done;
  const pauseCalls = calls.filter((c) => c.name === 'rimworld/pause_game');
  assert.equal(pauseCalls.length, 1, '只应有启动时那次解暂停');
  assert.deepEqual(pauseCalls[0].args, { pause: false });
});

test('pauseOnFinish=pause：启动前未暂停也强制暂停', async () => {
  const { deps, calls } = makeDeps();
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 3000, sampleIntervalMs: 1000, pauseOnFinish: 'pause' });
  await started._done;
  const pauseCalls = calls.filter((c) => c.name === 'rimworld/pause_game');
  assert.deepEqual(pauseCalls[pauseCalls.length - 1].args, { pause: true });
});

test('参数名回归护栏：解暂停必须用 pause=false（不是 paused=false）', async () => {
  // 真实事故：rimworld/pause_game 的参数名是 pause（默认 true），传 paused 会被忽略、
  // 走默认值 → 本意解暂停却执行了暂停，游戏不动、任务被停滞检测判为 aborted，而调用无任何报错。
  // 这条护栏钉住参数名，防止回归时又写错。
  const { calls } = await runToCompletion({ durationMs: 2000, sampleIntervalMs: 1000 });
  const pauseCalls = calls.filter((c) => c.name === 'rimworld/pause_game');
  assert.ok(pauseCalls.length >= 1, '必须调用过 pause_game');
  const first = pauseCalls[0];
  assert.equal(first.args.pause, false, '启动时必须 pause=false 解暂停');
  assert.ok(!('paused' in first.args), '不得使用错误参数名 paused（会被游戏忽略并走默认 true）');
  const unpauseIdx = calls.findIndex((c) => c.name === 'rimworld/pause_game');
  const setSpeedIdx = calls.findIndex((c) => c.name === 'rimworld/set_time_speed');
  assert.ok(setSpeedIdx >= 0 && setSpeedIdx < unpauseIdx, '必须先设速度再解暂停');
  assert.deepEqual(calls[setSpeedIdx].args, { speed: BASE.speed }, 'set_time_speed 只传 speed');
});

test('mustCall 抓「传输成功但操作未生效」：载荷 success=false 视为失败', async () => {
  const { deps } = makeDeps();
  const origCall = deps.call;
  deps.call = async (name, args, opts) => {
    if (name === 'rimworld/set_time_speed') {
      // 模拟「工具存在但参数被拒」：传输层 ok，载荷 success=false
      return { ok: true, result: { success: false, message: 'Unknown time speed' } };
    }
    return origCall(name, args, opts);
  };
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 3000, sampleIntervalMs: 1000 });
  await started._done;
  const final = r.get('t-0001');
  assert.equal(final.state, 'failed');
  assert.match(String(final.error), /未生效|Unknown time speed/);
});

test('上游 RPC 失败：state=failed 且 error 可读，且仍尝试恢复暂停', async () => {
  const { deps, calls } = makeDeps();
  // 就地替换 deps.call（不要重新赋值 deps 变量——runner 持有的是原对象引用）
  const origCall = deps.call;
  const pauseRestores = [];
  deps.call = async (name, args, opts) => {
    if (name === 'rimworld/set_time_speed') {
      calls.push({ name, args, opts });
      return { ok: false, error: { code: -32603, message: 'GABP 连接已关闭' } };
    }
    if (name === 'rimworld/pause_game' && args && args.pause === true) {
      pauseRestores.push({ ...args });
    }
    return origCall(name, args, opts);
  };
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 3000, sampleIntervalMs: 1000 });
  await started._done;
  const final = r.get('t-0001');
  assert.equal(final.state, 'failed');
  assert.match(String(final.error), /GABP 连接已关闭/);
  assert.equal(pauseRestores.length, 1, '失败时也必须把游戏恢复成暂停，否则会静默留在运行态');
});

test('configure：任务运行中改节奏，不中断任务；联动判定沿用启动时的 burst 阈值', async () => {
  const { deps } = makeDeps();
  const r = createRunner(deps);
  const started = await r.start({
    ...BASE, durationMs: 600000, sampleIntervalMs: 2000, snapshotIntervalMs: 30000,
    burst: { sampleMs: 1000, snapshotMs: 10000 },
  });
  // 2000 > burst.sampleMs(1000) → 不联动，保留 30000
  const same = r.configure('t-0001', { sampleIntervalMs: 2000, snapshotIntervalMs: 30000 });
  assert.equal(same.success, true);
  assert.equal(same.snapshotIntervalMs, 30000);
  assert.equal(same.snapshotIntervalMsCoerced, false);
  // 压到 1000 → 联动到 10000
  const burstRes = r.configure('t-0001', { sampleIntervalMs: 1000, snapshotIntervalMs: 30000 });
  assert.equal(burstRes.sampleIntervalMs, 1000);
  assert.equal(burstRes.snapshotIntervalMs, 10000);
  assert.equal(burstRes.snapshotIntervalMsCoerced, true);
  // 退出猝发 → 回到 2000/30000
  const back = r.configure('t-0001', { sampleIntervalMs: 2000, snapshotIntervalMs: 30000 });
  assert.equal(back.snapshotIntervalMs, 30000);
  assert.equal(back.snapshotIntervalMsCoerced, false);
  r.stop('t-0001');
  await started._done;
  assert.equal(r.get('t-0001').state, 'cancelled');
});

test('configure：对不存在或已结束的任务返回可读错误', async () => {
  const { deps } = makeDeps();
  const r = createRunner(deps);
  assert.equal(r.configure('nope', { sampleIntervalMs: 1000 }).errorCode, 'TASK_NOT_FOUND');
  const started = await r.start({ ...BASE, durationMs: 1000, sampleIntervalMs: 1000 });
  await started._done;
  assert.equal(r.get('t-0001').state, 'completed');
  assert.equal(r.configure('t-0001', { sampleIntervalMs: 1000 }).errorCode, 'TASK_NOT_RUNNING');
});

test('list：返回最近任务视图', async () => {
  const { r } = await runToCompletion({ durationMs: 1000, sampleIntervalMs: 1000 });
  const list = r.list();
  assert.equal(list.length, 1);
  assert.equal(list[0].taskId, 't-0001');
  assert.equal(list[0].state, 'completed');
});

test('保留上限：samples/snapshots 只留最近 N 条，累计计数不受影响', async () => {
  const { final } = await runToCompletion({
    durationMs: 10000, sampleIntervalMs: 1000, snapshotIntervalMs: 1000,
    keep: { samples: 3, snapshots: 2 },
  });
  assert.equal(final.samples.length, 3);
  assert.equal(final.snapshots.length, 2);
  assert.ok(final.samplesDone >= 8, `累计采样数应 >= 8，实际 ${final.samplesDone}`);
  assert.ok(final.snapshotsDone >= 8, `累计快照数应 >= 8，实际 ${final.snapshotsDone}`);
});

test('样本里带 tps（Δticks / Δ秒）', async () => {
  // tickRate 语义 = 每毫秒推进多少 tick；0.06 即 60 ticks/秒（Superfast 的正常量级）
  const { final } = await runToCompletion(
    { durationMs: 3000, sampleIntervalMs: 1000 },
    { tickRate: 0.06 },
  );
  assert.ok(final.samples.length >= 2 && final.samples.length <= 3, `3000ms/1000ms 应留 2~3 点`);
  // 跳过第一个采样点：它的 Δt 包含整段启动开销（探测窗口等），tps 天然偏低。
  const steady = final.samples.slice(1);
  assert.ok(steady.length >= 1, '至少要有 1 个稳态采样点');
  assert.ok(Math.abs(steady[0].tps - 60) < 0.001, `稳态 tps 应为 60，实际 ${steady[0].tps}`);
});

test('快照落盘：每次快照调用 appendSnapshot 追加一行', async () => {
  const { snapshotLines } = await runToCompletion({
    durationMs: 4000, sampleIntervalMs: 1000, snapshotIntervalMs: 2000,
  });
  assert.ok(snapshotLines.length >= 1 && snapshotLines.length <= 2, `应落盘 1~2 条快照，实际 ${snapshotLines.length}`);
  assert.equal(snapshotLines[0].file, '/fake/samples/a.jsonl');
  assert.ok(snapshotLines[0].entry.atMs >= 2000 && snapshotLines[0].entry.atMs < 6000, `首次快照时间应在 2000~6000ms 之间（含启动探测开销），实际 \${snapshotLines[0].entry.atMs}`);
});

test('采样点全量落盘：appendSample 每轮调用一次，不受内存滚动窗口限制', async () => {
  // 真实缺陷：采样点原先只进内存数组（保留最近 keep.samples 条），从不落盘。
  // 1s 采样跑 1 小时会丢掉 3400/3600 个原始点，与 DPA 环形缓冲是同一类问题。
  const { deps } = makeDeps();
  const appends = [];
  deps.appendSample = (file, sample) => { appends.push({ file, ...sample }); };
  const r = createRunner(deps);
  const started = await r.start({
    ...BASE, durationMs: 10000, sampleIntervalMs: 1000, snapshotIntervalMs: 30000,
    keep: { samples: 3, snapshots: 2 }, samplePath: '/fake/samples/persisted.jsonl',
  });
  await started._done;
  const final = r.get('t-0001');
  assert.ok(final.samplesDone >= 8, `累计采样应 >= 8，实际 ${final.samplesDone}`);
  assert.equal(final.samples.length, 3, '内存里只保留滚动窗口 3 条');
  assert.equal(appends.length, final.samplesDone, '落盘条数必须等于累计采样数（全量，不截断）');
  assert.equal(appends[0].file, '/fake/samples/persisted.jsonl', '应写入 samplePath');
  assert.ok(Number.isFinite(appends[0].ticksGame), '落盘内容含 ticksGame');
  assert.ok(appends[appends.length - 1].atMs > appends[0].atMs, '落盘按时间递增');
});

test('未注入 appendSample 时不炸（可选依赖）', async () => {
  const { deps } = makeDeps();
  delete deps.appendSample;
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 2000, sampleIntervalMs: 1000 });
  await started._done;
  assert.equal(r.get('t-0001').state, 'completed');
});

test('samplePath 兼容旧名 snapshotPath', async () => {
  const { deps } = makeDeps();
  const r = createRunner(deps);
  const started = await r.start({
    ...BASE, durationMs: 1000, sampleIntervalMs: 1000, snapshotPath: '/legacy/path.jsonl',
  });
  assert.equal(started.samplePath, '/legacy/path.jsonl');
  await started._done;
});

test('recover：孤儿任务记账，默认不动游戏；开启开关且未超龄时才恢复暂停', async () => {
  // 用可控真 tick（数量级与真实游戏一致），避免用 epoch 毫秒当 tick 导致比较永远成立
  let ticks = 5000;
  const { deps, t, calls, journalLines } = makeDeps({ tickSource: () => ticks });
  const r = createRunner(deps);
  // startedAtUtc 取当前虚拟时刻，使 age 可控（虚拟时钟从 0 起，用真实日期会算出巨大负 age）
  const startedAtUtc = new Date(t.ms).toISOString();
  const orphan = {
    taskId: 't-dead', runId: '2026-09-17-t-dead', pid: 999999,
    startedAtUtc,
    journalPath: '/fake/tasks/2026-09-17-t-dead.journal.jsonl',
    lastCheckpoint: { atMs: 30000, ticksGame: 100 },
  };
  t.ms = 1000; // age 可控：1000ms

  // 默认 restorePause=false：只记账，不动游戏
  const a = await r.recover([orphan], { maxAgeMs: Number.MAX_SAFE_INTEGER });
  assert.equal(a[0].action, 'recorded-game-still-running', `实际 ${JSON.stringify(a[0])}`);
  assert.ok(!calls.some((c) => c.name === 'rimworld/pause_game'), '默认不得自动暂停游戏');
  assert.ok(journalLines.some((l) => l.type === 'end' && l.state === 'orphaned'));

  // 幂等：再跑一次仍是同一判断
  const b = await r.recover([orphan], { maxAgeMs: Number.MAX_SAFE_INTEGER });
  assert.equal(b[0].action, 'recorded-game-still-running');

  // 显式开启且未超龄：允许自动恢复暂停
  const c = await r.recover([orphan], { restorePause: true, maxAgeMs: Number.MAX_SAFE_INTEGER });
  assert.equal(c[0].action, 'paused-game');
  assert.ok(calls.some((x) => x.name === 'rimworld/pause_game' && x.args.pause === true));

  // 超龄（age=1000ms > maxAge=1ms）→ 即便开了开关也不自动暂停
  calls.length = 0;
  const d = await r.recover([orphan], { restorePause: true, maxAgeMs: 1 });
  assert.equal(d[0].action, 'recorded-game-still-running');
  assert.ok(!calls.some((x) => x.name === 'rimworld/pause_game'));

  // tick 未前进 → 判定游戏已停/已重载
  ticks = 100; // 与 checkpoint 相同 → 没有前进
  const e = await r.recover([orphan], {});
  assert.equal(e[0].action, 'recorded-game-idle', `实际 ${JSON.stringify(e[0])}`);

  // 探测本身失败 → deferred-probe-failed（**不写终态**，留待下次启动重试）
  const broken = { ...deps, call: async () => ({ ok: false, error: { message: '桥接未连接' } }) };
  const r2 = createRunner(broken);
  const f = await r2.recover([orphan], {});
  assert.equal(f[0].action, 'deferred-probe-failed');
  assert.match(String(f[0].error), /桥接未连接/);
  assert.equal(f[0].recorded, false, '探测失败不得写终态记录（否则孤儿状态被消费、无法重试）');
});

test('recover：GABP 未就绪时不探测、不记账（真机缺陷回归）', async () => {
  // 真实事故：恢复逻辑在启动路径上跑，此时 GABP 还是 idle（惰性连接），
  // 而探测直接调 gabpBridge.callTool 绕过 ensureGabpConnected → action 恒为 probe-failed，
  // 且写了终态记录导致孤儿状态被一次性消费、再也重试不了。
  // 现在：调用方先 ensureGabpConnected，未就绪则传 gabpReady=false → 推迟到下次启动。
  let probeCalls = 0;
  const { deps, journalLines } = makeDeps();
  const origCall = deps.call;
  deps.call = async (name, args, opts) => {
    if (name === 'rimworld/get_game_info') probeCalls++;
    return origCall(name, args, opts);
  };
  const r = createRunner(deps);
  const orphan = {
    taskId: 't-orphan', runId: '2026-09-18-t-orphan', pid: 999999,
    startedAtUtc: new Date(0).toISOString(),
    journalPath: '/fake/tasks/2026-09-18-t-orphan.journal.jsonl',
    lastCheckpoint: { atMs: 30000, ticksGame: 100 },
  };
  const res = await r.recover([orphan], { gabpReady: false });
  assert.equal(res[0].action, 'deferred-gabp-not-ready');
  assert.equal(res[0].recorded, false, 'GABP 未就绪不得写终态记录');
  assert.equal(probeCalls, 0, '既然没就绪就不该白试一次探测');
  assert.equal(journalLines.filter((l) => l.type === 'end').length, 0, '不得留下 end 记录');
});

test('recover：无 checkpoint 的短命任务用 startTicksGame 兜底（真机缺陷回归）', async () => {
  // 实测事故：任务跑得比 checkpoint 周期（30 个采样点）还短 → 一个 checkpoint 都没有 →
  // lastTicks=null → 恢复逻辑误判成 recorded-game-idle，而游戏其实一直在跑。
  let ticks = 5000;
  const { deps, journalLines } = makeDeps({ tickSource: () => ticks });
  const r = createRunner(deps);
  const orphan = {
    taskId: 't-short', runId: '2026-09-18-t-short', pid: 999999,
    startedAtUtc: new Date(0).toISOString(),
    journalPath: '/fake/tasks/2026-09-18-t-short.journal.jsonl',
    startTicksGame: 4008607,   // note 记录里的起始 tick
    lastCheckpoint: null,       // 没有 checkpoint
  };
  ticks = 4010000;              // 游戏已推进
  const res = await r.recover([orphan], {});
  assert.equal(res[0].action, 'recorded-game-still-running',
    `应据 startTicksGame 判定游戏仍在跑，实际 ${JSON.stringify(res[0])}`);
  assert.ok(journalLines.some((l) => l.type === 'end' && l.state === 'orphaned'));

  // 对照：tick 未前进则仍判 idle
  ticks = 4008607;
  const res2 = await r.recover([orphan], {});
  assert.equal(res2[0].action, 'recorded-game-idle');
});

test('readOrphans 提取 note 里的 startTicksGame', async () => {
  // 上游保证：journal 里 note 记录带 startTicksGame，readOrphans 必须把它带出来，
  // 否则 runner 没有兜底值可用。
  const fs = await import('node:fs');
  const os = await import('node:os');
  const path = await import('node:path');
  const { readOrphans, appendRecord } = await import('../longTask/journal.js');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'uesd-orphan-'));
  const file = path.join(dir, '2026-09-18-t-x.journal.jsonl');
  appendRecord(file, {
    type: 'start', taskId: 't-x', runId: '2026-09-18-t-x', pid: 123,
    startedAtUtc: '2026-09-18T11:10:14.848Z', requestedDurationMs: 3600000,
  });
  appendRecord(file, { type: 'note', atMs: 562, startTicksGame: 4008607, wasRunning: true, probeGapMs: 500 });
  const orphans = readOrphans(dir);
  assert.equal(orphans.length, 1);
  assert.equal(orphans[0].startTicksGame, 4008607);
  assert.equal(orphans[0].wasRunning, true);
  assert.equal(orphans[0].lastCheckpoint, null);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('end 记录带 error 字段（可观测性回归）', async () => {
  // 真实困扰：一条只含 {"state":"failed","reason":"error"} 的 end 记录，事后完全看不出失败原因。
  const { deps, journalLines } = makeDeps();
  const origCall = deps.call;
  deps.call = async (name, args, opts) => {
    if (name === 'rimworld/set_time_speed') {
      return { ok: false, error: { message: 'GABP 连接已关闭' } };
    }
    return origCall(name, args, opts);
  };
  const r = createRunner(deps);
  const started = await r.start({ ...BASE, durationMs: 3000, sampleIntervalMs: 1000 });
  await started._done;
  const end = journalLines.find((l) => l.type === 'end');
  assert.ok(end, '应有 end 记录');
  assert.equal(end.state, 'failed');
  assert.match(String(end.error), /GABP 连接已关闭/, 'end 记录必须能看出失败原因');
  assert.equal(typeof end.samplesDone, 'number');
});








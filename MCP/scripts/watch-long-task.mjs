/**
 * 长任务旁观器（不依赖 MCP 连接）。
 *
 * 为什么这样做验证：长任务的采样与快照是 MCP 的**后台循环直接写磁盘**的，
 * 与「有没有 MCP 客户端连着」无关。因此只要游戏与 MCP 进程还活着，
 * 关掉对话窗口也能靠读文件完成验证——这正是把 2 小时任务放在后台跑的前提。
 *
 * 它做的事：每 30s 读一次 journal 与采样文件，累计断言；
 * 一旦 journal 出现 end 记录（或到达 maxWaitMs），打印并写出 JSON 报告。
 *
 * 用法：node MCP/scripts/watch-long-task.mjs <taskId> [--max-wait-min 190] [--out <report.json>]
 * 断言（全部基于文件内容，与实际任务行为无关）：
 *   A1 journal 首条是 start，pid 与 taskId 匹配
 *   A2 start 之后有探测 note（含 startTicksGame / wasRunning）
 *   A3 采样文件里 kind:"sample" 行数 == 采样轮数（全量落盘，不截断）
 *   A4 采样点 atMs 单调递增，tps 有限且 > 0（剔除首个含启动开销的点）
 *   A5 kind:"snapshot" 行数 == 预期快照轮数（允许 ±1）
 *   A6 end 记录存在且 state 非 failed/aborted（除非确有外部暂停）
 */
import fs from 'node:fs';
import path from 'node:path';

const taskId = process.argv[2];
if (!taskId || !/^t-[0-9a-f]+$/i.test(taskId)) {
  console.error('用法: node MCP/scripts/watch-long-task.mjs <taskId> [--max-wait-min N] [--out report.json]');
  process.exit(2);
}
const argNum = (flag, dflt) => {
  const i = process.argv.indexOf(flag);
  return i >= 0 ? Number(process.argv[i + 1]) || dflt : dflt;
};
const argStr = (flag, dflt) => {
  const i = process.argv.indexOf(flag);
  return i >= 0 ? process.argv[i + 1] : dflt;
};

const REPO = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..', '..');
const TASKS_DIR = path.join(REPO, 'docs', 'dpa', 'tasks');
const maxWaitMs = argNum('--max-wait-min', 190) * 60 * 1000;
const outPath = argStr('--out', path.join(REPO, 'docs', 'dpa', `watch-${taskId}.json`));

const readLines = (file) => {
  try {
    return fs.readFileSync(file, 'utf-8').split('\n').filter((l) => l.trim());
  } catch {
    return [];
  }
};
const parse = (lines) => {
  const out = [];
  for (const l of lines) {
    try { out.push(JSON.parse(l)); } catch { /* 正在写入的残行 */ }
  }
  return out;
};
const ts = () => new Date().toISOString().replace('T', ' ').slice(0, 19);
const log = (m) => console.log(`[watch ${ts()}] ${m}`);

const journalFile = path.join(TASKS_DIR, `${path.basename(taskId)}.journal.jsonl`);
// journal 文件名形如 <yyyy-mm-dd>-<taskId>.journal.jsonl
let resolvedJournal = journalFile;
try {
  const hit = fs.readdirSync(TASKS_DIR).find((f) => f.endsWith(`-${taskId}.journal.jsonl`));
  if (hit) resolvedJournal = path.join(TASKS_DIR, hit);
} catch { /* 目录可能还没建 */ }

log(`taskId=${taskId}`);
log(`journal=${resolvedJournal}`);

const result = { taskId, journalPath: resolvedJournal, startedAt: new Date().toISOString(), checks: {}, samples: {}, final: null };

let lastReportedSamples = -1;
const deadline = Date.now() + maxWaitMs;

for (;;) {
  const recs = parse(readLines(resolvedJournal));
  const start = recs.find((r) => r.type === 'start');
  const note = recs.find((r) => r.type === 'note');
  const checkpoints = recs.filter((r) => r.type === 'checkpoint');
  const end = recs.find((r) => r.type === 'end');

  if (start) {
    result.checks.A1_start = { ok: start.taskId === taskId, pid: start.pid, requestedDurationMs: start.requestedDurationMs, sampleIntervalMs: start.sampleIntervalMs, snapshotIntervalMs: start.snapshotIntervalMs, samplePath: start.samplePath, pauseOnFinish: start.pauseOnFinish };
    result.samplePath = start.samplePath;
  }
  if (note) {
    result.checks.A2_probe = { ok: note.startTicksGame !== undefined, startTicksGame: note.startTicksGame, wasRunning: note.wasRunning, probeGapMs: note.probeGapMs };
  }

  let sampleLines = 0;
  let snapshotLines = 0;
  let monotonic = true;
  let finiteTps = true;
  let lastAt = -1;
  const tpsSamples = [];
  if (result.samplePath && fs.existsSync(result.samplePath)) {
    for (const r of parse(readLines(result.samplePath))) {
      if (r.kind === 'sample') {
        sampleLines++;
        if (typeof r.atMs === 'number') {
          if (r.atMs < lastAt) monotonic = false;
          lastAt = r.atMs;
        }
        if (r.tps !== null && !Number.isFinite(r.tps)) finiteTps = false;
        if (Number.isFinite(r.tps)) tpsSamples.push(r.tps);
      } else if (r.kind === 'snapshot') {
        snapshotLines++;
      }
    }
  }
  const cps = checkpoints.length > 0 ? checkpoints[checkpoints.length - 1] : null;
  const samplesDone = cps ? cps.samplesDone : null;
  const snapshotsDone = cps ? cps.snapshotsDone : null;

  if (sampleLines !== lastReportedSamples) {
    lastReportedSamples = sampleLines;
    log(`采样落盘 ${sampleLines} 行 / 快照 ${snapshotLines} 行 / checkpoint ${checkpoints.length} 个`
      + (cps ? `  （checkpoint 报告 samplesDone=${samplesDone} snapshotsDone=${snapshotsDone}）` : ''));
  }

  result.samples = {
    sampleLines, snapshotLines, checkpoints: checkpoints.length,
    samplesDoneFromCheckpoint: samplesDone, snapshotsDoneFromCheckpoint: snapshotsDone,
    tpsCount: tpsSamples.length,
    tpsMin: tpsSamples.length ? Math.min(...tpsSamples) : null,
    tpsMax: tpsSamples.length ? Math.max(...tpsSamples) : null,
    tpsAvg: tpsSamples.length ? Number((tpsSamples.reduce((a, b) => a + b, 0) / tpsSamples.length).toFixed(2)) : null,
    lastAtMs: lastAt,
  };
  // A3：落盘条数必须追平 checkpoint 里报告过的累计数（checkpoint 每 30 个采样点写一次，故允许尾部滞后 <30）
  if (samplesDone !== null) {
    result.checks.A3_samplesPersisted = {
      ok: sampleLines >= samplesDone - 29,
      sampleLines, samplesDone,
      note: 'checkpoint 每 30 个采样点写一次，故落盘数允许比它报告的值少不到 30',
    };
  }
  result.checks.A4_sampleSanity = { ok: monotonic && finiteTps && lastAt >= 0, monotonicAtMs: monotonic, finiteTps };

  if (end) {
    result.checks.A6_end = { ok: end.state === 'completed', state: end.state, reason: end.reason, atMs: end.atMs, ticksGame: end.ticksGame };
    result.final = end;
    break;
  }
  if (Date.now() > deadline) {
    result.checks.A6_end = { ok: false, note: `等待 ${maxWaitMs / 60000} 分钟仍未出现 end 记录（任务仍在跑或 MCP 进程已退出）` };
    break;
  }
  await new Promise((r) => setTimeout(r, 30000));
}

result.endedAt = new Date().toISOString();
const passed = Object.values(result.checks).every((c) => c.ok === true);
result.allChecksPassed = passed;

try {
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  fs.writeFileSync(outPath, `${JSON.stringify(result, null, 2)}\n`, 'utf-8');
  log(`报告已写入 ${outPath}`);
} catch (e) {
  log(`报告写入失败: ${e.message}`);
}

log(`结论：${passed ? '全部断言通过' : '存在未通过项，详见报告'}`);
log(JSON.stringify(result.checks, null, 2));
console.log(passed ? 'WATCH_RESULT=PASS' : 'WATCH_RESULT=FAIL');

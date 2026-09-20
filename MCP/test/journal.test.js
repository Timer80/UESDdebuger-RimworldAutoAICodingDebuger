import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { makeRunId, appendRecord, readRecords, readOrphans, filterOrphans } from '../longTask/journal.js';

// 2026-09-20：测试不再往 %TEMP% 里堆垃圾。
// 此前每次跑测试都留下 uesd-journal-* 目录，实测累计 294 个（见发布前检查报告 §11.5）。
const tmpDirs = new Set();

function tmpDir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'uesd-journal-'));
  tmpDirs.add(dir);
  return dir;
}

after(() => {
  for (const dir of tmpDirs) {
    try { fs.rmSync(dir, { recursive: true, force: true }); } catch { /* 清理失败不影响测试结论 */ }
  }
});

const START = {
  type: 'start', runId: '2026-09-17-t-aaaa', taskId: 't-aaaa', pid: 4242,
  startedAtUtc: '2026-09-17T10:00:00.000Z', requestedDurationMs: 9000000,
  speed: 'Superfast', sampleIntervalMs: 2000, snapshotIntervalMs: 30000,
  samplePath: 'docs/dpa/samples/x.jsonl', pauseOnFinish: 'restore',
  pausedBefore: true, timeSpeedBefore: 'Normal',
};

test('makeRunId 形如 <日期>-<taskId>', () => {
  assert.equal(makeRunId('t-8f31', new Date('2026-09-17T03:04:05Z')), '2026-09-17-t-8f31');
});

test('appendRecord 追加一行合法 JSON，parent 目录自动创建', () => {
  const dir = tmpDir();
  const file = path.join(dir, 'nested', 'tasks', 'a.journal.jsonl');
  appendRecord(file, START);
  appendRecord(file, { type: 'checkpoint', atMs: 30000, ticksGame: 100, tps: 59, samplesDone: 15, snapshotsDone: 1 });
  const raw = fs.readFileSync(file, 'utf-8');
  const lines = raw.trimEnd().split('\n');
  assert.equal(lines.length, 2);
  assert.equal(JSON.parse(lines[0]).type, 'start');
  assert.equal(JSON.parse(lines[1]).samplesDone, 15);
});

test('appendRecord 对同一文件连续追加不互相覆盖', () => {
  const dir = tmpDir();
  const file = path.join(dir, 'a.journal.jsonl');
  for (let i = 0; i < 5; i++) appendRecord(file, { type: 'checkpoint', atMs: i * 1000 });
  assert.equal(readRecords(file).length, 5);
});

test('readRecords 跳过损坏行而不抛错', () => {
  const dir = tmpDir();
  const file = path.join(dir, 'a.journal.jsonl');
  fs.writeFileSync(file, '{"type":"start"}\nNOT JSON\n{"type":"end","state":"completed"}\n');
  const recs = readRecords(file);
  assert.equal(recs.length, 2);
  assert.equal(recs[1].state, 'completed');
});

test('readRecords 对不存在的文件返回空数组', () => {
  assert.deepEqual(readRecords(path.join(tmpDir(), 'nope.jsonl')), []);
});

test('readOrphans 只挑出没有 end 记录的任务', () => {
  const dir = tmpDir();
  const a = path.join(dir, 'a.journal.jsonl');
  const b = path.join(dir, 'b.journal.jsonl');
  appendRecord(a, START);
  appendRecord(a, { type: 'end', state: 'completed', atMs: 9000000 });
  appendRecord(b, { ...START, taskId: 't-bbbb', runId: '2026-09-17-t-bbbb' });
  appendRecord(b, { type: 'checkpoint', atMs: 30000, ticksGame: 100, tps: 59, samplesDone: 15, snapshotsDone: 1 });
  const orphans = readOrphans(dir);
  assert.equal(orphans.length, 1);
  assert.equal(orphans[0].taskId, 't-bbbb');
  assert.equal(orphans[0].lastCheckpoint.atMs, 30000);
  assert.equal(orphans[0].lastCheckpoint.ticksGame, 100);
  assert.equal(orphans[0].journalPath, b);
});

test('readOrphans 对空目录/不存在目录返回空数组', () => {
  assert.deepEqual(readOrphans(tmpDir()), []);
  assert.deepEqual(readOrphans(path.join(tmpDir(), 'missing')), []);
});

test('filterOrphans：pid 仍存活则不动它', () => {
  const orphans = [{ taskId: 't-a', pid: 111 }, { taskId: 't-b', pid: 222 }];
  const kept = filterOrphans(orphans, { isAlive: (pid) => pid === 111 });
  assert.deepEqual(kept.map((o) => o.taskId), ['t-b']);
});

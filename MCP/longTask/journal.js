/**
 * 任务日志（规格 §4）：一行一个 JSON 的追加式记录，用于 MCP 进程重启后查账。
 *
 * 为什么追加而非覆盖：追加才留得下「节奏何时改变、tick 何时停在原地」的痕迹，
 * 而这正是排查一次长采样为何中断时唯一要看的东西。60 分钟任务约 120 行 checkpoint，
 * 2.5 小时约 300 行，量级无害。
 */
import fs from 'node:fs';
import path from 'node:path';

const JOURNAL_EXT = '.journal.jsonl';

/** runId 形如 2026-09-17-t-8f31（按天归档便于人翻） */
export function makeRunId(taskId, now = new Date()) {
  const d = now instanceof Date ? now : new Date(now);
  const yyyy = d.getUTCFullYear();
  const mm = String(d.getUTCMonth() + 1).padStart(2, '0');
  const dd = String(d.getUTCDate()).padStart(2, '0');
  return `${yyyy}-${mm}-${dd}-${taskId}`;
}

export function journalFileName(runId) {
  return `${runId}${JOURNAL_EXT}`;
}

/** 追加一条记录；parent 目录按需创建。IO 失败返回 false，绝不抛出打断任务。 */
export function appendRecord(file, record) {
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.appendFileSync(file, `${JSON.stringify(record)}\n`, 'utf-8');
    return true;
  } catch (e) {
    return false;
  }
}

/** 读全部记录；损坏行跳过（日志本身不能成为新的故障源） */
export function readRecords(file) {
  let raw;
  try {
    raw = fs.readFileSync(file, 'utf-8');
  } catch (e) {
    return [];
  }
  const out = [];
  for (const line of raw.split('\n')) {
    const s = line.trim();
    if (!s) continue;
    try {
      const rec = JSON.parse(s);
      if (rec && typeof rec === 'object') out.push(rec);
    } catch (e) {
      /* 损坏行跳过 */
    }
  }
  return out;
}

/** 扫描目录，返回没有 end 记录的任务（孤儿候选） */
export function readOrphans(dir) {
  let names;
  try {
    names = fs.readdirSync(dir);
  } catch (e) {
    return [];
  }
  const orphans = [];
  for (const name of names) {
    if (!name.endsWith(JOURNAL_EXT)) continue;
    const file = path.join(dir, name);
    const recs = readRecords(file);
    if (recs.length === 0) continue;
    if (recs.some((r) => r.type === 'end')) continue;
    const start = recs.find((r) => r.type === 'start');
    if (!start) continue;
    const checkpoints = recs.filter((r) => r.type === 'checkpoint');
    // 起始 tick 存在 note 记录里（start 拿不到——必须先设速度再读）。
    // 它必须在没有 checkpoint 时作为兜底：checkpoint 每 30 个采样点才写一次，
    // 短命任务（< 30 个采样点）一个 checkpoint 都没有；此时若只看 checkpoint，
    // 恢复逻辑会因 lastTicks=null 误判成「游戏没在动」（实测踩到，见 spec §4.2）。
    const note = recs.find((r) => r.type === 'note' && r.startTicksGame !== undefined);
    orphans.push({
      ...start,
      journalPath: file,
      startTicksGame: note ? note.startTicksGame : null,
      wasRunning: note ? note.wasRunning : null,
      lastCheckpoint: checkpoints.length > 0 ? checkpoints[checkpoints.length - 1] : null,
      records: recs.length,
    });
  }
  return orphans;
}

/** 过滤掉「pid 仍存活」的条目（= 另一个 MCP 实例在跑，不干预） */
export function filterOrphans(orphans, { isAlive }) {
  return (orphans || []).filter((o) => !(typeof isAlive === 'function' && isAlive(o.pid)));
}

export default { makeRunId, journalFileName, appendRecord, readRecords, readOrphans, filterOrphans };

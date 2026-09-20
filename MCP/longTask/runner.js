/**
 * 长任务循环（规格 §3）。形态：直接解暂停 + 轮询即时 TPS + 定期 DPA 快照落盘。
 *
 * 为什么不用块化 play_for：play_for 的语义是「解暂停 → 等 N 毫秒 → 再暂停」，
 * 块化会让 2 小时采样在块边界暂停 240 次（白送的扰动），且需要两条代码路径。
 * 这里游戏全程不暂停，一次连续跑完，单次 MCP 调用（查询/取消/改节奏）都 < 1s。
 *
 * 心跳必须走**游戏进程内的单次 RPC**：`rimworld/get_game_info`（见下方 HEARTBEAT_TOOL）。
 * 它只返回 ticksGame，实测单次约 8ms，且不 spawn 任何子进程。
 * **不要**改用 MCP 原生的 `get_game_status`：它每次执行 detectGameStage()，其中
 * findRimWorldProcess() 会起一个 tasklist 子进程，1s 一次 = 每小时 3600 次，会污染被测对象。
 * （2026-09-18 修正前这里用的是不存在的 `rimworld/get_game_status`，见 HEARTBEAT_TOOL 注释。）
 *
 * 依赖全部注入（deps），因此本模块可在无游戏、无网络的环境下完整单测。
 *
 * deps 契约：
 *   call(name, args, opts) -> Promise<{ok, result?, error?}>   游戏侧 RPC
 *   now() -> number                                            单调毫秒
 *   sleep(ms) -> Promise                                       可注入的睡眠
 *   journalAppend(file, record) -> boolean                     追加一条任务日志
 *   appendSample(file, sample) -> any                          追加一条 tick 采样点（可选但强烈建议：
 *                                                              不注入则长窗口数据只留内存滚动窗口，会丢）
 *   appendSnapshot(file, entry, payload) -> any                追加一条 DPA 快照（可选）
 *   journalDir / sampleDir -> string                           目录
 */
import path from 'node:path';
import { makeRunId, journalFileName } from './journal.js';

const DEFAULT_KEEP = { samples: 200, snapshots: 20 };
const DEFAULT_BURST = { sampleMs: 1000, snapshotMs: 10000 };
const FINAL_STATES = new Set(['completed', 'cancelled', 'failed', 'aborted', 'orphaned']);
const CHECKPOINT_EVERY_SAMPLES = 30;
const RPC_TIMEOUT_MS = 30000;
// 外部暂停/卡死判定：tick **持续**多久不前进才算游戏真没在跑。
//
// 为什么按「时长」而不是「轮数」：起初用的是「连续 3 轮」（2s 间隔 = 6 秒），
// 实测在 2 小时任务里被误杀——RimWorld 会自动存档、跑长事件、生成地图，这些都会让
// tick 短暂停几秒然后继续（实测一次停顿后 20 秒内又推进了 ~2856 ticks，游戏从未卡死）。
// 按轮数判会在这些正常毛刺上把任务打成 aborted，丢掉后面 84 分钟的数据。
// 按时长判的语义也更贴近直觉：「tick 一分多钟没动 = 游戏真卡住了」。
const DEFAULT_STALL_ABORT_MS = 60000;
// 启动时判定「游戏原本是否在跑」的观察窗口：取两次 tick，看是否前进。
// 上限 500ms，且不超过采样间隔的 1/4（采样很密时别让这段观察占比过大）。
const PROBE_RUNNING_MIN_MS = 200;
const PROBE_RUNNING_MAX_MS = 500;

function pushBounded(arr, item, max) {
  arr.push(item);
  while (arr.length > max) arr.shift();
}

export function createRunner(deps) {
  /** taskId -> task */
  const tasks = new Map();
  /** taskId -> { cancelRequested } */
  const control = new Map();

  function runningTask() {
    for (const t of tasks.values()) {
      if (t.state === 'running') return t;
    }
    return null;
  }

  /**
   * 快照间隔强制联动：采样间隔压到猝发阈值以下时，快照间隔必须同步压下来。
   * 否则会超出 DPA 的环形缓冲（Profiler.RECORDS_HELD = 2000，60fps 下约 33 秒覆盖一圈）而丢数据。
   */
  function effectiveSnapshotInterval(sampleIntervalMs, snapshotIntervalMs, burst) {
    const b = burst || DEFAULT_BURST;
    if (sampleIntervalMs <= b.sampleMs) {
      return { value: Math.min(snapshotIntervalMs, b.snapshotMs), coerced: snapshotIntervalMs > b.snapshotMs };
    }
    return { value: snapshotIntervalMs, coerced: false };
  }

  function journalPathFor(runId) {
    return path.join(deps.journalDir, journalFileName(runId));
  }

  function jAppend(task, record) {
    if (typeof deps.journalAppend === 'function') {
      deps.journalAppend(task.journalPath, record);
    }
  }

  /** 结束时的暂停处置（规格 §3.1 pauseOnFinish 三态） */
  async function settlePause(task) {
    const mode = task.pauseOnFinish;
    if (mode === 'keep') return;
    // restore 的语义：只有明确读到「启动前在跑」（pausedBefore === false）才保持运行。
    // pausedBefore 为 null 表示原状态未知（启动早期就失败了，读状态没成功）——此时按「暂停」
    // 处理：因为我们可能已经把游戏解暂停了，暂停是唯一不会留下一个没人管的后台任务的选择。
    const wantPaused = mode === 'pause' ? true : task.pausedBefore !== false;
    try {
      await deps.call('rimworld/pause_game', { pause: wantPaused }, { timeoutMs: RPC_TIMEOUT_MS });
    } catch (e) {
      // 恢复失败不改变任务终态，但记账（否则游戏会静默留在运行态）
      jAppend(task, {
        type: 'note',
        atMs: deps.now() - task.startedAtMs,
        message: `恢复暂停状态失败: ${e && e.message ? e.message : String(e)}`,
      });
    }
  }

  /**
   * 判定「启动前游戏是否在跑」。
   *
   * 为什么需要：pauseOnFinish=restore 的语义是「恢复启动前的状态」，但心跳工具
   * rimworld/get_game_info 不返回 paused，拿不到暂停标志。改用最直接的证据：
   * 在**还没解暂停**的时刻取两次 tick，间隔一小段，看 tick 是否前进。
   *   tick 前进 → 原本在跑 → 结束时保持运行
   *   tick 未动 → 原本暂停（或未加载）→ 结束时暂停
   * 读失败返回 null，由 settlePause 按「暂停」兜底。
   */
  async function probeWasRunning(sampleIntervalMs) {
    const gap = Math.max(
      PROBE_RUNNING_MIN_MS,
      Math.min(PROBE_RUNNING_MAX_MS, Math.floor((Number(sampleIntervalMs) || 2000) / 4)),
    );
    try {
      const first = await readHeartbeat();
      if (!Number.isFinite(first.ticksGame)) return null;
      await deps.sleep(gap);
      const second = await readHeartbeat();
      if (!Number.isFinite(second.ticksGame)) return null;
      return { running: second.ticksGame > first.ticksGame, ticksGame: second.ticksGame, gapMs: gap };
    } catch (e) {
      return null;
    }
  }

  /** 心跳工具名。**必须**是 RimBridge 实际注册的工具（已用 rimbridge/list_capabilities 核实）。 */
  const HEARTBEAT_TOOL = 'rimworld/get_game_info';

  /**
   * 读一次心跳：只取 ticksGame。
   *
   * 2026-09-18 修正：原先用的是 'rimworld/get_game_status'——**该工具在 RimBridge 侧并不存在**
   * （125 个能力里只有 rimworld/get_game_info）。当时是从 index.js 里 UE /status 响应的
   * ticksGame/paused 字段推断出的名字，把「字段来源」误当成了「工具名来源」，导致长任务一启动就
   * 报 Tool not found 而失败。
   *
   * 另一个后果：get_game_info **不返回 paused**，所以外部暂停检测不能再靠 paused 字段，
   * 改为 tick 持续停滞时长判据（见 DEFAULT_STALL_ABORT_MS 与 runLoop）。
   */
  async function readHeartbeat() {
    const res = await deps.call(HEARTBEAT_TOOL, {}, { timeoutMs: RPC_TIMEOUT_MS });
    if (!res || res.ok !== true) {
      const msg = res && res.error
        ? (res.error.message || JSON.stringify(res.error))
        : `${HEARTBEAT_TOOL} 失败`;
      throw new Error(msg);
    }
    const r = res.result || {};
    return {
      ticksGame: Number.isFinite(r.ticksGame) ? r.ticksGame : null,
    };
  }

  /**
   * 关键 RPC：失败即中止任务。
   * 为什么必须检查：deps.call 失败时返回 {ok:false} 而不是抛错。若不检查，
   * 「解暂停失败」会让任务照样跑满时长并报 completed——采到的是游戏根本没动的一堆数据，
   * 而调用方以为成功。宁可明确失败，也不要产出看似成功的垃圾采样。
   *
   * 2026-09-18 加强：还要看**载荷**。RimBridge 对「工具存在但参数无效」的响应是
   * { ok:true, result:{ success:true, ... } } 这种「传输成功但操作可能没生效」的形态——
   * 例如给 rimworld/pause_game 传错参数名（它要 pause，不是 paused）时参数被忽略、
   * 走默认值 true，于是本意解暂停却执行了暂停，而返回值毫无异常。只查 ok 抓不到这种情况。
   */
  async function mustCall(name, args) {
    const res = await deps.call(name, args, { timeoutMs: RPC_TIMEOUT_MS });
    if (!res || res.ok !== true) {
      const msg = res && res.error ? (res.error.message || JSON.stringify(res.error)) : `${name} 失败`;
      throw new Error(`${name} 失败: ${msg}`);
    }
    const payload = res.result;
    if (payload && typeof payload === 'object' && payload.success === false) {
      const msg = payload.message || payload.error || JSON.stringify(payload).slice(0, 200);
      throw new Error(`${name} 未生效: ${msg}`);
    }
    return payload;
  }

  async function runLoop(task) {
    const keep = { ...DEFAULT_KEEP, ...(task.keep || {}) };
    try {
      // start 必须**最先**记：它的作用是「这个进程里曾经有过这个任务」这一事实的审计记录。
      // 若排在首次 RPC 之后，则任何早期失败（桥接未连、游戏未载入）都会让日志里只有 end，
      // 孤儿恢复与事后排查就失去了「任务确实启动过」这个锚点。
      jAppend(task, task.journalStart);

      // 设速度（游戏侧只解暂停、不动时间刻度的话，跑出来的 TPS 不可比）
      await mustCall('rimworld/set_time_speed', { speed: task.speed });

      // 判定启动前是否在跑（供 pauseOnFinish=restore）。必须在解暂停之前做。
      // 尽力而为：读不到就退化成 null，由 settlePause 按「暂停」处理。
      const probe = await probeWasRunning(task.sampleIntervalMs);
      task.pausedBefore = probe ? !probe.running : null;
      task.startTicksGame = probe ? probe.ticksGame : null;
      // 起始 tick 是 start 之后才拿到的（要先设速度），单独记一条以便事后对齐
      jAppend(task, {
        type: 'note', atMs: deps.now() - task.startedAtMs,
        startTicksGame: task.startTicksGame, wasRunning: probe ? probe.running : null,
        probeGapMs: probe ? probe.gapMs : null,
      });

      await mustCall('rimworld/pause_game', { pause: false });

      let lastSampleAt = 0;
      let lastSampleTicks = task.startTicksGame;
      let nextSnapshotAt = task.snapshotIntervalMs;
      task.ticksGame = task.startTicksGame;
      // 外部暂停/卡死检测：连续这么多轮 tick 完全没前进就判定游戏没在跑。
      // 为什么不用 paused 字段：心跳工具 get_game_info 不返回 paused（见 readHeartbeat 注释）。
      // 为什么需要这个检测：外部暂停会让任务继续按墙钟计时并采到一堆 tick 不变的数据，
      // 最后报 completed——比明确 aborted 更糟。
      let stallSinceMs = null; // tick 本轮未前进的起始时刻（null=正在前进）

      for (;;) {
        const ctl = control.get(task.taskId);
        if (ctl && ctl.cancelRequested) {
          task.state = 'cancelled';
          task.completionReason = 'cancelled-by-request';
          break;
        }
        if (task.elapsedMs >= task.requestedDurationMs) {
          task.state = 'completed';
          task.completionReason = 'duration-elapsed';
          break;
        }

        // 先睡再采样：保证「取消最迟一个采样周期生效」
        await deps.sleep(task.sampleIntervalMs);
        const elapsed = deps.now() - task.startedAtMs;
        if (elapsed > task.requestedDurationMs) {
          task.elapsedMs = task.requestedDurationMs;
          task.state = 'completed';
          task.completionReason = 'duration-elapsed';
          break;
        }
        task.elapsedMs = elapsed;

        const st = await readHeartbeat();
        task.ticksGame = st.ticksGame;

        const dt = elapsed - lastSampleAt;
        const dticks = Number.isFinite(st.ticksGame) && Number.isFinite(lastSampleTicks)
          ? st.ticksGame - lastSampleTicks
          : null;
        if (dticks !== null && dticks <= 0) {
          // tick 本轮没前进：记下这一轮停顿的起点，等持续够 stallAbortMs 再判死。
          // 不能用「轮数」判——正常毛刺（自动存档/长事件）会让 tick 停几秒然后继续。
          if (stallSinceMs === null) stallSinceMs = elapsed;
          const stalledForMs = elapsed - stallSinceMs;
          if (stalledForMs >= task.stallAbortMs) {
            task.state = 'aborted';
            task.completionReason = 'tick-stalled';
            task.error = `tick 已持续 ${Math.round(stalledForMs / 1000)}s 未前进（ticksGame=${st.ticksGame}），`
              + '游戏可能被外部暂停或卡住；请用 rimworld/get_game_info 与 rimworld/get_ui_state 确认现场。'
              + '若只是长时间自动存档/事件，可调大 config.json 的 rimBridge.longTask.stallAbortMs。';
            break;
          }
        } else {
          stallSinceMs = null;
        }

        const tps = dt > 0 && dticks != null ? (dticks * 1000) / dt : null;
        lastSampleAt = elapsed;
        lastSampleTicks = st.ticksGame;
        task.samplesDone++;
        const sample = { atMs: elapsed, ticksGame: st.ticksGame, tps };
        pushBounded(task.samples, sample, keep.samples);
        task.lastSample = sample;
        // 采样点必须**全量落盘**。keep.samples 只约束内存里供 task_status 直接看的滚动窗口；
        // 若只留内存，1s 采样跑 1 小时会丢掉 3400/3600 个原始点——这与 DPA 环形缓冲
        // （2000 格 ≈ 33s 覆盖一圈）是同一类问题：长窗口数据的价值全在「留得住」。
        // 采样点与快照写同一个文件（kind 字段区分），便于按时间轴对齐两者。
        if (typeof deps.appendSample === 'function') {
          deps.appendSample(task.samplePath, sample);
        }

        if (elapsed >= nextSnapshotAt) {
          const snap = await deps.call('rimworld/dpa_snapshot', { ...(task.snapshotArgs || {}) }, { timeoutMs: RPC_TIMEOUT_MS * 2 });
          task.snapshotsDone++;
          const rowCount = snap && snap.ok && snap.result && Array.isArray(snap.result.rows)
            ? snap.result.rows.length
            : null;
          const entry = { atMs: elapsed, ticks: st.ticksGame, rows: rowCount, file: task.samplePath };
          pushBounded(task.snapshots, entry, keep.snapshots);
          task.lastSnapshot = entry;
          if (typeof deps.appendSnapshot === 'function') {
            deps.appendSnapshot(task.samplePath, entry, snap && snap.result ? snap.result : null);
          }
          while (nextSnapshotAt <= elapsed) nextSnapshotAt += task.snapshotIntervalMs;
        }

        if (task.samplesDone % CHECKPOINT_EVERY_SAMPLES === 0) {
          jAppend(task, {
            type: 'checkpoint', atMs: elapsed, ticksGame: task.ticksGame, tps,
            samplesDone: task.samplesDone, snapshotsDone: task.snapshotsDone,
          });
        }
      }
    } catch (e) {
      task.state = 'failed';
      task.error = e && e.message ? e.message : String(e);
      task.completionReason = 'error';
    } finally {
      try {
        await settlePause(task);
      } catch (e) {
        /* settlePause 内部已兜底 */
      }
      task.finishedAtUtc = new Date().toISOString();
      task.remainingMs = Math.max(0, task.requestedDurationMs - task.elapsedMs);
      // error 必须落进 end 记录：此前只写了 reason="error"，事后完全看不出失败原因
      // （真实困扰：一条只含 {"state":"failed","reason":"error"} 的记录，无从排查）。
      jAppend(task, {
        type: 'end', state: task.state, atMs: task.elapsedMs,
        ticksGame: task.ticksGame, reason: task.completionReason || null,
        ...(task.error ? { error: task.error } : {}),
        samplesDone: task.samplesDone, snapshotsDone: task.snapshotsDone,
      });
    }
  }

  function toSnapshot(task) {
    return {
      taskId: task.taskId, runId: task.runId, state: task.state,
      requestedDurationMs: task.requestedDurationMs,
      elapsedMs: task.elapsedMs, remainingMs: task.remainingMs,
      samplesDone: task.samplesDone, snapshotsDone: task.snapshotsDone,
      sampleIntervalMs: task.sampleIntervalMs,
      snapshotIntervalMs: task.snapshotIntervalMs,
      snapshotIntervalMsCoerced: task.snapshotIntervalMsCoerced,
      pauseOnFinish: task.pauseOnFinish,
      startedAtUtc: task.startedAtUtc, finishedAtUtc: task.finishedAtUtc || null,
      pausedBefore: task.pausedBefore, ticksGame: task.ticksGame,
      lastSample: task.lastSample, lastSnapshot: task.lastSnapshot,
      samples: task.samples, snapshots: task.snapshots,
      samplePath: task.samplePath, journalPath: task.journalPath,
      completionReason: task.completionReason, error: task.error,
    };
  }

  const runner = {
    /** 启动一个后台长任务。成功返回任务快照，失败返回 {success:false,errorCode,...}。 */
    async start(opts) {
      const busy = runningTask();
      if (busy) {
        return {
          success: false,
          errorCode: 'TASK_ALREADY_RUNNING',
          runningTaskId: busy.taskId,
          message: `已有长任务在跑（taskId=${busy.taskId}）。同一时刻只允许一个长任务——两个任务会互相污染暂停/速度状态。`,
        };
      }
      const taskId = opts.taskId || `t-${Math.random().toString(16).slice(2, 8)}`;
      const runId = opts.runId || makeRunId(taskId);
      const burst = opts.burst || DEFAULT_BURST;
      const eff = effectiveSnapshotInterval(opts.sampleIntervalMs, opts.snapshotIntervalMs, burst);
      // 采样文件同时承载「timer 采样点」与「DPA 快照」（以 kind 字段区分），
      // 因此叫 samplePath 而不是 snapshotPath —— 后者会让人以为只有快照，从而漏掉采样点也要落盘。
      // snapshotPath 作为旧名保留兼容。
      const samplePath = opts.samplePath || opts.snapshotPath || path.join(deps.sampleDir, `${runId}.jsonl`);
      const journalStart = {
        type: 'start', runId, taskId, pid: process.pid,
        startedAtUtc: new Date().toISOString(),
        requestedDurationMs: opts.durationMs,
        speed: opts.speed,
        sampleIntervalMs: opts.sampleIntervalMs,
        snapshotIntervalMs: eff.value,
        samplePath,
        pauseOnFinish: opts.pauseOnFinish,
      };
      const task = {
        taskId, runId, state: 'running',
        requestedDurationMs: opts.durationMs,
        elapsedMs: 0,
        remainingMs: opts.durationMs,
        speed: opts.speed,
        sampleIntervalMs: opts.sampleIntervalMs,
        snapshotIntervalMs: eff.value,
        snapshotIntervalMsCoerced: eff.coerced,
        samplePath,
        pauseOnFinish: opts.pauseOnFinish,
        snapshotArgs: opts.snapshotArgs || null,
        keep: opts.keep || null,
        stallAbortMs: Number.isFinite(opts.stallAbortMs) && opts.stallAbortMs > 0
          ? opts.stallAbortMs
          : DEFAULT_STALL_ABORT_MS,
        // 记下调用方的猝发阈值：configure 必须用同一套阈值重新判定联动，
        // 否则默认 1000/10000 会把「采样 2000 + 快照 30000」误判成需要压缩到 10000。
        burst,
        journalPath: journalPathFor(runId),
        journalStart,
        startedAtMs: deps.now(),
        startedAtUtc: journalStart.startedAtUtc,
        pausedBefore: null,
        ticksGame: null,
        samplesDone: 0,
        snapshotsDone: 0,
        samples: [],
        snapshots: [],
        lastSample: null,
        lastSnapshot: null,
        completionReason: null,
        error: null,
      };
      tasks.set(taskId, task);
      control.set(taskId, { cancelRequested: false });
      task.done = runLoop(task); // 不 await：后台跑
      // started:true 是规格 §3.1 契约字段（与 success 同义但对调用方更直白）。
      // _done：任务结束句柄。生产调用方靠 task_status 轮询，不需要它；
      // 但测试必须能等到终态，调用方在需要时也能 await 一个长任务的最终结果（转发层会剥掉它）。
      // samplePath 由 toSnapshot 提供；snapshotPath 是旧名别名，保留以免破坏既有调用方。
      return {
        success: true,
        started: true,
        _done: task.done,
        snapshotPath: task.samplePath,
        ...toSnapshot(task),
      };
    },

    /** 请求取消；最迟一个采样周期内生效。 */
    stop(taskId) {
      const task = tasks.get(taskId);
      if (!task) return { success: false, errorCode: 'TASK_NOT_FOUND', message: `未找到任务 ${taskId}` };
      if (FINAL_STATES.has(task.state)) {
        return {
          success: false, errorCode: 'TASK_NOT_RUNNING', state: task.state,
          message: `任务 ${taskId} 已处于 ${task.state}`,
        };
      }
      const ctl = control.get(taskId);
      if (ctl) ctl.cancelRequested = true;
      return { success: true, taskId, state: task.state, message: '将在下个采样周期停止并处理暂停状态' };
    },

    /** 中途改节奏（进入/退出猝发测量）。不重启任务、不碰游戏。 */
    configure(taskId, { sampleIntervalMs, snapshotIntervalMs } = {}) {
      const task = tasks.get(taskId);
      if (!task) return { success: false, errorCode: 'TASK_NOT_FOUND', message: `未找到任务 ${taskId}` };
      if (FINAL_STATES.has(task.state)) {
        return {
          success: false, errorCode: 'TASK_NOT_RUNNING', state: task.state,
          message: `任务 ${taskId} 已处于 ${task.state}，无法改节奏`,
        };
      }
      if (Number.isFinite(sampleIntervalMs) && sampleIntervalMs > 0) task.sampleIntervalMs = sampleIntervalMs;
      if (Number.isFinite(snapshotIntervalMs) && snapshotIntervalMs > 0) task.requestedSnapshotIntervalMs = snapshotIntervalMs;
      const eff = effectiveSnapshotInterval(
        task.sampleIntervalMs,
        task.requestedSnapshotIntervalMs != null ? task.requestedSnapshotIntervalMs : task.snapshotIntervalMs,
        task.burst,
      );
      task.snapshotIntervalMs = eff.value;
      task.snapshotIntervalMsCoerced = eff.coerced;
      jAppend(task, {
        type: 'configure', atMs: deps.now() - task.startedAtMs,
        sampleIntervalMs: task.sampleIntervalMs,
        snapshotIntervalMs: task.snapshotIntervalMs,
        coerced: eff.coerced,
      });
      // 注意顺序：先改任务状态再取快照，否则调用方拿到的是改之前的节奏
      return { success: true, ...toSnapshot(task) };
    },

    get(taskId) {
      const task = tasks.get(taskId);
      return task ? toSnapshot(task) : null;
    },

    list() {
      const all = [...tasks.values()].sort((a, b) => {
        if (a.state === 'running' && b.state !== 'running') return -1;
        if (b.state === 'running' && a.state !== 'running') return 1;
        return (b.startedAtMs || 0) - (a.startedAtMs || 0);
      });
      return all.map(toSnapshot);
    },

    snapshot: toSnapshot,

    /**
     * 进程重启后的孤儿任务处置（规格 §4.2）。幂等。
     * 默认 restorePause=false —— 只记账、不自动动游戏：自动暂停会让「下次启动」产生
     * 意外副作用，交给用户经 task_status 见过账后再决定。
     *
     * gabpReady：调用方（index.js）是否已确保 GABP 连接。
     * 为什么需要它：本方法在启动路径上被调用，此时 GABP 可能还是 idle（惰性连接）。
     * 探测失败时**不写终态记录** —— 否则孤儿状态被一次性消费掉，下次启动无从重试，
     * 真机实测就是这样得到一堆 action="probe-failed" 的终态、且拿不到任何游戏状态。
     */
    async recover(orphans, { restorePause = false, maxAgeMs = 600000, gabpReady = true } = {}) {
      const results = [];
      for (const o of orphans || []) {
        const ageMs = deps.now() - Date.parse(o.startedAtUtc || 0);
        const entry = { taskId: o.taskId, runId: o.runId, journalPath: o.journalPath, action: null, recorded: false };

        if (gabpReady !== true) {
          // GABP 没连上：不探测、不写记录，留待下次启动重试
          entry.action = 'deferred-gabp-not-ready';
          results.push(entry);
          continue;
        }

        try {
          const st = await readHeartbeat();
          // 判定基准：最后一次 checkpoint 的 tick；没有 checkpoint（短命任务）则退回 note 里的起始 tick。
          // 只看 checkpoint 会在「任务跑得比 checkpoint 周期还短」时把 lastTicks 当成 null，
          // 从而误判成「游戏没在动」（实测踩到）。
          const lastTicks = o.lastCheckpoint
            ? o.lastCheckpoint.ticksGame
            : (Number.isFinite(o.startTicksGame) ? o.startTicksGame : null);
          const gameAdvanced = Number.isFinite(st.ticksGame)
            && Number.isFinite(lastTicks)
            && st.ticksGame > lastTicks;
          if (gameAdvanced && restorePause && Number.isFinite(ageMs) && ageMs <= maxAgeMs) {
            await deps.call('rimworld/pause_game', { pause: true }, { timeoutMs: RPC_TIMEOUT_MS });
            entry.action = 'paused-game';
          } else if (gameAdvanced) {
            entry.action = 'recorded-game-still-running';
          } else {
            entry.action = 'recorded-game-idle';
          }
        } catch (e) {
          // 探测出错同样不写终态：这多半是暂时性的（GABP 刚断、游戏正在载入），
          // 写成终态就再也重试不了，只会留下一条查不出原因的失败记录。
          entry.action = 'deferred-probe-failed';
          entry.error = e && e.message ? e.message : String(e);
          results.push(entry);
          continue;
        }

        if (typeof deps.journalAppend === 'function') {
          deps.journalAppend(o.journalPath, {
            type: 'end', state: 'orphaned', reason: 'MCP 进程重启', atMs: null, action: entry.action,
          });
          entry.recorded = true;
        }
        results.push(entry);
      }
      return results;
    },
  };

  return runner;
}

export default createRunner;





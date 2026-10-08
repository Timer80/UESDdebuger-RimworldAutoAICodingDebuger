/**
 * 超时推导纯策略（规格 §2）。无 I/O、无外部导入，便于单测。
 *
 * 四档优先级（由高到低）：
 *   T4 显式覆盖  config.rimBridge.longTask.toolTimeouts[<短名>]
 *   T3 入参推导  「时长就是入参」的工具：play_for{durationMs} + 余量，step_game_ticks/play_until_letter{timeoutMs} + 余量
 *   T2 慢档      load_game / save_game / execute_debug_action / create_hook / C# 编译执行 → default*2
 *   T1 默认      其余全部 → default
 *
 * 为什么要有余量：入参是游戏侧「跑多久」，超时必须比它长，否则游戏还在跑、MCP 先报超时，
 * 而超时 ≠ 取消（游戏侧操作会继续），调用方会陷入「结果丢失 + 副作用已发生」。
 */

// 与 gabpClient.js 的 DEFAULT_REQUEST_TIMEOUT_MS 保持同值；此处独立定义避免循环导入
const FALLBACK_DEFAULT_MS = 30000;

// T2 慢档白名单的「短名」形态（传入名已去掉 rimworld/ 前缀时用）
const SLOW_BARE_NAMES = new Set(['load_game', 'save_game', 'execute_debug_action', 'create_hook']);

// T3 推导表：工具短名 → { 取值字段, 余量字段 或 余量常量 }
const INPUT_DERIVED = new Map([
  ['play_for', { argKey: 'durationMs', overheadKey: 'playForOverheadMs' }],
  ['step_game_ticks', { argKey: 'timeoutMs', overheadMs: 5000 }],
  ['play_until_letter', { argKey: 'timeoutMs', overheadMs: 5000 }],
]);

// 这些工具自身的默认值本来就长于 30 s，不能当成 T1 处理
const LONG_DEFAULTS_MS = new Map([['step_game_ticks', 120000], ['play_until_letter', 300000]]);

const DEFAULTS = {
  defaultMs: FALLBACK_DEFAULT_MS,
  slowMs: FALLBACK_DEFAULT_MS * 2,
  maxSingleCallTimeoutMs: 1800000,
  playForOverheadMs: 10000,
  taskSampleIntervalMs: 2000,
  taskSnapshotIntervalMs: 30000,
  taskBurstSampleIntervalMs: 1000,
  taskBurstSnapshotIntervalMs: 10000,
  taskSampleKeep: 200,
  taskSnapshotKeep: 20,
  // tick 持续多久不前进才判定游戏没在跑（见 runner.js 的 DEFAULT_STALL_ABORT_MS 说明）
  stallAbortMs: 60000,
  recoverRestorePause: false,
  recoverMaxAgeMs: 600000,
  toolTimeouts: {},
};

/** 从工具全名取最后一段：rimworld/play_for → play_for；rimworld.play_for → play_for；create_hook → create_hook */
export function bareToolName(name) {
  const s = String(name == null ? '' : name);
  const i = s.lastIndexOf('/');
  const j = s.lastIndexOf('.');
  const cut = Math.max(i, j);
  return cut === -1 ? s : s.slice(cut + 1);
}

function positiveNumber(v, fallback) {
  return Number.isFinite(v) && v > 0 ? v : fallback;
}

/** 从 config 读全部阈值。对 config.rimBridge 被写成字符串等损坏形态容错（规格 §5.2）。 */
export function readThresholds(config) {
  const rb = config && typeof config === 'object' && config.rimBridge && typeof config.rimBridge === 'object'
    ? config.rimBridge
    : {};
  const lt = rb.longTask && typeof rb.longTask === 'object' ? rb.longTask : {};
  const defaultMs = positiveNumber(Number(rb.requestTimeoutMs), DEFAULTS.defaultMs);
  return {
    defaultMs,
    slowMs: defaultMs * 2,
    maxSingleCallTimeoutMs: positiveNumber(Number(lt.maxSingleCallTimeoutMs), DEFAULTS.maxSingleCallTimeoutMs),
    playForOverheadMs: positiveNumber(Number(lt.playForOverheadMs), DEFAULTS.playForOverheadMs),
    taskSampleIntervalMs: positiveNumber(Number(lt.taskSampleIntervalMs), DEFAULTS.taskSampleIntervalMs),
    taskSnapshotIntervalMs: positiveNumber(Number(lt.taskSnapshotIntervalMs), DEFAULTS.taskSnapshotIntervalMs),
    taskBurstSampleIntervalMs: positiveNumber(Number(lt.taskBurstSampleIntervalMs), DEFAULTS.taskBurstSampleIntervalMs),
    taskBurstSnapshotIntervalMs: positiveNumber(Number(lt.taskBurstSnapshotIntervalMs), DEFAULTS.taskBurstSnapshotIntervalMs),
    taskSampleKeep: positiveNumber(Number(lt.taskSampleKeep), DEFAULTS.taskSampleKeep),
    taskSnapshotKeep: positiveNumber(Number(lt.taskSnapshotKeep), DEFAULTS.taskSnapshotKeep),
    stallAbortMs: positiveNumber(Number(lt.stallAbortMs), DEFAULTS.stallAbortMs),
    recoverRestorePause: lt.recoverRestorePause === true,
    recoverMaxAgeMs: positiveNumber(Number(lt.recoverMaxAgeMs), DEFAULTS.recoverMaxAgeMs),
    toolTimeouts: lt.toolTimeouts && typeof lt.toolTimeouts === 'object' && !Array.isArray(lt.toolTimeouts)
      ? lt.toolTimeouts
      : {},
  };
}

/** T3 是否成立：取值字段是有限正数才推导；推导值不低于默认档 */
function deriveFromArgs(bare, args, t) {
  const spec = INPUT_DERIVED.get(bare);
  if (!spec) return null;
  const raw = args && typeof args === 'object' ? args[spec.argKey] : undefined;
  if (!Number.isFinite(raw) || raw <= 0) return null;
  const overhead = spec.overheadMs != null ? spec.overheadMs : t[spec.overheadKey];
  return Math.max(t.defaultMs, raw + overhead);
}

/** 该工具在无 T3/T4 时的档位（T1/T2 或工具自带的长默认值） */
function baselineMs(bare, t) {
  if (SLOW_BARE_NAMES.has(bare)) return t.slowMs;
  const own = LONG_DEFAULTS_MS.get(bare);
  if (own != null) return Math.max(t.defaultMs, own);
  return t.defaultMs;
}

function explicitOverrideMs(bare, t) {
  const v = t.toolTimeouts[bare];
  return Number.isFinite(v) && v > 0 ? v : null;
}

/** 解析本次调用该等多久（毫秒） */
export function resolveTimeoutMs(name, args, config) {
  const t = readThresholds(config);
  const bare = bareToolName(name);
  const explicit = explicitOverrideMs(bare, t);
  if (explicit != null) return explicit;
  const derived = deriveFromArgs(bare, args, t);
  if (derived != null) return derived;
  return baselineMs(bare, t);
}

/** 本次是否为「入参推导」出来的超时。用于把预期内的长档超时排除出 forceReconnect 计数。 */
export function isInputDerivedTimeout(name, args, config) {
  const t = readThresholds(config);
  const bare = bareToolName(name);
  if (explicitOverrideMs(bare, t) != null) return false;
  const derived = deriveFromArgs(bare, args, t);
  if (derived == null) return false;
  return derived !== baselineMs(bare, t);
}

/**
 * 派发处用的 opts 工厂。无 T3/T4 且非 T2/自带长默认值档时返回 null
 * → 调用方回退到既有的 {slow:true} 判定，行为与改动前一致。
 */
export function timeoutOptsFor(name, args, config) {
  const t = readThresholds(config);
  const bare = bareToolName(name);
  const explicit = explicitOverrideMs(bare, t);
  if (explicit != null) {
    return { timeoutMs: explicit, inputDerived: false };
  }
  const derived = deriveFromArgs(bare, args, t);
  if (derived != null) {
    return { timeoutMs: derived, inputDerived: derived !== baselineMs(bare, t) };
  }
  if (SLOW_BARE_NAMES.has(bare) || LONG_DEFAULTS_MS.has(bare)) {
    return { timeoutMs: baselineMs(bare, t), inputDerived: false };
  }
  return null;
}

export default { resolveTimeoutMs, isInputDerivedTimeout, timeoutOptsFor, readThresholds, bareToolName };


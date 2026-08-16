#!/usr/bin/env node
/**
 * RimWorld MCP Server - SSE/HTTP Mode
 * Manual start mode, Trae connects via HTTP
 */

import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { SSEServerTransport } from '@modelcontextprotocol/sdk/server/sse.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import express from 'express';
import cors from 'cors';
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
} from '@modelcontextprotocol/sdk/types.js';
import { exec, spawn } from 'child_process';
import { promisify } from 'util';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import os from 'os';
import { GabpClient } from './gabpClient.js';

const execAsync = promisify(exec);
// P2-MCP-1：加超时上限与 maxBuffer 兜底，避免 exec 子进程无限期 hang 或 stdout 撑爆内存。
// 默认 8s 超时 + 1MB maxBuffer；调用方可经 opts 覆盖（opts.timeout/opts.maxBuffer 显式覆盖默认）。
const execAsyncSafe = (cmd, opts = {}) => execAsync(cmd, {
  timeout: opts.timeout || 8000,
  maxBuffer: opts.maxBuffer || 1024 * 1024,
  ...opts
});

// P1-MCP-2：记录本进程（MCP 服务）自己启动的 RimWorld 进程 PID，供 stop_game 定向停止，
// 避免按镜像名杀（会误伤玩家手动开的其它 RimWorld 实例）。
let selfStartedPid = null;

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Config
const PORT = process.env.MCP_PORT || 3000;
const HOST = process.env.MCP_HOST || '127.0.0.1';
// !!! 鉴权设计说明（P0-CS-1 / P0-MCP-1，待实现，勿删除本注释）:
// 本项目默认绑定 127.0.0.1 且目前无鉴权；按作者决策，鉴权只针对「MCP 服务器 → 游戏内 UE HTTP」
// 这条链路（而非 MCP↔客户端，客户端侧靠 toolConfig 压缩列表管理）。
// 对齐机制（已在代码审查中拍板）: 游戏侧每次启动随机生成 token 并写入 MCP/ports.json 的 token 字段，
// 本服务启动时读取同一 token；所有「写操作」(execute_csharp_code / create_hook / area/* /
// debugaction/execute 等) 请求头带 Authorization: Bearer <token>，游戏侧校验失败返回 401；
// 「读操作」(status / log 等) 可免 token。
// 警告: 切勿在无鉴权时把 MCP_HOST 设为 0.0.0.0（非回环绑定会把上述全部操作面暴露到网络）。


// stdio 传输模式判定：环境变量 MCP_TRANSPORT=stdio 或启动参数含 --stdio
const IS_STDIO = process.env.MCP_TRANSPORT === 'stdio' || process.argv.includes('--stdio');

// Load config
function loadConfig() {
  const configPath = path.join(__dirname, 'config.json');
  const config = JSON.parse(fs.readFileSync(configPath, 'utf-8'));
  config.logPath = config.logPath.replace('%USERNAME%', os.userInfo().username);
  return config;
}

const config = loadConfig();

// UnityExplorer HTTP API 基础 URL
// 端口从模组写入的 ports.json 读取（ueHttpPort），启动时缓存一次；UE 请求连接失败时可强制重读刷新
const UE_PORTS_FILE = path.join(__dirname, 'ports.json');
const UE_PORT_FALLBACK = 3001;

// 读取 ports.json 的 ueHttpPort，失败/缺失返回 null
function readUeHttpPortFromFile() {
  try {
    const raw = fs.readFileSync(UE_PORTS_FILE, 'utf-8');
    const data = JSON.parse(raw);
    const p = Number(data && data.ueHttpPort);
    return Number.isInteger(p) && p > 0 && p <= 65535 ? p : null;
  } catch { return null; }
}

// 端口缓存：启动时读一次；forceRefresh 或缓存为空时重读
let ueHttpPortCache = null;
function getUeBaseUrl(forceRefresh = false) {
  if (forceRefresh || ueHttpPortCache === null) {
    ueHttpPortCache = readUeHttpPortFromFile() ?? UE_PORT_FALLBACK;
  }
  return `http://127.0.0.1:${ueHttpPortCache}`;
}

// ---- P0-MCP-1：从 ports.json 读 token（与 P0-CS-1 游戏侧写入契约对齐） ----
// 契约：游戏侧每次启动随机生成 token 并写入 ports.json 顶层 token 字段；
// MCP 侧写操作请求带 Authorization: Bearer <token>；连接失败/401 时强制刷新。
function readTokenFromFile() {
  try {
    const data = JSON.parse(fs.readFileSync(UE_PORTS_FILE, 'utf-8'));
    const t = typeof data?.token === 'string' ? data.token.trim() : null;
    return t && t.length >= 8 ? t : null;
  } catch { return null; }
}
let authTokenCache = null;
const AUTH_WARN_THROTTLE_MS = 30_000; // 无 token 告警节流：30s 内只打一次（简报步骤 1 的无 token WARN）
let lastAuthWarnAt = 0;
function getAuthToken(forceRefresh = false) {
  if (forceRefresh || authTokenCache === null) {
    authTokenCache = readTokenFromFile();
  }
  if (!authTokenCache) {
    // 简报步骤 1 逐字要求：无 token 时告警。用时间戳节流，避免平凡态刷屏但仍保留可见性。
    const now = Date.now();
    if (now - lastAuthWarnAt >= AUTH_WARN_THROTTLE_MS) {
      lastAuthWarnAt = now;
      log('WARN', 'ports.json 无 token（游戏未启动或未写入），写操作将不带鉴权头');
    }
  }
  return authTokenCache;
}
function refreshUeEndpoint() {
  ueHttpPortCache = null;
  authTokenCache = null;
  void getUeBaseUrl(true);
  return getAuthToken(true);
}

// P3-MCP-4：监听 ports.json 变更，文件被改/替换/删除重建时刷新端口与 token 缓存，下一请求自动用新值（无需重启）。
// rename 事件后旧 watcher 失效，关闭并重新挂载，形成兜底。
function watchPortsFile() {
  if (!fs.existsSync(UE_PORTS_FILE)) return;
  const onChange = () => { ueHttpPortCache = null; authTokenCache = null; log('INFO', 'ports.json 变更，已刷新端口/token 缓存（P3-MCP-4）'); };
  try {
    const w = fs.watch(UE_PORTS_FILE, (ev) => {
      if (ev === 'rename' || ev === 'change') {
        clearTimeout(onChange.__t);
        onChange.__t = setTimeout(onChange, 300);
        try { w.close(); watchPortsFile(); } catch (e) {}   // rename 后重挂
      }
    });
  } catch (e) { log('WARN', `ports.json watch 失败: ${e.message}`); }
}

const UE_BASE_URL = getUeBaseUrl();

// RIMAPI 基础 URL 配置
const RIMAPI_BASE_URL = (process.env.RIMAPI_BASE_URL || 'http://localhost:8765').replace(/\/$/, '');

// 错误消息常量
const ERROR_RIMAPI_NOT_ENABLED = "RIMAPI 模组未启用或未运行。请先安装并启用 RIMAPI 模组，然后加载游戏存档。";
const ERROR_MAP_NOT_LOADED = "需要进入游戏地图后才能使用此功能。请先加载或创建一个游戏存档。";

// ========== 相机流抓帧（post_stream_start / post_stream_stop 联动） ==========
// 当 post_stream_start 带 output_frames=true 时，MCP 端 spawn 一个抓帧子进程
// （node stream-capture/capture-udp-frames.cjs）连 RIMAPI 的 UDP 推流端口，流式逐帧写盘。
// 停止信号：父进程关闭子进程的 stdin（--stdin-stop），子进程收到 EOF 后优雅收尾并输出统计。
// 共享状态存在模块级，便于 post_stream_stop 联动停止。
const STREAM_CAPTURE_SCRIPT = path.join(__dirname, 'stream-capture', 'capture-udp-frames.cjs');
const STREAM_CAPTURE_DEFAULT_DIR = path.join(__dirname, 'stream-capture', 'out');
// 当前激活的抓帧会话：{ child, dir, port } ；无抓帧时为 null
let activeStreamCapture = null;

// spawn 抓帧子进程（流式写盘）。port 为 RIMAPI UDP 推流端口；dir 为输出目录。返回 { child, dir }。
function _startStreamCapture({ port, dir }) {
  // 互斥（P0-MCP-3）：已有抓帧会话运行中时拒绝再次 start，避免覆盖正在写入的帧。
  if (activeStreamCapture) {
    return { error: '已有抓帧会话在运行 (dir: ' + activeStreamCapture.dir + ', port: ' + activeStreamCapture.port
      + ')。请先调用 post_stream_stop 停止再重新录制，避免覆盖正在写入的帧（P0-MCP-3）。' };
  }
  const outDir = path.resolve(dir || STREAM_CAPTURE_DEFAULT_DIR);
  try { fs.mkdirSync(outDir, { recursive: true }); } catch (_e) { /* 忽略 */ }

  let child = null;
  try {
    child = spawn('node', [STREAM_CAPTURE_SCRIPT, '--host', '127.0.0.1', '--port', String(port),
      '--out', outDir, '--stdin-stop', '--idle-timeout-ms', '4000'], { stdio: ['pipe', 'pipe', 'pipe'] });
  } catch (_e) {
    return { error: '抓帧子进程启动失败: ' + (_e && _e.message || _e) };
  }
  let stderrTail = '';
  child.stderr && child.stderr.on('data', (d) => { stderrTail = (stderrTail + d).slice(-2000); });
  activeStreamCapture = { child, dir: outDir, port };
  return { child, dir: outDir };
}

// 停止抓帧：关闭子进程 stdin（EOF 触发其优雅收尾），等待至多 waitMs，超时则 kill。返回收尾信息。
function _stopStreamCapture(waitMs = config.timings?.streamStopWaitMs ?? 5000) {
  if (!activeStreamCapture) return { active: false };
  const cap = activeStreamCapture;
  activeStreamCapture = null;
  const { child, dir, port } = cap;
  return new Promise((resolve) => {
    let settled = false;
    const settle = (item) => { if (!settled) { settled = true; clearTimeout(timer); resolve({ dir, port, ...item }); } };
    // 等待子进程自然退出（stdin EOF → CAPTURE_RESULT 输出）
    child.once('exit', (code, signal) => settle({ childExited: true, exitCode: code, signal: signal || null }));
    const timer = setTimeout(() => {
      if (child.exitCode === null && child.signalCode === null) { try { child.kill('SIGKILL'); } catch (_e) { /* 忽略 */ } }
      settle({ childExited: false, forcedKill: true });
    }, waitMs);
    // 关闭子进程 stdin，触发其 EOF 并优雅收尾
    try { if (child.stdin) child.stdin.end(); } catch (_e) { /* 忽略 */ }
  });
}

// ========== 日志机制（Task 6） ==========
const LOGS_DIR = path.join(__dirname, 'logs');
try { fs.mkdirSync(LOGS_DIR, { recursive: true }); } catch (e) { /* 忽略目录创建失败 */ }

const MAX_LOG_FILES = 5;           // 最多保留 5 个日志文件（新日志覆盖最旧）
const MAX_LOG_BYTES = 100 * 1024 * 1024; // 单个日志文件上限（100MB）；达到即滚动，防单文件失控

let overflowSeq = 0;               // 滚动序号：每次滚动归档到递增后缀，支持同会话多次滚动且不冲突

// 保留上限：在 LOGS_DIR 下清理「当前会话之外」的最旧日志，使总文件数 <= MAX_LOG_FILES。
// 只删 mcp-*.log 与滚动产生的 mcp-*.log.overflow、mcp-*.log.overflow.N（运行期自有日志），绝不碰其它文件；
// 三者合并按 mtime 保最新 MAX_LOG_FILES 个，避免 .overflow 长期堆积再次撑爆磁盘。
function pruneLogs() {
  try {
    const files = fs.readdirSync(LOGS_DIR)
      .filter(f => /^mcp-\d{8}-\d{6}\.log(\.overflow(\.\d+)?)?$/.test(f))
      .map(f => ({ f, p: path.join(LOGS_DIR, f), m: fs.statSync(path.join(LOGS_DIR, f)).mtimeMs }))
      .filter(x => x.p !== LOG_FILE_PATH)              // 保留当前会话文件
      .sort((a, b) => b.m - a.m);                       // 最新在前
    while (files.length >= MAX_LOG_FILES) {             // 当前 1 个 + 其余 <= 4 → 总数 <=5
      const oldest = files.pop();
      try { fs.unlinkSync(oldest.p); } catch (e) { /* 忽略删除失败 */ }
    }
  } catch (e) { /* 日志清理失败不影响启动 */ }
}

function _pad2(n, w = 2) { return String(n).padStart(w, '0'); }
function _formatLogTime(d) { return `${_pad2(d.getHours())}:${_pad2(d.getMinutes())}:${_pad2(d.getSeconds())}.${_pad2(d.getMilliseconds(), 3)}`; }
function _formatFileStamp(d) { return `${d.getFullYear()}${_pad2(d.getMonth() + 1)}${_pad2(d.getDate())}-${_pad2(d.getHours())}${_pad2(d.getMinutes())}${_pad2(d.getSeconds())}`; }

const LOG_FILE_PATH = path.join(LOGS_DIR, `mcp-${_formatFileStamp(new Date())}.log`);
pruneLogs();   // 首调：启动时修剪旧日志，保留含当前在内的最近 5 个
let logStream = fs.createWriteStream(LOG_FILE_PATH, { flags: 'a' });
logStream.on('error', () => { /* 日志流错误（含 EPIPE）不转 uncaughtException：防死循环 */ });

// 当前日志超过 MAX_LOG_BYTES 时：结束当前流，归档到递增序号后缀，并立即开启新一轮日志。
// 新轮开始后调用 pruneLogs()，使文件数回到 <=5。
function rollLogIfNeeded() {
  if (logStream && typeof logStream.bytesWritten === 'number' && logStream.bytesWritten >= MAX_LOG_BYTES) {
    try { logStream.end(); } catch (e) {}
    try {
      // 把当前长文件立即收口（保留现场），再开新文件；归档名带递增序号，支持同会话多次滚动且不冲突
      fs.renameSync(LOG_FILE_PATH, `${LOG_FILE_PATH}.overflow.${++overflowSeq}`);
      logStream = fs.createWriteStream(LOG_FILE_PATH, { flags: 'a' });
      logStream.on('error', () => {});
      pruneLogs();   // 归档后总量可能 >5，立即修剪到 5
    } catch (e) { /* 滚动失败则继续写原流 */ }
  }
}

// 统一日志：格式 [HH:mm:ss.SSS] <LEVEL> msg，同时 console 输出与写入日志文件
// stdio 模式下 stdout 被 MCP 协议占用，日志改走 stderr 避免污染协议流；SSE 模式保持 stdout
function log(level, msg) {
  const line = `[${_formatLogTime(new Date())}] <${level}> ${msg}`;
  try { IS_STDIO ? console.error(line) : console.log(line); } catch (e) { /* ignore */ }
  try { logStream.write(line + '\n'); } catch (e) { /* ignore */ }
  rollLogIfNeeded();   // 体积上限：超限滚动 + 修剪到 5
}

// ========== per-tool 工具开关（toolConfig.json） ==========
const TOOL_CONFIG_PATH = path.join(__dirname, 'toolConfig.json');

function loadToolConfig() {
  try {
    const parsed = JSON.parse(fs.readFileSync(TOOL_CONFIG_PATH, 'utf-8'));
    return {
      defaultEnabled: parsed.defaultEnabled !== false,
      tools: (parsed && parsed.tools) || {}
    };
  } catch (e) {
    log('WARN', `toolConfig.json 读取/解析失败，回退为全部启用: ${e.message}`);
    return { defaultEnabled: true, tools: {} };
  }
}

const toolConfig = loadToolConfig();

function isToolEnabled(name) {
  // !!! 设计说明（已多次被当作"安全 BUG"汇报，请勿随手去掉聚合器可达性）:
  // 1) 这里的开关只控制该工具是否"直接暴露给 AI"（出现在 listTools / 直接可被客户端调用），
  //    即控的是模型上下文的体积，不是安全边界。
  // 2) 为什么默认只暴露 ~10-23 个：有些客户端（如 Trae）无法在明面上挂 200+ 个 MCP 工具，
  //    同时一次性把 200+ 工具描述 + 使用范式塞给 AI 也会把模型上下文撑爆。因此用
  //    defaultEnabled=false + 白名单压缩，让列表可控。
  // 3) 被"关闭"的工具仍经 agg_call_tool 按名可达（见 allToolNames / getEnabledTools）——这是刻意设计，
  //    保证"压缩列表"不损失功能；agg_call_tool 是元工具，始终由独立开关控制。
  // 4) 高危面（eval/execute_csharp_code/create_hook/force stop 等）如要真正按风险把门，应加
  //    "独立硬性启用门槛"，而不是依赖这里的默认关闭——否则只是 UI 伪装、仍可经 agg_call_tool 触发。
  if (name in toolConfig.tools) {
    return toolConfig.tools[name] === true;
  }
  return toolConfig.defaultEnabled;
}

// 工具参数摘要：仅保留关键字段，长度受限，避免刷屏
function summarizeToolArgs(name, args) {
  if (!args || typeof args !== 'object') return '{}';
  const parts = [];
  for (const [k, v] of Object.entries(args)) {
    if (v === undefined || v === null) continue;
    let sv;
    if (k === 'code' && typeof v === 'string') {
      sv = JSON.stringify(v.slice(0, 80));
    } else if (typeof v === 'string') {
      sv = v;
    } else {
      sv = JSON.stringify(v);
    }
    if (typeof sv === 'string' && sv.length > 80) sv = sv.slice(0, 80) + '…';
    parts.push(`${k}=${sv}`);
  }
  return parts.length ? `{${parts.join(', ')}}` : '{}';
}

// ========== 游戏阶段状态机（Task 1） ==========
const STAGES = {
  STOPPED: 'GAME_STOPPED',
  STARTING: 'GAME_STARTING',
  MAIN_MENU: 'MAIN_MENU',
  IN_GAME: 'IN_GAME'
};

const ERROR_CODES = {
  GAME_NOT_RUNNING: 'GAME_NOT_RUNNING',
  GAME_STARTING: 'GAME_STARTING',
  MAP_NOT_LOADED: 'MAP_NOT_LOADED',
  RIMAPI_NOT_READY: 'RIMAPI_NOT_READY',
  UE_NOT_READY: 'UE_NOT_READY',
  SERVICE_UNAVAILABLE: 'SERVICE_UNAVAILABLE'
};

// 统一 guidance 文案表（Task 1.5）：errorCode → { message, guidance }
const STAGE_ERROR_GUIDANCE = {
  GAME_NOT_RUNNING: { message: '游戏未运行', guidance: '先用 start_game 启动游戏（waitForNotification=true 会等待主菜单就绪），再调用本工具' },
  GAME_STARTING: { message: '游戏启动中', guidance: '游戏进程已存在但尚未就绪，等待 start_game 返回或稍后重试' },
  MAP_NOT_LOADED: { message: '游戏已运行但未进入地图/世界', guidance: '用 start_quick_test 快速进测试地图，或手动加载/创建殖民地后重试' },
  RIMAPI_NOT_READY: { message: '游戏运行中但 RIMAPI 模组未就绪', guidance: '确认游戏已启用 RIMAPI 模组并加载存档；或先 start_game 再重试' },
  UE_NOT_READY: { message: 'UE 未就绪', guidance: 'UE 需进入游戏世界才初始化；用 start_quick_test 进地图后重试' },
  SERVICE_UNAVAILABLE: { message: 'MCP 服务不可用', guidance: '请检查 MCP 服务器是否正在运行' }
};

// 生成统一错误结构：{ success:false, errorCode, currentStage, requiredStage, message, guidance }
function stageError(errorCode, currentStage, requiredStage, extraMessage) {
  const tpl = STAGE_ERROR_GUIDANCE[errorCode] || STAGE_ERROR_GUIDANCE.SERVICE_UNAVAILABLE;
  return {
    success: false,
    errorCode,
    currentStage,
    requiredStage,
    message: extraMessage ? `${tpl.message}（${extraMessage}）` : tpl.message,
    guidance: tpl.guidance
  };
}

// RIMAPI HTTP API 调用辅助函数
async function _rimapiRequest(method, path, query = null, jsonBody = null, timeoutSeconds = 30, needAuth = false) {
  try {
    let url = RIMAPI_BASE_URL + path;
    
    // 添加查询参数
    if (query) {
      const cleanQuery = Object.entries(query)
        .filter(([_, v]) => v !== null && v !== undefined)
        .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(v)}`)
        .join('&');
      if (cleanQuery) {
        url = url + '?' + cleanQuery;
      }
    }

    const options = {
      method: method.toUpperCase(),
      headers: {
        'Accept': 'application/json'
      }
    };

    if (jsonBody !== null) {
      options.headers['Content-Type'] = 'application/json';
      options.body = JSON.stringify(jsonBody);
    }

    // P0-MCP-1：写类调用方（needAuth=true）带 Authorization: Bearer <token>
    if (needAuth) { const t = getAuthToken(); if (t) options.headers['Authorization'] = `Bearer ${t}`; }

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), timeoutSeconds * 1000);
    options.signal = controller.signal;

    const response = await fetch(url, options);
    clearTimeout(timeoutId);

    const status = response.status;
    const raw = await response.text();

    if (!raw) {
      return { ok: status >= 200 && status < 300, status, data: {} };
    }

    try {
      const parsed = JSON.parse(raw);
      if (typeof parsed === 'object' && parsed !== null) {
        parsed._http_status = status;
        return parsed;
      }
      return { ok: status >= 200 && status < 300, status, data: parsed };
    } catch {
      return { ok: status >= 200 && status < 300, status, raw };
    }
  } catch (error) {
    if (error.name === 'AbortError') {
      return { ok: false, status: 0, error: 'Request timeout' };
    }
    return { ok: false, status: 0, error: error.message };
  }
}

// 统一判断 RIMAPI 响应是否成功：兼容 _rimapiRequest 的三种返回形态
// （解析后 JSON 对象含 success/_http_status；无 body 或非 JSON 时 { ok, status, data }；网络/超时错误 { ok:false, status:0, error }）
function _isRimapiSuccess(response) {
  if (!response || typeof response !== 'object') return false;
  if (response.success === true) return true;
  if (response._http_status >= 200 && response._http_status < 300) return true;
  if (response.ok === true) return true;
  if (response.status >= 200 && response.status < 300) return true;
  return false;
}

// 检查 RIMAPI 是否可用
async function checkRimapiAvailable() {
  try {
    const result = await _rimapiRequest('GET', '/api/v1/game/state', null, null, (config.timings?.rimapiProbeTimeoutMs ?? 5000) / 1000);
    // RIMAPI 返回 { success: true/false, data: {...} }
    return result.success === true || (result._http_status >= 200 && result._http_status < 300);
  } catch {
    return false;
  }
}

// 检查地图是否已加载
async function checkMapLoaded() {
  try {
    const result = await _rimapiRequest('GET', '/api/v1/game/state', null, null, (config.timings?.rimapiProbeTimeoutMs ?? 5000) / 1000);
    if (result.success && result.data) {
      // 根据 RIMAPI 返回的游戏状态判断是否在地图中
      // program_state: Playing 表示在地图中
      return result.data.program_state === 'Playing' || result.data.map_count > 0;
    }
    return false;
  } catch {
    return false;
  }
}

// 查询进程启动时间（毫秒时间戳），失败返回 null
// P2-MCP-1：改用 execAsyncSafe（8s 超时 + maxBuffer 兜底），并加 30s TTL 缓存。
// 缓存在固定 (pid, window) 窗口内复用，避免每次调用都 spawn powershell；
// pid 变化或缓存过期即重查，进程重启（pid 复用）由 pid 键位天然隔离。
const START_TIME_TTL_MS = 30_000;
let startTimeCache = null; // { pid, ms, at }
async function getProcessStartTime(pid) {
  const now = Date.now();
  if (
    startTimeCache &&
    startTimeCache.pid === pid &&
    startTimeCache.ms !== null &&
    now - startTimeCache.at < START_TIME_TTL_MS
  ) {
    return startTimeCache.ms;
  }
  let ms = null;
  try {
    const { stdout } = await execAsyncSafe(
      `powershell -NoProfile -Command "(Get-Process -Id ${pid} -ErrorAction SilentlyContinue).StartTime.ToString('yyyy-MM-dd HH:mm:ss.fff')"`
    );
    const timeStr = stdout.trim();
    if (!timeStr || /error/i.test(timeStr)) ms = null;
    else {
      const t = new Date(timeStr).getTime();
      ms = Number.isFinite(t) ? t : null;
    }
  } catch (e) {
    ms = null;
  }
  // 只有成功才写入缓存（失败不缓存，下次可重试），且保留 pid 配套
  startTimeCache = { pid, ms, at: now };
  return ms;
}

// 多源阶段探测（Task 1.2）：进程（tasklist）+ RIMAPI（8765）+ UE（3001）
async function detectGameStage() {
  const proc = await findRimWorldProcess();
  const gamePid = proc ? proc.pid : null;

  let rimapiAvailable = false;
  let programState = null;
  let mapCount = 0;
  let ueAvailable = false;
  let ueGame = null;

  if (gamePid) {
    // RIMAPI 探测
    const rimapiResult = await _rimapiRequest('GET', '/api/v1/game/state', null, null, (config.timings?.rimapiProbeTimeoutMs ?? 5000) / 1000);
    rimapiAvailable = rimapiResult.success === true || (rimapiResult._http_status >= 200 && rimapiResult._http_status < 300);
    if (rimapiAvailable && rimapiResult.data) {
      programState = rimapiResult.data.program_state;
      mapCount = rimapiResult.data.map_count || 0;
    }
    // UE 探测（短超时，P2-MCP-5 可配 ueProbeTimeoutMs）
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), config.timings?.ueProbeTimeoutMs ?? 3000);
      const ueResp = await fetch(`${UE_BASE_URL}/unityexplorer/status`, { signal: controller.signal });
      clearTimeout(timeoutId);
      if (ueResp.ok) {
        const ueData = await ueResp.json();
        // 兼容两种状态结构：{ data: { uiReady } } 或 { data: { status: { uiReady } } }
        const ueStatus = (ueData.data && (ueData.data.status || ueData.data)) || {};
        ueAvailable = ueData && ueData.success === true && ueData.data && ueStatus.uiReady === true;
        // 游戏状态块（UESDdebuger UELoader 直读 Verse 静态字段，不依赖 RIMAPI）：
        // 供 start_game（游戏初始化完成/主菜单就绪）与 start_quick_test（世界 tick 走动）判定
        if (ueStatus.game && typeof ueStatus.game === 'object') {
          const g = ueStatus.game;
          ueGame = {
            programState: typeof g.programState === 'string' ? g.programState : null,
            loading: g.loading === true,
            inGame: g.inGame === true,
            mainMenu: g.mainMenu === true,
            gameTick: Number.isFinite(g.gameTick) ? g.gameTick : null,
            paused: g.paused === true
          };
        }
      }
    } catch (e) {
      ueAvailable = false;
    }
  }

  // inGame 判定：UE 游戏状态优先（不依赖 RIMAPI）；否则退回原有 UE uiReady / RIMAPI 判定。
  // RIMAPI 模组未启用时（rimapi_available=false），仅凭 UE 也能正确识别阶段。
  const ueGameAvailable = ueGame !== null;
  const inGame = ueGameAvailable
    ? ueGame.inGame
    : (ueAvailable || (rimapiAvailable && (programState === 'Playing' || mapCount > 0)));

  let stage;
  if (!gamePid) {
    stage = STAGES.STOPPED;
  } else if (ueGameAvailable) {
    // 以 UE 游戏状态为准：仍在加载（DoPlayLoad/地图生成）→ STARTING；主菜单 → MAIN_MENU
    if (inGame) {
      stage = STAGES.IN_GAME;
    } else if (ueGame.loading) {
      stage = STAGES.STARTING;
    } else {
      stage = STAGES.MAIN_MENU;
    }
  } else if (!rimapiAvailable && !ueAvailable) {
    // 进程存在但双通道均不可达：启动时间未知或 <20s 判为 STARTING，否则视为主菜单
    const startTime = await getProcessStartTime(gamePid);
    if (startTime === null || Date.now() - startTime < 20000) {
      stage = STAGES.STARTING;
    } else {
      stage = STAGES.MAIN_MENU;
    }
  } else if (!inGame) {
    stage = STAGES.MAIN_MENU;
  } else {
    stage = STAGES.IN_GAME;
  }

  return {
    stage,
    running: !!gamePid,
    gamePid,
    rimapiAvailable,
    ueAvailable,
    inGame,
    ueGameAvailable,
    ueMainMenu: ueGameAvailable ? ueGame.mainMenu : false,
    ueGameTick: ueGameAvailable ? ueGame.gameTick : null,
    uePaused: ueGameAvailable ? ueGame.paused : false,
    ueLoading: ueGameAvailable ? ueGame.loading : false
  };
}

// 前置检查中间件（Task 1.3）：要求游戏处于指定阶段（支持数组，任一满足即可）
function requireStage(requiredStage, handler) {
  return async function(args) {
    const detected = await detectGameStage();
    const required = Array.isArray(requiredStage) ? requiredStage : [requiredStage];
    if (required.includes(detected.stage)) {
      return handler(args, detected);
    }
    let errorCode;
    if (detected.stage === STAGES.STOPPED) errorCode = ERROR_CODES.GAME_NOT_RUNNING;
    else if (detected.stage === STAGES.STARTING) errorCode = ERROR_CODES.GAME_STARTING;
    else if (detected.stage === STAGES.MAIN_MENU) errorCode = ERROR_CODES.MAP_NOT_LOADED;
    else errorCode = ERROR_CODES.SERVICE_UNAVAILABLE;
    return stageError(errorCode, detected.stage, required);
  };
}

// 前置检查中间件：需要 RIMAPI
function requireRimapi(handler) {
  return async function(args) {
    const detected = await detectGameStage();
    if (!detected.rimapiAvailable) {
      const errorCode = detected.running ? ERROR_CODES.RIMAPI_NOT_READY : ERROR_CODES.GAME_NOT_RUNNING;
      return stageError(errorCode, detected.stage, null, ERROR_RIMAPI_NOT_ENABLED);
    }
    return handler(args);
  };
}

// 前置检查中间件：需要地图已加载
function requireMapLoaded(handler) {
  return async function(args) {
    const detected = await detectGameStage();
    if (!detected.inGame) {
      const errorCode = detected.running ? ERROR_CODES.MAP_NOT_LOADED : ERROR_CODES.GAME_NOT_RUNNING;
      return stageError(errorCode, detected.stage, null, ERROR_MAP_NOT_LOADED);
    }
    return handler(args);
  };
}

// 轮询等待进入 IN_GAME（P6：load/存档加载后立即调用时游戏可能仍在 GAME_STARTING/MAIN_MENU）。
// 默认 60s 上限、2s 间隔；游戏退出（STOPPED）立即失败，避免空等。
// 返回 { ok:true, detected } 或 { ok:false, error: stageError 对象（已附阶段信息） }。
async function waitForInGame(timeoutMs = config.timings?.waitForInGameMs ?? 60000, pollMs = 2000) {
  const deadline = Date.now() + timeoutMs;
  let lastDetected = null;
  while (Date.now() < deadline) {
    const detected = await detectGameStage();
    lastDetected = detected;
    if (detected.stage === STAGES.IN_GAME) return { ok: true, detected };
    if (detected.stage === STAGES.STOPPED) {
      return { ok: false, error: stageError(ERROR_CODES.GAME_NOT_RUNNING, detected.stage, [STAGES.IN_GAME]) };
    }
    await new Promise(resolve => setTimeout(resolve, pollMs));
  }
  const d = lastDetected || await detectGameStage();
  const errorCode = d.running ? ERROR_CODES.MAP_NOT_LOADED : ERROR_CODES.GAME_NOT_RUNNING;
  return {
    ok: false,
    error: stageError(errorCode, d.stage, [STAGES.IN_GAME],
      `等待进入游戏超时（${timeoutMs}ms），当前阶段 ${d.stage}`)
  };
}

// UnityExplorer HTTP API 调用辅助函数
// opts（P0-MCP-1，可选）：{ auth: 'auto'|true|false } —— MVP 默认按 method 判定（POST/DELETE 带 token）；
// 对明确写操作可传 { auth: true } 强化。
async function callUnityExplorerAPI(endpoint, method = 'GET', body = null, opts = {}) {
  // P1-MCP-3 / P2-MCP-5：请求超时档位来自 config.timings（缺省优雅回退到 5s/10s）。
  // opts.timeoutMs 可显式覆盖；opts.slow=true 时走慢档。
  const timeouts = {
    normal: config.timings?.ueRequestTimeoutMs ?? 5000,
    slow:   config.timings?.ueSlowRequestTimeoutMs ?? 10000,
  };
  const timeoutMs = opts.slow ? timeouts.slow : (opts.timeoutMs || timeouts.normal);

  // options 提到 try 外：连接失败重试（换新端口）或 401 重试（换新 token）时可直接复用
  const options = {
    method,
    headers: {}
  };

  if (body && (method === 'POST' || method === 'DELETE')) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }

  // P0-MCP-1：写操作注入 Authorization: Bearer <token>（读操作免 token）
  const needsAuth = opts.auth === true || (opts.auth !== false && (method === 'POST' || method === 'DELETE'));
  if (needsAuth) {
    const token = getAuthToken();
    if (token) options.headers['Authorization'] = `Bearer ${token}`;
  }

  // P1-MCP-3：每次 fetch（含 401 刷新 token 重试、连接错误换端口重试）都用独立的 AbortController +
  // timeoutMs 定时器兜底。超时后 abort()，内层 catch 把 _aborted 挂到错误上；
  // 外层据此判定 UE_TIMEOUT，不再走连接错误重试（避免超时后再空等一轮）。
  const fetchWithTimeout = async (url, baseOpts) => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    const fetchOpts = { ...baseOpts, signal: controller.signal };
    try {
      return await fetch(url, fetchOpts);
    } catch (e) {
      // 超时兜底：单独标记，使其与 "连接错误" 区分（超时不重试）
      e._aborted = controller.signal.aborted;
      throw e;
    } finally {
      clearTimeout(timer);
    }
  };

  const readJson = async (resp) => {
    try { return await resp.json(); }
    catch { return {}; }   // 空/非 JSON body 防御（如 204）
  };

  try {
    const url = `${UE_BASE_URL}${endpoint}`;
    const response = await fetchWithTimeout(url, options);
    const data = await readJson(response);

    // P0-MCP-1：显式处理 401 —— 强制刷新 token（游戏可能已重写 ports.json）后重试一次，
    // 仍 401 则返回 UNAUTHORIZED，不再走阶段探测。
    // 重试 fetch 同样经 fetchWithTimeout，带独立 AbortController + 超时兜底。
    if (response.status === 401) {
      const t2 = refreshUeEndpoint();   // 强制刷新 token（游戏可能已重写 ports.json）
      if (t2) {
        options.headers['Authorization'] = `Bearer ${t2}`;
        const retryResp = await fetchWithTimeout(`${getUeBaseUrl(true)}${endpoint}`, options);
        const retryData = await readJson(retryResp);
        return { success: retryResp.ok && retryData.success, data: retryData.data || retryData, error: retryData.error, errorCode: retryData.errorCode, statusCode: retryResp.status };
      }
      return { success: false, errorCode: 'UNAUTHORIZED', error: '游戏侧鉴权被拒（token 失效）', statusCode: 401, guidance: '请重启游戏刷新 token' };
    }

    return {
      success: response.ok && data.success,
      data: data.data || data,
      error: data.error,
      errorCode: data.errorCode,
      statusCode: response.status
    };
  } catch (error) {
    // P1-MCP-3/Task4：AbortController 触发的超时一律返回 UE_TIMEOUT，绝不走连接错误重试。
    if (error && error._aborted) {
      return { success: false, errorCode: 'UE_TIMEOUT', error: `UE 请求超时(${timeoutMs}ms)`, statusCode: 0 };
    }
    // 连接类错误（fetch failed / ECONNREFUSED 等）：UE 端口可能已变化（ports.json 的 ueHttpPort），
    // 强制重读刷新缓存后，用新端口重试一次
    const isConnError = /fetch failed|ECONNREFUSED|ECONNRESET|ENOTFOUND|network error|connection refused/i.test(String(error && error.message));
    if (isConnError) {
      try {
        const retryUrl = `${getUeBaseUrl(true)}${endpoint}`;
        const retryResp = await fetchWithTimeout(retryUrl, options);
        const retryData = await readJson(retryResp);
        return {
          success: retryResp.ok && retryData.success,
          data: retryData.data || retryData,
          error: retryData.error,
          errorCode: retryData.errorCode,
          statusCode: retryResp.status
        };
      } catch (retryErr) {
        // 重试仍失败：以重试错误信息继续走原有错误处理
        error = retryErr;
      }
    }
    // 不再裸返回 HTTP_ERROR：经阶段探测生成可读错误（Task 2.3）
    try {
      const detected = await detectGameStage();
      if (!detected.running) {
        return stageError(ERROR_CODES.GAME_NOT_RUNNING, detected.stage, [STAGES.IN_GAME]);
      }
      if (!detected.inGame) {
        return stageError(ERROR_CODES.MAP_NOT_LOADED, detected.stage, [STAGES.IN_GAME]);
      }
      return stageError(ERROR_CODES.UE_NOT_READY, detected.stage, [STAGES.IN_GAME], `游戏内服务未响应：${error.message}`);
    } catch (stageErr) {
      return {
        success: false,
        error: error.message,
        errorCode: 'HTTP_ERROR'
      };
    }
  }
}

// P2-MCP-8：tasklist CSV 行解析。正确识别带引号字段、双引号转义、含逗号的字段值
// （如内存 "1,024 K"），不再用脆弱的 split('","')。返回按逗号分隔的单元格数组。
function parseTasklistCsv(line) {
  const cells = []; let cur = ''; let inQ = false;
  for (let i = 0; i < line.length; i++) {
    const c = line[i];
    if (inQ) {
      if (c === '"') { if (line[i + 1] === '"') { cur += '"'; i++; } else inQ = false; }
      else cur += c;
    } else if (c === '"') inQ = true;
    else if (c === ',') { cells.push(cur); cur = ''; }
    else cur += c;
  }
  cells.push(cur);
  return cells;
}

// Find process by name。PID 改为纯数字（Number.isInteger && >0）；返回 { name, pid: number, memory }。
async function findProcess(processName) {
  try {
    const { stdout } = await execAsyncSafe(`tasklist /FI "IMAGENAME eq ${processName}" /FO CSV /NH`);
    const line = stdout.split(/\r?\n/).map(s => s.trim()).find(l => l);
    if (!line) return null;
    const cells = parseTasklistCsv(line);
    const pidNum = Number(cells[1]);
    if (!Number.isInteger(pidNum) || pidNum <= 0) return null;   // PID 纯数字校验
    return { name: cells[0], pid: pidNum, memory: cells[4] };
  } catch (error) {
    return null;
  }
}

// Find RimWorld process
async function findRimWorldProcess() {
  return findProcess('RimWorldWin64.exe');
}

// P1-MCP-2：按 PID 探测进程是否存活（tasklist /FI "PID eq <pid>"）。
// 作为定向停止的前置校验：仅当自启 PID 仍存活时才按 PID 杀。
async function isPidAlive(pid) {
  if (!Number.isInteger(Number(pid)) || Number(pid) <= 0) return false;
  try {
    const { stdout } = await execAsync(`tasklist /FI "PID eq ${Number(pid)}" /NH`);
    return /\.exe\s+\d+/i.test(stdout);
  } catch (error) {
    return false;
  }
}

// Find Steam process
async function findSteamProcess() {
  return findProcess('steam.exe');
}

// 关闭指定进程的 Unity "Debug (Player)" 告知窗（Win32 WM_CLOSE，等同点击「确定」）。
// 匹配条件：窗口属于目标进程 + 标题精确等于 "Debug (Player)" + 可见，避免误伤其它窗口。
// 窗口在游戏启动后约 1~3s 才出现，此处轮询最多 timeoutMs 并反复关闭可能重复弹出的窗口。
// 返回是否至少成功关闭过一次。
// P3-MCP-5：内嵌的 C#/PowerShell 已抽取为独立脚本 MCP/dismiss-debug-player.ps1，这里按文件调用。
async function dismissDebugPlayerWindow(pid, timeoutMs = config.timings?.dismissWindowTimeoutMs ?? 20000) {
  if (!pid || process.platform !== 'win32') return false;
  const scriptPath = path.join(__dirname, 'dismiss-debug-player.ps1');
  if (!fs.existsSync(scriptPath)) {
    log('WARN', `start_game 自动关窗脚本缺失: ${scriptPath}`);
    return false;
  }
  try {
    const { stdout } = await execAsync(
      `powershell -NoProfile -ExecutionPolicy Bypass -File "${scriptPath}" -TargetPid ${String(pid)} -TimeoutMs ${String(timeoutMs)}`,
      { timeout: timeoutMs + 10000, maxBuffer: 1024 * 1024 }
    );
    const closed = stdout.includes('CLOSED');
    log(closed ? 'INFO' : 'WARN', `start_game 自动关窗（pid=${pid}）: ${closed ? 'CLOSED' : 'NONE'}`);
    return closed;
  } catch (e) {
    log('WARN', `start_game 自动关窗执行失败: ${e.message}`);
    return false;
  }
}

// Start game with Steam check and wait for main menu notification
// P3-MCP-1：useSteam 参数已废弃：启动 Steam 与否由 findSteamProcess() 自动检测决定，不再由该参数强制。保留签名仅向后兼容。
async function startGame(useSteam = true, waitForNotification = true, timeout = config.startTimeout || 180000) {
  try {
    const existingProcess = await findRimWorldProcess();
    if (existingProcess) {
      // P1-MCP-1：真实进程检测 + 不重复 spawn。游戏已在跑时不拒绝，而是返回当前阶段，
      // 明确不再启动新实例（避免双实例端口冲突）。
      const det = await detectGameStage();
      return {
        success: true,
        alreadyRunning: true,
        message: `游戏已运行 (PID: ${existingProcess.pid})，当前阶段 ${det.stage}；未重复启动（P1-MCP-1）。`,
        pid: existingProcess.pid ? Number(existingProcess.pid) : null,
        stage: det.stage
      };
    }

    // Reset notification state before starting
    mainMenuNotified = false;
    mainMenuNotifyTimestamp = null;

    // Check and start Steam if needed
    const steamProcess = await findSteamProcess();
    if (!steamProcess) {
      console.log('Steam not running, starting Steam...');
      
      if (!fs.existsSync(config.steamPath)) {
        return {
          success: false,
          message: `Steam not found at: ${config.steamPath}. Please update config.json with correct steamPath.`
        };
      }
      
      const steamChild = spawn(config.steamPath, [], {
        detached: true,
        stdio: 'ignore'
      });
      
      steamChild.on('error', (err) => {
        console.error('Failed to start Steam:', err);
      });
      
      console.log('Waiting 15 seconds for Steam to initialize...');
      await new Promise(resolve => setTimeout(resolve, config.timings?.steamWaitMs ?? 15000));
    }

    // Always start RimWorld directly from path (not through Steam)
    console.log('Starting RimWorld from path:', config.gamePath);
    
    if (!fs.existsSync(config.gamePath)) {
      return {
        success: false,
        message: `RimWorld not found at: ${config.gamePath}. Please update config.json with correct gamePath.`
      };
    }
    
    // 启动游戏：优先委托 McpRimDebug 桥接 launch（autoAttach=true）——
    // 它会在 attach 成功后才主动关闭 Unity 的 "Debug (Player)" 告知窗（实测 WM_CLOSE
    // 必须等调试器连上后才生效；未 attach 时该窗口会一直挂着）。attach 成功后游戏挂起，
    // 需再调 resume 让游戏继续运行。桥接不可用或启动失败时回退到本机 spawn。
    let startedPid = null;
    let bridgeFailedMsg = null;
    if (monoClient) {
      // launch 自动 attach 最长等 90s，给足 150s 超时避免 MCP 60s 默认超时提前触发回退
      const launchRes = await callMonoTool('launch', { path: config.gamePath, autoAttach: true }, 150000);
      const launchText = (launchRes.content || []).filter((c) => c.type === 'text').map((c) => c.text).join('');
      let launchObj = null;
      try { launchObj = JSON.parse(launchText); } catch (e) { launchObj = null; }
      const ok = launchObj && (launchObj.ok === true || launchObj.Ok === true);
      const data = (launchObj && (launchObj.data || launchObj.Data)) || {};
      if (ok && data.pid) {
        startedPid = data.pid;
        selfStartedPid = Number(data.pid);
        log('INFO', `start_game 桥接 launch 成功（pid=${data.pid}, autoAttached=${data.autoAttached}, debugPlayerWindowClosed=${data.debugPlayerWindowClosed}），resume 恢复游戏运行`);
        if (data.needResume || data.attached) {
          const resumeRes = await callMonoTool('resume', {});
          log('INFO', `start_game resume 结果: ${(resumeRes.content || []).map((c) => c.text || '').join('')}`);
        }
      } else {
        bridgeFailedMsg = launchObj && (launchObj.message || launchObj.Message) || launchText || 'launch 未成功';
        log('WARN', `start_game 桥接 launch 未成功，回退 spawn: ${String(bridgeFailedMsg).slice(0, 300)}`);
      }
    }

    if (!startedPid) {
      // 回退前先确认：桥接 launch 即使超时/失败，也可能已拉起游戏进程（launch 内部先启动
      // 进程再 attach）。此时必须复用已有进程，绝不能再次 spawn 造成双实例端口冲突。
      const already = await findRimWorldProcess();
      if (already) {
        startedPid = already.pid;
        selfStartedPid = Number(already.pid);
        log('WARN', `桥接 launch 未成功（${String(bridgeFailedMsg || '').slice(0, 200)}），但检测到游戏进程 pid=${already.pid}，复用而不重复启动`);
      } else {
        const child = spawn(config.gamePath, [], {
          detached: true,
          stdio: 'ignore'
        });

        child.on('error', (err) => {
          console.error('Failed to start game:', err);
        });

        // 回退路径：仅拉起进程并尽力尝试关窗（未 attach 时 WM_CLOSE 通常被 Unity 忽略，
        // 真正可靠的关窗依赖桥接 launch 的 attach 流程）
        const spawnPid = child && child.pid;
        if (spawnPid) {
          log('INFO', `start_game 已 spawn（pid=${spawnPid}），尽力自动关闭 Debug(Player) 告知窗 ...`);
          dismissDebugPlayerWindow(spawnPid).catch((e) =>
            log('WARN', `start_game 自动关窗异常: ${e.message}`));
        }
      }
    }

    await new Promise(resolve => setTimeout(resolve, 5000));
    const process = await findRimWorldProcess();
    // 无论走哪条启动路径（桥接 launch / 复用已存在 / 本机 spawn），最后都以 find 到的真实进程为准，
    // 统一登记为 selfStartedPid，供 stop_game 定向停止。
    if (process) {
      selfStartedPid = Number(process.pid);
    }
    
    if (!process) {
      return {
        success: false,
        message: 'Game failed to start'
      };
    }

    // If not waiting for notification, return immediately
    if (!waitForNotification) {
      return {
        success: true,
        message: `Game started (PID: ${process.pid})`,
        pid: process.pid
      };
    }

    // Wait for main menu: 轮询 detectGameStage()（Task 3，不依赖模组通知源）
    // 主菜单就绪 = 游戏初始化完成：UE 游戏状态块（直读 Verse，不依赖 RIMAPI）显示
    // ProgramState==Entry 且无进行中的长事件；RIMAPI 在 DoPlayLoad 阶段即可达，不能作为判据。
    console.log('Game started, waiting for main menu...');
    const startTime = Date.now();
    const checkInterval = 500;
    const processStableDelay = config.timings?.processStableDelayMs ?? 25000;  // UE 游戏状态不可用时的退回判据：进程存在且距启动 >= 25s
    
    while (Date.now() - startTime < timeout) {
      // 1) 模组通知为加速信号（任一先到即成功）
      if (mainMenuNotified) {
        return {
          success: true,
          message: 'Game started and main menu reached (via mod notification)',
          state: 'main_menu',
          pid: process.pid,
          elapsedTime: Date.now() - startTime,
          notifiedAt: mainMenuNotifyTimestamp,
          source: 'mod_notification'
        };
      }
      
      // 2) 轮询阶段探测
      const detected = await detectGameStage();
      const now = Date.now();
      
      // 3) 进程退出
      if (!detected.running) {
        return {
          success: false,
          message: 'Game process exited before reaching main menu',
          state: 'process_exit',
          currentStage: detected.stage,
          pid: process.pid,
          elapsedTime: now - startTime,
          guidance: '游戏进程已退出；可检查游戏是否启动失败，或查看 read_rimworld_log'
        };
      }
      
      // 4) 默认判据：游戏初始化完成（UE 游戏状态显示主菜单就绪，不依赖 RIMAPI）
      if (detected.ueMainMenu) {
        return {
          success: true,
          message: 'Game started and initialization complete (main menu ready)',
          state: 'main_menu',
          pid: detected.gamePid || process.pid,
          elapsedTime: now - startTime,
          source: 'stage_probe'
        };
      }
      
      // 5) 退回判据：UE 游戏状态不可用（UE HTTP 未启用/旧版 DLL）时，
      //    进程存在且启动已超稳定期（>= 25s）视为主菜单就绪
      if (!detected.ueGameAvailable && now - startTime >= processStableDelay) {
        return {
          success: true,
          message: 'Game started and main menu reached (process stable, UE game status unavailable)',
          state: 'main_menu',
          pid: detected.gamePid || process.pid,
          elapsedTime: now - startTime,
          source: 'stage_probe'
        };
      }
      
      await new Promise(resolve => setTimeout(resolve, checkInterval));
    }
    
    // Timeout
    const finalDetected = await detectGameStage();
    return {
      success: false,
      state: 'timeout',
      message: `Timeout waiting for main menu (${timeout}ms)`,
      currentStage: finalDetected.stage,
      pid: finalDetected.gamePid || process.pid,
      elapsedTime: Date.now() - startTime,
      guidance: '游戏进程已启动；可调 get_game_status 查看当前阶段，或手动等待主菜单'
    };
    
  } catch (error) {
    return {
      success: false,
      message: `Start failed: ${error.message}`
    };
  }
}

// Stop game
async function stopGame(force = false) {
  try {
    // P1-MCP-2：优先按 selfStartedPid 定向停止（只杀本服务启动的进程，不误伤玩家手动开的其它实例）。
    // 自启 PID 失效或无时回退 findRimWorldProcess()；只有两者都无才判定未运行。
    let pid = selfStartedPid && await isPidAlive(selfStartedPid) ? selfStartedPid : null;
    selfStartedPid = null;
    const proc = pid ? null : await findRimWorldProcess();
    if (!pid && !proc) {
      return { success: false, message: 'Game not running' };
    }
    const target = pid || Number(proc.pid);

    if (force) {
      await execAsync(`taskkill /F /PID ${target}`);
    } else {
      await execAsync(`taskkill /PID ${target}`);
    }

    await new Promise(resolve => setTimeout(resolve, 2000));
    const still = await findRimWorldProcess();

    if (still && Number(still.pid) === target) {
      return { success: false, message: 'Game still closing, may need force', pid: target };
    }

    return { success: true, message: 'Game closed', pid: target };
  } catch (error) {
    return { success: false, message: `Stop failed: ${error.message}` };
  }
}

// Get game status - 多源阶段探测（Task 1.4）+ GABP 桥接状态（spec §6.1）
// tool-cleanup 1.10：并入原 status（mono 调试器状态：debug 端口等）与 GABP 桥细节
// （agentId / lastError / connectedAt，来自 gabpClient.status()）
async function getGameStatus() {
  const detected = await detectGameStage();
  const gabpStatus = gabpBridge ? gabpBridge.status() : null;
  // mono 调试器状态：原 status 工具信息并入（McpRimDebug 仍保留 status 协议工具，仅不再对外暴露）；
  // 桥接就绪时探测一次（探测失败不阻断，仅置 probeError）
  let mono = { bridgeReady: false, toolsCount: 0 };
  if (monoClient) {
    mono = { bridgeReady: true, toolsCount: monoTools.length };
    try {
      const st = await monoClient.callTool({ name: 'status', arguments: {} });
      const content = Array.isArray(st && st.content) ? st.content : [];
      let parsed = null;
      for (const c of content) {
        if (c && c.type === 'text' && typeof c.text === 'string') {
          try { parsed = JSON.parse(c.text); break; } catch (e) { /* 跳过非 JSON 片段 */ }
        }
      }
      const data = parsed && typeof parsed === 'object' ? parsed.data : null;
      if (data && typeof data === 'object') {
        // 断点统计字段：兼容 PascalCase（C# ToolResult 默认序列化）与 camelCase 键名，数值缺省 0
        const numField = (key) => {
          const v = data[key] != null ? data[key] : data[key[0].toUpperCase() + key.slice(1)];
          return typeof v === 'number' && Number.isFinite(v) ? v : 0;
        };
        mono = {
          ...mono,
          state: data.state != null ? data.state : null,
          attached: data.attached === true,
          gameProcessRunning: data.gameProcessRunning === true,
          debugHost: data.debugHost != null ? data.debugHost : null,
          debugPort: data.debugPort != null ? data.debugPort : null,
          debugPortFromLog: data.debugPortFromLog != null ? data.debugPortFromLog : null,
          debugPortOpen: data.debugPortOpen === true,
          suspended: data.suspended === true,
          protocolVersion: data.protocolVersion != null ? data.protocolVersion : null,
          vmVersion: data.vmVersion != null ? data.vmVersion : null,
          needResume: data.needResume === true,
          lastDisconnectReason: data.lastDisconnectReason != null ? data.lastDisconnectReason : null,
          breakpointsActive: numField('breakpointsActive'),
          breakpointsAddedTotal: numField('breakpointsAddedTotal'),
          breakpointsRemovedTotal: numField('breakpointsRemovedTotal'),
          eventRequestsActive: numField('eventRequestsActive')
        };
      }
    } catch (e) {
      mono = { ...mono, probeError: e.message };
    }
  }
  // 断点卡死提示（breakpointHint）：mono 已 attach 且存在活动断点/事件请求，
  // 且调试器挂起（mono.suspended）或游戏 UE 侧暂停（detected.uePaused，来自 detectGameStage 的 ueGame.paused）
  // ——视为疑似卡在断点处；不满足条件时为 null
  const bpActiveCount = typeof mono.breakpointsActive === 'number' ? mono.breakpointsActive : 0;
  const evActiveCount = typeof mono.eventRequestsActive === 'number' ? mono.eventRequestsActive : 0;
  let breakpointHint = null;
  if (mono.attached === true && (bpActiveCount + evActiveCount > 0)
      && (mono.suspended === true || detected.uePaused === true)) {
    const hangSource = mono.suspended === true ? 'mono 调试器挂起' : '游戏 UE 侧暂停';
    breakpointHint = `游戏疑似卡住/挂起（${hangSource}）：剩余 ${bpActiveCount} 个断点`
      + (evActiveCount > 0 ? `、${evActiveCount} 个活动事件请求` : '')
      + `。建议：break_list 查看断点，break_remove/break_clear 释放断点，resume 恢复游戏执行`;
  }
  mono.breakpointHint = breakpointHint;
  return {
    running: detected.running,
    stage: detected.stage,
    game_pid: detected.gamePid,
    rimapi_available: detected.rimapiAvailable,
    ue_available: detected.ueAvailable,
    in_game: detected.inGame,
    ue_game_available: detected.ueGameAvailable,
    ue_game_tick: detected.ueGameTick,
    ue_game_paused: detected.uePaused,
    ue_game_loading: detected.ueLoading,
    ue_main_menu_ready: detected.ueMainMenu,
    gabp: gabpStatus
      ? {
          state: gabpStatus.state,
          port: gabpStatus.port,
          agentId: gabpStatus.agentId,
          toolsCount: gabpStatus.toolsCount,
          lastError: gabpStatus.lastError,
          connectedAt: gabpStatus.connectedAt
        }
      : { state: 'disabled', port: null, agentId: null, toolsCount: 0, lastError: null, connectedAt: null },
    mono
  };
}

// 游戏综合信息（tool-cleanup 1.9）：三源合并——
// 1) MCP 配置 config（本地，唯一确定可用源；保留原 get_config 的完整配置展示）
// 2) GABP 游戏信息（gabpBridge.callTool('rimworld/get_game_info')，GABP 连接后可用）
// 3) RIMAPI 版本（_rimapiRequest('GET', '/api/v1/version')，游戏运行且 RIMAPI 模组可用时）
// 外部源不可用（未连接/超时/失败）时对应字段返回占位信息，不阻断整体调用。
async function getGameInfo() {
  // 源 1：MCP 配置
  const configInfo = { ...config };
  // 源 2：GABP 游戏信息（仅连接时探测；失败不阻断）
  let gabpInfo = null;
  if (gabpBridge && gabpBridge.isConnected()) {
    try {
      const probe = await gabpBridge.callTool('rimworld/get_game_info', {});
      gabpInfo = probe && probe.ok ? probe.result : null;
    } catch (e) {
      log('WARN', `get_game_info: GABP 探测失败: ${e.message}`);
    }
  }
  // 源 3：RIMAPI 版本（仅游戏运行时探测；失败不阻断）
  let rimapiVersion = null;
  const detected = await detectGameStage();
  if (detected.running) {
    try {
      const ver = await _rimapiRequest('GET', '/api/v1/version', null, null, (config.timings?.rimapiProbeTimeoutMs ?? 5000) / 1000);
      if (ver && (ver.success === true || (ver._http_status >= 200 && ver._http_status < 300))) {
        rimapiVersion = ver.data && typeof ver.data === 'object' ? ver.data : { raw: ver.data };
      }
    } catch (e) {
      log('WARN', `get_game_info: RIMAPI 版本探测失败: ${e.message}`);
    }
  }
  return {
    config: configInfo,
    gabp: gabpInfo
      ? { available: true, info: gabpInfo }
      : { available: false, info: null, note: 'GABP 未连接或探测失败（需 RimBridgeServer 连接后可用）' },
    rimapi: rimapiVersion
      ? { available: true, version: rimapiVersion }
      : { available: false, version: null, note: 'RIMAPI 不可用（游戏未运行或模组未就绪）' },
    stage: detected.stage,
    running: detected.running,
    retrieved_at: new Date().toISOString()
  };
}

// Read log
async function readLog(lines = 100) {
  try {
    if (!fs.existsSync(config.logPath)) {
      return {
        success: false,
        message: `Log file not found: ${config.logPath}`
      };
    }

    const content = fs.readFileSync(config.logPath, 'utf-8');
    const allLines = content.split('\n');
    const lastLines = allLines.slice(-lines).join('\n');

    return {
      success: true,
      content: lastLines,
      totalLines: allLines.length,
      returnedLines: Math.min(lines, allLines.length),
      logPath: config.logPath
    };
  } catch (error) {
    return {
      success: false,
      message: `Read log failed: ${error.message}`
    };
  }
}

// Tail log
async function tailLog(lines = 50) {
  try {
    if (!fs.existsSync(config.logPath)) {
      return {
        success: false,
        message: `Log file not found: ${config.logPath}`
      };
    }

    const stats = fs.statSync(config.logPath);
    const content = fs.readFileSync(config.logPath, 'utf-8');
    const allLines = content.split('\n');
    
    return {
      success: true,
      content: allLines.slice(-lines).join('\n'),
      fileSize: stats.size,
      lastModified: stats.mtime,
      totalLines: allLines.length
    };
  } catch (error) {
    return {
      success: false,
      message: `Read log failed: ${error.message}`
    };
  }
}

// Start quick test and wait for map loaded（Task 4.3：新端点 + 轮询 inGame）
async function startQuickTest(timeout = config.timings?.quickTestTimeoutMs ?? 120000) {
  try {
    // Check if game is running
    const process = await findRimWorldProcess();
    if (!process) {
      return stageError(ERROR_CODES.GAME_NOT_RUNNING, STAGES.STOPPED, [STAGES.IN_GAME]);
    }

    // Reset map loaded notification state
    mapLoadedNotified = false;
    mapLoadedNotifyTimestamp = null;

    // Send HTTP command to UESDdebuger game mod to trigger quick test
    const modUrl = `${UE_BASE_URL}/trigger-quicktest`;
    
    try {
      // P1-MCP-3：trigger-quicktest 独立加 10s AbortController 兜底（触发失败/卡死不悬挂；slow 不适用，
      // 该 fetch 仅下发命令码，真正等待进图由下方轮询负责，故这里给固定 10s 即可）。
      const controller = new AbortController();
      const triggerTimer = setTimeout(() => controller.abort(), 10000);
      let response;
      try {
        response = await fetch(modUrl, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            ...(getAuthToken() ? { 'Authorization': `Bearer ${getAuthToken()}` } : {})
          },
          body: JSON.stringify({ timestamp: new Date().toISOString() }),
          signal: controller.signal
        });
      } finally {
        clearTimeout(triggerTimer);
      }
      
      if (!response.ok) {
        return stageError(ERROR_CODES.UE_NOT_READY, null, [STAGES.IN_GAME], `游戏内服务返回 ${response.status} ${response.statusText}`);
      }
      
      console.log('Quick test command sent to RimWorld mod, waiting for map loading...');
    } catch (fetchError) {
      // 请求失败：经阶段探测生成可读错误
      const detected = await detectGameStage();
      if (!detected.running) {
        return stageError(ERROR_CODES.GAME_NOT_RUNNING, detected.stage, [STAGES.IN_GAME]);
      }
      return stageError(ERROR_CODES.UE_NOT_READY, detected.stage, [STAGES.IN_GAME], `游戏内服务未响应：${fetchError.message}`);
    }

    // Wait for map loaded: 轮询 detectGameStage()，进图后需确认世界 tick 走动（不依赖 RIMAPI）。
    // 信号源：模组通知 / 阶段探测均可判定"已进图"；最终成功需 UE 游戏状态 gameTick 两次采样递增
    // （ProgramState==Playing 即 FinalizeInit 完成，tick 开始走动；暂停/仍在过渡则继续等待）。
    const startTime = Date.now();
    const checkInterval = 500;
    const tickCheckInterval = 1500;  // tick 对比采样间隔（保证正常速度下 tick 明显递增）
    let lastTick = null;             // 已进图后上一次采样到的 gameTick（null=尚未采样）
    
    while (Date.now() - startTime < timeout) {
      // 1) 轮询阶段探测
      const detected = await detectGameStage();
      
      // 2) 进程退出
      if (!detected.running) {
        return {
          success: false,
          message: 'Game process exited before map loading completed',
          state: 'process_exit',
          currentStage: detected.stage,
          elapsedTime: Date.now() - startTime
        };
      }
      
      // 3) 信号源：模组通知或阶段探测均可视为"已进图"
      const inGameNow = mapLoadedNotified || detected.inGame;
      if (!inGameNow) {
        lastTick = null;
        await new Promise(resolve => setTimeout(resolve, checkInterval));
        continue;
      }
      
      // 4) 世界 tick 走动确认（不依赖 RIMAPI，直读 UE 游戏状态 gameTick）：
      //    连续两次采样递增即地图生成完成（FinalizeInit）且世界在走动
      if (detected.ueGameTick !== null && detected.ueGameTick !== undefined) {
        if (lastTick !== null && detected.ueGameTick > lastTick) {
          return {
            success: true,
            message: 'Quick test completed, map loaded and world tick advancing',
            state: 'map_loaded',
            pid: detected.gamePid || process.pid,
            elapsedTime: Date.now() - startTime,
            gameTick: detected.ueGameTick,
            source: 'stage_probe'
          };
        }
        lastTick = detected.ueGameTick;
        await new Promise(resolve => setTimeout(resolve, tickCheckInterval));
      } else {
        // 无游戏状态（UE 不可用/旧版 DLL）：沿用旧判据 inGame 即成功
        return {
          success: true,
          message: 'Quick test completed and map loaded successfully',
          state: 'map_loaded',
          pid: detected.gamePid || process.pid,
          elapsedTime: Date.now() - startTime,
          source: 'stage_probe'
        };
      }
    }
    
    // Timeout
    const finalDetected = await detectGameStage();
    return {
      success: false,
      message: `Timeout waiting for map loaded / world tick advancing (${timeout}ms)`,
      state: 'timeout',
      currentStage: finalDetected.stage,
      pid: finalDetected.gamePid || process.pid,
      elapsedTime: Date.now() - startTime,
      guidance: '地图生成可能仍未完成，或游戏处于暂停（tick 未走动）；可调 get_game_status 查看当前阶段'
    };
    
  } catch (error) {
    return {
      success: false,
      message: `Quick test failed: ${error.message}`
    };
  }
}

// Tool definitions
// ========== McpRimDebug（mono 调试器）桥接 ==========
// 把 McpRimDebug（stdio 的 .NET MCP 服务器，20 个 mono 断点/求值工具）作为子进程内嵌转发，
// 使 rimworld_DebugInEnvironment 这一个 SSE 服务器同时提供：游戏控制 + UE 工具 + mono 调试工具。
const MONO_DEBUG_TOOL_NAMES = new Set([
  'attach', 'detach', 'resume', 'suspend', 'launch',
  'break_add', 'break_list', 'break_remove', 'break_clear', 'break_exception',
  'wait', 'step', 'threads', 'callstack', 'locals', 'inspect',
  'eval', 'find_types', 'find_methods'
]);

let monoClient = null;
let monoTransport = null;
let monoTools = []; // 桥接就绪后填充的协议工具描述（合并进 ListTools 响应）
let monoLastError = null; // 最近一次 mono 桥接/调用错误（供 /health 透出 mono.lastError）

// 解析 McpRimDebug 启动命令：
// 1) 便携模式（zip 运行时）：UESDdebuger/runtime/dotnet/dotnet.exe + runtime/McpRimDebug/McpRimDebug.dll
//    （FDD 发布，dll 与便携 dotnet 运行时配对运行，目标机无需安装 .NET）
// 2) 开发模式（本机已装 .NET）：McpRimDebug/bin/Debug/net10.0/McpRimDebug.exe（apphost 自举）
// 返回 { command, args } 或 null（都不可用时）
function monoDebugCommand() {
  const root = path.join(__dirname, '..');
  const portableDotnet = path.join(root, 'runtime', 'dotnet', 'dotnet.exe');
  const portableDll = path.join(root, 'runtime', 'McpRimDebug', 'McpRimDebug.dll');
  if (fs.existsSync(portableDotnet) && fs.existsSync(portableDll)) {
    return { command: portableDotnet, args: [portableDll], mode: 'portable' };
  }
  const devExe = path.join(root, 'McpRimDebug', 'bin', 'Debug', 'net10.0', 'McpRimDebug.exe');
  if (fs.existsSync(devExe)) {
    return { command: devExe, args: [], mode: 'dev' };
  }
  return null;
}

// 启动 McpRimDebug 子进程并拉取工具清单（失败则禁用桥接，不阻断主服务器）
async function initMonoBridge() {
  const cmd = monoDebugCommand();
  if (!cmd) {
    log('WARN', 'McpRimDebug not found (portable runtime/dotnet + dll nor dev bin); mono debug tools disabled');
    return;
  }
  const exe = cmd.command;
  try {
    monoTransport = new StdioClientTransport({
      command: exe,
      args: cmd.args,
      env: {
        ...process.env,
        MCP_RIMDBG_GAME_PATH: config.gamePath,
        MCP_RIMDBG_LOG_PATH: config.logPath,
        MCP_RIMDBG_HOST: '127.0.0.1',
        MCP_RIMDBG_PORT: '56574'
      }
    });
    monoClient = new Client(
      { name: 'UESDdebuger-MCP-mono-bridge', version: '1.0.0' },
      { capabilities: {} }
    );
    await monoClient.connect(monoTransport);
    const listResult = await monoClient.listTools();
    const rawTools = Array.isArray(listResult) ? listResult : ((listResult && listResult.tools) || []);
    monoTools = rawTools
      .filter(t => MONO_DEBUG_TOOL_NAMES.has(t.name))
      .map(t => ({
        name: t.name,
        description: `[mono调试] ${t.description || 'RimWorld Mono Soft Debugger tool'}`,
        inputSchema: t.inputSchema || { type: 'object', properties: {} }
      }));
    log('INFO', `McpRimDebug bridge ready: ${monoTools.length} mono debug tools (${exe})`);
  } catch (e) {
    monoLastError = e && e.message ? e.message : String(e);
    log('ERROR', `McpRimDebug bridge init failed: ${e.message}`);
    try { if (monoClient) await monoClient.close(); } catch (e2) { }
    try { if (monoTransport) await monoTransport.close(); } catch (e2) { }
    monoClient = null;
    monoTransport = null;
  }
}

// 转发工具调用到 McpRimDebug（协议工具参数 → SDK callTool → 规范化 MCP 响应）
// timeoutMs：SDK 默认请求超时 60s，但 launch 的自动 attach 最长可等待
// LaunchAutoAttachTimeoutMs=90s（端口出现 + 缓冲 + attach 重试），必须显式放宽，
// 否则 60s 先超时会错误地触发回退 spawn（造成双实例/关不掉窗口）。
async function callMonoTool(name, args, timeoutMs = config.timings?.monoToolTimeoutMs ?? 60000) {
  if (!monoClient) {
    return {
      content: [{ type: 'text', text: JSON.stringify({
        ok: false, error: 'mono 调试桥接未就绪（McpRimDebug 未启动或初始化失败，见服务器日志）'
      }, null, 2) }],
      isError: true
    };
  }
  try {
    const res = await monoClient.callTool({ name, arguments: args || {} }, undefined, {
      requestOptions: { timeout: timeoutMs }
    });
    const content = Array.isArray(res && res.content) ? res.content
      : [{ type: 'text', text: JSON.stringify(res || {}, null, 2) }];
    return { content, isError: !!(res && res.isError) };
  } catch (e) {
    monoLastError = e && e.message ? e.message : String(e);
    return {
      content: [{ type: 'text', text: JSON.stringify({
        ok: false, error: `mono 调试调用失败: ${e.message}`
      }, null, 2) }],
      isError: true
    };
  }
}

const TOOLS = [
  {
    name: 'start_game',
    description: 'Start RimWorld game and wait for main menu notification from mod',
    inputSchema: {
      type: 'object',
      properties: {
        useSteam: {
          type: 'boolean',
          description: 'Start via Steam（兼容保留，无效）已由进程检测自动决定（P3-MCP-1）',
          default: true
        },
        waitForNotification: {
          type: 'boolean',
          description: 'Wait for main menu notification from mod (default true)',
          default: true
        },
        timeout: {
          type: 'number',
          description: 'Timeout in ms for waiting notification (default from config.startTimeout, fallback 180000)'
        }
      }
    }
  },
  {
    name: 'stop_game',
    description: 'Stop RimWorld game',
    inputSchema: {
      type: 'object',
      properties: {
        force: {
          type: 'boolean',
          description: 'Force stop (default false)',
          default: false
        }
      }
    }
  },
  {
    name: 'get_game_status',
    description: '获取游戏运行状态与阶段（多源探测：进程/RIMAPI/UE，无需前置）',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'read_rimworld_log',
    description: '[RimWorld 日志文件] 读取 RimWorld 游戏日志文件（Player.log）末尾内容',
    inputSchema: {
      type: 'object',
      properties: {
        lines: {
          type: 'number',
          description: 'Number of lines (default 100)',
          default: 100
        }
      }
    }
  },
  {
    name: 'tail_rimworld_log',
    description: '[RimWorld 日志文件] 获取 RimWorld 游戏日志文件（Player.log）最新内容',
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
  {
    name: 'get_game_info',
    description: '获取游戏综合信息（三源合并：MCP 配置 config + GABP 游戏信息 + RIMAPI 版本），GABP/RIMAPI 不可用时对应字段返回占位信息，不阻断整体调用',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'start_quick_test',
    description: 'Trigger quick test in RimWorld and wait for map loading completion',
    inputSchema: {
      type: 'object',
      properties: {
        timeout: {
          type: 'number',
          description: 'Timeout in ms for waiting map loaded notification (default config.timings.quickTestTimeoutMs 120000)',
          default: 120000
        }
      }
    }
  },
  // ========== UnityExplorer (UE) 工具 ==========
  {
    name: 'inspect_type',
    description: '[UE工具][需要进入地图] Inspect a type in UnityExplorer, opens the Inspector panel showing fields, methods, properties of the type. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {
        typeName: { 
          type: 'string', 
          description: 'Full type name, e.g., RimWorld.Pawn, Verse.Map, System.String' 
        }
      },
      required: ['typeName']
    }
  },
  {
    name: 'get_unityexplorer_status',
    description: '[UE工具][需要进入地图] Get UnityExplorer current status, including UI readiness and panel states. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'clear_unityexplorer_logs',
    description: '[UE工具][需要进入地图][UE 日志面板] 清空 UnityExplorer 日志面板中的所有日志条目。Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_unityexplorer_logs',
    description: '[UE工具][需要进入地图][UE 日志面板] 从 UnityExplorer 日志面板获取日志内容。Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {
        count: { 
          type: 'number', 
          description: 'Number of entries to get (default 50, max 200)',
          default: 50,
          minimum: 1,
          maximum: 200
        }
      }
    }
  },
  {
    name: 'create_hook',
    description: '[UE工具][需要进入地图] Create a method Hook for intercepting and debugging method calls. Can intercept any method call to view parameters and return values. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {
        typeName: { 
          type: 'string', 
          description: '完整类型名，必须带命名空间（程序集前缀），如 Verse.Pawn（不要写成 RimWorld.Pawn 会报 TYPE_NOT_FOUND）' 
        },
        methodName: { 
          type: 'string', 
          description: 'Method name, e.g., Tick, ToString' 
        },
        patchType: { 
          type: 'string', 
          description: 'Patch type: Prefix (before method), Postfix (after method), Finalizer (always runs), Transpiler (modifies IL)',
          enum: ['Prefix', 'Postfix', 'Finalizer', 'Transpiler'],
          default: 'Postfix'
        },
        patchCode: { 
          type: 'string', 
          description: 'Custom Hook code (optional). Example: static void Postfix(Pawn __instance) { Log.Message(__instance.Name.ToString()); }' 
        }
      },
      required: ['typeName', 'methodName']
    }
  },
  {
    name: 'toggle_hook',
    description: '[UE工具][需要进入地图] Enable or disable a created Hook. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {
        hookId: { 
          type: 'string', 
          description: 'Hook ID returned by create_hook' 
        },
        enabled: { 
          type: 'boolean', 
          description: 'true to enable, false to disable' 
        }
      },
      required: ['hookId', 'enabled']
    }
  },
  {
    name: 'delete_hook',
    description: '[UE工具][需要进入地图] Delete a created Hook, the Hook will no longer be active after deletion. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {
        hookId: { 
          type: 'string', 
          description: 'Hook ID to delete' 
        }
      },
      required: ['hookId']
    }
  },
  {
    name: 'list_hooks',
    description: '[UE工具][需要进入地图] Get list of all created Hooks, including Hook ID, target method, enabled status, etc. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  // ========== C# Console 工具 ==========
  {
    name: 'execute_csharp_code',
    description: '[UE工具][需要进入地图] Execute C# code in UnityExplorer C# Console. Can run REPL code, define classes, or execute scripts. Only works after entering a game map and opening UnityExplorer C# Console panel at least once.',
    inputSchema: {
      type: 'object',
      properties: {
        code: { 
          type: 'string', 
          description: 'C# code to execute. Examples: "Log(\"Hello\");", "var x = 5; x + 3;", "using UnityEngine; Debug.Log(\"test\");"' 
        }
      },
      required: ['code']
    }
  },
  {
    name: 'reset_csharp_console',
    description: '[UE工具][需要进入地图] Reset UnityExplorer C# Console to initial state, clearing all variables and defined classes. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'add_using_directive',
    description: '[UE工具][需要进入地图] Add a using directive to C# Console, allowing use of types from the specified namespace without full qualification. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {
        namespace: { 
          type: 'string', 
          description: 'Namespace to add, e.g., "UnityEngine.UI", "System.Linq", "RimWorld"' 
        }
      },
      required: ['namespace']
    }
  },
  // ===== UE 工具定义结束 =====
  // ========== RIMAPI 工具 ==========
  // 流控制
  {
    name: 'post_stream_start',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 开始相机视频流；output_frames=true 时随流抓帧输出图片序列',
    inputSchema: {
      type: 'object',
      properties: {
        output_frames: { type: 'boolean', description: '是否输出图片流（抓帧保存为 JPEG 序列）。true 则随流逐帧写到 out_dir（缺省 stream-capture/out），调用 post_stream_stop 停止并产出结果' },
        out_dir: { type: 'string', description: '图片流输出目录（配合 output_frames=true，缺省 stream-capture/out）' }
      }
    }
  },
  {
    name: 'post_stream_stop',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 停止相机视频流',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'post_stream_setup',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 配置视频流参数',
    inputSchema: {
      type: 'object',
      properties: {
        ip: { type: 'string', description: 'IP 地址' },
        port: { type: 'integer', description: '端口' },
        frame_width: { type: 'integer', description: '帧宽度' },
        frame_height: { type: 'integer', description: '帧高度' },
        fps: { type: 'integer', description: '帧率' },
        quality: { type: 'integer', description: 'JPEG 质量 (1-100)' }
      }
    }
  },
  // 查询类工具
  {
    name: 'get_maps',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取地图列表',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_map_things',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取地图上的物品',
    inputSchema: {
      type: 'object',
      properties: {
        map_id: { type: 'integer', description: '地图 ID' }
      },
      required: ['map_id']
    }
  },
  {
    name: 'get_map_plants',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取地图上的植物',
    inputSchema: {
      type: 'object',
      properties: {
        map_id: { type: 'integer', description: '地图 ID' }
      },
      required: ['map_id']
    }
  },
  {
    name: 'get_map_weather',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取地图天气信息',
    inputSchema: {
      type: 'object',
      properties: {
        map_id: { type: 'integer', description: '地图 ID' }
      },
      required: ['map_id']
    }
  },
  {
    name: 'get_map_animals',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取地图上的动物',
    inputSchema: {
      type: 'object',
      properties: {
        map_id: { type: 'integer', description: '地图 ID' }
      },
      required: ['map_id']
    }
  },
  {
    name: 'get_research',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取研究进度',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_factions',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取派系列表',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_world_caravans',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取世界地图商队列表',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  // 操作类工具
  {
    name: 'post_incident_execute',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 执行随机事件',
    inputSchema: {
      type: 'object',
      properties: {
        incident_def_name: { type: 'string', description: '事件定义名称，如 RaidEnemy, TraderCaravanArrival' },
        target_map_id: { type: 'integer', description: '目标地图 ID' }
      },
      required: ['incident_def_name', 'target_map_id']
    }
  },
  {
    name: 'post_order_designate',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 下达区域指令（采矿、收割、狩猎、拆除等）',
    inputSchema: {
      type: 'object',
      properties: {
        map_id: { type: 'integer', description: '地图 ID' },
        type: { 
          type: 'string', 
          description: '指令类型: mine(采矿), harvest(收割), hunt(狩猎), deconstruct(拆除), remove-all(清除所有)',
          enum: ['mine', 'harvest', 'hunt', 'deconstruct', 'remove-all', 'Mine', 'Harvest', 'Hunt', 'Deconstruct']
        },
        point_a: {
          type: 'object',
          description: '区域起点坐标 {x, y, z}',
          properties: {
            x: { type: 'integer' },
            y: { type: 'integer' },
            z: { type: 'integer' }
          },
          required: ['x', 'y', 'z']
        },
        point_b: {
          type: 'object',
          description: '区域终点坐标 {x, y, z}',
          properties: {
            x: { type: 'integer' },
            y: { type: 'integer' },
            z: { type: 'integer' }
          },
          required: ['x', 'y', 'z']
        }
      },
      required: ['map_id', 'type', 'point_a', 'point_b']
    }
  },
  // ========== 新增 RIMAPI 工具 ==========
  // 殖民者详细信息
  {
    name: 'get_colonists_detailed',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取所有殖民者的详细信息（包含需求、工作、医疗等）',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_colonist_detailed',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取单个殖民者的详细信息',
    inputSchema: {
      type: 'object',
      properties: {
        id: { 
          type: 'integer', 
          description: '殖民者 ID' 
        }
      },
      required: ['id']
    }
  },
  // UI 交互工具
  {
    name: 'post_ui_message',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 在游戏中显示消息通知',
    inputSchema: {
      type: 'object',
      properties: {
        text: { 
          type: 'string', 
          description: '消息文本' 
        }
      },
      required: ['text']
    }
  },
  {
    name: 'post_ui_dialog',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 在游戏中显示对话框',
    inputSchema: {
      type: 'object',
      properties: {
        text: { 
          type: 'string', 
          description: '对话框文本' 
        },
        title: { 
          type: 'string', 
          description: '对话框标题（可选）' 
        }
      },
      required: ['text']
    }
  },
  // 开发者控制台工具
  {
    name: 'post_dev_console',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 操作开发者控制台',
    inputSchema: {
      type: 'object',
      properties: {
        action: { 
          type: 'string', 
          description: '操作类型: message(发送消息), clear(清除控制台)',
          enum: ['message', 'clear']
        },
        message: { 
          type: 'string', 
          description: '消息内容（action=message 时必填）' 
        }
      },
      required: ['action']
    }
  },
  // ========== DebugAction 工具 ==========
  {
    name: 'get_debug_action_categories',
    description: '[DebugAction工具][需要进入地图] 获取所有DebugAction分类列表及其数量。',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'search_debug_actions',
    description: '[DebugAction工具][需要进入地图] 按关键词搜索DebugAction，在路径、标签和分类中搜索匹配项。仅返回简化信息（path和label），如需详细信息请使用 GABP 镜像 rimworld.get_debug_action。',
    inputSchema: {
      type: 'object',
      properties: {
        query: { 
          type: 'string', 
          description: '搜索关键词' 
        },
        category: { 
          type: 'string', 
          description: '可选，限定搜索的分类' 
        }
      },
      required: ['query']
    }
  },
  // ========== MapStructure 工具 ==========
  {
    name: 'get_map_structure',
    description: '[MapStructure工具][需要进入地图] 根据输入路径返回对应的Map结构数据。空路径返回根节点列表（Grid、Manager、Component等分类），不完整路径返回子菜单路径列表，完整路径返回详细信息（字段、属性、方法等）。',
    inputSchema: {
      type: 'object',
      properties: {
        path: { 
          type: 'string', 
          description: 'Map结构路径，如 "Grid\\ThingGrid"。为空时返回根节点列表' 
        }
      }
    }
  },
  {
    name: 'search_map_structure',
    description: '[MapStructure工具][需要进入地图] 通过关键词搜索Map结构路径，在路径、标签、分类、类名中搜索匹配项。支持分类过滤。',
    inputSchema: {
      type: 'object',
      properties: {
        query: { 
          type: 'string', 
          description: '搜索关键词' 
        },
        category: { 
          type: 'string', 
          description: '可选，分类过滤：Grid, Manager, Component, Watcher, Spawner, Lister, Path, Draw, Other' 
        }
      },
      required: ['query']
    }
  }
];

// ========== GABP（RimBridgeServer）桥接集成（spec §6/§7/§8） ==========

// 去重镜像（tool-cleanup 收窄）：仅 rimworld.search_debug_actions 保持去重隐藏（镜像经反向别名走 UE 原后端）。
// 其余 13 个镜像（list_colonists/list_mods/save_game/load_game/set_time_speed/clear_selection/
// list_debug_action_roots/list_debug_action_children/get_debug_action/execute_debug_action/
// jump_camera_to_cell/set_camera_zoom/select_pawn）已恢复暴露，作为被删原生工具的唯一入口。
const GABP_DEDUP_TOOL_NAMES = new Set([
  'rimworld.search_debug_actions'
]);

// 移除集（tool-cleanup）：RBS 框架工具 rimbridge.get_bridge_status / rimbridge.ping 信息并入
// get_game_status；旧镜像 rimworld.get_game_info 已由原生 get_game_info（三源合并）承接。
// 三者从 GABP 工具清单排除（tools-changed 过滤），且经 resolveToolAlias 拦截（命中即返回原名
// → 调用报「未知 GABP 镜像工具」，防止 rimworld.get_game_info 经前缀剥离误路由到原生 get_game_info）。
const GABP_REMOVED_TOOL_NAMES = new Set([
  'rimbridge.get_bridge_status',
  'rimbridge.ping',
  'rimworld.get_game_info'
]);

// GABP 桥接状态（惰性初始化，见 startGABPBridge）
let gabpBridge = null;
let gabpTools = []; // 连接后从 GabpClient 'tools-changed' 事件填充（仅 isConnected() 时非空）

// GABP 假死检测：连续 tools/call 超时达阈值时主动断开重连（RimBridge 不响应但 TCP 未断时）。
let gabpConsecutiveTimeout = 0;
const GABP_TIMEOUT_RECOVER_THRESHOLD = (config?.rimBridge?.timeoutRecoverThreshold) || 3;

// GABP 发现+连接轮询编排（spec §4.3 / §6.1）。
// Task 1 遗留指针：connect() 重试不重新发现（token 每次游戏启动都会变，旧 token 会无限重试）。
// 本函数承担轮询发现：周期性 discoverRimBridge()，发现新 port/token 后中止在途重试并重新 connect()；
// 停止条件：disconnect（stop_game / 主动断开）时停止；意外断连（游戏退出/重启）后自动重新开始。
const GABP_POLL_INTERVAL_MS = 5000;
let gabpPollTimer = null;
let gabpConnectTarget = null; // 已发起连接的端点 {port, token}，用于检测端点变化

function stopGabpPolling() {
  if (gabpPollTimer !== null) {
    clearTimeout(gabpPollTimer);
    gabpPollTimer = null;
  }
  gabpConnectTarget = null;
}

async function scheduleDiscoverAndConnect() {
  if (!gabpBridge || gabpBridge.state === 'disabled') return;
  // config.rimBridge.autoConnect === false 时显式关闭自动发现+连接（spec §10）
  const rb = config.rimBridge || {};
  if (rb.autoConnect === false) return;
  if (gabpBridge.isConnected()) return;
  if (gabpPollTimer !== null) return; // 已在轮询
  log('INFO', 'GABP 开始轮询发现+连接');
  const poll = async () => {
    gabpPollTimer = null;
    if (!gabpBridge || gabpBridge.state === 'disabled' || gabpBridge.isConnected()) return;
    try {
      const discovered = gabpBridge.discoverRimBridge();
      if (discovered) {
        const changed = gabpConnectTarget === null ||
          discovered.port !== gabpConnectTarget.port ||
          discovered.token !== gabpConnectTarget.token;
        if (changed) {
          if (gabpConnectTarget !== null) {
            // 端点变化：先中止旧 token 的在途重试并清空目标，下一轮再连新端点（避免与在途 connect 竞态）
            log('WARN', `GABP 端点变化（port=${gabpConnectTarget.port} → ${discovered.port}），中止在途重试`);
            gabpBridge.disconnect();
            gabpConnectTarget = null;
          } else {
            gabpConnectTarget = { port: discovered.port, token: discovered.token };
            log('INFO', `GABP 发起连接 port=${discovered.port}`);
            gabpBridge.connect(discovered.port, discovered.token).catch((e) => {
              log('WARN', `GABP connect 异常: ${e && e.message ? e.message : String(e)}`);
            });
          }
        }
      }
    } catch (e) {
      log('WARN', `GABP 轮询发现异常: ${e && e.message ? e.message : String(e)}`);
    }
    if (gabpBridge && !gabpBridge.isConnected() && gabpBridge.state !== 'disabled') {
      gabpPollTimer = setTimeout(poll, GABP_POLL_INTERVAL_MS);
    }
  };
  poll();
}

// 启动 GABP 桥接（仿 startMonoBridge）：惰性——仅初始化 GabpClient 并注册事件，
// 不主动起子进程/不主动连接（连接由 scheduleDiscoverAndConnect 触发）；失败不阻断主服务器（spec §6.1）
async function startGABPBridge() {
  try {
    if (gabpBridge) return;
    gabpBridge = new GabpClient(config, { log });
    gabpBridge.on('tools-changed', (e) => {
      const all = e && Array.isArray(e.tools) ? e.tools : [];
      // §8 去重 + 移除集（tool-cleanup）：不镜像 rimworld.execute_debug_action（GABP 路由）；
      // rimworld.search_debug_actions 已走 UE 原后端，镜像同样不暴露；rimbridge.get_bridge_status /
      // rimbridge.ping / rimworld.get_game_info 命中移除集，一并从工具清单排除
      gabpTools = all.filter(t => t && typeof t.name === 'string' &&
        !GABP_DEDUP_TOOL_NAMES.has(t.name) && !GABP_REMOVED_TOOL_NAMES.has(t.name));
      log('INFO', `GABP 镜像工具并入清单: ${gabpTools.length} 个（去重+移除 ${all.length - gabpTools.length} 个）`);
    });
    gabpBridge.on('disconnected', (e) => {
      gabpTools = []; // 断连后镜像清单清空，工具列表回落
      stopGabpPolling();
      const reason = e && e.reason ? e.reason : 'unknown';
      log('INFO', `GABP 断开（reason=${reason}）`);
      if (reason !== 'client disconnect') {
        // 意外断连（游戏退出/重启，token 变化）→ 重新发现+连接（spec §4.3）
        scheduleDiscoverAndConnect();
      }
    });
    gabpBridge.on('error', (e) => {
      const err = e && e.error ? e.error : e;
      log('ERROR', `GABP error: ${err && err.message ? err.message : JSON.stringify(err)}`);
    });
    log('INFO', `GABP 桥接初始化完成（state=${gabpBridge.state}）`);
  } catch (e) {
    log('ERROR', `GABP 桥接初始化失败（不阻断主服务器）: ${e && e.message ? e.message : String(e)}`);
  }
}

// §6.4 结果归一化：保证 tools/call outcome 日志解析（{success:true|false}）与现有调用方契约兼容
function wrapGABPResult(raw, ok) {
  return ok
    ? { success: true, data: raw }
    : { success: false, errorCode: 'GABP_ERROR', error: String(raw) };
}

// GABP 错误响应（A 类断连报错 / 镜像不可用），文本输出统一 JSON.stringify(..., null, 2)
function gabpErrorResponse(msg) {
  return {
    content: [{ type: 'text', text: JSON.stringify(wrapGABPResult(msg, false), null, 2) }],
    isError: true
  };
}

// 检查表缺陷错误响应：文本统一可读 errorCode + message（区别于 gabpErrorResponse 的 wrapGABPResult 结构）
function contentErrorResponse(errorCode, message) {
  return { content: [{ type: 'text', text: JSON.stringify({ success: false, errorCode, message }, null, 2) }], isError: true };
}

// 检查表缺陷 4：识别 GABP 超时错误（用于 delete_area/delete_zone 健康探测）
function isGabpErrorTimeout(obj) {
  const txt = JSON.stringify(obj || {});
  return /GABP_ERROR/.test(txt) && (/超时/.test(txt) || /-32001/.test(txt));
}

// 手搓 Area 删除/清空（UEAreaActions）：调 UELoader 的 /area/delete、/area/clear 端点。
// 替代 RimBridgeServer 的有缺陷 delete_area/clear_area（读线程改主线程状态→连接僵死）。
// 返回结构与原 GABP 工具尽量兼容（success / deletedArea|area / selectedAllowedArea / state）。
async function callAreaAction(endpoint, areaId, toolName) {
  const r = await callUnityExplorerAPI(endpoint, 'POST', { areaId });
  if (r && r.success) {
    const body = r.data || {};
    return {
      content: [{ type: 'text', text: JSON.stringify({
        success: true,
        ...(body.deletedArea ? { deletedArea: body.deletedArea } : {}),
        ...(body.area ? { area: body.area } : {}),
        ...(body.previousCellCount != null ? { previousCellCount: body.previousCellCount } : {}),
        ...(body.selectedAllowedArea ? { selectedAllowedArea: body.selectedAllowedArea } : {}),
        state: body.state || {},
        source: 'ue-area-actions'
      }, null, 2) }]
    };
  }
  // 失败：返回 {success:false, message}
  const msg = (r && r.error) || (r && r.data && r.data.message) || 'UE Area 操作失败';
  return contentErrorResponse(r && r.errorCode ? r.errorCode : 'AREA_OP_ERROR',
    `${toolName} 失败：${msg}（改走 UELoader 自研 UEAreaActions，非 GABP 通道）`);
}

// 调用 GABP 工具并归一化（§6.3/§6.4）；filterResult：可选本地结果过滤（tool-cleanup 后无调用方传参，保留兼容）
// opts：透传给 gabpClient.callTool 的第三参，如 {slow:true} → 慢超时（requestTimeoutMs*2）
async function callGABPTool(rbsName, args, filterResult, opts) {
  if (!gabpBridge || !gabpBridge.isConnected()) {
    return gabpErrorResponse(`GABP 未连接（state=${gabpBridge ? gabpBridge.state : 'disabled'}），无法调用 ${rbsName}`);
  }
  const res = await gabpBridge.callTool(rbsName, args || {}, opts || {});
  if (res && res.ok) {
    gabpConsecutiveTimeout = 0; // 成功响应重置超时计数
    let data = res.result;
    if (typeof filterResult === 'function') {
      data = filterResult(data, args || {});
    }
    return { content: [{ type: 'text', text: JSON.stringify(wrapGABPResult(data, true), null, 2) }] };
  }
  // 透传 gabpClient 的错误（AuthenticationFailed / SessionNotEstablished 等语义）
  const err = res && res.error ? res.error : { code: -32603, message: String(res) };
  const errMsg = String((err && err.message) || err || '');
  // GABP 假死检测：tools/call 请求超时（RimBridge 不响应但 TCP 未断）连续达阈值 -> 主动断开重连
  if (/超时/.test(errMsg) || /-32001/.test(errMsg)) {
    gabpConsecutiveTimeout++;
    if (gabpConsecutiveTimeout >= GABP_TIMEOUT_RECOVER_THRESHOLD) {
      log('WARN', `GABP 连续 ${gabpConsecutiveTimeout} 次 tools/call 超时，判定 RimBridge 假死，主动断开并重新发现连接`);
      gabpConsecutiveTimeout = 0;
      if (gabpBridge) { try { gabpBridge.disconnect(); } catch (e) { log('WARN', `GABP 断开异常: ${e.message}`); } }
      scheduleDiscoverAndConnect();
    } else {
      log('DEBUG', `GABP 请求超时计数 ${gabpConsecutiveTimeout}/${GABP_TIMEOUT_RECOVER_THRESHOLD}（${rbsName}）`);
    }
  } else {
    gabpConsecutiveTimeout = 0; // 非超时失败（如参数错误）不累计
  }
  return gabpErrorResponse(`GABP 调用 ${rbsName} 失败（code=${err.code}）: ${errMsg}`);
}

// §8 镜像命名：rimworld.* / rimbridge.*（与原生平铺名零冲突）
function isGabpMirrorName(name) {
  return typeof name === 'string' && (name.startsWith('rimworld.') || name.startsWith('rimbridge.'));
}

function gabpToolNames() {
  const names = new Set();
  for (const t of gabpTools) {
    if (t && typeof t.name === 'string') names.add(t.name);
  }
  return names;
}

// 元工具：压缩模式下列出/调用已启用工具
const AGG_META_TOOLS = [
  { name: 'agg_list_tools', description: '列出当前已启用的全部工具及其描述', inputSchema: { type: 'object', properties: {}, additionalProperties: false } },
  { name: 'agg_call_tool', description: '按工具名调用任意已启用工具（用于压缩模式下调用未直接暴露的工具）', inputSchema: { type: 'object', properties: { tool: { type: 'string', description: '工具名称' }, args: { type: 'object', description: '工具参数', additionalProperties: true } }, required: ['tool'] } },
  { name: 'mcp_help', description: '[元工具] 多级菜单查询工具用法：无参返回一级概览（6 个类别及计数）；传 path 逐级深入（如 path:"bridge/lua_script"、path:"game_control/game/gameplay/camera"）返回子组计数或工具列表；传 tool 返回单个工具的详细说明（参数schema/前置条件/调用示例/易错提示）。始终可用。', inputSchema: { type: 'object', properties: { tool: { type: 'string', description: '要查询的工具名（省略则返回菜单概览/子组）' }, path: { type: 'string', description: '菜单路径，/ 分隔（如 "bridge/lua_script"、"game_control/game/gameplay/debug_action"）' } }, additionalProperties: false } }
];

// ========== mcp_help 元数据（add-mcp-introspection-tool spec） ==========
// 类别标注：system / unityexplorer / game_control（原 rimapi+debugaction 并入）/ mono(由 MONO_DEBUG_TOOL_NAMES 判定) / meta；
// GABP 镜像（rimworld.*/rimbridge.*）由 isGabpMirrorName 兜底判定为 bridge（tool-menu-hierarchy spec）
const TOOL_CATEGORY = {
  // system
  start_game: 'system', stop_game: 'system', get_game_status: 'system',
  read_rimworld_log: 'system', tail_rimworld_log: 'system', get_game_info: 'system', start_quick_test: 'system',
  // unityexplorer
  inspect_type: 'unityexplorer', get_unityexplorer_status: 'unityexplorer',
  clear_unityexplorer_logs: 'unityexplorer', get_unityexplorer_logs: 'unityexplorer',
  create_hook: 'unityexplorer', toggle_hook: 'unityexplorer', delete_hook: 'unityexplorer',
  list_hooks: 'unityexplorer', execute_csharp_code: 'unityexplorer',
  reset_csharp_console: 'unityexplorer', add_using_directive: 'unityexplorer',
  // rimapi（tool-cleanup：已删除 post_camera_change_zoom/post_camera_change_position/get_game_state/get_colonists/post_game_load/post_game_save/post_game_speed/get_version/get_mods_info/post_select/post_deselect）
  // tool-menu-hierarchy：rimapi 18 个并入 game_control 类别（game 下各子组）
  post_stream_start: 'game_control', post_stream_stop: 'game_control', post_stream_setup: 'game_control',
  get_maps: 'game_control',
  get_map_things: 'game_control', get_map_plants: 'game_control', get_map_weather: 'game_control',
  get_map_animals: 'game_control', get_research: 'game_control', get_factions: 'game_control',
  get_world_caravans: 'game_control',
  post_incident_execute: 'game_control', post_order_designate: 'game_control',
  get_colonists_detailed: 'game_control', get_colonist_detailed: 'game_control',
  post_ui_message: 'game_control', post_ui_dialog: 'game_control', post_dev_console: 'game_control',
  // debugaction（tool-cleanup：已删除 list_debug_actions/get_debug_action_detail/execute_debug_action）
  // tool-menu-hierarchy：debugaction 4 个并入 game_control 类别（game/gameplay/debug_action 子组）
  get_debug_action_categories: 'game_control',
  search_debug_actions: 'game_control', get_map_structure: 'game_control', search_map_structure: 'game_control',
  // meta
  agg_list_tools: 'meta', agg_call_tool: 'meta', mcp_help: 'meta'
};

// 前置条件显式标注（未列出的按类别兜底规则，见 toolPrecondition）
const TOOL_PRECONDITION = {
  start_game: '游戏未运行时可启动；已有 RimWorld 进程运行时会拒绝（端口冲突）',
  stop_game: '游戏运行中',
  start_quick_test: '游戏已运行（主菜单或游戏中）'
};

// 易错点与调用示例（运行时实测积累）
const TOOL_USAGE_NOTES = {
  get_colonist_detailed: { example: '{ "id": 123 }', notes: '参数为 id（殖民者ID），不是 pawn_id；兼容 pawn_id/pawnId/colonist_id 别名；id 可传 "Thing_Human737" 字符串，自动提取数字 id' },
  get_colonists_detailed: { example: '{}', notes: '返回全部殖民者详细信息（v2 端点）' },
  search_debug_actions: { example: '{ "query": "Spawn" }', notes: '参数为 query（子串匹配）；也兼容 keyword 别名' },
  search_map_structure: { example: '{ "query": "Thing" }', notes: '参数为 query（子串匹配）；也兼容 keyword 别名' },
  inspect_type: { example: '{ "typeName": "Verse.Pawn" }', notes: '需完整类型名（命名空间.类型）。注意用 Verse.Pawn 而非 RimWorld.Pawn（会报 Type not found）；兼容 type_name 别名' },
  get_map_structure: { example: '{}', notes: '查询当前地图结构（UE 实现）' },
  execute_csharp_code: { example: '{ "code": "Verse.GenTicks.TicksGame" }', notes: '控制台默认无 using 引用，裸类型名（如 GenTicks）会报 CS0103；须用全限定名或先 add_using_directive（兼容 script/expression 别名）' },
  get_map_things: { example: '{ "mapId": 1 }', notes: '查询地图物品列表；兼容 map_id/mapId/mapID 参数名（RIMAPI 端点为 map_id）' },
  get_map_plants: { example: '{ "mapId": 1 }', notes: '查询地图植物；兼容 map_id/mapId/mapID 参数名' },
  get_map_weather: { example: '{ "mapId": 1 }', notes: '查询地图天气；兼容 map_id/mapId/mapID 参数名' },
  get_map_animals: { example: '{ "mapId": 1 }', notes: '查询地图动物；兼容 map_id/mapId/mapID 参数名' },
  create_hook: { example: '{ "typeName": "Verse.Pawn", "methodName": "get_Label", "patchType": "Postfix" }', notes: '类型须带命名空间（Verse.Pawn，不是 RimWorld.Pawn 会报 TYPE_NOT_FOUND）；兼容 targetType/targetMethod/hookType 别名；patchCode 为可选自定义代码' },
  post_ui_message: { example: '{ "text": "Hello" }', notes: '参数为 text；兼容 message 别名' },
  post_dev_console: { example: '{ "action": "message", "message": "..." }', notes: 'action 可选 message/clear；兼容 console/command 别名' },
  eval: { example: '{ "expression": "this.def.defName", "threadId": 14, "frameIndex": 0 }', notes: 'mono 迷你 C# 语法：this/局部变量/静态类型全名/字面量 + 成员链；不支持算术运算符；需 VM 挂起或自动挂起求值；threadId 可为 0 自动选择' },
  attach: { example: '{ "port": 56574 }', notes: '游戏调试端口每次运行随机，先调 get_game_status 查看 debugPortFromLog 并传入该端口；mono 调试为一次性会话，detach/断开后如需再次调试需重启调试服务器（McpRimDebug）' },
  post_stream_start: { example: '{ "output_frames": true, "out_dir": "C:/tmp/frames" }', notes: 'RIMAPI 端点 POST /api/v1/stream/start；成功后用 GET /api/v1/stream/status 验证 IsStreaming，未真正拉起返回 STREAM_NOT_READY。可选参数 output_frames(bool)：true 时随流抓帧，把 UDP 推流逐帧流式保存为 JPEG 序列到 out_dir（缺省 MCP/stream-capture/out）；再调 post_stream_stop 即停止抓帧并产出结果' },
  post_stream_stop: { example: '{}', notes: 'RIMAPI 端点 POST /api/v1/stream/stop；若此前 post_stream_start 开启了 output_frames，本调用会自动停止抓帧子进程并返回帧统计（capture 字段）' },
  post_stream_setup: { example: '{ "ip": "127.0.0.1", "port": 5007, "frame_width": 1920, "frame_height": 1080, "fps": 15, "quality": 30 }', notes: 'RIMAPI 端点 POST /api/v1/stream/setup，参数经 JSON body 传递，映射 RIMAPI StreamConfigDto(Address/Port/FrameWidth/FrameHeight/TargetFps/JpegQuality)' },
  post_incident_execute: { example: '{ "incident_def_name": "RaidEnemy", "target_map_id": 1 }', notes: '内部映射为 POST /api/v1/incident/trigger 的 { name, map_id }；incident_def_name 为事件 Def 名；load 后立即调用会自动等待进入地图（默认 60s，可传 waitTimeoutMs 调整上限），超时返回当前阶段信息' },
  post_order_designate: { example: '{ "map_id": 1, "type": "Mine", "point_a": { "x": 0, "y": 0, "z": 0 }, "point_b": { "x": 5, "y": 0, "z": 5 } }', notes: '端点 POST /api/v1/order/designate/area；type 枚举：mine/harvest/hunt/deconstruct/remove-all' },
  find_types: { example: '{ "query": "ThingDef", "limit": 50 }', notes: 'query 子串匹配类型全名（大小写不敏感），limit 上限 200' },
  find_methods: { example: '{ "query": "Tick", "limit": 50 }', notes: 'query 子串匹配方法名（大小写不敏感），limit 上限 200' },
  // 元工具（usage-hint-on-failure：失败响应自动附加调用范式时使用）
  agg_call_tool: { example: '{ "tool": "get_game_status", "args": {} }', notes: '聚合调用任意工具（含 toolConfig.json 中禁用的工具）：tool 为工具名，args 为该工具参数；单个工具的正确调用范式可经 mcp_help tool=<工具名> 查询' },
  mcp_help: { example: '{ "tool": "create_hook" }', notes: '传 tool 查询单个工具用法（参数 schema/前置条件/调用示例/易错提示）；无参返回一级概览；传 path 逐级深入子组菜单' }
};

// ========== 多级菜单（tool-menu-hierarchy spec） ==========
// 一级类别 6 个：system / unityexplorer / mono / meta / bridge / game_control（合计 183 = 7+11+19+3+16+127）
// TOOL_SUBGROUP：143 个工具 → 叶子子组路径（键为实际工具名：GABP 镜像带 rimworld./rimbridge. 前缀，
// rimapi 与 debugaction 原生工具无前缀）。bridge = 16（lua_script 7 + ops 6 + wait 3）；
// game_control = 127（game 下 9 子组；gameplay 下 8 孙组，合计 64）。
// 类别归属见 toolCategory()：镜像 → bridge；TOOL_SUBGROUP 其余 → game_control。
const TOOL_SUBGROUP = {
  // ===== bridge 一级类别（16，rimbridge.*）=====
  // lua_script（7）
  'rimbridge.compile_lua': 'bridge/lua_script',
  'rimbridge.compile_lua_file': 'bridge/lua_script',
  'rimbridge.get_lua_reference': 'bridge/lua_script',
  'rimbridge.run_lua': 'bridge/lua_script',
  'rimbridge.run_lua_file': 'bridge/lua_script',
  'rimbridge.run_script': 'bridge/lua_script',
  'rimbridge.get_script_reference': 'bridge/lua_script',
  // ops（6）
  'rimbridge.get_capability': 'bridge/ops',
  'rimbridge.list_capabilities': 'bridge/ops',
  'rimbridge.get_operation': 'bridge/ops',
  'rimbridge.list_operation_events': 'bridge/ops',
  'rimbridge.list_operations': 'bridge/ops',
  'rimbridge.list_logs': 'bridge/ops',
  // wait（3）
  'rimbridge.wait_for_game_loaded': 'bridge/wait',
  'rimbridge.wait_for_long_event_idle': 'bridge/wait',
  'rimbridge.wait_for_operation': 'bridge/wait',

  // ===== game_control 一级类别（127，game 下 9 子组）=====
  // state（19：18 镜像 + post_incident_execute）
  'rimworld.pause_game': 'game_control/game/state',
  'rimworld.play_for': 'game_control/game/state',
  'rimworld.step_game_ticks': 'game_control/game/state',
  'rimworld.set_god_mode': 'game_control/game/state',
  'rimworld.set_debug_setting': 'game_control/game/state',
  'rimworld.set_colonist_job_logging': 'game_control/game/state',
  'rimworld.load_game_ready': 'game_control/game/state',
  'rimworld.go_to_main_menu': 'game_control/game/state',
  'rimworld.start_debug_game': 'game_control/game/state',
  'rimworld.start_debug_game_ready': 'game_control/game/state',
  'rimworld.play_until_letter': 'game_control/game/state',
  'rimworld.list_saves': 'game_control/game/state',
  'rimworld.get_ui_state': 'game_control/game/state',
  'rimworld.get_ui_layout': 'game_control/game/state',
  'rimworld.get_camera_state': 'game_control/game/state',
  'rimworld.save_game': 'game_control/game/state',
  'rimworld.load_game': 'game_control/game/state',
  'rimworld.set_time_speed': 'game_control/game/state',
  'post_incident_execute': 'game_control/game/state',

  // gameplay（64）→ 8 孙组
  // camera（8）
  'rimworld.move_camera': 'game_control/game/gameplay/camera',
  'rimworld.jump_camera_to_pawn': 'game_control/game/gameplay/camera',
  'rimworld.set_camera_zoom_extension': 'game_control/game/gameplay/camera',
  'rimworld.zoom_camera': 'game_control/game/gameplay/camera',
  'rimworld.frame_pawns': 'game_control/game/gameplay/camera',
  'rimworld.frame_cell_rect': 'game_control/game/gameplay/camera',
  'rimworld.jump_camera_to_cell': 'game_control/game/gameplay/camera',
  'rimworld.set_camera_zoom': 'game_control/game/gameplay/camera',
  // interact（8）
  'rimworld.click_cell': 'game_control/game/gameplay/interact',
  'rimworld.click_screen_target': 'game_control/game/gameplay/interact',
  'rimworld.click_ui_target': 'game_control/game/gameplay/interact',
  'rimworld.drag_cell': 'game_control/game/gameplay/interact',
  'rimworld.right_click_cell': 'game_control/game/gameplay/interact',
  'rimworld.scroll_ui_target': 'game_control/game/gameplay/interact',
  'rimworld.set_hover_target': 'game_control/game/gameplay/interact',
  'rimworld.clear_hover_target': 'game_control/game/gameplay/interact',
  // query（11：6 镜像 + 5 rimapi）
  'rimworld.get_map_target_info': 'game_control/game/gameplay/query',
  'rimworld.get_screen_targets': 'game_control/game/gameplay/query',
  'rimworld.get_cell_info': 'game_control/game/gameplay/query',
  'rimworld.get_cells_info': 'game_control/game/gameplay/query',
  'rimworld.find_random_cell_near': 'game_control/game/gameplay/query',
  'rimworld.flood_fill_cells': 'game_control/game/gameplay/query',
  'get_maps': 'game_control/game/gameplay/query',
  'get_map_things': 'game_control/game/gameplay/query',
  'get_map_plants': 'game_control/game/gameplay/query',
  'get_map_weather': 'game_control/game/gameplay/query',
  'get_map_animals': 'game_control/game/gameplay/query',
  // selection（5）
  'rimworld.get_selection_semantics': 'game_control/game/gameplay/selection',
  'rimworld.deselect_pawn': 'game_control/game/gameplay/selection',
  'rimworld.get_selected_pawn_inventory_state': 'game_control/game/gameplay/selection',
  'rimworld.clear_selection': 'game_control/game/gameplay/selection',
  'rimworld.select_pawn': 'game_control/game/gameplay/selection',
  // ui_panel（13）
  'rimworld.list_selected_gizmos': 'game_control/game/gameplay/ui_panel',
  'rimworld.execute_gizmo': 'game_control/game/gameplay/ui_panel',
  'rimworld.get_context_menu_options': 'game_control/game/gameplay/ui_panel',
  'rimworld.open_context_menu': 'game_control/game/gameplay/ui_panel',
  'rimworld.close_context_menu': 'game_control/game/gameplay/ui_panel',
  'rimworld.execute_context_menu_option': 'game_control/game/gameplay/ui_panel',
  'rimworld.list_inspect_tabs': 'game_control/game/gameplay/ui_panel',
  'rimworld.open_inspect_tab': 'game_control/game/gameplay/ui_panel',
  'rimworld.list_main_tabs': 'game_control/game/gameplay/ui_panel',
  'rimworld.open_main_tab': 'game_control/game/gameplay/ui_panel',
  'rimworld.close_main_tab': 'game_control/game/gameplay/ui_panel',
  'rimworld.open_window_by_type': 'game_control/game/gameplay/ui_panel',
  'rimworld.close_window': 'game_control/game/gameplay/ui_panel',
  // ui_notice（10）
  'rimworld.open_letter': 'game_control/game/gameplay/ui_notice',
  'rimworld.dismiss_letter': 'game_control/game/gameplay/ui_notice',
  'rimworld.list_letters': 'game_control/game/gameplay/ui_notice',
  'rimworld.list_messages': 'game_control/game/gameplay/ui_notice',
  'rimworld.list_alerts': 'game_control/game/gameplay/ui_notice',
  'rimworld.activate_alert': 'game_control/game/gameplay/ui_notice',
  'rimworld.list_languages': 'game_control/game/gameplay/ui_notice',
  'rimworld.switch_language': 'game_control/game/gameplay/ui_notice',
  'rimworld.take_screenshot': 'game_control/game/gameplay/ui_notice',
  'rimworld.screenshot_cell_rect': 'game_control/game/gameplay/ui_notice',
  // ui_dialog（5：2 镜像 + 3 rimapi）
  'rimworld.press_accept': 'game_control/game/gameplay/ui_dialog',
  'rimworld.press_cancel': 'game_control/game/gameplay/ui_dialog',
  'post_ui_message': 'game_control/game/gameplay/ui_dialog',
  'post_ui_dialog': 'game_control/game/gameplay/ui_dialog',
  'post_dev_console': 'game_control/game/gameplay/ui_dialog',
  // debug_action（4，原 debugaction）
  'get_debug_action_categories': 'game_control/game/gameplay/debug_action',
  'search_debug_actions': 'game_control/game/gameplay/debug_action',
  'get_map_structure': 'game_control/game/gameplay/debug_action',
  'search_map_structure': 'game_control/game/gameplay/debug_action',

  // arch（14：13 镜像 + post_order_designate）
  'rimworld.list_architect_categories': 'game_control/game/arch',
  'rimworld.list_architect_designators': 'game_control/game/arch',
  'rimworld.select_architect_designator': 'game_control/game/arch',
  'rimworld.apply_architect_designator': 'game_control/game/arch',
  'rimworld.get_designator_state': 'game_control/game/arch',
  'rimworld.list_areas': 'game_control/game/arch',
  'rimworld.create_allowed_area': 'game_control/game/arch',
  'rimworld.delete_area': 'game_control/game/arch',
  'rimworld.select_allowed_area': 'game_control/game/arch',
  'rimworld.clear_area': 'game_control/game/arch',
  'rimworld.list_zones': 'game_control/game/arch',
  'rimworld.delete_zone': 'game_control/game/arch',
  'rimworld.set_zone_target': 'game_control/game/arch',
  'post_order_designate': 'game_control/game/arch',

  // mods（9）
  'rimworld.list_mod_settings_surfaces': 'game_control/game/mods',
  'rimworld.get_mod_settings': 'game_control/game/mods',
  'rimworld.open_mod_settings': 'game_control/game/mods',
  'rimworld.reload_mod_settings': 'game_control/game/mods',
  'rimworld.update_mod_settings': 'game_control/game/mods',
  'rimworld.set_mod_enabled': 'game_control/game/mods',
  'rimworld.reorder_mod': 'game_control/game/mods',
  'rimworld.get_mod_configuration_status': 'game_control/game/mods',
  'rimworld.list_mods': 'game_control/game/mods',

  // dpa（6）
  'rimworld.dpa_status': 'game_control/game/dpa',
  'rimworld.dpa_snapshot': 'game_control/game/dpa',
  'rimworld.dpa_patch_methods': 'game_control/game/dpa',
  'rimworld.dpa_cleanup': 'game_control/game/dpa',
  'rimworld.dpa_reset': 'game_control/game/dpa',
  'rimworld.dpa_stop': 'game_control/game/dpa',

  // debugmenu（6）
  'rimworld.spawn_thing': 'game_control/game/debugmenu',
  'rimworld.set_draft': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_roots': 'game_control/game/debugmenu',
  'rimworld.list_debug_action_children': 'game_control/game/debugmenu',
  'rimworld.get_debug_action': 'game_control/game/debugmenu',
  'rimworld.execute_debug_action': 'game_control/game/debugmenu',

  // colonist（3：1 镜像 + 2 rimapi）
  'rimworld.list_colonists': 'game_control/game/colonist',
  'get_colonists_detailed': 'game_control/game/colonist',
  'get_colonist_detailed': 'game_control/game/colonist',

  // stream（3，rimapi）
  'post_stream_start': 'game_control/game/stream',
  'post_stream_stop': 'game_control/game/stream',
  'post_stream_setup': 'game_control/game/stream',

  // world（3，rimapi）
  'get_research': 'game_control/game/world',
  'get_factions': 'game_control/game/world',
  'get_world_caravans': 'game_control/game/world'
};

// 一级类别顺序（mcp_help 一级概览与菜单树根）
const MENU_CATEGORIES = ['system', 'unityexplorer', 'mono', 'meta', 'bridge', 'game_control'];
// bridge 二级子组（3）
const MENU_BRIDGE_SUBGROUPS = ['lua_script', 'ops', 'wait'];
// game_control → game 下 9 子组
const MENU_GAME_CONTROL_SUBGROUPS = { game: ['state', 'gameplay', 'arch', 'mods', 'dpa', 'debugmenu', 'colonist', 'stream', 'world'] };
// gameplay 下 8 孙组
const MENU_GAMEPLAY_SUBGROUPS = ['camera', 'interact', 'query', 'selection', 'ui_panel', 'ui_notice', 'ui_dialog', 'debug_action'];

// 旧名 → 新路径（BREAKING 迁移提示）
const MENU_PATH_MIGRATIONS = {
  rimbridge: 'bridge',
  'game_control/bridge': 'bridge',
  debugaction: 'game_control/game/gameplay/debug_action',
  'game_control/debugaction': 'game_control/game/gameplay/debug_action'
};

// 子组工具计数：value 等于 prefix 或以 prefix/ 开头（供非叶子子组聚合计数）
function subgroupToolCount(prefix) {
  let n = 0;
  for (const v of Object.values(TOOL_SUBGROUP)) {
    if (v === prefix || v.startsWith(prefix + '/')) n++;
  }
  return n;
}

// 解析多级菜单路径（/ 分隔）。返回：
// { type:'subgroup', path, name, children:[{name,path,count}] } 或
// { type:'tools', path, name, filter:{category|subgroup} } 或 { error }
function resolveMenuPath(pathStr) {
  const segs = String(pathStr || '').split('/').filter(s => s && s.trim());
  if (segs.length === 0) {
    return { error: '空路径：请从一级类别开始（' + MENU_CATEGORIES.join('/') + '）' };
  }
  const joined = segs.join('/');
  if (MENU_PATH_MIGRATIONS[joined]) {
    return { error: `路径已变更：${joined} → ${MENU_PATH_MIGRATIONS[joined]}（请改用新路径）` };
  }
  const category = segs[0];
  if (!MENU_CATEGORIES.includes(category)) {
    return { error: `未知一级类别: ${category}；可用：${MENU_CATEGORIES.join('/')}` };
  }
  // 叶子类别（无子组）：system/unityexplorer/mono/meta → 工具列表
  if (['system', 'unityexplorer', 'mono', 'meta'].includes(category)) {
    if (segs.length === 1) return { type: 'tools', path: category, name: category, filter: { category } };
    return { error: `${category} 为叶子类别，无更细路径；完整路径: ${category}` };
  }
  if (category === 'bridge') {
    if (segs.length === 1) {
      return {
        type: 'subgroup', path: 'bridge', name: 'bridge',
        children: MENU_BRIDGE_SUBGROUPS.map(s => ({ name: s, path: `bridge/${s}`, count: subgroupToolCount(`bridge/${s}`) }))
      };
    }
    const sub = segs[1];
    if (!MENU_BRIDGE_SUBGROUPS.includes(sub)) {
      return { error: `未知 bridge 子组: ${sub}；可用：${MENU_BRIDGE_SUBGROUPS.map(s => `bridge/${s}`).join('、')}` };
    }
    if (segs.length === 2) return { type: 'tools', path: `bridge/${sub}`, name: sub, filter: { subgroup: `bridge/${sub}` } };
    return { error: `bridge/${sub} 为叶子子组，无更细路径；完整路径: bridge/${sub}` };
  }
  // category === 'game_control'
  if (segs.length === 1) {
    return {
      type: 'subgroup', path: 'game_control', name: 'game_control',
      children: [{ name: 'game', path: 'game_control/game', count: subgroupToolCount('game_control/game') }]
    };
  }
  if (segs[1] !== 'game') {
    return { error: `未知 game_control 子组: ${segs[1]}；game_control 下唯一子组为 game（完整路径 game_control/game）` };
  }
  const gameSubs = MENU_GAME_CONTROL_SUBGROUPS.game;
  if (segs.length === 2) {
    return {
      type: 'subgroup', path: 'game_control/game', name: 'game',
      children: gameSubs.map(s => ({ name: s, path: `game_control/game/${s}`, count: subgroupToolCount(`game_control/game/${s}`) }))
    };
  }
  const sub = segs[2];
  if (!gameSubs.includes(sub)) {
    return { error: `未知 game 子组: ${sub}；可用：${gameSubs.map(s => `game_control/game/${s}`).join('、')}` };
  }
  if (sub !== 'gameplay') {
    if (segs.length === 3) return { type: 'tools', path: `game_control/game/${sub}`, name: sub, filter: { subgroup: `game_control/game/${sub}` } };
    return { error: `game_control/game/${sub} 为叶子子组，无更细路径；完整路径: game_control/game/${sub}` };
  }
  if (segs.length === 3) {
    return {
      type: 'subgroup', path: 'game_control/game/gameplay', name: 'gameplay',
      children: MENU_GAMEPLAY_SUBGROUPS.map(s => ({ name: s, path: `game_control/game/gameplay/${s}`, count: subgroupToolCount(`game_control/game/gameplay/${s}`) }))
    };
  }
  const gp = segs[3];
  if (!MENU_GAMEPLAY_SUBGROUPS.includes(gp)) {
    return { error: `未知 gameplay 孙组: ${gp}；可用：${MENU_GAMEPLAY_SUBGROUPS.map(s => `game_control/game/gameplay/${s}`).join('、')}` };
  }
  if (segs.length === 4) return { type: 'tools', path: `game_control/game/gameplay/${gp}`, name: gp, filter: { subgroup: `game_control/game/gameplay/${gp}` } };
  return { error: `game_control/game/gameplay/${gp} 为叶子孙组，无更细路径；完整路径: game_control/game/gameplay/${gp}` };
}

// 工具名 → 启用状态与描述首行（叶子工具列表用；GABP 镜像未连接时 entries 无对应项，用占位描述）
function toolListLine(name) {
  const t = allToolEntries().find(e => e.name === name);
  const desc = t ? String(t.description || '') : (TOOL_SUBGROUP[name] ? '（GABP 镜像，连接 RimBridgeServer 后自动出现）' : '');
  return `- [${toolEnabledNow(name) ? '启用' : '禁用'}] ${name} ${String(desc).split(/\r?\n/)[0]}`;
}

// 构建菜单节点输出（subgroup → 子组列表；tools → 工具名列表）
function buildMenuNode(node) {
  if (!node || node.error) {
    return { content: [{ type: 'text', text: JSON.stringify({ success: false, error: node && node.error }, null, 2) }], isError: true };
  }
  if (node.type === 'subgroup') {
    const payload = {
      level: node.path.split('/').length,
      path: node.path,
      name: node.name,
      total: node.children.reduce((a, c) => a + c.count, 0),
      children: node.children,
      note: '继续深入请传 path（如 ' + (node.children[0] ? node.children[0].path : '') + '）；叶子节点传 path 返回工具列表；查询单个工具传 tool 参数'
    };
    return { content: [{ type: 'text', text: JSON.stringify(payload, null, 2) }] };
  }
  // type === 'tools'
  const names = allToolEntries()
    .filter(t => {
      if (node.filter && node.filter.category) return toolCategory(t.name) === node.filter.category;
      if (node.filter && node.filter.subgroup) return TOOL_SUBGROUP[t.name] === node.filter.subgroup;
      return false;
    })
    .map(t => t.name);
  // 叶子节点应覆盖 TOOL_SUBGROUP 全量（GABP 未连接时 entries 缺镜像名，从 TOOL_SUBGROUP 补全）
  if (node.filter && node.filter.subgroup) {
    for (const [k, v] of Object.entries(TOOL_SUBGROUP)) {
      if (v === node.filter.subgroup && !names.includes(k)) names.push(k);
    }
  }
  names.sort();
  const payload = {
    level: node.path.split('/').length,
    path: node.path,
    name: node.name,
    total: names.length,
    tools: names.map(toolListLine)
  };
  return { content: [{ type: 'text', text: JSON.stringify(payload, null, 2) }] };
}

function toolCategory(name) {
  if (TOOL_CATEGORY[name]) return TOOL_CATEGORY[name];
  if (MONO_DEBUG_TOOL_NAMES.has(name)) return 'mono';
  if (isGabpMirrorName(name)) return 'bridge';
  if (TOOL_SUBGROUP[name]) return 'game_control';
  return 'unknown';
}

function toolPrecondition(name) {
  if (TOOL_PRECONDITION[name]) return TOOL_PRECONDITION[name];
  const cat = toolCategory(name);
  if (cat === 'mono') return '需先 attach 连接调试端口（端口每次运行随机，先调 get_game_status 查看 debugPortFromLog 并传入该端口）';
  if (cat === 'bridge') return '需 RimBridgeServer 连接';
  if (cat === 'unityexplorer') return '需进入地图（UnityExplorer 在游戏世界内初始化）';
  if (cat === 'game_control') {
    if (isGabpMirrorName(name)) return '需 RimBridgeServer 连接（GABP 镜像）';
    return '需进入地图且 RIMAPI 模组就绪';
  }
  return '无需前置';
}

// 当前是否可直接调用（mono 工具取决于桥接是否就绪；其余按 toolConfig）
function toolEnabledNow(name) {
  if (MONO_DEBUG_TOOL_NAMES.has(name)) return monoTools.some(t => t.name === name);
  return isToolEnabled(name);
}

// 全部工具条目（mono 工具在桥接未就绪时用占位条目，保证概览覆盖全量；GABP 镜像仅在连接后非空）
function allToolEntries() {
  const entries = [...TOOLS, ...AGG_META_TOOLS, ...gabpTools];
  for (const m of MONO_DEBUG_TOOL_NAMES) {
    const found = monoTools.find(t => t.name === m);
    entries.push(found || { name: m, description: '[mono调试] mono 调试工具（桥接未就绪，仅提供名称）', inputSchema: { type: 'object', properties: {} } });
  }
  return entries;
}

// mcp_help 核心逻辑：无参=一级概览（多级菜单根），带 path=逐级深入，带 tool=详情
function mcpHelpResult(args) {
  const target = args && args.tool;
  const entries = allToolEntries();
  const usageHintFor = (name) => `该工具在 toolConfig.json 中禁用，需通过 agg_call_tool 调用（tool: ${name}）`;

  // tool → 单个工具详情（回归点：与改造前一致，category 现为 game_control/bridge）
  if (target) {
    const meta = entries.find(t => t.name === target);
    if (!meta) {
      return { content: [{ type: 'text', text: JSON.stringify({ success: false, error: `未知工具: ${target}`, hint: `共 ${entries.length} 个工具；先无参调用 mcp_help 查看一级概览；工具名支持 rimworld./rimbridge./rigworld. 前缀别名` }, null, 2) }], isError: true };
    }
    const enabled = toolEnabledNow(target);
    const detail = {
      name: meta.name,
      category: toolCategory(meta.name),
      enabled,
      description: meta.description,
      inputSchema: meta.inputSchema,
      precondition: toolPrecondition(meta.name),
      ...(TOOL_USAGE_NOTES[meta.name] || {})
    };
    if (!enabled) detail.usageHint = usageHintFor(target);
    return { content: [{ type: 'text', text: JSON.stringify(detail, null, 2) }] };
  }

  // path → 多级菜单（resolveMenuPath + buildMenuNode）
  const path = args && args.path;
  if (path) {
    const node = resolveMenuPath(path);
    if (node.error) {
      return { content: [{ type: 'text', text: JSON.stringify({ success: false, error: node.error }, null, 2) }], isError: true };
    }
    return buildMenuNode(node);
  }

  // 无参 → 一级概览（6 类别 + bridge/game_control 子组计数）
  const categoryDesc = {
    system: '进程控制与日志',
    unityexplorer: 'UE 检视 / Hook / C# 控制台',
    mono: 'mono 软调试（SDB）',
    meta: '聚合入口与帮助',
    bridge: 'RimBridge 桥接基础（Lua/脚本/操作/等待）',
    game_control: '游戏内控制（状态/地图交互/UI/架构/Mod 等）'
  };
  const categories = MENU_CATEGORIES.map(c => {
    const count = (c === 'bridge') ? subgroupToolCount('bridge')
      : (c === 'game_control') ? subgroupToolCount('game_control/game')
      : entries.filter(t => toolCategory(t.name) === c).length;
    return { path: c, name: c, count, desc: categoryDesc[c] || '' };
  });
  const total = categories.reduce((a, c) => a + c.count, 0);
  const bridge = {};
  for (const s of MENU_BRIDGE_SUBGROUPS) bridge[s] = subgroupToolCount(`bridge/${s}`);
  const gameSubgroups = MENU_GAME_CONTROL_SUBGROUPS.game.map(s => ({ name: s, count: subgroupToolCount(`game_control/game/${s}`) }));
  const overview = {
    total,
    level: 'root',
    note: '多级菜单：无参=一级概览；传 path 逐级深入（如 path:"bridge/lua_script"、path:"game_control/game/gameplay/camera"）；传 tool 返回单个工具详情；enabled=false 的工具需经 agg_call_tool 调用；工具名可带 rimworld./rimbridge./rigworld. 前缀（原生工具自动剥前缀路由，GABP 镜像名优先）',
    categories,
    bridge,
    game_control: { game: gameSubgroups.reduce((a, s) => a + s.count, 0), gameSubgroups }
  };
  return { content: [{ type: 'text', text: JSON.stringify(overview, null, 2) }] };
}

function getEnabledTools() {
  return [...TOOLS, ...monoTools, ...AGG_META_TOOLS, ...gabpTools].filter(t => isToolEnabled(t.name));
}

// 全部工具名（含未直接暴露的）：用于 agg_call_tool 存在性校验（禁用 ≠ 不存在，仍可经聚合器调用）
// GABP 镜像工具名动态并入（连接后非空，spec §6.2）
function allToolNames() {
  return new Set([
    ...TOOLS.map(t => t.name),
    ...MONO_DEBUG_TOOL_NAMES,
    ...AGG_META_TOOLS.map(t => t.name),
    ...gabpTools.map(t => t.name)
  ]);
}

// 原生工具前缀别名归一化（spec §8 / P3）：调用方可能带 rimworld./rimbridge./rigworld. 等前缀，
// 去前缀后命中原生工具名的调用剥前缀走原生路由。GABP 镜像名（rimworld.*/rimbridge.*）优先按原名走镜像，
// 只有镜像清单中确实存在（RBS 真实暴露）才保留原名，避免镜像与原生重名时误剥。
const TOOL_NAME_PREFIXES = ['rigworld.', 'rimworld.', 'rimbridge.'];

// §8 去重镜像反向别名（tool-dup-cleanup / tool-cleanup）：去重镜像名不再暴露，但调用方可能仍用带前缀旧名调用；
// 经此表映射回原生名，保证旧调用方式安全可达（直接调用与 agg_call_tool 均先经 resolveToolAlias）。
// 注意：rimworld.search_debug_actions 镜像名与原生名相同，经前缀剥离即可命中；search_debug_actions 已恢复为 UE 原后端，
// 经此反向别名映射回原生名后走 UE 实现（而非 GABP），保证旧调用方式安全可达。
// tool-cleanup 收窄：其余 14 个旧镜像（list_colonists / list_mods / get_game_info / save_game / load_game /
// set_time_speed / clear_selection / list_debug_action_roots / list_debug_action_children / get_debug_action /
// jump_camera_to_cell / execute_debug_action / set_camera_zoom / select_pawn）对应原生工具已完全清除，
// 不再需要反向别名（原生工具不存在，映射无意义；其调用会走 GABP_REMOVED_TOOL_NAMES 拦截或「未知镜像」报错）。
const DEDUP_MIRROR_TO_NATIVE = {
  'rimworld.search_debug_actions': 'search_debug_actions'
};

function resolveToolAlias(name) {
  if (typeof name !== 'string') return name;
  // GABP 镜像优先：原名在镜像清单中（RBS 真实暴露）→ 不剥前缀，保持镜像路由
  if (isGabpMirrorName(name) && gabpToolNames().has(name)) return name;
  // 去重镜像反向别名：镜像已去重（不在 gabpTools，RBS 不暴露）但调用方仍用带前缀旧名 → 映射回原生名
  if (isGabpMirrorName(name)) {
    const native = DEDUP_MIRROR_TO_NATIVE[name];
    if (native) {
      log('debug', `tool dedup mirror alias: ${name} → ${native}`);
      return native;
    }
  }
  // 移除集拦截（tool-cleanup）：rimbridge.get_bridge_status / rimbridge.ping / rimworld.get_game_info
  // 命中即返回原名 → 不剥前缀、不走原生路由，后续镜像检查报「未知 GABP 镜像工具」。
  // 位置：镜像优先 + 反向别名之后、前缀剥离之前（防止 rimworld.get_game_info 剥前缀误路由到原生 get_game_info）。
  if (GABP_REMOVED_TOOL_NAMES.has(name)) return name;
  const lower = name.toLowerCase();
  for (const prefix of TOOL_NAME_PREFIXES) {
    if (lower.startsWith(prefix)) {
      const stripped = name.slice(prefix.length);
      if (allToolNames().has(stripped)) {
        log('debug', `tool name prefix alias: ${name} → ${stripped}`);
        return stripped;
      }
    }
  }
  return name;
}

// 参数别名归一化：调用方可能使用常见同义词（keyword/type_name 等），统一映射到 schema 标准参数名，
// 避免因参数名不一致导致 RIMAPI/UE 侧 "Required parameter xxx not found"。
const TOOL_ARG_ALIASES = {
  // DebugAction / MapStructure 搜索类：keyword → query
  search_debug_actions: { keyword: 'query' },
  search_map_structure: { keyword: 'query' },
  // mono find_types / find_methods：keyword → query
  find_types: { keyword: 'query' },
  find_methods: { keyword: 'query' },
  // UE 类型检查：type_name → typeName
  inspect_type: { type_name: 'typeName', typeName_simple: 'typeName' },
  // UE Hook：targetType/targetMethod → typeName/methodName，hookType → patchType
  create_hook: { targetType: 'typeName', target_type: 'typeName', targetMethod: 'methodName', target_method: 'methodName', hookType: 'patchType', hook_type: 'patchType', patch_code: 'patchCode' },
  toggle_hook: { hook_id: 'hookId' },
  delete_hook: { hook_id: 'hookId' },
  // 事件触发：incident → incident_def_name，mapId → target_map_id
  post_incident_execute: { incident: 'incident_def_name', mapId: 'target_map_id', map_id: 'target_map_id' },
  // 区域指令：order → type
  post_order_designate: { order: 'type' },
  // UI 消息：message → text
  post_ui_message: { message: 'text' },
  post_ui_dialog: { message: 'text' },
  // 开发者控制台：console → action，cmd → message
  post_dev_console: { console: 'action', command: 'action', cmd: 'message', content: 'message' },
  // C# 控制台：script → code
  execute_csharp_code: { script: 'code', expression: 'code' },
  // 单个殖民者详情：RIMAPI 端点参数为 id（测试中误用 pawn_id 踩坑）
  get_colonist_detailed: { pawn_id: 'id', pawnId: 'id', colonist_id: 'id', colonistId: 'id' },
  // rimworld.select_pawn 镜像（实机测试发现）：RBS select_pawn 的 schema 为 pawnName/pawnId/append（无 type），
  // 兼容旧 post_select 签名 {name,type} / {id}：name→pawnName、id→pawnId（type=pawn 注入对 RBS 无效但无害）
  'rimworld.select_pawn': { name: 'pawnName', id: 'pawnId' },
  // 地图内查询：RIMAPI 端点参数为 map_id（camelCase / 大写 ID 均归一）
  get_map_things: { mapId: 'map_id', mapID: 'map_id' },
  get_map_plants: { mapId: 'map_id', mapID: 'map_id' },
  get_map_weather: { mapId: 'map_id', mapID: 'map_id' },
  get_map_animals: { mapId: 'map_id', mapID: 'map_id' },
  // C# using：using → namespace
  add_using_directive: { using: 'namespace', ns: 'namespace' }
};

function normalizeToolArgs(name, args) {
  if (!args || typeof args !== 'object') return args || {};
  const out = { ...args };

  // 1. 参数名别名：keyword→query、type_name→typeName、pawn_id→id 等（详见 TOOL_ARG_ALIASES）
  const aliases = TOOL_ARG_ALIASES[name];
  if (aliases) {
    for (const [alias, canonical] of Object.entries(aliases)) {
      if (out[alias] !== undefined && out[canonical] === undefined) {
        out[canonical] = out[alias];
      }
    }
  }

  // 2.5. RIMAPI 数字 id 提取：get_colonist_detailed 的 id 可能是 "Thing_Human737" 形式
  // （pawnId 别名已在步骤 1 归一为 id），提取末尾数字作为 RIMAPI 数字 id；不含数字则保留原值。
  if (name === 'get_colonist_detailed' && typeof out.id === 'string') {
    const m = out.id.match(/\d+/);
    if (m) {
      log('debug', `${name}: id "${out.id}" → numeric ${Number(m[0])}`);
      out.id = Number(m[0]);
    }
  }

  // 3. 路径类参数反斜杠归一化：调用方 JSON 可能传 \\（双反斜杠），服务端只认单反斜杠
  if (name === 'get_map_structure') {
    for (const key of ['path', 'parentPath', 'parent_path']) {
      if (typeof out[key] === 'string') {
        out[key] = out[key].replace(/\\\\/g, '\\');
      }
    }
  }

  return out;
}

// ========== 失败响应自动附加调用范式（usage-hint-on-failure） ==========
// 工具调用失败时，错误响应自动附带正确调用范式（pattern/example/params/aliases/precondition）；
// 未知工具名附带 top-3 相似工具建议（含各自调用范式）。所有辅助函数均须健壮（绝不抛异常），
// 由 CallToolRequestSchema 的失败路径统一调用。

// 查找工具条目：name → { name, inputSchema, description, ... }（检索 TOOLS / monoTools / gabpTools /
// AGG_META_TOOLS，再兜底 allToolEntries()——含 mono 未就绪时的占位条目）；未找到返回 null。
function toolSchema(name) {
  if (typeof name !== 'string' || !name) return null;
  for (const arr of [TOOLS, monoTools, gabpTools, AGG_META_TOOLS]) {
    const found = (arr || []).find(t => t && t.name === name);
    if (found) return found;
  }
  const all = allToolEntries();
  return (all || []).find(t => t && t.name === name) || null;
}

// 按 prop 类型/default/enum 构造示例参数骨架值（供调用范式示例使用）
function _skeletonValue(prop) {
  if (!prop || typeof prop !== 'object') return '<value>';
  if (prop.default !== undefined) return prop.default;
  switch (prop.type) {
    case 'string': return (Array.isArray(prop.enum) && prop.enum.length > 0) ? prop.enum[0] : '<string>';
    case 'number':
    case 'integer': return 0;
    case 'boolean': return true;
    case 'object': return {};
    case 'array': return [];
    default: return (Array.isArray(prop.enum) && prop.enum.length > 0) ? prop.enum[0] : '<value>';
  }
}

// 构建原始 usage 数据（不含 pattern 字符串）：schema / 示例参数 / 参数说明 / 别名 / 前置条件 / 启用状态
function _buildUsageCore(name) {
  const schema = toolSchema(name);
  const props = (schema && schema.inputSchema && schema.inputSchema.properties) || {};
  const required = (schema && schema.inputSchema && Array.isArray(schema.inputSchema.required))
    ? schema.inputSchema.required : [];
  const exampleObj = {};
  const params = {};
  for (const key of Object.keys(props)) {
    const prop = props[key] || {};
    exampleObj[key] = _skeletonValue(prop);
    params[key] = { type: prop.type || 'any', required: required.includes(key) };
  }
  // 参数别名：TOOL_ARG_ALIASES 存储的 { 别名: 规范名 } 映射原样收录
  const aliases = {};
  const aliasMap = TOOL_ARG_ALIASES[name];
  if (aliasMap) {
    for (const [alias, canonical] of Object.entries(aliasMap)) {
      aliases[alias] = canonical;
    }
  }
  return {
    schema,
    exampleObj,
    params,
    aliases,
    precondition: toolPrecondition(name) || '',
    enabled: isToolEnabled(name) !== false
  };
}

// 生成可复制粘贴的调用范式字符串（JSON 压缩为单行）：
// 有 schema → agg_call_tool 聚合范式（禁用/隐藏工具同样可经 agg_call_tool 调用，始终可用）；
// 无 schema → 回退 TOOL_USAGE_NOTES 示例，再兜底空参数聚合范式。
function formatCallPattern(name) {
  const core = _buildUsageCore(name);
  if (core.schema) {
    return `agg_call_tool { tool: "${name}", args: ${JSON.stringify(core.exampleObj)} }`;
  }
  const note = TOOL_USAGE_NOTES[name];
  if (note && typeof note.example === 'string') return note.example;
  return `agg_call_tool { tool: "${name}", args: {} }`;
}

// 构建完整 usage 提示对象 { pattern, example, params, aliases, precondition }；name 非法时返回 null
function buildUsageHint(name) {
  if (!name || typeof name !== 'string') return null;
  const core = _buildUsageCore(name);
  const note = TOOL_USAGE_NOTES[name];
  return {
    pattern: formatCallPattern(name),
    example: (note && typeof note.example === 'string') ? note.example : JSON.stringify(core.exampleObj),
    params: core.params,
    aliases: core.aliases,
    precondition: core.precondition
  };
}

// 双字母组 Dice 系数（大小写不敏感，0~1）：衡量两个名称的字符序列相似度
function _bigramDice(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string') return 0;
  const la = a.toLowerCase();
  const lb = b.toLowerCase();
  const grams = (s) => {
    const set = new Set();
    for (let i = 0; i < s.length - 1; i++) set.add(s.slice(i, i + 2));
    return set;
  };
  const ga = grams(la);
  const gb = grams(lb);
  if (ga.size === 0 && gb.size === 0) return la === lb ? 1 : 0;
  let inter = 0;
  for (const g of ga) if (gb.has(g)) inter++;
  return (2 * inter) / (ga.size + gb.size);
}

// 相似工具建议：全名大小写不敏感精确匹配 +100、子串包含 +70、双字母组 Dice 系数 +0~100，
// 另对 resolveToolAlias 能解析到的真实工具名加权；排除输入自身，按分降序取前 limit 个。
// 返回 [{ name, pattern }]；空输入返回 []。
function suggestTools(unknownName, limit = 3) {
  if (!unknownName || typeof unknownName !== 'string') return [];
  const lower = unknownName.toLowerCase();
  const scored = [];
  for (const cand of allToolNames()) {
    if (typeof cand !== 'string') continue;
    const cl = cand.toLowerCase();
    if (cl === lower) continue; // 排除输入自身（大小写不敏感）
    let score = 0;
    if (cl === lower) score += 100; // 精确匹配（防御性保留，正常已被排除）
    if (cl.includes(lower) || lower.includes(cl)) score += 70; // 子串包含
    score += _bigramDice(cl, lower) * 100; // 双字母组 Dice 系数
    scored.push({ name: cand, score });
  }
  // 别名解析增强：输入能经 resolveToolAlias 映射为真实工具名（前缀剥离/反向别名）→ 加权
  const resolved = resolveToolAlias(unknownName);
  if (resolved !== unknownName && allToolNames().has(resolved)) {
    const exist = scored.find(s => s.name === resolved);
    if (exist) exist.score += 30;
    else scored.push({ name: resolved, score: 30 });
  }
  scored.sort((a, b) => b.score - a.score);
  return scored.slice(0, limit).map(s => ({ name: s.name, pattern: formatCallPattern(s.name) }));
}

// 失败响应附加 usage/相似工具建议（仅 isError === true 时处理；内部任何异常一律原样返回原 result）：
// JSON 错误体 → 注入 suggestions/usage（含缺必需参数时的 usage.fix）；纯文本错误 → 追加相似工具与正确调用范式。
function attachUsageOnFailure(name, args, result) {
  try {
    if (!result) return result;
    const text = result.content && result.content[0] && result.content[0].text;
    if (typeof text !== 'string') return result;
    // 触发条件：协议级错误（isError）或 JSON 业务错误体（success === false，如阶段门禁 stageError）
    if (result.isError !== true) {
      let bizFail = false;
      try {
        const p = JSON.parse(text);
        bizFail = !!(p && typeof p === 'object' && !Array.isArray(p) && p.success === false);
      } catch (e) { bizFail = false; }
      if (!bizFail) return result;
    }

    // 确定内部目标工具名：agg_call_tool → args.tool；mcp_help → args.tool || args.path；其余 → name 自身
    let targetName = name;
    if (name === 'agg_call_tool') targetName = (args && args.tool) || name;
    else if (name === 'mcp_help') targetName = (args && (args.tool || args.path)) || name;

    // 未知工具检测（中英双语 + GABP 镜像工具）
    const unknownRe = /未知工具[:：]?\s*([^\s，。,；;（("]+)|Unknown tool[:：]?\s*([^\s"'，。；;（(]+)|未知 GABP 镜像工具[:：]?\s*([^\s，。,；;（("]+)/i;
    const m = text.match(unknownRe);
    const unknownName = m ? (m[1] || m[2] || m[3]) : null;

    // 缺必需参数检测：RIMAPI 侧 "Required parameter xxx not found" / 本地 "缺少参数 xxx"
    const missingRe = /Required parameter (\w+) not found/i;
    const missingRe2 = /缺少参数 (\w+)/;
    const mm = text.match(missingRe) || text.match(missingRe2);
    const missingParam = mm ? mm[1] : null;

    let parsed = null;
    try { parsed = JSON.parse(text); } catch (e) { parsed = null; }

    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
      // JSON 错误体：未知工具 → 附加 suggestions + 首选建议的 usage；已知工具失败 → 附加 usage
      if (unknownName) {
        parsed.suggestions = suggestTools(unknownName);
        if (Array.isArray(parsed.suggestions) && parsed.suggestions.length > 0 && !parsed.usage) {
          parsed.usage = buildUsageHint(parsed.suggestions[0].name);
        }
      } else {
        const hint = buildUsageHint(targetName);
        if (hint) parsed.usage = hint;
      }
      // 缺必需参数 → usage 内附加 fix（missing 参数 + 期望参数名列表）
      if (missingParam && parsed.usage && parsed.usage.params) {
        parsed.usage.fix = { missing: missingParam, expected: Object.keys(parsed.usage.params) };
      }
      result.content[0].text = JSON.stringify(parsed, null, 2);
    } else {
      // 纯文本错误：未知工具 → 追加相似工具 + 首选范式；已知工具失败 → 追加正确调用范式
      if (unknownName) {
        const sug = suggestTools(unknownName);
        if (sug.length > 0) {
          result.content[0].text = `${text}\n相似工具: ${sug.map(s => s.name).join('、')}\n正确调用范式: ${sug[0].pattern}`;
        }
      } else {
        const hint = buildUsageHint(targetName);
        if (hint) result.content[0].text = `${text}\n\n正确调用范式: ${hint.pattern}`;
      }
    }
    if (result.content[0].text !== text) {
      log('debug', `attachUsageOnFailure: ${name} 失败响应已附加调用范式/建议`);
    }
    return result;
  } catch (e) {
    return result;
  }
}

// Tool handler function
async function handleToolCall(name, args) {
  const originalName = name;
  // 原生工具前缀别名归一化（P3）：rimworld./rimbridge./rigworld. 前缀 + 原生名 → 剥前缀走原生路由
  const resolvedName = resolveToolAlias(name);
  if (resolvedName !== name) {
    log('debug', `handleToolCall: tool alias ${name} → ${resolvedName}`);
    name = resolvedName;
  }
  // 参数别名归一化：keyword→query、type_name→typeName 等（详见 TOOL_ARG_ALIASES）
  args = normalizeToolArgs(name, args);
  // 修复 1（fix-debug-server-defects，缺口 B）：rimworld.select_pawn 是 GABP 镜像工具（RBS select_pawn），
  // 其语义固定为"选择殖民者"；调用方可能不带 type，此时注入 type='pawn'，保证镜像调用带完整语义
  // （tool-cleanup 后 post_select 原生工具已清除，select_pawn 仅经 GABP 镜像通道可达）。
  if (originalName === 'rimworld.select_pawn' && args.type === undefined) {
    log('debug', 'rimworld.select_pawn 缺 type，注入 type=pawn');
    args.type = 'pawn';
  }

  // mono 调试工具 → 转发 McpRimDebug
  if (MONO_DEBUG_TOOL_NAMES.has(name)) {
    return callMonoTool(name, args);
  }

  // GABP 路由（spec §6.3）：镜像工具 → 直接转发 gabpBridge.callTool；
  // tool-cleanup：RBS_ROUTE 原生路由表已整体删除（13 个旧原生工具完全清除），不再有条件转发路径
  if (isGabpMirrorName(name)) {
    // Area 写操作改走自研实现（UEAreaActions）：屏蔽 RimBridgeServer 的 delete_area/clear_area
    // （其在读线程改主线程状态导致连接僵死），改调 UELoader 手搓 /area/delete、/area/clear，
    // 经 UEMainThreadDispatcher 主线程安全执行，且不依赖 GABP 连接（断开也能用）。
    if (name === 'rimworld.delete_area' || name === 'rimworld.clear_area') {
      const areaId = args && (args.areaId ?? args.id ?? args.areaIdOrAlias);
      if (!areaId) {
        return contentErrorResponse('MISSING_PARAMETER', `缺少参数 areaId。示例：agg_call_tool { tool: "${name}", args: {"areaId":"Area_6_Named_活动区 2"} }`);
      }
      const endpoint = name === 'rimworld.delete_area' ? '/area/delete' : '/area/clear';
      return await callAreaAction(endpoint, areaId, name);
    }
    if (!gabpBridge || !gabpBridge.isConnected()) {
      return gabpErrorResponse(`GABP 未连接（state=${gabpBridge ? gabpBridge.state : 'disabled'}），镜像工具 ${name} 需 RimBridgeServer 连接`);
    }
    if (!gabpToolNames().has(name)) {
      return gabpErrorResponse(`未知 GABP 镜像工具: ${name}（可用镜像见 agg_list_tools / mcp_help）`);
    }
    // 修复 2（fix-debug-server-defects）：start_debug_game_ready 已加载幂等短路——
    // 殖民地已加载（playable=true）时直接返回 ready，不转发 RimBridge（避免 30s 超时）。
    // 判定源：get_game_info（status=game_loaded / mapCount>0 / playable=true 任一成立即已加载）。
    // 探测失败时保守走原转发（不阻断真实启动流程）。
    if (name === 'rimworld.start_debug_game_ready') {
      const probe = await gabpBridge.callTool('rimworld/get_game_info', {});
      const info = probe && probe.ok ? probe.result : null;
      const loaded = !!(info && (info.status === 'game_loaded' || info.playable === true || (info.mapCount != null && info.mapCount > 0)));
      if (loaded) {
        log('INFO', `start_debug_game_ready 短路：游戏已加载（status=${info.status} mapCount=${info.mapCount}），直接返回 ready`);
        return { content: [{ type: 'text', text: JSON.stringify(wrapGABPResult({ ready: true, alreadyLoaded: true, status: info.status, mapCount: info.mapCount, ticksGame: info.ticksGame, source: 'idempotent-shortcut' }, true), null, 2) }] };
      }
      log('INFO', `start_debug_game_ready 未短路（status=${info ? info.status : 'probe-failed'}），继续转发 RimBridge`);
    }
    // play_for 主线程错误分类（检查表缺陷 1）：RimBridge 端 get_realtimeSinceStartup 仅主线程可调。
    if (name === 'rimworld.play_for') {
      const r = await callGABPTool('rimworld/play_for', args);
      const text = JSON.stringify(r);
      if (/get_realtimeSinceStartup can only be called from the main thread/.test(text)) {
        return contentErrorResponse('PLAY_FOR_MAIN_THREAD',
          'rimworld.play_for 当前不可用：RimBridge 端调用 Unity get_realtimeSinceStartup 需在主线程执行（C# 端缺陷）。建议改用 step_game_ticks 逐帧推进，或等待 RimBridge mod 修复。');
      }
      return r;
    }
    // area 一致性标注（检查表缺陷 3）：create 后 RimBridge list_areas 可能延迟。
    if (name === 'rimworld.create_allowed_area') {
      const r = await callGABPTool('rimworld/create_allowed_area', args);
      // 把 listSyncNote 写回 content text（修复：此前只改局部 parsed 未回写，调用方看不到）
      if (r && r.content && r.content[0] && typeof r.content[0].text === 'string') {
        try {
          const parsed = JSON.parse(r.content[0].text);
          if (parsed && parsed.success) {
            parsed.listSyncNote = '已建区域；RimBridge 的 list_areas 可能延迟/需刷新才显示，如需确认可用 selectedAllowedArea 或重新 list_areas。';
            r.content[0].text = JSON.stringify(parsed, null, 2);
          }
        } catch (e) { /* JSON 解析失败则保持原样 */ }
      }
      return r;
    }
    // 删除超时健康探测（仅遗留 delete_zone；delete_area/clear_area 已改走自研实现）。
    if (name === 'rimworld.delete_zone') {
      const r = await callGABPTool(name.replace(/\./g, '/'), args);
      if (isGabpErrorTimeout(r)) {
        return contentErrorResponse('AREA_OP_CHANNEL_BUSY',
          '删除操作 GABP 超时；连接可能繁忙。请用 list_areas/list_zones 确认当前状态后重试，勿重复并发删除。');
      }
      return r;
    }
    // 镜像名 → RBS 原名：rimworld.dpa_status → rimworld/dpa_status（与 toMCPTool 的 /→. 互逆）
    // P1-MCP-3.1：load_game / save_game / execute_debug_action 属慢操作（载入/保存序列化、在游戏线程执行调试动作），
    // 传 {slow:true} → gabpClient 慢超时（requestTimeoutMs * 2）。
    const _rbs = name.replace(/\./g, '/');
    const _slow = /^(rimworld|rimbridge)\.(load_game|save_game|execute_debug_action)$/.test(name);
    return callGABPTool(_rbs, args, undefined, _slow ? { slow: true } : null);
  }

  switch (name) {
    case 'start_game': {
      const timeout = args?.timeout ?? config.startTimeout ?? 180000;
      const result = await startGame(args?.useSteam ?? true, args?.waitForNotification ?? true, timeout);
      if (result && result.success) {
        // 成功路径末尾：触发 GABP 后台轮询发现+连接（spec §6.1）
        scheduleDiscoverAndConnect();
      }
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'stop_game': {
      const result = await stopGame(args?.force ?? false);
      // 停止 GABP 发现/连接轮询并断开（spec §6.1）；游戏进程退出后 socket RST/close 由 gabpClient 处理
      stopGabpPolling();
      if (gabpBridge) {
        gabpBridge.disconnect();
      }
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'get_game_status': {
      const result = await getGameStatus();
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'read_rimworld_log': {
      const result = await readLog(args?.lines ?? 100);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'tail_rimworld_log': {
      const result = await tailLog(args?.lines ?? 50);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'get_game_info': {
      const result = await getGameInfo();
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'start_quick_test': {
      const timeout = args?.timeout ?? (config.timings?.quickTestTimeoutMs ?? 120000);
      const result = await startQuickTest(timeout);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    // UnityExplorer 工具（Task 2.1：统一接入 requireStage(['IN_GAME'])）
    case 'inspect_type': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/inspect/type', 'POST', {
          typeName: args.typeName
        });
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'get_unityexplorer_status': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/status', 'GET');
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'clear_unityexplorer_logs': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/log/clear', 'POST');
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'get_unityexplorer_logs': {
      const count = Math.min(Math.max(1, args?.count ?? 50), 200);
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI(`/unityexplorer/log?count=${count}`, 'GET');
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'create_hook': {
      // 黑名单：禁止对日志/递归高风险方法创建 Hook（曾致 Log.Message patch 递归刷 63MB 日志 + mono 原生崩溃）
      const HOOK_BLACKLIST_METHODS = /^(Message|Error|Warning|ErrorOnce|WarningOnce|Clear|Log|Assert|LogWarning|LogError|LogMessage)$/;
      const typeName = String(args.typeName || '');
      const methodName = String(args.methodName || '');
      // 日志类判定：typeName 为 Verse.Log / UnityEngine.Debug 等日志类型（以 .Log 或 .Debug 结尾）
      const logTypeHit = /(^|\.)(Log|Debug)$/.test(typeName);
      if (logTypeHit && HOOK_BLACKLIST_METHODS.test(methodName)) {
        return {
          content: [{ type: 'text', text: JSON.stringify({ success: false, errorCode: 'HOOK_BLACKLISTED', message: `禁止对日志方法创建 Hook：${typeName}.${methodName} 命中 Hook 黑名单。原因：日志方法（如 Verse.Log.Message）被全游戏高频调用，且 UnityExplorer/UEHookStore 的 patch 执行内部又会调用日志，hook 后会产生递归日志风暴——曾实测 1 分钟内刷出 63.5MB（约 2.9 万次 Log.Message_Patch2）并触发 mono 原生崩溃（Native Crash）。请改对普通实例方法创建 Hook。`, guidance: '可用 find_methods / inspect_type 挑选安全方法（如 Verse.TickManager 的普通实例方法），或改 Hook 目标为非日志类。', hint: '若确实需要拦截日志输出，建议用 execute_csharp_code 直接 Patch 或拦截，而非 create_hook。' }, null, 2) }],
          isError: true
        };
      }
      const result = await requireStage([STAGES.IN_GAME], async () => {
        const r = await callUnityExplorerAPI('/unityexplorer/hook/create', 'POST', {
          typeName: args.typeName,
          methodName: args.methodName,
          patchType: args.patchType || 'Postfix',
          patchCode: args.patchCode
        }, { slow: true });  // P1-MCP-3.1：Hook 创建需主线程编译/插入 patch，属慢操作
        // 测试踩坑：RimWorld.Pawn 会报 TYPE_NOT_FOUND，类型须用 Verse.Pawn（程序集命名空间）
        if (!r.success && r.errorCode === 'TYPE_NOT_FOUND' && typeof r.error === 'string' && r.error.includes('not found')) {
          r.guidance = '类型未找到：请使用完整命名空间类型名。例如 Verse.Pawn（不是 RimWorld.Pawn），方法如 get_Label、Tick。可用 inspect_type 或 find_types 先确认正确类型名。';
        }
        if (!r.success && r.errorCode === 'HOOK_CREATE_ERROR' && typeof r.error === 'string' && r.error.includes('Main thread operation timed out')) {
          r.guidance = 'Hook 创建在主线程超时：请换普通实例方法（避免 UnityEngine 高频调用如 Log.Message），或稍后重试。';
        }
        if (!r.success && r.errorCode === 'METHOD_NOT_FOUND') {
          r.suggestions = '方法名须精确（含大小写）；重载需附参数签名。可用 find_methods 搜索确认正确方法名。';
        }
        return r;
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'toggle_hook': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/hook/toggle', 'POST', {
          hookId: args.hookId,
          enabled: args.enabled
        });
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'delete_hook': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/hook', 'DELETE', {
          hookId: args.hookId
        });
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'list_hooks': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/hook/list', 'GET');
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    // C# Console 工具
    case 'execute_csharp_code': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        const r = await callUnityExplorerAPI('/unityexplorer/console/execute', 'POST', {
          code: args.code
        }, { slow: true });  // P1-MCP-3.1：编译/执行大段 C# 属慢操作，慢超时（正常 2 倍）
        // 测试踩坑：控制台默认无 using 引用，GenTicks 等裸类型名会报 CS0103，须用全限定名或先 add_using_directive
        if (!r.success && (r.errorCode === 'COMPILATION_ERROR' || (typeof r.error === 'string' && r.error.includes('CS0')))) {
          r.guidance = '编译失败：C# 控制台默认没有 using 引用。请使用全限定类型名（如 Verse.GenTicks.TicksGame），或先用 add_using_directive 添加命名空间（如 Verse）。';
        }
        return r;
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'reset_csharp_console': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/console/reset', 'POST');
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'add_using_directive': {
      const result = await requireStage([STAGES.IN_GAME], async () => {
        return callUnityExplorerAPI('/unityexplorer/console/addusing', 'POST', {
          namespace: args.namespace
        });
      })(args);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }
    // ===== UE 工具 case 结束 =====

    // RIMAPI 工具 - 流控制（RIMAPI 端点路径为 /api/v1/stream/*，404 时附加 guidance；
    // tool-cleanup：post_camera_change_zoom / post_camera_change_position 已删除，相机控制走 GABP 镜像）
    case 'post_stream_start': {
      const result = await requireMapLoaded(async () => {
        const start = await _rimapiRequest('POST', '/api/v1/camera/stream/start', null, {}, 30, true);
        if (!_isRimapiSuccess(start)) {
          const notFound = start._http_status === 404 ? { guidance: 'RIMAPI 流端点 /api/v1/camera/stream/start 未找到；请确认 RIMAPI v1.10.0 端点路径' } : {};
          return { success: false, data: start.data || start, error: start.error, ...notFound };
        }
        // 权威验证流是否真正拉起：RIMAPI 用 SnakeCaseContractResolver，StreamStatusDto.IsStreaming 序列化为 is_streaming。
        const status = await _rimapiRequest('GET', '/api/v1/camera/stream/status', null, null, (config.timings?.rimapiProbeTimeoutMs ?? 5000) / 1000);
        const sd = status && (status.data || status.result || status);
        const isStreaming = !!(sd && (sd.is_streaming === true || sd.IsStreaming === true));
        if (!isStreaming) {
          return { success: false, errorCode: 'STREAM_NOT_READY', message: '流未真正拉起：RIMAPI stream/status 返回 is_streaming=false。请先 post_stream_setup 配置 ip/port/frame_width/frame_height/fps/quality，或检查 RIMAPI UdpCameraStream。' };
        }
        const base = { isStreaming: true, status: status.data || status };
        // 可选：输出图片流（output_frames=true 时随流抓帧，流式写盘到 out_dir 或默认目录）
        if (args.output_frames === true) {
          // 从 setup 配置取 UDP 推流端口（status 可含 config.port），缺省 5007
          const conf = (sd && (sd.config || sd.Config)) || {};
          const port = Number(conf.port || 5007);
          const dir = args.out_dir || STREAM_CAPTURE_DEFAULT_DIR;
          const started = _startStreamCapture({ port, dir });
          if (started.error) {
            // overwriteRejected 便于客户端辨识：已有抓帧会话运行中被拒，而非启动失败（P0-MCP-3）
            return { success: false, data: base, error: started.error, overwriteRejected: true };
          }
          base.capture = {
            enabled: true,
            dir: started.dir,
            port,
            note: '抓帧子进程已启动；将逐帧流式写入目录，调用 post_stream_stop 停止并产出结果'
          };
        }
        return { success: true, data: base };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_stream_stop': {
      const streamGuidance = { guidance: 'RIMAPI 视频流端点调用失败；若为 404 请确认 RIMAPI v1.10.0 端点路径 /api/v1/camera/stream/stop' };
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/camera/stream/stop', null, {}, 30, true);
        const base = {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error,
          ...(response._http_status === 404 ? streamGuidance : {})
        };
        // 联动停止抓帧：若之前 post_stream_start 开了 output_frames，写停止标记并等待子进程收尾
        if (activeStreamCapture) {
          const cap = await _stopStreamCapture(config.timings?.streamStopWaitMs ?? 5000);
          base.data = { ...(base.data || {}), captureStopped: true, capture: cap };
        }
        return base;
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_stream_setup': {
      const result = await requireMapLoaded(async () => {
        // RIMAPI 用 SnakeCaseContractResolver 反序列化 StreamConfigDto body，须用 snake_case 字段
        const body = {
          address: args.ip ?? '127.0.0.1',
          port: args.port ?? 5007,
          frame_width: args.frame_width ?? 1280,
          frame_height: args.frame_height ?? 720,
          target_fps: args.fps ?? 15,
          jpeg_quality: args.quality ?? 50
        };
        const response = await _rimapiRequest('POST', '/api/v1/camera/stream/setup', null, body, 30, true);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // RIMAPI 工具 - 查询类 (GET)
    // tool-cleanup：get_game_state / get_colonists 已删除，游戏状态见 get_game_info / get_game_status，
    // 殖民者信息经 get_colonist_detailed / get_colonists_detailed（RIMAPI）或 GABP 镜像获取
    case 'get_maps': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('GET', '/api/v1/maps');
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_map_things': {
      const result = await requireMapLoaded(async () => {
        const query = args.map_id !== undefined ? { map_id: args.map_id } : null;
        const response = await _rimapiRequest('GET', '/api/v1/map/things', query);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_map_plants': {
      const result = await requireMapLoaded(async () => {
        const query = args.map_id !== undefined ? { map_id: args.map_id } : null;
        const response = await _rimapiRequest('GET', '/api/v1/map/plants', query);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_map_weather': {
      const result = await requireMapLoaded(async () => {
        const query = args.map_id !== undefined ? { map_id: args.map_id } : null;
        const response = await _rimapiRequest('GET', '/api/v1/map/weather', query);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_map_animals': {
      const result = await requireMapLoaded(async () => {
        const query = args.map_id !== undefined ? { map_id: args.map_id } : null;
        const response = await _rimapiRequest('GET', '/api/v1/map/animals', query);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_research': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('GET', '/api/v1/research/progress');
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_factions': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('GET', '/api/v1/factions');
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_world_caravans': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('GET', '/api/v1/world/caravans');
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // RIMAPI 工具 - 操作类 (POST)
    // tool-cleanup：post_game_load / post_game_save 已删除，存档读/写走 GABP 镜像（rimworld.load_game / rimworld.save_game）
    case 'post_incident_execute': {
      // P6：load/存档加载后立即调用时游戏可能仍在 GAME_STARTING，自动轮询等待进入 IN_GAME（默认 60s，附阶段信息）
      const waitMs = Number.isFinite(args?.waitTimeoutMs) ? args.waitTimeoutMs : (config.timings?.waitForInGameMs ?? 60000);
      const awaited = await waitForInGame(waitMs);
      if (!awaited.ok) {
        return { content: [{ type: 'text', text: JSON.stringify(awaited.error, null, 2) }] };
      }
      const result = await (async () => {
        const body = { name: args.incident_def_name, map_id: args.target_map_id };
        if (args.incident_parms !== undefined) body.incident_parms = args.incident_parms;
        const response = await _rimapiRequest('POST', '/api/v1/incident/trigger', null, body, 30, true);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_order_designate': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/order/designate/area', null, {
          map_id: args.map_id,
          type: args.type,
          point_a: args.point_a,
          point_b: args.point_b
        }, 30, true);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // ========== 新增 RIMAPI 工具处理 ==========
    // tool-cleanup：post_game_speed 已删除，时间速度控制走 GABP 镜像（rimworld.set_time_speed）
    // 殖民者详细信息
    case 'get_colonists_detailed': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('GET', '/api/v2/colonists/detailed');
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_colonist_detailed': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('GET', '/api/v2/colonist/detailed', { id: args.id });
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // tool-cleanup：get_version 已删除（版本并入 get_game_info 三源合并 / RIMAPI 段）；
    // get_mods_info / post_select / post_deselect 已删除（模组信息与选择控制走 GABP 镜像）

    // UI 交互工具
    case 'post_ui_message': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/ui/message', null, { text: args.text }, 30, true);
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_ui_dialog': {
      const result = await requireMapLoaded(async () => {
        const body = { text: args.text };
        if (args.title) body.title = args.title;
        const response = await _rimapiRequest('POST', '/api/v1/ui/dialog', null, body, 30, true);
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // 开发者控制台工具
    case 'post_dev_console': {
      const result = await requireMapLoaded(async () => {
        const body = { action: args.action };
        if (args.message) body.message = args.message;
        const response = await _rimapiRequest('POST', '/api/v1/dev/console', null, body, 30, true);
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // ========== DebugAction 工具处理 ==========
    // tool-cleanup：list_debug_actions / get_debug_action_detail / execute_debug_action 已删除
    //（DebugAction 操作全部走 GABP 镜像：rimworld.list_debug_action_roots / list_debug_action_children /
    // get_debug_action / execute_debug_action；查询保留 get_debug_action_categories / search_debug_actions）
    case 'get_debug_action_categories': {
      // 惰性索引：服务端每次请求按时间预算增量展开，未展开完返回 complete=false，
      // 循环续调直到 complete 或总超时（120s）或 MAX_POLLS 次数上限，取最后一次完整结果。
      // P2-MCP-6：退避 sleep 封顶，避免忙轮询打爆服务端。
      const MAX_POLLS = 200;
      const sleep = (ms) => new Promise(r => setTimeout(r, ms));
      const deadline = Date.now() + 120000;
      let last;
      let finalData = null;
      let n = 0;
      do {
        last = await callUnityExplorerAPI('/debugaction/categories', 'GET');
        if (!last.success) {
          if (last.errorCode === 'GAME_NOT_READY') {
            return { content: [{ type: 'text', text: `❌ ${last.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
          }
          return { content: [{ type: 'text', text: JSON.stringify(last, null, 2) }] };
        }
        finalData = last.data;
        n += 1;
        if (n >= MAX_POLLS) break;
        if (!finalData?.complete) await sleep(Math.min(150 * Math.pow(2, Math.min(n, 8)), 1500)); // 退避封顶
      } while (!finalData?.complete && Date.now() < deadline);
      const catResult = { success: true, data: {
        categories: finalData?.categories ?? [],
        count: finalData?.categories?.length ?? 0,
        complete: !!finalData?.complete,
        incomplete: !finalData?.complete && Date.now() >= deadline
      }};
      return { content: [{ type: 'text', text: JSON.stringify(catResult, null, 2) }] };
    }

    case 'search_debug_actions': {
      const queryParams = new URLSearchParams();
      queryParams.append('query', args.query);
      if (args?.category) queryParams.append('category', args.category);
      const maxResults = Number.isInteger(args?.maxResults) ? args.maxResults : 200;
      queryParams.append('maxResults', String(maxResults));
      // 惰性索引：每次请求按时间预算增量展开并返回当前快照，循环直到 complete/truncated 或总超时（120s）或 MAX_POLLS 次数上限。
      // P2-MCP-6：退避 sleep 封顶，避免忙轮询打爆服务端。
      const MAX_POLLS = 200;
      const sleep = (ms) => new Promise(r => setTimeout(r, ms));
      const deadline = Date.now() + 120000;
      let last;
      let finalData = null;
      let n = 0;
      do {
        last = await callUnityExplorerAPI(`/debugaction/search?${queryParams.toString()}`, 'GET');
        if (!last.success) {
          if (last.errorCode === 'GAME_NOT_READY') {
            return { content: [{ type: 'text', text: `❌ ${last.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
          }
          return { content: [{ type: 'text', text: JSON.stringify(last, null, 2) }] };
        }
        finalData = last.data;
        n += 1;
        if (n >= MAX_POLLS) break;
        if (!finalData?.complete && !finalData?.truncated) await sleep(Math.min(150 * Math.pow(2, Math.min(n, 8)), 1500)); // 退避封顶
      } while (!finalData?.complete && !finalData?.truncated && Date.now() < deadline);
      const searchResult = { success: true, data: {
        query: args.query,
        results: finalData?.results ?? [],
        count: finalData?.results?.length ?? 0,
        complete: !!finalData?.complete,
        truncated: !!finalData?.truncated,
        incomplete: !finalData?.complete && !finalData?.truncated && Date.now() >= deadline,
        maxResults
      }};
      return { content: [{ type: 'text', text: JSON.stringify(searchResult, null, 2) }] };
    }

    // ========== MapStructure 工具处理 ==========
    case 'get_map_structure': {
      const queryParams = new URLSearchParams();
      if (args?.path) queryParams.append('path', args.path);
      const queryStr = queryParams.toString();
      const endpoint = queryStr ? `/mapstructure/get?${queryStr}` : '/mapstructure/get';
      
      const result = await callUnityExplorerAPI(endpoint, 'GET');
      if (!result.success && result.errorCode === 'GAME_NOT_READY') {
        return { content: [{ type: 'text', text: `❌ ${result.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
      }
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'search_map_structure': {
      const queryParams = new URLSearchParams();
      queryParams.append('query', args.query);
      if (args?.category) queryParams.append('category', args.category);
      const result = await callUnityExplorerAPI(`/mapstructure/search?${queryParams.toString()}`, 'GET');
      if (!result.success && result.errorCode === 'GAME_NOT_READY') {
        return { content: [{ type: 'text', text: `❌ ${result.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
      }
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    default:
      throw new Error(`Unknown tool: ${name}`);
  }
}

// Create a new server instance for each connection
function createServer() {
  const server = new Server(
    {
      name: 'rimworld-mcp-server',
      version: '1.0.0',
    },
    {
      capabilities: {
        tools: {},
      },
    }
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => {
    return { tools: getEnabledTools() };
  });

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args } = request.params;
    const startMs = Date.now();
    const summary = summarizeToolArgs(name, args);
    try {
      let result;
      if (name === 'agg_list_tools') {
        if (!isToolEnabled('agg_list_tools')) {
          result = { content: [{ type: 'text', text: 'agg_list_tools 已禁用' }], isError: true };
        } else {
          const all = [...TOOLS, ...monoTools, ...AGG_META_TOOLS, ...gabpTools];
          result = {
            content: [{ type: 'text', text: all.map(t => `- ${isToolEnabled(t.name) ? '[启用]' : '[禁用]'} ${t.name} ${String(t.description || '').split(/\r?\n/)[0]}`).join('\n') }]
          };
        }
      } else if (name === 'agg_call_tool') {
        const target = args && args.tool;
        if (!isToolEnabled('agg_call_tool')) {
          result = { content: [{ type: 'text', text: 'agg_call_tool 已禁用' }], isError: true };
        } else if (!target) {
          result = { content: [{ type: 'text', text: '缺少参数 tool（工具名称）' }], isError: true };
        } else if (target === 'agg_list_tools' || target === 'agg_call_tool' || target === 'mcp_help') {
          result = { content: [{ type: 'text', text: '不可通过 agg_call_tool 调用元工具' }], isError: true };
        } else {
          const resolved = resolveToolAlias(target);
          if (!allToolNames().has(resolved)) {
            result = { content: [{ type: 'text', text: `未知工具: ${target}（可用工具请调用 agg_list_tools 查看）` }], isError: true };
          } else {
            const innerArgs = (args && args.args) || {};
            // 修复 1（fix-debug-server-defects，缺口 B）：agg 路径已 resolve 别名（原始名在 target 中），
            // 与 handleToolCall 相同语义——rimworld.select_pawn 缺 type 时注入 pawn（见 handleToolCall 同款注释）
            if (target === 'rimworld.select_pawn' && innerArgs.type === undefined) {
              log('debug', 'agg_call_tool rimworld.select_pawn 缺 type，注入 type=pawn');
              innerArgs.type = 'pawn';
            }
            result = await handleToolCall(resolved, innerArgs);
          }
        }
      } else if (name === 'mcp_help') {
        if (!isToolEnabled('mcp_help')) {
          result = { content: [{ type: 'text', text: 'mcp_help 已禁用' }], isError: true };
        } else {
          result = mcpHelpResult(args);
        }
      } else {
        if (!isToolEnabled(name)) {
          result = { content: [{ type: 'text', text: `工具已禁用: ${name}（可在 MCP/toolConfig.json 中启用）` }], isError: true };
        } else {
          result = await handleToolCall(name, args);
        }
      }
      const elapsed = Date.now() - startMs;
      let outcome = 'unknown';
      try {
        const text = result?.content?.[0]?.text;
        const parsed = text ? JSON.parse(text) : null;
        outcome = parsed && parsed.success === true ? 'success'
          : (parsed && parsed.success === false ? `error(${parsed.errorCode || 'unknown'})`
          : 'ok');
      } catch (e) { outcome = 'ok'; }
      log('INFO', `tools/call ${name} args=${summary} → ${elapsed}ms ${outcome}`);
      // 失败路径：自动附加正确调用范式/相似工具建议（usage-hint-on-failure）
      result = attachUsageOnFailure(name, args, result);
      return result;
    } catch (error) {
      const elapsed = Date.now() - startMs;
      log('ERROR', `tools/call ${name} args=${summary} → ${elapsed}ms error=${error.message}`);
      // 不再抛出：改为返回可读错误结果并附带调用范式（usage-hint-on-failure）
      const errMsg = error && error.message !== undefined ? String(error.message) : String(error || 'Unknown error');
      let resp;
      const um = errMsg.match(/Unknown tool[:：]?\s*([^\s]+)/i);
      if (um) {
        // 未知工具（handleToolCall 默认分支抛出的 "Unknown tool: xxx"）→ 附相似工具建议 + 首选 usage
        const unk = um[1];
        resp = {
          success: false,
          error: `未知工具: ${unk}（可用工具请调用 agg_list_tools 或 mcp_help 查看）`,
          suggestions: suggestTools(unk)
        };
        if (Array.isArray(resp.suggestions) && resp.suggestions.length > 0) {
          resp.usage = buildUsageHint(resp.suggestions[0].name);
        }
      } else {
        resp = { success: false, error: errMsg };
        const h = buildUsageHint(name);
        if (h) resp.usage = h;
      }
      return { content: [{ type: 'text', text: JSON.stringify(resp, null, 2) }], isError: true };
    }
  });

  return server;
}

// Create Express app
const app = express();

// Enable CORS
// 注: CORS 放开是为了让本机 IDE/客户端跨源访问；真鉴权在「MCP→游戏 UE HTTP」链路的 token 上
// （见顶部 HOST/PORT 处的鉴权设计说明）。P0-MCP-1：收紧 origin —— 只放行本机回环来源，
// 其余跨源浏览器请求拒绝；allowedHeaders 需含 Authorization（MCP→UE 鉴权头进转发）。
app.use(cors({
  origin: (origin, cb) => {
    if (!origin) return cb(null, true);          // 非浏览器请求
    if (/^https?:\/\/(localhost|127\.0\.0\.1|\[::1\])(:\d+)?$/.test(origin)) return cb(null, true);
    return cb(new Error('CORS origin not allowed'));
  },
  methods: ['GET', 'POST', 'OPTIONS'],
  allowedHeaders: ['Content-Type', 'Authorization']
}));

// Note: Don't use express.json() here - SSE transport handles body parsing itself

// Store transports by session ID
const transports = new Map();

// Main menu notification state
// P3-MCP-2：mainMenuNotifyTimestamp 是关键路径（start_game 成功返回 notifiedAt），保留。
let mainMenuNotified = false;
let mainMenuNotifyTimestamp = null;

// Map loaded notification state
// P3-MCP-2：mapLoadedNotifyTimestamp 是遗留字段，仅 /health 展示，已不再影响流程。
let mapLoadedNotified = false;
let mapLoadedNotifyTimestamp = null;

// SSE endpoint
app.get('/sse', async (req, res) => {
  log('INFO', 'SSE client connecting...');
  
  // Create transport first - it will set the headers
  const transport = new SSEServerTransport('/message', res);
  const sessionId = transport.sessionId;
  transports.set(sessionId, transport);
  
  log('INFO', `SSE session ${sessionId} connected`);
  
  try {
    // Create a new server instance for each connection
    const server = createServer();
    await server.connect(transport);
  } catch (error) {
    log('ERROR', `Server connection error (session ${sessionId}): ${error.message}`);
    transports.delete(sessionId);
    return;
  }
  
  req.on('close', () => {
    log('INFO', `SSE session ${sessionId} disconnected`);
    transports.delete(sessionId);
  });
});

// Message endpoint
app.post('/message', async (req, res) => {
  const sessionId = req.query.sessionId;
  
  if (!sessionId) {
    log('WARN', 'POST /message missing sessionId');
    return res.status(400).json({ error: 'Missing sessionId' });
  }
  
  const transport = transports.get(sessionId);
  
  if (transport) {
    await transport.handlePostMessage(req, res);
  } else {
    log('WARN', `POST /message no active connection for session ${sessionId}`);
    res.status(400).json({ error: 'No active connection for session' });
  }
});

// Health check
app.get('/health', (req, res) => {
  res.setHeader('Content-Type', 'application/json');
  const health = {
    status: fatalErrorCount > 5 ? 'degraded' : 'ok',
    state: fatalErrorCount > 5 ? 'degraded' : 'ok',
    server: 'rimworld-mcp-server',
    version: '1.0.0',
    fatalErrorCount,
    connected: transports.size > 0,
    sessions: Array.from(transports.keys()),
    mainMenuNotified: mainMenuNotified,
    mainMenuNotifyTimestamp: mainMenuNotifyTimestamp,
    mapLoadedNotified: mapLoadedNotified,
    mapLoadedNotifyTimestamp: mapLoadedNotifyTimestamp
  };
  // 透出 mono/gabp 最近错误（若存在）
  if (monoLastError) health.mono = { lastError: monoLastError };
  if (gabpBridge && typeof gabpBridge.status === 'function') {
    const gs = gabpBridge.status();
    if (gs && gs.lastError) health.gabp = { lastError: gs.lastError };
  }
  res.end(JSON.stringify(health));
});

// Notify main menu endpoint
app.post('/notify-mainmenu', express.json(), (req, res) => {
  // P0-MCP-1：来源校验 —— 游戏侧约定标记头（x-uesd-debuger-notify: 1）或 Bearer token（与 P0-CS-1 同一契约）
  const okByHeader = req.headers['x-uesd-debuger-notify'] === '1';
  const okByAuth = getAuthToken() && req.headers.authorization === `Bearer ${getAuthToken()}`;
  if (!okByHeader && !okByAuth) {
    return res.status(403).json({ success: false, error: 'notify 来源校验失败：缺少游戏侧约定标记（P0-MCP-1）' });
  }
  const timestamp = new Date().toISOString();
  mainMenuNotified = true;
  mainMenuNotifyTimestamp = timestamp;
  
  log('INFO', `POST /notify-mainmenu received (${timestamp})`);
  
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({
    success: true,
    message: 'Main menu notification received',
    timestamp: timestamp,
    state: 'notified'
  }));
});

// Notify map loaded endpoint
app.post('/notify-maploaded', express.json(), (req, res) => {
  // P0-MCP-1：来源校验 —— 游戏侧约定标记头（x-uesd-debuger-notify: 1）或 Bearer token（与 P0-CS-1 同一契约）
  const okByHeader = req.headers['x-uesd-debuger-notify'] === '1';
  const okByAuth = getAuthToken() && req.headers.authorization === `Bearer ${getAuthToken()}`;
  if (!okByHeader && !okByAuth) {
    return res.status(403).json({ success: false, error: 'notify 来源校验失败：缺少游戏侧约定标记（P0-MCP-1）' });
  }
  const timestamp = new Date().toISOString();
  mapLoadedNotified = true;
  mapLoadedNotifyTimestamp = timestamp;
  
  log('INFO', `POST /notify-maploaded received (${timestamp})`);
  
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({
    success: true,
    message: 'Map loaded notification received',
    timestamp: timestamp,
    state: 'notified'
  }));
});

// 未捕获异常/拒绝：记录日志后不退出
let fatalErrorCount = 0;
process.on('uncaughtException', (err) => {
  fatalErrorCount += 1;
  log('ERROR', `uncaughtException: ${err && err.stack ? err.stack : String(err)}`);
  log('WARN', `第 ${fatalErrorCount} 次未捕获异常；连续多次请重启（P2-MCP-2）。`);
});
process.on('unhandledRejection', (reason) => {
  log('ERROR', `unhandledRejection: ${reason instanceof Error ? (reason.stack || reason.message) : String(reason)}`);
});

// P1-MCP-5：进程退出清理 —— 回收 mono 子进程（McpRimDebug 的 stdio transport/shared child）、
// 抓帧子进程、GABP 桥接 socket，并 flush 日志流，避免孤儿子进程/句柄泄漏。
async function cleanupAll() {
  try { if (monoClient) await monoClient.close(); } catch (e) {}
  try { if (monoTransport) await monoTransport.close(); } catch (e) {}
  if (activeStreamCapture) { try { await _stopStreamCapture(2000); } catch (e) {} }
  try { if (gabpBridge && gabpBridge.disconnect) gabpBridge.disconnect(); } catch (e) {}
  try { logStream.end(); } catch (e) {}
}
let cleaned = false;
async function safeCleanup() { if (cleaned) return; cleaned = true; await cleanupAll(); }
// 异步深清理放 beforeExit / SIGINT / SIGTERM（exit 内无法 await）
process.on('beforeExit', () => { void safeCleanup(); });
process.on('SIGINT',  () => { void safeCleanup().finally(() => process.exit(130)); });
process.on('SIGTERM', () => { void safeCleanup().finally(() => process.exit(143)); });
process.on('exit',    () => { try { logStream.end(); } catch (e) {} });  // exit 内仅同步 flush 日志

// 启动 McpRimDebug（mono 调试工具转发），失败不阻断主服务器
async function startMonoBridge() {
  try {
    await initMonoBridge();
  } catch (e) {
    log('ERROR', `McpRimDebug bridge start failed: ${e.message}`);
  }
}

async function main() {
  if (IS_STDIO) {
    //【P1-MCP-4】推荐 stdio 单实例（本分支）：不监听端口，由客户端直接 spawn 进程，通过 stdin/stdout 通信。
    // - 单实例理由：本服务的任务、鉴权 token、主菜单/地图通知、selfStartedPid 等全局状态都在模块级；
    //   HTTP 多实例共享同一份全局状态会互踩（一个实例收到通知改写 shared 状态，另几个不知情），
    //   且多个实例还可能争相启动/停止游戏（见 P1-MCP-1/2 的双实例端口冲突与误杀风险）。
    //   stdio 由客户端按需 spawn，天然单实例，杜绝上述问题。
    // - SSE（下方 app.listen 分支）保留但限单客户端：仅用于仍走 HTTP 的 IDE/客户端（如 Trae），
    //   同一时间应只有一个客户端连接，避免多会话共享全局状态。
    log('INFO', 'RimWorld MCP Server starting (stdio mode)');
    const server = createServer();
    const transport = new StdioServerTransport();
    await server.connect(transport);
    log('INFO', 'stdio transport connected; waiting for MCP messages on stdin');
    // 内嵌启动 McpRimDebug（mono 调试工具转发），失败不阻断主服务器
    await startMonoBridge();
    // 惰性初始化 GABP 桥接，随后触发发现+连接（使独立 server 也能连游戏内 RBS），失败不阻断主服务器
    await startGABPBridge();
    scheduleDiscoverAndConnect();
    watchPortsFile();   // P3-MCP-4：stdio 分支挂载 ports.json watcher
    return;
  }

  // SSE 模式（默认，无 MCP_TRANSPORT/--stdio 标记时）：监听 HTTP 端口
  // P0-MCP-1：非回环绑定但 ports.json 无 token 时禁止启动（无鉴权会把操作面暴露到网络）
  if (HOST !== '127.0.0.1' && HOST !== 'localhost' && HOST !== '::1' && !getAuthToken()) {
    console.error('[FATAL] 非回环绑定 MCP_HOST=' + HOST + ' 但 ports.json 无 token。拒绝启动（P0-MCP-1）。');
    process.exit(1);
  }
  app.listen(PORT, HOST, () => {
    log('INFO', `Server listening on http://${HOST}:${PORT} (SSE: /sse, Health: /health)`);
    console.log('========================================');
    console.log('  RimWorld MCP Server (SSE Mode)');
    console.log('========================================');
    console.log(`Server running at http://${HOST}:${PORT}`);
    console.log(`SSE endpoint: http://${HOST}:${PORT}/sse`);
    console.log(`Health check: http://${HOST}:${PORT}/health`);
    console.log(`Log file: ${LOG_FILE_PATH}`);
    console.log('========================================');
    console.log('');
    console.log('Configure Trae MCP with:');
    console.log(`  URL: http://${HOST}:${PORT}/sse`);
    console.log('');

    // 内嵌启动 McpRimDebug（mono 调试工具转发），失败不阻断主服务器
    startMonoBridge();
    // 惰性初始化 GABP 桥接，随后触发发现+连接（使独立 server 也能连游戏内 RBS），失败不阻断主服务器
    startGABPBridge();
    scheduleDiscoverAndConnect();
    watchPortsFile();   // P3-MCP-4：SSE 分支挂载 ports.json watcher
  });
}

main().catch((err) => {
  log('ERROR', `Fatal startup error: ${err && err.stack ? err.stack : String(err)}`);
  process.exit(1);
});

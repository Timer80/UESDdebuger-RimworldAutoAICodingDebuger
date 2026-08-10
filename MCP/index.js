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

const execAsync = promisify(exec);

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// Config
const PORT = process.env.MCP_PORT || 3000;
const HOST = process.env.MCP_HOST || '127.0.0.1';

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

const UE_BASE_URL = getUeBaseUrl();

// RIMAPI 基础 URL 配置
const RIMAPI_BASE_URL = (process.env.RIMAPI_BASE_URL || 'http://localhost:8765').replace(/\/$/, '');

// 错误消息常量
const ERROR_RIMAPI_NOT_ENABLED = "RIMAPI 模组未启用或未运行。请先安装并启用 RIMAPI 模组，然后加载游戏存档。";
const ERROR_MAP_NOT_LOADED = "需要进入游戏地图后才能使用此功能。请先加载或创建一个游戏存档。";

// ========== 日志机制（Task 6） ==========
const LOGS_DIR = path.join(__dirname, 'logs');
try { fs.mkdirSync(LOGS_DIR, { recursive: true }); } catch (e) { /* 忽略目录创建失败 */ }

function _pad2(n, w = 2) { return String(n).padStart(w, '0'); }
function _formatLogTime(d) { return `${_pad2(d.getHours())}:${_pad2(d.getMinutes())}:${_pad2(d.getSeconds())}.${_pad2(d.getMilliseconds(), 3)}`; }
function _formatFileStamp(d) { return `${d.getFullYear()}${_pad2(d.getMonth() + 1)}${_pad2(d.getDate())}-${_pad2(d.getHours())}${_pad2(d.getMinutes())}${_pad2(d.getSeconds())}`; }

const LOG_FILE_PATH = path.join(LOGS_DIR, `mcp-${_formatFileStamp(new Date())}.log`);
const logStream = fs.createWriteStream(LOG_FILE_PATH, { flags: 'a' });

// 统一日志：格式 [HH:mm:ss.SSS] <LEVEL> msg，同时 console 输出与写入日志文件
// stdio 模式下 stdout 被 MCP 协议占用，日志改走 stderr 避免污染协议流；SSE 模式保持 stdout
function log(level, msg) {
  const line = `[${_formatLogTime(new Date())}] <${level}> ${msg}`;
  try { IS_STDIO ? console.error(line) : console.log(line); } catch (e) { /* ignore */ }
  try { logStream.write(line + '\n'); } catch (e) { /* ignore */ }
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
async function _rimapiRequest(method, path, query = null, jsonBody = null, timeoutSeconds = 30) {
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
    const result = await _rimapiRequest('GET', '/api/v1/game/state', null, null, 5);
    // RIMAPI 返回 { success: true/false, data: {...} }
    return result.success === true || (result._http_status >= 200 && result._http_status < 300);
  } catch {
    return false;
  }
}

// 检查地图是否已加载
async function checkMapLoaded() {
  try {
    const result = await _rimapiRequest('GET', '/api/v1/game/state', null, null, 5);
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
async function getProcessStartTime(pid) {
  try {
    const { stdout } = await execAsync(
      `powershell -NoProfile -Command "(Get-Process -Id ${pid} -ErrorAction SilentlyContinue).StartTime.ToString('yyyy-MM-dd HH:mm:ss.fff')"`
    );
    const timeStr = stdout.trim();
    if (!timeStr || /error/i.test(timeStr)) return null;
    const t = new Date(timeStr).getTime();
    return Number.isFinite(t) ? t : null;
  } catch (e) {
    return null;
  }
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
    const rimapiResult = await _rimapiRequest('GET', '/api/v1/game/state', null, null, 5);
    rimapiAvailable = rimapiResult.success === true || (rimapiResult._http_status >= 200 && rimapiResult._http_status < 300);
    if (rimapiAvailable && rimapiResult.data) {
      programState = rimapiResult.data.program_state;
      mapCount = rimapiResult.data.map_count || 0;
    }
    // UE 探测（3s 短超时）
    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 3000);
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

// UnityExplorer HTTP API 调用辅助函数
async function callUnityExplorerAPI(endpoint, method = 'GET', body = null) {
  // options 提到 try 外：连接失败重试（换新端口）时可直接复用
  const options = {
    method,
    headers: {}
  };

  if (body && (method === 'POST' || method === 'DELETE')) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(body);
  }

  try {
    const url = `${UE_BASE_URL}${endpoint}`;
    const response = await fetch(url, options);
    const data = await response.json();
    
    return {
      success: response.ok && data.success,
      data: data.data || data,
      error: data.error,
      errorCode: data.errorCode,
      statusCode: response.status
    };
  } catch (error) {
    // 连接类错误（fetch failed / ECONNREFUSED 等）：UE 端口可能已变化（ports.json 的 ueHttpPort），
    // 强制重读刷新缓存后，用新端口重试一次
    const isConnError = /fetch failed|ECONNREFUSED|ECONNRESET|ENOTFOUND|network error|connection refused/i.test(String(error && error.message));
    if (isConnError) {
      try {
        const retryUrl = `${getUeBaseUrl(true)}${endpoint}`;
        const retryResp = await fetch(retryUrl, options);
        const retryData = await retryResp.json();
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

// Find process by name
async function findProcess(processName) {
  try {
    const { stdout } = await execAsync(`tasklist /FI "IMAGENAME eq ${processName}" /FO CSV /NH`);
    const lines = stdout.trim().split('\n').filter(line => line.includes(processName));
    
    if (lines.length > 0) {
      const parts = lines[0].split('","');
      return {
        name: parts[0].replace('"', ''),
        pid: parts[1],
        memory: parts[4]
      };
    }
    return null;
  } catch (error) {
    return null;
  }
}

// Find RimWorld process
async function findRimWorldProcess() {
  return findProcess('RimWorldWin64.exe');
}

// Find Steam process
async function findSteamProcess() {
  return findProcess('steam.exe');
}

// 关闭指定进程的 Unity "Debug (Player)" 告知窗（Win32 WM_CLOSE，等同点击「确定」）。
// 通过 PowerShell Add-Type 内嵌 C# P/Invoke 实现（Node 侧无 user32 绑定）；
// 匹配条件：窗口属于目标进程 + 标题精确等于 "Debug (Player)" + 可见，避免误伤其它窗口。
// 窗口在游戏启动后约 1~3s 才出现，此处轮询最多 timeoutMs 并反复关闭可能重复弹出的窗口。
// 返回是否至少成功关闭过一次。
async function dismissDebugPlayerWindow(pid, timeoutMs = 20000) {
  if (!pid || process.platform !== 'win32') return false;
  const script = `
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinDismiss {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static IntPtr FindWindowByPid(uint target) {
    IntPtr hit = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == target) {
        var t = new StringBuilder(256); GetWindowText(h, t, t.Capacity);
        if (IsWindowVisible(h) && t.ToString() == "Debug (Player)") { hit = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return hit;
  }
}
"@
$targetPid = ${pid}
$deadline = (Get-Date).AddMilliseconds(${timeoutMs})
$closed = $false
while ((Get-Date) -lt $deadline) {
  $h = [WinDismiss]::FindWindowByPid($targetPid)
  if ($h -ne [IntPtr]::Zero) {
    [void][WinDismiss]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $closed = $true
  } elseif ($closed) { break }
  Start-Sleep -Milliseconds 400
}
if ($closed) { Write-Output "CLOSED" } else { Write-Output "NONE" }
`;
  try {
    const encoded = Buffer.from(script, 'utf16le').toString('base64');
    const { stdout } = await execAsync(
      `powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand ${encoded}`,
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
async function startGame(useSteam = true, waitForNotification = true, timeout = config.startTimeout || 180000) {
  try {
    const existingProcess = await findRimWorldProcess();
    if (existingProcess) {
      return {
        success: false,
        message: `Game already running (PID: ${existingProcess.pid})`
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
      await new Promise(resolve => setTimeout(resolve, 15000));
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
    const processStableDelay = 25000;  // UE 游戏状态不可用时的退回判据：进程存在且距启动 >= 25s
    
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
          guidance: '游戏进程已退出；可检查游戏是否启动失败，或查看 read_log'
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
    const process = await findRimWorldProcess();
    if (!process) {
      return {
        success: false,
        message: 'Game not running'
      };
    }

    if (force) {
      await execAsync(`taskkill /F /IM RimWorldWin64.exe`);
    } else {
      await execAsync(`taskkill /IM RimWorldWin64.exe`);
    }

    await new Promise(resolve => setTimeout(resolve, 2000));
    const checkProcess = await findRimWorldProcess();
    
    if (checkProcess) {
      return {
        success: false,
        message: 'Game still closing, may need force'
      };
    }

    return {
      success: true,
      message: 'Game closed'
    };
  } catch (error) {
    return {
      success: false,
      message: `Stop failed: ${error.message}`
    };
  }
}

// Get game status - 多源阶段探测（Task 1.4）
async function getGameStatus() {
  const detected = await detectGameStage();
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
    ue_main_menu_ready: detected.ueMainMenu
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
async function startQuickTest(timeout = 120000) {
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
      const response = await fetch(modUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ timestamp: new Date().toISOString() })
      });
      
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
  'status', 'attach', 'detach', 'resume', 'suspend', 'launch',
  'break_add', 'break_list', 'break_remove', 'break_clear', 'break_exception',
  'wait', 'step', 'threads', 'callstack', 'locals', 'inspect',
  'eval', 'find_types', 'find_methods'
]);

let monoClient = null;
let monoTransport = null;
let monoTools = []; // 桥接就绪后填充的协议工具描述（合并进 ListTools 响应）

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
async function callMonoTool(name, args, timeoutMs = 60000) {
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
          description: 'Start via Steam (default true)',
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
    name: 'read_log',
    description: 'Read game log',
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
    name: 'tail_log',
    description: 'Get latest log content',
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
    name: 'get_config',
    description: 'Get current config',
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
          description: 'Timeout in ms for waiting map loaded notification (default 120000)',
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
    description: '[UE工具][需要进入地图] Clear all log entries in UnityExplorer log panel. Only works after entering a game map.',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_unityexplorer_logs',
    description: '[UE工具][需要进入地图] Get log content from UnityExplorer log panel. Only works after entering a game map.',
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
  // Camera 控制
  {
    name: 'post_camera_change_zoom',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 调整游戏相机缩放级别',
    inputSchema: {
      type: 'object',
      properties: {
        zoom: { 
          type: 'integer', 
          description: '缩放级别，整数' 
        }
      },
      required: ['zoom']
    }
  },
  {
    name: 'post_camera_change_position',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 移动游戏相机到指定坐标',
    inputSchema: {
      type: 'object',
      properties: {
        x: { type: 'integer', description: 'X 坐标' },
        y: { type: 'integer', description: 'Y 坐标' }
      },
      required: ['x', 'y']
    }
  },
  // 流控制
  {
    name: 'post_stream_start',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 开始相机视频流',
    inputSchema: {
      type: 'object',
      properties: {}
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
    name: 'get_game_state',
    description: '[RIMAPI工具][需要RIMAPI模组] 获取游戏当前状态',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_colonists',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 获取殖民者列表',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
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
    name: 'post_game_load',
    description: '[RIMAPI工具][需要RIMAPI模组] 加载游戏存档',
    inputSchema: {
      type: 'object',
      properties: {
        save_name: { type: 'string', description: '存档名称' },
        check_version: { type: 'boolean', description: '校验存档版本（可选，默认 false）' },
        skip_mod_mismatch: { type: 'boolean', description: '跳过模组不匹配检查（可选，默认 false）' }
      },
      required: ['save_name']
    }
  },
  {
    name: 'post_game_save',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 保存游戏',
    inputSchema: {
      type: 'object',
      properties: {
        save_name: { type: 'string', description: '存档名称' }
      },
      required: ['save_name']
    }
  },
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
  // 游戏速度控制
  {
    name: 'post_game_speed',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 设置游戏时间速度 (0=暂停, 1=正常, 2=快速, 3=超快, 4=超快-开发者模式)',
    inputSchema: {
      type: 'object',
      properties: {
        speed: { 
          type: 'integer', 
          description: '游戏速度 (0-4)',
          minimum: 0,
          maximum: 4
        }
      },
      required: ['speed']
    }
  },
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
  // 游戏版本和模组信息
  {
    name: 'get_version',
    description: '[RIMAPI工具][需要RIMAPI模组] 获取游戏版本、模组版本和 API 版本信息',
    inputSchema: {
      type: 'object',
      properties: {}
    }
  },
  {
    name: 'get_mods_info',
    description: '[RIMAPI工具][需要RIMAPI模组] 获取已加载模组列表',
    inputSchema: {
      type: 'object',
      properties: {
        uid: { 
          type: 'string', 
          description: '模组 packageId（RIMAPI 运行时参数名 uid，文档为 package_id），用于按包 ID 过滤单个模组信息；不传返回全部' 
        }
      }
    }
  },
  // 对象选择控制
  {
    name: 'post_select',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 选择游戏中的对象',
    inputSchema: {
      type: 'object',
      properties: {
        type: { 
          type: 'string', 
          description: '对象类型枚举：pawn（殖民者/小人）、building（建筑）、item（物品）；大小写不敏感（自动小写）',
          enum: ['pawn', 'building', 'item']
        },
        id: { 
          type: 'integer', 
          description: '对象 ID' 
        }
      },
      required: ['type', 'id']
    }
  },
  {
    name: 'post_deselect',
    description: '[RIMAPI工具][需要RIMAPI模组][需要进入地图] 取消当前选择',
    inputSchema: {
      type: 'object',
      properties: {}
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
    name: 'list_debug_actions',
    description: '[DebugAction工具][需要进入地图] 列出RimWorld原版DebugAction菜单。默认只返回一级菜单，如需获取子菜单请传入parentPath参数。返回信息包含path、label和hasChildren（是否有子菜单）。',
    inputSchema: {
      type: 'object',
      properties: {
        category: { 
          type: 'string', 
          description: '按分类过滤（可选），如 General, Pawns, Spawning' 
        },
        parentPath: { 
          type: 'string', 
          description: '父菜单路径，传入后可获取该菜单的子菜单。为空时返回一级菜单' 
        },
        includeHidden: { 
          type: 'boolean', 
          description: '是否包含隐藏的DebugAction（默认false）',
          default: false
        }
      }
    }
  },
  {
    name: 'get_debug_action_detail',
    description: '[DebugAction工具][需要进入地图] 获取指定DebugAction的完整详细信息，包括动作类型、游戏状态要求、DLC要求、是否可见等完整元数据。需要传入从 list_debug_actions 获取的 path。',
    inputSchema: {
      type: 'object',
      properties: {
        path: { 
          type: 'string', 
          description: 'DebugAction完整路径，用单反斜杠分隔，如 "Actions\\Spawn thing..."（双反斜杠会自动归一化）' 
        }
      },
      required: ['path']
    }
  },
  {
    name: 'execute_debug_action',
    description: '[DebugAction工具][需要进入地图] 执行指定的RimWorld DebugAction。支持多种动作类型：Action(直接执行)、ToolMap(需要坐标或矩形区域，Kill工具支持pawnId)、ToolWorld(需要世界瓦片)、ToolMapForPawns(需要Pawn ID)。所有工具都支持自动执行，无需手动点击。',
    inputSchema: {
      type: 'object',
      properties: {
        path: { 
          type: 'string', 
          description: 'DebugAction路径，如 "Actions\\General\\Destroy"' 
        },
        mapX: { 
          type: 'integer', 
          description: '地图X坐标（ToolMap类型需要）' 
        },
        mapZ: { 
          type: 'integer', 
          description: '地图Z坐标（ToolMap类型需要）' 
        },
        worldTile: { 
          type: 'integer', 
          description: '世界地图瓦片ID（ToolWorld类型需要）' 
        },
        pawnId: { 
          type: 'integer', 
          description: 'Pawn的thingIDNumber（ToolMapForPawns类型或Kill工具需要）' 
        },
        rect: {
          type: 'object',
          description: '矩形区域坐标（矩形区域工具需要，如Clear area）',
          properties: {
            startX: { type: 'integer', description: '矩形起始X坐标' },
            startZ: { type: 'integer', description: '矩形起始Z坐标' },
            endX: { type: 'integer', description: '矩形结束X坐标' },
            endZ: { type: 'integer', description: '矩形结束Z坐标' }
          },
          required: ['startX', 'startZ', 'endX', 'endZ']
        }
      },
      required: ['path']
    }
  },
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
    description: '[DebugAction工具][需要进入地图] 按关键词搜索DebugAction，在路径、标签和分类中搜索匹配项。仅返回简化信息（path和label），如需详细信息请使用 get_debug_action_detail 工具。',
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

// 元工具：压缩模式下列出/调用已启用工具
const AGG_META_TOOLS = [
  { name: 'agg_list_tools', description: '列出当前已启用的全部工具及其描述', inputSchema: { type: 'object', properties: {}, additionalProperties: false } },
  { name: 'agg_call_tool', description: '按工具名调用任意已启用工具（用于压缩模式下调用未直接暴露的工具）', inputSchema: { type: 'object', properties: { tool: { type: 'string', description: '工具名称' }, args: { type: 'object', description: '工具参数', additionalProperties: true } }, required: ['tool'] } },
  { name: 'mcp_help', description: '[元工具] 查询任何工具的用法：无参数返回全部工具概览（名称/类别/启用状态/描述，禁用工具提示经 agg_call_tool 调用）；传 tool 参数返回单个工具的详细说明（参数schema/前置条件/调用示例/易错提示）。始终可用。', inputSchema: { type: 'object', properties: { tool: { type: 'string', description: '要查询的工具名（省略则返回全部工具概览）' } }, additionalProperties: false } }
];

// ========== mcp_help 元数据（add-mcp-introspection-tool spec） ==========
// 类别标注：system / unityexplorer / rimapi / debugaction / mono(由 MONO_DEBUG_TOOL_NAMES 判定) / meta
const TOOL_CATEGORY = {
  // system
  start_game: 'system', stop_game: 'system', get_game_status: 'system',
  read_log: 'system', tail_log: 'system', get_config: 'system', start_quick_test: 'system',
  // unityexplorer
  inspect_type: 'unityexplorer', get_unityexplorer_status: 'unityexplorer',
  clear_unityexplorer_logs: 'unityexplorer', get_unityexplorer_logs: 'unityexplorer',
  create_hook: 'unityexplorer', toggle_hook: 'unityexplorer', delete_hook: 'unityexplorer',
  list_hooks: 'unityexplorer', execute_csharp_code: 'unityexplorer',
  reset_csharp_console: 'unityexplorer', add_using_directive: 'unityexplorer',
  // rimapi
  post_camera_change_zoom: 'rimapi', post_camera_change_position: 'rimapi',
  post_stream_start: 'rimapi', post_stream_stop: 'rimapi', post_stream_setup: 'rimapi',
  get_game_state: 'rimapi', get_colonists: 'rimapi', get_maps: 'rimapi',
  get_map_things: 'rimapi', get_map_plants: 'rimapi', get_map_weather: 'rimapi',
  get_map_animals: 'rimapi', get_research: 'rimapi', get_factions: 'rimapi',
  get_world_caravans: 'rimapi', post_game_load: 'rimapi', post_game_save: 'rimapi',
  post_incident_execute: 'rimapi', post_order_designate: 'rimapi', post_game_speed: 'rimapi',
  get_colonists_detailed: 'rimapi', get_colonist_detailed: 'rimapi', get_version: 'rimapi',
  get_mods_info: 'rimapi', post_select: 'rimapi', post_deselect: 'rimapi',
  post_ui_message: 'rimapi', post_ui_dialog: 'rimapi', post_dev_console: 'rimapi',
  // debugaction
  list_debug_actions: 'debugaction', get_debug_action_detail: 'debugaction',
  execute_debug_action: 'debugaction', get_debug_action_categories: 'debugaction',
  search_debug_actions: 'debugaction', get_map_structure: 'debugaction', search_map_structure: 'debugaction',
  // meta
  agg_list_tools: 'meta', agg_call_tool: 'meta', mcp_help: 'meta'
};

// 前置条件显式标注（未列出的按类别兜底规则，见 toolPrecondition）
const TOOL_PRECONDITION = {
  start_game: '游戏未运行时可启动；已有 RimWorld 进程运行时会拒绝（端口冲突）',
  stop_game: '游戏运行中',
  start_quick_test: '游戏已运行（主菜单或游戏中）',
  get_game_state: '需 RIMAPI 模组可用（无需进入地图）',
  get_version: '需 RIMAPI 模组可用',
  get_mods_info: '需 RIMAPI 模组可用（运行时版本可能要求 uid 参数）',
  post_game_load: '需 RIMAPI 模组可用'
};

// 易错点与调用示例（运行时实测积累）
const TOOL_USAGE_NOTES = {
  get_colonist_detailed: { example: '{ "id": 123 }', notes: '参数为 id（殖民者ID），不是 pawn_id；兼容 pawn_id/pawnId/colonist_id 别名' },
  get_colonists_detailed: { example: '{}', notes: '返回全部殖民者详细信息（v2 端点）' },
  search_debug_actions: { example: '{ "query": "Spawn" }', notes: '参数为 query（子串匹配）；也兼容 keyword 别名' },
  search_map_structure: { example: '{ "query": "Thing" }', notes: '参数为 query（子串匹配）；也兼容 keyword 别名' },
  inspect_type: { example: '{ "typeName": "Verse.Pawn" }', notes: '需完整类型名（命名空间.类型）。注意用 Verse.Pawn 而非 RimWorld.Pawn（会报 Type not found）；兼容 type_name 别名' },
  get_map_structure: { example: '{}', notes: '查询当前地图结构（UE 实现）' },
  execute_debug_action: { example: '{ "path": "Spawning\\\\Spawn thing...", "mapX": 10, "mapZ": 10 }', notes: 'path 需先 list_debug_actions 获取；path 用单反斜杠（自动归一化双反斜杠）；ToolMap 类需 mapX/mapZ，ToolWorld 需 worldTile，Kill 工具可传 pawnId' },
  list_debug_actions: { example: '{ "parentPath": "Spawning" }', notes: '默认只返回一级菜单；子菜单需传 parentPath' },
  execute_csharp_code: { example: '{ "code": "Verse.GenTicks.TicksGame" }', notes: '控制台默认无 using 引用，裸类型名（如 GenTicks）会报 CS0103；须用全限定名或先 add_using_directive（兼容 script/expression 别名）' },
  post_select: { example: '{ "type": "pawn", "id": 312 }', notes: 'type 为小写枚举 pawn/building/item（自动小写，传 Pawn 也能用）；兼容 thingId/pawnId 别名' },
  create_hook: { example: '{ "typeName": "Verse.Pawn", "methodName": "get_Label", "patchType": "Postfix" }', notes: '类型须带命名空间（Verse.Pawn，不是 RimWorld.Pawn 会报 TYPE_NOT_FOUND）；兼容 targetType/targetMethod/hookType 别名；patchCode 为可选自定义代码' },
  post_camera_change_position: { example: '{ "x": 125, "y": 125 }', notes: 'x/y 为地图平面坐标；兼容 z 别名（z→y）' },
  post_ui_message: { example: '{ "text": "Hello" }', notes: '参数为 text；兼容 message 别名' },
  post_dev_console: { example: '{ "action": "message", "message": "..." }', notes: 'action 可选 message/clear；兼容 console/command 别名' },
  eval: { example: '{ "expression": "this.def.defName", "threadId": 14, "frameIndex": 0 }', notes: 'mono 迷你 C# 语法：this/局部变量/静态类型全名/字面量 + 成员链；不支持算术运算符；需 VM 挂起或自动挂起求值；threadId 可为 0 自动选择' },
  attach: { example: '{ "port": 56574 }', notes: '游戏调试端口每次运行随机，先调 status 查看 debugPortFromLog 并传入该端口' },
  get_mods_info: { example: '{ "uid": "redeyedev.rimapi" }', notes: 'uid 参数即模组 packageId（RIMAPI 运行时命名，文档中为 package_id）；不传时返回全部模组，传时按 packageId 过滤。如：redeyedev.rimapi / UESDdebuger.debug.unityexplorer / brrainz.harmony' },
  post_stream_start: { example: '{}', notes: 'RIMAPI 端点 POST /api/v1/camera/stream/start' },
  post_stream_stop: { example: '{}', notes: 'RIMAPI 端点 POST /api/v1/camera/stream/stop' },
  post_stream_setup: { example: '{ "ip": "127.0.0.1", "port": 5007, "frame_width": 1920, "frame_height": 1080, "fps": 15, "quality": 30 }', notes: 'RIMAPI 端点 POST /api/v1/camera/stream/setup，参数以 query 传递（ip/port/frame_width/frame_height/fps/quality）' },
  post_game_save: { example: '{ "save_name": "MySave" }', notes: 'RIMAPI GameSaveRequestDto(FileName) 经 SnakeCaseContractResolver 反序列化，MCP 映射 save_name→file_name（body）；存档名不含扩展名' },
  post_game_load: { example: '{ "save_name": "MySave" }', notes: 'RIMAPI GameLoadRequestDto(FileName/CheckVersion/SkipModMismatch) 反序列化为 file_name/check_version/skip_mod_mismatch（body）；可选 check_version/skip_mod_mismatch' },
  post_incident_execute: { example: '{ "incident_def_name": "RaidEnemy", "target_map_id": 1 }', notes: '内部映射为 POST /api/v1/incident/trigger 的 { name, map_id }；incident_def_name 为事件 Def 名' },
  post_order_designate: { example: '{ "map_id": 1, "type": "Mine", "point_a": { "x": 0, "y": 0, "z": 0 }, "point_b": { "x": 5, "y": 0, "z": 5 } }', notes: '端点 POST /api/v1/order/designate/area；type 枚举：mine/harvest/hunt/deconstruct/remove-all' },
  find_types: { example: '{ "query": "ThingDef", "limit": 50 }', notes: 'query 子串匹配类型全名（大小写不敏感），limit 上限 200' },
  find_methods: { example: '{ "query": "Tick", "limit": 50 }', notes: 'query 子串匹配方法名（大小写不敏感），limit 上限 200' }
};

function toolCategory(name) {
  if (TOOL_CATEGORY[name]) return TOOL_CATEGORY[name];
  if (MONO_DEBUG_TOOL_NAMES.has(name)) return 'mono';
  return 'unknown';
}

function toolPrecondition(name) {
  if (TOOL_PRECONDITION[name]) return TOOL_PRECONDITION[name];
  const cat = toolCategory(name);
  if (cat === 'mono') return '需先 attach 连接调试端口（端口每次运行随机，先调 status 查看 debugPortFromLog 并传入该端口）';
  if (cat === 'unityexplorer' || cat === 'debugaction') return '需进入地图（UnityExplorer 在游戏世界内初始化）';
  if (cat === 'rimapi') return '需进入地图且 RIMAPI 模组就绪';
  return '无需前置';
}

// 当前是否可直接调用（mono 工具取决于桥接是否就绪；其余按 toolConfig）
function toolEnabledNow(name) {
  if (MONO_DEBUG_TOOL_NAMES.has(name)) return monoTools.some(t => t.name === name);
  return isToolEnabled(name);
}

// 全部工具条目（mono 工具在桥接未就绪时用占位条目，保证概览覆盖全量）
function allToolEntries() {
  const entries = [...TOOLS, ...AGG_META_TOOLS];
  for (const m of MONO_DEBUG_TOOL_NAMES) {
    const found = monoTools.find(t => t.name === m);
    entries.push(found || { name: m, description: '[mono调试] mono 调试工具（桥接未就绪，仅提供名称）', inputSchema: { type: 'object', properties: {} } });
  }
  return entries;
}

// mcp_help 核心逻辑：无参=概览，带 tool=详情
function mcpHelpResult(args) {
  const target = args && args.tool;
  const entries = allToolEntries();
  const usageHintFor = (name) => `该工具在 toolConfig.json 中禁用，需通过 agg_call_tool 调用（tool: ${name}）`;

  if (!target) {
    const order = { system: 0, unityexplorer: 1, rimapi: 2, debugaction: 3, mono: 4, meta: 5 };
    const tools = entries
      .map(t => ({
        name: t.name,
        category: toolCategory(t.name),
        enabled: toolEnabledNow(t.name),
        description: String(t.description || '').split(/\r?\n/)[0]
      }))
      .map(t => t.enabled ? t : { ...t, usageHint: usageHintFor(t.name) })
      .sort((a, b) => (order[a.category] ?? 9) - (order[b.category] ?? 9) || a.name.localeCompare(b.name));
    return { content: [{ type: 'text', text: JSON.stringify({ total: tools.length, note: 'enabled=false 的工具需经 agg_call_tool 调用；查询单个工具用法请传 tool 参数', tools }, null, 2) }] };
  }

  const meta = entries.find(t => t.name === target);
  if (!meta) {
    return { content: [{ type: 'text', text: JSON.stringify({ success: false, error: `未知工具: ${target}`, hint: `共 ${entries.length} 个工具；先无参调用 mcp_help 查看全览` }, null, 2) }], isError: true };
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

function getEnabledTools() {
  return [...TOOLS, ...monoTools, ...AGG_META_TOOLS].filter(t => isToolEnabled(t.name));
}

// 全部工具名（含未直接暴露的）：用于 agg_call_tool 存在性校验（禁用 ≠ 不存在，仍可经聚合器调用）
const ALL_TOOL_NAMES = new Set([...TOOLS.map(t => t.name), ...MONO_DEBUG_TOOL_NAMES, ...AGG_META_TOOLS.map(t => t.name)]);

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
  // 相机位置：y 语义即地图 z 轴，兼容 z 传法
  post_camera_change_position: { z: 'y' },
  // 存档名：name → save_name
  post_game_save: { name: 'save_name' },
  post_game_load: { name: 'save_name' },
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
  // 玩家选择：thingId → id
  post_select: { thingId: 'id', pawnId: 'id' },
  // 单个殖民者详情：RIMAPI 端点参数为 id（测试中误用 pawn_id 踩坑）
  get_colonist_detailed: { pawn_id: 'id', pawnId: 'id', colonist_id: 'id', colonistId: 'id' },
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

  // 2. post_select 的 type：RIMAPI 只接受小写枚举（pawn/building/item），自动小写避免大小写踩坑
  if (name === 'post_select' && typeof out.type === 'string') {
    out.type = out.type.toLowerCase();
  }

  // 3. 路径类参数反斜杠归一化：调用方 JSON 可能传 \\（双反斜杠），服务端只认单反斜杠
  if (name === 'get_debug_action_detail' || name === 'execute_debug_action' ||
      name === 'list_debug_actions' || name === 'get_map_structure') {
    for (const key of ['path', 'parentPath', 'parent_path']) {
      if (typeof out[key] === 'string') {
        out[key] = out[key].replace(/\\\\/g, '\\');
      }
    }
  }

  return out;
}

// Tool handler function
async function handleToolCall(name, args) {
  // 参数别名归一化：keyword→query、type_name→typeName 等（详见 TOOL_ARG_ALIASES）
  args = normalizeToolArgs(name, args);

  // mono 调试工具 → 转发 McpRimDebug
  if (MONO_DEBUG_TOOL_NAMES.has(name)) {
    return callMonoTool(name, args);
  }

  switch (name) {
    case 'start_game': {
      const timeout = args?.timeout ?? config.startTimeout ?? 180000;
      const result = await startGame(args?.useSteam ?? true, args?.waitForNotification ?? true, timeout);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'stop_game': {
      const result = await stopGame(args?.force ?? false);
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

    case 'read_log': {
      const result = await readLog(args?.lines ?? 100);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'tail_log': {
      const result = await tailLog(args?.lines ?? 50);
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }]
      };
    }

    case 'get_config': {
      return {
        content: [{ type: 'text', text: JSON.stringify(config, null, 2) }]
      };
    }

    case 'start_quick_test': {
      const timeout = args?.timeout ?? 120000;
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
      const result = await requireStage([STAGES.IN_GAME], async () => {
        const r = await callUnityExplorerAPI('/unityexplorer/hook/create', 'POST', {
          typeName: args.typeName,
          methodName: args.methodName,
          patchType: args.patchType || 'Postfix',
          patchCode: args.patchCode
        });
        // 测试踩坑：RimWorld.Pawn 会报 TYPE_NOT_FOUND，类型须用 Verse.Pawn（程序集命名空间）
        if (!r.success && r.errorCode === 'TYPE_NOT_FOUND' && typeof r.error === 'string' && r.error.includes('not found')) {
          r.guidance = '类型未找到：请使用完整命名空间类型名。例如 Verse.Pawn（不是 RimWorld.Pawn），方法如 get_Label、Tick。可用 inspect_type 或 find_types 先确认正确类型名。';
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
        });
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

    // RIMAPI 工具 - Camera 控制
    case 'post_camera_change_zoom': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/camera/change/zoom', { zoom: args.zoom });
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_camera_change_position': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/camera/change/position', { x: args.x, y: args.y });
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // RIMAPI 工具 - 流控制（RIMAPI 端点路径为 /api/v1/camera/stream/*，404 时附加 guidance）
    case 'post_stream_start': {
      const streamGuidance = { guidance: 'RIMAPI 视频流端点调用失败；若为 404 请确认 RIMAPI 版本是否支持 camera/stream，可用 post_camera_screenshot 代替' };
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/camera/stream/start', null, {});
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error,
          ...(response._http_status === 404 ? streamGuidance : {})
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_stream_stop': {
      const streamGuidance = { guidance: 'RIMAPI 视频流端点调用失败；若为 404 请确认 RIMAPI 版本是否支持 camera/stream，可用 post_camera_screenshot 代替' };
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/camera/stream/stop', null, {});
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error,
          ...(response._http_status === 404 ? streamGuidance : {})
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_stream_setup': {
      const streamGuidance = { guidance: 'RIMAPI 视频流端点调用失败；若为 404 请确认 RIMAPI 版本是否支持 camera/stream，可用 post_camera_screenshot 代替' };
      const result = await requireMapLoaded(async () => {
        // RIMAPI 以 query 接收 ip/port/frame_width/frame_height/fps/quality
        const query = {};
        if (args.ip !== undefined) query.ip = args.ip;
        if (args.port !== undefined) query.port = args.port;
        if (args.frame_width !== undefined) query.frame_width = args.frame_width;
        if (args.frame_height !== undefined) query.frame_height = args.frame_height;
        if (args.fps !== undefined) query.fps = args.fps;
        if (args.quality !== undefined) query.quality = args.quality;
        const response = await _rimapiRequest('POST', '/api/v1/camera/stream/setup', query, {});
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error,
          ...(response._http_status === 404 ? streamGuidance : {})
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // RIMAPI 工具 - 查询类 (GET)
    case 'get_game_state': {
      const result = await requireRimapi(async () => {
        const response = await _rimapiRequest('GET', '/api/v1/game/state');
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_colonists': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('GET', '/api/v1/colonists');
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

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
    case 'post_game_load': {
      const result = await requireRimapi(async () => {
        // RIMAPI 使用 SnakeCaseContractResolver 反序列化 body：GameLoadRequestDto(FileName/CheckVersion/SkipModMismatch) → file_name/check_version/skip_mod_mismatch
        const body = { file_name: args.save_name };
        if (args.check_version !== undefined) body.check_version = args.check_version;
        if (args.skip_mod_mismatch !== undefined) body.skip_mod_mismatch = args.skip_mod_mismatch;
        const response = await _rimapiRequest('POST', '/api/v1/game/load', null, body);
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_game_save': {
      const result = await requireMapLoaded(async () => {
        // RIMAPI 使用 SnakeCaseContractResolver 反序列化 body：GameSaveRequestDto(FileName) → file_name
        const response = await _rimapiRequest('POST', '/api/v1/game/save', null, { file_name: args.save_name });
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_incident_execute': {
      const result = await requireMapLoaded(async () => {
        const body = { name: args.incident_def_name, map_id: args.target_map_id };
        if (args.incident_parms !== undefined) body.incident_parms = args.incident_parms;
        const response = await _rimapiRequest('POST', '/api/v1/incident/trigger', null, body);
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
        });
        return {
          success: _isRimapiSuccess(response),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // ========== 新增 RIMAPI 工具处理 ==========
    // 游戏速度控制
    case 'post_game_speed': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/game/speed', { speed: args.speed });
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

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

    // 游戏版本和模组信息
    case 'get_version': {
      const result = await requireRimapi(async () => {
        const response = await _rimapiRequest('GET', '/api/v1/version');
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_mods_info': {
      const result = await requireRimapi(async () => {
        const query = args.uid !== undefined && args.uid !== null && args.uid !== '' ? { uid: args.uid } : null;
        const response = await _rimapiRequest('GET', '/api/v1/mods/info', query);
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // 对象选择控制
    case 'post_select': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/select', { type: args.type, id: args.id });
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'post_deselect': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/deselect');
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // UI 交互工具
    case 'post_ui_message': {
      const result = await requireMapLoaded(async () => {
        const response = await _rimapiRequest('POST', '/api/v1/ui/message', null, { text: args.text });
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
        const response = await _rimapiRequest('POST', '/api/v1/ui/dialog', null, body);
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
        const response = await _rimapiRequest('POST', '/api/v1/dev/console', null, body);
        return {
          success: response.success === true || (response._http_status >= 200 && response._http_status < 300),
          data: response.data || response,
          error: response.error
        };
      })(args);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    // ========== DebugAction 工具处理 ==========
    case 'list_debug_actions': {
      const queryParams = new URLSearchParams();
      // 支持 camelCase 和 snake_case 两种参数名
      const category = args?.category ?? args?.category;
      const parentPath = args?.parentPath ?? args?.parent_path;
      const includeHidden = args?.includeHidden ?? args?.include_hidden;
      
      if (category) queryParams.append('category', category);
      if (parentPath) queryParams.append('parentPath', parentPath);
      if (includeHidden) queryParams.append('includeHidden', 'true');
      
      const queryStr = queryParams.toString();
      const endpoint = queryStr ? `/debugaction/list?${queryStr}` : '/debugaction/list';
      
      log('debug', `list_debug_actions called with args: ${JSON.stringify(args)}`);
      log('debug', `parentPath: ${parentPath}`);
      log('debug', `endpoint: ${endpoint}`);
      
      const result = await callUnityExplorerAPI(endpoint, 'GET');
      // 处理游戏未启动的情况
      if (!result.success && result.errorCode === 'GAME_NOT_READY') {
        return { content: [{ type: 'text', text: `❌ ${result.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
      }
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_debug_action_detail': {
      const result = await callUnityExplorerAPI(`/debugaction/info?path=${encodeURIComponent(args.path)}`, 'GET');
      if (!result.success && result.errorCode === 'GAME_NOT_READY') {
        return { content: [{ type: 'text', text: `❌ ${result.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
      }
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'execute_debug_action': {
      const body = { path: args.path };
      if (args.mapX !== undefined && args.mapZ !== undefined) {
        body.mapX = args.mapX;
        body.mapZ = args.mapZ;
      }
      if (args.worldTile !== undefined) {
        body.worldTile = args.worldTile;
      }
      if (args.pawnId !== undefined) {
        body.pawnId = args.pawnId;
      }
      if (args.rect !== undefined) {
        body.rect = args.rect;
      }
      const result = await callUnityExplorerAPI('/debugaction/execute', 'POST', body);
      if (!result.success && result.errorCode === 'GAME_NOT_READY') {
        return { content: [{ type: 'text', text: `❌ ${result.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
      }
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }

    case 'get_debug_action_categories': {
      // 惰性索引：服务端每次请求按时间预算增量展开，未展开完返回 complete=false，
      // 循环续调直到 complete 或总超时（120s），取最后一次完整结果
      const deadline = Date.now() + 120000;
      let last;
      let finalData = null;
      do {
        last = await callUnityExplorerAPI('/debugaction/categories', 'GET');
        if (!last.success) {
          if (last.errorCode === 'GAME_NOT_READY') {
            return { content: [{ type: 'text', text: `❌ ${last.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
          }
          return { content: [{ type: 'text', text: JSON.stringify(last, null, 2) }] };
        }
        finalData = last.data;
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
      // 惰性索引：每次请求按时间预算增量展开并返回当前快照，循环直到 complete/truncated 或总超时（120s）
      const deadline = Date.now() + 120000;
      let last;
      let finalData = null;
      do {
        last = await callUnityExplorerAPI(`/debugaction/search?${queryParams.toString()}`, 'GET');
        if (!last.success) {
          if (last.errorCode === 'GAME_NOT_READY') {
            return { content: [{ type: 'text', text: `❌ ${last.error || '游戏未启动。请先启动游戏并加载或创建一个存档。'}` }] };
          }
          return { content: [{ type: 'text', text: JSON.stringify(last, null, 2) }] };
        }
        finalData = last.data;
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
          const all = [...TOOLS, ...monoTools, ...AGG_META_TOOLS];
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
        } else if (!ALL_TOOL_NAMES.has(target)) {
          result = { content: [{ type: 'text', text: `未知工具: ${target}（可用工具请调用 agg_list_tools 查看）` }], isError: true };
        } else {
          result = await handleToolCall(target, (args && args.args) || {});
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
      return result;
    } catch (error) {
      const elapsed = Date.now() - startMs;
      log('ERROR', `tools/call ${name} args=${summary} → ${elapsed}ms error=${error.message}`);
      throw error;
    }
  });

  return server;
}

// Create Express app
const app = express();

// Enable CORS
app.use(cors({
  origin: '*',
  methods: ['GET', 'POST', 'OPTIONS'],
  allowedHeaders: ['Content-Type', 'Authorization']
}));

// Note: Don't use express.json() here - SSE transport handles body parsing itself

// Store transports by session ID
const transports = new Map();

// Main menu notification state
let mainMenuNotified = false;
let mainMenuNotifyTimestamp = null;

// Map loaded notification state
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
  res.end(JSON.stringify({ 
    status: 'ok', 
    server: 'rimworld-mcp-server',
    version: '1.0.0',
    connected: transports.size > 0,
    sessions: Array.from(transports.keys()),
    mainMenuNotified: mainMenuNotified,
    mainMenuNotifyTimestamp: mainMenuNotifyTimestamp,
    mapLoadedNotified: mapLoadedNotified,
    mapLoadedNotifyTimestamp: mapLoadedNotifyTimestamp
  }));
});

// Notify main menu endpoint
app.post('/notify-mainmenu', express.json(), (req, res) => {
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
process.on('uncaughtException', (err) => {
  log('ERROR', `uncaughtException: ${err && err.stack ? err.stack : String(err)}`);
});
process.on('unhandledRejection', (reason) => {
  log('ERROR', `unhandledRejection: ${reason instanceof Error ? (reason.stack || reason.message) : String(reason)}`);
});

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
    // stdio 模式：不监听端口，由客户端直接 spawn 进程，通过 stdin/stdout 通信
    log('INFO', 'RimWorld MCP Server starting (stdio mode)');
    const server = createServer();
    const transport = new StdioServerTransport();
    await server.connect(transport);
    log('INFO', 'stdio transport connected; waiting for MCP messages on stdin');
    // 内嵌启动 McpRimDebug（mono 调试工具转发），失败不阻断主服务器
    await startMonoBridge();
    return;
  }

  // SSE 模式（默认，无 MCP_TRANSPORT/--stdio 标记时）：监听 HTTP 端口
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
  });
}

main().catch((err) => {
  log('ERROR', `Fatal startup error: ${err && err.stack ? err.stack : String(err)}`);
  process.exit(1);
});

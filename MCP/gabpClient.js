/**
 * GabpClient — GABP 协议客户端（连接游戏内 RimBridgeServer 模组的 GABP TCP 服务器）
 *
 * 模块职责：
 *  - GABP LSP 帧（Content-Length 头 + UTF-8 JSON body）读写与解析；
 *    读侧按双 CRLF（\r\n\r\n）定位头/体分界（body 起点 = 分隔符之后、长度 = N 字节），
 *    天然容忍 Content-Type 等任意头字段。
 *  - 会话握手 session/hello、工具列举 tools/list、工具调用 tools/call。
 *  - 从 Player.log（config.logPath）发现 RimBridgeServer 端口与 token（spec §4）。
 *  - 镜像工具缓存 gabpTools（toMCPTool 转换）与状态机
 *    （disabled / idle / discovering / connecting / connected / error）。
 *
 * 对应 spec：MCP/specs/gabp-rimbridge-integration.md（定案版，§3 / §4 / §5 / §10）
 * 运行环境：Node / bun，仅内置模块（node:net / node:crypto / node:fs / node:events）
 * 依赖约定：new GabpClient(config, deps)；必须使用 config.logPath 与 config.rimBridge；
 *           deps.log(level, msg) 为日志函数（与 index.js 同签名），缺省回退 console。
 *
 * 模块格式：ESM（import/export），与 index.js 一致（MCP/package.json "type": "module"），
 *           Node >= 16 / bun 均可直接 import。
 */
import net from 'node:net';
import crypto from 'node:crypto';
import fs from 'node:fs';
import { EventEmitter } from 'node:events';

// ========== GABP 协议常量（spec §3） ==========

const PROTOCOL_VERSION = 'gabp/1';
const HOST = '127.0.0.1';

// LSP 头/体分界：双 CRLF
const CRLFCRLF = Buffer.from('\r\n\r\n');

// GABP 错误码表（反编译 GabpErrorCodes 全表，spec §3.2）
const GABP_ERROR_CODES = {
  INVALID_REQUEST: -32600,
  METHOD_NOT_FOUND: -32601,
  INVALID_PARAMS: -32602,
  INTERNAL_ERROR: -32603,
  SERVER_ERROR: -32000,
  AUTHENTICATION_FAILED: -31000,
  SESSION_NOT_ESTABLISHED: -31001,
  TOOL_NOT_FOUND: -31002,
  EVENT_CHANNEL_NOT_FOUND: -31003,
  RESOURCE_NOT_FOUND: -31004,
  METHOD_NOT_ALLOWED: -31005,
};

// Player.log 发现正则（spec §4）
const RE_GABP_PORT = /\[RimBridge\] GABP server running standalone on port (\d+)/;
const RE_GABP_TOKEN = /\[RimBridge\] Bridge token: ([0-9a-f]{32})/i;

// 指数退避参数：1s→2s→4s→...→30s cap，±25% jitter（spec §5.1 connect）
const BACKOFF_BASE_MS = 1000;
const BACKOFF_CAP_MS = 30000;
const BACKOFF_JITTER = 0.25;

// 接收缓冲上限：单帧 body 远超此值时丢弃缓冲防止内存膨胀（正常响应远小于此值）
const MAX_BUFFER_BYTES = 16 * 1024 * 1024;

// session/hello 客户端信息（spec §3.3）
const CLIENT_NAME = 'UESDdebuger-MCP';
const CLIENT_VERSION = '1.0';
const BRIDGE_VERSION = '1.0';

// 请求超时默认值（config.rimBridge.requestTimeoutMs 可调，spec §5.2）
const DEFAULT_REQUEST_TIMEOUT_MS = 30000;

// ========== 工具转换（spec §5.3） ==========

function toMCPTool(raw) {
  return {
    name: raw.name.replace(/\//g, '.'), // rimworld/list_colonists → rimworld.list_colonists
    description: `[RimBridge] ${raw.title || raw.name}${raw.description ? ' ' + raw.description : ''}`,
    inputSchema: raw.inputSchema || { type: 'object', properties: {} },
  };
}

// 从 LSP 头区提取 Content-Length（大小写不敏感，值 trim）；未找到返回 null
function parseContentLength(headerStr) {
  for (const line of headerStr.split('\r\n')) {
    const m = /^content-length:\s*(\d+)\s*$/i.exec(line);
    if (m) {
      return parseInt(m[1], 10);
    }
  }
  return null;
}

// deps.log 缺省回退（index.js 未接入前使用）
function defaultLog(level, msg) {
  try {
    console.log(`[GabpClient] <${level}> ${msg}`);
  } catch (e) {
    /* 日志失败忽略 */
  }
}

class GabpClient extends EventEmitter {
  constructor(config, deps) {
    super();
    this._config = config || {};
    this._deps = deps || {};
    const logFn = this._deps && this._deps.log;
    this._logFn = typeof logFn === 'function' ? logFn : defaultLog;

    const rb = this._config.rimBridge || {};
    const timeout = Number(rb.requestTimeoutMs);
    this._requestTimeoutMs = Number.isFinite(timeout) && timeout > 0 ? timeout : DEFAULT_REQUEST_TIMEOUT_MS;

    // 对外状态（spec §5.1）
    this.state = rb.enabled === false ? 'disabled' : 'idle';
    this.port = null;
    this.agentId = null;
    this.app = null;
    this.capabilities = null;
    this.gabpTools = [];
    this.lastError = null;
    this.connectedAt = null;

    // 内部状态
    this._discovered = null;
    this._socket = null;
    this._buffer = Buffer.alloc(0);
    this._pending = new Map(); // id → {resolve, reject, timer, method}
    this._eventsReceived = 0;
    this._abortConnect = false;
    this._connectPromise = null;
    this._abortWake = null;
    this._lastSocketError = null;
    this._connectTarget = null; // 已发起/在途连接的端点 {port, token}（状态追踪用）
  }

  // ---------- 按需重连（lazy reconnect / watcher 入口） ----------

  // 工具调用前的断连检测 + 重连（spec §10 扩展）：
  //   - disabled → false；connected → true（零成本直通）。
  //   - 未连接/断连（state=error/idle/connecting 等）→ discover（重读 Player.log 拿最新
  //     token，覆盖外部手动重启游戏后 token 滚动）→ 有限次 _connectOnce 尝试。
  //   - 连不上快速返回 false（不做后台无限退避：调用方据此报「未连接/未就绪」，用户稍后
  //     重试即可，RimBridgeServer 启动约 65s 延迟，无需空转等待）。
  // 无后台轮询、无进程/文件监听——断连检测完全惰性（被调用时发现）。
  async ensureConnected(attempts = 2, retryDelayMs = 500) {
    if (this.state === 'disabled') return false;
    if (this.state === 'connected') return true;
    // 已有在途连接循环（显式 connect()/forceReconnect 场景）：等它一小段时间，
    // 避免双 _connectOnce 并发建 socket。
    if (this._connectPromise) {
      await Promise.race([
        this._connectPromise.then(() => undefined, () => undefined),
        this._sleep(retryDelayMs * 3),
      ]);
      if (this.state === 'connected') return true;
    }
    for (let i = 0; i < attempts; i++) {
      let endpoint;
      try {
        endpoint = this.discoverRimBridge();
      } catch (e) {
        this._log('WARN', `ensureConnected discover 异常: ${e.message}`);
        return false;
      }
      if (!endpoint) {
        this._log('DEBUG', 'ensureConnected: 日志中未发现 RimBridge（游戏未启动或 RimBridgeServer 未就绪）');
        return false;
      }
      this._connectTarget = { port: endpoint.port, token: endpoint.token };
      try {
        await this._connectOnce(endpoint.port, endpoint.token); // 成功内部置 state=connected
        if (this.state === 'connected') {
          this._log('INFO', `ensureConnected: 重连成功（port=${endpoint.port}, agentId=${this.agentId}）`);
          void this._syncTools(); // 刷新镜像工具清单（不阻塞本次调用返回）
          return true;
        }
      } catch (e) {
        this._setError(e);
        this._log('WARN', `ensureConnected 第 ${i + 1}/${attempts} 次失败: ${e && e.message ? e.message : String(e)}`);
      }
      if (i < attempts - 1) await this._sleep(retryDelayMs);
    }
    // 收尾：失败后把可能残留在 connecting/discovering 的状态归位为 idle，保持状态机可重试
    if (this.state === 'connecting' || this.state === 'discovering' || this.state === 'error') {
      this.state = 'idle';
    }
    return false;
  }

  _sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // ---------- 日志 / 错误辅助 ----------

  _log(level, msg) {
    try {
      this._logFn(level, `[GabpClient] ${msg}`);
    } catch (e) {
      /* 日志失败不影响主流程（与 index.js log() 行为一致） */
    }
  }

  _err(code, message) {
    return { code, message };
  }

  _normalizeError(e) {
    if (e && typeof e === 'object' && typeof e.code === 'number' && typeof e.message === 'string') {
      return e;
    }
    const msg = e && e.message ? String(e.message) : String(e);
    return { code: GABP_ERROR_CODES.INTERNAL_ERROR, message: msg };
  }

  _setError(e) {
    const msg = e && e.message ? String(e.message) : String(e);
    this.lastError = msg;
    // EventEmitter 对 'error' 事件无监听器时会抛出：无监听时降级为日志，避免进程崩溃
    if (this.listenerCount('error') > 0) {
      this.emit('error', { error: this._normalizeError(e) });
    } else {
      this._log('ERROR', `error 事件（无监听器，已降级为日志）: ${msg}`);
    }
  }

  // ---------- 发现（spec §4） ----------

  discoverRimBridge() {
    const rb = this._config.rimBridge || {};
    if (rb.enabled === false) {
      this._log('DEBUG', 'discoverRimBridge: rimBridge.enabled=false，跳过');
      return null;
    }
    const prevState = this.state;
    this.state = 'discovering';
    try {
      // 显式配置优先（spec §4.4）：port/token 均显式时跳过日志解析。
      // token 凭据安全：优先读取环境变量 MCP_RIMBRIDGE_PORT / MCP_RIMBRIDGE_TOKEN，
      // 避免把 token 写进提交的 config.json（见代码审查 P2-BD-6）。
      const envPort = process.env.MCP_RIMBRIDGE_PORT;
      const envToken = process.env.MCP_RIMBRIDGE_TOKEN;
      const cfgPort = envPort != null ? envPort : rb.port;
      const cfgToken = envToken != null ? envToken : rb.token;
      if (cfgPort != null && cfgToken != null) {
        this._discovered = { port: Number(cfgPort), token: String(cfgToken) };
        this._log('INFO', `discoverRimBridge: 使用${envToken != null ? '环境变量' : '显式配置'} port=${this._discovered.port}`);
        return this._discovered;
      }
      const logPath = this._config.logPath;
      if (!logPath) {
        this._log('WARN', 'discoverRimBridge: config.logPath 缺失，无法发现');
        return null;
      }
      let content;
      try {
        content = fs.readFileSync(logPath, 'utf-8');
      } catch (e) {
        this._log('DEBUG', `discoverRimBridge: 读取日志失败 ${logPath}: ${e.message}`);
        return null;
      }
      const portMatch = RE_GABP_PORT.exec(content);
      const tokenMatch = RE_GABP_TOKEN.exec(content);
      if (portMatch && tokenMatch) {
        this._discovered = { port: Number(portMatch[1]), token: tokenMatch[1] };
        this._log('INFO', `discoverRimBridge: 发现 RimBridge port=${this._discovered.port}`);
        return this._discovered;
      }
      this._log('DEBUG', 'discoverRimBridge: 日志中尚未发现 RimBridge（游戏未启动或未就绪）');
      return null;
    } finally {
      this.state = prevState;
    }
  }

  // ---------- 连接（spec §5.1 / §5.2） ----------

  async connect(port, token) {
    if (this.state === 'disabled') {
      throw this._err(GABP_ERROR_CODES.SERVER_ERROR, 'GabpClient 已禁用（rimBridge.enabled=false）');
    }
    if (this.state === 'connected') {
      this._log('DEBUG', 'connect: 已连接，忽略重复调用');
      return;
    }
    if (this._connectPromise) {
      // 旧连接循环已被 forceReconnect()/disconnect() 中止、正在退出时（_abortConnect=true），
      // 等它收尾后继续新建循环；否则（正常退避重试中）复用同一 promise，避免双循环竞态。
      if (!this._abortConnect) {
        return this._connectPromise;
      }
      try {
        await this._connectPromise;
      } catch (e) {
        /* 旧循环退出时抛错（主动中止），忽略 */
      }
      this._connectPromise = null;
    }
    this._connectPromise = this._connectLoop(port, token);
    try {
      await this._connectPromise;
    } finally {
      this._connectPromise = null;
    }
  }

  // 强制重连（项 7）：中止在途退避重试循环、销毁 socket、清空端点缓存与单飞锁，
  // 使下一次 connect() 能以全新状态（重新 discover 后的端点）发起。
  // 与 disconnect() 的区别：不断开"已连接"会话语义，专用于"旧端点失效需要立即换新"
  // （游戏重启 token 滚动、端口偏移、外部手动启动后端点变化）。
  forceReconnect() {
    this._abortConnect = true;
    if (this._abortWake) {
      this._abortWake();
    }
    if (this._socket) {
      try {
        this._socket.destroy();
      } catch (e) {
        this._log('WARN', `forceReconnect 销毁 socket 异常: ${e.message}`);
      }
      this._socket = null;
    }
    const err = this._err(GABP_ERROR_CODES.SERVER_ERROR, 'GABP 强制重连');
    for (const entry of this._pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(err);
    }
    this._pending.clear();
    this._discovered = null; // 下次 discoverRimBridge 重读 Player.log / env / config
    // 注意：不在此处清 _connectPromise——旧循环收尾（abort 后 return）由旧 connect()
    // 的 finally 完成；外部随后调 connect() 会因 _abortConnect=true 先等旧循环退出再新建。
    this.state = this.state === 'disabled' ? 'disabled' : 'idle';
    this.connectedAt = null;
    this._log('INFO', 'GABP 强制重连：已中止在途连接并清空端点缓存');
    // 推迟到旧 connect() 的 finally 清空 _connectPromise 后再跑一次 ensureConnected，
    // 让重连即时发生（若立即调用，旧 promise 尚未清空会走「等待在途」分支）。
    Promise.resolve().then(() => this.ensureConnected());
  }

  // 端点解析优先级：显式参数 > 环境变量 MCP_RIMBRIDGE_PORT/TOKEN > config.rimBridge > discoverRimBridge 缓存
  _resolveEndpoint(port, token) {
    const rb = this._config.rimBridge || {};
    const envPort = process.env.MCP_RIMBRIDGE_PORT;
    const envToken = process.env.MCP_RIMBRIDGE_TOKEN;
    const p = port != null ? port : (envPort != null ? envPort : (rb.port != null ? rb.port : (this._discovered ? this._discovered.port : null)));
    const t = token != null ? token : (envToken != null ? envToken : (rb.token != null ? rb.token : (this._discovered ? this._discovered.token : null)));
    if (p == null || t == null) {
      return null;
    }
    return { port: Number(p), token: String(t) };
  }

  async _connectLoop(port, token) {
    // 端点每轮重解析（项 4）：网络级失败后端点可能已变化（token 每次游戏启动滚动、
    // 端口偏移），退避重试前先重新 discover，避免用旧端点无限 ECONNREFUSED。
    let endpoint = this._resolveEndpoint(port, token);
    if (!endpoint) {
      this._setError('connect: 缺少 port/token，请先 discoverRimBridge() 或提供显式配置');
      throw this._err(GABP_ERROR_CODES.SERVER_ERROR, 'connect 需要 port/token（可先调用 discoverRimBridge()）');
    }
    this._abortConnect = false;
    let attempt = 0;
    while (!this._abortConnect) {
      try {
        await this._connectOnce(endpoint.port, endpoint.token);
        this._log('INFO', `GABP 会话建立成功（port=${endpoint.port}, agentId=${this.agentId}）`);
        await this._syncTools();
        return;
      } catch (e) {
        if (this._abortConnect) {
          return;
        }
        this._setError(e);
        const delay = this._connectDelayMs(attempt);
        attempt += 1;
        this._log('WARN', `GABP 连接第 ${attempt} 次失败: ${e && e.message ? e.message : String(e)}；${delay}ms 后重试`);
        const aborted = await this._sleepAbortable(delay);
        if (aborted) {
          return;
        }
        // 项 4 + 项 6：sleep 未被中止 → 网络级失败时强制重新 discover（重读
        // Player.log / env / config），端点变化则下一轮用新端点；discover 仍不可用
        // 时继续退避等 RimBridgeServer 起来（启动约 65s 延迟）。
        if (this._isNetworkError(e)) {
          const fresh = this.discoverRimBridge();
          if (fresh) {
            endpoint = fresh;
          }
        }
      }
    }
  }

  // 网络级错误判定：端点不可达（无人监听 / 拒绝 / 重置 / 超时 / 域名解析失败）时
  // 应重新 discover（token 滚动后旧端点必失败），区别于协议级错误（认证失败/会话未建立
  // ——端点是对的，重 discover 无意义）。
  _isNetworkError(e) {
    const msg = String((e && e.message) || e || '');
    return /ECONNREFUSED|ECONNRESET|ETIMEDOUT|ENOTFOUND|EHOSTUNREACH|ENETUNREACH|EPIPE|fetch failed|connection refused|network error|socket 错误/i.test(msg);
  }

  // 指数退避：1s→2s→...→30s cap，±25% jitter
  _connectDelayMs(attempt) {
    const base = Math.min(BACKOFF_BASE_MS * Math.pow(2, attempt), BACKOFF_CAP_MS);
    const jitter = base * BACKOFF_JITTER * (Math.random() * 2 - 1);
    return Math.max(100, Math.round(base + jitter));
  }

  // 可被 disconnect() 中断的等待
  _sleepAbortable(ms) {
    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this._abortWake = null;
        resolve(false);
      }, ms);
      this._abortWake = () => {
        clearTimeout(timer);
        this._abortWake = null;
        resolve(true);
      };
    });
  }

  _connectOnce(port, token) {
    return new Promise((resolve, reject) => {
      let settled = false;
      const finish = (fn, value) => {
        if (settled) {
          return;
        }
        settled = true;
        fn(value);
      };
      this.state = 'connecting';
      this.port = port;
      this._lastSocketError = null;
      this._buffer = Buffer.alloc(0);
      const socket = new net.Socket();
      this._socket = socket;

      socket.on('data', (chunk) => {
        try {
          this._handleData(chunk);
        } catch (e) {
          this._log('ERROR', `GABP 收帧异常: ${e.message}`);
          this._setError(e);
        }
      });
      socket.on('error', (e) => {
        this._lastSocketError = e;
        this._log('WARN', `GABP socket 错误: ${e.message}`);
      });
      socket.on('close', () => {
        this._handleSocketClosed(this._lastSocketError);
        const err = this._lastSocketError || new Error('GABP socket 在握手前关闭');
        finish(reject, err);
      });

      socket.connect(port, HOST, () => {
        this._sendHello(token).then((welcome) => {
          this._onWelcome(welcome, port);
          finish(resolve, welcome);
        }, (err) => {
          try {
            socket.destroy();
          } catch (e) {
            this._log('DEBUG', `握手失败后销毁 socket 异常: ${e.message}`);
          }
          finish(reject, err);
        });
      });
    });
  }

  _sendHello(token) {
    return this._request('session/hello', {
      token,
      bridgeVersion: BRIDGE_VERSION,
      platform: process.platform || 'unknown',
      launchId: crypto.randomUUID(),
      clientInfo: { name: CLIENT_NAME, version: CLIENT_VERSION },
    });
  }

  _onWelcome(welcome, port) {
    const w = welcome && typeof welcome === 'object' ? welcome : {};
    this.agentId = w.agentId || null;
    this.app = w.app || null;
    this.capabilities = w.capabilities || null;
    this.port = port;
    this.connectedAt = new Date().toISOString();
    this.lastError = null;
    this.state = 'connected';
    const methods = this.capabilities && Array.isArray(this.capabilities.methods) ? this.capabilities.methods : [];
    this._log('INFO', `GABP 握手成功: agentId=${this.agentId}, schemaVersion=${w.schemaVersion || '?'}, methods=${methods.length}`);
    this.emit('connected', w);
  }

  // ---------- 断开（spec §5.2） ----------

  disconnect() {
    this._abortConnect = true;
    if (this._abortWake) {
      this._abortWake();
    }
    if (this._socket) {
      try {
        this._socket.destroy();
      } catch (e) {
        this._log('WARN', `disconnect 销毁 socket 异常: ${e.message}`);
      }
      this._socket = null;
    }
    const err = this._err(GABP_ERROR_CODES.SERVER_ERROR, 'GABP 已主动断开');
    for (const entry of this._pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(err);
    }
    this._pending.clear();
    if (this.state === 'connected') {
      this.state = 'idle';
      this.connectedAt = null;
      this.agentId = null;
      this.capabilities = null;
      this.emit('disconnected', { reason: 'client disconnect' });
    } else if (this.state !== 'disabled') {
      this.state = 'idle';
    }
    this._log('DEBUG', 'GabpClient.disconnect() 完成');
  }

  // socket close/error → 状态置 error 并触发 disconnected；由上层决定重连（spec §5.2）
  _handleSocketClosed(err) {
    if (this.state === 'connected') {
      const reason = err && err.message ? err.message : 'connection lost';
      this.state = 'error';
      this.lastError = reason;
      this.connectedAt = null;
      this._log('WARN', `GABP 连接断开: ${reason}`);
      this.emit('disconnected', { reason });
    }
    const closedErr = this._err(GABP_ERROR_CODES.SERVER_ERROR, 'GABP 连接已关闭');
    for (const entry of this._pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(closedErr);
    }
    this._pending.clear();
  }

  // ---------- 请求关联（spec §5.2） ----------

  _request(method, params, opts = {}) {
    return new Promise((resolve, reject) => {
      const socket = this._socket;
      if (!socket || socket.destroyed || !socket.writable) {
        reject(this._err(GABP_ERROR_CODES.SESSION_NOT_ESTABLISHED, `GABP 未连接，无法发送 ${method}`));
        return;
      }
      const id = crypto.randomUUID();
      // P1-MCP-3.1：per-call 超时。默认 this._requestTimeoutMs；opts.slow → 翻倍（慢操作：载存/调试动作等）。
      // 2026-09-17：opts.timeoutMs 显式值优先于 slow 判定——由 MCP/longTask/timeoutPolicy.js
      // 按入参推导（如 play_for{durationMs:600000} 应等 610000ms 而不是默认的 30000/60000），
      // 使「时长就是入参」的工具自动获得与请求时长匹配的超时。
      const timeoutMs = Number.isFinite(opts.timeoutMs) && opts.timeoutMs > 0
        ? opts.timeoutMs
        : (opts.slow ? this._requestTimeoutMs * 2 : this._requestTimeoutMs);
      const timer = setTimeout(() => {
        this._pending.delete(id);
        reject(this._err(GABP_ERROR_CODES.SERVER_ERROR, `GABP 请求 '${method}' 超时（${timeoutMs}ms）`));
      }, timeoutMs);
      this._pending.set(id, { resolve, reject, timer, method });
      try {
        this._writeMessage({ v: PROTOCOL_VERSION, id, type: 'request', method, params: params || {} });
      } catch (e) {
        clearTimeout(timer);
        this._pending.delete(id);
        reject(this._err(GABP_ERROR_CODES.SERVER_ERROR, `GABP 发送 '${method}' 失败: ${e.message}`));
      }
    });
  }

  // ---------- LSP 帧读写（spec §3.1 / §5.2） ----------

  _writeMessage(msg) {
    const socket = this._socket;
    if (!socket || socket.destroyed || !socket.writable) {
      throw new Error('GABP socket 不可写');
    }
    const body = JSON.stringify(msg);
    const byteLen = Buffer.byteLength(body, 'utf8');
    socket.write(`Content-Length: ${byteLen}\r\nContent-Type: application/json\r\n\r\n`);
    socket.write(body, 'utf8');
  }

  // 读侧帧解析：先定位双 CRLF 作为头/体分界，body 起点 = 分隔符之后、长度 = N 字节；
  // 服务器发送 Content-Type 等额外头字段时天然兼容（spec §3.1，禁止按头行偏移取体）
  _handleData(chunk) {
    this._buffer = this._buffer.length === 0 ? chunk : Buffer.concat([this._buffer, chunk]);
    if (this._buffer.length > MAX_BUFFER_BYTES) {
      // 防御：单帧 body 异常超大或头不完整时丢弃缓冲，防止内存膨胀（正常响应远小于此值）
      this._log('WARN', `GABP 接收缓冲超过 ${MAX_BUFFER_BYTES} 字节且无完整帧，丢弃缓冲`);
      this._buffer = Buffer.alloc(0);
      return;
    }
    let headerEnd = this._buffer.indexOf(CRLFCRLF);
    while (headerEnd !== -1) {
      const headerStr = this._buffer.subarray(0, headerEnd).toString('utf8');
      const contentLength = parseContentLength(headerStr);
      if (contentLength === null) {
        // 畸形帧（缺 Content-Length）：丢弃到分隔符后继续，避免死循环
        this._log('WARN', 'GABP 帧缺少 Content-Length 头，跳过该帧');
        this._buffer = this._buffer.subarray(headerEnd + CRLFCRLF.length);
        headerEnd = this._buffer.indexOf(CRLFCRLF);
        continue;
      }
      const bodyStart = headerEnd + CRLFCRLF.length;
      const frameEnd = bodyStart + contentLength;
      if (this._buffer.length < frameEnd) {
        return; // body 未收齐，等待更多数据
      }
      const body = this._buffer.subarray(bodyStart, frameEnd);
      this._buffer = this._buffer.subarray(frameEnd);
      try {
        this._onMessage(JSON.parse(body.toString('utf8')));
      } catch (e) {
        this._log('WARN', `GABP 帧 JSON 解析失败: ${e.message}`);
      }
      headerEnd = this._buffer.indexOf(CRLFCRLF);
    }
  }

  _onMessage(msg) {
    if (!msg || typeof msg !== 'object') {
      this._log('WARN', 'GABP 收到非对象消息，忽略');
      return;
    }
    if (msg.type === 'response') {
      const entry = this._pending.get(msg.id);
      if (!entry) {
        this._log('WARN', `GABP 响应 id 未知: ${msg.id}`);
        return;
      }
      this._pending.delete(msg.id);
      clearTimeout(entry.timer);
      if (msg.error) {
        entry.reject(this._normalizeError(msg.error));
      } else {
        entry.resolve(msg.result);
      }
      return;
    }
    if (msg.type === 'event') {
      // 事件流本阶段未启用：仅丢弃并计数（spec §9）
      this._eventsReceived += 1;
      this._log('DEBUG', `GABP event 丢弃: method=${msg.method}, 累计=${this._eventsReceived}`);
      return;
    }
    this._log('WARN', `GABP 未知消息类型: ${msg.type}`);
  }

  // ---------- 对外 API（spec §5.1） ----------

  async listTools() {
    const res = await this._request('tools/list', {});
    return res && Array.isArray(res.tools) ? res.tools : [];
  }

  async callTool(name, args, opts = {}) {
    try {
      if (typeof name !== 'string' || name.length === 0) {
        return { ok: false, error: this._err(GABP_ERROR_CODES.INVALID_PARAMS, 'tool name 必填') };
      }
      const parameters = args && typeof args === 'object' ? args : {};
      // P1-MCP-3.1：opts.slow → _request 按慢超时（requestTimeoutMs * 2）
      const res = await this._request('tools/call', { name, parameters }, opts);
      // 服务器直接透传 result；非对象时防御性包装（spec §3.3）
      let result = res;
      if (result === null || typeof result !== 'object') {
        result = { value: result };
      }
      return { ok: true, result };
    } catch (e) {
      return { ok: false, error: this._normalizeError(e) };
    }
  }

  async subscribeEvents(channels, handler) {
    // 预留接口（spec §9）：本阶段不实现事件订阅
    return {
      ok: false,
      error: this._err(GABP_ERROR_CODES.METHOD_NOT_FOUND, 'events/subscribe 本阶段未实现（预留接口）'),
    };
  }

  isConnected() {
    return this.state === 'connected';
  }

  status() {
    return {
      state: this.state,
      port: this.port,
      agentId: this.agentId,
      toolsCount: this.gabpTools.length,
      lastError: this.lastError,
      connectedAt: this.connectedAt,
    };
  }

  // ---------- 镜像工具缓存（spec §5.2） ----------

  async _syncTools() {
    try {
      const descriptors = await this.listTools();
      this.gabpTools = (Array.isArray(descriptors) ? descriptors : [])
        .filter((d) => d && typeof d.name === 'string')
        .map(toMCPTool);
      this._log('INFO', `GABP 镜像工具已同步: ${this.gabpTools.length} 个`);
      this.emit('tools-changed', { tools: this.gabpTools });
    } catch (e) {
      this._log('WARN', `GABP tools/list 失败: ${e && e.message ? e.message : String(e)}`);
      this._setError(e);
    }
  }
}

export { GabpClient, toMCPTool };
export default GabpClient;

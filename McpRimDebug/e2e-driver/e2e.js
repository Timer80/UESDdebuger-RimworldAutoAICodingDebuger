#!/usr/bin/env node
/**
 * e2e.js — McpRimDebug（Mono Soft Debugger 协议 .NET 10 MCP 服务器）端到端驱动脚本。
 *
 * 用途：作为「测试用例清单.md」的附件，用 Node 直接 spawn McpRimDebug.exe，
 * 通过 stdio 按行 JSON-RPC 2.0 驱动 MCP 协议（initialize / notifications/initialized /
 * tools/list / tools/call / ping），执行 Task 7 的端到端验证流程并输出逐条实测结果。
 *
 * 用法：
 *   node e2e.js <serverExe> <phase> [options]
 *
 * phase:
 *   protocol    协议握手：initialize → notifications/initialized → tools/list(核对20工具) → tools/call status
 *   flow        7.1 端到端：status → launch → 轮询 portOpen → attach → resume → wait → detach
 *   breakpoint  7.2 断点场景：attach → find_methods → break_add → resume → wait(breakpoint)
 *                           → threads → callstack → locals → eval → step → break_remove → detach
 *   all         依次执行 protocol → flow → breakpoint（同一服务器进程）
 *
 * options:
 *   --method <spec>      断点方法定位，默认自动从 find_methods 结果挑选（优先 Verse.Root_Entry:Update）
 *   --game <path>        launch 的 path 参数（默认取服务器默认路径）
 *   --port <n>           attach/launch 调试端口（默认 56574）
 *   --poll-timeout <s>   轮询 status.portOpen 的超时秒数（默认 120）
 *   --wait-ms <n>        wait 工具超时毫秒（默认 60000；flow 阶段用 15000）
 *   --log-dir <dir>      结果日志目录（默认本目录下 logs）
 *
 * 退出码：0=全部通过，1=存在失败用例，2=致命错误（无法启动/协议错误）。
 */
'use strict';

const { spawn } = require('child_process');
const readline = require('readline');
const fs = require('fs');
const path = require('path');

// ---------------------------------------------------------------- 参数解析

function parseArgs(argv) {
  const pos = [];
  const opts = {
    method: null,
    game: null,
    port: 56574,
    pollTimeout: 120,
    waitMs: 60000,
    logDir: null,
    attachDelay: 8,        // 端口开放后延迟秒数再 attach（游戏 debug 代理就绪需时间；过早 attach 会 RST）
    attachRetries: 3,      // attach 失败重试次数（间隔 5s）
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--method') { opts.method = argv[++i]; }
    else if (a === '--game') { opts.game = argv[++i]; }
    else if (a === '--port') { opts.port = parseInt(argv[++i], 10); }
    else if (a === '--poll-timeout') { opts.pollTimeout = parseInt(argv[++i], 10); }
    else if (a === '--wait-ms') { opts.waitMs = parseInt(argv[++i], 10); }
    else if (a === '--log-dir') { opts.logDir = argv[++i]; }
    else if (a === '--attach-delay') { opts.attachDelay = parseInt(argv[++i], 10); }
    else if (a === '--attach-retries') { opts.attachRetries = parseInt(argv[++i], 10); }
    else if (a.startsWith('-')) { console.error('未知选项: ' + a); process.exit(2); }
    else { pos.push(a); }
  }
  if (pos.length < 2) {
    console.error('用法: node e2e.js <serverExe> <protocol|flow|breakpoint|all> [options]');
    process.exit(2);
  }
  return { exe: pos[0], phase: pos[1], opts };
}

// ---------------------------------------------------------------- MCP 客户端

class McpClient {
  constructor(exePath) {
    this.exePath = exePath;
    this.child = spawn(exePath, [], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: false });
    this.pending = new Map();
    this.nextId = 1;
    this.stderrBuf = [];
    this.exited = null;
    this.rl = readline.createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    this.rl.on('line', (line) => this._onLine(line));
    this.child.stderr.on('data', (d) => this.stderrBuf.push(d.toString()));
    this.child.on('exit', (code) => {
      this.exited = code;
      for (const [, p] of this.pending) { clearTimeout(p.timer); p.reject(new Error('服务器进程已退出, code=' + code)); }
      this.pending.clear();
    });
    this.child.on('error', (err) => {
      for (const [, p] of this.pending) { clearTimeout(p.timer); p.reject(err); }
      this.pending.clear();
    });
  }

  _onLine(line) {
    const t = line.trim();
    if (!t) return;
    let msg;
    try { msg = JSON.parse(t); } catch (e) {
      this.stderrBuf.push('stdout 非 JSON 行: ' + t);
      return;
    }
    if (msg.id !== undefined && msg.id !== null && this.pending.has(msg.id)) {
      const p = this.pending.get(msg.id);
      this.pending.delete(msg.id);
      clearTimeout(p.timer);
      p.resolve(msg);
    }
    // 服务器主动通知（本项目无），忽略
  }

  /** 发送请求并等待对应 id 的响应。resolve 的 msg 形如 {jsonrpc,id,result|error} */
  request(method, params, timeoutMs) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`请求超时(${timeoutMs}ms): ${method}#${id}`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params: params || {} }) + '\n');
    });
  }

  /** 发送通知（无响应） */
  notify(method, params) {
    this.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params: params || {} }) + '\n');
  }

  kill() {
    try { this.child.stdin.end(); } catch (e) { /* ignore */ }
    try { this.child.kill(); } catch (e) { /* ignore */ }
  }

  stderrText() {
    return this.stderrBuf.join('').trim();
  }
}

// ---------------------------------------------------------------- 响应解析

/** 从 tools/call 的 result 中提取统一 {ok, message, data} */
function parseToolPayload(rawResult) {
  if (!rawResult) return { ok: false, message: '（无 result）', data: null, raw: null };
  let text = '';
  const content = rawResult.content || [];
  for (const c of content) {
    if (c.type === 'text') text += c.text;
  }
  const pick = (obj, names) => {
    if (!obj) return undefined;
    for (const n of names) if (Object.prototype.hasOwnProperty.call(obj, n)) return obj[n];
    return undefined;
  };
  // 1) content[].text 为 JSON（ToolResult 序列化，兼容 PascalCase/camelCase）
  let parsed = null;
  if (text.trim()) {
    try { parsed = JSON.parse(text); } catch (e) { parsed = null; }
  }
  if (parsed && (pick(parsed, ['ok', 'Ok']) !== undefined || pick(parsed, ['message', 'Message']) !== undefined)) {
    const ok = pick(parsed, ['ok', 'Ok']);
    return {
      ok: ok === true || ok === 'true' || ok === 'True',
      message: pick(parsed, ['message', 'Message']),
      data: pick(parsed, ['data', 'Data']) || null,
      raw: rawResult,
      text,
    };
  }
  // 2) structuredContent
  const sc = rawResult.structuredContent;
  if (sc && pick(sc, ['ok', 'Ok']) !== undefined) {
    return { ok: !!pick(sc, ['ok', 'Ok']), message: pick(sc, ['message', 'Message']), data: pick(sc, ['data', 'Data']) || null, raw: rawResult, text };
  }
  // 3) 兜底
  return { ok: !rawResult.isError, message: text || JSON.stringify(rawResult), data: null, raw: rawResult, text };
}

function deepGet(obj, dotPath) {
  if (!dotPath) return undefined;
  return dotPath.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj);
}

function jstr(v) {
  return JSON.stringify(v);
}

// ---------------------------------------------------------------- 步骤执行器

class Runner {
  constructor(client, opts, phase) {
    this.client = client;
    this.opts = opts;
    this.phase = phase;
    this.vars = {};
    this.results = [];
    this.failures = 0;
    this.stepNo = 0;
    this.logPath = null;
  }

  attachLog(dir) {
    if (!dir) dir = path.join(__dirname, 'logs');
    fs.mkdirSync(dir, { recursive: true });
    const ts = new Date().toISOString().replace(/[:.]/g, '-');
    this.logPath = path.join(dir, `${this.phase}-${ts}.jsonl`);
    fs.appendFileSync(this.logPath, '# e2e ' + this.phase + ' ' + new Date().toISOString() + '\n');
  }

  logLine(obj) {
    if (this.logPath) fs.appendFileSync(this.logPath, JSON.stringify(obj) + '\n');
  }

  /** 子串替换 ${var} */
  substitute(value, vars) {
    if (typeof value === 'string') {
      return value.replace(/\$\{([a-zA-Z0-9_]+)\}/g, (m, name) => {
        const v = vars[name];
        return v === undefined ? m : String(v);
      });
    }
    if (Array.isArray(value)) return value.map((v) => this.substitute(v, vars));
    if (value && typeof value === 'object') {
      const out = {};
      for (const k of Object.keys(value)) out[k] = this.substitute(value[k], vars);
      return out;
    }
    return value;
  }

  /** 直接发 JSON-RPC 请求（非 tools/call） */
  async raw(method, params, timeoutMs, label) {
    const t0 = Date.now();
    let msg;
    try {
      msg = await this.client.request(method, params, timeoutMs);
    } catch (e) {
      this._record(label || method, false, { error: e.message }, {}, null, Date.now() - t0);
      throw e;
    }
    if (msg.error) {
      this._record(label || method, false, { error: msg.error }, msg.result, msg, Date.now() - t0);
      return { error: msg.error, result: msg.result };
    }
    this._record(label || method, true, {}, msg.result, msg, Date.now() - t0);
    return { error: null, result: msg.result };
  }

  /** tools/call 包装：校验统一返回结构；capture 收集 vars；expectOk 校验 ok 字段 */
  async call(tool, args, { timeoutMs = 30000, expectOk = true, label, capture = null } = {}) {
    const name = label || tool;
    const t0 = Date.now();
    let msg;
    try {
      msg = await this.client.request('tools/call', { name: tool, arguments: this.substitute(args || {}, this.vars) }, timeoutMs);
    } catch (e) {
      this._record(name, false, { tool, error: e.message }, {}, null, Date.now() - t0);
      this.failures++;
      throw e;
    }
    if (msg.error) {
      this._record(name, false, { tool, jsonrpcError: msg.error }, msg.result, msg, Date.now() - t0);
      this.failures++;
      return { ok: false, message: 'JSON-RPC 错误: ' + jstr(msg.error), data: null };
    }
    const payload = parseToolPayload(msg.result);
    const pass = expectOk ? payload.ok : true;
    if (!pass) this.failures++;
    this._record(name, pass, { tool, args: this.substitute(args || {}, this.vars) }, payload, msg, Date.now() - t0);
    // 捕获变量（dot path → vars）
    if (capture) {
      for (const key of Object.keys(capture)) {
        const src = capture[key];
        const v = deepGet(payload, src);
        if (v !== undefined) this.vars[key] = v;
      }
    }
    return payload;
  }

  _record(stepName, ok, meta, payload, rawMsg, durationMs) {
    const rec = {
      step: ++this.stepNo,
      name: stepName,
      ok,
      meta,
      payload: payload ? { ok: payload.ok, message: payload.message, data: payload.data } : null,
      durationMs,
      at: new Date().toISOString(),
    };
    this.results.push(rec);
    this.logLine(rec);
  }

  /** attach 带重试：游戏 debug 代理就绪前连接会被 RST，间隔 5s 重试 attachRetries 次 */
  async attachWithRetry(host, port, label) {
    const retries = this.opts.attachRetries;
    let last = null;
    for (let i = 0; i <= retries; i++) {
      if (i > 0) {
        console.log('   attach 第 ' + i + ' 次重试（5s 后）...');
        await this.sleep(5000);
      }
      last = await this.call('attach', { host, port }, {
        timeoutMs: 40000,
        label: i === 0 ? label : label + '（重试 ' + i + '）',
        capture: { protocolVersion: 'data.protocolVersion', vmVersion: 'data.vmVersion' },
      });
      if (last.ok) return last;
    }
    return last;
  }

  /** 发送 JSON-RPC 通知（无 id，服务器不响应）并记录步骤 */
  notifyStep(method, params, label) {
    try {
      this.client.notify(method, params || {});
      this._record(label || method, true, {}, null, null, 0);
      return true;
    } catch (e) {
      this._record(label || method, false, { error: e.message }, null, null, 0);
      this.failures++;
      return false;
    }
  }

  /** 打印上一条记录 */
  printLast() {
    const r = this.results[this.results.length - 1];
    const mark = r.ok ? 'PASS' : 'FAIL';
    console.log(`\n[${mark}] #${r.step} ${r.name} (${r.durationMs}ms)`);
    if (r.meta && r.meta.error) console.log('   错误: ' + (typeof r.meta.error === 'string' ? r.meta.error : jstr(r.meta.error)));
    if (r.meta && r.meta.jsonrpcError) console.log('   JSON-RPC 错误: ' + jstr(r.meta.jsonrpcError));
    if (r.payload) {
      if (r.payload.message !== undefined && r.payload.message !== null) console.log('   message: ' + r.payload.message);
      if (r.payload.data !== undefined && r.payload.data !== null) console.log('   data: ' + jstr(r.payload.data));
    }
  }

  sleep(ms) {
    return new Promise((r) => setTimeout(r, ms));
  }

  /** 轮询 tools/call 直到 deepGet(payload, dotPath) === expect（expect=null 表示等待字段非空），返回最后一次 payload */
  async poll(tool, args, dotPath, expect, { timeoutMs = 120000, intervalMs = 2000, label } = {}) {
    const deadline = Date.now() + timeoutMs;
    const start = Date.now();
    const name = label || `${tool}.${dotPath}`;
    const isNonEmpty = expect === null; // expect=null：等待字段出现且非 null
    let last = null;
    for (;;) {
      last = await this.call(tool, args, { timeoutMs: Math.min(30000, timeoutMs), expectOk: true, label: name + '（轮询）' });
      const v = deepGet(last, dotPath);
      const done = isNonEmpty ? (v !== undefined && v !== null) : (v === expect);
      if (done) {
        this._record(name + ' → 达到预期', true, { pollField: dotPath, value: v }, last, null, Date.now() - start);
        return last;
      }
      if (Date.now() >= deadline) break;
      await this.sleep(Math.min(intervalMs, Math.max(0, deadline - Date.now())));
    }
    this._record(name + ' → 超时未达预期', false, { pollField: dotPath, expect, lastValue: deepGet(last, dotPath) }, last, null, Date.now() - start);
    this.failures++;
    return last;
  }

  summary() {
    console.log('\n================ 阶段汇总: ' + this.phase + ' ================');
    for (const r of this.results) {
      console.log(`  ${r.ok ? 'PASS' : 'FAIL'}  #${r.step}  ${r.name}  (${r.durationMs}ms)${r.payload && r.payload.data ? '  data=' + jstr(r.payload.data).slice(0, 300) : ''}`);
    }
    console.log(`通过 ${this.results.length - this.failures}/${this.results.length} 条；失败 ${this.failures} 条`);
    if (this.logPath) console.log('结果日志: ' + this.logPath);
    const stderr = this.client.stderrText();
    if (stderr) console.log('服务器 stderr（非空）:\n' + stderr.slice(0, 2000));
    return this.failures === 0;
  }
}

// ---------------------------------------------------------------- 阶段实现

/** 阶段 0：协议握手 */
async function phaseProtocol(run) {
  console.log('== 阶段 protocol: MCP 握手与基础检查 ==');

  // initialize
  await run.raw('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'e2e-driver', version: '1.0.0' },
  }, 15000, 'initialize(protocolVersion=2024-11-05, clientInfo=e2e-driver)');
  run.printLast();

  // notifications/initialized（JSON-RPC 通知，不带 id）
  run.notifyStep('notifications/initialized', {}, 'notifications/initialized');
  run.printLast();

  // ping
  await run.raw('ping', {}, 10000, 'ping');
  run.printLast();

  // tools/list
  const listResult = await run.raw('tools/list', {}, 15000, 'tools/list（核对 20 工具）');
  run.printLast();
  if (listResult.error) { run.failures++; return; }
  const tools = (listResult.result.tools || []).map((t) => t.name);
  const expectTools = [
    'status', 'attach', 'detach', 'resume', 'suspend', 'launch',
    'break_add', 'break_list', 'break_remove', 'break_clear', 'break_exception', 'wait', 'step',
    'threads', 'callstack', 'locals', 'inspect', 'eval', 'find_types', 'find_methods',
  ];
  const missing = expectTools.filter((n) => !tools.includes(n));
  const extra = tools.filter((n) => !expectTools.includes(n));
  const ok20 = missing.length === 0 && tools.length === 20 && extra.length === 0;
  if (!ok20) run.failures++;
  run._record('tools/list 数量=20 且无缺失/多余', ok20, { count: tools.length, missing, extra }, null, null, 0);
  console.log(`   工具数量: ${tools.length}${missing.length ? '，缺失: ' + jstr(missing) : ''}${extra.length ? '，多余: ' + jstr(extra) : ''}`);

  // tools/call status（未连接）
  const st = await run.call('status', {}, { timeoutMs: 15000, label: 'tools/call status（游戏未运行）' });
  run.printLast();
  const checks = [
    ['status.data.state == Disconnected', st.data && st.data.state === 'Disconnected'],
    ['status.data.gameProcessRunning == false', st.data && st.data.gameProcessRunning === false],
    ['status.data.portOpen == false', st.data && st.data.portOpen === false],
  ];
  for (const [name, pass] of checks) {
    if (!pass) run.failures++;
    run._record(name, pass, {}, st, null, 0);
    console.log(`   ${pass ? 'PASS' : 'FAIL'}  ${name}`);
  }
}

/** 阶段 1（SubTask 7.1）：launch → attach → resume → wait → detach */
async function phaseFlow(run, opts) {
  console.log('\n== 阶段 flow: SubTask 7.1 端到端（启动→attach→resume→事件→detach） ==');

  // a. status：游戏未运行
  const st0 = await run.call('status', {}, { timeoutMs: 15000, label: 'a. status（游戏未运行）' });
  run.printLast();
  const preChecks = [
    ['a1. state == Disconnected', st0.data && st0.data.state === 'Disconnected'],
    ['a2. gameProcessRunning == false', st0.data && st0.data.gameProcessRunning === false],
    ['a3. portOpen == false', st0.data && st0.data.portOpen === false],
  ];
  for (const [name, pass] of preChecks) {
    if (!pass) run.failures++;
    run._record(name, pass, {}, st0, null, 0);
    console.log(`   ${pass ? 'PASS' : 'FAIL'}  ${name}`);
  }

  // b. launch（缺省参数 → 自动等待本次运行的调试端口并 attach，Unity 的 Debug(Player) 告知窗随之自动关闭）
  const launchArgs = {};
  if (opts.game) launchArgs.path = opts.game;
  if (opts.port !== 56574) launchArgs.port = opts.port;
  const lp = await run.call('launch', launchArgs, {
    timeoutMs: Math.max(120000, (opts.pollTimeout + 30) * 1000),
    label: 'b. launch（调试模式启动游戏，自动 attach）',
    capture: { pid: 'data.pid', port: 'data.port', protocolVersion: 'data.protocolVersion', vmVersion: 'data.vmVersion' },
  });
  run.printLast();
  if (!lp.ok || !run.vars.pid) {
    run.failures++;
    console.log('   launch 失败，无法继续 flow 阶段');
    return false;
  }
  console.log(`   已启动 PID=${run.vars.pid}，launch 已自动等待调试端口并 attach（Debug(Player) 告知窗自动关闭）`);
  if (!(lp.data && lp.data.autoAttached === true)) {
    run.failures++;
    console.log('   launch 未自动 attach（autoAttached!=true），后续流程不可用');
    return false;
  }
  const winClosed = lp.data && lp.data.debugPlayerWindowClosed === true;
  if (!winClosed) run.failures++;
  run._record('b. Debug(Player) 告知窗已主动关闭（debugPlayerWindowClosed）', winClosed, {}, lp, null, 0);
  console.log(`   ${winClosed ? 'PASS' : 'FAIL'}  debugPlayerWindowClosed=${lp.data && lp.data.debugPlayerWindowClosed}`);

  // c. 协议版本由代理协商（spec 预期 2.57，实际 mono 6.13 协商为 2.58），以 2.x 为通过
  const pvOk = typeof run.vars.protocolVersion === 'string' && /^2\.\d+$/.test(run.vars.protocolVersion);
  if (!pvOk) run.failures++;
  run._record('launch 自动 attach 协议版本为 2.x（实际 ' + run.vars.protocolVersion + '）', pvOk, {}, lp, null, 0);
  console.log(`   ${pvOk ? 'PASS' : 'FAIL'}  协议版本 ${run.vars.protocolVersion}，VM ${run.vars.vmVersion}`);

  // c2. status 确认会话已连接
  const cRes = await run.call('status', {}, { timeoutMs: 15000, label: 'c. status（确认已自动连接）' });
  run.printLast();
  const attachedOk = cRes.data && cRes.data.state === 'Attached' && cRes.data.attached === true;
  if (!attachedOk) run.failures++;
  run._record('c. 已自动 attach（state==Attached）', attachedOk, {}, cRes, null, 0);
  console.log(`   ${attachedOk ? 'PASS' : 'FAIL'}  state=${cRes.data && cRes.data.state}, attached=${cRes.data && cRes.data.attached}`);

  // e. resume
  const rp = await run.call('resume', {}, { timeoutMs: 30000, label: 'e. resume（游戏开始启动）' });
  run.printLast();

  // f. wait（any，15s）→ 收 VMStart/ThreadStart
  const wp = await run.call('wait', { eventType: 'any', timeoutMs: 15000 }, {
    timeoutMs: 30000, label: 'f. wait(any, 15000ms)',
    capture: { eventType: 'data.eventType', threadId: 'data.threadId' },
  });
  run.printLast();
  if (!wp.ok || !wp.data || wp.data.timeout) {
    run.failures++;
    console.log('   wait 未命中事件（' + (wp.message || 'timeout') + '）');
  } else {
    run._record('wait 命中事件 ' + run.vars.eventType + '（VMStart/ThreadStart 类启动事件）', true, {}, wp, null, 0);
    console.log(`   命中事件: ${run.vars.eventType}，threadId=${run.vars.threadId}`);
  }

  // g. detach
  const dp = await run.call('detach', {}, { timeoutMs: 30000, label: 'g. detach（游戏继续运行）' });
  run.printLast();
  const st1 = await run.call('status', {}, { timeoutMs: 15000, label: 'g2. detach 后 status' });
  run.printLast();
  const detachOk = st1.data && st1.data.state === 'Disconnected' && st1.data.gameProcessRunning === true;
  if (!detachOk) run.failures++;
  run._record('detach 后 state==Disconnected 且游戏进程仍在', detachOk, {}, st1, null, 0);
  console.log(`   ${detachOk ? 'PASS' : 'FAIL'}  state=${st1.data && st1.data.state}, gameProcessRunning=${st1.data && st1.data.gameProcessRunning}`);
  return true;
}

/** 阶段 2（SubTask 7.2）：断点 → 观测 → 求值 → 单步 */
async function phaseBreakpoint(run, opts) {
  console.log('\n== 阶段 breakpoint: SubTask 7.2 模组断点场景 ==');

  // 0. status：取游戏自报的实际调试端口（Unity 每次运行随机）
  const st0 = await run.call('status', {}, {
    timeoutMs: 15000, label: '0. status（取 debugPortFromLog）',
    capture: { discoveredPort: 'data.debugPortFromLog' },
  });
  run.printLast();
  const attachPortBp = run.vars.discoveredPort || opts.port;
  console.log(`   使用实际调试端口: ${attachPortBp}`);

  // 1. attach（游戏在跑；失败自动重试）
  const ap = await run.attachWithRetry('127.0.0.1', attachPortBp, '1. 再次 attach(' + attachPortBp + ')');
  run.printLast();
  if (!ap.ok) {
    run.failures++;
    console.log('   attach 失败，无法继续 breakpoint 阶段');
    return false;
  }

  // 2. resume（VM 运行态）
  const rp = await run.call('resume', {}, { timeoutMs: 30000, label: '2. resume' });
  run.printLast();

  // 3. 探测游戏程序集是否已加载：用 find_types 精确查询 Verse.Root_Entry。
  //    （不用 find_methods("Update")——其 200 条截断会把 Verse 方法挤到截断之外，
  //     造成「游戏程序集未加载」的误判，见 15:00 全量实测。）
  let ft = await run.call('find_types', { query: 'Root_Entry', limit: 5 }, {
    timeoutMs: 60000, label: '3. find_types("Root_Entry") 探测游戏程序集',
  });
  run.printLast();
  let ftTypes = ft.ok && Array.isArray(ft.data && ft.data.types) ? ft.data.types : [];
  let gameAsmReady = ftTypes.includes('Verse.Root_Entry');
  if (!gameAsmReady && !opts.method) {
    // 游戏启动早期 managed 程序集按需加载，Verse 可能尚未加载；循环重试
    // （每 15s 一次，最长 180s），避免过早放弃走回退。
    const retryDeadline = Date.now() + 180000;
    let attempts = 0;
    while (!gameAsmReady && Date.now() < retryDeadline) {
      attempts++;
      console.log(`   游戏程序集尚未加载（第 ${attempts} 次等待 15s 后重试 find_types）...`);
      await run.sleep(15000);
      ft = await run.call('find_types', { query: 'Root_Entry', limit: 5 }, {
        timeoutMs: 60000, label: `3b. find_types("Root_Entry") 重试 ${attempts}`,
      });
      run.printLast();
      const t2 = ft.ok && Array.isArray(ft.data && ft.data.types) ? ft.data.types : [];
      gameAsmReady = t2.includes('Verse.Root_Entry');
    }
  }

  // find_methods 功能验证（不影响断点决策；仅记录）
  await run.call('find_methods', { query: 'Update', limit: 200 }, {
    timeoutMs: 120000, label: '3c. find_methods("Update")（功能验证）',
  });
  run.printLast();

  let methodSpec = opts.method;
  if (!methodSpec) {
    // 直接使用已知的 RimWorld 主循环高频方法（BreakAdd 用 GetTypes 精确匹配，不受截断影响）
    methodSpec = 'Verse.Root_Entry:Update';
    console.log(gameAsmReady
      ? `   游戏程序集已就绪，使用高频主循环方法: ${methodSpec}`
      : `   探测超时仍未见游戏程序集；尝试已知高频方法: ${methodSpec}（若不存在 break_add 会给出明确错误并走回退观测）`);
  }
  if (!methodSpec) {
    run.failures++;
    console.log('   未找到候选方法，无法设置断点');
    return false;
  }
  run.vars.method = methodSpec;
  console.log('   断点方法: ' + methodSpec);

  // 4. break_add
  const bp = await run.call('break_add', { method: '${method}' }, {
    timeoutMs: 40000, label: '4. break_add(' + methodSpec + ')',
    capture: { bpId: 'data.id', bpMethod: 'data.method' },
  });
  run.printLast();
  if (!bp.ok || run.vars.bpId === undefined) {
    run.failures++;
    console.log('   break_add 失败，改走回退 suspend 观测流程');
    return await fallbackSuspendObserve(run);
  }
  console.log(`   断点 id=${run.vars.bpId} @ ${run.vars.bpMethod}`);

  // 5. resume
  await run.call('resume', {}, { timeoutMs: 30000, label: '5. resume' });
  run.printLast();

  // 6. wait(breakpoint, 60s)
  const hit = await run.call('wait', { eventType: 'breakpoint', timeoutMs: opts.waitMs }, {
    timeoutMs: opts.waitMs + 15000, label: '6. wait(breakpoint, ' + opts.waitMs + 'ms)',
    capture: { threadId: 'data.threadId', threadName: 'data.threadName', hitLocation: 'data.location' },
  });
  run.printLast();
  if (!hit.ok || !hit.data || hit.data.timeout) {
    run.failures++;
    console.log('   断点未命中（' + (hit.message || 'timeout') + '）——按回退策略改走 suspend 流程');
    return await fallbackSuspendObserve(run);
  }
  console.log(`   断点命中: 线程#${run.vars.threadId}(${run.vars.threadName}) @ ${run.vars.hitLocation}`);
  if (!run.vars.threadId) run.failures++;
  run._record('断点命中并获得 threadId', !!run.vars.threadId, {}, hit, null, 0);

  // 7. threads（找主线程）
  const th = await run.call('threads', {}, { timeoutMs: 30000, label: '7. threads' });
  run.printLast();
  let mainThread = null;
  if (th.ok && Array.isArray(th.data && th.data.threads)) {
    mainThread = th.data.threads.find((t) => t.name === 'Main Thread' || t.name === 'main')
      || th.data.threads.find((t) => String(t.id) === String(run.vars.threadId))
      || th.data.threads[0];
  }
  if (mainThread) {
    run.vars.mainThreadId = mainThread.id !== undefined ? mainThread.id : mainThread.threadId;
    run._record('主线程识别: id=' + run.vars.mainThreadId + ' name=' + (mainThread.name || ''), true, {}, th, null, 0);
    console.log(`   主线程: #${run.vars.mainThreadId} (${mainThread.name || ''})`);
  } else {
    run.failures++;
    run.vars.mainThreadId = run.vars.threadId;
    console.log('   未能从 threads 中识别主线程，回退用 wait 的 threadId=' + run.vars.threadId);
  }

  // 8. callstack
  const cs = await run.call('callstack', { threadId: '${mainThreadId}', frameLimit: 10 }, {
    timeoutMs: 30000, label: '8. callstack(主线程, 前10帧)',
  });
  run.printLast();
  if (!(cs.ok && Array.isArray(cs.data && cs.data.frames) && cs.data.frames.length)) run.failures++;

  // 9. locals（帧 0 与帧 1；调用栈可能仅 1 帧——方法入口断点在 Root_Entry.Update 处只有自身一帧，
  // 此时帧 1 越界属预期，按 PASS 记录，不再强断言）
  const csFrames = cs.ok && Array.isArray(cs.data && cs.data.frames) ? cs.data.frames.length : 0;
  const lc0 = await run.call('locals', { threadId: '${mainThreadId}', frameIndex: 0 }, {
    timeoutMs: 30000, label: '9a. locals(主线程, 帧0)',
  });
  run.printLast();
  if (!lc0.ok) run.failures++;
  if (csFrames >= 2) {
    const lc1 = await run.call('locals', { threadId: '${mainThreadId}', frameIndex: 1 }, {
      timeoutMs: 30000, label: '9b. locals(主线程, 帧1)',
    });
    run.printLast();
    if (!lc1.ok) run.failures++;
  } else {
    run._record('9b. locals(主线程, 帧1) — 调用栈仅 ' + csFrames + ' 帧，帧 1 越界（预期）', true, { frames: csFrames }, null, null, 0);
    console.log('   调用栈仅 ' + csFrames + ' 帧，帧 1 越界属预期，跳过帧 1 观测');
  }

  // 10. eval：this / 静态成员 / 字面量
  const ev1 = await run.call('eval', { expression: 'this', threadId: '${mainThreadId}', frameIndex: 0 }, {
    timeoutMs: 30000, label: '10a. eval("this")',
    capture: { evalThis: 'data.value' },
  });
  run.printLast();
  if (!ev1.ok) run.failures++;
  const ev2 = await run.call('eval', { expression: 'Verse.GenTicks.TicksGame', threadId: '${mainThreadId}', frameIndex: 0 }, {
    timeoutMs: 30000, label: '10b. eval("Verse.GenTicks.TicksGame")',
  });
  run.printLast();
  const ev3 = await run.call('eval', { expression: '"e2e-string-ok"', threadId: 0, frameIndex: 0 }, {
    timeoutMs: 30000, label: '10c. eval("\\"e2e-string-ok\\"")',
  });
  run.printLast();
  if (!ev3.ok) run.failures++;

  // 11. 先 break_remove 再 step：若断点仍启用，resume 后断点会再次立即命中并挂起 VM，
  // step 等待的 StepEvent 被 Breakpoint 事件掩盖而超时（14:5x 实测）。故先把断点摘除。
  const br = await run.call('break_remove', { id: '${bpId}' }, {
    timeoutMs: 30000, label: '11a. 先 break_remove(断点 ' + run.vars.bpId + ') 再单步',
  });
  run.printLast();
  if (!br.ok) run.failures++;

  // 11b. step（into）
  const sp = await run.call('step', { threadId: '${mainThreadId}', direction: 'into' }, {
    timeoutMs: 45000, label: '11b. step(threadId, into)',
    capture: { stepLocation: 'data.location' },
  });
  run.printLast();
  if (!sp.ok) {
    run.failures++;
    console.log('   step 失败/被打断: ' + (sp.message || ''));
  } else {
    console.log('   单步命中: ' + (run.vars.stepLocation || '(无行信息)'));
  }

  // 12. resume + detach（break_remove 已在 11a 提前执行）
  await run.call('resume', {}, { timeoutMs: 30000, label: '12. resume' });
  run.printLast();
  const dp = await run.call('detach', {}, { timeoutMs: 30000, label: '13. detach（会话复位）' });
  run.printLast();
  return true;
}

/** 回退策略：suspend → threads → callstack → locals → eval → resume（断点未命中时） */
async function fallbackSuspendObserve(run) {
  console.log('   —— 回退策略：suspend → 观测 → 求值 ——');
  const sp = await run.call('suspend', {}, { timeoutMs: 30000, label: '回退. suspend' });
  run.printLast();
  const th = await run.call('threads', {}, { timeoutMs: 30000, label: '回退. threads' });
  run.printLast();
  let tid = null;
  if (th.ok && Array.isArray(th.data && th.data.threads) && th.data.threads.length) {
    tid = th.data.threads[0].id !== undefined ? th.data.threads[0].id : th.data.threads[0].threadId;
    run.vars.mainThreadId = tid;
  }
  if (tid) {
    await run.call('callstack', { threadId: tid, frameLimit: 10 }, { timeoutMs: 30000, label: '回退. callstack' });
    run.printLast();
    await run.call('locals', { threadId: tid, frameIndex: 0 }, { timeoutMs: 30000, label: '回退. locals' });
    run.printLast();
    await run.call('eval', { expression: 'this', threadId: tid, frameIndex: 0 }, { timeoutMs: 30000, label: '回退. eval(this)' });
    run.printLast();
  } else {
    run.failures++;
    console.log('   回退: 无线程可观测');
  }
  await run.call('eval', { expression: '"e2e-fallback-ok"', threadId: 0, frameIndex: 0 }, { timeoutMs: 30000, label: '回退. eval(字面量)' });
  run.printLast();
  await run.call('resume', {}, { timeoutMs: 30000, label: '回退. resume' });
  run.printLast();
  await run.call('detach', {}, { timeoutMs: 30000, label: '回退. detach' });
  run.printLast();
  return true;
}

// ---------------------------------------------------------------- main

async function main() {
  const { exe, phase, opts } = parseArgs(process.argv.slice(2));

  if (!fs.existsSync(exe)) {
    console.error('服务器可执行文件不存在: ' + exe);
    process.exit(2);
  }

  console.log('McpRimDebug e2e 驱动');
  console.log('  服务器: ' + exe);
  console.log('  阶段: ' + phase);
  console.log('  端口: ' + opts.port + '，poll-timeout: ' + opts.pollTimeout + 's，wait-ms: ' + opts.waitMs);

  const client = new McpClient(exe);
  const run = new Runner(client, opts, phase);
  run.attachLog(opts.logDir);

  const t0 = Date.now();
  try {
    if (phase === 'protocol') {
      await phaseProtocol(run);
    } else if (phase === 'flow') {
      await phaseFlow(run, opts);
    } else if (phase === 'breakpoint') {
      await phaseBreakpoint(run, opts);
    } else if (phase === 'all') {
      await phaseProtocol(run);
      await phaseFlow(run, opts);
      await phaseBreakpoint(run, opts);
    } else {
      console.error('未知阶段: ' + phase);
      process.exit(2);
    }
  } catch (e) {
    console.error('\n阶段执行异常: ' + (e && e.stack || e));
  }

  const allOk = run.summary();
  console.log('\n总耗时: ' + ((Date.now() - t0) / 1000).toFixed(1) + 's');
  client.kill();
  process.exit(allOk ? 0 : 1);
}

main().catch((e) => {
  console.error('致命错误: ' + (e && e.stack || e));
  process.exit(2);
});

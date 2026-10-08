#!/usr/bin/env node
/**
 * rst-reconnect-test.js — 验证 RST 修复：异常断开（强杀调试器）后能否再次 attach。
 * 直接 spawn 独立 McpRimDebug（不经 MCP 聚合），驱动：
 *   attach → resume → (可选 detach | 强杀) → 再次 attach
 * 用法: node rst-reconnect-test.js <exe> <port> <mode:clean|kill|guard>
 *   guard（2026-09-20 新增）：Attached 状态下调 reconnect，必须**短路**且明确回报「连接实际未断」，
 *   不得拆掉健康会话、不得换端口（旧实现在这里会 ResetSession 并导致 attach 失败）。
 */
'use strict';
const { spawn } = require('child_process');
const readline = require('readline');
const net = require('net');

const exe = process.argv[2];
const port = parseInt(process.argv[3], 10);
const mode = process.argv[4] || 'clean';
const host = '127.0.0.1';

function makeClient(exePath) {
  const child = spawn(exePath, [], { stdio: ['pipe', 'pipe', 'pipe'] });
  const rl = readline.createInterface({ input: child.stdout, crlfDelay: Infinity });
  const pending = new Map();
  let nextId = 1;
  rl.on('line', (line) => {
    let msg; try { msg = JSON.parse(line); } catch { return; }
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
  });
  return {
    child,
    req(method, params, timeoutMs = 30000) {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const t = setTimeout(() => { pending.delete(id); reject(new Error(`timeout ${method}`)); }, timeoutMs);
        pending.set(id, (m) => { clearTimeout(t); resolve(m); });
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params: params || {} }) + '\n');
      });
    },
    async call(tool, args) {
      const r = await this.req('tools/call', { name: tool, arguments: args || {} });
      const content = (r.result && r.result.content || []).filter(c => c.type === 'text').map(c => c.text).join('');
      let parsed; try { parsed = JSON.parse(content); } catch { parsed = null; }
      return { ok: parsed && parsed.ok === true, message: parsed ? parsed.message : content, data: parsed && parsed.data };
    },
    kill() { try { child.kill('SIGKILL'); } catch {} }
  };
}

function portConns(p) {
  return new Promise((resolve) => {
    const s = net.connect(p, '127.0.0.1');
    s.on('connect', () => { s.destroy(); resolve('open'); });
    s.on('error', (e) => resolve('closed:' + e.code));
    setTimeout(() => { s.destroy(); resolve('timeout'); }, 3000);
  });
}

(async () => {
  const results = [];
  const rec = (name, ok, extra) => { results.push({ name, ok, ...extra }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? ' ' + JSON.stringify(extra) : ''}`); };

  // ---- 第一次会话 ----
  const c1 = makeClient(exe);
  await c1.req('initialize', { protocolVersion: '2024-11-05', capabilities: {} });
  c1.req('notifications/initialized', {});
  const a1 = await c1.call('attach', { host, port });
  rec('1st attach', a1.ok, { msg: a1.message && a1.message.slice(0, 80) });
  const r1 = await c1.call('resume', {});
  rec('1st resume', r1.ok, { msg: r1.message });

  // ---- 2026-09-20 新增：已连接时 reconnect 必须短路，且显式说明「连接实际未断」 ----
  if (mode === 'guard') {
    const s0 = await c1.call('status', {});
    const portBefore = s0.data && s0.data.debugPort;
    rec('guard: 前置 status 为 Attached', !!(s0.data && s0.data.state === 'Attached'),
      { state: s0.data && s0.data.state, port: portBefore });

    const rc = await c1.call('reconnect', {});
    rec('guard: reconnect 返回 ok（短路）', rc.ok, { msg: rc.message && rc.message.slice(0, 140) });
    rec('guard: skipped=true', !!(rc.data && rc.data.skipped === true), { data: rc.data });
    rec('guard: noop=true', !!(rc.data && rc.data.noop === true));
    rec('guard: detail=connection-alive', !!(rc.data && rc.data.detail === 'connection-alive'));
    // 用户要求：不能只回 true，必须有一句"实际未断"，防止被误读成 MCP 错误
    rec('guard: 文案含「连接实际未断」',
      /实际未断/.test(String(rc.message || '') + JSON.stringify(rc.data || {})));
    rec('guard: 短路未更换端口', !!(rc.data && rc.data.port === portBefore),
      { before: portBefore, after: rc.data && rc.data.port });

    const s1 = await c1.call('status', {});
    rec('guard: 短路后仍 Attached', !!(s1.data && s1.data.state === 'Attached'), { state: s1.data && s1.data.state });
    rec('guard: 短路后端口未变', !!(s1.data && s1.data.debugPort === portBefore));

    const d1 = await c1.call('detach', {});
    rec('guard: detach 收尾正常', d1.ok, { msg: d1.message });
    c1.kill();

    const passG = results.filter(r => r.ok).length;
    console.log(`\n=== ${passG}/${results.length} PASS (mode=guard) ===`);
    process.exit(passG === results.length ? 0 : 1);
  }

  if (mode === 'clean') {
    const d1 = await c1.call('detach', {});
    rec('clean detach', d1.ok, { msg: d1.message });
    c1.kill();
  } else {
    // 异常断开：直接强杀，不发 detach（模拟崩溃/超时强断）
    console.log('--- 强杀调试器模拟异常断开 ---');
    c1.kill();
  }
  await new Promise(r => setTimeout(r, 3000));

  // ---- 第二次会话（关键）----
  console.log(`--- 再次 attach（mode=${mode}）---`);
  const c2 = makeClient(exe);
  await c2.req('initialize', { protocolVersion: '2024-11-05', capabilities: {} });
  c2.req('notifications/initialized', {});
  const a2 = await c2.call('attach', { host, port });
  rec('2nd attach after ' + mode, a2.ok, { msg: a2.message && a2.message.slice(0, 120) });

  if (a2.ok) {
    const r2 = await c2.call('resume', {});
    rec('2nd resume', r2.ok, { msg: r2.message });
    const d2 = await c2.call('detach', {});
    rec('2nd detach', d2.ok, { msg: d2.message });
  }
  c2.kill();

  const pass = results.filter(r => r.ok).length;
  console.log(`\n=== ${pass}/${results.length} PASS (mode=${mode}) ===`);
  process.exit(pass === results.length ? 0 : 1);
})().catch(e => { console.error('FATAL', e); process.exit(2); });

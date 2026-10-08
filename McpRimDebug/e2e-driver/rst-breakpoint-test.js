#!/usr/bin/env node
/**
 * rst-breakpoint-test.js — 断点功能回归：确认 RST 改动未破坏正常调试。
 * attach → resume → find_methods(Root_Entry:Update 类) → break_add → resume → wait(breakpoint) → detach
 * 用法: node rst-breakpoint-test.js <exe> <port>
 */
'use strict';
const { spawn } = require('child_process');
const readline = require('readline');

const exe = process.argv[2];
const port = parseInt(process.argv[3], 10);
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
    req(method, params, timeoutMs = 20000) {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        const t = setTimeout(() => { pending.delete(id); reject(new Error(`timeout ${method}`)); }, timeoutMs);
        pending.set(id, (m) => { clearTimeout(t); resolve(m); });
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params: params || {} }) + '\n');
      });
    },
    async call(tool, args, timeoutMs = 20000) {
      const r = await this.req('tools/call', { name: tool, arguments: args || {} }, timeoutMs);
      const content = (r.result && r.result.content || []).filter(c => c.type === 'text').map(c => c.text).join('');
      let parsed; try { parsed = JSON.parse(content); } catch { parsed = null; }
      return { ok: parsed && parsed.ok === true, message: parsed ? parsed.message : content, data: parsed && parsed.data };
    },
    kill() { try { child.kill('SIGKILL'); } catch {} }
  };
}

(async () => {
  const results = [];
  const rec = (name, ok, extra) => { results.push({ name, ok, ...extra }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? ' ' + JSON.stringify(extra).slice(0, 200) : ''}`); };

  const c = makeClient(exe);
  await c.req('initialize', { protocolVersion: '2024-11-05', capabilities: {} });
  c.req('notifications/initialized', {});

  const a = await c.call('attach', { host, port });
  rec('attach', a.ok, { msg: a.message && a.message.slice(0, 60) });
  const r = await c.call('resume', {});
  rec('resume', r.ok, { msg: r.message });

  // find_methods 找 Root_Entry:Update（高频主循环方法）
  const fm = await c.call('find_methods', { query: 'Root_Entry:Update', limit: 10 }, 60000);
  const methods = fm.ok && Array.isArray(fm.data && fm.data.methods) ? fm.data.methods : [];
  const target = methods.find(m => (m.type === 'Verse.Root_Entry' || (m.type || '').endsWith('Root_Entry')) && /Update/i.test(m.method || m.name || ''));
  rec('find_methods Root_Entry:Update', !!target, { found: methods.length, sample: methods.slice(0, 3) });

  if (target) {
    const spec = `${target.type}:${target.method || target.name}`;
    const ba = await c.call('break_add', { method: spec }, 40000);
    rec('break_add ' + spec, ba.ok, { msg: ba.message && ba.message.slice(0, 80), id: ba.data && (ba.data.id ?? ba.data.breakpointId) });
    const bpId = ba.ok && (ba.data && (ba.data.id ?? ba.data.breakpointId));

    // resume 后断点应命中（Root_Entry.Update 高频）
    await c.call('resume', {}, 30000);
    const w = await c.call('wait', { eventType: 'breakpoint', timeoutMs: 20000 }, 35000);
    rec('wait breakpoint hit', w.ok && w.data && !w.data.timeout, { msg: w.message && w.message.slice(0, 80), evt: w.data && w.data.eventType });

    if (bpId !== undefined && bpId !== null) {
      await c.call('break_remove', { id: bpId }, 30000);
    }
    await c.call('resume', {}, 30000);
  }

  const d = await c.call('detach', {}, 30000);
  rec('detach', d.ok, { msg: d.message });
  c.kill();

  const pass = results.filter(r => r.ok).length;
  console.log(`\n=== ${pass}/${results.length} PASS ===`);
  process.exit(pass === results.length ? 0 : 1);
})().catch(e => { console.error('FATAL', e); process.exit(2); });

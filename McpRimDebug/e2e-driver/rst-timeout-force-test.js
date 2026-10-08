#!/usr/bin/env node
/**
 * rst-timeout-force-test.js — 验证 ForceDisconnectForTimeout（看门狗超时强断）走 RST 后能再次 attach。
 * attach 后调用一个会超时的 VM 命令（suspend 后不发 resume 不算；用 eval 卡死不可控）。
 * 实际做法：attach 成功后调用 find_methods 超大范围或依赖超时的操作不现实。
 * 替代：直接验证 "attach → 进程内 Detach()（走 VM_Dispose + RST-Close）→ 再次 attach" 已在 clean 测试覆盖。
 * 本脚本验证更贴近真实异常断开的场景：attach → resume 后立即 SIGTERM（进程收到终止信号，.NET 默认 Environment.Exit 不走 detach）
 * → 检查残留 → 再次 attach。
 * 用法: node rst-timeout-force-test.js <exe> <port>
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
    async call(tool, args) {
      const r = await this.req('tools/call', { name: tool, arguments: args || {} });
      const content = (r.result && r.result.content || []).filter(c => c.type === 'text').map(c => c.text).join('');
      let parsed; try { parsed = JSON.parse(content); } catch { parsed = null; }
      return { ok: parsed && parsed.ok === true, message: parsed ? parsed.message : content, data: parsed && parsed.data };
    },
    kill(sig) { try { child.kill(sig || 'SIGKILL'); } catch {} }
  };
}

(async () => {
  const results = [];
  const rec = (name, ok, extra) => { results.push({ name, ok, ...extra }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${extra ? ' ' + JSON.stringify(extra) : ''}`); };

  // 1st session: attach + resume
  const c1 = makeClient(exe);
  await c1.req('initialize', { protocolVersion: '2024-11-05', capabilities: {} });
  c1.req('notifications/initialized', {});
  const a1 = await c1.call('attach', { host, port });
  rec('1st attach', a1.ok, { msg: a1.message && a1.message.slice(0, 60) });
  const r1 = await c1.call('resume', {});
  rec('1st resume', r1.ok, { msg: r1.message });

  // SIGTERM（优雅终止信号，.NET 默认进程退出不执行 detach —— 模拟崩溃/被杀）
  console.log('--- SIGTERM 调试器（模拟进程被杀，不走 detach）---');
  c1.kill('SIGTERM');
  await new Promise(r => setTimeout(r, 3000));

  // 2nd attach
  console.log('--- 再次 attach ---');
  const c2 = makeClient(exe);
  await c2.req('initialize', { protocolVersion: '2024-11-05', capabilities: {} });
  c2.req('notifications/initialized', {});
  const a2 = await c2.call('attach', { host, port });
  rec('2nd attach after SIGTERM', a2.ok, { msg: a2.message && a2.message.slice(0, 100) });
  if (a2.ok) {
    await c2.call('resume', {});
    await c2.call('detach', {});
  }
  c2.kill();

  const pass = results.filter(r => r.ok).length;
  console.log(`\n=== ${pass}/${results.length} PASS ===`);
  process.exit(pass === results.length ? 0 : 1);
})().catch(e => { console.error('FATAL', e); process.exit(2); });

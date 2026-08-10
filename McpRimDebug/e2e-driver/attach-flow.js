// attach-flow.js — 对已运行（listen 中）的游戏执行 attach→resume→wait→detach，不做任何前置探测
// 用法: node attach-flow.js <serverExe>
'use strict';
const { spawn } = require('child_process');
const readline = require('readline');
const exe = process.argv[2];
const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
const rl = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;
rl.on('line', (line) => {
  const t = line.trim();
  if (!t) return;
  try {
    const msg = JSON.parse(t);
    if (msg.id !== undefined && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  } catch (e) { /* non-json */ }
});
function req(method, params, timeoutMs) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => { pending.delete(id); reject(new Error('timeout ' + method)); }, timeoutMs);
    pending.set(id, (m) => { clearTimeout(t); resolve(m); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params: params || {} }) + '\n');
  });
}
async function call(tool, args, timeoutMs) {
  const r = await req('tools/call', { name: tool, arguments: args || {} }, timeoutMs || 30000);
  if (r.error) return { error: r.error };
  return JSON.parse(r.result.content[0].text);
}
(async () => {
  await req('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'attach-flow', version: '1' } }, 10000);
  console.log('attach ...');
  let a = await call('attach', { host: '127.0.0.1', port: 56574 }, 40000);
  console.log('attach:', JSON.stringify(a));
  if (a.ok) {
    console.log('resume ...');
    console.log('resume:', JSON.stringify(await call('resume', {}, 30000)));
    console.log('wait(any,15000) ...');
    console.log('wait:', JSON.stringify(await call('wait', { eventType: 'any', timeoutMs: 15000 }, 30000)));
    console.log('detach ...');
    console.log('detach:', JSON.stringify(await call('detach', {}, 30000)));
  }
  child.kill();
  process.exit(0);
})().catch((e) => { console.error(e); child.kill(); process.exit(2); });

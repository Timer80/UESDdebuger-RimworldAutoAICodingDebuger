// observe4.js — 验证假设：status 的 TCP 探测是否杀死 suspend=y 的游戏
// launch 后不调用 status（无探测连接），仅用 netstat 查端口 + process.kill 查存活
// 用法: node observe4.js <serverExe> [秒数=15] [--with-probe]
'use strict';
const { spawn, execSync } = require('child_process');
const readline = require('readline');
const exe = process.argv[2];
const seconds = parseInt(process.argv[3] || '15', 10);
const withProbe = process.argv.includes('--with-probe');

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
function alive(pid) {
  try { process.kill(pid, 0); return true; }
  catch (e) { return e.code === 'EPERM'; }
}
function portListening() {
  try {
    const out = execSync('netstat -ano | findstr "56574 LISTENING"', { encoding: 'utf8' });
    return out.trim().length > 0;
  } catch (e) { return false; }
}
(async () => {
  await req('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'observe4', version: '1' } }, 10000);
  const lp = await req('tools/call', { name: 'launch', arguments: {} }, 30000);
  const lpp = JSON.parse(lp.result.content[0].text);
  console.log('launch:', JSON.stringify(lpp), '| withProbe=' + withProbe);
  const pid = lpp.Data && lpp.Data.pid;
  const t0 = Date.now();
  while (Date.now() - t0 < seconds * 1000) {
    const now = Date.now();
    const al = alive(pid);
    const pl = portListening();
    console.log(`T+${now - t0}ms alive=${al} port=${pl}`);
    if (!al) { console.log('>>> 进程已死'); break; }
    if (withProbe && now - t0 > 500) {
      // 模拟一次 status 探测
      console.log('>>> 调用 status（TCP 探测）...');
      try {
        const r = await req('tools/call', { name: 'status', arguments: {} }, 15000);
        const sp = JSON.parse(r.result.content[0].text);
        console.log('status:', JSON.stringify(sp));
      } catch (e) { console.log('status 异常: ' + e.message); }
      withProbe = false; // 只探测一次
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  child.kill();
  process.exit(0);
})().catch((e) => { console.error(e); child.kill(); process.exit(2); });

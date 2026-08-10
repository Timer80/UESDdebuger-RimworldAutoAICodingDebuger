// observe.js — 观察 launch 后游戏进程是否自行存活（不 attach）
// 用法: node observe.js <serverExe> [秒数=30]
'use strict';
const { spawn } = require('child_process');
const readline = require('readline');
const exe = process.argv[2];
const seconds = parseInt(process.argv[3] || '30', 10);

const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
const rl = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;
rl.on('line', (line) => {
  try {
    const msg = JSON.parse(line);
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
(async () => {
  await req('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'observe', version: '1' } }, 10000);
  const lp = await req('tools/call', { name: 'launch', arguments: {} }, 30000);
  const payload = JSON.parse(lp.result.content[0].text);
  console.log('launch:', JSON.stringify(payload));
  const pid = payload.Data && payload.Data.pid;
  const t0 = Date.now();
  let lastAlive = null;
  while (Date.now() - t0 < seconds * 1000) {
    await new Promise((r) => setTimeout(r, 1000));
    const st = await req('tools/call', { name: 'status', arguments: {} }, 15000);
    const sp = JSON.parse(st.result.content[0].text);
    const alive = sp.Data && sp.Data.gameProcessRunning === true;
    if (alive !== lastAlive || alive) {
      const ms = Date.now() - t0;
      if (alive) {
        const proc = require('child_process').execSync('tasklist /FI "PID eq ' + pid + '" /FO CSV /NH').toString().trim();
        console.log(`T+${ms}ms 游戏存活: ${proc ? proc.split(',')[0] : '?'}`);
      } else {
        console.log(`T+${ms}ms 游戏进程消失 (gameProcessRunning=false)`);
      }
    }
    lastAlive = alive;
  }
  child.kill();
  process.exit(0);
})().catch((e) => { console.error(e); child.kill(); process.exit(2); });

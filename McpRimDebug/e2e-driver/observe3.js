// observe3.js — 服务器 launch 后逐秒用 tasklist 验证进程真实存活 + 端口 + 尝试 attach
// 用法: node observe3.js <serverExe> [秒数=25]
'use strict';
const { spawn, execSync } = require('child_process');
const readline = require('readline');
const exe = process.argv[2];
const seconds = parseInt(process.argv[3] || '25', 10);

const child = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
const rl = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;
const nonJson = [];
rl.on('line', (line) => {
  const t = line.trim();
  if (!t) return;
  try {
    const msg = JSON.parse(t);
    if (msg.id !== undefined && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  } catch (e) { nonJson.push(t); }
});
function req(method, params, timeoutMs) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => { pending.delete(id); reject(new Error('timeout ' + method)); }, timeoutMs);
    pending.set(id, (m) => { clearTimeout(t); resolve(m); });
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params: params || {} }) + '\n');
  });
}
function tasklistAlive(pid) {
  // Node 原生信号 0 探测：进程存在返回 true
  try { process.kill(pid, 0); return true; }
  catch (e) { return e.code === 'EPERM'; }
}
(async () => {
  await req('initialize', { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'observe3', version: '1' } }, 10000);
  const lp = await req('tools/call', { name: 'launch', arguments: {} }, 30000);
  const lpp = JSON.parse(lp.result.content[0].text);
  console.log('launch:', JSON.stringify(lpp));
  const pid = lpp.Data && lpp.Data.pid;
  const t0 = Date.now();
  let attached = false;
  while (Date.now() - t0 < seconds * 1000) {
    const now = Date.now();
    let tl = null, port = null, st = null;
    try { tl = tasklistAlive(pid); } catch (e) {}
    try {
      const p = execSync('netstat -ano | findstr ":' + '56574' + ' LISTENING"', { encoding: 'utf8' });
      port = p.trim().length > 0;
    } catch (e) { port = false; }
    try {
      const r = await req('tools/call', { name: 'status', arguments: {} }, 15000);
      st = JSON.parse(r.result.content[0].text);
    } catch (e) { st = null; }
    const srv = st && st.Data ? st.Data.gameProcessRunning : '?';
    console.log(`T+${now - t0}ms tasklist=${tl} port=${port} status.gameRunning=${srv}`);
    if (!tl) { console.log('>>> tasklist 确认进程已死'); break; }
    // 端口开放后延迟 3s 再 attach（等代理就绪）
    if (port && !attached && (now - t0) > 3000) {
      attached = true;
      console.log('尝试 attach ...');
      try {
        const ar = await req('tools/call', { name: 'attach', arguments: { host: '127.0.0.1', port: 56574 } }, 30000);
        const ap = JSON.parse(ar.result.content[0].text);
        console.log('attach 结果:', JSON.stringify(ap));
        if (ap.ok) {
          await req('tools/call', { name: 'resume', arguments: {} }, 30000).then((r) => console.log('resume:', JSON.stringify(JSON.parse(r.result.content[0].text))));
        }
      } catch (e) { console.log('attach 异常: ' + e.message); }
      break;
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  console.log('nonJson(游戏stdout透传):', JSON.stringify(nonJson.slice(0, 10)));
  child.kill();
  process.exit(0);
})().catch((e) => { console.error(e); child.kill(); process.exit(2); });

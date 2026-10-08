'use strict';
// 真实负载压测：持续推流（15fps、混合分块、多帧），观察 capture 进程是否卡死/不退出。
const { spawn } = require('child_process');
const dgram = require('dgram');
const fs = require('fs');
const path = require('path');

const PORT = 5011;
const MAGIC = Buffer.from('CAM', 'ascii');
const CHUNK = 59990;
const OUT_DIR = 'C:\\tmp\\load-test';
const SCRIPT = 'C:\\SteamLibrary\\steamapps\\common\\RimWorld\\Mods\\UESDdebuger\\MCP\\stream-capture\\capture-udp-frames.cjs';
const FRAMES = 60;          // 推 60 帧，15fps → 4 秒
const TIMEOUT = 20000;      // 总超时

fs.rmSync(OUT_DIR, { recursive: true, force: true });
fs.mkdirSync(OUT_DIR, { recursive: true });

function datagram(payload, idx, total) {
  const len = Buffer.alloc(4);
  len.writeInt32LE(payload.length, 0);
  return Buffer.concat([MAGIC, len, Buffer.from([idx, total]), payload]);
}
function makeJpg(size) {
  const j = Buffer.alloc(size);
  j[0] = 0xFF; j[1] = 0xD8;
  for (let i = 2; i < size - 2; i++) j[i] = (i * 31 + (i % 251)) & 0xFF;
  j[size - 2] = 0xFF; j[size - 1] = 0xD9;
  return j;
}

const sendSock = dgram.createSocket('udp4');
function sendChunkedSync(img) {
  const total = Math.ceil(img.length / CHUNK);
  for (let i = 0; i < total; i++) {
    const p = img.subarray(i * CHUNK, Math.min((i + 1) * CHUNK, img.length));
    sendSock.send(datagram(p, i, total), PORT, '127.0.0.1');
  }
  return total;
}

// 混合负载：单包 / 3块 / 4块 / 2块 轮流，接近真实场景大小分布
function frameSize(i) {
  const patterns = [3000, 59990 + 800, 59990 * 3 + 5000, 59990 * 2 + 123, 59990 + 123, 59990 * 4 + 500];
  return patterns[i % patterns.length];
}

const recv = spawn('node', [SCRIPT, '--host', '127.0.0.1', '--port', String(PORT), '--out', OUT_DIR, '--idle-timeout-ms', '3000', '--max-frames', '200'], { stdio: ['pipe', 'pipe', 'pipe'] });
let stderr = '';
recv.stderr.on('data', d => { stderr += d; });
let recvExited = false;

const overallTimer = setTimeout(() => {
  console.log('\n🔥 超时未收到接收进程退出事件 —— 可能死循环/卡住！');
  console.log('stderr tail: ' + stderr.slice(-500));
  try { recv.kill('SIGKILL'); } catch (_e) {}
  process.exit(2);
}, TIMEOUT);

recv.on('exit', (code, signal) => {
  recvExited = true;
  clearTimeout(overallTimer);
  console.log('\n--- 接收进程退出 code=' + code + ' signal=' + signal + ' ---');
  const files = fs.readdirSync(OUT_DIR).filter(f => f.endsWith('.jpg')).sort();
  let complete = 0, total = 0, truncated = 0;
  for (const f of files) {
    const buf = fs.readFileSync(path.join(OUT_DIR, f));
    total++;
    const soi = buf[0] === 0xFF && buf[1] === 0xD8;
    const eoi = buf[buf.length - 2] === 0xFF && buf[buf.length - 1] === 0xD9;
    if (soi && eoi) complete++; else { truncated++; console.log('  ❌ 截断: ' + f + ' size=' + buf.length + ' EOI=' + eoi); }
  }
  console.log('产出帧: ' + total + '  完整: ' + complete + '  截断: ' + truncated + '  (期望 ' + FRAMES + ' 帧完整)');
  console.log((complete === FRAMES) ? '\n🎉 PASS 无死循环，全部完整' : '\n⚠️ 部分帧未完整或未产出');
  sendSock.close();
  process.exit(complete === FRAMES ? 0 : 1);
});

// 等绑定后开始推流
setTimeout(() => {
  let i = 0;
  function next() {
    if (i >= FRAMES) { console.log('[sender] 已发完 ' + FRAMES + ' 帧，等接收端收尾'); return; }
    const img = makeJpg(frameSize(i));
    const chunks = sendChunkedSync(img);
    if (i % 20 === 0) console.log('[sender] frame#' + i + ' size=' + img.length + ' chunks=' + chunks);
    i++;
    setTimeout(next, 66); // 15fps
  }
  next();
}, 1500);

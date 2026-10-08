#!/usr/bin/env node
/**
 * RIMAPI UDP 视频流抓帧器
 * ------------------------------------
 * 监听 RIMAPI 的 UDP 视频流端口，把每个数据报里的 JPEG 帧保存成文件序列。
 *
 * 帧格式（RIMAPI UdpCameraStream，一帧可能被拆成多个数据报）：
 *   "CAM"(3B) + 本块长度 int32LE(4B) + chunkIndex(1B) + totalChunks(1B) + JPEG负载
 *
 * 重组（reassemble.cjs）：
 *   以 chunkIndex==0 开启新帧，累计各块，收齐 totalChunks 个块后拼出完整 JPEG 写盘。
 *   非 CAM 数据报回退到扫描首个 FFD8 成帧（向后兼容旧单包流）。
 *
 * 运行：
 *   node capture-udp-frames.cjs [--host HOST] [--port PORT] [--out DIR]
 *       [--idle-timeout-ms MS] [--max-frames N]
 *       [--stdin-stop]       启用 stdin 关闭作停止信号（父进程关闭本进程 stdin 收尾）
 *       [--stop-mark FILE]   兼容旧行为：轮询指定标记文件作停止信号
 *
 * 结束条件（任一满足）：
 *   - stdin 关闭（--stdin-stop 时，优雅收尾）
 *   - 停止标记文件出现（--stop-mark 时）
 *   - 静默超时（--idle-timeout-ms，缺省 15s）
 *   - 达到 max-frames
 *   - 进程收到 SIGINT/SIGTERM
 */
'use strict';

const dgram = require('dgram');
const fs = require('fs');
const path = require('path');
const { FrameReassembler } = require('./reassemble.cjs');

function parseArgs(argv) {
  const args = { host: '127.0.0.1', port: 5007, out: null, stopMark: null,
                 stdinStop: false, idleTimeoutMs: 15000, maxFrames: Infinity };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    const next = () => argv[++i];
    if (a === '--host') args.host = next();
    else if (a === '--port') args.port = parseInt(next(), 10);
    else if (a === '--out') args.out = next();
    else if (a === '--stop-mark') args.stopMark = next();
    else if (a === '--stdin-stop') args.stdinStop = true;
    else if (a === '--idle-timeout-ms') args.idleTimeoutMs = parseInt(next(), 10);
    else if (a === '--max-frames') args.maxFrames = parseInt(next(), 10);
  }
  if (!args.out) throw new Error('--out DIR 必须指定');
  return args;
}

function finish(socket, resolve, reason) {
  try { socket.close(); } catch (_) {}
  process.stderr.write(`[capture] stopped: ${reason}\n`);
  try { resolve(); } catch (_) {}
}

async function main() {
  const args = parseArgs(process.argv.slice(2));

  fs.mkdirSync(args.out, { recursive: true });
  const stopMark = args.stopMark ? path.resolve(args.stopMark) : null;
  if (stopMark && fs.existsSync(stopMark)) {
    try { fs.unlinkSync(stopMark); } catch (_) {}
  }

  const reassembler = new FrameReassembler({ timeoutMs: 400 });
  const socket = dgram.createSocket('udp4');
  let frames = 0;
  let startTime = Date.now();
  let lastPacketAt = Date.now();
  let stopped = false;
  const saved = [];

  await new Promise((resolve, reject) => {
    socket.on('error', (e) => { if (!stopped) reject(e); });

    socket.on('message', (msg) => {
      lastPacketAt = Date.now();
      const res = reassembler.push(msg);
      if (res.type === 'dropped') return;
      if (res.type === 'pending') return;
      // completed：res.jpg 是一张完整 JPEG（重组或旧单包）
      frames++;
      const name = 'frame_' + String(frames).padStart(7, '0') + '.jpg';
      const file = path.join(args.out, name);
      fs.writeFileSync(file, res.jpg);
      saved.push(file);
      if (frames % 50 === 0) process.stderr.write(`[capture] ${frames} frames\n`);
      if (frames >= args.maxFrames) {
        stopped = true;
        finish(socket, resolve, 'max-frames reached');
      }
    });

    socket.bind(args.port, args.host, () => {
      // 必须在 bind 之后设置才生效。增大 UDP 接收缓冲区：RIMAPI 一帧拆成多个
      // ~60KB chunk 会在同步循环里连发，默认 SO_RCVBUF(64KB) 装不下多块，导致大帧丢包。
      try { socket.setRecvBufferSize(8 * 1024 * 1024); } catch (_e) { /* 平台不支持时忽略 */ }
      process.stderr.write(`[capture] listening udp ${args.host}:${args.port} -> ${args.out}\n`);
    });

    // idle watchdog
    const idleTimer = setInterval(() => {
      if (stopped) { clearInterval(idleTimer); return; }
      const idle = Date.now() - lastPacketAt;
      if (idle > args.idleTimeoutMs) {
        stopped = true;
        finish(socket, resolve, `idle ${idle}ms > ${args.idleTimeoutMs}ms`);
      }
    }, 1000);

    // stdin close as stop signal (--stdin-stop): parent closes stdin -> EOF -> graceful stop
    if (args.stdinStop && process.stdin) {
      // 必须先让 stdin 流流动（resume），否则 pipe 数据不被排空、不会触发 'end'/'close'
      try { process.stdin.resume(); } catch (_e) { /* 忽略 */ }
      const onEnd = () => {
        if (stopped) return;
        stopped = true;
        finish(socket, resolve, 'stdin closed');
      };
      // 'end' resolves when stdin reaches EOF; 'close' also covers edge cases
      process.stdin.on('end', onEnd);
      process.stdin.on('close', onEnd);
    }

    // stop-mark watcher (--stop-mark, backward compat)
    const markTimer = setInterval(() => {
      if (stopped) { clearInterval(markTimer); return; }
      if (stopMark && fs.existsSync(stopMark)) {
        stopped = true;
        finish(socket, resolve, 'stop-mark detected');
      }
    }, 500);

    const onSig = () => {
      if (stopped) return;
      stopped = true;
      finish(socket, resolve, 'signal');
    };
    process.on('SIGINT', onSig);
    process.on('SIGTERM', onSig);
  });

  const durSec = ((Date.now() - startTime) / 1000).toFixed(2);
  let totalBytes = 0;
  for (const f of saved) totalBytes += fs.statSync(f).size;

  const result = {
    ok: true,
    frames,
    durationSec: parseFloat(durSec),
    outputDir: path.resolve(args.out),
    totalBytes,
    totalMB: parseFloat((totalBytes / (1024 * 1024)).toFixed(2)),
    start: new Date(startTime).toISOString(),
    end: new Date().toISOString()
  };
  process.stdout.write('\n[[CAPTURE_RESULT]]\n' + JSON.stringify(result, null, 2) + '\n');
}

if (require.main === module) {
  main().catch((e) => {
    process.stderr.write('[capture] FATAL: ' + (e && e.stack || e) + '\n');
    process.exit(1);
  });
}

module.exports = { main };

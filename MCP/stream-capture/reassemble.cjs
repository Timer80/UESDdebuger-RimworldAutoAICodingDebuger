'use strict';

// RIMAPI UDP 帧协议解析 + 分块重组（纯逻辑，无 I/O）。
// 协议（发送端 RimworldRestApi/Camera/UdpCameraStream.cs CreateDataPacket）：
//   "CAM"(3B) + 本块长度 int32LE(4B) + chunkIndex(1B) + totalChunks(1B) + 负载(≤59990B)
const MAGIC = Buffer.from('CAM', 'ascii');
const HEADER = 9;             // 3 + 4 + 1 + 1
const DEFAULT_TIMEOUT = 400;  // ms

// 解析一个 CAM 数据报；非法或非 CAM 返回 { valid:false }。
function parseCamDatagram(buf) {
  if (!buf || buf.length < HEADER || !buf.subarray(0, 3).equals(MAGIC)) {
    return { valid: false };
  }
  const chunkLen = buf.readInt32LE(3);
  const chunkIndex = buf[7];
  const totalChunks = buf[8];
  if (chunkLen < 0 || HEADER + chunkLen > buf.length) {
    return { valid: false };
  }
  return {
    valid: true,
    chunkIndex,
    totalChunks,
    chunk: buf.subarray(HEADER, HEADER + chunkLen),
  };
}

// 旧单包兜底：扫描 FFD8 起返回剩余字节；否则 -1。
function findJPEGStart(buf) {
  for (let i = 0; i < buf.length - 1; i++) {
    if (buf[i] === 0xFF && buf[i + 1] === 0xD8) return i;
  }
  return -1;
}

class FrameReassembler {
  constructor({ timeoutMs = DEFAULT_TIMEOUT } = {}) {
    this.timeoutMs = timeoutMs;
    this.cur = null;    // { chunks:[], total, received, startedAt }
    this._dropped = 0;
  }

  // push(msg [, now]) -> { type:'completed', jpg, legacy? } | { type:'pending' } | { type:'dropped' }
  push(msg, now = Date.now()) {
    const parsed = parseCamDatagram(msg);
    // 旧单包 / 未知包：回退到 FFD8 扫描整包成帧
    if (!parsed.valid) {
      const off = findJPEGStart(msg);
      if (off >= 0) return { type: 'completed', jpg: msg.subarray(off), legacy: true };
      return { type: 'dropped' };
    }
    // 超时清理未完成帧
    if (this.cur && now - this.cur.startedAt > this.timeoutMs) {
      this._dropped++;
      this.cur = null;
    }
    // chunkIndex==0 开启新帧；若上一帧未收齐，丢弃它
    if (parsed.chunkIndex === 0) {
      if (this.cur && this.cur.received !== this.cur.total) this._dropped++;
      if (parsed.totalChunks < 1) return { type: 'dropped' };
      this.cur = {
        chunks: new Array(parsed.totalChunks),
        total: parsed.totalChunks,
        received: 0,
        startedAt: now,
      };
    }
    if (!this.cur) return { type: 'dropped' };
    if (parsed.chunkIndex >= this.cur.total) return { type: 'dropped' };
    if (this.cur.chunks[parsed.chunkIndex] === undefined) {
      this.cur.chunks[parsed.chunkIndex] = parsed.chunk;
      this.cur.received++;
    }
    if (this.cur.received === this.cur.total) {
      const jpg = Buffer.concat(this.cur.chunks);
      this.cur = null;
      return { type: 'completed', jpg };
    }
    return { type: 'pending' };
  }

  stats() { return { dropped: this._dropped }; }
}

module.exports = { FrameReassembler, parseCamDatagram, findJPEGStart, HEADER };

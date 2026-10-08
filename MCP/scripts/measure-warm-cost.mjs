#!/usr/bin/env node
/**
 * 热往返基线：单次连接内连调 N 次，量常驻 MCP 的相对单价。
 * 与 measure-cli-cost.mjs（每调用一次就冷启进程）对比。
 *
 * 用法：node scripts/measure-warm-cost.mjs [N] [http://127.0.0.1:3000/sse]
 */

const n = Number(process.argv[2] || 5);
const url = process.argv[3] || 'http://127.0.0.1:3000/sse';

const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
const { SSEClientTransport } = await import('@modelcontextprotocol/sdk/client/sse.js');

const client = new Client({ name: 'measure-warm-cost', version: '1.0.0' });
const withTimeout = (p, ms, label) => Promise.race([
  p,
  new Promise((_, rej) => setTimeout(() => rej(new Error(`${label} 超时 ${ms}ms`)), ms).unref?.()),
]);

const t0 = Date.now();
await withTimeout(client.connect(new SSEClientTransport(new URL(url))), 10000, 'connect');
const tConnect = Date.now() - t0;

const times = [];
for (let i = 0; i < n; i++) {
  const t = Date.now();
  await withTimeout(client.callTool({ name: 'get_game_status', arguments: {} }), 15000, 'callTool');
  times.push(Date.now() - t);
}
client.close().catch(() => {});

console.log(`connect              : ${tConnect} ms`);
console.log(`每次 callTool 耗时   : ${times.map((x) => x + 'ms').join(', ')}`);
console.log(`平均（不含 connect） : ${Math.round(times.reduce((a, b) => a + b, 0) / times.length)} ms`);

process.exit(0);

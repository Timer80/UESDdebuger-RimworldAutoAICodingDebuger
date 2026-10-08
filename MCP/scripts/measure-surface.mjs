#!/usr/bin/env node
/**
 * 调用侧体积测量：连运行中的 SSE 服务器，量 tools/list 的真实载荷。
 *
 * 用途：回答"工具面到底给模型塞了多少字节"——这是决定 MCP 是否该退化成 CLI 的
 * 唯一硬指标（每轮对话都要付这份钱）。
 *
 * 前提：服务器以 SSE 启动（MCP/start-mcp.ps1，默认 127.0.0.1:3000/sse）。
 * 用法：node scripts/measure-surface.mjs [http://127.0.0.1:3000/sse]
 */

const url = process.argv[2] || 'http://127.0.0.1:3000/sse';

const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
const { SSEClientTransport } = await import('@modelcontextprotocol/sdk/client/sse.js');

const client = new Client({ name: 'measure-surface', version: '1.0.0' });

const withTimeout = (p, ms, label) => Promise.race([
  p,
  new Promise((_, rej) => setTimeout(() => rej(new Error(`${label} 超时 ${ms}ms`)), ms).unref?.()),
]);

const t0 = Date.now();
await withTimeout(client.connect(new SSEClientTransport(new URL(url))), 10000, 'connect');
const connectedMs = Date.now() - t0;

const t1 = Date.now();
const { tools } = await withTimeout(client.listTools(), 10000, 'listTools');
const listMs = Date.now() - t1;
client.close().catch(() => {});

// 逐工具量 name / description / inputSchema 三部分，找出谁在吃预算
const rows = tools.map((t) => ({
  name: t.name,
  total: JSON.stringify(t).length,
  nameLen: t.name.length,
  descLen: (t.description || '').length,
  schemaLen: JSON.stringify(t.inputSchema || {}).length,
}));
const sum = (k) => rows.reduce((a, r) => a + r[k], 0);
const payload = JSON.stringify({ tools });

console.log(`服务器        : ${url}`);
console.log(`连接耗时      : ${connectedMs} ms`);
console.log(`listTools耗时 : ${listMs} ms`);
console.log('');
console.log(`直接暴露工具数 : ${tools.length}`);
console.log(`tools/list 载荷: ${payload.length.toLocaleString()} 字符 (~${Math.round(payload.length / 3.5).toLocaleString()} tokens, 按 3.5 字符/token 估)`);
console.log(`  其中 name        : ${sum('nameLen').toLocaleString()}`);
console.log(`  其中 description : ${sum('descLen').toLocaleString()}  (${Math.round(sum('descLen') / payload.length * 100)}%)`);
console.log(`  其中 inputSchema : ${sum('schemaLen').toLocaleString()}  (${Math.round(sum('schemaLen') / payload.length * 100)}%)`);
console.log('');
console.log('最占预算的 10 个工具：');
for (const r of [...rows].sort((a, b) => b.total - a.total).slice(0, 10)) {
  console.log(`  ${String(r.total).padStart(7)}  ${r.name}  (desc ${r.descLen} / schema ${r.schemaLen})`);
}

process.exit(0);

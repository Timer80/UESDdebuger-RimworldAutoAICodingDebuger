#!/usr/bin/env node
/**
 * CLI 式一次性调用成本测量：进程启动 → 连接 → 调一次工具 → 退出。
 *
 * 用途：这是"把 MCP 改成 CLI"必须付的单价。与常驻 MCP 的热往返
 * （同进程内一次 tools/call，无进程启动）对比，才能判断退化是否值得。
 *
 * 用法：node scripts/measure-cli-cost.mjs [http://127.0.0.1:3000/sse]
 */

const url = process.argv[2] || 'http://127.0.0.1:3000/sse';
const t0 = Date.now();

const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
const { SSEClientTransport } = await import('@modelcontextprotocol/sdk/client/sse.js');

const client = new Client({ name: 'measure-cli-cost', version: '1.0.0' });
const withTimeout = (p, ms, label) => Promise.race([
  p,
  new Promise((_, rej) => setTimeout(() => rej(new Error(`${label} 超时 ${ms}ms`)), ms).unref?.()),
]);

const tImport = Date.now() - t0;
const t1 = Date.now();
await withTimeout(client.connect(new SSEClientTransport(new URL(url))), 10000, 'connect');
const tConnect = Date.now() - t1;

const t2 = Date.now();
const res = await withTimeout(client.callTool({ name: 'get_game_status', arguments: {} }), 15000, 'callTool');
const tCall = Date.now() - t2;
client.close().catch(() => {});

const body = JSON.stringify(res);
console.log(`import SDK        : ${tImport} ms`);
console.log(`connect           : ${tConnect} ms`);
console.log(`callTool          : ${tCall} ms`);
console.log(`进程内累计(不含node启动) : ${Date.now() - t0} ms`);
console.log(`返回载荷           : ${body.length} 字符`);

process.exit(0);

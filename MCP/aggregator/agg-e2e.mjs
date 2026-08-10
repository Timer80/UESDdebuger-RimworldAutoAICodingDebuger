#!/usr/bin/env node
/**
 * 聚合器端到端验证脚本（agg-e2e.mjs）
 * - 用 MCP SDK Client 连接 http://127.0.0.1:3100/sse
 * - 通过环境变量 AGG_COMPRESS 感知模式（默认视为 compress=true）
 *   - compress=true：断言 2 元工具 + 5 coreTools，共 7 个工具
 *   - compress=false：断言工具总数 >= 60 且含 start_game / get_config
 * - 每个用例打印 [PASS]/[FAIL]，任一失败 process.exit(1)
 */

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';

const BASE_URL = process.env.AGG_BASE_URL || 'http://127.0.0.1:3100';
// 默认按 config.json 的 compress=true 处理；AGG_COMPRESS=false 时切换为非压缩模式预期
const COMPRESS = process.env.AGG_COMPRESS !== 'false';

let pass = 0;
let fail = 0;

function check(cond, desc) {
  if (cond) { pass++; console.log(`[PASS] ${desc}`); }
  else { fail++; console.log(`[FAIL] ${desc}`); }
}

async function connect() {
  const transport = new SSEClientTransport(new URL(`${BASE_URL}/sse`));
  const client = new Client({ name: 'agg-e2e', version: '1.0.0' }, { capabilities: {} });
  await client.connect(transport);
  return { client, transport };
}

async function runCompressCases() {
  const { client, transport } = await connect();
  console.log('--- 压缩模式用例 (compress=true) ---');

  // 用例 1：listTools 总数 === 2 + 5，且包含全部预期工具
  const lt = await client.listTools();
  const names = lt.tools.map(t => t.name);
  check(lt.tools.length === 7, `工具总数 === 7（实际 ${lt.tools.length}）`);
  for (const n of ['agg_list_tools', 'agg_call_tool', 'start_game', 'get_game_status', 'read_log', 'tail_log', 'get_config']) {
    check(names.includes(n), `工具清单包含 ${n}`);
  }

  // 用例 2：agg_list_tools 返回文本目录
  const dir = await client.callTool({ name: 'agg_list_tools', arguments: {} });
  const dirText = (dir.content && dir.content[0] && dir.content[0].text) || '';
  check(dirText.includes('rimworld'), 'agg_list_tools 文本含 "rimworld"');
  check(dirText.includes('healthy'), 'agg_list_tools 文本含 "healthy"');

  // 用例 3：agg_call_tool 路由到上游 get_game_status（content 非空即可，游戏未运行时 isError 也可接受）
  const r3 = await client.callTool({
    name: 'agg_call_tool',
    arguments: { server: 'rimworld', tool: 'get_game_status', args: {} }
  });
  check(Array.isArray(r3.content) && r3.content.length > 0, 'agg_call_tool 路由 get_game_status 成功（content 非空）');

  // 用例 4：直接调用白名单 get_game_status
  const r4 = await client.callTool({ name: 'get_game_status', arguments: {} });
  check(Array.isArray(r4.content) && r4.content.length > 0, '白名单直调 get_game_status 成功（content 非空）');

  // 用例 5：agg_call_tool 未知工具 → isError === true
  const r5 = await client.callTool({
    name: 'agg_call_tool',
    arguments: { server: 'rimworld', tool: 'no_such_tool_xyz', args: {} }
  });
  check(r5.isError === true, 'agg_call_tool 未知工具返回 isError=true');

  // 用例 6：/health 端点
  const resp = await fetch(`${BASE_URL}/health`);
  const h = await resp.json();
  check(h.status === 'ok', 'health status==="ok"');
  check(Array.isArray(h.upstreams) && h.upstreams[0] && h.upstreams[0].name === 'rimworld', 'upstreams[0].name==="rimworld"');
  check(h.compress === true, `health compress===true（实际 ${h.compress}）`);
  check(h.toolCount === 7, `health toolCount===7（实际 ${h.toolCount}）`);

  await client.close();
  await transport.close();
}

async function runUncompressedCases() {
  const { client, transport } = await connect();
  console.log('--- 非压缩模式用例 (compress=false) ---');

  // 工具总数 >= 60，且含 start_game / get_config
  const lt = await client.listTools();
  const names = lt.tools.map(t => t.name);
  check(lt.tools.length >= 60, `工具总数 >= 60（实际 ${lt.tools.length}）`);
  check(names.includes('start_game'), '工具清单包含 start_game');
  check(names.includes('get_config'), '工具清单包含 get_config');

  // 路由调用 get_game_status 成功（content 非空）
  const r = await client.callTool({ name: 'get_game_status', arguments: {} });
  check(Array.isArray(r.content) && r.content.length > 0, '路由调用 get_game_status 成功（content 非空）');

  // health 检查：compress=false 且 toolCount 与 listTools 一致
  const resp = await fetch(`${BASE_URL}/health`);
  const h = await resp.json();
  check(h.status === 'ok', 'health status==="ok"');
  check(h.compress === false, `health compress===false（实际 ${h.compress}）`);
  check(h.toolCount === lt.tools.length, `health toolCount 与 listTools 一致（实际 ${h.toolCount} vs ${lt.tools.length}）`);

  await client.close();
  await transport.close();
}

async function main() {
  console.log(`聚合器 e2e 验证启动: AGG_COMPRESS=${process.env.AGG_COMPRESS ?? '(未设置，按 true)'}`);
  if (COMPRESS) {
    await runCompressCases();
  } else {
    await runUncompressedCases();
  }

  console.log('');
  console.log(`结果汇总: ${pass} PASS / ${fail} FAIL（compress=${COMPRESS}）`);
  if (fail > 0) process.exit(1);
  console.log('全部用例通过');
}

main().catch((e) => {
  console.error(`[FAIL] 脚本异常: ${e && e.stack ? e.stack : String(e)}`);
  process.exit(1);
});

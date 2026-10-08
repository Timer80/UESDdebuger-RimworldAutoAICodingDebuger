import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8');
const toolConfig = JSON.parse(fs.readFileSync(path.join(here, '..', 'toolConfig.json'), 'utf-8'));

// 连真实 server（InMemoryTransport），走真实被拦截的元工具路径
async function withClient(fn) {
  const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
  const { InMemoryTransport } = await import('@modelcontextprotocol/sdk/inMemory.js');
  const server = mod._testHooks.createServer();
  const [clientT, serverT] = InMemoryTransport.createLinkedPair();
  const client = new Client({ name: 'test-client', version: '1.0.0' });
  try {
    await Promise.all([server.connect(serverT), client.connect(clientT)]);
    return await fn(client);
  } finally {
    await client.close().catch(() => {});
    await server.close().catch(() => {});
  }
}

const callText = (client, name, args) =>
  client.callTool({ name, arguments: args }).then((r) => (r.content || []).map((c) => c.text || '').join(''));
const withCallText = (name, args) => withClient((client) => callText(client, name, args));

// ===========================================================================
// 热重载桥：隐藏工具（easter egg）——不引导存在，但显式调用可用且必附局限警告
// ===========================================================================

test('隐藏：agg_list_tools 可调用集里不含 hotreload_*', async () => {
  const text = await withCallText('agg_call_tool', { tool: 'agg_list_tools', args: {} });
  assert.ok(!/hotreload_/.test(text), `可调用集不应出现热重载桥，实际片段: ${text.slice(0, 300)}`);
});

test('隐藏：mcp_help 概览与 unityexplorer 叶子列表均不呈现', async () => {
  const top = await withCallText('mcp_help', {});
  assert.ok(!/hotreload/i.test(top), '一级概览不应提及热重载桥');
  const ue = await withCallText('mcp_help', { path: 'unityexplorer' });
  assert.ok(!/hotreload/i.test(ue), `unityexplorer 工具列表不应含 hotreload_*，实际: ${ue.slice(0, 300)}`);
});

test('隐藏：原二级路径 unityexplorer/hotreload 不再可达（不引导）', async () => {
  const text = await withCallText('mcp_help', { path: 'unityexplorer/hotreload' });
  assert.match(text, /叶子类别|未知/, `二级路径应被拒绝，实际: ${text.slice(0, 200)}`);
});

test('隐藏：显式按名调用仍可达，且响应附带实验性标记与局限清单', async () => {
  const text = await withCallText('agg_call_tool', { tool: 'hotreload_status', args: {} });
  assert.match(text, /"hiddenTool":\s*true/, '响应应标注为隐藏工具');
  assert.match(text, /"experimental":\s*true/, '响应应标注为实验性');
  assert.match(text, /"limitations"/, '响应应附局限清单');
  assert.match(text, /字段布局变化不覆盖/, '局限清单应点明字段布局不覆盖');
  assert.match(text, /本轮不生效/, 'notice 应说明被跳过的方法本轮不生效');
});

test('挂起防护：判据必须精确匹配 needResume 的值（禁止宽松 /true/ 误判）', () => {
  assert.ok(!/\/needResume\/\.test\(text\)\s*&&\s*\/true\/\.test\(text\)/.test(source),
    '旧实现 /true/ 会命中 attached/bridgeReady 等字段 → hotreload_apply 恒被误拒');
  assert.match(source, /"needResume"\\s\*:\\s\*\(true\|false\)/, '应精确解析 needResume 的布尔值');
});

test('隐藏：描述标注"[实验性·隐藏工具]"，且三件套在 toolConfig 默认关闭', () => {
  for (const n of ['hotreload_apply', 'hotreload_watch', 'hotreload_status']) {
    assert.equal(toolConfig.tools[n], false, `${n} 应默认关闭（不暴露在工具列表）`);
  }
  assert.match(source, /\[实验性·隐藏工具\]\[热重载桥\]/, '工具描述应标注实验性·隐藏');
  assert.match(source, /function isConcealedTool\(/, '应有 isConcealedTool 隐藏判定');
  assert.match(source, /function hotreloadWrap\(/, '应有 hotreloadWrap 局限警告包装');
});

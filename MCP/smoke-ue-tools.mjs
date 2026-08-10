// smoke-ue-tools.mjs — UE 工具恢复注册冒烟测试
// 用法: node smoke-ue-tools.mjs   （MCP 服务器需已在本机 3000 端口运行）
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';

const UE_NAMES = [
  'inspect_type', 'get_unityexplorer_status', 'clear_unityexplorer_logs',
  'get_unityexplorer_logs', 'create_hook', 'toggle_hook', 'delete_hook',
  'list_hooks', 'execute_csharp_code', 'reset_csharp_console', 'add_using_directive',
];

async function main() {
  const transport = new SSEClientTransport(new URL('http://127.0.0.1:3000/sse'));
  const client = new Client({ name: 'smoke-ue-tools', version: '1.0.0' });
  await client.connect(transport);
  console.log('=== connected ===');

  const tools = await client.listTools();
  const all = tools.tools;
  const ue = all.filter((t) => UE_NAMES.includes(t.name));
  const missing = UE_NAMES.filter((n) => !all.some((t) => t.name === n));
  console.log('total tools =', all.length);
  console.log('UE tools registered =', ue.length);
  console.log('UE tools missing =', missing.length ? missing.join(', ') : 'none');
  ue.forEach((t) => console.log('  - ' + t.name));

  // 实际调用测试：get_unityexplorer_status（只读，无副作用）
  console.log('\n=== call get_unityexplorer_status ===');
  try {
    const r = await client.callTool({ name: 'get_unityexplorer_status', arguments: {} });
    const text = (r.content || []).filter((c) => c.type === 'text').map((c) => c.text).join('');
    console.log(text || JSON.stringify(r));
  } catch (e) {
    console.log('call error:', e.message);
  }

  // 实际调用测试：execute_csharp_code 只读表达式
  console.log('\n=== call execute_csharp_code("Verse.GenTicks.TicksGame") ===');
  try {
    const r = await client.callTool({ name: 'execute_csharp_code', arguments: { code: 'Verse.GenTicks.TicksGame' } });
    const text = (r.content || []).filter((c) => c.type === 'text').map((c) => c.text).join('');
    console.log(text || JSON.stringify(r));
  } catch (e) {
    console.log('call error:', e.message);
  }

  await client.close();
  console.log('\n=== done ===');
}

main().catch((e) => { console.error('FATAL:', e); process.exit(1); });

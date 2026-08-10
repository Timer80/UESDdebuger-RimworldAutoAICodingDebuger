// mcp-ue-e2e.mjs — 11 个 UE 工具全量端到端测试（经 MCP 服务器 3000 → 游戏内 3001）
// 用法: node mcp-ue-e2e.mjs   （MCP 服务器需运行，游戏需已进入世界）
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';

const UE_NAMES = [
  'inspect_type', 'get_unityexplorer_status', 'clear_unityexplorer_logs',
  'get_unityexplorer_logs', 'create_hook', 'toggle_hook', 'delete_hook',
  'list_hooks', 'execute_csharp_code', 'reset_csharp_console', 'add_using_directive',
];

let pass = 0;
let fail = 0;

function report(name, ok, detail) {
  if (ok) { pass++; console.log(`[PASS] ${name}  ${detail || ''}`); }
  else { fail++; console.log(`[FAIL] ${name}  ${detail || ''}`); }
}

async function main() {
  const transport = new SSEClientTransport(new URL('http://127.0.0.1:3000/sse'));
  const client = new Client({ name: 'mcp-ue-e2e', version: '1.0.0' });
  await client.connect(transport);

  // 1. tools/list
  const tools = await client.listTools();
  const all = tools.tools.map(t => t.name);
  const missing = UE_NAMES.filter(n => !all.includes(n));
  report('tools/list 11 个 UE 工具注册', missing.length === 0, `total=${all.length} missing=${missing.join(',') || 'none'}`);

  async function call(name, args, timeoutMs = 60000) {
    try {
      const r = await client.callTool({ name, arguments: args || {} }, undefined, { timeout: timeoutMs });
      const text = (r.content || []).filter(c => c.type === 'text').map(c => c.text).join('');
      try { return JSON.parse(text); } catch { return { success: false, raw: text }; }
    } catch (e) {
      return { success: false, error: e.message };
    }
  }

  // 2. status
  const status = await call('get_unityexplorer_status', {});
  report('get_unityexplorer_status', status.success && status.data?.status?.uiReady === true,
    `uiReady=${status.data?.status?.uiReady}`);

  // 3. execute_csharp_code
  const eval1 = await call('execute_csharp_code', { code: 'Verse.GenTicks.TicksGame' });
  report('execute_csharp_code(TicksGame)', eval1.success && /^\d+$/.test(String(eval1.data?.result ?? '')),
    `result=${eval1.data?.result}`);

  // 4. execute_csharp_code 错误路径
  const evalBad = await call('execute_csharp_code', { code: 'this will not compile !!!' });
  report('execute_csharp_code(非法代码)→报错', !evalBad.success, evalBad.error || '');

  // 5. inspect_type
  const insp = await call('inspect_type', { typeName: 'Verse.TickManager' });
  report('inspect_type(Verse.TickManager)', insp.success, insp.data?.message || '');

  // 6. add_using_directive
  const au = await call('add_using_directive', { namespace: 'RimWorld' });
  report('add_using_directive(RimWorld)', au.success, au.data?.message || '');

  // 7. reset_csharp_console
  const rc = await call('reset_csharp_console', {});
  report('reset_csharp_console', rc.success, rc.data?.message || '');

  // 8. create_hook（游戏内主循环方法）
  const hook = await call('create_hook', {
    typeName: 'Verse.Root_Play',
    methodName: 'Update',
    patchType: 'Postfix',
  });
  const hookId = hook.data?.hookId;
  report('create_hook(Verse.Root_Play:Update)', hook.success && !!hookId, `hookId=${hookId}`);

  // 9. list_hooks
  const list1 = await call('list_hooks', {});
  const inList = (list1.data?.hooks || []).some(h => h.hookId === hookId);
  report('list_hooks 含新建 hook', list1.success && inList, `total=${list1.data?.totalCount}`);

  // 10. toggle_hook off
  const toggled = await call('toggle_hook', { hookId, enabled: false });
  report('toggle_hook(off)', toggled.success && toggled.data?.enabled === false, `enabled=${toggled.data?.enabled}`);

  // 11. delete_hook
  const del = await call('delete_hook', { hookId });
  report('delete_hook', del.success, del.data?.message || '');

  // 12. get_unityexplorer_logs
  const logs = await call('get_unityexplorer_logs', { count: 20 });
  report('get_unityexplorer_logs', logs.success && Array.isArray(logs.data?.logs), `count=${logs.data?.logs?.length}`);

  // 13. clear_unityexplorer_logs
  const clr = await call('clear_unityexplorer_logs', {});
  const logs2 = await call('get_unityexplorer_logs', { count: 5 });
  report('clear_unityexplorer_logs', clr.success && Array.isArray(logs2.data?.logs), `afterClear=${logs2.data?.logs?.length}`);

  // 14. 异常路径：不存在的类型
  const badType = await call('inspect_type', { typeName: 'No.Such.Type.Xyz' });
  report('inspect_type(不存在类型)→报错', !badType.success, badType.error || '');

  await client.close();

  console.log(`\n==== 汇总: PASS ${pass} / FAIL ${fail} ====`);
  process.exit(fail === 0 ? 0 : 1);
}

main().catch(e => { console.error('FATAL:', e); process.exit(1); });

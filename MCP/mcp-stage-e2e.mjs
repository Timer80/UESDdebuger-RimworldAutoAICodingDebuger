// mcp-stage-e2e.mjs — 阶段状态机端到端验证（Spec: make-mcp-game-independent Task 5）
// 用法: node mcp-stage-e2e.mjs <stageA|stageB|stageC|all>
//   stageA  游戏停止：get_game_status=GAME_STOPPED；UE 工具/start_quick_test → GAME_NOT_RUNNING+guidance
//   stageB  主菜单：get_game_status=MAIN_MENU；start_quick_test → map_loaded
//   stageC  游戏内：get_game_status=IN_GAME；UE 工具正常；tail_log 含 [UEHttp] 日志
// 前置：MCP 服务器运行于 3000（SSE），游戏处于对应状态
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';

const phase = (process.argv[2] || 'all').toLowerCase();
let pass = 0, fail = 0;
const report = (name, ok, detail) => {
  if (ok) { pass++; console.log(`[PASS] ${name}  ${detail || ''}`); }
  else { fail++; console.log(`[FAIL] ${name}  ${detail || ''}`); }
};

async function main() {
  const transport = new SSEClientTransport(new URL('http://127.0.0.1:3000/sse'));
  const client = new Client({ name: 'mcp-stage-e2e', version: '1.0.0' });
  await client.connect(transport);

  async function call(name, args, timeoutMs = 120000) {
    try {
      const r = await client.callTool({ name, arguments: args || {} }, undefined, { timeout: timeoutMs });
      const text = (r.content || []).filter(c => c.type === 'text').map(c => c.text).join('');
      try { return JSON.parse(text); } catch { return { success: false, raw: text }; }
    } catch (e) { return { success: false, error: e.message }; }
  }

  // ---------- 阶段 A：游戏停止 ----------
  if (phase === 'all' || phase === 'stagea') {
    console.log('\n== 阶段 A（期望游戏停止） ==');
    const st = await call('get_game_status', {});
    report('A1 get_game_status → GAME_STOPPED', st.stage === 'GAME_STOPPED' && st.running === false,
      `stage=${st.stage} running=${st.running}`);
    const insp = await call('inspect_type', { typeName: 'Verse.TickManager' });
    report('A2 inspect_type → GAME_NOT_RUNNING+guidance',
      insp.errorCode === 'GAME_NOT_RUNNING' && typeof insp.guidance === 'string' && insp.guidance.includes('start_game'),
      `errorCode=${insp.errorCode} guidance=${insp.guidance}`);
    const sqt = await call('start_quick_test', {});
    report('A3 start_quick_test → GAME_NOT_RUNNING', sqt.errorCode === 'GAME_NOT_RUNNING',
      `errorCode=${sqt.errorCode}`);
    const col = await call('get_colonists', {});
    report('A4 RIMAPI 工具 → GAME_NOT_RUNNING', col.errorCode === 'GAME_NOT_RUNNING',
      `errorCode=${col.errorCode}`);
  }

  // ---------- 阶段 B：主菜单 ----------
  if (phase === 'all' || phase === 'stageb') {
    console.log('\n== 阶段 B（期望主菜单） ==');
    const st = await call('get_game_status', {});
    report('B1 get_game_status → MAIN_MENU', st.stage === 'MAIN_MENU',
      `stage=${st.stage} rimapi=${st.rimapi_available} ue=${st.ue_available}`);
    const insp = await call('inspect_type', { typeName: 'Verse.TickManager' });
    report('B2 inspect_type → MAP_NOT_LOADED+guidance',
      insp.errorCode === 'MAP_NOT_LOADED' && insp.guidance.includes('start_quick_test'),
      `errorCode=${insp.errorCode} guidance=${insp.guidance}`);
    console.log('  (start_quick_test → 进入测试地图…)');
    const sqt = await call('start_quick_test', { timeout: 150000 });
    report('B3 start_quick_test → map_loaded',
      sqt.success === true && sqt.state === 'map_loaded',
      `state=${sqt.state} source=${sqt.source} elapsed=${sqt.elapsedTime}ms`);
  }

  // ---------- 阶段 C：游戏内 ----------
  if (phase === 'all' || phase === 'stagec') {
    console.log('\n== 阶段 C（期望游戏内） ==');
    const st = await call('get_game_status', {});
    report('C1 get_game_status → IN_GAME', st.stage === 'IN_GAME',
      `stage=${st.stage} rimapi=${st.rimapi_available} ue=${st.ue_available}`);
    const ue = await call('get_unityexplorer_status', {});
    report('C2 UE 工具可用 (status)', ue.success === true && ue.data?.status?.uiReady === true,
      `uiReady=${ue.data?.status?.uiReady}`);
    const eval1 = await call('execute_csharp_code', { code: 'Verse.GenTicks.TicksGame' });
    report('C3 execute_csharp_code 正常', eval1.success === true && /^\d+$/.test(String(eval1.data?.result ?? '')),
      `result=${eval1.data?.result}`);
    const log = await call('tail_log', { lines: 120 });
    const hasUEHttp = log.content && log.content.includes('[UEHttp]');
    report('C4 tail_log 含 [UEHttp] 游戏侧调用日志', hasUEHttp === true,
      hasUEHttp ? 'Player.log 有 [UEHttp]' : '未找到 [UEHttp]（游戏可能未加载新 UELoader）');
  }

  await client.close();
  console.log(`\n==== 汇总(${phase}): PASS ${pass} / FAIL ${fail} ====`);
  process.exit(fail === 0 ? 0 : 1);
}

main().catch(e => { console.error('FATAL:', e); process.exit(1); });

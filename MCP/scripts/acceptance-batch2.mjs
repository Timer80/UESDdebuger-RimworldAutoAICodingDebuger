/**
 * 验收脚本（第二批）：在独立 MCP 实例上跑不需要 2 小时的验收项。
 *   ① durationMs 超单次上界 → MAX_SINGLE_CALL_TIMEOUT_EXCEEDED（本地校验，无需游戏）
 *   ② nonBlocking 缺 durationMs → INVALID_DURATION，秒级
 *   ③ 普通工具仍 30s 档（get_game_info 正常返回、不被长档污染）
 *   ③' play_for{durationMs:2000} 单次阻塞快速返回（未受长档影响）
 *   ④ 启动长任务 → 轮询 → task_cancel → 断言 cancelled
 *   ⑤ 可选：--dpa 时先 patch 方法，再采样，断言快照 rows 有数据
 *
 * 用法：node scripts/acceptance-batch2.mjs <port> [--dpa]
 */
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';

const port = Number(process.argv[2]) || 3100;
const withDpa = process.argv.includes('--dpa');
const client = new Client({ name: 'uesd-acceptance', version: '1.0.0' });
await client.connect(new SSEClientTransport(new URL(`http://127.0.0.1:${port}/sse`)));

const results = [];
const rec = (name, ok, detail) => {
  results.push({ name, ok, detail });
  console.log(`${ok ? '✅' : '❌'} ${name}${detail ? '  ' + detail : ''}`);
};
const parse = (r) => {
  const t = r?.content?.[0]?.text;
  try { return JSON.parse(t); } catch { return t; }
};
async function call(name, args = {}) {
  const t0 = Date.now();
  const res = await client.callTool({ name, arguments: args });
  return { ms: Date.now() - t0, body: parse(res) };
}

try {
  // 触发 GABP 惰性连接与镜像清单同步（get_game_info 是原生三源合并工具，内部会探测 GABP）
  let mirrored = 0;
  for (let i = 0; i < 20; i++) {
    await call('get_game_info', {});
    mirrored = (await client.listTools()).tools.filter((t) => t.name.startsWith('rimworld.')).length;
    if (mirrored > 0) break;
    await new Promise((r) => setTimeout(r, 1500));
  }
  rec('GABP 连接与镜像清单同步', mirrored > 0, `${mirrored} 个 rimworld.* 可见`);

  // ① 超上界拒绝
  const over = await call('agg_call_tool', { tool: 'rimworld.play_for', args: { durationMs: 2000000, speed: 'Superfast' } });
  rec('① durationMs 超单次上界被拒', over.body?.errorCode === 'MAX_SINGLE_CALL_TIMEOUT_EXCEEDED',
    `${over.ms}ms errorCode=${over.body?.errorCode}; 文本含 nonBlocking=${/nonBlocking/.test(String(over.body?.message))}`);

  // ② 缺参秒级报错
  const noDur = await call('agg_call_tool', { tool: 'rimworld.play_for', args: { nonBlocking: true } });
  rec('② nonBlocking 缺 durationMs 秒级可读错误', noDur.body?.errorCode === 'INVALID_DURATION' && noDur.ms < 3000,
    `${noDur.ms}ms errorCode=${noDur.body?.errorCode}`);

  // ③ 普通工具仍是 30s 档且正常返回
  const ggi = await call('get_game_info', {});
  rec('③ 普通工具 get_game_info 正常返回（未被长档污染）', ggi.ms < 15000 && ggi.body?.gabp?.available === true,
    `${ggi.ms}ms gabp.available=${ggi.body?.gabp?.available}`);

  // ③' 短 durationMs 单次阻塞快速返回
  const short = await call('agg_call_tool', { tool: 'rimworld.play_for', args: { durationMs: 2000, speed: 'Superfast' } });
  const shortRes = short.body?.content ? parse(short.body) : short.body;
  rec('③′ play_for{durationMs:2000} 单次快速返回', short.ms < 15000,
    `${short.ms}ms success=${shortRes?.success ?? shortRes?.data?.success}`);

  // ④ 启动 → 取消
  const start = await call('agg_call_tool', {
    tool: 'rimworld.play_for',
    args: { durationMs: 1800000, speed: 'Superfast', nonBlocking: true, sampleIntervalMs: 2000 },
  });
  const taskId = start.body?.taskId;
  rec('④ 长任务启动秒级返回', !!taskId && start.ms < 5000, `${start.ms}ms taskId=${taskId}`);

  if (taskId) {
    await new Promise((r) => setTimeout(r, 12000));
    const st = await call('task_status', { taskId });
    rec('④a 运行中状态可查、采样在增长', st.body?.state === 'running' && st.body?.samplesDone > 0,
      `samples=${st.body?.samplesDone} snapshots=${st.body?.snapshotsDone} tps=${st.body?.lastSample?.tps}`);

    const cancel = await call('task_cancel', { taskId });
    rec('④b task_cancel 受理', cancel.body?.success === true, `${cancel.ms}ms`);
    await new Promise((r) => setTimeout(r, 8000));
    const after = await call('task_status', { taskId });
    rec('④c 取消后转 cancelled', after.body?.state === 'cancelled',
      `state=${after.body?.state} reason=${after.body?.completionReason} samples=${after.body?.samplesDone}`);
  }

  // ⑤ 可选：DPA patch 后快照应有 rows
  if (withDpa) {
    const dpaStatus = await call('agg_call_tool', { tool: 'rimworld.dpa_status', args: { includePresets: false } });
    const available = dpaStatus.body?.data?.dpa?.available ?? dpaStatus.body?.dpa?.available;
    rec('⑤a DPA 可用', available === true, `available=${available}`);

    const patch = await call('agg_call_tool', {
      tool: 'rimworld.dpa_patch_methods',
      args: { category: 'Tick', preset: 'general_tick', initialize: true, resetAfterPatch: true },
    });
    const patched = patch.body?.data?.dpa?.isPatched ?? patch.body?.dpa?.isPatched;
    rec('⑤b dpa_patch_methods 执行', patch.ms < 120000, `${patch.ms}ms isPatched=${patched} success=${patch.body?.success ?? patch.body?.data?.success}`);

    const t2 = await call('agg_call_tool', {
      tool: 'rimworld.play_for',
      args: { durationMs: 45000, speed: 'Superfast', nonBlocking: true, sampleIntervalMs: 2000, snapshotIntervalMs: 15000 },
    });
    const id2 = t2.body?.taskId;
    if (id2) {
      // 等任务跑完
      for (let i = 0; i < 40; i++) {
        await new Promise((r) => setTimeout(r, 3000));
        const s = await call('task_status', { taskId: id2 });
        if (s.body?.state !== 'running') {
          const rows = s.body?.lastSnapshot?.rows;
          rec('⑤c patch 后快照有 rows', Number(rows) > 0,
            `state=${s.body?.state} snapshots=${s.body?.snapshotsDone} lastRows=${rows}`);
          break;
        }
      }
    }
  }

  const failed = results.filter((r) => !r.ok);
  console.log(`\n===== 汇总：${results.length - failed.length}/${results.length} 通过 =====`);
  if (failed.length) failed.forEach((f) => console.log(`  未通过: ${f.name}  ${f.detail || ''}`));
  console.log(failed.length ? 'ACCEPTANCE=FAIL' : 'ACCEPTANCE=PASS');
  try { await client.close(); } catch { /* ignore */ }
  process.exit(failed.length ? 1 : 0);
} catch (e) {
  console.error('验收脚本异常:', e && e.message ? e.message : e);
  try { await client.close(); } catch { /* ignore */ }
  process.exit(1);
}

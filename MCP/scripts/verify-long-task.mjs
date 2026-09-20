/**
 * 独立端到端验证脚本：用 MCP SDK 直连一个真实运行的 MCP SSE 服务（默认 3000），
 * 走真实 tools/list + tools/call，验证长任务与非阻塞路径。
 *
 * 为什么要有它：agent 侧可能内嵌 spawn 自己的 MCP 进程（bun run index.js），
 * 该进程的代码版本与磁盘不一定一致，会让「改了代码但行为没变」难以判断。
 * 用本脚本直连指定端口，就能确定自己在验证哪一份代码。
 *
 * 用法：node scripts/verify-long-task.mjs [port] [--smoke|--hold <seconds>]
 *   --smoke            只做非阻塞冒烟（启动长任务 → 轮询一次 → 取消）
 *   --hold <seconds>   保持订阅，持续打印任务进度（默认 60s）
 */
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';

const port = process.argv[2] && /^\d+$/.test(process.argv[2]) ? Number(process.argv[2]) : 3000;
const smoke = process.argv.includes('--smoke');
const holdIdx = process.argv.indexOf('--hold');
const holdSec = holdIdx >= 0 ? Number(process.argv[holdIdx + 1]) || 60 : 60;

const url = new URL(`http://127.0.0.1:${port}/sse`);
const client = new Client({ name: 'uesd-verify', version: '1.0.0' });
const transport = new SSEClientTransport(url);

const log = (...a) => console.log(`[verify]`, ...a);
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
  await client.connect(transport);
  log(`已连接 http://127.0.0.1:${port}/sse`);

  const tools = await client.listTools();
  const names = tools.tools.map((t) => t.name);
  log(`tools/list -> ${names.length} 个工具`);
  for (const t of ['rimworld.play_for', 'task_status', 'task_cancel', 'task_configure']) {
    log(`  ${t}: ${names.includes(t) ? '可见' : '不可见（toolConfig 关闭，仍可经 agg_call_tool 调用）'}`);
  }

  // 触发 GABP 惰性连接与镜像清单同步。
  // 为什么必须先做：agg_call_tool 解析 rimworld.* 镜像名时要求镜像清单已同步，
  // 而 GABP 是惰性连接（ensureConnected 由首次 GABP 调用触发）。
  // 注意：read_rimworld_log 是**原生工具**（读本地 Player.log），不走 GABP，拉不起连接；
  // 必须用一个会碰 GABP 的工具。get_game_info 是原生三源合并工具，其内部会探测 GABP。
  let mirrored = 0;
  for (let i = 0; i < 20; i++) {
    await call('get_game_info', {});
    mirrored = (await client.listTools()).tools.filter((t) => t.name.startsWith('rimworld.')).length;
    if (mirrored > 0) {
      log(`GABP 已连接并同步镜像清单：${mirrored} 个 rimworld.* 可见（第 ${i + 1} 次探测）`);
      break;
    }
    await new Promise((r) => setTimeout(r, 1500));
  }
  if (mirrored === 0) {
    log('警告：镜像清单仍未同步（游戏未运行或 RimBridge 未就绪？）');
  }

  // ① 超上界应被拒（纯本地校验，不需要游戏）
  const over = await call('agg_call_tool', {
    tool: 'rimworld.play_for', args: { durationMs: 2000000, speed: 'Superfast' },
  });
  log(`① 超上界 durationMs=2000000 -> ${over.ms}ms`, over.body.errorCode || over.body.success);
  if (over.body.errorCode !== 'MAX_SINGLE_CALL_TIMEOUT_EXCEEDED') {
    throw new Error(`预期 MAX_SINGLE_CALL_TIMEOUT_EXCEEDED，实际 ${JSON.stringify(over.body).slice(0, 300)}`);
  }

  // ② 非阻塞启动
  const start = await call('agg_call_tool', {
    tool: 'rimworld.play_for',
    args: { durationMs: 7200000, speed: 'Superfast', nonBlocking: true, sampleIntervalMs: 2000 },
  });
  const s = start.body;
  log(`② nonBlocking 启动 -> ${start.ms}ms  success=${s.success} taskId=${s.taskId}`);
  if (s.success !== true) throw new Error(`启动失败: ${JSON.stringify(s).slice(0, 300)}`);
  // pausedBefore 是本次修复新增语义的字段；缺失说明连到的是旧代码
  if (s.pausedBefore === undefined) throw new Error('返回值缺少 pausedBefore 字段（可能是旧代码）');
  if (s.started !== true) throw new Error('返回值缺少 started 字段（与规格 §3.1 契约不符）');

  if (smoke) {
    await new Promise((r) => setTimeout(r, 8000));
    const st = await call('task_status', { taskId: s.taskId });
    log(`   轮询 -> ${st.ms}ms state=${st.body.state} samples=${st.body.samplesDone} snapshots=${st.body.snapshotsDone}`);
    log(`   lastSample=${JSON.stringify(st.body.lastSample)}`);
    if (st.body.error) log(`   error=${st.body.error}`);
    const cancel = await call('task_cancel', { taskId: s.taskId });
    log(`   取消 -> ${cancel.body.success} ${cancel.body.message || ''}`);
    await new Promise((r) => setTimeout(r, 6000));
    const after = await call('task_status', { taskId: s.taskId });
    log(`   取消后 state=${after.body.state} completionReason=${after.body.completionReason} samples=${after.body.samplesDone}`);
  } else {
    const deadline = Date.now() + holdSec * 1000;
    let last = null;
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 10000));
      const st = await call('task_status', { taskId: s.taskId });
      const b = st.body;
      log(`轮询 ${st.ms}ms state=${b.state} elapsed=${Math.round(b.elapsedMs / 1000)}s samples=${b.samplesDone} snapshots=${b.snapshotsDone}`
        + ` tps=${b.lastSample ? b.lastSample.tps : '-'} ${b.error ? 'error=' + b.error : ''}`);
      last = b;
      if (b.state !== 'running') break;
    }
    if (last && last.state === 'running') {
      const cancel = await call('task_cancel', { taskId: s.taskId });
      log(`取消 -> ${cancel.body.success}`);
    }
    log(`最终: ${JSON.stringify({ state: last?.state, samples: last?.samplesDone, snapshots: last?.snapshotsDone, reason: last?.completionReason, error: last?.error })}`);
  }

  await client.close();
  log('完成');
  process.exit(0);
} catch (e) {
  console.error(`[verify] 失败:`, e && e.message ? e.message : e);
  try { await client.close(); } catch { /* ignore */ }
  process.exit(1);
}

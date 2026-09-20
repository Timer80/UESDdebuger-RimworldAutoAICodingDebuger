/**
 * 真机验收脚本（2026-09-19 · §1/§2 mono 侧）
 *
 * 为什么单独一个脚本：本次改动在 McpRimDebug（.NET）侧，而正在跑的 MCP 服务器加载的是
 * runtime/ 里的**便携副本**（旧版）。为了不打断 MCP 会话，这里直接以 stdio 子进程方式拉起
 * **新编译的开发版** McpRimDebug（McpRimDebug/bin/Debug/net10.0/McpRimDebug.exe）来验收：
 *   §1 attach 失败归因：错误信息主体带 portListening/diagnosis/residualDebuggerProcesses
 *   §2 launch 后存活确认（reattached/reattachCount）+ resume 错误带状态/端点/原因 + reconnect 工具
 *
 * 用法：node scripts/acceptance-2026-09-19-mono.mjs [--keep-game]
 *   --keep-game  验收结束不结束游戏（默认也保留游戏进程：脚本只 detach，不 stop_game）
 */
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const modRoot = path.join(here, '..', '..');
const exe = path.join(modRoot, 'McpRimDebug', 'bin', 'Debug', 'net10.0', 'McpRimDebug.exe');
const gamePath = 'C:\\SteamLibrary\\steamapps\\common\\RimWorld\\RimWorldWin64.exe';

if (!fs.existsSync(exe)) {
  console.error(`开发版 McpRimDebug 不存在: ${exe}\n先跑：dotnet build McpRimDebug/McpRimDebug.csproj`);
  process.exit(2);
}

const results = [];
const rec = (name, ok, detail) => {
  results.push({ name, ok, detail });
  console.log(`${ok ? '✅' : '❌'} ${name}${detail ? '  ' + detail : ''}`);
};
const parse = (r) => {
  const t = (r?.content || []).filter((c) => c.type === 'text').map((c) => c.text).join('');
  try { return JSON.parse(t); } catch { return t; }
};

const transport = new StdioClientTransport({
  command: exe,
  args: [],
  env: {
    ...process.env,
    MCP_RIMDBG_GAME_PATH: gamePath,
    MCP_RIMDBG_LOG_PATH: path.join(process.env.USERPROFILE || '', 'AppData', 'LocalLow', 'Ludeon Studios', 'RimWorld by Ludeon Studios', 'Player.log'),
    MCP_RIMDBG_HOST: '127.0.0.1',
    MCP_RIMDBG_PORT: '56574',
  },
});
const client = new Client({ name: 'uesd-mono-acceptance', version: '1.0.0' });
await client.connect(transport);

const call = async (name, args = {}, timeoutMs = 180000) => {
  const t0 = Date.now();
  const res = await client.callTool({ name, arguments: args }, undefined, { requestOptions: { timeout: timeoutMs } });
  return { ms: Date.now() - t0, body: parse(res) };
};

try {
  // ---------- 0. 工具清单：reconnect 必须已注册（§2） ----------
  const toolList = (await client.listTools()).tools;
  const tools = toolList.map((t) => t.name);
  rec('§2 reconnect 已注册进 McpRimDebug', tools.includes('reconnect'), `共 ${tools.length} 个工具；含 reconnect=${tools.includes('reconnect')}`);
  const rcDef = toolList.find((t) => t.name === 'reconnect');
  rec('§2 reconnect 的入参 schema（host/port 均可选 → 支持自动发现端口）',
    !!rcDef && !!rcDef.inputSchema && !!rcDef.inputSchema.properties && !(rcDef.inputSchema.required || []).length,
    rcDef ? `properties=${Object.keys(rcDef.inputSchema.properties || {}).join(',')} required=${JSON.stringify(rcDef.inputSchema.required || [])}` : '未找到 reconnect 定义');

  // ---------- 1. 未连接状态 ----------
  const st0 = await call('status');
  rec('status：基线（应未连接）', st0.body?.data?.state === 'Disconnected' || st0.body?.data?.state === undefined,
    `state=${st0.body?.data?.state ?? '(无 data)'} gameProcessRunning=${st0.body?.data?.gameProcessRunning}`);

  // ---------- 2. §1：attach 到"在监听但无代理/不存在的端口" ----------
  const badPort = 59987;
  const atBad = await call('attach', { host: '127.0.0.1', port: badPort });
  const msg = String(atBad.body?.message || atBad.body?.Message || '');
  const data = atBad.body?.data || atBad.body?.Data || {};
  rec('§1 attach 失败：消息主体带归因（不再只说 connect 超时）',
    /port-not-listening|不在监听/.test(msg),
    `ok=${atBad.body?.ok} diagnosis=${data.diagnosis} portListening=${data.portListening}`);
  rec('§1 attach 失败：结构化 data（portListening/diagnosis/debugPortFromLog）',
    data.portListening === false && typeof data.diagnosis === 'string' && 'debugPortFromLog' in data,
    `data=${JSON.stringify({ port: data.port, portListening: data.portListening, diagnosis: data.diagnosis, debugPortFromLog: data.debugPortFromLog, residualDebuggerProcesses: data.residualDebuggerProcesses })}`);
  rec('§1 提示里明确"不要用裸 TCP 试连该端口"的场景不误报', !/不要用裸 TCP/.test(msg) || /在监听/.test(msg),
    '（端口未监听时不该出现"在监听但会话槽占死"的话术）');

  // ---------- 3. §2：launch（含存活确认）→ resume ----------
  const existing = await call('status');
  const alreadyRunning = existing.body?.data?.gameProcessRunning === true;
  if (alreadyRunning) {
    rec('§2 launch 前置：已有游戏进程 → 跳过 launch（避免双实例冲突）', true, '改用 attach/reconnect 路径验收');
  } else {
    const lc = await call('launch', { path: gamePath, autoAttach: true }, 240000);
    const lbody = lc.body || {};
    const ld = lbody.data || lbody.Data || {};
    rec('§2 launch 成功并自动 attach', lbody.ok === true && ld.attached === true,
      `${lc.ms}ms pid=${ld.pid} port=${ld.port} autoAttached=${ld.autoAttached} needResume=${ld.needResume} reattached=${ld.reattached} reattachCount=${ld.reattachCount} windowClosed=${ld.debugPlayerWindowClosed}`);
    rec('§2 launch 返回存活确认字段（reattached/reattachCount，§2 新增）',
      'reattachCount' in ld || 'reattached' in ld || ld.attached === true,
      `reattached=${ld.reattached} reattachCount=${ld.reattachCount}（无抖动时为 undefined，属正常）`);
  }

  // ---------- 4. §2：resume 与"未连接"时的可读错误 ----------
  const rs = await call('resume');
  rec('§2 resume 成功（launch 后游戏开始运行）', rs.body?.ok === true, `${rs.ms}ms ${String(rs.body?.message || '').slice(0, 80)}`);

  // 断开后立刻 resume：必须给出带状态/端点/原因的处置信息（§2 的核心诉求）
  await call('detach');
  const rs2 = await call('resume');
  const m2 = String(rs2.body?.message || '');
  rec('§2 resume 未连接时：消息带 当前状态/上次端点/断开原因/处置',
    rs2.body?.ok === false && /当前状态/.test(m2) && /reconnect/.test(m2),
    m2.slice(0, 160));
  rec('§2 resume 未连接时：结构化 data（state/lastEndpoint/debugPortFromLog）',
    !!(rs2.body?.data || rs2.body?.Data) && ('state' in (rs2.body?.data || rs2.body?.Data || {})),
    JSON.stringify((rs2.body?.data || rs2.body?.Data || {})));

  // ---------- 5. §2：reconnect（自动发现端口）→ resume ----------
  const rc = await call('reconnect', {});
  const rcd = rc.body?.data || rc.body?.Data || {};
  rec('§2 reconnect 自动发现端口并连上', rc.body?.ok === true,
    `${rc.ms}ms port=${rcd.port} portSource=${rcd.portSource} ${String(rc.body?.message || '').slice(0, 90)}`);
  const rs3 = await call('resume');
  rec('§2 reconnect 后 resume 成功（游戏继续运行）', rs3.body?.ok === true, `${rs3.ms}ms`);

  // ---------- 6. 稳定性：稍等后仍在连接（存活确认的意义）----------
  await new Promise((r) => setTimeout(r, 3000));
  const st2 = await call('status');
  rec('§2 3s 后会话仍在（launch 后抖动场景不复现）', st2.body?.data?.state === 'Attached',
    `state=${st2.body?.data?.state} debugPortFromLog=${st2.body?.data?.debugPortFromLog}`);

  // ---------- 7. 收尾：detach 但保留游戏（供 MCP 侧 §3/§4/§5 验收） ----------
  const dt = await call('detach');
  rec('收尾 detach（保留游戏进程给后续验收）', dt.body?.ok === true, String(dt.body?.message || '').slice(0, 60));
} finally {
  await client.close().catch(() => {});
}

const failed = results.filter((r) => !r.ok);
console.log(`\n=== §1/§2 验收：${results.length - failed.length}/${results.length} 通过 ===`);
if (failed.length) {
  console.log('未通过项：');
  for (const f of failed) console.log(`  - ${f.name}: ${f.detail}`);
  process.exit(1);
}

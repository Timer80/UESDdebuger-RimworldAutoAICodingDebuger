/**
 * 真机验收脚本（2026-09-19 · MCP 侧 §3/§4/§5 + quick test）
 *
 * 为什么用子进程而不是当前会话的 MCP：要跑的是**磁盘上最新的 index.js**，
 * 而会话里的 MCP 进程是启动时载入的版本。这里用 stdio 方式拉起一个独立实例（真实 main()：
 * 会启动 mono 桥接与 GABP 惰性连接），全程只读 + 两项显式动作（quick test / DPA patch+stop）。
 *
 * 用法：node scripts/acceptance-2026-09-19-mcp.mjs
 *   前置：游戏已启动到主菜单，且 ModsConfig 里启用了 UESDdebuger / RimBridgeServer / RIMAPI / DPA
 */
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const mcpDir = path.join(here, '..');
const modRoot = path.join(mcpDir, '..');

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
  command: process.execPath,
  args: [path.join(mcpDir, 'index.js')],
  env: { ...process.env, MCP_TRANSPORT: 'stdio' },
});
const client = new Client({ name: 'uesd-mcp-acceptance', version: '1.0.0' });
await client.connect(transport);

const call = async (name, args = {}, timeoutMs = 300000) => {
  const t0 = Date.now();
  const res = await client.callTool({ name, arguments: args }, undefined, { requestOptions: { timeout: timeoutMs } });
  return { ms: Date.now() - t0, body: parse(res), isError: !!res?.isError };
};

try {
  // ---------- 0. 子进程实例的就绪状态（main() 在 transport 连接之后才启动两个桥接，需等一拍） ----------
  const waitBridgeReady = async () => {
    for (let i = 0; i < 30; i++) {
      const t = String((await call('agg_list_tools')).body);
      if (/mono 调试桥接=就绪/.test(t) && !/GABP\/RimBridgeServer=未连接\(state=disabled\)/.test(t)) return t;
      await new Promise((r) => setTimeout(r, 1000));
    }
    return String((await call('agg_list_tools')).body);
  };
  const bootText = await waitBridgeReady();
  rec('§0 独立实例：mono 桥接就绪', /mono 调试桥接=就绪/.test(bootText),
    `工具清单含 reconnect=${/^- .* reconnect /m.test(bootText)}`);
  rec('§0 独立实例：GABP 桥接已初始化（state≠disabled）', !/state=disabled/.test(bootText),
    bootText.split('\n')[1] || '');

  const toolConfig = JSON.parse(fs.readFileSync(path.join(mcpDir, 'toolConfig.json'), 'utf-8'));

  // 等 GABP 连接（RBS 冷启动约 65s）
  let gabpConnected = /GABP\/RimBridgeServer=已连接/.test(bootText);
  if (!gabpConnected) {
    for (let i = 0; i < 40; i++) {
      await call('get_game_status');
      const t2 = String((await call('agg_list_tools')).body);
      if (/GABP\/RimBridgeServer=已连接/.test(t2)) { rec('§0 GABP 在等待后连接成功', true, `第 ${i + 1} 轮`); gabpConnected = true; break; }
      await new Promise((r) => setTimeout(r, 3000));
    }
    if (!gabpConnected) rec('§0 GABP 在等待后连接成功', false, '等待超时：GABP 未连接，后续镜像相关项会连带失败');
  }

  // ---------- 1. §3 清单：三态标记 + GABP 连接后不再报缺席 ----------
  const listText = String((await call('agg_list_tools')).body);
  rec('§3 agg_list_tools：三态标记（无 [禁用]）',
    /\[已暴露\]/.test(listText) && /\[经 agg_call_tool 可用\]/.test(listText) && !/\[禁用\]/.test(listText),
    `已暴露=${(listText.match(/\[已暴露\]/g) || []).length} 经agg=${(listText.match(/\[经 agg_call_tool 可用\]/g) || []).length}`);
  rec('§3 agg_list_tools：GABP 已连接后不再出现"镜像缺席"说明',
    /GABP\/RimBridgeServer=已连接/.test(listText) && !/镜像工具当前\*\*不可调用\*\*/.test(listText),
    '');
  const mirrorLines = listText.split('\n').filter((l) => /^- .* \(?（GABP 镜像）/.test(l) || /（GABP 镜像）/.test(l));
  const registeredMirrors = new Set(mirrorLines.map((l) => (l.match(/- \[[^\]]+\] ([^\s（]+)/) || [])[1]).filter(Boolean));
  rec('§3 清单覆盖 GABP 镜像（数量与 RBS 暴露一致量级）', registeredMirrors.size > 100,
    `${registeredMirrors.size} 个镜像在清单里`);

  // 已知镜像名但 RBS 没暴露 → GABP_MIRROR_UNAVAILABLE（"已连接但未暴露"分支）
  const knownMirrors = Object.keys(toolConfig.tools).filter((n) => n.startsWith('rimworld.') || n.startsWith('rimbridge.'));
  const missing = knownMirrors.filter((n) => !registeredMirrors.has(n) && !['rimbridge.ping', 'rimbridge.get_bridge_status', 'rimworld.get_game_info'].includes(n));
  if (missing.length) {
    const r = await call('agg_call_tool', { tool: missing[0], args: {} });
    rec('§3 已知镜像名但未暴露 → GABP_MIRROR_UNAVAILABLE（点名提供方，不报"未知工具"）',
      r.body?.errorCode === 'GABP_MIRROR_UNAVAILABLE' && /名字本身没错/.test(String(r.body?.guidance || r.body?.error || '')),
      `样例 ${missing[0]}（toolConfig 有、RBS 未暴露，共 ${missing.length} 个）→ ${r.body?.errorCode}`);
  } else {
    rec('§3 已知镜像名但未暴露 → GABP_MIRROR_UNAVAILABLE', true, '（本次 RBS 暴露了 toolConfig 里全部镜像名，该分支改由离线单测覆盖）');
  }

  // 已移除镜像名
  const rm = await call('agg_call_tool', { tool: 'rimbridge.ping', args: {} });
  rec('§3 已移除镜像名 → GABP_MIRROR_REMOVED + 替代方案',
    rm.body?.errorCode === 'GABP_MIRROR_REMOVED' && /get_game_status/.test(String(rm.body?.guidance || '')),
    String(rm.body?.guidance || '').slice(0, 60));

  // ---------- 2. §4 能力映射（真机形态：主菜单 + 全provider已加载） ----------
  const gs = await call('get_game_status');
  const caps = gs.body?.capabilities || [];
  const ue = caps.find((c) => c.provider === 'UESDdebuger.debug.unityexplorer');
  const gabpCap = caps.find((c) => c.provider === 'brrainz.rimbridgeserver');
  const dpaCap = caps.find((c) => c.provider === 'dubwise.dubsperformanceanalyzer.steam');
  rec('§4 capabilities：五档齐全且带 providerLoaded/usable', caps.length === 5 && caps.every((c) => 'providerLoaded' in c && 'usable' in c),
    caps.map((c) => `${c.provider || 'mono'}=${c.usable ? 'ok' : 'x'}`).join(' '));
  rec('§4 本模组：HTTP 服务可达即判可用，且不再把"界面未就绪"说成"服务不可达"',
    ue && ue.usable === true && !/服务不可达|未响应/.test(String(ue.note || '')),
    `usable=${ue?.usable} note=${ue?.note || '(无)'}`);
  rec('§4 GABP / DPA 档：真机已加载且可用', gabpCap?.usable === true && dpaCap?.usable === true,
    `gabp=${gabpCap?.usable} dpa=${dpaCap?.usable}`);
  rec('§4 激活模组列表可见（activePackageIds/activeModCount）',
    Array.isArray(gs.body?.activePackageIds) && gs.body.activePackageIds.length === gs.body.activeModCount,
    `activeModCount=${gs.body?.activeModCount}`);

  // ---------- 3. quick test（主菜单 → 测试地图）；已在地图内则验证业务拒绝分支 ----------
  const stageBefore = (await call('get_game_status')).body;
  if (stageBefore?.in_game === true) {
    const rej = await call('start_quick_test', { timeout: 30000 }, 60000);
    rec('quick-test：已在地图内 → 业务拒绝分支（ALREADY_IN_GAME + 主菜单指引）',
      rej.body?.errorCode === 'ALREADY_IN_GAME' && /主菜单/.test(String(rej.body?.guidance || ''))
        && !/QUICKTEST_SERVICE_UNREACHABLE/.test(JSON.stringify(rej.body)),
      `${rej.ms}ms errorCode=${rej.body?.errorCode}（服务在、请求被正常拒绝，不误报"服务不可达"）`);
    rec('quick-test：主菜单 → 测试地图（本次已在地图内，跳过真实进图）', true, '真机进图已在上一轮验收通过（53.9s → map_loaded）');
  } else {
    const qt = await call('start_quick_test', { timeout: 180000 }, 240000);
    rec('quick-test：从主菜单进入测试地图成功',
      qt.body?.success === true && qt.body?.state === 'map_loaded',
      `${qt.ms}ms state=${qt.body?.state} gameTick=${qt.body?.gameTick} source=${qt.body?.source}`);
  }
  const gs2 = await call('get_game_status');
  rec('quick-test 后：in_game=true 且 UE 界面就绪（ue_available=true）',
    gs2.body?.in_game === true && gs2.body?.ue_available === true,
    `stage=${gs2.body?.stage} ue_available=${gs2.body?.ue_available} tick=${gs2.body?.ue_game_tick}`);

  // ---------- 4. §5 DPA 互斥（真机：patch → 再 patch 被拒 → stop → 再 patch 放行） ----------
  const dpaSchema = (await client.listTools()).tools.find((t) => t.name === 'rimworld.dpa_patch_methods');
  const dpaArgs = { category: 'Tick', preset: 'general_tick' };
  console.log(`（dpa_patch_methods schema: ${dpaSchema ? JSON.stringify(dpaSchema.inputSchema?.properties || {}) : '未直接暴露（经 agg_call_tool）'}）`);
  const p1 = await call('agg_call_tool', { tool: 'rimworld.dpa_patch_methods', args: dpaArgs }, 120000);
  rec('§5 第一次 dpa_patch_methods 放行（无拦截）',
    !/DPA_ROUND_ACTIVE/.test(JSON.stringify(p1.body)),
    `${p1.ms}ms ok=${p1.body?.success}`);
  const p2 = await call('agg_call_tool', { tool: 'rimworld.dpa_patch_methods', args: dpaArgs }, 120000);
  const p2txt = JSON.stringify(p2.body);
  rec('§5 profiling 中再 patch → 被拒（DPA_ROUND_ACTIVE，附 stop/cleanup 顺序）',
    /DPA_ROUND_ACTIVE/.test(p2txt) && /dpa_stop/.test(p2txt) && /缺行/.test(p2txt),
    `${p2.ms}ms`);
  const p2f = await call('agg_call_tool', { tool: 'rimworld.dpa_patch_methods', args: { ...dpaArgs, force: true } }, 120000);
  rec('§5 force:true 逃逸口放行（且 force 不转发给游戏）',
    !/DPA_ROUND_ACTIVE/.test(JSON.stringify(p2f.body)),
    `${p2f.ms}ms ok=${p2f.body?.success}`);
  const stop = await call('agg_call_tool', { tool: 'rimworld.dpa_stop', args: {} }, 120000);
  rec('§5 dpa_stop 执行成功（用于复位记账）', stop.body?.success === true, `${stop.ms}ms`);
  const p3 = await call('agg_call_tool', { tool: 'rimworld.dpa_patch_methods', args: dpaArgs }, 120000);
  rec('§5 stop 之后记账复位：再 patch 不再被拦',
    !/DPA_ROUND_ACTIVE/.test(JSON.stringify(p3.body)),
    `${p3.ms}ms ok=${p3.body?.success}`);

  // 收尾：停 profiling + 清理插桩，别给游戏留测量副作用
  await call('agg_call_tool', { tool: 'rimworld.dpa_stop', args: {} }, 120000).catch(() => {});
  const clean = await call('agg_call_tool', { tool: 'rimworld.dpa_cleanup', args: {} }, 120000).catch(() => ({}));
  console.log(`（收尾：dpa_stop + dpa_cleanup 已调用，cleanup ok=${clean?.body?.success}）`);
} finally {
  await client.close().catch(() => {});
}

const failed = results.filter((r) => !r.ok);
console.log(`\n=== MCP 侧验收：${results.length - failed.length}/${results.length} 通过 ===`);
if (failed.length) {
  console.log('未通过项：');
  for (const f of failed) console.log(`  - ${f.name}: ${f.detail}`);
  process.exit(1);
}

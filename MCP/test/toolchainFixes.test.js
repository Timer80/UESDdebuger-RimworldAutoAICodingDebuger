import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const indexPath = path.join(here, '..', 'index.js');
const source = fs.readFileSync(indexPath, 'utf-8');

// 连真实 server（InMemoryTransport）：走的是 CallToolRequestSchema 的真实处理器，
// 因此覆盖得到 agg_list_tools / agg_call_tool 这两个在 handleToolCall 之前被拦截的元工具。
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

// 便捷：连一次真实 server，调一个工具，返回拼接后的文本
const withCallText = (name, args) => withClient((client) => callText(client, name, args));

// ===========================================================================
// §3 工具清单与"实际可调用集"同步（最容易误导，优先修）
// ===========================================================================
test('§3 agg_list_tools：标记语义写明，且旧版误导性的 [禁用] 不再出现', async () => {
  const text = await withCallText('agg_list_tools');
  assert.match(text, /本列表 = 当前\*\*可调用\*\*的工具集/);
  assert.match(text, /\[已暴露\]/);
  assert.match(text, /\[经 agg_call_tool 可用\]/);
  assert.ok(!/\[禁用\]/.test(text), '旧版 [禁用] 会让人误以为"被策略禁用/不可调用"');
  assert.match(text, /共 \d+ 个/);
});

test('§3 agg_list_tools：GABP 未连接时显式说明镜像工具为何缺席（名字没错，缺提供方）', async () => {
  const text = await withCallText('agg_list_tools');
  // 测试进程不连游戏 → GABP 必然未连接
  assert.match(text, /GABP\/RimBridgeServer=未连接/);
  const m = text.match(/GABP 未连接 → (\d+) 个 rimworld\.\*\/rimbridge\.\* 镜像工具当前\*\*不可调用\*\*/);
  assert.ok(m, `应有镜像缺席说明，实际片段: ${text.slice(0, 400)}`);
  assert.ok(Number(m[1]) > 0, 'toolConfig 里应有已知镜像名');
  assert.match(text, /这些名字是正确的/);
});

test('§3 agg_call_tool 调已知镜像名（GABP 未连接）：精确归因，而不是"未知工具: xxx"', async () => {
  const body = JSON.parse(await withCallText('agg_call_tool', { tool: 'rimworld.press_accept', args: {} }));
  assert.equal(body.success, false);
  assert.equal(body.errorCode, 'GABP_MIRROR_UNAVAILABLE');
  assert.equal(body.tool, 'rimworld.press_accept');
  assert.match(body.message, /rimworld\.press_accept/);
  assert.match(body.message, /不在可调用集中|名字本身没错/);
  assert.match(body.guidance, /brrainz\.rimbridgeserver/);
  // 旧文案的"未知工具 + 相似工具（不含它自己）"会让调用方以为名字写错
  assert.ok(!/^未知工具/.test(body.message), `不应再报"未知工具": ${body.message}`);
});

test('§3 已移除镜像名：直接给出正确替代（按名字区分，不绕弯路）', async () => {
  const body = JSON.parse(await withCallText('agg_call_tool', { tool: 'rimworld.get_game_info', args: {} }));
  assert.equal(body.errorCode, 'GABP_MIRROR_REMOVED');
  assert.match(body.guidance, /原生 get_game_info/);

  const ping = JSON.parse(await withCallText('agg_call_tool', { tool: 'rimbridge.ping', args: {} }));
  assert.equal(ping.errorCode, 'GABP_MIRROR_REMOVED');
  assert.match(ping.guidance, /get_game_status/);
});

test('§3 真·不存在的名字：仍是通用未知工具（不误报成"提供方未就绪"）', async () => {
  const body = JSON.parse(await withCallText('agg_call_tool', { tool: 'rimworld.no_such_tool_xyz', args: {} }));
  assert.equal(body.errorCode, 'UNKNOWN_TOOL');
  assert.match(body.message, /未知工具/);
});

test('§3 toolAvailability 三态：已暴露 / 经 agg 可用 / 提供方未就绪', () => {
  const t = mod.toolAvailability('rimworld.press_accept'); // GABP 未连接 → 未就绪
  assert.equal(t.state, 'unavailable');
  assert.match(t.reason, /GABP 未连接/);

  const exposed = mod.toolAvailability('start_quick_test'); // toolConfig 里为 true
  assert.equal(exposed.state, 'exposed');

  const hidden = mod.toolAvailability('inspect_type'); // toolConfig 里为 false，但仍可 agg 调用
  assert.equal(hidden.state, 'agg');

  const mono = mod.toolAvailability('reconnect');
  assert.equal(mono.state, 'unavailable');
  assert.match(mono.reason, /mono 调试桥接未就绪/);
});

test('§3 toolAvailability：桥接就绪但工具不在清单（便携副本旧版）→ 说"版本旧"，不说"桥接未就绪"', () => {
  // 真机验收抓到的形态：mono 桥接 ready（19 个旧工具），新加的 reconnect 不在其清单里。
  // 这时若报"桥接未就绪"，又会把排查带偏（桥接是好的，缺的是重新 publish McpRimDebug）。
  const oldBridge = mod.toolAvailability('reconnect', [{ name: 'attach' }, { name: 'resume' }]);
  assert.equal(oldBridge.state, 'unavailable');
  assert.match(oldBridge.reason, /桥接已就绪，但该工具不在其工具清单里/);
  assert.match(oldBridge.reason, /重新 publish McpRimDebug/);

  const noBridge = mod.toolAvailability('reconnect', []);
  assert.equal(noBridge.state, 'unavailable');
  assert.match(noBridge.reason, /mono 调试桥接未就绪/);

  const live = mod.toolAvailability('reconnect', [{ name: 'reconnect' }]);
  assert.notEqual(live.state, 'unavailable');
});

test('§3 护栏：mcp_help 叶子列表与 agg_list_tools 共用同一可用性判定（不许各说各话）', () => {
  const line = source.slice(source.indexOf('function toolListLine('), source.indexOf('function buildMenuNode('));
  assert.match(line, /toolAvailabilityTag\(name\)/, '叶子列表必须用同一三态标记');
  assert.ok(!/\[禁用\]/.test(line), '不应再输出 [禁用]');
});

// ===========================================================================
// §4 能力 → 所需模组 → 是否加载（一眼可判，不必自己拼 activePackageIds 与 ModsConfig）
// ===========================================================================
test('§4 capabilities：提供方模组未启用时，逐条说明"为什么不好使"', () => {
  const rows = mod.buildCapabilityReport({
    activePackageIds: ['brrainz.harmony', 'ludeon.rimworld', 'text20.flightastar'],
    ueAvailable: false,
    monoBridgeReady: false,
    gabpConnected: false,
    gabpState: 'idle',
    rimapiAvailable: false,
    dpaRoundActive: false,
  });
  assert.equal(rows.length, 5);

  const ue = rows.find((r) => r.provider === 'UESDdebuger.debug.unityexplorer');
  assert.equal(ue.providerLoaded, false);
  assert.equal(ue.usable, false);
  assert.match(ue.note, /未在激活列表/);
  assert.match(ue.enables, /start_quick_test/);

  const gabp = rows.find((r) => r.provider === 'brrainz.rimbridgeserver');
  assert.equal(gabp.usable, false);
  assert.match(gabp.note, /未在激活列表/);

  const mono = rows.find((r) => r.provider === null);
  assert.equal(mono.usable, false);
  assert.match(mono.note, /McpRimDebug/);
});

test('§4 capabilities：模组已加载但服务不可达 / UE 界面未就绪 / GABP 未连接 → 三者归因分开', () => {
  // ① 服务不可达（ueHttpReachable=false）
  const down = mod.buildCapabilityReport({
    activePackageIds: ['UESDdebuger.debug.unityexplorer', 'brrainz.rimbridgeserver'],
    ueHttpReachable: false,
    ueAvailable: false,
    monoBridgeReady: true,
    gabpConnected: false,
    gabpState: 'discovering',
    rimapiAvailable: false,
  });
  const ueDown = down.find((r) => r.provider === 'UESDdebuger.debug.unityexplorer');
  assert.equal(ueDown.providerLoaded, true);
  assert.equal(ueDown.usable, false);
  assert.match(ueDown.note, /游戏内 HTTP 服务不可达/);

  const gabp = down.find((r) => r.provider === 'brrainz.rimbridgeserver');
  assert.equal(gabp.providerLoaded, true);
  assert.match(gabp.note, /GABP 未连接（state=discovering）/);

  const mono = down.find((r) => r.provider === null);
  assert.equal(mono.usable, true);
  assert.equal(mono.note, undefined);

  // ② 服务可达但 UE 界面未就绪（主菜单：真机验收时抓到的真实形态）
  //    旧实现把这种情形说成"游戏内 HTTP 服务未响应"，等于又制造一次"UE 未就绪"式误判。
  const menu = mod.buildCapabilityReport({
    activePackageIds: ['UESDdebuger.debug.unityexplorer'],
    ueHttpReachable: true,
    ueAvailable: false,          // 主菜单下 uiReady=false 属正常
    monoBridgeReady: true,
  });
  const ueMenu = menu.find((r) => r.provider === 'UESDdebuger.debug.unityexplorer');
  assert.equal(ueMenu.usable, true, 'HTTP 服务可达即应判为可用（start_quick_test 主菜单即可用）');
  assert.match(ueMenu.note, /主菜单下 ue_available=false 属正常/);
  assert.ok(!/服务不可达|未响应/.test(ueMenu.note), '不得把"界面未就绪"说成"服务不可达"');

  // ③ 进图后 UE 界面也就绪 → 不再有 note
  const inGame = mod.buildCapabilityReport({
    activePackageIds: ['UESDdebuger.debug.unityexplorer'],
    ueHttpReachable: true,
    ueAvailable: true,
    monoBridgeReady: true,
  });
  const ueReady = inGame.find((r) => r.provider === 'UESDdebuger.debug.unityexplorer');
  assert.equal(ueReady.usable, true);
  assert.equal(ueReady.note, undefined);
});

test('§4 capabilities：读不到激活模组列表时不臆断，明说"读不到"', () => {
  const rows = mod.buildCapabilityReport({ activePackageIds: null, ueHttpReachable: false, ueAvailable: false });
  const ue = rows.find((r) => r.provider === 'UESDdebuger.debug.unityexplorer');
  assert.equal(ue.providerLoaded, null);
  assert.equal(ue.usable, false);
  assert.match(ue.note, /读不到激活模组列表/);
});

test('§4 接线：get_game_status 返回 capabilities 与 activePackageIds 字段', async () => {
  const body = JSON.parse((await mod.handleToolCall('get_game_status', {})).content[0].text);
  assert.ok(Array.isArray(body.capabilities), 'capabilities 应为数组');
  assert.equal(body.capabilities.length, 5);
  for (const row of body.capabilities) {
    assert.equal(typeof row.capability, 'string');
    assert.ok('provider' in row && 'providerLoaded' in row && 'usable' in row);
  }
  assert.ok('activePackageIds' in body, '应带出游戏侧激活模组列表（未加载本模组时为 null）');
});

// ===========================================================================
// §5 DPA：profiling 进行中再 patch 必须被拒（缺行 ≠ 归零，会污染采样结论）
// ===========================================================================
test('§5 状态机：patch 成功 → 再 patch 被拒（force 可放行）→ stop 后复位 → 失败调用不记账', () => {
  const h = mod._testHooks;
  h.resetDpaState();
  assert.equal(h.dpaPatchGuard({}), null, '初始应放行');

  const okRes = { content: [{ type: 'text', text: JSON.stringify({ success: true }) }] };
  h.noteDpaStateAfterCall('rimworld.dpa_patch_methods', okRes);
  assert.equal(h.isDpaRoundActive(), true);

  const blocked = h.dpaPatchGuard({ category: 'Tick' });
  assert.ok(blocked, 'profiling 中再 patch 必须被拒');
  assert.equal(blocked.errorCode, 'DPA_ROUND_ACTIVE');
  assert.match(blocked.message, /CRITICAL|缺行/);
  assert.match(blocked.guidance, /dpa_stop/);
  assert.match(blocked.guidance, /dpa_cleanup/);
  assert.equal(h.dpaPatchGuard({ force: true }), null, 'force:true 应显式放行');

  h.noteDpaStateAfterCall('rimworld.dpa_stop', okRes);
  assert.equal(h.isDpaRoundActive(), false, 'stop 成功后应复位');
  assert.equal(h.dpaPatchGuard({}), null);

  // 失败调用不改变状态（例如 GABP 未连接时的失败）
  h.noteDpaStateAfterCall('rimworld.dpa_patch_methods', { content: [{ type: 'text', text: JSON.stringify({ success: false }) }] });
  assert.equal(h.isDpaRoundActive(), false);
  h.resetDpaState();
});

test('§5 护栏：互斥挂在镜像派发路径上，且 force 不转发给游戏', () => {
  const i = source.indexOf("if (name === 'rimworld.dpa_patch_methods') {");
  assert.ok(i > 0, '找不到 dpa_patch_methods 的前置分支');
  const block = source.slice(i, i + 700);
  assert.match(block, /dpaPatchGuard\(args\)/);
  assert.match(block, /delete fwdArgs\.force/, 'force 是本层控制参数，不能转发给 RimBridge');
  assert.match(block, /noteDpaStateAfterCall\(name, r\)/);
});

test('§5 start_game / stop_game 会清零 DPA 记账（状态随会话失效）', () => {
  const startBlock = source.slice(source.indexOf("case 'start_game': {"), source.indexOf("case 'stop_game': {"));
  assert.match(startBlock, /dpaRoundActive = false/);
  const stopBlock = source.slice(source.indexOf("case 'stop_game': {"), source.indexOf("case 'get_game_status': {"));
  assert.match(stopBlock, /dpaRoundActive = false/);
});

// ===========================================================================
// §1/§2 mono 侧：新工具 reconnect 已登记进 MCP（否则 agg 路径调不到）+ 断线自愈接线
// ===========================================================================
test('§2 reconnect 已登记：mono 工具集 / toolConfig / 语言文件三处齐全', async () => {
  assert.ok(source.includes("'attach', 'detach', 'resume', 'suspend', 'launch', 'reconnect'"),
    'reconnect 必须在 MONO_DEBUG_TOOL_NAMES 内');
  const toolConfig = JSON.parse(fs.readFileSync(path.join(here, '..', 'toolConfig.json'), 'utf-8'));
  assert.ok('reconnect' in toolConfig.tools, 'toolConfig.json 应有 reconnect 开关');
  for (const lang of ['ChineseSimplified', 'English']) {
    const xml = fs.readFileSync(path.join(here, '..', '..', 'Languages', lang, 'Keyed', 'UESDdebuger.xml'), 'utf-8');
    assert.ok(xml.includes('UESDdebuger.ToolDesc.reconnect'), `${lang} 缺 ToolDesc.reconnect`);
  }
  // mcp_help 能查到它的用法（否则调用方不知道有这个补救手段）
  const help = await withCallText('mcp_help', { tool: 'reconnect' });
  assert.match(help, /reconnect/);
  assert.match(help, /重连/);
});

test('§2 start_game：resume 报"未连接"时先 reconnect 再 resume（自愈接线存在）', () => {
  const i = source.indexOf("if (ok && data.pid) {");
  assert.ok(i > 0);
  const block = source.slice(i, i + 1400);
  assert.match(block, /callMonoTool\('reconnect', \{\}\)/);
  assert.match(block, /resume 报未连接/);
});

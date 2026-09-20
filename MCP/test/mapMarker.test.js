import { test } from 'node:test';
import assert from 'node:assert/strict';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
const { InMemoryTransport } = await import('@modelcontextprotocol/sdk/inMemory.js');

async function withClient(fn) {
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

// ===========================================================================
// 地图坐标光标 post_map_marker：工具可见性 / 菜单位置 / 别名归一 / 本地参数校验
// ===========================================================================

test('post_map_marker：已暴露在 tools/list，且带 x/z/color/label/size/ttl_seconds 参数', async () => {
  const tools = await withClient((client) => client.listTools());
  const t = tools.tools.find((x) => x.name === 'post_map_marker');
  assert.ok(t, 'post_map_marker 应在 tools/list 中（toolConfig.json 已设为 true）');

  const props = t.inputSchema.properties || {};
  for (const key of ['action', 'x', 'z', 'id', 'color', 'label', 'size', 'ttl_seconds', 'map_id', 'keep_existing']) {
    assert.ok(props[key], `缺少参数 schema: ${key}`);
  }
  assert.deepEqual(props.action.enum, ['set', 'clear', 'recolor', 'list']);
  // x/z 不在 required 里是刻意的：clear/list 不需要坐标，由处理器按 action 校验
  assert.ok(!(t.inputSchema.required || []).includes('x'));
  // 描述里要写清"左键点击消除"这个玩家侧交互，否则 AI 不会主动告诉玩家
  assert.match(t.description, /点击.*消除/);
});

test('mcp_help tool=post_map_marker：给出调用示例、前置条件与 shader 换色说明', async () => {
  const payload = JSON.parse(await withPromise(mod.mcpHelpResult({ tool: 'post_map_marker' })));
  assert.equal(payload.name, 'post_map_marker');
  assert.equal(payload.category, 'game_control');
  assert.equal(payload.availability.state, 'exposed');
  assert.match(payload.precondition, /进入地图/);
  assert.match(payload.example, /"x": ?123/);
  // 关键坑点必须写进 notes：内置 hellsphere shader 不能换色
  assert.match(payload.notes, /_Color/);
  assert.match(payload.notes, /点击/);
});

test('mcp_help path=game_control/game/gameplay/marker：叶子菜单可达该工具', async () => {
  const p1 = JSON.parse(await withPromise(mod.mcpHelpResult({ path: 'game_control/game/gameplay' })));
  assert.ok(p1.children.some((c) => c.name === 'marker'), 'gameplay 下应新增 marker 孙组');

  const p2 = JSON.parse(await withPromise(mod.mcpHelpResult({ path: 'game_control/game/gameplay/marker' })));
  assert.equal(p2.type ?? 'tools', 'tools');
  assert.ok(p2.tools.some((line) => line.includes('post_map_marker')), `叶子列表应含 post_map_marker: ${JSON.stringify(p2)}`);
});

test('post_map_marker：坐标/时长/颜色的常见别名归一为 x/z/ttl_seconds/color', () => {
  const a = mod.normalizeToolArgs('post_map_marker', { mapX: 12, mapZ: 34, colour: 'red' });
  assert.equal(a.x, 12);
  assert.equal(a.z, 34);
  assert.equal(a.color, 'red');

  const b = mod.normalizeToolArgs('post_map_marker', { x: 1, z: 2, duration: 30, text: '敌人' });
  assert.equal(b.ttl_seconds, 30);
  assert.equal(b.label, '敌人');

  // 已经是规范名时不被别名覆盖
  const c = mod.normalizeToolArgs('post_map_marker', { mapX: 9, x: 5 });
  assert.equal(c.x, 5);
});

test('post_map_marker：action=set 缺 x/z 时在本地直接报错（不发网络请求）', async () => {
  const text = await withPromise(mod.handleToolCall('post_map_marker', { action: 'set' }));
  assert.match(text, /需要整数 x 与 z/);
  assert.match(text, /post_map_marker/); // 附带正确调用范式
});

test('post_map_marker：未知 action 在本地直接报错', async () => {
  const text = await withPromise(mod.handleToolCall('post_map_marker', { action: 'explode', x: 1, z: 1 }));
  assert.match(text, /未知 action: explode/);
  assert.match(text, /set \/ clear \/ recolor \/ list/);
});

test('post_map_marker：不是 GABP 镜像（不因 GABP 未连接而不可用）', () => {
  const t = mod.toolAvailability('post_map_marker');
  assert.notEqual(t.state, 'unavailable', `本工具走 UE 通道，不该受 GABP 连接状态影响: ${JSON.stringify(t)}`);
});

// ---- 小工具：把 mcpHelpResult / handleToolCall 的返回整形为纯文本 ----
function withPromise(result) {
  return Promise.resolve(result).then((r) => {
    if (typeof r === 'string') return r;
    return (r.content || []).map((c) => c.text || '').join('');
  });
}

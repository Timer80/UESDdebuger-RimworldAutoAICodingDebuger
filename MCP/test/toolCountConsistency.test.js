import { test } from 'node:test';
import assert from 'node:assert/strict';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器/桥接
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

// ===========================================================================
// 工具数口径一致性（2026-09-20 修复）
// 背景：修复前 mcp_help 无参概览报 194，而它自己的 tool= 提示报 191（allToolNames().size）——
// 概览的 bridge/game_control 走静态 TOOL_SUBGROUP 条目数（含源码里已不存在的陈旧键），
// 其余类别走动态注册表，叶子列表又是第三套口径。本文件把这四者钉成同一套。
// ===========================================================================

const parse = async (value) => JSON.parse(
  await Promise.resolve(value).then((r) =>
    typeof r === 'string' ? r : (r.content || []).map((c) => c.text || '').join('')));

/** 从叶子行 "- [标记] 工具名 描述" 里取工具名 */
const nameOfLine = (line) => line.replace(/^-\s*/, '').replace(/^\[[^\]]*\]\s*/, '').split(/\s+/)[0];

test('概览 total == 各一级类别计数之和（自洽）', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  assert.equal(root.total, root.categories.reduce((a, c) => a + c.count, 0));
});

test('概览 total == menuVisible == 菜单可见工具数', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  assert.equal(root.menuVisible, root.total, 'menuVisible 必须等于 total');
  assert.equal(mod.menuUniverseNames().length, root.total,
    `menuUniverseNames()=${mod.menuUniverseNames().length} 与概览 total=${root.total} 不一致`);
});

test('菜单可见数 == 菜单宇宙大小；宇宙只可能比可调用集多出"注册表已登记但提供方未就绪"的镜像', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  const universe = mod.menuUniverseNames();
  const callable = mod.allToolNames();
  const concealed = [...callable].filter((n) => n.startsWith('hotreload_'));
  const universeSet = new Set(universe);
  const callableSet = new Set(callable);

  assert.equal(concealed.length, 3, '隐藏工具应为 hotreload_apply/watch/status 三个');
  assert.equal(root.concealedCount, concealed.length, '概览回报的 concealedCount 应一致');
  assert.equal(root.callableTotal, callable.size, '概览回报的 callableTotal 应等于 allToolNames().size');
  assert.equal(root.total, universe.length, `菜单可见 ${root.total} != 菜单宇宙 ${universe.length}`);

  // ① 可调用集里所有非隐藏工具都必须在菜单里（不漏）
  const missing = [...callableSet].filter((n) => !n.startsWith('hotreload_') && !universeSet.has(n));
  assert.deepEqual(missing, [], '可调用工具漏进菜单: ' + missing.join(', '));

  // ② 菜单里多出来的名字，只能是"注册表已登记、等提供方就绪"的镜像（GABP 离线时的正常情形）
  const extra = universe.filter((n) => !callableSet.has(n));
  const notMirror = extra.filter((n) => !/^(rimworld|rimbridge)\./.test(n));
  assert.deepEqual(notMirror, [], '菜单里出现了既不可调用、也不是注册镜像的名字: ' + notMirror.join(', '));

  // ③ GABP 已连接时（callable 含镜像）必须严格相等
  const mirrorsLive = [...callableSet].filter((n) => /^(rimworld|rimbridge)\./.test(n)).length;
  if (mirrorsLive > 0) {
    assert.equal(root.total + concealed.length, callable.size,
      `GABP 已连接时：菜单可见 ${root.total} + 隐藏 ${concealed.length} != 可调用 ${callable.size}`);
  }
});

test('game_control 概览计数 == 子组计数之和 == 各子组叶子 total 之和', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  const gc = root.categories.find((c) => c.path === 'game_control');
  const sumSubgroups = root.game_control.gameSubgroups.reduce((a, s) => a + s.count, 0);
  assert.equal(gc.count, sumSubgroups, 'game_control 计数与子组和不一致');
  assert.equal(root.game_control.game, sumSubgroups, 'game_control.game 与子组和不一致');

  let leafSum = 0;
  for (const s of root.game_control.gameSubgroups) {
    const leaf = await parse(mod.mcpHelpResult({ path: 'game_control/game/' + s.name }));
    leafSum += leaf.total;
  }
  assert.equal(gc.count, leafSum, `game_control 计数 ${gc.count} != 各子组叶子合计 ${leafSum}`);
});

test('bridge 概览计数 == 其叶子合计（lua_script + ops + wait）', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  const bridge = root.categories.find((c) => c.path === 'bridge');
  let leafSum = 0;
  for (const s of ['lua_script', 'ops', 'wait']) {
    const leaf = await parse(mod.mcpHelpResult({ path: 'bridge/' + s }));
    leafSum += leaf.total;
  }
  assert.equal(bridge.count, leafSum, `bridge 计数 ${bridge.count} != 叶子合计 ${leafSum}`);
});

test('叶子里的名字全部属于菜单宇宙；多出来的只能是"注册表已登记、等提供方就绪"的镜像', async () => {
  const universe = new Set(mod.menuUniverseNames());
  const callable = mod.allToolNames();
  const root = await parse(mod.mcpHelpResult({}));
  const leaves = [];
  for (const c of root.categories) {
    if (c.path === 'bridge') {
      for (const s of ['lua_script', 'ops', 'wait']) leaves.push('bridge/' + s);
    } else if (c.path === 'game_control') {
      for (const s of root.game_control.gameSubgroups) leaves.push('game_control/game/' + s.name);
    } else {
      leaves.push(c.path);
    }
  }
  const notInUniverse = [];
  const notCallableNorMirror = [];
  for (const p of leaves) {
    const leaf = await parse(mod.mcpHelpResult({ path: p }));
    // 非叶子子组会返回 children，需要再下一层
    const paths = leaf.tools ? [p] : (leaf.children || []).map((ch) => ch.path);
    for (const pp of paths) {
      const inner = pp === p ? leaf : await parse(mod.mcpHelpResult({ path: pp }));
      for (const line of inner.tools || []) {
        const n = nameOfLine(line);
        if (!universe.has(n)) notInUniverse.push(`${pp} → ${n}`);
        if (!callable.has(n) && !/^(rimworld|rimbridge)\./.test(n)) {
          notCallableNorMirror.push(`${pp} → ${n}`);
        }
      }
    }
  }
  assert.deepEqual(notInUniverse, [], '菜单里出现不在菜单宇宙中的名字: ' + notInUniverse.join(', '));
  assert.deepEqual(notCallableNorMirror, [],
    '菜单里出现既不可调用、也不是注册镜像的名字: ' + notCallableNorMirror.join(', '));
});

test('_reserved 预留项不得出现在菜单里', async () => {
  const names = mod.menuUniverseNames();
  assert.ok(!names.includes('camera_follow_thing'),
    'camera_follow_thing 是 _reserved 预留项，不应进入菜单/计数');
});

test('隐藏工具不得出现在任何菜单叶子中', async () => {
  const root = await parse(mod.mcpHelpResult({}));
  const all = [];
  for (const c of root.categories) {
    const targets = c.path === 'bridge'
      ? ['bridge/lua_script', 'bridge/ops', 'bridge/wait']
      : c.path === 'game_control'
        ? root.game_control.gameSubgroups.map((s) => 'game_control/game/' + s.name)
        : [c.path];
    for (const p of targets) {
      const leaf = await parse(mod.mcpHelpResult({ path: p }));
      for (const line of leaf.tools || []) all.push(nameOfLine(line));
    }
  }
  const leaked = all.filter((n) => n.startsWith('hotreload_'));
  assert.deepEqual(leaked, [], '隐藏工具漏进菜单: ' + leaked.join(', '));
});

import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：导入 index.js 之前必须设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const markerPath = path.join(here, '..', '.launch-compiler');

// ---------------------------------------------------------------------------
// start_game 的 csharpConsole 覆盖：口径归一（合法值/别名/非法值必须报错）
// 口径与游戏侧一致：UE 侧 StartupCompilerPreference.ParseLaunchOverride
// ---------------------------------------------------------------------------
test('csharpConsole 归一：合法值 = Roslyn / Mono.CSharp（含别名与大小写/空白）', () => {
  assert.deepEqual(mod.normalizeLaunchCompiler('Roslyn'), { token: 'Roslyn', error: null });
  assert.deepEqual(mod.normalizeLaunchCompiler('roslyn'), { token: 'Roslyn', error: null });
  assert.deepEqual(mod.normalizeLaunchCompiler('  Roslyn  '), { token: 'Roslyn', error: null });
  assert.deepEqual(mod.normalizeLaunchCompiler('Mono.CSharp'), { token: 'Mono.CSharp', error: null });
  assert.deepEqual(mod.normalizeLaunchCompiler('mono.csharp'), { token: 'Mono.CSharp', error: null });
  assert.deepEqual(mod.normalizeLaunchCompiler('mcs'), { token: 'Mono.CSharp', error: null });
  assert.deepEqual(mod.normalizeLaunchCompiler('MonoCSharp'), { token: 'Mono.CSharp', error: null });
});

test('csharpConsole 归一：未指定（undefined/null/空串）= 不覆盖，不是错误', () => {
  for (const v of [undefined, null, '', '   ']) {
    const r = mod.normalizeLaunchCompiler(v);
    assert.equal(r.token, null, `${String(v)} 应归一为『不覆盖』`);
    assert.equal(r.error, null, `${String(v)} 不应报错`);
  }
});

test('csharpConsole 归一：非法值必须带 error（绝不静默回退成『没指定』）', () => {
  for (const v of ['dotnet', 'csc', 'roslyn2', 'mono']) {
    const r = mod.normalizeLaunchCompiler(v);
    assert.equal(r.token, null, `${v} 不应产生 token`);
    assert.ok(r.error && r.error.length > 0, `${v} 必须报错`);
  }
  const nonString = mod.normalizeLaunchCompiler(42);
  assert.ok(nonString.error, '非字符串必须报错');
});

// ---------------------------------------------------------------------------
// 标记文件读写：写 ⇒ 文件内容 = token；清 ⇒ 文件消失（防上一次覆盖串到下一次启动）
// ---------------------------------------------------------------------------
test('标记文件：写 ⇒ 落在 MCP/.launch-compiler 且内容为归一后的 token', () => {
  try { fs.unlinkSync(markerPath); } catch { /* 不存在即目标状态 */ }
  const info = mod.writeLaunchCompilerMarker('Mono.CSharp');
  assert.equal(info.written, true);
  assert.equal(info.error, null);
  assert.equal(info.path, markerPath);
  assert.equal(fs.readFileSync(markerPath, 'utf-8'), 'Mono.CSharp');
});

test('标记文件：不指定 ⇒ 清掉残留标记（幂等）', () => {
  // 先造一个残留
  fs.writeFileSync(markerPath, 'Roslyn', 'utf-8');
  const first = mod.writeLaunchCompilerMarker(null);
  assert.equal(first.cleared, true);
  assert.equal(fs.existsSync(markerPath), false, '残留标记必须被清掉');
  const second = mod.writeLaunchCompilerMarker(null);
  assert.equal(second.cleared, false, '本来就没有残留时不该谎报 cleared');
  assert.equal(second.error, null);
});

// ---------------------------------------------------------------------------
// 派发层：非法 csharpConsole 必须在**启动游戏之前**被拒绝（无副作用）
// ---------------------------------------------------------------------------
test('start_game：非法 csharpConsole ⇒ INVALID_ARGUMENT 且不写标记、不启动', async () => {
  try { fs.unlinkSync(markerPath); } catch { /* 不存在即目标状态 */ }
  const res = await mod.handleToolCall('start_game', { csharpConsole: 'dotnet' });
  const text = res.content[0].text;
  assert.ok(/INVALID_ARGUMENT/.test(text), `应报 INVALID_ARGUMENT，实际: ${text}`);
  assert.equal(fs.existsSync(markerPath), false, '非法参数不得留下标记文件');
});

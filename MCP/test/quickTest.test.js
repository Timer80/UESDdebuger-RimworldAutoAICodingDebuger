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

// 取一段源码（[start, end) 标记之间），用于"这段链路里不许出现 X"的回归护栏
function sliceSource(startMarker, endMarker) {
  const s = source.indexOf(startMarker);
  assert.ok(s >= 0, `源码里找不到起始标记: ${startMarker}`);
  const e = source.indexOf(endMarker, s);
  assert.ok(e > s, `源码里找不到结束标记: ${endMarker}`);
  return source.slice(s, e);
}

// ---------------------------------------------------------------------------
// 回归护栏 1：快速测试链路与 GABP 无关（这就是 2026-09-19 用户报的"quick-test 需要 GABP"）
// ---------------------------------------------------------------------------
test('护栏：start_quick_test 链路（下发+归因）源码里不得出现 GABP/RimBridge 连接性依赖', () => {
  const chain = sliceSource('// ---------- 快速测试触发链路（与 GABP 无关） ----------', '// Tool definitions');
  assert.ok(chain.length > 500, '取到的链路源码过短，标记可能已失效');
  // 允许注释里出现"与 GABP 无关"这类说明，但不允许任何实际的 GABP 调用/变量
  const codeOnly = chain
    .split('\n')
    .filter((l) => !/^\s*(\/\/|\*|\/\*)/.test(l))   // 去注释行
    .join('\n');
  assert.ok(!/\bgabp/i.test(codeOnly), 'start_quick_test 链路不应触碰 gabpBridge/GABP');
  assert.ok(!/\bensureGabpConnected|callGABPTool|callPlayFor\b/.test(codeOnly),
    'start_quick_test 链路不应调用任何 GABP 相关函数');
});

test('护栏：start_quick_test 派发必须发生在 GABP 镜像块之前（否则会被镜像连接性检查拦下）', () => {
  const caseAt = source.indexOf("case 'start_quick_test'");
  const gabpBlockAt = source.indexOf('if (isGabpMirrorName(name)) {');
  assert.ok(caseAt > 0, "找不到 case 'start_quick_test'");
  assert.ok(gabpBlockAt > 0, '找不到 GABP 镜像分支');
  assert.ok(caseAt > gabpBlockAt, 'switch 内的 case 位于镜像分支之后（handleToolCall 结构变化，请复核）');
  // 真正要保证的语义：原生工具名不进镜像分支
  assert.ok(!/start_quick_test/.test(source.slice(gabpBlockAt, source.indexOf('\n  switch (name) {'))),
    'start_quick_test 不应出现在 GABP 镜像分支内部');
});

test('护栏：工具描述显式声明不依赖 GABP/RimBridgeServer', () => {
  const toolDef = sliceSource("name: 'start_quick_test'", '// ========== UnityExplorer (UE) 工具 ==========');
  assert.match(toolDef, /不需要 GABP\/RimBridgeServer/);
});

// ---------------------------------------------------------------------------
// 归因正确性：三种真实原因要能区分开（并且不许再说成"UE 未就绪"）
// ---------------------------------------------------------------------------
test('buildQuickTestServiceError：服务自报 failed → 带出真实原因，且不扯 UE/GABP', () => {
  const err = mod.buildQuickTestServiceError(
    { reason: 'connect', retriable: true, message: '游戏内服务未响应：fetch failed（http://127.0.0.1:3001/trigger-quicktest）' },
    'MAIN_MENU',
    { ueHttpPort: 3001, hasToken: true, ueHttpStatus: 'failed', ueHttpError: '未能在 3001~3010 找到可用端口', updatedAt: '2026-09-19T13:00:00+08:00' }
  );
  assert.equal(err.success, false);
  assert.equal(err.errorCode, 'QUICKTEST_SERVICE_UNREACHABLE');
  assert.match(err.message, /未能在 3001~3010 找到可用端口/);
  assert.match(err.message, /ueHttpStatus=failed/);
  assert.equal(err.currentStage, 'MAIN_MENU');
  // 旧版错误文案会写成 "UE 未就绪"，把排查带偏
  assert.ok(!/UE 未就绪/.test(err.message), `不应再报 UE 未就绪: ${err.message}`);
  assert.match(err.guidance, /不依赖 GABP\/RimBridgeServer/);
  assert.match(err.guidance, /ModsConfig/);
});

test('buildQuickTestServiceError：ports.json 缺失（模组没加载）→ 明确指向模组未启用', () => {
  const err = mod.buildQuickTestServiceError(
    { reason: 'connect', retriable: true, message: '游戏内服务未响应：Unable to connect.' },
    'MAIN_MENU',
    null
  );
  assert.equal(err.errorCode, 'QUICKTEST_SERVICE_UNREACHABLE');
  assert.match(err.message, /读不到 .*ports\.json/);
  assert.match(err.message, /UESDdebuger 模组未加载/);
});

test('buildQuickTestServiceError：401（token 滚动）→ 点出 token 属上一次会话', () => {
  const err = mod.buildQuickTestServiceError(
    { kind: 'auth', reason: 'HTTP 401', retriable: true, httpStatus: 401, message: '游戏内服务返回 401 Unauthorized（http://127.0.0.1:3001/trigger-quicktest）' },
    'MAIN_MENU',
    { ueHttpPort: 3001, hasToken: true, ueHttpStatus: null, ueHttpError: null, updatedAt: null }
  );
  assert.equal(err.errorCode, 'QUICKTEST_SERVICE_UNREACHABLE');
  assert.match(err.message, /token 属上一次会话/);
});

test('buildQuickTestRejectedError：ALREADY_IN_GAME 等业务拒绝 → 透出游戏侧语义，不误报"服务不可达"', () => {
  const err = mod.buildQuickTestRejectedError({
    kind: 'business',
    reason: 'HTTP 400',
    retriable: false,
    httpStatus: 400,
    payloadErrorCode: 'ALREADY_IN_GAME',
    message: '游戏内服务返回 400 Bad Request：已在游戏内（http://127.0.0.1:3001/trigger-quicktest）',
  });
  assert.equal(err.success, false);
  assert.equal(err.errorCode, 'ALREADY_IN_GAME');
  assert.match(err.guidance, /主菜单/);
  assert.ok(!/未响应|不可达/.test(err.message), '业务拒绝不应被说成服务不可达');
});

test('护栏：业务拒绝分支与不可达分支的 kind 判定互斥（源码级）', () => {
  const fn = sliceSource('async function sendQuickTestTrigger()', '// 触发被游戏侧**正常拒绝**');
  assert.match(fn, /kind: isBusiness \? 'business' : \(isAuth \? 'auth' : 'unreachable'\)/);
  assert.match(fn, /retriable: isAuth \|\| response\.status >= 500/);
});

test('readPortsFileInfo：真实 ports.json 能读出端口/token/服务状态字段', () => {
  const info = mod.readPortsFileInfo();
  // 文件可能不存在（干净环境）→ null 也算合法；存在则字段类型必须对
  if (info) {
    assert.ok(info.ueHttpPort === null || Number.isInteger(info.ueHttpPort));
    assert.equal(typeof info.hasToken, 'boolean');
    assert.ok(info.ueHttpStatus === null || typeof info.ueHttpStatus === 'string');
  }
});

// ---------------------------------------------------------------------------
// 端到端（无游戏环境的失败路径）：错误码必须是快速测试专属那一档，而不是 UE_NOT_READY
//
// 注意：这条会真的向游戏内 HTTP 服务下发 /trigger-quicktest。若此刻游戏正在跑（有服务在监听），
// 下发了就等于真的让游戏进测试地图——会打断别人正在做的测试。所以先探一次服务可达性：
// 可达 → 跳过（真机实测留给人工/验收），不可达 → 才跑失败路径断言。
// ---------------------------------------------------------------------------
test('handleToolCall(start_quick_test)：无游戏/无服务时给出专属错误码（不是 UE_NOT_READY）', async (t) => {
  const info = mod.readPortsFileInfo();
  const base = `http://127.0.0.1:${info && info.ueHttpPort ? info.ueHttpPort : 3001}`;
  let serviceAlive = false;
  try {
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), 800);
    const resp = await fetch(`${base}/unityexplorer/status`, { signal: ctl.signal });
    clearTimeout(timer);
    serviceAlive = resp.ok;
  } catch (e) { serviceAlive = false; }
  if (serviceAlive) {
    t.skip(`游戏内 HTTP 服务在 ${base} 可达（有游戏在跑）：跳过真实下发，避免触发真机 quick test`);
    return;
  }

  const res = await mod.handleToolCall('start_quick_test', { timeout: 2000 });
  const body = JSON.parse(res.content[0].text);
  assert.equal(body.success, false);
  assert.ok(['GAME_NOT_RUNNING', 'QUICKTEST_SERVICE_UNREACHABLE'].includes(body.errorCode),
    `错误码应为 GAME_NOT_RUNNING 或 QUICKTEST_SERVICE_UNREACHABLE，实际 ${body.errorCode}: ${res.content[0].text.slice(0, 300)}`);
  assert.ok(!/UE 未就绪/.test(res.content[0].text), '不应再出现误导性的"UE 未就绪"');
  if (body.errorCode === 'QUICKTEST_SERVICE_UNREACHABLE') {
    assert.match(body.guidance, /UESDdebuger/);
    assert.match(body.message, /ports\.json/);
  }
});

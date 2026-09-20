#!/usr/bin/env node
/**
 * 工具总数计算器（权威口径）
 *
 * 背景：这个仓库里"工具数"有四个可能的来源，很容易数错：
 *   1. index.js 源码里的注册入口 —— TOOLS 数组字面量 + `TOOLS.push({...})`
 *      + AGG_META_TOOLS + MONO_DEBUG_TOOL_NAMES   ← **本脚本认这个作为本地工具**
 *   2. MCP/toolConfig.json —— 每个工具的暴露开关（注册表），用于交叉校验
 *   3. GABP 侧镜像工具（rimworld.* / rimbridge.*）—— 运行时由 RimBridgeServer 动态提供，
 *      本脚本按注册表里的前缀键计数（静态下界），并与 `--gabp <n>` 传入的实测值对比
 *   4. mcp_help 的菜单计数 —— 它是**静态菜单表**（TOOL_SUBGROUP / TOOL_CATEGORY）的条目数，
 *      混了 bridge/game_control 的静态分组与其余类别的动态注册表，**不是工具总数**，禁止当口径
 *
 * 用法：
 *   node scripts/count-tools.mjs                    # 数工作区
 *   node scripts/count-tools.mjs --base HEAD        # 与 git 版本对比，输出逐项差值
 *   node scripts/count-tools.mjs --base HEAD --json # 机器可读
 *   node scripts/count-tools.mjs --gabp 125         # 传入实测 GABP 工具数（get_game_status.gabp.toolsCount）
 *   node scripts/count-tools.mjs --live [url]       # 连运行中的服务器实测 listTools
 *
 * --live 的前提：MCP 服务器以 **SSE** 方式启动（如 scripts 外的 start-mcp.ps1，默认 127.0.0.1:3000/sse）。
 * 若服务器由宿主以 **stdio** 方式拉起（本仓库在 DSH 环境里就是这种），根本没有 TCP 监听，
 * --live 会报 ECONNREFUSED —— 此时"运行中工具数"请改从工具面取（`agg_list_tools` 自报的数量）。
 *
 * 注意：源码里的 `TOOLS` 是数组字面量，但工具也可能经 `TOOLS.push({...})` 追加注册
 * （post_map_marker 就是这么加的），只数字面量会漏。工作区文件可能是 CRLF，取源时统一成 LF。
 */

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { cwd: scriptDir, encoding: 'utf8' }).trim();
const relIndex = 'MCP/index.js';
const relConfig = 'MCP/toolConfig.json';

// ---------------------------------------------------------------- 参数
const argv = process.argv.slice(2);
const asJson = argv.includes('--json');
const baseRev = argv.includes('--base') ? argv[argv.indexOf('--base') + 1] : null;
const gabpOverride = argv.includes('--gabp') ? Number(argv[argv.indexOf('--gabp') + 1]) : null;

// ---------------------------------------------------------------- 取源
// 注意：工作区文件可能是 CRLF（core.autocrlf=true），而 git show 出来的是 LF。
// 正则里用 `\n` 锚定行首/行尾，所以必须先把行尾统一成 LF，否则同一份代码会数出两个结果。
function normalizeNewlines(s) {
  return s.replace(/\r\n?/g, '\n');
}

function readFrom(rev, relPath) {
  if (!rev) return normalizeNewlines(readFileSync(join(repoRoot, relPath), 'utf8'));
  return normalizeNewlines(execFileSync('git', ['show', `${rev}:${relPath}`], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }));
}

/** 截取 `const X = [ ... \n];` 形式的块（含起止行） */
function sliceBlock(src, startMarker, endMarker) {
  const s = src.indexOf(startMarker);
  if (s < 0) throw new Error(`找不到起始标记: ${startMarker}`);
  const e = src.indexOf(endMarker, s + startMarker.length);
  if (e < 0) throw new Error(`找不到结束标记: ${endMarker}`);
  return src.slice(s, e + endMarker.length);
}

/** 从源码算本地工具清单 */
function countSource(src) {
  // 1) TOOLS 数组字面量：元素形如 "\n  {\n    name: 'x',"
  const toolsBlock = sliceBlock(src, 'const TOOLS = [', '\n];');
  const literal = new Set([...toolsBlock.matchAll(/\n {2}\{\n {4}name: '([^']+)'/g)].map((m) => m[1]));

  // 2) TOOLS.push({ name: 'x', ... }) —— 追加注册（数组字面量之外）
  const pushed = new Set([...src.matchAll(/TOOLS\.push\(\{\s*\n\s*name: '([^']+)'/g)].map((m) => m[1]));

  // 3) AGG_META_TOOLS
  const metaBlock = sliceBlock(src, 'const AGG_META_TOOLS = [', '\n];');
  const meta = new Set([...metaBlock.matchAll(/\{ name: '([^']+)'/g)].map((m) => m[1]));

  // 4) MONO_DEBUG_TOOL_NAMES（mono 桥接就绪后由 monoTools 提供）
  const monoBlock = sliceBlock(src, 'const MONO_DEBUG_TOOL_NAMES = new Set([', ']);');
  const mono = new Set([...monoBlock.matchAll(/'([A-Za-z0-9_]+)'/g)].map((m) => m[1]));

  const local = new Set([...literal, ...pushed, ...meta, ...mono]);
  return { literal, pushed, meta, mono, local };
}

/** 注册表：toolConfig.json 的键 */
function countRegistry(cfgSrc) {
  const cfg = JSON.parse(cfgSrc);
  const keys = Object.keys(cfg.tools);
  const mirrors = new Set(keys.filter((k) => /^(rimworld|rimbridge)\./.test(k)));
  const native = new Set(keys.filter((k) => !/^(rimworld|rimbridge)\./.test(k)));
  // `_reserved`：刻意保留但尚未实现的工具名（例如 camera_follow_thing——C# 已实现、MCP 侧未实现）。
  // 这类键**不算漂移**，单独报，别当陈旧键删。
  const reservedRaw = cfg._reserved || {};
  const reserved = new Set(Array.isArray(reservedRaw) ? reservedRaw : Object.keys(reservedRaw));
  return { keys: new Set(keys), mirrors, native, reserved, enabled: keys.filter((k) => cfg.tools[k] === true) };
}

function analyze(label, indexSrc, configSrc) {
  const src = countSource(indexSrc);
  const reg = countRegistry(configSrc);
  const localList = [...src.local];
  return {
    label,
    parts: {
      'TOOLS 数组字面量': src.literal.size,
      'TOOLS.push(...)': src.pushed.size,
      AGG_META_TOOLS: src.meta.size,
      MONO_DEBUG_TOOL_NAMES: src.mono.size,
    },
    localNames: localList,
    localTotal: localList.length,
    registryKeys: reg.keys.size,
    registryNative: reg.native.size,
    mirrorNames: [...reg.mirrors],
    mirrorTotal: reg.mirrors.size,
    enabledCount: reg.enabled.length,
    reservedNames: [...reg.reserved],
    // 交叉校验
    definedButUnregistered: localList.filter((n) => !reg.keys.has(n)),
    registeredButUndefined: [...reg.native].filter((n) => !src.local.has(n) && !reg.reserved.has(n)),
    // 同一名字出现在多个注册入口（多为刻意：如 meta 与 TOOLS 并存）
    duplicateAcrossEntries: [
      ...intersect(src.literal, src.pushed),
      ...intersect(src.literal, src.meta),
      ...intersect(src.pushed, src.meta),
    ],
  };
}

function intersect(a, b) {
  return [...a].filter((x) => b.has(x)).map((x) => `${x}`);
}

const cur = analyze('工作区', readFrom(null, relIndex), readFrom(null, relConfig));
const base = baseRev ? analyze(baseRev, readFrom(baseRev, relIndex), readFrom(baseRev, relConfig)) : null;

// ---------------------------------------------------------------- 输出
const pad = (s, n) => String(s) + ' '.repeat(Math.max(0, n - [...String(s)].reduce((a, c) => a + (c.charCodeAt(0) > 255 ? 2 : 1), 0)));
const w1 = 34, w2 = 12, w3 = 12;

if (asJson) {
  console.log(JSON.stringify({ workingTree: cur, base }, null, 2));
  process.exit(0);
}

console.log(`仓库: ${repoRoot}`);
console.log(`对比基准: ${base ? baseRev : '(无，仅数工作区)'}\n`);
console.log(pad('注册入口', w1) + pad('工作区', w2) + pad(base ? baseRev : '', w3) + (base ? '差值' : ''));
console.log('-'.repeat(w1 + w2 + w3 + 8));

let deltaTotal = 0;
for (const key of Object.keys(cur.parts)) {
  const c = cur.parts[key];
  const b = base ? base.parts[key] : null;
  const d = base ? c - b : null;
  if (d) deltaTotal += d;
  console.log(pad(key, w1) + pad(c, w2) + pad(base ? b : '', w3) + (base ? (d > 0 ? `+${d}` : d === 0 ? '0' : d) : ''));
}

const row = (label, c, b) => {
  const d = base ? c - b : null;
  console.log(pad(label, w1) + pad(c, w2) + pad(base ? b : '', w3) + (base ? (d > 0 ? `+${d}` : d === 0 ? '0' : d) : ''));
};

console.log('-'.repeat(w1 + w2 + w3 + 8));
row('本地工具合计', cur.localTotal, base ? base.localTotal : 0);
row('GABP 镜像（注册表前缀键）', cur.mirrorTotal, base ? base.mirrorTotal : 0);
console.log('='.repeat(w1 + w2 + w3 + 8));
row('★ 工具总数（本地+镜像）', cur.localTotal + cur.mirrorTotal, base ? base.localTotal + base.mirrorTotal : 0);
row('toolConfig 注册表键数', cur.registryKeys, base ? base.registryKeys : 0);
row('其中 enabled=true', cur.enabledCount, base ? base.enabledCount : 0);

if (base) {
  const added = cur.localNames.filter((n) => !base.localNames.includes(n));
  const removed = base.localNames.filter((n) => !cur.localNames.includes(n));
  const mirrorsAdded = cur.mirrorNames.filter((n) => !base.mirrorNames.includes(n));
  const mirrorsRemoved = base.mirrorNames.filter((n) => !cur.mirrorNames.includes(n));
  console.log(`\n本地工具 新增(${added.length}): ${added.join(', ') || '—'}`);
  console.log(`本地工具 删除(${removed.length}): ${removed.join(', ') || '—'}`);
  console.log(`镜像 新增(${mirrorsAdded.length}): ${mirrorsAdded.slice(0, 12).join(', ') || '—'}${mirrorsAdded.length > 12 ? ' …' : ''}`);
  console.log(`镜像 删除(${mirrorsRemoved.length}): ${mirrorsRemoved.slice(0, 12).join(', ') || '—'}${mirrorsRemoved.length > 12 ? ' …' : ''}`);
}

console.log('\n交叉校验');
console.log(`  源码已定义但注册表缺键（无法开关）: ${cur.definedButUnregistered.join(', ') || '无'}`);
console.log(`  注册表有键但源码无定义（陈旧键）  : ${cur.registeredButUndefined.join(', ') || '无'}`);
console.log(`  预留未发布（_reserved，非漂移）    : ${cur.reservedNames.join(', ') || '无'}`);
console.log(`  同名跨入口重复                    : ${cur.duplicateAcrossEntries.join(', ') || '无'}`);

if (gabpOverride !== null) {
  console.log('\nGABP 实测对比');
  console.log(`  运行时实测 GABP 工具数: ${gabpOverride}`);
  console.log(`  注册表前缀键数        : ${cur.mirrorTotal}`);
  console.log(`  差                    : ${gabpOverride - cur.mirrorTotal}`);
  console.log(`  ⇒ 工具总数（本地 + 实测镜像）= ${cur.localTotal + gabpOverride}`);
}

// ---------------------------------------------------------------- --live：问运行中的服务器要真值
const liveArg = argv.includes('--live')
  ? (argv[argv.indexOf('--live') + 1] && !argv[argv.indexOf('--live') + 1].startsWith('--')
      ? argv[argv.indexOf('--live') + 1]
      : 'http://127.0.0.1:3000/sse')
  : null;

if (liveArg) {
  console.log(`\n运行中服务器实测（${liveArg}）`);
  try {
    const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
    const { SSEClientTransport } = await import('@modelcontextprotocol/sdk/client/sse.js');
    const client = new Client({ name: 'count-tools', version: '1.0.0' });
    const withTimeout = (p, ms, label) => Promise.race([
      p,
      new Promise((_, rej) => setTimeout(() => rej(new Error(`${label} 超时 ${ms}ms`)), ms).unref?.()),
    ]);
    await withTimeout(client.connect(new SSEClientTransport(new URL(liveArg))), 8000, 'connect');
    const liveNames = (await withTimeout(client.listTools(), 8000, 'listTools')).tools.map((t) => t.name);
    client.close().catch(() => {});

    const liveSet = new Set(liveNames);
    const known = new Set([...cur.localNames, ...cur.mirrorNames]);
    const unregistered = liveNames.filter((n) => !known.has(n));
    const notLive = [...known].filter((n) => !liveSet.has(n));

    console.log(`  listTools 实测工具数  : ${liveNames.length}`);
    console.log(`  对得上（注册表∪源码） : ${liveNames.length - unregistered.length}`);
    console.log(`  实测有、注册表/源码无 : ${unregistered.length} ${unregistered.length ? '→ ' + unregistered.join(', ') : ''}`);
    console.log(`  注册表/源码有、实测无 : ${notLive.length} ${notLive.length ? '→ ' + notLive.join(', ') : ''}`);
    console.log(`  对账: 实测 ${liveNames.length} + 未列出 ${notLive.length} = ${liveNames.length + notLive.length}（应等于 本地 ${cur.localTotal} + 镜像 ${cur.mirrorTotal} = ${cur.localTotal + cur.mirrorTotal}）`);
  } catch (err) {
    console.log(`  ⚠ 连不上或调用失败: ${err.message}`);
  }
}

// SSE 是长连接，必须显式退出，否则进程挂着不结束
process.exit(0);

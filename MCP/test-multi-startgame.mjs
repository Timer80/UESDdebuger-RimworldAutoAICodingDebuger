#!/usr/bin/env node
/**
 * 多次测试：连续 N 轮 start_game，每轮验证 Debug(Player) 是否被自动关闭、主窗口是否出现。
 * 用法：node test-multi-startgame.mjs [rounds=2]
 */
'use strict';
import { spawn, exec } from 'node:child_process';
import { promisify } from 'node:util';
import { createInterface } from 'node:readline';
const execAsync = promisify(exec);

const ROUNDS = parseInt(process.argv[2] || '2', 10);
const START_TIMEOUT_MS = 130000;
const WINDOW_POLL_MS = 1500;

async function listWindowsOfPid(pid) {
  const script = `
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public static class WinList2 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  public static string[] List(uint target) {
    var titles = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == target) {
        var t = new StringBuilder(256); GetWindowText(h, t, t.Capacity);
        if (IsWindowVisible(h) && t.Length > 0) titles.Add(t.ToString());
      }
      return true;
    }, IntPtr.Zero);
    return titles.ToArray();
  }
}
"@
$t = [WinList2]::List(${pid})
$t -join " | "
`;
  const encoded = Buffer.from(script, 'utf16le').toString('base64');
  try {
    const { stdout } = await execAsync(
      `powershell -NoProfile -ExecutionPolicy Bypass -EncodedCommand ${encoded}`,
      { timeout: 10000, maxBuffer: 1024 * 1024 }
    );
    return stdout.trim().split('\n').map((s) => s.trim()).filter(Boolean);
  } catch {
    return [];
  }
}

async function stopGame() {
  await execAsync('taskkill /F /IM RimWorldWin64.exe /T').catch(() => {});
}

// ---- MCP stdio 客户端 ----
function makeClient() {
  const child = spawn('bun', ['run', 'index.js', '--stdio'], {
    stdio: ['pipe', 'pipe', 'pipe'],
    env: { ...process.env, MCP_TRANSPORT: 'stdio' },
  });
  const pending = new Map();
  let nextId = 1;
  const rl = createInterface({ input: child.stdout, crlfDelay: Infinity });
  rl.on('line', (line) => {
    const t = line.trim();
    if (!t) return;
    let msg;
    try { msg = JSON.parse(t); } catch { return; }
    if (msg.id !== undefined && pending.has(msg.id)) {
      const p = pending.get(msg.id);
      pending.delete(msg.id);
      clearTimeout(p.timer);
      p.resolve(msg);
    }
  });
  child.stderr.on('data', (d) => {
    const s = d.toString().trim();
    if (s) console.log('  [server]', s);
  });
  const request = (method, params, timeoutMs) => {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error('timeout ' + method)); }, timeoutMs);
      pending.set(id, { resolve, reject, timer });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params: params || {} }) + '\n');
    });
  };
  const callTool = async (name, args, timeoutMs) => {
    const msg = await request('tools/call', { name, arguments: args || {} }, timeoutMs);
    if (msg.error) throw new Error('rpc error: ' + JSON.stringify(msg.error));
    const content = msg.result && msg.result.content || [];
    let text = '';
    for (const c of content) if (c.type === 'text') text += c.text;
    try { return JSON.parse(text); } catch { return { raw: text }; }
  };
  return { child, request, callTool };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

console.log(`==== 多次测试开始：${ROUNDS} 轮 ====`);
let pass = 0, fail = 0;

for (let round = 1; round <= ROUNDS; round++) {
  console.log(`\n---- 第 ${round}/${ROUNDS} 轮 ----`);
  await stopGame();
  await sleep(2000);

  const client = makeClient();
  try {
    await client.request('initialize', {
      protocolVersion: '2024-11-05', capabilities: {},
      clientInfo: { name: 'test-multi', version: '1.0.0' },
    }, 10000);
    client.child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');

    // 并行轮询窗口
    let gamePid = null;
    let debugSeen = false, mainSeen = false, closed = false;
    const poller = (async () => {
      const deadline = Date.now() + START_TIMEOUT_MS + 15000;
      while (Date.now() < deadline) {
        if (gamePid === null) {
          const { stdout } = await execAsync('tasklist /FI "IMAGENAME eq RimWorldWin64.exe" /FO CSV /NH').catch(() => ({ stdout: '' }));
          const line = stdout.trim().split('\n').find((l) => l.includes('RimWorldWin64.exe'));
          if (line) {
            gamePid = line.split('","')[1].replace('"', '');
            console.log(`  [poll] 游戏进程 pid=${gamePid}`);
          }
        }
        if (gamePid) {
          const titles = await listWindowsOfPid(gamePid);
          if (titles.includes('Debug (Player)')) debugSeen = true;
          const main = titles.filter((t) => t !== 'Debug (Player)');
          if (main.length > 0) mainSeen = true;
          // 结束态判定：主窗口在、且 Debug(Player) 已不在当前窗口列表
          if (mainSeen && !titles.includes('Debug (Player)')) {
            closed = true;
            console.log(`  [poll] 主窗口出现且 Debug(Player) 已消失（${titles.join(' / ')}）`);
            return;
          }
        }
        await sleep(WINDOW_POLL_MS);
      }
    })();

    const t0 = Date.now();
    const res = await client.callTool('start_game', { useSteam: false, waitForNotification: true, timeout: START_TIMEOUT_MS }, START_TIMEOUT_MS + 15000);
    const elapsed = Date.now() - t0;
    await poller.catch(() => {});

    const okFlag = res && res.success === true;
    const winClosed = closed; // 结束态：主窗口在且 Debug 窗口已消失（由 poller 判定）
    const verdict = okFlag && winClosed ? 'PASS' : 'FAIL';
    if (verdict === 'PASS') pass++; else fail++;
    console.log(`  第 ${round} 轮: ${verdict}（${elapsed}ms）`);
    console.log(`  start_game: ${JSON.stringify(res).slice(0, 220)}`);
    console.log(`  窗口状态: debugSeen=${debugSeen} mainSeen=${mainSeen} closed=${closed} → winClosed=${winClosed}`);
  } catch (e) {
    fail++;
    console.log(`  第 ${round} 轮异常: ${e.message}`);
  } finally {
    client.child.kill();
    await stopGame();
    await sleep(2000);
  }
}

console.log(`\n==== 汇总：${pass}/${ROUNDS} PASS ====`);
process.exit(fail > 0 ? 1 : 0);

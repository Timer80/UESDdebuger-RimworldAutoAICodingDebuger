#!/usr/bin/env node
/**
 * RimWorld MCP 聚合器（Aggregator）
 * - 聚合上游 MCP 服务器（默认 rimworld_DebugInEnvironment，SSE http://127.0.0.1:3000/sse）
 * - 支持压缩工具列表：只暴露 2 个元工具 + coreTools 白名单
 * - 非压缩模式：暴露上游全部工具（多上游同名冲突以 上游名__工具名 暴露并登记别名）
 * - 端口默认 3100（SSE: /sse, Health: /health）
 */

import express from 'express';
import cors from 'cors';
import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { SSEServerTransport } from '@modelcontextprotocol/sdk/server/sse.js';
import { ListToolsRequestSchema, CallToolRequestSchema } from '@modelcontextprotocol/sdk/types.js';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { SSEClientTransport } from '@modelcontextprotocol/sdk/client/sse.js';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// ========== 配置加载 ==========
const CONFIG_PATH = path.join(__dirname, 'config.json');
function loadConfig() {
  const raw = fs.readFileSync(CONFIG_PATH, 'utf-8');
  return JSON.parse(raw);
}
const config = loadConfig();

// 压缩开关：环境变量 AGG_COMPRESS（"true"/"false"）仅当显式设置时覆盖 config.compress，便于测试
let compress = config.compress === true;
if (process.env.AGG_COMPRESS !== undefined) {
  compress = process.env.AGG_COMPRESS === 'true';
}

const HOST = config.host || '127.0.0.1';
const PORT = config.port || 3100;

// ========== 日志机制（照抄 UESDdebuger/MCP/index.js 第 80-97 行） ==========
const LOGS_DIR = path.join(__dirname, 'logs');
try { fs.mkdirSync(LOGS_DIR, { recursive: true }); } catch (e) { /* 忽略目录创建失败 */ }

function _pad2(n, w = 2) { return String(n).padStart(w, '0'); }
function _formatLogTime(d) { return `${_pad2(d.getHours())}:${_pad2(d.getMinutes())}:${_pad2(d.getSeconds())}.${_pad2(d.getMilliseconds(), 3)}`; }
function _formatFileStamp(d) { return `${d.getFullYear()}${_pad2(d.getMonth() + 1)}${_pad2(d.getDate())}-${_pad2(d.getHours())}${_pad2(d.getMinutes())}${_pad2(d.getSeconds())}`; }

const LOG_FILE_PATH = path.join(LOGS_DIR, `mcp-${_formatFileStamp(new Date())}.log`);
const logStream = fs.createWriteStream(LOG_FILE_PATH, { flags: 'a' });

// 统一日志：格式 [HH:mm:ss.SSS] <LEVEL> msg，同时 console 输出与写入日志文件
function log(level, msg) {
  const line = `[${_formatLogTime(new Date())}] <${level}> ${msg}`;
  try { console.log(line); } catch (e) { /* ignore */ }
  try { logStream.write(line + '\n'); } catch (e) { /* ignore */ }
}

// 工具参数摘要（照抄 UESDdebuger/MCP/index.js 第 99-117 行）：仅保留关键字段，长度受限，避免刷屏
function summarizeToolArgs(name, args) {
  if (!args || typeof args !== 'object') return '{}';
  const parts = [];
  for (const [k, v] of Object.entries(args)) {
    if (v === undefined || v === null) continue;
    let sv;
    if (k === 'code' && typeof v === 'string') {
      sv = JSON.stringify(v.slice(0, 80));
    } else if (typeof v === 'string') {
      sv = v;
    } else {
      sv = JSON.stringify(v);
    }
    if (typeof sv === 'string' && sv.length > 80) sv = sv.slice(0, 80) + '…';
    parts.push(`${k}=${sv}`);
  }
  return parts.length ? `{${parts.join(', ')}}` : '{}';
}

// ========== 元工具定义（压缩模式额外暴露） ==========
const META_TOOLS = [
  {
    name: 'agg_list_tools',
    description: 'List tools as a multi-level menu. Without path: upstream health + top-level overview (categories). With path: drill into a menu subtree (path is delegated to upstream mcp_help).',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'menu path, e.g. "bridge", "bridge/lua_script", "game_control", "game_control/game", "game_control/game/gameplay", "game_control/game/gameplay/debug_action"' }
      },
      additionalProperties: false
    }
  },
  {
    name: 'agg_call_tool',
    description: 'Call any tool on any upstream server',
    inputSchema: {
      type: 'object',
      properties: {
        server: { type: 'string', description: 'upstream name' },
        tool: { type: 'string', description: 'tool name' },
        args: { type: 'object', description: 'tool arguments', additionalProperties: true }
      },
      required: ['server', 'tool']
    }
  }
];

// ========== 上游管理 ==========
const upstreams = [];           // 全部上游条目（含 disabled）
const routeMap = new Map();     // toolName → { upstreamName, originalName }

// 连接单个上游；任何失败都不抛异常，仅标记 healthy:false + error
async function connectUpstream(cfg) {
  const entry = {
    name: cfg.name,
    type: cfg.type,
    url: cfg.url,
    enabled: cfg.enabled !== false,
    client: null,
    transport: null,
    healthy: false,
    error: null,
    tools: new Map() // name → toolDef
  };
  upstreams.push(entry);

  if (!entry.enabled) {
    log('INFO', `上游 ${cfg.name} 已禁用，跳过连接`);
    return entry;
  }
  try {
    if (cfg.type !== 'sse') {
      entry.error = `不支持的 type: ${cfg.type}（仅支持 sse，保留字段供扩展）`;
      log('WARN', `上游 ${cfg.name} ${entry.error}`);
      return entry;
    }
    const transport = new SSEClientTransport(new URL(cfg.url));
    const client = new Client(
      { name: 'rimworld-aggregator', version: '1.0.0' },
      { capabilities: {} }
    );
    await client.connect(transport);
    const { tools } = await client.listTools();
    entry.client = client;
    entry.transport = transport;
    entry.healthy = true;
    entry.error = null;
    entry.tools = new Map(tools.map(t => [t.name, t]));
    log('INFO', `上游 ${cfg.name} 连接成功 (${cfg.url})，工具数=${tools.length}`);
  } catch (e) {
    entry.healthy = false;
    entry.error = e && e.message ? e.message : String(e);
    log('WARN', `上游 ${cfg.name} 连接失败: ${entry.error}`);
  }
  return entry;
}

// 重连单个上游（新建 client/transport，刷新工具索引）；失败冷却 + 超时保护（P2-MCP-3）
const RECONNECT_COOLDOWN_MS = 5000;
const RECONNECT_TIMEOUT_MS  = 8000;
async function tryReconnect(u) {
  if (!u) return false;
  const now = Date.now();
  if (u._lastReconnectAt && now - u._lastReconnectAt < RECONNECT_COOLDOWN_MS) return false; // 冷却期不重复重连
  u._lastReconnectAt = now;
  log('INFO', `尝试重连上游 ${u.name} ...`);
  try {
    if (u.client) { try { await u.client.close(); } catch (e) { /* ignore */ } }
    if (u.transport) { try { await u.transport.close(); } catch (e) { /* ignore */ } }
    const transport = new SSEClientTransport(new URL(u.url));
    const client = new Client(
      { name: 'rimworld-aggregator', version: '1.0.0' },
      { capabilities: {} }
    );
    // 超时保护：SSE connect 的 transport.start() 阶段无内置超时——上游假死后可能永不 resolve，
    // 因此用 Promise.race 兜底整体超时（reject 后剩余 pending 的 connect 由 transport.close() 释放）。
    const control = new AbortController();
    let timer = null;
    const connectPromise = Promise.race([
      client.connect(transport, { signal: control.signal }),
      new Promise((_, reject) => {
        timer = setTimeout(() => {
          control.abort();
          try { transport.close(); } catch (e) { /* ignore */ }
          reject(new Error(`重连超时（${RECONNECT_TIMEOUT_MS}ms）`));
        }, RECONNECT_TIMEOUT_MS);
      })
    ]);
    try {
      await connectPromise;
      clearTimeout(timer);
      const { tools } = await client.listTools();
      u.client = client;
      u.transport = transport;
      u.tools = new Map(tools.map(t => [t.name, t]));
      u.healthy = true;
      u.error = null;
      log('INFO', `上游 ${u.name} 重连成功，工具数=${tools.length}`);
      return true;
    } finally {
      clearTimeout(timer);
    }
  } catch (e) {
    u.healthy = false;
    u.error = e && e.message ? e.message : String(e);
    log('WARN', `上游 ${u.name} 重连失败: ${u.error}`);
    return false;
  }
}

// 按需重连：对 enabled 且 unhealthy 的上游，每次 ListTools/CallTool 请求到来时并行尝试重连一次
async function ensureHealthy() {
  const targets = upstreams.filter(u => u.enabled && !u.healthy);
  if (targets.length === 0) return;
  const results = await Promise.all(targets.map(u => tryReconnect(u)));   // 并行
  if (results.some(Boolean)) rebuildRouteMap();
}

// 重建全局路由表 routeMap：toolName → { upstreamName, originalName }
function rebuildRouteMap() {
  routeMap.clear();
  const healthy = upstreams.filter(u => u.enabled && u.healthy);

  if (compress) {
    // 压缩模式：coreTools 白名单按原名从健康上游工具池取，第一个拥有该工具的上游作为拥有者
    for (const coreName of config.coreTools || []) {
      for (const u of healthy) {
        if (u.tools.has(coreName)) {
          routeMap.set(coreName, { upstreamName: u.name, originalName: coreName });
          break;
        }
      }
    }
    return;
  }

  // 非压缩模式：全量合并；同名冲突工具以 上游名__工具名 暴露并登记别名（别名指向第一个声明者）
  const nameCount = new Map();
  for (const u of healthy) for (const n of u.tools.keys()) nameCount.set(n, (nameCount.get(n) || 0) + 1);
  const firstOwner = new Map();
  for (const u of healthy) for (const n of u.tools.keys()) if (!firstOwner.has(n)) firstOwner.set(n, u.name);

  for (const u of healthy) {
    for (const n of u.tools.keys()) {
      if (nameCount.get(n) > 1) {
        const exposed = `${u.name}__${n}`;
        routeMap.set(exposed, { upstreamName: u.name, originalName: n });
        if (!routeMap.has(n)) {
          // 别名：工具原名 → 第一个声明该工具的上游
          routeMap.set(n, { upstreamName: firstOwner.get(n), originalName: n });
        }
      } else {
        routeMap.set(n, { upstreamName: u.name, originalName: n });
      }
    }
  }
}

// 当前模式下列表工具（ListTools 返回值 tools 数组）
function getAggregatedTools() {
  const healthy = upstreams.filter(u => u.enabled && u.healthy);
  if (compress) {
    const tools = META_TOOLS.map(t => ({ ...t }));
    for (const coreName of config.coreTools || []) {
      for (const u of healthy) {
        if (u.tools.has(coreName)) {
          tools.push({ ...u.tools.get(coreName) });
          break;
        }
      }
    }
    return tools;
  }
  // 非压缩：合并所有健康上游工具，冲突以 上游名__工具名 展开
  const nameCount = new Map();
  for (const u of healthy) for (const n of u.tools.keys()) nameCount.set(n, (nameCount.get(n) || 0) + 1);
  const tools = [];
  for (const u of healthy) {
    for (const [n, def] of u.tools) {
      if (nameCount.get(n) > 1) tools.push({ ...def, name: `${u.name}__${n}` });
      else tools.push({ ...def });
    }
  }
  return tools;
}

// agg_list_tools 处理：多级菜单。有 path → 透传上游 mcp_help {path}；无 path → 前置各上游健康状态行 + 透传上游 mcp_help 无参（一级概览）
async function handleAggListTools(args) {
  const path = args && typeof args.path === 'string' && args.path.trim() ? args.path.trim() : null;
  const enabled = upstreams.filter(u => u.enabled);
  const healthy = enabled.filter(u => u.healthy);

  // 所有上游均不可用：健康降级提示
  if (healthy.length === 0) {
    const lines = enabled.map(u =>
      `[server:${u.name}] <unhealthy> 0 tools${u.error ? ` (${u.error})` : ''}`);
    return {
      content: [{ type: 'text', text: `所有上游均不可用，无法获取工具目录。\n${lines.join('\n')}` }],
      isError: true
    };
  }

  const target = healthy[0];
  try {
    const result = await target.client.callTool({
      name: 'mcp_help',
      arguments: path ? { path } : {}
    });
    const text = (result.content || []).map(c => (c.type === 'text' ? c.text : '')).join('\n');
    // 无 path 时前置各上游健康状态行，便于直接判断可用性
    const prefix = path
      ? ''
      : enabled.map(u =>
          `[server:${u.name}] ${u.healthy ? `<healthy> ${u.tools.size} tools` : `<unhealthy> 0 tools${u.error ? ` (${u.error})` : ''}`}`).join('\n') + '\n\n';
    return { content: [{ type: 'text', text: prefix + text }], isError: result.isError === true };
  } catch (e) {
    target.healthy = false;
    target.error = `调用失败，标记不可用待重连: ${e.message}`;
    throw e;
  }
}

// 调用上游工具；连接层异常时标记该上游 unhealthy（下次请求自动重连），并重新抛出
async function callUpstreamTool(u, originalName, args) {
  try {
    const result = await u.client.callTool({ name: originalName, arguments: args });
    return { content: result.content, isError: result.isError === true };
  } catch (e) {
    u.healthy = false;
    u.error = `调用失败，标记不可用待重连: ${e.message}`;
    throw e;
  }
}

// agg_call_tool 处理：server 校验 + 透传上游 agg_call_tool 元工具
// 语义（spec aggregator-tools-switches 修订）：开关=是否暴露给 AI，不控制能否使用。
// 因此不再用 u.tools.has(tool) 门禁（u.tools 仅含上游已启用工具），改为透传上游
// agg_call_tool——上游用 allToolNames()（含禁用工具）做存在性校验并直接执行。
// 不可直接 client.callTool({name: 禁用工具})：上游普通路径有 isToolEnabled 检查会拒绝。
async function handleAggCallTool(args) {
  const serverName = args && args.server;
  const tool = args && args.tool;
  const toolArgs = (args && args.args) || {};
  if (!serverName) {
    return { content: [{ type: 'text', text: '缺少参数 server（上游名称），正确调用范式: agg_call_tool { server: "<上游名>", tool: "<工具名>", args: { ... } }' }], isError: true };
  }
  if (!tool) {
    return { content: [{ type: 'text', text: '缺少参数 tool（工具名称），正确调用范式: agg_call_tool { server: "<上游名>", tool: "<工具名>", args: { ... } }' }], isError: true };
  }
  const u = upstreams.find(x => x.name === serverName);
  if (!u) {
    return { content: [{ type: 'text', text: `未知上游: ${serverName}，可用上游: ${upstreams.filter(x => x.enabled).map(x => x.name).join(', ')}，可调用 agg_list_tools { server: "<上游名>" } 或直接 agg_list_tools 查看可用上游` }], isError: true };
  }
  if (!u.healthy) {
    return { content: [{ type: 'text', text: `上游 ${serverName} 当前不可用: ${u.error || '未知错误'}` }], isError: true };
  }
  try {
    const result = await u.client.callTool({
      name: 'agg_call_tool',
      arguments: { tool, args: toolArgs },
    });
    return { content: result.content, isError: result.isError === true };
  } catch (e) {
    u.healthy = false;
    u.error = `调用失败，标记不可用待重连: ${e.message}`;
    throw e;
  }
}

// 按路由表执行普通工具调用（coreTools 直调 / 非压缩模式明细工具 / 冲突别名）
async function handleRoutedCall(name, args) {
  const route = routeMap.get(name);
  if (!route) {
    return { content: [{ type: 'text', text: `未知工具: ${name}（可用工具请调用 agg_list_tools 查看；或 mcp_help { tool: "<工具名>" } 查询用法）` }], isError: true };
  }
  const u = upstreams.find(x => x.name === route.upstreamName);
  if (!u || !u.healthy) {
    return { content: [{ type: 'text', text: `上游 ${route.upstreamName} 不可用` }], isError: true };
  }
  return await callUpstreamTool(u, route.originalName, args || {});
}

// ========== MCP 服务器（每个 SSE 连接创建一个实例，处理器逻辑共享） ==========
function createAggServer() {
  const server = new Server(
    { name: 'rimworld-mcp-aggregator', version: '1.0.0' },
    { capabilities: { tools: {} } }
  );

  server.setRequestHandler(ListToolsRequestSchema, async () => {
    await ensureHealthy();       // unhealthy 上游按需重连
    rebuildRouteMap();           // 保证路由表与工具池一致
    return { tools: getAggregatedTools() };
  });

  server.setRequestHandler(CallToolRequestSchema, async (request) => {
    const { name, arguments: args } = request.params;
    const startMs = Date.now();
    const summary = summarizeToolArgs(name, args);
    let result;
    try {
      await ensureHealthy();     // 每次调用前按需重连
      rebuildRouteMap();
      if (name === 'agg_list_tools') {
        result = await handleAggListTools(args || {});
      } else if (name === 'agg_call_tool') {
        result = await handleAggCallTool(args || {});
      } else {
        result = await handleRoutedCall(name, args || {});
      }
      const elapsed = Date.now() - startMs;
      log('INFO', `tools/call ${name} args=${summary} → ${elapsed}ms isError=${result.isError === true}`);
      return result;
    } catch (error) {
      const elapsed = Date.now() - startMs;
      log('ERROR', `tools/call ${name} args=${summary} → ${elapsed}ms error=${error.message}`);
      // 元工具（agg_list_tools / agg_call_tool / mcp_help）失败时仅返回错误本身，不加调用范式提示
      const isMetaTool = ['agg_list_tools', 'agg_call_tool', 'mcp_help'].includes(name);
      const usageHint = isMetaTool ? '' : '\n正确调用范式: agg_call_tool { tool: "<工具名>", args: { ... } }（或直接调用工具名）';
      return { content: [{ type: 'text', text: `调用失败: ${error.message}${usageHint}` }], isError: true };
    }
  });

  return server;
}

// ========== HTTP 服务（参照 UESDdebuger/MCP/index.js 第 2476-2626 行模式） ==========
const app = express();
app.use(cors());

// 按 sessionId 保存活跃的 SSE transport
const transports = new Map();

// SSE 端点
app.get('/sse', async (req, res) => {
  log('INFO', 'SSE client connecting...');
  const transport = new SSEServerTransport('/message', res);
  const sessionId = transport.sessionId;
  transports.set(sessionId, transport);
  log('INFO', `SSE session ${sessionId} connected`);
  try {
    const server = createAggServer();
    await server.connect(transport);
  } catch (error) {
    log('ERROR', `Server connection error (session ${sessionId}): ${error.message}`);
    transports.delete(sessionId);
    return;
  }
  req.on('close', () => {
    log('INFO', `SSE session ${sessionId} disconnected`);
    transports.delete(sessionId);
  });
});

// 消息端点（不用 express.json()，SSE transport 自行解析 body）
app.post('/message', async (req, res) => {
  // sessionId 通常由 SDK 客户端放在 query 中；兼容 body 携带的情况
  const sessionId = req.query.sessionId || (req.body && req.body.sessionId);
  if (!sessionId) {
    log('WARN', 'POST /message missing sessionId');
    return res.status(400).json({ error: 'Missing sessionId' });
  }
  const transport = transports.get(sessionId);
  if (transport) {
    await transport.handlePostMessage(req, res);
  } else {
    log('WARN', `POST /message no active connection for session ${sessionId}`);
    res.status(400).json({ error: 'No active connection for session' });
  }
});

// 健康检查
app.get('/health', (req, res) => {
  const tools = getAggregatedTools();
  res.setHeader('Content-Type', 'application/json');
  res.end(JSON.stringify({
    status: fatalErrorCount > 5 ? 'degraded' : 'ok',
    state: fatalErrorCount > 5 ? 'degraded' : 'ok',
    fatalErrorCount,
    compress,
    toolCount: tools.length,
    upstreams: upstreams.map(u => ({
      name: u.name,
      type: u.type,
      healthy: u.healthy,
      error: u.error,
      toolCount: u.tools.size
    }))
  }));
});

// 未捕获异常/拒绝：记录日志后不退出
let fatalErrorCount = 0;
process.on('uncaughtException', (err) => {
  fatalErrorCount += 1;
  log('ERROR', `uncaughtException: ${err && err.stack ? err.stack : String(err)}`);
  log('WARN', `第 ${fatalErrorCount} 次未捕获异常；连续多次请重启（P2-MCP-3）。`);
});
process.on('unhandledRejection', (reason) => {
  log('ERROR', `unhandledRejection: ${reason instanceof Error ? (reason.stack || reason.message) : String(reason)}`);
});

// ========== 启动 ==========
async function main() {
  // 逐个连接 enabled 上游，失败不阻断启动
  for (const cfg of config.upstreams || []) {
    await connectUpstream(cfg);
  }
  rebuildRouteMap();

  const healthyCount = upstreams.filter(u => u.enabled && u.healthy).length;
  log('INFO', `上游连接完成: ${healthyCount}/${upstreams.filter(u => u.enabled).length} healthy, compress=${compress}`);

  const httpServer = app.listen(PORT, HOST, () => {
    log('INFO', `Aggregator listening on http://${HOST}:${PORT} (SSE: /sse, Health: /health)`);
    console.log('========================================');
    console.log('  RimWorld MCP Aggregator (SSE Mode)');
    console.log('========================================');
    console.log(`Aggregator running at http://${HOST}:${PORT}`);
    console.log(`SSE endpoint: http://${HOST}:${PORT}/sse`);
    console.log(`Health check: http://${HOST}:${PORT}/health`);
    console.log(`Compress: ${compress}`);
    console.log(`Log file: ${LOG_FILE_PATH}`);
    console.log('========================================');
    console.log('');
  });

  // 端口占用：提示并退出码 1
  httpServer.on('error', (err) => {
    if (err && err.code === 'EADDRINUSE') {
      const msg = `端口 ${PORT} 已被占用，聚合器启动失败（请先停止占用该端口的进程）`;
      log('ERROR', msg);
      console.error(`[ERROR] ${msg}`);
      process.exit(1);
    }
    const msg = `HTTP 服务错误: ${err && err.message ? err.message : String(err)}`;
    log('ERROR', msg);
    console.error(`[ERROR] ${msg}`);
    process.exit(1);
  });
}

main();

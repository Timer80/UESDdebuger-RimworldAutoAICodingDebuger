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
    description: 'List all available tools across upstream servers with health status',
    inputSchema: { type: 'object', properties: {}, additionalProperties: false }
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

// 重连单个上游（新建 client/transport，刷新工具索引）
async function tryReconnect(u) {
  log('INFO', `尝试重连上游 ${u.name} ...`);
  try {
    if (u.client) { try { await u.client.close(); } catch (e) { /* ignore */ } }
    if (u.transport) { try { await u.transport.close(); } catch (e) { /* ignore */ } }
    const transport = new SSEClientTransport(new URL(u.url));
    const client = new Client(
      { name: 'rimworld-aggregator', version: '1.0.0' },
      { capabilities: {} }
    );
    await client.connect(transport);
    const { tools } = await client.listTools();
    u.client = client;
    u.transport = transport;
    u.tools = new Map(tools.map(t => [t.name, t]));
    u.healthy = true;
    u.error = null;
    log('INFO', `上游 ${u.name} 重连成功，工具数=${tools.length}`);
    return true;
  } catch (e) {
    u.healthy = false;
    u.error = e && e.message ? e.message : String(e);
    log('WARN', `上游 ${u.name} 重连失败: ${u.error}`);
    return false;
  }
}

// 按需重连：对 enabled 且 unhealthy 的上游，每次 ListTools/CallTool 请求到来时尝试重连一次
async function ensureHealthy() {
  let changed = false;
  for (const u of upstreams) {
    if (u.enabled && !u.healthy) {
      if (await tryReconnect(u)) changed = true;
    }
  }
  if (changed) rebuildRouteMap();
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

// agg_list_tools 的目录文本：每上游一行状态 + 每工具一行
function buildDirectoryText() {
  const lines = [];
  for (const u of upstreams) {
    if (!u.enabled) continue;
    if (!u.healthy) {
      lines.push(`[server:${u.name}] <unhealthy> 0 tools${u.error ? ` (${u.error})` : ''}`);
      continue;
    }
    lines.push(`[server:${u.name}] <healthy> ${u.tools.size} tools`);
    for (const [toolName, def] of u.tools) {
      let desc = String(def.description || '').split(/\r?\n/)[0] || '';
      if (desc.length > 200) desc = desc.slice(0, 200) + '…';
      lines.push(`- ${toolName} ${desc}`);
    }
  }
  return lines.join('\n');
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

// agg_call_tool 处理：server/tool 校验 + 路由透传
async function handleAggCallTool(args) {
  const serverName = args && args.server;
  const tool = args && args.tool;
  const toolArgs = (args && args.args) || {};
  if (!serverName) {
    return { content: [{ type: 'text', text: '缺少参数 server（上游名称）' }], isError: true };
  }
  if (!tool) {
    return { content: [{ type: 'text', text: '缺少参数 tool（工具名称）' }], isError: true };
  }
  const u = upstreams.find(x => x.name === serverName);
  if (!u) {
    return { content: [{ type: 'text', text: `未知上游: ${serverName}，可用上游: ${upstreams.filter(x => x.enabled).map(x => x.name).join(', ')}` }], isError: true };
  }
  if (!u.healthy) {
    return { content: [{ type: 'text', text: `上游 ${serverName} 当前不可用: ${u.error || '未知错误'}` }], isError: true };
  }
  if (!u.tools.has(tool)) {
    return { content: [{ type: 'text', text: `上游 ${serverName} 不存在工具: ${tool}` }], isError: true };
  }
  return await callUpstreamTool(u, tool, toolArgs);
}

// 按路由表执行普通工具调用（coreTools 直调 / 非压缩模式明细工具 / 冲突别名）
async function handleRoutedCall(name, args) {
  const route = routeMap.get(name);
  if (!route) {
    return { content: [{ type: 'text', text: `未知工具: ${name}（可用工具请调用 agg_list_tools 查看）` }], isError: true };
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
        result = { content: [{ type: 'text', text: buildDirectoryText() }], isError: false };
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
      return { content: [{ type: 'text', text: `调用失败: ${error.message}` }], isError: true };
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
    status: 'ok',
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
process.on('uncaughtException', (err) => {
  log('ERROR', `uncaughtException: ${err && err.stack ? err.stack : String(err)}`);
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

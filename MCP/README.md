# RimWorld Debug MCP 服务器

这个MCP服务器为RimWorld提供调试和开发工具，包括游戏控制、日志查看、DebugAction执行、UnityExplorer集成和RIMAPI功能。

## 功能

- **启动游戏**: 通过Steam或直接启动RimWorld（独立于游戏运行，可直接调用启动）
- **关闭游戏**: 正常或强制关闭游戏进程
- **查看状态**: 检查游戏是否正在运行，以及所处阶段（停止/启动中/主菜单/游戏内）
- **读取日志**: 查看游戏日志文件的最后N行
- **实时监控**: 获取日志文件的最新内容和文件信息
- **UnityExplorer 集成**: 游戏内代码执行、类型检查、Hook、日志等 11 个 UE 工具
- **RIMAPI 集成**: 游戏内殖民地/世界数据查询
- **日志终端与保存**: 启动时打开终端实时显示日志并连续落盘

## 游戏阶段与工具前置条件

服务器独立于游戏运行。游戏处于以下四阶段之一（`get_game_status` 返回 `stage` 字段）：

| 阶段 | 说明 |
|---|---|
| `GAME_STOPPED` | 游戏进程未运行 |
| `GAME_STARTING` | 进程存在但尚未就绪（启动 <20s 且双通道不可达） |
| `MAIN_MENU` | 已到主菜单，未进入地图 |
| `IN_GAME` | 已进入地图/世界 |

工具按前置条件分级，不满足时返回统一错误结构：

```json
{ "success": false, "errorCode": "GAME_NOT_RUNNING",
  "currentStage": "GAME_STOPPED", "requiredStage": ["IN_GAME"],
  "message": "游戏未运行", "guidance": "先用 start_game 启动游戏（waitForNotification=true 会等待主菜单就绪），再调用本工具" }
```

| 工具类 | 所需阶段 | 错误码 | 实现方法 |
|---|---|---|---|
| `start_game` / `get_game_status` | 任意 | - | 直接可用 |
| UE 工具（11 个，见下） | `IN_GAME` | `GAME_NOT_RUNNING` / `GAME_STARTING` / `MAP_NOT_LOADED` | `start_game` 启动 → `start_quick_test` 快速进测试地图 |
| RIMAPI 工具 | `IN_GAME`（RIMAPI 就绪） | `GAME_NOT_RUNNING` / `RIMAPI_NOT_READY` / `MAP_NOT_LOADED` | 同上 |

`start_quick_test` 可在主菜单直接调用：触发游戏内官方 DevQuickTest 流程（经 `UESDdebuger/UELoader` 的 `POST /trigger-quicktest`）自动进入测试地图，返回 `state:"map_loaded"`。

端口约定：
- **MCP 服务器**：默认 **stdio 本地接入**（由客户端直接 spawn 进程，不监听端口）；SSE 模式（`MCP_PORT`，默认 3000）保留用于手动调试
- **游戏内 UE HTTP 服务**：**动态端口**，从 3001 起递增探测空闲端口（UESDdebuger/UELoader 提供），实际端口由游戏侧写入 `UESDdebuger/MCP/ports.json`
- **RIMAPI**：`8765`（第三方模组固定端口，可通过环境变量 `RIMAPI_BASE_URL` 覆盖）

### 端口文件（ports.json）

游戏侧启动后写入 `UESDdebuger/MCP/ports.json`，MCP 服务器与 McpRimDebug 从中读取实际端口：

```json
{
  "ueHttpPort": 3001,
  "unityDebugPort": 56651,
  "updatedAt": "2026-08-07T17:00:00+08:00"
}
```

- `ueHttpPort`：UE HTTP 服务实际监听端口（3001 被占用时自动递增）
- `unityDebugPort`：Unity 调试端口（来自 Player.log 的 `Starting managed debugger on port XXXX`，未就绪时为 null）
- 文件缺失时：MCP 回退 3001，McpRimDebug 回退解析 Player.log

## 配置

编辑 `config.json` 文件：

```json
{
  "gamePath": "F:/RimWorld3/RimWorldWin64.exe",
  "logPath": "C:/Users/%USERNAME%/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log",
  "steamPath": "C:/Program Files (x86)/Steam/steam.exe",
  "appId": "294100"
}
```

### 配置项说明

- `gamePath`: RimWorld可执行文件路径
- `logPath`: 游戏日志文件路径（%USERNAME%会被自动替换为当前用户名）
- `steamPath`: Steam可执行文件路径
- `appId`: RimWorld的Steam应用ID（294100）

## 可用工具

### start_game
启动RimWorld游戏
- 参数:
  - `useSteam` (boolean): 是否通过Steam启动，默认为true

### stop_game
关闭RimWorld游戏
- 参数:
  - `force` (boolean): 是否强制关闭，默认为false

### get_game_status
获取游戏运行状态
- 返回: 运行状态、进程ID、内存使用

### read_log
读取游戏日志
- 参数:
  - `lines` (number): 读取最后多少行，默认100

### tail_log
获取日志最新内容（包含文件信息）
- 参数:
  - `lines` (number): 读取最后多少行，默认50

### get_config
获取当前配置信息

## 接入方式

### 默认：stdio 本地接入（推荐）

客户端（Trae/Claude 等）直接 spawn 本地 bun 进程，通过 stdin/stdout 通信，**不监听任何端口**：

```json
{
  "mcpServers": {
    "rimworld_DebugInEnvironment": {
      "command": "bun",
      "args": ["run", "C:/SteamLibrary/steamapps/common/RimWorld/Mods/UESDdebuger/MCP/index.js"],
      "env": { "MCP_TRANSPORT": "stdio" }
    }
  }
}
```

> 注意：`command` 必须用 `bun`；不要用 `npx`/`node`（node 下无 bun 运行时，且 npx 包装器会污染 stdout）。
> 便携 bun（推荐）：用 `<UESDdebuger>/runtime/bun/bun.exe`，无需本机安装 bun。工坊版只携带 `runtime/bun/bun.zip`（bun.exe 本体约 113MB 超过工坊上传限制），**首次启动游戏时模组自动解压出 bun.exe**；若尚未启动过游戏，先启动一次再连接。
> 便携 node 同理：`runtime/node/node.exe` 由 `runtime/node/node.zip` 首次启动自动解压（start-mcp.ps1 等脚本优先使用便携 node）。
> `MCP/node_modules/` 同理：工坊版只带 `MCP/node_modules.zip`（3700+ 碎文件打包成单个 zip），**首次启动游戏时模组自动解压出 node_modules**。

配置位置：
- Trae（用户级）：`%APPDATA%/Trae CN/User/mcp.json`
- Claude Desktop：`%APPDATA%/Claude/claude_desktop_config.json`

### 可选：SSE 手动模式（调试用）

需要手动先启动服务器并暴露 `MCP_PORT`（默认 3000）端口，客户端用 `url` 连接：

```powershell
# 打开新 PowerShell 窗口运行并实时显示日志（Tee 落盘双通道）
.\start-mcp.ps1
# 或直接运行
bun run index.js
```

客户端配置：

```json
{
  "mcpServers": {
    "rimworld_DebugInEnvironment": {
      "url": "http://127.0.0.1:3000/sse"
    }
  }
}
```

SSE 端点 `http://127.0.0.1:3000/sse`、健康检查 `http://127.0.0.1:3000/health`。此模式保留用于手动调试（如查看实时日志、用 curl 探活）。

## 启动服务器（SSE 手动模式）

```powershell
# 推荐：打开新 PowerShell 窗口运行并实时显示日志（Tee 落盘双通道）
.\start-mcp.ps1

# 或直接运行（SSE 模式为默认，stdio 模式用 $env:MCP_TRANSPORT='stdio'）
bun run index.js
```

MCP 日志双通道：
- `logs/mcp-<时间戳>.log`：index.js 内部统一日志（`[HH:mm:ss.SSS] <LEVEL> 消息`），连续追加
- `logs/mcp-window-<时间戳>.log`：窗口侧 Tee 备份
- 游戏侧：每次 UE 工具调用都会在 `Player.log` 记录 `[UEHttp] <METHOD> /<path> args={...} → ok/error(<code>), <耗时>ms`

## UnityExplorer 工具（需 IN_GAME）

| 工具 | 说明 |
|---|---|
| `get_unityexplorer_status` | UE 就绪状态（uiReady） |
| `execute_csharp_code` | 执行 C# 代码（UE Console） |
| `reset_csharp_console` / `add_using_directive` | 控制台维护 |
| `inspect_type` | 类型检查（Inspector） |
| `create_hook` / `toggle_hook` / `list_hooks` / `delete_hook` | 方法 Hook 管理 |
| `get_unityexplorer_logs` / `clear_unityexplorer_logs` | UE 日志查看/清空 |

## 注意事项

1. 确保配置的路径正确
2. 日志路径中的 `%USERNAME%` 会被自动替换为当前系统用户名
3. 强制关闭游戏可能导致存档丢失
4. UE 工具需游戏进入地图（IN_GAME）后可用；主菜单时返回 `MAP_NOT_LOADED` 并提供 guidance

---

# MCP 聚合器（MCP Aggregator）

目录：`UESDdebuger/MCP/aggregator/`。聚合器作为**中间层**，向上聚合本服务器的 SSE 接口（`http://127.0.0.1:3000/sse`，即上游 3000 服务器），对外提供统一的 MCP SSE 端点（默认 `http://127.0.0.1:3100/sse`），并支持**压缩工具列表**：默认只暴露 2 个元工具 + coreTools 白名单，避免向客户端暴露全部约 63 个工具。

## 用途

- **聚合**：将 3000 服务器的全部工具聚合到单一入口（支持多个上游，未来可扩展）。
- **压缩**：默认模式只暴露 `agg_list_tools` / `agg_call_tool` 两个元工具 + coreTools 白名单（`start_game` / `start_quick_test` / `stop_game` / `get_game_status` / `read_log` / `tail_log` / `get_config`），工具列表从 63 个压缩到 9 个。
- **按需重连**：上游不可用时不会导致聚合器崩溃，每次请求到来时自动尝试重连一次。

## 配置（aggregator/config.json）

```json
{
  "host": "127.0.0.1",
  "port": 3100,
  "compress": true,
  "coreTools": ["start_game", "start_quick_test", "stop_game", "get_game_status", "read_log", "tail_log", "get_config"],
  "upstreams": [
    { "name": "rimworld", "type": "sse", "url": "http://127.0.0.1:3000/sse", "enabled": true }
  ]
}
```

| 配置项 | 说明 |
|---|---|
| `host` / `port` | 聚合器监听地址（默认 127.0.0.1:3100） |
| `compress` | 压缩开关。`true` 只暴露 2 元工具 + coreTools 白名单；`false` 暴露上游全部工具（多上游同名冲突以 `上游名__工具名` 暴露并登记别名）。可用环境变量 `AGG_COMPRESS=true/false` 覆盖（便于测试） |
| `coreTools` | 压缩模式下白名单工具名，从各健康上游工具池按原名取出直接暴露 |
| `upstreams` | 上游服务器列表。`type` 目前仅支持 `sse`；`enabled:false` 跳过连接；单个上游连接失败只标记 unhealthy，不会阻断启动 |

## 两种模式

- **压缩模式（compress=true，默认）**：`listTools` 返回 2 个元工具 + 7 个白名单工具。元工具：
  - `agg_list_tools`：无参数，返回所有上游的工具目录与健康状态文本。
  - `agg_call_tool`：参数 `{server, tool, args}`（server/tool 必填），可调用任意上游的任意工具。
  - 白名单工具（如 `get_game_status`）直接调用即路由到拥有者上游。
- **非压缩模式（compress=false）**：`listTools` 返回上游全部工具合并清单（约 63 个），同名冲突以 `上游名__工具名` 暴露，原名登记为别名路由到第一个声明者。

## 与 3000 服务器的关系

- 聚合器是 3000 服务器的**客户端**：聚合器启动时通过 SSE 连接 3000 服务器并拉取工具清单。
- **3000 服务器必须先行启动**（`.\start-mcp.ps1`），否则聚合器会以 unhealthy 状态启动并在请求到来时自动重连。
- 3000 服务器本身无需任何改动。

## 启动方式

```powershell
# 先启动上游 3000 服务器
cd UESDdebuger/MCP
.\start-mcp.ps1

# 再启动聚合器（打开新 PowerShell 窗口运行 node index.js，Tee 双通道日志）
cd UESDdebuger/MCP/aggregator
.\start-aggregator.ps1
# 或直接运行 node index.js
```

- SSE 端点：`http://127.0.0.1:3100/sse`
- 健康检查：`http://127.0.0.1:3100/health`
- 日志：`aggregator/logs/mcp-<时间戳>.log`（内部）与 `mcp-window-<时间戳>.log`（窗口 Tee）
- 端口被占用时提示并退出码 1

## Trae mcp.json 接入示例

```json
{
  "mcpServers": {
    "rimworld_aggregated": {
      "url": "http://127.0.0.1:3100/sse"
    }
  }
}
```

## 端到端验证

```powershell
# 压缩模式（默认）
node agg-e2e.mjs

# 非压缩模式（需以 AGG_COMPRESS=false 启动聚合器）
$env:AGG_COMPRESS = 'false'
node index.js
node agg-e2e.mjs   # 脚本通过 AGG_COMPRESS 感知模式
```

## 工具开关配置（toolConfig.json）

`MCP/toolConfig.json` 是 **per-tool 启用开关**配置文件，控制每个工具是否出现在工具列表并可被调用。

### 配置结构

```json
{
  "defaultEnabled": false,
  "tools": {
    "agg_list_tools": true,
    "agg_call_tool": true,
    "start_game": true,
    "start_quick_test": true,
    "get_game_status": true,
    "read_log": true,
    "tail_log": true,
    "get_config": true
  }
}
```

| 配置项 | 说明 |
|---|---|
| `defaultEnabled` | 未在 `tools` 中列出的工具的默认启用值，缺省视为 `true` |
| `tools` | 工具名 → 布尔值的映射，逐个覆盖对应工具的启用状态 |

### 默认预设（压缩预设）

开箱即用（无 `toolConfig.json`）为**压缩预设**：仅启用 10 个工具：

- 3 个元/帮助工具：`agg_list_tools`、`agg_call_tool`、`mcp_help`
- 7 个核心高频工具：`start_game`、`start_quick_test`、`stop_game`、`get_game_status`、`read_log`、`tail_log`、`get_config`

### 切换全量

将 `defaultEnabled` 改为 `true`（或把 `tools` 中所有项置为 `true`），即可启用全部工具。

### 元工具用法

- `agg_list_tools`：无参数，列出全部工具并标注状态（`[启用]` / `[禁用]`），便于发现可通过聚合器调用的工具。
- `agg_call_tool`：参数 `{tool, args}`，可调用任意已存在的工具（**包括未直接暴露的禁用工具**），例如：

```json
{ "tool": "get_map_structure", "args": {} }
```

> 禁用语义：工具值为 `false` 时仅从工具列表隐藏（AI 无法直接调用）；直接调用会返回「工具已禁用」错误，但通过 `agg_call_tool` 仍可正常调用。

> 注意：修改 `toolConfig.json` 后需**重启 MCP 服务器**（或重连会话）才能生效。

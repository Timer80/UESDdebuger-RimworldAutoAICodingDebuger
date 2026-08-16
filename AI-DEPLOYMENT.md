# UESDdebuger — AI 部署文件（给 AI 看的部署指引）

> 面向 **AI 代理** 的端到端部署与调试部署文档。人类读者请移步 `MCP/first-configs.md`（各 IDE 的 MCP 配置代码）。
> 本文的目标：给出**决策 + 命令 + 验收标准**，让 AI 无需人工干预即可部署、启动、连接并排查本调试工具链。

---

## 1. 这是什么

`UESDdebuger` 是一个 RimWorld 调试模组，把以下组件打包成一个整体，目标是在**无监管状态下让 AI 自动调试游戏**（俗称“修红字”）：

| 组件 | 作用 | 接入面 |
|---|---|---|
| **UnityExplorer**（UE 4.9.0，官方二进制零修改） | 游戏内对象浏览器 / C# 控制台 / Inspector / Hook 工具；**按 F7 显示/隐藏界面** | 游戏内 HTTP 服务（动态端口，见 ports.json `ueHttpPort`） |
| **McpRimDebug**（SDB 代码级调试器） | 把游戏状态与调试能力暴露给外部 | 由 MCP 服务器自动调用 |
| **RimWorld API（RIMAPI）**（附属模组） | 查询殖民地/世界、存档读档、事件触发等 | MCP 服务器自动调用（需进图） |
| **DebugAction** | 执行 RimWorld 原生调试菜单动作 | MCP 服务器自动调用（需进图） |
| **RimBridgeServer（GABP）**（附属模组） | 提供 `rimworld.*` / `rimbridge.*` 工具镜像（约 184 个） | MCP 服务器作为 GABP 客户端自动连接 |

---

## 2. 目录 / 端口 / 文件地图

### 2.1 根目录（模组根）

```
<MOD> = C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger
```

| 路径 | 说明 |
|---|---|
| `<MOD>/About/About.xml` | 模组元数据。`packageId` = `UESDdebuger.debug.unityexplorer`；依赖 `brrainz.harmony`；支持 RimWorld 1.5 / 1.6 |
| `<MOD>/MCP/index.js` | **MCP 服务器主入口**（bun 运行） |
| `<MOD>/MCP/config.json` | 服务器配置（游戏/日志/Steam 路径、appId、起停超时、rimBridge） |
| `<MOD>/MCP/ports.json` | **游戏侧写入的运行时端口**：`ueHttpPort` / `unityDebugPort` / `token` / `updatedAt` |
| `<MOD>/MCP/toolConfig.json` | per-tool 暴露开关（true=直接暴露给 AI，false=仍可经 `agg_call_tool` 调用） |
| `<MOD>/MCP/package.json` | node 依赖声明 |
| `<MOD>/MCP/node_modules/` | deps（工坊版首次启动自动从 `node_modules.zip` 解压） |
| `<MOD>/MCP/aggregator/` |  MCP **聚合器**（中间层，压缩工具列表） |
| `<MOD>/MCP/start-mcp.ps1` / `start-mcp-stdio.ps1` / `start-admin.ps1` / `resolve-runtime.ps1` | 启动脚本 |
| `<MOD>/runtime/bun/bun.exe` | **便携 bun**（推荐用作 MCP 的 `command`；工坊版首次启动自动从 `bun.zip` 解压） |
| `<MOD>/runtime/node/node.exe` | 便携 node（从 `node.zip` 解压） |
| `<MOD>/runtime/McpRimDebug/` | McpRimDebug 运行时（mono 补丁用，含 Mono.Cecil 等） |
| `<MOD>/McpRimDebug/` | McpRimDebug 源码工程（net10.0） |
| `<MOD>/Source/` | UELoader / UE / UniverseLib 源码 |
| `<MOD>/RimWorldWin64_Data/boot.config` | 已含 `wait-for-managed-debugger=1` 与 `player-connection-debug=1` |

### 2.2 端口约定（重要）

| 端口 | 用途 | 说明 |
|---|---|---|
| **stdio**（无端口） | MCP 主接入方式 | 客户端直接 spawn bun 进程，经 stdin/stdout 通信 |
| **3000**（`MCP_PORT`） | SSE 模式（调试用） | `http://127.0.0.1:3000/sse`，健康检查 `/health`；用 `start-mcp.ps1` 或 `bun run index.js` |
| **3001+**（动态） | 游戏内 UE HTTP 服务 | 从 3001 递增探测空闲；实际值由游戏侧写入 `ports.json` 的 `ueHttpPort` |
| **3100** | 聚合器 SSE（可选） | `http://127.0.0.1:3100/sse`，需先启动上游 3000 |
| **8765** | RIMAPI | 第三方模组固定端口（可用 `RIMAPI_BASE_URL` 覆盖） |
| —（动态） | Unity 调试端口 | 从日志 `Starting managed debugger on port XXXX` 解析，写入 `ports.json.unityDebugPort` |

### 2.3 游戏阶段模型

`get_game_status` 返回 `stage`，工具按前置条件分级：

| 阶段 | 含义 | 前置条件 |
|---|---|---|
| `GAME_STOPPED` | 进程未运行 | — |
| `GAME_STARTING` | 进程存在未就绪（<20s 且双通道不可达） | — |
| `MAIN_MENU` | 已到主菜单，未进地图 | — |
| `IN_GAME` | 已进地图/世界 | UE / RIMAPI 工具可用 |

---

## 3. 部署前置检查（AI 应先做）

在写配置 / 启动前，逐项核对（缺一项即按 §8 排障）：

1. **游戏是否已启动过一次**：工坊版 `bun.exe` / `node.exe` / `node_modules` 由模组在首次启动时自动解压。
   - 检查 `<MOD>/runtime/bun/bun.exe` 与 `<MOD>/MCP/node_modules/` 是否已存在；不存在从对应 `.zip` 直接解压，否则 MCP 无法 spawn。
2. **run.ts 运行时路径**：首选便携 bun `<MOD>/runtime/bun/bun.exe`（无需本机安装）；兜底用系统 `bun`。
   - ⚠️ `command` 必须用 **bun**，**不要**用 `npx` / `node`（node 下无 bun 运行时，且 npx 包装器污染 stdio）。
3. **config.json 路径核对**：`gamePath` / `logPath` / `steamPath` / `appId`。日志路径中的 `%USERNAME%` 会被替换为当前用户名。
4. **附属模组（可选但推荐）**：RIMAPI、RimBridgeServer、DebugAction 相关。RIMAPI / RimBridgeServer 无需排在 UESDdebuger 前面。
5. **McpRimDebug（SDB）是否需要**：需要代码级调试才做 §6 的 Unity 调试模式改造（部署耗时最久，非必需可跳过）。

---

## 4. 在 IDE 中接入 MCP（stdio，推荐）

把以下 JSON 按所用 IDE 的配置位置填入（`<MOD>` 先替换为 §2.1 的实际路径）
各 IDE 配置位置与格式差异，直接复制 `MCP/first-configs.md`（Trae / opencode / Claude Desktop / Codex / Gemini CLI / Kilo Code / VSCode 已全部给出）。**AI 执行原则**：先读 `first-configs.md` 再写入，注意各 IDE 的 schema 差异（如 opencode 用 `type:"local"` + `command:[...]`，Codex 用 TOML，Gemini CLI 写顶层 `mcpServers`）。

---

## 5. 启动顺序与验收（核心流程）

标准连接顺序（AI 可直接照此执行）：

1. **（可选）SSE 手动模式**：如需日志实时透出 / curl 探活，先起上游：
   ```powershell
   cd C:/SteamLibrary/steamapps/common/RimWorld/Mods/UESDdebuger/MCP
   .\start-mcp.ps1           # 默认 SSE 3000，实时日志 + Tee 落盘
   # 等价：bun run index.js  （stdio 模式加 $env:MCP_TRANSPORT='stdio'）
   ```
2. **启动游戏**：调用 MCP 工具 `start_game`（`useSteam` 默认 true，`waitForNotification=true` 会等待主菜单就绪）。也可直接双击 RimWorldWin64.exe。
3. **验收标准 A（服务器在）」**：`get_game_status` 返回非错误结构，`stage` 为 `GAME_STARTING` → `MAIN_MENU` → `IN_GAME`。
4. **快速进图（如需 UE / RIMAPI）**：调用 `start_quick_test`（触发官方 DevQuickTest 进入测试地图），返回 `state:"map_loaded"`。
5. **验收标准 B（工具可用）**：`read_rimworld_log` 能读到 Player.log；`get_unityexplorer_status` 返回 UE `uiReady`；`rimworld.list_colonists` 等在进图后可用。
6. **RimBridge（若启用）**：启动后 MCP 后台轮询 Player.log 解析 `[RimBridge] GABP server running standalone on port <port>` 与 `[RimBridge] Bridge token: <32位hex>`，自动连接并镜像工具；用 `get_game_status` 的 `gabp.state` / `toolsCount` 验收。

> 游戏侧每次 UE 工具调用都会在 Player.log 写 `[UEHttp] <METHOD> /<path> args={...} → ok/error(<code>), <耗时>ms`，可用作调用链追踪。

---
## 6. 聚合器（压缩工具列表）

默认工具约 200 个对客户端太宽——需要精简时启用聚合器。

```powershell
cd C:/SteamLibrary/steamapps/common/RimWorld/Mods/UESDdebuger/MCP
.\start-mcp.ps1                # 先起上游 3000
cd ..\MCP\aggregator
.\start-aggregator.ps1         # 再起聚合器 3100
```

- 压缩模式（默认 `compress=true`）：`listTools` 只暴露 2 个元工具 `agg_list_tools` / `agg_call_tool` + 7 个 coreTools。
- 客户端接 `http://127.0.0.1:3100/sse`；工具即使未暴露/被开关关闭，仍可经 `agg_call_tool {tool, args}` 调用。
- 鉴权/开关：改 `toolConfig.json` 后需重启 MCP（或重连会话）才生效。

---
## 7. 可选：McpRimDebug（Unity 调试模式改造）

> **强制门禁（Mandatory Gate）**：走到本节时，**必须调用宿主提供的询问工具**（在 DSH 环境中即 `ask_user_question`）提出确认，**禁止自行代为决定、跳过或猜测默认分支**；若该工具不可用或调用失败，**立即中止流程并向用户报告**，不得继续任何下载 / 覆盖 / 删除动作。
> 进入改造前，用下方 JSON **原样**调用 `ask_user_question`（字段结构不可改动，`id`/`header`/`question`/`options` 缺一不可）：
>```json
>{
>  "questions": [{
>    "id": "s7_sdb",
>    "header": "确认 Unity 调试模式改造",
>    "question": "McpRimDebug 需要 Unity 调试模式改造（整个部署中耗时最久、最易翻车；仅凭 AI 无法完整部署，需下载并覆盖游戏运行库并人工确认版本），是否执行？",
>    "options": [
>      {"label": "跳过（Recommended）", "description": "不做代码级调试，直接进图，用 UnityExplorer / RIMAPI 表层调试"},
>      {"label": "执行改造", "description": "严格匹配 Unity 2022.3.35f1，备份并覆盖 WinPixEventRuntime.dll / UnityPlayer.dll / RimWorldWin64.exe"}
>    ]
>  }]
>}
>```

需要代码级调试（断点 / 变量检查）时执行。**这是整个部署中耗时最久、最易翻车的一步**

1. 确认游戏 Unity 版本：右击 `RimWorldWin64.exe` → 属性 → 详细信息 → 产品版本。一般而言现版本1.6 当前为 **2022.3.35f1**。
2. 从 **Unity 国际版构建版本**下载严格匹配的同版本 Windows 版。
   - ⚠️ 必须 **严格 2022.3.35f1**，不能用 `-c1` / `-c2` 等（有修改，不兼容）。
3. 取前置文件（来自 Unity 安装目录）：
   - `...\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\WinPixEventRuntime.dll`
   - `...\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\UnityPlayer.dll`
   - `...\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\WindowsPlayer.exe`
4. 游戏根目录先备份：`UnityPlayer.dll`、`WinPixEventRuntime.dll`、`RimWorldWin64.exe` 各复制一份改后缀。
5. 用第 3 步的 `UnityPlayer.dll` / `WinPixEventRuntime.dll` 覆盖同名；`WindowsPlayer.exe` 改名 `RimWorldWin64.exe` 覆盖。
6. 确认 `RimWorldWin64_Data\boot.config` 末尾含两行 `wait-for-managed-debugger=1` 与 `player-connection-debug=1`（本项目已配好）。
7. 重启游戏，即进调试模式；从 Player.log 或 `ports.json.unityDebugPort` 得到 Unity 调试端口。

---



## 8. 排障决策树（AI 遇错优先自查）

| 症状 | 可能原因 | 处理 |
|---|---|---|
| `command` 用 node/npx | node 下无 bun，npx 污染 stdio | 改用便携 bun `runtime/bun/bun.exe` |
| `bun.exe` 不存在 | 游戏尚未首次启动解压 | 启动一次游戏 或 从 `runtime/bun/bun.zip` 手动解压；再 spawn |
| `node_modules` 缺失 | 工坊版 zip 未解压 | 同理，先启动游戏或从 `MCP/node_modules.zip` 解压 |
| `start_game` 成功但 MCP 工具报 `GAME_NOT_RUNNING` | 未进图 / 阶段不符 | 先 `start_game`（等待主菜单）→ `start_quick_test` 进图 |
| UE / RIMAPI 工具报 `MAP_NOT_LOADED` / `RIMAPI_NOT_READY` | 尚在主菜单 | 进图后再调用 |
| `RimBridge` 未连接（`gabp.toolsCount=0`） | RBS 未启动 / 游戏未过 play-data load | 确认 RimBridgeServer 模组已装并启用，进主菜单后用 `get_game_status` 看 `gabp` 字段 |
| UE HTTP 端口 3001 被占 | 动态端口已递增 | 读 `<MOD>/MCP/ports.json` 的 `ueHttpPort`，勿硬编码 3001 |
| 调用看不出错也无日志 | 游戏侧未启 UE HTTP | 看 Player.log 里 `[UEHttp]` 是否出现；确认 F7 能呼出 UE |
| McpRimDebug 不生效 | Unity 调试模式未真正开启 | 核对 §6（尤其 Unity 版本严格匹配） |
| 改 `toolConfig.json` 不生效 | 需重启 | 重启 MCP 或重连会话 |

**通用日志入口**：
- MCP 日志：`logs/mcp-<时间戳>.log`（内部）、`logs/mcp-window-<时间戳>.log`（窗口 Tee）。
- 游戏日志：`C:/Users/<USER>/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`（可用工具 `read_rimworld_log` / `tail_rimworld_log`）。

---

## 9. 一句话提醒

- `command` 必须 `bun`（便携 `runtime/bun/bun.exe` 优先）；`MCP_TRANSPORT=stdio` 为默认接入。
- 端口别写死：UE HTTP 读 `ports.json`，SSE 用 3000，聚合器 3100。
- 工具「暴露 vs 可用」是两个概念：关闭开关=不出现在列表，仍可经 `agg_call_tool` 调用。
- 本模组为个人调试用途发布，非官方，与 Ludeon Studios 无关联。

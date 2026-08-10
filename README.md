# UESDdebuger - UnityExplorer + SDB Debug

RimWorld 调试工具链模组，为游戏内联调、AI 辅助开发（MCP）与代码级调试提供一体化能力.

- **MCP 调试服务器**：让支持 MCP 的客户端（Trae / Claude / opencode 等）直接控制游戏——启动/关闭、读日志、进测试地图、执行 C#、管理 Hook、查询殖民地/世界数据、执行 DebugAction 等 57 个工具；
- **游戏内 UnityExplorer**（UE 4.9.0，官方二进制零修改）：按 `F7` 随时呼出/隐藏，支持对象检视、C# 控制台、方法 Hook、场景/游戏对象浏览等；
- **SDB 代码级调试器（McpRimDebug）**：通过 Mono Soft Debugger 协议直连真实游戏进程，提供断点、调用栈、局部变量、单步、求值、对象检查等 20 个调试工具。

> 支持 RimWorld 1.6   
>  依赖 [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)
                        [RIMAPI](https://steamcommunity.com/sharedfiles/filedetails/?id=3593423732)。
---

## 快速开始

1. 将本模组放入 `RimWorld/Mods/` 并启用（需同时启用 Harmony）；
2. 首次启动游戏后，模组会自动完成大部分配置，比如：
   - 解压便携运行时（`runtime/bun`、`runtime/node`、`MCP/node_modules`，工坊版以 zip 形式携带）；
   - 探测游戏路径/日志路径/Steam 路径并写入 `MCP/config.json`；
3.根据进入游戏后显示的配置流程进行配置（不小心关闭了？可以在设置中重新找到）

## 功能组成

### 1. 游戏内 UnityExplorer

- 加载器在进入第一个场景后自动初始化官方 UnityExplorer 4.9.0 ，失败不影响游戏启动；
- UE 配置目录重定向到 `UE_Data/`，不在 `Assemblies/` 内写运行期文件；
- UE 日志自动桥接到 RimWorld 日志（`[UnityExplorer]` 前缀）；

### 2. 游戏内 HTTP 服务（UE 桥接）

- 游戏内启动本机 `127.0.0.1` HTTP 服务，动态端口（从 3001 起递增探测空闲端口，避免与其他模组冲突）；
- 实际端口写入 `MCP/ports.json`，MCP 服务器与调试器动态读取；
- 主菜单即可用 `/trigger-quicktest`（快速进测试地图）与 `/unityexplorer/status`；
- 每个工具调用都会在 `Player.log` 记录 `[UEHttp] <METHOD> /<path> ... → ok/error, 耗时`。

### 3. MCP 调试服务器（`MCP/`）

独立于游戏运行的 MCP 服务器（bun/node，默认 stdio 本地接入，可选 SSE），工具按游戏阶段分级（`GAME_STOPPED` / `GAME_STARTING` / `MAIN_MENU` / `IN_GAME`），前置条件不满足时返回统一错误与引导。

> **依赖 RIMAPI**：游戏数据查询与操作注入类工具（共 29 个，下表中标注 Ⓡ）依赖第三方模组 **RIMAPI**（Steam 创意工坊/独立发布，端口 `8765`，可用环境变量 `RIMAPI_BASE_URL` 覆盖）。未安装/未启用 RIMAPI 时，这些工具返回 `RIMAPI_NOT_READY` 错误并给出引导；其余工具（基础、UE 集成、DebugAction、Map 结构）不依赖 RIMAPI。

#### 完整工具清单（57 个）

**基础与进程控制（7）**

| 工具 | 说明 |
|---|---|
| `start_game` / `stop_game` | 通过 Steam 或直接启动 / 正常或强制关闭游戏 |
| `get_game_status` | 运行状态与阶段（多源探测：进程 / RIMAPI / UE） |
| `read_log` / `tail_log` | 读取游戏日志末尾 N 行 / 实时最新日志 |
| `get_config` | 获取当前配置 |
| `start_quick_test` | 主菜单即可用：快速进入官方 DevQuickTest 测试地图 |

**聚合与元工具（3）**

| 工具 | 说明 |
|---|---|
| `agg_list_tools` | 列出所有上游工具目录与启用状态 |
| `agg_call_tool` | 按名调用任意工具（含未直接暴露的禁用工具） |
| `mcp_help` | 工具概览 / 单个工具详情 |

**UnityExplorer 集成（11，需进入地图，经游戏内 UE HTTP 桥接）**

| 工具 | 说明 |
|---|---|
| `get_unityexplorer_status` | UE 就绪状态 |
| `execute_csharp_code` | 游戏内执行 C# 代码（UE Console） |
| `reset_csharp_console` / `add_using_directive` | 控制台维护 |
| `inspect_type` | 类型检查（Inspector） |
| `create_hook` / `toggle_hook` / `delete_hook` / `list_hooks` | 方法 Hook 管理 |
| `get_unityexplorer_logs` / `clear_unityexplorer_logs` | UE 日志查看 / 清空 |

**RIMAPI 游戏数据（14， 依赖 RIMAPI，多数需进入地图,用于AI方便调用）**

| 工具 | 说明 |
|---|---|
| `get_game_state` | 游戏当前状态 |
| `get_colonists` | 殖民者列表 |
| `get_colonists_detailed` / `get_colonist_detailed` | 殖民者详细信息（需求/工作/医疗等） |
| `get_maps` | 地图列表 |
| `get_map_things` / `get_map_plants` / `get_map_animals` / `get_map_weather` | 地图物件 / 植物 / 动物 / 天气 |
| `get_research` | 研究进度 |
| `get_factions` | 派系列表 |
| `get_world_caravans` | 世界商队列表 |
| `get_version` / `get_mods_info` | 游戏/模组版本与已加载模组信息 |

**RIMAPI 操作注入（10， 依赖 RIMAPI,需进入地图,用于AI方便调用）**

| 工具 | 说明 |
|---|---|
| `post_game_load` / `post_game_save` | 加载 / 保存存档 |
| `post_incident_execute` | 执行随机事件 |
| `post_order_designate` | 下达区域指令（采矿、收割、狩猎、拆除等） |
| `post_game_speed` | 设置游戏速度（0=暂停 ~ 4=开发者模式） |
| `post_select` / `post_deselect` | 选择 / 取消选择对象（pawn/building/item） |
| `post_ui_message` / `post_ui_dialog` | 显示消息通知 / 对话框 |
| `post_dev_console` | 操作开发者控制台 |

**RIMAPI 镜头与视频流（5，Ⓡ 依赖 RIMAPI，需进入地图）**

| 工具 | 说明 |
|---|---|
| `post_camera_change_zoom` / `post_camera_change_position` | 调整相机缩放 / 移动相机 |
| `post_stream_start` / `post_stream_stop` / `post_stream_setup` | 相机视频流开始 / 停止 / 参数配置 |

**DebugAction（5，需进入地图，经 UE 桥接，不依赖 RIMAPI）**

| 工具 | 说明 |
|---|---|
| `list_debug_actions` | 列出原版 DebugAction 菜单（支持子菜单/隐藏项） |
| `get_debug_action_detail` | 获取动作完整元数据 |
| `execute_debug_action` | 执行 DebugAction（支持 Action / ToolMap / ToolWorld / ToolMapForPawns，自动执行） |
| `get_debug_action_categories` | 分类列表及数量 |
| `search_debug_actions` | 按关键词搜索 |

**Map 结构（2，需进入地图，经 UE 桥接，不依赖 RIMAPI）**

| 工具 | 说明 |
|---|---|
| `get_map_structure` | 按路径浏览 Map 结构（Grid/Manager/Component 等，含字段/属性/方法） |
| `search_map_structure` | 搜索 Map 结构路径 |

- **工具开关**：`MCP/toolConfig.json` 按工具启用/禁用，默认压缩预设仅暴露 10 个高频工具；
- **MCP 聚合器**（`MCP/aggregator/`）：中间层聚合器，默认压缩模式下只暴露 `agg_list_tools` / `agg_call_tool` + 核心白名单，避免向客户端暴露全部工具。

### 4. SDB 代码级调试器（`McpRimDebug/`）

.NET 10 编写的 MCP 服务器，通过 **Mono Soft Debugger（SDB）协议**直连真实游戏进程，无 Harmony 等注入手段：

#### 完整工具清单（20 个）

**会话管理（6）**

| 工具 | 说明 |
|---|---|
| `launch` | 以调试模式启动游戏（自动发现随机端口并 attach、自动关闭「Debug (Player)」告知窗） |
| `attach(host, port)` / `detach` | 连接 / 安全断开游戏调试端口 |
| `resume` / `suspend` | 恢复 / 挂起整个 VM |
| `status` | 会话状态：进程 / 端口监听 / 协议版本 + `debugPortFromLog` |

**断点与事件（6）**

| 工具 | 说明 |
|---|---|
| `break_add(method, line?)` | 方法入口断点或源码行断点 |
| `break_list` / `break_remove(id)` / `break_clear` | 断点注册表管理 |
| `break_exception` | 异常事件请求（可筛选 caught / uncaught） |
| `wait(eventType?, timeoutMs?)` | 阻塞等待匹配事件（断点/异常/方法进入等） |
| `step(threadId, direction?)` | 单步 into / over / out（无调试符号时自动退化为指令级） |

**观测（4）**

| 工具 | 说明 |
|---|---|
| `threads` | 全部线程（id / 名称 / 状态） |
| `callstack(threadId, frameLimit?)` | 调用栈：方法全名 + IL 偏移 + 源码 file:line |
| `locals(threadId, frameIndex?)` | 实参 / 局部量 / `this`（子对象返回句柄） |
| `inspect(handle)` | 按句柄展开对象字段 |

**求值与搜索（4）**

| 工具 | 说明 |
|---|---|
| `eval(expression, threadId?, frameIndex?)` | 游戏内表达式求值（`this` / 局部量 / 静态类型 / 成员链） |
| `find_types(query, limit?)` | 按子串搜索类型 |
| `find_methods(query, limit?)` | 按子串搜索方法 |

> 动态端口：Unity 调试端口每次运行随机，从 `Player.log` 自动解析（`status.debugPortFromLog`），不做 raw TCP 端口探测（避免触发 `DWP handshake failed` 导致游戏退出）。游戏本体无调试符号时，断点退化为方法入口断点、单步退化为指令级；带 PDB 的模组 DLL 无此限制。



## 设置

游戏内 **选项 → Mod 设置 → UESDdebuger**：

- 配置 `gamePath` / `logPath` / `steamPath` / `appId` / 启动超时；
- 「重新探测并重置配置」：重新探测路径并写回 `MCP/config.json`；
- 工具开关列表：逐个启用/禁用 MCP 工具（写回 `MCP/toolConfig.json`）。

## 许可与声明

- 本项目基于 MIT 等开源许可，第三方组件许可见 `THIRD_PARTY_NOTICES.md` 与 `LICENSE`；
- 模组引用的部分素材/商标归 Ludeon Studios Inc. 所有；本模组非官方内容，未获 Ludeon 认可；
- 用于调试目的，请勿在正式存档/多人环境中滥用。

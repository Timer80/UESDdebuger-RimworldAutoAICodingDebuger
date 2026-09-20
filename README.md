# UESDdebuger - UnityExplorer + SDB Debug

RimWorld 调试工具链模组，为游戏内联调、AI 辅助开发（MCP）与代码级调试提供一体化能力，由三部分组成：

- **游戏内 UnityExplorer**（UE 4.9.0，官方二进制零修改）：按 `F7` 随时呼出/隐藏，支持对象检视、C# 控制台、方法 Hook、场景/游戏对象浏览等；
- **MCP 调试服务器**：让支持 MCP 的客户端（Trae / Claude / opencode 等）直接控制游戏——启动/关闭、读日志、进测试地图、执行 C#、管理 Hook、查询殖民地/世界数据、执行 DebugAction、打地图坐标光标等；连接 RimBridgeServer（GABP）后**可调用工具总数 191 个**（本地 70 + 121 个 `rimworld.*` / `rimbridge.*` 镜像）；
- **SDB 代码级调试器（McpRimDebug）**：通过 Mono Soft Debugger 协议直连真实游戏进程，提供断点、调用栈、局部变量、单步、求值、对象检查等 20 个调试工具。

> **支持 RimWorld 1.5 / 1.6**
>
> **依赖**：
> - [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)（**必须**）：模组修补框架，UnityExplorer 与游戏侧桥接均依赖；
> - [RimBridgeServer](https://steamcommunity.com/sharedfiles/filedetails/?id=3727949765)（**推荐**）：提供 GABP 桥接，连接后**可调用**的 MCP 工具总数从 **70**（本地）增至 **191**（含 121 个 `rimworld.*` / `rimbridge.*` 镜像工具；菜单可见 188，差额是 3 个隐藏工具）；未安装时相关镜像工具不可用（菜单里仍会列出名字并标注"提供方未就绪"），其余功能不受影响；
> - [RIMAPI](https://steamcommunity.com/sharedfiles/filedetails/?id=3593423732)（**可选**）：游戏数据查询与操作注入类工具（29 个）依赖，未安装时仅这些工具返回 `RIMAPI_NOT_READY`。
---

## 快速开始
1. 直接下载仓库全部文件；
2. 将本仓库（模组）放入 `RimWorld/Mods/` 并启用（需同时启用 Harmony）；
3. 首次启动游戏后，模组会自动完成大部分配置，比如：
   - 解压便携运行时（`runtime/bun`、`runtime/node`、`MCP/node_modules`，工坊版以 zip 形式携带）；
   - 探测游戏路径/日志路径/Steam 路径并写入 `MCP/config.json`；
4. 根据进入游戏后显示的配置流程进行配置（不小心关闭了？可以在设置中重新找到）

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


#### 优先 stdio 启动（P1-MCP-4，单实例）

**推荐以 stdio 直接接入**：本服务用 `MCP_TRANSPORT=stdio` 环境变量或 `--stdio` 启动参数切换，stdout 走 MCP 协议、日志走 stderr，不监听任何 HTTP 端口，由客户端按需 spawn，**天然单实例**。

- 方式一（独立启动脚本）：`.\MCP\start-mcp-stdio.ps1`（等价于 `node MCP/index.js --stdio`）；
- 方式二（客户端命令行）：`node MCP/index.js --stdio`；
- 方式三（环境变量）：设置 `MCP_TRANSPORT=stdio` 后再运行 `node MCP/index.js`。

**为什么单实例（避免 HTTP 多实例互踩/争启）**：本服务的菜单/地图通知、鉴权 token、`selfStartedPid` 等全局状态均为模块级共享；HTTP（SSE）模式下若拉起多个实例，它们共享同一份全局状态会彼此互踩，且多个实例可能争相启动/停止游戏（双实例端口冲突、误杀风险，见 `start_game` / `stop_game` 防止重复启动与定向停止的相关实现）。stdio 由客户端每次连接按需创建独立进程，正好规避这一整类问题。

**SSE（HTTP）仍可用但限单客户端**：仅供仍走 HTTP 的 IDE/客户端（如 Trae）使用，同一时间应只有一个客户端连接（地址 `http://127.0.0.1:3000/sse`，`/health` 健康检查）。


#### 完整工具清单

> 口径：连接 RimBridgeServer（GABP）后 `agg_list_tools` 实测工具**总数 185**（system 7 / unityexplorer 11 / mono 20 / meta 3 / bridge 16 / game_control 128）。默认压缩仅直接暴露 23 个，其余经 `agg_call_tool` 调用。下方「基础与进程控制」等小节用工具新名（原 `read_log`→`read_rimworld_log`、`tail_log`→`tail_rimworld_log`、`get_config`→`get_game_info`）。

**基础与进程控制（7）**

| 工具 | 说明 |
|---|---|
| `start_game` / `stop_game` | 通过 Steam 或直接启动 / 正常或强制关闭游戏 |
| `get_game_status` | 运行状态与阶段（多源探测：进程 / RIMAPI / UE） |
| `read_rimworld_log` / `tail_rimworld_log` | 读取游戏日志末尾 N 行 / 实时最新日志 |
| `get_game_info` | 获取当前配置与综合信息（三源合并：config + GABP + RIMAPI） |
| `start_quick_test` | 主菜单即可用：快速进入官方 DevQuickTest 测试地图。**只依赖本模组的游戏内 HTTP 服务（`/trigger-quicktest`），不依赖 GABP/RimBridgeServer、RIMAPI 或 UE 是否进图**；失败时按 ports.json 自报状态区分「模组未加载 / 服务未启动 / token 过期」 |
| `task_status` / `task_cancel` / `task_configure` | **长任务三件套**（配合 `rimworld.play_for{nonBlocking:true}` 做 10 分钟以上的后台性能采样）：秒级返回 `taskId` 不阻塞调用方；`task_status` 看进度/最近采样点(tps)/最近 DPA 快照/落盘路径，无参列出全部（含 MCP 进程重启打断的孤儿任务）；`task_cancel` 最迟一个采样周期内停止并按 `pauseOnFinish` 处理暂停；`task_configure` 中途改采样节奏（采样压到 1 s 的猝发测量时快照间隔强制联动压到 10 s，避免超出 DPA 2000 格环形缓冲而丢数据）。采样点全量落盘到 `docs/dpa/samples/<runId>.jsonl` |

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

> 部分能力已迁移：`get_colonists`→`rimworld.list_colonists`、`get_mods_info`→`rimworld.list_mods`、`get_game_state`→原生合并 `get_game_info`（见「RimBridgeServer 镜像工具」）。下表保留历史原生名作对照。

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

> 存档/读档/速度/选择等能力在 tool-cleanup 后已被 **GABP 镜像承接**（见「RimBridgeServer 镜像工具」）：`post_game_load`→`rimworld.load_game`、`post_game_save`→`rimworld.save_game`、`post_game_speed`→`rimworld.set_time_speed`、`post_select`/`post_deselect`→`rimworld.select_pawn`/`rimworld.clear_selection`。下表保留历史原生名作对照，直接调用请用镜像名。

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

> 相机缩放/移动已被镜像承接：`post_camera_change_zoom`→`rimworld.set_camera_zoom`、`post_camera_change_position`→`rimworld.jump_camera_to_cell`。

| 工具 | 说明 |
|---|---|
| `post_camera_change_zoom` / `post_camera_change_position` | 调整相机缩放 / 移动相机 |
| `post_stream_start` / `post_stream_stop` / `post_stream_setup` | 相机视频流开始 / 停止 / 参数配置；`post_stream_start` 支持 `output_frames=true` 随流抓帧输出 JPEG 序列到 `out_dir`（配 `post_stream_stop` 停止） |

**DebugAction（5，需进入地图，经 UE 桥接，不依赖 RIMAPI）**

> 调试菜单遍历与执行已被镜像承接：`list_debug_actions`→`rimworld.list_debug_action_roots`/`rimworld.list_debug_action_children`、`get_debug_action_detail`→`rimworld.get_debug_action`、`execute_debug_action`→`rimworld.execute_debug_action`（仅 `path`/`pawnId`）。

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

**地图坐标光标（1，需进入地图，经 UE 桥接，不依赖 RIMAPI/GABP）**

> 把「AI 报的坐标数字」变成地图上一眼可见的准星——炼狱魔王炮风格的指示器（外环 + 四向刻线 + 贯穿十字 + 中心点）配地面坐标文字，解决「AI 报坐标、玩家对不上号」。**玩家鼠标左键点击光标即消除**（被窗口遮挡时那一次点击按 UI 点击处理，不会误消）。

| 工具 | 说明 |
|---|---|
| `post_map_marker` | `action`：`set`（默认，打光标）/ `clear`（不给 `id` 则清全部）/ `recolor`（改色）/ `list`（列出当前光标）。`x`/`z` 为目标格坐标；`color` 支持 13 种英文名（red/orange/gold/yellow/lime/green/teal/cyan/blue/purple/magenta/pink/white/gray）、`#RRGGBB`（也接受 `RRGGBB` / `#RGB`）或 `"r,g,b"`，默认 `#FF4A1F`（炼狱魔王同款橙红）；`label` 为光标上方标题；`size` 边长 1–40 格（默认 8）；`ttl_seconds` 定时消失（0/省略=常驻）；`id` 可并存多个（上限 8），`set` 不给 `id` 时默认先清掉已有光标 |

**RimBridgeServer（GABP）镜像工具（121 个，依赖 RimBridgeServer）**

> 依赖 **RimBridgeServer**（[Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3727949765)）。MCP 服务器内置 GABP 客户端，自动从游戏日志发现并连接游戏内 RimBridgeServer 的 GABP 服务器（默认端口 5174），把 RBS 工具以 `rimworld.*` / `rimbridge.*` 前缀镜像进工具表（命名 `/` → `.`）。游戏需进入主菜单后才启动 GABP 服务器，并在日志打印端口与 token。连接状态见 `get_game_status` 的 `gabp` 字段。**13 个重叠能力的原生工具已删除**，由恢复暴露的 GABP 镜像承接为唯一入口（如 `get_colonists`→`rimworld.list_colonists`、`post_game_load`→`rimworld.load_game`、`execute_debug_action`→`rimworld.execute_debug_action` 等）。

默认直接暴露的 13 个镜像：

| 工具 | 说明 |
|---|---|
| `rimworld.list_colonists` | 殖民者列表（含 pawnId） |
| `rimworld.list_mods` | Mod 列表 |
| `rimworld.save_game` / `rimworld.load_game` | 存档 / 读档（参数 `saveName`） |
| `rimworld.set_time_speed` | 设置时间流速 |
| `rimworld.clear_selection` / `rimworld.select_pawn` | 清除选区 / 选择殖民者（需完整 Thing id，如 `Thing_Human737`） |
| `rimworld.list_debug_action_roots` / `rimworld.list_debug_action_children` | 调试菜单根节点 / 子节点遍历 |
| `rimworld.get_debug_action` / `rimworld.execute_debug_action` | 调试动作详情 / 执行（仅 `path`/`pawnId`） |
| `rimworld.jump_camera_to_cell` / `rimworld.set_camera_zoom` | 相机跳转格子 / 设置缩放 |

其余镜像按功能分组（经 `agg_call_tool` 调用，约 108 个）：

- **bridge（16 个 `rimbridge.*`）**：桥接基础——`list_capabilities` / `get_capability` / `list_operations` / `get_operation` / `list_operation_events` / `list_logs` / `get_script_reference` / `get_lua_reference` / `run_script` / `run_lua` / `run_lua_file` / `compile_lua` / `compile_lua_file` / `wait_for_game_loaded` / `wait_for_long_event_idle` / `wait_for_operation`。
- **game_control 镜像（约 105 个 `rimworld.*`）**：游戏状态 / 时间 / 存档（`pause_game` / `step_game_ticks` / `load_game_ready` / `go_to_main_menu`）、相机（`move_camera` / `zoom_camera` / `frame_pawns`）、交互（`click_cell` / `drag_cell` / `right_click_cell`）、UI（`get_ui_state` / `get_ui_layout` / `open_main_tab` / `execute_gizmo`）、通知（`list_letters` / `list_alerts` / `take_screenshot`）、建筑/区域（`list_architect_*` / `create_allowed_area` / `delete_area` / `clear_area` / `list_areas` / `delete_zone`）、Mod 设置（`list_mod_settings_surfaces` / `get_mod_settings` / `update_mod_settings`）、DPA（`dpa_status` / `dpa_snapshot` / `dpa_cleanup`）、调试菜单与生成（`spawn_thing` / `set_draft` / `despawn_thing`）等。
- 另有 18 个 RIMAPI 原生 + 4 个 debugaction 原生 `game_control` 工具（见上表）保留原生后端。

> 完整逐工具清单与实测结果见 `MCP/index.js` 内工具注册表与 `MCP/announcements.md` 更新公告。

- **工具开关**：`MCP/toolConfig.json` 按工具启用/禁用，默认压缩预设仅暴露 23 个高频工具；
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

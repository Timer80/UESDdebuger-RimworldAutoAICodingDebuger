# UESDdebuger 更新公告

> 本文件为更新公告源（主菜单公告弹窗读取）。格式：`### <版本号>` 分段，文件内**从新到旧**排列。
> 显示规则：仅展示 `原版本 < 段版本 <= 当前模组版本` 的段落（从新到旧）。
> 新增版本段时在文件**最上方**插入，并同步递增 `About/About.xml` 的 `<modVersion>`。

### 0.9.0.1
- 新增 `post_map_marker`（地图坐标光标）：让AI 从报坐标数字变成展示地图上一眼可见的准星
- 修复dpa工具：`task_status` / `task_cancel` / `task_configure`——配合 `rimworld.play_for{nonBlocking:true}` 做 10 分钟以上的后台性能采样，秒级返回 `taskId`，可中途改采样节奏（采样压到 1 s 时快照自动联动压到 10 s）、可随时取消，采样点全部输出到 `docs/dpa/samples/<runId>.jsonl`、生命周期写入 `docs/dpa/tasks/<runId>.journal.jsonl`。
### 0.9.0.0
- 新增 RimBridgeServer（GABP）集成：MCP 服务器内置 GABP 客户端，自动从游戏日志发现并在游戏内 RimBridgeServer 模组（默认端口 5174）的 GABP 服务器上连接、握手，把 121 个`rimworld.*` / `rimbridge.*` 工具镜像进 MCP 工具表（连接后工具总数 191；菜单可见 188，另有 3 个隐藏工具）；13 个重叠能力的原生工具删除，对应 GABP 镜像成为唯一入口。
- 临时修复 RimBridge 的缺陷：`RimBridgeAreaMarshallingPatch` 把 Area 删除/清空派发到主线程执行，修复 RimBridgeServer 的 `delete_area`/`clear_area` 在 TCP 读线程改主线程状态造成的连接僵死；游戏侧自研 `UEAreaActions` 替代其僵死工具；`RimBridgeGABPStatusPatch` 监控 GABP 收发状态（`GET /rimbridge/status`）；GABP 假死自动重连。
- 安全加固：游戏侧 UE 桥接与 MCP→UE 通信增加 Bearer token 鉴权（写操作带 `Authorization` 头，401 自动刷新），非回环绑定无 token 拒绝启动；CORS 收紧、notify 来源校验、请求体上限 1MB + JSON 深度限制（防滥用）。
- MCP 服务器健壮性：游戏启动改真实进程检测 + 按 PID 定向停止（避免误杀）；UE/RIMAPI 请求超时、慢操作超时加倍、进程退出清理、`stdio` 单实例优先；日志保留上限至 5 个、单文件 100MB 滚动（防磁盘写爆）；`tasklist` CSV 健壮解析。
- 流/工具增强：`post_stream_start` 支持 `output_frames=true` 随流抓帧输出 JPEG 序列；手写 Area 删除/清空替代僵死工具；`create_hook` 加入黑名单禁止对日志类方法建 Hook（防递归刷日志崩溃）。
- 调试器（McpRimDebug）稳定性：修复会话状态机竞态（Attach/WithTimeout/eval/EventLoop）、全局命令锁忙警告、事件队列上限、step 超时校准；对象句柄超阈值分批回收防泄漏；超限回复全量落盘 + 截断报告；`find_*` 元数据/端口探测缓存加速。
- 性能与细节：主线程调度返回真实动作状态、`ExecuteCode` 返回真实耗时、调度器单例并发安全、时间戳统一 UTC、公告钩子 `patchInstalled` 修复；公告与配置源文件归位至 `MCP/` 恢复弹窗可读。

### 0.8.0.0
- RimWorld 调试模组（UnityExplorer 4.9.0 + SDB 代码级调试器 McpRimDebug）。
- 首次启动自动部署：探测并生成 `MCP/config.json`（游戏路径 / 日志路径 / Steam 路径）。
- 公告系统：配置公告（首次启动，单选 IDE 查看 MCP 配置代码，可复制）+ 更新公告（版本更新推送，从近到远）。
- 仅版本更新时只显示「现版本到原版本之间」的更新公告，不重复弹配置公告。
- 公告与配置代码改由外部文件维护：`MCP/announcements.md` 与 `MCP/first-configs.md`。
- 主菜单公告：按 F7 切换 UnityExplorer，调试工具链接入与部署详见《部署文档.md》《桥接操作文档.md》。

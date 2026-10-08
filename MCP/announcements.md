# UESDdebuger 更新公告

> 本文件为更新公告源（主菜单公告弹窗读取）。格式：`### <版本号>` 分段，文件内**从新到旧**排列。
> 显示规则：仅展示 `原版本 < 段版本 <= 当前模组版本` 的段落（从新到旧）。
> 新增版本段时在文件**最上方**插入，并同步递增 `About/About.xml` 的 `<modVersion>`。

### 0.9.0.0
- **安全加固**：游戏侧 UE 桥接与 MCP→UE 通信增加 Bearer token 鉴权；CORS 收紧、notify 通道校验、拒绝非回环来源；请求体上限 1MB + JSON 深度限制（防滥用）。
- **Hook 防护**：`create_hook` 加入黑名单，禁止对日志类方法（`Verse.Log`/`Debug`）建 Hook，避免递归刷日志导致崩溃；`UEHookStore.Remove` 改强类型 Unpatch。
- **调试器（McpRimDebug）稳定性**：修复会话状态机竞态（Attach/WithTimeout/eval/EventLoop）、命令锁忙警告、事件队列上限、step 超时校准；对象句柄超阈值分批回收防泄漏；超限回复全量落盘 + 截断报告；`find_*` 元数据/端口探测缓存加速。
- **MCP 服务器健壮性**：游戏启动改真实进程检测 + 按 PID 定向停止，避免误杀；UE/RIMAPI 请求超时、慢操作超时加倍、进程退出清理；`stdio` 单实例优先 + 启动脚本；`tasklist` CSV 健壮解析；日志保留上限 5 个 + 单文件 100MB 滚动（防磁盘写爆）。
- **流/工具增强**：`post_stream_start` 支持随流抓帧输出 JPEG 序列；手写 Area 删除/清空替代僵死工具；GABP 收发监控补丁与假死自动重连。
- **性能与细节**：主线程调度返回真实动作状态、`ExecuteCode` 返回真实耗时、调度器单例并发安全、时间戳统一 UTC、公告钩子 `patchInstalled` 修复。

### 0.8.0.0
- RimWorld 调试模组（UnityExplorer 4.9.0 + SDB 代码级调试器 McpRimDebug）。
- 首次启动自动部署：探测并生成 `MCP/config.json`（游戏路径 / 日志路径 / Steam 路径）。
- 公告系统：配置公告（首次启动，单选 IDE 查看 MCP 配置代码，可复制）+ 更新公告（版本更新推送，从近到远）。
- 仅版本更新时只显示「现版本到原版本之间」的更新公告，不重复弹配置公告。
- 公告与配置代码改由外部文件维护：`MCP/announcements.md` 与 `MCP/first-configs.md`。
- 主菜单公告：按 F7 切换 UnityExplorer，调试工具链接入与部署详见《部署文档.md》《桥接操作文档.md》。

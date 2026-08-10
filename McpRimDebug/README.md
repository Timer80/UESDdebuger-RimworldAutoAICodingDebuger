# McpRimDebug — RimWorld Mono Soft Debugger MCP 服务器

通过 **Mono Soft Debugger（SDB）协议**直接连接 RimWorld 真实游戏进程，向 AI 客户端（Trae / opencode 等支持 MCP 的 IDE）暴露代码级调试工具：断点、调用栈、局部变量、单步、求值、对象检查。

- 实现：.NET 10 控制台程序（stdio MCP 传输，官方 `ModelContextProtocol` SDK 2.1.0）
- 协议：SDB（mono soft debugger，与游戏协商为 **2.58**，VM：mono 6.13.0 Visual Studio built mono）
- 依赖：仅 vendor 的 SDB 客户端源码 + `Mono.Cecil`（未使用 Harmony 等注入手段）

---

## 1. 快速开始

### 1.1 构建

```powershell
cd McpRimDebug
dotnet build
```

产物：`McpRimDebug\bin\Debug\net10.0\McpRimDebug.exe`

### 1.2 自检

```powershell
.\bin\Debug\net10.0\McpRimDebug.exe --selftest
```

输出 `selftest 通过: 20 个工具注册齐全...` 即正常。selftest 会额外打印 `debugPortFromLog`（若游戏在跑），可顺带确认端口发现生效。

### 1.3 启动游戏（调试模式）

```powershell
.\启动器.ps1
```

启动器会：
1. 设置 `MCP_RIMDBG_GAME_PATH` / `MCP_RIMDBG_LOG_PATH` 等环境变量；
2. 检测到已有 RimWorld 进程时**中止**（Unity 调试端口每次运行随机，双实例会端口冲突）；
3. 启动 `RimWorldWin64.exe`。游戏窗口会弹出「Debug (Player)」告知框并显示**随机端口**——这是 Unity 的 `wait-for-managed-debugger=1` 机制；该窗口**不会**随 attach 自动消失，需点「确定」或由 `launch()` 工具主动关闭。

> 前提：游戏 `boot.config` 已开启调试（`wait-for-managed-debugger=1`，本项目目标环境已开启）。

> 提示：若用 MCP 工具的 `launch()`（默认 `autoAttach=true`）启动，工具会自动轮询 Player.log 拿到本次运行的随机端口并 attach，随后**主动关闭** Unity 的「Debug (Player)」告知窗（实测该窗口不会随 attach 自动消失，工具用 WM_CLOSE 关闭它）、一闪而过；`.\启动器.ps1` 则是纯手动流程（需自己点确定）。

---

## 2. 端口发现机制（重点）

Unity 内嵌调试代理的端口**每次运行随机**，启动时写入 Player.log：

```
Starting managed debugger on port 56651
```

因此**不要**依赖固定端口。标准流程：

```
launch()                      # 启动游戏；默认 autoAttach=true：自动取本次随机端口并 attach，告知窗自动关闭
resume()                      # resume 后游戏才开始运行
# 需要手动控制时用 launch(autoAttach=false)，再自行：
#   status() → 读 data.debugPortFromLog → attach(host, 端口) → resume()
```

- `status` 的 `debugPortFromLog` 字段即游戏自报端口；`debugPortOpen` 表示该端口是否已监听。
- 端口探测使用**本地监听表检查**（不建立真实连接），因为对 mono 调试代理做 raw TCP 探测会触发 `DWP handshake failed` 导致游戏进程退出。
- `launch` 不再注入 `MONO_SDB_ENV_OPTIONS`（实测被游戏自身 mono 选项覆盖，不可靠），只负责拉起进程。
- 局域网场景：`attach("<游戏机IP>", 端口)`，并在游戏机防火墙放行对应端口（TCP）。

---

## 3. MCP 接入示例

### 3.1 opencode（`opencode.json`）

```json
{
  "mcp": {
    "rimworld-debug": {
      "type": "local",
      "command": ["C:\\SteamLibrary\\steamapps\\common\\RimWorld\\Mods\\UESDdebuger\\McpRimDebug\\bin\\Debug\\net10.0\\McpRimDebug.exe"],
      "enabled": true
    }
  }
}
```

### 3.2 Trae（`.trae/mcp.json`）

```json
{
  "mcpServers": {
    "rimworld-debug": {
      "command": "C:\\SteamLibrary\\steamapps\\common\\RimWorld\\Mods\\UESDdebuger\\McpRimDebug\\bin\\Debug\\net10.0\\McpRimDebug.exe",
      "args": [],
      "type": "stdio"
    }
  }
}
```

### 3.3 环境变量

| 变量 | 默认值 | 说明 |
|---|---|---|
| `MCP_RIMDBG_GAME_PATH` | `C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe` | `launch` 启动的游戏路径 |
| `MCP_RIMDBG_LOG_PATH` | `%LOCALAPPDATA%\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log` | 端口发现读取的 Player.log 路径 |
| `MCP_RIMDBG_HOST` | `127.0.0.1` | attach/status 默认主机 |
| `MCP_RIMDBG_PORT` | `56574` | 缺省端口（实际以 debugPortFromLog 为准） |

---

## 4. 工具清单（20 个）

### 会话管理
| 工具 | 说明 |
|---|---|
| `status` | 会话状态：游戏进程/端口监听/连接/协议版本/挂起状态 + `debugPortFromLog` |
| `attach(host, port)` | 连接游戏调试端口（推荐传入 status 的 debugPortFromLog） |
| `detach` | 安全断开，游戏继续运行 |
| `resume` / `suspend` | 恢复 / 挂起整个 VM |
| `launch(path, port, workingDir)` | 以调试模式启动游戏（已有游戏进程会拒绝） |

### 断点与事件
| 工具 | 说明 |
|---|---|
| `break_add(method, line?)` | 方法入口断点或源码行断点；`Verse.Thing:DoWork` 或 `Verse.Thing:DoWork(System.String)` |
| `break_list` / `break_remove(id)` / `break_clear` | 断点注册表管理 |
| `break_exception(type?, caught?, uncaught?)` | 异常事件请求（缺省全部异常） |
| `wait(eventType?, timeoutMs?)` | 阻塞等待匹配事件，命中返回线程/位置/调用栈摘要 |
| `step(threadId, direction?)` | 单步 into/over/out；无调试符号时自动退化为指令级 |

### 观测
| 工具 | 说明 |
|---|---|
| `threads` | 全部线程（id/threadId/名称/状态） |
| `callstack(threadId, frameLimit?)` | 逐帧方法全名 + IL 偏移 + 源码 file:line |
| `locals(threadId, frameIndex?)` | 实参 / 局部量 / `this`，子对象返回句柄 |
| `inspect(handle)` | 按句柄展开对象字段（深度≤3、字段≤50、字符串≤512、数组前≤32） |

### 求值与搜索
| 工具 | 说明 |
|---|---|
| `eval(expression, threadId?, frameIndex?)` | 游戏内求值：`this` / 局部量 / 静态类型 / 字面量 / 成员链 `.字段 .属性 .方法(args)` |
| `find_types(query, limit?)` | 按子串搜类型 |
| `find_methods(query, limit?)` | 按子串搜方法 |

统一返回结构：`{ ok, message?, data? }`。

---

## 5. 典型调试会话

```
1. status              # 游戏在跑？debugPortFromLog=?
2. attach(127.0.0.1, <debugPortFromLog>)
3. resume
4. find_methods("Tick")          # 或 find_types("ThingDef")
5. break_add("Verse.Root_Entry:Update")
6. resume
7. wait("breakpoint", 60000)     # 命中 → 拿 threadId
8. threads / callstack(threadId)
9. locals(threadId, 0)
10. eval("this.def.defName", threadId, 0)
11. step(threadId, "into")       # 可先 break_remove 避免断点打断单步
12. detach
```

---

## 6. 端到端验证

`e2e-driver\e2e.js` 用 Node 直接 spawn 服务器，通过 stdio 驱动完整流程：

```powershell
cd e2e-driver
node e2e.js "..\bin\Debug\net10.0\McpRimDebug.exe" all
```

阶段：
- `protocol` — 握手 / tools/list（核对 20 工具）/ status
- `flow` — launch（自动 attach）→ resume → wait → detach
- `breakpoint` — 断点命中 → threads/callstack/locals/eval → step → break_remove → detach

实测（2026-08-06）：`all` **46/46 全 PASS**。`flow` 阶段轮询 `debugPortOpen` 时实测命中旧端口残留 56335 → 新端口 56779，验证了动态端口发现设计。

> `all` 要求游戏**未运行**（flow 会自行 launch）。已在跑时请先关闭游戏，或只跑 `breakpoint` 阶段 attach 现有实例。
> 详细用例见 `测试用例清单.md`。

---

## 7. 已知限制

1. **无调试符号**：游戏本体程序集无 PDB，`Locations` 为空 →
   - 断点退化为**方法入口断点**（IL 0）；
   - `step` 自动退化为**指令级（Min）单步**（行级 Line 无序列点不产生 StepEvent）；
   - 带 PDB 的模组 DLL 无此限制（断点可用源码行，step 走行级）。
2. **端口随机**：每次启动都不同，必须经 `status.debugPortFromLog` 获取（见第 2 节）。
3. **一次一个游戏进程**：双实例端口冲突，`launch` 会拒绝。
4. **禁止 raw TCP 端口探测**：会触发 mono 代理 `DWP handshake failed` 杀掉游戏；端口探测一律走监听表。
5. `attach` 后必须先 `resume` 游戏才运行（`wait-for-managed-debugger=1`）。

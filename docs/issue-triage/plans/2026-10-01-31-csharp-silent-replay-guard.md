# MCP 侧静默重放守卫（[31]）实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 消灭「非幂等写请求被 MCP 无条件重发」这个静默重放 —— 凡 `POST`/`DELETE` 命中连接错误或超时时**一律不再自动重发**，改为立刻返回带明确处置指引的 `UE_TRANSPORT_UNCERTAIN`；同时把「本次调用被发出过几次」做成 `attempts` / `replayed` 两个字段，出现在 `callUnityExplorerAPI` 的**每一条**返回路径上（取代只在重试成功时才出现的 `reconnected`）。

**架构：** 一个纯函数 `isReplaySafeRequest(endpoint, method, opts)` 做唯一的重放判定 —— **默认值反转**：`GET` 一律可重放（读无副作用），`POST`/`DELETE` 一律**不可**重放，只有登记在 `UE_REPLAY_SAFE_ENDPOINTS` 白名单里的 6 条写端点（`console/addusing`、`console/reset`、`inspect/type`、`log/clear`、`hotreload/watch`、`mapmarker/recolor`）才允许；调用点 `opts.idempotent` 可显式覆盖推导结果。这样**新增写端点默认安全**，不需要逐个登记，也就不会漏掉「除 `execute_csharp_code` 之外的另外 11 个写端点」。`callUnityExplorerAPI` 的 catch 块（`:812-835`）在 `shouldRefreshPort` 命中且判定不可重放时，于进入 `detectGameStage()` 之前直接返回 `UE_TRANSPORT_UNCERTAIN`（`attempts:1` / `replayed:false` / `uncertain:true`），因此这个路径**完全离线可测**；测试缝隙是模块级的 `ueFetchImpl` + `_testHooks.setFetch`，让「POST 到底有没有被发第二次」能被端到端断言。

**技术栈：** Node.js（ESM，`MCP/package.json` 的 `"type":"module"`）、Node 内置测试运行器 `node --test`（`MCP/package.json:9` 的 `npm test` = `node --test "test/*.test.js"`）。**零新依赖、零 C# 改动、零 DLL 重编译**。

---

## 0. 行号复核（本计划所有行号均已逐条回读，**以本节为准**）

> **复核方法（重要）：** 下表所有行号都用
> `[System.IO.File]::ReadAllLines('<绝对路径>')[$<n>-1]` 复核过，**不是**用带行号的文件阅读工具的输出
> （那种输出会随编辑漂移 —— 本项目 `docs/issue-triage/AGENT-BRIEF.md` §3.5 记着它少报过 27 行、且同文件内偏移不一致）。
> 执行者若要再核实，用同样方式，不要相信任何「读到的行号」。
>
> 复核时确认：`MCP/index.js` **TOTAL = 5460 行**；`MCP/package.json` 的 `scripts.test` 在第 **9** 行；
> `Source/UELoader/UEHttpServer.cs` **TOTAL = 799 行**；`Source/UELoader/UEHttpHandler.cs` **TOTAL = 771 行**；
> `Source/UELoader/UEMainThreadDispatcher.cs` **TOTAL = 173 行**；`Source/UELoader/AutoDeploy.cs` **TOTAL = 406 行**；
> `docs/mcp-吐槽板.md` **TOTAL = 1459 行**。

### 0.1 `MCP/index.js`（主修点，5460 行）

| 行号 | 内容（逐字回读） | 本计划怎么用 |
|---|---|---|
| 732 | 空行 | **任务 1 的声明插在 732 与 733 之间** |
| 733-735 | `// UnityExplorer HTTP API 调用辅助函数` + opts 注释 | 插入点的上文锚点 |
| 736 | `async function callUnityExplorerAPI(endpoint, method = 'GET', body = null, opts = {}) {` | 函数签名 |
| 739-743 | `normal: config.timings?.ueRequestTimeoutMs ?? 5000` / `slow: config.timings?.ueSlowRequestTimeoutMs ?? 10000` / `const timeoutMs = opts.slow ? timeouts.slow : (opts.timeoutMs \|\| timeouts.normal);` | **741** 是任务 4 接配置项的那一行；**743** 顺带记录了一个既有缺陷（`slow:true` 时 `opts.timeoutMs` 被吞）→ §7 登记项 |
| 745-754 | 「options 提到 try 外：连接失败重试…可直接复用」+ `options.body = JSON.stringify(body)` | 同一 body 被复用这件事的原文；注释要一并更正 |
| 766-779 | `fetchWithTimeout`；**771** 是 `return await fetch(url, fetchOpts);` | **771 改成 `ueFetchImpl(...)`**（任务 2 的测试缝隙，唯一一处 fetch 入口） |
| 786-811 | try 主路径 | 800（401 重试成功 return）、802（401 失败 return）、805-811（正常 return）—— 任务 3 在这三条上补 `attempts`/`replayed` |
| 812-835 | 整个 catch 块 | **主修点** |
| 816 | `const isConnError = /fetch failed\|ECONNREFUSED\|ECONNRESET\|ENOTFOUND\|network error\|connection refused/i.test(...)` | 判定条件，本计划**不改这条正则** |
| 817 | `const shouldRefreshPort = isConnError \|\| (error && error._aborted);` | **超时也命中重试**的那一行 |
| 818-821 | `if (shouldRefreshPort) {` … `const retryResp = await fetchWithTimeout(retryUrl, options);` | 无条件重发的现场 |
| 829 | `reconnected: true` | **退役**（任务 3） |
| 831-834 | `catch (retryErr) { error = retryErr; }` | 重试也失败的分支 |
| 836-839 | `if (error && error._aborted) return { ... errorCode: 'UE_TIMEOUT' ... };` | 任务 3 补字段 |
| 840-856 | 阶段错误（844 / 847 / 849 / 851-855） | 任务 3 补字段 |
| 2930 / 2983 | `const TOOL_USAGE_NOTES = {` / 它的收尾 `};` | **任务 3 的 note 插在 2983 之前**（2981-2982 是 `hotreload_watch` / `hotreload_status` 两条） |
| 2822-2832 | `mapMarkerResponse(r)`：成功时只 `JSON.stringify(r.data)` | 顶层加字段对它无影响（已核） |
| 2861-2868 | `hotreloadWrap(tool, result)`：往顶层**加**字段 | 顶层加字段对它无影响（已核） |
| 3281 / 3290 | `function toolListLine(` / `function buildMenuNode(` | **源码切片护栏**：本计划所有插入点都在 3290 之前 ⇒ 该切片逐字节不变 |
| 1430 / 1675 | `// ---------- 快速测试触发链路（与 GABP 无关） ----------` / `// Tool definitions` | **源码切片护栏**（`test/quickTest.test.js:28`）：本计划不碰这两点之间 |
| 4211 | `async function handleToolCall(` | — |
| 4371-4382 | `case 'stop_game': {` … `}` | **源码切片护栏**（`test/toolchainFixes.test.js:263-268`） |
| 5107 | `result = await handleToolCall(resolved, innerArgs);` | 改 `callUnityExplorerAPI` 即同时覆盖直接调用与 `agg_call_tool` |
| 5416-5443 | `export { ... }`（5442 = `menuUniverseNames,`，5443 = `};`） | **任务 1 在 5442 之后加一行 export** |
| 5450-5459 | `export const _testHooks = {`（5453 = `setGabpBridge:`） | **任务 2 在 5453 之后加 `setFetch`** |

### 0.2 `callUnityExplorerAPI` 的**全部写端点调用点**（爆炸半径的完整清单）

> 复核方法：对 `MCP/index.js` 全文正则 `callUnityExplorerAPI\(` 逐行枚举（**肯定命中的逐字枚举，不存在假阴性**），
> 结果 26 处：1 处是函数定义（736），25 处是调用点；其中 `GET` 11 处、`POST`/`DELETE` **14 处**。
> 14 个写调用点对应 **15 条写端点路径**（`:2719` 的 `endpoint` 是运行期决定的二选一）。

| # | `MCP/index.js` 行号 | 端点 | 方法 | 是否慢档 | 同一路径在 `UEHttpServer.cs` 的路由 |
|---|---|---|---|---|---|
| 1 | `:2719`（经 `callAreaAction`，`endpoint` 由 `:4271` 决定） | `/area/delete` | POST | 否 | `:410-412` |
| 2 | `:2719` | `/area/clear` | POST | 否 | `:414-416` |
| 3 | `:4423` | `/unityexplorer/inspect/type` | POST | 否 | `:387-389` |
| 4 | `:4443` | `/unityexplorer/log/clear` | POST | 否 | `:418-419` |
| 5 | `:4474` | `/unityexplorer/hook/create` | POST | `{slow:true}`（`:4479`） | `:425` |
| 6 | `:4499` | `/unityexplorer/hook/toggle` | POST | 否 | `:433` |
| 7 | `:4511` | `/unityexplorer/hook` | DELETE | 否 | `:441` |
| 8 | `:4532` | `/unityexplorer/console/execute` | POST | `{slow:true}`（`:4534`） | `:391-393` |
| 9 | `:4548` | `/unityexplorer/console/reset` | POST | 否 | `:395-396` |
| 10 | `:4557` | `/unityexplorer/console/addusing` | POST | 否 | `:398-400` |
| 11 | `:4583` | `/unityexplorer/hotreload/apply` | POST | 否 | `:518` |
| 12 | `:4589` | `/unityexplorer/hotreload/watch` | POST | 否 | `:522` |
| 13 | `:5001` | `/mapmarker/clear` | POST | 否 | `:505` |
| 14 | `:5008` | `/mapmarker/recolor` | POST | 否 | `:509` |
| 15 | `:5038` | `/mapmarker/set` | POST | `{slow:true}`（同行） | `:492` |

### 0.3 游戏侧（**次责，本计划默认不改，见 §2.2**）

| 行号 | 内容 |
|---|---|
| `Source/UELoader/UEHttpHandler.cs:192` | `ConsoleController.Evaluate(code, false);` |
| `Source/UELoader/UEHttpHandler.cs:201` | `}, 20000);` —— 游戏侧主线程预算 **20 秒** |
| `Source/UELoader/UEHttpHandler.cs:212-213` | `if (err != null) return Error(400, err, "COMPILATION_ERROR");` |
| `Source/UELoader/UEMainThreadDispatcher.cs:61` | `ExecuteOnMainThread(Action action, int timeoutMs = 10000)` |
| `Source/UELoader/UEMainThreadDispatcher.cs:86-87` | `if (!completed) throw new TimeoutException("Main thread operation timed out");` —— **超时只是不再等待，动作仍在主线程队列里** |
| `Source/UELoader/UEMainThreadDispatcher.cs:122` | `UEHttpLog.Warning($"[UEHttp] 主线程操作超时({timeoutMs}ms)，仍在主线程执行，结果弃置");` |
| `Source/UELoader/UEHttpServer.cs:541-543` | `int status = …; LogResult(result, sw); SendJson(response, status, result);` —— **返回 void，上层无从得知写失败** |
| `Source/UELoader/UEHttpServer.cs:778-794` | `SendJson(...)`；**792** 是 `UEHttpLog.Warning($"[UEHttp] Failed to send response: {ex.Message}");` —— **签名里没有 path / 请求标识** |
| `Source/UELoader/UEHttpServer.cs:585-614` | `BuildArgsSummary` 的端点 switch —— **没有 `area/*` 分支**（`:410/:414` 两条路由的参数不进请求日志） |
| `Source/UELoader/UEHttpServer.cs:730-736` | `PostGuard`：只做 method + token 检查 |

### 0.4 其它

| 对象 | 事实 |
|---|---|
| `MCP/config.json` | **全文单行**：`{"gamePath":…,"logPath":…,"steamPath":…,"appId":"294100","startTimeout":180000}` —— **无 `timings`、无 `rimBridge`** |
| `Source/UELoader/AutoDeploy.cs:253-315` | `WriteConfig` **先读现有文件再逐键覆盖**（`:263-271`），**只写** `gamePath/logPath/steamPath/appId/startTimeout`（`:281-295`）⇒ **手写的 `timings` 不会被抹掉，也永远不会被自动生成** |
| `Source/UELoader/UELoaderSettings.cs:197-198` | 设置界面写回 config.json 同样「保留未知键」 |
| `MCP/ports.json` | 存在：`{"ueHttpPort":3001,"token":"…","ueHttpStatus":"running",…}`。测试里 fetch 被替换 ⇒ **不会产生任何网络流量** |
| `MCP/package.json:9` | `"test": "node --test \"test/*.test.js\""` |
| `docs/mcp-吐槽板.md` | **1459 行**；锚点 `^## \[31\]` → 正文 **296-330**（建议三条在 **324-326**）；索引行 **48** |

### 0.5 既有断言护栏（改 `index.js` 前后逐条确认没碰碎）

| 断言 | 位置 | 本计划为什么不该影响它 |
|---|---|---|
| `toolListLine` → `buildMenuNode` 源码切片 | `MCP/test/toolchainFixes.test.js:120-124` | 切片两端在 **3281 / 3290**；本计划所有插入点在 732 / 771 / 812-856 / 2983 / 5442 / 5453，**全在 3281 之前** ⇒ 切片逐字节不变 |
| `start_game` / `stop_game` 源码切片 | `MCP/test/toolchainFixes.test.js:263-268` | 依赖 **4371-4382**，本计划不碰 |
| 快速测试链路切片 | `MCP/test/quickTest.test.js:28` | 依赖 **1430 → 1675** 两个标记，本计划不碰 |
| `start_quick_test` 派发必须在镜像块之前 | `MCP/test/quickTest.test.js:41-48` | 依赖 `:4262` 与 `switch (name) {` 的相对位置，本计划不碰 |
| GABP 镜像未知参数（计划 22 的产物） | `MCP/test/toolchainFixes.test.js:60-70` | 走 `gabpBridge`，与 `callUnityExplorerAPI` 无交集 |
| **超时策略 17 例** | `MCP/test/timeoutPolicy.test.js` | 全部测 `longTask/timeoutPolicy.js` + `gabpClient.js`（**GABP 通道**）。检索范围：`MCP/test/` 全目录对 `reconnected\|callUnityExplorerAPI\|attempts\|replayed\|UE_TIMEOUT` 检索 → **0 命中** ⇒ **`callUnityExplorerAPI` 的重试路径此前零测试覆盖**（本计划新增测试不会撞既有断言） |
| `post_map_marker` 工具层 | `MCP/test/mapMarker.test.js`（106 行全文已读） | 只测 tools/list、`mcp_help`、`normalizeToolArgs`、本地参数校验；**不断言网络返回体形状** ⇒ 顶层加字段无影响 |

---

## 1. 背景：复核结论 + 对裁决报告的四处更正

上游裁决报告 `triage/31-execute-csharp-code-silent-replay.md`（298 行，已全文读完）结论「值得修 / 单独立计划 / 主责在 MCP」，本计划**采纳**。本节只做两件事：把复核结果摆出来，并把**复核中发现���裁决报告写得不准或漏掉的地方**更正掉。执行者不需要重读 triage 报告即可开工。

### 1.1 板上 [31] 的原文要点（锚点 `^## \[31\]`，正文 296-330 行；**定位用标题锚点，未用行号**）

- **①症状**：一段「扫描并销毁 46×46 区域内 2099 个天然岩石」的 C#，MCP 返回体写的是 `destroying 0 … destroyed=0`；而板上引用的 `Player.log` 里是 `destroying 2099 … destroyed=2099`，外加 `[UEHttp] Failed to send response: Unable to write data to the transport connection: 远程主机强迫关闭了一个现有的连接。`（`:309-311`）⇒ 第一遍改了状态、写响应时连接被 RST，MCP 重试，返回的是第二遍的结果。
- **③影响**：返回体里的数字**是第二遍的**；调用方要么据此判失败、要么再发一次 —— 两条路都会得到相反的事实结论。
- **④建议三条**（`:324-326`）：
  1. `UEHttpHandler` 写响应失败时不要让上层无条件重试带副作用的调用，**或在重试前先回一句「上次可能已执行」**；
  2. 至少在 MCP 侧标注本次调用被执行了 N 次（哪怕只是 `attempts: 2`）；
  3. 文档里写死一条纪律：带副作用的 `execute_csharp_code`，验收必须回 `Player.log` 看第一遍的输出。

> ⚠️ **板上引用的那三行 `Player.log` 本轮无法复核** —— 日志已两次轮转，磁盘上检索不到 `DOORFIX`。
> **本计划不依赖那条日志**：下面每一条结论都是源码直读。

**对建议 ① 的处理（重要）：** 其中「**在重试前先回一句『上次可能已执行』』在当前协议下不可实现** —— 连接已被对端 RST，同一条连接上再写什么都会失败（这正是板上那条 `Failed to send response` 的由来）。**唯一能落地的形态就是「别重发」或「服务端去重」**。本计划做前者，服务端去重登记在 §7（需改协议 + 重编译，本轮不做）。

### 1.2 主修点复核：确凿，且比裁决报告写的还要硬一层

`MCP/index.js:812-835` 的 catch 块逐字复核（全文见 §0.1）：

| 事实 | 位置 | 说明 |
|---|---|---|
| **A. 无条件重发带副作用的请求** | `:818-821` | 对 `POST` / `DELETE` 一视同仁；**没有**任何 `if (isIdempotent)` 之类判断 |
| **B. 触发条件逐字命中** | `:816` | 正则含 `fetch failed` 与 `ECONNRESET` 两项；板上那条「远程主机强迫关闭了一个现有的连接」就是 `ECONNRESET` |
| **C. 超时也重试** | `:817` | `shouldRefreshPort = isConnError \|\| (error && error._aborted)` |
| **D. body 被复用** | `:745-754` | `:745` 的注释自己写着「options 提到 try 外：连接失败重试…可直接复用」⇒ **设计上就没打算区分幂等性** |
| **E. 没有请求身份** | 整段 | 无 idempotency key、无 attempt 计数、无「本次是否已执行」的任何标记 |

**🔴 更正 1（比裁决报告更硬的一层证据）：不只是「可能执行了两次」，超时路径下是「两次都会执行」。**
`Source/UELoader/UEMainThreadDispatcher.cs:86-87` 的超时分支只是 `throw new TimeoutException("Main thread operation timed out")` —— **它不取消动作，动作仍留在主线程队列里等着跑**；`:122` 的告警原文就是「仍在主线程执行，结果弃置」。⇒ 当 `execute_csharp_code` 的执行时间落在 10–20 秒之间时：MCP 在 10s 中止（`:817`），发第二个请求；第一个动作照样在主线程上跑完（结果被 HTTP 层弃置），第二个请求排在它后面**再跑一遍**。这比「返回了第二遍的数字」更糟：**两次改的是同一个世界**，且第二次跑在一个已经被第一次改过的世界上。

**⇒ 重放是结构必然，不是罕见边界**：MCP 慢档 **10s**（`:741` 的 `?? 10000` + `config.json` 无 `timings`）< 游戏侧主线程预算 **20s**（`UEHttpHandler.cs:201`）⇒ **任何执行超过 10 秒的写调用 100% 被重发**。

### 1.3 🔴 更正 2：写端点是 **15 条路径 / 14 个调用点**，不是裁决报告 §1.7 列的 12 个

裁决报告 §1.7 用 ripgrep 列出 12 个写端点，**漏掉了 `/area/delete` 与 `/area/clear`**（`MCP/index.js:2719` ← `endpoint` 由 `:4271` 决定；服务端路由 `UEHttpServer.cs:410-416`，调 `UEAreaActions.DeleteArea / ClearArea`）。这是两条**真·有副作用**的写端点，删两遍的失效形态与 `console/execute` 同类（第二遍找不到目标 ⇒ 报错 ⇒ 假失败）。完整清单（含每条的第二失效形态）见 **§0.2**。

**对本计划的影响：没有。** 因为本计划的方案是**按方法默认不重放**（§3），15 条全覆盖是**自动得到**的，不靠逐个登记 —— 这恰好是「按 `execute_csharp_code` 单工具立项会漏掉另外 11 个」这个坑的结构性解法：**新增写端点默认安全，不需要记得来登记**。裁决报告 §6-1 提的「`opts` 上加显式 `idempotent`，默认由端点/方法推导」方向是对的，本计划把它做成**一个纯函数 + 一张 6 条的白名单**。

### 1.4 🔴 更正 3：裁决报告 §6-4 建议的「给 `execute_csharp_code` 显式 `timeoutMs`（如 30000）」**在本仓库走不通**

`MCP/index.js:743` 是：

```javascript
  const timeoutMs = opts.slow ? timeouts.slow : (opts.timeoutMs || timeouts.normal);
```

**`slow:true` 时 `opts.timeoutMs` 被整段吞掉** —— 而 `:4534` 恰恰传的就是 `{ slow: true }`。照裁决报告原样做会给 `execute_csharp_code` 加一个**完全不起作用**的参数，看起来配了、实际还是 10s。

**本计划的处置（任务 4）：走 `config.timings.ueSlowRequestTimeoutMs`** —— `:741` 已经在读这个键，只是 `MCP/config.json` 里没有它。改配置比改代码取值顺序更安全（代码默认值保持 `10000` 不动 ⇒ 配置缺失时行为与今天逐字节相同）。`:743` 吞 `opts.timeoutMs` 这个既有缺陷登记在 §7.1。

### 1.5 🟠 更正 4：`MCP/config.json` 缺 `timings` 不只是「回落默认值」，它还顶着一条既有断言

`MCP/test/index.integration.test.js:235` 断言 `typeof cfg.timings === 'object'`（该测试的作用是防 `AutoDeploy` 把嵌套字典拼平成 `System.Collections.Generic.Dictionary\`2[...]`）。而当前 `MCP/config.json` 快照**没有 `timings` 也没有 `rimBridge`** ⇒ **这条断言在当前快照下不成立**。

（本计划的作者**没有跑过测试**（§2.3 要求执行者先取基线），所以这里只陈述「快照内容与断言不相容」这个**可核事实**，不断言测试当前的红/绿。任务 4 补上 `timings` 后它才成立 —— 那是**顺手修掉一条既有红**，不是新引入的。）

`AutoDeploy.WriteConfig`（`Source/UELoader/AutoDeploy.cs:253-315`）**只写** `gamePath/logPath/steamPath/appId/startTimeout`（`:281-295`），但它是**先读现有文件再逐键覆盖**（`:263-271`）⇒ **手写的 `timings` 不会被抹掉，但也永远不会被自动生成**。`UELoaderSettings.WriteProjectConfigs`（`Source/UELoader/UELoaderSettings.cs:197-198`）同样是「保留未知键」。⇒ 手写 `timings` 是安全且持久的。

### 1.6 已有信号 `reconnected` 的现状（裁决报告 §1.6 复核 + 时效更正）

- `MCP/index.js:829` 的 `reconnected: true` **只在「重试成功」时出现**；重试也失败时走 `:837` / `:841-856`，该字段消失 ⇒ 「代码已经跑过一次」这个事实 **100% 丢失**。
- **零说明性文档**：检索范围 `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\` 全树，字面量 `reconnected` 只命中三处 —— `triage/31-…md`（裁决报告自己）、`plans/2026-10-01-02-…md`（计划 02 的前置闸门在引用它）、以及本计划自身。**没有任何一处是「告诉调用方这个字段存在」的说明**。（裁决报告写作当时该结论成立，本节是它的时效更新。）
- **语义混叠**：它同时表示「刷新了 ports.json」与「重发了一次请求」，调用方分不出这两件事。

⇒ 任务 3 把它退役，换成**无条件**的 `attempts` / `replayed`（外加只在真正「不敢断定执行没发生」时出现的 `uncertain`），并在 `TOOL_USAGE_NOTES` 里点名，让 `mcp_help tool=` 查得到。

### 1.7 ✅ 一条**不需要**改的重试：401 刷新 token 重发是安全的

`:794-801` 那条「401 → 刷新 token → 重发一次」的路径**本计划一个字都不改**，理由是服务端侧的顺序：

```csharp
// Source/UELoader/UEHttpServer.cs:730-736
static Dictionary<string, object> PostGuard(HttpListenerRequest request, Func<Dictionary<string, object>> handler)
{
    if (request.HttpMethod != "POST")
        return MethodNotAllowed();
    if (!IsAuthorized(request)) return Unauthorized();   // ← 在 handler 之前就返回了
    return handler();
}
```

**401 ⇒ `handler()` 根本没被调用 ⇒ 第一次没有任何副作用 ⇒ 重发是安全的、也是必要的自愈**（`DeleteGuard` `:745-751` 同构）。把这条一并禁掉会**打断 token 滚动后的自愈能力**，那才是真回归。任务 3 只给它补上 `attempts: 2, replayed: true` 字段以便观测，**不改它的行为**。

---

## 2. 范围

### 2.1 ✅ 进计划

| 项 | 落点 | 理由 |
|---|---|---|
| **① 可重放性判定（默认值反转的纯函数 + 6 条白名单）** | 任务 1 | 唯一的重放决策点；离线可测；新增写端点默认安全 |
| **② 非幂等请求命中连接错误/超时时不再重发，改为 `UE_TRANSPORT_UNCERTAIN`** | 任务 2 | 这是本条**唯一真正的止血**。放在进入 `detectGameStage()` 之前 ⇒ 完全离线可测 |
| **③ `attempts` / `replayed` 无条件化 + `reconnected` 退役 + `TOOL_USAGE_NOTES` 点名** | 任务 3 | 板上建议 ②。成本近乎为零，收益是「第二遍」永远不再静默 |
| **④ 慢档超时预算对齐（`config.json` 登记 `timings.ueSlowRequestTimeoutMs: 30000`）** | 任务 4 | 修 §1.2 的「结构必然」；配置项改法比改代码取值顺序安全 |
| **⑤ `post_map_marker` 失败路径把 `uncertain` 信号显式写进错误文本** | 任务 3 步骤 8 | `mapMarkerResponse`（`:2822-2832`）把整个返回体拍平成一行纯文本，顶层字段会被吃掉 ⇒ 不补这一步，[31] 在地图光标工具上仍然静默 |

### 2.2 ❌ 明确排除

| 排除项 | 理由 |
|---|---|
| **🚫 写成「改 `UEHttpHandler` / `UEHttpServer` 让它别重试」** | **重试 100% 在 MCP 侧**（`MCP/index.js:812-835`）。`UEHttpServer.cs:391-393` 的路由只把 `code` 传下去，服务端**根本没有任何重试逻辑**，改它是找不到北的写法 |
| **🚫 板上建议 ① 的「重试前先回一句『上次可能已执行』」** | 物理不可实现：连接已被 RST，同连接上再写必然失败（板上那条 `Failed to send response` 就是这件事本身）。**不要为它设计任何代码** |
| **协议级正解（`requestId` + 服务端去重缓存）** | 唯一能让「重试」变成安全操作的改法，但需要改协议 + 改 DLL + 停游戏重新构建 + 定缓存淘汰策略。登记在 §7.2，**明确不与本计划打包** |
| **`UEHttpServer.SendJson` 告警带上 path / 请求标识** | 次责且**要改 `.cs` + 重编译**。本仓库此刻有一个并行调试会话正在用当前 DLL —— 重建会把它现场搞坏。登记在 §7.2，**默认不实施** |
| **改 `:743` 的取值顺序（`opts.timeoutMs` 被 `slow` 吞掉）** | 是真缺陷但与本条正交，且改了会动到全部慢档调用。登记在 §7.1 |
| **给 `MCP/index.js:812` 的 `:816` 正则增删关键字** | 判定条件本身没错（`ECONNRESET` / `fetch failed` 都该命中）；问题是「命中之后要不要重发」，不是「什么算命中」 |
| **任何 `Verse.Log` / `Debug.Log` 的 Harmony patch** | `MCP/index.js:4461-4462` 已把这条路列为黑名单（实测 1 分钟 63.5MB + mono 原生崩溃） |
| **改计划 02 的任何文件** | 那是另一个子代理的产物。本计划只在 §6 登记前置风险 |

### 2.3 ⚠️ 执行纪律（本计划对执行者的硬要求）

1. **先取测试基线再动手。** 本计划的作者**没有跑过 `npm test`**。执行者第一件事是在 `MCP` 目录跑一次 `npm test` 并**原样记录红/绿**，之后每一步都与基线对比 —— 只允许「基线红的仍然红」和「本计划的 19 条新测试转绿」，**不允许出现任何新的红**。
2. **全程离线。** 测试里 fetch 被替换成假实现（`_testHooks.setFetch`），**不得**在验证期间调用任何会动游戏状态的 MCP 工具（`execute_csharp_code` / `start_game` / `execute_debug_action` / `post_map_marker` …）—— 本仓库此刻有并行会话在调试同一款游戏。唯一允许的真机动作是任务 4 收尾的可选人工核对，且必须由维护者本人在并行会话结束后自己做。
3. **不重编译、不启动游戏、不改游戏安装目录。** 本计划**零 C# 改动**，因此没有「构建」这一步；`MCP/index.js` 直接就是生效的那份（`runtime/` 下没有 `index.js` 副本），改完即生效，**不需要构建部署**。
4. **行号纪律。** §0 的行号已用 `[System.IO.File]::ReadAllLines` 逐条复核；执行者若因前面的任务插入了行而使后续行号漂移，**按锚点文本定位，不要按行号硬套**。

---

## 3. 文件结构

| 文件 | 动作 | 职责 |
|---|---|---|
| `MCP/index.js` | 修改（1 处插入 + 3 处改写 + 9 处字段补齐，约 +90 行） | 全部逻辑：重放判定、拦截、`attempts`/`replayed` 信号、错误文案 |
| `MCP/test/replayGuard.test.js` | **新建** | 18 个测试，分三段：①纯函数（判定语义）②端到端（假 fetch，断言「POST 只发一次」）③信号与文案 |
| `MCP/config.json` | 修改（新增一个 `timings` 键） | 慢档超时预算 |

**明确不动：** `Source/` 下任何 `.cs`、`Assemblies/` 下任何 `.dll`、`runtime/`、`MCP/gabpClient.js`、`MCP/longTask/**`、`MCP/toolConfig.json`、`MCP/package.json`、`MCP/ports.json`、任何 `*.log`、`docs/mcp-吐槽板.md`、`docs/issue-triage/` 下除本计划自身以外的任何文件（含 `plans/2026-10-01-02-…md`）。

### `MCP/index.js` 的 7 个改动点一览

| # | 锚点（行号已复核） | 动作 | 任务 |
|---|---|---|---|
| 1 | `732` 之后 / `733` 之前 | 新增 `UE_REPLAY_SAFE_ENDPOINTS` + `isReplaySafeRequest()` + `ueFetchImpl` | 1（fetch 变量在任务 2 用到，此处一并声明） |
| 2 | `5442` 之后 | `export` 清单加 `isReplaySafeRequest` | 1 |
| 3 | `771` | `await fetch(url, fetchOpts)` → `await ueFetchImpl(url, fetchOpts)`（**唯一** fetch 入口） | 2 |
| 4 | `745` / `818-835` | 注释更正 + catch 块加「不可重放 ⇒ 直接返回 `UE_TRANSPORT_UNCERTAIN`」分支 | 2 |
| 5 | `5453` 之后 | `_testHooks` 加 `setFetch` | 2 |
| 6 | `800` / `802` / `805-811` / `829` / `838` / `844` / `847` / `849` / `851-855` | 全部返回路径补 `attempts`/`replayed`；`829` 的 `reconnected` 退役 | 3 |
| 7 | `2822-2832` / `2983` 之前 | `mapMarkerResponse` 失败路径带出 `uncertain`；`TOOL_USAGE_NOTES` 新增一条 | 3 |

> **为什么 #1 的常量表放在 `callUnityExplorerAPI` 前面而不是后面：** 它们是模块级 `const`，函数体在调用时才求值 ⇒ 只要在模块求值完成前初始化就安全（TDZ 不构成风险）。但放在调用点正上方最易读，也让任务 1 的「先写测试再实现」只有一个落点。`isReplaySafeRequest` 是**纯函数**（不读 `config`、不碰网络、不改状态），所以测试可以直接从 `mod` 上断言，不需要任何注入口。

---

## 任务 1：重放判定的唯一事实源（默认值反转的纯函数 + 6 条白名单）

**交付物：** `isReplaySafeRequest(endpoint, method, opts)` —— 一个纯函数，回答「这次请求在连接错误/超时后能不能原样重发一次」。**15 条写端点路径全部默认不可重放**（§0.2），只有 6 条幂等的写端点在白名单里；新增写端点自动落在「不可重放」一侧。

**文件：**
- 修改：`MCP/index.js:732` 之后 / `733` 之前（新增常量表 + 两个纯函数 + `ueFetchImpl` 变量）
- 修改：`MCP/index.js:5442` 之后（`export` 清单加一行）
- 创建：`MCP/test/replayGuard.test.js`

- [ ] **步骤 1：先跑一次测试基线并原样记录**

运行（在 `MCP` 目录）：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：把**每一条**失败用例的测试文件名 + 用例名 + 报错首行抄进你的工作笔记。这就是基线。§1.5 已经预告：当前 `MCP/config.json` 快照没有 `timings` 键，而 `MCP/test/index.integration.test.js:235` 断言 `typeof cfg.timings === 'object'` —— **这条很可能就在基线的红名单里**。任务 4 会把它变绿，那是预期内的。

> 若基线是**全绿**，那说明工作副本的 `config.json` 与 §0.4 记录的快照不同（游戏可能刚用 AutoDeploy 写过一份）。**以你本地的实际文件为准**，并在工作笔记里更正 §0.4；后续所有判断仍按「`config.json` 是合并写的、可手写」这条结论走（`AutoDeploy.cs:263-271`）。

- [ ] **步骤 2：编写失败的测试（任务的第 1 段：纯函数语义）**

创建 `MCP/test/replayGuard.test.js`：

```javascript
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 关键：必须在 import('../index.js') 之前设守卫，否则会启动服务器监听 3000 端口
process.env.UESD_MCP_NO_START = '1';
const mod = await import('../index.js');

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, '..', 'index.js'), 'utf-8');

// ===========================================================================
// ① 重放判定：默认值反转的纯函数（issue-triage [31]，triage 报告 §1.7）
// ===========================================================================

// §0.2 的完整写端点清单：15 条路径 / 14 个调用点。
// 这里逐条钉死「默认不可重放」，**新增端点的人不会漏登记**（默认就在不可重放一侧）。
const NON_REPLAYABLE_WRITES = [
  ['/area/delete', 'POST'],            // index.js:2719 ← :4271（callAreaAction）
  ['/area/clear', 'POST'],             // index.js:2719 ← :4271
  ['/unityexplorer/inspect/type', 'POST'],   // :4423（纯读，但走 POST；白名单里，见下）
  ['/unityexplorer/log/clear', 'POST'],      // :4443
  ['/unityexplorer/hook/create', 'POST'],    // :4474 {slow:true}
  ['/unityexplorer/hook/toggle', 'POST'],    // :4499
  ['/unityexplorer/hook', 'DELETE'],         // :4511
  ['/unityexplorer/console/execute', 'POST'],// :4532 {slow:true} —— 板上 [31] 的当事人
  ['/unityexplorer/console/reset', 'POST'],  // :4548
  ['/unityexplorer/console/addusing', 'POST'], // :4557
  ['/unityexplorer/hotreload/apply', 'POST'],  // :4583
  ['/unityexplorer/hotreload/watch', 'POST'],  // :4589
  ['/mapmarker/clear', 'POST'],         // :5001
  ['/mapmarker/recolor', 'POST'],       // :5008
  ['/mapmarker/set', 'POST'],           // :5038 {slow:true}
];

// 白名单外的写端点逐条断言不可重放。inspect/type 在白名单里，从这份清单里剔掉。
const MUST_BLOCK = NON_REPLAYABLE_WRITES.filter(([ep]) => ep !== '/unityexplorer/inspect/type');

test('T1 写端点默认不可重放：14 条逐条钉死', () => {
  for (const [ep, method] of MUST_BLOCK) {
    assert.equal(mod.isReplaySafeRequest(ep, method), false,
      `${method} ${ep} 必须默认不可重放：它有副作用，重发会改第二遍世界（或把「成功」骗成假成功）`);
  }
  assert.equal(MUST_BLOCK.length, 14, '写端点清单应与 §0.2 一致（15 条路径里 inspect/type 走白名单）');
});

test('T1 读端点一律可重放（读没有副作用，不能因此打断端口滚动自愈）', () => {
  for (const ep of [
    '/unityexplorer/status',
    '/unityexplorer/hook/list',
    '/unityexplorer/log?count=50',        // :4453 模板串，带查询串
    '/debugaction/categories',            // :4913
    '/debugaction/search?q=spawn',        // :4949 模板串
    '/mapmarker/list',                    // :4994
    '/mapstructure/search?query=Thing',   // :5045 模板串
    '/unityexplorer/hotreload/status',    // :4595 / :1760
  ]) {
    assert.equal(mod.isReplaySafeRequest(ep, 'GET'), true, `GET ${ep} 必须可重放`);
  }
});

test('T1 白名单：6 条幂等写端点允许重发，其余写端点一条都不许混进来', () => {
  for (const ep of [
    '/unityexplorer/console/addusing',
    '/unityexplorer/console/reset',
    '/unityexplorer/inspect/type',
    '/unityexplorer/log/clear',
    '/unityexplorer/hotreload/watch',
    '/mapmarker/recolor',
  ]) {
    assert.equal(mod.isReplaySafeRequest(ep, 'POST'), true, `${ep} 在白名单里，应当可重放`);
  }
  // 反向护栏：白名单不许悄悄变长（每加一条都要在代码注释里写清「为什么重发与不重发结果相同」）
  const start = source.indexOf('const UE_REPLAY_SAFE_ENDPOINTS = new Set([');
  assert.ok(start > 0, '找不到 UE_REPLAY_SAFE_ENDPOINTS');
  const block = source.slice(start, source.indexOf(']);', start));
  const entries = block.split('\n')
    .map((l) => l.trim())
    .filter((l) => l.startsWith("'") && l.endsWith("',"));   // 条目行以引号开头、以 "'," 结尾；注释行以 // 开头
  assert.equal(entries.length, 6, `白名单应恰好 6 条，实际 ${entries.length}：${JSON.stringify(entries)}`);
});

test('T1 opts.idempotent 显式覆盖优先于方法/端点推导', () => {
  // 调用点显式说「可重放」 ⇒ 放行（GET 之外也能显式打开）
  assert.equal(mod.isReplaySafeRequest('/unityexplorer/hook/create', 'POST', { idempotent: true }), true);
  // 调用点显式说「不可重放」 ⇒ 连 GET 都不放行（读端点在极端情况下也可能不可重放）
  assert.equal(mod.isReplaySafeRequest('/unityexplorer/status', 'GET', { idempotent: false }), false);
  // 不传 ⇒ 走推导
  assert.equal(mod.isReplaySafeRequest('/unityexplorer/status', 'GET', {}), true);
  assert.equal(mod.isReplaySafeRequest('/unityexplorer/console/execute', 'POST', {}), false);
});

test('T1 路径与方法写法差异不影响判定（大小写 / 缺前导斜杠 / 多余斜杠 / 空值）', () => {
  assert.equal(mod.isReplaySafeRequest('/unityexplorer/console/addusing', 'post'), true,
    '小写方法名也认（推导前统一大写）');
  assert.equal(mod.isReplaySafeRequest('unityexplorer/console/addusing', 'POST'), true,
    '缺前导斜杠也认（调用点写法不统一）');
  assert.equal(mod.isReplaySafeRequest('//unityexplorer//console//addusing', 'POST'), true,
    '重复斜杠要归一，否则白名单会被写法差异架空');
  assert.equal(mod.isReplaySafeRequest('/UnityExplorer/Console/Execute', 'POST'), false,
    '大小写不影响判定（端点比较不区分大小写）');
  // 防御：空 endpoint 不得抛异常，也不得被判成「可重放」
  assert.equal(mod.isReplaySafeRequest('', 'POST'), false);
  assert.equal(mod.isReplaySafeRequest(null, 'POST'), false);
  assert.equal(mod.isReplaySafeRequest(undefined, 'POST'), false);
});
```

- [ ] **步骤 3：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**5 个全部 FAIL**，报错统一是 `TypeError: mod.isReplaySafeRequest is not a function`（最后一条 `T1 白名单` 会先炸在 `source.indexOf(...) === -1` 上，同样是 FAIL）。**这正是我们要的失败形态** —— 证明测试真的在检验新行为，而不是恒真。

- [ ] **步骤 4：实现判定层（`MCP/index.js:732` 之后 / `733` 之前插入）**

在 `MCP/index.js:732`（空行）与 `:733`（`// UnityExplorer HTTP API 调用辅助函数`）**之间**插入：

```javascript
// ========== 传输层重放守卫（issue-triage [31]） ==========
// 背景：catch 块（:812-835）此前对**所有** POST/DELETE 无条件重发同一个请求。板上 [31] 的事故
// 就是这么来的：第一遍 execute_csharp_code 改完游戏状态、写响应时 UE 的 HTTP 连接被对端 RST，
// MCP 重发，第二遍拿到的是「第一遍做完之后的世界」，而调用方把第二遍的数字当成本次执行的结果。
// 更糟的是超时路径：UEMainThreadDispatcher.cs:86-87 的超时只抛 TimeoutException，**动作仍在主线程队列里**
// （:122 的告警原文「仍在主线程执行，结果弃置」）⇒ 两次都会执行，且第二次跑在被第一次改过的世界上。
//
// 判定原则（**默认值反转**，而不是逐个登记）：读（GET）无副作用 ⇒ 可重放；
// 写（POST/DELETE）**默认不可重放**；白名单只收「重发与不重发结果完全相同」的写端点。
// 这样新增写端点**默认安全**，不需要记得来登记 —— 这也正是「按 execute_csharp_code 单工具立项会漏掉
// 另外 11 个写端点」的结构性解法。
//
// ⚠️ 本守卫只管 catch 块那条「连接层重发」路径。401 刷新 token 的重发（:794-801）**不归它管，也不该归它管**：
// UEHttpServer.cs:730-736 的 PostGuard 在鉴权失败时**先 return Unauthorized()、根本不调 handler**，
// 所以 401 ⇒ 第一次没有任何副作用 ⇒ 重发既安全又必要（token 滚动后的自愈）。把它一起禁掉是纯回归。
const UE_REPLAY_SAFE_ENDPOINTS = new Set([
  '/unityexplorer/console/addusing',  // 幂等：重复加同一个 using 对编译上下文无副作用
  '/unityexplorer/console/reset',    // 幂等：重置回同一个初始态
  '/unityexplorer/inspect/type',      // 纯读（只是走 POST 传参），只返回类型元信息，不改任何东西
  '/unityexplorer/log/clear',         // 幂等：清两次还是空的
  '/unityexplorer/hotreload/watch',   // 幂等：把同一个开关设成同一个值
  '/mapmarker/recolor',               // 幂等：给同一个 id 设同一个颜色
  // 刻意**不**收 /unityexplorer/hook/toggle：重发会把开关翻两次 = 净效果为零，
  // 而响应还会报 success —— 这是本仓库能造出的最恶劣的假成功形态，必须拦。
]);

// fetch 的注入口。默认就是全局 fetch；测试用 _testHooks.setFetch 换掉它，
// 从而在**完全离线**的前提下断言「第二次请求到底有没有被发出去」。
let ueFetchImpl = (url, fetchOpts) => fetch(url, fetchOpts);

// 把 endpoint 归一成「小写、带前导斜杠、无查询串、无重复斜杠」，让白名单比较不受调用点写法影响。
function normalizeUeEndpoint(endpoint) {
  const raw = String(endpoint == null ? '' : endpoint).trim();
  const noQuery = raw.split('?')[0].split('#')[0];
  const withSlash = noQuery.startsWith('/') ? noQuery : `/${noQuery}`;
  return withSlash.replace(/\/{2,}/g, '/').toLowerCase();
}

// 这次请求在「连接错误 / 超时」后能不能原样重发一次。**纯函数**：不读 config、不碰网络、不改状态。
// opts.idempotent 显式传 true / false 时优先于推导；不传则按「方法 + 端点白名单」推导。
function isReplaySafeRequest(endpoint, method = 'GET', opts = {}) {
  if (opts && (opts.idempotent === true || opts.idempotent === false)) return opts.idempotent;
  const m = String(method == null ? '' : method).toUpperCase();
  if (m === 'GET' || m === 'HEAD') return true;        // 读：重发没有副作用
  if (m !== 'POST' && m !== 'DELETE') return false;    // PUT/PATCH 等一律保守（当前无调用点）
  return UE_REPLAY_SAFE_ENDPOINTS.has(normalizeUeEndpoint(endpoint));
}
```

- [ ] **步骤 5：把 `isReplaySafeRequest` 加进 export 清单（`MCP/index.js:5442` 之后）**

在 `MCP/index.js:5442`（`  menuUniverseNames,`）**之后**、`:5443`（`};`）**之前**加一行：

```javascript
  // issue-triage [31]：传输层重放判定（纯函数），供单测直接断言「哪些端点不许重发」
  isReplaySafeRequest,
```

- [ ] **步骤 6：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**5 个全部 PASS**。若 `T1 白名单` 那条失败并报 `白名单应恰好 6 条，实际 N`，说明数的方式不同（每条是否单独占一行）；**改实现里的排版，不要改断言的语义**。

- [ ] **步骤 7：跑全量测试确认没有引入新的红**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：与**步骤 1 的基线**逐条一致，只多出本任务的 5 条 PASS。特别确认这两条源码切片护栏没被碰碎（§0.5）：

| 断言 | 位置 | 为什么不该受影响 |
|---|---|---|
| `toolListLine` → `buildMenuNode` | `test/toolchainFixes.test.js:120-124` | 切片在 **3281→3290**，本次插入点在 **732** 与 **5442**，全在切片之前 |
| `start_game` / `stop_game` | `test/toolchainFixes.test.js:263-268` | 依赖 **4371-4382**，不碰 |

另外确认 `index.js` 仍能加载：`node -e "process.env.UESD_MCP_NO_START='1'; import('./index.js').then(m=>console.log('OK', typeof m.isReplaySafeRequest))"`（在 `MCP` 目录下执行；不启动服务器、不连游戏）。

- [ ] **步骤 8：改动清单（本项目不是 git 仓库，无提交动作）**

| 文件 | 动作 | 锚点 |
|---|---|---|
| `MCP/index.js` | 修改（+50 行） | `732` 之后 / `733` 之前：`UE_REPLAY_SAFE_ENDPOINTS` + `ueFetchImpl` + `normalizeUeEndpoint()` + `isReplaySafeRequest()` |
| `MCP/index.js` | 修改（+2 行） | `5442` 之后 export `isReplaySafeRequest` |
| `MCP/test/replayGuard.test.js` | **新建** | 5 个测试（第 1 段） |

**没有改任何 `.cs`、没有改 `config.json` / `package.json` / `gabpClient.js` / `toolConfig.json`、没有重编译、没有碰 `docs/` 下除本计划外的任何文件。**

---

## 任务 2：拦截重发（`UE_TRANSPORT_UNCERTAIN`）—— 本计划真正的止血

**交付物：** `callUnityExplorerAPI` 的 catch 块在 `shouldRefreshPort` 命中**且** `isReplaySafeRequest` 判否时，**不再发出第二个请求**，而是在进入 `detectGameStage()` 之前直接返回 `errorCode: 'UE_TRANSPORT_UNCERTAIN'` 的返回体（`uncertain: true` + 一段明确写着「不要直接重发，先对账」的 guidance）。同时给 fetch 加一个可注入的缝隙，使「第二个请求到底有没有被发出去」能被离线断言。

**文件：**
- 修改：`MCP/index.js:732` 之后 / `733` 之前（任务 1 的块末尾追加 `buildUncertainResponse`）
- 修改：`MCP/index.js:771`（唯一 fetch 入口换成 `ueFetchImpl`）
- 修改：`MCP/index.js:812-835`（catch 块加拦截分支 + 注释更正）
- 修改：`MCP/index.js:5442` 之后（`export` 清单加 `callUnityExplorerAPI`）
- 修改：`MCP/index.js:5453` 之后（`_testHooks` 加 `setFetch`）
- 测试：`MCP/test/replayGuard.test.js`（追加第 2 段）

- [ ] **步骤 1：给 fetch 加注入口（`MCP/index.js:771`）**

任务 1 已在 `:732` 之后声明了 `let ueFetchImpl = (url, fetchOpts) => fetch(url, fetchOpts);`。现在把 `:771` 这一行

```javascript
      return await fetch(url, fetchOpts);
```

替换为

```javascript
      return await ueFetchImpl(url, fetchOpts);
```

> `fetchWithTimeout`（`:766-779`）是 `callUnityExplorerAPI` 里**唯一**调用 `fetch` 的地方（`:798` 与 `:821` 都走它），所以**只改这一行**就覆盖了全部三条请求路径。生产行为逐字节不变（`ueFetchImpl` 默认就转发给全局 `fetch`），只有测试会把它换掉。

- [ ] **步骤 2：加测试钩子 `setFetch`（`MCP/index.js:5453` 之后）**

在 `MCP/index.js:5453`（`  setGabpBridge: (b) => { gabpBridge = b; },`）**之后**加一行：

```javascript
  // issue-triage [31]：让单测能替换 callUnityExplorerAPI 内部唯一的 fetch（index.js:771），
  // 从而在**完全离线、不启动游戏、不连任何端口**的前提下断言「第二个请求到底有没有被发出去」。
  // 传 null 恢复真实 fetch（测试的 afterEach 里必须调用）。
  setFetch: (fn) => { ueFetchImpl = fn || ((url, fetchOpts) => fetch(url, fetchOpts)); },
```

- [ ] **步骤 3：把 `callUnityExplorerAPI` 加进 export 清单（`MCP/index.js:5442` 之后，任务 1 那行下面）**

```javascript
  // issue-triage [31]：传输层重放拦截。必须导出整个函数（而不是把重试判定拆出去），
  // 因为本条要断言的是「有没有发出第二个请求」，那只能在调用点上看。
  callUnityExplorerAPI,
```

- [ ] **步骤 4：编写失败的测试（任务的第 2 段：端到端，离线）**

先把文件**顶部**那行 `import { test } from 'node:test';` 改成 `import { test, after } from 'node:test';`（下面这段要用 `after` 兜底还原 fetch）。

然后追加到 `MCP/test/replayGuard.test.js` **末尾**：

```javascript
// ===========================================================================
// ② 端到端：假 fetch 驱动 callUnityExplorerAPI，断言「第二个请求有没有被发出去」
//    全程零网络、零端口、零游戏状态；ports.json 会被读到但 fetch 已被替换，不产生任何流量。
// ===========================================================================

// 造一个「连接被对端 RST」的 fetch 失败错误。index.js:816 的正则同时含
// `fetch failed` 与 `ECONNRESET`，两者任一命中即可 —— 这里用 message='fetch failed'
// 并把真实成因放进 .cause，与 Node/undici 的实际形态一致。
function connResetError() {
  const e = new TypeError('fetch failed');
  e.cause = { code: 'ECONNRESET' };
  return e;
}

// 假 fetch：前 nCalls-1 次抛连接错误（模拟端口滚动/对端 RST），最后一次正常返回。
function fakeFetch(calls, { failTimes = Infinity, status = 200, payload = { success: true } } = {}) {
  return async (url, opts) => {
    calls.push({ url, method: opts.method, body: opts.body });
    if (calls.length <= failTimes) throw connResetError();
    return {
      ok: status >= 200 && status < 300,
      status,
      json: async () => payload,
    };
  };
}

// 假 fetch：永远不自己 resolve，只在 abort 信号到达时 reject —— 用来复现「超时」路径。
// fetchWithTimeout（:766-779）会在 abort 后把 e._aborted 挂上，正是 :817 判定重试的依据。
function hangingFetch(calls) {
  return (url, opts) => new Promise((_, reject) => {
    calls.push({ url, method: opts.method, body: opts.body });
    opts.signal.addEventListener('abort', () => reject(new Error('The operation was aborted.')));
  });
}

function withFetch(fn, impl) {
  mod._testHooks.setFetch(impl);
  try { return fn(); } finally { mod._testHooks.setFetch(null); }
}

after(() => mod._testHooks.setFetch(null));   // 兜底：即使某个用例抛异常也不留脏 fetch

test('T2 非幂等 POST 遇连接重置：只发 1 次，返回 UE_TRANSPORT_UNCERTAIN（板上 [31] 的止血点）', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/console/execute', 'POST', { code: 'while(true){}' }, { slow: true }),
    fakeFetch(calls, { failTimes: 5 }),
  );
  assert.equal(calls.length, 1, `必须只发出 1 个请求（重发 = 第二次执行）；实际 ${calls.length} 个`);
  assert.equal(r.success, false);
  assert.equal(r.errorCode, 'UE_TRANSPORT_UNCERTAIN');
  assert.equal(r.uncertain, true, 'uncertain 必须为真：调用方据此知道「游戏里可能已经执行过了」');
  assert.match(r.error, /可能已经在游戏里执行过/, '错误文案必须直说这个事实，不能只说失败');
});

test('T2 非幂等 DELETE 同样被拦（不是只拦 POST）', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/hook', 'DELETE', { hookId: 3 }),
    fakeFetch(calls, { failTimes: 5 }),
  );
  assert.equal(calls.length, 1, 'DELETE 也必须有副作用保护：删两遍的失效形态是「第二遍找不到 → 假失败」');
  assert.equal(r.errorCode, 'UE_TRANSPORT_UNCERTAIN');
});

test('T2 非幂等 POST 超时（abort）同样被拦，且绝不进入 detectGameStage', async () => {
  const calls = [];
  // 注意这里**不能**传 slow：index.js:743 是 `opts.slow ? slow : (opts.timeoutMs || normal)`，
  // slow 为真时 opts.timeoutMs 被整段吞掉（既有缺陷，见本计划 §7.1）。这里传 timeoutMs 走 normal 档。
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/console/execute', 'POST', { code: 'x' }, { timeoutMs: 60 }),
    hangingFetch(calls),
  );
  assert.equal(calls.length, 1, '超时也只发 1 次（旧代码 :817 把 _aborted 与连接错误同等对待）');
  assert.equal(r.errorCode, 'UE_TRANSPORT_UNCERTAIN');
  assert.notEqual(r.errorCode, 'UE_TIMEOUT', '非幂等超时要报 UE_TRANSPORT_UNCERTAIN，而不是笼统的超时');
});
});

test('T2 读端点（GET）遇连接错误**仍然**重发并成功 —— 不许为了止血打断端口滚动自愈', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/status', 'GET'),
    fakeFetch(calls, { failTimes: 1, payload: { success: true, data: { uiReady: true } } }),
  );
  assert.equal(calls.length, 2, 'GET 必须照旧重发一次（这是 :818-821 原本的自愈能力，不能一刀切掉）');
  assert.equal(r.success, true);
  assert.equal(r.data.uiReady, true);
});

test('T2 白名单里的幂等写端点仍然可以重发（别把 addusing 之类一起禁掉）', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/console/addusing', 'POST', { namespace: 'Verse' }),
    fakeFetch(calls, { failTimes: 1 }),
  );
  assert.equal(calls.length, 2, '白名单端点重发后仍是同一次语义，应当保留自动恢复');
  assert.equal(r.success, true);
});

test('T2 UE_TRANSPORT_UNCERTAIN 的 guidance 必须点名「不要直接重发」并指向真实存在的工具', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/hotreload/apply', 'POST', { modId: null }),
    fakeFetch(calls, { failTimes: 5 }),
  );
  assert.equal(r.errorCode, 'UE_TRANSPORT_UNCERTAIN');
  assert.match(r.guidance, /不要直接重发/, '板上 [31] 的次生损害就是调用方看到「没生效」再发一次 = 第三次执行');
  // 只许指向本仓库真实存在的工具名（index.js:1905 / :1970），不许现编
  assert.match(r.guidance, /read_rimworld_log/, 'read_rimworld_log 是真实工具名（index.js:1905）');
  assert.match(r.guidance, /get_unityexplorer_status/, 'get_unityexplorer_status 是真实工具名（index.js:1970）');
});
```

- [ ] **步骤 5：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**11 条里 6 条 FAIL（第 6-11 条）、5 条 PASS（第 1-5 条）**。逐条形态：

| # | 此刻的形态 | 说明 |
|---|---|---|
| 6、7、8 | **FAIL**：`calls.length === 2` 不成立（更可能是 `r.errorCode` 不是 `UE_TRANSPORT_UNCERTAIN`，而是 `GAME_NOT_RUNNING` / `UE_NOT_READY` 之类 —— 因为旧代码重发之后会走进 `detectGameStage()`） | 这正是要消灭的行为 |
| 9、11 | **PASS** | 「GET 仍重发」与「白名单仍重发」是**我们要守住的下界**，旧行为本来就对。它们先 PASS 才说明新测试没误伤自愈能力 |
| 10 | **FAIL** | `errorCode` / `guidance` 还不存在 |

> 若第 6-8 条报的是 `mod._testHooks.setFetch is not a function`，说明**步骤 2 漏了** —— 回到步骤 2。
> 若第 6 条卡住好几秒不返回，说明它走进了 `detectGameStage()`（会跑 `tasklist`），这同样是「拦截分支还没生效」的证据，不是测试写错了。

- [ ] **步骤 6：实现拦截（`MCP/index.js`）**

**6a. 在任务 1 的块末尾（`isReplaySafeRequest` 之后、`:733` 之前）追加返回体构造器：**

```javascript
// 传输不确定时的返回体。它必须带足信息，让调用方**不至于做出「失败 → 重发」的错误决定** ——
// 板上 [31] 的次生损害恰恰是：调用方看到「什么都没干」于是又发一次，那等于第三次执行。
// 文案里点名的两个工具都是本仓库真实存在的（index.js:1905 read_rimworld_log / :1970 get_unityexplorer_status）。
function buildUncertainResponse(endpoint, method, reason) {
  return {
    success: false,
    errorCode: 'UE_TRANSPORT_UNCERTAIN',
    error: `${method} ${endpoint} 在收到响应前传输中断（${reason}）。`
      + '该请求有副作用，已**放弃自动重发** —— 但它**可能已经在游戏里执行过了**。',
    statusCode: 0,
    uncertain: true,
    attempts: 1,
    replayed: false,
    guidance: '**不要直接重发。** 先对账再决定：'
      + '1) 用 read_rimworld_log（或直接看 Player.log）确认这段操作的执行痕迹出现了几次；'
      + '2) 已执行过一次的话，用只读工具核对当前世界状态（get_game_status / get_unityexplorer_status）；'
      + '3) 只有确认**没有**执行痕迹时，才重发一次。'
      + '若是游戏刚重启导致端口滚动，先用只读工具确认连通，再重发。'
  };
}
```

**6b. 在 `MCP/index.js:813-815`（catch 块的三行注释）之后、`:816` 之前，把注释与守卫一起改写：**

把 `:813-815`

```javascript
    // 连接类错误（fetch failed / ECONNREFUSED 等）或超时（UE_TIMEOUT）：都可能是游戏由外部
    // 启动后重写了 ports.json（随机端口），而本次请求用的是模块级 UE_BASE_URL 缓存的旧端口/fallback。
    // 统一强制从 ports.json 刷新端口后重试一次；重试成功则标记 reconnected。
```

替换为

```javascript
    // 连接类错误（fetch failed / ECONNRESET 等）或超时：都可能是游戏由外部启动后重写了
    // ports.json（随机端口），而本次请求用的是模块级缓存的旧端口/fallback ⇒ 值得刷新端口后重试一次。
    //
    // ⚠️ issue-triage [31]：**重试的前提是「重发一次没有副作用」**，旧的代码在这里完全没看方法与端点，
    // 对 POST/DELETE 一律重发。板上那次「destroying 2099 被回成 destroying 0」就是这么来的：
    // 第一遍已经改完世界、写响应时被对端 RST，重发的第二遍读的是改完以后的世界。
    // 超时路径更糟：UEMainThreadDispatcher.cs:86-87 超时只抛 TimeoutException，**动作仍在主线程队列里**
    // （:122 告警原文「仍在主线程执行，结果弃置」）⇒ 两次都会执行。
```

**6c. 在 `MCP/index.js:818`（`if (shouldRefreshPort) {`）**之前**插入拦截分支：**

```javascript
    // 拦截：有副作用的请求**不重发**。必须放在进入 detectGameStage()（:842）之前 ——
    // 阶段探测会跑 tasklist，既慢又会把这个「可能已执行过」的事实冲掉（stageError 的返回体里没有它）。
    if (shouldRefreshPort && !isReplaySafeRequest(endpoint, method, opts)) {
      const reason = isConnError ? String(error && error.message) : `UE 请求超时(${timeoutMs}ms)`;
      const uncertain = buildUncertainResponse(endpoint, method, reason);
      log('WARN', `callUnityExplorerAPI: ${method} ${endpoint} 传输不确定且请求非幂等，放弃自动重发（${uncertain.errorCode}）`);
      return uncertain;
    }
```

- [ ] **步骤 7：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**11 条全部 PASS**。重点确认三条：

- 第 6 条：`calls.length === 1` 且 `errorCode === 'UE_TRANSPORT_UNCERTAIN'`；
- 第 9 条（GET）：`calls.length === 2` 且 `success === true` —— **它 PASS 才说明拦截没有误伤端口滚动自愈**；
- 第 10 条：整个用例应在**几十毫秒**内返回（不再跑 `detectGameStage()`）。若它明显变慢，说明拦截分支插错位置了，回步骤 6c 对照 `:818`。

（第 6-8 条会往控制台打一行 `<WARN> callUnityExplorerAPI: …` —— 那是 `log()` 的既有行为，是刻意留的操作痕迹，不是测试噪声。）

- [ ] **步骤 8：跑全量测试确认没有引入新的红**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：与任务 1 步骤 7 的状态一致，只多出本任务 6 条 PASS。逐条确认 §0.5 的护栏：

| 断言 | 位置 | 为什么不该受影响 |
|---|---|---|
| `toolListLine` → `buildMenuNode` | `test/toolchainFixes.test.js:120-124` | 切片 3281→3290；本次改动在 732/771/813-821/5442/5453，**全在切片之前** |
| 快速测试链路切片 | `test/quickTest.test.js:28` | 依赖 1430→1675 两个标记，不碰 |
| `start_quick_test` 派发顺序 | `test/quickTest.test.js:41-48` | 不碰 `:4262` 与 `switch (name) {` |
| GABP 超时策略 17 例 | `test/timeoutPolicy.test.js` | 测 `longTask/timeoutPolicy.js` + `gabpClient.js`（GABP 通道），与 `callUnityExplorerAPI` 无交集 |

另外确认 `index.js` 仍能加载（不启动服务器、不连游戏）：
`node -e "process.env.UESD_MCP_NO_START='1'; import('./index.js').then(m=>console.log('OK', typeof m.callUnityExplorerAPI, typeof m._testHooks.setFetch))"`（在 `MCP` 目录下执行）。

- [ ] **步骤 9：改动清单（本项目不是 git 仓库，无提交动作）**

| 文件 | 动作 | 锚点 |
|---|---|---|
| `MCP/index.js` | 修改（+1 行 / -1 行） | `771`：`fetch` → `ueFetchImpl`（唯一 fetch 入口） |
| `MCP/index.js` | 修改（+11 行） | 任务 1 的块末尾追加 `buildUncertainResponse()` |
| `MCP/index.js` | 修改（+4 行 / -3 行） | `813-815` 注释改写 |
| `MCP/index.js` | 修改（+8 行） | `818` 之前插入拦截分支 |
| `MCP/index.js` | 修改（+4 行） | `5442` 之后 export `callUnityExplorerAPI` |
| `MCP/index.js` | 修改（+3 行） | `5453` 之后 `_testHooks.setFetch` |
| `MCP/test/replayGuard.test.js` | 修改（追加） | 第 2 段：6 个测试（合计 11） |

**零 `.cs` 改动、零重编译、零游戏动作。**

---

## 任务 3：把「发过几次」做成**无条件**信号（板上建议 ②）

**交付物：** `callUnityExplorerAPI` 的**每一条**返回路径都带 `attempts` 与由它派生的 `replayed`；`:829` 那个只在重试成功时才出现、且语义混叠的 `reconnected` 退役；`map_marker` 的纯文本失败路径把 `uncertain` 显式写进文本（否则字段会被拍平吃掉）；`execute_csharp_code` 的 `TOOL_USAGE_NOTES` 点名这三个新概念，让调用方在 `mcp_help` 那一页就能查到。

**文件：**
- 修改：`MCP/index.js:786` 之前（`let attempts = 1;`）
- 修改：`MCP/index.js:798` 之前 / `:800`、`:802`、`:805-811`、`:821` 之前 / `:829`、`:838`、`:844`、`:847`、`:849`、`:851-855`
- 修改：`MCP/index.js:2822-2832`（`mapMarkerResponse`）
- 修改：`MCP/index.js:2937`（`TOOL_USAGE_NOTES` 里**已有**的 `execute_csharp_code` 那一条 —— 是合并，不是新增键）
- 测试：`MCP/test/replayGuard.test.js`（追加第 3 段）

> ⚠️ **不要新增第二个 `'execute_csharp_code'` 键。** `TOOL_USAGE_NOTES` 是普通对象字面量，同名键后者静默覆盖前者 —— 那样 :2937 那条 CS0103 的说明会凭空消失。本任务**就地扩写 :2937 那一条**。

- [ ] **步骤 1：加 `attempts` 计数器（`MCP/index.js:786` 之前）**

在 `MCP/index.js:785`（空行）与 `:786`（`  try {`）**之间**插入：

```javascript
  // issue-triage [31]：本次调用实际发出了几个 HTTP 请求。**每一条**返回路径都带上 attempts 与
  // 由它派生的 replayed —— 此前唯一的痕迹 reconnected（:829）只在「重试成功」时出现，
  // 重试也失败时整个字段消失，「代码已经跑过一次」这个事实 100% 丢失。
  let attempts = 1;
```

- [ ] **步骤 2：给 401 刷新 token 的重发路径补字段（`:794-802`）**

把 `MCP/index.js:796-802` 这 7 行

```javascript
      if (t2) {
        options.headers['Authorization'] = `Bearer ${t2}`;
        const retryResp = await fetchWithTimeout(`${getUeBaseUrl(true)}${endpoint}`, options);
        const retryData = await readJson(retryResp);
        return { success: retryResp.ok && retryData.success, data: retryData.data || retryData, error: retryData.error, errorCode: retryData.errorCode, statusCode: retryResp.status };
      }
      return { success: false, errorCode: 'UNAUTHORIZED', error: '游戏侧鉴权被拒（token 失效）', statusCode: 401, guidance: '请重启游戏刷新 token' };
```

替换为

```javascript
      if (t2) {
        options.headers['Authorization'] = `Bearer ${t2}`;
        // 401 ⇒ UEHttpServer.cs:730-736 的 PostGuard 在鉴权失败时**先返回、根本不调 handler**
        // ⇒ 第一次没有任何副作用，这次重发是安全且必要的自愈（所以它不受 isReplaySafeRequest 约束）。
        attempts = 2;
        const retryResp = await fetchWithTimeout(`${getUeBaseUrl(true)}${endpoint}`, options);
        const retryData = await readJson(retryResp);
        return { success: retryResp.ok && retryData.success, data: retryData.data || retryData, error: retryData.error, errorCode: retryData.errorCode, statusCode: retryResp.status, attempts, replayed: true };
      }
      return { success: false, errorCode: 'UNAUTHORIZED', error: '游戏侧鉴权被拒（token 失效）', statusCode: 401, attempts, replayed: false, guidance: '请重启游戏刷新 token' };
```

- [ ] **步骤 3：给正常成功路径补字段（`:805-811`）**

把

```javascript
    return {
      success: response.ok && data.success,
      data: data.data || data,
      error: data.error,
      errorCode: data.errorCode,
      statusCode: response.status
    };
```

替换为

```javascript
    return {
      success: response.ok && data.success,
      data: data.data || data,
      error: data.error,
      errorCode: data.errorCode,
      statusCode: response.status,
      attempts,
      replayed: false
    };
```

- [ ] **步骤 4：给连接层重试路径补字段并退役 `reconnected`（`:819-830`）**

在 `MCP/index.js:821`（`        const retryResp = await fetchWithTimeout(retryUrl, options);`）**之前**插入一行：

```javascript
        attempts = 2;   // 第二个请求即将发出（此时已过 isReplaySafeRequest 的放行闸门）
```

再把 `:823-830` 的返回体

```javascript
        return {
          success: retryResp.ok && retryData.success,
          data: retryData.data || retryData,
          error: retryData.error,
          errorCode: retryData.errorCode,
          statusCode: retryResp.status,
          reconnected: true
        };
```

替换为

```javascript
        return {
          success: retryResp.ok && retryData.success,
          data: retryData.data || retryData,
          error: retryData.error,
          errorCode: retryData.errorCode,
          statusCode: retryResp.status,
          attempts,
          replayed: true
          // 退役：reconnected。它同时表示「刷新了 ports.json」和「重发了一次请求」，调用方分不出；
          // 且只在重试成功时出现。文档侧对它的说明在整个 docs/ 树里零处（详见本计划 §1.6）。
        };
```

- [ ] **步骤 5：给错误路径补字段（`:838` / `:844` / `:847` / `:849` / `:851-855`）**

把 `:838`

```javascript
      return { success: false, errorCode: 'UE_TIMEOUT', error: `UE 请求超时(${timeoutMs}ms)`, statusCode: 0 };
```

替换为

```javascript
      return { success: false, errorCode: 'UE_TIMEOUT', error: `UE 请求超时(${timeoutMs}ms)`, statusCode: 0, attempts, replayed: attempts > 1 };
```

把 `:844`

```javascript
        return stageError(ERROR_CODES.GAME_NOT_RUNNING, detected.stage, [STAGES.IN_GAME]);
```

替换为

```javascript
        return { ...stageError(ERROR_CODES.GAME_NOT_RUNNING, detected.stage, [STAGES.IN_GAME]), attempts, replayed: attempts > 1 };
```

把 `:847`

```javascript
        return stageError(ERROR_CODES.MAP_NOT_LOADED, detected.stage, [STAGES.IN_GAME]);
```

替换为

```javascript
        return { ...stageError(ERROR_CODES.MAP_NOT_LOADED, detected.stage, [STAGES.IN_GAME]), attempts, replayed: attempts > 1 };
```

把 `:849`

```javascript
      return stageError(ERROR_CODES.UE_NOT_READY, detected.stage, [STAGES.IN_GAME], `游戏内服务未响应：${error.message}`);
```

替换为

```javascript
      return { ...stageError(ERROR_CODES.UE_NOT_READY, detected.stage, [STAGES.IN_GAME], `游戏内服务未响应：${error.message}`), attempts, replayed: attempts > 1 };
```

把 `:851-855`

```javascript
      return {
        success: false,
        error: error.message,
        errorCode: 'HTTP_ERROR'
      };
```

替换为

```javascript
      return {
        success: false,
        error: error.message,
        errorCode: 'HTTP_ERROR',
        attempts,
        replayed: attempts > 1
      };
```

- [ ] **步骤 6：补测试（任务的第 3 段）**

追加到 `MCP/test/replayGuard.test.js` **末尾**：

```javascript
// ===========================================================================
// ③ 信号无条件化 + 文案（板上建议 ②）
// ===========================================================================

test('T3 成功路径也带 attempts / replayed（不是只有出错时才带）', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/status', 'GET'),
    fakeFetch(calls, { failTimes: 0, payload: { success: true, data: { uiReady: true } } }),
  );
  assert.equal(calls.length, 1);
  assert.equal(r.attempts, 1, '正常路径必须显式带 attempts:1，而不是缺字段（缺字段 = 0 值混淆）');
  assert.equal(r.replayed, false);
});

test('T3 连接层重试成功：attempts:2 / replayed:true', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/status', 'GET'),
    fakeFetch(calls, { failTimes: 1, payload: { success: true, data: { uiReady: true } } }),
  );
  assert.equal(calls.length, 2);
  assert.equal(r.attempts, 2);
  assert.equal(r.replayed, true, 'replayed 必须由 attempts 派生，不再是那个语义混叠的 reconnected');
});

test('T3 401 刷新 token 的重发也算 attempts:2（这是安全的自愈，不是重放执行）', async () => {
  const calls = [];
  const r = await withFetch(
    () => mod.callUnityExplorerAPI('/unityexplorer/log/clear', 'POST'),
    async (url, opts) => {
      calls.push({ url, method: opts.method });
      if (calls.length === 1) return { ok: false, status: 401, json: async () => ({ success: false, errorCode: 'UNAUTHORIZED' }) };
      return { ok: true, status: 200, json: async () => ({ success: true, data: { cleared: 1 } }) };
    },
  );
  assert.equal(calls.length, 2);
  assert.equal(r.success, true);
  assert.equal(r.attempts, 2);
  assert.equal(r.replayed, true);
});

test('T3 源码：reconnected 已彻底退役，重发闸门与计数都在正确的位置', () => {
  assert.ok(!source.includes('reconnected'),
    'reconnected 字段必须从 index.js 里彻底消失（它在 docs/ 树里零说明，且只在重试成功时出现）');

  const fnStart = source.indexOf('async function callUnityExplorerAPI(');
  const fnEnd = source.indexOf('// P2-MCP-8：tasklist CSV 行解析');
  assert.ok(fnStart > 0 && fnEnd > fnStart, '取不到 callUnityExplorerAPI 的源码区间，先复核锚点');
  const fn = source.slice(fnStart, fnEnd);

  // 闸门：重发之前必须过 isReplaySafeRequest，且排在原重试分支之前
  assert.match(fn, /if \(shouldRefreshPort && !isReplaySafeRequest\(endpoint, method, opts\)\)/,
    '拦截闸门必须接在 endpoint/method/opts 上，并排在 if (shouldRefreshPort) 重试块之前');
  // 拦截必须早于 detectGameStage —— 阶段探测会跑 tasklist，且会把「可能已执行过」这个事实冲掉
  assert.ok(fn.indexOf('UE_TRANSPORT_UNCERTAIN') < fn.indexOf('await detectGameStage()'),
    'UE_TRANSPORT_UNCERTAIN 的返回必须排在 detectGameStage() 之前');

  // 两个重发点各��自增一次：401 刷新 token 的重发 与 连接层的重发
  assert.equal((fn.match(/attempts = 2;/g) || []).length, 2,
    '应有且仅有两处 `attempts = 2;`：401 刷新重发 与 连接层重发');

  // 三种取值都要出现：显式 true / 显式 false / 由 attempts 派生
  assert.match(fn, /replayed: true/);
  assert.match(fn, /replayed: false/);
  assert.match(fn, /replayed: attempts > 1/);
});

test('T3 mcp_help tool=execute_csharp_code 能查到 attempts / replayed / UE_TRANSPORT_UNCERTAIN 的含义', async () => {
  const text = await mod.mcpHelpResult({ tool: 'execute_csharp_code' })
    .then((r) => (r.content || []).map((c) => c.text || '').join(''));
  assert.match(text, /attempts/, 'attempts 必须在调用方必然看得到的那一页上');
  assert.match(text, /replayed/);
  assert.match(text, /UE_TRANSPORT_UNCERTAIN/);
  assert.match(text, /不要直接重发/, '必须点名「不要直接重发」——这是板上 [31] 的次生损害');
  // :2937 原有那条 CS0103 说明不许被同名键覆盖掉（对象字面量里后者会静默吃掉前者）
  assert.match(text, /CS0103/, '既有 CS0103 说明必须仍在（不许新增同名键把它覆盖掉）');
});

test('T3 post_map_marker 的失败路径把「可能已执行过」写进文本（顶层字段会被拍平吃掉）', async () => {
  const calls = [];
  const res = await withFetch(
    () => mod.handleToolCall('post_map_marker', { x: 12, z: 34, color: 'red' }),
    fakeFetch(calls, { failTimes: 5 }),
  );
  assert.equal(calls.length, 1, '地图光标同样只发一次');
  const text = (res.content || []).map((c) => c.text || '').join('');
  assert.match(text, /UE_TRANSPORT_UNCERTAIN/, 'mapMarkerResponse 把返回体拍平成纯文本，errorCode 必须出现在文本里');
  assert.match(text, /可能已经在游戏里执行过|游戏里可能已经执行过/, 'uncertain 的事实必须在文本里说出来，否则这里仍然是静默的');
  assert.match(text, /attempts/, 'attempts 也会被拍平吃掉，必须显式写进文本');
});
```

- [ ] **步骤 7：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**17 条里 6 条 FAIL（第 12-17 条）、11 条 PASS**。逐条：

| # | 此刻 | 为什么 |
|---|---|---|
| 12 成功路径带字段 | FAIL | `r.attempts` 是 `undefined`（步骤 3 还没做） |
| 13 重试成功带字段 | FAIL | 同上（步骤 4 还没做）；且此刻字段叫 `reconnected` |
| 14 的断言 `r.replayed === true` | FAIL | 同上 |
| 15 源码退役 | FAIL | `source.includes('reconnected')` 为 `true` |
| 16 `mcp_help` 文案 | FAIL | notes 里没有 `attempts` / `UE_TRANSPORT_UNCERTAIN` |
| 17 `post_map_marker` 文本 | FAIL | 文本里没有 `attempts`（`uncertain` 的那半句来自任务 2 的 `buildUncertainResponse`，**预计这半句会命中**） |

> 第 17 条若连 `errorCode` / 「可能已执行过」都取不到，说明拦截分支没生效（回到任务 2 步骤 6c）；若只缺 `attempts`，那是步骤 9 还没做。**按报错内容判断，不要盲改断言。**

- [ ] **步骤 8：扩写 `TOOL_USAGE_NOTES` 里已有的 `execute_csharp_code` 一条（`MCP/index.js:2937`）**

把 `MCP/index.js:2937` 这一行

```javascript
  execute_csharp_code: { example: '{ "code": "Verse.GenTicks.TicksGame" }', notes: '控制台默认无 using 引用，裸类型名（如 GenTicks）会报 CS0103；须用全限定名或先 add_using_directive（兼容 script/expression 别名）' },
```

替换为（**就地扩写，不要新增同名键**）

```javascript
  execute_csharp_code: { example: '{ "code": "Verse.GenTicks.TicksGame" }', notes: '控制台默认无 using 引用，裸类型名（如 GenTicks）会报 CS0103；须用全限定名或先 add_using_directive（兼容 script/expression 别名）。'
    + '【重放防护】返回体恒带两个字段：attempts = 本次实际发出的 HTTP 请求数，replayed = 是否发生过重发。'
    + '注意 replayed=true 只说明「发过两次请求」，**不代表代码跑了两次**——token 失效后的 401 重发也计入（那种情况下服务端根本没执行 handler，是安全的）。'
    + '真正要警惕的是 errorCode="UE_TRANSPORT_UNCERTAIN"（此时 uncertain=true、replayed=false）：'
    + '传输中断且本次请求有副作用，MCP 已**放弃自动重发**，但它**可能已经在游戏里执行过了**——'
    + '不要直接重发，先 read_rimworld_log 对账执行痕迹，再决定是否重发。' },
```

> 这条 note 会经 `mcpHelpResult` 的 `...(TOOL_USAGE_NOTES[meta.name] || {})` 展开到 `mcp_help tool=execute_csharp_code` 的返回体里 —— 是调用方必然会看的那一页。此前 `reconnected` 在整个 `docs/` 树里零说明（§1.6），就是「信号存在但没人知道」。

- [ ] **步骤 9：让 `mapMarkerResponse` 的失败路径带出 `uncertain`（`MCP/index.js:2822-2832`）**

把 `MCP/index.js:2822-2832`

```javascript
function mapMarkerResponse(r) {
  if (!r || r.success !== true) {
    const code = r && r.errorCode ? `（${r.errorCode}）` : '';
    const extra = r && r.guidance ? `\n${r.guidance}` : '';
    return {
      content: [{ type: 'text', text: `❌ 坐标光标操作失败${code}: ${(r && r.error) || '未知错误'}${extra}` }],
      isError: true
    };
  }
  return { content: [{ type: 'text', text: JSON.stringify(r.data, null, 2) }] };
}
```

替换为

```javascript
function mapMarkerResponse(r) {
  if (!r || r.success !== true) {
    const code = r && r.errorCode ? `（${r.errorCode}）` : '';
    const extra = r && r.guidance ? `\n${r.guidance}` : '';
    // issue-triage [31]：这条路径把整个返回体拍平成一行纯文本，顶层字段（attempts / replayed / uncertain）
    // 会被**整段吃掉** ⇒ 传输不确定这件事必须在文本里重新说一遍，否则地图光标工具上仍然是静默重放。
    const uncertain = r && r.uncertain === true
      ? `\n⚠️ 本次调用已放弃自动重发（attempts=${r.attempts == null ? 1 : r.attempts}, replayed=${r.replayed === true}），但游戏里可能已经执行过了；不要直接重发，先对账。`
      : '';
    return {
      content: [{ type: 'text', text: `❌ 坐标光标操作失败${code}: ${(r && r.error) || '未知错误'}${extra}${uncertain}` }],
      isError: true
    };
  }
  return { content: [{ type: 'text', text: JSON.stringify(r.data, null, 2) }] };
}
```

- [ ] **步骤 10：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**17 条全部 PASS**。

- [ ] **步骤 11：跑全量测试确认没有引入新的红**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：与任务 2 步骤 8 的状态一致，只多出本任务 6 条 PASS。**本任务碰到的既有测试面最广，逐条确认**：

| 断言 | 位置 | 为什么不该受影响 |
|---|---|---|
| `mcp_help tool=post_map_marker` 断言 `/点击/`、`/_Color/` | `test/mapMarker.test.js:47-57` | `mapMarkerResponse` **只在失败分支**加了文本，成功分支与 `mcp_help` 无关 |
| `post_map_marker` 未知 action / 缺 x/z | `test/mapMarker.test.js:83-93` | 那两条是 `handleToolCall` 里的**早返回**（`:5014` / `:5021`），根本不经过 `mapMarkerResponse` |
| `toolListLine` → `buildMenuNode` | `test/toolchainFixes.test.js:120-124` | 切片 3281→3290；本次改动在 786/796-856/2822-2837/2937，**全在切片之前** |
| `start_game` / `stop_game` | `test/toolchainFixes.test.js:263-268` | 依赖 4371-4382，不碰 |
| 工具计数一致性 | `test/toolCountConsistency.test.js` | 本任务**不新增/删除任何工具**，只加返回体字段 |

- [ ] **步骤 12：改动清单（本项目不是 git 仓库，无提交动作）**

| 文件 | 动作 | 锚点 |
|---|---|---|
| `MCP/index.js` | 修改（+4 行） | `786` 之前：`let attempts = 1;` |
| `MCP/index.js` | 修改（+2 行 / -0 行） | `796-802`：401 重发路径的 `attempts = 2` 与返回体补字段 |
| `MCP/index.js` | 修改（+2 行） | `805-811` 正常返回补字段 |
| `MCP/index.js` | 修改（+5 行 / -1 行） | `821` 之前 `attempts = 2;`；`829` `reconnected` → `attempts/replayed` |
| `MCP/index.js` | 修改（+5 行） | `838` / `844` / `847` / `849` / `851-855` 五条错误路径补字段 |
| `MCP/index.js` | 修改（+4 行 / -1 行） | `2822-2832` `mapMarkerResponse` 失败路径带出 `uncertain` |
| `MCP/index.js` | 修改（+5 行 / -1 行） | `2937` `TOOL_USAGE_NOTES` 就地扩写 |
| `MCP/test/replayGuard.test.js` | 修改（追加） | 第 3 段：6 个测试（合计 17） |

**零 `.cs` 改动、零重编译、零游戏动作、零工具增删。**

---

## 任务 4：慢档超时预算对齐（修「重放是结构必然」那一半）

**交付物：** `MCP/config.json` 里显式登记 `timings.ueSlowRequestTimeoutMs = 30000`，使 MCP 慢档（30s）**大于**游戏侧主线程预算（`Source/UELoader/UEHttpHandler.cs:201` 的 20000ms）。这样「MCP 先放弃 → `abort()` → 命中 `:817` → 重放」这条**必然发生**的路径不再被触发。**代码默认值 `10000` 刻意不动**（配置缺失的机器行为与今天逐字节相同）。

**文件：**
- 修改：`MCP/config.json`（新增一个 `timings` 键）
- 测试：`MCP/test/replayGuard.test.js`（追加第 4 段）

> **为什么是 30000 而不是 20000：** 必须**严格大于**游戏侧预算，否则两端同时到点、谁先返回不确定。30000 留 10s 余量；真正的执行耗时上限由游戏侧的 20000ms 兜底（超时后动作仍在主线程跑完、结果被弃置，见 `UEMainThreadDispatcher.cs:122`），所以 30000 不会让 MCP 比游戏侧多等出第二份执行。
> **为什么改配置而不是改代码：** `index.js:743` 是 `opts.slow ? timeouts.slow : (opts.timeoutMs || timeouts.normal)` —— `slow:true` 时 `opts.timeoutMs` 被整段吞掉（§1.4），照裁决报告原样加参数会得到一个**完全不起作用**的 `timeoutMs`。而 `:741` 本来就在读 `config.timings.ueSlowRequestTimeoutMs`，只是 `config.json` 里没这个键。

- [ ] **步骤 1：编写失败的测试（任务的第 4 段）**

追加到 `MCP/test/replayGuard.test.js` **末尾**：

```javascript
// ===========================================================================
// ④ 超时预算对齐：MCP 慢档必须 ≥ 游戏侧主线程预算
//    否则「MCP 先放弃 → abort → 命中 :817 → 重发」是**必然**而不是偶发（板上 [31] 的结构前提）。
// ===========================================================================

test('T4 config.json 的 timings.ueSlowRequestTimeoutMs ≥ 20000（游戏侧 UEHttpHandler.cs:201 的主线程预算）', async () => {
  const cfg = JSON.parse(fs.readFileSync(path.join(here, '..', 'config.json'), 'utf-8'));
  assert.equal(typeof cfg.timings, 'object', 'timings 必须存在（没有它 :741 会回落到 10s 硬编码默认值）');
  const v = cfg.timings.ueSlowRequestTimeoutMs;
  assert.ok(Number.isFinite(v) && v > 0, `ueSlowRequestTimeoutMs 应为正数，实际 ${v}`);
  assert.ok(v >= 20000,
    `慢档 ${v}ms 必须 ≥ 游戏侧主线程预算 20000ms（UEHttpHandler.cs:201）；`
    + '否则 MCP 会先于游戏放弃 → abort → 命中 :817 的重发分支（本条要消灭的正是这个）');
  // 这一条还会顺手修掉 test/index.integration.test.js:235 的既有断言
  //（它要求 typeof cfg.timings === 'object'，而当前 config.json 快照里没有这个键）。
});

test('T4 慢档仍从 config.timings 读，代码默认值刻意保持 10000 不动', () => {
  assert.match(source, /slow:\s*config\.timings\?\.ueSlowRequestTimeoutMs \?\? 10000/,
    'index.js:741 必须仍在读 config.timings.ueSlowRequestTimeoutMs；'
    + '且默认值保持 10000 —— 本计划只改配置，不改代码默认，避免影响没有 timings 的机器');
});
```

- [ ] **步骤 2：运行测试验证失败**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**19 条里 1 条 FAIL（第 19 条 `T4 config.json …`）、18 条 PASS**。第 19 条报 `timings 必须存在（…）`，因为当前 `config.json` 里没有这个键。第 18 条（源码断言）**预期 PASS** —— 它钉的是「代码没被动过」。

> 若第 18 条也 FAIL，说明有人已经改过 `:741` 的默认值。**停下来先查是谁改的**，不要顺手把代码默认值也改成 30000 —— 那会让所有没配 `timings` 的机器行为突变，且与本计划「只改配置」的设计相悖。

- [ ] **步骤 3：在 `MCP/config.json` 登记 `timings`**

把 `MCP/config.json` 全文（当前是**单行**）替换为：

```json
{"gamePath":"C:/SteamLibrary/steamapps/common/RimWorld/RimWorldWin64.exe","logPath":"C:/Users/Timer_0/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios/Player.log","steamPath":"d:/steam/steam.exe","appId":"294100","startTimeout":180000,"timings":{"ueSlowRequestTimeoutMs":30000}}
```

> **只增不改。** 其余五个键逐字保持原样。
> **为什么手写是安全的：** `Source/UELoader/AutoDeploy.cs:263-271` 的 `WriteConfig` 是「先读现有文件再逐键覆盖」，`Source/UELoader/UELoaderSettings.cs:197-198` 的设置界面写回同样「保留未知键」⇒ **这个 `timings` 既不会被游戏抹掉，也不会被自动生成**（`WriteConfig` 只写 `gamePath/logPath/steamPath/appId/startTimeout`，见 `:281-295`）。必须手写。
> 若你本地的 `config.json` 与上面这行不同（游戏可能刚写过），**只追加 `,"timings":{"ueSlowRequestTimeoutMs":30000}`，其余逐字不动**。

- [ ] **步骤 4：运行测试验证通过**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
node --test test/replayGuard.test.js
```

预期：**19 条全部 PASS**。

- [ ] **步骤 5：跑全量测试确认没有引入新的红，并核对那条既有断言**

运行：

```
Set-Location C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\MCP
npm test
```

预期：与任务 3 步骤 11 的状态一致，只多出本任务 2 条 PASS，并**额外**把 `MCP/test/index.integration.test.js` 里那条 `config.json 的结构是对象` 用例（`:222-256`，核心断言在 `:235`）从红变绿（若它在你的基线里就是红的）。**若它在你的基线里本来就是绿的**（说明你本地的 `config.json` 已有 `timings`），那本步骤不会有任何变化，照旧只多 2 条 PASS。

- [ ] **步骤 6：改动清单（本项目不是 git 仓库，无提交动作）**

| 文件 | 动作 | 锚点 |
|---|---|---|
| `MCP/config.json` | 修改（+1 键） | 追加 `"timings":{"ueSlowRequestTimeoutMs":30000}`，其余五键逐字不动 |
| `MCP/test/replayGuard.test.js` | 修改（追加） | 第 4 段：2 个测试（合计 19） |

**零 `.cs` 改动、零重编译、零游戏动作。`MCP/index.js` 在本任务里一个字都没改。**

---

## 4. 真机人工验收（**可选，且必须由维护者本人在并行调试会话结束后自己做**）

> ⚠️ 本计划的 19 个测试全部是**离线静态验证**（假 fetch + 纯函数）。它们证明的是「MCP 层的重放判定与信号正确」，**不是**「真实游戏里没有别的路径会重放」。那一条只有连上真游戏才验得了。
> ⚠️ **本计划作者与执行者都不得在并行调试会话期间调用任何会动游戏状态的 MCP 工具**（`execute_csharp_code` / `start_game` / `execute_debug_action` / `post_map_marker` / `create_method_hook` / `set_breakpoint` …）。

全部落完且并行会话结束后，维护者依次跑（前三步**只读**，第四步会真的改游戏状态，届时确保没人在调试）：

1. `get_unityexplorer_status` → 应正常返回，且返回体里能看到 `attempts: 1` / `replayed: false`。
2. `mcp_help {tool: "execute_csharp_code"}` → 应能在 notes 里读到 `attempts` / `replayed` / `UE_TRANSPORT_UNCERTAIN` / 「不要直接重发」四样。
3. `post_map_marker {action: "list"}` → 成功路径不受影响（返回体是 `r.data` 的 JSON，不含 attempts，属预期）。
4. `execute_csharp_code {code: "1 + 1;"}` → 正常返回，`attempts: 1`。**再发一次 `read_rimworld_log`** 确认 `Player.log` 里这条只有**一组**哨兵（板上 [31] 与 [20] 的判别方法见裁决报告 §2：一组哨兵出现两次且无 `[UnityExplorer]` 前缀差异 ⇒ [31]；同一条日志以 `[UnityExplorer] [Unity] X` + `X` 两种形态各一份 ⇒ [20]，那是计划 02 的范围）。
5. **压测那条必然路径（可选，最有价值的验收）**：发一段执行时间明显超过 10 秒的 `execute_csharp_code`。落地后它**不再触发重发**；预期拿到的是一次正常响应（若游戏侧超过 20s，则拿到一次 `UE_TIMEOUT`，但 **`attempts` 必须是 1**）。这一条是本计划的核心主张，改完后一定要亲自验一次。

**验收失败的判读**：若第 5 步出现 `attempts: 2`，说明还有第三条重发路径没被本计划覆盖 —— **不要放宽判定**，回去查是哪条路径绕过了 `isReplaySafeRequest`。

---

## 5. 自检（writing-plans 的三项）

**① 规格覆盖度。** 上游裁决报告 §6 列了 6 档修法 + 1 条「明确不做」，逐条对账：

| 裁决报告 §6 的修法 | 覆盖情况 |
|---|---|
| 1. 🔴 主修：给非幂等请求「不自动重试」的开关，返回 `UE_TRANSPORT_UNCERTAIN` + 对账 guidance | ✅ **任务 1 + 任务 2**。判定做成纯函数 `isReplaySafeRequest` + 默认值反转的白名单（比「逐个置 `false`」更抗漏） |
| 2. 🟠 补信号：`attempts` / `replayed` 出现在**每一条**返回路径，取代 `reconnected` | ✅ **任务 3**（含 `:838`/`:844`/`:847`/`:849`/`:851-855` 五条错误路径，一条不漏） |
| 2 的顺带：在 `TOOL_USAGE_NOTES` 点名 | ✅ 任务 3 步骤 8（就地扩写 `:2937`，并有一条测试钉住「CS0103 说明不许被同名键覆盖掉」） |
| 3. 🟠 计划 02 任务 3 的前置断言 | ✅ **§6 登记**（本计划**不改**计划 02 的任何文件；那是另一个子代理的产物） |
| 4. 🟠 结构性修：超时预算对齐 | ✅ **任务 4**。但走 `config.json` 而不是裁决报告写的 `opts.timeoutMs` —— 后者在 `:743` 的 `slow:true` 分支里**不起作用**（§1.4） |
| 5. 🟡 协议级正解（`requestId` + 服务端去重缓存） | ❌ **不做**，登记在 §7.2。裁决报告自己就说「建议作为后续阶段，不与 1/2/4 打包」，本计划照办 |
| 6. 📌 建议 ③（验收必须回 `Player.log` 看第一遍）+ 与 [09] 的配合 | ✅ §4 步骤 4/5 的验收纪律里写了「一组哨兵出现两次 ⇒ [31]」；[09] 的 `Player.log` 轮转风险在 §4 抬头处提示 |
| 7. 🚫 明确不做（三条） | ✅ 全部照办，见 §2.2 |

吐槽板条目 [31] 的三条建议逐条对账：①「别无条件重试 / 重试前先说一句」→ 任务 1+2（并**如实记录了「重试前先说一句」物理不可实现**，§1.1）；②「标注被执行了 N 次」→ 任务 3；③「文档写死回 `Player.log` 对账的纪律」→ §4。**无遗漏项。**

**⚠️ 一处与裁决报告的实质分歧（已在 §1.3 说明）：** 裁决报告 §1.7 列了 12 个写端点，本计划复核出的是 **15 条路径 / 14 个调用点**（漏了 `/area/delete` 与 `/area/clear`，两条都经 `:2719` ← `:4271`）。**对本计划的实现没有影响**（方案按方法默认不重放，覆盖是自动的），但事实必须更正，否则日后按那份清单做审计会漏两条。

**② 占位符扫描。** 全文无「待定」「TODO」「后续实现」「类似任务 N」；每个代码步骤都带可粘贴的完整代码块；所有被引用的标识符都在 §0 表里给出**已复核的行号**：`callUnityExplorerAPI`(736)、`fetchWithTimeout`(766)、`isReplaySafeRequest`/`normalizeUeEndpoint`/`UE_REPLAY_SAFE_ENDPOINTS`/`ueFetchImpl`(任务 1 定义于 732 之后)、`buildUncertainResponse`(任务 2 定义于任务 1 块末尾)、`stageError`(393)、`ERROR_CODES`(356-366)、`mapMarkerResponse`(2822)、`TOOL_USAGE_NOTES`(2930/2983)、`_testHooks`(5450-5459)、`log()`(283)、`mcpHelpResult`(用于 §4 与任务 3 测试)、`read_rimworld_log`(1905)、`get_unityexplorer_status`(1970)。**不存在「引用了未定义的东西」。**

**③ 类型/命名一致性。** `isReplaySafeRequest(endpoint, method, opts)` 的三参签名在**定义**（任务 1）、**调用**（`:818` 守卫 + 测试）与**导出**（`:5442` 之后）三处一致；`buildUncertainResponse(endpoint, method, reason)` 在定义与调用处一致，返回体字段 `uncertain` / `attempts` / `replayed` 在 `buildUncertainResponse`、`mapMarkerResponse`（读 `r.uncertain`/`r.attempts`/`r.replayed`）与测试断言三处一致；`UE_REPLAY_SAFE_ENDPOINTS` 的 6 个条目与任务 1 测试里的 6 个白名单断言**逐条对应**（测试用「源码切片数条目 = 6」反向钉死，防止表悄悄变长）。**无 drift。**

---

## 6. 与计划 02 的关系（**实施顺序是依赖，不是并列**）

裁决报告 §7.1 第 4 条已裁定：**本计划必须先于 `plans/2026-10-01-02-execute-csharp-code-output-capture.md` 落地**。理由复述并落到可执行层面：

| 关系 | 内容 |
|---|---|
| **顺序** | **本计划先落地**，再实施计划 02 的任务 3（游戏内验收矩阵）。计划 02 的 `:996-1013` 已经写了这个前置闸门（「步骤 4：前置断言 —— 先确认【31】的静默重放已修好」），两边的说法一致 |
| **正交，不互相覆盖** | [31] = **同一次调用被执行两次**（发生在 MCP 传输层，第二次执行有它自己独立的一份 `captured`）；[20] = **同一份内容被回显两次**（发生在游戏侧捕获管线内部）。修好任何一个都不会修好另一个 |
| **计划 02 的离线断言仍然有效** | 计划 02 任务 1/2/5/6 的断言全是对 `UEExecLogCapture` 的**纯函数**测试（`dotnet run --project Source/ExecLogCaptureTests`），不经过 HTTP、不经过 `callUnityExplorerAPI` ⇒ **重复执行不产生额外失败，结论不受本计划影响** |
| **计划 02 的实现仍然正确** | `UEExecLogCapture` 解析的是**某一次** `ExecuteCode` 的 `captured`；第二次执行有它自己独立的一份，解析照样正确，只是那份 `captured` 描述的是**第二遍之后的世界**。`output`/`result`/`logCapture` 的字段语义一条都不错 |

### 6.1 ⚠️ 本计划落地**不能**消除的反向假阳性（写给计划 02 的验收者）

**问题：** 计划 02 任务 3 那张 7 行矩阵是在本计划落地**之前**写的，行文里没有「本次调用只发了一次」这个前置条件。矩阵里任何**带副作用**的行，只要第一次已经改了状态、第二次返回 `0`/`null`/`COMPILATION_ERROR`，验收者就会记成「**失败**」，然后去「修」一个根本没坏的实现 —— 这是本条与计划 02 叠加后最现实的风险。

**本计划的处置（只登记，不越界改别人的文件）：**
1. **不改** `plans/2026-10-01-02-…md` 的任何一个字节 —— 那是另一个子代理的产物，改它会造成两份计划互相覆盖。
2. 本计划的任务 1-4 落地后，矩阵行的判读规则变成：**先看返回体的 `attempts`**。
   - `attempts: 1` → 该行的结果**可信**，可以按矩阵原样判读。
   - `attempts: 2` → 该行作废，**不是实现有问题**，是又踩了一次重放（说明本计划的落地不完整，回去查漏了哪条路径，见 §4 末尾的判读规则）。
   - `errorCode: "UE_TRANSPORT_UNCERTAIN"` → 该行作废，且**不要重发**，先对账（`uncertain: true` 表示游戏里可能已经执行过了）。
3. 因此，**建议的落地顺序是：本计划 任务 1→2→3→4 → 再实施计划 02 任务 3 的矩阵**。计划 02 的任务 1/2/5/6（纯函数 + 离线断言 + MCP 文案）与本计划无先后要求，可以并行。

---

## 7. 登记项（本计划**明确不做**的事，留给后续）

### 7.1 `index.js:743` 吞掉 `opts.timeoutMs`（既有缺陷，与本条正交）

```javascript
  const timeoutMs = opts.slow ? timeouts.slow : (opts.timeoutMs || timeouts.normal);
```

`slow: true` 时 `opts.timeoutMs` **被整段忽略**。当前三个 `{slow:true}` 调用点（`:4479` hook/create、`:4534` console/execute、`:5038` mapmarker/set）都没打算传 `timeoutMs`，所以今天没有实际损害；但它是一个**静默失效的旋钮** —— 谁按 triage 报告 §6-4 的字面建议去加 `timeoutMs`，就会得到一个完全不起作用的参数（本计划 §1.4）。

**修法（不在本计划）：** 改成 `const timeoutMs = opts.timeoutMs || (opts.slow ? timeouts.slow : timeouts.normal);`。**风险：** 改的是全部慢档调用的取值顺序，须与 §4 的验收一起做。

### 7.2 游戏侧：`UEHttpServer` 的可观测性与协议级去重（需改 `.cs` + 重编译，**本轮不做**）

裁决报告 §3 判定的次责确实存在，但两件事都必须先停游戏、重新构建 `UELoader.dll` —— **本仓库此刻有一个并行调试会话正在用当前 DLL**，重建会把它现场搞坏。登记如下：

| 项 | 落点 | 现状 | 该怎么做 |
|---|---|---|---|
| **(a) 写响应失败的告警带不上任何上下文** | `Source/UELoader/UEHttpServer.cs:778-794`，`:792` | `SendJson(HttpListenerResponse, int, Dictionary<string,object>)` 的**签名里没有 path / 请求标识**，所以 `:792` 只能打一行 `[UEHttp] Failed to send response: <ex.Message>`。调用方 `:543` 拿到的是 `void` ⇒ **上层完全不知道写失败了** | 给 `SendJson` 加一个 `string path` 形参，调用点 `:537` / `:543` 传 `path`，告警改成 `[UEHttp] Failed to send response: POST /<path> …`。**纯日志增强，零行为变更** |
| **(b) 补 `area/*` 的请求日志** | `Source/UELoader/UEHttpServer.cs:585-614` 的 `BuildArgsSummary` | switch 里**没有** `area/delete` / `area/clear` 分支 ⇒ `:410/:414` 两条路由的 `areaId` 不进请求日志（`LogRequest` 的 `:576-583` 会走 `default` 返回 null，打印不带 args） | 加两个 case 返回 `areaId=…` |
| **(c) 协议级去重（唯一能让「重试」变安全的改法）** | `MCP/index.js:4532-4533` + `Source/UELoader/UEHttpServer.cs:391-393` | 路由只把 `code` 传下去，协议里**没有任何请求身份** ⇒ 服务端**根本无法区分「重放」与「新调用」** | MCP 侧生成 `requestId` 放进 body；服务端按 `(requestId → 第一次的响应)` 做短期缓存，重复 id 直接回放第一次的响应、**不再执行**。响应写不出去时第一次的结果其实已经在服务器手里了，所以这是唯一能同时保住「自动恢复」与「不重复执行」的方案 |
| **(d) 板上建议 ① 的「重试前先回一句『上次可能已执行』」** | — | **物理不可实现**：连接已被对端 RST，同一条连接上再写必然失败（板上那条 `Failed to send response` 就是这件事本身） | **不要为它设计任何代码。** 本计划做的是「别重发」 |

**(a)(b) 的成本极低（各 3-5 行、无行为变更），建议在并行会话结束后的第一个空窗做；(c) 是独立的后续计划（改协议 + 重编译 + 缓存淘汰策略）。**

---

*行号复核：`MCP/index.js`、`MCP/config.json`、`Source/UELoader/UEHttpServer.cs`、`Source/UELoader/UEHttpHandler.cs`、`Source/UELoader/UEMainThreadDispatcher.cs`、`Source/UELoader/AutoDeploy.cs`、`docs/mcp-吐槽板.md`，均用 `[System.IO.File]::ReadAllLines` / `ReadAllText`；`MCP/index.js` 的 `callUnityExplorerAPI` 调用点清单（§0.2）由全文正则逐行枚举得到（肯定命中的枚举，非「零命中」推断）。板子定位一律用标题锚点，未用行号。本计划未修改任何既有文件。*
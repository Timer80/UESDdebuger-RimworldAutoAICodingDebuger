裁决：值得修
归并：**不并入计划 02 —— 必须单独立计划**（理由见 §7：改动面、爆炸半径、验收可归因性、实施顺序依赖，四条都指向单立）

---

## 0. 一句话结论

「[31] 静默重放」**是真的，且是本批最险的一条**：`MCP/index.js:812-835` 的 catch 块**无条件重发同一个 POST**（`ECONNRESET` 与 `fetch failed` 都逐字在 `:816` 的正则里，**超时也重试**：`:817` 的 `shouldRefreshPort = isConnError || error._aborted`），整条路径**没有任何幂等判断、没有请求 id、没有 attempt 计数**（唯一痕迹是只在「重试成功」分支出现的 `reconnected: true`，`:829`）。而且它不是罕见边界 —— **MCP 慢档 10 秒（`:740-743` + `config.json` 无 `timings`）< 游戏侧主线程预算 20 秒（`UEHttpHandler.cs:201`）⇒ 任何执行超过 10 秒的 `execute_csharp_code` 100% 被重放**。板上那次「销毁 2099 个岩石」正好落在这个量级。

---

## 1. 核实过程

### 1.1 吐槽板原文（行号用 `[System.IO.File]::ReadAllLines` 复核，定位用标题锚点）

`docs/mcp-吐槽板.md`，锚点 `Select-String -Pattern '^## \[31\]'`；全文件 **1459 行**：

- 索引行 **48**：`🔴 高 | 待修`
- 正文 **296-328 行**，其中：
  - **302-303** MCP 返回体：`output: [Unity] [DOORFIX] destroying 0 non-pawn things in 0..45 x 0..45` / `[DOORFIX] destroyed=0 errors=0`
  - **309-311** `Player.log` 三行：`destroying 2099 …` / `destroyed=2099 errors=0` / **`[UEHttp] Failed to send response: Unable to write data to the transport connection: 远程主机强迫关闭了一个现有的连接。`**
  - **313-314** 条目的因果推断：「同一段代码执行了两次」
  - **324-326** 三条建议

### 1.2 ❗`Player.log` 的原始证据**本轮无法复核**（必须诚实声明）

| 文件 | 行数（本次快照） | 检索式 | 命中 |
|---|---|---|---|
| `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log` | **1269** | `DOORFIX`、`Failed to send response` | **0** |
| 同目录 `Player-prev.log` | **3844** | 同上 | **0** |

⇒ **日志已轮转两次（板上引用的 `:6924/:6960/:7076` 已不在盘上）**，条目那段文字我**无法从磁盘复核**。

**但这不影响裁决** —— 下面 1.3–1.4 的全部结论都是源码直读，不依赖那条日志。

### 1.3 主修点确凿：`MCP/index.js:812-835` 的无条件重试

`MCP/index.js` 全文 **5460 行**，`ReadAllLines` 复核：

```javascript
812  } catch (error) {
816    const isConnError = /fetch failed|ECONNREFUSED|ECONNRESET|ENOTFOUND|network error|connection refused/i.test(String(error && error.message));
817    const shouldRefreshPort = isConnError || (error && error._aborted);
818    if (shouldRefreshPort) {
819      try {
820        const retryUrl = `${getUeBaseUrl(true)}${endpoint}`;
821        const retryResp = await fetchWithTimeout(retryUrl, options);   // ← 同一个 options = 同一个 POST、同一个 body
```

逐条对应本次事故：

| 事实 | 位置 | 说明 |
|---|---|---|
| **A. 无条件重试** | `:818-821` | 对 POST / DELETE 一视同仁；无 `if (isIdempotent)` 之类判断 |
| **B. 触发条件逐字命中** | `:816` | 板上那行「远程主机强迫关闭了一个现有的连接」= WSA **ECONNRESET**；Node/undici 的 `fetch` 在此情形抛 `TypeError`，其 `.message` 就是 **`"fetch failed"`** —— 两者**都在正则里** |
| **C. 超时也重试** | `:817` | `error._aborted` 与连接错误同等对待。`_aborted` 由 `:766-779` 的 `fetchWithTimeout` 在 `controller.abort()` 后挂上 |
| **D. body 被复用** | `:745-754` | 注释 `:745` 自己写着「options 提到 try 外：连接失败重试（换新端口）或 401 重试（换新 token）时可直接复用」⇒ **设计上就没打算区分幂等性** |
| **E. 没有请求身份** | 整段 | 无 idempotency key、无 attempt 计数、无「本次是否已执行」的任何标记 |

### 1.4 🔴 放大器：MCP 侧 10 秒 < 游戏侧 20 秒 ⇒ **重放是必然，不是偶然**

| 位置 | 值 |
|---|---|
| `MCP/index.js:4532-4534` | `execute_csharp_code` → `callUnityExplorerAPI(…, 'POST', { code }, { slow: true })` |
| `MCP/index.js:740-743` | `slow: config.timings?.ueSlowRequestTimeoutMs ?? 10000` |
| `MCP/config.json`（已读全文） | **无 `timings` 段** ⇒ 回落 **10000ms** |
| `Source/UELoader/UEHttpHandler.cs:201` | `UEMainThreadDispatcher.ExecuteOnMainThread(…, 20000)` ⇒ 游戏侧愿意等 **20 秒** |

⇒ **执行耗时落在 10–20 秒之间的脚本，必然被 MCP 中止并重发一次。** 板上那次「扫描并销毁 46×46 区域内 2099 个天然岩石」正是这个量级。

### 1.5 游戏侧：写响应失败**没有**把「可能已执行」传上来 —— 但它**也几乎不可能**做到

`Source/UELoader/UEHttpServer.cs`（全文 **799 行**，`ReadAllLines` 复核）：

```csharp
778  static void SendJson(HttpListenerResponse response, int statusCode, Dictionary<string, object> payload)
779  {
780      try {
782          string json = UELightJson.Serialize(payload);
787          response.OutputStream.Write(buffer, 0, buffer.Length);
788          response.OutputStream.Close();
789      }
790      catch (Exception ex)
791      {
792          UEHttpLog.Warning($"[UEHttp] Failed to send response: {ex.Message}");   // ← 只打一行，返回 void
793      }
794  }
```

- **`:790-793` 吞掉异常、`void` 返回、不上抛、不带请求标识**（没有 path、没有请求 id、没有「handler 已经跑完了」这个事实）。
- 调用方 `:541-543`：`int status = …; LogResult(result, sw); SendJson(response, status, result);` —— **返回 void**，上层无从得知写失败。
- ⚠️ **但必须说清：这条在物理上做不到「把已执行告诉客户端」** —— 连接已被对端 RST，同一条连接上再写什么都没用。**吐槽板建议 ①「重试前先回一句『上次可能已执行』」在当前协议下不可实现。**
- 它**能**做而没做的：(a) 告警里带上 path / 请求标识，让事后对账成为可能；(b) **在服务器侧做幂等去重**，让重放塌缩成一次执行。

服务器侧根本无从区分重放与新调用：

```csharp
391  case "unityexplorer/console/execute":
392      result = PostGuard(request, () =>
393          UEHttpHandler.ExecuteCode(GetString(body, "code")));      // ← 只传 code，没有任何请求身份
```

（`PostGuard` 在 `:730-736`：只做 method + token 检查。）

### 1.6 已有的信号：`reconnected: true` —— 存在、但不可依赖、且零文档

`MCP/index.js:823-830` 的重试成功分支返回 `reconnected: true`；`execute_csharp_code` 在 `:4539` `return r`、`:4542` `JSON.stringify(result, null, 2)` ⇒ **这个字段确实会出现在调用方看到的文本里**。三个问题：

1. **零文档**：`docs/` 全树 grep `reconnected` → **0 命中**（搜索范围 `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\`）。没有任何地方告诉调用方它存在。
2. **只在重试成功时出现**：重试也失败时（`:831-834` `error = retryErr` → `:837` `UE_TIMEOUT`，或 `:841-856` 的阶段错误）**完全没有这个字段** ⇒「代码已经跑过一次」这个事实 100% 丢失。
3. **语义混叠**：同时表示「刷新了 ports.json」和「重发了一次请求」，调用方无法区分。

### 1.7 爆炸半径：12 个写端点共用这条路（**不是 `execute_csharp_code` 专属**）

ripgrep 全量列出 `MCP/index.js` 内所有 `callUnityExplorerAPI('/` 调用，其中写端点为：

| 行号 | 端点 | 重放后果 |
|---|---|---|
| `:4474` | `unityexplorer/hook/create` | 多挂一个钩子 |
| `:4499` | `unityexplorer/hook/toggle` | 开关翻转两次 |
| `:4511` | `unityexplorer/hook`（DELETE） | 删两次 |
| `:4443` | `unityexplorer/log/clear` | 无害 |
| **`:4532`** | **`unityexplorer/console/execute`** | **任意 C#，后果最重** |
| `:4548` | `unityexplorer/console/reset` | 无害 |
| `:4557` | `unityexplorer/console/addusing` | 重复 using |
| `:4583` | `unityexplorer/hotreload/apply` | 打两次补丁 |
| `:4589` | `unityexplorer/hotreload/watch` | 无害 |
| `:5001` | `mapmarker/clear` | 无害 |
| `:5008` | `mapmarker/recolor` | 无害 |
| `:5038` | `mapmarker/set` | 光标被移动两次 |

⇒ `execute_csharp_code` 只是**后果最重的一个**（用户代码可以是任意 C#，可以删东西）。

---

## 2. 🚨 [31] 与 [20] 的区分（**两件事，不要混**）

| | **[20] 重复回显** | **[31] 静默重放** |
|---|---|---|
| 现象 | 同一**份内容**在 `data.output` 里出现 2 份 | 同一次**调用**被**执行两次**，返回的是第 2 遍的结果 |
| 发生层 | 游戏侧**捕获管线内部**（`UEHttpHandler.ExecuteCode` 的一次 `captured` 列表内） | **MCP 传输层**（`callUnityExplorerAPI` 的重试），第二次执行有它自己**独立**的一份 `captured` |
| 成因 | 本项目 `ExplorerBootstrap.OnUELog` 把 UE 日志用 `Verse.Log` 回灌一遍 | `MCP/index.js:818-821` 重发同一个 POST |
| 修好一个会怎样 | `[20]` 修好 ⇒ `output` 里只剩 1 份，**但第 2 次执行照样发生** | `[31]` 修好 ⇒ 只有 1 次执行，**但那一次的 output 里仍有 2 份** |
| 修复落点 | 计划 02 `UEExecLogCapture`（去重规则） | 本条 §4 的 MCP 重试改造 |

**判别方法（给 PLAN 的验收用）**：

- `Player.log` 里**同一个哨兵**（`Invoked REPL (no return value)` / `Unable to compile the code.`）出现 **2 组 = [31]**；
- 依据 triage 20 §1.4 实测：5 次 `execute_csharp_code` → `Invoked REPL (no return value)` 命中 10 行 = **5 组 × 2**，而那「×2」是 `[UnityExplorer] [Unity] ` 回声；**若同一组哨兵连着出现两次（无前缀差异），那是 [31]**。
- 同一条用户日志以 `[UnityExplorer] [Unity] X` + `X` 两种形态各一份 = **[20]**。

---

## 3. 责任层（明确回答：**MCP 为主责，UEHttpServer 为次责**）

| 现象 | 责任层 | 位置 |
|---|---|---|
| **无条件重发带副作用的 POST**（[31] 的直接成因） | **🔴 主责：本项目 MCP** | **`MCP/index.js:812-835`**（`:816` 正则含 `ECONNRESET`；`:817` 超时也重试；`:818-821` 用同一个 body 重发；`:745-754` 注释明写 options 设计上就是复用的） |
| **MCP 慢档 10s < 游戏侧 20s 预算，制造「必然重放」** | **🔴 主责：本项目 MCP** | `MCP/index.js:740-743` + `:4534`（`{ slow: true }`）+ `MCP/config.json`（无 `timings`）vs `Source/UELoader/UEHttpHandler.cs:201` |
| **写响应失败只打一行不含请求标识的告警** | **🟠 次责：本项目** | **`Source/UELoader/UEHttpServer.cs:778-794`**（`:792`）；返回 `void`，调用方 `:543` 无从得知 |
| **服务端无法区分重放与新调用** | **🟠 次责：本项目** | **`Source/UELoader/UEHttpServer.cs:391-393`**（路由只把 `code` 传下去，协议缺请求 id） |
| 建议 ①「重试前先回一句『上次可能已执行』」 | **⚠️ 协议限制，不可实现** | 连接已被 RST，同连接无法再写。**真正的落点只有两个：MCP 侧别重发 / 服务器侧去重** |
| `HttpListener` 抛 `Unable to write data to the transport connection` | .NET `System.Net.HttpListener` 的正常行为，**不是本项目代码错误** | 但**由本项目的无脑重试放大成静默重放** |

**⚠️ 写给 PLAN 的防呆提醒（本条最容易写歪的地方）：**
`UEHttpHandler` / `UEHttpServer` **完全不控制重试**。100% 的重试决策发生在 `MCP/index.js:812-835`。
**不要**把修复写成「改 `UEHttpHandler` 让它别重试」—— 那是找不到北的写法。

**不是 vanilla RimWorld 的锅**：本条全程走本项目自有的 `/unityexplorer/console/execute` + `Source/UELoader/UEHttpServer.cs`。
**不是 UnityExplorer 的锅**：重放发生在 UE 之上的 MCP 传输层，UE 根本没参与第二次决策。
**不是 RBS / GABP 的锅**：同 [20]，本条不走 GABP。

---

## 4. 影响与严重度复评

**维持 🔴 高。** 这是本批唯一一条命中 AGENT-BRIEF §5 第 2 条第一档（「会让自动化得出**事实相反**的结论」）的条目。

**比原判更严重的两点**：

1. **不是罕见边界，是结构必然。** §1.4 已证：MCP 慢档 10s < 游戏侧 20s ⇒ **执行超过 10 秒的 `execute_csharp_code` 100% 被重放**。任何真实规模的批量操作都会踩到。
2. **比「返回第二遍的数字」更狠的失效模式**：当第 2 次执行因为第 1 次已改变前置状态而**编译失败或抛异常**时，返回的是 `success:false`。调用方会把「我确实删了 2099 个东西」误判成「失败，重试」—— 此时再发一次就是**第三次**执行。

**诚实列出「不足以再往上」的理由**：

- 现有响应里**已经有** `reconnected: true`（`:829`）这个信号，**不是「完全零信号」**，只是「信号存在但不可依赖」（§1.6）。
- 修复主责在 MCP 一侧，**不需要碰 vanilla、不需要改 HTTP 协议即可止血**（§5 第 1 条），成本低。

---

## 5. 核心问题 ①：[31] 是否让计划 02 / [02] / [20] 的修复变得**不可信**？

### 明确回答：**不不可信 —— 但也不被它修复。是「正确但不足」。**

「你修好 `output`，也可能返回的是第二遍的」这句话**在语义层成立，在正确性层不成立**。分层说：

| 层 | 受不受 [31] 影响 | 理由 |
|---|---|---|
| **计划 02 的离线断言**（任务 1/2/5/6 全部 `dotnet run --project Source/ExecLogCaptureTests`） | **✅ 完全不受影响** | 全是对 `UEExecLogCapture` 的**纯函数**测试，不经过 HTTP、不经过 `callUnityExplorerAPI`。重复执行不产生任何额外断言失败。**测试结论仍然有效。** |
| **计划 02 的 `UEHttpHandler.ExecuteCode` 改写（任务 2）** | **✅ 仍然正确** | `UEExecLogCapture` 解析的是**某一次** `ExecuteCode` 的 `captured`。第 2 次执行有它自己独立的一份 `captured`，解析完全正确 —— 只是那份 `captured` 描述的是**第 2 次的世界状态**。`output`/`result`/`logCapture` 的**字段语义一条都不错**。 |
| **计划 02 任务 3 的游戏内验收矩阵** | **🔴 直接被污染** | 7 行全走真实 HTTP。**反向假阳性**是真正的危险：任何带副作用的行若第一次已改状态、第二次返回 `0`/`null`/`COMPILATION_ERROR`，验收者会记成**失败**，然后去「修」一个根本没坏的实现。 |
| **计划 02 任务 4 的文案（`MCP/index.js:2937`）** | **🟠 需要补免责** | 文案教调用方「要结果就走 `result`」；[31] 成立时 `result` 是**第二遍**的 `result`。必须补一句。 |

**⇒ 结论**：

1. **计划 02 的实现可以照原样实施，不需要打回重做。**
2. **但计划 02 落地后 [31] 仍然是敞着的** —— 计划 02 一行都不碰 `MCP/index.js:812-835`。
3. **计划 02 任务 3 必须加一条前置断言**（见 §6 第 3 条）。
4. ⚠️ **不要**把「修好 `output` 就能自证捕获正确」写进任何交付说明 —— §0.2 立的那个验收基线（「在 `Log Unity Debug = False` 下 `output` 也必须含 marker」）**依赖人工判读**，一旦发生重放，判读者没有任何提示。

**对 triage 02 / triage 20 的影响**：两者的结论**都不受影响**。
- triage 02 的「结构上抓不到 `Verse.Log.*`」→ 被计划 02 §0.2 用反编译推翻过，与 [31] 正交。
- triage 20 的「重复范围 = 哨兵 × 2」→ **恰恰相反**：triage 20 §1.4 实测 5 次调用 = 5 组哨兵，说明**正常情况下每次调用只产生 1 组哨兵**。**若某次出现 2 组，那就是 [31]，不是 [20]** —— 这反过来给 [31] 提供了一条可用的判别指纹。

---

## 6. 修的话怎么修

按「成本 / 收益」排序，不写实现：

1. **🔴 主修 —— 给非幂等请求一个「不自动重试」的开关（`MCP/index.js:812-835`）。**
   在 `callUnityExplorerAPI` 的 `opts` 上加显式 `idempotent`（默认由端点/方法推导），对 `execute_csharp_code` 这类任意副作用请求置 `false`：`shouldRefreshPort` 命中时**不重发**，直接返回 `errorCode: 'UE_TRANSPORT_UNCERTAIN'`，并在 `guidance` 里写死「**上一次请求可能已经在游戏里执行过了**；用 `read_rimworld_log` / grep `Player.log` 对账后再决定是否重发」。
   - 这是**唯一不需要协议变更就能止血**的改法。
   - 代价：UE 端口真的滚动（游戏刚重启）时这类调用从「自动恢复」变成「要手动再发一次」。**这个取舍应由维护者拍板，不要由子代理默认。**

2. **🟠 补信号 —— 每次响应都带 `attempts`（`MCP/index.js:823-830` 及所有 return 分支）。**
   把只在重试成功时出现的 `reconnected: true`（`:829`）改成**无条件**的 `attempts: <n>` + `replayed: <bool>`，并让它出现在**每一条**返回路径上 —— 包括 `:837` 的 `UE_TIMEOUT` 与 `:841-856` 的阶段错误。成本近乎为零，收益是「第二遍」这件事**永远不再静默**。
   - 顺带：`MCP/index.js:2937` 的 `TOOL_USAGE_NOTES` 里点名 `attempts`/`replayed`（`docs/` 全文 grep `reconnected` 零命中 ⇒ 现在没有任何地方告诉调用方这个字段存在）。

3. **🟠 计划 02 任务 3 的前置断言（不是本计划的改动，但要登记）。**
   任务 3 的 7 行矩阵在跑之前，每行**先跑一次幂等探针**（例如 `1 + 1;`）确认 `reconnected` 未出现；出现即中止矩阵并先修 §6 第 1 条。
   - 这是给计划 02 的**登记表项**，不是让它改实现。

4. **🟠 结构性修 —— 超时预算对齐（`MCP/index.js:740-743` / `:4534` vs `UEHttpHandler.cs:201`）。**
   慢档 10s 必须 ≥ 游戏侧 20s 主线程预算，否则「重放」从偶发变必然。至少给 `execute_csharp_code` 显式 `timeoutMs`（如 30000），或在 `config.json` 里配 `timings.ueSlowRequestTimeoutMs`。
   - 只做这条**不够**（连接被 RST 的路径依然重试），但不做这条，1/2 会被频繁触发。

5. **🟡 协议级正解（若维护者愿意动协议）—— 请求 id + 服务端去重。**
   `MCP/index.js:4532-4533` 在 body 里带客户端生成的 `requestId`；`Source/UELoader/UEHttpServer.cs:391-393` 把 `requestId` 传进 `UEHttpHandler.ExecuteCode`，服务器用 `(requestId → 第一次的响应)` 做短期缓存，重复 `requestId` 直接回放第一次的响应、**不再执行**。
   - 这是**唯一能让「重试」变成安全操作**的改法 —— 响应写不出去时，第一次的结果其实已经在服务器手里了。
   - 成本最高：改协议 + 改 DLL + 停游戏重新构建 + 缓存淘汰策略。**建议作为本计划的后续阶段，不与 1/2/4 打包。**

6. **📌 建议 ③（文档纪律「验收必须回 `Player.log` 看第一遍」）—— 采纳，成本为零，但要与 [09] 一起写。**
   `Player.log` 会被轮转（同板 [09]），所以这条纪律必须写成「**先确认当前 `Player.log` 就是本次会话的**」，否则对账本身会踩坑。

7. **🚫 明确不做**：
   - 🚫 写成「改 `UEHttpHandler` / `UEHttpServer` 让它别重试」—— **重试不在那里**（§3 防呆提醒）；
   - 🚫 任何形式的 `Verse.Log` / `Debug.Log` Harmony patch（`MCP/index.js:4461-4462` 黑名单、`:4469` 实测 1 分钟 63.5MB + mono 原生崩溃）；
   - 🚫 改 `ExplorerBootstrap.OnUELog` / `config.cfg` 的 `Log Unity Debug`（[15] / UE 上游的合理默认）。

---

## 7. 与计划 02 / [02] / [20] 的关系 + 范围裁决

### 7.1 裁决：**必须单独立计划，不并入计划 02**

四条理由，任一即足够：

1. **改动面不同。** 计划 02 的全部改动落在 `Source/UELoader/UEExecLogCapture.cs`（新建）+ `UEHttpHandler.cs` 的 `ExecuteCode` + `MCP/index.js` 的 `:2937`/`:2073-2082`/`:4539`。本条主修点是 **`MCP/index.js:812-835` 这个 catch 块本身** —— 计划 02 **一行都不碰它**（已在计划 02 全文 grep 确认：无 `重放`/`replay`/`幂等`/`ECONNRESET`/`callUnityExplorerAPI.*重试` 命中）。
2. **归并会污染计划 02 的验收。** 计划 02 任务 3 是一个 7 行游戏内矩阵。若把「关闭/改造重试」塞进同一份计划，矩阵每一行都同时在测两件正交的事（捕获正确性 + 是否重放），失败时无法归因。
3. **爆炸半径差一个数量级。** 计划 02 只服务 `execute_csharp_code` 一个工具；本条覆盖 `callUnityExplorerAPI` 上 **12 个写端点**（§1.7）。按 `execute_csharp_code` 立项会漏掉 11 个。
4. **实施顺序有依赖，不是并列。** 本条应当**先于**计划 02 落地 —— 只有在「不会静默重放」的前提下，计划 02 任务 3 的矩阵结果才可解释。⇒ 若主代理坚持合并，至少要在计划 02 **前面**加一个「任务 0」并标成阻塞项。

### 7.2 是否已被现有计划覆盖：**否**

- `docs/issue-triage/plans/` 现有 **14** 份、`triage/` 现有 **24** 份；全树 grep `重放`/`replay`/`幂等`/`idempot`/`attempts`/`reconnected`/`ECONNRESET`/`执行两次`：**无一条覆盖本条**。
- 计划 01（`2026-10-01-01-debug-action-execution-evidence.md`）处理的是 `execute_debug_action` 的「报成功但没执行」，走 **GABP** 通道，与本条的 MCP 重试**无交集**。
- 计划 02（`2026-10-01-02-…`）已并入 [20] 与 [34]，**均未提及重放**。

### 7.3 [02] 与 [20] 的处置建议（主代理批注用）

- **[02]**：严重度维持 triage 20 §5.2 给出的 **🔴 → 🟠**（`Verse.Log.*` 已证实落盘，前提「不落 Player.log」被证伪）。本条**不改这个裁决**，但在批注里加一句：**「`execute_csharp_code` 的返回体可能来自第二次执行（[31]）」**。
- **[20]**：维持 triage 20 的裁决（🟠 → 🟡 低-中，并入计划 02）。本条**不改**，只需在批注里点明二者**正交**（§2 的判别方法）。
- **[09]**（Player.log 轮转）：本条的建议 ③ 依赖它，建议两份批注互相引用。

---

## 8. 搜索范围与「无法核实」清单（**凡「不存在/零命中」均已写出范围**）

| 项 | 范围 / 状态 |
|---|---|
| 吐槽板 `[31]` 段 | 标题锚点 `^## \[31\]` 定位（**未用行号定位**）；`[System.IO.File]::ReadAllLines` 复核：全文件 **1459 行**，索引行 **48**，正文 **296-328** |
| `MCP/index.js` | 全文 **5460 行**；`ReadAllLines` 复核 `:736` `:740-743` `:745-754` `:766-779` `:786-811` `:812-857` `:1474` `:4530-4544`；ripgrep 行号与 `ReadAllLines` 在 `:146`/`:816`/`:817`/`:829` 四处交叉一致 |
| `Source/UELoader/UEHttpServer.cs` | 全文 **799 行**；`ReadAllLines` 复核 `:344-363`（ListenLoop）`:365-544`（ProcessRequest）`:618-637`（LogResult）`:730-736`（PostGuard）`:778-794`（SendJson） |
| `Source/UELoader/UEHttpHandler.cs` | 全文 **771 行**；`ReadAllLines` 复核 `:163-245`，含 `:192` `ConsoleController.Evaluate(code,false)`、`:201` 的 **20000ms** 预算、`:207-210` 退订、`:239-244` `Success` |
| `MCP/config.json` | 已读全文：**无 `timings` 段** ⇒ 慢档回落 10000ms |
| `MCP/test/` | 9 个测试文件全列（`hotreloadConcealment` / `index.integration` / `journal` / `mapMarker` / `quickTest` / `runner` / `timeoutPolicy` / `toolchainFixes` / `toolCountConsistency`）。`timeoutPolicy.test.js` 的 17 条用例**全是 GABP 超时策略** ⇒ **`callUnityExplorerAPI` 的重试路径零测试覆盖**（⇒ PLAN 加测试不会撞既有断言） |
| `docs/issue-triage/**` | `plans/`(14) + `triage/`(24) 全树；grep `重放\|replay\|幂等\|idempot\|attempts\|reconnected\|ECONNRESET\|执行两次`：**无一条覆盖本条** |
| `docs/` 全文 grep `reconnected` | 搜索范围 = `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\` 全树 ⇒ **0 命中**（§1.6 结论 1 的依据） |
| **❗无法核实：`Player.log` 里 DOORFIX 的原始三行** | `Player.log` 快照 **1269 行**、`Player-prev.log` 快照 **3844 行**；两者对 `DOORFIX` 与 `Failed to send response` 的检索**均零命中**（日志已两次轮转）。⇒ 板上引用的 `:6924/:6960/:7076` **本轮无法复核**。**裁决不依赖它**（§1.3–1.4 全为源码直读） |
| **❗无法核实：第二次执行与第一次的间隔** | `MCP/logs/` 共 5 个 `.log`（最大 `mcp-20261001-093829.log` 168512 字节），最新为 **10-01**；事故发生在 **10-02** ⇒ **MCP 侧无本次调用记录**，无法独立确认「确实重试了一次」 |
| **推断（基于证据，非源码）**：Node/undici 在 ECONNRESET 时 `.message === "fetch failed"` | §1.3-B。依据：`:816` 正则同时列了 `fetch failed` 与 `ECONNRESET`，说明作者见过二者；**即便 `.message` 不是 `fetch failed`，`ECONNRESET` 也在正则里、路径成立**。标注为推断，但**不构成结论的单点依赖** |
| **未使用** | `GABS-main/`（Go）、RBS DLL 反编译、`Source/UE-4.9.0-official/**`（UnityExplorer 源码）、`McpRimDebug/` —— 本条不涉及 |
| **搜索范围声明（防「局部检索当全局结论」）** | 本报告所有「不存在 / 零命中」结论的搜索范围均已写在上表。**本条未引用 `McpRimDebug/` 下任何路径**（triage 20 §1.1 已证该目录无 `ConsoleController.cs`）。**本条未使用任何带 `\|` 的 `search_string_literals` 检索**（§3.5 告警）；表内含 `\|` 的两处均为 ripgrep **正则**检索且已逐条拆明关键字 |

---

*行号复核：`MCP/index.js`、`Source/UELoader/UEHttpServer.cs`、`Source/UELoader/UEHttpHandler.cs`、吐槽板，均用 `[System.IO.File]::ReadAllLines`；其余行号来自 ripgrep（已与 `ReadAllLines` 交叉一致）。板子定位一律用标题锚点，未用行号。本报告未修改任何既有文件。*
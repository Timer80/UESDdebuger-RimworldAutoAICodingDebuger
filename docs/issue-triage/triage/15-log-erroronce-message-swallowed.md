# [15] `Log.ErrorOnce` 的消息正文被 `Error_Patch2` 吞掉，只剩栈

裁决：不值得修

## 2. 一句话结论

**核心断言已被证伪**：消息正文**没有被吞掉**，`Player-prev.log:11301` 里就完整躺着 `currentSelectable: null`；板上建议的「修 `OnUELog` 别丢原 message」是在修一个**不存在的 bug**（该函数第 204 行本来就在转发）；真实原因是**日志轮转 + `ErrorOnce` 去重**，而轮转已经是板上第 [09] 条。

## 3. 核实过程

### 3.1 读了什么

| 文件 / 工具 | 用途 |
|---|---|
| `docs/mcp-吐槽板.md:36-76` | 条目原文 |
| `Source/UELoader/ExplorerBootstrap.cs:195-218` | 本项目自己的 UE 日志桥 |
| `Source/UELoader/UEHttpLog.cs`、`UEHttpHandler.cs` | 同类日志转发实现对照 |
| 全 `Source/` grep `Verse\.Log\|HarmonyPatch.*Log` | 找本项目是否 patch 了 `Verse.Log` |
| `Mods/` 全目录 grep `typeof\(Verse\.Log\)|typeof\(Log\)` | 找真正的 patcher |
| `RimworldMCPDebug/reference/RIMAPI-master/.../Hooks/DevToolsHooks.cs` | 命中物，读全文 79 行 |
| 反编译器 MCP（`ue` = UnityExplorer 程序集） | 读 `ExplorerCore.LogUnity`、`ExplorerStandalone.CreateInstance`、`ExplorerEditorLoader.LogHandler` |
| **本机 `Player.log`（1,053,806 B）与 `Player-prev.log`（1,005,570 B）** | **决定性证据** |
| `rimworld-source-online` 读 vanilla `Source/Verse/Log.cs`、`Source/Verse/GizmoGridDrawer.cs` | 核对板上的 vanilla 行号与 `ErrorOnce` 实现 |

### 3.2 板上引用的 vanilla 行号 —— 对的

`search_source` 实测：`./Source/Verse/GizmoGridDrawer.cs:134` =
```csharp
Log.ErrorOnce($"{arg} currentSelectable: {obj.ToStringSafe()}", 3427734);
```
板上写「`GizmoGridDrawer.cs:132-135`」，134 落在区间内 ✔。
（附带：文件在 `Source/Verse/`，不在 `Source/RimWorld/` —— 再次印证板上 [13]。）

### 3.3 `ErrorOnce` 的 vanilla 实现 —— 消息一定会走到 Player.log

`Source/Verse/Log.cs`（约 169 行起）：
```csharp
public static void ErrorOnce(string text, int key)
{
    lock (logLock)
    {
        if (!PreventLogging && usedKeys.Add(key))
        {
            Error(text);
        }
    }
}
```
而 `Error(text)` 的末尾（**在 `if (!currentlyLoggingError)` 块之外**）无条件执行：
```csharp
        Debug.LogError(text);
```
⇒ 消息正文必然被交给 Unity 日志系统 → `Player.log`。**vanilla 侧不存在丢弃。**

### 3.4 本项目自己的桥 —— **在转发，没有丢**

`Source/UELoader/ExplorerBootstrap.cs:195-218`：
```csharp
private static void OnUELog(string message, LogType logType)
{
    try
    {
        switch (logType)
        {
            case LogType.Error:
            case LogType.Exception:
            case LogType.Assert:
                Log.Error($"[UnityExplorer] {message}");   // ← 第 204 行，message 原样带上
                break;
            case LogType.Warning:
                Log.Warning($"[UnityExplorer] {message}");
                break;
            default:
                Log.Message($"[UnityExplorer] {message}");
                break;
        }
    }
    catch { }
}
```
板上第 49 行的栈也自证了这点：`OnUELog ... (at ExplorerBootstrap.cs:204)` 出现在链上，说明**第 204 行确实被调用了**。**建议 1（"别丢原 message"）是在修一个不存在的缺陷。**

> ⚠ **派单说明与事实不符**：派单要求「说清 `Log.Error_Patch2` / `ExplorerBootstrap` 属于第三方 UnityExplorer」。实测**不成立**：`ExplorerBootstrap.cs` 是**本项目自己的源文件**（`Source/UELoader/`，已编译进 `Assemblies/UELoader.dll`，反编译器上下文 `ueloader` 指向该 dll）；而 UnityExplorer **从不 patch `Verse.Log`**（对 `ue` 程序集 grep `Verse.Log` 字符串字面量 → `No matches found`，grep `Log_Prefix` → 0 命中）。

### 3.5 `Verse.Log.Error_Patch2` 到底是谁 —— 是 RIMAPI，不是 UnityExplorer

全 `Mods/` 目录 grep `typeof(Verse\.Log)|typeof(Log)|HarmonyPatch.*Log\)|GetMethod.*"Error"` → **3 处命中，全部在同一个文件**：
`Mods/RimworldMCPDebug/reference/RIMAPI-master/Source/RIMAPI/RimworldRestApi/Hooks/DevToolsHooks.cs`
```csharp
:70  [HarmonyPatch(typeof(Log), nameof(Log.Error), new Type[] { typeof(string) })]
:71  public static class LogErrorPatch
:72  {
:73      static void Postfix(string text)
:74      {
:75          SendLogToSse("Error", text);
:76      }
:77  }
```
`Error_Patch2` 正是 Harmony 给 `Verse.Log.Error(string)` 生成的动态方法包装（`_PatchN` 后缀）。
**关键：它是 `Postfix`，不是 `Prefix`** ⇒ 原方法体照常执行，**只多一次 SSE 转发，不抑制任何东西**。**RIMAPI 也不是元凶。**

### 3.6 决定性证据：消息正文就在日志里

对 `Player.log` 与 `Player-prev.log` 分别 `Select-String`（`-SimpleMatch`，子串匹配）：

| 模式 | `Player.log`（当前） | `Player-prev.log`（轮转后） |
|---|---:|---:|
| `currentSelectable` | **0** | **2** ✅ |
| `Pawn_CreepJoinerTracker` | 0 | 4 |
| `ErrorOnce` | 0 | 4 |
| `Error_Patch2` | 4 | 9 |
| `[UnityExplorer]` | 272 | 245 |
| `3427734` | 0 | 0 |

`Player-prev.log` 命中行原文（**逐字**）：
```
11301:     - FINALIZER T20Probe.BUG07: Void P0_BUG07_Recruit:Fin_DrawGizmoGridFor(Exception __exception) currentSelectable: null
11335:     - FINALIZER T20Probe.BUG07: Void P0_BUG07_Recruit:Fin_DrawGizmoGridFor(Exception __exception) currentSelectable: null
```
⇒ **`currentSelectable:` 完整存在、文本无损。**板上所说「命中 0 行」是因为**grep 的是轮转后的新 `Player.log`，那一条在旧文件里**。

同时 `Player.log` 的 `Error_Patch2` 上下文（4 处全文展开）也证明消息在另一条路径上同样无损：
```
[UnityExplorer] [Unity] GUI Error: You are pushing more GUIClips than you are popping. Make sure they are balanced.
UnityEngine.StackTraceUtility:ExtractStackTrace ()
UnityEngine.DebugLogHandler:LogFormat (...)
UnityEngine.Logger:Log (UnityEngine.LogType,object)
UnityEngine.Debug:LogError (object)
(wrapper dynamic-method) MonoMod.Utils.DynamicMethodDefinition:Verse.Log.Error_Patch2 (string)
UELoader.ExplorerBootstrap:OnUELog (string,UnityEngine.LogType) (at .../UELoader/ExplorerBootstrap.cs:204)
UnityExplorer.ExplorerStandalone/<>c:<UnityExplorer.IExplorerLoader.get_OnLogError>b__23_0 (object)
UnityExplorer.ExplorerCore:Log (object,UnityEngine.LogType)
UnityExplorer.ExplorerCore:LogUnity (object,UnityEngine.LogType)
UnityExplorer.Runtime.MonoHelper:Application_logMessageReceived (string,string,UnityEngine.LogType)
```
注意这里消息**只多了前缀，主体一字未改**。

### 3.7 无法核实 / 明确不成立的部分

- `3427734` 在**两个**日志里都是 0 命中 —— 但这**符合预期**：那个数字只是 `ErrorOnce` 的去重 key，**从不进入消息文本**（板上把它当判据本身就不成立）。
- `Verse.Log:ErrorOnce` 在当前 `Player.log` 0 命中 —— 因为那是**另一次会话**的产物，早已轮转出去。**不是「只剩栈」，是「这次日志里没有这次事件」。**
- 我**没有**复现出板上那次 `Pawn_CreepJoinerTracker.GetGizmos` 的 NRE（当前日志 0 命中），**无法确认**当次会话究竟发生了什么 —— 这一段明确标注为 **未知**，不做推断。

## 4. 影响与严重度复评

**从 🟠 下调到 🟡（且实为 🟡 的文档项）。**

- 板上担心的「grep 不到消息 → 误判没抛异常」是**真实的假阴性风险**，这点它没说错。
- 但**该风险不来自 `Error_Patch2`/`OnUELog`**，而来自两个别的东西：
  1. **日志轮转**（板上已是第 [09] 条「每次游戏启动 Player.log 轮转，旧内容进 Player-prev.log，但没有任何工具暴露这一点」）；
  2. **`ErrorOnce` 的去重语义**（`usedKeys.Add(key)`，`Log.cs`）—— 一个 key 一局只记一次，第二次起静默不写。**这是 vanilla 行为，加不加 UE 都一样**，且完全不可被任何补丁修复。
- ⇒ **同一根因已被 [09] 覆盖**，本条是 [09] 的一个「变种错觉」，**新增价值接近零**。
- **唯一残留的真实影响（🟡，且是本项目可修的）**：**日志前缀被套了两层** —— UE 加 `[Unity] `（`ExplorerCore.LogUnity`：`Log($"[Unity] {message}", logType)`），本项目再加 `[UnityExplorer] `（`ExplorerBootstrap.cs:204`），最终落盘成 `[UnityExplorer] [Unity] <原文>`（§3.6 实测 272/245 行）。
  **对子串 grep 无影响**（板上担心的假阴性不会由此产生），但会让「按行首匹配 vanilla 文本」的判据失效，并让日志可读性变差。
- **明确不算「假成功 / 假阴性」的形态**：这条**不会**让自动化报出与事实相反的**成功**结论；它只会让人**漏看**一次已发生的异常，且漏看的机制（轮转 + 去重）**另有条目跟踪**、且**可被 `[UnityExplorer]`/`Error_Patch2` 栈帧检出**（栈确实在）。故不构成 🔴。

## 5. 责任归属

**本项目（但只对「双层前缀」这一小块负责）；主线是纯环境事实，不是任何人的 bug。**

| 环节 | 归属 | 理由 |
|---|---|---|
| `Log.ErrorOnce` → `Error` → `Debug.LogError(text)` | **上游 vanilla RimWorld** | `Source/Verse/Log.cs`，消息无条件落到 `Debug.LogError`。且 `usedKeys` 去重是 vanilla 设计。 |
| `Verse.Log.Error_Patch2` 的**存在** | **第三方（RIMAPI）** | `DevToolsHooks.cs:70` 的 `[HarmonyPatch(typeof(Log), nameof(Log.Error), ...)]`。**但它是 Postfix，不改变行为，也不在本项目控制范围内。** 本项目**既不能改、也不该改**；改它属于改另一个项目的 mod，且 RIMAPI 升级会被覆盖。 |
| `ExplorerCore.LogUnity` 加 `[Unity] ` | **第三方（UnityExplorer 4.9.0）** | 反编译确认。vendored 在 `Source/UE-4.9.0-official/`，升级会覆盖。 |
| `ExplorerBootstrap.OnUELog` 加 `[UnityExplorer] ` | **本项目** | `Source/UELoader/ExplorerBootstrap.cs:204`，本项目自己的代码。 |

**关于派单的三个问题，明确回答：**

1. **「`Log.Error_Patch2` / `ExplorerBootstrap` 属于第三方 UnityExplorer」** → **不成立**。
   `ExplorerBootstrap.cs` 是本项目文件；`Error_Patch2` 来自 RIMAPI；UnityExplorer 对 `Verse.Log` 零 patch。
2. **「本项目能不能改？」** → 改 `[UnityExplorer] ` 这个前缀：**能**（本项目源码）。
3. **「改了会不会被上游覆盖/升级打回？」** →
   - 改 `ExplorerBootstrap.cs`：**不会**被打回，它是本项目的文件。
   - 改 UnityExplorer 的 `[Unity] `：**会**，属于 vendored 第三方，4.9.0→新版即失效，**不要动**。
   - 改 RIMAPI 的 `DevToolsHooks.cs`：**会**，且那是**别人的仓库**，**越界，不要动**。

## 6. 修的话怎么修

**不建议按板上建议 1 实现。**（`OnUELog` 没丢消息，加 `[UELoader-original]` 只会再套一层前缀，制造噪声与重复。）

若维护者仍想做，唯一有代码依据的改动：

1. **可选的 🟡 清理**：`Source/UELoader/ExplorerBootstrap.cs:201-211`，把 `[UnityExplorer] ` 前缀与 UE 的 `[Unity] ` 去重（改用 `[UELoader] ` 或干脆不加工具前缀，只保留 UE 的 `[Unity] `）。
   - 前提：需先确认 `OnUELog` 的其它调用方是否依赖 `[UnityExplorer]` 前缀做过滤（`MCP/logs/` 下若有解析脚本依赖，需一并检查）。
   - 收益：**仅改善可读性与「按行首匹配」判据**；**不解决**板上担心的假阴性（那个归 [09]）。
2. **真正该做的是补文档**（板上建议 2 方向对，但归因要改写）：在日志相关文档里写明 **判 vanilla 日志的可靠顺序**：
   1. 先确认查的是 `Player.log` 还是 `Player-prev.log`（**轮转**）；
   2. `ErrorOnce` 类事件**一局只出现一次**（`usedKeys` 去重），重复触发在日志里查不到第二次；
   3. UE 下文本带 `[UnityExplorer] [Unity] ` 前缀，但**子串 grep 不受影响**，可放心按 vanilla 原文 grep。
4. **不要**做的事：不要给 `OnUELog` 加「原 message」补丁；不要改 UnityExplorer；不要改 RIMAPI。

## 7. 是否已被现有计划覆盖

**本条无对应实现计划，但根因被另一条目覆盖。**

- `docs/superpowers/plans/` 下只有 `2026-09-27-ue-csharp-console-roslyn-upgrade.md`；对其 grep `Error_Patch2|ErrorOnce|currentSelectable|Player\.log|行号前缀` → **`No matches found`**；其 12 个任务全是 Roslyn/CSConsole 升级，**零交集**。
- **但根因指向板上第 [09] 条**（`docs/mcp-吐槽板.md:26`）：
  > 「每次游戏启动 `Player.log` **轮转**，旧内容进 `Player-prev.log`，但没有任何工具暴露这一点」🟡 低 / 待修

  §3.6 的实测（`currentSelectable` 在 `Player-prev.log` 有 2 命中、在 `Player.log` 有 0 命中）**就是 [09] 的直接证据**。
- ⇒ 若维护者要动这一块，应该**并入 [09] 的计划**一起处理，而不是给 [15] 单开计划。
- 另：§3.4 指出派单前提有误（把本项目文件当成第三方），**建议主代理同步修正派单描述**，避免后续 PLAN 子代理沿着错误前提去改 UnityExplorer 或 RIMAPI。
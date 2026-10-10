# UE C# 控制台编译器升级可行性评估

- 评估对象：`UESDdebuger` 内嵌的 UnityExplorer 4.9.0 C# 控制台（`Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole`）
- 运行环境实测：RimWorld 1.6.4871 / Unity **2022.3.35f1** / Mono（`mscorlib 4.0.0.0`，netstandard **2.1**）
- 评估方式：静态代码审计 + **在运行中的游戏进程内实测** + 独立 Mono 进程 PoC
- 结论日期：本次会话

---

## 1. 结论（TL;DR）

| 结论 | 判定 |
|---|---|
| 换成 Roslyn（现代 C# 编译器） | **技术可行，已端到端实证**（Roslyn 4.14 在游戏进程内编译并执行了 C# 13 特性，含"引用游戏 API"与"Harmony 打补丁"两条真实路径） |
| 只升级 `mcs.dll`（换更新的 Mono.CSharp） | **不可行 / 零收益**——已实测：Unity 2022.3 自带的 Mono.CSharp（比现用 fork 新）语言上限完全相同 |
| 当前控制台语言上限 | **约 C# 7.0–7.2，且带缺口**（无本地函数、无类型模式 switch；C# 8 起全部不支持） |
| 升级代价 | 净增 **约 12.7 MB** 程序集体积、**+53 MB** 工作集；首次编译 **约 3.5 s**（预热后 ≈0.2 s/次）；需重写 REPL / 自动补全 / Hook 编译（估 **6–11 人日**） |
| 推荐路线 | Roslyn 4.x + polyfill + **引用集恒为 21 个本体程序集，一切额外引用靠诊断自愈按需补**，**限定 Mono 配置**，分阶段落地并保留 mcs 回退开关 |

> **勘误（2026-10-10）**：本段"21 个"为当时的本体侧口径；现行实现为 **22**（21 本体 + UE 侧 `UniverseLib.Mono`），见 `ReferenceProfile.SmallProfile`（`src/RoslynBridge/ReferenceProfile.cs`）。历史数字保留不改写。

---

## 2. 现状：当前编译器到底是什么

| 项目 | 实测值 |
|---|---|
| 编译器 | `Mono.CSharp`（`mcs.dll`，程序集名 `mcs, Version=1.0.0.0`，1.54 MB） |
| 来源 | sinai-dev/mcs-unity 的 fork，经 ILRepack 合并进 `UnityExplorer.STANDALONE.Mono.dll` |
| 配置 | `ScriptEvaluator.cs`：`LanguageVersion.Experimental`（枚举上限已含 `V_7_2`） |
| 执行机制 | `Mono.CSharp.Evaluator`：编译进 **动态程序集**（`AssemblyBuilder`，故 `Assembly.Location` 抛 `NotSupportedException`） |
| mcs 被引用的位置 | `ConsoleController` / `ScriptEvaluator` / `ScriptInteraction` / `CSAutoCompleter` / `HookInstance`（5 个文件）+ `UERuntimeHelper`（仅 IL2CPP 的 `Mono.CSharp.IL2CPP.Blacklist`） |

`LanguageVersion.Experimental` 已是该构建的最高档，**没有"改个开关"的提升空间**。

## 3. 语言特性实测矩阵

方法：每条特性单独编译（避免 mcs 语法错误后的"吞掉后续声明"式恢复），在**游戏进程内**探测现用 `mcs.dll`，并在独立进程用 **Unity 2022.3 自带 mcs.exe** 复测对照。两者结论一致。

| 特性 | 版本 | 现用 mcs.dll | Unity 自带 Mono.CSharp | Roslyn 4.14 |
|---|---|---|---|---|
| 元组 / 解构 | 7.0 | ✅ | ✅ | ✅ |
| `out var` / `out _` | 7.0 | ✅ | ✅ | ✅ |
| `is T x` 模式 | 7.0 | ✅ | ✅ | ✅ |
| 字符串插值 | 6 | ✅ | ✅ | ✅ |
| ref 返回 / `ref readonly` | 7.0/7.2 | ✅ | ✅ | ✅ |
| `readonly struct` | 7.2 | ✅ | ✅ | ✅ |
| `private protected` | 7.2 | ✅ | ✅ | ✅ |
| `stackalloc` → `Span<T>` | 7.2 | ✅ | ✅ | ✅ |
| throw 表达式 | 7.0 | ✅ | ✅ | ✅ |
| **本地函数** | 7.0 | ❌ CS1525 | ❌ CS1525 | ✅ |
| **`switch` + 类型模式** | 7.0 | ❌ **ICE**（`NotImplementedException: type pattern matching`） | ❌ 同 ICE | ✅ |
| `unmanaged` 约束 | 7.3 | ❌ CS0246 | ❌ CS0246 | ✅ |
| switch 表达式 | 8 | ❌ | ❌ | ✅ |
| `using` 声明 | 8 | ❌ | ❌ | ✅ |
| 可空引用类型 `string?` | 8 | ❌ | ❌ | ✅ |
| `??=` | 8 | ❌ | ❌ | ✅ |
| 静态本地函数 | 8 | ❌ | ❌ | ✅ |
| 范围 `a[1..]` | 8 | ❌ | ❌ | ✅ |
| 接口默认实现 | 8 | ❌ | ❌ | ✅ |
| `readonly` 成员 | 8 | ❌ | ❌ | ✅ |
| record / `with` | 9 | ❌ | ❌ | ✅ |
| `init` 访问器 | 9 | ❌ | ❌ | ✅ |
| 目标类型 `new()` | 9 | ❌ | ❌ | ✅ |
| `is not` / `and` 模式 | 9 | ❌ | ❌ | ✅ |

> 注意点：现用 fork 认出了 Mono 早期实验性的 **primary constructor** 语法（错误码 `CS9010`），这会让 `x switch {…}` 之类输入报出**误导性错误信息**——对使用者是额外的体验损失。

## 4. Roslyn 路线：已在游戏进程内端到端验证

PoC 位置：`_poc_roslyn/`（`Harness.cs` 为可复现驱动，反射调用 Roslyn，无需编译期引用）。

### 4.1 实测通过的能力

```
mono=4.0.30319.42000  clr=mscorlib 4.0.0.0
span=mscorlib  index=present  isExternalInit=missing
LOAD      System.Numerics.Vectors 4.1.3.0
LOAD      System.Runtime.CompilerServices.Unsafe 6.0.0.0
LOAD      System.Threading.Tasks.Extensions 4.2.0.0
LOAD      System.Memory 4.0.1.1
LOAD      System.Collections.Immutable 8.0.0.0
LOAD      System.Reflection.Metadata 8.0.0.0
LOAD      Microsoft.CodeAnalysis 4.14.0.0
LOAD      Microsoft.CodeAnalysis.CSharp 4.14.0.0

[NoShim]   success=False  CS0518: 未定义 IsExternalInit        ← 预期：需要 polyfill
[WithShim] success=True   bytes=5120
   loaded=WithShim
   Modern.F(1) = one        ← switch 表达式
   Modern.F(2) = two
   Modern.F(9) = many
   Modern.G()  = 105        ← record + with + 属性模式
   Modern.H(1) = set        ← is not
   new R(7,"z") = R { A = 7, B = z }   ← record ToString
```

**即：record、`with`、属性模式、switch 表达式、目标类型 `new`、`is not` 全部在 Unity 2022.3 的 Mono 上编译并成功执行。**

### 4.2 已验证的工程约束（直接决定实现方式）

1. **依赖清单与加载顺序是硬约束**
   `System.Numerics.Vectors` → `System.Runtime.CompilerServices.Unsafe` → `System.Threading.Tasks.Extensions` → `System.Memory` → `System.Collections.Immutable` → `System.Reflection.Metadata` → `Microsoft.CodeAnalysis` → `Microsoft.CodeAnalysis.CSharp`。
   **顺序错误会致命**：先加载 `System.Memory` 时其 typeref 解析失败，Mono 会**缓存这次失败**，之后即使补上 `System.Numerics.Vectors`（版本完全匹配）也仍然抛
   `TypeLoadException: Could not resolve type with token 01000065 ... System.Numerics.Vector`，且**该进程无法恢复**（实测：游戏进程内首次顺序错误后，该进程内 Roslyn 永久不可用，需重启进程）。
   → 实现必须**固定顺序、一次性加载、失败即降级**，不能"失败后重试"。

2. **必须补 polyfill 类型**
   运行时缺失（`Type.GetType(..., System.Runtime)` 实测）：
   - `System.Runtime.CompilerServices.IsExternalInit`（C# 9 `init` / `record` **必需**，已实测为唯一阻塞项）
   - 推及 C# 11+ `required`：`RequiredMemberAttribute` / `CompilerFeatureRequiredAttribute` / `SetsRequiredMembersAttribute`（同类，需 shim）

   运行时**已有**（无需 polyfill，均实测 present）：`Span<T>` / `ReadOnlySpan<T>` / `Memory<T>`（在 **mscorlib**）、`System.Index`、`System.Range`、`System.HashCode`、`IAsyncEnumerable<T>`、`ValueTask`、`Buffers.ArrayPool<T>`。

3. **`Assembly.Load(byte[])` 可用**（实测：Cecil 生成程序集 + Roslyn `Emit` 产物均可加载执行）。
   → 该路线**仅适用于 Mono 配置**；IL2CPP 不支持 `Assembly.Load(byte[])`，故 IL2CPP 配置必须继续走 mcs。RimWorld 是 Mono，UESDdebuger 也只发布 `STANDALONE_Mono`，影响面很小。

4. **字节码体积、时延与内存**（实测）
   - 依赖集 8 个 DLL 合计 **13,321,152 B ≈ 12.7 MB**（vs 现 `mcs.dll` 1.54 MB；`Assemblies/` 现总计 5.57 MB → 约 18 MB）
   - **游戏进程内**加载 8 个依赖：**1407 ms**
   - 首次编译（146 个程序集引用）：**3552 ms**；连续第 2 次起编译+加载 ≈ **200 ms/次**（4 次连续编译+加载共 827 ms）
   - 独立 Mono 进程冷启动（含 Mono 启动）2.56–2.67 s，空进程基线 57 ms
   - **内存增量 = +53 MB 工作集**（18 MB → 71 MB，两次复现；仅 `LoadFrom` 不触发 JIT 时只 +1 MB，成本集中在首次真正使用编译器）

5. **与现有 mcs 控制台共存**（实测，干净进程）
   加载整套 Roslyn 后，mcs 控制台继续正常编译执行（显式探测 mcs 编译无错；此后所有控制台提交均由 mcs 正常编译运行），进程内程序集 159 个。
   ⚠️ 上一轮（**依赖加载顺序错误的进程**）出现过一次不可复现的假错误（`CS1929: StringBuilder 没有 Append`）。本轮正确顺序的干净进程中**未复现**，判断为"错误加载顺序导致的进程级污染"的伴生现象，而非共存本身的问题。
   仍然建议：`ScriptEvaluator.OnAssemblyLoad` **补 try/catch**（当前无异常保护），并让 Roslyn 在控制台初始化前完成加载。

6. **跨 submission 引用与 REPL 状态**（实测）
   - 跨 submission 引用可用：submission 2 可引用 submission 1 编译出的程序集。
   - 注意：`Assembly.Load(byte[])` 载入的程序集 **没有 `Location`**，无法用 `CreateFromFile` 引用，需用 `CreateFromImage`（内存）或先落盘——实测用落盘方式打通。
   - **变量状态跨 submission 保留已验证**：`Run()=6 → Bump() → Run()=16`（会话类静态字段方案）。
     → 迁移**不强制依赖** `Microsoft.CodeAnalysis.CSharp.Scripting` 包（该包不在本机 NuGet 缓存中，需联网拉取）。

7. **许可**
   Roslyn 为 MIT，与 GPL-3.0 的 UnityExplorer 兼容；需在 `THIRD_PARTY_NOTICES.md` 增补 Roslyn + 7 个依赖的声明（项目已有该文件与 `MODIFICATIONS.md` 惯例）。

### 4.3 游戏内端到端复验（干净进程，本次会话实测）

环境：RimWorld 1.6.4871 / Unity 2022.3.35f1 / **mono 6.13.0**，21 个模组，已进入地图（`Map-0-PlayerHome`）。
所有步骤均通过 MCP `execute_csharp_code` 在**运行中的游戏进程内**执行。

| # | 验证项 | 结果 |
|---|---|---|
| 1 | 按正确顺序加载 8 个依赖 | 全部 `OK`，耗时 **1407 ms** |
| 2 | 编译现代 C# 并**调用真实 RimWorld API** | `success=True`，146 个程序集引用，首次 **3552 ms** |
| 3 | 执行结果 | `Info()=map=Map-0-PlayerHome ticks=3363`、`Calc(2)=20`、`Calc(9)=0`（switch 表达式）、`Rec()=8`（record+`with`）、`recToString=ProbeRec { A = 3, B = zz }` |
| 4 | **Harmony 补丁**（Roslyn 编译的方法打真实游戏方法） | 目标 `Int32 Verse.GenTicks.get_TicksGame()`；补丁后 3 次读取 → `hits 0 -> 3 (delta=3)`；`UnpatchAll` 后 `delta=0`，游戏正常（读取 4445） |
| 5 | 跨 submission 引用 | submission2 引用 submission1 编译的程序集：`loaded=True` |
| 6 | **REPL 变量状态跨 submission 保留** | `Run()=6 → Bump() → Run()=16` |
| 7 | mcs 共存 | Roslyn 加载后 mcs 编译**无错误**；此后所有控制台提交均由 mcs 正常编译（游戏内共 8 次提交） |

关键结论：**"Roslyn 编译的代码能引用游戏程序集、能作为 Harmony 补丁作用于游戏方法、能被反复提交并保留状态"这四条迁移核心假设全部在真实游戏进程中成立。**

原始输出（节选）：

```
ROS_COMPILE_BEGIN
roslyn=4.14.0.0 optsParams=22
references=146
compileMs=3552 success=True bytes=5120
Info()=map=Map-0-PlayerHome ticks=3363
Calc(2)=20 Calc(9)=0
Rec()=8 NotNull(null)=False
recToString=ProbeRec { A = 3, B = zz }
ROS_COMPILE_END

ROS_PATCH_BEGIN
patchCompile ok=True bytes=2560
target=Int32 get_TicksGame()
patched hits 0 -> 3 (delta=3) reads=4445,4445,4445
afterUnpatch hits=3 (delta=0) read=4445
ROS_PATCH_END

ROS_STATE_BEGIN
A) mcs after roslyn: errors=[] (empty=OK)
B) submission1 loaded=True
C) submission2 loaded=True (references跨submission)
D) Run()=6 -> Bump() -> Run()=16 (状态跨submission保留)
ROS_STATE_END
```

### 4.4 外部模组实测：把 TEXT20 当第三方模组调参（本次会话实测）

测试对象：`TEXT20`（`text20.flightastar`，飞行寻路模组）——按"外部模组"对待，只从控制台外部 poke 它的参数。
测试源码落盘为 `_poc_roslyn/Text20Defs.cs`，游戏内读取该文件交给 Roslyn 编译执行（文件式工作流）。

**a) 引用自动就位**：现用控制台的"引用所有已加载程序集"策略在 Roslyn 下同样成立，无需白名单——
`TEXT20@...\Mods\TEXT20\1.6\Assemblies\TEXT20.dll`、`TEXT20.Unity@...\TEXT20.Unity.dll` 自动进入 146 个引用。

**b) 强类型读参数（真值）与热调参 + 回滚**

```
1) Dump     : EnableInterception=True factor=0.5 HeightPerTick=0.01 VerticalWaitTicks=120
              | takelandCost=5 nodeBudget=200000 eps=1 heuristic=Octile budgetRange=[250000,2000000]
2) Toggle   : EnableInterception=False        ← 运行时开关，立即生效
3) Toggle   : EnableInterception=True         ← 回滚
4) SetFactor: factor 0.5 -> 0.25              ← 热调参（FlightPawnConfig 热路径字段）
5) Restore  : factor 0.25 -> 0.5              ← 回滚
8) Final    : EnableInterception=True factor=0.5 ... （与初始状态一致，无残留）
```

**c) 读外部模组的持久化 ModSettings（走游戏 API）**

```
7) Handles  : FlightPawn2Framework.PathPlan.FlightAStar.TEXT20Mod persistedFactor=0.5
```

**d) 泛型 Def 数据库盘点 + 字段级参数抽取**

```
vanillaThingDefs=3870 TEXT20DefTypes=5 [WingAAnimationDef|WingAnimationDef|SampleDef|SegmentDef|FlightMappingDef]
  WingAnimationDef=2 [WingFlight_ThreeLayer,WingFlight_ThreeLayer_Static,]
    first=WingFlight_ThreeLayer label=Wing Flight (Three Layer)
      totalFrames = 160        drawSize = (2.00, 2.00)      texPathPrefix = Wings/Wings
      xmlFramePoseDataSouth = MIRROR_NORTH                 xmlFramePoseDataWest = MIRROR_EAST
      currentFrameIndex = 0    defaultHeadScale = (1.00, 1.00, 1.00)
  SampleDef=8 [WingSample_Takeoff,...]      first=WingSample_Takeoff rangeStart=0 rangeEnd=19 tickInterval=1
  SegmentDef=8 [Takeoff,...]               first=Takeoff samples=WingSample_Takeoff mode=Forward direction=Forward
  FlightMappingDef=6 [WingMap_Takeoff,...] first=WingMap_Takeoff phase=Takeoff
                                           modContentPack = text20.flightastar  ← 可反查 Def 归属模组
```

> 注：`DefDatabase<Def>`（基类容器）是空的，必须按具体 Def 类型取 `DefDatabase<T>` —— 这是 RimWorld 的 API 语义，与编译器无关（第一版探测因此得到 `TEXT20Defs=0`，修正后正常）。

**e) 同一份文件的语言能力 A/B**（决定性对照）

| | 结果 |
|---|---|
| **老 mcs 控制台** | `mcsErrors=5`：(63,22) 本地函数 `int SwitchExpr(int x) =>`、(66,14) `using var`、(73,25) `with` … 全部 CS1525 拒绝 |
| **Roslyn 4.14** | `compile ok=True`，执行输出 `switchExpr(2)=20 | usingDecl.ok=True | nullableRef.isEmpty=True | record.with=ProbeRec { A = 9, B = wing }` |

同一份源码，里面就是"读 TEXT20 参数 + 盘点 Def"的业务逻辑——老编译器连编译都过不去。

**f) 顺带发现的两个工程细节**

- Roslyn 诊断带精确行列：本轮真实报出我漏写的 `using System.Collections.Generic;`（`(61,24): error CS0246: 'List<>' could not be found`），定位成本远低于 mcs 的解析恢复式报错。
- 现用控制台里 `Mono.CSharp.*` 类型被 ILRepack `/internalize` 成 internal：REPL 顶层若声明 `Mono.CSharp.CompilerSettings` 这类变量会直接 `CS0052 Inconsistent accessibility`，必须塞进 lambda/块内。这属于现架构的可用性瑕疵，迁移后自然消失。

### 4.5 实参级验证：翅膀 pawn 的段序列与 Burst job 入参（本次会话实测）

对象：已挂翅膀的殖民者（`Hediff_FlightWingTest`，三个殖民者均挂；`animDef=WingFlight_ThreeLayer_Static`，`totalFrames=160`）。
测试源码：`_poc_roslyn/Text20Runtime.cs`（Roslyn 编译，含 record/switch 表达式/别名等现代语法）。

**a) 三层链解析（L3→L2→L1）与覆盖率**

```
phase=Takeoff  seg=Takeoff    sample=WingSample_Takeoff    range=[0,19]   tick=1 Forward  dur=20t
phase=Cruising seg=CruiseStart sample=WingSample_CruiseStart range=[20,39] tick=2 Forward  dur=40t
               seg=Cruising    sample=WingSample_Cruising    range=[40,59] tick=2 Loop     dur=40t
               seg=CruiseEnd   sample=WingSample_CruiseEnd   range=[80,99] tick=2 Forward  dur=40t
phase=Landing  seg=Landing    sample=WingSample_Landing    range=[100,119] tick=1 Forward dur=20t
phase=Falling  seg=Falling    sample=WingSample_Falling    range=[140,159] tick=2 Forward dur=40t
phase=Hovering seg=Hovering   sample=WingSample_Hovering   range=[120,139] tick=3 PingPong dur=60t
phase=Cruising tag=fastCruise seg=FastCruise sample=WingSample_FastCruise range=[60,79] tick=2 Loop dur=40t
COVERAGE: frames=160 min=0 max=159 contiguous0..N=True        ← 8 段窗口恰好无缝覆盖 160 帧
durations: Takeoff=20 Landing=20 CruiseStart=40 CruiseEnd=40 MinCruise=40 Falling=40
GetRoundTripVerticalCost(pawn,def)=40 → takelandCost = 40/4 = 10
```

`FlightComp.BuildSections()` 产出的运行时编排与上面逐条一致（默认 5 相位 + fastCruise 1 相位）。

**b) Burst job 实参（复刻 `FlightPathInjector.SolveAndInject` 的同一套推导，实跑取值）**

```
burstAvailable = True
defaults: takelandCost=5     nodeBudget=200000    PPCPC=1
derived : takelandCost=10    nodeBudget=468750 (ResolveNodeBudget(250,250,10,2000000))    PPCPC=1
speeds  : groundMoveSpeed=4.6   flightCruiseSpeed=0  （无 CruiseSpeedOverride → 速度比分支 → r=1 中性，与源码注释一致）
FlightJobConfig { PawnFlightPowerCostPerCell=1, takelandCost=10, nodeBudget=468750,
                  heuristic=2(Octile), heuristicWeight=1, remainingFuel=0(无限), skyUsagePenalty=0 }
```

要点：`takelandCost` 由**翅膀动画定义**推导（起降 40 tick / 4 = 10），不是静态默认 5；`nodeBudget` 必须在 `takelandCost` 定稿**之后**推导（源码注释强调的次序）——本次实测 468750 vs 静态 200000，正是那条"静态预算落在 TK≈12 悬崖"问题的现场数据。

**c) 用同一份实参真跑求解（Burst 路径）**

```
long   (125,136)->(165,176)  found len=45 airPoints=43 totalCost=96.57 flightCost=96.57
                             nodesExpanded=4408 exceededBudget=False
                             takeoff=(125,136) landing=(165,176)  1.4ms
short  (125,136)->(131,142)  found len=9  airPoints=0  totalCost=31.31 flightCost=0
                             nodesExpanded=198  0.1ms
```

长距离 43/45 个点在 Window/Sky 层、短距离纯地面 —— 参数确实被求解器消费（不是"读了没生效"）。

**d) 段序列的运行时播放（实参级：帧号落在映射窗口内）**

用模组自己的飞行命令入口起飞（`FlightCommandHandler.OrderFlySingle`），随后逐次采样链播放器：

| 采样 | 状态槽 | 链播放器当前段 | pawn 实际帧号 | 对应上方映射 |
|---|---|---|---|---|
| POLL1 | `Phase=Cruising, Segment=FastCruise, IsInAir=True` | `[60..79] tick2 Loop dur=40t` (lastSegment=FastCruise) | **70** ∈ [60,79] | fastCruise 映射 ✓ |
| POLL4 | `Phase=Cruising, Segment=Cruising` | `[40..59] tick2 Loop` **chainIndex=1** | **58** ∈ [40,59] | Cruising 三段链中段 ✓ |
| POLL5 | `Phase=Cruising, Segment=CruiseEnd` | `[80..99] tick2 Forward` **chainIndex=2**, `completed=True` | **99** = 窗口末帧 | `ResolveTargetSection` 的 CruiseEnd→index2 规则 ✓ |
| POLL2/3/6 | `Phase=Hovering` | `[120..139] tick3 PingPong dur=60t` | 127 / 133 / 134 ∈ [120,139] | Hovering 映射 ✓ |

即：**Def → 运行时编排 → 实际播放帧号/节奏三段全部对齐**，且巡航"完整模式"与"fastCruise 模式"两条映射都被现场触发过。
未抓到 Takeoff / Landing 的实时采样（Takeoff 仅 20 tick ≈0.33 s，采样间隔 1–3 s，窗口太窄）——这两段由上面的窗口/时长/覆盖率验证覆盖。

**e) 过程中 Roslyn 报错质量（顺带观察）**

编译期真实报出并可精确定位：`Map.UniqueID` 不存在（应为 `uniqueID`）、`Verse.AnimationDef` 与 TEXT20 `AnimationDef` 同名冲突（需别名）、`JobDefOf` 在 `RimWorld` 而非 `Verse.AI`、`PathEndMode` 在 `Verse.AI`、`FlightSegment` 无 `Takeoff` 成员。这些若用 mcs 的解析恢复式报错，定位成本会高得多。

### 4.6 默认引用与默认 using 的代价实测（本次会话）

**模组程序集来源：必须走模组加载器，不能扫目录**

```
mods=21 withAssemblies=14 modAssemblies=34 | path: workshop=20 local=14 noLocation=0
  brrainz.harmony n=2 [0Harmony,HarmonyMod]
  redeyedev.rimapi n=2 [Newtonsoft.Json,RIMAPI]
  brrainz.rimbridgeserver n=9 [Gabp.Runtime,Lib.GAB,MoonSharp.Interpreter,Newtonsoft.Json,…]
  uesddebuger.debug.unityexplorer n=9 [Mono.Cecil,…,UELoader,UnityExplorer.Standalone.Mono,UniverseLib.Mono]
  text20.flightastar n=2 [TEXT20,TEXT20.Unity]      …（共 14 个模组带程序集）
```

**20/34 个模组程序集在 Steam 创意工坊目录** → 只扫 `<Game>/Mods/*/Assemblies` 会漏掉 59%，必须用
`LoadedModManager.RunningModsListForReading` → `mod.assemblies.loadedAssemblies`。

> API 细节（踩了 3 次）：`ModContentPack.assemblies` 是**字段**（类型 `Verse.ModAssemblyHandler`），
> 程序集列表是它的**字段** `public List<Assembly> loadedAssemblies`。
> `GetProperty("assemblies")` 返回 null（直接 NRE）；`handler.assemblies` 不存在。

**引用数量对编译耗时的影响（同一 snippet，含 `Verse.Find.CurrentMap` 与 switch 表达式）**

```
A(已加载)          147 refs : 499ms / 477ms（两次）
B(+Managed+模组)   253 refs : 555ms / 507ms（两次）
→ 多 106 个引用只增加 +30~56ms（+6~10%），可忽略
```

**命名空间与撞名（全量 using 的真实代价）**

```
本体公共类型数: Verse=1739 RimWorld=5899 Verse.AI=278 RimWorld.Planet=302
                System=276 UnityEngine=721 UnityEngine.UI=61
模组: 公共命名空间 137 个 / 公共类型名 2827 个

★ 默认 using 内部歧义（Verse族 vs System/UnityEngine）= 5 个：
   Keyframe[Verse/UnityEngine]  Graphic[Verse/UnityEngine.UI]  GridLayout[Verse/UnityEngine]
   Text[Verse/UnityEngine.UI]   WeakReference`1[Verse/System]

★ 模组类型与本体核心命名空间重名 = 34 个（若把模组命名空间也全量 using）：
   Coroutine@MoonSharp.Interpreter  FloatRange@RIMAPI.Models  IServiceProvider@RIMAPI.Core
   StackTraceUtility@Analyzer.Profiling  EventAttributes/FieldAttributes/MethodAttributes/
   ParameterAttributes/ICustomAttributeProvider@Mono.Cecil …
★ 模组之间重名 = 19 组（Extensions×4、Settings、Code、TextureHelper、GabpError…）
```

**结论**：**引用可以放心加**（+6~10% 编译耗时）；但**定稿仍选 Small 档**（21 个，元数据开销更小、且残差已归零），Full 作为可选档。**using 才是需要克制的那个**——
默认只加本体核心（代价 5 个低频歧义名），模组命名空间走按需添加，否则 34+19 组重名会制造新的"AI 每次都得试一下"。

**引用范围实验：要不要收窄到 `Assembly-CSharp.dll`？（实测）**

同一 snippet，三组引用集（`narrow` = mscorlib/netstandard/System.dll/System.Core.dll/Assembly-CSharp.dll，共 5 个）：

| snippet | narrow(5) | +UnityEngine(9) | full(106) |
|---|---|---|---|
| `Verse.GenTicks.TicksGame` | ✅ | ✅ | ✅ |
| `new UnityEngine.Vector3(1,2,3)` | ❌ CS0246 | ✅ | ✅ |
| `Verse.Find.CurrentMap.mapPawns.FreeColonists[0].DrawPos`（**游戏自己的 API**） | ❌ CS0246 | ✅ | ✅ |
| `typeof(Verse.PathFinder.UnmanagedGridTraverseParams)` | ✅ | ✅ | ✅ |
| `typeof(Verse.PathFinderCostTuning)` | ✅ | ✅ | ✅ |
| `Unity.Collections.NativeArray<int>` | ❌ CS0234 | ✅（在 CoreModule 里） | ✅ |
| `Unity.Mathematics.int2` | ❌ CS0234 | ❌ CS0234 | ✅ |
| `Verse.PathFinder.ScheduledPathJob` | ❌ CS0122（非 public） | ❌ 同 | ❌ 同 |

结论：**收窄到 Assembly-CSharp.dll 不可行**——连游戏自己的 `pawn.DrawPos` 都编不过（`DrawPos` 返回 `UnityEngine.Vector3`，而 `Assembly-CSharp.dll` 里 `contains UnityEngine.Vector3 == False`，其实测公共类型 9382 个，全是游戏代码不含 Unity 类型）。全集只多 **+30~56 ms**，没有收窄的理由。

**覆盖面普查：四档引用集到底能覆盖多少（实测）**

对 `Assembly-CSharp` 里 `Verse`/`RimWorld` 命名空间的公共非嵌套类型（样本 1200 个）逐成员检查"签名是否引用了引用集之外的程序集"：

| 引用集 | 类型至少有 1 个成员被卡 | 成员被卡 | 仍缺的程序集（Top） |
|---|---|---|---|
| 5 引用（mscorlib/netstandard/System/System.Core/Assembly-CSharp） | **281/1200 = 23%** | **1001/10658 = 9%** | `UnityEngine.CoreModule`=853、`mscorlib`=60、`System.Xml`=59、`Unity.Collections`=17 |
| 6 引用（+`Assembly-CSharp-firstpass`） | 23% | 9%（差 1 个成员） | 同上 |
| **9 引用（+`UnityEngine.dll`/`CoreModule`/`IMGUIModule`/`UI`）** | **38/1200 = 3%** | **91/10658 = 0.85%** | `System.Xml`=59、`Unity.Collections`=17、`mscorlib`=6 |
| 14 引用（再加 Text/Physics/Animation/Collections/Mathematics） | 33/1200 = 2% | 74/10658 = 0.7% | `System.Xml`=59、`mscorlib`=6 |

**只引 Assembly-CSharp 时成片失效的成员**（原始数据）：

```
Verse.Widgets        152/191 卡  例: BeginGroup(arg:Rect)
Verse.GenDraw         37/52  卡  例: DrawLineBetween(arg:Vector3)
Verse.MaterialPool    12/12  卡  例: MatFrom(ret:Material)
Verse.UI              10/18  卡  例: GUIToScreenPoint(ret:Vector2)
Verse.IntVec3          9/47  卡  例: ToVector2(ret:Vector2)
Verse.Thing            9/155 卡  例: DrawNowAt(arg:Vector3)
Verse.Pawn             3/288 卡  例: DrawShadowAt(arg:Vector3)
Verse.Find             4/86  卡  例: Camera(ret:Camera)
Verse.PathFinder       0/15  ✅   RimWorld.StatDefOf 0/217 ✅   Verse.GenSpawn 0/16 ✅
```

→ 一句话：**逻辑/数据 API 基本能用，渲染/UI/绘制/数学转换类成员成片不可用**；而这正是调试时最常用的（画圈、读 DrawPos、读材质、开 UI 组）。结论仍是"不要收窄"，但若非要省引用，**甜点是 9~14 个**（覆盖 99%+），不是 1 个。

**逼近最小引用集（逐档收敛，实测）**

| 档 | 类型被卡 | 成员被卡 | 残差（仍缺的程序集） |
|---|---|---|---|
| 9 引用 | 39/1200 (3%) | 92/10658 (0.86%) | `System.Xml`=59、`Unity.Collections`=17、`mscorlib`=6、`System.Xml.Linq`=5、`steamworks`=3 |
| **13 引用** | **4/1200 (0.33%)** | **5/10658 (0.05%)** | `steamworks`=3、`Assembly-CSharp-firstpass`=1、`mscorlib`=1 |
| 16 引用 | 3/1200 | 4/10658 | `steamworks`=3、`mscorlib`=1 |
| 20 引用 | 1/1200 | 1/10658 | `mscorlib`=1 |

**13 引用 = 最小可用集**（成员覆盖 **99.95%**）：

```
mscorlib.dll            netstandard.dll         System.dll               System.Core.dll
System.Xml.dll          System.Xml.Linq.dll     Assembly-CSharp.dll
UnityEngine.dll         UnityEngine.CoreModule.dll
UnityEngine.IMGUIModule.dll                     UnityEngine.UI.dll
Unity.Collections.dll   UnityEngine.AudioModule.dll
```

再加 `Unity.Mathematics.dll`（=14）覆盖 Job/Burst/寻路代码。剩下 5 个成员不可用的都是冷门：`ParseHelper.PublishedFileId_t` / `ModMetaData.PublishedFileId_t`（Steamworks）、`ModAssetBundlesHandler.List<T>`、`Current.ColorCorrectionCurves`（firstpass）。

**定稿选择：引用集恒为 21 个本体程序集，一切额外引用靠诊断自愈按需补**（不设 Full 档）。理由不是"避免本体冲突"（下面实测否定），而是**元数据开销可控 + 零维护清单**：没列到的程序集只在你真正用到时才被加进来。

补一条实测：**本体程序集在游戏启动后是 106/106 全部已加载的**（修正路径正/反斜杠比较后测得），所以"引用已加载程序集"这一源天然等于全集——实现时必须**按 `Location` 是否在本体目录下过滤**，否则会把同样"已加载"的模组程序集一起带进来。

Small 档的 21 个（含 `Assembly-CSharp-firstpass`）：`mscorlib`、`netstandard`、`System`、`System.Core`、`System.Xml`、`System.Xml.Linq`、`Assembly-CSharp`、`Assembly-CSharp-firstpass`、`UnityEngine`、`UnityEngine.CoreModule`、`UnityEngine.IMGUIModule`、`UnityEngine.UI`、`UnityEngine.AudioModule`、`UnityEngine.TextRenderingModule`、`UnityEngine.PhysicsModule`、`UnityEngine.AnimationModule`、`UnityEngine.ParticleSystemModule`、`UnityEngine.AssetBundleModule`、`Unity.Collections`、`Unity.Mathematics`、`com.rlabrecque.steamworks.net`。

**残差归零的那一条（含一次自我纠错）**：20 档时唯一残留是 `Verse.ModAssetBundlesHandler.loadedAssetBundles : List<UnityEngine.AssetBundle>`——缺 `UnityEngine.AssetBundleModule`（先前误记为 `@mscorlib`，那是 `List<>` 的定义程序集）。补上后残差 0，自愈退化为纯保险。

**缺引用能不能现场查？（实测：能，且能自动补）**

```
5 引用下取本体 DrawPos:
    FAIL (1,106): error CS0012: The type 'Vector3' is defined in an assembly that is not referenced.
                  You must add a reference to assembly 'UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'.
    ← 缺哪个程序集，诊断里直接写着

5 引用下直接写 UnityEngine.Time:
    FAIL (1,58): error CS0103: The name 'UnityEngine' does not exist in the current context
    ← 这种不带程序集名，需按命名空间前缀反查（只读元数据扫 Managed 目录）

自动补 UnityEngine.CoreModule 后重试第一段:
    OK
```

→ **引用集不必"先猜对"**：默认 Small 档（21 个）已实测残差归零，缺引用时还能由诊断驱动自动补齐并重试（计划中 `任务 13：引用自愈`）。

**`Assembly-CSharp-firstpass.dll` 是什么（实测）**：61 个公共类型，命名空间**只有** `UnityStandardAssets.ImageEffects`（`AAMode`/`Antialiasing`/`Bloom`/`Blur`/`DepthOfField`…），是 Unity 标准资源的后处理效果，**与 RimWorld 游戏逻辑无关**。引不引几乎无差别（成员被卡 1001→1000）。它属于"Managed 全集"的一部分——**全集方案天然包含，不会漏**；只有"只引 Assembly-CSharp"的收窄方案才会漏掉它。

**撞名的归因（重要）**：撞名**只由 `using` 组合决定，与引用多少程序集无关**。默认 using 的 5 个歧义名（`Keyframe/Graphic/GridLayout/Text/WeakReference<T>`）**全部来自本体内部**（`Verse` ↔ `UnityEngine`/`System`），**模组贡献 0** → "不引用模组"对撞名没有任何改善；真正要克制的是"不要把模组命名空间塞进 using"。

**本体寻路 / Burst 在哪里（实测）**

- **全在 `Assembly-CSharp.dll`**：`Verse.PathFinder`、`Verse.PathFinderCostTuning`、`Verse.PathFinderJob`（+`CalcNode`/`ResultData`）、`Verse.PathFinderMapData`。
- 本体确实用 Burst，且**托管侧的 DirectCall 包装也在 Assembly-CSharp.dll**：
  `RimWorld.MapGenUtility.ComputeLargestRects_0000B773$BurstDirectCall`、`RimWorld.Planet.FastTileFinder.ComputeQueryJob.SphericalDistance_00014F7E$BurstDirectCall`、`RimWorld.Planet.PlanetLayer.{CalculateAverageTileSize,IntGetTileSize,IntGetTileCenter}_*$BurstDirectCall`、`$BurstDirectCallInitializer`。
- ⚠️ **嵌套 Job 类型不可访问**：`Verse.PathFinder.ScheduledPathJob` 实测 `CS0122: inaccessible due to its protection level` → 控制台里想观察这些内部 Job 只能走反射（或 hook 外部可见入口）。
- Burst 的**原生编译产物是 native 代码**（例如 TEXT20 的 `1.6/Burst/text20_burst_win64.dll`），Roslyn 能编译/调用的是**托管面**：`[BurstCompile]` 方法的托管签名 + DirectCall 蹦床。

## 5. 方案对比

| 方案 | 语言上限 | 体积 | 改造量 | 判定 |
|---|---|---|---|---|
| **A. 升级 `mcs.dll`** | C# 7.0–7.2（不变） | ±0 | 极小 | ❌ **已实测零收益**，不做 |
| **B. 换 Roslyn 4.x** | C# 13（Roslyn 4.14） | +12.7 MB | 中（6–11 人日） | ✅ **推荐** |
| **C. 双轨（mcs 自动补全 + Roslyn 执行）** | C# 13 | +12.7 MB | 大（两套语义模型） | ⚠️ 不推荐；仅"Roslyn 主 + 可开关回退 mcs"这一变体值得保留 |

方案 A 的关键证据：Unity 2022.3 自带 `Mono.CSharp.dll`（1.35 MB，`MonoBleedingEdge/lib/mono/4.5/`）逐特性实测结果与现用 fork **完全一致**（同样无本地函数、同样 `case int i:` ICE、同样无任何 C# 8 特性）。Mono 官方 `Mono.CSharp` 已停止演进，**这条路已经走到头**。

## 6. 需要改造的代码面（工作量清单）

| 文件 | 依赖 mcs 的部分 | 改造内容 | 估算 |
|---|---|---|---|
| `CSConsole/ScriptEvaluator.cs` | `Evaluator`/`CompilerContext`/`StreamReportPrinter`/`ReferenceAssembly` | 抽出 `IScriptEvaluator`；新增 Roslyn 实现（`CSharpCompilation` + `Emit` → `Assembly.Load`） | 1–2 天 |
| `CSConsole/ConsoleController.cs` | `Evaluator.Compile`、`CompiledMethod.Invoke`、`_reportPrinter.ErrorsCount` | 编译/执行/诊断输出改写；REPL 结果与"定义类"两条路径重接 | 1–2 天 |
| REPL 状态 | mcs 内置（变量跨次保留） | 用 `Microsoft.CodeAnalysis.CSharp.Scripting`（`CSharpScript`/`ScriptState`）或自建 submission 链 | 1–2 天 |
| `CSConsole/ScriptInteraction.cs` | 继承 `InteractiveBase`；`GetUsing/GetVars`；反射 `Evaluator.source_file` + `CompilationSourceFile.Containers` 实现 `GetClasses` | Roslyn 版 REPL 助手（globals/host object）+ 变量/类清单 | 0.5–1 天 |
| `CSConsole/CSAutoCompleter.cs` | `Evaluator.GetCompletions` | 用语法树 + 语义模型自建补全（**不要**引入 Workspaces/Features，会再 +12 MB 且需 MEF 组合） | 1–2 天 |
| `Hooks/HookInstance.cs` | 静态 `ScriptEvaluator` 编译 Harmony patch 类 + 反射取 `TypeDefinition.Definition` | Roslyn 编译 patch 类 → `Assembly.Load` → `GetType/GetMethod`（实现反而更干净） | 0.5–1 天 |
| `CSConsole/Lexers/*`、`LexerBuilder` | 无 mcs 依赖 | 仅需补 C# 8+ 关键字（`record`/`init`/`required`/`with` 等） | 0.5 天 |
| 打包/许可 | — | 8 个 DLL 固定顺序加载、可选 ILRepack 合并、`THIRD_PARTY_NOTICES.md` | 0.5–1 天 |
| 回归验证 | — | 游戏内 + MCP `execute_csharp_code` 端到端 | 1–2 天 |

> 说明：上表为**估算**（未实测人日）。UI/补全若要达到"IDE 级"体验会显著超出该范围；若只求"能编译现代 C#"，核心是前 4 行（约 3–6 人日）。

## 7. 建议实施步骤

1. **Phase 0 — 打包与加载器**：把 8 个依赖固定顺序加载（懒加载 + 后台预热，避免 1.4 s 加载 + 3.5 s 首次编译阻塞 UI），失败时保留 mcs 路径。**必须**先于任何 Roslyn 调用完成，且不允许失败重试。
2. **Phase 1 — 编译器内核**：`IScriptEvaluator` 抽象 + `RoslynScriptEvaluator`；引用来源 = 已加载程序集的 `Location` 自动收集（与现行为一致，外部模组自动可用）；注入 `IsExternalInit` 等 polyfill 源；跨 submission 引用用 `CreateFromImage`（内存）或落盘；`ConsoleController.Evaluate` 走新实现，配置项 `使用 Roslyn 控制台`（默认开，可回退）。
3. **Phase 2 — REPL 语义**：变量跨次保留、`GetVars/GetUsing/GetClasses`。
4. **Phase 3 — 自动补全**：语义模型版补全 + 关键字表更新。
5. **Phase 4 — Hook 编译迁移**（`HookInstance`）。
6. **Phase 5 — 打包/合规**：体积策略（是否需要 ILRepack/压缩分发，参考 `bundle-env.ps1` 里 node.exe 超工坊体积限制的处理惯例）、`THIRD_PARTY_NOTICES.md` / `MODIFICATIONS.md`。

**验收标准**
- 第 3 节矩阵中 C# 8/9 各行在控制台内可编译执行（record + `with` + switch 表达式用例为准）
- MCP `execute_csharp_code` 既有调用（C# 7 语法）无回归
- 控制台首次可用时间增量 ≤ 3 s（可后台预热摊平）
- 关闭 Roslyn 开关时，行为与当前版本一致

## 8. 风险清单

| 风险 | 说明 | 缓解 |
|---|---|---|
| **依赖加载顺序** | 顺序错误会使 Mono 缓存解析失败，**进程内永久不可用**（已实测） | 固定顺序、初始化前完成、失败即回退 mcs、不重试 |
| 依赖版本漂移 | PoC 用 Immutable/Metadata 8.0.0（Roslyn 4.14 声明需 9.0.0），在 Mono 宽松绑定下可用；换严格运行时会失败 | 正式落地时按 nuspec 取精确版本（Immutable/Metadata 9.0.0、Memory 4.5.5 等） |
| 冷启动 2.5 s | 首次编译卡顿 | 后台预热 / 首次使用提示 |
| 体积 +12.7 MB / 内存 +53 MB | 工坊分发可能超限；常驻内存开销需评估 | ILRepack 合并 + 压缩分发（沿用现有 node.zip 思路）；懒加载 + 后台预热 |
| mcs 与 Roslyn 同进程 | 干净进程实测可共存（8 次提交无异常）；仅在**依赖加载顺序错误**的进程里出现过污染性假错误；`OnAssemblyLoad` 无异常保护 | 给钩子加 try/catch；固定顺序加载；Roslyn 在控制台初始化前装好 |
| IL2CPP 配置 | `Assembly.Load(byte[])` 不可用 | 仅 Mono 配置启用 Roslyn；IL2CPP 继续 mcs（`#if CPP` 分支） |
| 补全体验回退 | 自建补全弱于 mcs 的 `GetCompletions` | 先做"成员/命名空间/关键字"三来源补全，后续再迭代 |

## 9. 复现与证据

```powershell
# 1) 编译 PoC 驱动（Unity 自带的 Mono/mcs）
$mb = "C:\Program Files\Unity 2022.3.35f1\Editor\Data\MonoBleedingEdge"
& "$mb\bin\mono.exe" "$mb\lib\mono\4.5\mcs.exe" -out:_poc_roslyn\Harness.exe _poc_roslyn\Harness.cs
# 2) 运行（加载顺序在 Harness.LoadAll 内固定）
& "$mb\bin\mono.exe" _poc_roslyn\Harness.exe "$PWD\_poc_roslyn\"
```

| 产物 | 说明 |
|---|---|
| `_poc_roslyn/Harness.cs` | Roslyn 端到端 PoC（反射驱动：解析 → 编译 → Emit → `Assembly.Load` → 执行） |
| `_poc_roslyn/*.dll` | PoC 依赖集（含 `System.Numerics.Vectors 4.1.3.0`） |
| `_poc_roslyn/Probe.cs` / `Probe2.cs` | 语言特性探针源码（矩阵数据来源） |
| `_poc_roslyn/Text20Defs.cs` | 外部模组（TEXT20）参数盘点测试源码：Roslyn 编译的现代 C#，文件式工作流 |
| `_poc_roslyn/Text20Runtime.cs` | 实参级验证源码：段序列解析 + Burst job 入参复刻 + 真跑求解 + 状态槽注入/起飞/落地 |
| `Source/.../lib/net35/mcs.dll` | 现用编译器（1.54 MB，程序集名 `mcs`） |
| `C:\Program Files\Unity 2022.3.35f1\Editor\Data\MonoBleedingEdge\lib\mono\4.5\Mono.CSharp.dll` | 对照用"更新的 Mono.CSharp"（1.35 MB） |

> `_poc_roslyn/` 为本次评估新增的临时目录，不属于模组发布内容，可整体移入回收站。

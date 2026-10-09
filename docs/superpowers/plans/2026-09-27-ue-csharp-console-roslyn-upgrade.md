# UE C# 控制台 Roslyn 编译器升级 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 把 `UESDdebuger` 内嵌 UnityExplorer 的 C# 控制台从 `Mono.CSharp`（语言上限≈C# 7.2，无本地函数/无 C# 8+）换成 **Roslyn 4.14**，让控制台能编译执行到 C# 13，同时保留 mcs 作为可开关回退。

**架构：** 新增 `UnityExplorer.RoslynBridge` 命名空间（依赖加载器 / 引用集合 / 编译内核 / 诊断 / polyfill / REPL 会话 / 求值器门面），通过新的 `IScriptEvaluator` 抽象与既有 `ScriptEvaluator`(mcs) 并列；`ConsoleController` 只面向抽象，用配置项 `CSharp Console Use Roslyn` 切换，Roslyn 不可用时自动回退 mcs。Roslyn 依赖 8 个 DLL 作为**载荷随包分发**（`lib/roslyn/`），**不**用 NuGet 还原（本机缓存缺 Immutable/Metadata 9.0.0 等，离线还原会失败）。

**技术栈：** C#（net35 目标框架，`LangVersion 10.0`）、Microsoft.CodeAnalysis.CSharp 4.14.0（netstandard2.0 构建）、Unity 2022.3.35f1 的 Mono 6.13、RimWorld 1.6.4871、测试用 net472 控制台工程 + 手写 `Check()` runner（沿用 `Source/HotReloadTests` 约定）。

---

## 0. 前置事实与定稿决策（已实测，不要重新摸索）

> **全部原始输出与逐档实验数据在 `docs/csharp-console-compiler-upgrade-feasibility.md`**：语言上限矩阵见其 §3、工程约束见 §4.2、游戏内端到端复验见 §4.3、外部模组实参级验证见 §4.4/§4.5、**引用集实验（四档覆盖/最小集/自愈）见 §4.6**。
> 本节只保留**执行本计划时必须知道**的结论；表里凡是写"实测"的，都不要在执行中凭直觉改掉。

来源：`docs/csharp-console-compiler-upgrade-feasibility.md`（含全部原始输出）。

| 事实 | 值 |
|---|---|
| 现状编译器 | `lib/net35/mcs.dll`（程序集名 `mcs`），ILRepack 合并进 `Assemblies/UnityExplorer.STANDALONE.Mono.dll` |
| 现状上限 | C# 7.0–7.2 且有缺口（**无本地函数**、**`case int i:` 直接 ICE**、无 `unmanaged`、C# 8+ 全无） |
| 「只升级 mcs.dll」 | **零收益**（Unity 自带的新 Mono.CSharp 上限完全相同）——不要做 |
| Roslyn 可用性 | 游戏进程内实测通过：record/`with`/switch 表达式/属性模式/目标类型 `new`/`is not` 全部编译并执行成功 |
| 依赖加载顺序 | `Vectors → Unsafe → Tasks.Extensions → Memory → Immutable → Metadata → CodeAnalysis → CodeAnalysis.CSharp`；**顺序错误会让 Mono 缓存类型解析失败，进程内不可恢复** |
| 必需 polyfill | `System.Runtime.CompilerServices.IsExternalInit`（record/`init`）；C# 11 `required` 需 `RequiredMemberAttribute` 等 |
| 运行时已有（勿 polyfill） | `Span/Memory/ReadOnlySpan`（在 mscorlib）、`Index`、`Range`、`HashCode`、`IAsyncEnumerable`、`ValueTask`、`ArrayPool` |
| `Assembly.Load(byte[])` | 可用（Mono）。IL2CPP 不可用 → 本计划**只针对 Mono 配置**（`STANDALONE_Mono`，RimWorld 用的就是它） |
| 性能 | 加载 1407 ms；首次编译 3552 ms；预热后 ≈200 ms/次；工作集 +53 MB |
| 载荷体积 | 8 个 DLL ≈ 12.7 MB |
| 游戏程序集 | `RimWorldWin64_Data/Managed` 共 **106 个 dll / 40.1 MB**（`Assembly-CSharp.dll`、`mscorlib.dll`、`netstandard.dll`、`System.*` 19 个、`UnityEngine*` 69 个、`Unity.*` 9 个、其余 8 个） |
| **缺引用可以现场查（任务 13 的依据）** | 编译诊断直接给出缺失程序集名（`CS0012 … You must add a reference to assembly 'UnityEngine.CoreModule, Version=…'`），补上重试即通过（**闭环已实测**）；`CS0103` 不带程序集名 → 按命名空间只读元数据反查。明细见报告 §4.6 |
| **模组程序集来源（实测）** | 只能用**模组加载器汇报**，不能扫目录：21 个运行中模组 / 14 个带程序集 / **33 个模组程序集**，其中 **20 个在工坊目录、14 个在 `Mods/` 下 → 只扫 `Mods/` 会漏掉 59%**（该路径**只在自愈需要解析模组类型时**作为候选源使用，不预先加入引用集） |
| **引用集覆盖度与最小集（实测，明细见报告 §4.6）** | 只引 `Assembly-CSharp`(+mscorlib/netstandard/System/System.Core) → 类型被卡 **23%**、成员被卡 **9%**（`Widgets` 152/191、`GenDraw` 37/52、`MaterialPool` 12/12…渲染/UI/绘制成片失效）；逐档收敛后 **21 档残差 0**。结论：**不要收窄到 Assembly-CSharp**，也不要手工"猜"一个最小集 —— 用 SmallProfile + 自愈 |
| **`Assembly-CSharp-firstpass.dll` 是什么（实测）** | 61 个公共类型，命名空间**只有** `UnityStandardAssets.ImageEffects`（AAMode/Antialiasing/Bloom/Blur/DepthOfField…），是 Unity 标准资源后处理效果，**与 RimWorld 逻辑无关**；引不引几乎无差别（成员被卡 1001→1000）。它属于"Managed 全集"的一部分，**全集方案天然包含、不会漏**；只有"只引 Assembly-CSharp"那种收窄方案才会漏掉它 |
| **撞名的归因（关键）** | 撞名**只由 using 组合决定，与引用多少个程序集无关**。默认 using 里那 5 个歧义名（`Keyframe/Graphic/GridLayout/Text/WeakReference<T>`）**全部来自本体内部**（Verse ↔ UnityEngine/System），**模组贡献 0** → 不引用模组对撞名毫无改善 |
| **本体寻路 / Burst 位置（实测）** | **都在 `Assembly-CSharp.dll` 里**：`Verse.PathFinder`、`PathFinderCostTuning`、`PathFinderJob`+`CalcNode`/`ResultData`、`PathFinderMapData`；本体 `[BurstCompile]` 的 DirectCall 包装也在（`RimWorld.MapGenUtility.ComputeLargestRects_0000B773$BurstDirectCall`、`RimWorld.Planet.FastTileFinder.ComputeQueryJob.SphericalDistance_00014F7E$BurstDirectCall`、`RimWorld.Planet.PlanetLayer.*$BurstDirectCall`、`$BurstDirectCallInitializer`）。**但嵌套 Job 类型不可访问**：`Verse.PathFinder.ScheduledPathJob` 实测 `CS0122`（非 public）→ 要碰它得走反射；Burst 的**原生产物是 native 代码**，Roslyn 只能编译/调用托管面 |
| **本体程序集"启动即拉满"（实测，重要）** | 修正路径比较口径后：`Managed` **106 个 dll 全部已在进程里加载（106/106，未加载 0）**。→ "引用已加载程序集"这一源**天然等于本体全集**；也说明之前用 `Assembly.LoadFrom` 批量灌本体 dll 是多余且危险的操作（见 §3.1）。**实现要点：按 `Location` 是否位于本体 `Managed` 目录来过滤**，否则会把模组程序集（也是"已加载"）一起带进来（定稿后编译期已不扫已加载程序集，本要点仅作历史实测记录；相关开关为 `C# Console Allow Mod Type Resolution`） |
| **引用决策（定稿：单一小集 + 全部额外靠诊断触发）** | **没有档位、没有 Full**：编译期引用集**恒为 `SmallProfile` 的 21 个本体程序集**（从 `Managed` 解析；不在磁盘上的跳过）。**不预设任何其它引用**——不扫"已加载程序集"、不预先加模组。一切额外引用（未列出的 Unity 模块、`Unity.Mathematics` 之外的东西、模组类型、`System.*` 其余件）**全部由编译诊断触发自愈补入**（任务 13）。<br>**21 个**：`mscorlib`、`netstandard`、`System`、`System.Core`、`System.Xml`、`System.Xml.Linq`、`Assembly-CSharp`、`Assembly-CSharp-firstpass`、`UnityEngine`、`UnityEngine.CoreModule`、`UnityEngine.IMGUIModule`、`UnityEngine.UI`、`UnityEngine.AudioModule`、`UnityEngine.TextRenderingModule`、`UnityEngine.PhysicsModule`、`UnityEngine.AnimationModule`、`UnityEngine.ParticleSystemModule`、`UnityEngine.AssetBundleModule`、`Unity.Collections`、`Unity.Mathematics`、`com.rlabrecque.steamworks.net` |
| **模组类型怎么用（同上是自愈驱动）** | 模组程序集**不进引用集**；它们只作为**自愈器的解析候选**（`ReferenceScanner.ModAssemblyPaths()`，由模组加载器汇报，覆盖工坊）。写成 `TEXT20.Foo` 时 → 诊断报缺 → 自愈去已加载程序集/候选表里找 → 补上重试。配置项 `C# Console Allow Mod Type Resolution`（默认 **true**）只用于"彻底不许碰模组"的场景（关掉=自愈只解析本体程序集） |
| **Small 档残差的真相（实测，含一次自我纠错）** | 早先"5 个不可达成员"是**跨档位混算**的结果：`ParseHelper/ModMetaData.PublishedFileId_t`（Steamworks）、`Current.ColorCorrectionCurves`（firstpass）在 9/13/16 档就被覆盖了；20 档真正的残差**只有 1 条**——`Verse.ModAssetBundlesHandler.loadedAssetBundles : List<UnityEngine.AssetBundle>`，缺的是 **`UnityEngine.AssetBundleModule`**（我先前把它误读成 `@mscorlib`，那是 `List<>` 的定义程序集，属打印口径问题）。它**是原版程序集**（在 `Managed` 里）→ **加进 Small 档（=21）后残差归零**，自愈退化为纯保险 |
| **"全集会不会冲突"实测（8 片段 × 2 档，错误码+警告码都查）** | 20 引用与全集 106 **双档 8/8 全过、零 error 零 warning**，含最易撞的 facade 类型（`System.WeakReference<T>`、`Tasks.Task.CompletedTask`、`Marshal.SizeOf`）→ **本体内部未见引用冲突**。**真正的重名冲突在模组侧**：实测 `0Harmony` 出现在 2 个模组、`Newtonsoft.Json` 出现在 2 个模组（RIMAPI + RimBridgeServer）→ 同时引用会 CS0433/CS1704，由"按 simple name 去重、先到先得"处理。<br>→ 选 Small 档的理由是**体积/元数据开销与编译速度**，不是"避免本体冲突"（附注：自愈器**不得**对 CS0433/CS1704 生效，见任务 13） |
| **模组程序集的正确 API（踩过 3 次坑）** | `Verse.ModContentPack.assemblies` 是**字段**（不是属性），类型 `Verse.ModAssemblyHandler`；真正的列表是它的**字段** `public List<Assembly> loadedAssemblies`。即 `mod.assemblies.loadedAssemblies`。三个坑：`GetProperty("assemblies")` 返回 null（NRE）；`h.assemblies` 不存在；猜测成员名会连着编译失败 |
| **引用数量对编译耗时的影响（实测）** | 同一 snippet：147 refs = 477–499 ms；**253 refs（+Managed 全集 +模组）= 507–555 ms → 只多 +30~56 ms（+6~10%）**，可忽略 |
| **命名空间与撞名（实测）** | 本体公共类型：`Verse`1739 / `RimWorld`5899 / `Verse.AI`278 / `RimWorld.Planet`302 / `System`276 / `UnityEngine`721；模组公共命名空间 **137** 个、公共类型名 2827 个 |
| **默认 using 加 `Verse/RimWorld` 的代价（实测）** | 与 `System`/`UnityEngine`/`UnityEngine.UI` 撞名的**只有 5 个**：`Keyframe`、`Graphic`、`GridLayout`、`Text`、`WeakReference<T>` → 可接受（低频 GUI/GC 类型，需要时全限定即可） |
| **"模组命名空间全量 using" 的代价（实测）** | **34 个模组类型与本体核心命名空间重名**（`Coroutine@MoonSharp`、`FloatRange@RIMAPI`、`IServiceProvider@RIMAPI`、`StackTraceUtility@Analyzer`、`EventAttributes/FieldAttributes/MethodAttributes…@Mono.Cecil`…）**+ 19 组模组之间重名**（`Extensions`×4、`Settings`、`Code`、`TextureHelper`…）→ **不要默认全量 using 模组命名空间**，会制造新的"每次都得试一下" |
| 控制台当前默认 using | `System, System.Linq, System.Text, System.Collections, System.Collections.Generic, System.Reflection, UnityEngine, UniverseLib`（**无 `Verse`/`RimWorld`** → 裸 `GenTicks` 报 CS0103，这是「控制台一开是空的」的真正原因） |

**已验证的 Roslyn 载荷（固定 8 件套）**

| 文件 | 版本 | 来源 |
|---|---|---|
| `Microsoft.CodeAnalysis.dll` | 4.14.0 | NuGet `microsoft.codeanalysis.common/4.14.0/lib/netstandard2.0` |
| `Microsoft.CodeAnalysis.CSharp.dll` | 4.14.0 | NuGet `microsoft.codeanalysis.csharp/4.14.0/lib/netstandard2.0` |
| `System.Collections.Immutable.dll` | 8.0.0 | NuGet `system.collections.immutable/8.0.0/lib/netstandard2.0` |
| `System.Reflection.Metadata.dll` | 8.0.0 | NuGet `system.reflection.metadata/8.0.0/lib/netstandard2.0` |
| `System.Memory.dll` | 4.5.3 | NuGet `system.memory/4.5.3/lib/netstandard2.0` |
| `System.Runtime.CompilerServices.Unsafe.dll` | 6.0.0 | NuGet `system.runtime.compilerservices.unsafe/6.0.0/lib/netstandard2.0` |
| `System.Threading.Tasks.Extensions.dll` | 4.5.2 | NuGet `system.threading.tasks.extensions/4.5.2/lib/netstandard2.0` |
| `System.Numerics.Vectors.dll` | 4.1.3.0 | 取 NuGet 包 **`system.numerics.vectors/4.4.0`** 的 `lib/net46`（实测该包=程序集 4.1.3.0，与 PoC 一致；**4.5.0 包是 4.1.4.0、与 PoC 实测不符，不要用**。可行性验证时曾临时用 VS 安装目录的副本，不可再分发） |

---

## 1. 文件结构

### 新建

| 文件 | 职责 |
|---|---|
| `Source/UE-4.9.0-official/UnityExplorer-4.9.0/lib/roslyn/*.dll` | 8 件套载荷（入库，随包分发） |
| `Source/UE-4.9.0-official/UnityExplorer-4.9.0/lib/roslyn/VERSIONS.md` | 记录载荷精确版本与来源（换版本必须整套重验） |
| `src/RoslynBridge/RoslynAssemblyLoader.cs` | 固定顺序加载 + 健康标志 + 只尝试一次 |
| `src/RoslynBridge/ReferenceSet.cs` | **纯逻辑**：引用去重 / 黑名单 / 顺序规则 |
| `src/RoslynBridge/ReferenceScanner.cs` | IO：从 `Managed` 解析 `SmallProfile`（21 个）→ 交给 `ReferenceSet`；另提供 `ModAssemblyPaths()`（只作自愈候选源，不进引用集） |
| `src/RoslynBridge/PolyfillSource.cs` | polyfill 源码常量（`IsExternalInit` 等） |
| `src/RoslynBridge/RoslynCompiler.cs` | parse → compile → emit → `byte[]` + 诊断 |
| `src/RoslynBridge/DiagnosticFormatter.cs` | Roslyn `Diagnostic` → 控制台既有风格文本 |
| `src/RoslynBridge/ReplSession.cs` | submission 链、变量提升、值捕获、`GetVars/GetUsing/GetClasses` |
| `src/RoslynBridge/RoslynScriptEvaluator.cs` | 组装：加载器 + 引用扫描 + 编译 + 会话 + 执行 |
| `src/CSConsole/IScriptEvaluator.cs` | 抽象：现有 mcs 版与 Roslyn 版共同实现 |
| `Source/RoslynBridgeTests/RoslynBridgeTests.csproj` | net472 Exe 测试工程（link 被测源码 + 引 Roslyn 载荷） |
| `Source/RoslynBridgeTests/Program.cs` | 手写 `Check()` runner（沿用 `HotReloadTests` 风格） |

### 修改

| 文件 | 改动 |
|---|---|
| `src/CSConsole/ScriptEvaluator.cs` | 实现 `IScriptEvaluator`；`OnAssemblyLoad` 加 try/catch |
| `src/CSConsole/ConsoleController.cs` | 面向 `IScriptEvaluator`；`DefaultUsing` 扩展；编译/执行改走抽象；Reset 时按配置选实现 |
| `src/CSConsole/ScriptInteraction.cs` | `GetUsing/GetVars/GetClasses` 改走抽象（去掉对 `Mono.CSharp` 内部结构的反射） |
| `src/CSConsole/CSAutoCompleter.cs` | 补全走 `IScriptEvaluator.GetCompletions` |
| `src/CSConsole/Lexers/KeywordLexer.cs` | 补 C# 8+ 关键字（`record/init/required/with/global/not/and/or`…） |
| `src/Hooks/HookInstance.cs` | patch 类编译改走抽象 |
| `src/Config/ConfigManager.cs` | 新增 `CSharp_Console_Use_Roslyn`(bool, 默认 true)、`CSharp_Console_Allow_Mod_Type_Resolution`(bool, 默认 true)、`CSharp_Console_Default_Usings`(string, 默认扩展表)。**不设引用档位配置**（引用集恒为 21，额外全靠诊断自愈） |
| `src/UnityExplorer.csproj` | 新增 `<Reference>` 指向 `lib/roslyn/*.dll`（`Private=false`，不复制到输出，由部署脚本单独搬运）；新增 `RoslynBridge` 编译项 |
| `Source/UE-4.9.0-official/UnityExplorer-4.9.0/build.ps1` | STANDALONE_Mono 段：产物旁复制 8 件套；**ILRepack 不吸收 Roslyn**（体积+内部化会让 `Type.GetType` 失败） |
| `MODIFICATIONS.md` / `THIRD_PARTY_NOTICES.md` | 声明 Roslyn(MIT) 及 7 个依赖；载荷分发说明 |
| `MCP/index.js` | `execute_csharp_code` 的 `notes` 文案（第 2937 行）改为「默认引用本体 21 个程序集，缺件与模组类型靠诊断自愈现场补入，可直接用 Verse/RimWorld 裸类型名」 |

---

## 任务 1：Roslyn 载荷入库 + 依赖加载器

**文件：**
- 创建：`Source/UE-4.9.0-official/UnityExplorer-4.9.0/lib/roslyn/`（8 个 dll）、`lib/roslyn/VERSIONS.md`
- 创建：`src/RoslynBridge/RoslynAssemblyLoader.cs`
- 创建：`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`、`Source/RoslynBridgeTests/Program.cs`

- [ ] **步骤 1：把 8 件套放进 `lib/roslyn/`**

从 NuGet 缓存/官方包取（版本严格按 §0 表格）：

```powershell
$u = "$env:USERPROFILE\.nuget\packages"
$dst = "Source\UE-4.9.0-official\UnityExplorer-4.9.0\lib\roslyn"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item "$u\microsoft.codeanalysis.common\4.14.0\lib\netstandard2.0\Microsoft.CodeAnalysis.dll" $dst -Force
Copy-Item "$u\microsoft.codeanalysis.csharp\4.14.0\lib\netstandard2.0\Microsoft.CodeAnalysis.CSharp.dll" $dst -Force
Copy-Item "$u\system.collections.immutable\8.0.0\lib\netstandard2.0\System.Collections.Immutable.dll" $dst -Force
Copy-Item "$u\system.reflection.metadata\8.0.0\lib\netstandard2.0\System.Reflection.Metadata.dll" $dst -Force
Copy-Item "$u\system.memory\4.5.3\lib\netstandard2.0\System.Memory.dll" $dst -Force
Copy-Item "$u\system.runtime.compilerservices.unsafe\6.0.0\lib\netstandard2.0\System.Runtime.CompilerServices.Unsafe.dll" $dst -Force
Copy-Item "$u\system.threading.tasks.extensions\4.5.2\lib\netstandard2.0\System.Threading.Tasks.Extensions.dll" $dst -Force
# System.Numerics.Vectors 不在本机缓存：从官方 NuGet 包取（勿用 VS 安装目录副本 —— 那是不可再分发的产物）
# 用 4.4.0（实测 lib/net46 = 程序集 4.1.3.0，与 PoC 一致）；4.5.0 包是 4.1.4.0、与 PoC 不符，不要用
nuget install System.Numerics.Vectors -Version 4.4.0 -OutputDirectory temp\pkg
Copy-Item "temp\pkg\System.Numerics.Vectors.4.4.0\lib\net46\System.Numerics.Vectors.dll" $dst -Force
```

入库后**逐件核对程序集版本**（文件名对不上是常见坑）：

```powershell
Get-ChildItem Source\UE-4.9.0-official\UnityExplorer-4.9.0\lib\roslyn\*.dll | ForEach-Object {
  $n = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName)
  "{0,-46} {1}" -f $_.Name, $n.Version
}
```

期望：`System.Numerics.Vectors` = **4.1.3.0**（与 §0 表格一致）。若取到的是 4.1.4.0/4.1.6.0，Mono 的宽松绑定通常也接受，但**必须**重跑任务 12 的验收矩阵确认（`System.Memory` 的 `MemoryExtensions` 依赖该程序集）。

写 `lib/roslyn/VERSIONS.md`，逐行记录文件名/版本/来源包（照 §0 表格抄），末尾加一句：

```
换任何一件都必须整套重验（docs/csharp-console-compiler-upgrade-feasibility.md §4.2 的顺序/版本约束）。
```

- [ ] **步骤 2：写失败测试（加载顺序 + 失败不抛 + 幂等）**

`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AssemblyName>RoslynBridgeTests</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\RoslynAssemblyLoader.cs" Link="RoslynAssemblyLoader.cs" />
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\ReferenceSet.cs" Link="ReferenceSet.cs" />
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\PolyfillSource.cs" Link="PolyfillSource.cs" />
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\DiagnosticFormatter.cs" Link="DiagnosticFormatter.cs" />
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\RoslynCompiler.cs" Link="RoslynCompiler.cs" />
    <Reference Include="Microsoft.CodeAnalysis"><HintPath>..\UE-4.9.0-official\UnityExplorer-4.9.0\lib\roslyn\Microsoft.CodeAnalysis.dll</HintPath></Reference>
    <Reference Include="Microsoft.CodeAnalysis.CSharp"><HintPath>..\UE-4.9.0-official\UnityExplorer-4.9.0\lib\roslyn\Microsoft.CodeAnalysis.CSharp.dll</HintPath></Reference>
  </ItemGroup>
</Project>
```

`Source/RoslynBridgeTests/Program.cs`（骨架 + 任务 1 用例）：

```csharp
using System;
using UnityExplorer.RoslynBridge;

namespace RoslynBridgeTests
{
    internal static class Program
    {
        static int failures;
        static void Check(string name, bool cond, string detail = null)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name
                + (cond || string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
            if (!cond) failures++;
        }

        static int Main()
        {
            TestLoadOrder();
            TestLoadFailureDoesNotThrow();
            TestEnsureLoadedIsIdempotent();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }

        static void TestLoadOrder()
        {
            var o = RoslynAssemblyLoader.LoadOrder;
            Check("载荷件数=8", o.Length == 8, "got " + o.Length);
            Check("Vectors 必须先于 Memory",
                Array.IndexOf(o, "System.Numerics.Vectors.dll") < Array.IndexOf(o, "System.Memory.dll"));
            Check("CodeAnalysis 必须早于 CodeAnalysis.CSharp",
                Array.IndexOf(o, "Microsoft.CodeAnalysis.dll") < Array.IndexOf(o, "Microsoft.CodeAnalysis.CSharp.dll"));
            Check("CodeAnalysis.CSharp 必须最后", o[o.Length - 1] == "Microsoft.CodeAnalysis.CSharp.dll");
        }

        static void TestLoadFailureDoesNotThrow()
        {
            bool ok = false;
            try { ok = RoslynAssemblyLoader.EnsureLoaded(@"Z:\definitely\missing"); }
            catch (Exception e) { Check("缺目录不抛异常", false, e.GetType().Name); return; }
            Check("缺目录返回 false", !ok);
            Check("缺目录后 IsAvailable=false", !RoslynAssemblyLoader.IsAvailable);
        }

        static void TestEnsureLoadedIsIdempotent()
        {
            // 判别"一次性锁"的存在：锁定后即使换成**真实载荷目录**也必须直接返回缓存结果、禁止重试
            //（Mono 会缓存失败的类型解析）。无锁实现对真实目录会返回 true → 本用例即失败。
            string real = FindRealPayloadDir();
            bool second = RoslynAssemblyLoader.EnsureLoaded(real);
            Check("幂等：锁定后对真实载荷目录仍返回 false", second == false);
            Check("幂等：锁定后 IsAvailable 仍为 false", RoslynAssemblyLoader.IsAvailable == false);
        }

        /// <summary>从测试程序目录向上找到仓库里的真实载荷目录；找不到返回 null（用例据此报错）。</summary>
        static string FindRealPayloadDir()
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string p = System.IO.Path.Combine(dir.FullName,
                    @"UE-4.9.0-official\UnityExplorer-4.9.0\lib\roslyn");
                if (System.IO.Directory.Exists(p)) return p;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
```

- [ ] **步骤 3：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `The type or namespace name 'RoslynAssemblyLoader' could not be found`（源文件尚未创建）

- [ ] **步骤 4：实现 `RoslynAssemblyLoader`**

`src/RoslynBridge/RoslynAssemblyLoader.cs`：

```csharp
using System;
using System.IO;
using System.Reflection;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 固定顺序加载 Roslyn 载荷。
    /// ⚠️ 顺序不可调换：System.Memory 先于 System.Numerics.Vectors 加载时，Mono 会缓存
    /// typeref 解析失败，之后补齐也无法恢复（需重启进程）。见可行性报告 §4.2。
    /// ⚠️ 只尝试一次：失败后重试会命中同一个失败缓存，故用 s_attempted 锁死。
    /// </summary>
    public static class RoslynAssemblyLoader
    {
        public static readonly string[] LoadOrder =
        {
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.Memory.dll",
            "System.Collections.Immutable.dll",
            "System.Reflection.Metadata.dll",
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
        };

        static bool s_attempted;
        static bool s_available;
        static string s_failure;

        public static bool IsAvailable => s_available;
        public static string FailureReason => s_failure;

        public static bool EnsureLoaded(string payloadDir)
        {
            if (s_attempted) return s_available;
            s_attempted = true;
            try
            {
                foreach (string file in LoadOrder)
                {
                    string full = Path.Combine(payloadDir, file);
                    if (!File.Exists(full))
                        throw new FileNotFoundException("载荷缺失：" + full);
                    Assembly.LoadFrom(full);
                }
                s_available = true;
            }
            catch (Exception ex)
            {
                s_failure = ex.GetType().Name + ": " + ex.Message;
                s_available = false;
            }
            return s_available;
        }
    }
}
```

- [ ] **步骤 5：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`

- [ ] **步骤 6：Commit**

```bash
# ⚠️ lib/ 目录在 .gitignore 里（`Source/UE-4.9.0-official/UnityExplorer-4.9.0/lib/`），
#    Roslyn 载荷是**新文件** → 普通 git add 会被忽略，必须 -f（与 runtime/dotnet/ 同一处理）
git add -f Source/UE-4.9.0-official/UnityExplorer-4.9.0/lib/roslyn
git add Source/RoslynBridgeTests Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/RoslynAssemblyLoader.cs
git commit -m "feat(roslyn): 载荷入库与固定顺序依赖加载器"
```

---

## 任务 2：默认引用集合（SmallProfile 21 个本体程序集 + 模组自愈候选）

**文件：**
- 创建：`src/RoslynBridge/ReferenceSet.cs`（纯逻辑）
- 创建：`src/RoslynBridge/ReferenceScanner.cs`（IO，依赖 UnityEngine/ExplorerCore，**不进** net472 测试工程，靠任务 12 矩阵验收）
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestReferenceSet`）
- 修改：`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`（**只** link `ReferenceSet.cs`——它在任务 1 步骤 2 已写入；`ReferenceScanner.cs` 不要 link，否则 net472 侧会因缺 UnityEngine 而编译失败）

**需求来源（必须满足）：** 控制台开局就要能用游戏本体与模组的类型，AI 不必每次先试错才发现控制台是空的。

- [ ] **步骤 1：写失败测试**

在 `Program.cs` 的 `Main` 里加 `TestReferenceSet();`，并加入：

```csharp
        static void TestReferenceSet()
        {
            var set = new ReferenceSet();
            Check("空集为空", set.Paths.Count == 0);

            Check("加一条路径成功", set.TryAdd(@"C:\a\mscorlib.dll", "mscorlib", ""));
            Check("同 simple name 去重（先到先得）", !set.TryAdd(@"C:\b\mscorlib.dll", "mscorlib", ""));
            Check("去重后仍只有 1 条", set.Paths.Count == 1);
            Check("去重保留先到的那条", set.Paths[0] == @"C:\a\mscorlib.dll");

            Check("dynamic 程序集跳过", !set.TryAdd(@"", "eval-3", ""));
            Check("空路径跳过", !set.TryAdd(null, "Foo", ""));
            Check("completions 跳过", !set.TryAdd(@"C:\a\completions.dll", "completions", ""));

            Check("黑名单命中跳过", !set.TryAdd(@"C:\a\Bad.dll", "Bad", "Bad;Other"));
            Check("黑名单带 .dll 后缀也能命中", !set.TryAdd(@"C:\a\Bad2.dll", "Bad2", "Bad2.dll;"));
            Check("黑名单大小写不敏感", !set.TryAdd(@"C:\a\Bad3.dll", "Bad3", "bad3"));
            Check("未命中黑名单可加入", set.TryAdd(@"C:\a\Good.dll", "Good", "Bad;Other"));

            Check("顺序稳定（先加载的在前）", set.Paths[0] == @"C:\a\mscorlib.dll");
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `'ReferenceSet' could not be found`

- [ ] **步骤 3：实现 `ReferenceSet`**

`src/RoslynBridge/ReferenceSet.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>引用路径集合：按 simple name 去重（先到先得）、可黑名单、跳过动态程序集。纯逻辑，可离线单测。</summary>
    public sealed class ReferenceSet
    {
        readonly List<string> s_paths = new List<string>();
        readonly HashSet<string> s_names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public IList<string> Paths => s_paths;

        public bool TryAdd(string location, string simpleName, string blacklist)
        {
            if (string.IsNullOrEmpty(location)) return false;
            if (string.IsNullOrEmpty(simpleName)) return false;
            if (simpleName.StartsWith("eval-", StringComparison.Ordinal)) return false;
            if (simpleName == "completions") return false;
            if (IsBlacklisted(simpleName, blacklist)) return false;
            if (!s_names.Add(simpleName)) return false;
            s_paths.Add(location);
            return true;
        }

        public static bool IsBlacklisted(string simpleName, string blacklist)
        {
            if (string.IsNullOrEmpty(blacklist)) return false;
            foreach (string raw in blacklist.Split(';'))
            {
                string bl = raw.Trim();
                if (bl.Length == 0) continue;
                if (bl.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    bl = bl.Substring(0, bl.Length - 4);
                if (string.Equals(bl, simpleName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
```

- [ ] **步骤 4：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`

- [ ] **步骤 5：实现 `ReferenceScanner`（IO 层，游戏内才跑）**

`src/RoslynBridge/ReferenceScanner.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Verse;   // ModContentPack / LoadedModManager

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 引用集**只有一份**：`SmallProfile` 的 21 个本体程序集（从 `Managed` 解析）。
    /// **不预设任何其它引用**（不扫已加载程序集、不预先加模组）——
    /// 一切额外引用由任务 13 的**诊断自愈**按需补入（CS0012 直接给程序集名；CS0246/CS0103 按命名空间反查）。
    /// 实测依据：21 个的成员覆盖残差为 0；本体 106 个在启动后虽全部已加载，但**不加进编译期引用集**，
    /// 以压住元数据开销（明细见报告 §4.6）。
    /// </summary>
    public static class ReferenceScanner
    {
        /// <summary>唯一引用集：21 个（实测残差 0；加 UnityEngine.AssetBundleModule 后归零）。</summary>
        public static readonly string[] SmallProfile =
        {
            "mscorlib", "netstandard", "System", "System.Core", "System.Xml", "System.Xml.Linq",
            "Assembly-CSharp", "Assembly-CSharp-firstpass",
            "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.IMGUIModule", "UnityEngine.UI",
            "UnityEngine.AudioModule", "UnityEngine.TextRenderingModule",
            "UnityEngine.PhysicsModule", "UnityEngine.AnimationModule", "UnityEngine.ParticleSystemModule",
            "UnityEngine.AssetBundleModule",
            "Unity.Collections", "Unity.Mathematics", "com.rlabrecque.steamworks.net",
        };

        /// <summary>本体根目录（实测 Application.dataPath == &lt;game&gt;/RimWorldWin64_Data）。</summary>
        public static string GameRootOfThisProcess()
            => Path.GetDirectoryName(UnityEngine.Application.dataPath);

        /// <summary>本体 Managed 目录（实测存在且 106 个 dll 齐全）。</summary>
        public static string ManagedDir()
            => Path.Combine(UnityEngine.Application.dataPath, "Managed");

        public static ReferenceSet Collect(string blacklist, out string report)
        {
            var set = new ReferenceSet();
            int fromManaged = 0;

            string managed = ManagedDir();
            if (Directory.Exists(managed))
            {
                foreach (string simple in SmallProfile)
                {
                    string f = Path.Combine(managed, simple + ".dll");
                    if (File.Exists(f) && set.TryAdd(f, simple, blacklist)) fromManaged++;
                }
            }

            report = $"refs: small={fromManaged}/{SmallProfile.Length} total={set.Paths.Count}";
            return set;
        }

        /// <summary>
        /// 供**自愈器**使用的候选程序集路径（不预先加入引用集）：由模组加载器汇报的模组程序集。
        /// ⚠️ 不要扫 &lt;Game&gt;/Mods：Steam 创意工坊模组不在那里（实测 33 个模组程序集里 20 个在 workshop 目录）。
        /// ⚠️ `mod.assemblies` 是**字段**（类型 `ModAssemblyHandler`），程序集列表是它的**字段** `loadedAssemblies`。
        /// </summary>
        public static List<string> ModAssemblyPaths(string blacklist)
        {
            var list = new List<string>();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod.assemblies == null) continue;
                List<Assembly> asms = mod.assemblies.loadedAssemblies;
                if (asms == null) continue;
                foreach (Assembly a in asms)
                {
                    string loc;
                    try { loc = a.Location; } catch { continue; }
                    if (string.IsNullOrEmpty(loc)) continue;
                    if (ReferenceSet.IsBlacklisted(a.GetName().Name, blacklist)) continue;
                    if (!list.Contains(loc)) list.Add(loc);
                }
            }
            return list;
        }
    }
}
```

- [ ] **步骤 6：游戏内验收（并入任务 12 矩阵执行）**

按定稿（§3 执行说明），**游戏内验收集中在任务 8/11/12**；本任务只提交离线测试证据（步骤 1–4）。原验收的验证点全部由任务 12 端到端矩阵覆盖，对应关系：

| 本任务验证点 | 任务 12 矩阵行 |
|---|---|
| 裸名开局不空：`[System.AppDomain.CurrentDomain.GetAssemblies().Length, Verse.GenTicks.TicksGame]` 成功且不出现 CS0103 | #1 |
| 模组类型靠自愈按需解析：默认（`C# Console Allow Mod Type Resolution`=true）首次执行 `typeof(FlightPawn2Framework.PathPlan.FlightAStar.FlightPawnConfig).Assembly.GetName().Name` 应成功、日志出现 `自愈补引用：TEXT20.dll`；把该开关关掉 → Reset → 重跑应报 `CS0246` | #4 |
| 防收窄回归：`Verse.Find.CurrentMap.mapPawns.FreeColonists[0].DrawPos.y` 成功（引用集一旦被收窄成只引 Assembly-CSharp.dll，这里立刻 CS0246，实测见 §0） | #14、#15 |
| 清单外 Unity 模块：`typeof(UnityEngine.Video.VideoPlayer).FullName` 靠自愈补齐（21 个清单不含 VideoModule） | #3、#17 |

- [ ] **步骤 7：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/ReferenceSet.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/ReferenceScanner.cs Source/RoslynBridgeTests
git commit -m "feat(roslyn): 默认引用集（SmallProfile=21 本体程序集，模组作自愈候选）"
```

---

## 任务 3：默认 using 扩展（让裸类型名直接可用）

**文件：**
- 修改：`src/CSConsole/ConsoleController.cs:39-53`（`DefaultUsing` 数组）
- 修改：`src/Config/ConfigManager.cs`（新增两个配置项，插在 `CSConsole_Assembly_Blacklist` 之后）
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestDefaultUsingsParsing`）
- 修改：`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`（加一条 link）：

```xml
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\DefaultUsings.cs" Link="DefaultUsings.cs" />
```

**需求来源：** 「控制台一开是空的」的根因是没有 `Verse`/`RimWorld` using，不是没有引用。

- [ ] **步骤 1：写失败测试（默认 using 表的解析规则）**

在 `src/RoslynBridge/` 新增 `DefaultUsings.cs`（放常量与解析），先写测试：

```csharp
        static void TestDefaultUsingsParsing()
        {
            var list = DefaultUsings.Parse("Verse;RimWorld;; Verse.AI ;");
            Check("分号切分", list.Count == 3, "got " + list.Count);
            Check("去空项", !list.Contains(""));
            Check("去重（含首尾空格视为同一个）", list.Count == 3);
            Check("含 Verse", list.Contains("Verse"));
            Check("含 RimWorld", list.Contains("RimWorld"));
            Check("含 Verse.AI", list.Contains("Verse.AI"));

            var defaults = DefaultUsings.BuiltIn;
            Check("内置含 Verse", defaults.Contains("Verse"));
            Check("内置含 RimWorld", defaults.Contains("RimWorld"));
            Check("内置含 UnityEngine", defaults.Contains("UnityEngine"));
            Check("内置含 System.Linq", defaults.Contains("System.Linq"));
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `'DefaultUsings' could not be found`

- [ ] **步骤 3：实现 `DefaultUsings`**

`src/RoslynBridge/DefaultUsings.cs`：

```csharp
using System;
using System.Collections.Generic;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>控制台默认 using。含 RimWorld 本体命名空间，裸类型名（GenTicks/Find/Pawn…）可直接编译。
    /// 实测代价：把 Verse/RimWorld/Verse.AI/RimWorld.Planet 与 System/UnityEngine/UnityEngine.UI 同时 using，
    /// 只有 5 个名字会歧义 —— Keyframe、Graphic、GridLayout、Text、WeakReference&lt;T&gt;（低频，需要时全限定）。
    /// ⚠️ 故意**不含**模组命名空间：实测 137 个模组命名空间里有 34 个类型名与本体核心重名、19 组模组之间重名
    ///    （Coroutine@MoonSharp / FloatRange@RIMAPI / IServiceProvider@RIMAPI / MethodAttributes@Mono.Cecil …），
    ///    全量塞进来会制造新的"每次都得试一下"。模组命名空间走 add_using_directive / 配置项按需追加。
    /// </summary>
    public static class DefaultUsings
    {
        public static readonly string[] BuiltIn =
        {
            "System", "System.Linq", "System.Text", "System.Collections", "System.Collections.Generic",
            "System.Reflection", "UnityEngine", "UniverseLib",
            "Verse", "RimWorld", "Verse.AI", "RimWorld.Planet",
        };

        public static List<string> Parse(string semicolonSeparated)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(semicolonSeparated)) return result;
            foreach (string raw in semicolonSeparated.Split(';'))
            {
                string ns = raw.Trim();
                if (ns.Length == 0) continue;
                if (!result.Contains(ns)) result.Add(ns);
            }
            return result;
        }
    }
}
```

- [ ] **步骤 4：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`

- [ ] **步骤 5：接线到 `ConsoleController`**

把 `ConsoleController.cs` 的 `DefaultUsing` 改为读配置（保留 `#if CPP` 分支）：

```csharp
        static readonly string[] DefaultUsing = DefaultUsings.BuiltIn;
```

在 `ResetConsole` 里，用配置覆盖（允许用户追加）：

```csharp
            usingDirectives = new HashSet<string>();
            foreach (string use in DefaultUsings.Parse(ConfigManager.CSharp_Console_Default_Usings.Value))
                AddUsing(use);
```

- [ ] **步骤 6：加配置项**

`src/Config/ConfigManager.cs`：字段区加

```csharp
        public static ConfigElement<bool> CSharp_Console_Use_Roslyn;
        public static ConfigElement<bool> CSharp_Console_Allow_Mod_Type_Resolution;
        public static ConfigElement<string> CSharp_Console_Default_Usings;
```

`Init` 里（紧跟 `CSConsole_Assembly_Blacklist` 之后）加

```csharp
            CSharp_Console_Use_Roslyn = new("C# Console Use Roslyn",
                "Use the Roslyn compiler (C# 13) for the C# Console instead of the legacy Mono.CSharp compiler.\n" +
                "If Roslyn fails to initialize, the console automatically falls back to Mono.CSharp.",
                true);

            CSharp_Console_Allow_Mod_Type_Resolution = new("C# Console Allow Mod Type Resolution",
                "When a compile fails because a reference is missing, may the auto-healer resolve it from a mod assembly?\n" +
                "Default ON. Mods are never referenced up-front either way - a mod assembly only enters the reference set\n" +
                "when your code actually touches a mod type (and the compiler asks for it).\n" +
                "Turn OFF to keep the console strictly base-game (mod types will stay unresolvable).",
                true);

            CSharp_Console_Default_Usings = new("C# Console Default Usings",
                "Namespaces added as using directives when the C# Console is reset.\n" +
                "Separate each namespace with a semicolon ';'.",
                string.Join(";", DefaultUsings.BuiltIn));
```

（`ConfigManager.cs` 顶部需要 `using UnityExplorer.RoslynBridge;`）

- [ ] **步骤 7：游戏内验收（并入任务 12 矩阵执行）**

本任务只提交离线测试证据；两条游戏内验证由任务 12 矩阵覆盖：`GenTicks.TicksGame` 裸名成功（#1）、`Find.CurrentMap.mapPawns.FreeColonists.Count` 返回殖民者数量（#2，当前存档为 3）。

- [ ] **步骤 8：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/DefaultUsings.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ConsoleController.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/Config/ConfigManager.cs Source/RoslynBridgeTests
git commit -m "feat(csconsole): 默认 using 扩展（Verse/RimWorld），裸类型名开箱可用"
```

---

## 任务 4：polyfill 源

**文件：**
- 创建：`src/RoslynBridge/PolyfillSource.cs`
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestPolyfills`）

- [ ] **步骤 1：写失败测试**

```csharp
        static void TestPolyfills()
        {
            var refs = ReferenceSetForThisProcess();

            var without = RoslynCompiler.Compile("NoShim", "public record R(int A);", refs, includePolyfills: false);
            Check("无 polyfill：record 编译失败", !without.Success);
            Check("无 polyfill：报 CS0518 IsExternalInit",
                without.Diagnostics.Contains("CS0518") && without.Diagnostics.Contains("IsExternalInit"),
                without.Diagnostics);

            var with = RoslynCompiler.Compile("WithShim", "public record R(int A);", refs, includePolyfills: true);
            Check("有 polyfill：record 编译成功", with.Success, with.Diagnostics);
            Check("有 polyfill：产出非空 PE", with.PeBytes != null && with.PeBytes.Length > 0);

            var req = RoslynCompiler.Compile("Req", "public class C { public required int X { get; set; } }",
                refs, includePolyfills: true);
            Check("有 polyfill：required 成员编译成功", req.Success, req.Diagnostics);
        }

        static System.Collections.Generic.List<string> ReferenceSetForThisProcess()
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!string.IsNullOrEmpty(a.Location)) list.Add(a.Location); } catch { }
            }
            return list;
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `'RoslynCompiler' could not be found`（任务 5 才实现；此步先确认测试本身写对）

- [ ] **步骤 3：实现 `PolyfillSource`**

`src/RoslynBridge/PolyfillSource.cs`：

```csharp
namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 运行时不提供的编译器要求类型（Unity Mono 缺；Span/Index/Range/HashCode 等已存在，勿重复声明）。
    /// isExternalInit: C# 9 record / init；其余三个: C# 11 required。
    /// </summary>
    public static class PolyfillSource
    {
        public const string FileName = "__RoslynPolyfills.cs";

        public const string Code = @"
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct |
                           System.AttributeTargets.Field | System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : System.Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) { FeatureName = featureName; }
        public string FeatureName { get; }
        public bool IsOptional { get; set; }
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [System.AttributeUsage(System.AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : System.Attribute { }
}
";
    }
}
```

- [ ] **步骤 4：Commit（连同任务 5 一起提交，见下）**

---

## 任务 5：编译内核 + 诊断格式化

**文件：**
- 创建：`src/RoslynBridge/RoslynCompiler.cs`
- 创建：`src/RoslynBridge/DiagnosticFormatter.cs`
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestCompiler`、`TestDiagnosticFormat`）

- [ ] **步骤 1：写失败测试**

```csharp
        static void TestCompiler()
        {
            var refs = ReferenceSetForThisProcess();

            var ok = RoslynCompiler.Compile("Ok", "public static class M { public static int F(int x) => x switch { 1 => 10, _ => 0 }; }",
                refs, includePolyfills: true);
            Check("现代语法编译成功", ok.Success, ok.Diagnostics);
            Check("成功时 ErrorCount=0", ok.ErrorCount == 0);

            var asm = System.Reflection.Assembly.Load(ok.PeBytes);
            Check("产物可加载并执行", (int)asm.GetType("M").GetMethod("F").Invoke(null, new object[] { 1 }) == 10);

            var bad = RoslynCompiler.Compile("Bad", "public class X { Missing y; }", refs, includePolyfills: true);
            Check("缺类型编译失败", !bad.Success);
            Check("失败时 ErrorCount>0", bad.ErrorCount > 0);
            Check("失败时无 PE", bad.PeBytes == null);
        }

        static void TestDiagnosticFormat()
        {
            var refs = ReferenceSetForThisProcess();
            var r = RoslynCompiler.Compile("Fmt", "public class X { Missing y; }", refs, includePolyfills: true);
            Check("诊断含文件名占位（行号）", r.Diagnostics.Contains("(1,"), r.Diagnostics);
            Check("诊断含 error CS", r.Diagnostics.Contains("error CS"), r.Diagnostics);
            Check("诊断含类型名 Missing", r.Diagnostics.Contains("Missing"), r.Diagnostics);
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `'RoslynCompiler' could not be found`

- [ ] **步骤 3：实现 `DiagnosticFormatter`**

`src/RoslynBridge/DiagnosticFormatter.cs`：

```csharp
using System.Text;
using Microsoft.CodeAnalysis;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>把 Roslyn 诊断格式化成像 mcs 那样的 `(行,列): error CSxxxx: 消息`，控制台/日志观感不变。</summary>
    public static class DiagnosticFormatter
    {
        public static string Format(Diagnostic d, string fileName = null)
        {
            FileLinePositionSpan span = d.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            int col = span.StartLinePosition.Character + 1;
            string id = d.Id;
            string sev = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            return $"({line},{col}): {sev} {id}: {d.GetMessage()}";
        }

        public static string FormatAll(System.Collections.Generic.IEnumerable<Diagnostic> diags,
                                       string fileName = null,
                                       bool errorsOnly = true)
        {
            var sb = new StringBuilder();
            foreach (Diagnostic d in diags)
            {
                if (errorsOnly && d.Severity != DiagnosticSeverity.Error) continue;
                sb.AppendLine(Format(d, fileName));
            }
            return sb.ToString().TrimEnd();
        }
    }
}
```

- [ ] **步骤 4：实现 `RoslynCompiler`**

`src/RoslynBridge/RoslynCompiler.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace UnityExplorer.RoslynBridge
{
    public sealed class CompileResult
    {
        public bool Success;
        public byte[] PeBytes;
        public string Diagnostics = "";
        public int ErrorCount;
    }

    /// <summary>parse → compile → emit 到内存。不负责加载（调用方决定 Assembly.Load 或 CreateFromImage）。</summary>
    public static class RoslynCompiler
    {
        static readonly CSharpParseOptions ParseOptions =
            new CSharpParseOptions(LanguageVersion.Latest);

        static readonly CSharpCompilationOptions CompileOptions =
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        public static CompileResult Compile(string assemblyName, string source,
                                            IEnumerable<string> referencePaths, bool includePolyfills,
                                            IEnumerable<string> extraSources = null,
                                            string extraBaseSource = null)
        {
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, ParseOptions) };
            if (extraSources != null)
                foreach (string s in extraSources)
                    trees.Add(CSharpSyntaxTree.ParseText(s, ParseOptions));
            if (includePolyfills)
                trees.Add(CSharpSyntaxTree.ParseText(PolyfillSource.Code, ParseOptions));

            var refs = new List<MetadataReference>();
            foreach (string p in referencePaths)
            {
                try { refs.Add(MetadataReference.CreateFromFile(p)); }
                catch { /* 非托管/损坏 dll：跳过，Roslyn 只读元数据 */ }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName, trees, refs, CompileOptions);

            var result = new CompileResult();
            using (var ms = new MemoryStream())
            {
                EmitResult emit = compilation.Emit(ms);
                result.ErrorCount = 0;
                foreach (Diagnostic d in emit.Diagnostics)
                    if (d.Severity == DiagnosticSeverity.Error) result.ErrorCount++;
                result.Diagnostics = DiagnosticFormatter.FormatAll(emit.Diagnostics);
                result.Success = emit.Success && result.ErrorCount == 0;
                if (result.Success)
                    result.PeBytes = ms.ToArray();
            }
            return result;
        }
    }
}
```

- [ ] **步骤 5：运行四个测试组确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`（含 `TestPolyfills`，它会用上任务 4 的 `PolyfillSource`）

- [ ] **步骤 6：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/PolyfillSource.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/RoslynCompiler.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/DiagnosticFormatter.cs Source/RoslynBridgeTests
git commit -m "feat(roslyn): 编译内核、诊断格式化与必需 polyfill"
```

---

## 任务 6：REPL 会话（变量提升 / 值捕获 / 清单查询）

**文件：**
- 创建：`src/RoslynBridge/ReplSession.cs`
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestReplSession`）
- 修改：`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`（加一条 link）：

```xml
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\ReplSession.cs" Link="ReplSession.cs" />
```

**设计（已验证机制）：** 每次提交编译成一个静态类 `Submission_N : Submission_{N-1}`：
- 顶层 `var x = …` / `T x = …` 被**提升**为该类的 `public static` 字段 → 下次提交通过继承链可见；
- 其余语句进 `Run()`；
- 末条语句若是表达式 → 追加 `__result = <expr>;` 实现"值打印"；
- 既有变量在本次提交中的**赋值**（`x = 5;`）直接落到继承链上的字段；
- 提交产物以 `CreateFromImage` 形式引用给下一次编译（`Assembly.Load(byte[])` 无 `Location`，不能 `CreateFromFile`）。

- [ ] **步骤 1：写失败测试**

```csharp
        static void TestReplSession()
        {
            var refs = ReferenceSetForThisProcess();
            var s = new ReplSession(refs);

            var r1 = s.Submit("var x = 5;");
            Check("提交1 无错误", r1.ErrorCount == 0, r1.Diagnostics);
            Check("提交1 定义了变量 x", s.GetVars().Contains("x"));

            var r2 = s.Submit("var y = x + 37;");
            Check("提交2 能读到提交1 的变量", r2.ErrorCount == 0, r2.Diagnostics);
            Check("变量表含 x 与 y", s.GetVars().Contains("x") && s.GetVars().Contains("y"));

            var r3 = s.Submit("y");
            Check("末表达式产生返回值", r3.ErrorCount == 0 && s.LastValue?.ToString() == "42",
                "value=" + s.LastValue + " diag=" + r3.Diagnostics);

            var r4 = s.Submit("x = 100;");
            Check("可对既有变量赋值", r4.ErrorCount == 0, r4.Diagnostics);
            var r5 = s.Submit("y = x + 1; y");
            Check("赋值后再次求值", s.LastValue?.ToString() == "101", "value=" + s.LastValue);

            var r6 = s.Submit("public class MyProbe { public static int V() => 7; }");
            Check("提交类定义成功", r6.ErrorCount == 0, r6.Diagnostics);
            var r7 = s.Submit("MyProbe.V()");
            Check("下一次提交能用到上一次定义的类", s.LastValue?.ToString() == "7", "value=" + s.LastValue);
            Check("GetClasses 列出用户类", s.GetClasses().Contains("MyProbe"));

            var r8 = s.Submit("using System.Text;");
            Check("using 指令成功", r8.ErrorCount == 0, r8.Diagnostics);
            Check("GetUsing 含 System.Text", s.GetUsing().Contains("System.Text"));
            var r9 = s.Submit("new StringBuilder(\"ab\").Append(\"c\").ToString()");
            Check("using 对后续提交生效", s.LastValue?.ToString() == "abc", "value=" + s.LastValue);

            var bad = s.Submit("var z = NotExistingThing;");
            Check("错误提交不污染会话", bad.ErrorCount > 0);
            var stillOk = s.Submit("y");
            Check("错误后会话仍可用", stillOk.ErrorCount == 0 && s.LastValue?.ToString() == "101");
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `'ReplSession' could not be found`

- [ ] **步骤 3：实现 `ReplSession`（分三小块，先跑通再补齐）**

`src/RoslynBridge/ReplSession.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 无状态桩的 REPL：提交 N 编译成 Submission_N（继承 Submission_{N-1}），
    /// 顶层变量提升为静态字段以跨提交保留；末表达式写入 __result 实现值打印。
    /// </summary>
    public sealed class ReplSession
    {
        readonly List<string> s_referencePaths;
        readonly List<MetadataReference> s_imageRefs = new List<MetadataReference>();
        readonly List<string> s_usings = new List<string>();
        readonly List<string> s_classes = new List<string>();
        readonly List<KeyValuePair<string, Type>> s_vars = new List<KeyValuePair<string, Type>>();

        int s_submission;
        Assembly s_lastAssembly;

        public object LastValue { get; private set; }
        public CompileResult LastResult { get; private set; }

        public ReplSession(IEnumerable<string> referencePaths)
        {
            s_referencePaths = new List<string>(referencePaths);
        }

        public CompileResult Submit(string code)
        {
            int n = s_submission + 1;
            string className = "Submission_" + n;
            string baseName = n == 1 ? null : "Submission_" + (n - 1);

            SyntaxTree tree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.Latest));
            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            // 1) using 指令：吞进会话表，不进类体
            foreach (UsingDirectiveSyntax u in root.Usings)
            {
                string ns = u.Name.ToString();
                if (!s_usings.Contains(ns)) s_usings.Add(ns);
            }

            // 2) 顶层变量声明 → 提升为静态字段；顶层类定义 → 直接放进本提交
            var fields = new StringBuilder();
            var classes = new StringBuilder();
            var statements = new List<StatementSyntax>();
            foreach (MemberDeclarationSyntax m in root.Members)
            {
                if (m is GlobalStatementSyntax g)
                {
                    if (g.Statement is LocalDeclarationStatementSyntax local && local.UsingKeyword == default)
                    {
                        string typeText = local.Declaration.Type.ToString();
                        bool isVar = typeText == "var";
                        foreach (VariableDeclaratorSyntax v in local.Declaration.Variables)
                        {
                            string fieldType = typeText;
                            if (isVar)
                            {
                                ITypeSymbol inferred = GetInferredType(tree, v);
                                fieldType = inferred?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)) ?? "object";
                            }
                            fields.AppendLine($"    public static {fieldType} {v.Identifier.Text};");
                            if (v.Initializer != null)
                                statements.Add(SyntaxFactory.ParseStatement(
                                    $"{v.Identifier.Text} = {v.Initializer.Value};"));
                            if (!s_vars.Any(p => p.Key == v.Identifier.Text))
                                s_vars.Add(new KeyValuePair<string, Type>(v.Identifier.Text, null));
                        }
                    }
                    else
                    {
                        statements.Add(g.Statement);
                    }
                }
                else if (m is ClassDeclarationSyntax || m is StructDeclarationSyntax
                         || m is InterfaceDeclarationSyntax || m is EnumDeclarationSyntax)
                {
                    classes.AppendLine(m.ToFullString());
                    string name = m is BaseTypeDeclarationSyntax b ? b.Identifier.Text : null;
                    if (name != null && !s_classes.Contains(name)) s_classes.Add(name);
                }
            }

            // 3) 末条语句若是表达式 → 捕获为 __result
            string valueExpr = null;
            if (statements.Count > 0 && statements[statements.Count - 1] is ExpressionStatementSyntax es)
            {
                valueExpr = es.Expression.ToString();
                statements.RemoveAt(statements.Count - 1);
            }

            var body = new StringBuilder();
            foreach (StatementSyntax st in statements) body.AppendLine("        " + st.ToFullString().Trim());
            if (valueExpr != null) body.AppendLine($"        __result = {valueExpr};");

            var src = new StringBuilder();
            src.AppendLine("using System;");
            foreach (string ns in s_usings) src.AppendLine("using " + ns + ";");
            src.AppendLine($"public class {className}" + (baseName != null ? " : " + baseName : ""));
            src.AppendLine("{");
            if (baseName == null) src.AppendLine("    public static object __result;");
            src.Append(fields);
            src.AppendLine("    public static void Run()");
            src.AppendLine("    {");
            src.Append(body);
            src.AppendLine("    }");
            src.Append(classes);
            src.AppendLine("}");

            var trees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(src.ToString(), new CSharpParseOptions(LanguageVersion.Latest)),
                CSharpSyntaxTree.ParseText(PolyfillSource.Code, new CSharpParseOptions(LanguageVersion.Latest)),
            };

            var metadataRefs = new List<MetadataReference>(s_imageRefs);
            foreach (string p in s_referencePaths)
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(p)); } catch { }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                className + "_asm", trees, metadataRefs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var result = new CompileResult();
            byte[] pe;
            using (var ms = new MemoryStream())
            {
                var emit = compilation.Emit(ms);
                result.Diagnostics = DiagnosticFormatter.FormatAll(emit.Diagnostics);
                result.ErrorCount = emit.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
                result.Success = emit.Success && result.ErrorCount == 0;
                pe = result.Success ? ms.ToArray() : null;
                result.PeBytes = pe;
            }

            LastResult = result;
            if (!result.Success) return result;   // 失败：会话状态不变（不推进 submission 计数）

            s_imageRefs.Add(MetadataReference.CreateFromImage(pe));
            s_submission = n;
            Assembly asm = Assembly.Load(pe);
            s_lastAssembly = asm;
            LastValue = null;
            try
            {
                Type t = asm.GetType(className);
                t.GetMethod("Run").Invoke(null, null);
                Type probe = asm.GetType(className);
                while (probe != null && probe.GetField("__result") == null) probe = probe.BaseType;
                if (probe != null) LastValue = probe.GetField("__result").GetValue(null);
                RefreshVarTypes(asm, className);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostics += Environment.NewLine + "运行时异常: " + ex.InnerException?.Message ?? ex.Message;
            }
            return result;
        }

        ITypeSymbol GetInferredType(SyntaxTree tree, VariableDeclaratorSyntax v)
        {
            // 只为 var 推断字段类型：用一次性编译的语义模型；失败则回落 object
            try
            {
                var comp = CSharpCompilation.Create("infer",
                    new[] { tree },
                    s_referencePaths.Where(File.Exists).Select(MetadataReference.CreateFromFile),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                SemanticModel model = comp.GetSemanticModel(tree);
                return model.GetTypeInfo(v.Initializer.Value).Type;
            }
            catch { return null; }
        }

        void RefreshVarTypes(Assembly asm, string className)
        {
            for (int i = 0; i < s_vars.Count; i++)
            {
                FieldInfo f = null;
                for (Type t = asm.GetType(className); t != null && f == null; t = t.BaseType)
                    f = t.GetField(s_vars[i].Key);
                if (f != null)
                    s_vars[i] = new KeyValuePair<string, Type>(s_vars[i].Key, f.FieldType);
            }
        }

        public string GetVars()
        {
            var sb = new StringBuilder();
            foreach (var kv in s_vars)
            {
                object val = null;
                try { val = s_lastAssembly?.GetType("Submission_" + s_submission)?.GetField(kv.Key)?.GetValue(null); }
                catch { }
                sb.AppendLine(kv.Key + " = " + (val ?? "null") + "   (" + (kv.Value?.Name ?? "?") + ")");
            }
            return sb.ToString().TrimEnd();
        }

        public string GetUsing() => string.Join(Environment.NewLine, s_usings.ToArray());
        public string GetClasses() => string.Join(Environment.NewLine, s_classes.ToArray());
    }
}
```

- [ ] **步骤 4：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`。若 `TestReplSession` 有失败项，按失败信息修正：
- 「提交2 能读到提交1 的变量」失败 → 检查基类链是否正确（`Submission_2 : Submission_1`）
- 「using 对后续提交生效」失败 → 检查 `s_usings` 是否在生成源码里展开
- 变量类型显示 `?` → `RefreshVarTypes` 未命中字段，检查 `GetField` 是否遍历了基类

- [ ] **步骤 5：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/ReplSession.cs Source/RoslynBridgeTests
git commit -m "feat(roslyn): REPL 会话（变量提升/值捕获/清单查询）"
```

---

## 任务 7：`IScriptEvaluator` 抽象 + `RoslynScriptEvaluator`

**文件：**
- 创建：`src/CSConsole/IScriptEvaluator.cs`
- 创建：`src/RoslynBridge/RoslynScriptEvaluator.cs`
- 修改：`src/CSConsole/ScriptEvaluator.cs`（实现接口 + `OnAssemblyLoad` 加 try/catch）
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestRoslynEvaluatorFacade`，用假引用集直接跑）
- 修改：`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`（加两条 link）：

```xml
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\CSConsole\IScriptEvaluator.cs" Link="IScriptEvaluator.cs" />
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\RoslynScriptEvaluator.cs" Link="RoslynScriptEvaluator.cs" />
```

- [ ] **步骤 1：定义接口（先写，让两边都实现）**

`src/CSConsole/IScriptEvaluator.cs`：

```csharp
using System;

namespace UnityExplorer.CSConsole
{
    /// <summary>C# 控制台求值器抽象：mcs 版（ScriptEvaluator）与 Roslyn 版（RoslynScriptEvaluator）共同实现。</summary>
    public interface IScriptEvaluator : IDisposable
    {
        /// <summary>编译并执行一段输入；返回是否成功。REPL 值经 LastValue 暴露。</summary>
        bool Evaluate(string input, out string output);

        /// <summary>补全候选（不含前缀）；prefix 为已输入的前缀。</summary>
        string[] GetCompletions(string input, out string prefix);

        string GetUsing();
        string GetVars();
        string GetClasses();

        void AddUsing(string namespaceName);

        bool IsAvailable { get; }
        string FailureReason { get; }
    }
}
```

- [ ] **步骤 2：写失败测试**

```csharp
        static void TestRoslynEvaluatorFacade()
        {
            var refs = ReferenceSetForThisProcess();
            using (var ev = new RoslynScriptEvaluator(refs))
            {
                Check("门面可用", ev.IsAvailable);

                string outp;
                Check("执行 x=1", ev.Evaluate("var x = 1;", out outp), outp);
                Check("执行 x+41 得 42", ev.Evaluate("x + 41", out outp) && ev.LastValueText == "42", "value=" + ev.LastValueText);
                Check("GetVars 含 x", ev.GetVars().Contains("x"));
                Check("AddUsing 后 using 列表更新", AddUsingAndCheck(ev));

                Check("语法错误返回 false", !ev.Evaluate("var = ;", out outp));
                Check("错误后仍可用", ev.Evaluate("x", out outp) && ev.LastValueText == "1");
            }
        }

        static bool AddUsingAndCheck(RoslynScriptEvaluator ev)
        {
            ev.AddUsing("System.Text");
            return ev.GetUsing().Contains("System.Text");
        }
```

- [ ] **步骤 3：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `'RoslynScriptEvaluator' could not be found`

- [ ] **步骤 4：实现 `RoslynScriptEvaluator`**

`src/RoslynBridge/RoslynScriptEvaluator.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using UnityExplorer.CSConsole;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>Roslyn 求值器门面：载荷健康 + 引用扫描 + REPL 会话。</summary>
    public sealed class RoslynScriptEvaluator : IScriptEvaluator
    {
        readonly ReplSession s_session;
        public object LastValue { get; private set; }
        public string LastValueText => LastValue?.ToString();

        public bool IsAvailable { get; private set; }
        public string FailureReason { get; private set; }

        public RoslynScriptEvaluator(IEnumerable<string> referencePaths, string blacklist = "")
        {
            s_session = new ReplSession(referencePaths);
            IsAvailable = true;
        }

        public bool Evaluate(string input, out string output)
        {
            CompileResult r = s_session.Submit(input);
            LastValue = s_session.LastValue;
            output = r.Diagnostics;
            return r.Success;
        }

        public string[] GetCompletions(string input, out string prefix)
        {
            // 本任务先返回空数组（保证本任务可独立编译/测试通过）；任务 9 接入：
            //   return RoslynCompletion.Complete(input, s_session, out prefix);
            prefix = "";
            return Array.Empty<string>();
        }

        public string GetUsing() => s_session.GetUsing();
        public string GetVars() => s_session.GetVars();
        public string GetClasses() => s_session.GetClasses();
        public void AddUsing(string namespaceName) => s_session.Submit("using " + namespaceName + ";");
        public void Dispose() { }
    }
}
```

- [ ] **步骤 5：`ScriptEvaluator`(mcs) 实现同一接口**

`src/CSConsole/ScriptEvaluator.cs` 加接口声明并把现有逻辑包一层（不改变 mcs 行为）：

```csharp
    public class ScriptEvaluator : Evaluator, IScriptEvaluator
    {
        public bool IsAvailable => true;
        public string FailureReason => null;

        public bool Evaluate(string input, out string output)
        {
            output = null;
            CompiledMethod repl = null;
            try { repl = Compile(input); }
            catch (Exception ex) { output = ex.Message; return false; }

            if (repl != null)
            {
                object ret = null;
                try { repl.Invoke(ref ret); }
                catch (Exception ex) { output = ex.Message; return false; }
                output = ret?.ToString();
                return true;
            }
            output = _reportPrinter.ErrorsCount > 0 ? _textWriter.ToString() : null;
            return _reportPrinter.ErrorsCount == 0;
        }

        public string[] GetCompletions(string input, out string prefix)
        {
            try { return base.GetCompletions(input, out prefix); }
            catch { prefix = ""; return Array.Empty<string>(); }
        }

        public string GetUsing() => base.GetUsing();
        public string GetVars() => base.GetVars();
        public void AddUsing(string ns) => Run("using " + ns + ";");

        // 原来在 ScriptInteraction.GetClasses 里的 mcs 内部反射，集中搬到这里（接口要求两边都能给类清单）
        static readonly FieldInfo fi_sourceFile = AccessTools.Field(typeof(Evaluator), "source_file");

        public string GetClasses()
        {
            if (fi_sourceFile.GetValue(this) is CompilationSourceFile sourceFile && sourceFile.Containers.Any())
            {
                var sb = new StringBuilder();
                sb.Append($"There are {sourceFile.Containers.Count} defined classes:");
                foreach (TypeDefinition type in sourceFile.Containers.Where(it => it is TypeDefinition))
                {
                    sb.Append($"\n\n{type.MemberName.Name}:");
                    foreach (MemberCore member in type.Members)
                        sb.Append($"\n\t- {member.AttributeTargets}: \"{member.MemberName.Name}\" ({member.ModFlags})");
                }
                return sb.ToString();
            }
            return null;
        }
```

（该文件顶部需补 `using System.Linq;`、`using System.Text;`、`using System.Reflection;`、`using HarmonyLib;`；`fi_sourceFile` 即原 `ScriptInteraction` 里的 `AccessTools.Field(typeof(Evaluator), "source_file")`）

同时给 `OnAssemblyLoad` 加保护（可行性报告 §4.2 第 5 条）：

```csharp
        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                string name = args.LoadedAssembly.GetName().Name;
                if (StdLib.Contains(name)) return;
                Reference(args.LoadedAssembly);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"[CSConsole] 引用新加载程序集失败（已忽略）：{args.LoadedAssembly.GetName().Name} - {ex.GetType().Name}");
            }
        }
```

- [ ] **步骤 6：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`

- [ ] **步骤 7：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/IScriptEvaluator.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ScriptEvaluator.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/RoslynScriptEvaluator.cs Source/RoslynBridgeTests
git commit -m "feat(csconsole): IScriptEvaluator 抽象 + Roslyn 门面，mcs 兼容实现"
```

---

## 任务 8：`ConsoleController` 接线 + 开关 + 回退

**文件：**
- 修改：`src/CSConsole/ConsoleController.cs:57-139`（`Init`/`ResetConsole`/`Evaluate`）
- 修改：`src/CSConsole/ScriptInteraction.cs`
- 修改：`src/CSConsole/CSAutoCompleter.cs:82`

- [ ] **步骤 1：改 `ConsoleController` 持有抽象**

```csharp
        public static IScriptEvaluator Evaluator { get; private set; }
        static IScriptEvaluator LegacyEvaluator;
        public static bool UsingRoslyn { get; private set; }
```

`ResetConsole` 改写为：

```csharp
        public static void ResetConsole(bool logSuccess = true)
        {
            if (SRENotSupported) return;

            Evaluator?.Dispose();
            GenerateTextWriter();

            string blacklist = ConfigManager.CSConsole_Assembly_Blacklist.Value;
            bool wantRoslyn = ConfigManager.CSharp_Console_Use_Roslyn.Value;
            string payloadDir = Path.Combine(ExplorerCore.ExplorerFolder, "Roslyn");

            if (wantRoslyn && RoslynAssemblyLoader.EnsureLoaded(payloadDir))
            {
                ReferenceSet refs = ReferenceScanner.Collect(blacklist, out string report);
                Evaluator = new RoslynScriptEvaluator(refs.Paths, ReferenceScanner.ManagedDir(),
                    ConfigManager.CSharp_Console_Allow_Mod_Type_Resolution.Value
                        ? ReferenceScanner.ModAssemblyPaths(blacklist)
                        : new List<string>());
                Evaluator = new RoslynScriptEvaluator(refs.Paths, blacklist);
                UsingRoslyn = true;
                ExplorerCore.Log($"[CSConsole] Roslyn 求值器就绪（{report}）");
            }
            else
            {
                Evaluator = new ScriptEvaluator(evaluatorStringWriter) { InteractiveBaseClass = typeof(ScriptInteraction) };
                UsingRoslyn = false;
                if (wantRoslyn)
                    ExplorerCore.LogWarning($"[CSConsole] 回退 mcs：{RoslynAssemblyLoader.FailureReason}");
            }

            usingDirectives = new HashSet<string>();
            foreach (string use in DefaultUsings.Parse(ConfigManager.CSharp_Console_Default_Usings.Value))
                AddUsing(use);

            if (logSuccess) ExplorerCore.Log("C# Console reset");
        }
```

`Evaluate(string input, bool supressLog)` 改为：

```csharp
        public static void Evaluate(string input, bool supressLog = false)
        {
            if (SRENotSupported) return;
            try
            {
                if (Evaluator.Evaluate(input, out string output))
                {
                    if (!string.IsNullOrEmpty(output))
                        ExplorerCore.Log($"Invoked REPL, result: {output}");
                    else if (!supressLog)
                        ExplorerCore.Log("Code compiled without errors.");
                }
                else if (!supressLog)
                {
                    ExplorerCore.LogWarning($"Unable to compile the code.{Environment.NewLine}{output}");
                }
            }
            catch (Exception ex)
            {
                if (!supressLog) ExplorerCore.LogWarning(ex);
            }
        }
```

`Init()` 里那句 mcs 专用的 `Evaluator.Compile("0 == 0")` 自检也要改成抽象调用（否则编译不过）：

```csharp
            try
            {
                ResetConsole(false);
                // 求值器自检：任一实现都必须能编译一个平凡表达式
                if (!Evaluator.Evaluate("0 == 0", out string probeDiag))
                    throw new Exception(string.IsNullOrEmpty(probeDiag) ? "evaluator probe failed" : probeDiag);
            }
            catch (Exception ex)
            {
                DisableConsole(ex);
                return;
            }
```

（`ResetConsole` 里「按 `CSharp_Console_Default_Usings` 逐个 `AddUsing`」这段沿用任务 3 步骤 5 的写法，本任务不改语义。）

- [ ] **步骤 2：`ScriptInteraction` 去掉 mcs 内部反射**
```csharp
        public static void GetUsing() => Log(Evaluator.GetUsing());
        public static void GetVars()  { string v = Evaluator.GetVars(); Log(string.IsNullOrEmpty(v) ? "No variables defined." : v); }
        public static void GetClasses() { string c = Evaluator.GetClasses(); Log(string.IsNullOrEmpty(c) ? "No classes defined." : c); }
```

（`Evaluator` 现在是 `IScriptEvaluator`；`ScriptInteraction.cs` 顶部删掉 `using Mono.CSharp;`）

- [ ] **步骤 3：补全调用点改为抽象**

`src/CSConsole/CSAutoCompleter.cs:82`：

```csharp
            string[] evaluatorCompletions = ConsoleController.Evaluator.GetCompletions(input, out string prefix);
```

- [ ] **步骤 4：构建**

运行：
```powershell
cd Source/UE-4.9.0-official/UnityExplorer-4.9.0
dotnet build src/UnityExplorer.csproj -c STANDALONE_Mono
```
预期：`Build succeeded`，无 `CS0246`/`CS0103`（若报缺 `RoslynCompletion`，说明任务 9 未做——此时先把 `GetCompletions` 临时返回空数组并在任务 9 接回）

- [ ] **步骤 5：部署并在游戏内验收「开关 + 回退」**

```powershell
# 停游戏（MCP stop_game）→ 复制产物与 Roslyn 载荷到**游戏加载的 Mods 副本**
# （仓库目录不是模组目录，产物复制进仓库不会被游戏读到；仓库只是源码真源）
$mod = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Copy-Item Release\UnityExplorer.Standalone.Mono\UnityExplorer.STANDALONE.Mono.dll "$mod\Assemblies\" -Force
New-Item -ItemType Directory -Force -Path "$mod\UE_Data\sinai-dev-UnityExplorer\Roslyn" | Out-Null
Copy-Item lib\roslyn\*.dll "$mod\UE_Data\sinai-dev-UnityExplorer\Roslyn\" -Force
```

启动游戏进图后（MCP `start_quick_test`）：
调用：`agg_call_tool { tool: "execute_csharp_code", args: { "code": "GenTicks.TicksGame" } }`
预期：成功；UE 日志里有 `[CSConsole] Roslyn 求值器就绪（refs: small=<n>/21 total=<m>）`——格式以任务 2 `ReferenceScanner.Collect` 的 report 为准（实测应为 `small=21/21 total=21`；用 `get_unityexplorer_logs` 查看）

回退验收：把配置 `C# Console Use Roslyn` 改为 false（UE 配置面板或配置文件），重开控制台，日志应出现 `回退 mcs` 或直接以 mcs 初始化（无 `Roslyn 求值器就绪`），且 `GenTicks.TicksGame` 仍能执行。

- [ ] **步骤 6：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ConsoleController.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/ScriptInteraction.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/CSAutoCompleter.cs
git commit -m "feat(csconsole): 接线 IScriptEvaluator，Roslyn/mcs 开关与自动回退"
```

---

## 任务 9：自动补全迁移（语义模型三来源）

**文件：**
- 创建：`src/RoslynBridge/RoslynCompletion.cs`
- 修改：`src/RoslynBridge/ReplSession.cs`（新增 `AllNamespaces()`，供命名空间补全）
- 修改：`src/CSConsole/Lexers/KeywordLexer.cs`（补 C# 8+ 关键字）
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestCompletion`）
- 修改：`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`（加一条 link）：

```xml
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\RoslynCompletion.cs" Link="RoslynCompletion.cs" />
```

- [ ] **步骤 1：写失败测试**

```csharp
        static void TestCompletion()
        {
            var refs = ReferenceSetForThisProcess();
            using (var ev = new RoslynScriptEvaluator(refs))
            {
                ev.Evaluate("var sb = new System.Text.StringBuilder();", out _);
                string prefix;
                string[] c1 = ev.GetCompletions("sb.App", out prefix);
                Check("成员补全命中 Append*", c1.Any(s => s.StartsWith("end") || s.StartsWith("Append")), string.Join(",", c1));
                Check("补全前缀正确", prefix.Length > 0, "prefix=" + prefix);

                string[] c2 = ev.GetCompletions("System.Tex", out prefix);
                Check("命名空间补全命中 t/Text*",
                    c2.Any(s => s.StartsWith("t") || s.StartsWith("Text")), string.Join(",", c2));
            }

            Check("关键字表含 record", KeywordLexer.keywords.Contains("record"));
            Check("关键字表含 init", KeywordLexer.keywords.Contains("init"));
            Check("关键字表含 required", KeywordLexer.keywords.Contains("required"));
            Check("关键字表含 with", KeywordLexer.keywords.Contains("with"));
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败（`RoslynCompletion` 未创建）；或断言失败（关键字未补）

- [ ] **步骤 3：实现 `RoslynCompletion`**

`src/RoslynBridge/RoslynCompletion.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 轻量补全（不引 Workspaces/Features，省 ~12MB 与 MEF 组合）：
    ///   1) 形如 A.B 的成员补全：把 "A" 当表达式求类型 → 列公共成员
    ///   2) 命名空间补全：已加载程序集 + 引用的命名空间前缀匹配
    /// prefix 为已输入的部分，候选只返回补全余下部分（与原 CSAutoCompleter 语义一致）。
    /// </summary>
    public static class RoslynCompletion
    {
        static readonly Regex MemberPattern = new Regex(@"([A-Za-z_][\w\.]*)\.([A-Za-z_]\w*)$", RegexOptions.Compiled);

        public static string[] Complete(string input, ReplSession session, out string prefix)
        {
            prefix = "";
            if (string.IsNullOrEmpty(input)) return Array.Empty<string>();

            Match m = MemberPattern.Match(input);
            if (!m.Success) return Array.Empty<string>();

            string receiver = m.Groups[1].Value;
            prefix = m.Groups[2].Value;

            // 来源 1：成员补全（接收者是会话里的变量/类型）
            Type t = session.ResolveExpressionType(receiver);
            if (t != null)
            {
                return t.GetMembers(System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.Instance |
                                    System.Reflection.BindingFlags.Static)
                        .Select(mi => mi.Name)
                        .Distinct()
                        .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n != prefix)
                        .Select(n => n.Substring(prefix.Length))
                        .OrderBy(n => n)
                        .Take(50)
                        .ToArray();
            }

            // 来源 2：命名空间补全（"System.Tex" → "t"/"tEncoding"…）
            return NamespaceCompletions(receiver + "." + prefix, session);
        }

        static string[] NamespaceCompletions(string fullPrefix, ReplSession session)
        {
            var result = new List<string>();
            foreach (string ns in session.AllNamespaces())
            {
                if (!ns.StartsWith(fullPrefix, StringComparison.Ordinal)) continue;
                if (ns.Length == fullPrefix.Length) continue;
                string rest = ns.Substring(fullPrefix.Length);
                int dot = rest.IndexOf('.');
                string next = dot < 0 ? rest : rest.Substring(0, dot + 1);
                if (!result.Contains(next)) result.Add(next);
            }
            result.Sort(StringComparer.Ordinal);
            return result.Take(50).ToArray();
        }
    }
}
```

在 `ReplSession` 增补解析入口（供补全与测试使用）：

```csharp
        /// <summary>把补全用的接收者表达式交给会话编译一次并取回其运行时类型；失败返回 null。</summary>
        public Type ResolveExpressionType(string expression)
        {
            if (string.IsNullOrEmpty(expression)) return null;
            string probeName = "__probe_" + Guid.NewGuid().ToString("N");
            var src = new System.Text.StringBuilder();
            src.AppendLine("using System;");
            foreach (string ns in s_usings) src.AppendLine("using " + ns + ";");
            src.AppendLine("public static class " + probeName);
            src.AppendLine("{");
            src.AppendLine("    public static object Get() { return " + expression + "; }");
            src.AppendLine("}");
            var trees = new[] { CSharpSyntaxTree.ParseText(src.ToString()) };
            var metadataRefs = new List<MetadataReference>(s_imageRefs);
            foreach (string p in s_referencePaths)
            {
                try { metadataRefs.Add(MetadataReference.CreateFromFile(p)); } catch { }
            }
            var comp = CSharpCompilation.Create(probeName,
                trees, metadataRefs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using (var ms = new System.IO.MemoryStream())
            {
                if (!comp.Emit(ms).Success) return null;
                try
                {
                    var asm = System.Reflection.Assembly.Load(ms.ToArray());
                    object v = asm.GetType(probeName).GetMethod("Get").Invoke(null, null);
                    return v?.GetType();
                }
                catch { return null; }
            }
        }
```

（注意：`sb` 是会话静态字段，所以在探测类里可见——这正是任务 6 继承链的用途。）

再接上命名空间补全需要的数据源（**只用 Roslyn 元数据，不加载程序集**，避免把 106 个本体 dll 真载入进程）：

```csharp
        // ReplSession 内新增
        HashSet<string> s_namespaces;

        public IEnumerable<string> AllNamespaces()
        {
            if (s_namespaces != null) return s_namespaces;
            s_namespaces = new HashSet<string>(StringComparer.Ordinal);
            var refs = new List<MetadataReference>();
            foreach (string p in s_referencePaths)
            {
                try { refs.Add(MetadataReference.CreateFromFile(p)); } catch { }
            }
            CSharpCompilation comp = CSharpCompilation.Create("nsprobe", null, refs);
            foreach (MetadataReference r in refs)
            {
                if (!(comp.GetAssemblyOrModuleSymbol(r) is IAssemblySymbol asm)) continue;
                CollectNamespaces(asm.GlobalNamespace, s_namespaces);
            }
            return s_namespaces;
        }

        static void CollectNamespaces(INamespaceSymbol ns, HashSet<string> sink)
        {
            foreach (INamespaceSymbol child in ns.GetNamespaceMembers())
            {
                string name = child.ToDisplayString();
                if (name.Length > 0) sink.Add(name);
                CollectNamespaces(child, sink);
            }
        }
```

- [ ] **步骤 4：补 `KeywordLexer` 关键字**

`src/CSConsole/Lexers/KeywordLexer.cs` 的 `keywords` 集合加入：`record`、`init`、`required`、`with`、`global`、`not`、`and`、`or`、`file`、`scoped`、`unmanaged`。

- [ ] **步骤 5：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`

- [ ] **步骤 6：游戏内验收（并入任务 12 矩阵执行）**

本任务只提交离线测试证据（含 `GetCompletions` 两条用例）。进程内补全验证由任务 12 矩阵 #19 执行（API 级调用，无需人工打字）；UE 控制台 UI 下拉（输入 `sb.App` 出现候选）为人工加分项，可选。

- [ ] **步骤 7：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/RoslynCompletion.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/CSConsole/Lexers/KeywordLexer.cs Source/RoslynBridgeTests
git commit -m "feat(csconsole): 语义模型轻量补全与 C# 8+ 关键字"
```

---

## 任务 10：`HookInstance` 迁移（Roslyn 编译 Harmony patch）

**文件：**
- 修改：`src/Hooks/HookInstance.cs:12-90`
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestHookCompile`）

- [ ] **步骤 1：写失败测试（编译产物能被 Harmony 当补丁方法取到）**

```csharp
        static void TestHookCompile()
        {
            var refs = ReferenceSetForThisProcess();   // 测试进程里含 0Harmony（HotReloadTests 同款引用）
            using (var ev = new RoslynScriptEvaluator(refs))
            {
                string patchSource = @"
    [HarmonyLib.HarmonyPatch]
    public static class DynamicPatch_Probe
    {
        public static void Prefix() { }
    }";
                string wrapped = "public static class DynamicPatch_Probe_Wrapper { }";   // 占位：真实代码见步骤 3
                bool ok = ev.Evaluate(patchSource + "\n" + wrapped, out string diag);
                Check("patch 类编译成功", ok, diag);

                var asm = ev.LastCompiledAssembly;
                Check("已加载补丁程序集", asm != null);
                Type t = asm.GetType("DynamicPatch_Probe");
                Check("能取到 Prefix 方法", t?.GetMethod("Prefix") != null);
            }
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败（`LastCompiledAssembly` 属性不存在）

- [ ] **步骤 3：给 `RoslynScriptEvaluator` 暴露编译产物程序集，并改写 `HookInstance`**

`RoslynScriptEvaluator` 增加：

```csharp
        public System.Reflection.Assembly LastCompiledAssembly => s_session.LastAssembly;
```

`ReplSession` 增加 `public Assembly LastAssembly => s_lastAssembly;`

`src/Hooks/HookInstance.cs` 把"复用静态 mcs 求值器 + 反射 `Evaluator.source_file`"改为：

```csharp
        // 旧的 mcs 静态求值器与 source_file/TypeDefinition 反射已移除（Roslyn 无此内部结构）
        static readonly IScriptEvaluator PatchEvaluator = ConsoleController.Evaluator;

        public bool CompileAndGenerateProcessor(string patchSource)
        {
            Unpatch();
            try
            {
                patchProcessor = ExplorerCore.Harmony.CreateProcessor(TargetMethod);

                string typeName = "DynamicPatch_" + DateTime.Now.Ticks;
                string code =
                    "public static class " + typeName + "\n{\n" + patchSource + "\n}";

                if (!PatchEvaluator.Evaluate(code, out string diag))
                    throw new FormatException("Unable to compile the generated patch!\n" + diag);

                Assembly asm = ((RoslynScriptEvaluator)PatchEvaluator).LastCompiledAssembly;
                Type type = asm.GetType(typeName);
                if (type == null)
                    throw new FormatException("编译产物里找不到补丁类型 " + typeName);

                postfix  = type.GetMethod("Postfix",  BindingFlags.Static | BindingFlags.Public);
                prefix   = type.GetMethod("Prefix",   BindingFlags.Static | BindingFlags.Public);
                finalizer= type.GetMethod("Finalizer",BindingFlags.Static | BindingFlags.Public);
                transpiler = type.GetMethod("Transpiler", BindingFlags.Static | BindingFlags.Public);

                if (prefix != null)    patchProcessor.AddPrefix(prefix);
                if (postfix != null)   patchProcessor.AddPostfix(postfix);
                if (finalizer != null) patchProcessor.AddFinalizer(finalizer);
                if (transpiler != null) patchProcessor.AddTranspiler(transpiler);
                return true;
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning(ex);
                return false;
            }
        }
```

（`HookInstance.cs` 顶部删除 `using Mono.CSharp;` 与 `using UnityExplorer.CSConsole;` 中不再使用的成员；`fi_sourceFile` / `pi_Definition` 两个反射字段与 `GenerateDefaultPatchSourceCode` 里的 `GetClasses()` 相关反射一并删除。）

- [ ] **步骤 4：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`

- [ ] **步骤 5：游戏内验收（并入任务 12 矩阵执行）**

本任务只提交离线测试证据（含 `TestHookCompile`）。真实 Harmony 链路（`create_hook Verse.GenTicks.get_TicksGame` → `list_hooks` → `execute_csharp_code` → `delete_hook`）由任务 12 矩阵 #20 执行。

- [ ] **步骤 6：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/Hooks/HookInstance.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge
git commit -m "feat(hooks): 动态 Harmony 补丁改用 Roslyn 编译"
```

---

## 任务 11：打包、预热与许可

**文件：**
- 修改：`src/UnityExplorer.csproj`（新增 8 个 `<Reference>`）
- 修改：`Source/UE-4.9.0-official/UnityExplorer-4.9.0/build.ps1:125-134`
- 创建：`src/RoslynBridge/RoslynPreloader.cs`
- 修改：`ExplorerCore.cs`（启动时后台预热）
- 修改：`MODIFICATIONS.md`、`THIRD_PARTY_NOTICES.md`

- [ ] **步骤 1：csproj 加引用（不复制到输出）**

`src/UnityExplorer.csproj` 的 `<!-- Mono -->` ItemGroup 之后新增：

```xml
    <!-- Roslyn 载荷：仅编译期引用；运行时由部署脚本放到 UE_Data/sinai-dev-UnityExplorer/Roslyn/ 后 Assembly.LoadFrom -->
    <ItemGroup Condition="'$(Configuration)'=='STANDALONE_Mono' or '$(Configuration)'=='BIE5_Mono' or '$(Configuration)'=='BIE6_Mono' or '$(Configuration)'=='ML_Mono'">
        <Reference Include="Microsoft.CodeAnalysis">
            <HintPath>..\lib\roslyn\Microsoft.CodeAnalysis.dll</HintPath>
            <Private>False</Private>
        </Reference>
        <Reference Include="Microsoft.CodeAnalysis.CSharp">
            <HintPath>..\lib\roslyn\Microsoft.CodeAnalysis.CSharp.dll</HintPath>
            <Private>False</Private>
        </Reference>
    </ItemGroup>
```

- [ ] **步骤 2：build.ps1 STANDALONE_Mono 段搬运载荷（不 ILRepack）**

```powershell
# ----------- Standalone Mono -----------
dotnet build src/UnityExplorer.sln -c Release_STANDALONE_Mono
$Path = "Release/UnityExplorer.Standalone.Mono"
# ILRepack：只吸收 mcs/Tomlet；**不要**吸收 Roslyn（体积 + internalize 会让反射式加载失效）
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.Standalone.Mono.dll $Path/UnityExplorer.Standalone.Mono.dll $Path/mcs.dll $Path/Tomlet.dll
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
# Roslyn 载荷：原样放到产物旁的 Roslyn/ 子目录（部署脚本再搬到 UE_Data）
New-Item -ItemType Directory -Force -Path $Path/Roslyn | Out-Null
Copy-Item lib/roslyn/*.dll $Path/Roslyn/ -Force
Remove-Item $Path/../UnityExplorer.Standalone.Mono.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.Standalone.Mono.zip .\$Path\*
```

- [ ] **步骤 3：后台预热**

`src/RoslynBridge/RoslynPreloader.cs`：

```csharp
using System.IO;
using System.Threading;
using UnityExplorer.Config;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>启动后后台预热：加载载荷 + 编译一次空提交，把 ~4.9s 冷启动摊到加载期。</summary>
    public static class RoslynPreloader
    {
        public static void Start(string payloadDir)
        {
            if (!ConfigManager.CSharp_Console_Use_Roslyn.Value) return;
            var t = new Thread(() =>
            {
                try
                {
                    if (!RoslynAssemblyLoader.EnsureLoaded(payloadDir)) return;
                    ReferenceSet refs = ReferenceScanner.Collect(
                        ConfigManager.CSConsole_Assembly_Blacklist.Value, out _);
                    var warm = new ReplSession(refs.Paths);
                    warm.Submit("var __warm = 1;");
                    // 21 个清单的常见缺口：把常见的"缺引用自愈"提前跑掉，避免用户/AI 第一次调用时多等 1~2 轮编译
                    warm.Submit("var __warmV = UnityEngine.Vector3.zero; var __warmM = Verse.Find.CurrentMap;");
                    ExplorerCore.Log("[CSConsole] Roslyn 预热完成" + (healer?.Healed.Count > 0 ? ("（顺带自愈 " + healer.Healed.Count + " 个引用）") : ""));
                }
                catch (System.Exception ex)
                {
                    ExplorerCore.LogWarning("[CSConsole] Roslyn 预热失败（不影响回退）：" + ex.Message);
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
```

在 `ExplorerCore.Init`（`ExplorerCore.cs` 中初始化 UI 之前）调用：

```csharp
            RoslynBridge.RoslynPreloader.Start(
                System.IO.Path.Combine(ExplorerCore.ExplorerFolder, "Roslyn"));
```

- [ ] **步骤 4：许可与声明**

`THIRD_PARTY_NOTICES.md` 追加：

```markdown
| `UE_Data/sinai-dev-UnityExplorer/Roslyn/*.dll` | **MIT** | Microsoft Roslyn（Microsoft.CodeAnalysis 4.14.0 / CSharp 4.14.0）及依赖 System.Collections.Immutable、System.Reflection.Metadata、System.Memory、System.Runtime.CompilerServices.Unsafe、System.Threading.Tasks.Extensions、System.Numerics.Vectors（均 MIT，随包分发，版本见 `Source/UE-4.9.0-official/UnityExplorer-4.9.0/lib/roslyn/VERSIONS.md`） |
```

`MODIFICATIONS.md` 的「新增文件」段落追加一行：

```markdown
- `lib/roslyn/`（Roslyn 编译器载荷 8 件套）及其在 `UE_Data/sinai-dev-UnityExplorer/Roslyn/` 的部署副本
```

- [ ] **步骤 5：构建 + 部署 + 游戏内验收（时延与体积）**

```powershell
# 构建（调度顺序保证本构建已包含任务 9/10/13 的改动：执行顺序 1→10 → 13 → 11 → 12）
cd Source/UE-4.9.0-official/UnityExplorer-4.9.0
dotnet build src/UnityExplorer.csproj -c STANDALONE_Mono

# 停游戏（MCP stop_game）→ 部署到游戏加载的 Mods 副本（与任务 8 步骤 5 同款命令）
$mod = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger'
Copy-Item Release\UnityExplorer.Standalone.Mono\UnityExplorer.STANDALONE.Mono.dll "$mod\Assemblies\" -Force
New-Item -ItemType Directory -Force -Path "$mod\UE_Data\sinai-dev-UnityExplorer\Roslyn" | Out-Null
Copy-Item lib\roslyn\*.dll "$mod\UE_Data\sinai-dev-UnityExplorer\Roslyn\" -Force

# 重启进图（MCP start_quick_test）后：
# 冷启动时延：启动日志出现 "[CSConsole] Roslyn 预热完成"，首次 execute_csharp_code 响应 < 500ms（预热摊掉 ~3.5s）
"{0:N1} MB" -f ((Get-ChildItem "C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\UE_Data\sinai-dev-UnityExplorer\Roslyn" -File | Measure-Object Length -Sum).Sum / 1MB)
```
体积预期：约 `12.7 MB`；`Assemblies/UnityExplorer.STANDALONE.Mono.dll` 体积与改动前同量级（Roslyn 未被 ILRepack 吸收）

- [ ] **步骤 6：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/UnityExplorer.csproj Source/UE-4.9.0-official/UnityExplorer-4.9.0/build.ps1 Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/RoslynPreloader.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/ExplorerCore.cs MODIFICATIONS.md THIRD_PARTY_NOTICES.md
git commit -m "chore(roslyn): 载荷打包、后台预热与许可声明"
```

---

## 任务 12：MCP 文案与端到端验收

**文件：**
- 修改：`MCP/index.js:2937`（`execute_csharp_code` 的 `notes`）
- 修改：`docs/csharp-console-compiler-upgrade-feasibility.md`（末尾补「已实现」状态）
- 修改：`README.md`（控制台能力说明）

- [ ] **步骤 1：更新 MCP 工具文案**

`MCP/index.js` 第 2937 行改为：

```javascript
  execute_csharp_code: { example: '{ "code": "GenTicks.TicksGame" }', notes: '控制台默认引用 21 个本体程序集（SmallProfile，实测本体 API 覆盖残差为 0）；清单外的 Unity 模块、System 件与模组类型（含 Steam 创意工坊，由模组加载器汇报）由编译诊断自愈现场补入（日志出现「自愈补引用：xxx.dll」，可用 `C# Console Allow Mod Type Resolution` 禁用模组解析）。并默认 using Verse/RimWorld/Verse.AI/UnityEngine，裸类型名（如 GenTicks、Find、Pawn）可直接使用。模组命名空间默认**不**加入 using（实测会把 34 个重名类型变成歧义），需要时用 add_using_directive。已知与默认 using 冲突、必须全限定的裸名：Keyframe、Graphic、GridLayout、Text、WeakReference。编译器为 Roslyn（C# 13），支持 record/switch 表达式/using 声明等现代语法；若 Roslyn 载荷缺失会自动回退旧编译器（届时仅支持 C# 7 语法）。' },
```

- [ ] **步骤 2：端到端验收矩阵（逐条执行并记录结果）**

对每条调用 `agg_call_tool { tool: "execute_csharp_code", args: { "code": "<下表的 code>" } }`，期望 `success=true`（例外：#20 走 `create_hook`/`list_hooks`/`delete_hook`；#19 在 execute 内调用补全 API）：

| # | code | 验证点 |
|---|---|---|
| 1 | `GenTicks.TicksGame` | 默认 using（裸名） |
| 2 | `Find.CurrentMap.mapPawns.FreeColonists.Count` | 默认 using + 本体类型 |
| 3 | `typeof(UnityEngine.Video.VideoPlayer).Name` | **清单外模块靠自愈（首次）**：VideoModule 不在 21 个清单里，应日志出现「自愈补引用」后成功 |
| 4 | `typeof(FlightPawn2Framework.PathPlan.FlightAStar.FlightPawnConfig).Assembly.GetName().Name` | **模组类型靠自愈解析**：首次应成功（日志出现 `自愈补引用：TEXT20.dll`）；**把 `C# Console Allow Mod Type Resolution` 关掉后**重跑应为 CS0246（这是"只调试本体"模式） |
| 5 | `int F(int x) => x switch { 1 => 10, _ => 0 }; F(1)` | C# 8 switch 表达式 + C# 7 本地函数 |
| 6 | `using var ms = new System.IO.MemoryStream(); ms.Length` | C# 8 using 声明 |
| 7 | `var (a, b) = (1, 2); a + b` | 元组解构（回归项） |
| 8 | `record R(int A); new R(7).ToString()` | C# 9 record（polyfill 生效） |
| 9 | `object o = null; o is not null` | C# 9 模式 |
| 10 | `Log("hello")` | REPL 助手仍可用 |
| 11 | `typeof(HarmonyLib.Harmony).Assembly.Location` | **工坊模组程序集靠自愈解析**（`brrainz.harmony` 装在 workshop 目录 → 验证自愈走的是"模组加载器汇报"而不是目录扫描） |
| 18 | `UnityEngine.Video.VideoPlayer` / `UnityEngine.AssetBundle`（21 个清单未含的模块） | 自愈按 CS0012/CS0246 补齐（清单没列 ≠ 用不了） |
| 19 | 先执行 `var sb = new System.Text.StringBuilder();`，再执行 `string.Join(",", UnityExplorer.CSConsole.ConsoleController.Evaluator.GetCompletions("sb.App", out string __p))` | **补全（并入自任务 9）**：返回候选含 `Append`（API 级验证；UE 控制台 UI 下拉为人工加分项） |
| 20 | `create_hook { type_name: "Verse.GenTicks", method_name: "get_TicksGame" }` → `list_hooks` → `execute_csharp_code "Verse.GenTicks.TicksGame"` → `delete_hook` | **真实 Harmony 链路（并入自任务 10）**：hook 创建/列出/执行/删除全成功 |
| 12 | `Find.CurrentMap.mapPawns.AllPawnsSpawned.Count` | 本体集合 API 裸名可用（无歧义回归） |
| 13 | `System.WeakReference<Verse.Pawn>` / `Verse.WeakReference<Verse.Pawn>` | 已知歧义名全限定后仍可用（默认 using 的 5 个冲突名之一） |
| 14 | `Verse.Find.CurrentMap.mapPawns.FreeColonists[0].DrawPos.y` | **防收窄回归**：一旦只引 Assembly-CSharp.dll，这里立刻 CS0246（实测见 §0） |
| 15 | `((System.Action)(() => { Verse.Widgets.BeginGroup(new UnityEngine.Rect(0f, 0f, 10f, 10f)); Verse.GenDraw.DrawLineBetween(UnityEngine.Vector3.zero, UnityEngine.Vector3.one); Verse.MaterialPool.MatFrom((string)null, (UnityEngine.Shader)null); })).GetType().Name` | **覆盖度回归**：这三个成员是"只引 Assembly-CSharp"时被卡最狠的类型（`Widgets` 152/191、`GenDraw` 37/52、`MaterialPool` 12/12，见 §0）。包在**不执行的 lambda** 里：只验编译期可解析，不会真的画线/取材质 |
| 16 | 进程内调 `RoslynCompiler.CompileWithHealing`（手造 5 引用小集 mscorlib/netstandard/System/System.Core/Assembly-CSharp，healer 搜索目录 = Managed）编译 `Verse.Find.CurrentMap.mapPawns.FreeColonists[0].DrawPos.y`，再对**同一引用集**编译第二段 | **自愈闭环（核心保障）**：第一段 success=true 且 healLog 含 `自愈补引用：UnityEngine.CoreModule.dll`；第二段 success=true 且 healLog 为空（已进集合、不重复补） |
| 17 | 再次执行 `typeof(UnityEngine.Video.VideoPlayer).Name`（行 3 已自愈补过） | **幂等**：success=true 且**不再出现**新的自愈补引用行；若行 3 自愈对该命名空间解析失败，应给出可读诊断而不是静默失败 |

- [ ] **步骤 3：TEXT20 实参级回归（复用可行性阶段的用例）**

调用：`agg_call_tool { tool: "execute_csharp_code", args: { "code": "<_poc_roslyn/Text20Defs.cs 的 All() 逻辑精简单行版>" } }`

具体单行版（等价于可行性阶段的 Def 盘点）：

```csharp
string.Join(",", Verse.DefDatabase<FlightPawn2Framework.Flight2Animation.SampleDef>.AllDefsListForReading.Select(d => d.defName))
```

预期：返回含 `WingSample_Takeoff` 的 8 个采样名（证明外部模组 Def 泛型查询在正式实现里同样可用）

- [ ] **步骤 4：回退路径验收**

把配置 `C# Console Use Roslyn` 置 false → 重启 → 验证 #1、#7 仍通过、#5/#8 失败并给出可读诊断（这是回退的预期行为）。

- [ ] **步骤 5：Commit**

```bash
git add MCP/index.js docs/csharp-console-compiler-upgrade-feasibility.md README.md
git commit -m "docs: 控制台默认引用/using 与 Roslyn 能力说明，端到端验收"
```

---

## 任务 13：引用自愈（从编译诊断里读出缺哪个程序集并自动补）

**为什么要有这个任务**：实测证明缺引用这件事**可以现场查**——
```
5 引用下 `...DrawPos.y`  → CS0012: The type 'Vector3' is defined in an assembly that is not referenced.
                           You must add a reference to assembly 'UnityEngine.CoreModule, Version=0.0.0.0, ...'   ← 缺谁写在里面
补上 UnityEngine.CoreModule 后重试同一段 → OK                                                   ← 闭环成立
直接写 `UnityEngine.Time` → CS0103（**不带**程序集名，需按命名空间反查）
```
有了它，"引用集该多大"就不再是必须先答对的问题：**引用集恒为 21 个本体程序集（实测残差已归零），不设档位**；缺引用（清单外 Unity 模块、System 件、模组类型）时自动补齐、不用人/AI 猜——一切额外引用都由编译诊断触发。

**文件：**
- 创建：`src/RoslynBridge/MissingReferenceParser.cs`（纯逻辑：从诊断文本提名字）
- 创建：`src/RoslynBridge/ReferenceHealer.cs`（按程序集名 / 按命名空间解析到具体 dll）
- 修改：`src/RoslynBridge/RoslynCompiler.cs`（加 `CompileWithHealing` 重试入口）
- 修改：`src/RoslynBridge/RoslynScriptEvaluator.cs`（Evaluate 走自愈入口 + 日志）
- 修改：`Source/RoslynBridgeTests/Program.cs`（加 `TestMissingReferenceParser`、`TestHealerByNamespace`）
- 修改：`Source/RoslynBridgeTests/RoslynBridgeTests.csproj`（加两条 link）

```xml
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\MissingReferenceParser.cs" Link="MissingReferenceParser.cs" />
    <Compile Include="..\UE-4.9.0-official\UnityExplorer-4.9.0\src\RoslynBridge\ReferenceHealer.cs" Link="ReferenceHealer.cs" />
```

- [ ] **步骤 1：写失败测试**

```csharp
        static void TestMissingReferenceParser()
        {
            string cs0012 = "(1,106): error CS0012: The type 'Vector3' is defined in an assembly that is not referenced." +
                            " You must add a reference to assembly 'UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'.";
            Check("CS0012 → 取出程序集名", MissingReferenceParser.AssemblyFromCs0012(cs0012) == "UnityEngine.CoreModule",
                MissingReferenceParser.AssemblyFromCs0012(cs0012));
            Check("非 CS0012 返回 null", MissingReferenceParser.AssemblyFromCs0012("(1,1): error CS1002: ; expected") == null);

            string cs0103 = "(1,58): error CS0103: The name 'UnityEngine' does not exist in the current context";
            Check("CS0103 → 取出名字", MissingReferenceParser.SymbolFromCs0103(cs0103) == "UnityEngine",
                MissingReferenceParser.SymbolFromCs0103(cs0103));

            string cs0246 = "(1,43): error CS0246: The type or namespace name 'UnityEngine' could not be found";
            Check("CS0246 → 取出名字", MissingReferenceParser.SymbolFromCs0246(cs0246) == "UnityEngine",
                MissingReferenceParser.SymbolFromCs0246(cs0246));

            Check("不误报其它诊断", MissingReferenceParser.SymbolFromCs0103("(2,1): error CS0106: bad modifier") == null);
        }

        static void TestHealerByNamespace()
        {
            // net472 上的等价复现：只引 mscorlib，代码里写 System.Xml 的名字 → CS0246/CS0103，
            // 自愈器应在搜索目录里找到 System.Xml.dll 并补上，重试后编译成功。
            string mscorlib = typeof(object).Assembly.Location;
            string searchDir = System.IO.Path.GetDirectoryName(mscorlib);
            var healer = new ReferenceHealer(new[] { searchDir });
            var refs = new System.Collections.Generic.List<string> { mscorlib };

            var r = RoslynCompiler.CompileWithHealing("HealTest",
                "public class P { public static System.Xml.XmlDocument D; }", refs, true, healer, out string healLog);

            Check("自愈后编译成功", r.Success, r.Diagnostics);
            Check("refs 里多了 System.Xml", refs.Exists(p => p.EndsWith("System.Xml.dll", StringComparison.OrdinalIgnoreCase)),
                string.Join(";", refs));
            Check("自愈日志记录了一行", healLog.Contains("System.Xml"), healLog);
            Check("第二段无需自愈即可编译", RoslynCompiler.CompileWithHealing("HealTest2",
                "public class P2 { public static System.Xml.XmlDocument D; }", refs, true, healer, out string log2).Success && log2 == "");
        }
```

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：编译失败，报 `'MissingReferenceParser' could not be found`

- [ ] **步骤 3：实现解析器与自愈器**

`src/RoslynBridge/MissingReferenceParser.cs`：

```csharp
using System.Text.RegularExpressions;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>从 Roslyn 诊断文本里提取"缺哪个程序集 / 缺哪个名字"。实测 CS0012 会带程序集全名（含版本）。</summary>
    public static class MissingReferenceParser
    {
        static readonly Regex Cs0012 = new Regex(@"You must add a reference to assembly '([^,'\s]+)",
            RegexOptions.Compiled);
        static readonly Regex Named = new Regex(@"The (?:type or namespace name|name) '([^']+)'",
            RegexOptions.Compiled);

        public static string AssemblyFromCs0012(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics) || diagnostics.IndexOf("CS0012", System.StringComparison.Ordinal) < 0)
                return null;
            Match m = Cs0012.Match(diagnostics);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static string SymbolFromCs0103(string diagnostics)
            => NamedFrom(diagnostics, "CS0103");

        public static string SymbolFromCs0246(string diagnostics)
            => NamedFrom(diagnostics, "CS0246");

        static string NamedFrom(string diagnostics, string code)
        {
            if (string.IsNullOrEmpty(diagnostics) || diagnostics.IndexOf(code, System.StringComparison.Ordinal) < 0)
                return null;
            int at = diagnostics.IndexOf(code, System.StringComparison.Ordinal);
            Match m = Named.Match(diagnostics, at);
            if (!m.Success) return null;
            string name = m.Groups[1].Value;
            return name.Contains(".") ? name.Substring(0, name.IndexOf('.')) : name;   // 命名空间前缀
        }
    }
}
```

`src/RoslynBridge/ReferenceHealer.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>把"缺程序集/缺命名空间"解析成具体 dll 路径。只读元数据，不用 Assembly.LoadFrom（避免真加载本体 dll）。</summary>
    public sealed class ReferenceHealer
    {
        readonly List<string> s_dirs;
        readonly Dictionary<string, string> s_nsCache = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<string> Healed { get; } = new List<string>();

        public ReferenceHealer(IEnumerable<string> searchDirs)
        {
            s_dirs = searchDirs.Where(Directory.Exists).ToList();
        }

        /// <summary>返回应该补进引用集的 dll 路径；无法解析返回 null。</summary>
        public string Resolve(string diagnostics, ICollection<string> currentRefs)
        {
            // ⚠️ CS0433（类型同时存在于两个程序集）/ CS1704（重复的程序集标识）是"重复定义"，不是"缺引用"：
            //    再加引用只会让冲突更糟 —— 必须拒绝自愈，交给日志报给人看。
            if (diagnostics.IndexOf("CS0433", StringComparison.Ordinal) >= 0
                || diagnostics.IndexOf("CS1704", StringComparison.Ordinal) >= 0)
                return null;

            string asm = MissingReferenceParser.AssemblyFromCs0012(diagnostics);
            if (asm != null)
            {
                string p = FindByAssemblyName(asm, currentRefs);
                if (p != null) { Healed.Add(p); return p; }
            }
            string sym = MissingReferenceParser.SymbolFromCs0246(diagnostics)
                       ?? MissingReferenceParser.SymbolFromCs0103(diagnostics);
            if (sym != null)
            {
                string p = FindByNamespace(sym, currentRefs);
                if (p != null) { Healed.Add(p); return p; }
            }
            return null;
        }

        string FindByAssemblyName(string simpleName, ICollection<string> currentRefs)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (a.GetName().Name == simpleName && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
                        return a.Location;
                }
                catch { }
            }
            foreach (string dir in s_dirs)
            {
                string p = Path.Combine(dir, simpleName + ".dll");
                if (File.Exists(p) && !currentRefs.Contains(p)) return p;
            }
            return null;   // 同名 dll 已在引用集里（补了也没用）→ 交给调用方放弃
        }

        string FindByNamespace(string ns, ICollection<string> currentRefs)
        {
            if (s_nsCache.TryGetValue(ns, out string cached)) return cached;
            foreach (string dir in s_dirs)
            {
                foreach (string dll in Directory.GetFiles(dir, "*.dll"))
                {
                    if (currentRefs.Contains(dll)) continue;
                    if (!DefinesNamespace(dll, ns)) continue;
                    s_nsCache[ns] = dll;
                    return dll;
                }
            }
            s_nsCache[ns] = null;
            return null;
        }

        /// <summary>只读元数据判断该 dll 是否定义了命名空间 ns（用 Roslyn 读程序集符号，不加载程序集）。</summary>
        static bool DefinesNamespace(string dllPath, string ns)
        {
            try
            {
                MetadataReference mr = MetadataReference.CreateFromFile(dllPath);
                CSharpCompilation probe = CSharpCompilation.Create("probe", null, new[] { mr });
                if (!(probe.GetAssemblyOrModuleSymbol(mr) is IAssemblySymbol asm)) return false;
                INamespaceSymbol cur = asm.GlobalNamespace;
                foreach (string part in ns.Split('.'))
                {
                    INamespaceSymbol next = cur.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
                    if (next == null) return false;
                    cur = next;
                }
                return true;
            }
            catch { return false; }
        }
    }
}
```

- [ ] **步骤 4：给 `RoslynCompiler` 加自愈重试入口**

```csharp
        public static CompileResult CompileWithHealing(string assemblyName, string source,
            List<string> referencePaths, bool includePolyfills, ReferenceHealer healer, out string healLog)
        {
            healLog = "";
            // 引用集只有 21 个，首次编译常需连补 1~3 个（Unity 模块/其它 System 件/模组），故给 5 轮。
            for (int round = 0; round < 5; round++)
            {
                CompileResult r = Compile(assemblyName, source, referencePaths, includePolyfills);
                if (r.Success) return r;
                string add = healer?.Resolve(r.Diagnostics, referencePaths);
                if (add == null) return r;                  // 解析不出来：原样返回诊断
                referencePaths.Add(add);
                healLog += "自愈补引用：" + System.IO.Path.GetFileName(add) + System.Environment.NewLine;
            }
            return Compile(assemblyName, source, referencePaths, includePolyfills);
        }
```

- [ ] **步骤 5：运行测试确认通过**

运行：`dotnet run --project Source/RoslynBridgeTests`
预期：`ALL PASS`

- [ ] **步骤 6：接线到求值器（Evaluate 走自愈 + 日志）**

`RoslynScriptEvaluator` 构造时建 healer（搜索目录：`Managed` + 已加载程序集所在目录），`Evaluate` 改为：

```csharp
        public bool Evaluate(string input, out string output)
        {
            CompileResult r = RoslynCompiler.CompileWithHealing(
                "Repl_" + s_session.NextId, input, s_referencePaths, true, s_healer, out string healLog);
            if (healLog.Length > 0)
                ExplorerCore.Log("[CSConsole] " + healLog.TrimEnd());
            LastValue = s_session.LastValue;
            output = r.Diagnostics;
            return r.Success;
        }
```

- [ ] **步骤 7：游戏内验收（并入任务 12 矩阵执行）**

本任务只提交离线测试证据（`TestMissingReferenceParser`、`TestHealerByNamespace`）。自愈闭环的进程内证明由任务 12 矩阵 #16（手造 5 引用小集调 `CompileWithHealing`）与 #3/#17/#18（清单外模块靠自愈补齐）执行。

- [ ] **步骤 8：Commit**

```bash
git add Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/MissingReferenceParser.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/ReferenceHealer.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/RoslynCompiler.cs Source/UE-4.9.0-official/UnityExplorer-4.9.0/src/RoslynBridge/RoslynScriptEvaluator.cs Source/RoslynBridgeTests
git commit -m "feat(roslyn): 缺引用自愈（CS0012/CS0246 → 自动补引用重试）"
```

---

## 2. 自检记录

| 检查项 | 结果 |
|---|---|
| 规格覆盖：现有编译器识别 / 语言上限 / Roslyn 可行性 / 外部模组引用 / 默认引用与 using / 回退 / 补全 / Hook / 打包 / 许可 / 文案 | 任务 1–12 覆盖；「只升级 mcs.dll」结论在 §0 标注为**不做** |
| 用户新增需求（默认引用本体+模组） | 任务 2（引用集合）+ 任务 3（默认 using）+ 任务 12 步骤 1（MCP 文案）；游戏内验收并入任务 12 矩阵（原任务 2 步骤 6 / 任务 3 步骤 7 的验证点由矩阵 #1/#2/#4/#14/#17 覆盖） |
| 占位符扫描 | 无 TODO/待定；每个代码步骤给出可粘贴代码 |
| 类型一致性 | `IScriptEvaluator.Evaluate(string, out string)`（任务 7 定义）在任务 8/10 一致使用；`ReplSession.LastAssembly`（任务 10 增补）与 `RoslynScriptEvaluator.LastCompiledAssembly`（任务 10 使用）对应；`ReferenceSet.TryAdd(location, simpleName, blacklist)`（任务 2）在 `ReferenceScanner` 中按同签名调用；任务 8 额外补上 `Init()` 里 `Evaluator.Compile("0 == 0")` → `Evaluator.Evaluate("0 == 0", out _)` 的改法（否则 mcs 专用 API 残留会编译失败） |
| 依赖未定义类型（首轮自检发现并已修） | ① `ScriptInteraction.ClassesText` 是凭空捏造的 → 改为 `ScriptEvaluator.GetClasses()` 自带实现（mcs 内部反射搬过去），并给出完整代码；② 任务 8 用到任务 9 才创建的 `RoslynCompletion` → 任务 7 先返回空数组，任务 9 接入；③ 任务 9 的命名空间补全依赖 `ReplSession.AllNamespaces()` → 已在任务 9 步骤 3 给出实现（用 Roslyn 元数据遍历，**不**加载程序集）；④ 任务 5 首用任务 4 创建的 `PolyfillSource` → 任务 4 步骤 4 已注明与任务 5 合并提交 |
| 测试工程 link 覆盖（首轮自检发现并已修） | 每个新建纯逻辑文件都在对应任务的文件清单里给出 csproj link 行：`ReferenceSet`（任务 2）、`DefaultUsings`（任务 3）、`ReplSession`（任务 6）、`IScriptEvaluator`+`RoslynScriptEvaluator`（任务 7）、`RoslynCompletion`（任务 9）。**`ReferenceScanner` 依赖 `UnityEngine`/`ExplorerCore`，明确不 link**（否则 net472 侧编译失败），改由任务 12 矩阵验收 |
| 游戏内步骤可达性 | 游戏内验收按定稿集中：任务 8（首次部署 + 开关/回退）、任务 11（再部署 + 时延/体积）、任务 12（端到端矩阵，吸收任务 2/3/9/10/13 的验收点）；其余任务只跑离线 `dotnet run --project Source/RoslynBridgeTests`。部署命令在任务 8 步骤 5 与任务 11 步骤 5（目标 = Mods 游戏副本绝对路径） |

---

## 3. 执行说明

- **调度顺序：任务 1→10 → 任务 13 → 任务 11 → 任务 12**（自愈必须先于打包与最终验收——任务 11 的构建要包含任务 13，任务 12 矩阵的自愈行才验得了）。
- 游戏内验收只在三处执行：**任务 8**（首次部署 + 开关/回退）、**任务 11**（再部署 + 时延/体积）、**任务 12**（端到端矩阵，吸收任务 2/3/9/10/13 的验收点）；其余任务只跑离线测试。部署目标是**游戏加载的 Mods 副本**（绝对路径命令见任务 8 步骤 5 / 任务 11 步骤 5）——产物复制进仓库目录不会被游戏读到；部署后用 MCP `stop_game` / `start_quick_test` 重启进图。
- 若 Roslyn 载荷顺序出过问题（例如手工加载试验），**必须重启游戏进程**再继续——Mono 的失败缓存不可逆（可行性报告 §4.2）。
- 每完成一个任务跑一次 `dotnet run --project Source/RoslynBridgeTests`，全绿再提交。

### 3.1 游戏内验证的两个已知陷阱（都踩过）

1. **不要用 `Assembly.Load(简单名)` 拿 Roslyn 程序集**。Roslyn 载荷没加载时，Mono 的按名解析**不抛异常**，而是返回 `Assembly-CSharp`（实测），随后报出莫名其妙错位的 `missing Microsoft.CodeAnalysis.MetadataReference`。验证脚本一律：
   - 用 `Assembly.LoadFrom(<载荷绝对路径>)` 显式加载（顺序见任务 1），**或**
   - 按**全名匹配**在 `AppDomain.CurrentDomain.GetAssemblies()` 里找（`GetName().Name == "Microsoft.CodeAnalysis" && Version.Major >= 4`）。
2. **不要对本体 `Managed` 目录里的 106 个 dll 做 `Assembly.LoadFrom`**。那是**真加载**（会往进程里塞第二份 UnityEngine 模块），实测在一次批量 `LoadFrom` 之后游戏进程重启了。**要用元数据就用 `MetadataReference.CreateFromFile`（只读元数据、不加载）**；反射式普查只对**已经加载**的 `Assembly-CSharp` 取 `GetExportedTypes()`（它引用的 UnityEngine 类型会由 Unity 自己已加载的模块解析）。
3. 每条游戏内验证命令控制在 **~10 秒以内**：`execute_csharp_code` 有 10000 ms 超时。**先把 `MetadataReference` 列表构建好再复用**——每次编译都重建 106 个引用会让一次调用直接超时（实测）。批量用例请拆成多次调用，每次 ≤8 条。

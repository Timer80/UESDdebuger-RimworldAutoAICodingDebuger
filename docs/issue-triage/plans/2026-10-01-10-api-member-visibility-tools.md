# API 成员可见性查询工具化 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让 MCP 的 `find_methods` 在结果里直接带出 Cecil 已经在手、却被丢弃的成员可见性与完整签名元数据，并在文档里写明「成员可见性到底该去哪问」，从根上消灭「`ThingMaker.MakeThing` 是只有单参重载还是带默认参的两参方法」「`Thing.Position` 有没有 public setter」这类**相反结论**。

**架构：** 纯增量、零第三方改动。在 `McpRimDebug` 内新增一个不碰 VM 的纯函数类 `CecilMethodSummary`（Cecil `MethodDefinition` → 签名串 + 新字段 + 去重键），由 `find_methods` 的既有结果构造函数调用；改动点全部落在已持有 `MethodDefinition` 的那一行上。离线场景（游戏没跑、正在写代码）由另一条腿补齐：`inspect_type` 的工具描述如实化 + 文档写明 `decompilerserver` 才是离线的成员查询入口。

**技术栈：** .NET 10 / C# 13（`McpRimDebug.csproj`，`Mono.Cecil` 0.10.4 + `ModelContextProtocol` 2.1.0）、Node.js ESM（`MCP/`，`node --test`）。

---

## 0. 给执行者的背景（你没有上文，请先读完这一节）

### 0.1 问题从哪来

维护者在 `docs/mcp-吐槽板.md:458-478` 的条目 `[10]` 记录：想知道某个 vanilla / 模组成员是否 public，只能靠反编译或撞编译错，**实战里连续踩了两次相反的结论**，两次都把错误结论写进了分计划正文。本计划解决的就是这条。

上游裁决报告在 `docs/issue-triage/triage/10-api-member-visibility.md`（裁决：值得修，严重度复评为 🟠）。裁决报告用只读反编译实证过两条事实，本计划完全采信：

- `Verse.ThingMaker` 只有一个成员：`public static Thing MakeThing(ThingDef def, ThingDef stuff = null)` —— **带默认参**，不是「只有单参重载」。按「单参重载」理解会写出 `stuff=null` 的 error 级日志。
- `Verse.Thing.Position` **有 public setter** —— 不是「不可写、须反射 `_position`」。

### 0.2 仓库的真实形态（动手前必须知道）

- 仓库根：`C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger`（下称 `<MOD>`）。所有路径相对它。
- **本仓库不是 git 仓库**（`git rev-parse --is-inside-work-tree` 报 `fatal: not a git repository`）。因此 `writing-plans` 技能惯用的「每步 commit」在本计划中**全部替换为「构建 + 测试全绿」**，不要执行任何 `git` 命令。
- `McpRimDebug` 的测试就是它自己的 `--selftest`，入口在 `McpRimDebug/Program.cs:30-31`（`args[0] == "--selftest"` → `RunSelfTest()`）。项目文档 `McpRimDebug/README.md:22-28` 记录的口径是：

  ```powershell
  cd <MOD>\McpRimDebug
  dotnet build
  .\bin\Debug\net10.0\McpRimDebug.exe --selftest
  ```

  本计划统一使用等价但更省事的根目录形式（会隐式重新构建）：

  ```powershell
  dotnet run --project McpRimDebug -- --selftest
  ```

  自检**全程不需要游戏在跑**，也不连 VM。
- `MCP/`（Node 侧）有真测试：`MCP/package.json:9` 定义 `"test": "node --test \"test/*.test.js\""`，共 9 个测试文件（`MCP/test/*.test.js`）。运行：

  ```powershell
  npm --prefix MCP test
  ```

  这些测试导入 `index.js` 前会先设 `process.env.UESD_MCP_NO_START = '1'`（见 `MCP/test/toolCountConsistency.test.js:4-6`），因此**不会**启动服务器或游戏，可以放心跑。
- **编译包含规则（极易踩坑）**：`McpRimDebug/McpRimDebug.csproj:20` 设了 `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>`，第 33 行只 `<Compile Include="*.cs" />`（**仅项目根目录**），第 31 行补 `SdbClient\*.cs`。**新建的 `.cs` 文件必须直接放在 `McpRimDebug/` 根目录，放进任何子目录都不会被编译。**
- 语言版本 `LangVersion 13.0`、未启用隐式 using 与可空上下文（`McpRimDebug.csproj:11-12,18`）。新文件必须写 `using`。

### 0.3 已复核的事实与行号（本计划作者逐条读原文确认）

| 事实 | 位置 | 备注 |
|---|---|---|
| `AddMethodResult` 只构造 `{ type, method }` | `McpRimDebug/DebugSession.Eval.cs:355-367` | 全文 13 行 |
| 去重键 `typeFullName + ":" + methodName` | `McpRimDebug/DebugSession.Eval.cs:359` | **同名重载会被合并成一条**，见 §0.5 |
| 调用点唯一，手上就是 `MethodDefinition m`，却只传 `m.Name` | `McpRimDebug/DebugSession.Eval.cs:507`（`foreach` 在 499-508） | 全仓库仅此一个调用点 |
| `FindMethods` 的 XML 注释写着「返回 {type, method} 列表」 | `McpRimDebug/DebugSession.Eval.cs:287` | 工具描述之外的第二处文案 |
| `find_methods` 工具描述写死「返回 {type, method} 列表」 | `McpRimDebug/RimDebugTools.cs:86-88`（描述在 87） | |
| `inspect_type` 工具定义与描述 | `MCP/index.js:1956-1968`（名称 1956，描述 1957） | 该文件全部描述都是**单行**字符串，无 `+` 拼接先例 |
| `inspect_type` 转发 | `MCP/index.js:4421-4430` | |
| 游戏侧 `InspectType` 只返回 `{typeName, message}` | `Source/UELoader/UEHttpHandler.cs:120-161`（返回体在 156-160） | |
| selftest 的 schema 完整性校验 | `McpRimDebug/Program.cs:107-144`；`paramAsserts` 字典 110-122 | 只校验**输入** schema，本计划不新增输入参数，故**不需要改这里** |
| selftest 各纯逻辑子测试的调用序列 | `McpRimDebug/Program.cs:174-176, 390-396`，最后一个是 `RunSafeAccessorSelfTest()`（编号 15） | 新增的子测试编为 **16** |
| selftest 成功汇总行 | `McpRimDebug/Program.cs:398-402` | 需追加新子测试名 |
| `README.md` 的 `inspect_type` 表格行 | `README.md:88` | |
| `README.md` 的 `find_methods` 表格行 | `README.md:226` | |
| `McpRimDebug/README.md` 的 `find_methods` 表格行 | `McpRimDebug/README.md:147`，紧随的返回结构说明在 149 | |
| `AI-DEPLOYMENT.md` §7 末尾（`---` 在 160 行之前） | `AI-DEPLOYMENT.md:128-159` | 追加新小节的位置 |
| `e2e` 断点驱动只读 `m.type` / `m.method` | `McpRimDebug/e2e-driver/rst-breakpoint-test.js:60` | 容忍新增字段，不会被本改动打破 |

**对裁决报告的修正（裁决报告漏掉或说错的地方，本计划以复核结果为准）：**

1. **裁决报告完全没提去重键** `DebugSession.Eval.cs:359`。它才是「看起来像答案、其实不足以判断」的成因 —— 同名重载会被合并成一条。本计划把「去重键含完整签名」列为任务 1 的必做项，并配回归测试。
   （说明：`Verse.ThingMaker` 那个例子**恰好**没被这条吃掉，因为该类型总共只有 1 个成员；但一般情况下会。）
2. 裁决报告 §6.1 提到要在 `Program.cs:120-121` 的 `paramAsserts` 补一行 —— 复核后 `find_types` 在 120、`find_methods` 在 121，区间无误；但**本计划不需要改那里**，因为新增的是响应字段而非输入参数，schema 校验不受影响。
3. 裁决报告只列了 `RimDebugTools.cs:86-88` 一处文案，漏了 `DebugSession.Eval.cs:287` 的同款 XML 注释。两处一并改。
4. 裁决报告建议的字段里没有 `isSpecialName`。而吐槽板的**第二个**坑（`Thing.Position` 有没有 setter）对应的 IL 成员正是 `set_Position`；`isSpecialName` 是让调用方一眼认出「这是属性/运算符访问器而非普通方法」的最低成本字段，补上。
5. Cecil 0.10.4 的一个实现细节（本计划作者反编译 `Mono.Cecil.dll` 0.10.4 `netstandard1.3`，MVID `a8b53d9b47b84463ad310aa3efcd97c3` 核实）：`ParameterDefinition.Constant` 的 setter **只写字段，不会把 `HasConstant` 置真**；`HasConstant` 的 getter 会调 `ResolveConstant` 再与内部哨兵 `NoValue` 比较，而 `ResolveConstant` 在 `module.HasImage()` 为假时会把常量重置为 `NoValue`。
   → 由此定下实现铁律：**只有当 `ParameterDefinition.IsOptional` 为真时才去读 `Constant`**，普通参数一律不碰，从根上避开这条解析路径。

   任务 1 的测试代码用到的 Cecil API 已逐条核实存在（同一个 MVID）：

   | API | 核实结果 |
   |---|---|
   | `AssemblyDefinition.CreateAssembly(AssemblyNameDefinition, string, ModuleKind)` | public static ✅ |
   | `AssemblyNameDefinition..ctor(string, Version)` / `AssemblyDefinition.Dispose()` | public ✅ |
   | `ModuleDefinition.TypeSystem`（`.Void` / `.Int32`）、`ModuleDefinition.Assembly` | public ✅（注意：**没有** `Module` 属性，反查程序集用 `Assembly`） |
   | `ModuleDefinition : ModuleReference : IMetadataScope` | ✅ 故 `new TypeReference(ns, name, mod, mod)` 合法（scope 传模块自身） |
   | `MethodDefinition..ctor(string name, MethodAttributes, TypeReference)` | public ✅ |
   | `MethodDefinition.IsPublic / IsStatic / IsVirtual / IsSpecialName` | public ✅ |
   | `ParameterDefinition..ctor(string name, ParameterAttributes, TypeReference)`、`IsOptional`、`Constant` | public ✅ |
   | `MethodAttributes.{Public, Assembly, Static, Virtual, SpecialName, HideBySig}`、`ParameterAttributes.{None, Optional}` | public 枚举成员全部存在 ✅ |
   | 命名空间是 `Mono.Cecil`（**不是** `Mono.Cecil.Cil`），故 `using Mono.Cecil;` 即够 | ✅ 与 `McpRimDebug/DebugSession.Eval.cs:3` 现有写法一致 |

### 0.4 本计划明确不做什么（以及为什么）

- **不给 `inspect_type` 的返回体加成员数据。** 三条理由：(a) 载荷无上界，`Verse.Thing` 这类类型有几百个成员，回灌进 MCP 上下文必然撑爆；(b) 它只在 `IN_GAME` 且 UE 面板就绪时可用，而需求发生的时刻恰恰是「还没进图、正在写代码」；(c) 它的正确定位就是「在人类可见的 UI 上开面板」。对它的正确修法是**如实描述**，即任务 2。
- **不新增 `describe_type` 这类成员级新工具**（裁决报告 §6.2 的可选项）。在线场景由改造后的 `find_methods` 覆盖，离线场景由任务 3 写明的 `decompilerserver` 覆盖，再加一个工具属于 YAGNI，且需要真实游戏才能验收。
- **不改任何第三方代码**：`Source/UE-4.9.0-official/`、`Source/UniverseLib-official/`、`McpRimDebug/SdbClient/` 一律不动。
- **不碰吐槽板本体** `docs/mcp-吐槽板.md`（条目 `[10]` 的状态由主代理更新，不在本计划范围）。
- **不把吐槽板条目 `[29]` 并进本计划的任何任务**（裁决：`triage/29-get-members-of-type-silent-truncation-and-overloads-self-exclusion.md`）。两者是**两套独立实现**，改一处不解决另一处：

  | | 本计划 / `find_methods` | `[29]` / `get_members_of_type` |
  |---|---|---|
  | 代码位置 | 本仓库 `McpRimDebug/DebugSession.Eval.cs`（`:290` `FindMethods`、`:355-367` `AddMethodResult`、`:499` 遍历） | **外部** `decompilerserver` MCP，源码不在本仓库 |
  | 枚举方式 | `foreach (TypeDefinition t …)`，泛型与非泛型在 Cecil 里 `FullName` 天然不同名（`Verse.ThingOwner\`1`）→ **结构上免疫重名** | 按字符串类型名走名字索引 → **正中靶心** |
  | 完整性承诺 | 诚实：命中上限时 `truncated = true`（`DebugSession.Eval.cs:501-505`） | 谎报：`hasMore:false` 配一个偏小的 `totalEstimate` |

  ⇒ `[29]` 在本计划里**只以「文档措辞」的形式存在**：任务 1 步骤 7、任务 2 步骤 1、任务 3 步骤 3/4/5 的文案里，都已写明两条例外：(a) **每次调用 decompilerserver 都必须显式带 `contextAlias:"vanilla"`**（条目 `[37]`，见 `triage/37-search-symbols-current-context-alias-and-zero-hit-audit.md`）—— current context 是跨会话共享的可变状态，不带 alias 会把一个正确的类型名报成「不存在」；(b) `typeId` 的**点分全限定名可以照常用**（条目 `[38]` 已实测 `Verse.AI.Pathing`、`Verse.AI.GenPath`、三点的 `Verse.AI.Pathing.DisableDirtyingScope` 均可直接解析，见 `triage/38-typeid-dotted-namespace-parsing.md`），**只有同名碰撞时**才需要改传 canonical type memberId，且交叉信号是 `search_members` 与 `get_members_of_type` 的 `totalEstimate` 对不上。**不要**为此新增代码任务或改动验收方式 —— 本计划的全部验收都在离线 `--selftest` 里完成，不依赖任何外部 MCP 服务。

### 0.5 向后兼容约定（必须逐字满足）

`find_methods` 的响应结构：

```
{ ok, message, data: { query, limit, count, truncated, methods: [ ... ] } }
```

- **顶层字段 `query` / `limit` / `count` / `truncated` / `methods` 全部保持不变。**
- **每条结果里既有的 `type`、`method` 两个字段原样保留、值不变。**
- 新增 6 个字段，**只增不换**：`signature`、`isPublic`、`isStatic`、`isVirtual`、`hasOptionalParameters`、`isSpecialName`。
- **唯一的行为变化**：去重键含完整签名后，同名重载不再被合并，因此 `count` 可能变大、`methods` 可能变长、命中 `limit` 的时机可能提前。这是修复本身。`truncated` 的语义不变，命中时 `message` 本来就会追加「达到扫描/结果上限，结果不完整」，调用方按原样处理即可。
- 已在库内读取 `find_methods` 结果的唯一消费者 `McpRimDebug/e2e-driver/rst-breakpoint-test.js:60` 只读 `m.type` 与 `m.method || m.name`，兼容。

---

## 1. 文件结构

动手前先锁定文件清单与职责。新增/修改的文件一律按「一个文件一件事」划分，沿用仓库既有命名与注释风格（中文 XML 文档注释、`// ---- 分节 ----` 注释线、`Console.Error.WriteLine("selftest 失败: …")` 报错风格）。

| 文件 | 动作 | 职责 |
|---|---|---|
| `McpRimDebug/CecilMethodSummary.cs` | **新建**（必须放在 `McpRimDebug/` 根目录） | 唯一的纯逻辑单元：`MethodDefinition` → C# 风格签名串、结果条目新增字段、去重键。不碰 VM / socket / socket 元数据，可被 `--selftest` 离线单测。 |
| `McpRimDebug/DebugSession.Eval.cs` | 修改 `:287`、`:355-367`、`:507` | `AddMethodResult` 改为接收 `MethodDefinition` 并走 `CecilMethodSummary`；去重键换成含签名的；唯一调用点改传 `m`；XML 注释同步。 |
| `McpRimDebug/RimDebugTools.cs` | 修改 `:87` | `find_methods` 的工具描述写清新字段与重载语义（会自动经 `MCP/index.js:1737` 转发到 `tools/list`，`index.js` 无需镜像）。 |
| `McpRimDebug/Program.cs` | 修改 `:11`（加 1 行 `using`）、`:394-402`（新增子测试调用 + 汇总行），文件末尾新增 2 个私有方法 | 第 16 组纯逻辑 selftest：`RunCecilMethodSummarySelfTest()` 及其 `RefType` 夹具。 |
| `MCP/index.js` | 修改 `:1957` | `inspect_type` 描述如实化：不返回任何成员数据，并指路到正确工具。 |
| `README.md` | 修改 `:88`、`:226`，新增一个小节 | 两处工具清单表补说明 + 新增「成员可见性该去哪问」。 |
| `McpRimDebug/README.md` | 修改 `:147`、`:149` | `find_methods` 行补字段说明 + 返回结构段补充。 |
| `AI-DEPLOYMENT.md` | 修改：在 `:158` 之后、`:160` 的 `---` 之前新增小节；`:176` 之后表格新增 1 行 | 给 AI 部署场景的「去哪问」指路与排障条目。 |

**任务的切分理由（为什么是 3 个而不是 1 个或 2 个）：**

- **任务 1（C# 代码 + selftest）** 与 **任务 2（`MCP/index.js` 一行文案）** 必须拆开：两者评审面完全不同 —— 前者是带内存 Cecil 构造的新代码，后者是 5460 行 Node 文件里的一条英文字符串，且只依赖自身、不依赖任务 1 的产物。审查者完全可能否掉任务 2 的措辞、同时批准任务 1。
- **任务 2** 与 **任务 3（3 个 Markdown）** 也必须拆开：任务是「让工具不再误导」，任务是「告诉人正确答案在哪」，是可独立发布的两件事；纯文案改动无构建成本，可以当天单独上。
- 任务 1 内部**不再拆**：`CecilMethodSummary`、`AddMethodResult` 改造、selftest 是同一个可测试交付物，拆开会产生「新文件存在但没人调用」的中间态。

---

### 任务 1：`find_methods` 直接给出成员可见性与完整签名

**文件：**
- 创建：`McpRimDebug/CecilMethodSummary.cs`
- 修改：`McpRimDebug/Program.cs:11`、`McpRimDebug/Program.cs:394-402`、`McpRimDebug/Program.cs`（末尾追加 2 个私有方法）
- 修改：`McpRimDebug/DebugSession.Eval.cs:287`、`McpRimDebug/DebugSession.Eval.cs:355-367`、`McpRimDebug/DebugSession.Eval.cs:507`
- 修改：`McpRimDebug/RimDebugTools.cs:87`
- 测试：内建在 `McpRimDebug --selftest`（无独立测试工程）

- [ ] **步骤 1：写入失败的自检**

  先在 `McpRimDebug/Program.cs` 第 11 行 `using Mono.Debugger.Soft;` **之前**插入一行（`Mono.Cecil` 按字母序在前）：

  ```csharp
  using Mono.Cecil;
  ```

  再把下面四个私有方法整体追加到 `Program.cs` 文件**末尾的最后一个 `}` 之前**（即 `RunTruncationSinkSelfTest()` 所在方法的闭合大括号之后、类的闭合大括号之前）。放在末尾而不是插在中间，是为了不打断 `RunSelfTest()` 里 15 组子测试的既有编号顺序。

  ```csharp
        /// <summary>selftest 夹具：造一个内存 Cecil 模块（CreateAssembly 产生的模块无元数据图片，但 TypeSystem 已就绪）。</summary>
        private static ModuleDefinition NewSelfTestModule()
        {
            AssemblyDefinition asm = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("FindMethodsSelfTest", new System.Version(1, 0, 0, 0)),
                "FindMethodsSelfTest.dll", ModuleKind.Dll);
            return asm.MainModule;
        }

        /// <summary>selftest 夹具：造一个带命名空间的类型引用。</summary>
        private static TypeReference RefType(ModuleDefinition mod, string ns, string name)
        {
            return new TypeReference(ns, name, mod, mod);
        }

        /// <summary>
        /// selftest 断言夹具：字典里该键必须存在、且其装箱 bool 值等于期望值。
        /// 不直接写 <c>entry[key] != (object)true</c>，避免依赖「装箱基类型值的引用相等」这一语言细节，
        /// 同时顺带把「键缺失」也判成失败。
        /// </summary>
        private static bool Flag(Dictionary<string, object> entry, string key, bool expected)
        {
            return entry.TryGetValue(key, out object v) && v is bool b && b == expected;
        }

        /// <summary>
        /// 第 16 组：find_methods 成员元数据（CecilMethodSummary）纯逻辑断言（不连 VM、不连游戏）。
        ///
        /// 用 Mono.Cecil 在内存里造方法对象，直接对 AddMethodResult 的真实实现断言：
        /// 1) 带默认参的方法签名渲染成 `... = null`（对应「单参重载 vs 带默认参」误判）；
        /// 2) 同名不同签名的方法去重键不同 —— 各占一条，不再像旧键 `type:method` 那样被合并；
        /// 3) 同一方法重复添加仍然只留一条（去重没被改坏）；
        /// 4) isPublic / isStatic / isVirtual / hasOptionalParameters / isSpecialName 映射正确；
        /// 5) 既有 type / method 字段原样保留（向后兼容）。
        /// </summary>
        private static bool RunCecilMethodSummarySelfTest()
        {
            AssemblyDefinition asm = null;
            try
            {
                ModuleDefinition mod = NewSelfTestModule();
                asm = mod.Assembly;   // ModuleDefinition 没有 Module 属性，反查所属程序集用 Assembly

                // --- 用例 1：public static + 1 必参 + 1 个「= null」可选参（复刻 Verse.ThingMaker.MakeThing 形态） ---
                var makeThingTwoArgs = new MethodDefinition("MakeThing",
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                    RefType(mod, "Verse", "Thing"));
                makeThingTwoArgs.Parameters.Add(new ParameterDefinition("def",
                    ParameterAttributes.None, RefType(mod, "Verse", "ThingDef")));
                var stuffParam = new ParameterDefinition("stuff",
                    ParameterAttributes.Optional, RefType(mod, "Verse", "ThingDef"));
                stuffParam.Constant = null;   // C# 的 `= null`：Cecil 0.10.4 显式赋值即生效
                makeThingTwoArgs.Parameters.Add(stuffParam);

                string sigTwo = CecilMethodSummary.FormatSignature(makeThingTwoArgs);
                if (sigTwo != "Verse.Thing MakeThing(Verse.ThingDef def, Verse.ThingDef stuff = null)")
                {
                    Console.Error.WriteLine(
                        "selftest 失败: 带默认参的方法签名应为 `Verse.Thing MakeThing(Verse.ThingDef def, Verse.ThingDef stuff = null)`，实际: " + sigTwo);
                    return false;
                }

                // --- 用例 2：同名单参重载（对应「只有单参重载」那种误判的另一半） ---
                var makeThingOneArg = new MethodDefinition("MakeThing",
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                    RefType(mod, "Verse", "Thing"));
                makeThingOneArg.Parameters.Add(new ParameterDefinition("def",
                    ParameterAttributes.None, RefType(mod, "Verse", "ThingDef")));

                string sigOne = CecilMethodSummary.FormatSignature(makeThingOneArg);
                if (sigOne != "Verse.Thing MakeThing(Verse.ThingDef def)")
                {
                    Console.Error.WriteLine(
                        "selftest 失败: 单参重载签名应为 `Verse.Thing MakeThing(Verse.ThingDef def)`，实际: " + sigOne);
                    return false;
                }

                // --- 用例 3：同名两参（带默认参）经 AddMethodResult 后必须各占一条 ---
                var results = new List<object>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                DebugSession.AddMethodResult(results, seen, "Verse.ThingMaker", makeThingOneArg);
                DebugSession.AddMethodResult(results, seen, "Verse.ThingMaker", makeThingTwoArgs);
                if (results.Count != 2)
                {
                    Console.Error.WriteLine(
                        "selftest 失败: 同名重载必须各占一条（旧去重键 type:method 会合并成 1 条），实际条数: " + results.Count);
                    return false;
                }

                // --- 用例 4：同一个方法重复添加仍只留一条（去重没被改坏） ---
                DebugSession.AddMethodResult(results, seen, "Verse.ThingMaker", makeThingTwoArgs);
                if (results.Count != 2)
                {
                    Console.Error.WriteLine(
                        "selftest 失败: 同一方法重复添加应被去重，实际条数: " + results.Count);
                    return false;
                }

                // --- 用例 5：条目字段齐全，且既有 type/method 未被改（向后兼容） ---
                var twoArgEntry = results[1] as Dictionary<string, object>;
                if (twoArgEntry == null)
                {
                    Console.Error.WriteLine("selftest 失败: find_methods 结果条目应为 Dictionary<string, object>");
                    return false;
                }
                if (!string.Equals(twoArgEntry["type"] as string, "Verse.ThingMaker", StringComparison.Ordinal)
                    || !string.Equals(twoArgEntry["method"] as string, "MakeThing", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "selftest 失败: 既有 type/method 字段的值必须保持不变，实际: "
                        + twoArgEntry["type"] + " / " + twoArgEntry["method"]);
                    return false;
                }
                if (!string.Equals(twoArgEntry["signature"] as string, sigTwo, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("selftest 失败: 条目 signature 字段应为 " + sigTwo
                        + "，实际: " + twoArgEntry["signature"]);
                    return false;
                }
                if (!Flag(twoArgEntry, "isPublic", true) || !Flag(twoArgEntry, "isStatic", true)
                    || !Flag(twoArgEntry, "isVirtual", false)
                    || !Flag(twoArgEntry, "hasOptionalParameters", true)
                    || !Flag(twoArgEntry, "isSpecialName", false))
                {
                    Console.Error.WriteLine("selftest 失败: public static 带默认参方法的布尔字段映射错误（isPublic/isStatic/isVirtual/hasOptionalParameters/isSpecialName）");
                    return false;
                }

                // --- 用例 6：非 public 的 static 方法（可见性真的会不同） ---
                var internalTick = new MethodDefinition("Tick",
                    MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig,
                    mod.TypeSystem.Void);
                var entry2 = new Dictionary<string, object> { ["type"] = "Verse.Thing", ["method"] = "Tick" };
                CecilMethodSummary.FillExtraFields(entry2, internalTick);
                if (!Flag(entry2, "isPublic", false) || !Flag(entry2, "isStatic", true)
                    || !Flag(entry2, "hasOptionalParameters", false)
                    || !string.Equals(entry2["signature"] as string, "System.Void Tick()", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("selftest 失败: internal static void Tick() 的字段映射错误，signature 实际: "
                        + entry2["signature"]);
                    return false;
                }

                // --- 用例 7：virtual 方法 ---
                var virtualDraw = new MethodDefinition("Draw",
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                    mod.TypeSystem.Void);
                var entry3 = new Dictionary<string, object>();
                CecilMethodSummary.FillExtraFields(entry3, virtualDraw);
                if (!Flag(entry3, "isVirtual", true) || !Flag(entry3, "isPublic", true))
                {
                    Console.Error.WriteLine("selftest 失败: public virtual void Draw() 的 isVirtual/isPublic 映射错误");
                    return false;
                }

                // --- 用例 8：属性访问器（对应「Thing.Position 有没有 setter」那个坑的 IL 侧） ---
                var positionSetter = new MethodDefinition("set_Position",
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                    mod.TypeSystem.Void);
                positionSetter.Parameters.Add(new ParameterDefinition("value",
                    ParameterAttributes.None, mod.TypeSystem.Int32));
                var entry4 = new Dictionary<string, object>();
                CecilMethodSummary.FillExtraFields(entry4, positionSetter);
                if (!Flag(entry4, "isSpecialName", true)
                    || !string.Equals(entry4["signature"] as string, "System.Void set_Position(System.Int32 value)", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("selftest 失败: 属性访问器应被识别为 isSpecialName=true，signature 实际: "
                        + entry4["signature"]);
                    return false;
                }

                // --- 用例 9：非 null 的可选参数默认值 ---
                var withIntDefault = new MethodDefinition("Add",
                    MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                    mod.TypeSystem.Int32);
                withIntDefault.Parameters.Add(new ParameterDefinition("a",
                    ParameterAttributes.None, mod.TypeSystem.Int32));
                var countParam = new ParameterDefinition("count",
                    ParameterAttributes.Optional, mod.TypeSystem.Int32);
                countParam.Constant = 5;
                withIntDefault.Parameters.Add(countParam);
                string sigAdd = CecilMethodSummary.FormatSignature(withIntDefault);
                if (sigAdd != "System.Int32 Add(System.Int32 a, System.Int32 count = 5)")
                {
                    Console.Error.WriteLine(
                        "selftest 失败: 标量默认值签名应为 `System.Int32 Add(System.Int32 a, System.Int32 count = 5)`，实际: " + sigAdd);
                    return false;
                }

                Console.WriteLine("    [成员元数据自测] find_methods 签名/可见性/static/virtual/默认参/特殊名映射正确，"
                    + "同名重载不再被去重合并");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("selftest 失败: 成员元数据自测异常: " + ex.Message);
                return false;
            }
            finally
            {
                if (asm != null)
                    asm.Dispose();
            }
        }
  ```

- [ ] **步骤 2：在 `RunSelfTest` 里挂上第 16 组**

  在 `McpRimDebug/Program.cs:394-396`（`// ---- 15) P2-MD-5：Safe* 访问器 …` 那三行）之后、`Program.cs:398` 的 `Console.WriteLine("selftest 通过: …")` 之前，插入：

  ```csharp
                  // ---- 16) find_methods 成员元数据：签名 / 可见性 / 默认参 / 重载不合并（纯逻辑，不连 VM） ----
                  if (!RunCecilMethodSummarySelfTest())
                      return 2;
  ```

  同时把 `Program.cs:398-402` 的汇总行改成（仅在末尾追加一项，其余原样保留）：

  ```csharp
                  Console.WriteLine("selftest 通过: 20 个工具注册齐全，schema 完整，未连接行为正确，"
                      + "配置环境变量生效，统一异常包装可用，表达式解析器自测通过，"
                      + "P1-MD-2 事件队列上限 + P1-MD-1 命令锁忙警告 + P1-MD-2.2 TruncationSink + "
                      + "P1-MD-3 句柄分批回收 + P2-MD-3 枚举去重 + P2-MD-4 端口探测缓存 + "
                      + "P2-MD-1 端口发现全文件兜底 + P2-MD-5 Safe* 访问器 + "
                      + "find_methods 成员元数据 纯逻辑断言通过");
  ```

- [ ] **步骤 3：运行自检，确认失败**

  运行：

  ```powershell
  dotnet run --project McpRimDebug -- --selftest
  ```

  预期：**编译失败**，报 `error CS0103: 当前上下文中不存在名称 'CecilMethodSummary'`（`Program.cs` 里已引用它，`CecilMethodSummary.cs` 尚未创建）。退出码非 0。
  这是 C# 语言下 TDD 的红灯形态 —— 与「运行后断言失败」等价。

- [ ] **步骤 4：创建纯逻辑实现 `McpRimDebug/CecilMethodSummary.cs`**

  新建文件，路径必须是 `McpRimDebug/CecilMethodSummary.cs`（**不要放进任何子目录**，见 §0.2）：

  ```csharp
  using System.Collections.Generic;
  using System.Text;
  using Mono.Cecil;

  namespace McpRimDebug
  {
      /// <summary>
      /// 把 Mono.Cecil 的 MethodDefinition 折算成 find_methods 结果里直接可读的成员元数据：
      /// 完整 C# 风格签名、可见性、是否 static / virtual、是否含默认参、是否特殊名访问器，以及去重键。
      ///
      /// 纯逻辑：不碰 VM、不做 socket 通信，全部输入来自调用方手里已有的 MethodDefinition，
      /// 因此可在 --selftest 里用内存 Cecil 对象离线单测。
      /// </summary>
      internal static class CecilMethodSummary
      {
          /// <summary>
          /// C# 风格完整签名：返回类型全名 + 空格 + 方法名 + 括号参数表，可选参数补「 = 默认值」。
          /// 例：Verse.Thing MakeThing(Verse.ThingDef def, Verse.ThingDef stuff = null)
          ///
          /// 注意：只有 IsOptional 为真的参数才读 Constant。Mono.Cecil 0.10.4 的 Constant setter
          /// 只写字段、不置 HasConstant，而 HasConstant 会走 ResolveConstant（无元数据图片时把常量
          /// 重置为 NoValue）；对非可选参数读 Constant 既无意义又有踩这条路径的风险。
          /// </summary>
          public static string FormatSignature(MethodDefinition m)
          {
              if (m == null)
                  return null;

              var sb = new StringBuilder();
              sb.Append(m.ReturnType != null ? m.ReturnType.FullName : "?");
              sb.Append(' ').Append(m.Name).Append('(');
              for (int i = 0; i < m.Parameters.Count; i++)
              {
                  if (i > 0)
                      sb.Append(", ");
                  sb.Append(FormatParameter(m.Parameters[i]));
              }
              sb.Append(')');
              return sb.ToString();
          }

          static string FormatParameter(ParameterDefinition p)
          {
              string typeName = p.ParameterType != null ? p.ParameterType.FullName : "?";
              if (!p.IsOptional)
                  return typeName + " " + p.Name;
              return typeName + " " + p.Name + " = " + FormatConstant(p.Constant);
          }

          /// <summary>默认值的可读形式；枚举型参数会落到常量分支，按底层整数值输出。</summary>
          static string FormatConstant(object c)
          {
              if (c == null)
                  return "null";
              if (c is string s)
                  return "\"" + s + "\"";
              if (c is bool b)
                  return b ? "true" : "false";
              return c.ToString();
          }

          /// <summary>
          /// 结果条目去重键：类型全名 + 方法名 + 完整签名。
          /// 签名必须在键里 —— 少了它同名重载会被合并成一条，调用方就无法分辨
          /// 「只有单参重载」与「一个带默认参的两参方法」，这正是 find_methods 过去最大的误导来源。
          /// </summary>
          public static string DedupKey(string typeFullName, MethodDefinition m)
          {
              if (m == null)
                  return typeFullName + ":" + "";
              return typeFullName + ":" + m.Name + ":" + FormatSignature(m);
          }

          /// <summary>
          /// 往既有结果条目上**追加**新字段（不删除、不改写已有键）：
          /// signature / isPublic / isStatic / isVirtual / hasOptionalParameters / isSpecialName。
          /// </summary>
          public static void FillExtraFields(Dictionary<string, object> entry, MethodDefinition m)
          {
              if (entry == null || m == null)
                  return;

              bool hasOptional = false;
              for (int i = 0; i < m.Parameters.Count; i++)
              {
                  if (m.Parameters[i].IsOptional)
                  {
                      hasOptional = true;
                      break;
                  }
              }

              entry["signature"] = FormatSignature(m);
              entry["isPublic"] = m.IsPublic;
              entry["isStatic"] = m.IsStatic;
              entry["isVirtual"] = m.IsVirtual;
              entry["hasOptionalParameters"] = hasOptional;
              entry["isSpecialName"] = m.IsSpecialName;
          }
      }
  }
  ```

- [ ] **步骤 5：改造 `AddMethodResult` 与其唯一调用点**

  把 `McpRimDebug/DebugSession.Eval.cs:355-367` 整体替换为：

  ```csharp
          internal static void AddMethodResult(List<object> results, HashSet<string> seen, string typeFullName, MethodDefinition m)
          {
              if (string.IsNullOrEmpty(typeFullName) || m == null)
                  return;
              // 去重键含完整签名：同名重载必须各占一条，否则「单参重载 vs 带默认参的两参方法」无从分辨。
              string key = CecilMethodSummary.DedupKey(typeFullName, m);
              if (!seen.Add(key))
                  return;
              var entry = new Dictionary<string, object>
              {
                  ["type"] = typeFullName,
                  ["method"] = m.Name,
              };
              // 追加可见性/签名等成员元数据（既有 type/method 保持不变）
              CecilMethodSummary.FillExtraFields(entry, m);
              results.Add(entry);
          }
  ```

  注意可见性从 `static`（private）改为 `internal static`：仅为让 `--selftest` 能直接对真实实现断言，无其他调用方（全仓库仅一处调用，见下一步）。

  把 `McpRimDebug/DebugSession.Eval.cs:507` 的调用点

  ```csharp
                              AddMethodResult(results, seen, fullName, m.Name);
  ```

  改为

  ```csharp
                              AddMethodResult(results, seen, fullName, m);
  ```

- [ ] **步骤 6：同步 `FindMethods` 的 XML 注释**

  把 `McpRimDebug/DebugSession.Eval.cs:287` 这一行

  ```csharp
          /// 在已加载程序集的类型方法上按方法名子串搜索（大小写不敏感），返回 {type, method} 列表。
  ```

  改为

  ```csharp
          /// 在已加载程序集的类型方法上按方法名子串搜索（大小写不敏感），返回 {type, method, signature,
          /// isPublic, isStatic, isVirtual, hasOptionalParameters, isSpecialName} 列表；
          /// 同名重载各占一条（去重键含完整签名），signature 里可选参数带「 = 默认值」。
  ```

  该 XML 注释原有两行（287-288），第二行 `/// 同样限流防卡死。` 保持不动。

- [ ] **步骤 7：改 `find_methods` 的工具描述**

  把 `McpRimDebug/RimDebugTools.cs:87` 这一行

  ```csharp
                  "在游戏程序集的类型方法上按方法名子串搜索（大小写不敏感），返回 {type, method} 列表；同样限流防卡死",
  ```

  改为（沿用同文件 25-29、41-43、79-81 行已有的多行 `+` 拼接风格）

  ```csharp
                  "在游戏程序集的类型方法上按方法名子串搜索（大小写不敏感），返回 {type, method} 列表；同样限流防卡死。"
                  + "【同名重载各占一条，不合并】每条另附成员元数据：signature（C# 风格完整签名，可选参数带「 = 默认值」）、"
                  + "isPublic、isStatic、isVirtual、hasOptionalParameters（是否存在带默认值的参数）、"
                  + "isSpecialName（true 表示属性/运算符访问器，如 get_X / set_X / op_Implicit）。"
                  + "判断成员是否 public、是否 static、是否只读、是不是带默认参的单方法，直接看这些字段，不必再反编译；"
                  + "注意本工具只覆盖方法，属性与字段请用 decompilerserver 的 get_members_of_type(mode:\"signatures\")；"
                + "用 decompilerserver 时有两点必须知道：(1) 每次调用都要显式带 contextAlias:\"vanilla\"，"
                + "它的当前上下文是跨会话共享的可变状态，不带 alias 会把正确的类型名报成 Type '<某段>' could not be found.；"
                + "(2) typeId 的点分全限定名可以照常用（仅同名碰撞时需改传 canonical type memberId，"
                + "即泛型与非泛型共用同一 fullName 的情况，如 Verse.ThingOwner —— 此时工具只返回一个的成员集却报 hasMore:false，"
                + "本例 kind:\"Method\" 返回 27、真值 77）。search_members 与 get_members_of_type 的 totalEstimate 对不上，即为踩到重名的信号。",
  ```

- [ ] **步骤 8：运行自检，确认通过**

  运行：

  ```powershell
  dotnet run --project McpRimDebug -- --selftest
  ```

  预期：退出码 0，末行输出 `selftest 通过: 20 个工具注册齐全，…find_methods 成员元数据 纯逻辑断言通过`，
  且在汇总行之前能看到一行 `[成员元数据自测] find_methods 签名/可见性/static/virtual/默认参/特殊名映射正确，同名重载不再被去重合并`。

  **任务 1 完成标准：这一步绿。**（本仓库非 git 仓库，无 commit 步骤。）

---

### 任务 2：`inspect_type` 的工具描述如实化

**文件：**
- 修改：`MCP/index.js:1957`
- 测试：`MCP/test/*.test.js`（`npm --prefix MCP test`）

- [ ] **步骤 1：改描述**

  把 `MCP/index.js:1957` 这一行

  ```js
      description: '[UE工具][需要进入地图] Inspect a type in UnityExplorer, opens the Inspector panel showing fields, methods, properties of the type. Only works after entering a game map.',
  ```

  替换为下面这**一整行**（该文件的工具描述一律是单行字符串、没有 `+` 拼接先例，保持一致）：

  ```js
      description: '[UE工具][需要进入地图] Inspect a type in UnityExplorer, opens the Inspector panel showing fields, methods, properties of the type. Only works after entering a game map. [LIMIT] This tool returns ONLY {typeName, message} - it does NOT return any member data (fields/methods/properties/visibility) to the caller; the panel it opens is a game-side UI that AI clients cannot read. To get member signatures/visibility/static/readonly use mcp__decompilerserver__get_members_of_type({typeId, mode:"signatures", contextAlias:"vanilla"}) which works offline, or the mono tool find_methods(query) which returns signature/isPublic/isStatic/isVirtual/hasOptionalParameters/isSpecialName but requires attach. [LIMIT-2] Two gotchas: (1) ALWAYS pass contextAlias:"vanilla" on every get_*/search_*/normalize_* call - the server current context is SHARED GLOBAL MUTABLE STATE that another session can move to a different assembly, and without the alias a perfectly valid type name comes back as Type '<some segment>' could not be found.; (2) a DOTTED full name as typeId is FINE (Verse.AI.Pathing with 2 dots resolves normally) - pass a canonical type memberId ONLY when the name is ambiguous, i.e. a generic and a non-generic type share one fullName (e.g. generic Verse.ThingOwner&lt;T&gt; vs non-generic Verse.ThingOwner) where the tool returns only one of them yet still reports hasMore:false (kind:"Method" gives 27 for Verse.ThingOwner, real total 77). Never conclude "this member does not exist" from an ambiguous-name result; if search_members and get_members_of_type disagree on totalEstimate, you hit that ambiguity.',
  ```

  只改描述，**不加输入参数、不改 `inputSchema`、不改转发逻辑**（`MCP/index.js:4421-4430`）—— 游戏侧 `Source/UELoader/UEHttpHandler.cs:120-161` 的返回体一个字都不动。

- [ ] **步骤 2：语法检查**

  运行：

  ```powershell
  node --check MCP/index.js
  ```

  预期：无输出，退出码 0。`MCP/package.json:6` 是 `"type": "module"`，`node --check` 会按 ESM 解析。

- [ ] **步骤 3：跑 MCP 侧测试套件**

  运行：

  ```powershell
  npm --prefix MCP test
  ```

  预期：9 个测试文件全部 `# pass`，退出码 0。其中 `MCP/test/toolchainFixes.test.js:96` 会调用 `toolAvailability('inspect_type')`，只校验可用性不校验描述；`MCP/test/mapMarker.test.js:44` 断言的是另一个工具（`post_map_marker`）的描述 —— 本改动不影响它们。测试导入 `index.js` 前会设 `UESD_MCP_NO_START=1`，不会启动服务器或游戏。

- [ ] **步骤 4：确认描述确实落到了工具表里**

  运行：

  ```powershell
  Select-String -Path MCP/index.js -Pattern '\[LIMIT\] This tool returns ONLY'
  ```

  预期：命中 1 处，行号 1957。

- [ ] **步骤 5：任务收尾**

  任务 2 完成标准：步骤 2 与步骤 3 均绿。它与任务 1 无依赖关系，可单独交付。

---

### 任务 3：文档补齐「成员可见性该去哪问」

**文件：**
- 修改：`README.md:88`、`README.md:226`（各新增一小节，位置见步骤）
- 修改：`McpRimDebug/README.md:147`、`McpRimDebug/README.md:149`
- 修改：`AI-DEPLOYMENT.md`（`:158` 之后新增小节；`:176` 之后表格新增 1 行）

排版按中文规范执行：中英文之间加空格、中文语境用全角标点、代码/参数用半角并加反引号。

- [ ] **步骤 1：`README.md` §3 的 `inspect_type` 表格行**

  把 `README.md:88` 这一行

  ```markdown
  | `inspect_type` | 类型检查（Inspector） |
  ```

  改为

  ```markdown
  | `inspect_type` | 类型检查（Inspector）。**只返回 `{typeName, message}`，不返回任何成员数据** —— 面板开在游戏 UI 上，AI 读不到。查成员可见性见文末「成员可见性该去哪问」 |
  ```

- [ ] **步骤 2：`README.md` §4 的 `find_methods` 表格行**

  把 `README.md:226` 这一行

  ```markdown
  | `find_methods(query, limit?)` | 按子串搜索方法 |
  ```

  改为

  ```markdown
  | `find_methods(query, limit?)` | 按子串搜索方法。**同名重载各占一条、不合并**；每条附带 `signature` / `isPublic` / `isStatic` / `isVirtual` / `hasOptionalParameters` / `isSpecialName`。仅覆盖方法，属性与字段见文末「成员可见性该去哪问」 |
  ```

- [ ] **步骤 3：`README.md` 新增小节**

  在 `README.md:228` 那行（以 `> 动态端口：Unity 调试端口每次运行随机…` 开头的引用块）**之后**、`README.md:230` 的 `## 设置` **之前**插入：

  ```markdown
  #### 成员可见性该去哪问

  写代码时最贵的错误是「以为某成员是 public / 以为某方法是单参重载 / 以为某属性只读」。判定顺序：

  | 场景 | 用什么 | 前置条件 |
  |---|---|---|
  | 已知方法名，查它的签名、可见性、是否带默认参 | `find_methods(query)`，读每条的 `signature` / `isPublic` / `hasOptionalParameters` | 需 `attach` 游戏 |
  | 任意成员（含属性、字段），查可见性 / 是否 static / 有无 setter | decompilerserver 的 `get_members_of_type({typeId, mode:"signatures", contextAlias:"vanilla"})`，返回 `signature` / `accessibility` / `isStatic` / `isVirtual`；属性再加 `kind:"property"`，需要精确签名时用 `get_decompiled_source` 看首行 | 纯离线，游戏不用跑，**写码时首选**（但有两个必须知道的例外，见下方两条 ⚠️） |
  | 想在游戏里看面板 | `inspect_type` | 需进图；只开 UI，不返回数据 |

  🚨 **硬规矩，先看这条：每一次调用 decompilerserver 的 `get_*` / `search_*` / `normalize_*`，都必须显式带 `contextAlias:"vanilla"`。** decompilerserver 的「当前上下文」是**跨会话共享的全局可变状态**，别的并行会话拿同一个服务器去反编译别的程序集，就会把它挪走（本环境实测被挪到 `gab` = `Lib.GAB.dll`，只有 94 个类型）。

  不带 alias 的后果不是「搜不到」，而是**报出一个逐字像「类型不存在」的假阴性**：

  ```
  get_members_of_type({ typeId: "Verse.AI.Pathing" })          # 不带 alias，context 已被别的会话漂走
  → Type 'Verse.AI' could not be found.                          # ← 假阴性，类型其实存在
  get_members_of_type({ typeId: "Verse.AI.Pathing", contextAlias: "vanilla" })
  → totalEstimate: 21                                            # ← 同一调用，带 alias 就正常
  ```

  本环境 `vanilla` / `game` / `default` / `rw16` 四个别名指向同一 MVID `61e4173561894da49d210260257b5097`（`Assembly-CSharp.dll`，16158 类型 / 76770 方法），任选其一即可。**不带 alias 的零命中连「没搜到」都不算，不能当证据。**

  ⚠️ **第二个例外：`typeId` 的点分全限定名本身完全可用，不要一刀切禁用。** 实测 `Verse.AI.Pawn_PathFollower`、`Verse.AI.Pathing`、`Verse.AI.GenPath`、以及三点的 `Verse.AI.Pathing.DisableDirtyingScope` 都能**直接传点分名正常解析**。只有当**该名字下存在多个同名类型**时（泛型 `ThingOwner<T>` 与非泛型 `Verse.ThingOwner` 共用同一个 `fullName`），工具才会解析到其中**一个**、返回**它的**成员集，却照样报 `hasMore:false` —— 等于静默漏掉整块成员。实测：`get_members_of_type({typeId:"Verse.ThingOwner", kind:"Method", contextAlias:"vanilla"})` 返回 27 个方法并报 `hasMore:false, totalEstimate:27`（去掉 `kind` 则是 37），真值是 77（27 泛型 + 42 非泛型 + 8 构造器）。被漏的不只是 public，private / protected 一起没（可见性不是变量，整块 42 个成员根本没被枚举）。

  **同名碰撞时的正确姿势**：`search_types(query="ThingOwner")` 拿 canonical type memberId（形如 `<mvid>:020008EC:T`）→ `get_members_of_type({typeId:"<mvid>:020008EC:T", contextAlias:"vanilla"})`，结果与真值吻合。交叉信号：`search_members` 与 `get_members_of_type` 的 `totalEstimate` 对不上，就说明踩到重名了。要确认单个成员的可见性，用 `get_member_details(memberId)` —— 它按 memberId 定位，不受重名解析影响。

  ⇒ 两条都要守：**不带 alias 的结果整体不采信**；拿到「看着完整却自称 `hasMore:false`」的结果时，也**不要据此下「该成员不存在」的结论**。

  `find_methods` 只覆盖方法：属性在 IL 层面是 `get_X` / `set_X` 这类 `isSpecialName` 的方法，能看出来，但它不告诉你 `X` 这个属性本身的存在与类型 —— 那要问 decompilerserver。

  ```

- [ ] **步骤 4：`McpRimDebug/README.md` 的 `find_methods` 行与返回结构说明**

  把 `McpRimDebug/README.md:147` 这一行

  ```markdown
  | `find_methods(query, limit?)` | 按子串搜方法 |
  ```

  改为

  ```markdown
  | `find_methods(query, limit?)` | 按子串搜方法。**同名重载各占一条**（去重键含完整签名）；每条附带 `signature`（C# 风格完整签名，可选参数带 ` = 默认值`）、`isPublic`、`isStatic`、`isVirtual`、`hasOptionalParameters`、`isSpecialName`。属性与字段不在本工具覆盖范围，离线查用 decompilerserver 的 `get_members_of_type({mode:"signatures", contextAlias:"vanilla"})`（⚠️ `contextAlias` 每次必带；`typeId` 的点分全限定名可照常用，仅同名碰撞时需改传 canonical type memberId，见下方说明） |
  ```

  把 `McpRimDebug/README.md:149` 这一行

  ```markdown
  统一返回结构：`{ ok, message?, data? }`。
  ```

  改为

  ```markdown
  统一返回结构：`{ ok, message?, data? }`。`find_methods` 的 `data` 为 `{ query, limit, count, truncated, methods }`，`methods` 每项为 `{ type, method, signature, isPublic, isStatic, isVirtual, hasOptionalParameters, isSpecialName }` —— 字段只增不换。
  ```

  再在 `McpRimDebug/README.md:149`（即上面改完的那一行）**之后**新起一段：

  ```markdown
  ⚠️ 上表提到的 decompilerserver `get_members_of_type`，**每次调用都必须显式带 `contextAlias:"vanilla"`**（`vanilla` / `game` / `default` / `rw16` 指向同一个 `Assembly-CSharp.dll`）—— current context 是**跨会话共享的可变状态**，被别的会话挪走后，一个**完全正确**的类型名会报成 `Type '<某段>' could not be found.`，与「类型不存在」逐字同形；**不带 alias 的结果不能当证据**。其 `typeId` 的点分全限定名**可以照常用**（实测 `Verse.AI.Pathing` 两个点正常）；只在**同名碰撞**时（泛型与非泛型共用同一 `fullName`，如 `Verse.ThingOwner`）才需要改传 canonical type memberId —— 此时工具只返回一个的成员集却仍报 `hasMore:false`。这两处都是外部服务的已知缺陷，本仓库无法修。`find_methods` 不受影响 —— 它按 `TypeDefinition` 遍历，泛型与非泛型天然不同名，且截断时诚实置 `truncated=true`。
  ```

- [ ] **步骤 5：`AI-DEPLOYMENT.md` 新增小节**

  在 `AI-DEPLOYMENT.md:158`（`7. 重启游戏，即进调试模式；从 Player.log 或 \`ports.json.unityDebugPort\` 得到 Unity 调试端口。`）**之后**、`AI-DEPLOYMENT.md:160` 的 `---` **之前**插入：

  ```markdown
  #### 写码前查成员可见性（不必等游戏跑起来）

  需要确认「某成员是否 public / 某方法是单参重载还是带默认参 / 某属性有没有 setter」时：

  - **首选 decompilerserver（纯离线，游戏不用跑）**：`get_members_of_type({ typeId: "<mvid>:02000xxx:T", mode: "signatures", contextAlias: "vanilla" })` 返回 `signature` / `accessibility` / `isStatic` / `isVirtual`；属性加 `kind: "property"`；要精确签名（含 ` = null` 这类默认值写法）再用 `get_decompiled_source` 看首行。⚠️ `contextAlias: "vanilla"` **每次都必须带**；`typeId` 的点分全限定名则**可以照常用**，见下。
  - **已在调试会话里**：`find_methods(query)` 的每条结果自带 `signature` / `isPublic` / `isStatic` / `isVirtual` / `hasOptionalParameters` / `isSpecialName`，且同名重载不再被合并。
  - **不要用 `inspect_type`**：它只在游戏 UI 上开面板，返回体只有 `{typeName, message}`，拿不到任何成员信息。

  这一节与上面的强制门禁无关：不装 §7 的调试模式改造也能用 decompilerserver 那条路。

  🚨 **先看这条：每一次调 decompilerserver 都必须显式带 `contextAlias: "vanilla"`。** 它的「当前上下文」是**跨会话共享的全局可变状态**，别的并行会话拿同一个服务器去反编译别的程序集，就会把它挪走（本环境实测被挪到 `gab` = `Lib.GAB.dll`，只有 94 个类型）。不带 alias 时，一个**完全正确**的 `typeId` 会报成 `Type '<某段>' could not be found.` —— 实测 `get_members_of_type({typeId:"Verse.AI.Pathing"})` 不带 alias 就是这条报错，加上 `contextAlias:"vanilla"` 后同一调用正常返回 21 个成员。本环境 `vanilla` / `game` / `default` / `rw16` 指向同一个 `Assembly-CSharp.dll`，任选其一即可。**不带 alias 的零命中不能当证据。**

  ⚠️ **第二个例外：`typeId` 的点分全限定名本身没问题，不要一刀切禁用。** 实测 `Verse.AI.Pawn_PathFollower`、`Verse.AI.Pathing`、`Verse.AI.GenPath`、三点的 `Verse.AI.Pathing.DisableDirtyingScope` 都能直接传点分名正常解析。只有**同名碰撞**时（泛型 `ThingOwner<T>` 与非泛型 `Verse.ThingOwner` 共用同一个 `fullName`）才需要改传 canonical type memberId —— 此时工具只返回其中一个的成员集却照样报 `hasMore:false`。实测 `get_members_of_type({typeId:"Verse.ThingOwner", kind:"Method", contextAlias:"vanilla"})` = 27 个方法、`totalEstimate:27`（去掉 `kind` 则是 37），真值 77。被漏的不只是 public，private / protected 一起没。

  **同名碰撞时的正确姿势**：`search_types` 拿 canonical type memberId 再传进去。交叉信号：`search_members` 与 `get_members_of_type` 的 `totalEstimate` 对不上即踩到重名。确认单个成员可见性用 `get_member_details(memberId)`，它按 memberId 定位、不受重名解析影响。

  ⇒ **不要**依据 `get_members_of_type` 的结果直接下「该成员不存在」的结论；也**不要**看到 `Type '<某段>' could not be found.` 就先去怀疑自己的类型名写错 —— 先加 `contextAlias` 重试一遍。两处缺陷都在外部 `decompilerserver` 服务侧，本项目无法修，只能这样绕。

  ```

- [ ] **步骤 6：`AI-DEPLOYMENT.md` 排障表新增一行**

  在 `AI-DEPLOYMENT.md:176`（`| McpRimDebug 不生效 | Unity 调试模式未真正开启 | 核对 §6（尤其 Unity 版本严格匹配） |`）之后插入：

  ```markdown
  | 写码时不确定某成员 public / 是否带默认参 | 靠猜或撞编译错，代价是错误结论写进代码 | 先查（见本节末「写码前查成员可见性」），别猜 |
  ```

- [ ] **步骤 7：验证文档改动全部落位**

  运行：

  ```powershell
  Select-String -Path README.md -Pattern '成员可见性该去哪问'
  Select-String -Path McpRimDebug/README.md -Pattern 'hasOptionalParameters'
  Select-String -Path AI-DEPLOYMENT.md -Pattern '写码前查成员可见性'
  Select-String -Path README.md,McpRimDebug/README.md,AI-DEPLOYMENT.md -Pattern '不返回任何成员数据|只开 UI，不返回数据|返回体只有 \{typeName, message\}'
  Select-String -Path README.md,McpRimDebug/README.md,AI-DEPLOYMENT.md,McpRimDebug/RimDebugTools.cs,MCP/index.js -Pattern 'canonical type memberId'
  Select-String -Path README.md,McpRimDebug/README.md,AI-DEPLOYMENT.md,McpRimDebug/RimDebugTools.cs,MCP/index.js -Pattern 'contextAlias'
  ```

  预期：第 1 条在 `README.md` 命中 3 处（`inspect_type` 表格行、`find_methods` 表格行、新增小节标题）；第 2 条在 `McpRimDebug/README.md` 命中 2 处（`find_methods` 行、返回结构段）；第 3 条在 `AI-DEPLOYMENT.md` 命中 2 处（新增小节标题、排障表行）；第 4 条在 `README.md` 命中 2 处（`inspect_type` 行的「不返回任何成员数据」+ 新增表的「只开 UI，不返回数据」）、在 `AI-DEPLOYMENT.md` 命中 1 处（「返回体只有 {typeName, message}」），`McpRimDebug/README.md` 不命中属正常 —— 那份文档只更新 `find_methods` 行、不复述 `inspect_type` 的限制。

  第 5 条校验 `[29]` 的同名碰撞例外措辞：**5 个文件必须各有 ≥1 处命中**（`README.md` 新增 ⚠️ 段、`McpRimDebug/README.md` 新增 ⚠️ 段、`AI-DEPLOYMENT.md` 首条 ⚠️、`McpRimDebug/RimDebugTools.cs` 的 `find_methods` 描述、`MCP/index.js` 的 `inspect_type` 描述）。任一文件 0 命中即表示该处指路没加例外限定，需要补齐后再收工。

第 6 条校验 `[37]` 的 `contextAlias` 硬规矩：同样是**这 5 个文件各 ≥1 处命中**。⚠️ 这一条比第 5 条更要紧 —— 不带 `contextAlias` 会把一个**完全正确**的类型名报成 `Type '<某段>' could not be found.`，读者照着「写码时首选」去做就会直接撞上，且报错本身看不出任何异常。若某文件 0 命中，说明该处把 decompilerserver 当权威却没写 alias，必须补齐再收工。

- [ ] **步骤 8：任务收尾**

  任务 3 完成标准：步骤 7 全部命中。纯文档，无需构建。

---

## 4. 全部验证命令速查

```powershell
# 任务 1（C#）
dotnet run --project McpRimDebug -- --selftest

# 任务 2（Node）
node --check MCP/index.js
npm --prefix MCP test

# 任务 3（文档）
Select-String -Path README.md,McpRimDebug/README.md,AI-DEPLOYMENT.md -Pattern '成员可见性|hasOptionalParameters'
```

三条命令都不需要游戏在跑，也都不会改变游戏状态。

## 5. 计划自检

**规格覆盖度**：吐槽板 `[10]` 的诉求是「`inspect_type` 之类的工具若能直接给出成员可见性 / 是否 static / 是否只读」。逐条对照：

| 诉求 | 覆盖任务 | 覆盖方式 |
|---|---|---|
| 成员可见性 | 任务 1 | `find_methods` 每条新增 `isPublic` |
| 是否 static | 任务 1 | 新增 `isStatic` |
| 是否只读 | 任务 1 + 任务 3 | 任务 1 新增 `isSpecialName`（认出 `set_X` / `get_X`）；属性级别的只读判定由任务 3 指向的 decompilerserver `kind:"property"` 覆盖 |
| 「只有单参重载 vs 带默认参」误判 | 任务 1 | `signature` 带 ` = 默认值` + `hasOptionalParameters` + 去重键含签名（§0.5 第 1 条修正） |
| 「有 public setter vs 不可写」误判 | 任务 3 | 文档写明离线该问 decompilerserver 的哪两个调用 |
| 「不知道正确答案在哪」 | 任务 2 + 任务 3 | `inspect_type` 描述指路 + 三份文档的查证顺序表 |

**占位符扫描**：全文无「待定」「TODO」「后续实现」「补充细节」「添加适当的错误处理」「类似任务 N」；每个代码步骤都带可直接粘贴的完整代码块；无引用未定义类型 —— `CecilMethodSummary` 在任务 1 步骤 4 定义、步骤 1 使用（先红后绿），`NewSelfTestModule` / `RefType` / `Flag` / `RunCecilMethodSummarySelfTest` 四个名字在步骤 1 的代码块里一次定义齐备。

**类型一致性**：`AddMethodResult` 的签名在步骤 1（测试调用）与步骤 5（实现）两处都是 `(List<object>, HashSet<string>, string, MethodDefinition)`，一致；`CecilMethodSummary` 的三个公开成员 `FormatSignature` / `DedupKey` / `FillExtraFields` 在定义处与两处调用处拼写一致；`FillExtraFields` 的返回类型是 `void`，两处调用都未接收返回值，一致。

**已知的、刻意留下的行为变化**：去重键含签名后 `find_methods` 的 `count` 可能变大（同名重载不再被合并）。这是修复本身，已在 §0.5 与任务 1 步骤 5 的注释里写明。

**外部依赖的已知限制（不在本计划修复范围）**：任务 1/2/3 的文案都把 `decompilerserver` 的 `get_members_of_type` 当作离线成员查询入口，但它有两处外部缺陷，已在所有指路文案里统一加了例外限定与绕过姿势：

| 缺陷 | 现象 | 文案里的绕过 |
|---|---|---|
| `[37]` current context 是**跨会话共享的可变状态** | 不带 `contextAlias` 时，一个**完全正确**的 `typeId` 报成 `Type '<某段>' could not be found.` | 每次调用显式带 `contextAlias:"vanilla"`；「不带 alias 的零命中不能当证据」 |
| `[29]` **同名碰撞**静默漏项 | `Verse.ThingOwner`（泛型 + 非泛型共用 `fullName`）只返回一个的成员集，却报 `hasMore:false`（`kind:"Method"` = 27，真值 77） | 仅同名碰撞时改传 canonical type memberId；`search_members` vs `get_members_of_type` 的 `totalEstimate` 对不上即告警 |

⚠️ **`typeId` 的点分全限定名本身是可用的**（`[38]` 实测 `Verse.AI.Pathing`、`Verse.AI.GenPath`、三点的 `Verse.AI.Pathing.DisableDirtyingScope` 均可直接解析），文案**不得**写成「不要传点分名」。两处缺陷都归属外部服务，详见 `triage/29-…md` / `triage/37-…md` / `triage/38-…md`，**本计划只补措辞、不加任务**（理由与不可归并的对照表见 §0.4）。

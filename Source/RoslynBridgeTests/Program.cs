using System;
using System.Linq;
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

        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--success-path")
                return RunSuccessPathInChild();
            TestLoadOrder();
            TestLoadFailureDoesNotThrow();
            TestEnsureLoadedPathKeyedLatch();
            TestSuccessPathViaChildProcess();
            TestReferenceSet();
            TestDefaultUsingsParsing();
            TestPolyfills();
            TestCompiler();
            TestDiagnosticFormat();
            TestReplSession();
            TestRoslynEvaluatorFacade();
            TestC5Guard();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }

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
            // 修复轮 1 #2：单参 GetField 不遍历基类 static 字段 → 早期变量曾恒显示 null；
            // 多次提交后 GetVars 必须沿继承链取到值（x 此刻 = 5）。
            Check("GetVars 显示早期变量的值（基类链）", s.GetVars().Contains("x = 5"), s.GetVars());

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

            // 修复轮 1 #4 + #1：运行期异常计入 ErrorCount、诊断带消息（覆盖消息优先级 bug）、
            // 且之后会话仍可用（运行期失败=推进计数按失败记账，程序集已加载不可干净回滚）。
            var boom = s.Submit("throw new System.Exception(\"boom\");");
            Check("运行期异常计入 ErrorCount", boom.ErrorCount > 0, "err=" + boom.ErrorCount);
            Check("运行期异常诊断含消息", boom.Diagnostics.Contains("boom"), boom.Diagnostics);
            var afterBoom = s.Submit("y");
            Check("运行期异常后会话仍可用", afterBoom.ErrorCount == 0 && s.LastValue?.ToString() == "101",
                "value=" + s.LastValue + " diag=" + afterBoom.Diagnostics);

            // 修复轮 1 #3：会话唯一程序集前缀 → 多会话并存互不串值，Dispose 幂等摘钩。
            var s2 = new ReplSession(refs);
            var q1 = s2.Submit("var a = 1;");
            var q2 = s2.Submit("a + 41");
            Check("多会话：第二会话独立求值", q1.ErrorCount == 0 && q2.ErrorCount == 0
                && s2.LastValue?.ToString() == "42", "value=" + s2.LastValue + " diag=" + q2.Diagnostics);
            var q3 = s.Submit("y");
            Check("多会话：第一会话不受影响", q3.ErrorCount == 0 && s.LastValue?.ToString() == "101",
                "value=" + s.LastValue);
            // 判别唯一前缀机制：程序集名必须是 Submission_<8位会话标签>_<n>_asm（4 段下划线），
            // 旧命名 Submission_<n>_asm 只有 3 段 —— 结构断言直接证伪同名回退。
            string asmName = System.Reflection.Assembly.Load(q2.PeBytes).GetName().Name;
            Check("提交程序集名含会话唯一标签", asmName.Split('_').Length == 4, asmName);
            bool disposed = true;
            try { s2.Dispose(); s2.Dispose(); } catch { disposed = false; }
            Check("会话 Dispose 摘钩幂等不抛", disposed);

            // 修复轮 2 Important：Dispose 后 Submit 必须清晰拒绝（失败结果 + "会话已释放" 诊断），
            // 不得静默成功、也不得运行期 NRE。判别推演（复审 2 勘误）：旧码无防御——s2 此刻
            // s_submission=2，n=3 需解析基类而 AssemblyResolve 已摘钩 → 运行期 NRE →
            // F4 记账 Success=false/ErrorCount=1、诊断"运行时异常:…"（前两条件为真）——
            // 仅第三条件（诊断含"已释放"）不成立 → 本断言必 FAIL。
            var post = s2.Submit("1 + 1");
            Check("Dispose 后 Submit 被拒绝（失败+已释放诊断）", !post.Success && post.ErrorCount > 0
                && post.Diagnostics.Contains("已释放"), post.Diagnostics);
        }

        static void TestRoslynEvaluatorFacade()
        {
            var refs = ReferenceSetForThisProcess();
            using (var ev = new RoslynScriptEvaluator(refs))
            {
                Check("门面可用", ev.IsAvailable);

                string outp;
                Check("执行 x=1", ev.Evaluate("var x = 1;", out outp), outp);
                Check("执行 x+41 得 42", ev.Evaluate("x + 41", out outp) && ev.LastValueText == "42", "value=" + ev.LastValueText);
                // A2（合并修复轮）：REPL 返回值经 output 带回（对齐 mcs 语义，HELP_REPL 承诺兑现）。
                // 判别力：旧码成功路径 output=r.Diagnostics（空串）→ outp=="42" 必 FAIL。
                Check("REPL 表达式成功后 output 带回结果值", ev.Evaluate("x + 41", out outp) && outp == "42", "out=" + outp);
                Check("GetVars 含 x", ev.GetVars().Contains("x"));
                Check("AddUsing 后 using 列表更新", AddUsingAndCheck(ev));

                Check("语法错误返回 false", !ev.Evaluate("var = ;", out outp));
                Check("错误后仍可用", ev.Evaluate("x", out outp) && ev.LastValueText == "1");
            }

            // A1（合并修复轮）：Allow_Mod_Type_Resolution 开关落位——构造往返 + 引用集不被放大 + 求值正常。
            // 判别力：旧码构造器无第三参 → 本段编译期 RED（CS1729/CS1501）；引用计数断言证伪
            // 旧三参设计"把 ModAssemblyPaths 预加进引用集"行为的复活。
            // ⚠️ 登记语句：任务 13 自愈器落地前该开关不改变引用集行为（转对端会话登记为待验收项）。
            using (var evT = new RoslynScriptEvaluator(refs, "", true))
            using (var evF = new RoslynScriptEvaluator(refs, "", false))
            {
                Check("A1 开关true 构造往返", evT.AllowModTypeResolution);
                Check("A1 开关false（默认）构造往返", !evF.AllowModTypeResolution);
                Check("A1 引用集不被开关放大", evT.ReferenceCount == refs.Count && evF.ReferenceCount == refs.Count,
                    "true=" + evT.ReferenceCount + " false=" + evF.ReferenceCount + " input=" + refs.Count);
                string outA1;
                Check("A1 开关true 求值仍正常", evT.Evaluate("1 + 1", out outA1) && evT.LastValueText == "2", "out=" + outA1);
            }

            // 坑 1（任务 6 生命周期契约）：求值器 Dispose 必须转发 s_session.Dispose（摘 AssemblyResolve 钩）。
            // 判别推演：若 Dispose 为空转发（简报原文），此处 Evaluate("1 + 1") 会静默成功 → 断言 FAIL。
            string outp2;
            var ev2 = new RoslynScriptEvaluator(refs);
            ev2.Dispose();
            Check("Dispose 后 Evaluate 返回 false", !ev2.Evaluate("1 + 1", out outp2), outp2);
        }

        static bool AddUsingAndCheck(RoslynScriptEvaluator ev)
        {
            ev.AddUsing("System.Text");
            return ev.GetUsing().Contains("System.Text");
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

        static void TestDefaultUsingsParsing()
        {
            var list = DefaultUsings.Parse("Verse;RimWorld;; Verse.AI ;");
            Check("分号切分", list.Count == 3, "got " + list.Count);
            Check("去空项", !list.Contains(""));
            Check("含 Verse", list.Contains("Verse"));
            Check("含 RimWorld", list.Contains("RimWorld"));
            Check("含 Verse.AI", list.Contains("Verse.AI"));

            var defaults = DefaultUsings.BuiltIn;
            Check("内置含 Verse", defaults.Contains("Verse"));
            Check("内置含 RimWorld", defaults.Contains("RimWorld"));
            Check("内置含 UnityEngine", defaults.Contains("UnityEngine"));
            Check("内置含 System.Linq", defaults.Contains("System.Linq"));

            // 修复轮 1：原"去重"断言与"分号切分"同值空转（输入无真重复，Contains 分支从未判 false），
            // 改为真重复输入 + 早退边界覆盖。
            var dup = DefaultUsings.Parse("Verse; Verse;RimWorld");
            Check("去重：真重复+空格差异视为同一个", dup.Count == 2, "got " + dup.Count);
            Check("真重复 → 计数 1", DefaultUsings.Parse("Verse;Verse").Count == 1);
            Check("Parse(null) → 空表", DefaultUsings.Parse(null).Count == 0);
            Check("Parse(\"\") → 空表", DefaultUsings.Parse("").Count == 0);
            Check("纯分号空白串 → 空表", DefaultUsings.Parse(" ;;; ").Count == 0);
        }

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

        /// <summary>
        /// 成功路径（s_available=true 分支）在独立进程里断言：静态一次性锁与失败用例同进程互斥，
        /// 谁先跑都会污染对方，故父进程自举本 exe 带 --success-path 参数执行。
        /// </summary>
        static int RunSuccessPathInChild()
        {
            string real = FindRealPayloadDir();
            bool ok = false;
            string detail = null;
            if (real == null)
                detail = "找不到真实载荷目录";
            else
            {
                // ── 修订 1：门闩按路径键控的语义断言（同一进程内可完整观察，故放在子进程）──
                // ① 不存在的路径：不消耗尝试、不加载（只留首个诊断）
                Check("子进程：不存在的路径 → false",
                    RoslynAssemblyLoader.EnsureLoaded(@"Z:\c5\missing-a") == false);
                Check("子进程：不存在的路径不登记尝试（失败原因只记首个）",
                    RoslynAssemblyLoader.FailureReason != null
                    && RoslynAssemblyLoader.EnsureLoaded(@"Z:\c5\missing-b") == false
                    && RoslynAssemblyLoader.FailureReason.Contains("missing-a"),
                    RoslynAssemblyLoader.FailureReason);
                // ② 存在但缺载荷的目录（载荷目录的父目录 lib/）：真尝试并失败 → 登记该路径
                string incomplete = System.IO.Path.GetDirectoryName(real);
                Check("子进程：存在但缺载荷的目录 → false（真尝试并登记）",
                    RoslynAssemblyLoader.EnsureLoaded(incomplete) == false);
                string failAfterIncomplete = RoslynAssemblyLoader.FailureReason;
                Check("子进程：同路径重复调用不重试（失败原因逐字不变）",
                    RoslynAssemblyLoader.EnsureLoaded(incomplete) == false
                    && RoslynAssemblyLoader.FailureReason == failAfterIncomplete,
                    RoslynAssemblyLoader.FailureReason);
                // ③ **不同且存在**的路径（真实载荷目录）→ 允许新尝试并成功：
                //    旧「一次性全局锁」在此恒返回 false（壳类推导错一次即永久锁死 mcs）→ 本断言即判别力。
                try { ok = RoslynAssemblyLoader.EnsureLoaded(real); }
                catch (Exception e) { detail = e.GetType().Name + ": " + e.Message; }
            }
            Check("子进程：换不同且存在的路径 → 允许重试并成功（修订 1 判别力）", ok, detail);
            Check("子进程：IsAvailable==true", RoslynAssemblyLoader.IsAvailable);
            Check("子进程：解析器已挂载（ResolverInstalled）", RoslynAssemblyLoader.ResolverInstalled);

            // ── 追加 8 行为断言（只能在本子进程做：父进程的载荷目录尝试必失败，映射必空、处理器必未挂）──
            // ① 映射在挂载前已填满（先填后挂，扫描期工作线程只读）；
            // ② 处理器应答载荷简单名：按名返回**已载实例**（版本号由 AssemblyName 解析，与 identity 无关）；
            // ③ 非载荷名一律 null → 对第三方零干扰（处理器不得吞别家请求，也不得抛异常）；
            // ④ 修订 2（应答面收窄）：载荷名 + 外来请求方 → null；载荷名 + 请求方不可判定(null) → 应答。
            int mapped = PayloadMapCount();
            Check("子进程：载荷映射已填充 8 条（先填后挂）", mapped == 8, "count=" + mapped);
            Check("子进程：LoadedPayloadCount==8（对外只读口径）",
                RoslynAssemblyLoader.LoadedPayloadCount == 8, "count=" + RoslynAssemblyLoader.LoadedPayloadCount);

            object answered = InvokePayloadResolver(
                "Microsoft.CodeAnalysis, Version=4.14.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
            Check("子进程：应答器按简单名应答载荷（版本无关）",
                answered is System.Reflection.Assembly
                && ((System.Reflection.Assembly)answered).GetName().Name == "Microsoft.CodeAnalysis",
                answered == null ? "返回 null" : answered.ToString());

            object foreign = InvokePayloadResolver(
                "Newtonsoft.Json, Version=13.0.3.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed");
            Check("子进程：应答器对非载荷名返回 null（第三方零干扰）", foreign == null,
                foreign == null ? null : foreign.ToString());

            // ④ 收窄：System.Memory 是通用名——请求方是别人（这里用自己的测试程序集当"外来请求方"
            //    的反例不成立，故显式用 mscorlib 当外来方）时必须 null；请求方不可判定时必须应答。
            var foreignRequester = typeof(string).Assembly;   // mscorlib：与载荷无关的第三方
            object narrowed = InvokePayloadResolverWithRequester(
                "System.Memory, Version=4.0.1.2, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51",
                foreignRequester);
            Check("子进程：通用名载荷 + 外来请求方 → null（修订 2 收窄）", narrowed == null,
                narrowed == null ? null : narrowed.ToString());

            object noRequester = InvokePayloadResolverWithRequester(
                "System.Memory, Version=4.0.1.2, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51", null);
            Check("子进程：载荷名 + 请求方不可判定(null) → 应答（主路径保底）",
                noRequester is System.Reflection.Assembly);

            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>反射读 RoslynAssemblyLoader 的载荷映射条数（internal 静态字段，测试程序集无 InternalsVisibleTo）。</summary>
        static int PayloadMapCount()
        {
            var f = typeof(RoslynAssemblyLoader).GetField("PayloadAssembliesByName",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (f == null) return -1;
            var d = f.GetValue(null) as System.Collections.IDictionary;
            return d == null ? -1 : d.Count;
        }

        /// <summary>反射调私有的 AppDomain 级应答器（模拟 Mono 的绑定回调）；方法缺失即抛（测试要响）。</summary>
        static object InvokePayloadResolver(string assemblyFullName)
        {
            return InvokePayloadResolverWithRequester(assemblyFullName, null);
        }

        /// <summary>
        /// 同上，但带 RequestingAssembly（修订 2 收窄需要）。`ResolveEventArgs(name, requester)` 是
        /// internal 构造，故先反射构造器、失败再反射设私有字段；两条途径都不可用即 FAIL（不得静默放过）。
        /// </summary>
        static object InvokePayloadResolverWithRequester(string assemblyFullName,
            System.Reflection.Assembly requester)
        {
            var m = typeof(RoslynAssemblyLoader).GetMethod("PayloadAssemblyResolver",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (m == null) throw new InvalidOperationException("PayloadAssemblyResolver 方法不存在");

            var args = BuildResolveArgs(assemblyFullName, requester);
            return m.Invoke(null, new object[] { null, args });
        }

        static ResolveEventArgs BuildResolveArgs(string assemblyFullName,
            System.Reflection.Assembly requester)
        {
            if (requester == null) return new ResolveEventArgs(assemblyFullName);

            var ctor = typeof(ResolveEventArgs).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string), typeof(System.Reflection.Assembly) }, null);
            if (ctor != null)
                return (ResolveEventArgs)ctor.Invoke(new object[] { assemblyFullName, requester });

            var args = new ResolveEventArgs(assemblyFullName);
            var field = typeof(ResolveEventArgs).GetField("_requestingAssembly",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(args, requester);
                return args;
            }
            Check("可构造带 RequestingAssembly 的 ResolveEventArgs（修订 2 断言前置）", false,
                "既无 internal 构造器也无 _requestingAssembly 字段");
            return args;
        }

        static void TestSuccessPathViaChildProcess()
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, "--success-path")
                {
                    UseShellExecute = false,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit();
                    Check("成功路径（子进程）退出码=0", p.ExitCode == 0, "exit=" + p.ExitCode);
                }
            }
            catch (Exception e)
            {
                Check("成功路径（子进程）能启动", false, e.GetType().Name + ": " + e.Message);
            }
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
            // 完整顺序锁死（可行性报告 §4.2：顺序错会导致 Mono 缓存 typeref 解析失败，进程内不可恢复）
            Check("完整加载顺序固定",
                string.Join(",", o) ==
                "System.Numerics.Vectors.dll,System.Runtime.CompilerServices.Unsafe.dll,"
                + "System.Threading.Tasks.Extensions.dll,System.Memory.dll,System.Collections.Immutable.dll,"
                + "System.Reflection.Metadata.dll,Microsoft.CodeAnalysis.dll,Microsoft.CodeAnalysis.CSharp.dll",
                string.Join(",", o));
        }

        static void TestLoadFailureDoesNotThrow()
        {
            bool ok = false;
            try { ok = RoslynAssemblyLoader.EnsureLoaded(@"Z:\definitely\missing"); }
            catch (Exception e) { Check("缺目录不抛异常", false, e.GetType().Name); return; }
            Check("缺目录返回 false", !ok);
            Check("缺目录后 IsAvailable=false", !RoslynAssemblyLoader.IsAvailable);
        }

        // 修订 1：失败门闩按**路径键控**（取代原一次性全局锁）——本进程只观察"失败侧"语义：
        // 同路径不重试（幂等）、不存在的路径不登记尝试；"不同且存在的路径可重试并成功"在子进程断言
        //（保持既有纪律：静态一次性状态的成功/失败两侧分进程，互不污染）。
        static void TestEnsureLoadedPathKeyedLatch()
        {
            // 上一条用例（TestLoadFailureDoesNotThrow）已用 Z:\definitely\missing 触发首个诊断
            string first = RoslynAssemblyLoader.FailureReason;
            Check("失败门闩：不存在路径的失败已留诊断", !string.IsNullOrEmpty(first), "reason=" + first);

            Check("失败门闩：不存在的路径重复调用仍 false",
                RoslynAssemblyLoader.EnsureLoaded(@"Z:\definitely\missing") == false);
            Check("失败门闩：不存在路径不改写首个诊断（不消耗尝试）",
                RoslynAssemblyLoader.FailureReason == first, RoslynAssemblyLoader.FailureReason);

            // 存在但缺载荷的目录（= 载荷目录的父目录 lib/）：真尝试 → 失败 → 登记该路径
            string real = FindRealPayloadDir();
            string incomplete = real == null ? null : System.IO.Path.GetDirectoryName(real);
            bool usable = incomplete != null && System.IO.Directory.Exists(incomplete)
                && !System.IO.File.Exists(System.IO.Path.Combine(incomplete, "System.Memory.dll"));
            Check("失败门闩：前置（存在但缺载荷的目录可用）", usable, incomplete);

            Check("失败门闩：存在但缺载荷的目录 → false（真尝试并登记）",
                usable && RoslynAssemblyLoader.EnsureLoaded(incomplete) == false,
                RoslynAssemblyLoader.FailureReason);
            string afterIncomplete = RoslynAssemblyLoader.FailureReason;
            Check("失败门闩：同路径重复调用不重试（幂等；失败原因逐字不变）",
                usable && RoslynAssemblyLoader.EnsureLoaded(incomplete) == false
                && RoslynAssemblyLoader.FailureReason == afterIncomplete,
                RoslynAssemblyLoader.FailureReason);
            Check("失败门闩：IsAvailable 仍为 false", !RoslynAssemblyLoader.IsAvailable);
            Check("失败门闩：解析器未挂载（未成功装载 ⇒ 未挂）",
                !RoslynAssemblyLoader.ResolverInstalled);
            Check("失败门闩：LoadedPayloadCount==0（无部分装载态）",
                RoslynAssemblyLoader.LoadedPayloadCount == 0,
                "count=" + RoslynAssemblyLoader.LoadedPayloadCount);
        }

        // ── C.5-lite 护栏（任务 8）：防复发断言 ──
        // UnityExplorer.RoslynBridge 全类型（含编译器生成嵌套 <>c*/<>c__DisplayClass*）的全部字段
        // （Static|Instance|Public|NonPublic），字段类型不得指向 8 件载荷程序集。
        // 判别力：修复前旧码 ≥6 处命中（RoslynCompiler.ParseOptions/CompileOptions 静态字段、
        // ReplSession.s_imageRefs 实例字段、<>c__DisplayClass23_0.v 闭包字段、<>c 上
        // Func<Diagnostic,bool>/Func<string,MetadataReference> 委托缓存字段）→ 必 FAIL。
        // 机制更正（Player.log 实证）：Mono GetFields 元数据遍历不按 BindingFlags 过滤即解析字段
        // 签名——实例/闭包/委托缓存字段一样炸；泛型字段签名解析连带解析**泛型实参** typeref
        // （List<MetadataReference> 炸在 MetadataReference 而非 List）→ 护栏必须递归泛型实参。
        // 边界：护栏只断**字段类型**；方法体/局部变量/参数使用载荷类型合法（那是 Roslyn 功能本身）。
        static void TestC5Guard()
        {
            string[] payloadAsms = new[]
            {
                "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
                "System.Collections.Immutable", "System.Reflection.Metadata",
                "System.Memory", "System.Threading.Tasks.Extensions",
                "System.Runtime.CompilerServices.Unsafe", "System.Numerics.Vectors",
            };

            // 修订 4（防"静默跳过=假绿"）：RTLE 兜底会丢型、GetFields 失败会丢型，两者都必须计数并断言为 0，
            // 否则护栏可能"扫了个空集"还报 PASS。
            Type[] types;
            int nullTypes = 0, loaderExceptions = 0;
            try { types = typeof(ReplSession).Assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException rtle)
            {
                types = rtle.Types.Where(t => t != null).ToArray();
                nullTypes = rtle.Types.Length - types.Length;
                loaderExceptions = rtle.LoaderExceptions == null ? 0 : rtle.LoaderExceptions.Length;
            }
            catch (Exception e) { Check("C.5 护栏：类型枚举可执行", false, e.GetType().Name); return; }

            var bad = new System.Text.StringBuilder();
            const System.Reflection.BindingFlags AllF = System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic;
            int scanned = 0, fieldEnumFailures = 0;
            foreach (Type t in types)
            {
                if (t == null || t.Namespace != "UnityExplorer.RoslynBridge") continue;
                System.Reflection.FieldInfo[] fs;
                try { fs = t.GetFields(AllF); } catch { fieldEnumFailures++; continue; }
                scanned++;
                foreach (System.Reflection.FieldInfo f in fs)
                    if (FieldTouchesPayload(f.FieldType, payloadAsms))
                        bad.AppendLine(t.Name + " . " + f.Name + " -> " + f.FieldType);
            }
            // 跳过计数断言（实测值写在用例名里）：空型 0 / LoaderException 0 / 字段枚举失败 0。任一非 0 即假绿风险。
            Check("C.5 护栏：零跳过（空型 " + nullTypes + " / LoaderException " + loaderExceptions
                + " / 字段枚举失败 " + fieldEnumFailures + "）",
                nullTypes == 0 && loaderExceptions == 0 && fieldEnumFailures == 0,
                "空型=" + nullTypes + " loaderExc=" + loaderExceptions + " fieldFail=" + fieldEnumFailures);
            Check("C.5 护栏：RoslynBridge 全类型零载荷类型字段（扫描 " + scanned + " 型）",
                bad.Length == 0, bad.ToString().TrimEnd());
        }

        // 泛型实参递归：FieldType.Assembly 只给泛型定义所在程序集（List<MetadataReference>→mscorlib），
        // 必须下钻 GetGenericArguments 才能抓到载荷实参（与 Mono 字段签名解析行为对齐）。
        static bool FieldTouchesPayload(Type t, string[] payloadAsms)
        {
            if (t == null) return false;
            while (t.IsArray || t.IsByRef || t.IsPointer) t = t.GetElementType();
            if (t == null) return false;
            string asm = t.Assembly.GetName().Name;
            foreach (string p in payloadAsms) if (asm == p) return true;
            if (t.IsGenericType)
                foreach (Type a in t.GetGenericArguments())
                    if (FieldTouchesPayload(a, payloadAsms)) return true;
            return false;
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

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
            TestEnsureLoadedIsIdempotent();
            TestSuccessPathViaChildProcess();
            TestReferenceSet();
            TestDefaultUsingsParsing();
            TestPolyfills();
            TestCompiler();
            TestDiagnosticFormat();
            TestReplSession();
            TestRoslynEvaluatorFacade();
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
                Check("GetVars 含 x", ev.GetVars().Contains("x"));
                Check("AddUsing 后 using 列表更新", AddUsingAndCheck(ev));

                Check("语法错误返回 false", !ev.Evaluate("var = ;", out outp));
                Check("错误后仍可用", ev.Evaluate("x", out outp) && ev.LastValueText == "1");
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
                try { ok = RoslynAssemblyLoader.EnsureLoaded(real); }
                catch (Exception e) { detail = e.GetType().Name + ": " + e.Message; }
            }
            Check("子进程：EnsureLoaded(真实载荷目录)==true", ok, detail);
            Check("子进程：IsAvailable==true", RoslynAssemblyLoader.IsAvailable);
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
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

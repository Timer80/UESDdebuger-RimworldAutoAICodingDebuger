using System;
using System.Linq;
using UnityExplorer.RoslynBridge;

namespace RoslynBridgeTests
{
    internal static class Program
    {
        static int failures;
        static int passLines;
        static int childPassLines;
        static int childFailLines;
        static void Check(string name, bool cond, string detail = null)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name
                + (cond || string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
            if (cond) passLines++; else failures++;
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
            // 根因清扫轮（§17）
            TestDefaultUsingsSources();
            TestDefaultUsingsResolvable();
            TestUnavailableUsingFilter();
            TestUsingPoisoningGuard();
            TestParseOptionsLanguageVersion();
            TestModernMemberDeclarations();
            TestCapabilityMatrix();
            TestUnrecognizedMemberRejected();
            TestNoTrailingSemicolon();
            TestDiagnosticsDoNotLeakAcrossSubmissions();
            TestAnonymousTypeReadable();
            TestEvaluatorSelection();
            TestUsingPrimeEquivalence();
            // 复审修订轮（§18）
            TestMcsErrorCountingSemantics();
            TestReferenceProfileUeSide();
            TestR5MinorFixes();
            // §19 REPL 助手轮
            TestHostBaseTypeNameFor();
            TestHostBaseGeneratedSourceShape();
            TestHostBaseSignatureAlignment();
            TestHostSelfReferenceNeeded();
            TestHostHelperNameShadowing();
            // §21 CS0029 值通道回退
            TestVoidValueChannelFallback();
            TestVoidFallbackProductionShape();
            // §22 任务 9：自动补全（语义模型成员 + 命名空间元数据 + 关键字表）
            TestCompletion();
            // 任务 10：HookInstance 的 Roslyn 补丁链路（包装 → 提交编译 → 产物取回 → Harmony 挂载生效）
            TestHookCompile();
            // 任务 10 修复轮（报告 §25 R-f）：Reset/Dispose 前"活 hook"判据（纯函数，离线断言）
            TestActiveHookResetWarning();
            // 任务 13：引用自愈（从编译诊断读出缺哪个程序集/命名空间 → 自动补引用重试）
            TestMissingReferenceParser();
            TestHealerByNamespace();
            TestHealerRefusalsAndSwitch();
            TestHealCapMessage();
            TestHealRollbackOnNoEffect();
            TestReferenceSetDedupeLog();
            TestHealReplIntegration();
            // S3（§20）：**机读计数行**——口径 = 各进程内实际打印的 PASS/FAIL 行数（循环展开后按行计）。
            // 父/子分开报，避免此前"父计数里混进了子进程输出"的统计失真。
            Console.WriteLine("COUNTS parent_pass=" + passLines + " parent_fail=" + failures
                + " child_pass=" + childPassLines + " child_fail=" + childFailLines
                + " total_pass=" + (passLines + childPassLines)
                + " total_fail=" + (failures + childFailLines));
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
            var s = NewSession(refs);

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
            var s2 = NewSession(refs);
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
            using (var ev = NewEvaluator(refs))
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
            using (var evT = NewEvaluator(refs, "", true))
            using (var evF = NewEvaluator(refs, "", false))
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
            var ev2 = NewEvaluator(refs);
            ev2.Dispose();
            Check("Dispose 后 Evaluate 返回 false", !ev2.Evaluate("1 + 1", out outp2), outp2);
        }

        static bool AddUsingAndCheck(RoslynScriptEvaluator ev)
        {
            ev.AddUsing("System.Text");
            return ev.GetUsing().Contains("System.Text");
        }

        /// <summary>
        /// §19：离线测试用"无宿主基类"形态构造会话（注入 null）——宿主程序集（UE）在测试进程里不存在，
        /// 若用生产默认（继承 ReplHostBase）所有离线提交都会 CS0246 而失去意义。
        /// 生产形态（默认 ctor + 继承 ReplHostBase）由 TestHostBase* 三个用例单独断言。
        /// </summary>
        /// <summary>§19：离线门面（注入 null 宿主基类，理由同 NewSession）。</summary>
        static RoslynScriptEvaluator NewEvaluator(System.Collections.Generic.IEnumerable<string> refs,
            string blacklist = "", bool allowMod = false)
            => new RoslynScriptEvaluator(refs, blacklist, allowMod, null);
        static ReplSession NewSession(System.Collections.Generic.IEnumerable<string> refs)
            => new ReplSession(refs, null);
        static System.Collections.Generic.List<string> ReferenceSetForThisProcess()
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!string.IsNullOrEmpty(a.Location)) list.Add(a.Location); } catch { }
            }
            return list;
        }

        // ══════════════ 根因清扫轮（§17）：B1 / B2 / B3 / B4 / 追加 E ══════════════

        /// <summary>B1(a) 一致性表：每个 BuiltIn 项都必须标注来源程序集，且表里不得有多余项。</summary>
        static void TestDefaultUsingsSources()
        {
            var missing = new System.Collections.Generic.List<string>();
            foreach (string ns in DefaultUsings.BuiltIn)
                if (DefaultUsings.OwnerOf(ns) == null) missing.Add(ns);
            Check("B1 一致性：BuiltIn 全项都有来源程序集（" + DefaultUsings.BuiltIn.Length + " 项）",
                missing.Count == 0, "未标注: " + string.Join(",", missing.ToArray()));
            Check("B1 一致性：来源表无多余项", DefaultUsings.SourceAssembly.Count == DefaultUsings.BuiltIn.Length,
                "表=" + DefaultUsings.SourceAssembly.Count + " BuiltIn=" + DefaultUsings.BuiltIn.Length);

            // 对照表（进报告 §17）
            foreach (string ns in DefaultUsings.BuiltIn)
                Console.WriteLine("USINGMAP " + ns + " -> " + DefaultUsings.OwnerOf(ns));
        }

        /// <summary>B1(b) 用**真实 dll 元数据**离线验证"默认 using 全项可用"（13/13 = `DefaultUsings.BuiltIn` 实际项数）。</summary>
        static void TestDefaultUsingsResolvable()
        {
            string managed = FindUpwards("RimWorldWin64_Data\\Managed");
            var ueAssemblies = FindUpwards("Assemblies");   // 仓库根的 Assemblies（UE 侧程序集）
            if (managed == null || ueAssemblies == null)
            {
                Check("B1(b) 默认 using 可用性（离线）：前置（能找到本体 Managed 与 UE Assemblies）",
                    false, "managed=" + managed + " ue=" + ueAssemblies);
                return;
            }

            // 引用集 = SmallProfile 里在磁盘上真实存在的件（本体 Managed + UE Assemblies 各取所需）
            var refs = new System.Collections.Generic.List<string>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (!string.IsNullOrEmpty(a.Location)) refs.Add(a.Location); } catch { }
            }
            foreach (string simple in new[]
            {
                "Assembly-CSharp", "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.IMGUIModule",
                "UnityEngine.UI", "UnityEngine.TextRenderingModule", "UnityEngine.PhysicsModule",
                "UnityEngine.AnimationModule", "UnityEngine.ParticleSystemModule",
                "UnityEngine.AssetBundleModule", "Unity.Collections", "Unity.Mathematics",
                "com.rlabrecque.steamworks.net", "System.Xml", "System.Xml.Linq",
            })
            {
                string p = System.IO.Path.Combine(managed, simple + ".dll");
                if (System.IO.File.Exists(p)) refs.Add(p);
            }
            string ue = System.IO.Path.Combine(ueAssemblies, "UniverseLib.Mono.dll");
            if (System.IO.File.Exists(ue)) refs.Add(ue);
            else Console.WriteLine("USINGMAP 警告：UE 侧 " + ue + " 不存在（UniverseLib 项必然失败）");

            int okCount = 0;
            var bad = new System.Text.StringBuilder();
            foreach (string ns in DefaultUsings.BuiltIn)
            {
                string src = "using " + ns + ";\npublic class __UsingProbe_" + okCount + " { }\n";
                var r = RoslynCompiler.Compile("UsingProbe_" + okCount, src, refs, includePolyfills: false);
                if (r.Success) okCount++;
                else bad.AppendLine(ns + " → " + r.Diagnostics.Replace("\r", " ").Replace("\n", " | "));
            }
            Check("B1(b) 默认 using 全项离线可用（" + okCount + "/" + DefaultUsings.BuiltIn.Length
                + "，真实 dll 元数据）", okCount == DefaultUsings.BuiltIn.Length, bad.ToString().TrimEnd());
        }

        /// <summary>B1/D 护栏函数：缺件时精确点名（且不误伤用户自加的模组命名空间）。</summary>
        static void TestUnavailableUsingFilter()
        {
            var allAvailable = DefaultUsings.Unavailable(DefaultUsings.BuiltIn, n => true);
            Check("B1/D 护栏：引用集齐备时零剔除", allAvailable.Count == 0,
                string.Join(",", allAvailable.ToArray()));

            var noUe = DefaultUsings.Unavailable(DefaultUsings.BuiltIn, n => n != "UniverseLib.Mono");
            Check("B1/D 护栏：缺 UniverseLib.Mono ⇒ 只剔 UniverseLib",
                noUe.Count == 1 && noUe[0] == "UniverseLib", string.Join(",", noUe.ToArray()));

            var mixed = new System.Collections.Generic.List<string>(DefaultUsings.BuiltIn);
            mixed.Add("SomeMod.Extras");
            var withMod = DefaultUsings.Unavailable(mixed, n => n != "UniverseLib.Mono");
            Check("B1/D 护栏：用户自加命名空间不被误剔（无来源标注 ⇒ 保留）",
                !withMod.Contains("SomeMod.Extras") && withMod.Count == 1, string.Join(",", withMod.ToArray()));
        }

        /// <summary>
        /// B1/D 纵深：① 编译不过的 using **不进会话表**（旧码在编译前就入表 ⇒ 一条脏 using 让此后
        /// 每一次提交都失败，实测症状就是 `new[]{1,2,3}.Sum()` 报 CS0246 'UniverseLib'）；
        /// ② 批量预置把不可用项剔除且**不毒化会话**。
        /// </summary>
        static void TestUsingPoisoningGuard()
        {
            var refs = ReferenceSetForThisProcess();

            // ① 单条失败 using 不得留表
            var s1 = NewSession(refs);
            var bad = s1.Submit("using UniverseLib;");       // 离线引用集里没有 UniverseLib
            Check("B1/D ①：无用引用的 using 提交失败（前置）", !bad.Success, bad.Diagnostics);
            Check("B1/D ①：失败 using 未进会话表", s1.GetUsing().IndexOf("UniverseLib", StringComparison.Ordinal) < 0,
                s1.GetUsing());
            var after = s1.Submit("var arr = new[] { 1, 2, 3 }; arr.Length");
            Check("B1/D ①：脏 using 之后的提交仍可用（旧码此处置 CS0246）",
                after.Success && s1.LastValue?.ToString() == "3",
                "diag=" + after.Diagnostics);

            // ② 批量预置：不可用项被剔除、可用项保留、会话不毒化
            var s2 = NewSession(refs);
            string diag;
            var accepted = s2.AddUsings(new[] { "System.Linq", "UniverseLib", "System.Text" }, out diag);
            Check("B1/D ②：批量预置剔除不可用项（保留 2 项）",
                accepted.Count == 2 && !accepted.Contains("UniverseLib"), string.Join(",", accepted.ToArray()));
            Check("B1/D ②：剔除原因写进诊断", !string.IsNullOrEmpty(diag) && diag.Contains("UniverseLib"), diag);
            var ok2 = s2.Submit("System.Linq.Enumerable.Range(1, 4).Sum()");
            Check("B1/D ②：剔除后会话可用", ok2.Success && s2.LastValue?.ToString() == "10",
                "diag=" + ok2.Diagnostics);
            Check("B1/D ②：GetUsing 不含被剔项",
                s2.GetUsing().IndexOf("UniverseLib", StringComparison.Ordinal) < 0, s2.GetUsing());
        }

        /// <summary>B2：LanguageVersion 确实生效（离线断言解析结果，不靠推断）。</summary>
        static void TestParseOptionsLanguageVersion()
        {
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "public record Point(int X, int Y);",
                new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(
                    Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest));
            var root = tree.GetRoot();
            int records = 0;
            foreach (var n in root.DescendantNodes())
                if (n is Microsoft.CodeAnalysis.CSharp.Syntax.RecordDeclarationSyntax) records++;
            Check("B2：LanguageVersion.Latest 下 record 解析为 RecordDeclarationSyntax", records == 1,
                "records=" + records);

            int errs = 0;
            foreach (var d in tree.GetDiagnostics())
                if (d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error) errs++;
            Check("B2：record 解析零语法错误", errs == 0, "errors=" + errs);
        }

        /// <summary>B2 回归：record / record struct / delegate 提交后**真的可用**（旧码静默丢弃 ⇒ CS0246）。</summary>
        static void TestModernMemberDeclarations()
        {
            var refs = ReferenceSetForThisProcess();

            var s = NewSession(refs);
            var def = s.Submit("public record Point(int X, int Y);");
            Check("B2 回归：record 提交成功", def.Success, def.Diagnostics);
            var use = s.Submit("new Point(3, 4).X + new Point(3, 4).Y");
            Check("B2 回归：record 类型后续提交可用（旧码此处 CS0246 'Point'）",
                use.Success && s.LastValue?.ToString() == "7", "diag=" + use.Diagnostics);
            Check("B2 回归：GetClasses 含 Point", s.GetClasses().Contains("Point"), s.GetClasses());

            var s2 = NewSession(refs);
            var rs = s2.Submit("public record struct Vec(int X, int Y);");
            Check("B2 回归：record struct 提交成功", rs.Success, rs.Diagnostics);
            var use2 = s2.Submit("new Vec(2, 5).X * new Vec(2, 5).Y");
            Check("B2 回归：record struct 后续提交可用", use2.Success && s2.LastValue?.ToString() == "10",
                "diag=" + use2.Diagnostics + " src=" + use2.GeneratedSource);

            var s3 = NewSession(refs);
            var dg = s3.Submit("public delegate int Doubler(int a);");
            Check("B2 清扫：delegate 声明提交成功（旧码静默丢弃）", dg.Success, dg.Diagnostics);
            var du = s3.Submit("Doubler d = a => a * 2; d(21)");
            Check("B2 清扫：delegate 后续提交可用", du.Success && s3.LastValue?.ToString() == "42",
                "diag=" + du.Diagnostics);
        }

        /// <summary>
        /// C# 9–13 能力矩阵（追加 F：逐项测"能/不能"，不能的归类原因）。
        /// 归因口径：分类未覆盖 / 语言版本未开 / polyfill 缺失 / 运行时缺 API / C# 语法歧义（非本实现可控）。
        /// 硬断言只覆盖"必须能用"的项；预期不支持的项以 MATRIX 行如实记录（报告 §17 引用）。
        /// </summary>
        static void TestCapabilityMatrix()
        {
            var refs = ReferenceSetForThisProcess();

            // (名称, 语言版本, 提交序列, 期望能用, 不支持时的归因)
            // 原始字符串字面量的定界符是三引号：在 C# 9 的测试源码里必须拼出来（裸写 \\"\\"\\" 会被测试自身解析坏）
            string tq = new string('"', 3);
            var cases = new (string Name, string Def, string Use, bool ExpectOk, string Reason)[]
            {
                ("record (C#9)", "public record P9(int A);", "new P9(3).A", true, ""),
                ("record struct (C#10)", "public record struct S10(int A);", "new S10(4).A", true, ""),
                ("init + required (C#9/11)", "public class Init9 { public int V { get; init; } public required string R { get; set; } }",
                    "new Init9 { V = 9, R = \"x\" }.V", true, ""),
                ("raw string (C#11)", "var raw = " + tq + "line" + tq + ";", "raw.Length", true, ""),
                ("pattern matching (C#9/10)", "object o = 5;", "o is int n && n > 3", true, ""),
                ("primary ctor (C#12)", "public class Pc12(int seed) { public int Twice() => seed * 2; }", "new Pc12(21).Twice()", true, ""),
                ("using alias (C#12 任意类型别名)", "using SB = System.Text.StringBuilder;", "new SB().Append(7).ToString()", true, ""),
                ("collection expr (C#12)", "int[] ce = [1, 2, 3];", "ce.Length", true, ""),
                ("params 集合 (C#13)", "static int Sum13(params System.Collections.Generic.IEnumerable<int> xs) { int n = 0; foreach (int x in xs) n += x; return n; }", "Sum13(1, 2, 3)", true, ""),
                ("顶层方法/属性成员 (REPL 便利)", "static int Twice(int a) => a * 2;", "Twice(21)", true, ""),
                ("file 作用域类型 (C#11)", "file class F11 { }", "1", false, "语言限制（CS9054：file 类型必须顶层，生成类里只能是嵌套类型）"),
                ("generic math (C#11 static abstract)", "interface IM11 { static abstract int Z { get; } }", "1", false, "运行时缺 API（CS8919：net472 不支持接口静态抽象成员）"),
            };

            foreach (var c in cases)
            {
                var s = NewSession(refs);
                var d1 = s.Submit(c.Def);
                bool ok = d1.Success;
                string why = d1.Success ? "" : FirstError(d1.Diagnostics);
                if (ok && c.Use != "1")
                {
                    var d2 = s.Submit(c.Use);
                    ok = d2.Success;
                    why = d2.Success ? "" : FirstError(d2.Diagnostics);
                }
                Console.WriteLine("MATRIX " + c.Name + " = " + (ok ? "PASS" : "FAIL")
                    + (ok ? "" : "  [" + why + "]"));
                if (c.ExpectOk)
                    Check("C# 矩阵：" + c.Name + " 可用", ok, why);
            }

            // 已知语法歧义（本轮实测，**非本实现引入**）：Roslyn 把 `a.b * c.d;` 解析成"指针类型局部声明"，
            // 与乘号表达式同形。此处如实记录，报告 §17 写清未做理由与修法（词法切分 + 括号消歧）。
            var sAmb = NewSession(refs);
            sAmb.Submit("var pp = new System.Drawing.Point(2, 3);");   // 该类型在离线引用集里可能没有 → 忽略结果
            var amb = sAmb.Submit("1 * 2;");
            Console.WriteLine("MATRIX 顶层 `expr.A * expr.B`（乘号 vs 指针类型歧义）= "
                + (amb.Success ? "PASS" : "FAIL") + "  [C# 语法歧义，见 §17 未做项]");
        }

        static string FirstError(string diagnostics)
        {
            if (string.IsNullOrEmpty(diagnostics)) return "无诊断";
            string first = diagnostics.Split('\n')[0].Trim();
            return first.Length > 150 ? first.Substring(0, 150) + "…" : first;
        }

        /// <summary>
        /// §17 根因：末尾缺分号的输入（REPL 常态）曾因解析器错误恢复被并进前一条声明 ⇒ 生成垃圾字段
        /// （实测 `public static v.X * v;` + CS0214 指针错误）。现在在解析前整段补分号。
        /// </summary>
        static void TestNoTrailingSemicolon()
        {
            var refs = ReferenceSetForThisProcess();

            var s = NewSession(refs);
            var r1 = s.Submit("var noSemi = 3");            // 无分号声明
            Check("§17：裸声明（无分号）提交成功", r1.Success, r1.Diagnostics);
            var r2 = s.Submit("noSemi * 2");                // 无分号表达式
            Check("§17：裸表达式（无分号）求值正确", r2.Success && s.LastValue?.ToString() == "6",
                "diag=" + r2.Diagnostics);

            // 多条语句同一行、末条缺分号：旧码会并把两条并进一条声明（生成垃圾字段）
            var s2 = NewSession(refs);
            var r3 = s2.Submit("var a = 4; var b = 5; a + b");
            Check("§17：同行多语句 + 末条缺分号 → 正确求值（旧码生成垃圾字段）",
                r3.Success && s2.LastValue?.ToString() == "9", "diag=" + r3.Diagnostics + " src=" + r3.GeneratedSource);
        }

        /// <summary>追加 F.2：未识别形态必须**响亮拒绝**（旧码静默丢弃 ⇒ 后续"类型莫名找不到"）。</summary>
        static void TestUnrecognizedMemberRejected()
        {
            var s = NewSession(ReferenceSetForThisProcess());
            var r = s.Submit("namespace Foo { public class Bar { } }");
            Check("F.2：namespace 成员被明确拒绝（不静默丢弃）",
                !r.Success && r.Diagnostics.Contains("不支持的顶层成员形态"), r.Diagnostics);
            Check("F.2：拒绝时诊断含形态名", r.Diagnostics.Contains("NamespaceDeclaration"), r.Diagnostics);

            var again = s.Submit("var okAfterReject = 1; okAfterReject");
            Check("F.2：拒绝不改会话状态（后续提交仍可用）",
                again.Success && s.LastValue?.ToString() == "1", "diag=" + again.Diagnostics);
        }

        /// <summary>B3：诊断**不得跨提交累积**（Important；这条污染曾把两个 agent 一起带偏）。</summary>
        static void TestDiagnosticsDoNotLeakAcrossSubmissions()
        {
            var s = NewSession(ReferenceSetForThisProcess());

            var fail = s.Submit("this is not valid C# at all");
            Check("B3：非法提交失败（前置）", !fail.Success && !string.IsNullOrEmpty(fail.Diagnostics));

            var next = s.Submit("var zz = 1; zz + 1");
            Check("B3：失败之后的提交成功", next.Success, next.Diagnostics);
            Check("B3：后续提交诊断不含上一条的错误文本",
                !next.Diagnostics.Contains("not valid C#"), next.Diagnostics);
            Check("B3：后续提交 LastResult 也是本次的（无累积容器）",
                !s.LastResult.Diagnostics.Contains("not valid C#"), s.LastResult.Diagnostics);

            var unknown = s.Submit("unknownIdent * 3");
            Check("B3：未知标识符的诊断含标识符名", !unknown.Success && unknown.Diagnostics.Contains("unknownIdent"),
                unknown.Diagnostics);
            Check("B3：该诊断不含更早提交的文本",
                !unknown.Diagnostics.Contains("not valid C#"), unknown.Diagnostics);
        }

        /// <summary>B3：匿名类型不再产生"半截无意义"的诊断 —— 按 object 提升 + 可读提示。</summary>
        static void TestAnonymousTypeReadable()
        {
            var s = NewSession(ReferenceSetForThisProcess());
            var decl = s.Submit("var anon = new { Name = \"a\", Age = 1 };");
            Check("B3：匿名类型声明提交成功（旧码生成非法字段类型 ⇒ 解析错误）", decl.Success, decl.Diagnostics);
            Check("B3：给出可读提示（已按 object 提升）",
                !string.IsNullOrEmpty(decl.Notes) && decl.Notes.Contains("匿名类型"), decl.Notes);

            var use = s.Submit("anon.Name");
            Check("B3：跨提交访问匿名成员失败但诊断可读（含成员名）",
                !use.Success && use.Diagnostics.Contains("Name"), use.Diagnostics);
        }

        /// <summary>追加 E：求值器选择真值表（离线；"当前生效的是 Roslyn"从此有非游戏内证据）。</summary>
        static void TestEvaluatorSelection()
        {
            // ChooseInitial：三输入真值表 ≠Roslyn 的组合共 7 种，全 Roslyn 只 1 种
            int roslynCount = 0, mcsCount = 0;
            foreach (bool compiled in new[] { false, true })
                foreach (bool want in new[] { false, true })
                    foreach (bool payload in new[] { false, true })
                    {
                        var c = EvaluatorSelector.ChooseInitial(compiled, want, payload);
                        bool expectRoslyn = compiled && want && payload;
                        Check("E：ChooseInitial(" + compiled + "," + want + "," + payload + ")=" + c,
                            (c == EvaluatorChoice.Roslyn) == expectRoslyn, "got " + c);
                        if (c == EvaluatorChoice.Roslyn) roslynCount++; else mcsCount++;
                    }
            Check("E：真值表计数（Roslyn 1 / mcs 7）", roslynCount == 1 && mcsCount == 7,
                "roslyn=" + roslynCount + " mcs=" + mcsCount);

            // ChooseAfterProbe：Roslyn+失败→回退 mcs；mcs+失败→Disable
            Check("E：Roslyn+探针通过→Roslyn",
                EvaluatorSelector.ChooseAfterProbe(EvaluatorChoice.Roslyn, true) == EvaluatorChoice.Roslyn);
            Check("E：Roslyn+探针失败→回退 mcs（不是 Disable）",
                EvaluatorSelector.ChooseAfterProbe(EvaluatorChoice.Roslyn, false) == EvaluatorChoice.Mcs);
            Check("E：mcs+探针通过→mcs",
                EvaluatorSelector.ChooseAfterProbe(EvaluatorChoice.Mcs, true) == EvaluatorChoice.Mcs);
            Check("E：mcs+探针失败→Disable",
                EvaluatorSelector.ChooseAfterProbe(EvaluatorChoice.Mcs, false) == EvaluatorChoice.Disable);

            // 追加 C：UsingRoslyn 语义 = true ⟺ 终态 Roslyn（实际生效 RoslynScriptEvaluator）
            Check("C：UsingRoslyn(Roslyn)=true", EvaluatorSelector.UsingRoslyn(EvaluatorChoice.Roslyn));
            Check("C：UsingRoslyn(mcs)=false", !EvaluatorSelector.UsingRoslyn(EvaluatorChoice.Mcs));
            Check("C：UsingRoslyn(Disable)=false", !EvaluatorSelector.UsingRoslyn(EvaluatorChoice.Disable));
        }

        /// <summary>
        /// R1（§18）mcs 语义 harness：用**真实的 Mono.CSharp** 管线验证失败判定口径（复审反编译结论）。
        /// 关键事实：`Evaluator.Compile` 开头调 `ctx.Report.Printer.Reset()`（把 ErrorsCount 清零）
        /// ⇒ ErrorsCount 是**本次**错误数、**不是粘性计数器** ⇒ 判失败必须用 `ErrorsCount > 0`；
        /// 而"与上一次之差 > 0"（§17.3 的旧口径）在"连续两次失败且错误数相同"时会**静默假成功**。
        /// </summary>
        static void TestMcsErrorCountingSemantics()
        {
            var sw = new System.IO.StringWriter();
            var printer = new Mono.CSharp.StreamReportPrinter(sw);
            var settings = new Mono.CSharp.CompilerSettings
            {
                Version = Mono.CSharp.LanguageVersion.Experimental,
                GenerateDebugInfo = false,
                StdLib = true,
                Target = Mono.CSharp.Target.Library,
                WarningLevel = 0,
                EnhancedWarnings = false,
            };
            var evaluator = new Mono.CSharp.Evaluator(new Mono.CSharp.CompilerContext(settings, printer));

            Func<string, int> compileErrors = input =>
            {
                try { evaluator.Compile(input, out Mono.CSharp.CompiledMethod m); }
                catch { /* 编译期异常也读本次计数 */ }
                return printer.ErrorsCount;
            };

            int e1 = compileErrors("class {");
            Check("R1 mcs：失败编译 → 本次 ErrorsCount > 0", e1 > 0, "count=" + e1);

            int prev = e1;
            int e2 = compileErrors("class {");
            Check("R1 mcs：连续两次同样失败 → 错误数相同（⇒ 每次编译前被 Reset 清零）",
                e2 > 0 && e2 == prev, "prev=" + prev + " now=" + e2);
            Check("R1 mcs：新口径 ErrorsCount>0 判失败（正确）", e2 > 0);
            Check("R1 mcs：旧口径 Δ>0 在同一场景判**成功** = RED 依据（§18.1 勘误）",
                !(e2 - prev > 0), "Δ=" + (e2 - prev));

            int e3 = compileErrors("int r1ok = 1;");
            Check("R1 mcs：成功编译后 ErrorsCount 归零", e3 == 0, "count=" + e3);
            Check("R1 mcs：报告文本仍累积（故长度增量只用于取本次文本）", sw.ToString().Length > 0);
        }

        /// <summary>
        /// R2（§18）：UE 侧命中必须按**文件存在性**判定。原实现只判 `ueDir != null`
        /// ⇒ 目录在而 dll 缺失时名字仍进集合 ⇒ report 假报 22/22、响亮告警永不触发、
        /// `DefaultUsings.Unavailable` 判为可用 ⇒ `using UniverseLib;` 不剔除 ⇒ B1 症状静默回归。
        /// </summary>
        static void TestReferenceProfileUeSide()
        {
            string managed = FindUpwards("RimWorldWin64_Data\\Managed");
            string ueAssemblies = FindUpwards("Assemblies");
            if (managed == null || ueAssemblies == null)
            {
                Check("R2：前置（能找到本体 Managed 与 UE Assemblies）", false,
                    "managed=" + managed + " ue=" + ueAssemblies);
                return;
            }

            string report;
            System.Collections.Generic.List<string> unavailable, missingUe;
            string ueSelf = FindBuiltUeAssembly();

            // §19：默认 using 现在含 `UnityExplorer`（来源 = 宿主自引用 `@self`）⇒ "齐备"必须连自引用一起给
            var set1 = ReferenceProfile.CollectFrom(managed, ueAssemblies, ueSelf,
                "UnityExplorer.STANDALONE.Mono", "", out report, out unavailable, out missingUe);
            Check("R2/§19：UE 侧 + 宿主自引用齐备 → 集合 = 23", set1.Paths.Count == 23, report);
            Check("R2：report 口径 small=22/22 且 ue=1 self=1",
                report.Contains("small=22/22") && report.Contains("ue=1") && report.Contains("self=1"), report);
            Check("R2/§19：齐备时无缺失、无可剔默认 using",
                missingUe.Count == 0 && unavailable.Count == 0, report);

            // 同一输入但**不给自引用** ⇒ self=0 + 响亮告警 + `UnityExplorer` 进剔除表（护栏即时生效）
            ReferenceProfile.CollectFrom(managed, ueAssemblies, "",
                out report, out unavailable, out missingUe);
            Check("R2/§19：缺宿主自引用 ⇒ self=0 + 告警 + 剔除 UnityExplorer（不静默）",
                report.Contains("self=0") && report.Contains("宿主自引用缺失")
                && unavailable.Contains("UnityExplorer"), report);

            // 目录存在但 dll 缺失：用本体 Data 目录（存在且无 UniverseLib.Mono.dll）
            string noUe = System.IO.Path.GetDirectoryName(managed);
            var set2 = ReferenceProfile.CollectFrom(managed, noUe, "",
                out report, out unavailable, out missingUe);
            Check("R2：UE 侧 dll 缺失 → 集合 = 21（旧码此处假报 22 且静默）",
                set2.Paths.Count == 21, report);
            Check("R2：ue=0 且告警点名缺失件与目录",
                report.Contains("ue=0") && report.Contains("UniverseLib.Mono") && report.Contains(noUe), report);
            Check("R2：缺件 ⇒ UniverseLib 进'不可用默认 using'（护栏据此剔除）",
                unavailable.Contains("UniverseLib"), string.Join(",", unavailable.ToArray()));

            var set3 = ReferenceProfile.CollectFrom(managed, null, "",
                out report, out unavailable, out missingUe);
            Check("R2：ueDir=null 也判为缺件（不抛、不假报）",
                set3.Paths.Count == 21 && unavailable.Contains("UniverseLib"), report);

            var set4 = ReferenceProfile.CollectFrom(managed, ueAssemblies, "UniverseLib.Mono",
                out report, out unavailable, out missingUe);
            Check("R2：黑名单对 UE 侧新增项生效（21 + 告警）",
                set4.Paths.Count == 21 && !set4.Contains("UniverseLib.Mono"), report);
        }

        /// <summary>R5（§18）次要项回归：显式接口实现不被加 public、方法名不污染 GetClasses、末行注释不吃分号。</summary>
        static void TestR5MinorFixes()
        {
            var refs = ReferenceSetForThisProcess();

            var s = NewSession(refs);
            var def = s.Submit("public interface IFoo { int Bar(); } public class Impl : IFoo { int IFoo.Bar() => 7; }");
            Check("R5b：显式接口实现可编译（旧行为：补 public ⇒ CS0106）", def.Success, def.Diagnostics);
            var use = s.Submit("((IFoo)new Impl()).Bar()");
            Check("R5b：显式接口实现可用", use.Success && s.LastValue?.ToString() == "7",
                "diag=" + use.Diagnostics);

            var s2 = NewSession(refs);
            var fn = s2.Submit("static int H1() => 1;");
            Check("R5c：方法声明提交成功", fn.Success, fn.Diagnostics);
            Check("R5c：GetClasses 不混入方法名（只登记类型/委托）",
                !s2.GetClasses().Contains("H1"), s2.GetClasses());
            var fnUse = s2.Submit("H1()");
            Check("R5c：方法仍跨提交可用", fnUse.Success && s2.LastValue?.ToString() == "1",
                "diag=" + fnUse.Diagnostics);

            var s3 = NewSession(refs);
            var cmt = s3.Submit("var cmt = 3 // trailing comment");
            Check("§18：末行注释不吃掉补的分号（旧码把 ';' 补进注释 ⇒ 语法错误）",
                cmt.Success, "diag=" + cmt.Diagnostics + " src=" + cmt.GeneratedSource);
            var cmtUse = s3.Submit("cmt // 带注释的表达式");
            Check("§18：带行注释的表达式求值正确", cmtUse.Success && s3.LastValue?.ToString() == "3",
                "diag=" + cmtUse.Diagnostics);
        }

        // ══════════════ §19 REPL 助手轮：宿主自引用 + ReplHostBase ══════════════

        /// <summary>§19 ①：baseName 取值逻辑（纯函数，离线可断言）。</summary>
        static void TestHostBaseTypeNameFor()
        {
            Check("§19：第 1 个提交的基类 = 助手基类",
                ReplSession.BaseTypeNameFor(1) == ReplSession.HostBaseTypeName
                && ReplSession.HostBaseTypeName == "UnityExplorer.RoslynBridge.ReplHostBase",
                ReplSession.BaseTypeNameFor(1));
            Check("§19：第 2/3 个提交仍继承上一个提交",
                ReplSession.BaseTypeNameFor(2) == "Submission_1"
                && ReplSession.BaseTypeNameFor(3) == "Submission_2",
                ReplSession.BaseTypeNameFor(2) + "," + ReplSession.BaseTypeNameFor(3));
            Check("§19：可注入宿主基类名（离线测试用）",
                ReplSession.BaseTypeNameFor(1, "object") == "object");
        }

        /// <summary>§19 ③：生产形态的生成源码——链根是助手基类，且 `__result` 交由基类承载。</summary>
        static void TestHostBaseGeneratedSourceShape()
        {
            var s = new ReplSession(ReferenceSetForThisProcess());   // **生产默认**：继承 ReplHostBase
            var r = s.Submit("1 + 1");
            string src = r.GeneratedSource;
            Check("§19：生成类链根 = ReplHostBase",
                src.Contains("public class Submission_1 : UnityExplorer.RoslynBridge.ReplHostBase"), src);
            Check("§19：生成类不再自声明 __result（由基类承载，避免值通道丢失）",
                !src.Contains("public static object __result;"), src);
            Check("§19：离线无宿主自引用时如实证诊断 ReplHostBase（生产侧由自引用解决）",
                !r.Success && r.Diagnostics.Contains("ReplHostBase"), r.Diagnostics);
        }

        /// <summary>
        /// §19 ②：助手基类成员与 `ScriptInteraction` 的**可移植子集逐一对齐**。
        /// 用 Roslyn 做**源码级结构断言**（名字 + static + 返回/参数类型文本）：
        /// 不必装载宿主程序集、不依赖任何 UE 类型，比反射更稳且同样可判别。
        /// </summary>
        static void TestHostBaseSignatureAlignment()
        {
            string host = FindFileUpwards("ReplHostBase.cs");
            string mcsSide = FindFileUpwards("ScriptInteraction.cs");
            if (host == null || mcsSide == null)
            {
                Check("§19：前置（能找到 ReplHostBase.cs 与 ScriptInteraction.cs）", false,
                    "host=" + host + " mcs=" + mcsSide);
                return;
            }

            var hostMembers = MemberSignatures(System.IO.File.ReadAllText(host), "ReplHostBase");
            var mcsMembers = MemberSignatures(System.IO.File.ReadAllText(mcsSide), "ScriptInteraction");

            string[] portable =
            {
                "CurrentTarget", "AllTargets", "Log", "Start", "Stop",
                "Copy", "Paste", "GetUsing", "GetVars", "GetClasses",
            };
            foreach (string name in portable)
            {
                string a, b;
                bool inHost = hostMembers.TryGetValue(name, out a);
                bool inMcs = mcsMembers.TryGetValue(name, out b);
                Check("§19 对齐：" + name + " 两侧同名同签名同 static"
                    + (inHost ? "（" + a + "）" : "（宿主缺失）"),
                    inHost && inMcs && a == b, "host=" + a + " mcs=" + b);
            }
            // `Inspect` 有两个重载（(object) 与 (Type)）：`MemberSignatures` 按顺序登记为 Inspect / Inspect2，
            // 故 S-次要项修复：**两个重载都参与比较**（此前只比了 Inspect2）。
            Check("§19 对齐：Inspect(object) 与 Inspect(Type) 两个重载都在且各自一致",
                hostMembers.ContainsKey("Inspect") && mcsMembers.ContainsKey("Inspect")
                && hostMembers["Inspect"] == mcsMembers["Inspect"]
                && hostMembers.ContainsKey("Inspect2") && mcsMembers.ContainsKey("Inspect2")
                && hostMembers["Inspect2"] == mcsMembers["Inspect2"],
                "host=" + (hostMembers.ContainsKey("Inspect") ? hostMembers["Inspect"] : "<缺>")
                + " / " + (hostMembers.ContainsKey("Inspect2") ? hostMembers["Inspect2"] : "<缺>")
                + "   mcs=" + (mcsMembers.ContainsKey("Inspect") ? mcsMembers["Inspect"] : "<缺>")
                + " / " + (mcsMembers.ContainsKey("Inspect2") ? mcsMembers["Inspect2"] : "<缺>"));

            string hostText = System.IO.File.ReadAllText(host);
            // 断"基类列表为空"（语义），而非文本里是否出现 InteractiveBase——注释里说明"不继承 …"是正常的
            bool hostHasNoBase = true;
            var hostTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(hostText);
            foreach (var node in hostTree.GetRoot().DescendantNodes())
            {
                var cls = node as Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax;
                if (cls != null && cls.Identifier.Text == "ReplHostBase" && cls.BaseList != null)
                    hostHasNoBase = false;
            }
            Check("§19：ReplHostBase **不继承**任何基类（尤其不接 mcs 的 InteractiveBase）",
                hostHasNoBase, host);
            Check("§19：ReplHostBase 承载 __result 值通道字段",
                hostText.Contains("public static object __result;"), host);
        }

        /// <summary>
        /// 取一个类里的成员签名（名字 → "static 返回类型 名字(参数类型…)"）；重载用 名字2/名字3 追加登记。
        /// </summary>
        static System.Collections.Generic.Dictionary<string, string> MemberSignatures(
            string source, string className)
        {
            var map = new System.Collections.Generic.Dictionary<string, string>();
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var cls = node as Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax;
                if (cls == null || cls.Identifier.Text != className) continue;
                foreach (var member in cls.Members)
                {
                    var method = member as Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax;
                    if (method != null)
                    {
                        var ptypes = new System.Collections.Generic.List<string>();
                        foreach (var p in method.ParameterList.Parameters) ptypes.Add(p.Type.ToString());
                        string sig = (HasModifier(method.Modifiers,
                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword) ? "static " : "")
                            + method.ReturnType + " " + method.Identifier.Text + "("
                            + string.Join(",", ptypes.ToArray()) + ")";
                        map[UniqueMemberKey(map, method.Identifier.Text)] = sig;
                        continue;
                    }
                    var prop = member as Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax;
                    if (prop != null)
                        map[UniqueMemberKey(map, prop.Identifier.Text)] =
                            (HasModifier(prop.Modifiers,
                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword) ? "static " : "")
                            + prop.Type + " " + prop.Identifier.Text;
                }
            }
            return map;
        }

        /// <summary>修饰符判定（用 RawKind，避免依赖 Roslyn 的 `Any/​Kind` 扩展——本文件 `using System.Linq` 会遮蔽它们）。</summary>
        static bool HasModifier(Microsoft.CodeAnalysis.SyntaxTokenList modifiers,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind kind)
        {
            foreach (var m in modifiers)
                if (m.RawKind == (int)kind) return true;
            return false;
        }

        static string UniqueMemberKey(System.Collections.Generic.Dictionary<string, string> map, string name)        {
            if (!map.ContainsKey(name)) return name;
            for (int i = 2; ; i++)
                if (!map.ContainsKey(name + i)) return name + i;
        }

        /// <summary>
        /// §19 (i) 判别力：**宿主自引用**是否真的必需 —— 同一份"生成类形态"源码，
        /// 带 UE 程序集引用 ⇒ 编译成功；不带 ⇒ CS0246 `ReplHostBase`（= 游戏内实测症状）。
        /// </summary>
        static void TestHostSelfReferenceNeeded()
        {
            string ue = FindBuiltUeAssembly();
            if (ue == null)
            {
                Check("§19：前置（能找到已构建的 UE 程序集）", false,
                    @"未找到 Release\UnityExplorer.Standalone.Mono\UnityExplorer.STANDALONE.Mono.dll");
                return;
            }

            var refs = ReferenceSetForThisProcess();
            var refsWithSelf = new System.Collections.Generic.List<string>(refs);
            refsWithSelf.Add(ue);
            const string src =
                "public class Submission_1 : UnityExplorer.RoslynBridge.ReplHostBase\n"
                + "{ public static void Run() { __result = 42; } }\n";

            var withSelf = RoslynCompiler.Compile("SelfRefOk", src, refsWithSelf, includePolyfills: false);
            Check("§19 (i)：带宿主自引用 ⇒ 继承 ReplHostBase 的提交类编译成功（GREEN）",
                withSelf.Success, withSelf.Diagnostics);

            var withoutSelf = RoslynCompiler.Compile("SelfRefFail", src, refs, includePolyfills: false);
            Check("§19 (i)：不带宿主自引用 ⇒ CS0246 ReplHostBase（RED 依据 = 游戏内实测症状）",
                !withoutSelf.Success && withoutSelf.Diagnostics.Contains("ReplHostBase"),
                withoutSelf.Diagnostics);

            // 游戏内的**决定性探针**：`typeof(UnityExplorer.CSConsole.ConsoleController).Name` 曾报 CS0246
            // ⇒ 提交编译解析不到 UE 程序集。带自引用后必须成功（这是 §19 的验收判据之一）。
            const string probe = "public class P { public static string N() "
                + "=> typeof(UnityExplorer.CSConsole.ConsoleController).Name; }";
            var probeWithSelf = RoslynCompiler.Compile("SelfProbeOk", probe, refsWithSelf, includePolyfills: false);
            Check("§19 (i)：`typeof(UnityExplorer.CSConsole.ConsoleController).Name` 带自引用可编译（游戏内判据）",
                probeWithSelf.Success, probeWithSelf.Diagnostics);
            var probeWithout = RoslynCompiler.Compile("SelfProbeFail", probe, refs, includePolyfills: false);
            Check("§19 (i)：同探针不带自引用 ⇒ CS0246（复现游戏内实测症状）",
                !probeWithout.Success && probeWithout.Diagnostics.Contains("ConsoleController"),
                probeWithout.Diagnostics);

            // §19 副作用核验：宿主程序集进了引用集后，**是否会把载荷类型拖进引用面**？
            // UE 的公开面里凡涉及 Roslyn 的类型都不带载荷类型签名（C.5 擦除的附带收益）——
            // 这里**不引用任何载荷程序集**，直接用 `CompileResult`（UE 类型）编译，成功即证明不拖载荷。
            const string surface = "public class S { public static bool M() "
                + "{ var r = new UnityExplorer.RoslynBridge.CompileResult(); r.Success = true; return r.Success; } }";
            // ⚠️ 离线特有：测试程序集自己**也**link 了同一批源码（同名类型）⇒ 会出现 CS0433 歧义，
            // 那不是产品问题（游戏内只有一个 UE 程序集）。故这次编译把测试程序集自身从引用里剔除。
            var refsUeOnly = new System.Collections.Generic.List<string>();
            foreach (string r in refsWithSelf)
                if (!System.IO.Path.GetFileNameWithoutExtension(r)
                        .Equals("RoslynBridgeTests", StringComparison.OrdinalIgnoreCase))
                    refsUeOnly.Add(r);
            var surfaceOk = RoslynCompiler.Compile("SelfSurface", surface, refsUeOnly, includePolyfills: false);
            Check("§19 副作用：引用宿主程序集**不**把载荷类型拖进引用面（无需 Roslyn 程序集即可用 UE 类型）",
                surfaceOk.Success, surfaceOk.Diagnostics);
        }

        /// <summary>
        /// G1：**实测** 12 个助手名在真实 `using` 环境下的绑定行为，并证明"继承成员优先于 using 导入的类型"。
        /// 变体 A（继承 ReplHostBase）应全部可用；变体 B（不继承）逐名记录真实失败码
        /// （`Log` 应为 **CS1955** —— 被 `Verse.Log` **类型**吃掉，正是游戏内实测的机理）。
        /// </summary>
        static void TestHostHelperNameShadowing()
        {
            string ue = FindBuiltUeAssembly();
            string managed = FindUpwards("RimWorldWin64_Data\\Managed");
            if (ue == null || managed == null)
            {
                Check("G1：前置（UE 程序集 + 本体 Managed 可得）", false, "ue=" + ue + " managed=" + managed);
                return;
            }

            var refs = ReferenceSetForThisProcess();
            refs.Add(ue);
            foreach (string extra in new[]
            {
                "Assembly-CSharp", "UnityEngine", "UnityEngine.CoreModule",
                "UnityEngine.IMGUIModule", "UnityEngine.UI",
            })
            {
                string p = System.IO.Path.Combine(managed, extra + ".dll");
                if (System.IO.File.Exists(p)) refs.Add(p);
            }
            string ueDir = FindUpwards("Assemblies");
            if (ueDir != null)
            {
                string ul = System.IO.Path.Combine(ueDir, "UniverseLib.Mono.dll");
                if (System.IO.File.Exists(ul)) refs.Add(ul);
            }

            const string usings = "using System; using System.Linq; using System.Text; using System.Collections; "
                + "using System.Collections.Generic; using System.Reflection; using UnityEngine; using UniverseLib; "
                + "using Verse; using RimWorld; using Verse.AI; using RimWorld.Planet; using UnityExplorer; ";

            var calls = new (string Name, string Body)[]
            {
                ("CurrentTarget", "var a = CurrentTarget;"),
                ("AllTargets", "var a = AllTargets;"),
                ("Log", "Log(\"hello\");"),
                ("Inspect(object)", "Inspect((object)null);"),
                ("Inspect(Type)", "Inspect((Type)null);"),
                ("Start", "Start((IEnumerator)null);"),
                ("Stop", "Stop((Coroutine)null);"),
                ("Copy", "Copy((object)null);"),
                ("Paste", "var p = Paste();"),
                ("GetUsing", "GetUsing();"),
                ("GetVars", "GetVars();"),
                ("GetClasses", "GetClasses();"),
            };

            int okWithBase = 0, i = 0;
            foreach (var c in calls)
            {
                i++;
                string withBase = usings + "public class A : UnityExplorer.RoslynBridge.ReplHostBase "
                    + "{ public static void Run() { " + c.Body + " } }";
                var ra = RoslynCompiler.Compile("ShadowA" + i, withBase, refs, includePolyfills: false);
                if (ra.Success) okWithBase++;
                else Console.WriteLine("HELPERMAP " + c.Name + " = 有基类:FAIL[" + FirstError(ra.Diagnostics) + "]");

                string noBase = usings + "public class B { public static void Run() { " + c.Body + " } }";
                var rb = RoslynCompiler.Compile("ShadowB" + i, noBase, refs, includePolyfills: false);
                Console.WriteLine("HELPERMAP " + c.Name + " = 有基类:" + (ra.Success ? "OK" : "FAIL")
                    + " / 无基类:" + (rb.Success ? "OK" : FirstError(rb.Diagnostics)));
            }
            Check("G1：12 个助手名在继承 ReplHostBase 后**全部可用**（" + okWithBase + "/" + calls.Length + "）",
                okWithBase == calls.Length, "只成功 " + okWithBase);

            // 机理实证：无基类时 `Log("hello")` 被 `Verse.Log` **类型**吃掉 ⇒ CS1955（游戏内实测码）
            var rlog = RoslynCompiler.Compile("ShadowLogNoBase",
                usings + "public class C { public static void Run() { Log(\"hello\"); } }",
                refs, includePolyfills: false);
            Check("G1：无基类时 Log 报 CS1955（= 解析到类型 `Verse.Log`，G1 归因实证）",
                !rlog.Success && rlog.Diagnostics.Contains("CS1955"), rlog.Diagnostics);
            // S-次要项修复：断言名写了 CS0103，判据也必须校该错误码（此前只判 !Success，名与实不符）
            var rGv = RoslynCompiler.Compile("ShadowGetVarsNoBase",
                usings + "public class D { public static void Run() { GetVars(); } }",
                refs, includePolyfills: false);
            Check("G1：无基类时 GetVars 报 CS0103（UE 特有名字，using 链无同名物）",
                !rGv.Success && rGv.Diagnostics.Contains("CS0103"), rGv.Diagnostics);

            // 对等后果：助手 `Log` 成员遮蔽 `Verse.Log` **类型** ⇒ 要用本体日志须全限定（与 mcs 路径一致）
            var qualified = RoslynCompiler.Compile("ShadowQualified",
                usings + "public class E : UnityExplorer.RoslynBridge.ReplHostBase "
                + "{ public static void Run() { Verse.Log.Message(\"x\"); } }",
                refs, includePolyfills: false);
            Check("G1：全限定 `Verse.Log.Message(...)` 仍可用（对等后果，非缺陷）",
                qualified.Success, qualified.Diagnostics);
        }

        /// <summary>找已构建的 UE 程序集（Release 输出；§19 的宿主自引用/助手断言都拿它当元数据引用）。</summary>
        static string FindBuiltUeAssembly()
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string p = System.IO.Path.Combine(dir.FullName,
                    @"UE-4.9.0-official\UnityExplorer-4.9.0\Release\UnityExplorer.Standalone.Mono\UnityExplorer.STANDALONE.Mono.dll");
                if (System.IO.File.Exists(p)) return p;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>从测试程序目录向上按文件名找文件（§19 源码级签名断言用）。</summary>
        static string FindFileUpwards(string fileName)
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                foreach (string p in System.IO.Directory.GetFiles(dir.FullName, fileName,
                             System.IO.SearchOption.AllDirectories))
                    return p;
                dir = dir.Parent;
            }
            return null;
        }

        // ══════════════ §21 CS0029：void 末表达式 → 值通道回退（错误驱动，两遍） ══════════════

        /// <summary>
        /// §21：末表达式是 **void** 时不得生成 `__result = &lt;expr&gt;;`（CS0029：void → object）。
        /// 机制 = 错误驱动回退：先常规生成/编译，**仅当** pass 1 失败、且诊断是"落在**注入赋值行**上的
        /// CS0029"、且该行右值**自然类型是 void** 时，才以"末表达式原样语句"重生成重编译一次（**最多一次**）。
        /// 离线装置：宿主基类注入 `System.Threading.Thread`（它有 public static void `Sleep(int)`），
        /// 于是无需 UE 运行期即可复现"末条 void 调用"与"用户自己的 void API"两种形态。
        /// </summary>
        static void TestVoidValueChannelFallback()
        {
            var refs = ReferenceSetForThisProcess();
            // 装置：不注入宿主基类（Thread 是 sealed，不能当基类）；改为先定义**用户自己的 void 方法**，

            // ① 末条 void 调用（= 游戏内 Log("hello") 的同型形态）⇒ 回退后编译成功、无 CS0029
            var s = NewSession(refs);
            s.Submit("public class Vh { public static void Noop() { } }");
            var r1 = s.Submit("Vh.Noop();");
            Check("§21 ①：末条 void 调用编译成功（不再 CS0029）", r1.Success, r1.Diagnostics);
            Check("§21 ①：确实走了回退路径（Passes=2 / FallbackForVoid）",
                r1.Passes == 2 && r1.FallbackForVoid, "passes=" + r1.Passes);
            Check("§21 ①：最终源码不再注入赋值（原样语句）",
                !r1.GeneratedSource.Contains("__result = Vh.Noop()") && r1.GeneratedSource.Contains("Vh.Noop();"),
                r1.GeneratedSource);
            Check("§21 ①：诊断不含 CS0029（第一遍的错不得泄漏）",
                !r1.Diagnostics.Contains("CS0029"), r1.Diagnostics);
            Check("§21 ①：无值可回显（与 mcs 的 void 语义一致）", s.LastValue == null);

            // ② 值通道不回归（游戏内实测 42 的离线等价断言）
            var s2 = NewSession(refs);
            s2.Submit("var x = 1;");
            var r2 = s2.Submit("x + 41");
            Check("§21 ②：值通道不回归（x + 41 = 42）",
                r2.Success && s2.LastValue?.ToString() == "42", "value=" + s2.LastValue);
            Check("§21 ②/⑧：值表达式**零额外开销**（Passes=1，未走回退）",
                r2.Passes == 1 && !r2.FallbackForVoid, "passes=" + r2.Passes);

            // ③ 1 + 1 = 2（最基本的值表达式）
            var r3 = s2.Submit("1 + 1");
            Check("§21 ③：1 + 1 = 2 且 Passes=1",
                r3.Success && s2.LastValue?.ToString() == "2" && r3.Passes == 1,
                "value=" + s2.LastValue + " passes=" + r3.Passes);

            // ④ void 调用**之后**提交值表达式 ⇒ 状态没被搞坏
            var s4 = NewSession(refs);
            s4.Submit("public class Vh { public static void Noop() { } }");
            s4.Submit("Vh.Noop();");
            var r4 = s4.Submit("2 + 3");
            Check("§21 ④：void 之后的值表达式仍返回值（=5）",
                r4.Success && s4.LastValue?.ToString() == "5", "value=" + s4.LastValue);

            // ⑤ 反向护栏：`Sleep(0).ToString()` 是**非法**用法（对 void 取成员）⇒ 仍按正常报错处理
            var r5 = s4.Submit("Vh.Noop().ToString()");
            Check("§21 ⑤：对 void 取成员的非法用法仍报错（未被过度抑制）",
                !r5.Success && r5.ErrorCount > 0, r5.Diagnostics);

            // ⑦ 用户自己的 void API（非我们助手）同样可用 ⇒ 证明不是"12 助手名白名单"
            var s7 = NewSession(refs);
            var r7 = s7.Submit("System.Threading.Thread.Sleep(0);");   // 用户自己的 void 游戏/框架 API（全限定）
            Check("§21 ⑦：用户自己的 void 静态调用也可用（非助手白名单）",
                r7.Success && r7.Passes == 2, r7.Diagnostics);

            // ⑧ 非 void 静态调用不受影响（值通道照常）
            var r8 = s7.Submit("System.Math.Abs(-3)");
            Check("§21 ⑧：非 void 静态调用值通道照常（=3，Passes=1）",
                r8.Success && s7.LastValue?.ToString() == "3" && r8.Passes == 1,
                "value=" + s7.LastValue + " passes=" + r8.Passes);

            // ⑨a **不过度回退**：用户自己写的 CS0029（cast void）+ 末表达式不是 void ⇒ 不回退、原样报错
            var s9a = NewSession(refs);
            s9a.Submit("public class Vh { public static void Noop() { } }");
            var r9a = s9a.Submit("(object)Vh.Noop()");
            Check("§21 ⑨a：用户自己代码里的 CS0029 **不触发回退**（Passes=1，不掩盖真实错误）",
                !r9a.Success && r9a.Passes == 1 && !r9a.FallbackForVoid,
                "passes=" + r9a.Passes + " diag=" + r9a.Diagnostics);

            // ⑨b/⑨c/⑩：void 末条 + 另有真实错误 ⇒ 回退后仍失败，且**上报第二遍诊断**（第一遍 CS0029 不泄漏）
            var s9b = NewSession(refs);
            s9b.Submit("public class Vh { public static void Noop() { } }");
            var r9b = s9b.Submit("int z = NotDefinedSymbol; Vh.Noop();");
            Check("§21 ⑨b：回退后仍失败且报**真实错误**（NotDefinedSymbol）",
                !r9b.Success && r9b.Passes == 2 && r9b.Diagnostics.Contains("NotDefinedSymbol"),
                "passes=" + r9b.Passes + " diag=" + r9b.Diagnostics);
            Check("§21 ⑨c：第二遍诊断优先（第一遍 CS0029 不泄漏给用户）",
                !r9b.Diagnostics.Contains("CS0029"), r9b.Diagnostics);
            Check("§21 ⑩：回退**最多一次**（Passes 恰为 2，绝无第三遍）", r9b.Passes == 2);

            // 性能：void 回退路径 vs 值表达式的**实测耗时**（报告要数字，不要推理）
            // 先各跑一次"热身"（JIT/首次编译），再测，避免把冷启动算进回退成本
            NewSession(refs).Submit("1 + 1");
            NewSession(refs).Submit("System.Threading.Thread.Sleep(0);");
            var swVoid = System.Diagnostics.Stopwatch.StartNew();
            var sv = NewSession(refs);
            sv.Submit("public class Vh { public static void Noop() { } }");
            var rv = sv.Submit("Vh.Noop();");
            swVoid.Stop();
            var swValue = System.Diagnostics.Stopwatch.StartNew();
            var sval = NewSession(refs);
            var rval = sval.Submit("1 + 1");
            swValue.Stop();
            Console.WriteLine("VOIDFALLBACK void_ms=" + swVoid.ElapsedMilliseconds
                + " value_ms=" + swValue.ElapsedMilliseconds
                + " passes_void=" + rv.Passes + " passes_value=" + rval.Passes);
        }

        /// <summary>
        /// §21 生产形态判别力：用**真实 UE 程序集**证明"助手是 void ⇒ 注入赋值必 CS0029"
        /// 与"去掉赋值即可编译"（只编译不执行，故不依赖 UE 运行期状态）。
        /// </summary>
        static void TestVoidFallbackProductionShape()
        {
            string ue = FindBuiltUeAssembly();
            if (ue == null)
            {
                Check("§21 生产形态：能找到已构建 UE 程序集", false, "未找到 Release 产物");
                return;
            }
            var refs = ReferenceSetForThisProcess();
            refs.Add(ue);

            const string withAssign =
                "public class S : UnityExplorer.RoslynBridge.ReplHostBase "
                + "{ public static void Run() { __result = Log(\"hello\"); } }";
            var bad = RoslynCompiler.Compile("VoidAssign", withAssign, refs, includePolyfills: false);
            Check("§21 生产形态：助手是 void ⇒ `__result = Log(...)` 报 CS0029（回退前提）",
                !bad.Success && bad.Diagnostics.Contains("CS0029"), bad.Diagnostics);

            const string noAssign =
                "public class S : UnityExplorer.RoslynBridge.ReplHostBase "
                + "{ public static void Run() { Log(\"hello\"); } }";
            var ok = RoslynCompiler.Compile("VoidNoAssign", noAssign, refs, includePolyfills: false);
            Check("§21 生产形态：去掉赋值后同一源码可编译（回退形态有效）", ok.Success, ok.Diagnostics);
        }

        /// <summary>B4：批量预置与逐条提交**行为等价**，且成本可见（打印实测毫秒，供报告引用）。</summary>
        static void TestUsingPrimeEquivalence()
        {
            var refs = ReferenceSetForThisProcess();
            string[] namespaces = { "System.Linq", "System.Text", "System.Collections.Generic" };

            var bulk = NewSession(refs);
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            string diag;
            var accepted = bulk.AddUsings(namespaces, out diag);
            sw1.Stop();
            var r1 = bulk.Submit("System.Linq.Enumerable.Range(1, 3).Sum()");

            var perItem = NewSession(refs);
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            foreach (string ns in namespaces) perItem.Submit("using " + ns + ";");
            sw2.Stop();
            var r2 = perItem.Submit("System.Linq.Enumerable.Range(1, 3).Sum()");

            Check("B4：批量预置接受数与逐条一致（" + accepted.Count + "）", accepted.Count == namespaces.Length);
            Check("B4：批量与逐条求值结果一致",
                r1.Success && r2.Success && bulk.LastValue?.ToString() == perItem.LastValue?.ToString(),
                "bulk=" + bulk.LastValue + " perItem=" + perItem.LastValue);
            Console.WriteLine("PRIMECOST bulk=" + sw1.ElapsedMilliseconds + "ms perItem="
                + sw2.ElapsedMilliseconds + "ms (" + namespaces.Length + " 项)");
        }

        /// <summary>从测试程序目录向上找指定相对目录（B1(b) 离线可用性验证用）。</summary>
        static string FindUpwards(string relative)
        {
            var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                string p = System.IO.Path.Combine(dir.FullName, relative);
                if (System.IO.Directory.Exists(p)) return p;
                dir = dir.Parent;
            }
            return null;
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

            // S3（§20）：子进程出口行也带机读计数（父进程 spawn 时捕获并汇总，口径一致）
            Console.WriteLine("COUNTS child_pass=" + passLines + " child_fail=" + failures);
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

        /// <summary>
        /// S3（§20）：子进程输出**捕获后再原样打印**，于是父进程能给出**精确**的子进程 PASS/FAIL 行数，
        /// 且父进程自身的计数不再被"闯入的"子进程输出干扰（此前是 stdout 直接继承、只能事后猜）。
        /// </summary>
        static void TestSuccessPathViaChildProcess()
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, "--success-path")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();

                    Console.WriteLine("--- 子进程输出开始 ---");
                    Console.Write(output);
                    if (!string.IsNullOrEmpty(err)) Console.WriteLine("[stderr] " + err);
                    Console.WriteLine("--- 子进程输出结束 ---");

                    childPassLines = CountLines(output, "PASS ");
                    childFailLines = CountLines(output, "FAIL ");
                    Check("成功路径（子进程）退出码=0", p.ExitCode == 0, "exit=" + p.ExitCode);
                    Check("成功路径（子进程）自报计数行为 0 失败（child_fail=" + childFailLines + "）",
                        childFailLines == 0);
                }
            }
            catch (Exception e)
            {
                Check("成功路径（子进程）能启动", false, e.GetType().Name + ": " + e.Message);
            }
        }

        static int CountLines(string text, string prefix)
        {
            int n = 0;
            if (string.IsNullOrEmpty(text)) return 0;
            foreach (string line in text.Split('\n'))
                if (line.StartsWith(prefix, StringComparison.Ordinal)) n++;
            return n;
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

            // ── S1（§20）：扫描面缺口修复 ──
            // 上面扫的是**测试程序集**（链接进来的 RoslynBridge 源码）；`ReplHostBase.cs` 依赖 UE/Unity 类型，
            // **不能**link 进测试工程（那会把 ExplorerCore/InspectorManager/ClipboardPanel 等拖进来，
            // 且若改为引用 UE dll 又会与链接副本撞 CS0433）。故改为**用元数据读真产物**：
            // Roslyn 打开已构建的 UE 程序集 → 遍历全部类型的字段签名 → 复用同一判据。
            // 这比 link 更强：扫的就是**发布的那个 dll**（含 ReplHostBase 与所有产物类型）。
            TestC5GuardOnBuiltAssembly();
        }

        /// <summary>S1：对**已构建的 UE 程序集**做 C.5 判据扫描（元数据，不装载、不执行）。</summary>
        static void TestC5GuardOnBuiltAssembly()
        {
            string ue = FindBuiltUeAssembly();
            if (ue == null)
            {
                Check("C.5 护栏（产物面）：能找到已构建 UE 程序集", false,
                    @"未找到 Release\UnityExplorer.Standalone.Mono\UnityExplorer.STANDALONE.Mono.dll");
                return;
            }

            string[] payloadAsms = PayloadAssemblyNames();
            var comp = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("metaScan",
                null, new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(ue) });
            var asm = comp.GetAssemblyOrModuleSymbol(
                comp.References.First()) as Microsoft.CodeAnalysis.IAssemblySymbol;
            if (asm == null)
            {
                Check("C.5 护栏（产物面）：能读到程序集符号", false, ue);
                return;
            }

            var bad = new System.Text.StringBuilder();
            int types = 0, fields = 0;
            var stack = new System.Collections.Generic.Stack<Microsoft.CodeAnalysis.INamespaceSymbol>();
            stack.Push(asm.GlobalNamespace);
            while (stack.Count > 0)
            {
                var ns = stack.Pop();
                foreach (var nested in ns.GetNamespaceMembers()) stack.Push(nested);
                foreach (var type in ns.GetTypeMembers()) ScanSymbolType(type, payloadAsms, ref types, ref fields, bad);
            }
            Check("C.5 护栏（产物面）：UE 程序集全类型零载荷类型字段（扫 " + types + " 型 / " + fields + " 字段）",
                bad.Length == 0, bad.ToString().TrimEnd());
            Check("C.5 护栏（产物面）：ReplHostBase 在扫描面内（S1 缺口已闭合）",
                types > 0 && System.IO.File.ReadAllText(FindFileUpwards("ReplHostBase.cs") ?? ue)
                    .Contains("class ReplHostBase"), "扫描 " + types + " 型");
        }

        static void ScanSymbolType(Microsoft.CodeAnalysis.INamedTypeSymbol type, string[] payloadAsms,
            ref int types, ref int fields, System.Text.StringBuilder bad)
        {
            types++;
            foreach (var member in type.GetMembers())
            {
                var f = member as Microsoft.CodeAnalysis.IFieldSymbol;
                if (f == null) continue;
                fields++;
                if (SymbolTouchesPayload(f.Type, payloadAsms))
                    bad.AppendLine(type.ToDisplayString() + " . " + f.Name + " -> " + f.Type.ToDisplayString());
            }
            foreach (var nested in type.GetTypeMembers())
                ScanSymbolType(nested, payloadAsms, ref types, ref fields, bad);
        }

        /// <summary>泛型实参递归（与反射版判据同语义）。</summary>
        static bool SymbolTouchesPayload(Microsoft.CodeAnalysis.ITypeSymbol t, string[] payloadAsms)
        {
            if (t == null) return false;
            var arr = t as Microsoft.CodeAnalysis.IArrayTypeSymbol;
            if (arr != null) return SymbolTouchesPayload(arr.ElementType, payloadAsms);
            string asmName = t.ContainingAssembly == null ? null : t.ContainingAssembly.Name;
            if (asmName != null)
                foreach (string p in payloadAsms) if (asmName == p) return true;
            var named = t as Microsoft.CodeAnalysis.INamedTypeSymbol;
            if (named != null)
                foreach (var arg in named.TypeArguments)
                    if (SymbolTouchesPayload(arg, payloadAsms)) return true;
            return false;
        }

        static string[] PayloadAssemblyNames()
            => new[]
            {
                "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
                "System.Collections.Immutable", "System.Reflection.Metadata",
                "System.Memory", "System.Threading.Tasks.Extensions",
                "System.Runtime.CompilerServices.Unsafe", "System.Numerics.Vectors",
            };

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

        // ══════════════ §22 任务 9：自动补全（语义模型成员 / 命名空间元数据 / 关键字表） ══════════════

        /// <summary>
        /// 任务 9：`IScriptEvaluator.GetCompletions` 的两条来源（成员=语义模型 / 命名空间=元数据）与关键字表。
        /// 离线口径（对简报的偏离，见报告 §22）：宿主程序集（UE）不在测试进程里 ⇒ 用 `NewEvaluator`
        /// （注入 null 基类，§19 惯例），否则每次提交都 CS0246 `ReplHostBase`、用例失去判别力。
        /// </summary>
        static void TestCompletion()
        {
            var refs = ReferenceSetForThisProcess();
            using (var ev = NewEvaluator(refs))
            {
                string outp;
                Check("§22 前置：提交建变量 sb",
                    ev.Evaluate("var sb = new System.Text.StringBuilder();", out outp), outp);

                string prefix;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string[] c1 = ev.GetCompletions("sb.App", out prefix);
                sw.Stop();
                long coldMs = sw.ElapsedMilliseconds;
                sw.Restart();
                string[] c1b = ev.GetCompletions("sb.App", out prefix);
                sw.Stop();
                Console.WriteLine("COMPLETIONCOST cold_ms=" + coldMs + " warm_ms=" + sw.ElapsedMilliseconds
                    + " candidates=" + c1.Length);
                Console.WriteLine("MEMCOMPS sb.App → [" + JoinList(c1) + "] prefix=" + prefix);
                // 判别力：旧码是任务 8 的空桩（GetCompletions 恒返回空数组）⇒ 本行必 FAIL。
                Check("§22 成员来源命中 Append*（" + c1.Length + " 项）", AnyPrefix(c1, "end"), JoinList(c1));
                Check("§22 冷热两次候选一致（补全不改会话状态、可重复）", SameList(c1, c1b),
                    JoinList(c1) + " vs " + JoinList(c1b));
                Check("§22 prefix = 已输入的 `App`", prefix == "App", "prefix=" + prefix);
                // 判别力：候选必须是"扣掉 prefix 的**余部**"——CSAutoCompleter 把候选**插入光标处**，
                // 回填整名会得到 `sb.AppendAppend`（任何"返回整名"的实现本行必 FAIL）。
                Check("§22 候选 = 余部而非整名（`end` 在表内、`Append` 不在）",
                    HasName(c1, "end") && !HasName(c1, "Append"), JoinList(c1));

                // 反向护栏：不存在的成员 ⇒ 空候选（不误报）
                string[] cNone = ev.GetCompletions("sb.Zzz", out prefix);
                Check("§22 无匹配成员 ⇒ 空候选（不误报）", cNone.Length == 0, JoinList(cNone));

                // ② 成员**链**接收者（`list.Count`）：只有语义模型能判出它的类型——反射表/字符串表都拿不到。
                Check("§22 前置：提交建 list",
                    ev.Evaluate("var list = new System.Collections.Generic.List<int>();", out outp), outp);
                string[] c2 = ev.GetCompletions("list.Count.ToStr", out prefix);
                Check("§22 成员链接收者（list.Count）经语义模型命中 ToString",
                    c2.Length == 1 && c2[0] == "ing", "prefix=" + prefix + " " + JoinList(c2));

                // ③ 类型名接收者（静态成员）：符号路径（类型表达式没有"值类型"）
                string[] c3 = ev.GetCompletions("System.Math.Ab", out prefix);
                Check("§22 类型名接收者命中 Abs（静态成员）",
                    prefix == "Ab" && HasName(c3, "s"), "prefix=" + prefix + " " + JoinList(c3));

                // ④ 命名空间来源：`System.Tex` → `t`（插入后正好是 `System.Text`）
                string[] c4 = ev.GetCompletions("System.Tex", out prefix);
                Console.WriteLine("NSCOMPS System.Tex → [" + JoinList(c4) + "] prefix=" + prefix);
                Check("§22 命名空间来源命中 t/Text*（" + c4.Length + " 项）", AnyPrefix(c4, "t"), JoinList(c4));
                Check("§22 命名空间候选含 `t`（能补成 System.Text）", HasName(c4, "t"), JoinList(c4));

                // ⑤ 命名空间全集缓存后仍一致（同会话第二次调用走缓存）
                string[] c5 = ev.GetCompletions("System.Tex", out prefix);
                Check("§22 命名空间来源可重复（缓存不改变结果）",
                    c5.Length == c4.Length && c5.Length > 0, JoinList(c5));
            }

            // 命名空间表：能覆盖**未加载**的 dll，且**不把程序集载入进程**（只读元数据）
            TestNamespaceSourceWithoutLoading();

            // 关键字表（C# 8+ 补齐）：源码文本断言（KeywordLexer 不可 link，理由见报告 §22）
            TestKeywordTableSource();

            // §22 复审收尾轮（R1–R4）
            TestCompletionRobustness();                 // R2 结构兜底 + R3 失效钩子 + R4③ 不静默
            TestNamespaceSourceIncludesSubmissions();   // R4④ 提交件进源 + R4② 转发命名空间探针
            TestCompletionUiDedupSource();              // R1 UI 合并点去重（源码级断言）
            // §23 var 推断缺陷修复轮
            TestVarInference();
        }

        /// <summary>
        /// §23：`var` 推断缺陷（对端游戏内实测：`var sb = new StringBuilder();` **编译成功**但字段类型**静默
        /// 退化为 `object`** ⇒ `GetVars()` 显示 `(Object)`、`sb.Append("x")` 报 CS1929）。
        /// 装置与生产同形：**先预置默认 using 里的 `System.Text`**（`DefaultUsings.BuiltIn` 含它 ⇒ 正式提交
        /// 编得过、旧探针（不带任何 using）解析不到 ⇒ 正好复现"成功但退化"）；会话用离线惯例 `NewSession`
        /// （注入 null 宿主基类，§19）。
        /// 断言面覆盖"探针与正式提交**同可见性**"的四条通道：usings（①）、历次提交件（②⑤）、本次提交的
        /// 已生成成员（④）与**后置**成员（⑧）、以及**基类**（⑨，注入可解析替身）；
        /// ③⑥⑦ 为对照组/不回归护栏。判别力逐条写在断言旁（**实测判定**：HEAD 码 = 9 条 RED，
        /// 半成品 = ④⑧⑨ 共 4 条 RED，本轮 = 全绿）。
        /// </summary>
        static void TestVarInference()
        {
            var refs = ReferenceSetForThisProcess();

            // ① 核心用例：裸类型名 + var（控制台最常见写法）
            var s = NewSession(refs);
            string diag;
            var accepted = s.AddUsings(new[] { "System.Text", "System.Collections.Generic" }, out diag);
            var r1 = s.Submit("var sb = new StringBuilder();");
            // 判别力：旧码此处 GeneratedSource 里是 `public static object sb;` ⇒ 本行必 FAIL。
            Check("§23 ① `var sb = new StringBuilder();` 推断为 StringBuilder（不是 object）",
                r1.Success && r1.GeneratedSource.Contains("public static System.Text.StringBuilder sb;"),
                "accepted=" + accepted.Count + " diag=" + diag + " | " + r1.Diagnostics);
            // 判别力：旧码 GetVars 打 `(Object)` ⇒ 本行必 FAIL（这也是游戏内唯一可见线索）。
            Check("§23 ① GetVars() 显示真实类型（不是 `(Object)`）",
                s.GetVars().Contains("StringBuilder") && !s.GetVars().Contains("(Object)"), s.GetVars());
            Check("§23 ① 成员补全可用（旧码 object ⇒ 0 候选）", CompletionsOf(s, "sb.App") > 0,
                "count=" + CompletionsOf(s, "sb.App"));
            // 行为面：旧码连正常使用都坏（CS1929 'object' does not contain 'Append'）
            var r1b = s.Submit("sb.Append(\"x\"); sb.Length");
            Check("§23 ① 行为面：`sb.Append(\"x\"); sb.Length` = 1（旧码 CS1929）",
                r1b.Success && s.LastValue?.ToString() == "1", r1b.Diagnostics + " value=" + s.LastValue);

            // ② 初始化式引用**会话变量**（跨提交）：旧探针看不到 Submission_N ⇒ object
            var s2 = NewSession(refs);
            s2.Submit("var a = 1;");
            var r2 = s2.Submit("var b = a + 1;");
            Check("§23 ② 跨提交引用会话变量的 var 推断为 int（不是 object）",
                r2.Success && r2.GeneratedSource.Contains("public static int b;"),
                r2.Diagnostics + " | " + r2.GeneratedSource);
            Check("§23 ② 行为面：`b + 40` = 42（a=1 ⇒ b=2；旧码 b 是 object ⇒ CS0019 ⇒ 本行必 FAIL）",
                s2.Submit("b + 40").Success && s2.LastValue?.ToString() == "42", "value=" + s2.LastValue);

            // ③ 对照组（游戏内 count=4 的那一组）：全限定写法旧码本来就正常 ⇒ 修复不得把它弄坏
            var s3 = NewSession(refs);
            var r3 = s3.Submit("var sb3 = new System.Text.StringBuilder();");
            Check("§23 ③ 对照组：全限定 var 仍推断为 StringBuilder",
                r3.Success && r3.GeneratedSource.Contains("public static System.Text.StringBuilder sb3;"),
                r3.Diagnostics);

            // ④ **同一次提交内**的兄弟变量（粘贴多行是常见用法）：新探针**不再复用原 tree**，故必须显式带上
            //    本次提交已生成的成员（`pendingMembers`），否则 `q` 在探针里解析不到 ⇒ 退回 object。
            //    ⚠️ 判别力（实测判定，**勿再写反**）：本条**对旧码无判别力** —— 旧探针把整段用户输入当**一棵 tree**
            //    编译，全局语句里的局部 `p` 对它本来就可见（HEAD 实测 PASS）。它的判别对象是"新探针但漏传
            //    `pendingMembers`"这一实现（去掉该参数 ⇒ `public static object q;` ⇒ 行为面 `q + 40` CS0019 ⇒ RED）。
            var s4 = NewSession(refs);
            var r4 = s4.Submit("var p = 1; var q = p + 1;");
            Check("§23 ④ 同提交内兄弟变量可见（q 推断为 int；防新探针漏传 pendingMembers，非旧码判别）",
                r4.Success && r4.GeneratedSource.Contains("public static int q;"),
                r4.Diagnostics + " | " + r4.GeneratedSource);
            Check("§23 ④ 行为面：`q + 40` = 42（p=1 ⇒ q=2；漏传 pendingMembers 的新探针 ⇒ object ⇒ CS0019 ⇒ RED）",
                s4.Submit("q + 40").Success && s4.LastValue?.ToString() == "42", "value=" + s4.LastValue);

            // ⑤ 会话里**自定义类型**的 var（历次提交件进探针引用集；旧码看不到 Submission_N ⇒ object）
            var s5 = NewSession(refs);
            s5.Submit("public class Vec23 { public int X; public Vec23(int x) { X = x; } }");
            var r5 = s5.Submit("var v = new Vec23(2);");
            Check("§23 ⑤ 用户自定义类型可推断（历次提交件进了探针引用集）",
                r5.Success && r5.GeneratedSource.Contains("Vec23 v;")
                && !r5.GeneratedSource.Contains("public static object v;"),
                r5.Diagnostics + " | " + r5.GeneratedSource);
            Check("§23 ⑤ 行为面：`v.X` = 2",
                s5.Submit("v.X").Success && s5.LastValue?.ToString() == "2", "value=" + s5.LastValue);

            // ⑥ 值通道不回归（§21 既有断言）
            var s6 = NewSession(refs);
            s6.Submit("var x = 1;");
            var r6 = s6.Submit("x + 41");
            Check("§23 ⑥ 值通道不回归：`x + 41` = 42",
                r6.Success && s6.LastValue?.ToString() == "42", "value=" + s6.LastValue);

            // ⑦ 匿名类型仍按 object 提升 + 提示（§17 既有语义不回归：匿名类型无法跨提交保留）
            var s7 = NewSession(refs);
            var r7 = s7.Submit("var anon = new { A = 1 };");
            Check("§23 ⑦ 匿名类型仍按 object 提升（不回归）",
                r7.Success && r7.GeneratedSource.Contains("public static object anon;"),
                r7.Diagnostics + " | " + r7.GeneratedSource);

            // ⑧ 同提交内**后置**的类型声明（粘贴顺序：先用法、后定义）：正式提交里成员查找与声明顺序无关
            //    （类型与语句同属生成类的类体），故探针必须看到**全部**成员 —— 只看"已处理成员"会让这里的
            //    `new Vec23b(2)` 解析失败 ⇒ 静默退回 object（把 §23 修的盲区换个位置复活）。
            //    判别力（实测判定）：**旧码 PASS**（旧探针编译整棵原 tree，顶层 class 就在同一棵树里）；
            //    判别对象 = "新探针只带已处理成员"的实现（半成品实测 FAIL）⇒ 属**防新机制倒退**的护栏。
            var s9 = NewSession(refs);
            var r9 = s9.Submit("var v9 = new Vec23b(2); class Vec23b { public int X; public Vec23b(int x) { X = x; } }");
            Check("§23 ⑧ 同提交内后置类型声明可见（v9 推断为 Vec23b，非 object）",
                r9.Success && r9.GeneratedSource.Contains("Vec23b v9;")
                && !r9.GeneratedSource.Contains("public static object v9;")
                // 探针类名泄漏的专用判据：探针若叫 `__CompletionProbe_xxx`，嵌套类型显示名会带该前缀 ⇒
                // 字段类型写成 `__CompletionProbe_xxx.Vec23b` ⇒ CS0246（半成品实测踩到过，见报告 §23.2）
                && !r9.GeneratedSource.Contains("__CompletionProbe_"),
                r9.Diagnostics + " | " + r9.GeneratedSource);
            Check("§23 ⑧ 行为面：`v9.X` = 2",
                s9.Submit("v9.X").Success && s9.LastValue?.ToString() == "2", "value=" + s9.LastValue);

            // ⑨ 第 1 个提交的探针必须继承**与该提交相同的基类**（生产 = 宿主助手基类 `ReplHostBase`，
            //    §19 (i) 宿主自引用保证它在引用集里）——否则助手成员按简单名对探针不可见 ⇒
            //    `var h = GetVars();` / 本例的 `var h = Hosted;` 静默退回 object（同一缺陷类的第三个盲区）。
            //    离线替身 `VarProbeHostBase23`（测试程序集里的 public 类 + 一个 public static 字段）=
            //    宿主自引用的离线对应物：把测试程序集自身显式放进引用集（形态上与生产 `self=` 同构）。
            //    判别力（实测判定）：**旧码 FAIL**（旧探针没有基类，`Hosted` 解析不到 ⇒ object）⇒ 本条是
            //    "与本次提交同可见性"这一契约的真判别断言。
            var refs9 = ReferenceSetForThisProcess();
            string selfLoc = typeof(Program).Assembly.Location;
            bool selfInRefs = false;
            for (int i = 0; i < refs9.Count; i++)
                if (string.Equals(refs9[i], selfLoc, StringComparison.OrdinalIgnoreCase)) { selfInRefs = true; break; }
            if (!selfInRefs) refs9.Add(selfLoc);
            var s10 = new ReplSession(refs9, "RoslynBridgeTests.VarProbeHostBase23");
            var r10 = s10.Submit("var h = Hosted;");
            Check("§23 ⑨ 第 1 提交的探针继承**与该提交相同的基类**（基类成员可见 ⇒ h 推断为 string）",
                r10.Success && r10.GeneratedSource.Contains("public static string h;"),
                "selfInRefs=" + selfInRefs + " | " + r10.Diagnostics + " | " + r10.GeneratedSource);
            Check("§23 ⑨ 行为面：`h + \"!\"` = h!（此式对 object 也成立 ⇒ 仅作行为面记录，判别力由上一行承担）",
                s10.Submit("h + \"!\"").Success && s10.LastValue?.ToString() == "h!", "value=" + s10.LastValue);

            // 开销证据：var 提交（多一次**只建语义模型**的探针）vs 纯值提交
            var s8 = NewSession(refs);
            s8.AddUsings(new[] { "System.Text" }, out diag);
            s8.Submit("1 + 1");                       // 热身（避免把冷启动算进来）
            var sw = System.Diagnostics.Stopwatch.StartNew();
            s8.Submit("var zz = new StringBuilder();");
            sw.Stop();
            long varMs = sw.ElapsedMilliseconds;
            sw.Restart();
            s8.Submit("1 + 1");
            sw.Stop();
            Console.WriteLine("VARINFERCOST var_ms=" + varMs + " value_ms=" + sw.ElapsedMilliseconds);

            // 探针本身的单次开销（只建 compilation + 绑定表达式：不 Emit、不 Load、不执行）——
            // 用来支撑"不另建第二套提交编译、也不复用提交 compilation"的机制选择（报告 §23.3）。
            var swp = System.Diagnostics.Stopwatch.StartNew();
            int probeCalls = 20;
            for (int i = 0; i < probeCalls; i++)
                s8.ResolveExpressionSymbol("new System.Text.StringBuilder()");
            swp.Stop();
            Console.WriteLine("PROBECOST calls=" + probeCalls + " total_ms=" + swp.ElapsedMilliseconds
                + " avg_us=" + (swp.ElapsedTicks * 1000000L / System.Diagnostics.Stopwatch.Frequency / probeCalls));

            // 拆开探针开销的**主要来源**：引用集**重建**（`CreateFromFile` 读元数据）× 同一批路径
            var swBuild = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < probeCalls; i++)
            {
                var tmp = new System.Collections.Generic.List<Microsoft.CodeAnalysis.MetadataReference>();
                foreach (string p in refs)
                {
                    try { tmp.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)); } catch { }
                }
            }
            swBuild.Stop();
            Console.WriteLine("PROBEREFS_BUILD calls=" + probeCalls + " total_ms=" + swBuild.ElapsedMilliseconds
                + " paths=" + refs.Count);

            // 共享引用集时的探针开销（`Submit` 的实际形态：同一次提交内只组装一次、多次 `var` 复用）
            var sharedRefs = new System.Collections.Generic.List<Microsoft.CodeAnalysis.MetadataReference>();
            foreach (string p in refs)
            {
                try { sharedRefs.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p)); } catch { }
            }
            var swShared = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < probeCalls; i++)
                s8.ResolveExpressionSymbol("new System.Text.StringBuilder()", null, null, sharedRefs);
            swShared.Stop();
            Console.WriteLine("PROBECOST_SHARED calls=" + probeCalls + " total_ms=" + swShared.ElapsedMilliseconds
                + " avg_us=" + (swShared.ElapsedTicks * 1000000L / System.Diagnostics.Stopwatch.Frequency / probeCalls));
        }

        static int CompletionsOf(ReplSession session, string input)
        {
            string prefix;
            return RoslynCompletion.Complete(input, session, out prefix).Length;
        }

        /// <summary>
        /// 复审收尾轮 R2 / R3 / R4③：
        /// ① 补全入口**结构上绝不抛**（异常 ⇒ 空候选 + 一条上报，**不静默**）；
        /// ② 命名空间缓存的失效钩子真的重建；
        /// ③ 引用读不出元数据**不静默**（报一条、只报一次）。
        /// </summary>
        static void TestCompletionRobustness()
        {
            var refs = ReferenceSetForThisProcess();
            var reported = new System.Collections.Generic.List<string>();
            RoslynCompletion.ReportDiagnostic = m => reported.Add(m);   // 测试用上报探针

            // ① 正常路径：不得误报（同时证"零引用失败"）
            using (var ev = NewEvaluator(refs))
            {
                string prefixOk;
                string[] ok = ev.GetCompletions("System.Tex", out prefixOk);
                Check("§22 R2 正常补全不误报降级日志", ok.Length > 0 && reported.Count == 0,
                    JoinList(ok) + " reported=" + reported.Count);
            }

            // ② 结构兜底：null 会话**故意**踩异常路径 ⇒ 空候选 + 一条上报，**不抛**
            // 判别力：旧码（入口无 try/catch）此处 NRE 直接逃出 ⇒ 用例以异常终止（RED）。
            string prefixNull;
            string[] nullResult = RoslynCompletion.Complete("sb.App", null, out prefixNull);
            Check("§22 R2 null 会话 ⇒ 空候选而非抛出（结构保证可被验证）",
                nullResult != null && nullResult.Length == 0, "prefix=" + prefixNull);
            Check("§22 R2 异常降级为**一条上报**（不静默吞）",
                reported.Count == 1 && !string.IsNullOrEmpty(reported[0]),
                "count=" + reported.Count + " msg=" + (reported.Count > 0 ? reported[0] : "<none>"));

            // ③ R3：失效钩子（任务 13 自愈补引用后**必须**调用）—— 判定"真的重建"
            var ses = NewSession(refs);
            System.Collections.Generic.IEnumerable<string> first = ses.AllNamespaces();
            int n1 = CountNamespaces(first);
            int folded = ses.NamespaceReferenceCount;
            ses.InvalidateNamespaceCache();
            System.Collections.Generic.IEnumerable<string> second = ses.AllNamespaces();
            Check("§22 R3 失效后**重建新表**（同会话、不同对象、内容一致）",
                !ReferenceEquals(first, second) && CountNamespaces(second) == n1 && n1 > 0,
                "n1=" + n1 + " same_object=" + ReferenceEquals(first, second));
            Check("§22 R3 无提交件时折进表的引用数 = 外部引用数",
                folded == refs.Count && ses.NamespaceReferenceFailureCount == 0,
                "folded=" + folded + " refs=" + refs.Count + " failures=" + ses.NamespaceReferenceFailureCount);

            // ④ R4③：打不开的引用不静默（报一条、只报一次）
            var badRefs = ReferenceSetForThisProcess();
            badRefs.Add(@"Z:\__no_such_dir__\Nope.dll");
            using (var evBad = NewEvaluator(badRefs))
            {
                int before = reported.Count;
                string p1, p2;
                evBad.GetCompletions("System.Tex", out p1);
                int afterFirst = reported.Count;
                evBad.GetCompletions("System.Tex", out p2);
                // 判别力：旧码（空 catch 静默跳过）⇒ delta 恒 0 ⇒ 本行必 FAIL。
                Check("§22 R4③ 引用读不出元数据 ⇒ 上报一条（不静默跳过）",
                    afterFirst == before + 1, "delta=" + (afterFirst - before));
                Check("§22 R4③ 只报一次（不每次按键刷屏）",
                    reported.Count == afterFirst, "delta2=" + (reported.Count - afterFirst));
            }

            RoslynCompletion.ReportDiagnostic = null;
        }

        /// <summary>
        /// 复审 R4④：命名空间源**包含会话提交件**（`imageRefs`）。可观测性说明：当前 `Submit` **拒绝**
        /// `namespace` 声明（"不支持的顶层成员形态"）⇒ 提交件只贡献**全局命名空间**（空名，不入表）⇒
        /// 行为上无增量，故判据用**结构断言**：折进表的引用数 = 外部引用数 + 提交件数。
        /// 另附**类型转发探针**（R4②）。
        /// </summary>
        static void TestNamespaceSourceIncludesSubmissions()
        {
            var refs = ReferenceSetForThisProcess();
            var plain = NewSession(refs);
            int plainCount = CountNamespaces(plain.AllNamespaces());
            int plainFolded = plain.NamespaceReferenceCount;

            var withSub = NewSession(refs);
            var r1 = withSub.Submit("public class ImgNsProbe { }");
            Check("§22 R4④ 前置：提交一个类", r1.Success, r1.Diagnostics);
            int subCount = CountNamespaces(withSub.AllNamespaces());

            Check("§22 R4④ 提交件折进命名空间源（引用数 = 外部 " + refs.Count + " + 提交件 1）",
                withSub.NamespaceReferenceCount == refs.Count + 1
                && withSub.NamespaceReferenceFailureCount == 0,
                "folded=" + withSub.NamespaceReferenceCount + " plain_folded=" + plainFolded);
            Console.WriteLine("NSIMAGES external=" + refs.Count + " plain_folded=" + plainFolded
                + " sub_folded=" + withSub.NamespaceReferenceCount
                + " plain_ns=" + plainCount + " sub_ns=" + subCount);

            TestForwardedNamespaceProbe();
        }

        /// <summary>
        /// R4② 探针：`[assembly: TypeForwardedTo(typeof(System.Text.StringBuilder))]` 编出的 dll **不含类型定义**、
        /// 只有 ExportedType 行；拿它单独做命名空间源 ⇒ 若 `System.Text` 在表里，说明 Roslyn 已把转发命名空间归类
        /// （netstandard/mono facade 的缺口**不存在**，无需额外实现）；否则记为已知限制（报告 §22.9）。
        /// </summary>
        static void TestForwardedNamespaceProbe()
        {
            string mscorlib = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == "mscorlib") { mscorlib = a.Location; break; } } catch { }
            }
            if (mscorlib == null) { Check("§22 R4② 转发探针前置：找到 mscorlib", false, null); return; }

            var fwd = new System.Collections.Generic.List<string>();
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(System.Text.StringBuilder))]");
            var comp = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("fwdprobe",
                new[] { tree },
                new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(mscorlib) },
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                    Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            using (var ms = new System.IO.MemoryStream())
            {
                var emit = comp.Emit(ms);
                if (!emit.Success)
                {
                    Check("§22 R4② 转发探针前置：纯转发程序集可发射", false,
                        emit.Diagnostics.Length.ToString());
                    return;
                }
                var imgRef = Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(ms.ToArray());
                var scan = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("fwdscan", null, new[] { imgRef });
                var asm = scan.GetAssemblyOrModuleSymbol(imgRef) as Microsoft.CodeAnalysis.IAssemblySymbol;
                if (asm == null) { Check("§22 R4② 转发探针前置：可读转发程序集符号", false, null); return; }
                var stack = new System.Collections.Generic.Stack<Microsoft.CodeAnalysis.INamespaceSymbol>();
                stack.Push(asm.GlobalNamespace);
                while (stack.Count > 0)
                {
                    var ns = stack.Pop();
                    foreach (var child in ns.GetNamespaceMembers())
                    {
                        fwd.Add(child.ToDisplayString());
                        stack.Push(child);
                    }
                }
            }
            Console.WriteLine("FWDNS forwarded_namespaces=[" + string.Join(",", fwd.ToArray()) + "]");
            Check("§22 R4② 转发命名空间已归类（ExportedType 不缺 ⇒ facade 无需额外实现）",
                HasName(fwd.ToArray(), "System.Text"), JoinList(fwd.ToArray()));
        }

        /// <summary>
        /// 复审 R1：UI 合并点去重。`CSAutoCompleter` 依赖 UniverseLib/UE（`InputFieldRef`/`AutoCompleteModal`/
        /// `SignatureHighlighter`）⇒ 不可 link 进测试工程（同 `KeywordLexer` 的处理），故用**源码级断言**：
        /// ① 三来源都走 `AddSuggestion`（无绕过去重的直接 `suggestions.Add` 残留）
        /// ② 折叠数有观测点（`LastFoldedDuplicates++`，**不静默折叠**）
        /// ③ 折叠判据 = 插入文本（`seenCompletions.Add(value)`）
        /// ④ 调用点兜底（求值器来源异常降级）
        /// 游戏内面板的视觉确认（`System.Tex` 不再出现两行 `t`）并入任务 12（报告 §22.9）。
        /// </summary>
        static void TestCompletionUiDedupSource()
        {
            string path = FindFileUpwards("CSAutoCompleter.cs");
            if (path == null) { Check("§22 R1 CSAutoCompleter.cs 可定位", false, null); return; }
            string text = System.IO.File.ReadAllText(path);
            int viaHelper = CountOccurrences(text, "AddSuggestion(");
            int directAdd = CountOccurrences(text, "suggestions.Add(");
            Check("§22 R1 三来源都经去重入口 AddSuggestion（1 定义 + 3 调用）",
                viaHelper == 4, "found=" + viaHelper);
            Check("§22 R1 无绕过去重的直接 suggestions.Add（仅 AddSuggestion 内部一处）",
                directAdd == 1, "found=" + directAdd);
            Check("§22 R1 折叠判据 = 插入文本、折叠数有观测点（不静默折叠）",
                text.Contains("seenCompletions.Add(value)") && text.Contains("LastFoldedDuplicates++"));
            Check("§22 R1 调用点兜底：求值器来源异常降级为日志",
                text.Contains("求值器补全异常"));
        }

        static int CountOccurrences(string text, string needle)
        {
            int n = 0, at = 0;
            while (true)
            {
                at = text.IndexOf(needle, at, StringComparison.Ordinal);
                if (at < 0) return n;
                n++;
                at += needle.Length;
            }
        }

        static int CountNamespaces(System.Collections.Generic.IEnumerable<string> namespaces)
        {
            int n = 0;
            foreach (string ns in namespaces) n++;
            return n;
        }

        /// <summary>
        /// 命名空间源的两条硬要求：① 覆盖**未加载**程序集的命名空间 ② **不加载程序集**（只读元数据）。
        /// 装置：往引用集里显式加一条本进程**从未加载**过的 net35 `UnityEngine.dll` 路径（该 dll 在仓库
        /// `lib/net35` 里就有，测试工程不引用它），断言它的命名空间进表、而它仍在进程外。
        /// 判别力：若实现走 `Assembly.Load*`/`ReflectionUtility.AllNamespaces`（反射=必须加载）而非 Roslyn
        /// 元数据，"UnityEngine 仍在进程外"必 FAIL。
        /// </summary>
        static void TestNamespaceSourceWithoutLoading()
        {
            string libNet35 = FindUpwards(@"UE-4.9.0-official\UnityExplorer-4.9.0\lib\net35");
            string ueDll = libNet35 == null ? null : System.IO.Path.Combine(libNet35, "UnityEngine.dll");
            if (ueDll == null || !System.IO.File.Exists(ueDll))
            {
                Check("§22 命名空间源前置：仓库内可定位 lib/net35/UnityEngine.dll", false, ueDll);
                return;
            }

            int loadedBefore = CountLoadedAssemblies("UnityEngine");
            var refs = ReferenceSetForThisProcess();   // 只含"已加载"⇒ 里面没有 UnityEngine
            refs.Add(ueDll);                           // 显式引用一个从未加载的 dll 路径
            var s = NewSession(refs);

            bool hasUnityEngine = false;
            int total = 0;
            foreach (string ns in s.AllNamespaces())
            {
                total++;
                if (ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal))
                    hasUnityEngine = true;
            }
            int loadedAfter = CountLoadedAssemblies("UnityEngine");

            Check("§22 命名空间源覆盖未加载 dll 的命名空间（UnityEngine.*）", hasUnityEngine,
                "refs=" + refs.Count + " ns=" + total);
            Check("§22 命名空间源只用元数据、不加载程序集（UnityEngine 仍在进程外）",
                loadedBefore == 0 && loadedAfter == 0, "before=" + loadedBefore + " after=" + loadedAfter);
            Check("§22 命名空间源非空（含 System.*）", total > 0, "ns=" + total);
        }

        /// <summary>
        /// 关键字表补齐（C# 8+）：**源码文本断言**。
        /// 为什么不是 `KeywordLexer.keywords.Contains(...)`（简报写法）：`KeywordLexer : Lexer`，而
        /// `Lexer.HighlightColor` 是 UnityEngine `Color`、`TryMatchCurrent(LexerBuilder)` 又拖进 LexerBuilder
        /// ⇒ link 进测试工程会连带 UnityEngine/UE 依赖（§20 对 `ReplHostBase` 的同类裁定）。改为读**真源码**的
        /// `keywords` 初始化块、逐 token 断言：判别力与"表里到底有没有这个词"等价（同一文件即编译输入）。
        /// </summary>
        static void TestKeywordTableSource()
        {
            string path = FindFileUpwards("KeywordLexer.cs");
            if (path == null)
            {
                Check("§22 关键字表源码可定位（KeywordLexer.cs）", false, "未找到 KeywordLexer.cs");
                return;
            }
            string text = System.IO.File.ReadAllText(path);
            int at = text.IndexOf("keywords", StringComparison.Ordinal);
            int open = at < 0 ? -1 : text.IndexOf('{', at);
            int close = open < 0 ? -1 : text.IndexOf('}', open);
            if (open < 0 || close <= open)
            {
                Check("§22 关键字表初始化块可解析", false, path);
                return;
            }
            string block = text.Substring(open + 1, close - open - 1);
            var tokens = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            var rx = new System.Text.RegularExpressions.Regex("\"([A-Za-z_][A-Za-z0-9_]*)\"");
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(block))
                tokens.Add(m.Groups[1].Value);

            // 判别力：本轮之前 `required`/`file`/`scoped` 三项**不在表里** ⇒ 本行必 FAIL。
            string[] wanted = { "record", "init", "required", "with", "file", "scoped",
                "unmanaged", "global", "not", "and", "or" };
            var missing = new System.Collections.Generic.List<string>();
            foreach (string kw in wanted)
                if (!tokens.Contains(kw)) missing.Add(kw);
            Check("§22 关键字表含 C# 8+ 关键字（表内 " + tokens.Count + " 个 token）",
                missing.Count == 0, "缺：" + string.Join(",", missing.ToArray()));
            Check("§22 关键字表含 required/file/scoped（本轮新增三项）",
                tokens.Contains("required") && tokens.Contains("file") && tokens.Contains("scoped"));
        }

        static bool AnyPrefix(string[] candidates, string prefix)
        {
            if (candidates == null) return false;
            foreach (string s in candidates)
                if (s != null && s.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        static bool HasName(string[] candidates, string name)
        {
            if (candidates == null) return false;
            foreach (string s in candidates)
                if (s == name) return true;
            return false;
        }

        static string JoinList(string[] items)
            => items == null ? "<null>" : string.Join(",", items);

        static bool SameList(string[] a, string[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        static int CountLoadedAssemblies(string simpleName)
        {
            int n = 0;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetName().Name == simpleName) n++; } catch { }
            }
            return n;
        }

        // ══════════════ 任务 10：HookInstance 的 Roslyn 补丁链路 ══════════════

        /// <summary>
        /// 任务 10：动态 Harmony 补丁（生产路径 `HookInstance.CompileAndGenerateProcessor`）的**离线**
        /// 端到端断言。走的就是生产代码本体（不是副本）：`DynamicPatch.Wrap` → 求值器提交编译 →
        /// `DynamicPatch.Locate` → `DynamicPatch.FindPatchMethods` → `HarmonyMethod` +
        /// `PatchProcessor.Patch()`（真把补丁挂到一个本进程方法上）。
        ///
        /// 判别力（三条都是明确必 FAIL 面）：
        ///  ① 简报步骤 1/3 的 `asm.GetType(typeName)`：REPL 把提交的顶层类型**嵌进**生成类
        ///     `Submission_&lt;tag&gt;_N`（见 `ReplSession.BuildSubmissionSource`）⇒ 产物里根本没有该简单名的
        ///     **顶层**类型 ⇒ 取不到。本用例把这条机制**正面钉死**（断言 GetType 返回 null）：REPL 若改成
        ///     "顶层类型原样进产物"，本行立刻变红，提示 Locate 的扫描策略需要复核。
        ///  ② 简报步骤 3 的 `BindingFlags.Static | BindingFlags.Public` 查找补丁方法：默认补丁源码是
        ///     `static void Postfix(...)`（**隐式 private**）⇒ 取不到 ⇒ 不挂补丁 ⇒ "补丁创建成功却永不生效"。
        ///  ③ 交付前 HEAD 码（HookInstance 仍是 mcs 反射版）：本用例依赖的
        ///     `RoslynScriptEvaluator.LastCompiledAssembly` / `DynamicPatch.*` 全都不存在 ⇒ 编译期 RED。
        /// </summary>
        static void TestHookCompile()
        {
            var refs = ReferenceSetForThisProcess();

            // 与生产 `GenerateDefaultPatchSourceCode` 同形：**无访问修饰符的 static 方法**（②的来源），
            // 参数用 Harmony 约定名 `__result`（ref 形参才能改返回值）。
            const string patchSource = "static void Postfix(ref int __result) { __result += 100; }";

            string typeName = DynamicPatch.NewTypeName();
            string code = DynamicPatch.Wrap(typeName, patchSource);

            using (var ev = NewEvaluator(refs))
            {
                string diag;
                Check("任务10 补丁源码编译成功", ev.Evaluate(code, out diag), diag);

                var asm = ev.LastCompiledAssembly;
                Check("任务10 拿到编译产物程序集", asm != null);

                // ① 钉死"产物里没有顶层类型"这条机制（简报写法失败的根因）
                Check("任务10 机制：产物里没有以补丁名命名的顶层类型（`asm.GetType(名字)` 必失败）",
                    asm != null && asm.GetType(typeName) == null,
                    "GetType(" + typeName + ") 意外命中 ⇒ REPL 产物形态已变，Locate 策略需复核");

                Type patchType = DynamicPatch.Locate(asm, typeName);
                Check("任务10 Locate 从产物里取回补丁类型（扫嵌套）", patchType != null);
                Check("任务10 补丁类型是生成提交类（Submission_*）的嵌套类",
                    patchType != null && patchType.IsNested && patchType.DeclaringType != null
                        && patchType.DeclaringType.Name.StartsWith("Submission_"),
                    patchType == null ? "<null>"
                        : (patchType.DeclaringType == null ? "<无声明类型>" : patchType.DeclaringType.Name));
                Console.WriteLine("HOOKCOMPILE type=" + typeName + " decl="
                    + (patchType == null || patchType.DeclaringType == null ? "<null>" : patchType.DeclaringType.FullName));

                System.Reflection.MethodInfo prefix, postfix, finalizer, transpiler;
                DynamicPatch.FindPatchMethods(patchType, out prefix, out postfix, out finalizer, out transpiler);
                // ② 判别力核心：隐式 private 的 static 方法必须被取到
                Check("任务10 取到 Postfix（隐式 private 也必须取到）", postfix != null);
                Check("任务10 判别力：该 Postfix 非 public（Public-only 查找必取不到）",
                    postfix != null && !postfix.IsPublic);
                // ②的正面实测 + R-b 对称两侧：钉住**简报写法**（Static|Public）与**生产写法**
                // （DynamicPatch.PatchMethodFlags = Public|NonPublic|Static）在同一形态（隐式 private 的
                // static Postfix）下的判别力 —— 用途是**防未来照简报回退**，**不是**历史缺陷取证：
                // 旧 mcs 路径本就用含 NonPublic 的 `ReflectionUtility.FLAGS`，报告 §24.2-② 已勘误（§25 R-a）。
                // 若哪天 `GenerateDefaultPatchSourceCode` 改成发 `public static void Postfix`，本行变红
                // ⇒ 提醒"Public-only 变体此刻也会成立"，别把它当成永久安全。
                Check("任务10 判别力：简报写法（Static|Public）取不到隐式 private 的 Postfix（钉住简报写法、防照简报回退）",
                    patchType != null && patchType.GetMethod("Postfix",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public) == null);
                // R-b 正向断言（与上一条构成对称两侧）：生产 flags 必须**能**取到隐式 private 的 Postfix。
                Check("任务10 对称正向：PatchMethodFlags（Public|NonPublic|Static）能取到隐式 private 的 Postfix",
                    patchType != null && patchType.GetMethod("Postfix", DynamicPatch.PatchMethodFlags) != null);
                Check("任务10 未定义的方法返回 null（Prefix/Finalizer/Transpiler）",
                    prefix == null && finalizer == null && transpiler == null);

                // ── R-e①：生产编排纯判定 FailureReason（三类失败 + 合法一例；覆盖 HookInstance 的调用面）──
                System.Reflection.MethodInfo none = null;
                Check("R-e FailureReason：产物程序集 null ⇒ 非 null（必须失败）",
                    DynamicPatch.FailureReason(null, null, none, none, none, none) != null);
                Check("R-e FailureReason：产物里找不到补丁类型（class null）⇒ 非 null（必须失败）",
                    DynamicPatch.FailureReason(asm, null, none, none, none, none) != null);
                Check("R-e FailureReason：四个补丁方法全 null ⇒ 非 null（R-d① 响亮失败的离线证明）",
                    DynamicPatch.FailureReason(asm, patchType, none, none, none, none) != null);
                Check("R-e FailureReason：合法 postfix ⇒ null（可继续注册补丁方法）",
                    DynamicPatch.FailureReason(asm, patchType, prefix, postfix, finalizer, transpiler) == null);

                // ── R-e②：两 hook 连发不串 —— 一次提交里放两个补丁类，按名 Locate 各取各的 ──
                const string twoHooksSource =
                    "public static class DynamicPatch_T10A { static void Postfix(ref int __result) { __result += 1; } }" + "\n"
                    + "public static class DynamicPatch_T10B { static void Postfix(ref int __result) { __result += 2; } }";
                string diagA;
                Check("R-e② 一次提交里两个补丁类编译成功", ev.Evaluate(twoHooksSource, out diagA), diagA);
                var asm2 = ev.LastCompiledAssembly;
                Type typeA = DynamicPatch.Locate(asm2, "DynamicPatch_T10A");
                Type typeB = DynamicPatch.Locate(asm2, "DynamicPatch_T10B");
                Check("R-e② Locate 各自取回（A、B 均非 null 且 ReferenceEquals 区分，不串包）",
                    typeA != null && typeB != null && !object.ReferenceEquals(typeA, typeB),
                    "A=" + typeA + " B=" + typeB);
                System.Reflection.MethodInfo preA, postA, finA, traA;
                DynamicPatch.FindPatchMethods(typeA, out preA, out postA, out finA, out traA);
                System.Reflection.MethodInfo preB, postB, finB, traB;
                DynamicPatch.FindPatchMethods(typeB, out preB, out postB, out finB, out traB);
                Check("R-e② FindPatchMethods 取到的是各自那个类型的方法（DeclaringType 分别为 A/B）",
                    postA != null && postB != null
                        && object.ReferenceEquals(postA.DeclaringType, typeA)
                        && object.ReferenceEquals(postB.DeclaringType, typeB)
                        && !object.ReferenceEquals(postA, postB),
                    "postA=" + postA + " postB=" + postB);

                // ── R-e③：非 static Postfix ⇒ FindPatchMethods 取不到 ⇒ FailureReason 非 null（响亮失败离线证明）──
                const string nonStaticSource =
                    "public class DynamicPatch_T10NS { void Postfix(ref int __result) { __result += 1; } }";
                string diagNS;
                Check("R-e③ 非 static 补丁类编译成功", ev.Evaluate(nonStaticSource, out diagNS), diagNS);
                var asm3 = ev.LastCompiledAssembly;
                Type typeNS = DynamicPatch.Locate(asm3, "DynamicPatch_T10NS");
                System.Reflection.MethodInfo preNS, postNS, finNS, traNS;
                DynamicPatch.FindPatchMethods(typeNS, out preNS, out postNS, out finNS, out traNS);
                Check("R-e③ FindPatchMethods 取不到非 static 的 Postfix（四方法全 null）",
                    typeNS != null && preNS == null && postNS == null && finNS == null && traNS == null,
                    "postNS=" + postNS);
                Check("R-e③ FailureReason 判必须失败（非 null ⇒ 生产路径响亮 return false）",
                    DynamicPatch.FailureReason(asm3, typeNS, preNS, postNS, finNS, traNS) != null);

                // ③ Harmony 链路端到端：真的挂上去、真的改变行为
                int before = HarmonyProbeTarget(1);
                Check("任务10 基线：探针方法未加补丁时返回 2", before == 2, "before=" + before);

                if (postfix == null)
                {
                    Check("任务10 判别力：补丁真的生效（Harmony 链路端到端）", false,
                        "Postfix 未取到 ⇒ 挂不上补丁（这正是 Public-only 查找的后果）");
                }
                else
                {
                    var harmony = new HarmonyLib.Harmony("UESDdebuger.RoslynBridgeTests.task10");
                    var processor = harmony.CreateProcessor(HarmonyProbeTargetMethod());
                    processor.AddPostfix(new HarmonyLib.HarmonyMethod(postfix));
                    processor.Patch();
                    try
                    {
                        int after = HarmonyProbeTarget(1);
                        Check("任务10 判别力：补丁真的生效（Harmony 链路端到端）", after == 102, "after=" + after);
                    }
                    finally
                    {
                        processor.Unpatch(postfix);
                    }
                    Check("任务10 补丁可卸下（恢复原行为）", HarmonyProbeTarget(1) == 2);
                }
            }
        }

        /// <summary>被补丁的探针目标（`NoInlining`：否则 JIT 内联会让 detour 观察不到）。</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int HarmonyProbeTarget(int a) => a + 1;

        static System.Reflection.MethodInfo HarmonyProbeTargetMethod()
            => typeof(Program).GetMethod("HarmonyProbeTarget",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);

        // ════════════ 任务 10 修复轮（报告 §25 R-f）：Reset/Dispose 活 hook 判据 ════════════

        /// <summary>
        /// R-f① 判据离线断言：生产侧 `ConsoleController.ResetConsoleCore` 在 `Evaluator?.Dispose()` 前调
        /// `DynamicPatch.ActiveHookResetWarning(HookList.currentHooks, …)`。HookList 是 Unity UI 类型、
        /// 进不了测试工程 ⇒ 用**同型容器**（OrderedDictionary：签名 → hook，与 HookList.currentHooks 同型）
        /// 走**生产代码本体**遍历：计数 == 0 ⇒ null（不打扰日志）；计数 &gt; 0 ⇒ 响亮警告（列 hook 数 + 目标方法）。
        /// 「计数 &gt; 0 才产出警告」= "路径真执行"的离线判据（ConsoleController 的接线属产物面，无法离线断言）。
        /// </summary>
        static void TestActiveHookResetWarning()
        {
            var hooks = new System.Collections.Specialized.OrderedDictionary();

            Check("R-f 判据：HookList 为空 ⇒ 返回 null（Reset 不打扰日志）",
                DynamicPatch.ActiveHookResetWarning(hooks, DescribeHookForResetTest) == null);
            Check("R-f 判据：HookList 为 null ⇒ 返回 null（防御）",
                DynamicPatch.ActiveHookResetWarning(null, DescribeHookForResetTest) == null);

            hooks.Add("Sig.A", new FakeHookForResetTest("Verse.GenTicks.get_TicksGame", true));
            hooks.Add("Sig.B", new FakeHookForResetTest("Verse.GenTicks.TicksGame", false));

            string warn = DynamicPatch.ActiveHookResetWarning(hooks, DescribeHookForResetTest);
            Check("R-f 判据：遍历 HookList 计数 > 0 ⇒ 产出响亮警告（含 hook 数 2）",
                warn != null && warn.Contains("2 个 hook"), warn ?? "<null>");
            Check("R-f 判据：警告逐条列出各目标方法（A、B 都在，含 On/Off 态）",
                warn != null
                    && warn.Contains("[On] Verse.GenTicks.get_TicksGame()")
                    && warn.Contains("[Off] Verse.GenTicks.TicksGame()"),
                warn ?? "<null>");
        }

        /// <summary>R-f 描述器（测试侧）：与生产的 ConsoleController.DescribeHookTargetForResetLog 同形——
        /// **用方法不用 lambda**（C.5 闭包纪律）。</summary>
        static string DescribeHookForResetTest(object hook)
        {
            FakeHookForResetTest h = hook as FakeHookForResetTest;
            return h == null ? "<null>" : h.Describe();
        }

        // ══════════════ 任务 13（引用自愈）离线断言 ══════════════
        // ReportSink / ModPathsProvider 捕获：**方法组注入，不用 lambda**（C.5 闭包纪律）。
        static readonly System.Collections.Generic.List<string> s_healSink
            = new System.Collections.Generic.List<string>();
        static readonly System.Collections.Generic.List<string> s_modPathProbe
            = new System.Collections.Generic.List<string>();

        static void HealSinkCapture(string message) => s_healSink.Add(message ?? "");
        static System.Collections.Generic.List<string> ModPathProbeForHealing() => s_modPathProbe;
        static bool SinkSaw(string fragment)
        {
            foreach (string m in s_healSink)
                if (m != null && m.Contains(fragment)) return true;
            return false;
        }
        static string SinkText() => string.Join(" | ", s_healSink.ToArray());
        static string Head(string text, int max)
            => string.IsNullOrEmpty(text) ? "" : (text.Length <= max ? text : text.Substring(0, max));

        /// <summary>简报步骤 1（含 CS1069 断言 = 边界探针实证；CS0234 = TDD 首跑实测码；dotted 取全名 = 定稿设计）。</summary>
        static void TestMissingReferenceParser()
        {
            string cs0012 = "(1,106): error CS0012: The type 'Vector3' is defined in an assembly that is not referenced." +
                            " You must add a reference to assembly 'UnityEngine.CoreModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'.";
            Check("CS0012 → 取出程序集名", MissingReferenceParser.AssemblyFromCs0012(cs0012) == "UnityEngine.CoreModule",
                MissingReferenceParser.AssemblyFromCs0012(cs0012));
            Check("非 CS0012 返回 null", MissingReferenceParser.AssemblyFromCs0012("(1,1): error CS1002: ; expected") == null);

            string cs1069 = "(17,45): error CS1069: The type name 'VideoPlayer' could not be found in the namespace 'UnityEngine.Video'."
                + " This type has been forwarded to assembly 'UnityEngine.VideoModule, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'."
                + " Consider adding a reference to that assembly.";
            Check("CS1069 → 取出程序集名（边界探针实测形态）",
                MissingReferenceParser.AssemblyFromCs1069(cs1069) == "UnityEngine.VideoModule",
                MissingReferenceParser.AssemblyFromCs1069(cs1069));
            Check("非 CS1069 → null（不误认 CS0012/CS1002）",
                MissingReferenceParser.AssemblyFromCs1069(cs0012) == null
                && MissingReferenceParser.AssemblyFromCs1069("(1,1): error CS1002: ; expected") == null);

            string cs0103 = "(1,58): error CS0103: The name 'UnityEngine' does not exist in the current context";
            Check("CS0103 → 取出名字", MissingReferenceParser.SymbolFromCs0103(cs0103) == "UnityEngine",
                MissingReferenceParser.SymbolFromCs0103(cs0103));

            string cs0246 = "(1,43): error CS0246: The type or namespace name 'UnityEngine' could not be found";
            Check("CS0246 → 取出名字", MissingReferenceParser.SymbolFromCs0246(cs0246) == "UnityEngine",
                MissingReferenceParser.SymbolFromCs0246(cs0246));
            Check("CS0246 dotted 名取全名（不截首段：简报首段截断会误配 System.Data）",
                MissingReferenceParser.SymbolFromCs0246(
                    "(1,1): error CS0246: The type or namespace name 'System.Data.DataTable' could not be found")
                == "System.Data.DataTable");

            string cs0234 = "(1,30): error CS0234: The type or namespace name 'Immutable' does not exist in the namespace 'System.Collections' (are you missing an assembly reference?)";
            Check("CS0234 → 组合完整命名空间（TDD 首跑实测码）",
                MissingReferenceParser.SymbolFromCs0234(cs0234) == "System.Collections.Immutable",
                MissingReferenceParser.SymbolFromCs0234(cs0234));
            Check("非 CS0234 → null", MissingReferenceParser.SymbolFromCs0234(cs0246) == null);

            Check("不误报其它诊断", MissingReferenceParser.SymbolFromCs0103("(2,1): error CS0106: bad modifier") == null);

            string n1, v1, c1, k1;
            bool got1 = MissingReferenceParser.TryAssemblyIdentity(cs0012, out n1, out v1, out c1, out k1);
            Check("CS0012 → 完整身份四元组（§4.10）", got1 && n1 == "UnityEngine.CoreModule"
                && v1 == "0.0.0.0" && c1 == "neutral" && k1 == "null",
                got1 + "/" + n1 + "/" + v1 + "/" + c1 + "/" + k1);
            string n2, v2, c2, k2;
            bool got2 = MissingReferenceParser.TryAssemblyIdentity(cs1069, out n2, out v2, out c2, out k2);
            Check("CS1069 → 完整身份四元组", got2 && n2 == "UnityEngine.VideoModule" && v2 == "0.0.0.0",
                got2 + "/" + n2 + "/" + v2);
            Check("无身份诊断 → TryAssemblyIdentity=false",
                !MissingReferenceParser.TryAssemblyIdentity(cs0103, out n2, out v2, out c2, out k2));
        }

        /// <summary>简报步骤 1（诊断码以 TDD 首跑实测为准：HEALPROBE 行打印原文）。</summary>
        static void TestHealerByNamespace()
        {
            // net472 上的等价复现：只引 mscorlib，代码里写 System.Xml 的名字 → CS0234/CS0246，
            // 自愈器应在搜索目录里找到 System.Xml.dll 并补上，重试后编译成功。
            string mscorlib = typeof(object).Assembly.Location;
            string searchDir = System.IO.Path.GetDirectoryName(mscorlib);

            // TDD 首跑捕获：真实诊断原文（CS0234 vs CS0246 分支以此为准，证据留档报告 §26）。
            var probe = RoslynCompiler.Compile("HealProbe",
                "public class P { public static System.Xml.XmlDocument D; }", new[] { mscorlib }, true);
            Console.WriteLine("HEALPROBE " + probe.Diagnostics.Replace("\r", "").Replace("\n", " | "));

            var healer = new ReferenceHealer(new[] { searchDir });
            var refs = new System.Collections.Generic.List<string> { mscorlib };

            var r = RoslynCompiler.CompileWithHealing("HealTest",
                "public class P { public static System.Xml.XmlDocument D; }", refs, true, healer, out string healLog);

            Check("自愈后编译成功", r.Success, r.Diagnostics);
            Check("refs 里多了 System.Xml", refs.Exists(p => p.EndsWith("System.Xml.dll", StringComparison.OrdinalIgnoreCase)),
                string.Join(";", refs.ToArray()));
            Check("自愈日志记录了一行", healLog.Contains("System.Xml"), healLog);
            Check("第二段无需自愈即可编译", RoslynCompiler.CompileWithHealing("HealTest2",
                "public class P2 { public static System.Xml.XmlDocument D; }", refs, true, healer, out string log2).Success && log2 == "", log2);
        }

        /// <summary>定稿测试③④⑥：CS0433/CS1704 响亮拒绝 / 开关 OFF 排除集 / §4.10 身份降级结构化日志。</summary>
        static void TestHealerRefusalsAndSwitch()
        {
            string searchDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);

            // ③ CS0433/CS1704：重复定义不是缺引用 —— 再加引用只会更糟，必须拒绝且响亮
            var healer = new ReferenceHealer(new[] { searchDir });
            healer.ReportSink = HealSinkCapture;
            s_healSink.Clear();
            var r433 = healer.Resolve("(1,1): error CS0433: The type 'X' exists in both 'A.dll' and 'B.dll'",
                new System.Collections.Generic.List<string>());
            Check("③ CS0433 拒绝自愈", r433 == null, r433 ?? "<null>");
            Check("③ CS0433 拒绝有响亮日志", SinkSaw("CS0433"), SinkText());
            s_healSink.Clear();
            var r1704 = healer.Resolve("(1,1): error CS1704: Assembly 'A' already references 'B'",
                new System.Collections.Generic.List<string>());
            Check("③ CS1704 拒绝自愈 + 响亮日志", r1704 == null && SinkSaw("CS1704"), SinkText());

            // ⑥ §4.10：完整身份未命中 → 退简单名，降级日志五字段（名字/旧版本/新版本/采用/原因）
            s_healSink.Clear();
            var healer6 = new ReferenceHealer(new[] { searchDir });
            healer6.ReportSink = HealSinkCapture;
            string mscorlibPath = typeof(object).Assembly.Location;
            var r6 = healer6.Resolve(
                "(1,1): error CS0012: You must add a reference to assembly 'mscorlib, Version=1.2.3.4, Culture=neutral, PublicKeyToken=b77a5c561934e089'.",
                new System.Collections.Generic.List<string>());
            string downgraded = null;
            foreach (string m in s_healSink)
                if (m.Contains("降级")) downgraded = m;
            Check("⑥ 身份不匹配 → 退简单名命中已加载 mscorlib",
                r6 != null && r6.Equals(mscorlibPath, StringComparison.OrdinalIgnoreCase), r6 ?? "<null>");
            Check("⑥ 降级结构化日志五字段齐全",
                downgraded != null && downgraded.Contains("名字=mscorlib") && downgraded.Contains("旧版本=1.2.3.4")
                    && downgraded.Contains("新版本=4.0.0.0") && downgraded.Contains("采用=") && downgraded.Contains("原因="),
                downgraded ?? ("<无降级日志> " + SinkText()));

            // ④ 开关做实：OFF ⇒ 模组路径作排除集（失败日志注明开关）；ON ⇒ 仅诊断触发可解析
            s_modPathProbe.Clear();
            s_modPathProbe.Add(System.IO.Path.Combine(searchDir, "Microsoft.CSharp.dll"));
            // 该 dll 在测试进程里可能已从 GAC 加载（Location ≠ framework 文件路径）——两个路径都算
            // 模组路径，才与生产"provider 返回的就是已加载程序集 Location"同形（否则排除集匹配不上）。
            foreach (System.Reflection.Assembly ma in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (ma.GetName().Name == "Microsoft.CSharp" && !string.IsNullOrEmpty(ma.Location))
                        s_modPathProbe.Add(ma.Location);
                }
                catch { }
            }
            var healer4 = new ReferenceHealer(new[] { searchDir });
            healer4.ReportSink = HealSinkCapture;
            healer4.ModPathsProvider = ModPathProbeForHealing;
            healer4.AllowModTypeResolution = false;
            s_healSink.Clear();
            const string cs0234csharp = "(1,30): error CS0234: The type or namespace name 'CSharp' does not exist in the namespace 'Microsoft' (are you missing an assembly reference?)";
            var rOff = healer4.Resolve(cs0234csharp, new System.Collections.Generic.List<string>());
            Check("④ OFF：模组路径作排除集（解析 null）", rOff == null, rOff ?? "<null>");
            Check("④ OFF：失败日志注明开关", SinkSaw("Allow_Mod_Type_Resolution"), SinkText());
            healer4.AllowModTypeResolution = true;
            var rOn = healer4.Resolve(cs0234csharp, new System.Collections.Generic.List<string>());
            Check("④ ON：同一诊断可解析到 Microsoft.CSharp.dll",
                rOn != null && rOn.EndsWith("Microsoft.CSharp.dll", StringComparison.OrdinalIgnoreCase), rOn ?? "<null>");
        }

        /// <summary>定稿测试②：≥6 个缺引用（框架目录取材，不造临时文件）⇒ 5 轮到顶必须打文案、不静默截断。</summary>
        /// <summary>任务 13（微审①修复）：**无关引用必须回滚**——加入后诊断文本完全未变即判无效并撤回，
        /// 防引用集被"只碰巧定义该前缀命名空间"的 dll 永久污染（自愈循环原不校验改善）。驱动方式：
        /// 源码用 `System.Foo`（System 命名空间里不存在的成员）⇒ CS0234 'Foo' in 'System' ⇒
        /// 命名空间通道退到前缀 `System` 并命中 System.dll，但该引用**不能**消除 CS0234 ⇒ 必须回滚。</summary>
        static void TestHealRollbackOnNoEffect()
        {
            string searchDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
            var healer = new ReferenceHealer(new[] { searchDir });
            healer.ReportSink = HealSinkCapture;
            healer.AllowModTypeResolution = false;
            s_healSink.Clear();
            var refs = new System.Collections.Generic.List<string> { typeof(object).Assembly.Location };
            int before = refs.Count;
            string log;
            var rr = RoslynCompiler.CompileWithHealing("HealRollbackProbe",
                "public class HRollback { public static void M() { System.Foo x; } }", refs, false, healer, out log);
            Check("⑦ 无关自愈被回滚（日志含『自愈回滚』）", log.Contains("自愈回滚"), log);
            Check("⑦ 回滚后引用集未增长", refs.Count == before, "before=" + before + " after=" + refs.Count);
            Check("⑦ 回滚后仍未成功（不得假装修好）", !rr.Success, rr.Success ? "意外成功" : "失败（预期）");
        }

        /// <summary>任务 13 §4.10-2（微审③指出的唯一缺口）：同简单名不同身份的去重**不得静默**。
        /// 用"框架目录 System.dll"与"进程内已加载 System 的 Location"（不同路径、同简单名）驱动：
        /// 断言先到者进入、后者被拒、且**有**结构化日志（保留/丢弃/身份/原因）；同路径重复加入不报。</summary>
        static void TestReferenceSetDedupeLog()
        {
            string fw = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
            string a = System.IO.Path.Combine(fw, "System.dll");
            string b = null;
            foreach (System.Reflection.Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetName().Name == "System") { b = asm.Location; break; } } catch { }
            }
            bool pre = System.IO.File.Exists(a) && !string.IsNullOrEmpty(b) && System.IO.File.Exists(b)
                && !string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
            Check("⑩ 前置：同简单名两个不同路径文件都在", pre, a + " | " + (b ?? "<null>"));
            if (!pre) return;
            var set = new ReferenceSet();
            var log = new System.Collections.Generic.List<string>();
            set.ReportSink = log.Add;                        // 方法组（无 lambda 捕获）
            bool first = set.TryAdd(a, "System", null);
            bool second = set.TryAdd(b, "System", null);
            Check("⑩ 先到者进入、后者被拒（单一编译内不可同简单名两版本）",
                first && !second, "first=" + first + " second=" + second);
            Check("⑩ 去重**不静默**：结构化日志含保留/丢弃/身份/原因",
                log.Count == 1 && log[0].Contains("保留=") && log[0].Contains("丢弃=")
                    && log[0].Contains("身份=") && log[0].Contains("原因="),
                log.Count == 0 ? "<无日志>" : log[0]);
            log.Clear();
            bool again = set.TryAdd(a, "System", null);
            Check("⑩ 同路径重复加入不再报（去重日志只针对**不同**路径）",
                !again && log.Count == 0, "again=" + again + " logN=" + log.Count);
        }

        static void TestHealCapMessage()
        {
            string searchDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
            var healer = new ReferenceHealer(new[] { searchDir });
            healer.ReportSink = HealSinkCapture;
            s_healSink.Clear();
            var refs = new System.Collections.Generic.List<string> { typeof(object).Assembly.Location };
            string src = "public class Cap {"
                + " public static System.Data.DataTable A;"
                + " public static System.Drawing.Bitmap B;"
                + " public static System.Web.HttpUtility C;"
                + " public static System.Management.ManagementObject D;"
                + " public static System.Configuration.ConfigurationManager E;"
                + " public static Microsoft.CSharp.CSharpCodeProvider F;"
                + "}";
            var r = RoslynCompiler.CompileWithHealing("HealCap", src, refs, true, healer, out string healLog);
            Check("② ≥6 缺引用：5 轮用尽仍失败", !r.Success, r.Diagnostics);
            Check("② 到顶文案（已补 5 个引用仍未通过 + 剩余诊断）",
                (r.Diagnostics.Contains("已补 5 个引用仍未通过") || healLog.Contains("已补 5 个引用仍未通过"))
                    && r.Diagnostics.Contains("CS0"),
                "diag=" + Head(r.Diagnostics, 300));
            Check("② 自愈计数=5（到顶而非提前放弃）", healer.Healed.Count == 5, "Healed=" + healer.Healed.Count);
        }

        /// <summary>定稿测试①⑤ + Submit 主循环（生产自愈路径的离线证明）。</summary>
        static void TestHealReplIntegration()
        {
            string mscorlib = typeof(object).Assembly.Location;
            string searchDir = System.IO.Path.GetDirectoryName(mscorlib);

            // ① System.Xml → System.Drawing 两轮成功，自愈计数 > 0（"分支真被执行"判据）
            var healer = new ReferenceHealer(new[] { searchDir });
            healer.ReportSink = HealSinkCapture;
            s_healSink.Clear();
            var refs = new System.Collections.Generic.List<string> { mscorlib };
            var r1 = RoslynCompiler.CompileWithHealing("HealRound1",
                "public class R1 { public static System.Xml.XmlDocument D; }", refs, true, healer, out string log1);
            Check("① 第一轮自愈成功（System.Xml）", r1.Success
                && refs.Exists(p => p.EndsWith("System.Xml.dll", StringComparison.OrdinalIgnoreCase)), r1.Diagnostics);
            var r2 = RoslynCompiler.CompileWithHealing("HealRound2",
                "public class R2 { public static System.Drawing.Bitmap B; }", refs, true, healer, out string log2);
            Check("① 第二轮自愈成功（System.Drawing）", r2.Success
                && refs.Exists(p => p.EndsWith("System.Drawing.dll", StringComparison.OrdinalIgnoreCase)), r2.Diagnostics);
            Check("① 两轮日志各记一行", log1.Contains("System.Xml") && log2.Contains("System.Drawing"), log1 + " || " + log2);
            Check("① 自愈计数 > 0（两轮合计）", healer.Healed.Count >= 2, "Healed=" + healer.Healed.Count);

            // ⑤ 加引用后 AllNamespaces：返回新对象（Invalidate 钩子真被调用）且含新引用的命名空间
            var healer5 = new ReferenceHealer(new[] { searchDir });
            var session = new ReplSession(new[] { mscorlib }, null);
            var before = session.AllNamespaces();
            bool beforeXml = false;
            foreach (string ns in before)
                if (ns == "System.Xml") beforeXml = true;
            Check("⑤ 修复前缓存不含 System.Xml", !beforeXml);
            var r3 = RoslynCompiler.CompileWithHealing("HealRound3",
                "public class R3 { public static System.Xml.XmlDocument D; }",
                session.ReferencePaths, true, healer5, out string log3, session);
            var after = session.AllNamespaces();
            bool afterXml = false;
            foreach (string ns in after)
                if (ns == "System.Xml") afterXml = true;
            Check("⑤ 自愈成功（经 ReferencePaths 同一 List）", r3.Success, r3.Diagnostics);
            Check("⑤ 钩子真被调用（AllNamespaces 返回新对象）", !ReferenceEquals(before, after));
            Check("⑤ 新引用命名空间可见", afterXml, "System.Xml 缺席");
            Check("⑤ ReferenceRevision 增长", session.ReferenceRevision > 0, "rev=" + session.ReferenceRevision);
            Check("⑤ LastHealLog 记录补引用", session.LastHealLog.Contains("System.Xml"), session.LastHealLog);

            // 生产主路径：ReplSession.Submit 的 emit 失败 → Resolve → 加引用 → 重试（≤5 轮）
            var healerS = new ReferenceHealer(new[] { searchDir });
            var sessionS = new ReplSession(new[] { mscorlib }, null, healerS);
            CompileResult sr = sessionS.Submit("var healVar = new System.Xml.XmlDocument();");
            Check("Submit 自愈后编译成功", sr.Success, sr.Diagnostics);
            Check("Submit LastHealLog 记录补引用", sessionS.LastHealLog.Contains("System.Xml"), sessionS.LastHealLog);
            Check("Submit ReferenceRevision>0", sessionS.ReferenceRevision > 0, "rev=" + sessionS.ReferenceRevision);
        }

        /// <summary>HookInstance 的离线替身（只承载描述所需的两个值；字段 string/bool，非载荷类型）。</summary>
        sealed class FakeHookForResetTest
        {
            readonly string target;
            readonly bool enabled;
            public FakeHookForResetTest(string target, bool enabled)
            {
                this.target = target;
                this.enabled = enabled;
            }
            public string Describe() => (enabled ? "[On] " : "[Off] ") + target + "()";
        }
    }

    /// <summary>
    /// §23 ⑨ 的**离线宿主基类替身**：模拟生产里"第 1 个提交继承宿主助手基类 `ReplHostBase`"（§19 (i)，
    /// 宿主自引用保证它在引用集里）。它属于**测试程序集自己的 public 类型**，不是 UE 类型，因此不需要
    /// ExplorerCore/Unity 依赖即可离线验证"探针基类 = 该提交的基类"这条可见性契约。
    /// 例：`var h = Hosted;` 只有探针继承了本类型才能推断出 `string`（否则静默退回 object）。
    /// `__result` 必须由基类承载（与 `ReplHostBase` 同形）：生成类只在 baseName == null 时自声明它，
    /// 注入基类后若基类没有它，值通道会 CS0103 '__result'（§19 的既有约定）。
    /// </summary>
    public class VarProbeHostBase23
    {
        public static readonly string Hosted = "h";
        public static object __result;
    }
}

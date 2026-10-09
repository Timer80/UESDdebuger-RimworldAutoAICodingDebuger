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

        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--success-path")
                return RunSuccessPathInChild();
            TestLoadOrder();
            TestLoadFailureDoesNotThrow();
            TestEnsureLoadedIsIdempotent();
            TestSuccessPathViaChildProcess();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
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

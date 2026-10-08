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
            // 第二次调用必须直接返回缓存结果（Mono 会缓存失败的类型解析 → 禁止重试）
            bool second = RoslynAssemblyLoader.EnsureLoaded(@"Z:\definitely\missing");
            Check("幂等：二次调用仍为 false", !second);
        }
    }
}

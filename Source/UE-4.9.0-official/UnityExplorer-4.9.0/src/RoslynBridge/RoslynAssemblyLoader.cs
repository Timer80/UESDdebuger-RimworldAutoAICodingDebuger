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

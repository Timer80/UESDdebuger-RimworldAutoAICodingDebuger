using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 固定顺序加载 Roslyn 载荷。
    /// ⚠️ 顺序不可调换：System.Memory 先于 System.Numerics.Vectors 加载时，Mono 会缓存
    /// typeref 解析失败，之后补齐也无法恢复（需重启进程）。见可行性报告 §4.2。
    /// ⚠️ 只尝试一次：失败后重试会命中同一个失败缓存，故用 s_attempted 锁死。
    /// ⚠️ C.5-lite 追加 8：LoadFrom 只把程序集放进默认加载上下文，不会自动应答后续 typeref
    /// 绑定——Unity Mono 无 netstandard 融合器，RimWorld 启动期 GetFields 字段扫描在无
    /// AssemblyResolve 应答方时 VM 直接挂死（peer mono 调试桥实测；Player.log 三轮炸点同机理）。
    /// E0 探针用 Type.GetType 直探"已载程序集"所以通过，GetFields 走装配绑定必须有应答方
    /// → 全部载入后挂 AppDomain 级应答器（见 PayloadAssemblyResolver）。
    /// 现状核实（grep 全 src）：唯一 AssemblyResolve 处理器是 ReplSession 会话级（任务 6 D4，
    /// 只应答 Submission_*_asm），对载荷不生效——本应答器与其零冲突、不重复。
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

        // ── 追加 8：AppDomain 级 AssemblyResolve 应答器 ──
        // 线程安全：AssemblyResolve 会在 Parallel 工作线程触发——映射在挂载前已填充完毕，
        // 处理器对静态字典只读（无懒初始化竞态、无锁）。
        // 挂载语义：静态命名方法存 static readonly 字段（禁匿名委托/lambda——任务 6 D4 踩过
        // 匿名委托摘不掉的坑）；"-=" 同一实例再 "+="，幂等重挂不泄漏。
        internal static readonly Dictionary<string, Assembly> PayloadAssembliesByName =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> PayloadSimpleNameSet = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp",
            "System.Collections.Immutable", "System.Reflection.Metadata",
            "System.Memory", "System.Threading.Tasks.Extensions",
            "System.Runtime.CompilerServices.Unsafe", "System.Numerics.Vectors",
        };

        static readonly ResolveEventHandler PayloadResolveHandler = PayloadAssemblyResolver;

        static Assembly PayloadAssemblyResolver(object sender, ResolveEventArgs args)
        {
            try
            {
                string simple = new AssemblyName(args.Name).Name;
                // 只应答 8 件载荷简单名；其余一律 null（零干扰第三方）。字典查不到返回 null——
                // 绝不在处理器内抛异常或 LoadFrom（绑定上下文重入风险）。
                if (simple == null || !PayloadSimpleNameSet.Contains(simple)) return null;
                Assembly asm;
                return PayloadAssembliesByName.TryGetValue(simple, out asm) ? asm : null;
            }
            catch { return null; }
        }

        public static bool EnsureLoaded(string payloadDir)
        {
            if (s_attempted) return s_available;
            s_attempted = true;
            try
            {
                // 先填充映射，后挂载：循环内逐个登记（简单名 → 已载程序集），
                // 全部成功后才安装应答器（挂载瞬间映射已完备，无部分装载态）。
                foreach (string file in LoadOrder)
                {
                    string full = Path.Combine(payloadDir, file);
                    if (!File.Exists(full))
                        throw new FileNotFoundException("载荷缺失：" + full);
                    Assembly loaded = Assembly.LoadFrom(full);
                    PayloadAssembliesByName[loaded.GetName().Name] = loaded;
                }
                AppDomain.CurrentDomain.AssemblyResolve -= PayloadResolveHandler;
                AppDomain.CurrentDomain.AssemblyResolve += PayloadResolveHandler;
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

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
    /// <para><b>任务 14（计划 F1）：载荷来源改为前置包 <c>UE.Roslyn</c>。</b>UE 不再随包分发自己那份 8 件，
    /// 交付路径走 <see cref="EnsureFrontPayload"/>（反射请前置包供载荷 → <see cref="AdoptLoadedPayload"/>
    /// 认领 + 挂应答器）；<see cref="EnsureLoaded"/> 保留为**开发期/自测用底层原语**（从指定目录
    /// LoadFrom），交付路径不再调用它（部署后 UE 侧已无 `UE_Data\...\Roslyn\` 目录）。
    /// 本类的 `LoadOrder` / 应答器 / "先填后挂" / 失败门闩等机制不变。</para>
    /// ⚠️ 失败门闩**按路径键控**（追加 8 修订 1）：同一路径只试一次（Mono 会缓存失败的类型解析，
    /// 重试无益），但未试过且存在的**不同**路径允许一次新尝试——避免"壳类推导错一次即永久锁死 mcs"。
    /// ⚠️ 应答器应答面**收窄**（追加 8 修订 2）：只应答请求方是本 UE 或 8 件载荷（或请求方不可判定）
    /// 的载荷名请求，通用名（System.Memory 等）不再无条件顶给第三方。
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

        static bool s_available;
        static bool s_resolverInstalled;
        static string s_failure;

        // ── 失败门闩：按"规范化路径"键控（追加 8 修订 1，取代原 `s_attempted` 一次性全局锁）──
        // 旧实现的单点失败：壳类自带推导路径且先运行 —— 一旦推导/部署形状不符（例：mod 用
        // LoadFolders 分出 `1.5/` 版本子目录时，UE 程序集目录的父目录**不是** mod 根），
        // 一次性锁就把 mcs 永久锁死，之后持正确路径的调用点（ExplorerCore.Init /
        // ConsoleController.ResetConsole）再也纠正不了。
        // 线程安全（记录，非本轮修复项）：本集合只被 EnsureLoaded 触碰，而调用点（壳类 cctor /
        // ExplorerCore.Init / ConsoleController.ResetConsole）都在主线程；
        // 应答器从不回调 EnsureLoaded ⇒ check-then-act 无并发窗口。将来若加后台调用点须补锁。
        // （AdoptLoadedPayload 同理：只在主线程改 s_available/映射。）
        static readonly HashSet<string> s_attemptedPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 本程序集简单名：应答面收窄（修订 2）的白名单之一。
        static readonly string SelfAssemblyName = typeof(RoslynAssemblyLoader).Assembly.GetName().Name;

        public static bool IsAvailable => s_available;
        public static string FailureReason => s_failure;
        /// <summary>应答器是否已挂上 AppDomain（壳类用它打**可验证**锚点）。</summary>
        public static bool ResolverInstalled => s_resolverInstalled;
        /// <summary>已登记映射的载荷件数（成功为 8）。</summary>
        public static int LoadedPayloadCount => PayloadAssembliesByName.Count;

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
                if (simple == null) return null;

                // ── 应答面收窄（追加 8 修订 2）：只应答"我们自己的请求链" ──
                // 条件：请求方不可判定（null）**或**请求方简单名 ∈ {本 UE 程序集} ∪ {8 件载荷}。
                // 理由：System.Memory / System.Collections.Immutable / System.Runtime.CompilerServices.Unsafe
                // 等是**通用名**——第三方 mod/组件按自己的版本请求时，无条件拿固定版本顶回去会撞版本、
                // 改坏别人的绑定。而我们的主路径请求方**必然是**本程序集（所有 typeref 都在 UE.dll 里：
                // 启动期字段扫描、JIT 期绑定都是它在请求），或 8 件载荷互相请求（CSharp → Immutable）；
                // 正在加载本程序集时 runtime 可能给 null（不可判定）→ 保持应答。故收窄不影响主路径。
                Assembly requester = GetRequestingAssembly(args);
                if (requester != null)
                {
                    string requesterName = requester.GetName().Name;
                    bool ours = string.Equals(requesterName, SelfAssemblyName,
                                    StringComparison.OrdinalIgnoreCase)
                                || PayloadSimpleNameSet.Contains(requesterName);
                    if (!ours) return null;
                }

                // 只应答 8 件载荷简单名；其余一律 null（零干扰第三方）。字典查不到返回 null——
                // 绝不在处理器内抛异常或 LoadFrom（绑定上下文重入风险）。
                if (!PayloadSimpleNameSet.Contains(simple)) return null;
                Assembly asm;
                return PayloadAssembliesByName.TryGetValue(simple, out asm) ? asm : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 取"请求方程序集"（修订 2 收窄的判据）。跨配置要点：本文件按设计进**全部 9 配置**编译
        /// （不在 csproj 非 STANDALONE 排除组），而 <c>ResolveEventArgs.RequestingAssembly</c> 是
        /// .NET 4.0 才加的成员——net35 目标（ML_Mono/BIE5_Mono/BIE6_Mono）**没有它**，直接访问必
        /// CS1061（本轮实测：ML_Mono 编译失败即此）。
        /// 分叉依据 = SDK 隐式注入的 TFM 符号（实测 ML_Mono 的 csc 命令行含 <c>NET35</c>、
        /// STANDALONE_Mono 含 <c>NET472</c>）。net35 面返回 null = "请求方不可判定" → 保持应答
        /// （与修订前行为一致）；交付面 STANDALONE_Mono（net472）走收窄分支。net35 三配置本来就不编
        /// Roslyn 控制台四件，此处的应答器只是安全网。
        /// </summary>
#if NET35
        static Assembly GetRequestingAssembly(ResolveEventArgs args) { return null; }
#else
        static Assembly GetRequestingAssembly(ResolveEventArgs args)
        {
            try { return args.RequestingAssembly; } catch { return null; }
        }
#endif

        /// <summary>路径规范化（门闩键）：失败即退化为原串（非法字符等），绝不在错误路径上抛。</summary>
        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        public static bool EnsureLoaded(string payloadDir)
        {
            // ① 成功态短路一切：载荷只加载一次，后续调用零副作用（ExplorerCore.Init / ResetConsole 幂等命中）。
            if (s_available) return true;

            // ② 同一路径只试一次：Mono 会缓存失败的类型解析，同路径重试无益（原一次性锁的正确部分）。
            // ③ 未试过且**存在**的路径允许一次新尝试 → 推导错/部署形状变（LoadFolders 版本子目录、
            //    换副本）时可被后续调用点自愈。
            // ④ 不存在的路径不消耗尝试、不覆盖既有诊断（噪音路径不得吃掉自愈机会）。
            string key = NormalizePath(payloadDir);
            if (s_attemptedPaths.Contains(key)) return false;
            if (!Directory.Exists(key))
            {
                if (s_failure == null)
                    s_failure = "载荷目录不存在：" + payloadDir;   // 首个诊断留痕（壳类/控制台日志可见）
                return false;
            }
            s_attemptedPaths.Add(key);

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
                s_resolverInstalled = true;
                s_available = true;
                s_failure = null;
            }
            catch (Exception ex)
            {
                s_failure = payloadDir + " → " + ex.GetType().Name + ": " + ex.Message;
                s_available = false;
            }
            return s_available;
        }

        /// <summary>
        /// **交付路径入口（任务 14 / F1）**：请前置包 <c>UE.Roslyn</c>（反射 + 字符串，零编译期引用）供载荷，
        /// 成功则**认领**已载入 AppDomain 的 8 件并挂应答器。
        /// 前置包缺席/未启用/加载失败 ⇒ **响亮失败**（failure 非空），调用方据此干净退回 mcs；永不抛。
        /// 与 <c>EnsureLoaded(dir)</c>（开发期/自测用底层原语：从指定目录 LoadFrom）互不干扰——载荷只加载
        /// 一次，两条路谁先成功谁生效（<c>s_available</c> 短路）。
        /// </summary>
        public static bool EnsureFrontPayload(out string failure)
        {
            if (s_available) { failure = null; return true; }

            string frontFailure;
            if (!RoslynFrontGate.TryEnsure(out frontFailure))
            {
                s_failure = "前置包 " + RoslynFrontGate.FrontPackageId + " 未安装或加载失败：" + frontFailure;
                failure = s_failure;
                return false;
            }

            return AdoptLoadedPayload(out failure);
        }

        /// <summary>
        /// 认领「已由前置包按自身加载顺序 LoadFrom 进 AppDomain」的 8 件载荷：填映射 → 挂应答器。
        /// 判据是 **AppDomain 实测**（不信任任何自报件数）：8 件齐才挂，缺一件即响亮失败且**不**挂
        /// （"先填后挂、无部分装载态"不变）。
        /// ⚠️ 应答器仍归 UE：前置包只 LoadFrom、**不**挂 AssemblyResolve（其源码无该处理器，已核实），
        /// 而"GetFields 绑定缺应答方 ⇒ VM 直接挂死"是 UE 自己程序集的风险（peer mono 调试桥实测）。
        /// </summary>
        public static bool AdoptLoadedPayload(out string failure)
        {
            if (s_available) { failure = null; return true; }

            Dictionary<string, Assembly> found =
                new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            Assembly[] loaded;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception ex)
            {
                s_failure = "认领已载载荷失败（枚举 AppDomain 异常）：" + ex.GetType().Name + ": " + ex.Message;
                failure = s_failure;
                return false;
            }

            foreach (Assembly a in loaded)
            {
                string name = SimpleNameOf(a);
                if (name == null || !PayloadSimpleNameSet.Contains(name)) continue;
                if (!found.ContainsKey(name)) found[name] = a;
            }

            if (found.Count < LoadOrder.Length)
            {
                s_failure = "前置包 " + RoslynFrontGate.FrontPackageId + " 已加载，但 AppDomain 里载荷不全："
                    + found.Count + "/" + LoadOrder.Length + "（缺失：" + DescribeMissingPayload(found) + "）";
                failure = s_failure;
                return false;
            }

            PayloadAssembliesByName.Clear();
            foreach (KeyValuePair<string, Assembly> kv in found)
                PayloadAssembliesByName[kv.Key] = kv.Value;

            AppDomain.CurrentDomain.AssemblyResolve -= PayloadResolveHandler;
            AppDomain.CurrentDomain.AssemblyResolve += PayloadResolveHandler;
            s_resolverInstalled = true;
            s_available = true;
            s_failure = null;
            failure = null;
            return true;
        }

        static string SimpleNameOf(Assembly assembly)
        {
            if (assembly == null) return null;
            try { return assembly.GetName().Name; }
            catch { return null; }
        }

        /// <summary>缺失载荷清单（诊断；用文件名口径，与 LoadOrder 一致）。</summary>
        static string DescribeMissingPayload(Dictionary<string, Assembly> found)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (string file in LoadOrder)
            {
                string simple = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? file.Substring(0, file.Length - 4) : file;
                if (found.ContainsKey(simple)) continue;
                if (sb.Length > 0) sb.Append("、");
                sb.Append(file);
            }
            return sb.Length == 0 ? "<无>" : sb.ToString();
        }
    }
}

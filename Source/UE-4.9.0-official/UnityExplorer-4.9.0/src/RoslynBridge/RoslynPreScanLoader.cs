#if STANDALONE_MONO
using System;
using System.IO;
using Verse; // StaticConstructorOnStartup 只在 STANDALONE 配置可用（ML/BIE 配置无 RimWorld 引用）

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 真·扫描前加载壳（C.5-lite 追加 6）。⚠️ 勿删。
    /// 时序实证（abe5040 上机，Player.log :1393 前置包 cctor → :1457 扫描 → :1566 仍炸）：
    /// UE 的 init 链（UELoader→ExplorerStandalone→ExplorerCore.Init）不在 CallAll 内、晚于
    /// ReportProbablyMissingAttributes 字段扫描——ExplorerCore.Init 处的早加载从未先于扫描运行过。
    /// 唯一可靠的扫描前注入点 = [StaticConstructorOnStartup]：CallAll 在 DoPlayLoad 内先跑完全部
    /// 该类 .cctor、之后才做字段扫描。本壳 cctor 在此窗口加载载荷并挂 AppDomain 级
    /// AssemblyResolve 应答器（RoslynAssemblyLoader.EnsureLoaded 内置，追加 8：无应答方时
    /// GetFields 绑定 VM 直接挂死——peer mono 调试桥实测）。
    /// 幂等：EnsureLoaded 有 s_attempted 锁 + 应答器 "-="/"+=" 重挂；与 ExplorerCore.Init 的
    /// 调用点谁先谁生效（追加 7）。失败缓存走 s_attempted，控制台 Reset 时回退 mcs。
    /// 日志走 Verse.Log（进 Player.log，验收 grep 锚点）+ LogToExplorerPanel（进 UE 面板缓存；
    /// 此期 ExplorerCore.Log 内部的 Loader 回调必 NRE，故只能 best-effort 包裹，见该方法注释）。
    /// 【本文件不引 ML/BIE 配置】：整文件 #if STANDALONE_MONO（实测只有 STANDALONE_Mono 配置被注入此符号，
    /// 见 ConsoleController.cs:154 同款约定），其余 8 配置该文件编译为空。
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class RoslynPreScanLoader
    {
        static RoslynPreScanLoader()
        {
            try
            {
                bool ok = RoslynAssemblyLoader.EnsureLoaded(DerivePayloadDir());
                int mapped = RoslynAssemblyLoader.LoadedPayloadCount;
                bool resolver = RoslynAssemblyLoader.ResolverInstalled;

                // 追加 9 修订 3：正锚点只报**可验证事实**（映射条数 + 委托挂载位）。
                // ⚠️ 不要用 `typeof(ReplSession).GetFields(...)` 当"钩子自检"：C.5 已把 ReplSession 的
                // 载荷类型字段全部擦除，载荷未载/应答器未挂时它**照样成功**——那是假阳性，会把缺陷
                // 放进验收。钩子真正生效的证据只有两条：Player.log 零 TypeLoadException +
                // 控制台 Roslyn 路径实际可用（本锚点证明不了它们，故不做此承诺）。
                if (ok && mapped == 8 && resolver)
                {
                    string msg = "[CSConsole] Roslyn 扫描前加载完成（载荷映射 8/8，解析器已挂载）";
                    Verse.Log.Message(msg);
                    LogToExplorerPanel(msg, false);
                }
                else
                {
                    string msg = "[CSConsole] Roslyn 扫描前加载未就绪（avail=" + ok
                        + "，载荷映射 " + mapped + "/8，解析器已挂载=" + resolver
                        + "，原因=" + (RoslynAssemblyLoader.FailureReason ?? "未尝试/未知")
                        + "）：控制台将在 Reset 时以正确路径重试或回退 mcs";
                    Verse.Log.Warning(msg);
                    LogToExplorerPanel(msg, true);
                }
            }
            catch (Exception ex)
            {
                string msg = "[CSConsole] Roslyn 扫描前加载失败（控制台将在 Reset 时回退 mcs）："
                    + ex.GetType().Name + ": " + ex.Message;
                Verse.Log.Warning(msg);
                LogToExplorerPanel(msg, true);
            }
        }

        /// <summary>
        /// UE 面板日志（best-effort）。⚠️ 勿直接裸调 <c>ExplorerCore.Log</c>：此期它**必然抛 NRE**——
        /// 其内部在 LogPanel 之后还要调 <c>Loader.OnLogMessage</c>，而 <c>ExplorerCore.Loader</c> 要到
        /// <c>ExplorerCore.Init</c> 才赋值（init 链在 UELoaderMod 的 SceneManager.sceneLoaded 里，
        /// 晚于本 cctor，见类注释时序实证）。LogPanel 那一半是安全的（Logs.Add + 两处 null 守卫），
        /// 故吞掉异常后面板仍留痕。
        /// 绝不能裸调、也绝不让它外泄：ExplorerCore.Log 的 NRE 会被各 catch 变成"加载失败"的**误报**。
        /// 但**空 catch 也不可接受**（追加 8 修订 5）：它会吞掉 ExplorerCore 静态 ctor 的
        /// TypeInitializationException（如 :31 的 Harmony 实例化失败）——那是一次性**永久毒化**，
        /// 之后面板通道全废，而 Player.log 却只剩成功行。故把真因经包外通道（Verse.Log，不依赖
        /// ExplorerCore）报一次，保证 Player.log 有真因；只报一次避免刷屏。
        /// 副作用：首次调用会触发 ExplorerCore 的静态 ctor（创建 Harmony 实例）；幂等、无害。
        /// </summary>
        static void LogToExplorerPanel(string message, bool warning)
        {
            try
            {
                if (warning) ExplorerCore.LogWarning(message);
                else ExplorerCore.Log(message);
            }
            catch (Exception ex)
            {
                if (s_panelChannelReported) return;
                s_panelChannelReported = true;
                string inner = ex.InnerException != null
                    ? " ← " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message
                    : "";
                string msg = "[CSConsole] UE 面板日志通道不可用（面板缓存丢失，不影响载荷加载）："
                    + ex.GetType().Name + ": " + ex.Message + inner;
                try { Verse.Log.Warning(msg); } catch { /* 兜底通道也失败：无处可报，只能放弃 */ }
            }
        }

        static bool s_panelChannelReported;

        // ExplorerCore.ExplorerFolder 此时尚不可用，故不能直接复用：它是
        // `Path.Combine(Loader.ExplorerFolderDestination, Loader.ExplorerFolderName)`（ExplorerCore.cs:28）
        // ——Loader 由 ExplorerCore.Init 赋值、ExplorerStandalone.explorerFolderDest 由 UELoader 的
        // RedirectConfigFolder 预置（ExplorerBootstrap.cs:182），两者都是运行期赋值（非静态初始化器/
        // 非常量），本 cctor 早于它们 → 直接读必 NRE。故按同一套规则自行推导（Live 部署实测命中主候选）：
        //   UE 程序集目录 = <ModRoot>/Assemblies → <ModRoot>/UE_Data/sinai-dev-UnityExplorer/Roslyn；
        // 回落官方默认（ExplorerStandalone.CheckExplorerFolder 未被重定向时用 DLL 旁目录）：
        //   <ModRoot>/Assemblies/sinai-dev-UnityExplorer/Roslyn。
        // 按"目录存在"择优；都不存在时取重定向路径（EnsureLoaded 缓存失败并告警，可见可查）。
        static string DerivePayloadDir()
        {
            string asmDir = Path.GetDirectoryName(
                Uri.UnescapeDataString(new Uri(typeof(ExplorerCore).Assembly.CodeBase).AbsolutePath));
            string modRoot = Path.GetDirectoryName(asmDir);
            string redirected = Path.Combine(modRoot, "UE_Data",
                ExplorerCore.DEFAULT_EXPLORER_FOLDER_NAME, "Roslyn");
            if (Directory.Exists(redirected)) return redirected;
            string besideDll = Path.Combine(asmDir, ExplorerCore.DEFAULT_EXPLORER_FOLDER_NAME, "Roslyn");
            return Directory.Exists(besideDll) ? besideDll : redirected;
        }
    }
}
#endif

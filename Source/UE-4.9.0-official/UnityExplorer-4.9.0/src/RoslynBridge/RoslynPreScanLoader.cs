#if STANDALONE_MONO
using System;
using Verse; // StaticConstructorOnStartup 只在 STANDALONE 配置可用（ML/BIE 配置无 RimWorld 引用）

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 真·扫描前加载壳（C.5-lite 追加 6）。⚠️ 勿删。
    /// 时序实证（abe5040 上机，Player.log :1393 前置包 cctor → :1457 扫描 → :1566 仍炸）：
    /// UE 的 init 链（UELoader→ExplorerStandalone→ExplorerCore.Init）不在 CallAll 内、晚于
    /// ReportProbablyMissingAttributes 字段扫描——ExplorerCore.Init 处的早加载从未先于扫描运行过。
    /// 唯一可靠的扫描前注入点 = [StaticConstructorOnStartup]：CallAll 在 DoPlayLoad 内先跑完全部
    /// 该类 .cctor、之后才做字段扫描。本壳 cctor 在此窗口**请前置包 UE.Roslyn 供载荷**
    /// （RoslynAssemblyLoader.EnsureFrontPayload：反射 + 字符串，零编译期引用）并挂 AppDomain 级
    /// AssemblyResolve 应答器（追加 8：无应答方时 GetFields 绑定 VM 直接挂死——peer mono 调试桥实测；
    /// 前置包自身不挂应答器，故这层仍归 UE）。
    /// 时序保障：RimWorld 在 CallAll **之前**已把全部启用模组的程序集 LoadFrom 进 AppDomain（与模组
    /// 列表顺序无关）⇒ 只要前置包启用，本壳 cctor 扫 AppDomain 必然找到它；`loadAfter`/`modDependencies`
    /// 只是意图表达，不承担正确性（见 About.xml 与前置包 README）。
    /// 幂等：EnsureFrontPayload 有 s_attempted 锁 + 应答器 "-="/"+=" 重挂；与 ExplorerCore.Init 的
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
                string failure;
                bool ok = RoslynAssemblyLoader.EnsureFrontPayload(out failure);
                int mapped = RoslynAssemblyLoader.LoadedPayloadCount;
                bool resolver = RoslynAssemblyLoader.ResolverInstalled;
                int expected = RoslynAssemblyLoader.LoadOrder.Length;

                // 追加 9 修订 3：正锚点只报**可验证事实**（映射条数 + 委托挂载位）。
                // ⚠️ 不要用 `typeof(ReplSession).GetFields(...)` 当"钩子自检"：C.5 已把 ReplSession 的
                // 载荷类型字段全部擦除，载荷未载/应答器未挂时它**照样成功**——那是假阳性，会把缺陷
                // 放进验收。钩子真正生效的证据只有两条：Player.log 零 TypeLoadException +
                // 控制台 Roslyn 路径实际可用（本锚点证明不了它们，故不做此承诺）。
                // 任务 14（F1）：载荷来源 = 前置包 UE.Roslyn（UE 侧不再有 UE_Data\...\Roslyn\ 副本）
                // ⇒ 锚点点名"谁供载荷"；未就绪分支必须让玩家一眼分清"包没装/没启用"（可去装/启用）
                // 与"包在但加载失败"（看它自己的 [Roslyn] 日志），且明说控制台仍可用。
                if (ok && mapped == expected && resolver)
                {
                    string msg = "[CSConsole] Roslyn 扫描前加载完成（前置包 " + RoslynFrontGate.FrontPackageId
                        + " 供载荷 " + mapped + "/" + expected + "，供体程序集=" 
                        + (RoslynFrontGate.SourcePath ?? "<未定位>") + "，解析器已挂载）";
                    Verse.Log.Message(msg);
                    LogToExplorerPanel(msg, false);
                }
                else
                {
                    string msg = "[CSConsole] Roslyn 扫描前加载未就绪（前置包 " + RoslynFrontGate.FrontPackageId
                        + " 未安装或加载失败："
                        + (failure ?? RoslynFrontGate.FailureReason ?? "未尝试/未知")
                        + "；载荷映射 " + mapped + "/" + expected + "，解析器已挂载=" + resolver
                        + "）：控制台将回退 mcs（mcs 不依赖前置包，控制台仍可用）";
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

        // 任务 14（F1）：原 `DerivePayloadDir()`（推导 UE 侧 `<ModRoot>\UE_Data\sinai-dev-UnityExplorer\
        // Roslyn`，回落 DLL 旁目录）已删除——UE 不再分发载荷，交付路径改请前置包供货（RoslynFrontGate）。
        // 前置包程序集路径的推导搬到了 RoslynFrontGate（`<game>\Mods\Roslyn\Assemblies\Roslyn.dll`，
        // 只用于把失败原因说清楚，不据此装载）。
    }
}
#endif

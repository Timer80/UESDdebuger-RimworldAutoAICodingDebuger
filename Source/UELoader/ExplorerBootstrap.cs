using System;
using System.IO;
using System.Reflection;
using UnityExplorer;
using UnityExplorer.UI;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// UnityExplorer 官方二进制的初始化引导（对应 spec redo-unityexplorer-integration Task 3）。
    ///
    /// 初始化流程：
    ///   1) CompatibilityHooks.Install() —— Task 4 预留的兼容层挂载点（必须在 UE 初始化之前安装）
    ///   2) RedirectConfigFolder() —— 把 UE 配置目录重定向到 UESDdebuger/UE_Data（默认在 UE DLL 旁边）
    ///   3) ExplorerStandalone.CreateInstance(logListener, modulesPath) —— 官方 UE 4.9.0 初始化入口
    ///
    /// F7 显示/隐藏：由 UE 原生处理（ExplorerCore.Update() 每帧检查
    /// InputManager.GetKeyDown(ConfigManager.Master_Toggle.Value)，默认 KeyCode.F7，
    /// 命中后切换 UIManager.ShowMenu）。因此加载器不重复挂接 KeyDown 处理器，避免双重切换；
    /// 这里提供等价 API ToggleMenu() 供其他代码调用。
    ///
    /// 失败处理：整个初始化包在 try/catch 内，输出可读日志（RimWorld 日志），不抛异常阻断游戏启动。
    /// </summary>
    public static class ExplorerBootstrap
    {
        public static string ModRoot { get; private set; }
        public static bool Initialized { get; private set; }

        public static void Initialize(string modRoot)
        {
            if (Initialized)
                return;

            ModRoot = modRoot;

            Log.Message("[UELoader] Initializing UnityExplorer 4.9.0 (official binaries)...");

            try
            {
                // Task 4 预留挂载点：RimWorld 兼容补丁必须在 UE 初始化之前应用
                CompatibilityHooks.Install();

                // 顶部 UI 下移补丁（调试工具栏 + 殖民地头像条避开 UE 菜单栏），不依赖 UE 初始化，提前安装
                UEUIShifter.Install();

                // UEHttpHandler 运行时初始化（日志流订阅 + 主线程调度器）。
                // 必须在主线程执行（UEMainThreadDispatcher.EnsureInitialized 会 new GameObject），
                // 因此由本方法（SceneManager.sceneLoaded 回调，主线程）调用，而非 Mod 构造函数。
                try
                {
                    UEHttpHandler.Init();
                }
                catch (Exception ex)
                {
                    Log.Error($"[UELoader] UEHttpHandler.Init FAILED: {ex.GetType().Name}: {ex.Message}");
                }

                // 配置目录：UESDdebuger/Assemblies -> UESDdebuger/UE_Data/sinai-dev-UnityExplorer
                RedirectConfigFolder();

                string modulesPath = Path.Combine(ModRoot, "UE_Data", "Modules");
                try
                {
                    Directory.CreateDirectory(modulesPath);
                }
                catch
                {
                    // 创建失败则让 UE 回落默认路径
                }

                // 官方初始化入口（UE 4.9.0 自带的 IExplorerLoader 实现，零修改二进制）
                ExplorerStandalone.CreateInstance(OnUELog, modulesPath);

                Initialized = true;
                Log.Message("[UELoader] UnityExplorer initialized. Press F7 to toggle the UI (default master toggle).");
            }
            catch (Exception ex)
            {
                // 可读日志 + 不阻断游戏启动
                Log.Error($"[UELoader] UnityExplorer initialization FAILED: {ex.GetType().Name}: {ex.Message}");
                Log.Error($"[UELoader] Details: {ex}");
            }
        }

        /// <summary>
        /// 提前启动游戏内 UE HTTP 服务（本机动态端口，从 3001 起递增探测空闲端口），
        /// 不依赖 UE 初始化与任何场景：
        /// 在 UELoaderMod 构造函数中调用（RimWorld 初始主菜单场景在 Mod 构造前已加载，
        /// SceneManager.sceneLoaded 无法覆盖主菜单，因此 HTTP 服务必须在此提前启动）。
        /// /trigger-quicktest 与 /unityexplorer/status 主菜单即可用；
        /// 其余 UE 工具需进世界后 UE 初始化（绑定世界场景）才实际可用。
        /// 启动成功后把实际端口写入 {rootDir}/MCP/ports.json（供 MCP 侧动态读取）。
        /// 幂等：UEHttpServer.Start 内部有 running 保护；失败不阻断游戏启动。
        /// </summary>
        public static void StartHttpServer(string rootDir)
        {
            try
            {
                UEHttpServer.Start(rootDir);
            }
            catch (Exception ex)
            {
                Log.Error($"[UELoader] UEHttpServer start FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示/隐藏 UE 界面。UE 4.9.0 的实际等价 API 是 UIManager.ShowMenu
        /// （ExplorerCore.Update() 响应 Master_Toggle 按键时使用的就是该属性）。
        /// </summary>
        public static void ToggleMenu()
        {
            try
            {
                UIManager.ShowMenu = !UIManager.ShowMenu;
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] ToggleMenu failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- 私有实现 ----

        /// <summary>
        /// 官方 ExplorerStandalone.CheckExplorerFolder() 默认把配置目录放在 UE DLL 旁边
        /// （即 UESDdebuger/Assemblies/sinai-dev-UnityExplorer）。
        /// 该目录由 protected static 字段 explorerFolderDest 缓存，且仅在其为 null 时计算。
        /// 因此在调用 CreateInstance() 之前预置该字段，即可把 UE 的 ExplorerFolder 重定向到
        /// UESDdebuger/UE_Data/sinai-dev-UnityExplorer（配置、Output、模块目录都落到 UESDdebuger 模组目录下，
        /// 避免在 Assemblies 里写运行期文件）。
        /// 该字段属于 UE 4.9.0 内部实现；若反射失败则回落默认行为（记录日志，不影响初始化）。
        /// </summary>
        private static void RedirectConfigFolder()
        {
            try
            {
                FieldInfo field = typeof(ExplorerStandalone).GetField(
                    "explorerFolderDest", BindingFlags.Static | BindingFlags.NonPublic);

                if (field == null)
                {
                    Log.Warning("[UELoader] explorerFolderDest field not found; UE config folder stays next to the DLL.");
                    return;
                }

                string dest = Path.Combine(ModRoot, "UE_Data");
                field.SetValue(null, dest);
                Log.Message($"[UELoader] UnityExplorer config folder redirected to {dest}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] Could not redirect UE config folder (will use default): {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// UE 日志桥接到 RimWorld 日志（ExplorerStandalone.OnLog 事件）。
        /// </summary>
        private static void OnUELog(string message, LogType logType)
        {
            try
            {
                switch (logType)
                {
                    case LogType.Error:
                    case LogType.Exception:
                    case LogType.Assert:
                        Log.Error($"[UnityExplorer] {message}");
                        break;
                    case LogType.Warning:
                        Log.Warning($"[UnityExplorer] {message}");
                        break;
                    default:
                        Log.Message($"[UnityExplorer] {message}");
                        break;
                }
            }
            catch
            {
                // 日志桥接本身失败不应影响 UE
            }
        }
    }
}

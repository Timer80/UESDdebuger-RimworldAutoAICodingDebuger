global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Reflection;
global using UnityEngine;
global using UnityEngine.UI;
global using UniverseLib;
global using UniverseLib.Utility;
using UnityExplorer.Config;
using UnityExplorer.ObjectExplorer;
using UnityExplorer.RoslynBridge;
using UnityExplorer.Runtime;
using UnityExplorer.UI;
using UnityExplorer.UI.Panels;
using UniverseLib.Input;

namespace UnityExplorer
{
    public static class ExplorerCore
    {
        public const string NAME = "UnityExplorer";
        public const string VERSION = "4.9.0";
        public const string AUTHOR = "Sinai";
        public const string GUID = "com.sinai.unityexplorer";

        public static IExplorerLoader Loader { get; private set; }
        public static string ExplorerFolder => Path.Combine(Loader.ExplorerFolderDestination, Loader.ExplorerFolderName);
        public const string DEFAULT_EXPLORER_FOLDER_NAME = "sinai-dev-UnityExplorer";

        public static HarmonyLib.Harmony Harmony { get; } = new HarmonyLib.Harmony(GUID);

        /// <summary>
        /// Initialize UnityExplorer with the provided Loader implementation.
        /// </summary>
        public static void Init(IExplorerLoader loader)
        {
            if (Loader != null)
                throw new Exception("UnityExplorer is already loaded.");

            Loader = loader;

            Log($"{NAME} {VERSION} initializing...");

            CheckLegacyExplorerFolder();
            Directory.CreateDirectory(ExplorerFolder);
            ConfigManager.Init(Loader.ConfigHandler);

            // ⚠️ 方案 A 早加载（任务 8 架构修复，2026-10-09 合议裁定，人类保留否决）——勿删。
            // 位置更正（C.5-lite 追加 7，Player.log 上机实证）：本调用**不早于**字段扫描。
            // RimWorld Verse.StaticConstructorOnStartupUtility.CallAll 跑完全部 [StaticConstructorOnStartup]
            // .cctor 之后，才以 Parallel.ForEach 对全部 mod 程序集类型做 GetFields 字段扫描
            // （ReportProbablyMissingAttributes）；而本 UE 没有 [StaticConstructorOnStartup]，其 init 链
            // （UELoader → ExplorerStandalone → 此处）由 UELoaderMod 的 SceneManager.sceneLoaded 触发，
            // 整体晚于 CallAll。上机时序：:1393 前置包 cctor → :1457 扫描 → :1566 仍炸，全日志无
            // "UnityExplorer 4.9.0 initializing"（该行在 Loader 赋值后、本链更后面才打）。
            // ⇒ 扫描前的加载与钩子安装由壳类 RoslynPreScanLoader（#if STANDALONE_MONO，唯一可靠的
            // 扫描前注入点）承担，此处无法替代它。
            // 此处保留的价值（第二层防御）：在 Universe.Init 之前装好载荷与解析钩子，防 UniverseLib/UI
            // 期的反射触雷——它只能保护"此处之后的反射"，保护不了字段扫描。
            // 根因三层：① 本程序集原有 4 个"载荷类型字段"（RoslynCompiler.ParseOptions/CompileOptions、
            // ReplSession.s_imageRefs、闭包 <>c__DisplayClass23_0:v）——Mono 字段签名解析不按
            // BindingFlags 过滤，载荷未加载即 TypeLoadException
            // （Player.log 实证：Could not load type of field 'UnityExplorer.RoslynBridge.ReplSession:s_imageRefs'
            // … Microsoft.CodeAnalysis 4.14.0.0 不可解析），且 Mono 缓存 typeref 解析失败、进程内不可恢复
            // → 此后 new ReplSession 必败 → 控制台 DisableConsole；② 载荷只 LoadFrom 不够：typeref 绑定
            // 必须有人应答（Type.GetType 直探已载程序集所以过，GetFields 走引用表绑定所以炸）；
            // ③ ExplorerCore 无 [StaticConstructorOnStartup]，早加载不可能先于扫描。
            // 对应修复三层：① 擦除载荷字段（C.5-lite）；② AppDomain 级 AssemblyResolve 应答器
            // （RoslynAssemblyLoader，追加 8）；③ 壳类扫描前注入（追加 6）。
            // 不读配置——配置只决定"是否使用"，不决定"是否加载"；ResetConsole 处的 EnsureLoaded
            // 幂等命中缓存（s_attempted 锁 + 应答器 "-="/"+=" 重挂），行为不变，与壳类谁先谁生效。
            // 跨配置安全：RoslynAssemblyLoader 不在 csproj 非 STANDALONE 排除组（排除组仅
            // ReferenceScanner + RoslynCompiler/DiagnosticFormatter/ReplSession/RoslynScriptEvaluator
            // 五件，本类仅依赖 System.*），9 配置全编译，无需 #if。
            // 升级路径（C 方案，已登记任务 12 矩阵/最终审查）：以上三层均为同一程序集内的止血；
            // 治本 = Roslyn 四件移出主程序集为卫星程序集（本调用点随之下沉/迁走）。前置包与壳类已是
            // 该路径的第一步；若再有早期反射器（UniverseLib/Harmony/第三方 mod 检查器）报同类
            // TypeLoadException，直接上 C 方案，勿再加时序类补丁。
            try { RoslynAssemblyLoader.EnsureLoaded(Path.Combine(ExplorerFolder, "Roslyn")); }
            catch (Exception ex)
            {
                LogWarning($"[CSConsole] Roslyn 载荷早加载失败（控制台将在 Reset 时回退 mcs）：{ex.Message}");
            }

            Universe.Init(ConfigManager.Startup_Delay_Time.Value, LateInit, Log, new()
            {
                Disable_EventSystem_Override = ConfigManager.Disable_EventSystem_Override.Value,
                Force_Unlock_Mouse = ConfigManager.Force_Unlock_Mouse.Value,
                Unhollowed_Modules_Folder = loader.UnhollowedModulesFolder
            });

            UERuntimeHelper.Init();
            ExplorerBehaviour.Setup();
            UnityCrashPrevention.Init();
        }

        // Do a delayed setup so that objects aren't destroyed instantly.
        // This can happen for a multitude of reasons.
        // Default delay is 1 second which is usually enough.
        static void LateInit()
        {
            SceneHandler.Init();

            Log($"Creating UI...");

            UIManager.InitUI();

            Log($"{NAME} {VERSION} ({Universe.Context}) initialized.");

            // InspectorManager.Inspect(typeof(Tests.TestClass));
        }

        internal static void Update()
        {
            // check master toggle
            if (InputManager.GetKeyDown(ConfigManager.Master_Toggle.Value))
            {
                UIManager.ShowMenu = !UIManager.ShowMenu;
            }
        }


        #region LOGGING

        public static void Log(object message)
            => Log(message, LogType.Log);

        public static void LogWarning(object message)
            => Log(message, LogType.Warning);

        public static void LogError(object message)
            => Log(message, LogType.Error);

        public static void LogUnity(object message, LogType logType)
        {
            if (!ConfigManager.Log_Unity_Debug.Value)
                return;

            Log($"[Unity] {message}", logType);
        }

        private static void Log(object message, LogType logType)
        {
            string log = message?.ToString() ?? "";

            LogPanel.Log(log, logType);

            switch (logType)
            {
                case LogType.Assert:
                case LogType.Log:
                    Loader.OnLogMessage(log);
                    break;

                case LogType.Warning:
                    Loader.OnLogWarning(log);
                    break;

                case LogType.Error:
                case LogType.Exception:
                    Loader.OnLogError(log);
                    break;
            }
        }

        #endregion


        #region LEGACY FOLDER MIGRATION

        // Can be removed eventually. For migration from <4.7.0
        static void CheckLegacyExplorerFolder()
        {
            string legacyPath = Path.Combine(Loader.ExplorerFolderDestination, "UnityExplorer");
            if (Directory.Exists(legacyPath))
            {
                LogWarning($"Attempting to migrate old 'UnityExplorer/' folder to 'sinai-dev-UnityExplorer/'...");

                // If new folder doesn't exist yet, let's just use Move().
                if (!Directory.Exists(ExplorerFolder))
                {
                    try
                    {
                        Directory.Move(legacyPath, ExplorerFolder);
                        Log("Migrated successfully.");
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Exception migrating folder: {ex}");
                    }
                }
                else // We have to merge
                {
                    try
                    {
                        CopyAll(new(legacyPath), new(ExplorerFolder));
                        Directory.Delete(legacyPath, true);
                        Log("Migrated successfully.");
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Exception migrating folder: {ex}");
                    }
                }
            }
        }

        public static void CopyAll(DirectoryInfo source, DirectoryInfo target)
        {
            Directory.CreateDirectory(target.FullName);

            // Copy each file into it's new directory.
            foreach (FileInfo fi in source.GetFiles())
                fi.MoveTo(Path.Combine(target.ToString(), fi.Name));

            // Copy each subdirectory using recursion.
            foreach (DirectoryInfo diSourceSubDir in source.GetDirectories())
            {
                DirectoryInfo nextTargetSubDir = target.CreateSubdirectory(diSourceSubDir.Name);
                CopyAll(diSourceSubDir, nextTargetSubDir);
            }
        }

        #endregion
    }
}

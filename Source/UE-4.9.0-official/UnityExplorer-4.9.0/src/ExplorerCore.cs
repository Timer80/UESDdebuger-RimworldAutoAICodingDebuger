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
            // 根因：RimWorld Verse.StaticConstructorOnStartupUtility.CallAll 在跑完各 mod 的
            // [StaticConstructorOnStartup] .cctor（含本 UE 的 init 链 → 此处）之后，会以
            // Parallel.ForEach 对全部 mod 程序集类型做 GetFields 字段扫描
            // （ReportProbablyMissingAttributes）。扫描到 ReplSession.s_imageRefs
            // （List<MetadataReference>）与闭包字段 <>c__DisplayClass23_0:v（VariableDeclaratorSyntax）
            // 时若 Roslyn 载荷未加载 → TypeLoadException（Player.log 实证：Could not load type of
            // field 'UnityExplorer.RoslynBridge.ReplSession:s_imageRefs' … Microsoft.CodeAnalysis
            // 4.14.0.0 不可解析）→ Mono 缓存 typeref 解析失败，进程内不可恢复 → 此后
            // new ReplSession 必败 → 控制台 DisableConsole。
            // 故必须在字段扫描发生之前无条件加载载荷（不读配置——配置只决定"是否使用"，
            // 不决定"是否加载"）；ResetConsole 处的 EnsureLoaded 幂等命中缓存，行为不变。
            // 跨配置安全：RoslynAssemblyLoader 不在 csproj 非 STANDALONE 排除组（排除组仅
            // ReferenceScanner + RoslynCompiler/DiagnosticFormatter/ReplSession/RoslynScriptEvaluator
            // 五件，本类仅依赖 System.*），9 配置全编译，无需 #if。
            // ⚠️ 位置约束与已知盲区（对端会话审查提出）：本调用是【UE 初始化链的一部分】，
            // 不是"比 UE 更早"——它依赖本链被完整执行到此处；若将来链序调整或前序步骤抛异常
            // 致此处被跳过，早加载会静默失效（字段依赖并未消除——本修复属止血，非治本）。
            // 升级路径（已登记任务 12 矩阵/最终审查）：若未来任何早期反射器（UniverseLib/Harmony/
            // 第三方 mod 检查器）再报同类 TypeLoadException，直接上 C 方案（Roslyn 四件移出主程序集
            // 为卫星程序集），勿再加时序类补丁。
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

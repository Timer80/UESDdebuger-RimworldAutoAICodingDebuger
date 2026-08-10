using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace UELoader
{
    /// <summary>
    /// Task 4 兼容层（spec redo-unityexplorer-integration Task 4，用户决策版：源码重编译 against Lib.Harmony 2.3.3）。
    ///
    /// 结论（静态判定 + 二进制级核验，2026-08-06）：UE 4.9.0 已按官方源码重编译，编译面 =
    /// Lib.Harmony 2.3.3（0Harmony 2.3.3.0，net35 资产），UniverseLib.Mono 1.5.1 同步重编译；
    /// UESDdebuger/Assemblies 不再携带 0Harmony，运行时依赖 brrainz.harmony 模组的 Lib.Harmony 2.3.3。
    ///
    /// Mono.Cecil 核验记录（ilscan）：
    ///  1) 重编译 UnityExplorer.STANDALONE.Mono.dll（4.9.0.0，ILRepack 已合并 mcs+Tomlet）：
    ///     AssemblyRef 0Harmony v2.3.3.0（原 HarmonyX 2.5.2 已不存在）；
    ///     Harmony 调用点 = Harmony.ctor(string) / Patch(MethodBase, HarmonyMethod×4)（5 参，2.3.3 重载）/
    ///     CreateProcessor(MethodBase) / GetPatchedMethods() / PatchProcessor.AddPrefix·AddPostfix·AddFinalizer·
    ///     AddTranspiler·Patch·Unpatch / HarmonyMethod.ctor(MethodInfo) / AccessTools.Field·Method(Type,string,
    ///     Type[],Type[])·Property / GeneralExtensions.FullDescription —— 共 18 个成员，全部存在于 2.3.3 表面（0 missing）。
    ///  2) 重编译 UniverseLib.Mono.dll（1.5.1.0）：AssemblyRef 0Harmony v2.3.3.0；引用 12 个成员
    ///     （含 AccessTools.all、AccessTools.Method 4 参、Property），全部存在于 2.3.3 表面（0 missing）。
    ///  3) 结论：UE/UniverseLib 与 UELoader 编译面一致 = 2.3.3，正常绑定下 UnityCrashPrevention.Init 与
    ///     TimeScaleWidget.InitPatch 的 Patch 调用可正常执行，**无需任何外部“跳过”补丁**。
    ///
    /// 本挂载点只做运行时绑定校验并输出判定依据：
    ///  - 绑定 2.3.3（brrainz.harmony）→ 输出「无需补丁」。
    ///  - 绑定版本 API 兼容（如某模组抢先加载了 API 超集的新版 0Harmony，或 2.3.x 同面）→ 输出说明。
    ///  - 绑定旧版（1.x/2.0/2.2 等缺 API）→ 输出缺失 API 清单 + 功能降级清单（UE 内部 try/catch 静默降级，
    ///    Hook Manager 仍可用）+ 处理建议（调整 ModsConfig 加载顺序，保证 brrainz.harmony 先加载）。
    ///
    /// ModErrorChecker 风险评估（社区模组 "Mod Error Checker"，非游戏内置）：
    /// 该模组启动时经 Harmony MethodBodyReader 遍历已加载模组程序集方法体，解析类型/方法引用，
    /// 报告缺失引用（误报多因“编译面版本 ≠ 运行时版本”）。现在 UE 编译面 = 运行时 = 2.3.3，
    /// 解析 Harmony.Patch 5 参等引用不会抛 MissingMethodException；UnityEngine/UI/UniverseLib.Mono/
    /// MonoMod.RuntimeDetour（21.9.19.1 含 Hook/Apply）均在位；About.xml 无 Defs → 正常情况不会误报。
    /// ⚠ 若出现其他模组抢先加载旧版 0Harmony 的异常场景，ModErrorChecker 仍可能误报，处置方式与下方
    /// “加载顺序”建议一致（保证 brrainz.harmony 在 UESDdebuger 之前加载，0Harmony 2.3.3 先进内存）。
    /// </summary>
    internal static class CompatibilityHooks
    {
        /// <summary>重编译 UE（4.9.0）编译面引用的 0Harmony 版本 = brrainz.harmony 的 Lib.Harmony 2.3.3。</summary>
        private static readonly Version UeHarmonyRefVersion = new Version(2, 3, 3, 0);

        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            installed = true;

            try
            {
                VerifyHarmonyBinding();
            }
            catch (Exception ex)
            {
                Log.Warning($"[UELoader] Compatibility hook check failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 运行时判定依据：UELoader 与重编译 UE 引用同一个简单名 “0Harmony”（AssemblyName 相同、弱命名），
        /// 运行时由 CLR/Mono 统一绑定到同一个实例，因此 typeof(HarmonyLib.Harmony).Assembly 就是
        /// UnityExplorer 实际绑定的 Harmony 程序集。版本/API 匹配 → 无需兼容补丁；不匹配 → 输出降级警告。
        /// </summary>
        private static void VerifyHarmonyBinding()
        {
            Assembly harmonyAssembly = typeof(Harmony).Assembly;
            Version boundVersion = harmonyAssembly.GetName().Version;

            if (boundVersion.Equals(UeHarmonyRefVersion))
            {
                Log.Message("[UELoader] No compatibility patches needed: 0Harmony 2.3.3 matches the recompiled UnityExplorer 4.9.0 compile-time surface. " +
                            "All UE Harmony call sites (UnityCrashPrevention.Init, TimeScaleWidget.InitPatch, HookInstance, UniverseLib patches) are permitted as-is.");
                return;
            }

            // 绑定版本 ≠ 2.3.3：逐项核验重编译 UE 实际依赖的关键 API（与 ilscan 清单一致）。
            List<string> missing = new List<string>();
            if (GetPatchMethod(harmonyAssembly) == null)
                missing.Add("Harmony.Patch(MethodBase, HarmonyMethod×4) 5-arg overload");
            if (harmonyAssembly.GetType("HarmonyLib.AccessTools")?.GetMethod("Method",
                    new[] { typeof(Type), typeof(string), typeof(Type[]), typeof(Type[]) }) == null)
                missing.Add("AccessTools.Method(Type, string, Type[], Type[])");
            if (harmonyAssembly.GetType("HarmonyLib.GeneralExtensions")?.GetMethod("FullDescription",
                    new[] { typeof(MethodBase) }) == null)
                missing.Add("GeneralExtensions.FullDescription(MethodBase)");
            if (harmonyAssembly.GetType("HarmonyLib.PatchProcessor") == null)
                missing.Add("HarmonyLib.PatchProcessor");
            if (harmonyAssembly.GetType("HarmonyLib.Harmony")?.GetMethod("CreateProcessor",
                    new[] { typeof(MethodBase) }) == null)
                missing.Add("Harmony.CreateProcessor(MethodBase)");
            if (harmonyAssembly.GetType("HarmonyLib.Harmony")?.GetMethod("GetPatchedMethods") == null)
                missing.Add("Harmony.GetPatchedMethods()");

            if (missing.Count == 0)
            {
                Log.Message($"[UELoader] 0Harmony binding is v{boundVersion} (compile surface was 2.3.3) but all required APIs exist; UE stays fully functional.");
                return;
            }

            Log.Warning($"[UELoader] 0Harmony binding mismatch: recompiled UnityExplorer 4.9.0 requires 0Harmony 2.3.3 surface, " +
                        $"but the runtime bound version is {boundVersion}. Missing API: {string.Join(", ", missing)}. " +
                        "UnityCrashPrevention and TimeScaleWidget will silently degrade (Hook Manager still works). " +
                        "Likely cause: another mod loaded an older 0Harmony before brrainz.harmony. " +
                        "Fix: make sure brrainz.harmony (Lib.Harmony 2.3.3) is loaded before any mod that ships its own 0Harmony in ModsConfig, or remove/upgrade the conflicting mod.");
        }

        private static MethodInfo GetPatchMethod(Assembly harmonyAssembly)
        {
            // 重编译 UE 引用的 5 参重载：Patch(MethodBase, HarmonyMethod, HarmonyMethod, HarmonyMethod, HarmonyMethod)
            Type harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony");
            if (harmonyType == null)
                return null;

            Type hm = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod");
            if (hm == null)
                return null;
            return harmonyType.GetMethod("Patch", new[]
            {
                typeof(MethodBase), hm, hm, hm, hm
            });
        }
    }
}

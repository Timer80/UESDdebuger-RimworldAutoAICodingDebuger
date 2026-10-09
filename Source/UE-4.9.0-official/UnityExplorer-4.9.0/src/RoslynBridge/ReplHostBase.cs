using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using UniverseLib;
using UnityExplorer.UI.Panels;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// Roslyn 路径的 REPL **助手基类**（§19 设计裁定 (i)：与 mcs 路径对等）。
    ///
    /// 背景（游戏内实证）：Roslyn 路径下 `Log("hello")` → `CS1955`、
    /// `typeof(UnityExplorer.CSConsole.ConsoleController).Name` → **CS0246** ⇒ 提交编译既解析不到
    /// 宿主程序集、也没有任何助手。mcs 路径之所以有助手，是因为它的提交基类是 `ScriptInteraction`
    /// （**就在 UE 程序集里**，且 `ConsoleController` 把 `InteractiveBaseClass` 设成它）。
    /// 本类把同一套助手搬到 Roslyn 生成类的继承链根：`Submission_1 : ReplHostBase`，
    /// 于是 `Log(...)`/`Inspect(...)`/`GetVars()` 等裸名在 `Run()` 里直接可用（**继承成员，不需要 using**）。
    ///
    /// ⚠️ 与 mcs 侧的三点差异（都必须在报告 §19 的成员表里注明）：
    ///  ① **不继承 `Mono.CSharp.InteractiveBase`**（那是 mcs 求值器的基类契约；Roslyn 路径不得拖 mcs 依赖）；
    ///  ② 全部成员是 **static**（与 `ScriptInteraction` 相同；生成类的 `Run()` 也是 static，
    ///     若做成实例成员则 `Run()` 里根本调不到）；
    ///  ③ 生成类的 `__result` 值通道字段由本类承载（原先是 `Submission_1` 自己声明；
    ///     现在 n==1 也有基类了，字段上移到本类，语义不变——`ReplSession` 沿继承链读它）。
    ///
    /// ⚠️ C.5 护栏：本类**不得**出现载荷类型（Microsoft.CodeAnalysis.* 等）字段——那会让启动期字段扫描
    /// 再次 TypeLoadException。本类只有 `__result`（object）一个字段。
    /// ⚠️ 日志/异常纪律：助手输出走 `ExplorerCore.Log`（对齐 mcs"打印到日志面板"）；
    /// 助手抛出的异常由 `ReplSession.Submit` 的运行时 catch 收进**本次**提交的诊断，
    /// 不会污染下一次提交（与 R1/B3 同源纪律）。
    /// </summary>
    public class ReplHostBase
    {
        /// <summary>REPL 值通道：末表达式的求值结果（由生成类的 `Run()` 写入，`ReplSession.LastValue` 读取）。
        /// 原在 `Submission_1` 内声明；n==1 现在也有基类，故上移到本类（静态字段，跨提交共享一个槽位，语义不变）。</summary>
        public static object __result;

        // ── 以下 12 个成员 = `ScriptInteraction` 的可移植子集，逐字对齐（名称/static/签名/语义）──

        public static object CurrentTarget
            => InspectorManager.ActiveInspector?.Target;

        public static object[] AllTargets
            => InspectorManager.Inspectors.Select(it => it.Target).ToArray();

        public static void Log(object message)
            => ExplorerCore.Log(message);

        public static void Inspect(object obj)
            => InspectorManager.Inspect(obj);

        public static void Inspect(Type type)
            => InspectorManager.Inspect(type);

        public static Coroutine Start(IEnumerator ienumerator)
            => RuntimeHelper.StartCoroutine(ienumerator);

        public static void Stop(Coroutine coro)
            => RuntimeHelper.StopCoroutine(coro);

        public static void Copy(object obj)
            => ClipboardPanel.Copy(obj);

        public static object Paste()
            => ClipboardPanel.Current;

        // ⚠️ 单一真源（§19 要求）：三个 helper 的取数与打印语义必须与 mcs 侧**完全同源**——
        // 都走 `ConsoleController.Evaluator`（`IScriptEvaluator`）→ 即当前生效求值器的实现，
        // 不在本类里另写一套（Roslyn 侧会转到 ReplSession.GetUsing/GetVars/GetClasses）。
        public static void GetUsing()
            => Log(UnityExplorer.CSConsole.ConsoleController.Evaluator.GetUsing());

        public static void GetVars()
        {
            string vars = UnityExplorer.CSConsole.ConsoleController.Evaluator.GetVars();
            if (string.IsNullOrEmpty(vars))
                ExplorerCore.LogWarning("No variables seem to be defined!");
            else
                Log(vars);
        }

        public static void GetClasses()
        {
            string classes = UnityExplorer.CSConsole.ConsoleController.Evaluator.GetClasses();
            if (string.IsNullOrEmpty(classes))
                ExplorerCore.LogWarning("No classes seem to be defined.");
            else
                Log(classes);
        }
    }
}

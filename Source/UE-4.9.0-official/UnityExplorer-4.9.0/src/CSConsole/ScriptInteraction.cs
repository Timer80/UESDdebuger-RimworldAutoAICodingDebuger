using HarmonyLib;
using System.Collections;
using System.Text;
using UnityExplorer.UI.Panels;

namespace UnityExplorer.CSConsole
{
    // mcs InteractiveBase 是 mcs 求值器要求的基类契约（ConsoleController 用它设 InteractiveBaseClass），
    // 唯一残留的 mcs 依赖；三个 helper 的 mcs 内部反射已在任务 7 搬进 ScriptEvaluator.GetClasses()。
    public class ScriptInteraction : Mono.CSharp.InteractiveBase
    {
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

        // 注意：裸 Evaluator 会解析到继承自 Mono.CSharp.Evaluator 的**实例**属性（只对 mcs 有效），
        // 必须显式走 ConsoleController.Evaluator（IScriptEvaluator），否则 Roslyn 路径下拿到 null。
        public static void GetUsing()
            => Log(ConsoleController.Evaluator.GetUsing());

        public static void GetVars()
        {
            string vars = ConsoleController.Evaluator.GetVars();
            if (string.IsNullOrEmpty(vars))
                ExplorerCore.LogWarning("No variables seem to be defined!");
            else
                Log(vars);
        }

        public static void GetClasses()
        {
            string classes = ConsoleController.Evaluator.GetClasses();
            if (string.IsNullOrEmpty(classes))
                ExplorerCore.LogWarning("No classes seem to be defined.");
            else
                Log(classes);
        }
    }
}

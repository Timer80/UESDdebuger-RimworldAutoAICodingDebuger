using System;
using System.Reflection;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 动态 Harmony 补丁的"包装 + 产物里取回"纯逻辑（任务 10）。
    /// 无 Unity/UE/Roslyn/mcs 依赖 ⇒ ① 进全部 9 配置编译（net35 亦可用）；② **可离线单测**：
    /// RoslynBridgeTests 用本类（生产代码本体，不是副本）走一遍"包装 → 提交编译 → 产物里定位类型 →
    /// 取补丁方法 → Harmony 挂载生效"。
    /// ⚠️ C.5：本类只有 System.Reflection 类型，无载荷类型字段。
    /// </summary>
    public static class DynamicPatch
    {
        /// <summary>补丁类名前缀（沿用 mcs 时代的命名，日志/产物里可辨识）。</summary>
        public const string TypeNamePrefix = "DynamicPatch_";

        /// <summary>补丁类型名 = 前缀 + Ticks（同一会话内每次提交产物独立 ⇒ 同名也不会串）。</summary>
        public static string NewTypeName() => TypeNamePrefix + DateTime.Now.Ticks.ToString();

        /// <summary>
        /// 补丁方法查找 flags：**必须含 NonPublic**。默认补丁源码里是 `static void Postfix(...)`——
        /// 无访问修饰符 = private（见 HookInstance.GenerateDefaultPatchSourceCode），而 REPL 生成类把用户
        /// 成员原样嵌入，不会替它补 public ⇒ 只用 `Static|Public` 会**静默**取不到任何补丁方法，
        /// 症状是"补丁创建成功、开关显示 On、但方法根本不生效"（最坏的一类失败：没有报错）。
        /// </summary>
        public const BindingFlags PatchMethodFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>把编辑器里的补丁方法源码包装成可提交的类（`public static class`，见 Locate 的调用面）。</summary>
        public static string Wrap(string typeName, string patchSource)
        {
            return "public static class " + typeName + Environment.NewLine
                + "{" + Environment.NewLine
                + patchSource + Environment.NewLine
                + "}";
        }

        /// <summary>
        /// 在提交产物里定位补丁类型；找不到 → null（调用方**响亮**报错，绝不静默跳过）。
        /// 为什么不能 `assembly.GetType(typeName)`：REPL 把每次提交的顶层类型**嵌进**生成类
        /// `Submission_&lt;tag&gt;_N`（ReplSession.BuildSubmissionSource），故补丁类型的真实全名是
        /// `Submission_&lt;tag&gt;_N+&lt;typeName&gt;`，而 N 是会话私有计数、外部不可知 ⇒ 只能按**简单名扫描**
        /// （`Assembly.GetTypes()` 已含嵌套类型，`Type.Name` 对嵌套类型就是它自己的简单名）。
        /// 扫的是**本次提交产物**（`ReplSession.LastAssembly`，只含本次 Submission_N + polyfill）⇒
        /// 误命中面极小；同一次提交不可能出现两个同名补丁类（一个补丁只提交一次）。
        /// </summary>
        public static Type Locate(Assembly assembly, string typeName)
        {
            if (assembly == null || string.IsNullOrEmpty(typeName)) return null;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 部分类型装载失败：仍用能拿到的那些判定（否则"整个产物一个类型都取不到"会更难查）
                types = ex.Types;
            }
            if (types == null) return null;

            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t != null && t.Name == typeName) return t;
            }
            return null;
        }

        /// <summary>取四个补丁方法（对应名字不存在 → 该 out 为 null）。flags 见 <see cref="PatchMethodFlags"/>。</summary>
        public static void FindPatchMethods(Type patchType, out MethodInfo prefix, out MethodInfo postfix,
            out MethodInfo finalizer, out MethodInfo transpiler)
        {
            prefix = null;
            postfix = null;
            finalizer = null;
            transpiler = null;
            if (patchType == null) return;

            prefix = patchType.GetMethod("Prefix", PatchMethodFlags);
            postfix = patchType.GetMethod("Postfix", PatchMethodFlags);
            finalizer = patchType.GetMethod("Finalizer", PatchMethodFlags);
            transpiler = patchType.GetMethod("Transpiler", PatchMethodFlags);
        }
    }
}

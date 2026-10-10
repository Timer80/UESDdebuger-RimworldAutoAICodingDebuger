using System;
using System.Collections;
using System.Reflection;
using System.Text;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 动态 Harmony 补丁的"包装 + 产物里取回"纯逻辑（任务 10）。
    /// 无 Unity/UE/Roslyn/mcs 依赖 ⇒ ① 进全部 9 配置编译（net35 亦可用）；② **可离线单测**：
    /// RoslynBridgeTests 用本类（生产代码本体，不是副本）走一遍"包装 → 提交编译 → 产物里定位类型 →
    /// 取补丁方法 → Harmony 挂载生效"。
    /// ⚠️ C.5：本类无实例字段（仅 2 个安全 const：string/BindingFlags），方法签名只出现
    /// System/System.Collections 类型（Assembly/Type/MethodInfo/IDictionary/Func 等），无载荷类型、无 lambda 闭包。
    /// </summary>
    public static class DynamicPatch
    {
        /// <summary>补丁类名前缀（沿用 mcs 时代的命名，日志/产物里可辨识）。</summary>
        public const string TypeNamePrefix = "DynamicPatch_";

        /// <summary>补丁类型名 = 前缀 + Ticks（同一会话内每次提交产物独立 ⇒ 同名也不会串）。</summary>
        public static string NewTypeName() => TypeNamePrefix + DateTime.Now.Ticks.ToString();

        /// <summary>
        /// 补丁方法查找 flags = `Public | NonPublic | Static`。
        /// **口径（R-a 勘误后）**：旧 mcs 路径用的是 `ReflectionUtility.FLAGS`（UniverseLib
        /// `ReflectionUtility.cs:23` = `Public|Instance|NonPublic|Static`）——它**本来就含 NonPublic**，
        /// 因为默认补丁源码是隐式 private 的 `static void Postfix(...)`（见
        /// HookInstance.GenerateDefaultPatchSourceCode）。本常量是**按旧口径的等价改写**（去掉 Instance 见下），
        /// **不是**修复本仓库既有缺陷；简报规格里的 `Static|Public` 相对旧实现反而是倒退，此处防的是
        /// "未来照简报回退"。
        /// **刻意不含 `Instance`（R-c）**：补丁方法恒 `static`。依据 = 非 static 补丁在 Harmony 的
        /// `PatchProcessor.Patch()` 抛 `InvalidProgramException`（Lib.Harmony 2.3.3 + net472 实测）；
        /// 收窄后这类补丁由 `FailureReason` 四方法全 null 判据**更早、更明确**地响亮失败
        /// （`CompileAndGenerateProcessor` 返回 false），而不是等 `Patch()` 抛异常。
        /// ⚠️ 出厂 `0Harmony.dll`（FileVersion 2.5.2，与测试用的 Lib.Harmony 2.3.3 不同）对非 static
        /// 补丁的语义差异**留上机核验**，本轮不查。
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
        /// `Submission_&lt;n&gt;`（ReplSession：类名 `Submission_` + 提交号，**不含 tag**；tag 只进**程序集名**
        /// `Submission_&lt;tag&gt;_&lt;n&gt;_asm`），故补丁类型的真实全名是
        /// `Submission_&lt;n&gt;+&lt;typeName&gt;`，而 n 是会话私有计数、外部不可知 ⇒ 只能按**简单名扫描**
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

        /// <summary>
        /// R-e：生产编排的**纯判定**（9 配置共用、无 Unity/Roslyn 依赖 ⇒ 可离线单测）。
        /// 返回 null = 可继续（去注册补丁方法）；非 null = 必须失败的原因文本，覆盖三类：
        /// ① 产物程序集为 null；② 产物里找不到补丁类型（<see cref="Locate"/> 返回 null）；
        /// ③ 四个补丁方法全 null（成因：方法名不在 Prefix/Postfix/Finalizer/Transpiler 名单，
        /// 或方法非 static —— <see cref="PatchMethodFlags"/> 只含 Static）。
        /// 调用方（<c>HookInstance.CompileAndGenerateProcessor</c>）拿到非 null ⇒
        /// `LogWarning(reason)` + `return false` —— 绝不"警告后仍 return true"（那会让 create_hook
        /// 拿到 success:true 却挂 0 个补丁函数，正是 R-d① 要关掉的静默假绿）。
        /// ⚠️ C.5：签名与方法体只出现 Assembly/Type/MethodInfo/string，无载荷类型、无 lambda。
        /// </summary>
        public static string FailureReason(Assembly asm, Type patchClass,
            MethodInfo prefix, MethodInfo postfix, MethodInfo finalizer, MethodInfo transpiler)
        {
            if (asm == null)
                return "补丁已编译但没有拿到编译产物程序集。（已按失败处理）";

            if (patchClass == null)
                return "编译产物里找不到补丁类型（DynamicPatch.Locate 返回 null）。（已按失败处理）";

            if (prefix == null && postfix == null && finalizer == null && transpiler == null)
                return "补丁源码里没有任何可识别的方法"
                    + "（Prefix/Postfix/Finalizer/Transpiler，且必须是 static）——补丁已编译但不会生效。（已按失败处理）";

            return null;
        }

        /// <summary>
        /// R-f①：控制台 Reset/Dispose 前的**活 hook 判据**（纯函数，判据本身可离线断言）。
        /// 遍历 <paramref name="currentHooks"/>：计数 == 0 ⇒ 返回 null（不打扰日志）；
        /// 计数 &gt; 0 ⇒ 返回必须原样 <c>LogWarning</c> 的文本（列出 hook 数与各目标方法）。
        /// 为什么响亮：补丁现在编译进控制台会话，而 Reset/Dispose 会摘掉该会话的
        /// <c>AssemblyResolve</c>（`Assembly.Load(byte[])` 产物不进默认加载上下文，按名解析是唯一补救）
        /// ⇒ 若补丁体引用了控制台里定义的类型/会话变量，该补丁首次执行（JIT）会在**被补丁的游戏方法
        /// 内部**抛 TypeLoadException/FileNotFoundException，而 UI 仍显示 On。默认生成补丁不受影响。
        /// </summary>
        /// <param name="currentHooks">`HookList.currentHooks`（OrderedDictionary：签名 → HookInstance）。</param>
        /// <param name="describeHook">单条 hook 的可读描述；**用方法传入，不用 lambda**（C.5 闭包纪律）。</param>
        public static string ActiveHookResetWarning(IDictionary currentHooks, Func<object, string> describeHook)
        {
            if (currentHooks == null || currentHooks.Count == 0)
                return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("[R-f] 控制台即将 Reset/Dispose，但 HookList 里还有 ")
              .Append(currentHooks.Count)
              .Append(" 个 hook：补丁已编译进控制台会话，若补丁体引用了控制台里定义的类型/会话变量，")
              .Append("本次 Dispose 摘掉 AssemblyResolve 后，该补丁首次执行（JIT）会在被补丁的方法内部抛 TypeLoadException/FileNotFoundException（UI 仍显示 On）。")
              .Append("默认生成补丁不受影响。目标方法：");

            foreach (object key in currentHooks.Keys)
            {
                object hook = currentHooks[key];
                string desc = describeHook == null ? null : describeHook(hook);
                sb.AppendLine().Append("  - ").Append(string.IsNullOrEmpty(desc) ? (key == null ? "<null>" : key.ToString()) : desc);
            }
            return sb.ToString();
        }
    }
}

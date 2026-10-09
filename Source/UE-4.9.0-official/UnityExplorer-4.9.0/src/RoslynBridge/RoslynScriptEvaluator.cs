using System;
using System.Collections.Generic;
using System.Linq;
using UnityExplorer.CSConsole;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>Roslyn 求值器门面：载荷健康 + 引用扫描 + REPL 会话。</summary>
    public sealed class RoslynScriptEvaluator : IScriptEvaluator
    {
        readonly ReplSession s_session;
        public object LastValue { get; private set; }
        public string LastValueText => LastValue?.ToString();

        public bool IsAvailable { get; private set; }
        public string FailureReason { get; private set; }

        // A1（合并修复轮）：配置项 CSharp_Console_Allow_Mod_Type_Resolution 的落位载体。
        // ⚠️ 铁律（对端审查裁定）：任务 13 自愈器落地前，本开关【不改变任何引用集行为】——
        // 严禁把 ReferenceScanner.ModAssemblyPaths() 的产物预加进引用集（那是旧三参设计，已裁定废弃）；
        // 模组程序集只允许经"编译诊断触发"的自愈路径进入（任务 13 实现）。
        public bool AllowModTypeResolution { get; }

        public RoslynScriptEvaluator(IEnumerable<string> referencePaths, string blacklist = "",
            bool allowModTypeResolution = false)
            : this(referencePaths, blacklist, allowModTypeResolution, ReplSession.HostBaseTypeName) { }

        /// <summary>
        /// §19：可注入生成类继承链根（<paramref name="hostBaseTypeName"/>）。生产走上面的公开 ctor
        /// （链根 = 助手基类）；离线测试传 null 绕过"测试进程里没有宿主程序集"，
        /// 以便继续单测门面自身语义（Evaluate/GetVars/AddUsing/Dispose）。
        /// </summary>
        internal RoslynScriptEvaluator(IEnumerable<string> referencePaths, string blacklist,
            bool allowModTypeResolution, string hostBaseTypeName)
        {
            s_session = new ReplSession(referencePaths, hostBaseTypeName);
            AllowModTypeResolution = allowModTypeResolution;
            IsAvailable = true;
        }

        // A1 观测点：引用集大小透传（测试断言"开关不放大引用集"用）。
        public int ReferenceCount => s_session.ReferenceCount;

        /// <summary>
        /// 最近一次成功提交的编译产物程序集（任务 10 消费点：`HookInstance` 从产物里取回动态补丁类型）。
        /// 仅 Roslyn 路径有"编译产物程序集"这一概念（mcs 路径反射内部结构取类型，无此属性）。
        /// </summary>
        public System.Reflection.Assembly LastCompiledAssembly => s_session.LastAssembly;

        public bool Evaluate(string input, out string output)
        {
            CompileResult r = s_session.Submit(input);
            LastValue = s_session.LastValue;
            LastNotes = r.Notes;
            // A2（合并修复轮）：REPL 返回值对齐 mcs 侧语义（HELP_REPL 承诺"末表达式返回值会打印"）：
            // 成功且无诊断且有值 → output 带回值文本。LastValue 由 ReplSession 逐提交重置（Submit :217），
            // 类/using 提交恒为 null → output 仍走 Diagnostics（="Code compiled without errors."，与 mcs 一致）。
            // B3（根因清扫轮）：成功但有**提示**（如匿名类型按 object 提升）且本次无值 → 把提示作为结果回显，
            // 否则用户只会看到后续的 CS0103/CS1061，看不到"为什么 p 变成了 object"。
            if (r.Success && string.IsNullOrEmpty(r.Diagnostics) && LastValue == null
                && !string.IsNullOrEmpty(LastNotes))
                output = LastNotes;
            else if (r.Success && string.IsNullOrEmpty(r.Diagnostics) && LastValue != null)
                output = LastValueText;
            else
                output = r.Diagnostics;
            return r.Success;
        }

        /// <summary>最近一次成功提交的可读提示（B3；无提示为 null）。</summary>
        public string LastNotes { get; private set; }

        /// <summary>
        /// 批量预置默认 using（B1/D 护栏 + B4：一次校验编译取代 N 次提交），返回被接受的命名空间；
        /// 不可用的项被剔除并写进 <paramref name="diagnostics"/>（调用方负责 LogWarning）。
        /// </summary>
        public List<string> PrimeUsings(IEnumerable<string> rootNamespaces, out string diagnostics)
            => s_session.AddUsings(rootNamespaces, out diagnostics);

        public string[] GetCompletions(string input, out string prefix)
        {
            // 任务 9 接线：语义模型（成员，含基类链）+ 命名空间元数据（前缀匹配）；候选 = 扣掉 prefix 的余部。
            // 任何解析失败都退化为"空候选"（补全绝不抛、绝不阻断输入）。
            return RoslynCompletion.Complete(input, s_session, out prefix);
        }

        public string GetUsing() => s_session.GetUsing();
        public string GetVars() => s_session.GetVars();
        public string GetClasses() => s_session.GetClasses();
        public void AddUsing(string namespaceName) => s_session.Submit("using " + namespaceName + ";");

        // 坑 1（任务 6 生命周期契约，覆盖简报步骤 4 的空 Dispose）：必须转发 s_session.Dispose()
        // 摘掉会话挂的 AppDomain.AssemblyResolve 钩，否则控制台每次 Reset 换会话都泄漏处理器。
        public void Dispose() => s_session.Dispose();
    }
}

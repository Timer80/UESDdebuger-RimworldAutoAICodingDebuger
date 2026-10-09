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
        {
            s_session = new ReplSession(referencePaths);
            AllowModTypeResolution = allowModTypeResolution;
            IsAvailable = true;
        }

        // A1 观测点：引用集大小透传（测试断言"开关不放大引用集"用）。
        public int ReferenceCount => s_session.ReferenceCount;

        public bool Evaluate(string input, out string output)
        {
            CompileResult r = s_session.Submit(input);
            LastValue = s_session.LastValue;
            // A2（合并修复轮）：REPL 返回值对齐 mcs 侧语义（HELP_REPL 承诺"末表达式返回值会打印"）：
            // 成功且无诊断且有值 → output 带回值文本。LastValue 由 ReplSession 逐提交重置（Submit :217），
            // 类/using 提交恒为 null → output 仍走 Diagnostics（="Code compiled without errors."，与 mcs 一致）。
            if (r.Success && string.IsNullOrEmpty(r.Diagnostics) && LastValue != null)
                output = LastValueText;
            else
                output = r.Diagnostics;
            return r.Success;
        }

        public string[] GetCompletions(string input, out string prefix)
        {
            // 本任务先返回空数组（保证本任务可独立编译/测试通过）；任务 9 接入：
            //   return RoslynCompletion.Complete(input, s_session, out prefix);
            prefix = "";
            return Array.Empty<string>();
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

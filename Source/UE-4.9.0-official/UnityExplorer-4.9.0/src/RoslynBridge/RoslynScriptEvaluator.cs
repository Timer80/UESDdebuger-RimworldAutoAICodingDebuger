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

        public RoslynScriptEvaluator(IEnumerable<string> referencePaths, string blacklist = "")
        {
            s_session = new ReplSession(referencePaths);
            IsAvailable = true;
        }

        public bool Evaluate(string input, out string output)
        {
            CompileResult r = s_session.Submit(input);
            LastValue = s_session.LastValue;
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

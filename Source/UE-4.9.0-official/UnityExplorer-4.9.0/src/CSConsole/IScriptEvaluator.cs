using System;

namespace UnityExplorer.CSConsole
{
    /// <summary>C# 控制台求值器抽象：mcs 版（ScriptEvaluator）与 Roslyn 版（RoslynScriptEvaluator）共同实现。</summary>
    public interface IScriptEvaluator : IDisposable
    {
        /// <summary>编译并执行一段输入；返回是否成功。REPL 值经 LastValue 暴露。</summary>
        bool Evaluate(string input, out string output);

        /// <summary>补全候选（不含前缀）；prefix 为已输入的前缀。</summary>
        string[] GetCompletions(string input, out string prefix);

        string GetUsing();
        string GetVars();
        string GetClasses();

        void AddUsing(string namespaceName);

        bool IsAvailable { get; }
        string FailureReason { get; }
    }
}

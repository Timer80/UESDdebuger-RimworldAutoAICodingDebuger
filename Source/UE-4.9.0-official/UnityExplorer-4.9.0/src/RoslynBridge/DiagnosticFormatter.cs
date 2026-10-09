using System.Text;
using Microsoft.CodeAnalysis;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>把 Roslyn 诊断格式化成像 mcs 那样的 `(行,列): error CSxxxx: 消息`，控制台/日志观感不变。</summary>
    public static class DiagnosticFormatter
    {
        public static string Format(Diagnostic d, string fileName = null)
        {
            FileLinePositionSpan span = d.Location.GetLineSpan();
            int line = span.StartLinePosition.Line + 1;
            int col = span.StartLinePosition.Character + 1;
            string id = d.Id;
            string sev = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            return $"({line},{col}): {sev} {id}: {d.GetMessage()}";
        }

        public static string FormatAll(System.Collections.Generic.IEnumerable<Diagnostic> diags,
                                       string fileName = null,
                                       bool errorsOnly = true)
        {
            var sb = new StringBuilder();
            foreach (Diagnostic d in diags)
            {
                if (errorsOnly && d.Severity != DiagnosticSeverity.Error) continue;
                sb.AppendLine(Format(d, fileName));
            }
            return sb.ToString().TrimEnd();
        }
    }
}

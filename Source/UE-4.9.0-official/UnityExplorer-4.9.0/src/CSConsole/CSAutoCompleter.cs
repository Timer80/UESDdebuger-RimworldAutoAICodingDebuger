using System.Text;
using UnityExplorer.CSConsole.Lexers;
using UnityExplorer.UI.Panels;
using UnityExplorer.UI.Widgets.AutoComplete;
using UniverseLib.UI.Models;

namespace UnityExplorer.CSConsole
{
    public class CSAutoCompleter : ISuggestionProvider
    {
        public InputFieldRef InputField => ConsoleController.Input;

        public bool AnchorToCaretPosition => true;

        bool ISuggestionProvider.AllowNavigation => true;

        public void OnSuggestionClicked(Suggestion suggestion)
        {
            ConsoleController.InsertSuggestionAtCaret(suggestion.UnderlyingValue);
            AutoCompleteModal.Instance.ReleaseOwnership(this);
        }

        private readonly HashSet<char> delimiters = new()
        {
            '{',
            '}',
            ',',
            ';',
            '<',
            '>',
            '(',
            ')',
            '[',
            ']',
            '=',
            '|',
            '&',
            '?'
        };

        private readonly List<Suggestion> suggestions = new();

        // R1（任务 9 复审）：三来源合并后**按插入文本去重**（保序：先到者胜）。等价判据 = 插入文本
        // **逐字相同**（插到光标处得到的源码完全一致），差异只在**高亮标签**（求值器来源 = 前缀青色高亮、
        // 命名空间来源 = 灰色）⇒ 保留先到者（求值器那条更精确：它标出了已输入的前缀）。
        // **不做静默折叠**：被折叠条数记在 `LastFoldedDuplicates`（观测点）——将来查"为什么少了候选"时
        // 能看到"折叠了几条"，而不是候选凭空消失。
        private readonly HashSet<string> seenCompletions = new();

        /// <summary>最近一次 <see cref="CheckAutocompletes"/> 中因"插入文本相同"被折叠掉的候选数。</summary>
        public int LastFoldedDuplicates { get; private set; }

        /// <summary>加入候选：插入文本重复即折叠（先到者胜）并计数；不重复则保序追加。</summary>
        private void AddSuggestion(string label, string value)
        {
            if (!seenCompletions.Add(value))
            {
                LastFoldedDuplicates++;
                return;
            }
            suggestions.Add(new Suggestion(label, value));
        }

        public void CheckAutocompletes()
        {
            if (string.IsNullOrEmpty(InputField.Text))
            {
                AutoCompleteModal.Instance.ReleaseOwnership(this);
                return;
            }

            suggestions.Clear();
            seenCompletions.Clear();
            LastFoldedDuplicates = 0;

            int caret = Math.Max(0, Math.Min(InputField.Text.Length - 1, InputField.Component.caretPosition - 1));
            int startIdx = caret;

            // If the character at the caret index is whitespace or delimiter,
            // or if the next character (if it exists) is not whitespace,
            // then we don't want to provide suggestions.
            if (char.IsWhiteSpace(InputField.Text[caret])
                || delimiters.Contains(InputField.Text[caret])
                || (InputField.Text.Length > caret + 1 && !char.IsWhiteSpace(InputField.Text[caret + 1])))
            {
                AutoCompleteModal.Instance.ReleaseOwnership(this);
                return;
            }

            // get the current composition string (from caret back to last delimiter)
            while (startIdx > 0)
            {
                startIdx--;
                char c = InputField.Text[startIdx];
                if (delimiters.Contains(c) || char.IsWhiteSpace(c))
                {
                    startIdx++;
                    break;
                }
            }
            string input = InputField.Text.Substring(startIdx, caret - startIdx + 1);

            // Get evaluator completions (mcs or Roslyn; both behind IScriptEvaluator)
            // R2（任务 9 复审）：调用点也兜一层 —— 求值器可能是 mcs 实现、`Evaluator` 也可能尚未就绪
            // （键入早于 ResetConsole 时）。异常降级为"本帧少这一个来源的候选 + 一条日志"，绝不打断输入。

            string prefix = "";
            string[] evaluatorCompletions = null;
            try
            {
                if (ConsoleController.Evaluator != null)
                    evaluatorCompletions = ConsoleController.Evaluator.GetCompletions(input, out prefix);
            }
            catch (Exception ex)
            {
                prefix = "";
                ExplorerCore.LogWarning("[CSConsole] 求值器补全异常（本帧无该来源候选）："
                    + ex.GetType().Name + ": " + ex.Message);
            }

            if (evaluatorCompletions != null && evaluatorCompletions.Length > 0)
            {
                foreach (string completion in evaluatorCompletions)
                    AddSuggestion(GetHighlightString(prefix, completion), completion);
            }

            // Get manual namespace completions

            foreach (string ns in ReflectionUtility.AllNamespaces)
            {
                if (ns.StartsWith(input))
                {
                    if (!namespaceHighlights.ContainsKey(ns))
                        namespaceHighlights.Add(ns, $"<color=#CCCCCC>{ns}</color>");

                    string completion = ns.Substring(input.Length, ns.Length - input.Length);
                    AddSuggestion(namespaceHighlights[ns], completion);   // R1：与求值器来源同名时折叠（先到者胜）
                }
            }

            // Get manual keyword completions

            foreach (string kw in KeywordLexer.keywords)
            {
                if (kw.StartsWith(input))// && kw.Length > input.Length)
                {
                    if (!keywordHighlights.ContainsKey(kw))
                        keywordHighlights.Add(kw, $"<color=#{SignatureHighlighter.keywordBlueHex}>{kw}</color>");

                    string completion = kw.Substring(input.Length, kw.Length - input.Length);
                    AddSuggestion(keywordHighlights[kw], completion);
                }
            }

            if (suggestions.Any())
            {
                AutoCompleteModal.TakeOwnership(this);
                AutoCompleteModal.Instance.SetSuggestions(suggestions);
            }
            else
            {
                AutoCompleteModal.Instance.ReleaseOwnership(this);
            }
        }


        private readonly Dictionary<string, string> namespaceHighlights = new();

        private readonly Dictionary<string, string> keywordHighlights = new();

        private readonly StringBuilder highlightBuilder = new();
        private const string OPEN_HIGHLIGHT = "<color=cyan>";

        private string GetHighlightString(string prefix, string completion)
        {
            highlightBuilder.Clear();
            highlightBuilder.Append(OPEN_HIGHLIGHT);
            highlightBuilder.Append(prefix);
            highlightBuilder.Append(SignatureHighlighter.CLOSE_COLOR);
            highlightBuilder.Append(completion);
            return highlightBuilder.ToString();
        }
    }
}

using System.Collections;
using System.Text;
using UnityExplorer.Config;
using UnityExplorer.RoslynBridge;
using UnityExplorer.UI;
using UnityExplorer.UI.Panels;
using UniverseLib.Input;
using UniverseLib.UI.Models;

namespace UnityExplorer.CSConsole
{
    public static class ConsoleController
    {
        public static IScriptEvaluator Evaluator { get; private set; }


        /// <summary>本次会话实际生效的求值器：true=Roslyn，false=mcs（回退）。</summary>
        public static bool UsingRoslyn { get; private set; }

        public static LexerBuilder Lexer { get; private set; }
        public static CSAutoCompleter Completer { get; private set; }

        public static bool SRENotSupported { get; private set; }
        public static int LastCaretPosition { get; private set; }
        public static float DefaultInputFieldAlpha { get; set; }

        public static bool EnableCtrlRShortcut { get; private set; } = true;
        public static bool EnableAutoIndent { get; private set; } = true;
        public static bool EnableSuggestions { get; private set; } = true;

        public static CSConsolePanel Panel => UIManager.GetPanel<CSConsolePanel>(UIManager.Panels.CSConsole);
        public static InputFieldRef Input => Panel.Input;

        public static string ScriptsFolder => Path.Combine(ExplorerCore.ExplorerFolder, "Scripts");

        static HashSet<string> usingDirectives;
        static StringBuilder evaluatorOutput;
        static StringWriter evaluatorStringWriter;
        static float timeOfLastCtrlR;

        static bool settingCaretCoroutine;
        static string previousInput;
        static int previousContentLength = 0;

#if CPP
        // IL2CPP 专属命名空间。本体/通用默认表已迁至 DefaultUsings.BuiltIn（可经配置覆盖），
        // 此处仅保留 Unhollower 两项：它们不在 BuiltIn 里，配置覆盖也不应丢失。
        static readonly string[] DefaultUsing = new string[]
        {
            "UnhollowerBaseLib",
            "UnhollowerRuntimeLib",
        };
#endif

        const int CSCONSOLE_LINEHEIGHT = 18;

        public static void Init()
        {
            try
            {
                // 求值器自检：任一实现（mcs / Roslyn）都必须能编译一个平凡表达式。
                // 追加 B（2026-10-09 根因清扫轮）：**探针已并入 ResetConsoleCore**——每次构造求值器
                // （Init、面板 Reset、配置切换）都跑，失败即按结果回退；此处只需看终态：
                // Disable = 两条路都编不了（原 B1 语义：Roslyn 探针失败先回退 mcs 再探针，两路都失败才禁用）。
                EvaluatorChoice choice = ResetConsoleCore(logSuccess: false, forceMcs: false);
                if (choice == EvaluatorChoice.Disable)
                    throw new Exception(string.IsNullOrEmpty(LastProbeDiagnostics)
                        ? "evaluator probe failed" : LastProbeDiagnostics);
            }
            catch (Exception ex)
            {
                DisableConsole(ex);
                return;
            }

            // Setup console
            Lexer = new LexerBuilder();
            Completer = new CSAutoCompleter();

            SetupHelpInteraction();

            Panel.OnInputChanged += OnInputChanged;
            Panel.InputScroller.OnScroll += OnInputScrolled;
            Panel.OnCompileClicked += Evaluate;
            Panel.OnResetClicked += ResetConsole;
            Panel.OnHelpDropdownChanged += HelpSelected;
            Panel.OnAutoIndentToggled += OnToggleAutoIndent;
            Panel.OnCtrlRToggled += OnToggleCtrlRShortcut;
            Panel.OnSuggestionsToggled += OnToggleSuggestions;
            Panel.OnPanelResized += OnInputScrolled;

            // Run startup script
            try
            {
                if (!Directory.Exists(ScriptsFolder))
                    Directory.CreateDirectory(ScriptsFolder);

                string startupPath = Path.Combine(ScriptsFolder, "startup.cs");
                if (File.Exists(startupPath))
                {
                    ExplorerCore.Log($"Executing startup script from '{startupPath}'...");
                    string text = File.ReadAllText(startupPath);
                    Input.Text = text;
                    Evaluate();
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"Exception executing startup script: {ex}");
            }
        }


        #region Evaluating

        static void GenerateTextWriter()
        {
            evaluatorOutput = new StringBuilder();
            evaluatorStringWriter = new StringWriter(evaluatorOutput);
        }

        public static void ResetConsole() => ResetConsole(true);

        /// <summary>
        /// 面板 Reset / 配置切换入口。追加 B：**现在就探针**（旧实现只在 Init 探针一次，
        /// 后续 Reset 只新建求值器 ⇒ 可能留下"Roslyn 已就绪但实际编译不了"的静默坏态）。
        /// 失败到 Disable 时不 Disable 整个控制台（面板重置不该把控制台打死），只打错误日志。
        /// </summary>
        public static void ResetConsole(bool logSuccess = true, bool forceMcs = false)
        {
            if (SRENotSupported)
                return;

            EvaluatorChoice choice = ResetConsoleCore(logSuccess, forceMcs);
            if (choice == EvaluatorChoice.Disable)
                ExplorerCore.LogError("[CSConsole] 重置失败：两条求值器都不可用（"
                    + (LastProbeDiagnostics ?? "evaluator probe failed") + "）");
        }

        /// <summary>最近一次探针的诊断文本（Init 把它交给 DisableConsole；面板日志也用它）。</summary>
        static string LastProbeDiagnostics;

        /// <summary>
        /// 构造求值器 + 预置默认 using + **探针**，返回终态（追加 E 的纯决策函数在此被喂入三个输入）。
        /// 顺序（追加 D 明确要求）：先按引用集可用性组装**有效** using → 再构造 → 再探针；
        /// 探针失败且初始是 Roslyn → 以 forceMcs 递归一次（forceMcs=true 时不再进 Roslyn 分支，无深度风险）。
        /// </summary>
        static EvaluatorChoice ResetConsoleCore(bool logSuccess, bool forceMcs)
        {
            // 替换/卸载会话时 Dispose（任务 6 生命周期契约：Roslyn 侧转发 s_session.Dispose 摘钩）
            Evaluator?.Dispose();

            // mcs 路径（回退）需要 TextWriter 传给 ScriptEvaluator 构造，Roslyn 路径不用。
            // 无条件生成，避免回退分支拿到 null writer。
            GenerateTextWriter();

            string blacklist = ConfigManager.CSConsole_Assembly_Blacklist.Value;
            bool wantRoslyn = !forceMcs && ConfigManager.CSharp_Console_Use_Roslyn.Value;
            // 配置判定只在一处（EvaluatorSelector）；用局部变量接收，避免恒假 const 触发 CS0162 不可达告警。
            bool roslynCompiled = EvaluatorSelector.RoslynCompiledIn;

            // 本会话的默认 using 候选：配置项（默认值 = DefaultUsings.BuiltIn 12 项）+ CPP 追加项
            List<string> defaultUsings = DefaultUsings.Parse(ConfigManager.CSharp_Console_Default_Usings.Value);
#if CPP
            foreach (string use in DefaultUsing)
                if (!defaultUsings.Contains(use)) defaultUsings.Add(use);
#endif

            // 输入 ③：载荷是否就绪（只有真要走 Roslyn 时才付这个代价）
            bool payloadLoaded = false;
#if STANDALONE_MONO
            if (roslynCompiled && wantRoslyn)
            {
                string payloadDir = Path.Combine(ExplorerCore.ExplorerFolder, "Roslyn");
                payloadLoaded = RoslynAssemblyLoader.EnsureLoaded(payloadDir);
            }
#endif

            // 输入 ①②③ → 纯函数决策（追加 E：真值表离线可断言）
            EvaluatorChoice choice = EvaluatorSelector.ChooseInitial(roslynCompiled, wantRoslyn, payloadLoaded);

            if (choice == EvaluatorChoice.Roslyn)
            {
#if STANDALONE_MONO
                ReferenceSet refs = ReferenceScanner.Collect(blacklist, out string report, out List<string> unavailable);

                // ── B1 护栏（数据驱动，非硬编码）：引用集缺件 ⇒ 把对应默认 using 先从本会话剔除 ──
                // 不剔除的实测症状：`UniverseLib` 缺引用时**每一次提交**都带 `using UniverseLib;` 去编译，
                // 连 `new[]{1,2,3}.Sum()` 都报 CS0246 'UniverseLib'，而求值器仍是 Roslyn
                // ⇒ 用户"看得见 Roslyn 却什么都写不了、报错还指向他从没写过的命名空间"。
                if (unavailable.Count > 0)
                {
                    var detail = new StringBuilder();
                    foreach (string ns in unavailable)
                    {
                        if (detail.Length > 0) detail.Append("、");
                        detail.Append(ns).Append("（需 ").Append(DefaultUsings.OwnerOf(ns) ?? "?").Append("）");
                    }
                    ExplorerCore.LogWarning("[CSConsole] 因程序集不可用，已从本次会话默认 using 中剔除："
                        + detail + "；其余默认项不受影响。" + report);
                    foreach (string ns in unavailable) defaultUsings.Remove(ns);
                }

                // A1（合并修复轮）：Allow_Mod_Type_Resolution 落位为第三参。铁律：任务 13 自愈器落地前
                // 该开关不改变引用集行为（实现侧 RoslynScriptEvaluator 仅存储，不预加 ModAssemblyPaths）。
                Evaluator = new RoslynScriptEvaluator(refs.Paths, blacklist,
                    ConfigManager.CSharp_Console_Allow_Mod_Type_Resolution.Value);
                ExplorerCore.Log($"[CSConsole] Roslyn 求值器就绪（{report}）");
#endif
            }
            else
            {
                Evaluator = new ScriptEvaluator(evaluatorStringWriter)
                {
                    InteractiveBaseClass = typeof(ScriptInteraction)
                };
                if (roslynCompiled && wantRoslyn && !payloadLoaded)
                    ExplorerCore.LogWarning($"[CSConsole] 回退 mcs：{RoslynAssemblyLoader.FailureReason}");
                else if (roslynCompiled && !forceMcs)   // C2：仅配置显式关闭 Roslyn 时打此行；forceMcs 回退路径
                                                        // 由"Roslyn 探针失败，回退 mcs"行覆盖，此处静默以免误标。
                    ExplorerCore.Log("[CSConsole] 使用 mcs 求值器（配置关闭 Roslyn）");
                else if (!roslynCompiled)
                    ExplorerCore.Log("[CSConsole] 使用 mcs 求值器（本配置未编入 Roslyn 实现）");
            }

            // ── 预置 using：Roslyn 侧一次校验编译（B4：旧路径 = 每项一次完整提交 + Assembly.Load），
            //    mcs 侧沿用逐条 Run（其 using 处理不走本机制）──
            usingDirectives = new HashSet<string>();
#if STANDALONE_MONO
            var roslynEvaluator = Evaluator as RoslynScriptEvaluator;
            if (roslynEvaluator != null)
            {
                string usingDiag;
                List<string> accepted = roslynEvaluator.PrimeUsings(defaultUsings, out usingDiag);
                foreach (string ns in accepted) usingDirectives.Add(ns);
                if (!string.IsNullOrEmpty(usingDiag))
                    ExplorerCore.LogWarning("[CSConsole] 默认 using 预置：" + usingDiag);
            }
            else
#endif
            {
                foreach (string use in defaultUsings)
                    AddUsing(use);
            }

            // ── 探针（追加 B：每次 Reset 都跑；结果驱动回退）──
            string probeDiag;
            bool probeOk = Evaluator.Evaluate("0 == 0", out probeDiag);
            LastProbeDiagnostics = probeDiag;

            EvaluatorChoice after = EvaluatorSelector.ChooseAfterProbe(choice, probeOk);
            if (after == EvaluatorChoice.Mcs && choice == EvaluatorChoice.Roslyn)
            {
                ExplorerCore.LogWarning("[CSConsole] Roslyn 探针失败，回退 mcs："
                    + (string.IsNullOrEmpty(probeDiag) ? "evaluator probe failed" : probeDiag));
                return ResetConsoleCore(logSuccess, forceMcs: true);   // 一次性回退，见方法注释
            }
            // R4（复审必修）：Disable **不再早退** —— 统一收尾（更新 UsingRoslyn + 打"求值器生效"行 +
            // 类型交叉核对）。早退会让 Disable 时 UsingRoslyn 保留旧值（可能与实际已死的求值器不符），
            // 且丢掉"求值器生效"锚点（与追加 A/C 的承诺不一致）。
            // 失败日志由调用方负责（ResetConsole → LogError；Init → throw → DisableConsole），此处不重复打。

            // ── 生效判定（追加 A/C）──
            // 追加 C：`UsingRoslyn` 是**唯一**可靠的"当前生效的是 Roslyn"判据（CS1525/record 报错都不是
            // 指纹：mcs 对未知关键字同样报 CS1525 风格错误，实测那次 `record` 失败正落在 mcs 生效区间）。
            UsingRoslyn = EvaluatorSelector.UsingRoslyn(after);
#if STANDALONE_MONO
            // 运行时交叉核对：标志与**实际类型**不符即告警（把"日志说 Roslyn 而实际不是"这类归因陷阱堵死）
            if ((Evaluator is RoslynScriptEvaluator) != UsingRoslyn)
                ExplorerCore.LogWarning("[CSConsole] 内部不一致：UsingRoslyn=" + UsingRoslyn
                    + " 但实际求值器是 " + Evaluator.GetType().Name + "，请报告此日志");
#endif
            // 追加 A：探针判定完成后打一行"最终生效者"，类型名现算（勿硬编码）
            ExplorerCore.Log("[CSConsole] 求值器生效：" + EvaluatorSelector.Describe(after)
                + " (" + Evaluator.GetType().Name + ")");

            if (logSuccess)
                ExplorerCore.Log($"C# Console reset");//. Using directives:\r\n{Evaluator.GetUsing()}");

            return after;
        }

        public static void AddUsing(string assemblyName)
        {
            if (!usingDirectives.Contains(assemblyName))
            {
                Evaluate($"using {assemblyName};", true);
                usingDirectives.Add(assemblyName);
            }
        }

        public static void Evaluate()
        {
            if (SRENotSupported)
                return;

            Evaluate(Input.Text);
        }

        public static void Evaluate(string input, bool supressLog = false)
        {
            if (SRENotSupported)
                return;

            try
            {
                if (Evaluator.Evaluate(input, out string output))
                {
                    if (!string.IsNullOrEmpty(output))
                        ExplorerCore.Log($"Invoked REPL, result: {output}");
                    else if (!supressLog)
                        ExplorerCore.Log("Code compiled without errors.");
                }
                else if (!supressLog)
                {
                    ExplorerCore.LogWarning($"Unable to compile the code.{Environment.NewLine}{output}");
                }
            }
            catch (Exception ex)
            {
                if (!supressLog)
                    ExplorerCore.LogWarning(ex);
            }
        }

        #endregion


        #region Update loop and event listeners

        public static void Update()
        {
            if (SRENotSupported)
                return;

            if (!InputManager.GetKey(KeyCode.LeftControl) && !InputManager.GetKey(KeyCode.RightControl))
            {
                if (InputManager.GetKeyDown(KeyCode.Home))
                    JumpToStartOrEndOfLine(true);
                else if (InputManager.GetKeyDown(KeyCode.End))
                    JumpToStartOrEndOfLine(false);
            }

            UpdateCaret(out bool caretMoved);

            if (!settingCaretCoroutine && EnableSuggestions)
            {
                if (AutoCompleteModal.CheckEscape(Completer))
                {
                    OnAutocompleteEscaped();
                    return;
                }

                if (caretMoved)
                    AutoCompleteModal.Instance.ReleaseOwnership(Completer);
            }

            if (EnableCtrlRShortcut
                && (InputManager.GetKey(KeyCode.LeftControl) || InputManager.GetKey(KeyCode.RightControl))
                && InputManager.GetKeyDown(KeyCode.R)
                && timeOfLastCtrlR.OccuredEarlierThanDefault())
            {
                timeOfLastCtrlR = Time.realtimeSinceStartup;
                Evaluate(Panel.Input.Text);
            }
        }

        static void OnInputScrolled() => HighlightVisibleInput(out _);

        static void OnInputChanged(string value)
        {
            if (SRENotSupported)
                return;

            // prevent escape wiping input
            if (InputManager.GetKeyDown(KeyCode.Escape))
            {
                Input.Text = previousInput;

                if (EnableSuggestions && AutoCompleteModal.CheckEscape(Completer))
                    OnAutocompleteEscaped();

                return;
            }

            previousInput = value;

            if (EnableSuggestions && AutoCompleteModal.CheckEnter(Completer))
                OnAutocompleteEnter();

            if (!settingCaretCoroutine)
            {
                if (EnableAutoIndent)
                    DoAutoIndent();
            }

            HighlightVisibleInput(out bool inStringOrComment);

            if (!settingCaretCoroutine)
            {
                if (EnableSuggestions)
                {
                    if (inStringOrComment)
                        AutoCompleteModal.Instance.ReleaseOwnership(Completer);
                    else
                        Completer.CheckAutocompletes();
                }
            }

            UpdateCaret(out _);
        }

        static void OnToggleAutoIndent(bool value)
        {
            EnableAutoIndent = value;
        }

        static void OnToggleCtrlRShortcut(bool value)
        {
            EnableCtrlRShortcut = value;
        }

        static void OnToggleSuggestions(bool value)
        {
            EnableSuggestions = value;
        }

        #endregion


        #region Caret position

        static void UpdateCaret(out bool caretMoved)
        {
            int prevCaret = LastCaretPosition;
            caretMoved = false;

            // Override up/down arrow movement when autocompleting
            if (EnableSuggestions && AutoCompleteModal.CheckNavigation(Completer))
            {
                Input.Component.caretPosition = LastCaretPosition;
                return;
            }

            if (Input.Component.isFocused)
            {
                LastCaretPosition = Input.Component.caretPosition;
                caretMoved = LastCaretPosition != prevCaret;
            }

            if (Input.Text.Length == 0)
                return;

            // If caret moved, ensure caret is visible in the viewport
            if (caretMoved)
            {
                UICharInfo charInfo = Input.TextGenerator.characters[LastCaretPosition];
                float charTop = charInfo.cursorPos.y;
                float charBot = charTop - CSCONSOLE_LINEHEIGHT;

                float viewportMin = Input.Transform.rect.height - Input.Transform.anchoredPosition.y - (Input.Transform.rect.height * 0.5f);
                float viewportMax = viewportMin - Panel.InputScroller.ViewportRect.rect.height;

                float diff = 0f;
                if (charTop > viewportMin)
                    diff = charTop - viewportMin;
                else if (charBot < viewportMax)
                    diff = charBot - viewportMax;

                if (Math.Abs(diff) > 1)
                {
                    RectTransform rect = Input.Transform;
                    rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, rect.anchoredPosition.y - diff);
                }
            }
        }

        public static void SetCaretPosition(int caretPosition)
        {
            Input.Component.caretPosition = caretPosition;

            // Fix to make sure we always really set the caret position.
            // Yields a frame and fixes text-selection issues.
            settingCaretCoroutine = true;
            Input.Component.readOnly = true;
            RuntimeHelper.StartCoroutine(DoSetCaretCoroutine(caretPosition));
        }

        static IEnumerator DoSetCaretCoroutine(int caretPosition)
        {
            Color color = Input.Component.selectionColor;
            color.a = 0f;
            Input.Component.selectionColor = color;

            EventSystemHelper.SetSelectionGuard(false);
            Input.Component.Select();

            yield return null; // ~~~~~~~ YIELD FRAME ~~~~~~~~~

            Input.Component.caretPosition = caretPosition;
            Input.Component.selectionFocusPosition = caretPosition;
            LastCaretPosition = Input.Component.caretPosition;

            color.a = DefaultInputFieldAlpha;
            Input.Component.selectionColor = color;

            Input.Component.readOnly = false;
            settingCaretCoroutine = false;
        }

        // For Home and End keys
        static void JumpToStartOrEndOfLine(bool toStart)
        {
            // Determine the current and next line
            UILineInfo thisline = default;
            UILineInfo? nextLine = null;
            for (int i = 0; i < Input.Component.cachedInputTextGenerator.lineCount; i++)
            {
                UILineInfo line = Input.Component.cachedInputTextGenerator.lines[i];

                if (line.startCharIdx > LastCaretPosition)
                {
                    nextLine = line;
                    break;
                }
                thisline = line;
            }

            if (toStart)
            {
                // Determine where the indented text begins
                int endOfLine = nextLine == null ? Input.Text.Length : nextLine.Value.startCharIdx;
                int indentedStart = thisline.startCharIdx;
                while (indentedStart < endOfLine - 1 && char.IsWhiteSpace(Input.Text[indentedStart]))
                    indentedStart++;

                // Jump to either the true start or the non-whitespace position,
                // depending on which one we are not at.
                if (LastCaretPosition == indentedStart)
                    SetCaretPosition(thisline.startCharIdx);
                else 
                    SetCaretPosition(indentedStart);
            }
            else
            {
                // If there is no next line, jump to the end of this line (+1, to the invisible next character position)
                if (nextLine == null)
                    SetCaretPosition(Input.Text.Length);
                else // jump to the next line start index - 1, ie. end of this line
                    SetCaretPosition(nextLine.Value.startCharIdx - 1);
            }
        }

        #endregion


        #region Lexer Highlighting

        private static void HighlightVisibleInput(out bool inStringOrComment)
        {
            inStringOrComment = false;
            if (string.IsNullOrEmpty(Input.Text))
            {
                Panel.HighlightText.text = "";
                Panel.LineNumberText.text = "1";
                return;
            }

            // Calculate the visible lines

            int topLine = -1;
            int bottomLine = -1;

            // the top and bottom position of the viewport in relation to the text height
            // they need the half-height adjustment to normalize against the 'line.topY' value.
            float viewportMin = Input.Transform.rect.height - Input.Transform.anchoredPosition.y - (Input.Transform.rect.height * 0.5f);
            float viewportMax = viewportMin - Panel.InputScroller.ViewportRect.rect.height;

            for (int i = 0; i < Input.TextGenerator.lineCount; i++)
            {
                UILineInfo line = Input.TextGenerator.lines[i];
                // if not set the top line yet, and top of line is below the viewport top
                if (topLine == -1 && line.topY <= viewportMin)
                    topLine = i;
                // if bottom of line is below the viewport bottom
                if ((line.topY - line.height) >= viewportMax)
                    bottomLine = i;
            }

            topLine = Math.Max(0, topLine - 1);
            bottomLine = Math.Min(Input.TextGenerator.lineCount - 1, bottomLine + 1);

            int startIdx = Input.TextGenerator.lines[topLine].startCharIdx;
            int endIdx = (bottomLine >= Input.TextGenerator.lineCount - 1)
                ? Input.Text.Length - 1
                : (Input.TextGenerator.lines[bottomLine + 1].startCharIdx - 1);


            // Highlight the visible text with the LexerBuilder

            Panel.HighlightText.text = Lexer.BuildHighlightedString(Input.Text, startIdx, endIdx, topLine, LastCaretPosition, out inStringOrComment);

            // Set the line numbers

            // determine true starting line number (not the same as the cached TextGenerator line numbers)
            int realStartLine = 0;
            for (int i = 0; i < startIdx; i++)
            {
                if (LexerBuilder.IsNewLine(Input.Text[i]))
                    realStartLine++;
            }
            realStartLine++;
            char lastPrev = '\n';

            StringBuilder sb = new();

            // append leading new lines for spacing (no point rendering line numbers we cant see)
            for (int i = 0; i < topLine; i++)
                sb.Append('\n');

            // append the displayed line numbers
            for (int i = topLine; i <= bottomLine; i++)
            {
                if (i > 0)
                    lastPrev = Input.Text[Input.TextGenerator.lines[i].startCharIdx - 1];

                // previous line ended with a newline character, this is an actual new line.
                if (LexerBuilder.IsNewLine(lastPrev))
                {
                    sb.Append(realStartLine.ToString());
                    realStartLine++;
                }

                sb.Append('\n');
            }

            Panel.LineNumberText.text = sb.ToString();

            return;
        }

        #endregion


        #region Autocompletes

        public static void InsertSuggestionAtCaret(string suggestion)
        {
            settingCaretCoroutine = true;
            Input.Text = Input.Text.Insert(LastCaretPosition, suggestion);

            SetCaretPosition(LastCaretPosition + suggestion.Length);
            LastCaretPosition = Input.Component.caretPosition;
        }

        private static void OnAutocompleteEnter()
        {
            // Remove the new line
            int lastIdx = Input.Component.caretPosition - 1;
            Input.Text = Input.Text.Remove(lastIdx, 1);

            // Use the selected suggestion
            Input.Component.caretPosition = LastCaretPosition;
            Completer.OnSuggestionClicked(AutoCompleteModal.SelectedSuggestion);
        }

        private static void OnAutocompleteEscaped()
        {
            AutoCompleteModal.Instance.ReleaseOwnership(Completer);
            SetCaretPosition(LastCaretPosition);
        }


        #endregion


        #region Auto indenting

        private static void DoAutoIndent()
        {
            if (Input.Text.Length > previousContentLength)
            {
                int inc = Input.Text.Length - previousContentLength;

                if (inc == 1)
                {
                    int caret = Input.Component.caretPosition;
                    Input.Text = Lexer.IndentCharacter(Input.Text, ref caret);
                    Input.Component.caretPosition = caret;
                    LastCaretPosition = caret;
                }
                else
                {
                    // todo indenting for copy+pasted content

                    //ExplorerCore.Log("Content increased by " + inc);
                    //var comp = Input.Text.Substring(PreviousCaretPosition, inc);
                    //ExplorerCore.Log("composition string: " + comp);
                }
            }

            previousContentLength = Input.Text.Length;
        }

        #endregion


        #region "Help" interaction

        private static void DisableConsole(Exception ex)
        {
            // 可观测性（任务 8 架构修复轮）：禁用原因必须进日志——此前只写 Input.Text，
            // 调试者只看到"控制台禁用"却拿不到异常（验收时它把 TypeLoadException 根因藏了一整轮）。
            // Input.Text 行为保持不变（那是给用户看的，日志是给调试者的）。
            ExplorerCore.LogWarning($"[CSConsole] 控制台已禁用：{ex}");
            SRENotSupported = true;
            Input.Component.readOnly = true;
            Input.Component.textComponent.color = "5d8556".ToColor();

            if (ex is NotSupportedException)
            {
                Input.Text = $@"The C# Console has been disabled because System.Reflection.Emit threw a NotSupportedException.

Easy, dirty fix: (will likely break on game updates)
    * Download the corlibs for the game's Unity version from here: https://unity.bepinex.dev/corlibs/
    * Unzip and copy mscorlib.dll (and System.Reflection.Emit DLLs, if present) from the folder
    * Paste and overwrite the files into <Game>_Data/Managed/

With UnityDoorstop: (BepInEx only, or if you use UnityDoorstop + Standalone release):
    * Download the corlibs for the game's Unity version from here: https://unity.bepinex.dev/corlibs/
    * Unzip and copy mscorlib.dll (and System.Reflection.Emit DLLs, if present) from the folder
    * Find the folder which contains doorstop_config.ini (the game folder, or your r2modman/ThunderstoreModManager profile folder)
    * Make a subfolder called 'corlibs' inside this folder.
    * Paste the DLLs inside the corlibs folder.
    * In doorstop_config.ini, set 'dllSearchPathOverride=corlibs'.

Doorstop example:
- <Game>\
    - <Game>_Data\...
    - BepInEx\...
    - corlibs\
        - mscorlib.dll
    - doorstop_config.ini (with dllSearchPathOverride=corlibs)
    - <Game>.exe
    - winhttp.dll";
            }
            else
            {
                Input.Text = $"The C# Console has been disabled. {ex}";
            }
        }

        private static readonly Dictionary<string, string> helpDict = new();

        public static void SetupHelpInteraction()
        {
            Dropdown drop = Panel.HelpDropdown;

            helpDict.Add("Help", "");
            helpDict.Add("Usings", HELP_USINGS);
            helpDict.Add("REPL", HELP_REPL);
            helpDict.Add("Classes", HELP_CLASSES);
            helpDict.Add("Coroutines", HELP_COROUTINES);

            foreach (KeyValuePair<string, string> opt in helpDict)
                drop.options.Add(new Dropdown.OptionData(opt.Key));
        }

        public static void HelpSelected(int index)
        {
            if (index == 0)
                return;

            KeyValuePair<string, string> helpText = helpDict.ElementAt(index);

            Input.Text = helpText.Value;

            Panel.HelpDropdown.value = 0;
        }


        internal const string STARTUP_TEXT = @"<color=#5d8556>// Welcome to the UnityExplorer C# Console!

// It is recommended to use the Log panel (or a console log window) while using this tool.
// Use the Help dropdown to see detailed examples of how to use the console.

// To execute a script automatically on startup, put the script at 'sinai-dev-UnityExplorer\Scripts\startup.cs'</color>";

        internal const string HELP_USINGS = @"// You can add a using directive to any namespace, but you must compile for it to take effect.
// It will remain in effect until you Reset the console.
using UnityEngine.UI;

// To see your current usings, use the ""GetUsing();"" helper.
// Note: You cannot add usings and evaluate REPL at the same time.";

        internal const string HELP_REPL = @"/* REPL (Read-Evaluate-Print-Loop) is a way to execute code immediately.
 * REPL code cannot contain any using directives or classes.
 * The return value of the last line of your REPL will be printed to the log.
 * Variables defined in REPL will exist until you Reset the console.
*/

// eg: This code would print 'Hello, World!', and then print 6 as the return value.
Log(""Hello, world!"");
var x = 5;
++x;

/* The following helpers are available in REPL mode:
 * CurrentTarget;     - System.Object, the target of the active Inspector tab
 * AllTargets;        - System.Object[], the targets of all Inspector tabs
 * Log(obj);          - prints a message to the console log
 * Inspect(obj);      - inspect the object with the Inspector
 * Inspect(someType); - inspect a Type with static reflection
 * Start(enumerator); - Coroutine, starts the IEnumerator as a Coroutine, and returns the Coroutine.
 * Stop(coroutine);   - stop the Coroutine ONLY if it was started with Start(ienumerator).
 * Copy(obj);         - copies the object to the UnityExplorer Clipboard
 * Paste();           - System.Object, the contents of the Clipboard.
 * GetUsing();        - prints the current using directives to the console log
 * GetVars();         - prints the names and values of the REPL variables you have defined
 * GetClasses();      - prints the names and members of the classes you have defined
 * help;              - the default REPL help command, contains additional helpers.
*/";

        internal const string HELP_CLASSES = @"// Classes you compile will exist until the application closes.
// You can soft-overwrite a class by compiling it again with the same name. The old class will still technically exist in memory.

// Compiled classes can be accessed from both inside and outside this console.
// Note: in IL2CPP, you must declare a Namespace to inject these classes with ClassInjector or it will crash the game.

public class HelloWorld
{
    public static void Main()
    {
        UnityExplorer.ExplorerCore.Log(""Hello, world!"");
    }
}

// In REPL, you could call the example method above with ""HelloWorld.Main();""
// Note: The compiler does not allow you to run REPL code and define classes at the same time.

// In REPL, use the ""GetClasses();"" helper to see the classes you have defined since the last Reset.";

        internal const string HELP_COROUTINES = @"// To start a Coroutine directly, use ""Start(SomeCoroutine());"" in REPL mode.

// To declare a coroutine, you will need to compile it separately. For example:
public class MyCoro
{
    public static IEnumerator Main()
    {
        yield return null;
        UnityExplorer.ExplorerCore.Log(""Hello, world after one frame!"");
    }
}
// To run this Coroutine in REPL, it would look like ""Start(MyCoro.Main());""";

        #endregion
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Mono.Debugger.Soft;

namespace McpRimDebug
{
    // ================================================================
    // 表达式 AST
    // ================================================================

    /// <summary>表达式 AST 节点类型。</summary>
    public enum ExprNodeKind
    {
        /// <summary>字面量（字符串/数字/null/true/false；Text 保存原始文本）。</summary>
        Literal,

        /// <summary>标识符（局部变量/实参/静态类型全名的起点；Text 保存名字）。</summary>
        Identifier,

        /// <summary>this 引用（当前栈帧的实例）。</summary>
        This,

        /// <summary>成员访问/调用：Target.Text（[args]）。</summary>
        Member,
    }

    /// <summary>
    /// 迷你表达式 AST。成员链（.字段 / .属性 / .方法(args)）表示为嵌套 Member 节点，
    /// 例如 this.def.defName 即 Member(Member(This, "def"), "defName")。
    /// </summary>
    public sealed class ExprNode
    {
        public ExprNodeKind Kind { get; set; }
        public string Text { get; set; }
        public ExprNode Target { get; set; }
        public List<ExprNode> Args { get; set; }
        public bool IsCall { get; set; }

        public static ExprNode Make(ExprNodeKind kind, string text)
        {
            return new ExprNode { Kind = kind, Text = text };
        }

        public static ExprNode MakeMember(ExprNode target, string name, List<ExprNode> args, bool isCall)
        {
            return new ExprNode { Kind = ExprNodeKind.Member, Target = target, Text = name, Args = args, IsCall = isCall };
        }

        /// <summary>重建表达式文本（用于错误提示与测试断言）。</summary>
        public string Describe()
        {
            switch (Kind)
            {
                case ExprNodeKind.Literal:
                case ExprNodeKind.Identifier:
                case ExprNodeKind.This:
                    return Text;
                case ExprNodeKind.Member:
                    var sb = new StringBuilder();
                    sb.Append(Target != null ? Target.Describe() : "?");
                    sb.Append('.').Append(Text);
                    if (IsCall)
                    {
                        sb.Append('(');
                        if (Args != null)
                        {
                            for (int i = 0; i < Args.Count; i++)
                            {
                                if (i > 0)
                                    sb.Append(", ");
                                sb.Append(Args[i].Describe());
                            }
                        }
                        sb.Append(')');
                    }
                    return sb.ToString();
                default:
                    return "?";
            }
        }

        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>表达式语法错误（含出错位置）。</summary>
    public sealed class ExprParseException : Exception
    {
        public int Position { get; private set; }

        public ExprParseException(string message, int position) : base(message)
        {
            Position = position;
        }
    }

    // ================================================================
    // 词法 + 解析
    // ================================================================

    /// <summary>
    /// 迷你表达式解析器。语法（不做完整 C# 语义，无二元运算/索引器/泛型）：
    ///   expr     := unit (member)*
    ///   unit     := '(' expr ')' | 字面量 | 标识符 | 'this'
    ///   member   := '.' 标识符 ( '(' arglist? ')' )?
    ///   arglist  := expr ( ',' expr )*
    /// 根标识符可以是点分形式（静态类型全名，如 Verse.Thing），由求值阶段决定
    /// 解析为局部变量 / 实参 / 静态类型。
    /// 本类不依赖 VM，可独立做解析层单元测试。
    /// </summary>
    public static class ExprParser
    {
        enum TokKind { Ident, Number, String, Dot, LParen, RParen, Comma, End }

        sealed class Token
        {
            public TokKind Kind;
            public string Text;
            public int Pos;
        }

        public static ExprNode Parse(string text)
        {
            if (text == null)
                throw new ExprParseException("表达式为空", 0);
            List<Token> tokens = Tokenize(text);
            var p = new Parser(tokens);
            ExprNode node = p.ParseExpr();
            if (p.Peek().Kind != TokKind.End)
                throw new ExprParseException("意外的记号 '" + p.Peek().Text + "'（位置 " + p.Peek().Pos + "）", p.Peek().Pos);
            return node;
        }

        static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            int n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                if (c == '.')
                {
                    tokens.Add(new Token { Kind = TokKind.Dot, Text = ".", Pos = i });
                    i++;
                    continue;
                }
                if (c == '(')
                {
                    tokens.Add(new Token { Kind = TokKind.LParen, Text = "(", Pos = i });
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    tokens.Add(new Token { Kind = TokKind.RParen, Text = ")", Pos = i });
                    i++;
                    continue;
                }
                if (c == ',')
                {
                    tokens.Add(new Token { Kind = TokKind.Comma, Text = ",", Pos = i });
                    i++;
                    continue;
                }
                if (c == '"')
                {
                    int start = i;
                    i++;
                    bool closed = false;
                    while (i < n)
                    {
                        if (text[i] == '\\' && i + 1 < n)
                        {
                            i += 2;
                            continue;
                        }
                        if (text[i] == '"')
                        {
                            i++;
                            closed = true;
                            break;
                        }
                        i++;
                    }
                    if (!closed)
                        throw new ExprParseException("字符串字面量未闭合", start);
                    tokens.Add(new Token { Kind = TokKind.String, Text = text.Substring(start, i - start), Pos = start });
                    continue;
                }
                if (char.IsDigit(c) || (c == '-' && i + 1 < n && char.IsDigit(text[i + 1])))
                {
                    int start = i;
                    if (text[i] == '-')
                        i++;
                    while (i < n && char.IsDigit(text[i]))
                        i++;
                    if (i < n && text[i] == '.')
                    {
                        i++;
                        while (i < n && char.IsDigit(text[i]))
                            i++;
                    }
                    if (i < n && (text[i] == 'e' || text[i] == 'E'))
                    {
                        i++;
                        if (i < n && (text[i] == '+' || text[i] == '-'))
                            i++;
                        while (i < n && char.IsDigit(text[i]))
                            i++;
                    }
                    if (i < n && "fFdDlL".IndexOf(text[i]) >= 0)
                        i++;
                    tokens.Add(new Token { Kind = TokKind.Number, Text = text.Substring(start, i - start), Pos = start });
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                        i++;
                    tokens.Add(new Token { Kind = TokKind.Ident, Text = text.Substring(start, i - start), Pos = start });
                    continue;
                }
                throw new ExprParseException("无法识别的字符 '" + c + "'", i);
            }
            tokens.Add(new Token { Kind = TokKind.End, Text = "", Pos = n });
            return tokens;
        }

        sealed class Parser
        {
            readonly List<Token> tokens;
            int pos;

            public Parser(List<Token> tokens)
            {
                this.tokens = tokens;
            }

            public Token Peek()
            {
                return tokens[pos];
            }

            Token Next()
            {
                return tokens[pos++];
            }

            public ExprNode ParseExpr()
            {
                ExprNode node = ParseUnit();
                while (Peek().Kind == TokKind.Dot)
                {
                    Next(); // '.'
                    Token name = Peek();
                    if (name.Kind != TokKind.Ident)
                        throw new ExprParseException("成员名必须是标识符（位置 " + name.Pos + "）", name.Pos);
                    Next();

                    bool isCall = false;
                    List<ExprNode> args = null;
                    if (Peek().Kind == TokKind.LParen)
                    {
                        isCall = true;
                        Next(); // '('
                        args = new List<ExprNode>();
                        if (Peek().Kind != TokKind.RParen)
                        {
                            args.Add(ParseUnit());
                            while (Peek().Kind == TokKind.Comma)
                            {
                                Next();
                                args.Add(ParseUnit());
                            }
                        }
                        if (Peek().Kind != TokKind.RParen)
                            throw new ExprParseException("方法调用缺少 ')'（位置 " + Peek().Pos + "）", Peek().Pos);
                        Next(); // ')'
                    }
                    node = ExprNode.MakeMember(node, name.Text, args, isCall);
                }
                return node;
            }

            ExprNode ParseUnit()
            {
                Token t = Peek();
                switch (t.Kind)
                {
                    case TokKind.Ident:
                        Next();
                        if (t.Text == "this")
                            return ExprNode.Make(ExprNodeKind.This, "this");
                        if (t.Text == "null" || t.Text == "true" || t.Text == "false")
                            return ExprNode.Make(ExprNodeKind.Literal, t.Text);
                        return ExprNode.Make(ExprNodeKind.Identifier, t.Text);
                    case TokKind.Number:
                    case TokKind.String:
                        Next();
                        return ExprNode.Make(ExprNodeKind.Literal, t.Text);
                    case TokKind.LParen:
                    {
                        Next();
                        ExprNode inner = ParseExpr();
                        if (Peek().Kind != TokKind.RParen)
                            throw new ExprParseException("缺少 ')'（位置 " + Peek().Pos + "）", Peek().Pos);
                        Next();
                        return inner;
                    }
                    default:
                        throw new ExprParseException("意外的记号 '" + (t.Text != null ? t.Text : t.Kind.ToString())
                            + "'（位置 " + t.Pos + "）", t.Pos);
                }
            }
        }
    }

    // ================================================================
    // 求值
    // ================================================================

    /// <summary>求值上下文：VM + 线程 + 栈帧。帧可能为 null（纯字面量链不需要帧）。</summary>
    public sealed class EvalContext
    {
        public VirtualMachine Vm { get; set; }
        public ThreadMirror Thread { get; set; }
        public StackFrame Frame { get; set; }
    }

    /// <summary>求值失败（可读消息直接进入工具返回）。</summary>
    public sealed class EvalException : Exception
    {
        public EvalException(string message) : base(message)
        {
        }
    }

    /// <summary>求值结果：要么是实例值，要么是静态类型（表达式根为类型且未访问实例成员时）。</summary>
    public sealed class EvalResult
    {
        public Value Value { get; set; }
        public TypeMirror StaticType { get; set; }
    }

    /// <summary>
    /// Task 5（计划阶段 4）：迷你表达式求值器。
    /// 语法（见 ExprParser）：根 = this / 局部变量 / 实参 / 静态类型全名 / 字面量；
    /// 成员链 = .字段（含继承链）→ .属性（getter 调用）→ .方法(args)（同步 InvokeMethod）。
    /// 所有 socket 操作要求调用方已持有命令锁且 VM 挂起（或处于本求值器的挂起-求值-恢复窗口内）；
    /// VMDisconnectedException 向上抛给工具层统一处理。
    /// 本类不依赖 DebugSession，解析层（ExprParser）可独立测试。
    /// </summary>
    public sealed class ExpressionEvaluator
    {
        /// <summary>按文本求值（内部会解析）。</summary>
        public EvalResult Evaluate(EvalContext ctx, string expression)
        {
            ExprNode node;
            try
            {
                node = ExprParser.Parse(expression);
            }
            catch (ExprParseException pe)
            {
                throw new EvalException("表达式语法错误: " + pe.Message + "（位置 " + pe.Position + "）");
            }
            return Evaluate(ctx, node);
        }

        /// <summary>按已解析的 AST 求值。</summary>
        public EvalResult Evaluate(EvalContext ctx, ExprNode node)
        {
            if (ctx == null || ctx.Vm == null)
                throw new EvalException("求值上下文无效（VM 缺失）");

            // 展平成员链：chain[0] 是最外层成员，root 是链底 unit
            var chain = new List<ExprNode>();
            ExprNode cur = node;
            while (cur.Kind == ExprNodeKind.Member)
            {
                chain.Add(cur);
                cur = cur.Target;
            }
            chain.Reverse();
            ExprNode root = cur;

            EvalValue ev;
            int start;
            switch (root.Kind)
            {
                case ExprNodeKind.This:
                    ev = ResolveThis(ctx);
                    start = 0;
                    break;
                case ExprNodeKind.Literal:
                    ev = new EvalValue { Value = EvalLiteral(ctx, root.Text) };
                    start = 0;
                    break;
                case ExprNodeKind.Identifier:
                    if (!TryResolveIdentifier(ctx, root.Text, out ev))
                    {
                        // 变量未命中 → 把根与连续非调用成员段折叠为静态类型全名尝试
                        if (!TryResolveAsType(ctx, root.Text, chain, out ev, out start))
                            throw new EvalException("无法解析根 '" + root.Text + "'：不是局部变量/实参/this，"
                                + "也未匹配到已加载的静态类型。静态类型请用完整名（如 Verse.Thing）或类名");
                    }
                    else
                    {
                        start = 0;
                    }
                    break;
                default:
                    throw new EvalException("无法解析表达式根");
            }

            for (int i = start; i < chain.Count; i++)
                ev = ResolveMember(ctx, ev, chain[i]);

            return new EvalResult { Value = ev.Value, StaticType = ev.StaticType };
        }

        /// <summary>表达式是否需要线程上下文（含 this/变量/任何方法调用）。</summary>
        public static bool NeedsThread(string expression)
        {
            return NeedsThread(ExprParser.Parse(expression));
        }

        /// <summary>表达式是否需要线程上下文（按已解析 AST）。</summary>
        public static bool NeedsThread(ExprNode n)
        {
            if (n.Kind == ExprNodeKind.This || n.Kind == ExprNodeKind.Identifier)
                return true;
            if (n.Kind == ExprNodeKind.Member)
            {
                if (n.IsCall)
                    return true;
                if (NeedsThread(n.Target))
                    return true;
                if (n.Args != null)
                {
                    foreach (ExprNode a in n.Args)
                    {
                        if (NeedsThread(a))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>表达式是否需要栈帧（解析 this / 局部变量 / 实参）。</summary>
        public static bool NeedsFrame(string expression)
        {
            return NeedsFrame(ExprParser.Parse(expression));
        }

        /// <summary>表达式是否需要栈帧（按已解析 AST）。</summary>
        public static bool NeedsFrame(ExprNode n)
        {
            if (n.Kind == ExprNodeKind.This || n.Kind == ExprNodeKind.Identifier)
                return true;
            if (n.Kind == ExprNodeKind.Member)
            {
                if (NeedsFrame(n.Target))
                    return true;
                if (n.Args != null)
                {
                    foreach (ExprNode a in n.Args)
                    {
                        if (NeedsFrame(a))
                            return true;
                    }
                }
            }
            return false;
        }

        // ------------------------------------------------------------ 根解析

        EvalValue ResolveThis(EvalContext ctx)
        {
            if (ctx.Frame == null)
                throw new EvalException("表达式需要 this，但当前没有可用栈帧（请提供有效的 threadId/frameIndex）");
            try
            {
                return new EvalValue { Value = ctx.Frame.GetThis() };
            }
            catch (InvalidStackFrameException)
            {
                throw new EvalException("当前栈帧无效，无法读取 this");
            }
        }

        bool TryResolveIdentifier(EvalContext ctx, string name, out EvalValue ev)
        {
            ev = null;
            if (ctx.Frame == null)
                return false;
            // 局部变量（可见 live range）
            try
            {
                IList<LocalVariable> locals = ctx.Frame.GetVisibleVariables();
                if (locals != null)
                {
                    foreach (LocalVariable lv in locals)
                    {
                        if (lv.Name == name)
                        {
                            ev = new EvalValue { Value = ctx.Frame.GetValue(lv) };
                            return true;
                        }
                    }
                }
            }
            catch (VMDisconnectedException) { throw; }
            catch { /* 无调试符号等 → 继续实参 */ }

            // 实参
            try
            {
                ParameterInfoMirror[] ps = ctx.Frame.Method.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].Name == name)
                    {
                        ev = new EvalValue { Value = ctx.Frame.GetValue(ps[i]) };
                        return true;
                    }
                }
            }
            catch (VMDisconnectedException) { throw; }
            catch { }
            return false;
        }

        /// <summary>
        /// 把根标识符与连续的非调用成员段折叠为静态类型全名（如 "Verse.Thing"），
        /// 从长到短尝试 vm.GetTypes（mono 端匹配完整名或类名）；命中则返回消耗掉的成员数。
        /// </summary>
        bool TryResolveAsType(EvalContext ctx, string rootName, List<ExprNode> chain, out EvalValue ev, out int start)
        {
            ev = null;
            start = 0;
            var segs = new List<string> { rootName };
            int i = 0;
            while (i < chain.Count && !chain[i].IsCall && segs.Count < 8)
            {
                segs.Add(chain[i].Text);
                i++;
            }
            for (int len = segs.Count; len >= 1; len--)
            {
                string cand = string.Join(".", segs.GetRange(0, len).ToArray());
                IList<TypeMirror> ts = ctx.Vm.GetTypes(cand, true);
                if (ts != null && ts.Count > 0)
                {
                    ev = new EvalValue { StaticType = ts[0] };
                    start = len - 1; // 已消耗的成员数（根标识符本身不算）
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------ 字面量

        static Value EvalLiteral(EvalContext ctx, string text)
        {
            VirtualMachine vm = ctx.Vm;
            if (text.Length >= 2 && text[0] == '"')
                return vm.RootDomain.CreateString(UnescapeString(text));
            switch (text)
            {
                case "null":
                    return vm.CreateValue(null);
                case "true":
                    return vm.CreateValue(true);
                case "false":
                    return vm.CreateValue(false);
            }
            char last = text[text.Length - 1];
            string body = text;
            if (last == 'f' || last == 'F')
                return vm.CreateValue(float.Parse(body.Substring(0, body.Length - 1), CultureInfo.InvariantCulture));
            if (last == 'd' || last == 'D')
                return vm.CreateValue(double.Parse(body.Substring(0, body.Length - 1), CultureInfo.InvariantCulture));
            if (last == 'l' || last == 'L')
                return vm.CreateValue(long.Parse(body.Substring(0, body.Length - 1), CultureInfo.InvariantCulture));
            if (text.IndexOf('.') >= 0 || text.IndexOf('e') >= 0 || text.IndexOf('E') >= 0)
                return vm.CreateValue(double.Parse(body, CultureInfo.InvariantCulture));
            if (int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32))
                return vm.CreateValue(i32);
            if (long.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64))
                return vm.CreateValue(i64);
            throw new EvalException("无法解析字面量: " + text);
        }

        static string UnescapeString(string quoted)
        {
            var sb = new StringBuilder();
            for (int i = 1; i < quoted.Length - 1; i++)
            {
                char c = quoted[i];
                if (c == '\\' && i + 1 < quoted.Length - 1)
                {
                    char n = quoted[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '0': sb.Append('\0'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        default: sb.Append(n); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------ 成员求值

        EvalValue ResolveMember(EvalContext ctx, EvalValue current, ExprNode member)
        {
            if (current.Value == null && current.StaticType == null)
                throw new EvalException("在 null 值上访问成员 '" + member.Text + "'（前序求值结果为 null）");

            TypeMirror type = current.StaticType != null ? current.StaticType : ValueType(ctx, current.Value);
            if (type == null)
                throw new EvalException("无法确定成员 '" + member.Text + "' 的宿主类型");

            if (member.IsCall)
                return InvokeMember(ctx, current, type, member);
            return AccessMember(ctx, current, type, member);
        }

        static TypeMirror ValueType(EvalContext ctx, Value v)
        {
            if (v is ObjectMirror om)
                return om.Type;
            if (v is StructMirror sm)
                return sm.Type;
            if (v is PrimitiveValue pv)
            {
                if (pv.Value == null)
                    return null;
                try { return ctx.Vm.RootDomain.GetCorrespondingType(pv.Value.GetType()); }
                catch (VMDisconnectedException) { throw; }
                catch { return null; }
            }
            return null;
        }

        EvalValue AccessMember(EvalContext ctx, EvalValue current, TypeMirror type, ExprNode member)
        {
            // 1) 字段（含继承链）
            FieldInfoMirror field = FindField(type, member.Text);
            if (field != null)
                return ReadField(ctx, current, field);

            // 2) 属性（含继承链；getter 调用）
            PropertyInfoMirror prop = FindProperty(type, member.Text);
            if (prop != null)
            {
                MethodMirror getter = null;
                try { getter = prop.GetGetMethod(true); }
                catch (VMDisconnectedException) { throw; }
                catch { getter = null; }
                if (getter == null)
                    throw new EvalException("属性 '" + member.Text + "' 没有 getter（只写属性/索引器不支持）");
                return new EvalValue { Value = CallMethod(ctx, current, type, getter, new List<Value>()) };
            }

            throw new EvalException("在类型 " + TypeName(type) + " 上未找到成员 '" + member.Text + "'（字段或属性）");
        }

        static FieldInfoMirror FindField(TypeMirror type, string name)
        {
            TypeMirror cur = type;
            int levels = 0;
            while (cur != null && levels < 24)
            {
                FieldInfoMirror f = null;
                try { f = cur.GetField(name); }
                catch (VMDisconnectedException) { throw; }
                catch { f = null; }
                if (f != null)
                    return f;
                try { cur = cur.BaseType; }
                catch (VMDisconnectedException) { throw; }
                catch { break; }
                levels++;
            }
            return null;
        }

        static PropertyInfoMirror FindProperty(TypeMirror type, string name)
        {
            TypeMirror cur = type;
            int levels = 0;
            while (cur != null && levels < 24)
            {
                PropertyInfoMirror p = null;
                try { p = cur.GetProperty(name); }
                catch (VMDisconnectedException) { throw; }
                catch { p = null; }
                if (p != null)
                    return p;
                try { cur = cur.BaseType; }
                catch (VMDisconnectedException) { throw; }
                catch { break; }
                levels++;
            }
            return null;
        }

        static EvalValue ReadField(EvalContext ctx, EvalValue current, FieldInfoMirror field)
        {
            if (field.IsStatic)
                return new EvalValue { Value = ReadStaticField(ctx, field) };

            if (current.StaticType != null)
                throw new EvalException("字段 '" + field.Name + "' 是实例字段，不能从静态类型上下文访问（需先有实例）");

            Value val = current.Value;
            if (val is StructMirror st)
                return new EvalValue { Value = ReadStructField(st, field.Name) };
            if (val is ObjectMirror om)
                return new EvalValue { Value = om.GetValue(field) };
            throw new EvalException("该值不支持字段访问（值类型 " + val.GetType().Name + "）");
        }

        static Value ReadStaticField(EvalContext ctx, FieldInfoMirror field)
        {
            TypeMirror owner = field.DeclaringType;
            if (ctx.Thread != null)
            {
                try { return owner.GetValue(field, ctx.Thread); }
                catch (VMDisconnectedException) { throw; }
                catch { }
            }
            return owner.GetValue(field);
        }

        static Value ReadStructField(StructMirror st, string name)
        {
            try { return st[name]; }
            catch (Exception ex) { throw new EvalException("结构体字段读取失败: " + ex.Message); }
        }

        EvalValue InvokeMember(EvalContext ctx, EvalValue current, TypeMirror type, ExprNode member)
        {
            // 实参求值
            var argValues = new List<Value>();
            if (member.Args != null)
            {
                foreach (ExprNode a in member.Args)
                {
                    EvalResult ar = Evaluate(ctx, a);
                    if (ar.StaticType != null)
                        throw new EvalException("实参不能是纯静态类型（请访问其成员或字段）: " + a.Describe());
                    argValues.Add(ar.Value);
                }
            }

            MethodMirror method = SelectMethod(ctx, type, member.Text, argValues);
            return new EvalValue { Value = CallMethod(ctx, current, type, method, argValues) };
        }

        /// <summary>按名字 + 参数个数 + 参数类型宽松匹配选择方法（继承链上的全部候选）。</summary>
        static MethodMirror SelectMethod(EvalContext ctx, TypeMirror type, string name, List<Value> args)
        {
            var candidates = new List<MethodMirror>();
            TypeMirror cur = type;
            int levels = 0;
            while (cur != null && levels < 24)
            {
                MethodMirror[] ms = null;
                try { ms = cur.GetMethods(); }
                catch (VMDisconnectedException) { throw; }
                catch { ms = null; }
                if (ms != null)
                {
                    foreach (MethodMirror m in ms)
                    {
                        if (m.Name == name)
                            candidates.Add(m);
                    }
                }
                try { cur = cur.BaseType; }
                catch (VMDisconnectedException) { throw; }
                catch { break; }
                levels++;
            }
            if (candidates.Count == 0)
                throw new EvalException("在类型 " + TypeName(type) + " 上未找到方法 '" + name + "'");

            // 参数个数过滤
            var byArity = new List<MethodMirror>();
            foreach (MethodMirror m in candidates)
            {
                ParameterInfoMirror[] ps = null;
                try { ps = m.GetParameters(); }
                catch (VMDisconnectedException) { throw; }
                catch { ps = null; }
                if (ps != null && ps.Length == args.Count)
                    byArity.Add(m);
            }
            if (byArity.Count == 0)
                throw new EvalException("方法 '" + name + "' 实参个数不匹配：期望 "
                    + ArityText(candidates) + " 个实参，实际 " + args.Count + " 个");

            if (byArity.Count == 1)
                return byArity[0];

            // 参数类型宽松匹配（简单名相等计分；并列取第一个）
            MethodMirror best = byArity[0];
            int bestScore = -1;
            foreach (MethodMirror m in byArity)
            {
                int score = 0;
                ParameterInfoMirror[] ps = m.GetParameters();
                for (int i = 0; i < ps.Length && i < args.Count; i++)
                {
                    if (ArgTypeMatches(ps[i].ParameterType, args[i]))
                        score++;
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = m;
                }
            }
            return best;
        }

        static string ArityText(List<MethodMirror> candidates)
        {
            var set = new SortedSet<int>();
            foreach (MethodMirror m in candidates)
            {
                try { set.Add(m.GetParameters().Length); }
                catch (VMDisconnectedException) { throw; }
                catch { }
            }
            if (set.Count == 0)
                return "未知";
            var parts = new List<string>();
            foreach (int a in set)
                parts.Add(a.ToString());
            return string.Join("/", parts.ToArray());
        }

        static bool ArgTypeMatches(TypeMirror paramType, Value arg)
        {
            string want = null;
            try { want = paramType.Name; }
            catch (VMDisconnectedException) { throw; }
            catch { return true; }
            if (string.IsNullOrEmpty(want) || arg == null)
                return true; // null 实参通配
            string got = ArgValueTypeName(arg);
            if (got == null)
                return false;
            return string.Equals(want, got, StringComparison.OrdinalIgnoreCase);
        }

        static string ArgValueTypeName(Value arg)
        {
            if (arg is EnumMirror em)
            {
                try { return em.Type.Name; }
                catch (VMDisconnectedException) { throw; }
                catch { return null; }
            }
            if (arg is ObjectMirror om)
            {
                try { return om.Type.Name; }
                catch (VMDisconnectedException) { throw; }
                catch { return null; }
            }
            if (arg is StructMirror sm)
            {
                try { return sm.Type.Name; }
                catch (VMDisconnectedException) { throw; }
                catch { return null; }
            }
            if (arg is PrimitiveValue pv)
                return pv.Value != null ? pv.Value.GetType().Name : null;
            return null;
        }

        /// <summary>执行方法/getter：静态 → TypeMirror.InvokeMethod；实例 → 接收者 InvokeMethod（同步）。</summary>
        static Value CallMethod(EvalContext ctx, EvalValue current, TypeMirror type, MethodMirror method, List<Value> args)
        {
            if (ctx.Thread == null)
                throw new EvalException("方法/属性调用需要线程上下文（无法解析线程，请提供有效 threadId）");

            if (method.IsStatic)
                return type.InvokeMethod(ctx.Thread, method, args);

            if (current.StaticType != null)
                throw new EvalException("方法 '" + method.Name + "' 是实例方法，不能从静态类型上下文调用");

            Value receiver = current.Value;
            if (receiver == null)
                throw new EvalException("在 null 上调用方法 '" + method.Name + "'");

            try
            {
                if (receiver is ObjectMirror om)
                    return om.InvokeMethod(ctx.Thread, method, args);
                if (receiver is StructMirror sm)
                    return sm.InvokeMethod(ctx.Thread, method, args);
                if (receiver is PrimitiveValue pv)
                    return pv.InvokeMethod(ctx.Thread, method, args);
                throw new EvalException("该值类型不支持方法调用: " + receiver.GetType().Name);
            }
            catch (InvocationException iex)
            {
                throw new EvalException("方法 " + TypeName(type) + ":" + method.Name + " 抛出异常: " + DescribeInvocation(ctx, iex));
            }
        }

        /// <summary>把被调用方法抛出的异常对象转成可读文本（异常类型 + Message 属性）。</summary>
        static string DescribeInvocation(EvalContext ctx, InvocationException iex)
        {
            ObjectMirror obj = iex.Exception;
            if (obj == null)
                return "（未知异常对象）";
            string typeName;
            try { typeName = obj.Type.FullName; }
            catch (VMDisconnectedException) { throw; }
            catch { typeName = "异常"; }

            // 尝试读取 Message 属性（getter 调用，需线程与挂起）
            try
            {
                if (ctx.Thread != null)
                {
                    PropertyInfoMirror msgProp = FindProperty(obj.Type, "Message");
                    if (msgProp != null)
                    {
                        MethodMirror getter = msgProp.GetGetMethod(true);
                        if (getter != null && !getter.IsStatic)
                        {
                            Value v = obj.InvokeMethod(ctx.Thread, getter, new List<Value>());
                            if (v is StringMirror s)
                            {
                                string text = s.Value;
                                if (!string.IsNullOrEmpty(text))
                                    return typeName + ": " + text;
                            }
                        }
                    }
                }
            }
            catch (VMDisconnectedException) { throw; }
            catch { }
            return typeName;
        }

        // ------------------------------------------------------------ 辅助

        static string TypeName(TypeMirror t)
        {
            if (t == null)
                return "?";
            try { return t.FullName; }
            catch (VMDisconnectedException) { throw; }
            catch { }
            try { return t.Name; }
            catch (VMDisconnectedException) { throw; }
            catch { return "?"; }
        }

        /// <summary>求值中间态：实例值或静态类型上下文。</summary>
        sealed class EvalValue
        {
            public Value Value;
            public TypeMirror StaticType;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 轻量补全（**不引 Workspaces/Features**：省 ~12MB 载荷与 MEF 组合开销）。两个来源：
    ///   ① **成员补全**（`A.B`）：把接收者 `A` 交给 <see cref="ReplSession.ResolveExpressionSymbol"/> 解析成
    ///      **类型符号**——走**语义模型**（只建编译、**不发射、不加载程序集、不执行用户表达式**），
    ///      再沿**基类链**枚举公共成员；
    ///   ② **命名空间补全**（`System.Tex`）：在会话引用的**元数据命名空间表**里做前缀匹配
    ///      （只读元数据，不把 106 个本体 dll 真载入进程）。
    /// `prefix` = 已输入的部分；候选**只返回补全余下部分**——`CSAutoCompleter` 的补全是
    /// "在光标处**插入**"，回填整名会得到 `sb.AppendAppend`。
    /// </summary>
    public static class RoslynCompletion
    {
        /// <summary>`receiver.prefix` 切分：接收者允许成员链（`list.Count`），前缀是不含点的尾段标识符。</summary>
        static readonly Regex MemberPattern = new Regex(@"([A-Za-z_][\w\.]*)\.([A-Za-z_]\w*)$");

        const int MaxCandidates = 50;

        public static string[] Complete(string input, ReplSession session, out string prefix)
        {
            prefix = "";
            if (string.IsNullOrEmpty(input) || session == null) return new string[0];

            Match m = MemberPattern.Match(input);
            if (!m.Success) return new string[0];

            string receiver = m.Groups[1].Value;
            prefix = m.Groups[2].Value;

            // 来源 ①：成员补全（接收者是会话变量/属性链/类型名，都由 Roslyn 判定，不必我们猜）
            ITypeSymbol type = session.ResolveExpressionSymbol(receiver);
            if (type != null)
            {
                string[] members = MemberCompletions(type, prefix);
                if (members.Length > 0) return members;
            }

            // 来源 ②：命名空间补全。两条来源**互补而非二选一**：`System.Tex` 的接收者 `System` 是命名空间
            // （不是类型符号 ⇒ type==null），而"类型符号没命中候选"（如接收者写成半截名字）时也回落到这里，
            // 交给命名空间前缀匹配去判——两边都不命中才返回空（不误报）。
            return NamespaceCompletions(receiver + "." + prefix, session);
        }

        /// <summary>沿**基类链**枚举公共成员名，扣掉 prefix 后**升序去重**返回（构造器/访问器/运算符不显示）。</summary>
        static string[] MemberCompletions(ITypeSymbol type, string prefix)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (ITypeSymbol cur = type; cur != null; cur = cur.BaseType)
            {
                // C.5 纪律：本方法体内只用显式循环——禁 LINQ + Roslyn 类型 lambda（非捕获 lambda 也会被
                // 编译器缓存成 `<>c` 的 `Func<ISymbol,…>` 静态字段，泛型实参是载荷类型 ⇒ 启动期字段扫描炸）。
                foreach (ISymbol member in cur.GetMembers())
                {
                    if (member.IsImplicitlyDeclared) continue;              // record 的 EqualityContract 等
                    if (member.DeclaredAccessibility != Accessibility.Public) continue;
                    string name = member.Name;
                    if (name.Length == 0 || name.IndexOf('.') >= 0) continue;   // `.ctor` / `IFoo.Bar` 形态
                    if (!IsCompletable(member)) continue;
                    if (name.Length <= prefix.Length) continue;            // 整名已敲完：无可补内容
                    if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!seen.Add(name)) continue;                          // 基类链上重名只留一条
                    candidates.Add(name.Substring(prefix.Length));
                }
            }
            candidates.Sort(StringComparer.Ordinal);
            if (candidates.Count > MaxCandidates)
                candidates.RemoveRange(MaxCandidates, candidates.Count - MaxCandidates);
            return candidates.ToArray();
        }

        /// <summary>补全表只收"用户能敲出来的成员名"：字段/属性/事件/嵌套类型/普通方法（不含访问器、运算符、转换）。</summary>
        static bool IsCompletable(ISymbol member)
        {
            SymbolKind kind = member.Kind;
            if (kind == SymbolKind.Field || kind == SymbolKind.Property
                || kind == SymbolKind.Event || kind == SymbolKind.NamedType) return true;
            if (kind != SymbolKind.Method) return false;
            var method = member as IMethodSymbol;
            if (method == null) return false;
            return method.MethodKind == MethodKind.Ordinary
                || method.MethodKind == MethodKind.ExplicitInterfaceImplementation
                || method.MethodKind == MethodKind.DelegateInvoke;
        }

        /// <summary>
        /// 命名空间前缀匹配：在 `fullPrefix`（= 接收者 + "." + 前缀）之后取**下一段**——含点则连同点一起返回
        /// （`System.Tex` → `t` 补出 `System.Text`，`t.` 提示还有子命名空间）。
        /// </summary>
        static string[] NamespaceCompletions(string fullPrefix, ReplSession session)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string ns in session.AllNamespaces())
            {
                if (ns.Length <= fullPrefix.Length) continue;
                if (!ns.StartsWith(fullPrefix, StringComparison.Ordinal)) continue;
                string rest = ns.Substring(fullPrefix.Length);
                int dot = rest.IndexOf('.');
                string next = dot < 0 ? rest : rest.Substring(0, dot + 1);
                if (seen.Add(next)) result.Add(next);
            }
            result.Sort(StringComparer.Ordinal);
            if (result.Count > MaxCandidates) result.RemoveRange(MaxCandidates, result.Count - MaxCandidates);
            return result.ToArray();
        }
    }
}

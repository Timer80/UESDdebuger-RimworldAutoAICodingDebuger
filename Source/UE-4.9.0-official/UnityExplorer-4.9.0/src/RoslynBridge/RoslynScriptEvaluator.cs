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
        // 任务 13（引用自愈）**把开关做实**（ConfigManager 文案 = 功能规格，不再是死开关）：
        //  · ON ⇒ 模组程序集**永不预加**，仅编译诊断触发的自愈把它带进引用集（healer 只从诊断进）；
        //  · OFF ⇒ 模组路径作 healer 的**排除集**（"已加载程序集 Location"候选通道也受排除，否则会绕过开关），
        //    失败日志注明是开关导致（承载 = ReferenceHealer.AllowModTypeResolution，ConsoleController 方法组接线）。
        public bool AllowModTypeResolution { get; }

        readonly ReferenceHealer s_healer;
        /// <summary>任务 13：自愈器实例（宿主注入 ModPathsProvider / ReportSink 方法组用）。</summary>
        public ReferenceHealer Healer => s_healer;

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
            // 任务 13：自愈器先建、注入会话（Submit 的 emit 失败循环即生产主循环）。
            // 固定搜索目录 = 引用集各文件所在目录 + 运行时目录；模组候选每次 Resolve 经
            // ModPathsProvider 现算（§4.1，禁止构造期快照）。
            s_healer = new ReferenceHealer(HealSearchDirs(referencePaths));
            s_healer.AllowModTypeResolution = allowModTypeResolution;
            s_session = new ReplSession(referencePaths, hostBaseTypeName, s_healer);
            AllowModTypeResolution = allowModTypeResolution;
            IsAvailable = true;
        }

        /// <summary>§4.10-3：把控制台初始化时因缺引用被剔除的默认 using 登记进会话
        /// （引用自愈增长引用集后由 <c>OnReferenceAdded</c> 验证式重算）。</summary>
        public void SetDeferredUsings(IEnumerable<string> unavailable) => s_session.SetDeferredUsings(unavailable);

        /// <summary>自愈固定搜索目录：引用集各文件所在目录 + 运行时目录（mscorlib 所在）。
        /// 方法实现、无 lambda（C.5 闭包纪律）。</summary>
        static List<string> HealSearchDirs(IEnumerable<string> referencePaths)
        {
            var dirs = new List<string>();
            if (referencePaths != null)
            {
                foreach (string p in referencePaths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string d = null;
                    try { d = System.IO.Path.GetDirectoryName(p); } catch { d = null; }
                    if (!string.IsNullOrEmpty(d) && !dirs.Contains(d)) dirs.Add(d);
                }
            }
            try
            {
                string rt = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
                if (!string.IsNullOrEmpty(rt) && !dirs.Contains(rt)) dirs.Add(rt);
            }
            catch { /* 动态程序集无 Location：跳过运行时目录 */ }
            return dirs;
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 前置包 <c>UE.Roslyn</c> 的**反射门**（任务 14 = 计划 F1）。
    ///
    /// <para><b>动机。</b>UE 此前随包分发**自己那份** Roslyn 8 件载荷
    /// （<c>UE_Data\sinai-dev-UnityExplorer\Roslyn\</c>，与前置包各 12.68 MB）。F1 起 UE
    /// 不再分发载荷，改为请前置包 <c>Mods\Roslyn</c> 供货；前置包缺席或加载失败 ⇒
    /// **干净退回 mcs**（控制台仍可用），且原因响亮（禁静默）。</para>
    ///
    /// <para><b>⚠️ 零编译期引用。</b>本文件对该包只有**字符串**入口名（前置包 README §4 的消费者契约）：
    /// 没有 <c>using Roslyn;</c>、没有对 <c>Mods\Roslyn\Assemblies\Roslyn.dll</c> 或任何 Roslyn 程序集的
    /// <c>&lt;Reference&gt;</c>。编译期引用会让 UE 变成下一个「字段依赖前置包」的受害者。
    /// 离线断言：<c>TestFrontGateDeclaresNoCompilerReference</c>（源码面去字面量扫描 + 产物面 AssemblyRef 探测）。</para>
    ///
    /// <para><b>为什么不自己 LoadFrom 前置包程序集。</b>前置包 README 的参考片段在找不到类型时兜底
    /// <c>Assembly.LoadFrom("Mods/Roslyn/Assemblies/Roslyn.dll")</c>。UE 侧**刻意不做**该兜底：
    /// ① 被玩家停用的模组其文件仍在磁盘上 —— 主动 LoadFrom 等于绕过玩家的模组列表（本门只查"磁盘上有没有"，
    /// 用于把失败原因说清楚，不据此装载）；② 不需要它：RimWorld 在
    /// <c>StaticConstructorOnStartupUtility.CallAll()</c> 之前已把**全部启用模组**的程序集 LoadFrom 进
    /// AppDomain（顺序无关），而本门的调用点就在 CallAll 窗口内 ⇒ 只要前置包启用，扫
    /// <c>AppDomain.CurrentDomain.GetAssemblies()</c> 必然命中。真正需要"主动触发"的是前置包自己的
    /// cctor/门面：本门通过反射调它的 <c>TryEnsureLoaded(out string)</c> 来触发（幂等）。</para>
    ///
    /// <para><b>跨配置。</b>本文件进全部 9 配置编译（与 <see cref="RoslynAssemblyLoader"/> 同款，只用
    /// <c>System.*</c>），net35 面不得用 <c>Array.Empty</c>/<c>??=</c> 之类新 API。</para>
    /// </summary>
    public static class RoslynFrontGate
    {
        /// <summary>前置包 packageId（与 `Mods\Roslyn\About\About.xml` 的 &lt;packageId&gt; 一致；勿改字面）。</summary>
        public const string FrontPackageId = "UE.Roslyn";

        /// <summary>入口类型全名。**只以字符串存在**（零 typeref）；前置包以常量形式公布同一串。</summary>
        public const string EntryTypeName = "Roslyn.RoslynLoader";

        /// <summary>入口方法名（前置包 README §5：`static bool TryEnsureLoaded(out string failure)`）。</summary>
        public const string ResolveEntryMethod = "TryEnsureLoaded";

        /// <summary>前置包程序集相对"Mods 目录"的位置（**仅用于诊断**：包在不在磁盘上，不据此装载）。</summary>
        public const string FrontAssemblyRelativePath = @"Roslyn\Assemblies\Roslyn.dll";

        /// <summary>载荷件数（与 RoslynAssemblyLoader.LoadOrder.Length 同为 8）。</summary>
        public const int ExpectedPayloadCount = 8;

        static bool s_attempted;
        static bool s_available;
        static string s_failure;
        static int s_loadedCount;
        static string s_sourcePath;

        /// <summary>前置包是否已供上载荷（8/8）。</summary>
        public static bool IsAvailable { get { return s_available; } }
        /// <summary>本门是否已跑过（区分「没跑」与「跑了但失败」）。</summary>
        public static bool Attempted { get { return s_attempted; } }
        /// <summary>失败原因；无失败为 null。</summary>
        public static string FailureReason { get { return s_failure; } }
        /// <summary>前置包自报的已载件数（成功为 8）。</summary>
        public static int LoadedCount { get { return s_loadedCount; } }
        /// <summary>命中的前置包程序集路径（诊断用；不可得为 null）。</summary>
        public static string SourcePath { get { return s_sourcePath; } }

        /// <summary>
        /// 真入口（幂等）：扫已载程序集找前置包门面 → 反射调 <c>TryEnsureLoaded(out string)</c>。
        /// **绝不抛异常**：任何失败都以"false + 非空 failure 文本"返回（调用方据此响亮告警并退回 mcs）。
        /// </summary>
        public static bool TryEnsure(out string failure)
        {
            if (s_attempted) { failure = s_failure; return s_available; }
            s_attempted = true;

            Assembly[] loaded;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception ex)
            {
                loaded = new Assembly[0];
                s_failure = "扫描已载程序集失败：" + Describe(ex);
            }

            string[] candidates = CandidateAssemblyPaths(DeriveModsDirectory());
            string fail;
            int count;
            string source;
            bool ok = TryEnsureCore(loaded, candidates, out fail, out count, out source);

            s_available = ok;
            s_loadedCount = count < 0 ? 0 : count;   // -1 = 读不到自报件数（见 TryInvokeLoader）
            s_sourcePath = source;
            s_failure = ok ? null : fail;
            failure = s_failure;
            return ok;
        }

        /// <summary>
        /// 门核心（注入"已载程序集集合 + 磁盘候选路径" ⇒ 离线可断言；先例 = EvaluatorSelector 的纯决策函数）。
        /// 成功条件 = 找到入口类型 **且** <c>TryEnsureLoaded</c> 返回 true **且**（能读到件数时）件数为 8。
        /// </summary>
        public static bool TryEnsureCore(IEnumerable<Assembly> loadedAssemblies, string[] candidatePaths,
            out string failure, out int loadedCount, out string sourcePath)
        {
            sourcePath = null;
            loadedCount = 0;

            Type loader;
            try { loader = FindLoaderType(loadedAssemblies); }
            catch (Exception ex)
            {
                failure = "前置包 " + FrontPackageId + " 探测异常：" + Describe(ex);
                return false;
            }

            if (loader == null)
            {
                failure = DescribeFrontPackageMissing(candidatePaths);
                return false;
            }

            sourcePath = SafeLocation(loader.Assembly);

            string frontFailure;
            int count;
            bool ok = TryInvokeLoader(loader, out frontFailure, out count);
            loadedCount = count < 0 ? 0 : count;   // 对外口径：不可得即 0

            if (!ok)
            {
                failure = frontFailure;
                return false;
            }

            // 前置包自报成功但件数不是 8 ⇒ 契约违例（响亮，不当作成功）。
            // count<0 = 其 LoadedCount 属性读不到 ⇒ 不据此否决（最终判据是"认领已载载荷"的 AppDomain 实测件数）。
            if (count >= 0 && count != ExpectedPayloadCount)
            {
                failure = "前置包 " + FrontPackageId + " 自报成功但载荷件数为 " + count
                    + "/" + ExpectedPayloadCount + "（契约违例）：" + DescribeFrontPackageState(loader);
                return false;
            }

            failure = null;
            return true;
        }

        /// <summary>在给定程序集集合里找入口类型（`GetType` 逐个试；单个程序集取型失败即跳过，绝不外泄异常）。</summary>
        public static Type FindLoaderType(IEnumerable<Assembly> assemblies)
        {
            if (assemblies == null) return null;
            foreach (Assembly a in assemblies)
            {
                if (a == null) continue;
                try
                {
                    Type t = a.GetType(EntryTypeName, false);
                    if (t != null) return t;
                }
                catch
                {
                    // 动态程序集/元数据残缺：跳过。真因由 DescribeFrontPackageMissing 的文本承担。
                }
            }
            return null;
        }

        /// <summary>
        /// 反射调入口方法。**绝不抛**：类型为 null、方法缺失、调用抛异常一律转成
        /// "false + 非空文本"（`TargetInvocationException` 拆出内层，否则真因被包装吞掉）。
        /// <paramref name="loadedCount"/> = 前置包自报件数；**-1 = 读不到**（调用方据此不否决成功）。
        /// </summary>
        public static bool TryInvokeLoader(Type loaderType, out string failure, out int loadedCount)
        {
            loadedCount = -1;
            if (loaderType == null)
            {
                failure = "前置包 " + FrontPackageId + " 未加载：未在已载程序集中找到类型 " + EntryTypeName;
                return false;
            }

            try
            {
                MethodInfo m = loaderType.GetMethod(ResolveEntryMethod,
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(string).MakeByRefType() }, null);
                if (m == null)
                {
                    failure = "前置包 " + FrontPackageId + " 缺少入口 " + ResolveEntryMethod
                        + "(out string)（API 变更？）：" + DescribeFrontPackageState(loaderType);
                    return false;
                }

                object[] args = new object[1];
                bool ok;
                try { ok = (bool)m.Invoke(null, args); }
                catch (TargetInvocationException tie)
                {
                    failure = "调用 " + EntryTypeName + "." + ResolveEntryMethod + " 抛出："
                        + Describe(tie.InnerException == null ? tie : tie.InnerException);
                    return false;
                }
                catch (Exception ex)
                {
                    failure = "调用 " + EntryTypeName + "." + ResolveEntryMethod + " 失败：" + Describe(ex);
                    return false;
                }

                int reported;
                loadedCount = ReadLoadedCount(loaderType, out reported) ? reported : -1;
                string frontFailure = args[0] as string;

                if (!ok)
                {
                    failure = string.IsNullOrEmpty(frontFailure)
                        ? "前置包 " + FrontPackageId + " 报告加载失败但未给原因：" + DescribeFrontPackageState(loaderType)
                        : frontFailure;
                    return false;
                }

                failure = null;
                return true;
            }
            catch (Exception ex)
            {
                failure = "前置包 " + FrontPackageId + " 入口反射失败：" + Describe(ex);
                return false;
            }
        }

        /// <summary>
        /// "前置包没找到"的**响亮**文本：把磁盘候选逐个点名（存在 ⇒ 包在盘上但没被加载/启用；
        /// 不存在 ⇒ 真没装）。禁静默：玩家据此一眼分辨"该装包"和"该启用包"。
        /// </summary>
        public static string DescribeFrontPackageMissing(string[] candidatePaths)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("前置包 ").Append(FrontPackageId).Append(" 未安装或未加载（未在已载程序集中找到类型 ")
              .Append(EntryTypeName).Append("）；磁盘探测：");
            if (candidatePaths == null || candidatePaths.Length == 0)
            {
                sb.Append("<无法推导候选路径>");
                return sb.ToString();
            }
            bool first = true;
            foreach (string p in candidatePaths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (!first) sb.Append("；");
                first = false;
                bool exists;
                try { exists = File.Exists(p); }
                catch { exists = false; }
                sb.Append(p).Append(exists ? "（存在 ⇒ 包已安装但未启用/未被加载）" : "（不存在 ⇒ 未安装）");
            }
            return sb.ToString();
        }

        /// <summary>前置包程序集路径候选（诊断用）：`&lt;Mods&gt;\Roslyn\Assemblies\Roslyn.dll`（按 UE 程序集位置推导）。</summary>
        public static string[] CandidateAssemblyPaths(string modsDirectory)
        {
            if (string.IsNullOrEmpty(modsDirectory)) return new string[0];
            try { return new string[] { Path.Combine(modsDirectory, FrontAssemblyRelativePath) }; }
            catch { return new string[0]; }
        }

        /// <summary>
        /// 从本程序集位置推导 Mods 目录（`&lt;game&gt;\Mods\UESDdebuger\Assemblies` → `&lt;game&gt;\Mods`）。
        /// 用 <c>Assembly.Location</c>（net35/net472/net6 通用）而**非** <c>CodeBase</c>：后者在 .NET 5+
        /// 标了 <c>[Obsolete]</c>（SYSLIB0012），本文件进 9 配置（含 net6 的 ML_Cpp_net6）编译，不能带警告。
        /// </summary>
        static string DeriveModsDirectory()
        {
            try
            {
                string asmPath = typeof(RoslynFrontGate).Assembly.Location;
                string asmDir = string.IsNullOrEmpty(asmPath)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : Path.GetDirectoryName(asmPath);
                if (string.IsNullOrEmpty(asmDir)) return null;
                string modRoot = Path.GetDirectoryName(asmDir);          // <…>\Mods\UESDdebuger
                if (string.IsNullOrEmpty(modRoot)) return null;
                return Path.GetDirectoryName(modRoot);                   // <…>\Mods
            }
            catch { return null; }
        }

        /// <summary>读前置包自报件数（best-effort；属性缺失/取型失败 ⇒ false，调用方**不**据此否决成功）。</summary>
        static bool ReadLoadedCount(Type loaderType, out int loadedCount)
        {
            loadedCount = 0;
            try
            {
                PropertyInfo p = loaderType.GetProperty("LoadedCount", BindingFlags.Public | BindingFlags.Static);
                if (p == null) return false;
                object v = p.GetValue(null, null);
                if (!(v is int)) return false;
                loadedCount = (int)v;
                return true;
            }
            catch
            {
                // 读不到件数时由"认领已载载荷"（RoslynAssemblyLoader.AdoptLoadedPayload）
                // 以 AppDomain 实测件数为准做最终判定，不用这个数字否决。
                return false;
            }
        }

        /// <summary>前置包自报状态（诊断文本；只在失败/违例路径调用，逐项 best-effort）。</summary>
        static string DescribeFrontPackageState(Type loaderType)
        {
            return "Attempted=" + ReadStatic(loaderType, "Attempted")
                + "、IsLoaded=" + ReadStatic(loaderType, "IsLoaded")
                + "、LoadedCount=" + ReadStatic(loaderType, "LoadedCount")
                + "、FailureReason=" + ReadStatic(loaderType, "FailureReason");
        }

        static string ReadStatic(Type type, string propertyName)
        {
            try
            {
                PropertyInfo p = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (p == null) return "<无此属性>";
                object v = p.GetValue(null, null);
                return v == null ? "<null>" : v.ToString();
            }
            catch (Exception ex) { return "<读取失败：" + ex.GetType().Name + ">"; }
        }

        static string SafeLocation(Assembly assembly)
        {
            try { return assembly == null ? null : assembly.Location; }
            catch { return null; }
        }

        static string Describe(Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}

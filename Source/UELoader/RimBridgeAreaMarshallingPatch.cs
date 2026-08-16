using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace UELoader
{
    /// <summary>
    /// RimBridgeServer architect 地区写操作主线程派发补丁。
    ///
    /// 背景：RimBridgeServer 的 GABP 工具执行在独立 TCP 读线程上同步 method.Invoke
    /// （TcpTransport.ReadMessagesAsync → MessageReceived.Invoke → HandleToolsCallAsync →
    /// ToolRegistry.CallToolAsync → CreateHandler 同步调用）。而 RimWorldArchitect.DeleteAreaResponse /
    /// ClearAreaResponse 在该读线程上直接访问 Unity/RimWorld 主线程状态（Area.Delete/Clear、
    /// Designator_AreaAllowed.SelectedArea、ReadStatus 读 Find.CurrentMap/TickManager/ScreenFader、
    /// CreateDesignatorStatePayload 读 DesignatorManager/CaptureSelectionState），未走
    /// RimBridgeMainThread.Invoke 派发 → 对真实 Area 执行变更时读线程永久阻塞，该连接读循环停滞，
    /// 后续所有请求全部超时。无效对象（TryResolve 提前返回 Failure）则 22ms 正常，不卡死。
    ///
    /// 本补丁：为 DeleteAreaResponse / ClearAreaResponse 各挂独立 Harmony prefix（共享静态状态，
    /// 经 __originalMethod 区分）。非主线程调用时，用 RimBridgeMainThread.Invoke 把一个
    /// 「调用原始方法体」的 lambda 派发到游戏主线程（主线程每帧 Pump），带超时同步等待；
    /// 主线程调用则放行原方法。用线程静态标志防递归：派发回调在主线程执行原方法体时，
    /// prefix 已见 inDispatch=true → 放行，原方法在主线程真正执行 → 不死锁不递归。
    ///
    /// 实现细节：prefix 用 __originalMethod 拿当前目标方法，派发时经 reflection 调用它；
    /// 主线程回调进入原方法体时，我们的 prefix 因 inDispatch=true 而放行，从而在主线程执行真实逻辑。
    /// </summary>
    public static class RimBridgeAreaMarshallingPatch
    {
        public const string HarmonyId = "UESDdebuger.rimbridge.architect.marshal";

        public static bool Installed { get; private set; }

        /// <summary>派发中（主线程执行体）标志，防递归。</summary>
        [ThreadStatic]
        private static bool inDispatch;

        /// <summary>派发超时（毫秒）。</summary>
        private const int DispatchTimeoutMs = 30000;

        private static bool installed;
        private static Type rmtType;
        private static PropertyInfo rmtIsMainThreadProp;
        private static MethodInfo rmtInvokeT;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                Type arch = FindType("RimBridgeServer.RimWorldArchitect");
                rmtType = FindType("RimBridgeServer.RimBridgeMainThread");
                if (arch == null || rmtType == null)
                {
                    Log.Warning("[RimBridgeAreaMarshalling] 未找到 RimBridgeServer 的 RimWorldArchitect/RimBridgeMainThread，补丁未安装");
                    return;
                }
                rmtIsMainThreadProp = rmtType.GetProperty("IsMainThread", BindingFlags.Static | BindingFlags.Public);
                rmtInvokeT = FindInvokeT(rmtType);
                if (rmtIsMainThreadProp == null || rmtInvokeT == null)
                {
                    Log.Warning("[RimBridgeAreaMarshalling] RimBridgeMainThread 成员查找不全，补丁未安装");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                var prefixMethod = AccessTools.Method(typeof(RimBridgeAreaMarshallingPatch), "MarshalArea_Prefix");

                foreach (string m in new[] { "DeleteAreaResponse", "ClearAreaResponse" })
                {
                    MethodBase target = AccessTools.Method(arch, m, new[] { typeof(string) });
                    Log.Message($"[RimBridgeAreaMarshalling] resolve {arch.FullName}.{m}: method={(target == null ? "NULL" : target.ToString())} isStatic={target?.IsStatic} isPublic={target?.IsPublic}");
                    if (target == null)
                    {
                        // 诊断：列出 arch 上所有名字以 m 开头或含 "AreaResponse" 的 public static 方法
                        foreach (var mm in arch.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (mm.Name.Contains("AreaResponse"))
                                Log.Message($"  [diag] found {mm.Name}({string.Join(",", Array.ConvertAll(mm.GetParameters(), p => p.ParameterType.Name))}) static={mm.IsStatic} public={mm.IsPublic}");
                        }
                        Log.Warning($"[RimBridgeAreaMarshalling] RimWorldArchitect.{m} 未找到，跳过");
                        continue;
                    }
                    harmony.Patch(target, prefix: new HarmonyMethod(prefixMethod));
                    Log.Message($"[RimBridgeAreaMarshalling] 已为 {target.DeclaringType.FullName}.{target.Name} 挂主线程派发补丁");
                }
                // 运行时确认：harmony.GetPatchedMethods()
                try
                {
                    var patched = harmony.GetPatchedMethods();
                    foreach (var pm in patched)
                        Log.Message($"[RimBridgeAreaMarshalling] 已打补丁的方法: {pm.DeclaringType?.FullName}.{pm.Name}");
                }
                catch (Exception e) { Log.Warning($"[RimBridgeAreaMarshalling] GetPatchedMethods 失败: {e.Message}"); }
                Installed = true;
            }
            catch (Exception ex)
            {
                Installed = false;
                Log.Error($"[RimBridgeAreaMarshalling] 安装补丁失败: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 共享 prefix：非主线程调用时把执行派发到主线程；主线程调用直接放行。
        /// </summary>
        private static bool MarshalArea_Prefix(string areaId, ref object __result, MethodBase __originalMethod)
        {
            try
            {
                bool isMain = true;
                try { isMain = (bool)rmtIsMainThreadProp.GetValue(null, null); } catch (Exception e) { Log.Warning($"[RimBridgeAreaMarshalling] 读 IsMainThread 失败: {e.Message}"); }
                if (UEHttpLog.DiagnosticEnabled)
                    UEHttpLog.Message($"[RimBridgeAreaMarshalling] prefix: {__originalMethod.Name}(areaId={areaId}) isMain={isMain} inDispatch={inDispatch}");   // 原 L116 由 Log.Message 改为受开关控制的 UEHttpLog.Message
                if (inDispatch)
                    return true; // 主线程派发回调内执行原方法体 → 放行

                if (isMain)
                    return true; // 主线程直接执行

                // 非主线程：派发到主线程执行原方法体，同步取结果。
                inDispatch = true;
                try
                {
                    object result = DispatchToMain(areaId, __originalMethod);
                    __result = result;
                    if (UEHttpLog.DiagnosticEnabled)
                        UEHttpLog.Message($"[RimBridgeAreaMarshalling] 派发完成: {__originalMethod.Name} result={(result == null ? "null" : "ok")}");   // 原 L129 由 Log.Message 改为受开关控制的 UEHttpLog.Message
                    return false; // 跳过原方法（读线程上）
                }
                finally
                {
                    inDispatch = false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RimBridgeAreaMarshalling] 派发失败: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                // 失败放行原方法（读线程执行；若仍死锁由上层超时兜底）
                return true;
            }
        }

        /// <summary>
        /// 派发到主线程并等待结果。
        /// </summary>
        private static object DispatchToMain(string areaId, MethodBase original)
        {
            // 构造「调用原方法体」的委托。
            MethodInfo mi = (MethodInfo)original;
            Func<string, object> body = id => mi.Invoke(null, new object[] { id });

            // RimBridgeMainThread.Invoke<T>(Func<T>, int) 泛型。
            // lambda body 在主线程执行；此时 inDispatch=true（ThreadStatic 主线程上未置！）。
            // 注意：ThreadStatic inDispatch 在主线程回调时，主线程自己的槽位是 false！
            // 进入 mi.Invoke → 我们的 prefix 在主线程再次触发 → 主线程 isMain=true → return true 放行，
            // 原方法体在主线程执行（安全）。因此无需依赖 inDispatch，主线程 isMain 判断即可防死锁。
            // 故这里不用 inDispatch 也能正确工作；保留 inDispatch 仅作额外保险（读线程槽位）。

            MethodInfo genericInvoke = rmtInvokeT.MakeGenericMethod(typeof(object));
            object[] args = { (Func<object>)(() => body(areaId)), DispatchTimeoutMs };
            object result = genericInvoke.Invoke(null, args);
            return result;
        }

        private static MethodInfo FindInvokeT(Type t)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (m.Name == "Invoke" && m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 1)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType.IsGenericType &&
                        ps[0].ParameterType.GetGenericTypeDefinition() == typeof(Func<>))
                        return m;
                }
            }
            return null;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}

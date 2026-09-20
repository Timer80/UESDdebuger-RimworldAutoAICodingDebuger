using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace UELoader
{
    /// <summary>
    /// 热重载纯逻辑校验（无 Verse/Unity 依赖，可独立单测）。
    /// 所有方法为静态、无副作用；失败不抛异常，返回 bool/列表。
    /// </summary>
    public static class HotReloadValidator
    {
        static readonly BindingFlags AllBindings =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>枚举类型上声明的全部方法（含不可重载者，供调用方分类计数）。</summary>
        public static IEnumerable<MethodInfo> EnumerateDeclaredMethods(Type type)
            => type.GetMethods(AllBindings);

        /// <summary>枚举类型上可重载的方法（跳过构造器/泛型定义/静态构造器/属性访问器/事件访问器/无方法体者）。</summary>
        public static IEnumerable<MethodInfo> EnumerateReloadableMethods(Type type)
        {
            foreach (MethodInfo m in type.GetMethods(AllBindings))
            {
                if (ShouldSkipMethod(m)) continue;
                yield return m;
            }
        }

        /// <summary>
        /// 在旧类型上按「名字 + 参数类型全名序列 + 返回类型」精确匹配新方法。
        /// 替代 Type.GetMethod(name, flags)：后者在类型有重载时抛 AmbiguousMatchException，
        /// 且无法区分重载（会取到任意一个重载）。找不到返回 null。
        /// </summary>
        public static MethodInfo FindMatchingMethod(Type oldType, MethodInfo freshMethod)
        {
            if (oldType == null || freshMethod == null) return null;
            foreach (MethodInfo m in oldType.GetMethods(AllBindings))
            {
                if (!string.Equals(m.Name, freshMethod.Name, StringComparison.Ordinal)) continue;
                if (SignaturesMatch(m, freshMethod)) return m;
            }
            return null;
        }

        /// <summary>
        /// 是否应跳过（不可 detour）：构造器 / 泛型定义 / 静态构造器 / 属性访问器 / 事件访问器 /
        /// 抽象方法 / 接口方法 / 任何无 IL 方法体者（PrepareMethod+GetFunctionPointer 必定失败）。
        /// </summary>
        public static bool ShouldSkipMethod(MethodBase m)
        {
            if (m == null) return true;
            if (m.IsConstructor || m.IsGenericMethodDefinition
                || (m.IsStatic && m.IsSpecialName && m.Name == ".cctor")) return true;
            if (m.IsSpecialName && (m.Name.StartsWith("get_") || m.Name.StartsWith("set_")
                || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_"))) return true;
            if (m.IsAbstract) return true;
            if (m.DeclaringType != null && m.DeclaringType.IsInterface) return true;
            // 开放泛型类型（如 AnimationCompBase<T> 这种泛型定义）的方法没有单一代码地址：
            // PrepareMethod/GetFunctionPointer 必定失败（2026-09-20 TEXT20 实测 detourFailed=1）。
            // 泛型类型的方法不在热重载覆盖范围（封闭泛型实例是运行期生成的，无法定位其影子类型）。
            if (m.DeclaringType != null && m.DeclaringType.IsGenericTypeDefinition) return true;
            if (GetIl(m) == null) return true;
            return false;
        }

        /// <summary>签名对比（影子类型兼容）：参数类型全名序列 + 返回类型全名逐项相等。</summary>
        public static bool SignaturesMatch(MethodInfo old, MethodInfo fresh)
        {
            if (!string.Equals(old.Name, fresh.Name, StringComparison.Ordinal)) return false;
            ParameterInfo[] op = old.GetParameters();
            ParameterInfo[] fp = fresh.GetParameters();
            if (op.Length != fp.Length) return false;
            for (int i = 0; i < op.Length; i++)
            {
                if (!string.Equals(FullTypeName(op[i].ParameterType), FullTypeName(fp[i].ParameterType), StringComparison.Ordinal))
                    return false;
            }
            return string.Equals(FullTypeName(old.ReturnType), FullTypeName(fresh.ReturnType), StringComparison.Ordinal);
        }

        /// <summary>类型全名（含泛型参数名，影子类型按名称匹配）。</summary>
        public static string FullTypeName(Type t)
        {
            if (t.IsGenericType)
            {
                string name = t.GetGenericTypeDefinition().FullName ?? t.Name;
                int tick = name.IndexOf('`');
                if (tick >= 0) name = name.Substring(0, tick);
                return name + "<" + string.Join(",", t.GetGenericArguments().Select(FullTypeName)) + ">";
            }
            return t.FullName ?? t.Name;
        }

        // ---- IL 扫描基础设施 ----

        static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
        static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

        static HotReloadValidator()
        {
            foreach (FieldInfo fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (fi.FieldType != typeof(OpCode)) continue;
                var op = (OpCode)fi.GetValue(null);
                ushort code = unchecked((ushort)op.Value);
                if (code < 0x100) OneByteOpCodes[code] = op;
                else if ((code & 0xff00) == 0xfe00) TwoByteOpCodes[code & 0xff] = op;
            }
        }

        static bool IsFieldOp(OpCode op)
            => op == OpCodes.Ldfld || op == OpCodes.Ldflda || op == OpCodes.Stfld
            || op == OpCodes.Ldsfld || op == OpCodes.Ldsflda || op == OpCodes.Stsfld;

        /// <summary>操作数长度（字节，不含操作码本身）；InlineSwitch 额外读出分支数；未知类型返回 -1。</summary>
        static int OperandSize(OperandType t, byte[] il, int pos)
        {
            switch (t)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                case OperandType.ShortInlineBrTarget: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI:
                case OperandType.InlineBrTarget:
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                case OperandType.ShortInlineR: return 4;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    if (pos + 4 > il.Length) return -1;
                    int n = BitConverter.ToInt32(il, pos);
                    if (n < 0) return -1;
                    return 4 + n * 4;
                default: return -1;
            }
        }

        /// <summary>方法体 IL 字节；无方法体（抽象/接口/extern）或读取失败返回 null。</summary>
        public static byte[] GetIl(MethodBase method)
        {
            try
            {
                MethodBody body = method.GetMethodBody();
                return body == null ? null : body.GetILAsByteArray();
            }
            catch { return null; }
        }

        /// <summary>
        /// 解析方法体引用的字段（反射扫 IL → FieldInfo）。与 FieldReferenceKeys 共用同一次扫描逻辑。
        /// </summary>
        public static List<FieldInfo> FieldReferences(MethodBase method)
        {
            var list = new List<FieldInfo>();
            byte[] il = GetIl(method);
            if (il == null) return list;
            Module module = method.Module;
            int i = 0;
            while (i < il.Length)
            {
                OpCode op;
                byte b = il[i++];
                if (b == 0xFE)
                {
                    if (i >= il.Length) break;
                    op = TwoByteOpCodes[il[i++]];
                }
                else op = OneByteOpCodes[b];
                if (op.Size == 0) break;                    // 未知操作码（OpCode 默认值）→ 停止扫描
                int size = OperandSize(op.OperandType, il, i);
                if (size < 0) break;
                if (IsFieldOp(op) && size == 4 && i + 4 <= il.Length)
                {
                    try
                    {
                        FieldInfo f = module.ResolveField(BitConverter.ToInt32(il, i));
                        if (f != null) list.Add(f);
                    }
                    catch { /* 泛型上下文等无法解析的 token：跳过 */ }
                }
                i += size;
            }
            return list;
        }

        /// <summary>
        /// 反射扫描方法体 IL，解析字段 token，返回「声明类型全名|字段名」集合。
        /// 关键：两侧程序集都在内存里（旧程序集是启动时 LoadFrom 的、新程序集是 Load(byte[]) 的），
        /// 因此绝不读磁盘——磁盘文件已被新构建覆盖，读盘得到的是新方法体。
        /// </summary>
        public static HashSet<string> FieldReferenceKeys(MethodBase method)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (FieldInfo f in FieldReferences(method))
                if (f.DeclaringType != null) keys.Add(f.DeclaringType.FullName + "|" + f.Name);
            return keys;
        }

        /// <summary>
        /// 字段引用集一致性（按**字段名**比较，与原 Cecil 实现同语义）。
        /// 只比名字不比对声明类型：新旧程序集里同名类型（影子类型）的声明类型全名一致，
        /// 而单测夹具用不同类模拟新旧版本，若把类型名纳入键会把"仅改逻辑"误判为变化。
        /// 声明类型维度的安全由 FieldAccessSafe 的布局比对负责。
        /// </summary>
        public static bool FieldReferencesMatch(MethodInfo old, MethodInfo fresh)
            => FieldNames(old).SetEquals(FieldNames(fresh));

        /// <summary>只取字段名（去掉「声明类型全名|」前缀）。</summary>
        static HashSet<string> FieldNames(MethodBase method)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string key in FieldReferenceKeys(method))
            {
                int sep = key.LastIndexOf('|');
                names.Add(sep >= 0 ? key.Substring(sep + 1) : key);
            }
            return names;
        }

        /// <summary>
        /// 类型字段布局签名（规范化）：基类层级在前，**每层内按 Ordinal 排序**后收集
        /// 「名字:类型:静态性」。
        ///
        /// 为什么必须排序：`Type.GetFields()` 的返回顺序**未定义**——实测同一程序集内
        /// SampleV7 返回 [a,b]、SampleV8 返回 [b,a]（声明顺序完全相同）。若按原始顺序比对，
        /// 会把"布局其实没变"的类型判成变化 → 大量假阳性拒绝
        /// （2026-09-19 TEXT30 实测：9 个拒绝里多数由此而来）。
        ///
        /// 语义：能检出**增删字段、改名、改类型、改静态性**（含"中间插入字段导致后续偏移平移"，
        /// 因为集合本身变了）；**不能**检出"字段集合完全相同、仅声明顺序调换"（极端罕见，
        /// 且与反射顺序不可区分）。这是为了消除假阳性而接受的取舍。
        /// </summary>
        public static List<string> TypeLayoutSignature(Type type)
        {
            var chain = new List<Type>();
            for (Type cur = type; cur != null && cur != typeof(object); cur = cur.BaseType) chain.Add(cur);
            chain.Reverse();
            var sig = new List<string>();
            foreach (Type cur in chain)
            {
                var level = new List<string>();
                foreach (FieldInfo f in cur.GetFields(BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    level.Add(f.Name + ":" + FullTypeName(f.FieldType) + (f.IsStatic ? ":static" : ":instance"));
                }
                level.Sort(StringComparer.Ordinal);
                sig.AddRange(level);
            }
            return sig;
        }

        /// <summary>布局完全一致（字段名/类型/顺序/静态性）⇒ 字段偏移一致 ⇒ 影子类型 detour 安全。</summary>
        public static bool TypeLayoutMatches(Type oldType, Type freshType)
        {
            if (oldType == null || freshType == null) return false;
            List<string> a = TypeLayoutSignature(oldType);
            List<string> b = TypeLayoutSignature(freshType);
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>
        /// <summary>
        /// detour 前的字段安全闸门：**只保留真正与内存安全相关的判据**——被访问类型的字段布局。
        /// 对新旧方法体共同引用到的、且定义在被重载程序集内的每个类型，比对旧/新布局
        /// （字段名/类型/顺序/静态性）；布局一致 ⇒ 字段偏移一致 ⇒ 影子类型 detour 安全。
        ///
        /// 2026-09-19 TEXT30 实测修正两处过度保守：
        /// 1) 取消"字段引用集必须一致"的硬拒绝——改逻辑时方法换个字段读是常态（如 Postfix 改读
        ///    另一个既有字段），只要涉及类型布局未变，偏移就是安全的；真正的风险由布局闸门覆盖；
        /// 2) 引用字段的声明类型改为**新旧两侧合并**检查（原实现只看旧侧，会漏掉
        ///    "新代码新增访问了某个布局已变的类型"这一情形）。
        /// 编译器生成类型（迭代器/异步状态机 &lt;Method&gt;d__N）的布局随方法体改变，其成员与
        /// 工厂方法仍会被拒——这是有意的保守（旧实例 + 新字段偏移 = 内存错读）。
        /// </summary>
        public static bool FieldAccessSafe(MethodInfo old, MethodInfo fresh, out string reason)
        {
            reason = null;
            Assembly oldAsm = old.DeclaringType != null ? old.DeclaringType.Assembly : old.Module.Assembly;
            Assembly freshAsm = fresh.DeclaringType != null ? fresh.DeclaringType.Assembly : fresh.Module.Assembly;

            var checkedNames = new HashSet<string>(StringComparer.Ordinal);
            var refs = new List<FieldInfo>();
            refs.AddRange(FieldReferences(old));
            refs.AddRange(FieldReferences(fresh));
            foreach (FieldInfo f in refs)
            {
                Type dt = f.DeclaringType;
                if (dt == null) continue;
                bool inOld = dt.Assembly == oldAsm;
                bool inFresh = dt.Assembly == freshAsm;
                // 框架/其他程序集的类型（Verse.Map、Verse.IntVec3、System.Diagnostics.Stopwatch 等）
                // 未被重载，新旧代码引用的是同一个 Type 对象 → 无布局风险，跳过
                if (!inOld && !inFresh) continue;
                if (!checkedNames.Add(dt.FullName)) continue;

                Type oldT = oldAsm.GetType(dt.FullName, false);
                Type freshT = freshAsm.GetType(dt.FullName, false);
                if (oldT == null || freshT == null)
                {
                    reason = "类型缺失: " + dt.FullName;
                    return false;
                }
                if (!TypeLayoutMatches(oldT, freshT))
                {
                    reason = DescribeLayoutChange(dt.FullName);
                    return false;
                }
            }
            if (old.DeclaringType != null && fresh.DeclaringType != null
                && !TypeLayoutMatches(old.DeclaringType, fresh.DeclaringType))
            {
                reason = DescribeLayoutChange(fresh.DeclaringType.FullName);
                return false;
            }
            return true;
        }

        /// <summary>布局变化的可读原因；编译器生成类型（迭代器/异步状态机/闭包）给出可操作建议。</summary>
        static string DescribeLayoutChange(string fullName)
        {
            bool compilerGenerated = fullName.IndexOf('<') >= 0;
            return compilerGenerated
                ? "编译器生成类型布局变化: " + fullName
                  + "（迭代器/异步状态机的局部变量即其字段，改方法体就会改布局 → 其成员与工厂方法无法安全热重载；建议把改动逻辑外移到普通方法，或重启）"
                : "字段布局变化: " + fullName;
        }

        /// <summary>
        /// 从 Harmony.GetPatchedMethods() 结果中过滤出"patch 方法声明程序集 ∈ targets"的目标方法。
        /// 用途：热重载 mod 重打模式按"patch 代码属于目标旧程序集（及其历代热加载程序集）"精确卸载。
        /// </summary>
        public static List<MethodBase> FilterPatchesByAssembly(
            IEnumerable<MethodBase> patchedMethods, Assembly target)
            => FilterPatchesByAssembly(patchedMethods, new[] { target });

        /// <summary>同上，targets 为多代程序集集合（启动代 + 历代热加载代）。</summary>
        public static List<MethodBase> FilterPatchesByAssembly(
            IEnumerable<MethodBase> patchedMethods, ICollection<Assembly> targets)
        {
            var result = new List<MethodBase>();
            foreach (MethodBase m in patchedMethods)
            {
                try
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    bool hit = false;
                    foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Finalizers, info.Transpilers })
                    {
                        if (list == null) continue;
                        foreach (var p in list)
                        {
                            if (p.PatchMethod != null && p.PatchMethod.DeclaringType != null
                                && targets.Contains(p.PatchMethod.DeclaringType.Assembly)) { hit = true; break; }
                        }
                        if (hit) break;
                    }
                    if (hit) result.Add(m);
                }
                catch { /* 单个方法查询失败跳过 */ }
            }
            return result;
        }

        /// <summary>
        /// 枚举**全进程**被 Harmony patch 的目标方法。
        ///
        /// 必须用静态 `Harmony.GetAllPatchedMethods()`：实测 0Harmony 2.4.1 下
        /// `Harmony.GetPatchedMethods()` 是**实例方法且按 owner 作用域**——任意新实例返回 0
        /// （2026-09-19 实测：`new Harmony("probe").GetPatchedMethods()` → totalTargets=0，
        /// 而静态版 → 495）。用错会同时搞坏两件事：① 卸载漏掉历代补丁（跨代累积）；
        /// ② skipPatched 近似为空 → 对"已被补丁的方法"做 detour，与 Harmony 跳转链打架。
        ///
        /// 为兼容旧版 Harmony（无静态版）用反射探测，取不到再退回实例方法。
        /// </summary>
        public static List<MethodBase> AllPatchedMethods(HarmonyLib.Harmony instance)
        {
            try
            {
                MethodInfo mi = typeof(HarmonyLib.Harmony).GetMethod("GetAllPatchedMethods",
                    BindingFlags.Public | BindingFlags.Static);
                if (mi != null)
                {
                    var all = mi.Invoke(null, null) as IEnumerable<MethodBase>;
                    if (all != null) return all.ToList();
                }
            }
            catch { /* 反射失败 → 退回实例方法 */ }
            try { return instance != null ? instance.GetPatchedMethods().ToList() : new List<MethodBase>(); }
            catch { return new List<MethodBase>(); }
        }

        /// <summary>
        /// 收集"patch 方法来自 targets 程序集"的 (目标方法, patch 方法) 对。
        /// 卸载时按**单个 patch 方法**精确移除（Harmony.Unpatch(target, patchMethod)），
        /// 而不是按 owner 全量卸载——同一 owner 可能同时补丁了多个程序集（如 mod 的
        /// 主 dll + 附属 dll），按 owner 会误删未被重载那一侧的补丁。
        /// </summary>
        public static List<KeyValuePair<MethodBase, MethodInfo>> CollectPatchesFromAssemblies(
            IEnumerable<MethodBase> patchedMethods, ICollection<Assembly> targets)
        {
            var pairs = new List<KeyValuePair<MethodBase, MethodInfo>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (MethodBase m in patchedMethods)
            {
                try
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    foreach (var list in new[] { info.Prefixes, info.Postfixes, info.Finalizers, info.Transpilers })
                    {
                        if (list == null) continue;
                        foreach (var p in list)
                        {
                            MethodInfo pm = p.PatchMethod;
                            if (pm == null || pm.DeclaringType == null) continue;
                            if (!targets.Contains(pm.DeclaringType.Assembly)) continue;
                            // 去重键：目标方法 + patch 方法（同一 patch 可能被枚举多次）
                            string key = m.DeclaringType.FullName + "|" + m.Name + "|" + m.GetParameters().Length
                                + "||" + pm.DeclaringType.FullName + "|" + pm.Name;
                            if (seen.Add(key))
                                pairs.Add(new KeyValuePair<MethodBase, MethodInfo>(m, pm));
                        }
                    }
                }
                catch { /* 单个方法查询失败跳过 */ }
            }
            return pairs;
        }
    }
}

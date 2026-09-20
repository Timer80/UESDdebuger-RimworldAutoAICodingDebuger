using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UELoader;

namespace HotReloadTests
{
    internal static class Program
    {
        static int failures;

        static void Check(string name, bool cond, string detail = null)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name
                + (cond || string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
            if (!cond) failures++;
        }

        // 测试载体类型：v1 方法体引用的字段集 == v2（只改逻辑）；
        // v3 新增字段 → 字段集应不同。
        class SampleV1
        {
            int a;
            string b;
            string X { get { return b; } }
            public string Build() { return a.ToString() + b; }
        }
        class SampleV2
        {
            int a;
            string b;
            public string Build() { var s = a + 1; return s.ToString() + b + "!v2"; }
        }
        class SampleV3
        {
            int a;
            string b;
            int c; // 新增字段 → 字段引用集变化
            public string Build() { return a.ToString() + b + c; }
        }
        // v4：在 a 与 b 之间插入 x → Build 的字段引用集与 v1 完全相同，但 b 的偏移平移
        class SampleV4
        {
            int a;
            int x;
            string b;
            public string Build() { return a.ToString() + b; }
        }

        // v5/v6：方法引用"框架程序集"里的字段（string.Empty → ldsfld System.String::Empty）。
        // 这类字段的声明类型不在被重载程序集内 → 不得因"类型缺失"被拒（2026-09-19 TEXT31 实测回归）
        class SampleV5
        {
            public string Make() { return string.Empty + "x"; }
        }
        class SampleV6
        {
            public string Make() { return string.Empty + "y"; }   // 只改逻辑，引用集相同
        }

        // v7/v8：布局相同（都有 a、b），但方法读取的字段不同（v7 读 a，v8 读 b）。
        // TEXT30 实测的 Postfix 场景：改逻辑换了读取的既有字段 → 偏移仍安全，必须允许
        class SampleV7
        {
            int a;
            int b;
            public int Use() { return a; }
        }
        class SampleV8
        {
            int a;
            int b;
            public int Use() { return b; }
        }

        // 重载宿主：旧代码 oldType.GetMethod("F") 会抛 AmbiguousMatchException
        class OverloadHost
        {
            public int F(int x) { return x; }
            public int F(string s) { return s.Length; }
            public int F(int x, int y) { return x + y; }
        }

        abstract class AbstractHost { public abstract int A(); }
        // 开放泛型类型：其方法拿不到函数入口（TEXT20 实测 detourFailed=1）
        class GenericHost<T> { public int F(T x) { return 0; } }
        interface IHost { int B(); }

        static bool TestPrefix() => true;

        static int Main()
        {
            MethodInfo b1 = typeof(SampleV1).GetMethod("Build");
            MethodInfo b2 = typeof(SampleV2).GetMethod("Build");
            MethodInfo b3 = typeof(SampleV3).GetMethod("Build");
            MethodInfo b4 = typeof(SampleV4).GetMethod("Build");

            // 签名对比
            Check("signature v1==v2", HotReloadValidator.SignaturesMatch(b1, b2));
            Check("signature v1==v3", HotReloadValidator.SignaturesMatch(b1, b3));

            // 方法过滤
            Check("reloadable Build", !HotReloadValidator.ShouldSkipMethod(b1));
            Check("skip ctor", HotReloadValidator.ShouldSkipMethod(typeof(SampleV1).GetConstructor(Type.EmptyTypes)));
            Check("skip property getter", HotReloadValidator.ShouldSkipMethod(typeof(SampleV1).GetProperty("X", BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true)));

            // 重载匹配：按参数类型序列精确命中，且不抛 AmbiguousMatchException
            MethodInfo fInt = typeof(OverloadHost).GetMethod("F", new[] { typeof(int) });
            MethodInfo fStr = typeof(OverloadHost).GetMethod("F", new[] { typeof(string) });
            Check("find overload (int)", ReferenceEquals(HotReloadValidator.FindMatchingMethod(typeof(OverloadHost), fInt), fInt));
            Check("find overload (string)", ReferenceEquals(HotReloadValidator.FindMatchingMethod(typeof(OverloadHost), fStr), fStr));
            Check("find missing -> null", HotReloadValidator.FindMatchingMethod(typeof(OverloadHost), b1) == null);

            // 无 IL 方法体（抽象/接口）必须跳过，否则 detour 必然 "无法获取方法入口"
            Check("skip abstract", HotReloadValidator.ShouldSkipMethod(typeof(AbstractHost).GetMethod("A")));
            Check("skip interface method", HotReloadValidator.ShouldSkipMethod(typeof(IHost).GetMethod("B")));
            Check("reloadable concrete overload", !HotReloadValidator.ShouldSkipMethod(fInt));
            Check("skip open-generic type method", HotReloadValidator.ShouldSkipMethod(typeof(GenericHost<>).GetMethod("F")));

            // 反射字段引用集（v1/v2 逻辑不同但引用集相同；v3 新增字段 c → 不同）
            Check("fields v1==v2 (logic only)", HotReloadValidator.FieldReferencesMatch(b1, b2));
            Check("fields v1!=v3 (new field)", !HotReloadValidator.FieldReferencesMatch(b1, b3));

            // 类型字段布局
            Check("layout v1==v2", HotReloadValidator.TypeLayoutMatches(typeof(SampleV1), typeof(SampleV2)));
            Check("layout v1!=v3 (field added)", !HotReloadValidator.TypeLayoutMatches(typeof(SampleV1), typeof(SampleV3)));

            // 关键回归：引用集相同但布局平移 → 必须拒绝（仅比引用集发现不了）
            Check("fields v1==v4 (same refs)", HotReloadValidator.FieldReferencesMatch(b1, b4));
            Check("layout v1!=v4 (field inserted)", !HotReloadValidator.TypeLayoutMatches(typeof(SampleV1), typeof(SampleV4)));

            // 组合安全检查（detour 前置闸门）
            string reason12;
            Check("safe v1->v2", HotReloadValidator.FieldAccessSafe(b1, b2, out reason12));
            string reason14;
            Check("unsafe v1->v4 (offset shift)",
                !HotReloadValidator.FieldAccessSafe(b1, b4, out reason14) && !string.IsNullOrEmpty(reason14));

            // 框架字段（声明类型在别的程序集）不得被误判"类型缺失"
            MethodInfo m5 = typeof(SampleV5).GetMethod("Make");
            MethodInfo m6 = typeof(SampleV6).GetMethod("Make");
            string reason56;
            Check("safe v5->v6 (framework field, no false missing-type)",
                HotReloadValidator.FieldAccessSafe(m5, m6, out reason56));
            // TEXT30 实测回归：布局相同但读取的字段换了 → 引用集不同但偏移安全，必须允许
            MethodInfo u7 = typeof(SampleV7).GetMethod("Use");
            MethodInfo u8 = typeof(SampleV8).GetMethod("Use");
            Check("refs v7!=v8 (different field read)", !HotReloadValidator.FieldReferencesMatch(u7, u8));
            string reason78;
            bool safe78 = HotReloadValidator.FieldAccessSafe(u7, u8, out reason78);
            Check("safe v7->v8 (same layout, different field read)", safe78,
                reason78
                + " | V7=[" + string.Join(",", HotReloadValidator.TypeLayoutSignature(typeof(SampleV7))) + "]"
                + " V8=[" + string.Join(",", HotReloadValidator.TypeLayoutSignature(typeof(SampleV8))) + "]"
                + " refsOld=[" + string.Join(",", HotReloadValidator.FieldReferenceKeys(u7)) + "]"
                + " refsFresh=[" + string.Join(",", HotReloadValidator.FieldReferenceKeys(u8)) + "]");

            // 反向：布局真的变了必须拒绝
            string reason13;
            Check("unsafe v1->v3 (layout changed)", !HotReloadValidator.FieldAccessSafe(b1, b3, out reason13), reason13);

            // Harmony patch 归属过滤
            var harmony = new Harmony("hotreload.tests");
            MethodInfo target = typeof(SampleV1).GetMethod("Build");
            MethodInfo prefix = typeof(Program).GetMethod("TestPrefix", BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, new HarmonyMethod(prefix));

            Assembly testAsm = typeof(Program).Assembly;
            List<MethodBase> mine = HotReloadValidator.FilterPatchesByAssembly(harmony.GetPatchedMethods(), testAsm);
            Check("patch filter finds own patch", mine.Contains(target));

            Assembly other = typeof(object).Assembly;
            Check("patch filter empty for other asm", !HotReloadValidator.FilterPatchesByAssembly(harmony.GetPatchedMethods(), other).Contains(target));

            // 全局枚举 + 精确收集（回归：实例 GetPatchedMethods 是 owner 作用域的，
            // 用探针实例也必须能看到别的 owner 打的补丁 —— 实测 0Harmony 2.4.1 踩过此坑）
            var probe = new Harmony("hotreload.tests.probe");
            List<MethodBase> allTargets = HotReloadValidator.AllPatchedMethods(probe);
            Check("AllPatchedMethods is global (sees other owners' patches)", allTargets.Contains(target));
            var pairs = HotReloadValidator.CollectPatchesFromAssemblies(allTargets, new[] { testAsm });
            Check("CollectPatchesFromAssemblies finds our (target, patch) pair",
                pairs.Exists(kv => kv.Key == target && kv.Value == prefix));
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURES");
            return failures == 0 ? 0 : 1;
        }
    }
}

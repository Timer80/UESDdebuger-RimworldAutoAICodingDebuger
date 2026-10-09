using System;
using System.Collections.Generic;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>控制台默认 using。含 RimWorld 本体命名空间，裸类型名（GenTicks/Find/Pawn…）可直接编译。
    /// 实测代价：把 Verse/RimWorld/Verse.AI/RimWorld.Planet 与 System/UnityEngine/UnityEngine.UI 同时 using，
    /// 只有 5 个名字会歧义 —— Keyframe、Graphic、GridLayout、Text、WeakReference&lt;T&gt;（低频，需要时全限定）。
    /// ⚠️ 故意**不含**模组命名空间：实测 137 个模组命名空间里有 34 个类型名与本体核心重名、19 组模组之间重名
    ///    （Coroutine@MoonSharp / FloatRange@RIMAPI / IServiceProvider@RIMAPI / MethodAttributes@Mono.Cecil …），
    ///    全量塞进来会制造新的"每次都得试一下"。模组命名空间走 add_using_directive / 配置项按需追加。
    /// </summary>
    public static class DefaultUsings
    {
        public static readonly string[] BuiltIn =
        {
            "System", "System.Linq", "System.Text", "System.Collections", "System.Collections.Generic",
            "System.Reflection", "UnityEngine", "UniverseLib",
            "Verse", "RimWorld", "Verse.AI", "RimWorld.Planet",
        };

        public static List<string> Parse(string semicolonSeparated)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(semicolonSeparated)) return result;
            foreach (string raw in semicolonSeparated.Split(';'))
            {
                string ns = raw.Trim();
                if (ns.Length == 0) continue;
                if (!result.Contains(ns)) result.Add(ns);
            }
            return result;
        }
    }
}

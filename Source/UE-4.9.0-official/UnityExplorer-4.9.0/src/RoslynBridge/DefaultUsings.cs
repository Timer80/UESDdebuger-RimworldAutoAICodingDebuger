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
    /// ⚠️ B1（根因清扫轮）：每项都必须标注**来源程序集**（见 <see cref="SourceAssembly"/>）——
    ///    引用集缺该件时必须把对应 using 剔除，否则一个 using 会让**每一次提交**都编译失败
    ///    （实测症状：`UniverseLib` 缺引用时连 `new[]{1,2,3}.Sum()` 都报 CS0246 'UniverseLib'，
    ///    而求值器仍是 Roslyn ⇒ 用户"看得见 Roslyn 却什么都写不了、报错还指向他没写过的命名空间"）。
    /// </summary>
    public static class DefaultUsings
    {
        public static readonly string[] BuiltIn =
        {
            "System", "System.Linq", "System.Text", "System.Collections", "System.Collections.Generic",
            "System.Reflection", "UnityEngine", "UniverseLib",
            "Verse", "RimWorld", "Verse.AI", "RimWorld.Planet",
            // §19：UE 自身命名空间（由**宿主自引用**提供）——裸名 ConsoleController/InspectorManager 可直接用。
            // ⚠️ 既有 config.cfg 里存的是**旧 12 项字符串**（配置值优先于默认值）⇒ 老配置不会自动获得本项；
            //    助手本身不依赖它（继承成员 + 全限定名都能用），属便利项。
            "UnityExplorer",
        };

        /// <summary>
        /// "来源 = 宿主自引用（UE 自己的程序集）"标记（§19）。UE 程序集的简单名随配置变化
        /// （`UnityExplorer.STANDALONE.Mono` / `UnityExplorer.ML.Mono` …），故用标记代替硬编码；
        /// `ReferenceProfile.CollectFrom` 把它翻译成"自引用是否真的进了引用集"。
        /// </summary>
        public const string SelfAssemblyToken = "@self";

        /// <summary>
        /// 默认 using 根命名空间 → **提供该命名空间的程序集简单名**（B1 一致性表，13/13 全覆盖；
        /// 由测试 <c>TestDefaultUsingsSources</c> 断言"每个 BuiltIn 项都有来源"，新增默认项漏标即红）。
        /// 用途：`ReferenceScanner.Collect` 用引用集实际可用性过滤默认 using（缺失→剔除+明确日志），
        /// 把"一个 using 拖垮整条 Roslyn 路径"从**硬编码特判**升级为**数据驱动护栏**。
        /// 说明：Verse/RimWorld/Verse.AI/RimWorld.Planet 的根命名空间都在本体 Assembly-CSharp；
        ///       System.* 都在 mscorlib（System.Core 只需为 System.Linq 提供 Enumerable 的根命名空间
        ///       —— using 解析按**根命名空间**判定，故标 mscorlib 与标 System.Core 等价，取更保守的 mscorlib）；
        ///       `UnityExplorer` 在宿主自引用里（见 <see cref="SelfAssemblyToken"/>）。
        /// </summary>
        public static readonly Dictionary<string, string> SourceAssembly = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "System", "mscorlib" },
            { "System.Linq", "mscorlib" },
            { "System.Text", "mscorlib" },
            { "System.Collections", "mscorlib" },
            { "System.Collections.Generic", "mscorlib" },
            { "System.Reflection", "mscorlib" },
            { "UnityEngine", "UnityEngine" },
            { "UniverseLib", "UniverseLib.Mono" },
            { "Verse", "Assembly-CSharp" },
            { "RimWorld", "Assembly-CSharp" },
            { "Verse.AI", "Assembly-CSharp" },
            { "RimWorld.Planet", "Assembly-CSharp" },
            { "UnityExplorer", SelfAssemblyToken },
        };

        /// <summary>该默认 using 需要引用集里出现哪个程序集简单名（未标注 → 返回 null = 无法判定，按可用处理）。</summary>
        public static string OwnerOf(string rootNamespace)
        {
            string owner;
            return rootNamespace != null && SourceAssembly.TryGetValue(rootNamespace, out owner) ? owner : null;
        }

        /// <summary>
        /// 从给定 using 列表里挑出**引用集缺其来源程序集**的项（B1 护栏核心；纯函数，离线可测）。
        /// 未标注来源的项（用户配置里追加的模组命名空间等）一律保留——只拦"我们自己的默认项"。
        /// </summary>
        public static List<string> Unavailable(IEnumerable<string> usings, Func<string, bool> assemblyAvailable)
        {
            var result = new List<string>();
            if (usings == null || assemblyAvailable == null) return result;
            foreach (string ns in usings)
            {
                string owner = OwnerOf(ns);
                if (owner == null) continue;
                if (!assemblyAvailable(owner)) result.Add(ns);
            }
            return result;
        }

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

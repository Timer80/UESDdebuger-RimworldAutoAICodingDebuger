namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 运行时不提供的编译器要求类型（Unity Mono 缺；Span/Index/Range/HashCode 等已存在，勿重复声明）。
    /// isExternalInit: C# 9 record / init；其余三个: C# 11 required。
    /// </summary>
    public static class PolyfillSource
    {
        public const string FileName = "__RoslynPolyfills.cs";

        public const string Code = @"
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct |
                           System.AttributeTargets.Field | System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : System.Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) { FeatureName = featureName; }
        public string FeatureName { get; }
        public bool IsOptional { get; set; }
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }

    // §17 追加：C# 13 `params` 集合（params 修饰非数组集合类型，如 params IEnumerable&lt;int&gt;）
    // 需要编译器合成的 ParamCollectionAttribute（.NET 8+ 才有）。Unity Mono/net472 没有 ⇒ 缺它时
    // `params` 集合方法报 CS0656/CS0246。补上即可用（与 IsExternalInit/RequiredMember 同款 polyfill）。
    [System.AttributeUsage(System.AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    internal sealed class ParamCollectionAttribute : System.Attribute { }
}

namespace System.Diagnostics.CodeAnalysis
{
    [System.AttributeUsage(System.AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : System.Attribute { }
}
";
    }
}

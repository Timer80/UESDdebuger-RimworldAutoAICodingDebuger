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
}

namespace System.Diagnostics.CodeAnalysis
{
    [System.AttributeUsage(System.AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : System.Attribute { }
}
";
    }
}

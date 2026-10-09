using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace UnityExplorer.RoslynBridge
{
    public sealed class CompileResult
    {
        public bool Success;
        public byte[] PeBytes;
        public string Diagnostics = "";
        public int ErrorCount;
    }

    /// <summary>parse → compile → emit 到内存。不负责加载（调用方决定 Assembly.Load 或 CreateFromImage）。</summary>
    public static class RoslynCompiler
    {
        static readonly CSharpParseOptions ParseOptions =
            new CSharpParseOptions(LanguageVersion.Latest);

        static readonly CSharpCompilationOptions CompileOptions =
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        public static CompileResult Compile(string assemblyName, string source,
                                            IEnumerable<string> referencePaths, bool includePolyfills,
                                            IEnumerable<string> extraSources = null,
                                            string extraBaseSource = null)
        {
            var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source, ParseOptions) };
            if (extraSources != null)
                foreach (string s in extraSources)
                    trees.Add(CSharpSyntaxTree.ParseText(s, ParseOptions));
            if (includePolyfills)
                trees.Add(CSharpSyntaxTree.ParseText(PolyfillSource.Code, ParseOptions));

            var refs = new List<MetadataReference>();
            foreach (string p in referencePaths)
            {
                try { refs.Add(MetadataReference.CreateFromFile(p)); }
                catch { /* 非托管/损坏 dll：跳过，Roslyn 只读元数据 */ }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName, trees, refs, CompileOptions);

            var result = new CompileResult();
            using (var ms = new MemoryStream())
            {
                EmitResult emit = compilation.Emit(ms);
                result.ErrorCount = 0;
                foreach (Diagnostic d in emit.Diagnostics)
                    if (d.Severity == DiagnosticSeverity.Error) result.ErrorCount++;
                result.Diagnostics = DiagnosticFormatter.FormatAll(emit.Diagnostics);
                result.Success = emit.Success && result.ErrorCount == 0;
                if (result.Success)
                    result.PeBytes = ms.ToArray();
            }
            return result;
        }
    }
}

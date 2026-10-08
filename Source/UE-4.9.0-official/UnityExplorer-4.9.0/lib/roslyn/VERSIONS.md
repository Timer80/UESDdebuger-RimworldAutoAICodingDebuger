# Roslyn 载荷版本清单（8 件套）

进程内 `Assembly.Load` 用的固定依赖集，随包分发。来源全部为官方 NuGet 包（nuget.org flat-container / 本机 `%USERPROFILE%\.nuget\packages` 缓存），**不得**使用 Visual Studio 安装目录副本（不可再分发）。

| 文件 | 版本 | 来源包（NuGet） | 程序集版本（实测 `AssemblyName.Version`） |
|---|---|---|---|
| Microsoft.CodeAnalysis.dll | 4.14.0 | microsoft.codeanalysis.common/4.14.0/lib/netstandard2.0 | 4.14.0.0 |
| Microsoft.CodeAnalysis.CSharp.dll | 4.14.0 | microsoft.codeanalysis.csharp/4.14.0/lib/netstandard2.0 | 4.14.0.0 |
| System.Collections.Immutable.dll | 8.0.0 | system.collections.immutable/8.0.0/lib/netstandard2.0 | 8.0.0.0 |
| System.Reflection.Metadata.dll | 8.0.0 | system.reflection.metadata/8.0.0/lib/netstandard2.0 | 8.0.0.0 |
| System.Memory.dll | 4.5.3 | system.memory/4.5.3/lib/netstandard2.0 | 4.0.1.1 |
| System.Runtime.CompilerServices.Unsafe.dll | 6.0.0 | system.runtime.compilerservices.unsafe/6.0.0/lib/netstandard2.0 | 6.0.0.0 |
| System.Threading.Tasks.Extensions.dll | 4.5.2 | system.threading.tasks.extensions/4.5.2/lib/netstandard2.0 | 4.2.0.0 |
| System.Numerics.Vectors.dll | 程序集 4.1.3.0（包 4.4.0 的 lib/net46） | system.numerics.vectors/4.4.0 | 4.1.3.0 |

> `System.Numerics.Vectors` 选包说明：曾评估包 4.5.0（其 lib/net46 程序集实为 4.1.4.0），因与可行性报告 §4.2 PoC 实测版本 4.1.3.0 不符而弃用——所有顺序/绑定实测证据均在 4.1.3.0 上取得，改用 4.4.0 官方包以保持与实测一致。

换任何一件都必须整套重验（docs/csharp-console-compiler-upgrade-feasibility.md §4.2 的顺序/版本约束）。

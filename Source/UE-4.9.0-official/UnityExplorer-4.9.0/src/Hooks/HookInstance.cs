using HarmonyLib;
#if !STANDALONE_MONO
using Mono.CSharp;
#endif
using System.Text;
using UnityExplorer.CSConsole;
#if STANDALONE_MONO
using UnityExplorer.RoslynBridge;
#endif

namespace UnityExplorer.Hooks
{
    public class HookInstance
    {
        // Static 

#if STANDALONE_MONO
        // 任务 10（Roslyn 链路）：旧的"自有静态 mcs 求值器 + 反射 `Evaluator.source_file` /
        // `TypeDefinition.Definition`"整块删除 —— Roslyn 没有这些内部结构，且第二套编译器状态本身是
        // 两套语义漂移的来源。补丁现在走**控制台当前生效的求值器**（`ConsoleController.Evaluator`）：
        // 于是补丁源码与用户会话同引用集 / 同 usings / 同已定义类型，`var`、模组类型、会话变量都能用。
#else
        static readonly StringBuilder evaluatorOutput;
        static readonly ScriptEvaluator scriptEvaluator = new(new StringWriter(evaluatorOutput = new StringBuilder()));

        static HookInstance()
        {
            scriptEvaluator.Run("using System;");
            scriptEvaluator.Run("using System.Text;");
            scriptEvaluator.Run("using System.Reflection;");
            scriptEvaluator.Run("using System.Collections;");
            scriptEvaluator.Run("using System.Collections.Generic;");
        }
#endif

        // Instance

        public bool Enabled;

        public MethodInfo TargetMethod;
        public string PatchSourceCode;

        readonly string signature;
        PatchProcessor patchProcessor;

        MethodInfo postfix;
        MethodInfo prefix;
        MethodInfo finalizer;
        MethodInfo transpiler;

        public HookInstance(MethodInfo targetMethod)
        {
            this.TargetMethod = targetMethod;
            this.signature = TargetMethod.FullDescription();

            GenerateDefaultPatchSourceCode(targetMethod);

            if (CompileAndGenerateProcessor(PatchSourceCode))
                Patch();
        }

#if STANDALONE_MONO

        // ~~~~~~ Roslyn 路径（交付配置 STANDALONE_Mono；任务 10） ~~~~~~
        // 流程：包装源码 → 交**当前生效求值器**编译并加载产物 → 从产物里定位补丁类型 → 取四个补丁方法
        // → 交给 Harmony 的 PatchProcessor。包装/定位/取方法三段是 RoslynBridge.DynamicPatch 里的**生产代码**，
        // 同一段代码有离线断言（RoslynBridgeTests.TestHookCompile，见报告 §24）。
        public bool CompileAndGenerateProcessor(string patchSource)
        {
            Unpatch();

            try
            {
                patchProcessor = ExplorerCore.Harmony.CreateProcessor(TargetMethod);

                string typeName = DynamicPatch.NewTypeName();
                string code = DynamicPatch.Wrap(typeName, patchSource);

                IScriptEvaluator evaluator = ConsoleController.Evaluator;
                if (evaluator == null)
                    throw new FormatException("C# 控制台求值器尚未就绪（ConsoleController.Evaluator 为 null）。");

                if (!evaluator.Evaluate(code, out string diag))
                    throw new FormatException("Unable to compile the generated patch!\n" + diag);

                // 只有 Roslyn 路径有"编译产物程序集"这一概念；控制台回退 mcs 时动态补丁不可用
                // ——**响亮报错**（进 HookCreator 的日志），不静默"创建成功却不生效"。
                RoslynScriptEvaluator roslyn = evaluator as RoslynScriptEvaluator;
                if (roslyn == null)
                    throw new FormatException("当前求值器是 " + evaluator.GetType().Name
                        + "（Roslyn 未生效，已回退 mcs）⇒ 动态补丁需要 Roslyn 求值器。");

                Assembly asm = roslyn.LastCompiledAssembly;
                if (asm == null)
                    throw new FormatException("补丁已编译但没有拿到编译产物程序集。");

                // ⚠️ 不能 `asm.GetType(typeName)`：REPL 把本次提交的顶层类型嵌进生成类 `Submission_N`
                //（见 DynamicPatch.Locate 的说明）⇒ 按简单名扫描嵌套类型。
                Type patchClass = DynamicPatch.Locate(asm, typeName);
                if (patchClass == null)
                    throw new FormatException("编译产物里找不到补丁类型 " + typeName + "。");

                DynamicPatch.FindPatchMethods(patchClass, out prefix, out postfix, out finalizer, out transpiler);

                if (prefix == null && postfix == null && finalizer == null && transpiler == null)
                    ExplorerCore.LogWarning("补丁源码里没有任何可识别的方法"
                        + "（Prefix/Postfix/Finalizer/Transpiler，且必须是 static）——补丁已编译但不会生效。");

                // Create the harmony patches as defined（顺序与旧实现一致：postfix 先注册）

                if (postfix != null)
                    patchProcessor.AddPostfix(new HarmonyMethod(postfix));
                if (prefix != null)
                    patchProcessor.AddPrefix(new HarmonyMethod(prefix));
                if (finalizer != null)
                    patchProcessor.AddFinalizer(new HarmonyMethod(finalizer));
                if (transpiler != null)
                    patchProcessor.AddTranspiler(new HarmonyMethod(transpiler));

                return true;
            }
            catch (Exception ex)
            {
                // 诊断文本已在抛出的 FormatException 里（编译诊断由求值器带回）；此处只做统一上报。
                ExplorerCore.LogWarning($"Exception generating patch source code: {ex}");
                return false;
            }
        }

#else

        // ~~~~~~ 旧 mcs 路径（非交付配置：ML/BIE/CPP，本配置未编入 Roslyn 四件） ~~~~~~
        // 任务 10 只把**交付配置**（STANDALONE_Mono）迁到 Roslyn；其余 8 个配置的运行时求值器恒为 mcs
        // （RoslynScriptEvaluator 不在本配置的编译集里），若一并删除则 Hooks 功能在这些配置上**整块失效**
        // ⇒ 原实现按配置保留（零行为变更）。偏离简报之处与理由见报告 §24。

        // Evaluator.source_file 
        private static readonly FieldInfo fi_sourceFile = AccessTools.Field(typeof(Evaluator), "source_file");
        // TypeDefinition.Definition
        private static readonly PropertyInfo pi_Definition = AccessTools.Property(typeof(TypeDefinition), "Definition");

        public bool CompileAndGenerateProcessor(string patchSource)
        {
            Unpatch();

            StringBuilder codeBuilder = new();

            try
            {
                patchProcessor = ExplorerCore.Harmony.CreateProcessor(TargetMethod);

                // Dynamically compile the patch method

                codeBuilder.AppendLine($"static class DynamicPatch_{DateTime.Now.Ticks}");
                codeBuilder.AppendLine("{");
                codeBuilder.AppendLine(patchSource);
                codeBuilder.AppendLine("}");

                scriptEvaluator.Run(codeBuilder.ToString());

                if (ScriptEvaluator._reportPrinter.ErrorsCount > 0)
                    throw new FormatException($"Unable to compile the generated patch!");

                // TODO: Publicize MCS to avoid this reflection
                // Get the most recent Patch type in the source file
                TypeContainer typeContainer = ((CompilationSourceFile)fi_sourceFile.GetValue(scriptEvaluator))
                    .Containers
                    .Last(it => it.MemberName.Name.StartsWith("DynamicPatch_"));
                // Get the TypeSpec from the TypeDefinition, then get its "MetaInfo" (System.Type)
                Type patchClass = ((TypeSpec)pi_Definition.GetValue((Class)typeContainer, null)).GetMetaInfo();

                // Create the harmony patches as defined

                postfix = patchClass.GetMethod("Postfix", ReflectionUtility.FLAGS);
                if (postfix != null)
                    patchProcessor.AddPostfix(new HarmonyMethod(postfix));

                prefix = patchClass.GetMethod("Prefix", ReflectionUtility.FLAGS);
                if (prefix != null)
                    patchProcessor.AddPrefix(new HarmonyMethod(prefix));

                finalizer = patchClass.GetMethod("Finalizer", ReflectionUtility.FLAGS);
                if (finalizer != null)
                    patchProcessor.AddFinalizer(new HarmonyMethod(finalizer));

                transpiler = patchClass.GetMethod("Transpiler", ReflectionUtility.FLAGS);
                if (transpiler != null)
                    patchProcessor.AddTranspiler(new HarmonyMethod(transpiler));

                return true;
            }
            catch (Exception ex)
            {
                if (ex is FormatException)
                {
                    string output = scriptEvaluator._textWriter.ToString();
                    string[] outputSplit = output.Split('\n');
                    if (outputSplit.Length >= 2)
                        output = outputSplit[outputSplit.Length - 2];
                    evaluatorOutput.Clear();

                    if (ScriptEvaluator._reportPrinter.ErrorsCount > 0)
                        ExplorerCore.LogWarning($"Unable to compile the code. Evaluator's last output was:\r\n{output}");
                    else
                        ExplorerCore.LogWarning($"Exception generating patch source code: {ex}");
                }
                else
                    ExplorerCore.LogWarning($"Exception generating patch source code: {ex}");

                // ExplorerCore.Log(codeBuilder.ToString());

                return false;
            }
        }

#endif

        static string FullDescriptionClean(Type type)
        {
            string description = type.FullDescription().Replace("+", ".");
            if (description.EndsWith("&"))
                description = $"ref {description.Substring(0, description.Length - 1)}";
            return description;
        }

        private string GenerateDefaultPatchSourceCode(MethodInfo targetMethod)
        {
            StringBuilder codeBuilder = new();

            codeBuilder.Append("static void Postfix(");

            bool isStatic = targetMethod.IsStatic;

            List<string> arguments = new();

            if (!isStatic)
                arguments.Add($"{FullDescriptionClean(targetMethod.DeclaringType)} __instance");

            if (targetMethod.ReturnType != typeof(void))
                arguments.Add($"{FullDescriptionClean(targetMethod.ReturnType)} __result");

            ParameterInfo[] parameters = targetMethod.GetParameters();

            int paramIdx = 0;
            foreach (ParameterInfo param in parameters)
            {
                arguments.Add($"{FullDescriptionClean(param.ParameterType)} __{paramIdx}");
                paramIdx++;
            }

            codeBuilder.Append(string.Join(", ", arguments.ToArray()));

            codeBuilder.Append(")\n");

            // Patch body

            codeBuilder.AppendLine("{");
            codeBuilder.AppendLine("    try {");
            codeBuilder.AppendLine("       StringBuilder sb = new StringBuilder();");
            codeBuilder.AppendLine($"       sb.AppendLine(\"--------------------\");");
            codeBuilder.AppendLine($"       sb.AppendLine(\"{signature}\");");

            if (!targetMethod.IsStatic)
                codeBuilder.AppendLine($"       sb.Append(\"- __instance: \").AppendLine(__instance.ToString());");

            paramIdx = 0;
            foreach (ParameterInfo param in parameters)
            {
                codeBuilder.Append($"       sb.Append(\"- Parameter {paramIdx} '{param.Name}': \")");

                Type pType = param.ParameterType;
                if (pType.IsByRef) pType = pType.GetElementType();
                if (pType.IsValueType)
                    codeBuilder.AppendLine($".AppendLine(__{paramIdx}.ToString());");
                else
                    codeBuilder.AppendLine($".AppendLine(__{paramIdx}?.ToString() ?? \"null\");");

                paramIdx++;
            }

            if (targetMethod.ReturnType != typeof(void))
            {
                codeBuilder.Append("       sb.Append(\"- Return value: \")");
                if (targetMethod.ReturnType.IsValueType)
                    codeBuilder.AppendLine(".AppendLine(__result.ToString());");
                else
                    codeBuilder.AppendLine(".AppendLine(__result?.ToString() ?? \"null\");");
            }

            codeBuilder.AppendLine($"       UnityExplorer.ExplorerCore.Log(sb.ToString());");
            codeBuilder.AppendLine("    }");
            codeBuilder.AppendLine("    catch (System.Exception ex) {");
            codeBuilder.AppendLine($"        UnityExplorer.ExplorerCore.LogWarning($\"Exception in patch of {signature}:\\n{{ex}}\");");
            codeBuilder.AppendLine("    }");

            codeBuilder.AppendLine("}");

            return PatchSourceCode = codeBuilder.ToString();
        }

        public void TogglePatch()
        {
            if (!Enabled)
                Patch();
            else
                Unpatch();
        }

        public void Patch()
        {
            try
            {
                patchProcessor.Patch();

                Enabled = true;
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"Exception hooking method!\r\n{ex}");
            }
        }

        public void Unpatch()
        {
            try
            {
                if (prefix != null)
                    patchProcessor.Unpatch(prefix);
                if (postfix != null)
                    patchProcessor.Unpatch(postfix);
                if (finalizer != null)
                    patchProcessor.Unpatch(finalizer);
                if (transpiler != null)
                    patchProcessor.Unpatch(transpiler);

                Enabled = false;
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"Exception unpatching method: {ex}");
            }
        }
    }
}

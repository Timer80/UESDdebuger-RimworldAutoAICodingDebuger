using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 首次启动自动解压运行时组件。
    ///
    /// 背景：bun.exe（约 113MB）、node.exe（约 86MB）与 MCP/node_modules（3700+ 碎文件）
    /// 不适合直接进工坊（体积/条目数超限），工坊分发版本只携带压缩包：
    /// runtime/bun/bun.zip、runtime/node/node.zip、MCP/node_modules.zip。
    /// 各 IDE 的 MCP 配置指向 runtime/bun/bun.exe（run MCP/index.js），启动脚本则可能用
    /// runtime/node/node.exe，且 index.js 依赖 MCP/node_modules；若这些缺失，MCP 调试桥无法启动。
    ///
    /// 本组件以 RimWorld 官方启动加载模板 [StaticConstructorOnStartup] 执行（与 AutoDeploy 同模式）：
    /// 每次启动幂等检查各运行时的 exe，缺失/空文件且对应 zip 存在时自动解压，
    /// 保证用户配置好 IDE 之前运行时已就绪。任何失败仅记日志，绝不阻断游戏启动。
    /// 解压成功保留 zip（作为 exe 被误删时的修复回退）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class UnzipRuntime
    {
        const string BunDir = "bun";
        const string BunExeName = "bun.exe";
        const string BunZipName = "bun.zip";

        const string NodeDir = "node";
        const string NodeExeName = "node.exe";
        const string NodeZipName = "node.zip";

        const string NodeModulesZipName = "node_modules.zip";

        static UnzipRuntime()
        {
            try
            {
                var mod = LoadedModManager.GetMod<UELoaderMod>();
                string root = mod?.Content?.RootDir;
                if (string.IsNullOrEmpty(root))
                {
                    // 与 AutoDeploy 相同的极端时序保护：拿不到根目录则静默跳过
                    return;
                }
                EnsureBun(root);
                EnsureNode(root);
                EnsureNodeModules(root);
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UnzipRuntime] 初始化失败（不阻断启动）：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>幂等确保 runtime/bun/bun.exe 可用。</summary>
        public static bool EnsureBun(string rootDir)
            => EnsurePortableExe(rootDir, BunDir, BunExeName, BunZipName);

        /// <summary>幂等确保 runtime/node/node.exe 可用。</summary>
        public static bool EnsureNode(string rootDir)
            => EnsurePortableExe(rootDir, NodeDir, NodeExeName, NodeZipName);

        /// <summary>
        /// 幂等确保 MCP/node_modules 可用：
        /// - 目录已存在且非空 → 跳过（每次启动都检查，仅首次真正解压）；
        /// - 缺失/为空且 MCP/node_modules.zip 存在 → 解压整个目录树；
        /// - 两者皆无 / 解压失败 → 记日志并返回 false，不影响游戏。
        /// 兼容 PS Compress-Archive 生成的 zip：条目分隔符可能是 \ 或 /，目录条目以分隔符结尾。
        /// </summary>
        public static bool EnsureNodeModules(string rootDir)
        {
            try
            {
                string mcpDir = Path.Combine(rootDir, "MCP");
                string modulesDir = Path.Combine(mcpDir, "node_modules");
                string zipPath = Path.Combine(mcpDir, NodeModulesZipName);

                if (Directory.Exists(modulesDir) && Directory.EnumerateFileSystemEntries(modulesDir).Any())
                {
                    UEHttpLog.Message("[UnzipRuntime] node_modules 已就绪，跳过解压");
                    return true;
                }

                if (!File.Exists(zipPath))
                {
                    UEHttpLog.Warning("[UnzipRuntime] MCP/node_modules 缺失且 node_modules.zip 不存在，MCP 依赖包不可用");
                    return false;
                }

                Directory.CreateDirectory(modulesDir);
                int count = 0;
                using (var fs = File.OpenRead(zipPath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        // 统一为 / 分隔，便于识别目录条目与剥离顶层 node_modules/ 前缀
                        string rel = entry.FullName.Replace('\\', '/');
                        if (rel.EndsWith("/"))
                        {
                            string dirRel = StripNodeModulesPrefix(rel);
                            if (!string.IsNullOrEmpty(dirRel))
                            {
                                string d = Path.Combine(modulesDir, dirRel.Replace('/', Path.DirectorySeparatorChar));
                                if (IsInside(d, modulesDir))
                                    Directory.CreateDirectory(d);
                            }
                            continue;
                        }

                        rel = StripNodeModulesPrefix(rel);
                        if (string.IsNullOrEmpty(rel))
                            continue;
                        string dest = Path.Combine(modulesDir, rel.Replace('/', Path.DirectorySeparatorChar));
                        if (!IsInside(dest, modulesDir))
                            continue; // 防路径穿越（zip 内不应出现 ..）

                        string dir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        using (var input = entry.Open())
                        using (var output = File.Create(dest))
                            input.CopyTo(output);
                        count++;
                    }
                }
                UEHttpLog.Message($"[UnzipRuntime] 已从 node_modules.zip 解压 node_modules（{count} 个文件）");
                return true;
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UnzipRuntime] 解压 node_modules 失败（不阻断启动）：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>剥离 zip 内可选的顶层 node_modules/ 前缀（兼容打包整目录/包内容两种方式）。</summary>
        static string StripNodeModulesPrefix(string rel)
        {
            if (rel.StartsWith("node_modules/", StringComparison.Ordinal))
                return rel.Substring("node_modules/".Length);
            return rel;
        }

        /// <summary>防路径穿越：目标完整路径必须位于 root 之内。</summary>
        static bool IsInside(string path, string root)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 幂等确保 runtime/&lt;dir&gt;/&lt;exe&gt; 可用：
        /// - exe 已存在且非空 → 跳过（每次启动都检查，仅首次真正解压）；
        /// - exe 缺失/空且 zip 存在 → 解压出 exe（临时文件 + 覆盖移动，失败清理临时文件）；
        /// - 两者皆无 / 解压失败 → 记日志并返回 false，不影响游戏。
        /// </summary>
        static bool EnsurePortableExe(string rootDir, string dirName, string exeName, string zipName)
        {
            try
            {
                string exeDir = Path.Combine(rootDir, "runtime", dirName);
                string exePath = Path.Combine(exeDir, exeName);
                string zipPath = Path.Combine(exeDir, zipName);

                if (File.Exists(exePath) && new FileInfo(exePath).Length > 0)
                {
                    UEHttpLog.Message($"[UnzipRuntime] {exeName} 已就绪，跳过解压");
                    return true;
                }

                if (!File.Exists(zipPath))
                {
                    UEHttpLog.Warning($"[UnzipRuntime] runtime/{dirName}/{exeName} 缺失且 {zipName} 不存在，{dirName} 运行时不可用（可运行 bundle-env.ps1 生成）");
                    return false;
                }

                Directory.CreateDirectory(exeDir);
                string tmpPath = exePath + ".tmp";
                try
                {
                    using (var fs = File.OpenRead(zipPath))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                    {
                        var entry = zip.GetEntry(exeName);
                        if (entry == null)
                        {
                            UEHttpLog.Warning($"[UnzipRuntime] {zipName} 中未找到 {exeName}，跳过解压");
                            return false;
                        }
                        using (var input = entry.Open())
                        using (var output = File.Create(tmpPath))
                            input.CopyTo(output);
                    }

                    // 覆盖移动：先删旧（空/损坏文件）再移动，避免留下半文件
                    if (File.Exists(exePath))
                        File.Delete(exePath);
                    File.Move(tmpPath, exePath);

                    double mb = new FileInfo(exePath).Length / 1048576.0;
                    UEHttpLog.Message($"[UnzipRuntime] 已从 {zipName} 解压 {exeName}（{mb:F1} MB）");
                    return true;
                }
                finally
                {
                    // 解压失败清理临时文件
                    if (File.Exists(tmpPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UnzipRuntime] 解压 {exeName} 失败（不阻断启动）：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}

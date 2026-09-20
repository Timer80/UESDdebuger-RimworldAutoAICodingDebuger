using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 自研坐标光标 shader 的载入器。
    ///
    /// 背景（为什么不能用游戏内置 shader）：
    ///   炼狱魔王炮的地面指示 mote 用的是内置 shader "Custom/Mote hellsphere target"
    ///   （ShaderTypeDef MoteHellfireCannon_Target → shaderPath Map/MoteHellfireCannon_Target）。
    ///   该 shader 虽然声明了 _Color 属性，但像素着色器从头到尾没有读取它 —— 输出恒为
    ///   ScanTex(极坐标) * ScanMask.r + MainTex，颜色被烘焙死在贴图里。
    ///   旁证：RimWorld 自己的 Graphic_MoteWithAgeSecs 确实会 SetColor(_Color)，对这个
    ///   mote 是空操作。所以内置光标**无法换色**。
    ///
    /// 因此本模组自带一个程序化生成的 shader（ShaderProject/Assets/Shader/UECoordCursor.shader），
    /// 由 Unity 2020.3.35f1 打成 AssetBundle，放在模组根 AssetBundles/uecoordcursor（无扩展名）。
    ///
    /// 载入策略（两条路径，按序尝试）：
    ///   1) **首选**：模组根 AssetBundles/ 是 RimWorld 的约定位置，启动时 ModAssetBundlesHandler
    ///      已经把里面的文件全部 AssetBundle.LoadFromFile 进 Content.assetBundles.loadedAssetBundles。
    ///      直接复用那个实例即可。
    ///      ⚠ 不能对同一路径再调一次 AssetBundle.LoadFromFile：Unity 会拒绝并返回 null，报
    ///        "The AssetBundle '<path>' can't be loaded because another AssetBundle with the same
    ///         files is already loaded."（2026-09-20 实机踩到）。
    ///   2) 兜底：bundle 被放到别处时（例如 Shader/，与社区模组 helloshadercshape 同款布局），
    ///      自己 LoadFromFile 读。
    ///
    /// 打包方式见 ShaderProject/Assets/Shader/Editor/BuildUECoordCursorBundle.cs。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class UECoordCursorShader
    {
        /// <summary>bundle 内 Shader 资源名 = .shader 文件名（不含扩展名）。</summary>
        public const string ShaderAssetName = "UECoordCursor";

        /// <summary>bundle 文件名（RimWorld 要求无扩展名）。</summary>
        public const string BundleFileName = "uecoordcursor";

        /// <summary>兜底路径：按优先级尝试的相对模组根路径（AssetBundles/ 由 RimWorld 代管，见上）。</summary>
        static readonly string[] BundleRelativePaths =
        {
            "Shader/" + BundleFileName,
            "AssetBundles/" + BundleFileName,
        };

        static Shader shader;
        static bool everLoaded;
        static string loadError;

        /// <summary>最近一次载入失败的原因（成功时为 null）。</summary>
        public static string LoadError { get { return loadError; } }

        /// <summary>取 shader；失败返回 null（不抛异常）。首次调用会做真实的 bundle 载入。</summary>
        public static Shader Get()
        {
            if (shader != null)          // UnityEngine.Object 的 == 重载：被销毁的对象也算 null
                return shader;

            // 曾经成功过但现在为 null = 对象被销毁（例如模组热重载卸载了 bundle）→ 允许重载。
            // 从未成功过 = 环境缺 bundle → 不反复重试，避免每次调用都打磁盘。
            if (!everLoaded && loadError != null)
                return null;

            shader = TryLoad(out loadError);
            if (shader != null)
                everLoaded = true;
            return shader;
        }

        /// <summary>诊断用：列出所有会尝试的绝对路径。</summary>
        public static string ExpectedPaths()
        {
            string root = ResolveModRoot();
            if (root == null)
                return "(无法定位模组根目录)";
            var parts = new string[BundleRelativePaths.Length];
            for (int i = 0; i < BundleRelativePaths.Length; i++)
                parts[i] = Path.Combine(root, BundleRelativePaths[i].Replace('/', Path.DirectorySeparatorChar));
            return string.Join(" | ", parts);
        }

        // ------------------------------------------------------------------ 内部

        /// <summary>必须在主线程调用（AssetBundle/Shader 都是 UnityEngine 对象）。</summary>
        static Shader TryLoad(out string error)
        {
            // 路径 1：复用 RimWorld 已加载的模组 AssetBundle
            Shader fromGame = TryLoadFromGameBundles(out string gameError);
            if (fromGame != null)
            {
                error = null;
                UEHttpLog.Info("[UECoordCursor] shader 载入成功（复用 RimWorld 已加载的 AssetBundle）: " + fromGame.name);
                return fromGame;
            }

            // 路径 2：自己 LoadFromFile（bundle 不在 AssetBundles/ 时）
            Shader fromFile = TryLoadFromFile(out string fileError);
            if (fromFile != null)
            {
                error = null;
                UEHttpLog.Info("[UECoordCursor] shader 载入成功（自行 LoadFromFile）: " + fromFile.name);
                return fromFile;
            }

            error = "① 复用游戏已加载 bundle 失败: " + gameError + "；② 自行 LoadFromFile 失败: " + fileError
                + "。期望路径: " + ExpectedPaths()
                + " —— 请在 ShaderProject 里跑一次 UESDdebuger → Build Coord Cursor AssetBundle 打包脚本";
            UEHttpLog.Warning("[UECoordCursor] shader 载入失败: " + error);
            return null;
        }

        /// <summary>首选路径：从 ModContentPack.assetBundles.loadedAssetBundles 里取已加载的 bundle。</summary>
        static Shader TryLoadFromGameBundles(out string error)
        {
            error = null;
            try
            {
                UELoaderMod mod = UELoaderMod.Instance;
                if (mod == null || mod.Content == null)
                {
                    error = "UELoaderMod.Instance / Content 不可用";
                    return null;
                }

                ModAssetBundlesHandler handler = mod.Content.assetBundles;
                if (handler == null)
                {
                    error = "Content.assetBundles 为 null";
                    return null;
                }

                List<AssetBundle> bundles = handler.loadedAssetBundles;
                if (bundles == null || bundles.Count == 0)
                {
                    error = "模组根 AssetBundles/ 下没有任何已加载的 bundle"
                        + "（检查文件是否放在 AssetBundles/ 且**无扩展名**）";
                    return null;
                }

                var probed = new List<string>();
                for (int i = 0; i < bundles.Count; i++)
                {
                    AssetBundle bundle = bundles[i];
                    if (bundle == null)
                        continue;

                    Shader found = bundle.LoadAsset<Shader>(ShaderAssetName);
                    if (found != null)
                    {
                        if (!found.isSupported)
                        {
                            error = "Shader '" + found.name + "' 不被当前 GPU 支持（isSupported=false）";
                            UEHttpLog.Warning("[UECoordCursor] " + error);
                            return null;
                        }
                        return found;
                    }

                    try { probed.Add(bundle.name + "[" + string.Join(",", bundle.GetAllAssetNames()) + "]"); }
                    catch { probed.Add(bundle.name); }
                }

                error = "已加载的 " + bundles.Count + " 个 bundle 中都没有 Shader '" + ShaderAssetName
                    + "'。实际内容: " + string.Join(" ", probed);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>兜底路径：bundle 不在 RimWorld 代管的 AssetBundles/ 目录时，自己读文件。</summary>
        static Shader TryLoadFromFile(out string error)
        {
            error = null;
            string root = ResolveModRoot();
            if (root == null)
            {
                error = "无法定位模组根目录（UELoaderMod.Instance 与程序集位置都拿不到）";
                return null;
            }

            var tried = new List<string>();
            for (int i = 0; i < BundleRelativePaths.Length; i++)
            {
                string path = Path.Combine(root, BundleRelativePaths[i].Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    continue;

                tried.Add(path);
                try
                {
                    AssetBundle bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null)
                    {
                        // 常见原因：该 bundle 已被 RimWorld 加载过（Unity 不允许同文件重复加载）
                        error = "AssetBundle.LoadFromFile 返回 null（文件损坏 / 平台不匹配 / 已被 RimWorld 加载过）: " + path;
                        continue;
                    }

                    Shader loaded = bundle.LoadAsset<Shader>(ShaderAssetName);
                    if (loaded == null)
                    {
                        error = "bundle 内找不到 Shader '" + ShaderAssetName + "'；bundle 实际包含: "
                                + string.Join(", ", bundle.GetAllAssetNames());
                        bundle.Unload(false);
                        continue;
                    }

                    // Unload(false)：释放 bundle 的序列化容器，但保留已 LoadAsset 出来的对象。
                    bundle.Unload(false);

                    if (!loaded.isSupported)
                    {
                        error = "Shader '" + loaded.name + "' 不被当前 GPU 支持（isSupported=false）";
                        return null;
                    }
                    return loaded;
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message + "  (path=" + path + ")";
                }
            }

            if (tried.Count == 0)
                error = "候选路径下都不存在该文件（" + ExpectedPaths() + "）";
            return null;
        }

        static string ResolveModRoot()
        {
            // 首选：模组实例的 RootDir（最权威）
            try
            {
                UELoaderMod mod = UELoaderMod.Instance;
                if (mod != null && mod.Content != null && !string.IsNullOrEmpty(mod.Content.RootDir))
                    return mod.Content.RootDir;
            }
            catch { }

            // 兜底：<mod>/Assemblies/UELoader.dll → 上两级 = <mod>
            try
            {
                string asm = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asm))
                {
                    string dir1 = Path.GetDirectoryName(asm);
                    if (!string.IsNullOrEmpty(dir1))
                    {
                        string dir2 = Path.GetDirectoryName(dir1);
                        if (!string.IsNullOrEmpty(dir2))
                            return dir2;
                    }
                }
            }
            catch { }

            return null;
        }
    }
}

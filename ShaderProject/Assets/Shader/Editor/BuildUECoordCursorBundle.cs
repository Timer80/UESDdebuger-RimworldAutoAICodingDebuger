// =============================================================================
// BuildUECoordCursorBundle.cs  ——  UESDdebuger 坐标光标 shader 打包脚本
// -----------------------------------------------------------------------------
// 作用：把 Assets/Shader/UECoordCursor.shader 打进 AssetBundle，并自动拷贝到
//       模组根目录的 AssetBundles/ 下（RimWorld 约定位置、无扩展名）。
//
// 两种用法：
//   1) 编辑器菜单：UESDdebuger → Build Coord Cursor AssetBundle
//   2) 命令行（无需打开编辑器界面）：
//        "C:\Unity 2020.3.35f1\Editor\Unity.exe" ^
//          -batchmode -quit -nographics ^
//          -projectPath "<本工程目录>" ^
//          -executeMethod BuildUECoordCursorBundle.BuildFromCommandLine ^
//          -logFile "<日志路径>"
//      退出码 0 = 成功。
//
// 产物：
//   <工程>/Assets/AssetBundles/uecoordcursor        （bundle 本体）
//   <模组根>/AssetBundles/uecoordcursor             （游戏实际读取的文件）
//   注意：.manifest 与 Unity 自动生成的空 "AssetBundles" bundle 不要拷进模组目录
//        （RimWorld 的 ModAssetBundlesHandler 只接受**无扩展名**的文件）。
// =============================================================================

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildUECoordCursorBundle
{
    const string BundleName = "uecoordcursor";
    const string OutputDir = "Assets/AssetBundles";

    [MenuItem("UESDdebuger/Build Coord Cursor AssetBundle")]
    public static void BuildMenu()
    {
        Build();
    }

    /// <summary>批处理入口：构建 → 校验 → 退出码 0/1。</summary>
    public static void BuildFromCommandLine()
    {
        try
        {
            Build();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[UECoordCursor] 打包失败: " + e);
            EditorApplication.Exit(1);
        }
    }

    public static void Build()
    {
        if (!Directory.Exists(OutputDir))
            Directory.CreateDirectory(OutputDir);

        Debug.Log("[UECoordCursor] 开始打包 AssetBundle ...");
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            OutputDir, BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

        if (manifest == null)
            throw new Exception("BuildAssetBundles 返回 null（着色器编译失败？看上面的控制台错误）");

        string[] bundles = manifest.GetAllAssetBundles();
        Debug.Log("[UECoordCursor] 本次产出的 bundle: " + string.Join(", ", bundles));

        string builtBundle = Path.Combine(OutputDir, BundleName);
        if (!File.Exists(builtBundle))
            throw new Exception("没有生成 " + builtBundle + "（检查 UECoordCursor.shader.meta 里的 assetBundleName 是否为 " + BundleName + "）");

        // 自检：把打好的 bundle 读回来，确认 shader 能被 LoadAsset 到
        AssetBundle probe = AssetBundle.LoadFromFile(builtBundle);
        if (probe == null)
            throw new Exception("打包产物无法被 AssetBundle.LoadFromFile 读取: " + builtBundle);

        string[] assetNames = probe.GetAllAssetNames();
        Debug.Log("[UECoordCursor] bundle 内资源: " + string.Join(", ", assetNames));

        Shader probeShader = probe.LoadAsset<Shader>("UECoordCursor");
        if (probeShader == null)
            throw new Exception("bundle 内找不到名为 UECoordCursor 的 Shader（资源名必须与 .shader 文件名一致）");
        Debug.Log("[UECoordCursor] Shader 自检通过: " + probeShader.name
                  + "  isSupported=" + probeShader.isSupported);
        probe.Unload(false);

        // 拷贝到模组根/AssetBundles/<BundleName>（无扩展名）
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string modRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
        string destDir = Path.Combine(modRoot, "AssetBundles");
        Directory.CreateDirectory(destDir);

        string dest = Path.Combine(destDir, BundleName);
        File.Copy(builtBundle, dest, true);

        Debug.Log("[UECoordCursor] ✅ 打包完成，已部署到: " + dest
                  + "  (" + new FileInfo(dest).Length + " bytes)");
    }
}

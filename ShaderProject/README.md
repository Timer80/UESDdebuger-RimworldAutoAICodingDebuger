# ShaderProject —— 坐标光标自研 shader（Unity 2020.3.35f1）

> **要新建/改其它 shader？先读 [`../docs/shader-guide-for-ai.md`](../docs/shader-guide-for-ai.md)**
> —— 那是通用教程（内置 shader 认不认 `_Color` 的判定方法、打包、载入、验证闭环、故障对照表），
> 本文只讲这一个工程。

这个 Unity 工程只做一件事：把 `Assets/Shader/UECoordCursor.shader` 打成 AssetBundle，
供模组在运行时读出来，用于「地图坐标光标」（`post_map_marker` 工具）。

## 为什么不能用游戏内置的炼狱魔王炮 shader

炼狱魔王（Diabolus）的地面指示 mote 用的是内置 shader `Custom/Mote hellsphere target`
（`ShaderTypeDef MoteHellfireCannon_Target` → `shaderPath Map/MoteHellfireCannon_Target`，
三张贴图 `Things/Mote/MoteHellfireCannon_Target{,_Scan,_Scan_Mask}`）。

它 **Properties 里声明了 `_Color`，但像素着色器从不读取它**。HLSL 反汇编与 GLSL 明文两侧互证，
ps 的输出恒为：

```
SV_Target0 = clamp( ScanTex(极坐标旋转 uv) * ScanMask.r + MainTex , 0 , 1 )
```

整段 ps 里唯一的 cb0 读取是 `_AgeSecs / _ScanSpeed / _ScanSpeedAccel` 三个 float。
旁证：RimWorld 自己的 `Graphic_MoteWithAgeSecs.DrawWorker` 确实会
`propertyBlock.SetColor(ShaderPropertyIDs.Color, color)` —— 对这个 mote 是空操作，
即 `graphicData.color` 对炼狱魔王炮指示器无效。

**结论：内置光标换不了色，所以自研一个。**

## 自研 shader 的设计

`UECoordCursor.shader` **静态、朴素、只为定位**。全部由 UV 程序化生成，**不需要任何贴图**，
**不含任何动画**（shader 里不读 `_Time`）。元件只有四样：

| 元件 | 说明 |
|---|---|
| 外环 | 半径 0.955 |
| 四向刻线 | 外环向内指的短刻度，炼狱魔王炮那圈刻度的观感 |
| 贯穿十字 | 细线，正中心断开，避免糊住目标格 |
| 中心点 | 精确标示目标格中心 |

> 第一版做过雷达扫描扇面 + 声纳脉冲 + 呼吸发光，实机看太花；
> 这是测试模组，光标只需要指明「是哪一格」，所以全部砍掉。

关键实现点：

- `Blend SrcAlpha One`：与游戏内置发光 mote 一致的加色观感
- `ZTest Always`：**目标格被山体/屋顶挡住时也能看见**（报点场景的刚需）；
  想要原版那种被墙遮挡的效果，改成 `ZTest Greater` 重新打包即可
- `fwidth` 抗锯齿：任意缩放倍率下线条边缘都是 ~1px，不闪烁
- 颜色 100% 由 `_Color` 决定 —— 这就是「可指示颜色」
- 唯一的动态是 `_Alpha` 淡入淡出（C# 每帧 `SetFloat`），属功能性而非装饰

## 打包

### 方式 A：编辑器菜单

打开本工程（Unity 2020.3.35f1）→ 菜单 **UESDdebuger → Build Coord Cursor AssetBundle**。

### 方式 B：命令行（无需打开界面）

```powershell
& "C:\Unity 2020.3.35f1\Editor\Unity.exe" `
  -batchmode -quit -nographics `
  -projectPath "C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\ShaderProject" `
  -executeMethod BuildUECoordCursorBundle.BuildFromCommandLine `
  -logFile "C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\ShaderProject\build.log"
```

退出码 0 = 成功。日志里应看到：

```
[UECoordCursor] bundle 内资源: assets/shader/uecoordcursor.shader
[UECoordCursor] Shader 自检通过: UESDdebuger/CoordCursor  isSupported=True
[UECoordCursor] ✅ 打包完成，已部署到: <模组根>\AssetBundles\uecoordcursor
```

> 首次在新机器上跑会先导入 Packages，要几分钟；之后增量只要十几秒。

## 产物位置（重要）

```
<模组根>/AssetBundles/uecoordcursor        ← 游戏实际读取的文件（**无扩展名**，RimWorld 约定）
```

脚本会自动从 `ShaderProject/Assets/AssetBundles/uecoordcursor` 拷过去。
`.manifest` 与 Unity 自动生成的空 `AssetBundles` bundle **不要**拷进模组目录 —— RimWorld 的
`ModAssetBundlesHandler.IsAcceptableExtension` 只接受**无扩展名**的文件。

## 载入路径（`UECoordCursorShader.cs`）

1. **首选**：bundle 在 `AssetBundles/` 时，RimWorld 启动阶段已经把它加载进
   `ModContentPack.assetBundles.loadedAssetBundles`，直接复用那个实例。
   ⚠ **不能对同一路径再调 `AssetBundle.LoadFromFile`** —— Unity 会拒绝并返回 `null`：
   `The AssetBundle '<path>' can't be loaded because another AssetBundle with the same files is already loaded.`
2. **兜底**：bundle 被放到别处（如 `Shader/`，社区模组 helloshadercshape 的布局）时，自己 `LoadFromFile`。

## 调参

shader 参数由 `Source/UELoader/UEMapMarker.cs` 的 `CreateMaterial()` 设置
（都带 `HasProperty` 保护，改 shader 属性名不会炸）：

| 属性 | 默认 | 作用 |
|---|---|---|
| `_Color` | 工具传入 | 线条颜色 —— **唯一的换色入口** |
| `_Alpha` | 1 | 整体透明度（淡入淡出用） |
| `_Intensity` | 1.1 | 发光强度 |
| `_RingWidth` | 0.022 | 线宽（以半径 1 为基准的归一化值） |

线宽与四样元件的半径都写死在 shader 的 `frag` 里，改完重新打包即可。

## 排错

| 现象 | 处理 |
|---|---|
| 游戏日志 `[UECoordCursor] shader 载入失败` | 看后面的两段原因（复用游戏 bundle / 自行 LoadFromFile）；确认 `AssetBundles/uecoordcursor` 存在且**没有扩展名** |
| `bundle 内找不到 Shader 'UECoordCursor'` | 资源名必须与 `.shader` 文件名一致；检查 `.shader.meta` 里的 `assetBundleName: uecoordcursor` |
| `isSupported=false` | 显卡不支持；一般是 shader 编译目标太高，去掉 `#pragma target 3.0` 试试 |
| 打包报 `没有生成 .../uecoordcursor` | `.shader.meta` 里的 `assetBundleName` 被改了 |
| 光标看不见 | 先 `post_map_marker {action:"list"}` 确认 `shaderReady=true`；再确认光标在镜头视野内 |
| 换色无效 | 确认 `_Color` 真的被读 —— 参考上面「内置 shader 无法换色」那节，这个坑踩过一次 |

## 改动生效条件

**必须重启游戏**：`AssetBundles/uecoordcursor` 是 RimWorld 启动时扫描加载的，
热重载只管其它模组的 DLL，不管 AssetBundle。

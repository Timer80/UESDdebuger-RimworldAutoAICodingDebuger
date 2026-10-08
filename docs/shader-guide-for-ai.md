# RimWorld 自定义 Shader 开发指南（写给 AI）

> 适用对象：在本模组（UESDdebuger）里干活、需要给 RimWorld 加自定义着色器的 AI 助手。
> 本文所有结论都在本机实机验证过，附「怎么复核」的方法，不要求你信我。
>
> 相关文档：`ShaderProject/README.md`（本模组坐标光标 shader 工程）、
> `docs/2026-09-20-地图坐标光标.md`（这条链路的完整案例与踩坑记录）。

---

## 0. TL;DR 检查清单

按顺序做，任一步失败就先解决它，别往下走：

1. **先用内置 shader 试**。RimWorld 自带一百多个 shader（`mcp__rimworld-shader-sage__list_shaders`
   可列全），**绝大多数认 `_Color`**。只有确认内置的都不行，才自研。
2. 判定「内置 shader 认不认色」的唯一可靠方法：**读它的片元着色器**，看有没有 `* _Color`。
   **不要**看 Properties 声明，**不要**只看 regex 搜索结果（见 §1.2）。
3. 自研 → 写 `.shader` → 打 AssetBundle（Unity 批处理）→ 拷进 `<mod>/AssetBundles/<名字>`（**无扩展名**）→
   **重启游戏**（bundle 是启动时加载的，热重载管不着）。
4. C# 侧**只从 `ModContentPack.assetBundles.loadedAssetBundles` 取**，
   **绝对不要**对 `AssetBundles/` 下的路径再调 `AssetBundle.LoadFromFile`（见 §4.2）。
5. 验证闭环一步都不能省（见 §6）：打包日志 → Player.log → **截图肉眼确认** → 放大裁剪看对称性。

---

## 1. 先判断：到底要不要自研 shader

### 1.1 内置 shader 怎么找、怎么用

三层结构，别搞混：

```
ThingDef / GraphicData
  └─ <shaderType>MoteHellfireCannon_Target</shaderType>        ← ShaderTypeDef 的 defName
       └─ Defs/Core/Misc/ShaderTypeDefs/ShaderTypes.xml
            <shaderPath>Map/MoteHellfireCannon_Target</shaderPath>   ← 资源路径（≠ shader 真名）
                 └─ ShaderDatabase.LoadShader(ShaderTypeDef) 或 LoadShader("Map/xxx")
                      ├─ 先 Resources.Load("Materials/" + shaderPath, typeof(Shader))
                      └─ 再 ContentFinder<Shader>.TryFindAssetInModBundles(shaderPath)
```

- shader **文件里**写的 `Shader "Custom/Mote hellsphere target"` 是 Unity 的 shader 名，
  和 `shaderPath` **不是一回事**。`Shader.Find("Custom/...")` 用前者，`ShaderDatabase.LoadShader` 用后者。
- `ShaderDatabase.LoadShader` **失败不抛异常**，会回退到 `DefaultShader`（Cutout）并 `Log.Warning`。
  需要感知失败就比对 `shader.name`。

### 1.2 ★ 核心陷阱：Properties 声明了 `_Color` ≠ 片元着色器会读它

本机已直接核实的数据点：

| shader（真名） | shaderPath | 片元输出 | 认 `_Color`？ |
|---|---|---|---|
| `Custom/Mote glow` | `Map/MoteGlow` | `tex * _Color * 顶点色 * _Color2`（clamp） | ✅ 认 |
| `Custom/HighlightRing` | `Map/HighlightRing` | `tex * _Color * _Color2` | ✅ 认 |
| `Custom/Mote hellsphere target` | `Map/MoteHellfireCannon_Target` | `clamp( ScanTex(极坐标旋转uv) * ScanMask.r + MainTex , 0 , 1 )` | ❌ **不认** |

炼狱魔王炮那个就是反例：Properties 里明明有 `_Color ("Color", Color) = (1,1,1,1)`，
但整段 ps 唯一的 cb0 读取是 `_AgeSecs / _ScanSpeed / _ScanSpeedAccel`，
`_Color` 从头到尾没被读 → `SetColor("_Color", x)` 是空操作，`graphicData.color` 也是空操作。
（旁证：`Graphic_MoteWithAgeSecs.DrawWorker` 确实会 `propertyBlock.SetColor(ShaderPropertyIDs.Color, color)`，
对这个 mote 纯属白干。）

**判定方法（照做）**：

1. 读 GLSL 版本，别看 HLSL 反汇编（变量名会丢，只有 `cb0[3]` 这种）：
   ```
   mcp__rimworld-shader-sage__read_shader_file  path="Shaders_GLSL/<名字>.shader"
   ```
2. 在 `#ifdef FRAGMENT` 段里找 `main()`，看最后几行有没有 `* _Color`。
3. 文件里通常有多个平台变体（`platform=15` / `platform=15,18` 等），**至少看一个完整的**。

**⚠ 已知工具坑**：`mcp__rimworld-shader-sage__search_shader` 的索引**不完整** ——
对 `Custom_Mote_glow.shader` / `Custom_HighlightRing.shader` 这类文件，
它只索引到 ShaderLab 头（Properties 那几行），搜不到 GLSL 正文里的 `* _Color`。
拿搜索结果当「认不认色」的判据会**得出错误结论**。搜索只配用来定位文件，别用来下结论。

### 1.3 结论表

| 需求 | 建议 |
|---|---|
| 只是换个颜色 / 透明度 | 用内置的 `Map/MoteGlow`（`Custom/Mote glow`），认色还认顶点色 |
| 要一个地面光圈/圆环 | `Map/HighlightRing`（`Custom/HighlightRing`），认 `_Color` + `_Color2` |
| 要内置 shader 没有的形状/动画/裁剪 | 自研（本文 §2 起） |

---

## 2. 环境事实（本机固定值，直接用，别重新探测）

| 项 | 值 |
|---|---|
| 游戏 | RimWorld **1.6.4871 rev691**，Unity 运行时 **2022.3.35f1** |
| 游戏根 | `C:\SteamLibrary\steamapps\common\RimWorld` |
| 游戏程序集 | `…\RimWorldWin64_Data\Managed\Assembly-CSharp.dll` |
| Unity 编辑器 | `C:\Unity 2020.3.35f1\Editor\Unity.exe` |
| 本模组根 | `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger` |
| Player.log | `%USERPROFILE%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log` |
| 游戏内 HTTP 端口+token | `<模组根>\MCP\ports.json`（游戏重启会换 token） |
| UE C# 控制台 | `POST http://127.0.0.1:<port>/unityexplorer/console/execute`，body `{"code":"..."}`，要 Bearer token |

### 2.1 ★ Unity 版本兼容规则

**只能「旧版本打包 → 新版本运行」，反过来不行。**
- 实测：用 **2020.3.35f1** 打的 bundle 在 **2022.3.35f1** 的游戏里正常载入（本模组坐标光标就是这么跑的）。
- 所以不必非得装 2022.3 的编辑器；手上有哪个够老的 Unity 就用哪个。
- 但**别用比游戏更新的 Unity 打包** —— bundle 会静默载入失败。

### 2.2 现成的参考实现（本机就有）

| 路径 | 能学到什么 |
|---|---|
| `C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\ShaderProject\` | **推荐起点**：可用的 Unity 工程 + 打包脚本 + 自动部署，全链路已验证 |
| `F:\RimWorld3\Mods\TEXT02\` | Unity 工程模板（bundle 名 `modshaders`，含 `.mat`/`.png` 一起打的情况） |
| `F:\RimWorld3\Mods\helloshadercshape\` | 运行期从 AssetBundle 载入 shader 的 C# 写法 + 动态建 mote；bundle 放在 `<mod>/Shader/` |
| `C:\Users\Timer_0\Desktop\shader教程\` | 用户自己写了一半的教程：`Helloshader.shader`（带详细注释的最小 shader）、`HellsphereCannon.xml`（ShaderTypeDef + mote 的 XML 全貌）、`AssetBundleBrowser.cs` |

---

## 3. 工程布局与打包

### 3.1 目录结构

```
<mod>/ShaderProject/                      ← Unity 工程（可直接复制 TEXT02 的再改）
  Assets/Shader/<名字>.shader             ← shader 源文件
  Assets/Shader/<名字>.shader.meta        ← ★ 必须带 assetBundleName
  Assets/Shader/Editor/BuildXxxBundle.cs  ← 打包脚本（放 Editor/ 才不进运行时编译）
  ProjectSettings/ProjectVersion.txt      ← m_EditorVersion: 2020.3.35f1
<mod>/AssetBundles/<bundlename>           ← ★ 游戏读取的产物（无扩展名）
```

`.shader.meta` 模板（GUID 随便换一个 32 位十六进制）：

```yaml
fileFormatVersion: 2
guid: 7f3c1a94d2b64e0f8c5a1d6e9b2047ac
ShaderImporter:
  externalObjects: {}
  defaultTextures: []
  nonModifiableTextures: []
  preprocessorOverride: 0
  userData: 
  assetBundleName: uecoordcursor      # ← 决定产物文件名
  assetBundleVariant: 
```

### 3.2 ★ 产物位置的硬规则

RimWorld 的 `ModAssetBundlesHandler.ReloadAll` 扫 `<mod>/AssetBundles/`，
并且 `IsAcceptableExtension` **只接受没有扩展名的文件**。所以：

- ✅ `<mod>/AssetBundles/uecoordcursor`（无扩展名）
- ❌ `<mod>/AssetBundles/uecoordcursor.bundle`（有扩展名 → 直接跳过）
- ❌ `<mod>/AssetBundles/uecoordcursor.manifest`（同上）
- ❌ `<mod>/AssetBundles/AssetBundles` —— 这是 `BuildPipeline` 在输出目录里自动生成的
  **空 bundle**，拷进模组是噪音，别拷。

打包脚本里建议把「自检 + 部署」一起做了（照抄 `BuildUECoordCursorBundle.cs`）：
打完立刻 `AssetBundle.LoadFromFile` 读回来 + `LoadAsset<Shader>` + `isSupported`，
**任何一步失败就 `EditorApplication.Exit(1)`** —— 让批处理能靠退出码发现 shader 编译不了。

### 3.3 批处理命令（可直接复制改路径）

```powershell
& "C:\Unity 2020.3.35f1\Editor\Unity.exe" `
  -batchmode -quit -nographics `
  -projectPath "<模组根>\ShaderProject" `
  -executeMethod BuildUECoordCursorBundle.BuildFromCommandLine `
  -logFile "<模组根>\ShaderProject\build.log"
"exit=$LASTEXITCODE"     # 0 = 成功
# 然后务必 grep 日志：
Select-String -Path "<模组根>\ShaderProject\build.log" -Pattern 'Shader error|error CS|\[UECoordCursor\]'
```

- **首次**在新机器上跑会先导入 Packages（几分钟）；之后增量十几秒。
- shader 编译错误**不会**让 `BuildAssetBundles` 抛异常，但会写进这个 log —— 一定要 grep。

---

## 4. C# 侧载入

### 4.1 首选：复用 RimWorld 已经加载的 bundle

bundle 放在 `<mod>/AssetBundles/` 时，**启动阶段 RimWorld 已经把它加载好了**：

```csharp
ModAssetBundlesHandler handler = UELoaderMod.Instance.Content.assetBundles;
List<AssetBundle> bundles = handler.loadedAssetBundles;
for (int i = 0; i < bundles.Count; i++) {
    Shader s = bundles[i].LoadAsset<Shader>("UECoordCursor");   // ← 资源名 = .shader 的**文件名**
    if (s != null) return s;                                     // 成功后**不要** Unload（是 RimWorld 的）
}
```

**资源名 = `.shader` 文件的文件名（不含扩展名）**，不是文件里 `Shader "..."` 声明的名字。
即 `UECoordCursor.shader` → `LoadAsset<Shader>("UECoordCursor")`。

### 4.2 ★ 绝不能对同一路径重复 `LoadFromFile`

```csharp
// ❌ 这样写会在 bundle 位于 AssetBundles/ 时必然失败：
AssetBundle b = AssetBundle.LoadFromFile(path);   // 返回 null！
```

Unity 会拒绝并打印：

```
The AssetBundle '<绝对路径>' can't be loaded because another AssetBundle with the same files is already loaded.
```

排查时**不要**被这句误导成「文件损坏」或者「Unity 版本不匹配」—— 它只是说「已经加载过了」。
真·版本不匹配的表现是 `LoadFromFile` 也返回 null，但**日志里没有这句话**。

所以正确写法是：先走 §4.1；只有在 bundle 被放到 **RimWorld 不管的目录**（比如 `<mod>/Shader/`，
社区模组 helloshadercshape 就是这么放的）时，才自己 `LoadFromFile`。

### 4.3 其他必须项

- **主线程**：`AssetBundle` / `Shader` / `new Material` 全是 Unity 对象，只能在主线程碰。
  静态字段持有 `Shader` 会触发 RimWorld 启动警告：
  `Type X probably needs a StaticConstructorOnStartup attribute, because it has a field shader of type Shader.`
  → 给类加 `[StaticConstructorOnStartup]`。
- **`isSupported`**：载入后检查，不支持就报错退出，别硬画。
- **改 shader 要重启游戏**：AssetBundle 是启动时扫的；本模组的热重载只重打其它模组的 DLL，
  不管 bundle，也不管自己（UELoader.dll）。

### 4.4 ★ 日志门控坑

`UEHttpLog.Message(...)` **默认被 `DiagnosticEnabled` 吞掉**（防 HTTP 逐请求刷屏）。
你自己写的「载入成功」这类**关键生命周期日志必须用 `UEHttpLog.Info/Warning/Error`**，
否则游戏里一切正常、日志里一个字都没有，你会以为没执行。

（这个坑真踩过：第一版用 `Message` 打「shader 载入成功」，日志全空，白白怀疑了半天。）

---

## 5. 写 RimWorld shader 的硬约束

### 5.1 网格与坐标

- 地图平面用 `MeshPool.plane10`（1×1 格，躺在 XZ 平面），UV 是 0..1。
  片段里 `float2 p = i.uv * 2.0 - 1.0;` 得到以中心为原点、半径 1 的圆盘坐标。
- 摆位（与 `Graphic_Mote.DrawMote` 完全同款）：
  ```csharp
  Matrix4x4 m = default;
  m.SetTRS(cell.ToVector3ShiftedWithAltitude(AltitudeLayer.MoteOverhead),
           Quaternion.identity, new Vector3(sizeX, 1f, sizeZ));
  Graphics.DrawMesh(MeshPool.plane10, m, material, 0);
  ```
- `AltitudeLayer.X.AltitudeFor()` 决定 y。想要「盖在生物/建筑之上」就用 `MoteOverhead`。

### 5.2 Pass 状态惯例（照抄 vanilla）

```hlsl
Tags { "Queue"="Transparent+151" "IgnoreProjector"="True"
       "RenderType"="Transparent" "DisableBatching"="True" }
Blend  SrcAlpha One      // 加色发光观感；vanilla mote 常见这个
                         // （炼狱魔王炮 / Custom/Mote glow 都是 SrcAlpha One；
                         //   Custom/Mote 用的是 SrcAlpha OneMinusSrcColor）
ZWrite Off
Cull   Off
```

**ZTest 怎么选**（我核对过的几个 vanilla mote —— `Custom/Mote` / `Custom/Mote glow` /
炼狱魔王炮 —— 全都写 `ZTest Greater`）：

| 选择 | 效果 |
|---|---|
| `ZTest Greater` | 与 vanilla 一致的遮挡表现（被墙/屋顶挡住就看不见） |
| `ZTest Always` | **穿墙可见** —— 报点类指示器推荐，目标在山体后面也要看得见 |

### 5.3 抗锯齿与编译目标

用 `fwidth` 做屏幕空间抗锯齿（线宽在任意缩放倍率下稳定 ~1px）：

```hlsl
#pragma target 3.0            // fwidth 需要 SM3.0
float BandAA(float dist, float halfWidth) {
    float aa = fwidth(dist) * 0.7 + 1e-6;
    return 1.0 - smoothstep(halfWidth - aa, halfWidth + aa, dist);
}
```

### 5.4 ★ 区间型 helper 的对称性陷阱

```hlsl
// along ∈ [r0, r1] 才亮 → 这个函数**只画一个方向**！
float Tick(float2 p, float2 dir, float r0, float r1, float w) {
    float along = dot(p, dir);
    ...
    float seg = smoothstep(r0-aa, r0+aa, along) * (1 - smoothstep(r1-aa, r1+aa, along));
    return band * seg;
}
```

画十字必须**四个方向各写一次**：

```hlsl
mask += Tick(p, float2( 1, 0), 0.10, 0.95, w);
mask += Tick(p, float2(-1, 0), 0.10, 0.95, w);
mask += Tick(p, float2( 0, 1), 0.10, 0.95, w);
mask += Tick(p, float2( 0,-1), 0.10, 0.95, w);
```

少写两个方向 → 得到**只有右臂和上臂的半截十字**。这个 bug 在正常缩放的截图上几乎看不出来，
**必须裁剪放大**才能发现（见 §6.5）。

### 5.5 RimWorld 会替你设的 uniform

| 属性 | 谁设的 | 说明 |
|---|---|---|
| `_Color` | `Graphic_Mote.DrawMote` 用 `MaterialPropertyBlock` 设 | 只对**读它**的 shader 有效 |
| `_AgeSecs` | `Graphic_MoteWithAgeSecs` | 走 `MaterialPropertyBlock`，自己画时要 `mat.SetFloat` |
| `_Time` | Unity 内置 | 自己 `Graphics.DrawMesh` 时也能用；不想有动画就别读它 |

### 5.6 自研 shader 的写法建议

- **程序化生成形状，不要贴图**：省掉「贴图也要打进 bundle / 也要能被 LoadAsset」一整类麻烦，
  而且改形状只要改一行数学。
- **颜色走 `_Color` 一个入口**：用户/AI 只需要传一个颜色就够了。
- 参数全部用 `mat.HasProperty("_X")` 保护再 `SetFloat/SetColor`：
  以后改 shader 属性名不会把 C# 炸掉。

---

## 6. ★ 验证闭环（这一节是本文最重要的部分）

shader 这类东西**不能靠读代码判断对错**，必须真跑起来看。按顺序：

### 6.1 打包并确认 shader 编译通过

§3.3 的命令 + `grep 'Shader error'`。退出码 0 **不代表** shader 编译成功。

### 6.2 确认产物部署到位

```powershell
Get-Item "<模组根>\AssetBundles\<bundlename>" | Select-Object Length,LastWriteTime
```
大小应该在几 KB；如果是 0 或者没有 `.manifest` 生成，说明 `assetBundleName` 没配上。

### 6.3 重启游戏（不可跳过）

bundle 是启动时加载的。改完必须重启游戏，热重载不管 bundle。
快速循环：`stop_game(force) → start_game(waitForNotification:false) → POST /trigger-quicktest`。

### 6.4 读 Player.log 确认载入路径

```powershell
$log="$env:USERPROFILE\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log"
Select-String -Path $log -Pattern 'UECoordCursor|AssetBundle|shader' | Select-Object -Last 20
```
期望看到「载入成功」；看到「can't be loaded because another AssetBundle…」→ 回 §4.2。

### 6.5 截图肉眼确认（**必须做**）

本机没有内置截图接口，用 UE 的 C# 控制台调 Unity：

```csharp
// POST /unityexplorer/console/execute  {"code":"..."}
UnityEngine.ScreenCapture.CaptureScreenshot("C:/tmp/shot.png")
```
写盘在帧末，**等 3~4 秒**再读文件。

两个配套技巧：
- **先关掉游戏里的 Debug log 窗口**，否则它会盖住视野：
  `Verse.Find.WindowStack.TryRemove(typeof(LudeonTK.EditWindow_Log), true)`
  （注意类是 `LudeonTK.EditWindow_Log`，**不在** `Verse` 命名空间 —— 1.6 挪过）
- **把要看的局部裁剪放大**再判断细节（对称性、线宽、抗锯齿都是这样才看得出来的）：
  ```powershell
  Add-Type -AssemblyName System.Drawing
  $src=[System.Drawing.Image]::FromFile('C:\tmp\shot.png')
  $crop=New-Object System.Drawing.Bitmap(720,720)
  $g=[System.Drawing.Graphics]::FromImage($crop)
  $g.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
  $g.DrawImage($src,(New-Object System.Drawing.Rectangle(0,0,720,720)),
              (New-Object System.Drawing.Rectangle($x,$y,180,180)),[System.Drawing.GraphicsUnit]::Pixel)
  $g.Dispose(); $crop.Save('C:\tmp\crop.png',[System.Drawing.Imaging.ImageFormat]::Png)
  ```

### 6.6 交互类效果要真的点一下

「左键点击消除」这种不能只验「函数被调用」：用系统级鼠标注入（`SetCursorPos` + `mouse_event`）
打到光标屏幕坐标上，再查状态。

```csharp
// 取屏幕坐标（Unity 是左下原点）
Verse.Find.Camera.WorldToScreenPoint(new Verse.IntVec3(x,0,z).ToVector3ShiftedWithAltitude(...))
```
→ 换算：`deskX = clientOrigin.X + ux`，`deskY = clientOrigin.Y + (clientHeight - uy)`。

**坑**：游戏窗口最小化时 `GetClientRect` 返回 0x0、`ClientToScreen` 返回 `(-32000,-32000)`。
先 `ShowWindow(hwnd, SW_RESTORE=9)` + `SetForegroundWindow`，再取 rect。

---

## 7. 故障对照表（报错原文 → 原因 → 处置）

| 现象 / 日志原文 | 原因 | 处置 |
|---|---|---|
| `The AssetBundle '<path>' can't be loaded because another AssetBundle with the same files is already loaded.` | RimWorld 启动已加载过 `AssetBundles/` 下的它，你又 `LoadFromFile` 一次 | 改走 `Content.assetBundles.loadedAssetBundles`（§4.1） |
| `AssetBundle.LoadFromFile` 返回 null，日志里**没有**上面那句 | 文件损坏 / Unity 版本比游戏新 / 平台不对 | 用不高于游戏版本的 Unity 重新打；校验文件大小 |
| `Could not load asset bundle at <path>` | 同上，RimWorld 自己的加载也失败了 | 同上 |
| `bundle 内找不到 Shader 'X'；bundle 实际包含: ...` | 资源名 ≠ `.shader` 文件名 | 用返回的实际包含列表对齐名字 |
| `bundle 实际包含: assets/shader/xxx.shader` 为空列表 | `.shader.meta` 没配 `assetBundleName` | 补上再打 |
| `Type X probably needs a StaticConstructorOnStartup attribute` | 静态字段持有 Unity 资源 | 给类加 `[StaticConstructorOnStartup]` |
| 游戏里没变化，日志也没有你的自定义消息 | 用的 `UEHttpLog.Message`（默认被门控） | 改用 `UEHttpLog.Info/Warning/Error`（§4.4） |
| 颜色设了没反应 | shader 片元**没读** `_Color` | 读片元着色器确认（§1.2）；内置的话换 `Map/MoteGlow` |
| `Shader 'X' 不被当前 GPU 支持（isSupported=false）` | 编译目标太高 | 去掉 `#pragma target 3.0` 之类 |
| Unity 打包日志里有 `Shader error at ...` 但退出码是 0 | `BuildAssetBundles` 不因 shader 失败而抛异常 | **每次都 grep build.log** |
| 改了 shader 但游戏里还是老样子 | 没重启游戏 | 重启（§6.3） |
| 图形只有一半 / 不对称 | 区间型 helper 只写了一个方向 | §5.4 |

---

## 8. 可以直接抄的模板

本模组这套是**已验证可用**的最小完整实现：

| 文件 | 作用 |
|---|---|
| `ShaderProject/Assets/Shader/UECoordCursor.shader` | 程序化静态准星：外环 + 四向刻线 + 贯穿十字 + 中心点；无贴图、无动画、`_Color` 控色 |
| `ShaderProject/Assets/Shader/UECoordCursor.shader.meta` | `assetBundleName` 配置样板 |
| `ShaderProject/Assets/Shader/Editor/BuildUECoordCursorBundle.cs` | 打包 + 自检 + 自动部署 + 退出码 |
| `Source/UELoader/UECoordCursorShader.cs` | 载入器：先复用游戏已加载 bundle，再兜底 `LoadFromFile`，失败原因写得很细 |
| `Source/UELoader/UEMapMarker.cs` | 怎么用这个 shader 画东西（`Graphics.DrawMesh` + 淡入淡出 + 地面文字） |

复制时的最小改动清单：
1. 改 `.shader` 文件名 + `.shader.meta` 里的 `guid`（换一个随机 32 位十六进制）和 `assetBundleName`
2. 改打包脚本里的 `BundleName` 常量
3. 改 C# 载入器里的 `ShaderAssetName` / `BundleFileName`
4. 重新打包 + 重启游戏 + 按 §6 验一遍

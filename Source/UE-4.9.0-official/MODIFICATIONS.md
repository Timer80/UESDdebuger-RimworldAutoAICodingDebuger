# UnityExplorer 4.9.0 — 本地修改声明（MODIFICATIONS）

按 GNU GPL-3.0 §5(a) 要求，声明对 UnityExplorer 4.9.0 官方源码所做的全部修改。

## 基线版本

- 项目：UnityExplorer 4.9.0（作者 Sina-Dev，版权归原作者所有）
- 基线来源：官方发行版（`UE-4.9.0.zip`，随本目录存档）
- 原始许可：GPL-3.0（见 `UnityExplorer-4.9.0/LICENSE`）

## 修改文件清单（与官方 zip 逐文件哈希比对）

| 文件 | 状态 | 说明 |
|---|---|---|
| `UnityExplorer-4.9.0/src/UnityExplorer.csproj` | 修改 | 依赖与引用调整，见下 |

其余全部 `.cs` 源文件及其余文件与官方发布一致，未做改动。

## 修改内容（`src/UnityExplorer.csproj`）

1. **Harmony 依赖替换**：`HarmonyX 2.5.2` → `Lib.Harmony 2.3.3`（`IncludeAssets="compile"`），
   使编译产物对接 RimWorld 官方 Harmony 模组（`brrainz.harmony`）的运行时接口。
2. **UniverseLib.Mono 引用替换**：由 NuGet 包（针对 HarmonyX 2.5.2 编译）改为引用本地重编译版
   `..\..\..\UniverseLib-official\UniverseLib-1.5.1\Release\UniverseLib.Mono\UniverseLib.Mono.dll`
   （该本地版本以 Lib.Harmony 2.3.3 编译，见 `Source/UniverseLib-official/MODIFICATIONS.md`）。

源码内对应位置均以 `<!-- UESDdebuger recompile: ... -->` 注释标注。

## 新增文件（本地重编译产物，非源码改动）

`UnityExplorer-4.9.0/Release/UnityExplorer.Standalone.Mono/` 下新增：

- `UnityExplorer.STANDALONE.Mono.dll`（由上述修改后源码编译）
- `UniverseLib.Mono.dll`（本地重编译版）
- `mcs.dll`、`Tomlet.dll`（依赖项，随产物输出）

## 构建方式

- 配置：`STANDALONE_Mono`（`UnityExplorer.csproj` 中 Condition 匹配的 Mono 配置）
- 前置：以 `Lib.Harmony 2.3.3` 本地重编译的 `UniverseLib.Mono.dll`
- 输出：`UnityExplorer-4.9.0/Release/UnityExplorer.Standalone.Mono/`

## 许可

修改后的整体仍按 GPL-3.0 授权。再分发须保留本声明、原 `LICENSE` 与 `THIRDPARTY_LICENSES.md`，
并提供本修改版源码。

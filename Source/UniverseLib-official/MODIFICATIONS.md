# UniverseLib 1.5.1 — 本地修改声明（MODIFICATIONS）

按 LGPL-2.1 §5 要求，声明对 UniverseLib 1.5.1 官方源码所做的全部修改。

## 基线版本

- 项目：UniverseLib 1.5.1（版权归原作者所有）
- 基线来源：官方发布（`UniverseLib-1.5.1.zip`，随本目录存档）
- 原始许可：LGPL-2.1（见 `UniverseLib-1.5.1/LICENSE`）

## 修改文件清单（与官方 zip 逐文件哈希比对）

| 文件 | 状态 | 说明 |
|---|---|---|
| `UniverseLib-1.5.1/src/UniverseLib.csproj` | 修改 | 依赖调整，见下 |

其余全部 `.cs` 源文件及其余文件与官方发布一致，未做改动。

## 修改内容（`src/UniverseLib.csproj`）

- **Harmony 依赖替换**：`HarmonyX 2.5.2` → `Lib.Harmony 2.3.3`（`IncludeAssets="compile"`），
  使编译产物对接 RimWorld 官方 Harmony 模组（`brrainz.harmony`）的运行时接口。

源码内对应位置以 `<!-- UESDdebuger recompile: ... -->` 注释标注。

## 新增文件（本地重编译产物，非源码改动）

- `UniverseLib-1.5.1/Release/UniverseLib.Mono/UniverseLib.Mono.dll` 及 `.xml`
- `UniverseLib-1.5.1/Release/NuGet_Mono/lib/net35/UniverseLib.Mono.dll` 及 `.xml`

## 构建方式

- 配置：Mono 目标（`src/UniverseLib.csproj`）
- 依赖：`Lib.Harmony 2.3.3`
- 输出：`Release/UniverseLib.Mono/`、`Release/NuGet_Mono/`

## 许可

修改后的库仍按 LGPL-2.1 授权。再分发须保留本声明与原 `LICENSE`，并提供本修改版库源码；
下游程序以动态链接方式使用本库时，须允许用户以修改版库重新链接。

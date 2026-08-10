# UESDdebuger — 许可证与第三方声明（NOTICE）

本文件说明 UESDdebuger 项目内各组成部分的许可边界、第三方版权声明，以及禁止随包分发的内容。
法律义务的分层方式与 GPL/LGPL/MIT 的兼容性矩阵保持一致（宽松许可可并入 copyleft，反向则不行）。

## 1. 许可总览

| 目录 / 组件 | 许可 | 说明 |
|---|---|---|
| `LICENSE`（仓库根） | **GPL-3.0** | 覆盖游戏内模组部分及仓库整体默认许可 |
| `Source/UELoader/`、`About/` | **GPL-3.0** | 游戏内模组代码。因链接/加载 GPLv3 的 UnityExplorer 且随包分发其二进制，按 GPLv3 §5 必须整体按 GPL-3.0 授权 |
| `McpRimDebug/` | **MIT**（见 `McpRimDebug/LICENSE`） | 独立进程，经 SDB 协议与游戏通信，不链接 GPL 代码，不受传染 |
| `MCP/` | **MIT**（见 `MCP/LICENSE`） | 独立 Node.js 进程，经 HTTP 与游戏内服务通信，不受传染 |
| `Source/UE-4.9.0-official/` | **GPL-3.0** | UnityExplorer 4.9.0 官方源码（作者 Sina-Dev，版权保留，见其自带 `LICENSE`），本地重编译修改（仅 `src/UnityExplorer.csproj`，见 `Source/UE-4.9.0-official/MODIFICATIONS.md`） |
| `Source/UniverseLib-official/` | **LGPL-2.1** | UniverseLib 1.5.1 官方源码（版权保留，见其自带 `LICENSE`），本地重编译修改（仅 `src/UniverseLib.csproj`，见 `Source/UniverseLib-official/MODIFICATIONS.md`） |
| `Assemblies/UnityExplorer.STANDALONE.Mono.dll` | **GPL-3.0** | 由修改后源码本地重编译的二进制（改动见 `MODIFICATIONS.md`），随包分发须保持 GPL-3.0 声明并提供对应源码 |
| `Assemblies/UniverseLib.Mono.dll` | **LGPL-2.1** | 由修改后源码本地重编译的二进制（改动见 `MODIFICATIONS.md`），随包分发须保持 LGPL-2.1 声明（动态链接，提供再链接材料） |
| `Assemblies/Mono.Cecil*.dll` | **MIT** | Mono.Cecil（Jb Evain / Mono 项目） |
| `Assemblies/MonoMod.*.dll` | **MIT** | MonoMod（0x0ade） |
| `McpRimDebug/SdbClient/` | **MIT/X11** | vendored Mono.Debugger.Soft（mono 项目，协议 2.57，内容保持上游原样） |
| `McpRimDebug` 依赖：ModelContextProtocol SDK 2.1.0、Mono.Cecil 0.10.4 | **MIT** | NuGet 依赖 |
| `MCP/` 依赖：`@modelcontextprotocol/sdk`、`express`、`cors`、`hono` 等 | **MIT** | npm 依赖（各自保留版权声明，见 `MCP/node_modules/*/LICENSE`） |
| `UE_official/`、`.staging/ue/` | **GPL-3.0 / LGPL-2.1 / MIT** | 官方 UE 发行版暂存存档（含 zips、Runtime 目录），仅供本地构建参考，不作为模组分发内容 |
| `_decomp/` | **禁止分发** | 反编译的 RimWorld 游戏源码（Ludeon 专有），仅本地参考，严禁外发 |
| `未分类文件/`、`调试文件覆盖/` | **禁止分发** | 游戏本体可执行文件副本（`RimWorldWin64.exe`、`UnityPlayer.dll`、`boot.config`）与调试覆盖文件，严禁外发 |

## 2. GPL-3.0（覆盖游戏内模组部分）

仓库根 `LICENSE` 为 GNU GPL v3.0 标准全文。游戏内模组部分（`Source/UELoader/`、`About/`、`Assemblies/UELoader.dll`）
依 GPLv3 §5 以 GPL-3.0 授权。要求：

- 再分发时保留本声明与 GPLv3 全文；
- 提供可构建的完整对应源码；
- 不添加任何「进一步限制」。

## 3. LGPL-2.1（UniverseLib）

UniverseLib 1.5.1（见 `Source/UniverseLib-official/UniverseLib-1.5.1/LICENSE`）以 LGPL-2.1 授权。
`UniverseLib.Mono.dll` 通过动态链接方式使用，随包分发时保留其许可文本与版权声明。

## 4. MIT 组件版权声明

以下组件以 MIT（或 MIT/X11）授权，版权归各自作者所有：

- Mono.Cecil / MonoMod：见各二进制随附声明；
- Mono.Debugger.Soft（vendored）：Copyright (C) 2006 Novell, Inc 及后续贡献者；
- ModelContextProtocol SDK：Copyright Microsoft；
- 其余 npm 包：见 `MCP/node_modules/*/LICENSE`。

MIT 许可全文见 `McpRimDebug/LICENSE` 与 `MCP/LICENSE`。

## 5. 禁止分发清单（重要）

以下内容**不得**进入任何对外发布包（GitHub、Steam Workshop 等），否则构成对
Ludeon Studios 版权及 RimWorld EULA 的违反：

1. `_decomp/*.decompiled.cs` —— 反编译的游戏源码；
2. `未分类文件/` —— `RimWorldWin64.exe`、`UnityPlayer.dll`、`WinPixEventRuntime.dll`、`boot.config`；
3. `调试文件覆盖/` —— 游戏文件覆盖副本（`RimWorldWin64_Data/boot.config` 等）。

以上路径已加入 `.gitignore`，且 `_decomp/` 已从 git 索引移除（仅保留本地工作区文件）。

## 6. RimWorld 模组合规提示

- 本模组仅依赖公开的 RimWorld 模组 API（`Assembly-CSharp` 编译引用，不随包分发）；
- 运行依赖 `brrainz.harmony`（见 `About/About.xml` modDependencies）；
- 因 UnityExplorer 属调试/修改工具，上传 Steam Workshop 可能被平台标记为 unsafe mod，属平台政策问题，与本文件无关。

## 7. RimWorld EULA 免责声明（必须展示）

RimWorld EULA（THINGS YOU CREATE USING OUR SOFTWARE 一节）要求每个衍生作品展示以下免责声明。
本声明已写入 `About/About.xml` 的 description（随模组发布），此处同时存档：

> "Portions of the materials used to create this content/mod are trademarks and/or copyrighted works of Ludeon Studios Inc. All rights reserved by Ludeon. This content/mod is not official and is not endorsed by Ludeon."

对外发布渠道（Steam Workshop 页面、GitHub README 等）应保持该声明可见，且不得以任何方式暗示官方制作或 Ludeon 背书。

## 8. 修改声明（GPL-3.0 §5 / LGPL-2.1 §5）

UnityExplorer 4.9.0 与 UniverseLib 1.5.1 随包源码均**经过本地重编译修改**：为对接 RimWorld 官方 Harmony 模组加载（`brrainz.harmony`），将 HarmonyX 2.5.2 替换为 Lib.Harmony 2.3.3。修改范围（经与官方发行版 zip 逐文件哈希比对）仅涉及各自的 `src/*.csproj`，所有 `.cs` 源文件与官方一致；同时新增本地重编译产物。具体修改内容见：

- `Source/UE-4.9.0-official/MODIFICATIONS.md`（UnityExplorer，GPL-3.0）
- `Source/UniverseLib-official/MODIFICATIONS.md`（UniverseLib，LGPL-2.1）

源码内对应位置以 `<!-- UESDdebuger recompile: ... -->` 注释标注。

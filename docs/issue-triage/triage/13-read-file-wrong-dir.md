裁决：不值得修

**一句话结论**：现象 100% 可复现、`File not found` 确实不带任何提示，但责任方是**外部 MCP 服务器 `rimworld-source-online`（不在本仓库内，本项目改不了它的 `read_file`）**；在本项目内唯一能做的只是「引用 vanilla 行号一律带目录」的书写约定 —— 那是文档习惯不是修工具，且吐槽者自己也已认定「⚠️ 坑的真正来源不是 MCP，是上游文档」（`docs/mcp-吐槽板.md:134`）。按 §5 尺子，本项目侧不构成「值得修」。

---

## 3. 核实过程

### 3.1 现象复现（只读调用，未改任何状态）

```
mcp__rimworld-source-online__read_file{ path:"Source/RimWorld/PawnRenderer.cs", start_line:0, line_count:5 }
→ Error: File not found: Source/RimWorld/PawnRenderer.cs
```
**逐字确认：错误信息只有路径本身，没有 basename 模糊匹配建议、没有候选列表、没有 hint。**

```
mcp__rimworld-source-online__read_file{ path:"Source/Verse/PawnRenderer.cs", start_line:350, line_count:6 }
→ 	{
→ 		Vector3 zero = Vector3.zero;
→ 		PawnRenderFlags pawnRenderFlags = DefaultRenderFlagsNow | PawnRenderFlags.Cache | PawnRenderFlags.DrawNow;
```
`start_line` 是 0-indexed（另见条目 [14]），故上面第 3 行即 **1-indexed 第 353 行**，
与吐槽板 `:131` 引用的 `PawnRenderer.cs:353` 逐字一致 —— **条目里的行号是真的**。

### 3.2 「渲染栈被劈成两个目录」也复现了

```
read_file{path:"Source/Verse/PawnRenderNode.cs", line_count:2}
→ using System; / using System.Collections.Generic;   [TRUNCATED] Showing 2/685 lines.

read_file{path:"Source/RimWorld/PortraitsCache.cs", line_count:2}
→ using System; / using System.Collections.Generic;   [TRUNCATED] Showing 2/290 lines.
```
⇒ `PawnRenderer` / `PawnRenderNode` 在 `Source/Verse/`，`PortraitsCache` 在 `Source/RimWorld/`，
条目 `:120-123` 的描述属实。

### 3.3 决定性归属证据：这条链路不是本项目的代码

- 全仓 grep `rimworld-source-online` → **只有 2 处命中，都在 `docs/mcp-吐槽板.md`（:128、:129）**，没有任何源码/配置命中。
- 全仓 grep `search_defs` / `get_def_details` / `search_source` / `list_directory` → 在 `MCP/`、`Source/`、`McpRimDebug/` 下**零命中**（只出现在吐槽板与 `skills/using-superpowers/references/gemini-tools.md`）。
- `MCP/toolConfig.json` 的 192 个工具键、`MCP/aggregator/config.json:6-8` 的 upstream（只有 `rimworld` → `http://127.0.0.1:3000/sse`）里都没有这条链路。
- 旁证：`docs/shader-guide-for-ai.md:15 / :68 / :73` 用的 `mcp__rimworld-shader-sage__*` 同样是外部服务器（同样在本仓库零命中），且那份文档 `:73` 已经写明「⚠ 已知工具坑：`search_shader` 的索引**不完整**」—— 说明维护者对这类**外部 MCP 工具缺陷**的既有处理方式就是**在文档里写已知坑**，而不是改工具。这与本条目的归属判断一致。

> 标注为**未核实**的部分：`rimworld-source-online` 的具体实现与维护方（我没能定位到它的源码或进程命令行 —— `Get-CimInstance Win32_Process` 在当前沙箱下返回「拒绝访问」，`~/.dsh` 下也没找到 MCP 服务器配置文件）。所以我能确证的是「**不在本仓库内**」，不能确证「由谁维护」。

### 3.4 吐槽者自己的归因

`docs/mcp-吐槽板.md:134-135`：
> ⚠️ 坑的真正来源不是 MCP，是**上游文档**：分计划通篇写裸文件名（`PawnRenderer.cs:353`）…

`:144-152` 的三条建议里，第 1、2 条（`read_file` 猜目录提示、`locate_type`）都是**改外部 MCP**；第 3 条（文档侧引用带目录）才是本项目内的。

---

## 4. 影响与严重度复评

原标注 **🟡 低** → 维持 **🟡 低**，且**进一步下调紧迫性**。

- **不会造成相反结论**：报错是诚实的「这个路径不存在」，没有任何错误数据被返回。调用方据此改用 `search_source` 就能继续。
- **代价是一次静默往返**：`read_file` 猜错目录 → `File not found` → `search_source` 定位 → 再 `read_file`。浪费一次调用，不浪费正确性。
- 唯一被点名担心的连锁风险（`COMMON.md §10` 那种「一处 API 认知错误会连锁」）是**人为把错目录带进注释/规格档**的风险，靠调用纪律与文档约定即可控，不需要动工具。
- 附带小坑（`:141-142` 说的 `[TRUNCATED]` 脚注被当成「没读到/失败」）我也在实测输出里见到了（`list_directory` 与 `read_file` 都带 `[TRUNCATED] … (Tip: …)` 脚注）—— 同样是外部服务器行为，同样只能靠文档说明。

**不构成 🔴 / 🟠：无假成功、无假阴性、无系统性浪费。**

---

## 5. 责任归属

**第三方 / 外部工具**（非本项目）。

理由：
1. `read_file` 的实现不在本仓库（全仓零命中，见 3.3），本项目**没有任何可改的代码路径**能改变它的错误信息；
2. 吐槽者本人已给出同向归因（`:134`「坑的真正来源不是 MCP，是上游文档」）；
3. 本仓库对同类外部工具缺陷的既有处置先例是**文档化已知坑**（`docs/shader-guide-for-ai.md:73`）。

**本项目侧唯一可担的那一小块责任是纯文档**：分计划引用 vanilla 行号时未带目录。这是本项目内部产物（`docs/superpowers/plans/*.md`）的书写规范问题，成本极低 —— 但它是**规范**，不是本条目的「修工具」，也不值得单独立一个实现计划。

---

## 6. 修的话怎么修

**本项目内可做的（如果维护者仍想做，属于文档约定，不是工具修复）**：

1. 在 `docs/superpowers/plans/` 的新计划、以及 `docs/issue-triage/README.md` 之外的项目级约定里，写明一条书写规范：
   **引用 vanilla / 模组源码行号一律带目录**，写成 `Source/Verse/PawnRenderer.cs:353` 而不是裸 `PawnRenderer.cs:353`。
   （吐槽板 `:149-150` 已自行提出同样建议，并提到「本档的代码注释里我已经这么建议了（审查报告 Minor M4）」。）
2. 在面向 AI 的文档（如 `AI-DEPLOYMENT.md` 的工具使用约定段）加一句操作提示：
   调 `rimworld-source-online` 的 `read_file` 前，若只知道裸文件名，先用 `search_source` 或 `list_directory` 定位真实路径 —— 命名空间 ≠ 目录（`Verse` 命名空间的渲染栈全在 `Source/Verse/`，UI 相关的却在 `Source/RimWorld/`）。

**本项目内明确不建议做的**：

- 不要为了「修这条」去改本项目的 MCP 服务器（`MCP/index.js`）—— 它与 `rimworld-source-online` 无任何代码关系，改了也不生效。
- 不要为此新增本地 `locate_type` 之类的工具来替代：外部服务器已有 `search_source` 能定位路径；而且**本项目已有更强的定位能力** —— `decompilerserver` 的 `search_types` / `get_member_details` / `get_overrides` 都能直接回答「这个类型/成员是什么、签名与可见性如何」，只是同样属于外部服务器。

**若要真修**，只能反馈给 `rimworld-source-online` 的维护方（建议 1：File not found 时做 basename 模糊匹配提示；建议 2：加 `locate_type`）。**本项目不是承接方。**

---

## 7. 是否已被现有计划覆盖

**不适用（本项目内无可修对象，故无「被覆盖」问题）。**

- `docs/superpowers/plans/` 下只有一份 `2026-09-27-ue-csharp-console-roslyn-upgrade.md`（C# 控制台 Roslyn 升级，12 个任务），与本条目无关。
- `docs/issue-triage/plans/` 当前为空，无其它子代理写过重复计划。

**结论：不值得修。建议维护者把本条状态改为「不接受（第三方）」，若要留痕就把「文档侧引用带目录」这条书写规范并进现有的写作规范文档，而不是单开一个 issue。**
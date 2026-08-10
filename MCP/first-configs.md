# UESDdebuger — 各 IDE 的 MCP 配置代码

## Header

UESDdebuger 把以下组件打包成一个调试用模组（UnityExplorer + SDB 代码级调试器 + MCP 调试桥）：

| 组件 | 作用 | 怎么用 |
|---|---|---|
| UnityExplorer（UE 4.9.0） | 游戏内对象浏览器 / C# 控制台 / Inspector / Hook 工具 | 本身也可手动使用 |
| McpRimDebug（SDB 调试器） | 代码级调试桥，把游戏状态与调试能力暴露给外部 | 由 MCP 服务器自动调用 |
| RimWorld API（RIMAPI） | 通过该模组实现对游戏的便捷控制（查询殖民地/世界数据、存档读档、事件触发等） | 由 MCP 服务器自动调用（进图后 RIMAPI 就绪） |
| DebugAction | 执行 RimWorld 原生调试菜单动作（列表 / 搜索 / 执行） | 由 MCP 服务器自动调用（需进图） |

其目标为实现在无监管状态下AI对游戏的自动调试需求（又名修红字）。

以下为配置方法：
1.既然已经看到本页面，其各个路径应以自动设置好
（如果需要再调整，比如使用rimworld副本，前往模组设置修改或直接修改配置文件）

2.打开IDE，找到MCP配置文件，复制配置代码到IDE的MCP配置文件中（或其他加入mcp服务器方法）。

> 提示：`<UESDdebuger>/runtime/bun/bun.exe`、`runtime/node/node.exe`、`MCP/node_modules/` 均无需手动准备。工坊版只携带压缩包 `runtime/bun/bun.zip`（约 39MB）、`runtime/node/node.zip`（约 34MB）与 `MCP/node_modules.zip`（约 5MB；exe 本体 113/86MB、node_modules 3700+ 碎文件不适合直接上传），首次启动游戏时模组会自动解压出 bun.exe / node.exe / node_modules；若游戏尚未启动过，先启动一次游戏再连接 MCP。


## Trae

```json
{
  "mcpServers": {
    "rimworld_DebugInEnvironment": {
      "command": "<UESDdebuger>/runtime/bun/bun.exe",
      "args": ["run", "<UESDdebuger>/MCP/index.js"],
      "env": { "MCP_TRANSPORT": "stdio" }
    }
  }
}
```

## opencode

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "rimworld_DebugInEnvironment": {
      "type": "local",
      "command": ["<UESDdebuger>/runtime/bun/bun.exe", "run", "<UESDdebuger>/MCP/index.js"],
      "enabled": true,
      "environment": {
        "MCP_TRANSPORT": "stdio"
      }
    }
  }
}
```

## Claude Desktop

```json
{
  "mcpServers": {
    "rimworld_DebugInEnvironment": {
      "command": "<UESDdebuger>/runtime/bun/bun.exe",
      "args": ["run", "<UESDdebuger>/MCP/index.js"],
      "env": { "MCP_TRANSPORT": "stdio" }
    }
  }
}
```

## Codex

```toml
[mcp_servers.rimworld_DebugInEnvironment]
command = "<UESDdebuger>/runtime/bun/bun.exe"
args = ["run", "<UESDdebuger>/MCP/index.js"]
env = { MCP_TRANSPORT = "stdio" }
```

## Gemini CLI

写入 `~/.gemini/settings.json`，服务器定义在顶层 `mcpServers` 对象下：

```json
{
  "mcpServers": {
    "rimworld_DebugInEnvironment": {
      "command": "<UESDdebuger>/runtime/bun/bun.exe",
      "args": ["run", "<UESDdebuger>/MCP/index.js"],
      "env": { "MCP_TRANSPORT": "stdio" }
    }
  }
}
```

## Kilo Code

```json
{
  "mcpServers": {
    "rimworld_DebugInEnvironment": {
      "command": "<UESDdebuger>/runtime/bun/bun.exe",
      "args": ["run", "<UESDdebuger>/MCP/index.js"],
      "env": { "MCP_TRANSPORT": "stdio" }
    }
  }
}
```

## VSCode

```json
{
  "servers": {
    "rimworld_DebugInEnvironment": {
      "type": "stdio",
      "command": "<UESDdebuger>/runtime/bun/bun.exe",
      "args": ["run", "<UESDdebuger>/MCP/index.js"],
      "env": { "MCP_TRANSPORT": "stdio" }
    }
  }
}
```

## Footer




3.现在，你应该已经可以调用DebugAction，UnityExplorer，RIMAPI（如果安装了附属模组，不需要排序置于前面）

如果启动McpRimDebug，需要一定程度上修改游戏本身以启动unity调试模式。


3.1确认游戏的 Unity 版本：看游戏根目录的 RimWorldWin64.exe，右键属性 → 详细信息 → 产品版本，目前1.6版本为 2022.3.35f1。


3.2前往 Unity 国际版构建版本下载安装 Windows 版、版本号严格匹配。


3.3 装好后进安装目录取前置文件，路径是：
[安装目录]\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\WinPixEventRuntime.dll

[安装目录]\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\UnityPlayer.dll

[安装目录]\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\WindowsPlayer.exe


3.4 复制或替换游戏本体文件（核心步骤，先备份）目标为游戏目录根下的 UnityPlayer.dll、WinPixEventRuntime.dll、RimWorldWin64.exe 各复制一份改后缀当备份；再把上面那个目录里的 UnityPlayer.dll、WinPixEventRuntime.dll 复制过去覆盖同名文件，最后把里面的 WindowsPlayer.exe 改名为 RimWorldWin64.exe 覆盖过去。


3.5 配置游戏文件：在 RimWorldWin64_Data\boot.config 末尾追加两行 wait-for-managed-debugger=1 和 player-connection-debug=1（本项目已配好）。

3.6 重启游戏，即可进入调试模式。

4.到此完全配置完成



> 本项目为个人调试用途发布，非官方模组，与 Ludeon Studios 无关联
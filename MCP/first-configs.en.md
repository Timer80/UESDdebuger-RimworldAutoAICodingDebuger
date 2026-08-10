# UESDdebuger — MCP configuration code for each IDE

## Header

UESDdebuger bundles the following components into a single debugging mod (UnityExplorer + SDB code-level debugger + MCP debug bridge):

| Component | Purpose | How to use |
|---|---|---|
| UnityExplorer (UE 4.9.0) | In-game object browser / C# console / Inspector / Hook tools | Can also be used manually |
| McpRimDebug (SDB debugger) | Code-level debug bridge exposing game state and debugging capabilities to external clients | Automatically invoked by the MCP server |
| RimWorld API (RIMAPI) | Convenient game control through this mod (query colony/world data, save/load, trigger events, etc.) | Automatically invoked by the MCP server (RIMAPI ready after entering a map) |
| DebugAction | Execute native RimWorld debug menu actions (list / search / execute) | Automatically invoked by the MCP server (requires entering a map) |

Its goal is to let an AI debug the game automatically without supervision (also known as "fixing red errors").

Here is how to configure:
1. Since you can already see this page, the paths should have been set up automatically.
(If you need to adjust them, e.g. when using a copy of RimWorld, change them in the mod settings or edit the config file directly.)

2. Open your IDE, find its MCP config file, and paste the configuration code into the IDE's MCP config file (or use any other way of adding an MCP server).

> Note: `<UESDdebuger>/runtime/bun/bun.exe`, `runtime/node/node.exe` and `MCP/node_modules/` need no manual setup. The Workshop build ships only the archives `runtime/bun/bun.zip` (~39MB), `runtime/node/node.zip` (~34MB) and `MCP/node_modules.zip` (~5MB; the exes are ~113/86MB and node_modules has 3700+ small files, both unsuitable for direct upload). On the first game launch the mod automatically extracts bun.exe / node.exe / node_modules; if you haven't launched the game yet, start the game once before connecting the MCP.

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

Write to `~/.gemini/settings.json`; the server is defined under the top-level `mcpServers` object:

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

3. At this point you should already be able to use DebugAction, UnityExplorer, and RIMAPI (if the companion mod is installed, it does not need to be ordered before this one).

If you want to use McpRimDebug, you need to modify the game somewhat to enable Unity debug mode.

3.1 Confirm the game's Unity version: right-click RimWorldWin64.exe in the game root directory → Properties → Details → Product version. For version 1.6 it is currently 2022.3.35f1.

3.2 Download and install the Windows build from Unity's international release archive with a strictly matching version number.

3.3 Once installed, get the prerequisite files from the install directory:
[InstallDir]\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\WinPixEventRuntime.dll

[InstallDir]\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\UnityPlayer.dll

[InstallDir]\Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono\WindowsPlayer.exe

3.4 Copy or replace the game's core files (key step — back up first). In the game root directory, make a copy of UnityPlayer.dll, WinPixEventRuntime.dll and RimWorldWin64.exe, each renamed with a different extension as a backup; then copy UnityPlayer.dll and WinPixEventRuntime.dll from the directory above over the same-named files, and finally rename WindowsPlayer.exe to RimWorldWin64.exe and overwrite.

3.5 Configure the game files: append two lines to the end of RimWorldWin64_Data\boot.config — wait-for-managed-debugger=1 and player-connection-debug=1 (already done in this project).

3.6 Restart the game and it will enter debug mode.

4. Configuration is now fully complete.

> This project is published for personal debugging purposes. It is not an official mod and is not affiliated with Ludeon Studios.

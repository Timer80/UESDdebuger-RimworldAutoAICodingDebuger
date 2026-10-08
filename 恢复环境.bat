@echo off
chcp 65001 >nul
setlocal
title UESDdebuger 环境恢复（回到首次安装状态）

rem 说明：本批处理只是 恢复环境.ps1 的启动器。
rem   · 重压 MCP\node_modules.zip（按当前 node_modules 状态冻结）
rem   · 清掉解压产物 node_modules / node.exe / bun.exe（首启会由各自 zip 重建）
rem   · 删除两个首启标志 MCP\.auto-deploy.done / MCP\.announce.done
rem   · 顺带清掉首启会自动重建的 config.json / ports.json 与运行日志
rem
rem 游戏开着也能跑：会只结束占用文件的 MCP 端进程（bun/node/dotnet 跑 MCP/index.js 与
rem McpRimDebug 的那些），不碰 RimWorldWin64。这些运行时归 MCP 端在游戏进程外加载，
rem 与游戏运行无关；会重建它们的是**游戏的启动时刻**（AutoDeploy/UnzipRuntime）。
rem 所以：清完不要再重启游戏，否则又会被重建。

set "PS1=%~dp0恢复环境.ps1"
if not exist "%PS1%" (
    echo [错误] 找不到 恢复环境.ps1（应与本文件放在同一目录）
    pause
    exit /b 1
)

where pwsh >nul 2>nul
if %errorlevel%==0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
)

endlocal

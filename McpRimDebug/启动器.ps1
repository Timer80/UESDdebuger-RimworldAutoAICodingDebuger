# ============================================================
# McpRimDebug 启动器：一键设置调试环境变量并启动 RimWorld。
#
# 用法（PowerShell）：
#   .\启动器.ps1                # 用默认路径启动游戏
#   .\启动器.ps1 -SkipLaunch    # 仅设置环境变量，不启动游戏（供手动启动场景）
#
# 说明：
#   1) Unity 内嵌调试代理的端口**每次运行随机**（boot.config 的
#      wait-for-managed-debugger=1 触发），并写入 Player.log：
#      "Starting managed debugger on port XXXX"。
#   2) 游戏启动后会弹出「等待调试器」窗口并显示实际端口——该窗口是 Unity 机制，
#      点击 Attach 后游戏继续运行；窗口消失后即可用 status 工具获取端口。
#   3) 启动后请调用 MCP 工具 status，读取 data.debugPortFromLog 得到实际端口，
#      再用 attach(host, 该端口) 连接（详见 README.md）。
#   4) 一次仅运行一个游戏进程，否则端口冲突会导致无法 attach。
# ============================================================

param(
    [switch]$SkipLaunch,
    [string]$GamePath = "C:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64.exe",
    [string]$LogPath  = "$env:LOCALAPPDATA\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Player.log",
    [string]$Host_    = "127.0.0.1",
    [int]   $Port     = 56574
)

$ErrorActionPreference = "Stop"

Write-Host "== McpRimDebug 启动器 ==" -ForegroundColor Cyan

if (-not (Test-Path $GamePath)) {
    Write-Host "[错误] 游戏可执行文件不存在: $GamePath" -ForegroundColor Red
    Write-Host "请用 -GamePath 指定正确路径（例如 -GamePath D:\Games\RimWorld\RimWorldWin64.exe）"
    exit 1
}

# 设置环境变量（MCP 服务器实时读取，launch/attach/status 均生效）
[Environment]::SetEnvironmentVariable("MCP_RIMDBG_GAME_PATH", $GamePath, "Process")
[Environment]::SetEnvironmentVariable("MCP_RIMDBG_LOG_PATH",  $LogPath,  "Process")
[Environment]::SetEnvironmentVariable("MCP_RIMDBG_HOST",      $Host_,    "Process")
[Environment]::SetEnvironmentVariable("MCP_RIMDBG_PORT",      [string]$Port, "Process")

Write-Host "[ok] 环境变量已设置:" -ForegroundColor Green
Write-Host "     MCP_RIMDBG_GAME_PATH = $GamePath"
Write-Host "     MCP_RIMDBG_LOG_PATH  = $LogPath"
Write-Host "     MCP_RIMDBG_HOST      = $Host_"
Write-Host "     MCP_RIMDBG_PORT      = $Port"

if ($SkipLaunch) {
    Write-Host "[ok] -SkipLaunch 已指定，跳过启动游戏（请自行启动 RimWorld）。"
    exit 0
}

# 检测已有游戏进程（一次仅一个进程，否则端口冲突）
$running = Get-Process -Name RimWorldWin64 -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[错误] 检测到 RimWorld 已在运行 (PID $($running.Id))。" -ForegroundColor Red
    Write-Host "Unity 调试端口每次运行随机，同时运行两个实例会端口冲突而无法 attach。"
    Write-Host "请先关闭现有游戏进程后重试。"
    exit 1
}

Write-Host "[ok] 启动游戏: $GamePath"
try {
    Start-Process -FilePath $GamePath -WorkingDirectory (Split-Path $GamePath)
} catch {
    Write-Host "[错误] 启动失败: $_" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "下一步：" -ForegroundColor Yellow
Write-Host "  1) 游戏窗口会弹出「等待调试器」窗口并显示随机调试端口（Unity 机制），"
Write-Host "     该窗口会自动等待 MCP 端 attach；也可手动确认后继续。"
Write-Host "  2) 调用 MCP 工具 status —— 读取 data.debugPortFromLog 获取实际端口。"
Write-Host "  3) 调用 attach(host, debugPortFromLog) 连接，随后 resume 游戏开始运行。"
Write-Host "     （示例: attach(127.0.0.1, 56651)）"

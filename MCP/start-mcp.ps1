# ============================================================
# start-mcp.ps1 - Start RimWorld MCP Server (SSE mode)
# Usage: run `.\start-mcp.ps1` inside UESDdebuger/MCP
#
# - Opens a new PowerShell window running `node index.js`
# - index.js writes logs to logs/mcp-<YYYYMMDD-HHmmss>.log by itself
# - Tee-Object below is an extra window-side copy (mcp-window-*.log)
# ============================================================

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logsDir   = Join-Path $scriptDir 'logs'
if (-not (Test-Path $logsDir)) {
  New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
}

# 解析运行时：优先 UESDdebuger/runtime/ 便携 node，找不到回退 PATH
. (Join-Path $scriptDir 'resolve-runtime.ps1')
$nodeExe = Get-ToolPath -Name node
if (-not $nodeExe) {
  Write-Host '[错误] 未找到 node（无便携 runtime/node/node.exe，PATH 中也没有 node），请先运行 bundle-env.ps1 或安装 Node.js' -ForegroundColor Red
  exit 1
}

$windowLogFile = Join-Path $logsDir ("mcp-window-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

Start-Process powershell -ArgumentList '-NoExit', '-Command', "cd '$scriptDir'; & '$nodeExe' index.js 2>&1 | Tee-Object -FilePath '$windowLogFile'" -WorkingDirectory $scriptDir

Write-Host ''
Write-Host 'RimWorld MCP Server is starting in a new PowerShell window...' -ForegroundColor Green
Write-Host ''
Write-Host 'Log files:'
Write-Host ("  - index.js auto log : {0}\mcp-<timestamp>.log" -f $logsDir)
Write-Host "  - window tee log    : $windowLogFile"
Write-Host ''
Write-Host 'SSE endpoint : http://127.0.0.1:3000/sse'
Write-Host 'Health check : http://127.0.0.1:3000/health'
Write-Host ''

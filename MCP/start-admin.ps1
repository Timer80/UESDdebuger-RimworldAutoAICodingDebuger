# RimWorld MCP 服务器启动脚本（管理员权限）
param(
    [switch]$NoAdminCheck
)

# 获取脚本所在目录
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $scriptDir) {
    $scriptDir = Get-Location
}

# 检查是否以管理员身份运行
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")

if (-not $isAdmin -and -not $NoAdminCheck) {
    # 如果不是管理员，重新以管理员身份启动
    Write-Host "需要管理员权限，正在提升权限..." -ForegroundColor Yellow
    $scriptPath = Join-Path $scriptDir "start-admin.ps1"
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$scriptPath`" -NoAdminCheck" -Verb RunAs -Wait
    exit
}

# 以管理员身份运行 MCP 服务器
try {
    . (Join-Path $scriptDir 'resolve-runtime.ps1')
    $nodePath = Get-ToolPath -Name node
    if (-not $nodePath) { throw '未找到 node（无便携 runtime/node/node.exe，PATH 中也没有 node）' }
} catch {
    Write-Error "未找到 Node.js，请先运行 bundle-env.ps1 生成便携运行时，或安装 Node.js 并添加到环境变量: $_"
    exit 1
}

$indexPath = Join-Path $scriptDir "index.js"

if (-not (Test-Path $indexPath)) {
    Write-Error "找不到 index.js 文件: $indexPath"
    exit 1
}

Write-Host "========================================" -ForegroundColor Green
Write-Host "  RimWorld MCP 服务器 (管理员模式)" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host "Node 路径: $nodePath" -ForegroundColor Cyan
Write-Host "脚本路径: $indexPath" -ForegroundColor Cyan
Write-Host "工作目录: $scriptDir" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

# 切换到工作目录
Set-Location $scriptDir

# 启动 MCP 服务器
& $nodePath $indexPath

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Error "MCP 服务器异常退出，错误码: $LASTEXITCODE"
}

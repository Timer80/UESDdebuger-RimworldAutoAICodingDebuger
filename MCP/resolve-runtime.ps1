# ============================================================
# resolve-runtime.ps1 - 解析便携运行时工具路径
#
# 统一入口：各启动脚本（start-mcp.ps1 / start-admin.ps1 /
# start-aggregator.ps1 等）优先使用 UESDdebuger/runtime/ 下的便携工具，
# 找不到时回退 PATH 上的系统工具。
#
# 用法：
#   . .\resolve-runtime.ps1          # 点源后调用函数
#   Get-ToolPath -Name node          # 返回 node 路径（便携优先）
#   Get-ToolPath -Name bun
#   Get-ToolPath -Name dotnet
# ============================================================

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = Get-Location }

# UESDdebuger 根目录（MCP/resolve-runtime.ps1 → 上一级）
$runtimeRoot = Join-Path (Split-Path $scriptDir -Parent) "runtime"

$runtimeTools = @{
    node   = "node\node.exe"
    bun    = "bun\bun.exe"
    dotnet = "dotnet\dotnet.exe"
}

function Get-ToolPath {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("node", "bun", "dotnet")]
        [string]$Name
    )

    # 1) 便携运行时优先
    $rel = $runtimeTools[$Name]
    if ($rel) {
        $portable = Join-Path $runtimeRoot $rel
        if (Test-Path $portable) {
            return (Resolve-Path $portable).Path
        }
    }

    # 2) 回退 PATH
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    return $null
}

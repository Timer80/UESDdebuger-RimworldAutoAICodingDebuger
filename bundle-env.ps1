# ============================================================
# bundle-env.ps1 - 构建完全便携化的开发/调试工具链运行时
#
# 目标：把 MCP 服务器 / 聚合器 / McpRimDebug 所需的运行时全部打入
#   runtime/ 目录，使整个 UESDdebuger 项目拷贝到任意 Windows 机器后
#   无需安装 node / bun / .NET 即可使用。
#
# runtime/ 结构：
#   runtime/node/node.exe               ← Node.js（复制本机）
#   runtime/bun/bun.exe                 ← bun（复制本机，可选）
#   runtime/dotnet/                     ← .NET 运行时 zip 便携包（官方免安装版）
#       dotnet.exe + host/fxr + shared/Microsoft.NETCore.App/10.0.x
#   runtime/McpRimDebug/                ← McpRimDebug FDD 发布产物（dll + deps）
#
# 用法：
#   .\bundle-env.ps1                      # 全量构建
#   .\bundle-env.ps1 -SkipNode -SkipBun -SkipDotnet   # 跳过某类
#   .\bundle-env.ps1 -SkipBun -SkipDotnet             # 只要 node（最小）
#   .\bundle-env.ps1 -DotnetZipPath <本地zip>          # 离线：用本地 runtime zip
# ============================================================

param(
    [switch]$SkipNode,
    [switch]$SkipBun,
    [switch]$SkipDotnet,
    [switch]$SkipMcpRimDebug,
    [string]$NodeSource   = "C:\Program Files\nodejs\node.exe",
    [string]$BunSource    = "$env:USERPROFILE\.bun\bin\bun.exe",
    [string]$DotnetVersion = "10.0.5",
    [string]$DotnetZipPath,              # 指定则用本地 zip，不再下载
    [string]$DotnetZipUrl  = "https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/dotnet-runtime-{version}-win-x64.zip"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $root) { $root = Get-Location }

function New-Dir([string]$p) {
    if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
}

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "== $msg ==" -ForegroundColor Cyan
}

function Write-Ok([string]$msg) {
    Write-Host ("[ok] " + $msg) -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host "[skip] $msg" -ForegroundColor Yellow
}

$runtimeRoot = Join-Path $root "runtime"
New-Dir $runtimeRoot

Write-Host ""
Write-Host "==============================" -ForegroundColor Green
Write-Host "  UESDdebuger 便携环境打包 (bundle-env)"
Write-Host "==============================" -ForegroundColor Green

# ---------- 1. Node.js ----------
if ($SkipNode) {
    Write-Warn "Node.js（-SkipNode）"
} else {
    Write-Step "1/5 Node.js"
    $nodeDir = Join-Path $runtimeRoot "node"
    New-Dir $nodeDir
    $nodeDst = Join-Path $nodeDir "node.exe"
    if (Test-Path $NodeSource) {
        Copy-Item -LiteralPath $NodeSource -Destination $nodeDst -Force
        $ver = & $nodeDst --version 2>$null
        Write-Ok "node.exe -> $nodeDst  ($ver)"
        # node.exe（约86MB）同样超过工坊上传体积限制：同步生成 node.zip（仅含顶层 node.exe），
        # 工坊分发只带 zip，首次游戏启动时由 UnzipRuntime 自动解压出 node.exe。
        $nodeZip = Join-Path $nodeDir "node.zip"
        Compress-Archive -LiteralPath $nodeDst -DestinationPath $nodeZip -CompressionLevel Optimal -Force
        Write-Ok ("node.zip -> {0}（{1:N1} MB）" -f $nodeZip, ((Get-Item $nodeZip).Length/1MB))
    } else {
        Write-Warn "Node.js 源不存在: $NodeSource（用 -NodeSource 指定）"
    }
}

# ---------- 2. bun ----------
if ($SkipBun) {
    Write-Warn "bun（-SkipBun）"
} else {
    Write-Step "2/5 bun"
    $bunDir = Join-Path $runtimeRoot "bun"
    New-Dir $bunDir
    $bunDst = Join-Path $bunDir "bun.exe"
    if (Test-Path $BunSource) {
        Copy-Item -LiteralPath $BunSource -Destination $bunDst -Force
        Write-Ok "bun.exe -> $bunDst"
        # bun.exe（约113MB）超过工坊上传体积限制：同步生成 bun.zip（仅含顶层 bun.exe），
        # 工坊分发只带 zip，首次游戏启动时由 UnzipRuntime 自动解压出 bun.exe。
        $bunZip = Join-Path $bunDir "bun.zip"
        Compress-Archive -LiteralPath $bunDst -DestinationPath $bunZip -CompressionLevel Optimal -Force
        Write-Ok ("bun.zip -> {0}（{1:N1} MB）" -f $bunZip, ((Get-Item $bunZip).Length/1MB))
    } else {
        Write-Warn "bun 源不存在: $BunSource（用 -BunSource 指定；MCP 服务器可用 node 替代，可不打）"
    }
}

# ---------- 3. .NET runtime zip（免安装便携版） ----------
if ($SkipDotnet) {
    Write-Warn ".NET runtime（-SkipDotnet）"
} else {
    Write-Step "3/5 .NET runtime ($DotnetVersion)"
    $dotnetDir = Join-Path $runtimeRoot "dotnet"
    $dotnetExe = Join-Path $dotnetDir "dotnet.exe"
    if (Test-Path $dotnetExe) {
        Write-Ok "已存在 $dotnetExe，跳过（删除 runtime/dotnet 可重建）"
    } else {
        $tmpZip = $DotnetZipPath
        if (-not $tmpZip -or -not (Test-Path $tmpZip)) {
            $tmpZip = Join-Path $env:TEMP "dotnet-runtime-$DotnetVersion-win-x64.zip"
            $url = $DotnetZipUrl.Replace("{version}", $DotnetVersion)
            Write-Host "下载: $url"
            Invoke-WebRequest -Uri $url -OutFile $tmpZip -UseBasicParsing
        } else {
            Write-Host "使用本地 zip: $tmpZip"
        }
        New-Dir $dotnetDir
        Write-Host "解压中...（$([math]::Round((Get-Item $tmpZip).Length/1MB,1)) MB）"
        Expand-Archive -LiteralPath $tmpZip -DestinationPath $dotnetDir -Force
        Write-Ok "dotnet -> $dotnetExe"
    }
    if (Test-Path $dotnetExe) {
        # 便携运行时无 SDK，--version 会失败；用 --list-runtimes 校验运行时已就位
        $rt = & $dotnetExe --list-runtimes 2>$null | Select-Object -First 1
        Write-Ok "便携 dotnet: $rt"
    }
}

# ---------- 4. McpRimDebug（FDD 发布，配合便携 dotnet 运行） ----------
if ($SkipMcpRimDebug) {
    Write-Warn "McpRimDebug（-SkipMcpRimDebug）"
} else {
    Write-Step "4/5 McpRimDebug (FDD publish)"
    $mcpOut = Join-Path $runtimeRoot "McpRimDebug"
    $mcpCsproj = Join-Path $root "McpRimDebug\McpRimDebug.csproj"
    if (-not (Test-Path $mcpCsproj)) {
        Write-Warn "csproj 不存在: $mcpCsproj"
    } else {
        # publish 需要 SDK：用系统 dotnet（便携运行时无 SDK），运行时才用便携 dotnet
        $dotnetCmd = (Get-Command dotnet -ErrorAction SilentlyContinue)
        if (-not $dotnetCmd) {
            Write-Host "[错误] 系统未安装 .NET SDK，无法 publish McpRimDebug（可先装 SDK 或用其它机器 publish 后拷贝）" -ForegroundColor Red
            exit 1
        }
        Write-Host "用系统 $($dotnetCmd.Source) publish（框架依赖 FDD，运行时用便携 dotnet）"
        & $dotnetCmd.Source publish $mcpCsproj -c Release -o $mcpOut
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[错误] publish 失败，退出码 $LASTEXITCODE" -ForegroundColor Red
            exit 1
        }
        $dll = Join-Path $mcpOut "McpRimDebug.dll"
        if (Test-Path $dll) {
            Write-Ok "McpRimDebug.dll -> $dll"
        }
    }
}

# ---------- 5. MCP/node_modules（碎文件打包成单个 zip，首次启动自动解压） ----------
Write-Step "5/5 MCP/node_modules"
$mcpModulesDir = Join-Path $root "MCP\node_modules"
$mcpModulesZip = Join-Path $root "MCP\node_modules.zip"
if (-not (Test-Path $mcpModulesDir)) {
    Write-Warn "MCP\node_modules 不存在（未 npm install？），跳过 node_modules.zip"
} else {
    Compress-Archive -Path $mcpModulesDir -DestinationPath $mcpModulesZip -CompressionLevel Optimal -Force
    Write-Ok ("node_modules.zip -> {0}（{1:N1} MB，{2} 个文件打包）" -f $mcpModulesZip,
        ((Get-Item $mcpModulesZip).Length/1MB),
        (Get-ChildItem $mcpModulesDir -Recurse -File | Measure-Object).Count)
}

Write-Step "完成"
$total = (Get-ChildItem $runtimeRoot -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
Write-Ok ("runtime/ 总大小: {0:N1} MB" -f ($total/1MB))

Write-Host ""
Write-Host "下一步：" -ForegroundColor Yellow
Write-Host "  1) 验证: runtime/McpRimDebug 下用 runtime/dotnet/dotnet.exe McpRimDebug.dll --selftest"
Write-Host "  2) MCP 服务器: runtime/node/node.exe UESDdebuger/MCP/index.js（stdio 模式自动优先便携 McpRimDebug）"
Write-Host "  3) 分发: 整个 UESDdebuger 目录 + runtime/ 一起打包 zip 即可（见 桥接操作文档.md）"
Write-Host ""

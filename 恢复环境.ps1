# ============================================================================
#  恢复环境.ps1 —— 把 UESDdebuger 快速还原成「玩家首次安装」的干净状态
#
#  做两件事：
#    1) 重压环境：按当前 MCP/node_modules 重新生成 MCP/node_modules.zip，
#       再解压一遍验证「zip ↔ 目录」零差异。
#    2) 删除两个首启标志：MCP\.auto-deploy.done、MCP\.announce.done，
#       并顺带清掉首启会自动重建的环境文件与运行日志。
#
#  下一次启动游戏时，模组会自己走完整首启流程：
#    重新探测路径生成 MCP/config.json、解压 node.exe / bun.exe / node_modules、
#    弹「部署方法」配置公告 + 全量更新公告。
#
#  用法：双击 恢复环境.bat（无需管理员权限）
#
#  重要前提：请先【关闭游戏】。脚本会自动结束占用文件的 MCP 服务器进程；
#            本脚本自身也在本目录内，不建议在此目录开着编辑器/终端跑它。
# ============================================================================

$ErrorActionPreference = 'Continue'
$Root = $PSScriptRoot
$MCP  = Join-Path $Root 'MCP'

# 双击运行时窗口会立刻关掉，需要停一下让人看清结果；
# 但被其它脚本/自动化调用（非交互）时绝不能阻塞 —— 所以只在交互式宿主里暂停。
$Interactive = $true
try { $Interactive = -not [Console]::IsInputRedirected } catch { $Interactive = $true }
function Wait-BeforeExit {
    if ($Interactive) { Read-Host '按回车键关闭窗口' }
    else { Write-Info '（非交互运行，不等待按键）' }
}

# ---------------------------------------------------------------- 外观
function Write-Title([string]$t) {
    Write-Host ''
    Write-Host ('=' * 68) -ForegroundColor DarkCyan
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ('=' * 68) -ForegroundColor DarkCyan
}
function Write-Ok([string]$m)   { Write-Host "  [完成] $m" -ForegroundColor Green }
function Write-Info([string]$m) { Write-Host "  [信息] $m" -ForegroundColor Gray }
function Write-Warn([string]$m) { Write-Host "  [跳过] $m" -ForegroundColor Yellow }
function Write-Bad([string]$m)  { Write-Host "  [警告] $m" -ForegroundColor Red }
function Write-Key([string]$m)  { Write-Host "  $m" -ForegroundColor White }

# ---------------------------------------------------------------- 回收站删除
Add-Type -AssemblyName Microsoft.VisualBasic | Out-Null

function Remove-ToRecycleBin([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return $false }
    try {
        if ((Get-Item -LiteralPath $p -Force).PSIsContainer) {
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory(
                $p,
                [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
                [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
        } else {
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile(
                $p,
                [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,
                [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
        }
        return $true
    } catch {
        Write-Bad "回收失败：$p —— $($_.Exception.Message)"
        return $false
    }
}

Write-Title 'UESDdebuger 环境恢复（回到首次安装状态）'
Write-Key "模组目录: $Root"
Write-Key "开始时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

# ================================================================ 步骤 1/5
Write-Title '1/5  结束占用文件的进程（只杀 MCP 端，不碰游戏）'

# 只认 MCP 端的加载者：
#   · MCP 服务器 = 命令行跑 MCP/index.js 的 bun/node
#   · 调试桥     = 跑 McpRimDebug 的 dotnet（占着 runtime/McpRimDebug/*.dll）
# 绝不匹配本脚本自己的命令行（否则会误杀自身），也绝不碰 RimWorldWin64。
function Stop-McpSideProcesses {
    $n = 0
    $targets = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -ne $PID -and $_.CommandLine -and (
            ($_.CommandLine -match 'MCP[\\/]index\.js' -and $_.Name -match '^(bun|node)\.exe$') -or
            ($_.CommandLine -match 'McpRimDebug'        -and $_.Name -eq 'dotnet.exe')
        )
    })
    foreach ($proc in $targets) {
        try {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
            Write-Ok "已结束 $($proc.Name) PID=$($proc.ProcessId)"
            $n++
        } catch {
            Write-Bad "结束 PID=$($proc.ProcessId) 失败：$($_.Exception.Message)"
        }
    }
    return $n
}

$killed = Stop-McpSideProcesses
Start-Sleep -Seconds 2
if ($killed -eq 0) { Write-Info '没有发现需要结束的 MCP 相关进程' }

# ---------------------------------------------------------------- 游戏不必关
# 交付态 = 首启态（解压产物与状态文件都不在），而重建它们的是 **游戏的启动时刻**
# —— AutoDeploy / UnzipRuntime 只在 [StaticConstructorOnStartup] 跑一次，启动时若发现缺失就地解压/写配置。
# 游戏跑起来之后，它用的是自带的 Mono；runtime/{node,bun,dotnet} 与 MCP/node_modules
# 都是 **MCP 端进程**（bun/npm/dotnet）在游戏进程外加载的，与游戏运行无关。
# 所以：游戏开着也能清，只要确保 MCP 端没有进程正占用这些文件即可。
# 反过来，清完**不要再重启游戏**，否则又被重建（下次启动就当作"玩家首启"，正是想要的效果）。
$rimAlive = @(Get-Process RimWorldWin64 -ErrorAction SilentlyContinue)
if ($rimAlive.Count -gt 0) {
    Write-Info "游戏正在运行（PID $($rimAlive.Id -join ',')）—— 无需关闭，继续清理"
} else {
    Write-Info '游戏未运行 —— 同样可以清理'
}

# 文件锁探测
# 被占用时先重杀 MCP 端再探 —— MCP 宿主（agent/IDE）会在几秒内把服务器进程拉起来，
# 重新占住 node_modules / McpRimDebug.dll（实测 4 秒内就回来了），所以这一步要能重试而不是直接放弃。
$lockTargets = @(
    'MCP\node_modules\package.json',
    'runtime\node\node.exe',
    'runtime\bun\bun.exe',
    'runtime\McpRimDebug\McpRimDebug.dll'
)
function Get-LockedTargets {
    $locked = @()
    foreach ($rel in $lockTargets) {
        $full = Join-Path $Root $rel
        if (-not (Test-Path -LiteralPath $full)) { continue }
        try {
            $fs = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $fs.Close()
        } catch {
            $locked += $rel
        }
    }
    return $locked
}

$lockedNames = Get-LockedTargets
for ($attempt = 1; $attempt -le 3 -and $lockedNames.Count -gt 0; $attempt++) {
    Write-Warn "有 $($lockedNames.Count) 个文件被占用（第 $attempt 次处理），重新结束 MCP 端进程后复查"
    Stop-McpSideProcesses | Out-Null
    Start-Sleep -Seconds 3
    $lockedNames = Get-LockedTargets
}

if ($lockedNames.Count -gt 0) {
    Write-Bad '以下文件仍被占用（多半是编辑器/杀毒/终端在占用，非 MCP 端）：'
    $lockedNames | ForEach-Object { Write-Bad "    $_" }
    Write-Info '继续执行；若后续删除/解压失败，请关掉这些程序后重跑本脚本。'
} else {
    Write-Ok '关键文件均未被占用'
}

# ================================================================ 步骤 2/5
Write-Title '2/5  重压环境（重新生成 MCP\node_modules.zip）'

Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$nmDir = Join-Path $MCP 'node_modules'
$nmZip = Join-Path $MCP 'node_modules.zip'

if (-not (Test-Path -LiteralPath $nmDir)) {
    Write-Warn "MCP\node_modules 不存在（当前是未解压状态），本次不需要重压"
    $nmDirFiles = 0
} else {
    $nmStat = Get-ChildItem -LiteralPath $nmDir -Recurse -File -Force -ErrorAction SilentlyContinue |
              Measure-Object -Sum Length
    $nmDirFiles = $nmStat.Count
    Write-Info ("当前 node_modules      : {0} 个文件 / {1} MB" -f $nmStat.Count, [math]::Round($nmStat.Sum / 1MB, 2))

    # 解压环境的运行痕迹先清掉，让重压出来的是干净依赖树
    foreach ($junk in @('MCP\logs', 'MCP\ports.json', 'MCP\config.json',
                        'MCP\.auto-deploy.done', 'MCP\.announce.done')) {
        if (Remove-ToRecycleBin (Join-Path $Root $junk)) { Write-Ok "已回收 $junk（重压前清理）" }
    }

    $recompressed = $false
    try {
        # includeBaseDirectory=$true → zip 条目带 node_modules/ 前缀
        # （模组解压端 UnzipRuntime.StripNodeModulesPrefix 会剥掉该前缀，两种布局都兼容）
        $tmpZip = Join-Path $env:TEMP ("nm-rezip-{0}.zip" -f (Get-Random))
        if (Test-Path -LiteralPath $tmpZip) { Remove-Item -LiteralPath $tmpZip -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $nmDir, $tmpZip,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $true)

        $tmpZipInfo = Get-Item -LiteralPath $tmpZip
        if ($tmpZipInfo.Length -lt 10000) {
            throw "生成的 zip 体积异常（$($tmpZipInfo.Length) B），判定为失败"
        }

        # 新包验证通过后再回收旧包，避免出现「既没有旧包也没有新包」的空档
        Remove-ToRecycleBin $nmZip | Out-Null
        Move-Item -LiteralPath $tmpZip -Destination $nmZip -Force

        $zipEntries = ([System.IO.Compression.ZipFile]::OpenRead($nmZip)).Entries.Count
        Write-Ok ("新 MCP\node_modules.zip : {0} 个条目 / {1} MB" -f $zipEntries, [math]::Round((Get-Item -LiteralPath $nmZip).Length / 1MB, 2))
        $recompressed = $true
    } catch {
        Write-Bad "重压失败（旧 zip 保持不变）：$($_.Exception.Message)"
    }

    # 解压回 node_modules 并逐文件比对，证明「zip ↔ 目录」零差异
    if ($recompressed) {
        Write-Info '正在解压验证（zip → 临时目录 → node_modules）……'
        $verifyTmp = Join-Path $env:TEMP ("nm-verify-{0}" -f (Get-Random))
        try {
            [System.IO.Compression.ZipFile]::ExtractToDirectory($nmZip, $verifyTmp)
        } catch {
            Write-Bad "解压验证失败：$($_.Exception.Message)"
            $verifyTmp = $null
        }

        if ($verifyTmp -and (Test-Path -LiteralPath $verifyTmp)) {
            # 兼容带/不带顶层 node_modules 前缀两种解压布局
            $inner = Join-Path $verifyTmp 'node_modules'
            $actual = if (Test-Path -LiteralPath $inner) { $inner } else { $verifyTmp }

            function Snapshot([string]$b) {
                Get-ChildItem $b -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
                    ($_.FullName.Substring($b.Length + 1) -replace '\\', '/') + '|' + $_.Length
                } | Sort-Object
            }
            $diff = Compare-Object (Snapshot $actual) (Snapshot $nmDir)
            Write-Key ("重压前 {0} 个文件  →  解压后 {1} 个文件，差异条目 {2}" -f $nmDirFiles, (Snapshot $actual).Count, $diff.Count)
            if ($diff.Count -eq 0) {
                Write-Ok '零差异校验通过：node_modules 与 node_modules.zip 完全一致'
            } else {
                Write-Bad 'zip 与原树存在差异，请人工复核：'
                $diff | Select-Object -First 10 | ForEach-Object { Write-Bad "    $($_.SideIndicator) $($_.InputObject)" }
            }
            if (Test-Path -LiteralPath $verifyTmp) { Remove-Item -LiteralPath $verifyTmp -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}

# ================================================================ 步骤 3/5
Write-Title '3/5  解压产物清场（node.exe / bun.exe / node_modules）'
# 说明：这三样都由模组首启时按各自 zip 自动解压重建，交付包必须是「只有 zip、没有解压产物」的形态。
#   本步骤的作用就是把「已解压」的机器（开发机）重新压回首启态；
#   重压 node_modules.zip 已在步骤 2 完成（那里必须先解压出 node_modules 才有东西可压）。

Remove-ToRecycleBin $nmDir | Out-Null
Remove-ToRecycleBin (Join-Path $Root 'runtime\bun\bun.exe') | Out-Null
Remove-ToRecycleBin (Join-Path $Root 'runtime\node\node.exe') | Out-Null
Write-Ok '已清掉解压产物（首启会由各自 zip 自动重建）'

# ================================================================ 步骤 4/5
Write-Title '4/5  删除两个首启标志与首启会重建的环境文件'

$stateFiles = @(
    'MCP\.auto-deploy.done',   # 部署标志：删除后下次启动重新探测路径写 config.json
    'MCP\.announce.done',      # 公告标志：删除后下次启动重新播报全量更新日志
    'MCP\config.json',         # 首启自动生成（本机路径探测结果）
    'MCP\ports.json',          # 每次启动自动重写（动态端口）
    'MCP\logs',                # 运行日志
    'ShaderProject\Logs',      # Unity 日志
    'UE_Data\sinai-dev-UnityExplorer\logs'   # UnityExplorer 日志
)
foreach ($rel in $stateFiles) {
    if (Remove-ToRecycleBin (Join-Path $Root $rel)) { Write-Ok "已回收 $rel" }
    else { Write-Warn "不存在，无需处理：$rel" }
}

# ================================================================ 步骤 5/5
Write-Title '5/5  验证：是否已回到「玩家首次安装」状态'

$mustExist = @{
    'MCP\node_modules.zip'                    = '依赖包归档（首启据此重建 node_modules）'
    'runtime\bun\bun.zip'                     = 'bun 归档（首启据此重建 bun.exe）'
    'runtime\node\node.zip'                   = 'node 归档（首启据此重建 node.exe）'
    'runtime\dotnet\dotnet.exe'               = 'dotnet 运行时（无 zip，不可重建）'
    'runtime\McpRimDebug\McpRimDebug.dll'     = 'McpRimDebug 桥（无 zip，不可重建）'
}
$mustNotExist = @(
    'MCP\node_modules',
    'runtime\node\node.exe',
    'runtime\bun\bun.exe',
    'MCP\.auto-deploy.done',
    'MCP\.announce.done',
    'MCP\config.json',
    'MCP\ports.json'
)

$problems = 0

# 说明：这里的判定**不看游戏是否在跑**。游戏跑起来之后用的是自带 Mono，
# runtime/{node,bun,dotnet} 与 MCP/node_modules 归 MCP 端进程加载，与游戏运行无关，
# 所以"游戏开着"本身不影响首启态是否成立。
# 唯一要留意的是：**清完别再重启游戏**，否则 AutoDeploy/UnzipRuntime 会在启动时把它们重建出来。
$rimAlive = @(Get-Process RimWorldWin64 -ErrorAction SilentlyContinue)
Write-Key '--- 环境 ---'
if ($rimAlive.Count -gt 0) {
    Write-Host ("  [游戏运行中] PID $($rimAlive.Id -join ',') —— 不影响本判定；只要之后不再重启游戏，交付态就保持不变") -ForegroundColor Gray
} else {
    Write-Host '  [游戏未运行]' -ForegroundColor Gray
}

Write-Key '--- 必须存在 ---'
foreach ($rel in $mustExist.Keys | Sort-Object) {
    if (Test-Path -LiteralPath (Join-Path $Root $rel)) {
        Write-Host ("  [OK]   {0,-40} {1}" -f $rel, $mustExist[$rel]) -ForegroundColor Green
    } else {
        Write-Host ("  [缺失] {0,-40} {1}" -f $rel, $mustExist[$rel]) -ForegroundColor Red
        $problems++
    }
}
Write-Key '--- 必须不存在（已回到首启态：解压产物与状态文件都不该在） ---'
foreach ($rel in $mustNotExist) {
    if (Test-Path -LiteralPath (Join-Path $Root $rel)) {
        Write-Host ("  [残留] {0}" -f $rel) -ForegroundColor Red
        $problems++
    } else {
        Write-Host ("  [已清] {0}" -f $rel) -ForegroundColor Green
    }
}

Write-Title '恢复结果'
if ($problems -eq 0) {
    Write-Ok '环境已回到「玩家首次安装」状态，可以打包发布。'
    Write-Info '下次启动游戏时，模组会自动：重新生成 config.json → 解压运行时 → 弹配置公告与全量更新公告。'
} else {
    Write-Bad "有 $problems 项未达预期，请查看上面的红字提示。"
}
Write-Key "结束时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host ''
Wait-BeforeExit

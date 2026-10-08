<#
.SYNOPSIS
    监控 mcp-吐槽板.md 的条目变化；有新条目/条目被改动时立刻退出，
    借由"后台作业结束通知"把主代理叫醒（自动续跑）。

.DESCRIPTION
    机制：
      * 以「条目」为单位做基线，而不是整文件 hash —— 追加新条目只报新增，
        改老条目只报那条，不会被无关改动淹没。
      * 基线存 state/board-state.json，可用 -ResetBaseline 重新取样。
      * 哈希前会剥掉 `> 🤖` 开头的行（本代理自己写的 triage 批注），
        避免"我写批注 → 自己被自己叫醒"的回环。
      * 检出变化后：写 state、落 inbox/pending-<n>.md、print 到 stdout、exit 0。

.EXAMPLE
    pwsh -NoProfile -File watch-tutanban.ps1 -ResetBaseline
    pwsh -NoProfile -File watch-tutanban.ps1          # 阻塞等待新条目
#>
[CmdletBinding()]
param(
    [string] $Board = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\mcp-吐槽板.md',
    [string] $WorkDir = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\issue-triage',
    [int]    $PollSeconds = 5,
    [int]    $MaxMinutes = 0,        # 0 = 无限等待
    [switch] $ResetBaseline,         # 只重新取样基线然后退出
    [switch] $Quiet                  # 取样基线时不打印报告
)

$ErrorActionPreference = 'Stop'
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$StateFile = Join-Path $WorkDir 'state\board-state.json'
$InboxDir  = Join-Path $WorkDir 'inbox'
$HistFile  = Join-Path $WorkDir 'state\history.log'

New-Item -ItemType Directory -Force -Path (Split-Path $StateFile), $InboxDir | Out-Null

function Write-Utf8([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, $Utf8NoBom)
}

# ── 把吐槽板切成「条目」：以行首 "## " 为界 ───────────────────────────────
function Get-BoardEntries([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $text = [System.IO.File]::ReadAllText($Path, $Utf8NoBom)
    $lines = $text -split "`r?`n"

    $entries = [ordered]@{}
    $key = $null; $buf = [System.Collections.Generic.List[string]]::new()

    foreach ($ln in $lines) {
        if ($ln -match '^##\s+(.*\S)\s*$') {
            if ($null -ne $key) { $entries[$key] = ($buf -join "`n") }
            $head = $Matches[1]
            # "[15] xxx" → "15"；无编号的标题（"值得表扬的部分…"）原样用
            $key = if ($head -match '^\[(\d+)\]') { $Matches[1] } else { $head }
            $buf = [System.Collections.Generic.List[string]]::new()
            [void]$buf.Add($ln)
        }
        elseif ($null -ne $key) { [void]$buf.Add($ln) }
    }
    if ($null -ne $key) { $entries[$key] = ($buf -join "`n") }
    return $entries
}

function Get-EntryHash([string]$Body) {
    # 剥掉本代理自己写的批注**块**（`> 🤖` 标记行 + 其后所有 `>` 续行），再归一化空白哈希。
    # 注意：批注常写成多行引用块，只认标记行会让主代理把自己的批注当成"条目被改动"而自唤醒。
    $keep = [System.Collections.Generic.List[string]]::new()
    $inNote = $false        # 条目内的 `> 🤖` 引用块
    $skipSection = $false   # 整节跳过：`## 🤖 Triage 台账` 等本代理自有小节
    foreach ($ln in ($Body -split "`r?`n")) {
        if ($ln -match '^\s*##\s') {
            # 新标题：结束上一段；带 🤖 的标题开一节"整节跳过"，直到下一个标题
            $skipSection = ($ln -match '^\s*##\s*🤖')
            if (-not $skipSection) { [void]$keep.Add($ln.TrimEnd()) }
            continue
        }
        if ($skipSection) { continue }
        if ($ln -match '^\s*>\s*🤖') { $inNote = $true; continue }
        if ($inNote) {
            if ($ln -match '^\s*>\s*') { continue }   # 批注续行
            $inNote = $false
        }
        [void]$keep.Add($ln.TrimEnd())
    }
    $norm = (($keep -join "`n") -replace '\s+', ' ').Trim()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($norm))
    return ([System.BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 16)
}

function Save-State($Entries) {
    $obj = [ordered]@{
        board        = $Board
        capturedAt   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        entryHashes  = [ordered]@{}
    }
    foreach ($k in $Entries.Keys) { $obj.entryHashes[$k] = Get-EntryHash $Entries[$k] }
    Write-Utf8 $StateFile (($obj | ConvertTo-Json -Depth 6))
}

function Load-State {
    if (-not (Test-Path -LiteralPath $StateFile)) { return $null }
    try { return (Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json) }
    catch { return $null }
}

function Get-EntryLabel([string]$Key, [string]$Body) {
    $first = ($Body -split "`r?`n")[0]
    return ("{0}  {1}" -f $Key, ($first -replace '^#+\s*', '').Trim())
}

# ── 基线 ────────────────────────────────────────────────────────────────
$entries = Get-BoardEntries $Board
if ($entries.Count -eq 0) { Write-Error "吐槽板解析不到任何条目：$Board"; exit 2 }

if ($ResetBaseline) {
    Save-State $entries
    if (-not $Quiet) {
        Write-Host "[baseline] 已重新取样：$($entries.Count) 条 → $StateFile"
    }
    exit 0
}

$state = Load-State
if ($null -eq $state) {
    Save-State $entries
    Write-Host "[baseline] 首次运行，已取样 $($entries.Count) 条；接下来进入等待。"
}
else {
    $known = @{}
    foreach ($p in $state.entryHashes.PSObject.Properties) { $known[$p.Name] = [string]$p.Value }
    $newKeys   = @($entries.Keys | Where-Object { -not $known.ContainsKey($_) })
    $changed   = @($entries.Keys | Where-Object { $known.ContainsKey($_) -and $known[$_] -ne (Get-EntryHash $entries[$_]) })
    if ($newKeys.Count -gt 0 -or $changed.Count -gt 0) {
        # 消费掉：写新基线，避免同一批反复触发
        Save-State $entries
        $seq = 1
        if (Test-Path -LiteralPath $HistFile) {
            $seq = ([int]((Get-Content -LiteralPath $HistFile | Measure-Object).Count)) + 1
        }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $out   = Join-Path $InboxDir "pending-$stamp.md"

        $sb = [System.Text.StringBuilder]::new()
        [void]$sb.AppendLine("# 吐槽板变化 #$seq — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("- 新增条目: $(if($newKeys.Count){$newKeys -join ', '}else{'（无）'})")
        [void]$sb.AppendLine("- 被改动条目: $(if($changed.Count){$changed -join ', '}else{'（无）'})")
        [void]$sb.AppendLine()
        foreach ($k in @($changed + $newKeys)) {
            $tag = if ($newKeys -contains $k) { '新增' } else { '改动' }
            [void]$sb.AppendLine("## [$tag] $(Get-EntryLabel $k $entries[$k])")
            [void]$sb.AppendLine()
            [void]$sb.AppendLine($entries[$k])
            [void]$sb.AppendLine()
        }
        Write-Utf8 $out $sb.ToString()
        Add-Content -LiteralPath $HistFile -Encoding UTF8 -Value ("#{0}`t{1}`tnew=[{2}]`tchanged=[{3}]`t-> {4}" -f $seq, $stamp, ($newKeys -join ','), ($changed -join ','), $out)

        Write-Host "[changed] 检出 $($newKeys.Count) 新增 / $($changed.Count) 改动 → $out"
        Write-Host $sb.ToString()
        exit 0
    }
    # 无变化：留着旧基线不动
}

# ── 等待循环（后台阻塞运行，退出即唤醒主代理）────────────────────────────
$deadline = if ($MaxMinutes -gt 0) { (Get-Date).AddMinutes($MaxMinutes) } else { [datetime]::MaxValue }
Write-Host "[watching] $Board  (每 ${PollSeconds}s 轮询, 无限期等待新条目 — 等这个作业结束通知即可续跑)"
$lastMtime = (Get-Item -LiteralPath $Board).LastWriteTimeUtc

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $PollSeconds
    try { $item = Get-Item -LiteralPath $Board -ErrorAction Stop } catch { continue }
    if ($item.LastWriteTimeUtc -eq $lastMtime) { continue }
    $lastMtime = $item.LastWriteTimeUtc

    $entries = Get-BoardEntries $Board
    $known = @{}
    foreach ($p in $state.entryHashes.PSObject.Properties) { $known[$p.Name] = [string]$p.Value }
    $newKeys = @($entries.Keys | Where-Object { -not $known.ContainsKey($_) })
    $changed = @($entries.Keys | Where-Object { $known.ContainsKey($_) -and $known[$_] -ne (Get-EntryHash $entries[$_]) })
    if ($newKeys.Count -eq 0 -and $changed.Count -eq 0) { continue }   # 纯 🤖 批注，自忽略

    Save-State $entries
    $seq = 1
    if (Test-Path -LiteralPath $HistFile) { $seq = ([int]((Get-Content -LiteralPath $HistFile | Measure-Object).Count)) + 1 }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $out   = Join-Path $InboxDir "pending-$stamp.md"

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# 吐槽板变化 #$seq — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- 新增条目: $(if($newKeys.Count){$newKeys -join ', '}else{'（无）'})")
    [void]$sb.AppendLine("- 被改动条目: $(if($changed.Count){$changed -join ', '}else{'（无）'})")
    [void]$sb.AppendLine()
    foreach ($k in @($changed + $newKeys)) {
        $tag = if ($newKeys -contains $k) { '新增' } else { '改动' }
        [void]$sb.AppendLine("## [$tag] $(Get-EntryLabel $k $entries[$k])")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine($entries[$k])
        [void]$sb.AppendLine()
    }
    Write-Utf8 $out $sb.ToString()
    Add-Content -LiteralPath $HistFile -Encoding UTF8 -Value ("#{0}`t{1}`tnew=[{2}]`tchanged=[{3}]`t-> {4}" -f $seq, $stamp, ($newKeys -join ','), ($changed -join ','), $out)

    Write-Host "[changed] 检出 $($newKeys.Count) 新增 / $($changed.Count) 改动 → $out"
    Write-Host $sb.ToString()
    exit 0
}

Write-Host "[timeout] $MaxMinutes 分钟内无变化，退出以便重新挂载。"
exit 0
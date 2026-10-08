<#
.SYNOPSIS
    把 ~/.dsh/skills 下的技能 SKILL.md 拷进 docs/issue-triage/skills/，
    供子代理用 read 工具"手动注入"（子代理拿不到 skill 工具）。

.DESCRIPTION
    每次派子代理前跑一次，保证子代理读到的是**当时的快照**，
    不会因为磁盘上技能文件中途被改而读到半新半旧的内容。
    只写 docs/issue-triage/skills/ 目录，绝不碰别处。
#>
[CmdletBinding()]
param(
    [string] $Source = 'C:\Users\Timer_0\.dsh\skills',
    [string] $Dest   = 'C:\SteamLibrary\steamapps\common\RimWorld\Mods\UESDdebuger\docs\issue-triage\skills',
    [string[]] $Only  = @(
        'using-superpowers',
        'systematic-debugging',
        'brainstorming',
        'writing-plans',
        'chinese-documentation'
    )
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Dest | Out-Null

foreach ($s in $Only) {
    $src = Join-Path $Source $s
    if (-not (Test-Path -LiteralPath $src)) {
        Write-Warning "跳过（找不到技能）：$s"
        continue
    }
    $dst = Join-Path $Dest $s
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    # 只拷 SKILL.md + 其 references/ 下的文档（*.md），够子代理读
    $files = @(Get-ChildItem -LiteralPath $src -Filter '*.md' -File -ErrorAction SilentlyContinue)
    if (Test-Path -LiteralPath (Join-Path $src 'references')) {
        $files += @(Get-ChildItem -LiteralPath (Join-Path $src 'references') -Filter '*.md' -File -Recurse -ErrorAction SilentlyContinue)
    }
    $n = 0
    foreach ($f in $files) {
        $target = Join-Path $dst ($f.FullName.Substring($src.Length).TrimStart('\'))
        $parent = Split-Path $target -Parent
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        Copy-Item -LiteralPath $f.FullName -Destination $target -Force
        $n++
    }
    Write-Host "[inject] $s -> $n 个文件"
}
Write-Host "[inject] 完成，目标目录：$Dest"
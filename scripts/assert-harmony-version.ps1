<#
.SYNOPSIS
    Build-time guard: vendor 0Harmony.dll version must equal the brrainz runtime version.

.DESCRIPTION
    Mono assembly binding is an EXACT version match. The 0Harmony, Version=X written
    into the typeref at compile time must resolve to exactly X at runtime, otherwise
    field signature resolution fails with TypeLoadException:

        Could not load type of field 'UnityExplorer.ExplorerCore:<Harmony>k__BackingField'
        due to: Could not resolve type with token 01000011 from typeref
        (expected class 'HarmonyLib.Harmony' in assembly
         '0Harmony, Version=2.3.3.0, Culture=neutral, PublicKeyToken=null')

    The trigger is Verse.StaticConstructorOnStartupUtility.ReportProbablyMissingAttributes:
    it calls GetFields() on every mod type WITHOUT filtering BindingFlags, so a single
    unresolvable Harmony field signature fails the whole batch -- and it happens in the
    post-long-event phase, which looks unrelated to this mod.

    Hence this constraint is hard, and must be enforced at BUILD time.

.PARAMETER VendorAssembly
    Source\UELoader\lib\0Harmony.dll (compile-time reference copy)

.PARAMETER ExpectedVersion
    brrainz runtime version, e.g. 2.4.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $VendorAssembly,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedVersion
)

$ErrorActionPreference = 'Stop'

function Fail([string] $message) {
    Write-Host ''
    Write-Host ('[FAIL] ' + $message) -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $VendorAssembly)) {
    Fail ('vendor 0Harmony copy missing: ' + $VendorAssembly)
}

try {
    $an = [System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path -LiteralPath $VendorAssembly).Path)
} catch {
    Fail ('cannot read 0Harmony identity: ' + $_.Exception.Message)
}

if ($an.Name -ne '0Harmony') {
    Fail ('assembly name is not 0Harmony but ' + $an.Name + '. The UE-bundled HarmonyX 2.5.2 cannot be used here.')
}

$actual = $an.Version.ToString()
if ($actual -ne $ExpectedVersion) {
    $msg  = 'vendor 0Harmony version ' + $actual + ' != expected ' + $ExpectedVersion + '.'
    $msg += [Environment]::NewLine + 'This makes the game throw TypeLoadException on startup (field signature resolution),'
    $msg += [Environment]::NewLine + 'reported in the post-long-event phase and looking unrelated to this mod. Sync the vendor copy:'
    $msg += [Environment]::NewLine + '    source: Steam Workshop brrainz.harmony (2009463077), Current\Assemblies\0Harmony.dll'
    $msg += [Environment]::NewLine + '    e.g.   Mods\2009463077\Current\Assemblies\0Harmony.dll'
    $msg += [Environment]::NewLine + 'Then rebuild. The vendor copy is compile-time only and is not shipped.'
    Fail $msg
}

Write-Host ('[OK] 0Harmony version ' + $actual + ' matches brrainz runtime surface')
exit 0

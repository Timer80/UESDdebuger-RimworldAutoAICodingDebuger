# ----------- MelonLoader IL2CPP (net6) -----------
dotnet build src/UnityExplorer.sln -c Release_ML_Cpp_net6
$Path = "Release\UnityExplorer.MelonLoader.IL2CPP.net6preview"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net6 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.ML.IL2CPP.net6preview.dll $Path/UnityExplorer.ML.IL2CPP.net6preview.dll $Path/mcs.dll 
# (cleanup and move files)
Remove-Item $Path/UnityExplorer.ML.IL2CPP.net6preview.deps.json
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
New-Item -Path "$Path" -Name "Mods" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.ML.IL2CPP.net6preview.dll -Destination $Path/Mods -Force
New-Item -Path "$Path" -Name "UserLibs" -ItemType "directory" -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Unhollower.dll -Destination $Path/UserLibs -Force
# (create zip archive)
Remove-Item $Path/../UnityExplorer.MelonLoader.IL2CPP.net6preview.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.MelonLoader.IL2CPP.net6preview.zip .\$Path\*

# ----------- MelonLoader IL2CPP (net472) -----------
dotnet build src/UnityExplorer.sln -c Release_ML_Cpp_net472
$Path = "Release/UnityExplorer.MelonLoader.IL2CPP"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/net35 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.ML.IL2CPP.dll $Path/UnityExplorer.ML.IL2CPP.dll $Path/mcs.dll 
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
New-Item -Path "$Path" -Name "Mods" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.ML.IL2CPP.dll -Destination $Path/Mods -Force
New-Item -Path "$Path" -Name "UserLibs" -ItemType "directory" -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Unhollower.dll -Destination $Path/UserLibs -Force
# (create zip archive)
Remove-Item $Path/../UnityExplorer.MelonLoader.IL2CPP.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.MelonLoader.IL2CPP.zip .\$Path\*

# ----------- MelonLoader Mono -----------
dotnet build src/UnityExplorer.sln -c Release_ML_Mono
$Path = "Release/UnityExplorer.MelonLoader.Mono"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.ML.Mono.dll $Path/UnityExplorer.ML.Mono.dll $Path/mcs.dll 
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
New-Item -Path "$Path" -Name "Mods" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.ML.Mono.dll -Destination $Path/Mods -Force
New-Item -Path "$Path" -Name "UserLibs" -ItemType "directory" -Force
Move-Item -Path $Path/UniverseLib.Mono.dll -Destination $Path/UserLibs -Force
# (create zip archive)
Remove-Item $Path/../UnityExplorer.MelonLoader.Mono.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.MelonLoader.Mono.zip .\$Path\*

# ----------- BepInEx IL2CPP -----------
dotnet build src/UnityExplorer.sln -c Release_BIE_Cpp
$Path = "Release/UnityExplorer.BepInEx.IL2CPP"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.BIE.IL2CPP.dll $Path/UnityExplorer.BIE.IL2CPP.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE.IL2CPP.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Unhollower.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
Remove-Item $Path/../UnityExplorer.BepInEx.IL2CPP.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.BepInEx.IL2CPP.zip .\$Path\*

# ----------- BepInEx IL2CPP CoreCLR -----------
dotnet build src/UnityExplorer.sln -c Release_BIE_CoreCLR
$Path = "Release/UnityExplorer.BepInEx.IL2CPP.CoreCLR"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/net6/ /lib:lib/interop/ /lib:$Path /internalize /out:$Path/UnityExplorer.BIE.IL2CPP.CoreCLR.dll $Path/UnityExplorer.BIE.IL2CPP.CoreCLR.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/Il2CppInterop.Common.dll
Remove-Item $Path/Il2CppInterop.Runtime.dll
Remove-Item $Path/Microsoft.Extensions.Logging.Abstractions.dll
Remove-Item $Path/UnityExplorer.BIE.IL2CPP.CoreCLR.deps.json
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE.IL2CPP.CoreCLR.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.IL2CPP.Interop.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
Remove-Item $Path/../UnityExplorer.BepInEx.IL2CPP.CoreCLR.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.BepInEx.IL2CPP.CoreCLR.zip .\$Path\*

# ----------- BepInEx 5 Mono -----------
dotnet build src/UnityExplorer.sln -c Release_BIE5_Mono
$Path = "Release/UnityExplorer.BepInEx5.Mono"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.BIE5.Mono.dll $Path/UnityExplorer.BIE5.Mono.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE5.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
Remove-Item $Path/../UnityExplorer.BepInEx5.Mono.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.BepInEx5.Mono.zip .\$Path\*

# ----------- BepInEx 6 Mono -----------
dotnet build src/UnityExplorer.sln -c Release_BIE6_Mono
$Path = "Release/UnityExplorer.BepInEx6.Mono"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$Path /internalize /out:$Path/UnityExplorer.BIE6.Mono.dll $Path/UnityExplorer.BIE6.Mono.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
New-Item -Path "$Path" -Name "plugins" -ItemType "directory" -Force
New-Item -Path "$Path" -Name "plugins/sinai-dev-UnityExplorer" -ItemType "directory" -Force
Move-Item -Path $Path/UnityExplorer.BIE6.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
Move-Item -Path $Path/UniverseLib.Mono.dll -Destination $Path/plugins/sinai-dev-UnityExplorer -Force
# (create zip archive)
Remove-Item $Path/../UnityExplorer.BepInEx6.Mono.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.BepInEx6.Mono.zip .\$Path\*

# ----------- Standalone Mono -----------
dotnet build src/UnityExplorer.sln -c Release_STANDALONE_Mono
$Path = "Release/UnityExplorer.Standalone.Mono"
# 游戏本体 Managed 目录：本交付面（RimWorld mod）的 UE 程序集自 Roslyn 壳类起**首次**带
# Assembly-CSharp 引用（RoslynPreScanLoader 的 [StaticConstructorOnStartup] 来自 Verse）——
# ILRepack 只带 /lib:lib/net35 时解析不了该引用并 exit 1：
#   Mono.Cecil.AssemblyResolutionException: Failed to resolve 'Assembly-CSharp, Version=1.6.…'
# 故把本体 Managed 目录一并作为解析目录（沿用 Directory.Build.props 的路径口径：
# 环境变量 RIMWORLD_GAME_DIR > 脚本位置向上 5 级的同级默认位置）。
$RimWorldManaged = if ($env:RIMWORLD_GAME_DIR) {
    Join-Path $env:RIMWORLD_GAME_DIR 'RimWorldWin64_Data\Managed'
} else {
    Join-Path (Resolve-Path "$PSScriptRoot\..\..\..\..\..").Path 'RimWorldWin64_Data\Managed'
}
if (-not (Test-Path (Join-Path $RimWorldManaged 'Assembly-CSharp.dll'))) {
    throw "找不到本体程序集目录：$RimWorldManaged（Assembly-CSharp.dll 缺失）。请设 `$env:RIMWORLD_GAME_DIR。"
}
# ILRepack
# ⚠️ 产物名用本仓库 csproj 的 AssemblyName（UnityExplorer.STANDALONE.Mono.dll）：官方脚本原写
# UnityExplorer.Standalone.Mono.dll，在本仓库不存在（$Path 里只有前者）⇒ ILRepack 会因输入缺失失败。
lib/ILRepack.exe /target:library /lib:lib/net35 /lib:$RimWorldManaged /lib:$Path /internalize /out:$Path/UnityExplorer.STANDALONE.Mono.dll $Path/UnityExplorer.STANDALONE.Mono.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/../UnityExplorer.Standalone.Mono.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.Standalone.Mono.zip .\$Path\*

# ----------- Standalone IL2CPP -----------
dotnet build src/UnityExplorer.sln -c Release_STANDALONE_Cpp
$Path = "Release/UnityExplorer.Standalone.IL2CPP"
# ILRepack
lib/ILRepack.exe /target:library /lib:lib/net472 /lib:lib/unhollowed /lib:$Path /internalize /out:$Path/UnityExplorer.Standalone.IL2CPP.dll $Path/UnityExplorer.Standalone.IL2CPP.dll $Path/mcs.dll $Path/Tomlet.dll
# (cleanup and move files)
Remove-Item $Path/Tomlet.dll
Remove-Item $Path/mcs.dll
Remove-Item $Path/Iced.dll
Remove-Item $Path/UnhollowerBaseLib.dll
Remove-Item $Path/../UnityExplorer.Standalone.IL2CPP.zip -ErrorAction SilentlyContinue
7z a $Path/../UnityExplorer.Standalone.IL2CPP.zip .\$Path\*

# ----------- Editor (mono) -----------
$Path1 = "Release/UnityExplorer.Standalone.Mono"
$Path2 = "UnityEditorPackage/Runtime"
Copy-Item $Path1/UnityExplorer.STANDALONE.Mono.dll -Destination $Path2
Copy-Item $Path1/UniverseLib.Mono.dll -Destination $Path2
Remove-Item Release/UnityExplorer.Editor.zip -ErrorAction SilentlyContinue
7z a Release/UnityExplorer.Editor.zip .\UnityEditorPackage\*
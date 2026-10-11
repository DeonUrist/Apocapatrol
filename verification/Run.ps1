param([string]$GameDir = 'E:\SteamLibrary\steamapps\common\Apocalypter', [switch]$UI, [switch]$WarRig)
$ErrorActionPreference = 'Stop'
$taskGame = (Resolve-Path -LiteralPath $GameDir).Path
$taskSource = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskRuntime = Join-Path $PSScriptRoot $(if ($WarRig) { 'runtime-warrig' } else { 'runtime' })
$taskOutput = Join-Path $PSScriptRoot $(if ($WarRig) { 'results-warrig' } else { 'results' })
dotnet build (Join-Path $taskSource 'Apocapatrol.csproj') -c Release -p:DeployToGame=false "-p:GameDir=$taskGame" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Apocapatrol build failed' }
dotnet build (Join-Path $PSScriptRoot 'NativeVerifier.csproj') -c Release "-p:GameDir=$taskGame" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Verifier build failed' }
New-Item -ItemType Directory -Path "$taskRuntime\BepInEx\plugins\Apocapatrol", "$taskRuntime\BepInEx\config", $taskOutput -Force | Out-Null
foreach ($file in @('Apocalypter.exe','UnityPlayer.dll','winhttp.dll','doorstop_config.ini','.doorstop_version')) { Copy-Item -LiteralPath "$taskGame\$file" -Destination $taskRuntime -Force }
Copy-Item -LiteralPath "$taskGame\BepInEx\core" -Destination "$taskRuntime\BepInEx" -Recurse -Force
foreach ($dir in @('Apocalypter_Data','MonoBleedingEdge')) { if (!(Test-Path -LiteralPath "$taskRuntime\$dir")) { New-Item -ItemType Junction -Path "$taskRuntime\$dir" -Target "$taskGame\$dir" | Out-Null } }
Copy-Item -LiteralPath "$taskSource\bin\Release\Apocapatrol.dll" -Destination "$taskRuntime\BepInEx\plugins\Apocapatrol" -Force
Copy-Item -LiteralPath "$taskSource\Textures", "$taskSource\theme" -Destination "$taskRuntime\BepInEx\plugins\Apocapatrol" -Recurse -Force
Copy-Item -LiteralPath "$PSScriptRoot\bin\Release\ApocapatrolTrailerVerifier.dll" -Destination "$taskRuntime\BepInEx\plugins" -Force
if ($WarRig) {
    New-Item -ItemType Directory -Path "$taskRuntime\BepInEx\plugins\WarRig" -Force | Out-Null
    foreach ($file in @('WarRig.dll', 'WarRig.glb', 'WarRig_colliders.glb', 'icon.png')) {
        Copy-Item -LiteralPath "$taskGame\BepInEx\plugins\WarRig\$file" -Destination "$taskRuntime\BepInEx\plugins\WarRig" -Force
    }
    if (Test-Path -LiteralPath "$taskGame\BepInEx\config\com.denis.apocalypter.warrig.cfg") {
        Copy-Item -LiteralPath "$taskGame\BepInEx\config\com.denis.apocalypter.warrig.cfg" -Destination "$taskRuntime\BepInEx\config" -Force
    }
}
@'
[Logging.Console]
Enabled = false
[Logging.Disk]
Enabled = true
LogLevels = All
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\BepInEx.cfg"
@'
[Debug]
VerboseLog = true
'@ | Set-Content -LiteralPath "$taskRuntime\BepInEx\config\com.denis.apocalypter.apocapatrol.cfg"
$taskReport = Join-Path $taskOutput 'report.txt'
if (Test-Path -LiteralPath $taskReport) { Remove-Item -LiteralPath $taskReport }
$taskArgs = @('-logFile', ('"' + $taskRuntime + '\unity.log"'), ('-trailer-output="' + $taskOutput + '"'), ('-trailer-templates="' + $taskSource + '\BaseTemplates"'))
if ($UI) { $taskArgs += @('-trailer-ui','-screen-width','1060','-screen-height','800','-screen-fullscreen','0') }
else { $taskArgs += @('-batchmode','-nographics') }
$taskProcess = Start-Process -FilePath "$taskRuntime\Apocalypter.exe" -WorkingDirectory $taskRuntime -WindowStyle Hidden -ArgumentList $taskArgs -PassThru
try {
    $taskDeadline = (Get-Date).AddMinutes(4)
    while (!$taskProcess.WaitForExit(1000)) { if ((Get-Date) -gt $taskDeadline) { throw 'Native verification timed out' } }
    if (!(Test-Path -LiteralPath $taskReport)) { throw 'Verifier produced no report' }
    Get-Content -LiteralPath $taskReport
    if (!(Select-String -LiteralPath $taskReport -Pattern '^PASS:') -or (Select-String -LiteralPath $taskReport -Pattern '^FAIL:')) { throw 'Native verification failed' }
} finally { if (!$taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id } }

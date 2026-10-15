param([string]$GameDir='E:\SteamLibrary\steamapps\common\Apocalypter')
$ErrorActionPreference='Stop'
$taskMod=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskGame=(Resolve-Path -LiteralPath $GameDir).Path
$taskInstalled=Join-Path $taskGame 'BepInEx\plugins\Apocapatrol'
$taskConfig=Join-Path $taskGame 'BepInEx\config\com.denis.apocalypter.apocapatrol.cfg'
$taskBuilt=Join-Path $taskMod 'bin\Release\Apocapatrol.dll'
$taskVersion=[regex]::Match([IO.File]::ReadAllText((Join-Path $taskMod 'Plugin.cs')),'public const string VERSION = "([^"]+)"').Groups[1].Value
if(!$taskVersion){throw 'Cannot identify build version.'}
foreach($taskReport in @('results-merchant','results-traffic')) {
    $taskReportFile=Join-Path $taskMod "verification\$taskReport\report.txt"
    if(!(Test-Path -LiteralPath $taskReportFile) -or (Select-String -LiteralPath $taskReportFile -Pattern '^FAIL:') -or !(Select-String -LiteralPath $taskReportFile -Pattern '^PASS:')) {throw "Verification incomplete: $taskReport"}
}
$taskBackup=Join-Path $taskGame ("BepInEx\cache\Apocasetter\backup\Apocapatrol-$taskVersion-"+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
foreach($taskName in @('Apocapatrol.dll','README.md','MERCHANTS.md','MerchantLayouts.json','PatrolsAndLoot.json','.merchant-traffic-v1')) {
    $taskPath=Join-Path $taskInstalled $taskName
    if(Test-Path -LiteralPath $taskPath){Copy-Item -LiteralPath $taskPath -Destination $taskBackup}
}
Copy-Item -LiteralPath $taskConfig -Destination $taskBackup
$taskPreserved=@{}
foreach($taskDirectory in @('BaseTemplates','PlayerTemplates','Textures')) {
    foreach($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskInstalled $taskDirectory) -File -Recurse -ErrorAction SilentlyContinue) {$taskPreserved[$taskFile.FullName]=(Get-FileHash -LiteralPath $taskFile.FullName).Hash}
}
$taskDll=Join-Path $taskInstalled 'Apocapatrol.dll'
try {Copy-Item -LiteralPath $taskBuilt -Destination $taskDll -Force}
catch {
    # A loaded assembly can be renamed while Windows refuses overwriting its mapped bytes.
    $taskPrevious=Join-Path $taskBackup 'Apocapatrol.in-use.dll'
    Move-Item -LiteralPath $taskDll -Destination $taskPrevious
    try {Copy-Item -LiteralPath $taskBuilt -Destination $taskDll} catch {Move-Item -LiteralPath $taskPrevious -Destination $taskDll;throw}
}
foreach($taskName in @('README.md','MERCHANTS.md')) {Copy-Item -LiteralPath (Join-Path $taskMod $taskName) -Destination $taskInstalled -Force}
python (Join-Path $PSScriptRoot 'Upgrade-MerchantFiles.py') $taskInstalled
if($LASTEXITCODE -ne 0){throw 'Merchant metadata migration failed; backup retained.'}
foreach($taskPath in $taskPreserved.Keys) {if((Get-FileHash -LiteralPath $taskPath).Hash -ne $taskPreserved[$taskPath]) {throw "Preserved file changed: $taskPath"}}
$taskHash=(Get-FileHash -LiteralPath $taskDll).Hash
if($taskHash -ne (Get-FileHash -LiteralPath $taskBuilt).Hash){throw 'Installed DLL does not match build.'}
$taskEvidence=@("Apocapatrol $taskVersion", "Installed DLL SHA256: $taskHash", "Backup: $taskBackup", "Vehicle templates and textures preserved: $($taskPreserved.Count) files",'Fixtures use native game components with physics simulation disabled; live terrain driving is not benchmarked.')
foreach($taskReport in @('results-merchant','results-traffic')) {
    $taskReportFile=Join-Path $taskMod "verification\$taskReport\report.txt"
    $taskEvidence+="$taskReport PASS checks: $((Select-String -LiteralPath $taskReportFile -Pattern '^PASS:').Count)"
    Copy-Item -LiteralPath $taskReportFile -Destination (Join-Path $taskInstalled "$taskReport-$taskVersion.txt") -Force
}
$taskEvidence | Set-Content -LiteralPath (Join-Path $taskInstalled "VERIFICATION-$taskVersion.txt")
$taskEvidence

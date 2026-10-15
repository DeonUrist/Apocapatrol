param([string]$GameDir='E:\SteamLibrary\steamapps\common\Apocalypter')
$ErrorActionPreference='Stop'
$taskMod=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskRepo=Split-Path -Parent $taskMod
$taskGame=(Resolve-Path -LiteralPath $GameDir).Path
$taskPlugins=Join-Path $taskGame 'BepInEx\plugins'
$taskConfig=Join-Path $taskGame 'BepInEx\config'
function Assert-GameClosed {
    if(Get-CimInstance Win32_Process -Filter "Name='Apocalypter.exe'" | Where-Object {$_.ExecutablePath -eq "$taskGame\Apocalypter.exe"}) {throw 'Close the user game before staging.'}
}
Assert-GameClosed
$taskStamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$taskBackup="$taskGame\BepInEx\cache\Apocasetter\backup\Apocapatrol-2.13.0-$taskStamp"
$taskPayload="$taskRepo\_releases\Apocapatrol-2.13.0-payload-$taskStamp"
$taskDlls=@{
    Apocapatrol="$taskMod\bin\Release\Apocapatrol.dll"
    NPCAI="$taskRepo\npcai\bin\Release\NPCAI.dll"
    ApocapatrolMerchantSpotter="$taskMod\debug\MerchantSpotter\bin\Release\ApocapatrolMerchantSpotter.dll"
}
foreach($taskSource in $taskDlls.Values) {if(!(Test-Path -LiteralPath $taskSource)) {throw "Missing built DLL: $taskSource"}}
New-Item -ItemType Directory -Path $taskBackup,"$taskPayload\plugins","$taskPayload\config" -Force | Out-Null
foreach($taskName in $taskDlls.Keys) {
    New-Item -ItemType Directory -Path "$taskPayload\plugins\$taskName","$taskBackup\$taskName" -Force | Out-Null
    if(Test-Path -LiteralPath "$taskPlugins\$taskName\$taskName.dll") {Copy-Item -LiteralPath "$taskPlugins\$taskName\$taskName.dll" -Destination "$taskBackup\$taskName"}
    Copy-Item -LiteralPath $taskDlls[$taskName] -Destination "$taskPayload\plugins\$taskName"
}
foreach($taskName in @('BaseTemplates','PlayerTemplates')) {
    $taskDir="$taskPlugins\Apocapatrol\$taskName"
    New-Item -ItemType Directory -Path "$taskPayload\plugins\Apocapatrol\$taskName" -Force | Out-Null
    if(Test-Path -LiteralPath $taskDir) {Copy-Item -LiteralPath $taskDir -Destination "$taskBackup\Apocapatrol" -Recurse}
    foreach($taskFile in Get-ChildItem -LiteralPath $taskDir -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        $taskText=[IO.File]::ReadAllText($taskFile.FullName)
        $taskJson=$taskText | ConvertFrom-Json
        if($null -eq $taskJson.PSObject.Properties['faction']) {
            $taskCoyotes=@('Bodyguard','Bolter','Gunnar','Lugger','Mechanic','Merchant','Organic_Mechanic','Pistoleer','Pup')
            $taskPeople=@($taskJson.driver,$taskJson.passenger,$taskJson.rider,$taskJson.rider2,$taskJson.rider3,$taskJson.rider4)
            if($null -ne $taskJson.trailer) {$taskPeople+=@($taskJson.trailer.driver,$taskJson.trailer.passenger,$taskJson.trailer.rider,$taskJson.trailer.rider2,$taskJson.trailer.rider3,$taskJson.trailer.rider4)}
            $taskFaction=if($taskPeople | Where-Object {$_ -in $taskCoyotes}) {'Coyotes'} else {'Scrapyard'}
            $taskText=[regex]::new('\{').Replace($taskText,('{' + [Environment]::NewLine + '    "faction": "'+$taskFaction+'",'),1)
        }
        $null=$taskText | ConvertFrom-Json
        [IO.File]::WriteAllText("$taskPayload\plugins\Apocapatrol\$taskName\$($taskFile.Name)",$taskText)
    }
}
$taskExistingNames=@(Get-ChildItem -LiteralPath "$taskPayload\plugins\Apocapatrol\BaseTemplates","$taskPayload\plugins\Apocapatrol\PlayerTemplates" -Filter '*.json' -File | ForEach-Object {([IO.File]::ReadAllText($_.FullName)|ConvertFrom-Json).name})
foreach($taskFile in Get-ChildItem -LiteralPath "$taskMod\BaseTemplates" -Filter '*.json' -File) {
    $taskName=([IO.File]::ReadAllText($taskFile.FullName)|ConvertFrom-Json).name
    if($taskName -notin $taskExistingNames) {Copy-Item -LiteralPath $taskFile.FullName -Destination "$taskPayload\plugins\Apocapatrol\BaseTemplates"}
}
$taskStatePath="$taskPlugins\Apocapatrol\PatrolsAndLoot.json"
Copy-Item -LiteralPath $taskStatePath -Destination "$taskBackup\Apocapatrol"
$taskState=[IO.File]::ReadAllText($taskStatePath)|ConvertFrom-Json
$taskDefaults=[IO.File]::ReadAllText("$taskMod\EditorDefaults.json")|ConvertFrom-Json
if($null -eq $taskState.PSObject.Properties['merchants']) {$taskState | Add-Member -NotePropertyName merchants -NotePropertyValue @($taskDefaults.merchants)}
if(!(Test-Path -LiteralPath "$taskPlugins\Apocapatrol\.coyote-traffic-v1") -and !($taskState.patrols | Where-Object {$_.id -eq 'coyote-patrol'})) {
    $taskState.patrols=@($taskState.patrols)+@($taskDefaults.patrols | Where-Object {$_.id -eq 'coyote-patrol'})
}
$taskState.schema=3
[IO.File]::WriteAllText("$taskPayload\plugins\Apocapatrol\PatrolsAndLoot.json",($taskState|ConvertTo-Json -Depth 100))
[IO.File]::WriteAllText("$taskPayload\plugins\Apocapatrol\.coyote-traffic-v1",'1')
foreach($taskFile in @('MerchantLayouts.json','.coyote-traffic-v1')) {if(Test-Path -LiteralPath "$taskPlugins\Apocapatrol\$taskFile") {Copy-Item -LiteralPath "$taskPlugins\Apocapatrol\$taskFile" -Destination "$taskBackup\Apocapatrol"}}
$taskCfgPath="$taskConfig\com.denis.apocalypter.apocapatrol.cfg"
Copy-Item -LiteralPath $taskCfgPath -Destination $taskBackup
$taskLines=[Collections.Generic.List[string]]::new()
foreach($taskLine in [IO.File]::ReadAllLines($taskCfgPath)) {
    if($taskLine -match '^\s*(PatrolDaySight|PatrolNightSight|EngineMinRange|EngineMaxRange)\s*=') {
        while($taskLines.Count -gt 0 -and $taskLines[$taskLines.Count-1].StartsWith('#')) {$taskLines.RemoveAt($taskLines.Count-1)}
        continue
    }
    $taskLines.Add($taskLine)
}
$taskCfgText=[string]::Join([Environment]::NewLine,$taskLines).Replace('Apocapatrol v2.12.0','Apocapatrol v2.13.0')
[IO.File]::WriteAllText("$taskPayload\config\com.denis.apocalypter.apocapatrol.cfg",$taskCfgText)
$taskSpotterCfg='com.denis.apocalypter.merchantspotter.cfg'
if(Test-Path -LiteralPath "$taskConfig\$taskSpotterCfg") {Copy-Item -LiteralPath "$taskConfig\$taskSpotterCfg" -Destination $taskBackup;Copy-Item -LiteralPath "$taskConfig\$taskSpotterCfg" -Destination "$taskPayload\config"}
else {[IO.File]::WriteAllText("$taskPayload\config\$taskSpotterCfg",("[Debug]"+[Environment]::NewLine+"Enabled = false"+[Environment]::NewLine+"ToggleKey = NumpadMultiply"+[Environment]::NewLine))}
Copy-Item -LiteralPath "$taskMod\README.md","$taskMod\MERCHANTS.md" -Destination "$taskPayload\plugins\Apocapatrol"
Copy-Item -LiteralPath "$taskMod\MERCHANTS.md" -Destination "$taskPayload\plugins\ApocapatrolMerchantSpotter\README.md"
Copy-Item -LiteralPath "$taskRepo\npcai\README.md" -Destination "$taskPayload\plugins\NPCAI"
$taskReports=@()
foreach($taskKind in @('traffic','merchant','warrig')) {
    $taskReport="$taskMod\verification\results-$taskKind\report.txt"
    if(!(Test-Path -LiteralPath $taskReport)) {throw "Missing verification report: $taskKind"}
    $taskReportLines=[IO.File]::ReadAllLines($taskReport)
    if($taskReportLines | Where-Object {$_ -match '^FAIL:'}) {throw "Failed verification: $taskKind"}
    $taskReports+="$taskKind checks="+@($taskReportLines|Where-Object {$_ -match '^PASS:'}).Count
}
# Recheck before touching the installed files. Texture files are never copied by this script.
Assert-GameClosed
foreach($taskName in $taskDlls.Keys) {New-Item -ItemType Directory -Path "$taskPlugins\$taskName" -Force|Out-Null;Copy-Item -LiteralPath "$taskPayload\plugins\$taskName\$taskName.dll" -Destination "$taskPlugins\$taskName" -Force}
foreach($taskDir in @('BaseTemplates','PlayerTemplates')) {Copy-Item -LiteralPath "$taskPayload\plugins\Apocapatrol\$taskDir" -Destination "$taskPlugins\Apocapatrol" -Recurse -Force}
foreach($taskFile in @('PatrolsAndLoot.json','.coyote-traffic-v1','README.md','MERCHANTS.md')) {Copy-Item -LiteralPath "$taskPayload\plugins\Apocapatrol\$taskFile" -Destination "$taskPlugins\Apocapatrol" -Force}
Copy-Item -LiteralPath "$taskPayload\plugins\ApocapatrolMerchantSpotter\README.md" -Destination "$taskPlugins\ApocapatrolMerchantSpotter" -Force
Copy-Item -LiteralPath "$taskPayload\config\com.denis.apocalypter.apocapatrol.cfg","$taskPayload\config\$taskSpotterCfg" -Destination $taskConfig -Force
$taskEvidence=@('Apocapatrol 2.13.0; NPC Overhaul 1.9.15; Merchant Object Spotter 1.0.0')+$taskReports
foreach($taskName in $taskDlls.Keys) {
    $taskExpected=(Get-FileHash -LiteralPath $taskDlls[$taskName] -Algorithm SHA256).Hash
    $taskActual=(Get-FileHash -LiteralPath "$taskPlugins\$taskName\$taskName.dll" -Algorithm SHA256).Hash
    if($taskExpected -ne $taskActual) {throw "Installed hash differs: $taskName"}
    $taskEvidence+="$taskName SHA256=$taskActual"
}
$taskEvidence+="Backup=$taskBackup"
$taskEvidence+='Native construction, traffic, trading, persistence and popup checks passed. Live driving physics were not exercised by these fixtures.'
$taskEvidence|Set-Content -LiteralPath "$taskPlugins\Apocapatrol\VERIFICATION-2.13.0.txt"
$taskEvidence|Set-Content -LiteralPath "$taskPayload\plugins\Apocapatrol\VERIFICATION-2.13.0.txt"
$taskZip="$taskRepo\_releases\Apocapatrol-2.13.0-$taskStamp.zip"
Compress-Archive -Path "$taskPayload\*" -DestinationPath $taskZip
$taskEvidence
"Archive=$taskZip"

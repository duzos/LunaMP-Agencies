<#
 Packages an Agencies release: builds Release, stages client + server (each with the
 updater helper and build.txt), zips them and writes SHA256SUMS.txt.
 Tag is derived from LmpCommon/Agency/AgenciesBuild.cs (v{UpstreamVersion}-agencies.{Number}).
 Output: Artifacts/Release-<tag>/
#>
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = if ($env:DOTNET) { $env:DOTNET } else { 'C:/Users/james/.dotnet/dotnet.exe' }
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path $dotnet)) { throw "dotnet not found at $dotnet" }
if (-not (Test-Path $sevenZip)) { throw "7z.exe not found at $sevenZip" }

# 1. tag from AgenciesBuild.cs
$src = Get-Content -Raw (Join-Path $repo 'LmpCommon\Agency\AgenciesBuild.cs')
$mNum = [regex]::Match($src, 'const\s+int\s+Number\s*=\s*(\d+)\s*;')
$mVer = [regex]::Match($src, 'const\s+string\s+UpstreamVersion\s*=\s*"([^"]+)"\s*;')
if (-not $mNum.Success -or -not $mVer.Success) { throw 'Could not read Number/UpstreamVersion from AgenciesBuild.cs' }
$number = $mNum.Groups[1].Value
$tag = "v$($mVer.Groups[1].Value)-agencies.$number"
Write-Host "Packaging $tag (build $number)"

# 2. build
foreach ($proj in 'Server\Server.csproj', 'LmpClient\LmpClient.csproj', 'Updater\LmpAgenciesUpdater\LmpAgenciesUpdater.csproj') {
    Write-Host "=== building $proj ==="
    & $dotnet build (Join-Path $repo $proj) -c Release
    if ($LASTEXITCODE -ne 0) { throw "build failed: $proj" }
}

$helperBin = Join-Path $repo 'Updater\LmpAgenciesUpdater\bin\Release\net48'
$helperFiles = 'LmpAgenciesUpdater.exe', 'LmpAgenciesUpdater.exe.config'
foreach ($f in $helperFiles) { if (-not (Test-Path (Join-Path $helperBin $f))) { throw "missing helper output: $f" } }

function Add-Updater([string]$dir) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($f in $helperFiles) { Copy-Item (Join-Path $helperBin $f) $dir -Force }
    # no BOM, no newline: just N
    [System.IO.File]::WriteAllText((Join-Path $dir 'build.txt'), $number, (New-Object System.Text.UTF8Encoding($false)))
}

$out = Join-Path $repo "Artifacts\Release-$tag"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# 3. client
$gd = Join-Path $out 'Client\GameData'
$mod = Join-Path $gd 'LunaMultiplayer'
foreach ($f in 'Plugins', 'Button', 'Localization', 'PartSync', 'Icons', 'Flags') { New-Item -ItemType Directory -Force -Path (Join-Path $mod $f) | Out-Null }
Copy-Item (Join-Path $repo 'External\Dependencies\Harmony\*') $gd -Recurse -Force
Get-ChildItem (Join-Path $repo 'LmpClient\bin\Release') -File | Copy-Item -Destination (Join-Path $mod 'Plugins') -Force
Copy-Item (Join-Path $repo 'LunaMultiplayer.version') (Join-Path $mod 'LunaMultiplayer.version') -Force
Get-ChildItem (Join-Path $repo 'LmpClient\Resources') -File -Filter *.png | Copy-Item -Destination (Join-Path $mod 'Button') -Force
Copy-Item (Join-Path $repo 'LmpClient\Localization\XML\*') (Join-Path $mod 'Localization') -Recurse -Force
Copy-Item (Join-Path $repo 'LmpClient\ModuleStore\XML\*.xml') (Join-Path $mod 'PartSync') -Force
Get-ChildItem (Join-Path $repo 'LmpClient\Resources\Icons') -File | Copy-Item -Destination (Join-Path $mod 'Icons') -Force
Get-ChildItem (Join-Path $repo 'LmpClient\Resources\Flags') -File | Copy-Item -Destination (Join-Path $mod 'Flags') -Force
Add-Updater (Join-Path $mod 'Updater')

# 4. client assertions
& (Join-Path $repo 'Scripts\Assert-ClientPackageLayout.ps1') -GameData $gd
if ($LASTEXITCODE -ne 0) { throw 'client layout check failed' }
$bad = Get-ChildItem $gd -Recurse -File -Filter *.dll | Where-Object { $_.FullName -match '[\/]Updater[\/]' }
if ($bad) { throw "dll found under client Updater/: $($bad.FullName -join ', ')" }

# 5. server
$srv = Join-Path $out 'Server'
New-Item -ItemType Directory -Force -Path $srv | Out-Null
Copy-Item (Join-Path $repo 'Server\bin\Release\net10.0\*') $srv -Recurse -Force
Add-Updater (Join-Path $srv 'Updater')
$protected = 'Universe', 'Config', 'logs', 'Plugins', 'update-staging'
$badTop = Get-ChildItem $srv -Force | Where-Object { $n = $_.Name; $protected | Where-Object { $_ -ieq $n } }
if ($badTop) { throw "server package has protected top-level entries: $($badTop.Name -join ', ')" }
$bad = Get-ChildItem (Join-Path $srv 'Updater') -Recurse -File -Filter *.dll
if ($bad) { throw 'dll found under server Updater/' }

# 6. zip + sums
$clientZip = Join-Path $out "LunaMultiplayer-Agencies-Client-$tag.zip"
$serverZip = Join-Path $out "LunaMultiplayer-Agencies-Server-$tag.zip"
Push-Location (Join-Path $out 'Client')
try { & $sevenZip a -tzip $clientZip 'GameData' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'client zip failed' } } finally { Pop-Location }
Push-Location $srv
try { & $sevenZip a -tzip $serverZip '*' | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'server zip failed' } } finally { Pop-Location }
$lines = Get-FileHash $clientZip, $serverZip -Algorithm SHA256 | ForEach-Object { '{0}  {1}' -f $_.Hash.ToLowerInvariant(), (Split-Path $_.Path -Leaf) }
[System.IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Get-Content (Join-Path $out 'SHA256SUMS.txt')
Write-Host "Output: $out"

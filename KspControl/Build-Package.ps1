<# Creates an offline read-only preview package. Never installs or launches anything. #>
[CmdletBinding()]
param([string]$Dotnet = 'C:/Users/james/.dotnet/dotnet.exe')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $repo
try {
    function Assert-CleanSource {
        $changes = & git status --porcelain --untracked-files=no
        if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Tracked source changes present; commit before packaging.' }
        $untracked = & git ls-files --others --exclude-standard -- KspControl LmpClient
        if ($LASTEXITCODE -ne 0 -or $untracked) { throw 'Untracked control/client source present; classify and commit before packaging.' }
    }
    Assert-CleanSource
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve source commit.' }
    $artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'Artifacts'))
    $output = Join-Path $artifacts ('KspControl-preview-' + $commit.Substring(0,12) + '-' + [Guid]::NewGuid().ToString('N'))
    # Unique output; never delete or replace any previous package.
    if (Test-Path -LiteralPath $output) { throw 'Output already exists.' }
    $stage = Join-Path $output 'package'
    $publish = Join-Path $output 'publish'
    New-Item -ItemType Directory -Path $stage,$publish | Out-Null
    & $Dotnet build KspControl/Bridge/KspControl.Bridge.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Bridge build failed.' }
    & $Dotnet publish KspControl/Host/KspControl.Host.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
    Assert-CleanSource
    if ((& git rev-parse HEAD).Trim() -ne $commit) { throw 'Source commit changed during build; discard this incomplete output.' }
    $bridgeDirectory = Join-Path $stage 'GameData/KspControl/Plugins'
    $hostDirectory = Join-Path $stage 'Host'
    New-Item -ItemType Directory -Path $bridgeDirectory,$hostDirectory | Out-Null
    foreach ($name in 'KspControlBridge.dll','KspControl.Contracts.dll') {
        Copy-Item -LiteralPath (Join-Path $repo "KspControl/Bridge/bin/Release/net472/$name") -Destination $bridgeDirectory
    }
    Copy-Item -LiteralPath (Join-Path $publish 'KspControl.Host.exe') -Destination $hostDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $stage
    $expected = @('GameData/KspControl/Plugins/KspControlBridge.dll','GameData/KspControl/Plugins/KspControl.Contracts.dll','Host/KspControl.Host.exe','README.md')
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')
        if ($relative -notin $expected) { throw "Unexpected packaged file: $relative" }
        [ordered]@{ path=$relative; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes=$_.Length }
    })
    if ($files.Count -ne $expected.Count) { throw 'Missing required package file.' }
    $manifest = [ordered]@{
        schemaVersion=1; version='0.1.0-preview'; sourceCommit=$commit; prerelease=$true
        installed=$false; liveValidated=$false; mode='read_only'; protocolVersion=1
        requirements=@('Windows x64','KSP 1.12 compatible runtime','LunaMP client built from this source commit with ControlObservation.ApiVersion 1','Existing LunaMP Newtonsoft.Json assembly version 13.0.0.0; not bundled in GameData')
        capabilities=@('observation','part_control_descriptors','science_descriptors','configured_snapshots')
        unavailable=@('construction_import','flight_control','science_execution','mutation_authority','screenshots')
        files=$files
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8
    $zip = Join-Path $output 'KspControl-0.1.0-preview-win-x64.zip'
    Compress-Archive -LiteralPath (Join-Path $stage 'GameData'),(Join-Path $stage 'Host'),(Join-Path $stage 'README.md'),(Join-Path $stage 'manifest.json') -DestinationPath $zip
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entries = @($archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\','/') })
        if ($entries.Count -ne 5 -or @($entries | Where-Object { $_ -notin ($expected + 'manifest.json') }).Count -ne 0) { throw 'Archive allowlist verification failed.' }
        foreach ($file in $files) {
            $entry = $archive.Entries | Where-Object { $_.FullName.Replace('\','/') -eq $file.path }
            $stream = $entry.Open()
            try { $actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            if ($actual -ne $file.sha256) { throw "Archive hash mismatch: $($file.path)" }
        }
    } finally { $archive.Dispose() }
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($zip) | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt')
    Assert-CleanSource
    if ((& git rev-parse HEAD).Trim() -ne $commit) { throw 'Source commit changed during packaging; this output is not verified.' }
    Write-Output "Offline preview verified: $zip"
} finally { Pop-Location }



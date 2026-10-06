[CmdletBinding()]
param([Parameter(Mandatory)][string]$KspDirectory, [string]$EnterpriseServicesPath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $repo 'LmpClient/LmpClient.csproj')
$names = @($project.SelectNodes("//*[local-name()='KspRuntime']") | ForEach-Object { $_.Include.Split(';') })
$source = Join-Path $KspDirectory 'Launcher_Data/Managed'
$destination = Join-Path $repo 'External/KSPRuntime'
$sources = @{}
foreach ($name in $names) {
    $path = Join-Path $source $name
    if ($name -eq 'System.EnterpriseServices.dll' -and $EnterpriseServicesPath) { $path = $EnterpriseServicesPath }
    if (!(Test-Path -LiteralPath $path)) { throw "Missing compatible launcher runtime: $path" }
    $version = $null
    if (![version]::TryParse([Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path -LiteralPath $path).Path).FileVersion, [ref]$version)) {
        throw "KSP cannot load ${path}: native FileVersion is absent or invalid. Supply a verified versioned Mono runtime via -EnterpriseServicesPath when applicable."
    }
    $assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $path).Path)
    if ($assembly.GetName().Name -ne [IO.Path]::GetFileNameWithoutExtension($name)) {
        throw "Unexpected assembly identity in $path; expected $name"
    }
    if ($assembly.GetCustomAttributesData().AttributeType.FullName -contains 'System.Runtime.CompilerServices.ReferenceAssemblyAttribute') {
        throw "Cannot deploy reference assembly: $path"
    }
    $sources[$name] = $path
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($name in $names) { Copy-Item -LiteralPath $sources[$name] -Destination $destination -Force }
Write-Output "Prepared $($names.Count) compatible KSP runtime assemblies."

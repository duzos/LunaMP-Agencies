[CmdletBinding()]
param([Parameter(Mandatory)][string]$KspDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content (Join-Path $repo 'LmpClient/LmpClient.csproj')
$names = @($project.SelectNodes("//*[local-name()='KspRuntime']") | ForEach-Object { $_.Include.Split(';') })
$source = Join-Path $KspDirectory 'Launcher_Data/Managed'
$destination = Join-Path $repo 'External/KSPRuntime'
foreach ($name in $names) {
    $path = Join-Path $source $name
    if (!(Test-Path -LiteralPath $path)) { throw "Missing compatible launcher runtime: $path" }
    $assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $path).Path)
    if ($assembly.GetCustomAttributesData().AttributeType.FullName -contains 'System.Runtime.CompilerServices.ReferenceAssemblyAttribute') {
        throw "Cannot deploy reference assembly: $path"
    }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $destination -Force }
Write-Output "Prepared $($names.Count) compatible KSP runtime assemblies."

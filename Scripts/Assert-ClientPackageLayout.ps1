<#
.SYNOPSIS
    Fails when a staged client GameData folder would break KSP's Harmony install check.

.DESCRIPTION
    KSP's HarmonyInstallChecker refuses to start ("Multiple Harmony installations
    detected") when 0Harmony.dll exists anywhere other than GameData\000_Harmony.
    The LmpClient build output contains a Harmony\000_Harmony folder, so copying
    that output recursively into LunaMultiplayer\Plugins produces a broken package.
    Run this against every staged client GameData folder before zipping it.

.PARAMETER GameData
    The staged GameData folder (the one that contains LunaMultiplayer and 000_Harmony).
#>
param(
    [Parameter(Mandatory = $true)][string]$GameData
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $GameData).Path.TrimEnd('\')
$expected = Join-Path $root '000_Harmony\0Harmony.dll'

$copies = @(Get-ChildItem -Path $root -Recurse -File -Filter '0Harmony.dll')
$misplaced = @($copies | Where-Object { $_.FullName -ne $expected })

if ($misplaced.Count -gt 0) {
    Write-Host "[!] Harmony is packaged outside GameData\000_Harmony:" -ForegroundColor Red
    $misplaced | ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Red }
    exit 1
}
if (-not (Test-Path $expected)) {
    Write-Host "[!] GameData\000_Harmony\0Harmony.dll is missing from $root" -ForegroundColor Red
    exit 1
}
Write-Host "[ok] Client package layout: Harmony only in GameData\000_Harmony"
exit 0

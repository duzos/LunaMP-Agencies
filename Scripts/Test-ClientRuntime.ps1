[CmdletBinding()]
param([Parameter(Mandatory)][string]$KspDirectory, [Parameter(Mandatory)][string]$ClientPlugins)
$ErrorActionPreference = 'Stop'
$roots = @($ClientPlugins, (Join-Path $KspDirectory 'KSP_x64_Data/Managed'), (Join-Path $KspDirectory 'GameData/000_Harmony'))
$available = @{}
foreach ($root in $roots) {
    foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.dll' -File) {
        $name = [Reflection.AssemblyName]::GetAssemblyName($file.FullName)
        if (!$available.ContainsKey($name.Name)) { $available[$name.Name] = $file.FullName }
    }
}
$pending = [Collections.Generic.Queue[string]]::new()
foreach ($name in @('LmpClient','LmpCommon','Newtonsoft.Json')) { $pending.Enqueue($name) }
$checked = @{}
$versionDifferences = [Collections.Generic.HashSet[string]]::new()
while ($pending.Count) {
    $name = $pending.Dequeue()
    if ($checked.ContainsKey($name)) { continue }
    if (!$available.ContainsKey($name)) { throw "Missing runtime dependency: $name" }
    $assembly = [Reflection.Assembly]::LoadFile($available[$name])
    if ($name.StartsWith('System.') -and ($assembly.GetCustomAttributesData().AttributeType.FullName -contains 'System.Runtime.CompilerServices.ReferenceAssemblyAttribute')) {
        throw "Reference assembly in client runtime: $name"
    }
    $checked[$name] = $true
    foreach ($reference in $assembly.GetReferencedAssemblies()) {
        if (!$available.ContainsKey($reference.Name)) { throw "$name requires missing runtime dependency $($reference.FullName)" }
        $provided = [Reflection.AssemblyName]::GetAssemblyName($available[$reference.Name])
        $providedToken = [BitConverter]::ToString($provided.GetPublicKeyToken())
        $requestedToken = [BitConverter]::ToString($reference.GetPublicKeyToken())
        if ($providedToken -ne $requestedToken) {
            # Unity's Mono assemblies use several framework public-key identities. Report
            # these explicitly; only an actual Mono smoke run establishes their binding.
            [void]$versionDifferences.Add("$name requests $($reference.FullName); package provides $($provided.FullName)")
        }
        if ($provided.Version -ne $reference.Version) {
            [void]$versionDifferences.Add("$name requests $($reference.Name) $($reference.Version); package provides $($provided.Version)")
        }
        $pending.Enqueue($reference.Name)
    }
}
foreach ($difference in $versionDifferences) { Write-Output "Version binding requires Mono verification: $difference" }
# LmpCommon targets net472's Http4.2; the bundled Unity implementation is4.0.
# Check the specific APIs used by BannedIpsRetriever instead of assuming same-name compatibility.
$http = [Reflection.Assembly]::LoadFile($available['System.Net.Http'])
if (!$http.GetType('System.Net.Http.HttpClientHandler').GetProperty('ServerCertificateCustomValidationCallback') -or
    !$http.GetType('System.Net.Http.HttpClient').GetMethod('GetStreamAsync', [type[]]@([string]))) {
    throw 'Bundled Http implementation lacks the APIs used by BannedIpsRetriever'
}
Write-Output "Client runtime dependency closure and Http member checks passed ($($checked.Count) assemblies). Version binding and gameplay still require KSP verification."



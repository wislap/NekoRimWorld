param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$GameManagedDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$buildRoot = (Resolve-Path -LiteralPath $BuildDirectory).Path
$managedRoot = (Resolve-Path -LiteralPath $GameManagedDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new output directory; referenced native contract evidence must not be overwritten.' }
$dll = Join-Path $buildRoot 'NekoRimWorld.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'The isolated Mod build is missing.' }
# Loading types and invoking static directory builders does not construct a
# Verse.Game, initialize a map, start Unity or attach the bridge runtime.
[Reflection.Assembly]::LoadFrom((Join-Path $managedRoot 'UnityEngine.CoreModule.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $managedRoot 'Assembly-CSharp.dll')) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom($dll)
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
foreach ($descriptor in @(
    @('WriteCatalog', 'Describe', 'write-catalog.json'),
    @('ColonyManagementContract', 'Catalog', 'colony-management.json'),
    @('ColonyReadContract', 'Catalog', 'colony-state.json'),
    @('NativeAreaManualHookSupport', 'Describe', 'manual-area.json'),
    @('ColonyObservationContract', 'Catalog', 'colony-observation.json')
)) {
    $type = $assembly.GetType('Neko.RimWorld.' + $descriptor[0], $true)
    $method = $type.GetMethod($descriptor[1], [Reflection.BindingFlags]'NonPublic,Static')
    $directory = $method.Invoke($null, @())
    [IO.File]::WriteAllText((Join-Path $outputRoot $descriptor[2]), $directory.ToString() + [Environment]::NewLine, $utf8)
}
$manifest = [ordered]@{
    format = 'native_contract_export.v1'
    assembly_version = $assembly.GetName().Version.ToString()
    assembly_path = $dll
    assembly_sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    game_assembly_sha256 = (Get-FileHash -LiteralPath (Join-Path $managedRoot 'Assembly-CSharp.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    generated_utc = [DateTime]::UtcNow.ToString('o')
    execution = 'static catalog metadata only; no game initialization'
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5) + [Environment]::NewLine, $utf8)
Write-Output $outputRoot

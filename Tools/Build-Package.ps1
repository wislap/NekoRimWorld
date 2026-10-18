#Requires -Version 7.2
<#
.SYNOPSIS
Build and verify an installable RimWorld Mod ZIP without deploying it.
.PARAMETER OutputDirectory
Parent directory for a new NekoRimWorld-VERSION output directory. Existing
version directories are never overwritten. Build failures retain their logs.
#>
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/packages'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-Utf8([string]$Path, [string]$Value) {
    [IO.File]::WriteAllText($Path, $Value.Replace("`r`n", "`n") + "`n", $utf8)
}

function Copy-Tree([string]$Source, [string]$Target) {
    New-Item -ItemType Directory -Path $Target -ErrorAction Stop | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        if ($item.Name -in @('bin', 'obj', '.git', '.vs')) { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links are not package inputs: $($item.FullName)" }
        $destination = Join-Path $Target $item.Name
        if ($item.PSIsContainer) { Copy-Tree $item.FullName $destination }
        else { Copy-Item -LiteralPath $item.FullName -Destination $destination }
    }
}

function Invoke-DotNet([string]$LogName, [string[]]$Arguments) {
    $log = Join-Path $bundle $LogName
    & dotnet @Arguments *> $log
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE). See $log" }
}

function Read-Language([string]$Path) {
    [xml]$xml = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    if ($xml.DocumentElement.LocalName -ne 'LanguageData') { throw "Invalid language root: $Path" }
    $keys = [ordered]@{}
    foreach ($node in $xml.DocumentElement.ChildNodes) {
        if ($node.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
        if ($keys.Contains($node.Name) -or [string]::IsNullOrWhiteSpace($node.InnerText)) { throw "Duplicate or empty language key: $($node.Name)" }
        $keys[$node.Name] = $node.InnerText
    }
    return $keys
}

function Assert-Languages {
    $english = Read-Language (Join-Path $root 'Languages/English/Keyed/Settings.xml')
    $chinese = Read-Language (Join-Path $root 'Languages/ChineseSimplified/Keyed/Settings.xml')
    if (Compare-Object @($english.Keys) @($chinese.Keys)) { throw 'English and Chinese language keys differ.' }
    foreach ($key in $english.Keys) {
        $left = @([regex]::Matches($english[$key], '\{\d+\}') | ForEach-Object Value | Sort-Object -Unique)
        $right = @([regex]::Matches($chinese[$key], '\{\d+\}') | ForEach-Object Value | Sort-Object -Unique)
        if (($left -join ',') -ne ($right -join ',')) { throw "Translation parameters differ: $key" }
    }
    $ui = Get-Content -LiteralPath (Join-Path $root 'Source/NekoRimWorldMod.cs') -Raw
    foreach ($match in [regex]::Matches($ui, '"(NekoRimWorld_\w+)"\.Translate\(')) {
        if (-not $english.Contains($match.Groups[1].Value)) { throw "Missing UI translation: $($match.Groups[1].Value)" }
    }
    $runtime = Get-Content -LiteralPath (Join-Path $root 'Source/BridgeRuntime.cs') -Raw
    $states = [regex]::Match($runtime, '(?s)enum BridgeConnectionState\s*\{([^}]+)\}').Groups[1].Value
    foreach ($state in ($states -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
        if (-not $english.Contains("NekoRimWorld_State_$state")) { throw "Missing connection-state translation: $state" }
    }
}

function Add-DependencyNotices($Dependencies, $Assets, $Locks) {
    $rows = [Collections.Generic.List[string]]::new()
    $rows.Add('# Included runtime dependencies')
    $rows.Add('')
    $rows.Add('Harmony is installed separately. Game and framework reference assemblies are not included.')
    $rows.Add('Licenses and notices below are copied from the restored NuGet packages, except the pinned GraphQL-Parser upstream license.')
    $rows.Add('')
    $records = @()
    foreach ($dependency in $Dependencies) {
        $id = [string]$dependency.package
        $version = [string]$Locks[$id].resolved
        $library = $Assets.libraries["$id/$version"]
        $package = $null
        foreach ($folder in $Assets.packageFolders.Keys) {
            $candidate = Join-Path $folder $library.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $package = $candidate; break }
        }
        if (-not $package) { throw "Restored package is missing: $id/$version" }
        [xml]$nuspec = Get-Content -LiteralPath (Join-Path $package ($id.ToLowerInvariant() + '.nuspec')) -Raw
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        $licenseUrl = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        $licenseDescription = if ($license) { $license.InnerText } elseif ($licenseUrl) { $licenseUrl.InnerText } else { throw "Missing license metadata: $id" }
        $destination = Join-Path $stage "Licenses/$id"
        New-Item -ItemType Directory -Path $destination | Out-Null
        $notices = @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY-NOTICES)(\.|$)' })
        foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $destination $notice.Name) }
        if ($dependency.Contains('licenseFile')) {
            if ($version -ne $dependency.licenseVersion) { throw "Review the pinned upstream license for $id/$version" }
            Copy-Item -LiteralPath (Join-Path $root $dependency.licenseFile) -Destination (Join-Path $destination 'LICENSE.md')
        } elseif (-not ($notices | Where-Object { $_.Name -match '^LICENSE(\.|$)' })) {
            throw "Package has no bundled license; add a pinned upstream license: $id/$version"
        }
        $rows.Add("## $id $version")
        $rows.Add('')
        $rows.Add("Assembly: $($dependency.assembly)")
        $rows.Add("License: $licenseDescription")
        $rows.Add("License files: Licenses/$id/")
        if ($dependency.Contains('licenseSource')) { $rows.Add("Upstream license source: $($dependency.licenseSource)") }
        $rows.Add('')
        $records += @{ package = $id; version = $version; assembly = $dependency.assembly; nugetContentHash = $Locks[$id].contentHash }
    }
    Write-Utf8 (Join-Path $stage 'THIRD-PARTY-NOTICES.md') ($rows -join "`n")
    return $records
}

Get-Command dotnet -ErrorAction Stop | Out-Null
[xml]$about = Get-Content -LiteralPath (Join-Path $root 'About/About.xml') -Raw -Encoding utf8
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'Source/NekoRimWorld.csproj') -Raw
$version = [string]$about.ModMetaData.modVersion
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne [string]$project.Project.PropertyGroup.Version) { throw 'Mod metadata and project versions must match (X.Y.Z).' }
if ([string]$about.ModMetaData.packageId -ne 'neko.rimworld.bridge') { throw 'Unexpected Mod packageId.' }
if ($about.DocumentElement.LocalName -ne 'ModMetaData' -or [string]::IsNullOrWhiteSpace([string]$about.ModMetaData.name)) { throw 'Expected ModMetaData with a non-empty name.' }
if ([string]$about.ModMetaData.name -match '\(Dev\)') { throw 'Package metadata still uses the development name.' }
$harmony = @($about.ModMetaData.modDependencies.li | Where-Object { $_.packageId -eq 'brrainz.harmony' })
if ($harmony.Count -ne 1 -or -not $harmony[0].displayName -or -not ($harmony[0].SelectSingleNode('downloadUrl') -or $harmony[0].SelectSingleNode('steamWorkshopUrl'))) { throw 'Harmony requires its packageId, displayName and a download or Workshop URL.' }
if ('brrainz.harmony' -notin @($about.ModMetaData.loadAfter.li)) { throw 'The Mod must load after Harmony.' }
$gameVersions = @($about.ModMetaData.supportedVersions.li | ForEach-Object { [string]$_ })
if (($gameVersions -join ',') -ne '1.6') { throw 'Review the package layout before changing supported game versions.' }
[xml]$loadFolders = Get-Content -LiteralPath (Join-Path $root 'LoadFolders.xml') -Raw
if ((@($loadFolders.loadFolders.'v1.6'.li) -join ',') -ne '/,1.6') { throw 'Expected root and 1.6 load folders.' }
Assert-Languages
$dependencies = @(Get-Content -LiteralPath (Join-Path $root 'Tools/RuntimeDependencies.json') -Raw | ConvertFrom-Json -AsHashtable)
if (($dependencies.assembly | Sort-Object -Unique).Count -ne $dependencies.Count) { throw 'Duplicate runtime assembly in package recipe.' }
foreach ($dependency in $dependencies) {
    if ($dependency.assembly -notmatch '^[A-Za-z0-9_.-]+\.dll$' -or $dependency.package -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid runtime dependency name.' }
    if ($dependency.Contains('licenseFile') -and -not (Test-Path -LiteralPath (Join-Path $root $dependency.licenseFile) -PathType Leaf)) { throw "Missing pinned upstream license: $($dependency.licenseFile)" }
}

$output = [IO.Path]::GetFullPath($OutputDirectory)
# Reject reparse-point ancestors before creating an output tree we may clean.
$ancestor = $output
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Output path contains a link: $ancestor" }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = "NekoRimWorld-$version"
$bundle = Join-Path $output $name
New-Item -ItemType Directory -Path $bundle -ErrorAction Stop | Out-Null
$work = Join-Path $bundle '.work'
New-Item -ItemType Directory -Path $work | Out-Null
$stage = Join-Path $work 'stage/NekoRimWorld'
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $source = Join-Path $work 'source'
    Copy-Tree (Join-Path $root 'Source') (Join-Path $source 'Source')
    Copy-Tree (Join-Path $root 'Tests/BridgeSmoke') (Join-Path $source 'Tests/BridgeSmoke')
    Copy-Tree (Join-Path $root 'Tests/fixtures') (Join-Path $source 'Tests/fixtures')
    # Stage only reviewed distributable files, never a whole workspace folder.
    $packageAssets = @('About/About.xml', 'LoadFolders.xml', 'LICENSE', 'README.zh-CN.md', 'README.en.md',
        'Languages/English/Keyed/Settings.xml', 'Languages/ChineseSimplified/Keyed/Settings.xml')
    foreach ($optional in @('About/Preview.png', 'About/ModIcon.png')) {
        if (Test-Path -LiteralPath (Join-Path $root $optional) -PathType Leaf) { $packageAssets += $optional }
    }
    foreach ($relative in $packageAssets) {
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destination
    }
    $inputHashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\', '/')
        $inputHashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    foreach ($relative in $packageAssets + @('Tools/Build-Package.ps1', 'Tools/RuntimeDependencies.json')) {
        $inputHashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $projectPath = Join-Path $source 'Source/NekoRimWorld.csproj'
    $build = Join-Path $work 'build'
    Invoke-DotNet 'restore.log' @('restore', $projectPath, '--locked-mode', '--nologo')
    Invoke-DotNet 'build.log' @('build', $projectPath, '-c', 'Release', '--no-restore', '--nologo', '--disable-build-servers', '-o', $build, "-p:PathMap=$source=/_/src")
    $expected = @('NekoRimWorld.dll') + @($dependencies.assembly)
    $actual = @(Get-ChildItem -LiteralPath $build -Filter '*.dll' -File | ForEach-Object Name)
    if (Compare-Object $expected $actual) { throw 'Runtime DLL list differs from the reviewed package recipe. See build.log and Tools/RuntimeDependencies.json.' }
    $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $build 'NekoRimWorld.dll')).Version
    if ($assemblyVersion.ToString(3) -ne $version) { throw 'Built assembly version differs from Mod metadata.' }

    $smokeProject = Join-Path $source 'Tests/BridgeSmoke/BridgeSmoke.csproj'
    $smokeBuild = Join-Path $work 'smoke'
    Invoke-DotNet 'smoke-restore.log' @('restore', $smokeProject, '--locked-mode', '--nologo', '-p:PlatformTarget=AnyCPU')
    Invoke-DotNet 'smoke-build.log' @('build', $smokeProject, '-c', 'Release', '--no-restore', '--nologo', '--disable-build-servers', '-f', 'net8.0', '-p:PlatformTarget=AnyCPU', '-o', $smokeBuild)
    Invoke-DotNet 'smoke-run.log' @((Join-Path $smokeBuild 'BridgeSmoke.dll'))

    $assemblies = Join-Path $stage '1.6/Assemblies'
    New-Item -ItemType Directory -Path $assemblies | Out-Null
    foreach ($file in $expected) { Copy-Item -LiteralPath (Join-Path $build $file) -Destination (Join-Path $assemblies $file) }
    $assets = Get-Content -LiteralPath (Join-Path $source 'Source/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $locks = (Get-Content -LiteralPath (Join-Path $source 'Source/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable).dependencies['.NETFramework,Version=v4.7.2']
    $dependencyRecords = Add-DependencyNotices $dependencies $assets $locks
    $commit = $null
    $dirty = $null
    if ((Test-Path -LiteralPath (Join-Path $root '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        $commit = (& git -C $root rev-parse HEAD).Trim()
        $dirty = [bool](& git -C $root status --porcelain)
    }
    $provenance = [ordered]@{
        format = 'neko.rimworld.package.v1'; version = $version; packageId = [string]$about.ModMetaData.packageId
        supportedGameVersions = $gameVersions; configuration = 'Release'; targetFramework = 'net472'
        sourceCommit = $commit; sourceWorktreeDirty = $dirty; dotnetSdk = (& dotnet --version).Trim()
        sourceLockSha256 = (Get-FileHash -LiteralPath (Join-Path $source 'Source/packages.lock.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        inputSha256 = $inputHashes
        smokeTests = @{ framework = 'net8.0'; passed = $true }; dependencies = @($dependencyRecords)
    }
    Write-Utf8 (Join-Path $stage 'BUILD.json') ($provenance | ConvertTo-Json -Depth 8)
    $archive = Join-Path $bundle "$name.zip"
    $partial = $archive + '.partial'
    $zip = [IO.Compression.ZipFile]::Open($partial, [IO.Compression.ZipArchiveMode]::Create)
    $files = [ordered]@{}
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName) {
            $relative = 'NekoRimWorld/' + [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]'2000-01-01T00:00:00+00:00'
            $inputStream = [IO.File]::OpenRead($file.FullName)
            try {
                $outputStream = $entry.Open()
                try { $inputStream.CopyTo($outputStream) }
                finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
            $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    } finally { $zip.Dispose() }
    $archiveHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Utf8 (Join-Path $bundle "$name.zip.sha256") "$archiveHash  $name.zip"
    Write-Utf8 (Join-Path $bundle "$name.manifest.json") (@{ build = $provenance; archive = "$name.zip"; archiveSha256 = $archiveHash; files = $files } | ConvertTo-Json -Depth 10)

    # Delete only this invocation's verified work tree; retain output and logs.
    $resolvedWork = [IO.Path]::GetFullPath($work)
    if ($resolvedWork -ne [IO.Path]::GetFullPath((Join-Path $bundle '.work')) -or -not $resolvedWork.StartsWith($bundle + [IO.Path]::DirectorySeparatorChar)) { throw 'Unexpected work directory; cleanup refused.' }
    if ((Get-Item -LiteralPath $resolvedWork -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Work directory became a link; cleanup refused.' }
    if (Get-ChildItem -LiteralPath $resolvedWork -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Work directory contains a link; cleanup refused.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    [IO.File]::Move($partial, $archive)
    Write-Output "Built and verified: $archive"
    Write-Output "SHA256: $archiveHash"
    Write-Output 'No files were deployed and no release was published.'
} catch {
    Write-Warning "Package build did not complete. Logs and work files are retained in $bundle"
    throw
}

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$repoRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $GameRoot).Path)
$artifacts = Join-Path $repoRoot 'artifacts'
$dist = Join-Path $repoRoot 'dist'
$expectedGameAssembly = '56584F8D7E96FDB3716EC00E5FB27238A53CD2E92D6150B8F1F435EAA7453541'

function Assert-RepoPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build output escaped the repository: $full"
    }
}

foreach ($output in @($artifacts, $dist)) {
    Assert-RepoPath $output
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
}

$required = @(
    (Join-Path $root 'GameAssembly.dll'),
    (Join-Path $root 'BepInEx\core\BepInEx.Core.dll'),
    (Join-Path $root 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'),
    (Join-Path $root 'BepInEx\interop\Assembly-CSharp.dll'),
    (Join-Path $root 'BepInEx\interop\UnityEngine.CoreModule.dll')
)
foreach ($path in $required) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required build input was not found: $path"
    }
}

$actualGameAssembly = (Get-FileHash -LiteralPath (Join-Path $root 'GameAssembly.dll') -Algorithm SHA256).Hash
if ($actualGameAssembly -ne $expectedGameAssembly) {
    throw "Unsupported GameAssembly.dll. Expected $expectedGameAssembly; actual $actualGameAssembly."
}

$pluginProject = Join-Path $repoRoot 'src\HeroesRedemption.SafeTeleport\HeroesRedemption.SafeTeleport.csproj'
$liveProject = Join-Path $repoRoot 'src\SafeTeleportLive\SafeTeleportLive.csproj'
$fixtureProject = Join-Path $repoRoot 'tests\SafeTeleportFixture\SafeTeleportFixture.csproj'
$pluginOutput = Join-Path $artifacts 'plugin-build'
$liveOutput = Join-Path $artifacts 'live-build'

& dotnet build $pluginProject --configuration Release --nologo --output $pluginOutput "-p:GameRoot=$root"
if ($LASTEXITCODE -ne 0) { throw "Plugin build failed with exit code $LASTEXITCODE." }

& dotnet publish $liveProject --configuration Release --nologo --output $liveOutput --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Live tool publish failed with exit code $LASTEXITCODE." }

$pluginDll = Join-Path $pluginOutput 'HeroesRedemption.SafeTeleport.dll'
$liveExe = Join-Path $liveOutput 'HeroesRedemption.SafeTeleportLive.exe'
if (!(Test-Path -LiteralPath $pluginDll -PathType Leaf)) { throw "Plugin output is missing: $pluginDll" }
if (!(Test-Path -LiteralPath $liveExe -PathType Leaf)) { throw "Live tool output is missing: $liveExe" }

if (!$SkipTests) {
    & dotnet run --project $fixtureProject --configuration Release -- --baseline
    if ($LASTEXITCODE -ne 0) { throw "Plugin baseline fixture failed with exit code $LASTEXITCODE." }
    & dotnet run --project $fixtureProject --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Plugin modified fixture failed with exit code $LASTEXITCODE." }

    $liveConfig = Join-Path $repoRoot 'packaging\live\safe-teleport-config.json'
    & $liveExe --fixture-baseline "--config=$liveConfig"
    if ($LASTEXITCODE -ne 0) { throw "Live baseline fixture failed with exit code $LASTEXITCODE." }
    & $liveExe --fixture "--config=$liveConfig"
    if ($LASTEXITCODE -ne 0) { throw "Live modified fixture failed with exit code $LASTEXITCODE." }
    & $liveExe --validate "--config=$liveConfig" "--game-root=$root"
    if ($LASTEXITCODE -ne 0) { throw "Live static validation failed with exit code $LASTEXITCODE." }
}

function New-PackageManifest([string]$Directory, [string]$PackageName) {
    $files = @()
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -ne 'manifest.json' | Sort-Object Name) {
        $files += [ordered]@{
            name = $file.Name
            length = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    $manifest = [ordered]@{
        name = $PackageName
        version = '1.0.0'
        targetGameAssemblySha256 = $expectedGameAssembly
        files = $files
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'manifest.json') -Encoding utf8NoBOM
}

$pluginStage = Join-Path $artifacts 'package-bepinex'
$liveStage = Join-Path $artifacts 'package-live'
New-Item -ItemType Directory -Force -Path $pluginStage, $liveStage | Out-Null

Copy-Item -LiteralPath $pluginDll -Destination $pluginStage
foreach ($name in @('Install.ps1', 'Rollback.ps1', 'local.heroesredemption.safeteleport.cfg', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\bepinex\$name") -Destination $pluginStage
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $pluginStage
New-PackageManifest $pluginStage 'HeroesRedemption SafeTeleport BepInEx'

Copy-Item -LiteralPath $liveExe -Destination $liveStage
foreach ($name in @('Start-SafeTeleport.cmd', 'Rollback.ps1', 'safe-teleport-config.json', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\live\$name") -Destination $liveStage
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $liveStage
New-PackageManifest $liveStage 'HeroesRedemption SafeTeleport Live'

$pluginZip = Join-Path $dist 'HeroesRedemption-SafeTeleport-BepInEx.zip'
$liveZip = Join-Path $dist 'HeroesRedemption-SafeTeleport-Live.zip'
Compress-Archive -Path (Join-Path $pluginStage '*') -DestinationPath $pluginZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $liveStage '*') -DestinationPath $liveZip -CompressionLevel Optimal

$sumLines = foreach ($zip in @($pluginZip, $liveZip)) {
    '{0}  dist/{1}' -f (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($zip)
}
$sumLines | Set-Content -LiteralPath (Join-Path $repoRoot 'SHA256SUMS') -Encoding ascii

Write-Output "GAME_ASSEMBLY_SHA256=$actualGameAssembly"
Write-Output "PLUGIN_SHA256=$((Get-FileHash -LiteralPath $pluginDll -Algorithm SHA256).Hash)"
Write-Output "LIVE_EXE_SHA256=$((Get-FileHash -LiteralPath $liveExe -Algorithm SHA256).Hash)"
foreach ($line in $sumLines) { Write-Output "PACKAGE=$line" }
Write-Output 'STATUS=PASS'

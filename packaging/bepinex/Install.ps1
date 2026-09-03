[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot
)

$ErrorActionPreference = 'Stop'
$expectedGameAssembly = '56584F8D7E96FDB3716EC00E5FB27238A53CD2E92D6150B8F1F435EAA7453541'
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $GameRoot).Path)
$gameAssembly = Join-Path $root 'GameAssembly.dll'
$bepInEx = Join-Path $root 'BepInEx'
$sourceDll = Join-Path $PSScriptRoot 'HeroesRedemption.SafeTeleport.dll'
$sourceConfig = Join-Path $PSScriptRoot 'local.heroesredemption.safeteleport.cfg'
$destinationDir = Join-Path $bepInEx 'plugins\HeroesRedemption.SafeTeleport'
$destinationDll = Join-Path $destinationDir 'HeroesRedemption.SafeTeleport.dll'
$configPath = Join-Path $bepInEx 'config\local.heroesredemption.safeteleport.cfg'
$stateDir = Join-Path $bepInEx '.heroesredemption-safeteleport'
$stateFile = Join-Path $stateDir 'install-state.json'

if (!(Test-Path -LiteralPath $gameAssembly -PathType Leaf)) { throw "GameAssembly.dll not found: $gameAssembly" }
if (!(Test-Path -LiteralPath $bepInEx -PathType Container)) { throw "BepInEx folder not found: $bepInEx" }
if (!(Test-Path -LiteralPath $sourceDll -PathType Leaf)) { throw "Plugin payload not found: $sourceDll" }
if (!(Test-Path -LiteralPath $sourceConfig -PathType Leaf)) { throw "Config payload not found: $sourceConfig" }

$actualGameAssembly = (Get-FileHash -LiteralPath $gameAssembly -Algorithm SHA256).Hash
if ($actualGameAssembly -ne $expectedGameAssembly) {
    throw "GameAssembly SHA-256 mismatch at $gameAssembly. Expected $expectedGameAssembly; actual $actualGameAssembly."
}
if (Test-Path -LiteralPath $stateFile) {
    throw "An installation state already exists: $stateFile. Run Rollback.ps1 before installing again."
}

New-Item -ItemType Directory -Force -Path $destinationDir, (Split-Path -Parent $configPath), $stateDir | Out-Null
$backupDll = Join-Path $stateDir 'previous-plugin.dll'
$backupConfig = Join-Path $stateDir 'previous-config.cfg'
$hadPlugin = Test-Path -LiteralPath $destinationDll -PathType Leaf
$hadConfig = Test-Path -LiteralPath $configPath -PathType Leaf
if ($hadPlugin) { Copy-Item -LiteralPath $destinationDll -Destination $backupDll -Force }
if ($hadConfig) { Copy-Item -LiteralPath $configPath -Destination $backupConfig -Force }

Copy-Item -LiteralPath $sourceDll -Destination $destinationDll -Force
if (!$hadConfig) { Copy-Item -LiteralPath $sourceConfig -Destination $configPath -Force }

$installedHash = (Get-FileHash -LiteralPath $destinationDll -Algorithm SHA256).Hash
$payloadHash = (Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash
if ($installedHash -ne $payloadHash) { throw "Installed plugin hash readback mismatch at $destinationDll." }
$configHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash

$state = [ordered]@{
    gameRoot = $root
    destinationDll = $destinationDll
    configPath = $configPath
    installedHash = $installedHash
    installedConfigHash = $configHash
    hadPlugin = $hadPlugin
    hadConfig = $hadConfig
    backupDll = $backupDll
    backupConfig = $backupConfig
}
$state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $stateFile -Encoding utf8NoBOM

Write-Output "INSTALL_DESTINATION=$destinationDll"
Write-Output "INSTALL_SHA256=$installedHash"
Write-Output "CONFIG_DESTINATION=$configPath"
Write-Output "CONFIG_SHA256=$configHash"
Write-Output "PRESERVED_PLUGIN=$hadPlugin"
Write-Output "PRESERVED_CONFIG=$hadConfig"
Write-Output 'STATUS=PASS'

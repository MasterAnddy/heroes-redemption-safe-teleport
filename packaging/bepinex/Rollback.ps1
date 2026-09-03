[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $GameRoot).Path)
$bepInEx = Join-Path $root 'BepInEx'
$stateDir = Join-Path $bepInEx '.heroesredemption-safeteleport'
$stateFile = Join-Path $stateDir 'install-state.json'

if (!(Test-Path -LiteralPath $stateFile -PathType Leaf)) {
    Write-Output 'STATE=ABSENT'
    Write-Output 'STATUS=PASS'
    exit 0
}

$state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
$prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
foreach ($candidate in @($state.destinationDll, $state.configPath, $state.backupDll, $state.backupConfig, $stateFile)) {
    $full = [IO.Path]::GetFullPath([string]$candidate)
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Rollback path is outside the selected game root: $full"
    }
}

if (Test-Path -LiteralPath $state.destinationDll -PathType Leaf) {
    $currentHash = (Get-FileHash -LiteralPath $state.destinationDll -Algorithm SHA256).Hash
    if ($currentHash -ne [string]$state.installedHash) {
        throw "Installed plugin changed after installation: $($state.destinationDll). Current SHA-256: $currentHash"
    }
    Remove-Item -LiteralPath $state.destinationDll -Force
}

if ([bool]$state.hadPlugin) {
    if (!(Test-Path -LiteralPath $state.backupDll -PathType Leaf)) { throw "Plugin backup is missing: $($state.backupDll)" }
    Copy-Item -LiteralPath $state.backupDll -Destination $state.destinationDll -Force
}

if ([bool]$state.hadConfig) {
    if (!(Test-Path -LiteralPath $state.backupConfig -PathType Leaf)) { throw "Config backup is missing: $($state.backupConfig)" }
    Copy-Item -LiteralPath $state.backupConfig -Destination $state.configPath -Force
}
elseif (Test-Path -LiteralPath $state.configPath -PathType Leaf) {
    $currentConfigHash = (Get-FileHash -LiteralPath $state.configPath -Algorithm SHA256).Hash
    if ($currentConfigHash -ne [string]$state.installedConfigHash) {
        throw "Generated config was edited after installation: $($state.configPath). Preserve it manually or restore the installed version before rollback."
    }
    Remove-Item -LiteralPath $state.configPath -Force
}

foreach ($backup in @($state.backupDll, $state.backupConfig)) {
    if (Test-Path -LiteralPath $backup -PathType Leaf) { Remove-Item -LiteralPath $backup -Force }
}
Remove-Item -LiteralPath $stateFile -Force
if ((Test-Path -LiteralPath $stateDir -PathType Container) -and !(Get-ChildItem -LiteralPath $stateDir -Force)) {
    Remove-Item -LiteralPath $stateDir -Force
}

Write-Output "RESTORED_PLUGIN=$([bool]$state.hadPlugin)"
Write-Output "RESTORED_CONFIG=$([bool]$state.hadConfig)"
Write-Output 'STATUS=PASS'

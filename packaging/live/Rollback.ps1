$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$tool = Join-Path $here 'HeroesRedemption.SafeTeleportLive.exe'
$config = Join-Path $here 'safe-teleport-config.json'

if (-not (Test-Path -LiteralPath $tool)) { throw "Missing tool: $tool" }
if (-not (Test-Path -LiteralPath $config)) { throw "Missing config: $config" }

& $tool --restore "--config=$config"
if ($LASTEXITCODE -ne 0) { throw "Rollback helper exited with $LASTEXITCODE" }

# The separately running hotkey host is no longer useful after its entry hook is
# restored. Stop only this package's process name; never stop the game process.
Get-Process -Name 'HeroesRedemption.SafeTeleportLive' -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -ne $PID } |
    Stop-Process -Force

Write-Output 'ROLLBACK_SCRIPT status=PASS gameProcessUntouched=true'


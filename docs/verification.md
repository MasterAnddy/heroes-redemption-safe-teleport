# Verification record

This document records the reproducible build and controlled fixtures for the release packages. The process reads the selected game directory for reference assemblies and target validation. It does not write to game files, save data, or a running game process.

## Test environment and input

- Windows x64
- .NET SDK 8
- BepInEx 6 IL2CPP interop assemblies generated
- Supported `GameAssembly.dll` SHA-256:
  `56584F8D7E96FDB3716EC00E5FB27238A53CD2E92D6150B8F1F435EAA7453541`

Full command:

```powershell
.\Build.ps1 -GameRoot '<game-root>'
```

Expected result: exit code `0`; both projects report `0 warnings / 0 errors`; the final build line is `STATUS=PASS`.

## BepInEx checkpoint policy

Baseline command:

```powershell
dotnet run --project tests/SafeTeleportFixture/SafeTeleportFixture.csproj --configuration Release -- --baseline
```

Relevant output, exit code `0`:

```text
MODE=baseline
OUTPUT=teleported=false destination=none autoCheckpoints=0
CHECKS=2
STATUS=PASS
EXIT_STATUS=0
```

Modified command:

```powershell
dotnet run --project tests/SafeTeleportFixture/SafeTeleportFixture.csproj --configuration Release
```

Relevant output, exit code `0`:

```text
MODE=modified
OUTPUT=autoDestination=(0,0) manualDestination=(7,3) boundedCount=4 nearestBounded=(11.3,2)
SPAWN_FALLBACK=OldPrison(-134.8125,-105.625)
GUARDS=damage:true pauseRecording:true deathRecording:true sceneReset:true playerReset:true postTeleportBlock:true
CHECKS=26
STATUS=PASS
EXIT_STATUS=0
```

This fixture covers the reliability delay, damage/pause/death guards, manual-position preference, scene and player-instance isolation, the brief post-teleport recording block, and the automatic history limit.

## Live hook and rollback fixture

Baseline command:

```powershell
HeroesRedemption.SafeTeleportLive.exe --fixture-baseline --config=safe-teleport-config.json
```

Relevant output, exit code `0`:

```text
FIXTURE mode=baseline input=(12.5,-3.25) original=40534883EC3080796000488BD9
FIXTURE F7=NO_HANDLER F6=NO_HANDLER before=(12.5, -3.25) after=(12.5, -3.25) moved=0
STATUS=PASS EXIT_STATUS=0
```

Modified command:

```powershell
HeroesRedemption.SafeTeleportLive.exe --fixture --config=safe-teleport-config.json
```

Relevant output, exit code `0`:

```text
FIXTURE mode=modified input=(12.5,-3.25) original=40534883EC3080796000488BD9
FIXTURE hookAddress=0x12345601000 codeBytes=352 patch=48B80010604523010000FFE090
FIXTURE F7=(12.5, -3.25) F6=(12.5, -3.25) source=manual-anchor moved=1 velocityAfter=(0,0)
FIXTURE sceneChange=0x22220000 anchorCleared=1 scene=Cemetery verifiedSpawn=(-123.5, -1.125) emergency1=(1, 6) emergency2=(5, 2)
FIXTURE rollback=40534883EC3080796000488BD9 exact=1
STATUS=PASS EXIT_STATUS=0
```

The fixture verifies that the 13-byte entry trampoline is recognizable, a scene change clears the anchor, F6 selects the manual anchor or verified scene spawn, teleporting clears velocity, and rollback restores every entry byte.

## Live static target validation

Command:

```powershell
HeroesRedemption.SafeTeleportLive.exe --validate --config=safe-teleport-config.json --game-root='<game-root>'
```

Relevant output, exit code `0`:

```text
moduleHash=PASS prologue=PASS patchLength=13 hookCodeBytes=352 status=PASS
```

This validation reads the complete `GameAssembly.dll` SHA-256 and the original 13 bytes of `PlayerStats.Update`. The release build does not install a Live hook into a running process.

## Package and privacy checks

Both archives are reopened and enumerated. Each archive contains only this project's minimal runtime files, configuration, documentation, license, manifest, and rollback script. Required checks:

```text
BINARY_PATH_HITS=0
TEXT_PATH_HITS=0
DIST_FORBIDDEN_ENTRIES=0
MANIFEST_FAILURES=0
SHA256SUM_FAILURES=0
PACKAGE_TEXT_NONASCII_HITS=0
MANAGED_USER_STRING_NONASCII_HITS=0
```

The scan checks every packaged text file and all managed metadata user strings in the plugin and Live tool for non-ASCII messages. It also checks package text and binary strings for user-specific workspace paths, user names, temporary directories, and project build directories. The framework-dependent Live executable retains Microsoft's standard apphost provenance path; it does not identify the package builder or user. Packages exclude PDB files, runtime logs, PIDs, live addresses, game binaries, save data, and unrelated modules. The authoritative archive hashes are in [`SHA256SUMS`](../SHA256SUMS).

After extraction, a fixture containing only the target hash and an empty `BepInEx` directory runs the installer and rollback. The installed DLL must match the payload, and rollback must remove the DLL and installation state when no prior files existed. The modified Live fixture also runs from a newly extracted archive. Required output, exit code `0`:

```text
INSTALL_READBACK_STATUS=PASS
RESTORED_PLUGIN=False
RESTORED_CONFIG=False
ROLLBACK_READBACK_STATUS=PASS
FIXTURE_GAME_BINARY_REMOVED=True
REOPEN_INSTALL_ROLLBACK_STATUS=PASS
```

## Rollback boundaries

- The BepInEx package's `Install.ps1` preserves an existing plugin and configuration before replacement. `Rollback.ps1` validates the installed DLL and restores those backups.
- The Live package's F12 handler and `Rollback.ps1` recognize only an entry marked with this tool's `HRTPSAFE` magic value and version. An unknown entry is left unchanged.
- Live entry changes occur only after threads are suspended and no instruction pointer is inside the patch range. After restoring the entry, the allocation is released only when the active-depth counter reaches zero and no thread is executing inside that allocation.

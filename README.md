# Heroes & Redemption Safe Teleport

An offline single-player recovery tool for *Heroes & Redemption*. Press **F6** to return to a reliable position and **F7** to save a manual checkpoint. Teleporting also clears the character's rigidbody velocity so collision forces do not immediately push the character back into the stuck area.

The repository provides two distributions:

| Package | Use case | Requires BepInEx | Hotkeys |
|---|---|---:|---|
| `HeroesRedemption-SafeTeleport-BepInEx.zip` | Persistent use; starts with the game and records reliable checkpoints automatically | Yes | F6, F7 |
| `HeroesRedemption-SafeTeleport-Live.zip` | Immediate recovery when the game is already running | No | F6, F7, F8, F12 |

Use one distribution at a time. Running both would make each tool handle the same F6/F7 key presses. After downloading, verify the package against the repository's [`SHA256SUMS`](SHA256SUMS).

## Supported game build

Both tools validate `GameAssembly.dll` before performing version-sensitive work:

```text
56584F8D7E96FDB3716EC00E5FB27238A53CD2E92D6150B8F1F435EAA7453541
```

A different hash indicates an unsupported game build, so the tool stops before applying version-specific type or function locations.

## Usage

### Persistent BepInEx plugin

1. Install BepInEx 6 IL2CPP and launch the game at least once so it generates `BepInEx/interop`.
2. Extract the BepInEx package.
3. Close the game, then run the following in PowerShell:

```powershell
$GameRoot = Read-Host 'Enter the game directory'
.\Install.ps1 -GameRoot $GameRoot
```

While a map is active, the plugin records positions only when the player is alive, gameplay is unpaused, and the position has passed the configured reliability delay. Changing maps or replacing the player instance clears previous checkpoints.

### Live recovery tool

1. Install the .NET 8 Runtime and extract the Live package.
2. Enter a playable map, then run `Start-SafeTeleport.cmd`.
3. Return to the game and press **F6** to recover. After reaching a confirmed safe location, press **F7** to save it.
4. When finished, press **F12** to restore the entry hook and exit the tool.

The Live tool changes only the running game process's memory; it does not modify the game assembly on disk. F12 restores the original entry bytes before releasing the allocation when no hook invocation remains active. The operating system reclaims any retained allocation when the game exits. If the tool exits unexpectedly while the game is still running, use the package's `Rollback.ps1`.

## Build from source

Requirements: Windows PowerShell, the .NET 8 SDK, and a game directory where BepInEx 6 IL2CPP has already generated its interop assemblies.

```powershell
.\Build.ps1 -GameRoot (Read-Host 'Enter the game directory')
```

The build script:

1. Validates the target game assembly.
2. Builds the persistent plugin and Live tool.
3. Runs the baseline and modified fixtures plus Live static validation.
4. Produces two minimal packages in `dist/` and refreshes `SHA256SUMS`.

The build does not copy or package game binaries. See [`docs/verification.md`](docs/verification.md) for the reproducible test procedure.

## Repository layout

```text
src/HeroesRedemption.SafeTeleport/  BepInEx IL2CPP plugin
src/SafeTeleportLive/               Windows live recovery tool
tests/SafeTeleportFixture/          Unity-independent checkpoint policy fixture
packaging/                           Installation, configuration, and rollback files
dist/                                Release-ready archives
```

## Scope and limitations

- Designed for offline single-player use.
- Does not include game binaries, save data, or third-party frameworks.
- BepInEx checkpoints last only for the current scene and player instance. Live anchors last only while the tool is running.
- F6 is a recovery shortcut rather than pathfinding or collision navigation. For an unknown scene, confirm that any nearby emergency candidate is safe.

*Heroes & Redemption* and its assets belong to their respective rights holders. This project is distributed under the [MIT License](LICENSE) and is not affiliated with the game's developer or publisher.

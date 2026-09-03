# SafeTeleport: persistent BepInEx plugin

This distribution starts with the game and records positions that the player has traversed and survived for the configured reliability delay. Those positions provide recovery checkpoints when the character becomes stuck.

## Install

Requirements: BepInEx 6 IL2CPP must already be installed, and the game must have completed at least one successful launch so `BepInEx/interop` exists.

Open PowerShell in the extracted package directory:

```powershell
$GameRoot = Read-Host 'Enter the game directory'
.\Install.ps1 -GameRoot $GameRoot
```

Start the game after installation completes.

## Hotkeys

- **F6**: Return to the manual checkpoint. If no manual checkpoint exists, return to the nearest reliable automatic checkpoint. If the current run has no reliable history yet, use the verified spawn for a known scene.
- **F7**: Save the current position as the manual checkpoint for this map.

Changing maps or replacing the player instance clears all checkpoints. Teleporting updates both `Transform` and `Rigidbody2D`, then clears rigidbody velocity.

The configuration file is located at:

```text
BepInEx/config/local.heroesredemption.safeteleport.cfg
```

## Rollback

Close the game, then run:

```powershell
$GameRoot = Read-Host 'Enter the game directory'
.\Rollback.ps1 -GameRoot $GameRoot
```

The rollback script validates the installed DLL and restores the plugin and configuration preserved during installation. If a newly installed configuration was edited afterward, the script preserves the current state and stops before removing that file.

This package supports only the `GameAssembly.dll` SHA-256 listed in `manifest.json`.

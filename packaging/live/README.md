# SafeTeleport Live: immediate F6 recovery tool

Use this distribution when the game is already running and the character is currently stuck. It leaves `GameAssembly.dll` unchanged on disk and installs a reversible temporary hook in the running game process.

## Requirements

- Windows x64
- .NET 8 Runtime
- The tool and game must run at the same Windows privilege level
- `GameAssembly.dll` must match the SHA-256 recorded in `manifest.json`

## Usage

1. Enter a playable map, then run `Start-SafeTeleport.cmd`.
2. Return to the game and use these hotkeys:
   - **F6**: Return to the F7 anchor. If no anchor exists, use the verified spawn for the current scene; for an unknown scene, cycle through nearby emergency candidates.
   - **F7**: Save the current position.
   - **F8**: Clear the saved position.
   - **F12**: Restore the original entry bytes and exit. The allocation is released after active hook calls finish; otherwise the operating system reclaims it when the game exits. The game remains running.

Hotkeys respond only while the game window is in the foreground. Teleporting also clears `Rigidbody2D` velocity.

If the tool exits unexpectedly while the game is still running, run:

```powershell
.\Rollback.ps1
```

Rollback handles only a hook containing this tool's magic value and version marker. If another tool owns the entry, the bytes are left unchanged.

This tool contains version-specific function offsets. Validate a new game build before updating the target hash or offsets.

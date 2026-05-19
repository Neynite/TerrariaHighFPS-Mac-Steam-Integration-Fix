# TerrariaHighFPS

A standalone patcher for vanilla **Terraria 1.4.5** that adds high-FPS interpolation — identical in effect to the [HighFPSSupport](https://steamcommunity.com/sharedfiles/filedetails/?id=3119712528) tModLoader mod, but without tModLoader, so **Steam achievements stay enabled**.

## Compatible Platforms

| Platform | Supported |
|---|---|
| 🪟 Windows | ✅ |
| 🍎 macOS | ✅(Partially) |
| 🐧 Linux | ✅ |

## How it works

The patcher uses Mono.Cecil to inject IL code directly into `Terraria.exe`. It hooks into the game's draw loop to interpolate entity positions (players, NPCs, projectiles, pets) between 60 Hz logic ticks, making movement appear smooth at any framerate. The game's own `UpdateTimeAccumulator` is used as the interpolation factor, so it works with whatever FPS your machine can push.

> **Frame Skip must be set to OFF** in Terraria's video settings for this to do anything.

## Requirements

- Terraria 1.4.5 (Steam)
- Python 3 with `tkinter` (usually bundled)
- **Windows:** nothing extra
- **macOS:** `mono` runtime (`brew install mono` or equivalent)
- **Linux:** `mono` runtime (`sudo apt install mono-runtime` or equivalent)

## Setup

1. Clone or download this repository.
2. Run the launcher:

   ```
   python Launcher.py
   ```

3. If your Terraria folder isn't found automatically, you'll be prompted to locate it.
4. Click **Update/Create Patch** to generate the patched executable.
5. Click **Launch High FPS (True Smooth)** to play.

The launcher backs up your original `Terraria.exe` as `Terraria.exe.vanilla` before doing anything, so you can always restore it.

## Buttons

| Button | What it does |
|---|---|
| Launch High FPS (True Smooth) | Applies the patch and launches Terraria |
| Launch Vanilla (60 FPS) | Restores the original exe and launches |
| Update/Create Patch | Re-runs the patcher (use after a game update) |
| Change Game Directory | Manually select your Terraria folder |

## Config

The launcher saves your Terraria installation path to `config.json` in the same folder as the launcher:

```json
{
  "game_dir": "/path/to/your/Terraria"
}
```

You can edit this manually if needed. The launcher looks for `Terraria.exe` inside the specified directory to validate it.

Default paths it tries automatically:

- **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\Terraria`
- **macOS:** `~/Library/Application Support/Steam/steamapps/common/Terraria/Terraria.app/Contents/Resources`
- **Linux:** `/data/SteamLibrary/steamapps/common/Terraria`

## Steam Integration

You can wire the launcher into Steam so that clicking **Play** in your library automatically applies the patch and launches through Steam (keeping the overlay, playtime tracking, etc.).

> **NOTICE:** This is currently broken for macOS

1. In Steam, right-click **Terraria → Properties → General → Launch Options** and set:

   ```
   python /path/to/Launcher.py %command%
   ```

   On Windows:

   ```
   python "C:\path\to\Launcher.py" %command%
   ```

2. Click **Play** in Steam. Steam passes its launch command to the launcher via `%command%`, the launcher patches the exe if needed, and then hands the command back to Steam to actually start the game.

In this mode the launcher window closes itself immediately after launching, and the Steam overlay works normally.

## After a Terraria update

Steam will overwrite `Terraria.exe` when the game updates. Just click **Update/Create Patch** again — the launcher will re-patch from the vanilla backup.

## Building from source

```bash
# Compile the logic DLL (needs FNA.dll and Terraria.exe in path)
mcs -r:FNA.dll -r:Terraria.exe HighFPSLogic.cs -out:HighFPSLogic.dll -target:library

# Compile the patcher
mcs -r:Mono.Cecil.dll -r:/usr/lib/mono/4.8-api/Facades/netstandard.dll Patcher.cs -out:Patcher.exe

# Run the patcher manually
mono Patcher.exe Terraria.exe Terraria.exe.highfps HighFPSLogic.dll
```

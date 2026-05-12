# Better Battery Game

By Dezgard

Better Battery Game automatically swaps low batteries in held powered tools when the character is carrying a compatible charged spare.

Version 0.1.0 is the first test build. It keeps the scope small on purpose: held tools only, carried spare batteries only, and no ship-wide battery searching yet.

The mod checks the battery inside the held tool, not the tool itself. If the battery is low, it removes the dead battery and inserts the best compatible carried spare battery.

The mod also creates support logs. If something goes wrong, close the game and upload the newest log from `BepInEx\BatterySwapLogs`.

## Features

- Swaps low tool batteries.
- Checks held tools.
- Uses carried spare batteries.
- Matches compatible battery types.
- Skips batteries in chargers.
- Skips batteries inside other tools.
- Keeps ship storage untouched.
- Uses a configurable charge threshold.
- Logs swap attempts.
- Logs missing spare batteries.
- Logs failed swap attempts.

## Requirements

- Ostranauts
- BepInEx 5 x64
- Requires C#

## Install

1. Install BepInEx 5 for Ostranauts.
2. Put `OstranautsBatterySwap.dll` in:

```text
Ostranauts\BepInEx\plugins\
```

3. Restart the game.

When loaded, the BepInEx log should show:

```text
Ostranauts Battery Swap 0.1.0 loaded.
```

## Config

After first launch, BepInEx creates:

```text
Ostranauts\BepInEx\config\com.dezgard.ostranauts.batteryswap.cfg
```

Default settings:

```text
SwapThresholdPercent = 10
SwapCooldownSeconds = 2
IncludeDragSlot = false
```

## Support Logs

Support logs are written to:

```text
Ostranauts\BepInEx\BatterySwapLogs\
```

Look for:

```text
BatteryAutoSwap-*.log
```

Useful log lines:

- `SWAP_BEGIN`
- `SWAP_DONE`
- `NO_SPARE`
- `SWAP_FAIL`
- `SWAP_ERROR`

## Build From Source

This project targets `.NET Framework 4.7.2` and references the local Ostranauts install. If your game is not installed at `G:\Steam\steamapps\common\Ostranauts`, update `GameDir` in `OstranautsBatterySwap.csproj`.

```powershell
dotnet build -c Release
```

The built DLL will be in:

```text
bin\Release\net472\OstranautsBatterySwap.dll
```

## Release Files

The current packaged build is kept in:

```text
release\OstranautsBatterySwap-0.1.0\
```

## Notes

This is an early test build. It only handles carried spare batteries for held tools. Charger searching and ship storage searching are intentionally left out until the basic swap behavior is proven stable.

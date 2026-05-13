# Better Battery Game

By Dezgard

Better Battery Game automatically swaps low batteries in held powered tools when the character has a compatible charged spare.

Version 0.2.1 adds walk-to-charger support. The mod uses carried spare batteries first, then walks to compatible ship chargers if no carried spare is available.

The mod checks the battery inside the held tool, not the tool itself. If the battery is low, it removes the dead battery and inserts the best compatible charged battery it can safely use.

The mod also creates support logs. If something goes wrong, close the game and upload the newest log from `BepInEx\BatterySwapLogs`.

## Features

- Swaps low tool batteries.
- Checks held tools.
- Uses carried spare batteries.
- Uses ship chargers as fallback.
- Walks to chargers before charger swaps.
- Requeues the original tool action after a charger swap.
- Puts drained batteries into chargers.
- Matches compatible battery types.
- Keeps charger batteries separate from carried spares.
- Skips batteries inside other tools.
- Keeps ship storage untouched.
- Uses a configurable charge threshold.
- Logs swap attempts.
- Logs charger battery use.
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
Ostranauts Battery Swap 0.2.1 loaded.
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
UseShipChargers = true
WalkToShipChargers = true
ChargerUseRangeTiles = 1.25
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
- `CHARGER_SPARE`
- `CHARGER_WALK_QUEUED`
- `CHARGER_ARRIVED`
- `CHARGER_REQUEUE_DONE`
- `PUT_OLD_IN_CHARGER`
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
release\OstranautsBatterySwap-0.2.1\
```

## Notes

This is an early test build. It handles held tools only. It does not search general ship storage; charger use is limited to compatible batteries already sitting inside loaded ship chargers.

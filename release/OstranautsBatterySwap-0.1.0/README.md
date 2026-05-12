# Ostranauts Battery Swap

By Dezgard

Automatically swaps low batteries in held powered tools when the character has a compatible charged spare battery in carried inventory.

## Install

1. Install BepInEx for Ostranauts.
2. Put `OstranautsBatterySwap.dll` into:

```text
Ostranauts\BepInEx\plugins
```

3. Restart the game.

## Current Test Scope

- Checks held left and right hand tools.
- Swaps only batteries already carried by the same character.
- Does not search ship storage.
- Does not pull batteries out of other powered tools.
- Does not pull batteries out of chargers.
- Leaves logging in `Ostranauts\BepInEx\BatterySwapLogs`.

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

## Test

1. Hold a powered tool with a low battery.
2. Carry a compatible charged spare battery in a backpack, pouch, crate, or pocket.
3. Queue a tool action.
4. Check `BatteryAutoSwap-*.log` for `SWAP_BEGIN` and `SWAP_DONE`.

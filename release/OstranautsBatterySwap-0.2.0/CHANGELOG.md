# Changelog

## 0.2.0

- Added compatible ship charger fallback when no carried spare is available.
- Carried spare batteries are still preferred first.
- Drained batteries are placed into the charger when the charged battery came from that charger.
- Added `UseShipChargers` config.
- Added charger-specific support log lines.
- Fixed false `PUTBACK_FAIL` logging when the old battery was actually returned.

## 0.1.0

- Added first automatic battery swap test.
- Checks powered tools in held left and right hand slots.
- Swaps at or below 10% charge by default.
- Uses compatible spare batteries carried by the same character.
- Skips batteries inside chargers.
- Skips batteries already installed in other powered tools.
- Logs swap attempts to `BatterySwapLogs`.
- No ship-wide battery searching yet.

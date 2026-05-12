# Changelog

## 0.1.0

- Added first automatic battery swap test.
- Checks powered tools in held left and right hand slots.
- Swaps at or below 10% charge by default.
- Uses compatible spare batteries carried by the same character.
- Skips batteries inside chargers.
- Skips batteries already installed in other powered tools.
- Logs swap attempts to `BatterySwapLogs`.
- No ship-wide battery searching yet.

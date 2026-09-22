# Changelog

## Unreleased

### 2026-09-22
- Beacon: `open dotline` / `close dotline`.
- Beacon: flight tracking (`flight` → Tab → number); IATA → ICAO callsign mapping for ~80 airlines.
- Dotline: live flight on the wallpaper — dead-reckoned motion, marching-dot trail and course, departure/destination airports and route from adsbdb.com, hover card.
- Dotline: map shows only time-zone dots with a hover card (removed day/night shading).
- Dotline: FOCUS tab opens its own widget; custom focus length (h:m:s, ≤ 24 h) remembered.
- Crash-safe (atomic) saving in Beacon; damaged settings kept as a backup.
- Performance: Dotline CPU ~55% → ~7% of a core.
- Security: flight numbers validated on both sides of the hand-off file.

### 2026-09-21
- Dotline created: life-in-dots, goals, tabs, drag/resize/snap with HUD motion, Tactical styling.
- Dotline: focus timer with session log; wallpaper world map (Miller projection calibration).
- Beacon: time-of-day greeting; labelled amber progress bar (focus session / day).
- Projects grouped under `Vantage/`.

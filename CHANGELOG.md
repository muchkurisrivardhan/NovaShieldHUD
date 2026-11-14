# Changelog

## Unreleased

### 2026-11-14
- Lockscreen milestone 2: `--apply` sets the HUD as the lock screen through the documented WinRT API (no packaging or admin needed), saving your picture first; `--restore` puts it back byte for byte; `--status` shows both. Refuses politely while Spotlight is on.
- Lockscreen (new, milestone 1 of 6): renders the lock screen HUD picture at the monitor's real resolution in six themes, following Beacon's style by default. `--preview`, `--theme`, `--size`, `--debug-zones`, `--selftest`. Nothing on the PC is changed yet.
- Beacon Settings: **Save** keeps the window open and shows when it saved; **Close** leaves.
- Three more styles for Beacon and Dotline: **Cyberpunk** (neon pink and cyan on deep violet), **Retro CRT** (phosphor-green terminal, monospace) and **Minimal mono** (black, white and grey).
- Dotline follows Beacon's look: Tactical, HUD or Classic, with Beacon's accent colour. Changing it in Beacon restarts Dotline in the new style.
- Beacon Settings has a **Dotline** page: start Dotline with Beacon (on by default), birthday, lifespan, dot size, background, lock positions, time-zone dots.
- Beacon starts Dotline when it starts, and finds `Dotline.exe` beside itself (ready for one installer).
- Removed flight tracking from both apps (Beacon's `flight` command, Dotline's plane layer and its adsb.lol / adsb.fi / adsbdb.com calls).

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

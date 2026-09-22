# Features

Everything the two apps do, how it works, and why you'd use it. Dates are when each landed.

## Beacon — launcher (`Alt+Space`)

Beacon's own feature table is in [beacon/README.md](../beacon/README.md): apps, files, windows,
system commands, sites, calculator, plain-English sentences, local voice (Whisper), Claude
(`?`), custom commands, timers and reminders, quick answers. Added for Vantage:

| Feature | How it works | Use case | Added |
|---|---|---|---|
| **Time-of-day greeting** | GOOD MORNING / AFTERNOON / EVENING / NIGHT replaces the CPU/RAM readout in the Tactical header | A glanceable header instead of numbers you never act on | 2026-09-21 |
| **Labelled amber bar** | The bar under the command box shows the running Dotline focus session ("FOCUS · 18:20 LEFT"), otherwise how much of today has passed ("DAY 72% GONE") | Know how long is left in a focus block every time you open Beacon | 2026-09-21 |
| **Open / Close Dotline** | `open dotline`, `close dotline` — also usable by the plain-English and Claude layers | Hide the widgets for a screen share, bring them back after | 2026-09-22 |
| **Track a flight** | `flight` → Tab → flight number → Enter, or `flight AA100`. Converts ticket numbers (AA100) to broadcast callsigns (AAL100) for ~80 airlines. Starts Dotline if needed | Pick someone up at the airport; follow a colleague's trip | 2026-09-22 |
| **Stop tracking** | `flight` → "Stop tracking AA100" | | 2026-09-22 |
| **Crash-safe saving** | Settings, timers and usage are written to a temp file and swapped in; a damaged settings file is kept as `settings.bad-*.json` instead of silently reset | Your custom commands and shortcuts survive a power cut | 2026-09-22 |

## Dotline — desktop widgets

The widgets live on the desktop layer: under every window, hidden from Alt+Tab, still visible
after Win+D. Drag to move (snaps to screen edges), drag the corner dots or Ctrl+scroll to
resize, double-click to edit, right-click for everything else.

| Feature | How it works | Use case | Added |
|---|---|---|---|
| **Life in dots** | Birthday + lifespan (default 70) → one dot per week, month or year; lived / now / left. Hover a dot for its date and your age then | A daily nudge about how you spend time | 2026-09-21 |
| **Goals** | Name, start, achieve-by date, % done (click the 20-segment bar). Time passed shown as dots; ON TRACK / BEHIND x% / OVERDUE / ACHIEVED | Keep a deadline visible without opening an app | 2026-09-21 |
| **Tabs** | One widget switches between LIFE, each goal and + GOAL; "Open in new widget" puts views side by side | Several goals without desktop clutter | 2026-09-21 |
| **Tactical HUD look** | Gunmetal plates, amber accent, Agency FB, chamfered corners, adjustable glass (Clear / Glass / Solid) | Matches Beacon | 2026-09-21 |
| **Motion** | Lock-on brackets while dragging, scan sweep on drop, eased glide to "Move to" presets and screen edges | Feels like one system | 2026-09-21 |
| **Focus timer** | FOCUS tab opens its own widget: 60-dot dial, 15/25/50/90 min or CUSTOM h:m:s (≤ 24 h, remembered), pause, +5 min, stop. Logs sessions; 7-day history. Survives restarts | Pomodoro / deep-work blocks | 2026-09-21 |
| **Zone dots on the wallpaper** | Up to 5 cities as small orange dots placed on your map wallpaper (Miller projection fitted to its grid lines). Hover: day/night icon, local time, city, date, offset from you | "Is it working hours in Hyderabad?" at a glance | 2026-09-21 |
| **Live flight on the wallpaper** | Position from adsb.lol (adsb.fi fallback) every 15 s, dead-reckoned in between so the plane glides. Route from adsbdb.com: departure and destination marked, marching dots behind (flown) and ahead (to go). Hover: route, km flown / to go, altitude, speed, heading, type, registration | Watch a family member's flight land | 2026-09-22 |
| **Persistence** | Every change saved immediately and atomically; a hard kill loses nothing (tested: files identical after force-close + reopen) | | 2026-09-22 |
| **Low CPU** | Ambient animations capped at 8–12 fps, shadows on static layers, flight drawn in its own small window: ~55% → ~7% of a core while tracking | Runs all day | 2026-09-22 |

## Limits worth knowing

- The map overlay only works on the wallpaper it was calibrated for (`2454655.jpg`). Another map needs new `X0`/`Y0`/`K` values in `state.json`.
- Flights appear only when airborne or broadcasting on the ground. Free data has no history from takeoff; the trail starts when you add the flight. Some callsigns have no known route.
- At world scale an airliner moves about one pixel a minute; the marching dots show direction.
- Main monitor only for the map.

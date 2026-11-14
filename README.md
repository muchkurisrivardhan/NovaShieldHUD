# Vantage

A tactical HUD layer for the Windows desktop: a keyboard launcher, desktop widgets, a focus
timer and a live world map on your wallpaper, all in one gunmetal-and-amber style.

Two apps, built to work together, and a lock screen to match:

| App | What it is |
|---|---|
| **[Beacon](beacon/)** | A Spotlight-style launcher. `Alt+Space`, type, Enter. Apps, files, windows, system commands, timers, plain-English sentences, local voice, and Claude for anything else. |
| **[Dotline](dotline/)** | Desktop widgets that sit on your wallpaper: your life and your goals as dots, a focus timer, and time zones on a world-map wallpaper. Installed and set up through Beacon, and always in Beacon's style. |
| **[Lockscreen](lockscreen/)** | A HUD picture for the Windows lock and sign-in screens, in Beacon's style. In progress: the renderer and preview work; applying it, widgets and auto-refresh are next. |

![Focus timer](docs/media/focus-timer.png)

## What you can do with it

| You want to… | Do this |
|---|---|
| See how much of your life, year or goal is left | Dotline's **LIFE** tab and goal tabs: one dot per week, month or year |
| Stay on one task | **FOCUS** tab: pick 15/25/50/90 min or a custom h:m:s (up to 24 h) → START. Beacon's amber bar shows the countdown |
| Schedule across time zones | Right-click a widget → **World map…** → pick up to 5 cities. Hover an orange dot on your wallpaper for its local time and offset |
| Launch, find, switch | `Alt+Space`, type part of an app, file or window name |
| Control the PC by sentence | "turn the volume to 75 and dim the screen" |
| Open / close the widgets | Beacon: `open dotline`, `close dotline`. Beacon starts them with itself (Settings → **Dotline**) |
| Change the look of both apps | Beacon Settings → **Appearance**: Tactical, HUD, Classic, Cyberpunk, Retro CRT or Minimal mono, plus an accent colour. Dotline follows |
| Set up the widgets | Beacon Settings → **Dotline**: birthday, lifespan, dot size, background, lock positions, time-zone dots |

Every feature, with how it works: **[docs/FEATURES.md](docs/FEATURES.md)**.

## Requirements

- Windows 10/11 (developed on Windows 11 ARM64), .NET 10 SDK
- Beacon: NuGet packages Anthropic, NAudio.WinMM, Whisper.net (restored on first build)
- Dotline: no packages
- Optional: an Anthropic API key (Beacon's `?` / Claude features), entered in Beacon's Settings

## Build and run

```bash
dotnet build -c Release beacon/Beacon.csproj
dotnet publish -c Release -o dotline/dist dotline/Dotline.csproj
```

Run `beacon/bin/Release/net10.0-windows/Beacon.exe`; it starts Dotline too.

Self-tests (date math, map projection):

```bash
dotnet dotline/bin/Release/net10.0-windows/Dotline.dll --selftest
```

## Where your data lives

Nothing is uploaded except what a feature needs (below). All state is local, per Windows user:

| Path | Contents |
|---|---|
| `%APPDATA%\Beacon\` | settings (API key encrypted with Windows DPAPI), timers, usage counts |
| `%APPDATA%\Dotline\state.json` | birthday, goals, widget layout, focus log, map zones (also edited from Beacon's Settings) |
| `%APPDATA%\Vantage\` | hand-off file between the apps: the running focus session |

Network use: Claude API (only when you ask Claude) and site favicons. Details in [SECURITY.md](SECURITY.md).

## Status and licence

Pre-release. Source is public to gather feedback; **all rights reserved** (see [LICENSE](LICENSE)) until an open-source licence is chosen. Security testing still to do: [SECURITY.md](SECURITY.md).
History: [CHANGELOG.md](CHANGELOG.md).

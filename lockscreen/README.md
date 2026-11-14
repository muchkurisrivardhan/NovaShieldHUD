# Lockscreen

A HUD picture for the real Windows lock screen and sign-in screen: charcoal background, grid,
scanlines, a ring assembly that frames your account picture, and four corner readouts. It
follows Beacon's style, so the lock screen matches the launcher and the widgets.

![Amber](../docs/media/lockscreen-amber.png)

**Status: milestone 3 of 6.** The renderer, preview, apply, restore and the account emblem
work. Lock screen widgets and scheduled refresh are not built yet.

## From Beacon

Beacon Settings → Appearance has a **Lock screen** card: pick the style (or match the
launcher), edit the corner text and effects, then **Preview**, **Apply to lock screen** or
**Restore my picture**. While the HUD is applied, Beacon's **Save** re-applies it so a changed
style or text shows. The command line below does the same thing.

## Set it as your lock screen

1. Your lock screen must show a **Picture** (not Windows Spotlight or a slideshow):
   Settings > Personalization > Lock screen > Personalize your lock screen > Picture.
   `--apply` checks this and changes nothing if Spotlight is on.
2. Run `Lockscreen.exe --apply`. It renders the HUD at your monitor's real resolution and sets it.
3. To see the HUD behind the PIN or password box too, turn on Settings > Personalization >
   Lock screen > **Show the lock screen background picture on the sign-in screen**.
   (`--apply` mentions this once; it never changes the setting itself.)
4. Press Win+L to look.

### Putting it back

`Lockscreen.exe --restore` sets your own picture back and deletes the saved copy.

- The first `--apply` saves the picture you had, exactly as Windows hands it over, to
  `%LOCALAPPDATA%\LockscreenWidget\original`. Later applies never overwrite it with the HUD.
- If you set a new picture yourself after applying, the next `--apply` notices and saves that
  one instead, so `--restore` brings back your latest choice, not an old one.
- Restored means the same picture, byte for byte (checked by SHA-256 in testing). Windows'
  own record of where the picture came from may differ; that bookkeeping isn't visible to apps.
- `--status` shows what's set and what's saved, without changing anything.

The picture is set with the documented `Windows.System.UserProfile.LockScreen` API, which
works for this program without MSIX packaging, so no certificate, Developer Mode or admin
rights are needed for it. The spec's admin-only registry fallback (PersonalizationCSP) wasn't
needed and isn't built.

## Try it

```bash
dotnet build -c Release lockscreen/Lockscreen.csproj
```

```bash
lockscreen/bin/Release/net10.0-windows/Lockscreen.exe --preview hud.png
```

| Option | What it does |
|---|---|
| `--preview <out.png>` | Render to a file. Changes nothing on the PC |
| `--theme <name>` | `amber`, `cyan`, `phosphor`, `redalert`, `mono`, `neon`, `custom`, or `beacon` (default) |
| `--size <W>x<H>` | Render at this size instead of the primary monitor's real pixels |
| `--debug-zones` | Outline the areas kept clear for the Windows clock and the sign-in box |
| `--apply` | Render at the monitor's size and set it as the lock screen (see below) |
| `--restore` | Put your own lock screen picture back |
| `--emblem <dir>` | Write the account-picture padlock PNGs (448 and 1080 px) for every theme, or just `--theme <name>`. Changes nothing |
| `--status` | Show what's set and what's saved; changes nothing |
| `--selftest` | Config parsing, tokens, theme math and a check of the rendered picture |

## Themes

`beacon` (the default) follows Beacon's style: Tactical → amber, HUD → cyan, Retro CRT →
phosphor, Cyberpunk → neon, Minimal mono → mono, Classic → your accent colour. An accent
colour chosen in Beacon replaces the theme's own.

| Theme | Accent | Background |
|---|---|---|
| `amber` | `#FFB000` | `#0E0F11` |
| `cyan` | `#00E5FF` | `#0A0F14` |
| `phosphor` | `#33FF66` | `#0A0F0B` |
| `redalert` | `#FF3B3B` | `#120A0A` |
| `mono` | `#E8E8E8` | `#0C0C0C` |
| `neon` | `#FF2E97` | `#12041F` |

## Configuration

`%LOCALAPPDATA%\LockscreenWidget\config.json`, created with defaults on first run. A value
that isn't valid is replaced by its default and reported as a warning; the rest of the file
still applies.

```json
{
  "theme": "beacon",
  "ringCenter": { "x": 0.5, "y": 0.42 },
  "ringScale": 1.0,
  "corners": {
    "topLeft":     ["SYSTEM", "SECURE SHELL // v1.0"],
    "topRight":    ["{DATE}", "{WEEKDAY}"],
    "bottomLeft":  ["NODE-01", "LINK: LOCAL // ENCRYPTED"],
    "bottomRight": ["STATUS: SEALED", "{UPTIME_DAYS}D UPTIME"]
  },
  "effects": { "grid": true, "scanlines": true, "vignette": true, "glow": true },
  "widgets": { "nodeLabel": "NODE-01", "countdown": { "label": "TARGET", "date": "2027-01-01" } },
  "refreshMinutes": 60
}
```

A custom theme: set `"theme": "custom"` and add
`"customTheme": { "accent": "#RRGGBB", "background": "#RRGGBB", "dimOpacity": 0.55, "glow": 1.0 }`.

Corner text tokens: `{DATE}`, `{WEEKDAY}`, `{DAY_OF_YEAR}`, `{YEAR_PROGRESS}`,
`{UPTIME_DAYS}`, `{COUNTDOWN:YYYY-MM-DD}`. There is no time-of-day token: Windows shows a
live clock, and a time baked into a picture goes stale.

## Account picture emblem

A matching padlock emblem for your Windows account picture. On the sign-in screen it sits in
the empty centre of the HUD ring.

![Emblem](../docs/media/lockscreen-emblem.png)

```bash
Lockscreen.exe --emblem emblems
```

That writes `emblem-<theme>-448.png` and `emblem-<theme>-1080.png` for every theme into the
`emblems` folder (add `--theme amber` for just one). The background is a charcoal disc with
transparent corners, so Windows' circular crop shows a clean disc.

Windows has no API for a desktop app to set the account picture, so this only makes the file.
Set it yourself: **Settings > Accounts > Your info > Choose a file**, and pick the PNG (1080
is the safe size). Use the one whose theme matches your lock screen.

## What it shows, and what it never will

Anyone at the keyboard can read the lock screen. The picture only carries the corner text
you type and the tokens above: no user name, PC name, IP address, email, notifications, file
names or calendar details.

## What this does NOT touch

- No code is injected into, hooked into, or replaced in any Windows process.
- No credential provider: Windows sign-in, PIN, password and Windows Hello are untouched.
- No system files or their permissions; Defender, SmartScreen, UAC and the lock itself stay on.
- No auto-login, no second password screen.
- No network calls, no telemetry.
- Spotlight and the sign-in screen background setting are only read or mentioned, never changed.

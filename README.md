# NovaShield HUD

A tactical HUD layer for the Windows desktop: a keyboard launcher, desktop widgets, a focus
timer, a live world map on your wallpaper and a matching lock screen, all in one
gunmetal-and-amber style.

**Website and downloads:** <https://muchkurisrivardhan.github.io/vantage/>

| Part | What it is |
|---|---|
| **[Beacon](beacon/)** | A Spotlight-style launcher. `Alt+Space`, type, Enter. Apps, files, windows, system commands, timers, plain-English sentences, local voice, and Claude for anything else. |
| **[Dotline](dotline/)** | Desktop widgets that sit on your wallpaper: your life and your goals as dots, a focus timer, and time zones on a world-map wallpaper. Set up through Beacon, always in Beacon's style. |
| **[Lockscreen](lockscreen/)** | A HUD picture for the Windows lock and sign-in screens, in Beacon's style, kept current by a scheduled refresh. Plus a matching account picture and Widgets-board widgets. |

![Focus timer](docs/media/focus-timer.png)

## Setup guide

### 1. Install

1. Download the installer for your PC from the [website](https://muchkurisrivardhan.github.io/vantage/#download)
   or [GitHub Releases](https://github.com/muchkurisrivardhan/vantage/releases/latest):
   - `NovaShieldHUD-Setup-x64.exe`: Intel and AMD PCs
   - `NovaShieldHUD-Setup-arm64.exe`: Snapdragon and other ARM PCs
   (Not sure? Settings > System > About > *System type*.)
2. Run it. It installs for you only, in `%LOCALAPPDATA%\Programs\NovaShield HUD`: no admin
   prompt, and nothing else to download (.NET is included).
3. Windows may warn about an unrecognised app, because the installer isn't code-signed yet:
   **More info > Run anyway**. If **Smart App Control** is on, Windows blocks unsigned apps
   outright and there's no Run anyway; it can't be installed on that PC until releases are signed.
4. Finish starts Beacon. Its icon is in the tray; press `Alt+Space`.

### 2. Make it yours

Everything is in **Beacon Settings** (tray icon > Settings, or type `settings`):

| Page | What to set |
|---|---|
| **General** | Start Beacon when you sign in, the launcher shortcut, **Check for updates** |
| **Appearance** | The style (Tactical, HUD, Classic, Cyberpunk, Retro CRT, Minimal mono) and accent colour. Dotline and the lock screen follow |
| **Dotline** | Your birthday and lifespan, dot size, background, lock positions, time-zone dots |
| **Voice** | Download a Whisper model to speak instead of typing (`Alt+M`) |
| **Plain English** | Your Anthropic API key, only if you want `?` / Claude |

### 3. The lock screen

1. Windows must show a **Picture** on the lock screen, not Windows Spotlight:
   Settings > Personalization > Lock screen > Personalize your lock screen > **Picture**.
2. Beacon Settings > Appearance > **Lock screen**: pick a style, edit the corner text,
   **Preview**, then **Apply to lock screen**. Your own picture is saved first.
3. Optional: Settings > Personalization > Lock screen > **Show the lock screen background
   picture on the sign-in screen**, to see the HUD behind the PIN box too.
4. Press `Win+L`.

Applying also adds a **scheduled refresh** (below), so the date and uptime on the picture
stay current. **Restore my picture** puts your own picture back byte for byte and removes the
refresh.

Optional extras, in [lockscreen/README.md](lockscreen/README.md): a matching padlock account
picture, and widgets for the Widgets board (that one asks for admin once, to trust its
signing certificate).

### 4. Updates

Beacon never checks by itself. Settings > General > **Check for updates** asks GitHub for the
latest release; **Update** downloads the installer, checks it against the SHA-256 published
with the release, installs it over the old version and restarts Beacon. Settings are kept.

For maintainers: push a tag like `v0.6.0` and [the release workflow](.github/workflows/release.yml)
builds both installers, runs the self-tests and publishes the release that Update offers.

### 5. Uninstall

Settings > Apps > Installed apps > **NovaShield HUD** > Uninstall. It stops the apps, puts your
own lock screen picture back and removes the scheduled refresh. Your settings folders (below)
are left for a reinstall; delete them to remove every trace.

## Auto-refresh: the scheduled task

`Lockscreen.exe --apply` (and Beacon's Apply button) registers one Task Scheduler task,
**NovaShield HUD lock screen refresh**, for your account only:

| When | What |
|---|---|
| You sign in | re-renders the HUD and sets it again |
| You lock the PC (`Win+L`) | same |
| Every hour (`refreshMinutes` in the config, 15 to 1440) | same |

- It runs as you, with your normal rights: no admin, no stored password, no downloads.
- It runs `Lockscreen.exe --refresh` hidden, which only re-applies while the HUD is still your
  lock screen. If you've since set another picture yourself, or turned Spotlight on, it
  changes nothing.
- See it in Task Scheduler (Task Scheduler Library), or with `Lockscreen.exe --status`.
- **Restore my picture**, `Lockscreen.exe --restore` or uninstalling removes it.

## What this does NOT touch

- **Signing in.** No credential provider: Windows sign-in, PIN, password and Windows Hello are
  untouched. No auto-login, no second password screen.
- **Windows itself.** No code injected into, hooked into or replaced in any Windows process. No
  system files or their permissions. Defender, SmartScreen, UAC and the lock itself stay on.
- **Admin rights.** The apps, the lock screen picture and the scheduled refresh need none.
  The only admin step is the optional Widgets-board package trusting its own certificate.
- **Your settings.** Spotlight and the sign-in-screen background setting are only read or
  mentioned, never changed. Only the lock screen picture is set, through Windows' documented
  lock screen API, and your own picture is saved first.
- **Other users.** Everything is per user: the install, the settings, the scheduled task.
- **The network,** except: Claude (only when you ask it, with your own key), website icons in
  the launcher (can be turned off), the Whisper model you choose to download, and GitHub
  when you click Check for updates. No telemetry, no account, no ads.
- **What's on the lock screen.** Only the corner text you type and the date/uptime fill-ins: no
  user name, PC name, IP address, email, notifications, file names or calendar.

## Where your data lives

All state is local, per Windows user:

| Path | Contents |
|---|---|
| `%APPDATA%\Beacon\` | settings (API key encrypted with Windows DPAPI), timers, usage counts |
| `%APPDATA%\Dotline\state.json` | birthday, goals, widget layout, focus log, map zones |
| `%APPDATA%\NovaShieldHUD\` | hand-off file between the apps: the running focus session |
| `%LOCALAPPDATA%\LockscreenWidget\` | lock screen config, the rendered picture, your saved original |

Details in [SECURITY.md](SECURITY.md).

## Build from source

Windows 10/11, .NET 10 SDK.

```bash
dotnet build -c Release beacon/Beacon.csproj
dotnet publish -c Release -o dotline/dist dotline/Dotline.csproj
dotnet build -c Release lockscreen/Lockscreen.csproj
```

Run `beacon/bin/Release/net10.0-windows/Beacon.exe`; it finds and starts the others.

Self-tests:

```bash
dotnet dotline/bin/Release/net10.0-windows/Dotline.dll --selftest
lockscreen/bin/Release/net10.0-windows10.0.19041.0/Lockscreen.exe --selftest
```

The installer (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install JRSoftware.InnoSetup`):

```bash
powershell -ExecutionPolicy Bypass -File installer/build.ps1 -Arch x64 -Version 0.5.0
```

Every feature, with how it works: [docs/FEATURES.md](docs/FEATURES.md). History: [CHANGELOG.md](CHANGELOG.md).

## Licence

Not open source. The app is free to install and use; the source is public to read and review;
all other rights are reserved. See [LICENSE](LICENSE). The open-source components it includes,
and their licences, are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Windows is a trademark of Microsoft Corporation; Claude is a trademark of Anthropic, PBC.
NovaShield HUD isn't affiliated with either.

## Support it

It's made by one person. [Buy me a coffee](https://buymeacoffee.com/srivardhanmuchuri).

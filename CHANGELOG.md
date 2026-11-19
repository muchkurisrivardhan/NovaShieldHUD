# Changelog

## Unreleased

### 2026-11-19 (evening)
- **Prompt Ring** (Beacon, `Ctrl+Alt+P`): select text you dictated in any app, press the shortcut, and a ring opens at the cursor in your Beacon style with Build, Fix, Workflow, Research, Write, Explain, Plan and Clean up. Pick one (click or 1-8) and the text is rewritten into a structured prompt (goal, context, requirements, constraints, how to check it's done...) and pasted over the selection; Ctrl+Z undoes it, and the clipboard is given back. The structures follow Anthropic's prompting advice; what you didn't say becomes a [question] to fill in, never an invented detail.
- **Free local AI, no account or key:** the rewriting runs on your PC through llama.cpp (LLamaSharp, x64 and ARM64). Settings → Prompt Ring downloads a free Apache-licensed model: Qwen 2.5 1.5B (1 GB, recommended) or Qwen 3 4B (2.5 GB, better), checked against a pinned SHA-256. Without one, the ring lays your words out in the chosen structure with questions for the rest.
- **Opening apps by name, without restarting Beacon:** Beacon now watches the Start Menu, so an app installed while it runs opens by name seconds later, and an "open X" that finds nothing rescans (Store apps). Spoken names match however dictation spaced or misheard them: "free l l m" opens Free LLM API, "tail scale" and "tailscail" open Tailscale, "note pad plus plus" opens Notepad++. Two equally close apps are never guessed between.

### 2026-11-19 (later)
- **Installer asks about Filee:** a Select Components page, with Filee ticked by default ("NovaShield HUD with Filee") or left out ("NovaShield HUD only"). Unticking it on an update removes the copy installed before. Filee gets its own Start menu entry.
- **Credits:** Beacon Settings → Filee has a Credits card for Filee's author, Myeonggu Kim (KnifeLemon), with links; the README has a Credits section.
- **Smart App Control:** with it on, Windows blocks Filee's unsigned files one by one ("Part of this app has been blocked"), so the ring can't open and Filee closes. Beacon now reads its state, doesn't start Filee by itself while it's on, and says so on the Filee page.
- `filee/` now holds all of upstream Filee: tests, docs, installers and the contributor guides too.

### 2026-11-19
- **New app: Filee**, the drag-and-drop file converter, forked from [KnifeLemon/Filee](https://github.com/KnifeLemon/Filee) (MIT, upstream commit `cf96cd5`) into `filee/`. Hold `Ctrl`, drag files, drop them on a format in the ring at the cursor.
- **Filee wears Beacon's look, live.** Beacon writes its style into `%APPDATA%\Filee\theme.json` (surfaces, text, accent, light/dark, corner radius, font); the fork watches that file and restyles in place. Tactical is gunmetal and amber with square corners in Bahnschrift, HUD cyan on navy, Cyberpunk pink on violet, Retro CRT phosphor green in Cascadia Mono, Minimal mono greys, Classic keeps Filee's own rounded surfaces with Beacon's accent.
- Beacon Settings has a **Filee** page: start with Beacon (on by default), follow Beacon's look, a live preview of the format ring in the style you are picking, ring size and opacity, captions, pause, upstream update notices, Open / Close.
- Beacon commands: **Convert files (Filee)** (`convert`, `filee`, `png`, `pdf`...) and **Close Filee**.
- Installer bundles Filee in `filee\` (self-contained, with the rhwp and 7-Zip engines; the large engines download on demand) and stops it on update and uninstall.

### 2026-11-16
- **Removed the lock screen HUD** (the Lockscreen app, its Widgets-board widgets, and the Lock screen card in Beacon Settings → Appearance). Updating over 0.5.0 puts your own lock screen picture back and removes the scheduled refresh. If you installed the Widgets-board package, remove it in Settings → Apps. The older lock screen entries below are history.

### 2026-11-14
- Renamed to **NovaShield HUD** (was Vantage). The focus hand-off file moved to `%APPDATA%\NovaShieldHUD\`. The widget package keeps its old identity and certificate name so installed copies still work.
- **Installer:** `installer/build.ps1` makes `NovaShieldHUD-Setup-x64.exe` / `-arm64.exe` (Inno Setup, self-contained, per-user, no admin). Uninstall restores your lock screen picture and removes the scheduled refresh.
- **Releases:** pushing a `v*` tag builds both installers, runs the self-tests and publishes them with `SHA256SUMS.txt` (`.github/workflows/release.yml`).
- **Beacon updates:** Settings > General > **Check for updates** / **Update**: downloads the latest release's installer, checks its SHA-256, installs silently and restarts Beacon.
- Lockscreen milestone 5: **scheduled refresh**. `--apply` adds a per-user task that re-applies the HUD at sign-in, at lock and every `refreshMinutes` (hidden, no admin, no downloads); `--refresh` leaves a picture you've set yourself alone; `--restore` removes the task. Tested on this PC: registered without a prompt, ran with result 0, removed by restore.
- Lockscreen milestone 6: README setup guide, the scheduled task, and "what this does NOT touch".
- Licence: decided not to open-source. LICENSE now lets anyone install and use the app for free; the source stays all rights reserved. New `THIRD-PARTY-NOTICES.txt` lists the MIT-licensed components and the trademark notices, and ships in the installer.
- Website: working download buttons once a release exists, Buy Me a Coffee in place of Ko-fi, FAQ on updates and uninstalling.
- Fix: the Lockscreen project no longer compiles the widget provider's sources (they broke its build).
- Beacon Settings → Appearance has a **Lock screen** card: style, the eight corner text lines (with {DATE}/{COUNTDOWN} fill-ins), effect toggles, and Preview / Apply / Restore buttons. Save re-applies the HUD while it's on your lock screen.
- Lockscreen milestone 4: widgets. A packaged, signed, out-of-process COM widget provider (Windows App SDK 1.8, Widgets component only) with Node Status, Day / Year Progress and Countdown in small and medium sizes (Adaptive Cards, monospace). `widgets/build-package.ps1` packs and signs the MSIX with a self-signed development certificate; `install.ps1` trusts it (one admin prompt) and installs; `uninstall.ps1` removes both. Verified on this PC: installs, COM activation starts the provider from its package, Windows lists it as a widget provider, and the widgets appear in Win+W > Add widgets. Lock screen placement is up to Windows (not yet checked).
- Lockscreen milestone 3: `--emblem <dir>` writes the account-picture padlock PNGs (448 and 1080 px) in every theme. A charcoal disc with transparent corners, so Windows' circular crop is clean. Windows can't set the account picture from a desktop app, so the README gives the manual step.
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

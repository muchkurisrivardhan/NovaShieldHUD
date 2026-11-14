# Launch plan

## Before going public

1. **Rename** — done: *Vantage* (was a private working name that borrowed a trademark).
2. **Licence** — currently all rights reserved. Later: keep source-available but all rights reserved for now, or open source
   (MIT = simplest, Apache-2.0 = patent grant, GPL-3.0 = forks must stay open).
3. **Security tests** in [SECURITY.md](../SECURITY.md) — at least 1–5 and 10.
4. **Wallpaper.** Don't ship the map wallpaper (third-party image); document how to calibrate
   another map, or ship a public-domain map (Natural Earth).
5. **Releases.** GitHub Actions: build, self-tests, zip `Beacon` + `Dotline`, SHA-256 checksums.
6. **Record the clips** in [DEMO-VIDEOS.md](DEMO-VIDEOS.md).

## Release & patch policy

- Semantic versions: `1.0.0`, security fixes as patch releases (`1.0.1`), noted in CHANGELOG under **Security**.
- Private vulnerability reporting on GitHub; fix window target 14 days for high severity.
- Dependabot for NuGet; re-run the vulnerable-package check each release.

## Posts (drafts — post them yourself)

**Short (X / Threads / Bluesky)**
> I turned my Windows desktop into a HUD. Alt+Space launcher, my life as dots on the
> wallpaper, a focus timer and time zones you hover on a world
> map. Local-first, no telemetry. Open source soon — want it? [link] [clip 6]

**Reddit (r/Windows11, r/desktops, r/csharp, r/productivity)**
> **Title:** I built a tactical HUD for Windows: launcher + desktop widgets in one style
>
> Two WPF/.NET 10 apps. **Beacon** is a Spotlight-style launcher (apps, files, windows,
> plain-English commands, local Whisper voice, optional Claude). **Dotline** puts widgets on
> the desktop layer: life and goals as dots, a focus timer, up to five time-zone dots on a
> world-map wallpaper. One look (Tactical, HUD or Classic) across both apps. Everything is stored locally.
> Looking for feedback before I open-source it — what would you use it for?

**Show HN**
> Show HN: A HUD for the Windows desktop – launcher and dot widgets in one style

**Where:** r/Windows11, r/desktops, r/Rainmeter (similar audience), r/csharp, r/dotnet,
Hacker News, Product Hunt (after v1.0), a short YouTube/TikTok of clip 6.

**Gauge demand:** GitHub stars/watchers, "Notify me" issue, comments asking for a download.
Open-source when there's clear interest.

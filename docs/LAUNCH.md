# Launch plan

## Before going public

1. **Rename** — done: *NovaShield HUD* (was *Vantage*). Check the name before launch: a
   Novashield, Inc. has filed NOVASHIELD for PC security software in the US (Class 9).
2. **Licence** — decided: not open source. Free to install and use, source public to read, all
   other rights reserved (LICENSE). Third-party licences in THIRD-PARTY-NOTICES.txt.
3. **Code signing** — needed: Smart App Control blocks the unsigned installer and exes
   outright. Azure Trusted Signing or an OV certificate, wired into installer/build.ps1.
4. **Security tests** in [SECURITY.md](../SECURITY.md) — at least 1–5 and 10.
5. **Wallpaper.** Don't ship the map wallpaper (third-party image); document how to calibrate
   another map, or ship a public-domain map (Natural Earth).
6. **Releases** — done: push a `v*` tag; .github/workflows/release.yml builds both installers, runs the self-tests and publishes them with SHA256SUMS.txt.
7. **Record the clips** in [DEMO-VIDEOS.md](DEMO-VIDEOS.md).

## Release & patch policy

- Semantic versions: `1.0.0`, security fixes as patch releases (`1.0.1`), noted in CHANGELOG under **Security**.
- Private vulnerability reporting on GitHub; fix window target 14 days for high severity.
- Dependabot for NuGet; re-run the vulnerable-package check each release.

## Posts (drafts — post them yourself)

**Short (X / Threads / Bluesky)**
> I turned my Windows desktop into a HUD. Alt+Space launcher, my life as dots on the
> wallpaper, a focus timer and time zones you hover on a world
> map. Local-first, no telemetry. Free download: [link] [clip 6]

**Reddit (r/Windows11, r/desktops, r/csharp, r/productivity)**
> **Title:** I built a tactical HUD for Windows: launcher + desktop widgets in one style
>
> Two WPF/.NET 10 apps. **Beacon** is a Spotlight-style launcher (apps, files, windows,
> plain-English commands, local Whisper voice, optional Claude). **Dotline** puts widgets on
> the desktop layer: life and goals as dots, a focus timer, up to five time-zone dots on a
> world-map wallpaper. One look (Tactical, HUD or Classic) across both apps. Everything is stored locally.
> Free to download. Looking for feedback — what would you use it for?

**Show HN**
> Show HN: A HUD for the Windows desktop – launcher and dot widgets in one style

**Where:** r/Windows11, r/desktops, r/Rainmeter (similar audience), r/csharp, r/dotnet,
Hacker News, Product Hunt (after v1.0), a short YouTube/TikTok of clip 6.

**Gauge demand:** GitHub stars/watchers, "Notify me" issue, comments asking for a download.


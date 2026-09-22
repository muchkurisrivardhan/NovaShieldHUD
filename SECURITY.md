# Security

## Reporting a vulnerability

Please don't open a public issue. Use GitHub's **private vulnerability reporting**
(Security → Report a vulnerability) on this repository. Expect an acknowledgement within
7 days; fixes ship as a patch release with a note in [CHANGELOG.md](CHANGELOG.md).

## Supported versions

Only the latest release gets security fixes.

## What the apps can do (attack surface)

| Area | Why it matters | Current protection |
|---|---|---|
| **Beacon runs programs and commands** | Custom commands can start programs and PowerShell; system commands can shut down, close apps, kill processes | Runs as the normal user (no UAC, can't touch elevated windows). Destructive commands are flagged; Claude-proposed destructive steps need confirmation (on by default); voice auto-run never runs destructive commands |
| **Claude integration** | Model output chooses commands to run — prompt injection risk (e.g. from clipboard or page text) | Only registered commands can be called, with typed parameters; destructive ones gated. API key stored with Windows DPAPI (current user) |
| **Local voice** | Microphone audio | Transcribed on the PC with Whisper; not uploaded |
| **Network calls** | Claude API; favicons fetched directly from each site; flight data from adsb.lol, adsb.fi, adsbdb.com | Flight calls only while a flight is tracked; only the flight number/callsign is sent; responses parsed as JSON data, never executed |
| **Hand-off files** in `%APPDATA%\Vantage` | Any program running as you can write them | Treated as untrusted: flight numbers must match `^[A-Z0-9]{2,10}$`, values are URL-escaped, malformed files ignored |
| **Beacon starts Dotline** | Finds `dotline\` by walking up from Beacon's folder | Same-user only; the path is not taken from any file or input |
| **Beacon closes Dotline** | Ends processes named `Dotline` | Same-user processes only |
| **State files** | Settings, goals, focus log | Local only; atomic writes; damaged files are kept aside, not overwritten |

## Checked so far (2026-09-22)

- [x] No secrets, keys or personal paths in the source tree
- [x] `dotnet list package --vulnerable --include-transitive`: no known-vulnerable packages
- [x] Flight input validated on both sides of the hand-off file
- [x] Network responses handled as data (JSON parse only); malformed responses don't crash
- [x] Force-close + reopen: saved state identical (no corruption)

## Security tests still to do before a public release

1. **Command execution** — custom commands with quotes, `&`, `|`, `;`, `$(...)`, backticks and very long arguments: confirm arguments can't break out into extra commands.
2. **Prompt injection** — put instructions in the clipboard, window titles and file names, then use `?` and voice: confirm nothing destructive runs without confirmation.
3. **Hand-off files** — fuzz `focus.json`, `flight.json`, `flight-trail.json` (huge files, deep nesting, wrong types, NaN/huge numbers, invalid UTF-8): no crash, no hang, no unbounded memory.
4. **Network responses** — serve malicious/oversized/slow JSON for the flight APIs (local proxy): timeouts hold, no crash, text shown as plain text.
5. **Settings file tampering** — edited `settings.json` and `state.json`: app keeps running or starts fresh with a backup.
6. **DPAPI key storage** — confirm the key is never logged, shown, or written in plain text; another Windows user can't read it.
7. **Process launching** — confirm Beacon never starts Dotline or custom commands from the current working directory or `PATH` hijacks (DLL/exe planting).
8. **Privilege** — both apps run unelevated; check nothing requests elevation.
9. **Dependencies** — re-run the vulnerable-package check each release; watch Whisper.net native runtimes.
10. **Static analysis** — enable .NET security analyzers (`<AnalysisMode>All</AnalysisMode>`) and run CodeQL on GitHub.
11. **Release integrity** — build releases from CI, publish SHA-256 checksums, and consider code signing so SmartScreen doesn't flag the exes.

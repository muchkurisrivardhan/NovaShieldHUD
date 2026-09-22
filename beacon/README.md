# Beacon

A Spotlight-style launcher for Windows. Resident in the tray, summoned with a global
hotkey, appears over whatever you are doing without disturbing it.

Built for Windows 11 on ARM64 (Snapdragon X), .NET 10 + WPF.

```bash
dotnet run -c Release --project beacon/Beacon.csproj
```

## What it does

| | |
|---|---|
| **Launch** | Start Menu apps and Store/UWP apps, fuzzy-matched |
| **Find files** | Every file on every fixed drive — ~309k entries, indexed in ~2s |
| **Run commands** | Window snapping, volume and brightness levels, media keys, lock/sleep/shutdown, close all apps, kill process |
| **Open sites** | 45 known sites by name, your browser bookmarks, and any typed address |
| **Calculate** | Inline arithmetic, Enter to copy |
| **Plain English** | Whole sentences — "turn the volume to 75 and dim the screen", "close all apps and shut down" — parsed on this PC |
| **Speak** | Whisper transcribes locally, and an unambiguous, safe instruction runs itself |
| **Ask Claude** | `?` followed by an instruction, executed against the command registry |
| **Your own commands** | Web searches, programs, PowerShell, paths — each with an optional global shortcut |
| **Switch windows** | Type part of an open window's title or app name and jump to it — above launching a second copy |
| **Workspaces** | A "Run program" command with several lines opens them all: "start work" |
| **Timers & reminders** | "timer 25 min", "remind me at 5 pm to call mom" — saved, so they survive a restart |
| **Quick answers** | ip, wifi, battery, disk space, uptime, cpu, ram, pc name — Enter copies |
| **Clipboard** | Copied a link or a path? Alt+Space and it's already the top row |

## The one rule that matters

Commands act on **the window you were in before Beacon opened**, never on Beacon itself.

`ForegroundTracker` captures `GetForegroundWindow()` *before* the overlay is shown and
hands that handle to every command. Without this, "snap this window left" snaps the search
bar. Everything in `WindowManager` takes that captured handle, never the current foreground.

## Layout

```
Core/         Command registry, fuzzy matcher, settings, custom commands
Interop/      Win32: hotkeys, foreground capture, window management, audio, brightness
Nl/           Sentence parser — rules that turn plain English into registry calls
Providers/    Result sources: apps, files, commands, bookmarks, web, calculator, language, AI
Ai/           Claude planner — turns a sentence into calls against the registry
UI/           Overlay window, settings window, hotkey recorder, icon caches
```

Adding a capability is one entry in `CommandRegistry`. It is declarative rather than a bare
delegate so the AI layer can read the same list and generate tool definitions from it —
there is exactly one source of truth for what the app can do.

## Design decisions worth knowing

**Plain English is parsed by rules, not by a model.** The phrasings people use on a launcher
are a small closed set — open a thing, search a thing somewhere, set a level, close things,
power off. `Nl/NlParser` matches those in microseconds, offline, for free, and identically
every time. Only what the rules cannot place is offered to Claude, one row further down. The
parser describes work and never performs it, which is what makes it safe to run on every
keystroke; `Nl/NlRunner` is the only thing that executes, step by step, in the order spoken.

**A sentence must look like an order to be read as one.** It has to begin with a verb, or
name a known site ("latest telugu songs on youtube"). Single words never qualify, so `chrome`
and `wifi_scan.py` stay ordinary searches. When one fragment of a chain cannot be understood,
the whole reading is abandoned rather than half-run.

**Exact levels need the Core Audio API and WMI.** The media keys move volume in driver-defined
steps, which cannot express "set volume to 75%", so `AudioEndpoint` sets a real scalar through
`IAudioEndpointVolume`. Brightness goes through `WmiMonitorBrightnessMethods` by late-bound
COM. Both are hand-written interop rather than NuGet packages (NAudio.Wasapi, System.Management),
which keeps the dependency list as short as the feature is small.

**Radio switches admit what they cannot do.** Toggling the Wi-Fi or Bluetooth *radio* needs
elevation or the WinRT projection; a normal process cannot. So "turn off wifi" disconnects via
netsh, which genuinely works, and Bluetooth and night light open their Settings page instead
of reporting a success that never happened.

**Everything (voidtools) is preferred when present.** It reads the NTFS master file table
and stays current from the USN journal. Beacon talks to it over its HTTP/JSON server rather
than the official SDK, because the SDK ships x86/x64 DLLs only and an ARM64 process cannot
P/Invoke an x64 DLL. Enable *Options → HTTP Server* in Everything and set the port in
Settings → Search & Index.

**The built-in index is the fallback, and it is honestly second-best.** Reading the MFT
directly needs a raw volume handle, which needs elevation; this build runs as a normal user
by design. So it walks the tree instead: ~2s to build, and only as fresh as the last
refresh (default every 30 min, or run *Rebuild File Index*).

**Paths are stored interned.** 427k files live in only 79k folders, so each directory is
stored once and entries keep `(directoryIndex, fileName)`. That is ~30 MB instead of ~140 MB.

**Search runs progressively.** Providers are queried in parallel and results render as each
one lands. Gating fast results behind a slow filesystem probe made the whole launcher feel
broken even when every part worked.

**Match quality is tiered, not additive.** An exact filename match returns a fixed high
score so it always beats a subsequence match — otherwise `beacon.csproj` surfaced
`Beacon_x_wpftmp.csproj.nuget.g.props` first. Path quality (`.venv`, `node_modules`, `obj`,
`AppData`) only reorders results *within* a tier.

**"Run program or script" means whatever Win+R means.** ShellExecute takes a program and its
arguments as two separate things, so `notepad C:\notes.txt` — one line, exactly what anyone
types into the Run box — fails outright. `Interop/ShellRun` reproduces the Run box's own
order: the whole line first (URLs, `ms-settings:`, `shell:startup`, and unquoted paths
containing spaces), then a quoted program, then the longest leading portion that exists on
disk, and only then a split at the first space. The program still goes to ShellExecute, which
is what searches PATH and the App Paths registry key — that is why `chrome` works with no path.
The one deliberate difference from Run: a `.ps1` is executed rather than opened in an editor.

**The clipboard row shows links and paths, never text.** Opening the bar with a URL or an
existing path on the clipboard offers to open it. Arbitrary copied text is deliberately not
shown: it is too often a password or a private message to put on screen unasked.

**Quick answers are local only.** There is a local IP and no public one, because finding the
public address means asking a third-party server, and a launcher should not phone anyone
because you typed two letters.

**Every row knows where it lives, and Ctrl+C copies it.** An app row shows the executable
its Start Menu shortcut points at, resolved through `IShellLink` on an STA thread *after* the
list is first published — the scan stays instant and the paths fill in behind it. Store apps
have no such path, so they show the `shell:AppsFolder` name, which is the thing you can hand
to explorer.exe or put in a shortcut. Files copy their full path, bookmarks their URL.

**The highlight only sticks when the user moved it.** Results stream in provider by provider,
so an auto-selected row had to stop being restored on every re-render: typing "brave" could
leave the bar pointing at a command that answered one keystroke earlier while the app sat on
top. Arrow keys set the selection as the user's; the next keystroke hands it back.

**Favicons are fetched from each site directly**, never via Google's or DuckDuckGo's icon
services — either would receive the list of every site you have bookmarked.

**Windows 11 Explorer tabs are not scriptable.** There is no public API to open a folder as
a tab. Reusing an already-open Explorer window via Shell COM is the closest supported
behaviour, and is the default.

**Destructive actions need a second, deliberate keystroke** — typed, spoken, or proposed by
Claude. Kill process, shutdown, close all apps, empty Recycle Bin, and any custom command
marked *Ask before running*. Dictation may run a sentence by itself only when the reading is
exact and nothing in it is destructive: misheard speech must never be why a PC shut down.

## Known limits

- Runs non-elevated, so it cannot move, close or send keys to windows owned by processes
  running as administrator (Task Manager, regedit, admin terminals). Those silently do nothing.
- Exclusive-fullscreen apps will cover the overlay. Borderless-windowed is fine.
- Light and system themes are stored but not rendered; the overlay always draws dark.
- Firefox bookmarks are not read (they live in a locked SQLite database).
- New files do not appear until the next index refresh.
- Brightness only works on displays that implement the WMI brightness class — a laptop panel,
  usually not an external monitor.
- "Close all apps" posts WM_CLOSE; an app that ignores it, or that stops to ask about unsaved
  work, stays open.

## Settings

`%APPDATA%\Beacon\settings.json`. The API key is encrypted with DPAPI scoped to your
Windows account and is never written in plaintext; `ANTHROPIC_API_KEY` takes precedence if
set. Open settings from the tray icon, or type `beacon settings`.

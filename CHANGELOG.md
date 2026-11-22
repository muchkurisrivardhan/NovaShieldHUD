# Changelog

## Unreleased

### 2026-11-22 (quick ask, drawing panel, style preview)
- **Tap Ctrl+Alt to type to Kai.** A quick tap of the talk key now opens a small box beside Kai in your launcher style: type, press Enter, and your brain answers (Shift+Enter for a new line, Esc to close, the mic button to talk instead). Holding the key still talks. Prefer the old hands-free listening on a tap? Settings > Avatar > "A quick tap of it".
- **The launcher's "?" uses your brain.** With no API key (or with Claude Code or Codex as your brain), the AI row is now "Ask Kai: …", answered by the brain chosen in Settings > Brain, your Claude subscription included, and Kai acts on your PC. No more "no API key set" dead end.
- **Kai draws in a panel, not over the whole screen.** Drawings now appear in a panel in the bottom-left corner (by default), dressed exactly like your launcher style: cut corners, scanlines and a "KAI // EXPLAINS" header for Tactical, HUD, Cyberpunk and Retro CRT; soft rounded plates for Classic and Mono; the style's own colours and fonts. Drag it by the header, resize it from the corner, and the drawing re-fits. Kai walks over beside the panel to present.
- **New Settings > Drawing page:** where the panel goes (bottom left, bottom right, left half, right half, full screen), its width, height and background; clean or hand-drawn sketch lines; drawing speed; grid and captions; how detailed the drawings are; which kind to draw when you don't say; whether Kai explains out loud; when the drawing closes; and **your own instructions** for every drawing ("Explain like I'm 12", "Always add one real-world example"...). A live preview shows the panel on a screen, and "Try a sample drawing" opens a real one with your settings.
- **Launcher style preview.** Settings > Appearance shows a live picture of the launcher next to the style list, changing as you pick a style or accent colour, before you save.

### 2026-11-20 (Kai sees the screen)
- **Kai can look at your screen and point at things.** Ask "what's this?", "where do I click to turn on backups?", "what does this error mean?", "explain this chart" or "look at my screen". Kai takes a screenshot of the screen your pointer is on (stepping out of the picture), has your brain look at it, then circles or boxes the right things on your **real** screen with numbered labels, and walks over to each one while explaining. The overlay is see-through and click-through, so you can follow along and click while Kai shows you; say "got it" (or Ctrl+Alt+N) to clear it, or it fades by itself.
- Works with Claude (API key), OpenRouter (a model that can see images), Claude Code (it may read only the screenshot) and ChatGPT through Codex (attached). The model on this PC can't see, so Kai says so. Only on such a request, never saved; turn it off in Settings > Avatar ("Let Kai look at my screen").

### 2026-11-20 (Kai teaches and rewrites)
- **Kai explains by drawing on your screen.** Say "explain…", "draw…", "how does … work", or name a picture: **mind map**, **concept map** (relationships), **timeline**, **process flow**, **cycle**, **hierarchy**, **compare**, **Venn**, **cutaway** or **3D illustration**. The screen dims into a sketchboard in your Beacon style, and Kai draws the picture part by part while talking it through: lines sketch themselves in, cards pop in, the part being explained glows while the rest steps back, and a caption shows what Kai is saying. Click for the next step, say "tell me more about…" to go deeper, and "got it", Esc or right-click to close.
- The AI only describes the picture, as checked data (never code); Beacon lays it out and draws it. Works with every brain; on this PC the model is held to the exact shape by grammar.
- **Rewrite selected text by voice.** Select text anywhere and say "fix it" (grammar, spelling, filler), "rewrite it" (the prompt ring opens at the cursor so you pick a style), "rewrite it as a research / bug report / build prompt", or ask for any change: "make it shorter", "more formal", "translate it to Spanish", "summarize it". Kai pastes the result over the selection; Ctrl+Z undoes it. Without an AI, prompt-ring styles still give you the guide.

### 2026-11-20 (push-to-talk)
- **Push-to-talk for Nova.** Hold **Ctrl + Alt** and talk; let go and Kai gets it. A quick tap listens hands-free until you pause. Kai comes up beside the cursor if away. Pressing another key while holding (Ctrl+Alt+N, Ctrl+Alt+P, Ctrl+Alt+Del...) cancels, so shortcuts keep working; AltGr never starts talking. Change it to **Right Ctrl** or turn it off in Settings > Avatar.
- **The Listening pill.** While Nova listens, a small pill rides beside your cursor with voice bars that move with your mic ("Listening · let go to send" while holding). When you stop, it shrinks to three pulsing dots until the reply starts. It wears your Beacon style: accent glow, surface and text colours, font, square-cut corners for Tactical/HUD/Cyberpunk/CRT and round for Mono/Classic. Clicks pass straight through it. It replaces the old "Listening…" speech bubble, for Ctrl+M and clicking Kai too.
- The speech model now starts loading the moment you start talking, so the reply comes sooner after you stop.

### 2026-11-20 (Kai)
- **Nova now appears as Kai**, a 3D chibi companion in a glowing cyber suit on a holo base, replacing the robot. Kai's glow, suit, hair and eyes all take your Beacon style's colour (amber Tactical, cyan HUD, pink Cyberpunk, green CRT, grey Mono), and follow the mood of the conversation.
- **Kai acts and has expressions:** waves hello when called; breathes, bobs and blinks; turns to look at your pointer; lifts a hand to the headphone with wide eyes while listening, with sound waves pulsing to your voice; puts a hand to the face under a "?" with half-closed eyes while thinking; gestures and moves the mouth while talking; makes a worried face when something went wrong; walks after the pointer when it wanders off; and cheers with ^ ^ eyes and hearts when you stroke the head. The hair strand springs as Kai moves, and the base's rings turn (faster while thinking).
- Kai is drawn by Beacon's own 3D from a model inside the app (no game engine, nothing to download). If the model can't be read, Nova shows a simple glowing orb instead.

### 2026-11-20 (later)
- **Nova is now a 3D pet beside your cursor.** `Ctrl+M` brings it up next to the pointer, listening; `Ctrl+Alt+N` (or right-click it, or say "bye") sends it away. A small robot drawn live in 3D: gunmetal head with a dark visor and glowing eyes, antenna, hovering body with a glowing core, floating hands and two rings of light, all in your Beacon style's colour. It turns its head to follow the pointer, blinks and bobs, perks up and pulses with your voice while listening, spins its rings while thinking, and bounces and lights its mouth while talking. Stroke its head with the mouse to pet it: it squints happily, wiggles and sends up hearts. It follows the pointer when it wanders off and stays put when it comes close. Replies are spoken, with a small speech bubble; approvals are answered with Yes/No in the bubble or by saying yes or no.
- **Removed:** Nova's chat window and the 2D avatar (orb, LED globe, character art). The Avatar page now has the live 3D preview, its size, following the cursor, and moods. Existing Ctrl+Alt+N bindings for Nova move to Ctrl+M.

### 2026-11-20
- **Settings, reorganised** into groups in the sidebar: **Beacon** (General, Commands, Shortcuts, Custom Commands, Search & Index, Appearance, Advanced), **Apps** (Dotline, Filee, Prompt Ring) and **AI** (Brain, Nova Assistant, Avatar, Voice, Connectors, Plain English). Nothing is set in two places any more: the on-PC AI model moved to Brain, Nova's speaking options to Voice, and email to Connectors.
- **Brain page:** "Answering now" with Nova's avatar, then **Top 10 AI** as tiles (the first two, our top picks, in green), **Claude** (your subscription through Claude Code, or an API key), **ChatGPT** (your plan through Codex), **On this PC** (download and pick a free model) and **More models** filtered by what you want to see (Free, Fast, Smartest, All). The Top 10 is checked against OpenRouter's live list; one that's gone is greyed out. Every AI option uses the same tile.
- **Avatar page** and a new avatar for Nova, drawn live in your Beacon style: an **LED globe** of dots (new default), a glowing **orb**, or a **character** with 24 poses (idle, listening, thinking, searching, alert, happy, confirmed, error...). Drop the character art in `%APPDATA%\Beacon\avatar` or `beacon/Assets/Avatar` (see the README there); its blue glow is recoloured to your style's accent automatically. With **moods** on, Nova's colour and pose follow the tone of the conversation. Size: small, default or large. The page previews every state and pose.
- **Connectors page:** email (moved here), with Calendar, Notes and tasks, and more add-ons marked as coming soon.

### 2026-11-19 (late)
- **Brain page** (Settings → Brain) for Nova: "Which AI answers you" with an Effort slider (Low, Medium, High, Max), and an Accounts tab.
  - **On this PC** (free, private), the default.
  - **OpenRouter, one click:** Connect opens OpenRouter's page in your browser, you press Authorize, and Nova is connected (OAuth with PKCE; the key is encrypted for your Windows account). Free models cost nothing; "Free Models Router" is the default. Claude, GPT, Gemini and the rest are paid from your OpenRouter balance.
  - **Claude by your own API key** (Haiku 5.5, Sonnet 5.5, Opus 5.5), billed per use to your Anthropic account.
  - **Your Claude subscription, through Claude Code:** Nova talks through the Claude Code app installed on your PC. You sign in once in Claude Code, on Anthropic's own page (Settings → Brain → Accounts → Sign in opens it). NovaShield starts the unmodified program, text only, and never sees the login. Anthropic's terms allow this ("an end user signing in to the unmodified Claude Code binary with their own Claude subscription"); it uses the plan's monthly Agent SDK credit. Pick Claude Haiku, Sonnet or Opus.
  - **Your ChatGPT plan, through Codex:** the same with OpenAI's Codex app and your ChatGPT sign-in. OpenAI's docs don't say whether another app may do this; confirm with OpenAI before relying on it in a public release.
  - Not offered: a NovaShield "Sign in with Claude" button of its own. Anthropic doesn't allow other apps to collect claude.ai logins without its approval.

### 2026-11-19 (night)
- **Nova, the talking assistant** (originally `Ctrl+Alt+N`, the "Talk to Nova" command). A panel in the corner in your Beacon style, with an orb that shows when it's listening, thinking and speaking. Talk (Whisper, on this PC) or type; Nova answers out loud with a Windows voice and, when it ends on a question, listens for your answer. It keeps the conversation, so "summarize the second one" means the email it just listed.
- **Runs on your PC, free:** the same local AI as the Prompt Ring. Qwen 3 4B is now the recommended model (Nova needs it to use tools reliably; the 1.5B one guesses). Replies are held by grammar to one JSON shape, so the model can only say something or call a real tool. The model's memory is kept between turns, so a turn costs the new words, not the whole conversation again.
- **Tools:** do anything Beacon can (open apps and sites, search, volume, timers, windows...), check the inbox, read an email to summarize it, open it in the browser, and draft a reply you send yourself. Plain commands Beacon understands exactly run at once without the model. Anything that can't be undone (shut down, close apps) shows an approval card first.
- **Email over IMAP with an app password:** free, read-only (nothing marked read, moved, deleted or sent), straight to your mail server. Gmail, Yahoo, iCloud and others are recognised from the address. Outlook and Hotmail no longer accept app passwords, so they can't be added yet. The password is encrypted for your Windows account.
- Settings → **Nova Assistant**: what's ready, voice and listening, the email account with a Test button.

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

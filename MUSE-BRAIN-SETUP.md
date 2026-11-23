# Muse brain — Nova answers through Muse Code

Beacon can use Meta's **Muse Code** as Nova's brain, the same way it already uses
Claude Code: it runs the `muse` app you installed on your PC, signed in with your
own Meta account, and reads the reply. No API key is involved anywhere — NovaShield
never sees your login, and it never sends a key. Each run is text-only, read-only
(no writes, no shell), in an empty folder, and nothing is saved afterwards.

## What you need

- **Muse Code installed** on the PC (see below).
- **Signed in** to Muse Code in your browser (see below).
- **A Muse Code plan.** Signing in alone may not be enough: Muse Code is billed
  separately from the Muse personal-agent app — its plans start at **$5/month
  (Everyday Usage, roughly 10–50 prompts every 5 hours)**. If sign-in asks for
  payment, that is the plan it wants. The moment of truth is the sign-in step: if
  it completes without asking for payment, you are covered.

## 1. Install Muse Code (Windows)

In PowerShell:

```powershell
irm https://dev.meta.ai/install.ps1 | iex
```

Then check it is there:

```powershell
muse --version
```

That puts `muse.cmd` in `%LOCALAPPDATA%\Programs\muse` and adds it to your user
PATH. If `muse` is not found in a fresh terminal, close and reopen the terminal
(or sign out and back in) so the new PATH loads.

## 2. Sign in

Either run this once in a terminal:

```powershell
muse login
```

It prints Meta's link and a short code: open the link, approve it, and the sign-in
is saved on your PC. Or, in Beacon: **Settings → Brain → Accounts → Your Muse
subscription → Sign in**, which opens the same sign-in in a console window, then
press **Check** — it should say "Signed in to Muse Code with your Muse
subscription."

## 3. Pick the Muse brain

**Settings → Brain**, in the **Muse** card, pick a model:

| Chip | What it is |
|---|---|
| Muse Spark 1.3 | The fast all-rounder. The default. |
| Muse Ember 1.0 | The lightest and cheapest. |
| Muse Wonder 1.0 | The most capable. Needs a higher plan for full use. |

Save the Brain page as usual. (Nova's effort slider maps to Muse's reasoning
effort: Low → low, Medium → medium, High → high, Max → xhigh.)

## 4. Test it

Press `Alt+Space` or `Ctrl+M` and ask Kai something, for example "what time is
it" or "open notepad". Kai should answer in Muse's voice. Then try "what's this?"
with the screen visible — Muse can look at a screenshot, so the "what's on my
screen" answers work with this brain too.

## How it works (for the curious)

Each turn runs one headless command, roughly:

```text
muse exec --json --no-session-log --max-model-steps 1 --workspace <empty temp folder>
  --disable-write --disable-shell --approval-mode on-request
  --reasoning-effort <low|medium|high|xhigh> --model <id>
  --prompt-file <system prompt + conversation>
```

The prompt file holds the system prompt and the conversation so far (as
`USER:` / `NOVA:` lines), with the instruction to reply with the JSON object
only. The reply is read from the `run.terminal.completed` event of the JSON
stream. The prompt file and any screenshot are deleted when the run ends. If
your installed Muse Code is older and rejects one of the flags, Beacon retries
once with a minimal flag set. If `META_API_KEY` or `MODEL_API_KEY` is set in
your environment (scripts or CI), Beacon strips it from the brain's process so
the browser subscription sign-in wins — an API key would otherwise outrank it.

## If it doesn't work

| What you see | What to do |
|---|---|
| "Muse Code isn't installed" | Run the install command above, then close and reopen the terminal / Beacon. |
| "Muse Code is installed but not signed in" | Press **Sign in** on the Accounts tab and approve the link in your browser. |
| Sign-in asks for payment | You need a Muse Code plan — $5/month Everyday at dev.meta.ai/billing. Your Muse personal-agent subscription does not cover it. |
| "Muse Code didn't answer" | In a terminal run `muse exec --json "say hi"` — if that fails, the problem is sign-in or plan, not Beacon. Press **Check** on the Accounts tab. |
| Kai says "Something went wrong: …" | Read the message after the colon: it is Muse Code's own error (auth, plan limit, or an old CLI). |
| Answers are slow | Drop the effort slider to Low, or pick Ember. Wonder is the slowest. |
| "I hit my plan limit" | The $5 plan allows roughly 10–50 prompts every 5 hours. Wait for the window to reset, or raise the plan at dev.meta.ai/billing. |

## Uninstalling / going back

Pick any other brain on Settings → Brain (for example "On this PC") and save.
To remove Muse Code itself, uninstall it from Settings → Apps like any program;
NovaShield never touches it otherwise.

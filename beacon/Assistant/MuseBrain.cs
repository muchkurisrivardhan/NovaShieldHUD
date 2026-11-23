using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Beacon.Assistant;

/// <summary>
/// Nova's brain through the user's own installed copy of Muse Code (their Muse
/// subscription, signed in with the browser). Mirrors <see cref="CliBrains"/>.
///
/// NovaShield only starts the unmodified program the user installed, hands it the
/// conversation as text and reads the reply. It never reads, stores or passes on a login:
/// signing in happens inside Muse Code, on Meta's own page, and each user signs in with
/// their own account. Every run is text-only (no tools, no writes, no shell), in an empty
/// folder, with nothing saved.
///
/// How it works: <c>muse exec --json --prompt-file …</c> runs one prompt to completion
/// and streams JSONL events on stdout; the final text is the
/// <c>run.terminal.completed</c> event's <c>payload.text</c>. The prompt goes in a file
/// (not on the command line) so a long conversation can't hit the command-line length
/// limit. A run is stateless (<c>--no-session-log</c>, no <c>--session-id</c>), capped at
/// one model step, and scoped to the empty temp folder (<c>--workspace</c>).
///
/// Flags were checked against Meta's own Muse Code docs (dev.meta.ai) and the
/// <c>muse-cli</c> skill mirror of <c>muse exec --help</c>. They move fast: if the
/// installed CLI rejects one, the run retries once with a minimal flag set, exactly like
/// <see cref="CliBrains"/> does for older Claude Code versions.
/// </summary>
public static class MuseBrain
{
    /// <summary>Muse models by their Model API id. Standard (non-contributor) ids only.</summary>
    public static readonly CloudBrains.Choice[] MuseModels =
    [
        new("muse-spark-1.3", "Muse Spark 1.3 (your Muse subscription)", "The fast all-rounder. Uses your Muse plan's usage, through Muse Code."),
        new("muse-ember-1.0", "Muse Ember 1.0 (your Muse subscription)", "The lightest and cheapest. Uses your Muse plan's usage, through Muse Code."),
        new("muse-wonder-1.0", "Muse Wonder 1.0 (your Muse subscription)", "The most capable. Uses your Muse plan's usage, through Muse Code."),
    ];

    /// <summary>Where the user gets Muse Code. (The codebase keeps install links in the UI file.)</summary>
    public const string InstallUrl = "https://dev.meta.ai/docs/muse-code";

    /// <summary>The program's path: the installer's place first, then PATH (where.exe).</summary>
    public static string? Find()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var native = Path.Combine(localAppData, "Programs", "muse", "muse.cmd");
        if (File.Exists(native)) return native;
        try
        {
            using var where = Process.Start(new ProcessStartInfo("where.exe", "muse")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            var lines = where?.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
            where?.WaitForExit(3000);
            return lines.FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                   ?? lines.FirstOrDefault(l => l.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Installed and signed in? With a line for the Settings page either way.
    ///
    /// Muse Code exposes no credential-status command, so this checks what a sign-in leaves
    /// behind: the credential file at %USERPROFILE%\.config\muse\auth.json (only its
    /// existence is read, never its contents). A stale or expired session still fails at
    /// run time, and that failure comes back as the reply error, so sign in again then.
    /// </summary>
    public static Task<(bool Ready, string Status)> StatusAsync(CancellationToken ct)
    {
        if (Find() is null)
            return Task.FromResult((false, "Muse Code isn't installed. Install it from dev.meta.ai, then press Sign in."));
        var auth = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "muse", "auth.json");
        return Task.FromResult(File.Exists(auth)
            ? (true, "Signed in to Muse Code with your Muse subscription.")
            : (false, "Muse Code is installed but not signed in. Press Sign in."));
    }

    /// <summary>
    /// Opens the program's own sign-in in a console window: <c>muse login</c> prints Meta's
    /// link and a short code, and the browser page does the rest.
    /// </summary>
    public static bool StartSignIn()
    {
        if (Find() is not { } exe) return false;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\" login\"") { UseShellExecute = true });
        return true;
    }

    /// <summary>
    /// One reply from the conversation, as the text the program printed. With an image (a
    /// screenshot), it goes in with --image so Muse can look at it.
    /// </summary>
    public static async Task<string> ChatAsync(string? model, string effort,
        IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct, byte[]? jpeg = null)
    {
        var exe = Find() ?? throw new InvalidOperationException(
            "Muse Code isn't installed. See Settings > Brain > Accounts.");

        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var transcript = new StringBuilder();
        foreach (var (role, content) in messages.Where(m => m.Role != "system"))
            transcript.Append(role == "assistant" ? "NOVA: " : "USER: ").AppendLine(content).AppendLine();

        var folder = Path.Combine(Path.GetTempPath(), "NovaShield", "brain");
        Directory.CreateDirectory(folder);   // nothing for the program to read, but the screenshot when there is one
        var image = Path.Combine(folder, "screen.jpg");
        if (jpeg is not null) await File.WriteAllBytesAsync(image, jpeg, ct);
        else File.Delete(image);
        var promptFile = Path.Combine(folder, "nova-muse-prompt.txt");
        try
        {
            await File.WriteAllTextAsync(promptFile,
                system +
                "\n\nDo not run commands, use tools, edit files or access the network: only reply." +
                "\n\nThe conversation so far:\n" + transcript +
                "Reply to the last USER message as Nova, with the JSON object only.", ct);
            List<string> full =
            [
                "exec", "--json", "--no-session-log", "--max-model-steps", "1",
                "--workspace", folder, "--disable-write", "--disable-shell",
                "--approval-mode", "on-request", "--reasoning-effort", Effort(effort),
                "--prompt-file", promptFile,
            ];
            if (jpeg is not null) full.AddRange(["--image", image]);
            if (!string.IsNullOrEmpty(model)) full.AddRange(["--model", model]);
            var (code, output, error) = await RunAsync(exe, full, ct, TimeSpan.FromMinutes(3), folder);
            if (code != 0 && Unknown(error))
            {
                // An older Muse Code without --disable-write, --disable-shell, --approval-mode
                // or --workspace: the same run without them. Still read-only by instruction,
                // one model step, and in an empty folder.
                List<string> minimal =
                [
                    "exec", "--json", "--no-session-log", "--max-model-steps", "1",
                    "--reasoning-effort", Effort(effort), "--prompt-file", promptFile,
                ];
                if (jpeg is not null) minimal.AddRange(["--image", image]);
                if (!string.IsNullOrEmpty(model)) minimal.AddRange(["--model", model]);
                (code, output, error) = await RunAsync(exe, minimal, ct, TimeSpan.FromMinutes(3), folder);
            }
            var text = FinalText(output);
            if (text is null) throw new InvalidOperationException("Muse Code didn't answer: " + Short(error.Length > 0 ? error : output));
            if (code != 0) throw new InvalidOperationException("Muse Code: " + Short(text));
            return text;
        }
        finally
        {
            // The screenshot and the prompt (which holds the conversation) don't stay on disk.
            try { File.Delete(image); } catch (IOException) { }
            try { File.Delete(promptFile); } catch (IOException) { }
        }
    }

    /// <summary>Nova's effort levels to Muse's --reasoning-effort levels.</summary>
    static string Effort(string effort) => effort switch
    {
        "medium" => "medium",
        "high" => "high",
        "max" => "xhigh",
        _ => "low",
    };

    /// <summary>
    /// The final text out of a --json JSONL stream: the run.terminal.completed event's
    /// payload.text. Streaming run.output.delta chunks are the fallback, concatenated.
    /// </summary>
    static string? FinalText(string output)
    {
        string? terminal = null;
        var deltas = new StringBuilder();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonObject? obj;
            try { obj = JsonNode.Parse(line) as JsonObject; }
            catch (System.Text.Json.JsonException) { continue; }
            if (obj is null) continue;
            var type = obj["payload_type"]?.GetValue<string>();
            var text = (obj["payload"] as JsonObject)?["text"]?.GetValue<string>();
            if (text is null) continue;
            if (type == "run.terminal.completed") terminal = text;
            else if (type == "run.output.delta") deltas.Append(text);
        }
        if (!string.IsNullOrWhiteSpace(terminal)) return terminal;
        return deltas.Length > 0 ? deltas.ToString() : null;
    }

    static bool Unknown(string error) =>
        error.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("unrecognized", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("not supported", StringComparison.OrdinalIgnoreCase);

    static string Short(string s) => s.Trim().Length > 300 ? s.Trim()[..300] + "…" : s.Trim();

    static async Task<(int Code, string Output, string Error)> RunAsync(string exe, IEnumerable<string> args,
        CancellationToken ct, TimeSpan timeout, string? folder = null)
    {
        // A .cmd shim (the Muse installer drops muse.cmd) has to go through cmd.exe.
        var shim = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(shim ? "cmd.exe" : exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = folder ?? Path.GetTempPath(),
        };
        if (shim) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        // An API key in the environment outranks the browser subscription sign-in, so the
        // key (if one is set for scripts or CI) must not leak into the brain's process.
        foreach (var key in new[] { "META_API_KEY", "MODEL_API_KEY" })
            psi.Environment.Remove(key);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + exe);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        process.StandardInput.Close();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        return (process.ExitCode, await output, await error);
    }
}

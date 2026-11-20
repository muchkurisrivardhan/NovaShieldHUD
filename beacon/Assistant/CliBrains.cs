using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Beacon.Assistant;

/// <summary>
/// Nova's brain through the user's own installed copy of Claude Code (their Claude
/// subscription) or OpenAI's Codex CLI (their ChatGPT plan).
///
/// NovaShield only starts the unmodified program the user installed, hands it the
/// conversation as text and reads the reply. It never reads, stores or passes on a login:
/// signing in happens inside Claude Code or Codex, on Anthropic's or OpenAI's own page, and
/// each user signs in with their own account. Every run is text-only (no tools), in an
/// empty folder, with nothing saved.
/// </summary>
public static class CliBrains
{
    public enum Cli { ClaudeCode, Codex }

    /// <summary>Claude Code's model aliases, which follow the current models.</summary>
    public static readonly CloudBrains.Choice[] ClaudeCodeModels =
    [
        new("haiku", "Claude Haiku (your Claude subscription)", "Fast. Uses your Claude plan's Agent SDK credit, through Claude Code."),
        new("sonnet", "Claude Sonnet (your Claude subscription)", "Smarter. Uses your Claude plan's Agent SDK credit, through Claude Code."),
        new("opus", "Claude Opus (your Claude subscription)", "The most capable. Uses your Claude plan's Agent SDK credit, through Claude Code."),
    ];

    static string Name(Cli cli) => cli == Cli.ClaudeCode ? "claude" : "codex";

    /// <summary>The program's path: the native installer's place first, then PATH (where.exe).</summary>
    public static string? Find(Cli cli)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = cli == Cli.ClaudeCode
            ? Path.Combine(home, ".local", "bin", "claude.exe")
            : Path.Combine(home, ".local", "bin", "codex.exe");
        if (File.Exists(native)) return native;
        try
        {
            using var where = Process.Start(new ProcessStartInfo("where.exe", Name(cli))
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

    /// <summary>Installed and signed in? With a line for the Settings page either way.</summary>
    public static async Task<(bool Ready, string Status)> StatusAsync(Cli cli, CancellationToken ct)
    {
        if (Find(cli) is not { } exe)
            return (false, cli == Cli.ClaudeCode
                ? "Claude Code isn't installed. Install it from code.claude.com, then press Sign in."
                : "Codex isn't installed. Install it from developers.openai.com/codex, then press Sign in.");
        var (code, output, error) = await RunAsync(exe, cli == Cli.ClaudeCode ? ["auth", "status"] : ["login", "status"], null, ct, TimeSpan.FromSeconds(20));
        if (cli == Cli.ClaudeCode)
        {
            var method = (JsonNode.Parse(output.Trim().Length > 0 ? output : "{}") as JsonObject)?["authMethod"]?.GetValue<string>();
            if (code != 0 || method is null or "none") return (false, "Claude Code is installed but not signed in. Press Sign in.");
            return (true, method == "claude.ai"
                ? "Signed in with your Claude subscription."
                : $"Signed in to Claude Code ({method}); it isn't your Claude subscription, so it's billed as that.");
        }
        var text = (output + error).Trim();
        var signedIn = code == 0 && text.Contains("logged in", StringComparison.OrdinalIgnoreCase)
                       && !text.Contains("not logged in", StringComparison.OrdinalIgnoreCase);
        return signedIn
            ? (true, text.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? "Signed in with your ChatGPT plan." : "Signed in to Codex: " + text)
            : (false, "Codex is installed but not signed in. Press Sign in.");
    }

    /// <summary>Opens the program's own sign-in in a console window: its browser page does the rest.</summary>
    public static bool StartSignIn(Cli cli)
    {
        if (Find(cli) is not { } exe) return false;
        var args = cli == Cli.ClaudeCode ? "auth login" : "login";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\" {args}\"") { UseShellExecute = true });
        return true;
    }

    /// <summary>
    /// One reply from the conversation, as the text the program printed. With an image (a
    /// screenshot), Claude Code may read that one file, and Codex gets it attached.
    /// </summary>
    public static async Task<string> ChatAsync(Cli cli, string? model, string effort,
        IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct, byte[]? jpeg = null)
    {
        var exe = Find(cli) ?? throw new InvalidOperationException(
            (cli == Cli.ClaudeCode ? "Claude Code" : "Codex") + " isn't installed. See Settings > Brain > Accounts.");

        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content));
        var transcript = new StringBuilder();
        foreach (var (role, content) in messages.Where(m => m.Role != "system"))
            transcript.Append(role == "assistant" ? "NOVA: " : "USER: ").AppendLine(content).AppendLine();

        var folder = Path.Combine(Path.GetTempPath(), "NovaShield", "brain");
        Directory.CreateDirectory(folder);   // nothing for the program to read, but the screenshot when there is one
        var image = Path.Combine(folder, "screen.jpg");
        if (jpeg is not null) await File.WriteAllBytesAsync(image, jpeg, ct);
        else File.Delete(image);
        try
        {
            if (cli == Cli.ClaudeCode)
            {
                var promptFile = Path.Combine(folder, "nova-system.txt");
                var see = jpeg is not null
                    ? "\n\nThe screenshot is the file screen.jpg in the current folder. Read it with the Read tool first; read nothing else."
                    : "";
                await File.WriteAllTextAsync(promptFile, system + see + "\n\nThe conversation so far is on standard input. Reply to the last USER message as Nova, with the JSON object only.", ct);
                List<string> full =
                [
                    "-p", "Reply to the conversation on standard input, as Nova, with the JSON object only.",
                    "--output-format", "json", "--system-prompt-file", promptFile,
                    // Only the screenshot may be read, and only when there is one.
                    "--tools", jpeg is not null ? "Read" : "", "--strict-mcp-config", "--no-session-persistence", "--max-turns", jpeg is not null ? "3" : "1",
                    "--safe-mode", "--effort", effort,
                ];
                if (jpeg is not null) full.AddRange(["--allowedTools", "Read(./screen.jpg)"]);
                if (!string.IsNullOrEmpty(model)) full.AddRange(["--model", model]);
                var (code, output, error) = await RunAsync(exe, full, transcript.ToString(), ct, TimeSpan.FromMinutes(3), folder);
                if (code != 0 && Unknown(error))
                {
                    // An older Claude Code without --safe-mode or --effort: the same run without them.
                    full.RemoveRange(full.IndexOf("--safe-mode"), 3);
                    (code, output, error) = await RunAsync(exe, full, transcript.ToString(), ct, TimeSpan.FromMinutes(3), folder);
                }
                var result = (JsonNode.Parse(output.Trim().Length > 0 ? output : "{}") as JsonObject)?["result"]?.GetValue<string>();
                if (result is null) throw new InvalidOperationException("Claude Code didn't answer: " + Short(error.Length > 0 ? error : output));
                if (code != 0) throw new InvalidOperationException("Claude Code: " + Short(result));
                return result;
            }
            else
            {
                var answerFile = Path.Combine(folder, "nova-codex-answer.txt");
                File.Delete(answerFile);
                List<string> args =
                [
                    "exec", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only",
                    "-c", $"model_reasoning_effort={(effort == "max" ? "high" : effort)}",
                    "-o", answerFile,
                ];
                if (!string.IsNullOrEmpty(model)) args.AddRange(["-m", model]);
                if (jpeg is not null) args.AddRange(["-i", image]);   // the screenshot, attached
                args.Add("-");   // the prompt comes on standard input
                var prompt = system + "\n\nDo not run commands or use tools: only reply.\n\n" + transcript +
                             "Reply to the last USER message as Nova, with the JSON object only.";
                var (code, output, error) = await RunAsync(exe, args, prompt, ct, TimeSpan.FromMinutes(3), folder);
                if (code != 0 && Unknown(error))
                {
                    args.RemoveRange(args.IndexOf("-c"), 2);
                    (code, output, error) = await RunAsync(exe, args, prompt, ct, TimeSpan.FromMinutes(3), folder);
                }
                if (File.Exists(answerFile)) return await File.ReadAllTextAsync(answerFile, ct);
                throw new InvalidOperationException("Codex didn't answer: " + Short(error.Length > 0 ? error : output));
            }
        }
        finally
        {
            // The screenshot doesn't stay on disk.
            try { File.Delete(image); } catch (IOException) { }
        }
    }

    static bool Unknown(string error) =>
        error.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("unexpected argument", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("unrecognized", StringComparison.OrdinalIgnoreCase);

    static string Short(string s) => s.Trim().Length > 300 ? s.Trim()[..300] + "…" : s.Trim();

    static async Task<(int Code, string Output, string Error)> RunAsync(string exe, IEnumerable<string> args, string? stdin,
        CancellationToken ct, TimeSpan timeout, string? folder = null)
    {
        // A .cmd shim (npm installs) has to go through cmd.exe.
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
        // An API key in the environment would outrank the user's subscription sign-in.
        foreach (var key in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "OPENAI_API_KEY", "CODEX_API_KEY" })
            psi.Environment.Remove(key);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + exe);
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
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

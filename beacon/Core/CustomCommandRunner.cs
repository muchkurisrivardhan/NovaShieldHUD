using System.Diagnostics;
using System.IO;
using Beacon.Interop;

namespace Beacon.Core;

/// <summary>Executes user-defined commands. Shared by the typed list, hotkeys and the AI.</summary>
public static class CustomCommandRunner
{
    /// <summary>
    /// PowerShell snippets are user code and can hang. This bounds how long the launcher
    /// will wait before reporting back, so a runaway script cannot wedge the UI thread's
    /// continuation forever.
    /// </summary>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(30);

    public static async Task<string> RunAsync(
        CustomCommand command,
        string query,
        IntPtr targetWindow,
        FolderOpenMode folderMode,
        string? customManager,
        Action<string> notify,
        CancellationToken ct = default)
    {
        var target = Substitute(command.Target, query);
        var arguments = Substitute(command.Arguments, query);

        try
        {
            switch (command.Kind)
            {
                case CustomCommandKind.WebSearch:
                    Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                    return $"Opened {command.Name}.";

                case CustomCommandKind.OpenPath:
                    return OpenPath(target, folderMode, customManager, notify, command.Name);

                case CustomCommandKind.Launch:
                    return await LaunchAsync(command, target, arguments).ConfigureAwait(false);

                case CustomCommandKind.PowerShell:
                    return await RunPowerShellAsync(command, target, ct).ConfigureAwait(false);

                default:
                    return $"Don't know how to run {command.Name}.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"{command.Name} failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Run-box semantics, one line per thing to start. A single line is an ordinary launcher;
    /// several make a workspace - "start work" opening the editor, the project folder and the
    /// usual tabs in one go. Each line is independent: one typo must not stop the rest.
    /// </summary>
    private static async Task<string> LaunchAsync(CustomCommand command, string target, string arguments)
    {
        var lines = target.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#'))   // "# comments" let a long workspace explain itself
            .ToList();

        if (lines.Count == 1)
        {
            ShellRun.Start(lines[0], arguments, command.WorkingDirectory);
            return $"Ran {command.Name}.";
        }

        var failures = new List<string>();

        foreach (var line in lines)
        {
            try
            {
                ShellRun.Start(line, workingDirectory: command.WorkingDirectory);
            }
            catch (Exception ex)
            {
                failures.Add(ex.Message);
            }

            // Starting a dozen programs in the same instant makes them fight for focus and
            // for the disk; a short stagger lets each window land in order.
            await Task.Delay(250).ConfigureAwait(false);
        }

        return failures.Count == 0
            ? $"Opened {command.Name}: {lines.Count} items."
            : $"{command.Name}: {lines.Count - failures.Count} of {lines.Count} opened. {failures[0]}";
    }

    private static string OpenPath(
        string path,
        FolderOpenMode folderMode,
        string? customManager,
        Action<string> notify,
        string name)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);

        if (Directory.Exists(expanded))
        {
            ExplorerHelper.OpenFolder(expanded, folderMode, customManager, notify);
            return $"Opened folder {expanded}.";
        }

        Process.Start(new ProcessStartInfo { FileName = expanded, UseShellExecute = true });
        return $"Opened {name}.";
    }

    private static async Task<string> RunPowerShellAsync(
        CustomCommand command, string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Environment.ExpandEnvironmentVariables(command.WorkingDirectory),
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null) return "Could not start PowerShell.";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ScriptTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return $"{command.Name} timed out after {ScriptTimeout.TotalSeconds:N0}s.";
        }

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            var detail = stderr.Length > 0 ? stderr : stdout;
            return $"{command.Name} exited with {process.ExitCode}. {Shorten(detail)}".Trim();
        }

        return stdout.Length > 0 ? Shorten(stdout) : $"{command.Name} finished.";
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>The result line is read inside a narrow search bar, so keep it to one.</summary>
    private static string Shorten(string text, int max = 240)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= max ? flattened : flattened[..(max - 1)] + "…";
    }

    public static string Substitute(string template, string query) =>
        string.IsNullOrEmpty(template)
            ? template
            : template.Replace(
                CustomCommand.QueryToken,
                // A web template needs the term escaped; anything else wants it literal.
                template.Contains("://", StringComparison.Ordinal) ? Uri.EscapeDataString(query) : query,
                StringComparison.OrdinalIgnoreCase);
}

using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Beacon.Interop;

/// <summary>
/// Runs a line the way the Windows Run box (Win+R) does.
///
/// ShellExecute on its own is close but not the same: it takes a file and arguments as two
/// separate things, so "notepad C:\notes.txt" - one line, exactly what anyone types into Run -
/// fails, because there is no program called "notepad C:\notes.txt". The Run box splits the
/// line itself, and tries the whole string as a path first so that an unquoted
/// "C:\Program Files\App\app.exe" still works. That order is reproduced here:
///
///   1. the whole line, if it is a URL, a protocol (ms-settings:, shell:startup) or something
///      that exists on disk - including paths with spaces and no quotes
///   2. a quoted program, with the remainder as arguments
///   3. the longest leading portion that exists as a file, with the rest as arguments
///   4. failing all that, split at the first space
///
/// The program itself is still handed to ShellExecute, which is what searches PATH and the
/// App Paths registry key - that is why "chrome" or "winword" work in Run without a path.
/// </summary>
internal static class ShellRun
{
    /// <summary>
    /// Starts <paramref name="input"/>. <paramref name="extraArguments"/> is appended to any
    /// arguments found in the line itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing runnable, or Windows refused.</exception>
    public static void Start(string input, string extraArguments = "", string workingDirectory = "")
    {
        var line = Environment.ExpandEnvironmentVariables((input ?? string.Empty).Trim());
        if (line.Length == 0) throw new InvalidOperationException("Nothing to run.");

        var (file, arguments) = Split(line);

        arguments = string.IsNullOrWhiteSpace(extraArguments)
            ? arguments
            : (arguments.Length == 0 ? extraArguments.Trim() : $"{arguments} {extraArguments.Trim()}");

        // .ps1 is the one place we deliberately differ from Run: double-clicking a PowerShell
        // script opens it in an editor, which is never what someone meant by "run this".
        if (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\" {arguments}".Trim();
            file = "powershell.exe";
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                WorkingDirectory = Environment.ExpandEnvironmentVariables(workingDirectory ?? string.Empty),
                UseShellExecute = true,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new InvalidOperationException(
                $"Windows could not find \u201c{file}\u201d. Check the name, or give the full path.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The UAC prompt was dismissed; not an error worth dressing up.
            throw new InvalidOperationException("Cancelled.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }

    /// <summary>Splits a Run-style line into the thing to start and its arguments.</summary>
    public static (string File, string Arguments) Split(string line)
    {
        // 1. Whole line: URLs, protocols, and anything that exists as typed.
        if (IsWholeTarget(line)) return (line, string.Empty);

        // 2. "C:\Program Files\App\app.exe" -flag
        if (line.StartsWith('"'))
        {
            var close = line.IndexOf('"', 1);
            if (close > 1) return (line[1..close], line[(close + 1)..].Trim());
        }

        // 3. Unquoted path with spaces: take the longest prefix that is a real file.
        for (var i = line.LastIndexOf(' '); i > 0; i = line.LastIndexOf(' ', i - 1))
        {
            var candidate = line[..i].Trim();
            if (candidate.Length == 0) continue;

            if (File.Exists(candidate) || File.Exists(candidate + ".exe"))
                return (candidate, line[i..].Trim());
        }

        // 4. Plain "program arguments".
        var space = line.IndexOf(' ');
        return space < 0 ? (line, string.Empty) : (line[..space], line[(space + 1)..].Trim());
    }

    /// <summary>True when the entire line is the thing to open, arguments and all.</summary>
    private static bool IsWholeTarget(string line)
    {
        if (line.Contains("://", StringComparison.Ordinal)) return true;

        // A protocol such as ms-settings: or shell:startup - but not a drive letter (one
        // character before the colon) and not a quoted path, whose leading " is not a
        // legal scheme character even though it sits in the same place.
        var colon = line.IndexOf(':');
        if (colon > 1 && IsScheme(line.AsSpan(..colon))) return true;

        var bare = line.Trim('"');
        return File.Exists(bare) || Directory.Exists(bare);
    }

    private static bool IsScheme(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        }

        return text.Length > 0;
    }
}

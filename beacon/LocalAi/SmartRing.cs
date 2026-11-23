using System.IO;
using Beacon.Core;

namespace Beacon.LocalAi;

/// <summary>
/// The one shortcut behind both rings. It copies the selection once and routes by what was
/// selected:
/// <list type="bullet">
/// <item>prose text → the prompt ring (shape it into a prompt),</item>
/// <item>code text → the Code Ring, pasted back in place,</item>
/// <item>a file (Explorer, the desktop) → read it and run the Code Ring on its contents, with
/// the result shown in a read-only window (nothing is written to disk).</item>
/// </list>
/// The clipboard save/restore and the busy guard live here, so the two rings only do their own
/// ring + model + paste.
/// </summary>
public sealed class SmartRing(Settings settings, Action<string> notify)
{
    readonly PromptRing _prompt = new(settings, notify);
    readonly CodeRing _code = new(settings, notify);
    bool _busy;

    /// <summary>Files the ring will read as text; anything else is treated as binary and skipped.</summary>
    const long MaxFileBytes = 1_000_000;

    public async Task RunAsync(IntPtr target)
    {
        if (_busy) return;
        _busy = true;
        SelectionTransfer.Snapshot? saved = null;
        try
        {
            await SelectionTransfer.WaitForModifiersReleasedAsync();
            saved = SelectionTransfer.Save();
            var (changed, text, files) = await SelectionTransfer.CopySelectionRichAsync();

            if (!changed)
            {
                notify("Select some text, code, or a file first, then press the ring shortcut.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                if (CodeHeuristics.LooksLikeCode(text))
                    await _code.RunWithTextAsync(target, text);
                else
                    await _prompt.RunWithTextAsync(target, text);
            }
            else if (files.Count > 0)
            {
                await RunForFileAsync(files);
            }
            else
            {
                notify("Couldn't read that selection as text. Select some text, code, or a code file.");
            }
        }
        finally
        {
            if (saved is not null) SelectionTransfer.Restore(saved);
            _busy = false;
        }
    }

    /// <summary>The first selected file, read as text and handed to the Code Ring.</summary>
    async Task RunForFileAsync(IReadOnlyList<string> files)
    {
        var path = files[0];
        var name = Path.GetFileName(path);
        if (files.Count > 1)
            notify($"Using the first selected file: {name}");

        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            notify("That doesn't look like a file the ring can read.");
            return;
        }
        if (!info.Exists) { notify($"Can't find {name}."); return; }
        if (info.Length > MaxFileBytes) { notify($"{name} is too big for the ring (over 1 MB)."); return; }

        string code;
        try { code = await File.ReadAllTextAsync(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notify($"Couldn't read {name} ({e.Message}).");
            return;
        }

        if (LooksBinary(code))
        {
            notify($"{name} looks like a binary file, not code or text.");
            return;
        }

        await _code.RunForFileAsync(path, code);
    }

    /// <summary>A NUL byte early on is the quickest tell that a file is binary, not text.</summary>
    static bool LooksBinary(string text)
    {
        var n = Math.Min(text.Length, 8000);
        for (var i = 0; i < n; i++)
            if (text[i] == '\0') return true;
        return false;
    }
}

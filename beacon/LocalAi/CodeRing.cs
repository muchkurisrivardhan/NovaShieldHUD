using System.IO;
using System.Text.RegularExpressions;
using Beacon.Core;
using Beacon.UI;

namespace Beacon.LocalAi;

/// <summary>
/// The Code Ring round trip: copy the selected code, show the ring, run the chosen action on
/// the free model on this PC, and paste the result back over the selection. Ctrl+Z undoes it.
///
/// A sibling of <see cref="PromptRing"/>, sharing the same ring window and selection plumbing.
/// The one difference is that code needs a model: without one there is no sensible rules-only
/// fallback, so the user is pointed at the download instead. The core (ring + model + paste)
/// is split out as <see cref="RunWithTextAsync"/> so the <see cref="SmartRing"/> router can run
/// it on a selection it has already copied.
/// </summary>
public sealed partial class CodeRing(Settings settings, Action<string> notify)
{
    readonly LocalLlm _llm = LocalLlm.Shared;
    bool _busy;

    public async Task RunAsync(IntPtr target)
    {
        if (_busy) return;
        _busy = true;
        SelectionTransfer.Snapshot? saved = null;
        try
        {
            await SelectionTransfer.WaitForModifiersReleasedAsync();
            saved = SelectionTransfer.Save();
            var text = (await SelectionTransfer.CopySelectionAsync()).Trim();
            if (text.Length == 0)
            {
                notify("Select some code (or an error message) first, then press the Code Ring shortcut.");
                return;
            }
            await RunWithTextAsync(target, text);
        }
        finally
        {
            if (saved is not null) SelectionTransfer.Restore(saved);
            _busy = false;
        }
    }

    /// <summary>
    /// The ring, the model run and the paste, on text already on the clipboard. The caller owns
    /// the clipboard save/restore and the busy guard.
    /// </summary>
    internal async Task RunWithTextAsync(IntPtr target, string text)
    {
        var done = await RunCoreAsync(text);
        if (done is { } d) await SelectionTransfer.PasteAsync(target, d.Result);
    }

    /// <summary>
    /// A selected file: the same ring and model run, but the result is shown in a read-only
    /// window (an Explorer selection has no caret to paste into). Nothing is written unless the
    /// user picks Save, which opens a dialog pre-filled with a new sibling name.
    /// </summary>
    internal async Task RunForFileAsync(string path, string code)
    {
        var fileName = Path.GetFileName(path);
        var done = await RunCoreAsync(code);
        if (done is { } d)
            new UI.RingResultWindow(settings, d.Style.Label + "  ·  " + fileName, d.Result,
                saveDir: Path.GetDirectoryName(path), saveName: SuggestName(path, d.Style.Id)).Show();
    }

    /// <summary>A default name for Save: a new sibling of the original, never the original itself.</summary>
    static string SuggestName(string path, string actionId)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);   // includes the dot, or "" if none
        return actionId switch
        {
            "fix" => stem + ".fixed" + ext,
            "comment" => stem + ".commented" + ext,
            "simplify" => stem + ".simplified" + ext,
            "types" => stem + ".typed" + ext,
            "tests" => stem + ".tests" + ext,
            "explain" => stem + ".explained.md",
            "review" => stem + ".review.md",
            "regex" => stem + ".regex.txt",
            _ => stem + ".result" + ext,
        };
    }

    /// <summary>
    /// Shows the ring, runs the chosen action on this PC, and returns the chosen action and its
    /// cleaned result — or null when the user cancelled, there is no model, or it came back
    /// empty (each already reported to the user). No paste or window: the caller places it.
    /// </summary>
    async Task<(PromptStyle Style, string Result)?> RunCoreAsync(string text)
    {
        if (text.Length > 16_000)
        {
            notify("That selection is too long for the Code Ring (over 16,000 characters).");
            return null;
        }

        var model = LocalModels.Usable(settings.LocalModel);
        if (model is null)
        {
            notify("The Code Ring needs a local AI model. Download one in Settings > Prompt Ring.");
            return null;
        }

        var ring = new PromptRingWindow(settings, text, hasModel: true, CodeStyles.All, "Code", "working");
        ring.Show();
        var style = await ring.Choice;
        if (style is null) return null;
        ring.ShowWorking(style, true);

        string result;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            // Off the UI thread: sampling is CPU work that would freeze the ring's spinner.
            result = await Task.Run(() => _llm.CompleteAsync(LocalModels.PathFor(model),
                CodeStyles.SystemPrompt(style), CodeStyles.UserMessage(text), maxTokens: 2000, timeout.Token));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ring.Close();
            notify("The local AI couldn't do that (" + e.Message + ").");
            return null;
        }

        result = StripFences(result).Trim();
        ring.Close();
        if (result.Length == 0)
        {
            notify("The local AI returned nothing. Try a smaller selection or a different action.");
            return null;
        }
        return (style, result);
    }

    /// <summary>
    /// Small models sometimes wrap the answer in a ```lang ... ``` block despite the
    /// instruction not to. If the whole reply is one fenced block, unwrap it so clean code is
    /// pasted; otherwise leave it alone (it may be code that legitimately contains backticks).
    /// </summary>
    static string StripFences(string text)
    {
        var t = text.Trim();
        var m = FullFence().Match(t);
        return m.Success ? m.Groups[1].Value : t;
    }

    [GeneratedRegex(@"\A```[^\n]*\n(.*?)\n?```\z", RegexOptions.Singleline)]
    private static partial Regex FullFence();
}

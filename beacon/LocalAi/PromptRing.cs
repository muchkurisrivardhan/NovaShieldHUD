using System.Windows;
using Beacon.Core;
using Beacon.UI;

namespace Beacon.LocalAi;

/// <summary>
/// The whole prompt-ring round trip: copy the selection, show the ring, rewrite the text in
/// the chosen style on this PC, paste it back over the selection, give the clipboard back.
/// Ctrl+Z in the app undoes the paste.
/// </summary>
public sealed class PromptRing(Settings settings, Action<string> notify)
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
                notify("Select the text you dictated first, then press the prompt ring shortcut.");
                return;
            }
            if (text.Length > 12_000)
            {
                notify("That selection is too long to rewrite (over 12,000 characters).");
                return;
            }

            var model = LocalModels.Usable(settings.LocalModel);
            var ring = new PromptRingWindow(settings, text, model is not null);
            ring.Show();
            var style = await ring.Choice;
            if (style is null) return;
            ring.ShowWorking(style, model is not null);

            string result;
            if (model is null)
            {
                result = PromptStyles.Template(style, text);
            }
            else
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    // Off the UI thread: sampling is CPU work that would freeze the ring's spinner.
                    result = await Task.Run(() => _llm.CompleteAsync(LocalModels.PathFor(model), PromptStyles.SystemPrompt(style),
                        PromptStyles.UserMessage(text), maxTokens: 1200, timeout.Token));
                    if (string.IsNullOrWhiteSpace(result)) result = PromptStyles.Template(style, text);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    notify("The local AI couldn't rewrite it (" + e.Message + "), so you got the guide instead.");
                    result = PromptStyles.Template(style, text);
                }
            }

            ring.Close();
            await SelectionTransfer.PasteAsync(target, result);
        }
        finally
        {
            if (saved is not null) SelectionTransfer.Restore(saved);
            _busy = false;
        }
    }
}

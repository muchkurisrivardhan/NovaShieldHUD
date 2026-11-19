using System.Speech.Synthesis;
using System.Windows;
using Beacon.Assistant;
using Beacon.Core;
using Beacon.LocalAi;

namespace Beacon.UI;

/// <summary>The Nova Assistant page: readiness, voice, and the email account.</summary>
public partial class SettingsWindow
{
    private void InitAssistantPage()
    {
        try
        {
            using var synth = new SpeechSynthesizer();
            AssistantVoicePicker.ItemsSource = synth.GetInstalledVoices().Where(v => v.Enabled)
                .Select(v => v.VoiceInfo.Name).ToList();
        }
        catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AssistantVoicePicker.IsEnabled = AssistantVoiceTest.IsEnabled = false;
        }

        AssistantVoiceTest.Click += (_, _) =>
        {
            try
            {
                var synth = new SpeechSynthesizer();
                if (AssistantVoicePicker.SelectedItem is string name) synth.SelectVoice(name);
                synth.SpeakCompleted += (_, _) => synth.Dispose();
                synth.SpeakAsync("Hi, I'm Nova. You've got two new emails. Want me to summarize them?");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        };

        AssistantGoModel.Click += (_, _) => ShowPage(PageLocalAi);
        AssistantGoVoice.Click += (_, _) => ShowPage(PageVoice);
        AssistantOpen.Click += async (_, _) =>
        {
            if (_registry.Find(Settings.AssistantCommandId) is { } command)
                await command.RunAsync(new ActionContext { TargetWindow = IntPtr.Zero, Dismiss = () => { }, Notify = _ => { } });
        };

        EmailTest.Click += async (_, _) =>
        {
            SaveEmailFields();
            _settings.Save();
            EmailTest.IsEnabled = false;
            EmailStatus.Text = "Signing in…";
            var error = await new EmailInbox(_settings.EmailAccount).TestAsync(CancellationToken.None);
            EmailStatus.Text = error ?? "Signed in. Nova can read your inbox.";
            EmailTest.IsEnabled = true;
            RefreshEmailState();
        };
        EmailForget.Click += (_, _) =>
        {
            _settings.EmailAddress = "";
            _settings.EmailImapHost = "";
            _settings.SetEmailPassword(null);
            _settings.Save();
            EmailAddress.Text = EmailServer.Text = "";
            EmailPassword.Clear();
            EmailStatus.Text = "Removed.";
            RefreshEmailState();
        };
    }

    private void LoadAssistantPage()
    {
        AssistantSpeaks.IsChecked = _settings.AssistantSpeaks;
        AssistantListens.IsChecked = _settings.AssistantListensForReply;
        AssistantVoicePicker.SelectedItem = _settings.AssistantVoice is { } v
            && AssistantVoicePicker.ItemsSource is IEnumerable<string> voices && voices.Contains(v) ? v : null;
        if (AssistantVoicePicker.SelectedItem is null && AssistantVoicePicker.Items.Count > 0) AssistantVoicePicker.SelectedIndex = 0;
        EmailAddress.Text = _settings.EmailAddress;
        EmailServer.Text = _settings.EmailImapHost;
        RefreshEmailState();

        var model = LocalModels.ForAssistant(_settings.LocalModel);
        var voice = Voice.WhisperModels.IsDownloaded(Voice.WhisperModels.Find(_settings.VoiceModel));
        AssistantReadiness.Text =
            (model is null
                ? "✗ AI model: not downloaded. Get Qwen 3 4B on the Prompt Ring page; until then Nova only runs plain commands."
                : model.Id == "qwen3-4b"
                    ? "✓ AI model: Qwen 3 4B."
                    : "! AI model: " + model.Label + " is small for conversation. Qwen 3 4B is much better at it.") + "\n" +
            (voice
                ? "✓ Speech model: ready, so Nova listens when it opens."
                : "✗ Speech model: not downloaded (Voice page). You can still type to Nova.") + "\n" +
            (_settings.EmailAccount() is null
                ? "– Email: not set up (below). Optional."
                : "✓ Email: " + _settings.EmailAddress + ".");
    }

    private void RefreshEmailState() =>
        EmailPasswordState.Text = _settings.EmailPasswordProtected is null
            ? "Not saved yet."
            : "Saved (encrypted). Type a new one only to change it.";

    private void SaveEmailFields()
    {
        _settings.EmailAddress = EmailAddress.Text.Trim();
        _settings.EmailImapHost = EmailServer.Text.Trim();
        if (EmailPassword.Password.Length > 0)
        {
            _settings.SetEmailPassword(EmailPassword.Password);
            EmailPassword.Clear();
        }
    }

    private void SaveAssistantPage()
    {
        _settings.AssistantSpeaks = AssistantSpeaks.IsChecked == true;
        _settings.AssistantListensForReply = AssistantListens.IsChecked == true;
        _settings.AssistantVoice = AssistantVoicePicker.SelectedItem as string;
        SaveEmailFields();
    }
}

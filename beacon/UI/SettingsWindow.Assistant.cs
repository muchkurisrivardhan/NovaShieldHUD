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

        AssistantGoModel.Click += (_, _) => ShowPage(PageBrain);
        AssistantGoVoice.Click += (_, _) => ShowPage(PageVoice);
        AssistantOpen.Click += async (_, _) =>
        {
            if (_registry.Find(Settings.AssistantCommandId) is { } command)
                await command.RunAsync(new ActionContext { TargetWindow = IntPtr.Zero, Dismiss = () => { }, Notify = _ => { } });
        };

        SentinelView.Click += (_, _) =>
            new AuditLogWindow(_settings, AuditLog.Shared.Entries()) { Owner = this }.Show();
        SentinelClear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Delete Nova's activity log? This can't be undone.", "Clear activity log",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            AuditLog.Shared.Clear();
            RefreshSentinelSummary();
        };

        WatchersOn.Click += (_, _) => WatcherList.IsEnabled = WatchersOn.IsChecked == true;

        McpOn.Click += (_, _) => McpPanel.IsEnabled = McpOn.IsChecked == true;
        McpCopy.Click += (_, _) => CopyText(McpSnippet.Text, McpCopy);
        McpCopyClaudeCode.Click += (_, _) => CopyText($"claude mcp add novashield -- \"{BeaconPath}\" --mcp", McpCopyClaudeCode);
        McpForgetAgents.Click += (_, _) =>
        {
            _settings.McpAllowedAgents.Clear();
            _settings.Save();
            RefreshMcpAgents();
        };

        MemoryView.Click += (_, _) =>
        {
            var window = new MemoryWindow(_settings, MemoryStore.Shared) { Owner = this };
            window.Closed += (_, _) => RefreshMemorySummary();
            window.Show();
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

        WatchersOn.IsChecked = _settings.WatchersEnabled;
        WatcherList.IsEnabled = _settings.WatchersEnabled;
        WatchRemindersBox.IsChecked = _settings.WatchReminders;
        WatchBriefingBox.IsChecked = _settings.WatchBriefing;
        BriefingTimeBox.Text = _settings.BriefingTime;
        QuietFromBox.Text = _settings.QuietFrom;
        QuietToBox.Text = _settings.QuietTo;

        TeamExpertPicker.SelectedItem = TeamExpertPicker.Items.OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == _settings.TeamExpert) ?? TeamExpertPicker.Items[0];
        var ready = AgentTeam.Experts.Where(id => AgentTeam.Available(id, _settings)).Select(AgentTeam.Name).ToList();
        var auto = AgentTeam.Automatic(_settings, id => AgentTeam.Available(id, _settings));
        TeamStatus.Text = ready.Count == 0
            ? "No cloud brain is set up yet, so there's no expert to ask. Add Claude, Muse or ChatGPT on the Brain page, under Accounts."
            : $"Set up on this PC: {string.Join(", ", ready)}." + (auto is null ? "" : $" Automatic means {AgentTeam.Name(auto)}.");

        McpOn.IsChecked = _settings.McpEnabled;
        McpPanel.IsEnabled = _settings.McpEnabled;
        McpPc.IsChecked = _settings.McpAllowPc;
        McpReminders.IsChecked = _settings.McpAllowReminders;
        McpMemory.IsChecked = _settings.McpAllowMemory;
        McpEmail.IsChecked = _settings.McpAllowEmail;
        McpSnippet.Text =
            "{\n  \"mcpServers\": {\n    \"novashield\": {\n      \"command\": " + System.Text.Json.JsonSerializer.Serialize(BeaconPath) +
            ",\n      \"args\": [\"--mcp\"]\n    }\n  }\n}";
        RefreshMcpAgents();

        var model = LocalModels.ForAssistant(_settings.LocalModel);
        var voice = Voice.WhisperModels.IsDownloaded(Voice.WhisperModels.Find(_settings.VoiceModel));
        AssistantReadiness.Text =
            (model is null
                ? "✗ AI model: not downloaded. Pick a brain on the Brain page; until then Nova only runs plain commands."
                : model.Id == "qwen3-4b"
                    ? "✓ AI model: Qwen 3 4B."
                    : "! AI model: " + model.Label + " is small for conversation. Qwen 3 4B is much better at it.") + "\n" +
            (voice
                ? "✓ Speech model: ready, so Nova listens when it opens."
                : "✗ Speech model: not downloaded (Voice page). You can still type to Nova.") + "\n" +
            (_settings.EmailAccount() is null
                ? "– Email: not set up (Connectors page). Optional."
                : "✓ Email: " + _settings.EmailAddress + ".");
        RefreshSentinelSummary();
        RefreshMemorySummary();
    }

    private void RefreshMemorySummary()
    {
        var items = MemoryStore.Shared.All();
        var local = items.Count(m => m.Private);
        MemorySummary.Text = items.Count == 0
            ? "Nova doesn't remember anything yet."
            : $"Nova remembers {items.Count} thing{(items.Count == 1 ? "" : "s")}" +
              (local > 0 ? $", {local} of them on this PC only." : ".") +
              $" Latest: “{items[^1].Text}”";
    }

    private void RefreshSentinelSummary()
    {
        var entries = AuditLog.Shared.Entries();
        if (entries.Count == 0)
        {
            SentinelSummary.Text = "Activity log: empty. Kept on this PC only, encrypted, for 30 days.";
            return;
        }
        var asked = entries.Count(e => e.Outcome is "Approved by you" or "Declined by you");
        var blocked = entries.Count(e => e.Outcome == "Blocked");
        var last = entries[^1];
        SentinelSummary.Text =
            $"Activity log: {entries.Count} actions in the last 30 days ({asked} asked you, {blocked} blocked). " +
            $"Latest: {last.Action}, {last.Outcome.ToLowerInvariant()}, {last.Time:MMM d, h:mm tt}.";
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

        _settings.WatchersEnabled = WatchersOn.IsChecked == true;
        _settings.WatchReminders = WatchRemindersBox.IsChecked == true;
        _settings.WatchBriefing = WatchBriefingBox.IsChecked == true;
        // A time that doesn't parse keeps the old one rather than switching a watcher off by accident.
        string Time(string text, string old) => WatcherHub.Clock(text) is { } t ? t.ToString("HH:mm") : old;
        _settings.BriefingTime = Time(BriefingTimeBox.Text, _settings.BriefingTime);
        _settings.QuietFrom = Time(QuietFromBox.Text, _settings.QuietFrom);
        _settings.QuietTo = Time(QuietToBox.Text, _settings.QuietTo);
        if (TeamExpertPicker.SelectedItem is System.Windows.Controls.ComboBoxItem { Tag: string expert }) _settings.TeamExpert = expert;

        _settings.McpEnabled = McpOn.IsChecked == true;
        _settings.McpAllowPc = McpPc.IsChecked == true;
        _settings.McpAllowReminders = McpReminders.IsChecked == true;
        _settings.McpAllowMemory = McpMemory.IsChecked == true;
        _settings.McpAllowEmail = McpEmail.IsChecked == true;
    }

    /// <summary>The program agents start: this Beacon.exe, wherever it's installed.</summary>
    static string BeaconPath => Environment.ProcessPath ?? "Beacon.exe";

    private void RefreshMcpAgents() =>
        McpAgents.Text = _settings.McpAllowedAgents.Count == 0
            ? "No agent allowed yet. The first time one connects, a card asks you."
            : "Allowed: " + string.Join(", ", _settings.McpAllowedAgents) + ".";

    static void CopyText(string text, System.Windows.Controls.Button button)
    {
        try
        {
            Clipboard.SetText(text);
            button.Content = "Copied";
        }
        catch (System.Runtime.InteropServices.ExternalException) { button.Content = "Try again"; }
    }
}

using System.IO;
using System.Net.Http;
using System.Windows;
using Beacon.Assistant;
using Beacon.LocalAi;

namespace Beacon.UI;

/// <summary>The Brain page: which AI answers Nova, how hard it thinks, and the accounts behind it.</summary>
public partial class SettingsWindow
{
    private sealed record BrainChoice(string Kind, string Model, string Label, string Note)
    {
        public override string ToString() => Label;
    }

    private CancellationTokenSource? _openRouterCts;

    private void InitBrainPage()
    {
        BrainTabDefault.Click += (_, _) => ShowBrainTab(accounts: false);
        BrainTabAccounts.Click += (_, _) => ShowBrainTab(accounts: true);
        BrainPicker.SelectionChanged += (_, _) =>
            BrainNote.Text = (BrainPicker.SelectedItem as BrainChoice)?.Note ?? "";

        OpenRouterConnect.Click += async (_, _) =>
        {
            _openRouterCts?.Cancel();
            var cts = _openRouterCts = new CancellationTokenSource();
            OpenRouterConnect.IsEnabled = false;
            OpenRouterStatus.Text = "Waiting for you to press Authorize in the browser…";
            try
            {
                var key = await CloudBrains.ConnectOpenRouterAsync(cts.Token);
                _settings.SetOpenRouterKey(key);
                _settings.Save();
                OpenRouterStatus.Text = "Connected.";
                await FillBrainPickerAsync();
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException or HttpRequestException
                                           or InvalidDataException or System.Net.HttpListenerException or System.Net.Sockets.SocketException)
            {
                OpenRouterStatus.Text = e.Message;
            }
            finally
            {
                OpenRouterConnect.IsEnabled = true;
                RefreshOpenRouterState();
            }
        };
        OpenRouterDisconnect.Click += (_, _) =>
        {
            _settings.SetOpenRouterKey(null);
            _settings.Save();
            OpenRouterStatus.Text = "Disconnected. To revoke the key too, delete it at openrouter.ai/settings/keys.";
            RefreshOpenRouterState();
        };

        BrainClaudeSave.Click += (_, _) =>
        {
            if (BrainClaudeKey.Password.Length == 0) return;
            _settings.SetApiKey(BrainClaudeKey.Password);
            _settings.Save();
            BrainClaudeKey.Clear();
            BrainClaudeStatus.Text = "Saved (encrypted for your Windows account).";
        };
        BrainClaudeTest.Click += async (_, _) =>
        {
            if (_settings.ApiKey is not { Length: > 0 } key) { BrainClaudeStatus.Text = "Save a key first."; return; }
            BrainClaudeStatus.Text = "Testing…";
            try
            {
                var reply = await CloudBrains.ClaudeAsync(key, "claude-haiku-5-5", "low",
                    [("system", "Reply with {\"say\": \"ok\", \"tool\": null, \"args\": {}}"), ("user", "test")], CancellationToken.None);
                BrainClaudeStatus.Text = reply.Contains("ok", StringComparison.OrdinalIgnoreCase) ? "Works. Claude answered." : "Claude answered, oddly: " + reply;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                BrainClaudeStatus.Text = "Didn't work: " + e.Message;
            }
        };
        ClaudeCodeSignIn.Click += (_, _) => SignInCli(CliBrains.Cli.ClaudeCode, ClaudeCodeStatus);
        CodexSignIn.Click += (_, _) => SignInCli(CliBrains.Cli.Codex, CodexStatus);
        ClaudeCodeCheck.Click += async (_, _) => await CheckCliAsync(CliBrains.Cli.ClaudeCode, ClaudeCodeStatus);
        CodexCheck.Click += async (_, _) => await CheckCliAsync(CliBrains.Cli.Codex, CodexStatus);
        ClaudeCodeInstall.Click += (_, _) => OpenUrl("https://code.claude.com/docs/en/setup");
        CodexInstall.Click += (_, _) => OpenUrl("https://developers.openai.com/codex/cli");
        Closed += (_, _) => _openRouterCts?.Cancel();
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private void SignInCli(CliBrains.Cli cli, System.Windows.Controls.TextBlock status)
    {
        status.Text = CliBrains.StartSignIn(cli)
            ? "A window opened: follow it, and press Authorize on the page your browser shows. Then press Check."
            : (cli == CliBrains.Cli.ClaudeCode ? "Claude Code" : "Codex") + " isn't installed yet: press Get, install it, then Sign in.";
    }

    private async Task CheckCliAsync(CliBrains.Cli cli, System.Windows.Controls.TextBlock status)
    {
        status.Text = "Checking…";
        try { status.Text = (await CliBrains.StatusAsync(cli, CancellationToken.None)).Status; }
        catch (Exception e) when (e is not OutOfMemoryException) { status.Text = "Couldn't check: " + e.Message; }
    }

    private void ShowBrainTab(bool accounts)
    {
        BrainTabDefault.IsChecked = !accounts;
        BrainTabAccounts.IsChecked = accounts;
        BrainDefault.Visibility = accounts ? Visibility.Collapsed : Visibility.Visible;
        BrainAccounts.Visibility = accounts ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadBrainPage()
    {
        BrainEffort.Value = Math.Max(0, Array.IndexOf(CloudBrains.EffortLevels, _settings.BrainEffort));
        BrainClaudeStatus.Text = _settings.ApiKey is { Length: > 0 } ? "A key is saved." : "No key yet.";
        RefreshOpenRouterState();
        _ = FillBrainPickerAsync();
        _ = CheckCliAsync(CliBrains.Cli.ClaudeCode, ClaudeCodeStatus);
        _ = CheckCliAsync(CliBrains.Cli.Codex, CodexStatus);
    }

    private void RefreshOpenRouterState()
    {
        var connected = _settings.OpenRouterKey is { Length: > 0 };
        OpenRouterDisconnect.IsEnabled = connected;
        if (OpenRouterConnect.IsEnabled && OpenRouterStatus.Text is "" or "Connected." or "Not connected.")
            OpenRouterStatus.Text = connected ? "Connected." : "Not connected.";
    }

    private async Task FillBrainPickerAsync()
    {
        var local = LocalModels.ForAssistant(_settings.LocalModel);
        var choices = new List<BrainChoice>
        {
            new("local", "", "On this PC: " + (local?.Label ?? "no model downloaded yet"),
                local is null ? "Free and private. Download Qwen 3 4B on the Prompt Ring page first." : "Free and private: nothing leaves this PC."),
        };
        choices.AddRange(CliBrains.ClaudeCodeModels.Select(m => new BrainChoice("claudecode", m.Id, m.Label, m.Note + " Sign in on the Accounts tab.")));
        choices.Add(new BrainChoice("codex", "", "ChatGPT (your ChatGPT plan)", "Through OpenAI's Codex app, with your Codex model. Sign in on the Accounts tab."));
        choices.AddRange(CloudBrains.ClaudeModels.Select(m => new BrainChoice("claude", m.Id, m.Label + " (API key)", m.Note + " Needs your API key (Accounts).")));

        if (_settings.OpenRouterKey is { Length: > 0 })
        {
            try
            {
                var models = await CloudBrains.OpenRouterModelsAsync(CancellationToken.None);
                choices.AddRange(models.Select(m => new BrainChoice("openrouter", m.Id, "OpenRouter: " + m.Label, m.Note)));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (_settings.BrainOpenRouterModel.Length > 0)
                    choices.Add(new BrainChoice("openrouter", _settings.BrainOpenRouterModel, "OpenRouter: " + _settings.BrainOpenRouterModel, "Couldn't load the model list right now."));
            }
        }

        BrainPicker.ItemsSource = choices;
        BrainPicker.SelectedItem = choices.FirstOrDefault(c => c.Kind == _settings.Brain
            && (c.Kind is "local" or "codex" || c.Model == c.Kind switch
            {
                "claude" => _settings.BrainClaudeModel,
                "claudecode" => _settings.BrainClaudeCodeModel,
                _ => _settings.BrainOpenRouterModel,
            }))
            ?? choices[0];
    }

    private void SaveBrainPage()
    {
        if (BrainPicker.SelectedItem is BrainChoice choice)
        {
            _settings.Brain = choice.Kind;
            if (choice.Kind == "claude") _settings.BrainClaudeModel = choice.Model;
            if (choice.Kind == "openrouter") _settings.BrainOpenRouterModel = choice.Model;
            if (choice.Kind == "claudecode") _settings.BrainClaudeCodeModel = choice.Model;
        }
        _settings.BrainEffort = CloudBrains.EffortLevels[(int)Math.Clamp(BrainEffort.Value, 0, 3)];
    }
}

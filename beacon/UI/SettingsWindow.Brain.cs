using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Beacon.Assistant;
using Beacon.LocalAi;

namespace Beacon.UI;

/// <summary>The Brain page: which AI answers Nova, how hard it thinks, and the accounts behind it.</summary>
public partial class SettingsWindow
{
    private sealed record BrainChoice(string Kind, string Model, string Label, string Note);

    /// <summary>
    /// The Top 10, through OpenRouter: the best-known models from each maker, ours to order.
    /// Each is checked against OpenRouter's live list; one that's gone is shown greyed out.
    /// </summary>
    private static readonly (string Id, string Name, string Maker)[] TopTen =
    [
        ("anthropic/claude-sonnet-5.5", "Claude Sonnet 5.5", "Anthropic"),
        ("openai/gpt-6.2-sol", "GPT-6.2 Sol", "OpenAI"),
        ("google/gemini-3.1-pro-preview", "Gemini 3.1 Pro", "Google"),
        ("anthropic/claude-opus-5.5", "Claude Opus 5.5", "Anthropic"),
        ("x-ai/grok-4.7", "Grok 4.7", "xAI"),
        ("deepseek/deepseek-v4-pro", "DeepSeek V4 Pro", "DeepSeek"),
        ("google/gemini-3.8-flash", "Gemini 3.8 Flash", "Google"),
        ("moonshotai/kimi-k3", "Kimi K3", "Moonshot"),
        ("qwen/qwen4-max", "Qwen4 Max", "Alibaba"),
        ("mistralai/mistral-large-4-0", "Mistral Large 4", "Mistral"),
    ];

    private BrainChoice _brainChoice = new("local", "", "", "");
    private readonly List<(Button Chip, BrainChoice Choice)> _brainChips = [];
    private List<CloudBrains.Choice> _openRouterModels = [];
    private string _brainPreference = "free";

    private CancellationTokenSource? _openRouterCts;

    private void InitBrainPage()
    {
        BrainTabDefault.Click += (_, _) => ShowBrainTab(accounts: false);
        BrainTabAccounts.Click += (_, _) => ShowBrainTab(accounts: true);
        BrainMore.SelectionChanged += (_, _) =>
        {
            if (BrainMore.SelectedItem is CloudBrains.Choice m)
                SelectBrain(new BrainChoice("openrouter", m.Id, m.Label, m.Note + OpenRouterHint()));
        };
        PromptRingGoBrain.Click += (_, _) => ShowPage(PageBrain);
        BuildBrainChips();

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
                await LoadOpenRouterModelsAsync();
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
        MuseSignIn.Click += (_, _) => SignInMuse();
        MuseCheck.Click += async (_, _) => await CheckMuseAsync();
        MuseInstall.Click += (_, _) => OpenUrl(MuseBrain.InstallUrl);
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

    private void SignInMuse()
    {
        MuseStatus.Text = MuseBrain.StartSignIn()
            ? "A window opened: it shows Meta's link and a short code — open the link, approve it, then press Check."
            : "Muse Code isn't installed yet: press Get, install it, then Sign in.";
    }

    private async Task CheckMuseAsync()
    {
        MuseStatus.Text = "Checking…";
        try { MuseStatus.Text = (await MuseBrain.StatusAsync(CancellationToken.None)).Status; }
        catch (Exception e) when (e is not OutOfMemoryException) { MuseStatus.Text = "Couldn't check: " + e.Message; }
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
        BrainAvatar.FollowSettings(_settings);
        _brainChoice = CurrentBrain();
        BuildBrainChips();
        _ = LoadOpenRouterModelsAsync();
        _ = CheckCliAsync(CliBrains.Cli.ClaudeCode, ClaudeCodeStatus);
        _ = CheckCliAsync(CliBrains.Cli.Codex, CodexStatus);
        _ = CheckMuseAsync();
    }

    private void RefreshOpenRouterState()
    {
        var connected = _settings.OpenRouterKey is { Length: > 0 };
        OpenRouterDisconnect.IsEnabled = connected;
        if (OpenRouterConnect.IsEnabled && OpenRouterStatus.Text is "" or "Connected." or "Not connected.")
            OpenRouterStatus.Text = connected ? "Connected." : "Not connected.";
    }

    /// <summary>What the saved settings say answers now.</summary>
    private BrainChoice CurrentBrain() => _settings.Brain switch
    {
        "claude" => new("claude", _settings.BrainClaudeModel, CloudBrains.ClaudeModels.FirstOrDefault(m => m.Id == _settings.BrainClaudeModel)?.Label ?? _settings.BrainClaudeModel, "With your API key."),
        "claudecode" => new("claudecode", _settings.BrainClaudeCodeModel, CliBrains.ClaudeCodeModels.FirstOrDefault(m => m.Id == _settings.BrainClaudeCodeModel)?.Label ?? "Claude", "With your Claude subscription, through Claude Code."),
        "codex" => new("codex", "", "ChatGPT", "With your ChatGPT plan, through Codex."),
        "muse" => new("muse", _settings.BrainMuseModel, MuseBrain.MuseModels.FirstOrDefault(m => m.Id == _settings.BrainMuseModel)?.Label ?? "Muse", "With your Muse subscription, through Muse Code."),
        "openrouter" => new("openrouter", _settings.BrainOpenRouterModel,
            TopTen.FirstOrDefault(t => t.Id == _settings.BrainOpenRouterModel).Name ?? _settings.BrainOpenRouterModel, "Through OpenRouter." + OpenRouterHint()),
        _ => new("local", _settings.LocalModel, "On this PC: " + (LocalModels.ForAssistant(_settings.LocalModel)?.Label ?? "no model yet"), "Free and private: nothing leaves this PC."),
    };

    private string OpenRouterHint() =>
        _settings.OpenRouterKey is { Length: > 0 } ? "" : " Connect OpenRouter on the Accounts tab to use it.";

    /// <summary>Every option as the same kind of tile; the top two Top 10 tiles in the success colour.</summary>
    private void BuildBrainChips()
    {
        _brainChips.Clear();
        BrainTop10.Children.Clear();
        BrainClaudeSub.Children.Clear();
        BrainClaudeApi.Children.Clear();
        BrainChatGpt.Children.Clear();
        BrainMuseSub.Children.Clear();
        BrainLocalChips.Children.Clear();
        BrainPrefs.Children.Clear();

        var live = _openRouterModels.Select(m => m.Id).ToHashSet();
        for (var i = 0; i < TopTen.Length; i++)
        {
            var (id, name, maker) = TopTen[i];
            var top = i < 2;
            var chip = AddChip(BrainTop10, new BrainChoice("openrouter", id, name, $"{maker}, through OpenRouter." + OpenRouterHint()),
                name, top ? $"#{i + 1}  ·  top pick" : $"#{i + 1}  ·  {maker}", top);
            if (live.Count > 0 && !live.Contains(id))
            {
                chip.IsEnabled = false;
                chip.ToolTip = "Not offered by OpenRouter right now.";
            }
        }

        foreach (var m in CliBrains.ClaudeCodeModels)
            AddChip(BrainClaudeSub, new BrainChoice("claudecode", m.Id, m.Label.Replace(" (your Claude subscription)", ""), m.Note),
                m.Label.Replace(" (your Claude subscription)", ""), "your subscription");
        foreach (var m in CloudBrains.ClaudeModels)
            AddChip(BrainClaudeApi, new BrainChoice("claude", m.Id, m.Label, m.Note + " With your API key."), m.Label, m.Note.Split(':')[0]);
        AddChip(BrainChatGpt, new BrainChoice("codex", "", "ChatGPT", "With your ChatGPT plan, through OpenAI's Codex app."), "ChatGPT", "your ChatGPT plan");
        foreach (var m in MuseBrain.MuseModels)
            AddChip(BrainMuseSub, new BrainChoice("muse", m.Id, m.Label.Replace(" (your Muse subscription)", ""), m.Note),
                m.Label.Replace(" (your Muse subscription)", ""), "your subscription");

        foreach (var m in LocalModels.All)
        {
            var downloaded = LocalModels.IsDownloaded(m);
            var chip = AddChip(BrainLocalChips, new BrainChoice("local", m.Id, "On this PC: " + m.Label, "Free and private: nothing leaves this PC."),
                m.Label, downloaded ? "downloaded" : "download below first");
            chip.IsEnabled = downloaded;
        }

        foreach (var (key, label) in new[] { ("free", "Free"), ("fast", "Fast"), ("smart", "Smartest"), ("all", "All") })
        {
            var pref = new Button
            {
                Style = (Style)FindResource("AiChip"), MinWidth = 0, Content = label,
                Tag = key == _brainPreference ? "on" : null,
            };
            pref.Click += (_, _) => { _brainPreference = key; BuildBrainChips(); FillMoreModels(); };
            BrainPrefs.Children.Add(pref);
        }

        ShowBrainChoice();
    }

    private Button AddChip(Panel panel, BrainChoice choice, string title, string sub, bool top = false)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = sub, FontSize = 11, Opacity = 0.75 });
        var chip = new Button { Style = (Style)FindResource(top ? "AiChipTop" : "AiChip"), Content = content };
        chip.Click += (_, _) => SelectBrain(choice);
        panel.Children.Add(chip);
        _brainChips.Add((chip, choice));
        return chip;
    }

    private void SelectBrain(BrainChoice choice)
    {
        _brainChoice = choice;
        if (choice.Kind == "local" && choice.Model.Length > 0) _settings.LocalModel = choice.Model;
        ShowBrainChoice();
    }

    private void ShowBrainChoice()
    {
        foreach (var (chip, c) in _brainChips)
            chip.Tag = c.Kind == _brainChoice.Kind && (c.Model == _brainChoice.Model || c.Kind == "codex") ? "on" : null;
        BrainCurrent.Text = _brainChoice.Label;
        BrainNote.Text = _brainChoice.Note;
    }

    private async Task LoadOpenRouterModelsAsync()
    {
        try { _openRouterModels = await CloudBrains.OpenRouterModelsAsync(CancellationToken.None); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            BrainMoreNote.Text = "Couldn't load OpenRouter's list right now.";
            return;
        }
        BuildBrainChips();
        FillMoreModels();
    }

    /// <summary>OpenRouter's models, narrowed to what the user asked to see.</summary>
    private void FillMoreModels()
    {
        static bool Has(string id, params string[] words) => words.Any(w => id.Contains(w, StringComparison.OrdinalIgnoreCase));
        var list = _openRouterModels.Where(m => _brainPreference switch
        {
            "free" => m.Note.Contains("free, no cost", StringComparison.Ordinal),
            "fast" => Has(m.Id, "flash", "mini", "haiku", "lite", "nano", "small", "fast", "air", "turbo"),
            "smart" => Has(m.Id, "opus", "-pro", "max", "large", "ultra", "sonnet", "reasoner", "r1"),
            _ => true,
        }).ToList();
        BrainMore.ItemsSource = list;
        BrainMore.DisplayMemberPath = "Label";
        BrainMore.SelectedItem = list.FirstOrDefault(m => _brainChoice.Kind == "openrouter" && m.Id == _brainChoice.Model);
        BrainMoreNote.Text = $"{list.Count} models" + (_settings.OpenRouterKey is { Length: > 0 } ? "." : ". Connect OpenRouter on the Accounts tab to use them.");
    }

    private void SaveBrainPage()
    {
        var c = _brainChoice;
        _settings.Brain = c.Kind;
        if (c.Kind == "claude") _settings.BrainClaudeModel = c.Model;
        if (c.Kind == "claudecode") _settings.BrainClaudeCodeModel = c.Model;
        if (c.Kind == "muse") _settings.BrainMuseModel = c.Model;
        if (c.Kind == "openrouter") _settings.BrainOpenRouterModel = c.Model;
        if (c.Kind == "local" && c.Model.Length > 0) _settings.LocalModel = c.Model;
        _settings.BrainEffort = CloudBrains.EffortLevels[(int)Math.Clamp(BrainEffort.Value, 0, 3)];
    }
}

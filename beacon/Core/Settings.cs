using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Interop;

namespace Beacon.Core;

/// <summary>
/// User-editable configuration, persisted to %APPDATA%\Beacon\settings.json.
/// </summary>
public sealed class Settings
{
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beacon");

    public static string FilePath => Path.Combine(Directory, "settings.json");
    public static string StatsPath => Path.Combine(Directory, "usage.json");

    // ---------------------------------------------------------------- hotkey

    /// <summary>Modifier mask, as the MOD_* constants. Defaults to Alt.</summary>
    public uint HotkeyModifiers { get; set; } = NativeMethods.MOD_ALT;

    /// <summary>Virtual-key code. Defaults to Space, giving Alt+Space.</summary>
    public uint HotkeyVirtualKey { get; set; } = 0x20;

    // ----------------------------------------------------------------- files

    /// <summary>Everything's HTTP server port. Its own default is 80.</summary>
    public int EverythingPort { get; set; } = 80;

    /// <summary>
    /// Folders to index. Empty means every fixed drive, which is the default: a launcher
    /// that cannot find a file outside your profile is not really a file search.
    /// </summary>
    public List<string> IndexRoots { get; set; } = [];

    /// <summary>
    /// Folder names skipped anywhere in the tree.
    ///
    /// Deliberately minimal: the point of the index is to find everything, so the default
    /// excludes only things that are not user files by definition. Measured on this machine,
    /// these three remove 148,000 entries - almost all of it WinSxS, the Windows component
    /// store, which is a hardlink farm of OS internals nobody searches for. Everything else,
    /// including node_modules and build output, is indexed unless you add it here.
    ///
    /// Common additions, if the index gets noisy:
    ///   node_modules, .git, __pycache__, .venv, venv, obj, bin, target, dist
    /// Those four alone account for a further ~120,000 entries on this machine.
    /// </summary>
    public List<string> ExcludedFolders { get; set; } =
    [
        "WinSxS",
        "$Recycle.Bin",
        "System Volume Information",
    ];

    /// <summary>Hidden and system files are OS internals; off by default.</summary>
    public bool IncludeHiddenAndSystem { get; set; }

    /// <summary>
    /// How often to rebuild. Without the USN journal there is no way to notice new files,
    /// so a periodic walk is the only way the index stays honest. 0 disables refreshing.
    /// </summary>
    public int IndexRefreshMinutes { get; set; } = 30;

    // ------------------------------------------------------------- appearance

    public int MaxResults { get; set; } = 9;

    public bool RunAtStartup { get; set; } = true;

    /// <summary>"dark", "light" or "system".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>
    /// Look of the launcher bar: "tactical" (gunmetal and amber), "hud" (cyan holographic) or
    /// "classic" (rounded, neutral).
    /// </summary>
    public string OverlayStyle { get; set; } = "tactical";

    public string AccentColor { get; set; } = "#5B8DEF";

    public double WindowWidth { get; set; } = 620;

    /// <summary>Vertical placement as a percentage of the work area height.</summary>
    public double TopOffsetPercent { get; set; } = 20;

    /// <summary>Multiplier on every font size in the overlay.</summary>
    public double FontScale { get; set; } = 1.0;

    public bool ShowFileIcons { get; set; } = true;

    public bool ShowResultSubtitles { get; set; } = true;

    /// <summary>Row padding: "comfortable" or "compact".</summary>
    public string Density { get; set; } = "comfortable";

    // ---------------------------------------------------------------- dotline

    /// <summary>Start Dotline, the desktop widgets, whenever Beacon starts.</summary>
    public bool LaunchDotline { get; set; } = true;

    // ------------------------------------------------------------------ filee

    /// <summary>Start Filee, the drag-and-drop converter, whenever Beacon starts.</summary>
    public bool LaunchFilee { get; set; } = true;

    /// <summary>Filee wears Beacon's style and accent (written to Filee's theme.json).</summary>
    public bool FileeFollowsLook { get; set; } = true;

    // ---------------------------------------------------------------- browsing

    /// <summary>
    /// Icons are fetched from each site directly rather than via Google's or DuckDuckGo's
    /// favicon endpoints, so no third party receives the list of sites you have bookmarked.
    /// "direct" or "off".
    /// </summary>
    public string FaviconMode { get; set; } = "direct";

    public bool ShowFavicons => !string.Equals(FaviconMode, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>"reuse", "new" or "custom".</summary>
    public string FolderOpenMode { get; set; } = "reuse";

    [JsonIgnore]
    public Interop.FolderOpenMode FolderOpenModeValue => FolderOpenMode.ToLowerInvariant() switch
    {
        "new" => Interop.FolderOpenMode.NewWindow,
        "custom" => Interop.FolderOpenMode.CustomManager,
        _ => Interop.FolderOpenMode.ReuseWindow,
    };

    /// <summary>Command line for the chosen file manager; {path} is substituted.</summary>
    public string CustomFileManager { get; set; } = string.Empty;

    // ------------------------------------------------------------------ voice

    /// <summary>Shows the microphone button in the bar.</summary>
    public bool VoiceEnabled { get; set; } = true;

    /// <summary>Whisper model id; see Voice.WhisperModels. Runs locally.</summary>
    public string VoiceModel { get; set; } = "base.en";

    /// <summary>waveIn device index; -1 means the Windows default.</summary>
    public int VoiceDevice { get; set; } = -1;

    /// <summary>Stop listening automatically when you stop talking.</summary>
    public bool VoiceAutoStop { get; set; } = true;

    /// <summary>
    /// Run what you dictated as soon as Beacon is sure of it, instead of leaving it in the
    /// box for you to press Enter. Only ever applies to an unambiguous, non-destructive
    /// reading: anything that shuts down, closes or deletes still waits for a keystroke.
    /// </summary>
    public bool VoiceAutoRun { get; set; } = true;

    // ------------------------------------------------------- natural language

    /// <summary>
    /// Understand whole sentences ("turn the volume to 75 and dim the screen") locally,
    /// without a model. Off leaves plain name matching plus the explicit "?" path to Claude.
    /// </summary>
    public bool NaturalLanguage { get; set; } = true;

    // --------------------------------------------------------- custom commands

    public List<CustomCommand> CustomCommands { get; set; } = [];

    /// <summary>
    /// Global chords bound to built-in commands, keyed by command id. Custom commands
    /// carry their own binding on the command itself.
    ///
    /// An absent key means "never configured", so a command may claim a default chord; a
    /// key holding an empty binding means the user cleared it, and it stays cleared.
    /// </summary>
    public Dictionary<string, HotkeyBinding> BuiltInHotkeys { get; set; } = [];

    /// <summary>Alt+M, the twin of Alt+Space: one opens the bar to type, one to speak.</summary>
    public const string VoiceCommandId = "voice_search";

    public void ApplyDefaultShortcuts()
    {
        BuiltInHotkeys.TryAdd(VoiceCommandId, new HotkeyBinding
        {
            Modifiers = NativeMethods.MOD_ALT,
            VirtualKey = 0x4D,   // M
        });

        // Added once: a binding the user clears afterwards stays cleared (see PromptRingOffered).
        // Ctrl+M brings Nova up beside the cursor; Ctrl+Alt+N sends it away. Ctrl+Alt+N used to
        // open Nova's chat window: that binding becomes Ctrl+M, unless the user picked their own.
        if (!NovaPetOffered)
        {
            if (!BuiltInHotkeys.TryGetValue(AssistantCommandId, out var old) || !old.IsSet
                || (old.Modifiers == (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT) && old.VirtualKey == 0x4E))
                BuiltInHotkeys[AssistantCommandId] = new HotkeyBinding { Modifiers = NativeMethods.MOD_CONTROL, VirtualKey = 0x4D };   // M
            BuiltInHotkeys.TryAdd(NovaDismissCommandId, new HotkeyBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                VirtualKey = 0x4E,   // N
            });
            NovaPetOffered = true;
            AssistantOffered = true;
        }

        if (!PromptRingOffered)
        {
            BuiltInHotkeys.TryAdd(PromptRingCommandId, new HotkeyBinding
            {
                Modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                VirtualKey = 0x50,   // P
            });
            PromptRingOffered = true;
        }
    }

    public const string PromptRingCommandId = "prompt_ring";
    public const string AssistantCommandId = "nova_assistant";

    public const string NovaDismissCommandId = "nova_dismiss";

    /// <summary>From before the pet: kept so old settings files still read.</summary>
    public bool AssistantOffered { get; set; }

    /// <summary>Set once Ctrl+M and Ctrl+Alt+N have been given to the pet, so changing them sticks.</summary>
    public bool NovaPetOffered { get; set; }

    /// <summary>Set once Ctrl+Alt+P has been given to the prompt ring, so clearing it sticks.</summary>
    public bool PromptRingOffered { get; set; }

    // -------------------------------------------------------------- assistant

    /// <summary>
    /// Which brain answers Nova: "local" (this PC), "claude" (API key), "openrouter",
    /// "claudecode" (the user's Claude Code sign-in) or "codex" (the user's Codex sign-in).
    /// </summary>
    public string Brain { get; set; } = "local";

    public string BrainClaudeModel { get; set; } = "claude-haiku-5-5";

    /// <summary>Claude Code model alias: "haiku", "sonnet" or "opus".</summary>
    public string BrainClaudeCodeModel { get; set; } = "haiku";

    /// <summary>"openrouter/free" picks among OpenRouter's free models by itself.</summary>
    public string BrainOpenRouterModel { get; set; } = "openrouter/free";

    /// <summary>"low", "medium", "high" or "max". Higher is smarter but slower (cloud brains).</summary>
    public string BrainEffort { get; set; } = "low";

    /// <summary>The key OpenRouter handed over when the user clicked Authorize, encrypted (DPAPI).</summary>
    public string? OpenRouterKeyProtected { get; set; }

    public void SetOpenRouterKey(string? key) => OpenRouterKeyProtected = string.IsNullOrWhiteSpace(key)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));

    [JsonIgnore]
    public string? OpenRouterKey
    {
        get
        {
            if (string.IsNullOrWhiteSpace(OpenRouterKeyProtected)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(OpenRouterKeyProtected), null, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
        }
    }

    /// <summary>Nova's 3D pet floats after the cursor (when it wanders off); off: it stays where it appeared.</summary>
    public bool PetFollowsCursor { get; set; } = true;

    /// <summary>"small", "default" or "large": the pet's size on screen.</summary>
    public string AvatarSize { get; set; } = "default";

    /// <summary>
    /// Push-to-talk for Nova: "ctrl+alt" (hold Ctrl+Alt to talk, let go to send; tap to listen
    /// hands-free), "rctrl" (the same with Right Ctrl), or "off".
    /// </summary>
    public string PushToTalk { get; set; } = "ctrl+alt";

    /// <summary>The pet's glow follows the tone of the conversation.</summary>
    public bool AvatarMoods { get; set; } = true;

    /// <summary>Speak Nova's replies aloud.</summary>
    public bool AssistantSpeaks { get; set; } = true;

    /// <summary>After Nova asks a question, listen for the answer without a key press.</summary>
    public bool AssistantListensForReply { get; set; } = true;

    /// <summary>Which Windows voice speaks (its name), or null for the default.</summary>
    public string? AssistantVoice { get; set; }

    public string EmailAddress { get; set; } = "";

    /// <summary>IMAP server; empty means the one EmailInbox.HostFor picks from the address.</summary>
    public string EmailImapHost { get; set; } = "";

    public int EmailImapPort { get; set; } = 993;

    /// <summary>The app password, encrypted for this Windows user (DPAPI), like the API key.</summary>
    public string? EmailPasswordProtected { get; set; }

    public void SetEmailPassword(string? plaintext) => EmailPasswordProtected = string.IsNullOrWhiteSpace(plaintext)
        ? null
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext.Trim()), null, DataProtectionScope.CurrentUser));

    /// <summary>The account to sign in with, or null when it isn't set up (or can't be decrypted here).</summary>
    public Assistant.EmailAccount? EmailAccount()
    {
        if (string.IsNullOrWhiteSpace(EmailAddress) || string.IsNullOrWhiteSpace(EmailPasswordProtected)) return null;
        var host = string.IsNullOrWhiteSpace(EmailImapHost) ? Assistant.EmailInbox.HostFor(EmailAddress) : EmailImapHost.Trim();
        if (host is null) return null;
        try
        {
            var password = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(EmailPasswordProtected), null, DataProtectionScope.CurrentUser));
            // Google shows app passwords in groups of four ("abcd efgh ijkl mnop"); spaces are not part of it.
            if (host == "imap.gmail.com") password = password.Replace(" ", "");
            return new Assistant.EmailAccount(EmailAddress.Trim(), password, host, EmailImapPort is > 0 and < 65536 ? EmailImapPort : 993);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    // -------------------------------------------------------------- local ai

    /// <summary>Which free local model the prompt ring uses (LocalModels id).</summary>
    public string LocalModel { get; set; } = "qwen3-4b";

    // -------------------------------------------------------------------- ai

    public bool AiEnabled { get; set; } = true;

    /// <summary>
    /// Model id. Defaults to Haiku 4.5 - the cheapest and fastest of the current family,
    /// and more than capable of mapping one sentence onto a known command list.
    /// </summary>
    public string AiModel { get; set; } = AiModels.Default;

    /// <summary>
    /// Every destructive command still requires an explicit confirmation keystroke before
    /// it runs, no matter what the model proposed. Turning this off is deliberately
    /// possible but not recommended - one misread sentence can then kill the wrong process.
    /// </summary>
    public bool ConfirmDestructiveAiActions { get; set; } = true;

    /// <summary>Upper bound on tool-call round trips, so a confused model can't loop forever.</summary>
    public int AiMaxTurns { get; set; } = 6;

    /// <summary>
    /// DPAPI-protected API key, base64 encoded. Never the plaintext key - this file sits in
    /// a roaming profile, and CurrentUser-scoped DPAPI ties it to this Windows account.
    /// </summary>
    [JsonPropertyName("apiKeyProtected")]
    public string? ApiKeyProtected { get; set; }

    // ------------------------------------------------------------ key access

    /// <summary>
    /// Resolves the key, preferring the environment variable so the settings file can stay
    /// empty on machines where the key is already provisioned.
    /// </summary>
    [JsonIgnore]
    public string? ApiKey
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

            if (string.IsNullOrWhiteSpace(ApiKeyProtected)) return null;

            try
            {
                var cipher = Convert.FromBase64String(ApiKeyProtected);
                var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // Copied between machines or user accounts: DPAPI can no longer open it.
                return null;
            }
        }
    }

    public void SetApiKey(string? plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            ApiKeyProtected = null;
            return;
        }

        var cipher = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext.Trim()), null, DataProtectionScope.CurrentUser);

        ApiKeyProtected = Convert.ToBase64String(cipher);
    }

    // -------------------------------------------------------------- persistence

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded is not null)
                {
                    loaded.ApplyDefaultShortcuts();
                    return loaded;
                }
            }
        }
        catch
        {
            // A malformed settings file should not stop the launcher from starting, but keep it
            // aside rather than let the fresh defaults below overwrite the user's settings.
            try { File.Copy(FilePath, Path.Combine(Directory, $"settings.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true); }
            catch { }
        }

        var fresh = new Settings();
        fresh.ApplyDefaultShortcuts();
        fresh.Save();
        return fresh;
    }

    /// <summary>Write beside, then swap in: a crash or power cut mid-write can't leave a torn file.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            WriteAtomic(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Read-only profile or locked file; settings simply won't persist this session.
        }
    }
}

/// <summary>A global chord, stored in a form that survives serialisation.</summary>
public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }

    [JsonIgnore]
    public bool IsSet => Modifiers != 0 && VirtualKey != 0;
}

/// <summary>
/// The models offered in settings. Ids are exact - the API rejects date-suffixed variants.
/// </summary>
public static class AiModels
{
    public const string Haiku45 = "claude-haiku-4-5";
    public const string Sonnet5 = "claude-sonnet-5";
    public const string Opus5 = "claude-opus-5";

    public const string Default = Haiku45;

    public static readonly (string Id, string Label, string Note)[] All =
    [
        (Haiku45, "Claude Haiku 4.5", "Fastest and cheapest. Ample for mapping a sentence onto a command."),
        (Sonnet5, "Claude Sonnet 5", "Better at ambiguous, multi-step requests. Slower, costs more."),
        (Opus5, "Claude Opus 5", "Most capable. Overkill for most launcher requests."),
    ];

    /// <summary>
    /// Haiku 4.5 rejects output_config.effort, so the caller must know whether to send it.
    /// </summary>
    public static bool SupportsEffort(string model) =>
        !string.Equals(model, Haiku45, StringComparison.OrdinalIgnoreCase);

    public static string LabelFor(string id) =>
        All.FirstOrDefault(m => m.Id == id).Label ?? id;
}

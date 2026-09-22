namespace Beacon.Core;

public enum CustomCommandKind
{
    /// <summary>A URL template containing {query}.</summary>
    WebSearch,

    /// <summary>An executable or script, with arguments.</summary>
    Launch,

    /// <summary>A PowerShell snippet whose output is shown in the bar.</summary>
    PowerShell,

    /// <summary>A named shortcut to a file or folder.</summary>
    OpenPath,
}

/// <summary>
/// A command the user defined in settings.
///
/// Stored as plain data so the whole set round-trips through settings.json, and so the
/// same record can feed the typed search list, an optional global hotkey, and the AI tool
/// list without three separate representations.
/// </summary>
public sealed class CustomCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..12];

    public string Name { get; set; } = string.Empty;

    /// <summary>Shown under the name, and given to the model as the tool description.</summary>
    public string Description { get; set; } = string.Empty;

    public CustomCommandKind Kind { get; set; } = CustomCommandKind.WebSearch;

    /// <summary>
    /// URL template, executable path, script body, or filesystem path depending on
    /// <see cref="Kind"/>. May contain the {query} placeholder.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string Glyph { get; set; } = "";

    /// <summary>Space-separated extra words that should match this command.</summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>
    /// Requires a second, deliberate Enter before running, and an explicit approval when
    /// the AI proposes it. Defaults on for PowerShell, which can do arbitrary damage.
    /// </summary>
    public bool RequiresConfirmation { get; set; }

    public bool ExposeToAi { get; set; } = true;

    // ---- optional global hotkey ----

    public uint HotkeyModifiers { get; set; }
    public uint HotkeyVirtualKey { get; set; }

    public bool HasHotkey => HotkeyModifiers != 0 && HotkeyVirtualKey != 0;

    /// <summary>True when the command needs something typed after its name.</summary>
    public bool TakesQuery =>
        Target.Contains(QueryToken, StringComparison.OrdinalIgnoreCase) ||
        Arguments.Contains(QueryToken, StringComparison.OrdinalIgnoreCase);

    public const string QueryToken = "{query}";

    public CustomCommand Clone() => (CustomCommand)MemberwiseClone();

    /// <summary>Null when valid; otherwise the reason it cannot be saved.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Give the command a name.";
        if (string.IsNullOrWhiteSpace(Target))
        {
            return Kind switch
            {
                CustomCommandKind.WebSearch => "Enter a URL, using {query} where the search term goes.",
                CustomCommandKind.Launch => "Choose a program or script to run.",
                CustomCommandKind.PowerShell => "Enter the PowerShell to run.",
                _ => "Choose a file or folder to open.",
            };
        }

        if (Kind == CustomCommandKind.WebSearch &&
            !Target.Contains("://", StringComparison.Ordinal))
        {
            return "The URL needs a scheme, for example https://";
        }

        return null;
    }
}

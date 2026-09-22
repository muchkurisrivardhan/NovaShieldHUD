namespace Beacon.Core;

public enum ParameterType
{
    String,
    Number,
    Boolean,
}

/// <summary>
/// One argument a command accepts. Doubles as the JSON-schema source for AI tool use,
/// which is why descriptions here are written for a model to read, not a human.
/// </summary>
public sealed class CommandParameter
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public ParameterType Type { get; init; } = ParameterType.String;
    public bool Required { get; init; } = true;
    /// <summary>When set, the model must pick one of these values.</summary>
    public string[]? Choices { get; init; }
}

/// <summary>
/// Everything Beacon can do is one of these. Deliberately a plain object rather than a
/// plugin interface: adding a capability is a ~15-line entry in <see cref="CommandRegistry"/>.
///
/// The payoff for making it declarative instead of a bare delegate is that the AI layer can
/// read the same list and turn it into tool definitions, so there is exactly one source of
/// truth for "what can this app do".
/// </summary>
public sealed class BeaconCommand
{
    /// <summary>snake_case. Used verbatim as the AI tool name, so it must be stable.</summary>
    public required string Id { get; init; }

    /// <summary>Shown in the results list.</summary>
    public required string Title { get; init; }

    public string Subtitle { get; init; } = "";

    /// <summary>Written for the model: when should it reach for this?</summary>
    public required string Description { get; init; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; init; } = "";

    /// <summary>Extra words that should match this command in search but aren't in the title.</summary>
    public string[] Keywords { get; init; } = [];

    /// <summary>
    /// Irreversible or disruptive. Gated behind an explicit confirmation step when the AI
    /// proposes it, per the app's safety policy.
    /// </summary>
    public bool IsDestructive { get; init; }

    /// <summary>Set false for commands that only make sense typed, not driven by a model.</summary>
    public bool ExposeToAi { get; init; } = true;

    /// <summary>
    /// Set false for commands that exist only as AI tools. Apps and files already have their
    /// own providers, so surfacing "Launch Application" as a typed row would just duplicate them.
    /// </summary>
    public bool ShowInSearch { get; init; } = true;

    public CommandParameter[] Parameters { get; init; } = [];

    /// <summary>
    /// Row titles for a command the typed list fans out into one row per choice. Without
    /// these, "Mute / Unmute" with choices on|off would render as "Mute / Unmute On".
    /// </summary>
    public IReadOnlyDictionary<string, string>? ChoiceTitles { get; init; }

    /// <summary>Grouping for the Settings command library ("Window", "System", ...).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Runs the command. The returned string is fed back to the model as the tool result,
    /// so it should describe what actually happened ("Snapped Visual Studio Code left").
    /// </summary>
    public required Func<ActionContext, Task<string>> RunAsync { get; init; }

    public bool TakesArguments => Parameters.Length > 0;
}

/// <summary>
/// What a command gets when it runs. Crucially carries <see cref="TargetWindow"/> — the
/// window the user was in before Beacon appeared — not whatever is focused right now.
/// </summary>
public sealed class ActionContext
{
    public required IntPtr TargetWindow { get; init; }

    public IReadOnlyDictionary<string, string> Arguments { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Free-text remainder of the query, for commands that take one blob of input.</summary>
    public string RawArgument { get; init; } = "";

    /// <summary>Dismisses the overlay. Most commands call this before doing their work.</summary>
    public required Action Dismiss { get; init; }

    /// <summary>Surfaces a transient message to the user (tray balloon / inline status).</summary>
    public required Action<string> Notify { get; init; }

    public string Arg(string name, string fallback = "") =>
        Arguments.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public int ArgInt(string name, int fallback) =>
        Arguments.TryGetValue(name, out var v) && int.TryParse(v, out var i) ? i : fallback;

    public bool ArgBool(string name, bool fallback = false) =>
        Arguments.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : fallback;
}

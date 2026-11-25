using System.Text.RegularExpressions;
using Beacon.Core;

namespace Beacon.Assistant;

/// <summary>
/// The agent team's router. Three tiers: Beacon's rules ("open spotify", no model at all), the
/// default brain (normally the free one on this PC), and an expert cloud brain for hard asks.
///
/// Hand-offs happen only when the user says so: "ask Claude to…", "Kai, ask Muse…", "ask the
/// expert…". Saying a name is the consent, so nothing is asked; and since the model never
/// decides to hand off, nothing it reads (an email) can send the conversation out. The expert
/// gets the recent chat without private memories, its answers are tagged with its name, every
/// hand-off is in the activity log, and its tool calls go through the same Sentinel gate as
/// any brain's: the expert gets no extra powers.
/// </summary>
public static partial class AgentTeam
{
    /// <summary>A request for the expert.</summary>
    /// <param name="Who">"expert" (the one picked in Settings), or a brain id: claude, claudecode, muse, codex, openrouter.</param>
    /// <param name="Request">What to ask it.</param>
    public sealed record Handoff(string Who, string Request);

    /// <summary>The cloud brains, in the order "auto" tries them.</summary>
    public static readonly string[] Experts = ["claude", "claudecode", "muse", "codex", "openrouter"];

    /// <summary>
    /// "ask claude to write a toast" → (claude, "write a toast"). A bare "ask claude" (or "let
    /// claude handle this") hands over <paramref name="previous"/>, the request before it; null
    /// when there's nothing to hand over or it isn't a hand-off at all.
    /// </summary>
    public static Handoff? Match(string text, string? previous)
    {
        var m = HandoffRx().Match(text ?? "");
        if (!m.Success) return null;
        var who = m.Groups["who"].Value.ToLowerInvariant() switch
        {
            "chatgpt" or "gpt" or "codex" => "codex",
            var w when w.StartsWith("claude code") => "claudecode",
            var w => w,
        };
        var rest = m.Groups["rest"].Value.Trim().TrimStart(':', ',', '-', ' ').Trim();
        if (rest.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) rest = rest[3..].Trim();
        if (rest.Length == 0 || ThisRx().IsMatch(rest))
        {
            if (string.IsNullOrWhiteSpace(previous)) return null;
            rest = previous.Trim();
        }
        return new Handoff(who, rest);
    }

    /// <summary>
    /// The brain id for a hand-off, or null when none is set up. "claude" means the API key when
    /// there is one, else Claude Code; "expert" is the Settings pick, or for "auto" the default
    /// brain when it's a cloud one, else the first that's set up.
    /// </summary>
    public static string? Resolve(string who, Settings s, Func<string, bool> available)
    {
        if (who == "claude") return s.ApiKey is { Length: > 0 } ? "claude" : available("claudecode") ? "claudecode" : null;
        if (who != "expert") return available(who) ? who : null;
        if (s.TeamExpert is { Length: > 0 } pick && pick != "auto") return available(pick) ? pick : null;
        return Automatic(s, available);
    }

    /// <summary>What "automatic" picks: the default brain when it's a cloud one, else the first set up.</summary>
    public static string? Automatic(Settings s, Func<string, bool> available) =>
        Experts.Contains(s.Brain) && available(s.Brain) ? s.Brain : Experts.FirstOrDefault(available);

    /// <summary>Is a cloud brain usable: a key for the API brains, the program installed for the sign-in ones.</summary>
    public static bool Available(string id, Settings s) => id switch
    {
        "claude" => s.ApiKey is { Length: > 0 },
        "openrouter" => s.OpenRouterKey is { Length: > 0 } && s.BrainOpenRouterModel.Length > 0,
        "claudecode" => CliBrains.Find(CliBrains.Cli.ClaudeCode) is not null,
        "codex" => CliBrains.Find(CliBrains.Cli.Codex) is not null,
        "muse" => MuseBrain.Find() is not null,
        _ => false,
    };

    /// <summary>The tag on the expert's answers.</summary>
    public static string Name(string id) => id switch
    {
        "claude" => "Claude",
        "claudecode" => "Claude Code",
        "muse" => "Muse",
        "codex" => "ChatGPT",
        "openrouter" => "OpenRouter",
        _ => "Expert",
    };

    /// <summary>What to say when the named expert isn't set up.</summary>
    public static string Missing(string who) => who switch
    {
        "expert" => "I don't have an expert brain set up yet. Add Claude, Muse or ChatGPT on the Brain page, under Accounts.",
        _ => $"{Name(who == "claude" ? "claude" : who)} isn't set up on this PC yet. Add it on the Brain page, under Accounts.",
    };

    [GeneratedRegex(@"^\s*(?:(?:hey|ok|okay)\s+)?(?:(?:kai|nova)\s*[,:]?\s+)?(?:please\s+)?(?:ask|let)\s+(?:the\s+)?(?<who>expert|claude\s+code|claude|muse|codex|chatgpt|gpt|openrouter)\b(?:\s+(?:for|on)\s+(?:this|that))?(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HandoffRx();

    [GeneratedRegex(@"^(?:handle|answer|do|take|try|look at)\s+(?:this|that|it)(?:\s+one)?[.!?]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ThisRx();
}

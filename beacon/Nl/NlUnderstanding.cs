namespace Beacon.Nl;

/// <summary>What a sentence turned out to mean.</summary>
public enum NlKind
{
    /// <summary>One or more commands to run, in order.</summary>
    Actions,

    /// <summary>Not an action at all - the user described something to look for.</summary>
    Search,
}

/// <summary>
/// One command in a plan, already resolved to a registry id and its arguments.
///
/// Storing the id rather than the <see cref="Core.BeaconCommand"/> keeps the parser free of
/// the registry's lifetime: a plan can be built, logged and shown before anything is run.
/// </summary>
/// <param name="Description">Human phrasing for the confirmation line: "set volume to 75%".</param>
public sealed record NlStep(
    string CommandId,
    Dictionary<string, string> Args,
    string Description);

/// <summary>
/// The parser's answer. Deliberately one small immutable object: the UI decides what to do
/// with it, and the parser never touches the machine itself.
/// </summary>
public sealed class NlUnderstanding
{
    public required NlKind Kind { get; init; }

    /// <summary>One line describing the whole thing, shown as the row title.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<NlStep> Steps { get; init; } = [];

    /// <summary>For <see cref="NlKind.Search"/>: what to actually search for.</summary>
    public string SearchTerm { get; init; } = string.Empty;

    /// <summary>
    /// 0-1. Rule matches that pinned every word score 1; a fuzzy command-name match scores
    /// lower, and the UI uses that to decide whether voice may run it unattended.
    /// </summary>
    public double Confidence { get; init; } = 1;

    /// <summary>Any step that cannot be undone, which forces the confirm-then-run path.</summary>
    public bool IsDestructive { get; init; }
}

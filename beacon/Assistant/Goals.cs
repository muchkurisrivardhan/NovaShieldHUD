namespace Beacon.Assistant;

/// <summary>Where one step of a plan stands.</summary>
public enum GoalStatus
{
    Pending,
    Running,
    Done,
    /// <summary>It failed and the user chose to carry on without it.</summary>
    Skipped,
    Failed,
    /// <summary>A risky step the user said no to.</summary>
    Declined,
    /// <summary>The plan stopped before reaching it.</summary>
    NotRun,
}

/// <summary>One step: a tool call written in full when the plan is made, never changed afterwards.</summary>
/// <param name="Summary">Sentinel's description, as the user is asked about it.</param>
public sealed record GoalStep(string Label, string Tool, IReadOnlyDictionary<string, string> Args, Risk Risk, string Summary)
{
    /// <summary>Steps Sentinel still asks about while the plan runs, even after the plan was approved.</summary>
    public bool StillAsks => Risk is Risk.Destructive or Risk.External;
}

/// <summary>
/// A frozen plan: made from the user's own words only, approved as a whole, then run exactly as
/// approved. Nothing read while it runs (an email, a tool result) can add, change or reorder a
/// step: that's what keeps a planted instruction from steering it.
/// </summary>
/// <param name="Goal">The user's words the plan was made from.</param>
public sealed record GoalPlan(string Goal, string Title, IReadOnlyList<GoalStep> Steps)
{
    public const int MaxSteps = 8;

    public int StillAskCount => Steps.Count(s => s.StillAsks);
}

/// <summary>The checklist on screen and the questions about it. The pet window supplies the real one.</summary>
public interface IGoalUi
{
    /// <summary>The plan is ready: show the checklist.</summary>
    void Show(GoalPlan plan);

    void Update(int index, GoalStatus status, string detail);

    /// <summary>One yes for the whole plan (risky steps still ask when reached).</summary>
    Task<bool> ApprovePlanAsync(GoalPlan plan);

    /// <summary>A step failed: true to skip it and carry on, false to stop the plan.</summary>
    Task<bool> SkipFailedAsync(int index, GoalStep step, string error);

    /// <summary>The plan is over (done, stopped or not run): the closing line.</summary>
    void Finish(string summary);
}

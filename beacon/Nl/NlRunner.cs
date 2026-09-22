using Beacon.Core;

namespace Beacon.Nl;

/// <summary>
/// Runs a parsed plan, step by step, in the order it was spoken.
///
/// Sequential and not parallel on purpose: "close all apps and shut down" only means anything
/// if the steps happen in that order, and a plan is at most a handful of steps, so there is
/// nothing to gain by overlapping them.
/// </summary>
public sealed class NlRunner
{
    private readonly CommandRegistry _registry;
    private readonly Settings _settings;

    public NlRunner(CommandRegistry registry, Settings settings)
    {
        _registry = registry;
        _settings = settings;
    }

    /// <summary>
    /// Breathing room between steps. Windows posts WM_CLOSE asynchronously, so a chain that
    /// closes apps and then shuts down would otherwise fire the shutdown before a single app
    /// had noticed it was asked to quit.
    /// </summary>
    private static readonly TimeSpan BetweenSteps = TimeSpan.FromMilliseconds(250);

    /// <summary>Runs every step and returns one line per step describing what happened.</summary>
    public async Task<IReadOnlyList<string>> RunAsync(NlUnderstanding plan, ActionContext ctx)
    {
        var messages = new List<string>();

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (i > 0) await Task.Delay(BetweenSteps).ConfigureAwait(false);

            messages.Add(await RunStepAsync(plan.Steps[i], ctx).ConfigureAwait(false));
        }

        return messages;
    }

    private async Task<string> RunStepAsync(NlStep step, ActionContext ctx)
    {
        if (step.CommandId.StartsWith(NlParser.CustomStepId, StringComparison.Ordinal))
        {
            var id = step.CommandId[NlParser.CustomStepId.Length..];
            var custom = _settings.CustomCommands.FirstOrDefault(c => c.Id == id);

            if (custom is null) return $"That command no longer exists.";

            ctx.Dismiss();

            return await CustomCommandRunner.RunAsync(
                custom,
                query: string.Empty,
                ctx.TargetWindow,
                _settings.FolderOpenModeValue,
                _settings.CustomFileManager,
                ctx.Notify).ConfigureAwait(false);
        }

        var command = _registry.Find(step.CommandId);
        if (command is null) return $"Don't know how to {step.Description}.";

        var scoped = new ActionContext
        {
            TargetWindow = ctx.TargetWindow,
            Arguments = step.Args,
            Dismiss = ctx.Dismiss,
            Notify = ctx.Notify,
        };

        return await command.RunAsync(scoped).ConfigureAwait(false);
    }
}

using System.Windows.Threading;
using Beacon.Core;
using Beacon.Nl;

namespace Beacon.Assistant;

/// <summary>
/// The assistant's hands: Beacon's own plain-English command parser and runner for anything
/// on the PC, and the inbox for email. Anything that can't be undone (shutting down, closing
/// apps...) is put to the user as an approval card first, whatever the model decided.
/// </summary>
public sealed class AssistantTools(
    NlParser parser, NlRunner runner, EmailInbox inbox, Dispatcher ui,
    Func<IntPtr> target, Func<string, Task<bool>> approve, Action<string> notify) : IAssistantTools
{
    public Task<string> DoAsync(string instruction, CancellationToken ct) =>
        ui.InvokeAsync(() => RunInstructionAsync(instruction)).Task.Unwrap();

    /// <summary>Also the fast path: a plain command the parser fully understands skips the model.</summary>
    public async Task<string> RunInstructionAsync(string instruction)
    {
        var plan = parser.Parse(instruction);
        if (plan is null || plan.Kind != NlKind.Actions || plan.Steps.Count == 0)
            return $"Couldn't work out how to \"{instruction}\" on this PC. Beacon can open apps and sites, search, " +
                   "change volume and brightness, set timers, manage windows and open settings.";

        if (plan.IsDestructive && !await approve(plan.Summary))
            return $"The user said no, so I didn't {plan.Summary}.";

        var context = new ActionContext
        {
            TargetWindow = target(),
            Arguments = new Dictionary<string, string>(),
            Dismiss = () => { },
            Notify = notify,
        };
        var results = await runner.RunAsync(plan, context);
        return $"Did: {plan.Summary}. " + string.Join(" ", results.Where(r => !string.IsNullOrWhiteSpace(r)));
    }

    public Task<string> CheckInboxAsync(CancellationToken ct) => inbox.CheckAsync(ct);
    public Task<string> ReadEmailAsync(int number, CancellationToken ct) => inbox.ReadAsync(number, ct);
    public Task<string> OpenEmailAsync(int number, CancellationToken ct) => ui.InvokeAsync(() => inbox.OpenAsync(number)).Task.Unwrap();
    public Task<string> ReplyEmailAsync(int number, string text, CancellationToken ct) => ui.InvokeAsync(() => inbox.ReplyAsync(number, text)).Task.Unwrap();
}

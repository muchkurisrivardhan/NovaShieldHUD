using System.Net.Http;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Beacon.Core;
using Beacon.Interop;

// WinForms (pulled in for the tray icon) also defines a Message type.
using ClaudeMessage = Anthropic.Models.Messages.Message;

namespace Beacon.Ai;

/// <summary>One command the model actually invoked, and what came of it.</summary>
public sealed record AiStep(string CommandId, string Title, bool WasDestructive, string Outcome);

public sealed class AiRunResult
{
    public string? Message { get; init; }
    public IReadOnlyList<AiStep> Steps { get; init; } = [];
    public string? Error { get; init; }
    public bool WasDeclined { get; init; }

    public bool Succeeded => Error is null;
}

/// <summary>
/// Asked to confirm a destructive action. Returns true to proceed.
/// </summary>
public delegate Task<bool> ConfirmDestructive(BeaconCommand command, string humanReadableCall);

/// <summary>
/// Turns a sentence into calls against <see cref="CommandRegistry"/>.
///
/// The registry is the only source of capabilities - tools are generated from it, so a
/// command added there is immediately reachable by the model with no second declaration
/// to keep in sync. Destructive commands are never executed on the model's say-so alone;
/// they route through <see cref="ConfirmDestructive"/> first.
/// </summary>
public sealed class AiPlanner
{
    private const int MaxTokens = 4096;

    private readonly CommandRegistry _registry;
    private readonly Settings _settings;

    private AnthropicClient? _client;
    private string? _clientKey;

    public AiPlanner(CommandRegistry registry, Settings settings)
    {
        _registry = registry;
        _settings = settings;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.ApiKey);

    private AnthropicClient? GetClient()
    {
        var key = _settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) return null;

        // Rebuild only when the key actually changed, so settings edits take effect
        // without a restart but we aren't allocating a client per query.
        if (_client is null || _clientKey != key)
        {
            _client = new AnthropicClient { ApiKey = key };
            _clientKey = key;
        }

        return _client;
    }

    public async Task<AiRunResult> RunAsync(
        string userPrompt,
        IntPtr targetWindow,
        Action dismiss,
        Action<string> notify,
        ConfirmDestructive confirm,
        CancellationToken ct)
    {
        var client = GetClient();
        if (client is null)
        {
            return new AiRunResult
            {
                Error = "No API key set. Add one in Beacon's settings, or set ANTHROPIC_API_KEY.",
            };
        }

        var commands = _registry.AiExposed.ToList();
        var tools = commands.Select(BuildTool).Select(t => new ToolUnion(t)).ToArray();

        var messages = new List<MessageParam>
        {
            new() { Role = Role.User, Content = userPrompt },
        };

        var steps = new List<AiStep>();

        for (var turn = 0; turn < _settings.AiMaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            var request = new MessageCreateParams
            {
                Model = _settings.AiModel,
                MaxTokens = MaxTokens,
                System = BuildSystemPrompt(targetWindow),
                Tools = tools,
                Messages = messages,

                // Haiku 4.5 rejects output_config.effort outright, so only the larger
                // models get an explicit low setting to keep launcher latency down.
                OutputConfig = AiModels.SupportsEffort(_settings.AiModel)
                    ? new OutputConfig { Effort = Effort.Low }
                    : null,
            };

            ClaudeMessage response;
            try
            {
                response = await client.Messages.Create(request, cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new AiRunResult { Steps = steps, Error = Describe(ex) };
            }

            // A refusal comes back as HTTP 200, so stop_reason must be checked before content.
            if (response.StopReason == "refusal")
            {
                return new AiRunResult
                {
                    Steps = steps,
                    Error = "Claude declined this request.",
                };
            }

            var toolUses = new List<ToolUseBlock>();
            var textParts = new List<string>();
            var assistantBlocks = new List<ContentBlockParam>();

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    textParts.Add(text.Text);
                    assistantBlocks.Add(new TextBlockParam { Text = text.Text });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? use))
                {
                    toolUses.Add(use);
                    assistantBlocks.Add(new ToolUseBlockParam
                    {
                        ID = use.ID,
                        Name = use.Name,
                        Input = use.Input,
                    });
                }
            }

            if (toolUses.Count == 0)
            {
                return new AiRunResult
                {
                    Steps = steps,
                    Message = string.Join("\n", textParts).Trim(),
                };
            }

            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantBlocks });

            // All results for one assistant turn go back in a SINGLE user message -
            // splitting them teaches the model to stop issuing parallel calls.
            var results = new List<ContentBlockParam>();

            foreach (var use in toolUses)
            {
                ct.ThrowIfCancellationRequested();

                var command = _registry.Find(use.Name);
                if (command is null)
                {
                    results.Add(new ToolResultBlockParam
                    {
                        ToolUseID = use.ID,
                        Content = $"No such command: {use.Name}",
                        IsError = true,
                    });
                    continue;
                }

                var args = FlattenArguments(use.Input);

                if (command.IsDestructive && _settings.ConfirmDestructiveAiActions)
                {
                    var approved = await confirm(command, Describe(command, args)).ConfigureAwait(false);
                    if (!approved)
                    {
                        steps.Add(new AiStep(command.Id, command.Title, true, "Declined by user."));
                        results.Add(new ToolResultBlockParam
                        {
                            ToolUseID = use.ID,
                            Content = "The user declined this action. Do not retry it; " +
                                      "stop and report what you did instead.",
                        });
                        continue;
                    }
                }

                string outcome;
                bool failed = false;
                try
                {
                    var ctx = new ActionContext
                    {
                        TargetWindow = targetWindow,
                        Arguments = args,
                        RawArgument = userPrompt,
                        Dismiss = dismiss,
                        Notify = notify,
                    };

                    outcome = await command.RunAsync(ctx).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = $"The command threw: {ex.Message}";
                    failed = true;
                }

                steps.Add(new AiStep(command.Id, command.Title, command.IsDestructive, outcome));
                results.Add(new ToolResultBlockParam
                {
                    ToolUseID = use.ID,
                    Content = outcome,
                    IsError = failed,
                });
            }

            messages.Add(new MessageParam { Role = Role.User, Content = results });
        }

        return new AiRunResult
        {
            Steps = steps,
            Error = $"Gave up after {_settings.AiMaxTurns} steps.",
        };
    }

    // ------------------------------------------------------------- tool schema

    private static Tool BuildTool(BeaconCommand command)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in command.Parameters)
        {
            var schema = new Dictionary<string, object>
            {
                ["type"] = p.Type switch
                {
                    ParameterType.Number => "number",
                    ParameterType.Boolean => "boolean",
                    _ => "string",
                },
                ["description"] = p.Description,
            };

            if (p.Choices is { Length: > 0 }) schema["enum"] = p.Choices;

            properties[p.Name] = schema;

            // strict mode requires every property to be listed in `required`; optional
            // parameters are expressed by allowing null alongside their real type.
            required.Add(p.Name);
            if (!p.Required) schema["type"] = new[] { (string)schema["type"], "null" };
        }

        var raw = ToRaw(new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        });

        return new Tool
        {
            Name = command.Id,
            Description = command.Description,
            InputSchema = InputSchema.FromRawUnchecked(raw),
            Strict = true,
        };
    }

    /// <summary>
    /// The SDK's raw-schema escape hatch wants JsonElement values, so round-trip through
    /// a serialize/parse rather than hand-building elements.
    /// </summary>
    private static Dictionary<string, JsonElement> ToRaw(Dictionary<string, object> source)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(source));
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static Dictionary<string, string> FlattenArguments(
        IReadOnlyDictionary<string, JsonElement>? input)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input is null) return args;

        foreach (var (key, value) in input)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;

            args[key] = value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : value.ToString();
        }

        return args;
    }

    // ------------------------------------------------------------------ prompt

    private string BuildSystemPrompt(IntPtr targetWindow)
    {
        var title = ForegroundTracker.GetTitle(targetWindow);
        var process = ForegroundTracker.GetProcessName(targetWindow);

        var focused = string.IsNullOrWhiteSpace(title)
            ? process ?? "unknown"
            : $"\"{title}\"{(process is null ? "" : $" ({process})")}";

        return $"""
            You are the command layer of Beacon, a keyboard launcher for Windows. The user
            summoned you with a hotkey and typed one instruction. Carry it out using the
            tools available, then reply with one short sentence saying what you did.

            The window the user was working in when they opened Beacon is {focused}. Commands
            described as acting on "the previously focused window" act on that one. When the
            user says "this window", "it", or names no window at all, they mean that window.
            If they name a different application, call list_windows first to find it.

            Rules:
            - Prefer acting over asking. If an instruction is workable under a reasonable
              reading, do it rather than requesting clarification.
            - If an instruction is genuinely ambiguous in a way that changes what gets
              destroyed or closed, ask instead of guessing.
            - Only use the tools listed. If nothing fits, say so plainly in one sentence.
            - Never claim to have done something a tool result did not confirm.
            - Keep the final reply to one line. The user reads it in a narrow search bar.
            """;
    }

    private static string Describe(BeaconCommand command, IReadOnlyDictionary<string, string> args)
    {
        if (args.Count == 0) return command.Title;

        var rendered = string.Join(", ", args.Select(kv => $"{kv.Key}: {kv.Value}"));
        return $"{command.Title} ({rendered})";
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "Could not reach the Claude API. Check your connection.",
        TaskCanceledException => "The request to Claude timed out.",
        _ when ex.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("401") => "Claude rejected the API key. Check it in settings.",
        _ when ex.Message.Contains("429") => "Rate limited by the Claude API. Try again shortly.",
        _ => $"Claude request failed: {ex.Message}",
    };
}

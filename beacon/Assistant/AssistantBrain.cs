using System.Text.Json;
using System.Text.Json.Nodes;
using Beacon.LocalAi;

namespace Beacon.Assistant;

/// <summary>What the assistant can reach on this PC. The window supplies the real ones.</summary>
public interface IAssistantTools
{
    /// <summary>Carries out a short Beacon instruction ("open notion", "volume 30"). Returns what happened.</summary>
    Task<string> DoAsync(string instruction, CancellationToken ct);
    Task<string> CheckInboxAsync(CancellationToken ct);
    Task<string> ReadEmailAsync(int number, CancellationToken ct);
    Task<string> OpenEmailAsync(int number, CancellationToken ct);
    Task<string> ReplyEmailAsync(int number, string text, CancellationToken ct);
}

/// <summary>
/// Nova's conversation: a free model on this PC, talking in short spoken sentences and
/// reaching the PC and the inbox through a handful of tools.
///
/// Every reply is held by grammar to one JSON shape - {"say", "tool", "args"} - so a small
/// model can't drift into prose that does nothing, or call a tool that doesn't exist. A turn
/// loops while the model asks for tools (at most a few rounds), saying each line as it comes.
/// The conversation is kept, so "summarize the second one" means the email it just listed.
/// </summary>
public sealed class AssistantBrain(LocalLlm llm, IAssistantTools tools)
{
    const int MaxRounds = 4;
    const int KeepMessages = 16;   // the last 8 exchanges; the model's window is small

    static readonly string[] ToolNames = ["do", "check_inbox", "read_email", "open_email", "reply_email"];

    readonly List<(string Role, string Content)> _history = [];

    /// <summary>Built once per conversation: the model's cache stays valid only while it doesn't change.</summary>
    string? _system;

    /// <summary>Raised for each line Nova says, as soon as it is decided (for speaking and the transcript).</summary>
    public event Action<string>? Said;

    /// <summary>Raised when a tool starts: its name and arguments, for the "working" state.</summary>
    public event Action<string>? Working;

    /// <summary>
    /// An exchange handled without the model (a plain command run directly), recorded so the
    /// conversation still knows about it: "now make it louder" after "open spotify".
    /// </summary>
    public void Remember(string userText, string reply)
    {
        _history.Add(("user", $"[{DateTime.Now:h:mm tt}] {userText.Trim()}"));
        _history.Add(("assistant", JsonSerializer.Serialize(new { say = reply, tool = (string?)null, args = new { } })));
    }

    public void Reset()
    {
        _history.Clear();
        _system = null;
    }

    public async Task RespondAsync(string modelPath, string userText, CancellationToken ct)
    {
        // The time goes with the message, not the instructions, which must stay the same all conversation.
        _history.Add(("user", $"[{DateTime.Now:h:mm tt}] {userText.Trim()}"));
        for (var round = 0; round < MaxRounds; round++)
        {
            _system ??= SystemPrompt();
            var messages = new List<(string, string)> { ("system", _system) };
            messages.AddRange(_history.TakeLast(KeepMessages).SkipWhile(m => m.Role != "user"));

            var raw = await llm.ChatAsync(modelPath, messages, 400, Gbnf, ct).ConfigureAwait(false);
            _history.Add(("assistant", raw));
            var (say, tool, args) = Parse(raw);

            // Before a tool runs, only a short "let me check": a longer line would be a guess at
            // what the tool is about to say, spoken as if it were known.
            if (tool is not null && say.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8) say = "One moment.";
            if (!string.IsNullOrWhiteSpace(say)) Said?.Invoke(say.Trim());
            if (tool is null) return;

            Working?.Invoke(tool);
            string result;
            try { result = await RunToolAsync(tool, args, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                result = "That didn't work: " + e.Message;
            }
            _history.Add(("user", $"[Result of {tool}]\n{result}\n[Now reply to me about this; use another tool only if it's needed.]"));
        }
        Said?.Invoke("Sorry, I got stuck on that one. Could you say it another way?");
    }

    Task<string> RunToolAsync(string tool, Dictionary<string, string> args, CancellationToken ct)
    {
        string Arg(string name) => args.TryGetValue(name, out var v) ? v : "";
        int Number() => int.TryParse(new string(Arg("number").Where(char.IsDigit).ToArray()), out var n) ? n : 1;
        return tool switch
        {
            "do" => tools.DoAsync(Arg("instruction"), ct),
            "check_inbox" => tools.CheckInboxAsync(ct),
            "read_email" => tools.ReadEmailAsync(Number(), ct),
            "open_email" => tools.OpenEmailAsync(Number(), ct),
            "reply_email" => tools.ReplyEmailAsync(Number(), Arg("text"), ct),
            _ => Task.FromResult("No such tool."),
        };
    }

    internal static (string Say, string? Tool, Dictionary<string, string> Args) Parse(string raw)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject o) return (raw, null, args);
            var say = o["say"]?.GetValue<string>() ?? "";
            var tool = o["tool"] is JsonValue t && t.TryGetValue<string>(out var name) && ToolNames.Contains(name) ? name : null;
            if (o["args"] is JsonObject a)
                foreach (var (k, v) in a)
                    if (v is JsonValue value && value.TryGetValue<string>(out var s)) args[k] = s;
            return (say, tool, args);
        }
        catch (JsonException)
        {
            return ("Sorry, I lost my words there. Say that again?", null, args);
        }
    }

    static string SystemPrompt() =>
        $$$"""
        You are Nova, the voice assistant built into NovaShield HUD on the user's Windows PC. You talk like a helpful friend: warm, natural, and short, one to three sentences, because everything you say is spoken aloud. No lists, no markdown, no emoji.

        Reply only with JSON: {"say": "what you say out loud", "tool": null or a tool name, "args": {}}

        Tools:
        - do, args {"instruction": "..."}: do something on the PC: open an app or website, search the web or YouTube, change volume or brightness, set a timer, manage windows, open settings. Write the instruction as a short command, for example "open notion", "open free llm", "set volume to 30", "search youtube for lofi music", "timer 10 minutes".
        - check_inbox, args {}: get the user's new emails.
        - read_email, args {"number": "2"}: get the full text of email number 2 from the last inbox check, to summarize it or answer about it.
        - open_email, args {"number": "2"}: open email number 2 in the browser.
        - reply_email, args {"number": "2", "text": "the reply"}: open a reply draft with that text, which the user sends themselves.

        How to behave:
        - Use a tool only when the request needs one, and one tool at a time. With a tool, "say" is only a few words such as "Let me check." Never guess what the tool will find.
        - After a tool result, tell the user what matters in plain words. When there's a natural next step, offer it as a short question, for example "You've got two new emails, one from Amazon and one from Priya. Want me to summarize them, or open one?"
        - To summarize, explain or answer anything about an email, first use read_email to get its text; the inbox list only has senders and subjects. "The second one" means number 2 in the last list.
        - To answer or reply to an email, use reply_email with the reply written out in full, in the user's voice, to the person who sent it.
        - To do anything on the PC, use do. Don't say you did something unless a tool result says it happened.
        - Use only what tools return. Never make up emails, names, numbers or results. If something failed or isn't set up, say so simply.
        - If the user just chats, chat back briefly. If you don't know, say so.

        Today is {{{DateTime.Now:dddd d MMMM yyyy}}}. Each message from the user starts with the current time in brackets.
        """;

    /// <summary>The one reply shape the model may produce.</summary>
    const string Gbnf = """
        root   ::= "{" ws "\"say\":" ws string "," ws "\"tool\":" ws tool "," ws "\"args\":" ws args ws "}"
        tool   ::= "null" | "\"do\"" | "\"check_inbox\"" | "\"read_email\"" | "\"open_email\"" | "\"reply_email\""
        args   ::= "{" ws ( pair ( ws "," ws pair )* )? ws "}"
        pair   ::= key ws ":" ws string
        key    ::= "\"instruction\"" | "\"number\"" | "\"text\""
        string ::= "\"" ( [^"\\\x7F\x00-\x1F] | "\\" ["\\/bfnrt] )* "\""
        ws     ::= [ \t\n]{0,4}
        """;
}

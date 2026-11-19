using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;

namespace Beacon.Assistant;

/// <summary>
/// The brains Nova can use besides the model on this PC: Claude through the user's own
/// Anthropic API key, and OpenRouter, connected with one click (OAuth PKCE: a browser page,
/// Authorize, done). Both get the same instructions and give the same JSON replies as the
/// local model, so the conversation code doesn't care which one answers.
/// </summary>
public static class CloudBrains
{
    public sealed record Choice(string Id, string Label, string Note);

    /// <summary>Claude models by API key, cheapest first.</summary>
    public static readonly Choice[] ClaudeModels =
    [
        new("claude-haiku-5-5", "Claude Haiku 5.5", "Fast and very cheap: $0.10 / $0.50 per million tokens in / out."),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Smarter, a little slower: $2 / $10 per million tokens."),
        new("claude-opus-5-5", "Claude Opus 5.5", "The most capable: $4 / $20 per million tokens."),
    ];

    public static readonly string[] EffortLevels = ["low", "medium", "high", "max"];

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    // ------------------------------------------------------------------ claude

    static AnthropicClient? _claude;
    static string? _claudeKey;

    public static async Task<string> ClaudeAsync(string apiKey, string model, string effort,
        IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct)
    {
        if (_claude is null || _claudeKey != apiKey)
        {
            _claude = new AnthropicClient { ApiKey = apiKey };
            _claudeKey = apiKey;
        }

        var system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content))
                     + "\n\nReply with the JSON object only.";
        var request = new MessageCreateParams
        {
            Model = model,
            MaxTokens = 8000,
            System = system,
            Messages = messages.Where(m => m.Role != "system").Select(m => new MessageParam
            {
                Role = m.Role == "assistant" ? Role.Assistant : Role.User,
                Content = m.Content,
            }).ToList(),
            OutputConfig = new OutputConfig
            {
                Effort = effort switch { "medium" => Effort.Medium, "high" => Effort.High, "max" => Effort.Max, _ => Effort.Low },
            },
        };

        var response = await _claude.Messages.Create(request, cancellationToken: ct).ConfigureAwait(false);
        // A refusal comes back as a normal response; check before reading the text.
        if (response.StopReason == "refusal")
            return """{"say": "I can't help with that one.", "tool": null, "args": {}}""";

        var text = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out TextBlock? t)) text.Append(t.Text);
        return text.ToString();
    }

    // ------------------------------------------------------------------ openrouter

    public static async Task<string> OpenRouterAsync(string key, string model, string effort,
        IReadOnlyList<(string Role, string Content)> messages, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 4000,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Role == "system" ? m.Content + "\n\nReply with the JSON object only." : m.Content,
            }).ToArray()),
            ["reasoning"] = new JsonObject { ["effort"] = effort == "max" ? "high" : effort, ["exclude"] = true },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("X-Title", "NovaShield HUD");

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = (JsonNode.Parse(json) as JsonObject)?["error"]?["message"]?.GetValue<string>() ?? response.StatusCode.ToString();
            throw new HttpRequestException(response.StatusCode == HttpStatusCode.Unauthorized
                ? "OpenRouter no longer accepts the connection. Connect again in Settings > Brain."
                : response.StatusCode == HttpStatusCode.PaymentRequired
                    ? "Your OpenRouter balance is empty. Pick a free model, or add credit on openrouter.ai."
                    : "OpenRouter: " + message);
        }
        return (JsonNode.Parse(json) as JsonObject)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    /// <summary>OpenRouter's models, free ones first. Needs no key.</summary>
    public static async Task<List<Choice>> OpenRouterModelsAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync("https://openrouter.ai/api/v1/models", ct).ConfigureAwait(false);
        var list = new List<(Choice Choice, bool Free)>();
        foreach (var m in (JsonNode.Parse(json)?["data"] as JsonArray) ?? [])
        {
            var id = m?["id"]?.GetValue<string>();
            if (id is null || id.EndsWith(":batch", StringComparison.Ordinal)) continue;   // batch-only, not for chat
            var name = m?["name"]?.GetValue<string>() ?? id;
            var free = m?["pricing"]?["prompt"]?.GetValue<string>() == "0" && m?["pricing"]?["completion"]?.GetValue<string>() == "0";
            var label = free && !name.Contains("free", StringComparison.OrdinalIgnoreCase) ? name + " (free)" : name;
            list.Add((new Choice(id, label, free ? id + " · free, no cost" : id + " · paid from your OpenRouter balance"), free));
        }
        return list.OrderByDescending(x => x.Free).ThenBy(x => x.Choice.Label, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Choice).ToList();
    }

    /// <summary>
    /// "Connect with OpenRouter": opens OpenRouter's Authorize page in the browser and waits for
    /// it to send the browser back to this PC with a one-time code, which is exchanged for a key.
    /// The PKCE verifier never leaves Beacon, so the code is useless to anyone else.
    /// </summary>
    public static async Task<string> ConnectOpenRouterAsync(CancellationToken ct)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var callback = $"http://localhost:{port}/callback";
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        Process.Start(new ProcessStartInfo(
            $"https://openrouter.ai/auth?callback_url={Uri.EscapeDataString(callback)}&code_challenge={challenge}&code_challenge_method=S256")
        { UseShellExecute = true });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        string? code = null;
        using (timeout.Token.Register(() => { try { listener.Stop(); } catch (ObjectDisposedException) { } }))
        {
            while (code is null)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    throw new TimeoutException("No answer from OpenRouter. Try Connect again.");
                }
                code = context.Request.Url?.AbsolutePath == "/callback" ? context.Request.QueryString["code"] : null;
                await Reply(context, code is not null).ConfigureAwait(false);
            }
        }

        var exchange = new JsonObject { ["code"] = code, ["code_verifier"] = verifier, ["code_challenge_method"] = "S256" };
        using var response = await Http.PostAsync("https://openrouter.ai/api/v1/auth/keys",
            new StringContent(exchange.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var key = (JsonNode.Parse(json) as JsonObject)?["key"]?.GetValue<string>();
        if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(key))
            throw new InvalidDataException("OpenRouter didn't hand over a key. Try Connect again.");
        return key;
    }

    static async Task Reply(HttpListenerContext context, bool ok)
    {
        var html = $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>NovaShield HUD</title>
            <style>body{background:#17181a;color:#e6e6e6;font:16px Bahnschrift,Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0}
            div{border:1px solid #f0a030;padding:32px 40px;text-align:center}h1{color:#f0a030;font-size:22px;letter-spacing:.08em;margin:0 0 8px}</style></head>
            <body><div><h1>{{(ok ? "NOVA IS CONNECTED" : "SOMETHING WENT WRONG")}}</h1>
            <p>{{(ok ? "OpenRouter is linked to NovaShield HUD. You can close this tab." : "No code came back. Close this tab and press Connect again.")}}</p></div></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

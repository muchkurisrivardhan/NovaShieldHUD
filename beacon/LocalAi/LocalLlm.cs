using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Beacon.LocalAi;

/// <summary>
/// A language model running on this PC through llama.cpp (LLamaSharp, CPU). No account, no
/// key, no network: the model file is downloaded once (LocalModels) and everything after
/// that is local.
///
/// Loading a model takes a second or two and holds its size in memory, so the loaded model
/// is kept while it is in use and let go after a few idle minutes.
/// </summary>
public sealed class LocalLlm : IDisposable
{
    static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(5);

    /// <summary>One loaded model for the whole app: the assistant and the prompt ring share it.</summary>
    public static LocalLlm Shared { get; } = new();

    readonly SemaphoreSlim _gate = new(1, 1);
    LLamaWeights? _weights;
    LLamaContext? _context;
    readonly List<LLamaToken> _cached = [];
    ModelParams? _params;
    string? _path;
    Timer? _idle;

    /// <summary>Runs one request: system instructions and one user message, the reply as text.</summary>
    public Task<string> CompleteAsync(string modelPath, string system, string user,
        int maxTokens, CancellationToken ct, IProgress<string>? partial = null) =>
        ChatAsync(modelPath, [("system", system), ("user", user)], maxTokens, null, ct, partial);

    /// <summary>
    /// A whole conversation in, the next reply out. With <paramref name="gbnf"/>, the reply is
    /// held to that grammar token by token (llama.cpp grammar sampling), which is how a small
    /// model is made to answer in exact JSON every time.
    ///
    /// The model's working memory (its KV cache) is kept between calls, and only the part of
    /// the prompt that differs from last time is processed. A conversation only grows at the
    /// end, so each turn costs the new words, not the whole instructions and history again:
    /// on a laptop CPU that is the difference between seconds and most of a minute.
    /// </summary>
    public async Task<string> ChatAsync(string modelPath, IReadOnlyList<(string Role, string Content)> messages,
        int maxTokens, string? gbnf, CancellationToken ct, IProgress<string>? partial = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _idle?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_weights is null || _path != modelPath)
            {
                DisposeModel();
                _params = new ModelParams(modelPath)
                {
                    ContextSize = 8192,
                    GpuLayerCount = 0,
                    Threads = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8)),
                };
                _weights = await Task.Run(() => LLamaWeights.LoadFromFile(_params), ct).ConfigureAwait(false);
                _path = modelPath;
            }
            _context ??= _weights.CreateContext(_params!);

            var template = new LLamaTemplate(_weights) { AddAssistant = true };
            foreach (var (role, content) in messages) template.Add(role, content);
            var prompt = Encoding.UTF8.GetString(template.Apply());
            var tokens = _context.Tokenize(prompt, true, true).ToList();
            if (tokens.Count + maxTokens > _context.ContextSize)
                throw new InvalidOperationException("That's more text than the model can hold at once.");

            // Keep what the cache already has in common with this prompt; process the rest.
            var keep = 0;
            while (keep < tokens.Count && keep < _cached.Count && tokens[keep] == _cached[keep]) keep++;
            if (keep == tokens.Count) keep--;   // at least one token must be decoded to get its logits
            _context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, keep, -1);
            _cached.RemoveRange(keep, _cached.Count - keep);

            var batch = new LLamaBatch();
            var pending = tokens.GetRange(keep, tokens.Count - keep);
            try
            {
                var (result, _, _) = await _context.DecodeAsync(pending, LLamaSeqId.Zero, batch, keep, ct).ConfigureAwait(false);
                if (result != DecodeResult.Ok) throw new InvalidOperationException("The model couldn't read the prompt (" + result + ").");
                _cached.AddRange(pending);

                using var sampling = new DefaultSamplingPipeline
                {
                    Temperature = 0.2f, TopP = 0.9f, RepeatPenalty = 1.05f,
                    Grammar = gbnf is null ? null : new Grammar(gbnf, "root"),
                };
                var decoder = new StreamingTokenDecoder(_context);
                var text = new StringBuilder();
                var logits = batch.TokenCount - 1;
                for (var i = 0; i < maxTokens; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var token = sampling.Sample(_context.NativeHandle, logits);
                    if (token.IsEndOfGeneration(_context.Vocab)) break;
                    decoder.Add(token);
                    text.Append(decoder.Read());
                    partial?.Report(text.ToString());
                    if (StopMarkers.Any(m => text.ToString().EndsWith(m, StringComparison.Ordinal))) break;

                    batch.Clear();
                    batch.Add(token, _cached.Count, LLamaSeqId.Zero, true);
                    if (await _context.DecodeAsync(batch, ct).ConfigureAwait(false) != DecodeResult.Ok) break;
                    _cached.Add(token);
                    logits = 0;
                }
                return gbnf is null ? Tidy(text.ToString()) : text.ToString().Trim();
            }
            catch
            {
                // A cancelled or failed decode leaves the cache in an unknown state: start clean next time.
                _context.NativeHandle.MemoryClear(true);
                _cached.Clear();
                throw;
            }
        }
        finally
        {
            _idle ??= new Timer(_ => Unload());
            _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    static readonly string[] StopMarkers = ["<|im_end|>", "<|end|>", "<|eot_id|>", "</s>"];

    /// <summary>Stop markers that slipped through, a "Here is..." preamble, code fences around the whole reply.</summary>
    static string Tidy(string reply)
    {
        var t = reply;
        foreach (var marker in new[] { "<|im_end|>", "<|end|>", "<|eot_id|>", "</s>" })
            t = t.Replace(marker, "");
        t = t.Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"^(here is|here's|sure[,!]?)[^\n]*:\s*\n", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (t.StartsWith("```") && t.EndsWith("```"))
            t = t[(t.IndexOf('\n') + 1)..^3].Trim();
        return t;
    }

    void Unload()
    {
        if (!_gate.Wait(0)) return;   // in use: the next request re-arms the timer
        try { DisposeModel(); }
        finally { _gate.Release(); }
    }

    void DisposeModel()
    {
        _context?.Dispose();
        _context = null;
        _cached.Clear();
        _weights?.Dispose();
        _weights = null;
        _path = null;
    }

    public void Dispose()
    {
        _idle?.Dispose();
        DisposeModel();
        _gate.Dispose();
    }
}

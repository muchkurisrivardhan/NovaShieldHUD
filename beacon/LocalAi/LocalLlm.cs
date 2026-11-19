using System.Text;
using LLama;
using LLama.Common;
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

    readonly SemaphoreSlim _gate = new(1, 1);
    LLamaWeights? _weights;
    ModelParams? _params;
    string? _path;
    Timer? _idle;

    /// <summary>Runs one request: system instructions and one user message, the reply as text.</summary>
    public async Task<string> CompleteAsync(string modelPath, string system, string user,
        int maxTokens, CancellationToken ct, IProgress<string>? partial = null)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _idle?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_weights is null || _path != modelPath)
            {
                _weights?.Dispose();
                _params = new ModelParams(modelPath)
                {
                    ContextSize = 4096,
                    GpuLayerCount = 0,
                    Threads = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8)),
                };
                _weights = await Task.Run(() => LLamaWeights.LoadFromFile(_params), ct).ConfigureAwait(false);
                _path = modelPath;
            }

            var template = new LLamaTemplate(_weights) { AddAssistant = true };
            template.Add("system", system);
            template.Add("user", user);
            var prompt = Encoding.UTF8.GetString(template.Apply());

            var executor = new StatelessExecutor(_weights, _params!) { ApplyTemplate = false };
            var inference = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = ["<|im_end|>", "<|end|>", "<|eot_id|>", "</s>"],
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f, TopP = 0.9f, RepeatPenalty = 1.05f },
            };

            var text = new StringBuilder();
            await foreach (var piece in executor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
            {
                text.Append(piece);
                partial?.Report(text.ToString());
            }
            return Tidy(text.ToString());
        }
        finally
        {
            _idle ??= new Timer(_ => Unload());
            _idle.Change(IdleUnload, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

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
        try { _weights?.Dispose(); _weights = null; _path = null; }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _idle?.Dispose();
        _weights?.Dispose();
        _gate.Dispose();
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace Beacon.Voice;

/// <summary>
/// Runs Whisper locally. Nothing is sent anywhere: the model file and the audio both stay
/// on this machine.
///
/// The model is loaded on first use and kept, because loading takes longer than
/// transcribing a short phrase - reloading per query would make every second use feel as
/// slow as the first.
/// </summary>
public sealed partial class Transcriber : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private WhisperFactory? _factory;
    private string? _loadedPath;

    /// <summary>
    /// Loads the model without transcribing. Called the moment listening starts: loading
    /// takes ~2 s, which measured as nearly all of the first query's latency, and the
    /// user is speaking for about that long anyway - so the load hides behind their voice.
    /// </summary>
    public async Task PreloadAsync(WhisperModel model, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(WhisperModels.PathFor(model), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(string path, CancellationToken ct)
    {
        if (_factory is not null && _loadedPath == path) return;

        _factory?.Dispose();
        _factory = await Task.Run(() => WhisperFactory.FromPath(path), ct).ConfigureAwait(false);
        _loadedPath = path;
    }

    public async Task<string> TranscribeAsync(
        WhisperModel model, float[] samples, string? vocabularyHint, CancellationToken ct)
    {
        var path = WhisperModels.PathFor(model);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(path, ct).ConfigureAwait(false);

            var builder = _factory!.CreateBuilder()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 2, 8))
                .WithNoContext()
                .WithSingleSegment();

            builder = model.Language is null
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(model.Language);

            // Whisper conditions on the prompt, so listing the names that actually appear in
            // this launcher (installed apps, known sites) makes it spell them correctly.
            if (!string.IsNullOrWhiteSpace(vocabularyHint)) builder = builder.WithPrompt(vocabularyHint);

            await using var processor = builder.Build();

            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
                text.Append(segment.Text);

            return Clean(text.ToString());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whisper annotates non-speech as "[BLANK_AUDIO]" or "(music)", and dictation-style
    /// output ends sentences with a full stop. Neither belongs in a search box.
    /// </summary>
    public static string Clean(string raw)
    {
        var text = Annotations().Replace(raw, " ");
        text = Whitespace().Replace(text, " ").Trim();
        text = text.TrimEnd('.', '!', ',', ';');

        return text;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*")]
    private static partial Regex Annotations();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public void Unload()
    {
        _gate.Wait();
        try
        {
            _factory?.Dispose();
            _factory = null;
            _loadedPath = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}

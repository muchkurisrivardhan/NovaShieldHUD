using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Beacon.Core;

namespace Beacon.Voice;

public sealed record WhisperModel(
    string Id,
    string Label,
    string Note,
    string FileName,
    long SizeBytes,
    string Sha256,
    string? Language)
{
    public string SizeLabel => $"{SizeBytes / 1024d / 1024d:N0} MB";
}

/// <summary>
/// The speech models Beacon can use, and their download.
///
/// Files come from the official whisper.cpp repository on Hugging Face and are checked
/// against SHA-256 hashes pinned here. The hash is not taken from the server at download
/// time: a hash fetched alongside the file only proves the transfer was intact, not that
/// the file is the one this code was written against.
/// </summary>
public static class WhisperModels
{
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    // Quantised (q5_1) builds: about a third the size of the full-precision files with a
    // negligible accuracy difference, and faster to run on CPU.
    public static readonly WhisperModel[] All =
    [
        new("base.en", "Base (English)", "Recommended. Fast and accurate for search-length phrases.",
            "ggml-base.en-q5_1.bin", 59_721_011,
            "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f", "en"),

        new("tiny.en", "Tiny (English)", "Smallest and quickest; stumbles more on names.",
            "ggml-tiny.en-q5_1.bin", 32_166_155,
            "c77c5766f1cef09b6b7d47f21b546cbddd4157886b3b5d6d4f709e91e66c7c2b", "en"),

        new("small.en", "Small (English)", "Most accurate; slower, noticeably so on long phrases.",
            "ggml-small.en-q5_1.bin", 190_098_681,
            "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30", "en"),

        new("base", "Base (multilingual)", "Detects and transcribes other languages.",
            "ggml-base-q5_1.bin", 59_707_625,
            "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", null),
    ];

    public static WhisperModel Default => All[0];

    public static WhisperModel Find(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public static string Directory => Path.Combine(Settings.Directory, "models");

    public static string PathFor(WhisperModel model) => Path.Combine(Directory, model.FileName);

    public static bool IsDownloaded(WhisperModel model) => File.Exists(PathFor(model));

    public static void Delete(WhisperModel model)
    {
        var path = PathFor(model);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>
    /// Downloads to a temporary file, verifies the hash, then moves it into place - so an
    /// interrupted or tampered download can never be mistaken for a usable model.
    /// </summary>
    public static async Task DownloadAsync(
        WhisperModel model, IProgress<double>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var target = PathFor(model);
        var partial = target + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beacon/1.0");

        using (var response = await http.GetAsync(BaseUrl + model.FileName,
                   HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? model.SizeBytes;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(partial);

            var buffer = new byte[1 << 16];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report(Math.Clamp(received / (double)total, 0, 1));
            }
        }

        string actual;
        await using (var check = File.OpenRead(partial))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
        }

        if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException(
                $"The downloaded model did not match its expected checksum and was discarded.");
        }

        File.Move(partial, target, overwrite: true);
    }
}

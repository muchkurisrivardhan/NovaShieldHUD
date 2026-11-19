using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Beacon.Core;

namespace Beacon.LocalAi;

public sealed record LocalModel(string Id, string Label, string Note, string Url, string FileName, long SizeBytes, string Sha256, string Licence)
{
    public string SizeLabel => $"{SizeBytes / 1024d / 1024d / 1024d:N1} GB";
}

/// <summary>
/// The free AI models Beacon can run on this PC, and their download. Only openly licensed
/// models (Apache 2.0) that anyone may use for free, commercially or not. Each file is
/// checked against the SHA-256 pinned here, as the Whisper models are.
/// </summary>
public static class LocalModels
{
    public static readonly LocalModel[] All =
    [
        new("qwen2.5-1.5b", "Qwen 2.5 1.5B (fast)",
            "Recommended for most PCs. About 1 GB of memory; a prompt takes a few seconds.",
            "https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf",
            "qwen2.5-1.5b-instruct-q4_k_m.gguf", 1_117_320_736,
            "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e", "Apache 2.0, Alibaba Cloud (Qwen)"),

        new("qwen3-4b", "Qwen 3 4B (better)",
            "Follows instructions more closely and writes better. About 3 GB of memory; slower. 8 GB of RAM or more.",
            "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_497_281_120,
            "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597", "Apache 2.0, Alibaba Cloud (Qwen)"),
    ];

    public static LocalModel Default => All[0];

    public static LocalModel Find(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public static string Directory => Path.Combine(Settings.Directory, "models");

    public static string PathFor(LocalModel model) => Path.Combine(Directory, model.FileName);

    public static bool IsDownloaded(LocalModel model) => File.Exists(PathFor(model));

    /// <summary>The model to use: the chosen one if it's downloaded, else any downloaded one.</summary>
    public static LocalModel? Usable(string? chosenId)
    {
        var chosen = Find(chosenId);
        return IsDownloaded(chosen) ? chosen : All.FirstOrDefault(IsDownloaded);
    }

    public static void Delete(LocalModel model)
    {
        var path = PathFor(model);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>To a temporary file, hash checked, then moved into place.</summary>
    public static async Task DownloadAsync(LocalModel model, IProgress<double>? progress, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var target = PathFor(model);
        var partial = target + ".part";

        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Beacon/1.0");

        try
        {
            using (var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? model.SizeBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(partial);
                var buffer = new byte[1 << 17];
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
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded model did not match its expected checksum and was discarded.");

            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }
}

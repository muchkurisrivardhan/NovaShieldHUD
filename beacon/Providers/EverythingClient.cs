using System.Net.Http;
using System.Text.Json;

namespace Beacon.Providers;

public sealed record EverythingHit(string Name, string Path, bool IsFolder)
{
    public string FullPath => System.IO.Path.Combine(Path, Name);
}

/// <summary>
/// Talks to Everything (voidtools) over its built-in HTTP/JSON server rather than the
/// official SDK.
///
/// Reason: the SDK ships Everything32.dll / Everything64.dll only. This machine is ARM64,
/// and an ARM64 process cannot P/Invoke an x64 DLL — the alternative would be compiling
/// Beacon as x64 and running the whole app under emulation, which is a bad trade on a
/// Snapdragon. HTTP is architecture-agnostic and costs one checkbox in Everything's
/// Options > HTTP Server.
/// </summary>
public sealed class EverythingClient
{
    private readonly HttpClient _http;
    private readonly int _port;

    /// <summary>
    /// Everything answers a localhost query in single-digit milliseconds when it is up.
    /// A generous timeout here would be paid on every keystroke when it is not, so this is
    /// deliberately tight: anything slower than this is treated as "not running".
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// After a failed probe, stop trying for a while. Without this, every keystroke pays
    /// the full timeout waiting for a server that isn't there - which is precisely the
    /// stall a launcher can least afford. Re-probing periodically means starting Everything
    /// mid-session still upgrades results without a restart.
    /// </summary>
    private static readonly TimeSpan UnavailableCooldown = TimeSpan.FromSeconds(15);

    private DateTimeOffset _skipUntil = DateTimeOffset.MinValue;

    public EverythingClient(int port)
    {
        _port = port;
        _http = new HttpClient { Timeout = RequestTimeout };
    }

    public bool LastProbeSucceeded { get; private set; }

    public async Task<IReadOnlyList<EverythingHit>> SearchAsync(
        string term, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];

        if (DateTimeOffset.UtcNow < _skipUntil)
        {
            LastProbeSucceeded = false;
            return [];
        }

        var url = $"http://localhost:{_port}/?search={Uri.EscapeDataString(term)}" +
                  $"&json=1&path_column=1&count={max}";

        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                MarkUnavailable();
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("results", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                MarkAvailable();
                return [];
            }

            var hits = new List<EverythingHit>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (string.IsNullOrEmpty(name)) continue;

                hits.Add(new EverythingHit(
                    name,
                    path ?? string.Empty,
                    string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase)));
            }

            MarkAvailable();
            return hits;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout, not our cancellation: Everything isn't answering.
            MarkUnavailable();
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            MarkUnavailable();
            return [];
        }
    }

    private void MarkAvailable()
    {
        LastProbeSucceeded = true;
        _skipUntil = DateTimeOffset.MinValue;
    }

    private void MarkUnavailable()
    {
        LastProbeSucceeded = false;
        _skipUntil = DateTimeOffset.UtcNow + UnavailableCooldown;
    }
}

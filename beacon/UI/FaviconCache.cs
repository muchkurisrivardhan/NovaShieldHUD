using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Beacon.UI;

/// <summary>
/// Site icons, fetched straight from each site and cached on disk.
///
/// Deliberately not routed through Google's or DuckDuckGo's favicon endpoints. Those are
/// faster and almost always succeed, but using one means handing a third party the list of
/// every domain in the user's bookmarks. Fetching <c>https://host/favicon.ico</c> directly
/// tells nobody anything they would not learn from the visit itself.
///
/// Consequences accepted: some sites serve no icon at that path, and first paint for a new
/// domain needs a network round trip. Misses are cached as misses so they cost once.
/// </summary>
public sealed class FaviconCache
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MissRetryAfter = TimeSpan.FromDays(7);

    private const int MaxBytes = 512 * 1024;

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, ImageSource?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = true;

    /// <summary>Raised when an icon arrives later, so the list can redraw.</summary>
    public event Action? IconArrived;

    public FaviconCache(string directory)
    {
        _directory = directory;

        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = FetchTimeout,
        };

        // Some sites refuse requests without a browser-ish UA.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Beacon/1.0");

        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch
        {
            Enabled = false;
        }
    }

    /// <summary>
    /// Returns a cached icon immediately, or null and starts a background fetch. Never
    /// blocks: this is called during list rendering.
    /// </summary>
    public ImageSource? Get(string url)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(url)) return null;

        string host;
        try
        {
            host = new Uri(url).Host;
        }
        catch
        {
            return null;
        }

        if (host.Length == 0) return null;

        if (_memory.TryGetValue(host, out var cached)) return cached;

        var file = CacheFile(host);
        if (File.Exists(file))
        {
            var image = LoadFromDisk(file);
            _memory[host] = image;
            return image;
        }

        var missMarker = file + ".miss";
        if (File.Exists(missMarker))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(missMarker) < MissRetryAfter)
                {
                    _memory[host] = null;
                    return null;
                }

                File.Delete(missMarker);
            }
            catch
            {
                return null;
            }
        }

        if (_inFlight.TryAdd(host, 0))
            _ = FetchAsync(host, file, missMarker);

        return null;
    }

    private async Task FetchAsync(string host, string file, string missMarker)
    {
        try
        {
            // Try the conventional location, then the bare domain's root as a fallback for
            // sites that only declare their icon in HTML.
            foreach (var candidate in new[]
                     {
                         $"https://{host}/favicon.ico",
                         $"https://www.{host}/favicon.ico",
                     })
            {
                try
                {
                    using var response = await _http.GetAsync(candidate).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (bytes.Length is 0 or > MaxBytes) continue;

                    await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);

                    var image = LoadFromDisk(file);
                    if (image is null) continue;

                    _memory[host] = image;
                    IconArrived?.Invoke();
                    return;
                }
                catch
                {
                    // Try the next candidate.
                }
            }

            // Record the miss so a site without an icon is not re-fetched on every keystroke.
            _memory[host] = null;
            try
            {
                await File.WriteAllTextAsync(missMarker, string.Empty).ConfigureAwait(false);
            }
            catch
            {
                // Cache directory unwritable; the in-memory miss still holds for this session.
            }
        }
        finally
        {
            _inFlight.TryRemove(host, out _);
        }
    }

    private static ImageSource? LoadFromDisk(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();

            // OnLoad so the file handle is released immediately; otherwise the cached file
            // stays locked for the process lifetime.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.UriSource = new Uri(file, UriKind.Absolute);
            image.DecodePixelWidth = 32;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch
        {
            // Corrupt or unsupported icon format (some sites serve HTML error pages here).
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Nothing more to do.
            }

            return null;
        }
    }

    private string CacheFile(string host)
    {
        var safe = string.Concat(host.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_directory, safe + ".ico");
    }

    public void Clear()
    {
        _memory.Clear();

        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            Directory.CreateDirectory(_directory);
        }
        catch
        {
            // Files in use; they will be overwritten as they are re-fetched.
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Beacon.Core;

/// <summary>
/// Updates from GitHub Releases. A release (made by .github/workflows/release.yml when a v* tag is
/// pushed) carries NovaShieldHUD-Setup-x64.exe, NovaShieldHUD-Setup-arm64.exe and SHA256SUMS.txt.
/// The installer is downloaded over HTTPS, checked against SHA256SUMS.txt, and run silently; it
/// closes Beacon and Dotline, replaces the files and starts Beacon again.
/// </summary>
public static class Updater
{
    public const string Repo = "muchkurisrivardhan/vantage";

    public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public sealed record Release(Version Version, string Tag, string SetupUrl, string SumsUrl, string AssetName);

    static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NovaShieldHUD-Beacon/" + Current);   // GitHub's API requires one
        return http;
    }

    /// <summary>The latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct)
    {
        using var http = Http();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (ParseTag(tag) is not { } version || version <= Current) return null;

        string? Url(string name) => root.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() == name) is { ValueKind: JsonValueKind.Object } a
            ? a.GetProperty("browser_download_url").GetString() : null;
        var asset = $"NovaShieldHUD-Setup-{Arch}.exe";
        return Url(asset) is { } setup && Url("SHA256SUMS.txt") is { } sums
            ? new Release(version, tag, setup, sums, asset)
            : throw new InvalidDataException($"Release {tag} has no {asset} or SHA256SUMS.txt yet. Try again in a few minutes.");
    }

    /// <summary>"v1.2.3" or "1.2.3-beta" → 1.2.3; anything else → null.</summary>
    public static Version? ParseTag(string tag) =>
        Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var v) ? Norm(v) : null;

    static Version Norm(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>Downloads the installer to %TEMP% and checks its SHA-256. Returns its path.</summary>
    public static async Task<string> DownloadAsync(Release r, IProgress<double>? progress, CancellationToken ct)
    {
        using var http = Http();
        var sums = await http.GetStringAsync(r.SumsUrl, ct);
        var want = sums.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == r.AssetName)?[0]
            ?? throw new InvalidDataException("SHA256SUMS.txt doesn't list " + r.AssetName);

        var path = Path.Combine(Path.GetTempPath(), r.AssetName);
        using (var response = await http.GetAsync(r.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(path);
            var buffer = new byte[1 << 16];
            long got = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                got += read;
                if (total > 0) progress?.Report(got / (double)total);
            }
        }

        await using (var check = File.OpenRead(path))
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(check, ct)), want, StringComparison.OrdinalIgnoreCase))
            {
                check.Close();
                File.Delete(path);
                throw new InvalidDataException("The download didn't match its published checksum and was deleted.");
            }
        return path;
    }

    /// <summary>Runs the installer silently. It closes Beacon itself and starts the new one when done.</summary>
    public static void Install(string setup) =>
        Process.Start(new ProcessStartInfo(setup, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
}

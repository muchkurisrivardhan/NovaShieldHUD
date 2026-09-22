using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Beacon.Core;
using Beacon.Interop;
using Forms = System.Windows.Forms;

namespace Beacon.Providers;

/// <summary>
/// One-line answers about this PC: "ip", "battery", "wifi", "disk space", "uptime".
///
/// Answered locally, never over the network - which is why there is a local IP and no public
/// one: finding the public address means asking a third-party service, and a launcher should
/// not phone anyone just because you typed two letters.
/// </summary>
public sealed partial class InfoProvider : IResultProvider
{
    private readonly SystemStats _stats = new();

    public string Name => "Info";

    public int Weight => 0;

    /// <summary>Same as the calculator: a direct answer to a direct question sits on top.</summary>
    private const int AnswerScore = 1100;

    private sealed record Topic(string Label, string Glyph, string[] Keys, Func<string?> Answer);

    private Topic[]? _topics;

    private Topic[] Topics => _topics ??=
    [
        new("Local IP address", "", ["ip", "ip address", "local ip", "ipv4"], LocalIp),
        new("Wi-Fi", "", ["wifi", "wi-fi", "wifi name", "ssid", "wifi signal"], Wifi),
        new("Battery", "", ["battery", "charge", "battery level"], Battery),
        new("Disk space", "", ["disk", "disk space", "storage", "free space", "drives"], Disks),
        new("Uptime", "", ["uptime", "up time", "since boot", "last restart"], Uptime),
        new("CPU", "", ["cpu", "processor", "cpu usage"], Cpu),
        new("Memory", "", ["ram", "memory", "memory usage", "ram usage"], Memory),
        new("Computer name", "", ["hostname", "computer name", "pc name", "device name"], () => Environment.MachineName),
        new("Windows version", "", ["windows version", "os version", "winver"],
            () => $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})"),
    ];

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        var key = Question().Replace(query.Text.ToLowerInvariant(), "").Trim();
        var topic = Topics.FirstOrDefault(t => t.Keys.Contains(key));

        if (topic is null) return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        // Wi-Fi and CPU take a moment (netsh, a CPU sample); keep them off the UI thread.
        return Task.Run<IReadOnlyList<SearchResult>>(() =>
        {
            var answer = topic.Answer() ?? "Not available on this PC";

            return
            [
                new SearchResult
                {
                    Title = answer,
                    Subtitle = $"{topic.Label}  ·  Enter to copy",
                    Source = "Info",
                    Glyph = topic.Glyph,
                    Score = AnswerScore,
                    CopyText = answer,
                    InvokeAsync = ctx =>
                    {
                        try
                        {
                            System.Windows.Clipboard.SetText(answer);
                            ctx.Notify($"Copied: {answer}");
                        }
                        catch
                        {
                            // Clipboard held open by another process; the answer is still on screen.
                        }

                        ctx.Dismiss();
                        return Task.CompletedTask;
                    },
                },
            ];
        }, ct);
    }

    /// <summary>The words around a question: "what's my ip?" asks the same thing as "ip".</summary>
    [GeneratedRegex(@"^(what'?s|what\s+is|whats|show|show\s+me|how\s+much|check|get)\s+|\b(my|the|this\s+pc'?s?|current)\b|\?|\s+left$|\s+remaining$")]
    private static partial Regex Question();

    // ------------------------------------------------------------------ answers

    private static string? LocalIp()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => $"{a.Address}  ({n.Name})"))
            .ToList();

        return addresses.Count == 0 ? "Not connected" : string.Join("   ", addresses);
    }

    private static string? Wifi()
    {
        if (!NetworkControl.Netsh("wlan show interfaces", out var output)) return "No wireless adapter";

        string? Field(string name) => output.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(name, StringComparison.OrdinalIgnoreCase) && l.Contains(':'))
            ?.Split(':', 2)[1].Trim();

        var ssid = Field("SSID");
        if (string.IsNullOrEmpty(ssid)) return "Not connected to Wi-Fi";

        var signal = Field("Signal");
        return signal is null ? ssid : $"{ssid}  ·  signal {signal}";
    }

    private static string? Battery()
    {
        var power = Forms.SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)) return "No battery - running on mains";

        var percent = (int)Math.Round(power.BatteryLifePercent * 100);
        var plugged = power.PowerLineStatus == Forms.PowerLineStatus.Online;

        if (plugged) return percent >= 100 ? "100%  ·  plugged in, full" : $"{percent}%  ·  charging";

        var left = power.BatteryLifeRemaining;
        return left > 0
            ? $"{percent}%  ·  about {CommandRegistry.Describe(TimeSpan.FromMinutes(Math.Round(left / 60.0)))} left"
            : $"{percent}%  ·  on battery";
    }

    private static string? Disks() =>
        string.Join("   ", DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => $"{d.Name.TrimEnd('\\')} {Gb(d.AvailableFreeSpace)} free of {Gb(d.TotalSize)}"));

    private static string Gb(long bytes) => $"{bytes / 1_073_741_824d:0} GB";

    private static string? Uptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var since = DateTime.Now - up;
        return $"{CommandRegistry.Describe(new TimeSpan(up.Days, up.Hours, up.Minutes, 0))}  ·  since {since:ddd h:mm tt}";
    }

    private string? Cpu()
    {
        // CPU load is a difference between two readings; one reading alone is meaningless.
        _stats.CpuPercent();
        Thread.Sleep(250);
        return $"{_stats.CpuPercent()}% in use";
    }

    private string? Memory() => $"{_stats.MemoryPercent()}% in use";
}

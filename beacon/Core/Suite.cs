using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Beacon.Core;

/// <summary>
/// Beacon's side of the Vantage apps that live beside it (D:\projects\Vantage\...):
/// starting and stopping Dotline, and handing it a flight to track. The apps talk through
/// small JSON files in %APPDATA%\Vantage - no sockets, no services.
/// </summary>
public static partial class Suite
{
    static readonly string SharedDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vantage");

    static readonly string FlightFile = Path.Combine(SharedDir, "flight.json");

    // ------------------------------------------------------------------ dotline

    public static bool DotlineRunning => Process.GetProcessesByName("Dotline").Length > 0;

    /// <summary>
    /// Dotline's exe, found by walking up from Beacon's own folder to Vantage: the newest of
    /// its published build (dist) and its Release build.
    /// </summary>
    public static string? FindDotline()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var root = Path.Combine(dir.FullName, "dotline");
            if (!Directory.Exists(root)) continue;
            return new[]
                {
                    Path.Combine(root, "dist", "Dotline.exe"),
                    Path.Combine(root, "bin", "Release", "net10.0-windows", "Dotline.exe"),
                }
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        return null;
    }

    public static string OpenDotline()
    {
        if (DotlineRunning) return "Dotline is already running.";
        if (FindDotline() is not { } exe) return "Couldn't find Dotline next to Beacon (expected Vantage\\dotline).";
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        return "Opened Dotline.";
    }

    /// <summary>Dotline saves every change the moment it happens, so ending it loses nothing.</summary>
    public static string CloseDotline()
    {
        var running = Process.GetProcessesByName("Dotline");
        if (running.Length == 0) return "Dotline isn't running.";
        foreach (var p in running)
        {
            using (p)
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return DotlineRunning ? "Windows refused to close Dotline." : "Closed Dotline.";
    }

    // ------------------------------------------------------------------ flights

    public sealed record FlightRequest(string Flight, string Callsign);

    /// <summary>The flight Dotline is tracking, if any.</summary>
    public static FlightRequest? CurrentFlight()
    {
        try
        {
            if (!File.Exists(FlightFile)) return null;
            var r = JsonSerializer.Deserialize<FlightRequest>(File.ReadAllText(FlightFile));
            return string.IsNullOrWhiteSpace(r?.Flight) ? null : r;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Hand a flight to Dotline (starting it if needed). Returns what happened, for the status line.</summary>
    public static string Track(string typed)
    {
        var flight = Normalize(typed);
        if (!ValidFlight().IsMatch(flight)) return "A flight number is 2–10 letters and digits, like AA100.";
        var callsign = ToCallsign(flight);
        Write(new FlightRequest(flight, callsign));
        var started = DotlineRunning ? "" : " " + OpenDotline();
        var shown = callsign == flight ? flight : $"{flight} ({callsign})";
        return $"Tracking {shown} on your wallpaper map.{started}";
    }

    public static string StopTracking()
    {
        var current = CurrentFlight();
        Write(new FlightRequest("", ""));
        return current is null ? "No flight was being tracked." : $"Stopped tracking {current.Flight}.";
    }

    static void Write(FlightRequest r)
    {
        Directory.CreateDirectory(SharedDir);
        Settings.WriteAtomic(FlightFile, JsonSerializer.Serialize(r));
    }

    public static string Normalize(string typed) => Regex.Replace(typed.ToUpperInvariant(), @"[\s\-]", "");

    /// <summary>
    /// Airlines put the ICAO code in the broadcast callsign ("AAL100"), while tickets show the
    /// IATA flight number ("AA100"). Maps the common airlines; anything else is used as typed.
    /// </summary>
    public static string ToCallsign(string flight)
    {
        var m = IataFlight().Match(flight);
        if (!m.Success || !Airlines.TryGetValue(m.Groups[1].Value, out var icao)) return flight;
        return icao + m.Groups[2].Value.TrimStart('0') + m.Groups[3].Value;
    }

    [GeneratedRegex(@"^([A-Z0-9]{2})(\d{1,4})([A-Z]?)$")]
    private static partial Regex IataFlight();

    [GeneratedRegex(@"^[A-Z0-9]{2,10}$")]
    private static partial Regex ValidFlight();

    // ponytail: the ~70 carriers people usually fly; ICAO callsigns (AAL100) and others pass through as typed.
    static readonly Dictionary<string, string> Airlines = new()
    {
        ["AA"] = "AAL", ["UA"] = "UAL", ["DL"] = "DAL", ["WN"] = "SWA", ["AS"] = "ASA", ["B6"] = "JBU", ["NK"] = "NKS",
        ["F9"] = "FFT", ["HA"] = "HAL", ["G4"] = "AAY", ["SY"] = "SCX", ["AC"] = "ACA", ["WS"] = "WJA", ["AM"] = "AMX",
        ["Y4"] = "VOI", ["BA"] = "BAW", ["VS"] = "VIR", ["LH"] = "DLH", ["AF"] = "AFR", ["KL"] = "KLM", ["LX"] = "SWR",
        ["OS"] = "AUA", ["SN"] = "BEL", ["SK"] = "SAS", ["AY"] = "FIN", ["IB"] = "IBE", ["VY"] = "VLG", ["EI"] = "EIN",
        ["TP"] = "TAP", ["AZ"] = "ITY", ["LO"] = "LOT", ["FR"] = "RYR", ["U2"] = "EZY", ["W6"] = "WZZ", ["TK"] = "THY",
        ["PC"] = "PGT", ["EK"] = "UAE", ["QR"] = "QTR", ["EY"] = "ETD", ["SV"] = "SVA", ["GF"] = "GFA", ["WY"] = "OMA",
        ["FZ"] = "FDB", ["G9"] = "ABY", ["AI"] = "AIC", ["6E"] = "IGO", ["UK"] = "VTI", ["SG"] = "SEJ", ["IX"] = "AXB",
        ["QP"] = "AKJ", ["SQ"] = "SIA", ["TR"] = "TGW", ["CX"] = "CPA", ["NH"] = "ANA", ["JL"] = "JAL", ["KE"] = "KAL",
        ["OZ"] = "AAR", ["CA"] = "CCA", ["MU"] = "CES", ["CZ"] = "CSN", ["BR"] = "EVA", ["CI"] = "CAL", ["QF"] = "QFA",
        ["VA"] = "VOZ", ["JQ"] = "JST", ["NZ"] = "ANZ", ["TG"] = "THA", ["MH"] = "MAS", ["AK"] = "AXM", ["GA"] = "GIA",
        ["PR"] = "PAL", ["VN"] = "HVN", ["ET"] = "ETH", ["SA"] = "SAA", ["KQ"] = "KQA", ["MS"] = "MSR", ["AT"] = "RAM",
        ["LA"] = "LAN", ["AV"] = "AVA", ["CM"] = "CMP", ["G3"] = "GLO", ["AD"] = "AZU", ["AR"] = "ARG", ["5X"] = "UPS",
        ["FX"] = "FDX",
    };
}

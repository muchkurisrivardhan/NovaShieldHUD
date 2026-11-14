using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Windows.Widgets;

namespace Vantage.Lockscreen.Widgets;

/// <summary>
/// Builds the Adaptive Card (template) and its data for each widget.
///
/// Everything shown is safe for a stranger at the lock screen to read: a node label from config
/// (not the real PC name), whether the network is up (not which one), battery percent, uptime,
/// the day/year elapsed, and a countdown. No names, addresses, files or notifications.
/// </summary>
static class Cards
{
    public static string Template(string kind, WidgetSize size)
    {
        var small = size == WidgetSize.Small;
        return kind switch
        {
            "Day_Year_Progress" => small ? ProgressSmall : ProgressMedium,
            "Countdown" => small ? CountdownSmall : CountdownMedium,
            _ => small ? NodeSmall : NodeMedium,   // Node_Status
        };
    }

    public static string Data(string kind) => kind switch
    {
        "Day_Year_Progress" => ProgressData(),
        "Countdown" => CountdownData(),
        _ => NodeData(),
    };

    // ---- data ---------------------------------------------------------------------------

    static string NodeData()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var online = SafeNetwork();
        return new JsonObject
        {
            ["nodeLabel"] = ReadConfig().Node,
            ["link"] = online ? "ONLINE" : "OFFLINE",
            ["linkColor"] = online ? "Good" : "Attention",
            ["power"] = Power(),
            ["uptime"] = $"{(int)up.TotalDays}D {up.Hours:00}H",
        }.ToJsonString();
    }

    static string ProgressData()
    {
        var now = DateTime.Now;
        var day = now.TimeOfDay.TotalHours / 24 * 100;
        var start = new DateTime(now.Year, 1, 1);
        var year = (now - start).TotalDays / (start.AddYears(1) - start).TotalDays * 100;
        return new JsonObject
        {
            ["dayPct"] = ((int)Math.Round(day)).ToString(),
            ["dayBar"] = Bar(day),
            ["yearPct"] = ((int)Math.Round(year)).ToString(),
            ["yearBar"] = Bar(year),
        }.ToJsonString();
    }

    static string CountdownData()
    {
        var c = ReadConfig();
        var days = Math.Max(0, (c.Date.Date - DateTime.Today).Days);
        return new JsonObject
        {
            ["label"] = c.Label,
            ["days"] = days.ToString(),
            ["unit"] = days == 1 ? "DAY TO" : "DAYS TO",
            ["date"] = c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }.ToJsonString();
    }

    /// <summary>A 12-cell bar in block characters; reads like the HUD's dots in a monospace card.</summary>
    static string Bar(double pct, int cells = 12)
    {
        var filled = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100 * cells);
        return new string('█', filled) + new string('░', cells - filled);
    }

    // ---- safe system readouts -----------------------------------------------------------

    /// <summary>Whether a network is up. Only a yes/no; never the network's name or address.</summary>
    static bool SafeNetwork()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch (NetworkInformationException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, Reserved; public int LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out PowerStatus s);

    static string Power()
    {
        if (!GetSystemPowerStatus(out var s)) return "—";
        if ((s.BatteryFlag & 128) != 0) return "AC POWER";   // no system battery
        var pct = s.BatteryPercent <= 100 ? $"{s.BatteryPercent}%" : "—";
        return $"{pct} · {(s.AcLine == 1 ? "CHARGING" : "BATTERY")}";
    }

    static (string Node, string Label, DateTime Date) ReadConfig()
    {
        // Read-only, from the real per-user path (USERPROFILE isn't redirected for a packaged app).
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", "LockscreenWidget", "config.json");
        try
        {
            var w = JsonNode.Parse(File.ReadAllText(path))?["widgets"];
            var node = w?["nodeLabel"]?.GetValue<string>() is { Length: > 0 } n ? n : "NODE-01";
            var label = w?["countdown"]?["label"]?.GetValue<string>() is { Length: > 0 } l ? l : "TARGET";
            var date = DateTime.TryParseExact(w?["countdown"]?["date"]?.GetValue<string>(), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : new DateTime(2027, 1, 1);
            return (node, label, date);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return ("NODE-01", "TARGET", new DateTime(2027, 1, 1));
        }
    }

    // ---- templates (${...} is filled from the data above) -------------------------------

    const string Mono = "\"fontType\": \"Monospace\"";

    static string Card(string body) =>
        $$"""
        { "type": "AdaptiveCard", "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5", "body": [ {{body}} ] }
        """;

    static readonly string NodeMedium = Card($$"""
        { "type": "ColumnSet", "columns": [
          { "type": "Column", "width": "auto", "verticalContentAlignment": "Center",
            "items": [ { "type": "TextBlock", "text": "▣", "size": "ExtraLarge", "color": "Accent", "spacing": "None" } ] },
          { "type": "Column", "width": "stretch", "items": [
            { "type": "TextBlock", "text": "${nodeLabel}", "weight": "Bolder", "size": "Medium", {{Mono}}, "spacing": "None" },
            { "type": "TextBlock", "text": "STATUS: SEALED", "isSubtle": true, "size": "Small", {{Mono}}, "spacing": "None" } ] } ] },
        { "type": "ColumnSet", "spacing": "Medium", "columns": [
          { "type": "Column", "width": "stretch", "items": [
            { "type": "TextBlock", "text": "LINK", "isSubtle": true, "size": "Small", {{Mono}}, "spacing": "None" },
            { "type": "TextBlock", "text": "${link}", "weight": "Bolder", "color": "${linkColor}", {{Mono}}, "spacing": "None" } ] },
          { "type": "Column", "width": "stretch", "items": [
            { "type": "TextBlock", "text": "POWER", "isSubtle": true, "size": "Small", {{Mono}}, "spacing": "None" },
            { "type": "TextBlock", "text": "${power}", "weight": "Bolder", {{Mono}}, "spacing": "None" } ] },
          { "type": "Column", "width": "stretch", "items": [
            { "type": "TextBlock", "text": "UPTIME", "isSubtle": true, "size": "Small", {{Mono}}, "spacing": "None" },
            { "type": "TextBlock", "text": "${uptime}", "weight": "Bolder", {{Mono}}, "spacing": "None" } ] } ] }
        """);

    static readonly string NodeSmall = Card($$"""
        { "type": "TextBlock", "text": "${nodeLabel}", "weight": "Bolder", "size": "Medium", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "LINK ${link}", "color": "${linkColor}", {{Mono}}, "size": "Small", "spacing": "None" },
        { "type": "TextBlock", "text": "PWR  ${power}", {{Mono}}, "size": "Small", "spacing": "None" },
        { "type": "TextBlock", "text": "UP   ${uptime}", "isSubtle": true, {{Mono}}, "size": "Small", "spacing": "None" }
        """);

    static readonly string ProgressMedium = Card($$"""
        { "type": "TextBlock", "text": "DAY / YEAR", "weight": "Bolder", "size": "Medium", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "DAY   ${dayPct}%", {{Mono}}, "spacing": "Medium", "isSubtle": true, "size": "Small" },
        { "type": "TextBlock", "text": "${dayBar}", "color": "Accent", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "YEAR  ${yearPct}%", {{Mono}}, "spacing": "Medium", "isSubtle": true, "size": "Small" },
        { "type": "TextBlock", "text": "${yearBar}", "color": "Accent", {{Mono}}, "spacing": "None" }
        """);

    static readonly string ProgressSmall = Card($$"""
        { "type": "TextBlock", "text": "DAY  ${dayPct}%", {{Mono}}, "size": "Small", "spacing": "None" },
        { "type": "TextBlock", "text": "${dayBar}", "color": "Accent", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "YEAR ${yearPct}%", {{Mono}}, "size": "Small", "spacing": "Small" },
        { "type": "TextBlock", "text": "${yearBar}", "color": "Accent", {{Mono}}, "spacing": "None" }
        """);

    static readonly string CountdownMedium = Card($$"""
        { "type": "TextBlock", "text": "${label}", "weight": "Bolder", "size": "Medium", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "${days}", "size": "ExtraLarge", "weight": "Bolder", "color": "Accent", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "${unit} ${date}", "isSubtle": true, "size": "Small", {{Mono}}, "spacing": "None" }
        """);

    static readonly string CountdownSmall = Card($$"""
        { "type": "TextBlock", "text": "${label}", "weight": "Bolder", "size": "Small", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "${days}", "size": "ExtraLarge", "weight": "Bolder", "color": "Accent", {{Mono}}, "spacing": "None" },
        { "type": "TextBlock", "text": "${unit}", "isSubtle": true, "size": "Small", {{Mono}}, "spacing": "None" }
        """);
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace Vantage.Lockscreen;

/// <summary>
/// Expands {DATE}, {WEEKDAY}, {DAY_OF_YEAR}, {YEAR_PROGRESS}, {UPTIME_DAYS} and
/// {COUNTDOWN:YYYY-MM-DD}. Every token is safe for a stranger to read: no names, addresses,
/// files or notifications. Unknown tokens are left as typed, and everything is upper case.
/// </summary>
public static partial class Tokens
{
    public static string Expand(string text, DateTime now, TimeSpan uptime) =>
        TokenPattern().Replace(text, m =>
        {
            var inv = CultureInfo.InvariantCulture;
            switch (m.Groups[1].Value)
            {
                case "DATE": return now.ToString("dd MMM yyyy", inv);
                case "WEEKDAY": return now.ToString("dddd", inv);
                case "DAY_OF_YEAR": return now.DayOfYear.ToString(inv);
                case "YEAR_PROGRESS":
                    var start = new DateTime(now.Year, 1, 1);
                    var pct = (now - start).TotalDays / (start.AddYears(1) - start).TotalDays * 100;
                    return Math.Round(pct).ToString(inv) + "%";
                case "UPTIME_DAYS": return ((int)uptime.TotalDays).ToString(inv);
                case "COUNTDOWN" when DateTime.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd", inv, DateTimeStyles.None, out var target):
                    return Math.Max(0, (target.Date - now.Date).Days).ToString(inv);
                default: return m.Value;
            }
        }).ToUpperInvariant();

    [GeneratedRegex(@"\{([A-Z_]+)(?::([^}]*))?\}")]
    private static partial Regex TokenPattern();
}

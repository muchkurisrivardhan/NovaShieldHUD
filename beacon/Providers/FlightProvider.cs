using Beacon.Core;

namespace Beacon.Providers;

/// <summary>
/// "flight" → Tab → a flight number → Enter: Dotline shows that plane live on the wallpaper map.
/// Also answers "flight AA100" in one go, and offers to stop the flight being tracked.
///
/// Beacon itself never goes online here: it only writes down which flight you asked for.
/// Dotline fetches positions (from adsb.lol), and only while a flight is being tracked.
/// </summary>
public sealed class FlightProvider : IResultProvider
{
    const string Glyph = "";   // Segoe Fluent Icons: Airplane

    public string Name => "Flights";

    public int Weight => 40;

    static readonly string[] Words = ["flight", "flights", "track flight", "flight tracker", "plane"];

    public static readonly SearchScope Scope = new(
        "Flight", Glyph, IconUrl: null, HomeUrl: null, UrlFor: _ => "", Row: TrackRow);

    static SearchResult TrackRow(string typed)
    {
        var flight = Suite.Normalize(typed);
        if (flight.Length == 0)
        {
            return new SearchResult
            {
                Title = "Type a flight number",
                Subtitle = "Like AA100, BA117 or UAL2678  ·  Esc to leave",
                Glyph = Glyph, Source = "Flight", Score = 1,
                InvokeAsync = _ => Task.CompletedTask,
            };
        }

        var callsign = Suite.ToCallsign(flight);
        return new SearchResult
        {
            Title = $"Track {flight} on your wallpaper",
            Subtitle = (callsign == flight ? "" : $"Callsign {callsign}  ·  ") + "Live position every 15 s  ·  Enter to track",
            Glyph = Glyph, Source = "Flight", Score = 1,
            InvokeAsync = ctx =>
            {
                ctx.Dismiss();
                ctx.Notify(Suite.Track(flight));
                return Task.CompletedTask;
            },
        };
    }

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        var text = query.Text.ToLowerInvariant();
        var rows = new List<SearchResult>();
        if (text.Length < 3) return Task.FromResult<IReadOnlyList<SearchResult>>(rows);

        // "flight AA100": track straight away.
        if (query.Prefix is "flight" or "flights" && query.Rest.Length > 0)
        {
            var row = TrackRow(query.Rest);
            row.Score = 1000;
            rows.Add(row);
        }
        // "fli…": the scope row. Tab (or Enter) turns the bar into a flight-number box.
        else if (Words.Any(w => w.StartsWith(text)))
        {
            rows.Add(new SearchResult
            {
                Title = "Track a flight",
                Subtitle = "Tab, then type a flight number  ·  shows live on your wallpaper map",
                Glyph = Glyph, Source = "Flight", Score = 900,
                Scope = Scope, EntersScopeOnInvoke = true,
                InvokeAsync = _ => Task.CompletedTask,
            });
        }

        if (Words.Any(w => w.StartsWith(text) || text.StartsWith(w)) && Suite.CurrentFlight() is { } current)
        {
            rows.Add(new SearchResult
            {
                Title = $"Stop tracking {current.Flight}",
                Subtitle = "Removes the plane from your wallpaper map",
                Glyph = Glyph, Source = "Flight", Score = 850,
                InvokeAsync = ctx =>
                {
                    ctx.Dismiss();
                    ctx.Notify(Suite.StopTracking());
                    return Task.CompletedTask;
                },
            });
        }
        return Task.FromResult<IReadOnlyList<SearchResult>>(rows);
    }
}

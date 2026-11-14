namespace Beacon.Core;

/// <summary>
/// A site the bar can search "inside".
///
/// Pressing Tab on a site row (or typing a site prefix followed by a space, like "yt ")
/// turns the bar into a single-site search box: the site's icon and name sit in a chip at
/// the start of the bar, and everything typed after it becomes that site's query. The same
/// pattern as a browser's "Press Tab to search YouTube".
/// </summary>
/// <param name="Name">Shown in the chip, e.g. "YouTube".</param>
/// <param name="IconUrl">Any URL on the site; its favicon becomes the chip icon.</param>
/// <param name="HomeUrl">Opened when Enter is pressed with nothing typed.</param>
/// <param name="UrlFor">Builds the search URL for a typed term.</param>
public sealed record SearchScope(
    string Name,
    string Glyph,
    string? IconUrl,
    string? HomeUrl,
    Func<string, string> UrlFor);

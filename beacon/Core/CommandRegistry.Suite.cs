namespace Beacon.Core;

public sealed partial class CommandRegistry
{
    /// <summary>The other NovaShield HUD apps: Dotline (desktop widgets, focus timer, wallpaper map).</summary>
    private static IEnumerable<BeaconCommand> SuiteCommands()
    {
        yield return new BeaconCommand
        {
            Id = "open_dotline",
            Title = "Open Dotline",
            Subtitle = "Life, goals, focus timer and the wallpaper map",
            Description = "Start Dotline, the desktop widgets app (life timer, goals, focus timer, wallpaper world map).",
            Glyph = "",
            Keywords = ["dotline", "widgets", "life", "goals", "focus", "wallpaper", "start dotline", "launch dotline"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                return Task.FromResult(Suite.OpenDotline());
            },
        };

        yield return new BeaconCommand
        {
            Id = "close_dotline",
            Title = "Close Dotline",
            Subtitle = "Quit the widgets; everything stays saved",
            Description = "Quit Dotline, the desktop widgets app. Its settings and progress are already saved.",
            Glyph = "",
            Keywords = ["dotline", "widgets", "quit dotline", "exit dotline", "stop dotline", "hide widgets"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                return Task.FromResult(Suite.CloseDotline());
            },
        };
    }
}

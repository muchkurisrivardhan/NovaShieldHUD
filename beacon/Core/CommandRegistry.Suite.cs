namespace Beacon.Core;

public sealed partial class CommandRegistry
{
    /// <summary>
    /// The other NovaShield HUD apps: Dotline (desktop widgets, focus timer, wallpaper map) and
    /// Filee (drag-and-drop file converter).
    /// </summary>
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

        yield return new BeaconCommand
        {
            Id = "open_filee",
            Title = "Convert files (Filee)",
            Subtitle = "Hold Ctrl and drag files: pick a format in the ring",
            Description = "Start Filee, the drag-and-drop file converter, and show its window. Hold Ctrl while dragging files to open the format ring at the cursor.",
            Glyph = "\uE8AB",
            Keywords = ["filee", "convert", "converter", "convert file", "png", "pdf", "jpg", "webp", "mp4", "format", "open filee", "start filee"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                return Task.FromResult(Suite.OpenFilee(show: true));
            },
        };

        yield return new BeaconCommand
        {
            Id = "close_filee",
            Title = "Close Filee",
            Subtitle = "Quit the converter; Ctrl+drag stops opening the ring",
            Description = "Quit Filee, the drag-and-drop file converter. Its settings and presets are saved.",
            Glyph = "\uE8AB",
            Keywords = ["filee", "quit filee", "exit filee", "stop filee", "close converter"],
            RunAsync = ctx =>
            {
                ctx.Dismiss();
                return Task.FromResult(Suite.CloseFilee());
            },
        };
    }
}

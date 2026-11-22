using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Beacon.UI;

/// <summary>
/// A small picture of the launcher in a style, for the Appearance page: the header strip of
/// the framed looks, the command bar with its placeholder, two results (the first picked) and
/// the status line, in the style's own colours, fonts and corners.
/// </summary>
public static class LauncherPreview
{
    public static FrameworkElement Build(StyleLook look)
    {
        var placeholder = look.Style switch
        {
            "tactical" => "Enter command — apps, files, sites, or ? for Claude",
            "cyberpunk" => "Jack in — apps, files, sites, or ? for Claude",
            "crt" => "C:\\> apps, files, sites, or ? for Claude_",
            "hud" => "Awaiting command — apps, files, sites, or ? for Claude",
            _ => "Search apps, files and commands—or ? to ask Claude",
        };
        var root = new Grid { Margin = new Thickness(4) };
        root.Effect = look.Chamfer ? new DropShadowEffect { Color = look.Accent, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 } : null;
        var surface = look.Style == "tactical" ? look.Surface : look.Page;
        root.Children.Add(look.Plate(surface, 0xF6, look.Chamfer ? look.Accent : look.Border, 1, 12));
        if (look.Scanlines)
            root.Children.Add(new Rectangle { Fill = look.ScanlineBrush(), Margin = new Thickness(2, 12, 2, 12), IsHitTestVisible = false });

        var stack = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        root.Children.Add(stack);
        var font = look.Font;
        TextBlock T(string text, double size, Color c, FontFamily? f = null, bool bold = false) => new()
        {
            Text = text, FontSize = size, Foreground = look.Brush(c), FontFamily = f ?? font,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // Header strip.
        if (look.Header)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var online = new StackPanel { Orientation = Orientation.Horizontal };
            online.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = look.Brush(look.Accent), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            online.Children.Add(T(look.Style == "tactical" ? "GOOD EVENING" : "SYSTEMS ONLINE", 8.5, look.Accent, look.Tag));
            DockPanel.SetDock(online, Dock.Right);
            head.Children.Add(online);
            head.Children.Add(T(look.Style == "crt" ? "BEACON // TERMINAL" : "BEACON  //  COMMAND INTERFACE", 8.5, look.Accent, look.Tag));
            stack.Children.Add(head);
        }

        // Command bar.
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 6), MinHeight = 30 };
        if (look.Style == "tactical") bar.Children.Add(look.Plate(look.Page, 0xE8, look.Border, 1, 6));
        var barRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 5, 8, 5) };
        barRow.Children.Add(new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(look.Chamfer ? 0 : 9), Margin = new Thickness(0, 0, 8, 0),
            BorderBrush = look.Brush(look.Accent), BorderThickness = new Thickness(1.2), Background = look.Brush(look.Accent, 0x22),
        });
        barRow.Children.Add(T(placeholder, 11.5, look.Muted));
        bar.Children.Add(barRow);
        stack.Children.Add(bar);
        if (!look.Header || look.Style == "hud" || look.Style == "cyberpunk" || look.Style == "crt")
            stack.Children.Add(new Rectangle { Height = 1, Fill = look.Brush(look.Chamfer ? look.Accent : look.Text, 0x30), Margin = new Thickness(0, 0, 0, 6) });

        // Two results, the first picked.
        stack.Children.Add(Row(look, "Ask Claude to do something", "Type an instruction", "AI", true, T));
        stack.Children.Add(Row(look, "Notion", "App", "APP", false, T));

        // Status line.
        var status = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var clock = T(DateTime.Now.ToString("HH:mm:ss"), 9.5, look.Accent, look.Tag, true);
        DockPanel.SetDock(clock, Dock.Right);
        status.Children.Add(clock);
        status.Children.Add(T(look.Header ? "▸ 577,090 FILES INDEXED" : "577,090 files indexed", 9, look.Muted, look.Header ? look.Tag : font));
        stack.Children.Add(status);
        return new Viewbox { Child = new Border { Width = 420, Child = root }, Stretch = Stretch.Uniform };
    }

    static FrameworkElement Row(StyleLook look, string title, string sub, string tag, bool picked,
        Func<string, double, Color, FontFamily?, bool, TextBlock> T)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        if (picked)
        {
            if (look.Style == "tactical") row.Children.Add(look.Plate(look.SurfaceAlt, 0xF0, look.Border, 1, 6));
            else row.Children.Add(new Border
            {
                Background = look.Brush(look.Chamfer ? look.Accent : look.Text, look.Chamfer ? (byte)0x2A : (byte)0x1F),
                CornerRadius = new CornerRadius(look.Chamfer ? 0 : 8),
            });
            if (look.Chamfer) row.Children.Add(new Rectangle { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Fill = look.Brush(look.Accent) });
        }
        var line = new DockPanel { Margin = new Thickness(12, 4, 10, 5) };
        var t = T(look.Header ? $"[ {tag} ]" : tag, 9, look.Header ? look.Accent : look.Muted, look.Tag, false);
        t.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(t, Dock.Right);
        line.Children.Add(t);
        var words = new StackPanel();
        words.Children.Add(T(title, 12.5, look.Text, null, false));
        words.Children.Add(T(sub, 9.5, picked && look.Chamfer ? look.Accent : look.Muted, null, false));
        line.Children.Add(words);
        row.Children.Add(line);
        return row;
    }
}

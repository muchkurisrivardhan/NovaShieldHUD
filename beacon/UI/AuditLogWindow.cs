using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beacon.Assistant;
using Beacon.Core;

namespace Beacon.UI;

/// <summary>
/// Sentinel's activity log: what Nova did, newest first, with its risk tier and whether it ran,
/// was approved, declined or blocked. Read-only; clearing happens on the settings page.
/// Dressed in the current Beacon style.
/// </summary>
public sealed class AuditLogWindow : Window
{
    const int MaxRows = 500;

    readonly Color _accent, _page, _surface, _border, _text, _secondary;

    public AuditLogWindow(Settings settings, IReadOnlyList<AuditEntry> entries)
    {
        var p = Suite.PaletteFor(settings);
        Color C(string key, string fallback) => Parse(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback);
        _accent = Parse(p.Accent);
        _page = C("page", "#1C1C1F");
        _surface = C("surface", "#26262A");
        _border = C("border", "#3A3A40");
        _text = C("text", "#F2F2F2");
        _secondary = C("textSecondary", "#A0A0A8");

        Title = "Nova activity log";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Width = 880; Height = 600;
        MinWidth = 560; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily(p.Font + ", Segoe UI");

        var grid = new Grid { Margin = new Thickness(18, 12, 18, 14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: title, count, close. Drag to move.
        var header = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Sentinel · what Nova did", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush(_text) });
        titles.Children.Add(new TextBlock
        {
            Text = entries.Count == 0
                ? "Nothing yet. Every action Nova takes will show up here."
                : $"{entries.Count} in the last {AuditLog.Keep.TotalDays:0} days, newest first. Stored on this PC only, encrypted.",
            FontSize = 12, Foreground = Brush(_secondary), Margin = new Thickness(0, 3, 0, 0),
        });
        header.Children.Add(titles);
        var close = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
            Background = Brush(_surface), BorderBrush = Brush(WithAlpha(_accent, 0x90)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "✕", FontSize = 12, Foreground = Brush(_text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "Close (Esc)",
        };
        close.MouseLeftButtonUp += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        grid.Children.Add(header);

        // Column headings.
        var headings = Row(["When", "Who", "What", "Risk", "Result"], heading: true);
        headings.Margin = new Thickness(0, 14, 0, 4);
        Grid.SetRow(headings, 1);
        grid.Children.Add(headings);

        // The rows.
        var list = new StackPanel();
        foreach (var e in entries.Reverse().Take(MaxRows))
        {
            var what = string.IsNullOrEmpty(e.Note) ? e.Action : $"{e.Action}  ({e.Note})";
            var row = Row([e.Time.ToString("MMM d, h:mm tt"), e.Actor, what, e.Risk, e.Outcome], heading: false, outcome: e.Outcome);
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var frame = new Border
        {
            Child = scroll, BorderBrush = Brush(WithAlpha(_border, 0xD0)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Background = Brush(WithAlpha(_surface, 0xC0)), Padding = new Thickness(0, 2, 0, 2),
        };
        Grid.SetRow(frame, 2);
        grid.Children.Add(frame);

        Content = new Border
        {
            Margin = new Thickness(8), CornerRadius = new CornerRadius(12),
            Background = Brush(WithAlpha(_page, 0xF7)), BorderBrush = Brush(WithAlpha(_accent, 0xB0)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.5 },
            Child = grid,
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>One row of five columns; the result is coloured so blocks and declines stand out.</summary>
    Grid Row(string[] cells, bool heading, string? outcome = null)
    {
        var row = new Grid { Margin = new Thickness(0) };
        foreach (var width in new[] { 130.0, 60.0, -1, 110.0, 130.0 })
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width) });
        for (var i = 0; i < cells.Length; i++)
        {
            var color = heading ? _secondary
                : i == 4 && outcome is "Blocked" or "Declined by you" ? Color.FromRgb(0xF0, 0x6A, 0x5A)
                : i == 4 && outcome == "Approved by you" ? _accent
                : i == 2 ? _text : _secondary;
            var cell = new TextBlock
            {
                Text = cells[i], FontSize = heading ? 11.5 : 12.5, Foreground = Brush(color),
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = i == 2 ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(12, heading ? 0 : 6, 8, heading ? 0 : 6),
            };
            Grid.SetColumn(cell, i);
            row.Children.Add(cell);
        }
        if (!heading)
        {
            var line = new Border { BorderBrush = Brush(WithAlpha(_border, 0x80)), BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetColumnSpan(line, 5);
            row.Children.Insert(0, line);
        }
        return row;
    }

    static SolidColorBrush Brush(Color c) => new(c);

    static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x5B, 0x8D, 0xEF); }
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

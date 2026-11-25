using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beacon.Assistant;
using Beacon.Core;

namespace Beacon.UI;

/// <summary>
/// What Nova remembers, newest first. Each memory can be deleted, or marked "This PC only" so
/// cloud brains never see it. "Delete all" wipes the store after a confirmation. Dressed in the
/// current Beacon style; the list redraws whenever the store changes.
/// </summary>
public sealed class MemoryWindow : Window
{
    readonly MemoryStore _store;
    readonly StackPanel _list = new();
    readonly TextBlock _count = new();
    readonly Color _accent, _page, _surface, _border, _text, _secondary;

    public MemoryWindow(Settings settings, MemoryStore store)
    {
        _store = store;
        var p = Suite.PaletteFor(settings);
        Color C(string key, string fallback) => Parse(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback);
        _accent = Parse(p.Accent);
        _page = C("page", "#1C1C1F");
        _surface = C("surface", "#26262A");
        _border = C("border", "#3A3A40");
        _text = C("text", "#F2F2F2");
        _secondary = C("textSecondary", "#A0A0A8");

        Title = "What Nova remembers";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Width = 720; Height = 560;
        MinWidth = 460; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily(p.Font + ", Segoe UI");

        var grid = new Grid { Margin = new Thickness(18, 12, 18, 14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header.
        var header = new Grid { Cursor = Cursors.SizeAll, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "What Nova remembers", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brush(_text) });
        _count.FontSize = 12;
        _count.Foreground = Brush(_secondary);
        _count.Margin = new Thickness(0, 3, 0, 0);
        _count.TextWrapping = TextWrapping.Wrap;
        titles.Children.Add(_count);
        header.Children.Add(titles);
        var close = Pill("✕", primary: false);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.ToolTip = "Close (Esc)";
        close.MouseLeftButtonUp += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        grid.Children.Add(header);

        // The memories.
        var frame = new Border
        {
            Child = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            BorderBrush = Brush(WithAlpha(_border, 0xD0)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Background = Brush(WithAlpha(_surface, 0xC0)),
        };
        Grid.SetRow(frame, 1);
        grid.Children.Add(frame);

        // Footer: how to add, and Delete all.
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = "To add one, tell Nova \"remember that…\". Stored on this PC only, encrypted.",
            FontSize = 11.5, Foreground = Brush(_secondary), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        var deleteAll = Pill("Delete all", primary: false);
        deleteAll.MouseLeftButtonUp += (_, _) =>
        {
            if (_store.All().Count == 0) return;
            if (MessageBox.Show(this, "Delete everything Nova remembers? This can't be undone.", "Delete all memories",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                _store.Clear();
        };
        Grid.SetColumn(deleteAll, 1);
        footer.Children.Add(deleteAll);
        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        Content = new Border
        {
            Margin = new Thickness(8), CornerRadius = new CornerRadius(12),
            Background = Brush(WithAlpha(_page, 0xF7)), BorderBrush = Brush(WithAlpha(_accent, 0xB0)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.5 },
            Child = grid,
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Action redraw = () => Dispatcher.BeginInvoke(Redraw);
        _store.Changed += redraw;
        Closed += (_, _) => _store.Changed -= redraw;
        Redraw();
    }

    void Redraw()
    {
        _list.Children.Clear();
        var items = _store.All();
        var hidden = items.Count(m => m.Private);
        _count.Text = items.Count == 0
            ? "Nothing yet."
            : $"{items.Count} memories, newest first. {hidden} marked This PC only (cloud brains never see those).";
        if (items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "Nova doesn't remember anything about you yet. Tell it \"remember that my sister is Priya\" and it will.",
                Foreground = Brush(_secondary), FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14),
            });
            return;
        }
        foreach (var m in items.Reverse()) _list.Children.Add(Row(m));
    }

    UIElement Row(MemoryItem m)
    {
        var row = new Grid { Margin = new Thickness(0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Margin = new Thickness(14, 9, 10, 9) };
        text.Children.Add(new TextBlock { Text = m.Text, FontSize = 13, Foreground = Brush(_text), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = m.Created.ToString("MMM d, yyyy"), FontSize = 11, Foreground = Brush(_secondary), Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(text);

        var local = new CheckBox
        {
            Content = new TextBlock { Text = "This PC only", Foreground = Brush(_secondary), FontSize = 12 },
            IsChecked = m.Private, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Ticked: cloud brains (Claude, Muse, OpenRouter…) never see this memory.",
        };
        local.Click += (_, _) => _store.SetPrivate(m.Id, local.IsChecked == true);
        Grid.SetColumn(local, 1);
        row.Children.Add(local);

        var delete = Pill("Delete", primary: false);
        delete.Margin = new Thickness(0, 0, 12, 0);
        delete.VerticalAlignment = VerticalAlignment.Center;
        delete.MouseLeftButtonUp += (_, _) => _store.Remove(m.Id);
        Grid.SetColumn(delete, 2);
        row.Children.Add(delete);

        return new Border { Child = row, BorderBrush = Brush(WithAlpha(_border, 0x80)), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    Border Pill(string label, bool primary) => new()
    {
        CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Padding = new Thickness(12, 5, 12, 6),
        Background = Brush(primary ? WithAlpha(_accent, 0x30) : WithAlpha(_surface, 0xF2)),
        BorderBrush = Brush(primary ? _accent : WithAlpha(_accent, 0x90)), BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = label, FontSize = 12.5, Foreground = Brush(_text), HorizontalAlignment = HorizontalAlignment.Center },
    };

    static SolidColorBrush Brush(Color c) => new(c);

    static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x5B, 0x8D, 0xEF); }
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

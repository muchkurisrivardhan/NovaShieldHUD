using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Beacon.Core;
using Microsoft.Win32;

namespace Beacon.UI;

/// <summary>
/// A plain, read-only viewer for a Code Ring result run on a selected file. A file in Explorer
/// has no text caret to paste into, so the result (fixed code, tests, an explanation...) is
/// shown here instead: a monospaced, selectable box the user can read and copy from. Nothing is
/// written to disk. Dressed in the current Beacon style, like the ring itself.
///
/// Esc or the × closes it; drag the header to move; Copy puts the whole result on the clipboard.
/// </summary>
public sealed class RingResultWindow : Window
{
    readonly Color _accent, _page, _surface, _border, _text, _secondary;
    readonly FontFamily _font;

    /// <param name="saveDir">Folder the Save dialog opens in (the source file's folder). No Save button when null.</param>
    /// <param name="saveName">The default filename the Save dialog proposes (a new sibling, never the original).</param>
    public RingResultWindow(Settings settings, string title, string result, string? saveDir = null, string? saveName = null)
    {
        var p = Suite.PaletteFor(settings);
        Color C(string key, string fallback) => Parse(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback);
        _accent = Parse(p.Accent);
        _page = C("page", "#1C1C1F");
        _surface = C("surface", "#26262A");
        _border = C("border", "#3A3A40");
        _text = C("text", "#F2F2F2");
        _secondary = C("textSecondary", "#A0A0A8");
        _font = new FontFamily(p.Font + ", Segoe UI");
        var glow = settings.OverlayStyle is "hud" or "cyberpunk" or "crt";

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Width = 760; Height = 580;
        MinWidth = 420; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = _font;
        Title = title;

        var root = new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(WithAlpha(_page, 0xF7)),
            BorderBrush = new SolidColorBrush(WithAlpha(_accent, 0xB0)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = glow ? _accent : Colors.Black, BlurRadius = 24, ShadowDepth = 0, Opacity = glow ? 0.45 : 0.5 },
        };

        var grid = new Grid { Margin = new Thickness(16, 12, 16, 14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: title + close. Drag to move.
        var header = new Grid { Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_text), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var close = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
            Background = new SolidColorBrush(_surface), BorderBrush = new SolidColorBrush(WithAlpha(_accent, 0x90)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "✕", FontSize = 12, Foreground = new SolidColorBrush(_text), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = "Close (Esc)",
        };
        close.MouseLeftButtonUp += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // The result: monospaced, read-only, selectable, scrollable both ways.
        var box = new TextBox
        {
            Text = result,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace"),
            FontSize = 13,
            Background = new SolidColorBrush(WithAlpha(_surface, 0xF2)),
            Foreground = new SolidColorBrush(_text),
            BorderBrush = new SolidColorBrush(WithAlpha(_border, 0xD0)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            CaretBrush = new SolidColorBrush(_accent),
        };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);

        // Footer: Copy + a hint.
        var footer = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock
        {
            Text = "Nothing was saved — read-only. Esc to close.", FontSize = 11.5,
            Foreground = new SolidColorBrush(_secondary), VerticalAlignment = VerticalAlignment.Center,
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);

        // Save: offered only in file mode. A Save dialog, pre-filled with a NEW sibling name, so
        // the common path writes a fresh file and any overwrite is the user's explicit choice.
        if (saveName is not null)
        {
            var save = Button("Save as…", filled: false);
            save.Margin = new Thickness(0, 0, 10, 0);
            save.MouseLeftButtonUp += (_, _) =>
            {
                var dialog = new SaveFileDialog
                {
                    FileName = saveName,
                    InitialDirectory = saveDir is not null && Directory.Exists(saveDir) ? saveDir : "",
                    Filter = "All files (*.*)|*.*",
                    OverwritePrompt = true,
                    AddExtension = false,
                };
                if (dialog.ShowDialog(this) != true) return;
                try
                {
                    File.WriteAllText(dialog.FileName, result);
                    ((TextBlock)save.Child).Text = "Saved";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ((TextBlock)save.Child).Text = "Couldn't save";
                }
            };
            buttons.Children.Add(save);
        }

        var copy = Button("Copy", filled: true);
        copy.MouseLeftButtonUp += (_, _) =>
        {
            try { Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, result), copy: true); ((TextBlock)copy.Child).Text = "Copied"; }
            catch (System.Runtime.InteropServices.ExternalException) { ((TextBlock)copy.Child).Text = "Try again"; }
        };
        buttons.Children.Add(copy);

        Grid.SetRow(footer, 2);
        grid.Children.Add(footer);

        root.Child = grid;
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>A pill button: accent-filled for the primary action, outlined for a secondary one.</summary>
    Border Button(string text, bool filled) => new()
    {
        CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Padding = new Thickness(16, 6, 16, 7),
        Background = new SolidColorBrush(filled ? WithAlpha(_accent, 0x30) : WithAlpha(_surface, 0xF2)),
        BorderBrush = new SolidColorBrush(filled ? _accent : WithAlpha(_accent, 0x90)),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(_text) },
    };

    static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x5B, 0x8D, 0xEF); }
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

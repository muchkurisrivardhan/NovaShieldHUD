using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Beacon.Core;

namespace Beacon.UI.Pet;

/// <summary>
/// The quick-ask box: a tap of the talk key opens it beside Kai, to type to the AI instead of
/// talking. Enter sends, Esc (or clicking elsewhere) closes it and gives the keyboard back to
/// the app you were in; the mic button switches to talking. It wears the launcher's style.
/// </summary>
public sealed class QuickAskBox : Window
{
    readonly TextBox _input = new();
    readonly TextBlock _hint = new();
    bool _closing;

    /// <summary>Enter: what was typed.</summary>
    public event Action<string>? Sent;

    /// <summary>The mic button: talk instead.</summary>
    public event Action? Talk;

    /// <summary>Closed without sending.</summary>
    public event Action? Cancelled;

    public QuickAskBox(Settings settings)
    {
        var look = StyleLook.For(settings);
        Title = "Ask Kai";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        FontFamily = look.Font;

        var root = new Grid { Margin = new Thickness(8) };
        root.Effect = new DropShadowEffect { Color = look.Chamfer ? look.Accent : Colors.Black, BlurRadius = 16, ShadowDepth = 0, Opacity = look.Chamfer ? 0.45 : 0.5 };
        root.Children.Add(look.Plate(look.Page, 0xF4, look.Chamfer ? look.Accent : look.Border, 1, 10));
        if (look.Scanlines)
            root.Children.Add(new System.Windows.Shapes.Rectangle { Fill = look.ScanlineBrush(), Margin = new Thickness(2, 10, 2, 10), IsHitTestVisible = false });

        var stack = new StackPanel { Margin = new Thickness(14, 9, 10, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = look.Header ? look.HeaderText("ask") : "Ask Kai",
            FontFamily = look.Header ? look.Tag : look.Font, FontSize = 11, Margin = new Thickness(2, 0, 0, 6),
            Foreground = look.Brush(look.Header ? look.Accent : look.Muted),
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var field = new Grid();
        _input.Background = Brushes.Transparent;
        _input.BorderThickness = new Thickness(0);
        _input.Foreground = look.Brush(look.Text);
        _input.CaretBrush = look.Brush(look.Accent);
        _input.SelectionBrush = look.Brush(look.Accent);
        _input.FontSize = 15;
        _input.FontFamily = look.Style == "crt" ? look.Tag : look.Font;
        _input.TextWrapping = TextWrapping.Wrap;
        _input.MaxHeight = 120;
        _input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _input.Padding = new Thickness(0, 2, 0, 2);
        _hint.Text = look.Style == "crt" ? "type and press Enter_" : "Type to Kai… (Enter to send)";
        _hint.Foreground = look.Brush(look.Muted);
        _hint.FontSize = 15;
        _hint.FontFamily = _input.FontFamily;
        _hint.IsHitTestVisible = false;
        _hint.Margin = new Thickness(2, 2, 0, 0);
        field.Children.Add(_hint);
        field.Children.Add(_input);
        row.Children.Add(field);

        var mic = new Border
        {
            Width = 30, Height = 30, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(look.Chamfer ? 0 : 15), Background = look.Brush(look.Accent, 0x24),
            BorderBrush = look.Brush(look.Accent, 0x90), BorderThickness = new Thickness(1), ToolTip = "Talk instead",
            Child = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Foreground = look.Brush(look.Accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        mic.MouseLeftButtonUp += (_, e) => { e.Handled = true; Finish(() => Talk?.Invoke()); };
        Grid.SetColumn(mic, 1);
        row.Children.Add(mic);
        stack.Children.Add(row);
        stack.Children.Add(new TextBlock
        {
            Text = "Enter: send  ·  Shift+Enter: new line  ·  Esc: close", FontSize = 10.5, Margin = new Thickness(2, 6, 0, 0),
            Foreground = look.Brush(look.Muted),
        });
        root.Children.Add(stack);
        Content = root;

        _input.TextChanged += (_, _) => _hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Finish(() => Cancelled?.Invoke()); }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                var text = _input.Text.Trim();
                if (text.Length > 0) Finish(() => Sent?.Invoke(text));
            }
        };
        // Clicked somewhere else: an empty box goes away; one with words in it stays until Esc.
        Deactivated += (_, _) => { if (_input.Text.Trim().Length == 0) Finish(() => Cancelled?.Invoke()); };

        Opacity = 0;
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            Activate();
            _input.Focus();
            Keyboard.Focus(_input);
        };
    }

    /// <summary>Beside Kai: right of it if there's room, else left; level with its middle.</summary>
    public void PlaceBeside(Rect pet)
    {
        var area = SystemParameters.WorkArea;
        var x = pet.Right + 4;
        if (x + Width > area.Right) x = pet.Left - Width - 4;
        Left = Math.Max(area.Left, x);
        Top = Math.Clamp(pet.Top + pet.Height / 2 - 50, area.Top, area.Bottom - 120);
    }

    void Finish(Action then)
    {
        if (_closing) return;
        _closing = true;
        Close();
        then();
    }
}

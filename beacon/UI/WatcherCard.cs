using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Beacon.Assistant;
using Beacon.Core;

namespace Beacon.UI;

/// <summary>
/// A watcher's card: a small panel in Beacon's style, stacked above the tray at the bottom
/// right. It never takes the keyboard (you keep typing where you were), stays until you answer
/// it, and runs an offer only when you click it. At most four are on screen; a fifth pushes out
/// the oldest.
/// </summary>
public sealed class WatcherCard : Window
{
    const double CardWidth = 360, Gap = 8, Edge = 12;
    const int MaxOpen = 4;

    static readonly List<WatcherCard> Open = [];

    readonly Color _accent, _page, _surface, _border, _text, _secondary;

    /// <summary>Shows an alert, with a sound unless the user is busy or it's quiet hours.</summary>
    public static WatcherCard Present(Settings settings, WatcherAlert alert)
    {
        var card = new WatcherCard(settings, alert);
        Open.Add(card);
        card.Closed += (_, _) => { Open.Remove(card); Relayout(); };
        card.SizeChanged += (_, _) => Relayout();
        card.Show();
        if (!alert.Silent)
            (alert.Kind == "reminder" ? SystemSounds.Exclamation : SystemSounds.Asterisk).Play();
        while (Open.Count > MaxOpen) Open[0].Close();
        return card;
    }

    /// <summary>
    /// A yes/no card, for an outside agent's request: <paramref name="yes"/> or <paramref name="no"/>.
    /// Closing it, being pushed out by newer cards, or two minutes without an answer all count
    /// as no, so an agent is never left waiting forever and nothing is approved by default.
    /// </summary>
    public static Task<bool> AskAsync(Settings settings, string kind, string title, string body, string yes = "Allow", string no = "Deny")
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var card = Present(settings, new WatcherAlert(kind, title, body,
        [
            new WatcherOffer(yes, () => answer.TrySetResult(true), Primary: true),
            new WatcherOffer(no, () => answer.TrySetResult(false)),
        ], Silent: false));
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timeout.Tick += (_, _) => card.Close();
        card.Closed += (_, _) => { timeout.Stop(); answer.TrySetResult(false); };
        timeout.Start();
        return answer.Task;
    }

    /// <summary>Newest at the bottom, older ones stacked above it.</summary>
    static void Relayout()
    {
        var work = SystemParameters.WorkArea;
        var bottom = work.Bottom - Edge;
        for (var i = Open.Count - 1; i >= 0; i--)
        {
            var c = Open[i];
            var h = c.ActualHeight > 0 ? c.ActualHeight : 160;
            c.Left = work.Right - CardWidth - Edge;
            c.Top = bottom - h;
            bottom = c.Top - Gap;
        }
    }

    WatcherCard(Settings settings, WatcherAlert alert)
    {
        var p = Suite.PaletteFor(settings);
        Color C(string key, string fallback) => Parse(p.Colors is { } c && c.TryGetValue(key, out var v) ? v : fallback);
        _accent = Parse(p.Accent);
        _page = C("page", "#1C1C1F");
        _surface = C("surface", "#26262A");
        _border = C("border", "#3A3A40");
        _text = C("text", "#F2F2F2");
        _secondary = C("textSecondary", "#A0A0A8");
        var glow = settings.OverlayStyle is "hud" or "cyberpunk" or "crt";

        Title = "Nova: " + alert.Title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = CardWidth;
        SizeToContent = SizeToContent.Height;
        Left = -10000; Top = -10000;   // placed by Relayout once its height is known
        FontFamily = new FontFamily(p.Font + ", Segoe UI");

        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };

        // Header: a dot, "NOVA · REMINDER", and a close cross.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tag = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tag.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brush(_accent), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        tag.Children.Add(new TextBlock { Text = "NOVA · " + alert.Kind.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush(_accent) });
        header.Children.Add(tag);
        var close = new TextBlock { Text = "✕", FontSize = 12, Foreground = Brush(_secondary), Cursor = Cursors.Hand, ToolTip = "Dismiss", Padding = new Thickness(6, 0, 0, 0) };
        close.MouseLeftButtonUp += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock { Text = alert.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush(_text), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = alert.Body, FontSize = 13, LineHeight = 19, Foreground = Brush(_secondary),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });

        // The offers. Each runs only on a click, then the card goes.
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var offer in alert.Offers)
        {
            var pill = new Border
            {
                CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 0, 8, 6),
                Background = Brush(offer.Primary ? WithAlpha(_accent, 0x38) : WithAlpha(_page, 0xF0)),
                BorderBrush = Brush(offer.Primary ? _accent : WithAlpha(_accent, 0x80)), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = offer.Label, FontSize = 12.5, FontWeight = offer.Primary ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brush(_text) },
            };
            pill.MouseLeftButtonUp += (_, _) =>
            {
                offer.Run();   // first: closing an approval card on its own counts as a no
                Close();
            };
            buttons.Children.Add(pill);
        }
        stack.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(12),
            Background = Brush(WithAlpha(_surface, 0xF7)),
            BorderBrush = Brush(WithAlpha(_accent, 0xB0)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { Color = glow ? _accent : Colors.Black, BlurRadius = 20, ShadowDepth = 0, Opacity = glow ? 0.45 : 0.5 },
            Child = stack,
        };

        // Never takes the keyboard from the app you're in.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ex = Interop.NativeMethods.GetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= Interop.NativeMethods.WS_EX_TOOLWINDOW | Interop.NativeMethods.WS_EX_NOACTIVATE;
            Interop.NativeMethods.SetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        };
        Opacity = 0;
        Loaded += (_, _) =>
        {
            Relayout();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        };
    }

    static SolidColorBrush Brush(Color c) => new(c);

    static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x5B, 0x8D, 0xEF); }
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

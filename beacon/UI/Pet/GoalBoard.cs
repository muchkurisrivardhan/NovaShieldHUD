using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Beacon.Assistant;
using Beacon.Core;

namespace Beacon.UI.Pet;

/// <summary>
/// Kai's plan as a checklist, in the same panel style as Kai's explanations ("KAI // GOAL"),
/// placed where Settings > Drawing puts that panel. Each step shows what it does and, for the
/// ones that will still ask (can't be undone, leaves the PC), a note saying so. Steps tick off
/// as they run. Stop (or ×) cancels the rest. It never takes the keyboard.
/// </summary>
public sealed class GoalBoard : Window
{
    readonly StyleLook _look;
    readonly List<(TextBlock Glyph, TextBlock Label, TextBlock Detail)> _rows = [];
    readonly TextBlock _footer = new();
    readonly Border _stop;
    readonly Rect _area;
    readonly bool _anchorBottom;
    readonly int _count;
    readonly Action _onStop;
    bool _finished;

    public GoalBoard(Settings settings, GoalPlan plan, Rect area, Action onStop)
    {
        _look = StyleLook.For(settings);
        _area = area;
        _count = plan.Steps.Count;
        _onStop = onStop;
        _anchorBottom = settings.DrawPlace is not ("left" or "right" or "full");

        Title = "Kai's plan: " + plan.Title;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Math.Min(area.Width, 520);
        SizeToContent = SizeToContent.Height;
        MaxHeight = area.Height;
        Left = area.Left;
        Top = area.Top;
        FontFamily = _look.Font;

        var root = new Grid { Margin = new Thickness(6) };
        root.Effect = new DropShadowEffect { Color = _look.Chamfer ? _look.Accent : Colors.Black, BlurRadius = 18, ShadowDepth = 0, Opacity = _look.Chamfer ? 0.35 : 0.55 };
        root.Children.Add(_look.Plate(_look.Page, 0xF2, _look.Chamfer ? _look.Accent : _look.Border, 1, 16));
        if (_look.Scanlines)
            root.Children.Add(new Rectangle { Fill = _look.ScanlineBrush(), Margin = new Thickness(2, 16, 2, 16), IsHitTestVisible = false });

        var stack = new StackPanel { Margin = new Thickness(18, 10, 18, 14) };
        root.Children.Add(stack);

        // Header: "KAI // GOAL" and the close cross.
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(_look.Header
            ? new TextBlock { Text = _look.HeaderText("goal"), FontFamily = _look.Tag, FontSize = 12, Foreground = _look.Brush(_look.Accent) }
            : new TextBlock { Text = "Kai's plan", FontSize = 12.5, Foreground = _look.Brush(_look.Muted) });
        var close = new TextBlock { Text = "✕", FontSize = 13, Foreground = _look.Brush(_look.Muted), Cursor = Cursors.Hand, ToolTip = "Close (stops the plan if it's running)" };
        close.MouseLeftButtonUp += (_, _) => { if (!_finished) _onStop(); Close(); };
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        stack.Children.Add(header);
        if (_look.Header)
            stack.Children.Add(new Rectangle { Height = 1, Fill = _look.Brush(_look.Accent, 0x60), Margin = new Thickness(0, 0, 0, 8) });

        // Title, underlined in the accent.
        stack.Children.Add(new TextBlock { Text = plan.Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = _look.Brush(_look.Text), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new Rectangle { Height = 3, Width = 64, Fill = _look.Brush(_look.Accent), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 10) });

        // The steps.
        var steps = new StackPanel();
        var scroll = new ScrollViewer { Content = steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(160, area.Height - 190) };
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var glyph = new TextBlock { Text = "○", FontSize = 15, Foreground = _look.Brush(_look.Muted), VerticalAlignment = VerticalAlignment.Top };
            row.Children.Add(glyph);
            var body = new StackPanel();
            var label = new TextBlock { Text = $"{i + 1}. {step.Label}", FontSize = 13.5, Foreground = _look.Brush(_look.Text), TextWrapping = TextWrapping.Wrap };
            body.Children.Add(label);
            if (step.StillAsks)
                body.Children.Add(new TextBlock
                {
                    Text = "asks you first · " + (step.Risk == Risk.Destructive ? "can't be undone" : "leaves this PC"),
                    FontSize = 11.5, Foreground = _look.Brush(_look.Accent), Margin = new Thickness(0, 1, 0, 0),
                });
            var detail = new TextBlock { FontSize = 11.5, Foreground = _look.Brush(_look.Muted), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 1, 0, 0) };
            body.Children.Add(detail);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            steps.Children.Add(row);
            _rows.Add((glyph, label, detail));
        }
        stack.Children.Add(scroll);

        // Footer: where it stands, and Stop.
        var foot = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footer.Text = $"{plan.Steps.Count} step{(plan.Steps.Count == 1 ? "" : "s")}. Waiting for your OK: say yes or no, or use Kai's buttons.";
        _footer.FontSize = 12;
        _footer.Foreground = _look.Brush(_look.Muted);
        _footer.TextWrapping = TextWrapping.Wrap;
        _footer.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(_footer);
        _stop = new Border
        {
            CornerRadius = new CornerRadius(_look.Chamfer ? 0 : 8), Cursor = Cursors.Hand, Padding = new Thickness(14, 5, 14, 6), Margin = new Thickness(12, 0, 0, 0),
            Background = _look.Brush(_look.Surface), BorderBrush = _look.Brush(_look.Accent, 0x90), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "Stop", FontSize = 12.5, Foreground = _look.Brush(_look.Text) },
            VerticalAlignment = VerticalAlignment.Center,
        };
        _stop.MouseLeftButtonUp += (_, _) => { if (!_finished) _onStop(); };
        Grid.SetColumn(_stop, 1);
        foot.Children.Add(_stop);
        stack.Children.Add(foot);

        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var ex = Interop.NativeMethods.GetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= Interop.NativeMethods.WS_EX_TOOLWINDOW | Interop.NativeMethods.WS_EX_NOACTIVATE;
            Interop.NativeMethods.SetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        };
        SizeChanged += (_, _) => Anchor();
        Opacity = 0;
        Loaded += (_, _) =>
        {
            Anchor();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        };
    }

    /// <summary>Grows upwards from the bottom of the area for the bottom placements, downwards otherwise.</summary>
    void Anchor() => Top = _anchorBottom ? _area.Bottom - ActualHeight : _area.Top;

    public void SetStep(int index, GoalStatus status, string detail)
    {
        if (index < 0 || index >= _rows.Count) return;
        var (glyph, label, det) = _rows[index];
        glyph.Text = status switch
        {
            GoalStatus.Running => "◐",
            GoalStatus.Done => "✓",
            GoalStatus.Skipped => "↷",
            GoalStatus.Failed => "✕",
            GoalStatus.Declined => "⊘",
            GoalStatus.NotRun => "–",
            _ => "○",
        };
        glyph.Foreground = status switch
        {
            GoalStatus.Running or GoalStatus.Done => _look.Brush(_look.Accent),
            GoalStatus.Failed => new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x5A)),
            _ => _look.Brush(_look.Muted),
        };
        label.Opacity = status is GoalStatus.NotRun or GoalStatus.Declined or GoalStatus.Skipped ? 0.55 : 1;
        if (detail.Length > 0)
        {
            det.Text = detail;
            det.Visibility = Visibility.Visible;
        }
        if (status == GoalStatus.Running) _footer.Text = $"Step {index + 1} of {_count}…";
    }

    public void Finish(string summary)
    {
        _finished = true;
        _footer.Text = summary;
        _footer.Foreground = _look.Brush(_look.Text);
        _stop.Visibility = Visibility.Collapsed;
    }
}

/// <summary>
/// The goal checklist and its questions, for <see cref="AssistantBrain"/>. The brain runs on a
/// worker thread; everything here is passed to the UI thread. Plan and failure questions go
/// through Kai's own approval bubble, so they're answered by click or by a spoken yes or no.
/// </summary>
public sealed class GoalUi(Settings settings, Dispatcher ui, Func<string, string?, Task<bool>> approve, Func<Rect> area, Action stop) : IGoalUi
{
    GoalBoard? _board;

    public void Show(GoalPlan plan) => ui.Invoke(() =>
    {
        _board?.Close();
        _board = new GoalBoard(settings, plan, area(), stop);
        _board.Show();
    });

    public void Update(int index, GoalStatus status, string detail) => ui.BeginInvoke(() => _board?.SetStep(index, status, detail));

    public Task<bool> ApprovePlanAsync(GoalPlan plan)
    {
        var why = plan.StillAskCount switch
        {
            0 => "It's all on this PC and easy to undo. The list is on screen.",
            1 => "One step will still ask you first, because it can't be undone or leaves this PC.",
            var k => $"{k} steps will still ask you first, because they can't be undone or leave this PC.",
        };
        return ui.InvokeAsync(() => approve($"run this {plan.Steps.Count}-step plan", why)).Task.Unwrap();
    }

    public Task<bool> SkipFailedAsync(int index, GoalStep step, string error) =>
        ui.InvokeAsync(() => approve($"skip step {index + 1} and carry on", $"It didn't work: {error}. Say no to stop the plan.")).Task.Unwrap();

    public void Finish(string summary) => ui.BeginInvoke(() => _board?.Finish(summary));
}

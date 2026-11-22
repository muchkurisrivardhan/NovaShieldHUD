using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Beacon.Teach;
using Beacon.UI.Pet;
using Visual = Beacon.Teach.Visual;

namespace Beacon.UI;

/// <summary>
/// The Drawing page (where Kai draws, how it looks, what it draws, the user's own
/// instructions) and the launcher style preview on the Appearance page.
/// </summary>
public partial class SettingsWindow
{
    private string _drawPlace = "bottom-left", _drawStyle = "clean", _drawSpeed = "normal", _drawDetail = "normal";
    private ExplainBoard? _sampleBoard;

    private void InitDrawingPage()
    {
        DrawWidth.ValueChanged += (_, _) => RefreshDrawPreview();
        DrawHeight.ValueChanged += (_, _) => RefreshDrawPreview();
        DrawOpacity.ValueChanged += (_, _) => RefreshDrawPreview();
        DrawTry.Click += (_, _) => TrySampleDrawing();
        ThemePicker.SelectionChanged += (_, _) => { RefreshStylePreview(); RefreshDrawPreview(); };
        AccentColor.TextChanged += (_, _) => { RefreshStylePreview(); RefreshDrawPreview(); };
        Closed += (_, _) => _sampleBoard?.Dismiss();

        foreach (var sample in new[] { "Explain like I'm 12.", "Always add one real-world example.", "Keep every note under 8 words.", "Use metric units." })
        {
            var chip = new Button { Style = (Style)FindResource("AiChip"), MinWidth = 0, Content = "+ " + sample };
            chip.Click += (_, _) =>
            {
                var text = DrawInstructions.Text.TrimEnd();
                if (!text.Contains(sample, StringComparison.OrdinalIgnoreCase))
                    DrawInstructions.Text = (text.Length > 0 ? text + "\n" : "") + sample;
            };
            DrawInstructionSamples.Children.Add(chip);
        }
    }

    private void LoadDrawingPage()
    {
        _drawPlace = _settings.DrawPlace;
        _drawStyle = _settings.DrawStyle;
        _drawSpeed = _settings.DrawSpeed;
        _drawDetail = _settings.DrawDetail;
        DrawWidth.Value = _settings.DrawWidth;
        DrawHeight.Value = _settings.DrawHeight;
        DrawOpacity.Value = _settings.DrawOpacity;
        DrawGrid.IsChecked = _settings.DrawGrid;
        DrawCaptions.IsChecked = _settings.DrawCaptions;
        DrawNarrate.IsChecked = _settings.DrawNarrate;
        DrawInstructions.Text = _settings.DrawInstructions;
        Pick(DrawKind, _settings.DrawKind);
        Pick(DrawCloseAfter, _settings.DrawCloseAfter.ToString());
        TalkTapMode.SelectedItem = TalkTapMode.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.TalkTap) ?? TalkTapMode.Items[0];
        BuildDrawChips();
        RefreshStylePreview();

        static void Pick(ComboBox box, string tag) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];
    }

    private void BuildDrawChips()
    {
        DrawPlaces.Children.Clear();
        DrawStyles.Children.Clear();
        DrawSpeeds.Children.Clear();
        DrawDetails.Children.Clear();
        foreach (var (key, label) in new[]
                 {
                     ("bottom-left", "Bottom left"), ("bottom-right", "Bottom right"), ("left", "Left half"),
                     ("right", "Right half"), ("full", "Full screen"),
                 })
            Chip(DrawPlaces, label, null, key == _drawPlace, () => { _drawPlace = key; BuildDrawChips(); });
        foreach (var (key, label, sub) in new[] { ("clean", "Clean", "Crisp lines"), ("sketch", "Sketch", "Hand-drawn wobble") })
            Chip(DrawStyles, label, sub, key == _drawStyle, () => { _drawStyle = key; BuildDrawChips(); });
        foreach (var (key, label) in new[] { ("slow", "Slow"), ("normal", "Normal"), ("fast", "Fast") })
            Chip(DrawSpeeds, label, null, key == _drawSpeed, () => { _drawSpeed = key; BuildDrawChips(); });
        foreach (var (key, label, sub) in new[]
                 {
                     ("brief", "Brief", "Up to 6 parts"), ("normal", "Normal", "Up to 14 parts"), ("detailed", "Detailed", "10 to 16 parts"),
                 })
            Chip(DrawDetails, label, sub, key == _drawDetail, () => { _drawDetail = key; BuildDrawChips(); });
        var corner = _drawPlace is "bottom-left" or "bottom-right";
        DrawWidth.IsEnabled = DrawHeight.IsEnabled = corner;
        RefreshDrawPreview();
    }

    /// <summary>The settings on this page as they are now, saved or not, for the previews.</summary>
    private Core.Settings DrawDraft() => new()
    {
        OverlayStyle = (ThemePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.OverlayStyle,
        AccentColor = AccentColor.Text.Trim(),
        Theme = _settings.Theme,
        DrawPlace = _drawPlace,
        DrawWidth = DrawWidth.Value,
        DrawHeight = DrawHeight.Value,
        DrawOpacity = DrawOpacity.Value,
        DrawStyle = _drawStyle,
        DrawSpeed = _drawSpeed,
        DrawDetail = _drawDetail,
        DrawGrid = DrawGrid.IsChecked == true,
        DrawCaptions = DrawCaptions.IsChecked == true,
        DrawNarrate = DrawNarrate.IsChecked == true,
        DrawKind = (DrawKind.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto",
        DrawCloseAfter = int.TryParse((DrawCloseAfter.SelectedItem as ComboBoxItem)?.Tag as string, out var s) ? s : 0,
        DrawInstructions = DrawInstructions.Text.Trim(),
    };

    private void RefreshStylePreview()
    {
        if (StylePreview is null || ThemePicker is null) return;
        var draft = DrawDraft();
        StylePreview.Content = LauncherPreview.Build(StyleLook.For(draft));
    }

    /// <summary>A small screen with the panel where it will go, in the style's colours.</summary>
    private void RefreshDrawPreview()
    {
        if (DrawPreview is null || DrawWidthValue is null) return;
        DrawWidthValue.Text = $"{DrawWidth.Value:P0} of the screen";
        DrawHeightValue.Text = $"{DrawHeight.Value:P0} of the screen";
        DrawOpacityValue.Text = $"{DrawOpacity.Value:P0} solid";
        var draft = DrawDraft();
        var look = StyleLook.For(draft);
        const double sw = 320, sh = 180;
        var screen = new Canvas { Width = sw, Height = sh, ClipToBounds = true };
        screen.Children.Add(new Rectangle
        {
            Width = sw, Height = sh, RadiusX = 4, RadiusY = 4,
            Fill = new LinearGradientBrush(Color.FromRgb(0x2A, 0x3B, 0x55), Color.FromRgb(0x18, 0x1E, 0x2A), 60),
        });
        // A pretend app window behind it, and the taskbar.
        var app = new Rectangle { Width = sw * 0.62, Height = sh * 0.6, Fill = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), RadiusX = 3, RadiusY = 3 };
        Canvas.SetLeft(app, sw * 0.3);
        Canvas.SetTop(app, sh * 0.1);
        screen.Children.Add(app);
        var taskbar = new Rectangle { Width = sw, Height = 10, Fill = new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x10, 0x14)) };
        Canvas.SetTop(taskbar, sh - 10);
        screen.Children.Add(taskbar);

        var area = ExplainBoard.Place(draft, new Rect(0, 0, 1920, 1040));
        var k = sw / 1920;
        var panel = look.Plate(look.Page, (byte)(255 * Math.Clamp(draft.DrawOpacity, 0.6, 1)), look.Chamfer ? look.Accent : look.Border, 1, 5);
        panel.Width = area.Width * k;
        panel.Height = area.Height * k;
        Canvas.SetLeft(panel, area.Left * k);
        Canvas.SetTop(panel, area.Top * k);
        screen.Children.Add(panel);
        // A few lines of drawing inside it.
        var cx = area.Left * k + panel.Width / 2;
        var cy = area.Top * k + panel.Height / 2 + 4;
        var r = Math.Min(panel.Width, panel.Height) * 0.28;
        for (var i = 0; i < 5; i++)
        {
            var a = i * Math.PI * 2 / 5 - Math.PI / 2;
            var end = new Point(cx + Math.Cos(a) * r * 1.3, cy + Math.Sin(a) * r);
            screen.Children.Add(new Line { X1 = cx, Y1 = cy, X2 = end.X, Y2 = end.Y, Stroke = look.Brush(look.Accent, 0xB0), StrokeThickness = 1 });
            var dot = new Rectangle { Width = 14, Height = 7, RadiusX = 2, RadiusY = 2, Fill = look.Brush(look.SurfaceAlt), Stroke = look.Brush(look.Accent), StrokeThickness = 0.8 };
            Canvas.SetLeft(dot, end.X - 7);
            Canvas.SetTop(dot, end.Y - 3.5);
            screen.Children.Add(dot);
        }
        var hub = new Ellipse { Width = 16, Height = 16, Fill = look.Brush(look.Accent) };
        Canvas.SetLeft(hub, cx - 8);
        Canvas.SetTop(hub, cy - 8);
        screen.Children.Add(hub);
        var tag = new TextBlock { Text = look.Header ? look.HeaderText("explains") : "Kai explains", FontFamily = look.Header ? look.Tag : look.Font, FontSize = 5.5, Foreground = look.Brush(look.Header ? look.Accent : look.Muted) };
        Canvas.SetLeft(tag, area.Left * k + 6);
        Canvas.SetTop(tag, area.Top * k + 3);
        screen.Children.Add(tag);
        DrawPreview.Content = new Viewbox { Child = screen };
    }

    /// <summary>A sample drawing with the page's settings, stepping by itself (no voice), to see it for real.</summary>
    private void TrySampleDrawing()
    {
        _sampleBoard?.Dismiss();
        var visual = Visual.Parse("""
            {"kind":"mindmap","title":"The water cycle","nodes":[
             {"id":"w","label":"Water cycle"},
             {"id":"e","label":"Evaporation","parent":"w","note":"The sun warms water into vapour."},
             {"id":"c","label":"Condensation","parent":"w","note":"Vapour cools into clouds."},
             {"id":"p","label":"Precipitation","parent":"w","note":"Rain and snow fall."},
             {"id":"r","label":"Collection","parent":"w","note":"Rivers carry it back to the sea."},
             {"id":"t","label":"Transpiration","parent":"e","note":"Plants breathe out water."}],
             "links":[],
             "steps":[{"say":"Here's the water cycle: water going round and round.","focus":["w"]},
              {"say":"The sun warms the sea, and water rises as vapour.","focus":["e","t"]},
              {"say":"High up it cools and gathers into clouds.","focus":["c"]},
              {"say":"Then it falls as rain or snow, and flows back to the sea.","focus":["p","r"]}]}
            """)!;
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var m = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice ?? Matrix.Identity;
        Rect Dip(System.Drawing.Rectangle b) => new(m.Transform(new Point(b.Left, b.Top)), m.Transform(new Point(b.Right, b.Bottom)));
        var draft = DrawDraft();
        var board = _sampleBoard = new ExplainBoard(draft, visual, Dip(screen.Bounds), null, Dip(screen.WorkingArea));
        board.Show();
        var step = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(draft.DrawSpeed == "fast" ? 1.8 : draft.DrawSpeed == "slow" ? 3.6 : 2.6) };
        void Next()
        {
            if (step < visual.Steps.Count) board.Step(step++);
            else { board.Finish(visual.Title); timer.Stop(); }
        }
        timer.Tick += (_, _) => Next();
        board.Skipped += Next;
        board.Dismissed += timer.Stop;
        board.Loaded += (_, _) => { Next(); timer.Start(); };
    }

    private void SaveDrawingPage()
    {
        var d = DrawDraft();
        _settings.DrawPlace = d.DrawPlace;
        _settings.DrawWidth = d.DrawWidth;
        _settings.DrawHeight = d.DrawHeight;
        _settings.DrawOpacity = d.DrawOpacity;
        _settings.DrawStyle = d.DrawStyle;
        _settings.DrawSpeed = d.DrawSpeed;
        _settings.DrawDetail = d.DrawDetail;
        _settings.DrawGrid = d.DrawGrid;
        _settings.DrawCaptions = d.DrawCaptions;
        _settings.DrawNarrate = d.DrawNarrate;
        _settings.DrawKind = d.DrawKind;
        _settings.DrawCloseAfter = d.DrawCloseAfter;
        _settings.DrawInstructions = d.DrawInstructions;
        _settings.TalkTap = (TalkTapMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "type";
    }
}

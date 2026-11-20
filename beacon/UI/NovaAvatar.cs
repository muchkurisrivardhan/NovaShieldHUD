using System.Windows;
using System.Windows.Media;
using Beacon.Core;

namespace Beacon.UI;

/// <summary>
/// Nova's face, drawn live: a glowing orb, or a globe of LED dots that turns slowly. One
/// control everywhere Nova appears (its window, the Brain page, the Avatar page), so it looks
/// the same in each.
///
/// It shows what Nova is doing (breathing when idle, following the mic while listening, a
/// scanning band while thinking, ripples while speaking) and, with moods on, the tone of the
/// conversation: its colour leans warm and bright when things go well, cool and steady while
/// it works something out, and amber when something went wrong. The base colour is always the
/// Beacon style's accent.
/// </summary>
public sealed class NovaAvatar : FrameworkElement
{
    public enum Activity { Idle, Listening, Thinking, Speaking }
    public enum Feeling { Calm, Happy, Focused, Concerned }

    /// <summary>"character" (the 24-pose art, when it's installed), "globe" or "orb".</summary>
    public string Look { get; set; } = "orb";

    /// <summary>A pose shown for a moment (Greeting, Searching, Alert...), over the automatic one.</summary>
    public string? Pose { get; private set; }
    DateTime _poseUntil;

    /// <summary>Shows a pose for a while, then goes back to following state and mood.</summary>
    public void Flash(string pose, double seconds = 3)
    {
        Pose = pose;
        _poseUntil = DateTime.Now.AddSeconds(seconds);
    }

    /// <summary>The pose for what Nova is doing now and the mood it's in.</summary>
    public string CurrentPose()
    {
        if (Pose is not null && DateTime.Now < _poseUntil) return Pose;
        Pose = null;
        return State switch
        {
            Activity.Listening => "Listening",
            Activity.Thinking => "Thinking",
            _ => (Moods ? Mood : Feeling.Calm) switch
            {
                Feeling.Happy => "Happy",
                Feeling.Concerned => "Sad",
                Feeling.Focused => State == Activity.Speaking ? "Idea" : "Doubtful",
                _ => "Idle",
            },
        };
    }
    public Color Accent { get; set; } = Color.FromRgb(0xF0, 0xA0, 0x30);
    public Activity State { get; set; } = Activity.Idle;
    public Feeling Mood { get; set; } = Feeling.Calm;
    public bool Moods { get; set; } = true;

    /// <summary>Mic level, 0 to 1, while listening.</summary>
    public double Level { get; set; }

    readonly DateTime _start = DateTime.Now;
    Color _shown;
    bool _hasShown;
    TimeSpan _lastFrame;

    public NovaAvatar()
    {
        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        IsHitTestVisible = false;
    }

    /// <summary>Takes the accent and look from Beacon's settings.</summary>
    public void FollowSettings(Settings settings)
    {
        try { Accent = (Color)ColorConverter.ConvertFromString(Suite.PaletteFor(settings).Accent); }
        catch (FormatException) { }
        Look = settings.AvatarLook;
        Moods = settings.AvatarMoods;
    }

    /// <summary>The colour for a mood, from the accent.</summary>
    public static Color MoodColor(Color accent, Feeling mood) => mood switch
    {
        Feeling.Happy => Mix(Mix(accent, Color.FromRgb(0x6B, 0xE3, 0x9A), 0.45), Colors.White, 0.1),
        Feeling.Focused => Mix(accent, Color.FromRgb(0x6F, 0xB7, 0xFF), 0.4),
        Feeling.Concerned => Mix(accent, Color.FromRgb(0xFF, 0x8A, 0x4C), 0.55),
        _ => accent,
    };

    /// <summary>A rough reading of a line's tone, from its words. Good enough for a colour.</summary>
    public static Feeling Detect(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        bool Any(params string[] words) => words.Any(w => t.Contains(w, StringComparison.Ordinal));
        if (Any("sorry", "couldn't", "can't", "failed", "error", "wrong", "problem", "didn't work", "not set up", "urgent", "refused"))
            return Feeling.Concerned;
        if (Any("thanks", "thank you", "great", "awesome", "nice", "perfect", "love", "done", "opened", "you're welcome", "good morning", "good evening", "haha", "!"))
            return Feeling.Happy;
        if (Any("how ", "why ", "explain", "summar", "plan", "think", "research", "compare", "let me check", "checking", "?"))
            return Feeling.Focused;
        return Feeling.Calm;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        // About 30 frames a second is plenty for a glow, and half the work of 60.
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime - _lastFrame < TimeSpan.FromMilliseconds(32)) return;
            _lastFrame = r.RenderingTime;
        }
        if (IsVisible) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        var t = (DateTime.Now - _start).TotalSeconds;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 * 0.8;

        // Ease toward the mood's colour, so a change of mood glides rather than snaps.
        var target = Moods ? MoodColor(Accent, Mood) : Accent;
        _shown = _hasShown ? Mix(_shown, target, 0.08) : target;
        _hasShown = true;

        if (Look == "character" && AvatarArt.Get(CurrentPose(), _shown) is { } art) DrawCharacter(dc, art, size, t);
        else if (Look is "globe" or "character") DrawGlobe(dc, c, radius, t);   // no art yet: the globe
        else DrawOrb(dc, c, radius, t);
    }

    /// <summary>The character, floating gently over a ring of light in the accent colour.</summary>
    void DrawCharacter(DrawingContext dc, ImageSource art, double size, double t)
    {
        var bob = size * 0.025 * Math.Sin(t * (State == Activity.Speaking ? 7 : 2));
        var cx = ActualWidth / 2;
        var baseY = ActualHeight / 2 + size * 0.38;
        var ring = State == Activity.Listening ? 1 + Math.Min(Level * 2, 0.2) : 1;
        for (var i = 0; i < 3; i++)
        {
            var w = size * (0.32 + i * 0.07) * ring;
            dc.DrawEllipse(null, new Pen(Brush(WithAlpha(_shown, (byte)(150 - i * 40))), Math.Max(1, size * 0.012)),
                new Point(cx, baseY - i * size * 0.035), w, w * 0.22);
        }

        // Fit the picture in the square, faded at the edges so a sheet's background melts away.
        var aspect = art.Width / art.Height;
        var h = size * 0.98;
        var w2 = h * aspect;
        if (w2 > size) { w2 = size; h = w2 / aspect; }
        var rect = new Rect(cx - w2 / 2, ActualHeight / 2 - h / 2 + bob, w2, h);
        var fade = new RadialGradientBrush(Colors.Black, Colors.Transparent) { RadiusX = 0.5, RadiusY = 0.5 };
        fade.GradientStops.Clear();
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.72));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        fade.Freeze();
        dc.PushOpacityMask(fade);
        dc.DrawImage(art, rect);
        dc.Pop();
    }

    void DrawOrb(DrawingContext dc, Point c, double radius, double t)
    {
        var scale = State switch
        {
            Activity.Speaking => 1 + 0.07 * Math.Abs(Math.Sin(t * 9)),
            Activity.Listening => 1 + Math.Min(Level * 2.5, 0.25),
            Activity.Thinking => 1,
            _ => 1 + 0.035 * Math.Sin(t * 2.2),
        };
        var r = radius * 0.72 * scale;

        // Soft glow: a few wide, faint rings.
        for (var i = 3; i >= 1; i--)
            dc.DrawEllipse(Brush(WithAlpha(_shown, (byte)(18 * i))), null, c, r + i * radius * 0.07, r + i * radius * 0.07);

        var body = new RadialGradientBrush(Mix(_shown, Colors.White, 0.5), _shown) { GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.45, 0.4) };
        body.Freeze();
        dc.DrawEllipse(body, null, c, r, r);

        var halo = new Pen(Brush(WithAlpha(_shown, State == Activity.Listening ? (byte)0xE0 : (byte)0x60)), Math.Max(1, radius * 0.04));
        dc.DrawEllipse(null, halo, c, radius, radius);

        if (State == Activity.Thinking)
        {
            var a0 = t * 5;
            var arc = new StreamGeometry();
            using (var g = arc.Open())
            {
                g.BeginFigure(new Point(c.X + radius * Math.Cos(a0), c.Y + radius * Math.Sin(a0)), false, false);
                g.ArcTo(new Point(c.X + radius * Math.Cos(a0 + 1.6), c.Y + radius * Math.Sin(a0 + 1.6)),
                    new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            dc.DrawGeometry(null, new Pen(Brush(_shown), Math.Max(1.5, radius * 0.08)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
        }
    }

    void DrawGlobe(DrawingContext dc, Point c, double radius, double t)
    {
        var spacing = radius / 6.5;
        var spin = t * (State == Activity.Thinking ? 1.4 : 0.35);
        var dot = spacing * 0.3;

        dc.DrawEllipse(Brush(WithAlpha(_shown, 0x14)), null, c, radius * 1.08, radius * 1.08);

        for (var lat = -78.0; lat <= 78.0; lat += 180.0 / 13)
        {
            var phi = lat * Math.PI / 180;
            var ring = radius * Math.Cos(phi);
            var y = c.Y - radius * Math.Sin(phi);
            var count = Math.Max(1, (int)Math.Round(2 * Math.PI * ring / spacing));
            for (var i = 0; i < count; i++)
            {
                var theta = 2 * Math.PI * i / count + spin;
                var depth = Math.Cos(theta);             // 1 facing us, -1 at the back
                if (depth < -0.2) continue;              // the back of the globe stays dark
                var x = c.X + ring * Math.Sin(theta);
                var bright = 0.25 + 0.75 * (depth + 0.2) / 1.2;

                var dx = x - c.X; var dy = y - c.Y;
                var dist = Math.Sqrt(dx * dx + dy * dy) / radius;
                bright *= State switch
                {
                    Activity.Speaking => 0.55 + 0.45 * Math.Sin(dist * 9 - t * 11),
                    Activity.Listening => 0.35 + Math.Min(Level * 4, 0.65) + 0.1 * Math.Sin(t * 6 + dist * 4),
                    Activity.Thinking => 0.35 + 0.65 * Math.Exp(-Math.Pow((y - c.Y) / radius - Math.Sin(t * 2.4), 2) * 8),
                    _ => 0.7 + 0.3 * Math.Sin(t * 1.6 + lat * 0.08 + i),
                };
                bright = Math.Clamp(bright, 0.08, 1);
                var r = dot * (0.65 + 0.35 * (depth + 1) / 2);
                dc.DrawEllipse(Brush(WithAlpha(Mix(_shown, Colors.White, bright * 0.25), (byte)(255 * bright))), null, new Point(x, y), r, r);
            }
        }
    }

    // A small cache: the globe draws a few hundred dots a frame in a handful of shades.
    readonly Dictionary<uint, SolidColorBrush> _brushes = [];

    SolidColorBrush Brush(Color c)
    {
        var key = (uint)(c.A & 0xF0) << 24 | (uint)(c.R & 0xF8) << 16 | (uint)(c.G & 0xF8) << 8 | (uint)(c.B & 0xF8);
        if (_brushes.TryGetValue(key, out var b)) return b;
        if (_brushes.Count > 512) _brushes.Clear();
        b = new SolidColorBrush(Color.FromArgb((byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key));
        b.Freeze();
        return _brushes[key] = b;
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

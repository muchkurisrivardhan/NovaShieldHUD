using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using Beacon.Core;

namespace Beacon.UI.Pet;

/// <summary>
/// Nova in 3D: a small robot pet drawn live with WPF's own 3D (no game engine, no model
/// files). A gunmetal head with a dark visor and glowing eyes, an antenna, ears, a hovering
/// body with a glowing core, floating hands, and two rings of light underneath, all glowing
/// in the Beacon style's accent.
///
/// It breathes and blinks when idle, turns its head to follow the cursor, perks up and pulses
/// with the mic while listening, spins its rings while thinking, and bounces and lights its
/// mouth while speaking. Stroke its head with the mouse and it squints happily, wiggles and
/// sends up hearts. The same control is the pet beside the cursor and the previews in Settings.
/// </summary>
public sealed class NovaPet3D : Grid
{
    public enum Activity { Idle, Listening, Thinking, Speaking }
    public enum Feeling { Calm, Happy, Focused, Concerned }

    public Activity State { get; set; } = Activity.Idle;
    public Feeling Mood { get; set; } = Feeling.Calm;
    public bool Moods { get; set; } = true;

    /// <summary>Mic level, 0 to 1, while listening.</summary>
    public double Level { get; set; }

    /// <summary>Where to look, in this control's coordinates (the cursor), or null to look ahead.</summary>
    public Point? LookAt { get; set; }

    /// <summary>Raised when it's clicked (the pet window starts listening).</summary>
    public event Action? Tapped;

    /// <summary>Raised now and then while it's being petted.</summary>
    public event Action? Petted;

    public Color Accent
    {
        get => _accent;
        set { _accent = value; _target = value; }
    }

    // ---- the model's moving parts
    readonly Viewport3D _view = new() { ClipToBounds = false, IsHitTestVisible = false };
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly TranslateTransform3D _bob = new();
    readonly ScaleTransform3D _pop = new(1, 1, 1);
    readonly AxisAngleRotation3D _yaw = new(new Vector3D(0, 1, 0), 0), _pitch = new(new Vector3D(1, 0, 0), 0), _tilt = new(new Vector3D(0, 0, 1), 0);
    readonly AxisAngleRotation3D _bodyWiggle = new(new Vector3D(0, 0, 1), 0);
    readonly ScaleTransform3D _leftEye = new(1, 1, 1, -0.27, 0.6, 0.86), _rightEye = new(1, 1, 1, 0.27, 0.6, 0.86);
    readonly TranslateTransform3D _eyeShift = new();
    readonly ScaleTransform3D _mouth = new(1, 1, 1, 0, 0.33, 0.86);
    readonly AxisAngleRotation3D _ring1 = new(new Vector3D(0, 1, 0), 0), _ring2 = new(new Vector3D(0, 1, 0), 0);
    readonly ScaleTransform3D _ringPulse = new(1, 1, 1, 0, -1.15, 0);
    readonly TranslateTransform3D _leftHand = new(), _rightHand = new();
    readonly List<SolidColorBrush> _glows = [];
    readonly DirectionalLight _rim = new(Colors.Orange, new Vector3D(1.2, -0.3, 1));

    Color _accent = Color.FromRgb(0xF0, 0xA0, 0x30), _shown, _target;
    readonly DateTime _start = DateTime.Now;
    double _nextBlink = 2, _happy, _strokes, _appear = 1;
    Point? _lastMouse;
    TimeSpan _lastFrame;

    public NovaPet3D()
    {
        _shown = _accent;
        _view.Camera = new PerspectiveCamera(new Point3D(0, 0.15, 6.4), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 32);
        _view.Children.Add(new ModelVisual3D { Content = BuildModel() });
        Children.Add(_view);
        Children.Add(_overlay);
        Background = Brushes.Transparent;   // the whole square takes the mouse: petting and clicks

        Loaded += (_, _) => CompositionTarget.Rendering += OnFrame;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnFrame;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => _lastMouse = null;
        MouseLeftButtonUp += (_, _) => Tapped?.Invoke();
    }

    /// <summary>Takes the accent and preferences from Beacon's settings.</summary>
    public void FollowSettings(Settings settings)
    {
        try { Accent = (Color)ColorConverter.ConvertFromString(Suite.PaletteFor(settings).Accent); }
        catch (FormatException) { }
        Moods = settings.AvatarMoods;
    }

    /// <summary>Pops in from nothing (when summoned).</summary>
    public void Appear() => _appear = 0;

    Model3DGroup BuildModel()
    {
        var gunmetal = MeshKit.Metal(Color.FromRgb(0x3A, 0x3D, 0x44));
        var darkMetal = MeshKit.Metal(Color.FromRgb(0x24, 0x26, 0x2B), 20);
        var visor = MeshKit.Metal(Color.FromRgb(0x08, 0x0A, 0x0E), 90);
        Material Glow()
        {
            var (m, brush) = MeshKit.Glow(_accent);
            _glows.Add(brush);
            return m;
        }

        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(0x50, 0x52, 0x58)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0xE8, 0xE8, 0xEE), new Vector3D(-0.6, -0.7, -1)));
        root.Children.Add(_rim);

        var pet = new Model3DGroup();
        var place = new Transform3DGroup();
        place.Children.Add(_pop);
        place.Children.Add(_bob);
        pet.Transform = place;

        // Head, turned toward the cursor.
        var head = new Model3DGroup();
        var turn = new Transform3DGroup();
        turn.Children.Add(new RotateTransform3D(_tilt, 0, 0.5, 0));
        turn.Children.Add(new RotateTransform3D(_pitch, 0, 0.5, 0));
        turn.Children.Add(new RotateTransform3D(_yaw, 0, 0.5, 0));
        head.Transform = turn;
        head.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0, 0.55, 0), 0.9, 0.8, 0.85, 56, 36), gunmetal));
        head.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0, 0.55, 0.3), 0.72, 0.52, 0.6, 56, 36), visor));
        var eyes = new Model3DGroup { Transform = _eyeShift };
        eyes.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(-0.27, 0.6, 0.86), 0.11, 0.15, 0.05), Glow()) { Transform = _leftEye });
        eyes.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0.27, 0.6, 0.86), 0.11, 0.15, 0.05), Glow()) { Transform = _rightEye });
        head.Children.Add(eyes);
        head.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0, 0.33, 0.86), 0.12, 0.025, 0.03), Glow()) { Transform = _mouth });
        head.Children.Add(new GeometryModel3D(MeshKit.Cylinder(new Point3D(0, 1.28, 0), 0.035, 0.38), darkMetal));
        head.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0, 1.72, 0), 0.09, 0.09, 0.09), Glow()));
        foreach (var side in new[] { -1, 1 })
        {
            head.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(side * 0.9, 0.58, 0), 0.13, 0.24, 0.24), darkMetal));
            head.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(side * 0.99, 0.58, 0), 0.05, 0.13, 0.13), Glow()));
        }
        pet.Children.Add(head);

        // Body with its glowing core, wiggling when happy.
        var body = new Model3DGroup { Transform = new RotateTransform3D(_bodyWiggle, 0, -0.45, 0) };
        body.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0, -0.45, 0), 0.56, 0.5, 0.5), gunmetal));
        body.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0, -0.4, 0.46), 0.12, 0.12, 0.06), Glow()));
        pet.Children.Add(body);

        // Floating hands.
        pet.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(-0.78, -0.42, 0.1), 0.17, 0.17, 0.17), darkMetal) { Transform = _leftHand });
        pet.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0.78, -0.42, 0.1), 0.17, 0.17, 0.17), darkMetal) { Transform = _rightHand });

        // Two rings of light it hovers on.
        var rings = new Model3DGroup { Transform = _ringPulse };
        rings.Children.Add(new GeometryModel3D(MeshKit.Torus(new Point3D(0, -1.1, 0), 0.62, 0.028), Glow()) { Transform = new RotateTransform3D(_ring1, 0, -1.1, 0) });
        rings.Children.Add(new GeometryModel3D(MeshKit.Torus(new Point3D(0, -1.3, 0), 0.85, 0.022), Glow()) { Transform = new RotateTransform3D(_ring2, 0, -1.3, 0) });
        pet.Children.Add(rings);

        root.Children.Add(pet);
        return root;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        // Stroking the head (the top half): each bit of movement there is petting.
        if (_lastMouse is { } last && p.Y < ActualHeight * 0.55)
        {
            _strokes += (p - last).Length;
            if (_strokes > 140)
            {
                _strokes = 0;
                _happy = Math.Min(1, _happy + 0.5);
                AddHeart();
                Petted?.Invoke();
            }
        }
        _lastMouse = p;
    }

    void AddHeart()
    {
        var heart = new TextBlock
        {
            Text = "♥", FontSize = 18 + Random.Shared.Next(10),
            Foreground = new SolidColorBrush(Mix(_shown, Color.FromRgb(0xFF, 0x6F, 0xA5), 0.6)),
        };
        Canvas.SetLeft(heart, ActualWidth * (0.3 + Random.Shared.NextDouble() * 0.4));
        Canvas.SetTop(heart, ActualHeight * 0.18);
        heart.Tag = 0.0;
        _overlay.Children.Add(heart);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime - _lastFrame < TimeSpan.FromMilliseconds(30)) return;
            _lastFrame = r.RenderingTime;
        }
        if (!IsVisible) return;
        var t = (DateTime.Now - _start).TotalSeconds;
        const double dt = 0.033;

        // Pop in.
        _appear = Math.Min(1, _appear + dt * 3.5);
        var pop = _appear < 1 ? 1 - Math.Pow(1 - _appear, 3) * Math.Cos(_appear * 9) : 1;
        _pop.ScaleX = _pop.ScaleY = _pop.ScaleZ = Math.Max(0.01, pop);

        // Colour: the accent, leaning toward the mood's, eased.
        _target = Moods ? MoodColor(_accent, _happy > 0.3 ? Feeling.Happy : Mood) : _accent;
        _shown = Mix(_shown, _target, 0.08);
        var glow = State == Activity.Listening ? Mix(_shown, Colors.White, Math.Min(Level * 3, 0.5)) : _shown;
        foreach (var b in _glows) b.Color = glow;
        _rim.Color = Mix(_shown, Colors.Black, 0.35);

        // Hover and bob; a little bounce while speaking.
        var bob = 0.08 * Math.Sin(t * 2.1) + (State == Activity.Speaking ? 0.06 * Math.Abs(Math.Sin(t * 8)) : 0);
        _bob.OffsetY = bob + _happy * 0.06 * Math.Abs(Math.Sin(t * 10));

        // Look at the cursor, within a comfortable range.
        double yaw = 0, pitch = 0;
        if (LookAt is { } look && ActualWidth > 0)
        {
            var dx = (look.X - ActualWidth / 2) / ActualWidth;
            var dy = (look.Y - ActualHeight * 0.35) / ActualHeight;
            yaw = Math.Clamp(dx * 55, -35, 35);
            pitch = Math.Clamp(dy * 45, -20, 25);
        }
        if (State == Activity.Thinking) { yaw = 18 * Math.Sin(t * 0.8); pitch = -14; }
        _yaw.Angle += (yaw - _yaw.Angle) * 0.15;
        _pitch.Angle += (pitch - _pitch.Angle) * 0.15;
        _tilt.Angle = State == Activity.Listening ? 8 * Math.Sin(t * 1.3) : _happy * 10 * Math.Sin(t * 6);
        _eyeShift.OffsetX = _yaw.Angle / 35 * 0.05;
        _eyeShift.OffsetY = -_pitch.Angle / 25 * 0.03;

        // Eyes: blink now and then; squint happily when petted; wide while listening.
        var eyeY = 1.0;
        if (t > _nextBlink) { eyeY = 0.1; if (t > _nextBlink + 0.13) _nextBlink = t + 2.5 + Random.Shared.NextDouble() * 3.5; }
        if (_happy > 0.3) eyeY = 0.35;
        else if (State == Activity.Listening) eyeY = 1.15 + Math.Min(Level * 2, 0.3);
        _leftEye.ScaleY = _rightEye.ScaleY = eyeY;

        // Mouth: talking.
        _mouth.ScaleX = State == Activity.Speaking ? 1 + 0.6 * Math.Abs(Math.Sin(t * 13)) : _happy > 0.3 ? 1.5 : 1;
        _mouth.ScaleY = State == Activity.Speaking ? 1 + 2.5 * Math.Abs(Math.Sin(t * 11)) : 1;

        // Rings: slow turn, fast while thinking, pulsing with the voice while listening.
        var spin = State == Activity.Thinking ? 9 : 1.5;
        _ring1.Angle = (_ring1.Angle + spin * 3) % 360;
        _ring2.Angle = (_ring2.Angle - spin * 2) % 360;
        var ring = State == Activity.Listening ? 1 + Math.Min(Level * 2.5, 0.3) : 1 + 0.03 * Math.Sin(t * 3);
        _ringPulse.ScaleX = _ringPulse.ScaleZ = ring;

        // Hands: float, wave while speaking, clap-wiggle when happy.
        var wave = State == Activity.Speaking ? 0.12 * Math.Sin(t * 7) : 0.04 * Math.Sin(t * 2.4 + 1);
        _leftHand.OffsetY = wave;
        _rightHand.OffsetY = -wave + _happy * 0.15 * Math.Sin(t * 12);
        _bodyWiggle.Angle = _happy * 7 * Math.Sin(t * 12);

        _happy = Math.Max(0, _happy - dt * 0.25);

        // Hearts float up and fade.
        for (var i = _overlay.Children.Count - 1; i >= 0; i--)
        {
            var h = (FrameworkElement)_overlay.Children[i];
            var age = (double)h.Tag + dt;
            h.Tag = age;
            Canvas.SetTop(h, Canvas.GetTop(h) - 1.4);
            h.Opacity = Math.Max(0, 1 - age / 1.4);
            if (age > 1.4) _overlay.Children.RemoveAt(i);
        }
    }

    /// <summary>The colour for a mood, from the accent.</summary>
    public static Color MoodColor(Color accent, Feeling mood) => mood switch
    {
        Feeling.Happy => Mix(Mix(accent, Color.FromRgb(0x6B, 0xE3, 0x9A), 0.4), Colors.White, 0.1),
        Feeling.Focused => Mix(accent, Color.FromRgb(0x6F, 0xB7, 0xFF), 0.4),
        Feeling.Concerned => Mix(accent, Color.FromRgb(0xFF, 0x8A, 0x4C), 0.55),
        _ => accent,
    };

    /// <summary>A rough reading of a line's tone, from its words. Good enough for a colour.</summary>
    public static Feeling Detect(string text)
    {
        var t = " " + text.ToLowerInvariant() + " ";
        bool Any(params string[] words) => words.Any(w => t.Contains(w, StringComparison.Ordinal));
        if (Any("sorry", "couldn't", "can't", "failed", "error", "wrong", "problem", "didn't work", "not set up", "refused"))
            return Feeling.Concerned;
        if (Any("thanks", "thank you", "great", "awesome", "nice", "perfect", "love", "done", "opened", "you're welcome", "haha", "!"))
            return Feeling.Happy;
        if (Any("how ", "why ", "explain", "summar", "plan", "think", "compare", "let me check", "checking", "?"))
            return Feeling.Focused;
        return Feeling.Calm;
    }

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}

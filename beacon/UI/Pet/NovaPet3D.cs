using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Beacon.Core;
using Beacon.UI.Pet.Kai;

namespace Beacon.UI.Pet;

/// <summary>
/// Nova in 3D, as Kai: a chibi in a glowing cyber suit standing on a holo base, drawn live with
/// WPF's own 3D from the model in the app's resources. Every blue in the model (glow, suit,
/// hair, eyes) takes the Beacon style's accent.
///
/// Kai is animated in code through the model's joints: breathes and bobs, blinks, turns
/// to look at the cursor, waves hello when called, lifts a hand to the headphone and opens
/// wide eyes while listening (sound waves pulse with the mic), puts a hand to the chin under a
/// "?" while thinking, gestures and moves the mouth while speaking, walks when following the
/// cursor, and cheers with ^ ^ eyes when petted. Stroke the head to pet. The same control is
/// the pet beside the cursor and the previews in Settings.
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

    /// <summary>Sideways movement, -1 (left) to 1 (right), while following the cursor: Kai walks.</summary>
    public double Walk { get; set; }

    /// <summary>Raised when it's clicked (the pet window starts listening).</summary>
    public event Action? Tapped;

    /// <summary>Raised now and then while it's being petted.</summary>
    public event Action? Petted;

    public Color Accent
    {
        get => _accent;
        set => _accent = value;
    }

    readonly Viewport3D _view = new() { ClipToBounds = false, IsHitTestVisible = false };
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly DirectionalLight _rim = new(Colors.Orange, new Vector3D(1.2, -0.3, 1));
    readonly ScaleTransform3D _pop = new(1, 1, 1);

    Color _accent = Color.FromRgb(0xF0, 0xA0, 0x30), _shown;
    Color? _tintedFor;
    readonly DateTime _start = DateTime.Now;
    double _nextBlink = 2, _happy, _strokes, _appear = 1, _wave;
    Point? _lastMouse;
    TimeSpan _lastFrame;

    // Kai's parts (null when the model couldn't be loaded and the fallback orb is shown).
    readonly KaiAsset? _kai = KaiAsset.Shared;
    readonly Dictionary<string, Joint> _joints = [];
    readonly List<(GeometryModel3D Model, GlbDocument.Material Source)> _parts = [];
    readonly List<GlowPaint> _glows = [];
    readonly Dictionary<(KaiFace.Eyes, KaiFace.Mouth, bool), BitmapSource> _faces = [];
    ImageBrush? _faceBrush;
    KaiAsset.Picture? _faceBase;
    (byte R, byte G, byte B) _ink = KaiFace.Ink;
    (KaiFace.Eyes, KaiFace.Mouth, bool) _faceShown = (KaiFace.Eyes.Open, KaiFace.Mouth.Smile, false);
    double _strandAngle, _strandSpeed, _lastYaw, _lastBodyY;

    // ---- the fallback orb
    readonly List<SolidColorBrush> _orbGlows = [];
    readonly TranslateTransform3D _orbBob = new();

    public NovaPet3D()
    {
        _shown = _accent;
        // Looking slightly down at Kai, who stands about two units tall on the base.
        _view.Camera = new PerspectiveCamera(new Point3D(0, 1.45, 4.6), new Vector3D(0, 0.98 - 1.45, -4.6), new Vector3D(0, 1, 0), 30);
        _view.Children.Add(new ModelVisual3D { Content = _kai is null ? BuildOrb() : BuildKai(_kai) });
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

    /// <summary>Pops in from nothing and waves hello (when summoned).</summary>
    public void Appear()
    {
        _appear = 0;
        _wave = 1.8;
    }

    // ------------------------------------------------------------------ building Kai

    /// <summary>A joint: its own turn (applied first), scale and offset, then its place in the parent, then a spin.</summary>
    sealed class Joint
    {
        public readonly Model3DGroup Group = new();
        public readonly AxisAngleRotation3D X = new(new Vector3D(1, 0, 0), 0), Y = new(new Vector3D(0, 1, 0), 0), Z = new(new Vector3D(0, 0, 1), 0);
        public readonly AxisAngleRotation3D Spin = new(new Vector3D(0, 1, 0), 0);
        public readonly ScaleTransform3D Scale = new(1, 1, 1);
        public readonly TranslateTransform3D Move = new();

        public Joint(Matrix3D rest)
        {
            var t = new Transform3DGroup();
            t.Children.Add(new RotateTransform3D(Z));
            t.Children.Add(new RotateTransform3D(Y));
            t.Children.Add(new RotateTransform3D(X));
            t.Children.Add(Scale);
            t.Children.Add(Move);
            t.Children.Add(new MatrixTransform3D(rest));
            t.Children.Add(new RotateTransform3D(Spin));
            Group.Transform = t;
        }

        /// <summary>Eases toward a pose, degrees.</summary>
        public void Ease(double x, double y, double z, double k = 0.18)
        {
            X.Angle += (x - X.Angle) * k;
            Y.Angle += (y - Y.Angle) * k;
            Z.Angle += (z - Z.Angle) * k;
        }

        public void Show(double target, double k = 0.2)
        {
            var s = Scale.ScaleX + (target - Scale.ScaleX) * k;
            if (s < 0.002) s = 0.0001;
            Scale.ScaleX = Scale.ScaleY = Scale.ScaleZ = s;
        }
    }

    /// <summary>A glowing material, recoloured every frame: lighter for solid glow strips, see-through for holograms.</summary>
    /// <summary>Lasting: kept when the materials are rebuilt for a new accent (the "?").</summary>
    sealed record GlowPaint(SolidColorBrush? Brush, DiffuseMaterial? Diffuse, EmissiveMaterial? Emissive, double White, double Alpha, bool Lasting = false);

    Model3DGroup BuildKai(KaiAsset kai)
    {
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(0x78, 0x78, 0x82)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0xD8, 0xD8, 0xE0), new Vector3D(-0.55, -0.6, -1)));
        root.Children.Add(_rim);

        var model = new Model3DGroup { Transform = _pop };
        var groups = new Model3DGroup[kai.Rig.Joints.Count];
        for (var i = 0; i < kai.Rig.Joints.Count; i++)
        {
            var j = kai.Rig.Joints[i];
            var joint = new Joint(ToMatrix(j.Rest));
            _joints[j.Name] = joint;
            groups[i] = joint.Group;
            (j.Parent < 0 ? model : groups[j.Parent]).Children.Add(joint.Group);
        }
        // Parts are added in the order they were read, the see-through holograms last so they
        // blend over what's behind them.
        var order = Enumerable.Range(0, kai.Rig.Parts.Count)
            .OrderBy(i => Source(kai, kai.Rig.Parts[i].Material).Blend ? 1 : 0);
        foreach (var i in order)
        {
            var part = kai.Rig.Parts[i];
            var gm = new GeometryModel3D { Geometry = kai.Meshes[i] };
            groups[part.Joint].Children.Add(gm);
            _parts.Add((gm, Source(kai, part.Material)));
        }

        // The "?" over the head while thinking.
        if (kai.Think is { } think)
        {
            var q = new Joint(new TranslateTransform3D(0.62, 1.53, 0.1).Value);
            q.Show(0, 1);
            var mask = Mask(think);
            var paint = new GlowPaint(null, new DiffuseMaterial(mask), new EmissiveMaterial(mask), 0.15, 1, Lasting: true);
            _glows.Add(paint);
            var mg = new MaterialGroup();
            mg.Children.Add(paint.Diffuse!);
            mg.Children.Add(paint.Emissive!);
            var quad = new MeshGeometry3D
            {
                Positions = [new(-0.22, 0.22, 0), new(0.22, 0.22, 0), new(0.22, -0.22, 0), new(-0.22, -0.22, 0)],
                TextureCoordinates = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)],
                TriangleIndices = [0, 3, 2, 0, 2, 1],
            };
            q.Group.Children.Add(new GeometryModel3D(quad, mg) { BackMaterial = mg });
            _joints["Question"] = q;
            model.Children.Add(q.Group);
        }

        root.Children.Add(model);
        Paint();
        return root;
    }

    static GlbDocument.Material Source(KaiAsset kai, int index) =>
        index >= 0 && index < kai.Rig.Doc.Materials.Count
            ? kai.Rig.Doc.Materials[index]
            : new GlbDocument.Material([1, 1, 1, 1], [0, 0, 0], 1, 0, false, false, false, null, false);

    static Matrix3D ToMatrix(double[] m) => new(
        m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);

    static bool IsGlow(GlbDocument.Material m) => m.Emissive.Sum() > 0.1 || (m.Unlit && m.Blend);

    static bool IsOutline(GlbDocument.Material m) =>
        m.Unlit && !m.Blend && m.Texture is null && 0.2126 * m.BaseColor[0] + 0.7152 * m.BaseColor[1] + 0.0722 * m.BaseColor[2] < 0.05;

    /// <summary>(Re)builds every material for the current accent: tinted colours and pictures, glows, the face.</summary>
    void Paint()
    {
        if (_kai is null) return;
        var tint = new KaiTint(_accent.R, _accent.G, _accent.B);
        _tintedFor = _accent;
        _ink = tint.Apply(KaiFace.Ink.R, KaiFace.Ink.G, KaiFace.Ink.B);
        _glows.RemoveAll(g => !g.Lasting);
        _faces.Clear();

        var pictures = new Dictionary<int, ImageBrush>();
        ImageBrush Picture(int index, bool repeat)
        {
            if (pictures.TryGetValue(index, out var b)) return b;
            var p = _kai.Images[index]!;
            var px = (byte[])p.Bgra.Clone();
            tint.ApplyBgra(px);
            if (index == _kai.FaceImage) _faceBase = p with { Bgra = px };
            b = KaiAsset.TextureBrush(KaiAsset.ToBitmap(px, p.Width, p.Height), repeat);
            return pictures[index] = b;
        }

        foreach (var (gm, m) in _parts)
        {
            Material material;
            var picture = m.Texture is { } t && t < _kai.Images.Length && _kai.Images[t] is not null ? t : (int?)null;
            if (IsOutline(m))
            {
                // A cartoon outline: a slightly larger shell drawn from inside, so only its rim shows.
                var (r, g, b) = tint.Apply(TintBytes(m).R, TintBytes(m).G, TintBytes(m).B);
                gm.Material = null;
                gm.BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(r, g, b)));
                continue;
            }
            if (IsGlow(m))
            {
                var group = new MaterialGroup();
                if (picture is { } pi)
                {
                    // A holo picture (the floor's light pool): its brightness, in the glow colour.
                    var mask = Mask(_kai.Images[pi]!);
                    var paint = new GlowPaint(null, new DiffuseMaterial(mask), new EmissiveMaterial(mask), 0, m.BaseColor[3]);
                    mask.Opacity = m.BaseColor[3];
                    group.Children.Add(paint.Diffuse!);
                    group.Children.Add(paint.Emissive!);
                    _glows.Add(paint);
                }
                else
                {
                    var brush = new SolidColorBrush(_accent);
                    var bright = 0.2126 * m.BaseColor[0] + 0.7152 * m.BaseColor[1] + 0.0722 * m.BaseColor[2] > 0.4;
                    _glows.Add(new GlowPaint(brush, null, null, m.Blend ? 0 : bright ? 0.35 : 0.15, m.Blend ? m.BaseColor[3] : 1));
                    group.Children.Add(new DiffuseMaterial(m.Blend ? brush : new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x14))));
                    group.Children.Add(new EmissiveMaterial(brush));
                }
                material = group;
            }
            else if (picture is { } pi)
            {
                var brush = Picture(pi, m.Repeat);
                if (pi == _kai.FaceImage)
                {
                    // The face: lit, plus a little of its own light so it never goes muddy.
                    _faceBrush = brush;
                    var group = new MaterialGroup();
                    group.Children.Add(new DiffuseMaterial(brush));
                    group.Children.Add(new EmissiveMaterial(brush) { Color = Color.FromRgb(0x40, 0x40, 0x40) });
                    material = group;
                }
                else material = new DiffuseMaterial(brush);
            }
            else
            {
                var (r, g, b) = tint.Apply(TintBytes(m).R, TintBytes(m).G, TintBytes(m).B);
                var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(m.BaseColor[3] * 255), r, g, b));
                if (m.Metallic > 0.5)
                {
                    var group = new MaterialGroup();
                    group.Children.Add(new DiffuseMaterial(brush));
                    group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x98)), 30));
                    material = group;
                }
                else material = new DiffuseMaterial(brush);
            }
            gm.Material = material;
            gm.BackMaterial = m.DoubleSided ? material : null;
        }
        _faceShown = default;
        ShowFace((KaiFace.Eyes.Open, KaiFace.Mouth.Smile, false));
    }

    static (byte R, byte G, byte B) TintBytes(GlbDocument.Material m) => KaiTint.FromLinear(m.BaseColor[0], m.BaseColor[1], m.BaseColor[2]);

    /// <summary>A picture's brightness as a white see-through mask, so a glow colour can be laid over it.</summary>
    static ImageBrush Mask(KaiAsset.Picture p)
    {
        var px = new byte[p.Bgra.Length];
        for (var i = 0; i + 3 < px.Length; i += 4)
        {
            var lum = Math.Max(p.Bgra[i], Math.Max(p.Bgra[i + 1], p.Bgra[i + 2]));
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = (byte)(p.Bgra[i + 3] * lum / 255);
        }
        return KaiAsset.TextureBrush(KaiAsset.ToBitmap(px, p.Width, p.Height), false);
    }

    void ShowFace((KaiFace.Eyes, KaiFace.Mouth, bool) key)
    {
        if (key == _faceShown || _faceBrush is null || _faceBase is null) return;
        if (!_faces.TryGetValue(key, out var bitmap))
        {
            var px = KaiFace.Paint(_faceBase.Bgra, _faceBase.Width, _faceBase.Height, key.Item1, key.Item2, key.Item3, _ink);
            _faces[key] = bitmap = KaiAsset.ToBitmap(px, _faceBase.Width, _faceBase.Height);
        }
        _faceBrush.ImageSource = bitmap;
        _faceShown = key;
    }

    // ------------------------------------------------------------------ the fallback orb

    Model3DGroup BuildOrb()
    {
        var root = new Model3DGroup();
        root.Children.Add(new AmbientLight(Color.FromRgb(0x50, 0x52, 0x58)));
        root.Children.Add(new DirectionalLight(Color.FromRgb(0xE8, 0xE8, 0xEE), new Vector3D(-0.6, -0.7, -1)));
        var (glow, brush) = MeshKit.Glow(_accent);
        _orbGlows.Add(brush);
        var orb = new Model3DGroup { Transform = _orbBob };
        orb.Children.Add(new GeometryModel3D(MeshKit.Ellipsoid(new Point3D(0, 1.1, 0), 0.5, 0.5, 0.5), glow));
        root.Children.Add(orb);
        return root;
    }

    // ------------------------------------------------------------------ petting

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
        Canvas.SetTop(heart, ActualHeight * 0.12);
        heart.Tag = 0.0;
        _overlay.Children.Add(heart);
    }

    // ------------------------------------------------------------------ animation

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

        if (_tintedFor != _accent) Paint();

        // Pop in.
        _appear = Math.Min(1, _appear + dt * 3.5);
        var pop = _appear < 1 ? 1 - Math.Pow(1 - _appear, 3) * Math.Cos(_appear * 9) : 1;
        _pop.ScaleX = _pop.ScaleY = _pop.ScaleZ = Math.Max(0.01, pop);
        _wave = Math.Max(0, _wave - dt);

        // Colour: the accent, leaning toward the mood's, eased.
        var target = Moods ? MoodColor(_accent, _happy > 0.3 ? Feeling.Happy : Mood) : _accent;
        _shown = Mix(_shown, target, 0.08);
        var glow = State == Activity.Listening ? Mix(_shown, Colors.White, Math.Min(Level * 3, 0.5)) : _shown;
        _rim.Color = Mix(_shown, Colors.Black, 0.35);
        foreach (var g in _glows)
        {
            var c = Mix(glow, Colors.White, g.White);
            if (g.Brush is not null) g.Brush.Color = Color.FromArgb((byte)(255 * g.Alpha), c.R, c.G, c.B);
            if (g.Diffuse is not null) g.Diffuse.Color = c;
            if (g.Emissive is not null) g.Emissive.Color = c;
        }
        foreach (var b in _orbGlows) b.Color = glow;
        _orbBob.OffsetY = 0.06 * Math.Sin(t * 2.1);

        if (_kai is not null) Animate(t, dt);
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

    Joint? J(string name) => _joints.GetValueOrDefault(name);

    void Animate(double t, double dt)
    {
        var happy = _happy > 0.3;
        var walking = Math.Abs(Walk) > 0.05;
        var speaking = State == Activity.Speaking;

        // Where to look, within a comfortable range.
        double yaw = 0, pitch = 0;
        if (LookAt is { } look && ActualWidth > 0)
        {
            var dx = (look.X - ActualWidth / 2) / ActualWidth;
            var dy = (look.Y - ActualHeight * 0.32) / ActualHeight;
            yaw = Math.Clamp(dx * 70, -40, 40);
            pitch = Math.Clamp(dy * 50, -18, 22);
        }

        // Body: breathe and bob; bounce while speaking; hop when happy; turn to walk.
        var bodyY = 0.012 * Math.Sin(t * 2.1)
                    + (speaking ? 0.02 * Math.Abs(Math.Sin(t * 8)) : 0)
                    + (happy ? 0.07 * Math.Abs(Math.Sin(t * 9)) : 0)
                    + (walking ? 0.025 * Math.Abs(Math.Sin(t * 10)) : 0);
        if (J(KaiRig.Body) is { } body)
        {
            body.Move.OffsetY = bodyY;
            body.Ease(0, walking ? 38 * Math.Sign(Walk) * Math.Min(1, Math.Abs(Walk) * 2) : 0, walking ? -4 * Walk : 0, 0.12);
        }
        J(KaiRig.Torso)?.Ease(1.5 * Math.Sin(t * 2.1 + 0.6), yaw * 0.25, happy ? 6 * Math.Sin(t * 12) : 0);

        // Head: at the cursor; up and to the side while thinking; tilted toward the ear while listening.
        var (hx, hy, hz) = State switch
        {
            Activity.Thinking => (-10.0, 14 * Math.Sin(t * 0.8), 6.0),
            Activity.Listening => (pitch * 0.5 + 3, yaw * 0.5, -7 + 2 * Math.Sin(t * 1.3)),
            _ when happy => (-6.0, 0.0, 9 * Math.Sin(t * 6)),
            _ when _wave > 0 => (-4.0, yaw * 0.5, -6.0),
            _ => (pitch, yaw, speaking ? 3 * Math.Sin(t * 2.3) : 0),
        };
        J(KaiRig.Head)?.Ease(hx, hy, hz, 0.15);

        // Arms. Left is Kai's left (the viewer's right), on the headphone side.
        double lx, lz, lex, lez, rx, rz, rex, rez;
        if (happy)
        {
            // Cheer.
            (lx, lz, lex, lez) = (0, 125 + 15 * Math.Sin(t * 12), 0, 25);
            (rx, rz, rex, rez) = (0, -125 - 15 * Math.Sin(t * 12 + 1), 0, -25);
        }
        else
        {
            var swing = walking ? 30 * Math.Sin(t * 10) * Math.Min(1, Math.Abs(Walk) * 2) : 2 * Math.Sin(t * 1.7);
            (lx, lz, lex, lez) = (swing, 6 + 2 * Math.Sin(t * 2.1), -12, 0);
            (rx, rz, rex, rez) = (-swing, -6 - 2 * Math.Sin(t * 2.1), -12, 0);
            switch (State)
            {
                case Activity.Listening:
                    (lx, lz, lex, lez) = (-24 + 4 * Math.Sin(t * 3), 68 + Math.Min(Level * 40, 8), -12.6, 96);
                    break;
                case Activity.Thinking:
                    (rx, rz, rex, rez) = (-45, -26, -72 + 4 * Math.Sin(t * 1.5), -28);
                    break;
                case Activity.Speaking:
                    (lx, lz, lex) = (-15 - 12 * Math.Sin(t * 5), 12 + 8 * Math.Sin(t * 3), -35 - 15 * Math.Sin(t * 5 + 1));
                    (rx, rz, rex) = (-15 - 12 * Math.Sin(t * 4.3 + 2), -12 - 8 * Math.Sin(t * 3.4), -35 - 15 * Math.Sin(t * 4.3 + 3));
                    break;
            }
            if (_wave > 0 && State != Activity.Thinking)
                (rx, rz, rex, rez) = (0, -135, 0, -30 + 25 * Math.Sin(t * 14));   // hello!
        }
        J(KaiRig.LeftArm)?.Ease(lx, 0, lz);
        J(KaiRig.LeftElbow)?.Ease(lex, 0, lez);
        J(KaiRig.RightArm)?.Ease(rx, 0, rz);
        J(KaiRig.RightElbow)?.Ease(rex, 0, rez);

        // Legs: step while walking, a little kick when happy.
        var step = walking ? 28 * Math.Sin(t * 10) * Math.Min(1, Math.Abs(Walk) * 2) : happy ? 10 * Math.Sin(t * 9) : 0;
        J(KaiRig.LeftLeg)?.Ease(step, 0, 1.4, 0.3);
        J(KaiRig.RightLeg)?.Ease(-step, 0, -1.4, 0.3);

        // The hair strand springs with the head and body.
        if (J(KaiRig.Head) is { } head && J(KaiRig.Strand) is { } strand)
        {
            var push = (head.Y.Angle - _lastYaw) * 0.6 + (bodyY - _lastBodyY) * 400;
            _lastYaw = head.Y.Angle;
            _lastBodyY = bodyY;
            _strandSpeed += -_strandAngle * 0.25 - _strandSpeed * 0.18 + push;
            _strandAngle = Math.Clamp(_strandAngle + _strandSpeed * 0.5, -35, 35);
            strand.Ease(1.1, 0, 3.4 + _strandAngle, 1);
        }

        // Props: sound waves while listening, "?" while thinking, rings turning.
        J(KaiRig.Waves)?.Show(State == Activity.Listening ? 1 + Math.Min(Level * 2, 0.35) + 0.05 * Math.Sin(t * 6) : 0, 0.3);
        if (J("Question") is { } q)
        {
            q.Show(State == Activity.Thinking ? 1 : 0, 0.2);
            q.Move.OffsetY = 0.03 * Math.Sin(t * 2.5);
            q.Y.Angle = 20 * Math.Sin(t * 1.2);
        }
        var fast = State == Activity.Thinking ? 5 : 1;
        if (J(KaiRig.Ring) is { } ring) ring.Spin.Angle = (ring.Spin.Angle + 25 * fast * dt) % 360;
        if (J(KaiRig.Arcs) is { } arcs)
        {
            arcs.Spin.Angle = (arcs.Spin.Angle - 15 * fast * dt + 360) % 360;
            arcs.Show(State == Activity.Listening ? 1 + Math.Min(Level * 1.5, 0.2) : 1, 0.3);
        }

        // Face.
        var concerned = Moods && Mood == Feeling.Concerned;
        KaiFace.Eyes eyes;
        KaiFace.Mouth mouth;
        if (happy) (eyes, mouth) = (KaiFace.Eyes.Happy, KaiFace.Mouth.Grin);
        else
        {
            (eyes, mouth) = State switch
            {
                Activity.Listening => (KaiFace.Eyes.Wide, KaiFace.Mouth.Oh),
                Activity.Thinking => (KaiFace.Eyes.Lidded, KaiFace.Mouth.Wavy),
                Activity.Speaking => (KaiFace.Eyes.Open, Talk(t)),
                _ when _wave > 0 => (KaiFace.Eyes.Open, KaiFace.Mouth.Grin),
                _ when concerned => (KaiFace.Eyes.Open, KaiFace.Mouth.Frown),
                _ => (KaiFace.Eyes.Open, KaiFace.Mouth.Smile),
            };
            // Blink now and then (not mid-squint).
            if (eyes is (KaiFace.Eyes.Open or KaiFace.Eyes.Wide) && t > _nextBlink)
            {
                var b = t - _nextBlink;
                eyes = b < 0.05 || b > 0.12 ? KaiFace.Eyes.Half : KaiFace.Eyes.Closed;
                if (b > 0.17) _nextBlink = t + 2.5 + Random.Shared.NextDouble() * 3.5;
            }
        }
        ShowFace((eyes, mouth, concerned && !happy));
    }

    static KaiFace.Mouth Talk(double t)
    {
        var v = (Math.Sin(t * 13) + Math.Sin(t * 7.3)) / 2;
        return v > 0.45 ? KaiFace.Mouth.TalkBig : v > -0.15 ? KaiFace.Mouth.TalkSmall : KaiFace.Mouth.Smile;
    }

    /// <summary>The colour for a mood, from the accent.</summary>
    public static Color MoodColor(Color accent, Feeling mood) => mood switch
    {
        Feeling.Happy => Mix(Mix(accent, Color.FromRgb(0x6B, 0xE3, 0x9A), 0.4), Colors.White, 0.1),
        Feeling.Focused => Mix(accent, Color.FromRgb(0x6F, 0xB7, 0xFF), 0.4),
        Feeling.Concerned => Mix(accent, Color.FromRgb(0xFF, 0x8A, 0x4C), 0.55),
        _ => accent,
    };

    /// <summary>A rough reading of a line's tone, from its words. Good enough for a colour and a face.</summary>
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

using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace Vantage.Lockscreen;

static class Program
{
    const string Usage = """
        Lockscreen - the Vantage lock screen HUD

          --preview <out.png>   render the HUD to a file; changes nothing on the PC
          --theme <name>        amber, cyan, phosphor, redalert, mono, neon, custom, or beacon (default: follow Beacon)
          --size <W>x<H>        render at this size instead of the primary monitor's
          --debug-zones         outline the areas kept clear for the Windows clock and sign-in box
          --apply               render at the monitor's size and set it as the lock screen
                                (the first apply saves your current picture for --restore)
          --restore             put back your original lock screen picture
          --emblem <dir>        write the account-picture padlock PNGs (448 and 1080 px);
                                every theme, or just --theme <name>. Changes nothing on the PC
          --status              show the current lock screen picture and what's saved; changes nothing
          --selftest            run the built-in tests
        """;

    [STAThread]
    static int Main(string[] args)
    {
        string? preview = null, theme = null, emblemDir = null;
        int w = 0, h = 0;
        bool zones = false, apply = false, restore = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--selftest": return SelfTest.Run();
                case "--preview" when i + 1 < args.Length: preview = args[++i]; break;
                case "--theme" when i + 1 < args.Length: theme = args[++i]; break;
                case "--size" when i + 1 < args.Length && ParseSize(args[i + 1], out w, out h): i++; break;
                case "--emblem" when i + 1 < args.Length: emblemDir = args[++i]; break;
                case "--debug-zones": zones = true; break;
                case "--apply": apply = true; break;
                case "--restore": restore = true; break;
                case "--status": return Run(() => Task.FromResult((0, LockScreenApplier.Status())));
                default:
                    Console.Error.WriteLine($"Don't understand \"{args[i]}\".\n\n{Usage}");
                    return 1;
            }
        }

        if (restore) return Run(LockScreenApplier.RestoreAsync);
        if (apply) preview = LockScreenApplier.AppliedPng;
        if (preview is null && emblemDir is null) { Console.WriteLine(Usage); return 0; }

        var warnings = new List<string>();
        var cfg = Config.Load(warnings);
        var t = Themes.Resolve(cfg, theme, warnings);
        foreach (var warning in warnings) Console.Error.WriteLine("warning: " + warning);

        if (emblemDir is not null)
        {
            // Every built-in theme, unless --theme pins one (which may be beacon-following or custom).
            var themes = theme is null ? Themes.BuiltIn : [t];
            var dir = emblemDir;
            System.IO.Directory.CreateDirectory(dir);
            foreach (var em in themes)
                foreach (var size in new[] { 448, 1080 })
                {
                    var path = System.IO.Path.Combine(dir, $"emblem-{em.Name}-{size}.png");
                    EmblemRenderer.Save(size, em, path);
                    Console.WriteLine($"{path}  {size}x{size}  theme {em.Name}");
                }
        }
        if (preview is null) return 0;   // --emblem on its own is done here

        // The lock screen is always the monitor's real size; --size is for previews only.
        if (w == 0 || apply) (w, h) = PrimaryMonitor();
        var bmp = HudRenderer.Render(w, h, cfg, t, DateTime.Now, TimeSpan.FromMilliseconds(Environment.TickCount64), zones && !apply);
        HudRenderer.SavePng(bmp, preview);
        Console.WriteLine($"{preview}  {w}x{h}  theme {t.Name}");
        return apply ? Run(() => LockScreenApplier.ApplyAsync(preview)) : 0;
    }

    /// <summary>
    /// Runs a WinRT call on a pool thread (Main is STA for WPF drawing; blocking an STA thread on
    /// WinRT async work can hang) and turns a refusal from Windows into a plain message.
    /// </summary>
    static int Run(Func<Task<(int Code, string Message)>> work)
    {
        try
        {
            var (code, message) = Task.Run(work).GetAwaiter().GetResult();
            (code == 0 ? Console.Out : Console.Error).WriteLine(message);
            return code;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or System.IO.IOException)
        {
            Console.Error.WriteLine($"Windows refused: {e.Message} (0x{e.HResult:X8}). Nothing else was changed.");
            return 5;
        }
    }

    static bool ParseSize(string s, out int w, out int h)
    {
        w = h = 0;
        var p = s.ToLowerInvariant().Split('x');
        return p.Length == 2 && int.TryParse(p[0], out w) && int.TryParse(p[1], out h) && w is >= 320 and <= 16384 && h is >= 320 and <= 16384;
    }

    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    /// <summary>Real pixels of the primary monitor, whatever its display scaling.</summary>
    static (int W, int H) PrimaryMonitor()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));   // per-monitor v2: no scaled-down numbers
        return (GetSystemMetrics(0), GetSystemMetrics(1));
    }
}

/// <summary>Config parsing, token expansion, theme math, and a check of the rendered picture.</summary>
static class SelfTest
{
    static int failed;

    static void Check(bool ok, string what)
    {
        if (ok) return;
        failed++;
        Console.Error.WriteLine("FAIL: " + what);
    }

    static void Eq<T>(T got, T want, string what) => Check(Equals(got, want), $"{what}: got {got}, want {want}");

    public static int Run()
    {
        ConfigTests();
        TokenTests();
        ThemeTests();
        RenderTests();
        EmblemTests();
        Console.WriteLine(failed == 0 ? "selftest ok" : $"selftest: {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void ConfigTests()
    {
        var w = new List<string>();
        var d = new Config();
        Eq(Config.Parse(d.ToJson(), w).ToJson(), d.ToJson(), "defaults survive a round trip");
        Eq(w.Count, 0, "defaults parse without warnings");

        var c = Config.Parse("""
            { "theme": "cyan", "ringCenter": { "x": 0.4, "y": 0.5 }, "ringScale": 1.2,
              "corners": { "topLeft": ["A", "b"] }, "effects": { "grid": false },
              "widgets": { "nodeLabel": "N7", "countdown": { "label": "LAUNCH", "date": "2027-03-01" } },
              "refreshMinutes": 30 }
            """, w);
        Eq(w.Count, 0, "valid file: no warnings");
        Eq(c.Theme, "cyan", "theme");
        Eq(c.RingX, 0.4, "ringCenter.x");
        Eq(c.RingScale, 1.2, "ringScale");
        Eq(c.Corners[0][0], "A", "topLeft title");
        Eq(c.Corners[1][0], "{DATE}", "untouched corner keeps its default");
        Eq(c.Effects.Grid, false, "effects.grid");
        Eq(c.Effects.Glow, true, "missing effect defaults on");
        Eq(c.NodeLabel, "N7", "nodeLabel");
        Eq(c.Countdown.Date, new DateTime(2027, 3, 1), "countdown date");
        Eq(c.RefreshMinutes, 30, "refreshMinutes");

        w.Clear();
        c = Config.Parse("""
            { "theme": "plaid", "ringScale": 9, "ringCenter": { "x": "left" }, "corners": { "topRight": ["only one"] },
              "widgets": { "countdown": { "date": "next week" } }, "refreshMinutes": 1 }
            """, w);
        Eq(w.Count, 6, "each bad value gets its own warning");
        Eq(c.Theme, "beacon", "bad theme falls back");
        Eq(c.RingScale, 1.0, "bad ringScale falls back");
        Eq(c.RingX, 0.5, "wrong type falls back");
        Eq(c.Corners[1][1], "{WEEKDAY}", "bad corner falls back");
        Eq(c.Countdown.Date, new DateTime(2027, 1, 1), "bad date falls back");
        Eq(c.RefreshMinutes, 60, "bad refresh falls back");

        w.Clear();
        Eq(Config.Parse("{ not json", w).ToJson(), d.ToJson(), "broken JSON gives defaults");
        Eq(w.Count, 1, "broken JSON warns once");

        w.Clear();
        Eq(Config.Parse("""{ "theme": "custom" }""", w).Theme, "beacon", "custom without customTheme falls back");
        var custom = Config.Parse("""{ "theme": "custom", "customTheme": { "accent": "#112233", "background": "#000000", "dimOpacity": 0.4, "glow": 2 } }""", w);
        Eq(Themes.Resolve(custom, null, w).Accent, Themes.Hex("#112233"), "custom theme accent");
    }

    static void TokenTests()
    {
        var noon = new DateTime(2026, 11, 14, 12, 0, 0);
        var up = TimeSpan.FromHours(84);   // 3.5 days
        string X(string s) => Tokens.Expand(s, noon, up);
        Eq(X("{DATE}"), "14 NOV 2026", "{DATE}");
        Eq(X("{WEEKDAY}"), "SATURDAY", "{WEEKDAY}");
        Eq(X("{DAY_OF_YEAR}"), "318", "{DAY_OF_YEAR}");
        Eq(X("{YEAR_PROGRESS}"), "87%", "{YEAR_PROGRESS}");
        Eq(X("{UPTIME_DAYS}d uptime"), "3D UPTIME", "{UPTIME_DAYS} and upper case");
        Eq(X("T-{COUNTDOWN:2027-01-01}"), "T-48", "{COUNTDOWN}");
        Eq(X("{COUNTDOWN:2020-01-01}"), "0", "a past countdown stops at 0");
        Eq(X("{COUNTDOWN:soon} {NOPE}"), "{COUNTDOWN:SOON} {NOPE}", "unknown tokens are left alone");
    }

    static void ThemeTests()
    {
        Eq(Themes.Hex("#FFB000"), System.Windows.Media.Color.FromRgb(0xFF, 0xB0, 0x00), "hex");
        Check(!Themes.TryParse("FFB000", out _) && !Themes.TryParse("#FFB0", out _) && !Themes.TryParse("#GGGGGG", out _), "bad hex rejected");
        var amber = Themes.BuiltIn[0];
        Eq(amber.A(0.5).A, (byte)128, "accent at 50%");
        Eq(Themes.Lighten(Themes.Hex("#000000"), 0.5).R, (byte)128, "lighten halfway");
        Eq(Themes.Lighten(Themes.Hex("#102030"), 0), Themes.Hex("#102030"), "lighten by nothing");
        Eq(LockScreenApplier.ImageExtension([0xFF, 0xD8, 0xFF]), ".jpg", "JPEG sniffed");
        Eq(LockScreenApplier.ImageExtension([0x89, 0x50, 0x4E, 0x47, 0x0D]), ".png", "PNG sniffed");
        Eq(LockScreenApplier.ImageExtension([0x42, 0x4D, 0]), ".bmp", "BMP sniffed");

        Eq(Themes.FromBeacon("tactical", "#5B8DEF").Name, "amber", "Tactical -> amber");
        Eq(Themes.FromBeacon("hud", "#5b8def").Name, "cyan", "HUD -> cyan");
        Eq(Themes.FromBeacon("crt", "#5B8DEF").Name, "phosphor", "Retro CRT -> phosphor");
        Eq(Themes.FromBeacon("cyberpunk", "#5B8DEF").Name, "neon", "Cyberpunk -> neon");
        Eq(Themes.FromBeacon("mono", "#5B8DEF").Name, "mono", "Minimal mono -> mono");
        Eq(Themes.FromBeacon("classic", "#5B8DEF").Accent, Themes.Hex("#5B8DEF"), "Classic uses Beacon's accent");
        var red = Themes.FromBeacon("hud", "#FF0000");
        Check(red.Accent == Themes.Hex("#FF0000") && red.Background == Themes.Hex("#0A0F14"), "a chosen accent replaces the style's colour, not its background");
    }

    static void RenderTests()
    {
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (3440, 1440), (1080, 1920) })
        {
            var R = HudRenderer.RingRadius(w, h, 1);
            Check(w * 0.5 - R >= 0 && w * 0.5 + R <= w && h * 0.42 - R >= 0, $"ring fits on {w}x{h}");
            var (clock, _) = HudRenderer.Zones(w, h);
            Check(h * 0.42 - 0.82 * R >= clock.Bottom, $"bright rings stay below the clock on {w}x{h}");
        }

        const int W = 960, H = 540;
        var cfg = new Config();
        var bmp = HudRenderer.Render(W, H, cfg, Themes.BuiltIn[0], new DateTime(2026, 11, 14), TimeSpan.FromDays(3));
        Eq((bmp.PixelWidth, bmp.PixelHeight), (W, H), "render size");
        var px = new byte[W * H * 4];
        bmp.CopyPixels(px, W * 4, 0);
        int Red(double x, double y) => px[((int)y * W + (int)x) * 4 + 2];

        double cx = W * cfg.RingX, cy = H * cfg.RingY, R0 = HudRenderer.RingRadius(W, H, 1);
        Check(Red(cx, cy) < 60, $"the core's centre is empty (red {Red(cx, cy)})");
        var (clockZone, signIn) = HudRenderer.Zones(W, H);
        Check(Red(clockZone.Left + clockZone.Width / 2, clockZone.Top + clockZone.Height / 2) < 60, "clock zone is dark");
        Check(Red(signIn.Left + signIn.Width / 2, signIn.Top + signIn.Height / 2) < 60, "sign-in zone is dark");

        // The thick dashed ring: bright along most of its circle (dash 70 of every 104).
        var lit = Enumerable.Range(0, 360).Count(a => Red(cx + 0.82 * R0 * Math.Cos(a * Math.PI / 180), cy + 0.82 * R0 * Math.Sin(a * Math.PI / 180)) > 150);
        Check(lit is > 180 and < 300, $"dashed ring is drawn with gaps ({lit}/360 bright)");
        Check(Red(44 * 0.5 + 4, 44 * 0.5) > 120, $"top-left bracket is drawn (red {Red(44 * 0.5 + 4, 44 * 0.5)})");
    }

    static void EmblemTests()
    {
        foreach (var size in new[] { 448, 1080 })
        {
            var t = Themes.BuiltIn[0];   // amber
            var bmp = EmblemRenderer.Render(size, t);
            Eq((bmp.PixelWidth, bmp.PixelHeight), (size, size), $"emblem {size} size");
            var px = new byte[size * size * 4];   // Pbgra32: B,G,R,A, premultiplied
            bmp.CopyPixels(px, size * 4, 0);
            byte At(int x, int y, int ch) => px[(y * size + x) * 4 + ch];

            Check(At(2, 2, 3) == 0, $"emblem {size} corner is transparent");
            Check(At(size / 2, size / 2, 3) > 0, $"emblem {size} centre has the disc");

            // Full-opacity accent pixels (the sharp padlock and ring; the blurred glow is skipped).
            var accent = 0;
            for (var i = 0; i < size * size; i++)
                if (px[i * 4 + 3] == 255 &&
                    Math.Abs(px[i * 4] - t.Accent.B) < 30 && Math.Abs(px[i * 4 + 1] - t.Accent.G) < 30 && Math.Abs(px[i * 4 + 2] - t.Accent.R) < 30)
                    accent++;
            Check(accent > 300, $"emblem {size} draws the accent padlock ({accent} px)");
        }
    }
}

using System.Diagnostics;
using System.IO;

namespace Beacon.Providers;

/// <param name="Path">
/// Where the app actually lives on disk: the .exe a Start Menu shortcut points at, resolved
/// after the list is first published. Null for Store apps, which have no path a user can use.
/// </param>
public sealed record AppEntry(
    string Name,
    string LaunchTarget,
    string Subtitle,
    bool IsPackaged,
    string? IconSource,
    string? Path = null)
{
    /// <summary>
    /// What the row shows underneath the name, and what Ctrl+C copies: the executable when
    /// we know it, the shortcut it came from until then, and for a Store app the
    /// shell:AppsFolder name - which is not a file, but is the thing you can make a shortcut
    /// to or pass to explorer.exe.
    /// </summary>
    public string Location => Path ?? LaunchTarget;
}

/// <summary>
/// Builds the list of launchable applications once at startup, in the background.
///
/// Two sources, because neither alone is complete:
///   * Start Menu .lnk scan   - fast, gives real file paths (so we can pull icons), Win32 only.
///   * Get-StartApps          - the only dependency-free way to enumerate packaged/UWP apps,
///                              but costs a PowerShell spawn, so it runs off the hot path.
/// </summary>
public sealed class AppIndex
{
    private volatile IReadOnlyList<AppEntry> _entries = [];

    public IReadOnlyList<AppEntry> Entries => _entries;
    public bool IsReady { get; private set; }

    public event Action? Updated;

    private int _building;   // 1 while a build runs: a burst of installs rebuilds once, not once per file
    private bool _again;     // something changed during that build: run one more
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounce;

    /// <summary>
    /// Watches both Start Menus, so an app installed while Beacon runs can be opened by name
    /// a few seconds later, without restarting Beacon. Installers write several shortcuts in
    /// a burst, so changes are gathered for a few seconds before one rebuild.
    /// </summary>
    public void WatchForInstalls()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var watcher = new FileSystemWatcher(root, "*.lnk")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += (_, _) => RequestRefresh();
                watcher.Deleted += (_, _) => RequestRefresh();
                watcher.Renamed += (_, _) => RequestRefresh();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // No watching on this root; the on-miss rescan below still finds new apps.
            }
        }
    }

    /// <summary>
    /// Rebuilds the list soon. Also called when a spoken or typed "open X" finds nothing, which
    /// is how Store apps (no Start Menu shortcut to watch) are picked up after installing.
    /// </summary>
    public void RequestRefresh(TimeSpan? delay = null)
    {
        _debounce ??= new Timer(_ => _ = RebuildAsync());
        _debounce.Change(delay ?? TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private async Task RebuildAsync()
    {
        if (Interlocked.Exchange(ref _building, 1) == 1) { _again = true; return; }
        try
        {
            do
            {
                _again = false;
                try { await BuildAsync().ConfigureAwait(false); }
                catch (Exception e) when (e is not OutOfMemoryException) { }   // keep the old list
            }
            while (_again);
        }
        finally { Interlocked.Exchange(ref _building, 0); }
    }

    public async Task BuildAsync(CancellationToken ct = default)
    {
        // Shortcuts land first so the launcher is usable within milliseconds of startup.
        var shortcuts = await Task.Run(ScanStartMenu, ct).ConfigureAwait(false);
        Publish(shortcuts);

        // Then upgrade each one from "a shortcut somewhere in the Start Menu" to the real
        // executable path. Separate pass because it is COM work an order of magnitude slower
        // than the scan, and nothing about searching needs to wait for it.
        shortcuts = await ResolveTargetsAsync(shortcuts, ct).ConfigureAwait(false);
        Publish(shortcuts);

        var packaged = await ScanPackagedAppsAsync(ct).ConfigureAwait(false);
        if (packaged.Count > 0)
        {
            var merged = shortcuts.ToList();
            var known = shortcuts.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            merged.AddRange(packaged.Where(p => known.Add(p.Name)));
            Publish(merged);
        }

        IsReady = true;
    }

    /// <summary>
    /// Resolves every shortcut to its target on one STA thread, because IShellLink is
    /// apartment-threaded. Entries whose target cannot be read keep their shortcut path -
    /// still a real location, just a less useful one.
    /// </summary>
    private static Task<List<AppEntry>> ResolveTargetsAsync(
        IReadOnlyList<AppEntry> entries, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<List<AppEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var resolved = new List<AppEntry>(entries.Count);

            foreach (var entry in entries)
            {
                if (ct.IsCancellationRequested) break;

                var target = entry.IsPackaged ? null : Interop.ShortcutResolver.Resolve(entry.LaunchTarget);
                resolved.Add(target is null ? entry : entry with { Path = target });
            }

            tcs.TrySetResult(resolved.Count == entries.Count ? resolved : entries.ToList());
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Beacon shortcut resolver",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private void Publish(IReadOnlyList<AppEntry> entries)
    {
        _entries = entries;
        Updated?.Invoke();
    }

    private static List<AppEntry> ScanStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };

        var found = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                string name;
                try
                {
                    name = Path.GetFileNameWithoutExtension(file);
                }
                catch
                {
                    continue;
                }

                if (IsNoise(name)) continue;

                // First writer wins: per-user shortcuts are scanned after machine-wide ones
                // and are usually the more relevant of a duplicate pair, but either is fine.
                found.TryAdd(name, new AppEntry(
                    Name: name,
                    LaunchTarget: file,
                    Subtitle: "Application",
                    IsPackaged: false,
                    IconSource: file));
            }
        }

        return found.Values.ToList();
    }

    /// <summary>
    /// Uninstallers, help files and readme links clutter the list badly if left in.
    /// </summary>
    private static bool IsNoise(string name)
    {
        ReadOnlySpan<string> noise =
        [
            "uninstall", "readme", "read me", "help", "license", "changelog",
            "release notes", "documentation", "website", "support", "报错",
        ];

        foreach (var n in noise)
            if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Packaged (Store/UWP) apps have no file path to find, so we ask the shell.
    /// Anything that fails here is non-fatal: the user simply gets Win32 apps only.
    /// </summary>
    private static async Task<List<AppEntry>> ScanPackagedAppsAsync(CancellationToken ct)
    {
        var results = new List<AppEntry>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                            "\"Get-StartApps | Where-Object { $_.AppID -like '*!*' } | " +
                            "ForEach-Object { $_.Name + '|' + $_.AppID }\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return results;

            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('|', 2);
                if (parts.Length != 2) continue;

                var name = parts[0].Trim();
                var appId = parts[1].Trim();
                if (name.Length == 0 || appId.Length == 0) continue;
                if (IsNoise(name)) continue;

                var shellName = $"shell:AppsFolder\\{appId}";

                results.Add(new AppEntry(
                    Name: name,
                    LaunchTarget: shellName,
                    Subtitle: "Store app",
                    IsPackaged: true,
                    // Packaged apps have no file to take an icon from, but the Shell can
                    // render one from the same AppsFolder name used to launch them.
                    IconSource: shellName));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Packaged app enumeration is best-effort.
        }

        return results;
    }

    public static void Launch(AppEntry entry)
    {
        if (entry.IsPackaged)
        {
            // Packaged apps can only be started via the shell's AppsFolder alias.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = entry.LaunchTarget,
                UseShellExecute = true,
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = entry.LaunchTarget,
            UseShellExecute = true,
        });
    }
}

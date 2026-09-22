using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Beacon.Core;

namespace Beacon.Providers;

/// <param name="Score">
/// Match score including the path penalty, when it came from the local index. Zero for
/// results from Everything, which the caller scores itself.
/// </param>
public sealed record FileEntry(string Name, string FullPath, bool IsFolder, int Score = 0);

/// <summary>
/// A whole-machine filename index.
///
/// <para>Storage: paths are not stored whole. Measured on this machine, a full C: + D: walk
/// finds ~427,000 files spread over only ~79,000 folders, so the directory portion of a path
/// repeats about five times on average. Interning directories and keeping just
/// (directoryIndex, fileName) per entry costs roughly a third of what one string per file
/// would - about 45 MB instead of 140 MB. Full paths are materialised only for the handful
/// of results actually shown.</para>
///
/// <para>This is a deliberate second-best. Everything (voidtools) reads the NTFS Master File
/// Table directly and keeps itself current from the USN journal, which is why it indexes in
/// seconds and updates live. Doing that ourselves requires opening a raw volume handle,
/// which requires elevation - and this build runs as a normal user by design. So we walk the
/// tree instead: slower to build (~16s), and only as fresh as the last refresh.</para>
/// </summary>
public sealed class FileIndex
{
    /// <summary>Hard ceiling so a pathological disk cannot exhaust memory.</summary>
    private const int MaxEntries = 2_000_000;

    // Parallel arrays rather than an object per file: 427k objects would add ~7 MB of
    // headers alone, and none of the per-entry data needs identity.
    private string[] _dirs = [];
    private string[] _names = [];
    private int[] _dirOf = [];
    private bool[] _isFolder = [];

    /// <summary>
    /// Per-directory ranking adjustment, computed once at build time.
    ///
    /// The index deliberately contains everything, including dependency trees and build
    /// output - but a hit inside .venv or obj is almost never the one you meant. Rather
    /// than hiding those files (which would defeat the point of a full index), they are
    /// ranked down. Because directories are interned, this is one lookup per result
    /// instead of per-path string work on every keystroke.
    /// </summary>
    private int[] _dirPenalty = [];

    private int _count;

    public bool IsReady { get; private set; }
    public bool IsBuilding { get; private set; }
    public int Count => _count;
    public int FolderCount => _dirs.Length;
    public TimeSpan LastBuildDuration { get; private set; }
    public DateTimeOffset LastBuilt { get; private set; }

    /// <summary>Raised when a rebuild finishes, so the UI can refresh its hint.</summary>
    public event Action? Updated;

    // ------------------------------------------------------------------- roots

    /// <summary>
    /// Every fixed drive. "Index everything" is taken literally; what gets filtered is
    /// controlled by <see cref="Settings.ExcludedFolders"/> and the hidden/system rule.
    /// </summary>
    public static IReadOnlyList<string> FixedDriveRoots()
    {
        var roots = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                roots.Add(drive.RootDirectory.FullName);
            }
            catch
            {
                // A drive that disappears mid-enumeration is not worth failing over.
            }
        }

        return roots;
    }

    // ------------------------------------------------------------------ build

    public Task BuildAsync(IndexOptions options, CancellationToken ct = default) =>
        Task.Run(() => Build(options, ct), ct);

    private void Build(IndexOptions options, CancellationToken ct)
    {
        if (IsBuilding) return;
        IsBuilding = true;

        var clock = Stopwatch.StartNew();

        try
        {
            // Skipping reparse points is a correctness requirement, not a filter: junctions
            // such as the legacy "Application Data" links point back up the tree, and
            // following them duplicates entries or loops forever.
            var attributesToSkip = FileAttributes.ReparsePoint;
            if (!options.IncludeHiddenAndSystem)
                attributesToSkip |= FileAttributes.Hidden | FileAttributes.System;

            var enumeration = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = attributesToSkip,
                ReturnSpecialDirectories = false,
            };

            var dirIndexByPath = new Dictionary<string, int>(128_000, StringComparer.OrdinalIgnoreCase);
            var dirs = new List<string>(128_000);
            var names = new List<string>(512_000);
            var dirOf = new List<int>(512_000);
            var isFolder = new List<bool>(512_000);

            int InternDirectory(string path)
            {
                if (dirIndexByPath.TryGetValue(path, out var existing)) return existing;

                var index = dirs.Count;
                dirs.Add(path);
                dirIndexByPath[path] = index;
                return index;
            }

            var pending = new Stack<string>();
            foreach (var root in options.Roots) pending.Push(root);

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (names.Count >= MaxEntries) break;

                var dir = pending.Pop();
                if (options.IsExcluded(dir)) continue;

                int dirIndex;
                try
                {
                    dirIndex = InternDirectory(dir);
                }
                catch
                {
                    continue;
                }

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", enumeration))
                    {
                        names.Add(Path.GetFileName(file));
                        dirOf.Add(dirIndex);
                        isFolder.Add(false);

                        if (names.Count >= MaxEntries) break;
                    }
                }
                catch
                {
                    // Permission denied or the folder vanished; keep going.
                }

                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir, "*", enumeration))
                    {
                        if (options.IsExcluded(sub)) continue;

                        names.Add(Path.GetFileName(sub));
                        dirOf.Add(dirIndex);
                        isFolder.Add(true);

                        pending.Push(sub);
                    }
                }
                catch
                {
                    // Same: an unreadable subtree must not abort the whole index.
                }
            }

            _dirs = [.. dirs];
            _names = [.. names];
            _dirOf = [.. dirOf];
            _isFolder = [.. isFolder];
            _dirPenalty = dirs.Select(PathPenalty).ToArray();
            _count = names.Count;

            LastBuildDuration = clock.Elapsed;
            LastBuilt = DateTimeOffset.Now;
            IsReady = true;
        }
        catch (OperationCanceledException)
        {
            // Leave whatever was previously published in place.
        }
        finally
        {
            IsBuilding = false;
            Updated?.Invoke();
        }
    }

    // --------------------------------------------------------- path ranking

    /// <summary>Folders whose contents are machine-generated rather than authored.</summary>
    private static readonly HashSet<string> LowValueSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "site-packages", "dist-packages", "__pycache__",
        ".venv", "venv", ".git", ".svn", ".hg", ".tox", ".mypy_cache", ".pytest_cache",
        "obj", "bin", "dist", "build", "target", "out", "Release", "Debug",
        "packages", "vendor", "Pods", ".gradle", ".m2", ".cargo", ".rustup", ".nuget",
        "DriverStore", "Installer", "Downloaded Installations", "$Windows.~BT",
    };

    /// <summary>Folders that hold real data but are usually not what a person means.</summary>
    private static readonly HashSet<string> BackgroundSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Recent", "Temp", "tmp", "Cache", "cache", "Caches",
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System32",
    };

    // On the shared 0-1000 match scale. A prefix hit buried in AppData lands around 750,
    // below "Open YouTube" (~1025) but still above weak fuzzy matches elsewhere.
    private const int LowValuePenalty = 250;
    private const int BackgroundPenalty = 120;
    private const int DepthPenalty = 3;
    private const int MaxPenalty = 400;

    private static int PathPenalty(string dir)
    {
        var penalty = 0;
        var depth = 0;

        foreach (var range in dir.AsSpan().Split(Path.DirectorySeparatorChar))
        {
            var segment = dir.AsSpan()[range];
            if (segment.IsEmpty) continue;

            depth++;

            var text = segment.ToString();
            if (LowValueSegments.Contains(text)) penalty += LowValuePenalty;
            else if (BackgroundSegments.Contains(text)) penalty += BackgroundPenalty;
        }

        // A file buried twelve levels deep is less likely to be the target than one near
        // the top of a project, independent of what the folders are called.
        if (depth > 4) penalty += (depth - 4) * DepthPenalty;

        return -Math.Min(penalty, MaxPenalty);
    }

    // ----------------------------------------------------------------- search

    /// <summary>
    /// Scores every entry in parallel. At ~427k entries a single-threaded pass is well over
    /// 100ms, which would be felt on every keystroke; partitioning it across cores and
    /// keeping only a small top-N per partition brings it into single-digit milliseconds.
    /// </summary>
    public IReadOnlyList<FileEntry> Search(string needle, int max, CancellationToken ct)
    {
        if (!IsReady || _count == 0 || string.IsNullOrWhiteSpace(needle)) return [];

        // A query containing a separator is about location, so match the whole path.
        var matchFullPath = needle.Contains('\\') || needle.Contains('/');

        var names = _names;
        var dirOf = _dirOf;
        var dirs = _dirs;
        var penalties = _dirPenalty;
        var count = _count;

        var partitions = new ConcurrentBag<List<(int Score, int Index)>>();

        try
        {
            Parallel.For(
                0,
                Environment.ProcessorCount,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                partition =>
                {
                    var local = new List<(int Score, int Index)>(max * 2);
                    var lowest = int.MinValue;

                    var chunk = (count + Environment.ProcessorCount - 1) / Environment.ProcessorCount;
                    var start = partition * chunk;
                    var end = Math.Min(start + chunk, count);

                    for (var i = start; i < end; i++)
                    {
                        var score = matchFullPath
                            ? FuzzyMatcher.Score(Path.Combine(dirs[dirOf[i]], names[i]), needle)
                            : FuzzyMatcher.Score(names[i], needle);

                        if (score == FuzzyMatcher.NoMatch) continue;

                        score += penalties[dirOf[i]];

                        // Keep the partition list small; only sort when it grows past 4x.
                        if (local.Count >= max * 4)
                        {
                            local.Sort(static (a, b) => b.Score.CompareTo(a.Score));
                            local.RemoveRange(max, local.Count - max);
                            lowest = local[^1].Score;
                        }

                        if (local.Count >= max && score <= lowest) continue;
                        local.Add((score, i));
                    }

                    partitions.Add(local);
                });
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        return partitions
            .SelectMany(p => p)
            .OrderByDescending(x => x.Score)
            .Take(max)
            .Select(x => new FileEntry(
                names[x.Index],
                Path.Combine(dirs[dirOf[x.Index]], names[x.Index]),
                _isFolder[x.Index],
                x.Score))
            .ToList();
    }
}

/// <summary>What to index and what to leave out.</summary>
public sealed class IndexOptions
{
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>
    /// Folder names (not paths) to skip anywhere in the tree. Defaults cover build output
    /// and dependency caches, which can easily be the majority of files on a dev machine
    /// while being the last thing anyone searches for.
    /// </summary>
    public required IReadOnlyCollection<string> ExcludedFolderNames { get; init; }

    /// <summary>
    /// Hidden and system files are excluded by default. They are overwhelmingly OS
    /// internals, and including them roughly doubles the index for no practical gain.
    /// </summary>
    public bool IncludeHiddenAndSystem { get; init; }

    /// <summary>
    /// Whole subtrees to skip, by full path. Used for Beacon's own data folder: without it,
    /// the favicon cache (youtube.com.ico and friends) shows up as search results.
    /// </summary>
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];

    private HashSet<string>? _excluded;

    public bool IsExcluded(string path)
    {
        foreach (var excludedPath in ExcludedPaths)
        {
            if (path.StartsWith(excludedPath, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (ExcludedFolderNames.Count == 0) return false;

        _excluded ??= new HashSet<string>(ExcludedFolderNames, StringComparer.OrdinalIgnoreCase);

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        return name.Length > 0 && _excluded.Contains(name);
    }
}

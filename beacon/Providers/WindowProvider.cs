using System.Runtime.InteropServices;
using System.Text;
using Beacon.Core;
using Beacon.Interop;

namespace Beacon.Providers;

/// <summary>
/// Open windows as results: type part of a window's title or its app's name and jump to it.
///
/// Ranked just above installed apps, because when an app is already open, going back to it
/// is nearly always what was meant - typing "brave" should not start a second browser.
/// </summary>
public sealed class WindowProvider : IResultProvider
{
    public string Name => "Windows";

    public int Weight => 40;

    public Task<IReadOnlyList<SearchResult>> QueryAsync(SearchQuery query, CancellationToken ct)
    {
        if (query.Text.Length < 2) return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var self = (uint)Environment.ProcessId;
        var results = new List<SearchResult>();

        foreach (var (title, process, handle) in CommandRegistry.EnumerateWindows())
        {
            ct.ThrowIfCancellationRequested();

            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            if (pid == self) continue;

            var score = FuzzyMatcher.ScoreAny(query.Text, title, process);
            if (score == FuzzyMatcher.NoMatch) continue;

            var exe = ExecutablePath(pid);
            var window = handle;

            results.Add(new SearchResult
            {
                Title = title,
                Subtitle = $"Switch to this window  ·  {process}",
                Source = "Window",
                Glyph = "",
                IconPath = exe,
                CopyText = exe,
                Score = score + Weight,
                InvokeAsync = ctx =>
                {
                    ctx.Dismiss();
                    ForegroundTracker.FocusWindow(window);
                    return Task.CompletedTask;
                },
            });
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    /// <summary>
    /// The window's executable, for its icon. QueryFullProcessImageName rather than
    /// Process.MainModule: the latter throws for elevated processes and for x64 processes
    /// inspected from this ARM64 one, which is most of them here.
    /// </summary>
    internal static string? ExecutablePath(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

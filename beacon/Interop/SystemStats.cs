using System.Runtime.InteropServices;

namespace Beacon.Interop;

/// <summary>
/// CPU and memory load for the Tactical header readouts.
///
/// Straight kernel32 calls rather than PerformanceCounter: no counter-category lookup, no
/// extra package, and cheap enough to sample every second while the bar is open.
/// </summary>
public sealed class SystemStats
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    /// <summary>
    /// Percentage of CPU time spent busy since the previous call. The first call has no
    /// earlier sample to compare with and returns 0.
    /// </summary>
    public int CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        // Kernel time includes idle time, so busy = (kernel + user) - idle.
        var total = kernel.Value + user.Value;
        var idleDelta = idle.Value - _lastIdle;
        var totalDelta = total - _lastTotal;
        var first = _lastTotal == 0;

        _lastIdle = idle.Value;
        _lastTotal = total;

        if (first || totalDelta == 0) return 0;
        return (int)Math.Round(100.0 * (totalDelta - idleDelta) / totalDelta);
    }

    public int MemoryPercent()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (int)status.MemoryLoad : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

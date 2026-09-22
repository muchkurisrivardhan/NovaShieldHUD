using System.Diagnostics;

namespace Beacon.Interop;

/// <summary>
/// The radio controls Windows exposes to a normal, non-elevated process.
///
/// This is thinner than it looks like it should be, on purpose. Flipping the Wi-Fi or
/// Bluetooth *radio* needs either administrator rights or the WinRT Radios API, which would
/// pull the Windows SDK projection into the build. What a plain process can do is disconnect
/// and reconnect Wi-Fi through netsh, so that is what these methods do - and everything else
/// opens the exact Settings page rather than reporting a success that never happened.
/// </summary>
internal static class NetworkControl
{
    /// <summary>Drops the current Wi-Fi connection. Works without elevation.</summary>
    public static bool Disconnect() => Netsh("wlan disconnect", out _);

    /// <summary>
    /// Reconnects to a saved network - the first profile netsh lists, which is the one
    /// Windows itself would prefer. False when there is no saved profile to use.
    /// </summary>
    public static bool TryReconnect(out string? profile)
    {
        profile = FirstProfile();
        return profile is not null && Netsh($"wlan connect name=\"{profile}\"", out _);
    }

    private static string? FirstProfile()
    {
        if (!Netsh("wlan show profiles", out var output)) return null;

        foreach (var line in output.Split('\n'))
        {
            // "    All User Profile     : HomeNetwork"
            var colon = line.IndexOf(':');
            if (colon < 0 || !line.Contains("Profile", StringComparison.OrdinalIgnoreCase)) continue;

            var name = line[(colon + 1)..].Trim();
            if (name.Length > 0) return name;
        }

        return null;
    }

    internal static bool Netsh(string arguments, out string output)
    {
        output = string.Empty;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return false;

            output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

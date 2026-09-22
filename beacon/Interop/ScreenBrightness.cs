using System.Diagnostics;
using System.Reflection;

namespace Beacon.Interop;

/// <summary>
/// Laptop panel brightness, through the WMI classes the brightness slider itself uses.
///
/// Two deliberate choices:
///
/// WMI is reached by late-bound COM (WbemScripting.SWbemLocator) rather than the
/// System.Management package. That package is a NuGet dependency this app does not
/// otherwise need, and the scripting API has been in every Windows since XP.
///
/// External monitors almost always refuse. Desktop displays expose brightness over DDC/CI,
/// not WMI, and most simply do not implement WmiMonitorBrightnessMethods - so on a desktop
/// PC these calls fail, and the caller is expected to say so rather than pretend.
/// </summary>
internal static class ScreenBrightness
{
    public static int? Get()
    {
        try
        {
            foreach (var monitor in Query("SELECT * FROM WmiMonitorBrightness"))
            {
                var value = GetProperty(monitor, "CurrentBrightness");
                if (value is not null) return Convert.ToInt32(value);
            }
        }
        catch
        {
            // No WMI, no panel, or a provider that refuses to answer.
        }

        return null;
    }

    /// <summary>True when at least one display accepted the new level.</summary>
    public static bool TrySet(int percent)
    {
        var level = Math.Clamp(percent, 0, 100);

        try
        {
            var applied = false;
            foreach (var methods in Query("SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                // WmiSetBrightness(timeout in seconds, brightness 0-100).
                Invoke(methods, "WmiSetBrightness", BindingFlags.InvokeMethod, 1u, (byte)level);
                applied = true;
            }

            if (applied) return true;
        }
        catch
        {
            // Fall through to the shell path below.
        }

        return TrySetViaPowerShell(level);
    }

    /// <summary>
    /// Last resort. Slower (a whole PowerShell start-up) but it goes through CIM rather than
    /// the scripting API, so it still works if late-bound COM is unavailable.
    /// </summary>
    private static bool TrySetViaPowerShell(int level)
    {
        try
        {
            var script =
                "$m = Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBrightnessMethods " +
                "-ErrorAction Stop; " +
                $"Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness " +
                $"-Arguments @{{Timeout=1; Brightness={level}}} | Out-Null";

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            // Bounded: a hung WMI provider must not hang the launcher.
            return process.WaitForExit(6000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------- WMI plumbing

    private static IEnumerable<object> Query(string wql)
    {
        var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        if (locatorType is null) yield break;

        var locator = Activator.CreateInstance(locatorType);
        if (locator is null) yield break;

        var service = Invoke(locator, "ConnectServer", BindingFlags.InvokeMethod, ".", "root\\wmi");
        if (service is null) yield break;

        var set = Invoke(service, "ExecQuery", BindingFlags.InvokeMethod, wql);
        if (set is null) yield break;

        // SWbemObjectSet is an IEnumVARIANT collection, which does not survive a cast to
        // IEnumerable on .NET Core - walk it by index instead.
        var count = Convert.ToInt32(GetProperty(set, "Count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            var item = Invoke(set, "ItemIndex", BindingFlags.InvokeMethod, i);
            if (item is not null) yield return item;
        }
    }

    private static object? Invoke(object target, string name, BindingFlags flags, params object?[] args) =>
        target.GetType().InvokeMember(name, flags, null, target, args);

    private static object? GetProperty(object target, string name)
    {
        try
        {
            return Invoke(target, name, BindingFlags.GetProperty);
        }
        catch
        {
            return null;
        }
    }
}

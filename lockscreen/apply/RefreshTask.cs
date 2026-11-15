using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace Vantage.Lockscreen;

/// <summary>
/// A Task Scheduler task, for the current user only, that runs "Lockscreen.exe --refresh" when you
/// sign in, when you lock the PC, and every refreshMinutes, so {DATE} and {UPTIME_DAYS} stay current.
/// It runs as you with your normal rights (no admin, no stored password) and is made with Windows'
/// own schtasks.exe. --apply adds it; --restore removes it.
/// </summary>
public static class RefreshTask
{
    public const string Name = "NovaShield HUD lock screen refresh";

    // ponytail: conhost --headless keeps the console window from flashing up every hour; it ships with
    // Windows 10 1809+ but isn't documented. If it ever goes, make a WinExe twin of Lockscreen.exe.
    public static string Xml(string exe, int minutes)
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        exe = SecurityElement.Escape(exe);
        var start = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss");
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Keeps the NovaShield HUD lock screen picture's date and uptime current. Remove it with: Lockscreen.exe --restore</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><UserId>{user}</UserId></LogonTrigger>
                <SessionStateChangeTrigger><StateChange>SessionLock</StateChange><UserId>{user}</UserId></SessionStateChangeTrigger>
                <TimeTrigger><StartBoundary>{start}</StartBoundary><Repetition><Interval>PT{minutes}M</Interval></Repetition></TimeTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>conhost.exe</Command><Arguments>--headless "{exe}" --refresh</Arguments></Exec></Actions>
            </Task>
            """;
    }

    /// <summary>Adds or updates the task. Returns null, or why Windows refused.</summary>
    public static string? Install(int minutes)
    {
        var xml = Path.Combine(Path.GetTempPath(), "novashield-refresh-task.xml");
        File.WriteAllText(xml, Xml(Environment.ProcessPath!, minutes), Encoding.Unicode);   // schtasks wants UTF-16
        try { return Schtasks("/Create", "/F", "/TN", Name, "/XML", xml); }
        finally { File.Delete(xml); }
    }

    public static string? Remove() => Installed ? Schtasks("/Delete", "/F", "/TN", Name) : null;

    public static bool Installed => Schtasks("/Query", "/TN", Name) is null;

    static string? Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? null : err.Trim();
    }
}

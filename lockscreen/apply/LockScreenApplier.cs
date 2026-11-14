using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System.UserProfile;

namespace Vantage.Lockscreen;

/// <summary>
/// Sets the lock screen picture through the documented WinRT API (LockScreen.SetImageFileAsync)
/// and puts the original back. Nothing else on the PC is changed: Spotlight is only read, never
/// switched off, and the sign-in screen setting is only mentioned, never written.
///
/// The first apply saves a copy of the current picture to %LOCALAPPDATA%\LockscreenWidget\original.
/// That copy is never overwritten (a later apply would otherwise back up the HUD itself);
/// --restore sets it back and then deletes it, so the next apply backs up whatever is current then.
/// </summary>
public static class LockScreenApplier
{
    static readonly string BackupDir = Path.Combine(Config.Dir, "original");
    static readonly string BackupInfo = Path.Combine(BackupDir, "backup.json");
    static readonly string HintedFile = Path.Combine(Config.Dir, "signin-hint-shown");
    public static readonly string AppliedPng = Path.Combine(Config.Dir, "current.png");

    /// <param name="AppliedSha">What Windows reported after our last apply. If the current picture
    /// differs, you set a new one yourself since, and that becomes the original to restore.</param>
    sealed record Backup(string Image, string? OriginalUri, bool Spotlight, DateTime SavedUtc, string? AppliedSha = null);

    /// <summary>Windows Spotlight rotating the lock screen picture (read only).</summary>
    public static bool SpotlightOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
        return k?.GetValue("RotatingLockScreenEnabled") is int v && v != 0;
    }

    public static bool HasBackup => File.Exists(BackupInfo);

    public static string Status()
    {
        long size;
        using (var s = LockScreen.GetImageStream()) size = (long)s.Size;
        return $"""
            Current lock screen picture: {size:n0} bytes{(File.Exists(AppliedPng) && HasBackup ? " (the HUD, if nothing changed it since)" : "")}
            Windows Spotlight on the lock screen: {(SpotlightOn() ? "ON" : "off")}
            Original saved for --restore: {(HasBackup ? ReadBackup().SavedUtc.ToLocalTime().ToString("g") : "no (the first --apply saves it)")}
            """;
    }

    /// <summary>Sets <paramref name="png"/> as the lock screen. Returns (exit code, message).</summary>
    public static async Task<(int Code, string Message)> ApplyAsync(string png)
    {
        if (SpotlightOn())
            return (3, "Windows Spotlight is choosing your lock screen picture, so a picture can't be set.\n" +
                       "Switch it to a picture first: Settings > Personalization > Lock screen > Personalize your lock screen > Picture.\n" +
                       "Nothing was changed.");

        var current = await CurrentPictureAsync();
        if (!HasBackup || ReadBackup().AppliedSha != Sha(current)) await SaveBackupAsync(current);

        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(png));
        await LockScreen.SetImageFileAsync(file);
        var info = ReadBackup() with { AppliedSha = Sha(await CurrentPictureAsync()) };
        Settings.WriteAtomic(BackupInfo, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));

        var msg = "Lock screen set. Press Win+L to see it.";
        if (!File.Exists(HintedFile))
        {
            msg += "\n\nOne-time tip: to see the HUD behind the PIN or password box too, turn on\n" +
                   "Settings > Personalization > Lock screen > \"Show the lock screen background picture on the sign-in screen\".\n" +
                   "To put your old picture back at any time: Lockscreen.exe --restore";
            try { File.WriteAllText(HintedFile, DateTime.UtcNow.ToString("o")); } catch (IOException) { }
        }
        return (0, msg);
    }

    /// <summary>
    /// The current lock screen picture's bytes. Its path (OriginalImageFile) is hidden from apps
    /// without package identity, and can point into a cache Windows may clear anyway.
    /// </summary>
    static async Task<byte[]> CurrentPictureAsync()
    {
        using var stream = LockScreen.GetImageStream();
        using var input = stream.AsStreamForRead();
        using var copy = new MemoryStream();
        await input.CopyToAsync(copy);
        return copy.ToArray();
    }

    static string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));

    static Backup ReadBackup() => JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupInfo))!;

    static async Task SaveBackupAsync(byte[] bytes)
    {
        if (bytes.Length == 0) throw new IOException("Windows returned an empty lock screen picture, so there is nothing safe to back up.");
        if (Directory.Exists(BackupDir)) Directory.Delete(BackupDir, recursive: true);   // an older original you've since replaced
        Directory.CreateDirectory(BackupDir);

        var image = Path.Combine(BackupDir, "image" + ImageExtension(bytes));
        await File.WriteAllBytesAsync(image, bytes);

        var info = new Backup(Path.GetFileName(image), LockScreen.OriginalImageFile?.ToString(), SpotlightOn(), DateTime.UtcNow);
        Settings.WriteAtomic(BackupInfo, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>File type from the first bytes: Windows decides how to read the file by its extension.</summary>
    public static string ImageExtension(byte[] b) => b switch
    {
        [0xFF, 0xD8, ..] => ".jpg",
        [0x89, 0x50, 0x4E, 0x47, ..] => ".png",
        [0x42, 0x4D, ..] => ".bmp",
        _ => ".jpg",
    };

    public static async Task<(int Code, string Message)> RestoreAsync()
    {
        if (!HasBackup) return (0, "Nothing to restore: the lock screen was never changed by Lockscreen (or it was already restored).");
        var info = ReadBackup();
        var image = Path.Combine(BackupDir, info.Image);
        if (!File.Exists(image)) return (4, $"The saved original picture is missing ({image}). Nothing was changed.");

        await LockScreen.SetImageFileAsync(await StorageFile.GetFileFromPathAsync(image));
        Directory.Delete(BackupDir, recursive: true);
        try { File.Delete(AppliedPng); } catch (IOException) { }
        return (0, "Your original lock screen picture is back.");
    }
}

static class Settings
{
    /// <summary>Write beside, then swap in, so a crash can't leave half a file.</summary>
    public static void WriteAtomic(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beacon.Core;

namespace Beacon.Assistant;

/// <summary>One thing Nova did, or was stopped from doing.</summary>
/// <param name="Actor">"Nova" when the model decided it, "You" for a command you gave directly.</param>
/// <param name="Risk">The Sentinel tier, in words: "read", "on this PC", "can't be undone", "leaves this PC".</param>
/// <param name="Outcome">"Ran", "Approved by you", "Declined by you" or "Blocked".</param>
/// <param name="Note">Why it was handled that way, e.g. "after reading an email".</param>
public sealed record AuditEntry(DateTime Time, string Actor, string Tool, string Action, string Risk, string Outcome, string Note);

/// <summary>
/// Sentinel's record of every tool call: kept on this PC only, encrypted with Windows DPAPI for
/// the current user (like the API key and email password), and trimmed to the last 30 days.
/// Readable and clearable on Settings > Nova Assistant. Nothing in it ever leaves the PC.
/// </summary>
public sealed class AuditLog(string path)
{
    public static AuditLog Shared { get; } = new(Path.Combine(Settings.Directory, "audit.dat"));

    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    /// <summary>Separates this blob from Beacon's other DPAPI secrets.</summary>
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NovaShield.Sentinel.Audit.v1");

    readonly object _lock = new();
    List<AuditEntry>? _entries;

    /// <summary>Raised after an entry is added or the log is cleared (on the calling thread).</summary>
    public event Action? Changed;

    public void Add(AuditEntry entry)
    {
        lock (_lock)
        {
            Load();
            _entries!.Add(entry);
            Trim();
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Oldest first, last 30 days only.</summary>
    public IReadOnlyList<AuditEntry> Entries()
    {
        lock (_lock)
        {
            Load();
            Trim();
            return _entries!.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries = [];
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    void Trim()
    {
        var cutoff = DateTime.Now - Keep;
        _entries!.RemoveAll(e => e.Time < cutoff);
    }

    void Load()
    {
        if (_entries is not null) return;
        _entries = [];
        try
        {
            if (!File.Exists(path)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            _entries = JsonSerializer.Deserialize<List<AuditEntry>>(plain) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable (another Windows user's file, or damaged): start a fresh log rather than fail.
            _entries = [];
        }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_entries), Entropy, DataProtectionScope.CurrentUser);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // A log that can't be written mustn't stop Nova; the entry stays in memory this session.
        }
    }
}

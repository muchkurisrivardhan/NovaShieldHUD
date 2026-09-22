using System.IO;
using System.Text.Json;

namespace Beacon.Core;

/// <summary>
/// Frequency + recency memory. This is most of what separates a launcher that feels
/// smart from one that feels dumb: after you pick Chrome for "ch" twice, "ch" should
/// stop offering Character Map first.
/// </summary>
public sealed class UsageStats
{
    private sealed record Entry(int Count, DateTimeOffset LastUsed);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly Lock _gate = new();

    public UsageStats(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>Score boost to add to a result, roughly 0-50.</summary>
    public int BoostFor(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e)) return 0;

            // Frequency saturates so a single item can't permanently dominate the list.
            int frequency = (int)(Math.Log2(e.Count + 1) * 10);

            var age = DateTimeOffset.UtcNow - e.LastUsed;
            int recency = age.TotalHours switch
            {
                < 1 => 20,
                < 24 => 12,
                < 24 * 7 => 6,
                < 24 * 30 => 2,
                _ => 0,
            };

            return Math.Min(frequency + recency, 50);
        }
    }

    public void Record(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        lock (_gate)
        {
            var count = _entries.TryGetValue(key, out var e) ? e.Count : 0;
            _entries[key] = new Entry(count + 1, DateTimeOffset.UtcNow);
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json);
            if (data is null) return;

            lock (_gate)
            {
                foreach (var (k, v) in data) _entries[k] = v;
            }
        }
        catch
        {
            // Corrupt or unreadable stats are not worth failing startup over.
        }
    }

    private void Save()
    {
        try
        {
            Dictionary<string, Entry> snapshot;
            lock (_gate) snapshot = new Dictionary<string, Entry>(_entries);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Settings.WriteAtomic(_path, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // Losing usage history is a cosmetic problem, never a fatal one.
        }
    }
}

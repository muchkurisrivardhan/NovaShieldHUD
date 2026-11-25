using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Core;

namespace Beacon.Assistant;

/// <summary>One thing the user asked Nova to remember.</summary>
/// <param name="Private">"This PC only": never shown to a cloud brain.</param>
public sealed record MemoryItem(string Id, string Text, bool Private, DateTime Created);

/// <summary>
/// Nova's long-term memory: facts the user asked it to keep ("my sister is Priya"). Stored on
/// this PC only, encrypted with Windows DPAPI for the current user, like the audit log.
///
/// Nova only adds to it when the user asks (Sentinel enforces that), and the user sees and
/// deletes everything on Settings > Nova Assistant. Memories reach the model labelled as
/// facts the user gave, never as instructions. Private ones are left out for cloud brains.
/// </summary>
public sealed partial class MemoryStore(string path)
{
    public static MemoryStore Shared { get; } = new(Path.Combine(Settings.Directory, "memory.dat"));

    /// <summary>A small model's window is tight: keep the store and each memory short.</summary>
    public const int MaxItems = 200, MaxLength = 300;

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NovaShield.Memory.v1");

    readonly object _lock = new();
    List<MemoryItem>? _items;

    /// <summary>Bumped on every change, so the brain knows when to rebuild its instructions.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after any change (on the calling thread).</summary>
    public event Action? Changed;

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<MemoryItem> All()
    {
        lock (_lock) { Load(); return _items!.ToList(); }
    }

    /// <summary>Adds a memory, or returns why it can't. Returns the item kept.</summary>
    public (MemoryItem? Item, string? Error) Add(string text, bool isPrivate)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return (null, "There was nothing to remember.");
        if (text.Length > MaxLength) return (null, $"That's too long to remember (over {MaxLength} characters). Say it shorter.");
        MemoryItem item;
        lock (_lock)
        {
            Load();
            // The same fact said again: refresh it rather than keep two copies.
            _items!.RemoveAll(m => string.Equals(m.Text, text, StringComparison.OrdinalIgnoreCase));
            if (_items.Count >= MaxItems) return (null, $"My memory is full ({MaxItems} things). Delete some on Settings, Nova Assistant.");
            item = new MemoryItem(Guid.NewGuid().ToString("N")[..12], text, isPrivate, DateTime.Now);
            _items.Add(item);
            Save();
        }
        Changed?.Invoke();
        return (item, null);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_lock)
        {
            Load();
            removed = _items!.RemoveAll(m => m.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void SetPrivate(string id, bool isPrivate)
    {
        lock (_lock)
        {
            Load();
            var i = _items!.FindIndex(m => m.Id == id);
            if (i < 0 || _items[i].Private == isPrivate) return;
            _items[i] = _items[i] with { Private = isPrivate };
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items = [];
            Version++;
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Memories that match a query, best first: by shared words (ignoring tiny ones), so
    /// "where does Priya live" finds "Priya lives in Pune". <paramref name="onThisPc"/> false
    /// leaves out private memories (a cloud brain is asking).
    /// </summary>
    public List<MemoryItem> Search(string query, bool onThisPc, int max = 8)
    {
        if (Words(query).Count == 0) return All().Where(m => onThisPc || !m.Private).Reverse().Take(max).ToList();
        return Score(query, onThisPc).Take(max).Select(x => x.Item).ToList();
    }

    /// <summary>Every memory sharing a word with the query, with how many it shares, best first.</summary>
    public List<(MemoryItem Item, int Score)> Score(string query, bool onThisPc)
    {
        var words = Words(query);
        return All().Where(m => onThisPc || !m.Private)
            .Select(m => (Item: m, Score: Words(m.Text).Count(words.Contains)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.Created)
            .ToList();
    }

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "my", "me", "i", "is", "are", "was", "of", "to", "in", "on", "at", "and", "or", "for",
        "what", "where", "who", "when", "how", "do", "does", "did", "you", "your", "that", "this", "it", "about",
        "remember", "forget", "know", "tell", "please", "with", "from",
    };

    static HashSet<string> Words(string text) =>
        WordRx().Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.Length > 1 && !Stop.Contains(w))
            .Select(w => w.EndsWith('s') && w.Length > 3 ? w[..^1] : w)   // "sisters" ~ "sister"
            .ToHashSet();

    [GeneratedRegex(@"[\p{L}\p{N}@.+-]+")]
    private static partial Regex WordRx();

    void Load()
    {
        if (_items is not null) return;
        _items = [];
        try
        {
            if (!File.Exists(path)) return;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            _items = JsonSerializer.Deserialize<List<MemoryItem>>(plain) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            _items = [];
        }
    }

    void Save()
    {
        Version++;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_items), Entropy, DataProtectionScope.CurrentUser);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, cipher);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Kept in memory for this session even if the disk write failed.
        }
    }
}

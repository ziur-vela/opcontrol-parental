using System.Collections.Concurrent;

namespace OpControl.Parental.Server.Services;

public sealed class LoginThrottle
{
    private sealed class Entry
    {
        public int Count;
        public DateTime WindowStartUtc = DateTime.UtcNow;
    }

    private const int MaxAttempts = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public bool IsBlocked(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            if (DateTime.UtcNow - entry.WindowStartUtc > Window)
            {
                entry.Count = 0;
                entry.WindowStartUtc = DateTime.UtcNow;
            }
            return entry.Count >= MaxAttempts;
        }
    }

    public void RecordFailure(string key)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            if (DateTime.UtcNow - entry.WindowStartUtc > Window)
            {
                entry.Count = 0;
                entry.WindowStartUtc = DateTime.UtcNow;
            }
            entry.Count++;
        }
    }

    public void Reset(string key) => _entries.TryRemove(key, out _);
}

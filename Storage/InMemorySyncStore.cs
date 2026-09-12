using System.Collections.Concurrent;
using Entangle.Model;

namespace Entangle.Storage;

/// <summary>Thread-safe in-memory implementation of <see cref="ISyncStore"/>.</summary>
public sealed class InMemorySyncStore : ISyncStore
{
    private readonly ConcurrentDictionary<string, SyncEntry> _entries;
    private readonly ConcurrentQueue<PendingChange> _pending = new();
    private long _nextPendingId;

    public InMemorySyncStore(bool ignoreCase = false)
    {
        _entries = new ConcurrentDictionary<string, SyncEntry>(
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public IReadOnlyCollection<SyncEntry> GetEntries() => _entries.Values.ToArray();

    public SyncEntry? GetEntry(string relativePath) =>
        _entries.TryGetValue(relativePath, out var entry) ? entry : null;

    public void Upsert(SyncEntry entry) => _entries[entry.Path] = entry;

    public void Remove(string relativePath) => _entries.TryRemove(relativePath, out _);

    public IReadOnlyCollection<PendingChange> GetPendingChanges() => _pending.ToArray();

    public void EnqueueChange(SyncEntry entry) =>
        _pending.Enqueue(new PendingChange(
            Interlocked.Increment(ref _nextPendingId),
            entry.Path,
            entry.Tombstone,
            DateTimeOffset.UtcNow));

    public void ClearPendingChanges() => _pending.Clear();
}

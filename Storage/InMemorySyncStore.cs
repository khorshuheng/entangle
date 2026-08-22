using System.Collections.Concurrent;
using Beam.Model;

namespace Beam.Storage;

/// <summary>Thread-safe in-memory implementation of <see cref="ISyncStore"/>.</summary>
public sealed class InMemorySyncStore : ISyncStore
{
    private readonly ConcurrentDictionary<string, SyncEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PendingChange> _pending = new();
    private long _nextPendingId;

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

using System.Collections.Concurrent;
using Beam.Model;

namespace Beam.Storage;

/// <summary>Thread-safe in-memory implementation of <see cref="ISyncStore"/>.</summary>
public sealed class InMemorySyncStore : ISyncStore
{
    private readonly ConcurrentDictionary<string, SyncEntry> _entries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<SyncEntry> GetEntries() => _entries.Values.ToArray();

    public SyncEntry? GetEntry(string relativePath) =>
        _entries.TryGetValue(relativePath, out var entry) ? entry : null;

    public void Upsert(SyncEntry entry) => _entries[entry.Path] = entry;

    public void Remove(string relativePath) => _entries.TryRemove(relativePath, out _);
}

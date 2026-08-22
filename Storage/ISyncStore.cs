using Beam.Model;

namespace Beam.Storage;

/// <summary>
/// Storage abstraction for local sync state. In-memory implementation is used
/// until persistence lands; a SQLite implementation can be swapped in later.
/// </summary>
public interface ISyncStore
{
    /// <summary>Snapshot of all known entries.</summary>
    IReadOnlyCollection<SyncEntry> GetEntries();

    /// <summary>Fetch a single entry by relative path, or null.</summary>
    SyncEntry? GetEntry(string relativePath);

    /// <summary>Insert or replace the entry for a path.</summary>
    void Upsert(SyncEntry entry);

    /// <summary>Remove any entry for a path.</summary>
    void Remove(string relativePath);
}

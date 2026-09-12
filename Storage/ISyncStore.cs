using Entangle.Model;

namespace Entangle.Storage;

/// <summary>
/// The local sync state: the known entry for each path. Implementations are
/// responsible for their own thread safety, since the filesystem watcher and the
/// sync engine use the store concurrently.
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

namespace Beam.Model;

/// <summary>
/// A local change that has not yet been confirmed synced to the peer. Entries
/// are enqueued as the filesystem watcher detects them and cleared once a full
/// state exchange/reconcile succeeds.
/// </summary>
public sealed record PendingChange(
    long Id,
    string Path,
    bool Tombstone,
    DateTimeOffset CreatedAt);

namespace Entangle.Model;

/// <summary>
/// A single filesystem entry in sync state. Paths are forward-slash relative
/// paths from the synced root (no leading slash). Value equality is used to
/// detect state changes between scans.
/// </summary>
public sealed record SyncEntry(
    string Path,
    EntryType Type,
    DateTimeOffset Mtime,
    bool Tombstone = false,
    string ContentHash = "");

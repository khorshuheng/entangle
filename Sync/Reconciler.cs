using Entangle.Model;

namespace Entangle.Sync;

/// <summary>
/// Compares local and peer state per relative path and produces the list of
/// actions needed to converge. Files are always copied whole; there are no
/// partial updates. Content, not metadata, decides whether a copy is needed.
/// </summary>
public static class Reconciler
{
    /// <summary>
    /// Default value for the tombstone retention window: reclaim a tombstone as
    /// soon as the peer no longer holds the path. Raising the window delays
    /// reclamation, which guards against a peer whose view is transiently
    /// partial. It is not needed for correctness, because a peer that still has
    /// the file reports it as live (which goes through conflict resolution, not
    /// reclamation), while a peer that reports nothing has no file for the
    /// delete to reach.
    /// </summary>
    public static readonly TimeSpan DefaultTombstoneRetention = TimeSpan.Zero;

    public static IReadOnlyList<ReconcileAction> Plan(
        IReadOnlyCollection<SyncEntry> local,
        IReadOnlyCollection<SyncEntry> peer,
        string myPeerId,
        string peerId,
        bool ignoreCase = false,
        TimeSpan? tombstoneRetention = null,
        DateTimeOffset? now = null,
        Action<string, string>? onDuplicatePath = null)
    {
        var comparer = PathUtil.Comparer(ignoreCase);
        var localByPath = PathUtil.IndexByPath(local, comparer, onDuplicatePath);
        var peerByPath = PathUtil.IndexByPath(peer, comparer, onDuplicatePath);
        var retention = tombstoneRetention ?? DefaultTombstoneRetention;
        var at = now ?? DirectoryScanner.UtcNowMs();
        var actions = new List<ReconcileAction>();

        foreach (var path in localByPath.Keys.Union(peerByPath.Keys, comparer))
        {
            var hasLocal = localByPath.TryGetValue(path, out var localEntry);
            var hasPeer = peerByPath.TryGetValue(path, out var peerEntry);

            if (hasLocal && hasPeer)
            {
                if (localEntry!.Tombstone && peerEntry!.Tombstone)
                {
                    // Both sides agree the path is deleted, so the tombstone has
                    // done its job and can be reclaimed on both sides.
                    actions.Add(new ReconcileAction(localEntry, ReconcileActionKind.Remove));
                    continue;
                }

                if (localEntry == peerEntry)
                    continue; // already identical

                // Content is the convergence criterion, not metadata. Matching
                // hashes mean there is nothing to copy even when the mtimes
                // differ, so a metadata-only difference (touch, editor
                // rewrite, restored backup, coarse filesystem timestamps) must
                // never trigger a transfer. Live directories carry no content
                // at all, so presence is likewise all that matters.
                if (!localEntry!.Tombstone && !peerEntry!.Tombstone
                    && localEntry.Type == peerEntry.Type
                    && (localEntry.Type == EntryType.Directory
                        || (localEntry.ContentHash.Length > 0
                            && localEntry.ContentHash == peerEntry.ContentHash)))
                    continue;

                var (winner, kind) = LwwResolver.Resolve(localEntry!, peerEntry!, myPeerId, peerId);
                actions.Add(new ReconcileAction(winner, kind));
            }
            else if (hasLocal)
            {
                var entry = localEntry!;
                if (!entry.Tombstone)
                {
                    actions.Add(new ReconcileAction(entry, ReconcileActionKind.Push));
                    continue;
                }

                // The peer has no entry at all, so there is nothing to delete
                // there and propagating the tombstone would be pointless. Such a
                // tombstone is always safe to reclaim: a peer that still held
                // the file would report it as live (handled above, where LWW
                // decides), so it could never observe this delete anyway. The
                // retention window is an optional extra guard for a peer whose
                // view is transiently partial, and defaults to zero.
                if (at - entry.Mtime >= retention)
                    actions.Add(new ReconcileAction(entry, ReconcileActionKind.Remove));
            }
            else
            {
                var entry = peerEntry!;

                // A peer tombstone with no local entry means the path is already
                // absent here. Pulling it would just recreate a tombstone we may
                // have reclaimed a moment ago, so there is nothing to do.
                if (entry.Tombstone)
                    continue;

                actions.Add(new ReconcileAction(entry, ReconcileActionKind.Pull));
            }
        }

        return actions;
    }
}

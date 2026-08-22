using Beam.Model;

namespace Beam.Sync;

/// <summary>
/// Last-write-wins conflict resolution. The entry with the newer last-modified
/// time wins; equal timestamps are broken deterministically by peer id so both
/// peers reach the same decision. The losing side adopts the winner's content
/// (full overwrite) and timestamp.
/// </summary>
public static class LwwResolver
{
    /// <summary>
    /// Decide which side's entry wins for a path. Returns the winning entry and
    /// the direction the losing side must copy it (Push = local wins, Pull =
    /// peer wins).
    /// </summary>
    public static (SyncEntry Winner, ReconcileActionKind Kind) Resolve(
        SyncEntry local,
        SyncEntry peer,
        string myPeerId,
        string peerId)
    {
        var comparison = local.Mtime.CompareTo(peer.Mtime);
        if (comparison > 0)
            return (local, ReconcileActionKind.Push);
        if (comparison < 0)
            return (peer, ReconcileActionKind.Pull);

        // Equal timestamps: deterministic tie-break. The lexicographically
        // smaller peer id wins, so both peers converge on the same entry.
        var localWins = string.CompareOrdinal(myPeerId, peerId) <= 0;
        return localWins
            ? (local, ReconcileActionKind.Push)
            : (peer, ReconcileActionKind.Pull);
    }
}

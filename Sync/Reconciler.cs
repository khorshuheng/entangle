using Beam.Model;

namespace Beam.Sync;

/// <summary>
/// Compares local and peer state per relative path and produces the list of
/// push/pull actions needed to converge. Files are always copied whole; there
/// are no partial updates.
/// </summary>
public static class Reconciler
{
    public static IReadOnlyList<ReconcileAction> Plan(
        IReadOnlyCollection<SyncEntry> local,
        IReadOnlyCollection<SyncEntry> peer,
        string myPeerId,
        string peerId)
    {
        var localByPath = local.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var peerByPath = peer.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var actions = new List<ReconcileAction>();

        foreach (var path in localByPath.Keys.Union(peerByPath.Keys))
        {
            var hasLocal = localByPath.TryGetValue(path, out var localEntry);
            var hasPeer = peerByPath.TryGetValue(path, out var peerEntry);

            if (hasLocal && hasPeer)
            {
                if (localEntry == peerEntry)
                    continue; // already identical

                var (winner, kind) = LwwResolver.Resolve(localEntry!, peerEntry!, myPeerId, peerId);
                actions.Add(new ReconcileAction(winner, kind));
            }
            else if (hasLocal)
            {
                actions.Add(new ReconcileAction(localEntry!, ReconcileActionKind.Push));
            }
            else
            {
                actions.Add(new ReconcileAction(peerEntry!, ReconcileActionKind.Pull));
            }
        }

        return actions;
    }
}

using Entangle.Model;

namespace Entangle.Sync;

public enum ReconcileActionKind
{
    Push = 1,
    Pull = 2,

    /// <summary>
    /// Drop a local tombstone whose deletion is settled: both sides agree the
    /// path is deleted, or the peer has no record of the path at all and the
    /// tombstone is old enough to rule out a partial peer view.
    /// </summary>
    Remove = 3,
}

/// <summary>
/// A single convergence step: copy <see cref="Source"/> (the winning entry) to
/// the other side. <see cref="Kind"/> says whether that means pushing our copy
/// to the peer or pulling the peer's copy to us.
/// </summary>
public sealed record ReconcileAction(SyncEntry Source, ReconcileActionKind Kind);

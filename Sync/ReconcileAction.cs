using Entangle.Model;

namespace Entangle.Sync;

public enum ReconcileActionKind
{
    None = 0,
    Push = 1,
    Pull = 2,
}

/// <summary>
/// A single convergence step: copy <see cref="Source"/> (the winning entry) to
/// the other side. <see cref="Kind"/> says whether that means pushing our copy
/// to the peer or pulling the peer's copy to us.
/// </summary>
public sealed record ReconcileAction(SyncEntry Source, ReconcileActionKind Kind);

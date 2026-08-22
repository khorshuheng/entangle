using Beam.Model;
using Beam.Sync;

namespace Beam.Tests;

public class ReconcilerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
    private static readonly DateTimeOffset T1 = DateTimeOffset.FromUnixTimeMilliseconds(2_000);

    private static SyncEntry File(string path, DateTimeOffset mtime, string hash) =>
        new(path, EntryType.File, mtime, false, hash);

    private static IReadOnlyList<ReconcileAction> Plan(
        IReadOnlyCollection<SyncEntry> local,
        IReadOnlyCollection<SyncEntry> peer) =>
        Reconciler.Plan(local, peer, "a", "b");

    [Fact]
    public void IdenticalEntriesProduceNoActions()
    {
        var entry = File("x", T0, "h");
        Assert.Empty(Plan([entry], [entry]));
    }

    [Fact]
    public void LocalOnlyEntryIsPushed()
    {
        var actions = Plan([File("x", T0, "h")], []);
        var action = Assert.Single(actions);
        Assert.Equal(ReconcileActionKind.Push, action.Kind);
        Assert.Equal("x", action.Source.Path);
    }

    [Fact]
    public void PeerOnlyEntryIsPulled()
    {
        var actions = Plan([], [File("x", T0, "h")]);
        var action = Assert.Single(actions);
        Assert.Equal(ReconcileActionKind.Pull, action.Kind);
        Assert.Equal("x", action.Source.Path);
    }

    [Fact]
    public void DifferingEntriesUseNewerMtime()
    {
        var actions = Plan([File("x", T0, "local")], [File("x", T1, "peer")]);
        var action = Assert.Single(actions);
        Assert.Equal(ReconcileActionKind.Pull, action.Kind); // peer newer
        Assert.Equal("peer", action.Source.ContentHash);
    }

    [Fact]
    public void BothTombstonesProduceNoActions()
    {
        var localTombstone = new SyncEntry("x", EntryType.File, T0, Tombstone: true);
        var peerTombstone = new SyncEntry("x", EntryType.File, T1, Tombstone: true);
        Assert.Empty(Plan([localTombstone], [peerTombstone]));
    }

    [Fact]
    public void LocalTombstoneDeletesWhenNewerThanPeerLive()
    {
        var localTombstone = new SyncEntry("x", EntryType.File, T1, Tombstone: true);
        var peerLive = File("x", T0, "peer");

        var actions = Plan([localTombstone], [peerLive]);
        var action = Assert.Single(actions);
        Assert.Equal(ReconcileActionKind.Push, action.Kind);
        Assert.True(action.Source.Tombstone);
    }

    [Fact]
    public void PeerTombstoneDeletesWhenNewerThanLocalLive()
    {
        var localLive = File("x", T0, "local");
        var peerTombstone = new SyncEntry("x", EntryType.File, T1, Tombstone: true);

        var actions = Plan([localLive], [peerTombstone]);
        var action = Assert.Single(actions);
        Assert.Equal(ReconcileActionKind.Pull, action.Kind);
        Assert.True(action.Source.Tombstone);
    }

    [Fact]
    public void LiveDirectoriesWithDifferentMtimesProduceNoActions()
    {
        var localDir = new SyncEntry("d", EntryType.Directory, T0);
        var peerDir = new SyncEntry("d", EntryType.Directory, T1);
        Assert.Empty(Plan([localDir], [peerDir]));
    }

    [Fact]
    public void IgnoreCaseTreatsCaseVariantsAsSamePath()
    {
        var actions = Reconciler.Plan(
            [File("Readme.md", T1, "local")],
            [File("readme.md", T0, "peer")],
            "a", "b", ignoreCase: true);

        var action = Assert.Single(actions);
        Assert.Equal(ReconcileActionKind.Push, action.Kind); // local newer wins
    }
}

using Entangle.Model;
using Entangle.Sync;

namespace Entangle.Tests;

public class ReconcilerTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
    private static readonly DateTimeOffset T1 = DateTimeOffset.FromUnixTimeMilliseconds(2_000);

    private static SyncEntry File(string path, DateTimeOffset mtime, string hash) =>
        new(path, EntryType.File, mtime, false, hash);

    private static IReadOnlyList<ReconcileAction> Plan(
        IReadOnlyCollection<SyncEntry> local,
        IReadOnlyCollection<SyncEntry> peer,
        DateTimeOffset? now = null,
        TimeSpan? retention = null) =>
        Reconciler.Plan(local, peer, "a", "b", tombstoneRetention: retention, now: now);

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
    public void SameContentWithDifferentMtimeProducesNoActions()
    {
        // Byte-identical content must not be copied just because the mtimes
        // differ (touch, editor save-without-change, restored backup).
        Assert.Empty(Plan([File("x", T0, "same-hash")], [File("x", T1, "same-hash")]));
    }

    [Fact]
    public void SamePathDifferentContentStillUsesNewerMtime()
    {
        var actions = Plan([File("x", T0, "local-hash")], [File("x", T1, "peer-hash")]);
        var action = Assert.Single(actions);
        Assert.Equal(ReconcileActionKind.Pull, action.Kind);
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
    public void BothTombstonesAreReclaimed()
    {
        // Both sides agree the path is deleted, so the delete has fully
        // propagated and neither side needs to keep the tombstone.
        var localTombstone = new SyncEntry("x", EntryType.File, T0, Tombstone: true);
        var peerTombstone = new SyncEntry("x", EntryType.File, T1, Tombstone: true);
        var action = Assert.Single(Plan([localTombstone], [peerTombstone]));
        Assert.Equal(ReconcileActionKind.Remove, action.Kind);
        Assert.Equal("x", action.Source.Path);
    }

    [Fact]
    public void TombstoneForPathThePeerNeverKnewIsReclaimed()
    {
        // Nothing to delete on the peer, so there is no delete for it to
        // observe and the tombstone has no further purpose.
        var tombstone = new SyncEntry("x", EntryType.File, T0, Tombstone: true);
        var action = Assert.Single(Plan([tombstone], []));
        Assert.Equal(ReconcileActionKind.Remove, action.Kind);
    }

    [Fact]
    public void TombstoneIsKeptWithinConfiguredRetention()
    {
        // Opt-in guard against a peer whose view is transiently partial.
        var tombstone = new SyncEntry("x", EntryType.File, T0, Tombstone: true);
        Assert.Empty(Plan([tombstone], [], now: T0.AddHours(1), retention: TimeSpan.FromDays(7)));
    }

    [Fact]
    public void TombstoneIsReclaimedAfterConfiguredRetention()
    {
        var tombstone = new SyncEntry("x", EntryType.File, T0, Tombstone: true);
        var actions = Plan([tombstone], [], now: T0.AddDays(7).AddSeconds(1), retention: TimeSpan.FromDays(7));
        Assert.Equal(ReconcileActionKind.Remove, Assert.Single(actions).Kind);
    }

    [Fact]
    public void PeerTombstoneWithNoLocalEntryProducesNoAction()
    {
        // Nothing to delete locally, and pulling would only recreate a
        // tombstone this side may already have reclaimed.
        var peerTombstone = new SyncEntry("x", EntryType.File, T1, Tombstone: true);
        Assert.Empty(Plan([], [peerTombstone]));
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
    public void PathsCollidingUnderIgnoreCaseDoNotThrow()
    {
        // A case-sensitive filesystem can legitimately hold both names while
        // case-insensitive matching is configured. That must not abort the pass.
        var collisions = new List<(string First, string Second)>();
        var local = new[]
        {
            File("Readme.md", T1, "local-hash"),
            File("readme.md", T1, "other-hash"),
        };
        var peer = new[] { File("Readme.md", T0, "peer-hash") };

        var actions = Reconciler.Plan(
            local,
            peer,
            "a",
            "b",
            ignoreCase: true,
            onDuplicatePath: (first, second) => collisions.Add((first, second)));

        var collision = Assert.Single(collisions);
        Assert.Equal("Readme.md", collision.First);
        Assert.Equal("readme.md", collision.Second);

        // The first entry wins, so the plan is computed against it.
        var action = Assert.Single(actions);
        Assert.Equal("Readme.md", action.Source.Path);
        Assert.Equal(ReconcileActionKind.Push, action.Kind);
    }

    [Fact]
    public void PathsCollidingUnderIgnoreCaseOnThePeerSideDoNotThrow()
    {
        var collisions = new List<(string First, string Second)>();
        var peer = new[]
        {
            File("Data.bin", T0, "peer-hash"),
            File("data.bin", T0, "other-hash"),
        };

        var actions = Reconciler.Plan(
            [],
            peer,
            "a",
            "b",
            ignoreCase: true,
            onDuplicatePath: (first, second) => collisions.Add((first, second)));

        Assert.Single(collisions);
        Assert.Equal("Data.bin", Assert.Single(actions).Source.Path);
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

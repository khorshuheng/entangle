using Entangle.Model;
using Entangle.Sync;

namespace Entangle.Tests;

public class LwwResolverTests
{
    private static readonly DateTimeOffset Older = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
    private static readonly DateTimeOffset Newer = DateTimeOffset.FromUnixTimeMilliseconds(2_000);

    private static SyncEntry File(string path, DateTimeOffset mtime, string hash = "h") =>
        new(path, EntryType.File, mtime, false, hash);

    [Fact]
    public void NewerLocalEntryWins()
    {
        var (winner, kind) = LwwResolver.Resolve(
            File("a", Newer), File("a", Older), "local", "peer");

        Assert.Equal("h", winner.ContentHash);
        Assert.Equal(Newer, winner.Mtime);
        Assert.Equal(ReconcileActionKind.Push, kind);
    }

    [Fact]
    public void NewerPeerEntryWins()
    {
        var (winner, kind) = LwwResolver.Resolve(
            File("a", Older), File("a", Newer), "local", "peer");

        Assert.Equal(Newer, winner.Mtime);
        Assert.Equal(ReconcileActionKind.Pull, kind);
    }

    [Fact]
    public void EqualMtimeSmallerPeerIdWins()
    {
        // "a" < "b", so the local side wins deterministically on both peers.
        var (winner, kind) = LwwResolver.Resolve(
            File("a", Newer, "local-hash"), File("a", Newer, "peer-hash"), "a", "b");

        Assert.Equal("local-hash", winner.ContentHash);
        Assert.Equal(ReconcileActionKind.Push, kind);
    }

    [Fact]
    public void EqualMtimeLargerPeerIdLoses()
    {
        // local id "b" > peer id "a", so the peer wins on both sides.
        var (winner, kind) = LwwResolver.Resolve(
            File("a", Newer, "local-hash"), File("a", Newer, "peer-hash"), "b", "a");

        Assert.Equal("peer-hash", winner.ContentHash);
        Assert.Equal(ReconcileActionKind.Pull, kind);
    }

    [Fact]
    public void TombstoneWinsWhenNewerThanLiveEntry()
    {
        var live = File("a", Older);
        var tombstone = new SyncEntry("a", EntryType.File, Newer, Tombstone: true);

        var (winner, kind) = LwwResolver.Resolve(tombstone, live, "local", "peer");

        Assert.True(winner.Tombstone);
        Assert.Equal(ReconcileActionKind.Push, kind);
    }
}

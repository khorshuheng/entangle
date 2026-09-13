using System.Text;
using Entangle.Configuration;
using Entangle.Model;
using Entangle.Proto;
using Entangle.Storage;
using Entangle.Sync;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Entangle.Tests;

/// <summary>
/// R5 coverage: the receiving side must record the entry it just wrote rather
/// than waiting for a filesystem watcher event that may never arrive.
/// </summary>
public sealed class PutFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "entangle-put-" + Guid.NewGuid().ToString("N"));

    public PutFileStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (SyncServiceImpl Service, ISyncStore Store) BuildService()
    {
        var db = Path.Combine(_dir, "state.db");
        var options = new EntangleOptions
        {
            SyncDirectory = _dir,
            DatabasePath = db,
            PeerAddress = "http://127.0.0.1:1",
            PeerId = "a",
        };

        var store = new SqliteSyncStore(db);
        var ignore = new IgnoreMatcher(_dir, db, options.EffectiveIgnorePatterns, options.IgnoreCase);
        return (new SyncServiceImpl(options, store, ignore, NullLogger<SyncServiceImpl>.Instance), store);
    }

    [Fact]
    public async Task PutFileRecordsTheEntryItWrote()
    {
        var (service, store) = BuildService();
        var content = Encoding.UTF8.GetBytes("line1\r\nline2\r\n"); // text: hashing normalizes CRLF

        var reply = await service.PutFile(
            new FileContent
            {
                Path = "sub/file.txt",
                Content = ByteString.CopyFrom(content),
                MtimeUnixMs = 1_700_000_000_000,
            },
            null!);

        Assert.True(reply.Accepted);

        var entry = store.GetEntry("sub/file.txt");
        Assert.NotNull(entry);
        Assert.Equal(EntryType.File, entry!.Type);
        Assert.False(entry.Tombstone);
        Assert.Equal(1_700_000_000_000, entry.Mtime.ToUnixTimeMilliseconds());

        // The recorded hash must be the one the scanner computes for the file on
        // disk; otherwise the next pass sees a difference and re-pulls.
        var onDisk = Path.Combine(_dir, "sub", "file.txt");
        Assert.Equal(ContentHasher.HashFile(onDisk), entry.ContentHash);
        Assert.Equal("line1\r\nline2\r\n", await File.ReadAllTextAsync(onDisk));
    }

    [Fact]
    public async Task PutFileRefusesIgnoredPaths()
    {
        // A peer whose configuration differs must not be able to push a path we
        // deliberately exclude onto our disk.
        var (service, store) = BuildService();

        var reply = await service.PutFile(
            new FileContent
            {
                Path = ".git/config",
                Content = ByteString.CopyFromUtf8("git-internals"),
                MtimeUnixMs = 1,
            },
            null!);

        Assert.False(reply.Accepted);
        Assert.False(File.Exists(Path.Combine(_dir, ".git", "config")));
        Assert.Null(store.GetEntry(".git/config"));
    }

    [Fact]
    public async Task PutFileRefusesTheMetadataDatabasePath()
    {
        // Worst case of the same class of bug: a peer that legitimately syncs a
        // file named like our state database would otherwise overwrite it.
        var (service, store) = BuildService();

        var reply = await service.PutFile(
            new FileContent
            {
                Path = "state.db",
                Content = ByteString.CopyFromUtf8("not really a database"),
                MtimeUnixMs = 1,
            },
            null!);

        Assert.False(reply.Accepted);
        Assert.Null(store.GetEntry("state.db"));
    }

    [Fact]
    public async Task PutDirectoryRecordsTheEntry()
    {
        var (service, store) = BuildService();

        await service.PutFile(new FileContent { Path = "empty-dir", IsDirectory = true }, null!);

        var entry = store.GetEntry("empty-dir");
        Assert.NotNull(entry);
        Assert.Equal(EntryType.Directory, entry!.Type);
        Assert.True(Directory.Exists(Path.Combine(_dir, "empty-dir")));
    }

    [Fact]
    public async Task PutTombstoneRecordsTheEntry()
    {
        var (service, store) = BuildService();
        await service.PutFile(
            new FileContent { Path = "gone.txt", Content = ByteString.CopyFromUtf8("x"), MtimeUnixMs = 1 },
            null!);

        await service.PutFile(new FileContent { Path = "gone.txt", Tombstone = true, MtimeUnixMs = 2 }, null!);

        var entry = store.GetEntry("gone.txt");
        Assert.NotNull(entry);
        Assert.True(entry!.Tombstone);
        Assert.False(File.Exists(Path.Combine(_dir, "gone.txt")));
    }
}

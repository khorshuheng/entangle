using System.Net;
using System.Net.Sockets;
using Entangle;
using Entangle.Configuration;
using Entangle.Sync;
using Microsoft.AspNetCore.Builder;
using Entangle.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Entangle.Tests;

/// <summary>
/// Linux-linux integration tests: two in-process Entangle peers on different
/// directories and ports, exercising add/modify/delete propagation, LWW
/// conflict resolution, and offline re-sync.
/// </summary>
public sealed class SyncIntegrationTests : IAsyncLifetime
{
    private readonly string _dirA = Path.Combine(Path.GetTempPath(), "entangle-it-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _dirB = Path.Combine(Path.GetTempPath(), "entangle-it-b-" + Guid.NewGuid().ToString("N"));

    private EntangleOptions _optionsA = null!;
    private EntangleOptions _optionsB = null!;
    private WebApplication _a = null!;
    private WebApplication _b = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dirA);
        Directory.CreateDirectory(_dirB);

        var portA = GetFreePort();
        var portB = GetFreePort();

        _optionsA = Options(_dirA, portA, portB, "a");
        _optionsB = Options(_dirB, portB, portA, "b");

        _a = EntangleApp.Build(_optionsA);
        _b = EntangleApp.Build(_optionsB);

        await _a.StartAsync();
        await _b.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await DisposeAppAsync(_a);
        await DisposeAppAsync(_b);

        TryDelete(_dirA);
        TryDelete(_dirB);
    }

    [Fact]
    public async Task TouchOnlyChangeIsNotTransferred()
    {
        var pathA = Path.Combine(_dirA, "touch.txt");
        var pathB = Path.Combine(_dirB, "touch.txt");
        await File.WriteAllTextAsync(pathA, "touch-content");
        await WaitForAsync(() => File.Exists(pathB));
        await Task.Delay(TimeSpan.FromSeconds(2)); // settle

        var mtimeB = File.GetLastWriteTimeUtc(pathB);
        var pushesBefore = Metrics(_a).Pushes;

        // Metadata-only change on A: content stays byte-identical.
        File.SetLastWriteTimeUtc(pathA, File.GetLastWriteTimeUtc(pathA).AddSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(pushesBefore, Metrics(_a).Pushes);
        Assert.Equal(mtimeB, File.GetLastWriteTimeUtc(pathB));
        Assert.Equal("touch-content", await File.ReadAllTextAsync(pathB));
    }

    [Fact]
    public async Task LargeFilePropagatesOnPush()
    {
        // Larger than gRPC's 4 MiB default, which used to make this fail
        // silently with ResourceExhausted.
        var payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_dirA, "large.bin"), payload);

        var pathB = Path.Combine(_dirB, "large.bin");
        await WaitForAsync(() => File.Exists(pathB) && new FileInfo(pathB).Length == payload.Length, 30);
        Assert.Equal(payload, await File.ReadAllBytesAsync(pathB));
    }

    [Fact]
    public async Task LargeFilePropagatesOnPull()
    {
        // Exercises the client's receive limit: A comes back with no entry for
        // the path and has to pull the whole file from B.
        await _a.StopAsync();
        await _a.DisposeAsync();

        var payload = new byte[5 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(Path.Combine(_dirB, "large-pull.bin"), payload);
        await Task.Delay(TimeSpan.FromSeconds(2)); // let B record it locally

        _a = EntangleApp.Build(_optionsA);
        await _a.StartAsync();

        var pathA = Path.Combine(_dirA, "large-pull.bin");
        await WaitForAsync(() => File.Exists(pathA) && new FileInfo(pathA).Length == payload.Length, 30);
        Assert.Equal(payload, await File.ReadAllBytesAsync(pathA));
    }

    [Fact]
    public async Task DeletionIsReclaimedOnBothSides()
    {
        var pathA = Path.Combine(_dirA, "reclaim.txt");
        var pathB = Path.Combine(_dirB, "reclaim.txt");
        await File.WriteAllTextAsync(pathA, "doomed");
        await WaitForAsync(() => File.Exists(pathB));

        File.Delete(pathA);
        await WaitForAsync(() => !File.Exists(pathB));

        // Once both sides agree the path is deleted the tombstone is reclaimed,
        // so settled deletions do not accumulate forever.
        await WaitForAsync(() =>
            Store(_a).GetEntry("reclaim.txt") is null && Store(_b).GetEntry("reclaim.txt") is null);

        Assert.True(Metrics(_a).EntriesRemoved > 0 || Metrics(_b).EntriesRemoved > 0);

        // And the file must not come back.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(pathA));
        Assert.False(File.Exists(pathB));
    }

    [Fact]
    public async Task IgnoredPathsAreNotSynced()
    {
        Directory.CreateDirectory(Path.Combine(_dirA, ".git"));
        Directory.CreateDirectory(Path.Combine(_dirA, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_dirA, ".git", "config"), "git-internals");
        await File.WriteAllTextAsync(Path.Combine(_dirA, "node_modules", "dep.js"), "dep");
        await File.WriteAllTextAsync(Path.Combine(_dirA, "keep.txt"), "keep");

        await WaitForAsync(() => File.Exists(Path.Combine(_dirB, "keep.txt")));
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(Directory.Exists(Path.Combine(_dirB, ".git")));
        Assert.False(Directory.Exists(Path.Combine(_dirB, "node_modules")));
        Assert.Null(Store(_a).GetEntry(".git/config"));
        Assert.Null(Store(_a).GetEntry(".git"));
        Assert.Null(Store(_b).GetEntry("node_modules/dep.js"));
    }

    [Fact]
    public async Task SymlinksAreNotSynced()
    {
        var outside = Path.Combine(Path.GetTempPath(), "entangle-link-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(outside, "linked content");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_dirA, "link.txt"), outside);
            await File.WriteAllTextAsync(Path.Combine(_dirA, "real.txt"), "real");

            await WaitForAsync(() => File.Exists(Path.Combine(_dirB, "real.txt")));
            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.False(File.Exists(Path.Combine(_dirB, "link.txt")));
            Assert.Null(Store(_a).GetEntry("link.txt"));
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public async Task AddModifyDeletePropagate()
    {
        // Add
        await File.WriteAllTextAsync(Path.Combine(_dirA, "hello.txt"), "v1");
        await WaitForAsync(() =>
            File.Exists(Path.Combine(_dirB, "hello.txt"))
            && File.ReadAllText(Path.Combine(_dirB, "hello.txt")) == "v1");

        // Modify
        await File.WriteAllTextAsync(Path.Combine(_dirA, "hello.txt"), "v2");
        await WaitForAsync(() =>
            File.Exists(Path.Combine(_dirB, "hello.txt"))
            && File.ReadAllText(Path.Combine(_dirB, "hello.txt")) == "v2");

        // Delete
        File.Delete(Path.Combine(_dirA, "hello.txt"));
        await WaitForAsync(() => !File.Exists(Path.Combine(_dirB, "hello.txt")));
    }

    [Fact]
    public async Task LastWriteWinsConflictResolution()
    {
        await File.WriteAllTextAsync(Path.Combine(_dirA, "conflict.txt"), "old");
        await WaitForAsync(() => File.Exists(Path.Combine(_dirB, "conflict.txt")));

        // Modify on B afterwards so B has the newer mtime.
        await File.WriteAllTextAsync(Path.Combine(_dirB, "conflict.txt"), "new");

        await WaitForAsync(() =>
            File.Exists(Path.Combine(_dirA, "conflict.txt"))
            && File.ReadAllText(Path.Combine(_dirA, "conflict.txt")) == "new");

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_dirB, "conflict.txt")));
    }

    [Fact]
    public async Task OfflineResync()
    {
        // Take B offline.
        await _b.StopAsync();
        await _b.DisposeAsync();

        // Make changes on A while B is down.
        await File.WriteAllTextAsync(Path.Combine(_dirA, "offline.txt"), "offline");
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Bring B back and verify it converges.
        _b = EntangleApp.Build(_optionsB);
        await _b.StartAsync();

        await WaitForAsync(() =>
            File.Exists(Path.Combine(_dirB, "offline.txt"))
            && File.ReadAllText(Path.Combine(_dirB, "offline.txt")) == "offline");
    }

    private static SyncMetrics Metrics(WebApplication app) =>
        app.Services.GetRequiredService<SyncMetrics>();

    private static ISyncStore Store(WebApplication app) =>
        app.Services.GetRequiredService<ISyncStore>();

    private static EntangleOptions Options(string dir, int port, int peerPort, string peerId) => new()
    {
        SyncDirectory = dir,
        DatabasePath = Path.Combine(dir, "entangle.db"), // inside the sync dir to exercise ignore logic
        Port = port,
        PeerAddress = $"http://127.0.0.1:{peerPort}",
        PeerId = peerId,
        SyncIntervalSeconds = 1,
        RescanIntervalSeconds = 1,
        MaxBackoffSeconds = 2,
    };

    private static async Task DisposeAppAsync(WebApplication? app)
    {
        if (app is null)
            return;

        try
        {
            await app.StopAsync();
        }
        catch
        {
            // already stopped
        }

        await app.DisposeAsync();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (condition())
                    return;
            }
            catch
            {
                // transient (file not ready); keep polling
            }

            await Task.Delay(200);
        }

        Assert.Fail("Condition was not met within the timeout.");
    }
}

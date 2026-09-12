using System.Net;
using System.Net.Sockets;
using Entangle;
using Entangle.Configuration;
using Entangle.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Entangle.Tests;

/// <summary>
/// R1 regression coverage: two locations that already hold byte-identical
/// content with different timestamps (a restored backup, a copy made with
/// `cp -p`, a pre-seeded remote, coarse filesystem timestamps) must converge
/// without transferring anything at all. The directories are populated before
/// the peers start, so there is no write-ordering race that would legitimately
/// cause a first-time copy.
/// </summary>
public sealed class PrePopulatedSyncTests : IAsyncLifetime
{
    private readonly string _dirA = Path.Combine(Path.GetTempPath(), "entangle-pre-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _dirB = Path.Combine(Path.GetTempPath(), "entangle-pre-b-" + Guid.NewGuid().ToString("N"));
    private WebApplication _a = null!;
    private WebApplication _b = null!;

    private static readonly DateTimeOffset OlderTime = DateTimeOffset.UtcNow.AddDays(-3);
    private static readonly DateTimeOffset NewerTime = DateTimeOffset.UtcNow.AddDays(-1);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_dirA, "nested"));
        Directory.CreateDirectory(Path.Combine(_dirB, "nested"));

        // Byte-identical trees, deliberately different mtimes on every entry.
        foreach (var (dir, stamp) in new[] { (_dirA, NewerTime), (_dirB, OlderTime) })
        {
            Write(Path.Combine(dir, "readme.md"), "same content\n", stamp);
            Write(Path.Combine(dir, "nested", "data.bin"), "binary\0payload", stamp.AddHours(1));
            Directory.SetLastWriteTimeUtc(Path.Combine(dir, "nested"), stamp.UtcDateTime);
            Directory.SetLastWriteTimeUtc(dir, stamp.UtcDateTime);
        }

        var portA = GetFreePort();
        var portB = GetFreePort();

        _a = EntangleApp.Build(Options(_dirA, portA, portB, "a"));
        _b = EntangleApp.Build(Options(_dirB, portB, portA, "b"));

        return Task.WhenAll(_a.StartAsync(), _b.StartAsync());
    }

    public async Task DisposeAsync()
    {
        foreach (var app in new[] { _a, _b })
        {
            try { await app.StopAsync(); } catch { }
            await app.DisposeAsync();
        }

        TryDelete(_dirA);
        TryDelete(_dirB);
    }

    [Fact]
    public async Task PrePopulatedIdenticalTreesTransferNothing()
    {
        await Task.Delay(TimeSpan.FromSeconds(4)); // several reconcile + rescan cycles

        Assert.Equal(0, Metrics(_a).Transfers);
        Assert.Equal(0, Metrics(_b).Transfers);
        Assert.Equal(0, Metrics(_a).BytesPushed + Metrics(_b).BytesPushed);

        // Nothing was overwritten, so the original timestamps survive.
        Assert.Equal(NewerTime.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(_dirA, "readme.md")));
        Assert.Equal(OlderTime.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(_dirB, "readme.md")));
        Assert.Equal("same content\n", await File.ReadAllTextAsync(Path.Combine(_dirB, "readme.md")));
        Assert.Equal(NewerTime.AddHours(1).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(_dirA, "nested", "data.bin")));
        Assert.Equal(OlderTime.AddHours(1).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(_dirB, "nested", "data.bin")));
    }

    private static void Write(string path, string content, DateTimeOffset mtime)
    {
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, mtime.UtcDateTime);
    }

    private static SyncMetrics Metrics(WebApplication app) =>
        app.Services.GetRequiredService<SyncMetrics>();

    private static EntangleOptions Options(string dir, int port, int peerPort, string peerId) => new()
    {
        SyncDirectory = dir,
        DatabasePath = Path.Combine(dir, "entangle.db"),
        Port = port,
        PeerAddress = $"http://127.0.0.1:{peerPort}",
        PeerId = peerId,
        SyncIntervalSeconds = 1,
        RescanIntervalSeconds = 1,
        MaxBackoffSeconds = 2,
    };

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

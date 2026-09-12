using System.Net;
using System.Net.Sockets;
using Entangle;
using Entangle.Configuration;
using Microsoft.AspNetCore.Builder;

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

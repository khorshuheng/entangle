using System.Text;
using Entangle.Model;
using Entangle.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Entangle.Tests;

/// <summary>R4 coverage: writes replace a file atomically, never in place.</summary>
public sealed class AtomicWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "entangle-atomic-" + Guid.NewGuid().ToString("N"));

    public AtomicWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ConcurrentReaderNeverSeesPartialContent()
    {
        var target = Path.Combine(_dir, "file.bin");
        var small = Encoding.UTF8.GetBytes("small");
        var large = new byte[256 * 1024];
        Random.Shared.NextBytes(large);

        await PathUtil.WriteAllBytesAtomicAsync(target, small, DateTimeOffset.UtcNow);

        var stop = false;
        var lengths = new List<int>();
        var reader = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(target);
                    lock (lengths)
                        lengths.Add(bytes.Length);
                }
                catch (IOException)
                {
                    // Acceptable: the path was momentarily unavailable.
                }
            }
        });

        for (var i = 0; i < 60; i++)
            await PathUtil.WriteAllBytesAtomicAsync(target, i % 2 == 0 ? large : small, DateTimeOffset.UtcNow);

        Volatile.Write(ref stop, true);
        await reader;

        lock (lengths)
        {
            Assert.NotEmpty(lengths);
            Assert.All(
                lengths,
                length => Assert.True(
                    length == small.Length || length == large.Length,
                    $"observed a partially written file of {length} bytes"));
        }
    }

    [Fact]
    public async Task AtomicWriteLeavesNoTempArtifacts()
    {
        var target = Path.Combine(_dir, "nested", "file.txt");
        await PathUtil.WriteAllBytesAtomicAsync(target, Encoding.UTF8.GetBytes("content"), DateTimeOffset.UtcNow);

        Assert.Equal("content", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.GetFiles(_dir, "*" + PathUtil.TempSuffix + "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ScannerIgnoresTempArtifacts()
    {
        File.WriteAllText(Path.Combine(_dir, "real.txt"), "real");
        File.WriteAllText(Path.Combine(_dir, "real.txt" + PathUtil.TempSuffix + "-deadbeef"), "in flight");

        var scanner = new DirectoryScanner(NullLogger<DirectoryScanner>.Instance);
        var entries = scanner.Scan(_dir);

        var entry = Assert.Single(entries);
        Assert.Equal("real.txt", entry.Path);
    }
}

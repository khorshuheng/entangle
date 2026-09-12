using Entangle.Model;
using Entangle.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Entangle.Tests;

/// <summary>R8 coverage: symlinks are neither traversed nor synced.</summary>
public sealed class DirectoryScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "entangle-scan-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "entangle-outside-" + Guid.NewGuid().ToString("N"));
    private readonly DirectoryScanner _scanner = new(NullLogger<DirectoryScanner>.Instance);

    public DirectoryScannerTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outside })
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ScannerReturnsRegularEntries()
    {
        File.WriteAllText(Path.Combine(_root, "file.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "nested.txt"), "nested");

        var paths = _scanner.Scan(_root).Select(e => e.Path).OrderBy(p => p).ToArray();

        Assert.Equal(new[] { "file.txt", "sub", "sub/nested.txt" }, paths);
    }

    [Fact]
    public void ScannerSkipsFileAndDirectorySymlinks()
    {
        File.WriteAllText(Path.Combine(_outside, "target.txt"), "outside");
        Directory.CreateDirectory(Path.Combine(_outside, "target-dir"));
        File.WriteAllText(Path.Combine(_outside, "target-dir", "deep.txt"), "deep");
        File.WriteAllText(Path.Combine(_root, "real.txt"), "real");

        File.CreateSymbolicLink(Path.Combine(_root, "file-link.txt"), Path.Combine(_outside, "target.txt"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "dir-link"), Path.Combine(_outside, "target-dir"));

        var paths = _scanner.Scan(_root).Select(e => e.Path).ToArray();

        Assert.Equal(["real.txt"], paths);
    }

    [Fact]
    public void SelfReferentialDirectorySymlinkDoesNotRecurse()
    {
        File.WriteAllText(Path.Combine(_root, "real.txt"), "real");
        Directory.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);

        // A followed link here would recurse indefinitely.
        var paths = _scanner.Scan(_root).Select(e => e.Path).ToArray();

        Assert.Equal(["real.txt"], paths);
    }

    [Fact]
    public void ScanSingleIgnoresSymlinks()
    {
        File.WriteAllText(Path.Combine(_outside, "target.txt"), "outside");
        var link = Path.Combine(_root, "link.txt");
        File.CreateSymbolicLink(link, Path.Combine(_outside, "target.txt"));

        Assert.Null(_scanner.ScanSingle(link, _root));
        Assert.Null(_scanner.ScanSingle(Path.Combine(_root, "link.txt" + PathUtil.TempSuffix + "-x"), _root));
    }

    [Fact]
    public void ScanSingleReturnsEntryForRegularFile()
    {
        var path = Path.Combine(_root, "plain.txt");
        File.WriteAllText(path, "plain");

        var entry = _scanner.ScanSingle(path, _root);
        Assert.NotNull(entry);
        Assert.Equal("plain.txt", entry!.Path);
        Assert.Equal(EntryType.File, entry.Type);
    }
}

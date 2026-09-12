using Entangle.Model;

namespace Entangle.Sync;

/// <summary>Helpers for resolving sync-relative paths safely within a root.</summary>
public static class PathUtil
{
    /// <summary>
    /// Suffix used for the temporary file of an atomic write. Paths carrying it
    /// are always excluded from sync state, so the write-then-rename dance never
    /// becomes an entry that would be propagated to the peer.
    /// </summary>
    public const string TempSuffix = ".entangle-tmp";

    /// <summary>True when the path names a temporary artifact of an atomic write.</summary>
    public static bool IsTempArtifact(string path)
        => Path.GetFileName(path).Contains(TempSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Write <paramref name="content"/> to <paramref name="fullPath"/> atomically:
    /// a sibling temporary file is written (with the final mtime applied) and
    /// then moved over the destination. A concurrent reader therefore observes
    /// either the previous content or the complete new content, never a
    /// partially written file.
    /// </summary>
    public static async Task WriteAllBytesAtomicAsync(
        string fullPath,
        byte[] content,
        DateTimeOffset? mtime = null,
        CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Path has no parent directory: {fullPath}");
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(
            directory,
            Path.GetFileName(fullPath) + TempSuffix + "-" + Guid.NewGuid().ToString("N"));

        try
        {
            await File.WriteAllBytesAsync(temp, content, ct);
            if (mtime is { } value)
                File.SetLastWriteTimeUtc(temp, value.UtcDateTime);

            File.Move(temp, fullPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file is ignored by the scanner anyway.
        }
    }

    /// <summary>String comparer for sync paths under the given case-sensitivity.</summary>
    public static StringComparer Comparer(bool ignoreCase) =>
        ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Index entries by path under <paramref name="comparer"/>, tolerating paths
    /// that collide under it. Collisions happen when a case-sensitive filesystem
    /// legitimately holds both "Foo" and "foo" while case-insensitive matching
    /// is configured, or when the store's collation no longer matches the active
    /// setting. The first entry wins and the collision is reported, rather than
    /// throwing: an exception here would abort an entire reconcile pass.
    /// </summary>
    public static Dictionary<string, SyncEntry> IndexByPath(
        IEnumerable<SyncEntry> entries,
        StringComparer comparer,
        Action<string, string>? onDuplicate = null)
    {
        var map = new Dictionary<string, SyncEntry>(comparer);
        foreach (var entry in entries)
        {
            if (map.TryGetValue(entry.Path, out var existing))
            {
                onDuplicate?.Invoke(existing.Path, entry.Path);
                continue;
            }

            map[entry.Path] = entry;
        }

        return map;
    }

    /// <summary>String comparison for sync paths under the given case-sensitivity.</summary>
    public static StringComparison Comparison(bool ignoreCase) =>
        ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Resolve a forward-slash sync-relative path against <paramref name="root"/>,
    /// rejecting any path that escapes the root (absolute paths or "..").
    /// </summary>
    public static string ResolveWithinRoot(string root, string relativePath, bool ignoreCase = false)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var fullRoot = Path.GetFullPath(root);
        var comparison = Comparison(ignoreCase);

        if (!full.Equals(fullRoot, comparison)
            && !full.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException($"Path escapes sync root: {relativePath}");
        }

        return full;
    }

    /// <summary>
    /// Delete a file (or directory, recursively), then prune any now-empty
    /// parent directories up to, but not including, the sync root.
    /// </summary>
    public static void DeletePathAndPruneEmptyParents(string root, string fullPath, bool ignoreCase = false)
    {
        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
        else if (File.Exists(fullPath))
            File.Delete(fullPath);

        PruneEmptyParents(root, fullPath, ignoreCase);
    }

    /// <summary>Remove now-empty parent directories of <paramref name="fullPath"/> up to the root.</summary>
    public static void PruneEmptyParents(string root, string fullPath, bool ignoreCase = false)
    {
        var rootFull = Path.GetFullPath(root);
        var comparison = Comparison(ignoreCase);
        var dir = Path.GetDirectoryName(fullPath);

        while (dir is not null
               && !Path.GetFullPath(dir).Equals(rootFull, comparison)
               && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }
}

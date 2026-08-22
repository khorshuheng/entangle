namespace Beam.Sync;

/// <summary>Helpers for resolving sync-relative paths safely within a root.</summary>
public static class PathUtil
{
    /// <summary>
    /// Resolve a forward-slash sync-relative path against <paramref name="root"/>,
    /// rejecting any path that escapes the root (absolute paths or "..").
    /// </summary>
    public static string ResolveWithinRoot(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var fullRoot = Path.GetFullPath(root);

        if (!full.Equals(fullRoot, StringComparison.Ordinal)
            && !full.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path escapes sync root: {relativePath}");
        }

        return full;
    }

    /// <summary>
    /// Delete a file (or directory, recursively), then prune any now-empty
    /// parent directories up to, but not including, the sync root.
    /// </summary>
    public static void DeletePathAndPruneEmptyParents(string root, string fullPath)
    {
        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
        else if (File.Exists(fullPath))
            File.Delete(fullPath);

        PruneEmptyParents(root, fullPath);
    }

    /// <summary>Remove now-empty parent directories of <paramref name="fullPath"/> up to the root.</summary>
    public static void PruneEmptyParents(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root);
        var dir = Path.GetDirectoryName(fullPath);

        while (dir is not null
               && !Path.GetFullPath(dir).Equals(rootFull, StringComparison.Ordinal)
               && Directory.Exists(dir)
               && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }
}

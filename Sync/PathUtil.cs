namespace Beam.Sync;

/// <summary>Helpers for resolving sync-relative paths safely within a root.</summary>
public static class PathUtil
{
    /// <summary>String comparer for sync paths under the given case-sensitivity.</summary>
    public static StringComparer Comparer(bool ignoreCase) =>
        ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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

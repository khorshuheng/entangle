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
}

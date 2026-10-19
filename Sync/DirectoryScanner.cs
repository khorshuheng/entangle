using Entangle.Model;

namespace Entangle.Sync;

/// <summary>
/// Walks the synced directory and produces <see cref="SyncEntry"/> records with
/// forward-slash relative paths, skipping configured ignored paths (the local
/// metadata database and its sidecar files).
/// </summary>
public sealed class DirectoryScanner
{
    private readonly ILogger<DirectoryScanner> _logger;

    public DirectoryScanner(ILogger<DirectoryScanner> logger) => _logger = logger;

    /// <summary>Recursively scan a root directory.</summary>
    public ScanResult Scan(string root, IgnoreMatcher? ignore = null)
    {
        var fullRoot = Path.GetFullPath(root);
        var matcher = ignore ?? new IgnoreMatcher(fullRoot);
        var result = new List<SyncEntry>();
        var unreadable = new List<string>();
        Walk(fullRoot, fullRoot, string.Empty, matcher, result, unreadable);
        return new ScanResult(result, unreadable);
    }

    /// <summary>
    /// Produce the entry for a single changed path, or null if it is ignored,
    /// the root itself, or no longer exists.
    /// </summary>
    public SyncEntry? ScanSingle(string fullPath, string root, IgnoreMatcher? ignore = null)
    {
        var full = Path.GetFullPath(fullPath);
        var fullRoot = Path.GetFullPath(root);
        var matcher = ignore ?? new IgnoreMatcher(fullRoot);
        if (matcher.IsIgnored(full) || IsSymlink(full))
            return null;

        var relative = ToRelative(fullRoot, full);
        if (relative.Length == 0)
            return null; // the root directory itself

        if (Directory.Exists(full))
            return new SyncEntry(relative, EntryType.Directory, MtimeOfDirectory(full));

        if (File.Exists(full))
            return new SyncEntry(relative, EntryType.File, MtimeOfFile(full), false, ContentHasher.HashFile(full));

        return null;
    }

    private void Walk(
        string root,
        string current,
        string relative,
        IgnoreMatcher ignore,
        List<SyncEntry> result,
        List<string> unreadable)
    {
        string[] dirs;
        string[] files;
        try
        {
            dirs = Directory.GetDirectories(current);
            files = Directory.GetFiles(current);
        }
        catch (Exception ex)
        {
            // The directory is there but cannot be listed (a lock or a
            // permission denial). Its children are unknown, not gone, so record
            // the whole subtree as unreadable: reporting them as absent would
            // turn a transient access failure into a propagated deletion.
            _logger.LogDebug(ex, "Cannot enumerate {Directory}; keeping its previous state", current);
            unreadable.Add(relative);
            return;
        }

        foreach (var dir in dirs)
        {
            var full = Path.GetFullPath(dir);
            if (ignore.IsIgnored(full) || IsSymlink(full))
                continue;

            var childRelative = ToRelative(root, full);
            result.Add(new SyncEntry(childRelative, EntryType.Directory, MtimeOfDirectory(full)));
            Walk(root, full, childRelative, ignore, result, unreadable);
        }

        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            if (ignore.IsIgnored(full) || IsSymlink(full))
                continue;

            try
            {
                result.Add(new SyncEntry(ToRelative(root, full), EntryType.File, MtimeOfFile(full), false, ContentHasher.HashFile(full)));
            }
            catch (Exception ex)
            {
                // A file held open without sharing (or one this user cannot
                // read) is unreadable, not deleted. Report it so the caller
                // keeps the state it already had.
                _logger.LogDebug(ex, "Cannot read {File}; keeping its previous state", full);
                unreadable.Add(ToRelative(root, full));
            }
        }
    }

    /// <summary>
    /// Result of a scan: the entries that could be read, together with the
    /// paths whose state could not be determined because they were unreadable
    /// (locked or permission-denied).
    /// </summary>
    public sealed record ScanResult(
        IReadOnlyList<SyncEntry> Entries,
        IReadOnlyCollection<string> UnreadablePaths)
    {
        /// <summary>
        /// True when <paramref name="relativePath"/> is, or lies under, a path
        /// the scan could not read. Its absence from <see cref="Entries"/> then
        /// says nothing about whether it still exists. The empty path marks a
        /// root whose own contents could not be enumerated.
        /// </summary>
        public bool IsUnreadable(string relativePath, StringComparison comparison)
        {
            foreach (var unreadable in UnreadablePaths)
            {
                if (unreadable.Length == 0
                    || relativePath.Equals(unreadable, comparison)
                    || relativePath.StartsWith(unreadable + "/", comparison))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Symlinks are neither followed nor synced. A directory link could recurse
    /// forever or drag in an unrelated tree, and link identity is not something
    /// the wire protocol carries, so pretending they are ordinary entries would
    /// let the two peers disagree about the same path. Skipping is the policy.
    /// </summary>
    internal static bool IsSymlink(string fullPath)
    {
        try
        {
            return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            // Vanished or unreadable: not a symlink, and the existence checks
            // that follow decide what to do with it.
            return false;
        }
    }

    internal static string ToRelative(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    // Mtimes are normalized to millisecond precision so they compare equal on
    // both peers (the wire format carries Unix milliseconds, and the local
    // filesystem may report finer-grained timestamps).
    internal static DateTimeOffset MtimeOfFile(string fullPath)
        => TruncateToMs(new DateTimeOffset(File.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero));

    internal static DateTimeOffset MtimeOfDirectory(string fullPath)
        => TruncateToMs(new DateTimeOffset(Directory.GetLastWriteTimeUtc(fullPath), TimeSpan.Zero));

    private static DateTimeOffset TruncateToMs(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    /// <summary>Current UTC time truncated to millisecond precision.</summary>
    public static DateTimeOffset UtcNowMs() => TruncateToMs(DateTimeOffset.UtcNow);
}

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
    public IReadOnlyList<SyncEntry> Scan(string root, IgnoreMatcher? ignore = null)
    {
        var fullRoot = Path.GetFullPath(root);
        var matcher = ignore ?? new IgnoreMatcher(fullRoot);
        var result = new List<SyncEntry>();
        Walk(fullRoot, fullRoot, matcher, result);
        return result;
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

    private void Walk(string root, string current, IgnoreMatcher ignore, List<SyncEntry> result)
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
            _logger.LogError(ex, "Failed to enumerate directory {Directory}", current);
            return;
        }

        foreach (var dir in dirs)
        {
            var full = Path.GetFullPath(dir);
            if (ignore.IsIgnored(full) || IsSymlink(full))
                continue;

            result.Add(new SyncEntry(ToRelative(root, full), EntryType.Directory, MtimeOfDirectory(full)));
            Walk(root, full, ignore, result);
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
                _logger.LogWarning(ex, "Skipping unreadable file {File}", full);
            }
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

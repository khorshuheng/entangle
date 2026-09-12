using System.Text;
using System.Text.RegularExpressions;

namespace Entangle.Sync;

/// <summary>
/// Decides which paths are excluded from sync. There are three sources: exact
/// absolute paths (the metadata database and its SQLite sidecars), user
/// configured glob patterns matched against sync-relative paths, and the
/// temporary artifacts written by atomic file replacement.
/// <para>
/// An ignore pattern is local policy: an ignored path is never scanned, never
/// recorded as an entry, and never reconciled, but it is never interpreted as a
/// deletion of the peer's copy either.
/// </para>
/// </summary>
public sealed class IgnoreMatcher
{
    private readonly string _root;
    private readonly HashSet<string> _exactPaths;
    private readonly List<string> _configuredPatterns;
    private readonly List<Regex> _patterns;

    public IgnoreMatcher(
        string root,
        string? databasePath = null,
        IEnumerable<string>? patterns = null,
        bool ignoreCase = false)
    {
        _root = Path.GetFullPath(root);
        _exactPaths = new HashSet<string>(
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            var database = Path.GetFullPath(databasePath);
            foreach (var path in new[] { database, database + "-wal", database + "-shm", database + "-journal" })
                _exactPaths.Add(path);
        }

        _configuredPatterns = (patterns ?? [])
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToList();

        var regexOptions = RegexOptions.CultureInvariant
                           | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

        _patterns = _configuredPatterns
            .Select(pattern => new Regex(Translate(pattern), regexOptions))
            .ToList();
    }

    /// <summary>The configured patterns, for diagnostics.</summary>
    public IReadOnlyList<string> Patterns => _configuredPatterns;

    /// <summary>True when the absolute path is excluded from sync.</summary>
    public bool IsIgnored(string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (_exactPaths.Contains(full))
            return true;

        var relative = Path.GetRelativePath(_root, full);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return true; // outside the sync root: not ours to sync

        return IsIgnoredRelative(relative);
    }

    /// <summary>True when the sync-relative path is excluded from sync.</summary>
    public bool IsIgnoredRelative(string relativePath)
    {
        if (PathUtil.IsTempArtifact(relativePath))
            return true;

        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(normalized))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Full exclusion check for a sync-relative path, including the exact paths
    /// (the metadata database and its sidecars) that are not expressible as
    /// patterns. Use this rather than <see cref="IsIgnoredRelative"/> whenever a
    /// relative path is about to be acted on, so the state database can never be
    /// overwritten by a peer that happens to sync a file of the same name.
    /// </summary>
    public bool IsIgnoredEntry(string relativePath)
        => IsIgnored(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Translate a glob into a regex over sync-relative paths:
    /// <list type="bullet">
    /// <item><c>node_modules/</c> or <c>node_modules</c> — that name at any depth, plus everything under it</item>
    /// <item><c>*.log</c> — matching names at any depth</item>
    /// <item><c>build/output</c> — matched from the root, since it contains a separator</item>
    /// <item><c>/vendor</c> — explicitly anchored at the root</item>
    /// <item><c>**</c> — any number of segments; <c>*</c> stays within one segment</item>
    /// </list>
    /// </summary>
    private static string Translate(string pattern)
    {
        var trimmed = pattern.Trim();
        var rooted = trimmed.StartsWith('/');
        var body = GlobBody(trimmed.Trim('/'));
        var containsSeparator = trimmed.Trim('/').Contains('/');

        // A pattern with a separator is anchored to the root; a bare name may
        // match a segment at any depth.
        var expression = rooted || containsSeparator
            ? $"^{body}($|/.*)$"
            : $"^(?:.*/)?{body}($|/.*)$";

        return expression;
    }

    private static string GlobBody(string glob)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        builder.Append("(?:.*/)?"); // "**/" also matches zero segments
                    }
                    else
                    {
                        builder.Append(".*");
                    }

                    break;
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }
}

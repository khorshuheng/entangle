using Entangle.Sync;

namespace Entangle.Tests;

/// <summary>R7 coverage: which paths the ignore matcher excludes from sync.</summary>
public class IgnoreMatcherTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "entangle-ignore-root"));
    private static readonly string Db = Path.Combine(Root, "entangle.db");

    private static IgnoreMatcher Matcher(params string[] patterns) => new(Root, Db, patterns);

    [Theory]
    [InlineData(".git/config", true)]
    [InlineData("nested/.git/config", true)]
    [InlineData(".git", true)]
    [InlineData("node_modules/left-pad/index.js", true)]
    [InlineData("a/b/node_modules/x", true)]
    [InlineData("src/main.cs", false)]
    [InlineData("gitignore.txt", false)]
    [InlineData("mynode_modules/x", false)]
    public void DirectoryNamePatternMatchesAtAnyDepth(string relative, bool ignored)
    {
        var matcher = Matcher(".git/", "node_modules/");
        Assert.Equal(ignored, matcher.IsIgnoredRelative(relative));
    }

    [Theory]
    [InlineData("a.log", true)]
    [InlineData("deep/nested/b.log", true)]
    [InlineData("a.log.keep", false)]
    [InlineData("alog", false)]
    public void WildcardPatternMatchesFileNames(string relative, bool ignored)
    {
        var matcher = Matcher("*.log");
        Assert.Equal(ignored, matcher.IsIgnoredRelative(relative));
    }

    [Theory]
    [InlineData("build/output/bin.dll", true)]
    [InlineData("build/output", true)]
    [InlineData("sub/build/output/bin.dll", false)] // contains a separator: anchored at the root
    [InlineData("build/other/bin.dll", false)]
    public void PatternWithSeparatorIsRootAnchored(string relative, bool ignored)
    {
        var matcher = Matcher("build/output");
        Assert.Equal(ignored, matcher.IsIgnoredRelative(relative));
    }

    [Theory]
    [InlineData("vendor/lib.cs", true)]
    [InlineData("sub/vendor/lib.cs", false)]
    public void LeadingSlashAnchorsAtRoot(string relative, bool ignored)
    {
        var matcher = Matcher("/vendor");
        Assert.Equal(ignored, matcher.IsIgnoredRelative(relative));
    }

    [Theory]
    [InlineData("tmp/scratch.tmp", true)]
    [InlineData("scratch.tmp", true)]
    [InlineData("a/b/c.tmp", true)]
    [InlineData("scratch.txt", false)]
    public void DoubleStarMatchesAnyNumberOfSegments(string relative, bool ignored)
    {
        var matcher = Matcher("**/*.tmp");
        Assert.Equal(ignored, matcher.IsIgnoredRelative(relative));
    }

    [Fact]
    public void IsIgnoredEntrySeesExactPathsThatPatternsCannotExpress()
    {
        // The metadata database is registered as an absolute path, not a pattern,
        // so the patterns-only check misses it. Anything acting on a path must
        // use IsIgnoredEntry for this reason.
        var matcher = new IgnoreMatcher(Root, Db, [".git/"]);

        Assert.True(matcher.IsIgnoredEntry("entangle.db"));
        Assert.True(matcher.IsIgnoredEntry("entangle.db-wal"));
        Assert.True(matcher.IsIgnoredEntry(".git/HEAD"));
        Assert.False(matcher.IsIgnoredEntry("src/app.js"));

        Assert.False(matcher.IsIgnoredRelative("entangle.db"));
    }

    [Fact]
    public void MetadataDatabaseAndSidecarsAreIgnored()
    {
        var matcher = new IgnoreMatcher(Root, Db);
        Assert.True(matcher.IsIgnored(Db));
        Assert.True(matcher.IsIgnored(Db + "-wal"));
        Assert.True(matcher.IsIgnored(Db + "-shm"));
        Assert.True(matcher.IsIgnored(Db + "-journal"));
        Assert.False(matcher.IsIgnored(Path.Combine(Root, "other.db")));
    }

    [Fact]
    public void AtomicWriteArtifactsAreAlwaysIgnored()
    {
        var matcher = new IgnoreMatcher(Root, Db);
        Assert.True(matcher.IsIgnoredRelative("notes.txt" + PathUtil.TempSuffix + "-abc123"));
    }

    [Fact]
    public void PathsOutsideTheRootAreIgnored()
    {
        var matcher = new IgnoreMatcher(Root, Db);
        Assert.True(matcher.IsIgnored(Path.Combine(Path.GetTempPath(), "elsewhere", "file.txt")));
    }

    [Fact]
    public void CaseInsensitiveMatchingFollowsTheOption()
    {
        Assert.True(new IgnoreMatcher(Root, Db, ["*.LOG"], ignoreCase: true).IsIgnoredRelative("a.log"));
        Assert.False(new IgnoreMatcher(Root, Db, ["*.LOG"], ignoreCase: false).IsIgnoredRelative("a.log"));
    }

    [Fact]
    public void DefaultsCoverGitAndNodeModules()
    {
        var options = new Entangle.Configuration.EntangleOptions();
        var matcher = new IgnoreMatcher(Root, Db, options.IgnorePatterns);
        Assert.True(matcher.IsIgnoredRelative(".git/HEAD"));
        Assert.True(matcher.IsIgnoredRelative("node_modules/x/y.js"));
        Assert.False(matcher.IsIgnoredRelative("src/app.js"));
    }
}

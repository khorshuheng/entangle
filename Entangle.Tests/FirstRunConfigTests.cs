using System.Text.Json;
using Entangle.Configuration;
using Microsoft.Extensions.Configuration;

namespace Entangle.Tests;

/// <summary>
/// First-start configuration: the defaults a fresh working directory gets, the
/// file written for it, and the promise that later starts leave that file alone.
/// </summary>
public sealed class FirstRunConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "entangle-first-run-" + Guid.NewGuid().ToString("N"));
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "entangle-state-" + Guid.NewGuid().ToString("N"));

    public FirstRunConfigTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_stateRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        try { Directory.Delete(_stateRoot, recursive: true); } catch { }
    }

    private string ConfigPath => Path.Combine(_dir, FirstRunConfig.FileName);

    /// <summary>Binds the configuration file the way the application does.</summary>
    private EntangleOptions BindFromFile() =>
        new ConfigurationBuilder()
            .AddJsonFile(ConfigPath)
            .Build()
            .GetSection("Entangle")
            .Get<EntangleOptions>()!;

    [Fact]
    public void DefaultsSatisfyValidation()
    {
        var options = new EntangleOptions();

        FirstRunConfig.ApplyDefaults(options, _stateRoot);

        Assert.Empty(options.Validate());
        Assert.Equal(FirstRunConfig.DefaultSyncDirectory, options.SyncDirectory);

        // State lives outside the working directory, in a directory per synced tree.
        Assert.StartsWith(_stateRoot + Path.DirectorySeparatorChar, options.DatabasePath);
        Assert.EndsWith(FirstRunConfig.DatabaseFileName, options.DatabasePath);
        Assert.False(options.DatabasePath.StartsWith(_dir, StringComparison.Ordinal));

        // The peer is deliberately left unchosen: no address can be guessed, so the
        // run stops with a reminder instead.
        Assert.Equal(EntangleOptions.UnsetPeerAddress, options.PeerAddress);
        Assert.False(options.PeerIsConfigured);
    }

    [Fact]
    public void EachSyncedTreeGetsItsOwnStateDirectory()
    {
        // Same directory name, two places — and the same-host case, two names.
        var first = Path.Combine(_dir, "one", "sync");
        var sameBasenameElsewhere = Path.Combine(_dir, "two", "sync");
        var secondInstance = Path.Combine(_dir, "one", "sync-b");

        var paths = new[] { first, sameBasenameElsewhere, secondInstance }
            .Select(tree => FirstRunConfig.DefaultDatabasePath(tree, _stateRoot))
            .ToList();

        Assert.Equal(3, paths.Distinct().Count());
    }

    [Fact]
    public void TheStatePathForATreeIsStable()
    {
        var tree = Path.Combine(_dir, "sync");

        Assert.Equal(
            FirstRunConfig.DefaultDatabasePath(tree, _stateRoot),
            FirstRunConfig.DefaultDatabasePath(tree, _stateRoot));

        // Trailing separators name the same tree.
        Assert.Equal(
            FirstRunConfig.DefaultDatabasePath(tree, _stateRoot),
            FirstRunConfig.DefaultDatabasePath(tree + Path.DirectorySeparatorChar, _stateRoot));
    }

    [Theory]
    [InlineData("My Sync Dir", "my-sync-dir")]
    [InlineData("sync", "sync")]
    [InlineData("***", "sync")]
    [InlineData(".hidden", "hidden")]
    public void TheInstanceStateNameStaysReadableAndSafe(string directoryName, string expectedSlug)
    {
        var instance = FirstRunConfig.InstanceName(Path.Combine(_dir, directoryName));

        Assert.StartsWith(expectedSlug + "-", instance);
        Assert.Equal(expectedSlug.Length + 9, instance.Length); // slug, dash, 8 hex digits
    }

    [Fact]
    public void DefaultsKeepValuesSuppliedByAnotherSource()
    {
        var options = new EntangleOptions
        {
            SyncDirectory = "/data/sync",
            DatabasePath = "/data/state.db",
            PeerAddress = "http://10.0.0.2:5000",
            PeerId = "peer-explicit",
        };

        FirstRunConfig.ApplyDefaults(options, _stateRoot);

        Assert.Equal("/data/sync", options.SyncDirectory);
        Assert.Equal("/data/state.db", options.DatabasePath);
        Assert.Equal("http://10.0.0.2:5000", options.PeerAddress);
        Assert.Equal("peer-explicit", options.PeerId);
    }

    [Fact]
    public void DefaultPeerIdNamesTheHost()
    {
        var options = new EntangleOptions();

        FirstRunConfig.ApplyDefaults(options, _stateRoot);

        Assert.Equal("peer-" + Environment.MachineName.Trim().ToLowerInvariant(), options.PeerId);
    }

    [Fact]
    public void FirstStartWritesTheDefaults()
    {
        var options = new EntangleOptions();

        var wrote = FirstRunConfig.InitializeIfMissing(_dir, options, out var path, _stateRoot);

        Assert.True(wrote);
        Assert.Equal(ConfigPath, path);
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void WrittenFileCarriesNoDerivedKeys()
    {
        var options = new EntangleOptions();
        FirstRunConfig.InitializeIfMissing(_dir, options, out _, _stateRoot);

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var keys = document.RootElement
            .GetProperty("Entangle")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        // Derived, read-only members must stay out of the file the user edits.
        Assert.DoesNotContain("PeerIsConfigured", keys);
        Assert.DoesNotContain("EffectiveIgnorePatterns", keys);

        // While the values that can be edited are all there.
        Assert.Contains("PeerAddress", keys);
        Assert.Contains("IgnorePatterns", keys);
    }

    [Fact]
    public void WrittenFileRecordsTheStatePathAbsolutely()
    {
        var options = new EntangleOptions();
        FirstRunConfig.InitializeIfMissing(_dir, options, out _, _stateRoot);

        var path = BindFromFile().DatabasePath;

        // An absolute path, so the run is independent of the working directory, and
        // no "~" for anything to have to expand.
        Assert.True(Path.IsPathRooted(path));
        Assert.StartsWith(_stateRoot, path);
        Assert.DoesNotContain("~", path);
    }

    [Fact]
    public void WrittenFileBindsBackToTheSameOptions()
    {
        var options = new EntangleOptions();
        FirstRunConfig.InitializeIfMissing(_dir, options, out _, _stateRoot);

        var bound = BindFromFile();

        Assert.Equal(options.SyncDirectory, bound.SyncDirectory);
        Assert.Equal(options.DatabasePath, bound.DatabasePath);
        Assert.Equal(options.Port, bound.Port);
        Assert.Equal(options.PeerAddress, bound.PeerAddress);
        Assert.Equal(options.PeerId, bound.PeerId);
        Assert.Equal(options.RescanIntervalSeconds, bound.RescanIntervalSeconds);
        Assert.Equal(options.SyncIntervalSeconds, bound.SyncIntervalSeconds);
        Assert.Equal(options.MaxBackoffSeconds, bound.MaxBackoffSeconds);
        Assert.Equal(options.MaxMessageSizeBytes, bound.MaxMessageSizeBytes);
        Assert.Equal(options.TombstoneRetentionDays, bound.TombstoneRetentionDays);
        Assert.Equal(options.IgnoreCase, bound.IgnoreCase);
        Assert.Equal(new[] { ".git/", "node_modules/" }, bound.EffectiveIgnorePatterns);
    }

    [Fact]
    public void WrittenFileCarriesTheIgnorePatternsOnce()
    {
        var options = new EntangleOptions();
        FirstRunConfig.InitializeIfMissing(_dir, options, out _, _stateRoot);

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));

        Assert.Equal("Entangle", Assert.Single(document.RootElement.EnumerateObject()).Name);

        // The generated file lists the defaults explicitly rather than relying on
        // them being unset, so the file alone documents the policy in force.
        var patterns = document.RootElement
            .GetProperty("Entangle")
            .GetProperty("IgnorePatterns")
            .EnumerateArray()
            .Select(pattern => pattern.GetString()!)
            .ToList();

        Assert.Equal(new[] { ".git/", "node_modules/" }, patterns);
    }

    [Fact]
    public void SecondStartLeavesTheFileAlone()
    {
        FirstRunConfig.InitializeIfMissing(_dir, new EntangleOptions(), out _, _stateRoot);
        var edited = File.ReadAllText(ConfigPath).Replace("\"Port\": 5000", "\"Port\": 5050");
        File.WriteAllText(ConfigPath, edited);

        var wrote = FirstRunConfig.InitializeIfMissing(_dir, new EntangleOptions(), out var path, _stateRoot);

        Assert.False(wrote);
        Assert.Equal(ConfigPath, path);
        Assert.Equal(edited, File.ReadAllText(ConfigPath));
        Assert.Equal(5050, BindFromFile().Port);
    }

    [Fact]
    public void MissingValuesInAnExistingFileAreNotFilledIn()
    {
        File.WriteAllText(ConfigPath, """{ "Entangle": { "Port": 5000 } }""");

        var options = new EntangleOptions();
        var wrote = FirstRunConfig.InitializeIfMissing(_dir, options, out _, _stateRoot);

        Assert.False(wrote);
        Assert.Equal("", options.SyncDirectory);
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void InvalidOptionsAreNeverWritten()
    {
        var options = new EntangleOptions { Port = 0 };

        var wrote = FirstRunConfig.InitializeIfMissing(_dir, options, out _, _stateRoot);

        Assert.False(wrote);
        Assert.False(File.Exists(ConfigPath));
    }
}

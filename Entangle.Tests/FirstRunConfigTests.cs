using System.Text.Json;
using Entangle.Configuration;
using Microsoft.Extensions.Configuration;

namespace Entangle.Tests;

/// <summary>
/// First-start configuration: the defaults a fresh state directory gets, the file
/// written there, and the promise that later starts leave it alone.
/// </summary>
public sealed class FirstRunConfigTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "entangle-state-" + Guid.NewGuid().ToString("N"));

    public FirstRunConfigTests() => Directory.CreateDirectory(_stateDir);

    public void Dispose()
    {
        try { Directory.Delete(_stateDir, recursive: true); } catch { }
    }

    private string ConfigPath => FirstRunConfig.ConfigPath(_stateDir);

    /// <summary>Binds the generated configuration file.</summary>
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

        FirstRunConfig.ApplyDefaults(options, _stateDir);

        Assert.Empty(options.Validate());
        Assert.Equal(FirstRunConfig.DefaultSyncDirectory, options.SyncDirectory);

        // The database is collocated with the configuration file, in the state
        // directory, so configuration and state move together.
        Assert.Equal(FirstRunConfig.DefaultDatabasePath(_stateDir), options.DatabasePath);
        Assert.Equal(Path.Combine(_stateDir, FirstRunConfig.DatabaseFileName), options.DatabasePath);

        // The peer is deliberately left unchosen: no address can be guessed, so the
        // run stops with a reminder instead.
        Assert.Equal(EntangleOptions.UnsetPeerAddress, options.PeerAddress);
        Assert.False(options.PeerIsConfigured);
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

        FirstRunConfig.ApplyDefaults(options, _stateDir);

        Assert.Equal("/data/sync", options.SyncDirectory);
        Assert.Equal("/data/state.db", options.DatabasePath);
        Assert.Equal("http://10.0.0.2:5000", options.PeerAddress);
        Assert.Equal("peer-explicit", options.PeerId);
    }

    [Fact]
    public void DefaultPeerIdNamesTheHost()
    {
        var options = new EntangleOptions();

        FirstRunConfig.ApplyDefaults(options, _stateDir);

        Assert.Equal("peer-" + Environment.MachineName.Trim().ToLowerInvariant(), options.PeerId);
    }

    [Fact]
    public void FirstStartWritesTheDefaultsIntoTheStateDirectory()
    {
        var options = new EntangleOptions();

        var wrote = FirstRunConfig.InitializeIfMissing(_stateDir, options, out var path);

        Assert.True(wrote);
        Assert.Equal(ConfigPath, path);
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void WrittenFileCarriesNoDerivedKeys()
    {
        var options = new EntangleOptions();
        FirstRunConfig.InitializeIfMissing(_stateDir, options, out _);

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
    public void WrittenFileRecordsTheDatabaseBesideIt()
    {
        var options = new EntangleOptions();
        FirstRunConfig.InitializeIfMissing(_stateDir, options, out _);

        var path = BindFromFile().DatabasePath;

        // An absolute path, next to the configuration file, with no "~" for
        // anything to have to expand.
        Assert.True(Path.IsPathRooted(path));
        Assert.Equal(Path.Combine(_stateDir, FirstRunConfig.DatabaseFileName), path);
        Assert.DoesNotContain("~", path);
    }

    [Fact]
    public void WrittenFileBindsBackToTheSameOptions()
    {
        var options = new EntangleOptions();
        FirstRunConfig.InitializeIfMissing(_stateDir, options, out _);

        var bound = BindFromFile();

        Assert.Equal(options.SyncDirectory, bound.SyncDirectory);
        Assert.Equal(options.DatabasePath, bound.DatabasePath);
        Assert.Equal(options.Port, bound.Port);
        Assert.Equal(options.BindAddress, bound.BindAddress);
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
        FirstRunConfig.InitializeIfMissing(_stateDir, options, out _);

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
        FirstRunConfig.InitializeIfMissing(_stateDir, new EntangleOptions(), out _);
        var edited = File.ReadAllText(ConfigPath).Replace("\"Port\": 5000", "\"Port\": 5050");
        File.WriteAllText(ConfigPath, edited);

        var wrote = FirstRunConfig.InitializeIfMissing(_stateDir, new EntangleOptions(), out var path);

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
        var wrote = FirstRunConfig.InitializeIfMissing(_stateDir, options, out _);

        Assert.False(wrote);
        Assert.Equal("", options.SyncDirectory);
        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void InvalidOptionsAreNeverWritten()
    {
        var options = new EntangleOptions { Port = 0 };

        var wrote = FirstRunConfig.InitializeIfMissing(_stateDir, options, out _);

        Assert.False(wrote);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void TheStateDirectoryPrefersTheChoiceThenTheEnvironmentThenTheDefault()
    {
        var chosen = Path.Combine(_stateDir, "chosen");
        var fromEnvironment = Path.Combine(_stateDir, "from-env");
        var previous = Environment.GetEnvironmentVariable(FirstRunConfig.StateDirectoryVariable);

        try
        {
            Environment.SetEnvironmentVariable(FirstRunConfig.StateDirectoryVariable, fromEnvironment);

            Assert.Equal(Path.GetFullPath(fromEnvironment), FirstRunConfig.ResolveStateDirectory(null));

            // An explicit choice wins over the environment.
            Assert.Equal(Path.GetFullPath(chosen), FirstRunConfig.ResolveStateDirectory(chosen));

            Environment.SetEnvironmentVariable(FirstRunConfig.StateDirectoryVariable, null);
            Assert.Equal(FirstRunConfig.DefaultStateDirectory, FirstRunConfig.ResolveStateDirectory(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FirstRunConfig.StateDirectoryVariable, previous);
        }
    }

    [Fact]
    public void AddConfigReadsTheStateDirectoryFile()
    {
        File.WriteAllText(ConfigPath, """{ "Entangle": { "Port": 5050, "PeerId": "peer-file" } }""");

        var configuration = new ConfigurationManager();
        FirstRunConfig.AddConfig(configuration, _stateDir);
        configuration.AddCommandLine(["--Entangle:PeerId=peer-cli"]);

        // The file is read, and the command line still wins over it.
        Assert.Equal(5050, configuration.GetValue<int>("Entangle:Port"));
        Assert.Equal("peer-cli", configuration["Entangle:PeerId"]);
    }

    [Fact]
    public void AddConfigAddsNothingWhenThereIsNoFile()
    {
        var configuration = new ConfigurationManager();
        FirstRunConfig.AddConfig(configuration, Path.Combine(_stateDir, "absent"));

        Assert.Null(configuration["Entangle:Port"]);
    }
}

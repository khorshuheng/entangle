using Entangle.Configuration;
using Entangle.Sync;
using Microsoft.Extensions.Configuration;

namespace Entangle.Tests;

/// <summary>
/// Binding of the options that carry a default, in particular
/// <see cref="EntangleOptions.IgnorePatterns"/>: a configured list replaces the
/// built-in defaults instead of being appended to them.
/// </summary>
public sealed class EntangleOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "entangle-options-" + Guid.NewGuid().ToString("N"));

    public EntangleOptionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Binds an "Entangle" section holding exactly the given JSON.</summary>
    private EntangleOptions Bind(string sectionJson)
    {
        var path = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(path, $$"""{ "Entangle": {{sectionJson}} }""");

        return new ConfigurationBuilder()
            .AddJsonFile(path)
            .Build()
            .GetSection("Entangle")
            .Get<EntangleOptions>()!;
    }

    [Fact]
    public void UnsetIgnorePatternsFallBackToTheDefaults()
    {
        var options = Bind("""{ "Port": 5000 }""");

        Assert.Null(options.IgnorePatterns);
        Assert.Equal(EntangleOptions.DefaultIgnorePatterns, options.EffectiveIgnorePatterns);
    }

    [Fact]
    public void ConfiguredIgnorePatternsReplaceTheDefaults()
    {
        var options = Bind("""{ "IgnorePatterns": [ "*.log", "build/" ] }""");

        Assert.Equal(new[] { "*.log", "build/" }, options.EffectiveIgnorePatterns);
    }

    [Fact]
    public void EmptyIgnorePatternsAreIndistinguishableFromUnsetOnes()
    {
        // Configuration flattens an empty array to no keys at all, so "[]" cannot
        // be told apart from an absent key: both fall back to the defaults.
        var options = Bind("""{ "IgnorePatterns": [] }""");

        Assert.Null(options.IgnorePatterns);
        Assert.Equal(EntangleOptions.DefaultIgnorePatterns, options.EffectiveIgnorePatterns);
    }

    [Fact]
    public void BlankIgnorePatternsExcludeNothing()
    {
        var options = Bind("""{ "IgnorePatterns": [ "" ] }""");

        // IgnoreMatcher drops blank patterns, which is the only way to configure
        // "exclude nothing": an empty array means the defaults (see above).
        var matcher = new IgnoreMatcher(_dir, databasePath: null, options.EffectiveIgnorePatterns);

        Assert.False(matcher.IsIgnoredRelative(".git/HEAD"));
        Assert.False(matcher.IsIgnoredRelative("node_modules/x/y.js"));
    }

    [Fact]
    public void IgnoreCaseFollowsTheConfiguredValue()
    {
        Assert.True(Bind("""{ "IgnoreCase": true }""").IgnoreCase);
        Assert.False(Bind("""{ "IgnoreCase": false }""").IgnoreCase);
    }

    [Theory]
    [InlineData("http://otherhost:5000")]
    [InlineData("http://localhost:5001")]
    public void AChosenPeerCountsAsConfigured(string address)
    {
        var options = Bind($$"""{ "PeerAddress": "{{address}}" }""");

        Assert.True(options.PeerIsConfigured);
    }

    [Theory]
    [InlineData("unset")]
    [InlineData("UNSET")]
    [InlineData(" unset ")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnchosenPeerIsNotConfigured(string address)
    {
        var options = Bind($$"""{ "PeerAddress": "{{address}}" }""");

        Assert.False(options.PeerIsConfigured);
    }

    [Fact]
    public void TheMarkerIsAcceptedByValidation()
    {
        // Accepting the marker is what allows a reminder instead of "must be set".
        var options = new EntangleOptions { PeerAddress = EntangleOptions.UnsetPeerAddress };
        FirstRunConfig.ApplyDefaults(options);

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("http://otherhost:5000")]
    [InlineData("http://127.0.0.1:5001")]
    [InlineData("https://remote.example.com")]
    public void ADialablePeerAddressPassesValidation(string address)
    {
        var options = new EntangleOptions { PeerAddress = address };
        FirstRunConfig.ApplyDefaults(options);

        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("localhost:5001")]
    [InlineData("otherhost:5000")]
    [InlineData("ftp://otherhost:5000")]
    [InlineData("/srv/peer")]
    [InlineData("not an address")]
    public void AMalformedPeerAddressIsRejected(string address)
    {
        var options = new EntangleOptions { PeerAddress = address };
        FirstRunConfig.ApplyDefaults(options);

        Assert.Contains(options.Validate(), error => error.Contains("PeerAddress"));
    }
}

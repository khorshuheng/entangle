using Entangle.Configuration;

namespace Entangle.Tests;

/// <summary>
/// The command line is parsed before anything else happens: asking for help, the
/// version, or nothing at all must never be mistaken for a request to start.
/// </summary>
public class CommandLineTests
{
    [Fact]
    public void NoArgumentsAsksForUsage()
    {
        var parsed = CommandLine.Parse([]);

        Assert.Equal(Command.Help, parsed.Command);
        Assert.Empty(parsed.Arguments);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpFlagAsksForUsage(string flag)
    {
        Assert.Equal(Command.Help, CommandLine.Parse([flag]).Command);
    }

    [Fact]
    public void VersionFlagReportsTheVersion()
    {
        Assert.Equal(Command.Version, CommandLine.Parse(["--version"]).Command);
    }

    [Fact]
    public void HelpWinsOverEveryOtherFlag()
    {
        var parsed = CommandLine.Parse(["run", "--version", "--help"]);

        Assert.Equal(Command.Help, parsed.Command);
        Assert.Empty(parsed.Arguments);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    public void FlagsAfterRunDoNotStartTheService(string flag)
    {
        var parsed = CommandLine.Parse(["run", flag]);

        Assert.NotEqual(Command.Run, parsed.Command);
        Assert.Empty(parsed.Arguments);
    }

    [Fact]
    public void RunPassesItsOptionsThrough()
    {
        var parsed = CommandLine.Parse(["run", "--Entangle:Port=5555", "--Entangle:PeerAddress=http://other:5000"]);

        Assert.Equal(Command.Run, parsed.Command);
        Assert.Equal(new[] { "--Entangle:Port=5555", "--Entangle:PeerAddress=http://other:5000" }, parsed.Arguments);
    }

    [Fact]
    public void RunOnItsOwnHasNothingToPassOn()
    {
        var parsed = CommandLine.Parse(["run"]);

        Assert.Equal(Command.Run, parsed.Command);
        Assert.Empty(parsed.Arguments);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("--Entangle:Port=5555")]
    [InlineData("Run")]
    [InlineData("-x")]
    public void AnythingElseIsAUsageError(string argument)
    {
        var parsed = CommandLine.Parse([argument]);

        Assert.Equal(Command.UsageError, parsed.Command);
        Assert.Equal(new[] { argument }, parsed.Arguments);
    }

    [Fact]
    public void UsageDescribesEveryCommandAndTheConfigurationSurface()
    {
        var usage = CommandLine.Usage;

        Assert.Contains("entangle run", usage);
        Assert.Contains("--help", usage);
        Assert.Contains("--version", usage);
        Assert.Contains("--Entangle:PeerAddress", usage);
    }

    [Fact]
    public void VersionIsNeverBlank()
    {
        Assert.False(string.IsNullOrWhiteSpace(CommandLine.Version));
    }
}

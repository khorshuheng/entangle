using System.Reflection;

namespace Entangle.Configuration;

/// <summary>What the command line asked the process to do.</summary>
public enum Command
{
    /// <summary>Start the sync service: <c>entangle run</c>.</summary>
    Run,

    /// <summary>Print usage: bare <c>entangle</c>, <c>--help</c>, or <c>-h</c>.</summary>
    Help,

    /// <summary>Print the version: <c>--version</c>.</summary>
    Version,

    /// <summary>Nothing runnable was asked for; report usage and exit non-zero.</summary>
    UsageError,
}

/// <summary>
/// A parsed command line. <paramref name="Arguments"/> holds the options to pass
/// to configuration for <see cref="Command.Run"/>, and the arguments that could
/// not be understood for <see cref="Command.UsageError"/>.
/// </summary>
public sealed record ParsedCommandLine(Command Command, IReadOnlyList<string> Arguments);

/// <summary>
/// Turns the process arguments into a command. Parsing is pure — no I/O, no
/// environment access, nothing created — so asking for help or the version can
/// never have the side effects of a start.
/// </summary>
public static class CommandLine
{
    private static readonly string[] HelpFlags = ["--help", "-h"];
    private static readonly string[] VersionFlags = ["--version"];

    public static ParsedCommandLine Parse(string[] args)
    {
        // Help and version answer a question wherever they appear, including after
        // "run"; help wins when both are present.
        if (args.Any(HelpFlags.Contains))
            return new ParsedCommandLine(Command.Help, []);
        if (args.Any(VersionFlags.Contains))
            return new ParsedCommandLine(Command.Version, []);

        // No arguments asks for usage rather than reporting an error.
        if (args.Length == 0)
            return new ParsedCommandLine(Command.Help, []);

        if (args[0] == "run")
            return new ParsedCommandLine(Command.Run, args[1..]);

        // Options are configuration, and only "run" consumes configuration. Treated
        // leniently, `entangle --Entangle:Port=5001` would start a server unnoticed.
        return new ParsedCommandLine(Command.UsageError, args);
    }

    /// <summary>The running assembly's informational version, source revision included.</summary>
    public static string Version =>
        typeof(CommandLine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CommandLine).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Usage text: the commands, the configuration sources, and one example.</summary>
    public static string Usage => """
        entangle — bidirectional file/directory sync between two peers

        Usage:
          entangle run [options]    start the sync service
          entangle --help           show this text
          entangle --version        show the version

        Configuration is read from appsettings.json — the per-user file under the
        state directory (~/.entangle on Linux, %LOCALAPPDATA%\entangle on Windows),
        overridden by one in the working directory — from environment variables
        (Entangle__Key), and from the options given to "run"
        (--Entangle:Key=value), in that order of precedence.

          entangle run --Entangle:PeerAddress=http://otherhost:5000

        Unless configured otherwise, the synced files live in ./entangled and the
        state database under the user's state directory (~/.entangle on Linux,
        %LOCALAPPDATA%\entangle on Windows), in a directory per synced tree.

        The gRPC port listens on loopback only; set Entangle:BindAddress to "any"
        (or an IP address) to reach it from another machine. The service has no
        authentication, so only do that on a trusted network or over a tunnel.
        """;
}

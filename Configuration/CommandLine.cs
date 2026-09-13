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
/// not be understood for <see cref="Command.UsageError"/>. <paramref name="StateDirectory"/>
/// is the directory chosen with <c>--state-dir</c>, if any.
/// </summary>
public sealed record ParsedCommandLine(
    Command Command,
    IReadOnlyList<string> Arguments,
    string? StateDirectory = null);

/// <summary>
/// Turns the process arguments into a command. Parsing is pure — no I/O, no
/// environment access, nothing created — so asking for help or the version can
/// never have the side effects of a start.
/// </summary>
public static class CommandLine
{
    private const string StateDirectoryFlag = "--state-dir";

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
            return ParseRun(args[1..]);

        // Options are configuration, and only "run" consumes configuration. Treated
        // leniently, `entangle --Entangle:Port=5001` would start a server unnoticed.
        return new ParsedCommandLine(Command.UsageError, args);
    }

    /// <summary>
    /// Splits the options after "run" into the ones the host consumes here
    /// (<c>--state-dir</c>, which has to be known before configuration is read) and
    /// the ones handed on to configuration.
    /// </summary>
    private static ParsedCommandLine ParseRun(string[] args)
    {
        var options = new List<string>();
        string? stateDirectory = null;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (argument == StateDirectoryFlag)
            {
                // A flag with no value is reported like any other argument that
                // could not be understood.
                if (i + 1 >= args.Length)
                    return new ParsedCommandLine(Command.UsageError, [argument]);

                stateDirectory = args[++i];
            }
            else if (argument.StartsWith(StateDirectoryFlag + "=", StringComparison.Ordinal))
            {
                stateDirectory = argument[(StateDirectoryFlag.Length + 1)..];
            }
            else
            {
                options.Add(argument);
            }
        }

        return new ParsedCommandLine(Command.Run, options, stateDirectory);
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

        Configuration is read from appsettings.json in the state directory, from
        environment variables (Entangle__Key), and from the options given to "run"
        (--Entangle:Key=value), in that order of precedence. The state directory
        defaults to ~/.entangle on Linux (%LOCALAPPDATA%\entangle on Windows) and
        holds the configuration and the SQLite database together.

          entangle run --Entangle:PeerAddress=http://otherhost:5000

        --state-dir <dir> (or ENTANGLE_STATE_DIR) picks a different state directory,
        which is how two peers run on one host:

          entangle run --state-dir ~/.entangle/peer-b

        Unless configured otherwise, the synced files live in ./entangled.

        The gRPC port listens on loopback only; set Entangle:BindAddress to "any"
        (or an IP address) to reach it from another machine. The service has no
        authentication, so only do that on a trusted network or over a tunnel.
        """;
}

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Entangle.Configuration;

/// <summary>
/// The state directory a peer uses: <c>~/.entangle</c>, or
/// <c>%LOCALAPPDATA%\entangle</c> on Windows, holding that peer's appsettings.json
/// and its SQLite database together. Starting a peer from an arbitrary working
/// directory therefore leaves that directory alone, and running two peers on one
/// host is a matter of giving each its own state directory. A working directory's
/// own appsettings.json is never read.
/// </summary>
public static class FirstRunConfig
{
    /// <summary>Name of the configuration file inside the state directory.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>Default sync directory: a subdirectory of the working directory.</summary>
    public const string DefaultSyncDirectory = "./entangled";

    /// <summary>Name of the SQLite database inside the state directory.</summary>
    public const string DatabaseFileName = "entangle.db";

    /// <summary>Environment variable naming the state directory.</summary>
    public const string StateDirectoryVariable = "ENTANGLE_STATE_DIR";

    /// <summary>
    /// The state directory used when none is chosen: <c>~/.entangle</c>, or
    /// <c>%LOCALAPPDATA%\entangle</c> on Windows, which is where that platform
    /// keeps per-user application state.
    /// </summary>
    public static string DefaultStateDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "entangle")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".entangle");

    /// <summary>
    /// Resolves the state directory: an explicit choice first, then
    /// <see cref="StateDirectoryVariable"/>, then <see cref="DefaultStateDirectory"/>.
    /// The result is absolute so every consumer agrees on it regardless of the
    /// working directory.
    /// </summary>
    public static string ResolveStateDirectory(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
            return Path.GetFullPath(chosen);

        var fromEnvironment = Environment.GetEnvironmentVariable(StateDirectoryVariable);
        return !string.IsNullOrWhiteSpace(fromEnvironment)
            ? Path.GetFullPath(fromEnvironment)
            : DefaultStateDirectory;
    }

    /// <summary>Path of the configuration file inside <paramref name="stateDirectory"/>.</summary>
    public static string ConfigPath(string stateDirectory) => Path.Combine(stateDirectory, FileName);

    /// <summary>
    /// Default database path: the state directory itself, collocated with the
    /// configuration file, so a peer's configuration and its state live and move
    /// together. Keeping SQLite outside the synced tree also keeps it off a
    /// mounted filesystem, where its locking cannot be relied on.
    /// </summary>
    public static string DefaultDatabasePath(string stateDirectory) =>
        Path.Combine(stateDirectory, DatabaseFileName);

    /// <summary>
    /// Fills in the values that no configuration source supplies, leaving anything
    /// already set — from the environment or the command line — untouched.
    /// </summary>
    public static void ApplyDefaults(EntangleOptions options, string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(options.SyncDirectory))
            options.SyncDirectory = DefaultSyncDirectory;
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            options.DatabasePath = DefaultDatabasePath(stateDirectory);
        if (string.IsNullOrWhiteSpace(options.PeerAddress))
            options.PeerAddress = EntangleOptions.UnsetPeerAddress;
        if (string.IsNullOrWhiteSpace(options.PeerId))
            options.PeerId = NewPeerId();
        if (options.IgnorePatterns is null)
            options.IgnorePatterns = [.. EntangleOptions.DefaultIgnorePatterns];
    }

    /// <summary>
    /// Adds the state directory's appsettings.json to <paramref name="configuration"/>
    /// as its lowest-precedence source, so the environment and the command line —
    /// which the caller adds afterwards — override it. Adds nothing when the file is
    /// absent, so starting a peer never creates the state directory by itself.
    /// </summary>
    public static void AddConfig(IConfigurationBuilder configuration, string stateDirectory)
    {
        var path = ConfigPath(stateDirectory);
        if (File.Exists(path))
            configuration.AddJsonFile(path, optional: true);
    }

    /// <summary>
    /// Creates the configuration file for a first start: <paramref name="options"/>
    /// gains the defaults for whatever nothing else supplied, and the result is
    /// written to <paramref name="stateDirectory"/> — but only when no file is there
    /// yet, and only when the result is valid, so a start that fails validation can
    /// never leave a broken file behind. Returns whether a file was written, with
    /// its path in <paramref name="path"/>.
    /// </summary>
    public static bool InitializeIfMissing(string stateDirectory, EntangleOptions options, out string path)
    {
        path = ConfigPath(stateDirectory);
        if (File.Exists(path))
            return false;

        ApplyDefaults(options, stateDirectory);
        if (options.Validate().Count > 0)
            return false;

        Directory.CreateDirectory(stateDirectory);
        File.WriteAllText(path, Serialize(options));
        return true;
    }

    /// <summary>Serialises the effective options as the "Entangle" section of a config file.</summary>
    private static string Serialize(EntangleOptions options) =>
        JsonSerializer.Serialize(new { Entangle = options }, JsonOptions) + Environment.NewLine;

    /// <summary>
    /// A peer id derived from the machine name, so a peer is recognisable in logs
    /// and configuration. Falls back to a random id for the rare host that cannot
    /// report a name, so a first start never writes an empty (invalid) value.
    /// </summary>
    private static string NewPeerId()
    {
        var hostname = Hostname();
        return hostname.Length > 0 ? $"peer-{hostname}" : $"peer-{RandomSuffix()}";
    }

    private static string Hostname()
    {
        try
        {
            return Environment.MachineName.Trim().ToLowerInvariant();
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static string RandomSuffix() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

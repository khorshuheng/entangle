using System.Security.Cryptography;
using System.Text.Json;

namespace Entangle.Configuration;

/// <summary>
/// Writes the default appsettings.json for a working directory that has none, so
/// a fresh install starts without hand-written configuration. An existing file is
/// never modified: from the second start on, the file belongs to the user.
/// </summary>
public static class FirstRunConfig
{
    /// <summary>Name of the configuration file, relative to the content root.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>Default sync directory: a subdirectory of the working directory.</summary>
    public const string DefaultSyncDirectory = "./entangled";

    /// <summary>Default path of the local state database in the working directory.</summary>
    public const string DefaultDatabasePath = "./entangle.db";

    /// <summary>
    /// Default peer address, expecting a second instance on the same host on the
    /// next port up.
    /// </summary>
    public const string DefaultPeerAddress = "http://localhost:5001";

    /// <summary>
    /// Fills in the values that no configuration source supplies, leaving anything
    /// already set — from the environment or the command line — untouched.
    /// </summary>
    public static void ApplyDefaults(EntangleOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SyncDirectory))
            options.SyncDirectory = DefaultSyncDirectory;
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            options.DatabasePath = DefaultDatabasePath;
        if (string.IsNullOrWhiteSpace(options.PeerAddress))
            options.PeerAddress = DefaultPeerAddress;
        if (string.IsNullOrWhiteSpace(options.PeerId))
            options.PeerId = NewPeerId();
        if (options.IgnorePatterns is null)
            options.IgnorePatterns = [.. EntangleOptions.DefaultIgnorePatterns];
    }

    /// <summary>
    /// Creates the configuration file for a first start: <paramref name="options"/>
    /// gains the defaults for whatever nothing else supplied, and the result is
    /// written to <paramref name="contentRootPath"/> — but only when no file is
    /// there yet, and only when the result is valid, so a start that fails
    /// validation can never leave a broken file behind. Returns whether a file was
    /// written, with its path in <paramref name="path"/>.
    /// </summary>
    public static bool InitializeIfMissing(string contentRootPath, EntangleOptions options, out string path)
    {
        path = Path.Combine(contentRootPath, FileName);
        if (File.Exists(path))
            return false;

        ApplyDefaults(options);
        if (options.Validate().Count > 0)
            return false;

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

using System.Security.Cryptography;
using System.Text;
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

    /// <summary>File name of the state database inside its per-tree directory.</summary>
    public const string DatabaseFileName = "entangle.db";

    /// <summary>
    /// Where per-tree state lives: <c>~/.entangle</c>, or
    /// <c>%LOCALAPPDATA%\entangle</c> on Windows, which is where that platform keeps
    /// per-user application state.
    /// </summary>
    public static string StateRoot => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "entangle")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".entangle");

    /// <summary>
    /// Fills in the values that no configuration source supplies, leaving anything
    /// already set — from the environment or the command line — untouched.
    /// </summary>
    public static void ApplyDefaults(EntangleOptions options, string? stateRoot = null)
    {
        if (string.IsNullOrWhiteSpace(options.SyncDirectory))
            options.SyncDirectory = DefaultSyncDirectory;
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            options.DatabasePath = DefaultDatabasePath(options.SyncDirectory, stateRoot);
        if (string.IsNullOrWhiteSpace(options.PeerAddress))
            options.PeerAddress = EntangleOptions.UnsetPeerAddress;
        if (string.IsNullOrWhiteSpace(options.PeerId))
            options.PeerId = NewPeerId();
        if (options.IgnorePatterns is null)
            options.IgnorePatterns = [.. EntangleOptions.DefaultIgnorePatterns];
    }

    /// <summary>
    /// Default path of the state database for a synced tree: one directory per tree
    /// under the state root, so two peers on one host — or two trees sharing a
    /// directory name in different places — never share state. Keeping the database
    /// outside the synced tree also keeps SQLite off a mounted filesystem, where its
    /// locking cannot be relied on.
    /// </summary>
    public static string DefaultDatabasePath(string syncDirectory, string? stateRoot = null) =>
        Path.Combine(stateRoot ?? StateRoot, InstanceName(Path.GetFullPath(syncDirectory)), DatabaseFileName);

    /// <summary>
    /// Directory name identifying one synced tree: a readable slug of its directory
    /// name plus a short hash of its full path, so two trees with the same name in
    /// different places stay apart.
    /// </summary>
    public static string InstanceName(string resolvedSyncDirectory)
    {
        var trimmed = resolvedSyncDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..8];
        return $"{Slug(Path.GetFileName(trimmed))}-{hash.ToLowerInvariant()}";
    }

    /// <summary>
    /// Creates the configuration file for a first start: <paramref name="options"/>
    /// gains the defaults for whatever nothing else supplied, and the result is
    /// written to <paramref name="contentRootPath"/> — but only when no file is
    /// there yet, and only when the result is valid, so a start that fails
    /// validation can never leave a broken file behind. Returns whether a file was
    /// written, with its path in <paramref name="path"/>.
    /// </summary>
    public static bool InitializeIfMissing(
        string contentRootPath,
        EntangleOptions options,
        out string path,
        string? stateRoot = null)
    {
        path = Path.Combine(contentRootPath, FileName);
        if (File.Exists(path))
            return false;

        ApplyDefaults(options, stateRoot);
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

    /// <summary>A directory name that is safe on every platform, or "sync" when nothing usable is left.</summary>
    private static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
                slug.Append(character);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }

        var trimmed = slug.ToString().Trim('-');
        return trimmed.Length > 0 ? trimmed : "sync";
    }

    private static string RandomSuffix() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

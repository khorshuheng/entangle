using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Entangle.Configuration;

/// <summary>
/// Writes the default appsettings.json for an install that has none, so a fresh
/// start needs no hand-written configuration. The generated file lives under the
/// state root (<c>~/.entangle</c>), not in whatever directory the service happens
/// to be run from, so starting it from anywhere leaves that directory alone. A
/// working directory's own appsettings.json, when there is one, is the user's and
/// takes precedence; nothing is written to it or over it.
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
    /// Creates the per-user configuration file for a first start: <paramref name="options"/>
    /// gains the defaults for whatever nothing else supplied, and the result is
    /// written under the state root — but only when neither the working directory
    /// nor the state root has a file yet, and only when the result is valid, so a
    /// start that fails validation can never leave a broken file behind.
    /// <paramref name="path"/> is the file to edit for a missing value: the working
    /// directory's own file when there is one, since it takes precedence, and the
    /// per-user file otherwise. Returns whether a file was written.
    /// </summary>
    public static bool InitializeIfMissing(
        string contentRootPath,
        EntangleOptions options,
        out string path,
        string? stateRoot = null)
    {
        // A file in the working directory is the user's own and wins over the
        // per-user file, so it is never generated, appended to, or overridden.
        path = Path.Combine(contentRootPath, FileName);
        if (File.Exists(path))
            return false;

        var userFile = Path.Combine(stateRoot ?? StateRoot, FileName);
        path = userFile;
        if (File.Exists(userFile))
            return false;

        ApplyDefaults(options, stateRoot);
        if (options.Validate().Count > 0)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        File.WriteAllText(userFile, Serialize(options));
        return true;
    }

    /// <summary>
    /// Adds the per-user configuration file to <paramref name="configuration"/> as
    /// its lowest-precedence source — below the working directory's own
    /// appsettings.json, the environment, and the command line — so anything a
    /// later source sets wins. Adds nothing when the file is absent, so a run that
    /// keeps its configuration in the working directory never creates the state root.
    /// </summary>
    public static void AddUserConfig(IConfigurationBuilder configuration, string? stateRoot = null)
    {
        var root = stateRoot ?? StateRoot;
        if (!File.Exists(Path.Combine(root, FileName)))
            return;

        configuration.Sources.Insert(0, new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(root),
            Path = FileName,
            Optional = true,
        });
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

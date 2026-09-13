using System.Text.Json.Serialization;

namespace Entangle.Configuration;

/// <summary>
/// Runtime configuration for an Entangle peer, bound from the "Entangle" configuration
/// section. Values may come from appsettings.json, environment variables
/// (Entangle__SyncDirectory, ...), or CLI args (--Entangle:SyncDirectory, ...).
/// </summary>
public sealed class EntangleOptions
{
    /// <summary>Local directory to keep in sync (required).</summary>
    public string SyncDirectory { get; set; } = "";

    /// <summary>Path to the local SQLite state database (required).</summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>Port the gRPC server listens on.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>Address of the peer instance to sync with (required).</summary>
    public string PeerAddress { get; set; } = "";

    /// <summary>Stable unique id for this peer; used for LWW tie-break (required).</summary>
    public string PeerId { get; set; } = "";

    /// <summary>Interval, in seconds, for the periodic full rescan.</summary>
    public int RescanIntervalSeconds { get; set; } = 30;

    /// <summary>Interval, in seconds, between sync/reconcile passes.</summary>
    public int SyncIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum retry backoff, in seconds, while the peer is unreachable.</summary>
    public int MaxBackoffSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum size, in bytes, of a single gRPC message in either direction.
    /// gRPC's own default is 4 MiB, which silently caps the size of a file the
    /// service can sync; this is raised so whole-file transfers work for
    /// normal-sized files.
    /// </summary>
    public int MaxMessageSizeBytes { get; set; } = DefaultMaxMessageSizeBytes;

    /// <summary>Default value for <see cref="MaxMessageSizeBytes"/> (64 MiB).</summary>
    public const int DefaultMaxMessageSizeBytes = 64 * 1024 * 1024;

    /// <summary>
    /// How long, in days, a deletion tombstone is kept for a path the peer has
    /// no record of. Tombstones that both sides agree on are reclaimed
    /// immediately. Such an extra tombstone is not needed for the delete to
    /// reach the peer (and its absence means the peer holds no file to delete),
    /// so this defaults to 0; raise it only to protect against a peer whose
    /// view is transiently partial.
    /// </summary>
    public int TombstoneRetentionDays { get; set; }

    /// <summary>
    /// Compare paths case-insensitively. Defaults to true on Windows (whose
    /// filesystems are typically case-insensitive) and false elsewhere.
    /// </summary>
    public bool IgnoreCase { get; set; } = OperatingSystem.IsWindows();

    /// <summary>
    /// Glob patterns, matched against sync-relative paths, whose matches are
    /// excluded from sync. Unset means <see cref="DefaultIgnorePatterns"/>; a
    /// configured list replaces them entirely. An empty array is indistinguishable
    /// from an unset key — configuration flattens it away — so the defaults apply
    /// to it as well; a list of blank entries is what excludes nothing.
    /// </summary>
    public List<string>? IgnorePatterns { get; set; }

    /// <summary>Patterns in force when <see cref="IgnorePatterns"/> is unset.</summary>
    public static IReadOnlyList<string> DefaultIgnorePatterns { get; } = [".git/", "node_modules/"];

    /// <summary>
    /// The patterns to apply: the configured ones, or the defaults when nothing is
    /// configured. Kept out of configuration serialisation, which writes
    /// <see cref="IgnorePatterns"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> EffectiveIgnorePatterns => IgnorePatterns ?? DefaultIgnorePatterns;

    /// <summary>Returns configuration errors; empty when the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(SyncDirectory))
            errors.Add("Entangle:SyncDirectory must be set.");
        if (string.IsNullOrWhiteSpace(DatabasePath))
            errors.Add("Entangle:DatabasePath must be set.");
        if (string.IsNullOrWhiteSpace(PeerAddress))
            errors.Add("Entangle:PeerAddress must be set.");
        if (string.IsNullOrWhiteSpace(PeerId))
            errors.Add("Entangle:PeerId must be set.");
        if (Port is < 1 or > 65535)
            errors.Add($"Entangle:Port must be between 1 and 65535 (got {Port}).");
        if (RescanIntervalSeconds <= 0)
            errors.Add($"Entangle:RescanIntervalSeconds must be positive (got {RescanIntervalSeconds}).");
        if (SyncIntervalSeconds <= 0)
            errors.Add($"Entangle:SyncIntervalSeconds must be positive (got {SyncIntervalSeconds}).");
        if (MaxBackoffSeconds <= 0)
            errors.Add($"Entangle:MaxBackoffSeconds must be positive (got {MaxBackoffSeconds}).");
        if (MaxMessageSizeBytes <= 0)
            errors.Add($"Entangle:MaxMessageSizeBytes must be positive (got {MaxMessageSizeBytes}).");
        if (TombstoneRetentionDays < 0)
            errors.Add($"Entangle:TombstoneRetentionDays must not be negative (got {TombstoneRetentionDays}).");

        return errors;
    }
}

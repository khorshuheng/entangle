namespace Beam.Configuration;

/// <summary>
/// Runtime configuration for a Beam peer, bound from the "Beam" configuration
/// section. Values may come from appsettings.json, environment variables
/// (Beam__SyncDirectory, ...), or CLI args (--Beam:SyncDirectory, ...).
/// </summary>
public sealed class BeamOptions
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

    /// <summary>Returns configuration errors; empty when the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(SyncDirectory))
            errors.Add("Beam:SyncDirectory must be set.");
        if (string.IsNullOrWhiteSpace(DatabasePath))
            errors.Add("Beam:DatabasePath must be set.");
        if (string.IsNullOrWhiteSpace(PeerAddress))
            errors.Add("Beam:PeerAddress must be set.");
        if (string.IsNullOrWhiteSpace(PeerId))
            errors.Add("Beam:PeerId must be set.");
        if (Port is < 1 or > 65535)
            errors.Add($"Beam:Port must be between 1 and 65535 (got {Port}).");
        if (RescanIntervalSeconds <= 0)
            errors.Add($"Beam:RescanIntervalSeconds must be positive (got {RescanIntervalSeconds}).");
        if (SyncIntervalSeconds <= 0)
            errors.Add($"Beam:SyncIntervalSeconds must be positive (got {SyncIntervalSeconds}).");
        if (MaxBackoffSeconds <= 0)
            errors.Add($"Beam:MaxBackoffSeconds must be positive (got {MaxBackoffSeconds}).");

        return errors;
    }
}

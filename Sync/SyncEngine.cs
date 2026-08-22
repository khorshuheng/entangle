using Beam.Configuration;
using Beam.Model;
using Beam.Storage;

namespace Beam.Sync;

/// <summary>
/// Background service that periodically exchanges state with the peer and
/// applies the reconcile plan so both sides converge. Additions and
/// modifications copy the whole file; directories are materialized as empty
/// directories.
/// </summary>
public sealed class SyncEngine : BackgroundService
{
    private readonly BeamOptions _options;
    private readonly ISyncStore _store;
    private readonly PeerClient _peer;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        BeamOptions options,
        ISyncStore store,
        PeerClient peer,
        ILogger<SyncEngine> logger)
    {
        _options = options;
        _store = store;
        _peer = peer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Sync engine starting; peer {Peer} every {Interval}s",
            _options.PeerAddress,
            _options.SyncIntervalSeconds);

        var syncInterval = TimeSpan.FromSeconds(_options.SyncIntervalSeconds);
        var maxBackoff = TimeSpan.FromSeconds(_options.MaxBackoffSeconds);
        var backoff = TimeSpan.FromSeconds(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            var success = false;
            try
            {
                await ReconcileOnceAsync(stoppingToken);
                success = true;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Reconcile failed; {Pending} pending changes, retrying in {Delay}s",
                    _store.GetPendingChanges().Count,
                    backoff.TotalSeconds);
            }

            var delay = success ? syncInterval : backoff;
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Grow backoff after a failure, reset it after a success.
            backoff = success
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, maxBackoff.TotalSeconds));
        }
    }

    /// <summary>Exchange state and apply one full reconcile pass.</summary>
    public async Task ReconcileOnceAsync(CancellationToken ct = default)
    {
        var local = _store.GetEntries().ToList();
        var (peerId, peer) = await _peer.ExchangeStateAsync(_options.PeerId, local, ct);
        var actions = Reconciler.Plan(local, peer, _options.PeerId, peerId);

        if (actions.Count > 0)
        {
            _logger.LogInformation("Reconciling {Count} paths", actions.Count);
            _logger.LogDebug("Local state: {Local}", Dump(local));
            _logger.LogDebug("Peer state: {Peer}", Dump(peer));
            await ApplyAsync(actions, ct);
        }

        // Full state exchange succeeded; local changes are now on the peer.
        _store.ClearPendingChanges();
    }

    private static string Dump(IEnumerable<SyncEntry> entries)
        => string.Join("; ", entries.Select(e =>
            $"{e.Path}|{e.Type}|{e.Mtime:O}|{(e.ContentHash.Length >= 8 ? e.ContentHash[..8] : e.ContentHash)}"));

    private async Task ApplyAsync(IReadOnlyList<ReconcileAction> actions, CancellationToken ct)
    {
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case ReconcileActionKind.Pull:
                    await PullAsync(action.Source, ct);
                    break;
                case ReconcileActionKind.Push:
                    await PushAsync(action.Source, ct);
                    break;
            }
        }
    }

    private async Task PullAsync(SyncEntry entry, CancellationToken ct)
    {
        var full = PathUtil.ResolveWithinRoot(_options.SyncDirectory, entry.Path);

        if (entry.Tombstone)
        {
            PathUtil.DeletePathAndPruneEmptyParents(_options.SyncDirectory, full);
            _store.Upsert(entry);
            _logger.LogDebug("Deleted {Path} (peer tombstone)", entry.Path);
            return;
        }

        var (content, mtime, isDirectory) = await _peer.GetFileAsync(entry.Path, ct);

        if (isDirectory)
        {
            if (File.Exists(full))
                File.Delete(full);
            Directory.CreateDirectory(full);
        }
        else
        {
            if (Directory.Exists(full))
                Directory.Delete(full, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, content, ct);
            File.SetLastWriteTimeUtc(full, mtime.UtcDateTime);
        }

        // Record the converged entry immediately; the watcher will confirm it.
        _store.Upsert(entry);
        _logger.LogDebug("Pulled {Path}", entry.Path);
    }

    private async Task PushAsync(SyncEntry entry, CancellationToken ct)
    {
        var full = PathUtil.ResolveWithinRoot(_options.SyncDirectory, entry.Path);

        if (entry.Tombstone)
        {
            await _peer.DeleteAsync(entry.Path, entry.Mtime, ct);
            _logger.LogDebug("Pushed deletion of {Path}", entry.Path);
            return;
        }

        if (entry.Type == EntryType.Directory)
        {
            await _peer.PutFileAsync(entry.Path, Array.Empty<byte>(), entry.Mtime, isDirectory: true, ct);
        }
        else
        {
            var content = await File.ReadAllBytesAsync(full, ct);
            await _peer.PutFileAsync(entry.Path, content, entry.Mtime, isDirectory: false, ct);
        }

        _logger.LogDebug("Pushed {Path}", entry.Path);
    }
}

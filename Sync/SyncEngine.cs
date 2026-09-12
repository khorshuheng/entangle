using Entangle.Configuration;
using Entangle.Model;
using Entangle.Storage;
using Grpc.Core;

namespace Entangle.Sync;

/// <summary>
/// Background service that periodically exchanges state with the peer and
/// applies the reconcile plan so both sides converge. Additions and
/// modifications copy the whole file; directories are materialized as empty
/// directories.
/// </summary>
public sealed class SyncEngine : BackgroundService
{
    private readonly EntangleOptions _options;
    private readonly ISyncStore _store;
    private readonly PeerClient _peer;
    private readonly IgnoreMatcher _ignore;
    private readonly SyncMetrics _metrics;
    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(
        EntangleOptions options,
        ISyncStore store,
        PeerClient peer,
        IgnoreMatcher ignore,
        SyncMetrics metrics,
        ILogger<SyncEngine> logger)
    {
        _options = options;
        _store = store;
        _peer = peer;
        _ignore = ignore;
        _metrics = metrics;
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
                    "Reconcile failed; {Entries} local entries, retrying in {Delay}s",
                    _store.GetEntries().Count,
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
        // Ignored paths are not part of sync state in either direction: they are
        // never advertised to the peer, and a peer entry for one is never acted
        // on (ignoring is local policy, not a delete). This must use the full
        // check, not just the patterns, so a peer advertising a path equal to
        // this instance's metadata database cannot overwrite it.
        var local = _store.GetEntries()
            .Where(entry => !_ignore.IsIgnoredEntry(entry.Path))
            .ToList();
        var (peerId, peerState) = await _peer.ExchangeStateAsync(_options.PeerId, local, ct);
        var peer = peerState
            .Where(entry => !_ignore.IsIgnoredEntry(entry.Path))
            .ToList();
        var actions = Reconciler.Plan(
            local,
            peer,
            _options.PeerId,
            peerId,
            _options.IgnoreCase,
            TimeSpan.FromDays(_options.TombstoneRetentionDays),
            onDuplicatePath: (first, second) =>
                _logger.LogWarning(
                    "Paths {First} and {Second} collide under the active case-sensitivity; "
                    + "keeping the first and ignoring the second",
                    first,
                    second));

        if (actions.Count > 0)
        {
            _logger.LogInformation("Reconciling {Count} paths", actions.Count);
            _logger.LogDebug("Local state: {Local}", Dump(local));
            _logger.LogDebug("Peer state: {Peer}", Dump(peer));
            await ApplyAsync(actions, ct);
        }
    }

    private static string Dump(IEnumerable<SyncEntry> entries)
        => string.Join("; ", entries.Select(e =>
            $"{e.Path}|{e.Type}|{e.Mtime:O}|{(e.ContentHash.Length >= 8 ? e.ContentHash[..8] : e.ContentHash)}"));

    private async Task ApplyAsync(IReadOnlyList<ReconcileAction> actions, CancellationToken ct)
    {
        foreach (var action in actions)
        {
            try
            {
                switch (action.Kind)
                {
                    case ReconcileActionKind.Pull:
                        await PullAsync(action.Source, ct);
                        break;
                    case ReconcileActionKind.Push:
                        await PushAsync(action.Source, ct);
                        break;
                    case ReconcileActionKind.Remove:
                        _store.Remove(action.Source.Path);
                        _metrics.RecordRemove();
                        _logger.LogDebug("Reclaimed tombstone for {Path}", action.Source.Path);
                        break;
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
            {
                // Diagnose rather than swallow: an oversize file would
                // otherwise retry forever with no indication of the cause.
                _logger.LogError(
                    ex,
                    "Peer rejected {Path}: it exceeds the configured gRPC message limit "
                    + "(Entangle:MaxMessageSizeBytes = {Limit} bytes)",
                    action.Source.Path,
                    _options.MaxMessageSizeBytes);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                // The peer's state said the path existed but its filesystem
                // disagrees; retry on the next pass rather than failing the run.
                _logger.LogWarning(
                    "Peer no longer has {Path} ({Detail}); will re-reconcile",
                    action.Source.Path,
                    ex.Status.Detail);
            }
        }
    }

    private async Task PullAsync(SyncEntry entry, CancellationToken ct)
    {
        var full = PathUtil.ResolveWithinRoot(_options.SyncDirectory, entry.Path, _options.IgnoreCase);

        if (entry.Tombstone)
        {
            PathUtil.DeletePathAndPruneEmptyParents(_options.SyncDirectory, full, _options.IgnoreCase);
            _store.Upsert(entry);
            _metrics.RecordPull(0);
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
            await PathUtil.WriteAllBytesAtomicAsync(full, content, mtime, ct);
        }

        // Record the converged entry immediately; the watcher will confirm it.
        _store.Upsert(entry);
        _metrics.RecordPull(isDirectory ? 0 : content.Length);
        _logger.LogDebug("Pulled {Path}", entry.Path);
    }

    private async Task PushAsync(SyncEntry entry, CancellationToken ct)
    {
        var full = PathUtil.ResolveWithinRoot(_options.SyncDirectory, entry.Path, _options.IgnoreCase);

        if (entry.Tombstone)
        {
            if (await _peer.DeleteAsync(entry.Path, entry.Mtime, ct))
                _metrics.RecordPush(0);
            else
                WarnRefused(entry.Path);

            _logger.LogDebug("Pushed deletion of {Path}", entry.Path);
            return;
        }

        if (entry.Type == EntryType.Directory)
        {
            if (await _peer.PutFileAsync(entry.Path, Array.Empty<byte>(), entry.Mtime, isDirectory: true, ct))
                _metrics.RecordPush(0);
            else
                WarnRefused(entry.Path);
        }
        else
        {
            var info = new FileInfo(full);
            if (info.Length > _options.MaxMessageSizeBytes)
            {
                _logger.LogError(
                    "Not sending {Path} ({Size} bytes): it exceeds Entangle:MaxMessageSizeBytes ({Limit} bytes)",
                    entry.Path,
                    info.Length,
                    _options.MaxMessageSizeBytes);
                return;
            }

            var content = await File.ReadAllBytesAsync(full, ct);
            if (await _peer.PutFileAsync(entry.Path, content, entry.Mtime, isDirectory: false, ct))
                _metrics.RecordPush(content.Length);
            else
                WarnRefused(entry.Path);
        }

        _logger.LogDebug("Pushed {Path}", entry.Path);
    }

    /// <summary>
    /// The peer rejected a path because it excludes it. Report the mismatch
    /// rather than retrying invisibly on every pass.
    /// </summary>
    private void WarnRefused(string path) =>
        _logger.LogWarning(
            "Peer refused {Path} because it is excluded there; check that IgnorePatterns agree on both sides",
            path);
}

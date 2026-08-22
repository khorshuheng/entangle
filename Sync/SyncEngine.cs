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

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.SyncIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconcile failed; will retry");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Exchange state and apply one full reconcile pass.</summary>
    public async Task ReconcileOnceAsync(CancellationToken ct = default)
    {
        var local = _store.GetEntries().ToList();
        var peer = await _peer.ExchangeStateAsync(_options.PeerId, local, ct);
        var actions = Reconciler.Plan(local, peer, _options.PeerId);

        if (actions.Count == 0)
            return;

        _logger.LogInformation("Reconciling {Count} paths", actions.Count);
        await ApplyAsync(actions, ct);
    }

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

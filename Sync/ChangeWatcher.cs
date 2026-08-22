using Beam.Configuration;
using Beam.Model;
using Beam.Storage;

namespace Beam.Sync;

/// <summary>
/// Background service that keeps the local sync store in step with the
/// filesystem. Performs an initial full scan, then combines a
/// <see cref="FileSystemWatcher"/> for low-latency events with a periodic full
/// rescan to catch missed or racy events. The configured metadata database and
/// its sidecar files are ignored to avoid feedback loops.
/// </summary>
public sealed class ChangeWatcher : BackgroundService
{
    private readonly BeamOptions _options;
    private readonly ISyncStore _store;
    private readonly DirectoryScanner _scanner;
    private readonly ILogger<ChangeWatcher> _logger;
    private readonly HashSet<string> _ignored;
    private readonly StringComparer _pathComparer;

    public ChangeWatcher(
        BeamOptions options,
        ISyncStore store,
        DirectoryScanner scanner,
        ILogger<ChangeWatcher> logger)
    {
        _options = options;
        _store = store;
        _scanner = scanner;
        _logger = logger;
        _pathComparer = PathUtil.Comparer(options.IgnoreCase);
        _ignored = BuildIgnoredPaths(options);
    }

    /// <summary>
    /// Build initial state synchronously during startup so the sync engine
    /// never reconciles against an empty or partial local view.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Rescan();
        _logger.LogInformation("Initial scan complete: {Count} entries", _store.GetEntries().Count);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Change watcher starting for {Directory}", _options.SyncDirectory);

        using var watcher = new FileSystemWatcher(_options.SyncDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        watcher.Created += (_, e) => OnUpsert(e.FullPath);
        watcher.Changed += (_, e) => OnUpsert(e.FullPath);
        watcher.Deleted += (_, e) => OnDelete(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            OnDelete(e.OldFullPath);
            OnUpsert(e.FullPath);
        };

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.RescanIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Rescan();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
        }
    }

    private void Rescan()
    {
        try
        {
            var scanned = _scanner.Scan(_options.SyncDirectory, _ignored);
            var byPath = scanned.ToDictionary(e => e.Path, _pathComparer);

            var changed = 0;
            foreach (var existing in _store.GetEntries())
            {
                if (existing.Tombstone)
                    continue; // tombstones persist until reconciliation settles

                if (!byPath.ContainsKey(existing.Path))
                {
                    // Vanished since the last scan (e.g. a missed watcher event):
                    // record a deletion tombstone so the delete still propagates.
                    var tombstone = new SyncEntry(existing.Path, existing.Type, DirectoryScanner.UtcNowMs(), Tombstone: true);
                    _store.Upsert(tombstone);
                    _store.EnqueueChange(tombstone);
                    changed++;
                }
            }

            foreach (var entry in scanned)
            {
                var old = _store.GetEntry(entry.Path);
                if (old is null || old != entry)
                {
                    _store.Upsert(entry);
                    _store.EnqueueChange(entry);
                    changed++;
                }
            }

            _logger.LogDebug("Rescan: {Entries} entries, {Changed} changed", scanned.Count, changed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic rescan failed");
        }
    }

    private void OnUpsert(string fullPath)
    {
        if (IsIgnored(fullPath))
            return;

        try
        {
            var entry = _scanner.ScanSingle(fullPath, _options.SyncDirectory, _ignored);
            if (entry is not null)
            {
                var old = _store.GetEntry(entry.Path);
                if (old != entry)
                {
                    _store.Upsert(entry);
                    _store.EnqueueChange(entry);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process change event for {Path}", fullPath);
        }
    }

    private void OnDelete(string fullPath)
    {
        if (IsIgnored(fullPath))
            return;

        var relative = DirectoryScanner.ToRelative(
            Path.GetFullPath(_options.SyncDirectory), Path.GetFullPath(fullPath));

        // If it's already a tombstone (e.g. a deletion we applied), keep it.
        var existing = _store.GetEntry(relative);
        if (existing is { Tombstone: true })
            return;

        var type = existing?.Type ?? EntryType.File;
        var tombstone = new SyncEntry(relative, type, DirectoryScanner.UtcNowMs(), Tombstone: true);
        _store.Upsert(tombstone);
        _store.EnqueueChange(tombstone);
    }

    private bool IsIgnored(string fullPath) => _ignored.Contains(Path.GetFullPath(fullPath));

    private static HashSet<string> BuildIgnoredPaths(BeamOptions options)
    {
        var ignored = new HashSet<string>(options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
            return ignored;

        var db = Path.GetFullPath(options.DatabasePath);
        ignored.Add(db);
        ignored.Add(db + "-wal");
        ignored.Add(db + "-shm");
        ignored.Add(db + "-journal");
        return ignored;
    }
}

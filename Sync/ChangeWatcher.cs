using Entangle.Configuration;
using Entangle.Model;
using Entangle.Storage;

namespace Entangle.Sync;

/// <summary>
/// Background service that keeps the local sync store in step with the
/// filesystem. Performs an initial full scan, then combines a
/// <see cref="FileSystemWatcher"/> for low-latency events with a periodic full
/// rescan to catch missed or racy events. The configured metadata database and
/// its sidecar files are ignored to avoid feedback loops.
/// </summary>
public sealed class ChangeWatcher : BackgroundService
{
    private readonly EntangleOptions _options;
    private readonly ISyncStore _store;
    private readonly DirectoryScanner _scanner;
    private readonly IgnoreMatcher _ignore;
    private readonly ILogger<ChangeWatcher> _logger;
    private readonly StringComparer _pathComparer;

    public ChangeWatcher(
        EntangleOptions options,
        ISyncStore store,
        DirectoryScanner scanner,
        IgnoreMatcher ignore,
        ILogger<ChangeWatcher> logger)
    {
        _options = options;
        _store = store;
        _scanner = scanner;
        _ignore = ignore;
        _logger = logger;
        _pathComparer = PathUtil.Comparer(options.IgnoreCase);
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
            var scanned = _scanner.Scan(_options.SyncDirectory, _ignore);
            var byPath = PathUtil.IndexByPath(scanned, _pathComparer, (first, second) =>
                _logger.LogWarning(
                    "Paths {First} and {Second} collide under the active case-sensitivity; "
                    + "keeping the first and ignoring the second",
                    first,
                    second));

            var changed = 0;
            foreach (var existing in _store.GetEntries())
            {
                if (_ignore.IsIgnoredEntry(existing.Path))
                {
                    // The path now matches an ignore pattern (or is the metadata
                    // database). Ignoring is local policy, not a deletion, so
                    // drop the entry instead of recording a tombstone that would
                    // delete the peer's copy. This is checked before the
                    // tombstone branch so tombstones for newly ignored paths are
                    // dropped too, rather than lingering un-reconcilable forever.
                    _store.Remove(existing.Path);
                    changed++;
                    continue;
                }

                if (existing.Tombstone)
                    continue; // tombstones persist until reconciliation settles

                if (!byPath.ContainsKey(existing.Path))
                {
                    // Vanished since the last scan (e.g. a missed watcher event):
                    // record a deletion tombstone so the delete still propagates.
                    var tombstone = new SyncEntry(existing.Path, existing.Type, DirectoryScanner.UtcNowMs(), Tombstone: true);
                    _store.Upsert(tombstone);
                    changed++;
                }
            }

            foreach (var entry in scanned)
            {
                var old = _store.GetEntry(entry.Path);
                if (old is null || old != entry)
                {
                    _store.Upsert(entry);
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
            var entry = _scanner.ScanSingle(fullPath, _options.SyncDirectory, _ignore);
            if (entry is not null)
            {
                var old = _store.GetEntry(entry.Path);
                if (old != entry)
                {
                    _store.Upsert(entry);
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
    }

    private bool IsIgnored(string fullPath) => _ignore.IsIgnored(fullPath);

}
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
        _ignored = BuildIgnoredPaths(options);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Change watcher starting for {Directory}", _options.SyncDirectory);

        Rescan();
        _logger.LogInformation("Initial scan complete: {Count} entries", _store.GetEntries().Count);

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
            var byPath = scanned.ToDictionary(e => e.Path, StringComparer.Ordinal);

            var removed = 0;
            foreach (var existing in _store.GetEntries())
            {
                if (!byPath.ContainsKey(existing.Path))
                {
                    _store.Remove(existing.Path);
                    removed++;
                }
            }

            foreach (var entry in scanned)
            {
                var old = _store.GetEntry(entry.Path);
                if (old is null || old != entry)
                    _store.Upsert(entry);
            }

            _logger.LogDebug("Rescan: {Entries} entries, {Removed} removed", scanned.Count, removed);
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
                _store.Upsert(entry);
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

        _store.Remove(DirectoryScanner.ToRelative(Path.GetFullPath(_options.SyncDirectory), Path.GetFullPath(fullPath)));
    }

    private bool IsIgnored(string fullPath) => _ignored.Contains(Path.GetFullPath(fullPath));

    private static HashSet<string> BuildIgnoredPaths(BeamOptions options)
    {
        var ignored = new HashSet<string>(StringComparer.Ordinal);
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

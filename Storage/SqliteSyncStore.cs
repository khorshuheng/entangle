using Beam.Model;
using Microsoft.Data.Sqlite;

namespace Beam.Storage;

/// <summary>
/// SQLite-backed <see cref="ISyncStore"/> persisting sync entries and the
/// pending-change queue at a configurable path. All access is serialized
/// through a single connection guarded by a semaphore.
/// </summary>
public sealed class SqliteSyncStore : ISyncStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public SqliteSyncStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must not be empty.", nameof(databasePath));

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        CreateSchema();
    }

    public IReadOnlyCollection<SyncEntry> GetEntries() => Run(() =>
    {
        var entries = new List<SyncEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, type, mtime_ms, tombstone, content_hash FROM entries;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            entries.Add(ReadEntry(reader));
        return entries;
    });

    public SyncEntry? GetEntry(string relativePath) => Run<SyncEntry?>(() =>
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT path, type, mtime_ms, tombstone, content_hash FROM entries WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", relativePath);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    });

    public void Upsert(SyncEntry entry) => Run(() =>
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries (path, type, mtime_ms, tombstone, content_hash)
            VALUES ($path, $type, $mtime, $tombstone, $hash)
            ON CONFLICT(path) DO UPDATE SET
                type = excluded.type,
                mtime_ms = excluded.mtime_ms,
                tombstone = excluded.tombstone,
                content_hash = excluded.content_hash;
            """;
        AddEntryParams(cmd, entry);
        cmd.ExecuteNonQuery();
    });

    public void Remove(string relativePath) => Run(() =>
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM entries WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", relativePath);
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyCollection<PendingChange> GetPendingChanges() => Run(() =>
    {
        var changes = new List<PendingChange>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, path, tombstone, created_at_ms FROM pending_changes ORDER BY id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            changes.Add(new PendingChange(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));
        }

        return changes;
    });

    public void EnqueueChange(SyncEntry entry) => Run(() =>
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO pending_changes (path, tombstone, created_at_ms) VALUES ($path, $tombstone, $created);";
        cmd.Parameters.AddWithValue("$path", entry.Path);
        cmd.Parameters.AddWithValue("$tombstone", entry.Tombstone ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    });

    public void ClearPendingChanges() => Run(() =>
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_changes;";
        cmd.ExecuteNonQuery();
    });

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entries (
                path         TEXT PRIMARY KEY,
                type         INTEGER NOT NULL,
                mtime_ms     INTEGER NOT NULL,
                tombstone    INTEGER NOT NULL,
                content_hash TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pending_changes (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                path          TEXT NOT NULL,
                tombstone     INTEGER NOT NULL,
                created_at_ms INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static SyncEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0),
        (EntryType)reader.GetInt64(1),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
        reader.GetInt64(3) != 0,
        reader.GetString(4));

    private static void AddEntryParams(SqliteCommand cmd, SyncEntry entry)
    {
        cmd.Parameters.AddWithValue("$path", entry.Path);
        cmd.Parameters.AddWithValue("$type", (int)entry.Type);
        cmd.Parameters.AddWithValue("$mtime", entry.Mtime.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$tombstone", entry.Tombstone ? 1 : 0);
        cmd.Parameters.AddWithValue("$hash", entry.ContentHash);
    }

    private void Run(Action action)
    {
        _gate.Wait();
        try
        {
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private T Run<T>(Func<T> func)
    {
        _gate.Wait();
        try
        {
            return func();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _connection.Dispose();
        _gate.Dispose();
    }
}

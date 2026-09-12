namespace Entangle.Sync;

/// <summary>
/// Transfer counters for one peer instance. This exists so tests can assert
/// that no content moved (for example, that an unchanged file is never
/// transferred) instead of inferring it from timestamps.
/// </summary>
public sealed class SyncMetrics
{
    private long _pulls;
    private long _pushes;
    private long _bytesPulled;
    private long _bytesPushed;
    private long _entriesRemoved;

    /// <summary>Entries copied from the peer to us (files, directories, tombstones).</summary>
    public long Pulls => Interlocked.Read(ref _pulls);

    /// <summary>Entries copied from us to the peer (files, directories, tombstones).</summary>
    public long Pushes => Interlocked.Read(ref _pushes);

    /// <summary>File bytes received from the peer.</summary>
    public long BytesPulled => Interlocked.Read(ref _bytesPulled);

    /// <summary>File bytes sent to the peer.</summary>
    public long BytesPushed => Interlocked.Read(ref _bytesPushed);

    /// <summary>Tombstones reclaimed locally (not a peer transfer).</summary>
    public long EntriesRemoved => Interlocked.Read(ref _entriesRemoved);

    /// <summary>Total entries that crossed the wire in either direction.</summary>
    public long Transfers => Pulls + Pushes;

    internal void RecordPull(int bytes)
    {
        Interlocked.Increment(ref _pulls);
        Interlocked.Add(ref _bytesPulled, bytes);
    }

    internal void RecordPush(int bytes)
    {
        Interlocked.Increment(ref _pushes);
        Interlocked.Add(ref _bytesPushed, bytes);
    }

    internal void RecordRemove() => Interlocked.Increment(ref _entriesRemoved);
}

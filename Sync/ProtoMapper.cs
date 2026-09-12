using Entangle.Model;
using Entangle.Proto;

namespace Entangle.Sync;

/// <summary>Maps between the domain <see cref="SyncEntry"/> and the wire type.</summary>
public static class ProtoMapper
{
    public static SyncEntryProto ToProto(SyncEntry entry) => new()
    {
        Path = entry.Path,
        Type = entry.Type == EntryType.Directory ? SyncEntryType.Directory : SyncEntryType.File,
        MtimeUnixMs = entry.Mtime.ToUnixTimeMilliseconds(),
        Tombstone = entry.Tombstone,
        ContentHash = entry.ContentHash,
    };

    public static SyncEntry FromProto(SyncEntryProto proto) => new(
        proto.Path,
        proto.Type == SyncEntryType.Directory ? EntryType.Directory : EntryType.File,
        DateTimeOffset.FromUnixTimeMilliseconds(proto.MtimeUnixMs),
        proto.Tombstone,
        proto.ContentHash);
}

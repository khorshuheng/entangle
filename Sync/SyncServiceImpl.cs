using Entangle.Configuration;
using Entangle.Model;
using Entangle.Proto;
using Entangle.Storage;
using Google.Protobuf;
using Grpc.Core;
using SyncRpc = Entangle.Proto.Sync;

namespace Entangle.Sync;

/// <summary>
/// gRPC service exposing this peer's state and file content to the other peer.
/// Reconciliation (deciding what to push/pull) lives in the sync engine; this
/// class only serves transport operations.
/// </summary>
public sealed class SyncServiceImpl : SyncRpc.SyncBase
{
    private readonly EntangleOptions _options;
    private readonly ISyncStore _store;
    private readonly IgnoreMatcher _ignore;
    private readonly ILogger<SyncServiceImpl> _logger;

    public SyncServiceImpl(
        EntangleOptions options,
        ISyncStore store,
        IgnoreMatcher ignore,
        ILogger<SyncServiceImpl> logger)
    {
        _options = options;
        _store = store;
        _ignore = ignore;
        _logger = logger;
    }

    public override Task<StateReply> ExchangeState(StateRequest request, ServerCallContext context)
    {
        _logger.LogDebug("State exchange from peer {PeerId} with {Count} entries", request.PeerId, request.Entries.Count);

        var reply = new StateReply { PeerId = _options.PeerId };
        foreach (var entry in _store.GetEntries())
            reply.Entries.Add(ProtoMapper.ToProto(entry));

        return Task.FromResult(reply);
    }

    public override Task<FileContent> GetFile(FileRequest request, ServerCallContext context)
    {
        var full = ResolveWithinRoot(request.Path);

        if (Directory.Exists(full))
        {
            return Task.FromResult(new FileContent
            {
                Path = request.Path,
                IsDirectory = true,
                MtimeUnixMs = new DateTimeOffset(Directory.GetLastWriteTimeUtc(full), TimeSpan.Zero).ToUnixTimeMilliseconds(),
            });
        }

        if (!File.Exists(full))
            throw new RpcException(new Status(StatusCode.NotFound, $"File not found: {request.Path}"));

        var bytes = File.ReadAllBytes(full);
        return Task.FromResult(new FileContent
        {
            Path = request.Path,
            Content = ByteString.CopyFrom(bytes),
            MtimeUnixMs = new DateTimeOffset(File.GetLastWriteTimeUtc(full), TimeSpan.Zero).ToUnixTimeMilliseconds(),
        });
    }

    public override async Task<PutFileReply> PutFile(FileContent request, ServerCallContext context)
    {
        var full = ResolveWithinRoot(request.Path);

        // Refuse anything this instance excludes. A peer whose configuration
        // differs would otherwise push paths we deliberately ignore onto our
        // disk, including a file named like our own metadata database.
        if (_ignore.IsIgnored(full))
        {
            _logger.LogWarning(
                "Refused {Path}: it is excluded here (ignore pattern or metadata database)",
                request.Path);
            return new PutFileReply { Accepted = false };
        }

        if (request.Tombstone)
        {
            var existing = _store.GetEntry(request.Path);
            var type = existing?.Type ?? EntryType.File;
            var deletedAt = DateTimeOffset.FromUnixTimeMilliseconds(request.MtimeUnixMs);

            PathUtil.DeletePathAndPruneEmptyParents(_options.SyncDirectory, full, _options.IgnoreCase);
            _store.Upsert(new SyncEntry(request.Path, type, deletedAt, Tombstone: true));

            return new PutFileReply { Accepted = true };
        }

        if (request.IsDirectory)
        {
            if (File.Exists(full))
                File.Delete(full);
            Directory.CreateDirectory(full);

            // Record the state we just created, exactly as the scanner would
            // compute it, so a missed watcher event cannot leave the store stale.
            _store.Upsert(new SyncEntry(
                request.Path,
                EntryType.Directory,
                DirectoryScanner.MtimeOfDirectory(full)));

            return new PutFileReply { Accepted = true };
        }

        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);

        var mtime = request.MtimeUnixMs != 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(request.MtimeUnixMs)
            : (DateTimeOffset?)null;

        var content = request.Content.ToByteArray();
        await PathUtil.WriteAllBytesAtomicAsync(full, content, mtime);

        // Record the converged entry immediately. Relying on the watcher alone
        // leaves the store stale whenever an event is dropped, which makes the
        // next reconcile pass re-pull a file we already have.
        _store.Upsert(new SyncEntry(
            request.Path,
            EntryType.File,
            mtime ?? DirectoryScanner.MtimeOfFile(full),
            false,
            ContentHasher.HashContent(content)));

        return new PutFileReply { Accepted = true };
    }

    /// <summary>
    /// Resolve a sync-relative path against the synced root, rejecting any path
    /// that escapes the root (absolute paths or ".." traversal).
    /// </summary>
    private string ResolveWithinRoot(string relativePath)
    {
        try
        {
            return PathUtil.ResolveWithinRoot(_options.SyncDirectory, relativePath, _options.IgnoreCase);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }
}

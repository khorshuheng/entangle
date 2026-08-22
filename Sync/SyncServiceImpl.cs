using Beam.Configuration;
using Beam.Model;
using Beam.Proto;
using Beam.Storage;
using Google.Protobuf;
using Grpc.Core;
using SyncRpc = Beam.Proto.Sync;

namespace Beam.Sync;

/// <summary>
/// gRPC service exposing this peer's state and file content to the other peer.
/// Reconciliation (deciding what to push/pull) lives in the sync engine; this
/// class only serves transport operations.
/// </summary>
public sealed class SyncServiceImpl : SyncRpc.SyncBase
{
    private readonly BeamOptions _options;
    private readonly ISyncStore _store;
    private readonly ILogger<SyncServiceImpl> _logger;

    public SyncServiceImpl(BeamOptions options, ISyncStore store, ILogger<SyncServiceImpl> logger)
    {
        _options = options;
        _store = store;
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

    public override Task<PutFileReply> PutFile(FileContent request, ServerCallContext context)
    {
        var full = ResolveWithinRoot(request.Path);

        if (request.IsDirectory)
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
            File.WriteAllBytes(full, request.Content.ToByteArray());
            if (request.MtimeUnixMs != 0)
            {
                var mtime = DateTimeOffset.FromUnixTimeMilliseconds(request.MtimeUnixMs).UtcDateTime;
                File.SetLastWriteTimeUtc(full, mtime);
            }
        }

        return Task.FromResult(new PutFileReply { Accepted = true });
    }

    /// <summary>
    /// Resolve a sync-relative path against the synced root, rejecting any path
    /// that escapes the root (absolute paths or ".." traversal).
    /// </summary>
    private string ResolveWithinRoot(string relativePath)
    {
        try
        {
            return PathUtil.ResolveWithinRoot(_options.SyncDirectory, relativePath);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }
}

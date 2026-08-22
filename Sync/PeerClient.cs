using Beam.Model;
using Beam.Proto;
using Google.Protobuf;
using Grpc.Net.Client;
using SyncRpc = Beam.Proto.Sync;

namespace Beam.Sync;

/// <summary>
/// gRPC client for talking to the configured peer. Wraps the generated client
/// and translates between wire types and the domain model.
/// </summary>
public sealed class PeerClient : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly SyncRpc.SyncClient _client;

    static PeerClient()
    {
        // Allow plaintext (h2c) HTTP/2, which is how Beam peers talk to each
        // other on a LAN without TLS.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    public PeerClient(string peerAddress)
    {
        if (string.IsNullOrWhiteSpace(peerAddress))
            throw new ArgumentException("Peer address must not be empty.", nameof(peerAddress));

        _channel = GrpcChannel.ForAddress(peerAddress);
        _client = new SyncRpc.SyncClient(_channel);
    }

    /// <summary>Send our state and receive the peer's id and current state.</summary>
    public async Task<(string PeerId, IReadOnlyList<SyncEntry> Entries)> ExchangeStateAsync(
        string myPeerId,
        IReadOnlyCollection<SyncEntry> entries,
        CancellationToken ct = default)
    {
        var request = new StateRequest { PeerId = myPeerId };
        foreach (var entry in entries)
            request.Entries.Add(ProtoMapper.ToProto(entry));

        var reply = await _client.ExchangeStateAsync(request, cancellationToken: ct);
        return (reply.PeerId, reply.Entries.Select(ProtoMapper.FromProto).ToList());
    }

    /// <summary>Fetch a file's content and mtime from the peer.</summary>
    public async Task<(byte[] Content, DateTimeOffset Mtime, bool IsDirectory)> GetFileAsync(
        string path,
        CancellationToken ct = default)
    {
        var reply = await _client.GetFileAsync(new FileRequest { Path = path }, cancellationToken: ct);
        return (reply.Content.ToByteArray(), DateTimeOffset.FromUnixTimeMilliseconds(reply.MtimeUnixMs), reply.IsDirectory);
    }

    /// <summary>Push a file's content (or an empty directory) to the peer.</summary>
    public async Task PutFileAsync(
        string path,
        byte[] content,
        DateTimeOffset mtime,
        bool isDirectory,
        CancellationToken ct = default)
    {
        var request = new FileContent
        {
            Path = path,
            Content = ByteString.CopyFrom(content),
            MtimeUnixMs = mtime.ToUnixTimeMilliseconds(),
            IsDirectory = isDirectory,
        };

        await _client.PutFileAsync(request, cancellationToken: ct);
    }

    /// <summary>Ask the peer to delete a path (a deletion tombstone).</summary>
    public async Task DeleteAsync(string path, DateTimeOffset mtime, CancellationToken ct = default)
    {
        var request = new FileContent
        {
            Path = path,
            MtimeUnixMs = mtime.ToUnixTimeMilliseconds(),
            Tombstone = true,
        };

        await _client.PutFileAsync(request, cancellationToken: ct);
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}

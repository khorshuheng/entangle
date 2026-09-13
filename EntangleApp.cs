using System.Net;
using Entangle.Configuration;
using Entangle.Storage;
using Entangle.Sync;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Entangle;

/// <summary>Builds a configured Entangle peer <see cref="WebApplication"/>.</summary>
public static class EntangleApp
{
    /// <summary>
    /// Builds the peer on <paramref name="builder"/>, which the caller has already
    /// configured, so the host and the peer share one configuration root instead of
    /// the host reading a second, different one.
    /// </summary>
    public static WebApplication Build(WebApplicationBuilder builder, EntangleOptions options)
    {
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<SyncMetrics>();
        builder.Services.AddSingleton(new IgnoreMatcher(
            options.SyncDirectory,
            options.DatabasePath,
            options.EffectiveIgnorePatterns,
            options.IgnoreCase));
        builder.Services.AddGrpc(grpc =>
        {
            // gRPC's 4 MiB default would silently cap the file size this
            // service can move; align both directions with the configured cap.
            grpc.MaxReceiveMessageSize = options.MaxMessageSizeBytes;
            grpc.MaxSendMessageSize = options.MaxMessageSizeBytes;
        });
        builder.Services.AddSingleton<DirectoryScanner>();
        builder.Services.AddSingleton<ISyncStore>(_ => new SqliteSyncStore(options.DatabasePath, options.IgnoreCase));
        builder.Services.AddHostedService<ChangeWatcher>();
        builder.Services.AddSingleton(new PeerClient(options.PeerAddress, options.MaxMessageSizeBytes));
        builder.Services.AddHostedService<SyncEngine>();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            void Http2(ListenOptions listen) => listen.Protocols = HttpProtocols.Http2;

            // Loopback by default: the API is unauthenticated, so being reachable
            // from a network is an explicit choice through Entangle:BindAddress.
            if (options.BindAddress.Equals(EntangleOptions.AnyBindAddress, StringComparison.OrdinalIgnoreCase))
                kestrel.ListenAnyIP(options.Port, Http2);
            else if (options.BindAddress.Equals(EntangleOptions.LoopbackBindAddress, StringComparison.OrdinalIgnoreCase))
                kestrel.ListenLocalhost(options.Port, Http2);
            else
                kestrel.Listen(IPAddress.Parse(options.BindAddress), options.Port, Http2);
        });

        var app = builder.Build();

        app.MapGrpcService<SyncServiceImpl>();
        app.MapGet("/", () => "Entangle sync service");

        return app;
    }
}

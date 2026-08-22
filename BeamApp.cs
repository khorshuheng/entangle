using Beam.Configuration;
using Beam.Storage;
using Beam.Sync;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Beam;

/// <summary>Builds a configured Beam peer <see cref="WebApplication"/>.</summary>
public static class BeamApp
{
    public static WebApplication Build(BeamOptions options)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddSingleton(options);
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<DirectoryScanner>();
        builder.Services.AddSingleton<ISyncStore>(_ => new SqliteSyncStore(options.DatabasePath, options.IgnoreCase));
        builder.Services.AddHostedService<ChangeWatcher>();
        builder.Services.AddSingleton(new PeerClient(options.PeerAddress));
        builder.Services.AddHostedService<SyncEngine>();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(options.Port, listen => listen.Protocols = HttpProtocols.Http2);
        });

        var app = builder.Build();

        app.MapGrpcService<SyncServiceImpl>();
        app.MapGet("/", () => "Beam sync service");

        return app;
    }
}

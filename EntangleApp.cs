using Entangle.Configuration;
using Entangle.Storage;
using Entangle.Sync;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Entangle;

/// <summary>Builds a configured Entangle peer <see cref="WebApplication"/>.</summary>
public static class EntangleApp
{
    public static WebApplication Build(EntangleOptions options)
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
        app.MapGet("/", () => "Entangle sync service");

        return app;
    }
}

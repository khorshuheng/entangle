using Beam.Configuration;
using Beam.Storage;
using Beam.Sync;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// Load and validate configuration, failing fast on invalid values.
var beam = builder.Configuration.GetSection("Beam").Get<BeamOptions>() ?? new BeamOptions();
var errors = beam.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors)
        Console.Error.WriteLine($"Beam configuration error: {error}");

    throw new InvalidOperationException("Invalid Beam configuration: " + string.Join("; ", errors));
}

// Resolve relative paths once, against the content root, so every consumer
// (scanner, watcher, store) operates on the same absolute paths.
beam.SyncDirectory = Path.GetFullPath(beam.SyncDirectory);
beam.DatabasePath = Path.GetFullPath(beam.DatabasePath);

// The sync directory must be usable before we start watching it.
try
{
    Directory.CreateDirectory(beam.SyncDirectory);
}
catch (Exception ex)
{
    throw new InvalidOperationException(
        $"Beam:SyncDirectory '{beam.SyncDirectory}' is not usable: {ex.Message}", ex);
}

builder.Services.AddSingleton(beam);
builder.Services.AddGrpc();
builder.Services.AddSingleton<DirectoryScanner>();
builder.Services.AddSingleton<ISyncStore>(_ => new SqliteSyncStore(beam.DatabasePath, beam.IgnoreCase));
builder.Services.AddHostedService<ChangeWatcher>();
builder.Services.AddSingleton(new PeerClient(beam.PeerAddress));
builder.Services.AddHostedService<SyncEngine>();

// Listen on the configured port for plaintext HTTP/2 gRPC. Bind all
// interfaces so a peer on another machine can reach us.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(beam.Port, listen => listen.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();

app.MapGrpcService<SyncServiceImpl>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();

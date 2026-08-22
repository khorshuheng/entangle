using Beam.Configuration;
using Beam.Services;
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

// Listen on the configured port for plaintext HTTP/2 gRPC. Bind all
// interfaces so a peer on another machine can reach us.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(beam.Port, listen => listen.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();

app.MapGrpcService<GreeterService>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();

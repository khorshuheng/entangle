using Beam;
using Beam.Configuration;

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

BeamApp.Build(beam).Run();

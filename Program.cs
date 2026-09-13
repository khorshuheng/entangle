using Entangle;
using Entangle.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Load configuration, failing fast on invalid values.
var entangle = builder.Configuration.GetSection("Entangle").Get<EntangleOptions>() ?? new EntangleOptions();

// First start in a working directory that has no configuration: the working
// directory holds the synced files and the state database, so the peer runs
// unattended. All other starts read the file as it is, however the user has since
// edited it.
if (FirstRunConfig.InitializeIfMissing(builder.Environment.ContentRootPath, entangle, out var configPath))
    Console.WriteLine($"Entangle: wrote default configuration to {configPath}");

var errors = entangle.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors)
        Console.Error.WriteLine($"Entangle configuration error: {error}");

    throw new InvalidOperationException("Invalid Entangle configuration: " + string.Join("; ", errors));
}

// Resolve relative paths once, against the content root, so every consumer
// (scanner, watcher, store) operates on the same absolute paths.
entangle.SyncDirectory = Path.GetFullPath(entangle.SyncDirectory);
entangle.DatabasePath = Path.GetFullPath(entangle.DatabasePath);

// The sync directory must be usable before we start watching it.
try
{
    Directory.CreateDirectory(entangle.SyncDirectory);
}
catch (Exception ex)
{
    throw new InvalidOperationException(
        $"Entangle:SyncDirectory '{entangle.SyncDirectory}' is not usable: {ex.Message}", ex);
}

EntangleApp.Build(entangle).Run();

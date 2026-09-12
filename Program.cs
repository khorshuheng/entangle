using Entangle;
using Entangle.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Load and validate configuration, failing fast on invalid values.
var entangle = builder.Configuration.GetSection("Entangle").Get<EntangleOptions>() ?? new EntangleOptions();
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

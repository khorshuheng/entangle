using Entangle;
using Entangle.Configuration;

// Answer help and version first, before a builder or any file exists: asking a
// question must never start a service or leave anything behind.
var parsed = CommandLine.Parse(args);
switch (parsed.Command)
{
    case Command.Run:
        break;

    case Command.Help:
        Console.WriteLine(CommandLine.Usage);
        return 0;

    case Command.Version:
        Console.WriteLine(CommandLine.Version);
        return 0;

    case Command.UsageError:
        foreach (var argument in parsed.Arguments)
            Console.Error.WriteLine($"entangle: unrecognised argument '{argument}'");

        Console.Error.WriteLine();
        Console.Error.WriteLine(CommandLine.Usage);
        return 2;
}

var builder = WebApplication.CreateBuilder(parsed.Arguments.ToArray());

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

    return 1;
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
    Console.Error.WriteLine(
        $"Entangle configuration error: Entangle:SyncDirectory '{entangle.SyncDirectory}' is not usable: {ex.Message}");

    return 1;
}

EntangleApp.Build(entangle).Run();
return 0;

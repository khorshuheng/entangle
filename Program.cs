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

// The state directory has to be known before configuration is read — it is where
// the configuration file lives — so it comes from the command line or the
// environment, not from the file itself.
var stateDirectory = FirstRunConfig.ResolveStateDirectory(parsed.StateDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = parsed.Arguments.ToArray() });

// Configuration comes from the state directory's appsettings.json, the
// environment, and the command line — never from the working directory — so a peer
// behaves the same wherever it is started. The sources ASP.NET added by default (a
// working-directory appsettings.json and appsettings.{env}.json) are dropped so
// the same configuration root feeds both this binding and the host.
builder.Configuration.Sources.Clear();
FirstRunConfig.AddConfig(builder.Configuration, stateDirectory);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(parsed.Arguments.ToArray());

// Load configuration, failing fast on invalid values.
var entangle = builder.Configuration.GetSection("Entangle").Get<EntangleOptions>() ?? new EntangleOptions();

// First start with no configuration in the state directory: the synced files live
// in the working directory and the configuration and database in the state
// directory. All other starts read the file as the user has since edited it.
if (FirstRunConfig.InitializeIfMissing(stateDirectory, entangle, out var configPath))
    Console.WriteLine($"Entangle: wrote default configuration to {configPath}");

// The database is collocated with the configuration by default; derive it when an
// existing file does not name one.
if (string.IsNullOrWhiteSpace(entangle.DatabasePath))
    entangle.DatabasePath = FirstRunConfig.DefaultDatabasePath(stateDirectory);

var errors = entangle.Validate();
if (errors.Count > 0)
{
    foreach (var error in errors)
        Console.Error.WriteLine($"Entangle configuration error: {error}");

    return 1;
}

// The peer's address cannot be guessed, so a peer that has never been given one
// stops the run, before the sync directory, the store, and the port. Dialling a
// placeholder forever would present a missing setting as a network problem.
//
// This check comes after validation on purpose: reporting "no peer configured" for
// a configuration that is broken elsewhere would hide the real error, and on a
// first start it would point at a file that validation prevented from being
// written.
if (!entangle.PeerIsConfigured)
{
    Console.Error.WriteLine(PeerReminder.Compose(configPath));
    return 1;
}

// Resolve relative paths once, against the content root, so every consumer
// (scanner, watcher, store) operates on the same absolute paths.
entangle.SyncDirectory = Path.GetFullPath(entangle.SyncDirectory);
entangle.DatabasePath = Path.GetFullPath(entangle.DatabasePath);

// The state database lives outside the synced tree by default, so its directory
// has to exist before SQLite opens it: a missing one surfaces as an unhandled
// SqliteException rather than as a configuration error. It is created before the
// sync directory so that a failure here — a read-only home, most likely — cannot
// leave an empty directory behind in the user's synced data.
var databaseDirectory = Path.GetDirectoryName(entangle.DatabasePath);
try
{
    if (!string.IsNullOrEmpty(databaseDirectory))
        Directory.CreateDirectory(databaseDirectory);
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"Entangle configuration error: Entangle:DatabasePath '{entangle.DatabasePath}' is not usable: {ex.Message}");

    return 1;
}

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

EntangleApp.Build(builder, entangle).Run();
return 0;

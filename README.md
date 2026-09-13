# Entangle

A bidirectional file/directory sync service built as a pair of peer gRPC
services on .NET 10. Each peer watches a configurable directory and keeps it
in sync with the other, with last-write-wins conflict resolution, deletion
propagation via tombstones, and eventual consistency across offline periods.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- ASP.NET Core runtime / targeting pack (included with the SDK on most platforms)

### Arch Linux

The ASP.NET Core components ship as separate packages on Arch:

```sh
sudo pacman -S aspnet-targeting-pack aspnet-runtime
```

## Configuration

Configuration is read from `appsettings.json` in the state directory,
environment variables (`Entangle__Key`), and CLI args (`--Entangle:Key`), in that
order of precedence. The state directory is `~/.entangle` on Linux and
`%LOCALAPPDATA%\entangle` on Windows, and holds both the configuration and the
SQLite database; `--state-dir <dir>` or `ENTANGLE_STATE_DIR` chooses another one.
The working directory is never read for configuration, so a peer behaves the same
wherever it is started:

| Key                    | Description                                              |
| ---------------------- | -------------------------------------------------------- |
| `SyncDirectory`        | Local directory to keep in sync (required; first start uses `./entangled`) |
| `DatabasePath`         | Path to the local SQLite state database (optional; defaults to `entangle.db` in the state directory, next to `appsettings.json`) |
| `Port`                 | gRPC listen port (default `5000`)                        |
| `BindAddress`          | Address to listen on: `loopback` (default; `127.0.0.1` and `[::1]`), `any` (every interface), or a literal IP address |
| `PeerAddress`          | Address of the peer instance, as an absolute http(s) URL (required; first start writes the marker `unset`, which stops the run with a reminder) |
| `PeerId`               | Stable unique id, used for LWW tie-break (required; first start uses `peer-<hostname>`) |
| `RescanIntervalSeconds`| Periodic full rescan interval (default `30`)             |
| `SyncIntervalSeconds`  | Interval between reconcile passes (default `5`)          |
| `MaxBackoffSeconds`    | Max retry backoff while the peer is unreachable (default `60`) |
| `MaxMessageSizeBytes`  | Max gRPC message size in bytes, i.e. the largest file that can sync (default `67108864`, 64 MiB) |
| `TombstoneRetentionDays` | Days to keep a deletion tombstone for a path the peer has no record of; `0` (default) reclaims it immediately. Tombstones both sides agree on are always reclaimed immediately |
| `IgnorePatterns`       | Glob patterns excluded from sync (default `[ ".git/", "node_modules/" ]`; a configured list replaces them) |
| `IgnoreCase`           | Case-insensitive path handling (default on Windows)      |

The service has no authentication or transport encryption, so by default it
listens on loopback only. Syncing between two machines means setting
`BindAddress` to `any` (or the address of the interface facing the peer), which
exposes the API to everything that can reach that address and port. Do that only
on a network you trust, or — better — reach the peer over a tunnel or VPN (for
example Tailscale or an SSH port forward) and leave the listener on loopback.

Run two peers by giving each its own state directory — and so its own
configuration, database, and port — with the two pointing at each other:

```sh
entangle run --state-dir ~/.entangle/peer-a --Entangle:Port=5000 --Entangle:PeerId=peer-a \
  --Entangle:PeerAddress=http://localhost:5001
entangle run --state-dir ~/.entangle/peer-b --Entangle:Port=5001 --Entangle:PeerId=peer-b \
  --Entangle:PeerAddress=http://localhost:5000
```

Both would otherwise generate the same `peer-<hostname>` id, so set `PeerId`
explicitly in at least one: with equal ids the equal-mtime tie-break cannot pick a
winner, and the two copies would not converge.

### First start

On first start, when the state directory has no appsettings.json, Entangle writes
one there: `./entangled` as the sync directory, `entangle.db` beside the
configuration as the state database, port `5000`, and a `PeerId` taken from the
machine name (`peer-<hostname>`). Anything supplied by environment variables or
CLI args is kept and recorded, so a start with `--Entangle:SyncDirectory=/data`
writes `/data` into the file. The file is only ever written when it is absent and
the options are valid, so a failed start leaves nothing behind.

Nothing is created in the working directory. The configuration and the database
live together in the state directory, so a peer's setup and its state move as a
unit, and the database stays out of the synced tree — which also keeps SQLite off
a mounted filesystem, where its locking cannot be relied on.

A peer's database describes the tree it was last synced with, so repointing a
peer at a different tree is not just a configuration edit. Changing
`SyncDirectory`, or running from a different working directory while it is the
relative default, reuses the old records: the peer sees the previous tree's paths
as deletions and propagates them. Give each tree its own state directory
(`--state-dir`), or delete `entangle.db` when repointing a peer.

The peer address is the exception, because it cannot be guessed: the file gets
the marker `"PeerAddress": "unset"` and the run stops there, printing which file
to edit and how. Nothing else is created — no sync directory, no database, no
listening port — until an address is there:

```sh
entangle run                                              # writes the file, stops
entangle run --Entangle:PeerAddress=http://otherhost:5000  # or say it up front
```

After that the file is yours: it is never rewritten. Edit it to change the
setup, or override individual keys from the environment or the command line.

### Ignored paths

`IgnorePatterns` uses globs matched against sync-relative paths. A configured
list replaces the built-in defaults; an empty list does not, because
configuration cannot tell `[]` apart from an unset key, so the defaults stay in
force. To exclude nothing at all, configure a single blank entry (`[ "" ]`),
which matches nothing.

| Pattern          | Matches                                                       |
| ---------------- | ------------------------------------------------------------- |
| `node_modules/`  | that name at any depth, plus everything under it                |
| `*.log`          | matching file (or directory) names at any depth                 |
| `build/output`   | matched from the root, because it contains a separator           |
| `/vendor`        | explicitly anchored at the root                                  |
| `**/*.tmp`       | `**` spans segments, `*` stays within one segment                |

The metadata database and its SQLite sidecars are always ignored, as are the
temporary files used for atomic writes. **Ignoring is local policy, not a
deletion**: a path that starts matching a pattern stops being tracked here but
is left untouched on the peer.

Symlinks are not followed and are not synced.

### Case-colliding paths

With `IgnoreCase` enabled (the default on Windows), paths that differ only in
case are treated as the same path. If a case-sensitive filesystem holds both
`Foo` and `foo`, the first one encountered wins and a warning naming both paths
is logged; the sync loop keeps running rather than aborting.

## Build

For development:

```sh
make          # linux: publish/linux-x64/entangle
build.cmd     # windows: publish\win-x64\entangle.exe
```

Both publish a single framework-dependent binary (managed assemblies and the
native SQLite library bundled in). No appsettings.json ships with it: the binary
reads its configuration from the state directory, which a first start creates.
Both accept `test`, `clean`, and an explicit runtime identifier (`make RID=linux-arm64`,
`build.cmd win-arm64`). Framework-dependent means the machine that runs the
result needs the ASP.NET Core 10 runtime, not just the .NET runtime.

Plain MSBuild still works:

```sh
dotnet build
```

## Run

```sh
entangle run         # installed binary
dotnet run -- run    # from source
```

Starting is explicit: a bare `entangle`, `entangle --help`, and
`entangle --version` print usage or the version and exit without writing
configuration, creating a sync directory or database, or binding a port.

Options to `run` are configuration, and take precedence over the state
directory's appsettings.json and the environment:

```sh
entangle run --Entangle:Port=5001 --Entangle:PeerAddress=http://otherhost:5000
```

`--state-dir <dir>` (or `ENTANGLE_STATE_DIR`) selects a different state directory,
which is how a second peer runs on the same host:

Exit codes: `0` success (help and version included), `1` configuration problem,
`2` usage error.

The service listens on the configured port using plaintext HTTP/2 gRPC, on
loopback unless `BindAddress` says otherwise (see above).

## Test

```sh
make test        # or: build.cmd test
```

or directly:

```sh
dotnet test
```

Includes unit tests for reconcile/LWW logic and a linux-linux integration test
running two in-process peers verifying add/modify/delete propagation, LWW
conflict resolution, and offline re-sync.

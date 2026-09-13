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

Configuration is read from appsettings.json, environment variables
(`Entangle__Key`), or CLI args (`--Entangle:Key`):

| Key                    | Description                                              |
| ---------------------- | -------------------------------------------------------- |
| `SyncDirectory`        | Local directory to keep in sync (required; first start uses `./entangled`) |
| `DatabasePath`         | Path to the local SQLite state database (required; first start uses `./entangle.db`) |
| `Port`                 | gRPC listen port (default `5000`)                        |
| `PeerAddress`          | Address of the peer instance (required; first start uses `http://localhost:5001`) |
| `PeerId`               | Stable unique id, used for LWW tie-break (required; first start uses `peer-<hostname>`) |
| `RescanIntervalSeconds`| Periodic full rescan interval (default `30`)             |
| `SyncIntervalSeconds`  | Interval between reconcile passes (default `5`)          |
| `MaxBackoffSeconds`    | Max retry backoff while the peer is unreachable (default `60`) |
| `MaxMessageSizeBytes`  | Max gRPC message size in bytes, i.e. the largest file that can sync (default `67108864`, 64 MiB) |
| `TombstoneRetentionDays` | Days to keep a deletion tombstone for a path the peer has no record of; `0` (default) reclaims it immediately. Tombstones both sides agree on are always reclaimed immediately |
| `IgnorePatterns`       | Glob patterns excluded from sync (default `[ ".git/", "node_modules/" ]`; a configured list replaces them) |
| `IgnoreCase`           | Case-insensitive path handling (default on Windows)      |

Run two peers on the same host with different directories, ports, and peer ids
pointing at each other. Both would generate the same `peer-<hostname>` id, so
set `PeerId` explicitly in at least one of them: with equal ids the equal-mtime
tie-break cannot pick a winner, and the two copies would not converge.

### First start

On first start in a working directory that has no appsettings.json, Entangle
writes one: `./entangled` as the sync directory, `./entangle.db` as the state
database, port `5000`, peer address `http://localhost:5001`, and a `PeerId`
taken from the machine name (`peer-<hostname>`). Anything supplied by
environment variables or CLI args is kept and recorded, so a start with
`--Entangle:SyncDirectory=/data` writes `/data` into the file. The file is only
ever written when it is absent and the options are valid, so a failed start
leaves nothing behind.

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
native SQLite library bundled in) plus the appsettings.json next to it, and both
accept `test`, `clean`, and an explicit runtime identifier (`make RID=linux-arm64`,
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

Options to `run` are configuration, and take precedence over appsettings.json
and the environment:

```sh
entangle run --Entangle:Port=5001 --Entangle:PeerAddress=http://otherhost:5000
```

Exit codes: `0` success (help and version included), `1` configuration problem,
`2` usage error.

The service listens on the configured port using plaintext HTTP/2 gRPC.

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

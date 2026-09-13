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
| `SyncDirectory`        | Local directory to keep in sync (required)               |
| `DatabasePath`         | Path to the local SQLite state database (required)       |
| `Port`                 | gRPC listen port (default `5000`)                        |
| `PeerAddress`          | Address of the peer instance (required)                  |
| `PeerId`               | Stable unique id, used for LWW tie-break (required)      |
| `RescanIntervalSeconds`| Periodic full rescan interval (default `30`)             |
| `SyncIntervalSeconds`  | Interval between reconcile passes (default `5`)          |
| `MaxBackoffSeconds`    | Max retry backoff while the peer is unreachable (default `60`) |
| `MaxMessageSizeBytes`  | Max gRPC message size in bytes, i.e. the largest file that can sync (default `67108864`, 64 MiB) |
| `TombstoneRetentionDays` | Days to keep a deletion tombstone for a path the peer has no record of; `0` (default) reclaims it immediately. Tombstones both sides agree on are always reclaimed immediately |
| `IgnorePatterns`       | Glob patterns excluded from sync (default `[ ".git/", "node_modules/" ]`; a configured list replaces them) |
| `IgnoreCase`           | Case-insensitive path handling (default on Windows)      |

Run two peers on the same host with different directories, ports, and peer ids
pointing at each other.

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

```sh
dotnet build
```

## Run

```sh
dotnet run
```

The service listens on the configured port using plaintext HTTP/2 gRPC.

## Test

```sh
dotnet test
```

Includes unit tests for reconcile/LWW logic and a linux-linux integration test
running two in-process peers verifying add/modify/delete propagation, LWW
conflict resolution, and offline re-sync.

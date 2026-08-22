# Beam

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
(`Beam__Key`), or CLI args (`--Beam:Key`):

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
| `IgnoreCase`           | Case-insensitive path handling (default on Windows)      |

Run two peers on the same host with different directories, ports, and peer ids
pointing at each other.

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

# beam

A gRPC service built on .NET 10.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- ASP.NET Core runtime / targeting pack (included with the SDK on most platforms)

### Arch Linux

The ASP.NET Core components ship as separate packages on Arch:

```sh
sudo pacman -S aspnet-targeting-pack aspnet-runtime
```

## Build

```sh
dotnet build
```

## Run

```sh
dotnet run
```

The service listens on `http://localhost:5000` (HTTP/2) by default.

## Service

The default template exposes a `Greeter` service defined in `Protos/greet.proto`.

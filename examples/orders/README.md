# Example: orders

A tiny ASP.NET Core minimal API whose configuration is declared with docuconf. It shows the three things the .NET SDK
gives an app:

- an ordinary options class with DataAnnotations, plus docuconf attributes for descriptions and secrets
  ([`OrdersOptions.cs`](OrdersOptions.cs));
- one check at startup that reports every problem at once, with stable codes ([`Program.cs`](Program.cs));
- a CUE contract exported from the options class, for the platform to validate before it deploys
  ([`contract.cue`](contract.cue)).

The options bind from the `Orders` section, so each setting is an `ORDERS__*` environment variable:

| Variable | Type | Rules |
|---|---|---|
| `ORDERS__PORT` | int | 1–65535, default `8080` |
| `ORDERS__LOGLEVEL` | enum | `debug`, `info`, `warn`, `error`; default `info` |
| `ORDERS__DATABASEURL` | url | secret, required, scheme `postgres` |
| `ORDERS__ALLOWEDORIGINS__0`, `__1`, ... | list of strings | at least 1 item; default `["http://localhost:3000"]` |
| `ORDERS__REQUESTTIMEOUT` | duration, `hh:mm:ss` | `00:00:01`–`00:05:00`, default `00:00:30` |
| `ORDERS__WORKERCOUNT` | int | 1–64, default `4` |

[`Orders.Api.csproj`](Orders.Api.csproj) builds against the SDK in this repository with a `ProjectReference`.

## Run it

```console
$ cd examples/orders
$ ORDERS__DATABASEURL=postgres://orders:pw@localhost:5432/orders dotnet run
$ curl localhost:8080/healthz
ok
$ curl localhost:8080/config
{"port":8080,"logLevel":"info","databaseUrl":"***","allowedOrigins":["http://localhost:3000"],"requestTimeout":"00:00:30","workerCount":4}
```

`/config` shows the typed values; the secret is always `***`.

## When the configuration is wrong

With `ORDERS__PORT=0` and no `ORDERS__DATABASEURL`, the service refuses to start, exits 1 and lists every problem, not
just the first:

```console
$ ORDERS__PORT=0 dotnet run
Invalid configuration:
  [missing_required] ORDERS__DATABASEURL: is required (Orders:DatabaseUrl)
  [out_of_range] ORDERS__PORT: The field Port must be between 1 and 65535.
```

In Kubernetes the same lines go to `/dev/termination-log`, so `kubectl describe pod` shows them.
[`smoke.sh`](smoke.sh) checks both runs; CI runs it on every push.

## Export the contract

`contract.cue` is generated; never edit it by hand. Re-export it after changing `OrdersOptions.cs` (CI fails if it is
out of date):

```console
$ cd examples/orders
$ dotnet build -c Release
$ dotnet bin/Release/net10.0/Orders.Api.dll docuconf export contract.cue
Wrote contract.cue
```

Run the export against the build or publish output: the contract includes any `appsettings*.json` files that ship
with the app (this example has none).

## Deploy

The app ships `contract.cue`, and the platform checks its inputs against it before anything reaches the cluster:
`docuconf vet` reports every bad or missing value, secret given as a literal or policy violation, and
`docuconf render` turns valid inputs into the pod's env, writing the list as `ORDERS__ALLOWEDORIGINS__0`, ... and the
timeout as `00:00:30` because the contract says .NET reads them that way. A Crossplane composition can evaluate the
same contract in plain CUE, and a Helm-based platform can use the
[docuconf Helm chart](https://github.com/docuconf/docuconf-go/tree/main/helm), which generates a `values.schema.json`
from the contract.

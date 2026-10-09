# Example: orders

A tiny ASP.NET Core minimal API whose configuration is declared with docuconf. It shows the three things the .NET SDK
gives an app:

- an ordinary options class with DataAnnotations, plus docuconf attributes for descriptions and secrets; `WorkerCount`
  is documented with an XML doc comment instead, whose `<remarks>` become the contract's `details`
  ([`OrdersOptions.cs`](OrdersOptions.cs));
- one check at startup that reports every problem at once, with stable codes, and exits 1
  ([`Program.cs`](Program.cs));
- a CUE contract exported from the options class, for the platform to validate before it deploys
  ([`contract.cue`](contract.cue)), and the docs generated from it ([`CONFIG.md`](CONFIG.md),
  [`CONFIG.agents.md`](CONFIG.agents.md)).

The options bind from the `Orders` section, so each setting is an `ORDERS__*` environment variable:

| Variable | Type | Rules |
|---|---|---|
| `ORDERS__PORT` | int | 1–65535, default `8080` |
| `ORDERS__LOGLEVEL` | enum | `debug`, `info`, `warn`, `error`; default `info` |
| `ORDERS__DATABASEURL` | url | secret, required, scheme `postgres`, at most 2048 characters |
| `ORDERS__ALLOWEDORIGINS__0`, `__1`, ... | list of strings | at least 1 item; default `["http://localhost:3000"]` |
| `ORDERS__REQUESTTIMEOUT` | duration, `hh:mm:ss` | `00:00:01`–`00:05:00`, default `00:00:30` |
| `ORDERS__WORKERCOUNT` | int | 1–64, default `4` |
| `WEBHOOK_KEYS` | list of strings, comma-separated (`[Csv]`) | secret, optional; 1–2 keys of 32–256 characters each |

[`Orders.Api.csproj`](Orders.Api.csproj) builds against the SDK in this repository with a `ProjectReference`, adds
the declaration analyzer, and re-exports `contract.cue` on every build (`DocuconfContractPath`).

## Run it

```console
$ cd examples/orders
$ ORDERS__DATABASEURL=postgres://orders:pw@localhost:5432/orders dotnet run
$ curl localhost:8080/healthz
ok
$ curl localhost:8080/config
{"port":8080,"logLevel":"info","databaseUrl":"***","allowedOrigins":["http://localhost:3000"],"requestTimeout":"00:00:30","workerCount":4,"webhookKeys":"***"}
```

`/config` shows the typed values; secrets are always `***`, set or not.

## When the configuration is wrong

With `ORDERS__PORT=0` and no `ORDERS__DATABASEURL`, the service refuses to start, exits 1 and lists every problem, not
just the first. A misspelt variable gets a hint:

```console
$ ORDERS__PORT=0 ORDERS__WORKERCUONT=2 dotnet run
docuconf: ORDERS__WORKERCUONT is set but not declared; did you mean ORDERS__WORKERCOUNT?
docuconf: 2 configuration problems:
  [missing_required] ORDERS__DATABASEURL: is required (Orders:DatabaseUrl)
  [out_of_range] ORDERS__PORT: '0' is below the minimum 1
```

In Kubernetes the same lines go to `/dev/termination-log`, so `kubectl describe pod` shows them.
[`smoke.sh`](smoke.sh) checks both runs, and the webhook key set below; CI runs it on every push.

## Rotate a key

`WEBHOOK_KEYS` is a key set: `POST /webhooks/payments` accepts a body whose `X-Signature` header is the hex
HMAC-SHA256 of the body under any key in the list ([`Webhook.cs`](Webhook.cs)). `[Csv]` makes the list one value,
`old,new`, so one Kubernetes Secret key holds it:

```yaml
WEBHOOK_KEYS: # a key set: one Secret key holding "old,new" while rotating
  secretKeyRef: {name: orders-webhooks, key: keys}
```

A variable is read once, at start, so a new key reaches the service only when the pods restart; with two keys valid at
once, no webhook is turned away while that happens:

1. Add the new key as the second item (`old,new` in the Secret), and roll out.
2. Switch the sender to the new key.
3. Remove the old key (`new`), and roll out.

The contract allows 1 or 2 keys of 32 to 256 characters each, so a trailing comma or a truncated key stops the service
at startup instead of locking out the sender:

```console
$ ORDERS__DATABASEURL=postgres://orders:pw@localhost:5432/orders \
    WEBHOOK_KEYS=old-webhook-key-0123456789abcdef0123, dotnet run
docuconf: 1 configuration problem:
  [out_of_range] WEBHOOK_KEYS: item 1 is 0 characters, below itemMinLength 32 (value redacted)
```

[`WebhookTests.cs`](../orders.Tests/WebhookTests.cs) walks through a rotation, and [`smoke.sh`](smoke.sh) posts
webhooks signed with both keys. [SPEC section 6.1](https://github.com/docuconf/docuconf-go/blob/main/spec/SPEC.md#61-rotation)
covers rotation in general.

## Export the contract

`contract.cue` is generated; never edit it by hand. Every build re-exports it, so commit it with your change to
`OrdersOptions.cs`. CI builds with `-p:DocuconfContractCheck=true`, which fails when it is out of date. By hand:

```console
$ cd examples/orders
$ dotnet build -c Release
$ dotnet bin/Release/net10.0/Orders.Api.dll docuconf export contract.cue --check
contract.cue is up to date
```

Run the export against the build or publish output: the contract includes any `appsettings*.json` files that ship
with the app (this example has none).

## Generated docs

[`CONFIG.md`](CONFIG.md), [`CONFIG.agents.md`](CONFIG.agents.md) and [`docs.json`](docs.json) are generated from
`contract.cue` by the `docuconf` CLI from [docuconf-go](https://github.com/docuconf/docuconf-go); never edit them by
hand either. The first is the reference for developers, the second the rules and facts AI agents need to change the
code or set deployment values, and the third the docs model both are rendered from. Regenerate them after exporting
the contract; CI runs the same commands with `--check` in place of `-o` and fails when they are out of date:

```console
$ cd examples/orders
$ docuconf docs contract.cue -o CONFIG.md
$ docuconf docs contract.cue --format agents -o CONFIG.agents.md
$ docuconf docs contract.cue --format model -o docs.json
```

`ORDERS__WORKERCOUNT` shows where the text comes from: the XML doc `<summary>` is its description, and the
`<remarks>` its details. `WEBHOOK_KEYS`'s details carry its rotation steps as a numbered list.

## Deploy

The app ships `contract.cue`, and the platform checks its inputs against it before anything reaches the cluster:
`docuconf vet` reports every bad or missing value, secret given as a literal or policy violation, and
`docuconf render` turns valid inputs into the pod's env, writing the list as `ORDERS__ALLOWEDORIGINS__0`, ... and the
timeout as `00:00:30` because the contract says .NET reads them that way. A Crossplane composition can evaluate the
same contract in plain CUE, and a Helm-based platform can use the
[docuconf Helm chart](https://github.com/docuconf/docuconf-go/tree/main/helm), which generates a `values.schema.json`
from the contract.

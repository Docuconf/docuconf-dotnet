# Example: shop

One image that runs as two kinds of process, a web server and a job worker, with its configuration declared with
docuconf. It shows three parts of the spec end to end:

- **roles** (SPEC §4.9): `[Roles("web", "worker")]` on the options class declares the roles, and `[Roles(...)]` on an
  input limits it to them. docuconf declares `DOCUCONF_ROLE`, a required enum of the roles, which the platform sets
  per workload; only the inputs that role uses are required;
- **requiredIf** (SPEC §4.2.1): `LOG_SCHEMA_URL` is required while `LOG_FORMAT` is `json`, and the
  `webhook-signing-key` file while `WEBHOOKS_ENABLED` is `true`;
- an **included fragment** (SPEC §4.8): `[Includes("otel@1")]` puts the OpenTelemetry SDK's `OTEL_*` variables in
  the contract, and startup checks their values although no property binds them.

The declaration is [`ShopOptions.cs`](ShopOptions.cs); [`contract.cue`](contract.cue) is exported from it, and
[`CONFIG.md`](CONFIG.md), [`CONFIG.agents.md`](CONFIG.agents.md) and [`docs.json`](docs.json) are generated from that.
The app is no real server: it loads its configuration at startup, as a service would, prints one line for its role
and exits 0 ([`Program.cs`](Program.cs)). The contract is the same in every docuconf SDK's shop example.

| Variable or file | Type | Rules |
|---|---|---|
| `DOCUCONF_ROLE` | enum | required: `web` or `worker`; declared by docuconf |
| `DATABASE_URL` | url | required, secret, scheme `postgres` or `postgresql` |
| `PORT` | int | `web` only; 1–65535, default `8080` |
| `ALLOWED_ORIGINS` | list of strings, `a,b` | `web` only; required |
| `QUEUES` | list of strings, `a,b` | `worker` only; required |
| `WEBHOOKS_ENABLED` | bool | `worker` only; default `false` |
| `LOG_FORMAT` | enum | `text` or `json`; default `text` |
| `LOG_SCHEMA_URL` | url | scheme `https`; required when `LOG_FORMAT` is `json` |
| `OTEL_*` | from `otel@1` | optional; `OTEL_EXPORTER_OTLP_PROTOCOL` is `grpc`, `http/protobuf` or `http/json`, ... |
| file `webhook-signing-key` | text, `/etc/shop/webhooks/signing.key` | `worker` only; secret, at least 32 characters; required when `WEBHOOKS_ENABLED` is `true` |

Every input has an explicit `[EnvName]`, so the variables are the contract's names rather than `SHOP__*`. The lists
are `[Csv]`: one value, `default,mail`, as the platform writes it (without `[Csv]`, .NET reads a list as
`QUEUES__0`, `QUEUES__1`, ...).

## Run it

From `examples/shop`, as each role:

```console
$ export DATABASE_URL=postgres://shop:pw@localhost:5432/shop
$ DOCUCONF_ROLE=web ALLOWED_ORIGINS=https://shop.example dotnet run
shop web: port 8080, origins https://shop.example
$ DOCUCONF_ROLE=worker QUEUES=default,mail dotnet run
shop worker: queues default,mail, webhooks off
```

The worker doesn't need `ALLOWED_ORIGINS`, and the web server doesn't need `QUEUES`. With JSON logs and no schema URL,
the app refuses to start, exits 1 and lists every problem, without printing the database URL:

```console
$ DOCUCONF_ROLE=worker QUEUES=default LOG_FORMAT=json dotnet run
docuconf: 1 configuration problem:
  [missing_required] LOG_SCHEMA_URL: is required when LOG_FORMAT is "json" (Shop:LogSchemaUrl)
```

File inputs are read under `DOCUCONF_FILE_ROOT` when it is set, so to run the worker with webhooks locally, put a key
of at least 32 characters at `./dev/etc/shop/webhooks/signing.key` and set `DOCUCONF_FILE_ROOT=./dev
WEBHOOKS_ENABLED=true`. It then prints `webhooks on`.

## The test matrix

[`MatrixTests.cs`](../shop.Tests/MatrixTests.cs) runs these cases through `DocuconfTesting`, each from
`DATABASE_URL=postgres://shop@db:5432/shop` and a fresh, empty file root. [`smoke.sh`](smoke.sh) runs cases 1, 2 and
6 against the published app and checks its output and exit status. CI runs both.

| # | Environment (plus `DATABASE_URL`) | Expected |
|---|---|---|
| 1 | `DOCUCONF_ROLE=web ALLOWED_ORIGINS=https://shop.example` | ok: `shop web: port 8080, origins https://shop.example` |
| 2 | `DOCUCONF_ROLE=worker QUEUES=default,mail` | ok: `shop worker: queues default,mail, webhooks off`; the worker needs no `ALLOWED_ORIGINS` |
| 3 | `DOCUCONF_ROLE=web` | `ALLOWED_ORIGINS` `missing_required` |
| 4 | `QUEUES=default` (no role) | `DOCUCONF_ROLE` `missing_required`, and nothing else |
| 5 | `DOCUCONF_ROLE=cron` | `DOCUCONF_ROLE` `not_in_enum`, and nothing else |
| 6 | `DOCUCONF_ROLE=worker QUEUES=default LOG_FORMAT=json` | `LOG_SCHEMA_URL` `missing_required`: `is required when LOG_FORMAT is "json"` |
| 7 | as 6, plus `LOG_SCHEMA_URL=https://logs.example/v1.json` | ok |
| 8 | `DOCUCONF_ROLE=worker QUEUES=default WEBHOOKS_ENABLED=true`, no key file | `webhook-signing-key` `file_missing`: `..., and is required when WEBHOOKS_ENABLED is true` |
| 9 | as 8, with a key of 32 or more characters at the path under the file root | ok: `webhooks on` |
| 10 | `DOCUCONF_ROLE=web ALLOWED_ORIGINS=https://shop.example WEBHOOKS_ENABLED=true` | ok: the key file belongs to the worker |
| 11 | `DOCUCONF_ROLE=web ALLOWED_ORIGINS=https://shop.example OTEL_EXPORTER_OTLP_PROTOCOL=carrier-pigeon` | `OTEL_EXPORTER_OTLP_PROTOCOL` `not_in_enum`: fragment variables are checked at boot |

With no role, or one the contract doesn't declare, the role is the one problem reported (cases 4 and 5): nothing else
can be checked until docuconf knows which inputs the role uses.

## Export the contract and the docs

`contract.cue` is generated; never edit it by hand. Every build re-exports it, so commit it with your change to
`ShopOptions.cs`. CI builds with `-p:DocuconfContractCheck=true`, which fails when it is out of date, checks it against
the CUE meta-schema with `cue vet -c`, and checks the generated docs. Regenerate the docs with the `docuconf` CLI from
[docuconf-go](https://github.com/docuconf/docuconf-go) after exporting:

```console
$ cd examples/shop
$ dotnet build -c Release
$ docuconf docs contract.cue -o CONFIG.md
$ docuconf docs contract.cue --format agents -o CONFIG.agents.md
$ docuconf docs contract.cue --format model -o docs.json
```

The docs show the roles and conditions: `CONFIG.md` marks `QUEUES` as `worker` only, and the signing key as required
when `WEBHOOKS_ENABLED` is `true`.

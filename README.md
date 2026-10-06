# docuconf for .NET

Typed configuration contracts for the .NET Options pattern. Your options class, with the DataAnnotations
you already use, becomes a contract that your Kubernetes platform checks **before deploy**, and that your
app checks again **at startup**. It covers environment variables, `appsettings*.json`, and file inputs:
TLS key pairs, CA bundles, keystores, JSON config files and licence files.

Part of [docuconf](https://github.com/docuconf). See the
[specification](https://github.com/docuconf/docuconf-go/blob/main/spec/SPEC.md).

> **Status:** `0.1.0-alpha`. The contract format is a draft (`v1alpha1`) and the API may change.

## Declare

```csharp
[ConfigContract("billing-api", Section = "Billing")]
public sealed class BillingOptions
{
    [Required, Secret, UrlSchemes("postgres", "postgresql")]
    [Description("Primary Postgres connection string")]
    public string DatabaseUrl { get; set; } = "";

    [Range(1, 65535), Description("HTTP listen port")]
    public int Port { get; set; } = 8080;

    [Required]
    [TlsFile("/etc/billing/tls", DnsNames = ["billing.internal"], MinRemaining = "720h", Reload = Reload.Watch)]
    [Description("Certificate the API serves HTTPS with")]
    public TlsKeyPair ServingCertificate { get; set; } = new();

    [Required, ConfigFile("/etc/billing/rates/rates.json")]
    [Description("Pricing tiers by monthly volume")]
    public RatesConfig Rates { get; set; } = new();
}
```

Everything else is ordinary .NET: `[Required]`, `[Range]`, `[MinLength]`, `[RegularExpression]`, `[AllowedValues]`
and `[Url]` become contract constraints. `Description` is required, because every input in a contract is documented.

| Attribute | Input |
|---|---|
| `[Secret]` | Must come from a Kubernetes Secret; never printed. |
| `[UrlSchemes("https")]` | A URL with an allowed scheme. |
| `[TlsFile(dir)]` on a `TlsKeyPair` | `tls.crt`, `tls.key`, optional `ca.crt`. Checked for key match, expiry (`MinRemaining`), `DnsNames`, `KeyAlgorithms`, and the chain to `ca.crt` (`RequireCA`). `.Current` reloads rotated certificates. |
| `[ConfigFile(path)]` on any class | A JSON file deserialized into that class. The contract carries a JSON Schema generated from it, so the platform checks the file against the same type. |
| `[CaBundleFile(path)]` on a `CaBundle` | PEM CA certificates. |
| `[KeystoreFile(path, PasswordProperty = ...)]` on a `Keystore` | A PKCS#12 keystore; its password is a `[Secret]` property. |
| `[TextFile(path, Pattern = ...)]` on a `string` | A text file such as a licence key; the property receives the content. |
| `[BinaryFile(path)]` on a `BinaryFile` | Opaque bytes. |
| `[External("KeyVault")]` | Supplied by a provider the platform does not control; left out of the contract. |

## Validate at startup

```csharp
if (DocuconfExport.RunIfRequested(args)) return;   // see Export below

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDocuconf<BillingOptions>();     // binds, loads files, ValidateOnStart
```

Startup fails with every problem at once, each with a stable code, and secrets redacted:

```
OptionsValidationException: [file_missing] rates: /etc/billing/rates/rates.json does not exist;
[invalid_scheme] BILLING__DATABASEURL: must use one of the schemes postgres, postgresql;
[out_of_range] BILLING__PORT: The field Port must be between 1 and 65535.;
[missing_required] BILLING__WAREHOUSEAPI: is required (Billing:WarehouseApi)
```

The same lines go to `/dev/termination-log`, so `kubectl describe pod` shows them. For local development, set
`DOCUCONF_FILE_ROOT=./dev` to read `/etc/billing/tls` from `./dev/etc/billing/tls`.

## Export

Run the published app with `docuconf export`:

```sh
dotnet publish -c Release -o out
dotnet out/Billing.Api.dll docuconf export contract.cue
```

The contract includes the `appsettings.json` values that ship with the app as defaults, and
`appsettings.{Environment}.json` values as profiles selected by `ASPNETCORE_ENVIRONMENT`, so a value set in
`appsettings.Production.json` counts as supplied. A `[Secret]` value in any appsettings file is an export error.
Variable names follow the configuration path: `Billing:Port` is `BILLING__PORT`. The contract records that .NET
reads `TimeSpan` as `hh:mm:ss` and lists as `NAME__0`, `NAME__1`, so the platform renders values that way.

## Structured values

A property marked `[JsonVar]` is a `json` variable: one environment variable holding compact JSON, bound to your
own type. The contract carries a JSON Schema generated from the type, so the platform checks the value before
deploy, and docuconf checks it again at startup with the type's DataAnnotations.

```csharp
[Required, JsonVar]
[Description("Rate limit for the public API")]
public RateLimit Limits { get; set; } = null!;   // BILLING__LIMITS={"rps":10,"burst":20}
```

The value is read like a config file (camelCase names, read case-insensitively; enums as strings; unknown
properties rejected). In `appsettings.json` or an overlay it can also be an ordinary nested section, which the
configuration binder fills, so the platform can render it into an overlay as an object. An initializer or an
appsettings value becomes the contract `default` and must satisfy the type's constraints. A JSON string replaces the
whole object; a nested section merges key by key with lower layers, as configuration sections always do.

## Platform overlays and injected secrets

A platform can supply settings as a file in your own appsettings format instead of environment variables. Declare
the file on the options class; docuconf loads it between your baked-in appsettings files and environment
variables, and puts it in the contract so the platform renders values into it:

```csharp
[ConfigContract("catalog", Section = "Catalog")]
[ConfigOverlay("platform", "/app/config/appsettings.Production.json", ReloadOnChange = true)]
public sealed class CatalogOptions { /* ... */ }
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddDocuconfOverlays<CatalogOptions>();   // appsettings < overlay < environment
builder.Services.AddDocuconf<CatalogOptions>();
```

This is the same as `AddJsonFile("/app/config/appsettings.Production.json", optional: true, reloadOnChange: true)`,
placed before environment variables even though `CreateBuilder` has already added them, and watched by polling,
because Kubernetes updates a mounted ConfigMap by swapping a symlink. Read reloadable values through
`IOptionsMonitor<CatalogOptions>`. Overlay values are validated like any other. Keep the overlay in a directory of
its own: the platform mounts the directory, so it must not hold your app's files.

Secrets injected at startup — by Bank-Vaults' `vault-env`, a wrapper such as `op run`, or the Vault Agent — need
nothing special: docuconf validates the environment and files as they are when the process starts, after
injection. On the platform side they are declared as `injected` values (SPEC §4.5.1). If a `[Secret]` value still
holds a reference when the app starts (it begins with `vault:`, `op://` or `ref+`), the injector did not run, and
startup fails with `invalid_type` naming the variable and the reference scheme, never the value.

## Develop

```sh
dotnet test   # needs the cue CLI for the contract checks: go install cuelang.org/go/cmd/cue@v0.17.1
```

`scripts/smoke-consumer.sh` packs the library, installs it into a clean app from a local feed, and checks export and
startup validation. Releases are published from CI with NuGet trusted publishing; see [RELEASING.md](RELEASING.md).

`samples/Billing.Api` uses every input kind. The tests check exported contracts against a copy of the CUE
meta-schema in `tests/Docuconf.Tests/spec`; refresh it with `scripts/sync-spec.sh`.

Licence: [MIT](LICENSE).

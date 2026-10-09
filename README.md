# docuconf for .NET

Documentation: [docuconf.dev](https://docuconf.dev) · [.NET guide](https://docuconf.dev/languages/dotnet/)

Typed configuration contracts for the .NET Options pattern. Your options class, with the DataAnnotations
you already use, becomes a contract that your Kubernetes platform checks **before deploy**, and that your
app checks again **at startup**. It covers environment variables, `appsettings*.json`, and file inputs:
TLS key pairs, CA bundles, keystores, JSON config files and licence files.

Part of [docuconf](https://github.com/docuconf). See the
[specification](https://github.com/docuconf/docuconf-go/blob/main/spec/SPEC.md).
A complete app: [`examples/orders`](examples/orders).

> **Status:** `0.1.0-alpha`. The contract format is a draft (`v1alpha1`) and the API may change.

## 1. Install

The package is `Docuconf.Options`; its namespace is `Docuconf`. It is not on nuget.org yet, so build it into a
local feed and install it from there (.NET 8 or later):

```sh
git clone https://github.com/docuconf/docuconf-dotnet.git
dotnet pack docuconf-dotnet/src/Docuconf -c Release -o ~/docuconf-feed
dotnet nuget add source ~/docuconf-feed --name docuconf-local
cd YourApp
dotnet add package Docuconf.Options --prerelease
```

The package brings the library, a Roslyn analyzer that reports declaration mistakes as build errors, and an MSBuild
target that exports the contract on build. With the first release on nuget.org the last command alone is enough.

## 2. Declare

Add `[ConfigContract]`, a description for each input (`[Description]`, or the XML doc `<summary>`) and, for secrets,
`[Secret]` to the options class you already have:

```csharp
[ConfigContract("orders-api", Section = "Orders")]
public sealed class OrdersOptions
{
    [Range(1, 65535)]
    [Description("HTTP listen port")]
    public int Port { get; set; } = 8080;

    [AllowedValues("debug", "info", "warn", "error")]
    [Description("Minimum level of log messages to write")]
    public string LogLevel { get; set; } = "info";

    // [Secret]: the platform must supply it from a Kubernetes Secret, and docuconf never prints it.
    // [MaxLength] bounds the URL in characters; a longer one fails startup with out_of_range.
    [Required, Secret, UrlSchemes("postgres"), MaxLength(2048)]
    [Description("Postgres connection string for the orders database")]
    public string DatabaseUrl { get; set; } = "";

    // A list arrives as ORDERS__ALLOWEDORIGINS__0, ORDERS__ALLOWEDORIGINS__1, ...
    [MinLength(1)]
    [Description("Origins allowed to call the API from a browser")]
    public List<string> AllowedOrigins { get; set; } = ["http://localhost:3000"];

    // A TimeSpan arrives as hh:mm:ss (00:00:30); the platform writes "30s" and renders it that way.
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    [Description("Time allowed to handle one request")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // An XML doc comment works instead of [Description]: the <summary> is the description, and the <remarks> are the
    // details, longer docs for docuconf docs (the project sets GenerateDocumentationFile).

    /// <summary>Background workers that process new orders.</summary>
    /// <remarks>
    /// <para>
    /// Each worker holds one connection from the pool of <see cref="DatabaseUrl"/>, so keep this below the database's
    /// connection limit.
    /// </para>
    /// <list type="bullet">
    /// <item><description>Raise it when the order queue backs up.</description></item>
    /// <item><description>Lower it when the database is the bottleneck.</description></item>
    /// </list>
    /// </remarks>
    [Range(1, 64)]
    public int WorkerCount { get; set; } = 4;

    // A key set (SPEC §4.3, §6.1): every key in it is valid at once, so a key can be rotated without turning webhooks
    // away. One Kubernetes Secret key holds "old,new" during a rotation, and [EnvName] gives it a name of its own. A
    // KeySet is always secret, so it has no initializer, and it never prints its keys.

    /// <summary>Keys that verify the signature on incoming payment webhooks.</summary>
    /// <remarks>
    /// <para>
    /// A webhook is accepted when it is signed with any key in the set. Each key is 32 to 256 characters, so an empty or
    /// truncated key fails at boot. Without this variable, the service rejects every webhook.
    /// </para>
    /// </remarks>
    [KeySet(KeyMinLength = 32, KeyMaxLength = 256), EnvName("WEBHOOK_KEYS")]
    public KeySet? WebhookKeys { get; set; }
}
```

The file needs `using System.ComponentModel;`, `using System.ComponentModel.DataAnnotations;` and `using Docuconf;`.
Variable names follow the configuration path: `Orders:Port` is `ORDERS__PORT`. Initializers are the defaults the
contract records. A mistake such as a missing description or a default outside its `[Range]` is a build error
(see [Analyzer](#analyzer)).

## 3. Run

```csharp
// `dotnet Orders.Api.dll docuconf export contract.cue` writes the contract and exits.
if (DocuconfExport.RunIfRequested(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.AddDocuconf<OrdersOptions>();   // binds the Orders section and validates it at startup
var app = builder.Build();

// The validated options, or every problem on stderr and exit status 1.
var orders = app.Services.LoadOrExit<OrdersOptions>();
```

`builder.AddDocuconf<T>()` binds the section, loads file inputs and config-file overlays, and validates everything
when the host starts, so `app.Run()` alone is enough. `app.Services.LoadOrExit<T>()` gives you the options before
`app.Run()`, as the example does to read the port. Read them anywhere else through `IOptions<T>` or
`IOptionsMonitor<T>` as usual.

`AddDocuconf` replaces `BindConfiguration` (or `Bind`) and `ValidateDataAnnotations` for that class: it binds every
property on its own, so one bad value does not hide the others, and checks every DataAnnotation once. Keeping those
calls as well is reported at startup.

## 4. See an error

```console
$ ORDERS__PORT=0 ORDERS__WORKERCUONT=2 dotnet run
docuconf: ORDERS__WORKERCUONT is set but not declared; did you mean ORDERS__WORKERCOUNT?
docuconf: 2 configuration problems:
  [missing_required] ORDERS__DATABASEURL: is required (Orders:DatabaseUrl)
  [out_of_range] ORDERS__PORT: '0' is below the minimum 1
```

Every problem at once, one per line, each with a stable code (SPEC §11.2) and the variable name; secret values are
never printed. The process exits with status 1, and the same lines go to `/dev/termination-log`, so
`kubectl describe pod` shows them. A variable that is set but not declared, and close to a declared name, gets a hint;
it is not an error. To get an `OptionsValidationException` instead of the exit, use
`builder.AddDocuconf<OrdersOptions>(s => s.ThrowOnInvalid = true)`.

## 5. Test your config

`DocuconfTesting` binds and validates an options class from an environment you pass in. It does not read or change
the process environment, build a host, start threads or write the termination log. With xUnit:

```csharp
public class OrdersOptionsTests
{
    [Fact]
    public void Port_zero_is_rejected()
    {
        var problems = DocuconfTesting.Validate<OrdersOptions>(new Dictionary<string, string>
        {
            ["ORDERS__PORT"] = "0",
            ["ORDERS__DATABASEURL"] = "postgres://orders@localhost:5432/orders",
        });

        var problem = Assert.Single(problems);
        Assert.Equal("[out_of_range] ORDERS__PORT: '0' is below the minimum 1", problem.ToString());
    }

    [Fact]
    public void Defaults_are_valid()
    {
        var options = DocuconfTesting.Load<OrdersOptions>(new Dictionary<string, string>
        {
            ["ORDERS__DATABASEURL"] = "postgres://orders@localhost:5432/orders",
        });

        Assert.Equal(8080, options.Port);
    }
}
```

Each `Violation` has a `Code` (`Codes.OutOfRange`, ...), the `Input` (variable or file input name) and a `Message`.
For file inputs, pass `DOCUCONF_FILE_ROOT` in the environment, or set `s => s.FileRoot = dir`.

## 6. Export the contract

Set the contract's path in the project file, and every build writes it from the built app:

```xml
<DocuconfContractPath>contract.cue</DocuconfContractPath>
```

In CI, build with `-p:DocuconfContractCheck=true`: the build writes nothing and fails when the checked-in file is out
of date. The check ignores only `metadata.generator.version` (the SDK version), so upgrading the SDK does not by
itself make the file stale. You can also run the export by hand, against the publish output so the `appsettings*.json` files that ship
are included:

```sh
dotnet publish -c Release -o out
dotnet out/Orders.Api.dll docuconf export contract.cue
dotnet out/Orders.Api.dll docuconf export contract.cue --check   # exit 1 when out of date
dotnet out/Orders.Api.dll docuconf export contract.json          # JSON, for the contract-first mode
```

`-` writes to stdout and `--format cue|json` picks the format. Any other `docuconf` command is a usage error, so a typo
never starts the app. The contract includes the `appsettings.json` values that ship with the app as defaults, and
`appsettings.{Environment}.json` values as profiles selected by `ASPNETCORE_ENVIRONMENT`. It records that .NET reads
`TimeSpan` as `hh:mm:ss` and lists as `NAME__0`, `NAME__1` (or `a,b` for a `[Csv]` list), so the platform renders
values that way.

## 7. Deploy

Ship `contract.cue` with the app. The platform checks its inputs against it before anything reaches the cluster:
`docuconf vet` reports every bad or missing value, secret given as a literal or policy violation, and
`docuconf render` turns valid inputs into the pod's environment. A Crossplane composition can evaluate the same
contract in plain CUE, and a Helm-based platform can use the
[docuconf Helm chart](https://github.com/docuconf/docuconf-go/tree/main/helm). At startup the app checks again, as
step 4 shows, including the file inputs the platform could not see.

---

## Reference

### Local development

A `[Secret]` cannot have a default, and an `appsettings*.json` file that sets one fails the export, because the file
ships inside the image. Keep local secrets out of the content root with
[user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets), which `WebApplication` loads in the
Development environment:

```sh
dotnet user-secrets init
dotnet user-secrets set Orders:DatabaseUrl postgres://orders:pw@localhost:5432/orders
```

or set the environment variable (`ORDERS__DATABASEURL`) in `launchSettings.json`. File inputs are read under a
local directory when `DOCUCONF_FILE_ROOT` (or the configuration value `Docuconf:FileRoot`, for example in
`appsettings.Development.json`) is set: `/etc/billing/tls` becomes `./dev/etc/billing/tls` with `./dev`.

### Attributes

`[Required]`, `[Range]`, `[MinLength]`, `[MaxLength]`, `[Length]`, `[StringLength]`, `[RegularExpression]`,
`[AllowedValues]` and `[Url]` become contract constraints, and startup reports them in the contract's words
(`'0' is below the minimum 1`); a constraint with your own `ErrorMessage` keeps it. A constraint that does not fit the
property's type, such as `[UrlSchemes]` on an `int` or `[Range]` on a `string`, is a declaration error rather than a
rule the contract silently drops. Integer types narrower than 64 bits export their own range, so the platform never
sends a value the property cannot hold. Lengths count characters (Unicode scalar values), not UTF-16 units, so `日本`
is 2 and an emoji is 1: `[MaxLength]` or `[StringLength]` on a URL exports `maxLength`, `[JsonVar(MaxLength = 256)]`
bounds a `json` value as received (or, from an appsettings file or overlay, as compact JSON), and `[ItemLength(2, 4)]`
bounds every item of a string list (`itemMinLength`/`itemMaxLength`), for apps that store values in fixed-width
fields. A value above its limit fails startup with `out_of_range`; a secret's error gives its length, never its value.
`[ItemLength]` on an integer list, a minimum above the maximum, or a minimum length on a URL is a declaration error.
Values are parsed by the exact rules of SPEC §5, whatever .NET's own parsers accept, so the platform and every SDK
take the same strings; anything else is `invalid_type`. Nothing is trimmed: `" true"`, `"8080\n"` and `"5s "` fail.
A `bool` is `true` or `false` in any case (`TRUE`, `False`), never `1`, `yes` or `on`. An integer is decimal digits
with an optional sign (`+5` and `007` are fine; `0x10`, `1_000`, `1e3` and `5.0` are not), and outside its type's
range `out_of_range`. A number has a digit on each side of its point, with an optional exponent, whatever the culture
(`25e-2`; never `.5`, `5.`, `0,5`, `Infinity` or a hex float). A `TimeSpan` is `[d.]hh:mm:ss[.fffffff]` (`00:00:30`,
`1.02:03:04.5`), never `00:30`, a bare number or a sign. URLs need a `scheme://`, and enum names match exactly as
declared. These rules apply to appsettings values and overlays as well as to the environment.
A list given as one value (`ORDERS__ALLOWEDORIGINS=a,b`) is `invalid_type` with the indexed form to use instead, and
list items must be numbered from 0 with no gap. A list marked `[Csv]` is the other way round: one value, `a,b`, split
on the separator exactly as given, so `a, b` is `a` and ` b`, and a trailing comma leaves an empty item for
`[ItemLength]` to reject.

| Attribute | Input |
|---|---|
| `[Description("...")]` | The description, at least 5 characters; or the XML doc `<summary>` (see [Descriptions and details](#descriptions-and-details)). |
| `[Secret]` | Must come from a Kubernetes Secret; no default; never printed. A record's generated `ToString` would print it, so a record needs its own `PrintMembers`. |
| `[UrlSchemes("https")]` on a `string` or `Uri` | A URL with an allowed scheme. |
| `[EnvName("LOG_LEVEL")]` | Overrides the derived variable name. The app reads that variable, and the configuration path (for appsettings) when it is not set. |
| `[ItemRange(0, 1023)]` on an `int[]`, `List<long>`, ... | Bounds every item of an integer list (`itemMin`/`itemMax`). |
| `[ItemLength(2, 4)]` on a `string[]`, `List<string>`, ... | Bounds the length of every item of a string list, in characters (`itemMinLength`/`itemMaxLength`). |
| `[Csv]` or `[Csv(";")]` on a list | The list is one value with its items joined by the separator (`encoding: csv`), instead of `NAME__0`, `NAME__1`, ... Use it when one Secret key holds the list. On a `KeySet`, it changes the separator. |
| `KeySet` property, with `[KeySet(...)]` | A key set (`keySet`): secret keys that are all valid at once, for rotation (see [Key sets](#key-sets)). `MinKeys` (1) and `MaxKeys` (2) bound the number of keys, `KeyMinLength` and `KeyMaxLength` each key. |
| `[Deprecated("Use PORT instead", ReplacedBy = "PORT")]` | The input is being removed (`deprecated`): it still loads, and startup warns when it is set (see [Deprecated inputs](#deprecated-inputs)). |
| `[Display(GroupName = "database")]` | The docs group (`group`). |
| `[Examples("orders", "billing")]` | Example values for the docs (`examples`); not on a secret. |
| `[ConfigurationKeyName("PORT")]` | Microsoft.Extensions.Configuration's own rename: the configuration key, and so the variable name, follow it. A `configKey` equal to the variable's name is left out of the contract. |
| `[MaxLength(200)]` with `[UrlSchemes]` or `[Url]` | Bounds a URL's length in characters (`maxLength`). |
| `[JsonVar]` on a class | One variable holding JSON, checked against the class (see below). |
| `[JsonVar(MaxLength = 256)]` | Bounds a `json` value's length in characters, measured as received (`maxLength`). |
| `[External("KeyVault")]` | Supplied by a provider the platform does not control; left out of the contract. Dictionaries and lists of objects need it (or `[JsonVar]`). |
| `[TlsFile(dir)]` on a `TlsKeyPair` | `tls.crt`, `tls.key`, optional `ca.crt`. Checked for key match, expiry (`MinRemaining`), `DnsNames`, `KeyAlgorithms`, and the chain to `ca.crt` (`RequireCA`). `.Current` reloads rotated certificates. |
| `[ConfigFile(path)]` on any class | A JSON, YAML or TOML file (by its extension, or `Format = "yaml"`) deserialized into that class. The contract carries a JSON Schema generated from it. On a `ConfigFile<T>`, `.Value` reloads the file when it changes, which `Reload = Reload.Watch` needs. |
| `[CaBundleFile(path)]` on a `CaBundle` | PEM CA certificates. |
| `[KeystoreFile(path, PasswordProperty = ...)]` on a `Keystore` | A PKCS#12 keystore; its password is a `[Secret]` property. |
| `[TextFile(path, Pattern = ...)]` on a `string` | A text file such as a licence key; the property receives the content. |
| `[BinaryFile(path)]` on a `BinaryFile` | Opaque bytes. |

### Descriptions and details

Every input has a **description**: what it is, in one phrase of plain text. It is `[Description("...")]` (or
`[Display(Description = "...")]`), or else the property's XML doc `<summary>`, on one line without its final period. A
missing or blank description, or one under 5 characters, is a declaration error. An input may also have **details**:
CommonMark on why it exists and when to change it, from the XML doc `<remarks>`, at most 4000 characters (Unicode code
points). Details go into the contract for generated docs only and are never read at runtime.

The compiler writes doc comments to an XML file beside the assembly (`Orders.Api.xml`) only when the project asks for
it, and docuconf reads them from there, so set `GenerateDocumentationFile` in the app's project file:

```xml
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <!-- Docs are wanted on the options classes, not on every public member. -->
    <NoWarn>$(NoWarn);CS1591</NoWarn>
```

Without it, only `[Description]` counts, at build (the analyzer) and at startup alike. `dotnet publish` copies the XML
file to the output. `<remarks>` become CommonMark: `<para>` is a paragraph, `<c>`, `<see cref>`, `<see langword>` and
`<paramref>` are code spans, `<see href>` is a link, `<code>` is a fenced block, `<list type="bullet|number">` is a
list, `<b>`/`<i>` are emphasis and `<br/>` a line break; other elements keep their text. Remarks over 4000 characters,
or blank, fail the export and startup.

`docuconf docs` in the [docuconf CLI](https://github.com/docuconf/docuconf-go) generates CONFIG.md and
CONFIG.agents.md from the exported contract: `docuconf docs contract.cue -o CONFIG.md`, and
`--format agents -o CONFIG.agents.md`.

### File inputs and structured values

```csharp
[ConfigContract("billing-api", Section = "Billing")]
public sealed class BillingOptions
{
    [Required]
    [TlsFile("/etc/billing/tls", DnsNames = ["billing.internal"], MinRemaining = "720h", Reload = Reload.Watch)]
    [Description("Certificate the API serves HTTPS with")]
    public TlsKeyPair ServingCertificate { get; set; } = new();

    [Required, ConfigFile("/etc/billing/rates/rates.json")]
    [Description("Pricing tiers by monthly volume")]
    public RatesConfig Rates { get; set; } = new();

    [Required, JsonVar]
    [Description("Rate limit for the public API")]
    public RateLimit Limits { get; set; } = null!;   // BILLING__LIMITS={"rps":10,"burst":20}
}
```

A `[JsonVar]` value is read like a config file (camelCase names, read case-insensitively; enums as strings; unknown
properties rejected) and its type's DataAnnotations are checked at startup. In `appsettings.json` or an overlay it can
also be a nested section. [`samples/Billing.Api`](samples/Billing.Api) uses every input kind.

A config file may be JSON, YAML (read with [YamlDotNet](https://github.com/aaubry/YamlDotNet), YAML 1.2 core schema)
or TOML (read with [Tomlyn](https://github.com/xoofx/Tomlyn)); YAML and TOML are converted to JSON and bound the same
way. A file that does not parse is `file_malformed`; one that parses but does not bind to the class (a wrong type, an
unknown property) or breaks its DataAnnotations is `schema_mismatch`.

### Analyzer

The package's analyzer reports declaration errors in the IDE and fails the build, with the messages export and
startup would give later:

| ID | Error |
|---|---|
| DOCUCONF001 | An input has no `[Description]`, or XML doc `<summary>` when the project generates a documentation file, of at least 5 characters. |
| DOCUCONF002 | A `[Secret]` has an initializer. |
| DOCUCONF003 | A constraint does not fit the property's type. |
| DOCUCONF004 | A literal default (or the `0` of an unset number) violates `[Range]` or `[AllowedValues]`. |
| DOCUCONF005 | An `[EnvName]` is not UPPER_SNAKE_CASE. |
| DOCUCONF006 | The `[ConfigContract]` service name is not a DNS label. |
| DOCUCONF007 | A pattern uses .NET-only regex features; contracts need RE2. |
| DOCUCONF008 | A record's generated `ToString` would print a `[Secret]`. |
| DOCUCONF009 | A dictionary or list of objects has no `[JsonVar]` or `[External]`. |
| DOCUCONF010 | A file input attribute is on the wrong property type. |
| DOCUCONF011 | A `[Deprecated]` message is blank or over 500 characters, or the input is `[Required]`. |

Checks that need the built app (appsettings files, defaults computed by code) run at export and startup.

### Key sets

A `KeySet` holds secret keys that are all valid at once, so a key can be rotated without an outage (SPEC §4.3, §6.1):
webhook signatures, inbound API keys, HMAC-signed tokens. The platform supplies it from one Secret key holding
`old,new` while a key is rotated:

```csharp
[KeySet(KeyMinLength = 32, KeyMaxLength = 256), EnvName("WEBHOOK_KEYS")]
public KeySet? WebhookKeys { get; set; }
```

`Keys` lists them in order, `Contains(candidate)` compares an API key with every key in constant time, and
`Verify(check)` runs a check of yours against each key, all of them even after one matches, for an HMAC:

```csharp
// Tries every key, so the time taken does not say which one matched; each comparison is constant-time too.
return keys?.Verify(key => CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), expected)) == true;
```

A key set is always secret: it has no default, its keys are never trimmed, and `ToString()` and System.Text.Json show
`***`. A number of keys outside `MinKeys`..`MaxKeys` is `too_few_items` or `too_many_items`; a key outside its lengths,
or an empty one (a stray comma), is `out_of_range`, and no message holds a key. The generated docs print the rotation
steps for every key set.

### Deprecated inputs

`[Deprecated("...")]` marks a variable or file input the platform should stop setting (SPEC §4.2); `ReplacedBy`
names the input that replaces it. The message is required, at most 500 characters, and a `[Required]` input cannot be
deprecated, since the platform could not stop setting it. A deprecated input still loads and is still checked. When
it is set, startup logs a warning through the app's `ILogger` (or stderr) that names the input and the message, never
the value:

```text
docuconf: warning: OLD_PORT is deprecated but still set: Use PORT instead (replaced by PORT)
```

### Platform overlays and injected secrets

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
builder.AddDocuconf<CatalogOptions>();   // appsettings < overlay < environment, then binds and validates
```

This is the same as `AddJsonFile("/app/config/appsettings.Production.json", optional: true, reloadOnChange: true)`,
placed before environment variables even though `CreateBuilder` has already added them, and watched by polling,
because Kubernetes updates a mounted ConfigMap by swapping a symlink. Read reloadable values through
`IOptionsMonitor<CatalogOptions>`. Keep the overlay in a directory of its own: the platform mounts the directory, so it
must not hold your app's files. (`builder.Configuration.AddDocuconfOverlays<T>()` adds only the overlays.)

Secrets injected at startup, by Bank-Vaults' `vault-env`, a wrapper such as `op run`, or the Vault Agent, need
nothing special: docuconf validates the environment and files as they are when the process starts. If a `[Secret]`
value still holds a reference when the app starts (it begins with `vault:`, `op://` or `ref+`), the injector did not
run, and startup fails with `invalid_type` naming the variable and the reference scheme, never the value.

### Contract-first

When the contract comes first (written by hand in CUE, or another app's), validate an environment against it
directly, with no options class. Get it as JSON with `docuconf export contract.json`, or from hand-written CUE with
`cue export contract.cue --out json` (inside a CUE module that depends on `docuconf.dev/contract`), then:

```csharp
var config = DocuconfContract.FromFile("contract.json").LoadOrExit();   // the process environment
long port = config.Get<long>("PORT");
int workers = config.Get<int>("WORKERS");   // converts, or throws when the value does not fit
```

`Get<T>` throws on a variable the contract does not declare (with the closest name) and on a type it cannot convert
to; it never returns a silent default. `Load(env)` and `Validate(env)` take an environment map and throw or return the
violations. Values are typed: `long` for `int`, `double`, `bool`, `TimeSpan` for `duration`, `Uri` for `url`,
`IReadOnlyList<string>` or `IReadOnlyList<long>` for lists, a `KeySet` for `keySet`, a `JsonNode` for `json`, `string`
otherwise; an absent optional value is null. Printing the values shows secrets as `***`. Every wire encoding of
SPEC §5 is read, with the parsing rules above.

The mode covers the whole contract. Values are layered as a host with config files layers them: the variable's
default, then the selected profile's default (`profiles`, picked by the selector variable), then each overlay
(`overlays`, in json, yaml or toml, values at their `configKey`), then the environment. File inputs are loaded from
their paths, or the `pathEnv` variable's, and checked as declared ones are: a `config` file (json, yaml or toml) against
its JSON Schema and returned as a `JsonNode`, a `text` file as its text, and a `TlsKeyPair`, `CaBundle`, PKCS#12
`Keystore` or `BinaryFile` otherwise. Files and overlays are read under `DOCUCONF_FILE_ROOT` when it is set (or
`DocuconfSettings.FileRoot`). A deprecated input that is set is a warning in `ContractLoadResult.Warnings`, which
`Load` writes to stderr. A `json` value is checked against the variable's JSON Schema (draft
2020-12, with [JsonSchema.Net](https://github.com/json-everything/json-everything) 8, the last MIT-licensed major) and
fails with `schema_mismatch`, naming the location and keyword (`/perMinute fails minimum`) and, for a secret, never
the value; `minLength` and `maxLength` count code points, as in the other SDKs. A schema JsonSchema.Net cannot read,
or one with a `$schema` other than 2020-12, is an error when the contract is read.

### Conformance

The tests run docuconf's shared conformance suite (SPEC §12) through the contract-first mode. They read
`conformance/cases.json` from a docuconf-go checkout next to this repository, or from `DOCUCONF_CONFORMANCE`:

```sh
DOCUCONF_CONFORMANCE=../docuconf-go/conformance/cases.json DOCUCONF_REQUIRE_CONFORMANCE=1 \
  dotnet test -- --filter-class Docuconf.Tests.ConformanceTests --output detailed
```

Without the file the test is skipped, unless `DOCUCONF_REQUIRE_CONFORMANCE=1` (as in CI). Each case's files are
written under a new directory, which is its `DOCUCONF_FILE_ROOT`.

**Capability tags skipped: none.** The runner keeps an allow-list of the tags it supports, which is every tag in the
suite: `int64`, `json-schema`, `key-set`, `deprecated`, `strict-parsing`, `files`, `profiles` and `overlays`. A case
with a tag it does not know is skipped, never run, and a skip fails the test, so a new tag shows up as a failure
rather than a silent skip. PKCS#12 keystores (AES-256 with PBKDF2) are opened with .NET's own reader, and an Ed25519
key is matched to its certificate by docuconf itself, since .NET has no Ed25519 key type. CI runs the suite on Linux.
On Windows, .NET cannot parse an Ed25519 certificate at all, so an Ed25519 `tls.crt` is reported as
`certificate_invalid` there whatever `keyAlgorithms` allows; the suite's one Ed25519 case
(`files_tls/a key algorithm that is not allowed`) expects that code for a different reason.

`scripts/conformance.sh` runs the suite and the shared export check against a docuconf-go checkout: it declares
docuconf-go's `conformance/export/fixture.yaml` in C# (`tests/Docuconf.Tests/ConformanceExportTests.cs`), exports it,
and compares it with `conformance/export/golden.cue` using `docuconf conformance export`:

```sh
DOCUCONF_GO_DIR=../docuconf-go scripts/conformance.sh
```

### Develop

```sh
dotnet test   # net10.0 and net8.0; needs the cue CLI: go install cuelang.org/go/cmd/cue@v0.17.1
```

The README's C# snippets compile in `tests/Docuconf.Readme.Tests` and `examples/orders`, and a test fails when they
drift. `scripts/smoke-consumer.sh` packs the library, installs it from a local feed into clean .NET 10 and .NET 8 apps,
and checks the analyzer, the build-time export and startup validation. Releases are published from CI with NuGet
trusted publishing; see [RELEASING.md](RELEASING.md). The tests check exported contracts against a copy of the CUE
meta-schema in `tests/Docuconf.Tests/spec`; refresh it with `scripts/sync-spec.sh`.

Report a vulnerability privately, as [SECURITY.md](SECURITY.md) describes. Licence:
[MIT](https://github.com/docuconf/docuconf-dotnet/blob/main/LICENSE).

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;

namespace Docuconf.Tests;

/// <summary>
/// The shared export check (SPEC §11.2 item 3, §12): docuconf-go's <c>conformance/export/fixture.yaml</c>, declared
/// with this SDK's API in <see cref="FixtureOptions"/>, exports to a contract that <c>docuconf conformance export</c>
/// finds equal, as data, to <c>conformance/export/golden.cue</c>.
/// </summary>
/// <remarks>
/// The golden contract is read from the docuconf-go checkout next to <c>DOCUCONF_CONFORMANCE</c> (or
/// <c>DOCUCONF_GO_DIR</c>), and the CLI from <c>DOCUCONF_CLI</c> or <c>PATH</c>. With
/// <c>DOCUCONF_REQUIRE_CONFORMANCE=1</c> either one missing fails the test instead of skipping it.
/// </remarks>
public sealed class ConformanceExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("docuconf-fixture-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_fixture_exports_to_the_golden_contract()
    {
        var golden = Golden();
        var cli = Cli();
        var missing = (golden is null ? "conformance/export/golden.cue (set DOCUCONF_GO_DIR or DOCUCONF_CONFORMANCE)" : null)
            ?? (cli is null ? "the docuconf CLI (set DOCUCONF_CLI, or put docuconf on PATH)" : null);
        if (missing is not null)
        {
            Assert.False(Environment.GetEnvironmentVariable("DOCUCONF_REQUIRE_CONFORMANCE") == "1", $"Cannot run the export check without {missing}.");
            Assert.Skip($"Cannot run the export check without {missing}.");
        }

        var model = ContractReader.Read([typeof(FixtureOptions)]);
        var exported = Path.Join(_root, "exported.cue");
        File.WriteAllText(exported, CueWriter.Write(model, new CueWriter.Generator("Docuconf.Options", "0.0.0-test"), appVersion: "1.0.0"));

        var start = new ProcessStartInfo(cli!, ["conformance", "export", "--golden", golden!, exported])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0 && output.Contains("matches", StringComparison.Ordinal), $"{output}\n{File.ReadAllText(exported)}");
    }

    private static string? Golden()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOCUCONF_GO_DIR") is { Length: > 0 } go ? Path.Join(go, "conformance", "export", "golden.cue") : null,
            Environment.GetEnvironmentVariable("DOCUCONF_CONFORMANCE") is { Length: > 0 } cases ? Path.Join(Path.GetDirectoryName(Path.GetFullPath(cases)), "export", "golden.cue") : null,
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }

    private static string? Cli()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("DOCUCONF_CLI") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Join(p, "docuconf")))
            .Append(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "go", "bin", "docuconf"));
        return candidates.FirstOrDefault(c => c is { Length: > 0 } && File.Exists(c));
    }
}

/// <summary>
/// docuconf-go's <c>conformance/export/fixture.yaml</c>, declared as a .NET app would. Variables at the configuration
/// root take their environment names with <see cref="ConfigurationKeyNameAttribute"/>, so their configuration key is
/// the variable's own name and is not exported; <c>APP_NAME</c> keeps its <c>App:Name</c> key.
/// </summary>
[ConfigContract("docuconf-fixture")]
public sealed class FixtureOptions
{
    public FixtureApp App { get; set; } = new();

    [Required, Secret, UrlSchemes("postgres", "postgresql"), MaxLength(2048)]
    [Display(GroupName = "database", Description = "Primary Postgres connection string")]
    [ConfigurationKeyName("DATABASE_URL")]
    public string DatabaseUrl { get; set; } = "";

    [Range(1, 65535)]
    [Description("HTTP listen port")]
    [ConfigurationKeyName("PORT")]
    public int Port { get; set; } = 8080;

    [Range(0.0, 1.0)]
    [Description("Fraction of requests traced")]
    [ConfigurationKeyName("TRACE_RATIO")]
    public double TraceRatio { get; set; } = 0.25;

    [Description("Serve the debug endpoints")]
    [ConfigurationKeyName("DEBUG")]
    public bool Debug { get; set; }

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    [Description("Upstream request timeout")]
    [ConfigurationKeyName("REQUEST_TIMEOUT")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(90);

    [AllowedValues("debug", "info", "warn", "error")]
    [Description("Minimum log level")]
    [ConfigurationKeyName("LOG_LEVEL")]
    public string LogLevel { get; set; } = "info";

    [Csv(";"), Length(1, 5), ItemLength(1, 255)]
    [Description("CORS origins allowed to call the API")]
    [ConfigurationKeyName("ALLOWED_ORIGINS")]
    public List<string>? AllowedOrigins { get; set; }

    [ItemRange(0, 1023)]
    [Description("Shards this instance owns")]
    [ConfigurationKeyName("SHARDS")]
    public int[]? Shards { get; set; }

    [KeySet(KeyMinLength = 32, KeyMaxLength = 256)]
    [Description("Keys that verify webhook signatures")]
    [ConfigurationKeyName("WEBHOOK_KEYS")]
    public KeySet? WebhookKeys { get; set; }

    [JsonVar(MaxLength = 1024)]
    [Description("Per-client rate limits")]
    [ConfigurationKeyName("RATE_LIMITS")]
    public FixtureRateLimits? RateLimits { get; set; } = new() { PerMinute = 60 };

    [Deprecated("Use PORT instead", ReplacedBy = "PORT")]
    [Description("Port the service used to listen on")]
    [ConfigurationKeyName("OLD_PORT")]
    public long? OldPort { get; set; }

    [Secret]
    [Description("Password of the partner keystore")]
    [ConfigurationKeyName("PARTNER_PASSWORD")]
    public string? PartnerPassword { get; set; }

    [Required]
    [ConfigFile("/etc/app/settings/settings.json", PathEnv = "SETTINGS_FILE", Reload = Reload.Watch, MaxSize = 65536)]
    [Display(GroupName = "general", Description = "Application settings")]
    public ConfigFile<FixtureSettings> Settings { get; set; } = new();

    [ConfigFile("/etc/app/rules/rules.yaml")]
    [Description("Routing rules")]
    public FixtureSettings? Rules { get; set; }

    [ConfigFile("/etc/app/flags/flags.toml")]
    [Description("Feature defaults")]
    public FixtureSettings? Flags { get; set; }

    [TlsFile("/etc/app/tls", Reload = Reload.Watch, DnsNames = ["app.example.test", "api.example.test"],
        KeyAlgorithms = KeyAlgorithms.ECDSA | KeyAlgorithms.Ed25519, MinRemaining = "720h", RequireCA = true)]
    [Description("Certificate the service serves HTTPS with")]
    public TlsKeyPair? ServingTls { get; set; }

    [CaBundleFile("/etc/app/trust/bundle.pem", MinCertificates = 2)]
    [Description("CAs the service trusts")]
    public CaBundle? Trust { get; set; }

    [KeystoreFile("/etc/app/partner/keystore.p12", PasswordProperty = nameof(PartnerPassword))]
    [Description("Client certificate for the partner API")]
    public Keystore? Partner { get; set; }

    [TextFile("/etc/app/licence/licence.key", Pattern = "^[A-Z0-9-]+\\n?$", MinLength = 8, MaxLength = 64)]
    [Description("Licence key")]
    public string? Licence { get; set; }

    [BinaryFile("/data/geoip/geoip.mmdb", MaxSize = 134217728)]
    [Deprecated("Use geo-db instead", ReplacedBy = "geo-db")]
    [Description("GeoIP database")]
    public BinaryFile? Geoip { get; set; }

    [BinaryFile("/data/geo-db/geo.mmdb")]
    [Description("City-level location database")]
    public BinaryFile? GeoDb { get; set; }
}

public sealed class FixtureApp
{
    /// <summary>Service name, used in logs and metrics</summary>
    /// <remarks>Lower case, as a DNS label allows.</remarks>
    [EnvName("APP_NAME"), StringLength(40, MinimumLength = 2), RegularExpression("^[a-z][a-z0-9-]*$")]
    [Display(GroupName = "general"), Examples("orders", "billing")]
    public string Name { get; set; } = "orders";
}

public sealed class FixtureRateLimits
{
    [Required, Range(1, int.MaxValue)]
    public int PerMinute { get; set; }

    [Range(0, int.MaxValue)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Burst { get; set; }
}

public sealed class FixtureSettings
{
    [Required, MinLength(1)]
    public string Name { get; set; } = "";

    [Required, Range(1, int.MaxValue)]
    public int Replicas { get; set; }

    public List<string> Tags { get; set; } = [];
}

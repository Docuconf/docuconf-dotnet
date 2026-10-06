using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

[ConfigContract("gateway", Section = "Gateway")]
public sealed class GatewayOptions
{
    [Required, Secret, UrlSchemes("postgres")]
    [Description("Database connection string")]
    public string DatabaseUrl { get; set; } = "";

    [Range(1, 65535)]
    [Description("HTTP listen port")]
    public int Port { get; set; } = 8080;

    [Description("Whether to trace requests")]
    public bool Tracing { get; set; } = true;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    [Description("Upstream request timeout")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    [Required, MinLength(1)]
    [Description("Kafka brokers to connect to")]
    public List<string> Brokers { get; set; } = [];

    [Required, Secret]
    [Description("Password for the partner keystore")]
    public string KeystorePassword { get; set; } = "";

    [Required]
    [TlsFile("/etc/gw/tls", DnsNames = ["gw.internal"], MinRemaining = "720h", RequireCA = true, Reload = Reload.Watch)]
    [Description("Serving certificate")]
    public TlsKeyPair Tls { get; set; } = new();

    [Required]
    [ConfigFile("/etc/gw/routes/routes.json", MaxSize = 65536)]
    [Description("Routing table")]
    public Routes Routes { get; set; } = new();

    [KeystoreFile("/etc/gw/partner/keystore.p12", PasswordProperty = nameof(KeystorePassword))]
    [Description("Client certificate for the partner API")]
    public Keystore? Partner { get; set; }

    [CaBundleFile("/etc/gw/ca/bundle.pem", PathEnv = "SSL_CERT_FILE")]
    [Description("Private CAs to trust")]
    public CaBundle? TrustedCas { get; set; }

    [Required]
    [TextFile("/etc/gw/license/license.key", Pattern = "^[A-Z]{4}\\n?$")]
    [Description("Licence key")]
    public string License { get; set; } = "";

    [JsonVar]
    [Description("Per-client rate limit")]
    public RateLimit? RateLimit { get; set; }
}

public sealed class RateLimit
{
    [Required, Range(1, 10000)]
    [Description("Requests per second")]
    public int Rps { get; set; }

    [Range(0, 10000)]
    [Description("Extra requests allowed in a burst")]
    public int Burst { get; set; }

    [AllowedValues("ip", "token")]
    [Description("What identifies a client")]
    public string Key { get; set; } = "ip";
}

public sealed class Upstream
{
    [Required, RegularExpression("[a-z0-9.-]+")]
    [Description("Upstream host name")]
    public string Host { get; set; } = "";

    [Range(1, 100)]
    [Description("Share of traffic")]
    public int Weight { get; set; } = 1;
}

[ConfigContract("throttle", Section = "Throttle")]
[ConfigOverlay("platform", "/app/overlay/overrides.json")]
public sealed class ThrottleOptions
{
    [Required, JsonVar]
    [Description("Rate limit for the public API")]
    public RateLimit Limits { get; set; } = null!;

    [JsonVar]
    [Description("Upstream hosts and their weights")]
    public List<Upstream> Upstreams { get; set; } = [new() { Host = "a.svc", Weight = 1 }];

    [Secret, JsonVar]
    [Description("Partner API credentials")]
    public Credentials? Partner { get; set; }
}

public sealed class Credentials
{
    [Required, MinLength(8)]
    [Description("Client secret")]
    public string Secret { get; set; } = "";
}

public sealed class Routes
{
    [Required, MinLength(1)]
    [Description("Routes, matched in order")]
    public List<Route> Items { get; set; } = [];
}

public sealed class Route
{
    [Required, RegularExpression("/.*")]
    [Description("Path prefix to match")]
    public string Match { get; set; } = "";

    [Required, Url]
    [Description("Upstream URL")]
    public string Upstream { get; set; } = "";
}

/// <summary>A temp directory laid out like the gateway's container filesystem, with valid inputs.</summary>
public sealed class GatewayFiles : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("docuconf-").FullName;

    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    public CertificateAuthority Ca { get; }

    public GatewayFiles()
    {
        Ca = CertificateAuthority.Create("Test CA", Now);
        WriteTls(Ca, "gw.internal", Now.AddDays(-1), Now.AddDays(90));
        Write("/etc/gw/routes/routes.json", """{ "items": [ { "match": "/billing", "upstream": "http://billing.svc:8080" } ] }""");
        Write("/etc/gw/license/license.key", "ABCD\n");
        Write("/etc/gw/ca/bundle.pem", Ca.Certificate.ExportCertificatePem());
        var (leaf, key) = Ca.Issue("partner.client", Now.AddDays(-1), Now.AddDays(30));
        using var withKey = leaf.CopyWithPrivateKey(key);
        WriteBytes("/etc/gw/partner/keystore.p12", withKey.Export(X509ContentType.Pkcs12, "s3cret"));
    }

    public Dictionary<string, string?> Config() => new()
    {
        ["Gateway:DatabaseUrl"] = "postgres://app:hunter2@db:5432/gw",
        ["Gateway:Brokers:0"] = "kafka-0:9092",
        ["Gateway:KeystorePassword"] = "s3cret",
    };

    public void WriteTls(CertificateAuthority ca, string dnsName, DateTimeOffset notBefore, DateTimeOffset notAfter, ECDsa? keyOverride = null, string? caPem = null)
    {
        var (cert, key) = ca.Issue(dnsName, notBefore, notAfter);
        Write("/etc/gw/tls/tls.crt", cert.ExportCertificatePem());
        Write("/etc/gw/tls/tls.key", (keyOverride ?? key).ExportPkcs8PrivateKeyPem());
        Write("/etc/gw/tls/ca.crt", caPem ?? ca.Certificate.ExportCertificatePem());
    }

    public void Write(string path, string content)
    {
        var full = Path.Join(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void WriteBytes(string path, byte[] content)
    {
        var full = Path.Join(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    public void Delete(string path) => File.Delete(Path.Join(Root, path));

    public T Resolve<T>(Dictionary<string, string?> config, DateTimeOffset? now = null, string? terminationLog = null)
        where T : class
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDocuconf<T>(s =>
        {
            s.FileRoot = Root;
            s.TerminationLogPath = terminationLog ?? Path.Join(Root, "no-termination-log");
            s.Clock = () => now ?? Now;
        });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public sealed class CertificateAuthority
{
    public required X509Certificate2 Certificate { get; init; }

    public static CertificateAuthority Create(string name, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return new CertificateAuthority { Certificate = request.CreateSelfSigned(now.AddDays(-2), now.AddDays(400)) };
    }

    public (X509Certificate2 Certificate, ECDsa Key) Issue(string dnsName, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var serial = RandomNumberGenerator.GetBytes(12);
        return (request.Create(Certificate, notBefore, notAfter, serial), key);
    }
}

[ConfigContract("catalog", Section = "Catalog")]
[ConfigOverlay("platform", "/app/config/appsettings.Production.json", ReloadOnChange = true, Description = "Platform overrides, layered over the appsettings files")]
public sealed class CatalogOptions
{
    [Range(1, 500)]
    [Description("Items per page")]
    public int PageSize { get; set; } = 20;

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    [Description("How long catalog pages are cached")]
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    [Description("Categories shown on the home page")]
    public List<string> FeaturedCategories { get; set; } = [];

    [Required, UrlSchemes("https")]
    [Description("Search service endpoint")]
    public string SearchUrl { get; set; } = "";
}

[ConfigContract("broken-overlays", Section = "Broken")]
[ConfigOverlay("Platform", "app/config/settings.json")]
[ConfigOverlay("yaml", "/app/yaml/settings.yaml")]
[ConfigOverlay("one", "/app/shared/a.json")]
[ConfigOverlay("two", "/app/shared/b.json")]
public sealed class BrokenOverlayOptions
{
    [Description("Any value at all")]
    public string Value { get; set; } = "";
}

// An overlay at the filesystem root: with DOCUCONF_FILE_ROOT set to the app's own directory it lands among the
// app's files, which the runtime must refuse.
[ConfigContract("rooted", Section = "Rooted")]
[ConfigOverlay("platform", "/overlay.json")]
public sealed class RootedOverlayOptions
{
    [Description("Any value at all")]
    public string Value { get; set; } = "";
}

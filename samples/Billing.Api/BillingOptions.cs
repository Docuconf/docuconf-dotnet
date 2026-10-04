using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf;

namespace Billing.Api;

/// <summary>
/// Everything the billing API reads from its environment. This one class is the contract:
/// docuconf exports it for the platform and validates it at startup.
/// </summary>
[ConfigContract("billing-api", Section = "Billing")]
public sealed class BillingOptions
{
    [Required, Secret, UrlSchemes("postgres", "postgresql")]
    [Description("Primary Postgres connection string")]
    public string DatabaseUrl { get; set; } = "";

    [Range(1, 65535)]
    [Description("HTTP listen port")]
    public int Port { get; set; } = 8080;

    [Description("Minimum log level emitted")]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    [Description("Timeout for calls to the warehouse service")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Set in appsettings.json, so it has a default; the platform may override it.
    [Required, MinLength(1)]
    [Description("CORS origins allowed to call the API")]
    public string[] AllowedOrigins { get; set; } = [];

    // Set only in appsettings.Production.json: required everywhere else.
    [Required, UrlSchemes("https")]
    [Description("Base URL of the warehouse service")]
    public string WarehouseApi { get; set; } = "";

    [Required]
    [TlsFile("/etc/billing/tls", DnsNames = ["billing.internal"], KeyAlgorithms = KeyAlgorithms.ECDSA | KeyAlgorithms.RSA,
        MinRemaining = "720h", Reload = Reload.Watch)]
    [Description("Certificate the API serves HTTPS with")]
    public TlsKeyPair ServingCertificate { get; set; } = new();

    [Required]
    [ConfigFile("/etc/billing/rates/rates.json")]
    [Description("Pricing tiers by monthly volume")]
    public RatesConfig Rates { get; set; } = new();

    [CaBundleFile("/etc/billing/ca/bundle.pem", PathEnv = "SSL_CERT_FILE")]
    [Description("Private CAs trusted for calls to internal services")]
    public CaBundle? InternalCa { get; set; }

    [Required]
    [TextFile("/etc/billing/license/license.key", Pattern = "^[A-Z0-9]{5}(-[A-Z0-9]{5}){3}\\n?$")]
    [Description("Licence key for the tax engine")]
    public string LicenseKey { get; set; } = "";

    // A dictionary cannot be carried by environment variables, so it stays out of the contract
    // (export prints a warning) and still binds from appsettings.
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];
}

/// <summary>The rates file. Its JSON Schema in the contract is generated from this type.</summary>
public sealed class RatesConfig
{
    [Required, AllowedValues("USD", "EUR")]
    [Description("Currency all rates are in")]
    public string Currency { get; set; } = "USD";

    [Required, MinLength(1)]
    [Description("Tiers, cheapest last")]
    public List<RateTier> Tiers { get; set; } = [];
}

/// <summary>One pricing tier.</summary>
public sealed class RateTier
{
    [Range(1, int.MaxValue)]
    [Description("Upper bound of the tier, in transactions per month")]
    public int UpTo { get; set; }

    [Range(0.0001, 1000.0)]
    [Description("Price per transaction")]
    public double Rate { get; set; }
}

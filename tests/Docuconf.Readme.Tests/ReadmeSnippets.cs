// The README's code blocks live here (and in examples/orders), so they compile and, for the tests, run.
// ReadmeTests fails when a README block is not found in one of these files; edit both together.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography.X509Certificates;
using Docuconf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Orders.Api;

namespace Docuconf.Readme.Tests;

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

public sealed class RatesConfig
{
    [Required, AllowedValues("USD", "EUR")]
    [Description("Currency all rates are in")]
    public string Currency { get; set; } = "USD";
}

public sealed class RateLimit
{
    [Range(1, 10000)]
    [Description("Requests per second")]
    public int Rps { get; set; } = 10;

    [Range(0, 10000)]
    [Description("Extra requests allowed in a burst")]
    public int Burst { get; set; }
}

[ConfigContract("catalog", Section = "Catalog")]
[ConfigOverlay("platform", "/app/config/appsettings.Production.json", ReloadOnChange = true)]
public sealed class CatalogOptions { /* ... */ }

public static class Snippets
{
    public static void Overlays(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddDocuconf<CatalogOptions>();   // appsettings < overlay < environment, then binds and validates
    }

    public static void ThrowInstead(WebApplicationBuilder builder)
    {
        builder.AddDocuconf<OrdersOptions>(s => s.ThrowOnInvalid = true);
    }

    public static void ContractFirst()
    {
        var config = DocuconfContract.FromFile("contract.json").LoadOrExit();   // the process environment
        long port = config.Get<long>("PORT");
        int workers = config.Get<int>("WORKERS");   // converts, or throws when the value does not fit
        _ = (port, workers);
    }

    public static void ServingCertificatePerHandshake(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
        {
            var tls = kestrel.ApplicationServices.GetRequiredService<IOptions<BillingOptions>>().Value.ServingCertificate;
            https.ServerCertificateSelector = (_, _) => tls.Current;   // cheap: the files are looked at every 30 s at most
        }));
    }

    public static HealthCheckResult ReloadHealth(BillingOptions options)
    {
        ReloadStatus status = options.ServingCertificate.Status;
        // status.Generation: 1 after startup, plus one per accepted reload
        // status.LastReload: when the last accepted reload happened (null until one has)
        // status.LastRejection: the last rejected change (At, Input, Codes such as ["key_mismatch"]),
        //                       cleared when a later change is accepted
        return status.LastRejection is { } rejected
            ? HealthCheckResult.Degraded($"{rejected.Input}: change rejected at {rejected.At:u} ({string.Join(", ", rejected.Codes)})")
            : HealthCheckResult.Healthy($"generation {status.Generation}");
    }
}

[ConfigContract("partner-client", Section = "Partner")]
public sealed class PartnerOptions
{
    [Required, Secret]
    [Description("Password of the partner keystore")]
    public string? KeystorePassword { get; set; }

    [KeystoreFile("/etc/partner/keystore.p12", PasswordProperty = nameof(KeystorePassword), Reload = Reload.Watch)]
    [Description("Client certificate for the partner API")]
    public Keystore? ClientCertificate { get; set; }
}

public sealed class PartnerClient : IDisposable
{
    private readonly IDisposable _subscription;
    private volatile HttpClient _client;

    public PartnerClient(IOptions<PartnerOptions> options)
    {
        var keystore = options.Value.ClientCertificate!;
        _client = Create(keystore.Current);
        // Called with the new certificate after a changed keystore passes the checks; never for a rejected one.
        _subscription = keystore.OnChange(certificate => _client = Create(certificate));
    }

    public HttpClient Client => _client;

    private static HttpClient Create(X509Certificate2 certificate) =>
        new(new SocketsHttpHandler { SslOptions = { ClientCertificates = [certificate] } });

    public void Dispose() => _subscription.Dispose();
}

public class PartnerOptionsTests
{
    [Fact]
    public void The_watched_keystore_declaration_is_valid()
    {
        var problem = Assert.Single(DocuconfTesting.Validate<PartnerOptions>(new Dictionary<string, string>()));
        Assert.Equal(("missing_required", "PARTNER__KEYSTOREPASSWORD"), (problem.Code, problem.Input));
    }
}

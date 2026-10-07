// The README's code blocks live here (and in examples/orders), so they compile and, for the tests, run.
// ReadmeTests fails when a README block is not found in one of these files; edit both together.
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf;
using Microsoft.AspNetCore.Builder;
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
}

using Billing.Api;
using Docuconf;
using Microsoft.Extensions.Options;

// `dotnet Billing.Api.dll docuconf export contract.cue` writes the contract and exits.
if (DocuconfExport.RunIfRequested(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Binds the Billing section, loads the file inputs, and fails startup with every problem listed.
builder.Services.AddDocuconf<BillingOptions>();

var app = builder.Build();

app.MapGet("/healthz", (IOptions<BillingOptions> options) =>
{
    var o = options.Value;
    var certificate = o.ServingCertificate.Current; // reloaded when cert-manager rotates it
    return Results.Ok(new
    {
        o.Port,
        o.LogLevel,
        o.Rates.Currency,
        Tiers = o.Rates.Tiers.Count,
        CertificateExpires = certificate.NotAfter,
    });
});

app.Run();

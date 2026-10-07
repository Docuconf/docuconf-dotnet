using Docuconf;
using Microsoft.Extensions.Options;
using Orders.Api;

// `dotnet Orders.Api.dll docuconf export contract.cue` writes the contract and exits.
if (DocuconfExport.RunIfRequested(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Binds the Orders section (ORDERS__* environment variables, appsettings) and validates it at startup.
builder.Services.AddDocuconf<OrdersOptions>();

var app = builder.Build();

OrdersOptions orders;
try
{
    orders = app.Services.GetRequiredService<IOptions<OrdersOptions>>().Value;
}
catch (OptionsValidationException ex)
{
    // Every problem at once, each with a stable code. Secret values are never in the messages.
    Console.Error.WriteLine("Invalid configuration:");
    foreach (var failure in ex.Failures)
    {
        Console.Error.WriteLine("  " + failure);
    }

    Environment.ExitCode = 1;
    return;
}

app.MapGet("/healthz", () => "ok");
app.MapGet("/config", () => new
{
    orders.Port,
    orders.LogLevel,
    DatabaseUrl = "***", // [Secret]
    orders.AllowedOrigins,
    RequestTimeout = orders.RequestTimeout.ToString(),
    orders.WorkerCount,
});

app.Run($"http://0.0.0.0:{orders.Port}");

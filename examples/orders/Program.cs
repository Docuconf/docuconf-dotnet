using Docuconf;
using Orders.Api;

// `dotnet Orders.Api.dll docuconf export contract.cue` writes the contract and exits.
if (DocuconfExport.RunIfRequested(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.AddDocuconf<OrdersOptions>();   // binds the Orders section and validates it at startup
var app = builder.Build();

// The validated options, or every problem on stderr and exit status 1.
var orders = app.Services.LoadOrExit<OrdersOptions>();

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

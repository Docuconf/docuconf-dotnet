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
    WebhookKeys = "***", // [Secret], set or not
});

// Payment webhooks, signed with any key in WEBHOOK_KEYS (see OrdersOptions.cs for how to rotate it).
app.MapPost("/webhooks/payments", async (HttpRequest request) =>
{
    if (await Webhook.ReadBody(request.Body) is not { } body)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    return Webhook.Verify(orders.WebhookKeys, body, request.Headers["X-Signature"])
        ? Results.NoContent()
        : Results.Unauthorized();
});

app.Run($"http://0.0.0.0:{orders.Port}");

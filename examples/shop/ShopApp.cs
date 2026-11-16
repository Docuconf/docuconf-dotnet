namespace Shop;

/// <summary>What the app does once its configuration is valid: one line for its role.</summary>
public static class ShopApp
{
    public static string Describe(string role, ShopOptions options) => role switch
    {
        "web" => $"shop web: port {options.Port}, origins {string.Join(',', options.AllowedOrigins ?? [])}",
        "worker" => $"shop worker: queues {string.Join(',', options.Queues ?? [])}, webhooks {(options.WebhooksEnabled ? "on" : "off")}",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "docuconf accepts only the declared roles"),
    };
}

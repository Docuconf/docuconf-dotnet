using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf;

namespace Shop;

// One image, two roles (SPEC §4.9): the platform sets DOCUCONF_ROLE to web or worker, and docuconf declares and checks
// it. Every input has an explicit [EnvName], so the variables are the contract's names, not SHOP__*.
[ConfigContract("shop", Section = "Shop"), Roles("web", "worker"), Includes("otel@1")]
public sealed class ShopOptions
{
    [EnvName("DATABASE_URL"), Required, Secret, UrlSchemes("postgres", "postgresql")]
    [Description("Postgres connection string")]
    public string DatabaseUrl { get; set; } = "";

    // Web only: required (or defaulted) when DOCUCONF_ROLE is web, optional for the worker.
    [EnvName("PORT"), Range(1, 65535), Roles("web")]
    [Description("Port the web server listens on")]
    public int Port { get; set; } = 8080;

    // [Csv]: one value, a,b, as the platform writes it.
    [EnvName("ALLOWED_ORIGINS"), Csv, Required, Roles("web")]
    [Description("Origins the web server accepts browser requests from")]
    public List<string>? AllowedOrigins { get; set; }

    // Worker only.
    [EnvName("QUEUES"), Csv, Required, Roles("worker")]
    [Description("Queues the worker takes jobs from, in priority order")]
    public List<string>? Queues { get; set; }

    [EnvName("WEBHOOKS_ENABLED"), Roles("worker")]
    [Description("Send order webhooks to partners")]
    public bool WebhooksEnabled { get; set; }

    [EnvName("LOG_FORMAT"), AllowedValues("text", "json")]
    [Description("Log output format")]
    public string LogFormat { get; set; } = "text";

    // Required only while LOG_FORMAT is json (SPEC §4.2.1); a conditional input has no default.
    [EnvName("LOG_SCHEMA_URL"), UrlSchemes("https"), RequiredIf("LOG_FORMAT", "json")]
    [Description("JSON schema the structured logs follow")]
    public string? LogSchemaUrl { get; set; }

    // A file the worker reads only while it sends webhooks: required when WEBHOOKS_ENABLED is true, and never for web.
    [TextFile("/etc/shop/webhooks/signing.key", Name = "webhook-signing-key", MinLength = 32), Secret]
    [RequiredIf("WEBHOOKS_ENABLED", true), Roles("worker")]
    [Description("Key that signs outgoing order webhooks")]
    public string? WebhookSigningKey { get; set; }
}

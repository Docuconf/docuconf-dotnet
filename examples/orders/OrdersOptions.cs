using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf;

namespace Orders.Api;

// The Orders section: Orders:Port is the environment variable ORDERS__PORT, and so on.
// Initializers are the defaults the contract records.
[ConfigContract("orders-api", Section = "Orders")]
public sealed class OrdersOptions
{
    [Range(1, 65535)]
    [Description("HTTP listen port")]
    public int Port { get; set; } = 8080;

    [AllowedValues("debug", "info", "warn", "error")]
    [Description("Minimum level of log messages to write")]
    public string LogLevel { get; set; } = "info";

    // [Secret]: the platform must supply it from a Kubernetes Secret, and docuconf never prints it.
    // [MaxLength] bounds the URL in characters; a longer one fails startup with out_of_range.
    [Required, Secret, UrlSchemes("postgres"), MaxLength(2048)]
    [Description("Postgres connection string for the orders database")]
    public string DatabaseUrl { get; set; } = "";

    // A list arrives as ORDERS__ALLOWEDORIGINS__0, ORDERS__ALLOWEDORIGINS__1, ...
    [MinLength(1)]
    [Description("Origins allowed to call the API from a browser")]
    public List<string> AllowedOrigins { get; set; } = ["http://localhost:3000"];

    // A TimeSpan arrives as hh:mm:ss (00:00:30); the platform writes "30s" and renders it that way.
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    [Description("Time allowed to handle one request")]
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // An XML doc comment works instead of [Description]: the <summary> is the description, and the <remarks> are the
    // details, longer docs for docuconf docs (the project sets GenerateDocumentationFile).

    /// <summary>Background workers that process new orders.</summary>
    /// <remarks>
    /// <para>
    /// Each worker holds one connection from the pool of <see cref="DatabaseUrl"/>, so keep this below the database's
    /// connection limit.
    /// </para>
    /// <list type="bullet">
    /// <item><description>Raise it when the order queue backs up.</description></item>
    /// <item><description>Lower it when the database is the bottleneck.</description></item>
    /// </list>
    /// </remarks>
    [Range(1, 64)]
    public int WorkerCount { get; set; } = 4;

    // A key set (SPEC §6.1): [Csv] makes the list one value, "old,new", so one Kubernetes Secret key holds it, and
    // [EnvName] gives it a name of its own. No initializer: a [Secret] has no default.

    /// <summary>Keys that verify the signature on incoming payment webhooks.</summary>
    /// <remarks>
    /// <para>
    /// A webhook is accepted when it is signed with any key in the list, so the key can be rotated without turning
    /// webhooks away. To rotate:
    /// </para>
    /// <list type="number">
    /// <item><description>add the new key as the second item, and roll out;</description></item>
    /// <item><description>switch the sender to the new key;</description></item>
    /// <item><description>remove the old key, and roll out.</description></item>
    /// </list>
    /// <para>
    /// Each key is 32 to 256 characters, so an empty or truncated key fails at boot. Without this variable, the service
    /// rejects every webhook.
    /// </para>
    /// </remarks>
    [Csv, EnvName("WEBHOOK_KEYS"), Secret, MinLength(1), MaxLength(2), ItemLength(32, 256)]
    public List<string>? WebhookKeys { get; set; }
}

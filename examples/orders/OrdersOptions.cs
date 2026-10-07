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
    [Required, Secret, UrlSchemes("postgres")]
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

    [Range(1, 64)]
    [Description("Background workers that process new orders")]
    public int WorkerCount { get; set; } = 4;
}

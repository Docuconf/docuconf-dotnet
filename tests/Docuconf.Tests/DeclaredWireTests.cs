using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
}

[ConfigContract("wire", Section = "Wire")]
public sealed class WireOptions
{
    [Description("Where to send webhooks")]
    public Uri? Callback { get; set; }

    [UrlSchemes("https")]
    [Description("Upstream base URL")]
    public string Upstream { get; set; } = "https://api.example.com";

    [Description("Minimum level to log")]
    public LogLevel Level { get; set; } = LogLevel.Info;

    [Description("Fraction of traffic to sample")]
    public double Ratio { get; set; } = 0.5;

    [Description("Whether to trace requests")]
    public bool Tracing { get; set; }

    [Description("Signed offset")]
    public long Offset { get; set; }
}

// The conformance suite runs through the contract-first mode; these check that options classes, which bind through
// Microsoft.Extensions.Configuration, accept and reject the same strings.
public sealed class DeclaredWireTests
{
    private static WireOptions Resolve(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddDocuconf<WireOptions>(s => s.TerminationLogPath = "/nonexistent/termination-log");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<WireOptions>>().Value;
    }

    private static string Fails(string key, string value)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(new() { [key] = value }));
        var failure = Assert.Single(ex.Failures);
        return failure[1..failure.IndexOf(']')];
    }

    [Theory]
    [InlineData("Wire:Callback", "hooks.svc:8080", "invalid_type")]     // url/no scheme
    [InlineData("Wire:Upstream", "api.internal:443", "invalid_type")]
    [InlineData("Wire:Upstream", "http://api.internal", "invalid_scheme")]
    [InlineData("Wire:Level", "WARN", "not_in_enum")]                    // enum/values are case-sensitive
    [InlineData("Wire:Level", "verbose", "not_in_enum")]
    [InlineData("Wire:Level", "2", "not_in_enum")]
    [InlineData("Wire:Ratio", "0,5", "invalid_type")]                    // float/a decimal comma ...
    [InlineData("Wire:Ratio", "NaN", "invalid_type")]
    [InlineData("Wire:Ratio", "Infinity", "invalid_type")]
    [InlineData("Wire:Ratio", " 0.5", "invalid_type")]                   // values are never trimmed
    [InlineData("Wire:Tracing", "yes", "invalid_type")]
    [InlineData("Wire:Tracing", " true", "invalid_type")]
    [InlineData("Wire:Offset", "80.5", "invalid_type")]                  // int/not an integer
    [InlineData("Wire:Offset", "0x10", "invalid_type")]
    [InlineData("Wire:Offset", "99999999999999999999", "out_of_range")]  // int/beyond the 64-bit range
    public void Rejects_what_the_contract_rejects(string key, string value, string code) =>
        Assert.Equal(code, Fails(key, value));

    [Fact]
    public void Accepts_what_the_contract_accepts()
    {
        var options = Resolve(new()
        {
            ["Wire:Callback"] = "amqp://broker:5672",
            ["Wire:Level"] = "Warn",
            ["Wire:Ratio"] = "1e-1",
            ["Wire:Tracing"] = "TRUE",
            ["Wire:Offset"] = "9223372036854775807",
        });

        Assert.Equal("amqp://broker:5672", options.Callback!.OriginalString);
        Assert.Equal(LogLevel.Warn, options.Level);
        Assert.Equal(0.1, options.Ratio);
        Assert.True(options.Tracing);
        Assert.Equal(long.MaxValue, options.Offset);
    }
}

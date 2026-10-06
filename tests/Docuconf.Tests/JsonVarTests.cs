using System.Text.Json;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static Docuconf.Tests.ExportTests;

namespace Docuconf.Tests;

/// <summary><c>json</c> variables (SPEC §4.3): a structured value in one variable, checked against a schema from its type.</summary>
public sealed class JsonVarTests : IDisposable
{
    private readonly GatewayFiles _files = new();

    public void Dispose() => _files.Dispose();

    private static string Export(string? contentRoot = null)
    {
        var model = ContractReader.Read([typeof(ThrottleOptions)], new ContractReadSettings { ContentRoot = contentRoot });
        return CueWriter.Write(model, new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Throttle:Limits"] = """{"rps":10,"burst":20,"key":"token"}""",
    };

    private OptionsValidationException Fails(Dictionary<string, string?> config) =>
        Assert.Throws<OptionsValidationException>(() => _files.Resolve<ThrottleOptions>(config));

    private static string[] Codes(OptionsValidationException ex) => ex.Failures.Select(f => f[1..f.IndexOf(']')]).Order().ToArray();

    [Fact]
    public void Exports_type_json_with_a_schema_from_the_type()
    {
        var cue = Export();

        Assert.Contains("""
            		THROTTLE__LIMITS: {
            			type: "json"
            			description: "Rate limit for the public API"
            			required: true
            			configKey: "Throttle:Limits"
            			schema: {
            				"type": "object",
            """.Replace("\r\n", "\n", StringComparison.Ordinal), cue, StringComparison.Ordinal);
        Assert.Contains("""default: [{"host":"a.svc","weight":1}]""", cue, StringComparison.Ordinal);
        Assert.DoesNotContain("encoding", cue, StringComparison.Ordinal);

        var (exit, output) = Cue(CueModule(cue), "vet", "-c", "./out");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Appsettings_objects_become_json_defaults()
    {
        var root = Directory.CreateTempSubdirectory("docuconf-json-").FullName;
        try
        {
            File.WriteAllText(Path.Join(root, "appsettings.json"), """{ "Throttle": { "Limits": { "Rps": 5 } } }""");
            File.WriteAllText(Path.Join(root, "appsettings.Staging.json"), """{ "Throttle": { "Upstreams": [ { "Host": "b.svc", "Weight": 3 } ] } }""");

            var cue = Export(root);

            Assert.Contains("""default: {"rps":5,"burst":0,"key":"ip"}""", cue, StringComparison.Ordinal);
            Assert.Contains("""THROTTLE__UPSTREAMS: [{"host":"b.svc","weight":3}]""", cue, StringComparison.Ordinal);
            var (exit, output) = Cue(CueModule(cue), "vet", "-c", "./out");
            Assert.True(exit == 0, output);

            File.WriteAllText(Path.Join(root, "appsettings.json"), """{ "Throttle": { "Limits": { "Rps": 0 } } }""");
            var ex = Assert.Throws<ContractException>(() => Export(root));
            Assert.Contains(ex.Errors, e => e.Contains("appsettings.json: Throttle:Limits", StringComparison.Ordinal) && e.Contains("$.rps", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_schema_compiles_and_checks_values_like_the_app_does()
    {
        var module = CueModule(Export());
        Assert.Equal(0, Cue(module, "export", "./out", "-e", "vars.THROTTLE__LIMITS.schema", "--out", "json", "-o", "limits.schema.json").Exit);
        var (importExit, importOutput) = Cue(module, "import", "-f", "-p", "check", "-l", "#Limits:", "jsonschema:", "limits.schema.json", "-o", "limits.cue");
        Assert.True(importExit == 0, importOutput);

        File.WriteAllText(Path.Join(module, "good.json"), """{ "rps": 10, "key": "ip" }""");
        File.WriteAllText(Path.Join(module, "range.json"), """{ "rps": 0 }""");
        File.WriteAllText(Path.Join(module, "extra.json"), """{ "rps": 10, "window": 3 }""");
        File.WriteAllText(Path.Join(module, "enum.json"), """{ "rps": 10, "key": "cookie" }""");

        Assert.Equal(0, Cue(module, "vet", "-d", "#Limits", "limits.cue", "good.json").Exit);
        Assert.NotEqual(0, Cue(module, "vet", "-d", "#Limits", "limits.cue", "range.json").Exit);
        Assert.NotEqual(0, Cue(module, "vet", "-d", "#Limits", "limits.cue", "extra.json").Exit);
        Assert.NotEqual(0, Cue(module, "vet", "-d", "#Limits", "limits.cue", "enum.json").Exit);
    }

    // End to end: the platform renders a json value as an env var (compact JSON) and into an overlay (a nested
    // object at the configKey) with the CUE meta-schema; the app binds both forms to the same type.
    [Fact]
    public void Values_rendered_by_the_platform_bind_in_the_app()
    {
        var module = CueModule(Export());
        Directory.CreateDirectory(Path.Join(module, "platform"));
        File.WriteAllText(Path.Join(module, "platform", "render.cue"), """
            package platform

            import (
            	"docuconf.dev/contract"
            	app "docuconf.dev/out:throttle"
            )

            rendered: contract.#Render & {
            	contract: app
            	values: THROTTLE__LIMITS: {rps: 50, burst: 100, key: "token"}
            	overlays: platform: THROTTLE__UPSTREAMS: [{host: "a.svc", weight: 70}, {host: "b.svc", weight: 30}]
            }
            env: rendered.env
            file: rendered.configMaps[0].data["overrides.json"]
            """);
        var (envExit, envJson) = Cue(module, "export", "./platform", "-e", "env", "--out", "json");
        Assert.True(envExit == 0, envJson);
        var (fileExit, overlay) = Cue(module, "export", "./platform", "-e", "file", "--out", "text");
        Assert.True(fileExit == 0, overlay);

        var env = Assert.Single(JsonSerializer.Deserialize<List<Dictionary<string, string>>>(envJson)!);
        Assert.Equal("THROTTLE__LIMITS", env["name"]);
        var overlayPath = Path.Join(_files.Root, "app", "overlay", "overrides.json");
        Directory.CreateDirectory(Path.GetDirectoryName(overlayPath)!);
        File.WriteAllText(overlayPath, overlay);

        var config = new ConfigurationBuilder()
            .AddDocuconfOverlays<ThrottleOptions>(s => s.FileRoot = _files.Root)
            // What the environment variables provider does with THROTTLE__LIMITS.
            .AddInMemoryCollection([new(env["name"].Replace("__", ":", StringComparison.Ordinal), env["value"])])
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(config);
        services.AddDocuconf<ThrottleOptions>(s => s.FileRoot = _files.Root);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ThrottleOptions>>().Value;

        Assert.Equal((50, 100, "token"), (options.Limits.Rps, options.Limits.Burst, options.Limits.Key));
        Assert.Equal([("a.svc", 70), ("b.svc", 30)], options.Upstreams.Select(u => (u.Host, u.Weight)));
    }

    [Fact]
    public void Binds_a_json_string_and_keeps_the_default_when_unset()
    {
        var options = _files.Resolve<ThrottleOptions>(Valid());

        Assert.Equal((10, 20, "token"), (options.Limits.Rps, options.Limits.Burst, options.Limits.Key));
        Assert.Equal("a.svc", Assert.Single(options.Upstreams).Host);
        Assert.Null(options.Partner);
    }

    [Fact]
    public void Binds_a_nested_section_from_an_appsettings_file()
    {
        var config = new Dictionary<string, string?>
        {
            ["Throttle:Limits:Rps"] = "7",
            ["Throttle:Upstreams:0:Host"] = "c.svc",
            ["Throttle:Upstreams:0:Weight"] = "5",
        };

        var options = _files.Resolve<ThrottleOptions>(config);

        Assert.Equal(7, options.Limits.Rps);
        Assert.Equal(("c.svc", 5), (options.Upstreams[0].Host, options.Upstreams[0].Weight));
    }

    [Fact]
    public void Reports_every_problem_with_json_values()
    {
        var config = Valid();
        config["Throttle:Limits"] = """{"rps":0,"burst":-1}""";
        config["Throttle:Upstreams"] = """[{"host":"a.svc"},{"host":"b.svc","weight":500}]""";

        var ex = Fails(config);

        Assert.Equal(["schema_mismatch", "schema_mismatch", "schema_mismatch"], Codes(ex));
        Assert.Contains(ex.Failures, f => f.Contains("THROTTLE__LIMITS: $.rps:", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.Contains("THROTTLE__UPSTREAMS: $[1].weight:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"rps":10""")] // malformed
    [InlineData("""{"rps":"ten"}""")] // wrong type
    [InlineData("""{"rps":10,"window":3}""")] // a property the type does not have
    [InlineData("null")]
    public void Values_that_do_not_deserialize_are_invalid_type(string json)
    {
        var config = Valid();
        config["Throttle:Limits"] = json;

        var ex = Fails(config);

        Assert.Equal(["invalid_type"], Codes(ex));
        Assert.Contains("THROTTLE__LIMITS", ex.Failures.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_keys_in_a_nested_section_are_invalid_type()
    {
        var ex = Fails(new() { ["Throttle:Limits:Rps"] = "7", ["Throttle:Limits:Window"] = "3" });

        Assert.Equal(["invalid_type"], Codes(ex));
    }

    [Fact]
    public void A_required_json_variable_must_be_set()
    {
        var ex = Fails(new() { ["Throttle:Limits"] = "" });

        Assert.Equal(["missing_required"], Codes(ex));
    }

    [Theory]
    [InlineData("""{"secret":"hunter2"}""", "schema_mismatch")] // too short
    [InlineData("""{"secret":"hunter2-long","extra":"hunter2"}""", "invalid_type")]
    [InlineData("""{"secret":"hunter2-long""", "invalid_type")]
    public void Secret_json_values_are_never_printed(string json, string code)
    {
        var config = Valid();
        config["Throttle:Partner"] = json;

        var ex = Fails(config);

        Assert.Equal([code], Codes(ex));
        Assert.Contains("(value redacted)", ex.Failures.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Failures.Single(), StringComparison.Ordinal);
    }
}

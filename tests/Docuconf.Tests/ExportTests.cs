using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

public sealed class ExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("docuconf-export-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Export(params Type[] types)
    {
        var model = ContractReader.Read(types, new ContractReadSettings { ContentRoot = _root });
        return CueWriter.Write(model, new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));
    }

    private void AppSettings(string name, string json) => File.WriteAllText(Path.Join(_root, name), json);

    [Fact]
    public void Exports_the_gateway_contract()
    {
        AppSettings("appsettings.json", """{ "Gateway": { "Port": 9000, "Brokers": [ "kafka-0:9092", "kafka-1:9092" ] } }""");
        AppSettings("appsettings.Production.json", """{ "Gateway": { "Tracing": false } }""");

        var cue = Export(typeof(GatewayOptions));

        var golden = Path.Join(AppContext.BaseDirectory, "golden", "gateway.cue");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = Path.Join(AppContext.BaseDirectory, "..", "..", "..", "golden", "gateway.cue");
            File.WriteAllText(source, cue);
            return;
        }

        Assert.Equal(File.ReadAllText(golden), cue);
    }

    [Fact]
    public void Exported_contract_passes_the_meta_schema()
    {
        AppSettings("appsettings.json", """{ "Gateway": { "Port": 9000 } }""");
        AppSettings("appsettings.Staging.json", """{ "Gateway": { "Tracing": false } }""");
        var cue = Export(typeof(GatewayOptions));

        var module = CueModule(cue);
        var (exit, output) = Cue(module, "vet", "-c", "./out");

        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Config_file_schema_compiles_and_checks_files_like_the_app_does()
    {
        var cue = Export(typeof(GatewayOptions));
        var module = CueModule(cue);
        Assert.Equal(0, Cue(module, "export", "./out", "-e", "files.routes.schema", "--out", "json", "-o", "routes.schema.json").Exit);
        var (importExit, importOutput) = Cue(module, "import", "-f", "-p", "check", "-l", "#Routes:", "jsonschema:", "routes.schema.json", "-o", "routes.cue");
        Assert.True(importExit == 0, importOutput);

        File.WriteAllText(Path.Join(module, "good.json"), """{ "items": [ { "match": "/a", "upstream": "http://a.svc" } ] }""");
        File.WriteAllText(Path.Join(module, "extra.json"), """{ "items": [ { "match": "/a", "upstream": "http://a.svc", "weight": 3 } ] }""");
        File.WriteAllText(Path.Join(module, "nomatch.json"), """{ "items": [ { "upstream": "http://a.svc" } ] }""");

        Assert.Equal(0, Cue(module, "vet", "-d", "#Routes", "routes.cue", "good.json").Exit);
        Assert.NotEqual(0, Cue(module, "vet", "-d", "#Routes", "routes.cue", "extra.json").Exit);
        Assert.NotEqual(0, Cue(module, "vet", "-d", "#Routes", "routes.cue", "nomatch.json").Exit);
    }

    [Fact]
    public void Overlays_are_exported_and_pass_the_meta_schema()
    {
        var cue = Export(typeof(CatalogOptions));

        Assert.Contains("""
            	overlays: {
            		platform: {
            			description: "Platform overrides, layered over the appsettings files"
            			format: "json"
            			path: "/app/config/appsettings.Production.json"
            			keySeparator: ":"
            			reload: "watch"
            		}
            	}
            """.Replace("\r\n", "\n", StringComparison.Ordinal), cue, StringComparison.Ordinal);
        var (exit, output) = Cue(CueModule(cue), "vet", "-c", "./out");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Bad_overlay_declarations_are_all_reported()
    {
        var ex = Assert.Throws<ContractException>(() => Export(typeof(BrokenOverlayOptions)));

        Assert.Contains(ex.Errors, e => e.Contains("[ConfigOverlay(\"Platform\")]: the name must be a DNS label", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("path 'app/config/settings.json' must be an absolute", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("path '/app/yaml/settings.yaml' must be a .json file", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Overlays one, two share the directory /app/shared", StringComparison.Ordinal));
    }

    // End to end: the platform renders the overlay from the exported contract with the CUE meta-schema, and the
    // app binds the rendered file through AddDocuconfOverlays.
    [Fact]
    public void An_overlay_rendered_by_the_platform_binds_in_the_app()
    {
        var module = CueModule(Export(typeof(CatalogOptions)));
        Directory.CreateDirectory(Path.Join(module, "platform"));
        File.WriteAllText(Path.Join(module, "platform", "render.cue"), """
            package platform

            import (
            	"docuconf.dev/contract"
            	app "docuconf.dev/out:catalog"
            )

            rendered: contract.#Render & {
            	contract: app
            	overlays: platform: {
            		CATALOG__PAGESIZE:   50
            		CATALOG__CACHETTL:   "1m30s"
            		CATALOG__SEARCHURL:  "https://search.internal"
            		CATALOG__FEATUREDCATEGORIES: ["books", "games"]
            	}
            }
            file: rendered.configMaps[0].data["appsettings.Production.json"]
            """);
        var (exit, output) = Cue(module, "export", "./platform", "-e", "file", "--out", "text");
        Assert.True(exit == 0, output);

        var overlay = Path.Join(_root, "app", "config", "appsettings.Production.json");
        Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
        File.WriteAllText(overlay, output);
        var config = new ConfigurationBuilder()
            .AddDocuconfOverlays<CatalogOptions>(s => s.FileRoot = _root)
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(config);
        services.AddDocuconf<CatalogOptions>(s => s.FileRoot = _root);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<CatalogOptions>>().Value;

        Assert.Equal(50, options.PageSize);
        Assert.Equal(TimeSpan.FromSeconds(90), options.CacheTtl);
        Assert.Equal("https://search.internal", options.SearchUrl);
        Assert.Equal(["books", "games"], options.FeaturedCategories);
    }

    [Fact]
    public void Appsettings_values_become_defaults_and_profiles()
    {
        AppSettings("appsettings.json", """{ "Gateway": { "Port": 9000, "Brokers": [ "kafka-0:9092" ] } }""");
        AppSettings("appsettings.Production.json", """{ "Gateway": { "Timeout": "00:01:30" } }""");

        var model = ContractReader.Read([typeof(GatewayOptions)], new ContractReadSettings { ContentRoot = _root });

        Assert.Equal(9000L, model.Vars["GATEWAY__PORT"].Default);
        var brokers = model.Vars["GATEWAY__BROKERS"];
        Assert.False(brokers.Required); // supplied by the base file
        Assert.Equal("1m30s", model.Profiles!.Defaults["Production"]["GATEWAY__TIMEOUT"]);
        Assert.Equal("DOTNET_ENVIRONMENT", model.Profiles.Selector);
        Assert.True(model.Vars.ContainsKey("DOTNET_ENVIRONMENT"));
    }

    [Fact]
    public void Secrets_in_appsettings_are_rejected()
    {
        AppSettings("appsettings.Production.json", """{ "Gateway": { "DatabaseUrl": "postgres://app:pw@db/gw" } }""");

        var ex = Assert.Throws<ContractException>(() => Export(typeof(GatewayOptions)));

        Assert.Contains(ex.Errors, e => e.Contains("Gateway:DatabaseUrl", StringComparison.Ordinal) && e.Contains("[Secret]", StringComparison.Ordinal));
    }

    [Fact]
    public void Appsettings_values_must_satisfy_constraints()
    {
        AppSettings("appsettings.Staging.json", """{ "Gateway": { "Port": 70000 } }""");
        var ex = Assert.Throws<ContractException>(() => Export(typeof(GatewayOptions)));
        Assert.Contains(ex.Errors, e => e.Contains("appsettings.Staging.json: Gateway:Port = 70000 is above the maximum 65535", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_declarations_are_all_reported()
    {
        var ex = Assert.Throws<ContractException>(() => Export(typeof(BrokenOptions)));

        Assert.Contains(ex.Errors, e => e.Contains("Broken:NoDescription: add [Description", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Broken:Token: a [Secret] value cannot have a default", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Broken:Workers: the default 0 is below the minimum 1", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Broken:Code: pattern", StringComparison.Ordinal) && e.Contains("RE2", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Broken:Settings: Reload.Watch needs a ConfigFile<Routes> property", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Broken:Pair: a [TlsFile] property must be of type TlsKeyPair", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Broken:Scalar: a [JsonVar] property must be a class", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.Contains("Broken:Limit: the default {\"rps\":0,\"burst\":0,\"key\":\"ip\"} fails its schema at $.rps", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("00:00:30", "30s")]
    [InlineData("00:01:30", "1m30s")]
    [InlineData("1.02:03:04.5", "26h3m4s500ms")]
    [InlineData("00:00:00", "0s")]
    public void Durations_round_trip_through_go_syntax(string timeSpan, string go)
    {
        var ts = TimeSpan.Parse(timeSpan, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(go, GoDuration.Format(ts));
        Assert.Equal(ts, GoDuration.Parse(go));
    }

    /// <summary>
    /// The meta-schema module: <c>DOCUCONF_SPEC_CUE</c> (docuconf-go's <c>spec/cue</c>) when set, else the copy in
    /// <c>spec/</c>.
    /// </summary>
    internal static string CueModule(string contract)
    {
        var spec = Environment.GetEnvironmentVariable("DOCUCONF_SPEC_CUE") is { Length: > 0 } configured
            ? configured
            : Path.Join(AppContext.BaseDirectory, "spec");
        var module = Directory.CreateTempSubdirectory("docuconf-cue-").FullName;
        CopyDirectory(Path.Join(spec, "cue.mod"), Path.Join(module, "cue.mod"));
        CopyDirectory(Path.Join(spec, "contract"), Path.Join(module, "contract"));
        Directory.CreateDirectory(Path.Join(module, "out"));
        File.WriteAllText(Path.Join(module, "out", "contract.cue"), contract);
        return module;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Join(to, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(from))
        {
            CopyDirectory(dir, Path.Join(to, Path.GetFileName(dir)));
        }
    }

    internal static (int Exit, string Output) Cue(string workingDirectory, params string[] args)
    {
        var cue = FindCue();
        if (cue is null)
        {
            // DOCUCONF_REQUIRE_VET=1 (scripts/conformance.sh) makes a missing cue fail instead of skip.
            const string message = "The cue CLI is not installed; set DOCUCONF_CUE or put cue on PATH.";
            Assert.False(Environment.GetEnvironmentVariable("DOCUCONF_REQUIRE_VET") == "1", message);
            Assert.Skip(message);
        }

        var start = new ProcessStartInfo(cue) { WorkingDirectory = workingDirectory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static string? FindCue()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DOCUCONF_CUE"),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "go", "bin", "cue"),
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Join(p, "cue")));
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }
}

[ConfigContract("broken", Section = "Broken")]
public sealed class BrokenOptions
{
    public string NoDescription { get; set; } = "";

    [Secret]
    [Description("API token")]
    public string Token { get; set; } = "dev-token";

    [Range(1, 64)]
    [Description("Worker count")]
    public int Workers { get; set; }

    [RegularExpression("^(?=.*[A-Z]).+$")]
    [Description("Upper-case code")]
    public string Code { get; set; } = "";

    [ConfigFile("/etc/broken/settings.json", Reload = Reload.Watch)]
    [Description("Settings file")]
    public Routes Settings { get; set; } = new();

    [TlsFile("/etc/broken/tls")]
    [Description("A key pair of the wrong type")]
    public string Pair { get; set; } = "";

    [JsonVar]
    [Description("A scalar marked as JSON")]
    public int Scalar { get; set; }

    [JsonVar]
    [Description("A rate limit whose default is invalid")]
    public RateLimit Limit { get; set; } = new() { Rps = 0 };
}

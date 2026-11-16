using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

public enum RelayLogFormat
{
    Text,
    Json,
}

// Conditional requirements (SPEC §4.2.1) and a fragment (SPEC §4.8), declared.
[ConfigContract("relay", Section = "Relay")]
[Includes("otel@1")]
public sealed class RelayOptions
{
    [EnvName("TLS_ENABLED"), Description("Serve HTTPS instead of HTTP")]
    public bool TlsEnabled { get; set; }

    [EnvName("LOG_FORMAT"), Description("Log output format")]
    public RelayLogFormat LogFormat { get; set; } = RelayLogFormat.Text;

    [EnvName("CA_SUBJECT"), RequiredIf("TLS_ENABLED", true), Description("Subject of the CA that signs client certificates")]
    public string? CaSubject { get; set; }

    [EnvName("LOG_SCHEMA_URL"), Url, RequiredIf("LOG_FORMAT", RelayLogFormat.Json), Description("JSON schema the structured logs follow")]
    public string? LogSchemaUrl { get; set; }

    [TextFile("/etc/relay/allow.txt"), RequiredIf("TLS_ENABLED", true), Description("Client names allowed over TLS")]
    public string? Allowlist { get; set; }
}

// Roles (SPEC §4.9), declared.
[ConfigContract("shop", Section = "Shop")]
[Roles("web", "worker")]
public sealed class RoleShopOptions
{
    [EnvName("DATABASE_URL"), Required, Description("Database connection string")]
    public string? DatabaseUrl { get; set; }

    [EnvName("ORIGINS"), Required, Roles("web"), Description("Origins the web server accepts")]
    public string[]? Origins { get; set; }

    [EnvName("QUEUES"), Required, Roles("worker"), Description("Queues the worker takes jobs from")]
    public string[]? Queues { get; set; }
}

[ConfigContract("bad-conditions", Section = "Bad")]
[Includes("otel@1", "nope@1")]
public sealed class BadConditionOptions
{
    [Description("Has an initializer"), RequiredIf("BAD__FLAG", true)]
    public int Workers { get; set; } = 4;

    [Description("Is required already"), Required, RequiredIf("BAD__FLAG", true)]
    public string? Both { get; set; }

    [Description("Names no variable"), RequiredIf("MISSING", "x")]
    public string? Orphan { get; set; }

    [Description("Compares a bool with a string"), RequiredIf("BAD__FLAG", "yes")]
    public string? Mistyped { get; set; }

    [Description("A flag")]
    public bool Flag { get; set; }

    [Description("A role, but no roles"), Roles("web")]
    public string? WebOnly { get; set; }
}

[ConfigContract("bad-roles", Section = "Bad")]
[Roles("web")]
public sealed class BadRoleOptions
{
    [EnvName("DOCUCONF_ROLE"), Description("Declared by the app")]
    public string? Role { get; set; }
}

public sealed class DeclaredConditionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("docuconf-declared-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Json(Type type) =>
        CueWriter.WriteJson(ContractReader.Read([type]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));

    private T Resolve<T>(Dictionary<string, string?> config)
        where T : class
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddDocuconf<T>(s =>
        {
            s.FileRoot = _root;
            s.TerminationLogPath = Path.Join(_root, "no-termination-log");
        });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<T>>().Value;
    }

    private string[] Failures<T>(Dictionary<string, string?> config)
        where T : class =>
        Assert.Throws<OptionsValidationException>(() => Resolve<T>(config)).Failures
            .Select(f => f.Replace(_root, "", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Exports_conditions_roles_and_fragments()
    {
        var relay = JsonNode.Parse(Json(typeof(RelayOptions)))!;
        Assert.Equal("""["otel@1"]""", relay["includes"]!.ToJsonString());
        Assert.Equal("""{"TLS_ENABLED":true}""", relay["vars"]!["CA_SUBJECT"]!["requiredIf"]!.ToJsonString());
        Assert.Equal("""{"LOG_FORMAT":"Json"}""", relay["vars"]!["LOG_SCHEMA_URL"]!["requiredIf"]!.ToJsonString());
        Assert.Equal("""{"TLS_ENABLED":true}""", relay["files"]!["allowlist"]!["requiredIf"]!.ToJsonString());
        Assert.Equal("url", relay["vars"]!["OTEL_EXPORTER_OTLP_ENDPOINT"]!["type"]!.GetValue<string>());
        Assert.True(relay["vars"]!["OTEL_EXPORTER_OTLP_HEADERS"]!["secret"]!.GetValue<bool>());

        var shop = JsonNode.Parse(Json(typeof(RoleShopOptions)))!;
        Assert.Equal("""["web","worker"]""", shop["roles"]!.ToJsonString());
        Assert.Equal("""["web"]""", shop["vars"]!["ORIGINS"]!["roles"]!.ToJsonString());
        var role = shop["vars"]![DocuconfContract.RoleVar]!;
        Assert.Equal("enum", role["type"]!.GetValue<string>());
        Assert.True(role["required"]!.GetValue<bool>());
        Assert.Equal("""["web","worker"]""", role["values"]!.ToJsonString());

        // The contract-first mode reads what the declaration exports.
        Assert.Equal(["web", "worker"], DocuconfContract.FromJson(Json(typeof(RoleShopOptions))).Model.Roles);
        Assert.Equal(["otel@1"], DocuconfContract.FromJson(Json(typeof(RelayOptions))).Model.Includes);
    }

    [Theory]
    [InlineData(typeof(RelayOptions))]
    [InlineData(typeof(RoleShopOptions))]
    public void Exported_contracts_pass_the_meta_schema(Type type)
    {
        var cue = CueWriter.Write(ContractReader.Read([type]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));
        var (exit, output) = ExportTests.Cue(ExportTests.CueModule(cue), "vet", "-c", "./out");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Reports_bad_conditions_roles_and_fragments()
    {
        var errors = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(BadConditionOptions)])).Errors;
        Assert.Contains("Bad:Workers: a [RequiredIf] input has no default, since it is optional while its condition does not hold. Remove the initializer, and make it Int32?.", errors);
        Assert.Contains("Bad:Both: [RequiredIf] makes it required only while BAD__FLAG has a value; remove [Required].", errors);
        Assert.Contains("vars.BAD__ORPHAN: requiredIf must name another declared variable, not 'MISSING'.", errors);
        Assert.Contains("vars.BAD__MISTYPED: requiredIf: BAD__FLAG is \"yes\" is not a value BAD__FLAG can have.", errors);
        Assert.Contains("vars.BAD__WEBONLY: has roles, but the contract declares none.", errors);
        Assert.Contains(errors, e => e.StartsWith("[Includes]: 'nope@1' is not a well-known fragment; the fragments are ", StringComparison.Ordinal) && e.Contains("otel@1", StringComparison.Ordinal));

        var roles = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(BadRoleOptions)])).Errors;
        Assert.Contains("Bad:Role: docuconf declares DOCUCONF_ROLE for the roles itself; remove the property.", roles);
    }

    [Fact]
    public void A_condition_that_holds_requires_its_inputs()
    {
        var options = Resolve<RelayOptions>([]);
        Assert.Null(options.CaSubject);
        Assert.Null(options.Allowlist);

        Assert.Equal(
            [
                "[file_missing] allowlist: /etc/relay/allow.txt does not exist, and is required when TLS_ENABLED is true",
                "[missing_required] CA_SUBJECT: is required when TLS_ENABLED is true (Relay:CaSubject)",
                "[missing_required] LOG_SCHEMA_URL: is required when LOG_FORMAT is \"Json\" (Relay:LogSchemaUrl)",
            ],
            Failures<RelayOptions>(new() { ["TLS_ENABLED"] = "true", ["LOG_FORMAT"] = "Json" }));

        Directory.CreateDirectory(Path.Join(_root, "etc", "relay"));
        File.WriteAllText(Path.Join(_root, "etc", "relay", "allow.txt"), "billing\n");
        options = Resolve<RelayOptions>(new() { ["TLS_ENABLED"] = "true", ["CA_SUBJECT"] = "CN=Client CA" });
        Assert.Equal("billing\n", options.Allowlist);
    }

    [Fact]
    public void Fragment_variables_are_checked_at_boot()
    {
        Assert.Equal(
            ["[invalid_type] OTEL_SDK_DISABLED"],
            Failures<RelayOptions>(new() { ["OTEL_SDK_DISABLED"] = "maybe" }).Select(f => f[..f.IndexOf(':', StringComparison.Ordinal)]));
        Assert.Equal(
            ["[not_in_enum] OTEL_EXPORTER_OTLP_PROTOCOL"],
            Failures<RelayOptions>(new() { ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "carrier-pigeon" }).Select(f => f[..f.IndexOf(':', StringComparison.Ordinal)]));
        _ = Resolve<RelayOptions>(new() { ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc", ["OTEL_SERVICE_NAME"] = "relay" });
    }

    [Fact]
    public void A_role_requires_only_its_own_inputs()
    {
        Assert.Equal(["[missing_required] ORIGINS: is required (Shop:Origins)"],
            Failures<RoleShopOptions>(new() { ["DOCUCONF_ROLE"] = "web", ["DATABASE_URL"] = "db" }));
        var worker = Resolve<RoleShopOptions>(new() { ["DOCUCONF_ROLE"] = "worker", ["DATABASE_URL"] = "db", ["QUEUES:0"] = "mail" });
        Assert.Equal(["mail"], worker.Queues!);
        Assert.Null(worker.Origins);

        // With no role, or an unknown one, the role is the one problem reported.
        Assert.Equal(["[missing_required] DOCUCONF_ROLE: is required"], Failures<RoleShopOptions>(new() { ["DATABASE_URL"] = "db" }));
        Assert.Equal(["[not_in_enum] DOCUCONF_ROLE"],
            Failures<RoleShopOptions>(new() { ["DOCUCONF_ROLE"] = "cron", ["DATABASE_URL"] = "db" }).Select(f => f[..f.IndexOf(':', StringComparison.Ordinal)]));
    }

    /// <summary>
    /// The embedded fragments are docuconf-go's spec/fragments.json, next to <c>DOCUCONF_SPEC_CUE</c>. Refresh them
    /// with <c>scripts/sync-spec.sh</c>.
    /// </summary>
    [Fact]
    public void The_embedded_fragments_match_the_spec()
    {
        var spec = Environment.GetEnvironmentVariable("DOCUCONF_SPEC_CUE");
        if (string.IsNullOrEmpty(spec))
        {
            const string message = "DOCUCONF_SPEC_CUE is not set, so the fragments are not compared with the spec.";
            Assert.False(Environment.GetEnvironmentVariable("DOCUCONF_REQUIRE_VET") == "1", message);
            Assert.Skip(message);
            return;
        }

        var expected = JsonNode.Parse(File.ReadAllText(Path.Join(spec, "..", "fragments.json")));
        using var stream = typeof(DocuconfContract).Assembly.GetManifestResourceStream("Docuconf.fragments.json")!;
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(stream)), "src/Docuconf/Contract/fragments.json differs from the spec's; run scripts/sync-spec.sh.");
    }
}

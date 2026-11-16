using Docuconf.Contract;

namespace Docuconf.Tests;

/// <summary>Conditional requirements (SPEC §4.2.1), roles (SPEC §4.9) and fragments (SPEC §4.8) in the contract-first mode.</summary>
public sealed class ConditionalTests
{
    private const string Relay = """
        {
          "apiVersion": "docuconf.dev/v1alpha1",
          "kind": "ConfigContract",
          "metadata": {"name": "relay"},
          "includes": ["otel@1"],
          "vars": {
            "TLS_ENABLED": {"type": "bool", "description": "Serve HTTPS", "default": false},
            "LOG_FORMAT": {"type": "enum", "description": "Log output format", "values": ["text", "json"], "default": "text"},
            "LOG_SCHEMA_URL": {"type": "url", "description": "Schema of the logs", "requiredIf": {"LOG_FORMAT": "json"}},
            "CA_SUBJECT": {"type": "string", "description": "Client CA subject", "requiredIf": {"TLS_ENABLED": true}},
            "OTEL_SERVICE_NAME": {"type": "string", "description": "Service name reported in telemetry"}
          },
          "files": {
            "allowlist": {"type": "text", "description": "Allowed clients", "path": "/etc/relay/allow.txt", "requiredIf": {"TLS_ENABLED": true}}
          }
        }
        """;

    private const string Shop = """
        {
          "apiVersion": "docuconf.dev/v1alpha1",
          "kind": "ConfigContract",
          "metadata": {"name": "shop"},
          "roles": ["web", "worker"],
          "vars": {
            "DOCUCONF_ROLE": {"type": "enum", "description": "The role this workload runs", "required": true, "values": ["web", "worker"]},
            "ORIGINS": {"type": "list", "items": "string", "description": "Allowed origins", "required": true, "roles": ["web"]},
            "QUEUES": {"type": "list", "items": "string", "description": "Job queues", "required": true, "roles": ["worker"]},
            "BATCH": {"type": "int", "description": "Jobs per batch", "roles": ["worker"], "requiredIf": {"DOCUCONF_ROLE": "worker"}}
          }
        }
        """;

    private static List<string> Problems(string contract, Dictionary<string, string> env)
    {
        var root = Directory.CreateTempSubdirectory("docuconf-conditional-").FullName;
        try
        {
            return DocuconfContract.FromJson(contract).Validate(env, new DocuconfSettings { FileRoot = root })
                .Violations.Select(v => v.ToString().Replace(root, "", StringComparison.Ordinal)).ToList();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reads_the_conditions_roles_and_includes()
    {
        var relay = DocuconfContract.FromJson(Relay).Model;
        Assert.Equal(["otel@1"], relay.Includes);
        Assert.Equal(new RequiredIfSpec("TLS_ENABLED", true), relay.Vars["CA_SUBJECT"].RequiredIf);
        Assert.Equal("LOG_FORMAT is \"json\"", relay.Vars["LOG_SCHEMA_URL"].RequiredIf!.ToString());
        Assert.Equal("TLS_ENABLED", relay.Files["allowlist"].RequiredIf!.Var);

        var shop = DocuconfContract.FromJson(Shop).Model;
        Assert.Equal(["web", "worker"], shop.Roles);
        Assert.Equal(["web"], shop.Vars["ORIGINS"].Roles);
    }

    [Fact]
    public void A_condition_that_holds_makes_its_inputs_required()
    {
        Assert.Empty(Problems(Relay, []));
        Assert.Equal(
            [
                "[file_missing] allowlist: /etc/relay/allow.txt does not exist, and is required when TLS_ENABLED is true",
                "[missing_required] CA_SUBJECT: is required when TLS_ENABLED is true",
                "[missing_required] LOG_SCHEMA_URL: is required when LOG_FORMAT is \"json\"",
            ],
            Problems(Relay, new() { ["TLS_ENABLED"] = "true", ["LOG_FORMAT"] = "json" }).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_invalid_condition_variable_makes_nothing_required()
    {
        Assert.Equal(["TLS_ENABLED:invalid_type"], DocuconfContract.FromJson(Relay).Validate(new Dictionary<string, string> { ["TLS_ENABLED"] = "yes" })
            .Violations.Select(v => $"{v.Input}:{v.Code}"));
    }

    [Fact]
    public void A_role_requires_only_its_own_inputs()
    {
        Assert.Equal(["ORIGINS:missing_required"], Codes(new() { ["DOCUCONF_ROLE"] = "web" }));
        Assert.Equal(["BATCH:missing_required", "QUEUES:missing_required"], Codes(new() { ["DOCUCONF_ROLE"] = "worker" }));
        Assert.Empty(Codes(new() { ["DOCUCONF_ROLE"] = "worker", ["QUEUES"] = "mail", ["BATCH"] = "10" }));

        // With no role, or an unknown one, the role is the one problem reported.
        Assert.Equal(["DOCUCONF_ROLE:missing_required"], Codes([]));
        Assert.Equal(["DOCUCONF_ROLE:not_in_enum"], Codes(new() { ["DOCUCONF_ROLE"] = "cron" }));

        static List<string> Codes(Dictionary<string, string> env) => DocuconfContract.FromJson(Shop).Validate(env)
            .Violations.Select(v => $"{v.Input}:{v.Code}").Order(StringComparer.Ordinal).ToList();
    }

    [Theory]
    [InlineData("""{"A": {"type": "string", "description": "An input", "requiredIf": {"A": "x"}}}""", "vars.A: requiredIf must name another declared variable, not 'A'.")]
    [InlineData("""{"A": {"type": "string", "description": "An input", "requiredIf": {"B": "x"}}}""", "vars.A: requiredIf must name another declared variable, not 'B'.")]
    [InlineData("""{"A": {"type": "string", "description": "An input", "required": true, "requiredIf": {"B": true}}, "B": {"type": "bool", "description": "A flag"}}""", "vars.A: an input with requiredIf is neither required nor has a default.")]
    [InlineData("""{"A": {"type": "string", "description": "An input", "requiredIf": {"B": 1}}, "B": {"type": "bool", "description": "A flag"}}""", "vars.A: requiredIf: B is 1 is not a value B can have.")]
    [InlineData("""{"A": {"type": "string", "description": "An input", "requiredIf": {"B": "x"}}, "B": {"type": "string", "description": "A secret", "secret": true}}""", "vars.A: requiredIf names B, which is not a plain string, int, bool or enum variable.")]
    [InlineData("""{"A": {"type": "string", "description": "An input", "requiredIf": {"B": "x", "C": "y"}}}""", "vars.A: requiredIf must name one variable and its value: {VAR: value}.")]
    [InlineData("""{"A": {"type": "string", "description": "An input", "roles": ["web"]}}""", "vars.A: has roles, but the contract declares none.")]
    public void Rejects_a_bad_condition_or_role(string vars, string error)
    {
        var ex = Assert.Throws<ContractException>(() => DocuconfContract.FromJson(
            $$"""{"apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract", "vars": {{vars}}}"""));
        Assert.Contains(error, ex.Errors);
    }

    [Fact]
    public void Roles_need_the_role_variable()
    {
        var ex = Assert.Throws<ContractException>(() => DocuconfContract.FromJson("""
            {"apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract", "roles": ["web", "web"],
             "vars": {"A": {"type": "string", "description": "An input", "roles": ["cron"]}}}
            """));
        Assert.Contains("roles: role web is listed twice.", ex.Errors);
        Assert.Contains("vars.A: role cron is not one of the contract's roles, web, web.", ex.Errors);
        Assert.Contains(ex.Errors, e => e.StartsWith("vars.DOCUCONF_ROLE: a contract with roles declares it", StringComparison.Ordinal));
    }
}

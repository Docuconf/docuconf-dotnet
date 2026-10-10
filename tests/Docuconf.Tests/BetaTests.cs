using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docuconf.Contract;

namespace Docuconf.Tests;

[ConfigContract("hooks", Section = "Hooks")]
public sealed class HookOptions
{
    [KeySet(KeyMinLength = 8, KeyMaxLength = 64), EnvName("WEBHOOK_KEYS")]
    [Description("Keys that verify webhook signatures")]
    public KeySet? WebhookKeys { get; set; }

    [KeySet(MinKeys = 1, MaxKeys = 3), Csv(";")]
    [Description("API keys callers present")]
    public KeySet? ApiKeys { get; set; }

    [Range(1, 65535)]
    [Description("Port to listen on")]
    public int Port { get; set; } = 8080;

    [Deprecated("Use Hooks:Port instead", ReplacedBy = "HOOKS__PORT")]
    [Description("Old name of the listen port")]
    public int? ListenPort { get; set; }

    [Description("Whether to log each webhook")]
    public bool Verbose { get; set; }

    [Description("How long to wait for the sender")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    [Description("Fraction of webhooks sampled")]
    public double Sample { get; set; } = 0.5;
}

[ConfigContract("bad-hooks", Section = "Bad")]
public sealed class BadHookOptions
{
    [Required, Deprecated("Use Other instead")]
    [Description("A required input that is deprecated")]
    public string Required { get; set; } = "";

    [Deprecated("  ")]
    [Description("A deprecation without a message")]
    public string? Blank { get; set; }

    [Deprecated("x", ReplacedBy = "NOWHERE")]
    [Description("A replacement that is not declared")]
    public string? Gone { get; set; }

    [KeySet(MinKeys = 3, MaxKeys = 2)]
    [Description("Bounds the wrong way round")]
    public KeySet? Backwards { get; set; }

    [MaxLength(2)]
    [Description("A list bound on a key set")]
    public KeySet? Misfit { get; set; }

    [Description("A key set with a default")]
    public KeySet? Defaulted { get; set; } = new(["not-a-default"]);
}

public sealed class KeySetTypeTests
{
    private const string OldKey = "old-key-0123";
    private const string NewKey = "new-key-0123";

    private static Dictionary<string, string> Env(params (string Key, string Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    [Fact]
    public void A_key_set_binds_its_keys_in_order()
    {
        var options = DocuconfTesting.Load<HookOptions>(Env(("WEBHOOK_KEYS", $"{OldKey},{NewKey}"), ("HOOKS__APIKEYS", "a;b;c")));

        Assert.Equal([OldKey, NewKey], options.WebhookKeys!.Keys);
        Assert.Equal(["a", "b", "c"], options.ApiKeys!.Keys);
        Assert.True(options.WebhookKeys.Contains(NewKey));
        Assert.False(options.WebhookKeys.Contains("new-key-012"));
        Assert.False(options.WebhookKeys.Contains(null));
    }

    [Fact]
    public void Verify_tries_every_key_with_the_callers_check()
    {
        var body = Encoding.UTF8.GetBytes("{}");
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(NewKey), body);
        var keys = new KeySet([OldKey, NewKey]);
        int calls = 0;

        Assert.True(keys.Verify(key => { calls++; return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), signature); }));
        Assert.Equal(2, calls);
        Assert.False(new KeySet([OldKey]).Verify(key => CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), signature)));
    }

    [Fact]
    public void A_key_set_never_prints_a_key()
    {
        var keys = new KeySet([OldKey, NewKey]);

        Assert.Equal("***", keys.ToString());
        Assert.Equal("\"***\"", JsonSerializer.Serialize(keys));
        Assert.DoesNotContain(OldKey, JsonSerializer.Serialize(new { Keys = keys }), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OldKey + ",", "out_of_range")]                       // an empty second key, from a stray comma
    [InlineData(OldKey + ",short", "out_of_range")]                  // below keyMinLength
    [InlineData(OldKey + "," + NewKey + ",third-key-0123", "too_many_items")]
    public void A_bad_key_set_fails_without_printing_a_key(string value, string code)
    {
        var problem = Assert.Single(DocuconfTesting.Validate<HookOptions>(Env(("WEBHOOK_KEYS", value))));

        Assert.Equal(("WEBHOOK_KEYS", code), (problem.Input, problem.Code));
        Assert.DoesNotContain(OldKey, problem.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("short", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_key_is_out_of_range_without_a_minimum_length()
    {
        var problem = Assert.Single(DocuconfTesting.Validate<HookOptions>(Env(("HOOKS__APIKEYS", "a;;b"))));
        Assert.Equal(("HOOKS__APIKEYS", "out_of_range"), (problem.Input, problem.Code));
    }

    // One wording for an empty key in both modes (SPEC §4.3): its 1-based position, never a key.
    [Theory]
    [InlineData("old-key-0123;", "key 2 is empty")]
    [InlineData(";new-key-0123", "key 1 is empty")]
    [InlineData("a-key-0123;;b-key-0123", "key 2 is empty")]
    public void An_empty_key_is_named_by_its_position(string value, string message)
    {
        var declared = Assert.Single(DocuconfTesting.Validate<HookOptions>(Env(("HOOKS__APIKEYS", value))));
        Assert.Equal(("out_of_range", message), (declared.Code, declared.Message));

        var model = ContractReader.Read([typeof(HookOptions)]);
        var contract = DocuconfContract.FromJson(CueWriter.WriteJson(model, new CueWriter.Generator("Docuconf.Options", "0.1.0-test")));
        var loaded = Assert.Single(contract.Validate(Env(("HOOKS__APIKEYS", value))).Violations);
        Assert.Equal(("out_of_range", message), (loaded.Code, loaded.Message));
        Assert.DoesNotContain("key-0123", loaded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_set_is_exported_as_a_secret_keySet()
    {
        var cue = CueWriter.Write(ContractReader.Read([typeof(HookOptions)]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));

        Assert.Contains(
            "\t\tWEBHOOK_KEYS: {\n\t\t\ttype: \"keySet\"\n\t\t\tdescription: \"Keys that verify webhook signatures\"\n\t\t\tsecret: true\n"
            + "\t\t\tconfigKey: \"Hooks:WebhookKeys\"\n\t\t\tencoding: \"csv\"\n\t\t\tseparator: \",\"\n\t\t\tminKeys: 1\n\t\t\tmaxKeys: 2\n"
            + "\t\t\tkeyMinLength: 8\n\t\t\tkeyMaxLength: 64\n\t\t}\n",
            cue,
            StringComparison.Ordinal);
        Assert.Contains("\t\t\tseparator: \";\"\n\t\t\tminKeys: 1\n\t\t\tmaxKeys: 3\n", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_first_mode_returns_a_key_set()
    {
        var model = ContractReader.Read([typeof(HookOptions)]);
        var contract = DocuconfContract.FromJson(CueWriter.WriteJson(model, new CueWriter.Generator("Docuconf.Options", "0.1.0-test")));

        var values = contract.Load(Env(("WEBHOOK_KEYS", $"{OldKey},{NewKey}")));

        Assert.Equal([OldKey, NewKey], values.Get<KeySet>("WEBHOOK_KEYS")!.Keys);
        Assert.Contains("WEBHOOK_KEYS = ***", values.ToString(), StringComparison.Ordinal);
    }
}

public sealed class DeprecatedTests
{
    [Fact]
    public void A_deprecated_variable_is_exported_with_its_message()
    {
        var cue = CueWriter.Write(ContractReader.Read([typeof(HookOptions)]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));

        Assert.Contains("\t\t\tdeprecated: {\n\t\t\t\tmessage: \"Use Hooks:Port instead\"\n\t\t\t\treplacedBy: \"HOOKS__PORT\"\n\t\t\t}\n", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_declarations_are_reported()
    {
        var ex = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(BadHookOptions)]));

        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Required: a [Required] input cannot be [Deprecated]", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Blank: [Deprecated] needs a message that is not blank", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("BAD__GONE: [Deprecated] ReplacedBy 'NOWHERE'", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Backwards: [KeySet] needs MinKeys", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Misfit: [MaxLength] applies to strings, urls and lists; bound a key set", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Defaulted: a KeySet is secret and cannot have a default", StringComparison.Ordinal));
    }

    [Fact]
    public void A_message_above_500_characters_is_rejected()
    {
        Assert.Null(ContractReader.DeprecationProblem(new string('x', 500), null));
        Assert.Contains("above 500", ContractReader.DeprecationProblem(new string('x', 501), null), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_warns_about_a_deprecated_variable_that_is_set_without_printing_its_value()
    {
        using var files = new GatewayFiles();
        var result = files.Start<HookOptions>(new() { ["Hooks:ListenPort"] = "9191" });

        Assert.Null(result.ExitCode);
        Assert.Contains("docuconf: warning: HOOKS__LISTENPORT is deprecated but still set: Use Hooks:Port instead (replaced by HOOKS__PORT)", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("9191", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void No_warning_when_a_deprecated_variable_is_unset()
    {
        using var files = new GatewayFiles();
        var result = files.Start<HookOptions>([]);

        Assert.DoesNotContain("deprecated", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_analyzer_reports_a_bad_deprecation()
    {
        var diagnostics = await Compiler.Analyze("""
            [ConfigContract("svc", Section = "Svc")]
            public sealed class SvcOptions
            {
                [Required, Deprecated("Use Other instead")]
                [Description("A required input")]
                public string Name { get; set; } = "";

                [Deprecated("")]
                [Description("No message")]
                public string? Other { get; set; }
            }
            """);

        Assert.Contains(diagnostics, d => d.StartsWith("DOCUCONF011: Svc:Name: a [Required] input cannot be [Deprecated]", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.StartsWith("DOCUCONF011: Svc:Other: [Deprecated] needs a message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_analyzer_accepts_a_key_set()
    {
        var diagnostics = await Compiler.Analyze("""
            [ConfigContract("svc", Section = "Svc")]
            public sealed class SvcOptions
            {
                [KeySet(KeyMinLength = 32), Csv(";")]
                [Description("Keys that verify webhooks")]
                public KeySet? Keys { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }
}

/// <summary>SPEC §5: the exact forms each type accepts, whatever .NET's own parsers take.</summary>
public sealed class StrictParsingTests
{
    [Theory]
    [InlineData("HOOKS__VERBOSE", "1")]
    [InlineData("HOOKS__VERBOSE", "yes")]
    [InlineData("HOOKS__VERBOSE", " true")]
    [InlineData("HOOKS__PORT", "0x10")]
    [InlineData("HOOKS__PORT", "1_000")]
    [InlineData("HOOKS__PORT", "8080\n")]
    [InlineData("HOOKS__SAMPLE", ".5")]
    [InlineData("HOOKS__SAMPLE", "5.")]
    [InlineData("HOOKS__SAMPLE", "Infinity")]
    [InlineData("HOOKS__SAMPLE", "0,5")]
    [InlineData("HOOKS__TIMEOUT", "00:30")]
    [InlineData("HOOKS__TIMEOUT", "-00:00:05")]
    [InlineData("HOOKS__TIMEOUT", "24:00:00")]
    [InlineData("HOOKS__TIMEOUT", "00:00:30\n")]
    public void A_lenient_form_is_invalid_type(string name, string value)
    {
        var problem = Assert.Single(DocuconfTesting.Validate<HookOptions>(new Dictionary<string, string> { [name] = value }));
        Assert.Equal((name, "invalid_type"), (problem.Input, problem.Code));
    }

    [Theory]
    [InlineData("HOOKS__VERBOSE", "TRUE")]
    [InlineData("HOOKS__PORT", "+0080")]
    [InlineData("HOOKS__SAMPLE", "25e-2")]
    [InlineData("HOOKS__TIMEOUT", "1.00:00:00.5")]
    public void The_exact_forms_are_accepted(string name, string value) =>
        Assert.Empty(DocuconfTesting.Validate<HookOptions>(new Dictionary<string, string> { [name] = value }));
}

[ConfigContract("formats", Section = "Formats")]
public sealed class FormatOptions
{
    [ConfigFile("/etc/app/rules/rules.yaml")]
    [Description("Routing rules in YAML")]
    public Routes? Rules { get; set; }

    [ConfigFile("/etc/app/flags/flags.toml")]
    [Description("Routing rules in TOML")]
    public Routes? Flags { get; set; }

    [ConfigFile("/etc/app/live/routes.json", Reload = Reload.Watch)]
    [Description("Routing rules reloaded when they change")]
    public ConfigFile<Routes> Live { get; set; } = new();
}

public sealed class ConfigFormatTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("docuconf-formats-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string content)
    {
        var full = Path.Join(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private Dictionary<string, string> Env() => new() { ["DOCUCONF_FILE_ROOT"] = _root };

    [Fact]
    public void Yaml_and_toml_files_bind_like_json()
    {
        Write("/etc/app/rules/rules.yaml", "items:\n  - match: /billing\n    upstream: http://billing.svc\n");
        Write("/etc/app/flags/flags.toml", "[[items]]\nmatch = \"/orders\"\nupstream = \"http://orders.svc\"\n");

        var options = DocuconfTesting.Load<FormatOptions>(Env());

        Assert.Equal("/billing", Assert.Single(options.Rules!.Items).Match);
        Assert.Equal("/orders", Assert.Single(options.Flags!.Items).Match);
        Assert.False(options.Live.Present);
        Assert.Null(options.Live.Value);
    }

    [Fact]
    public void Formats_come_from_the_extension()
    {
        var model = ContractReader.Read([typeof(FormatOptions)]);

        Assert.Equal(("yaml", "toml", "json"), (model.Files["rules"].Format, model.Files["flags"].Format, model.Files["live"].Format));
        Assert.Equal(Reload.Watch, model.Files["live"].Reload);
    }

    [Theory]
    [InlineData("/etc/app/rules/rules.yaml", "items: [\n", "file_malformed")]
    [InlineData("/etc/app/flags/flags.toml", "items = nope\n", "file_malformed")]
    [InlineData("/etc/app/rules/rules.yaml", "items:\n  - match: /a\n    upstream: http://a\n    weight: 3\n", "schema_mismatch")]
    public void A_bad_file_is_reported_in_any_format(string path, string content, string code)
    {
        Write(path, content);

        var problem = Assert.Single(DocuconfTesting.Validate<FormatOptions>(Env()));

        Assert.Equal(code, problem.Code);
    }

    [Fact]
    public void A_watched_config_file_is_loaded_at_startup()
    {
        Write("/etc/app/live/routes.json", """{ "items": [ { "match": "/live", "upstream": "http://live.svc" } ] }""");

        var options = DocuconfTesting.Load<FormatOptions>(Env());

        Assert.True(options.Live.Present);
        Assert.Equal("/live", Assert.Single(options.Live.Value!.Items).Match);
    }
}

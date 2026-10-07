using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

public sealed class RunLimits
{
    public int? Max { get; set; }

    public string? Note { get; set; }
}

[ConfigContract("lengths", Section = "Lengths")]
public sealed class LengthOptions
{
    [UrlSchemes("https")]
    [MaxLength(24)]
    [Description("Where to report each run")]
    public string? Callback { get; set; }

    [JsonVar(MaxLength = 16)]
    [Description("Run limits as a JSON object")]
    public RunLimits? Limits { get; set; }

    [ItemLength(2, 4)]
    [Description("Branch codes, two to four characters each")]
    public string[] Branches { get; set; } = [];

    [Secret]
    [StringLength(30)]
    [Url]
    [Description("Database connection string")]
    public Uri? DbUrl { get; set; }
}

[ConfigContract("bad-lengths", Section = "Bad")]
public sealed class BadLengthOptions
{
    [ItemLength(4)]
    [Description("Integers have no item lengths")]
    public int[] Shards { get; set; } = [];

    [ItemLength(5, 4)]
    [Description("Lengths the wrong way round")]
    public string[] Backwards { get; set; } = [];

    [ItemLength(2)]
    [Description("A default item that is too long")]
    public string[] Codes { get; set; } = ["abc"];

    [UrlSchemes("https")]
    [MaxLength(10)]
    [Description("A default URL that is too long")]
    public string Home { get; set; } = "https://example.com";

    [UrlSchemes("https")]
    [MinLength(10)]
    [Description("A url takes no minimum length")]
    public string? Short { get; set; }

    [JsonVar(MaxLength = 5)]
    [Description("A default JSON value that is too long")]
    public RunLimits Limits { get; set; } = new() { Max = 1 };
}

public sealed class LengthTests
{
    private static string Export(Type type) =>
        CueWriter.Write(ContractReader.Read([type]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));

    private static LengthOptions Resolve(Dictionary<string, string?> config, string? json = null)
    {
        var builder = new ConfigurationBuilder().AddInMemoryCollection(config);
        if (json is not null)
        {
            builder.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(builder.Build());
        services.AddDocuconf<LengthOptions>(s => s.TerminationLogPath = "/nonexistent/termination-log");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<LengthOptions>>().Value;
    }

    private static string[] Failures(Dictionary<string, string?> config, string? json = null) =>
        Assert.Throws<OptionsValidationException>(() => Resolve(config, json)).Failures.Order().ToArray();

    [Fact]
    public void Lengths_are_exported()
    {
        var model = ContractReader.Read([typeof(LengthOptions)]);

        Assert.Equal(24, model.Vars["LENGTHS__CALLBACK"].MaxLength);
        Assert.Null(model.Vars["LENGTHS__CALLBACK"].MinLength);
        Assert.Equal(16, model.Vars["LENGTHS__LIMITS"].MaxLength);
        Assert.Equal((2, 4), (model.Vars["LENGTHS__BRANCHES"].ItemMinLength, model.Vars["LENGTHS__BRANCHES"].ItemMaxLength));
        Assert.Equal(30, model.Vars["LENGTHS__DBURL"].MaxLength);

        var cue = Export(typeof(LengthOptions));
        Assert.Contains("\t\t\titemMinLength: 2\n\t\t\titemMaxLength: 4\n", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void Exported_lengths_pass_the_meta_schema()
    {
        var (exit, output) = ExportTests.Cue(ExportTests.CueModule(Export(typeof(LengthOptions))), "vet", "-c", "./out");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Bad_lengths_are_reported_at_declaration()
    {
        var ex = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(BadLengthOptions)]));

        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Shards: [ItemLength] applies to lists of strings", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Backwards: [ItemLength(5, 4)] has its minimum above its maximum", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Codes: the default [\"abc\"] item 0 is 3 characters, above itemMaxLength 2", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Home: the default \"https://example.com\" is 19 characters, above maxLength 10", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Short: a url takes only a maximum length", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Limits: the default {\"max\":1} is 9 characters of JSON, above maxLength 5", StringComparison.Ordinal));
    }

    [Fact]
    public void Lengths_count_characters_not_bytes_or_utf16_units()
    {
        var options = Resolve(new()
        {
            ["Lengths:Callback"] = "https://例え.jp/日本語の道/一二三四",
            // 16 characters, though 26 bytes in UTF-8.
            ["Lengths:Limits"] = """{"note":"日本語の道"}""",
            ["Lengths:Branches:0"] = "ZÜ01",
            ["Lengths:Branches:1"] = "日本",
            // An emoji is 1 character but 2 UTF-16 units.
            ["Lengths:Branches:2"] = "😀😀😀😀",
        });

        Assert.Equal("https://例え.jp/日本語の道/一二三四", options.Callback);
        Assert.Equal("日本語の道", options.Limits!.Note);
        Assert.Equal(["ZÜ01", "日本", "😀😀😀😀"], options.Branches);
    }

    [Fact]
    public void Values_above_their_lengths_are_out_of_range()
    {
        var failures = Failures(new()
        {
            ["Lengths:Callback"] = "https://a.example/runs/42",
            ["Lengths:Branches:0"] = "BE",
            ["Lengths:Branches:1"] = "😀😀😀😀😀",
        });

        // The same words as the contract-first mode, not the DataAnnotations sentence.
        Assert.Equal(
            [
                "[out_of_range] LENGTHS__BRANCHES: item 1 is 5 characters, above itemMaxLength 4",
                "[out_of_range] LENGTHS__CALLBACK: 'https://a.example/runs/42' is 25 characters, above maxLength 24",
            ],
            failures);
        Assert.Equal(
            ["[out_of_range] LENGTHS__BRANCHES: item 1 is 1 characters, below itemMinLength 2"],
            Failures(new() { ["Lengths:Branches:0"] = "BE", ["Lengths:Branches:1"] = "B" }));
    }

    [Fact]
    public void A_json_value_is_measured_as_received()
    {
        Assert.Equal(12345678, Resolve(new() { ["Lengths:Limits"] = """{"max":12345678}""" }).Limits!.Max);
        Assert.Equal(
            ["[out_of_range] LENGTHS__LIMITS: is 17 characters of JSON, above maxLength 16"],
            Failures(new() { ["Lengths:Limits"] = """{"max":123456789}""" }));
        // Whitespace counts: the app receives it.
        Assert.Equal(
            ["[out_of_range] LENGTHS__LIMITS: is 17 characters of JSON, above maxLength 16"],
            Failures(new() { ["Lengths:Limits"] = """{ "max": 123456 }""" }));
    }

    [Fact]
    public void A_json_value_from_a_file_is_measured_as_compact_json()
    {
        // Pretty-printed in the file, but {"max":12345678} is 16 characters.
        Assert.Equal(12345678, Resolve([], """{ "Lengths": { "Limits": { "max": 12345678 } } }""").Limits!.Max);
        Assert.Equal(
            ["[out_of_range] LENGTHS__LIMITS: is 17 characters of JSON, above maxLength 16"],
            Failures([], """{ "Lengths": { "Limits": { "max": 123456789 } } }"""));
    }

    [Fact]
    public void A_too_long_secret_reports_its_length_not_its_value()
    {
        var failure = Assert.Single(Failures(new() { ["Lengths:DbUrl"] = "postgres://app:s3cr3t@db:5432/app" }));

        Assert.Equal("[out_of_range] LENGTHS__DBURL: is 33 characters, above maxLength 30 (value redacted)", failure);
    }

    [Fact]
    public void Contract_first_checks_lengths_and_rejects_item_lengths_on_int_lists()
    {
        var contract = """
            {"apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract", "metadata": {"name": "lengths"}, "vars": {
              "CALLBACK": {"type": "url", "description": "Where to report each run", "maxLength": 24},
              "LIMITS": {"type": "json", "description": "Run limits as JSON", "maxLength": 16},
              "BRANCHES": {"type": "list", "description": "Branch codes", "items": "string", "encoding": "csv", "itemMinLength": 2, "itemMaxLength": 4}}}
            """;
        var env = new Dictionary<string, string>
        {
            ["CALLBACK"] = "https://a.example/runs/4",
            ["LIMITS"] = """{"max":12345678}""",
            ["BRANCHES"] = "BE,ZÜ01,😀😀😀😀",
        };
        var config = DocuconfContract.FromJson(contract).Load(env);
        Assert.Equal(["BE", "ZÜ01", "😀😀😀😀"], config.Get<IReadOnlyList<string>>("BRANCHES"));

        env["LIMITS"] = """{ "max": 123456 }""";
        env["BRANCHES"] = "BE,B";
        var ex = Assert.Throws<ContractValidationException>(() => DocuconfContract.FromJson(contract).Load(env));
        Assert.Equal(["BRANCHES out_of_range", "LIMITS out_of_range"], ex.Violations.Select(v => $"{v.Input} {v.Code}").Order());

        var bad = contract.Replace("\"items\": \"string\"", "\"items\": \"int\"", StringComparison.Ordinal);
        var error = Assert.Throws<ContractException>(() => DocuconfContract.FromJson(bad));
        Assert.Contains("vars.BRANCHES: itemMinLength and itemMaxLength apply only to lists of strings.", error.Errors);
    }

    [Fact]
    public void Compact_json_does_not_escape_html_or_non_ascii()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{ "a": "<&>", "b": [1, 2.5, true, null], "c": "日本😀\n" }""");

        Assert.Equal("""{"a":"<&>","b":[1,2.5,true,null],"c":"日本😀\n"}""", CompactJson.Write(node));
    }
}

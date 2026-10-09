using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

// A dual-key secret (SPEC §6.1, docuconf-go conformance/load/key_set.yaml): one Secret key holds "old,new" while a
// key is rotated, so the list is csv rather than indexed.
[ConfigContract("keys", Section = "Keys")]
public sealed class KeySetOptions
{
    [Csv, EnvName("WEBHOOK_KEYS"), Secret, MinLength(1), MaxLength(2), ItemLength(32, 256)]
    [Description("Keys that verify webhook signatures")]
    public List<string>? WebhookKeys { get; set; }

    [Csv(";")]
    [Description("Ports to probe")]
    public int[] Probes { get; set; } = [80];
}

[ConfigContract("bad-csv", Section = "Bad")]
public sealed class BadCsvOptions
{
    [Csv]
    [Description("Not a list")]
    public string Name { get; set; } = "x";

    [Csv("")]
    [Description("No separator")]
    public List<string> Tags { get; set; } = [];
}

public sealed class CsvListTests
{
    private const string OldKey = "old-webhook-key-0123456789abcdef0123";
    private const string NewKey = "new-webhook-key-0123456789abcdef0123";

    private static KeySetOptions Resolve(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddDocuconf<KeySetOptions>(s => s.TerminationLogPath = "/nonexistent/termination-log");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeySetOptions>>().Value;
    }

    private static string[] Failures(Dictionary<string, string?> config) =>
        Assert.Throws<OptionsValidationException>(() => Resolve(config)).Failures.Order().ToArray();

    [Fact]
    public void A_csv_list_is_exported_with_its_encoding_and_separator()
    {
        var cue = CueWriter.Write(ContractReader.Read([typeof(KeySetOptions)]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));

        Assert.Contains(
            "\t\tWEBHOOK_KEYS: {\n\t\t\ttype: \"list\"\n\t\t\tdescription: \"Keys that verify webhook signatures\"\n\t\t\tsecret: true\n"
            + "\t\t\tconfigKey: \"Keys:WebhookKeys\"\n\t\t\titems: \"string\"\n\t\t\tencoding: \"csv\"\n\t\t\tseparator: \",\"\n"
            + "\t\t\tminItems: 1\n\t\t\tmaxItems: 2\n\t\t\titemMinLength: 32\n\t\t\titemMaxLength: 256\n\t\t}\n",
            cue,
            StringComparison.Ordinal);
        Assert.Contains("\t\t\tencoding: \"csv\"\n\t\t\tseparator: \";\"\n", cue, StringComparison.Ordinal);

        var (exit, output) = ExportTests.Cue(ExportTests.CueModule(cue), "vet", "-c", "./out");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void One_or_two_keys_bind_from_one_value()
    {
        Assert.Equal([OldKey], Resolve(new() { ["WEBHOOK_KEYS"] = OldKey }).WebhookKeys);
        Assert.Equal([OldKey, NewKey], Resolve(new() { ["WEBHOOK_KEYS"] = $"{OldKey},{NewKey}" }).WebhookKeys);
        Assert.Equal([80, 443], Resolve(new() { ["Keys:Probes"] = "80;443" }).Probes);
    }

    [Fact]
    public void An_empty_value_is_unset()
    {
        Assert.Null(Resolve(new() { ["WEBHOOK_KEYS"] = "" }).WebhookKeys);
    }

    [Theory]
    [InlineData(OldKey + ",", "[out_of_range] WEBHOOK_KEYS: item 1 is 0 characters, below itemMinLength 32 (value redacted)")]
    [InlineData(OldKey + ",new-webhook-key", "[out_of_range] WEBHOOK_KEYS: item 1 is 15 characters, below itemMinLength 32 (value redacted)")]
    [InlineData(OldKey + "," + NewKey + ",third-webhook-key-0123456789abcdef", "[too_many_items] WEBHOOK_KEYS: has 3 items, more than 2 (value redacted)")]
    public void A_bad_key_set_fails_without_printing_a_key(string value, string want)
    {
        var failure = Assert.Single(Failures(new() { ["WEBHOOK_KEYS"] = value }));

        Assert.Equal(want, failure);
        Assert.DoesNotContain("webhook-key", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_csv_int_item_that_is_not_an_integer_is_invalid()
    {
        Assert.Equal(["[invalid_type] KEYS__PROBES: item 1 is not an integer"], Failures(new() { ["Keys:Probes"] = "80;x" }));
    }

    [Fact]
    public void Bad_csv_declarations_are_reported()
    {
        var ex = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(BadCsvOptions)]));

        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Name: [Csv] applies to lists", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Tags: [Csv] needs a separator", StringComparison.Ordinal));
    }
}

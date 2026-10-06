using Docuconf.Contract;

namespace Docuconf.Tests;

public sealed class ContractFirstTests
{
    private const string Contract = """
        {
          "apiVersion": "docuconf.dev/v1alpha1",
          "kind": "ConfigContract",
          "metadata": {"name": "orders"},
          "vars": {
            "PORT": {"type": "int", "description": "Port to listen on", "min": 1, "max": 65535, "default": 8080},
            "TIMEOUT": {"type": "duration", "description": "Request timeout", "encoding": "iso8601", "max": "1m"},
            "TOKEN": {"type": "string", "description": "API token", "secret": true, "required": true, "minLength": 8}
          }
        }
        """;

    [Fact]
    public void Returns_typed_values_and_defaults()
    {
        var values = DocuconfContract.FromJson(Contract).Load(new Dictionary<string, string> { ["TIMEOUT"] = "PT2.5S", ["TOKEN"] = "0123456789" });

        Assert.Equal(8080L, values.Get<long>("PORT"));
        Assert.Equal(TimeSpan.FromMilliseconds(2500), values.Get<TimeSpan>("TIMEOUT"));
        Assert.Equal("0123456789", values["TOKEN"]);
    }

    [Fact]
    public void Reports_every_violation_and_writes_the_termination_log_without_secrets()
    {
        var log = Path.GetTempFileName();
        try
        {
            var ex = Assert.Throws<ContractValidationException>(() => DocuconfContract.FromJson(Contract).Load(
                new Dictionary<string, string> { ["PORT"] = "0", ["TIMEOUT"] = "PT2M", ["TOKEN"] = "hunter2" },
                new DocuconfSettings { TerminationLogPath = log }));

            Assert.Equal(["PORT:out_of_range", "TIMEOUT:out_of_range", "TOKEN:out_of_range"], ex.Violations.Select(v => $"{v.Input}:{v.Code}").Order());
            var written = File.ReadAllText(log);
            Assert.Contains("[out_of_range] TOKEN", written, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", written + ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Fact]
    public void Rejects_a_contract_it_cannot_read()
    {
        var ex = Assert.Throws<ContractException>(() => DocuconfContract.FromJson("""
            {"apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract",
             "vars": {"A": {"type": "decimal", "description": "Not a type"}, "B": {"type": "list", "description": "No items", "encoding": "tsv"}}}
            """));

        Assert.Contains("vars.A: unknown type 'decimal'.", ex.Errors);
        Assert.Contains("vars.B: unknown list encoding 'tsv'.", ex.Errors);
        Assert.Contains("vars.B: items must be \"string\" or \"int\".", ex.Errors);
    }

    // The contract this SDK exports, read back in contract-first mode, accepts what the platform renders for the app
    // (indexed lists, timespan durations) and agrees with the options class.
    [Fact]
    public void Reads_the_contract_this_sdk_exports()
    {
        var cue = CueWriter.Write(ContractReader.Read([typeof(BoundsOptions)]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));
        var module = ExportTests.CueModule(cue);
        var (exit, output) = ExportTests.Cue(module, "export", "./out", "--out", "json", "-o", "contract.json");
        Assert.True(exit == 0, output);
        var contract = DocuconfContract.FromFile(Path.Join(module, "contract.json"));

        var values = contract.Load(new Dictionary<string, string> { ["BOUNDS__SHARDS__0"] = "7", ["BOUNDS__SHARDS__1"] = "1023" });
        Assert.Equal([7L, 1023L], values.Get<IReadOnlyList<long>>("BOUNDS__SHARDS"));
        Assert.Equal(4L, values.Get<long>("BOUNDS__WORKERS"));

        var result = contract.Validate(new Dictionary<string, string> { ["BOUNDS__SHARDS__0"] = "1024", ["BOUNDS__WORKERS"] = "2147483648" });
        Assert.Equal(["BOUNDS__SHARDS:out_of_range", "BOUNDS__WORKERS:out_of_range"], result.Violations.Select(v => $"{v.Input}:{v.Code}").Order());
    }
}

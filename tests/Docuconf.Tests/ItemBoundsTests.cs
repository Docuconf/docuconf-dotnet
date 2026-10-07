using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

[ConfigContract("bounds", Section = "Bounds")]
public sealed class BoundsOptions
{
    [ItemRange(0, 1023)]
    [Description("Shard ids this instance owns")]
    public int[] Shards { get; set; } = [];

    [Description("Ports to probe, 32-bit")]
    public List<int> Probes { get; set; } = [];

    [Description("Offsets, 64-bit")]
    public List<long> Offsets { get; set; } = [];

    [ItemRange(-5, 100_000)]
    [Description("Retry delays, 16-bit unsigned")]
    public List<ushort> Delays { get; set; } = [];

    [Description("Worker count, 32-bit")]
    public int Workers { get; set; } = 4;

    [Description("Bytes per page, unsigned")]
    public uint PageBytes { get; set; } = 4096;

    [Description("Sequence start, 64-bit")]
    public long Start { get; set; }

    [Description("Largest id, unsigned 64-bit")]
    public ulong MaxId { get; set; } = 1;
}

[ConfigContract("bad-bounds", Section = "Bad")]
public sealed class BadBoundsOptions
{
    [ItemRange(0, 10)]
    [Description("Strings cannot have item bounds")]
    public List<string> Names { get; set; } = [];

    [ItemRange(10, 0)]
    [Description("Bounds the wrong way round")]
    public int[] Backwards { get; set; } = [];

    [ItemRange(0, 10)]
    [Description("A default outside the bounds")]
    public int[] Levels { get; set; } = [3, 11];
}

public sealed class ItemBoundsTests
{
    private static string Export(Type type) =>
        CueWriter.Write(ContractReader.Read([type]), new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));

    private static BoundsOptions Resolve(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddDocuconf<BoundsOptions>(s => s.TerminationLogPath = "/nonexistent/termination-log");
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<BoundsOptions>>().Value;
    }

    private static string[] Failures(Dictionary<string, string?> config) =>
        Assert.Throws<OptionsValidationException>(() => Resolve(config)).Failures.Order().ToArray();

    [Fact]
    public void Item_bounds_and_integer_type_ranges_are_exported()
    {
        var model = ContractReader.Read([typeof(BoundsOptions)]);

        static string Items(VarSpec v) => $"{v.ItemMin}..{v.ItemMax}";
        static string Range(VarSpec v) => $"{v.Min}..{v.Max}";
        Assert.Equal("0..1023", Items(model.Vars["BOUNDS__SHARDS"]));
        Assert.Equal("-2147483648..2147483647", Items(model.Vars["BOUNDS__PROBES"]));
        Assert.Equal("..", Items(model.Vars["BOUNDS__OFFSETS"]));
        // The declared bounds are clamped to what ushort holds.
        Assert.Equal("0..65535", Items(model.Vars["BOUNDS__DELAYS"]));
        Assert.Equal("-2147483648..2147483647", Range(model.Vars["BOUNDS__WORKERS"]));
        Assert.Equal("0..4294967295", Range(model.Vars["BOUNDS__PAGEBYTES"]));
        Assert.Equal("..", Range(model.Vars["BOUNDS__START"]));
        Assert.Equal("0..", Range(model.Vars["BOUNDS__MAXID"]));

        var cue = Export(typeof(BoundsOptions));
        Assert.Contains("\t\t\titemMin: 0\n\t\t\titemMax: 1023\n", cue, StringComparison.Ordinal);
    }

    [Fact]
    public void Exported_item_bounds_pass_the_meta_schema()
    {
        var (exit, output) = ExportTests.Cue(ExportTests.CueModule(Export(typeof(BoundsOptions))), "vet", "-c", "./out");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void Bad_item_bounds_are_reported_at_declaration()
    {
        var ex = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(BadBoundsOptions)]));

        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Names: [ItemRange] applies to lists of integers", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Backwards: [ItemRange(10, 0)] has its minimum above its maximum", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Bad:Levels: the default [3, 11] item 1 is above the maximum 10", StringComparison.Ordinal));
    }

    [Fact]
    public void Items_within_bounds_bind()
    {
        var options = Resolve(new()
        {
            ["Bounds:Shards:0"] = "0",
            ["Bounds:Shards:1"] = "1023",
            ["Bounds:Offsets:0"] = "9223372036854775807",
        });

        Assert.Equal([0, 1023], options.Shards);
        Assert.Equal([long.MaxValue], options.Offsets);
    }

    [Fact]
    public void An_item_outside_its_bounds_is_out_of_range()
    {
        var failures = Failures(new() { ["Bounds:Shards:0"] = "3", ["Bounds:Shards:1"] = "1024" });

        var failure = Assert.Single(failures);
        // The same words as the contract-first mode, not the DataAnnotations sentence.
        Assert.Equal("[out_of_range] BOUNDS__SHARDS: item 1 is above the maximum 1023", failure);
    }

    [Fact]
    public void An_item_the_element_type_cannot_hold_is_out_of_range()
    {
        var failures = Failures(new()
        {
            ["Bounds:Probes:0"] = "2147483648",
            ["Bounds:Offsets:0"] = "99999999999999999999",
            ["Bounds:Workers"] = "3000000000",
        });

        Assert.Equal(
            [
                "[out_of_range] BOUNDS__OFFSETS: item 0 is outside the 64-bit integer range",
                "[out_of_range] BOUNDS__PROBES: item 0 is outside the range of Int32",
                "[out_of_range] BOUNDS__WORKERS: '3000000000' is outside the range of Int32",
            ],
            failures);
    }

    [Fact]
    public void An_item_that_is_not_an_integer_is_invalid()
    {
        var failures = Failures(new() { ["Bounds:Shards:0"] = "1", ["Bounds:Shards:1"] = "x" });

        Assert.Equal(["[invalid_type] BOUNDS__SHARDS: item 1 is not an integer"], failures);
    }

    // SPEC §5: indexed items are numbered from 0 with no gap. The configuration binder alone would read
    // Shards:0 and Shards:2 as a two-item list.
    [Fact]
    public void An_indexed_list_with_a_gap_is_invalid()
    {
        var failures = Failures(new() { ["Bounds:Shards:0"] = "1", ["Bounds:Shards:2"] = "3", ["Bounds:Probes:1"] = "80" });

        Assert.Equal(
            [
                "[invalid_type] BOUNDS__PROBES: items must be numbered from BOUNDS__PROBES__0 with no gap, but BOUNDS__PROBES__0 is not set",
                "[invalid_type] BOUNDS__SHARDS: items must be numbered from BOUNDS__SHARDS__0 with no gap, but BOUNDS__SHARDS__1 is not set",
            ],
            failures);
    }

    [Fact]
    public void A_non_numeric_suffix_is_not_an_item()
    {
        var options = Resolve(new()
        {
            ["Bounds:Probes:0"] = "80",
            ["Bounds:Probes:HOST"] = "other",
            ["Bounds:Offsets:HOST"] = "other",
        });

        Assert.Equal([80], options.Probes);
        Assert.Empty(options.Offsets); // no items: the list is unset and the initializer stands
    }
}

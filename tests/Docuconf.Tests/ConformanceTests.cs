using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Docuconf.Contract;

namespace Docuconf.Tests;

/// <summary>
/// Runs the shared conformance suite (SPEC §12, docuconf-go <c>conformance/README.md</c>) through the contract-first
/// mode. Set <c>DOCUCONF_CONFORMANCE</c> to <c>cases.json</c>; otherwise it is read from a docuconf-go checkout next to
/// this repository. With <c>DOCUCONF_REQUIRE_CONFORMANCE=1</c> a missing file fails the test instead of skipping it.
/// </summary>
public sealed class ConformanceTests
{
    /// <summary>
    /// Capability tags this SDK supports: all of them, so no case is skipped. <c>json-schema</c> is checked with
    /// JsonSchema.Net.
    /// </summary>
    private static readonly HashSet<string> Supported = ["int64", "json-schema"];

    [Fact]
    public void Passes_the_conformance_suite()
    {
        var path = Locate();
        if (!File.Exists(path))
        {
            var message = $"Conformance cases not found at {path}. Set DOCUCONF_CONFORMANCE to docuconf-go's conformance/cases.json.";
            Assert.False(Environment.GetEnvironmentVariable("DOCUCONF_REQUIRE_CONFORMANCE") == "1", message);
            Assert.Skip(message);
        }

        var suite = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(1, suite["version"]!.GetValue<int>());

        int passed = 0;
        var skipped = new List<string>();
        var failures = new List<string>();
        foreach (var c in suite["cases"]!.AsArray())
        {
            var id = c!["id"]!.GetValue<string>();
            var missing = c["requires"]!.AsArray().Select(t => t!.GetValue<string>()).Where(t => !Supported.Contains(t)).ToList();
            if (missing.Count > 0)
            {
                skipped.Add($"{id} (requires {string.Join(", ", missing)})");
                continue;
            }

            string? failure;
            try
            {
                failure = Run(c);
            }
            catch (Exception ex)
            {
                failure = $"threw {ex.GetType().Name}: {ex.Message}";
            }

            if (failure is null)
            {
                passed++;
            }
            else
            {
                failures.Add($"{id}: {failure}");
            }
        }

        var summary = $"conformance: {passed} passed, {skipped.Count} skipped, {failures.Count} failed ({path})";
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(summary);
        foreach (var s in skipped)
        {
            output?.WriteLine("  skipped " + s);
        }

        Assert.True(failures.Count == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, failures));
        // Every capability tag is supported: a skip means a new tag this runner does not know yet.
        Assert.True(skipped.Count == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, skipped));
    }

    private static string Locate()
    {
        if (Environment.GetEnvironmentVariable("DOCUCONF_CONFORMANCE") is { Length: > 0 } configured)
        {
            return Path.GetFullPath(configured);
        }

        // ../docuconf-go/conformance/cases.json, next to this repository.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Docuconf.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Join(root, "..", "docuconf-go", "conformance", "cases.json"));
    }

    /// <summary>Runs one case; returns why it failed, or null.</summary>
    private static string? Run(JsonNode c)
    {
        var contract = DocuconfContract.FromJson(c["contract"]!.ToJsonString());
        var env = c["env"]!.AsObject().ToDictionary(e => e.Key, e => e.Value!.GetValue<string>(), StringComparer.Ordinal);

        var log = Path.GetTempFileName(); // The termination log is only written when the file exists.
        try
        {
            ContractValues? values = null;
            ContractValidationException? error = null;
            try
            {
                values = contract.Load(env, new DocuconfSettings { TerminationLogPath = log });
            }
            catch (ContractValidationException ex)
            {
                error = ex;
            }

            if (c["expect"] is JsonObject expect)
            {
                if (error is not null)
                {
                    return "expected success, got " + string.Join("; ", error.Violations);
                }

                var mismatches = expect
                    .Select(e => Compare(contract.Model.Vars[e.Key], e.Value, values![e.Key]) is { } why ? $"{e.Key}: {why}" : null)
                    .OfType<string>()
                    .ToList();
                return mismatches.Count == 0 ? null : string.Join("; ", mismatches);
            }

            var expected = c["errors"]!.AsArray()
                .Select(e => $"{e!["var"]!.GetValue<string>()}:{e["code"]!.GetValue<string>()}")
                .Order(StringComparer.Ordinal)
                .ToList();
            if (error is null)
            {
                return $"expected errors {string.Join(", ", expected)}, but loading succeeded";
            }

            var actual = error.Violations.Select(v => $"{v.Input}:{v.Code}").Order(StringComparer.Ordinal).ToList();
            if (!expected.SequenceEqual(actual))
            {
                return $"expected errors {string.Join(", ", expected)}, got {string.Join("; ", error.Violations)}";
            }

            var output = error.Message + "\n" + string.Join("\n", error.Violations) + "\n" + File.ReadAllText(log);
            var leaked = contract.Model.Vars.Values
                .Where(v => v.Secret)
                .SelectMany(v => env.Where(e => e.Key == v.Name || e.Key.StartsWith(v.Name + "__", StringComparison.Ordinal)))
                .Where(e => e.Value.Length > 0 && output.Contains(e.Value, StringComparison.Ordinal))
                .Select(e => e.Key)
                .ToList();
            return leaked.Count == 0 ? null : $"error output contains the value of secret {string.Join(", ", leaked)}";
        }
        finally
        {
            File.Delete(log);
        }
    }

    /// <summary>Compares a typed value with its expected JSON (conformance/README.md, step 3); returns why they differ, or null.</summary>
    private static string? Compare(VarSpec spec, JsonNode? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"expected {Show(expected)}, got {Show(actual)}";
        }

        bool equal = spec.Type switch
        {
            VarType.Int => actual is long l && IntEquals(expected, l),
            VarType.Float => actual is double d && expected.GetValue<double>() == d,
            VarType.Bool => actual is bool b && expected.GetValue<bool>() == b,
            VarType.Duration => actual is TimeSpan ts && expected.GetValue<string>() == Canonical(ts),
            VarType.Url => actual is Uri u && expected.GetValue<string>() == u.OriginalString,
            VarType.List when spec.Items == "int" => actual is IReadOnlyList<long> ints
                && expected.AsArray().Count == ints.Count && expected.AsArray().Zip(ints).All(p => IntEquals(p.First!, p.Second)),
            VarType.List => actual is IReadOnlyList<string> strings
                && expected.AsArray().Select(i => i!.GetValue<string>()).SequenceEqual(strings),
            VarType.Json => actual is JsonNode node && JsonNode.DeepEquals(expected, node),
            _ => actual is string s && expected.GetValue<string>() == s,
        };
        return equal ? null : $"expected {Show(expected)}, got {Show(actual)}";
    }

    // Compared as big integers, so a 64-bit value is never rounded through a double.
    private static bool IntEquals(JsonNode expected, long actual) =>
        BigInteger.TryParse(expected.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n == actual;

    private static string Canonical(TimeSpan ts) => ts < TimeSpan.Zero ? "-" + GoDuration.Format(ts.Negate()) : GoDuration.Format(ts);

    private static string Show(object? value) => value switch
    {
        null => "null",
        JsonNode node => node.ToJsonString(),
        TimeSpan ts => Canonical(ts),
        IEnumerable<long> ints => "[" + string.Join(",", ints) + "]",
        IEnumerable<string> strings => "[" + string.Join(",", strings.Select(s => "\"" + s + "\"")) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}

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
    /// The capability tags this SDK supports (SPEC §12): an allow-list, so a case with a tag this runner does not know
    /// is skipped, never run, and the skip fails the test below. Every tag the suite has today is here, so no case is
    /// skipped. <c>json-schema</c> is checked with JsonSchema.Net.
    /// </summary>
    private static readonly HashSet<string> Supported =
        ["int64", "json-schema", "key-set", "deprecated", "strict-parsing", "files", "profiles", "overlays"];

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
        // scripts/conformance.sh prints it, so a run shows how many cases ran and how many were skipped.
        if (Environment.GetEnvironmentVariable("DOCUCONF_CONFORMANCE_SUMMARY") is { Length: > 0 } summaryFile)
        {
            File.AppendAllText(summaryFile, summary + Environment.NewLine + string.Concat(skipped.Select(s => "  skipped " + s + Environment.NewLine)));
        }
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

    /// <summary>
    /// Runs one case; returns why it failed, or null. Its files are written under a new, empty directory, which is the
    /// case's <c>DOCUCONF_FILE_ROOT</c>; the case's <c>env</c> plus that is the whole environment.
    /// </summary>
    private static string? Run(JsonNode c)
    {
        var contract = DocuconfContract.FromJson(c["contract"]!.ToJsonString());
        var env = c["env"]!.AsObject().ToDictionary(e => e.Key, e => e.Value!.GetValue<string>(), StringComparer.Ordinal);

        var root = Directory.CreateTempSubdirectory("docuconf-conformance-").FullName;
        var log = Path.GetTempFileName(); // The termination log is only written when the file exists.
        try
        {
            foreach (var (path, content) in c["files"]?.AsObject() ?? [])
            {
                var target = Path.Join(root, path.TrimStart('/'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (content!["base64"] is { } base64)
                {
                    File.WriteAllBytes(target, Convert.FromBase64String(base64.GetValue<string>()));
                }
                else
                {
                    File.WriteAllText(target, content["text"]!.GetValue<string>(), new System.Text.UTF8Encoding(false));
                }
            }

            env["DOCUCONF_FILE_ROOT"] = root;
            var warnings = new StringWriter();
            ContractValues? values = null;
            ContractValidationException? error = null;
            try
            {
                values = contract.Load(env, new DocuconfSettings { TerminationLogPath = log, Error = warnings });
            }
            catch (ContractValidationException ex)
            {
                error = ex;
            }

            if (Leaked(contract, env, warnings.ToString()) is { } leakedInWarnings)
            {
                return $"a warning contains the value of secret {leakedInWarnings}";
            }

            if (c["expect"] is JsonObject expect)
            {
                if (error is not null)
                {
                    return "expected success, got " + string.Join("; ", error.Violations);
                }

                var mismatches = expect
                    .Select(e => Compare(contract.Model, e.Key, e.Value, values![e.Key]) is { } why ? $"{e.Key}: {why}" : null)
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
            return Leaked(contract, env, output) is { } leaked ? $"error output contains the value of secret {leaked}" : null;
        }
        finally
        {
            File.Delete(log);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The secret variables whose value, or one of whose keys or items, <paramref name="output"/> contains; or null.
    /// </summary>
    private static string? Leaked(DocuconfContract contract, Dictionary<string, string> env, string output)
    {
        var leaked = contract.Model.Vars.Values
            .Where(v => v.Secret)
            .SelectMany(v => env
                .Where(e => e.Key == v.Name || e.Key.StartsWith(v.Name + "__", StringComparison.Ordinal))
                .SelectMany(e => v.Separator is { } separator ? e.Value.Split(separator).Append(e.Value) : [e.Value])
                .Where(value => value.Length >= 4 && output.Contains(value, StringComparison.Ordinal))
                .Select(_ => v.Name))
            .Distinct()
            .ToList();
        return leaked.Count == 0 ? null : string.Join(", ", leaked);
    }

    /// <summary>
    /// Compares an input's typed value with its expected JSON (conformance/README.md, step 3); returns why they differ,
    /// or null. A <c>config</c> file is its data, a <c>text</c> file its text, any other file input <c>true</c>.
    /// </summary>
    private static string? Compare(ContractModel model, string name, JsonNode? expected, object? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"expected {Show(expected)}, got {Show(actual)}";
        }

        if (model.Files.TryGetValue(name, out var file))
        {
            bool same = file.Type switch
            {
                FileType.Config => actual is JsonNode data && JsonEquals(expected, data),
                FileType.Text => actual is string text && expected.GetValue<string>() == text,
                _ => expected.GetValueKind() == System.Text.Json.JsonValueKind.True,
            };
            return same ? null : $"expected {Show(expected)}, got {Show(actual)}";
        }

        var spec = model.Vars[name];
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
            VarType.KeySet => actual is KeySet keys && expected.AsArray().Select(i => i!.GetValue<string>()).SequenceEqual(keys.Keys),
            _ => actual is string s && expected.GetValue<string>() == s,
        };
        return equal ? null : $"expected {Show(expected)}, got {Show(actual)}";
    }

    /// <summary>JSON equality with numbers compared by value: <c>3</c> equals <c>3.0</c>.</summary>
    private static bool JsonEquals(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var v) && JsonEquals(p.Value, v)),
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => JsonEquals(p.First, p.Second)),
        (JsonValue x, JsonValue y) when x.GetValueKind() == System.Text.Json.JsonValueKind.Number && y.GetValueKind() == System.Text.Json.JsonValueKind.Number =>
            decimal.TryParse(x.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
            && decimal.TryParse(y.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? m == n
                : x.GetValue<double>() == y.GetValue<double>(),
        _ => JsonNode.DeepEquals(a, b),
    };

    // Compared as big integers, so a 64-bit value is never rounded through a double.
    private static bool IntEquals(JsonNode expected, long actual) =>
        BigInteger.TryParse(expected.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n == actual;

    private static string Canonical(TimeSpan ts) => ts < TimeSpan.Zero ? "-" + GoDuration.Format(ts.Negate()) : GoDuration.Format(ts);

    private static string Show(object? value) => value switch
    {
        null => "null",
        JsonNode node => node.ToJsonString(),
        KeySet keys => "[" + string.Join(",", keys.Keys.Select(k => "\"" + k + "\"")) + "]",
        TimeSpan ts => Canonical(ts),
        IEnumerable<long> ints => "[" + string.Join(",", ints) + "]",
        IEnumerable<string> strings => "[" + string.Join(",", strings.Select(s => "\"" + s + "\"")) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}

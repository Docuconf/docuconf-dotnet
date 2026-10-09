using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Docuconf.Contract;
using Docuconf.Runtime;

namespace Docuconf;

/// <summary>
/// The contract-first mode (SPEC §11.2, item 11): validates an environment against a contract given as JSON, with no
/// options class, and returns typed values. Use it when the contract is written by hand in CUE and exported with
/// <c>cue export contract.cue --out json</c>, or to check an environment against another app's contract.
/// </summary>
/// <remarks>
/// <para>
/// Every wire encoding of SPEC §5 is parsed: lists and key sets as <c>csv</c> (with the contract's <c>separator</c>),
/// <c>json</c> or <c>indexed</c> (<c>NAME__0</c>, <c>NAME__1</c>, ...), durations as <c>go</c>, <c>iso8601</c>,
/// <c>seconds</c> or <c>timespan</c>. Values are checked with the same parsers and constraint checks as declared
/// options, and <c>json</c> values against their JSON Schema (draft 2020-12).
/// </para>
/// <para>
/// Values are layered as a host with config files layers them (SPEC §4.4, §4.7): the variable's default, then the
/// default of the profile the contract's selector picks, then each config-file overlay, then the environment. File
/// inputs and overlays are read from their paths under <c>DOCUCONF_FILE_ROOT</c> (or
/// <see cref="DocuconfSettings.FileRoot"/>), and checked as declared ones are; a <c>config</c> file in json, yaml or
/// toml is checked against its JSON Schema.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var contract = DocuconfContract.FromFile("contract.json");
/// ContractValues config = contract.Load();          // the process environment; throws with every violation
/// long port = config.Get&lt;long&gt;("PORT");
/// </code>
/// </example>
public sealed class DocuconfContract
{
    private DocuconfContract(ContractModel model) => Model = model;

    /// <summary>The variables the contract declares.</summary>
    public ContractModel Model { get; }

    /// <summary>Reads a contract exported as JSON.</summary>
    /// <exception cref="ContractException">The JSON is not a contract this SDK can read.</exception>
    public static DocuconfContract FromJson(string json) => new(ContractJson.Read(json));

    /// <summary>Reads a contract exported as JSON from a file.</summary>
    /// <exception cref="ContractException">The file is not a contract this SDK can read.</exception>
    public static DocuconfContract FromFile(string path) => FromJson(File.ReadAllText(path));

    /// <summary>
    /// Validates <paramref name="environment"/> (the process environment when null) and returns the typed values and
    /// every violation. Variables the contract does not declare are ignored.
    /// </summary>
    public ContractLoadResult Validate(IReadOnlyDictionary<string, string>? environment = null) => Validate(environment, null);

    /// <summary>
    /// Validates <paramref name="environment"/> (the process environment when null) with <paramref name="settings"/>:
    /// its <see cref="DocuconfSettings.FileRoot"/> and <see cref="DocuconfSettings.Clock"/>. The typed values include
    /// each file input, by name: a <c>config</c> file's data as a <see cref="JsonNode"/>, a <c>text</c> file's text,
    /// and a <see cref="TlsKeyPair"/>, <see cref="CaBundle"/>, <see cref="Keystore"/> or <see cref="BinaryFile"/>.
    /// </summary>
    public ContractLoadResult Validate(IReadOnlyDictionary<string, string>? environment, DocuconfSettings? settings)
    {
        environment ??= ProcessEnvironment();
        var root = settings?.FileRoot ?? (environment.TryGetValue("DOCUCONF_FILE_ROOT", out var r) ? r : "");
        var violations = new List<Violation>();
        var warnings = new List<string>();

        // Below the environment: the selected profile's defaults (SPEC §4.4), then the overlays (SPEC §4.7).
        var layers = new Dictionary<string, Layer>(StringComparer.Ordinal);
        if (Model.Profiles is { } profiles)
        {
            foreach (var (name, value) in profiles.Defaults.GetValueOrDefault(SelectedProfile(profiles, environment)) ?? [])
            {
                layers[name] = Layer.Profile(value);
            }
        }

        Overlays.Load(Model, root, layers, violations, warnings);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var spec in Model.Vars.Values)
        {
            values[spec.Name] = ContractFirstLoader.Load(spec, layers.GetValueOrDefault(spec.Name), environment, violations, out var set);
            if (set && spec.Deprecated is { } deprecation)
            {
                warnings.Add(DeprecationWarning(spec.Name, deprecation));
            }
        }

        string? Password(string name) =>
            values.GetValueOrDefault(name) as string ?? (environment.TryGetValue(name, out var raw) ? raw : null);
        var now = settings?.Clock() ?? DateTimeOffset.UtcNow;
        foreach (var (name, value) in FileChecks.LoadContract(Model, n => environment.GetValueOrDefault(n), root, now, Password, violations))
        {
            values[name] = value;
            if (value is not null && Model.Files[name].Deprecated is { } deprecation)
            {
                warnings.Add(DeprecationWarning(name, deprecation));
            }
        }

        return new ContractLoadResult(new ContractValues(values, Model), violations) { Warnings = warnings };
    }

    /// <summary>
    /// The profile in effect (SPEC §4.4): the selector's value when the environment sets it, read as the selector's
    /// type reads it (for a string, the empty string is a value), or <c>profiles.default</c>.
    /// </summary>
    private string SelectedProfile(ProfilesSpec profiles, IReadOnlyDictionary<string, string> environment) =>
        environment.TryGetValue(profiles.Selector, out var selected)
        && (selected.Length > 0 || Model.Vars.GetValueOrDefault(profiles.Selector)?.Type == VarType.String)
            ? selected
            : profiles.Default;

    private static string DeprecationWarning(string input, DeprecationSpec deprecation) =>
        $"docuconf: warning: {input} is deprecated but still set: {deprecation.Message}"
        + (deprecation.ReplacedBy is { } by ? $" (replaced by {by})" : "");

    /// <summary>
    /// Validates <paramref name="environment"/> (the process environment when null) and returns the typed values.
    /// </summary>
    /// <exception cref="ContractValidationException">
    /// The environment violates the contract. Lists every violation; they are also written to the termination log.
    /// </exception>
    public ContractValues Load(IReadOnlyDictionary<string, string>? environment = null, DocuconfSettings? settings = null)
    {
        var result = Validate(environment, settings);
        Warn(settings, result);
        if (result.Violations.Count > 0)
        {
            var lines = result.Violations.Select(v => v.ToString()).ToList();
            TerminationLog.Write(settings ?? new DocuconfSettings(), DocuconfStartup.Format(lines));
            throw new ContractValidationException(result.Violations);
        }

        return result.Values;
    }

    /// <summary>
    /// Validates <paramref name="environment"/> (the process environment when null) and returns the typed values. When
    /// the environment violates the contract, prints <c>docuconf: N configuration problems:</c> and one line per
    /// violation to stderr and the termination log, and exits with status 1.
    /// </summary>
    public ContractValues LoadOrExit(IReadOnlyDictionary<string, string>? environment = null, DocuconfSettings? settings = null)
    {
        settings ??= new DocuconfSettings();
        environment ??= ProcessEnvironment();
        foreach (var hint in DocuconfStartup.TypoHints(Model.Vars.Values.ToList(), environment.Keys))
        {
            settings.Error.WriteLine(hint);
        }

        var result = Validate(environment, settings);
        Warn(settings, result);
        if (!result.IsValid)
        {
            DocuconfStartup.Report(settings, result.Violations.Select(v => v.ToString()).ToList());
            if (settings.ThrowOnInvalid)
            {
                throw new ContractValidationException(result.Violations);
            }

            settings.Exit(1);
        }

        return result.Values;
    }

    private static void Warn(DocuconfSettings? settings, ContractLoadResult result)
    {
        foreach (var warning in result.Warnings)
        {
            (settings?.Error ?? Console.Error).WriteLine(warning);
        }
    }

    private static Dictionary<string, string> ProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", StringComparer.Ordinal);
}

/// <summary>The outcome of <see cref="DocuconfContract.Validate(IReadOnlyDictionary{string, string}?, DocuconfSettings?)"/>.</summary>
/// <param name="Values">The typed values; a variable with a violation is null.</param>
/// <param name="Violations">Every violation, with its SPEC §11.2 code. Messages never contain secret values.</param>
public sealed record ContractLoadResult(ContractValues Values, IReadOnlyList<Violation> Violations)
{
    /// <summary>Whether the environment satisfies the contract.</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>
    /// Warnings that are not violations, one line each: a deprecated input that is still set (SPEC §4.2), a variable
    /// set in two overlays. They name inputs, never values. <see cref="DocuconfContract.Load"/> writes them to stderr.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Thrown by <see cref="DocuconfContract.Load"/> when the environment violates the contract.</summary>
public sealed class ContractValidationException(IReadOnlyList<Violation> violations)
    : Exception("Invalid configuration:" + Environment.NewLine + string.Join(Environment.NewLine, violations.Select(v => "  " + v)))
{
    /// <summary>Every violation.</summary>
    public IReadOnlyList<Violation> Violations { get; } = violations;
}

/// <summary>
/// Typed values by variable name: <see cref="string"/> for <c>string</c> and <c>enum</c>, <see cref="long"/> for
/// <c>int</c>, <see cref="double"/> for <c>float</c>, <see cref="bool"/>, <see cref="TimeSpan"/> for <c>duration</c>,
/// <see cref="Uri"/> for <c>url</c>, <c>IReadOnlyList&lt;string&gt;</c> or <c>IReadOnlyList&lt;long&gt;</c> for
/// <c>list</c>, a <see cref="KeySet"/> for <c>keySet</c>, and a <see cref="JsonNode"/> for <c>json</c>. File inputs are
/// there too, by name. An optional input with no value and no default is null.
/// <see cref="ToString"/> and the debugger show secret values as <c>***</c>.
/// </summary>
[DebuggerDisplay("Count = {Count}")]
[DebuggerTypeProxy(typeof(ContractValuesDebugView))]
public sealed class ContractValues : IReadOnlyDictionary<string, object?>
{
    private readonly IReadOnlyDictionary<string, object?> values;
    private readonly IReadOnlySet<string> secrets;
    private readonly string service;

    /// <summary>Wraps typed values by variable name. None is treated as secret.</summary>
    public ContractValues(IReadOnlyDictionary<string, object?> values)
        : this(values, null)
    {
    }

    internal ContractValues(IReadOnlyDictionary<string, object?> values, ContractModel? model)
    {
        this.values = values;
        secrets = model?.Vars.Values.Where(v => v.Secret).Select(v => v.Name)
            .Concat(model.Files.Values.Where(f => f.Secret).Select(f => f.Name))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        service = model?.Service ?? "";
    }

    /// <summary>
    /// The value of <paramref name="name"/> as <typeparamref name="T"/>, or default when it has no value. An <c>int</c>
    /// variable can be read as any integer type that holds its value, and an <c>int</c> or <c>float</c> as
    /// <see cref="double"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The contract does not declare <paramref name="name"/>.</exception>
    /// <exception cref="InvalidCastException">The value is not a <typeparamref name="T"/>.</exception>
    /// <exception cref="OverflowException">The integer does not fit in <typeparamref name="T"/>.</exception>
    public T? Get<T>(string name)
    {
        if (!values.TryGetValue(name, out var value))
        {
            throw new KeyNotFoundException(NotDeclared(name));
        }

        if (value is null)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is long l && ContractReader.IsInteger(target))
        {
            try
            {
                return (T)Convert.ChangeType(l, target, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                // Never the value: it may be a secret.
                throw new OverflowException($"{name} does not fit in {target.Name}; read it with Get<long>(\"{name}\").");
            }
        }

        if (value is long or double && (target == typeof(double) || target == typeof(float) || target == typeof(decimal)))
        {
            return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }

        throw new InvalidCastException($"{name} is a {Friendly(value)} value, not {Friendly(typeof(T))}; read it with Get<{Friendly(value)}>(\"{name}\").");
    }

    private string NotDeclared(string name)
    {
        var where = service.Length > 0 ? $"contract '{service}'" : "the contract";
        var closest = values.Keys
            .Select(k => (Key: k, Distance: DocuconfStartup.Distance(name, k, 2)))
            .Where(k => k.Distance <= 2)
            .OrderBy(k => k.Distance).ThenBy(k => k.Key, StringComparer.Ordinal)
            .Select(k => k.Key)
            .FirstOrDefault();
        return closest is null ? $"{name} is not in {where}." : $"{name} is not in {where}; did you mean {closest}?";
    }

    private static string Friendly(object value) => value switch
    {
        IReadOnlyList<long> => "IReadOnlyList<long>",
        KeySet => "KeySet",
        IReadOnlyList<string> => "IReadOnlyList<string>",
        JsonNode => "JsonNode",
        _ => Friendly(value.GetType()),
    };

    private static string Friendly(Type type) =>
        type == typeof(long) ? "long"
        : type == typeof(int) ? "int"
        : type == typeof(double) ? "double"
        : type == typeof(bool) ? "bool"
        : type == typeof(string) ? "string"
        : type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Friendly))}>"
        : type.Name;

    /// <summary>The variables and their values, with secrets shown as <c>***</c>.</summary>
    public override string ToString() =>
        "ContractValues { " + string.Join(", ", values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key} = {Display(v.Key)}")) + " }";

    internal object? Display(string key) => secrets.Contains(key) && values[key] is not null ? "***" : values[key] switch
    {
        IEnumerable<string> list => "[" + string.Join(", ", list) + "]",
        IEnumerable<long> list => "[" + string.Join(", ", list) + "]",
        var v => v,
    };

    /// <inheritdoc />
    public object? this[string key] => values[key];

    /// <inheritdoc />
    public IEnumerable<string> Keys => values.Keys;

    /// <inheritdoc />
    public IEnumerable<object?> Values => values.Values;

    /// <inheritdoc />
    public int Count => values.Count;

    /// <inheritdoc />
    public bool ContainsKey(string key) => values.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, out object? value) => values.TryGetValue(key, out value);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class ContractValuesDebugView(ContractValues values)
    {
        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public KeyValuePair<string, object?>[] Items =>
            values.Keys.Order(StringComparer.Ordinal).Select(k => new KeyValuePair<string, object?>(k, values.Display(k))).ToArray();
    }
}

/// <summary>Reads one variable of a contract from an environment.</summary>
internal static partial class ContractFirstLoader
{
    [GeneratedRegex(@"^(?<name>[A-Z][A-Z0-9_]*)__(?<index>0|[1-9][0-9]*)\z")]
    private static partial Regex IndexedKey();

    /// <summary>
    /// Reads one variable: from the environment when it is set there, else from the layer below it (an overlay value,
    /// checked like an environment value, or a profile default), else its own default. <paramref name="set"/> says
    /// whether the environment or an overlay set it.
    /// </summary>
    public static object? Load(VarSpec spec, Layer? layer, IReadOnlyDictionary<string, string> env, List<Violation> violations, out bool set)
    {
        set = false;
        // Indexed lists arrive as NAME__0, NAME__1, ... and must be numbered from 0 with no gap (SPEC §5).
        List<string>? indexed = null;
        string? raw = null;
        if (spec.IsListLike && spec.Encoding == "indexed")
        {
            var items = env
                .Select(e => (Match: IndexedKey().Match(e.Key), e.Value))
                .Where(e => e.Match.Success && e.Match.Groups["name"].Value == spec.Name)
                .Select(e => (Index: e.Match.Groups["index"].Value, e.Value))
                .ToList();
            if (items.Count > 0 && WireFormat.CheckIndexGap(spec.Name, items.Select(e => e.Index).ToList()) is { } gap)
            {
                violations.Add(new Violation(gap.Code, spec.Name, gap.Message));
                return null;
            }

            indexed = items.OrderBy(e => BigInteger(e.Index)).Select(e => e.Value).ToList();
        }
        else
        {
            env.TryGetValue(spec.Name, out raw);
        }

        // Empty is unset for every type but string (SPEC §5).
        bool present = indexed is not null ? indexed.Count > 0 : raw is not null && (raw.Length > 0 || spec.Type == VarType.String);
        string source = "";
        if (!present && layer is { Bad: true })
        {
            return null; // the overlay value was reported already
        }

        if (!present && layer is { Raw: not null } or { Items: not null })
        {
            // An overlay value, as the wire string it stands for (SPEC §4.7); empty is unset, as in the environment.
            (raw, indexed) = (layer.Raw, layer.Items is { } items ? [.. items] : null);
            present = indexed is not null || raw!.Length > 0 || spec.Type == VarType.String;
            source = $"in overlay {layer.Source}, ";
        }

        set = present;
        if (!present)
        {
            if (layer?.Typed is { } profileDefault)
            {
                return Output(spec, profileDefault);
            }

            if (spec.Required)
            {
                violations.Add(new Violation(Codes.MissingRequired, spec.Name, "is required"));
                return null;
            }

            return Output(spec, spec.Default);
        }

        if (spec.Secret && (indexed ?? [raw!]).Select(InjectorReference.SchemeOf).FirstOrDefault(s => s is not null) is { } scheme)
        {
            violations.Add(new Violation(Codes.InvalidType, spec.Name,
                $"holds an unresolved {scheme} reference; the injector that should resolve it did not run"));
            return null;
        }

        var problem = indexed is not null && spec.Type is not VarType.Json ? ParseList(spec, indexed, out var typed) : Parse(spec, raw!, out typed);
        if (problem is null && typed is not null)
        {
            // A json value's maxLength measures it as received (SPEC §4.3).
            problem = Constraints.Check(spec, typed, spec.Type == VarType.Json ? raw : null);
        }

        if (problem is not null)
        {
            bool show = !spec.Secret && raw is not null && spec.Type is not (VarType.List or VarType.Json or VarType.KeySet);
            violations.Add(new Violation(problem.Code, spec.Name, source + (show ? $"'{raw}' " : "") + problem.Message + (spec.Secret ? " (value redacted)" : "")));
            return null;
        }

        return Output(spec, typed);
    }

    /// <summary>Parses a wire string into the typed form <see cref="Constraints.Check"/> takes.</summary>
    private static Problem? Parse(VarSpec spec, string raw, out object? value)
    {
        value = null;
        Problem? problem;
        switch (spec.Type)
        {
            case VarType.Int:
                problem = WireFormat.ParseInt(raw, out var l);
                value = l;
                return problem;
            case VarType.Float:
                problem = WireFormat.ParseFloat(raw, out var d);
                value = d;
                return problem;
            case VarType.Bool:
                problem = WireFormat.ParseBool(raw, out var b);
                value = b;
                return problem;
            case VarType.Duration:
                problem = WireFormat.ParseDuration(raw, spec.Encoding ?? "go", out var ts);
                value = ts;
                return problem;
            case VarType.List or VarType.KeySet when spec.Encoding == "json":
                return ParseJsonList(spec, raw, out value);
            case VarType.List or VarType.KeySet:
                return ParseList(spec, raw.Split(spec.Separator ?? ",", StringSplitOptions.None), out value);
            case VarType.Json:
                try
                {
                    value = JsonNode.Parse(raw);
                    return null;
                }
                catch (JsonException)
                {
                    return new Problem(Codes.InvalidType, "is not valid JSON");
                }

            default: // string, url, enum: checked as strings by Constraints.
                value = raw;
                return null;
        }
    }

    private static Problem? ParseList(VarSpec spec, IReadOnlyList<string> items, out object? value)
    {
        value = null;
        var list = new List<object>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            if (spec.ItemType != "int")
            {
                list.Add(items[i]);
            }
            else if (WireFormat.ParseInt(items[i], out var n) is { } problem)
            {
                return problem with { Message = $"item {i} {problem.Message}" };
            }
            else
            {
                list.Add(n);
            }
        }

        value = list;
        return null;
    }

    private static Problem? ParseJsonList(VarSpec spec, string raw, out object? value)
    {
        value = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return new Problem(Codes.InvalidType, "is not a JSON array");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new Problem(Codes.InvalidType, "is not a JSON array");
            }

            var list = new List<object>();
            int i = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (spec.ItemType == "int")
                {
                    if (item.ValueKind != JsonValueKind.Number)
                    {
                        return new Problem(Codes.InvalidType, $"item {i} is not an integer");
                    }

                    // The number's own text, so 1.0 and 1e2 are not integers and 2^64 is out of range.
                    if (WireFormat.ParseInt(item.GetRawText(), out var n) is { } problem)
                    {
                        return problem with { Message = $"item {i} {problem.Message}" };
                    }

                    list.Add(n);
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    list.Add(item.GetString()!);
                }
                else
                {
                    return new Problem(Codes.InvalidType, $"item {i} is not a string");
                }

                i++;
            }

            value = list;
            return null;
        }
    }

    /// <summary>Converts a checked value (or a contract default) into the type <see cref="ContractValues"/> exposes.</summary>
    private static object? Output(VarSpec spec, object? value) => value switch
    {
        null => null,
        string s when spec.Type == VarType.Duration => GoDuration.Parse(s),
        string s when spec.Type == VarType.Url => new Uri(s, UriKind.Absolute),
        List<object> items when spec.Type == VarType.KeySet => new KeySet(items.Cast<string>()),
        List<object> items when spec.Items == "int" => items.Cast<long>().ToList().AsReadOnly(),
        List<object> items => items.Cast<string>().ToList().AsReadOnly(),
        _ => value,
    };

    private static System.Numerics.BigInteger BigInteger(string digits) =>
        System.Numerics.BigInteger.Parse(digits, CultureInfo.InvariantCulture);
}

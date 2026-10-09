using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;

namespace Docuconf.Runtime;

/// <summary>
/// Binds an options instance one property at a time, so one bad value is reported alongside every other
/// problem instead of stopping the binder at the first exception. Then loads and checks the file inputs.
/// </summary>
internal static class DocuconfBinder
{
    public static List<Violation> Bind(object target, IConfiguration configuration, ContractModel model, DocuconfSettings settings)
    {
        var violations = new List<Violation>();

        // Properties outside the contract ([External], dictionaries, lists of objects) bind normally.
        foreach (var (key, path) in model.Unmodeled)
        {
            var section = configuration.GetSection(key);
            if (!section.Exists())
            {
                continue;
            }

            try
            {
                var value = section.Get(path[^1].PropertyType);
                if (value is not null)
                {
                    SetPath(target, path, value);
                }
            }
            catch (InvalidOperationException ex)
            {
                violations.Add(new Violation(Codes.InvalidType, key, ex.Message));
            }
        }

        foreach (var spec in model.Vars.Values)
        {
            if (spec.PropertyPath is null)
            {
                continue;
            }

            var section = SectionOf(configuration, spec);
            if (!IsSet(section, spec))
            {
                // Unset (or empty, which counts as unset for non-strings): the initializer stands.
                continue;
            }

            // A list arrives as NAME__0, NAME__1, ...; one value such as NAME=a,b would otherwise bind as an empty list.
            // A [Csv] list is that one value.
            if (spec.Type == VarType.List && spec.Encoding != "csv" && section.Value is { Length: > 0 } scalar)
            {
                var shown = spec.Secret ? "" : $" ('{scalar}')";
                violations.Add(new Violation(Codes.InvalidType, spec.Name,
                    $"is a list; set {spec.Name}__0, {spec.Name}__1, ... instead of one value{shown}"));
                continue;
            }

            // The configuration binder adds every child of a list section in key order, so it would read NAME__0 and
            // NAME__2 as a two-item list, and NAME__HOST as an item. SPEC §5 makes a gap invalid_type.
            if (spec.Type == VarType.List && !IsCsvValue(section, spec) && WireFormat.CheckIndexGap(spec.Name, Items(section).Select(c => c.Key).ToList()) is { } gap)
            {
                violations.Add(new Violation(gap.Code, spec.Name, gap.Message));
                continue;
            }

            if (spec.Secret && InjectorReference.SchemeOf(section.Value) is { } scheme)
            {
                // The value is a reference such as vault:secret/data/db#url, still unresolved. Never print it.
                violations.Add(new Violation(Codes.InvalidType, spec.Name,
                    $"holds an unresolved {scheme} reference; the injector that should resolve it did not run"));
                continue;
            }

            if (spec.Type == VarType.Json)
            {
                BindJson(target, section, spec, violations);
                continue;
            }

            try
            {
                if (Convert(section, spec, out var value) is { } problem)
                {
                    var shown = spec.Secret || spec.Type == VarType.List ? "" : $"'{section.Value}' ";
                    violations.Add(new Violation(problem.Code, spec.Name, shown + problem.Message + (spec.Secret ? " (value redacted)" : "")));
                    continue;
                }

                SetPath(target, spec.PropertyPath, value);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                var shown = spec.Secret || spec.Type == VarType.List ? "" : $"'{section.Value}' ";
                violations.Add(new Violation(Codes.InvalidType, spec.Name, $"{shown}is not a valid {Describe(spec)}" + (spec.Secret ? " (value redacted)" : "")));
            }
        }

        FileChecks.LoadAll(target, model, settings, violations, configuration);
        return violations;
    }

    private static void BindJson(object target, IConfigurationSection section, VarSpec spec, List<Violation> violations)
    {
        var type = spec.ClrType!;
        // maxLength measures the string the app received, before parsing (SPEC §4.3).
        if (!string.IsNullOrEmpty(section.Value) && Constraints.MaxLength(spec, section.Value, "of JSON") is { } tooLong)
        {
            violations.Add(new Violation(tooLong.Code, spec.Name, tooLong.Message));
            return;
        }

        object? value;
        try
        {
            value = JsonVar.Bind(section, type);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // System.Text.Json and binder messages give the path and the type, not the value; secrets get neither.
            var detail = spec.Secret ? " (value redacted)" : $": {ex.Message}";
            violations.Add(new Violation(Codes.InvalidType, spec.Name, $"is not valid JSON for {type.Name}{detail}"));
            return;
        }

        if (value is null)
        {
            violations.Add(new Violation(Codes.InvalidType, spec.Name, $"is null; expected JSON for {type.Name}"));
            return;
        }

        var problems = JsonVar.Validate(value);
        foreach (var problem in problems)
        {
            var message = spec.Secret && problem.Attribute is { } attr
                ? $"fails {attr.GetType().Name.Replace("Attribute", "", StringComparison.Ordinal)} (value redacted)"
                : problem.Message;
            violations.Add(new Violation(Codes.SchemaMismatch, spec.Name, $"{problem.Path}: {message}"));
        }

        // A nested section (an appsettings file or overlay) is not a string: measure the compact JSON the platform
        // would render for it.
        if (problems.Count == 0 && string.IsNullOrEmpty(section.Value)
            && Constraints.MaxLength(spec, CompactJson.Write(JsonVar.ToNode(value, type)), "of JSON") is { } tooLongSection)
        {
            violations.Add(new Violation(tooLongSection.Code, spec.Name, tooLongSection.Message));
            return;
        }

        if (problems.Count == 0)
        {
            SetPath(target, spec.PropertyPath!, value);
        }
    }

    /// <summary>
    /// Converts a configuration value with the same wire rules as the contract-first mode (SPEC §5), so the declared
    /// options and a contract accept the same strings. Returns the problem, or null with the value set.
    /// </summary>
    private static Problem? Convert(IConfigurationSection section, VarSpec spec, out object? value)
    {
        value = null;
        var clr = Nullable.GetUnderlyingType(spec.ClrType!) ?? spec.ClrType!;
        if (spec.Type == VarType.List)
        {
            return ConvertList(section, spec, clr, out value);
        }

        var raw = section.Value!;
        Problem? problem;
        switch (spec.Type)
        {
            case VarType.Int:
                problem = WireFormat.ParseInt(raw, out var l) ?? WireFormat.FitsIn(l, clr);
                value = problem is null ? System.Convert.ChangeType(clr == typeof(ulong) ? (ulong)l : l, clr, CultureInfo.InvariantCulture) : null;
                return problem;
            case VarType.Float:
                problem = WireFormat.ParseFloat(raw, out var d);
                value = problem is null ? System.Convert.ChangeType(d, clr, CultureInfo.InvariantCulture) : null;
                return problem;
            case VarType.Bool:
                problem = WireFormat.ParseBool(raw, out var b);
                value = b;
                return problem;
            case VarType.Duration:
                problem = WireFormat.ParseDuration(raw, "timespan", out var ts);
                value = ts;
                return problem;
            case VarType.Url:
                // maxLength counts the characters of the URL as given, not of Uri.ToString() (SPEC §4.3).
                problem = WireFormat.ParseUrl(raw, out var uri) ?? Constraints.MaxLength(spec, raw);
                value = clr == typeof(Uri) ? uri : raw;
                return problem;
        }

        if (clr.IsEnum)
        {
            // Names only, matched exactly as the platform matches enum values: Enum.Parse would also accept "3" or "WARN".
            var name = Enum.GetNames(clr).FirstOrDefault(n => string.Equals(n, raw, StringComparison.Ordinal));
            if (name is null)
            {
                return new Problem(Codes.NotInEnum, $"is not one of {string.Join(", ", spec.Values!)}");
            }

            value = Enum.Parse(clr, name);
            return null;
        }

        value = TypeDescriptor.GetConverter(clr).ConvertFromInvariantString(raw);
        return null;
    }

    /// <summary>
    /// Whether a value is set: the section exists and is not empty (empty is unset for non-strings), and a list has at
    /// least one item (SPEC §5: <c>NAME__HOST</c> alone is not a list).
    /// </summary>
    internal static bool IsSet(IConfigurationSection section, VarSpec spec) =>
        section.Exists()
        && !(section.Value == "" && spec.Type != VarType.String)
        && !(spec.Type == VarType.List && section.Value is null && !Items(section).Any());

    /// <summary>
    /// Whether a <see cref="CsvAttribute"/> list is set as one value. An appsettings file may still give it as a JSON
    /// array, whose items are read like an indexed list's.
    /// </summary>
    internal static bool IsCsvValue(IConfigurationSection section, VarSpec spec) =>
        spec.Type == VarType.List && spec.Encoding == "csv" && section.Value is { Length: > 0 };

    /// <summary>The children of a list section that are items: keys that are a decimal index with no leading zero.</summary>
    private static IEnumerable<IConfigurationSection> Items(IConfigurationSection section) =>
        section.GetChildren().Where(c => WireFormat.IsIndex(c.Key));

    /// <summary>
    /// Builds the list from its items in index order. Each item of an int list must be an integer the element type
    /// holds. Collections that are not arrays or lists are left to the configuration binder.
    /// </summary>
    private static Problem? ConvertList(IConfigurationSection section, VarSpec spec, Type clr, out object? value)
    {
        value = null;
        var element = ContractReader.ElementType(clr)!;
        // A [Csv] list is split exactly as given (SPEC §5): an empty item stays, for itemMinLength to reject.
        var items = IsCsvValue(section, spec)
            ? section.Value!.Split(spec.Separator ?? ",").Select((v, i) => (Key: i.ToString(CultureInfo.InvariantCulture), Value: v)).ToList()
            : Items(section).OrderBy(c => c.Key.Length).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => (c.Key, Value: c.Value ?? "")).ToList();
        var typed = new List<object?>(items.Count);
        foreach (var (key, raw) in items)
        {
            if (spec.Items != "int")
            {
                typed.Add(raw);
            }
            else if ((WireFormat.ParseInt(raw, out var item) ?? WireFormat.FitsIn(item, element)) is { } problem)
            {
                return problem with { Message = $"item {key} {problem.Message}" };
            }
            else
            {
                typed.Add(System.Convert.ChangeType(element == typeof(ulong) ? (ulong)item : item, element, CultureInfo.InvariantCulture));
            }
        }

        if (clr.IsArray)
        {
            var array = Array.CreateInstance(element, typed.Count);
            for (int i = 0; i < typed.Count; i++)
            {
                array.SetValue(typed[i], i);
            }

            value = array;
            return null;
        }

        var listType = clr.IsInterface ? typeof(List<>).MakeGenericType(element) : clr;
        if (clr.IsAssignableFrom(listType) && Activator.CreateInstance(listType) is System.Collections.IList list)
        {
            foreach (var item in typed)
            {
                list.Add(item);
            }

            value = list;
            return null;
        }

        value = section.Get(spec.ClrType!);
        return null;
    }

    private static string Describe(VarSpec spec) => spec.Type switch
    {
        VarType.Int => "integer",
        VarType.Float => "number",
        VarType.Bool => "boolean (true or false)",
        VarType.Duration => "duration (hh:mm:ss)",
        VarType.Url => "absolute URL",
        VarType.Enum => "value; expected one of " + string.Join(", ", spec.Values!),
        VarType.List => "list",
        VarType.Json => "JSON value",
        _ => "string",
    };

    internal static void SetPath(object target, IReadOnlyList<PropertyInfo> path, object? value)
    {
        object current = target;
        for (int i = 0; i < path.Count - 1; i++)
        {
            var next = path[i].GetValue(current);
            if (next is null)
            {
                next = Activator.CreateInstance(path[i].PropertyType)!;
                path[i].SetValue(current, next);
            }

            current = next;
        }

        path[^1].SetValue(current, value);
    }

    internal static object? GetPath(object target, IReadOnlyList<PropertyInfo> path)
    {
        object? current = target;
        foreach (var prop in path)
        {
            if (current is null)
            {
                return null;
            }

            current = prop.GetValue(current);
        }

        return current;
    }

    /// <summary>
    /// The configuration section a variable binds from. A variable renamed with <see cref="EnvNameAttribute"/> is read
    /// from its environment variable name first (the name the contract exports and the platform renders), then from its
    /// configuration path, where appsettings files put it.
    /// </summary>
    internal static IConfigurationSection SectionOf(IConfiguration configuration, VarSpec spec)
    {
        if (!string.Equals(spec.Name, DerivedName(spec.ConfigKey), StringComparison.Ordinal))
        {
            var renamed = configuration.GetSection(spec.Name);
            if (renamed.Exists())
            {
                return renamed;
            }
        }

        return configuration.GetSection(spec.ConfigKey);
    }

    /// <summary>The environment variable name derived from a configuration path: <c>Billing:Port</c> is <c>BILLING__PORT</c>.</summary>
    internal static string DerivedName(string configKey) => configKey.Replace(":", "__", StringComparison.Ordinal).ToUpperInvariant();

    internal static string Root(DocuconfSettings settings, IConfiguration? configuration = null) =>
        settings.FileRoot
        ?? NonEmpty(configuration?["Docuconf:FileRoot"])
        ?? NonEmpty(configuration?["DOCUCONF_FILE_ROOT"])
        ?? Environment.GetEnvironmentVariable("DOCUCONF_FILE_ROOT") ?? "";

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    internal static JsonSerializerOptions JsonOptions => SchemaGenerator.SerializerOptions;
}

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

            var section = configuration.GetSection(spec.ConfigKey);
            if (!section.Exists() || (section.Value == "" && spec.Type != VarType.String))
            {
                // Unset (or empty, which counts as unset for non-strings): the initializer stands.
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

        FileChecks.LoadAll(target, model, settings, violations);
        return violations;
    }

    private static void BindJson(object target, IConfigurationSection section, VarSpec spec, List<Violation> violations)
    {
        var type = spec.ClrType!;
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
            if (spec.Items == "int" && CheckIntItems(section, ContractReader.ElementType(clr)!) is { } itemProblem)
            {
                return itemProblem;
            }

            value = section.Get(spec.ClrType!);
            return null;
        }

        var raw = section.Value!;
        if (spec.Type == VarType.Int)
        {
            var problem = WireFormat.ParseInt(raw, out var l) ?? WireFormat.FitsIn(l, clr);
            value = problem is null ? System.Convert.ChangeType(clr == typeof(ulong) ? (ulong)l : l, clr, CultureInfo.InvariantCulture) : null;
            return problem;
        }

        if (clr.IsEnum)
        {
            // Names only: Enum.Parse would also accept "3".
            var name = Enum.GetNames(clr).FirstOrDefault(n => string.Equals(n, raw, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException();
            value = Enum.Parse(clr, name);
            return null;
        }

        if (clr == typeof(TimeSpan))
        {
            value = TimeSpanParser.Parse(raw);
            return null;
        }

        if (clr == typeof(Uri))
        {
            value = new Uri(raw, UriKind.Absolute);
            return null;
        }

        if (clr == typeof(double) || clr == typeof(float))
        {
            var d = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new FormatException();
            }

            value = System.Convert.ChangeType(d, clr, CultureInfo.InvariantCulture);
            return null;
        }

        value = TypeDescriptor.GetConverter(clr).ConvertFromInvariantString(raw);
        return null;
    }

    /// <summary>Each item of an int list must be an integer the element type holds.</summary>
    private static Problem? CheckIntItems(IConfigurationSection section, Type element)
    {
        foreach (var child in section.GetChildren())
        {
            var raw = child.Value ?? "";
            if ((WireFormat.ParseInt(raw, out var item) ?? WireFormat.FitsIn(item, element)) is { } problem)
            {
                return problem with { Message = $"item {child.Key} {problem.Message}" };
            }
        }

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

    internal static string Root(DocuconfSettings settings) =>
        settings.FileRoot ?? Environment.GetEnvironmentVariable("DOCUCONF_FILE_ROOT") ?? "";

    internal static JsonSerializerOptions JsonOptions => SchemaGenerator.SerializerOptions;
}

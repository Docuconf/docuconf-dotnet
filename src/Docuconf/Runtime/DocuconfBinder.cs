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

            try
            {
                SetPath(target, spec.PropertyPath, Convert(section, spec));
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

    private static object? Convert(IConfigurationSection section, VarSpec spec)
    {
        var clr = Nullable.GetUnderlyingType(spec.ClrType!) ?? spec.ClrType!;
        if (spec.Type == VarType.List)
        {
            return section.Get(spec.ClrType!);
        }

        var raw = section.Value!;
        if (clr.IsEnum)
        {
            // Names only: Enum.Parse would also accept "3".
            var name = Enum.GetNames(clr).FirstOrDefault(n => string.Equals(n, raw, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException();
            return Enum.Parse(clr, name);
        }

        if (clr == typeof(TimeSpan))
        {
            return TimeSpanParser.Parse(raw);
        }

        if (clr == typeof(Uri))
        {
            return new Uri(raw, UriKind.Absolute);
        }

        if (clr == typeof(double) || clr == typeof(float))
        {
            var d = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new FormatException();
            }

            return System.Convert.ChangeType(d, clr, CultureInfo.InvariantCulture);
        }

        return TypeDescriptor.GetConverter(clr).ConvertFromInvariantString(raw);
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

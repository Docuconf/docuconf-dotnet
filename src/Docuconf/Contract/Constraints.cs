using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Docuconf.Runtime;

namespace Docuconf.Contract;

/// <summary>
/// Checks a typed value against a variable's contract constraints. Used for values that end up in the contract
/// (defaults, appsettings values), so a bad one fails at export rather than at deploy, and by the contract-first mode
/// at startup.
/// </summary>
internal static class Constraints
{
    /// <summary>
    /// Returns the violation, or null when the value is fine. Values are typed: <see cref="long"/> for int,
    /// <see cref="double"/> for float, a Go-syntax string or <see cref="TimeSpan"/> for duration, a string for string,
    /// url and enum, a <c>List&lt;object&gt;</c> of strings or longs for list, and a <see cref="JsonNode"/> for json.
    /// Messages never contain the value.
    /// </summary>
    public static Problem? Check(VarSpec spec, object value)
    {
        switch (spec.Type)
        {
            case VarType.Int when value is long l:
                if (spec.Min is long min && l < min) return OutOfRange($"is below the minimum {min}");
                if (spec.Max is long max && l > max) return OutOfRange($"is above the maximum {max}");
                return null;
            case VarType.Float when value is double d:
                if (!double.IsFinite(d)) return new Problem(Codes.InvalidType, "is not a finite number");
                if (ToDouble(spec.Min) is double fmin && d < fmin) return OutOfRange($"is below the minimum {fmin.ToString(CultureInfo.InvariantCulture)}");
                if (ToDouble(spec.Max) is double fmax && d > fmax) return OutOfRange($"is above the maximum {fmax.ToString(CultureInfo.InvariantCulture)}");
                return null;
            case VarType.Duration when value is string or TimeSpan:
                var ts = value as TimeSpan? ?? GoDuration.Parse((string)value);
                if (spec.Min is string dmin && ts < GoDuration.Parse(dmin)) return OutOfRange($"is shorter than {dmin}");
                if (spec.Max is string dmax && ts > GoDuration.Parse(dmax)) return OutOfRange($"is longer than {dmax}");
                return null;
            case VarType.Enum when value is string e:
                return spec.Values!.Contains(e, StringComparer.Ordinal) ? null : new Problem(Codes.NotInEnum, $"is not one of {string.Join(", ", spec.Values!)}");
            case VarType.Url when value is string u:
                return WireFormat.ParseUrl(u, out var uri) ?? WireFormat.CheckScheme(uri!, spec.Schemes);
            case VarType.String when value is string str:
                // Lengths count characters (Unicode scalar values), as CUE's strings.MinRunes does, not UTF-16 units.
                int length = str.EnumerateRunes().Count();
                if (spec.MinLength is int minLen && length < minLen) return OutOfRange($"is shorter than {minLen} characters");
                if (spec.MaxLength is int maxLen && length > maxLen) return OutOfRange($"is longer than {maxLen} characters");
                if (spec.Pattern is { } pattern && !Regex.IsMatch(str, pattern)) return new Problem(Codes.PatternMismatch, $"does not match {pattern}");
                return null;
            case VarType.List when value is List<object> items:
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] is not long item) continue;
                    if (spec.ItemMin is long itemMin && item < itemMin) return OutOfRange($"item {i} is below the minimum {itemMin}");
                    if (spec.ItemMax is long itemMax && item > itemMax) return OutOfRange($"item {i} is above the maximum {itemMax}");
                }

                if (spec.MinItems is int minItems && items.Count < minItems) return new Problem(Codes.TooFewItems, $"has {items.Count} items, fewer than {minItems}");
                if (spec.MaxItems is int maxItems && items.Count > maxItems) return new Problem(Codes.TooManyItems, $"has {items.Count} items, more than {maxItems}");
                return null;
            case VarType.Bool when value is bool:
                return null;
            case VarType.Json when value is JsonNode node:
                // A contract read from JSON has no .NET type to bind; its schema is the platform's to check.
                return spec.ClrType is null ? null : JsonVar.Check(spec, node) is { } problem ? new Problem(Codes.SchemaMismatch, problem) : null;
            default:
                return new Problem(Codes.InvalidType, $"is not a valid {spec.Type.ToString().ToLowerInvariant()}");
        }
    }

    private static Problem OutOfRange(string message) => new(Codes.OutOfRange, message);

    private static double? ToDouble(object? bound) => bound switch
    {
        double d => d,
        long l => l,
        _ => null,
    };

    /// <summary>Formats a non-secret value for an error message.</summary>
    public static string Show(object value) => value switch
    {
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        List<object> list => "[" + string.Join(", ", list.Select(Show)) + "]",
        JsonNode node => node.ToJsonString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}

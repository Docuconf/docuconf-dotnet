using System.Globalization;
using System.Text.RegularExpressions;

namespace Docuconf.Contract;

/// <summary>
/// Checks a value against a variable's contract constraints. Used for values that end up in the contract
/// (defaults, appsettings values), so a bad one fails at export rather than at deploy.
/// </summary>
internal static class Constraints
{
    /// <summary>Returns a description of the violation, or null when the value is fine.</summary>
    public static string? Check(VarSpec spec, object value)
    {
        switch (spec.Type)
        {
            case VarType.Int when value is long l:
                if (spec.Min is long min && l < min) return $"is below the minimum {min}";
                if (spec.Max is long max && l > max) return $"is above the maximum {max}";
                return null;
            case VarType.Float when value is double d:
                if (double.IsNaN(d) || double.IsInfinity(d)) return "is not a finite number";
                if (spec.Min is double fmin && d < fmin) return $"is below the minimum {fmin.ToString(CultureInfo.InvariantCulture)}";
                if (spec.Max is double fmax && d > fmax) return $"is above the maximum {fmax.ToString(CultureInfo.InvariantCulture)}";
                return null;
            case VarType.Duration when value is string s:
                var ts = GoDuration.Parse(s);
                if (spec.Min is string dmin && ts < GoDuration.Parse(dmin)) return $"is shorter than {dmin}";
                if (spec.Max is string dmax && ts > GoDuration.Parse(dmax)) return $"is longer than {dmax}";
                return null;
            case VarType.Enum when value is string e:
                return spec.Values!.Contains(e, StringComparer.Ordinal) ? null : $"is not one of {string.Join(", ", spec.Values!)}";
            case VarType.Url when value is string u:
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri)) return "is not an absolute URL";
                if (spec.Schemes is { } schemes && !schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
                {
                    return $"does not use one of the schemes {string.Join(", ", schemes)}";
                }

                return null;
            case VarType.String when value is string str:
                if (spec.MinLength is int minLen && str.Length < minLen) return $"is shorter than {minLen} characters";
                if (spec.MaxLength is int maxLen && str.Length > maxLen) return $"is longer than {maxLen} characters";
                if (spec.Pattern is { } pattern && !Regex.IsMatch(str, pattern)) return $"does not match {pattern}";
                return null;
            case VarType.List when value is List<object> items:
                if (spec.MinItems is int minItems && items.Count < minItems) return $"has fewer than {minItems} items";
                if (spec.MaxItems is int maxItems && items.Count > maxItems) return $"has more than {maxItems} items";
                return null;
            case VarType.Bool when value is bool:
                return null;
            default:
                return $"is not a valid {spec.Type.ToString().ToLowerInvariant()}";
        }
    }

    /// <summary>Formats a non-secret value for an error message.</summary>
    public static string Show(object value) => value switch
    {
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        List<object> list => "[" + string.Join(", ", list.Select(Show)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };
}

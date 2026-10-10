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
    /// <param name="spec">The variable.</param>
    /// <param name="value">The typed value.</param>
    /// <param name="wire">For a json value, the string the app received, which <c>maxLength</c> measures (SPEC §4.3).
    /// Null measures the compact JSON of <paramref name="value"/>, as the platform renders it.</param>
    public static Problem? Check(VarSpec spec, object value, string? wire = null)
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
                return WireFormat.ParseUrl(u, out var uri) ?? WireFormat.CheckScheme(uri!, spec.Schemes) ?? MaxLength(spec, u);
            case VarType.String when value is string str:
                // Lengths count characters (Unicode scalar values), as CUE's strings.MinRunes does, not UTF-16 units.
                int length = str.EnumerateRunes().Count();
                if (spec.MinLength is int minLen && length < minLen) return OutOfRange($"is shorter than {minLen} characters");
                if (spec.MaxLength is int maxLen && length > maxLen) return OutOfRange($"is longer than {maxLen} characters");
                if (spec.Pattern is { } pattern && !Re2.IsMatch(pattern, str)) return new Problem(Codes.PatternMismatch, $"does not match {pattern}");
                return null;
            case VarType.List when value is List<object> items:
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] is string s && ItemLength(spec, s) is { } tooLong) return tooLong with { Message = $"item {i} {tooLong.Message}" };
                    if (items[i] is not long item) continue;
                    if (spec.ItemMin is long itemMin && item < itemMin) return OutOfRange($"item {i} is below the minimum {itemMin}");
                    if (spec.ItemMax is long itemMax && item > itemMax) return OutOfRange($"item {i} is above the maximum {itemMax}");
                }

                if (spec.MinItems is int minItems && items.Count < minItems) return new Problem(Codes.TooFewItems, $"has {items.Count} items, fewer than {minItems}");
                if (spec.MaxItems is int maxItems && items.Count > maxItems) return new Problem(Codes.TooManyItems, $"has {items.Count} items, more than {maxItems}");
                return null;
            case VarType.KeySet when value is List<object> keys:
                // An empty key (a stray separator) is out of range whatever the bounds (SPEC §4.3). Never the key itself.
                for (int i = 0; i < keys.Count; i++)
                {
                    int n = Length((string)keys[i]);
                    if (n == 0) return OutOfRange($"key {i + 1} is empty");
                    if (spec.KeyMinLength is int keyMin && n < keyMin) return OutOfRange($"key {i + 1} is {n} characters, below keyMinLength {keyMin}");
                    if (spec.KeyMaxLength is int keyMax && n > keyMax) return OutOfRange($"key {i + 1} is {n} characters, above keyMaxLength {keyMax}");
                }

                int minKeys = spec.MinKeys ?? 1, maxKeys = spec.MaxKeys ?? 2;
                if (keys.Count < minKeys) return new Problem(Codes.TooFewItems, $"has {keys.Count} keys, fewer than minKeys {minKeys}");
                if (keys.Count > maxKeys) return new Problem(Codes.TooManyItems, $"has {keys.Count} keys, more than maxKeys {maxKeys}");
                return null;
            case VarType.Bool when value is bool:
                return null;
            case VarType.Json when value is JsonNode node:
                if (spec.MaxLength is not null && MaxLength(spec, wire ?? CompactJson.Write(node), "of JSON") is { } tooLongJson) return tooLongJson;
                // A declared value is checked by binding it to its .NET type; a contract read from JSON, which has no type,
                // against the contract's JSON Schema.
                var mismatch = spec.ClrType is not null ? JsonVar.Check(spec, node)
                    : spec.Schema is not null ? JsonSchemaCheck.Check(spec, node)
                    : null;
                return mismatch is null ? null : new Problem(Codes.SchemaMismatch, mismatch);
            default:
                return new Problem(Codes.InvalidType, $"is not a valid {spec.Type.ToString().ToLowerInvariant()}");
        }
    }

    /// <summary>
    /// The note after a secret's problem message. A key set's messages name keys by position and never hold one, so
    /// they read exactly as SPEC §4.3 words them (<c>key 2 is empty</c>), with no note.
    /// </summary>
    public static string Redacted(VarSpec spec) => spec.Secret && spec.Type != VarType.KeySet ? " (value redacted)" : "";

    private static Problem OutOfRange(string message) => new(Codes.OutOfRange, message);

    /// <summary>Length in characters: Unicode scalar values, as CUE's <c>strings.MaxRunes</c> counts, not UTF-16 units.</summary>
    public static int Length(string s) => s.EnumerateRunes().Count();

    /// <summary>
    /// A url or json value above <c>maxLength</c>. The message gives the length, never the value, so it is safe for
    /// secrets.
    /// </summary>
    public static Problem? MaxLength(VarSpec spec, string wire, string what = "")
    {
        if (spec.MaxLength is not int max) return null;
        int n = Length(wire);
        var of = what.Length > 0 ? " " + what : "";
        return n > max ? OutOfRange($"is {n} characters{of}, above maxLength {max}") : null;
    }

    /// <summary>A string list item outside <c>itemMinLength</c>/<c>itemMaxLength</c>; the message never quotes it.</summary>
    public static Problem? ItemLength(VarSpec spec, string item)
    {
        if (spec.ItemMinLength is null && spec.ItemMaxLength is null) return null;
        int n = Length(item);
        if (spec.ItemMinLength is int min && n < min) return OutOfRange($"is {n} characters, below itemMinLength {min}");
        if (spec.ItemMaxLength is int max && n > max) return OutOfRange($"is {n} characters, above itemMaxLength {max}");
        return null;
    }

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

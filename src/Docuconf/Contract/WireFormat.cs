using System.Globalization;
using System.Text.RegularExpressions;

namespace Docuconf.Contract;

/// <summary>A value that does not parse or does not satisfy a constraint: a stable code and a message without the value.</summary>
/// <param name="Code">A code from <see cref="Codes"/>.</param>
/// <param name="Message">What is wrong. Never contains the value, so it is safe for secrets.</param>
internal sealed record Problem(string Code, string Message);

/// <summary>
/// Parses wire strings (SPEC §5) into typed values. Shared by options binding, appsettings export and the
/// contract-first mode, so every path accepts and rejects the same strings.
/// </summary>
internal static partial class WireFormat
{
    [GeneratedRegex("^[+-]?[0-9]+\\z")]
    private static partial Regex IntegerSyntax();

    // The platform's own rule for a url literal (contract.cue, #Validate).
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s]+\z")]
    private static partial Regex UrlSyntax();

    // P[nD][T[nH][nM][nS]] (SPEC §5): at least one component, and one after a T; n may have a fraction after . or ,.
    [GeneratedRegex(@"^P(?!\z)(?:([0-9]+(?:[.,][0-9]+)?)D)?(?:T(?=[0-9])(?:([0-9]+(?:[.,][0-9]+)?)H)?(?:([0-9]+(?:[.,][0-9]+)?)M)?(?:([0-9]+(?:[.,][0-9]+)?)S)?)?\z")]
    private static partial Regex Iso8601Syntax();

    // A decimal float (SPEC §5): a digit on each side of the point; never hex, inf, nan, .5 or 5.
    [GeneratedRegex(@"^[+-]?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z")]
    private static partial Regex FloatSyntax();

    // [d.]hh:mm:ss[.f] (SPEC §5): hh below 24, mm and ss below 60, one to seven fraction digits, unsigned.
    [GeneratedRegex(@"^(?:(?<d>[0-9]+)\.)?(?<h>[01]?[0-9]|2[0-3]):(?<m>[0-5][0-9]):(?<s>[0-5][0-9])(?:\.(?<f>[0-9]{1,7}))?\z")]
    private static partial Regex TimespanSyntax();

    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)?\z")]
    private static partial Regex SecondsSyntax();

    // An indexed list item's suffix: a decimal index with no leading zero (SPEC §5). NAME__HOST is not an item.
    [GeneratedRegex("^(?:0|[1-9][0-9]*)\\z")]
    private static partial Regex IndexSyntax();

    /// <summary>Whether <paramref name="suffix"/> (the part after <c>NAME__</c>) is an indexed list item.</summary>
    public static bool IsIndex(string suffix) => IndexSyntax().IsMatch(suffix);

    /// <summary>
    /// Indexed list items must be numbered from 0 with no gap (SPEC §5): a host that stops at the gap and one that
    /// skips it would read different lists. Returns the problem for the first missing index, or null.
    /// </summary>
    public static Problem? CheckIndexGap(string name, IReadOnlyCollection<string> indices)
    {
        var set = indices.ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < set.Count; i++)
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            if (!set.Contains(index))
            {
                return new Problem(Codes.InvalidType, $"items must be numbered from {name}__0 with no gap, but {name}__{index} is not set");
            }
        }

        return null;
    }

    /// <summary>Duration encodings (SPEC §5).</summary>
    public static readonly IReadOnlyList<string> DurationEncodings = ["go", "iso8601", "seconds", "timespan"];

    /// <summary>List encodings (SPEC §5).</summary>
    public static readonly IReadOnlyList<string> ListEncodings = ["csv", "json", "indexed"];

    /// <summary>A base-10 integer within the 64-bit signed range: <c>invalid_type</c> otherwise, or <c>out_of_range</c> beyond it.</summary>
    public static Problem? ParseInt(string raw, out long value)
    {
        value = 0;
        if (!IntegerSyntax().IsMatch(raw))
        {
            return new Problem(Codes.InvalidType, "is not an integer");
        }

        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return new Problem(Codes.OutOfRange, "is outside the 64-bit integer range");
        }

        return null;
    }

    /// <summary>
    /// The range an integer type holds, where it is narrower than 64 bits signed (SPEC §5): exported as
    /// <c>min</c>/<c>max</c> (or <c>itemMin</c>/<c>itemMax</c>) so the platform never sends a value the app cannot hold.
    /// </summary>
    public static (long? Min, long? Max) RangeOf(Type integerType)
    {
        var t = Nullable.GetUnderlyingType(integerType) ?? integerType;
        if (t == typeof(int)) return (int.MinValue, int.MaxValue);
        if (t == typeof(uint)) return (0, uint.MaxValue);
        if (t == typeof(short)) return (short.MinValue, short.MaxValue);
        if (t == typeof(ushort)) return (0, ushort.MaxValue);
        if (t == typeof(byte)) return (0, byte.MaxValue);
        if (t == typeof(sbyte)) return (sbyte.MinValue, sbyte.MaxValue);
        if (t == typeof(ulong)) return (0, null);
        return (null, null);
    }

    /// <summary>Checks a 64-bit value against an integer type's range.</summary>
    public static Problem? FitsIn(long value, Type integerType)
    {
        var (min, max) = RangeOf(integerType);
        return (min is { } lo && value < lo) || (max is { } hi && value > hi)
            ? new Problem(Codes.OutOfRange, $"is outside the range of {(Nullable.GetUnderlyingType(integerType) ?? integerType).Name}")
            : null;
    }

    /// <summary>A finite decimal number, read independently of the process locale.</summary>
    public static Problem? ParseFloat(string raw, out double value)
    {
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        value = 0;
        if (!FloatSyntax().IsMatch(raw) || !double.TryParse(raw, style, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            return new Problem(Codes.InvalidType, "is not a finite number");
        }

        return null;
    }

    /// <summary><c>true</c> or <c>false</c>, in any case.</summary>
    public static Problem? ParseBool(string raw, out bool value)
    {
        value = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        return value || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
            ? null
            : new Problem(Codes.InvalidType, "is not a boolean (true or false)");
    }

    /// <summary>An absolute URL of the form <c>scheme://...</c>, as the platform checks it.</summary>
    public static Problem? ParseUrl(string raw, out Uri? value)
    {
        value = null;
        if (!UrlSyntax().IsMatch(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out value))
        {
            return new Problem(Codes.InvalidType, "is not a URL of the form scheme://...");
        }

        return null;
    }

    /// <summary>Checks a URL's scheme against the allowed ones, ignoring case as URL schemes do.</summary>
    public static Problem? CheckScheme(Uri url, IReadOnlyList<string>? schemes) =>
        schemes is null || schemes.Contains(url.Scheme, StringComparer.OrdinalIgnoreCase)
            ? null
            : new Problem(Codes.InvalidScheme, $"must use one of the schemes {string.Join(", ", schemes)}");

    /// <summary>A duration in one of the encodings of SPEC §5.</summary>
    public static Problem? ParseDuration(string raw, string encoding, out TimeSpan value)
    {
        value = default;
        bool ok = encoding switch
        {
            "go" => GoDuration.TryParse(raw, out value),
            "iso8601" => TryParseIso8601(raw, out value),
            "seconds" => TryParseSeconds(raw, out value),
            "timespan" => TryParseTimespan(raw, out value),
            _ => throw new ArgumentException($"Unknown duration encoding '{encoding}'.", nameof(encoding)),
        };
        return ok ? null : new Problem(Codes.InvalidType, $"is not a duration in the {encoding} encoding, such as {Example(encoding)}");
    }

    private static string Example(string encoding) => encoding switch
    {
        "go" => "1m30s",
        "iso8601" => "PT90S",
        "seconds" => "90",
        _ => "00:01:30 (hh:mm:ss, or d.hh:mm:ss)",
    };

    private static bool TryParseIso8601(string raw, out TimeSpan value)
    {
        value = default;
        var m = Iso8601Syntax().Match(raw);
        if (!m.Success)
        {
            return false;
        }

        try
        {
            decimal seconds = 0;
            decimal Part(int group) => decimal.Parse(m.Groups[group].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (m.Groups[1].Success) seconds += Part(1) * 86400;
            if (m.Groups[2].Success) seconds += Part(2) * 3600;
            if (m.Groups[3].Success) seconds += Part(3) * 60;
            if (m.Groups[4].Success) seconds += Part(4);
            return TryFromSeconds(seconds, out value);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>[d.]hh:mm:ss[.f]</c>, exactly as SPEC §5 gives it: what <c>TimeSpan.Parse</c> reads, without its extra forms
    /// (a sign, <c>hh:mm</c>, a bare number of days, surrounding whitespace).
    /// </summary>
    public static bool TryParseTimespan(string raw, out TimeSpan value)
    {
        value = default;
        var m = TimespanSyntax().Match(raw);
        if (!m.Success)
        {
            return false;
        }

        try
        {
            decimal seconds = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600m
                + int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) * 60m
                + int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            if (m.Groups["d"].Success) seconds += decimal.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) * 86400m;
            if (m.Groups["f"].Success) seconds += decimal.Parse("0." + m.Groups["f"].Value, CultureInfo.InvariantCulture);
            return TryFromSeconds(seconds, out value);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseSeconds(string raw, out TimeSpan value)
    {
        value = default;
        return SecondsSyntax().IsMatch(raw)
            && decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            && TryFromSeconds(seconds, out value);
    }

    private static bool TryFromSeconds(decimal seconds, out TimeSpan value)
    {
        value = default;
        try
        {
            var ticks = decimal.Truncate(seconds * TimeSpan.TicksPerSecond);
            if (ticks > long.MaxValue)
            {
                return false;
            }

            value = TimeSpan.FromTicks((long)ticks);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Docuconf.Contract;

/// <summary>
/// Durations in Go syntax (<c>1h30m</c>), the form contracts and platform values use.
/// .NET reads them as <see cref="TimeSpan"/> in its own <c>hh:mm:ss</c> format; the platform renders that for us.
/// </summary>
public static partial class GoDuration
{
    [GeneratedRegex("^([0-9]+(ns|us|ms|s|m|h))+\\z")]
    private static partial Regex Syntax();

    /// <summary>Formats a non-negative duration, omitting zero units: 90s is <c>1m30s</c>.</summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Durations in a contract cannot be negative.");
        }

        if (value == TimeSpan.Zero)
        {
            return "0s";
        }

        long ticks = value.Ticks;
        var sb = new StringBuilder();
        Append(sb, ref ticks, TimeSpan.TicksPerHour, "h");
        Append(sb, ref ticks, TimeSpan.TicksPerMinute, "m");
        Append(sb, ref ticks, TimeSpan.TicksPerSecond, "s");
        Append(sb, ref ticks, TimeSpan.TicksPerMillisecond, "ms");
        Append(sb, ref ticks, 10, "us");
        if (ticks > 0)
        {
            sb.Append((ticks * 100).ToString(CultureInfo.InvariantCulture)).Append("ns");
        }

        return sb.ToString();
    }

    /// <summary>Parses a Go-syntax duration.</summary>
    public static TimeSpan Parse(string value) =>
        TryParse(value, out var result)
            ? result
            : throw new FormatException($"'{value}' is not a duration such as 30s, 5m or 720h.");

    /// <summary>
    /// Parses a duration as Go's <c>time.ParseDuration</c> does: an optional sign, then decimal numbers with a unit
    /// (<c>ns</c>, <c>us</c>, <c>µs</c>, <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c>), such as <c>1h2m3s4ms</c> or
    /// <c>1.5h</c>; <c>0</c> alone is zero. Precision below 100ns, which <see cref="TimeSpan"/> cannot hold, is
    /// truncated. Fails beyond Go's range of about 292 years.
    /// </summary>
    public static bool TryParse(string value, out TimeSpan result)
    {
        result = default;
        var m = GoSyntax().Match(value);
        if (!m.Success)
        {
            return false;
        }

        if (m.Groups["zero"].Success)
        {
            return true;
        }

        try
        {
            decimal nanos = 0;
            var numbers = m.Groups["n"].Captures;
            var units = m.Groups["u"].Captures;
            for (int i = 0; i < numbers.Count; i++)
            {
                var n = decimal.Parse(numbers[i].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                nanos += n * units[i].Value switch
                {
                    "h" => 3_600_000_000_000m,
                    "m" => 60_000_000_000m,
                    "s" => 1_000_000_000m,
                    "ms" => 1_000_000m,
                    "ns" => 1m,
                    _ => 1_000m, // us, µs (U+00B5), μs (U+03BC)
                };
            }

            if (nanos > long.MaxValue)
            {
                return false;
            }

            long ticks = (long)decimal.Truncate(nanos / 100);
            result = TimeSpan.FromTicks(m.Groups["sign"].Value == "-" ? -ticks : ticks);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^(?<sign>[-+]?)(?:(?<zero>0)|(?:(?<n>[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?<u>ns|us|\u00b5s|\u03bcs|ms|s|m|h))+)\z")]
    private static partial Regex GoSyntax();

    /// <summary>Whether <paramref name="value"/> is a Go-syntax duration.</summary>
    public static bool IsValid(string value) => Syntax().IsMatch(value);

    private static void Append(StringBuilder sb, ref long ticks, long unit, string suffix)
    {
        if (ticks >= unit)
        {
            sb.Append((ticks / unit).ToString(CultureInfo.InvariantCulture)).Append(suffix);
            ticks %= unit;
        }
    }
}

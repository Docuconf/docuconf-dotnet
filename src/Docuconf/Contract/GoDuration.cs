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
    [GeneratedRegex("^([0-9]+(ns|us|ms|s|m|h))+$")]
    private static partial Regex Syntax();

    [GeneratedRegex("([0-9]+)(ns|us|ms|s|m|h)")]
    private static partial Regex Part();

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
    public static TimeSpan Parse(string value)
    {
        if (!Syntax().IsMatch(value))
        {
            throw new FormatException($"'{value}' is not a duration such as 30s, 5m or 720h.");
        }

        long ticks = 0;
        foreach (Match m in Part().Matches(value))
        {
            long n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            ticks += m.Groups[2].Value switch
            {
                "h" => n * TimeSpan.TicksPerHour,
                "m" => n * TimeSpan.TicksPerMinute,
                "s" => n * TimeSpan.TicksPerSecond,
                "ms" => n * TimeSpan.TicksPerMillisecond,
                "us" => n * 10,
                _ => n / 100,
            };
        }

        return TimeSpan.FromTicks(ticks);
    }

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

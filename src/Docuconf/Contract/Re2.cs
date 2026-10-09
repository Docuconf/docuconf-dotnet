using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Docuconf.Contract;

/// <summary>
/// Matches a contract pattern (RE2, SPEC §4.3) with .NET's engine. The one difference that matters for anchored
/// patterns is <c>$</c>: .NET also matches it before a final newline, RE2 only at the end of the text, so
/// <c>^[0-9]+$</c> would accept <c>"5\n"</c> in .NET alone. Outside multi-line mode, <c>$</c> is matched as <c>\z</c>.
/// </summary>
internal static class Re2
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="pattern"/> matches anywhere in <paramref name="value"/>, as RE2 does.</summary>
    public static bool IsMatch(string pattern, string value) =>
        Cache.GetOrAdd(pattern, p => new Regex(Translate(p), RegexOptions.CultureInvariant)).IsMatch(value);

    /// <summary>The pattern with each <c>$</c> that is an end anchor written as <c>\z</c>.</summary>
    internal static string Translate(string pattern)
    {
        if (pattern.Contains("(?m", StringComparison.Ordinal) || pattern.Contains("(?im", StringComparison.Ordinal) || pattern.Contains("(?sm", StringComparison.Ordinal))
        {
            return pattern;
        }

        var sb = new StringBuilder(pattern.Length + 4);
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(c).Append(pattern[++i]);
                continue;
            }

            if (inClass)
            {
                inClass = c != ']';
                sb.Append(c);
                continue;
            }

            if (c == '[')
            {
                inClass = true;
                sb.Append(c);
                // A ] right after [ or [^ is a literal.
                if (i + 1 < pattern.Length && pattern[i + 1] == '^')
                {
                    sb.Append(pattern[++i]);
                }

                if (i + 1 < pattern.Length && pattern[i + 1] == ']')
                {
                    sb.Append(pattern[++i]);
                }

                continue;
            }

            sb.Append(c == '$' ? "\\z" : c.ToString());
        }

        return sb.ToString();
    }
}

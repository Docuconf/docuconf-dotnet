using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Docuconf.Contract;

/// <summary>
/// Writes a JSON value as the platform renders it (CUE's <c>json.Marshal</c>, SPEC §4.3): no insignificant
/// whitespace, fields in their order, and no escaping beyond what JSON requires, so <c>&lt;</c>, <c>&amp;</c>,
/// non-ASCII text and emoji stay as they are. System.Text.Json's encoders escape some of these, which would change
/// the length a json <c>maxLength</c> measures.
/// </summary>
internal static class CompactJson
{
    public static string Write(JsonNode? node)
    {
        var sb = new StringBuilder();
        Append(sb, node);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject o:
                sb.Append('{');
                bool first = true;
                foreach (var (key, value) in o)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendString(sb, key);
                    sb.Append(':');
                    Append(sb, value);
                }

                sb.Append('}');
                return;
            case JsonArray a:
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Append(sb, a[i]);
                }

                sb.Append(']');
                return;
            default:
                if (node.GetValueKind() == JsonValueKind.String) AppendString(sb, node.GetValue<string>());
                else sb.Append(node.ToJsonString()); // numbers, true, false: nothing to escape
                return;
        }
    }

    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                // Go's encoder (and so CUE's) also escapes U+2028 and U+2029.
                case < ' ' or '\u2028' or '\u2029': sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('"');
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docuconf.Runtime;

namespace Docuconf.Contract;

/// <summary>
/// A variable's value from below the environment (SPEC §4.4, §4.7): a profile default, already typed, or an overlay
/// value, as the wire string (or list items) it stands for, which is checked like an environment value.
/// </summary>
internal sealed record Layer
{
    /// <summary>A profile default.</summary>
    public object? Typed { get; init; }

    /// <summary>An overlay's scalar value as a wire string.</summary>
    public string? Raw { get; init; }

    /// <summary>An overlay's list value, item by item.</summary>
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>The overlay value was reported as a violation already.</summary>
    public bool Bad { get; init; }

    /// <summary>The overlay's name.</summary>
    public string Source { get; init; } = "";

    public static Layer Profile(object value) => new() { Typed = value };
}

/// <summary>
/// Reads a contract's config-file overlays (SPEC §4.7) in the contract-first mode: each one optional, from its path
/// under the file root, in json, yaml or toml; each variable's value at its <c>configKey</c>, split on the overlay's
/// <c>keySeparator</c>, with keys matched exactly.
/// </summary>
internal static class Overlays
{
    public static void Load(ContractModel model, string root, Dictionary<string, Layer> layers, List<Violation> violations, List<string> warnings)
    {
        var fromOverlay = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var overlay in model.Overlays.Values)
        {
            var path = root.Length == 0 ? overlay.Path : Path.Join(root, overlay.Path);
            if (!File.Exists(path))
            {
                continue; // an overlay is optional
            }

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                violations.Add(new Violation(Codes.FileUnreadable, overlay.Name, $"{path} could not be read: {ex.Message}"));
                continue;
            }

            if (!StructuredFile.TryParse(overlay.Format, data, out var document, out var error, out var detail))
            {
                violations.Add(new Violation(Codes.FileMalformed, overlay.Name, $"{path} {error}" + (detail is null ? "" : ": " + detail)));
                continue;
            }

            if (document is not JsonObject top)
            {
                violations.Add(new Violation(Codes.FileMalformed, overlay.Name, $"{path} does not hold an object at its top level"));
                continue;
            }

            foreach (var spec in model.Vars.Values)
            {
                // The selector chooses which files load, so an overlay cannot set it.
                if (spec.ConfigKey.Length == 0 || spec.Name == model.Profiles?.Selector)
                {
                    continue;
                }

                if (!TryLookup(top, spec.ConfigKey.Split(overlay.KeySeparator), out var value) || value is null)
                {
                    continue; // null is unset
                }

                if (fromOverlay.TryGetValue(spec.Name, out var first))
                {
                    warnings.Add($"docuconf: warning: {spec.Name} is set in overlays {first} and {overlay.Name}; {first} wins");
                    continue;
                }

                fromOverlay[spec.Name] = overlay.Name;
                if (spec.Secret)
                {
                    // Never printed: it is secret material in a ConfigMap.
                    violations.Add(new Violation(Codes.InvalidType, spec.Name,
                        $"is secret, but overlay {overlay.Name} sets it at {spec.ConfigKey}; supply secrets through the environment"));
                    layers[spec.Name] = new Layer { Bad = true, Source = overlay.Name };
                    continue;
                }

                if (WireValue(spec, value, out var layer) is { } problem)
                {
                    violations.Add(new Violation(Codes.InvalidType, spec.Name, $"in overlay {overlay.Name}, at {spec.ConfigKey}: {problem}"));
                    layers[spec.Name] = new Layer { Bad = true, Source = overlay.Name };
                    continue;
                }

                layers[spec.Name] = layer! with { Source = overlay.Name };
            }
        }
    }

    private static bool TryLookup(JsonObject document, string[] parts, out JsonNode? value)
    {
        JsonNode? current = document;
        foreach (var part in parts)
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
            {
                value = null;
                return false;
            }
        }

        value = current;
        return true;
    }

    /// <summary>
    /// Converts a native overlay value to the wire string it stands for (SPEC §4.7): a string as it is, a bool as
    /// <c>true</c> or <c>false</c>, a number with an integral value as an integer, any other in shortest round-trip
    /// decimal, a list item by item, and a <c>json</c> value as its compact JSON. Returns why it cannot, or null.
    /// </summary>
    private static string? WireValue(VarSpec spec, JsonNode value, out Layer? layer)
    {
        layer = null;
        if (spec.Type == VarType.Json)
        {
            layer = new Layer { Raw = CompactJson.Write(value) };
            return null;
        }

        if (spec.IsListLike)
        {
            if (value is not JsonArray array)
            {
                return $"is {KindOf(value)}, not a list";
            }

            var items = new List<string>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                if (Scalar(array[i]) is not { } item)
                {
                    return $"item {i} is {KindOf(array[i])}, not a scalar";
                }

                items.Add(item);
            }

            layer = new Layer { Items = items };
            return null;
        }

        if (Scalar(value) is not { } raw)
        {
            return $"is {KindOf(value)}, not a scalar";
        }

        layer = new Layer { Raw = raw };
        return null;
    }

    private static string? Scalar(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                return value.GetValue<string>();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Number:
                var text = value.ToJsonString();
                if (BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                {
                    return n.ToString(CultureInfo.InvariantCulture);
                }

                var d = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                return d == Math.Truncate(d) && Math.Abs(d) < 9.2233720368547758E+18
                    ? ((long)d).ToString(CultureInfo.InvariantCulture)
                    : d.ToString("R", CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "a list",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            _ => "a bool",
        },
        _ => "a value",
    };
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Docuconf.Contract;

/// <summary>
/// Generates the JSON Schema of a config file from the type the app deserializes it into, with
/// DataAnnotations constraints, so the platform checks files against the same type the app binds.
/// </summary>
public static class SchemaGenerator
{
    /// <summary>
    /// The serializer options config files are read with: camelCase names (read case-insensitively),
    /// enums as strings, and unknown properties rejected.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter() },
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>The JSON Schema for <paramref name="type"/>.</summary>
    public static JsonNode For(Type type)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        };
        return SerializerOptions.GetJsonSchemaAsNode(type, exporterOptions);
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return node;
        }

        // Object level: [Required] properties, and no properties beyond the declared ones.
        if (context.PropertyInfo is null && context.TypeInfo.Kind == JsonTypeInfoKind.Object)
        {
            var required = context.TypeInfo.Properties
                .Where(p => p.AttributeProvider?.IsDefined(typeof(RequiredAttribute), true) == true)
                .Select(p => p.Name)
                .ToList();
            if (required.Count > 0)
            {
                var existing = schema["required"] as JsonArray ?? [];
                foreach (var name in required.Where(n => !existing.Any(e => e?.GetValue<string>() == n)))
                {
                    existing.Add(name);
                }

                schema["required"] = existing;
            }

            schema["additionalProperties"] = false;
        }

        if (context.PropertyInfo?.AttributeProvider is not ICustomAttributeProvider attributes)
        {
            return schema;
        }

        T? Attr<T>() where T : Attribute => attributes.GetCustomAttributes(typeof(T), true).OfType<T>().FirstOrDefault();
        bool isArray = IsType(schema, "array");

        if (Attr<DescriptionAttribute>() is { } description)
        {
            schema["description"] = description.Description;
        }

        if (Attr<RangeAttribute>() is { } range && (IsType(schema, "integer") || IsType(schema, "number")))
        {
            // [Range(1, int.MaxValue)] says "at least 1": a bound at the type's own limit is left out, as the schema of
            // an unbounded property has none.
            var (lowest, highest) = Limits(context.PropertyInfo.PropertyType);
            var min = Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture);
            var max = Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture);
            if (min != lowest)
            {
                schema["minimum"] = JsonValue.Create(min);
            }

            if (max != highest)
            {
                schema["maximum"] = JsonValue.Create(max);
            }
        }

        void Bounds(int? min, int? max)
        {
            if (min is > 0)
            {
                schema[isArray ? "minItems" : "minLength"] = min;
            }

            if (max is > 0)
            {
                schema[isArray ? "maxItems" : "maxLength"] = max;
            }
        }

        if (Attr<StringLengthAttribute>() is { } sl)
        {
            Bounds(sl.MinimumLength, sl.MaximumLength);
        }

        if (Attr<MinLengthAttribute>() is { } minLength)
        {
            Bounds(minLength.Length, null);
        }

        if (Attr<MaxLengthAttribute>() is { } maxLength)
        {
            Bounds(null, maxLength.Length);
        }

        if (Attr<LengthAttribute>() is { } length)
        {
            Bounds(length.MinimumLength, length.MaximumLength);
        }

        if (Attr<RegularExpressionAttribute>() is { } regex)
        {
            schema["pattern"] = ContractReader.FullMatch(regex.Pattern);
        }

        if (Attr<AllowedValuesAttribute>() is { } allowed)
        {
            schema["enum"] = new JsonArray(allowed.Values.Select(v => (JsonNode?)JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture))).ToArray());
        }

        return schema;
    }

    private static (double Lowest, double Highest) Limits(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(int) ? (int.MinValue, int.MaxValue)
            : t == typeof(long) ? (long.MinValue, long.MaxValue)
            : t == typeof(short) ? (short.MinValue, short.MaxValue)
            : t == typeof(double) ? (double.MinValue, double.MaxValue)
            : t == typeof(float) ? (float.MinValue, float.MaxValue)
            : (double.NaN, double.NaN);
    }

    private static bool IsType(JsonObject schema, string type) => schema["type"] switch
    {
        JsonValue v => v.GetValue<string>() == type,
        JsonArray a => a.Any(x => x?.GetValue<string>() == type),
        _ => false,
    };
}

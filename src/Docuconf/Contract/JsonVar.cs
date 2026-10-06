using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Docuconf.Runtime;
using Microsoft.Extensions.Configuration;

namespace Docuconf.Contract;

/// <summary>Reads, writes and checks the values of <c>json</c> variables.</summary>
internal static class JsonVar
{
    private static readonly JsonSerializerOptions WriteOptions = new(SchemaGenerator.SerializerOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Binds the value at <paramref name="section"/>: a JSON string (an environment variable) is deserialized like a
    /// config file; a nested section (an appsettings file or overlay) is bound by the configuration binder, which
    /// rejects keys the type does not have. Returns null when the section holds neither.
    /// </summary>
    /// <exception cref="JsonException">The string is not JSON of the type.</exception>
    /// <exception cref="InvalidOperationException">The section does not bind to the type.</exception>
    public static object? Bind(IConfigurationSection section, Type type)
    {
        if (!string.IsNullOrEmpty(section.Value))
        {
            return JsonSerializer.Deserialize(section.Value, type, SchemaGenerator.SerializerOptions);
        }

        return section.GetChildren().Any()
            ? section.Get(type, o => o.ErrorOnUnknownConfiguration = true)
            : null;
    }

    /// <summary>The value as JSON, as the contract and the platform see it: camelCase names, nulls left out.</summary>
    public static JsonNode? ToNode(object value, Type type) => JsonSerializer.SerializeToNode(value, type, WriteOptions);

    /// <summary>DataAnnotations problems in the value, including in the items of a list.</summary>
    public static List<Annotations.Problem> Validate(object value)
    {
        if (value is IEnumerable items and not string)
        {
            var problems = new List<Annotations.Problem>();
            int i = 0;
            foreach (var item in items)
            {
                if (item is not null && item.GetType().IsClass && item is not string)
                {
                    problems.AddRange(Annotations.ValidateGraph(item, $"$[{i}]"));
                }

                i++;
            }

            return problems;
        }

        return Annotations.ValidateGraph(value, "$");
    }

    /// <summary>The first problem with a contract value (a default or an appsettings value), or null.</summary>
    public static string? Check(VarSpec spec, JsonNode node)
    {
        object? value;
        try
        {
            value = node.Deserialize(spec.ClrType!, SchemaGenerator.SerializerOptions);
        }
        catch (JsonException ex)
        {
            return $"does not match its type: {ex.Message}";
        }

        if (value is null)
        {
            return "is null";
        }

        return Validate(value) is [var first, ..] ? $"fails its schema at {first.Path}: {first.Message}" : null;
    }
}

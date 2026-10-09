using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Docuconf.Contract;

/// <summary>
/// Checks a <c>json</c> value against its variable's JSON Schema in the contract-first mode, where there is no .NET
/// type to bind it to (SPEC §4.3). JsonSchema.Net evaluates the schema as draft 2020-12, the dialect of the contract
/// meta-schema and the other SDKs.
/// </summary>
internal static class JsonSchemaCheck
{
    private static readonly ConditionalWeakTable<JsonNode, JsonSchema> Compiled = new();

    /// <summary>Why <paramref name="schema"/> is not a usable JSON Schema, or null when it is.</summary>
    public static string? Problem(JsonNode schema)
    {
        try
        {
            Compile(schema);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException or InvalidOperationException or FormatException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Why <paramref name="instance"/> does not match the schema of <paramref name="spec"/>, or null when it does. The
    /// message names the location and the failing keyword; for a secret it never quotes the schema's message, which
    /// may describe the value.
    /// </summary>
    public static string? Check(VarSpec spec, JsonNode? instance) => Check(spec.Schema!, spec.Secret, instance);

    /// <summary>Why <paramref name="instance"/> does not match <paramref name="schema"/>, or null when it does.</summary>
    public static string? Check(JsonNode schema, bool secret, JsonNode? instance)
    {
        using var document = JsonDocument.Parse(instance?.ToJsonString() ?? "null");
        var results = Compile(schema).Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (results.IsValid)
        {
            return null;
        }

        var errors = (results.Details is { Count: > 0 } details ? details : [results])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => (Location: d.InstanceLocation.ToString(), Keyword: e.Key.Length > 0 ? e.Key : Applicator(d.EvaluationPath.ToString()), Message: e.Value)))
            .ToList();
        if (errors.Count == 0)
        {
            return "does not match its schema";
        }

        // The deepest error is the most specific: a failing property rather than the object around it.
        var (location, keyword, message) = errors.OrderByDescending(e => e.Location.Length).First();
        var at = location.Length == 0 ? "the value" : location;
        return secret ? $"{at} fails {keyword}" : $"{at} fails {keyword}: {message}";
    }

    /// <summary>
    /// A <c>false</c> subschema fails with no keyword of its own; name the keyword that applied it, such as
    /// <c>additionalProperties</c>, from the evaluation path.
    /// </summary>
    private static string Applicator(string evaluationPath) =>
        evaluationPath.Split('/').LastOrDefault(Applicators.Contains) ?? "a false schema";

    private static readonly HashSet<string> Applicators =
    [
        "additionalProperties", "properties", "patternProperties", "unevaluatedProperties", "propertyNames",
        "items", "prefixItems", "unevaluatedItems", "contains", "not", "if", "then", "else", "allOf", "anyOf", "oneOf",
    ];

    /// <summary>
    /// Draft 2020-12, except that <c>minLength</c> and <c>maxLength</c> count code points, as the JSON Schema spec and
    /// the other SDKs do (SPEC §4.3); JsonSchema.Net's own count grapheme clusters.
    /// </summary>
    private static readonly BuildOptions Options = new()
    {
        Dialect = Dialect.Draft202012.With([new CodePointLength("minLength", min: true), new CodePointLength("maxLength", min: false)]),
    };

    private const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    private static JsonSchema Compile(JsonNode schema) =>
        Compiled.GetValue(schema, static s =>
        {
            // A $schema naming 2020-12 would select JsonSchema.Net's built-in dialect, and with it the grapheme count; the
            // contract's schemas are 2020-12 whether they say so or not (SPEC §4.3).
            if (s is JsonObject { } root && root["$schema"] is { } dialect)
            {
                var uri = dialect.GetValueKind() == JsonValueKind.String ? dialect.GetValue<string>().TrimEnd('#') : null;
                if (uri != Draft202012)
                {
                    throw new JsonSchemaException($"$schema must be {Draft202012} or absent, not {dialect.ToJsonString()}");
                }

                var copy = root.DeepClone().AsObject();
                copy.Remove("$schema");
                s = copy;
            }

            return JsonSchema.FromText(s.ToJsonString(), Options);
        });

    /// <summary><c>minLength</c> or <c>maxLength</c>, counting Unicode code points.</summary>
    private sealed class CodePointLength(string name, bool min) : IKeywordHandler
    {
        public string Name => name;

        public object? ValidateKeywordValue(JsonElement value) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0
                ? n
                : throw new JsonSchemaException($"{name} must be a non-negative integer");

        public void BuildSubschemas(KeywordData keyword, BuildContext context)
        {
        }

        public KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context)
        {
            if (context.Instance.ValueKind != JsonValueKind.String)
            {
                return new KeywordEvaluation { Keyword = name, IsValid = true };
            }

            long limit = (long)keyword.Value!;
            long length = Constraints.Length(context.Instance.GetString()!);
            bool valid = min ? length >= limit : length <= limit;
            return new KeywordEvaluation
            {
                Keyword = name,
                IsValid = valid,
                Error = valid ? null : $"is {length} characters, {(min ? "fewer" : "more")} than {limit}",
            };
        }
    }
}

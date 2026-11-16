using System.Text.Json.Nodes;

namespace Docuconf.Contract;

/// <summary>
/// The well-known fragments (SPEC §4.8): docuconf-go's <c>spec/fragments.json</c>, embedded. A fragment is keyed
/// name@major, such as <c>otel@1</c>, and holds the variables a runtime or library reads.
/// </summary>
internal static class Fragments
{
    private static readonly Lazy<JsonObject> Document = new(() =>
    {
        using var stream = typeof(Fragments).Assembly.GetManifestResourceStream("Docuconf.fragments.json")
            ?? throw new InvalidOperationException("docuconf: the embedded fragments.json is missing");
        return JsonNode.Parse(stream)!.AsObject();
    });

    /// <summary>The fragments' names, sorted.</summary>
    public static IReadOnlyList<string> Names => Document.Value.Select(p => p.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>A fragment's variables, by name, as contract JSON; null when there is no such fragment.</summary>
    public static JsonObject? Vars(string fragment) => Document.Value[fragment]?["vars"] as JsonObject;
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Docuconf.Contract;

/// <summary>
/// Reads a contract exported as JSON (<c>cue export contract.cue --out json</c>) into a <see cref="ContractModel"/>
/// for the contract-first mode. Reads the variables and profile defaults; file inputs and overlays are not checked in
/// that mode.
/// </summary>
internal static partial class ContractJson
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex EnvNameSyntax();

    public static ContractModel Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ContractException([$"The contract is not valid JSON: {ex.Message}"]);
        }

        if (root is not JsonObject doc)
        {
            throw new ContractException(["The contract must be a JSON object."]);
        }

        var errors = new List<string>();
        if (doc["kind"]?.GetValue<string>() is not "ConfigContract")
        {
            errors.Add("kind must be \"ConfigContract\".");
        }

        if (doc["apiVersion"]?.GetValue<string>() is not "docuconf.dev/v1alpha1")
        {
            errors.Add("apiVersion must be \"docuconf.dev/v1alpha1\".");
        }

        var model = new ContractModel { Service = doc["metadata"]?["name"]?.GetValue<string>() ?? "" };
        if (doc["vars"] is JsonObject vars)
        {
            foreach (var (name, node) in vars)
            {
                if (node is not JsonObject v)
                {
                    errors.Add($"vars.{name} must be an object.");
                    continue;
                }

                try
                {
                    if (ReadVar(name, v, errors) is { } spec)
                    {
                        model.Vars[name] = spec;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or OverflowException)
                {
                    errors.Add($"vars.{name}: {ex.Message}");
                }
            }
        }
        else if (doc["vars"] is not null)
        {
            errors.Add("vars must be an object.");
        }

        if (doc["profiles"] is JsonObject profiles)
        {
            ReadProfiles(profiles, model, errors);
        }

        if (errors.Count > 0)
        {
            throw new ContractException(errors);
        }

        return model;
    }

    /// <summary>Profile defaults (SPEC §4.4), typed like variable defaults.</summary>
    private static void ReadProfiles(JsonObject p, ContractModel model, List<string> errors)
    {
        var selector = p["selector"]?.GetValue<string>();
        if (selector is null || !model.Vars.ContainsKey(selector))
        {
            errors.Add($"profiles.selector '{selector}' must name a declared variable.");
            return;
        }

        var profiles = new ProfilesSpec { Selector = selector, Default = p["default"]?.GetValue<string>() ?? "Production" };
        foreach (var (profile, values) in p["defaults"] as JsonObject ?? [])
        {
            var defaults = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var (name, value) in values as JsonObject ?? [])
            {
                if (!model.Vars.TryGetValue(name, out var spec) || value is null)
                {
                    errors.Add($"profiles.defaults.{profile}.{name} must name a declared variable and hold a value.");
                    continue;
                }

                defaults[name] = TypedDefault(spec, value)!;
            }

            profiles.Defaults[profile] = defaults;
        }

        model.Profiles = profiles;
    }

    private static VarSpec? ReadVar(string name, JsonObject v, List<string> errors)
    {
        int before = errors.Count;
        void Error(string message) => errors.Add($"vars.{name}: {message}");

        if (!EnvNameSyntax().IsMatch(name))
        {
            Error("the name must be UPPER_SNAKE_CASE.");
        }

        var typeName = v["type"]?.GetValue<string>();
        VarType? type = typeName switch
        {
            "string" => VarType.String,
            "int" => VarType.Int,
            "float" => VarType.Float,
            "bool" => VarType.Bool,
            "duration" => VarType.Duration,
            "url" => VarType.Url,
            "enum" => VarType.Enum,
            "list" => VarType.List,
            "json" => VarType.Json,
            _ => null,
        };
        if (type is null)
        {
            Error($"unknown type '{typeName}'.");
            return null;
        }

        string? encoding = v["encoding"]?.GetValue<string>();
        string? items = v["items"]?.GetValue<string>();
        if (type == VarType.Duration)
        {
            encoding ??= "go";
            if (!WireFormat.DurationEncodings.Contains(encoding))
            {
                Error($"unknown duration encoding '{encoding}'.");
            }
        }
        else if (type == VarType.List)
        {
            encoding ??= "csv";
            if (!WireFormat.ListEncodings.Contains(encoding))
            {
                Error($"unknown list encoding '{encoding}'.");
            }

            if (items is not ("string" or "int"))
            {
                Error("items must be \"string\" or \"int\".");
            }
        }

        string? separator = v["separator"]?.GetValue<string>();
        if (separator is { Length: 0 })
        {
            Error("separator must not be empty.");
        }

        var values = v["values"] is JsonArray vs ? vs.Select(x => x!.GetValue<string>()).ToList() : null;
        if (type == VarType.Enum && values is not { Count: > 0 })
        {
            Error("an enum needs values.");
        }

        if (errors.Count > before)
        {
            return null;
        }

        var spec = new VarSpec
        {
            Name = name,
            ConfigKey = v["configKey"]?.GetValue<string>() ?? "",
            Type = type.Value,
            Description = v["description"]?.GetValue<string>() ?? "",
            Required = v["required"]?.GetValue<bool>() ?? false,
            Secret = v["secret"]?.GetValue<bool>() ?? false,
            Min = Bound(type.Value, v["min"]),
            Max = Bound(type.Value, v["max"]),
            MinLength = v["minLength"]?.GetValue<int>(),
            MaxLength = v["maxLength"]?.GetValue<int>(),
            Pattern = v["pattern"]?.GetValue<string>(),
            Schemes = v["schemes"] is JsonArray ss ? ss.Select(x => x!.GetValue<string>()).ToList() : null,
            Values = values,
            Items = items,
            MinItems = v["minItems"]?.GetValue<int>(),
            MaxItems = v["maxItems"]?.GetValue<int>(),
            ItemMin = v["itemMin"]?.GetValue<long>(),
            ItemMax = v["itemMax"]?.GetValue<long>(),
            ItemMinLength = v["itemMinLength"]?.GetValue<int>(),
            ItemMaxLength = v["itemMaxLength"]?.GetValue<int>(),
            Encoding = encoding,
            Separator = type == VarType.List && encoding == "csv" ? separator ?? "," : null,
            Schema = v["schema"]?.DeepClone(),
        };

        if ((spec.ItemMinLength is not null || spec.ItemMaxLength is not null) && spec.Items != "string")
        {
            Error("itemMinLength and itemMaxLength apply only to lists of strings.");
            return null;
        }

        if (spec.Pattern is not null)
        {
            try
            {
                _ = new Regex(spec.Pattern);
            }
            catch (ArgumentException ex)
            {
                Error($"pattern '{spec.Pattern}' does not compile: {ex.Message}");
                return null;
            }
        }

        if (v["default"] is { } def)
        {
            spec.Default = TypedDefault(spec, def);
        }

        return spec;
    }

    private static object? Bound(VarType type, JsonNode? node) => node is null ? null : type switch
    {
        VarType.Int => node.GetValue<long>(),
        VarType.Float => node.GetValue<double>(),
        VarType.Duration => GoDuration.Format(GoDuration.Parse(node.GetValue<string>())),
        _ => null,
    };

    /// <summary>A default in the typed form <see cref="Constraints.Check"/> takes.</summary>
    private static object? TypedDefault(VarSpec spec, JsonNode def) => spec.Type switch
    {
        VarType.Int => def.GetValue<long>(),
        VarType.Float => def.GetValue<double>(),
        VarType.Bool => def.GetValue<bool>(),
        VarType.Duration => GoDuration.Format(GoDuration.Parse(def.GetValue<string>())),
        VarType.List => def.AsArray().Select(i => spec.Items == "int" ? (object)i!.GetValue<long>() : i!.GetValue<string>()).ToList(),
        VarType.Json => def.DeepClone(),
        _ => def.GetValue<string>(),
    };
}

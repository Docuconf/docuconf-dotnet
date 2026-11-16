using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Docuconf.Contract;

/// <summary>
/// Reads a contract exported as JSON (<c>cue export contract.cue --out json</c>) into a <see cref="ContractModel"/>
/// for the contract-first mode: variables, file inputs, profiles and overlays.
/// </summary>
internal static partial class ContractJson
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]*\\z")]
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

        if (doc["files"] is JsonObject files)
        {
            foreach (var (name, node) in files)
            {
                try
                {
                    if (node is not JsonObject f)
                    {
                        errors.Add($"files.{name} must be an object.");
                    }
                    else if (ReadFile(name, f, model, errors) is { } spec)
                    {
                        model.Files[name] = spec;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or OverflowException)
                {
                    errors.Add($"files.{name}: {ex.Message}");
                }
            }
        }
        else if (doc["files"] is not null)
        {
            errors.Add("files must be an object.");
        }

        if (doc["overlays"] is JsonObject overlays)
        {
            foreach (var (name, node) in overlays)
            {
                try
                {
                    if (ReadOverlay(name, node as JsonObject, errors) is { } overlay)
                    {
                        model.Overlays[name] = overlay;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
                {
                    errors.Add($"overlays.{name}: {ex.Message}");
                }
            }
        }
        else if (doc["overlays"] is not null)
        {
            errors.Add("overlays must be an object.");
        }

        foreach (var spec in model.Vars.Values)
        {
            if (spec.Deprecated?.ReplacedBy is { } by && (by == spec.Name || !(model.Vars.ContainsKey(by) || model.Files.ContainsKey(by))))
            {
                errors.Add($"vars.{spec.Name}: deprecated.replacedBy '{by}' must name another input.");
            }
        }

        if (doc["profiles"] is JsonObject profiles)
        {
            ReadProfiles(profiles, model, errors);
        }

        ReadIncludes(doc["includes"], model, errors);
        model.Roles = ReadRoles(doc["roles"], m => errors.Add($"roles: {m}"));
        CheckRoles(model, errors);
        CheckConditions(model, errors);

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

                if (spec.Secret)
                {
                    errors.Add($"profiles.defaults.{profile}.{name}: a secret has no value in a config file.");
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
            "keySet" => VarType.KeySet,
            _ => null,
        };
        if (type is null)
        {
            Error($"unknown type '{typeName}'.");
            return null;
        }

        // details is documentation only (SPEC §4.2): checked as the meta-schema does, never used at runtime.
        string? details = null;
        if (v["details"] is { } detailsNode)
        {
            details = detailsNode.GetValueKind() == System.Text.Json.JsonValueKind.String ? detailsNode.GetValue<string>() : null;
            if (details is null)
            {
                Error("details must be a string.");
            }
            else if (XmlDocs.DetailsProblem(details) is { } problem)
            {
                Error(problem + ".");
            }
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
        else if (type is VarType.List or VarType.KeySet)
        {
            encoding ??= "csv";
            if (!WireFormat.ListEncodings.Contains(encoding))
            {
                Error($"unknown list encoding '{encoding}'.");
            }

            if (type == VarType.List && items is not ("string" or "int"))
            {
                Error("items must be \"string\" or \"int\".");
            }
        }

        if (type == VarType.KeySet && v["secret"]?.GetValue<bool>() != true)
        {
            Error("a keySet is always secret: set secret: true.");
        }

        int? minKeys = v["minKeys"]?.GetValue<int>(), maxKeys = v["maxKeys"]?.GetValue<int>();
        int? keyMinLength = v["keyMinLength"]?.GetValue<int>(), keyMaxLength = v["keyMaxLength"]?.GetValue<int>();
        if (type == VarType.KeySet && ((minKeys ?? 1) < 1 || (maxKeys ?? 2) < (minKeys ?? 1) || keyMinLength < 0 || keyMaxLength < Math.Max(1, keyMinLength ?? 0)))
        {
            Error("minKeys must be at least 1, maxKeys at least minKeys, and keyMaxLength at least 1 and at least keyMinLength.");
        }

        var deprecated = ReadDeprecated(v["deprecated"], Error);
        bool required = v["required"]?.GetValue<bool>() ?? false;
        if (deprecated is not null && required)
        {
            Error("a required variable cannot be deprecated: the platform could not stop setting it.");
        }

        if (deprecated is not null && v["requiredIf"] is not null)
        {
            Error("a conditionally required variable cannot be deprecated: the platform could not stop setting it.");
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

        if (v["schema"] is { } schemaNode && JsonSchemaCheck.Problem(schemaNode) is { } badSchema)
        {
            Error($"schema is not a valid JSON Schema: {badSchema}");
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
            Details = details,
            Required = required,
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
            Separator = type is VarType.List or VarType.KeySet && encoding == "csv" ? separator ?? "," : null,
            Schema = v["schema"]?.DeepClone(),
            MinKeys = type == VarType.KeySet ? minKeys ?? 1 : null,
            MaxKeys = type == VarType.KeySet ? maxKeys ?? 2 : null,
            KeyMinLength = keyMinLength,
            KeyMaxLength = keyMaxLength,
            Deprecated = deprecated,
            RequiredIf = ReadRequiredIf(v["requiredIf"], Error),
            Roles = ReadRoles(v["roles"], Error),
            Group = v["group"]?.GetValue<string>(),
            Examples = v["examples"] is JsonArray exampleList ? exampleList.Select(x => x!.GetValue<string>()).ToList() : null,
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

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?\\z")]
    private static partial Regex RoleSyntax();

    [GeneratedRegex("^[a-z][a-z0-9-]*@[1-9][0-9]*\\z")]
    private static partial Regex FragmentSyntax();

    /// <summary>The well-known fragments a contract includes (SPEC §4.8): their variables are inline in vars.</summary>
    private static void ReadIncludes(JsonNode? node, ContractModel model, List<string> errors)
    {
        if (node is null)
        {
            return;
        }

        if (node is not JsonArray list || list.Any(x => x?.GetValueKind() != JsonValueKind.String))
        {
            errors.Add("includes must be a list of fragment names, such as otel@1.");
            return;
        }

        foreach (var name in list.Select(x => x!.GetValue<string>()))
        {
            if (!FragmentSyntax().IsMatch(name))
            {
                errors.Add($"includes: '{name}' is not a fragment name, name@major.");
            }
            else if (model.Includes.Contains(name))
            {
                errors.Add($"includes: {name} is listed twice.");
            }
            else
            {
                model.Includes.Add(name);
            }
        }
    }

    /// <summary>A list of roles (SPEC §4.9): DNS labels, at least one, none twice.</summary>
    private static List<string>? ReadRoles(JsonNode? node, Action<string> error)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray list || list.Count == 0 || list.Any(x => x?.GetValueKind() != JsonValueKind.String))
        {
            error("roles must be a list of at least one role name.");
            return null;
        }

        var roles = list.Select(x => x!.GetValue<string>()).ToList();
        for (int i = 0; i < roles.Count; i++)
        {
            if (!RoleSyntax().IsMatch(roles[i]))
            {
                error($"role '{roles[i]}' must be a DNS label.");
            }
            else if (roles.IndexOf(roles[i]) < i)
            {
                error($"role {roles[i]} is listed twice.");
            }
        }

        return roles;
    }

    /// <summary>
    /// The inputs' roles against the contract's (SPEC §4.9), and DOCUCONF_ROLE: a required enum of exactly the roles.
    /// </summary>
    private static void CheckRoles(ContractModel model, List<string> errors)
    {
        var inputs = model.Vars.Values.Select(v => (What: $"vars.{v.Name}", v.Roles))
            .Concat(model.Files.Values.Select(f => (What: $"files.{f.Name}", f.Roles)));
        foreach (var (what, used) in inputs)
        {
            foreach (var r in used ?? [])
            {
                if (model.Roles is null)
                {
                    errors.Add($"{what}: has roles, but the contract declares none.");
                    break;
                }

                if (!model.Roles.Contains(r))
                {
                    errors.Add($"{what}: role {r} is not one of the contract's roles, {string.Join(", ", model.Roles)}.");
                }
            }
        }

        if (model.Roles is null)
        {
            return;
        }

        if (!model.Vars.TryGetValue(DocuconfContract.RoleVar, out var role)
            || role is not { Type: VarType.Enum, Required: true, Roles: null }
            || !role.Values!.SequenceEqual(model.Roles))
        {
            errors.Add($"vars.{DocuconfContract.RoleVar}: a contract with roles declares it as a required enum whose values are the roles, {string.Join(", ", model.Roles)}, used by every role.");
        }
    }

    /// <summary>
    /// Each input's requiredIf against the variables (SPEC §4.2.1): the condition names another variable, a plain
    /// string, int, bool or enum one, with a value it can hold; the input is optional, with no default.
    /// </summary>
    private static void CheckConditions(ContractModel model, List<string> errors)
    {
        var inputs = model.Vars.Values.Select(v => (What: $"vars.{v.Name}", Name: v.Name, v.RequiredIf, v.Required, HasDefault: v.Default is not null))
            .Concat(model.Files.Values.Select(f => (What: $"files.{f.Name}", Name: f.Name, f.RequiredIf, f.Required, HasDefault: false)));
        foreach (var (what, name, condition, required, hasDefault) in inputs)
        {
            if (condition is null)
            {
                continue;
            }

            if (required || hasDefault)
            {
                errors.Add($"{what}: an input with requiredIf is neither required nor has a default.");
            }

            if (condition.Var == name || !model.Vars.TryGetValue(condition.Var, out var on))
            {
                errors.Add($"{what}: requiredIf must name another declared variable, not '{condition.Var}'.");
                continue;
            }

            if (on.Secret || on.Type is not (VarType.String or VarType.Int or VarType.Bool or VarType.Enum))
            {
                errors.Add($"{what}: requiredIf names {on.Name}, which is not a plain string, int, bool or enum variable.");
                continue;
            }

            bool sameType = on.Type switch
            {
                VarType.Int => condition.Value is long,
                VarType.Bool => condition.Value is bool,
                _ => condition.Value is string,
            };
            var problem = sameType ? Constraints.Check(on, condition.Value) : null;
            if (!sameType || problem is not null)
            {
                errors.Add($"{what}: requiredIf: {condition} is not a value {on.Name} can have{(problem is null ? "" : ": " + problem.Message)}.");
            }
        }
    }

    /// <summary><c>requiredIf: {VAR: value}</c> (SPEC §4.2.1): one variable and a string, integer or boolean.</summary>
    private static RequiredIfSpec? ReadRequiredIf(JsonNode? node, Action<string> error)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject o || o.Count != 1)
        {
            error("requiredIf must name one variable and its value: {VAR: value}.");
            return null;
        }

        var (name, value) = o.First();
        object? typed = value?.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number when long.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l) => l,
            _ => null,
        };
        if (!EnvNameSyntax().IsMatch(name) || typed is null)
        {
            error("requiredIf must name one variable and a string, integer or boolean value.");
            return null;
        }

        return new RequiredIfSpec(name, typed);
    }

    /// <summary><c>deprecated: {message, replacedBy?}</c> (SPEC §4.2).</summary>
    private static DeprecationSpec? ReadDeprecated(JsonNode? node, Action<string> error)
    {
        if (node is null)
        {
            return null;
        }

        if (node is not JsonObject d)
        {
            error("deprecated must be an object: {message, replacedBy?}.");
            return null;
        }

        var message = d["message"]?.GetValue<string>();
        var replacedBy = d["replacedBy"]?.GetValue<string>();
        if (ContractReader.DeprecationProblem(message, replacedBy) is { } problem)
        {
            error($"deprecated {problem}.");
            return null;
        }

        return new DeprecationSpec(message!, replacedBy);
    }

    private static readonly string[] FileTypes = ["config", "tls", "caBundle", "keystore", "text", "binary"];

    /// <summary>A file input (SPEC §4.6).</summary>
    private static FileSpec? ReadFile(string name, JsonObject f, ContractModel model, List<string> errors)
    {
        int before = errors.Count;
        void Error(string message) => errors.Add($"files.{name}: {message}");

        var typeName = f["type"]?.GetValue<string>();
        FileType? type = typeName switch
        {
            "config" => FileType.Config,
            "tls" => FileType.Tls,
            "caBundle" => FileType.CaBundle,
            "keystore" => FileType.Keystore,
            "text" => FileType.Text,
            "binary" => FileType.Binary,
            _ => null,
        };
        if (type is null)
        {
            Error($"unknown type '{typeName}'; one of {string.Join(", ", FileTypes)}.");
            return null;
        }

        var path = f["path"]?.GetValue<string>();
        if (path is null || !path.StartsWith('/') || path.Contains("//", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal) || (path.Length > 1 && path.EndsWith('/')))
        {
            Error($"path '{path}' must be absolute and normalised.");
        }

        var format = f["format"]?.GetValue<string>();
        if (type == FileType.Config && format is not ("json" or "yaml" or "toml"))
        {
            Error("a config file's format must be json, yaml or toml.");
        }

        if (type == FileType.Keystore && (format ?? "pkcs12") != "pkcs12")
        {
            Error($"keystore format '{format}' is not supported: this SDK reads pkcs12 keystores only.");
        }

        var reload = f["reload"]?.GetValue<string>() ?? "restart";
        if (reload is not ("restart" or "watch"))
        {
            Error("reload must be restart or watch.");
        }

        var passwordVar = f["passwordVar"]?.GetValue<string>();
        if (passwordVar is not null && (!model.Vars.TryGetValue(passwordVar, out var password) || !password.Secret))
        {
            Error($"passwordVar '{passwordVar}' must name a declared secret variable.");
        }

        if (f["schema"] is { } schemaNode && JsonSchemaCheck.Problem(schemaNode) is { } badSchema)
        {
            Error($"schema is not a valid JSON Schema: {badSchema}");
        }

        string? pattern = f["pattern"]?.GetValue<string>();
        if (pattern is not null)
        {
            try
            {
                _ = new Regex(pattern);
            }
            catch (ArgumentException ex)
            {
                Error($"pattern '{pattern}' does not compile: {ex.Message}");
            }
        }

        string? minRemaining = f["minRemaining"]?.GetValue<string>();
        if (minRemaining is not null && !GoDuration.TryParse(minRemaining, out _))
        {
            Error($"minRemaining '{minRemaining}' is not a duration.");
        }

        var deprecated = ReadDeprecated(f["deprecated"], Error);
        bool required = f["required"]?.GetValue<bool>() ?? false;
        if (deprecated is not null && required)
        {
            Error("a required input cannot be deprecated: the platform could not stop setting it.");
        }

        if (deprecated is not null && f["requiredIf"] is not null)
        {
            Error("a conditionally required input cannot be deprecated: the platform could not stop setting it.");
        }

        if (errors.Count > before)
        {
            return null;
        }

        return new FileSpec
        {
            Name = name,
            Type = type.Value,
            Description = f["description"]?.GetValue<string>() ?? "",
            Details = f["details"]?.GetValue<string>(),
            Required = required,
            Secret = type is FileType.Tls or FileType.Keystore || (f["secret"]?.GetValue<bool>() ?? false),
            Path = path!,
            PathEnv = f["pathEnv"]?.GetValue<string>(),
            Reload = reload == "watch" ? Reload.Watch : Reload.Restart,
            MaxSize = f["maxSize"]?.GetValue<long>(),
            Format = type == FileType.Keystore ? "pkcs12" : format,
            Schema = f["schema"]?.DeepClone(),
            DnsNames = f["dnsNames"] is JsonArray dns ? dns.Select(x => x!.GetValue<string>()).ToList() : null,
            KeyAlgorithms = f["keyAlgorithms"] is JsonArray algorithms ? algorithms.Select(x => x!.GetValue<string>()).ToList() : null,
            MinRemaining = minRemaining,
            RequireCA = f["requireCA"]?.GetValue<bool>() ?? false,
            MinCertificates = f["minCertificates"]?.GetValue<int>(),
            PasswordVar = passwordVar,
            Pattern = pattern,
            MinLength = f["minLength"]?.GetValue<int>(),
            MaxLength = f["maxLength"]?.GetValue<int>(),
            Deprecated = deprecated,
            RequiredIf = ReadRequiredIf(f["requiredIf"], Error),
            Roles = ReadRoles(f["roles"], Error),
            Group = f["group"]?.GetValue<string>(),
        };
    }

    /// <summary>A config-file overlay (SPEC §4.7).</summary>
    private static OverlaySpec? ReadOverlay(string name, JsonObject? o, List<string> errors)
    {
        int before = errors.Count;
        void Error(string message) => errors.Add($"overlays.{name}: {message}");
        if (o is null)
        {
            Error("must be an object.");
            return null;
        }

        var format = o["format"]?.GetValue<string>();
        if (format is not ("json" or "yaml" or "toml"))
        {
            Error("format must be json, yaml or toml.");
        }

        var path = o["path"]?.GetValue<string>();
        if (path is null || !path.StartsWith('/') || path.Contains("//", StringComparison.Ordinal) || path.Contains("/../", StringComparison.Ordinal) || path.EndsWith('/'))
        {
            Error($"path '{path}' must be absolute and normalised.");
        }

        var separator = o["keySeparator"]?.GetValue<string>();
        if (separator is not (":" or "."))
        {
            Error("keySeparator must be \":\" or \".\".");
        }

        var reload = o["reload"]?.GetValue<string>() ?? "restart";
        if (reload is not ("restart" or "watch"))
        {
            Error("reload must be restart or watch.");
        }

        return errors.Count > before
            ? null
            : new OverlaySpec(name, path!, reload == "watch", o["description"]?.GetValue<string>()) { Format = format!, KeySeparator = separator! };
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
        VarType.KeySet => throw new FormatException("a keySet is secret and has no default"),
        VarType.Json => def.DeepClone(),
        _ => def.GetValue<string>(),
    };
}

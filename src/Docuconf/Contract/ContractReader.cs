using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Docuconf.Contract;

/// <summary>Settings for reading a contract.</summary>
public sealed class ContractReadSettings
{
    /// <summary>
    /// The directory holding the <c>appsettings*.json</c> files the app ships with, usually the publish output.
    /// When null, appsettings files are not read.
    /// </summary>
    public string? ContentRoot { get; init; }

    /// <summary>
    /// The variable that selects <c>appsettings.{Environment}.json</c>. Defaults to <c>ASPNETCORE_ENVIRONMENT</c>
    /// for ASP.NET Core apps and <c>DOTNET_ENVIRONMENT</c> otherwise.
    /// </summary>
    public string? ProfileSelector { get; init; }
}

/// <summary>Reads <see cref="ConfigContractAttribute"/> options classes into a <see cref="ContractModel"/>.</summary>
public static partial class ContractReader
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex EnvNameSyntax();

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$")]
    private static partial Regex ServiceSyntax();

    [GeneratedRegex("^[a-z]([-a-z0-9]{0,40}[a-z0-9])?$")]
    private static partial Regex InputNameSyntax();

    // Regex features .NET has and RE2 lacks: lookaround, backreferences, atomic groups, possessive quantifiers.
    [GeneratedRegex(@"\(\?[=!<>]|\(\?<[=!]|\\[1-9]|\\k<|\(\?>|[*+?}]\+")]
    private static partial Regex NonRe2();

    /// <summary>The <see cref="ConfigOverlayAttribute"/>s on the given options classes and their assemblies.</summary>
    internal static IReadOnlyList<ConfigOverlayAttribute> OverlaysOf(IEnumerable<Type> types) =>
        types.SelectMany(t => t.GetCustomAttributes<ConfigOverlayAttribute>())
            .Concat(types.Select(t => t.Assembly).Distinct().SelectMany(a => a.GetCustomAttributes<ConfigOverlayAttribute>()))
            .ToList();

    private static void ReadOverlays(IEnumerable<Type> types, ContractModel model, List<string> errors)
    {
        foreach (var o in OverlaysOf(types))
        {
            var where = $"[ConfigOverlay(\"{o.Name}\")]";
            if (!InputNameSyntax().IsMatch(o.Name))
            {
                errors.Add($"{where}: the name must be a DNS label of lowercase letters, digits and hyphens, starting with a letter.");
            }

            if (!o.Path.StartsWith('/') || o.Path.Contains("/../", StringComparison.Ordinal) || o.Path.Contains("//", StringComparison.Ordinal) || o.Path.EndsWith('/'))
            {
                errors.Add($"{where}: path '{o.Path}' must be an absolute, normalised path to a file.");
            }
            else if (!o.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{where}: path '{o.Path}' must be a .json file; overlays are loaded as JSON appsettings.");
            }

            if (o.Description is { Length: < 5 })
            {
                errors.Add($"{where}: the description must be at least 5 characters.");
            }

            var spec = new OverlaySpec(o.Name, o.Path, o.ReloadOnChange, o.Description);
            if (model.Overlays.TryGetValue(o.Name, out var existing) && existing != spec)
            {
                errors.Add($"{where} is declared twice, differently.");
            }

            model.Overlays[o.Name] = spec;
        }

        var byDir = model.Overlays.Values.GroupBy(o => System.IO.Path.GetDirectoryName(o.Path)).Where(g => g.Count() > 1);
        foreach (var group in byDir)
        {
            errors.Add($"Overlays {string.Join(", ", group.Select(o => o.Name))} share the directory {group.Key}; each needs its own, because the platform mounts the directory.");
        }
    }

    /// <summary>Reads every <see cref="ConfigContractAttribute"/> class in <paramref name="assembly"/>.</summary>
    public static ContractModel Read(Assembly assembly, ContractReadSettings? settings = null)
    {
        var types = assembly.GetTypes().Where(t => t.GetCustomAttribute<ConfigContractAttribute>() is not null).ToList();
        if (types.Count == 0)
        {
            throw new ContractException([$"No class in {assembly.GetName().Name} has [ConfigContract]."]);
        }

        return Read(types, settings);
    }

    /// <summary>Reads the given options classes, which must all have <see cref="ConfigContractAttribute"/>.</summary>
    public static ContractModel Read(IEnumerable<Type> optionsTypes, ContractReadSettings? settings = null)
    {
        settings ??= new ContractReadSettings();
        var types = optionsTypes.ToList();
        var errors = new List<string>();

        var services = types
            .Select(t => t.GetCustomAttribute<ConfigContractAttribute>()?.Service ?? throw new ArgumentException($"{t.Name} has no [ConfigContract]."))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (services.Count > 1)
        {
            errors.Add($"All [ConfigContract] classes must name the same service; found {string.Join(", ", services)}.");
        }

        if (!ServiceSyntax().IsMatch(services[0]))
        {
            errors.Add($"Service name '{services[0]}' must be a DNS label: lowercase letters, digits and hyphens.");
        }

        var model = new ContractModel { Service = services[0] };
        var pending = new List<(FileSpec Pending, string Owner, string PasswordKey, Type DeclaringType)>();
        foreach (var type in types)
        {
            var attr = type.GetCustomAttribute<ConfigContractAttribute>()!;
            object? instance = TryCreate(type, errors);
            ReadObject(type, instance, attr.Section, [], model, errors, pending);
        }

        ResolveKeystorePasswords(model, pending, errors);
        ReadOverlays(types, model, errors);

        if (settings.ContentRoot is not null)
        {
            var selector = settings.ProfileSelector ?? DefaultSelector(types);
            AppSettingsReader.Apply(model, settings.ContentRoot, selector, errors);
        }

        if (errors.Count > 0)
        {
            throw new ContractException(errors);
        }

        return model;
    }

    private static string DefaultSelector(IEnumerable<Type> types) =>
        types.Any(t => t.Assembly.GetReferencedAssemblies().Any(a => a.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true))
            ? "ASPNETCORE_ENVIRONMENT"
            : "DOTNET_ENVIRONMENT";

    private static object? TryCreate(Type type, List<string> errors)
    {
        try
        {
            return Activator.CreateInstance(type);
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or MemberAccessException)
        {
            errors.Add($"{type.Name} needs a public parameterless constructor so its defaults can be read.");
            return null;
        }
    }

    private static void ReadObject(
        Type type,
        object? instance,
        string section,
        IReadOnlyList<PropertyInfo> path,
        ContractModel model,
        List<string> errors,
        List<(FileSpec, string, string, Type)> pending)
    {
        CheckSecretsNotPrinted(type, errors);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || prop.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            var key = section.Length == 0 ? prop.Name : section + ":" + prop.Name;
            var propPath = path.Append(prop).ToList();

            if (prop.GetCustomAttribute<ExternalAttribute>() is not null)
            {
                model.Unmodeled.Add((key, propPath));
                continue;
            }

            if (prop.GetCustomAttribute<FileInputAttribute>() is { } fileAttr)
            {
                ReadFile(prop, fileAttr, key, propPath, model, errors, pending);
                continue;
            }

            var clr = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (prop.GetCustomAttribute<JsonVarAttribute>() is not null)
            {
                if (IsScalar(clr) || clr.IsValueType)
                {
                    errors.Add($"{key}: a [JsonVar] property must be a class, list or dictionary the JSON value deserializes into; {clr.Name} is a plain variable already.");
                    continue;
                }

                ReadVar(prop, clr, instance, key, propPath, model, errors, json: true);
            }
            else if (IsScalar(clr) || ListItemKind(clr) is not null)
            {
                ReadVar(prop, clr, instance, key, propPath, model, errors);
            }
            else if (IsComplex(clr))
            {
                object? child = instance is null ? null : prop.GetValue(instance) ?? TryCreate(clr, errors);
                ReadObject(clr, child, key, propPath, model, errors, pending);
            }
            else
            {
                // The binder would still read it (from appsettings or KEY__SUB environment variables) behind the
                // contract's back, so the app has to say which it is.
                errors.Add($"{key}: {Describe(clr)} cannot be described by the contract, but the app would still read it. Mark it [JsonVar] to make it one JSON variable, or [External(\"appsettings\")] to keep it out of the contract.");
            }
        }
    }

    private static void ReadVar(
        PropertyInfo prop, Type clr, object? instance, string key, List<PropertyInfo> path, ContractModel model, List<string> errors, bool json = false)
    {
        var name = prop.GetCustomAttribute<EnvNameAttribute>()?.Name ?? key.Replace(":", "__", StringComparison.Ordinal).ToUpperInvariant();
        if (!EnvNameSyntax().IsMatch(name))
        {
            errors.Add($"{key}: environment variable name '{name}' must be UPPER_SNAKE_CASE.");
            return;
        }

        if (model.Vars.ContainsKey(name))
        {
            errors.Add($"{key}: environment variable {name} is declared twice.");
            return;
        }

        var description = DescriptionOf(prop, key, errors);
        bool secret = prop.GetCustomAttribute<SecretAttribute>() is not null;
        bool required = prop.GetCustomAttribute<RequiredAttribute>() is not null;
        var listKind = ListItemKind(clr);

        VarType type;
        IReadOnlyList<string>? values = null;
        IReadOnlyList<string>? schemes = prop.GetCustomAttribute<UrlSchemesAttribute>()?.Schemes;
        if (json)
        {
            type = VarType.Json;
            listKind = null;
        }
        else if (listKind is not null)
        {
            type = VarType.List;
        }
        else if (clr == typeof(string))
        {
            if (schemes is not null || prop.GetCustomAttribute<UrlAttribute>() is not null)
            {
                type = VarType.Url;
            }
            else if (prop.GetCustomAttribute<AllowedValuesAttribute>() is { } allowed)
            {
                type = VarType.Enum;
                values = allowed.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").ToList();
            }
            else
            {
                type = VarType.String;
            }
        }
        else if (clr == typeof(Uri))
        {
            type = VarType.Url;
        }
        else if (clr.IsEnum)
        {
            type = VarType.Enum;
            values = Enum.GetNames(clr);
        }
        else if (clr == typeof(bool))
        {
            type = VarType.Bool;
        }
        else if (clr == typeof(TimeSpan))
        {
            type = VarType.Duration;
        }
        else if (IsInteger(clr))
        {
            type = VarType.Int;
        }
        else
        {
            type = VarType.Float;
        }

        CheckConstraintsFit(prop, clr, type, key, errors);

        object? min = null, max = null;
        if (prop.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            (min, max) = type switch
            {
                VarType.Int => ((object?)Convert.ToInt64(range.Minimum, CultureInfo.InvariantCulture), (object?)Convert.ToInt64(range.Maximum, CultureInfo.InvariantCulture)),
                VarType.Float => (Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture), Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture)),
                VarType.Duration => (GoDuration.Format(ToTimeSpan(range.Minimum)), GoDuration.Format(ToTimeSpan(range.Maximum))),
                _ => (null, null),
            };
        }

        if (type == VarType.Int)
        {
            // A field narrower than 64 bits exports its own range, so the platform never sends a value it cannot hold.
            (min, max) = Narrow((long?)min, (long?)max, WireFormat.RangeOf(clr));
        }

        long? itemMin = null, itemMax = null;
        var itemRange = prop.GetCustomAttribute<ItemRangeAttribute>();
        if (itemRange is not null && listKind != "int")
        {
            errors.Add($"{key}: [ItemRange] applies to lists of integers, such as int[] or List<long>.");
        }
        else if (itemRange is not null && itemRange.Minimum > itemRange.Maximum)
        {
            errors.Add($"{key}: [ItemRange({itemRange.Minimum}, {itemRange.Maximum})] has its minimum above its maximum.");
        }

        if (listKind == "int")
        {
            (itemMin, itemMax) = Narrow(itemRange?.Minimum, itemRange?.Maximum, WireFormat.RangeOf(ElementType(clr)!));
        }

        int? itemMinLength = null, itemMaxLength = null;
        if (prop.GetCustomAttribute<ItemLengthAttribute>() is { } itemLength)
        {
            if (listKind != "string")
            {
                errors.Add($"{key}: [ItemLength] applies to lists of strings, such as string[] or List<string>.");
            }
            else if (itemLength.MinimumLength < 0 || itemLength.MaximumLength < 0)
            {
                errors.Add($"{key}: [ItemLength] lengths must not be negative.");
            }
            else if (itemLength.MinimumLength > itemLength.MaximumLength)
            {
                errors.Add($"{key}: [ItemLength({itemLength.MinimumLength}, {itemLength.MaximumLength})] has its minimum above its maximum.");
            }
            else
            {
                itemMinLength = itemLength.MinimumLength > 0 ? itemLength.MinimumLength : null;
                itemMaxLength = itemLength.MaximumLength;
            }
        }

        // A json value's maxLength comes from [JsonVar(MaxLength = n)]: DataAnnotations' length attributes do not
        // apply to an object.
        var (minLength, maxLength) = type == VarType.Json
            ? (null, prop.GetCustomAttribute<JsonVarAttribute>()!.MaxLength is > 0 and var jsonMax ? jsonMax : (int?)null)
            : LengthBounds(prop);
        if (type == VarType.Url && minLength is not null)
        {
            errors.Add($"{key}: a url takes only a maximum length ([MaxLength] or [StringLength]); remove the minimum.");
            minLength = null;
        }

        if (type == VarType.Json && prop.GetCustomAttribute<JsonVarAttribute>()!.MaxLength < 0)
        {
            errors.Add($"{key}: [JsonVar(MaxLength)] must not be negative.");
        }
        string? pattern = FullMatch(prop.GetCustomAttribute<RegularExpressionAttribute>()?.Pattern);
        if (pattern is not null && NonRe2().IsMatch(pattern))
        {
            errors.Add($"{key}: pattern '{pattern}' uses .NET-only regex features (lookaround, backreferences, atomic groups). The platform and other SDKs need RE2.");
        }

        var spec = new VarSpec
        {
            Name = name,
            ConfigKey = key,
            Type = type,
            Description = description,
            Required = required,
            Secret = secret,
            Min = min,
            Max = max,
            MinLength = type == VarType.List ? null : minLength,
            MaxLength = type == VarType.List ? null : maxLength,
            MinItems = type == VarType.List ? minLength : null,
            MaxItems = type == VarType.List ? maxLength : null,
            ItemMin = itemMin,
            ItemMax = itemMax,
            ItemMinLength = itemMinLength,
            ItemMaxLength = itemMaxLength,
            Pattern = pattern,
            Schemes = schemes,
            Values = values,
            Items = listKind,
            Schema = json ? SchemaGenerator.For(clr) : null,
            PropertyPath = path,
            ClrType = prop.PropertyType,
        };

        var initial = instance is null ? null
            : json ? (prop.GetValue(instance) is { } structured ? JsonVar.ToNode(structured, clr) : null)
            : Normalize(prop.GetValue(instance), clr);
        bool isUnsetValueType = clr.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null
            && Equals(prop.GetValue(instance ?? Activator.CreateInstance(prop.DeclaringType!)), Activator.CreateInstance(clr));
        if (required && (initial is null || isUnsetValueType))
        {
            // [Required] with no real initializer: the platform must supply it.
            spec.Default = null;
        }
        else if (initial is not null)
        {
            spec.Default = initial;
            spec.Required = false;
        }

        if (secret && spec.Default is not null)
        {
            errors.Add($"{key}: a [Secret] value cannot have a default. Remove the initializer.");
            spec.Default = null;
        }

        if (spec.Default is not null && Constraints.Check(spec, spec.Default) is { } problem)
        {
            errors.Add($"{key}: the default {Constraints.Show(spec.Default)} {problem.Message}. Add a valid initializer or mark it [Required].");
        }

        model.Vars[name] = spec;
    }

    /// <summary>
    /// A record's compiler-generated <c>ToString</c> prints every property, so a <see cref="SecretAttribute"/> value
    /// would end up in any log line that prints the options. The record must leave secrets out of <c>PrintMembers</c>.
    /// </summary>
    private static void CheckSecretsNotPrinted(Type type, List<string> errors)
    {
        var printMembers = type.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, [typeof(System.Text.StringBuilder)]);
        bool isRecord = type.GetMethod("<Clone>$") is not null;
        if (!isRecord || printMembers?.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
        {
            return;
        }

        foreach (var secret in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetCustomAttribute<SecretAttribute>() is not null))
        {
            errors.Add($"{type.Name}.{secret.Name}: {type.Name} is a record, so its generated ToString prints this [Secret] value. Make {type.Name} a class, or declare PrintMembers to leave secrets out.");
        }
    }

    /// <summary>
    /// A constraint the variable's contract type cannot carry would be dropped from the contract while the app still
    /// enforces it (or, for [UrlSchemes], exported where the platform rejects it), so it is a declaration error.
    /// </summary>
    private static void CheckConstraintsFit(PropertyInfo prop, Type clr, VarType type, string key, List<string> errors)
    {
        var exported = type.ToString().ToLowerInvariant();
        void Misfit(string attribute, string fits) => errors.Add(
            $"{key}: [{attribute}] applies to {fits}, but {clr.Name} is exported as a{(exported[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "n" : "")} {exported} variable. Remove the attribute or change the property's type.");

        if (prop.GetCustomAttribute<UrlSchemesAttribute>() is not null && type != VarType.Url)
        {
            Misfit("UrlSchemes", "string or Uri properties");
        }

        if (prop.GetCustomAttribute<AllowedValuesAttribute>() is not null && (type != VarType.Enum || clr.IsEnum))
        {
            Misfit("AllowedValues", clr.IsEnum ? "string properties; a C# enum already limits the values to its members" : "string properties");
        }

        if (prop.GetCustomAttribute<RangeAttribute>() is not null && type is not (VarType.Int or VarType.Float or VarType.Duration))
        {
            Misfit("Range", "numbers and TimeSpan");
        }

        if (prop.GetCustomAttribute<RegularExpressionAttribute>() is not null && type != VarType.String)
        {
            Misfit("RegularExpression", "string properties");
        }

        // A url takes a maximum length (maxLength); a minimum on a url is reported where the lengths are read.
        if (prop.GetCustomAttribute<StringLengthAttribute>() is not null && type is not (VarType.String or VarType.Url))
        {
            Misfit("StringLength", "string and url properties");
        }

        foreach (var name in new[] { (typeof(MinLengthAttribute), "MinLength"), (typeof(MaxLengthAttribute), "MaxLength"), (typeof(LengthAttribute), "Length") }
            .Where(a => prop.GetCustomAttribute(a.Item1) is not null).Select(a => a.Item2))
        {
            if (type is not (VarType.String or VarType.Url or VarType.List))
            {
                Misfit(name, "strings, urls and lists");
            }
        }
    }

    private static void ReadFile(
        PropertyInfo prop,
        FileInputAttribute attr,
        string key,
        List<PropertyInfo> path,
        ContractModel model,
        List<string> errors,
        List<(FileSpec, string, string, Type)> pending)
    {
        var name = attr.Name ?? Kebab(prop.Name);
        if (!InputNameSyntax().IsMatch(name))
        {
            errors.Add($"{key}: file input name '{name}' must be lowercase letters, digits and hyphens.");
            return;
        }

        if (!attr.Path.StartsWith('/') || attr.Path.Contains("/../", StringComparison.Ordinal) || attr.Path.EndsWith('/'))
        {
            errors.Add($"{key}: path '{attr.Path}' must be absolute and normalised, without a trailing slash.");
        }

        if (attr.PathEnv is not null && !EnvNameSyntax().IsMatch(attr.PathEnv))
        {
            errors.Add($"{key}: PathEnv '{attr.PathEnv}' must be UPPER_SNAKE_CASE.");
        }

        if (attr.Reload == Reload.Watch && attr is ConfigFileAttribute or TextFileAttribute)
        {
            // Their content is read once, when the options bind; claiming Watch would mislead the platform.
            errors.Add($"{key}: Reload.Watch is not supported for [{attr.GetType().Name.Replace("Attribute", "", StringComparison.Ordinal)}] yet; use Reload.Restart so the platform rolls the pods when it changes.");
        }

        var description = DescriptionOf(prop, key, errors);
        bool required = prop.GetCustomAttribute<RequiredAttribute>() is not null;
        bool secret = prop.GetCustomAttribute<SecretAttribute>() is not null;
        long? maxSize = attr.MaxSize > 0 ? attr.MaxSize : null;

        void Expect(Type expected)
        {
            if (prop.PropertyType != expected)
            {
                errors.Add($"{key}: a [{attr.GetType().Name.Replace("Attribute", "", StringComparison.Ordinal)}] property must be of type {expected.Name}.");
            }
        }

        FileSpec spec;
        switch (attr)
        {
            case ConfigFileAttribute:
                if (!IsComplex(prop.PropertyType))
                {
                    errors.Add($"{key}: a [ConfigFile] property must be a class the JSON file deserializes into.");
                }

                spec = new FileSpec
                {
                    Name = name, Type = FileType.Config, Format = "json", Description = description, Required = required, Secret = secret,
                    Path = attr.Path, PathEnv = attr.PathEnv, Reload = attr.Reload, MaxSize = maxSize,
                    Schema = SchemaGenerator.For(prop.PropertyType), PropertyPath = path,
                };
                break;

            case TlsFileAttribute tls:
                Expect(typeof(TlsKeyPair));
                if (tls.MinRemaining is not null && !GoDuration.IsValid(tls.MinRemaining))
                {
                    errors.Add($"{key}: MinRemaining '{tls.MinRemaining}' must be a duration such as 720h.");
                }

                spec = new FileSpec
                {
                    Name = name, Type = FileType.Tls, Description = description, Required = required, Secret = true,
                    Path = attr.Path, PathEnv = attr.PathEnv, Reload = attr.Reload, MaxSize = maxSize,
                    DnsNames = tls.DnsNames.Length > 0 ? tls.DnsNames : null,
                    KeyAlgorithms = tls.KeyAlgorithms == KeyAlgorithms.Any
                        ? null
                        : Enum.GetValues<KeyAlgorithms>().Where(a => a != KeyAlgorithms.Any && tls.KeyAlgorithms.HasFlag(a)).Select(a => a.ToString()).ToList(),
                    MinRemaining = tls.MinRemaining, RequireCA = tls.RequireCA, PropertyPath = path,
                };
                break;

            case CaBundleFileAttribute ca:
                Expect(typeof(CaBundle));
                spec = new FileSpec
                {
                    Name = name, Type = FileType.CaBundle, Description = description, Required = required, Secret = secret,
                    Path = attr.Path, PathEnv = attr.PathEnv, Reload = attr.Reload, MaxSize = maxSize,
                    MinCertificates = ca.MinCertificates == 1 ? null : ca.MinCertificates, PropertyPath = path,
                };
                break;

            case KeystoreFileAttribute ks:
                Expect(typeof(Keystore));
                spec = new FileSpec
                {
                    Name = name, Type = FileType.Keystore, Format = "pkcs12", Description = description, Required = required, Secret = true,
                    Path = attr.Path, PathEnv = attr.PathEnv, Reload = attr.Reload, MaxSize = maxSize, PropertyPath = path,
                };
                if (ks.PasswordProperty is not null)
                {
                    var section = key.Contains(':', StringComparison.Ordinal) ? key[..key.LastIndexOf(':')] : "";
                    var passwordKey = section.Length == 0 ? ks.PasswordProperty : section + ":" + ks.PasswordProperty;
                    pending.Add((spec, key, passwordKey, prop.DeclaringType!));
                }

                break;

            case TextFileAttribute text:
                if (prop.PropertyType != typeof(string))
                {
                    errors.Add($"{key}: a [TextFile] property must be a string.");
                }

                if (text.Pattern is not null && NonRe2().IsMatch(text.Pattern))
                {
                    errors.Add($"{key}: pattern '{text.Pattern}' uses .NET-only regex features. The platform and other SDKs need RE2.");
                }

                spec = new FileSpec
                {
                    Name = name, Type = FileType.Text, Description = description, Required = required, Secret = secret,
                    Path = attr.Path, PathEnv = attr.PathEnv, Reload = attr.Reload, MaxSize = maxSize,
                    Pattern = text.Pattern, MinLength = text.MinLength > 0 ? text.MinLength : null,
                    MaxLength = text.MaxLength > 0 ? text.MaxLength : null, PropertyPath = path,
                };
                break;

            case BinaryFileAttribute:
                Expect(typeof(BinaryFile));
                spec = new FileSpec
                {
                    Name = name, Type = FileType.Binary, Description = description, Required = required, Secret = secret,
                    Path = attr.Path, PathEnv = attr.PathEnv, Reload = attr.Reload, MaxSize = maxSize, PropertyPath = path,
                };
                break;

            default:
                errors.Add($"{key}: unknown file attribute {attr.GetType().Name}.");
                return;
        }

        if (!model.Files.TryAdd(name, spec))
        {
            errors.Add($"{key}: file input '{name}' is declared twice.");
        }
    }

    private static void ResolveKeystorePasswords(ContractModel model, List<(FileSpec Spec, string Owner, string PasswordKey, Type DeclaringType)> pending, List<string> errors)
    {
        foreach (var (spec, owner, passwordKey, _) in pending)
        {
            var password = model.Vars.Values.FirstOrDefault(v => v.ConfigKey == passwordKey);
            if (password is null)
            {
                errors.Add($"{owner}: PasswordProperty points at {passwordKey}, which is not a string property in the contract.");
                continue;
            }

            if (!password.Secret)
            {
                errors.Add($"{owner}: the keystore password {passwordKey} must be marked [Secret].");
            }

            model.Files[spec.Name] = new FileSpec
            {
                Name = spec.Name, Type = spec.Type, Format = spec.Format, Description = spec.Description, Required = spec.Required,
                Secret = spec.Secret, Path = spec.Path, PathEnv = spec.PathEnv, Reload = spec.Reload, MaxSize = spec.MaxSize,
                PasswordVar = password.Name, PropertyPath = spec.PropertyPath, PasswordPath = password.PropertyPath,
            };
        }
    }

    private static string DescriptionOf(PropertyInfo prop, string key, List<string> errors)
    {
        var description = prop.GetCustomAttribute<DescriptionAttribute>()?.Description
            ?? prop.GetCustomAttribute<DisplayAttribute>()?.GetDescription();
        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length < 5)
        {
            errors.Add($"{key}: add [Description(\"...\")] of at least 5 characters. Every input in a contract is documented.");
            return "";
        }

        return description.Trim();
    }

    private static (int? Min, int? Max) LengthBounds(PropertyInfo prop)
    {
        int? min = null, max = null;
        if (prop.GetCustomAttribute<StringLengthAttribute>() is { } sl)
        {
            max = sl.MaximumLength;
            min = sl.MinimumLength > 0 ? sl.MinimumLength : null;
        }

        if (prop.GetCustomAttribute<MinLengthAttribute>() is { } minAttr)
        {
            min = minAttr.Length;
        }

        if (prop.GetCustomAttribute<MaxLengthAttribute>() is { } maxAttr && maxAttr.Length > 0)
        {
            max = maxAttr.Length;
        }

        if (prop.GetCustomAttribute<LengthAttribute>() is { } len)
        {
            min = len.MinimumLength;
            max = len.MaximumLength;
        }

        return (min, max);
    }

    internal static object? Normalize(object? value, Type clr)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return s.Length == 0 ? null : s;
            case bool b:
                return b;
            case TimeSpan ts:
                return GoDuration.Format(ts);
            case Uri u:
                return u.ToString();
            case Enum e:
                return e.ToString();
            case float or double or decimal:
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            case IEnumerable list when value is not string:
                var items = list.Cast<object?>().ToList();
                if (items.Count == 0)
                {
                    return null;
                }

                return ListItemKind(clr) == "int"
                    ? items.Select(i => (object)Convert.ToInt64(i, CultureInfo.InvariantCulture)).ToList()
                    : items.Select(i => (object)(Convert.ToString(i, CultureInfo.InvariantCulture) ?? "")).ToList();
            default:
                return IsInteger(value.GetType()) ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : value;
        }
    }

    private static TimeSpan ToTimeSpan(object value) => value switch
    {
        TimeSpan ts => ts,
        string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"Cannot read {value} as a TimeSpan."),
    };

    internal static bool IsInteger(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
        || t == typeof(uint) || t == typeof(ushort) || t == typeof(sbyte) || t == typeof(ulong);

    internal static bool IsScalar(Type t) =>
        t == typeof(string) || t == typeof(bool) || t == typeof(TimeSpan) || t == typeof(Uri) || t.IsEnum
        || IsInteger(t) || t == typeof(double) || t == typeof(float) || t == typeof(decimal);

    private static (long? Min, long? Max) Narrow(long? min, long? max, (long? Min, long? Max) range) =>
        (range.Min is { } lo && (min is null || min < lo) ? lo : min,
         range.Max is { } hi && (max is null || max > hi) ? hi : max);

    /// <summary>The element type of an array or a generic collection, or null.</summary>
    internal static Type? ElementType(Type t) =>
        t == typeof(string) ? null
        : t.IsArray ? t.GetElementType()
        : t.IsGenericType && t.GetGenericArguments().Length == 1 && typeof(IEnumerable).IsAssignableFrom(t) ? t.GetGenericArguments()[0]
        : null;

    /// <summary>"string" or "int" for a list of scalars the binder fills from indexed keys; null otherwise.</summary>
    internal static string? ListItemKind(Type t)
    {
        var element = ElementType(t);
        if (element is null)
        {
            return null;
        }

        if (element == typeof(string))
        {
            return "string";
        }

        return IsInteger(element) ? "int" : null;
    }

    private static bool IsComplex(Type t) =>
        t.IsClass && t != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(t) && !IsScalar(t);

    private static string Describe(Type t) =>
        typeof(IDictionary).IsAssignableFrom(t) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            ? "a dictionary"
            : typeof(IEnumerable).IsAssignableFrom(t) ? "a list of objects" : t.Name;

    /// <summary>
    /// [RegularExpression] must match the whole value, while a contract pattern (like CUE's =~ and JSON Schema's
    /// pattern) matches anywhere in it. Anchoring keeps the platform and the app agreeing on what is valid.
    /// </summary>
    internal static string? FullMatch(string? pattern) => pattern is null ? null : $"^(?:{pattern})$";

    internal static string Kebab(string pascal) =>
        string.Concat(pascal.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

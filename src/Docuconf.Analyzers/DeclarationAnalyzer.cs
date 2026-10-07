using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Docuconf.Analyzers;

/// <summary>
/// Reports, in the IDE and at build, the declaration errors that <c>docuconf export</c> and startup would report later:
/// every check that can be made from the source alone. Mirrors <c>ContractReader</c> in the runtime library.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeclarationAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "Docuconf";
    private const string HelpLink = "https://github.com/docuconf/docuconf-dotnet#analyzer";

    private static DiagnosticDescriptor Rule(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    internal static readonly DiagnosticDescriptor MissingDescription = Rule("DOCUCONF001",
        "Every input needs a description",
        "{0}: add [Description(\"...\")] of at least 5 characters; every input in a contract is documented");

    internal static readonly DiagnosticDescriptor SecretWithDefault = Rule("DOCUCONF002",
        "A secret cannot have a default",
        "{0}: a [Secret] value cannot have a default; remove the initializer");

    internal static readonly DiagnosticDescriptor ConstraintMisfit = Rule("DOCUCONF003",
        "The constraint does not fit the property's type",
        "{0}: [{1}] applies to {2}, but {3} is exported as a{4} {5} variable; remove the attribute or change the property's type");

    internal static readonly DiagnosticDescriptor DefaultViolates = Rule("DOCUCONF004",
        "The default violates the property's constraints",
        "{0}: the default {1} {2}; add a valid initializer or mark it [Required]");

    internal static readonly DiagnosticDescriptor BadEnvName = Rule("DOCUCONF005",
        "Environment variable names are UPPER_SNAKE_CASE",
        "{0}: environment variable name '{1}' must be UPPER_SNAKE_CASE");

    internal static readonly DiagnosticDescriptor BadService = Rule("DOCUCONF006",
        "The service name must be a DNS label",
        "Service name '{0}' must be a DNS label: lowercase letters, digits and hyphens");

    internal static readonly DiagnosticDescriptor NonRe2Pattern = Rule("DOCUCONF007",
        "Patterns must be RE2",
        "{0}: pattern '{1}' uses .NET-only regex features (lookaround, backreferences, atomic groups); the platform and other SDKs need RE2");

    internal static readonly DiagnosticDescriptor SecretInRecord = Rule("DOCUCONF008",
        "A record's ToString prints secrets",
        "{0}: {1} is a record, so its generated ToString prints this [Secret] value; make {1} a class, or declare PrintMembers to leave secrets out");

    internal static readonly DiagnosticDescriptor NotDescribable = Rule("DOCUCONF009",
        "The contract cannot describe this property",
        "{0}: {1} cannot be described by the contract, but the app would still read it; mark it [JsonVar] to make it one JSON variable, or [External(\"appsettings\")] to keep it out of the contract");

    internal static readonly DiagnosticDescriptor FileInputType = Rule("DOCUCONF010",
        "A file input needs its own property type",
        "{0}: a [{1}] property must be of type {2}");

    private static readonly Regex EnvNameSyntax = new("^[A-Z][A-Z0-9_]*$");
    private static readonly Regex ServiceSyntax = new("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$");
    private static readonly Regex NonRe2 = new(@"\(\?[=!<>]|\(\?<[=!]|\\[1-9]|\\k<|\(\?>|[*+?}]\+");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        MissingDescription, SecretWithDefault, ConstraintMisfit, DefaultViolates, BadEnvName, BadService, NonRe2Pattern,
        SecretInRecord, NotDescribable, FileInputType);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var contract = type.GetAttributes().FirstOrDefault(a => Is(a.AttributeClass, "Docuconf.ConfigContractAttribute"));
        if (contract is null)
        {
            return;
        }

        if (contract.ConstructorArguments.Length == 1 && contract.ConstructorArguments[0].Value is string service && !ServiceSyntax.IsMatch(service))
        {
            context.ReportDiagnostic(Diagnostic.Create(BadService, LocationOf(contract, type), service));
        }

        var section = contract.NamedArguments.FirstOrDefault(a => a.Key == "Section").Value.Value as string ?? "";
        new Walker(context).Object(type, section, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
    }

    private enum VarType
    {
        String, Int, Float, Bool, Duration, Url, Enum, List, Json,
    }

    private sealed class Walker(SymbolAnalysisContext context)
    {
        private void Report(DiagnosticDescriptor rule, Location location, params object[] args) =>
            context.ReportDiagnostic(Diagnostic.Create(rule, location, args));

        public void Object(INamedTypeSymbol type, string section, HashSet<ITypeSymbol> visiting)
        {
            if (!visiting.Add(type))
            {
                return;
            }

            var properties = Properties(type).ToList();
            if (type.IsRecord && !type.GetMembers("PrintMembers").Any(m => !m.IsImplicitlyDeclared))
            {
                foreach (var secret in properties.Where(p => Has(p, "Docuconf.SecretAttribute")))
                {
                    Report(SecretInRecord, secret.Locations.FirstOrDefault() ?? Location.None, Key(section, secret), type.Name);
                }
            }

            foreach (var prop in properties)
            {
                Property(prop, Key(section, prop), visiting);
            }

            visiting.Remove(type);
        }

        private void Property(IPropertySymbol prop, string key, HashSet<ITypeSymbol> visiting)
        {
            var location = prop.Locations.FirstOrDefault() ?? Location.None;
            if (Has(prop, "Docuconf.ExternalAttribute"))
            {
                return;
            }

            if (Find(prop, "Docuconf.FileInputAttribute") is { } file)
            {
                FileInput(prop, file, key, location);
                return;
            }

            var clr = Unwrap(prop.Type);
            bool json = Has(prop, "Docuconf.JsonVarAttribute");
            if (json || IsScalar(clr) || ListItemKind(clr) is not null)
            {
                Variable(prop, clr, key, location, json);
            }
            else if (IsComplex(clr))
            {
                Object((INamedTypeSymbol)clr, key, visiting);
            }
            else
            {
                Report(NotDescribable, location, key, Describe(clr));
            }
        }

        private void Variable(IPropertySymbol prop, ITypeSymbol clr, string key, Location location, bool json)
        {
            if (Find(prop, "Docuconf.EnvNameAttribute") is { } envName && envName.ConstructorArguments.FirstOrDefault().Value is string name && !EnvNameSyntax.IsMatch(name))
            {
                Report(BadEnvName, LocationOf(envName, prop), key, name);
            }

            if (!HasDescription(prop))
            {
                Report(MissingDescription, location, key);
            }

            var listKind = ListItemKind(clr);
            VarType type;
            if (json)
            {
                type = VarType.Json;
                listKind = null;
            }
            else if (listKind is not null)
            {
                type = VarType.List;
            }
            else if (clr.SpecialType == SpecialType.System_String)
            {
                type = Has(prop, "Docuconf.UrlSchemesAttribute") || Has(prop, "System.ComponentModel.DataAnnotations.UrlAttribute") ? VarType.Url
                    : Has(prop, "System.ComponentModel.DataAnnotations.AllowedValuesAttribute") ? VarType.Enum
                    : VarType.String;
            }
            else if (Is(clr, "System.Uri"))
            {
                type = VarType.Url;
            }
            else if (clr.TypeKind == TypeKind.Enum)
            {
                type = VarType.Enum;
            }
            else if (clr.SpecialType == SpecialType.System_Boolean)
            {
                type = VarType.Bool;
            }
            else if (Is(clr, "System.TimeSpan"))
            {
                type = VarType.Duration;
            }
            else
            {
                type = IsInteger(clr) ? VarType.Int : VarType.Float;
            }

            Misfits(prop, clr, key, type, listKind);

            if (Find(prop, "System.ComponentModel.DataAnnotations.RegularExpressionAttribute") is { } regex
                && regex.ConstructorArguments.FirstOrDefault().Value is string pattern && NonRe2.IsMatch(pattern))
            {
                Report(NonRe2Pattern, LocationOf(regex, prop), key, pattern);
            }

            var initializer = Initializer(prop);
            bool secret = Has(prop, "Docuconf.SecretAttribute");
            if (secret && initializer is not null && !IsEmpty(initializer))
            {
                Report(SecretWithDefault, initializer.GetLocation(), key);
                return;
            }

            if (!secret)
            {
                DefaultInBounds(prop, clr, key, type, initializer);
            }
        }

        private void Misfits(IPropertySymbol prop, ITypeSymbol clr, string key, VarType type, string? listKind)
        {
            var exported = type.ToString().ToLowerInvariant();
            var article = "aeiou".IndexOf(exported[0]) >= 0 ? "n" : "";
            void Misfit(AttributeData attribute, string name, string fits) =>
                Report(ConstraintMisfit, LocationOf(attribute, prop), key, name, fits, clr.Name, article, exported);

            if (Find(prop, "Docuconf.UrlSchemesAttribute") is { } schemes && type != VarType.Url)
            {
                Misfit(schemes, "UrlSchemes", "string or Uri properties");
            }

            if (Find(prop, "System.ComponentModel.DataAnnotations.AllowedValuesAttribute") is { } allowed && (type != VarType.Enum || clr.TypeKind == TypeKind.Enum))
            {
                Misfit(allowed, "AllowedValues", clr.TypeKind == TypeKind.Enum ? "string properties; a C# enum already limits the values to its members" : "string properties");
            }

            if (Find(prop, "System.ComponentModel.DataAnnotations.RangeAttribute") is { } range && type is not (VarType.Int or VarType.Float or VarType.Duration))
            {
                Misfit(range, "Range", "numbers and TimeSpan");
            }

            if (Find(prop, "System.ComponentModel.DataAnnotations.RegularExpressionAttribute") is { } regex && type != VarType.String)
            {
                Misfit(regex, "RegularExpression", "string properties");
            }

            if (Find(prop, "System.ComponentModel.DataAnnotations.StringLengthAttribute") is { } stringLength && type is not (VarType.String or VarType.Url))
            {
                Misfit(stringLength, "StringLength", "string and url properties");
            }

            foreach (var name in new[] { "MinLength", "MaxLength", "Length" })
            {
                if (Find(prop, $"System.ComponentModel.DataAnnotations.{name}Attribute") is { } length && type is not (VarType.String or VarType.Url or VarType.List))
                {
                    Misfit(length, name, "strings, urls and lists");
                }
            }

            if (Find(prop, "Docuconf.ItemRangeAttribute") is { } itemRange && listKind != "int")
            {
                Misfit(itemRange, "ItemRange", "lists of integers, such as int[] or List<long>");
            }

            if (Find(prop, "Docuconf.ItemLengthAttribute") is { } itemLength && listKind != "string")
            {
                Misfit(itemLength, "ItemLength", "lists of strings, such as string[] or List<string>");
            }

            if (type == VarType.Json && (IsScalar(clr) || clr.IsValueType))
            {
                Misfit(Find(prop, "Docuconf.JsonVarAttribute")!, "JsonVar", "classes, lists and dictionaries a JSON value deserializes into");
            }
        }

        /// <summary>Checks a literal default (or the 0 of an unset number) against [Range] and [AllowedValues].</summary>
        private void DefaultInBounds(IPropertySymbol prop, ITypeSymbol clr, string key, VarType type, ExpressionSyntax? initializer)
        {
            bool required = Has(prop, "System.ComponentModel.DataAnnotations.RequiredAttribute");
            bool nullable = !SymbolEqualityComparer.Default.Equals(prop.Type, clr);
            object? value = initializer is null ? null : Literal(initializer);
            if (required && value is 0d)
            {
                return; // [Required] with the type's zero: the platform must supply it.
            }

            if (initializer is null && !required && !nullable && (type is VarType.Int or VarType.Float))
            {
                value = 0d; // An unset number defaults to 0, which the contract records.
            }

            if (value is null)
            {
                return;
            }

            if (type is VarType.Int or VarType.Float && value is double number
                && Find(prop, "System.ComponentModel.DataAnnotations.RangeAttribute") is { ConstructorArguments.Length: 2 } range
                && ToDouble(range.ConstructorArguments[0].Value) is { } min && ToDouble(range.ConstructorArguments[1].Value) is { } max)
            {
                var shown = number.ToString(CultureInfo.InvariantCulture);
                if (number < min)
                {
                    Report(DefaultViolates, initializer?.GetLocation() ?? prop.Locations.First(), key, shown, $"is below the minimum {Show(min)}");
                }
                else if (number > max)
                {
                    Report(DefaultViolates, initializer?.GetLocation() ?? prop.Locations.First(), key, shown, $"is above the maximum {Show(max)}");
                }
            }

            if (type == VarType.Enum && value is string text && text.Length > 0
                && Find(prop, "System.ComponentModel.DataAnnotations.AllowedValuesAttribute") is { } allowed)
            {
                var values = allowed.ConstructorArguments.SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : ImmutableArray.Create(a))
                    .Select(v => Convert.ToString(v.Value, CultureInfo.InvariantCulture)).ToList();
                if (!values.Contains(text))
                {
                    Report(DefaultViolates, initializer!.GetLocation(), key, $"\"{text}\"", $"is not one of {string.Join(", ", values)}");
                }
            }
        }

        private void FileInput(IPropertySymbol prop, AttributeData attribute, string key, Location location)
        {
            if (!HasDescription(prop))
            {
                Report(MissingDescription, location, key);
            }

            var name = attribute.AttributeClass!.Name;
            var expected = name switch
            {
                "TlsFileAttribute" => "Docuconf.TlsKeyPair",
                "CaBundleFileAttribute" => "Docuconf.CaBundle",
                "KeystoreFileAttribute" => "Docuconf.Keystore",
                "BinaryFileAttribute" => "Docuconf.BinaryFile",
                "TextFileAttribute" => "System.String",
                _ => null,
            };
            var shortName = name.Substring(0, name.Length - "Attribute".Length);
            if (expected is not null && !Is(Unwrap(prop.Type), expected))
            {
                Report(FileInputType, location, key, shortName, expected.Substring(expected.LastIndexOf('.') + 1));
            }

            if (name == "ConfigFileAttribute" && !IsComplex(Unwrap(prop.Type)))
            {
                Report(FileInputType, location, key, shortName, "a class the JSON file deserializes into");
            }

            if (name == "TextFileAttribute" && attribute.NamedArguments.FirstOrDefault(a => a.Key == "Pattern").Value.Value is string pattern && NonRe2.IsMatch(pattern))
            {
                Report(NonRe2Pattern, LocationOf(attribute, prop), key, pattern);
            }
        }
    }

    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>();
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var prop in t.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsIndexer || prop.DeclaredAccessibility != Accessibility.Public
                    || prop.SetMethod is not { DeclaredAccessibility: Accessibility.Public } || !seen.Add(prop.Name))
                {
                    continue;
                }

                yield return prop;
            }
        }
    }

    private static string Key(string section, IPropertySymbol prop) => section.Length == 0 ? prop.Name : section + ":" + prop.Name;

    private static bool HasDescription(IPropertySymbol prop)
    {
        string? description = null;
        foreach (var attribute in prop.GetAttributes())
        {
            if (Is(attribute.AttributeClass, "System.ComponentModel.DescriptionAttribute"))
            {
                description = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            }
            else if (Is(attribute.AttributeClass, "System.ComponentModel.DataAnnotations.DisplayAttribute"))
            {
                description ??= attribute.NamedArguments.FirstOrDefault(a => a.Key == "Description").Value.Value as string;
            }
        }

        return description is not null && description.Trim().Length >= 5;
    }

    private static ExpressionSyntax? Initializer(IPropertySymbol prop) =>
        prop.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Initializer?.Value;

    /// <summary>Initializers that leave a secret without a default: <c>""</c>, <c>null!</c>, <c>default</c>, <c>[]</c>.</summary>
    private static bool IsEmpty(ExpressionSyntax expression) => expression switch
    {
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang => IsEmpty(bang.Operand),
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NullLiteralExpression) || literal.IsKind(SyntaxKind.DefaultLiteralExpression)
            || (literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText.Length == 0),
        DefaultExpressionSyntax => true,
        MemberAccessExpressionSyntax member => member.ToString() is "string.Empty" or "String.Empty",
        CollectionExpressionSyntax collection => collection.Elements.Count == 0,
        ObjectCreationExpressionSyntax creation => creation.ArgumentList is null or { Arguments.Count: 0 } && creation.Initializer is null,
        ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList.Arguments.Count == 0 && creation.Initializer is null,
        _ => false,
    };

    /// <summary>A number literal as a double, a string literal as a string, or null.</summary>
    private static object? Literal(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.NumericLiteralExpression) => ToDouble(literal.Token.Value),
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax operand }
            when operand.IsKind(SyntaxKind.NumericLiteralExpression) => -ToDouble(operand.Token.Value),
        _ => null,
    };

    private static double? ToDouble(object? value) => value switch
    {
        null or string => null,
        IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static string Show(double d) => d.ToString(CultureInfo.InvariantCulture);

    private static Location LocationOf(AttributeData attribute, ISymbol fallback) =>
        attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? fallback.Locations.FirstOrDefault() ?? Location.None;

    private static bool Has(ISymbol symbol, string attribute) => Find(symbol, attribute) is not null;

    /// <summary>The first attribute of <paramref name="name"/> or a class derived from it.</summary>
    private static AttributeData? Find(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a =>
        {
            for (var t = a.AttributeClass; t is not null; t = t.BaseType)
            {
                if (Is(t, name))
                {
                    return true;
                }
            }

            return false;
        });

    private static bool Is(ITypeSymbol? type, string fullName) =>
        type is not null && type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::" + fullName;

    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    private static bool IsInteger(ITypeSymbol t) => t.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
        or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_UInt32 or SpecialType.System_UInt16
        or SpecialType.System_SByte or SpecialType.System_UInt64;

    private static bool IsScalar(ITypeSymbol t) =>
        t.SpecialType is SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Double
            or SpecialType.System_Single or SpecialType.System_Decimal
        || IsInteger(t) || t.TypeKind == TypeKind.Enum || Is(t, "System.TimeSpan") || Is(t, "System.Uri");

    private static bool IsEnumerable(ITypeSymbol t) =>
        t is IArrayTypeSymbol || t.SpecialType == SpecialType.System_Collections_IEnumerable
        || t.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_Collections_IEnumerable);

    private static ITypeSymbol? ElementType(ITypeSymbol t) =>
        t.SpecialType == SpecialType.System_String ? null
        : t is IArrayTypeSymbol array ? array.ElementType
        : t is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic && IsEnumerable(t) ? generic.TypeArguments[0]
        : null;

    private static string? ListItemKind(ITypeSymbol t) => ElementType(t) switch
    {
        null => null,
        { SpecialType: SpecialType.System_String } => "string",
        var e when IsInteger(e) => "int",
        _ => null,
    };

    private static bool IsComplex(ITypeSymbol t) =>
        t.TypeKind == TypeKind.Class && t.SpecialType != SpecialType.System_String && !IsEnumerable(t) && !IsScalar(t);

    private static string Describe(ITypeSymbol t) =>
        t.AllInterfaces.Any(i => i.Name is "IDictionary" or "IReadOnlyDictionary") || t.Name is "IDictionary" or "IReadOnlyDictionary"
            ? "a dictionary"
            : IsEnumerable(t) ? "a list of objects" : t.Name;
}

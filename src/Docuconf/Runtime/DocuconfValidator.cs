using System.Collections;
using System.ComponentModel.DataAnnotations;
using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Docuconf.Runtime;

/// <summary>Binds an options class per property and loads its file inputs.</summary>
internal sealed class DocuconfConfigureOptions<T>(IConfiguration configuration, DocuconfSettings settings) : IConfigureOptions<T>
    where T : class
{
    public void Configure(T options)
    {
        var model = ContractCache.For(typeof(T));
        BindState.Set(options, DocuconfBinder.Bind(options, configuration, model, settings));
    }
}

/// <summary>
/// Validates an options class at startup: binding and file problems, required values, and every
/// DataAnnotations constraint. Reports all violations together, with stable codes, and never prints secrets.
/// </summary>
internal sealed class DocuconfValidateOptions<T>(IConfiguration configuration, DocuconfSettings settings) : IValidateOptions<T>
    where T : class
{
    public ValidateOptionsResult Validate(string? name, T options)
    {
        var model = ContractCache.For(typeof(T));
        var violations = new List<Violation>(BindState.Get(options));
        var alreadyInvalid = violations.Select(v => v.Input).ToHashSet(StringComparer.Ordinal);

        foreach (var spec in model.Vars.Values)
        {
            if (spec.PropertyPath is null || alreadyInvalid.Contains(spec.Name))
            {
                continue;
            }

            var section = configuration.GetSection(spec.ConfigKey);
            bool present = DocuconfBinder.IsSet(section, spec);
            if (spec.Required && !present)
            {
                violations.Add(new Violation(Codes.MissingRequired, spec.Name, $"is required ({spec.ConfigKey})"));
                continue;
            }

            var value = DocuconfBinder.GetPath(options, spec.PropertyPath);
            CheckAnnotations(spec, options, value, violations);
            if (spec.Schemes is { } schemes && value is not null && UrlOf(value) is { } uri
                && !schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            {
                violations.Add(new Violation(Codes.InvalidScheme, spec.Name, $"must use one of the schemes {string.Join(", ", schemes)}"));
            }
        }

        if (violations.Count == 0)
        {
            return ValidateOptionsResult.Success;
        }

        var lines = violations.Select(v => v.ToString()).ToList();
        TerminationLog.Write(settings, typeof(T).Name, lines);
        return ValidateOptionsResult.Fail(lines);
    }

    private static void CheckAnnotations(VarSpec spec, object options, object? value, List<Violation> violations)
    {
        var property = spec.PropertyPath![^1];
        var owner = spec.PropertyPath.Count == 1 ? options : DocuconfBinder.GetPath(options, spec.PropertyPath.Take(spec.PropertyPath.Count - 1).ToList()) ?? options;
        var context = new ValidationContext(owner) { MemberName = property.Name, DisplayName = property.Name };
        foreach (var attr in property.GetCustomAttributes(typeof(ValidationAttribute), true).Cast<ValidationAttribute>())
        {
            if (attr is RequiredAttribute)
            {
                continue; // Presence is checked against configuration above.
            }

            if (spec.Type == VarType.Url && attr is StringLengthAttribute or MaxLengthAttribute or LengthAttribute)
            {
                continue; // maxLength, checked while binding in characters rather than UTF-16 units.
            }

            if (attr.GetValidationResult(value, context) is not { } result || result == ValidationResult.Success)
            {
                continue;
            }

            var code = CodeFor(attr, value);
            var message = spec.Secret
                ? $"fails {attr.GetType().Name.Replace("Attribute", "", StringComparison.Ordinal)} (value redacted)"
                : result.ErrorMessage ?? "is invalid";
            violations.Add(new Violation(code, spec.Name, message));
        }
    }

    private static string CodeFor(ValidationAttribute attr, object? value) => attr switch
    {
        RangeAttribute or ItemRangeAttribute or ItemLengthAttribute => Codes.OutOfRange,
        RegularExpressionAttribute => Codes.PatternMismatch,
        AllowedValuesAttribute or DeniedValuesAttribute => Codes.NotInEnum,
        UrlAttribute => Codes.InvalidScheme,
        StringLengthAttribute or MinLengthAttribute or MaxLengthAttribute or LengthAttribute when value is ICollection list =>
            IsTooFew(attr, list.Count) ? Codes.TooFewItems : Codes.TooManyItems,
        StringLengthAttribute or MinLengthAttribute or MaxLengthAttribute or LengthAttribute => Codes.OutOfRange,
        _ => Codes.InvalidType,
    };

    private static bool IsTooFew(ValidationAttribute attr, int count) => attr switch
    {
        MinLengthAttribute min => count < min.Length,
        LengthAttribute len => count < len.MinimumLength,
        _ => false,
    };

    private static Uri? UrlOf(object value) => value switch
    {
        Uri u => u,
        string s when Uri.TryCreate(s, UriKind.Absolute, out var u) => u,
        _ => null,
    };
}

/// <summary>Writes startup failures where <c>kubectl describe pod</c> shows them.</summary>
internal static class TerminationLog
{
    public static void Write(DocuconfSettings settings, string optionsName, IEnumerable<string> lines)
    {
        var path = settings.TerminationLogPath ?? Environment.GetEnvironmentVariable("DOCUCONF_TERMINATION_LOG") ?? "/dev/termination-log";
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(path, $"Invalid configuration ({optionsName}):\n" + string.Join("\n", lines) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the options exception still reports the problems.
        }
    }
}

/// <summary>Contracts are read once per options type.</summary>
internal static class ContractCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ContractModel> Models = new();

    public static ContractModel For(Type type) => Models.GetOrAdd(type, t => ContractReader.Read([t]));
}

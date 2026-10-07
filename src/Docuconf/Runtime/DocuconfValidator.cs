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
/// Validates an options class at startup: binding and file problems, required values, the contract's constraints, and
/// every other DataAnnotation. Reports all violations together, with stable codes, and never prints secrets.
/// </summary>
internal sealed class DocuconfValidateOptions<T>(IConfiguration configuration) : IValidateOptions<T>
    where T : class
{
    public ValidateOptionsResult Validate(string? name, T options)
    {
        var violations = Collect(options);
        return violations.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(violations.Select(v => v.ToString()));
    }

    public List<Violation> Collect(T options)
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

            var section = DocuconfBinder.SectionOf(configuration, spec);
            bool present = DocuconfBinder.IsSet(section, spec);
            if (spec.Required && !present)
            {
                violations.Add(new Violation(Codes.MissingRequired, spec.Name, $"is required ({spec.ConfigKey})"));
                continue;
            }

            var value = DocuconfBinder.GetPath(options, spec.PropertyPath);
            bool custom = HasCustomMessage(spec.PropertyPath[^1]);
            if (!custom)
            {
                CheckContract(spec, value, present ? section.Value : null, violations);
            }

            CheckAnnotations(spec, options, value, violations, custom);
        }

        return violations;
    }

    /// <summary>
    /// Checks the constraints the contract carries with the same code as the contract-first mode, so both modes word a
    /// problem the same way: <c>[out_of_range] ORDERS__PORT: '0' is below the minimum 1</c>.
    /// </summary>
    private static void CheckContract(VarSpec spec, object? value, string? raw, List<Violation> violations)
    {
        if (value is null || Typed(spec, value) is not { } typed || Constraints.Check(spec, typed) is not { } problem)
        {
            return;
        }

        bool show = !spec.Secret && raw is not null && spec.Type != VarType.List;
        violations.Add(new Violation(problem.Code, spec.Name, (show ? $"'{raw}' " : "") + problem.Message + (spec.Secret ? " (value redacted)" : "")));
    }

    /// <summary>A bound value in the form <see cref="Constraints.Check"/> takes, or null for types it does not check here.</summary>
    private static object? Typed(VarSpec spec, object value) => spec.Type switch
    {
        VarType.Int when value is ulong u => u > long.MaxValue ? null : (long)u,
        VarType.Int => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        VarType.Float => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        VarType.Duration when value is TimeSpan ts => ts,
        VarType.Url when value is Uri uri => uri.OriginalString,
        VarType.String or VarType.Url or VarType.Enum => value is Enum e ? e.ToString() : value as string,
        VarType.List when value is IEnumerable items => items.Cast<object?>()
            .Select(i => spec.Items == "int" ? (i is ulong big && big > long.MaxValue ? (object)long.MaxValue : Convert.ToInt64(i, System.Globalization.CultureInfo.InvariantCulture)) : (object)(Convert.ToString(i, System.Globalization.CultureInfo.InvariantCulture) ?? ""))
            .ToList(),
        _ => null,
    };

    /// <summary>
    /// DataAnnotations the contract already carries are checked by <see cref="CheckContract"/>. When the app gave one of
    /// them its own message, the property keeps its DataAnnotations messages instead.
    /// </summary>
    private static bool InContract(ValidationAttribute attr) =>
        attr is RangeAttribute or MinLengthAttribute or MaxLengthAttribute or LengthAttribute or StringLengthAttribute
            or AllowedValuesAttribute or RegularExpressionAttribute or ItemRangeAttribute or ItemLengthAttribute;

    private static bool HasCustomMessage(System.Reflection.PropertyInfo property) =>
        property.GetCustomAttributes(typeof(ValidationAttribute), true).Cast<ValidationAttribute>()
            .Any(a => InContract(a) && (a.ErrorMessageResourceType is not null || a.ErrorMessage != DefaultMessage(a)));

    // Some attributes report their built-in message as ErrorMessage, so compare with a fresh instance's.
    private static string? DefaultMessage(ValidationAttribute attr) => attr switch
    {
        AllowedValuesAttribute => new AllowedValuesAttribute().ErrorMessage,
        MinLengthAttribute => new MinLengthAttribute(0).ErrorMessage,
        MaxLengthAttribute => new MaxLengthAttribute(1).ErrorMessage,
        LengthAttribute => new LengthAttribute(0, 1).ErrorMessage,
        StringLengthAttribute => new StringLengthAttribute(1).ErrorMessage,
        RegularExpressionAttribute => new RegularExpressionAttribute(".").ErrorMessage,
        RangeAttribute => new RangeAttribute(0, 1).ErrorMessage,
        _ => null,
    };

    private static void CheckAnnotations(VarSpec spec, object options, object? value, List<Violation> violations, bool custom)
    {
        var property = spec.PropertyPath![^1];
        var owner = spec.PropertyPath.Count == 1 ? options : DocuconfBinder.GetPath(options, spec.PropertyPath.Take(spec.PropertyPath.Count - 1).ToList()) ?? options;
        var context = new ValidationContext(owner) { MemberName = property.Name, DisplayName = property.Name };
        foreach (var attr in property.GetCustomAttributes(typeof(ValidationAttribute), true).Cast<ValidationAttribute>())
        {
            if (attr is RequiredAttribute || (InContract(attr) && !custom))
            {
                continue; // Presence is checked against configuration above, contract constraints by CheckContract.
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
}

/// <summary>Writes startup failures where <c>kubectl describe pod</c> shows them.</summary>
internal static class TerminationLog
{
    public static void Write(DocuconfSettings settings, string text)
    {
        var path = settings.TerminationLogPath ?? Environment.GetEnvironmentVariable("DOCUCONF_TERMINATION_LOG") ?? "/dev/termination-log";
        if (path.Length == 0 || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.AppendAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: stderr still reports the problems.
        }
    }
}

/// <summary>Contracts are read once per options type.</summary>
internal static class ContractCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ContractModel> Models = new();

    public static ContractModel For(Type type) => Models.GetOrAdd(type, t => ContractReader.Read([t]));
}

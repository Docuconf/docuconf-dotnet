using Docuconf.Contract;
using Microsoft.Extensions.Configuration;

namespace Docuconf.Runtime;

/// <summary>
/// What a declared options class requires at this boot (SPEC §4.2.1, §4.9): the workload's role, and the inputs whose
/// condition holds. A condition reads the condition variable's bound value (the configuration, else the initializer);
/// a value with a violation of its own makes nothing required.
/// </summary>
internal sealed class BootRules
{
    private readonly ContractModel model;
    private readonly IReadOnlyDictionary<string, object?> unbound;
    private readonly object options;
    private readonly IReadOnlySet<string> invalid;
    private readonly string? role;

    public BootRules(ContractModel model, IReadOnlyDictionary<string, object?> unbound, object options, IReadOnlySet<string> invalid)
    {
        (this.model, this.unbound, this.options, this.invalid) = (model, unbound, options, invalid);
        role = model.Roles is { } roles && ValueOf(DocuconfContract.RoleVar) is string r && roles.Contains(r) ? r : null;
    }

    /// <summary>
    /// Whether the workload's role uses an input with <paramref name="roles"/>. With no role, or one the contract does
    /// not declare, no role-specific input is used, so the role is the one problem reported.
    /// </summary>
    public bool InRole(IReadOnlyList<string>? roles) => roles is null || (role is not null && roles.Contains(role));

    /// <summary>The condition, as messages write it, when it makes the input required at this boot; or null.</summary>
    public string? RequiredWhen(RequiredIfSpec? condition, IReadOnlyList<string>? roles) =>
        condition is not null && InRole(roles) && condition.HoldsFor(ValueOf(condition.Var)) ? condition.ToString() : null;

    /// <summary>Whether a variable is required at this boot, and why when a condition makes it so.</summary>
    public (bool Required, string? When) Requirement(VarSpec spec)
    {
        var when = RequiredWhen(spec.RequiredIf, spec.Roles);
        return ((spec.Required && InRole(spec.Roles)) || when is not null, when);
    }

    /// <summary>A file input as this boot requires it.</summary>
    public FileSpec ForBoot(FileSpec spec)
    {
        var when = RequiredWhen(spec.RequiredIf, spec.Roles);
        bool required = InRole(spec.Roles) && (spec.Required || when is not null);
        return required == spec.Required && when is null ? spec : spec.WithRequired(required, when);
    }

    /// <summary>
    /// A variable's value as a condition compares it: a string (an enum's member name), a long or a bool; null when it
    /// has none, or has a violation.
    /// </summary>
    private object? ValueOf(string name)
    {
        if (invalid.Contains(name) || !model.Vars.TryGetValue(name, out var spec))
        {
            return null;
        }

        var value = unbound.TryGetValue(name, out var loaded) ? loaded
            : spec.PropertyPath is { } path ? DocuconfBinder.GetPath(options, path)
            : null;
        object? typed = value switch
        {
            null => null,
            bool b => b,
            string s => s,
            Enum e => e.ToString(),
            ulong u => u > long.MaxValue ? null : (long)u,
            sbyte or byte or short or ushort or int or uint or long => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
        return typed is not null && Constraints.Check(spec, typed) is null ? typed : null;
    }

    /// <summary>
    /// Loads the variables no property binds: those of included fragments (SPEC §4.8) and DOCUCONF_ROLE (SPEC §4.9).
    /// They are read from the configuration at their names, and checked as the contract-first mode checks them.
    /// </summary>
    public static Dictionary<string, object?> LoadUnbound(ContractModel model, IConfiguration configuration, List<Violation> violations)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var spec in model.Vars.Values.Where(v => v.PropertyPath is null))
        {
            var section = configuration.GetSection(spec.ConfigKey);
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            if (section.Value is { } value)
            {
                env[spec.Name] = value;
            }

            foreach (var item in section.GetChildren().Where(c => WireFormat.IsIndex(c.Key) && c.Value is not null))
            {
                env[$"{spec.Name}__{item.Key}"] = item.Value!;
            }

            values[spec.Name] = ContractFirstLoader.Load(spec, null, env, violations, out _);
        }

        return values;
    }
}

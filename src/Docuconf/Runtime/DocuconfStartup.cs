using Docuconf.Contract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Docuconf.Runtime;

/// <summary>The options classes registered with <c>AddDocuconf</c>, in registration order.</summary>
internal sealed class DocuconfRegistry
{
    public List<Registration> Types { get; } = [];

    /// <summary>Whether typo hints were printed, so a <c>LoadOrExit</c> before the host starts does not print them twice.</summary>
    public bool Hinted { get; set; }

    public sealed record Registration(Type Type, DocuconfSettings Settings, Func<IServiceProvider, IReadOnlyList<string>> Resolve, Action<IServiceProvider> CheckConflicts);
}

/// <summary>
/// Replaces the host's startup validator (the one <c>ValidateOnStart</c> registers), so that invalid configuration
/// ends the process with one clean report instead of an unhandled exception: the options classes registered with
/// <c>AddDocuconf</c> are validated first, then everything else <c>ValidateOnStart</c> covers, as before.
/// </summary>
internal sealed class DocuconfStartupValidator(IServiceProvider services, DocuconfRegistry registry) : IStartupValidator
{
    // The host's own validator for ValidateOnStart is internal to Microsoft.Extensions.Options (.NET 8 and later).
    private static readonly Type? HostValidator =
        typeof(IStartupValidator).Assembly.GetType("Microsoft.Extensions.Options.StartupValidator");

    public void Validate()
    {
        DocuconfStartup.ValidateOrExit(services, registry, registry.Types);
        if (HostValidator is not null)
        {
            ((IStartupValidator)ActivatorUtilities.CreateInstance(services, HostValidator)).Validate();
        }
    }
}

/// <summary>Validates registered options classes and reports failures the way SPEC §11.2(5) and the README describe.</summary>
internal static class DocuconfStartup
{
    public static void ValidateOrExit(IServiceProvider services, DocuconfRegistry registry, IReadOnlyList<DocuconfRegistry.Registration> registrations)
    {
        if (registrations.Count == 0)
        {
            return;
        }

        foreach (var registration in registrations)
        {
            registration.CheckConflicts(services);
        }

        var settings = registrations[0].Settings;
        var configuration = services.GetRequiredService<IConfiguration>();
        var problems = new List<string>();
        var declared = new List<VarSpec>();
        Type? failedType = null;
        foreach (var registration in registrations)
        {
            try
            {
                var failures = registration.Resolve(services);
                declared.AddRange(ContractCache.For(registration.Type).Vars.Values);
                if (failures.Count > 0)
                {
                    problems.AddRange(failures);
                    failedType ??= registration.Type;
                }
            }
            catch (ContractException ex)
            {
                // The declaration itself is wrong; docuconf export reports the same lines.
                problems.AddRange(ex.Errors.Select(e => $"{e} (in the declaration of {registration.Type.Name})"));
                failedType ??= registration.Type;
            }
        }

        if (!registry.Hinted)
        {
            registry.Hinted = true;
            foreach (var hint in TypoHints(declared, settings.EnvironmentNames()))
            {
                settings.Error.WriteLine(hint);
            }

            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Docuconf");
            foreach (var registration in registrations)
            {
                ContractModel model;
                try
                {
                    model = ContractCache.For(registration.Type);
                }
                catch (ContractException)
                {
                    continue; // reported above
                }

                foreach (var (input, deprecation) in SetDeprecated(model, configuration, registration.Settings))
                {
                    WarnDeprecated(logger, settings.Error, input, deprecation);
                }
            }
        }

        if (problems.Count == 0)
        {
            return;
        }

        problems = Redact(problems, declared, configuration);
        Report(settings, problems);
        if (registrations.Any(r => r.Settings.ThrowOnInvalid))
        {
            throw new OptionsValidationException(Options.DefaultName, failedType!, problems);
        }

        settings.Exit(1);
    }

    /// <summary>The deprecated inputs (SPEC §4.2) the configuration still sets.</summary>
    internal static IEnumerable<(string Input, DeprecationSpec Deprecation)> SetDeprecated(ContractModel model, IConfiguration configuration, DocuconfSettings settings)
    {
        foreach (var spec in model.Vars.Values)
        {
            if (spec.Deprecated is { } deprecation && DocuconfBinder.IsSet(DocuconfBinder.SectionOf(configuration, spec), spec))
            {
                yield return (spec.Name, deprecation);
            }
        }

        var root = DocuconfBinder.Root(settings, configuration);
        foreach (var spec in model.Files.Values)
        {
            if (spec.Deprecated is { } deprecation
                && FileChecks.Exists(spec, FileChecks.Locate(spec, FileChecks.Lookup(configuration), root)))
            {
                yield return (spec.Name, deprecation);
            }
        }
    }

    /// <summary>
    /// Logs that a deprecated input is still set (SPEC §11.2): its name and its <c>deprecated</c> message, never its
    /// value. It is a warning, not a violation.
    /// </summary>
    internal static void WarnDeprecated(ILogger? logger, TextWriter fallback, string input, DeprecationSpec deprecation)
    {
        var replaced = deprecation.ReplacedBy is { } by ? $" (replaced by {by})" : "";
        if (logger is not null)
        {
            logger.LogWarning("docuconf: {Input} is deprecated but still set: {Message}{Replaced}", input, deprecation.Message, replaced);
        }
        else
        {
            fallback.WriteLine($"docuconf: warning: {input} is deprecated but still set: {deprecation.Message}{replaced}");
        }
    }

    /// <summary>Writes the report to stderr and the termination log.</summary>
    public static void Report(DocuconfSettings settings, IReadOnlyList<string> problems)
    {
        var text = Format(problems);
        if (!settings.ThrowOnInvalid)
        {
            settings.Error.Write(text);
            settings.Error.Flush();
        }

        TerminationLog.Write(settings, text);
    }

    /// <summary><c>docuconf: N configuration problems:</c>, then one indented line per problem.</summary>
    public static string Format(IReadOnlyList<string> problems) =>
        (problems.Count == 1 ? "docuconf: 1 configuration problem:\n" : $"docuconf: {problems.Count} configuration problems:\n")
        + string.Concat(problems.Select(p => "  " + p + "\n"));

    /// <summary>
    /// Replaces the value of every secret variable in <paramref name="lines"/> with <c>***</c>. docuconf's own messages
    /// never hold one; this covers messages from validators the app adds, such as its own <c>IValidateOptions</c>.
    /// Values shorter than 4 characters are left alone, because replacing them would garble every message.
    /// </summary>
    public static List<string> Redact(IEnumerable<string> lines, IEnumerable<VarSpec> declared, IConfiguration configuration)
    {
        var secrets = new List<string>();
        foreach (var spec in declared.Where(v => v.Secret))
        {
            var section = DocuconfBinder.SectionOf(configuration, spec);
            if (section.Value is { } value)
            {
                secrets.Add(value);
                if (DocuconfBinder.IsCsvValue(section, spec))
                {
                    secrets.AddRange(value.Split(spec.Separator ?? ","));
                }
            }

            secrets.AddRange(section.GetChildren().Select(c => c.Value).OfType<string>());
        }

        secrets = secrets.Where(s => s.Length >= 4).Distinct().OrderByDescending(s => s.Length).ToList();
        return lines.Select(line => secrets.Aggregate(line, (l, s) => l.Replace(s, "***", StringComparison.Ordinal))).ToList();
    }

    /// <summary>
    /// <c>docuconf: DATABSE_URL is set but not declared; did you mean DATABASE_URL?</c> for each set variable that is
    /// not declared but is close to a declared name: an edit distance of at most 2, and at most 1 for names of up to
    /// 5 characters, so <c>HOST</c> is not taken for <c>PORT</c>. Values are never printed.
    /// </summary>
    public static IEnumerable<string> TypoHints(IReadOnlyCollection<VarSpec> declared, IEnumerable<string> environment)
    {
        var names = declared.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var set in environment.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (names.Contains(set) || IsItemOfDeclaredList(set, declared))
            {
                continue;
            }

            int limit = set.Length <= 5 ? 1 : 2;
            var match = names
                .Select(n => (Name: n, Distance: Distance(set, n, limit)))
                .Where(m => m.Distance <= limit)
                .OrderBy(m => m.Distance).ThenBy(m => m.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (match.Name is not null)
            {
                yield return $"docuconf: {set} is set but not declared; did you mean {match.Name}?";
            }
        }
    }

    private static bool IsItemOfDeclaredList(string name, IEnumerable<VarSpec> declared) =>
        declared.Any(v => v.IsListLike && name.StartsWith(v.Name + "__", StringComparison.Ordinal));

    /// <summary>Levenshtein distance, stopping early once it exceeds <paramref name="limit"/>.</summary>
    internal static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            int best = current[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }

            if (best > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>The names of the process environment variables.</summary>
    public static IEnumerable<string> ProcessEnvironment() =>
        Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => (string)k);

    /// <summary>
    /// Resolves <typeparamref name="T"/> and returns its failures, so a caller can report them with other classes'.
    /// </summary>
    public static IReadOnlyList<string> Resolve<T>(IServiceProvider services)
        where T : class
    {
        try
        {
            _ = services.GetRequiredService<IOptionsMonitor<T>>().Get(Options.DefaultName);
            return [];
        }
        catch (OptionsValidationException ex)
        {
            return ex.Failures.ToList();
        }
    }

    /// <summary>
    /// Throws when <typeparamref name="T"/> is also bound or validated by the host's own extensions: they would bind the
    /// section a second time, stop at the first value they cannot convert, and report every problem twice.
    /// </summary>
    public static void CheckConflicts<T>(IServiceProvider services)
        where T : class
    {
        var name = typeof(T).Name;
        var found = new List<string>();
        foreach (var configure in services.GetServices<IConfigureOptions<T>>())
        {
            var type = configure.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConfigureNamedOptions<,>)
                && type.GetGenericArguments()[1] == typeof(IConfiguration))
            {
                found.Add("BindConfiguration");
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NamedConfigureFromConfigurationOptions<>))
            {
                found.Add("Configure<" + name + ">(configuration) or Bind");
            }
        }

        if (services.GetServices<IValidateOptions<T>>().Any(v => v.GetType().IsGenericType
            && v.GetType().GetGenericTypeDefinition() == typeof(DataAnnotationValidateOptions<>)))
        {
            found.Add("ValidateDataAnnotations");
        }

        if (found.Count > 0)
        {
            throw new InvalidOperationException(
                $"{name} is also registered with {string.Join(" and ", found.Distinct())}. AddDocuconf<{name}>() binds the section and checks every DataAnnotation itself, "
                + "reporting every problem once; remove those calls (keep AddDocuconf).");
        }
    }

    /// <summary>
    /// Throws when the app was started as <c>app docuconf ...</c> but <see cref="DocuconfExport.RunIfRequested"/> did not
    /// handle it, so the export does not silently start the app instead.
    /// </summary>
    public static void CheckNotExportRun(string[] commandLine)
    {
        if (commandLine.Length > 1 && commandLine[1] == "docuconf")
        {
            var command = string.Join(' ', commandLine.Skip(1));
            throw new InvalidOperationException(
                $"'{command}' was requested but DocuconfExport.RunIfRequested(args) did not run. Call it first in Program.cs: if (DocuconfExport.RunIfRequested(args)) return;");
        }
    }

    /// <summary>The <c>IOptionsMonitor</c> path above, typed for a registration.</summary>
    public static DocuconfRegistry.Registration Registration<T>(DocuconfSettings settings)
        where T : class => new(typeof(T), settings, Resolve<T>, CheckConflicts<T>);
}

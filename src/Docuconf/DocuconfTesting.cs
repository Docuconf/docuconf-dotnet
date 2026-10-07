using Docuconf.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Docuconf;

/// <summary>
/// Validates an options class against an explicit environment, for unit tests. Nothing reads or changes the process
/// environment, no host or service provider is built, nothing is written to the termination log, and no background
/// thread is started.
/// </summary>
/// <example>
/// <code>
/// var problems = DocuconfTesting.Validate&lt;OrdersOptions&gt;(new() { ["ORDERS__PORT"] = "0" });
/// Assert.Contains(problems, p => p.Code == Codes.OutOfRange &amp;&amp; p.Input == "ORDERS__PORT");
/// </code>
/// </example>
public static class DocuconfTesting
{
    /// <summary>
    /// Binds <typeparamref name="T"/> from <paramref name="environment"/>, written as environment variables
    /// (<c>ORDERS__PORT</c>, <c>ORDERS__ALLOWEDORIGINS__0</c>), and returns every violation; empty when it is valid.
    /// </summary>
    /// <param name="environment">The whole environment the app would see.</param>
    /// <param name="configure">Settings such as <see cref="DocuconfSettings.FileRoot"/> for file inputs.</param>
    /// <exception cref="ContractException">The declaration of <typeparamref name="T"/> is invalid.</exception>
    public static IReadOnlyList<Violation> Validate<T>(IReadOnlyDictionary<string, string> environment, Action<DocuconfSettings>? configure = null)
        where T : class, new()
    {
        var (options, configuration) = Bind<T>(environment, configure);
        return new DocuconfValidateOptions<T>(configuration).Collect(options);
    }

    /// <summary>Binds and validates <typeparamref name="T"/> from <paramref name="environment"/> and returns it.</summary>
    /// <exception cref="OptionsValidationException">The environment is invalid; <c>Failures</c> lists every problem.</exception>
    public static T Load<T>(IReadOnlyDictionary<string, string> environment, Action<DocuconfSettings>? configure = null)
        where T : class, new()
    {
        var (options, configuration) = Bind<T>(environment, configure);
        var violations = new DocuconfValidateOptions<T>(configuration).Collect(options);
        return violations.Count == 0
            ? options
            : throw new OptionsValidationException(Options.DefaultName, typeof(T), violations.Select(v => v.ToString()));
    }

    private static (T Options, IConfiguration Configuration) Bind<T>(IReadOnlyDictionary<string, string> environment, Action<DocuconfSettings>? configure)
        where T : class, new()
    {
        var settings = new DocuconfSettings { TerminationLogPath = "" };
        configure?.Invoke(settings);

        // As the environment variables provider reads them: a double underscore separates sections.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(environment.Select(e => new KeyValuePair<string, string?>(e.Key.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal), e.Value)))
            .Build();
        // The file root comes from the given environment, never from the process's.
        settings.FileRoot ??= configuration["Docuconf:FileRoot"] ?? configuration["DOCUCONF_FILE_ROOT"] ?? "";
        var options = new T();
        new DocuconfConfigureOptions<T>(configuration, settings).Configure(options);
        return (options, configuration);
    }
}

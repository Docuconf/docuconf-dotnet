using System.Reflection;
using Docuconf.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Docuconf;

/// <summary>Runtime settings for docuconf.</summary>
public sealed class DocuconfSettings
{
    /// <summary>
    /// A directory prepended to every file input path, for local development and tests
    /// (<c>/etc/billing/tls</c> becomes <c>{FileRoot}/etc/billing/tls</c>). Defaults to the <c>Docuconf:FileRoot</c>
    /// configuration value (so it can live in <c>launchSettings.json</c> or <c>appsettings.Development.json</c>), then
    /// the <c>DOCUCONF_FILE_ROOT</c> environment variable, or none.
    /// </summary>
    public string? FileRoot { get; set; }

    /// <summary>Where startup failures are written. Defaults to <c>DOCUCONF_TERMINATION_LOG</c> or <c>/dev/termination-log</c>.</summary>
    public string? TerminationLogPath { get; set; }

    /// <summary>The current time, for certificate checks. Replaceable in tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// When invalid configuration stops the host, throw <see cref="OptionsValidationException"/> instead of printing
    /// the problems and exiting with status 1. Off by default.
    /// </summary>
    public bool ThrowOnInvalid { get; set; }

    internal TextWriter Error { get; set; } = Console.Error;

    internal Action<int> Exit { get; set; } = System.Environment.Exit;

    internal Func<IEnumerable<string>> EnvironmentNames { get; set; } = DocuconfStartup.ProcessEnvironment;
}

/// <summary>Registers docuconf options classes.</summary>
public static class DocuconfServiceCollectionExtensions
{
    /// <summary>
    /// Binds <typeparamref name="T"/> from the section named in its <see cref="ConfigContractAttribute"/>, loads its
    /// file inputs, and validates everything when the host starts. Invalid configuration stops the host: the problems
    /// are printed to stderr and the termination log, one per line, and the process exits with status 1.
    /// </summary>
    /// <remarks>
    /// This replaces <c>BindConfiguration</c>/<c>Bind</c> and <c>ValidateDataAnnotations</c> for <typeparamref name="T"/>:
    /// keeping them as well is reported at startup, because they would bind the section a second time.
    /// </remarks>
    /// <example>
    /// <code>builder.Services.AddDocuconf&lt;BillingOptions&gt;();</code>
    /// </example>
    public static OptionsBuilder<T> AddDocuconf<T>(this IServiceCollection services, Action<DocuconfSettings>? configure = null)
        where T : class
    {
        if (typeof(T).GetCustomAttribute<ConfigContractAttribute>() is null)
        {
            throw new InvalidOperationException($"{typeof(T).Name} needs [ConfigContract(\"service-name\")].");
        }

        DocuconfStartup.CheckNotExportRun(Environment.GetCommandLineArgs());

        var settings = new DocuconfSettings();
        configure?.Invoke(settings);

        services.AddSingleton<IConfigureOptions<T>>(sp => new DocuconfConfigureOptions<T>(sp.GetRequiredService<IConfiguration>(), settings));
        services.AddSingleton<IValidateOptions<T>>(sp => new DocuconfValidateOptions<T>(sp.GetRequiredService<IConfiguration>()));
        // Without a change token source IOptionsMonitor<T> never sees configuration reloads, such as an overlay
        // declared with ReloadOnChange (SPEC §4.7) or reloadOnChange appsettings.
        services.AddSingleton<IOptionsChangeTokenSource<T>>(sp => new ConfigurationChangeTokenSource<T>(sp.GetRequiredService<IConfiguration>()));

        // The host calls the IStartupValidator when it starts. docuconf's replaces the one ValidateOnStart registers,
        // and runs it after reporting docuconf's own problems, so ValidateOnStart keeps working for other options.
        var registry = Registry(services);
        if (registry.Types.All(r => r.Type != typeof(T)))
        {
            registry.Types.Add(DocuconfStartup.Registration<T>(settings));
        }

        services.RemoveAll<IStartupValidator>();
        services.AddSingleton<IStartupValidator, DocuconfStartupValidator>();
        return services.AddOptions<T>();
    }

    /// <summary>
    /// Adds <typeparamref name="T"/>'s config-file overlays to <c>builder.Configuration</c> and registers it with
    /// <see cref="AddDocuconf{T}(IServiceCollection, Action{DocuconfSettings}?)"/>, with the same settings.
    /// </summary>
    /// <example>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddDocuconf&lt;CatalogOptions&gt;();
    /// </code>
    /// </example>
    public static OptionsBuilder<T> AddDocuconf<T>(this IHostApplicationBuilder builder, Action<DocuconfSettings>? configure = null)
        where T : class
    {
        builder.Configuration.AddDocuconfOverlays<T>(configure);
        return builder.Services.AddDocuconf<T>(configure);
    }

    /// <summary>
    /// Returns the validated <typeparamref name="T"/>, or, when the configuration is invalid, prints every problem to
    /// stderr and the termination log and exits with status 1. Use it to read options before <c>app.Run()</c>.
    /// </summary>
    /// <example>
    /// <code>var orders = app.Services.LoadOrExit&lt;OrdersOptions&gt;();</code>
    /// </example>
    public static T LoadOrExit<T>(this IServiceProvider services)
        where T : class
    {
        var registry = services.GetService<DocuconfRegistry>();
        var registration = registry?.Types.FirstOrDefault(r => r.Type == typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered; call builder.Services.AddDocuconf<{typeof(T).Name}>() first.");
        DocuconfStartup.ValidateOrExit(services, registry!, [registration]);
        return services.GetRequiredService<IOptions<T>>().Value;
    }

    private static DocuconfRegistry Registry(IServiceCollection services)
    {
        if (services.FirstOrDefault(d => d.ServiceType == typeof(DocuconfRegistry))?.ImplementationInstance is DocuconfRegistry existing)
        {
            return existing;
        }

        var registry = new DocuconfRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}

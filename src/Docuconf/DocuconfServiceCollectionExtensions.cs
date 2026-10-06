using System.Reflection;
using Docuconf.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf;

/// <summary>Runtime settings for docuconf.</summary>
public sealed class DocuconfSettings
{
    /// <summary>
    /// A directory prepended to every file input path, for local development and tests
    /// (<c>/etc/billing/tls</c> becomes <c>{FileRoot}/etc/billing/tls</c>). Defaults to the
    /// <c>DOCUCONF_FILE_ROOT</c> environment variable, or none.
    /// </summary>
    public string? FileRoot { get; set; }

    /// <summary>Where startup failures are written. Defaults to <c>DOCUCONF_TERMINATION_LOG</c> or <c>/dev/termination-log</c>.</summary>
    public string? TerminationLogPath { get; set; }

    /// <summary>The current time, for certificate checks. Replaceable in tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
}

/// <summary>Registers docuconf options classes.</summary>
public static class DocuconfServiceCollectionExtensions
{
    /// <summary>
    /// Binds <typeparamref name="T"/> from the section named in its <see cref="ConfigContractAttribute"/>,
    /// loads its file inputs, and validates everything when the host starts.
    /// </summary>
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

        var settings = new DocuconfSettings();
        configure?.Invoke(settings);

        services.AddSingleton<IConfigureOptions<T>>(sp => new DocuconfConfigureOptions<T>(sp.GetRequiredService<IConfiguration>(), settings));
        services.AddSingleton<IValidateOptions<T>>(sp => new DocuconfValidateOptions<T>(sp.GetRequiredService<IConfiguration>(), settings));
        // Without a change token source IOptionsMonitor<T> never sees configuration reloads, such as an overlay
        // declared with ReloadOnChange (SPEC §4.7) or reloadOnChange appsettings.
        services.AddSingleton<IOptionsChangeTokenSource<T>>(sp => new ConfigurationChangeTokenSource<T>(sp.GetRequiredService<IConfiguration>()));
        return services.AddOptions<T>().ValidateOnStart();
    }
}

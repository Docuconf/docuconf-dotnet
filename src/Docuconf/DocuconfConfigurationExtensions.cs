using Docuconf.Contract;
using Docuconf.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Docuconf;

/// <summary>Loads the config-file overlays an options class declares (SPEC §4.7).</summary>
public static class DocuconfConfigurationExtensions
{
    private const string EnvironmentVariablesSource =
        "Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationSource";

    /// <summary>
    /// Adds each <see cref="ConfigOverlayAttribute"/> on <typeparamref name="T"/> (or its assembly) as an optional
    /// JSON file, between the appsettings files the app ships with and environment variables, so the order is
    /// always: <c>appsettings.json</c> &lt; <c>appsettings.{Environment}.json</c> &lt; overlays &lt; environment.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WebApplication.CreateBuilder</c> has already added environment variables when this runs, so each overlay
    /// is inserted before the last unprefixed environment variables source rather than appended.
    /// </para>
    /// <para>
    /// An overlay declared with <see cref="ConfigOverlayAttribute.ReloadOnChange"/> is watched by polling, because
    /// Kubernetes updates a mounted ConfigMap by swapping a symlink, which file system events can miss. Read its
    /// values through <c>IOptionsMonitor&lt;T&gt;</c> to see changes.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.Configuration.AddDocuconfOverlays&lt;CatalogOptions&gt;();
    /// builder.Services.AddDocuconf&lt;CatalogOptions&gt;();
    /// </code>
    /// </example>
    public static IConfigurationBuilder AddDocuconfOverlays<T>(this IConfigurationBuilder builder, Action<DocuconfSettings>? configure = null)
        where T : class
    {
        var settings = new DocuconfSettings();
        configure?.Invoke(settings);
        var root = DocuconfBinder.Root(settings, builder as IConfiguration);

        var index = builder.Sources.Count;
        for (var i = builder.Sources.Count - 1; i >= 0; i--)
        {
            var source = builder.Sources[i];
            if (source.GetType().FullName == EnvironmentVariablesSource
                && string.IsNullOrEmpty(source.GetType().GetProperty("Prefix")?.GetValue(source) as string))
            {
                index = i;
                break;
            }
        }

        foreach (var overlay in ContractReader.OverlaysOf([typeof(T)]).DistinctBy(o => o.Name))
        {
            var path = root.Length > 0 ? Path.Join(root, overlay.Path) : overlay.Path;
            CheckNotOverShippedFiles(overlay, path);
            builder.Sources.Insert(index++, Source(path, overlay.ReloadOnChange));
        }

        return builder;
    }

    private static JsonConfigurationSource Source(string path, bool reloadOnChange)
    {
        // The directory may not exist yet (local runs, a mount that arrives later): watch from the nearest
        // existing ancestor, as JsonConfigurationSource.ResolveFileProvider does.
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var relative = Path.GetFileName(path);
        while (!Directory.Exists(directory) && Path.GetDirectoryName(directory) is { } parent)
        {
            relative = Path.Join(Path.GetFileName(directory), relative);
            directory = parent;
        }

        var provider = new PhysicalFileProvider(directory)
        {
            UsePollingFileWatcher = reloadOnChange,
            UseActivePolling = reloadOnChange,
        };
        return new JsonConfigurationSource
        {
            FileProvider = provider,
            Path = relative,
            Optional = true,
            ReloadOnChange = reloadOnChange,
        };
    }

    // The platform mounts the overlay's directory, which hides whatever the image has there (SPEC §4.7).
    private static void CheckNotOverShippedFiles(ConfigOverlayAttribute overlay, string path)
    {
        var directory = Path.GetFullPath(Path.GetDirectoryName(path)!).TrimEnd(Path.DirectorySeparatorChar);
        var appDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(directory, appDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Overlay '{overlay.Name}' at {overlay.Path} is in the app's own directory; mounting it would hide the app's files. Use a directory of its own, such as /app/config.");
        }
    }
}

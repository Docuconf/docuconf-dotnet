using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

// Config-file overlays at runtime (SPEC §4.7). Sets a process environment variable, so it does not run in
// parallel with other tests that read the environment.
[Collection("environment")]
public sealed class OverlayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("docuconf-overlay-").FullName;
    private string OverlayFile => Path.Join(_root, "app", "config", "appsettings.Production.json");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CATALOG__SEARCHURL", null);
        Directory.Delete(_root, recursive: true);
    }

    private void WriteOverlay(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OverlayFile)!);
        File.WriteAllText(OverlayFile, json);
    }

    // As WebApplication.CreateBuilder leaves it: appsettings files, then environment variables.
    private IConfigurationBuilder Builder(Dictionary<string, string?> appsettings) =>
        new ConfigurationBuilder().AddInMemoryCollection(appsettings).AddEnvironmentVariables();

    [Fact]
    public void Overlay_sits_between_appsettings_and_the_environment()
    {
        WriteOverlay("""{ "Catalog": { "PageSize": 50, "SearchUrl": "https://overlay.example.com" } }""");
        Environment.SetEnvironmentVariable("CATALOG__SEARCHURL", "https://env.example.com");

        var builder = Builder(new() { ["Catalog:PageSize"] = "10", ["Catalog:CacheTtl"] = "00:02:00" })
            .AddDocuconfOverlays<CatalogOptions>(s => s.FileRoot = _root);
        var config = builder.Build();

        Assert.Equal("50", config["Catalog:PageSize"]);                         // overlay over appsettings
        Assert.Equal("00:02:00", config["Catalog:CacheTtl"]);                   // appsettings where the overlay is silent
        Assert.Equal("https://env.example.com", config["Catalog:SearchUrl"]);   // environment over overlay
        Assert.EndsWith("EnvironmentVariablesConfigurationSource", builder.Sources[^1].GetType().Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_overlay_is_not_an_error()
    {
        var config = Builder(new() { ["Catalog:PageSize"] = "10" })
            .AddDocuconfOverlays<CatalogOptions>(s => s.FileRoot = _root)
            .Build();

        Assert.Equal("10", config["Catalog:PageSize"]);
    }

    [Fact]
    public async Task A_watched_overlay_reloads_when_the_platform_updates_it()
    {
        WriteOverlay("""{ "Catalog": { "PageSize": 50, "SearchUrl": "https://search.internal" } }""");
        var config = Builder([]).AddDocuconfOverlays<CatalogOptions>(s => s.FileRoot = _root).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(config);
        services.AddDocuconf<CatalogOptions>(s => s.FileRoot = _root);
        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<IOptionsMonitor<CatalogOptions>>();
        Assert.Equal(50, monitor.CurrentValue.PageSize);

        var changed = new TaskCompletionSource();
        using var _ = monitor.OnChange(o => { if (o.PageSize == 75) changed.TrySetResult(); });
        WriteOverlay("""{ "Catalog": { "PageSize": 75, "SearchUrl": "https://search.internal" } }""");

        // The polling watcher checks every few seconds.
        Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)));
        Assert.Equal(75, monitor.CurrentValue.PageSize);
    }

    [Fact]
    public void Overlay_values_are_validated_like_any_other()
    {
        WriteOverlay("""{ "Catalog": { "PageSize": 1000, "SearchUrl": "http://insecure.example.com" } }""");
        var config = Builder([]).AddDocuconfOverlays<CatalogOptions>(s => s.FileRoot = _root).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(config);
        services.AddDocuconf<CatalogOptions>(s => s.FileRoot = _root);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<CatalogOptions>>().Value);

        Assert.Contains(ex.Failures, f => f.StartsWith("[out_of_range] CATALOG__PAGESIZE", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.StartsWith("[invalid_scheme] CATALOG__SEARCHURL", StringComparison.Ordinal));
    }

    [Fact]
    public void An_overlay_among_the_apps_own_files_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Builder([]).AddDocuconfOverlays<RootedOverlayOptions>(s => s.FileRoot = AppContext.BaseDirectory));
        Assert.Contains("hide the app's files", ex.Message, StringComparison.Ordinal);
    }
}

[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

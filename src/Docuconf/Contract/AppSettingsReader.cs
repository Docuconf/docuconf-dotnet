using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Docuconf.Contract;

/// <summary>
/// Reads the appsettings files the app ships with into the contract (SPEC §4.4):
/// <c>appsettings.json</c> values become defaults, <c>appsettings.{Environment}.json</c> values become profiles.
/// </summary>
internal static class AppSettingsReader
{
    public static void Apply(ContractModel model, string contentRoot, string selector, List<string> errors)
    {
        var basePath = Path.Combine(contentRoot, "appsettings.json");
        if (File.Exists(basePath))
        {
            var config = Load(basePath);
            foreach (var spec in model.Vars.Values)
            {
                if (Read(config, spec, "appsettings.json", errors) is { } value)
                {
                    spec.Default = value;
                    spec.Required = false;
                }
            }
        }

        var profiles = new ProfilesSpec { Selector = selector };
        var profileFiles = Directory.Exists(contentRoot)
            ? Directory.GetFiles(contentRoot, "appsettings.*.json").Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        foreach (var file in profileFiles)
        {
            var fileName = Path.GetFileName(file);
            var environment = fileName["appsettings.".Length..^".json".Length];
            if (environment.Length == 0 || environment.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            var config = Load(file);
            var defaults = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var spec in model.Vars.Values)
            {
                if (Read(config, spec, fileName, errors) is { } value)
                {
                    defaults[spec.Name] = value;
                }
            }

            if (defaults.Count > 0)
            {
                profiles.Defaults[environment] = defaults;
            }
        }

        if (profiles.Defaults.Count == 0)
        {
            return;
        }

        model.Profiles = profiles;
        if (!model.Vars.ContainsKey(selector))
        {
            model.Vars[selector] = new VarSpec
            {
                Name = selector,
                ConfigKey = "",
                Type = VarType.String,
                Description = "Hosting environment; selects appsettings.{Environment}.json",
                Default = profiles.Default,
            };
        }
    }

    private static IConfigurationRoot Load(string path) =>
        new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build();

    private static object? Read(IConfiguration config, VarSpec spec, string fileName, List<string> errors)
    {
        if (spec.ConfigKey.Length == 0)
        {
            return null;
        }

        var section = config.GetSection(spec.ConfigKey);
        if (!section.Exists())
        {
            return null;
        }

        if (spec.Secret)
        {
            errors.Add($"{fileName} sets {spec.ConfigKey}, which is [Secret]. A secret in appsettings ships inside the image; supply it from a Kubernetes Secret instead.");
            return null;
        }

        object? value;
        try
        {
            value = Convert(section, spec);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            errors.Add($"{fileName}: {spec.ConfigKey} is not a valid {spec.Type.ToString().ToLowerInvariant()}: {ex.Message}");
            return null;
        }

        if (value is not null && Constraints.Check(spec, value) is { } problem)
        {
            errors.Add($"{fileName}: {spec.ConfigKey} = {Constraints.Show(value)} {problem}.");
            return null;
        }

        return value;
    }

    private static object? Convert(IConfigurationSection section, VarSpec spec)
    {
        if (spec.Type == VarType.List)
        {
            var items = section.GetChildren()
                .OrderBy(c => int.TryParse(c.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : int.MaxValue)
                .Select(c => c.Value ?? "")
                .ToList();
            return items.Count == 0
                ? null
                : spec.Items == "int"
                    ? items.Select(i => (object)long.Parse(i, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToList()
                    : items.Select(i => (object)i).ToList();
        }

        var raw = section.Value;
        if (raw is null || (raw.Length == 0 && spec.Type != VarType.String))
        {
            return null;
        }

        return spec.Type switch
        {
            VarType.Int => long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture),
            VarType.Float => double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture),
            VarType.Bool => bool.Parse(raw),
            VarType.Duration => GoDuration.Format(TimeSpanParser.Parse(raw)),
            VarType.Enum => spec.Values!.FirstOrDefault(v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"'{raw}' is not one of {string.Join(", ", spec.Values!)}"),
            _ => raw,
        };
    }
}

/// <summary>
/// TimeSpan parsing for configuration values. Rejects bare numbers: <c>TimeSpan.Parse("30")</c> is 30 days,
/// which is never what someone writing "30" meant.
/// </summary>
internal static class TimeSpanParser
{
    public static TimeSpan Parse(string raw)
    {
        if (!raw.Contains(':', StringComparison.Ordinal))
        {
            throw new FormatException($"'{raw}' is ambiguous as a TimeSpan; write it as hh:mm:ss, for example 00:00:30.");
        }

        return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);
    }
}

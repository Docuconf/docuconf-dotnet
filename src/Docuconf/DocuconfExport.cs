using System.Reflection;
using Docuconf.Contract;

namespace Docuconf;

/// <summary>
/// Writes the app's <c>contract.cue</c>. Call <see cref="RunIfRequested"/> first thing in <c>Program.cs</c>, then run
/// the built app with <c>docuconf export</c>:
/// <code>dotnet bin/Release/net10.0/publish/Billing.Api.dll docuconf export contract.cue</code>
/// Run it against the publish output: the contract includes the appsettings files that ship with the app.
/// </summary>
public static class DocuconfExport
{
    private const string Usage = "usage: <app> docuconf export <output.cue> [--content-root <dir>] [--app-version <version>] [--profile-selector <ENV_VAR>]";

    /// <summary>
    /// When <paramref name="args"/> start with <c>docuconf export</c>, writes the contract and returns true; the caller
    /// should then return without starting the app. Otherwise returns false.
    /// </summary>
    public static bool RunIfRequested(string[] args, Assembly? assembly = null)
    {
        if (args.Length < 2 || args[0] != "docuconf" || args[1] != "export")
        {
            return false;
        }

        assembly ??= Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("No entry assembly.");
        if (args.Length < 3 || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(Usage);
            Environment.ExitCode = 2;
            return true;
        }

        string? Option(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        try
        {
            var cue = ToCue(assembly, new ContractReadSettings
            {
                ContentRoot = Option("--content-root") ?? AppContext.BaseDirectory,
                ProfileSelector = Option("--profile-selector"),
            }, Option("--app-version"), out var warnings);
            File.WriteAllText(args[2], cue);
            foreach (var warning in warnings)
            {
                Console.Error.WriteLine("warning: " + warning);
            }

            Console.WriteLine($"Wrote {args[2]}");
        }
        catch (ContractException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
        }

        return true;
    }

    /// <summary>Reads every <see cref="ConfigContractAttribute"/> class in <paramref name="assembly"/> and writes the contract.</summary>
    public static string ToCue(Assembly assembly, ContractReadSettings? settings, string? appVersion, out IReadOnlyList<string> warnings)
    {
        var model = ContractReader.Read(assembly, settings);
        warnings = model.Warnings;
        return CueWriter.Write(model, GeneratorInfo, appVersion);
    }

    /// <summary>The SDK name and version recorded in contracts.</summary>
    public static CueWriter.Generator GeneratorInfo { get; } = new(
        "Docuconf.Options",
        typeof(DocuconfExport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");
}

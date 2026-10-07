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
    private const string Usage =
        "usage: <app> docuconf export <output.cue|output.json|-> [--format cue|json] [--check] [--content-root <dir>] [--app-version <version>] [--profile-selector <ENV_VAR>]";

    /// <summary>
    /// When <paramref name="args"/> start with <c>docuconf</c>, runs the docuconf command and returns true; the caller
    /// should then return without starting the app. Otherwise returns false.
    /// </summary>
    /// <remarks>
    /// <c>docuconf export &lt;file&gt;</c> writes the contract: CUE, or JSON when the file ends in <c>.json</c> or
    /// <c>--format json</c> is given. <c>-</c> writes it to stdout. With <c>--check</c> nothing is written: the command
    /// exits with status 1 when the file differs from a fresh export, for CI. Any other <c>docuconf</c> command is a
    /// usage error (exit status 2), so a typo never starts the app instead.
    /// </remarks>
    public static bool RunIfRequested(string[] args, Assembly? assembly = null)
    {
        if (args.Length < 1 || args[0] != "docuconf")
        {
            return false;
        }

        Environment.ExitCode = Run(args, assembly ?? Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("No entry assembly."), Console.Out, Console.Error);
        return true;
    }

    /// <summary>Runs a <c>docuconf</c> command and returns the exit status.</summary>
    internal static int Run(string[] args, Assembly assembly, TextWriter stdout, TextWriter stderr)
    {

        if (args.Length < 3 || args[1] != "export" || args[2].StartsWith("--", StringComparison.Ordinal))
        {
            if (args.Length >= 2 && args[1] != "export")
            {
                stderr.WriteLine($"docuconf: unknown command '{args[1]}'.");
            }

            stderr.WriteLine(Usage);
            return 2;
        }

        string? Option(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var output = args[2];
        var format = Option("--format") ?? (output.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "cue");
        if (format is not ("cue" or "json"))
        {
            stderr.WriteLine($"docuconf: --format must be cue or json, not '{format}'.");
            stderr.WriteLine(Usage);
            return 2;
        }

        bool check = args.Contains("--check");
        try
        {
            var model = ContractReader.Read(assembly, new ContractReadSettings
            {
                ContentRoot = Option("--content-root") ?? AppContext.BaseDirectory,
                ProfileSelector = Option("--profile-selector"),
            });
            var text = format == "json"
                ? CueWriter.WriteJson(model, GeneratorInfo, Option("--app-version"))
                : CueWriter.Write(model, GeneratorInfo, Option("--app-version"));
            foreach (var warning in model.Warnings)
            {
                stderr.WriteLine("warning: " + warning);
            }

            if (check)
            {
                var current = File.Exists(output) ? File.ReadAllText(output).Replace("\r\n", "\n", StringComparison.Ordinal) : null;
                if (current != text)
                {
                    stderr.WriteLine(current is null
                        ? $"docuconf: {output} does not exist; run docuconf export {output} to write it."
                        : $"docuconf: {output} is out of date; run docuconf export {output} again and commit it.");
                    return 1;
                }

                stdout.WriteLine($"{output} is up to date");
            }
            else if (output == "-")
            {
                stdout.Write(text);
            }
            else
            {
                File.WriteAllText(output, text);
                stdout.WriteLine($"Wrote {output}");
            }
        }
        catch (ContractException ex)
        {
            stderr.WriteLine(ex.Message);
            return 1;
        }

        return 0;
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

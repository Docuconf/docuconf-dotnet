using System.Reflection;
using Docuconf.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Docuconf.Tests;

/// <summary>Compiles declarations the way a user's project would, for the analyzer and for export tests.</summary>
internal static class Compiler
{
    private static readonly string Usings = """
        global using System;
        global using System.Collections.Generic;
        global using System.ComponentModel;
        global using System.ComponentModel.DataAnnotations;
        global using Docuconf;

        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Append(typeof(ConfigContractAttribute).Assembly.Location)
            .Distinct()
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList());

    public static CSharpCompilation Compilation(string source) =>
        CSharpCompilation.Create(
            "Decl" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(Usings + source, new CSharpParseOptions(LanguageVersion.Latest))],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>The analyzer's diagnostics for <paramref name="source"/>, as <c>ID: message</c>.</summary>
    public static async Task<IReadOnlyList<string>> Analyze(string source)
    {
        var compilation = Compilation(source);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "the test source does not compile: " + string.Join("; ", errors));
        var diagnostics = await compilation.WithAnalyzers([new DeclarationAnalyzer()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).Select(d => $"{d.Id}: {d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)}").ToList();
    }

    /// <summary>Compiles and loads <paramref name="source"/>.</summary>
    public static Assembly Load(string source)
    {
        using var stream = new MemoryStream();
        var result = Compilation(source).Emit(stream);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }
}

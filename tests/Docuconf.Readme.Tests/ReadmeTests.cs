using System.Text.RegularExpressions;

namespace Docuconf.Readme.Tests;

/// <summary>
/// Every C# and XML block in README.md is compiled code: it must appear, line for line, in a file the build compiles.
/// The console block is checked by examples/orders/smoke.sh, and the sh blocks by scripts/smoke-consumer.sh and CI.
/// </summary>
public sealed partial class ReadmeTests
{
    [GeneratedRegex(@"^```(?<lang>\w*)\n(?<body>.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Block();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Docuconf.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Docuconf.slnx not found above " + AppContext.BaseDirectory);
    }

    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static bool ContainsRun(List<string> haystack, List<string> needle)
    {
        for (int i = 0; i + needle.Count <= haystack.Count; i++)
        {
            if (haystack.Skip(i).Take(needle.Count).SequenceEqual(needle, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static TheoryData<string, string> Blocks()
    {
        var readme = File.ReadAllText(Path.Join(RepoRoot(), "README.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var data = new TheoryData<string, string>();
        foreach (Match m in Block().Matches(readme))
        {
            if (m.Groups["lang"].Value is "csharp" or "xml")
            {
                data.Add(m.Groups["lang"].Value, m.Groups["body"].Value);
            }
        }

        return data;
    }

    [Fact]
    public void The_readme_has_compiled_snippets() => Assert.True(Blocks().Count >= 8);

    [Theory]
    [MemberData(nameof(Blocks))]
    public void Every_readme_snippet_is_compiled(string lang, string block)
    {
        var root = RepoRoot();
        var sources = new[] { "examples/orders", "tests/Docuconf.Readme.Tests" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Join(root, d), "*.*", SearchOption.TopDirectoryOnly))
            .Where(f => f.EndsWith(lang == "xml" ? ".csproj" : ".cs", StringComparison.Ordinal));

        Assert.True(
            sources.Any(f => ContainsRun(Lines(File.ReadAllText(f)), Lines(block))),
            $"This README {lang} block is not in examples/orders or tests/Docuconf.Readme.Tests, so it is not compiled:\n{block}");
    }
}

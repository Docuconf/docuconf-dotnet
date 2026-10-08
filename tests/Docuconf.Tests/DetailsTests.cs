using System.Xml.Linq;
using Docuconf.Contract;

namespace Docuconf.Tests;

/// <summary>Descriptions from <c>[Description]</c> or the XML doc summary, details from the remarks (SPEC §14.7).</summary>
public sealed class DetailsTests
{
    private const string Declaration = """
        namespace Shop;

        [ConfigContract("shop", Section = "Shop")]
        public sealed class ShopOptions
        {
            /// <summary>
            /// Background workers that process
            /// new orders.
            /// </summary>
            /// <remarks>
            /// <para>Each worker holds one connection to <see cref="DatabaseUrl"/>; <see langword="null"/> is never
            /// valid, and <paramref name="x"/> is shown as code. See <see href="https://example.com/pool">the pool guide</see>.</para>
            /// <list type="bullet">
            /// <item><description>Raise it when the queue backs up.</description></item>
            /// <item><term>Lower</term><description>when the database is busy.</description></item>
            /// </list>
            /// <para>Set it with <c>SHOP__WORKERS</c>:</para>
            /// <code language="sh">
            /// SHOP__WORKERS=8
            ///   dotnet Shop.dll
            /// </code>
            /// <list type="number">
            /// <item><description>first</description></item>
            /// <item><description>second</description></item>
            /// </list>
            /// </remarks>
            [Range(1, 64)]
            public int Workers { get; set; } = 4;

            /// <summary>Ignored: the attribute wins.</summary>
            /// <remarks>Only details.</remarks>
            [Required, Secret, UrlSchemes("postgres"), Description("Database connection string")]
            public string DatabaseUrl { get; set; } = "";

            [Description("Region the shop runs in")]
            public string Region { get; set; } = "eu";

            /// <summary>Nested settings file.</summary>
            /// <remarks>Mounted from a ConfigMap.</remarks>
            [TextFile("/etc/shop/banner.txt")]
            public string? Banner { get; set; }

            public Inner Limits { get; set; } = new();

            public sealed class Inner
            {
                /// <summary>Most items in one order.</summary>
                /// <remarks>Plain text remarks.
                ///
                /// A second paragraph after a blank line.</remarks>
                [Range(1, 100)]
                public int MaxItems { get; set; } = 10;
            }
        }
        """;

    private static ContractModel Read(string source) => ContractReader.Read(Compiler.LoadWithDocs(source));

    [Fact]
    public void Description_comes_from_the_summary_when_there_is_no_Description_attribute()
    {
        var model = Read(Declaration);

        Assert.Equal("Background workers that process new orders", model.Vars["SHOP__WORKERS"].Description);
        Assert.Equal("Database connection string", model.Vars["SHOP__DATABASEURL"].Description);
        Assert.Equal("Region the shop runs in", model.Vars["SHOP__REGION"].Description);
        Assert.Null(model.Vars["SHOP__REGION"].Details);
        Assert.Equal("Most items in one order", model.Vars["SHOP__LIMITS__MAXITEMS"].Description);
        Assert.Equal("Nested settings file", model.Files["banner"].Description);
    }

    [Fact]
    public void Details_are_the_remarks_as_CommonMark()
    {
        var model = Read(Declaration);

        Assert.Equal(
            """
            Each worker holds one connection to `ShopOptions.DatabaseUrl`; `null` is never valid, and `x` is shown as code. See [the pool guide](https://example.com/pool).

            - Raise it when the queue backs up.
            - **Lower**: when the database is busy.

            Set it with `SHOP__WORKERS`:

            ```sh
            SHOP__WORKERS=8
              dotnet Shop.dll
            ```

            1. first
            2. second
            """.ReplaceLineEndings("\n"),
            model.Vars["SHOP__WORKERS"].Details);
        Assert.Equal("Only details.", model.Vars["SHOP__DATABASEURL"].Details);
        Assert.Equal("Plain text remarks.\n\nA second paragraph after a blank line.", model.Vars["SHOP__LIMITS__MAXITEMS"].Details);
        Assert.Equal("Mounted from a ConfigMap.", model.Files["banner"].Details);
    }

    [Fact]
    public void Details_are_exported_after_the_description_and_pass_the_meta_schema()
    {
        var model = Read(Declaration);
        var cue = CueWriter.Write(model, new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));
        var json = CueWriter.WriteJson(model, new CueWriter.Generator("Docuconf.Options", "0.1.0-test"));

        Assert.Matches("description: \"Background workers that process new orders\"\n\\s*details: \"Each worker holds one connection", cue);
        Assert.Matches("\"description\": \"Nested settings file\",\n\\s*\"details\": \"Mounted from a ConfigMap.\",", json);

        var module = ExportTests.CueModule(cue);
        var (exit, output) = ExportTests.Cue(module, "vet", "-c", "./out");
        Assert.True(exit == 0, output);
    }

    [Fact]
    public void A_missing_description_fails()
    {
        var ex = Assert.Throws<ContractException>(() => Read("""
            [ConfigContract("shop")]
            public sealed class ShopOptions
            {
                /// <remarks>Details, but no summary.</remarks>
                public int Workers { get; set; } = 4;

                /// <summary>Tiny</summary>
                public int Other { get; set; } = 4;
            }
            """));

        Assert.Contains(ex.Errors, e => e.StartsWith("Workers: add [Description(\"...\")] or a /// <summary>", StringComparison.Ordinal));
        Assert.Contains(ex.Errors, e => e.StartsWith("Other: add [Description(\"...\")] or a /// <summary>", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_an_XML_doc_file_only_the_attribute_counts()
    {
        var ex = Assert.Throws<ContractException>(() => ContractReader.Read(Compiler.Load("""
            [ConfigContract("shop")]
            public sealed class ShopOptions
            {
                /// <summary>Background workers.</summary>
                public int Workers { get; set; } = 4;
            }
            """)));

        Assert.Contains(ex.Errors, e => e.StartsWith("Workers: add [Description", StringComparison.Ordinal));
    }

    [Fact]
    public void Details_over_4000_code_points_fail()
    {
        string Source(int n) => $$"""
            [ConfigContract("shop")]
            public sealed class ShopOptions
            {
                /// <summary>Background workers.</summary>
                /// <remarks>{{string.Concat(Enumerable.Repeat("日本", n))}}</remarks>
                public int Workers { get; set; } = 4;
            }
            """;

        // 2000 times 日本 is 4000 code points: the most details may have.
        Assert.Equal(4000, Read(Source(2000)).Vars["WORKERS"].Details!.Length);
        var ex = Assert.Throws<ContractException>(() => Read(Source(2001)));
        Assert.Contains("Workers: the <remarks> of its XML doc comment are its details: details are 4002 characters (Unicode code points); the most is 4000.", ex.Errors);
    }

    [Fact]
    public async Task The_analyzer_accepts_a_summary_when_doc_comments_are_compiled()
    {
        const string source = """
            [ConfigContract("shop")]
            public sealed class ShopOptions
            {
                /// <summary>Background workers.</summary>
                public int Workers { get; set; } = 4;

                /// <summary>Tiny</summary>
                public int Other { get; set; } = 4;
            }
            """;

        Assert.Equal(["DOCUCONF001: Other: add [Description(\"...\")] or a /// <summary> (with GenerateDocumentationFile) of at least 5 characters; every input in a contract is documented"],
            await Compiler.Analyze(source, docs: true));
        // A plain comment is not documentation.
        Assert.Equal(2, (await Compiler.Analyze(source.Replace("/// <summary>", "// <summary>", StringComparison.Ordinal))).Count);
    }

    [Fact]
    public void Code_points_count_surrogate_pairs_once()
    {
        Assert.Equal(2, XmlDocs.CodePoints("日😀"));
        Assert.Null(XmlDocs.DetailsProblem(string.Concat(Enumerable.Repeat("😀", 4000))));
        Assert.NotNull(XmlDocs.DetailsProblem(string.Concat(Enumerable.Repeat("😀", 4001))));
        Assert.Equal("details must not be blank", XmlDocs.DetailsProblem(" \n "));
    }

    [Fact]
    public void Converts_line_breaks_and_emphasis()
    {
        var remarks = XElement.Parse("<remarks>One<br/>two <b>bold</b> and <i>it</i>, <see cref=\"T:Shop.ShopOptions\"/>.</remarks>");
        Assert.Equal("One\\\ntwo **bold** and *it*, `ShopOptions`.", XmlDocs.Markdown(remarks));
        Assert.Equal("Uses ShopOptions", XmlDocs.PlainText(XElement.Parse("<summary> Uses <see cref=\"T:Shop.ShopOptions\"/>. </summary>")));
    }

    [Fact]
    public void Contract_first_loads_a_contract_with_details_and_ignores_them()
    {
        var contract = DocuconfContract.FromJson("""
            {
              "apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract", "metadata": {"name": "shop"},
              "vars": {"PORT": {"type": "int", "description": "Port to listen on", "details": "# Why\n\nBehind the mesh, keep the default.", "default": 8080}},
              "files": {"banner": {"type": "text", "description": "Banner text", "details": "Docs **only**.", "path": "/etc/shop/banner.txt"}}
            }
            """);

        Assert.Equal(9090L, contract.Load(new Dictionary<string, string> { ["PORT"] = "9090" }).Get<long>("PORT"));
    }

    [Fact]
    public void Contract_first_rejects_details_the_meta_schema_rejects()
    {
        string Contract(string details) =>
            """{"apiVersion": "docuconf.dev/v1alpha1", "kind": "ConfigContract", "metadata": {"name": "shop"}, "vars": {"PORT": {"type": "int", "description": "Port to listen on", "details": """
            + System.Text.Json.JsonSerializer.Serialize(details) + "}}}";

        Assert.Contains("vars.PORT: details must not be blank.", Assert.Throws<ContractException>(() => DocuconfContract.FromJson(Contract("  "))).Errors);
        Assert.Contains(
            "vars.PORT: details are 4002 characters (Unicode code points); the most is 4000.",
            Assert.Throws<ContractException>(() => DocuconfContract.FromJson(Contract(string.Concat(Enumerable.Repeat("日本", 2001))))).Errors);
    }
}

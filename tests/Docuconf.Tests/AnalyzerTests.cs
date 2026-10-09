namespace Docuconf.Tests;

/// <summary>The analyzer reports declaration errors at build, with the words export and startup use.</summary>
public sealed class AnalyzerTests
{
    [Fact]
    public async Task A_valid_declaration_has_no_diagnostics()
    {
        var diagnostics = await Compiler.Analyze("""
            [ConfigContract("orders-api", Section = "Orders")]
            public sealed class OrdersOptions
            {
                [Range(1, 65535), Description("HTTP listen port")]
                public int Port { get; set; } = 8080;

                [AllowedValues("debug", "info"), Description("Minimum log level")]
                public string LogLevel { get; set; } = "info";

                [Required, Secret, UrlSchemes("postgres"), Description("Database connection string")]
                public string DatabaseUrl { get; set; } = "";

                [MinLength(1), Description("Origins allowed to call the API")]
                public List<string> AllowedOrigins { get; set; } = ["http://localhost:3000"];

                [Range(typeof(TimeSpan), "00:00:01", "00:05:00"), Description("Time allowed per request")]
                public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

                [Required, Range(1, 64), Description("Background workers")]
                public int WorkerCount { get; set; }

                [EnvName("LOG_FORMAT"), Description("Log output format")]
                public string LogFormat { get; set; } = "json";

                [External("appsettings")]
                public Dictionary<string, string> Headers { get; set; } = [];

                public Nested Child { get; set; } = new();
            }

            public sealed class Nested
            {
                [Description("A nested setting")]
                public bool Enabled { get; set; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Reports_every_declaration_error()
    {
        var diagnostics = await Compiler.Analyze("""
            [ConfigContract("Orders_API", Section = "Orders")]
            public sealed record OrdersOptions
            {
                [Range(1, 65535)]
                public int Port { get; set; } = 8080;

                [Secret, Description("Database connection string")]
                public string DatabaseUrl { get; set; } = "postgres://localhost/orders";

                [UrlSchemes("https"), Description("Upstream service")]
                public int Upstream { get; set; } = 1;

                [Range(1, 64), Description("Background workers")]
                public int WorkerCount { get; set; }

                [AllowedValues("debug", "info"), Description("Minimum log level")]
                public string LogLevel { get; set; } = "trace";

                [EnvName("log-format"), Description("Log output format")]
                public string LogFormat { get; set; } = "json";

                [RegularExpression("^(?=a).*$"), Description("Tenant identifier")]
                public string Tenant { get; set; } = "";

                [Description("Extra response headers")]
                public Dictionary<string, string> Headers { get; set; } = [];

                [TextFile("/etc/orders/licence.key"), Description("Licence key")]
                public int Licence { get; set; }
            }
            """);

        Assert.Equal(
        [
            "DOCUCONF006: Service name 'Orders_API' must be a DNS label: lowercase letters, digits and hyphens",
            "DOCUCONF001: Orders:Port: add [Description(\"...\")] or a /// <summary> (with GenerateDocumentationFile) of at least 5 characters; every input in a contract is documented",
            "DOCUCONF008: Orders:DatabaseUrl: OrdersOptions is a record, so its generated ToString prints this [Secret] value; make OrdersOptions a class, or declare PrintMembers to leave secrets out",
            "DOCUCONF002: Orders:DatabaseUrl: a [Secret] value cannot have a default; remove the initializer",
            "DOCUCONF003: Orders:Upstream: [UrlSchemes] applies to string or Uri properties, but Int32 is exported as an int variable; remove the attribute or change the property's type",
            "DOCUCONF004: Orders:WorkerCount: the default 0 is below the minimum 1; add a valid initializer or mark it [Required]",
            "DOCUCONF004: Orders:LogLevel: the default \"trace\" is not one of debug, info; add a valid initializer or mark it [Required]",
            "DOCUCONF005: Orders:LogFormat: environment variable name 'log-format' must be UPPER_SNAKE_CASE",
            "DOCUCONF007: Orders:Tenant: pattern '^(?=a).*$' uses .NET-only regex features (lookaround, backreferences, atomic groups); the platform and other SDKs need RE2",
            "DOCUCONF009: Orders:Headers: a dictionary cannot be described by the contract, but the app would still read it; mark it [JsonVar] to make it one JSON variable, or [External(\"appsettings\")] to keep it out of the contract",
            "DOCUCONF010: Orders:Licence: a [TextFile] property must be of type String",
        ], diagnostics);
    }

    [Theory]
    [InlineData("[Range(1, 10)] public string Name { get; set; } = \"\";", "[Range] applies to numbers and TimeSpan, but String is exported as a string variable")]
    [InlineData("[AllowedValues(1, 2)] public int Level { get; set; } = 1;", "[AllowedValues] applies to string properties, but Int32 is exported as an int variable")]
    [InlineData("[RegularExpression(\"^a$\")] public int Code { get; set; } = 1;", "[RegularExpression] applies to string properties, but Int32 is exported as an int variable")]
    [InlineData("[MinLength(1)] public int Count { get; set; } = 1;", "[MinLength] applies to strings, urls and lists, but Int32 is exported as an int variable")]
    [InlineData("[ItemRange(0, 9)] public List<string> Names { get; set; } = [];", "[ItemRange] applies to lists of integers, such as int[] or List<long>, but List is exported as a list variable")]
    [InlineData("[ItemLength(2, 4)] public List<long> Shards { get; set; } = [];", "[ItemLength] applies to lists of strings, such as string[] or List<string>, but List is exported as a list variable")]
    [InlineData("[Csv] public string Name { get; set; } = \"\";", "[Csv] applies to lists, such as string[] or List<string>, but String is exported as a string variable")]
    public async Task A_constraint_that_does_not_fit_the_type_is_an_error(string property, string expected)
    {
        var diagnostics = await Compiler.Analyze($$"""
            [ConfigContract("svc", Section = "Svc")]
            public sealed class SvcOptions
            {
                [Description("A setting for the test")]
                {{property}}
            }
            """);

        Assert.Contains(diagnostics, d => d.StartsWith("DOCUCONF003: ", StringComparison.Ordinal) && d.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Length_constraints_on_urls_and_string_lists_are_fine()
    {
        var diagnostics = await Compiler.Analyze("""
            [ConfigContract("svc", Section = "Svc")]
            public sealed class SvcOptions
            {
                [UrlSchemes("https"), MaxLength(200), Description("Where to report each run")]
                public string? Callback { get; set; }

                [Url, StringLength(30), Description("Database connection string")]
                public System.Uri? DbUrl { get; set; }

                [ItemLength(2, 4), Description("Branch codes, two to four characters each")]
                public string[] Branches { get; set; } = [];
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_record_that_leaves_secrets_out_of_PrintMembers_is_fine()
    {
        var diagnostics = await Compiler.Analyze("""
            [ConfigContract("svc", Section = "Svc")]
            public sealed record SvcOptions
            {
                [Required, Secret, Description("API token for the partner")]
                public string Token { get; set; } = "";

                private bool PrintMembers(System.Text.StringBuilder builder) => false;
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Classes_without_ConfigContract_are_ignored()
    {
        Assert.Empty(await Compiler.Analyze("""
            public sealed class Plain
            {
                [Range(1, 10)] public string Name { get; set; } = "";
            }
            """));
    }
}

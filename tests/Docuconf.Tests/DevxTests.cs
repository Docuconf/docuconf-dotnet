using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Docuconf.Contract;
using Docuconf.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

[ConfigContract("shop", Section = "Shop")]
public sealed class ShopOptions
{
    [Range(1, 65535)]
    [Description("HTTP listen port")]
    public int Port { get; set; } = 8080;

    [EnvName("LOG_LEVEL"), AllowedValues("debug", "info", "warn")]
    [Description("Minimum log level")]
    public string LogLevel { get; set; } = "info";

    [Required, Secret, UrlSchemes("postgres")]
    [Description("Database connection string")]
    public string DatabaseUrl { get; set; } = "";

    [Description("Tags attached to every order")]
    public List<string> Tags { get; set; } = ["a"];

    [Range(1, 10, ErrorMessage = "Retries must be 1 to 10.")]
    [Description("Retries for a failed payment")]
    public int Retries { get; set; } = 3;

    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    [Description("Time allowed for one request")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

[ConfigContract("bad-url", Section = "Bad")]
public sealed class SchemesOnIntOptions
{
    [UrlSchemes("https"), Description("Upstream service")]
    public int Upstream { get; set; } = 1;
}

[ConfigContract("bad-allowed", Section = "Bad")]
public sealed class AllowedOnIntOptions
{
    [AllowedValues(1, 2), Description("Compression level")]
    public int Level { get; set; } = 1;
}

[ConfigContract("bad-range", Section = "Bad")]
public sealed class RangeOnStringOptions
{
    [Range(1, 10), Description("Name of the region")]
    public string Region { get; set; } = "eu";
}

[ConfigContract("bad-dictionary", Section = "Bad")]
public sealed class DictionaryOptions
{
    [Description("Extra response headers")]
    public Dictionary<string, string> Headers { get; set; } = [];
}

[ConfigContract("bad-record", Section = "Bad")]
public sealed record RecordOptions
{
    [Required, Secret]
    [Description("API token for the partner")]
    public string Token { get; set; } = "";
}

public sealed class SecretEchoValidator : IValidateOptions<ShopOptions>
{
    public ValidateOptionsResult Validate(string? name, ShopOptions options) =>
        ValidateOptionsResult.Fail($"database {options.DatabaseUrl} is not reachable");
}

public sealed class DevxTests : IDisposable
{
    private const string Secret = "postgres://shop:hunter2-pw@db:5432/shop";
    private readonly GatewayFiles _files = new();

    public void Dispose() => _files.Dispose();

    private static Dictionary<string, string?> Valid() => new() { ["Shop:DatabaseUrl"] = Secret };

    // P0 1: a bad environment ends the process with one clean report, not a stack trace.
    [Fact]
    public void Invalid_configuration_prints_one_line_per_problem_and_exits_1()
    {
        var result = _files.Start<ShopOptions>(new() { ["Shop:Port"] = "0" });

        const string Expected = """
            docuconf: 2 configuration problems:
              [missing_required] SHOP__DATABASEURL: is required (Shop:DatabaseUrl)
              [out_of_range] SHOP__PORT: '0' is below the minimum 1

            """;
        Assert.Equal(Expected.Replace("\r\n", "\n", StringComparison.Ordinal), result.Stderr);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(result.Stderr, result.TerminationLog);
    }

    [Fact]
    public void Valid_configuration_starts_silently()
    {
        var result = _files.Start<ShopOptions>(Valid());

        Assert.Equal("", result.Stderr);
        Assert.Null(result.ExitCode);
        Assert.Equal("", result.TerminationLog);
    }

    [Fact]
    public async Task A_real_host_exits_through_docuconf_instead_of_failing_to_start()
    {
        var error = new StringWriter();
        int? exit = null;
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Shop:Port"] = "70000", ["Shop:DatabaseUrl"] = Secret });
        builder.AddDocuconf<ShopOptions>(s =>
        {
            s.Error = error;
            s.Exit = code => exit = code;
            s.TerminationLogPath = "";
            s.EnvironmentNames = () => [];
        });
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Equal("docuconf: 1 configuration problem:\n  [out_of_range] SHOP__PORT: '70000' is above the maximum 65535\n", error.ToString());
    }

    [Fact]
    public void ThrowOnInvalid_throws_instead_of_exiting()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Shop:Port"] = "0" }).Build();
        var error = new StringWriter();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddDocuconf<ShopOptions>(s =>
        {
            s.ThrowOnInvalid = true;
            s.Error = error;
            s.Exit = _ => Assert.Fail("ThrowOnInvalid must not exit");
            s.TerminationLogPath = "";
        });
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(2, ex.Failures.Count());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ValidateOnStart_keeps_working_for_other_options()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Valid()).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        services.AddOptions<OtherOptions>().Validate(_ => false, "other is invalid").ValidateOnStart();
        services.AddDocuconf<ShopOptions>(s => s.EnvironmentNames = () => []);
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal("other is invalid", ex.Message);
    }

    public sealed class OtherOptions
    {
        public int Value { get; set; }
    }

    [Fact]
    public void LoadOrExit_returns_the_options_or_reports_and_exits()
    {
        int? exit = null;
        var error = new StringWriter();
        IServiceProvider Provider(Dictionary<string, string?> config)
        {
            var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
            services.AddDocuconf<ShopOptions>(s =>
            {
                s.Error = error;
                s.Exit = code => exit = code;
                s.TerminationLogPath = "";
                s.EnvironmentNames = () => [];
            });
            return services.BuildServiceProvider();
        }

        Assert.Equal(8080, Provider(Valid()).LoadOrExit<ShopOptions>().Port);
        Assert.Null(exit);

        Assert.ThrowsAny<Exception>(() => Provider(new()).LoadOrExit<ShopOptions>());
        Assert.Equal(1, exit);
        Assert.StartsWith("docuconf: 1 configuration problem:\n  [missing_required] SHOP__DATABASEURL", error.ToString(), StringComparison.Ordinal);
    }

    // Rule 1: secrets never reach an error, even one from the app's own validator.
    [Fact]
    public void Secrets_in_messages_from_the_apps_own_validators_are_redacted()
    {
        var result = _files.Start<ShopOptions>(Valid(), more: s => s.AddSingleton<IValidateOptions<ShopOptions>, SecretEchoValidator>());

        Assert.Contains("database *** is not reachable", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.TerminationLog, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_values_redact_secrets_when_printed()
    {
        var json = CueWriter.WriteJson(ContractReader.Read([typeof(ShopOptions)]), DocuconfExport.GeneratorInfo);
        var values = DocuconfContract.FromJson(json).Load(new Dictionary<string, string> { ["SHOP__DATABASEURL"] = Secret });

        Assert.DoesNotContain("hunter2", values.ToString(), StringComparison.Ordinal);
        Assert.Contains("SHOP__DATABASEURL = ***", values.ToString(), StringComparison.Ordinal);
        Assert.Contains("SHOP__PORT = 8080", values.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_that_would_print_a_secret_is_a_declaration_error()
    {
        var ex = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(RecordOptions)]));

        Assert.Contains(ex.Errors, e => e.StartsWith("RecordOptions.Token: RecordOptions is a record, so its generated ToString prints this [Secret] value", StringComparison.Ordinal));
    }

    // P0 2: [EnvName] binds the variable the contract exports.
    [Fact]
    public void EnvName_binds_the_name_the_contract_exports()
    {
        var model = ContractReader.Read([typeof(ShopOptions)]);
        Assert.True(model.Vars.ContainsKey("LOG_LEVEL"));

        var options = DocuconfTesting.Load<ShopOptions>(new Dictionary<string, string> { ["LOG_LEVEL"] = "debug", ["SHOP__DATABASEURL"] = Secret });
        Assert.Equal("debug", options.LogLevel);

        var problems = DocuconfTesting.Validate<ShopOptions>(new Dictionary<string, string> { ["LOG_LEVEL"] = "trace", ["SHOP__DATABASEURL"] = Secret });
        Assert.Equal("[not_in_enum] LOG_LEVEL: 'trace' is not one of debug, info, warn", Assert.Single(problems).ToString());
    }

    [Fact]
    public void EnvName_still_reads_the_configuration_path_from_appsettings()
    {
        var config = Valid();
        config["Shop:LogLevel"] = "warn";

        Assert.Equal("warn", _files.Resolve<ShopOptions>(config).LogLevel);
    }

    [Fact]
    public void EnvName_round_trips_through_the_platform_rendering()
    {
        // What the platform renders from the contract is what the app reads.
        var contract = DocuconfContract.FromJson(CueWriter.WriteJson(ContractReader.Read([typeof(ShopOptions)]), DocuconfExport.GeneratorInfo));
        var env = new Dictionary<string, string> { ["LOG_LEVEL"] = "warn", ["SHOP__DATABASEURL"] = Secret };

        Assert.Equal("warn", contract.Load(env).Get<string>("LOG_LEVEL"));
        Assert.Equal("warn", DocuconfTesting.Load<ShopOptions>(env).LogLevel);
    }

    // P1 4: AddDocuconf next to BindConfiguration/ValidateDataAnnotations is reported, not silently doubled.
    [Fact]
    public void BindConfiguration_and_ValidateDataAnnotations_next_to_AddDocuconf_are_reported()
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(Valid()).Build());
        services.AddOptions<ShopOptions>().BindConfiguration("Shop").ValidateDataAnnotations().ValidateOnStart();
        services.AddDocuconf<ShopOptions>();
        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Equal(
            "ShopOptions is also registered with BindConfiguration and ValidateDataAnnotations. AddDocuconf<ShopOptions>() binds the section and checks every DataAnnotation itself, reporting every problem once; remove those calls (keep AddDocuconf).",
            ex.Message);
    }

    // P1 6 and rule 2: a constraint the contract cannot carry is a declaration error.
    [Theory]
    [InlineData(typeof(SchemesOnIntOptions), "Bad:Upstream: [UrlSchemes] applies to string or Uri properties, but Int32 is exported as an int variable.")]
    [InlineData(typeof(AllowedOnIntOptions), "Bad:Level: [AllowedValues] applies to string properties, but Int32 is exported as an int variable.")]
    [InlineData(typeof(RangeOnStringOptions), "Bad:Region: [Range] applies to numbers and TimeSpan, but String is exported as a string variable.")]
    [InlineData(typeof(DictionaryOptions), "Bad:Headers: a dictionary cannot be described by the contract, but the app would still read it.")]
    public void A_declaration_the_platform_would_reject_fails_export(Type type, string expected)
    {
        var ex = Assert.Throws<ContractException>(() => ContractReader.Read([type]));

        Assert.Contains(ex.Errors, e => e.StartsWith(expected, StringComparison.Ordinal));
    }

    // P1 7: one value for a list variable is an error that says how to set it.
    [Fact]
    public void A_scalar_for_a_list_is_invalid_type_with_the_fix()
    {
        var problems = DocuconfTesting.Validate<ShopOptions>(new Dictionary<string, string> { ["SHOP__TAGS"] = "x,y", ["SHOP__DATABASEURL"] = Secret });

        Assert.Equal("[invalid_type] SHOP__TAGS: is a list; set SHOP__TAGS__0, SHOP__TAGS__1, ... instead of one value ('x,y')", Assert.Single(problems).ToString());
    }

    // P1 12: the same words as the contract-first mode; a custom ErrorMessage is kept.
    [Fact]
    public void Constraint_messages_use_the_variable_name_and_the_value()
    {
        var problems = DocuconfTesting.Validate<ShopOptions>(new Dictionary<string, string>
        {
            ["SHOP__PORT"] = "0",
            ["SHOP__TIMEOUT"] = "00:10:00",
            ["SHOP__RETRIES"] = "11",
            ["SHOP__DATABASEURL"] = "mysql://shop:hunter2-pw@db/shop",
        }).Select(p => p.ToString()).ToList();

        Assert.Equal(
        [
            "[invalid_scheme] SHOP__DATABASEURL: must use one of the schemes postgres (value redacted)",
            "[out_of_range] SHOP__PORT: '0' is below the minimum 1",
            "[out_of_range] SHOP__RETRIES: Retries must be 1 to 10.",
            "[out_of_range] SHOP__TIMEOUT: '00:10:00' is longer than 5m",
        ], problems.Order(StringComparer.Ordinal));
    }

    // Rule 7: the duration error shows the form the app expects.
    [Fact]
    public void A_go_style_duration_shows_the_expected_form()
    {
        var problems = DocuconfTesting.Validate<ShopOptions>(new Dictionary<string, string> { ["SHOP__TIMEOUT"] = "30s", ["SHOP__DATABASEURL"] = Secret });

        Assert.Equal("[invalid_type] SHOP__TIMEOUT: '30s' is not a duration in the timespan encoding, such as 00:01:30 (hh:mm:ss, or d.hh:mm:ss)", Assert.Single(problems).ToString());
    }

    // P1 8: Get<T> converts or throws; it never returns a silent default.
    [Fact]
    public void Get_converts_integers_and_throws_on_a_mismatch_or_a_typo()
    {
        var values = DocuconfContract.FromJson(CueWriter.WriteJson(ContractReader.Read([typeof(ShopOptions)]), DocuconfExport.GeneratorInfo))
            .Load(new Dictionary<string, string> { ["SHOP__PORT"] = "9000", ["SHOP__DATABASEURL"] = Secret });

        Assert.Equal(9000L, values.Get<long>("SHOP__PORT"));
        Assert.Equal(9000, values.Get<int>("SHOP__PORT"));
        Assert.Equal(9000, values.Get<int?>("SHOP__PORT"));
        Assert.Equal(9000d, values.Get<double>("SHOP__PORT"));
        Assert.Equal("SHOP__PORT does not fit in Byte; read it with Get<long>(\"SHOP__PORT\").", Assert.Throws<OverflowException>(() => values.Get<byte>("SHOP__PORT")).Message);
        Assert.Equal("SHOP__PORT is a long value, not string; read it with Get<long>(\"SHOP__PORT\").", Assert.Throws<InvalidCastException>(() => values.Get<string>("SHOP__PORT")).Message);
        Assert.Equal("SHOP__PROT is not in contract 'shop'; did you mean SHOP__PORT?", Assert.Throws<KeyNotFoundException>(() => values.Get<long>("SHOP__PROT")).Message);
    }

    // P1 9: export writes JSON that the contract-first mode reads, with no CUE module.
    [Fact]
    public void Export_writes_json_for_a_json_path_or_format()
    {
        var assembly = Compiler.Load(OrdersSource);
        var dir = Directory.CreateTempSubdirectory("docuconf-export-").FullName;
        try
        {
            var path = Path.Join(dir, "contract.json");
            var (code, stdout, _) = RunExport(assembly, "docuconf", "export", path);

            Assert.Equal(0, code);
            Assert.Equal($"Wrote {path}\n", stdout);
            var contract = DocuconfContract.FromFile(path);
            Assert.Equal(8080L, contract.Load(new Dictionary<string, string>()).Get<long>("ORDERS__PORT"));

            var (_, json, _) = RunExport(assembly, "docuconf", "export", "-", "--format", "json");
            Assert.Equal(File.ReadAllText(path), json);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // P2: --check for CI, - for stdout, and a mistyped command is a usage error.
    [Fact]
    public void Export_check_fails_when_the_file_is_out_of_date()
    {
        var assembly = Compiler.Load(OrdersSource);
        var dir = Directory.CreateTempSubdirectory("docuconf-export-").FullName;
        try
        {
            var path = Path.Join(dir, "contract.cue");
            var (missing, _, missingError) = RunExport(assembly, "docuconf", "export", path, "--check");
            Assert.Equal(1, missing);
            Assert.Contains("does not exist", missingError, StringComparison.Ordinal);

            Assert.Equal(0, RunExport(assembly, "docuconf", "export", path).Code);
            Assert.Equal((0, $"{path} is up to date\n", ""), RunExport(assembly, "docuconf", "export", path, "--check"));

            File.AppendAllText(path, "// edited\n");
            var (stale, _, staleError) = RunExport(assembly, "docuconf", "export", path, "--check");
            Assert.Equal(1, stale);
            Assert.Equal($"docuconf: {path} is out of date; run docuconf export {path} again and commit it.\n", staleError);

            var (_, cue, _) = RunExport(assembly, "docuconf", "export", "-");
            Assert.StartsWith("// Code generated by docuconf. DO NOT EDIT.", cue, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_mistyped_docuconf_command_is_a_usage_error()
    {
        var (code, _, error) = RunExport(typeof(DevxTests).Assembly, "docuconf", "exprot", "x.cue");

        Assert.Equal(2, code);
        Assert.StartsWith("docuconf: unknown command 'exprot'.\nusage: <app> docuconf export", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AddDocuconf_refuses_to_start_the_app_for_a_docuconf_command()
    {
        DocuconfStartup.CheckNotExportRun(["app.dll"]);
        DocuconfStartup.CheckNotExportRun(["app.dll", "--urls", "http://*:80"]);

        var ex = Assert.Throws<InvalidOperationException>(() => DocuconfStartup.CheckNotExportRun(["app.dll", "docuconf", "export", "c.cue"]));
        Assert.StartsWith("'docuconf export c.cue' was requested but DocuconfExport.RunIfRequested(args) did not run.", ex.Message, StringComparison.Ordinal);
    }

    // P1 10: the secret-in-appsettings error names the local alternative.
    [Fact]
    public void A_secret_in_appsettings_points_to_user_secrets()
    {
        var dir = Directory.CreateTempSubdirectory("docuconf-appsettings-").FullName;
        try
        {
            File.WriteAllText(Path.Join(dir, "appsettings.Development.json"), """{ "Shop": { "DatabaseUrl": "postgres://localhost/shop" } }""");

            var ex = Assert.Throws<ContractException>(() => ContractReader.Read([typeof(ShopOptions)], new ContractReadSettings { ContentRoot = dir }));

            Assert.Contains(ex.Errors, e => e.EndsWith("For local runs use dotnet user-secrets or an environment variable.", StringComparison.Ordinal));
            Assert.DoesNotContain(ex.Errors, e => e.Contains("localhost/shop", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Rule 5 and P2: a variable close to a declared name gets a hint, never the value.
    [Fact]
    public void A_typo_in_a_variable_name_gets_a_hint()
    {
        var result = _files.Start<ShopOptions>(Valid(), environment: ["SHOP__PROT", "SHOP_PORT", "LOG_LEVL", "SHOP__TAGS__0", "HOSTNAME", "PATH", "SHOP__PORT"]);

        Assert.Equal(
            "docuconf: LOG_LEVL is set but not declared; did you mean LOG_LEVEL?\n"
            + "docuconf: SHOP_PORT is set but not declared; did you mean SHOP__PORT?\n"
            + "docuconf: SHOP__PROT is set but not declared; did you mean SHOP__PORT?\n",
            result.Stderr);
        Assert.Null(result.ExitCode);
    }

    [Fact]
    public void Short_names_need_a_closer_match()
    {
        var declared = new[] { new VarSpec { Name = "PORT", ConfigKey = "Port", Type = VarType.Int, Description = "HTTP port" } };

        Assert.Empty(DocuconfStartup.TypoHints(declared, ["HOST", "PWD"]));
        Assert.Single(DocuconfStartup.TypoHints(declared, ["PORTS"]));
    }

    // P2 testing: an explicit environment, without the process environment, a host or the termination log.
    [Fact]
    public void Testing_validates_an_explicit_environment()
    {
        var problems = DocuconfTesting.Validate<ShopOptions>(new Dictionary<string, string> { ["SHOP__PORT"] = "0" });

        Assert.Equal(["missing_required", "out_of_range"], problems.Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.Empty(DocuconfTesting.Validate<ShopOptions>(new Dictionary<string, string> { ["SHOP__DATABASEURL"] = Secret }));
        Assert.Throws<OptionsValidationException>(() => DocuconfTesting.Load<ShopOptions>(new Dictionary<string, string>()));
    }

    [Fact]
    public void Testing_reads_the_file_root_from_the_given_environment()
    {
        var env = new Dictionary<string, string>
        {
            ["DOCUCONF_FILE_ROOT"] = _files.Root,
            ["GATEWAY__DATABASEURL"] = "postgres://app:pw@db/gw",
            ["GATEWAY__BROKERS__0"] = "kafka:9092",
            ["GATEWAY__KEYSTOREPASSWORD"] = "s3cret",
        };

        var options = DocuconfTesting.Load<GatewayOptions>(env, s => s.Clock = () => _files.Now);

        Assert.Equal("ABCD\n", options.License);
    }

    [Fact]
    public void The_file_root_can_come_from_configuration()
    {
        Assert.Equal("/from/config", DocuconfBinder.Root(new DocuconfSettings(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Docuconf:FileRoot"] = "/from/config" }).Build()));
        Assert.Equal("/explicit", DocuconfBinder.Root(new DocuconfSettings { FileRoot = "/explicit" }, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Docuconf:FileRoot"] = "/from/config" }).Build()));
    }

    [Fact]
    public void Contract_first_LoadOrExit_reports_and_exits()
    {
        var contract = DocuconfContract.FromJson(CueWriter.WriteJson(ContractReader.Read([typeof(ShopOptions)]), DocuconfExport.GeneratorInfo));
        var error = new StringWriter();
        int? exit = null;

        contract.LoadOrExit(new Dictionary<string, string> { ["SHOP__PORT"] = "0" }, new DocuconfSettings { Error = error, Exit = c => exit = c, TerminationLogPath = "" });

        Assert.Equal(1, exit);
        Assert.Equal("docuconf: 2 configuration problems:\n  [missing_required] SHOP__DATABASEURL: is required\n  [out_of_range] SHOP__PORT: '0' is below the minimum 1\n", error.ToString());
    }

    private const string OrdersSource = """
        [ConfigContract("orders-api", Section = "Orders")]
        public sealed class OrdersOptions
        {
            [Range(1, 65535), Description("HTTP listen port")]
            public int Port { get; set; } = 8080;
        }
        """;

    private static (int Code, string Stdout, string Stderr) RunExport(System.Reflection.Assembly assembly, params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = DocuconfExport.Run(args, assembly, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}

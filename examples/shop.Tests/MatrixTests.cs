using Docuconf;

namespace Shop.Tests;

// The shop example's test matrix, the same in every docuconf SDK. Each case starts from DATABASE_URL and a fresh, empty
// file root, and either loads and prints the role's line or fails with exactly the listed problems.
public sealed class MatrixTests : IDisposable
{
    private const string DatabaseUrl = "postgres://shop@db:5432/shop";
    private const string KeyPath = "etc/shop/webhooks/signing.key";

    private readonly string _root = Directory.CreateTempSubdirectory("shop-matrix-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Dictionary<string, string> Environment(params (string Name, string Value)[] vars)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DATABASE_URL"] = DatabaseUrl,
            ["DOCUCONF_FILE_ROOT"] = _root,
        };
        foreach (var (name, value) in vars)
        {
            env[name] = value;
        }

        return env;
    }

    /// <summary>Loads the configuration as the app does, and returns the line it prints.</summary>
    private static string Run(Dictionary<string, string> env)
    {
        var problems = DocuconfTesting.Validate<ShopOptions>(env);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        return ShopApp.Describe(env[DocuconfContract.RoleVar], DocuconfTesting.Load<ShopOptions>(env));
    }

    private static Violation Fails(Dictionary<string, string> env, string input, string code)
    {
        var problem = Assert.Single(DocuconfTesting.Validate<ShopOptions>(env));
        Assert.Equal((input, code), (problem.Input, problem.Code));
        Assert.DoesNotContain(DatabaseUrl, problem.ToString(), StringComparison.Ordinal);
        return problem;
    }

    private void WriteKey(string key)
    {
        var path = Path.Join(_root, KeyPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, key);
    }

    [Fact]
    public void Case01_web_loads() =>
        Assert.Equal("shop web: port 8080, origins https://shop.example",
            Run(Environment(("DOCUCONF_ROLE", "web"), ("ALLOWED_ORIGINS", "https://shop.example"))));

    [Fact]
    public void Case02_worker_needs_no_allowed_origins() =>
        Assert.Equal("shop worker: queues default,mail, webhooks off",
            Run(Environment(("DOCUCONF_ROLE", "worker"), ("QUEUES", "default,mail"))));

    [Fact]
    public void Case03_web_needs_allowed_origins() =>
        Fails(Environment(("DOCUCONF_ROLE", "web")), "ALLOWED_ORIGINS", Codes.MissingRequired);

    [Fact]
    public void Case04_no_role_is_the_one_problem() =>
        Fails(Environment(("QUEUES", "default")), "DOCUCONF_ROLE", Codes.MissingRequired);

    [Fact]
    public void Case05_an_undeclared_role_is_the_one_problem() =>
        Fails(Environment(("DOCUCONF_ROLE", "cron")), "DOCUCONF_ROLE", Codes.NotInEnum);

    [Fact]
    public void Case06_json_logs_need_a_schema_url()
    {
        var problem = Fails(
            Environment(("DOCUCONF_ROLE", "worker"), ("QUEUES", "default"), ("LOG_FORMAT", "json")),
            "LOG_SCHEMA_URL", Codes.MissingRequired);
        Assert.Contains("is required when LOG_FORMAT is \"json\"", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Case07_json_logs_with_a_schema_url_load() =>
        Assert.Equal("shop worker: queues default, webhooks off",
            Run(Environment(("DOCUCONF_ROLE", "worker"), ("QUEUES", "default"), ("LOG_FORMAT", "json"),
                ("LOG_SCHEMA_URL", "https://logs.example/v1.json"))));

    [Fact]
    public void Case08_webhooks_need_the_signing_key()
    {
        var problem = Fails(
            Environment(("DOCUCONF_ROLE", "worker"), ("QUEUES", "default"), ("WEBHOOKS_ENABLED", "true")),
            "webhook-signing-key", Codes.FileMissing);
        Assert.Contains(", and is required when WEBHOOKS_ENABLED is true", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Case09_webhooks_with_the_signing_key_load()
    {
        const string key = "whsec-0123456789abcdef0123456789abcdef";
        WriteKey(key);
        var env = Environment(("DOCUCONF_ROLE", "worker"), ("QUEUES", "default"), ("WEBHOOKS_ENABLED", "true"));

        Assert.Equal("shop worker: queues default, webhooks on", Run(env));
        Assert.Equal(key, DocuconfTesting.Load<ShopOptions>(env).WebhookSigningKey);
    }

    [Fact]
    public void Case10_the_signing_key_belongs_to_the_worker() =>
        Assert.Equal("shop web: port 8080, origins https://shop.example",
            Run(Environment(("DOCUCONF_ROLE", "web"), ("ALLOWED_ORIGINS", "https://shop.example"), ("WEBHOOKS_ENABLED", "true"))));

    [Fact]
    public void Case11_fragment_variables_are_checked_at_boot() =>
        Fails(
            Environment(("DOCUCONF_ROLE", "web"), ("ALLOWED_ORIGINS", "https://shop.example"),
                ("OTEL_EXPORTER_OTLP_PROTOCOL", "carrier-pigeon")),
            "OTEL_EXPORTER_OTLP_PROTOCOL", Codes.NotInEnum);
}

using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Docuconf.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly GatewayFiles _files = new();

    public void Dispose() => _files.Dispose();

    private OptionsValidationException Fails(Dictionary<string, string?> config, DateTimeOffset? now = null) =>
        Assert.Throws<OptionsValidationException>(() => _files.Resolve<GatewayOptions>(config, now));

    private static string[] Codes(OptionsValidationException ex)
    {
        return ex.Failures.Select(f => f[1..f.IndexOf(']')]).Order().ToArray();
    }

    [Fact]
    public void Binds_variables_and_loads_every_file_input()
    {
        var options = _files.Resolve<GatewayOptions>(_files.Config());

        Assert.Equal(8080, options.Port);
        Assert.Equal(["kafka-0:9092"], options.Brokers);
        Assert.Equal("/billing", Assert.Single(options.Routes.Items).Match);
        Assert.Equal("ABCD\n", options.License);
        Assert.Contains("gw.internal", options.Tls.Current.Subject);
        using var partner = options.Partner!.Load(options.KeystorePassword);
        Assert.True(partner.HasPrivateKey);
        Assert.Single(options.TrustedCas!.Load());
    }

    [Fact]
    public void Reports_every_problem_together()
    {
        var config = _files.Config();
        config.Remove("Gateway:DatabaseUrl");
        config["Gateway:Port"] = "eighty";
        config["Gateway:Tracing"] = "yes";
        config["Gateway:Timeout"] = "30"; // TimeSpan.Parse would read 30 days

        var ex = Fails(config);

        Assert.Equal(["invalid_type", "invalid_type", "invalid_type", "missing_required"], Codes(ex));
        Assert.Contains(ex.Failures, f => f.Contains("GATEWAY__TIMEOUT", StringComparison.Ordinal) && f.Contains("hh:mm:ss", StringComparison.Ordinal));
    }

    [Fact]
    public void Never_prints_secret_values()
    {
        var config = _files.Config();
        config["Gateway:DatabaseUrl"] = "mysql://app:hunter2@db/gw";

        var ex = Fails(config);

        Assert.Equal(["invalid_scheme"], Codes(ex));
        Assert.DoesNotContain(ex.Failures, f => f.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void Constraint_violations_carry_their_codes()
    {
        var config = _files.Config();
        config["Gateway:Port"] = "70000";
        config["Gateway:Timeout"] = "00:10:00";
        config.Remove("Gateway:Brokers:0");
        config["Gateway:Brokers"] = null;

        var ex = Fails(config);

        Assert.Equal(["missing_required", "out_of_range", "out_of_range"], Codes(ex));
    }

    [Fact]
    public void Empty_values_count_as_unset_for_non_strings()
    {
        var config = _files.Config();
        config["Gateway:Port"] = "";
        config["Gateway:Tracing"] = "false";

        var options = _files.Resolve<GatewayOptions>(config);

        Assert.Equal(8080, options.Port);
        Assert.False(options.Tracing);
    }

    [Fact]
    public void Certificate_close_to_expiry_is_reported()
    {
        // The certificate has 90 days; 80 days on, 10 remain, short of the 720h (30 days) required.
        var ex = Fails(_files.Config(), _files.Now.AddDays(80));
        Assert.Equal(["certificate_expiring"], Codes(ex));
    }

    [Fact]
    public void Expired_certificate_is_reported()
    {
        var ex = Fails(_files.Config(), _files.Now.AddDays(100));
        Assert.Contains("certificate_invalid", Codes(ex));
    }

    [Fact]
    public void Certificate_must_cover_the_declared_names()
    {
        _files.WriteTls(_files.Ca, "other.internal", _files.Now.AddDays(-1), _files.Now.AddDays(90));
        var ex = Fails(_files.Config());
        Assert.Equal(["certificate_name_mismatch"], Codes(ex));
    }

    [Fact]
    public void Key_must_match_the_certificate()
    {
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _files.WriteTls(_files.Ca, "gw.internal", _files.Now.AddDays(-1), _files.Now.AddDays(90), keyOverride: otherKey);
        var ex = Fails(_files.Config());
        Assert.Equal(["key_mismatch"], Codes(ex));
    }

    [Fact]
    public void Certificate_must_chain_to_its_CA()
    {
        var stranger = CertificateAuthority.Create("Unrelated CA", _files.Now);
        _files.WriteTls(_files.Ca, "gw.internal", _files.Now.AddDays(-1), _files.Now.AddDays(90), caPem: stranger.Certificate.ExportCertificatePem());
        var ex = Fails(_files.Config());
        Assert.Equal(["certificate_invalid"], Codes(ex));
        Assert.Contains(ex.Failures, f => f.Contains("does not chain", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_required_file_is_reported()
    {
        _files.Delete("/etc/gw/license/license.key");
        var ex = Fails(_files.Config());
        Assert.Equal(["file_missing"], Codes(ex));
    }

    [Fact]
    public void Malformed_config_file_is_reported()
    {
        _files.Write("/etc/gw/routes/routes.json", """{ "items": [ { "match": "/a", """);
        var ex = Fails(_files.Config());
        Assert.Equal(["file_malformed"], Codes(ex));
    }

    [Fact]
    public void Unknown_properties_in_a_config_file_are_rejected()
    {
        _files.Write("/etc/gw/routes/routes.json", """{ "items": [ { "match": "/a", "upstream": "http://a", "weight": 3 } ] }""");
        var ex = Fails(_files.Config());
        Assert.Equal(["file_malformed"], Codes(ex));
    }

    [Fact]
    public void Config_file_is_checked_against_its_type()
    {
        _files.Write("/etc/gw/routes/routes.json", """{ "items": [ { "match": "billing", "upstream": "not a url" } ] }""");
        var ex = Fails(_files.Config());
        Assert.Equal(["schema_mismatch", "schema_mismatch"], Codes(ex));
        Assert.Contains(ex.Failures, f => f.Contains("$.items[0].match", StringComparison.Ordinal));
    }

    [Fact]
    public void Text_file_must_match_its_pattern()
    {
        _files.Write("/etc/gw/license/license.key", "abcd");
        var ex = Fails(_files.Config());
        Assert.Equal(["pattern_mismatch"], Codes(ex));
    }

    [Fact]
    public void Keystore_must_open_with_its_password()
    {
        var config = _files.Config();
        config["Gateway:KeystorePassword"] = "wrong";
        var ex = Fails(config);
        Assert.Equal(["keystore_unreadable"], Codes(ex));
    }

    [Fact]
    public void Failures_are_written_to_the_termination_log()
    {
        var log = Path.Join(_files.Root, "termination-log");
        File.WriteAllText(log, "");
        var config = _files.Config();
        config["Gateway:Port"] = "0";

        Assert.Throws<OptionsValidationException>(() => _files.Resolve<GatewayOptions>(config, terminationLog: log));

        Assert.Contains("[out_of_range] GATEWAY__PORT", File.ReadAllText(log), StringComparison.Ordinal);
    }
}

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Docuconf.Contract;

namespace Docuconf.Tests;

[ConfigContract("reloads", Section = "Reloads")]
public sealed class ReloadOptions
{
    [Required, Secret]
    [Description("Password for the partner keystore")]
    public string KeystorePassword { get; set; } = "";

    [ConfigFile("/etc/app/live/routes.json", Reload = Reload.Watch)]
    [Description("Routing rules reloaded when they change")]
    public ConfigFile<Routes> Live { get; set; } = new();

    [KeystoreFile("/etc/app/partner/keystore.p12", PasswordProperty = nameof(KeystorePassword), Reload = Reload.Watch)]
    [Description("Client certificate for the partner API")]
    public Keystore? Partner { get; set; }

    [TlsFile("/etc/app/tls", DnsNames = ["app.internal"], Reload = Reload.Watch)]
    [Description("Serving certificate")]
    public TlsKeyPair Tls { get; set; } = new();
}

/// <summary>Reloads of watched inputs (SPEC §4.6.2): hooks, status, and the boot checks on every new value.</summary>
public sealed class ReloadTests : IDisposable
{
    private const string Password = "s3cret";
    private readonly string _root = Directory.CreateTempSubdirectory("docuconf-reload-").FullName;
    private readonly CertificateAuthority _ca;
    private readonly StringWriter _log = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private int _writes;

    public ReloadTests()
    {
        _ca = CertificateAuthority.Create("Test CA", _now);
        WriteRoutes("/first");
        WriteKeystore("partner.one", Password);
        WriteTls("app.internal");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Full(string path) => Path.Join(_root, path);

    /// <summary>Writes a file with a timestamp that differs from the previous write's.</summary>
    private void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!);
        File.WriteAllBytes(Full(path), content);
        File.SetLastWriteTimeUtc(Full(path), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_writes));
    }

    private void WriteRoutes(string match) =>
        Write("/etc/app/live/routes.json", System.Text.Encoding.UTF8.GetBytes($$"""{ "items": [ { "match": "{{match}}", "upstream": "http://a.svc" } ] }"""));

    private void WriteKeystore(string name, string password)
    {
        var (leaf, key) = _ca.Issue(name, _now.AddDays(-1), _now.AddDays(365));
        using var withKey = leaf.CopyWithPrivateKey(key);
        Write("/etc/app/partner/keystore.p12", withKey.Export(X509ContentType.Pkcs12, password));
    }

    private void WriteTls(string dnsName, ECDsa? keyOverride = null)
    {
        var (cert, key) = _ca.Issue(dnsName, _now.AddDays(-1), _now.AddDays(90));
        Write("/etc/app/tls/tls.crt", System.Text.Encoding.UTF8.GetBytes(cert.ExportCertificatePem()));
        Write("/etc/app/tls/tls.key", System.Text.Encoding.UTF8.GetBytes((keyOverride ?? key).ExportPkcs8PrivateKeyPem()));
    }

    /// <summary>Moves the clock past every reload interval, so the next read looks at the files.</summary>
    private void Later() => _now = _now.AddMinutes(1);

    private Dictionary<string, string> Env() => new() { ["RELOADS__KEYSTOREPASSWORD"] = Password };

    private ReloadOptions Load() => DocuconfTesting.Load<ReloadOptions>(Env(), s =>
    {
        s.FileRoot = _root;
        s.Clock = () => _now;
        s.Error = _log;
    });

    [Fact]
    public void A_hook_runs_with_the_new_value_after_an_accepted_change_and_never_for_a_rejected_one()
    {
        var live = Load().Live;
        var seen = new List<string>();
        var also = new List<string>();
        using var first = live.OnChange(r => seen.Add(r.Items[0].Match));
        using var second = live.OnChange(r => also.Add(r.Items[0].Match));
        var token = live.GetReloadToken();

        Assert.Equal(new ReloadStatus(1, null, null), live.Status);
        Assert.Equal("live", live.Input);

        WriteRoutes("/second");
        Later();
        Assert.Equal("/second", live.Value!.Items[0].Match);
        Assert.Equal(["/second"], seen);
        Assert.Equal(["/second"], also);
        Assert.True(token.HasChanged);
        Assert.Equal(new ReloadStatus(2, _now, null), live.Status);
        var accepted = _now;

        // A file that no longer parses is rejected: the value stays, no hook runs, and the status names the code.
        Write("/etc/app/live/routes.json", "{ \"items\": [ { \"match\": \"/leak"u8.ToArray());
        Later();
        token = live.GetReloadToken();
        Assert.Equal("/second", live.Value!.Items[0].Match);
        Assert.Equal(["/second"], seen);
        Assert.False(token.HasChanged);
        var status = live.Status;
        Assert.Equal((2L, accepted), (status.Generation, status.LastReload));
        Assert.Equal((_now, "live"), (status.LastRejection!.At, status.LastRejection.Input));
        Assert.Equal(["file_malformed"], status.LastRejection.Codes);
        Assert.Contains("live: a changed file was rejected (file_malformed); keeping the previous value", _log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("/leak", _log.ToString(), StringComparison.Ordinal);

        // A later accepted change clears the rejection.
        WriteRoutes("/third");
        Later();
        Assert.Equal("/third", live.Value!.Items[0].Match);
        Assert.Equal(new ReloadStatus(3, _now, null), live.Status);
        Assert.Equal(["/second", "/third"], seen);
    }

    [Fact]
    public void A_hook_that_throws_is_logged_by_name_and_type_and_does_not_stop_the_others_or_the_reload()
    {
        var live = Load().Live;
        var seen = new List<string>();
        using var bad = live.OnChange(_ => throw new InvalidOperationException("holds /second"));
        using var good = live.OnChange(r => seen.Add(r.Items[0].Match));

        WriteRoutes("/second");
        Later();

        Assert.Equal("/second", live.Value!.Items[0].Match);
        Assert.Equal(["/second"], seen);
        Assert.Equal(2, live.Status.Generation);
        Assert.Contains("docuconf: warning: live: an OnChange callback threw System.InvalidOperationException", _log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("holds", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_disposed_hook_is_not_called()
    {
        var live = Load().Live;
        var seen = 0;
        live.OnChange(_ => seen++).Dispose();

        WriteRoutes("/second");
        Later();

        Assert.Equal("/second", live.Value!.Items[0].Match);
        Assert.Equal(0, seen);
    }

    [Fact]
    public void A_keystore_reloads_with_the_password_read_at_startup()
    {
        var options = Load();
        var partner = options.Partner!;
        var first = partner.Current.Thumbprint;
        X509Certificate2? changed = null;
        using var hook = partner.OnChange(c => changed = c);

        // The environment is read once: changing the bound password does not change what a reload opens it with.
        options.KeystorePassword = "rotated";
        WriteKeystore("partner.two", "rotated");
        Later();

        Assert.Equal(first, partner.Current.Thumbprint);
        Assert.Null(changed);
        Assert.Equal(["keystore_unreadable"], partner.Status.LastRejection!.Codes);
        Assert.Equal(1, partner.Status.Generation);
        Assert.DoesNotContain("rotated", _log.ToString(), StringComparison.Ordinal);

        WriteKeystore("partner.three", Password);
        Later();

        Assert.Contains("partner.three", partner.Current.Subject, StringComparison.Ordinal);
        Assert.Equal(partner.Current.Thumbprint, changed!.Thumbprint);
        Assert.Equal(new ReloadStatus(2, _now, null), partner.Status);
    }

    [Fact]
    public void A_tls_pair_that_fails_the_startup_checks_is_not_swapped_in()
    {
        var tls = Load().Tls;
        var first = tls.Current.Thumbprint;
        var changes = 0;
        using var hook = tls.OnChange(_ => changes++);

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteTls("app.internal", stranger);
        Later();
        Assert.Equal(first, tls.Current.Thumbprint);
        Assert.Equal(["key_mismatch"], tls.Status.LastRejection!.Codes);

        WriteTls("other.internal");
        Later();
        Assert.Equal(first, tls.Current.Thumbprint);
        Assert.Equal(["certificate_name_mismatch"], tls.Status.LastRejection!.Codes);

        WriteTls("app.internal");
        Later();
        Assert.NotEqual(first, tls.Current.Thumbprint);
        Assert.Equal(1, changes);
        Assert.Equal(new ReloadStatus(2, _now, null), tls.Status);
    }

    [Fact]
    public void The_contract_first_mode_reloads_a_watched_config_file_with_the_same_hooks_and_status()
    {
        var model = ContractReader.Read([typeof(ReloadOptions)]);
        var contract = DocuconfContract.FromJson(CueWriter.WriteJson(model, new CueWriter.Generator("Docuconf.Options", "0.1.0-test")));
        var values = contract.Load(Env(), new DocuconfSettings { FileRoot = _root, Clock = () => _now, Error = _log, TerminationLogPath = "" });

        var live = values.Get<ConfigFile<JsonNode>>("live")!;
        var seen = new List<string>();
        using var hook = live.OnChange(node => seen.Add((string)node["items"]![0]!["match"]!));
        Assert.Equal("/first", (string)values.Get<JsonNode>("live")!["items"]![0]!["match"]!);

        WriteRoutes("/second");
        Later();
        Assert.Equal("/second", (string)values.Get<JsonNode>("live")!["items"]![0]!["match"]!);
        Assert.Equal(["/second"], seen);
        Assert.Equal(new ReloadStatus(2, _now, null), live.Status);

        // A file that breaks the schema is rejected, as at startup.
        Write("/etc/app/live/routes.json", """{ "items": [ { "match": 7 } ] }"""u8.ToArray());
        Later();
        Assert.Equal("/second", (string)values.Get<JsonNode>("live")!["items"]![0]!["match"]!);
        Assert.Equal(["schema_mismatch"], live.Status.LastRejection!.Codes);

        // The keystore reloads with the password the contract read at startup.
        var partner = values.Get<Keystore>("partner")!;
        var first = partner.Current.Thumbprint;
        WriteKeystore("partner.two", "rotated");
        Later();
        Assert.Equal(first, partner.Current.Thumbprint);
        Assert.Equal(["keystore_unreadable"], partner.Status.LastRejection!.Codes);
    }

    [Fact]
    public void An_input_docuconf_did_not_load_has_no_status_and_never_calls_back()
    {
        var unloaded = new ConfigFile<Routes>();

        Assert.Equal(new ReloadStatus(0, null, null), unloaded.Status);
        Assert.False(unloaded.GetReloadToken().HasChanged);
        unloaded.OnChange(_ => throw new InvalidOperationException()).Dispose();
        Assert.Throws<InvalidOperationException>(() => new Keystore().Current);
    }
}

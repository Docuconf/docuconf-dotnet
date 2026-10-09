using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Docuconf.Contract;

namespace Docuconf.Runtime;

/// <summary>
/// Loads file inputs and checks what the platform could not see before deploy (SPEC §11.2 item 7): presence, size,
/// format, the schema or bound type, certificates and keystores. The declared options and the contract-first mode run
/// the same checks.
/// </summary>
internal static class FileChecks
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Where a file input is on disk: its <c>path</c>, or the value of its <c>pathEnv</c> variable when that is set,
    /// under <c>DOCUCONF_FILE_ROOT</c> (SPEC §11.1).
    /// </summary>
    public static string Locate(FileSpec spec, Func<string, string?> lookup, string root)
    {
        var path = spec.PathEnv is { } pathEnv && lookup(pathEnv) is { Length: > 0 } moved ? moved : spec.Path;
        return root.Length == 0 ? path : Path.Join(root, path);
    }

    /// <summary>Loads every file input of a declared options class into <paramref name="target"/>.</summary>
    public static void LoadAll(object target, ContractModel model, DocuconfSettings settings, List<Violation> violations, Microsoft.Extensions.Configuration.IConfiguration? configuration = null)
    {
        var root = DocuconfBinder.Root(settings, configuration);
        foreach (var spec in model.Files.Values)
        {
            var path = Locate(spec, Lookup(configuration), root);
            string? Password() => spec.PasswordPath is null ? null : DocuconfBinder.GetPath(target, spec.PasswordPath) as string;
            var configType = spec.Type == FileType.Config ? spec.WatchedType ?? spec.PropertyPath![^1].PropertyType : null;
            var now = settings.Clock();
            if (!TryLoad(spec, path, Password, now, configType, violations, out var value))
            {
                continue;
            }

            object? bound = spec.Type switch
            {
                FileType.Config when spec.WatchedType is { } watched => Watched(watched, spec, path, value, Password, settings.Clock, configType!),
                FileType.Config or FileType.Text => value,
                FileType.Tls => new TlsKeyPair { Directory = path },
                FileType.CaBundle => new CaBundle { Path = path },
                FileType.Keystore => new Keystore { Path = path },
                _ => new BinaryFile { Path = path },
            };
            DocuconfBinder.SetPath(target, spec.PropertyPath!, bound);
        }
    }

    /// <summary>
    /// Loads every file input of a contract (the contract-first mode): a <c>config</c> file as its data
    /// (<see cref="JsonNode"/>), a <c>text</c> file as its text, and the others as <see cref="TlsKeyPair"/>,
    /// <see cref="CaBundle"/>, <see cref="Keystore"/> or <see cref="BinaryFile"/>. An absent optional input is null.
    /// </summary>
    public static Dictionary<string, object?> LoadContract(
        ContractModel model, Func<string, string?> lookup, string root, DateTimeOffset now, Func<string, string?> password, List<Violation> violations)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var spec in model.Files.Values)
        {
            var path = Locate(spec, lookup, root);
            values[spec.Name] = TryLoad(spec, path, () => spec.PasswordVar is { } v ? password(v) : null, now, null, violations, out var value)
                ? spec.Type switch
                {
                    FileType.Config or FileType.Text => value,
                    FileType.Tls => new TlsKeyPair { Directory = path },
                    FileType.CaBundle => new CaBundle { Path = path },
                    FileType.Keystore => new Keystore { Path = path },
                    _ => new BinaryFile { Path = path },
                }
                : null;
        }

        return values;
    }

    /// <summary>Whether a file input is there, for the boot warning about deprecated inputs.</summary>
    public static bool Exists(FileSpec spec, string path) =>
        spec.Type == FileType.Tls ? File.Exists(Path.Join(path, "tls.crt")) : File.Exists(path);

    /// <summary>
    /// Loads and checks one file input at <paramref name="path"/>. Returns true when it is present and passes every
    /// check, with its value: a config file's data (bound to <paramref name="configType"/> when given, a
    /// <see cref="JsonNode"/> otherwise), a text file's text, or null for the other types. Returns false, with no
    /// violation, for an absent optional input.
    /// </summary>
    public static bool TryLoad(FileSpec spec, string path, Func<string?> password, DateTimeOffset now, Type? configType, List<Violation> violations, out object? value)
    {
        value = null;
        var reader = new Reader(spec, violations);
        try
        {
            switch (spec.Type)
            {
                case FileType.Tls:
                    return LoadTls(reader, spec, path, now);
                case FileType.Binary:
                    return reader.Read(path, spec.Required, open: false) is not null;
            }

            var data = reader.Read(path, spec.Required);
            if (data is null)
            {
                return false;
            }

            return spec.Type switch
            {
                FileType.Config => LoadConfig(reader, spec, data, configType, out value),
                FileType.Text => LoadText(reader, spec, data, out value),
                FileType.CaBundle => LoadCaBundle(reader, spec, data),
                _ => LoadKeystore(reader, spec, data, password(), now),
            };
        }
        catch (UnauthorizedAccessException)
        {
            reader.Unreadable(path);
            return false;
        }
        catch (IOException ex)
        {
            reader.Fail(Codes.FileUnreadable, $"{path} could not be read: {ex.Message}");
            return false;
        }
    }

    /// <summary>Reads the files of one input, reporting each problem for it.</summary>
    private sealed class Reader(FileSpec spec, List<Violation> violations)
    {
        public void Fail(string code, string message) => violations.Add(new Violation(code, spec.Name, message));

        public void Unreadable(string path) => Fail(Codes.FileUnreadable,
            $"{path} exists but cannot be read by this process. Secret volumes are owned by root; a non-root container needs the pod's securityContext.fsGroup set.");

        /// <summary>
        /// The file's content; null with no violation when an optional file is absent, or null with a violation.
        /// <paramref name="open"/> false only checks that it can be opened, and returns an empty array.
        /// </summary>
        public byte[]? Read(string path, bool required, bool open = true)
        {
            if (Directory.Exists(path))
            {
                Fail(Codes.FileMalformed, $"{path} is a directory, not a file");
                return null;
            }

            if (!File.Exists(path))
            {
                if (required)
                {
                    Fail(Codes.FileMissing, spec.PathEnv is { } pathEnv
                        ? $"{path} does not exist (mount it there, or set {pathEnv})"
                        : $"{path} does not exist");
                }

                return null;
            }

            long size = new FileInfo(path).Length;
            if (spec.MaxSize is { } maxSize && size > maxSize)
            {
                Fail(Codes.FileTooLarge, $"{path} is {size} bytes, above maxSize {maxSize}");
                return null;
            }

            if (!open)
            {
                using var _ = File.OpenRead(path);
                return [];
            }

            return File.ReadAllBytes(path);
        }
    }

    private static bool LoadConfig(Reader reader, FileSpec spec, byte[] data, Type? configType, out object? value)
    {
        value = null;
        var format = spec.Format ?? "json";
        if (!StructuredFile.TryParse(format, data, out var node, out var error, out var detail))
        {
            // A parser's message may quote the file; a secret's never shows.
            reader.Fail(Codes.FileMalformed, error + (detail is null || spec.Secret ? "" : ": " + detail));
            return false;
        }

        if (configType is null)
        {
            // The contract-first mode has no type to bind, so the file is checked against its JSON Schema.
            if (spec.Schema is { } schema && JsonSchemaCheck.Check(schema, spec.Secret, node) is { } mismatch)
            {
                reader.Fail(Codes.SchemaMismatch, mismatch);
                return false;
            }

            value = node;
            return true;
        }

        object? bound;
        try
        {
            bound = node.Deserialize(configType, DocuconfBinder.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // System.Text.Json messages give the JSON path and the type, never the value.
            reader.Fail(Codes.SchemaMismatch, $"does not bind to {configType.Name}" + (spec.Secret ? "" : ": " + ex.Message));
            return false;
        }

        if (bound is null)
        {
            reader.Fail(Codes.FileMalformed, "is empty or null");
            return false;
        }

        var problems = Annotations.ValidateGraph(bound, "$");
        foreach (var problem in problems)
        {
            reader.Fail(Codes.SchemaMismatch, spec.Secret ? $"{problem.Path} is invalid" : $"{problem.Path}: {problem.Message}");
        }

        value = bound;
        return problems.Count == 0;
    }

    private static bool LoadText(Reader reader, FileSpec spec, byte[] data, out object? value)
    {
        value = null;
        string text;
        try
        {
            text = StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            reader.Fail(Codes.FileMalformed, "is not valid UTF-8 text");
            return false;
        }

        // Lengths count characters (Unicode scalar values, SPEC §4.3), and nothing is trimmed.
        int length = Constraints.Length(text);
        bool ok = true;
        if (spec.MinLength is { } min && length < min)
        {
            reader.Fail(Codes.OutOfRange, $"is {length} characters, below minLength {min}");
            ok = false;
        }

        if (spec.MaxLength is { } max && length > max)
        {
            reader.Fail(Codes.OutOfRange, $"is {length} characters, above maxLength {max}");
            ok = false;
        }

        if (spec.Pattern is { } pattern && !Re2.IsMatch(pattern, text))
        {
            reader.Fail(Codes.PatternMismatch, $"does not match {pattern}");
            ok = false;
        }

        value = text;
        return ok;
    }

    private static bool LoadTls(Reader reader, FileSpec spec, string dir, DateTimeOffset now)
    {
        var crt = reader.Read(Path.Join(dir, "tls.crt"), spec.Required);
        if (crt is null)
        {
            return false;
        }

        var key = reader.Read(Path.Join(dir, "tls.key"), required: true);
        if (key is null)
        {
            return false;
        }

        var chain = new List<X509Certificate2>();
        try
        {
            if (!ParseCertificates(crt, chain, out var bad))
            {
                reader.Fail(Codes.CertificateInvalid, $"tls.crt: certificate {bad} does not parse");
                return false;
            }

            if (chain.Count == 0)
            {
                reader.Fail(Codes.FileMalformed, "tls.crt holds no PEM certificate");
                return false;
            }

            var leaf = chain[0];
            switch (KeyMatches(leaf, crt, key))
            {
                case KeyMatch.Unparseable:
                    reader.Fail(Codes.FileMalformed, "tls.key holds no parseable PEM private key");
                    return false;
                case KeyMatch.Mismatch:
                    reader.Fail(Codes.KeyMismatch, "tls.key does not match the certificate in tls.crt");
                    return false;
            }

            bool ok = true;
            var notBefore = leaf.NotBefore.ToUniversalTime();
            var notAfter = leaf.NotAfter.ToUniversalTime();
            if (now < notBefore)
            {
                reader.Fail(Codes.CertificateInvalid, $"the certificate is not valid until {notBefore:u}");
                ok = false;
            }
            else if (now >= notAfter)
            {
                reader.Fail(Codes.CertificateInvalid, $"the certificate expired at {notAfter:u}");
                ok = false;
            }
            else if (spec.MinRemaining is { } minRemaining && notAfter - now < GoDuration.Parse(minRemaining))
            {
                reader.Fail(Codes.CertificateExpiring, $"the certificate expires at {notAfter:u}, sooner than minRemaining {minRemaining}");
                ok = false;
            }

            foreach (var name in spec.DnsNames ?? [])
            {
                if (!leaf.MatchesHostname(name))
                {
                    reader.Fail(Codes.CertificateNameMismatch, $"the certificate does not cover {name}");
                    ok = false;
                }
            }

            if (spec.KeyAlgorithms is { Count: > 0 } allowed && KeyAlgorithmOf(leaf) is var algorithm && !allowed.Contains(algorithm))
            {
                reader.Fail(Codes.CertificateInvalid, $"the certificate uses a {algorithm} key; allowed: {string.Join(", ", allowed)}");
                ok = false;
            }

            var caData = reader.Read(Path.Join(dir, "ca.crt"), spec.RequireCA);
            if (caData is null)
            {
                return ok && !spec.RequireCA;
            }

            var cas = new List<X509Certificate2>();
            try
            {
                if (!ParseCertificates(caData, cas, out _) || cas.Count == 0)
                {
                    reader.Fail(Codes.CertificateInvalid, "ca.crt holds no parseable PEM certificate");
                    return false;
                }

                if (spec.RequireCA && Chain(leaf, chain.Skip(1), cas, now) is { } why)
                {
                    reader.Fail(Codes.CertificateInvalid, $"the certificate does not chain to ca.crt: {why}");
                    ok = false;
                }
            }
            finally
            {
                cas.ForEach(c => c.Dispose());
            }

            return ok;
        }
        finally
        {
            chain.ForEach(c => c.Dispose());
        }
    }

    private static string? Chain(X509Certificate2 leaf, IEnumerable<X509Certificate2> intermediates, List<X509Certificate2> roots, DateTimeOffset now)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots.ToArray());
        chain.ChainPolicy.ExtraStore.AddRange(intermediates.ToArray());
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        if (chain.Build(leaf))
        {
            return null;
        }

        var status = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
        return status.Length > 0 ? status : "the chain does not build";
    }

    private static bool LoadCaBundle(Reader reader, FileSpec spec, byte[] data)
    {
        var certificates = new List<X509Certificate2>();
        try
        {
            if (!ParseCertificates(data, certificates, out var bad))
            {
                reader.Fail(Codes.CertificateInvalid, $"certificate {bad} does not parse");
                return false;
            }

            if (certificates.Count == 0)
            {
                reader.Fail(Codes.FileMalformed, "holds no PEM certificates");
                return false;
            }

            int min = spec.MinCertificates ?? 1;
            if (certificates.Count < min)
            {
                reader.Fail(Codes.FileMalformed, $"holds {certificates.Count} certificate{(certificates.Count == 1 ? "" : "s")}, need at least {min}");
                return false;
            }

            return true;
        }
        finally
        {
            certificates.ForEach(c => c.Dispose());
        }
    }

    private static bool LoadKeystore(Reader reader, FileSpec spec, byte[] data, string? password, DateTimeOffset now)
    {
        // An unset password variable is an empty password (SPEC §11.2, item 7).
        X509Certificate2 certificate;
        try
        {
            certificate = Pkcs12Loader.Load(data, password ?? "");
        }
        catch (CryptographicException)
        {
            reader.Fail(Codes.KeystoreUnreadable, $"is not a PKCS#12 keystore that opens with the password from {spec.PasswordVar ?? "(no password variable)"}");
            return false;
        }

        using (certificate)
        {
            if (now >= certificate.NotAfter.ToUniversalTime())
            {
                reader.Fail(Codes.CertificateInvalid, $"the keystore's certificate expired at {certificate.NotAfter.ToUniversalTime():u}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses every <c>CERTIFICATE</c> block of a PEM file into <paramref name="certificates"/>. Returns false with the
    /// index of the first block that does not parse.
    /// </summary>
    private static bool ParseCertificates(byte[] pem, List<X509Certificate2> certificates, out int bad)
    {
        bad = 0;
        foreach (var (label, der) in PemBlocks(pem))
        {
            if (label != "CERTIFICATE")
            {
                continue;
            }

            try
            {
#if NET9_0_OR_GREATER
                certificates.Add(X509CertificateLoader.LoadCertificate(der));
#else
                certificates.Add(new X509Certificate2(der));
#endif
            }
            catch (CryptographicException)
            {
                bad = certificates.Count;
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<(string Label, byte[] Der)> PemBlocks(byte[] pem)
    {
        var text = Encoding.UTF8.GetString(pem);
        var blocks = new List<(string, byte[])>();
        int offset = 0;
        while (offset < text.Length && PemEncoding.TryFind(text.AsSpan(offset), out var fields))
        {
            var block = text.AsSpan(offset);
            blocks.Add((block[fields.Label].ToString(), Convert.FromBase64String(block[fields.Base64Data].ToString())));
            offset += fields.Location.End.Value;
        }

        return blocks;
    }

    private enum KeyMatch
    {
        Match,
        Mismatch,
        Unparseable,
    }

    /// <summary>Whether <c>tls.key</c> holds the private key of the leaf certificate.</summary>
    private static KeyMatch KeyMatches(X509Certificate2 leaf, byte[] crt, byte[] key)
    {
        var block = PemBlocks(key).FirstOrDefault(b => b.Label.EndsWith("PRIVATE KEY", StringComparison.Ordinal));
        if (block.Der is null)
        {
            return KeyMatch.Unparseable;
        }

        // .NET has no Ed25519 key type, so an Ed25519 pair is matched by deriving the public key from the private one.
        if (Ed25519.SeedOf(block.Der) is { } seed)
        {
            return leaf.PublicKey.Oid.Value == Ed25519.Oid
                && Ed25519.PublicKey(seed).AsSpan().SequenceEqual(leaf.PublicKey.EncodedKeyValue.RawData)
                    ? KeyMatch.Match
                    : KeyMatch.Mismatch;
        }

        try
        {
            using var pair = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(crt), Encoding.UTF8.GetString(key));
            return KeyMatch.Match;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return KeyParses(Encoding.UTF8.GetString(key)) ? KeyMatch.Mismatch : KeyMatch.Unparseable;
        }
    }

    private static bool KeyParses(string pem)
    {
        foreach (var create in new Func<AsymmetricAlgorithm>[] { RSA.Create, ECDsa.Create })
        {
            using var algorithm = create();
            try
            {
                algorithm.ImportFromPem(pem);
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
            }
        }

        return false;
    }

    private static string KeyAlgorithmOf(X509Certificate2 cert) => cert.PublicKey.Oid.Value switch
    {
        "1.2.840.113549.1.1.1" => "RSA",
        "1.2.840.10045.2.1" => "ECDSA",
        Ed25519.Oid => "Ed25519",
        var oid => oid ?? "unknown",
    };

    /// <summary>A <see cref="ConfigFile{T}"/> for a watched config file.</summary>
    private static object Watched(Type type, FileSpec spec, string path, object? initial, Func<string?> password, Func<DateTimeOffset> clock, Type configType)
    {
        object? Reload()
        {
            var problems = new List<Violation>();
            return TryLoad(spec, path, password, clock(), configType, problems, out var value) ? value : null;
        }

        var wrapper = typeof(ConfigFile<>).MakeGenericType(type);
        return Activator.CreateInstance(wrapper, BindingFlags.NonPublic | BindingFlags.Instance, null, [path, initial, (Func<object?>)Reload], null)!;
    }

    /// <summary>
    /// Reads a <c>pathEnv</c> variable from the app's configuration, which holds the environment variables; from the
    /// process environment only when there is none.
    /// </summary>
    public static Func<string, string?> Lookup(Microsoft.Extensions.Configuration.IConfiguration? configuration) =>
        configuration is null ? Environment.GetEnvironmentVariable : name => configuration[name];
}

/// <summary>Recursive DataAnnotations validation of an object graph.</summary>
internal static class Annotations
{
    public sealed record Problem(string Path, string Message, ValidationAttribute? Attribute);

    public static List<Problem> ValidateGraph(object value, string path)
    {
        var problems = new List<Problem>();
        Visit(value, path, problems, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return problems;
    }

    private static void Visit(object value, string path, List<Problem> problems, HashSet<object> seen)
    {
        if (!seen.Add(value))
        {
            return;
        }

        var type = value.GetType();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var propValue = prop.GetValue(value);
            var name = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            var context = new ValidationContext(value) { MemberName = prop.Name, DisplayName = prop.Name };
            foreach (var attr in prop.GetCustomAttributes(typeof(ValidationAttribute), true).Cast<ValidationAttribute>())
            {
                if (attr.GetValidationResult(propValue, context) is { } result && result != ValidationResult.Success)
                {
                    problems.Add(new Problem($"{path}.{name}", result.ErrorMessage ?? "is invalid", attr));
                }
            }

            switch (propValue)
            {
                case null or string:
                    break;
                case System.Collections.IEnumerable items:
                    int i = 0;
                    foreach (var item in items)
                    {
                        if (item is not null && IsObject(item.GetType()))
                        {
                            Visit(item, $"{path}.{name}[{i}]", problems, seen);
                        }

                        i++;
                    }

                    break;
                default:
                    if (IsObject(propValue.GetType()))
                    {
                        Visit(propValue, $"{path}.{name}", problems, seen);
                    }

                    break;
            }
        }

        if (value is IValidatableObject validatable)
        {
            foreach (var result in validatable.Validate(new ValidationContext(value)))
            {
                problems.Add(new Problem(path, result.ErrorMessage ?? "is invalid", null));
            }
        }
    }

    private static bool IsObject(Type t) =>
        t.IsClass && t != typeof(string) && t.Namespace?.StartsWith("System", StringComparison.Ordinal) != true;
}

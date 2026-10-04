using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docuconf.Contract;

namespace Docuconf.Runtime;

/// <summary>
/// Loads file inputs into the options instance and checks what the platform could not see before deploy
/// (SPEC §11.2 item 7): presence, size, format, the bound type's constraints, certificates and keystores.
/// </summary>
internal static class FileChecks
{
    public static void LoadAll(object target, ContractModel model, DocuconfSettings settings, List<Violation> violations)
    {
        var root = DocuconfBinder.Root(settings);
        foreach (var spec in model.Files.Values)
        {
            var path = root.Length == 0 ? spec.Path : Path.Join(root, spec.Path);
            bool exists = spec.Type == FileType.Tls ? Directory.Exists(path) : File.Exists(path);
            if (!exists)
            {
                if (spec.Required)
                {
                    violations.Add(new Violation(Codes.FileMissing, spec.Name, $"{spec.Path} does not exist"));
                }

                continue;
            }

            try
            {
                Load(target, spec, path, settings, violations);
            }
            catch (UnauthorizedAccessException)
            {
                violations.Add(new Violation(Codes.FileUnreadable, spec.Name,
                    $"{spec.Path} is not readable by this process. Secret volumes are owned by root; a non-root container needs the pod's fsGroup set."));
            }
            catch (IOException ex)
            {
                violations.Add(new Violation(Codes.FileUnreadable, spec.Name, $"{spec.Path} could not be read: {ex.Message}"));
            }
        }
    }

    private static void Load(object target, FileSpec spec, string path, DocuconfSettings settings, List<Violation> violations)
    {
        if (spec.MaxSize is { } maxSize && spec.Type != FileType.Tls && new FileInfo(path).Length > maxSize)
        {
            violations.Add(new Violation(Codes.FileTooLarge, spec.Name, $"{spec.Path} is larger than {maxSize} bytes"));
            return;
        }

        switch (spec.Type)
        {
            case FileType.Config:
                LoadConfig(target, spec, path, violations);
                break;
            case FileType.Text:
                LoadText(target, spec, path, violations);
                break;
            case FileType.Tls:
                DocuconfBinder.SetPath(target, spec.PropertyPath!, new TlsKeyPair { Directory = path });
                CheckTls(spec, path, settings.Clock(), violations);
                break;
            case FileType.CaBundle:
                DocuconfBinder.SetPath(target, spec.PropertyPath!, new CaBundle { Path = path });
                CheckCaBundle(spec, path, violations);
                break;
            case FileType.Keystore:
                DocuconfBinder.SetPath(target, spec.PropertyPath!, new Keystore { Path = path });
                var password = spec.PasswordPath is null ? null : DocuconfBinder.GetPath(target, spec.PasswordPath) as string;
                if (!Pkcs12Loader.TryLoad(File.ReadAllBytes(path), password, out _))
                {
                    violations.Add(new Violation(Codes.KeystoreUnreadable, spec.Name,
                        $"{spec.Path} is not a PKCS#12 keystore that opens with {spec.PasswordVar ?? "an empty password"}"));
                }

                break;
            case FileType.Binary:
                DocuconfBinder.SetPath(target, spec.PropertyPath!, new BinaryFile { Path = path });
                break;
        }
    }

    private static void LoadConfig(object target, FileSpec spec, string path, List<Violation> violations)
    {
        var type = spec.PropertyPath![^1].PropertyType;
        object? value;
        try
        {
            using var stream = File.OpenRead(path);
            value = JsonSerializer.Deserialize(stream, type, DocuconfBinder.JsonOptions);
        }
        catch (JsonException ex)
        {
            // Messages give the JSON path and line, never the value.
            violations.Add(new Violation(Codes.FileMalformed, spec.Name, $"{spec.Path}: {ex.Message}"));
            return;
        }

        if (value is null)
        {
            violations.Add(new Violation(Codes.FileMalformed, spec.Name, $"{spec.Path} is empty or null"));
            return;
        }

        foreach (var problem in Annotations.ValidateGraph(value, "$"))
        {
            violations.Add(new Violation(Codes.SchemaMismatch, spec.Name, $"{spec.Path}: {problem.Path}: {problem.Message}"));
        }

        DocuconfBinder.SetPath(target, spec.PropertyPath, value);
    }

    private static void LoadText(object target, FileSpec spec, string path, List<Violation> violations)
    {
        var text = File.ReadAllText(path);
        var label = spec.Secret ? "" : $" ({spec.Path})";
        if (spec.MinLength is { } min && text.Length < min)
        {
            violations.Add(new Violation(Codes.OutOfRange, spec.Name, $"is shorter than {min} characters{label}"));
        }

        if (spec.MaxLength is { } max && text.Length > max)
        {
            violations.Add(new Violation(Codes.OutOfRange, spec.Name, $"is longer than {max} characters{label}"));
        }

        if (spec.Pattern is { } pattern && !Regex.IsMatch(text, pattern))
        {
            violations.Add(new Violation(Codes.PatternMismatch, spec.Name, $"does not match {pattern}{label}"));
        }

        DocuconfBinder.SetPath(target, spec.PropertyPath!, text);
    }

    private static void CheckTls(FileSpec spec, string dir, DateTimeOffset now, List<Violation> violations)
    {
        var certPath = Path.Combine(dir, "tls.crt");
        var keyPath = Path.Combine(dir, "tls.key");
        var caPath = Path.Combine(dir, "ca.crt");
        foreach (var required in spec.RequireCA ? new[] { certPath, keyPath, caPath } : [certPath, keyPath])
        {
            if (!File.Exists(required))
            {
                violations.Add(new Violation(Codes.FileMissing, spec.Name, $"{spec.Path}/{Path.GetFileName(required)} does not exist"));
                return;
            }
        }

        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPem(File.ReadAllText(certPath));
        }
        catch (CryptographicException)
        {
            violations.Add(new Violation(Codes.CertificateInvalid, spec.Name, $"{spec.Path}/tls.crt is not a PEM certificate"));
            return;
        }

        using (cert)
        {
            try
            {
                using var withKey = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                violations.Add(new Violation(Codes.KeyMismatch, spec.Name, $"{spec.Path}/tls.key is not a valid private key for tls.crt"));
            }

            if (now < cert.NotBefore.ToUniversalTime())
            {
                violations.Add(new Violation(Codes.CertificateInvalid, spec.Name, $"the certificate is not valid until {cert.NotBefore.ToUniversalTime():u}"));
            }
            else if (now > cert.NotAfter.ToUniversalTime())
            {
                violations.Add(new Violation(Codes.CertificateInvalid, spec.Name, $"the certificate expired at {cert.NotAfter.ToUniversalTime():u}"));
            }
            else if (spec.MinRemaining is { } minRemaining && cert.NotAfter.ToUniversalTime() - now < GoDuration.Parse(minRemaining))
            {
                violations.Add(new Violation(Codes.CertificateExpiring, spec.Name,
                    $"the certificate expires at {cert.NotAfter.ToUniversalTime():u}, sooner than the required {minRemaining}"));
            }

            foreach (var name in spec.DnsNames ?? [])
            {
                if (!cert.MatchesHostname(name))
                {
                    violations.Add(new Violation(Codes.CertificateNameMismatch, spec.Name, $"the certificate does not cover {name}"));
                }
            }

            if (spec.KeyAlgorithms is { } allowed)
            {
                var algorithm = KeyAlgorithmOf(cert);
                if (!allowed.Contains(algorithm))
                {
                    violations.Add(new Violation(Codes.CertificateInvalid, spec.Name,
                        $"the certificate uses a {algorithm} key; allowed: {string.Join(", ", allowed)}"));
                }
            }

            if (spec.RequireCA)
            {
                var ca = new X509Certificate2Collection();
                ca.ImportFromPemFile(caPath);
                var chainCerts = new X509Certificate2Collection();
                chainCerts.ImportFromPemFile(certPath);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(ca);
                chain.ChainPolicy.ExtraStore.AddRange(chainCerts);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationTime = now.UtcDateTime;
                if (!chain.Build(cert))
                {
                    var status = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
                    violations.Add(new Violation(Codes.CertificateInvalid, spec.Name, $"the certificate does not chain to ca.crt: {status}"));
                }
            }
        }
    }

    private static string KeyAlgorithmOf(X509Certificate2 cert) => cert.PublicKey.Oid.Value switch
    {
        "1.2.840.113549.1.1.1" => "RSA",
        "1.2.840.10045.2.1" => "ECDSA",
        "1.3.101.112" => "Ed25519",
        var oid => oid ?? "unknown",
    };

    private static void CheckCaBundle(FileSpec spec, string path, List<Violation> violations)
    {
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPemFile(path);
        }
        catch (CryptographicException)
        {
            violations.Add(new Violation(Codes.FileMalformed, spec.Name, $"{spec.Path} contains a malformed certificate"));
            return;
        }

        int min = spec.MinCertificates ?? 1;
        if (certificates.Count < min)
        {
            violations.Add(new Violation(Codes.FileMalformed, spec.Name, $"{spec.Path} holds {certificates.Count} certificates; at least {min} required"));
        }

        foreach (var c in certificates)
        {
            c.Dispose();
        }
    }
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

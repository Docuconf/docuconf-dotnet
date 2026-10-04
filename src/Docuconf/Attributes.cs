namespace Docuconf;

/// <summary>
/// Marks an options class as part of a service's configuration contract.
/// Every class with the same <see cref="Service"/> in an assembly is exported into one contract.
/// </summary>
/// <param name="service">The service name: a DNS label such as <c>billing-api</c>.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConfigContractAttribute(string service) : Attribute
{
    /// <summary>The service name: a DNS label such as <c>billing-api</c>.</summary>
    public string Service { get; } = service;

    /// <summary>
    /// The configuration section the class binds to, such as <c>Billing</c>.
    /// Environment variable names are derived from it: <c>Billing:Port</c> becomes <c>BILLING__PORT</c>.
    /// Empty binds to the configuration root.
    /// </summary>
    public string Section { get; init; } = "";
}

/// <summary>
/// The value is secret. The platform must supply it from a Kubernetes Secret, it may not have a default,
/// and docuconf never prints it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>The value is a URL whose scheme must be one of <see cref="Schemes"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UrlSchemesAttribute(params string[] schemes) : Attribute
{
    /// <summary>The allowed schemes, such as <c>https</c> or <c>postgres</c>.</summary>
    public IReadOnlyList<string> Schemes { get; } = schemes;
}

/// <summary>Overrides the environment variable name derived from the configuration path.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EnvNameAttribute(string name) : Attribute
{
    /// <summary>The environment variable name, in UPPER_SNAKE_CASE.</summary>
    public string Name { get; } = name;
}

/// <summary>
/// The value comes from a configuration provider the platform does not control, such as Azure Key Vault,
/// so it is left out of the contract.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExternalAttribute(string provider) : Attribute
{
    /// <summary>The provider that supplies the value, for documentation.</summary>
    public string Provider { get; } = provider;
}

/// <summary>How the app picks up a changed file input.</summary>
public enum Reload
{
    /// <summary>The app reads the file at startup, so a changed source needs a rollout.</summary>
    Restart,

    /// <summary>The app reloads the file itself.</summary>
    Watch,
}

/// <summary>Public-key algorithms a TLS certificate may use.</summary>
[Flags]
public enum KeyAlgorithms
{
    /// <summary>No restriction.</summary>
    Any = 0,

    /// <summary>RSA keys.</summary>
    RSA = 1,

    /// <summary>ECDSA keys.</summary>
    ECDSA = 2,

    /// <summary>Ed25519 keys.</summary>
    Ed25519 = 4,
}

/// <summary>Base class for attributes that declare a file input.</summary>
public abstract class FileInputAttribute(string path) : Attribute
{
    /// <summary>The absolute path the app reads: a directory for TLS key pairs, a file otherwise.</summary>
    public string Path { get; } = path;

    /// <summary>The input name in the contract. Defaults to the property name in kebab-case.</summary>
    public string? Name { get; init; }

    /// <summary>An environment variable the platform sets to <see cref="Path"/>.</summary>
    public string? PathEnv { get; init; }

    /// <summary>How the app picks up a changed file.</summary>
    public Reload Reload { get; init; } = Reload.Restart;

    /// <summary>Upper bound on the file size in bytes. Zero means no limit.</summary>
    public long MaxSize { get; init; }
}

/// <summary>
/// A JSON configuration file, deserialized into the property's type. The contract carries a JSON Schema
/// generated from that type, so the platform validates the file against the same type the app binds.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigFileAttribute(string path) : FileInputAttribute(path);

/// <summary>
/// A TLS key pair in the <c>kubernetes.io/tls</c> layout (<c>tls.crt</c>, <c>tls.key</c>, optional <c>ca.crt</c>).
/// The property must be a <see cref="TlsKeyPair"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TlsFileAttribute(string path) : FileInputAttribute(path)
{
    /// <summary>Names the certificate must cover.</summary>
    public string[] DnsNames { get; init; } = [];

    /// <summary>Allowed public-key algorithms.</summary>
    public KeyAlgorithms KeyAlgorithms { get; init; } = KeyAlgorithms.Any;

    /// <summary>Least remaining validity, as a Go duration such as <c>720h</c>.</summary>
    public string? MinRemaining { get; init; }

    /// <summary>Whether <c>ca.crt</c> must be present and the certificate must chain to it.</summary>
    public bool RequireCA { get; init; }
}

/// <summary>A PEM file of CA certificates. The property must be a <see cref="CaBundle"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CaBundleFileAttribute(string path) : FileInputAttribute(path)
{
    /// <summary>Least number of certificates the bundle must hold.</summary>
    public int MinCertificates { get; init; } = 1;
}

/// <summary>A PKCS#12 keystore. The property must be a <see cref="Keystore"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class KeystoreFileAttribute(string path) : FileInputAttribute(path)
{
    /// <summary>The name of the secret property, in the same class, that holds the keystore password.</summary>
    public string? PasswordProperty { get; init; }
}

/// <summary>A text file, such as a licence key. The property must be a <see cref="string"/> and receives the content.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class TextFileAttribute(string path) : FileInputAttribute(path)
{
    /// <summary>An RE2-compatible pattern the content must match.</summary>
    public string? Pattern { get; init; }

    /// <summary>Least length in characters.</summary>
    public int MinLength { get; init; }

    /// <summary>Greatest length in characters. Zero means no limit.</summary>
    public int MaxLength { get; init; }
}

/// <summary>Opaque bytes, such as a GeoIP database. The property must be a <see cref="BinaryFile"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BinaryFileAttribute(string path) : FileInputAttribute(path);

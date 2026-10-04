using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Docuconf;

/// <summary>
/// A TLS key pair mounted in the <c>kubernetes.io/tls</c> layout. docuconf checks it at startup;
/// <see cref="Current"/> returns the certificate and picks up rotated files.
/// </summary>
public sealed class TlsKeyPair
{
    private readonly object _gate = new();
    private X509Certificate2? _current;
    private DateTime _loadedStamp;
    private DateTime _nextCheck;

    /// <summary>The directory holding <c>tls.crt</c>, <c>tls.key</c> and optionally <c>ca.crt</c>.</summary>
    public string Directory { get; internal set; } = "";

    /// <summary>Path of the certificate chain, leaf first.</summary>
    public string CertificatePath => Path.Combine(Directory, "tls.crt");

    /// <summary>Path of the private key.</summary>
    public string KeyPath => Path.Combine(Directory, "tls.key");

    /// <summary>Path of the issuing CA certificate, when present.</summary>
    public string CaPath => Path.Combine(Directory, "ca.crt");

    /// <summary>Loads the certificate with its private key.</summary>
    public X509Certificate2 Load() => LoadPem(CertificatePath, KeyPath);

    /// <summary>
    /// The certificate, reloaded when the mounted files change. Suitable for a Kestrel
    /// <c>ServerCertificateSelector</c>, which runs on every handshake: the files are checked at most every 30 seconds.
    /// </summary>
    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (_current is not null && now < _nextCheck)
                {
                    return _current;
                }

                _nextCheck = now.AddSeconds(30);
                // Kubernetes swaps a ..data symlink, so compare the files' own timestamps.
                var stamp = File.GetLastWriteTimeUtc(CertificatePath);
                if (_current is null || stamp != _loadedStamp)
                {
                    _current = Load();
                    _loadedStamp = stamp;
                }

                return _current;
            }
        }
    }

    internal static X509Certificate2 LoadPem(string certPath, string keyPath)
    {
        var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        // Windows needs an exportable copy for SslStream; elsewhere this is harmless.
        return OperatingSystem.IsWindows()
            ? Pkcs12Loader.Load(cert.Export(X509ContentType.Pkcs12), null)
            : cert;
    }
}

/// <summary>A mounted PEM bundle of CA certificates.</summary>
public sealed class CaBundle
{
    /// <summary>Path of the bundle.</summary>
    public string Path { get; internal set; } = "";

    /// <summary>Loads every certificate in the bundle.</summary>
    public X509Certificate2Collection Load()
    {
        var certificates = new X509Certificate2Collection();
        certificates.ImportFromPemFile(Path);
        return certificates;
    }
}

/// <summary>A mounted PKCS#12 keystore.</summary>
public sealed class Keystore
{
    /// <summary>Path of the keystore.</summary>
    public string Path { get; internal set; } = "";

    /// <summary>Loads the certificate and private key with the given password.</summary>
    public X509Certificate2 Load(string? password) => Pkcs12Loader.Load(File.ReadAllBytes(Path), password);
}

/// <summary>A mounted binary file.</summary>
public sealed class BinaryFile
{
    /// <summary>Path of the file.</summary>
    public string Path { get; internal set; } = "";

    /// <summary>Opens the file for reading.</summary>
    public Stream OpenRead() => File.OpenRead(Path);
}

internal static class Pkcs12Loader
{
    public static X509Certificate2 Load(byte[] data, string? password)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(data, password, X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(data, password, X509KeyStorageFlags.Exportable);
#endif
    }

    public static bool TryLoad(byte[] data, string? password, out CryptographicException? error)
    {
        try
        {
            using var _ = Load(data, password);
            error = null;
            return true;
        }
        catch (CryptographicException ex)
        {
            error = ex;
            return false;
        }
    }
}

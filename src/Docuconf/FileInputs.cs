using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Primitives;

namespace Docuconf;

/// <summary>
/// A TLS key pair mounted in the <c>kubernetes.io/tls</c> layout. docuconf checks it at startup;
/// <see cref="Current"/> returns the certificate and picks up rotated files once they pass the same checks.
/// </summary>
public sealed class TlsKeyPair : IWatchedInput
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

    /// <summary>Set when docuconf loaded the pair: reloads it only after the startup checks pass.</summary>
    internal Watcher<X509Certificate2>? Watcher { get; set; }

    /// <summary>Loads the certificate with its private key, from the files as they are now, unchecked.</summary>
    public X509Certificate2 Load() => LoadPem(CertificatePath, KeyPath);

    /// <summary>
    /// The certificate, reloaded when the mounted files change. Suitable for a Kestrel
    /// <c>ServerCertificateSelector</c>, which runs on every handshake: the files are checked at most every 30 seconds.
    /// A changed pair replaces the certificate only when it passes the startup checks (key match, expiry, names,
    /// chain); until then the previous one stays.
    /// </summary>
    public X509Certificate2 Current
    {
        get
        {
            if (Watcher is { } watcher)
            {
                return watcher.Current;
            }

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

    /// <inheritdoc />
    public string Input => Watcher?.Input ?? "";

    /// <inheritdoc />
    public ReloadStatus Status => Watcher?.Status ?? Unwatched.Status;

    /// <inheritdoc />
    public IChangeToken GetReloadToken() => Watcher?.GetReloadToken() ?? Unwatched.Token;

    /// <summary>
    /// Calls <paramref name="callback"/> with the new certificate after a changed pair passes the checks and replaces
    /// the old one; never for a rejected change. While a callback is registered the files are looked at every 30
    /// seconds without a read of <see cref="Current"/>. A callback that throws is logged, by input name and exception
    /// type, and the others still run. Dispose the result to unsubscribe.
    /// </summary>
    public IDisposable OnChange(Action<X509Certificate2> callback) => Watcher?.OnChange(callback) ?? Unwatched.Subscription;

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
public sealed class Keystore : IWatchedInput
{
    /// <summary>Path of the keystore.</summary>
    public string Path { get; internal set; } = "";

    /// <summary>Set when docuconf loaded the keystore: reloads it with the password read at startup.</summary>
    internal Watcher<X509Certificate2>? Watcher { get; set; }

    /// <summary>Loads the certificate and private key with the given password.</summary>
    public X509Certificate2 Load(string? password) => Pkcs12Loader.Load(File.ReadAllBytes(Path), password);

    /// <summary>
    /// The certificate and private key, opened with the password read at startup and reloaded when the file changes:
    /// at most every 30 seconds, on the read that notices it. A changed keystore replaces the value only when it passes
    /// the startup checks; one that does not open with that password is rejected as <c>keystore_unreadable</c> and the
    /// previous value stays. Environment variables do not change in a running process, so rotating the password needs
    /// a rollout.
    /// </summary>
    /// <exception cref="InvalidOperationException">docuconf did not load this keystore.</exception>
    public X509Certificate2 Current => Watcher?.Current
        ?? throw new InvalidOperationException("This Keystore was not loaded by docuconf; open it with Load(password).");

    /// <inheritdoc />
    public string Input => Watcher?.Input ?? "";

    /// <inheritdoc />
    public ReloadStatus Status => Watcher?.Status ?? Unwatched.Status;

    /// <inheritdoc />
    public IChangeToken GetReloadToken() => Watcher?.GetReloadToken() ?? Unwatched.Token;

    /// <summary>
    /// Calls <paramref name="callback"/> with the new certificate after a changed keystore passes the checks and
    /// replaces the old one; never for a rejected change. While a callback is registered the file is looked at every
    /// 30 seconds without a read of <see cref="Current"/>. A callback that throws is logged, by input name and
    /// exception type, and the others still run. Dispose the result to unsubscribe.
    /// </summary>
    public IDisposable OnChange(Action<X509Certificate2> callback) => Watcher?.OnChange(callback) ?? Unwatched.Subscription;
}

/// <summary>
/// A config file declared with <see cref="Reload.Watch"/> (SPEC §4.6.2): <see cref="Value"/> reloads it when the
/// mounted file changes. Declare the property as <c>ConfigFile&lt;Routes&gt;</c> with <see cref="ConfigFileAttribute"/>;
/// the contract's schema comes from <typeparamref name="T"/>. The contract-first mode returns a
/// <c>ConfigFile&lt;JsonNode&gt;</c> for a watched config file.
/// </summary>
/// <remarks>
/// The file is checked at startup like any config file. A changed file is reread at most every 10 seconds, when
/// <see cref="Value"/> is read, or from a timer while an <see cref="OnChange"/> callback is registered; one that no
/// longer parses or binds is rejected (see <see cref="Status"/>), and the last good value stays.
/// </remarks>
/// <typeparam name="T">The type the file deserializes into.</typeparam>
public sealed class ConfigFile<T> : IWatchedInput
    where T : class
{
    private readonly Watcher<T>? _watcher;

    /// <summary>A config file that is not loaded: <see cref="Present"/> is false until docuconf binds it.</summary>
    public ConfigFile()
    {
    }

    internal ConfigFile(string path, Watcher<T> watcher)
    {
        Path = path;
        _watcher = watcher;
    }

    /// <summary>Where the file is read from.</summary>
    public string Path { get; } = "";

    /// <summary>Whether the file was found at startup. A required file is always present once startup succeeds.</summary>
    public bool Present => _watcher is not null;

    /// <summary>The file's current content, reloaded when the file changes; null for an absent optional file.</summary>
    public T? Value => _watcher?.Current;

    /// <inheritdoc />
    public string Input => _watcher?.Input ?? "";

    /// <inheritdoc />
    public ReloadStatus Status => _watcher?.Status ?? Unwatched.Status;

    /// <inheritdoc />
    public IChangeToken GetReloadToken() => _watcher?.GetReloadToken() ?? Unwatched.Token;

    /// <summary>
    /// Calls <paramref name="callback"/> with the new value after a changed file passes the checks and replaces the
    /// old one; never for a rejected change. While a callback is registered the file is looked at every 10 seconds
    /// without a read of <see cref="Value"/>. A callback that throws is logged, by input name and exception type, and
    /// the others still run. Dispose the result to unsubscribe. An absent optional file never calls it.
    /// </summary>
    public IDisposable OnChange(Action<T> callback) => _watcher?.OnChange(callback) ?? Unwatched.Subscription;

    /// <summary>Returns the file's path; the content may be secret.</summary>
    public override string ToString() => $"ConfigFile<{typeof(T).Name}>({Path})";
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

using System.Reflection;
using System.Text.Json.Nodes;

namespace Docuconf.Contract;

/// <summary>A service's configuration contract, as read from its options classes.</summary>
public sealed class ContractModel
{
    /// <summary>The service name.</summary>
    public required string Service { get; init; }

    /// <summary>Environment variable inputs, keyed by name.</summary>
    public SortedDictionary<string, VarSpec> Vars { get; } = new(StringComparer.Ordinal);

    /// <summary>File inputs, keyed by input name.</summary>
    public SortedDictionary<string, FileSpec> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Config-file overlays the platform may mount (SPEC §4.7), keyed by name.</summary>
    public SortedDictionary<string, OverlaySpec> Overlays { get; } = new(StringComparer.Ordinal);

    /// <summary>Profiles from <c>appsettings.{Environment}.json</c>, or null when there are none.</summary>
    public ProfilesSpec? Profiles { get; set; }

    /// <summary>Settings that could not be put in the contract, with the reason.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Properties outside the contract that still bind from configuration: [External] ones and shapes env vars cannot carry.</summary>
    internal List<(string Key, IReadOnlyList<PropertyInfo> Path)> Unmodeled { get; } = [];
}

/// <summary>A config-file overlay: a JSON appsettings file the platform mounts (SPEC §4.7).</summary>
/// <param name="Name">The overlay's name, a DNS label.</param>
/// <param name="Path">Where the platform mounts the file.</param>
/// <param name="ReloadOnChange">Whether the app reloads the file when it changes.</param>
/// <param name="Description">What the overlay is for, if given.</param>
public sealed record OverlaySpec(string Name, string Path, bool ReloadOnChange, string? Description);

/// <summary>Contract variable types (SPEC §4.3).</summary>
public enum VarType
{
    /// <summary>A string.</summary>
    String,
    /// <summary>A 64-bit integer.</summary>
    Int,
    /// <summary>A floating-point number.</summary>
    Float,
    /// <summary>true or false.</summary>
    Bool,
    /// <summary>A duration.</summary>
    Duration,
    /// <summary>A URL.</summary>
    Url,
    /// <summary>One of a fixed set of strings.</summary>
    Enum,
    /// <summary>A list of strings or integers.</summary>
    List,
    /// <summary>A structured value sent as JSON, checked against <see cref="VarSpec.Schema"/>.</summary>
    Json,
}

/// <summary>One environment variable in the contract.</summary>
public sealed class VarSpec
{
    /// <summary>The environment variable name.</summary>
    public required string Name { get; init; }
    /// <summary>The .NET configuration key, such as <c>Billing:Port</c>.</summary>
    public required string ConfigKey { get; init; }
    /// <summary>The contract type.</summary>
    public required VarType Type { get; init; }
    /// <summary>The description.</summary>
    public required string Description { get; init; }
    /// <summary>Whether the platform must supply it.</summary>
    public bool Required { get; set; }
    /// <summary>Whether it is secret.</summary>
    public bool Secret { get; init; }
    /// <summary>The default: string, long, double, bool, string list, long list, or a JSON node for a json variable.</summary>
    public object? Default { get; set; }
    /// <summary>Numeric lower bound (int, float) or duration lower bound in Go syntax.</summary>
    public object? Min { get; init; }
    /// <summary>Numeric upper bound (int, float) or duration upper bound in Go syntax.</summary>
    public object? Max { get; init; }
    /// <summary>Least string length.</summary>
    public int? MinLength { get; init; }
    /// <summary>Greatest string length.</summary>
    public int? MaxLength { get; init; }
    /// <summary>RE2 pattern.</summary>
    public string? Pattern { get; init; }
    /// <summary>Allowed URL schemes.</summary>
    public IReadOnlyList<string>? Schemes { get; init; }
    /// <summary>Allowed enum values.</summary>
    public IReadOnlyList<string>? Values { get; init; }
    /// <summary>List item type: <c>string</c> or <c>int</c>.</summary>
    public string? Items { get; init; }
    /// <summary>Least list length.</summary>
    public int? MinItems { get; init; }
    /// <summary>Greatest list length.</summary>
    public int? MaxItems { get; init; }
    /// <summary>A json variable's JSON Schema, generated from its type.</summary>
    public JsonNode? Schema { get; init; }

    internal IReadOnlyList<PropertyInfo>? PropertyPath { get; init; }
    internal Type? ClrType { get; init; }
}

/// <summary>Contract file input types (SPEC §4.6).</summary>
public enum FileType
{
    /// <summary>A structured config file.</summary>
    Config,
    /// <summary>A TLS key pair directory.</summary>
    Tls,
    /// <summary>A PEM CA bundle.</summary>
    CaBundle,
    /// <summary>A keystore.</summary>
    Keystore,
    /// <summary>A text file.</summary>
    Text,
    /// <summary>Opaque bytes.</summary>
    Binary,
}

/// <summary>One file input in the contract.</summary>
public sealed class FileSpec
{
    /// <summary>The input name.</summary>
    public required string Name { get; init; }
    /// <summary>The file type.</summary>
    public required FileType Type { get; init; }
    /// <summary>The description.</summary>
    public required string Description { get; init; }
    /// <summary>Whether the platform must supply it.</summary>
    public bool Required { get; init; }
    /// <summary>Whether its content is secret.</summary>
    public bool Secret { get; init; }
    /// <summary>Where the app reads it.</summary>
    public required string Path { get; init; }
    /// <summary>Environment variable set to the path.</summary>
    public string? PathEnv { get; init; }
    /// <summary>How the app picks up changes.</summary>
    public Reload Reload { get; init; }
    /// <summary>Size limit in bytes.</summary>
    public long? MaxSize { get; init; }
    /// <summary>Config file format (always json in .NET).</summary>
    public string? Format { get; init; }
    /// <summary>JSON Schema of a config file, generated from its type.</summary>
    public JsonNode? Schema { get; init; }
    /// <summary>TLS: names the certificate must cover.</summary>
    public IReadOnlyList<string>? DnsNames { get; init; }
    /// <summary>TLS: allowed key algorithms.</summary>
    public IReadOnlyList<string>? KeyAlgorithms { get; init; }
    /// <summary>TLS: least remaining validity, Go syntax.</summary>
    public string? MinRemaining { get; init; }
    /// <summary>TLS: whether ca.crt is required.</summary>
    public bool RequireCA { get; init; }
    /// <summary>CA bundle: least number of certificates.</summary>
    public int? MinCertificates { get; init; }
    /// <summary>Keystore: the secret variable holding its password.</summary>
    public string? PasswordVar { get; init; }
    /// <summary>Text: RE2 pattern.</summary>
    public string? Pattern { get; init; }
    /// <summary>Text: least length.</summary>
    public int? MinLength { get; init; }
    /// <summary>Text: greatest length.</summary>
    public int? MaxLength { get; init; }

    internal IReadOnlyList<PropertyInfo>? PropertyPath { get; init; }
    internal IReadOnlyList<PropertyInfo>? PasswordPath { get; init; }
}

/// <summary>Values from <c>appsettings.{Environment}.json</c> files (SPEC §4.4).</summary>
public sealed class ProfilesSpec
{
    /// <summary>The variable that selects the profile.</summary>
    public required string Selector { get; init; }
    /// <summary>The profile in effect when the selector is unset.</summary>
    public string Default { get; init; } = "Production";
    /// <summary>Per-profile defaults, keyed by profile then variable name.</summary>
    public SortedDictionary<string, SortedDictionary<string, object>> Defaults { get; } = new(StringComparer.Ordinal);
}

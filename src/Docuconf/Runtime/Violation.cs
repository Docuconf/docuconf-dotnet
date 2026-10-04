using System.Runtime.CompilerServices;

namespace Docuconf.Runtime;

/// <summary>Stable error codes (SPEC §11.2).</summary>
public static class Codes
{
#pragma warning disable CS1591 // The names are the documentation.
    public const string MissingRequired = "missing_required";
    public const string InvalidType = "invalid_type";
    public const string OutOfRange = "out_of_range";
    public const string PatternMismatch = "pattern_mismatch";
    public const string NotInEnum = "not_in_enum";
    public const string InvalidScheme = "invalid_scheme";
    public const string TooFewItems = "too_few_items";
    public const string TooManyItems = "too_many_items";
    public const string FileMissing = "file_missing";
    public const string FileUnreadable = "file_unreadable";
    public const string FileTooLarge = "file_too_large";
    public const string FileMalformed = "file_malformed";
    public const string SchemaMismatch = "schema_mismatch";
    public const string CertificateInvalid = "certificate_invalid";
    public const string CertificateExpiring = "certificate_expiring";
    public const string CertificateNameMismatch = "certificate_name_mismatch";
    public const string KeyMismatch = "key_mismatch";
    public const string KeystoreUnreadable = "keystore_unreadable";
#pragma warning restore CS1591
}

/// <summary>One configuration problem found at startup.</summary>
/// <param name="Code">A stable code from <see cref="Codes"/>.</param>
/// <param name="Input">The environment variable or file input name.</param>
/// <param name="Message">What is wrong. Never contains a secret value.</param>
public sealed record Violation(string Code, string Input, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"[{Code}] {Input}: {Message}";
}

/// <summary>Violations found while binding an options instance, kept until it is validated.</summary>
internal static class BindState
{
    private static readonly ConditionalWeakTable<object, List<Violation>> Table = new();

    public static void Set(object options, List<Violation> violations) => Table.AddOrUpdate(options, violations);

    public static List<Violation> Get(object options) => Table.TryGetValue(options, out var v) ? v : [];
}

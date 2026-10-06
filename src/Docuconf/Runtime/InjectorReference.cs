namespace Docuconf.Runtime;

/// <summary>
/// Recognises references that an injector (Bank-Vaults vault-env, <c>op run</c>, vals) should have replaced with
/// the real value before the app started (SPEC §4.5.1). Seeing one at startup means the injector did not run.
/// </summary>
internal static class InjectorReference
{
    private static readonly string[] Schemes = ["vault:", "op://", "ref+"];

    /// <summary>The reference scheme <paramref name="value"/> starts with, or null when it is not a reference.</summary>
    public static string? SchemeOf(string? value) =>
        value is null ? null : Schemes.FirstOrDefault(s => value.StartsWith(s, StringComparison.Ordinal));
}

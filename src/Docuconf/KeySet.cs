using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Docuconf;

/// <summary>
/// A set of secret keys that are all valid at once, so a key can be rotated without an outage: the contract's
/// <c>keySet</c> type (SPEC §4.3, §6.1). It is for the side that verifies: webhook signatures, inbound API keys, JWT
/// HMAC verification, cookie-signing fallbacks.
/// </summary>
/// <remarks>
/// <para>
/// Declare it as a property of type <see cref="KeySet"/>; <see cref="KeySetAttribute"/> sets its bounds. It is always
/// secret: the platform supplies it from a Kubernetes Secret, as one value holding <c>old,new</c> during a rotation
/// (<see cref="CsvAttribute"/> changes the comma), and it has no default.
/// </para>
/// <para>
/// Keys are never trimmed, and an empty key (a stray separator) is always <c>out_of_range</c>. <see cref="ToString"/>,
/// the debugger and <c>System.Text.Json</c> show <c>***</c>, never a key.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [KeySet(KeyMinLength = 32, KeyMaxLength = 256)]
/// public KeySet? WebhookKeys { get; set; }
///
/// bool ok = options.WebhookKeys?.Verify(key =&gt;
///     CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), signature)) == true;
/// </code>
/// </example>
[DebuggerDisplay("KeySet {Count} keys: ***")]
[JsonConverter(typeof(KeySetJsonConverter))]
public sealed class KeySet
{
    private readonly string[] _keys;

    /// <summary>A key set holding <paramref name="keys"/>, in order.</summary>
    public KeySet(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys.ToArray();
        if (_keys.Any(k => k is null))
        {
            throw new ArgumentException("A key set cannot hold null.", nameof(keys));
        }
    }

    /// <summary>The keys, in the order the platform gave them.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <summary>The number of keys.</summary>
    public int Count => _keys.Length;

    /// <summary>
    /// Whether <paramref name="candidate"/> is one of the keys, such as an API key a caller presents. It is compared with
    /// every key in constant time, so the time taken does not say which key matched, or how much of one; it depends only
    /// on the number of keys and the lengths involved.
    /// </summary>
    public bool Contains(string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(candidate);
        bool found = false;
        foreach (var key in _keys)
        {
            found |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), bytes);
        }

        return found;
    }

    /// <summary>
    /// Calls <paramref name="check"/> with each key, as UTF-8 bytes, and returns whether any call returned true. Use it
    /// for checks that need the key itself, such as an HMAC. Every key is tried, even after one matches, so the time
    /// taken does not say which key matched; <paramref name="check"/> should compare in constant time itself, with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// </summary>
    public bool Verify(Func<byte[], bool> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        bool ok = false;
        foreach (var key in _keys)
        {
            ok |= check(Encoding.UTF8.GetBytes(key));
        }

        return ok;
    }

    /// <summary>Returns <c>***</c>: the keys are secret.</summary>
    public override string ToString() => "***";

    private sealed class KeySetJsonConverter : JsonConverter<KeySet>
    {
        public override KeySet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new JsonException("A KeySet is secret and is not read from JSON.");

        public override void Write(Utf8JsonWriter writer, KeySet value, JsonSerializerOptions options) => writer.WriteStringValue("***");
    }
}

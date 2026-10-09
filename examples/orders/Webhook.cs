using System.Security.Cryptography;
using Docuconf;

namespace Orders.Api;

/// <summary>Checks the signature on incoming payment webhooks against the key set in <c>WEBHOOK_KEYS</c>.</summary>
public static class Webhook
{
    /// <summary>The largest body <c>POST /webhooks/payments</c> reads.</summary>
    public const int MaxBody = 1 << 20;

    /// <summary>
    /// Whether <paramref name="signature"/>, the hex-encoded HMAC-SHA256 of <paramref name="body"/>, was made with any
    /// of <paramref name="keys"/>. Accepting every key in the set is what lets a key be rotated: during the overlap
    /// the old and the new key both work.
    /// </summary>
    public static bool Verify(KeySet? keys, byte[] body, string? signature)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(signature ?? "");
        }
        catch (FormatException)
        {
            return false;
        }

        // Tries every key, so the time taken does not say which one matched; each comparison is constant-time too.
        return keys?.Verify(key => CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, body), expected)) == true;
    }

    /// <summary>Reads at most <see cref="MaxBody"/> bytes of <paramref name="body"/>; null when it is longer.</summary>
    public static async Task<byte[]?> ReadBody(Stream body)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + n > MaxBody)
            {
                return null;
            }

            buffer.Write(chunk, 0, n);
        }

        return buffer.ToArray();
    }
}

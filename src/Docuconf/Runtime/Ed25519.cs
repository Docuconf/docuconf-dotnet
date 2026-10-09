using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;

namespace Docuconf.Runtime;

/// <summary>
/// Just enough Ed25519 (RFC 8032) to check that a <c>tls.key</c> belongs to its certificate: .NET has no Ed25519 key
/// type, so <c>X509Certificate2.CreateFromPem</c> cannot pair them. Derives the public key from a PKCS#8 private key.
/// Used once per boot, so it favours clarity over speed, and it never signs anything.
/// </summary>
internal static class Ed25519
{
    public const string Oid = "1.3.101.112";

    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly (BigInteger X, BigInteger Y) Base = (
        BigInteger.Parse("15112221349535400772501151409588531511454012693041857206046113283949847762202", System.Globalization.CultureInfo.InvariantCulture),
        BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033603165251855960", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The 32-byte seed of a PKCS#8 Ed25519 private key, or null when <paramref name="der"/> is not one.</summary>
    public static byte[]? SeedOf(byte[] der)
    {
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var info = reader.ReadSequence();
            _ = info.ReadInteger();
            var algorithm = info.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != Oid)
            {
                return null;
            }

            var wrapped = info.ReadOctetString();
            var seed = new AsnReader(wrapped, AsnEncodingRules.DER).ReadOctetString();
            return seed.Length == 32 ? seed : null;
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    /// <summary>The 32-byte public key for a private key seed.</summary>
    public static byte[] PublicKey(byte[] seed)
    {
        var h = SHA512.HashData(seed);
        var a = h[..32];
        a[0] &= 248;
        a[31] &= 127;
        a[31] |= 64;
        var scalar = new BigInteger(a, isUnsigned: true, isBigEndian: false);

        // Double and add, in affine coordinates.
        (BigInteger X, BigInteger Y) result = (0, 1), addend = Base;
        while (scalar > 0)
        {
            if (!scalar.IsEven)
            {
                result = Add(result, addend);
            }

            addend = Add(addend, addend);
            scalar >>= 1;
        }

        var encoded = result.Y.ToByteArray(isUnsigned: true, isBigEndian: false);
        Array.Resize(ref encoded, 32);
        if (!result.X.IsEven)
        {
            encoded[31] |= 0x80;
        }

        return encoded;
    }

    // Twisted Edwards addition with a = -1.
    private static (BigInteger, BigInteger) Add((BigInteger X, BigInteger Y) p, (BigInteger X, BigInteger Y) q)
    {
        var t = Mod(D * p.X * q.X * p.Y * q.Y);
        var x = Mod((p.X * q.Y + p.Y * q.X) * Inverse(Mod(1 + t)));
        var y = Mod((p.Y * q.Y + p.X * q.X) * Inverse(Mod(1 - t)));
        return (x, y);
    }

    private static BigInteger Mod(BigInteger n) => ((n % P) + P) % P;

    private static BigInteger Inverse(BigInteger n) => BigInteger.ModPow(Mod(n), P - 2, P);
}

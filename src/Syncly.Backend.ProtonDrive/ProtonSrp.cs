using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// Proton's SRP-6a (public-link flavour). The username is empty; the URL password is the secret.
/// </summary>
internal static class ProtonSrp
{
    internal readonly record struct Proof(string ClientProof, string ClientEphemeral, byte[] ExpectedServer);

    public static Proof Prove(string password, ProtonInfoResponse info)
    {
        var n = ParseModulus(info.Modulus);
        var g = new BigInteger(2);
        var B = FromUnsigned(Convert.FromBase64String(info.ServerEphemeral));
        if (B <= 0 || B >= n)
            throw new ProtonDriveException("Proton Drive sent an invalid SRP challenge.");

        var salt = Convert.FromBase64String(info.UrlPasswordSalt);
        var x = FromUnsigned(ComputeX(password, salt, info.Version));
        var a = RandomScalar(n);
        var A = BigInteger.ModPow(g, a, n);
        var u = FromUnsigned(HashConcat(Pad(A, n), Pad(B, n)));
        var k = FromUnsigned(HashConcat(Pad(n, n), Pad(g, n)));

        var gx = BigInteger.ModPow(g, x, n);
        var baseS = SubMod(B, k * gx % n, n);
        var S = BigInteger.ModPow(baseS, a + u * x, n);
        var K = HashConcat(Pad(S, n));

        var clientProof = HashConcat(
            Xor(HashConcat(Pad(n, n)), HashConcat(Pad(g, n))),
            HashConcat([]),
            salt,
            Pad(A, n),
            Pad(B, n),
            K);

        var serverProof = HashConcat(Pad(A, n), clientProof, K);

        return new Proof(
            Convert.ToBase64String(clientProof),
            Convert.ToBase64String(ToUnsigned(A)),
            serverProof);
    }

    public static bool VerifyServer(Proof proof, string serverProofB64)
    {
        if (string.IsNullOrWhiteSpace(serverProofB64))
            return false;

        var actual = Convert.FromBase64String(serverProofB64);
        return CryptographicOperations.FixedTimeEquals(proof.ExpectedServer, actual);
    }

    internal static BigInteger ParseModulus(string armored)
    {
        var hex = ExtractSignedBody(armored);
        if (hex.Length == 0)
            throw new ProtonDriveException("Proton Drive modulus is missing.");

        hex = hex.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        return FromUnsigned(Convert.FromHexString(hex));
    }

    internal static string ExtractSignedBody(string armored)
    {
        var lines = armored.Replace("\r\n", "\n").Split('\n');
        var body = new StringBuilder();
        var started = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                started = false;
                continue;
            }

            if (line.StartsWith("-----END", StringComparison.Ordinal))
                break;

            if (line.StartsWith("Hash:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!started)
            {
                if (line.Length == 0)
                    started = true;
                continue;
            }

            body.Append(line.Trim());
        }

        return body.ToString();
    }

    private static byte[] ComputeX(string password, byte[] salt, byte version)
    {
        _ = version;
        var inner = HashConcat(Encoding.UTF8.GetBytes(password));
        var data = new byte[salt.Length + inner.Length];
        salt.CopyTo(data, 0);
        inner.CopyTo(data, salt.Length);
        return HashConcat(data);
    }

    private static byte[] HashConcat(params byte[][] parts)
    {
        using var sha = SHA512.Create();
        foreach (var part in parts)
            sha.TransformBlock(part, 0, part.Length, null, 0);

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }

    private static BigInteger RandomScalar(BigInteger n)
    {
        var bytes = new byte[n.GetByteCount(isUnsigned: true)];
        BigInteger a;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            a = FromUnsigned(bytes);
        } while (a <= 1 || a >= n - 1);

        return a;
    }

    private static byte[] Pad(BigInteger value, BigInteger n)
    {
        var size = n.GetByteCount(isUnsigned: true);
        var raw = ToUnsigned(value);
        if (raw.Length == size)
            return raw;

        var padded = new byte[size];
        raw.CopyTo(padded, size - raw.Length);
        return padded;
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var result = new byte[n];
        for (var i = 0; i < n; i++)
            result[i] = (byte)(a[i] ^ b[i]);
        return result;
    }

    private static BigInteger SubMod(BigInteger a, BigInteger b, BigInteger n)
    {
        var r = (a - b) % n;
        return r < 0 ? r + n : r;
    }

    private static BigInteger FromUnsigned(byte[] bytes)
    {
        if (bytes.Length == 0)
            return BigInteger.Zero;

        var copy = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, copy, 1, bytes.Length);
        Array.Reverse(copy);
        return new BigInteger(copy);
    }

    private static byte[] ToUnsigned(BigInteger value)
    {
        var bytes = value.ToByteArray();
        Array.Reverse(bytes);
        var start = 0;
        while (start < bytes.Length - 1 && bytes[start] == 0)
            start++;

        return bytes[start..];
    }
}

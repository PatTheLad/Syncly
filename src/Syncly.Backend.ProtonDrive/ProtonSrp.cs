using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// Proton SRP-6a as implemented by <c>ProtonMail/go-srp</c>: little-endian 2048-bit
/// group, bcrypt + expandHash for the password, expandHash for proofs.
/// </summary>
internal static class ProtonSrp
{
    internal const int BitLength = 2048;
    internal const int ByteLength = BitLength / 8;

    internal readonly record struct Proof(string ClientProof, string ClientEphemeral, byte[] ExpectedServer);

    public static Proof Prove(string password, ProtonInfoResponse info)
    {
        var version = info.Version == 0 ? (byte)4 : info.Version;
        if (version < 3)
            throw new ProtonDriveException($"Unsupported Proton Drive SRP version {info.Version}.");

        byte[] salt;
        byte[] serverEphemeral;
        try
        {
            salt = Convert.FromBase64String(info.UrlPasswordSalt);
            serverEphemeral = Convert.FromBase64String(info.ServerEphemeral);
        }
        catch (FormatException ex)
        {
            throw new ProtonDriveException("Proton Drive sent an unreadable SRP challenge.", ex);
        }

        return GenerateProofs(Encoding.UTF8.GetBytes(password), salt, info.Modulus, serverEphemeral);
    }

    public static bool VerifyServer(Proof proof, string serverProofB64)
    {
        if (string.IsNullOrWhiteSpace(serverProofB64))
            return false;

        try
        {
            var actual = Convert.FromBase64String(serverProofB64);
            return CryptographicOperations.FixedTimeEquals(proof.ExpectedServer, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static Proof GenerateProofs(
        byte[] password,
        byte[] salt,
        string signedModulus,
        byte[] serverEphemeral)
    {
        var modulus = ParseModulus(signedModulus);
        var hashed = HashPasswordV3(password, salt, modulus);
        var n = FromLe(modulus);
        var b = FromLe(serverEphemeral);
        CheckParams(n, b);

        var nMinus1 = n - 1;
        var x = FromLe(hashed);
        var g = new BigInteger(2);
        var gBytes = ToLeFixed(g);
        var nBytes = ToLeFixed(n);

        var kInput = new byte[ByteLength * 2];
        gBytes.CopyTo(kInput, 0);
        nBytes.CopyTo(kInput, ByteLength);
        var k = FromLe(ExpandHash(kInput)) % n;
        if (k <= 1 || k >= nMinus1)
            throw new ProtonDriveException("Proton Drive SRP multiplier is out of bounds.");

        BigInteger secret;
        byte[] aBytes;
        BigInteger scrambling;
        GenerateEphemeral(n, nMinus1, serverEphemeral, out secret, out aBytes, out scrambling);

        var gx = BigInteger.ModPow(g, x, n);
        var kgx = k * gx % n;
        var baseS = (b - kgx) % n;
        if (baseS < 0)
            baseS += n;

        var exponent = (scrambling * x + secret) % nMinus1;
        var shared = BigInteger.ModPow(baseS, exponent, n);
        var sharedBytes = ToLeFixed(shared);

        var clientProofInput = Concat(aBytes, serverEphemeral, sharedBytes);
        var clientProof = ExpandHash(clientProofInput);
        var serverProof = ExpandHash(Concat(aBytes, clientProof, sharedBytes));

        return new Proof(
            Convert.ToBase64String(clientProof),
            Convert.ToBase64String(aBytes),
            serverProof);
    }

    internal static byte[] ParseModulus(string armored)
    {
        var body = ExtractSignedBody(armored).Trim();
        if (body.Length == 0)
            throw new ProtonDriveException("Proton Drive modulus is missing.");

        try
        {
            return Convert.FromBase64String(body);
        }
        catch (FormatException ex)
        {
            throw new ProtonDriveException(
                "Proton Drive sent an SRP modulus Syncly could not read.", ex);
        }
    }

    internal static string ExtractSignedBody(string armored)
    {
        var lines = armored.Replace("\r\n", "\n").Split('\n');
        var body = new StringBuilder();
        var started = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("-----BEGIN PGP SIGNATURE", StringComparison.Ordinal))
                break;

            if (line.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                started = false;
                continue;
            }

            if (line.StartsWith("-----END", StringComparison.Ordinal))
                break;

            if (line.StartsWith("Hash:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!started)
            {
                if (line.Length == 0)
                    started = true;
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
                line = line[2..];

            body.Append(line.Trim());
        }

        return body.ToString();
    }

    internal static byte[] ExpandHash(ReadOnlySpan<byte> data)
    {
        var output = new byte[256];
        for (byte i = 0; i < 4; i++)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            sha.AppendData(data);
            sha.AppendData([i]);
            if (!sha.TryGetHashAndReset(output.AsSpan(i * 64, 64), out _))
                throw new ProtonDriveException("SRP hash failed.");
        }

        return output;
    }

    internal static byte[] HashPasswordV3(byte[] password, byte[] salt, byte[] modulus)
    {
        var crypted = OpenBsdBCrypt.Generate("2y", Encoding.Latin1.GetChars(password), BcryptSalt16(salt), 10);
        var input = new byte[crypted.Length + modulus.Length];
        Encoding.ASCII.GetBytes(crypted, input.AsSpan(0, crypted.Length));
        modulus.CopyTo(input, crypted.Length);
        return ExpandHash(input);
    }

    /// <summary>
    /// Proton <c>computeKeyPassword</c> / <c>derive_key_passphrase</c>: bcrypt the
    /// password with a 16-byte salt, then keep the trailing 31-character hash
    /// (drop <c>$2y$10$</c> and the encoded salt). That 31-byte string is the OpenPGP passphrase.
    /// </summary>
    internal static byte[] DeriveKeyPassphrase(byte[] password, byte[] salt)
    {
        byte[] salt16;
        if (salt.Length == 16)
            salt16 = salt;
        else if (salt.Length == 10)
            salt16 = BcryptSalt16(salt);
        else
            throw new ProtonDriveException(
                $"Proton Drive share salt is {salt.Length} bytes; expected 16.");

        var full = OpenBsdBCrypt.Generate("2y", Encoding.Latin1.GetChars(password), salt16, 10);
        if (full.Length < 60)
            throw new ProtonDriveException("Proton Drive bcrypt hash was truncated.");

        return Encoding.ASCII.GetBytes(full[29..]);
    }

    internal static byte[] BcryptSalt16(byte[] salt)
    {
        var salt16 = new byte[16];
        if (salt.Length >= 16)
        {
            Buffer.BlockCopy(salt, 0, salt16, 0, 16);
            return salt16;
        }

        Buffer.BlockCopy(salt, 0, salt16, 0, salt.Length);
        var suffix = "proton"u8;
        suffix[..Math.Min(suffix.Length, 16 - salt.Length)].CopyTo(salt16.AsSpan(salt.Length));
        return salt16;
    }

    private static void CheckParams(BigInteger n, BigInteger b)
    {
        if (n.GetBitLength() != BitLength)
            throw new ProtonDriveException("Proton Drive SRP modulus has the wrong size.");

        if (n % 8 != 3)
            throw new ProtonDriveException("Proton Drive SRP modulus is not a valid group.");

        var nMinus1 = n - 1;
        if (b <= 1 || b >= nMinus1)
            throw new ProtonDriveException("Proton Drive sent an invalid SRP challenge.");
    }

    private static void GenerateEphemeral(
        BigInteger n,
        BigInteger nMinus1,
        byte[] serverEphemeral,
        out BigInteger secret,
        out byte[] aBytes,
        out BigInteger scrambling)
    {
        var g = new BigInteger(2);
        var lower = new BigInteger(BitLength * 2);

        while (true)
        {
            var buf = new byte[ByteLength];
            RandomNumberGenerator.Fill(buf);
            secret = FromLe(buf) % nMinus1;
            if (secret <= lower || secret >= nMinus1)
                continue;

            var a = BigInteger.ModPow(g, secret, n);
            aBytes = ToLeFixed(a);
            scrambling = FromLe(ExpandHash(Concat(aBytes, serverEphemeral)));
            if (scrambling.IsZero)
                continue;

            return;
        }
    }

    private static BigInteger FromLe(byte[] bytes) =>
        new(bytes, isUnsigned: true, isBigEndian: false);

    private static byte[] ToLeFixed(BigInteger value)
    {
        var buf = new byte[ByteLength];
        if (!value.TryWriteBytes(buf, out _, isUnsigned: true, isBigEndian: false))
            throw new ProtonDriveException("SRP value exceeds the Proton Drive group size.");

        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var length = 0;
        foreach (var part in parts)
            length += part.Length;

        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}

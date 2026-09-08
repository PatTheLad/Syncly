using System.Numerics;
using Syncly.Backend.ProtonDrive;

namespace Syncly.Sync.Tests;

public class ProtonSrpTests
{
    // Vectors from ProtonMail/go-srp / proton-sdk srp.rs.
    private const string TestModulusB64 =
        "W2z5HBi8RvsfYzZTS7qBaUxxPhsfHJFZpu3Kd6s1JafNrCCH9rfvPLrfuqocxWPgWDH2R8neK7PkNvjxto9TStuY5z7jAzWRvFWN9cQhAKkdWgy0JY6ywVn22+HFpF4cYesHrqFIKUPDMSSIlWjBVmEJZ/MusD44ZT29xcPrOqeZvwtCffKtGAIjLYPZIEbZKnDM1Dm3q2K/xS5h+xdhjnndhsrkwm9U9oyA2wxzSXFL+pdfj2fOdRwuR5nW0J2NFrq3kJjkRmpO/Genq1UW+TEknIWAb6VzJJJA244K/H8cnSx2+nSNZO3bbo6Ys228ruV9A8m6DhxmS+bihN3ttQ==";

    private const string TestModulusClearsign =
        "-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n" + TestModulusB64 +
        "\n-----BEGIN PGP SIGNATURE-----\nVersion: ProtonMail\nComment: https://protonmail.com\n\nwl4EARYIABAFAlwB1j0JEDUFhcTpUY8mAAD8CgEAnsFnF4cF0uSHKkXa1GIa\nGO86yMV4zDZEZcDSJo0fgr8A/AlupGN9EdHlsrZLmTA1vhIx+rOgxdEff28N\nkvNM7qIK\n=q6vu\n-----END PGP SIGNATURE-----";

    private const string TestSaltB64 = "yKlc5/CvObfoiw==";
    private static readonly byte[] TestPassword = "abc123"u8.ToArray();

    [Fact]
    public void Signed_modulus_is_base64_not_hex()
    {
        var body = ProtonSrp.ExtractSignedBody(TestModulusClearsign);
        Assert.Equal(TestModulusB64, body);
        Assert.DoesNotContain("wl4EARYI", body, StringComparison.Ordinal);

        var modulus = ProtonSrp.ParseModulus(TestModulusClearsign);
        Assert.Equal(Convert.FromBase64String(TestModulusB64), modulus);
        Assert.Equal(ProtonSrp.ByteLength, modulus.Length);
    }

    [Fact]
    public void ExpandHash_is_256_bytes()
    {
        Assert.Equal(256, ProtonSrp.ExpandHash("abc"u8.ToArray()).Length);
    }

    [Fact]
    public void GenerateProofs_runs_against_the_signed_modulus()
    {
        var salt = Convert.FromBase64String(TestSaltB64);
        var modulus = ProtonSrp.ParseModulus(TestModulusClearsign);
        var hashed = ProtonSrp.HashPasswordV3(TestPassword, salt, modulus);
        var n = new BigInteger(modulus, isUnsigned: true, isBigEndian: false);
        var x = new BigInteger(hashed, isUnsigned: true, isBigEndian: false);
        var g = new BigInteger(2);
        var gBytes = ToLe(g);
        var nBytes = ToLe(n);
        var kInput = new byte[ProtonSrp.ByteLength * 2];
        gBytes.CopyTo(kInput, 0);
        nBytes.CopyTo(kInput, ProtonSrp.ByteLength);
        var k = new BigInteger(ProtonSrp.ExpandHash(kInput), isUnsigned: true, isBigEndian: false) % n;
        var v = BigInteger.ModPow(g, x, n);
        var b = (k * v + BigInteger.ModPow(g, 7, n)) % n;
        var serverEphemeral = ToLe(b);

        var proofs = ProtonSrp.GenerateProofs(TestPassword, salt, TestModulusClearsign, serverEphemeral);
        Assert.Equal(ProtonSrp.ByteLength, Convert.FromBase64String(proofs.ClientEphemeral).Length);
        Assert.Equal(256, Convert.FromBase64String(proofs.ClientProof).Length);
        Assert.Equal(256, proofs.ExpectedServer.Length);
    }

    private static byte[] ToLe(BigInteger value)
    {
        var buf = new byte[ProtonSrp.ByteLength];
        Assert.True(value.TryWriteBytes(buf, out _, isUnsigned: true, isBigEndian: false));
        return buf;
    }
}

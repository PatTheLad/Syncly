using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Syncly.Backend.ProtonDrive;

namespace Syncly.Sync.Tests;

public class ProtonPgpTests
{
    [Fact]
    public void Unlock_uses_the_encryption_subkey_not_the_signing_master()
    {
        var passphrase = "share-pass"u8.ToArray();
        var armored = ArmoredSigningPlusEncryptionKey(passphrase);
        var keys = ProtonPgp.UnlockPrivateKey(armored, passphrase);

        Assert.True(keys.EncryptionPublic.IsEncryptionKey);
        Assert.True(keys.PrivateKeys.Count >= 2);

        var cipher = ProtonPgp.EncryptToKey("hello"u8.ToArray(), keys.EncryptionPublic);
        var plain = ProtonPgp.DecryptWithPrivateKey(cipher, keys);
        Assert.Equal("hello"u8.ToArray(), plain);
    }

    private static string ArmoredSigningPlusEncryptionKey(byte[] passphrase)
    {
        var random = new SecureRandom();
        var rsa = new RsaKeyPairGenerator();
        rsa.Init(new KeyGenerationParameters(random, 2048));

        var signPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaSign, rsa.GenerateKeyPair(), DateTime.UtcNow);
        var encPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaEncrypt, rsa.GenerateKeyPair(), DateTime.UtcNow);

        var generator = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification,
            signPair,
            "syncly-share",
            SymmetricKeyAlgorithmTag.Aes256,
            Encoding.Latin1.GetChars(passphrase),
            true,
            null,
            null,
            random);
        generator.AddSubKey(encPair);

        using var buffer = new MemoryStream();
        using (var armored = new ArmoredOutputStream(buffer))
            generator.GenerateSecretKeyRing().Encode(armored);

        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}

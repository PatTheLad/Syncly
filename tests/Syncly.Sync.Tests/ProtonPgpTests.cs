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
        Assert.True(keys.SigningPublic.IsMasterKey);
    }

    [Fact]
    public void File_draft_includes_every_attribute_Proton_requires()
    {
        var passphrase = "parent-pass"u8.ToArray();
        var parent = ProtonPgp.UnlockPrivateKey(ArmoredSigningPlusEncryptionKey(passphrase), passphrase);
        var hashKey = "folder-hash-key-bytes-are-utf8"u8.ToArray();
        var draft = ProtonPgp.CreateFileDraft("syncly-ops.bin", "parent-link", parent, hashKey);

        foreach (var key in new[]
                 {
                     "Name", "Hash", "ParentLinkID", "NodePassphrase", "NodePassphraseSignature",
                     "NodeKey", "MIMEType", "ContentKeyPacket", "ContentKeyPacketSignature",
                 })
        {
            Assert.True(draft.Body.ContainsKey(key), key);
            Assert.False(string.IsNullOrWhiteSpace(draft.Body[key] as string), key);
        }

        Assert.Equal("parent-link", draft.Body["ParentLinkID"]);
        Assert.Equal(ProtonPgp.LookupHash("syncly-ops.bin", hashKey), draft.Body["Hash"]);

        var name = Encoding.UTF8.GetString(
            ProtonPgp.DecryptWithPrivateKey((string)draft.Body["Name"]!, parent));
        Assert.Equal("syncly-ops.bin", name);

        var unlocked = ProtonPgp.DecryptWithPrivateKey((string)draft.Body["NodePassphrase"]!, parent);
        var fileKeys = ProtonPgp.UnlockPrivateKey((string)draft.Body["NodeKey"]!, unlocked);
        var session = ProtonPgp.DecryptSessionKey(
            Convert.FromBase64String((string)draft.Body["ContentKeyPacket"]!),
            fileKeys.EncryptionPrivate);
        Assert.Equal(draft.SessionKey, session);
        Assert.Equal(PublicKeyAlgorithmTag.RsaSign, fileKeys.SigningPublic.Algorithm);
        Assert.Equal(PublicKeyAlgorithmTag.RsaEncrypt, fileKeys.EncryptionPublic.Algorithm);
        Assert.True(fileKeys.EncryptionPublic.IsEncryptionKey);
        Assert.True(Convert.FromBase64String((string)draft.Body["ContentKeyPacket"]!).Length > 200);
    }

    [Fact]
    public void Session_key_roundtrips_a_file_block()
    {
        var key = ProtonPgp.GenerateSessionKey();
        var plain = "syncly mailbox blob"u8.ToArray();
        var cipher = ProtonPgp.EncryptWithSessionKey(plain, key);
        Assert.Equal(plain, ProtonPgp.DecryptWithSessionKey(cipher, key));
    }

    [Fact]
    public void Verification_token_xors_the_block_prefix()
    {
        var code = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var block = Enumerable.Range(100, 40).Select(i => (byte)i).ToArray();
        var token = ProtonPgp.VerificationToken(code, block);
        Assert.Equal(32, token.Length);
        Assert.Equal((byte)(0 ^ 100), token[0]);
        Assert.Equal((byte)(31 ^ 131), token[31]);
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

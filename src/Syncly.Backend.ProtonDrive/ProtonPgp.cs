using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.IO;

namespace Syncly.Backend.ProtonDrive;

/// <summary>The slice of OpenPGP Proton Drive uses to wrap share keys and file contents.</summary>
internal static class ProtonPgp
{
    public static byte[] DecryptWithPassword(string armored, string password) =>
        DecryptWithPassword(armored, Encoding.UTF8.GetBytes(password));

    public static byte[] DecryptWithPassword(string armored, byte[] password)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.UTF8.GetBytes(armored)));
        var factory = new PgpObjectFactory(input);
        var encrypted = RequireEncrypted(factory);
        PgpPbeEncryptedData? pbe = null;

        foreach (PgpEncryptedData candidate in encrypted.GetEncryptedDataObjects())
        {
            if (candidate is PgpPbeEncryptedData found)
            {
                pbe = found;
                break;
            }
        }

        if (pbe is null)
            throw new ProtonDriveException("Proton Drive share passphrase is not password-encrypted.");

        using var clear = pbe.GetDataStream(ToChars(password));
        return ReadLiteral(new PgpObjectFactory(clear));
    }

    public static ProtonKeySet UnlockPrivateKey(string armoredKey, byte[] passphrase)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.UTF8.GetBytes(armoredKey)));
        var bundle = new PgpSecretKeyRingBundle(input);
        var unlocked = new List<(PgpPrivateKey Private, PgpPublicKey Public)>();

        foreach (PgpSecretKeyRing ring in bundle.GetKeyRings())
        {
            foreach (PgpSecretKey secret in ring.GetSecretKeys())
            {
                try
                {
                    var key = secret.ExtractPrivateKey(ToChars(passphrase));
                    if (key is not null)
                        unlocked.Add((key, secret.PublicKey));
                }
                catch (PgpException)
                {
                    // Wrong subkey or passphrase; try the next one.
                }
            }
        }

        if (unlocked.Count == 0)
            throw new ProtonDriveException("Could not unlock the Proton Drive share key.");

        var encryption = unlocked.FirstOrDefault(k => k.Public.IsEncryptionKey);
        if (encryption.Private is null)
            throw new ProtonDriveException("The Proton Drive share key has no encryption subkey.");

        return new ProtonKeySet(encryption.Private, encryption.Public, unlocked.Select(k => k.Private).ToList());
    }

    public static byte[] DecryptWithPrivateKey(string armored, ProtonKeySet keys) =>
        DecryptWithPrivateKey(armored, keys.PrivateKeys);

    public static byte[] DecryptWithPrivateKey(string armored, PgpPrivateKey key) =>
        DecryptWithPrivateKey(armored, [key]);

    public static byte[] DecryptWithPrivateKey(string armored, IReadOnlyList<PgpPrivateKey> keys)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.UTF8.GetBytes(armored)));
        var factory = new PgpObjectFactory(input);
        var encrypted = RequireEncrypted(factory);
        PgpPublicKeyEncryptedData? pk = null;

        foreach (PgpEncryptedData candidate in encrypted.GetEncryptedDataObjects())
        {
            if (candidate is not PgpPublicKeyEncryptedData found)
                continue;

            if (keys.Any(k => k.KeyId == found.KeyId))
            {
                pk = found;
                break;
            }

            pk ??= found;
        }

        if (pk is null)
            throw new ProtonDriveException("Proton Drive message is not encrypted to the share key.");

        var match = keys.FirstOrDefault(k => k.KeyId == pk.KeyId) ?? keys[0];
        using var clear = pk.GetDataStream(match);
        return ReadLiteral(new PgpObjectFactory(clear));
    }

    public static string EncryptToKey(byte[] plaintext, PgpPublicKey publicKey)
    {
        if (!publicKey.IsEncryptionKey)
            throw new ProtonDriveException("The Proton Drive share key cannot encrypt.");

        var outStream = new MemoryStream();
        using (var armored = new ArmoredOutputStream(outStream))
        {
            var encGen = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Aes256, true, new SecureRandom());
            encGen.AddMethod(publicKey);
            using var enc = encGen.Open(armored, new byte[4096]);
            var literal = new PgpLiteralDataGenerator();
            using var lit = literal.Open(enc, PgpLiteralData.Binary, "syncly", plaintext.Length, DateTime.UtcNow);
            lit.Write(plaintext);
        }

        return Encoding.ASCII.GetString(outStream.ToArray());
    }

    public static string EncryptWithPassword(byte[] plaintext, byte[] password)
    {
        var outStream = new MemoryStream();
        using (var armored = new ArmoredOutputStream(outStream))
        {
            var encGen = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Aes256, true, new SecureRandom());
            encGen.AddMethod(ToChars(password), HashAlgorithmTag.Sha256);
            using var enc = encGen.Open(armored, new byte[4096]);
            var literal = new PgpLiteralDataGenerator();
            using var lit = literal.Open(enc, PgpLiteralData.Binary, "syncly", plaintext.Length, DateTime.UtcNow);
            lit.Write(plaintext);
        }

        return Encoding.ASCII.GetString(outStream.ToArray());
    }

    private static PgpEncryptedDataList RequireEncrypted(PgpObjectFactory factory)
    {
        PgpObject? obj;
        while ((obj = factory.NextPgpObject()) is not null)
        {
            if (obj is PgpMarker or PgpOnePassSignatureList or PgpSignatureList)
                continue;

            if (obj is PgpEncryptedDataList list)
                return list;

            if (obj is PgpCompressedData compressed)
                return RequireEncrypted(new PgpObjectFactory(compressed.GetDataStream()));
        }

        throw new ProtonDriveException("Expected an encrypted Proton Drive message.");
    }

    private static byte[] ReadLiteral(PgpObjectFactory factory)
    {
        PgpObject? obj;
        while ((obj = factory.NextPgpObject()) is not null)
        {
            if (obj is PgpMarker or PgpOnePassSignatureList or PgpSignatureList)
                continue;

            if (obj is PgpCompressedData compressed)
                return ReadLiteral(new PgpObjectFactory(compressed.GetDataStream()));

            if (obj is PgpLiteralData literal)
            {
                using var stream = literal.GetInputStream();
                return Streams.ReadAll(stream);
            }
        }

        throw new ProtonDriveException("Proton Drive payload was not literal data.");
    }

    private static char[] ToChars(byte[] password)
    {
        var chars = new char[password.Length];
        for (var i = 0; i < password.Length; i++)
            chars[i] = (char)password[i];
        return chars;
    }
}

internal sealed class ProtonKeySet(
    PgpPrivateKey encryptionPrivate,
    PgpPublicKey encryptionPublic,
    IReadOnlyList<PgpPrivateKey> privateKeys)
{
    public PgpPrivateKey EncryptionPrivate { get; } = encryptionPrivate;

    public PgpPublicKey EncryptionPublic { get; } = encryptionPublic;

    public IReadOnlyList<PgpPrivateKey> PrivateKeys { get; } = privateKeys;
}

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
        var encrypted = RequireEncrypted(factory.NextPgpObject());
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
        return ReadLiteral(new PgpObjectFactory(clear).NextPgpObject());
    }

    public static (PgpPrivateKey Private, PgpPublicKey Public) UnlockPrivateKey(string armoredKey, byte[] passphrase)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.UTF8.GetBytes(armoredKey)));
        var bundle = new PgpSecretKeyRingBundle(input);

        foreach (PgpSecretKeyRing ring in bundle.GetKeyRings())
        {
            foreach (PgpSecretKey secret in ring.GetSecretKeys())
            {
                try
                {
                    var key = secret.ExtractPrivateKey(ToChars(passphrase));
                    if (key is not null)
                        return (key, secret.PublicKey);
                }
                catch (PgpException)
                {
                    // Wrong subkey or passphrase; try the next one.
                }
            }
        }

        throw new ProtonDriveException("Could not unlock the Proton Drive share key.");
    }

    public static byte[] DecryptWithPrivateKey(string armored, PgpPrivateKey key)
    {
        using var input = PgpUtilities.GetDecoderStream(new MemoryStream(Encoding.UTF8.GetBytes(armored)));
        var factory = new PgpObjectFactory(input);
        var encrypted = RequireEncrypted(factory.NextPgpObject());
        PgpPublicKeyEncryptedData? pk = null;

        foreach (PgpEncryptedData candidate in encrypted.GetEncryptedDataObjects())
        {
            if (candidate is PgpPublicKeyEncryptedData found && found.KeyId == key.KeyId)
            {
                pk = found;
                break;
            }

            pk ??= candidate as PgpPublicKeyEncryptedData;
        }

        if (pk is null)
            throw new ProtonDriveException("Proton Drive message is not encrypted to the share key.");

        using var clear = pk.GetDataStream(key);
        return ReadLiteral(new PgpObjectFactory(clear).NextPgpObject());
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

    public static string EncryptToKey(byte[] plaintext, PgpPublicKey publicKey)
    {
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

    private static PgpEncryptedDataList RequireEncrypted(PgpObject obj)
    {
        if (obj is PgpEncryptedDataList list)
            return list;

        if (obj is PgpCompressedData compressed)
            return RequireEncrypted(new PgpObjectFactory(compressed.GetDataStream()).NextPgpObject());

        throw new ProtonDriveException("Expected an encrypted Proton Drive message.");
    }

    private static byte[] ReadLiteral(PgpObject obj)
    {
        if (obj is PgpCompressedData compressed)
            obj = new PgpObjectFactory(compressed.GetDataStream()).NextPgpObject();

        if (obj is PgpLiteralData literal)
        {
            using var stream = literal.GetInputStream();
            return Streams.ReadAll(stream);
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

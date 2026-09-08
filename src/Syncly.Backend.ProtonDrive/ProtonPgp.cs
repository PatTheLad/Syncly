using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
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
        var unlocked = new List<(PgpPrivateKey Private, PgpPublicKey Public, bool CanSign)>();

        foreach (PgpSecretKeyRing ring in bundle.GetKeyRings())
        {
            foreach (PgpSecretKey secret in ring.GetSecretKeys())
            {
                try
                {
                    var key = secret.ExtractPrivateKey(ToChars(passphrase));
                    if (key is not null)
                        unlocked.Add((key, secret.PublicKey, secret.IsSigningKey));
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

        var signing = unlocked.FirstOrDefault(k => k.CanSign);
        if (signing.Private is null)
            signing = unlocked.FirstOrDefault(k => k.Public.IsMasterKey);
        if (signing.Private is null)
            signing = encryption;

        return new ProtonKeySet(
            encryption.Private,
            encryption.Public,
            signing.Private,
            signing.Public,
            unlocked.Select(k => k.Private).ToList());
    }

    public static string GeneratePassphrase()
    {
        var bytes = new byte[32];
        new SecureRandom().NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static (string Armored, ProtonKeySet Keys) GenerateNodeKey(byte[] passphrase)
    {
        var random = new SecureRandom();
        var rsa = new RsaKeyPairGenerator();
        rsa.Init(new KeyGenerationParameters(random, 2048));

        var signPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaSign, rsa.GenerateKeyPair(), DateTime.UtcNow);
        var encPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaEncrypt, rsa.GenerateKeyPair(), DateTime.UtcNow);
        var generator = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification,
            signPair,
            "Drive key",
            SymmetricKeyAlgorithmTag.Aes256,
            ToChars(passphrase),
            true,
            null,
            null,
            random);
        generator.AddSubKey(encPair);

        using var buffer = new MemoryStream();
        using (var armored = new ArmoredOutputStream(buffer))
            generator.GenerateSecretKeyRing().Encode(armored);

        var text = Encoding.ASCII.GetString(buffer.ToArray());
        return (text, UnlockPrivateKey(text, passphrase));
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
            using var lit = literal.Open(enc, PgpLiteralData.Binary, "", plaintext.Length, DateTime.UtcNow);
            lit.Write(plaintext);
        }

        return Encoding.ASCII.GetString(outStream.ToArray());
    }

    public static string EncryptAndSign(byte[] plaintext, PgpPublicKey recipient, ProtonKeySet signer)
    {
        if (!recipient.IsEncryptionKey)
            throw new ProtonDriveException("The Proton Drive folder key cannot encrypt.");

        var outStream = new MemoryStream();
        using (var armored = new ArmoredOutputStream(outStream))
        {
            var encGen = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Aes256, true, new SecureRandom());
            encGen.AddMethod(recipient);
            using var enc = encGen.Open(armored, new byte[4096]);

            var sigGen = new PgpSignatureGenerator(signer.SigningPublic.Algorithm, HashAlgorithmTag.Sha256);
            sigGen.InitSign(PgpSignature.BinaryDocument, signer.SigningPrivate);
            sigGen.GenerateOnePassVersion(false).Encode(enc);

            var literal = new PgpLiteralDataGenerator();
            using (var lit = literal.Open(enc, PgpLiteralData.Binary, "", DateTime.UtcNow, new byte[4096]))
            {
                lit.Write(plaintext);
                sigGen.Update(plaintext);
            }

            sigGen.Generate().Encode(enc);
        }

        return Encoding.ASCII.GetString(outStream.ToArray());
    }

    public static string SignDetachedArmored(byte[] data, ProtonKeySet keys)
    {
        var sigGen = new PgpSignatureGenerator(keys.SigningPublic.Algorithm, HashAlgorithmTag.Sha256);
        sigGen.InitSign(PgpSignature.BinaryDocument, keys.SigningPrivate);
        sigGen.Update(data);

        var outStream = new MemoryStream();
        using (var armored = new ArmoredOutputStream(outStream))
            sigGen.Generate().Encode(armored);

        return Encoding.ASCII.GetString(outStream.ToArray());
    }

    public static byte[] SignDetached(byte[] data, ProtonKeySet keys)
    {
        var sigGen = new PgpSignatureGenerator(keys.SigningPublic.Algorithm, HashAlgorithmTag.Sha256);
        sigGen.InitSign(PgpSignature.BinaryDocument, keys.SigningPrivate);
        sigGen.Update(data);

        using var buffer = new MemoryStream();
        sigGen.Generate().Encode(buffer);
        return buffer.ToArray();
    }

    public static string LookupHash(string name, ReadOnlySpan<byte> hashKey) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(hashKey, Encoding.UTF8.GetBytes(name)));

    public static byte[] GenerateSessionKey()
    {
        var key = new byte[32];
        new SecureRandom().NextBytes(key);
        return key;
    }

    public static byte[] EncryptSessionKey(byte[] sessionKey, PgpPublicKey publicKey)
    {
        if (publicKey.Algorithm is not (PublicKeyAlgorithmTag.RsaEncrypt or PublicKeyAlgorithmTag.RsaGeneral))
            throw new ProtonDriveException("The Proton Drive file key cannot wrap a content key.");

        var sessionInfo = CreateSessionInfo(sessionKey);
        var cipher = CipherUtilities.GetCipher("RSA//PKCS1Padding");
        cipher.Init(true, new ParametersWithRandom(publicKey.GetKey(), new SecureRandom()));
        var encrypted = cipher.DoFinal(sessionInfo);
        var mpi = new MPInteger(new BigInteger(1, encrypted)).GetEncoded();

        using var output = new MemoryStream();
        using (var pOut = new BcpgOutputStream(output))
            new PublicKeyEncSessionPacket(publicKey.KeyId, publicKey.Algorithm, [mpi]).Encode(pOut);

        return output.ToArray();
    }

    public static byte[] DecryptSessionKey(byte[] keyPacket, PgpPrivateKey privateKey)
    {
        var body = ReadPacketBody(keyPacket, PacketTag.PublicKeyEncryptedSession);
        // version (1) + key id (8) + algorithm (1) + MPI
        if (body.Length < 12)
            throw new ProtonDriveException("Proton Drive content key packet is not a session key.");

        var mpi = body.AsSpan(10).ToArray();
        var cipher = CipherUtilities.GetCipher("RSA//PKCS1Padding");
        cipher.Init(false, privateKey.Key);
        cipher.ProcessBytes(mpi, 2, mpi.Length - 2);
        var sessionInfo = cipher.DoFinal();
        if (sessionInfo.Length < 4 || sessionInfo[0] != (byte)SymmetricKeyAlgorithmTag.Aes256)
            throw new ProtonDriveException("Proton Drive content key is not AES-256.");

        return sessionInfo.AsSpan(1, sessionInfo.Length - 3).ToArray();
    }

    public static byte[] EncryptWithSessionKey(byte[] plaintext, byte[] sessionKey)
    {
        using var inner = new MemoryStream();
        var literal = new PgpLiteralDataGenerator();
        using (var lit = literal.Open(inner, PgpLiteralData.Binary, "", DateTime.UtcNow, new byte[1 << 16]))
            lit.Write(plaintext);

        var innerBytes = inner.ToArray();
        var random = new SecureRandom();
        var key = new KeyParameter(sessionKey);
        var cipher = CipherUtilities.GetCipher("AES/CFB/NoPadding");
        var iv = new byte[cipher.GetBlockSize()];
        cipher.Init(true, new ParametersWithIV(key, iv));

        var inlineIv = new byte[cipher.GetBlockSize() + 2];
        random.NextBytes(inlineIv.AsSpan(0, cipher.GetBlockSize()));
        Array.Copy(inlineIv, inlineIv.Length - 4, inlineIv, inlineIv.Length - 2, 2);

        var mdcHeader = new byte[] { 0xD3, 0x14 };
        var digest = DigestUtilities.CalculateDigest("SHA1", Concat(inlineIv, innerBytes, mdcHeader));
        var encrypted = cipher.DoFinal(Concat(inlineIv, innerBytes, mdcHeader, digest));

        using var output = new MemoryStream();
        using (var pOut = new BcpgOutputStream(output, PacketTag.SymmetricEncryptedIntegrityProtected, 1 + encrypted.Length))
        {
            pOut.WriteByte(1);
            pOut.Write(encrypted);
        }

        return output.ToArray();
    }

    public static byte[] DecryptWithSessionKey(byte[] ciphertext, byte[] sessionKey)
    {
        var body = ReadPacketBody(ciphertext, PacketTag.SymmetricEncryptedIntegrityProtected);
        if (body.Length < 2 || body[0] != 1)
            throw new ProtonDriveException("Unsupported Proton Drive block encryption.");

        var key = new KeyParameter(sessionKey);
        var cipher = CipherUtilities.GetCipher("AES/CFB/NoPadding");
        cipher.Init(false, new ParametersWithIV(key, new byte[cipher.GetBlockSize()]));
        var decrypted = cipher.DoFinal(body, 1, body.Length - 1);
        var prefix = cipher.GetBlockSize() + 2;
        if (decrypted.Length < prefix + 22)
            throw new ProtonDriveException("Proton Drive block is truncated.");

        var inner = decrypted.AsSpan(prefix, decrypted.Length - prefix - 22).ToArray();
        return ReadLiteral(new PgpObjectFactory(new MemoryStream(inner)));
    }

    public static byte[] VerificationToken(byte[] verificationCode, byte[] encryptedBlock)
    {
        var token = new byte[verificationCode.Length];
        for (var i = 0; i < token.Length; i++)
        {
            var b = i < encryptedBlock.Length ? encryptedBlock[i] : (byte)0;
            token[i] = (byte)(verificationCode[i] ^ b);
        }

        return token;
    }

    public static ProtonFileDraft CreateFileDraft(string name, string parentLinkId, ProtonKeySet parent, byte[] hashKey)
    {
        var passphrase = Encoding.UTF8.GetBytes(GeneratePassphrase());
        var (armoredKey, fileKeys) = GenerateNodeKey(passphrase);
        var sessionKey = GenerateSessionKey();
        var keyPacket = EncryptSessionKey(sessionKey, fileKeys.EncryptionPublic);

        return new ProtonFileDraft
        {
            FileKeys = fileKeys,
            SessionKey = sessionKey,
            Body = new Dictionary<string, object?>
            {
                ["Name"] = EncryptAndSign(Encoding.UTF8.GetBytes(name), parent.EncryptionPublic, parent),
                ["Hash"] = LookupHash(name, hashKey),
                ["ParentLinkID"] = parentLinkId,
                ["NodePassphrase"] = EncryptToKey(passphrase, parent.EncryptionPublic),
                ["NodePassphraseSignature"] = SignDetachedArmored(passphrase, parent),
                ["NodeKey"] = armoredKey,
                ["MIMEType"] = "application/octet-stream",
                ["ContentKeyPacket"] = Convert.ToBase64String(keyPacket),
                ["ContentKeyPacketSignature"] = SignDetachedArmored(sessionKey, fileKeys),
            },
        };
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

    private static byte[] ReadPacketBody(byte[] packet, PacketTag expected)
    {
        if (packet.Length < 2)
            throw new ProtonDriveException("Proton Drive OpenPGP packet is truncated.");

        var header = packet[0];
        if ((header & 0x80) == 0)
            throw new ProtonDriveException("Proton Drive OpenPGP packet header is invalid.");

        int offset;
        PacketTag tag;
        if ((header & 0x40) != 0)
        {
            tag = (PacketTag)(header & 0x3f);
            offset = ReadNewLength(packet, 1, out var bodyLength);
            if (offset + bodyLength > packet.Length)
                throw new ProtonDriveException("Proton Drive OpenPGP packet is truncated.");
            if (tag != expected)
                throw new ProtonDriveException("Unexpected Proton Drive OpenPGP packet.");
            return packet.AsSpan(offset, bodyLength).ToArray();
        }

        tag = (PacketTag)((header & 0x3f) >> 2);
        var lengthType = header & 0x3;
        offset = 1;
        var length = lengthType switch
        {
            0 => packet[offset++],
            1 => (packet[offset++] << 8) | packet[offset++],
            2 => (packet[offset++] << 24) | (packet[offset++] << 16) | (packet[offset++] << 8) | packet[offset++],
            _ => packet.Length - offset,
        };
        if (offset + length > packet.Length)
            throw new ProtonDriveException("Proton Drive OpenPGP packet is truncated.");
        if (tag != expected)
            throw new ProtonDriveException("Unexpected Proton Drive OpenPGP packet.");
        return packet.AsSpan(offset, length).ToArray();
    }

    private static int ReadNewLength(byte[] packet, int offset, out int bodyLength)
    {
        var first = packet[offset];
        if (first < 192)
        {
            bodyLength = first;
            return offset + 1;
        }

        if (first < 224)
        {
            bodyLength = ((first - 192) << 8) + packet[offset + 1] + 192;
            return offset + 2;
        }

        if (first == 255)
        {
            bodyLength = (packet[offset + 1] << 24)
                         | (packet[offset + 2] << 16)
                         | (packet[offset + 3] << 8)
                         | packet[offset + 4];
            return offset + 5;
        }

        throw new ProtonDriveException("Proton Drive OpenPGP packet uses an unsupported length.");
    }

    private static byte[] CreateSessionInfo(byte[] sessionKey)
    {
        var sessionInfo = new byte[sessionKey.Length + 3];
        sessionInfo[0] = (byte)SymmetricKeyAlgorithmTag.Aes256;
        sessionKey.CopyTo(sessionInfo, 1);
        var check = 0;
        for (var i = 1; i < sessionInfo.Length - 2; i++)
            check += sessionInfo[i];
        sessionInfo[^2] = (byte)(check >> 8);
        sessionInfo[^1] = (byte)check;
        return sessionInfo;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var size = parts.Sum(p => p.Length);
        var output = new byte[size];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(output, offset);
            offset += part.Length;
        }

        return output;
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
    PgpPrivateKey signingPrivate,
    PgpPublicKey signingPublic,
    IReadOnlyList<PgpPrivateKey> privateKeys)
{
    public PgpPrivateKey EncryptionPrivate { get; } = encryptionPrivate;

    public PgpPublicKey EncryptionPublic { get; } = encryptionPublic;

    public PgpPrivateKey SigningPrivate { get; } = signingPrivate;

    public PgpPublicKey SigningPublic { get; } = signingPublic;

    public IReadOnlyList<PgpPrivateKey> PrivateKeys { get; } = privateKeys;
}

internal sealed class ProtonFileDraft
{
    public required ProtonKeySet FileKeys { get; init; }

    public required byte[] SessionKey { get; init; }

    public required Dictionary<string, object?> Body { get; init; }
}

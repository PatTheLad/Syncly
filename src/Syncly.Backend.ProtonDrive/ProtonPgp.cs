using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.IO;

namespace Syncly.Backend.ProtonDrive;

/// <summary>The slice of OpenPGP Proton Drive uses to wrap share keys and file contents.</summary>
internal static class ProtonPgp
{
    private static readonly byte[] AnonymousSender = "Anonymous Sender    "u8.ToArray();

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
        // Proton's CreateFile verifier unlocks NodeKey with gopenpgp, then decrypts
        // ContentKeyPacket. BouncyCastle EdDSA→X25519 rings fail that check (200501):
        // gopenpgp drops the encryption subkey or cannot open the ECDH PKESK.
        // RSA bindings verify. The encrypt subkey is 1024-bit so the PKESK stays
        // ≤255 base64 characters (RSA-2048 is rejected as "too long").
        var signRsa = new RsaKeyPairGenerator();
        signRsa.Init(new KeyGenerationParameters(random, 2048));
        var signPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaSign, signRsa.GenerateKeyPair(), DateTime.UtcNow);

        var encRsa = new RsaKeyPairGenerator();
        encRsa.Init(new KeyGenerationParameters(random, 1024));
        var encPair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaEncrypt, encRsa.GenerateKeyPair(), DateTime.UtcNow);

        var primaryHashed = new PgpSignatureSubpacketGenerator();
        primaryHashed.SetKeyFlags(false, PgpKeyFlags.CanCertify | PgpKeyFlags.CanSign);
        var subHashed = new PgpSignatureSubpacketGenerator();
        subHashed.SetKeyFlags(false, PgpKeyFlags.CanEncryptCommunications | PgpKeyFlags.CanEncryptStorage);

        var generator = new PgpKeyRingGenerator(
            PgpSignature.PositiveCertification,
            signPair,
            "Drive key <no-reply@proton.me>",
            SymmetricKeyAlgorithmTag.Aes256,
            ToChars(passphrase),
            true,
            primaryHashed.Generate(),
            null,
            random);
        generator.AddSubKey(encPair, subHashed.Generate(), null);

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

        // Parent folder keys from Proton are ECDH. BouncyCastle's ECDH PKESK often fails
        // gopenpgp decrypt on the server (CreateFile 200501), so hand-roll go-crypto style.
        if (publicKey.Algorithm == PublicKeyAlgorithmTag.ECDH)
        {
            using var literal = new MemoryStream();
            var literalGen = new PgpLiteralDataGenerator();
            using (var lit = literalGen.Open(literal, PgpLiteralData.Binary, "", plaintext.Length, DateTime.UtcNow))
                lit.Write(plaintext);

            return ArmorEncrypted(publicKey, literal.ToArray());
        }

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

        if (recipient.Algorithm == PublicKeyAlgorithmTag.ECDH)
        {
            using var clear = new MemoryStream();
            var sigGen = new PgpSignatureGenerator(signer.SigningPublic.Algorithm, HashAlgorithmTag.Sha256);
            sigGen.InitSign(PgpSignature.BinaryDocument, signer.SigningPrivate);
            sigGen.GenerateOnePassVersion(false).Encode(clear);

            var literal = new PgpLiteralDataGenerator();
            using (var lit = literal.Open(clear, PgpLiteralData.Binary, "", DateTime.UtcNow, new byte[4096]))
            {
                lit.Write(plaintext);
                sigGen.Update(plaintext);
            }

            sigGen.Generate().Encode(clear);
            return ArmorEncrypted(recipient, clear.ToArray());
        }

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

    public static (byte[] KeyPacket, byte[] SessionKey) CreateContentKey(ProtonKeySet fileKeys)
    {
        var sessionKey = GenerateSessionKey();
        var keyPacket = EncryptSessionKey(sessionKey, fileKeys.EncryptionPublic);
        var encoded = Convert.ToBase64String(keyPacket);
        if (encoded.Length > 255)
            throw new ProtonDriveException(
                $"Proton Drive content key packet is {encoded.Length} characters; maximum is 255.");

        return (keyPacket, sessionKey);
    }

    public static byte[] DecryptSessionKey(byte[] keyPacket, PgpPrivateKey privateKey)
    {
        var body = ReadPacketBody(keyPacket, PacketTag.PublicKeyEncryptedSession);
        if (body.Length < 10)
            throw new ProtonDriveException("Proton Drive content key packet is truncated.");

        if (body[0] == 3)
        {
            var alg = (PublicKeyAlgorithmTag)body[9];
            if (alg is PublicKeyAlgorithmTag.RsaEncrypt or PublicKeyAlgorithmTag.RsaGeneral)
                return DecryptSessionKeyRsa(body, privateKey);
        }

        return DecryptSessionKeyEcdh(keyPacket, privateKey);
    }

    public static byte[] EncryptWithSessionKey(byte[] plaintext, byte[] sessionKey)
    {
        using var inner = new MemoryStream();
        var literal = new PgpLiteralDataGenerator();
        using (var lit = literal.Open(inner, PgpLiteralData.Binary, "", DateTime.UtcNow, new byte[1 << 16]))
            lit.Write(plaintext);

        return EncryptSeipd(inner.ToArray(), sessionKey);
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
        var (keyPacket, sessionKey) = CreateContentKey(fileKeys);

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
                ["ClientUID"] = Guid.NewGuid().ToString(),
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

    private static string ArmorEncrypted(PgpPublicKey recipient, byte[] clearPackets)
    {
        var sessionKey = GenerateSessionKey();
        var message = Concat(EncryptSessionKey(sessionKey, recipient), EncryptSeipd(clearPackets, sessionKey));
        using var outStream = new MemoryStream();
        using (var armored = new ArmoredOutputStream(outStream))
            armored.Write(message);
        return Encoding.ASCII.GetString(outStream.ToArray());
    }

    private static byte[] EncryptSessionKey(byte[] sessionKey, PgpPublicKey publicKey) =>
        publicKey.Algorithm switch
        {
            PublicKeyAlgorithmTag.ECDH => EncryptSessionKeyEcdh(sessionKey, publicKey),
            PublicKeyAlgorithmTag.RsaEncrypt or PublicKeyAlgorithmTag.RsaGeneral
                => EncryptSessionKeyRsa(sessionKey, publicKey),
            _ => throw new ProtonDriveException("Unsupported Proton Drive encryption key algorithm."),
        };

    /// <summary>
    /// v3 ECDH PKESK matching ProtonMail/go-crypto (40-byte PKCS5 pad + RFC6637 KDF).
    /// </summary>
    private static byte[] EncryptSessionKeyEcdh(byte[] sessionKey, PgpPublicKey publicKey)
    {
        if (publicKey.GetKey() is not X25519PublicKeyParameters recipient)
            throw new ProtonDriveException("The Proton Drive file key is not Curve25519.");

        var random = new SecureRandom();
        var ephGen = new X25519KeyPairGenerator();
        ephGen.Init(new X25519KeyGenerationParameters(random));
        var eph = ephGen.GenerateKeyPair();
        var ephPub = new byte[32];
        ((X25519PublicKeyParameters)eph.Public).Encode(ephPub);

        var point = new byte[33];
        point[0] = 0x40;
        ephPub.CopyTo(point, 1);
        var mpi = new MPInteger(new BigInteger(1, point)).GetEncoded();

        var shared = AgreeX25519((X25519PrivateKeyParameters)eph.Private, recipient);
        var wrapped = AesWrap(Rfc6637Kek(publicKey, shared), PgpPad.PadSessionData(SessionInfo(sessionKey), false));

        using var body = new MemoryStream();
        body.WriteByte(3);
        Span<byte> keyId = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(keyId, publicKey.KeyId);
        body.Write(keyId);
        body.WriteByte((byte)PublicKeyAlgorithmTag.ECDH);
        body.Write(mpi);
        body.WriteByte((byte)wrapped.Length);
        body.Write(wrapped);

        var bodyBytes = body.ToArray();
        using var output = new MemoryStream();
        using (var pOut = new BcpgOutputStream(output, PacketTag.PublicKeyEncryptedSession, bodyBytes.Length))
            pOut.Write(bodyBytes);

        return output.ToArray();
    }

    private static byte[] EncryptSessionKeyRsa(byte[] sessionKey, PgpPublicKey publicKey)
    {
        var sessionInfo = SessionInfo(sessionKey);
        var cipher = CipherUtilities.GetCipher("RSA/ECB/PKCS1Padding");
        cipher.Init(true, publicKey.GetKey());
        var encrypted = cipher.DoFinal(sessionInfo);
        var mpi = new MPInteger(new BigInteger(1, encrypted)).GetEncoded();

        using var body = new MemoryStream();
        body.WriteByte(3);
        Span<byte> keyId = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(keyId, publicKey.KeyId);
        body.Write(keyId);
        body.WriteByte((byte)publicKey.Algorithm);
        body.Write(mpi);

        var bodyBytes = body.ToArray();
        using var output = new MemoryStream();
        using (var pOut = new BcpgOutputStream(output, PacketTag.PublicKeyEncryptedSession, bodyBytes.Length))
            pOut.Write(bodyBytes);

        return output.ToArray();
    }

    private static byte[] EncryptSeipd(byte[] innerBytes, byte[] sessionKey)
    {
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

    private static byte[] SessionInfo(byte[] sessionKey)
    {
        var info = new byte[1 + sessionKey.Length + 2];
        info[0] = (byte)SymmetricKeyAlgorithmTag.Aes256;
        sessionKey.CopyTo(info, 1);
        var check = 0;
        foreach (var b in sessionKey)
            check += b;
        info[^2] = (byte)(check >> 8);
        info[^1] = (byte)check;
        return info;
    }

    private static byte[] AesWrap(byte[] kek, byte[] data)
    {
        var wrapper = WrapperUtilities.GetWrapper("AESWRAP");
        wrapper.Init(true, new KeyParameter(kek));
        return wrapper.Wrap(data, 0, data.Length);
    }

    private static byte[] DecryptSessionKeyRsa(byte[] body, PgpPrivateKey privateKey)
    {
        var mpiBits = (body[10] << 8) | body[11];
        var mpiLen = (mpiBits + 7) / 8;
        if (12 + mpiLen > body.Length)
            throw new ProtonDriveException("Proton Drive content key packet is truncated.");

        var encrypted = body.AsSpan(12, mpiLen).ToArray();
        var cipher = CipherUtilities.GetCipher("RSA/ECB/PKCS1Padding");
        cipher.Init(false, privateKey.Key);
        var sessionInfo = cipher.DoFinal(encrypted);
        if (sessionInfo.Length < 4 || sessionInfo[0] != (byte)SymmetricKeyAlgorithmTag.Aes256)
            throw new ProtonDriveException("Proton Drive content key is not AES-256.");

        var keyLen = sessionInfo.Length - 3;
        var check = 0;
        for (var i = 0; i < keyLen; i++)
            check += sessionInfo[1 + i];
        if (sessionInfo[^2] != (byte)(check >> 8) || sessionInfo[^1] != (byte)check)
            throw new ProtonDriveException("Proton Drive content key checksum is invalid.");

        return sessionInfo.AsSpan(1, keyLen).ToArray();
    }

    private static byte[] DecryptSessionKeyEcdh(byte[] keyPacket, PgpPrivateKey privateKey)
    {
        var body = ReadPacketBody(keyPacket, PacketTag.PublicKeyEncryptedSession);
        if (body.Length < 8)
            throw new ProtonDriveException("Proton Drive content key packet is truncated.");

        var version = body[0];
        int mpiOffset;
        if (version == 6)
        {
            var fpSize = body[1];
            mpiOffset = 2 + fpSize + 1;
            if (mpiOffset + 3 > body.Length || body[2 + fpSize] != (byte)PublicKeyAlgorithmTag.ECDH)
                throw new ProtonDriveException("Proton Drive content key packet is not ECDH.");
        }
        else if (version == 3)
        {
            mpiOffset = 10;
            if (body[9] != (byte)PublicKeyAlgorithmTag.ECDH)
                throw new ProtonDriveException("Proton Drive content key packet is not ECDH.");
        }
        else
            throw new ProtonDriveException("Proton Drive content key packet is not ECDH.");

        var mpiBits = (body[mpiOffset] << 8) | body[mpiOffset + 1];
        var mpiLen = (mpiBits + 7) / 8;
        if (mpiOffset + 2 + mpiLen + 1 > body.Length)
            throw new ProtonDriveException("Proton Drive content key packet is truncated.");

        var point = body.AsSpan(mpiOffset + 2, mpiLen);
        var ephPub = point[0] == 0x40 ? point[1..].ToArray() : point.ToArray();
        var wrapLen = body[mpiOffset + 2 + mpiLen];
        var wrapped = body.AsSpan(mpiOffset + 3 + mpiLen, wrapLen).ToArray();

        var secret = (X25519PrivateKeyParameters)privateKey.Key;
        var shared = AgreeX25519(secret, new X25519PublicKeyParameters(ephPub));
        var publicKey = new PgpPublicKey(privateKey.PublicKeyPacket);
        var padded = AesUnwrap(Rfc6637Kek(publicKey, shared), wrapped);
        var sessionInfo = PgpPad.UnpadSessionData(padded);
        if (version == 6)
        {
            if (sessionInfo.Length < 3)
                throw new ProtonDriveException("Proton Drive content key packet is truncated.");
            return sessionInfo.AsSpan(0, sessionInfo.Length - 2).ToArray();
        }

        if (sessionInfo.Length < 4 || sessionInfo[0] != (byte)SymmetricKeyAlgorithmTag.Aes256)
            throw new ProtonDriveException("Proton Drive content key is not AES-256.");

        return sessionInfo.AsSpan(1, sessionInfo.Length - 3).ToArray();
    }

    private static byte[] AgreeX25519(X25519PrivateKeyParameters privateKey, X25519PublicKeyParameters publicKey)
    {
        var agreement = new X25519Agreement();
        agreement.Init(privateKey);
        var shared = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(publicKey, shared, 0);
        return shared;
    }

    private static byte[] Rfc6637Kek(PgpPublicKey publicKey, byte[] secret)
    {
        var param = Rfc6637Param(publicKey);
        var digest = SHA256.HashData(Concat([0, 0, 0, 1], secret, param));
        return digest[..16];
    }

    private static byte[] Rfc6637Param(PgpPublicKey publicKey)
    {
        var ec = (ECDHPublicBcpgKey)publicKey.PublicKeyPacket.Key;
        var oid = ec.CurveOid.GetEncoded();
        using var param = new MemoryStream();
        param.Write(oid, 1, oid.Length - 1);
        param.WriteByte((byte)publicKey.Algorithm);
        param.WriteByte(0x03);
        param.WriteByte(0x01);
        param.WriteByte((byte)ec.HashAlgorithm);
        param.WriteByte((byte)ec.SymmetricKeyAlgorithm);
        param.Write(AnonymousSender);
        param.Write(publicKey.GetFingerprint());
        return param.ToArray();
    }

    private static byte[] AesUnwrap(byte[] kek, byte[] data)
    {
        var wrapper = WrapperUtilities.GetWrapper("AESWRAP");
        wrapper.Init(false, new KeyParameter(kek));
        return wrapper.Unwrap(data, 0, data.Length);
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

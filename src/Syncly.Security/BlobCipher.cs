using System.Security.Cryptography;

namespace Syncly.Security;

/// <summary>
/// AES-256-GCM over a key derived from the sync chain. The mailbox (folder or Proton Drive) only
/// ever sees ciphertext.
/// </summary>
public static class BlobCipher
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    private static readonly byte[] Magic = "SLY1"u8.ToArray();
    private static readonly byte[] HkdfSalt = "syncly-blob-v1"u8.ToArray();
    private static readonly byte[] HkdfInfo = "aes-256-gcm"u8.ToArray();

    public static byte[] Encrypt(SyncChain chain, ReadOnlySpan<byte> plaintext)
    {
        var key = Derive(chain);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[Magic.Length + NonceSize + ciphertext.Length + TagSize];
        Magic.CopyTo(blob, 0);
        nonce.CopyTo(blob.AsSpan(Magic.Length));
        ciphertext.CopyTo(blob.AsSpan(Magic.Length + NonceSize));
        tag.CopyTo(blob.AsSpan(blob.Length - TagSize));
        return blob;
    }

    public static byte[] Decrypt(SyncChain chain, ReadOnlySpan<byte> blob)
    {
        if (blob.Length < Magic.Length + NonceSize + TagSize)
            throw new CryptographicException("Encrypted pack is truncated.");

        if (!blob[..Magic.Length].SequenceEqual(Magic))
            throw new CryptographicException("This file is not a Syncly pack.");

        var key = Derive(chain);
        var nonce = blob.Slice(Magic.Length, NonceSize);
        var tag = blob[^TagSize..];
        var ciphertext = blob.Slice(Magic.Length + NonceSize, blob.Length - Magic.Length - NonceSize - TagSize);
        var plaintext = new byte[ciphertext.Length];

        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static bool LooksLikePack(ReadOnlySpan<byte> blob) =>
        blob.Length >= Magic.Length && blob[..Magic.Length].SequenceEqual(Magic);

    private static byte[] Derive(SyncChain chain)
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, chain.Secret, key, HkdfSalt, HkdfInfo);
        return key;
    }
}

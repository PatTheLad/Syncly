using System.Security.Cryptography;
using System.Text;

namespace Syncly.Security;

/// <summary>
/// The secret that binds devices to one mailbox. Anyone with the 24 words can read and write the
/// encrypted blobs; the cloud host never sees the key.
/// </summary>
public sealed class SyncChain
{
    public const int SecretSize = 32;
    public const string UriPrefix = "syncly:sync:v1:";

    private SyncChain(byte[] secret)
    {
        if (secret.Length != SecretSize)
            throw new ArgumentException("Sync chain secrets are 32 bytes.", nameof(secret));

        Secret = secret;
        Words = Bip39.Encode(secret);
        Uri = UriPrefix + Base32.Encode(secret);
        ChainId = Convert.ToHexStringLower(SHA256.HashData(secret))[..16];
    }

    public byte[] Secret { get; }

    public string Words { get; }

    public string Uri { get; }

    /// <summary>Truncated hash of the secret. Safe to store next to the blobs so a device can
    /// notice it is pointed at the wrong folder before it tries to decrypt anything.</summary>
    public string ChainId { get; }

    public string ToVault() => Convert.ToBase64String(Secret);

    public static SyncChain Create()
    {
        var secret = new byte[SecretSize];
        RandomNumberGenerator.Fill(secret);
        return new SyncChain(secret);
    }

    public static SyncChain FromSecret(byte[] secret) => new(secret.ToArray());

    public static SyncChain FromVault(string stored) =>
        new(Convert.FromBase64String(stored));

    /// <summary>Accepts 24 words, a <c>syncly:sync:v1:</c> URI, or raw base32.</summary>
    public static SyncChain Parse(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0)
            throw new FormatException("Paste the 24-word chain code or scan the QR.");

        if (trimmed.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase))
            return new SyncChain(Base32.Decode(trimmed[UriPrefix.Length..]));

        if (trimmed.Contains(' ', StringComparison.Ordinal)
            || trimmed.Contains('\n', StringComparison.Ordinal))
            return new SyncChain(Bip39.Decode(trimmed));

        if (trimmed.Length == 52 || trimmed.Length == 56)
            return new SyncChain(Base32.Decode(trimmed));

        return new SyncChain(Bip39.Decode(trimmed));
    }

    public static bool TryParse(string input, out SyncChain chain)
    {
        try
        {
            chain = Parse(input);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            chain = null!;
            return false;
        }
    }
}

/// <summary>RFC 4648 base32, no padding, lowercase. Compact enough to put in a QR.</summary>
public static class Base32
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return builder.ToString();
    }

    public static byte[] Decode(string text)
    {
        var clean = text.Trim().TrimEnd('=').ToLowerInvariant();
        var buffer = 0;
        var bits = 0;
        var output = new List<byte>(clean.Length * 5 / 8);

        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException("Chain URI contains a character that is not base32.");

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
            }
        }

        return [.. output];
    }
}

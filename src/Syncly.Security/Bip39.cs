using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Syncly.Security;

/// <summary>
/// BIP39 with the English wordlist. 256 bits of entropy plus an 8-bit checksum become 24 words,
/// so a mistyped code fails instead of silently joining the wrong chain.
/// </summary>
public static class Bip39
{
    private static readonly string[] Words = Load();

    public static IReadOnlyList<string> WordList => Words;

    public static string Encode(ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != 32)
            throw new ArgumentException("Sync chain secrets are 32 bytes (24 words).", nameof(entropy));

        var checksum = SHA256.HashData(entropy)[0];
        Span<byte> data = stackalloc byte[33];
        entropy.CopyTo(data);
        data[32] = checksum;

        var words = new string[24];
        var bits = new BitReader(data);
        for (var i = 0; i < 24; i++)
            words[i] = Words[bits.Read(11)];

        return string.Join(' ', words);
    }

    public static byte[] Decode(string phrase)
    {
        var parts = Split(phrase);
        if (parts.Length != 24)
            throw new FormatException("A Syncly chain code is 24 words.");

        Span<byte> data = stackalloc byte[33];
        var writer = new BitWriter(data);
        foreach (var word in parts)
        {
            var index = Array.BinarySearch(Words, word, StringComparer.Ordinal);
            if (index < 0)
                throw new FormatException($"Unknown word “{word}” in the chain code.");

            writer.Write(11, index);
        }

        var entropy = data[..32].ToArray();
        var expected = SHA256.HashData(entropy)[0];
        if (data[32] != expected)
            throw new FormatException("Chain code checksum does not match. Check for a typo.");

        return entropy;
    }

    public static bool TryDecode(string phrase, out byte[] secret)
    {
        try
        {
            secret = Decode(phrase);
            return true;
        }
        catch (FormatException)
        {
            secret = [];
            return false;
        }
    }

    private static string[] Split(string phrase)
    {
        return phrase
            .Trim()
            .ToLowerInvariant()
            .Split([' ', '\n', '\r', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] Load()
    {
        var assembly = typeof(Bip39).Assembly;
        var name = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith("Bip39English.txt", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("BIP39 wordlist is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var words = reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (words.Length != 2048)
            throw new InvalidOperationException($"BIP39 wordlist has {words.Length} words, expected 2048.");

        return words;
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var b = _data[_bit / 8];
                var bit = (b >> (7 - _bit % 8)) & 1;
                value = (value << 1) | bit;
                _bit++;
            }

            return value;
        }
    }

    private ref struct BitWriter(Span<byte> data)
    {
        private readonly Span<byte> _data = data;
        private int _bit;

        public void Write(int count, int value)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                    _data[_bit / 8] |= (byte)(1 << (7 - _bit % 8));

                _bit++;
            }
        }
    }
}

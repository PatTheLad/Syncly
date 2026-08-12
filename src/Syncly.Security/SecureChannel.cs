using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Syncly.Security;

public sealed class ChannelSecurityException(string message) : Exception(message);

/// <summary>
/// AES-GCM in both directions with separate keys and a strictly increasing counter per direction.
/// The counter is the nonce, so a replayed or rewound frame decrypts to nothing and is rejected
/// before it reaches the sync engine.
/// </summary>
public sealed class SecureChannel : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private long _sendCounter;
    private long _highestReceived = -1;

    internal SecureChannel(byte[] sendKey, byte[] receiveKey)
    {
        _send = new AesGcm(sendKey, TagSize);
        _receive = new AesGcm(receiveKey, TagSize);
    }

    public long FramesSent => _sendCounter;

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var counter = Interlocked.Increment(ref _sendCounter) - 1;

        var frame = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = frame.AsSpan(0, NonceSize);
        BinaryPrimitives.WriteInt64BigEndian(nonce[4..], counter);

        _send.Encrypt(
            nonce,
            plaintext,
            frame.AsSpan(NonceSize, plaintext.Length),
            frame.AsSpan(NonceSize + plaintext.Length, TagSize));

        return frame;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < NonceSize + TagSize)
            throw new ChannelSecurityException("Frame is too short to be authentic.");

        var nonce = frame[..NonceSize];
        var counter = BinaryPrimitives.ReadInt64BigEndian(nonce[4..]);

        if (counter <= _highestReceived)
            throw new ChannelSecurityException(
                $"Replayed or rewound frame (counter {counter}, expected above {_highestReceived}).");

        var payloadLength = frame.Length - NonceSize - TagSize;
        var plaintext = new byte[payloadLength];

        try
        {
            _receive.Decrypt(
                nonce,
                frame.Slice(NonceSize, payloadLength),
                frame.Slice(NonceSize + payloadLength, TagSize),
                plaintext);
        }
        catch (CryptographicException)
        {
            throw new ChannelSecurityException("Frame failed authentication.");
        }

        _highestReceived = counter;
        return plaintext;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }
}

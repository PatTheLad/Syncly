using System.Security.Cryptography;
using Syncly.Security;

namespace Syncly.Sync.Tests;

public class HandshakeTests
{
    private static async Task<(HandshakeResult A, HandshakeResult B)> RunAsync(
        DeviceIdentity a,
        DeviceIdentity b)
    {
        var (left, right) = MemoryChannel.Pair("handshake");

        var runA = Handshake.RunAsync(left, a, initiator: true);
        var runB = Handshake.RunAsync(right, b, initiator: false);

        await Task.WhenAll(runA, runB);
        return (runA.Result, runB.Result);
    }

    [Fact]
    public async Task Both_sides_authenticate_and_see_the_same_pairing_code()
    {
        using var a = DeviceIdentity.CreateEphemeral("Laptop");
        using var b = DeviceIdentity.CreateEphemeral("Phone");

        var (resultA, resultB) = await RunAsync(a, b);

        Assert.Equal(b.DeviceId, resultA.Peer.DeviceId);
        Assert.Equal(a.DeviceId, resultB.Peer.DeviceId);
        Assert.Equal(resultA.ComparisonCode, resultB.ComparisonCode);
        Assert.Equal(6, resultA.ComparisonCode.Length);
        Assert.Equal(resultA.TranscriptHash, resultB.TranscriptHash);
    }

    [Fact]
    public async Task The_session_carries_messages_in_both_directions()
    {
        using var a = DeviceIdentity.CreateEphemeral("Laptop");
        using var b = DeviceIdentity.CreateEphemeral("Phone");
        var (resultA, resultB) = await RunAsync(a, b);

        var frame = resultA.Channel.Encrypt("hello"u8);
        Assert.Equal("hello"u8.ToArray(), resultB.Channel.Decrypt(frame));

        var back = resultB.Channel.Encrypt("hi"u8);
        Assert.Equal("hi"u8.ToArray(), resultA.Channel.Decrypt(back));
    }

    [Fact]
    public async Task Replaying_a_frame_is_rejected()
    {
        using var a = DeviceIdentity.CreateEphemeral("Laptop");
        using var b = DeviceIdentity.CreateEphemeral("Phone");
        var (resultA, resultB) = await RunAsync(a, b);

        var first = resultA.Channel.Encrypt("one"u8);
        var second = resultA.Channel.Encrypt("two"u8);

        resultB.Channel.Decrypt(first);
        resultB.Channel.Decrypt(second);

        // Rewinding to an already-seen counter must not be accepted.
        Assert.Throws<ChannelSecurityException>(() => resultB.Channel.Decrypt(first));
    }

    [Fact]
    public async Task A_tampered_frame_is_rejected()
    {
        using var a = DeviceIdentity.CreateEphemeral("Laptop");
        using var b = DeviceIdentity.CreateEphemeral("Phone");
        var (resultA, resultB) = await RunAsync(a, b);

        var frame = resultA.Channel.Encrypt("payload"u8);
        frame[^1] ^= 0xFF;

        Assert.Throws<ChannelSecurityException>(() => resultB.Channel.Decrypt(frame));
    }

    [Fact]
    public async Task A_relay_that_swaps_the_ephemeral_key_cannot_complete_the_handshake()
    {
        using var a = DeviceIdentity.CreateEphemeral("Laptop");
        using var b = DeviceIdentity.CreateEphemeral("Phone");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;

        var (clientSide, attackerToClient) = MemoryChannel.Pair("victim");
        var (attackerToServer, serverSide) = MemoryChannel.Pair("target");

        var runA = Handshake.RunAsync(clientSide, a, initiator: true, ct);
        var runB = Handshake.RunAsync(serverSide, b, initiator: false, ct);

        _ = Task.Run(async () =>
        {
            // Hellos pass through untouched, so both sides still believe they know each other.
            await attackerToServer.SendAsync(await attackerToClient.ReceiveAsync(ct), ct);
            await attackerToClient.SendAsync(await attackerToServer.ReceiveAsync(ct), ct);

            // The key offers are replaced with the attacker's own, which is the classic relay.
            _ = await attackerToClient.ReceiveAsync(ct);
            _ = await attackerToServer.ReceiveAsync(ct);

            using var mine = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var forged = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new Dictionary<string, string>
                {
                    ["e"] = Convert.ToBase64String(mine.PublicKey.ExportSubjectPublicKeyInfo()),
                });

            await attackerToServer.SendAsync(forged, ct);
            await attackerToClient.SendAsync(forged, ct);

            while (!ct.IsCancellationRequested)
            {
                var forward = attackerToClient.ReceiveAsync(ct);
                var backward = attackerToServer.ReceiveAsync(ct);
                var done = await Task.WhenAny(forward, backward);

                if (done == forward)
                    await attackerToServer.SendAsync(await forward, ct);
                else
                    await attackerToClient.SendAsync(await backward, ct);
            }
        }, ct);

        // Neither side can verify a signature over a transcript the relay changed.
        await Assert.ThrowsAnyAsync<Exception>(async () => await runA);
        await Assert.ThrowsAnyAsync<Exception>(async () => await runB);
    }

    [Fact]
    public async Task A_device_cannot_claim_an_id_it_has_no_key_for()
    {
        using var a = DeviceIdentity.CreateEphemeral("Laptop");
        var (left, right) = MemoryChannel.Pair("spoof");

        var run = Handshake.RunAsync(left, a, initiator: true);

        _ = await right.ReceiveAsync();
        await right.SendAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object>
            {
                ["v"] = Handshake.ProtocolVersion,
                ["id"] = "0000000000000000000000000000000f",
                ["n"] = "Impostor",
                ["k"] = a.PublicKeyBase64,
                ["r"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            }));

        var error = await Assert.ThrowsAsync<HandshakeException>(async () => await run);
        Assert.Contains("device id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comparison_codes_are_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = Handshake.ComparisonCode(RandomNumberGenerator.GetBytes(8));
            Assert.Equal(6, code.Length);
            Assert.True(code.All(char.IsAsciiDigit));
        }
    }
}

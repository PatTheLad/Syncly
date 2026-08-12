using Syncly.Crdt;
using Syncly.Model;

namespace Syncly.Storage;

/// <summary>Per-peer sync bookkeeping: what they have, what they acked, when we last talked.</summary>
public sealed record PeerSyncState(
    string DeviceId,
    VersionVector Acked,
    VersionVector Theirs,
    DateTimeOffset LastSyncAt);

/// <summary>Trusted devices and the sync cursor we keep for each of them.</summary>
public sealed class PeerStore(SynclyDatabase database)
{
    public Task TrustAsync(TrustedDevice device, CancellationToken ct = default) =>
        database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO peers (device_id, display_name, public_key, trusted_at)
                VALUES ($id, $name, $key, $at)
                ON CONFLICT(device_id) DO UPDATE SET
                    display_name = excluded.display_name,
                    public_key = excluded.public_key
                """;
            command.Parameters.AddWithValue("$id", device.DeviceId);
            command.Parameters.AddWithValue("$name", device.DisplayName);
            command.Parameters.AddWithValue("$key", device.PublicKeyBase64);
            command.Parameters.AddWithValue("$at", device.TrustedAt.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(ct);
        }, ct);

    public Task RevokeAsync(string deviceId, CancellationToken ct = default) =>
        database.RunAsync(async connection =>
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM peers WHERE device_id = $id",
                         "DELETE FROM peer_state WHERE device_id = $id",
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", deviceId);
                await command.ExecuteNonQueryAsync(ct);
            }
        }, ct);

    public async Task<List<TrustedDevice>> ListAsync(CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT device_id, display_name, public_key, trusted_at FROM peers ORDER BY display_name";

            var peers = new List<TrustedDevice>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                peers.Add(new TrustedDevice(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3))));

            return peers;
        }, ct);

    public async Task<TrustedDevice?> FindAsync(string deviceId, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(p => p.DeviceId == deviceId);

    public Task RememberAddressAsync(string deviceId, string address, CancellationToken ct = default) =>
        database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE peers SET last_address = $address, last_seen = $seen WHERE device_id = $id";
            command.Parameters.AddWithValue("$address", address);
            command.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$id", deviceId);
            await command.ExecuteNonQueryAsync(ct);
        }, ct);

    public async Task<List<string>> KnownAddressesAsync(CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT last_address FROM peers WHERE last_address IS NOT NULL ORDER BY last_seen DESC";

            var addresses = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                addresses.Add(reader.GetString(0));

            return addresses;
        }, ct);

    public async Task<PeerSyncState> StateAsync(string deviceId, CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT acked_version, their_version, last_sync_at FROM peer_state WHERE device_id = $id";
            command.Parameters.AddWithValue("$id", deviceId);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new PeerSyncState(deviceId, new VersionVector(), new VersionVector(), DateTimeOffset.MinValue);

            return new PeerSyncState(
                deviceId,
                VersionVectorText.Parse(reader.GetString(0)),
                VersionVectorText.Parse(reader.GetString(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)));
        }, ct);

    /// <summary>
    /// Advances the cursor only after the peer acknowledged the batch, so an interrupted transfer
    /// resumes from the last confirmed point rather than from the start.
    /// </summary>
    public Task SaveStateAsync(PeerSyncState state, CancellationToken ct = default) =>
        database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO peer_state (device_id, acked_version, their_version, last_sync_at)
                VALUES ($id, $acked, $theirs, $at)
                ON CONFLICT(device_id) DO UPDATE SET
                    acked_version = excluded.acked_version,
                    their_version = excluded.their_version,
                    last_sync_at = excluded.last_sync_at
                """;
            command.Parameters.AddWithValue("$id", state.DeviceId);
            command.Parameters.AddWithValue("$acked", VersionVectorText.Format(state.Acked));
            command.Parameters.AddWithValue("$theirs", VersionVectorText.Format(state.Theirs));
            command.Parameters.AddWithValue("$at", state.LastSyncAt.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(ct);
        }, ct);

    /// <summary>
    /// The version every trusted peer has confirmed. Tombstones below this point can never be
    /// needed again, which is the only safe moment to collect them.
    /// </summary>
    public async Task<VersionVector?> StableVersionAsync(CancellationToken ct = default)
    {
        var peers = await ListAsync(ct);
        if (peers.Count == 0)
            return null;

        VersionVector? stable = null;
        foreach (var peer in peers)
        {
            var state = await StateAsync(peer.DeviceId, ct);
            stable = stable is null ? state.Acked : Intersect(stable, state.Acked);
        }

        return stable;
    }

    private static VersionVector Intersect(VersionVector a, VersionVector b)
    {
        var result = new VersionVector();
        foreach (var (actor, seq) in a)
            result.Advance(actor, Math.Min(seq, b.Next(actor)));

        return result;
    }
}

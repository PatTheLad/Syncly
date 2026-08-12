using Microsoft.Data.Sqlite;
using Syncly.Crdt;

namespace Syncly.Storage;

/// <summary>
/// The durable op log. Deltas for sync are read from here rather than from memory, so a device
/// that started from a snapshot can still serve a peer that has been offline for months.
/// </summary>
public sealed class OpLogStore(SynclyDatabase database)
{
    public Task AppendAsync(IReadOnlyList<Op> ops, CancellationToken ct = default)
    {
        if (ops.Count == 0)
            return Task.CompletedTask;

        return database.RunAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO ops (actor, seq, len, object_id, clock, payload)
                VALUES ($actor, $seq, $len, $object, $clock, $payload)
                ON CONFLICT (actor, seq) DO NOTHING
                """;

            var actor = command.Parameters.Add("$actor", SqliteType.Text);
            var seq = command.Parameters.Add("$seq", SqliteType.Integer);
            var len = command.Parameters.Add("$len", SqliteType.Integer);
            var obj = command.Parameters.Add("$object", SqliteType.Text);
            var clock = command.Parameters.Add("$clock", SqliteType.Text);
            var payload = command.Parameters.Add("$payload", SqliteType.Text);

            foreach (var op in ops)
            {
                actor.Value = op.Actor;
                seq.Value = op.Seq;
                len.Value = op.Length;
                obj.Value = op.ObjectId;
                clock.Value = op.Clock.ToString();
                payload.Value = OpCodec.Encode(op);
                await command.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }, ct);
    }

    public async Task<VersionVector> VersionAsync(CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT actor, MAX(seq + len) FROM ops GROUP BY actor";

            var version = new VersionVector();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                version.Advance(reader.GetString(0), reader.GetInt64(1));

            return version;
        }, ct);

    public async Task<List<Op>> ReadAllAsync(CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM ops ORDER BY clock, actor, seq";
            return await ReadOpsAsync(command, ct);
        }, ct);

    /// <summary>Exactly the ops <paramref name="peer"/> is missing, in causal order.</summary>
    public async Task<List<Op>> ReadSinceAsync(VersionVector peer, CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var actors = connection.CreateCommand();
            actors.CommandText = "SELECT DISTINCT actor FROM ops";

            var names = new List<string>();
            await using (var reader = await actors.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    names.Add(reader.GetString(0));

            var ops = new List<Op>();
            foreach (var actor in names)
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT payload FROM ops WHERE actor = $actor AND seq >= $from ORDER BY seq";
                command.Parameters.AddWithValue("$actor", actor);
                command.Parameters.AddWithValue("$from", peer.Next(actor));
                ops.AddRange(await ReadOpsAsync(command, ct));
            }

            ops.Sort(Replica.CausalOrder);
            return ops;
        }, ct);

    public async Task<long> CountAsync(CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ops";
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }, ct);

    /// <summary>Ops for one object, used when rebuilding a single page's projection.</summary>
    public async Task<List<Op>> ReadObjectAsync(string objectId, CancellationToken ct = default) =>
        await database.RunAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT payload FROM ops WHERE object_id = $id ORDER BY clock, actor, seq";
            command.Parameters.AddWithValue("$id", objectId);
            return await ReadOpsAsync(command, ct);
        }, ct);

    private static async Task<List<Op>> ReadOpsAsync(SqliteCommand command, CancellationToken ct)
    {
        var ops = new List<Op>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ops.Add(OpCodec.Decode(reader.GetString(0)));

        return ops;
    }
}

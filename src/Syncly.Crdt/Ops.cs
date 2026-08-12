using System.Text.Json.Serialization;
using Syncly.Model;

namespace Syncly.Crdt;

/// <summary>
/// The four op shapes the whole app is built from. Every op is idempotent and commutative, so a
/// replica can apply them in any order, more than once, and still land on the same state.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(TextInsert), "ti")]
[JsonDerivedType(typeof(TextDelete), "td")]
[JsonDerivedType(typeof(BlockUpsert), "bu")]
[JsonDerivedType(typeof(PropSet), "ps")]
public abstract record Op(OpId Id, Hlc Clock, string ObjectId)
{
    /// <summary>How many sequence numbers this op consumes from its actor.</summary>
    [JsonIgnore]
    public virtual int Length => 1;

    [JsonIgnore]
    public string Actor => Id.Actor;

    [JsonIgnore]
    public long Seq => Id.Seq;

    /// <summary>First sequence number after this op.</summary>
    [JsonIgnore]
    public long SeqEnd => Id.Seq + Length;
}

/// <summary>
/// Inserts a run of characters after <paramref name="After"/> (null means the start of the block).
/// The k-th character gets id <c>Id.Offset(k)</c>.
/// </summary>
public sealed record TextInsert(
    OpId Id,
    Hlc Clock,
    string ObjectId,
    string BlockId,
    OpId? After,
    string Text) : Op(Id, Clock, ObjectId)
{
    [JsonIgnore]
    public override int Length => Text.Length;
}

/// <summary>Tombstones characters by id. Applying it twice, or before the insert, is harmless.</summary>
public sealed record TextDelete(
    OpId Id,
    Hlc Clock,
    string ObjectId,
    string BlockId,
    IReadOnlyList<OpId> Targets) : Op(Id, Clock, ObjectId);

/// <summary>
/// Creates or edits a block. Placement (<see cref="ParentId"/> plus <see cref="Position"/>) and
/// <see cref="Kind"/> are separate LWW registers, so a concurrent move and type change both stick;
/// a null field means "this op does not touch that register".
/// </summary>
public sealed record BlockUpsert(
    OpId Id,
    Hlc Clock,
    string ObjectId,
    string BlockId,
    string? ParentId,
    string? Position,
    BlockKind? Kind) : Op(Id, Clock, ObjectId);

/// <summary>LWW register write on an object or a block, resolved by <see cref="Op.Clock"/>.</summary>
public sealed record PropSet(
    OpId Id,
    Hlc Clock,
    string ObjectId,
    string TargetId,
    string Key,
    string? Value) : Op(Id, Clock, ObjectId);

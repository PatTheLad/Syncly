using System.Globalization;

namespace Syncly.Crdt;

/// <summary>
/// Identifies one op, and by extension one character: a text run starting at seq N with L
/// characters owns the ids N..N+L-1, which is what lets a version vector stay a single number
/// per actor.
/// </summary>
public readonly record struct OpId(string Actor, long Seq) : IComparable<OpId>
{
    public const char Separator = '~';

    public int CompareTo(OpId other)
    {
        var c = Seq.CompareTo(other.Seq);
        return c != 0 ? c : string.CompareOrdinal(Actor, other.Actor);
    }

    public static bool operator <(OpId a, OpId b) => a.CompareTo(b) < 0;

    public static bool operator >(OpId a, OpId b) => a.CompareTo(b) > 0;

    public static bool operator <=(OpId a, OpId b) => a.CompareTo(b) <= 0;

    public static bool operator >=(OpId a, OpId b) => a.CompareTo(b) >= 0;

    public OpId Offset(long delta) => new(Actor, Seq + delta);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Actor}{Separator}{Seq}");

    public static OpId Parse(string value)
    {
        var i = value.LastIndexOf(Separator);
        if (i < 0)
            throw new FormatException($"Malformed op id '{value}'.");

        return new OpId(value[..i], long.Parse(value.AsSpan(i + 1), CultureInfo.InvariantCulture));
    }
}

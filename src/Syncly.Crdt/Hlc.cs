using System.Globalization;

namespace Syncly.Crdt;

/// <summary>
/// Hybrid logical clock. Wall time keeps timestamps human-meaningful, the counter keeps them
/// strictly monotonic, and the actor id makes the order total across devices with skewed clocks.
/// </summary>
public readonly record struct Hlc(long Wall, int Counter, string Actor) : IComparable<Hlc>
{
    public static readonly Hlc Zero = new(0, 0, string.Empty);

    public int CompareTo(Hlc other)
    {
        var c = Wall.CompareTo(other.Wall);
        if (c != 0)
            return c;
        c = Counter.CompareTo(other.Counter);
        return c != 0 ? c : string.CompareOrdinal(Actor, other.Actor);
    }

    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;

    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;

    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;

    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(Wall);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Wall}.{Counter}.{Actor}");

    public static Hlc Parse(string value)
    {
        var first = value.IndexOf('.');
        var second = value.IndexOf('.', first + 1);
        if (first < 0 || second < 0)
            throw new FormatException($"Malformed HLC '{value}'.");

        return new Hlc(
            long.Parse(value.AsSpan(0, first), CultureInfo.InvariantCulture),
            int.Parse(value.AsSpan(first + 1, second - first - 1), CultureInfo.InvariantCulture),
            value[(second + 1)..]);
    }
}

/// <summary>Generates <see cref="Hlc"/> values for one actor and absorbs remote timestamps.</summary>
public sealed class HlcClock(string actor, Func<long>? nowUnixMs = null)
{
    private readonly Func<long> _now = nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly Lock _gate = new();
    private long _wall;
    private int _counter;

    public string Actor { get; } = actor;

    public Hlc Tick()
    {
        lock (_gate)
        {
            var physical = _now();
            if (physical > _wall)
            {
                _wall = physical;
                _counter = 0;
            }
            else
            {
                _counter++;
            }

            return new Hlc(_wall, _counter, Actor);
        }
    }

    /// <summary>Merges a remote timestamp so our next tick is strictly after it.</summary>
    public void Observe(Hlc remote)
    {
        lock (_gate)
        {
            var physical = _now();
            var wall = Math.Max(Math.Max(_wall, remote.Wall), physical);

            if (wall == _wall && wall == remote.Wall)
                _counter = Math.Max(_counter, remote.Counter) + 1;
            else if (wall == _wall)
                _counter++;
            else if (wall == remote.Wall)
                _counter = remote.Counter + 1;
            else
                _counter = 0;

            _wall = wall;
        }
    }
}

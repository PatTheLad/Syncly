using System.Text;

namespace Syncly.Crdt;

/// <summary>
/// Fractional indices: order keys that can always be subdivided, so moving a block is one op and
/// two devices inserting at the same slot produce different keys that still sort deterministically
/// (ties break on block id). The alphabet is ASCII-ascending, so ordinal string compare is the
/// numeric order.
/// </summary>
public static class FracIndex
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int Radix = 62;

    public static readonly string Middle = Between(null, null);

    private static int Value(char c)
    {
        var i = Digits.IndexOf(c);
        if (i < 0)
            throw new FormatException($"Invalid fractional index digit '{c}'.");

        return i;
    }

    /// <summary>Returns a key strictly between <paramref name="low"/> and <paramref name="high"/>.</summary>
    public static string Between(string? low, string? high)
    {
        if (low is { Length: 0 })
            low = null;
        if (high is { Length: 0 })
            high = null;

        // Defensive: an inverted or equal pair still has to yield something usable.
        if (low is not null && high is not null && string.CompareOrdinal(low, high) >= 0)
            high = null;

        var sb = new StringBuilder();
        var i = 0;

        while (true)
        {
            var lo = low is not null && i < low.Length ? Value(low[i]) : 0;
            var hi = high is null ? Radix : i < high.Length ? Value(high[i]) : 0;

            if (hi - lo > 1)
            {
                sb.Append(Digits[lo + ((hi - lo) / 2)]);
                return sb.ToString();
            }

            sb.Append(Digits[lo]);
            i++;

            if (hi - lo == 1)
            {
                // Below this digit the upper bound no longer constrains us.
                var tail = low is not null && low.Length > i ? low[i..] : null;
                sb.Append(Between(tail, null));
                return sb.ToString();
            }
        }
    }

    /// <summary>Evenly spaced keys for building a list in one go.</summary>
    public static string[] Sequence(int count, string? low = null, string? high = null)
    {
        var keys = new string[count];
        var cursor = low;
        for (var i = 0; i < count; i++)
        {
            cursor = Between(cursor, high);
            keys[i] = cursor;
        }

        return keys;
    }

    public static int Compare(string a, string b) => string.CompareOrdinal(a, b);
}

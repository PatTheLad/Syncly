using Syncly.Crdt;

namespace Syncly.Crdt.Tests;

public class FracIndexTests
{
    [Fact]
    public void Between_nulls_is_a_valid_key()
    {
        var key = FracIndex.Between(null, null);
        Assert.False(string.IsNullOrEmpty(key));
    }

    [Fact]
    public void Between_is_strictly_ordered()
    {
        var a = FracIndex.Between(null, null);
        var b = FracIndex.Between(a, null);
        var mid = FracIndex.Between(a, b);

        Assert.True(string.CompareOrdinal(a, mid) < 0);
        Assert.True(string.CompareOrdinal(mid, b) < 0);
    }

    [Fact]
    public void Repeated_subdivision_at_the_front_stays_ordered()
    {
        var high = FracIndex.Between(null, null);
        var seen = new List<string>();

        for (var i = 0; i < 400; i++)
        {
            high = FracIndex.Between(null, high);
            seen.Add(high);
        }

        for (var i = 1; i < seen.Count; i++)
            Assert.True(string.CompareOrdinal(seen[i], seen[i - 1]) < 0, $"not descending at {i}");
    }

    [Fact]
    public void Repeated_subdivision_in_the_middle_stays_ordered()
    {
        var low = FracIndex.Between(null, null);
        var high = FracIndex.Between(low, null);

        for (var i = 0; i < 400; i++)
        {
            var mid = FracIndex.Between(low, high);
            Assert.True(string.CompareOrdinal(low, mid) < 0);
            Assert.True(string.CompareOrdinal(mid, high) < 0);
            low = mid;
        }
    }

    [Fact]
    public void Sequence_is_ascending()
    {
        var keys = FracIndex.Sequence(50);
        for (var i = 1; i < keys.Length; i++)
            Assert.True(string.CompareOrdinal(keys[i - 1], keys[i]) < 0);
    }

    [Fact]
    public void Inverted_bounds_still_produce_a_key()
    {
        var high = FracIndex.Between(null, null);
        var low = FracIndex.Between(high, null);

        var key = FracIndex.Between(low, high);
        Assert.True(string.CompareOrdinal(low, key) < 0);
    }
}

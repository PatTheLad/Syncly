using Syncly.Crdt;

namespace Syncly.Crdt.Tests;

public class ClockTests
{
    [Fact]
    public void Ticks_are_strictly_increasing_even_with_a_frozen_wall_clock()
    {
        var clock = new HlcClock("dev", () => 42);
        var last = clock.Tick();

        for (var i = 0; i < 100; i++)
        {
            var next = clock.Tick();
            Assert.True(last < next);
            last = next;
        }
    }

    [Fact]
    public void Observing_a_future_peer_pulls_us_ahead_of_it()
    {
        var clock = new HlcClock("dev", () => 1_000);
        var remote = new Hlc(9_999, 3, "other");

        clock.Observe(remote);
        Assert.True(remote < clock.Tick());
    }

    [Fact]
    public void Order_is_total_across_actors()
    {
        var a = new Hlc(5, 0, "aaa");
        var b = new Hlc(5, 0, "bbb");

        Assert.True(a < b);
        Assert.False(b < a);
    }

    [Fact]
    public void Roundtrips_through_text()
    {
        var hlc = new Hlc(1_700_000_000_000, 7, "dev01");
        Assert.Equal(hlc, Hlc.Parse(hlc.ToString()));
    }

    [Fact]
    public void OpIds_roundtrip_and_order_by_sequence()
    {
        var id = new OpId("dev01", 12);
        Assert.Equal(id, OpId.Parse(id.ToString()));
        Assert.True(id < id.Offset(1));
    }

    [Fact]
    public void Version_vector_tracks_coverage()
    {
        var vv = new VersionVector();
        Assert.False(vv.Contains(new OpId("a", 0)));

        vv.Advance("a", 3);
        Assert.True(vv.Contains(new OpId("a", 2)));
        Assert.False(vv.Contains(new OpId("a", 3)));

        var other = new VersionVector();
        other.Advance("a", 2);
        Assert.True(vv.Covers(other));
        Assert.False(other.Covers(vv));
    }
}

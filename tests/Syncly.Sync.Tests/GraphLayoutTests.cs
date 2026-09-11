using Syncly.App;
using Syncly.Model;

namespace Syncly.Sync.Tests;

public class GraphLayoutTests
{
    [Fact]
    public void Body_kind_follows_nesting_depth()
    {
        Assert.Equal(GraphBodyKind.Star, GraphLayout.BodyFor(0));
        Assert.Equal(GraphBodyKind.Planet, GraphLayout.BodyFor(1));
        Assert.Equal(GraphBodyKind.Moon, GraphLayout.BodyFor(2));
        Assert.Equal(GraphBodyKind.Asteroid, GraphLayout.BodyFor(3));
        Assert.Equal(GraphBodyKind.Meteor, GraphLayout.BodyFor(4));
        Assert.Equal(GraphBodyKind.Dust, GraphLayout.BodyFor(1, missing: true));
    }

    [Fact]
    public void Arrange_orbits_children_around_their_parent()
    {
        var star = Node("s", "Sol", 0, GraphBodyKind.Star);
        var planet = Node("p", "Earth", 1, GraphBodyKind.Planet, "s");
        var moon = Node("m", "Luna", 2, GraphBodyKind.Moon, "p");
        var dust = Node("d", "ghost", 1, GraphBodyKind.Dust, "s", missing: true);

        var graph = GraphLayout.Arrange([star, planet, moon, dust], []);

        var sun = graph.Nodes.Single(n => n.Id == "s");
        var earth = graph.Nodes.Single(n => n.Id == "p");
        var luna = graph.Nodes.Single(n => n.Id == "m");
        var ghost = graph.Nodes.Single(n => n.Id == "d");

        Assert.True(Dist(earth, luna) < Dist(sun, luna));
        Assert.True(Dist(sun, ghost) > Dist(sun, earth) * 0.5);
        Assert.Contains(graph.Orbits, o => o.Radius > 0);
        Assert.InRange(sun.X, 0.05, 0.95);
        Assert.InRange(earth.Y, 0.05, 0.95);
    }

    [Fact]
    public void Stars_are_larger_than_planets()
    {
        Assert.True(GraphLayout.SizeFor(GraphBodyKind.Star) > GraphLayout.SizeFor(GraphBodyKind.Planet));
        Assert.True(GraphLayout.SizeFor(GraphBodyKind.Planet) > GraphLayout.SizeFor(GraphBodyKind.Moon));
        Assert.True(GraphLayout.SizeFor(GraphBodyKind.Moon) > GraphLayout.SizeFor(GraphBodyKind.Meteor));
    }

    private static GraphNode Node(
        string id,
        string title,
        int depth,
        GraphBodyKind body,
        string? parentId = null,
        bool missing = false) =>
        new(id, title, null, 0, 0, false, missing, depth, GraphLayout.SizeFor(body), parentId, body);

    private static double Dist(GraphNode a, GraphNode b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

using Syncly.Model;

namespace Syncly.App;

/// <summary>
/// Nested solar-system layout: root pages are stars, children planets, then moons, asteroids,
/// meteors. Sibling systems sit in a galaxy ring or spiral.
/// </summary>
public static class GraphLayout
{
    public static GraphBodyKind BodyFor(int depth, bool missing = false) =>
        missing ? GraphBodyKind.Dust : depth switch
        {
            <= 0 => GraphBodyKind.Star,
            1 => GraphBodyKind.Planet,
            2 => GraphBodyKind.Moon,
            3 => GraphBodyKind.Asteroid,
            _ => GraphBodyKind.Meteor,
        };

    public static double SizeFor(GraphBodyKind body) => body switch
    {
        GraphBodyKind.Star => 0.034,
        GraphBodyKind.Planet => 0.017,
        GraphBodyKind.Moon => 0.01,
        GraphBodyKind.Asteroid => 0.0065,
        GraphBodyKind.Meteor => 0.0045,
        GraphBodyKind.Dust => 0.007,
        _ => 0.01,
    };

    public static PageGraph Arrange(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        if (nodes.Count == 0)
            return new PageGraph([], edges, []);

        var sized = nodes
            .Select(n =>
            {
                var body = n.Missing ? GraphBodyKind.Dust : n.Body;
                return n with { Body = body, Size = SizeFor(body) };
            })
            .ToList();

        var bodies = sized.ToDictionary(n => n.Id, n => new Body(n), StringComparer.Ordinal);
        foreach (var node in sized)
        {
            if (node.ParentId is not { Length: > 0 } pid
                || pid == node.Id
                || !bodies.TryGetValue(pid, out var parent))
                continue;
            parent.Children.Add(bodies[node.Id]);
        }

        var nested = new HashSet<string>(
            bodies.Values.SelectMany(b => b.Children.Select(c => c.Node.Id)),
            StringComparer.Ordinal);
        var roots = bodies.Values.Where(b => !nested.Contains(b.Node.Id)).ToList();
        if (roots.Count == 0)
            roots = [.. bodies.Values];

        foreach (var root in roots)
            Measure(root);

        PlaceGalaxy(roots);

        var orbits = new List<GraphOrbit>();
        foreach (var body in bodies.Values)
        {
            AddOrbit(orbits, body, body.Real());
            AddOrbit(orbits, body, body.Dust());
        }

        Normalize(bodies.Values, orbits);

        var laid = sized
            .Select(n =>
            {
                var b = bodies[n.Id];
                return n with { X = b.X, Y = b.Y };
            })
            .ToList();

        return new PageGraph(laid, edges, orbits);
    }

    private static void Measure(Body body)
    {
        foreach (var child in body.Children)
            Measure(child);

        var real = body.Real();
        var dust = body.Dust();
        var inner = OrbitRadius(body.Node.Size, body.Node.Depth, real);
        var outer = dust.Count == 0
            ? inner
            : OuterDustOrbit(body, inner, dust);

        var extent = body.Node.Size;
        if (real.Count > 0)
            extent = Math.Max(extent, inner + real.Max(c => c.Radius));
        if (dust.Count > 0)
            extent = Math.Max(extent, outer + dust.Max(c => c.Radius));
        body.Radius = extent;
        body.InnerOrbit = inner;
        body.OuterOrbit = outer;
    }

    private static void PlaceGalaxy(List<Body> roots)
    {
        if (roots.Count == 1)
        {
            Place(roots[0], 0, 0, Spin(roots[0].Node.Id));
            return;
        }

        if (roots.Count <= 10)
        {
            var ring = Math.Max(
                roots.Max(r => r.Radius) * 1.15,
                roots.Sum(r => r.Radius * 2) / (2 * Math.PI) * 1.4);
            for (var i = 0; i < roots.Count; i++)
            {
                var ang = 2 * Math.PI * i / roots.Count - Math.PI / 2;
                Place(roots[i], ring * Math.Cos(ang), ring * Math.Sin(ang), ang + Math.PI);
            }

            return;
        }

        var golden = Math.PI * (3 - Math.Sqrt(5));
        var spread = roots.Max(r => r.Radius) * 2.5;
        for (var i = 0; i < roots.Count; i++)
        {
            var r = spread * Math.Sqrt(i + 0.45);
            var ang = i * golden;
            Place(roots[i], r * Math.Cos(ang), r * Math.Sin(ang), Spin(roots[i].Node.Id));
        }
    }

    private static void Place(Body body, double x, double y, double spin)
    {
        body.X = x;
        body.Y = y;
        PlaceRing(body.Real(), x, y, body.InnerOrbit, spin);
        if (body.Dust().Count > 0)
            PlaceRing(body.Dust(), x, y, body.OuterOrbit, spin + 0.45);
    }

    private static void PlaceRing(List<Body> kids, double x, double y, double orbit, double spin)
    {
        var n = kids.Count;
        if (n == 0)
            return;

        for (var i = 0; i < n; i++)
        {
            var ang = spin + 2 * Math.PI * i / n - Math.PI / 2;
            Place(kids[i], x + orbit * Math.Cos(ang), y + orbit * Math.Sin(ang), ang + 0.85);
        }
    }

    private static double OrbitRadius(double parentSize, int depth, List<Body> kids)
    {
        if (kids.Count == 0)
            return parentSize;

        var maxR = kids.Max(c => c.Radius);
        var orbit = parentSize + Pad(depth) + maxR;
        if (kids.Count > 1)
        {
            var sin = Math.Sin(Math.PI / kids.Count);
            if (sin > 1e-6)
                orbit = Math.Max(orbit, maxR * 1.28 / sin);
        }

        return orbit;
    }

    private static double OuterDustOrbit(Body body, double inner, List<Body> dust)
    {
        var dustOrbit = OrbitRadius(body.Node.Size, body.Node.Depth + 1, dust);
        if (body.Real().Count == 0)
            return dustOrbit;

        return Math.Max(dustOrbit, inner + Pad(body.Node.Depth) + dust.Max(c => c.Radius));
    }

    private static void AddOrbit(List<GraphOrbit> orbits, Body parent, List<Body> kids)
    {
        if (kids.Count == 0)
            return;

        var radius = Dist(parent, kids[0]);
        if (radius < 1e-4)
            return;

        orbits.Add(new GraphOrbit(parent.X, parent.Y, radius, parent.Node.Depth));
    }

    private static void Normalize(IEnumerable<Body> bodies, List<GraphOrbit> orbits)
    {
        var list = bodies as IList<Body> ?? bodies.ToList();
        if (list.Count == 0)
            return;

        var minX = list.Min(b => b.X - b.Radius);
        var maxX = list.Max(b => b.X + b.Radius);
        var minY = list.Min(b => b.Y - b.Radius);
        var maxY = list.Max(b => b.Y + b.Radius);
        var span = Math.Max(Math.Max(maxX - minX, maxY - minY), 0.02);
        const double pad = 0.07;
        var scale = (1 - 2 * pad) / span;
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;

        foreach (var body in list)
        {
            body.X = 0.5 + (body.X - cx) * scale;
            body.Y = 0.5 + (body.Y - cy) * scale;
        }

        for (var i = 0; i < orbits.Count; i++)
        {
            var o = orbits[i];
            orbits[i] = o with
            {
                X = 0.5 + (o.X - cx) * scale,
                Y = 0.5 + (o.Y - cy) * scale,
                Radius = o.Radius * scale,
            };
        }
    }

    private static double Pad(int depth) => depth switch
    {
        <= 0 => 0.05,
        1 => 0.026,
        2 => 0.015,
        3 => 0.01,
        _ => 0.008,
    };

    private static double Dist(Body a, Body b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Spin(string id) => StableHash(id) / (double)uint.MaxValue * Math.PI * 2;

    public static uint StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in value)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    private sealed class Body(GraphNode node)
    {
        public GraphNode Node { get; } = node;
        public List<Body> Children { get; } = [];
        public double X;
        public double Y;
        public double Radius;
        public double InnerOrbit;
        public double OuterOrbit;

        public List<Body> Real() => [.. Children.Where(c => !c.Node.Missing)];
        public List<Body> Dust() => [.. Children.Where(c => c.Node.Missing)];
    }
}

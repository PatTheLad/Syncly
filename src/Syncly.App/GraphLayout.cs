using Syncly.Model;

namespace Syncly.App;

/// <summary>Deterministic layout for the page graph: a ring, then a few repulsion/spring steps.</summary>
public static class GraphLayout
{
    public static PageGraph Arrange(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges)
    {
        if (nodes.Count == 0)
            return new PageGraph([], edges);

        var points = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        if (nodes.Count == 1)
        {
            points[nodes[0].Id] = (0.5, 0.5);
        }
        else
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var angle = 2 * Math.PI * i / nodes.Count - Math.PI / 2;
                points[nodes[i].Id] = (0.5 + 0.36 * Math.Cos(angle), 0.5 + 0.36 * Math.Sin(angle));
            }

            Relax(points, edges, nodes.Count);
        }

        var laid = nodes
            .Select(n =>
            {
                var (x, y) = points[n.Id];
                return n with { X = x, Y = y };
            })
            .ToList();

        return new PageGraph(laid, edges);
    }

    private static void Relax(
        Dictionary<string, (double X, double Y)> points,
        IReadOnlyList<GraphEdge> edges,
        int count)
    {
        var ids = points.Keys.ToList();
        var steps = Math.Min(80, 20 + count * 2);
        for (var step = 0; step < steps; step++)
        {
            var force = ids.ToDictionary(id => id, _ => (X: 0.0, Y: 0.0), StringComparer.Ordinal);

            for (var i = 0; i < ids.Count; i++)
            for (var j = i + 1; j < ids.Count; j++)
            {
                var a = points[ids[i]];
                var b = points[ids[j]];
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var dist = Math.Max(0.02, Math.Sqrt(dx * dx + dy * dy));
                var push = 0.0025 / (dist * dist);
                var fx = dx / dist * push;
                var fy = dy / dist * push;
                force[ids[i]] = (force[ids[i]].X + fx, force[ids[i]].Y + fy);
                force[ids[j]] = (force[ids[j]].X - fx, force[ids[j]].Y - fy);
            }

            foreach (var edge in edges)
            {
                if (!points.TryGetValue(edge.From, out var a) || !points.TryGetValue(edge.To, out var b))
                    continue;

                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var dist = Math.Max(0.01, Math.Sqrt(dx * dx + dy * dy));
                var pull = (dist - 0.22) * 0.08;
                var fx = dx / dist * pull;
                var fy = dy / dist * pull;
                force[edge.From] = (force[edge.From].X + fx, force[edge.From].Y + fy);
                force[edge.To] = (force[edge.To].X - fx, force[edge.To].Y - fy);
            }

            foreach (var id in ids)
            {
                var p = points[id];
                var f = force[id];
                points[id] = (
                    Clamp(p.X + f.X * 0.6, 0.08, 0.92),
                    Clamp(p.Y + f.Y * 0.6, 0.08, 0.92));
            }
        }
    }

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;
}

namespace Syncly.Model;

public sealed record SearchHit(string ObjectId, string Title, string BlockId, string Snippet);

public sealed record Backlink(string ObjectId, string Title, string BlockId, string Text);

public sealed record GraphLink(string SourceId, string? TargetId, string TargetKey);

public enum GraphBodyKind
{
    Star,
    Planet,
    Moon,
    Asteroid,
    Meteor,
    Dust,
}

public enum GraphEdgeKind
{
    Parent,
    Link,
}

public sealed record GraphNode(
    string Id,
    string Title,
    string? Icon,
    double X,
    double Y,
    bool Current,
    bool Missing,
    int Depth = 0,
    double Size = 0.018,
    string? ParentId = null,
    GraphBodyKind Body = GraphBodyKind.Star);

public sealed record GraphEdge(string From, string To, GraphEdgeKind Kind = GraphEdgeKind.Link);

public sealed record GraphOrbit(double X, double Y, double Radius, int Depth);

public sealed record PageGraph(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    IReadOnlyList<GraphOrbit> Orbits);

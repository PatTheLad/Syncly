namespace Syncly.Model;

public sealed record SearchHit(string ObjectId, string Title, string BlockId, string Snippet);

public sealed record Backlink(string ObjectId, string Title, string BlockId, string Text);

public sealed record GraphLink(string SourceId, string? TargetId, string TargetKey);

public sealed record GraphNode(
    string Id,
    string Title,
    string? Icon,
    double X,
    double Y,
    bool Current,
    bool Missing);

public sealed record GraphEdge(string From, string To);

public sealed record PageGraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);


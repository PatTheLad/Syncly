namespace Syncly.Model;

public sealed record SearchHit(string ObjectId, string Title, string BlockId, string Snippet);

public sealed record Backlink(string ObjectId, string Title, string BlockId, string Text);

public sealed record GraphLink(string SourceId, string? TargetId, string TargetKey);

public enum GraphEdgeKind
{
    Parent,
    Link,
}

public sealed record GraphNode(
    string Id,
    string Title,
    string? Icon,
    bool Missing,
    int Depth = 0,
    string? ParentId = null,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset UpdatedAt = default,
    int Inbound = 0,
    int Outbound = 0);

public sealed record GraphEdge(string From, string To, GraphEdgeKind Kind = GraphEdgeKind.Link);

public sealed record PageGraph(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

public sealed record GraphPreview(
    string Id,
    string Title,
    string? Icon,
    IReadOnlyList<string> Path,
    IReadOnlyList<string> Lines,
    int ChildCount,
    int LinkCount,
    DateTimeOffset UpdatedAt);

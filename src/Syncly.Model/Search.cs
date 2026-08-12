namespace Syncly.Model;

public sealed record SearchHit(string ObjectId, string Title, string BlockId, string Snippet);

public sealed record Backlink(string ObjectId, string Title, string BlockId, string Text);

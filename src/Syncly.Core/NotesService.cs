using Syncly.Contracts.Abstractions;
using Syncly.Contracts.Models;

namespace Syncly.Core;

public sealed class NotesService
{
    private readonly INoteStore _store;

    public NotesService(INoteStore store) => _store = store;

    public Task<IReadOnlyList<Note>> ListAsync(CancellationToken ct = default) => _store.ListAsync(ct);

    public Task<Note?> GetAsync(string id, CancellationToken ct = default) => _store.GetAsync(id, ct);

    public async Task<IReadOnlyList<Note>> SearchAsync(string? query, CancellationToken ct = default)
    {
        var all = await _store.ListAsync(ct);
        if (string.IsNullOrWhiteSpace(query))
            return all;

        var q = query.Trim();
        return all
            .Where(n =>
                n.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                n.Body.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task<Note> CreateAsync(
        string? title = null,
        string? body = null,
        string? parentId = null,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var note = new Note
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim(),
            Body = body ?? string.Empty,
            ParentId = parentId,
            CreatedAt = now,
            UpdatedAt = now
        };
        return await _store.UpsertAsync(note, ct);
    }

    public async Task<Note> UpdateAsync(
        string id,
        string title,
        string body,
        string? parentId = null,
        bool updateParent = false,
        CancellationToken ct = default)
    {
        var existing = await _store.GetAsync(id, ct) ?? throw new InvalidOperationException("Note not found.");
        existing.Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        existing.Body = body;
        if (updateParent)
            existing.ParentId = parentId;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        return await _store.UpsertAsync(existing, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct = default) => _store.DeleteAsync(id, ct);
}

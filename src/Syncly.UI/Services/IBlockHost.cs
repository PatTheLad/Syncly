namespace Syncly.UI.Services;

/// <summary>
/// What a block can ask the editor to do. Keeping this as one interface rather than a dozen
/// <c>EventCallback</c> parameters keeps the block component readable and the editor in charge.
/// </summary>
public interface IBlockHost
{
    bool Reading { get; }

    /// <summary>The text block currently showing source markers. Null means every block is preview.</summary>
    string? FocusedBlockId { get; }

    Task TextChangedAsync(string blockId, string text, int caret);

    Task CommitTextAsync(string blockId, string text);

    Task SplitAsync(string blockId, string text, int caret);

    Task MergeBackwardAsync(string blockId);

    Task MergeForwardAsync(string blockId);

    Task IndentAsync(string blockId, bool indent);

    Task MoveAsync(string blockId, bool up);

    Task StepAsync(string blockId, bool down, int caret);

    Task MarkAsync(string blockId, string shortcut, string text, int start, int end);

    Task SlashAsync(string blockId);

    Task MenuKeyAsync(string key);

    Task EscapeAsync();

    Task ToggleTodoAsync(string blockId);

    /// <summary>Opens another page from a page-link block.</summary>
    Task OpenPageAsync(string pageId);

    /// <summary>Decrypts an attachment and returns a blob: object URL for inline preview.</summary>
    Task<string?> ResolveFileObjectUrlAsync(string fileId, string mime);

    /// <summary>True when the encrypted blob is already on disk (no decrypt).</summary>
    bool HasAttachedFile(string fileId);

    /// <summary>Writes the attachment to a temp path and opens it with the OS.</summary>
    Task OpenAttachedFileAsync(string blockId);

    /// <summary>Next paste/input attach is inserted after this block (null = end of page).</summary>
    Task PrepareAttachAfterAsync(string? blockId);

    /// <summary>Leaves reading mode with the caret in this block.</summary>
    Task EditAtAsync(string blockId);

    Task FocusAsync(string blockId);

    Task BlurAsync(string blockId, string text);

    Task PickCodeLanguageAsync(string blockId);

    string? ResolveLink(string title);
}

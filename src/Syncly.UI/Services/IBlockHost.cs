namespace Syncly.UI.Services;

/// <summary>
/// What a block can ask the editor to do. Keeping this as one interface rather than a dozen
/// <c>EventCallback</c> parameters keeps the block component readable and the editor in charge.
/// </summary>
public interface IBlockHost
{
    bool Reading { get; }

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

    /// <summary>Leaves reading mode with the caret in this block.</summary>
    Task EditAtAsync(string blockId);

    Task FocusAsync(string blockId);

    Task BlurAsync(string blockId, string text);

    string? ResolveLink(string title);
}

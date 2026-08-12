using Syncly.App;
using Syncly.Model;

namespace Syncly.UI.Services;

/// <summary>
/// UI-only state: which page is open, whether we are reading or editing, and which overlay is up.
/// Nothing here touches the CRDT; that is the workspace's job.
/// </summary>
public sealed class EditorState(SynclyApp app)
{
    private readonly List<string> _history = [];

    public SynclyApp App { get; } = app;

    public Workspace Workspace => App.Workspace;

    public string? CurrentPageId { get; private set; }

    /// <summary>Reading mode keeps a page read-only until you deliberately edit it.</summary>
    public bool ReadingMode { get; private set; } = true;

    public bool PaletteOpen { get; private set; }

    public bool SidebarOpen { get; private set; } = true;

    public string? FocusBlockId { get; private set; }

    public int FocusCaret { get; private set; }

    public event Action? Changed;

    public void Open(string pageId)
    {
        if (CurrentPageId == pageId)
            return;

        if (CurrentPageId is { } previous)
            _history.Add(previous);

        CurrentPageId = pageId;
        ReadingMode = true;
        FocusBlockId = null;
        Changed?.Invoke();
    }

    public bool CanGoBack => _history.Count > 0;

    public void Back()
    {
        if (_history.Count == 0)
            return;

        CurrentPageId = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Changed?.Invoke();
    }

    public void SetReadingMode(bool reading)
    {
        if (ReadingMode == reading)
            return;

        ReadingMode = reading;
        Changed?.Invoke();
    }

    public void ToggleReadingMode() => SetReadingMode(!ReadingMode);

    public void TogglePalette(bool? open = null)
    {
        PaletteOpen = open ?? !PaletteOpen;
        Changed?.Invoke();
    }

    public void ToggleSidebar()
    {
        SidebarOpen = !SidebarOpen;
        Changed?.Invoke();
    }

    /// <summary>Asks the editor to put the caret in a specific block after the next render.</summary>
    public void RequestFocus(string blockId, int caret = 0)
    {
        FocusBlockId = blockId;
        FocusCaret = caret;
        ReadingMode = false;
        Changed?.Invoke();
    }

    public void ClearFocusRequest() => FocusBlockId = null;

    public IReadOnlyList<PageRef> Roots => Workspace.ChildrenOf(null);
}

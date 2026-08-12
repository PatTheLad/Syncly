using Microsoft.AspNetCore.Components;
using Syncly.Core;

namespace Syncly.UI.Services;

public sealed class AppState : IDisposable
{
    private readonly SyncHostService _host;
    private readonly InteractivePairingPrompter _pairing;

    public AppState(SyncHostService host, InteractivePairingPrompter pairing)
    {
        _host = host;
        _pairing = pairing;
        _host.StateChanged += OnChanged;
        _pairing.PairingRequested += OnChanged;
    }

    public SyncHostService Host => _host;
    public InteractivePairingPrompter Pairing => _pairing;
    public event Action? Changed;

    private void OnChanged(object? sender, EventArgs e) => Changed?.Invoke();

    public void Dispose()
    {
        _host.StateChanged -= OnChanged;
        _pairing.PairingRequested -= OnChanged;
    }
}

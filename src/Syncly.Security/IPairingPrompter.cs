using Syncly.Model;

namespace Syncly.Security;

/// <summary>
/// Asked once per unknown device, after the handshake has already proven key possession. The user
/// only has to confirm that the six digits match on both screens.
/// </summary>
public interface IPairingPrompter
{
    Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct = default);
}

/// <summary>Refuses every unknown device; the default when no UI is attached.</summary>
public sealed class DenyUnknownDevices : IPairingPrompter
{
    public Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct = default) =>
        Task.FromResult(false);
}

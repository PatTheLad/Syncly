using Syncly.Model;
using Syncly.Security;

namespace Syncly.App;

/// <summary>
/// Bridges the sync engine's "is this device you?" question to the UI. The engine blocks on the
/// answer, so an unknown device gets nothing until someone confirms the six digits match.
/// </summary>
public sealed class PairingBroker : IPairingPrompter
{
    private readonly Lock _gate = new();
    private TaskCompletionSource<bool>? _answer;

    public PairingRequest? Pending { get; private set; }

    public event Action? Changed;

    public Task<bool> ConfirmAsync(PairingRequest request, CancellationToken ct = default)
    {
        TaskCompletionSource<bool> answer;

        lock (_gate)
        {
            // One prompt at a time; a second device has to wait its turn rather than stack dialogs.
            if (Pending is not null)
                return Task.FromResult(false);

            answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _answer = answer;
            Pending = request;
        }

        Changed?.Invoke();
        ct.Register(() => Resolve(false));
        return answer.Task;
    }

    public void Approve() => Resolve(true);

    public void Reject() => Resolve(false);

    private void Resolve(bool approved)
    {
        TaskCompletionSource<bool>? answer;

        lock (_gate)
        {
            answer = _answer;
            _answer = null;
            Pending = null;
        }

        answer?.TrySetResult(approved);
        Changed?.Invoke();
    }
}

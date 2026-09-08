using Microsoft.JSInterop;
using Syncly.App;

namespace Syncly.UI.Services;

/// <summary>Uses the Blazor <c>syncly.scanQr</c> helper.</summary>
public sealed class JsQrScanner(IJSRuntime js) : IQrScanner
{
    public async Task<string?> ScanAsync(CancellationToken ct = default)
    {
        _ = ct;
        return await js.InvokeAsync<string?>("syncly.scanQr");
    }
}

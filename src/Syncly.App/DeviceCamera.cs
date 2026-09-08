namespace Syncly.App;

/// <summary>Requests the camera before the webview or native scanner opens it.</summary>
public interface IDeviceCamera
{
    Task<bool> EnsurePermissionAsync(CancellationToken ct = default);
}

public sealed class AlwaysAllowedCamera : IDeviceCamera
{
    public Task<bool> EnsurePermissionAsync(CancellationToken ct = default) =>
        Task.FromResult(true);
}

/// <summary>Opens a QR scanner and returns the raw payload, or null if cancelled / unavailable.</summary>
public interface IQrScanner
{
    Task<string?> ScanAsync(CancellationToken ct = default);
}

public sealed class UnsupportedQrScanner : IQrScanner
{
    public Task<string?> ScanAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

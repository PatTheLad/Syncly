namespace Syncly.UI.Services;

public sealed record PickedFile(string FileName, string? MimeType, Stream Content) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync();
    }
}

/// <summary>Host file picker and open-with-OS for attachments.</summary>
public interface IFileAccess
{
    Task<PickedFile?> PickAsync(CancellationToken ct = default);

    Task OpenAsync(string fileName, string mime, byte[] bytes, CancellationToken ct = default);
}

public sealed class UnsupportedFileAccess : IFileAccess
{
    public Task<PickedFile?> PickAsync(CancellationToken ct = default) =>
        Task.FromResult<PickedFile?>(null);

    public Task OpenAsync(string fileName, string mime, byte[] bytes, CancellationToken ct = default) =>
        Task.FromException(new InvalidOperationException("Opening files is not supported here."));
}

using Syncly.UI.Services;

namespace Syncly.Mobile;

internal sealed class MauiFileAccess : IFileAccess
{
    public async Task<PickedFile?> PickAsync(CancellationToken ct = default)
    {
        var result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Attach a file",
        });

        if (result is null)
            return null;

        var stream = await result.OpenReadAsync();
        return new PickedFile(result.FileName, result.ContentType, stream);
    }

    public async Task OpenAsync(string fileName, string mime, byte[] bytes, CancellationToken ct = default)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "attachment";

        var path = Path.Combine(FileSystem.CacheDirectory, "syncly-open", Guid.NewGuid().ToString("N"), safe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);

        await Launcher.Default.OpenAsync(new OpenFileRequest(safe, new ReadOnlyFile(path)));
    }
}

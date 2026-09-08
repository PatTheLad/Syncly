using System.Diagnostics;
using Syncly.UI.Services;

namespace Syncly.Desktop;

internal sealed class DesktopFileAccess : IFileAccess
{
    public Task<PickedFile?> PickAsync(CancellationToken ct = default)
    {
        // Photino has no native file dialog API; InputFile in the UI is the primary path.
        // This host method remains for OpenAsync and future native dialogs.
        return Task.FromResult<PickedFile?>(null);
    }

    public async Task OpenAsync(string fileName, string mime, byte[] bytes, CancellationToken ct = default)
    {
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe))
            safe = "attachment";

        var path = Path.Combine(Path.GetTempPath(), "syncly-open", Guid.NewGuid().ToString("N"), safe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}

namespace Syncly.Sync;

/// <summary>
/// A shared mailbox: a folder on disk, a network share, or a cloud provider. Syncly only ever
/// writes opaque blobs here; the backend does not need to understand them.
/// </summary>
public interface ISyncBackend
{
    string Name { get; }

    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);

    Task<byte[]?> ReadAsync(string name, CancellationToken ct = default);

    Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct = default);

    Task TestAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>A directory you pick: USB stick, NAS mount, or any folder another device can also see.</summary>
public sealed class LocalFolderBackend : ISyncBackend
{
    public LocalFolderBackend(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Choose a folder to sync through.", nameof(path));

        DirectoryPath = Path.GetFullPath(path);
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string Name => "Local folder";

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        var names = Directory.Exists(DirectoryPath)
            ? Directory.GetFiles(DirectoryPath).Select(Path.GetFileName).OfType<string>().ToList()
            : [];

        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    public Task<byte[]?> ReadAsync(string name, CancellationToken ct = default)
    {
        var path = SafePath(name);
        return Task.FromResult(File.Exists(path) ? File.ReadAllBytes(path) : null);
    }

    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        var path = SafePath(name);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, data.ToArray(), ct);
        File.Move(temp, path, overwrite: true);
    }

    public Task TestAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var probe = Path.Combine(DirectoryPath, ".syncly-write-test");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        return Task.CompletedTask;
    }

    private string SafePath(string name)
    {
        var file = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(file) || file != name)
            throw new InvalidOperationException("Invalid mailbox file name.");

        return Path.Combine(DirectoryPath, file);
    }
}

public static class MailboxFiles
{
    public const string ChainManifest = "chain.json";
    public const string PackExtension = ".syncly";

    public static string PackName(string deviceId) => deviceId + PackExtension;

    public static bool IsPack(string name) =>
        name.EndsWith(PackExtension, StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith('.');

    public static string? DeviceIdOf(string name)
    {
        if (!IsPack(name))
            return null;

        return name[..^PackExtension.Length];
    }
}

public sealed class ChainMismatchException(string message) : Exception(message);

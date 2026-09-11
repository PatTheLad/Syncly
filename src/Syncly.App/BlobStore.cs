using Syncly.Security;

namespace Syncly.App;

/// <summary>
/// Encrypted file payloads on disk. Mailbox sync copies the sealed bytes as <c>b-{id}.syncly</c>;
/// the CRDT only stores metadata on File blocks.
/// </summary>
public sealed class BlobStore(string dataDirectory)
{
    public const long MaxBytes = 25L * 1024 * 1024;

    public string DirectoryPath { get; } = Path.Combine(dataDirectory, "blobs");

    public string PathFor(string fileId)
    {
        var safe = Path.GetFileName(fileId);
        if (string.IsNullOrWhiteSpace(safe) || safe != fileId)
            throw new InvalidOperationException("Invalid file id.");

        return Path.Combine(DirectoryPath, safe + ".sly");
    }

    public bool Has(string fileId) => File.Exists(PathFor(fileId));

    public IReadOnlyList<string> ListIds()
    {
        if (!Directory.Exists(DirectoryPath))
            return [];

        return Directory.GetFiles(DirectoryPath, "*.sly")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Where(id => id.Length > 0)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public async Task PutAsync(
        string fileId,
        Stream plaintext,
        SyncChain chain,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(DirectoryPath);

        await using var buffer = new MemoryStream();
        await plaintext.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxBytes)
            throw new InvalidOperationException(
                $"Files larger than {MaxBytes / (1024 * 1024)} MB cannot be attached yet.");

        var sealedBytes = BlobCipher.Encrypt(chain, buffer.ToArray());
        var path = PathFor(fileId);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, sealedBytes, ct);
        File.Move(temp, path, overwrite: true);
        LastPutBytes = buffer.Length;
    }

    /// <summary>Byte length of the last successful <see cref="PutAsync"/> plaintext.</summary>
    public long LastPutBytes { get; private set; }

    public async Task PutSealedAsync(string fileId, byte[] sealedBytes, CancellationToken ct = default)
    {
        if (!BlobCipher.LooksLikePack(sealedBytes))
            throw new InvalidOperationException("Mailbox blob is not a Syncly sealed file.");

        Directory.CreateDirectory(DirectoryPath);
        var path = PathFor(fileId);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, sealedBytes, ct);
        File.Move(temp, path, overwrite: true);
    }

    public bool Delete(string fileId)
    {
        var path = PathFor(fileId);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    /// <summary>Drops local blobs that no current File block points at.</summary>
    public int DeleteUnreferenced(IEnumerable<string> keepIds)
    {
        var keep = keepIds.ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var id in ListIds())
        {
            if (keep.Contains(id))
                continue;

            if (Delete(id))
                removed++;
        }

        return removed;
    }

    public async Task<byte[]?> ReadSealedAsync(string fileId, CancellationToken ct = default)
    {
        var path = PathFor(fileId);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public async Task<byte[]?> TryGetPlainAsync(string fileId, SyncChain chain, CancellationToken ct = default)
    {
        var sealedBytes = await ReadSealedAsync(fileId, ct);
        if (sealedBytes is null)
            return null;

        return BlobCipher.Decrypt(chain, sealedBytes);
    }
}

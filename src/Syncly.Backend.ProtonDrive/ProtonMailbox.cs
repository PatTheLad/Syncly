using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// After SRP, the share private key unlocks folder names and file contents. Syncly only stores
/// its own ciphertext blobs in that folder.
/// </summary>
internal sealed class ProtonMailbox
{
    private readonly PgpPrivateKey _shareKey;
    private readonly PgpPublicKey _sharePublic;
    private HttpClient? _http;
    private string _token = "";
    private string _volumeId = "";
    private string _rootLinkId = "";
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);

    private ProtonMailbox(PgpPrivateKey shareKey, PgpPublicKey sharePublic)
    {
        _shareKey = shareKey;
        _sharePublic = sharePublic;
    }

    public static ProtonMailbox Unlock(ProtonShareDto share, string urlPassword)
    {
        if (string.IsNullOrWhiteSpace(share.SharePassphrase) || string.IsNullOrWhiteSpace(share.ShareKey))
            throw new ProtonDriveException("Proton Drive did not return a share key for this link.");

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(share.SharePasswordSalt);
        }
        catch (FormatException ex)
        {
            throw new ProtonDriveException("Proton Drive share salt is not valid.", ex);
        }

        var password = Encoding.UTF8.GetBytes(urlPassword);
        var candidates = KeyPasswordCandidates(password, salt);

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var passphrase = ProtonPgp.DecryptWithPassword(share.SharePassphrase, candidate);
                var (privateKey, publicKey) = ProtonPgp.UnlockPrivateKey(share.ShareKey, passphrase);
                return new ProtonMailbox(privateKey, publicKey);
            }
            catch (Exception ex) when (ex is PgpException or ProtonDriveException or InvalidOperationException)
            {
                last = ex;
            }
        }

        throw new ProtonDriveException(
            "Could not decrypt the Proton Drive share passphrase. Check that the link still has Editor access.",
            last);
    }

    internal static IEnumerable<byte[]> KeyPasswordCandidates(byte[] password, byte[] salt)
    {
        yield return ProtonSrp.DeriveKeyPassphrase(password, salt);

        var salt16 = salt.Length == 16 ? salt : ProtonSrp.BcryptSalt16(salt);
        var full = Org.BouncyCastle.Crypto.Generators.OpenBsdBCrypt.Generate(
            "2y", Encoding.Latin1.GetChars(password), salt16, 10);
        yield return Encoding.ASCII.GetBytes(full);
        yield return password;
    }

    public void Bind(HttpClient http, string token, string uid, string accessToken, string volumeId, string linkId)
    {
        _http = http;
        _token = token;
        _volumeId = volumeId;
        _rootLinkId = linkId;
        _ = uid;
        _ = accessToken;
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        await RefreshAsync(ct);
        return _links.Keys.ToList();
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken ct)
    {
        await RefreshAsync(ct);
        if (!_links.TryGetValue(name, out var linkId))
            return null;

        var http = RequireHttp();
        var link = await http.GetFromJsonAsync<JsonElement>(
            $"v2/volumes/{_volumeId}/links/{linkId}", cancellationToken: ct);

        if (!TryGetLink(link, out var node))
            return null;

        if (node.TryGetProperty("FileProperties", out var file)
            && file.TryGetProperty("ActiveRevision", out var revision)
            && revision.TryGetProperty("Blocks", out var blocks)
            && blocks.ValueKind == JsonValueKind.Array)
        {
            using var buffer = new MemoryStream();
            foreach (var block in blocks.EnumerateArray())
            {
                if (!block.TryGetProperty("BareUrl", out var urlEl)
                    && !block.TryGetProperty("URL", out urlEl)
                    && !block.TryGetProperty("Url", out urlEl))
                    continue;

                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var raw = await http.GetByteArrayAsync(url, ct);
                var decrypted = DecryptBlock(node, raw);
                await buffer.WriteAsync(decrypted, ct);
            }

            return buffer.ToArray();
        }

        // Some public shares put a single encrypted payload on the link itself.
        if (node.TryGetProperty("Name", out _))
        {
            var fallback = await http.GetAsync($"urls/{_token}/files/{linkId}", ct);
            if (fallback.IsSuccessStatusCode)
            {
                var raw = await fallback.Content.ReadAsByteArrayAsync(ct);
                return DecryptBlock(node, raw);
            }
        }

        throw new ProtonDriveException($"Could not download “{name}” from Proton Drive.");
    }

    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await RefreshAsync(ct);
        var http = RequireHttp();

        if (_links.TryGetValue(name, out var existing))
        {
            await UploadRevisionAsync(existing, name, data, ct);
            return;
        }

        var encryptedName = ProtonPgp.EncryptToKey(Encoding.UTF8.GetBytes(name), _sharePublic);
        var body = new Dictionary<string, object?>
        {
            ["Name"] = encryptedName,
            ["Hash"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))),
            ["ParentLinkID"] = _rootLinkId,
            ["MIMEType"] = "application/octet-stream",
            ["Content"] = Convert.ToBase64String(EncryptPayload(data.ToArray())),
        };

        using var response = await http.PostAsJsonAsync(
            $"v2/volumes/{_volumeId}/folders/{_rootLinkId}/files", body, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Older public-link route used by the web client.
            using var retry = await http.PostAsJsonAsync($"urls/{_token}/files", new
            {
                Name = name,
                MIMEType = "application/octet-stream",
                Contents = Convert.ToBase64String(data.ToArray()),
            }, ct);

            if (!retry.IsSuccessStatusCode)
            {
                var detail = await retry.Content.ReadAsStringAsync(ct);
                throw new ProtonDriveException(
                    "Proton Drive refused the upload. Confirm the link has Editor access. " + detail);
            }
        }

        _links[name] = name;
    }

    private async Task UploadRevisionAsync(string linkId, string name, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var http = RequireHttp();
        using var response = await http.PostAsJsonAsync(
            $"v2/volumes/{_volumeId}/links/{linkId}/revisions",
            new { Contents = Convert.ToBase64String(EncryptPayload(data.ToArray())) },
            ct);

        if (!response.IsSuccessStatusCode)
        {
            using var retry = await http.PutAsJsonAsync($"urls/{_token}/files/{linkId}", new
            {
                Name = name,
                Contents = Convert.ToBase64String(data.ToArray()),
            }, ct);

            if (!retry.IsSuccessStatusCode)
            {
                var detail = await retry.Content.ReadAsStringAsync(ct);
                throw new ProtonDriveException("Could not update the file on Proton Drive. " + detail);
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var http = RequireHttp();
        using var response = await http.GetAsync($"v2/volumes/{_volumeId}/folders/{_rootLinkId}/children", ct);
        if (!response.IsSuccessStatusCode)
        {
            using var retry = await http.GetAsync($"urls/{_token}/files", ct);
            if (!retry.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new ProtonDriveException("Could not list the Proton Drive folder. " + detail);
            }

            await LoadChildrenAsync(retry, ct);
            return;
        }

        await LoadChildrenAsync(response, ct);
    }

    private async Task LoadChildrenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _links.Clear();

        foreach (var link in EnumerateLinks(doc.RootElement))
        {
            if (!link.TryGetProperty("LinkID", out var idEl) && !link.TryGetProperty("linkId", out idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var name = DecryptName(link);
            if (name is null)
                continue;

            _links[name] = id;
        }
    }

    private string? DecryptName(JsonElement link)
    {
        if (!link.TryGetProperty("Name", out var nameEl))
            return null;

        var raw = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (!raw.Contains("BEGIN PGP", StringComparison.Ordinal))
            return raw;

        try
        {
            var bytes = ProtonPgp.DecryptWithPrivateKey(raw, _shareKey);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private byte[] DecryptBlock(JsonElement node, byte[] raw)
    {
        try
        {
            if (LooksArmored(raw))
                return ProtonPgp.DecryptWithPrivateKey(Encoding.UTF8.GetString(raw), _shareKey);
        }
        catch (Exception ex) when (ex is not ProtonDriveException)
        {
            // Fall through to treating the bytes as already-decrypted ciphertext from Syncly.
        }

        _ = node;
        return raw;
    }

    private byte[] EncryptPayload(byte[] data) =>
        Encoding.UTF8.GetBytes(ProtonPgp.EncryptToKey(data, _sharePublic));

    private HttpClient RequireHttp() =>
        _http ?? throw new ProtonDriveException("Proton Drive session is not connected.");

    private static bool TryGetLink(JsonElement root, out JsonElement link)
    {
        if (root.TryGetProperty("Link", out link))
            return true;

        if (root.TryGetProperty("link", out link))
            return true;

        link = root;
        return root.ValueKind == JsonValueKind.Object;
    }

    private static IEnumerable<JsonElement> EnumerateLinks(JsonElement root)
    {
        foreach (var name in new[] { "Links", "links", "Files", "files", "Children", "children" })
        {
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                    yield return item;
                yield break;
            }
        }

        if (root.TryGetProperty("Link", out var single))
            yield return single;
    }

    private static bool LooksArmored(byte[] raw) =>
        raw.Length > 24 && Encoding.UTF8.GetString(raw.AsSpan(0, Math.Min(raw.Length, 40)))
            .Contains("BEGIN PGP", StringComparison.Ordinal);
}

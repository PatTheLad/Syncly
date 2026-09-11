using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Syncly.Backend.ProtonDrive;

/// <summary>
/// After SRP, the share private key unlocks the folder key. File names and contents use that
/// folder key, then each file's own node key, matching Proton Drive's public-link upload.
/// </summary>
internal sealed class ProtonMailbox
{
    private readonly ProtonKeySet _shareKeys;
    private HttpClient? _http;
    private string _token = "";
    private string _volumeId = "";
    private string _rootLinkId = "";
    private ProtonKeySet? _folderKeys;
    private byte[]? _hashKey;
    private readonly Dictionary<string, string> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Proton encrypts file contents in 4 MiB plaintext blocks.</summary>
    internal const int FileBlockBytes = 4 * 1024 * 1024;

    private ProtonMailbox(ProtonKeySet keys)
    {
        _shareKeys = keys;
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
                var keys = ProtonPgp.UnlockPrivateKey(share.ShareKey, passphrase);
                return new ProtonMailbox(keys);
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
        if (!_links.TryGetValue(name, out var linkId)
            && !TryLinkIdByNameHash(name, out linkId))
            return null;

        var http = RequireHttp();
        var node = await FetchLinkBundleAsync(linkId, ct);

        if (!TryUnlockFile(node, out _, out var sessionKey))
            throw new ProtonDriveException($"Could not download “{name}” from Proton Drive.");

        if (!TryReadBlockTargets(node, out var blocks))
            blocks = await LoadRevisionBlocksAsync(linkId, ReadActiveRevisionId(node), ct);

        if (blocks.Count == 0)
            throw new ProtonDriveException($"Could not download “{name}” from Proton Drive.");

        using var buffer = new MemoryStream();
        foreach (var (url, token) in blocks)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.TryAddWithoutValidation("pm-storage-token", token);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new ProtonDriveException($"Could not download “{name}” from Proton Drive. {detail}");
            }

            var raw = await response.Content.ReadAsByteArrayAsync(ct);
            await buffer.WriteAsync(DecryptFileBlock(raw, sessionKey), ct);
        }

        return buffer.ToArray();
    }

    public async Task WriteAsync(string name, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await RefreshAsync(ct);

        if (_links.TryGetValue(name, out var existing))
        {
            await UploadRevisionAsync(existing, data, ct);
            return;
        }

        var folder = RequireFolder();
        var draft = ProtonPgp.CreateFileDraft(name, _rootLinkId, folder.Keys, folder.HashKey);
        var created = await CreateFileAsync(draft.Body, ct);
        if (created is null)
        {
            if (await TryResumeExistingAsync(name, (string)draft.Body["Hash"]!, data, ct))
                return;

            created = await CreateFileAsync(draft.Body, ct);
            if (created is null)
            {
                throw new ProtonDriveException(
                    "Proton Drive refused the upload. An incomplete file may already exist in the shared folder. Delete leftover Syncly drafts there and sync again.");
            }
        }

        try
        {
            await UploadBlocksAndCommitAsync(created.Value.LinkId, created.Value.RevisionId, draft.FileKeys, draft.SessionKey, data, ct);
            RememberLink(name, (string)draft.Body["Hash"]!, created.Value.LinkId);
        }
        catch
        {
            await TryDeleteLinkAsync(created.Value.LinkId, ct);
            throw;
        }
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        await RefreshAsync(ct);
        if (!_links.TryGetValue(name, out var linkId)
            && !TryLinkIdByNameHash(name, out linkId))
            return;

        await TryDeleteLinkAsync(linkId, ct);
        _links.Remove(name);
    }

    private async Task<(string LinkId, string RevisionId)?> CreateFileAsync(
        Dictionary<string, object?> body,
        CancellationToken ct)
    {
        var http = RequireHttp();
        var paths = new[]
        {
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/files"),
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/folders/{_rootLinkId}/files"),
        };

        string? lastError = null;
        foreach (var path in paths)
        {
            using var response = await http.PostAsJsonAsync(path, body, ProtonDriveBackend.ApiJson, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            var root = doc.RootElement;
            var code = ReadCode(root);

            if (code is 0 or 1000 && TryReadCreatedFile(root, out var created))
                return created;

            // 2500 = name already taken (finished file or leftover draft).
            // 2501 = not found: the volume-level route often 422s on public links;
            // try the folder route instead of treating it as a conflict.
            if (code is 2500)
                return null;

            lastError = text;
            if (response.IsSuccessStatusCode || (int)response.StatusCode is 404 or 405 or 422)
                continue;

            throw new ProtonDriveException(
                "Proton Drive refused the upload. Confirm the link has Editor access. " + text);
        }

        throw new ProtonDriveException(
            "Proton Drive refused the upload. Confirm the link has Editor access. " + lastError);
    }

    private async Task<bool> TryResumeExistingAsync(string name, string hash, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await RefreshAsync(ct);
        if (_links.TryGetValue(name, out var byName))
        {
            await UploadRevisionAsync(byName, data, ct);
            return true;
        }

        if (_hashes.TryGetValue(hash, out var byHash))
        {
            await UploadRevisionAsync(byHash, data, ct);
            RememberLink(name, hash, byHash);
            return true;
        }

        var pending = await FindPendingAsync(hash, ct);
        if (pending is not { } draft)
            return false;

        var node = await FetchLinkBundleAsync(draft.LinkId, ct);
        if (!TryUnlockFile(node, out var fileKeys, out var sessionKey))
        {
            await TryDeleteLinkAsync(draft.LinkId, ct);
            return false;
        }

        var revisionId = draft.RevisionId ?? ReadActiveRevisionId(node);
        if (string.IsNullOrWhiteSpace(revisionId))
        {
            await UploadRevisionAsync(draft.LinkId, data, ct);
            RememberLink(name, hash, draft.LinkId);
            return true;
        }

        try
        {
            await UploadBlocksAndCommitAsync(draft.LinkId, revisionId, fileKeys, sessionKey, data, ct);
            RememberLink(name, hash, draft.LinkId);
            return true;
        }
        catch
        {
            await TryDeleteLinkAsync(draft.LinkId, ct);
            return false;
        }
    }

    private async Task<(string LinkId, string? RevisionId)?> FindPendingAsync(string hash, CancellationToken ct)
    {
        var http = RequireHttp();
        using var response = await http.PostAsJsonAsync(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{_rootLinkId}/checkAvailableHashes"),
            new Dictionary<string, object?> { ["Hashes"] = new[] { hash } },
            ProtonDriveBackend.ApiJson,
            ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("PendingHashes", out var pending)
            || pending.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in pending.EnumerateArray())
        {
            var itemHash = ReadString(item, "Hash");
            var linkId = ReadString(item, "LinkID") ?? ReadString(item, "LinkId");
            if (string.IsNullOrWhiteSpace(linkId))
                continue;
            if (!string.IsNullOrWhiteSpace(itemHash)
                && !string.Equals(itemHash, hash, StringComparison.OrdinalIgnoreCase))
                continue;

            return (linkId, ReadString(item, "RevisionID") ?? ReadString(item, "RevisionId"));
        }

        return null;
    }

    private async Task UploadRevisionAsync(string linkId, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var http = RequireHttp();
        var node = await FetchLinkBundleAsync(linkId, ct);
        if (!TryUnlockFile(node, out var fileKeys, out var sessionKey))
            throw new ProtonDriveException("Could not unlock the existing Proton Drive file.");

        var currentRevision = ReadActiveRevisionId(node);
        var body = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(currentRevision))
            body["CurrentRevisionID"] = currentRevision;

        using var response = await http.PostAsJsonAsync(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/files/{linkId}/revisions"),
            body,
            ProtonDriveBackend.ApiJson,
            ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (!TryReadCreatedRevision(doc.RootElement, out var revisionId))
        {
            throw new ProtonDriveException("Could not update the file on Proton Drive. " + text);
        }

        await UploadBlocksAndCommitAsync(linkId, revisionId, fileKeys, sessionKey, data, ct);
    }

    private async Task UploadBlocksAndCommitAsync(
        string linkId,
        string revisionId,
        ProtonKeySet fileKeys,
        byte[] sessionKey,
        ReadOnlyMemory<byte> data,
        CancellationToken ct)
    {
        var http = RequireHttp();
        var plaintext = data.ToArray();
        var slices = SplitFileBlocks(plaintext.Length);
        var prepared = new List<(int Index, byte[] Encrypted, byte[] Digest, Dictionary<string, object?> Body)>(slices.Count);
        var verificationCode = await TryVerificationCodeAsync(linkId, revisionId, ct);

        for (var i = 0; i < slices.Count; i++)
        {
            var (offset, count) = slices[i];
            var chunk = count == 0 ? [] : plaintext.AsSpan(offset, count).ToArray();
            var encrypted = ProtonPgp.EncryptWithSessionKey(chunk, sessionKey);
            var digest = SHA256.HashData(encrypted);
            var index = i + 1;
            var block = new Dictionary<string, object?>
            {
                ["Index"] = index,
                ["Size"] = encrypted.Length,
                ["Hash"] = Convert.ToBase64String(digest),
                ["EncSignature"] = ProtonPgp.EncryptToKey(ProtonPgp.SignDetached(chunk, fileKeys), fileKeys.EncryptionPublic),
            };
            if (verificationCode is not null)
            {
                block["Verifier"] = new Dictionary<string, object?>
                {
                    ["Token"] = Convert.ToBase64String(ProtonPgp.VerificationToken(verificationCode, encrypted)),
                };
            }

            prepared.Add((index, encrypted, digest, block));
        }

        var request = new Dictionary<string, object?>
        {
            ["VolumeID"] = _volumeId,
            ["LinkID"] = linkId,
            ["RevisionID"] = revisionId,
            ["BlockList"] = prepared.Select(b => b.Body).ToArray(),
            ["ThumbnailList"] = Array.Empty<object>(),
        };

        using var prepare = await http.PostAsJsonAsync(
            ProtonDriveBackend.DataPath("blocks"), request, ProtonDriveBackend.ApiJson, ct);
        var prepareText = await prepare.Content.ReadAsStringAsync(ct);
        using var prepareDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(prepareText) ? "{}" : prepareText);
        if (!TryReadUploadTargets(prepareDoc.RootElement, prepared.Count, out var targets))
            throw new ProtonDriveException("Proton Drive did not accept the file block. " + prepareText);

        foreach (var block in prepared)
        {
            if (!targets.TryGetValue(block.Index, out var target))
                throw new ProtonDriveException("Proton Drive did not accept the file block. " + prepareText);

            await UploadBlockBytesAsync(http, target.Url, target.Token, block.Encrypted, ct);
        }

        var manifest = prepared.SelectMany(b => b.Digest).ToArray();
        var commit = new Dictionary<string, object?>
        {
            ["State"] = 1,
            ["ManifestSignature"] = ProtonPgp.SignDetachedArmored(manifest, fileKeys),
            ["BlockList"] = prepared.Select(b => new Dictionary<string, object?>
            {
                ["Index"] = b.Index,
                ["Token"] = targets[b.Index].Token,
            }).ToArray(),
        };

        using var sealedRevision = await http.PutAsJsonAsync(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/files/{linkId}/revisions/{revisionId}"),
            commit,
            ProtonDriveBackend.ApiJson,
            ct);
        if (!sealedRevision.IsSuccessStatusCode)
        {
            var detail = await sealedRevision.Content.ReadAsStringAsync(ct);
            throw new ProtonDriveException("Proton Drive could not finish the upload. " + detail);
        }
    }

    private async Task<byte[]?> TryVerificationCodeAsync(
        string linkId,
        string revisionId,
        CancellationToken ct)
    {
        var http = RequireHttp();
        using var response = await http.GetAsync(
            ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{linkId}/revisions/{revisionId}/verification"),
            ct);
        if (!response.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("VerificationCode", out var codeEl))
            return null;

        var raw = codeEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async Task UploadBlockBytesAsync(
        HttpClient http,
        string bareUrl,
        string token,
        byte[] encrypted,
        CancellationToken ct)
    {
        using var raw = new HttpRequestMessage(HttpMethod.Post, bareUrl);
        raw.Headers.TryAddWithoutValidation("pm-storage-token", token);
        raw.Content = new ByteArrayContent(encrypted);
        raw.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var rawResponse = await http.SendAsync(raw, ct);
        if (rawResponse.IsSuccessStatusCode)
            return;

        using var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(encrypted);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        multipart.Add(part, "Block", "blob");
        using var form = new HttpRequestMessage(HttpMethod.Post, bareUrl);
        form.Headers.TryAddWithoutValidation("pm-storage-token", token);
        form.Content = multipart;
        using var formResponse = await http.SendAsync(form, ct);
        if (!formResponse.IsSuccessStatusCode)
        {
            var detail = await formResponse.Content.ReadAsStringAsync(ct);
            throw new ProtonDriveException("Proton Drive storage refused the file block. " + detail);
        }
    }

    private async Task TryDeleteLinkAsync(string linkId, CancellationToken ct)
    {
        try
        {
            var http = RequireHttp();
            await http.DeleteAsync(
                ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links/{linkId}"),
                ct);
        }
        catch
        {
            // Best-effort cleanup of a failed draft.
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        await EnsureFolderAsync(ct);
        var http = RequireHttp();
        var ids = new List<string>();
        string? anchor = null;

        while (true)
        {
            var path = ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/folders/{_rootLinkId}/children");
            if (!string.IsNullOrEmpty(anchor))
                path += "?AnchorID=" + Uri.EscapeDataString(anchor);

            using var response = await http.GetAsync(path, ct);
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

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (HasLinkObjects(root))
            {
                ApplyLinks(root);
                return;
            }

            foreach (var id in ReadLinkIds(root))
                ids.Add(id);

            var more = root.TryGetProperty("More", out var moreEl) && moreEl.ValueKind is JsonValueKind.True;
            anchor = root.TryGetProperty("AnchorID", out var anchorEl) ? anchorEl.GetString() : null;
            if (!more || string.IsNullOrEmpty(anchor))
                break;
        }

        _links.Clear();
        if (ids.Count == 0)
            return;

        await LoadLinkDetailsAsync(ids, ct);
    }

    private async Task EnsureFolderAsync(CancellationToken ct)
    {
        if (_folderKeys is not null && _hashKey is not null)
            return;

        var bundle = await FetchLinkBundleAsync(_rootLinkId, ct);
        var passphrase = ReadString(bundle.Link, "NodePassphrase");
        var nodeKey = ReadString(bundle.Link, "NodeKey");
        if (string.IsNullOrWhiteSpace(passphrase) || string.IsNullOrWhiteSpace(nodeKey))
            throw new ProtonDriveException("Proton Drive folder is missing its encryption key.");

        var unlocked = ProtonPgp.DecryptWithPrivateKey(passphrase, _shareKeys);
        _folderKeys = ProtonPgp.UnlockPrivateKey(nodeKey, unlocked);

        var hashArmored = bundle.NodeHashKey;
        if (string.IsNullOrWhiteSpace(hashArmored))
            throw new ProtonDriveException("Proton Drive folder is missing its name hash key.");

        _hashKey = ProtonPgp.DecryptWithPrivateKey(hashArmored, _folderKeys);
    }

    private async Task<ProtonLinkBundle> FetchLinkBundleAsync(string linkId, CancellationToken ct)
    {
        var http = RequireHttp();
        var path = ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links");
        using var response = await http.PostAsJsonAsync(
            path, new { LinkIDs = new[] { linkId } }, ProtonDriveBackend.ApiJson, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ProtonDriveException("Could not load the Proton Drive folder. " + text);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        ProtonLinkBundle? match = null;
        ProtonLinkBundle? first = null;
        var count = 0;
        foreach (var bundle in EnumerateLinkBundles(doc.RootElement))
        {
            count++;
            first ??= bundle;
            var id = ReadString(bundle.Link, "LinkID") ?? ReadString(bundle.Link, "linkId");
            if (id == linkId)
                match = bundle;
        }

        var chosen = match ?? (count == 1 ? first : null);
        if (chosen is null)
            throw new ProtonDriveException("Proton Drive did not return the shared folder.");

        return chosen.Value.Snapshot();
    }

    private async Task LoadLinkDetailsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var http = RequireHttp();
        const int batch = 150;
        for (var i = 0; i < ids.Count; i += batch)
        {
            var chunk = ids.Skip(i).Take(batch).ToArray();
            using var response = await http.PostAsJsonAsync(
                ProtonDriveBackend.DataPath($"v2/volumes/{_volumeId}/links"),
                new { LinkIDs = chunk },
                ProtonDriveBackend.ApiJson,
                ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new ProtonDriveException("Could not read Proton Drive folder entries. " + detail);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            ApplyLinks(doc.RootElement, clear: i == 0);
        }
    }

    private async Task LoadChildrenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        ApplyLinks(doc.RootElement);
    }

    private void ApplyLinks(JsonElement root, bool clear = true)
    {
        if (clear)
        {
            _links.Clear();
            _hashes.Clear();
        }

        foreach (var link in EnumerateLinks(root))
        {
            if (!link.TryGetProperty("LinkID", out var idEl) && !link.TryGetProperty("linkId", out idEl))
                continue;

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var hash = ReadString(link, "Hash");
            if (!string.IsNullOrWhiteSpace(hash))
                _hashes[hash] = id;

            var name = DecryptName(link);
            if (name is null)
                continue;

            _links[name] = id;
        }
    }

    private void RememberLink(string name, string hash, string linkId)
    {
        _links[name] = linkId;
        if (!string.IsNullOrWhiteSpace(hash))
            _hashes[hash] = linkId;
    }

    private bool TryLinkIdByNameHash(string name, out string linkId)
    {
        linkId = "";
        if (_hashKey is null)
            return false;

        var hash = ProtonPgp.LookupHash(name, _hashKey);
        if (!_hashes.TryGetValue(hash, out var found) || string.IsNullOrWhiteSpace(found))
            return false;

        linkId = found;
        return true;
    }

    internal static List<(int Offset, int Count)> SplitFileBlocks(int length)
    {
        var blocks = new List<(int Offset, int Count)>();
        if (length <= 0)
        {
            blocks.Add((0, 0));
            return blocks;
        }

        for (var offset = 0; offset < length; offset += FileBlockBytes)
            blocks.Add((offset, Math.Min(FileBlockBytes, length - offset)));

        return blocks;
    }

    private static bool HasLinkObjects(JsonElement root) =>
        EnumerateLinks(root).Any(link =>
            link.TryGetProperty("Name", out _)
            || (link.TryGetProperty("LinkID", out _) && link.TryGetProperty("MIMEType", out _)));

    private static IEnumerable<string> ReadLinkIds(JsonElement root)
    {
        foreach (var name in new[] { "LinkIDs", "linkIDs", "LinkIds" })
        {
            if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in arr.EnumerateArray())
            {
                var id = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id))
                    yield return id;
            }

            yield break;
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

        foreach (var keys in NameKeys())
        {
            try
            {
                return Encoding.UTF8.GetString(ProtonPgp.DecryptWithPrivateKey(raw, keys));
            }
            catch
            {
                // Try the share key if the folder key is not the recipient.
            }
        }

        return null;
    }

    private IEnumerable<ProtonKeySet> NameKeys()
    {
        if (_folderKeys is not null)
            yield return _folderKeys;
        yield return _shareKeys;
    }

    private async Task<List<(string Url, string? Token)>> LoadRevisionBlocksAsync(
        string linkId,
        string? revisionId,
        CancellationToken ct)
    {
        var blocks = new List<(string Url, string? Token)>();
        if (string.IsNullOrWhiteSpace(revisionId))
            return blocks;

        var http = RequireHttp();
        var path = ProtonDriveBackend.DataPath(
            $"v2/volumes/{_volumeId}/files/{linkId}/revisions/{revisionId}?FromBlockIndex=1&PageSize=50");
        using var response = await http.GetAsync(path, ct);
        if (!response.IsSuccessStatusCode)
            return blocks;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("Revision", out var revision)
            && revision.TryGetProperty("Blocks", out var blockEl)
            && blockEl.ValueKind == JsonValueKind.Array)
            ReadBlockTargets(blockEl, blocks);

        return blocks;
    }

    private static bool TryReadBlockTargets(ProtonLinkBundle node, out List<(string Url, string? Token)> blocks)
    {
        blocks = [];
        var file = node.FileView;
        if (file.ValueKind != JsonValueKind.Object)
            return false;

        if (file.TryGetProperty("ActiveRevision", out var revision)
            && revision.TryGetProperty("Blocks", out var blockEl)
            && blockEl.ValueKind == JsonValueKind.Array)
            return ReadBlockTargets(blockEl, blocks);

        return false;
    }

    private bool TryUnlockFile(ProtonLinkBundle node, out ProtonKeySet fileKeys, out byte[] sessionKey)
    {
        fileKeys = _shareKeys;
        sessionKey = [];

        var passphrase = ReadString(node.Link, "NodePassphrase");
        var nodeKey = ReadString(node.Link, "NodeKey");
        if (string.IsNullOrWhiteSpace(passphrase) || string.IsNullOrWhiteSpace(nodeKey))
            return false;

        var parent = _folderKeys ?? _shareKeys;
        byte[] unlocked;
        try
        {
            unlocked = ProtonPgp.DecryptWithPrivateKey(passphrase, parent);
        }
        catch
        {
            return false;
        }

        try
        {
            fileKeys = ProtonPgp.UnlockPrivateKey(nodeKey, unlocked);
        }
        catch
        {
            return false;
        }

        var packet = ReadString(node.FileView, "ContentKeyPacket");
        if (string.IsNullOrWhiteSpace(packet))
            return false;

        try
        {
            sessionKey = ProtonPgp.DecryptSessionKey(Convert.FromBase64String(packet), fileKeys.EncryptionPrivate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecryptFileBlock(byte[] raw, byte[] sessionKey)
    {
        try
        {
            return ProtonPgp.DecryptWithSessionKey(raw, sessionKey);
        }
        catch (Exception ex) when (ex is not ProtonDriveException)
        {
            throw new ProtonDriveException("Could not decrypt a Proton Drive file block.", ex);
        }
    }

    private FolderSecrets RequireFolder()
    {
        if (_folderKeys is null || _hashKey is null)
            throw new ProtonDriveException("Proton Drive folder keys are not ready.");

        return new FolderSecrets(_folderKeys, _hashKey);
    }

    private HttpClient RequireHttp() =>
        _http ?? throw new ProtonDriveException("Proton Drive session is not connected.");

    private static bool TryGetLinksContainer(JsonElement root, out JsonElement container)
    {
        foreach (var name in new[] { "Links", "links", "Files", "files", "Children", "children" })
        {
            if (root.TryGetProperty(name, out container)
                && container.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                return true;
        }

        container = default;
        return false;
    }

    private static IEnumerable<ProtonLinkBundle> EnumerateLinkBundles(JsonElement root)
    {
        if (TryGetLinksContainer(root, out var container))
        {
            if (container.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in container.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        yield return ReadBundle(item);
                }
            }
            else
            {
                foreach (var property in container.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                        yield return ReadBundle(property.Value);
                }
            }

            yield break;
        }

        if (root.ValueKind == JsonValueKind.Object)
            yield return ReadBundle(root);
    }

    private static ProtonLinkBundle ReadBundle(JsonElement item)
    {
        var link = item.TryGetProperty("Link", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : item;
        item.TryGetProperty("Folder", out var folder);
        item.TryGetProperty("File", out var file);
        if (folder.ValueKind != JsonValueKind.Object && link.TryGetProperty("FolderProperties", out var folderProps))
            folder = folderProps;
        return new ProtonLinkBundle(link, folder, file);
    }

    private static IEnumerable<JsonElement> EnumerateLinks(JsonElement root)
    {
        foreach (var name in new[] { "Links", "links", "Files", "files", "Children", "children" })
        {
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("Link", out var nested)
                        && nested.ValueKind == JsonValueKind.Object)
                        yield return nested;
                    else if (item.ValueKind == JsonValueKind.Object)
                        yield return item;
                }

                yield break;
            }
        }

        if (root.TryGetProperty("Link", out var single))
            yield return single;
    }

    private static string? ReadActiveRevisionId(ProtonLinkBundle node)
    {
        var file = node.FileView;
        if (file.ValueKind != JsonValueKind.Object)
            return null;

        if (file.TryGetProperty("ActiveRevision", out var revision))
            return ReadString(revision, "ID")
                   ?? ReadString(revision, "RevisionID")
                   ?? ReadString(revision, "ActiveRevisionID");

        return ReadString(file, "ActiveRevisionID");
    }

    private static bool ReadBlockTargets(JsonElement blocks, List<(string Url, string? Token)> output)
    {
        foreach (var block in blocks.EnumerateArray())
        {
            if (!block.TryGetProperty("BareUrl", out var urlEl)
                && !block.TryGetProperty("BareURL", out urlEl)
                && !block.TryGetProperty("URL", out urlEl)
                && !block.TryGetProperty("Url", out urlEl))
                continue;

            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var token = ReadString(block, "Token");
            output.Add((url, token));
        }

        return output.Count > 0;
    }

    private static bool TryReadCreatedFile(JsonElement root, out (string LinkId, string RevisionId) created)
    {
        created = default;
        if (!root.TryGetProperty("File", out var file) && !root.TryGetProperty("file", out file))
            return false;

        var linkId = ReadString(file, "ID") ?? ReadString(file, "LinkID");
        var revisionId = ReadString(file, "RevisionID") ?? ReadString(file, "RevisionId");
        if (string.IsNullOrWhiteSpace(linkId) || string.IsNullOrWhiteSpace(revisionId))
            return false;

        created = (linkId, revisionId);
        return true;
    }

    private static bool TryReadCreatedRevision(JsonElement root, out string revisionId)
    {
        revisionId = "";
        if (root.TryGetProperty("Revision", out var revision))
        {
            revisionId = ReadString(revision, "ID") ?? ReadString(revision, "RevisionID") ?? "";
            return !string.IsNullOrWhiteSpace(revisionId);
        }

        revisionId = ReadString(root, "ID") ?? ReadString(root, "RevisionID") ?? "";
        return !string.IsNullOrWhiteSpace(revisionId);
    }

    private static bool TryReadUploadTargets(
        JsonElement root,
        int expected,
        out Dictionary<int, (string Url, string Token)> targets)
    {
        targets = new Dictionary<int, (string Url, string Token)>();
        if (!root.TryGetProperty("UploadLinks", out var links) || links.ValueKind != JsonValueKind.Array)
            return false;

        var order = 0;
        foreach (var link in links.EnumerateArray())
        {
            order++;
            var url = ReadString(link, "BareURL") ?? ReadString(link, "BareUrl") ?? "";
            var token = ReadString(link, "Token") ?? "";
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(token))
                continue;

            var index = order;
            if (link.TryGetProperty("Index", out var indexEl) && indexEl.TryGetInt32(out var parsed) && parsed > 0)
                index = parsed;

            targets[index] = (url, token);
        }

        return targets.Count >= expected;
    }

    private static int ReadCode(JsonElement root) =>
        root.TryGetProperty("Code", out var code) && code.TryGetInt32(out var value) ? value : -1;

    private static string? ReadString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        return obj.TryGetProperty(name, out var el) ? el.GetString() : null;
    }

    private static string? ReadNestedString(JsonElement obj, string parent, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(parent, out var child) || child.ValueKind != JsonValueKind.Object)
            return null;

        return ReadString(child, name);
    }

    private readonly record struct FolderSecrets(ProtonKeySet Keys, byte[] HashKey);

    private readonly record struct ProtonLinkBundle(JsonElement Link, JsonElement Folder, JsonElement File)
    {
        public JsonElement FileView
        {
            get
            {
                if (File.ValueKind == JsonValueKind.Object)
                    return File;
                if (Link.ValueKind == JsonValueKind.Object && Link.TryGetProperty("FileProperties", out var props))
                    return props;
                if (Link.ValueKind == JsonValueKind.Object && Link.TryGetProperty("File", out var file))
                    return file;
                return default;
            }
        }

        public string? NodeHashKey =>
            ReadString(Folder, "NodeHashKey")
            ?? ReadNestedString(Link, "FolderProperties", "NodeHashKey")
            ?? ReadNestedString(Link, "Folder", "NodeHashKey")
            ?? ReadString(Link, "NodeHashKey");

        public ProtonLinkBundle Snapshot() =>
            new(
                Link.ValueKind == JsonValueKind.Object ? Link.Clone() : default,
                Folder.ValueKind == JsonValueKind.Object ? Folder.Clone() : default,
                File.ValueKind == JsonValueKind.Object ? File.Clone() : default);
    }
}
